using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;

namespace Yawble.CodeBrowser.Tests;

/// <summary>
/// Hardening against a hostile clone: one memory budget per member, bounded work per tick, coalesced pushes,
/// rescans after lost watcher events, per-owner sessions, no host path to the cloud, handle checks on folders and files,
/// hard links, git named outright. The clone is the agent's: everything in it is treated as hostile.
/// </summary>
public class ReviewHardeningTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private const string Window = "app:me";
    private const string Viewer = "cloud:viewer@example.com";
    private readonly TempDir _tmp = new();
    private readonly string _clone;
    private readonly string _outside;
    private readonly ManualClock _clock = new(T0);
    private CodeBrowserService? _service;
    private FakeHost? _host;

    public ReviewHardeningTests()
    {
        _clone = _tmp.Sub("clone");
        _outside = _tmp.Sub("outside");
        TempDir.Write(_outside, "secret.txt", "outside the clone");
        TempDir.Write(_clone, "src/app.cs", "class A {}\n");
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null) await _service.DisposeAsync();
        _tmp.Dispose();
    }

    private ChangeTracker Tracker(TrackerOptions? options = null) =>
        new(new SafeTree(_clone), (options ?? new TrackerOptions()) with { Watch = false }, _clock);

    private static string Refused(Action a) => Assert.Throws<CodeBrowserException>(a) is { Code: CodeBrowserException.Refused } e ? e.Message : "wrong code";

    private static string Lines(int n, int seed, int width = 60) =>
        string.Concat(Enumerable.Range(0, n).Select(i => $"{seed:D6} line {i:D5} ".PadRight(width, 'x') + "\n"));

    // ------------------------------------------------------------------ 1. one memory budget, a cap on files

    [Fact]
    public void ThousandsOfLargeFilesRewrittenStayWithinTheBudget()
    {
        var options = new TrackerOptions();
        using var t = Tracker(options);
        var max = 0L;
        for (var i = 0; i < 2500; i++)
        {
            var path = $"src/f{i:D4}.txt";
            var before = Lines(300, i);
            t.Saw(path, FileKinds.Lines(before), before.Length, _clock.Now);
            // Rewritten: every tenth line changed, a block removed (removed-text notes), lines added.
            var after = string.Concat(FileKinds.Lines(before).Select((l, n) => n % 10 == 0 ? l + "!\n" : n is > 100 and < 150 ? "" : l + "\n")) + Lines(50, -i);
            t.Saw(path, FileKinds.Lines(after), after.Length, _clock.Now);
            max = Math.Max(max, t.TrackedBytes);
            Assert.True(t.TrackedFiles <= options.MaxTrackedFiles);
        }
        Assert.True(max <= options.SnapshotCapBytes, $"tracking used {max} bytes, over the budget of {options.SnapshotCapBytes}");
        Assert.True(t.SnapshotBytes <= options.SnapshotCapBytes);
        Assert.True(t.TrackedFiles > 10); // the budget is used, not thrown away

        // The newest file keeps its lines; the oldest, forgotten, keeps only "changed at": lines unknown.
        var (newest, _, newestUnknown) = t.Marks("src/f2499.txt", _clock.Now);
        Assert.NotEmpty(newest);
        Assert.False(newestUnknown);
        var (oldest, oldRemoved, oldUnknown) = t.Marks("src/f0000.txt", _clock.Now);
        Assert.Empty(oldest);
        Assert.Empty(oldRemoved);
        Assert.True(oldUnknown);
        Assert.Equal("live", t.Marker("src/f0000.txt", false, _clock.Now));
    }

    [Fact]
    public void RemovedTextCountsAgainstTheBudget()
    {
        // A small budget: one file's removed-text notes alone would pass it, so they are dropped rather than kept outside it.
        var options = new TrackerOptions { SnapshotCapBytes = 64 * 1024 };
        using var t = Tracker(options);
        var text = Lines(200, 1);
        t.Saw("big.txt", FileKinds.Lines(text), text.Length, _clock.Now);
        for (var round = 0; round < 50; round++)
        {
            var lines = FileKinds.Lines(text).ToList();
            lines.RemoveRange(0, Math.Min(3, lines.Count));
            lines.AddRange(FileKinds.Lines(Lines(3, 1000 + round)));
            text = string.Join('\n', lines) + "\n";
            t.Saw("big.txt", FileKinds.Lines(text), text.Length, _clock.Now);
            Assert.True(t.TrackedBytes <= options.SnapshotCapBytes, $"round {round}: {t.TrackedBytes}");
        }
    }

    [Fact]
    public void PastTheFileCapTheLeastRecentlyUsedAreForgotten()
    {
        using var t = Tracker(new TrackerOptions { MaxTrackedFiles = 50 });
        for (var i = 0; i < 200; i++)
        {
            TempDir.Write(_clone, $"n/{i}.txt", "a\n");
            t.Notify($"n/{i}.txt", true, _clock.Now);
        }
        _clock.Advance(TimeSpan.FromSeconds(1));
        while (t.Process(_clock.Now) > 0) { }
        Assert.Equal(50, t.TrackedFiles);
        Assert.True(t.Marks("n/0.txt", _clock.Now).LinesUnknown);
        Assert.Equal("live", t.Marker("n/0.txt", false, _clock.Now));
        Assert.False(t.Marks("n/199.txt", _clock.Now).LinesUnknown);
    }

    // ------------------------------------------------------------------ 2. bounded work, single flight, lost events

    [Fact]
    public void ATickProcessesAtMostItsShareAndCarriesTheRestOver()
    {
        using var t = Tracker(new TrackerOptions { MaxPerTick = 10 });
        for (var i = 0; i < 35; i++)
        {
            TempDir.Write(_clone, $"many/{i}.txt", "x\n");
            t.Notify($"many/{i}.txt", true, _clock.Now);
        }
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(10, t.Process(_clock.Now));
        Assert.Equal(25, t.PendingCount);
        Assert.Equal(10, t.Process(_clock.Now));
        Assert.Equal(10, t.Process(_clock.Now));
        Assert.Equal(5, t.Process(_clock.Now));
        Assert.Equal(0, t.Process(_clock.Now));
    }

    [Fact]
    public void ProcessingIsSingleFlight()
    {
        using var t = Tracker();
        var nested = new List<int>();
        t.Changed += _ => nested.Add(t.Process(_clock.Now)); // a timer tick arriving while this one runs
        for (var i = 0; i < 3; i++)
        {
            TempDir.Write(_clone, $"s/{i}.txt", "x\n");
            t.Notify($"s/{i}.txt", true, _clock.Now);
        }
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, t.Process(_clock.Now));
        Assert.Equal([0, 0, 0], nested);
    }

    [Fact]
    public void AFullQueueMarksTheCloneForARescan()
    {
        using var t = Tracker(new TrackerOptions { MaxPending = 5 });
        for (var i = 0; i < 10; i++) t.Notify($"q/{i}.txt", true, _clock.Now);
        Assert.Equal(5, t.PendingCount);
        Assert.True(t.NeedsRescan);
    }

    [Fact]
    public void AfterLostEventsTheCloneIsRescannedByTimeAndSize()
    {
        using var t = Tracker();
        var seen = new List<ChangeEntry>();
        t.Changed += seen.Add;
        // Baselines the tracker knows, written an hour ago.
        var old = (T0 - TimeSpan.FromHours(1)).UtcDateTime;
        foreach (var (path, text) in new[] { ("a.txt", "1\n2\n"), ("same.txt", "s\n"), ("sized.txt", "abc\n"), ("gone.txt", "g\n") })
        {
            File.SetLastWriteTimeUtc(TempDir.Write(_clone, path, text), old);
            t.Saw(path, FileKinds.Lines(text), text.Length, _clock.Now);
        }
        File.SetLastWriteTimeUtc(Path.Combine(_clone, "src", "app.cs"), old);

        // The watcher overflows; meanwhile: a.txt changed, new.txt created, sized.txt changed with its old time kept, gone.txt deleted.
        _clock.Advance(TimeSpan.FromMinutes(1));
        t.Overflowed(_clock.Now);
        Assert.True(t.NeedsRescan);
        File.SetLastWriteTimeUtc(TempDir.Write(_clone, "a.txt", "1\n2\n3\n"), _clock.Now.UtcDateTime);
        File.SetLastWriteTimeUtc(TempDir.Write(_clone, "new.txt", "n\n"), _clock.Now.UtcDateTime);
        File.SetLastWriteTimeUtc(TempDir.Write(_clone, "sized.txt", "abcdef\n"), old);
        File.Delete(Path.Combine(_clone, "gone.txt"));

        t.Process(_clock.Now); // the rescan queues what changed (the debounce still applies)
        Assert.False(t.NeedsRescan);
        _clock.Advance(TimeSpan.FromSeconds(1));
        t.Process(_clock.Now);

        Assert.Equal(["a.txt", "gone.txt", "new.txt", "sized.txt"], seen.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal([new ChangeRange(3, 3, "added")], seen.Single(e => e.Path == "a.txt").Ranges);
        Assert.True(seen.Single(e => e.Path == "gone.txt").Deleted);
        Assert.Equal("live", t.Marker("sized.txt", false, _clock.Now));
        Assert.Null(t.Marker("same.txt", false, _clock.Now));
        Assert.Null(t.Marker("src/app.cs", false, _clock.Now));
    }

    // ------------------------------------------------------------------ 2, 7, 8. the service: pushes, sessions, root

    private CodeBrowserService Service(CodeBrowserOptions? options = null)
    {
        _host = new FakeHost(_clone);
        _service = new CodeBrowserService(_host, (options ?? new CodeBrowserOptions()) with { Tracker = new TrackerOptions { Watch = false } }, _clock);
        return _service;
    }

    private async Task<string> OpenAsync(string owner) =>
        (await _service!.DispatchAsync("code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }, owner, Ct))!["session"]!.GetValue<string>();

    private void Change(params string[] paths)
    {
        var tracker = _service!.TrackerOf(FakeHost.Key)!;
        foreach (var p in paths)
        {
            TempDir.Write(_clone, p, p + _clock.Now.Ticks + "\n");
            tracker.Notify(p, true, _clock.Now);
        }
        _clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Process(_clock.Now);
    }

    private List<JsonObject> Pushed() { lock (_host!.Events) return _host.Events.Where(e => e.Name == CodeBrowserService.ChangedEvent).Select(e => e.Data.AsObject()).ToList(); }

    private static List<string> PathsOf(JsonObject e) => e["changes"]!.AsArray().Select(c => c!["path"]!.GetValue<string>()).ToList();

    [Fact]
    public async Task ChangesAreCoalescedPerSession_OnePathOncePerEvent()
    {
        Service();
        var s = await OpenAsync(Window);
        Change("a.txt", "b.txt", "c.txt"); // one tick: the first goes now, the rest wait out the 250 ms
        var first = Assert.Single(Pushed());
        Assert.Equal(s, first["session"]!.GetValue<string>());
        Assert.Equal(["a.txt"], PathsOf(first));
        Assert.Equal("a.txt", first["path"]!.GetValue<string>());

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        _service!.FlushChanges(_clock.Now);
        Assert.Single(Pushed()); // not yet

        _clock.Advance(TimeSpan.FromMilliseconds(200));
        _service.FlushChanges(_clock.Now);
        var second = Pushed()[1];
        Assert.Equal(["b.txt", "c.txt"], PathsOf(second).Order(StringComparer.Ordinal));

        // The same path changed again and again within the window: it comes once, with its latest change.
        var tracker = _service.TrackerOf(FakeHost.Key)!;
        for (var i = 0; i < 5; i++)
        {
            TempDir.Write(_clone, "a.txt", string.Concat(Enumerable.Repeat("l\n", i + 2)));
            tracker.Notify("a.txt", false, _clock.Now);
            _clock.Advance(TimeSpan.FromMilliseconds(40));
            tracker.Process(_clock.Now + TimeSpan.FromSeconds(1));
        }
        _clock.Advance(TimeSpan.FromSeconds(1));
        _service.FlushChanges(_clock.Now);
        var after = Pushed().Skip(2).ToList();
        Assert.True(after.Count <= 2, $"{after.Count} events for one path within 250 ms windows");
        Assert.All(after, e => Assert.Equal(PathsOf(e).Count, PathsOf(e).Distinct().Count()));
    }

    [Fact]
    public async Task ChangesGoOnlyToOpenSessions_MarkedForTheCloudOnlyWhenAViewerOpenedThem()
    {
        Service();
        var w = await OpenAsync(Window);
        var c = await OpenAsync(Viewer);
        Change("a.txt");
        var events = Pushed();
        Assert.Equal(2, events.Count);
        Assert.False(events.Single(e => e["session"]!.GetValue<string>() == w)["cloud"]!.GetValue<bool>());
        Assert.True(events.Single(e => e["session"]!.GetValue<string>() == c)["cloud"]!.GetValue<bool>());

        await _service!.DispatchAsync("code.close", new JsonObject { ["session"] = w }, Window, Ct);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Change("b.txt");
        var later = Pushed().Skip(2).ToList();
        Assert.Equal(c, Assert.Single(later)["session"]!.GetValue<string>());

        await _service.DispatchAsync("code.close", new JsonObject { ["session"] = c }, Viewer, Ct);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Change("c.txt");
        Assert.Equal(3, Pushed().Count); // nobody has it open: nothing is pushed
    }

    [Fact]
    public async Task ACloudViewerCannotEvictTheWindowsSession()
    {
        Service(new CodeBrowserOptions { MaxSessions = 3 });
        var w = await OpenAsync(Window);
        var viewer = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            viewer.Add(await OpenAsync(Viewer));
        }
        Assert.NotNull(await _service!.DispatchAsync("code.list", new JsonObject { ["session"] = w, ["path"] = "" }, Window, Ct));
        // The viewer's own oldest went; its newest three stand.
        await Assert.ThrowsAsync<CodeBrowserException>(() => _service.DispatchAsync("code.list", new JsonObject { ["session"] = viewer[0], ["path"] = "" }, Viewer, Ct));
        foreach (var s in viewer.TakeLast(3))
            Assert.NotNull(await _service.DispatchAsync("code.list", new JsonObject { ["session"] = s, ["path"] = "" }, Viewer, Ct));
        // Another viewer cannot evict this one's either.
        for (var i = 0; i < 5; i++) await OpenAsync("cloud:other@example.com");
        Assert.NotNull(await _service.DispatchAsync("code.list", new JsonObject { ["session"] = viewer[^1], ["path"] = "" }, Viewer, Ct));
    }

    [Fact]
    public async Task ACloudViewerIsNotToldTheHostPath()
    {
        Service();
        var window = (await _service!.DispatchAsync("code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }, Window, Ct))!.AsObject();
        Assert.Equal(Path.GetFullPath(_clone), window["root"]!.GetValue<string>());
        var cloud = (await _service.DispatchAsync("code.open", new JsonObject { ["team"] = "web", ["member"] = "Ada" }, Viewer, Ct))!.AsObject();
        var root = cloud["root"]!.GetValue<string>();
        Assert.Equal("Ada", root);
        Assert.DoesNotContain(_tmp.Path, cloud.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tmp.Path.Replace("\\", "\\\\"), cloud.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ 4. a folder swapped while it is listed

    private static void WindowsOnly()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("the handle checks are Windows'");
    }

    [Fact]
    public void AFolderWhoseHandleLeadsOutsideAfterListingIsRefused()
    {
        WindowsOnly();
        var tree = new SafeTree(_clone);
        var real = tree.HandlePath;
        var swapped = false;
        tree.AfterEnumerate = _ => swapped = true;
        tree.HandlePath = h => swapped ? Path.Combine(_outside) : real(h);
        Refused(() => tree.List("src"));
        swapped = false;
        tree.AfterEnumerate = null;
        Assert.Single(tree.List("src")); // nothing swapped: listed as before
    }

    [Fact]
    public void AFolderSwappedForALinkWhileListedIsRefused()
    {
        WindowsOnly();
        var tree = new SafeTree(_clone);
        var src = Path.Combine(_clone, "src");
        var made = false;
        tree.AfterEnumerate = _ =>
        {
            Directory.Move(src, src + "-old");
            made = Links.TrySymlinkDir(src, _outside);
        };
        var e = Assert.Throws<CodeBrowserException>(() => tree.List("src"));
        if (!made) return; // no symlink privilege here: the folder was moved away, still refused (it is not the same folder)
        Assert.Equal(CodeBrowserException.Refused, e.Code);
    }

    [Fact]
    public void AFolderReplacedByAnotherWhileListedIsRefused()
    {
        WindowsOnly();
        var tree = new SafeTree(_clone);
        var src = Path.Combine(_clone, "src");
        tree.AfterEnumerate = _ =>
        {
            Directory.Move(src, src + "-old");
            Directory.CreateDirectory(src);
        };
        Assert.Contains("changed while it was listed", Refused(() => tree.List("src")));
    }

    // ------------------------------------------------------------------ 5. the file opened: .git, the deny list, hard links

    [Fact]
    public void TheOpenedFilesFinalPathIsCheckedAgainstGitAndTheDenyList()
    {
        WindowsOnly();
        var tree = new SafeTree(_clone, p => p.EndsWith("blocked.cs", StringComparison.OrdinalIgnoreCase));
        var real = tree.HandlePath;
        string? swap = null;
        tree.HandlePath = h => real(h) is { } f && swap is not null ? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(f)!)!, swap) : real(h);

        swap = Path.Combine(".git", "config");
        Assert.Contains(".git", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
        swap = Path.Combine("src", ".env");
        Assert.Contains("secrets", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
        swap = Path.Combine("keys", "server.pem");
        Assert.Contains("secrets", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
        swap = Path.Combine("src", "blocked.cs");
        Assert.Contains("secrets", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
        swap = null;
        tree.OpenFile("src/app.cs").Stream.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [Fact]
    public async Task AHardLinkIsNotReadOrDownloaded()
    {
        WindowsOnly();
        var link = Path.Combine(_clone, "src", "linked.txt");
        if (!CreateHardLinkW(link, Path.Combine(_outside, "secret.txt"), IntPtr.Zero)) Assert.Skip("hard links cannot be made here");
        var tree = new SafeTree(_clone);
        Assert.Contains("hard link", Refused(() => tree.OpenFile("src/linked.txt").Stream.Dispose()));
        tree.OpenFile("src/app.cs").Stream.Dispose(); // one name: fine

        Service();
        var s = await OpenAsync(Window);
        var read = await Assert.ThrowsAsync<CodeBrowserException>(() => _service!.DispatchAsync("code.read", new JsonObject { ["session"] = s, ["path"] = "src/linked.txt" }, Window, Ct));
        Assert.Equal(CodeBrowserException.Refused, read.Code);
        var download = await Assert.ThrowsAsync<CodeBrowserException>(() => _service!.DispatchAsync("code.download", new JsonObject { ["session"] = s, ["path"] = "src/linked.txt" }, Window, Ct));
        Assert.Equal(CodeBrowserException.Refused, download.Code);
    }

    [Fact]
    public void AFileWithMoreThanOneNameIsRefused_ThroughTheSeam()
    {
        WindowsOnly();
        var tree = new SafeTree(_clone) { LinkCount = _ => 2 };
        Assert.Contains("hard link", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
        tree.LinkCount = _ => null; // not known: refused too
        Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose());
    }

    // ------------------------------------------------------------------ 6. git: the repository named outright

    private sealed class CannedGit : IGitReader
    {
        public const string Sha = "1111111111111111111111111111111111111111";
        public List<(string Repository, IReadOnlyList<string> Args)> Calls { get; } = [];

        public Task<GitReadResult> RunAsync(string repository, IReadOnlyList<string> args, CancellationToken ct)
        {
            lock (Calls) Calls.Add((repository, args));
            var command = args.SkipWhile(a => a.StartsWith("-", StringComparison.Ordinal) || a.Contains('=')).FirstOrDefault();
            return Task.FromResult(command switch
            {
                "rev-parse" or "merge-base" => new GitReadResult(0, Sha),
                "ls-tree" => new GitReadResult(0, "100644 blob 2222222222222222222222222222222222222222\tsrc/app.cs\0"),
                _ => new GitReadResult(1, ""),
            });
        }
    }

    [Fact]
    public async Task EveryGitCommandNamesTheRepositoryAndRefusesReplaceObjects()
    {
        var team = _tmp.Sub("team");
        Directory.CreateDirectory(Path.Combine(team, ".git"));
        var shared = _tmp.Sub("shared.git");
        File.WriteAllText(Path.Combine(shared, "HEAD"), "ref: refs/heads/main\n");
        Directory.CreateDirectory(Path.Combine(shared, "objects"));
        var notARepo = _tmp.Sub("plain");

        foreach (var (repo, gitDir) in new[] { (team, Path.Combine(team, ".git")), (shared, shared) })
        {
            var git = new CannedGit();
            var view = new BranchView(git, new SafeTree(_clone), new BranchSource([notARepo, repo], "main", "members/ada"));
            await view.RefreshAsync(Ct);
            Assert.Null(view.Problem);
            Assert.NotEmpty(git.Calls);
            foreach (var (cwd, args) in git.Calls)
            {
                Assert.Equal(repo, cwd);
                Assert.Equal("--git-dir=" + gitDir, args[0]);
                Assert.Equal(BranchView.Hardening, args.Skip(1).Take(BranchView.Hardening.Count));
                Assert.Contains("--no-replace-objects", args);
                Assert.Contains("core.fsmonitor=false", args);
                Assert.Contains(args, a => a.StartsWith("core.hooksPath=", StringComparison.Ordinal));
                Assert.DoesNotContain(args, a => a.Contains(_clone, StringComparison.OrdinalIgnoreCase));
            }
        }

        // A folder that is not a repository is never handed to git (git would search upward from it).
        var none = new CannedGit();
        var nothing = new BranchView(none, new SafeTree(_clone), new BranchSource([notARepo], "main", null));
        await nothing.RefreshAsync(Ct);
        Assert.Empty(none.Calls);
        Assert.NotNull(nothing.Problem);
    }

    [Fact]
    public void AGitFileNamesTheRealGitFolder()
    {
        var real = _tmp.Sub("elsewhere/worktree-git");
        var wt = _tmp.Sub("wt");
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + real + "\n");
        Assert.Equal(Path.GetFullPath(real), BranchView.GitDirOf(wt));
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: missing\n");
        Assert.Null(BranchView.GitDirOf(wt));
    }
}
