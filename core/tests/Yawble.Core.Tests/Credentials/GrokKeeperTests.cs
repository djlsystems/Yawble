using System.Net;
using System.Net.Http.Headers;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Credentials;

/// <summary>
/// The Grok keeper's refresh path, against a STUB grok binary (a shell script
/// that records how it was run and rewrites auth.json the way xAI's CLI would). The real grok is never run and no
/// real Grok login is read.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class GrokKeeperTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A stub grok. <paramref name="refreshOn"/>: which first argument rewrites auth.json with
    /// <see cref="FakeSecrets.GrokSession2"/> ("models", "-p" via --no-auto-update, or "none").
    /// Every run appends its arguments and environment to calls.log in the stub's folder.
    /// </summary>
    private static string Stub(string dir, string refreshOn, int sleepMs = 0)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "grok");
        var log = Path.Combine(dir, "calls.log");
        File.WriteAllText(path, $$$"""
            #!/bin/sh
            echo "ARGS $*" >> '{{{log}}}'
            env | sort | sed 's/^/ENV /' >> '{{{log}}}'
            {{{(sleepMs > 0 ? $"sleep {sleepMs / 1000.0:0.###}" : "")}}}
            first="$1"
            [ "$first" = "--no-auto-update" ] && first="$2"
            if [ "$first" = "{{{refreshOn}}}" ]; then
              exp=$(( $(date +%s) + 7200 ))
              printf '{"https://auth.x.ai::00000000-0000-0000-0000-000000000000":{"access_token":"{{{FakeSecrets.GrokSession2}}}","refresh_token":"FAKE-refresh-2","auth_mode":"oauth","expires_at":%s}}' "$exp" > "$GROK_HOME/auth.json.tmp"
              mv "$GROK_HOME/auth.json.tmp" "$GROK_HOME/auth.json"
            fi
            exit 0
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string[] Calls(string dir) =>
        File.Exists(Path.Combine(dir, "calls.log")) ? File.ReadAllLines(Path.Combine(dir, "calls.log")) : [];

    [Fact]
    public async Task ExpiringSession_IsRefreshedByRunningGrokModelsInTheHostGrokHome_WithABuiltEnvironment()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stub grok is a shell script");
        using var db = new TempDb();
        var stubDir = Path.Combine(db.Dir, "stub");
        var home = GrokHome.InDataDir(Path.Combine(db.Dir, "data")).EnsureCreated();
        Assert.Equal("700", CredentialServiceTests.Stat(home.Path));
        FakeSecrets.WriteGrokAuth(home, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddMinutes(2)); // inside RefreshAhead

        // A key in the core's own environment must never reach the keeper's grok (it would outrank the session).
        Environment.SetEnvironmentVariable("XAI_API_KEY", "FAKE-should-not-leak");
        try
        {
            await using var keeper = new GrokKeeper(home, new GrokKeeperOptions { GrokBinary = Stub(stubDir, "models") });
            var result = await keeper.RefreshAsync();
            Assert.True(result.Valid);
            Assert.True(result.Changed);
            Assert.Equal(FakeSecrets.GrokSession2, keeper.CurrentSession()!.AccessToken);
            Assert.Equal(1, keeper.Runs);
            Assert.DoesNotContain(FakeSecrets.GrokSession2, result.ToString()); // never printed
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", null);
        }
        var calls = Calls(stubDir);
        Assert.Contains("ARGS models", calls);
        Assert.Contains($"ENV GROK_HOME={home.Path}", calls);
        Assert.Contains($"ENV HOME={home.Path}", calls);
        Assert.Contains("ENV GROK_DISABLE_AUTOUPDATER=1", calls);
        Assert.Contains("ENV GROK_TELEMETRY_ENABLED=false", calls);
        Assert.DoesNotContain(calls, l => l.StartsWith("ENV XAI_API_KEY") || l.StartsWith("ENV GROK_CODE_XAI_API_KEY") || l.StartsWith("ENV YAWBLE_"));
    }

    [Fact]
    public async Task WhenGrokModelsDoesNotRefresh_TheKeeperFallsBackToAOneTurnPrompt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stub grok is a shell script");
        using var db = new TempDb();
        var stubDir = Path.Combine(db.Dir, "stub");
        var home = GrokHome.InDataDir(Path.Combine(db.Dir, "data")).EnsureCreated();
        FakeSecrets.WriteGrokAuth(home, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddSeconds(-5)); // expired

        await using var keeper = new GrokKeeper(home, new GrokKeeperOptions { GrokBinary = Stub(stubDir, "-p") });
        Assert.Null(keeper.CurrentSession());
        var result = await keeper.RefreshAsync();
        Assert.True(result.Valid);
        Assert.Equal(2, keeper.Runs);
        var args = Calls(stubDir).Where(l => l.StartsWith("ARGS ")).ToArray();
        Assert.Equal(["ARGS models", "ARGS --no-auto-update -p Reply with OK. --max-turns 1"], args);
        Assert.Equal(FakeSecrets.GrokSession2, keeper.CurrentSession()!.AccessToken);
    }

    [Fact]
    public async Task AFreshSession_IsLeftAlone_AndConcurrentRefreshesRunGrokOnce()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stub grok is a shell script");
        using var db = new TempDb();
        var stubDir = Path.Combine(db.Dir, "stub");
        var home = GrokHome.InDataDir(Path.Combine(db.Dir, "data")).EnsureCreated();
        FakeSecrets.WriteGrokAuth(home, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddHours(1));
        await using var keeper = new GrokKeeper(home, new GrokKeeperOptions { GrokBinary = Stub(stubDir, "models", sleepMs: 400) });

        var fresh = await keeper.RefreshAsync();
        Assert.True(fresh.Valid);
        Assert.False(fresh.Changed);
        Assert.Equal(0, keeper.Runs);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => keeper.RefreshAsync(force: true)));
        Assert.Equal(1, keeper.Runs); // single-flight: only the keeper touches the refresh token, and only once at a time
        Assert.All(results, r => Assert.True(r.Changed));
    }

    [Fact]
    public async Task NotSignedInOrUnrefreshable_SaysSignInAgain()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stub grok is a shell script");
        using var db = new TempDb();
        var stubDir = Path.Combine(db.Dir, "stub");
        var home = GrokHome.InDataDir(Path.Combine(db.Dir, "data")).EnsureCreated();
        await using var keeper = new GrokKeeper(home, new GrokKeeperOptions { GrokBinary = Stub(stubDir, "none") });

        var notSignedIn = await keeper.RefreshAsync();
        Assert.False(notSignedIn.Valid);
        Assert.Contains("not signed in", notSignedIn.Detail);
        Assert.Equal(0, keeper.Runs);

        FakeSecrets.WriteGrokAuth(home, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddSeconds(-1));
        var expired = await keeper.RefreshAsync();
        Assert.False(expired.Valid);
        Assert.Equal("Grok login expired: sign in again", expired.Detail);
        Assert.Equal(2, keeper.Runs);
    }

    [Fact]
    public async Task Gateway_WithAnExpiredSession_WaitsForTheKeeper_ThenForwardsTheNewToken()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stub grok is a shell script");
        await using var f = await GatewayFixture.StartAsync(keeper: new GrokKeeperOptions { GrokBinary = Stub(Path.Combine(Path.GetTempPath(), "yawble-stub-" + Guid.NewGuid().ToString("N")), "models") });
        // The fixture's keeper runs on the fixture's clock; expires_at is real time, so align the clock with now.
        f.Clock.Now = DateTimeOffset.UtcNow;
        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.Now.AddSeconds(-1));
        await using var gateway = f.StartRun(20);
        using var http = ProxyClient.For(gateway);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Grok}/v1/settings");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        using var resp = await http.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("grok-session-2", resp.Headers.GetValues("x-fake-accepted").Single());
        Assert.Equal(1, f.Keeper!.Runs);
    }

    [Fact]
    public async Task Gateway_OnAnUpstream401ForGrok_AsksTheKeeperToRefresh_AndTheNextRequestUsesTheNewSession()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stub grok is a shell script");
        await using var f = await GatewayFixture.StartAsync(keeper: new GrokKeeperOptions { GrokBinary = Stub(Path.Combine(Path.GetTempPath(), "yawble-stub-" + Guid.NewGuid().ToString("N")), "models") });
        f.Clock.Now = DateTimeOffset.UtcNow;
        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.Now.AddHours(1)); // looks valid, but xAI revoked it
        await using var gateway = f.StartRun(21);
        using var http = ProxyClient.For(gateway);

        async Task<HttpResponseMessage> Ask()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Grok}/v1/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
            return await http.SendAsync(req, Ct);
        }
        f.Upstream.RefuseAll = true;
        using (var first = await Ask()) Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode); // Grok re-runs yawble-grok-auth and retries
        await Wait.UntilAsync(() => f.Keeper!.CurrentSession()?.AccessToken == FakeSecrets.GrokSession2, "keeper refreshed after the 401");
        Assert.Contains(f.Log.Read(21), e => e.Kind == GatewayEventKinds.CredentialProblem && e.Detail!.Contains(GrokRefusedWording));
        f.Upstream.RefuseAll = false;
        using var second = await Ask();
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("grok-session-2", second.Headers.GetValues("x-fake-accepted").Single());
    }

    // ------------------------------------------------------------------ a refresh run never leaves grok signed out
    // A fake runner (GrokKeeperOptions.RunGrok) stands in for grok on every OS: it records the arguments and rewrites
    // auth.json the way grok would. No real grok runs and no real ~/.grok is touched.

    private static GrokKeeper FakeKeeper(GrokHome home, List<string> calls, Func<IReadOnlyList<string>, string?> rewrite) =>
        new(home, new GrokKeeperOptions
        {
            GrokBinary = "fake-grok",
            RunGrok = args =>
            {
                calls.Add(string.Join(' ', args));
                if (rewrite(args) is { } text) File.WriteAllText(home.AuthFile, text);
                return Task.FromResult("exited 0");
            },
        });

    private static bool IsPrompt(IReadOnlyList<string> args) => args.Contains("-p");

    private static string Entry(string access, string? refresh, DateTimeOffset expires)
    {
        var entry = new System.Text.Json.Nodes.JsonObject { ["access_token"] = access, ["expires_at"] = expires.ToUnixTimeSeconds() };
        if (refresh is not null) entry["refresh_token"] = refresh;
        return new System.Text.Json.Nodes.JsonObject { ["https://auth.x.ai::0"] = entry }.ToJsonString();
    }

    [Fact]
    public async Task APromptRunThatLeavesAuthJsonEmpty_GetsItPutBack()
    {
        // What grok does when xAI refuses the refresh: `grok models` exits 0 (no change), then `grok -p` exits 1 and leaves auth.json as `{}`.
        using var db = new TempDb();
        var home = GrokHome.InDataDir(Path.Combine(db.Dir, "data")).EnsureCreated();
        FakeSecrets.WriteGrokAuth(home, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddSeconds(-5)); // expired, refresh token
        var before = File.ReadAllBytes(home.AuthFile);
        var calls = new List<string>();
        await using var keeper = FakeKeeper(home, calls, args => IsPrompt(args) ? "{}" : null);

        var result = await keeper.RefreshAsync();

        Assert.Equal(["models", "--no-auto-update -p Reply with OK. --max-turns 1"], calls);
        Assert.Equal(1, keeper.Restores);
        Assert.Equal(before, File.ReadAllBytes(home.AuthFile)); // byte for byte as it was
        Assert.Equal(FakeSecrets.GrokSession1, home.ReadSession()!.AccessToken);
        Assert.False(File.Exists(home.AuthFile + ".yawble-restore"));
        Assert.False(result.Valid); // still expired: the person is told to sign in again, but is not signed out
        Assert.DoesNotContain(FakeSecrets.GrokSession1, result.ToString());
    }

    [Fact]
    public async Task AModelsRunThatLeavesAuthJsonEmpty_GetsItPutBack_AndThePromptIsNotRun()
    {
        using var db = new TempDb();
        var home = GrokHome.InDataDir(Path.Combine(db.Dir, "data")).EnsureCreated();
        FakeSecrets.WriteGrokAuth(home, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddMinutes(2));
        var calls = new List<string>();
        await using var keeper = FakeKeeper(home, calls, args => args[0] == "models" ? "" : null);

        await keeper.RefreshAsync();

        Assert.Equal(["models"], calls); // the same refusal would clear it again
        Assert.Equal(1, keeper.Restores);
        Assert.Equal(FakeSecrets.GrokSession1, home.ReadSession()!.AccessToken);
    }

    [Fact]
    public async Task WithoutARefreshToken_TheOneTurnPromptIsNeverRun()
    {
        using var db = new TempDb();
        var home = GrokHome.InDataDir(Path.Combine(db.Dir, "data")).EnsureCreated();
        File.WriteAllText(home.AuthFile, Entry(FakeSecrets.GrokSession1, null, DateTimeOffset.UtcNow.AddSeconds(-5)));
        var calls = new List<string>();
        await using var keeper = FakeKeeper(home, calls, _ => null);

        var result = await keeper.RefreshAsync(force: true);

        Assert.Equal(["models"], calls);
        Assert.Equal(1, keeper.Runs);
        Assert.Equal(0, keeper.Restores);
        Assert.False(result.Valid);
        Assert.Equal(FakeSecrets.GrokSession1, home.ReadSession()!.AccessToken); // left as it was
    }

    [Fact]
    public async Task ARunThatRenewsTheSession_IsKept_AndNothingIsPutBack()
    {
        using var db = new TempDb();
        var home = GrokHome.InDataDir(Path.Combine(db.Dir, "data")).EnsureCreated();
        FakeSecrets.WriteGrokAuth(home, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddSeconds(-5));
        var renewed = Entry(FakeSecrets.GrokSession2, "FAKE-r2", DateTimeOffset.UtcNow.AddHours(2));
        var calls = new List<string>();
        await using var keeper = FakeKeeper(home, calls, args => IsPrompt(args) ? renewed : null);

        var result = await keeper.RefreshAsync();

        Assert.True(result.Valid);
        Assert.True(result.Changed);
        Assert.Equal(0, keeper.Restores);
        Assert.Equal(FakeSecrets.GrokSession2, home.ReadSession()!.AccessToken);
    }

    [Fact]
    public async Task Gateway_RemembersTheLastGrok401_AndASuccessClearsIt()
    {
        await using var f = await GatewayFixture.StartAsync(keeper: new GrokKeeperOptions { GrokBinary = "fake-grok", RunGrok = _ => Task.FromResult("exited 1") });
        f.Clock.Now = DateTimeOffset.UtcNow;
        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.Now.AddHours(1));
        await using var gateway = f.StartRun(24);
        using var http = ProxyClient.For(gateway);
        async Task<HttpStatusCode> Ask()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Grok}/v1/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
            using var resp = await http.SendAsync(req, Ct);
            return resp.StatusCode;
        }
        var watch = f.Host.GrokSignIn;
        Assert.Null(watch.Current(f.GrokHome.ReadSession()));

        f.Upstream.RefuseAll = true; // xAI refuses the session
        Assert.Equal(HttpStatusCode.Unauthorized, await Ask());
        var refusal = watch.Current(f.GrokHome.ReadSession());
        Assert.NotNull(refusal);
        Assert.Contains(GrokRefusedWording, refusal.Reason);
        Assert.DoesNotContain(FakeSecrets.GrokSession1, refusal.ToString());

        f.Upstream.RefuseAll = false;
        Assert.Equal(HttpStatusCode.OK, await Ask());
        Assert.Null(watch.Current(f.GrokHome.ReadSession()));

        // The gateway's own 401 (auth.json holds no session): remembered too, tied to "no session".
        File.WriteAllText(f.GrokHome.AuthFile, "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, await Ask());
        Assert.Equal("Grok is not signed in on this computer, or its sign-in could not be renewed: run 'grok' in your own terminal and sign in",
            watch.Current(null)!.Reason);
        // ...and not shown once auth.json holds a session again (the person signed in).
        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession2, f.Clock.Now.AddHours(1));
        Assert.Null(watch.Current(f.GrokHome.ReadSession()));
    }

    /// <summary>The card reason RunGateway writes for an upstream 401 on Grok's own sign-in (UpstreamRefused).</summary>
    private const string GrokRefusedWording = "the app asked grok to renew it";

    /// <summary>
    /// The wording on every OS. The test above skips on Windows (its stub grok is a /bin/sh script), which is how its old
    /// "asked the Grok keeper to refresh" went unseen there; this one needs no stub: the grok it names is not there, so the
    /// refresh it starts fails, and the 401's reason is what it checks.
    /// </summary>
    [Fact]
    public async Task Gateway_OnAnUpstream401ForGrok_SaysTheAppAskedGrokToRenewIt_OnEveryOs()
    {
        await using var f = await GatewayFixture.StartAsync(keeper: new GrokKeeperOptions { GrokBinary = Path.Combine(Path.GetTempPath(), "yawble-no-grok-" + Guid.NewGuid().ToString("N"), "grok") });
        f.Clock.Now = DateTimeOffset.UtcNow;
        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.Now.AddHours(1)); // looks valid, but xAI revoked it
        await using var gateway = f.StartRun(23);
        using var http = ProxyClient.For(gateway);
        f.Upstream.RefuseAll = true;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Grok}/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        using var resp = await http.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var problem = Assert.Single(f.Log.Read(23), e => e.Kind == GatewayEventKinds.CredentialProblem);
        Assert.Contains(GrokRefusedWording, problem.Detail);
        Assert.Contains("run 'grok' in your own terminal", problem.Detail);
    }

    [Fact]
    public async Task Gateway_WhenTheKeeperCannotRefresh_Answers401WithSignInAgain()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stub grok is a shell script");
        await using var f = await GatewayFixture.StartAsync(keeper: new GrokKeeperOptions { GrokBinary = Stub(Path.Combine(Path.GetTempPath(), "yawble-stub-" + Guid.NewGuid().ToString("N")), "none") });
        f.Clock.Now = DateTimeOffset.UtcNow;
        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.Now.AddSeconds(-1));
        await using var gateway = f.StartRun(22);
        using var http = ProxyClient.For(gateway);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Grok}/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        using var resp = await http.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(f.Upstream.Requests); // the expired session was never sent
        Assert.Contains(f.Log.Read(22), e => e.Kind == GatewayEventKinds.CredentialProblem && e.Detail == "Grok is not signed in on this computer, or its sign-in could not be renewed: run 'grok' in your own terminal and sign in");
    }
}
