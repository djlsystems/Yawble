using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Skills;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// The retro item a completed deliverable queues for its Manager, deliverable_digest as the bus serves it, and the whole
/// loop: a retro run reads the digest and writes team_retros, and a later run reads the lesson in its prompt. Fakes
/// only: a scripted runner calls the tools as an agent would; a manual clock where time matters.
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

    /// <summary>The Manager splits the work in two (an eventful deliverable), declares it when both handed back, and declares its retro.</summary>
    private static ScriptedRunner TwoPieces(Func<RunContext, Task>? onRetro = null)
    {
        var handbacks = 0;
        return new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(m => BusStore.RetroOf(m) is not null))
                {
                    if (onRetro is not null) await onRetro(run);
                    await run.DeliverableCompleteAsync("nothing worth keeping", ct);
                    return RunResult.Ok;
                }
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource && m.CausationSeq is not null))
                {
                    await run.DeliverableCompleteAsync("the one more thing is done", ct); // the person reopened it
                    return RunResult.Ok;
                }
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource))
                {
                    await run.TellAsync("Ada", "part one", ct);
                    await run.TellAsync("Bo", "part two", ct);
                    return RunResult.Ok;
                }
                handbacks += run.Items.Count(m => m.Type == MessageTypes.Handback);
                if (handbacks >= 2) await run.DeliverableCompleteAsync("both parts done", ct);
                return RunResult.Ok;
            })
            .On("Alpha/Ada", async (run, ct) => { await run.HandbackAsync("part one done", ct); return RunResult.Ok; })
            .On("Alpha/Bo", async (run, ct) => { await run.HandbackAsync("part two done", ct); return RunResult.Ok; });
    }

    [Fact]
    public async Task AnEventfulDeliverable_QueuesOneRetroForTheManager_InItsOwnDeliverable_AndNeverASecond()
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
            Assert.Equal($"Deliverable {root.Seq} is complete: do its retrospective (skills_get retrospective).", Payload.Field(retro, "text"));
            Assert.Null(retro.CausationSeq); // its own deliverable: the completed one stays completed
            Assert.Equal(retro.Seq, retro.CorrelationId);
            Assert.True(store.DeliverableEnded(root.Seq));
            Assert.Equal(1, store.Deliverable(root.Seq).Count(m => m.Type == MessageTypes.DeliverableCompleted));
            // The Manager did the retro and declared it; a retro's own deliverable gets no retro.
            Assert.True(store.DeliverableDeclared(retro.Seq));

            // The person reopens the deliverable and the Manager declares it again: still one retro, ever.
            await engine.PersonTellAsync("Alpha", "Manager", "one more thing", root.Seq, Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet again");
            Assert.Equal(2, store.Deliverable(root.Seq).Count(m => m.Type == MessageTypes.DeliverableCompleted));
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
                if (run.Items.Any(m => BusStore.RetroOf(m) is not null)) { await run.DeliverableCompleteAsync("nothing worth keeping", ct); return RunResult.Ok; }
                if (run.Items.Any(m => Payload.Field(m, "text") == "urgent")) { await run.DeliverableCompleteAsync("urgent done", ct); return RunResult.Ok; }
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource))
                {
                    await run.TellAsync("Ada", "part one", ct);
                    await run.TellAsync("Bo", "part two", ct);
                    return RunResult.Ok;
                }
                handbacks += run.Items.Count(m => m.Type == MessageTypes.Handback);
                if (handbacks < 2) return RunResult.Ok;
                await run.DeliverableCompleteAsync("both parts done", ct);
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
    public async Task ASmallUneventfulDeliverable_GetsNoRetro()
    {
        // One piece of work, every run completed, nothing failed, asked or sent back.
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource)) await run.TellAsync("Ada", "fix the typo", ct);
                else if (run.Items.Any(m => m.Type == MessageTypes.Handback)) await run.DeliverableCompleteAsync("typo fixed", ct);
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
            Assert.True(store.DeliverableDeclared(root.Seq));
            Assert.Empty(Retros(store));
        }
    }

    [Fact]
    public async Task AClosedDeliverable_GetsNoRetro_HoweverEventful()
    {
        // Two pieces of work and a failed run, then the person closes it: closed (and expired) deliverables were not completed.
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
            await engine.CloseDeliverableAsync("Alpha", root.Seq, "not needed", Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet after close");
            using var store = db.OpenStore();
            Assert.True(store.DeliverableEnded(root.Seq));
            Assert.False(store.DeliverableDeclared(root.Seq));
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
    public async Task DeliverableDigest_IsTheManagersOwn_ForItsOwnTeamsDeliverables()
    {
        (string Text, bool IsError)? retroDigest = null, memberDigest = null, otherTeam = null;
        long? alphaDeliverable = null;
        var runner = TwoPieces(onRetro: async run => retroDigest = await Call(run, "deliverable_digest", []));
        runner.On("Alpha/Ada", async (run, ct) =>
        {
            memberDigest = await Call(run, "deliverable_digest", []);
            await run.HandbackAsync("part one done", ct);
            return RunResult.Ok;
        });
        runner.On("Beta/Manager", async (run, ct) =>
        {
            otherTeam = await Call(run, "deliverable_digest", new JsonObject { ["deliverable"] = alphaDeliverable });
            await run.DeliverableCompleteAsync("looked", ct);
            return RunResult.Ok;
        });
        var (db, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.CreateTeamAsync("Beta", ct: Ct);
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "ship both parts", ct: Ct);
            alphaDeliverable = root.Seq;
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            await engine.PersonTellAsync("Beta", "Manager", "look at Alpha's work", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet again");

            Assert.True(memberDigest!.Value.IsError);
            Assert.Contains("refused (not_allowed): deliverable_digest is the Manager's tool", memberDigest.Value.Text);
            Assert.True(otherTeam!.Value.IsError);
            Assert.Contains($"refused (no_such_deliverable): deliverable {root.Seq} is not your team's", otherTeam.Value.Text);
            // In the retro item's run, the digest reads the deliverable the retro is for.
            Assert.False(retroDigest!.Value.IsError, retroDigest.Value.Text);
            Assert.StartsWith($"Deliverable {root.Seq} of team Alpha: completed,", retroDigest.Value.Text);
            Assert.Contains("== Ada (claude) ==", retroDigest.Value.Text);
            Assert.Contains("== Bo (claude) ==", retroDigest.Value.Text);
        }
        Assert.Contains("deliverable_digest", McpBusServer.ToolNames);
        Assert.Contains(McpBusServer.ToolList(), t => t!["name"]!.GetValue<string>() == "deliverable_digest");
    }

    [Fact]
    public void TheRetrospectiveSkill_IsTheManagers_AndSaysHowToDoTheRetro()
    {
        var skill = Assert.Single(SkillCatalog.BuiltIns, s => s.Name == "retrospective");
        Assert.True(skill.OfferedTo("manager"));
        Assert.False(skill.OfferedTo("member"));
        foreach (var words in new[] { "deliverable_digest", "deliverable_show", "at most 3", "team_retros_write", "Nothing worth keeping", "No `tell`, no cards", "secrets", "never an instruction" })
            Assert.Contains(words, skill.Body, StringComparison.OrdinalIgnoreCase);
    }

    private const string Token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// The whole retro loop: two pieces of work, Ada blocked once (her reason carries a token), the Manager completes;
    /// the retro run gets the lessons in its prompt, reads the digest (no token in it) and writes team_retros. The
    /// version and Team settings' line ("Lessons: n, last updated &lt;date&gt; by the retro of deliverable &lt;id&gt;",
    /// which the shell's TeamManageDialog prints from TeamLessons.deliverable) name the completed deliverable, the one
    /// the retro is for, not the retro item's own deliverable.
    /// </summary>
    [Fact]
    public async Task ARetroRun_ReadsTheDigest_WritesTeamRetros_AndTheVersionNamesTheCompletedDeliverable()
    {
        using var db = new TempDb();
        var skills = new SkillCatalog(db.ConnectionString);
        string? retroPrompt = null, laterPrompt = null;
        (string Text, bool IsError)? digest = null, write = null;
        long retroDeliverable = 0;
        var handbacks = 0;
        var adaRuns = 0;
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(m => BusStore.RetroOf(m) is not null))
                {
                    retroDeliverable = run.Deliverable;
                    retroPrompt = TeamPrompt.For(run); // the real launcher builds the prompt; reading the lessons records the version
                    digest = await Call(run, "deliverable_digest", []);
                    write = await Call(run, "team_retros_write", new JsonObject
                    {
                        ["text"] = "## Building and testing\n- Fetch the bundle before merging: the branch moved (2026-11-18, deliverable 1)",
                    });
                    await run.DeliverableCompleteAsync("kept 1 lesson", ct);
                    return RunResult.Ok;
                }
                if (run.Items.Any(m => Payload.Field(m, "text") == "later")) { laterPrompt = TeamPrompt.For(run); return RunResult.Ok; }
                if (run.Items.Any(m => m.Source == MessageTypes.PersonSource))
                {
                    await run.TellAsync("Ada", "part one", ct);
                    await run.TellAsync("Bo", "part two", ct);
                    return RunResult.Ok;
                }
                handbacks += run.Items.Count(m => m.Type == MessageTypes.Handback);
                if (handbacks >= 2) await run.DeliverableCompleteAsync("both parts done", ct);
                return RunResult.Ok;
            })
            .On("Alpha/Ada", async (run, ct) =>
            {
                if (adaRuns++ == 0) await run.BlockedAsync($"the push needs {Token} which I must not use", ct: ct);
                await run.HandbackAsync("part one done", ct);
                return RunResult.Ok;
            })
            .On("Alpha/Bo", async (run, ct) => { await run.HandbackAsync("part two done", ct); return RunResult.Ok; });
        var engine = new BusEngine(db.OpenStore(), runner,
            new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200), Skills = skills, IdleOffer = false });
        await using (engine)
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "ship both parts", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet", TimeSpan.FromSeconds(60));

            using var store = db.OpenStore();
            var retro = Assert.Single(Retros(store));
            Assert.Equal(root.Seq, BusStore.RetroOf(retro));
            Assert.NotEqual(root.Seq, retroDeliverable);

            // No lessons yet: the retro run's prompt has none.
            Assert.DoesNotContain(TeamRetros.PromptHeading, retroPrompt!);
            // The digest is of the completed deliverable, and the blocked reason's token is masked.
            Assert.False(digest!.Value.IsError, digest.Value.Text);
            Assert.StartsWith($"Deliverable {root.Seq} of team Alpha: completed,", digest.Value.Text);
            Assert.Contains("- blocked: «", digest.Value.Text);
            Assert.DoesNotContain(Token, digest.Value.Text);
            Assert.False(write!.Value.IsError, write.Value.Text);

            // A later run of the team gets the lesson in its prompt.
            await engine.PersonTellAsync("Alpha", "Manager", "later", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet again", TimeSpan.FromSeconds(60));
            Assert.Contains(TeamRetros.PromptHeading, laterPrompt!);
            Assert.Contains("- Fetch the bundle before merging", laterPrompt!);

            // Team settings' line: "by the retro of deliverable <id>" must be the deliverable the retro looked back on.
            var team = store.Teams().Single(t => t.Name == "Alpha").Id;
            var lessons = skills.Lessons(team);
            Assert.Equal(1, lessons.Count);
            Assert.Equal("Manager", lessons.Writer);
            Assert.True(lessons.Deliverable == root.Seq,
                $"team_retros version says deliverable {lessons.Deliverable} (the retro item's own deliverable is {retroDeliverable}); " +
                $"the completed deliverable the retro was for is {root.Seq}");

            // Settings > Skills' version history (shell versionWriter: "the retro of deliverable <id>") reads the
            // same version row, so it names the completed deliverable too, and the retro item's own deliverable appears nowhere.
            var version = Assert.Single(skills.Versions(team, TeamRetros.Name));
            Assert.Equal("Manager", version.Writer);
            Assert.Equal(root.Seq, version.Deliverable);
            Assert.NotEqual(retroDeliverable, version.Deliverable);
        }
    }

    /// <summary>An expired deliverable gets no retro: two pieces of work and a failed run (eventful), then it expires.</summary>
    [Fact]
    public async Task AnExpiredDeliverable_GetsNoRetro_HoweverEventful()
    {
        using var db = new TempDb();
        var clock = new DeliverableExpiryTests.ManualClock(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
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
        var engine = new BusEngine(new BusStore(db.ConnectionString, clock), runner,
            new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await using (engine)
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Manager", "ship both parts", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");

            clock.Advance(TimeSpan.FromDays(30));
            Assert.Equal(1, await engine.SweepExpiryAsync(clock.Now, Ct));
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet after expiry");

            using var store = db.OpenStore();
            Assert.Contains(store.Deliverable(root.Seq), m => m.Type == MessageTypes.DeliverableExpired);
            Assert.False(store.DeliverableDeclared(root.Seq));
            Assert.Empty(Retros(store));
        }
    }
}
