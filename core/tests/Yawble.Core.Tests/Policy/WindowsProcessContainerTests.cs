using System.Text.Json.Nodes;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Policy;

/// <summary>
/// The Windows ProcessContainer request, as measured on Windows 11:
/// <list type="bullet">
/// <item>ui.disable: true stops every program that imports USER32 (git, grok, PowerShell) with
/// STATUS_DLL_INIT_FAILED, so Windows emits ui.disable: false with every other UI limit explicit and strict.</item>
/// <item>Without enumeratePaths on the drive root, cmd del, rmdir /s and Remove-Item are denied inside a
/// read/write grant (microsoft/mxc#1676) and Node cannot run a script (lstat C:\). enumeratePaths is recursive: names on the
/// drive become listable; file contents and deniedPaths stay closed.</item>
/// </list>
/// Linux and macOS are unchanged.
/// </summary>
public class WindowsProcessContainerTests
{
    private static (CompiledRun Compiled, RunSpec Run) Compile(TargetOs os, CredentialMode mode = CredentialMode.ClaudeSubscription)
    {
        var run = PolicyFixtures.Run(os, mode);
        return (SandboxRequestCompiler.Compile(PolicyFixtures.Global, PolicyFixtures.Override, PolicyFixtures.Loosen, run), run);
    }

    private static List<string> Strings(JsonNode? array) =>
        ((JsonArray?)array ?? []).Select(n => n!.GetValue<string>()).ToList();

    [Theory]
    [InlineData(CredentialMode.ClaudeSubscription)]
    [InlineData(CredentialMode.ClaudeHostSignIn)]
    [InlineData(CredentialMode.GrokHostSignIn)]
    public void WindowsKeepsWin32kButEveryOtherUiLimitIsExplicitAndStrict(CredentialMode mode)
    {
        var (c, _) = Compile(TargetOs.Windows, mode);
        var r = c.Request;
        Assert.False(r["ui"]!["disable"]!.GetValue<bool>());
        Assert.Equal("none", r["ui"]!["clipboard"]!.GetValue<string>());
        Assert.False(r["ui"]!["injection"]!.GetValue<bool>());
        var pcUi = r["processContainer"]!["ui"]!.AsObject();
        Assert.Equal("container", pcUi["isolation"]!.GetValue<string>());
        Assert.False(pcUi["desktopSystemControl"]!.GetValue<bool>());
        Assert.False(pcUi["ime"]!.GetValue<bool>());
        Assert.Equal("none", pcUi["systemSettings"]!.GetValue<string>());
        Assert.Equal(["ui", "filesystem"], r["processContainer"]!.AsObject().Select(p => p.Key));
    }

    [Theory]
    [InlineData(TargetOs.Linux)]
    [InlineData(TargetOs.MacOS)]
    public void LinuxAndMacOsAreUnchanged(TargetOs os)
    {
        var (c, _) = Compile(os);
        Assert.True(c.Request["ui"]!["disable"]!.GetValue<bool>());
        Assert.Null(c.Request["processContainer"]);
    }

    /// <summary>
    /// enumeratePaths is recursive in the sandbox (measured: with C:\ alone, names anywhere on the drive list, while
    /// deniedPaths still list as EPERM). Ancestors below the root would add nothing, so the request names exactly what
    /// it grants: the drive root of every grant.
    /// </summary>
    [Fact]
    public void EnumeratePathsAreTheDriveRootsOfTheGrants()
    {
        var (c, _) = Compile(TargetOs.Windows);
        Assert.Equal([@"C:\"], Strings(c.Request["processContainer"]!["filesystem"]!["enumeratePaths"]));
    }

    [Fact]
    public void EachDriveHoldingAGrantIsEnumerated()
    {
        var roots = SandboxRequestCompiler.WindowsEnumeratePaths([@"C:\Users\alice\repo", @"D:\work\x", @"d:\other"], [],
            new PolicyPaths(TargetOs.Windows, @"C:\Users\alice"));
        Assert.Equal([@"C:\", @"D:\"], roots);
    }

    [Theory]
    [InlineData(TargetOs.Windows, "1")]
    [InlineData(TargetOs.Linux, null)]
    [InlineData(TargetOs.MacOS, null)]
    public void ClaudeOnWindowsUsesItsPowerShellToolBecauseGitBashCannotStartInAProcessContainer(TargetOs os, string? expected)
    {
        var (c, _) = Compile(os, CredentialMode.ClaudeHostSignIn);
        var env = Strings(c.Request["process"]!["env"]).Select(e => e.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(expected, env.GetValueOrDefault("CLAUDE_CODE_USE_POWERSHELL_TOOL"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_GIT_BASH_PATH"));
    }

    /// <summary>
    /// Claude Code on Windows keys ~/.claude.json "projects" by the path with FORWARD slashes (e.g. "D:/Work/x").
    /// Seeded with backslashes, the TUI stops on its folder-trust dialog in the sandbox.
    /// </summary>
    [Theory]
    [InlineData(TargetOs.Windows)]
    [InlineData(TargetOs.Linux)]
    [InlineData(TargetOs.MacOS)]
    public void TheClaudeTrustSeedUsesTheProjectKeyClaudeWritesOnThisOs(TargetOs os)
    {
        var (c, run) = Compile(os, CredentialMode.ClaudeHostSignIn);
        var seed = c.Seeds.Single(s => s.Path.EndsWith(".claude.json", StringComparison.Ordinal));
        var projects = JsonNode.Parse(seed.Content)!["projects"]!.AsObject();
        var key = Assert.Single(projects).Key;
        Assert.Equal(os == TargetOs.Windows ? run.Layout.ClonePath.Replace('\\', '/') : run.Layout.ClonePath, key);
        Assert.True(projects[key]!["hasTrustDialogAccepted"]!.GetValue<bool>());
    }

    /// <summary>
    /// Host tools the agent needs on Windows (git: C:\Program Files\Git\cmd), as the launcher detects them: on PATH
    /// after the CLI's own folders, and granted read-only by name (Program Files is readable to a ProcessContainer by
    /// its default ACL anyway; the request says so explicitly).
    /// </summary>
    [Fact]
    public void HostToolFoldersAreOnPathAndGrantedReadOnly()
    {
        var run = PolicyFixtures.Run(TargetOs.Windows, CredentialMode.ClaudeHostSignIn);
        run = run with { Layout = run.Layout with { HostToolFolders = [@"C:\Program Files\Git\cmd"] } };
        var c = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, run);
        var env = Strings(c.Request["process"]!["env"]).Select(e => e.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var path = env["PATH"].Split(';');
        Assert.Contains(@"C:\Program Files\Git\cmd", path);
        Assert.True(Array.IndexOf(path, @"C:\Program Files\Git\cmd") < Array.IndexOf(path, @"C:\Windows\System32"));
        Assert.Contains(@"C:\Program Files\Git\cmd", Strings(c.Request["filesystem"]!["readonlyPaths"]));
        Assert.Empty(SandboxRequestInvariants.Check(c, run));
    }

    // ---- The checker: the Windows processContainer section may hold only these values.

    private static void AssertCaught(Action<JsonObject> mutate, string expected)
    {
        var (c, run) = Compile(TargetOs.Windows);
        var request = (JsonObject)c.Request.DeepClone();
        mutate(request);
        var problems = SandboxRequestInvariants.CheckRequest(request, run);
        Assert.Contains(problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheCompiledWindowsRequestPassesTheChecker()
    {
        var (c, run) = Compile(TargetOs.Windows);
        Assert.Empty(SandboxRequestInvariants.CheckRequest(c.Request, run));
    }

    [Fact]
    public void CatchesAWeakerUiIsolation() =>
        AssertCaught(r => r["processContainer"]!["ui"]!["isolation"] = "desktop", "isolation");

    [Fact]
    public void CatchesDesktopSystemControlImeOrSystemSettings()
    {
        AssertCaught(r => r["processContainer"]!["ui"]!["desktopSystemControl"] = true, "desktopSystemControl");
        AssertCaught(r => r["processContainer"]!["ui"]!["ime"] = true, "ime");
        AssertCaught(r => r["processContainer"]!["ui"]!["systemSettings"] = "all", "systemSettings");
    }

    [Fact]
    public void CatchesUiEnabledWithoutTheProcessContainerLimits() =>
        AssertCaught(r => r.Remove("processContainer"), "processContainer.ui");

    [Fact]
    public void StillCatchesCaptureDenialsLearningModeAndCapabilities()
    {
        AssertCaught(r => r["processContainer"]!["captureDenials"] = new JsonObject { ["mode"] = "allow" }, "captureDenials");
        AssertCaught(r => r["processContainer"]!["learningMode"] = true, "learningMode");
        AssertCaught(r => r["processContainer"]!["capabilities"] = new JsonArray("internetClient"), "capabilities");
    }

    [Fact]
    public void CatchesAnEnumeratePathThatIsNotADriveRoot() =>
        AssertCaught(r => ((JsonArray)r["processContainer"]!["filesystem"]!["enumeratePaths"]!).Add(@"C:\Users\alice\Documents"), "enumeratePaths");

    [Fact]
    public void CatchesTheRootOfADriveWithNoGrant() =>
        AssertCaught(r => ((JsonArray)r["processContainer"]!["filesystem"]!["enumeratePaths"]!).Add(@"E:\"), "enumeratePaths");
}
