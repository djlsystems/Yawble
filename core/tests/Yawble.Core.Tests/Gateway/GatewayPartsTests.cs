using System.Diagnostics;
using System.Net;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Transcripts;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>The smaller parts: allowlist matching, yawble-grok-auth, the Grok home reader and the transcript store.</summary>
public class GatewayPartsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AllowHosts_MatchExactOrWildcardSubdomains_AndTheVendorFloorAlwaysWins()
    {
        string[] allow = ["registry.npmjs.org", "*.pypi.org", "api.anthropic.com", "*.grok.com"];
        Assert.True(Vhosts.HostAllowed("registry.npmjs.org", allow));
        Assert.True(Vhosts.HostAllowed("REGISTRY.npmjs.org", allow));
        Assert.True(Vhosts.HostAllowed("files.pypi.org", allow));
        Assert.False(Vhosts.HostAllowed("pypi.org", allow)); // the wildcard is subdomains only
        Assert.False(Vhosts.HostAllowed("evilpypi.org", allow));
        Assert.False(Vhosts.HostAllowed("registry.npmjs.org.evil.com", allow));
        Assert.False(Vhosts.HostAllowed("api.anthropic.com", allow)); // floor
        Assert.False(Vhosts.HostAllowed("cli-chat-proxy.grok.com", allow)); // floor beats *.grok.com
        Assert.False(Vhosts.HostAllowed("eu.api.x.ai", allow));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("104.16.1.1", false)]
    [InlineData("2606:4700::1", false)]
    public void PrivateAddresses_AreRecognised(string address, bool isPrivate) =>
        Assert.Equal(isPrivate, RunGateway.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public async Task SandboxGrokAuth_PrintsTheRunTokenOnly_AndFailsWithoutOne()
    {
        var psi = Binaries.GrokAuth();
        psi.Environment["YAWBLE_RUN_TOKEN"] = "yawrt_placeholder-for-test";
        psi.Environment["GROK_AUTH_EXPIRED"] = "1"; // a refresh run behaves the same: no network, same placeholder
        using (var p = Process.Start(psi)!)
        {
            p.StandardInput.Close();
            var stdout = await p.StandardOutput.ReadToEndAsync(Ct);
            await p.WaitForExitAsync(Ct);
            Assert.Equal(0, p.ExitCode);
            Assert.Equal("yawrt_placeholder-for-test\n", stdout);
        }
        var bare = Binaries.GrokAuth();
        using (var p = Process.Start(bare)!)
        {
            p.StandardInput.Close();
            var stdout = await p.StandardOutput.ReadToEndAsync(Ct);
            await p.WaitForExitAsync(Ct);
            Assert.Equal(1, p.ExitCode);
            Assert.Equal("", stdout);
        }
    }

    [Fact]
    public void GrokHome_ReadsTheObservedShapes_AndNothingElse()
    {
        using var db = new TempDb();
        var home = GrokHome.InDataDir(db.Dir).EnsureCreated();
        Assert.Null(home.ReadSession());
        Assert.False(home.SignedIn);

        // Observed shape (G5/G6, external provider): {"<issuer>::<uuid>": {"key": …, "auth_mode": "external", "expires_at": …}}.
        File.WriteAllText(home.AuthFile, """{"https://auth.x.ai::abc":{"key":"FAKE-k","auth_mode":"external","create_time":1,"expires_at":4102444800}}""");
        var s = home.ReadSession()!;
        Assert.Equal("FAKE-k", s.AccessToken);
        Assert.Equal("external", s.AuthMode);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), s.ExpiresAt);
        Assert.DoesNotContain("FAKE-k", s.ToString());

        // Two entries: the one that expires last wins. Milliseconds and ISO strings are read too.
        File.WriteAllText(home.AuthFile, """{"a":{"access_token":"FAKE-old","expires_at":1700000000000},"b":{"access_token":"FAKE-new","expires_at":"2099-01-01T00:00:00Z"}}""");
        Assert.Equal("FAKE-new", home.ReadSession()!.AccessToken);

        File.WriteAllText(home.AuthFile, "{ not json");
        Assert.Null(home.ReadSession());
    }

    [Fact]
    public async Task Transcripts_AreAppendedToAnOwnerOnlyFile_AndIndexed()
    {
        using var db = new TempDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
        var store = new TranscriptStore(Path.Combine(db.Dir, "data"), db.ConnectionString, clock);
        await using (var w = store.Open(42, "Alpha", "Ada"))
        {
            await w.WriteAsync("{\"type\":\"system\"}\n", Ct);
            await w.WriteAsync("\u001b[32mgreen\u001b[0m\n"u8.ToArray(), Ct); // PTY bytes pass through unchanged
            Assert.Null(store.Find(42)!.EndedAt);
            clock.Advance(TimeSpan.FromMinutes(3));
        }
        var info = store.Find(42)!;
        Assert.Equal("Alpha/Ada", info.Member);
        Assert.Equal(clock.Now, info.EndedAt);
        var bytes = store.Read(42)!;
        Assert.Equal(info.Bytes, bytes.Length);
        Assert.Equal("{\"type\":\"system\"}\n\u001b[32mgreen\u001b[0m\n", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.EndsWith(Path.Combine("transcripts", "Alpha", "Ada", "run-42.log"), info.Path);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("600", Credentials.CredentialServiceTests.Stat(info.Path));
            Assert.Equal("700", Credentials.CredentialServiceTests.Stat(Path.GetDirectoryName(info.Path)!));
        }
        Assert.Null(store.Read(43));
    }
}
