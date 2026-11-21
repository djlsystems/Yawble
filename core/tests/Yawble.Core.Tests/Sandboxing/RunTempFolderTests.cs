using System.Text;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.AgentCommands;

/// <summary>macOS's per-run short temp folder (B119 Part A): its name, the sun_path length rule, making, removing and the sweep.</summary>
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

    [Fact]
    public void TheSweepRemovesOnlyRunFoldersWhoseRunIsGone()
    {
        using var dir = new ScratchDir();
        var gone = RunTempFolder.Name("gone-run");
        var live = RunTempFolder.Name("live-run");
        foreach (var name in new[] { gone, live, "yr-notours", "com.apple.something", "yr-0123456789" })
            Directory.CreateDirectory(Path.Combine(dir.Path, name));
        File.WriteAllText(Path.Combine(dir.Path, gone, "left.txt"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "yr-abcdef01"), "a file, not a run folder");

        var removed = RunTempFolder.Sweep(dir.Path, name => name == live);

        Assert.Equal([gone], removed);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, gone)));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, live)));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "yr-notours")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "com.apple.something")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "yr-0123456789")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "yr-abcdef01")));
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
