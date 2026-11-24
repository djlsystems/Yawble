using System.Text;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.AgentCommands;

/// <summary>macOS's per-run short temp folder: its name, the sun_path length rule, making, removing and the sweep.</summary>
public class RunTempFolderTests
{
    [Fact]
    public void TheNameIsShortFixedAndDifferentPerRun()
    {
        var a = RunTempFolder.Name("r-0123456789abcdef01234567");
        Assert.Equal(a, RunTempFolder.Name("r-0123456789abcdef01234567"));
        Assert.NotEqual(a, RunTempFolder.Name("r-0123456789abcdef01234568"));
        Assert.Matches("^yr-[0-9a-f]{8}$", a);
        Assert.True(RunTempFolder.IsRunFolderName(a));
        foreach (var other in new[] { "yr-", "yr-0123456", "yr-0123456789", "yr-ABCDEF01", "xr-01234567", "T", "yr-0123abcz" })
            Assert.False(RunTempFolder.IsRunFolderName(other), other);
    }

    [Fact]
    public void TheFolderIsUnderTheUserTempFolderAndFitsAPipe()
    {
        var folder = RunTempFolder.PathFor(PolicyFixtures.MacUserTemp, "run-0001");
        Assert.Equal(PolicyFixtures.MacUserTemp + RunTempFolder.Name("run-0001"), folder);
        Assert.True(RunTempFolder.Fits(folder));
        // The rule: the folder + "/CoreFxPipe_" + the pipe-name budget is at most 104 bytes (macOS sun_path).
        Assert.True(Encoding.UTF8.GetByteCount(folder + RunTempFolder.PipePrefix) + RunTempFolder.PipeNameBudget <= 104);
        // One byte longer than the room there is fails.
        var room = 104 - RunTempFolder.PipeNameBudget - RunTempFolder.PipePrefix.Length;
        Assert.True(RunTempFolder.Fits("/" + new string('a', room - 1)));
        Assert.False(RunTempFolder.Fits("/" + new string('a', room)));
        // The member's tmp folder (what TMPDIR was) never fits.
        Assert.False(RunTempFolder.Fits("/Users/alice/Library/Application Support/Yawble/teams/alpha/members/dev1/tmp"));
    }

    [Fact]
    public void CreateMakesAnEmptyOwnerOnlyFolderAndRemoveDeletesIt()
    {
        using var dir = new ScratchDir();
        var folder = Path.Combine(dir.Path, RunTempFolder.Name("run-0001"));
        RunTempFolder.Create(folder);
        Assert.True(Directory.Exists(folder));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        // A leftover of the same run id (a crash before the sweep) is emptied, never reused as it was.
        Directory.CreateDirectory(Path.Combine(folder, "zsh"));
        File.WriteAllText(Path.Combine(folder, "zsh", "here-doc"), "x");
        RunTempFolder.Create(folder);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));

        File.WriteAllText(Path.Combine(folder, "CoreFxPipe_x"), "");
        Assert.True(RunTempFolder.Remove(folder));
        Assert.False(Directory.Exists(folder));
        Assert.True(RunTempFolder.Remove(folder)); // already gone: nothing to do
    }

    // Two app instances of one Mac user share DARWIN_USER_TEMP_DIR; each core's folders carry its owner marker.
    private static readonly RunTempOwner CoreA = new(RunTempOwner.HashDataDir("/Users/alice/Library/Application Support/Yawble"), 4101, 638_000_000_000_000_000);
    private static readonly RunTempOwner CoreB = new(RunTempOwner.HashDataDir("/Users/alice/src/yawble/dev-data"), 4202, 638_000_000_100_000_000);

    [Fact]
    public void TheSweepRemovesOnlyRunFoldersWhoseRunIsGone()
    {
        using var dir = new ScratchDir();
        var gone = RunTempFolder.Name("gone-run");
        var live = RunTempFolder.Name("live-run");
        RunTempFolder.Create(Path.Combine(dir.Path, gone), CoreA);
        RunTempFolder.Create(Path.Combine(dir.Path, live), CoreA);
        foreach (var name in new[] { "yr-notours", "com.apple.something", "yr-0123456789" })
            Directory.CreateDirectory(Path.Combine(dir.Path, name));
        File.WriteAllText(Path.Combine(dir.Path, gone, "left.txt"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "yr-abcdef01"), "a file, not a run folder");

        var removed = RunTempFolder.Sweep(dir.Path, name => name == live, CoreA, _ => true);

        Assert.Equal([gone], removed);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, gone)));
        Assert.False(File.Exists(Path.Combine(dir.Path, gone + RunTempFolder.MarkerSuffix)));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, live)));
        Assert.True(File.Exists(Path.Combine(dir.Path, live + RunTempFolder.MarkerSuffix)));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "yr-notours")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "com.apple.something")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "yr-0123456789")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "yr-abcdef01")));
    }

    [Fact]
    public void TheMarkerIsBesideTheFolderOwnerOnlyAndGoesWithIt()
    {
        using var dir = new ScratchDir();
        var folder = Path.Combine(dir.Path, RunTempFolder.Name("run-0001"));
        RunTempFolder.Create(folder, CoreA);
        var marker = folder + ".owner";
        Assert.Equal(marker, RunTempFolder.MarkerPath(folder));
        Assert.True(File.Exists(marker));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder)); // nothing inside: the run has read-write there
        Assert.Equal(CoreA, RunTempFolder.ReadMarker(marker));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(marker));
        // Made again (same run id): the marker names the new owner.
        RunTempFolder.Create(folder, CoreB);
        Assert.Equal(CoreB, RunTempFolder.ReadMarker(marker));
        Assert.True(RunTempFolder.Remove(folder));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void TheSweepOfCoreAKeepsLiveCoreBsFolders()
    {
        using var dir = new ScratchDir();
        string Make(string run, RunTempOwner owner)
        {
            var name = RunTempFolder.Name(run);
            RunTempFolder.Create(Path.Combine(dir.Path, name), owner);
            File.WriteAllText(Path.Combine(dir.Path, name, "CoreFxPipe_MSBuild1"), "");
            return name;
        }
        var aGone = Make("a-gone", CoreA);
        var aLive = Make("a-live", CoreA);
        var bOne = Make("b-one", CoreB);
        var bTwo = Make("b-two", CoreB);
        // B's runs are not in A's registry: A's isLive knows only A's live run.
        var removed = RunTempFolder.Sweep(dir.Path, name => name == aLive, CoreA, o => o == CoreB);

        Assert.Equal([aGone], removed);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, aGone)));
        foreach (var kept in new[] { aLive, bOne, bTwo })
        {
            Assert.True(Directory.Exists(Path.Combine(dir.Path, kept)), kept);
            Assert.True(File.Exists(Path.Combine(dir.Path, kept, "CoreFxPipe_MSBuild1")), kept);
            Assert.True(File.Exists(Path.Combine(dir.Path, kept + RunTempFolder.MarkerSuffix)), kept);
        }
    }

    [Fact]
    public void ADeadOwnersFoldersAreRemoved()
    {
        using var dir = new ScratchDir();
        var b = RunTempFolder.Name("b-run");
        RunTempFolder.Create(Path.Combine(dir.Path, b), CoreB);
        var removed = RunTempFolder.Sweep(dir.Path, _ => false, CoreA, _ => false);
        Assert.Equal([b], removed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path));
    }

    [Fact]
    public void TheDefaultAliveCheckUsesPidAndStartTime()
    {
        var self = RunTempOwner.ForThisProcess(Path.GetTempPath());
        Assert.Equal(Environment.ProcessId, self.Pid);
        Assert.True(RunTempOwner.IsAlive(self));
        // Same pid, another start time: the pid was reused, the owner is gone.
        Assert.False(RunTempOwner.IsAlive(self with { StartTicks = self.StartTicks - TimeSpan.FromHours(1).Ticks }));
        Assert.False(RunTempOwner.IsAlive(self with { StartTicks = self.StartTicks + TimeSpan.FromSeconds(5).Ticks }));
        // A pid that cannot be running.
        Assert.False(RunTempOwner.IsAlive(self with { Pid = int.MaxValue }));

        using var dir = new ScratchDir();
        var reused = RunTempFolder.Name("reused-pid");
        var running = RunTempFolder.Name("running");
        var noPid = RunTempFolder.Name("no-pid");
        RunTempFolder.Create(Path.Combine(dir.Path, reused), self with { DataDirHash = CoreB.DataDirHash, StartTicks = self.StartTicks - TimeSpan.FromDays(1).Ticks });
        RunTempFolder.Create(Path.Combine(dir.Path, running), self with { DataDirHash = CoreB.DataDirHash });
        RunTempFolder.Create(Path.Combine(dir.Path, noPid), CoreB with { Pid = int.MaxValue });
        var removed = RunTempFolder.Sweep(dir.Path, _ => false, CoreA);
        Assert.Equal(new[] { noPid, reused }.Order(), removed.Order());
        Assert.True(Directory.Exists(Path.Combine(dir.Path, running)));
    }

    [Fact]
    public void WithNoMarkerAYoungFolderIsKeptAndAnOldOneRemoved()
    {
        using var dir = new ScratchDir();
        var young = RunTempFolder.Name("young");
        var old = RunTempFolder.Name("old");
        Directory.CreateDirectory(Path.Combine(dir.Path, young));
        Directory.CreateDirectory(Path.Combine(dir.Path, old));
        Directory.SetLastWriteTimeUtc(Path.Combine(dir.Path, old), DateTime.UtcNow - RunTempFolder.UnownedAge - TimeSpan.FromHours(1));

        var removed = RunTempFolder.Sweep(dir.Path, _ => false, CoreA, _ => true);

        Assert.Equal([old], removed);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, young)));
        // A day later the young one is old too.
        Assert.Equal([young], RunTempFolder.Sweep(dir.Path, _ => false, CoreA, _ => true, DateTime.UtcNow + RunTempFolder.UnownedAge + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ATamperedOrUnreadableMarkerCountsAsNoMarkerAndNeverThrows()
    {
        using var dir = new ScratchDir();
        var names = new List<string>();
        var bad = new[] { "", "garbage", "v1 zz 1 2", "v2 0123456789abcdef 1 2", "v1 0123456789abcdef -1 2", "v1 0123456789abcdef 1", new string('x', 10_000) };
        for (var i = 0; i < bad.Length; i++)
        {
            var name = RunTempFolder.Name("bad-" + i);
            Directory.CreateDirectory(Path.Combine(dir.Path, name));
            File.WriteAllText(Path.Combine(dir.Path, name + RunTempFolder.MarkerSuffix), bad[i]);
            Assert.Null(RunTempFolder.ReadMarker(Path.Combine(dir.Path, name + RunTempFolder.MarkerSuffix)));
            names.Add(name);
        }
        // A marker that is a folder, not a file.
        var dirMarker = RunTempFolder.Name("dir-marker");
        Directory.CreateDirectory(Path.Combine(dir.Path, dirMarker));
        Directory.CreateDirectory(Path.Combine(dir.Path, dirMarker + RunTempFolder.MarkerSuffix));
        names.Add(dirMarker);

        // Young: all kept, nothing thrown.
        Assert.Empty(RunTempFolder.Sweep(dir.Path, _ => false, CoreA, _ => throw new InvalidOperationException("never asked")));
        foreach (var n in names) Assert.True(Directory.Exists(Path.Combine(dir.Path, n)), n);
        // Old: removed like a folder with no marker.
        var later = DateTime.UtcNow + RunTempFolder.UnownedAge + TimeSpan.FromHours(1);
        Assert.Equal(names.Order(), RunTempFolder.Sweep(dir.Path, _ => false, CoreA, _ => true, later).Order());
    }

    [Fact]
    public void AnAliveCheckThatThrowsKeepsTheFolder()
    {
        using var dir = new ScratchDir();
        var b = RunTempFolder.Name("b-run");
        RunTempFolder.Create(Path.Combine(dir.Path, b), CoreB);
        Assert.Empty(RunTempFolder.Sweep(dir.Path, _ => false, CoreA, _ => throw new InvalidOperationException("cannot tell")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, b)));
    }

    [Fact]
    public void AnOrphanMarkerIsRemovedAndOtherFilesAreNot()
    {
        using var dir = new ScratchDir();
        var orphan = RunTempFolder.Name("orphan") + RunTempFolder.MarkerSuffix;
        File.WriteAllText(Path.Combine(dir.Path, orphan), CoreB.Serialize());
        File.WriteAllText(Path.Combine(dir.Path, "yr-notours.owner"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "something.owner"), "x");

        Assert.Empty(RunTempFolder.Sweep(dir.Path, _ => false, CoreA, _ => true));

        Assert.False(File.Exists(Path.Combine(dir.Path, orphan)));
        Assert.True(File.Exists(Path.Combine(dir.Path, "yr-notours.owner")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "something.owner")));
    }

    [Fact]
    public void ALinkNamedLikeARunFolderIsNotFollowed()
    {
        using var dir = new ScratchDir();
        using var outside = new ScratchDir();
        File.WriteAllText(Path.Combine(outside.Path, "keep.txt"), "x");
        var link = RunTempFolder.Name("link");
        try { Directory.CreateSymbolicLink(Path.Combine(dir.Path, link), outside.Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // Windows without symlink rights
        // Owned by a dead core, and its marker a link too: neither the folder link nor its target is touched.
        var markerTarget = Path.Combine(outside.Path, "marker");
        File.WriteAllText(markerTarget, CoreB.Serialize());
        File.CreateSymbolicLink(Path.Combine(dir.Path, link + RunTempFolder.MarkerSuffix), markerTarget);
        Assert.Null(RunTempFolder.ReadMarker(Path.Combine(dir.Path, link + RunTempFolder.MarkerSuffix)));

        var removed = RunTempFolder.Sweep(dir.Path, _ => false, CoreA, _ => false, DateTime.UtcNow + TimeSpan.FromDays(30));

        Assert.Empty(removed);
        Assert.True(File.Exists(Path.Combine(outside.Path, "keep.txt")));
        Assert.True(File.Exists(markerTarget));
        Assert.NotNull(new DirectoryInfo(Path.Combine(dir.Path, link)).LinkTarget);
    }

    [Fact]
    public void TheMarkerTextRoundTrips()
    {
        Assert.Equal(CoreA, RunTempOwner.Parse(CoreA.Serialize()));
        Assert.Matches("^v1 [0-9a-f]{16} 4101 638000000000000000\n$", CoreA.Serialize());
        Assert.Equal(RunTempOwner.HashDataDir("/data/x"), RunTempOwner.HashDataDir("/data/x/"));
        Assert.NotEqual(RunTempOwner.HashDataDir("/data/x"), RunTempOwner.HashDataDir("/data/y"));
        Assert.True(CoreA.SameProcess(CoreA with { StartTicks = CoreA.StartTicks + 1000 }));
        Assert.False(CoreA.SameProcess(CoreA with { DataDirHash = CoreB.DataDirHash }));
        Assert.False(CoreA.SameProcess(CoreA with { Pid = 1 }));
    }

    [Fact]
    public void TheSweepOfAMissingFolderDoesNothing() =>
        Assert.Empty(RunTempFolder.Sweep(Path.Combine(Path.GetTempPath(), "yawble-missing-" + Guid.NewGuid().ToString("N")), _ => false));

    [Fact]
    public void ThePerUserTempFolderIsFoundOnlyOnMacOS()
    {
        var found = RunTempFolder.DarwinUserTempDir();
        if (!OperatingSystem.IsMacOS()) { Assert.Null(found); return; }
        Assert.NotNull(found);
        Assert.StartsWith("/", found);
        Assert.EndsWith("/T/", found);
    }

    private sealed class ScratchDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yawble-runtemp-" + Guid.NewGuid().ToString("N")[..12]);

        public ScratchDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { }
        }
    }
}
