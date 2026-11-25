using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Credentials;

/// <summary>
/// A stand-in for the host's credential store (macOS: the login Keychain). Items by service; it records every lookup,
/// so a test can tell an existence check (<see cref="Targets"/>) from a read of the secret (<see cref="Read"/>). Nothing
/// real is ever looked up.
/// </summary>
public sealed class FakeKeychain : IHostCredentialReader
{
    private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);

    public List<string> Checks { get; } = [];
    public List<string> Reads { get; } = [];

    /// <summary>When set, a read fails the way a refused Keychain prompt or a broken store does.</summary>
    public bool Throws { get; set; }

    public FakeKeychain Set(string service, string value)
    {
        lock (_items) _items[service] = value;
        return this;
    }

    public FakeKeychain Remove(string service)
    {
        lock (_items) _items.Remove(service);
        return this;
    }

    public IReadOnlyList<string> Targets(string filter)
    {
        lock (_items)
        {
            Checks.Add(filter);
            return _items.ContainsKey(filter) ? [filter] : [];
        }
    }

    public string? Read(string target)
    {
        lock (_items)
        {
            Reads.Add(target);
            if (Throws) throw new CredentialStoreException("the Keychain item could not be read: /usr/bin/security exited with code 51");
            return _items.GetValueOrDefault(target);
        }
    }

    /// <summary>The JSON Claude Code keeps in its Keychain item (the same as ~/.claude/.credentials.json).</summary>
    public static string ClaudeSignIn(string accessToken, DateTimeOffset expiresAt, string refreshToken = "sk-ant-ort01-FAKEKEYCHAINREFRESH") =>
        $"{{\"claudeAiOauth\":{{\"accessToken\":\"{accessToken}\",\"refreshToken\":\"{refreshToken}\",\"expiresAt\":{expiresAt.ToUnixTimeMilliseconds()},\"scopes\":[\"user:inference\"],\"subscriptionType\":\"max\"}}}}";
}

/// <summary>
/// On macOS the gateway reads Claude Code's own sign-in from the login Keychain item "Claude Code-credentials"
/// (through the reader seam; a fake here), not from a file; renewal is still the host's own claude. The value is never
/// logged. The item is read through /usr/bin/security (<see cref="MacHostCredentialReader"/>, a fake tool here) and the
/// token is kept in memory: read again only near expiry, during a renewal or after a 401. Fakes only: no real Keychain,
/// `security` or `claude` is used.
/// </summary>
public class ClaudeKeychainSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Service = "Claude Code-credentials";

    /// <summary>A file that must never be read when the Keychain is the source: it holds a different token.</summary>
    private static string DecoyFile(TempDb dir)
    {
        var file = Path.Combine(dir.Dir, "home", ".claude", ".credentials.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, FakeKeychain.ClaudeSignIn("sk-ant-oat01-FROMTHEFILE", DateTimeOffset.UtcNow.AddHours(4)));
        return file;
    }

    [Fact]
    public void TheKeychainServiceIsClaudeCodesOwn()
    {
        Assert.Equal(Service, ClaudeHostSession.KeychainService);
    }

    [Fact]
    public void Present_TheTokenIsReadFromTheKeychainItem_NotTheFile()
    {
        using var dir = new TempDb();
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(1_900_000_000_000);
        var keychain = new FakeKeychain().Set(Service, FakeKeychain.ClaudeSignIn(FakeSecrets.SetupToken, expires));
        var session = new ClaudeHostSession(DecoyFile(dir), () => null, keychain: keychain);

        var t = session.Read()!;
        Assert.Equal(FakeSecrets.SetupToken, t.AccessToken);
        Assert.Equal(expires, t.ExpiresAt);
        Assert.Equal([Service], keychain.Reads);
        Assert.Equal("the login Keychain item \"Claude Code-credentials\"", session.Source);
    }

    [Fact]
    public void Absent_OrNotASignIn_IsNoToken()
    {
        using var dir = new TempDb();
        var keychain = new FakeKeychain();
        var session = new ClaudeHostSession(DecoyFile(dir), () => null, keychain: keychain);
        Assert.Null(session.Read()); // no item: the file beside it is not a fallback

        keychain.Set(Service, "");
        Assert.Null(session.Read());
        keychain.Set(Service, "not json");
        Assert.Null(session.Read());
        keychain.Set(Service, "[]");
        Assert.Null(session.Read());
        keychain.Set(Service, "{\"claudeAiOauth\":{\"accessToken\":42}}");
        Assert.Null(session.Read());
        keychain.Set(Service, "{\"claudeAiOauth\":{}}");
        Assert.Null(session.Read());
        keychain.Throws = true; // the person clicked Deny, or the store failed
        Assert.Null(session.Read());
    }

    [Fact]
    public async Task Absent_TheGatewayRefusesWithTheReason_AndForwardsNothing()
    {
        using var dir = new TempDb();
        var session = new ClaudeHostSession(DecoyFile(dir), () => null, keychain: new FakeKeychain());
        await using var f = await GatewayFixture.StartAsync(claudeHost: session);
        await using var gateway = f.StartRun(11, claudeFromHost: true);
        using var http = ProxyClient.For(gateway);

        using var response = await http.SendAsync(Messages(gateway), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("not signed in on this computer", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(f.Upstream.Requests);
    }

    [Fact]
    public async Task Present_TheGatewayPutsTheKeychainTokenOnTheRequest()
    {
        using var dir = new TempDb();
        var keychain = new FakeKeychain().Set(Service, FakeKeychain.ClaudeSignIn(FakeSecrets.SetupToken, DateTimeOffset.UtcNow.AddHours(4)));
        await using var f = await GatewayFixture.StartAsync(claudeHost: new ClaudeHostSession(DecoyFile(dir), () => null, keychain: keychain));
        f.Upstream.ReleaseStream();
        await using var gateway = f.StartRun(12, claudeFromHost: true);
        using var http = ProxyClient.For(gateway);

        using var response = await http.SendAsync(Messages(gateway), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(f.Upstream.Requests);
        Assert.Equal("Bearer " + FakeSecrets.SetupToken, seen.Authorization);
        Assert.DoesNotContain("FROMTHEFILE", seen.Authorization);
    }

    [Fact]
    public async Task Expired_TheHostsClaudeRenewsTheKeychainItem_AndTheNewTokenIsUsed()
    {
        using var dir = new TempDb();
        var keychain = new FakeKeychain().Set(Service, FakeKeychain.ClaudeSignIn("sk-ant-oat01-EXPIRED", DateTimeOffset.UtcNow.AddMinutes(-3)));
        var ran = new List<string>();
        var session = new ClaudeHostSession(DecoyFile(dir), () => "claude", keychain: keychain, runClaude: (_, args, _) =>
        {
            lock (ran) ran.Add(string.Join(' ', args.Take(2)));
            // What Claude Code on a Mac does on use: it renews its own Keychain item.
            if (args[0] == "-p") keychain.Set(Service, FakeKeychain.ClaudeSignIn(FakeSecrets.SetupToken, DateTimeOffset.UtcNow.AddHours(8)));
            return Task.CompletedTask;
        });

        var token = await session.CurrentAsync(Ct);
        Assert.Equal(FakeSecrets.SetupToken, token!.AccessToken);
        Assert.Equal(["auth status", "-p Reply with the single word ok."], ran);
        Assert.Equal(FakeSecrets.SetupToken, (await session.CurrentAsync(Ct))!.AccessToken); // fresh now: no second renewal
        Assert.Equal(2, ran.Count);
    }

    [Fact]
    public async Task Expired_AndNotRenewed_IsNoToken()
    {
        using var dir = new TempDb();
        var keychain = new FakeKeychain().Set(Service, FakeKeychain.ClaudeSignIn("sk-ant-oat01-EXPIRED", DateTimeOffset.UtcNow.AddMinutes(-3)));
        var session = new ClaudeHostSession(DecoyFile(dir), () => "claude", keychain: keychain, runClaude: (_, _, _) => Task.CompletedTask);
        Assert.Null(await session.CurrentAsync(Ct));
    }

    [Fact]
    public async Task TheValueIsNeverLogged()
    {
        using var dir = new TempDb();
        const string access = "sk-ant-oat01-NEVERINALOGLINE0123456789";
        const string refresh = "sk-ant-ort01-NEVERINALOGLINEEITHER0123";
        var keychain = new FakeKeychain().Set(Service, FakeKeychain.ClaudeSignIn(access, DateTimeOffset.UtcNow.AddMinutes(1), refresh));
        var log = new List<string>();
        void Log(string line) { lock (log) log.Add(line); }

        // Renewal that does not renew, renewal without claude, a forced renewal, a renewal that works: every log line it makes.
        var session = new ClaudeHostSession(DecoyFile(dir), () => "claude", Log, keychain: keychain, runClaude: (_, _, _) => Task.CompletedTask);
        await session.CurrentAsync(Ct);
        await session.RefreshAsync(force: true, Ct);
        await new ClaudeHostSession(DecoyFile(dir), () => null, Log, keychain: keychain).RefreshAsync(force: true, Ct);
        var renewing = new ClaudeHostSession(DecoyFile(dir), () => "claude", Log, keychain: keychain, runClaude: (_, args, _) =>
        {
            if (args[0] == "-p") keychain.Set(Service, FakeKeychain.ClaudeSignIn(access + "NEW", DateTimeOffset.UtcNow.AddHours(8), refresh + "NEW"));
            return Task.CompletedTask;
        });
        Assert.NotNull(await renewing.CurrentAsync(Ct));

        Assert.NotEmpty(log);
        foreach (var line in log.Append(session.Source))
        {
            Assert.DoesNotContain("NEVERINALOGLINE", line);
            Assert.DoesNotContain("claudeAiOauth", line);
        }
    }

    [Fact]
    public void WithoutAKeychain_TheFileIsTheSource()
    {
        using var dir = new TempDb();
        var file = DecoyFile(dir);
        var session = new ClaudeHostSession(file, () => null);
        Assert.Equal("sk-ant-oat01-FROMTHEFILE", session.Read()!.AccessToken);
        Assert.Equal(file, session.Source);
    }

    [Fact]
    public void ForThisHost_IsTheKeychainOnlyOnAMac()
    {
        var session = ClaudeHostSession.ForThisHost(_ => null, () => null);
        if (OperatingSystem.IsMacOS()) Assert.Equal("the login Keychain item \"Claude Code-credentials\"", session.Source);
        else Assert.Equal(ClaudeHostSession.DefaultCredentialsFile(_ => null), session.Source);
    }

    // ---- The token is kept in memory; the item is read again only near expiry, during a renewal, or after a 401.

    [Fact]
    public async Task TwoRequestsInARow_ReadTheItemOnce()
    {
        using var dir = new TempDb();
        var keychain = new FakeKeychain().Set(Service, FakeKeychain.ClaudeSignIn(FakeSecrets.SetupToken, DateTimeOffset.UtcNow.AddHours(4)));
        var session = new ClaudeHostSession(DecoyFile(dir), () => null, keychain: keychain);

        Assert.Equal(FakeSecrets.SetupToken, (await session.CurrentAsync(Ct))!.AccessToken);
        Assert.Equal(FakeSecrets.SetupToken, (await session.CurrentAsync(Ct))!.AccessToken);
        Assert.Single(keychain.Reads);
    }

    [Fact]
    public async Task ThroughTheGateway_TwoModelRequestsRunTheSecurityToolOnce()
    {
        using var dir = new TempDb();
        var tool = new FakeSecurityTool().Set(Service, FakeKeychain.ClaudeSignIn(FakeSecrets.SetupToken, DateTimeOffset.UtcNow.AddHours(4)));
        var session = new ClaudeHostSession(DecoyFile(dir), () => null, keychain: new MacHostCredentialReader(tool));
        await using var f = await GatewayFixture.StartAsync(claudeHost: session);
        f.Upstream.ReleaseStream();
        await using var gateway = f.StartRun(13, claudeFromHost: true);
        using var http = ProxyClient.For(gateway);

        for (var i = 0; i < 2; i++)
        {
            using var response = await http.SendAsync(Messages(gateway), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(2, f.Upstream.Requests.Count);
        Assert.All(f.Upstream.Requests, r => Assert.Equal("Bearer " + FakeSecrets.SetupToken, r.Authorization));
        Assert.Equal(1, tool.Reads);
        Assert.All(tool.Calls, c => Assert.Equal(["find-generic-password", "-s", Service, "-w"], c));
    }

    [Fact]
    public async Task NearExpiry_TheItemIsReadAgain()
    {
        using var dir = new TempDb();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var keychain = new FakeKeychain().Set(Service, FakeKeychain.ClaudeSignIn("sk-ant-oat01-FIRST", clock.Now.AddMinutes(30)));
        var session = new ClaudeHostSession(DecoyFile(dir), () => null, clock: clock, keychain: keychain);

        Assert.Equal("sk-ant-oat01-FIRST", (await session.CurrentAsync(Ct))!.AccessToken);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("sk-ant-oat01-FIRST", (await session.CurrentAsync(Ct))!.AccessToken);
        Assert.Single(keychain.Reads); // 20 minutes left: kept

        // The host's claude renewed its item meanwhile; within RenewMargin of the kept token's expiry it is read again.
        keychain.Set(Service, FakeKeychain.ClaudeSignIn("sk-ant-oat01-SECOND", clock.Now.AddHours(8)));
        clock.Advance(TimeSpan.FromMinutes(16)); // 4 minutes left
        Assert.Equal("sk-ant-oat01-SECOND", (await session.CurrentAsync(Ct))!.AccessToken);
        Assert.Equal(2, keychain.Reads.Count);
        Assert.Equal("sk-ant-oat01-SECOND", (await session.CurrentAsync(Ct))!.AccessToken);
        Assert.Equal(2, keychain.Reads.Count);
    }

    [Fact]
    public async Task After401_TheItemIsReadAgain_EvenWhenTheRenewalDidNotRenew()
    {
        using var dir = new TempDb();
        var keychain = new FakeKeychain().Set(Service, FakeKeychain.ClaudeSignIn("sk-ant-oat01-REFUSED", DateTimeOffset.UtcNow.AddHours(4)));
        var ran = new List<string>();
        var session = new ClaudeHostSession(DecoyFile(dir), () => "claude", keychain: keychain, runClaude: (_, args, _) =>
        {
            lock (ran) ran.Add(string.Join(' ', args.Take(2)));
            return Task.CompletedTask;
        });
        Assert.Equal("sk-ant-oat01-REFUSED", (await session.CurrentAsync(Ct))!.AccessToken);
        var before = keychain.Reads.Count;

        // The API answered 401: the gateway forces a renewal (RenewClaudeHostAsync). The host's claude renewed its item
        // by itself meanwhile: the different, fresh token IS the renewal, and claude is not run.
        keychain.Set(Service, FakeKeychain.ClaudeSignIn("sk-ant-oat01-AGAIN", DateTimeOffset.UtcNow.AddHours(8)));
        Assert.True(await session.RefreshAsync(force: true, Ct));
        Assert.Empty(ran);
        Assert.True(keychain.Reads.Count > before);
        Assert.Equal("sk-ant-oat01-AGAIN", (await session.CurrentAsync(Ct))!.AccessToken);

        // A 401 whose renewal changes nothing is not renewed and keeps nothing: the next request reads the item again.
        var unchanged = new FakeKeychain().Set(Service, FakeKeychain.ClaudeSignIn("sk-ant-oat01-SAME", DateTimeOffset.UtcNow.AddHours(4)));
        var ran2 = new List<string>();
        var s2 = new ClaudeHostSession(DecoyFile(dir), () => "claude", keychain: unchanged, runClaude: (_, args, _) =>
        {
            lock (ran2) ran2.Add(string.Join(' ', args.Take(2)));
            return Task.CompletedTask;
        });
        await s2.CurrentAsync(Ct);
        Assert.False(await s2.RefreshAsync(force: true, Ct));
        Assert.Equal(["auth status", "-p Reply with the single word ok."], ran2);
        var afterRefresh = unchanged.Reads.Count;
        await s2.CurrentAsync(Ct);
        Assert.Equal(afterRefresh + 1, unchanged.Reads.Count);
    }

    [Fact]
    public async Task ASlowRead_DoesNotReplaceANewerKeptToken()
    {
        using var dir = new TempDb();
        var keychain = new SlowFirstReadKeychain(FakeKeychain.ClaudeSignIn("sk-ant-oat01-OLDER", DateTimeOffset.UtcNow.AddHours(4)));
        var session = new ClaudeHostSession(DecoyFile(dir), () => null, keychain: keychain);

        // The first read takes the older value and then stalls; a second read meanwhile gets the newer one and keeps it.
        var slow = Task.Run(session.Read, Ct);
        Assert.True(keychain.FirstReadTookItsValue.Wait(TimeSpan.FromSeconds(10), Ct));
        keychain.Value = FakeKeychain.ClaudeSignIn("sk-ant-oat01-NEWER", DateTimeOffset.UtcNow.AddHours(8));
        Assert.Equal("sk-ant-oat01-NEWER", session.Read()!.AccessToken);

        // The slow read finishes last: it returns what it read, but the kept token stays the newer one.
        keychain.ReleaseFirstRead.Set();
        Assert.Equal("sk-ant-oat01-OLDER", (await slow)!.AccessToken);
        var reads = keychain.Reads;
        Assert.Equal("sk-ant-oat01-NEWER", (await session.CurrentAsync(Ct))!.AccessToken);
        Assert.Equal(reads, keychain.Reads); // served from memory: no read
    }

    /// <summary>A Keychain stand-in whose first read takes the value, then waits to be released before it answers.</summary>
    private sealed class SlowFirstReadKeychain(string value) : IHostCredentialReader
    {
        private int _reads;
        public volatile string Value = value;
        public ManualResetEventSlim FirstReadTookItsValue { get; } = new();
        public ManualResetEventSlim ReleaseFirstRead { get; } = new();
        public int Reads => Volatile.Read(ref _reads);

        public IReadOnlyList<string> Targets(string filter) => [filter];

        public string? Read(string target)
        {
            var value = Value;
            if (Interlocked.Increment(ref _reads) == 1)
            {
                FirstReadTookItsValue.Set();
                ReleaseFirstRead.Wait(TimeSpan.FromSeconds(10));
            }
            return value;
        }
    }

    [Fact]
    public async Task TheFileIsStillReadAtEveryRequest()
    {
        using var dir = new TempDb();
        var file = DecoyFile(dir);
        var session = new ClaudeHostSession(file, () => null);
        Assert.Equal("sk-ant-oat01-FROMTHEFILE", (await session.CurrentAsync(Ct))!.AccessToken);
        File.WriteAllText(file, FakeKeychain.ClaudeSignIn("sk-ant-oat01-NEWFILE", DateTimeOffset.UtcNow.AddHours(4)));
        Assert.Equal("sk-ant-oat01-NEWFILE", (await session.CurrentAsync(Ct))!.AccessToken);
    }

    [Fact]
    public void AnUnreadableItem_IsNoToken_AndTheLogGivesTheExitCodeOnly()
    {
        using var dir = new TempDb();
        var log = new List<string>();
        var tool = new FakeSecurityTool { Fails = (51, "sk-ant-oat01-ECHOEDONSTDOUT0123456789") };
        var session = new ClaudeHostSession(DecoyFile(dir), () => null, log.Add, keychain: new MacHostCredentialReader(tool));
        Assert.Null(session.Read());
        Assert.Contains(log, l => l.Contains("exited with code 51"));
        Assert.DoesNotContain(log, l => l.Contains("ECHOED"));
        tool.Fails = null;
        tool.TimesOut = true;
        Assert.Null(session.Read());
        Assert.Contains(log, l => l.Contains("did not answer within 5 s"));
    }
    private static HttpRequestMessage Messages(RunGateway gateway)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://{Vhosts.Model}/v1/messages")
        {
            Content = new StringContent("{\"model\":\"fake\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token.Value);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20,claude-code-20250219");
        return request;
    }
}
