using Yawble.Core.Lifetime;
using Xunit;

namespace Yawble.Core.Tests.Lifetime;

/// <summary>
/// The shell's per-launch IPC folders, in a temp base folder. The "sockets" are plain files and whether one answers is a
/// fake, so this runs the same on every OS.
/// </summary>
public sealed class RuntimeFoldersTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("yawble-rt-test-").FullName;
    private readonly DateTime _now = DateTime.UtcNow.AddMinutes(10); // every folder made here is 10 minutes old
    private readonly HashSet<string> _answering = [];

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    private RuntimeFolders Folders() => new(p => _answering.Contains(p), () => _now);

    private string Launch(string name, params string[] files)
    {
        var dir = Path.Combine(_base, name);
        Directory.CreateDirectory(dir);
        foreach (var f in files)
        {
            var path = Path.Combine(dir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        return dir;
    }

    [Fact]
    public void TheLaunchFolderIsTheSocketsParentOnlyWhenNamedLikeMkdtemp()
    {
        var launch = Path.Combine(_base, "yawble-Ab12Cd");
        Assert.Equal(launch, RuntimeFolders.LaunchFolderOf(Path.Combine(launch, "core.sock")));
        Assert.Null(RuntimeFolders.LaunchFolderOf(Path.Combine(_base, "yawble-data-x1", "yawble-0011.sock"))); // the tests' own layout
        Assert.Null(RuntimeFolders.LaunchFolderOf(Path.Combine(_base, "core.sock")));
        if (OperatingSystem.IsWindows()) Assert.Null(RuntimeFolders.LaunchFolderOf(@"\\.\pipe\yawble-0123456789abcdef"));
    }

    [Fact]
    public void StaleFoldersWithDeadSocketsAreSwept_IncludingTheCompanionsNestedOne()
    {
        var dead = Launch("yawble-aaaaaa", "core.sock", Path.Combine("yawble-0a1b2c3d", "yawble-77.sock"));
        var empty = Launch("yawble-bbbbbb");

        var removed = Folders().SweepStale(_base, own: null);

        Assert.Equal(new[] { dead, empty }.Order(), removed.Order());
        Assert.False(Directory.Exists(dead));
        Assert.False(Directory.Exists(empty));
    }

    [Fact]
    public void AFolderWhoseSocketAnswersIsNeverTouched()
    {
        var live = Launch("yawble-cccccc", "core.sock", Path.Combine("yawble-0a1b2c3d", "yawble-77.sock"));
        _answering.Add(Path.Combine(live, "yawble-0a1b2c3d", "yawble-77.sock")); // the companion's socket still answers

        Assert.Empty(Folders().SweepStale(_base, own: null));
        Assert.True(File.Exists(Path.Combine(live, "core.sock")));
    }

    [Fact]
    public void OurOwnFolder_AYoungFolder_OneWithOtherFiles_AndOtherNamesAreKept()
    {
        var own = Launch("yawble-dddddd");
        var young = Launch("yawble-eeeeee", "core.sock");
        Directory.SetLastWriteTimeUtc(young, _now.AddSeconds(-30)); // the shell has just made it; its core is starting
        var notOurs = Launch("yawble-ffffff", "core.sock", "notes.txt");
        var otherName = Launch("yawble-data-123456", "core.sock");
        var longer = Launch("yawble-gggggggg", "core.sock");

        Assert.Empty(Folders().SweepStale(_base, own));

        foreach (var dir in new[] { own, young, notOurs, otherName, longer }) Assert.True(Directory.Exists(dir), dir);
        Assert.True(File.Exists(Path.Combine(notOurs, "core.sock")));
    }

    [Fact]
    public void AMissingBaseFolderIsNotAnError()
    {
        Assert.Empty(Folders().SweepStale(Path.Combine(_base, "nope"), own: null));
    }

    [Fact]
    public void RemoveOwnTakesOnlyAnEmptyFolder()
    {
        var emptied = Launch("yawble-hhhhhh");
        Assert.True(RuntimeFolders.RemoveOwn(emptied));
        Assert.False(Directory.Exists(emptied));

        var withFile = Launch("yawble-iiiiii", "core.sock");
        Assert.False(RuntimeFolders.RemoveOwn(withFile));
        Assert.True(File.Exists(Path.Combine(withFile, "core.sock")));
    }
}
