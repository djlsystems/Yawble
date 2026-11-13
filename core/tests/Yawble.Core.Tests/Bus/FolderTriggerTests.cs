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

    [Theory]
    [InlineData("documents", "../outside", "inside the team's documents")]
    [InlineData("host", "relative/path", "full path")]
    [InlineData("host", @"Z:\definitely\not\here\yawble", "is not there")]
    [InlineData("elsewhere", "x", "documents or host")]
    public async Task AWatchThatCannotWorkIsRefused(string root, string path, string why)
    {
        using var db = new TempDb();
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions());
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        var ada = await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => engine.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "w",
            new TriggerSchedule(TriggerKinds.Folder, 30, null, null, null), "Look.", true, true, Watch: new FolderWatch(root, path, null, 10)), Ct));
        Assert.Contains(why, e.Message);
    }
}
