using Yawble.Core.Bus;
using Yawble.Core.Storage;
using Xunit;

namespace Yawble.Core.Tests.Storage;

/// <summary>
/// Which folder above the data folder is made user-only. Only the shell's own app folder (named as
/// shell/src/main/app-folder.ts makes it) is; a custom --data-dir named "data" in the home or a shared folder tightens
/// only itself. A pure decision, so it runs on every OS.
/// </summary>
public sealed class AppRootToTightenTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "yawble-art");
    private static readonly string Home = Path.Combine(Base, "home", "kofi");

    private static string? Decide(string dataDir) => DataFolderAccess.AppRootToTighten(BusHost.AppRootFor(dataDir), Home);

    [Theory]
    [InlineData("Yawble")] // macOS ~/Library/Application Support/Yawble, Windows %LOCALAPPDATA%\Yawble
    [InlineData("yawble")] // Linux ~/.local/share/yawble
    public void TheShellsOwnAppFolder_IsTightened(string name)
    {
        var appFolder = Path.Combine(Home, ".local", "share", name);
        Assert.Equal(appFolder, Decide(Path.Combine(appFolder, "data")));
    }

    [Fact]
    public void ADataFolderInTheHome_LeavesTheHomeAsItIs() =>
        Assert.Null(Decide(Path.Combine(Home, "data")));

    [Fact]
    public void ADataFolderInASharedFolder_LeavesTheSharedFolderAsItIs() =>
        Assert.Null(Decide(Path.Combine(Base, "srv", "shared", "data")));

    [Fact]
    public void AHomeThatIsItselfNamedYawble_IsNeverTightened()
    {
        var home = Path.Combine(Base, "Yawble");
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
        Assert.Null(Decide(Path.Combine(Base, "YAWBLE", "data")));
        Assert.Null(Decide(Path.Combine(Base, "Yawble-old", "data")));
    }

    [Fact]
    public void NoAppRoot_TightensNothing()
    {
        Assert.Null(DataFolderAccess.AppRootToTighten(null, Home));
        Assert.Null(Decide(Path.Combine(Base, "Yawble", "yawble-data-x")));
    }

    [Fact]
    public void ATrailingSeparator_DoesNotChangeTheAnswer()
    {
        var appFolder = Path.Combine(Base, "Yawble");
        Assert.Equal(appFolder, DataFolderAccess.AppRootToTighten(appFolder + Path.DirectorySeparatorChar, null));
    }
}
