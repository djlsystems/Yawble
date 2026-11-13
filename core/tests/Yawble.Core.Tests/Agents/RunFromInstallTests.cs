using System.Text.Json.Nodes;
using Yawble.Core.Agents;
using Yawble.Core.Bus;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// The launcher runs the member's CLI from where the person installed it (DetectedCli, at launch), granting
/// exactly its install folders read-only and denying its config folders. No pinned version, no app toolchain.
/// </summary>
public class RunFromInstallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Home = "/home/alice";

    private static CliInstall Linux(DetectedCli cli, params string[] onDisk) =>
        CliInstall.FromDetected(TargetOs.Linux, Home, cli, new FakeFolders(TargetOs.Linux, onDisk));

    [Fact]
    public void GrokInsideItsConfigFolderGetsItsSiblingsDeniedOneByOne()
    {
        var exe = "/home/alice/.grok/downloads/grok-1.0.77-linux-x86_64";
        var install = Linux(TestClis.Found("grok", exe, ["/home/alice/.grok/downloads"], ["/home/alice/.grok"]),
            exe, "/home/alice/.grok/downloads/grok-1.0.76-linux-x86_64", "/home/alice/.grok/auth.json", "/home/alice/.grok/bin/grok",
            "/home/alice/.grok/sessions/a/b.jsonl");

        Assert.Equal([exe], install.Command);
        Assert.Equal(["/home/alice/.grok/downloads"], install.ReadOnlyFolders);
        // ~/.grok itself cannot be denied (it would cover the grant); everything in it except the way to the grant is.
        Assert.Equal(["/home/alice/.grok/auth.json", "/home/alice/.grok/bin", "/home/alice/.grok/sessions"], install.ConfigDenied);
    }

    [Fact]
    public void AGrantDeeperInsideAConfigFolderIsReachedThroughItsAncestors()
    {
        var exe = "/home/alice/.tool/a/b/prog";
        var install = Linux(TestClis.Found("claude", exe, ["/home/alice/.tool/a/b"], ["/home/alice/.tool"]),
            exe, "/home/alice/.tool/secret", "/home/alice/.tool/a/other", "/home/alice/.tool/a/b/lib.so");
        Assert.Equal(["/home/alice/.tool/a/other", "/home/alice/.tool/secret"], install.ConfigDenied.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ConfigFoldersThatDoNotExistAreNotDenied()
    {
        var exe = "/usr/local/lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe";
        var install = Linux(TestClis.Found("claude", exe, ["/usr/local/lib/node_modules/@anthropic-ai/claude-code"],
            ["/home/alice/.claude", "/home/alice/.claude.json"], "npm"), exe, "/home/alice/.claude.json");
        Assert.Equal(["/home/alice/.claude.json"], install.ConfigDenied);
    }

    [Theory]
    [InlineData("/home/alice", "contains the home folder")]
    [InlineData("/home", "contains the home folder")]
    [InlineData("/", "contains the home folder")]
    [InlineData("/home/alice/.claude", "contains its config folder")]
    [InlineData("/home/alice/.local/share", "is not inside its read-only folders")]
    public void AnInstallThatCannotBeGrantedSafelyIsRefused(string readOnly, string why)
    {
        var exe = "/home/alice/.claude/local/claude";
        var cli = TestClis.Found("claude", exe, [readOnly], ["/home/alice/.claude", "/home/alice/.claude.json"]);
        var e = Assert.Throws<ArgumentException>(() => Linux(cli, exe));
        Assert.Contains(why, e.Message);
    }

    [Fact]
    public void AJavaScriptEntryPointRunsUnderTheNodeInItsFolders()
    {
        var script = "/opt/node/lib/node_modules/@anthropic-ai/claude-code/cli.js";
        var cli = TestClis.Found("claude", script, ["/opt/node/lib/node_modules/@anthropic-ai/claude-code", "/opt/node/bin"], [], "npm");
        Assert.Equal(["/opt/node/bin/node", script], Linux(cli, script, "/opt/node/bin/node").Command);
        var e = Assert.Throws<ArgumentException>(() => Linux(cli with { ReadOnlyFolders = ["/opt/node/lib/node_modules/@anthropic-ai/claude-code"] }, script));
        Assert.Contains("no read-only folder holds node", e.Message);
    }

    [Fact]
    public void AWindowsCmdShimIsRefused()
    {
        var shim = @"C:\Users\alice\AppData\Roaming\npm\claude.cmd";
        var cli = TestClis.Found("claude", shim, [@"C:\Users\alice\AppData\Roaming\npm"], []);
        var e = Assert.Throws<ArgumentException>(() => CliInstall.FromDetected(TargetOs.Windows, @"C:\Users\alice", cli, new FakeFolders(TargetOs.Windows, shim)));
        Assert.Contains(".cmd wrapper", e.Message);
    }

    [Fact]
    public void ANotInstalledCliSaysSoWithTheInstallHelp()
    {
        var e = Assert.Throws<ArgumentException>(() => Linux(TestClis.Missing("grok")));
        Assert.Contains("grok is not installed", e.Message);
        Assert.Contains("Install grok from its vendor.", e.Message);
    }

    /// <summary>Every golden layout: the program is inside a read-only grant, no writable grant reaches an install
    /// folder, config folders are denied, and nothing grants the home folder itself.</summary>
    [Theory]
    [MemberData(nameof(GoldenRequestTests.Cases), MemberType = typeof(GoldenRequestTests))]
    public void EveryGoldenRequestGrantsExactlyTheInstall(string name)
    {
        var compiled = GoldenCases.Compile(name);
        var c = GoldenCases.All[name];
        var install = PolicyFixtures.Cli(c.Os, AgentPresets.CliOf(c.Mode), c.Install ?? PolicyFixtures.DefaultInstall(AgentPresets.CliOf(c.Mode)),
            AgentPresets.IsHostSignIn(c.Mode));
        var fs = compiled.Request["filesystem"]!;
        var ro = fs["readonlyPaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var rw = fs["readwritePaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var denied = fs["deniedPaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var p = new PolicyPaths(c.Os, PolicyFixtures.Machine(c.Os).Home);
        foreach (var folder in install.ReadOnlyFolders)
        {
            Assert.Contains(folder, ro);
            Assert.DoesNotContain(rw, g => p.Overlaps(g, folder));
        }
        Assert.All(install.Command, part => Assert.Contains(ro, g => p.IsWithin(part, g)));
        Assert.NotEmpty(install.ConfigDenied);
        Assert.All(install.ConfigDenied, d => Assert.Contains(d, denied));
        Assert.DoesNotContain(ro.Concat(rw), g => p.IsWithin(p.Home, g));
        // The CLI itself, or (Windows, Grok) the app's own yawble-run directly in front of it.
        IReadOnlyList<string> launch = c.Os == TargetOs.Windows && AgentPresets.CliOf(c.Mode) == AgentCli.Grok
            ? [@"C:\Program Files\Yawble\resources\bin\yawble-run.exe", .. install.Command]
            : install.Command;
        Assert.StartsWith(CommandLine.Join(c.Os, launch), compiled.Request["process"]!["commandLine"]!.GetValue<string>());
        // Host sign-in: exactly the sign-in file, read-only, never writable, and the CLI pointed at it; otherwise none of it.
        var signInEnv = compiled.Request["process"]!["env"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        if (AgentPresets.IsHostSignIn(c.Mode))
        {
            var file = Assert.Single(install.SignInReadOnly);
            Assert.Contains(file, ro);
            Assert.DoesNotContain(rw, g => p.Overlaps(g, file));
            Assert.DoesNotContain(ro, g => g != file && p.IsWithin(file, g) && !install.ReadOnlyFolders.Contains(g));
            Assert.Contains(signInEnv, e => e.StartsWith(AgentPresets.CliOf(c.Mode) == AgentCli.Claude ? "CLAUDE_SECURESTORAGE_CONFIG_DIR=" : "GROK_AUTH_PATH="));
            Assert.DoesNotContain(signInEnv, e => e.StartsWith("CLAUDE_CODE_OAUTH_TOKEN=") || e.StartsWith("ANTHROPIC_AUTH_TOKEN=")
                                                  || e.StartsWith("XAI_API_KEY=") || e.StartsWith("GROK_AUTH_PROVIDER_COMMAND="));
            // The rest of the config folder stays denied: its other entries are on the deny list one by one.
            Assert.DoesNotContain(file, denied);
            Assert.Contains(denied, d => p.IsWithin(d, p.Combine(p.Home, AgentPresets.CliOf(c.Mode) == AgentCli.Claude ? ".claude" : ".grok")));
        }
        else
        {
            Assert.DoesNotContain(signInEnv, e => e.StartsWith("CLAUDE_SECURESTORAGE_CONFIG_DIR=") || e.StartsWith("GROK_AUTH_PATH="));
            Assert.DoesNotContain(ro, g => g.EndsWith(".credentials.json") || g.EndsWith("auth.json"));
        }
        // Self-update off for both CLIs (the install is read-only anyway).
        var env = compiled.Request["process"]!["env"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        if (AgentPresets.CliOf(c.Mode) == AgentCli.Claude)
            foreach (var v in new[] { "DISABLE_AUTOUPDATER=1", "DISABLE_UPDATES=1" }) Assert.Contains(v, env);
        else
        {
            foreach (var v in new[] { "GROK_DISABLE_AUTOUPDATER=1", "GROK_AUTO_UPDATE=0" }) Assert.Contains(v, env);
            Assert.Contains("--no-auto-update", compiled.Request["process"]!["commandLine"]!.GetValue<string>());
        }
    }

    [Fact]
    public void AWritableGrantOverAnInstallFolderIsRefusedByTheInvariants()
    {
        var run = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription);
        // Only the person's loosen block can grant outside the grantable roots; even it cannot make the install writable.
        var patch = PolicyPatch.Parse("""{ "filesystem": { "readwrite": ["~/.local/share/claude"] } }""", PatchKind.Loosen);
        var e = Assert.ThrowsAny<Exception>(() => SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, patch, run));
        Assert.Contains("install folder", e.Message);
    }

    /// <summary>Detected at every launch, never cached: a CLI updated (moved to a new version folder) between two runs
    /// is the one the second run gets; a CLI removed between runs fails the next run with the install help.</summary>
    [Fact]
    public async Task EachLaunchDetectsTheCliAgain()
    {
        var root = Directory.CreateTempSubdirectory("yawble-detect-").FullName;
        var home = Path.Combine(root, "machine", "home");
        Directory.CreateDirectory(home);
        var v1 = Path.Combine(root, "v1");
        var v2 = Path.Combine(root, "v2");
        Directory.CreateDirectory(v1);
        Directory.CreateDirectory(v2);
        File.WriteAllText(Path.Combine(v1, "claude"), "");
        File.WriteAllText(Path.Combine(v2, "claude"), "");
        var clis = new FakeCliDetector(TestClis.Found("claude", Path.Combine(v1, "claude"), [v1], []), TestClis.Missing("grok"));
        await using var kit = await LaunchKit.StartAsync(clis: clis);
        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };

        await kit.Bus.PersonTellAsync("alpha", "dev1", "first", ct: Ct);
        await kit.OutcomeAsync("dev1");
        clis.Replace(TestClis.Found("claude", Path.Combine(v2, "claude"), [v2], [], version: "2.1.400"));
        await kit.Bus.PersonTellAsync("alpha", "dev1", "second", ct: Ct);
        await Wait.UntilAsync(() => kit.Backend.Requests.Count(r => LaunchKit.IsFor(r.Json, "dev1")) >= 2, "the second run");

        var requests = kit.Backend.Requests.Where(r => LaunchKit.IsFor(r.Json, "dev1")).Select(r => JsonNode.Parse(r.Json)!).ToList();
        string Cmd(JsonNode r) => r["process"]!["commandLine"]!.GetValue<string>();
        List<string> Ro(JsonNode r) => r["filesystem"]!["readonlyPaths"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.StartsWith(HostPathCanonicalizer.Canonicalize(Path.Combine(v1, "claude")), Cmd(requests[0]).Trim('\''));
        Assert.Contains(HostPathCanonicalizer.Canonicalize(v1), Ro(requests[0]));
        Assert.StartsWith(HostPathCanonicalizer.Canonicalize(Path.Combine(v2, "claude")), Cmd(requests[1]).Trim('\''));
        Assert.DoesNotContain(HostPathCanonicalizer.Canonicalize(v1), Ro(requests[1]));
        Assert.True(clis.Calls >= 2);

        // grok is not installed: dev2's run fails at once, naming it, and no sandbox starts for it.
        await kit.Bus.PersonTellAsync("alpha", "dev2", "hello", ct: Ct);
        var failed = await kit.OutcomeAsync("dev2");
        Assert.Equal(MessageTypes.Failed, failed.Type);
        Assert.Contains("grok is not installed", failed.Payload);
        Assert.DoesNotContain(kit.Backend.Requests, r => LaunchKit.IsFor(r.Json, "dev2"));
    }
}
