using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Yawble.Core.Guardrail;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Guardrail;

/// <summary>
/// Whether the sandbox's native backend on this host starts a sandbox in proxy-only mode, the mode every agent run and
/// every guardrail check uses. The sandbox has no request probe on Linux or macOS, so the gate is the sandbox's own answer to a
/// harmless real request (<c>/bin/true</c>, egress only through a loopback proxy). Null when it runs.
/// </summary>
internal static class RealSandboxProxyMode
{
    private static readonly Lazy<string?> s_refusal = new(Probe);

    public static string? Refusal => s_refusal.Value;

    private static string? Probe()
    {
        if (!OperatingSystem.IsLinux()) return "these real-sandbox tests are written for the sandbox's Linux (bubblewrap) backend";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var request = new JsonObject
        {
            ["version"] = SandboxPin.RequestContract,
            ["containment"] = "bubblewrap",
            ["process"] = new JsonObject { ["commandLine"] = "/bin/true", ["cwd"] = "/", ["env"] = new JsonArray("PATH=/usr/bin:/bin"), ["timeout"] = 60_000 },
            ["filesystem"] = new JsonObject { ["readonlyPaths"] = new JsonArray(), ["readwritePaths"] = new JsonArray(), ["deniedPaths"] = new JsonArray() },
            ["network"] = new JsonObject { ["egress"] = new JsonObject { ["default"] = "deny" }, ["ingress"] = new JsonObject { ["default"] = "deny", ["hostLoopback"] = "deny" } },
            ["runtimeConfig"] = new JsonObject { ["networkProxy"] = $"http://127.0.0.1:{port}" },
            ["telemetry"] = new JsonObject { ["enabled"] = false },
        };
        try
        {
            var e = NativeSandbox.Execute(request.ToJsonString());
            return e.Status != 0 ? "the sandbox refused a proxy-mode request: " + e.Message
                : e.ExitCode != 0 ? $"/bin/true exited {e.ExitCode} in the proxy-mode sandbox: {e.Stderr}"
                : null;
        }
        catch (DllNotFoundException e)
        {
            return "libmxc_ffi is not loadable: " + e.Message;
        }
    }

    public static void SkipUnlessAvailable()
    {
        if (Refusal is { } why) Assert.Skip(why);
    }

    /// <summary>Also appends the report to $YAWBLE_REAL_SANDBOX_REPORT when set.</summary>
    public static void Report(GuardrailReport report)
    {
        var text = report.ToText();
        TestContext.Current.SendDiagnosticMessage(text);
        if (Environment.GetEnvironmentVariable("YAWBLE_REAL_SANDBOX_REPORT") is { Length: > 0 } file)
            File.AppendAllText(file, $"--- {DateTimeOffset.UtcNow:O}\n{text}\n");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealSandboxGuardrailCollection
{
    public const string Name = "real sandbox guardrail check";
}

/// <summary>
/// The guardrail check under a REAL sandbox (Linux: bubblewrap + slirp4netns proxy mode), with the real
/// per-run gateway, the real probe and the native mxc_ffi. Every secret on disk is a fake; the gateway has no
/// credentials at all. Skipped only when the sandbox itself refuses a proxy-mode request on this host.
/// </summary>
[Collection(RealSandboxGuardrailCollection.Name)]
public class RealSandboxGuardrailTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertPassed(GuardrailReport report)
    {
        Assert.True(report.Sandbox.Refusal is null, $"the sandbox refused: {report.Sandbox.Refusal}");
        var bad = report.Results.Where(r => !r.AsExpected).Select(r => $"{r.Id}={r.Verdict}: {r.Evidence}").ToList();
        Assert.True(bad.Count == 0, "unexpected: " + string.Join(" | ", bad) + "\n" + report.Sandbox.Stderr);
        Assert.True(report.Passed);
        // It ran as the launcher's own run, on the sandbox's native backend, and ended by itself.
        Assert.Equal("yawble-ffi", report.Run!.Backend);
        Assert.Equal("completed", report.Run.End);
    }

    [Fact]
    public async Task UnderTheGlobalPolicyEveryEscapeIsBlocked()
    {
        RealSandboxProxyMode.SkipUnlessAvailable();
        await using var kit = GuardrailKit.Create(realGateway: true);
        var report = await kit.Check(new NativeSandboxBackend()).RunAsync(null, null, Ct);
        RealSandboxProxyMode.Report(report);
        AssertPassed(report);
        Assert.True(report.Subject.Synthetic);
    }

    [Theory]
    [InlineData("payments", "ada", "claude")]
    [InlineData("payments", "ben", "grok")]
    public async Task InsideAMembersSandboxEveryEscapeIsBlocked(string team, string member, string cli)
    {
        RealSandboxProxyMode.SkipUnlessAvailable();
        await using var kit = GuardrailKit.Create(realGateway: true);
        var report = await kit.Check(new NativeSandboxBackend()).RunAsync(team, member, Ct);
        RealSandboxProxyMode.Report(report);
        AssertPassed(report);
        Assert.Equal(cli, report.Subject.Cli);

        GuardrailResult R(string id) => report.Results.Single(r => r.Id == id);
        // Filesystem holds.
        foreach (var id in new[] { "app.data", "app.database", "app.grok-home", "app.grok-home-auth", "member.other-member", "member.real-other-member",
                     "member.other-team", "home.ssh", "home.ssh-id_ed25519", "home.claude-credentials", "home.grok-auth", "write.host-home",
                     "write.app-data", "write.other-team" })
            Assert.Equal(Verdicts.Blocked, R(id).Verdict);
        Assert.Equal(Verdicts.Allowed, R("write.clone").Verdict);
        // Network holds: no direct path out, the gateway refuses and logs.
        foreach (var id in new[] { "net.direct-ipv4", "net.direct-ipv6", "net.dns", "net.host-loopback", "net.host-loopback-slirp" })
            Assert.Equal(Verdicts.Blocked, R(id).Verdict);
        Assert.Equal(Verdicts.Allowed, R("gateway.reachable").Verdict);
        Assert.True(R("gateway.connect-denied-host").Logged);
        Assert.Contains(kit.GatewayEvents(), e => e.Kind == "connect-denied" && e.Target!.StartsWith("example.com", StringComparison.Ordinal)
                                                  && e.Member == $"{team}/{member}");
        // The CLI's install: its program readable, its folder listable, neither writable; its config folders blocked.
        AssertCliInstallHolds(report);
        // Processes and secrets.
        Assert.Equal(Verdicts.Blocked, R("proc.host-signal").Verdict);
        Assert.Equal(Verdicts.Blocked, R("proc.host-ptrace").Verdict);
        Assert.Equal(Verdicts.Blocked, R("secrets.scan").Verdict);
        Assert.Contains(cli == "claude" ? "CLAUDE_CODE_OAUTH_TOKEN" : "YAWBLE_RUN_TOKEN", R("secrets.run-token").Evidence);
        // Nothing the probe wrote or the core planted is left on the host.
        Assert.Empty(Directory.EnumerateFiles(kit.Root, ".yawble-guardcheck-*", SearchOption.AllDirectories));
        // A check before the member's first run leaves its clone folder absent or empty, so that run still gets
        // its clone (TeamRepos.PrepareAsync leaves a non-empty clone folder alone).
        var clonePath = kit.Layout(team, member, cli == "claude" ? AgentCli.Claude : AgentCli.Grok).ClonePath;
        Assert.True(!Directory.Exists(clonePath) || !Directory.EnumerateFileSystemEntries(clonePath).Any(),
            $"the check left entries in {clonePath}");
    }

    internal static void AssertCliInstallHolds(GuardrailReport report, bool expectSibling = false)
    {
        GuardrailResult R(string id) => report.Results.Single(r => r.Id == id);
        Assert.Equal(Verdicts.Allowed, R("cli.read-program").Verdict);
        Assert.Equal(Verdicts.Allowed, R("cli.list-install:0").Verdict);
        Assert.Equal(Verdicts.Blocked, R("write.cli-program").Verdict);
        Assert.Equal(Verdicts.Blocked, R("write.cli-install:0").Verdict);
        Assert.False(R("write.cli-install:0").SandboxOnly, "an install folder is a real read-only bind, not a tmpfs mask");
        var config = report.Results.Where(r => r.Category == "cli-config").ToList();
        Assert.NotEmpty(config);
        Assert.All(config, r => Assert.Equal(Verdicts.Blocked, r.Verdict));
        if (expectSibling) Assert.Equal(Verdicts.Blocked, R("cli.install-sibling:0").Verdict);
    }

    /// <summary>
    /// The vendors' own layouts inside the home: xAI's grok lives in ~/.grok/downloads, inside its config folder, so
    /// ~/.grok's other entries are denied one by one; native Claude's versions folder sits beside the rest of
    /// ~/.local/share/claude. The install is readable and not writable; a canary beside it and the config folders are not.
    /// </summary>
    [Theory]
    [InlineData("payments", "ben", "grok")]
    [InlineData("payments", "ada", "claude-native")]
    public async Task AnInstallInsideTheHomeGrantsOnlyItself(string team, string member, string layout)
    {
        RealSandboxProxyMode.SkipUnlessAvailable();
        await using var kit = GuardrailKit.Create(realGateway: true);
        kit.InstallInHome(layout);
        var report = await kit.Check(new NativeSandboxBackend()).RunAsync(team, member, Ct);
        RealSandboxProxyMode.Report(report);
        AssertPassed(report);
        AssertCliInstallHolds(report, expectSibling: true);
        if (layout == "grok")
        {
            // ~/.grok listed from inside shows only the sandbox's mount points (downloads/ on the way to the grant, the masks).
            Assert.Contains("only the sandbox's mount points", report.Results.Single(r => r.Id == "cli.config-list:0").Evidence);
            Assert.Equal(Verdicts.Blocked, report.Results.Single(r => r.Id == "home.grok-auth").Verdict);
        }
        Assert.Empty(Directory.EnumerateFiles(kit.Root, ".yawble-guardcheck-*", SearchOption.AllDirectories));
    }

    /// <summary>A secret put where the agent can see it is found; the check fails (the scan is not a formality).</summary>
    [Fact]
    public async Task ASecretInsideTheSandboxIsFound()
    {
        RealSandboxProxyMode.SkipUnlessAvailable();
        await using var kit = GuardrailKit.Create(realGateway: true);
        var workspace = kit.Layout("payments", "ada", AgentCli.Claude).Workspace;
        Directory.CreateDirectory(Path.Combine(workspace, ".config"));
        File.WriteAllText(Path.Combine(workspace, ".config", "leak.txt"), FakeSecrets.SetupToken + "-padding-to-full-length-0000000");
        var report = await kit.Check(new NativeSandboxBackend()).RunAsync("payments", "ada", Ct);
        RealSandboxProxyMode.Report(report);

        var scan = report.Results.Single(r => r.Id == "secrets.scan");
        Assert.Equal(Verdicts.Allowed, scan.Verdict);
        Assert.Contains("leak.txt", scan.Evidence);
        Assert.DoesNotContain(FakeSecrets.SetupToken, scan.Evidence);
        Assert.False(report.Passed);
    }

    /// <summary>The check follows the member's effective policy: a confirmed loosen (registry.npmjs.org) is reachable for ben only.</summary>
    [Fact]
    public async Task ALoosenedHostIsAllowedForThatMemberOnly()
    {
        RealSandboxProxyMode.SkipUnlessAvailable();
        await using var kit = GuardrailKit.Create(realGateway: true);
        var store = new PolicyStore(kit.ConnectionString, canonicalize: HostPathCanonicalizer.Canonicalize);
        var ben = kit.MemberId("payments", "ben");
        var layout = kit.Layout("payments", "ben", AgentCli.Grok);
        var loosen = new PolicyPatch(Network: new NetworkPatch(["registry.npmjs.org"]));
        store.SaveLoosen(ben, loosen, layout, store.PreviewLoosen(ben, loosen, layout));

        var benReport = await kit.Check(new NativeSandboxBackend()).RunAsync("payments", "ben", Ct);
        RealSandboxProxyMode.Report(benReport);
        AssertPassed(benReport);
        Assert.NotEmpty(benReport.Loosened);
        Assert.Equal(Verdicts.Allowed, benReport.Results.Single(r => r.Id == "gateway.connect-allowed-host").Verdict);

        var adaReport = await kit.Check(new NativeSandboxBackend()).RunAsync("payments", "ada", Ct);
        AssertPassed(adaReport);
        Assert.DoesNotContain(adaReport.Results, r => r.Id == "gateway.connect-allowed-host");
        Assert.Empty(adaReport.Loosened);
    }
}
