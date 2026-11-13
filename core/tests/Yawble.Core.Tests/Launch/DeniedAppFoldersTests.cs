using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// A fresh install's first runs: every run is denied the app's own folders, and the sandbox refuses a rule on a path that
/// does not exist (Windows: CreateProcessSecurityEnvironment, 0x80070003), so the missing ones are made before the run.
/// A temp folder stands in for the app's folder; nothing outside it is touched.
/// </summary>
public sealed class DeniedAppFoldersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("yawble-denied-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private RunLayout Layout(string appRoot) => RunLayout.Standard(TargetOsExtensions.Host, Path.Combine(_dir, "home"), appRoot, Path.Combine(_dir, "install"),
        new MemberRef("alpha", "Ada"), AgentCli.Claude,
        new CliInstall { ExecutablePath = Path.Combine(_dir, "claude.exe"), Command = [Path.Combine(_dir, "claude.exe")], ReadOnlyFolders = [], ConfigDenied = [], PathFolders = [] },
        [new MemberRef("alpha", "Ada"), new MemberRef("alpha", "Ben"), new MemberRef("beta", "Cy")]);

    [Fact]
    public void TheAppsMissingFoldersAreMade_SoAFreshInstallsRunsStart()
    {
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(Path.Combine(app, "data"));
        var layout = Layout(app);
        Assert.False(Directory.Exists(layout.CredentialDir)); // a fresh install using the OS credential store has none

        SandboxLauncher.EnsureDeniedAppFolders(layout, app);

        Assert.True(Directory.Exists(layout.CredentialDir));
        Assert.True(Directory.Exists(layout.HostGrokHome));
        Assert.NotEmpty(layout.OtherMemberRoots);
        Assert.All(layout.OtherMemberRoots, d => Assert.True(Directory.Exists(d), d));
        Assert.All(layout.OtherTeamRoots, d => Assert.True(Directory.Exists(d), d));
        Assert.Empty(Directory.GetFileSystemEntries(layout.CredentialDir)); // empty: nothing is written into it
    }

    [Fact]
    public void NothingIsMade_ForALayoutOfAnotherComputer_OrOutsideTheAppsFolder()
    {
        var elsewhere = Path.Combine(_dir, "not-made-app");
        SandboxLauncher.EnsureDeniedAppFolders(Layout(elsewhere), elsewhere);
        Assert.False(Directory.Exists(elsewhere));

        var app = Path.Combine(_dir, "app2");
        Directory.CreateDirectory(app);
        var outside = Path.Combine(_dir, "outside");
        var layout = Layout(app) with { OtherMemberRoots = [outside] };
        SandboxLauncher.EnsureDeniedAppFolders(layout, app);
        Assert.False(Directory.Exists(outside));
    }
}
