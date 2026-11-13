using System.Text.Json.Nodes;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Launch;

public class LaunchUnitTests
{
    /// <summary>
    /// The sandbox cannot bind a missing grant and on Linux blames slirp4netns; the native backend names the path and
    /// does not call the sandbox (so this runs on any host, with or without a sandbox).
    /// </summary>
    [Fact]
    public void TheNativeBackendNamesAMissingGrantInsteadOfCallingSandbox()
    {
        var present = Directory.CreateTempSubdirectory("yawble-preflight-").FullName;
        try
        {
            var missing = Path.Combine(present, "host-clis", "grok");
            var request = new JsonObject
            {
                ["filesystem"] = new JsonObject
                {
                    ["readonlyPaths"] = new JsonArray(present, missing),
                    ["readwritePaths"] = new JsonArray(present),
                    ["deniedPaths"] = new JsonArray(Path.Combine(present, "not-there-is-fine")),
                },
            }.ToJsonString();
            var e = Assert.Throws<SandboxStartException>(() => new NativeSandboxBackend().Spawn(request));
            Assert.Equal(NativeSandboxBackend.PreflightStatus, e.Status);
            Assert.StartsWith(NativeSandboxBackend.MissingGrantsReason, e.Message);
            Assert.EndsWith(missing, e.Message);
            Assert.Throws<SandboxStartException>(() => new NativeSandboxBackend().SpawnWithPty(request, PtySize.Default));
        }
        finally
        {
            Directory.Delete(present, true);
        }
    }

    /// <summary>A run token as the run-token registry issues them (yawrt_ + 43 URL-safe chars).</summary>
    private static string NewToken() => new Yawble.Core.Gateway.RunTokenRegistry().Issue(1, TimeSpan.FromHours(1)).Value;

    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcde";

    [Theory]
    [InlineData($$"""{"key":"{{Token}}","auth_mode":"external"}""", true)]
    [InlineData($$"""{"auth_mode":"external","key":"{{Token}}","created":"2026-11-06"}""", true)]
    [InlineData($$"""{"key":"{{Token}}","auth_mode":"oauth"}""", false)]
    [InlineData("""{"key":"another-runs-token-000000000000000000000000","auth_mode":"external"}""", false)]
    [InlineData($$"""{"key":"{{Token}}","auth_mode":"external","refresh_token":"x"}""", false)]
    [InlineData($$"""{"key":"{{Token}}","auth_mode":"external","session":"x"}""", false)]
    [InlineData("""{"api_key":"xai-fake"}""", false)]
    [InlineData("not json", false)]
    [InlineData("[]", false)]
    public void GrokAuthJsonIsCleanOnlyInTheExternalPlaceholderShape(string json, bool clean) =>
        Assert.Equal(clean, PostRunChecks.IsPlaceholderOnly(json, Token));

    private static RunLayout TempLayout(AgentCli cli, out string root)
    {
        root = Directory.CreateTempSubdirectory("yawble-launch-").FullName;
        return RunLayout.Standard(TargetOsExtensions.Host, Path.Combine(root, "home"), Path.Combine(root, "app"), Path.Combine(root, "install"),
            new Yawble.Core.Policy.MemberRef("alpha", "dev1"), cli, Agents.TestClis.InstallFor(root, Path.Combine(root, "home"), cli), []);
    }

    [Fact]
    public void PreRunRemovesAStalePlaceholderQuietlyAndRecordsAnythingElse()
    {
        var layout = TempLayout(AgentCli.Grok, out var root);
        try
        {
            var home = AgentPresets.GrokHome(layout);
            Directory.CreateDirectory(home);
            var file = Path.Combine(home, "auth.json");
            File.WriteAllText(file, $$"""{"key":"{{NewToken()}}","auth_mode":"external"}""");
            Assert.Empty(PostRunChecks.PreRun(layout, CredentialMode.GrokLogin));
            Assert.False(File.Exists(file));

            File.WriteAllText(file, """{"key":"fake-session","auth_mode":"oauth"}""");
            var events = PostRunChecks.PreRun(layout, CredentialMode.GrokLogin);
            Assert.Equal(PostRunChecks.GrokAuthFile, Assert.Single(events).Kind);
            Assert.False(File.Exists(file));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SeedsMergeIntoWhatTheCliKeptAndOverwriteOnlyTheirOwnKeys()
    {
        var layout = TempLayout(AgentCli.Claude, out var root);
        try
        {
            var seeds = AgentPresets.Seeds(layout, CredentialMode.ClaudeSubscription);
            RunFolders.Prepare(layout, seeds);
            var file = Path.Combine(AgentPresets.ClaudeConfigDir(layout), ".claude.json");
            // The CLI adds its own state between runs, and someone flips a seeded key.
            var state = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            state["numStartups"] = 7;
            state["hasCompletedOnboarding"] = false;
            File.WriteAllText(file, state.ToJsonString());

            RunFolders.Prepare(layout, seeds);
            var after = JsonNode.Parse(File.ReadAllText(file))!;
            Assert.Equal(7, after["numStartups"]!.GetValue<int>());
            Assert.True(after["hasCompletedOnboarding"]!.GetValue<bool>());
            Assert.Empty(SandboxRequestInvariants.ClaudeSeedProblems(seeds, layout));
            foreach (var dir in layout.ImplicitReadwrite) Assert.True(Directory.Exists(dir));
            if (!OperatingSystem.IsWindows())
            {
                // Owner-only: no group or other bits (a setgid bit inherited from the parent folder is harmless).
                const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(layout.Workspace) & others);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AClaudeSeedMissingAnyOfTheFourKeysIsRefused()
    {
        var run = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription);
        var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, run);
        Assert.Empty(SandboxRequestInvariants.Check(compiled, run));
        foreach (var drop in new[] { "hasCompletedOnboarding", "hasSeenAutoDefaultNudge", "projects", "skipDangerousModePermissionPrompt" })
        {
            var seeds = compiled.Seeds.Select(s =>
            {
                var node = JsonNode.Parse(s.Content);
                if (node is JsonObject o && o.Remove(drop)) return s with { Content = o.ToJsonString() };
                return s;
            }).ToList();
            Assert.NotEmpty(SandboxRequestInvariants.Check(compiled with { Seeds = seeds }, run));
        }
    }

    [Fact]
    public void TheSelfUpdateAndMarketplaceSwitchesAreInvariants()
    {
        foreach (var (mode, key) in new[]
        {
            (CredentialMode.ClaudeSubscription, "CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL"),
            (CredentialMode.GrokLogin, "GROK_AUTO_UPDATE"),
            (CredentialMode.GrokLogin, "GROK_CHANGELOG_OFFLINE"),
        })
        {
            var run = PolicyFixtures.Run(TargetOs.Linux, mode);
            var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, run);
            var request = (JsonObject)compiled.Request.DeepClone();
            var env = request["process"]!["env"]!.AsArray();
            env.Remove(env.First(e => e!.GetValue<string>().StartsWith(key + "=", StringComparison.Ordinal)));
            Assert.Contains(SandboxRequestInvariants.CheckRequest(request, run), p => p.Contains(key));
        }
    }

    [Fact]
    public void RunTokensAreFreshAndAcceptedByTheCompiler()
    {
        var a = NewToken();
        Assert.NotEqual(a, NewToken());
        var run = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.GrokLogin) with { RunToken = a };
        Assert.NotNull(SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, run));
    }

    [Fact]
    public void TheOutputBufferKeepsTheTailForReplay()
    {
        var b = new RunOutputBuffer(capacity: 8);
        var live = new List<byte>();
        b.Data += d => live.AddRange(d.ToArray());
        b.Write("hello "u8);
        b.Write("world"u8);
        Assert.Equal("lo world", System.Text.Encoding.ASCII.GetString(b.Snapshot()));
        Assert.Equal("hello world", System.Text.Encoding.ASCII.GetString([.. live]));
    }

    /// <summary>
    /// The native binding against the real libmxc_ffi: a compiled request goes through
    /// mxc_spawn_json into the sandbox's Linux backend. Where bwrap is missing, the sandbox refuses and the launcher
    /// gets its message; where bwrap is present, see <see cref="RealSandboxTests"/>.
    /// </summary>
    [Fact]
    public void TheNativeSpawnReachesSandboxLinuxBackend()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux only");
        if (RealSandboxTests.HasBwrap) Assert.Skip("bwrap is installed: the real end-to-end test covers this host");
        var run = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription);
        var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, run);
        // The fixture's fictional /home/alice paths are missing here, so the core's preflight refuses before the sandbox...
        var preflight = Assert.Throws<SandboxStartException>(() => new NativeSandboxBackend().Spawn(compiled.RequestJson));
        Assert.Equal(NativeSandboxBackend.PreflightStatus, preflight.Status);
        // ...and with the grants pointed at folders that exist, the request reaches the sandbox, which refuses for want of bwrap.
        var existing = Directory.CreateTempSubdirectory("yawble-native-").FullName;
        try
        {
            var request = compiled.Request.DeepClone().AsObject();
            foreach (var key in new[] { "readonlyPaths", "readwritePaths" })
                request["filesystem"]![key] = new System.Text.Json.Nodes.JsonArray(existing);
            var json = request.ToJsonString();
            var ex = Assert.Throws<SandboxStartException>(() => new NativeSandboxBackend().Spawn(json));
            Assert.NotEqual(NativeSandboxBackend.PreflightStatus, ex.Status);
            Assert.Contains("bwrap", ex.Message, StringComparison.OrdinalIgnoreCase);
            var pty = Assert.Throws<SandboxStartException>(() => new NativeSandboxBackend().SpawnWithPty(json, PtySize.Default));
            Assert.Contains("bwrap", pty.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(existing, recursive: true); }
    }
}

/// <summary>Runs only where the sandbox's Linux backend can run (bwrap, slirp4netns, iptables, iproute2 installed).</summary>
public class RealSandboxTests
{
    public static bool HasBwrap { get; } = OperatingSystem.IsLinux() && OnPath("bwrap");

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, tool)));

    [Fact]
    public async Task AToyCommandRunsAndIsKilledInARealSandboxSandbox()
    {
        if (!HasBwrap) Assert.Skip("bwrap is not installed on this host: the sandbox's Linux backend refuses every request");
        // A minimal request: the shell runs read-only from the host, no network, and sleeps; Stop must end it.
        var request = new JsonObject
        {
            ["version"] = SandboxPin.RequestContract,
            ["containment"] = "bubblewrap",
            ["process"] = new JsonObject
            {
                ["commandLine"] = "/bin/sh -c 'echo inside; sleep 600'",
                ["cwd"] = "/tmp",
                ["env"] = new JsonArray("PATH=/usr/bin:/bin"),
                ["timeout"] = 60_000,
            },
            ["filesystem"] = new JsonObject { ["readonlyPaths"] = new JsonArray("/usr", "/bin", "/lib", "/lib64"), ["readwritePaths"] = new JsonArray(), ["deniedPaths"] = new JsonArray() },
            ["network"] = new JsonObject { ["egress"] = new JsonObject { ["default"] = "deny" }, ["ingress"] = new JsonObject { ["default"] = "deny", ["hostLoopback"] = "deny" } },
            ["telemetry"] = new JsonObject { ["enabled"] = false },
        };
        using var p = new NativeSandboxBackend().Spawn(request.ToJsonString());
        var output = new RunOutputBuffer();
        var s = new RunSupervisor(p, 60, 0, new RunActivity(TimeProvider.System), output);
        var run = s.RunAsync(TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!System.Text.Encoding.UTF8.GetString(output.Snapshot()).Contains("inside") && DateTime.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        s.RequestStop();
        var exit = await run;
        Assert.Equal(RunEnd.Stopped, exit.End);
        Assert.False(exit.TreeSurvived);
        Assert.True(exit.KillLatency < TimeSpan.FromSeconds(5));
    }
}
