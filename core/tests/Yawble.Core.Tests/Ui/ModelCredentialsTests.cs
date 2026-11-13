using System.Text;
using System.Text.Json;
using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Ipc;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>
/// Settings → Model credentials wired to the real credential service: the 0600 file fallback, the host
/// Grok home and a stub grok/claude in the host panes. Fake secrets only; no real CLI or credential is touched.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ModelCredentialsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly CredentialService _service;
    private readonly GrokHome _grokHome;

    public ModelCredentialsTests()
    {
        _service = CredentialService.FileFallback(Path.Combine(_db.Dir, "data", "credentials"), _db.ConnectionString);
        _grokHome = GrokHome.InDataDir(_db.Dir).EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void BothSwitches_GoToTheServiceTheGatewayReads_AndNoValueComesBack()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the 0600 fallback is Linux's");
        var creds = new ModelCredentials(_service, _grokHome);
        var status = creds.Status();
        Assert.Equal("os-store", status.Implementation);
        Assert.Equal("file", status.Storage);
        Assert.Contains("only your account can read", status.StorageWarning);
        Assert.False(status.Claude.Subscription.Connected);
        Assert.False(status.Grok.Login.SignedIn);

        Assert.Equal("not_connected", Assert.Throws<BusRefusal>(() => creds.SetMode("claude", "apiKey")).Code);
        Assert.Equal("not_connected", Assert.Throws<BusRefusal>(() => creds.SetMode("grok", "login")).Code);
        Assert.Equal("bad_format", Assert.Throws<BusRefusal>(() => creds.SetClaudeSetupToken("two words here")).Code);
        Assert.Equal("empty", Assert.Throws<BusRefusal>(() => creds.SetXaiApiKey("  ")).Code);

        creds.SetClaudeSetupToken(" " + FakeSecrets.SetupToken + "\n");
        creds.SetAnthropicApiKey(FakeSecrets.AnthropicKey);
        creds.SetXaiApiKey(FakeSecrets.XaiKey);
        status = creds.Status();
        Assert.True(status.Claude.Subscription.Connected);
        Assert.NotNull(status.Claude.Subscription.ExpiresAt);
        Assert.Equal(status.Claude.Subscription.AddedAt + CredentialService.SetupTokenLifetime, status.Claude.Subscription.ExpiresAt);
        Assert.False(status.Claude.Subscription.RenewSoon);
        Assert.True(status.Claude.ApiKey.Connected);
        Assert.True(status.Grok.ApiKey.Connected);

        // The Claude switch is what the gateway's model.yawble.internal reads on every request.
        Assert.Equal((ClaudeMode.Subscription, FakeSecrets.SetupToken), _service.ClaudeCredential());
        creds.SetMode("claude", "apiKey");
        Assert.Equal((ClaudeMode.ApiKey, FakeSecrets.AnthropicKey), _service.ClaudeCredential());
        Assert.Equal("apiKey", creds.Status().Claude.Mode);
        // The setup-token's date survives the switch, so its expiry still shows.
        Assert.NotNull(creds.Status().Claude.Subscription.ExpiresAt);

        creds.SetMode("grok", "apiKey");
        Assert.Equal(GrokMode.ApiKey, _service.GrokMode);
        FakeSecrets.WriteGrokAuth(_grokHome, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddHours(6));
        creds.SetMode("grok", "login");
        Assert.Equal(GrokMode.Session, _service.GrokMode);
        status = creds.Status();
        Assert.True(status.Grok.Login.SignedIn);
        Assert.NotNull(status.Grok.Login.ExpiresAt);
        Assert.NotNull(status.Grok.Login.LastRefreshAt);
        Assert.Null(status.Grok.Login.Problem);

        // What the screens get never holds a value.
        var json = JsonSerializer.Serialize(status, RpcDispatcher.Json);
        foreach (var secret in FakeSecrets.All) Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("sk-ant", json);

        creds.Remove("grokLogin");
        Assert.False(creds.Status().Grok.Login.SignedIn);
        creds.Remove("anthropicApiKey");
        Assert.Null(_service.Get(CredentialSlot.AnthropicApiKey));
        Assert.False(creds.Status().Claude.ApiKey.Connected);
        Assert.Equal("bad_credential", Assert.Throws<BusRefusal>(() => creds.Remove("nope")).Code);
    }

    [Fact]
    public void AnExpiredGrokSession_StillSignedIn_WithAProblem()
    {
        FakeSecrets.WriteGrokAuth(_grokHome, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddMinutes(-5));
        var login = new ModelCredentials(_service, _grokHome).Status().Grok.Login;
        Assert.True(login.SignedIn);
        Assert.Contains("expired", login.Problem);
    }

    [Fact]
    public async Task GrokSignIn_RunsThePinnedGrokLogin_InTheHostGrokHome_WithoutTheCoresKeys()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stub grok is a shell script");
        var stubs = Path.Combine(_db.Dir, "host-clis");
        var grok = Stub(stubs, "grok", $$$"""
            echo "ARGS $*"
            echo "GROK_HOME=$GROK_HOME"
            echo "XAI=${XAI_API_KEY:-unset}"
            exp=$(( $(date +%s) + 7200 ))
            printf '{"https://auth.x.ai::0":{"access_token":"{{{FakeSecrets.GrokSession1}}}","auth_mode":"oauth","expires_at":%s}}' "$exp" > "$GROK_HOME/auth.json"
            echo "Signed in."
            """);
        var hub = new TerminalHub(new AppEvents());
        await using var keeper = new GrokKeeper(_grokHome, new GrokKeeperOptions { GrokBinary = grok });
        var creds = new ModelCredentials(_service, _grokHome, keeper);
        var panes = new HostPaneTerminals(hub, name => name == "grok" ? grok : null, keeper);
        var channel = TerminalHub.HostChannel(IHostTerminals.GrokLogin);

        Environment.SetEnvironmentVariable("XAI_API_KEY", FakeSecrets.XaiKey);
        int exit;
        try
        {
            var done = new TaskCompletionSource<int>();
            await panes.StartAsync(IHostTerminals.GrokLogin, channel, code => { creds.GrokLoginFinished(code); done.SetResult(code); }, Ct);
            exit = await done.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", null);
        }

        Assert.Equal(0, exit);
        var text = Replay(hub, channel);
        Assert.Contains("ARGS login", text);
        Assert.Contains($"GROK_HOME={_grokHome.Path}", text);
        Assert.Contains("XAI=unset", text);
        Assert.Contains("Signed in.\r\n", text); // pipe output is made xterm-ready
        Assert.True(hub.SessionOf(channel)!.Ended);
        Assert.True(creds.Status().Grok.Login.SignedIn);
    }

    [Fact]
    public async Task SetupTokenPane_RunsThePinnedClaude_TakesInput_AndWithholdsTheCoresCredentials()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stub claude is a shell script");
        var claude = Stub(Path.Combine(_db.Dir, "host-clis"), "claude", """
            echo "ARGS $*"
            echo "KEY=${ANTHROPIC_API_KEY:-unset}"
            printf 'Press Enter to continue: '
            read answer
            echo "got [$answer]"
            echo "FAKE-oauth-token-printed-for-the-person"
            """);
        var hub = new TerminalHub(new AppEvents());
        await using var keeper = new GrokKeeper(_grokHome, new GrokKeeperOptions { GrokBinary = "/nonexistent" });
        var panes = new HostPaneTerminals(hub, name => name == "claude" ? claude : null, keeper);
        var channel = TerminalHub.HostChannel(IHostTerminals.ClaudeSetupToken);

        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", FakeSecrets.AnthropicKey);
        try
        {
            var done = new TaskCompletionSource<int>();
            await panes.StartAsync(IHostTerminals.ClaudeSetupToken, channel, done.SetResult, Ct);
            Assert.True(hub.SessionOf(channel)!.InputEnabled);
            await WaitFor(() => Replay(hub, channel).Contains("Press Enter"));
            Assert.True(hub.Input(channel, Encoding.UTF8.GetBytes("ok\r")));
            Assert.Equal(0, await done.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
        var text = Replay(hub, channel);
        Assert.Contains("ARGS setup-token", text);
        Assert.Contains("KEY=unset", text);
        Assert.Contains("got [ok]", text);
        // The app shows the pane; it never stores what the pane printed.
        Assert.Null(_service.Get(CredentialSlot.ClaudeSetupToken));
    }

    [Fact]
    public async Task APaneWhoseCliIsNotInstalled_IsRefused()
    {
        var hub = new TerminalHub(new AppEvents());
        await using var keeper = new GrokKeeper(_grokHome, new GrokKeeperOptions { GrokBinary = "/nonexistent" });
        var panes = new HostPaneTerminals(hub, _ => null, keeper);
        var refused = await Assert.ThrowsAsync<BusRefusal>(() =>
            panes.StartAsync(IHostTerminals.GrokLogin, TerminalHub.HostChannel(IHostTerminals.GrokLogin), _ => { }, Ct));
        Assert.Equal("not_installed", refused.Code);
    }

    [Fact]
    public async Task StoppingAPane_KillsItsCli()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stub grok is a shell script");
        var grok = Stub(Path.Combine(_db.Dir, "host-clis"), "grok", "echo waiting; sleep 30");
        var hub = new TerminalHub(new AppEvents());
        await using var keeper = new GrokKeeper(_grokHome, new GrokKeeperOptions { GrokBinary = grok });
        var panes = new HostPaneTerminals(hub, _ => grok, keeper);
        var channel = TerminalHub.HostChannel(IHostTerminals.GrokLogin);
        var done = new TaskCompletionSource<int>();
        await panes.StartAsync(IHostTerminals.GrokLogin, channel, done.SetResult, Ct);
        await WaitFor(() => Replay(hub, channel).Contains("waiting"));
        Assert.True(await hub.StopAsync(channel));
        Assert.Equal(130, await done.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(_grokHome.SignedIn);
    }

    private static string Stub(string dir, string name, string body)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string Replay(TerminalHub hub, string channel) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(hub.Attach(channel)["replay"]!.GetValue<string>()));

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            await Task.Delay(25, Ct);
        }
    }
}
