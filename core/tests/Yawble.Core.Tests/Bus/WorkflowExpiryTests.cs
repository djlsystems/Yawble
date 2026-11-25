using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Link;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// An open workflow nobody works expires after the team's workflowExpiryDays (default 3); what keeps one from being
/// idle; the warn-then-close path for one waiting on the person's answer; Extend; its cards end; the recorded end is the
/// last real activity; new activity reopens it. Fake clock only: the store's clock and SweepExpiryAsync's now.
/// </summary>
public class WorkflowExpiryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static async Task<(TempDb Db, ManualClock Clock, BusEngine Engine)> Team(ScriptedRunner runner)
    {
        var db = new TempDb();
        var clock = new ManualClock(Start);
        var engine = new BusEngine(new BusStore(db.ConnectionString, clock), runner,
            new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        return (db, clock, engine);
    }

    private static async Task<WorkflowSummary> Listed(BusEngine engine, long id) =>
        Assert.Single(await engine.WorkflowsAsync("Alpha", 50, Ct), w => w.Id == id);

    private static DateTimeOffset LastActivity(TempDb db, long id)
    {
        using var s = db.OpenStore();
        return s.Workflow(id).Where(m => m.Type is not (MessageTypes.WorkflowExpired or BusEngine.WorkflowExpiring)).Max(m => m.OccurredAt);
    }

    [Fact]
    public async Task AnOpenWorkflowNobodyWorks_ExpiresAfterThreeDays_WithItsEndAtTheLastActivity()
    {
        var (db, clock, engine) = await Team(new ScriptedRunner());
        using (db)
        await using (engine)
        {
            var root = await engine.PersonTellAsync("Alpha", "Ada", "look into the flaky test", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            var last = LastActivity(db, root.Seq);

            var open = await Listed(engine, root.Seq);
            Assert.Equal(("open", (DateTimeOffset?)last.AddDays(3)), (open.State, open.ExpiresAt));
            Assert.Null(open.EndedAt);
            Assert.Null(open.Question); // no question asked

            clock.Now = last.AddDays(3).AddMinutes(-1);
            Assert.Equal(0, await engine.SweepExpiryAsync(clock.Now, Ct));
            Assert.Equal("open", (await Listed(engine, root.Seq)).State);

            clock.Now = last.AddDays(3).AddMinutes(1);
            Assert.Equal(1, await engine.SweepExpiryAsync(clock.Now, Ct));
            var expired = await Listed(engine, root.Seq);
            Assert.Equal("expired", expired.State);
            Assert.Equal(last, expired.EndedAt); // the last real activity, not the expiry: no false time
            Assert.Equal(clock.Now, expired.ExpiredAt);
            Assert.Null(expired.ExpiresAt);
            Assert.Equal("no activity for 3 days", expired.Ending);
            using (var s = db.OpenStore())
            {
                Assert.True(s.WorkflowEnded(root.Seq));
                var m = Assert.Single(s.Workflow(root.Seq), x => x.Type == MessageTypes.WorkflowExpired);
                Assert.Equal((MessageTypes.HostSource, "expired"), (m.Source, Payload.Field(m, "reason")));
            }
            Assert.Equal(0, await engine.SweepExpiryAsync(clock.Now.AddDays(10), Ct)); // once
        }
    }

    [Fact]
    public async Task TheTeamSetting_SetsTheDays_WithinOneTo365()
    {
        var (db, clock, engine) = await Team(new ScriptedRunner());
        using (db)
        await using (engine)
        {
            Assert.Equal(BusEngine.DefaultWorkflowExpiryDays, Assert.Single(await engine.TeamsAsync(Ct)).WorkflowExpiryDays);
            Assert.Equal(5, await engine.SetWorkflowExpiryDaysAsync("Alpha", 5, Ct));
            Assert.Equal(5, Assert.Single(await engine.TeamsAsync(Ct)).WorkflowExpiryDays);
            Assert.Equal("bad_days", (await Assert.ThrowsAsync<BusRefusal>(() => engine.SetWorkflowExpiryDaysAsync("Alpha", 0, Ct))).Code);
            Assert.Equal("bad_days", (await Assert.ThrowsAsync<BusRefusal>(() => engine.SetWorkflowExpiryDaysAsync("Alpha", 366, Ct))).Code);
            Assert.Equal(5, await engine.WorkflowExpiryDaysAsync("Alpha", Ct));

            var root = await engine.PersonTellAsync("Alpha", "Ada", "a slow one", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            var last = LastActivity(db, root.Seq);
            Assert.Equal(last.AddDays(5), (await Listed(engine, root.Seq)).ExpiresAt);
            Assert.Equal(0, await engine.SweepExpiryAsync(last.AddDays(4), Ct));
            Assert.Equal(1, await engine.SweepExpiryAsync(last.AddDays(5).AddSeconds(1), Ct));

            Assert.Equal(3, await engine.SetWorkflowExpiryDaysAsync("Alpha", null, Ct)); // back to the default
        }
    }

    [Fact]
    public async Task ARunningRun_IsNotIdle_HoweverLong()
    {
        var release = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) => release.Task);
        var (db, clock, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            var root = await engine.PersonTellAsync("Alpha", "Ada", "a long job", ct: Ct);
            await Wait.UntilAsync(() => runner.Admitted.Count == 1, "Ada running");
            clock.Advance(TimeSpan.FromDays(10));
            Assert.Equal(0, await engine.SweepExpiryAsync(clock.Now, Ct));
            var w = await Listed(engine, root.Seq);
            Assert.Equal(("open", (DateTimeOffset?)null), (w.State, w.ExpiresAt));
            release.SetResult(RunResult.Ok);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
        }
    }

    [Fact]
    public async Task QueuedWork_IsNotIdle()
    {
        var (db, clock, engine) = await Team(new ScriptedRunner());
        using (db)
        await using (engine)
        {
            await engine.PauseTeamAsync("Alpha", Ct); // what it is told waits, undelivered
            var root = await engine.PersonTellAsync("Alpha", "Ada", "when you are back", ct: Ct);
            await Wait.UntilAsync(() => { using var s = db.OpenStore(); return s.Pending().Any(p => p.Correlation == root.Seq); }, "Ada's delivery waiting");
            clock.Advance(TimeSpan.FromDays(10));
            Assert.Equal(0, await engine.SweepExpiryAsync(clock.Now, Ct));
            Assert.Null((await Listed(engine, root.Seq)).ExpiresAt);
        }
    }

    [Fact]
    public async Task AWakeSet_IsNotIdle_UntilItHasFired()
    {
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            await run.WakeMeAsync(60, "check the build again", ct);
            return RunResult.Ok;
        });
        var (db, clock, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            var root = await engine.PersonTellAsync("Alpha", "Ada", "watch the build", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            clock.Advance(TimeSpan.FromDays(10));
            Assert.Equal(0, await engine.SweepExpiryAsync(clock.Now, Ct));
            Assert.Null((await Listed(engine, root.Seq)).ExpiresAt);
        }
    }

    [Fact]
    public async Task AnAutomaticResumeStillToCome_IsNotIdle()
    {
        // The provider says to come back in a month (real time, so the pump's own resume sweep never fires in the test).
        var details = new Dictionary<string, object?> { ["retryAfter"] = DateTimeOffset.UtcNow.AddDays(30).ToString("O") };
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) => Task.FromResult(new RunResult(false, "usage limit", FailureClasses.Rate, details)));
        var (db, clock, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            var root = await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "Ada's limited run and the Manager's run");
            clock.Advance(TimeSpan.FromDays(10));
            Assert.Equal(0, await engine.SweepExpiryAsync(clock.Now, Ct));
            Assert.Null((await Listed(engine, root.Seq)).ExpiresAt);
        }
    }

    private static ScriptedRunner Asking() => new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
    {
        if (run.Items.All(i => i.CausationSeq is null)) await run.NeedsDecisionAsync("MIT or Apache?", ct);
        return RunResult.Ok;
    });

    [Fact]
    public async Task AWorkflowWaitingOnTheAnswer_IsWarnedOnceAtNMinusOne_ThenExpiresAtN()
    {
        var (db, clock, engine) = await Team(Asking());
        var warned = new List<WorkflowExpiryWarning>();
        engine.ExpiryWarned = warned.Add;
        using (db)
        await using (engine)
        {
            var root = await engine.PersonTellAsync("Alpha", "Ada", "add a licence", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            var question = Assert.Single(await engine.OpenDecisionsAsync("Alpha", Ct));
            var last = LastActivity(db, root.Seq);
            Assert.Equal(question.Seq, (await Listed(engine, root.Seq)).Question); // the list item names the question it waits on

            Assert.Equal(0, await engine.SweepExpiryAsync(last.AddDays(2).AddMinutes(-1), Ct));
            Assert.Empty(warned);

            clock.Now = last.AddDays(2).AddMinutes(1);
            Assert.Equal(0, await engine.SweepExpiryAsync(clock.Now, Ct));
            var w = Assert.Single(warned);
            var text = $"#{root.Seq} will expire tomorrow; it waits on your answer to question {question.Seq}";
            Assert.Equal((root.Seq, question.Seq, text), (w.Workflow, w.Question, w.Text));
            Assert.Equal(clock.Now.AddDays(1), w.ExpiresAt); // a full day's notice from the warning
            var listed = await Listed(engine, root.Seq);
            Assert.Equal(("open", text, (DateTimeOffset?)clock.Now.AddDays(1)), (listed.State, listed.ExpiryWarning, listed.ExpiresAt));
            Assert.Equal(text, Assert.Single(await engine.ExpiryWarningsAsync("Alpha", Ct)).Value.Text); // the Needs-you line
            using (var s = db.OpenStore()) Assert.Single(s.Workflow(root.Seq), m => m.Type == BusEngine.WorkflowExpiring);

            Assert.Equal(0, await engine.SweepExpiryAsync(clock.Now.AddHours(1), Ct));
            Assert.Single(warned); // one warning, however many sweeps
            Assert.Equal("open", (await Listed(engine, root.Seq)).State); // the warning is not activity...

            Assert.Equal(1, await engine.SweepExpiryAsync(clock.Now.AddDays(1), Ct)); // ...and at N days it expires
            var expired = await Listed(engine, root.Seq);
            Assert.Equal(("expired", (DateTimeOffset?)last), (expired.State, expired.EndedAt));
            Assert.Null(expired.Question); // an ended workflow waits on nothing
            Assert.Empty(await engine.ExpiryWarningsAsync("Alpha", Ct));
        }
    }

    [Fact]
    public async Task AnAppThatWasNotRunningAtWarningTime_WarnsFirst_AndExpiresADayLater()
    {
        var (db, clock, engine) = await Team(Asking());
        var warned = new List<WorkflowExpiryWarning>();
        engine.ExpiryWarned = warned.Add;
        using (db)
        await using (engine)
        {
            var root = await engine.PersonTellAsync("Alpha", "Ada", "add a licence", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            var late = LastActivity(db, root.Seq).AddDays(6);
            Assert.Equal(0, await engine.SweepExpiryAsync(late, Ct)); // warned, not closed without notice
            Assert.Equal(late.AddDays(1), Assert.Single(warned).ExpiresAt);
            Assert.Equal(0, await engine.SweepExpiryAsync(late.AddHours(23), Ct));
            Assert.Equal(1, await engine.SweepExpiryAsync(late.AddDays(1).AddSeconds(1), Ct));
        }
    }

    [Fact]
    public async Task Extend_ResetsTheClock_AndClearsTheWarning_ButNotForAnEndedWorkflow()
    {
        var (db, clock, engine) = await Team(Asking());
        var warned = new List<WorkflowExpiryWarning>();
        engine.ExpiryWarned = warned.Add;
        using (db)
        await using (engine)
        {
            var root = await engine.PersonTellAsync("Alpha", "Ada", "add a licence", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            var last = LastActivity(db, root.Seq);
            clock.Now = last.AddDays(2).AddHours(1);
            await engine.SweepExpiryAsync(clock.Now, Ct);
            Assert.Single(warned);

            var extended = await engine.ExtendWorkflowAsync("Alpha", root.Seq, Ct);
            Assert.Equal(("open", (DateTimeOffset?)clock.Now.AddDays(3), (string?)null), (extended.State, extended.ExpiresAt, extended.ExpiryWarning));
            Assert.Equal("Alpha", await engine.WorkflowTeamAsync(root.Seq, Ct));
            Assert.Empty(await engine.ExpiryWarningsAsync("Alpha", Ct));

            Assert.Equal(0, await engine.SweepExpiryAsync(last.AddDays(3).AddMinutes(1), Ct)); // the old expiry passes harmlessly
            Assert.Equal(0, await engine.SweepExpiryAsync(clock.Now.AddDays(2).AddMinutes(1), Ct));
            Assert.Equal(2, warned.Count); // warned again a day before the new expiry
            Assert.Equal(1, await engine.SweepExpiryAsync(clock.Now.AddDays(3).AddMinutes(1), Ct));

            var refused = await Assert.ThrowsAsync<BusRefusal>(() => engine.ExtendWorkflowAsync("Alpha", root.Seq, Ct));
            Assert.Equal("already_ended", refused.Code);
        }
    }

    [Fact]
    public async Task NewActivity_ReopensAnExpiredWorkflow_WhichCanExpireAgain()
    {
        var (db, clock, engine) = await Team(new ScriptedRunner());
        using (db)
        await using (engine)
        {
            var root = await engine.PersonTellAsync("Alpha", "Ada", "look into it", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            clock.Now = LastActivity(db, root.Seq).AddDays(4);
            Assert.Equal(1, await engine.SweepExpiryAsync(clock.Now, Ct));

            await engine.PersonTellAsync("Alpha", "Ada", "it is back, have another look", root.Seq, Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet again");
            using (var s = db.OpenStore()) Assert.False(s.WorkflowEnded(root.Seq));
            var reopened = await Listed(engine, root.Seq);
            Assert.Equal(("open", (DateTimeOffset?)null, (DateTimeOffset?)null), (reopened.State, reopened.EndedAt, reopened.ExpiredAt));
            Assert.Equal(clock.Now.AddDays(3), reopened.ExpiresAt);

            Assert.Equal(1, await engine.SweepExpiryAsync(clock.Now.AddDays(3).AddMinutes(1), Ct));
            using (var s = db.OpenStore()) Assert.Equal(2, s.Workflow(root.Seq).Count(m => m.Type == MessageTypes.WorkflowExpired));
        }
    }

    [Fact]
    public async Task AnExpiredWorkflowsCards_EndAtItsLastActivity_AndNewWorkMakesThemLiveAgain()
    {
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            if (run.Items.All(i => i.CausationSeq is null)) await run.BlockedAsync("no access to the registry", ct: ct);
            return RunResult.Ok;
        });
        var (db, clock, engine) = await Team(runner);
        using (db)
        await using (engine)
        {
            var board = new BoardStore(db.ConnectionString, clock);
            var projector = new BoardProjector(board);
            void Project() { using var s = db.OpenStore(); foreach (var m in s.MessagesFrom(Math.Max(0, board.Cursor()), 1000)) { projector.Apply(m); board.SetCursor(m.Seq); } }

            var root = await engine.PersonTellAsync("Alpha", "Ada", "publish the package", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            Project();
            var card = Assert.Single(board.CardsOfWorkflow(root.Seq));
            Assert.Equal((Lanes.NeedsYou, (string?)null), (card.Lane, card.EndedAt));

            var last = LastActivity(db, root.Seq);
            Assert.Equal(1, await engine.SweepExpiryAsync(last.AddDays(3).AddSeconds(1), Ct));
            Project();
            card = board.Card(card.Id)!;
            Assert.Equal((Lanes.Done, "done", "expired"), (card.Lane, card.Status, card.EndedHow));
            Assert.Equal(last, DateTimeOffset.Parse(card.EndedAt!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains(board.Trail(card.Id), t => t.Kind == "workflow expired");

            await engine.PersonTellAsync("Alpha", "Ada", "the registry is open now", root.Seq, Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "Ada ran again");
            Project();
            card = board.Card(card.Id)!;
            Assert.Equal(("completed", (string?)null, (string?)null), (card.Status, card.EndedAt, card.EndedHow)); // live again: it ran
            Assert.Contains(board.Trail(card.Id), t => t.Kind == "told" && t.Text == "the registry is open now");
        }
    }

    // ---------------------------------------------------------------- the board's other ends, without the engine

    private long _seq = 100;

    private Message M(string type, object payload, string source, long workflow, DateTimeOffset at) =>
        new(++_seq, type, JsonSerializer.Serialize(payload), source, workflow, null, 0, at);

    [Fact]
    public void ClosingOrCompletingAWorkflow_EndsEveryCardNotEndedYet_WithHowItEnded()
    {
        using var db = new TempDb();
        var board = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(board);
        var at = Start.AddHours(5);
        p.Apply(M("agent.instruction.payments/Priya", new { text = "write it" }, "payments/Manager", 7, Start));
        p.Apply(M("agent.instruction.payments/Wren", new { text = "test it" }, "payments/Manager", 7, Start));
        p.Apply(M(MessageTypes.Blocked, new { reason = "no key" }, "payments/Wren", 7, Start));
        p.Apply(M(MessageTypes.WorkflowClosed, new { reason = "not needed" }, MessageTypes.PersonSource, 7, at));
        var cards = board.CardsOfWorkflow(7);
        Assert.All(cards, c => Assert.Equal((Lanes.Done, "closed"), (c.Lane, c.EndedHow)));
        Assert.All(cards, c => Assert.Equal(at, DateTimeOffset.Parse(c.EndedAt!, System.Globalization.CultureInfo.InvariantCulture)));

        // Completed: a card waiting on a person stays where it is, but its time stops too.
        p.Apply(M("agent.instruction.payments/Priya", new { text = "next" }, "payments/Manager", 8, Start));
        p.Apply(M("agent.instruction.payments/Wren", new { text = "check" }, "payments/Manager", 8, Start));
        p.Apply(M(MessageTypes.NeedsDecision, new { question = "which?" }, "payments/Wren", 8, Start));
        p.Apply(M(MessageTypes.WorkflowCompleted, new { delivered = "shipped" }, "payments/Manager", 8, at));
        var done = board.CardsOfWorkflow(8);
        Assert.All(done, c => Assert.Equal("completed", c.EndedHow));
        Assert.Equal(Lanes.NeedsYou, done.Single(c => c.Member == "Wren").Lane);
    }

    // ---------------------------------------------------------------- the Concierge

    [Fact]
    public async Task TheConcierge_ReadsExpiredInTeamWorkflows_AndExtendsThroughTheWindowsRpc_OnlyInThePersonsTurn()
    {
        var (db, clock, engine) = await Team(new ScriptedRunner());
        using (db)
        await using (engine)
        {
            var quiet = await engine.PersonTellAsync("Alpha", "Ada", "a quiet one", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            clock.Now = LastActivity(db, quiet.Seq).AddDays(1);
            var open = await engine.PersonTellAsync("Alpha", "Ada", "an open one", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet again");
            await engine.SweepExpiryAsync(clock.Now.AddDays(2).AddMinutes(1), Ct); // the first one is 3 days quiet

            var calls = new List<(string Method, JsonObject? P)>();
            var store = new Yawble.Core.Concierge.ConciergeStore(db.ConnectionString);
            Yawble.Core.Concierge.ConciergeTools Tools(Yawble.Core.Concierge.ConciergeTurn turn) => new(engine, store, (m, p, _) =>
            {
                calls.Add((m, p));
                return Task.FromResult<System.Text.Json.Nodes.JsonNode?>(new System.Text.Json.Nodes.JsonObject());
            }, () => null, "c1", clock, null, false, turn);
            var person = new Yawble.Core.Concierge.ConciergeTurn("c1", byPerson: true, remote: false);

            var (list, _) = await Tools(person).CallToolAsync("team_workflows", new() { ["team"] = "Alpha" }, Ct);
            Assert.Contains($"#{quiet.Seq} [expired] a quiet one", list);
            Assert.Contains("expired: no activity for 3 days", list);
            Assert.Contains($"#{open.Seq} [open, nobody working it] an open one", list);
            Assert.Contains("workflow_extend resets the clock", list);

            var (said, error) = await Tools(person).CallToolAsync("workflow_extend", new() { ["team"] = "Alpha", ["workflow"] = open.Seq }, Ct);
            Assert.False(error, said);
            var call = Assert.Single(calls);
            Assert.Equal("bus.workflowExtend", call.Method);
            Assert.Equal(open.Seq, call.P!["workflow"]!.GetValue<long>());

            var review = new Yawble.Core.Concierge.ConciergeTurn("c1", byPerson: false, remote: false);
            var (refused, isError) = await Tools(review).CallToolAsync("workflow_extend", new() { ["team"] = "Alpha", ["workflow"] = open.Seq }, Ct);
            Assert.True(isError);
            Assert.Contains("not started by a message from the person", refused);
            Assert.Single(calls);
        }
    }

    // ---------------------------------------------------------------- where the state shows

    [Fact]
    public void ExpiredReadsExpired_OnTheCloudDashboard_AndInTheConciergesUpdates()
    {
        Assert.Contains(OrgSummary.Expired, OrgSummary.WorkflowStates);
        Assert.True(OrgSummary.Ended(OrgSummary.Expired));
        var w = new WorkflowSummary(4, "t", "Ada", "expired", false, [], "no activity for 3 days", Start, Start, 2, Start, Start.AddDays(3));
        Assert.Equal(OrgSummary.Expired, AppOrgSummarySource.StateOf(w, askingThePerson: true, blocked: false, ownerFailed: false));

        var expired = new Message(9, MessageTypes.WorkflowExpired, JsonSerializer.Serialize(new { reason = "expired", detail = "no activity for 3 days" }),
            MessageTypes.HostSource, 4, 8, 3, Start);
        var update = Yawble.Core.Concierge.ConciergeWatcher.Describe(expired, "Alpha");
        Assert.Equal("expired", update?.Kind);
        Assert.Contains("#4 expired: no activity for 3 days", update?.Text);
        Assert.True(Yawble.Core.Ui.UiRpc.Handles("workflowExtend"));
    }
}
