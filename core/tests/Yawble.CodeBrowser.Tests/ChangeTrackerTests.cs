using Xunit;

namespace Yawble.CodeBrowser.Tests;

/// <summary>Change detection from file writes: ranges, debounce, the bounded log, the snapshot cap, "most recently edited".</summary>
public class ChangeTrackerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private readonly TempDir _tmp = new();
    private readonly string _clone;
    private readonly ManualClock _clock = new(T0);

    public ChangeTrackerTests() => _clone = _tmp.Sub("clone");

    public void Dispose() => _tmp.Dispose();

    private ChangeTracker Tracker(TrackerOptions? options = null) =>
        new(new SafeTree(_clone), (options ?? new TrackerOptions()) with { Watch = false }, _clock);

    /// <summary>Writes a file, tells the tracker, and lets the debounce pass.</summary>
    private void Write(ChangeTracker t, string path, string text, bool created = false)
    {
        TempDir.Write(_clone, path, text);
        t.Notify(path, created, _clock.Now);
        _clock.Advance(TimeSpan.FromSeconds(1));
        t.Process(_clock.Now);
    }

    private void Baseline(ChangeTracker t, string path, string text)
    {
        TempDir.Write(_clone, path, text);
        t.Saw(path, FileKinds.Lines(text), text.Length, _clock.Now);
    }

    [Fact]
    public void AddedChangedAndRemovedRanges()
    {
        using var t = Tracker();
        var seen = new List<ChangeEntry>();
        t.Changed += seen.Add;
        Baseline(t, "a.txt", "1\n2\n3\n4\n5\n");

        Write(t, "a.txt", "1\n2\nnew\n3\n4\n5\n");
        Assert.Equal([new ChangeRange(3, 3, "added")], seen[^1].Ranges);

        Write(t, "a.txt", "1\nTWO\nnew\n3\n4\n5\n");
        Assert.Equal([new ChangeRange(2, 2, "changed")], seen[^1].Ranges);

        Write(t, "a.txt", "1\nTWO\nnew\n5\n");
        Assert.Equal([new ChangeRange(3, 3, "removed")], seen[^1].Ranges);

        var (lines, removed, unknown) = t.Marks("a.txt", _clock.Now);
        Assert.False(unknown);
        Assert.Equal([(2, 2, "changed"), (3, 3, "added")], lines.Select(l => (l.From, l.To, l.Kind)));
        var r = Assert.Single(removed);
        Assert.Equal(3, r.AfterLine);
        Assert.Equal("3\n4", r.Text);
    }

    [Fact]
    public void LongRemovedTextIsCutAtTheCap_EndingInARealEllipsis()
    {
        using var t = Tracker();
        var longLine = new string('x', 4010);
        Baseline(t, "a.txt", "keep\n" + longLine + "\nend\n");
        Write(t, "a.txt", "keep\nend\n");
        var (_, removed, _) = t.Marks("a.txt", _clock.Now);
        var r = Assert.Single(removed);
        Assert.Equal(4001, r.Text.Length);
        Assert.Equal(new string('x', 4000) + "\u2026", r.Text);
        Assert.EndsWith("\u2026", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MarksMoveWithTheLinesAroundThem()
    {
        using var t = Tracker();
        Baseline(t, "a.txt", "a\nb\nc\nd\n");
        Write(t, "a.txt", "a\nb\nc\nd\nadded at end\n"); // line 5 added
        Write(t, "a.txt", "a\nc\nd\nadded at end\n");   // line 2 removed: the added line is line 4 now
        var (lines, removed, _) = t.Marks("a.txt", _clock.Now);
        Assert.Equal((4, 4, "added"), lines.Select(l => (l.From, l.To, l.Kind)).Single());
        Assert.Equal(1, Assert.Single(removed).AfterLine);
        Write(t, "a.txt", "top\na\nc\nd\nadded at end\n");
        (lines, removed, _) = t.Marks("a.txt", _clock.Now);
        Assert.Equal([(1, 1, "added"), (5, 5, "added")], lines.Select(l => (l.From, l.To, l.Kind)));
        Assert.Equal(2, Assert.Single(removed).AfterLine);
    }

    [Fact]
    public void ANewFileIsAllAdded()
    {
        using var t = Tracker();
        ChangeEntry? last = null;
        t.Changed += e => last = e;
        Write(t, "src/new.cs", "x\ny\n", created: true);
        Assert.Equal([new ChangeRange(1, 2, "added")], last!.Ranges);
        Assert.False(last.LinesUnknown);
    }

    [Fact]
    public void AFileFirstSeenAlreadyChangedHasLinesUnknown()
    {
        using var t = Tracker();
        ChangeEntry? last = null;
        t.Changed += e => last = e;
        Write(t, "old.cs", "x\n");
        Assert.True(last!.LinesUnknown);
        Assert.Equal("recent", t.Marker("old.cs", false, _clock.Now + TimeSpan.FromMinutes(2)));
        Write(t, "old.cs", "x\ny\n"); // now there is a baseline
        Assert.Equal([new ChangeRange(2, 2, "added")], last.Ranges);
    }

    [Fact]
    public void WritesAreDebounced()
    {
        using var t = Tracker();
        var count = 0;
        t.Changed += _ => count++;
        Baseline(t, "a.txt", "1\n");
        TempDir.Write(_clone, "a.txt", "1\n2\n");
        t.Notify("a.txt", false, _clock.Now);
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        t.Notify("a.txt", false, _clock.Now);
        Assert.Equal(0, t.Process(_clock.Now)); // still being written
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        TempDir.Write(_clone, "a.txt", "1\n2\n3\n");
        t.Notify("a.txt", false, _clock.Now);
        _clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(1, t.Process(_clock.Now));
        Assert.Equal(1, count); // three events, one change
        Assert.Equal(0, t.Process(_clock.Now + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AFileWrittenWithoutPauseIsStillTaken()
    {
        using var t = Tracker();
        Baseline(t, "a.txt", "1\n");
        for (var i = 0; i < 12; i++)
        {
            TempDir.Write(_clone, "a.txt", string.Concat(Enumerable.Range(0, i + 2).Select(n => n + "\n")));
            t.Notify("a.txt", false, _clock.Now);
            _clock.Advance(TimeSpan.FromMilliseconds(200));
            if (t.Process(_clock.Now) == 1) return;
        }
        Assert.Fail("a file written every 200 ms was never processed");
    }

    [Fact]
    public void TheLogKeeps200ChangesOr30Minutes()
    {
        using var t = Tracker();
        Baseline(t, "a.txt", "0\n");
        for (var i = 1; i <= 230; i++) Write(t, "a.txt", $"{i}\n");
        var log = t.Log(_clock.Now);
        Assert.Equal(200, log.Count);
        Assert.All(log, e => Assert.Equal("a.txt", e.Path));

        _clock.Advance(TimeSpan.FromMinutes(25));
        Write(t, "b.txt", "b\n", created: true);
        _clock.Advance(TimeSpan.FromMinutes(6)); // the a.txt changes are now over 30 minutes old
        var after = t.Log(_clock.Now);
        Assert.Equal("b.txt", Assert.Single(after).Path);
        Assert.Null(t.Marker("a.txt", false, _clock.Now));
        Assert.Equal("recent", t.Marker("b.txt", false, _clock.Now));
        Assert.Empty(t.Marks("a.txt", _clock.Now).Lines);
    }

    [Fact]
    public void PastTheSnapshotCapTheLinesAreUnknown()
    {
        using var t = Tracker(new TrackerOptions { SnapshotCapBytes = 100 });
        var big = string.Concat(Enumerable.Range(0, 12).Select(i => $"line{i:00}\n")); // 84 bytes
        Baseline(t, "one.txt", big);
        Baseline(t, "two.txt", big); // over the cap: one.txt's baseline goes
        Assert.True(t.SnapshotBytes <= 100);
        ChangeEntry? last = null;
        t.Changed += e => last = e;
        Write(t, "one.txt", big + "more\n");
        Assert.True(last!.LinesUnknown);
        Assert.Empty(last.Ranges);
        Assert.Equal("live", t.Marker("one.txt", false, _clock.Now)); // the file marker still shows

        // A single file over the cap is never kept.
        using var small = Tracker(new TrackerOptions { SnapshotCapBytes = 10 });
        TempDir.Write(_clone, "three.txt", big);
        small.Saw("three.txt", FileKinds.Lines(big), big.Length, _clock.Now);
        Assert.Equal(0, small.SnapshotBytes);
    }

    [Fact]
    public void MarkersAreLiveThenRecentThenGone()
    {
        using var t = Tracker();
        Write(t, "src/deep/a.cs", "x\n", created: true);
        Assert.Equal("live", t.Marker("src/deep/a.cs", false, _clock.Now));
        Assert.Equal("live", t.Marker("src", true, _clock.Now));
        Assert.Equal("live", t.Marker("", true, _clock.Now));
        Assert.Null(t.Marker("other", true, _clock.Now));
        _clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal("recent", t.Marker("src", true, _clock.Now));
        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(t.Marker("src", true, _clock.Now));

        // During the member's current run, an older change still counts as recent.
        t.RunStartedAt = () => T0 - TimeSpan.FromHours(1);
        Assert.Equal("recent", t.Marker("src/deep/a.cs", false, _clock.Now));
    }

    [Fact]
    public void NoiseFoldersAreNotTracked()
    {
        using var t = Tracker();
        var count = 0;
        t.Changed += _ => count++;
        foreach (var p in new[] { "node_modules/x/index.js", "bin/Debug/a.dll", "obj/a.cs", "dist/app.js", ".git/index", "src/bin/x.txt" })
            Write(t, p, "x", created: true);
        Assert.Equal(0, count);
        Assert.Null(t.Marker("node_modules", true, _clock.Now));
    }

    [Fact]
    public void AHiddenFileIsMarkedButNeverRead()
    {
        using var t = Tracker();
        ChangeEntry? last = null;
        t.Changed += e => last = e;
        Write(t, ".env", "TOKEN=1\n", created: true);
        Assert.True(last!.LinesUnknown);
        Assert.Empty(last.Ranges);
        Assert.Equal(0, t.SnapshotBytes);
    }

    [Fact]
    public void ADeletedFileIsLoggedAndDropsItsMarker()
    {
        using var t = Tracker();
        Write(t, "gone.txt", "x\n", created: true);
        File.Delete(Path.Combine(_clone, "gone.txt"));
        t.Notify("gone.txt", false, _clock.Now);
        _clock.Advance(TimeSpan.FromSeconds(1));
        t.Process(_clock.Now);
        Assert.True(t.Log(_clock.Now)[^1].Deleted);
        Assert.Null(t.Marker("gone.txt", false, _clock.Now));
    }

    [Fact]
    public void MostRecentlyEditedComesFromTheLog()
    {
        using var t = Tracker();
        Baseline(t, "a.txt", "1\n2\n3\n");
        Write(t, "b.txt", "b\n", created: true);
        Write(t, "a.txt", "1\n2\nthree\n");
        Assert.Equal(("a.txt", 3), t.Latest(_clock.Now));
        File.Delete(Path.Combine(_clone, "a.txt")); // deleted since: the one before stands
        Assert.Equal(("b.txt", 1), t.Latest(_clock.Now));
    }

    [Fact]
    public void BeforeAnyChangeTheNewestFileStandsIn()
    {
        var old = TempDir.Write(_clone, "old.txt", "o");
        var newest = TempDir.Write(_clone, "src/newest.cs", "n");
        var secret = TempDir.Write(_clone, ".env", "s");
        var noise = TempDir.Write(_clone, "node_modules/x.js", "s");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(secret, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(noise, DateTime.UtcNow);
        using var t = Tracker();
        Assert.Equal(("src/newest.cs", 1), t.Latest(_clock.Now));
    }

    [Fact]
    public void AnEmptyCloneHasNoLatest()
    {
        using var t = Tracker();
        Assert.Null(t.Latest(_clock.Now));
    }

    [Fact]
    public async Task TheWatcherSeesRealWrites()
    {
        // The real FileSystemWatcher and debounce timer, on the system clock.
        TempDir.Write(_clone, "src/live.cs", "a\nb\n");
        using var t = new ChangeTracker(new SafeTree(_clone), new TrackerOptions { Debounce = TimeSpan.FromMilliseconds(100) });
        var got = new TaskCompletionSource<ChangeEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Changed += e => { if (e.Path == "src/live.cs" && e.Ranges.Count > 0) got.TrySetResult(e); };
        t.Start(seed: false);
        t.Saw("src/live.cs", ["a", "b"], 4, DateTimeOffset.UtcNow);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        TempDir.Write(_clone, "src/live.cs", "a\nb\nc\n");
        var entry = await got.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal([new ChangeRange(3, 3, "added")], entry.Ranges);
    }

    [Fact]
    public async Task StartingKeepsRecentFilesAsBaselines()
    {
        TempDir.Write(_clone, "recent.cs", "a\n");
        var old = TempDir.Write(_clone, "old.cs", "a\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));
        // Seed is what Start runs: files changed in the last 30 minutes (on the system clock) become baselines.
        using var seeded = new ChangeTracker(new SafeTree(_clone), new TrackerOptions { Watch = false }, TimeProvider.System);
        seeded.Seed();
        Assert.Equal(2, seeded.SnapshotBytes); // recent.cs only
        TempDir.Write(_clone, "recent.cs", "a\nb\n");
        ChangeEntry? last = null;
        seeded.Changed += e => last = e;
        seeded.Notify("recent.cs", false, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5));
        seeded.Process(DateTimeOffset.UtcNow);
        Assert.Equal([new ChangeRange(2, 2, "added")], last!.Ranges);
        await Task.CompletedTask;
    }
}
