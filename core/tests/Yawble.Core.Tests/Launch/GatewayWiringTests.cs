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
