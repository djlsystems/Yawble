using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.SandboxCheck;
using Xunit;

namespace Yawble.Core.Tests.Policy;

/// <summary>
/// Golden files per OS and per CLI. Each case compiles the same policy for a
/// fictional machine, compares the request and the launch plan with the committed files, and submits the
/// committed request file, byte for byte, to the real mxc_ffi native library.
/// Regenerate after an intended change: YAWBLE_UPDATE_GOLDEN=1 dotnet test --project core/tests/Yawble.Core.Tests
/// </summary>
public class GoldenRequestTests
{
    public static TheoryData<string> Cases => new(GoldenCases.All.Keys);

    [Theory]
    [MemberData(nameof(Cases))]
    public void CompilesToTheGoldenRequest(string name)
    {
        var compiled = GoldenCases.Compile(name);
        var request = compiled.Request.ToJsonString(GoldenCases.Pretty) + "\n";
        var launch = compiled.LaunchJson().ToJsonString(GoldenCases.Pretty) + "\n";

        if (Environment.GetEnvironmentVariable("YAWBLE_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenCases.SourceDir);
            File.WriteAllText(Path.Combine(GoldenCases.SourceDir, name + ".request.json"), request);
            File.WriteAllText(Path.Combine(GoldenCases.SourceDir, name + ".launch.json"), launch);
        }

        Assert.Equal(GoldenCases.Read(name + ".request.json"), request);
        Assert.Equal(GoldenCases.Read(name + ".launch.json"), launch);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SandboxAcceptsTheGoldenFile(string name)
    {
        var json = GoldenCases.Read(name + ".request.json");
        var result = SandboxRequestValidator.Validate(json);
        var line = $"{name}: {result.Level} (status {result.Status}): {result.Detail}";
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        // For the per-OS record: YAWBLE_VALIDATION_REPORT=<file> appends one line per golden file.
        if (Environment.GetEnvironmentVariable("YAWBLE_VALIDATION_REPORT") is { Length: > 0 } report)
            lock (GoldenCases.All)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                File.AppendAllText(report, $"{TargetOsExtensions.Host.Slug()} {line}\n");
            }
        Assert.True(result.Accepted, $"{name}: the sandbox rejected the golden request: {result.Detail}");
    }

    [Fact]
    public void ThereIsAGoldenFileForEveryOsAndCli()
    {
        foreach (var os in Enum.GetValues<TargetOs>())
            foreach (var cli in new[] { "claude", "grok" })
                Assert.Contains($"{os.Slug()}-{cli}", GoldenCases.All.Keys);
        var onDisk = Directory.GetFiles(GoldenCases.BuildDir, "*.request.json").Select(f => Path.GetFileName(f)[..^".request.json".Length]);
        Assert.Equal(GoldenCases.All.Keys.Order(), onDisk.Order());
    }

    /// <summary>
    /// Card 536 (B119): Allow localhost is closed when off. In the committed files, every macOS golden except
    /// macos-claude-localhost has ingress deny and hostLoopback deny and no ingress rule; macos-claude-localhost has exactly the
    /// SDK's pair, egress deny, and differs from its off twin macos-claude in network.ingress only.
    /// </summary>
    [Fact]
    public void OnlyTheLocalhostGoldenOpensTheMacLoopback()
    {
        var macs = GoldenCases.All.Where(c => c.Value.Os == TargetOs.MacOS).Select(c => c.Key).ToList();
        Assert.Contains("macos-claude-localhost", macs);
        Assert.Equal(["macos-claude-localhost"], GoldenCases.AllowLocalhost.Order());
        JsonObject Golden(string name) => JsonNode.Parse(GoldenCases.Read(name + ".request.json"))!.AsObject();
        var closed = new JsonObject { ["default"] = "deny", ["hostLoopback"] = "deny" };
        var open = new JsonObject { ["default"] = "allow", ["hostLoopback"] = "allow" };
        foreach (var name in macs)
        {
            var request = Golden(name);
            var c = GoldenCases.All[name];
            var run = PolicyFixtures.Run(c.Os, c.Mode, c.Kind, c.Install) with { AllowLocalhost = GoldenCases.AllowLocalhost.Contains(name) };
            Assert.True(JsonNode.DeepEquals(run.AllowLocalhost ? open : closed, request["network"]!["ingress"]),
                $"{name}: network.ingress is {request["network"]!["ingress"]!.ToJsonString()}");
            Assert.True(JsonNode.DeepEquals(new JsonObject { ["default"] = "deny" }, request["network"]!["egress"]), $"{name}: egress");
            Assert.Empty(SandboxRequestInvariants.MacLocalhostProblems(request, run));
            // The file read as an off run: only the localhost golden is refused.
            Assert.Equal(run.AllowLocalhost, SandboxRequestInvariants.MacLocalhostProblems(request, run with { AllowLocalhost = false }).Any());
        }
        var on = Golden("macos-claude-localhost");
        var off = Golden("macos-claude");
        on["network"]!["ingress"] = closed.DeepClone();
        Assert.True(JsonNode.DeepEquals(off, on), "macos-claude-localhost differs from macos-claude outside network.ingress");
    }

    /// <summary>The control: the sandbox's parser must still reject a golden file with one forbidden change.</summary>
    [Fact]
    public void SandboxRejectsAGoldenFileWithAnUnknownField()
    {
        var request = JsonNode.Parse(GoldenCases.Read("linux-claude.request.json"))!.AsObject();
        request["process"]!.AsObject()["shell"] = true;
        var result = SandboxRequestValidator.Validate(request.ToJsonString());
        Assert.Equal(ValidationLevel.Rejected, result.Level);
        var windows = JsonNode.Parse(GoldenCases.Read("windows-claude.request.json"))!.AsObject();
        windows["network"]!["ingress"]!.AsObject()["hostLoopback"] = "deny"; // identity-less proxy needs allow
        Assert.Equal(ValidationLevel.Rejected, SandboxRequestValidator.Validate(windows.ToJsonString()).Level);
    }
}

internal static class GoldenCases
{
    // NewLine pinned: it defaults to Environment.NewLine, so on Windows the compiled text came out CRLF against LF goldens.
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>OS x CLI x install layout (PolicyFixtures.Cli): the default layout is Anthropic's native installer for
    /// Claude and xAI's installer for Grok; the suffixed cases are the other ways a person installs Claude.</summary>
    public static readonly Dictionary<string, (TargetOs Os, CredentialMode Mode, RunKind Kind, bool Loosened, string? Install)> All = new()
    {
        ["windows-claude"] = (TargetOs.Windows, CredentialMode.ClaudeSubscription, RunKind.Headless, false, null),
        ["windows-claude-npm"] = (TargetOs.Windows, CredentialMode.ClaudeSubscription, RunKind.Headless, false, "npm"),
        ["windows-claude-npm-node"] = (TargetOs.Windows, CredentialMode.ClaudeSubscription, RunKind.Headless, false, "npm-node"),
        ["windows-grok"] = (TargetOs.Windows, CredentialMode.GrokLogin, RunKind.Headless, false, null),
        ["macos-claude"] = (TargetOs.MacOS, CredentialMode.ClaudeSubscription, RunKind.Headless, false, null),
        ["macos-claude-homebrew"] = (TargetOs.MacOS, CredentialMode.ClaudeSubscription, RunKind.Headless, false, "homebrew"),
        ["macos-claude-npm-node"] = (TargetOs.MacOS, CredentialMode.ClaudeSubscription, RunKind.Headless, false, "npm-node"),
        ["macos-grok"] = (TargetOs.MacOS, CredentialMode.GrokLogin, RunKind.Headless, false, null),
        ["linux-claude"] = (TargetOs.Linux, CredentialMode.ClaudeSubscription, RunKind.Headless, false, null),
        ["linux-claude-npm"] = (TargetOs.Linux, CredentialMode.ClaudeSubscription, RunKind.Headless, false, "npm"),
        ["linux-claude-npm-node"] = (TargetOs.Linux, CredentialMode.ClaudeSubscription, RunKind.Headless, false, "npm-node"),
        ["linux-grok"] = (TargetOs.Linux, CredentialMode.GrokLogin, RunKind.Headless, false, null),
        ["linux-claude-apikey"] = (TargetOs.Linux, CredentialMode.ClaudeApiKey, RunKind.Headless, false, null),
        ["linux-grok-apikey"] = (TargetOs.Linux, CredentialMode.GrokApiKey, RunKind.Headless, false, null),
        ["linux-grok-loosened"] = (TargetOs.Linux, CredentialMode.GrokLogin, RunKind.Headless, true, null),
        ["windows-claude-interactive"] = (TargetOs.Windows, CredentialMode.ClaudeSubscription, RunKind.Interactive, false, null),
        ["macos-grok-interactive"] = (TargetOs.MacOS, CredentialMode.GrokLogin, RunKind.Interactive, false, null),
        // Host sign-in: the CLI's own sign-in on the host, the exact file granted read-only.
        // macOS Claude has no case: its sign-in is in the login Keychain, held by the gateway (no grant; the invariants refuse one).
        ["linux-claude-signin"] = (TargetOs.Linux, CredentialMode.ClaudeHostSignIn, RunKind.Headless, false, null),
        ["linux-claude-npm-signin"] = (TargetOs.Linux, CredentialMode.ClaudeHostSignIn, RunKind.Headless, false, "npm"),
        ["linux-grok-signin"] = (TargetOs.Linux, CredentialMode.GrokHostSignIn, RunKind.Headless, false, null),
        ["windows-claude-signin"] = (TargetOs.Windows, CredentialMode.ClaudeHostSignIn, RunKind.Headless, false, null),
        ["windows-grok-signin"] = (TargetOs.Windows, CredentialMode.GrokHostSignIn, RunKind.Headless, false, null),
        ["macos-grok-signin"] = (TargetOs.MacOS, CredentialMode.GrokHostSignIn, RunKind.Headless, false, null),
        // The team's "Allow localhost (macOS)" on: the same run as macos-claude with the host's loopback open.
        ["macos-claude-localhost"] = (TargetOs.MacOS, CredentialMode.ClaudeSubscription, RunKind.Headless, false, null),
    };

    /// <summary>The cases compiled with the team's Allow localhost on (RunSpec.AllowLocalhost); every other case has it off.</summary>
    public static readonly HashSet<string> AllowLocalhost = ["macos-claude-localhost"];

    public static CompiledRun Compile(string name)
    {
        var c = All[name];
        return SandboxRequestCompiler.Compile(PolicyFixtures.Global, PolicyFixtures.Override, c.Loosened ? PolicyFixtures.Loosen : null,
            PolicyFixtures.Run(c.Os, c.Mode, c.Kind, c.Install) with { AllowLocalhost = AllowLocalhost.Contains(name) });
    }

    public static string BuildDir => Path.Combine(AppContext.BaseDirectory, "Golden");

    public static string SourceDir =>
        Path.Combine(ReleaseGate.FindRepoRoot(AppContext.BaseDirectory)!, "core", "tests", "Yawble.Core.Tests", "Golden");

    /// <summary>Reads the committed file (the source tree when updating, else the copy beside the tests).</summary>
    public static string Read(string file)
    {
        var dir = Environment.GetEnvironmentVariable("YAWBLE_UPDATE_GOLDEN") == "1" ? SourceDir : BuildDir;
        return File.ReadAllText(Path.Combine(dir, file)).ReplaceLineEndings("\n");
    }
}
