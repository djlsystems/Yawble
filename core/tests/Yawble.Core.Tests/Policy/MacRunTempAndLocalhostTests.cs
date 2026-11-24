using System.Text.Json.Nodes;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Policy;

/// <summary>
/// A macOS run's own short temp folder and the host loopback only with the team's Allow localhost
/// (the request side). The compiler's output and the invariants that re-check it.
/// </summary>
public class MacRunTempAndLocalhostTests
{
    private static RunSpec Mac(CredentialMode mode = CredentialMode.ClaudeSubscription) => PolicyFixtures.Run(TargetOs.MacOS, mode);

    private static CompiledRun Compile(RunSpec run) =>
        SandboxRequestCompiler.Compile(PolicyFixtures.Global, PolicyFixtures.Override, null, run);

    private static List<string> Array(JsonObject r, string key) => r["filesystem"]![key]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    private static string RunTemp => PolicyFixtures.MacUserTemp + RunTempFolder.Name("run-0001");

    private static List<string> Problems(RunSpec run, Action<JsonObject> mutate)
    {
        var request = (JsonObject)Compile(run).Request.DeepClone();
        mutate(request);
        return [.. SandboxRequestInvariants.CheckRequest(request, run)];
    }

    private static void SetEnv(JsonObject r, string key, string value)
    {
        var env = r["process"]!["env"]!.AsArray();
        var i = env.Select(n => n!.GetValue<string>()).ToList().FindIndex(e => e.StartsWith(key + "=", StringComparison.Ordinal));
        env[i] = $"{key}={value}";
    }

    // ---- Part A: the run's own temp folder.

    [Theory]
    [InlineData(CredentialMode.ClaudeSubscription)]
    [InlineData(CredentialMode.GrokLogin)]
    public void AMacRunGetsItsOwnShortTempFolder(CredentialMode mode)
    {
        var compiled = Compile(Mac(mode));
        var env = Launch.LaunchKit.Env(compiled.Request);
        var l = Mac(mode).Layout;
        Assert.Equal(RunTemp, env["TMPDIR"]);
        Assert.Equal(RunTemp + "/zsh", env["TMPPREFIX"]);
        Assert.Single(Array(compiled.Request, "readwritePaths"), RunTemp);
        // The member's tmp stays granted; nothing else in the per-user temp folder is (never T/ itself).
        Assert.Contains(l.Tmp, Array(compiled.Request, "readwritePaths"));
        Assert.DoesNotContain(Array(compiled.Request, "readwritePaths").Concat(Array(compiled.Request, "readonlyPaths")),
            g => g != RunTemp && (g + "/").StartsWith("/private/var/folders", StringComparison.Ordinal));
        if (mode == CredentialMode.ClaudeSubscription) Assert.Equal(l.Tmp, env["CLAUDE_CODE_TMPDIR"]);
        Assert.True(RunTempFolder.Fits(env["TMPDIR"]));
    }

    [Fact]
    public void LinuxAndWindowsKeepTheMembersTmp()
    {
        var linux = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription);
        var env = Launch.LaunchKit.Env(Compile(linux).Request);
        Assert.Equal(linux.Layout.Tmp, env["TMPDIR"]);
        Assert.Equal(linux.Layout.Tmp + "/zsh", env["TMPPREFIX"]);
        Assert.Null(linux.RunTemp);
        var windows = PolicyFixtures.Run(TargetOs.Windows, CredentialMode.ClaudeSubscription);
        Assert.Null(windows.RunTemp);
        Assert.Equal(windows.Layout.Tmp, Launch.LaunchKit.Env(Compile(windows).Request)["TEMP"]);
    }

    [Fact]
    public void AnotherRunGetsAnotherFolder()
    {
        var other = Mac() with { RunId = "run-0002" };
        var env = Launch.LaunchKit.Env(Compile(other).Request);
        Assert.Equal(PolicyFixtures.MacUserTemp + RunTempFolder.Name("run-0002"), env["TMPDIR"]);
        Assert.DoesNotContain(RunTemp, Array(Compile(other).Request, "readwritePaths"));
    }

    [Fact]
    public void AMacRunWithoutThePerUserTempFolderIsRefused()
    {
        var run = Mac() with { Layout = Mac().Layout with { UserTempDir = null } };
        var e = Assert.Throws<ArgumentException>(() => Compile(run));
        Assert.Contains("DARWIN_USER_TEMP_DIR", e.Message);
    }

    [Fact]
    public void ATempFolderTooLongForAPipeIsRefused()
    {
        var run = Mac() with { Layout = Mac().Layout with { UserTempDir = "/private/var/folders/zz/zyxvpxvq6csfxvn_n0000000000000/T/longer/" } };
        var e = Assert.Throws<InvalidOperationException>(() => Compile(run));
        Assert.Contains("too long", e.Message);
    }

    [Fact]
    public void ThePerUserTempFolderItselfIsNeverGranted() =>
        Assert.Contains(Problems(Mac(), r => r["filesystem"]!["readwritePaths"]!.AsArray().Add(PolicyFixtures.MacUserTemp.TrimEnd('/'))),
            p => p.Contains("reaches the per-user temp folder"));

    [Fact]
    public void AnAncestorOfThePerUserTempFolderIsNeverGranted() =>
        Assert.Contains(Problems(Mac(), r => r["filesystem"]!["readonlyPaths"]!.AsArray().Add("/private/var/folders")),
            p => p.Contains("reaches the per-user temp folder"));

    [Fact]
    public void AnotherRunsFolderIsNeverGranted()
    {
        var other = PolicyFixtures.MacUserTemp + RunTempFolder.Name("run-0002");
        Assert.Contains(Problems(Mac(), r => r["filesystem"]!["readwritePaths"]!.AsArray().Add(other)), p => p.Contains("reaches the per-user temp folder"));
        // A run folder name anywhere else is another run's too.
        Assert.Contains(Problems(Mac(), r => r["filesystem"]!["readwritePaths"]!.AsArray().Add("/Users/alice/" + RunTempFolder.Name("run-0002"))),
            p => p.Contains("another run's temp folder"));
    }

    [Fact]
    public void TheRunsFolderMustBeGrantedReadWrite() =>
        Assert.Contains(Problems(Mac(), r =>
        {
            var rw = r["filesystem"]!["readwritePaths"]!.AsArray();
            rw.Remove(rw.First(n => n!.GetValue<string>() == RunTemp));
        }), p => p.Contains("readwritePaths must contain the run's temp folder"));

    [Fact]
    public void TmpdirAndTmpprefixMustPointIntoTheRunsFolder()
    {
        var l = Mac().Layout;
        Assert.Contains(Problems(Mac(), r => SetEnv(r, "TMPDIR", l.Tmp)), p => p.Contains("TMPDIR"));
        Assert.Contains(Problems(Mac(), r => SetEnv(r, "TMPPREFIX", l.Tmp + "/zsh")), p => p.Contains("TMPPREFIX"));
    }

    [Fact]
    public void TheFolderMustBeNamedForThisRun()
    {
        // The request of run-0002 checked as run-0001's: its folder is not this run's.
        var request = (JsonObject)Compile(Mac() with { RunId = "run-0002" }).Request.DeepClone();
        var problems = SandboxRequestInvariants.CheckRequest(request, Mac());
        Assert.Contains(problems, p => p.Contains("readwritePaths must contain the run's temp folder"));
        Assert.Contains(problems, p => p.Contains("reaches the per-user temp folder"));
        Assert.Contains(problems, p => p.Contains("TMPDIR"));
    }

    // ---- Part B: the host loopback only with the team's Allow localhost.

    private static JsonObject Ingress(RunSpec run) => Compile(run).Request["network"]!["ingress"]!.AsObject();

    [Fact]
    public void MacLoopbackIsClosedByDefault()
    {
        Assert.False(Mac().AllowLocalhost);
        var ingress = Ingress(Mac());
        Assert.Equal("deny", ingress["default"]!.GetValue<string>());
        Assert.Equal("deny", ingress["hostLoopback"]!.GetValue<string>());
    }

    [Fact]
    public void MacLoopbackOpensOnlyWithAllowLocalhost()
    {
        var compiled = Compile(Mac() with { AllowLocalhost = true });
        var network = compiled.Request["network"]!.AsObject();
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["default"] = "allow", ["hostLoopback"] = "allow" }, network["ingress"]));
        // Nothing else changes: egress deny with no rules, the run's gateway the only proxy.
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["default"] = "deny" }, network["egress"]));
        Assert.Equal($"http://127.0.0.1:{PolicyFixtures.Port}", compiled.Request["runtimeConfig"]!["networkProxy"]!.GetValue<string>());
        var off = (JsonObject)Compile(Mac()).Request.DeepClone();
        off["network"]!["ingress"] = new JsonObject { ["default"] = "allow", ["hostLoopback"] = "allow" };
        Assert.True(JsonNode.DeepEquals(off, compiled.Request));
    }

    [Theory]
    [InlineData(TargetOs.Windows, "allow")]
    [InlineData(TargetOs.Linux, "deny")]
    public void WindowsAndLinuxIgnoreAllowLocalhost(TargetOs os, string expected)
    {
        var run = PolicyFixtures.Run(os, CredentialMode.ClaudeSubscription);
        Assert.True(JsonNode.DeepEquals(Compile(run).Request, Compile(run with { AllowLocalhost = true }).Request));
        Assert.Equal(expected, Ingress(run)["hostLoopback"]!.GetValue<string>());
    }

    [Fact]
    public void TheInvariantsRefuseMacLoopbackWithoutAllowLocalhost()
    {
        var problems = Problems(Mac(), r => r["network"]!["ingress"] = new JsonObject { ["default"] = "allow", ["hostLoopback"] = "allow" });
        Assert.Contains(problems, p => p.Contains("only with the team's Allow localhost"));
        Assert.Contains(problems, p => p.Contains("network.ingress.default must be deny on MacOS"));
        Assert.Contains(Problems(Mac(), r => r["network"]!["ingress"]!["hostLoopback"] = "allow"), p => p.Contains("Allow localhost"));
        Assert.Contains(Problems(Mac(), r => r["network"]!["ingress"]!["default"] = "allow"), p => p.Contains("ingress.default must be deny"));
    }

    [Fact]
    public void TheInvariantsWantBothHalvesWithAllowLocalhost()
    {
        var on = Mac() with { AllowLocalhost = true };
        Assert.Empty(Problems(on, _ => { }));
        Assert.Contains(Problems(on, r => r["network"]!["ingress"]!["default"] = "deny"), p => p.Contains("Allow localhost needs"));
        Assert.Contains(Problems(on, r => r["network"]!["ingress"]!["hostLoopback"] = "deny"), p => p.Contains("Allow localhost needs"));
    }

    [Fact]
    public void AllowLocalhostNeverAddsEgressRulesOrOtherIngress()
    {
        var on = Mac() with { AllowLocalhost = true };
        Assert.Contains(Problems(on, r => r["network"]!["egress"]!["allow"] = new JsonArray(new JsonObject { ["to"] = new JsonObject { ["cidr"] = "127.0.0.1/32" } })),
            p => p.Contains("network.egress rules are never emitted"));
        Assert.Contains(Problems(on, r => r["network"]!["egress"]!["default"] = "allow"), p => p.Contains("network.egress.default must be deny"));
        Assert.Contains(Problems(on, r => r["network"]!["ingress"]!["ports"] = new JsonArray(8080)), p => p.Contains("network.ingress.ports is never emitted"));
    }

    // ---- Proof that Allow localhost is closed when off (SandboxRequestInvariants.MacLocalhostProblems).

    private static List<string> LocalhostProblems(RunSpec run, Action<JsonObject> mutate)
    {
        var request = (JsonObject)Compile(run).Request.DeepClone();
        mutate(request);
        return [.. SandboxRequestInvariants.MacLocalhostProblems(request, run)];
    }

    [Theory]
    [InlineData(CredentialMode.ClaudeSubscription, RunKind.Headless)]
    [InlineData(CredentialMode.GrokLogin, RunKind.Headless)]
    [InlineData(CredentialMode.GrokLogin, RunKind.Interactive)]
    [InlineData(CredentialMode.GrokHostSignIn, RunKind.Headless)]
    public void OffTheCompiledMacRequestHasNoIngressAllow(CredentialMode mode, RunKind kind)
    {
        var run = PolicyFixtures.Run(TargetOs.MacOS, mode, kind);
        Assert.False(run.AllowLocalhost);
        var request = Compile(run).Request;
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["default"] = "deny", ["hostLoopback"] = "deny" }, request["network"]!["ingress"]));
        Assert.Empty(SandboxRequestInvariants.MacLocalhostProblems(request, run));
        Assert.Empty(SandboxRequestInvariants.CheckRequest(request, run));
    }

    public static TheoryData<string> OffTampering => new() { "default-allow", "loopback-allow", "loopback-missing", "default-missing", "allow-rules", "rules", "both" };

    private static void Tamper(JsonObject r, string how)
    {
        var ingress = r["network"]!["ingress"]!.AsObject();
        switch (how)
        {
            case "default-allow": ingress["default"] = "allow"; break;
            case "loopback-allow": ingress["hostLoopback"] = "allow"; break;
            case "loopback-missing": ingress.Remove("hostLoopback"); break;
            case "default-missing": ingress.Remove("default"); break;
            case "allow-rules": ingress["allow"] = new JsonArray(new JsonObject { ["from"] = new JsonObject { ["cidr"] = "127.0.0.1/32" } }); break;
            case "rules": ingress["rules"] = new JsonArray(new JsonObject { ["port"] = 8080, ["action"] = "allow" }); break;
            case "both": ingress["default"] = "allow"; ingress["hostLoopback"] = "allow"; break;
        }
    }

    [Theory]
    [MemberData(nameof(OffTampering))]
    public void OffAnyIngressAllowIsRefused(string how)
    {
        Assert.NotEmpty(LocalhostProblems(Mac(), r => Tamper(r, how)));
        // And the full check that the compiler runs on every request refuses it too.
        Assert.Contains(Problems(Mac(), r => Tamper(r, how)), p => p.Contains("network.ingress"));
    }

    [Fact]
    public void OffEachHalfIsNamed()
    {
        Assert.Contains(LocalhostProblems(Mac(), r => Tamper(r, "loopback-allow")), p => p.Contains("hostLoopback") && p.Contains("Allow localhost"));
        Assert.Contains(LocalhostProblems(Mac(), r => Tamper(r, "default-allow")), p => p.Contains("ingress.default must be deny on MacOS"));
        Assert.Contains(LocalhostProblems(Mac(), r => Tamper(r, "allow-rules")), p => p.Contains("network.ingress.allow: no ingress allow rule"));
        Assert.Contains(Problems(Mac(), r => Tamper(r, "allow-rules")), p => p.Contains("ingress allow rules are never emitted"));
    }

    [Fact]
    public void OnTheCompiledMacRequestIsExactlyTheSdksPair()
    {
        var on = Mac() with { AllowLocalhost = true };
        var request = Compile(on).Request;
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["default"] = "allow", ["hostLoopback"] = "allow" }, request["network"]!["ingress"]));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["default"] = "deny" }, request["network"]!["egress"]));
        Assert.Empty(SandboxRequestInvariants.MacLocalhostProblems(request, on));
        // The off check refuses the on request: the pair is exactly what the setting adds.
        Assert.NotEmpty(SandboxRequestInvariants.MacLocalhostProblems(request, Mac()));
    }

    [Fact]
    public void OnEgressStaysDenyAndTheGatewayStaysTheOnlyProxy()
    {
        var on = Mac() with { AllowLocalhost = true };
        Assert.Contains(LocalhostProblems(on, r => r["network"]!["egress"]!["default"] = "allow"), p => p.Contains("keeps network.egress deny"));
        Assert.Contains(LocalhostProblems(on, r => r["network"]!["egress"]!["allow"] = new JsonArray(new JsonObject { ["to"] = new JsonObject { ["cidr"] = "0.0.0.0/0" } })),
            p => p.Contains("keeps network.egress deny"));
        Assert.Contains(LocalhostProblems(on, r => r["network"]!["egress"]!["deny"] = new JsonArray()), p => p.Contains("keeps network.egress deny"));
        Assert.Contains(LocalhostProblems(on, r => r["runtimeConfig"]!["networkProxy"] = "http://127.0.0.1:1"), p => p.Contains("gateway as the only proxy"));
        Assert.Contains(LocalhostProblems(on, r => r["runtimeConfig"]!.AsObject().Remove("networkProxy")), p => p.Contains("gateway as the only proxy"));
        Assert.Contains(LocalhostProblems(on, r => r["network"]!["ingress"]!["allow"] = new JsonArray()), p => p.Contains("exactly ingress.default and hostLoopback"));
        Assert.Contains(LocalhostProblems(on, r => r["network"]!["ingress"]!["hostLoopback"] = "deny"), p => p.Contains("Allow localhost needs"));
    }

    [Fact]
    public void LinuxNeverOpensTheLoopbackEvenWithAllowLocalhost() =>
        Assert.Contains(Problems(PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription) with { AllowLocalhost = true },
            r => r["network"]!["ingress"] = new JsonObject { ["default"] = "allow", ["hostLoopback"] = "allow" }),
            p => p.Contains("hostLoopback: allow is never emitted on Linux"));
}
