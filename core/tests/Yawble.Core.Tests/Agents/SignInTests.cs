using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Agents;
using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Launch;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// Host sign-in: each sandbox reads the CLI's own existing sign-in on the host. Exactly the sign-in
/// file is granted, read-only (one setting per file, SignInCatalog); the rest of the CLI's config folder stays blocked;
/// the CLI is pointed at the host file by its own variable; whether the person is signed in comes from presence only.
/// Every file here is fake; no real sign-in is read.
/// </summary>
public class SignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ catalog (detection)

    [Fact]
    public void ClaudeOnLinuxAndWindowsIsTheCredentialsFileThroughItsStoreVariable()
    {
        var linux = new FakeHost(TargetOs.Linux, "/home/alice").File("/home/alice/.claude/.credentials.json", "{}");
        var s = SignInCatalog.Claude(linux.Host());
        Assert.Equal(SignInStatus.SignedIn, s.Status);
        var f = Assert.Single(s.Files);
        Assert.Equal(("/home/alice/.claude/.credentials.json", SignInAccess.ReadOnly, true), (f.Path, f.Access, f.Present));
        Assert.Equal("/home/alice/.claude", s.Environment["CLAUDE_SECURESTORAGE_CONFIG_DIR"]);
        Assert.Empty(linux.Reads); // presence only: never opened

        var windows = new FakeHost(TargetOs.Windows, @"C:\Users\alice");
        var w = SignInCatalog.Claude(windows.Host());
        Assert.Equal(SignInStatus.NotSignedIn, w.Status);
        Assert.Equal(@"C:\Users\alice\.claude\.credentials.json", Assert.Single(w.Files).Path);
        Assert.Contains("claude", w.Detail);
    }

    [Fact]
    public void ClaudeFollowsClaudeConfigDir()
    {
        var host = new FakeHost(TargetOs.Linux, "/home/alice").File("/srv/claude-cfg/.credentials.json", "{}");
        host.Vars["CLAUDE_CONFIG_DIR"] = "/srv/claude-cfg";
        var s = SignInCatalog.Claude(host.Host());
        Assert.Equal(SignInStatus.SignedIn, s.Status);
        Assert.Equal("/srv/claude-cfg", s.Environment["CLAUDE_SECURESTORAGE_CONFIG_DIR"]);
    }

    [Fact]
    public void ClaudeOnMacOsIsPendingNotSilentlyMissing()
    {
        // Even a fallback file on disk is not used: macOS Claude keeps its sign-in in the Keychain, which the app does not read.
        var host = new FakeHost(TargetOs.MacOS, "/Users/alice").File("/Users/alice/.claude/.credentials.json", "{}");
        var s = SignInCatalog.Claude(host.Host());
        Assert.Equal(SignInStatus.Pending, s.Status);
        Assert.Empty(s.Files);
        Assert.Contains("Keychain", s.Detail);
    }

    [Fact]
    public void GrokIsAuthJsonThroughGrokAuthPathOnEveryOs()
    {
        foreach (var (os, home, file) in new[]
                 {
                     (TargetOs.Linux, "/home/alice", "/home/alice/.grok/auth.json"),
                     (TargetOs.MacOS, "/Users/alice", "/Users/alice/.grok/auth.json"),
                     (TargetOs.Windows, @"C:\Users\alice", @"C:\Users\alice\.grok\auth.json"),
                 })
        {
            var s = SignInCatalog.Grok(new FakeHost(os, home).File(file, "{}").Host());
            Assert.Equal(SignInStatus.SignedIn, s.Status);
            Assert.Equal(file, s.Environment["GROK_AUTH_PATH"]);
            Assert.Equal(SignInAccess.ReadOnly, Assert.Single(s.Files).Access);
        }
    }

    [Fact]
    public async Task DetectionReportsTheSignInForTheAgentsDialog()
    {
        var host = new FakeHost(TargetOs.Linux, "/home/alice")
            .File("/home/alice/.grok/downloads/grok-1.0.77-linux-x86_64", FakeHost.Elf)
            .Link("/home/alice/.grok/bin/grok", "../downloads/grok-1.0.77-linux-x86_64")
            .File("/home/alice/.grok/auth.json", "{}")
            .OnPath("/home/alice/.grok/bin");
        host.VersionOutput = "grok 1.0.77 (abc)";
        var grok = await new GrokDetector().DetectAsync(host.Host(), Ct);
        Assert.True(grok.Found);
        Assert.Equal(SignInStatus.SignedIn, grok.SignIn!.Status);
        var claude = await new ClaudeDetector().DetectAsync(host.Host(), Ct);
        Assert.False(claude.Found);
        Assert.Equal(SignInStatus.NotSignedIn, claude.SignIn!.Status); // shown even when not installed
        Assert.DoesNotContain(host.Reads, r => r.EndsWith("auth.json") || r.EndsWith(".credentials.json"));
    }

    // ------------------------------------------------------------------ grants

    private static readonly string Exe = "/home/alice/.grok/downloads/grok-1.0.77-linux-x86_64";

    private static DetectedCli GrokSignedIn(string authPath = "/home/alice/.grok/auth.json", string? envPath = null) =>
        TestClis.Found("grok", Exe, ["/home/alice/.grok/downloads"], ["/home/alice/.grok"]) with
        {
            SignIn = new CliSignIn(SignInStatus.SignedIn, "signed in", [new CliSignInFile(authPath, SignInAccess.ReadOnly, true, "Grok")],
                new Dictionary<string, string> { ["GROK_AUTH_PATH"] = envPath ?? authPath }),
        };

    [Fact]
    public void OnlyTheSignInFileIsGrantedAndEveryOtherEntryOfTheConfigFolderIsDenied()
    {
        var install = CliInstall.FromDetected(TargetOs.Linux, "/home/alice", GrokSignedIn(),
            new FakeFolders(TargetOs.Linux, Exe, "/home/alice/.grok/auth.json", "/home/alice/.grok/auth.json.lock", "/home/alice/.grok/config.toml",
                "/home/alice/.grok/sessions/s.jsonl"));
        Assert.Equal(["/home/alice/.grok/auth.json"], install.SignInReadOnly);
        Assert.Empty(install.SignInReadWrite);
        Assert.Equal(["/home/alice/.grok/auth.json.lock", "/home/alice/.grok/config.toml", "/home/alice/.grok/sessions"], install.ConfigDenied);
    }

    [Fact]
    public void ClaudesSignInFileLeavesTheRestOfDotClaudeDenied()
    {
        var exe = "/home/alice/.local/share/claude/versions/2.1.318";
        var cli = TestClis.Found("claude", exe, ["/home/alice/.local/share/claude/versions"], ["/home/alice/.claude", "/home/alice/.claude.json"]) with
        {
            SignIn = new CliSignIn(SignInStatus.SignedIn, "", [new CliSignInFile("/home/alice/.claude/.credentials.json", SignInAccess.ReadOnly, true, "")],
                new Dictionary<string, string> { ["CLAUDE_SECURESTORAGE_CONFIG_DIR"] = "/home/alice/.claude" }),
        };
        var install = CliInstall.FromDetected(TargetOs.Linux, "/home/alice", cli, new FakeFolders(TargetOs.Linux, exe,
            "/home/alice/.claude/.credentials.json", "/home/alice/.claude/history.jsonl", "/home/alice/.claude/projects/p/s.jsonl",
            "/home/alice/.claude/settings.json", "/home/alice/.claude.json"));
        Assert.Equal(["/home/alice/.claude/history.jsonl", "/home/alice/.claude/projects", "/home/alice/.claude/settings.json", "/home/alice/.claude.json"],
            install.ConfigDenied);
    }

    [Fact]
    public void ASignInThatIsNotPresentIsNotGrantedAndTheConfigFolderIsDeniedWhole()
    {
        var cli = GrokSignedIn() with
        {
            SignIn = new CliSignIn(SignInStatus.NotSignedIn, "", [new CliSignInFile("/home/alice/.grok/auth.json", SignInAccess.ReadOnly, false, "")],
                new Dictionary<string, string> { ["GROK_AUTH_PATH"] = "/home/alice/.grok/auth.json" }),
        };
        var install = CliInstall.FromDetected(TargetOs.Linux, "/home/alice", cli, new FakeFolders(TargetOs.Linux, Exe, "/home/alice/.grok/config.toml"));
        Assert.Empty(install.SignInReadOnly);
        Assert.DoesNotContain("/home/alice/.grok/auth.json", install.ConfigDenied); // absent: nothing to deny or grant
        Assert.Contains("/home/alice/.grok/config.toml", install.ConfigDenied);
    }

    [Theory]
    [InlineData("/home/alice/.ssh/id_rsa", null, "not inside the CLI's own config folder")]
    [InlineData("/home/alice/.grok", null, "not inside the CLI's own config folder")]
    [InlineData("/home/alice/.grok/auth.json", "/home/alice/.ssh", "not a sign-in file or its folder")]
    [InlineData("/home/alice/.grok/downloads/auth.json", null, "overlaps an install folder")]
    public void ASignInGrantThatIsNotTheClisOwnFileIsRefused(string file, string? env, string why)
    {
        var e = Assert.Throws<ArgumentException>(() => CliInstall.FromDetected(TargetOs.Linux, "/home/alice", GrokSignedIn(file, env),
            new FakeFolders(TargetOs.Linux, Exe, file)));
        Assert.Contains(why, e.Message);
    }

    [Fact]
    public void NoPolicyCanMakeAReadOnlySignInWritable()
    {
        var run = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.ClaudeHostSignIn);
        var patch = PolicyPatch.Parse("""{ "filesystem": { "readwrite": ["~/.claude/.credentials.json"] } }""", PatchKind.Loosen);
        var e = Assert.ThrowsAny<Exception>(() => SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, patch, run));
        Assert.Contains("sign-in file", e.Message);
    }

    [Fact]
    public void TheInvariantsRefuseACredentialVariableOrAMissingPointerInHostSignInMode()
    {
        var run = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.GrokHostSignIn);
        var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, PolicyFixtures.Override, null, run);
        var env = compiled.Request["process"]!["env"]!.AsArray().Select(n => n!.GetValue<string>()).ToDictionary(e => e[..e.IndexOf('=')], e => e[(e.IndexOf('=') + 1)..]);
        Assert.Empty(SandboxRequestInvariants.CheckCredentials(env, run));

        var withKey = new Dictionary<string, string>(env) { ["XAI_API_KEY"] = run.RunToken };
        Assert.Contains(SandboxRequestInvariants.CheckCredentials(withKey, run), p => p.Contains("never sets XAI_API_KEY"));
        var noPointer = new Dictionary<string, string>(env);
        noPointer.Remove("GROK_AUTH_PATH");
        Assert.Contains(SandboxRequestInvariants.CheckCredentials(noPointer, run), p => p.Contains("GROK_AUTH_PATH"));
        // And the pointer never appears in another mode.
        var old = PolicyFixtures.Run(TargetOs.Linux, CredentialMode.GrokLogin);
        var oldEnv = SandboxRequestCompiler.Compile(PolicyFixtures.Global, PolicyFixtures.Override, null, old).Request["process"]!["env"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToDictionary(e => e[..e.IndexOf('=')], e => e[(e.IndexOf('=') + 1)..]);
        oldEnv["GROK_AUTH_PATH"] = "/home/alice/.grok/auth.json";
        Assert.Contains(SandboxRequestInvariants.CheckCredentials(oldEnv, old), p => p.Contains("only in host sign-in mode"));
    }

    // ------------------------------------------------------------------ the launcher

    private static FakeCliDetector Clis(string root, string home, Func<DetectedCli, DetectedCli>? claude = null, Func<DetectedCli, DetectedCli>? grok = null)
    {
        var c = TestClis.StandIn(root, home, "claude");
        var g = TestClis.StandIn(root, home, "grok");
        return new FakeCliDetector(claude?.Invoke(c) ?? c, grok?.Invoke(g) ?? g);
    }

    private static CliSignIn FileSignIn(string file, string variable, string value) =>
        new(File.Exists(file) ? SignInStatus.SignedIn : SignInStatus.NotSignedIn, File.Exists(file) ? "signed in" : "Not signed in: run 'claude'",
            [new CliSignInFile(file, SignInAccess.ReadOnly, File.Exists(file), "test")], new Dictionary<string, string> { [variable] = value });

    /// <summary>
    /// Claude's sign-in is held by the gateway: the member must be signed in on this
    /// computer, but its run is granted no sign-in file and no variable pointing at one; it presents only the run token.
    /// </summary>
    [Fact]
    public async Task ASignedInClaudeMemberRuns_WithTheRunTokenOnly_AndItsSignInIsNeverGranted()
    {
        var db = new TempDb();
        var root = HostPathCanonicalizer.Canonicalize(Path.Combine(db.Dir, "machine"));
        var home = Path.Combine(root, "home");
        var claudeDir = Path.Combine(home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var creds = Path.Combine(claudeDir, ".credentials.json");
        File.WriteAllText(creds, "{\"claudeAiOauth\":{\"accessToken\":\"FAKE\"}}");
        File.WriteAllText(Path.Combine(claudeDir, "history.jsonl"), "FAKE history");
        var clis = Clis(root, home, claude: c => c with { SignIn = FileSignIn(creds, "CLAUDE_SECURESTORAGE_CONFIG_DIR", claudeDir) });
        await using var kit = await LaunchKit.StartAsync(db: db, clis: clis, credentials: new HostSignInModes());
        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };

        await kit.Bus.PersonTellAsync("alpha", "dev1", "hello", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev1");
        Assert.Equal(MessageTypes.Completed, outcome.Type);
        var request = JsonNode.Parse(kit.Backend.Requests.First(r => LaunchKit.IsFor(r.Json, "dev1")).Json)!;
        List<string> L(string k) => request["filesystem"]![k]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.DoesNotContain(L("readonlyPaths"), g => g.StartsWith(claudeDir, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(L("readwritePaths"), g => g.StartsWith(claudeDir, StringComparison.OrdinalIgnoreCase));
        var env = request["process"]!["env"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.DoesNotContain(env, e => e.StartsWith("CLAUDE_SECURESTORAGE_CONFIG_DIR="));
        Assert.Contains(env, e => e.StartsWith("CLAUDE_CODE_OAUTH_TOKEN=yawrt_"));
        Assert.DoesNotContain(env, e => e.Contains("FAKE"));

        // Not signed in on this computer: the run fails at once with the reason, as before.
        File.Delete(creds);
    }

    /// <summary>
    /// Grok's sign-in is held by the gateway too: the member's grok is signed in on this computer, but its
    /// run is granted no sign-in file and no GROK_AUTH_PATH; it gets yawble-grok-auth (the placeholder) only.
    /// </summary>
    [Fact]
    public async Task ASignedInGrokMemberRuns_WithThePlaceholderOnly_AndItsSignInIsNeverGranted()
    {
        var db = new TempDb();
        var root = HostPathCanonicalizer.Canonicalize(Path.Combine(db.Dir, "machine"));
        var home = Path.Combine(root, "home");
        var grokDir = Path.Combine(home, ".grok");
        Directory.CreateDirectory(grokDir);
        var auth = Path.Combine(grokDir, "auth.json");
        File.WriteAllText(auth, "{\"https://auth.x.ai\":{\"key\":\"FAKE\"}}");
        var clis = Clis(root, home, grok: g => g with { SignIn = FileSignIn(auth, "GROK_AUTH_PATH", auth) });
        await using var kit = await LaunchKit.StartAsync(db: db, clis: clis, credentials: new HostSignInModes());
        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };

        await kit.Bus.PersonTellAsync("alpha", "dev2", "hello", ct: Ct);
        var outcome = await kit.OutcomeAsync("dev2");
        Assert.Equal(MessageTypes.Completed, outcome.Type);
        var request = JsonNode.Parse(kit.Backend.Requests.First(r => LaunchKit.IsFor(r.Json, "dev2")).Json)!;
        List<string> L(string k) => request["filesystem"]![k]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.DoesNotContain(L("readonlyPaths"), g => g.StartsWith(grokDir, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(L("readwritePaths"), g => g.StartsWith(grokDir, StringComparison.OrdinalIgnoreCase));
        var env = request["process"]!["env"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.DoesNotContain(env, e => e.StartsWith("GROK_AUTH_PATH="));
        Assert.Contains(env, e => e.StartsWith("GROK_AUTH_PROVIDER_COMMAND=") && e.Contains("yawble-grok-auth"));
        Assert.DoesNotContain(env, e => e.Contains("FAKE"));
    }

    [Fact]
    public async Task AMemberWhoseCliIsNotSignedInOrPendingFailsAtOnceWithTheReason()
    {
        var db = new TempDb();
        var root = HostPathCanonicalizer.Canonicalize(Path.Combine(db.Dir, "machine"));
        var home = Path.Combine(root, "home");
        var auth = Path.Combine(home, ".grok", "auth.json"); // never created: grok is not signed in
        var clis = Clis(root, home,
            claude: c => c with { SignIn = new CliSignIn(SignInStatus.Pending, SignInCatalog.MacClaudePending, [], new Dictionary<string, string>()) },
            grok: g => g with { SignIn = FileSignIn(auth, "GROK_AUTH_PATH", auth) });
        await using var kit = await LaunchKit.StartAsync(db: db, clis: clis, credentials: new HostSignInModes());
        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };

        await kit.Bus.PersonTellAsync("alpha", "dev1", "hello", ct: Ct); // claude
        var claude = await kit.OutcomeAsync("dev1");
        Assert.Equal(MessageTypes.Failed, claude.Type);
        Assert.Contains("cannot run here yet", claude.Payload);
        Assert.Contains("Keychain", claude.Payload);
        await kit.Bus.PersonTellAsync("alpha", "dev2", "hello", ct: Ct); // grok
        var grok = await kit.OutcomeAsync("dev2");
        Assert.Equal(MessageTypes.Failed, grok.Type);
        Assert.Contains("not signed in on this computer", grok.Payload);
        Assert.DoesNotContain(kit.Backend.Requests, r => LaunchKit.IsFor(r.Json, "dev1") || LaunchKit.IsFor(r.Json, "dev2"));
    }

    // ------------------------------------------------------------------ the gateway

    [Fact]
    public async Task TheGatewayForwardsTheClisOwnSignInUnchangedAndSwapsNothingIn()
    {
        await using var f = await GatewayFixture.StartAsync();
        // The app's own store holds a (fake) credential: in host sign-in mode it must NOT be used.
        f.Credentials.Set(CredentialSlot.AnthropicApiKey, FakeSecrets.AnthropicKey);
        f.Credentials.ClaudeMode = ClaudeMode.ApiKey;
        await using var gateway = f.Host.Start(new RunGatewaySpec(41, "Alpha/m41", new HashSet<string> { Vhosts.Model, Vhosts.Grok }, [], TimeSpan.FromHours(1))
        {
            HostSignIn = true,
        }, new NoBus());
        using var http = ProxyClient.For(gateway);

        foreach (var (vhost, path, secret, accepted) in new[]
                 {
                     (Vhosts.Model, "/v1/messages?beta=true", FakeSecrets.SetupToken, "claude-subscription"),
                     (Vhosts.Grok, "/v1/responses", FakeSecrets.GrokSession1, "grok-session-1"),
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{vhost}{path}")
            {
                Content = new StringContent("{\"stream\":false}", Encoding.UTF8, "application/json"),
            };
            // What the CLI sends with the host's sign-in: its own Bearer (here a fake), no run token.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            f.Upstream.ReleaseStream();
            using var response = await http.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(accepted, response.Headers.GetValues("x-fake-accepted").Single());
            f.Upstream.ResetStream();
        }
        Assert.All(f.Upstream.Requests, r => Assert.Null(r.ApiKey)); // the app's key was never added
        var forwards = f.Log.Read(41).Where(e => e.Kind == GatewayEventKinds.Forward).ToList();
        Assert.Equal(2, forwards.Count);
        Assert.All(forwards, e => Assert.Contains("host sign-in, passed through", e.Detail));
        Assert.DoesNotContain(f.Log.Read(41), e => FakeSecrets.All.Any(s => (e.Detail ?? "").Contains(s) || (e.Target ?? "").Contains(s)));

        // An expired or revoked host sign-in: the run gets a credential problem naming the person's own CLI, no refresh.
        f.Upstream.RefuseAll = true;
        using var refused = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Model}/v1/models");
        refused.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FakeSecrets.SetupToken);
        using var r401 = await http.SendAsync(refused, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, r401.StatusCode);
        Assert.Contains(f.Log.Read(41), e => e.Kind == GatewayEventKinds.CredentialProblem && e.Detail!.Contains("run 'claude' in your own terminal"));
    }

    [Fact]
    public async Task OutsideHostSignInModeTheRunTokenIsStillRequired()
    {
        await using var f = await GatewayFixture.StartAsync();
        await using var gateway = f.StartRun(42);
        using var http = ProxyClient.For(gateway);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Model}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FakeSecrets.SetupToken);
        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(f.Upstream.Requests);
    }
}
