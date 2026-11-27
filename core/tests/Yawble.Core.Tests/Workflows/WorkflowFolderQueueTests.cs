using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Workflows.Library;
using Yawble.Core.Workflows.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;
using RunStatus = Yawble.Core.Workflows.Running.WorkflowRunStatus;

namespace Yawble.Core.Tests.Workflows;

/// <summary>
/// A folder trigger's change that fires while the workflow member's run is going (or parked waiting on a tell) is queued, its
/// file lists merged, and ONE run starts with them when that run ends; every/cron fires are still skipped and noted. The queue is
/// stored, so a restart keeps it; a queued fire whose trigger was deleted is dropped.
/// </summary>
public class WorkflowFolderQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-24T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static string[] Strings(JsonNode? node) => (node as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToArray();

    private static TriggerDraft FolderTrigger(long member, string folder) =>
        new(null, member, "inbox", new TriggerSchedule(TriggerKinds.Folder, 3600, null, null, null), "", true, true,
            Watch: new FolderWatch(FolderWatch.Host, folder, null, 0, FolderWatch.AnyChange));

    // ------------------------------------------------------------------ the merge

    [Fact]
    public void Merge_ListsAFileOnce_WithItsLatestKind_InFirstSeenOrder_PerTrigger()
    {
        var q = WorkflowFolderQueue.Merge([], "trigger:1", WorkflowPayloads.FolderChange("C:/in", ["a.pdf", "b.pdf"], [], []));
        q = WorkflowFolderQueue.Merge(q, "trigger:2", WorkflowPayloads.FolderChange("C:/other", ["x.pdf"], [], []));
        q = WorkflowFolderQueue.Merge(q, "trigger:1", WorkflowPayloads.FolderChange("C:/in", ["c.pdf"], ["b.pdf"], ["a.pdf"]));

        q = WorkflowFolderQueue.FromJson(WorkflowFolderQueue.ToJson(q)); // as stored
        Assert.Equal(["trigger:1", "trigger:2"], q.Select(e => e.Source));
        Assert.Equal([("a.pdf", "changed"), ("b.pdf", "removed"), ("c.pdf", "added")], q[0].Files);
        var p = q[0].Payload();
        Assert.Equal("C:/in", p["root"]!.GetValue<string>());
        Assert.Equal(["c.pdf"], Strings(p["added"]));
        Assert.Equal(["b.pdf"], Strings(p["removed"]));
        Assert.Equal(["a.pdf"], Strings(p["changed"]));
        Assert.Equal(1L, q[0].TriggerId);
        Assert.Empty(WorkflowFolderQueue.FromJson("not json"));
    }

    // ------------------------------------------------------------------ a run going (the fake engine)

    private sealed class World : IAsyncDisposable
    {
        public required TempDb Db { get; init; }
        public required BusEngine Bus { get; init; }
        public required WorkflowLibrary Library { get; init; }
        public required WorkflowRunStore Store { get; init; }
        public required WorkflowRunner Runner { get; set; }
        public required FakeEngine Engine { get; init; }
        public long TeamId { get; init; }
        public long WorkflowId { get; set; }

        public WorkflowRunner NewRunner() => new(Store, Library, new FakeActionCatalog(), Engine, Path.Combine(Db.Dir, "workflows")) { Decisions = new FakeDecisions() };

        public async Task<long> WorkflowMemberAsync(string name)
        {
            var row = await Bus.AddWorkflowMemberAsync(TeamId, name, Ct);
            Store.SetWorkflowMember(row.Id, WorkflowId, null, [], [], null);
            return row.Id;
        }

        public MemberRef Member(long id)
        {
            using var s = Db.OpenStore();
            return s.Member(id)!;
        }

        public TriggerRow Trigger(long id)
        {
            using var s = Db.OpenStore();
            return s.Trigger(id)!;
        }

        public string Inbox()
        {
            var inbox = Path.Combine(Db.Dir, "inbox");
            Directory.CreateDirectory(inbox);
            return inbox;
        }

        /// <summary>Holds a run of the member at step b (the run is going).</summary>
        public async Task HoldARunAsync(long member)
        {
            Engine.HoldAt = "b";
            Assert.True(Runner.Start(member, "run now", null).Started);
            await Engine.Holding.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.True(Runner.IsRunning(member));
        }

        public async ValueTask DisposeAsync()
        {
            Engine.Gate.TrySetResult();
            await Runner.StopAsync();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static async Task<World> WorldAsync()
    {
        var db = new TempDb();
        WorkflowRunner? runner = null;
        var bus = new BusEngine(db.OpenStore(), new WorkflowDispatchRunner(new ScriptedRunner(), () => runner), new BusOptions { IdleOffer = false });
        var teamId = await bus.CreateTeamAsync("Alpha", ct: Ct);
        var library = new WorkflowLibrary(db.ConnectionString, () => new FakeActionCatalog(), FakeChecker.Check);
        var store = new WorkflowRunStore(db.ConnectionString);
        var engine = new FakeEngine();
        runner = new WorkflowRunner(store, library, new FakeActionCatalog(), engine, Path.Combine(db.Dir, "workflows")) { Decisions = new FakeDecisions() };
        bus.WorkflowMembers = runner;
        var world = new World { Db = db, Bus = bus, Library = library, Store = store, Runner = runner, Engine = engine, TeamId = teamId };
        world.WorkflowId = library.Save(Docs.Clean()).Id;
        await bus.StartAsync(Ct);
        return world;
    }

    /// <summary>Sweeps the folder trigger an hour on each time until it fires once more (a change seen, then settled).</summary>
    private static async Task<DateTimeOffset> SweepUntilFiredAsync(World w, long trigger, DateTimeOffset now)
    {
        var before = w.Trigger(trigger).LastFiredAt;
        for (var i = 0; i < 5; i++)
        {
            now = now.AddHours(1);
            await w.Bus.SweepTriggersAsync(now, Ct);
            if (w.Trigger(trigger).LastFiredAt != before) return now;
        }
        Assert.Fail("the folder trigger did not fire");
        return now;
    }

    [Fact]
    public async Task FolderChangesWhileARunIsGoing_AreQueuedMerged_AndRunOnceWhenItEnds()
    {
        await using var w = await WorldAsync();
        var bot = await w.WorkflowMemberAsync("Filer");
        var inbox = w.Inbox();
        var t = await w.Bus.SaveTriggerAsync(FolderTrigger(bot, inbox), Ct);
        var now = t.NextDueAt!.Value;
        await w.Bus.SweepTriggersAsync(now, Ct); // remembers the empty folder
        await w.HoldARunAsync(bot);

        File.WriteAllText(Path.Combine(inbox, "a.pdf"), "a");
        now = await SweepUntilFiredAsync(w, t.Id, now);
        var row = w.Trigger(t.Id);
        Assert.Equal("fired", row.LastOutcome); // recorded fired, the reason saying it was queued
        Assert.StartsWith("queued: a run of this member was going", row.LastReason);
        File.WriteAllText(Path.Combine(inbox, "a.pdf"), "a, longer"); // changed
        File.WriteAllText(Path.Combine(inbox, "b.pdf"), "b"); // added
        await SweepUntilFiredAsync(w, t.Id, now);

        Assert.Single(w.Store.RecentRuns(null, bot, 10)); // nothing skipped, nothing started yet
        Assert.Single(WorkflowFolderQueue.FromJson(w.Store.FolderQueue(bot)));

        w.Engine.Gate.SetResult();
        await w.Runner.WhenIdleAsync();

        var runs = w.Store.RecentRuns(null, bot, 10);
        Assert.Equal([RunStatus.Succeeded, RunStatus.Succeeded], runs.Select(r => r.Status));
        var follow = runs.First();
        Assert.Equal($"trigger:{t.Id}", follow.Trigger);
        Assert.Equal(inbox, follow.Input!["root"]!.GetValue<string>());
        Assert.Equal(["b.pdf"], Strings(follow.Input["added"]));
        Assert.Equal(["a.pdf"], Strings(follow.Input["changed"])); // added, then changed: listed once, as changed
        Assert.Empty(Strings(follow.Input["removed"]));
        Assert.Equal(2, w.Engine.Requests.Count);
        Assert.Null(w.Store.FolderQueue(bot)); // the queue is empty once its run started
    }

    [Fact]
    public async Task AnEveryFireWhileARunIsGoing_IsStillSkippedAndNoted_AndQueuesNothing()
    {
        await using var w = await WorldAsync();
        var bot = await w.WorkflowMemberAsync("Poller");
        await w.HoldARunAsync(bot);
        var t = await w.Bus.SaveTriggerAsync(new TriggerDraft(null, bot, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "", true, true), Ct);

        await w.Bus.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);

        var row = w.Trigger(t.Id);
        Assert.Equal("skipped", row.LastOutcome);
        Assert.Contains("one run at a time", row.LastReason);
        var skipped = w.Store.RecentRuns(null, bot, 10).First();
        Assert.Equal((RunStatus.Skipped, $"trigger:{t.Id}"), (skipped.Status, skipped.Trigger));
        Assert.Null(w.Store.FolderQueue(bot));
        w.Engine.Gate.SetResult();
        await w.Runner.WhenIdleAsync();
        Assert.Equal([RunStatus.Skipped, RunStatus.Succeeded], w.Store.RecentRuns(null, bot, 10).Select(r => r.Status));
    }

    [Fact]
    public async Task TheQueue_IsKeptOverARestart_AndRunsWhenTheResumedRunEnds()
    {
        await using var w = await WorldAsync();
        var bot = await w.WorkflowMemberAsync("Filer");
        var t = await w.Bus.SaveTriggerAsync(FolderTrigger(bot, w.Inbox()), Ct);
        await w.HoldARunAsync(bot);
        var fire = w.Runner.FireWorkflow(w.Member(bot), $"trigger:{t.Id}", WorkflowPayloads.FolderChange("C:/in", ["a.pdf"], [], []), queueWhileBusy: true);
        Assert.Equal("queued", fire.Outcome);

        await w.Runner.StopAsync(); // the app stops: the run is left running, the queue stays stored
        Assert.NotNull(w.Store.FolderQueue(bot));
        w.Engine.HoldAt = null;
        w.Runner = w.NewRunner();
        w.Bus.WorkflowMembers = w.Runner;
        Assert.Single(w.Runner.ResumeInterrupted());
        await w.Runner.WhenIdleAsync();

        var runs = w.Store.RecentRuns(null, bot, 10);
        Assert.Equal([RunStatus.Succeeded, RunStatus.Succeeded], runs.Select(r => r.Status));
        Assert.Equal(["a.pdf"], Strings(runs.First().Input!["added"]));
        Assert.Null(w.Store.FolderQueue(bot));
    }

    // ------------------------------------------------------------------ a run waiting on its tell (the real engine)

    private const string WaitingWorkflow = """
        { "format": "yawble-workflow/1", "id": "tell-wait", "name": "tell-wait", "trigger": { "accepts": ["once", "folder"] },
          "start": "a",
          "steps": [
            { "id": "a", "type": "tell", "member": "Manager", "team": "Alpha", "text": "Look at the inbox.", "wait": true, "timeout": "2h", "next": "c" },
            { "id": "c", "type": "end" } ] }
        """;

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public BusEngine Bus = null!;
        public WorkflowRunStore Store = null!;
        public WorkflowRunner Runner = null!;
        public long Bot;

        public async Task<Rig> InitAsync()
        {
            WorkflowRunner? runner = null;
            Bus = new BusEngine(Db.OpenStore(), new WorkflowDispatchRunner(new ScriptedRunner(), () => runner), new BusOptions { IdleOffer = false });
            var alpha = await Bus.CreateTeamAsync("Alpha", ct: Ct);
            var library = new WorkflowLibrary(Db.ConnectionString, () => FakeCatalog.MailAndSms());
            Store = new WorkflowRunStore(Db.ConnectionString);
            var tells = new BusWorkflowTells(Bus, Store);
            Runner = runner = new WorkflowRunner(Store, library, NoWorkflowActions.Instance, SeamWorkflowEngine.Instance, Path.Combine(Db.Dir, "workflows"),
                clock: new FakeClock(T0)) { Tellers = tells, WaitSources = [tells] };
            Bus.WorkflowMembers = runner;
            var saved = library.Save(WaitingWorkflow);
            Assert.True(saved.Ok, saved.Check.ToJsonString());
            await Bus.StartAsync(Ct);
            var row = await Bus.AddWorkflowMemberAsync(alpha, "Bot", Ct);
            Store.SetWorkflowMember(row.Id, saved.Id, null, [], [], null);
            Bot = row.Id;
            return this;
        }

        public async Task<long> FolderTriggerAsync()
        {
            var inbox = Path.Combine(Db.Dir, "inbox");
            Directory.CreateDirectory(inbox);
            return (await Bus.SaveTriggerAsync(FolderTrigger(Bot, inbox), Ct)).Id;
        }

        public TriggerFire Fire(long trigger, string[] added, string[] changed)
        {
            using var s = Db.OpenStore();
            return Runner.FireWorkflow(s.Member(Bot)!, $"trigger:{trigger}", WorkflowPayloads.FolderChange("C:/in", added, [], changed), queueWhileBusy: true);
        }

        /// <summary>The bot's run parked on its tell: its deliverable.</summary>
        public async Task<long> ParkAsync()
        {
            var parked = await Runner.RunMemberAsync(Bot, "run now", WorkflowPayloads.TriggerFired(1, "once", T0), Ct);
            Assert.Equal(RunStatus.Waiting, parked.Run.Status);
            return parked.Run.WaitDeliverableId!.Value;
        }

        public async Task EndTheWaitAsync(long deliverable)
        {
            using (var s = Db.OpenStore())
                s.Append(new NewMessage(MessageTypes.DeliverableCompleted, new JsonObject { ["delivered"] = "Done." }.ToJsonString(), "Alpha/Manager", deliverable));
            Assert.Single(await Runner.ResolveWaitsAsync(Ct));
            await Runner.WhenIdleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Runner.StopAsync();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task TwoFolderFiresWhileARunWaits_StartOneFollowUpRun_WithTheMergedList()
    {
        await using var r = await new Rig().InitAsync();
        var trigger = await r.FolderTriggerAsync();
        var deliverable = await r.ParkAsync();

        var first = r.Fire(trigger, ["a.pdf", "b.pdf"], []);
        var second = r.Fire(trigger, ["c.pdf", "a.pdf"], ["b.pdf"]);

        Assert.Equal(("queued", "queued"), (first.Outcome, second.Outcome));
        Assert.Contains("was waiting on deliverable", second.Reason);
        Assert.Single(r.Store.RecentRuns(null, r.Bot, 10)); // no skipped runs
        await r.EndTheWaitAsync(deliverable);

        var runs = r.Store.RecentRuns(null, r.Bot, 10);
        Assert.Equal(2, runs.Count);
        Assert.Equal(RunStatus.Succeeded, runs[1].Status);
        var follow = runs[0];
        Assert.Equal($"trigger:{trigger}", follow.Trigger);
        Assert.Equal(["a.pdf", "c.pdf"], Strings(follow.Input!["added"]));
        Assert.Equal(["b.pdf"], Strings(follow.Input["changed"]));
        Assert.Null(r.Store.FolderQueue(r.Bot));
    }

    [Fact]
    public async Task AQueuedFireWhoseTriggerWasDeleted_IsDropped()
    {
        await using var r = await new Rig().InitAsync();
        var trigger = await r.FolderTriggerAsync();
        var deliverable = await r.ParkAsync();
        Assert.Equal("queued", r.Fire(trigger, ["a.pdf"], []).Outcome);

        Assert.True(await r.Bus.DeleteTriggerAsync(trigger, Ct));
        await r.EndTheWaitAsync(deliverable);

        var run = Assert.Single(r.Store.RecentRuns(null, r.Bot, 10)); // the run that waited, and no follow-up
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Null(r.Store.FolderQueue(r.Bot));
    }
}
