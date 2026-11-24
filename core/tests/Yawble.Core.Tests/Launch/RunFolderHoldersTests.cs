using Yawble.Core.Launch;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// The run-folder backstop: <see cref="MacRunFolderHolders"/> finds the processes holding a run folder by cwd or open
/// files, whatever their ppid or group. All against <see cref="FakeProcessPaths"/>: no real process, any OS.
/// </summary>
public class RunFolderHoldersTests
{
    private const string Run = "/var/folders/ab/T/yr-283eebe3";
    private const string RealRun = "/private/var/folders/ab/T/yr-283eebe3";

    private static FakeProcessPaths Paths()
    {
        var paths = new FakeProcessPaths(self: 100);
        paths.Real[Run] = RealRun;
        return paths;
    }

    [Fact]
    public void AProcessWithAnOpenFileUnderTheFolderIsFound()
    {
        var paths = Paths();
        paths.Add(25734, cwd: "/Users/dev", files: ["/dev/null", RealRun + "/MSBuildTemp/tmp1.exec.cmd"]);
        paths.Add(25735, cwd: "/Users/dev", files: ["/usr/lib/dyld"]);

        Assert.Equal([25734], new MacRunFolderHolders(paths).Under(Run));
    }

    [Fact]
    public void AProcessWhoseCwdIsTheFolderOrUnderItIsFound()
    {
        var paths = Paths();
        paths.Add(501, cwd: RealRun);
        paths.Add(502, cwd: RealRun + "/work/src");
        paths.Add(503, cwd: "/tmp");

        Assert.Equal([501, 502], new MacRunFolderHolders(paths).Under(Run));
    }

    [Fact]
    public void TheFolderAsGivenMatchesToo()
    {
        var paths = Paths();
        paths.Add(501, cwd: Run + "/x");

        Assert.Equal([501], new MacRunFolderHolders(paths).Under(Run + "/"));
    }

    [Fact]
    public void ASiblingWithTheSamePrefixIsNotUnderTheFolder()
    {
        var paths = Paths();
        paths.Add(501, cwd: RealRun + "-other");
        paths.Add(502, files: [RealRun + "x/file"]);

        Assert.Empty(new MacRunFolderHolders(paths).Under(Run));
    }

    [Fact]
    public void TheCoreItselfAndPidsZeroAndOneAreNeverReported()
    {
        var paths = Paths();
        paths.Add(0, cwd: RealRun);
        paths.Add(1, cwd: RealRun);
        paths.Add(100, files: [RealRun + "/log"]);
        paths.Add(600, files: [RealRun + "/log"]);

        Assert.Equal([600], new MacRunFolderHolders(paths).Under(Run));
    }

    [Fact]
    public void AProcessThatCannotBeReadOrIsGoneIsSkippedQuietly()
    {
        var paths = Paths();
        paths.Add(501, cwd: RealRun, denied: true);
        paths.Add(502, cwd: RealRun, gone: true);
        paths.Add(503, files: [RealRun + "/obj/a.dll"]);

        Assert.Equal([503], new MacRunFolderHolders(paths).Under(Run));
    }

    [Fact]
    public void AnUnreadableProcessListFindsNothing()
    {
        var paths = Paths();
        paths.Add(501, cwd: RealRun);
        paths.ListFails = true;

        Assert.Empty(new MacRunFolderHolders(paths).Under(Run));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("relative/yr-1")]
    public void AnEmptyRootOrRelativeFolderFindsNothing(string folder)
    {
        var paths = Paths();
        paths.Add(501, cwd: "/Users/dev", files: ["/etc/hosts"]);

        Assert.Empty(new MacRunFolderHolders(paths).Under(folder));
    }

    [Fact]
    public void AFolderThatNoLongerResolvesStillMatchesAsGiven()
    {
        var paths = new FakeProcessPaths(self: 100);
        paths.Add(501, files: [Run + "/a"]);

        Assert.Equal([501], new MacRunFolderHolders(paths).Under(Run));
    }
}

/// <summary>A process list with cwd and open files per pid, for <see cref="MacRunFolderHolders"/>.</summary>
internal sealed class FakeProcessPaths(int self) : IProcessPaths
{
    private sealed record Proc(string? Cwd, string[] Files, bool Denied, bool Gone);

    private readonly SortedDictionary<int, Proc> _procs = [];

    public Dictionary<string, string> Real { get; } = [];

    public bool ListFails { get; set; }

    public int Self { get; } = self;

    public void Add(int pid, string? cwd = null, string[]? files = null, bool denied = false, bool gone = false) =>
        _procs[pid] = new Proc(cwd, files ?? [], denied, gone);

    public IReadOnlyList<int> Pids() => ListFails ? throw new IOException("proc_listallpids failed") : [.. _procs.Keys];

    // EPERM reads as nothing (null / empty), like libproc's 0 return; a pid gone since the list throws, the worst case.
    public string? Cwd(int pid) => Read(pid)?.Cwd;

    public IReadOnlyList<string> OpenFiles(int pid) => Read(pid)?.Files ?? [];

    public string RealPath(string path) => Real.TryGetValue(path, out var real) ? real : path;

    private Proc? Read(int pid)
    {
        var p = _procs[pid];
        if (p.Gone) throw new InvalidOperationException($"pid {pid} is gone");
        return p.Denied ? null : p;
    }
}
