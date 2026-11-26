using Yawble.Core.Bus;
using Yawble.Core.Storage;
using Xunit;

#pragma warning disable CA1416

namespace Yawble.Core.Tests.Storage;

/// <summary>
/// Which folder above the data folder is made user-only, as Program.cs feeds the decision (BusHost.AppRootFor of the
/// --data-dir). Only the shell's own app folder (named as shell/src/main/app-folder.ts makes it) is; a custom
/// --data-dir named "data" in the home or a shared folder tightens only itself. Pure decisions run on every OS; the
/// on-disk ones skip on Windows.
/// </summary>
public sealed class AppRootToTightenTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("yawble-art-").FullName;
    private string Home => Path.Combine(_root, "home", "pat");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string? Decide(string dataDir) => DataFolderAccess.AppRootToTighten(BusHost.AppRootFor(dataDir), Home);

    private string In(params string[] parts) => Path.Combine([Home, .. parts]);

    // --- the name is the test ---

    [Theory]
    [InlineData("Yawble")] // macOS ~/Library/Application Support/Yawble, Windows %LOCALAPPDATA%\Yawble
    [InlineData("yawble")] // Linux ~/.local/share/yawble
    public void TheShellsOwnAppFolder_IsTightened(string name)
    {
        var appFolder = Path.Combine(Home, ".local", "share", name);
        Assert.Equal(appFolder, Decide(Path.Combine(appFolder, "data")));
    }

    [Fact]
    public void AHomeThatIsItselfNamedYawble_IsNeverTightened()
    {
        var home = Path.Combine(_root, "Yawble");
        Assert.Null(DataFolderAccess.AppRootToTighten(BusHost.AppRootFor(Path.Combine(home, "data")), home + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AFilesystemRoot_IsNeverTightened()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Null(DataFolderAccess.AppRootToTighten(root, Home));
        Assert.Null(Decide(Path.Combine(root, "data")));
    }

    [Fact]
    public void ANameThatOnlyLooksLikeIt_IsNotTightened()
    {
        Assert.Null(Decide(Path.Combine(_root, "YAWBLE", "data")));
        Assert.Null(Decide(Path.Combine(_root, "Yawble-old", "data")));
    }

    [Fact]
    public void NoAppRoot_TightensNothing()
    {
        Assert.Null(DataFolderAccess.AppRootToTighten(null, Home));
        Assert.Null(Decide(Path.Combine(_root, "Yawble", "yawble-data-x")));
    }

    [Fact]
    public void ATrailingSeparator_DoesNotChangeTheAnswer()
    {
        var appFolder = Path.Combine(_root, "Yawble");
        Assert.Equal(appFolder, DataFolderAccess.AppRootToTighten(appFolder + Path.DirectorySeparatorChar, null));
    }

    // --- the default userData, as shell/src/main/app-folder.ts makes it on each OS ---

    [Fact]
    public void DefaultUserData_Windows_LocalAppData_IsTightened() =>
        Assert.Equal(In("AppData", "Local", "Yawble"), Decide(In("AppData", "Local", "Yawble", "data")));

    [Fact]
    public void DefaultUserData_MacOS_ApplicationSupport_IsTightened() =>
        Assert.Equal(In("Library", "Application Support", "Yawble"), Decide(In("Library", "Application Support", "Yawble", "data")));

    [Fact]
    public void DefaultUserData_Linux_XdgDataHome_IsTightened()
    {
        Assert.Equal(In(".local", "share", "yawble"), Decide(In(".local", "share", "yawble", "data")));
        var xdg = Path.Combine(_root, "xdg");
        Assert.Equal(Path.Combine(xdg, "yawble"), Decide(Path.Combine(xdg, "yawble", "data")));
    }

    [Fact]
    public void DefaultUserData_WithATrailingSeparatorOrDotSegments_IsStillTightened()
    {
        var app = In(".local", "share", "yawble");
        Assert.Equal(app, Decide(Path.Combine(app, "data") + Path.DirectorySeparatorChar));
        Assert.Equal(app, Decide(Path.Combine(app, "x", "..", ".", "data")));
    }

    // --- a custom --data-dir named data ---

    [Theory]
    [InlineData("data")]
    [InlineData("projects/data")]
    [InlineData(".local/share/data")]
    [InlineData("Library/Application Support/data")]
    public void ACustomDataDirNamedData_UnderTheHome_LeavesItsParentAsItIs(string rel) =>
        Assert.Null(Decide(In(rel.Split('/'))));

    [Fact]
    public void ACustomDataDirNamedData_ThroughDotDotIntoTheHome_LeavesTheHomeAsItIs() =>
        Assert.Null(Decide(In(".local", "share", "yawble", "..", "..", "..", "data")));

    [Fact]
    public void ACustomDataDirNamedData_InASharedOrRootFolder_LeavesItAsItIs()
    {
        Assert.Null(Decide(Path.Combine(_root, "srv", "shared", "data")));
        Assert.Null(Decide(Path.Combine(Path.GetPathRoot(_root)!, "data")));
    }

    // --- nested data folders ---

    [Fact]
    public void ANestedDataDir_UnderTheDefaultOne_TightensNothingAboveIt()
    {
        // --data-dir <userData>/data/data: AppRootFor is <userData>/data, whose leaf is "data", not Yawble's name.
        Assert.Null(Decide(In(".local", "share", "yawble", "data", "data")));
        Assert.Null(Decide(In("AppData", "Local", "Yawble", "data", "data")));
    }

    [Fact]
    public void ANestedDataDir_InTheHome_TightensNothingAboveIt() =>
        Assert.Null(Decide(In("data", "data")));

    [Fact]
    public void AYawbleFolderAsTheHomeItself_ByAnyEqualSpelling_IsNeverTightened()
    {
        var home = Path.Combine(_root, "yawble");
        foreach (var spelled in new[] { home, home + Path.DirectorySeparatorChar, Path.Combine(home, "x", "..") })
            Assert.Null(DataFolderAccess.AppRootToTighten(BusHost.AppRootFor(Path.Combine(home, "data")), spelled));
    }

    /// <summary>
    /// A known limit: a custom --data-dir in a folder that is itself literally named Yawble or yawble (here a shared
    /// one) is taken for the shell's own app folder and tightened. The name is the only test.
    /// </summary>
    [Fact]
    public void ASharedFolderLiterallyNamedYawble_IsTakenForTheAppFolder()
    {
        var shared = Path.Combine(_root, "srv", "yawble");
        Assert.Equal(shared, Decide(Path.Combine(shared, "data")));
    }

    // --- on disk, through Secure (POSIX) ---

    private const UnixFileMode Rwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Wide = UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [Fact]
    public void OnDisk_TheDefaultLayout_TightensTheAppFolderAndTheDataFolder_NotTheShare()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "file modes are Linux's and macOS's");
        var share = In(".local", "share");
        var app = Path.Combine(share, "yawble");
        var data = Path.Combine(app, "data");
        Directory.CreateDirectory(data);
        File.SetUnixFileMode(share, Rwx | Wide);
        File.SetUnixFileMode(app, Rwx | Wide);
        File.SetUnixFileMode(data, Rwx | Wide);

        DataFolderAccess.Secure(data, BusHost.AppRootFor(data), _ => { });

        Assert.Equal(Rwx, File.GetUnixFileMode(app));
        Assert.Equal(Rwx, File.GetUnixFileMode(data));
        Assert.Equal(Rwx | Wide, File.GetUnixFileMode(share));
    }

    [Fact]
    public void OnDisk_ACustomDataDirInTheHome_LeavesTheHomeAndItsFilesAsTheyAre()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "file modes are Linux's and macOS's");
        var data = In("data");
        Directory.CreateDirectory(data);
        var note = In("notes.txt");
        File.WriteAllText(note, "x");
        var noteMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead;
        File.SetUnixFileMode(note, noteMode);
        File.SetUnixFileMode(Home, Rwx | Wide);

        DataFolderAccess.Secure(data, BusHost.AppRootFor(data), _ => { });

        Assert.Equal(Rwx | Wide, File.GetUnixFileMode(Home));
        Assert.Equal(noteMode, File.GetUnixFileMode(note));
        Assert.Equal(Rwx, File.GetUnixFileMode(data));
    }

    [Fact]
    public void OnDisk_AnAppFolderThatIsALink_IsNotFollowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "file modes are Linux's and macOS's");
        var other = Path.Combine(_root, "someone-else");
        Directory.CreateDirectory(Path.Combine(other, "data"));
        File.SetUnixFileMode(other, Rwx | Wide);
        Directory.CreateDirectory(In(".local", "share"));
        var app = In(".local", "share", "yawble");
        Directory.CreateSymbolicLink(app, other);

        DataFolderAccess.Secure(Path.Combine(app, "data"), BusHost.AppRootFor(Path.Combine(app, "data")), _ => { });

        Assert.Equal(Rwx | Wide, File.GetUnixFileMode(other));
    }
}
