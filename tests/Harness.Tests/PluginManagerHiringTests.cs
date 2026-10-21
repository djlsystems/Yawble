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
        MarkAPersonOnlySetting(_dataRoot);
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

    /// <summary>
    /// The installed sample gains `live`, a bool that defaults to false and is `"setBy": "person"` -
    /// the shape an outward-acting plugin's real mode takes (an email plugin's `sendMode`).
    /// </summary>
    private static void MarkAPersonOnlySetting(string dataRoot)
    {
        var manifestPath = Path.Combine(dataRoot, "plugins", "sample-echo", "0.1.0", "plugin.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["config"]!["live"] = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "bool",
            ["default"] = false,
            ["setBy"] = "person",
            ["description"] = "Act for real rather than in the default safe mode.",
        };
        File.WriteAllText(manifestPath, manifest.ToJsonString());
    }

    /// <summary>
    /// A setting the manifest marks `"setBy": "person"` is out of an agent's reach: a Manager's hire
    /// may leave it at its default, or omit it, and is refused any other value with a sentence naming
    /// the field. Incoming content reaches a Manager's context, so it must not be able to widen what an
    /// outward-acting plugin may do.
    /// </summary>
    [Fact]
    public async Task A_manager_may_not_set_a_person_only_setting_to_anything_but_its_default()
    {
        var tools = ManagerTools(await ManagerKeyAsync());
        var live = new Dictionary<string, JsonElement> { ["live"] = JsonSerializer.SerializeToElement(true) };

        var refused = await tools.Hire("Sender", plugin: "sample-echo", config: live, cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", refused);
        Assert.Contains("`live` is set by a person only", refused);

        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        Assert.Null(host.Find(new ContainerId(_team, "Sender")));

        var atDefault = new Dictionary<string, JsonElement> { ["live"] = JsonSerializer.SerializeToElement(false) };
        Assert.StartsWith("HTTP 200", await tools.Hire("Drafts", plugin: "sample-echo", config: atDefault, cancellationToken: Ct));
        Assert.StartsWith("HTTP 200", await tools.Hire("Plain", plugin: "sample-echo", config: Config("reverse"), cancellationToken: Ct));
    }

    [Fact]
    public void A_manifest_setting_names_who_sets_it_as_person_or_anyone()
    {
        var (personOnly, _) = PluginConfigField.Parse("live", JsonDocument.Parse("""{"type":"bool","default":false,"setBy":"person"}""").RootElement);
        Assert.True(personOnly!.PersonOnly);

        var (anyone, _) = PluginConfigField.Parse("mode", JsonDocument.Parse("""{"type":"string","setBy":"anyone"}""").RootElement);
        Assert.False(anyone!.PersonOnly);

        var (none, refusal) = PluginConfigField.Parse("live", JsonDocument.Parse("""{"type":"bool","setBy":"manager"}""").RootElement);
        Assert.Null(none);
        Assert.Contains("`config.live.setBy` must be \"person\" or \"anyone\"", refusal);
    }

    private static Dictionary<string, JsonElement> Config(string mode) =>
        new() { ["mode"] = JsonSerializer.SerializeToElement(mode) };

    /// <summary>A PERSON's hire binding <paramref name="key"/> on <paramref name="team"/> - the
    /// only way a key becomes one a Manager may bind there.</summary>
    private Task PersonBindsAsync(string team, string key) =>
        Services.GetRequiredService<TeamRegistry>().HireMemberAsync(
            team, $"Bound{Guid.NewGuid():N}"[..12], "plugin:sample-echo", "", [], ct: Ct,
            settings: new PluginMemberSettings(
                new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = key }));

    /// <summary>
    /// M1 (round 2): a Manager binds only a key a PERSON has already bound on its own team. A Host
    /// variable (PATH), and a key set on the Host but bound by no person here - or only on another
    /// team - are refused with a reason that names no value.
    /// </summary>
    [Fact]
    public async Task A_manager_binds_only_keys_a_person_has_bound_on_its_team()
    {
        var tools = ManagerTools(await ManagerKeyAsync());
        var pathValue = Environment.GetEnvironmentVariable("PATH")!;

        var path = await tools.Hire("Echo2", plugin: "sample-echo", secrets: new() { ["token"] = "PATH" }, cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", path);
        Assert.Contains("`PATH`", path);
        Assert.Contains("not one a person has bound on this team", path);
        Assert.DoesNotContain(pathValue, path, StringComparison.Ordinal);

        // Set on the Host, but no person has bound it here yet - only on another team.
        await PersonBindsAsync(_other, TokenKey);
        var unbound = await tools.Hire("Echo3", plugin: "sample-echo", secrets: new() { ["token"] = TokenKey }, cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", unbound);
        Assert.DoesNotContain("set-by-a-person", unbound, StringComparison.Ordinal);

        // Once a person has bound it on this team, the Manager may bind it too.
        await PersonBindsAsync(_team, TokenKey);
        Assert.StartsWith("HTTP 200", await tools.Hire("Echo4", plugin: "sample-echo", secrets: new() { ["token"] = TokenKey }, cancellationToken: Ct));

        var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
        Assert.Null(host.Find(new ContainerId(_team, "Echo2")));
        Assert.Null(host.Find(new ContainerId(_team, "Echo3")));
        Assert.NotNull(host.Find(new ContainerId(_team, "Echo4")));
    }

    [Fact]
    public async Task A_manager_hires_an_installed_plugin_onto_its_own_team_with_logical_keys()
    {
        await PersonBindsAsync(_team, TokenKey);
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
        Assert.Contains("not one a person has bound on this team", unset);

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
