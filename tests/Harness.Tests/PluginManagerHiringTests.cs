using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// STEP 10, MANAGER HIRING (a person's decision, 2026-09-27): the `member` MCP tool, called by a
/// team's Manager, hires any installed plugin onto the Manager's own team - validated against the
/// manifest like a person's hire, with secrets bound to LOGICAL KEYS a person has already set and
/// never to values. Driven through the real tool, calling back into the real Host with the
/// Manager's own credential.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PluginManagerHiringTests : IAsyncLifetime
{
    private const string TokenKey = "SAMPLE_ECHO_MANAGER_HIRE_TOKEN";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-hire-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";
    private string _other = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);
        Environment.SetEnvironmentVariable(TokenKey, "set-by-a-person");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        var registry = Services.GetRequiredService<TeamRegistry>();
        _team = (await registry.CreateAsync("Mixed", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        _other = (await registry.CreateAsync("Other", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(TokenKey, null);
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private sealed class ServerClients(WebApplicationFactory<Program> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(factory.Server.CreateHandler(), disposeHandler: true) { BaseAddress = factory.Server.BaseAddress };
    }

    private string ManagerId => new ContainerId(_team, TeamRegistry.DefaultManagerName).ToString();

    private Task<string> ManagerKeyAsync() =>
        Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ManagerId, PrincipalKind.Container, _team, TeamRegistry.ManagerPermits, ct: Ct);

    /// <summary>
    /// The MCP tools as the team's Manager holds them: its own minted credential. SYNCHRONOUS on
    /// purpose - the accessor's context is an AsyncLocal, and one set inside an async helper does
    /// not flow back to its caller.
    /// </summary>
    private PlatformMcpTools ManagerTools(string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(ManagerId, PrincipalKind.Container, TeamRegistry.ManagerPermits.ToHashSet()), "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context }, Services.GetRequiredService<IPrincipalStore>(),
            Services.GetRequiredService<AgentCatalog>(), new ServerClients(_factory));
    }

    private static Dictionary<string, JsonElement> Config(string mode) =>
        new() { ["mode"] = JsonSerializer.SerializeToElement(mode) };

    [Fact]
    public async Task A_manager_hires_an_installed_plugin_onto_its_own_team_with_logical_keys()
    {
        var tools = ManagerTools(await ManagerKeyAsync());

        var hired = await tools.Hire("Echo", plugin: "sample-echo", config: Config("reverse"),
            secrets: new() { ["token"] = TokenKey }, cancellationToken: Ct);

        Assert.StartsWith("HTTP 200", hired);
        var member = new ContainerId(_team, "Echo");
        var snapshot = Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(member)!.Snapshot();
        Assert.Equal("plugin:sample-echo", snapshot.Agent);
        Assert.Equal(MemberRef.PluginKind, snapshot.Kind);

        // Its settings, the secret as the KEY the Manager bound - never a value.
        var settings = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(member, Ct);
        Assert.Equal("reverse", settings.Config["mode"].GetString());
        Assert.Equal(TokenKey, settings.Secrets["token"]);
        Assert.DoesNotContain("set-by-a-person", hired, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_managers_plugin_hire_is_validated_like_a_persons()
    {
        var tools = ManagerTools(await ManagerKeyAsync());

        // Against the manifest.
        var badConfig = await tools.Hire("Echo1", plugin: "sample-echo", config: Config("sideways"), cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", badConfig);
        Assert.Contains("must be one of: upper, reverse", badConfig);

        // Not installed.
        var missing = await tools.Hire("Echo2", plugin: "no-such-plugin", cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", missing);
        Assert.Contains("not installed", missing);

        // A key nobody set - or a value passed off as a key - is refused.
        var unset = await tools.Hire("Echo3", plugin: "sample-echo", secrets: new() { ["token"] = "NOBODY_SET_THIS_KEY" }, cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", unset);
        Assert.Contains("not set on this Host", unset);

        var value = await tools.Hire("Echo4", plugin: "sample-echo", secrets: new() { ["token"] = "hunter2 is my password" }, cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", value);

        // Never onto another team.
        var elsewhere = await tools.Hire("Echo5", team: _other, plugin: "sample-echo", cancellationToken: Ct);
        Assert.StartsWith("HTTP 403", elsewhere);

        // A plugin reads no prompt.
        Assert.StartsWith("Refused", await tools.Hire("Echo6", plugin: "sample-echo", prompt: "You are careful.", cancellationToken: Ct));

        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        Assert.All(new[] { "Echo1", "Echo2", "Echo3", "Echo4", "Echo6" }, n => Assert.Null(host.Find(new ContainerId(_team, n))));
        Assert.Null(host.Find(new ContainerId(_other, "Echo5")));
    }
}
