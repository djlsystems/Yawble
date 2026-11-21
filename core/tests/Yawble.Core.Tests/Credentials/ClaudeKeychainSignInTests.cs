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
            if (Throws) throw new CredentialStoreException("SecKeychainFindGenericPassword failed (OSStatus -128)");
            return _items.GetValueOrDefault(target);
        }
    }

    /// <summary>The JSON Claude Code keeps in its Keychain item (the same as ~/.claude/.credentials.json).</summary>
    public static string ClaudeSignIn(string accessToken, DateTimeOffset expiresAt, string refreshToken = "sk-ant-ort01-FAKEKEYCHAINREFRESH") =>
        $"{{\"claudeAiOauth\":{{\"accessToken\":\"{accessToken}\",\"refreshToken\":\"{refreshToken}\",\"expiresAt\":{expiresAt.ToUnixTimeMilliseconds()},\"scopes\":[\"user:inference\"],\"subscriptionType\":\"max\"}}}}";
}

/// <summary>
/// B118 Part A: on macOS the gateway reads Claude Code's own sign-in from the login Keychain item "Claude Code-credentials"
/// (through the reader seam; a fake here), not from a file; renewal is still the host's own claude. The value is never
/// logged. Fakes only: no real Keychain, `security` or `claude` is used.
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
