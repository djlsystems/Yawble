using System.Text.Json.Nodes;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Policy;

/// <summary>
/// The never-emit list and the credential invariants. Two halves: (1) the compiler cannot
/// produce a forbidden option from any combination of OS, CLI, credential mode, run kind and policy; (2) the
/// invariant checker, which runs on every compiled request, catches each forbidden option if it ever appears.
/// </summary>
public class NeverEmitTests
{
    public static TheoryData<TargetOs, CredentialMode, RunKind> Combinations()
    {
        var data = new TheoryData<TargetOs, CredentialMode, RunKind>();
        foreach (var os in Enum.GetValues<TargetOs>())
            foreach (var mode in Enum.GetValues<CredentialMode>())
                foreach (var kind in Enum.GetValues<RunKind>())
                    data.Add(os, mode, kind);
        return data;
    }

    private static readonly (PolicyPatch? Override, PolicyPatch? Loosen)[] Policies =
    [
        (null, null),
        (PolicyFixtures.Override, null),
        (PolicyFixtures.Override, PolicyFixtures.Loosen),
        (PolicyPatch.Parse("""{"network":{"allowHosts":[]},"compute":{"wallTimeoutSec":60,"idleTimeoutSec":60},"ui":{"clipboard":"all"}}""", PatchKind.Override),
         PolicyPatch.Parse("""{"filesystem":{"readwrite":["~/work"],"readonly":["~/Documents"]},"compute":{"wallTimeoutSec":86400}}""", PatchKind.Loosen)),
    ];

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EveryCombinationCompilesWithinTheInvariants(TargetOs os, CredentialMode mode, RunKind kind)
    {
        foreach (var (o, l) in Policies)
        {
            var run = PolicyFixtures.Run(os, mode, kind) with { Model = "some-model:latest" };
            var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, o, l, run); // throws on any violation
            Assert.Empty(SandboxRequestInvariants.Check(compiled, run));
            var text = compiled.RequestJson;
            foreach (var forbidden in new[] { "--audit", "captureDenials", "learningMode", "profileOverride", "guiAccess", "HTTP_PROXY", "NO_PROXY", "ANTHROPIC_API_KEY", "apiKeyHelper", "GROK_OIDC" })
                Assert.DoesNotContain(forbidden, text);
        }
    }

    // ---- The checker catches each forbidden option (mutation tests on valid requests).

    private static (CompiledRun Compiled, RunSpec Run) Valid(TargetOs os, CredentialMode mode = CredentialMode.ClaudeSubscription)
    {
        var run = PolicyFixtures.Run(os, mode);
        return (SandboxRequestCompiler.Compile(PolicyFixtures.Global, PolicyFixtures.Override, null, run), run);
    }

    private static void AssertCaught(TargetOs os, Action<JsonObject> mutate, string expected, CredentialMode mode = CredentialMode.ClaudeSubscription)
    {
        var (compiled, run) = Valid(os, mode);
        var request = (JsonObject)compiled.Request.DeepClone();
        mutate(request);
        var problems = SandboxRequestInvariants.CheckRequest(request, run);
        Assert.Contains(problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonArray Env(JsonObject r) => (JsonArray)r["process"]!["env"]!;

    [Fact]
    public void CatchesCaptureDenialsAllow() =>
        AssertCaught(TargetOs.Windows, r => r["processContainer"] = new JsonObject { ["captureDenials"] = new JsonObject { ["mode"] = "allow" } }, "processContainer");

    [Fact]
    public void CatchesSeatbeltProfileOverride() =>
        AssertCaught(TargetOs.MacOS, r => r["seatbelt"]!["profileOverride"] = "(version 1)(allow default)", "profileOverride");

    [Fact]
    public void CatchesSeatbeltGuiAccess() =>
        AssertCaught(TargetOs.MacOS, r => r["seatbelt"]!["guiAccess"] = true, "guiAccess");

    [Fact]
    public void CatchesSeatbeltKeychainAccess() =>
        AssertCaught(TargetOs.MacOS, r => r["seatbelt"]!["keychainAccess"] = true, "keychainAccess");

    [Theory]
    [InlineData(TargetOs.Linux)]
    [InlineData(TargetOs.MacOS)]
    public void CatchesHostLoopbackAllowOffWindows(TargetOs os) =>
        AssertCaught(os, r => r["network"]!["ingress"]!["hostLoopback"] = "allow", "hostLoopback");

    [Fact]
    public void CatchesAnOmittedEnvOnWindows() =>
        AssertCaught(TargetOs.Windows, r => r["process"]!.AsObject().Remove("env"), "env must be explicit");

    [Fact]
    public void CatchesInheritDefaultEnv() =>
        AssertCaught(TargetOs.Windows, r => r["process"]!["inheritDefaultEnv"] = true, "inheritDefaultEnv");

    [Fact]
    public void CatchesWindowsEnvWithoutSystemRoot() =>
        AssertCaught(TargetOs.Windows, r => r["process"]!["env"] = new JsonArray(Env(r).Where(e => !e!.GetValue<string>().StartsWith("SYSTEMROOT=")).Select(e => (JsonNode?)e!.GetValue<string>()).ToArray()), "SYSTEMROOT");

    [Fact]
    public void CatchesDaclMutation() =>
        AssertCaught(TargetOs.Windows, r => r["fallback"]!["allowDaclMutation"] = true, "allowDaclMutation");

    [Fact]
    public void CatchesTelemetryOn() =>
        AssertCaught(TargetOs.Linux, r => r["telemetry"]!["enabled"] = true, "telemetry");

    [Fact]
    public void CatchesAnotherContract() =>
        AssertCaught(TargetOs.Linux, r => r["version"] = "1.1.0-alpha", "version");

    [Fact]
    public void CatchesEgressAllow() =>
        AssertCaught(TargetOs.Linux, r => r["network"]!["egress"]!["default"] = "allow", "egress.default");

    [Fact]
    public void CatchesDirectEgressRules() =>
        AssertCaught(TargetOs.Linux, r => r["network"]!["egress"]!["allow"] = new JsonArray(new JsonObject { ["to"] = new JsonArray(new JsonObject { ["cidr"] = "0.0.0.0/0" }) }), "egress rules");

    [Fact]
    public void CatchesAnotherProxy() =>
        AssertCaught(TargetOs.Linux, r => r["runtimeConfig"]!["networkProxy"] = "http://127.0.0.1:1", "networkProxy");

    [Fact]
    public void CatchesUiEnabledOrClipboard()
    {
        AssertCaught(TargetOs.MacOS, r => r["ui"]!["disable"] = false, "ui.disable");
        AssertCaught(TargetOs.MacOS, r => r["ui"]!["clipboard"] = "all", "clipboard");
        AssertCaught(TargetOs.Windows, r => r["ui"]!["injection"] = true, "injection");
    }

    [Fact]
    public void CatchesProxyVariablesInEnv() =>
        AssertCaught(TargetOs.Linux, r => Env(r).Add("NO_PROXY=*"), "NO_PROXY");

    [Fact]
    public void CatchesLifecycleAndOtherSections()
    {
        AssertCaught(TargetOs.Linux, r => r["lifecycle"] = new JsonObject { ["destroyOnExit"] = false }, "lifecycle");
        AssertCaught(TargetOs.Linux, r => r["wslc"] = new JsonObject(), "wslc");
    }

    [Theory]
    [InlineData("readwritePaths", "/home/alice/.local/share/yawble")]
    [InlineData("readonlyPaths", "/home/alice/.local/share/yawble/data/yawble.db")]
    [InlineData("readonlyPaths", "/home/alice/.local/share/yawble/grok-home")]
    [InlineData("readwritePaths", "/home/alice/.local/share/yawble/teams/alpha/members/dev2")]
    [InlineData("readonlyPaths", "/home/alice/.local/share/yawble/teams/beta/members/manager/repo")]
    [InlineData("readonlyPaths", "/home/alice")]
    public void CatchesAForbiddenGrant(string list, string path) =>
        AssertCaught(TargetOs.Linux, r => ((JsonArray)r["filesystem"]![list]!).Add(path), "grant");

    [Fact]
    public void CatchesAMissingImplicitDenial() =>
        AssertCaught(TargetOs.Linux, r => ((JsonArray)r["filesystem"]!["deniedPaths"]!).RemoveAt(0), "implicit denial");

    // ---- Credential invariants.

    [Fact]
    public void CatchesTwoClaudeCredentialTiers()
    {
        AssertCaught(TargetOs.Linux, r => Env(r).Add($"ANTHROPIC_AUTH_TOKEN={PolicyFixtures.Token}"), "two Claude credential tiers");
        AssertCaught(TargetOs.Linux, r => Env(r).Add($"ANTHROPIC_API_KEY={PolicyFixtures.Token}"), "ANTHROPIC_API_KEY");
    }

    [Fact]
    public void CatchesARealLookingCredential() =>
        AssertCaught(TargetOs.Linux, r =>
        {
            var env = Env(r);
            var i = env.Select(e => e!.GetValue<string>()).ToList().FindIndex(e => e.StartsWith("CLAUDE_CODE_OAUTH_TOKEN="));
            env[i] = "CLAUDE_CODE_OAUTH_TOKEN=sk-ant-oat01-real";
        }, "only the run token placeholder");

    [Fact]
    public void CatchesGrokLoginWithAnApiKey() =>
        AssertCaught(TargetOs.Linux, r => Env(r).Add($"XAI_API_KEY={PolicyFixtures.Token}"), "never sets XAI_API_KEY", CredentialMode.GrokLogin);

    [Fact]
    public void CatchesGrokWithoutAllThreeBaseUrls() =>
        AssertCaught(TargetOs.Linux, r =>
        {
            var env = Env(r);
            var i = env.Select(e => e!.GetValue<string>()).ToList().FindIndex(e => e.StartsWith("GROK_MODELS_BASE_URL="));
            env.RemoveAt(i);
        }, "all three base URLs", CredentialMode.GrokLogin);

    [Fact]
    public void CatchesGrokOidcVariables() =>
        AssertCaught(TargetOs.Linux, r => Env(r).Add("GROK_OIDC_CLIENT_ID=x"), "GROK_OIDC", CredentialMode.GrokLogin);

    [Fact]
    public void SeedsNeverHoldACredentialsFile()
    {
        var (compiled, run) = Valid(TargetOs.Linux);
        foreach (var bad in new[] { ".credentials.json", "auth.json" })
        {
            var tampered = compiled with { Seeds = [.. compiled.Seeds, new SeedFile($"/home/alice/.local/share/yawble/teams/alpha/members/dev1/home/.claude/{bad}", "{}")] };
            Assert.Contains(SandboxRequestInvariants.Check(tampered, run), p => p.Contains("credentials file"));
        }
        var helper = compiled with { Seeds = [new SeedFile("/x/settings.json", """{"apiKeyHelper":"cat key"}""")] };
        Assert.Contains(SandboxRequestInvariants.Check(helper, run), p => p.Contains("apiKeyHelper"));
        foreach (var mode in Enum.GetValues<CredentialMode>())
            foreach (var seed in AgentPresets.Seeds(PolicyFixtures.Layout(TargetOs.Windows, AgentPresets.CliOf(mode)), mode))
                Assert.DoesNotContain(Path.GetFileName(seed.Path.Replace('\\', '/')), new[] { ".credentials.json", "auth.json" });
    }

    [Fact]
    public void AVendorHostOnTheGatewayListIsCaught()
    {
        var (compiled, run) = Valid(TargetOs.Linux);
        var tampered = compiled with { Gateway = compiled.Gateway with { AllowHosts = ["api.anthropic.com"] } };
        Assert.Contains(SandboxRequestInvariants.Check(tampered, run), p => p.Contains("vendor"));
    }

    [Fact]
    public void TheHostGrokHomeIsAlwaysDeniedAndNeverGranted()
    {
        foreach (var os in Enum.GetValues<TargetOs>())
            foreach (var mode in Enum.GetValues<CredentialMode>())
            {
                var run = PolicyFixtures.Run(os, mode);
                var r = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, PolicyFixtures.Loosen, run).Request;
                var grokHome = run.Layout.HostGrokHome;
                Assert.Contains(grokHome, r["filesystem"]!["deniedPaths"]!.AsArray().Select(n => n!.GetValue<string>()));
                foreach (var list in new[] { "readonlyPaths", "readwritePaths" })
                    Assert.DoesNotContain(r["filesystem"]![list]!.AsArray(), n => n!.GetValue<string>().StartsWith(grokHome, StringComparison.OrdinalIgnoreCase));
            }
    }

    [Fact]
    public void TheWindowsExecutorNeverGetsAudit()
    {
        var args = WxcExec.Arguments(@"C:\Users\alice\AppData\Local\Yawble\data\runs\run-0001.json");
        Assert.Equal("--config", args[0]);
        Assert.DoesNotContain("--audit", args);
        Assert.DoesNotContain("--config-base64", args);
    }

    [Fact]
    public void ArgumentsCannotBreakOutOfTheirWord()
    {
        foreach (var arg in new[] { "a b", "it's", "$(id)", "`id`", "a\"b", @"c:\dir\", "x;rm -rf /", "" })
        {
            Assert.Equal(arg, PosixSplitOne(CommandLine.QuotePosix(arg)));
            Assert.Equal(arg, WindowsSplitOne(CommandLine.QuoteWindows(arg)));
        }
        Assert.Throws<ArgumentException>(() => SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null,
            PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeSubscription) with { Model = "x --dangerously-load" }));
    }

    /// <summary>Minimal sh word parser for one single-quoted or bare word.</summary>
    private static string PosixSplitOne(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\'') { var end = s.IndexOf('\'', i + 1); sb.Append(s, i + 1, end - i - 1); i = end; }
            else if (s[i] == '\\') { sb.Append(s[++i]); }
            else if (s[i] == ' ') throw new InvalidOperationException("split into two words: " + s);
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>CommandLineToArgvW rules for one argument.</summary>
    private static string WindowsSplitOne(string s)
    {
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < s.Length && s[i] == '\\') { n++; i++; }
                if (i < s.Length && s[i] == '"') { sb.Append('\\', n / 2); if (n % 2 == 1) sb.Append('"'); else { quoted = !quoted; } }
                else { sb.Append('\\', n); i--; }
                continue;
            }
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted) throw new InvalidOperationException("split into two words: " + s);
            sb.Append(c);
        }
        return sb.ToString();
    }
    /// <summary>
    /// An app data root reached through a symlink (macOS temp folders: /var -> /private/var) still compiles:
    /// HOME is the canonical workspace, like cwd and the grants (found on a macOS run, 42161466988).
    /// </summary>
    [Theory]
    [InlineData(TargetOs.Linux)]
    [InlineData(TargetOs.MacOS)]
    [InlineData(TargetOs.Windows)]
    public void ASymlinkedAppDataRootCompilesWithACanonicalHome(TargetOs os)
    {
        var run = PolicyFixtures.Run(os, os == TargetOs.Windows ? CredentialMode.ClaudeApiKey : CredentialMode.GrokLogin);
        var root = run.Layout.AppDataRoot;
        var real = os == TargetOs.Windows ? @"D:\real" : "/private/real";
        string Canon(string p) => p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? real + p[root.Length..] : p;

        var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, run, Canon);

        var env = compiled.Request["process"]!["env"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        var home = env.Single(e => e.StartsWith("HOME=", StringComparison.Ordinal))["HOME=".Length..];
        Assert.StartsWith(real, home, StringComparison.Ordinal);
        Assert.StartsWith(real, compiled.Request["process"]!["cwd"]!.GetValue<string>(), StringComparison.Ordinal);
        if (os == TargetOs.Windows) Assert.Contains("USERPROFILE=" + home, env);
    }
}
