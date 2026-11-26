using Yawble.Core.Bus;
using Yawble.Core.Logs;
using Yawble.Core.Teams;
using Yawble.Core.Triggers;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// The bus gate is not held over slow file-system work: a folder trigger's folder is listed, and a deleted team's
/// folder is removed, while other callers take the gate. A hold that reaches the limit is written as one warning with
/// the holder's name. The listing and the removal are fakes that wait for the test.
/// </summary>
public class GateHoldTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(20);

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public BusEngine Engine = null!;
        public TeamRepos Repos = null!;
        public TriggerRow Trigger = null!;
        public string Inbox = "";

        public static async Task<Rig> CreateAsync(BusOptions? options = null)
        {
            var rig = new Rig();
            rig.Repos = new TeamRepos(rig.Db.Dir, rig.Db.ConnectionString);
            rig.Engine = new BusEngine(rig.Db.OpenStore(), new ScriptedRunner(), new BusOptions
            {
                Repos = rig.Repos, IdleOffer = false, SafetyPoll = TimeSpan.FromMilliseconds(50),
                SlowGateHold = options?.SlowGateHold ?? TimeSpan.FromHours(1),
                SlowGateReportEvery = options?.SlowGateReportEvery ?? TimeSpan.FromSeconds(10),
            });
            await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            var ada = await rig.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            rig.Inbox = Path.Combine(rig.Repos.TeamRoot("Alpha"), TeamDocuments.Folder, "inbox");
            Directory.CreateDirectory(rig.Inbox);
            rig.Trigger = await rig.Engine.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "new scans",
                new TriggerSchedule(TriggerKinds.Folder, 30, null, null, null), "File each new scan.", false, true,
                Watch: new FolderWatch(FolderWatch.Documents, "inbox", null, 0)), Ct);
            return rig;
        }

        public string? WatchState()
        {
            using var store = Db.OpenStore();
            return store.WatchState(Trigger.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    /// <summary>A listing that tells the test it has started and waits to be let go.</summary>
    private sealed class HeldListing
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        public int Calls;

        public Dictionary<string, long> List(string folder, string? glob)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            Assert.True(Release.Wait(TimeSpan.FromSeconds(60)), "the test lets the listing go");
            return FolderWatcher.Listing(folder, glob);
        }
    }

    [Fact]
    public async Task ASweep_ListsAFolderTriggersFolder_WithoutHoldingTheGate()
    {
        await using var rig = await Rig.CreateAsync();
        var listing = new HeldListing();
        rig.Engine.ListFolder = listing.List;

        var sweep = rig.Engine.SweepTriggersAsync(rig.Trigger.NextDueAt!.Value, Ct);
        await listing.Started.Task.WaitAsync(Soon, Ct);

        // The folder is being listed: the gate is free for everyone else.
        Assert.Single(await rig.Engine.TeamsAsync(Ct).WaitAsync(Soon, Ct));
        await rig.Engine.PersonTellAsync("Alpha", "Ada", "while you list", ct: Ct).WaitAsync(Soon, Ct);
        Assert.False(sweep.IsCompleted);
        Assert.Null(rig.WatchState());

        listing.Release.Set();
        await sweep.WaitAsync(Soon, Ct);
        Assert.NotNull(rig.WatchState()); // the first look remembers the folder
        Assert.Equal(1, listing.Calls);
    }

    [Fact]
    public async Task ATriggerEditedWhileItsFolderIsListed_WaitsForItsNextLook()
    {
        await using var rig = await Rig.CreateAsync();
        var listing = new HeldListing();
        rig.Engine.ListFolder = listing.List;
        var now = rig.Trigger.NextDueAt!.Value;

        var sweep = rig.Engine.SweepTriggersAsync(now, Ct);
        await listing.Started.Task.WaitAsync(Soon, Ct);
        var edited = await rig.Engine.SaveTriggerAsync(new TriggerDraft(rig.Trigger.Id, rig.Trigger.MemberId, rig.Trigger.Name, rig.Trigger.Schedule,
            rig.Trigger.Instruction, false, true, Watch: rig.Trigger.Watch! with { Glob = "*.pdf" }), Ct);
        listing.Release.Set();
        await sweep.WaitAsync(Soon, Ct);

        // The listing was of every file; the trigger watches *.pdf: nothing of it is recorded.
        Assert.Null(rig.WatchState());
        using var store = rig.Db.OpenStore();
        Assert.Equal(edited.NextDueAt, store.Trigger(rig.Trigger.Id)!.NextDueAt);
    }

    [Fact]
    public async Task ThePump_ListsFoldersInTheBackground_AndRecordsThemWhenTheListingEnds()
    {
        await using var rig = await Rig.CreateAsync();
        var listing = new HeldListing();
        rig.Engine.ListFolder = listing.List;
        var due = rig.Trigger.NextDueAt!.Value.AddSeconds(1);
        rig.Engine.TriggerClock = () => due;
        await rig.Engine.StartAsync(Ct);
        await listing.Started.Task.WaitAsync(Soon, Ct);

        // The pump goes on delivering while the folder is listed.
        await rig.Engine.PersonTellAsync("Alpha", "Ada", "while you list", ct: Ct).WaitAsync(Soon, Ct);
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "Ada's run took the instruction");
        Assert.Null(rig.WatchState());

        listing.Release.Set();
        await Wait.UntilAsync(() => rig.WatchState() is not null, "the look is recorded");
    }

    [Fact]
    public async Task DeletingATeam_RemovesItsFolderAfterTheGate_AndKeepsItsNameUntilTheFolderIsGone()
    {
        await using var rig = await Rig.CreateAsync();
        var folder = rig.Repos.TeamRoot("Alpha");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var teamsAtRemoval = -1;
        rig.Engine.RemoveFolder = path =>
        {
            Assert.Equal(folder, path);
            started.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "the test lets the removal go");
            Directory.Delete(path, recursive: true);
        };

        var delete = rig.Engine.DeleteTeamAsync("Alpha", Ct);
        await started.Task.WaitAsync(Soon, Ct);

        // The team is gone from the database, its folder is still there, and the gate is free.
        teamsAtRemoval = (await rig.Engine.TeamsAsync(Ct).WaitAsync(Soon, Ct)).Count;
        Assert.Equal(0, teamsAtRemoval);
        Assert.True(Directory.Exists(folder));
        Assert.False(delete.IsCompleted);
        await rig.Engine.CreateTeamAsync("Beta", ct: Ct).WaitAsync(Soon, Ct);
        var refused = await Assert.ThrowsAsync<BusRefusal>(() => rig.Engine.CreateTeamAsync("alpha", ct: Ct));
        Assert.Equal("busy", refused.Code);
        Assert.Equal(["Beta"], (await rig.Engine.TeamsAsync(Ct)).Select(t => t.Name));

        release.Set();
        Assert.Empty(await delete.WaitAsync(Soon, Ct));
        Assert.False(Directory.Exists(folder));
        await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
    }

    [Fact]
    public async Task AFolderThatCannotBeRemoved_IsReported_AndTheNameIsFreeAgain()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Engine.RemoveFolder = _ => throw new IOException("in use");

        var left = await rig.Engine.DeleteTeamAsync("Alpha", Ct);

        Assert.Contains("in use", Assert.Single(left));
        await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
    }

    [Fact]
    public async Task AGateHoldAtTheLimit_IsWrittenOnce_WithTheHoldersName()
    {
        var entries = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (entries) entries.Add(e); });
        await using var rig = await Rig.CreateAsync(new BusOptions { SlowGateHold = TimeSpan.Zero, SlowGateReportEvery = TimeSpan.FromHours(1) });

        // Every hold reaches a limit of zero; the rig's own calls wrote the one line the hour allows.
        await rig.Engine.TeamsAsync(Ct);
        await rig.Engine.RunSlotsAsync(Ct);

        DiagEntry[] held;
        lock (entries) held = [.. entries.Where(e => e.Area == "bus" && e.Text.Contains("gate was held"))];
        var line = Assert.Single(held);
        Assert.Equal(LogLevels.Warning, line.Level);
        Assert.Equal(nameof(BusEngine.CreateTeamAsync), line.Field("caller"));
        Assert.NotNull(line.Field("ms"));
        Assert.Equal("1", line.Field("count"));
    }

    [Fact]
    public async Task SlowHoldLines_SayHowManyHoldsTheyStandFor_AndQuickHoldsWriteNothing()
    {
        var entries = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (entries) entries.Add(e); });
        DiagEntry[] Held()
        {
            lock (entries) return [.. entries.Where(e => e.Area == "bus" && e.Text.Contains("gate was held"))];
        }

        await using (var quick = await Rig.CreateAsync()) // the limit is an hour
        {
            await quick.Engine.TeamsAsync(Ct);
            Assert.Empty(Held());
        }

        await using var rig = await Rig.CreateAsync(new BusOptions { SlowGateHold = TimeSpan.Zero, SlowGateReportEvery = TimeSpan.Zero });
        var before = Held().Length;
        await rig.Engine.RunSlotsAsync(Ct);
        await rig.Engine.RunSlotsAsync(Ct);
        var lines = Held().Skip(before).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal(nameof(BusEngine.RunSlotsAsync), l.Field("caller")));
    }
}
