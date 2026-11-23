using System.Net;
using System.Net.Http.Headers;
using Yawble.Core.Agents;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// Card 578 (B128 verify): the Settings > Agents Grok tile as agents.detect gives it (SignInCatalog.Grok under the
/// GatewaySignInOverlay) end to end with a real RunGateway, a fake upstream, a temp Grok home and a fake grok runner
/// (GrokKeeperOptions.RunGrok). No real grok runs and no real ~/.grok is read: the tile's FakeHost path maps to the temp
/// home's auth.json.
/// </summary>
public class B128VerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string TilePath = "/home/alice/.grok/auth.json";
    private const string Exe = "/home/alice/.grok/downloads/grok-1.0.77-linux-x86_64";

    /// <summary>The tile as the window sees it: detection from auth.json's bytes now, then the gateway overlay.</summary>
    private static Func<Task<CliSignIn>> Tile(GrokHome home, GrokSignInWatch watch)
    {
        string? Read() => File.Exists(home.AuthFile) ? File.ReadAllText(home.AuthFile) : null;
        var inner = new LiveDetector(() =>
        {
            var host = new FakeHost(TargetOs.Linux, "/home/alice");
            if (Read() is { } text) host.File(TilePath, text);
            return TestClis.Found("grok", Exe, ["/home/alice/.grok/downloads"], ["/home/alice/.grok"]) with
            {
                SignIn = SignInCatalog.Grok(host.Host()),
            };
        });
        var overlay = new GatewaySignInOverlay(inner, watch, p => p == TilePath ? Read() : null);
        return async () => (await overlay.DetectAllAsync(Ct)).Single().SignIn!;
    }

    private static async Task<HttpStatusCode> AskGrok(HttpClient http, RunGateway gateway)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{Vhosts.Grok}/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        using var resp = await http.SendAsync(req, Ct);
        return resp.StatusCode;
    }

    [Fact]
    public async Task AuthJsonShapes_OnTheTile_ThroughTheOverlay()
    {
        await using var f = await GatewayFixture.StartAsync();
        var tile = Tile(f.GrokHome, f.Host.GrokSignIn);
        foreach (var text in new[] { "{}", "", "{not json", """{"https://auth.x.ai::0":{"auth_mode":"oauth"}}""" })
        {
            File.WriteAllText(f.GrokHome.AuthFile, text);
            var s = await tile();
            Assert.Equal(SignInStatus.NotSignedIn, s.Status);
            Assert.StartsWith("Not signed in: run `grok` in your own terminal and sign in. ", s.Detail);
        }
        File.Delete(f.GrokHome.AuthFile);
        Assert.Equal(SignInStatus.NotSignedIn, (await tile()).Status);

        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddHours(1));
        var ok = await tile();
        Assert.Equal(SignInStatus.SignedIn, ok.Status);
        Assert.DoesNotContain("FAKE-", ok.Detail);
    }

    [Fact]
    public async Task AGateway401_TurnsTheTileNotSignedIn_AndALaterSuccessClearsIt()
    {
        await using var f = await GatewayFixture.StartAsync(keeper: new GrokKeeperOptions { GrokBinary = "fake-grok", RunGrok = _ => Task.FromResult("exited 0") });
        f.Clock.Now = DateTimeOffset.UtcNow;
        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.Now.AddHours(1));
        await using var gateway = f.StartRun(578);
        using var http = ProxyClient.For(gateway);
        var tile = Tile(f.GrokHome, f.Host.GrokSignIn);

        Assert.Equal(HttpStatusCode.OK, await AskGrok(http, gateway));
        Assert.Equal(SignInStatus.SignedIn, (await tile()).Status);

        f.Upstream.RefuseAll = true;
        Assert.Equal(HttpStatusCode.Unauthorized, await AskGrok(http, gateway));
        var refused = await tile();
        Assert.Equal(SignInStatus.NotSignedIn, refused.Status);
        Assert.StartsWith(SignInCatalog.GrokNotSignedIn + ". The app's last Grok request (", refused.Detail);
        Assert.DoesNotContain("FAKE-", refused.Detail);

        f.Upstream.RefuseAll = false;
        Assert.Equal(HttpStatusCode.OK, await AskGrok(http, gateway));
        Assert.Equal(SignInStatus.SignedIn, (await tile()).Status);
    }

    [Fact]
    public async Task AFakeGrokThatWritesEmptyJsonDuringTheGatewaysRefresh_DoesNotLeaveAuthJsonSignedOut()
    {
        var calls = new List<string>();
        GrokHome? home = null;
        await using var f = await GatewayFixture.StartAsync(keeper: new GrokKeeperOptions
        {
            GrokBinary = "fake-grok",
            RunGrok = args =>
            {
                lock (calls) calls.Add(string.Join(' ', args));
                File.WriteAllText(home!.AuthFile, "{}"); // what grok did on 2026-11-22 when xAI refused its refresh
                return Task.FromResult("exited 1");
            },
        });
        home = f.GrokHome;
        f.Clock.Now = DateTimeOffset.UtcNow;
        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, f.Clock.Now.AddHours(1));
        var before = File.ReadAllBytes(f.GrokHome.AuthFile);
        await using var gateway = f.StartRun(579);
        using var http = ProxyClient.For(gateway);

        f.Upstream.RefuseAll = true;
        Assert.Equal(HttpStatusCode.Unauthorized, await AskGrok(http, gateway)); // starts RefreshGrokAsync(force: true)
        var result = await f.Keeper!.RefreshAsync(force: true); // waits behind (or repeats) the gateway's refresh

        Assert.True(f.Keeper.Restores >= 1);
        Assert.Equal(before, File.ReadAllBytes(f.GrokHome.AuthFile));
        Assert.Equal(FakeSecrets.GrokSession1, f.GrokHome.ReadSession()!.AccessToken);
        Assert.DoesNotContain(FakeSecrets.GrokSession1, result.ToString());
        lock (calls) Assert.All(calls, c => Assert.Equal("models", c)); // the same refusal would clear it again: no -p

        // Not signed out, but the tile still says to sign in (xAI refused this session) and does not read Signed in.
        var s = await Tile(f.GrokHome, f.Host.GrokSignIn)();
        Assert.Equal(SignInStatus.NotSignedIn, s.Status);
        Assert.StartsWith(SignInCatalog.GrokNotSignedIn, s.Detail);
    }

    private sealed class LiveDetector(Func<DetectedCli> cli) : ICliDetector
    {
        public Task<IReadOnlyList<DetectedCli>> DetectAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DetectedCli>>([cli()]);
    }
}
