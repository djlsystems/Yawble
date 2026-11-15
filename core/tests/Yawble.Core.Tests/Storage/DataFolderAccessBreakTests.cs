using Yawble.Core.Bus;
using Yawble.Core.Storage;
using Xunit;

// The mode checks run only on Linux and macOS (each test skips on Windows first).
#pragma warning disable CA1416

namespace Yawble.Core.Tests.Storage;

/// <summary>
/// B031 item 7, tester's checks of DataFolderAccess.Secure as Program.cs calls it, with the app root from
/// BusHost.AppRootFor (the parent of any folder named "data"). POSIX only.
/// </summary>
public sealed class DataFolderAccessBreakTests : IDisposable
{
    private const UnixFileMode Rwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Wide = UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly string _root = Directory.CreateTempSubdirectory("yawble-dfb-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ACustomDataDirNamedData_InASharedFolder_LeavesTheSharedFolderAsItIs()
    {
        // --data-dir /srv/shared/data (or ~/data): AppRootFor gives /srv/shared (or the home), which is not Yawble's.
        // Expected: only the data folder is made user-only; the shared parent keeps its group and other access.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "file modes are Linux's and macOS's");
        var shared = Path.Combine(_root, "shared");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "team-notes.txt"), "x");
        File.SetUnixFileMode(shared, Rwx | Wide);
        var data = Path.Combine(shared, "data");

        DataFolderAccess.Secure(data, BusHost.AppRootFor(data), _ => { });

        Assert.Equal(Rwx, File.GetUnixFileMode(data));
        Assert.Equal(Rwx | Wide, File.GetUnixFileMode(shared));
    }

    [Fact]
    public void ADataDirThatIsALinkToSomeoneElsesFolder_IsLeftAsItIs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "file modes are Linux's and macOS's");
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var file = Path.Combine(other, "notes.txt");
        File.WriteAllText(file, "x");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        File.SetUnixFileMode(other, Rwx | Wide);
        var link = Path.Combine(_root, "data-link");
        Directory.CreateSymbolicLink(link, other);

        Assert.Equal(0, DataFolderAccess.Secure(link, null, _ => { }));
        Assert.Equal(Rwx | Wide, File.GetUnixFileMode(other));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead, File.GetUnixFileMode(file));
    }
}
