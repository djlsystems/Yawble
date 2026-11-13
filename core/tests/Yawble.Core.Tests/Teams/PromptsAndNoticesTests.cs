using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The role texts skills_get serves, the run prompt, batching shown as numbered items, blocked items, and the
/// Manager's notices on handback, completed and failed (interrupted included).
/// </summary>
public class PromptsAndNoticesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void RoleTexts_TellTheManagerHowToSplitAndDeclare_AndTheMemberToCommitAndNeverPush()
    {
        var manager = global::Yawble.Core.Gateway.RoleText.For("manager");
        Assert.StartsWith("# Manager", manager);
        foreach (var needle in new[] { "`status`", "`tell`", "END YOUR TURN", "handed back", "run ended", "did not hand back", "interrupted",
                     "numbered items", "members/<name>", "never ask a member to push", "`workflow_complete`", "`hire`", "`hiring`", "FULL PATH", "already declared", "`needs_decision`",
                     "Scheduled work (trigger", "act on that job only", "paid model run" })
        {
            Assert.Contains(needle, manager);
        }
        var member = global::Yawble.Core.Gateway.RoleText.For(" Member ");
        Assert.StartsWith("# Member", member);
        foreach (var needle in new[] { "members/<name>", "`git commit`", "Never push", "no remote and no git credential", "`progress`", "`handback`",
                     "`blocked`", "`item` number", "`needs_decision`", "only your Manager",
                     "Scheduled work (trigger", "do not hand back: call `workflow_complete`" })
        {
            Assert.Contains(needle, member);
        }
        Assert.Throws<ArgumentException>(() => global::Yawble.Core.Gateway.RoleText.For("admin"));
        Assert.DoesNotContain("\r", manager + member);
    }

    [Fact]
    public async Task ABatchShowsNumberedItems_BlockedTakesAnItemOfIt_AndTheManagerIsToldWhichCardWasBlocked()
    {
        using var db = new TempDb();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adaPrompts = new List<string>();
        var managerPrompts = new List<string>();
        var refusals = new List<string>();
        var runner = new ScriptedRunner();
        runner.On("Alpha/Ada", async (run, ct) =>
        {
            lock (adaPrompts) adaPrompts.Add(TeamPrompt.For(run));
            if (run.Items.Count == 1 && BatchText.TextOf(run.Items[0]) == "warm up")
            {
                await gate.Task.WaitAsync(ct); // busy while three more arrive
                await run.HandbackAsync("warmed up", ct);
                return RunResult.Ok;
            }
            try { await run.BlockedAsync("no such item", 4, ct); }
            catch (BusRefusal r) { lock (refusals) refusals.Add(r.Code + ": " + r.Message); }
            await run.BlockedAsync("the fixture file is missing", 2, ct);
            await run.HandbackAsync("did 1 and 3; 2 blocked", ct);
            return RunResult.Ok;
        });
        runner.On("Alpha/Bo", async (run, ct) =>
        {
            try { await run.BlockedAsync("x", 2, ct); }
            catch (BusRefusal r) { lock (refusals) refusals.Add(r.Code + ": " + r.Message); }
            await run.BlockedAsync("cannot reach the database", ct: ct); // gives up the whole run, no handback
            return RunResult.Ok;
        });
        runner.On("Alpha/Manager", (run, ct) =>
        {
            lock (managerPrompts) managerPrompts.Add(TeamPrompt.For(run));
            return Task.FromResult(RunResult.Ok);
        });
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100) });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
        await engine.StartAsync(Ct);

        var root = await engine.PersonTellAsync("Alpha", "Ada", "warm up", ct: Ct);
        await Wait.UntilAsync(() => runner.AdmittedNames().Contains("Alpha/Ada"), "Ada running");
        foreach (var text in new[] { "one", "two", "three" }) await engine.PersonTellAsync("Alpha", "Ada", text, root.Seq, Ct);
        gate.SetResult();
        await engine.PersonTellAsync("Alpha", "Bo", "migrate the db", ct: Ct);
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiescent", TimeSpan.FromSeconds(30));

        // Ada's second run: one batch of three numbered items, with how to block one.
        var batch = Assert.Single(adaPrompts, p => p.Contains("3 numbered items", StringComparison.Ordinal));
        Assert.Contains("You are Ada, a member of team Alpha", batch);
        Assert.Contains("\n1. one\n", batch);
        Assert.Contains("\n2. two\n", batch);
        Assert.Contains("\n3. three\n", batch);
        Assert.Contains("blocked and its item number", batch);
        Assert.Contains("no_such_item: item must be 1 to 3", Assert.Single(refusals, r => r.Contains("1 to 3", StringComparison.Ordinal)));
        Assert.Contains(refusals, r => r.StartsWith("no_such_item: this run has one instruction", StringComparison.Ordinal));

        using var store = db.OpenStore();
        var blocked = store.AllMessages().Single(m => m.Type == MessageTypes.Blocked && m.Source == "Alpha/Ada");
        Assert.Equal("two", Payload.Field(blocked, "instruction"));
        Assert.Equal("Ada is blocked on item 2: the fixture file is missing", BatchText.TextOf(blocked));

        // The Manager is woken by the run endings, in sentences, naming the blocked card.
        var notices = string.Join("\n", managerPrompts);
        Assert.Contains("You are Manager, the Manager of team Alpha", notices);
        Assert.Contains("Ada handed back: did 1 and 3; 2 blocked", notices);
        Assert.Contains("Ada's run ended. Blocked on item 2 (\"two\"): the fixture file is missing.", notices);
        Assert.Contains("Bo's run ended. It did not hand back. Blocked: cannot reach the database.", notices);
    }

    [Fact]
    public void FailedAndInterruptedNotices_SayWhatHappenedAndWhatToDo()
    {
        Message M(string type, string payload) => new(1, type, payload, "Alpha/Ada", 1, null, 1, DateTimeOffset.UtcNow);
        Assert.Equal("Ada's run failed: timed out after 30 minutes. It did not hand back.",
            BatchText.TextOf(M(MessageTypes.Failed, """{"runId":3,"reason":"timed out after 30 minutes","failureClass":"failed","handedBack":false,"blocked":[]}""")));
        Assert.StartsWith("Ada's run was interrupted: the app stopped while it ran",
            BatchText.TextOf(M(MessageTypes.Failed, """{"runId":3,"reason":"interrupted: the core stopped","failureClass":"interrupted"}""")));
        Assert.Contains("Tell it again if the work is still needed.",
            BatchText.TextOf(M(MessageTypes.Failed, """{"failureClass":"interrupted"}""")));
        Assert.Equal("Ada's run ended.", BatchText.TextOf(M(MessageTypes.Completed, """{"runId":3,"handedBack":true,"blocked":[]}""")));
        Assert.Equal("Ada's run ended. It did not hand back. It is not stuck: it asked to be woken at 2026-11-09 20:37 UTC to \"retry the build\". Wait for that; do not tell it again.",
            BatchText.TextOf(M(MessageTypes.Completed, """{"runId":3,"handedBack":false,"wake":{"at":"2026-11-09 20:37 UTC","note":"retry the build"},"blocked":[]}""")));
        Assert.Equal("Ada handed back: done", BatchText.TextOf(M(MessageTypes.Handback, """{"delivered":"done"}""")));
        Assert.Equal("[odd.type from Alpha/Ada] {}", BatchText.TextOf(M("odd.type", "{}")));
    }
}
