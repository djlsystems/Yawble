using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Yawble.Core.Triggers;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>Folder-change triggers (Yawble FolderWatch): poll, wait for the change to settle, tell the member what changed.</summary>
public class FolderTriggerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheWatcherRemembersFirst_WaitsForTheChangeToSettle_AndFiresOnce()
    {
        var a = new Dictionary<string, long> { ["a.txt"] = 1 };
        var (fire, state) = FolderWatcher.Look(a, null, T0, 10);
        Assert.Null(fire); // the first look only remembers
        var copying = new Dictionary<string, long> { ["a.txt"] = 1, ["big.pdf"] = 100 };
        (fire, state) = FolderWatcher.Look(copying, state, T0.AddSeconds(30), 10);
        Assert.Null(fire); // a change: wait for it to settle
        var grown = new Dictionary<string, long> { ["a.txt"] = 1, ["big.pdf"] = 5000 };
        (fire, state) = FolderWatcher.Look(grown, state, T0.AddSeconds(35), 10);
        Assert.Null(fire); // still moving: the quiet period starts again
        (fire, state) = FolderWatcher.Look(grown, state, T0.AddSeconds(40), 10);
        Assert.Null(fire); // still, but not for 10 s yet
        (fire, state) = FolderWatcher.Look(grown, state, T0.AddSeconds(46), 10);
        Assert.NotNull(fire);
        Assert.Equal(["big.pdf"], fire!.Added);
        (fire, _) = FolderWatcher.Look(grown, state, T0.AddSeconds(80), 10);
        Assert.Null(fire); // nothing new since
    }

    [Fact]
    public void AddedRemovedAndChangedSize_AreAChange_AndNamesAreFilteredByThePattern()
    {
        var before = new Dictionary<string, long> { ["keep.pdf"] = 1, ["gone.pdf"] = 2, ["grew.pdf"] = 3 };
        var after = new Dictionary<string, long> { ["keep.pdf"] = 1, ["grew.pdf"] = 30, ["new.pdf"] = 4 };
        var d = FolderWatcher.Diff(before, after);
        Assert.Equal(["new.pdf"], d.Added);
        Assert.Equal(["gone.pdf"], d.Removed);
        Assert.Equal(["grew.pdf"], d.Changed);
        Assert.Contains("- added (1): new.pdf", d.Describe("inbox"));

        var dir = Directory.CreateTempSubdirectory("yawble-watch-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "scan.PDF"), "x");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "y");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "deep.pdf"), "zz");
            Assert.Equal(["scan.PDF", "sub/deep.pdf"], FolderWatcher.Listing(dir, "*.pdf").Keys.Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AFolderTriggerOnTheTeamsDocuments_TellsItsMemberWhatChanged()
    {
        using var db = new TempDb();
        var repos = new TeamRepos(db.Dir, db.ConnectionString);
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { Repos = repos, IdleOffer = false });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        var ada = await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        var inbox = Path.Combine(repos.TeamRoot("Alpha"), TeamDocuments.Folder, "inbox");
        Directory.CreateDirectory(inbox);

        var t = await engine.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "new scans",
            new TriggerSchedule(TriggerKinds.Folder, 30, null, null, null), "Summarise each new scan into the documents.", false, true,
            Watch: new FolderWatch(FolderWatch.Documents, "inbox", "*.pdf", 0)), Ct);
        var now = t.NextDueAt!.Value;
        await engine.SweepTriggersAsync(now, Ct); // remembers the empty folder
        File.WriteAllText(Path.Combine(inbox, "invoice-17.pdf"), "pdf");
        File.WriteAllText(Path.Combine(inbox, "ignore.txt"), "not a pdf");
        await engine.SweepTriggersAsync(now = now.AddSeconds(31), Ct); // sees it, waits for it to settle
        await engine.SweepTriggersAsync(now.AddSeconds(31), Ct); // settled: fires

        using var store = db.OpenStore();
        var row = store.Trigger(t.Id)!;
        Assert.Equal("fired", row.LastOutcome);
        var told = store.Find(row.LastSeq!.Value)!;
        var text = Payload.Field(told, "text")!;
        Assert.Contains("Summarise each new scan", text);
        Assert.Contains("added (1): invoice-17.pdf", text);
        Assert.DoesNotContain("ignore.txt", text);
        Assert.Equal("inbox", row.Watch!.Path);
    }

    private const string MissingFolder = "<a full path that is not there>";

    [Theory]
    [InlineData("documents", "../outside", "inside the team's documents")]
    [InlineData("host", "relative/path", "full path")]
    [InlineData("host", MissingFolder, "is not there")] // a rooted path on the host OS (Z:\... is relative on macOS and Linux)
    [InlineData("elsewhere", "x", "documents or host")]
    public async Task AWatchThatCannotWorkIsRefused(string root, string path, string why)
    {
        if (path == MissingFolder) path = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N"), "yawble");        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions());
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        var ada = await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => engine.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "w",
            new TriggerSchedule(TriggerKinds.Folder, 30, null, null, null), "Look.", true, true, Watch: new FolderWatch(root, path, null, 10)), Ct));
        Assert.Contains(why, e.Message);
    }

    // ---------------------------------------------------------------- the "Fire on" setting

    /// <summary>Settles <paramref name="now"/> after <paramref name="state"/>: one look sees it, the next (quiet 10 s later) fires.</summary>
    private static (FolderChange? Fire, string State) Settle(Dictionary<string, long> now, string state, ref DateTimeOffset at, string fireOn)
    {
        var (fire, s) = FolderWatcher.Look(now, state, at = at.AddSeconds(30), 10, fireOn);
        Assert.Null(fire); // a change waits for its quiet period
        return FolderWatcher.Look(now, s, at = at.AddSeconds(30), 10, fireOn);
    }

    [Fact]
    public void AddedOnly_AnAddFires_ARemoveAloneNeverFires_ASizeChangeFiresNothing()
    {
        var at = T0;
        var (fire, state) = FolderWatcher.Look(new() { ["a.jpg"] = 1, ["b.jpg"] = 2 }, null, at, 10, FolderWatch.Added);
        Assert.Null(fire);

        (fire, state) = Settle(new() { ["a.jpg"] = 1, ["b.jpg"] = 2, ["c.jpg"] = 3 }, state, ref at, FolderWatch.Added);
        Assert.Equal(["c.jpg"], fire!.Added);

        // a remove alone: nothing now, and nothing later either (it is in the baseline, not kept pending)
        (fire, state) = Settle(new() { ["a.jpg"] = 1, ["c.jpg"] = 3 }, state, ref at, FolderWatch.Added);
        Assert.Null(fire);
        for (var i = 0; i < 3; i++)
        {
            (fire, state) = FolderWatcher.Look(new() { ["a.jpg"] = 1, ["c.jpg"] = 3 }, state, at = at.AddSeconds(30), 10, FolderWatch.Added);
            Assert.Null(fire);
        }

        // a size change: nothing
        (fire, state) = Settle(new() { ["a.jpg"] = 100, ["c.jpg"] = 3 }, state, ref at, FolderWatch.Added);
        Assert.Null(fire);
        (fire, _) = FolderWatcher.Look(new() { ["a.jpg"] = 100, ["c.jpg"] = 3 }, state, at.AddSeconds(30), 10, FolderWatch.Added);
        Assert.Null(fire);
    }

    [Fact]
    public void AddedOnly_ARemoveAndAnAddInOneQuietPeriod_FireForTheAddAndListOnlyIt()
    {
        var at = T0;
        var (_, state) = FolderWatcher.Look(new() { ["old.jpg"] = 1 }, null, at, 10, FolderWatch.Added);
        var (fire, _) = Settle(new() { ["new.jpg"] = 2 }, state, ref at, FolderWatch.Added);
        Assert.Equal(["new.jpg"], fire!.Added);
        Assert.Empty(fire.Removed);
        var text = fire.Describe("inbox");
        Assert.Contains("- added (1): new.jpg", text);
        Assert.DoesNotContain("removed", text);
        Assert.DoesNotContain("old.jpg", text);
    }

    [Fact]
    public void AddedOrChanged_FiresOnAnAddAndASizeChange_NotOnARemove()
    {
        var at = T0;
        var (_, state) = FolderWatcher.Look(new() { ["a.pdf"] = 1, ["b.pdf"] = 2 }, null, at, 10, FolderWatch.AddedOrChanged);
        var (fire, s) = Settle(new() { ["a.pdf"] = 1, ["b.pdf"] = 2, ["c.pdf"] = 3 }, state, ref at, FolderWatch.AddedOrChanged);
        Assert.Equal(["c.pdf"], fire!.Added);
        (fire, s) = Settle(new() { ["a.pdf"] = 10, ["b.pdf"] = 2, ["c.pdf"] = 3 }, s, ref at, FolderWatch.AddedOrChanged);
        Assert.Equal(["a.pdf"], fire!.Changed);
        Assert.Empty(fire.Added);
        Assert.Contains("- changed (1): a.pdf", fire.Describe("inbox"));
        (fire, s) = Settle(new() { ["a.pdf"] = 10, ["c.pdf"] = 3 }, s, ref at, FolderWatch.AddedOrChanged);
        Assert.Null(fire);
        // a remove, a change and an add together: the remove is not listed
        (fire, _) = Settle(new() { ["a.pdf"] = 11, ["d.pdf"] = 4 }, s, ref at, FolderWatch.AddedOrChanged);
        Assert.Equal(["d.pdf"], fire!.Added);
        Assert.Equal(["a.pdf"], fire.Changed);
        Assert.Empty(fire.Removed);
        Assert.DoesNotContain("removed", fire.Describe("inbox"));
    }

    [Fact]
    public void AnyChange_IsTodaysBehaviour_AndTheDefault()
    {
        var at = T0;
        var before = new Dictionary<string, long> { ["keep.pdf"] = 1, ["gone.pdf"] = 2, ["grew.pdf"] = 3 };
        var after = new Dictionary<string, long> { ["keep.pdf"] = 1, ["grew.pdf"] = 30, ["new.pdf"] = 4 };
        var (_, state) = FolderWatcher.Look(before, null, at, 10, FolderWatch.AnyChange);
        var (fire, s) = Settle(after, state, ref at, FolderWatch.AnyChange);
        Assert.Equal(["new.pdf"], fire!.Added);
        Assert.Equal(["gone.pdf"], fire.Removed);
        Assert.Equal(["grew.pdf"], fire.Changed);
        var text = fire.Describe("inbox");
        Assert.Contains("- removed (1): gone.pdf", text);
        Assert.Contains("- changed (1): grew.pdf", text);
        (fire, _) = Settle(before, s, ref at, FolderWatch.AnyChange); // a remove alone fires under any change
        Assert.Equal(["new.pdf"], fire!.Removed);

        // no setting: any change
        var (_, d0) = FolderWatcher.Look(before, null, T0, 10);
        var (_, d1) = FolderWatcher.Look(after, d0, T0.AddSeconds(30), 10);
        var (fireDefault, _) = FolderWatcher.Look(after, d1, T0.AddSeconds(60), 10);
        Assert.Equal(["gone.pdf"], fireDefault!.Removed);
        Assert.Equal(FolderWatch.AnyChange, new FolderWatch(FolderWatch.Documents, "", null, 10).FireOn);
    }
    /// <summary>A team with one member and a folder trigger on documents/inbox (quiet 0, looked at every 30 s).</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public BusEngine Engine = null!;
        public MemberRef Ada = null!;
        public string Inbox = "";
        public TriggerRow Trigger = null!;
        public DateTimeOffset Now;

        public static async Task<Rig> StartAsync(string? fireOn)
        {
            var rig = new Rig();
            var repos = new TeamRepos(rig.Db.Dir, rig.Db.ConnectionString);
            rig.Engine = new BusEngine(rig.Db.OpenStore(), new ScriptedRunner(), new BusOptions { Repos = repos, IdleOffer = false });
            await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            rig.Ada = await rig.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            rig.Inbox = Path.Combine(repos.TeamRoot("Alpha"), TeamDocuments.Folder, "inbox");
            Directory.CreateDirectory(rig.Inbox);
            var watch = new FolderWatch(FolderWatch.Documents, "inbox", null, 0);
            if (fireOn is not null) watch = watch with { FireOn = fireOn };
            rig.Trigger = await rig.Engine.SaveTriggerAsync(new TriggerDraft(null, rig.Ada.Id, "new photos",
                new TriggerSchedule(TriggerKinds.Folder, 30, null, null, null), "Archive each new photo.", false, true, Watch: watch), Ct);
            rig.Now = rig.Trigger.NextDueAt!.Value;
            await rig.Engine.SweepTriggersAsync(rig.Now, Ct); // remembers the empty folder
            return rig;
        }

        /// <summary>Two looks (sees it, then it has settled) and the trigger's row after them.</summary>
        public async Task<TriggerRow> SettleAsync()
        {
            await Engine.SweepTriggersAsync(Now = Now.AddSeconds(31), Ct);
            await Engine.SweepTriggersAsync(Now = Now.AddSeconds(31), Ct);
            using var store = Db.OpenStore();
            return store.Trigger(Trigger.Id)!;
        }

        public string Told(TriggerRow row)
        {
            using var store = Db.OpenStore();
            return Payload.Field(store.Find(row.LastSeq!.Value)!, "text")!;
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task SmokeSessions_TheMemberMovesTheFileOutAfterTheFire_AndNoSecondFireHappens()
    {
        await using var rig = await Rig.StartAsync(FolderWatch.Added);
        Assert.Equal(FolderWatch.Added, rig.Trigger.Watch!.FireOn);
        File.WriteAllText(Path.Combine(rig.Inbox, "IMG_0001.HEIC"), "photo");
        var first = await rig.SettleAsync();
        Assert.Equal("fired", first.LastOutcome);
        Assert.Contains("added (1): IMG_0001.HEIC", rig.Told(first));

        // the Manager archives it out of the watched folder
        var archive = Path.Combine(rig.Db.Dir, "archive");
        Directory.CreateDirectory(archive);
        File.Move(Path.Combine(rig.Inbox, "IMG_0001.HEIC"), Path.Combine(archive, "IMG_0001.HEIC"));
        var after = await rig.SettleAsync();
        await rig.Engine.SweepTriggersAsync(rig.Now = rig.Now.AddSeconds(31), Ct);
        await rig.Engine.SweepTriggersAsync(rig.Now = rig.Now.AddSeconds(31), Ct);
        using (var store = rig.Db.OpenStore()) after = store.Trigger(rig.Trigger.Id)!;
        Assert.Equal(first.LastSeq, after.LastSeq); // no second fire
        Assert.Equal(first.LastFiredAt, after.LastFiredAt);

        // the next photo fires again, and lists only itself
        File.WriteAllText(Path.Combine(rig.Inbox, "IMG_0002.HEIC"), "photo");
        var next = await rig.SettleAsync();
        Assert.NotEqual(first.LastSeq, next.LastSeq);
        var text = rig.Told(next);
        Assert.Contains("added (1): IMG_0002.HEIC", text);
        Assert.DoesNotContain("IMG_0001", text);
    }

    [Fact]
    public async Task ASkippedFire_FiresAtTheNextLook_WithTheSameFilteredList()
    {
        await using var rig = await Rig.StartAsync(FolderWatch.Added);
        File.WriteAllText(Path.Combine(rig.Inbox, "old.jpg"), "x");
        await rig.SettleAsync(); // fires for old.jpg
        await rig.Engine.PauseTeamAsync("Alpha", Ct);
        File.Delete(Path.Combine(rig.Inbox, "old.jpg"));
        File.WriteAllText(Path.Combine(rig.Inbox, "new.jpg"), "y");
        var skipped = await rig.SettleAsync();
        Assert.Equal("skipped", skipped.LastOutcome);
        Assert.Contains("paused", skipped.LastReason);

        await rig.Engine.ResumeTeamAsync("Alpha", Ct);
        await rig.Engine.SweepTriggersAsync(rig.Now = rig.Now.AddSeconds(31), Ct);
        using var store = rig.Db.OpenStore();
        var fired = store.Trigger(rig.Trigger.Id)!;
        Assert.Equal("fired", fired.LastOutcome);
        var text = rig.Told(fired);
        Assert.Contains("added (1): new.jpg", text);
        Assert.DoesNotContain("removed", text);
        Assert.DoesNotContain("old.jpg", text);
    }

    [Fact]
    public async Task ANewTriggerWithoutFireOnFiresOnAdded_AnEditWithoutItKeepsItsOwn_AndAnUnknownValueIsRefused()
    {
        await using var rig = await Rig.StartAsync("");
        Assert.Equal(FolderWatch.Added, rig.Trigger.Watch!.FireOn);
        var draft = new TriggerDraft(rig.Trigger.Id, rig.Ada.Id, "new photos", rig.Trigger.Schedule, "Archive each new photo.", false, true,
            Watch: rig.Trigger.Watch with { FireOn = FolderWatch.AddedOrChanged });
        Assert.Equal(FolderWatch.AddedOrChanged, (await rig.Engine.SaveTriggerAsync(draft, Ct)).Watch!.FireOn);
        var kept = await rig.Engine.SaveTriggerAsync(draft with { Watch = draft.Watch! with { FireOn = "" } }, Ct);
        Assert.Equal(FolderWatch.AddedOrChanged, kept.Watch!.FireOn);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => rig.Engine.SaveTriggerAsync(draft with { Watch = draft.Watch! with { FireOn = "removed" } }, Ct));
        Assert.Contains("fire on is one of added, added-or-changed, any", e.Message);
    }

    [Fact]
    public async Task AFolderTriggerSavedBefore0055_ReadsAsAnyChange_AndOtherKindsKeepNone()
    {
        using var db = new TempDb();
        long folderId, cronId;
        await using (var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions()))
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            var ada = await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            folderId = (await engine.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "w", new TriggerSchedule(TriggerKinds.Folder, 30, null, null, null),
                "Look.", true, true, Watch: new FolderWatch(FolderWatch.Documents, "inbox", null, 10, FolderWatch.Added)), Ct)).Id;
            cronId = (await engine.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "c", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null),
                "Tick.", true, true), Ct)).Id;
        }

        // Back to a database from before 0055: no fire_on column, then let the migrator apply 0055.
        const string migration = "0055_trigger_fire_on";
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"ALTER TABLE triggers DROP COLUMN fire_on; DELETE FROM schema_migrations WHERE name = '{migration}';";
            cmd.ExecuteNonQuery();
        }
        Assert.Contains(migration, Yawble.Core.Storage.SchemaMigrator.Migrate(db.ConnectionString));

        using var store = db.OpenStore();
        Assert.Equal(FolderWatch.AnyChange, store.Trigger(folderId)!.Watch!.FireOn);
        Assert.Null(store.Trigger(cronId)!.Watch);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT fire_on FROM triggers WHERE id = {cronId};";
            Assert.IsType<DBNull>(cmd.ExecuteScalar());
            cmd.CommandText = $"UPDATE triggers SET fire_on = 'removed' WHERE id = {folderId};";
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => cmd.ExecuteNonQuery()); // the CHECK
        }
    }
}
