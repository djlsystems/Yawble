using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Sandboxing;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Agents;
using Xunit;

namespace Yawble.Core.Tests.Launch;

public class GatewayWiringTests
{
    private static GatewayEvent E(string kind, string? vhost = null, int? status = null, string? detail = null, string? target = null) =>
        new(7, "alpha/dev2", DateTimeOffset.UtcNow, kind, false, vhost, "POST", target, status, detail);

    [Fact]
    public void TheVerdictComesFromTheGatewayLog()
    {
        Assert.Equal(GatewayVerdict.Clean with { DeniedHosts = [] }, GatewayVerdicts.From([E(GatewayEventKinds.Forward, Vhosts.Model, 200)]) with { DeniedHosts = [] });

        var expired = GatewayVerdicts.From([E(GatewayEventKinds.CredentialProblem, Vhosts.Grok, 401, "Grok login expired: sign in again")]);
        Assert.True(expired.CredentialRejected);
        Assert.Equal("Grok login expired: sign in again", expired.Reason);

        // The keeper refreshed after an upstream 401 and the CLI's retry went through: not a credential failure.
        var recovered = GatewayVerdicts.From([
            E(GatewayEventKinds.Forward, Vhosts.Grok, 401),
            E(GatewayEventKinds.CredentialProblem, Vhosts.Grok, 401, "Grok session refused by xAI: asked the Grok keeper to refresh"),
            E(GatewayEventKinds.Forward, Vhosts.Grok, 200),
        ]);
        Assert.False(recovered.CredentialRejected);

        var connects = GatewayVerdicts.From([
            E(GatewayEventKinds.ConnectDenied, target: "api.anthropic.com:443"),
            E(GatewayEventKinds.ConnectDenied, target: "api.anthropic.com:443"),
            E(GatewayEventKinds.ConnectDenied, target: "example.com:443"),
        ]);
        Assert.Equal(3, connects.DeniedConnects);
        Assert.Equal(["api.anthropic.com:443", "example.com:443"], connects.DeniedHosts);
    }

    [Fact]
    public void DeniedConnectsSumsTheFoldedRefusals()
    {
        // B105: five CONNECT refusals of one host in a run are one row with Count 5.
        var one = GatewayVerdicts.From([E(GatewayEventKinds.ConnectDenied, target: "api.anthropic.com:443") with { Count = 5 }]);
        Assert.Equal(5, one.DeniedConnects);
        Assert.Equal(["api.anthropic.com:443"], one.DeniedHosts);

        var mix = GatewayVerdicts.From([
            E(GatewayEventKinds.ConnectDenied, target: "api.anthropic.com:443") with { Count = 5 },
            E(GatewayEventKinds.Forward, Vhosts.Model, 200) with { Count = 9 }, // other kinds never count
            E(GatewayEventKinds.ConnectDenied, target: "example.com:443") with { Count = 2 },
            E(GatewayEventKinds.ConnectDenied, target: "other.example:8443"),
        ]);
        Assert.Equal(8, mix.DeniedConnects);
        Assert.Equal(["api.anthropic.com:443", "example.com:443", "other.example:8443"], mix.DeniedHosts);
    }

    [Fact]
    public void DeniedConnectsCountsFoldedRowsWrittenThroughTheGatewayLog()
    {
        using var db = new TempDb();
        db.OpenStore().Dispose(); // the schema
        var log = new GatewayLog(db.ConnectionString);
        for (var i = 0; i < 5; i++) log.Write(new GatewayEvent(51, "alpha/dev2", DateTimeOffset.UtcNow, GatewayEventKinds.ConnectDenied, false, Target: "blocked.example:443"));
        log.Write(new GatewayEvent(51, "alpha/dev2", DateTimeOffset.UtcNow, GatewayEventKinds.ConnectDenied, false, Target: "other.example:443"));
        var rows = log.Read(51).Where(e => e.Kind == GatewayEventKinds.ConnectDenied).ToList();
        Assert.Equal(2, rows.Count);
        var verdict = GatewayVerdicts.From(log.Read(51));
        Assert.Equal(6, verdict.DeniedConnects);
        Assert.Equal(["blocked.example:443", "other.example:443"], verdict.DeniedHosts);
    }

    [Fact]
    public async Task CredentialModesFollowTheCredentialServiceAndTheHostGrokHome()
    {
        await using var f = await GatewayFixture.StartAsync();
        var modes = new ServiceCredentialModes(f.Credentials, f.GrokHome);
        Assert.Null(modes.ModeFor(AgentCli.Claude));
        Assert.Null(modes.ModeFor(AgentCli.Grok));

        f.Credentials.Set(CredentialSlot.ClaudeSetupToken, FakeSecrets.SetupToken);
        Assert.Equal(CredentialMode.ClaudeSubscription, modes.ModeFor(AgentCli.Claude));
        f.Credentials.ClaudeMode = ClaudeMode.ApiKey;
        Assert.Null(modes.ModeFor(AgentCli.Claude));
        f.Credentials.Set(CredentialSlot.AnthropicApiKey, FakeSecrets.AnthropicKey);
        Assert.Equal(CredentialMode.ClaudeApiKey, modes.ModeFor(AgentCli.Claude));

        FakeSecrets.WriteGrokAuth(f.GrokHome, FakeSecrets.GrokSession1, DateTimeOffset.UtcNow.AddMinutes(-5)); // expired: the keeper renews
        Assert.Equal(CredentialMode.GrokLogin, modes.ModeFor(AgentCli.Grok));
        f.Credentials.GrokMode = GrokMode.ApiKey;
        Assert.Null(modes.ModeFor(AgentCli.Grok));
        f.Credentials.Set(CredentialSlot.XaiApiKey, FakeSecrets.XaiKey);
        Assert.Equal(CredentialMode.GrokApiKey, modes.ModeFor(AgentCli.Grok));
    }

    [Fact]
    public async Task TheLauncherIsOffWithAReasonWhenTheLayoutCannotBeHonoured()
    {
        using var db = new TempDb();
        var clis = new FakeCliDetector(TestClis.Missing("claude"), TestClis.Missing("grok"));
        string? Env(string _) => null;

        await using (var notData = LaunchWiring.Create(Path.Combine(db.Dir, "elsewhere"), db.ConnectionString, clis, null, new RunRegistry(), Env))
        {
            Assert.Null(notData.Launcher);
            Assert.Contains("<app root>/data", notData.Problem);
            Assert.IsType<Yawble.Core.Bus.UnavailableRunner>(notData.Runner);
        }
        await using (var noInstall = LaunchWiring.Create(Path.Combine(db.Dir, "app", "data"), db.ConnectionString, clis, null, new RunRegistry(), Env))
        {
            Assert.Contains("YAWBLE_INSTALL_DIR", noInstall.Problem);
            Assert.Contains("--install-dir", noInstall.Problem);
        }
        var install = Path.Combine(db.Dir, "install");
        await using (var ok = LaunchWiring.Create(Path.Combine(db.Dir, "app", "data"), db.ConnectionString, clis,
            install, new RunRegistry(), Env))
        {
            Assert.Null(ok.Problem);
            Assert.NotNull(ok.Launcher);
            Assert.IsType<SandboxAgentRunner>(ok.Runner);
            // The host Grok home is where the policy layout denies it to every run.
            Assert.True(Directory.Exists(Path.Combine(db.Dir, "app", GrokHome.FolderName)));
        }
    }
}
