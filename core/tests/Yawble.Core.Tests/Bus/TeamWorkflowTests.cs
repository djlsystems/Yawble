using Yawble.Core.Bus;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// The basic team workflow: Person → tell Manager → Manager tells two members → both hand back →
/// Manager workflow_complete, with fake runners, showing the causation chain and depth of every message.
/// </summary>
public class TeamWorkflowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PersonManagerTwoMembersHandbackAndComplete_CarriesCausationAndDepth()
    {
        using var db = new TempDb();
        var handbacks = new HashSet<string>();
        var refusals = new List<string>();
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(i => i.Source == MessageTypes.PersonSource))
                {
                    await run.ProgressAsync("splitting the work", ct);
                    await run.TellAsync("Ada", "build the parser", ct);
                    await run.TellAsync("Bo", "write the tests", ct);
                    return RunResult.Ok;
                }
                lock (handbacks)
                {
                    foreach (var i in run.Items.Where(i => i.Type == MessageTypes.Handback)) handbacks.Add(i.Source);
                    if (handbacks.Count < 2) return RunResult.Ok;
                }
                try
                {
                    await run.WorkflowCompleteAsync("parser and tests delivered", ct);
                }
                catch (BusRefusal r) when (r.Code == "busy")
                {
                    lock (refusals) refusals.Add(r.Message); // woken again when that work ends
                }
                return RunResult.Ok;
            });
        runner.Default = async (run, ct) =>
        {
            await run.ProgressAsync("working on: " + run.Prompt, ct);
            await run.HandbackAsync($"{run.Member.Name} finished", ct);
            return RunResult.Ok;
        };

        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
        await engine.StartAsync(Ct);

        var root = await engine.PersonTellAsync("Alpha", "Manager", "ship the parser with tests", ct: Ct);
        using var reader = db.OpenStore();
        await Wait.UntilAsync(() => reader.WorkflowDeclared(root.Seq), "workflow.completed");
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to go quiet");

        var log = reader.Workflow(root.Seq);
        foreach (var m in log)
        {
            TestContext.Current.SendDiagnosticMessage(
                $"seq={m.Seq} depth={m.Depth} causation={m.CausationSeq?.ToString() ?? "-"} corr={m.CorrelationId} {m.Type} from {m.Source} {m.Payload}");
        }

        // Every message of the run belongs to the person's workflow, and its depth is its cause's plus one.
        Assert.Equal(log.Count, reader.AllMessages().Count);
        var bySeq = log.ToDictionary(m => m.Seq);
        foreach (var m in log)
        {
            Assert.Equal(root.Seq, m.CorrelationId);
            if (m.CausationSeq is { } cause) Assert.Equal(bySeq[cause].Depth + 1, m.Depth);
        }

        Assert.Equal("agent.instruction.Alpha/Manager", root.Type);
        Assert.Null(root.CausationSeq);
        Assert.Equal(0, root.Depth);

        var tellAda = Assert.Single(log, m => m.Type == "agent.instruction.Alpha/Ada");
        var tellBo = Assert.Single(log, m => m.Type == "agent.instruction.Alpha/Bo");
        foreach (var tell in new[] { tellAda, tellBo })
        {
            Assert.Equal("Alpha/Manager", tell.Source);
            Assert.Equal(root.Seq, tell.CausationSeq);
            Assert.Equal(1, tell.Depth);
        }

        var handbackAda = Assert.Single(log, m => m.Type == MessageTypes.Handback && m.Source == "Alpha/Ada");
        var handbackBo = Assert.Single(log, m => m.Type == MessageTypes.Handback && m.Source == "Alpha/Bo");
        Assert.Equal((tellAda.Seq, 2), (handbackAda.CausationSeq!.Value, handbackAda.Depth));
        Assert.Equal((tellBo.Seq, 2), (handbackBo.CausationSeq!.Value, handbackBo.Depth));

        var managerFirst = Assert.Single(log, m => m.Type == MessageTypes.Completed && m.Source == "Alpha/Manager" && m.CausationSeq == root.Seq);
        Assert.Equal(1, managerFirst.Depth);
        Assert.Contains(log, m => m.Type == MessageTypes.Completed && m.Source == "Alpha/Ada" && m.CausationSeq == tellAda.Seq && m.Depth == 2);

        var declared = Assert.Single(log, m => m.Type == MessageTypes.WorkflowCompleted);
        Assert.Equal("Alpha/Manager", declared.Source);
        Assert.Equal("Alpha/Manager", Payload.Field(declared, "declaredBy"));
        Assert.Null(Payload.Field(declared, "onBehalfOf"));
        // Caused by the report that woke the declaring Manager run: a member's handback or completion (depth 2).
        var waker = bySeq[declared.CausationSeq!.Value];
        Assert.Contains(waker.Source, new[] { "Alpha/Ada", "Alpha/Bo" });
        Assert.Equal(3, declared.Depth);

        // The runs: every one ended completed, nothing left pending or queued.
        var runs = reader.Runs();
        Assert.All(runs, r => Assert.Equal("completed", r.State));
        Assert.Empty(reader.Pending());
        Assert.Empty(reader.Queue());
        Assert.Contains(runs, r => r.CausationSeq == tellAda.Seq);
        Assert.Contains(runs, r => r.CausationSeq == tellBo.Seq);
        TestContext.Current.SendDiagnosticMessage($"runs: {runs.Count}; busy refusals on the way: {refusals.Count} {string.Join(" | ", refusals)}");
    }

    [Fact]
    public async Task MembersMayTellOnlyTheManager_AndTellNeverCrossesTeams()
    {
        using var db = new TempDb();
        var outcomes = new List<string>();
        var runner = new ScriptedRunner();
        runner.On("Alpha/Ada", async (run, ct) =>
        {
            foreach (var to in new[] { "Bo", "Manager", "Ghost" })
            {
                try
                {
                    await run.TellAsync(to, "hello", ct);
                    lock (outcomes) outcomes.Add(to + ":ok");
                }
                catch (BusRefusal r)
                {
                    lock (outcomes) outcomes.Add(to + ":" + r.Code);
                }
            }
            return RunResult.Ok;
        });
        await using var engine = new BusEngine(db.OpenStore(), runner);
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.CreateTeamAsync("Beta", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
        await engine.AddMemberAsync("Beta", "Ghost", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "say hello", ct: Ct);
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");

        Assert.Equal(["Bo:not_allowed", "Manager:ok", "Ghost:no_such_member"], outcomes);
        // Beta's Manager was never woken by Alpha's traffic; Alpha's Manager was (Ada's tell, then her completion).
        Assert.DoesNotContain("Beta/Manager", runner.AdmittedNames());
        Assert.Contains("Alpha/Manager", runner.AdmittedNames());
    }

    [Fact]
    public async Task DepthLimitStopsAPingPongLoop()
    {
        using var db = new TempDb();
        var refusal = new TaskCompletionSource<BusRefusal>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<RunResult> PingPong(RunContext run, string to, CancellationToken ct)
        {
            try
            {
                await run.TellAsync(to, "your turn", ct);
            }
            catch (BusRefusal r)
            {
                refusal.TrySetResult(r);
            }
            return RunResult.Ok;
        }
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", (run, ct) => run.Items.Any(i => i.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
                ? PingPong(run, "Ada", ct)
                : Task.FromResult(RunResult.Ok))
            .On("Alpha/Ada", (run, ct) => PingPong(run, "Manager", ct));
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { DepthLimit = 5 });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Manager", "start the loop", ct: Ct);

        var refused = await refusal.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
        Assert.Equal("depth_limit", refused.Code);
        using var reader = db.OpenStore();
        var instructions = reader.AllMessages().Where(m => m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(5, instructions.Max(m => m.Depth)); // hop 6 was refused
    }

    [Fact]
    public async Task OnlyTheOwnerOrItsManagerDeclares_AndNotWhileWorkIsInFlight()
    {
        using var db = new TempDb();
        var results = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Try(string label, RunContext run, CancellationToken ct)
        {
            try
            {
                var m = await run.WorkflowCompleteAsync("done", ct);
                lock (results) results.Add($"{label}:ok:{Payload.Field(m, "onBehalfOf") ?? "-"}");
            }
            catch (BusRefusal r)
            {
                lock (results) results.Add($"{label}:{r.Code}");
            }
        }
        var runner = new ScriptedRunner()
            .On("Alpha/Manager", async (run, ct) =>
            {
                var root = run.Items[0];
                if (root.Type == "agent.instruction.Alpha/Manager")
                {
                    await run.TellAsync("Ada", "slow work", ct);
                    await Task.Delay(300, ct); // Ada is running now
                    await Try("manager-while-ada-runs", run, ct);
                    release.SetResult();
                }
                else if (run.Items.Any(i => i.Type == MessageTypes.Completed && i.Source == "Alpha/Bo"))
                {
                    await Try("manager-for-bo", run, ct); // Bo's own workflow, declared on his behalf
                }
                return RunResult.Ok;
            })
            .On("Alpha/Ada", async (run, ct) =>
            {
                await Try("ada-not-owner", run, ct);
                await release.Task.WaitAsync(ct);
                await run.HandbackAsync("slow work done", ct);
                return RunResult.Ok;
            })
            .On("Alpha/Bo", async (run, ct) => { await run.ProgressAsync("no declaration from me", ct); return RunResult.Ok; });
        await using var engine = new BusEngine(db.OpenStore(), runner);
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Manager", "coordinate", ct: Ct);
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "first workflow quiet");
        await engine.PersonTellAsync("Alpha", "Bo", "your own job", ct: Ct);
        await Wait.UntilAsync(() => { lock (results) return results.Any(r => r.StartsWith("manager-for-bo", StringComparison.Ordinal)); }, "on-behalf declaration");

        Assert.Contains("ada-not-owner:not_owner", results);
        Assert.Contains("manager-while-ada-runs:busy", results);
        Assert.Contains("manager-for-bo:ok:Alpha/Bo", results);
    }

    [Fact]
    public async Task AQueuedMemberCollectsOneWorkflowIntoOneNumberedBatch()
    {
        using var db = new TempDb();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner().On("Alpha/Blocker", async (run, ct) => { await gate.Task.WaitAsync(ct); return RunResult.Ok; });
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { MaxConcurrentAgents = () => 1 });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Blocker", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);

        await engine.PersonTellAsync("Alpha", "Blocker", "hold the only slot", ct: Ct);
        await Wait.UntilAsync(() => runner.AdmittedNames().Count == 1, "blocker running");
        var w1 = await engine.PersonTellAsync("Alpha", "Ada", "one", ct: Ct);
        var other = await engine.PersonTellAsync("Alpha", "Ada", "another workflow", ct: Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "two", causation: w1.Seq, ct: Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "three", causation: w1.Seq, ct: Ct);
        await Wait.UntilAsync(async () => (await engine.TeamStatusAsync("Alpha", Ct)).Single(s => s.Name == "Ada").QueuedInstructions == 4, "Ada holding 4");
        var ada = (await engine.TeamStatusAsync("Alpha", Ct)).Single(s => s.Name == "Ada");
        Assert.Equal(("queued", 1), (ada.State, ada.QueuePosition!.Value));

        gate.SetResult();
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
        var adaRuns = runner.Admitted.Where(r => r.Member.Name == "Ada").ToList();
        Assert.Equal(2, adaRuns.Count);
        Assert.Equal(["one", "two", "three"], adaRuns[0].Items.Select(BatchText.TextOf));
        Assert.Equal(w1.Seq, adaRuns[0].Causation);
        Assert.Contains("1. one", adaRuns[0].Prompt);
        Assert.Contains("3. three", adaRuns[0].Prompt);
        Assert.Equal(["another workflow"], adaRuns[1].Items.Select(BatchText.TextOf));
        Assert.Equal(other.Seq, adaRuns[1].Correlation);
    }
}
