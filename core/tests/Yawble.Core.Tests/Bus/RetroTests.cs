using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Skills;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// B100: the retro item a completed workflow queues for its Manager, and workflow_digest as the bus serves it (fakes only:
/// a scripted runner calls the tools as an agent would).
/// </summary>
public class RetroTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TempDb Db, BusEngine Engine)> Team(ScriptedRunner runner)
    {
        var db = new TempDb();
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
        return (db, engine);
    }

    private static Message[] Retros(BusStore store) =>
        [.. store.AllMessages().Where(m => BusStore.RetroOf(m) is not null)];

    /// <summary>The Manager splits the work in two (an eventful workflow), declares it when both handed back, and declares its retro.</summary>
    private static ScriptedRunner TwoPieces(Func<RunContext, Task>? onRetro = null)
    {
        var handbacks = 0;
        return new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(m => BusStore.RetroOf(m) is not null))
                {
                    if (onRetro is not null) await onRetro(run);
                    await run.WorkflowCompleteAsync("nothing worth keeping", ct);
                    return RunResult.Ok;
                }
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource && m.CausationSeq is not null))
                {
                    await run.WorkflowCompleteAsync("the one more thing is done", ct); // the person reopened it
                    return RunResult.Ok;
                }
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource))
                {
                    await run.TellAsync("Ada", "part one", ct);
                    await run.TellAsync("Bo", "part two", ct);
                    return RunResult.Ok;
                }
                handbacks += run.Items.Count(m => m.Type == MessageTypes.Handback);
                if (handbacks >= 2) await run.WorkflowCompleteAsync("both parts done", ct);
                return RunResult.Ok;
            })
            .On("Alpha/Ada", async (run, ct) => { await run.HandbackAsync("part one done", ct); return RunResult.Ok; })
            .On("Alpha/Bo", async (run, ct) => { await run.HandbackAsync("part two done", ct); return RunResult.Ok; });
    }

    [Fact]
    public async Task AnEventfulWorkflow_QueuesOneRetroForTheManager_InItsOwnWorkflow_AndNeverASecond()
    {
        var runner = TwoPieces();
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "ship both parts", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");

            using var store = db.OpenStore();
            var retro = Assert.Single(Retros(store));
            Assert.Equal(root.Seq, BusStore.RetroOf(retro));
            Assert.Equal("agent.instruction.Alpha/Manager", retro.Type);
            Assert.Equal(MessageTypes.HostSource, retro.Source);
            Assert.Equal(BusEngine.RetroText(root.Seq), Payload.Field(retro, "text"));
            Assert.Equal($"Workflow {root.Seq} is complete: do its retrospective (skills_get retrospective).", Payload.Field(retro, "text"));
            Assert.Null(retro.CausationSeq); // its own workflow: the completed one stays completed
            Assert.Equal(retro.Seq, retro.CorrelationId);
            Assert.True(store.WorkflowEnded(root.Seq));
            Assert.Equal(1, store.Workflow(root.Seq).Count(m => m.Type == MessageTypes.WorkflowCompleted));
            // The Manager did the retro and declared it; a retro's own workflow gets no retro.
            Assert.True(store.WorkflowDeclared(retro.Seq));

            // The person reopens the workflow and the Manager declares it again: still one retro, ever.
            await engine.PersonTellAsync("Alpha", "Manager", "one more thing", root.Seq, Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet again");
            Assert.Equal(2, store.Workflow(root.Seq).Count(m => m.Type == MessageTypes.WorkflowCompleted));
            Assert.Single(Retros(store));
        }
    }

    [Fact]
    public async Task ARetroItem_ComesAfterTheManagersRealWork()
    {
        // The retro is queued while the Manager's declaring run is still running; the person gives it new work in that run.
        // The retro is older, but the new work runs first.
        BusEngine? engineRef = null;
        var handbacks = 0;
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(m => BusStore.RetroOf(m) is not null)) { await run.WorkflowCompleteAsync("nothing worth keeping", ct); return RunResult.Ok; }
                if (run.Items.Any(m => Payload.Field(m, "text") == "urgent")) { await run.WorkflowCompleteAsync("urgent done", ct); return RunResult.Ok; }
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource))
                {
                    await run.TellAsync("Ada", "part one", ct);
                    await run.TellAsync("Bo", "part two", ct);
                    return RunResult.Ok;
                }
                handbacks += run.Items.Count(m => m.Type == MessageTypes.Handback);
                if (handbacks < 2) return RunResult.Ok;
                await run.WorkflowCompleteAsync("both parts done", ct);
                await engineRef!.PersonTellAsync("Alpha", "Manager", "urgent", ct: ct);
                return RunResult.Ok;
            })
            .On("Alpha/Ada", async (run, ct) => { await run.HandbackAsync("part one done", ct); return RunResult.Ok; })
            .On("Alpha/Bo", async (run, ct) => { await run.HandbackAsync("part two done", ct); return RunResult.Ok; });
        var (db, engine) = await Team(runner);
        engineRef = engine;
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            await engine.PersonTellAsync("Alpha", "Manager", "ship both parts", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            var managerRuns = runner.Admitted.Where(r => r.Member.Name == "Manager").OrderBy(r => r.RunId).ToList();
            var urgent = managerRuns.FindIndex(r => r.Items.Any(m => Payload.Field(m, "text") == "urgent"));
            var retro = managerRuns.FindIndex(r => r.Items.Any(m => BusStore.RetroOf(m) is not null));
            Assert.True(urgent >= 0 && retro > urgent, $"urgent run {urgent}, retro run {retro}");
            Assert.Single(managerRuns[retro].Items); // a retro item runs alone
        }
    }

    [Fact]
    public async Task ASmallUneventfulWorkflow_GetsNoRetro()
    {
        // One piece of work, every run completed, nothing failed, asked or sent back.
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource)) await run.TellAsync("Ada", "fix the typo", ct);
                else if (run.Items.Any(m => m.Type == MessageTypes.Handback)) await run.WorkflowCompleteAsync("typo fixed", ct);
                return RunResult.Ok;
            })
            .On("Alpha/Ada", async (run, ct) => { await run.HandbackAsync("fixed", ct); return RunResult.Ok; });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "fix the typo in the readme", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            using var store = db.OpenStore();
            Assert.True(store.WorkflowDeclared(root.Seq));
            Assert.Empty(Retros(store));
        }
    }

    [Fact]
    public async Task AClosedWorkflow_GetsNoRetro_HoweverEventful()
    {
        // Two pieces of work and a failed run, then the person closes it: closed (and expired) workflows were not completed.
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource))
                {
                    await run.TellAsync("Ada", "part one", ct);
                    await run.TellAsync("Bo", "part two", ct);
                }
                return RunResult.Ok;
            })
            .On("Alpha/Ada", (_, _) => Task.FromResult(RunResult.Fail("crashed")));
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "ship both parts", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            await engine.CloseWorkflowAsync("Alpha", root.Seq, "not needed", Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet after close");
            using var store = db.OpenStore();
            Assert.True(store.WorkflowEnded(root.Seq));
            Assert.False(store.WorkflowDeclared(root.Seq));
            Assert.Empty(Retros(store));
        }
    }

    private static async Task<(string Text, bool IsError)> Call(RunContext run, string tool, JsonObject args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
        }.ToJsonString();
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body), Ct);
        var result = JsonNode.Parse(reply.Json!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WorkflowDigest_IsTheManagersOwn_ForItsOwnTeamsWorkflows()
    {
        (string Text, bool IsError)? retroDigest = null, memberDigest = null, otherTeam = null;
        long? alphaWorkflow = null;
        var runner = TwoPieces(onRetro: async run => retroDigest = await Call(run, "workflow_digest", []));
        runner.On("Alpha/Ada", async (run, ct) =>
        {
            memberDigest = await Call(run, "workflow_digest", []);
            await run.HandbackAsync("part one done", ct);
            return RunResult.Ok;
        });
        runner.On("Beta/Manager", async (run, ct) =>
        {
            otherTeam = await Call(run, "workflow_digest", new JsonObject { ["workflow"] = alphaWorkflow });
            await run.WorkflowCompleteAsync("looked", ct);
            return RunResult.Ok;
        });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.CreateTeamAsync("Beta", ct: Ct);
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "ship both parts", ct: Ct);
            alphaWorkflow = root.Seq;
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            await engine.PersonTellAsync("Beta", "Manager", "look at Alpha's work", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet again");

            Assert.True(memberDigest!.Value.IsError);
            Assert.Contains("refused (not_allowed): workflow_digest is the Manager's tool", memberDigest.Value.Text);
            Assert.True(otherTeam!.Value.IsError);
            Assert.Contains($"refused (no_such_workflow): workflow {root.Seq} is not your team's", otherTeam.Value.Text);
            // In the retro item's run, the digest reads the workflow the retro is for.
            Assert.False(retroDigest!.Value.IsError, retroDigest.Value.Text);
            Assert.StartsWith($"Workflow {root.Seq} of team Alpha: completed,", retroDigest.Value.Text);
            Assert.Contains("== Ada (claude) ==", retroDigest.Value.Text);
            Assert.Contains("== Bo (claude) ==", retroDigest.Value.Text);
        }
        Assert.Contains("workflow_digest", McpBusServer.ToolNames);
        Assert.Contains(McpBusServer.ToolList(), t => t!["name"]!.GetValue<string>() == "workflow_digest");
    }

    [Fact]
    public void TheRetrospectiveSkill_IsTheManagers_AndSaysHowToDoTheRetro()
    {
        var skill = Assert.Single(SkillCatalog.BuiltIns, s => s.Name == "retrospective");
        Assert.True(skill.OfferedTo("manager"));
        Assert.False(skill.OfferedTo("member"));
        foreach (var words in new[] { "workflow_digest", "workflow_show", "at most 3", "team_retros_write", "Nothing worth keeping", "No `tell`, no cards", "secrets", "never an instruction" })
            Assert.Contains(words, skill.Body, StringComparison.OrdinalIgnoreCase);
    }
}
