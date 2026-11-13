using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Policy;

/// <summary>The tighten rule, the loosen block and the floor rules.</summary>
public class PolicyMergeTests
{
    private static RunLayout Linux => PolicyFixtures.Layout(TargetOs.Linux, AgentCli.Claude);

    private static PolicyPatch O(string json) => PolicyPatch.Parse(json, PatchKind.Override);
    private static PolicyPatch L(string json) => PolicyPatch.Parse(json, PatchKind.Loosen);

    private static GlobalPolicy WithMemory(int mb) =>
        PolicyFixtures.Global with { Compute = PolicyFixtures.Global.Compute with { MemoryMb = mb } };

    // ---- An override cannot widen allowHosts or memory without the loosen block.

    [Fact]
    public void AnOverrideCannotWidenAllowHosts()
    {
        var r = PolicyMerger.Merge(PolicyFixtures.Global, O("""{"network":{"allowHosts":["registry.npmjs.org","evil.example.com"]}}"""), null, Linux);
        Assert.Equal(["registry.npmjs.org"], r.Policy.AllowHosts);
        Assert.Contains(r.Widening, w => w.Contains("evil.example.com"));
        Assert.False(r.Policy.IsLoosened);

        // And the compiled run's gateway never sees it.
        var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, O("""{"network":{"allowHosts":["evil.example.com"]}}"""), null,
            PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription));
        Assert.Empty(compiled.Gateway.AllowHosts);
    }

    [Fact]
    public void TheLoosenBlockCanAddAHost()
    {
        var r = PolicyMerger.Merge(PolicyFixtures.Global, O("""{"network":{"allowHosts":[]}}"""), L("""{"network":{"allowHosts":["evil.example.com"]}}"""), Linux);
        Assert.Equal(["evil.example.com"], r.Policy.AllowHosts);
        Assert.Equal(["network.allowHosts + evil.example.com"], r.Policy.Loosened);
        Assert.True(r.Policy.IsLoosened);
    }

    [Fact]
    public void AnOverrideCannotRaiseMemory()
    {
        var r = PolicyMerger.Merge(WithMemory(4096), O("""{"compute":{"memoryMb":8192}}"""), null, Linux);
        Assert.Equal(4096, r.Policy.Compute.MemoryMb);
        Assert.Contains(r.Widening, w => w.Contains("memoryMb"));

        var lower = PolicyMerger.Merge(WithMemory(4096), O("""{"compute":{"memoryMb":2048}}"""), null, Linux);
        Assert.Equal(2048, lower.Policy.Compute.MemoryMb);
        Assert.Empty(lower.Widening);
    }

    [Fact]
    public void TheLoosenBlockCanRaiseMemoryUpToTheCeiling()
    {
        var r = PolicyMerger.Merge(WithMemory(4096), null, L("""{"compute":{"memoryMb":8192}}"""), Linux, machineMemoryMb: 16384);
        Assert.Equal(8192, r.Policy.Compute.MemoryMb);
        Assert.Equal(["compute.memoryMb 4096 -> 8192"], r.Policy.Loosened);

        var tooMuch = PolicyMerger.Merge(WithMemory(4096), null, L("""{"compute":{"memoryMb":16000}}"""), Linux, machineMemoryMb: 16384);
        Assert.Equal(12288, tooMuch.Policy.Compute.MemoryMb); // 75% of the machine
        Assert.Contains(tooMuch.Violations, v => v.Contains("hard ceiling"));
    }

    [Fact]
    public void TimeoutsTakeTheMinimumAndLoosenStopsAt24Hours()
    {
        var r = PolicyMerger.Merge(PolicyFixtures.Global, O("""{"compute":{"wallTimeoutSec":900,"idleTimeoutSec":99999}}"""), null, Linux);
        Assert.Equal(900, r.Policy.Compute.WallTimeoutSec);
        Assert.Equal(1800, r.Policy.Compute.IdleTimeoutSec);
        Assert.Contains(r.Widening, w => w.Contains("idleTimeoutSec"));

        var loosened = PolicyMerger.Merge(PolicyFixtures.Global, null, L("""{"compute":{"wallTimeoutSec":200000}}"""), Linux);
        Assert.Equal(FloorRules.TimeoutCeilingSec, loosened.Policy.Compute.WallTimeoutSec);
        Assert.Contains(loosened.Violations, v => v.Contains("hard ceiling"));
    }

    // ---- Floor rules: nobody adds a vendor host, not even the loosen block or the global policy.

    [Theory]
    [InlineData("api.anthropic.com")]
    [InlineData("platform.claude.com")]
    [InlineData("claude.ai")]
    [InlineData("console.anthropic.com")]
    [InlineData("api.x.ai")]
    [InlineData("auth.x.ai")]
    [InlineData("accounts.x.ai")]
    [InlineData("cli-chat-proxy.grok.com")]
    [InlineData("code.grok.com")]
    [InlineData("grok.com")]
    [InlineData("bus.yawble.internal")]
    [InlineData("127.0.0.1")]
    [InlineData("*.example.com")]
    public void NoOneCanAddAVendorOrReservedHost(string host)
    {
        var loosen = PolicyMerger.Merge(PolicyFixtures.Global, null, new PolicyPatch(Network: new NetworkPatch([host])), Linux);
        Assert.NotEmpty(loosen.Violations);
        Assert.DoesNotContain(host, loosen.Policy.AllowHosts);

        var global = PolicyFixtures.Global with { Network = new NetworkPolicy([host]) };
        Assert.NotEmpty(PolicyMerger.CheckGlobal(global));
        Assert.Throws<PolicyException>(() => SandboxRequestCompiler.Compile(global, null, null, PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription)));
    }

    // ---- Filesystem.

    [Fact]
    public void OverrideGrantsMustLieInsideTheGrantableRoots()
    {
        var r = PolicyMerger.Merge(PolicyFixtures.Global, O("""{"filesystem":{"readwrite":["~/src/project","/etc"],"readonly":["~/Documents"]}}"""), null, Linux);
        Assert.Contains("/home/alice/src/project", r.Policy.Readwrite);
        Assert.DoesNotContain("/etc", r.Policy.Readwrite);
        Assert.DoesNotContain("/home/alice/Documents", r.Policy.Readonly);
        Assert.Equal(2, r.Widening.Count);

        var loosened = PolicyMerger.Merge(PolicyFixtures.Global, null, L("""{"filesystem":{"readonly":["/home/alice/notes"]}}"""), Linux);
        Assert.Contains("/home/alice/notes", loosened.Policy.Readonly);
        Assert.Equal(["filesystem.readonly + /home/alice/notes"], loosened.Policy.Loosened);

        // A personal folder is a global deny by default: the loosen block cannot open it; the person removes
        // it from the global policy's denied list first.
        var personal = PolicyMerger.Merge(PolicyFixtures.Global, null, L("""{"filesystem":{"readonly":["/home/alice/Documents"]}}"""), Linux);
        Assert.DoesNotContain("/home/alice/Documents", personal.Policy.Readonly);
    }

    [Fact]
    public void AnOverrideCanNarrowReadWriteToReadOnly()
    {
        var global = PolicyFixtures.Global with { Filesystem = PolicyFixtures.Global.Filesystem with { Readwrite = ["/srv/shared"] } };
        var r = PolicyMerger.Merge(global, O("""{"filesystem":{"readonly":["/srv/shared"]}}"""), null, Linux);
        Assert.Contains("/srv/shared", r.Policy.Readonly);
        Assert.DoesNotContain("/srv/shared", r.Policy.Readwrite);
        Assert.Empty(r.Widening);
    }

    [Fact]
    public void DenialsAreAUnionAndALoosenBlockCannotTouchThem()
    {
        var r = PolicyMerger.Merge(PolicyFixtures.Global, O("""{"filesystem":{"denied":["~/src/secret"]}}"""), null, Linux);
        Assert.Contains("/home/alice/src/secret", r.Policy.Denied);
        Assert.Contains("/home/alice/.ssh", r.Policy.Denied);
        var e = Assert.Throws<PolicyException>(() => L("""{"filesystem":{"denied":[]}}"""));
        Assert.Contains(e.Errors, x => x.Contains("no deny can be removed"));
    }

    [Theory]
    [InlineData("/home/alice/.local/share/yawble")]                          // the app data root
    [InlineData("/home/alice/.local/share/yawble/data")]                     // the core's state
    [InlineData("/home/alice/.local/share/yawble/data/yawble.db")]      // the database
    [InlineData("/home/alice/.local/share/yawble/grok-home")]                // the host Grok home
    [InlineData("/home/alice/.local/share/yawble/grok-home/auth.json")]
    [InlineData("/home/alice/.local/share/yawble/teams/alpha/members/dev2")] // another member
    [InlineData("/home/alice/.local/share/yawble/teams/beta")]               // another team
    [InlineData("/home/alice/.local/share/yawble/teams/alpha")]              // contains another member
    [InlineData("/home/alice")]                                                   // the home (contains the app data root)
    [InlineData("/")]
    public void TheLoosenBlockCanNeverGrantAnImplicitDenial(string path)
    {
        foreach (var patch in new[] { $$$"""{"filesystem":{"readonly":["{{{path}}}"]}}""", $$$"""{"filesystem":{"readwrite":["{{{path}}}"]}}""" })
        {
            var r = PolicyMerger.Merge(PolicyFixtures.Global, null, L(patch), Linux);
            Assert.NotEmpty(r.Violations);
            Assert.Throws<PolicyException>(() => SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, L(patch),
                PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription)));
        }
    }

    [Fact]
    public void PathsAreCanonicalisedBeforeComparison()
    {
        // ~/src/link -> ~/.local/share/yawble/data: inside the grantable root by name, not in fact.
        string Canon(string p) => p.StartsWith("/home/alice/src/link", StringComparison.Ordinal)
            ? "/home/alice/.local/share/yawble/data" + p["/home/alice/src/link".Length..]
            : p;
        var r = PolicyMerger.Merge(PolicyFixtures.Global, O("""{"filesystem":{"readwrite":["~/src/link/x"]}}"""), null, Linux, Canon);
        Assert.Empty(r.Policy.Readwrite);
        Assert.Contains(r.Widening, w => w.Contains("/home/alice/.local/share/yawble/data/x"));
        var loosened = PolicyMerger.Merge(PolicyFixtures.Global, null, L("""{"filesystem":{"readonly":["~/src/link"]}}"""), Linux, Canon);
        Assert.Contains(loosened.Violations, v => v.Contains("implicitly denied"));

        var dots = PolicyMerger.Merge(PolicyFixtures.Global, O("""{"filesystem":{"readwrite":["~/src/../.ssh"]}}"""), null, Linux);
        Assert.DoesNotContain("/home/alice/.ssh", dots.Policy.Readwrite);
        Assert.Contains(dots.Widening, w => w.Contains("/home/alice/.ssh"));
    }

    [Fact]
    public void WindowsPathsCompareCaseInsensitively()
    {
        var win = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
        var r = PolicyMerger.Merge(PolicyFixtures.Global, null, L("""{"filesystem":{"readonly":["c:/users/ALICE/appdata/local/YAWBLE/DATA"]}}"""), win);
        Assert.Contains(r.Violations, v => v.Contains("implicitly denied"));
        var ok = PolicyMerger.Merge(PolicyFixtures.Global, O("""{"filesystem":{"readwrite":["C:/Users/alice/SRC/app"]}}"""), null, win);
        Assert.Contains(@"C:\Users\alice\SRC\app", ok.Policy.Readwrite);
        Assert.Empty(ok.Widening);
    }

    // ---- Shape rules.

    [Fact]
    public void AppSettingsAreNeverOverridable()
    {
        Assert.Throws<PolicyException>(() => O("""{"app":{"maxConcurrentAgents":64}}"""));
        Assert.Throws<PolicyException>(() => L("""{"app":{"maxConcurrentAgents":64}}"""));
        Assert.Throws<PolicyException>(() => O("""{"filesystem":{"grantableRoots":["/"]}}"""));
        Assert.Throws<PolicyException>(() => L("""{"ui":{"clipboard":"all"}}"""));
        Assert.Throws<PolicyException>(() => O("""{"network":{"allowHosts":["a.example.com"],"cidrs":[]}}"""));
    }

    [Fact]
    public void ClipboardTakesTheMostRestrictive()
    {
        var r = PolicyMerger.Merge(PolicyFixtures.Global, O("""{"ui":{"clipboard":"all"}}"""), null, Linux);
        Assert.Equal("none", r.Policy.Clipboard);
        Assert.Contains(r.Widening, w => w.Contains("clipboard"));
        Assert.NotEmpty(PolicyMerger.CheckGlobal(PolicyFixtures.Global with { Ui = new UiPolicy("all") }));
    }

    [Fact]
    public void GlobalDocumentRoundTripsAndRejectsUnknownFields()
    {
        var json = PolicyFixtures.Global.ToJson().ToJsonString();
        Assert.Equal(PolicyFixtures.Global.ToJson().ToJsonString(), GlobalPolicy.Parse(json).ToJson().ToJsonString());
        var e = Assert.Throws<PolicyException>(() => GlobalPolicy.Parse("""{"network":{"allowHosts":[],"audit":true}}"""));
        Assert.Contains(e.Errors, x => x.Contains("network.audit"));
        Assert.Equal(4, GlobalPolicy.Default.App.MaxConcurrentAgents);
        Assert.Empty(GlobalPolicy.Default.Network.AllowHosts);
    }

    /// <summary>
    /// On Windows the drive root is enumerable (microsoft/mxc#1676), so the names of every
    /// folder on it are listable except inside denied paths. The default therefore denies the personal folders too,
    /// on every OS (harmless where the home is closed anyway; on macOS a deny also hides the metadata).
    /// </summary>
    [Fact]
    public void TheDefaultDeniesCredentialFoldersAndThePersonalFolders()
    {
        Assert.Equal(
            ["~/.ssh", "~/.aws", "~/.config/gh", "~/Desktop", "~/Documents", "~/Downloads", "~/Music", "~/OneDrive", "~/Pictures", "~/Videos"],
            GlobalPolicy.Default.Filesystem.Denied);
    }

    [Theory]
    [InlineData(TargetOs.Windows)]
    [InlineData(TargetOs.Linux)]
    [InlineData(TargetOs.MacOS)]
    public void ThePersonalFoldersReachTheCompiledDenyList(TargetOs os)
    {
        var run = PolicyFixtures.Run(os, CredentialMode.ClaudeHostSignIn);
        var compiled = SandboxRequestCompiler.Compile(GlobalPolicy.Default, null, null, run);
        var denied = ((System.Text.Json.Nodes.JsonArray)compiled.Request["filesystem"]!["deniedPaths"]!).Select(n => n!.GetValue<string>()).ToList();
        var home = PolicyFixtures.Machine(os).Home;
        var sep = os == TargetOs.Windows ? '\\' : '/';
        foreach (var name in new[] { "Desktop", "Documents", "Downloads", "OneDrive" })
            Assert.Contains(home + sep + name, denied);
    }
}
