using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// On the real Host, a person removes a plugin, or one version of it, from the web app with the rules
/// of <c>yawble plugin remove</c>: refused while a member is hired on it (naming them), refused for the
/// active version while others are kept, a <c>plugins.removed</c> tenant row with the removal. A
/// Manager's credential is refused. Deleting a team never removes a plugin, and a team installed from
/// a package carries that package on its summary for the delete dialog to say so.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PluginRemoveRouteTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-remove-{Guid.NewGuid():N}");
    private readonly FixedSolutions _solutions = new();
    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Plugins => PluginInstall.PluginsRoot(_dataRoot);

    private PluginCatalog Catalog => Services.GetRequiredService<PluginCatalog>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<ITeamSolutions>(_solutions);
            }));

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _team = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("Plugins", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed class FixedSolutions : ITeamSolutions
    {
        public Dictionary<string, TeamSolution> Teams { get; } = [];

        public TeamSolution? For(string team) => Teams.GetValueOrDefault(team);
    }

    /// <summary>Installs <paramref name="versions"/> of sample-echo on disk, the last active, and rescans.</summary>
    private async Task InstalledAsync(params string[] versions)
    {
        foreach (var version in versions) PluginInstall.Write(_dataRoot, "sample-echo", version, active: version == versions[^1]);
        await Catalog.RescanAsync(Ct);
        Assert.NotNull(Catalog.For("sample-echo"));
    }

    private async Task<HttpClient> PersonAsync()
    {
        var person = _factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return person;
    }

    private async Task<HttpClient> ManagerAsync()
    {
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, _team,
            TeamRegistry.ManagerPermits, ct: Ct);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        return client;
    }

    private async Task HireAsync(HttpClient person, string name)
    {
        var hired = await person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name, agent = "plugin:sample-echo" }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> RemoveAsync(HttpClient client, string id, string? version = null)
    {
        var response = await client.DeleteAsync(
            $"/api/plugins/{id}" + (version is null ? "" : $"?version={Uri.EscapeDataString(version)}"), Ct);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(Ct));
    }

    private async Task<List<TenantEvent>> RemovedRowsAsync() =>
        [.. (await Services.GetRequiredService<ITenantLog>().ReadAsync(take: ITenantLog.MaxTake, ct: Ct)).Events
            .Where(e => e.Action == TenantActions.PluginRemoved)];

    // ---- refused while hired -------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_plugin_a_member_is_hired_on_is_refused_naming_the_member_and_nothing_is_removed()
    {
        await InstalledAsync("0.1.0");
        using var person = await PersonAsync();
        await HireAsync(person, "Echo");

        var (status, body) = await RemoveAsync(person, "sample-echo");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("Plugin sample-echo is in use by Plugins / Echo; remove those members first.", body.GetProperty("error").GetString());
        var member = Assert.Single(body.GetProperty("members").EnumerateArray());
        Assert.Equal(_team, member.GetProperty("team").GetString());
        Assert.Equal("Echo", member.GetProperty("memberName").GetString());

        Assert.True(Directory.Exists(Path.Combine(Plugins, "sample-echo", "0.1.0")));
        Assert.NotNull(Catalog.For("sample-echo"));
        Assert.Empty(await RemovedRowsAsync());
    }

    [Fact]
    public async Task Removing_the_only_version_is_removing_the_plugin_and_is_refused_while_hired()
    {
        await InstalledAsync("0.1.0");
        using var person = await PersonAsync();
        await HireAsync(person, "Echo");

        var (status, body) = await RemoveAsync(person, "sample-echo", "0.1.0");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("in use by Plugins / Echo", body.GetProperty("error").GetString());
        Assert.True(Directory.Exists(Path.Combine(Plugins, "sample-echo", "0.1.0")));
    }

    // ---- removed -------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_removes_a_plugin_nobody_is_hired_on_with_a_tenant_row_and_the_catalog_forgets_it()
    {
        await InstalledAsync("0.1.0", "0.2.0");
        using var person = await PersonAsync();

        var (status, body) = await RemoveAsync(person, "sample-echo");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("whole").GetBoolean());
        Assert.Equal(["0.1.0", "0.2.0"], body.GetProperty("versions").EnumerateArray().Select(v => v.GetString()));

        Assert.False(Directory.Exists(Path.Combine(Plugins, "sample-echo")));
        Assert.Null(Catalog.For("sample-echo"));
        Assert.DoesNotContain(Catalog.Refused, r => r.Id.Contains("sample-echo"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dataRoot, ".plugin-removed-*"));

        var row = Assert.Single(await RemovedRowsAsync());
        Assert.Equal(Email, row.ActorEmail);
        Assert.Equal("sample-echo", row.Subject);
        Assert.Contains("\"whole\":true", row.Detail);
        Assert.Contains("0.2.0", row.Detail);
    }

    [Fact]
    public async Task A_kept_version_is_removed_while_members_are_hired_and_the_active_one_is_refused_while_others_are_kept()
    {
        await InstalledAsync("0.1.0", "0.2.0");
        using var person = await PersonAsync();
        await HireAsync(person, "Echo");

        var (active, activeBody) = await RemoveAsync(person, "sample-echo", "0.2.0");
        Assert.Equal(HttpStatusCode.Conflict, active);
        Assert.StartsWith("sample-echo 0.2.0 is the active version.", activeBody.GetProperty("error").GetString());
        Assert.True(Directory.Exists(Path.Combine(Plugins, "sample-echo", "0.2.0")));

        var (kept, keptBody) = await RemoveAsync(person, "sample-echo", "0.1.0");
        Assert.Equal(HttpStatusCode.OK, kept);
        Assert.False(keptBody.GetProperty("whole").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(Plugins, "sample-echo", "0.1.0")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Plugins, "sample-echo"), ".*"));
        Assert.Equal("0.2.0", Catalog.For("sample-echo")!.Manifest.Version);

        var row = Assert.Single(await RemovedRowsAsync());
        Assert.Contains("\"whole\":false", row.Detail);
        Assert.Contains("0.1.0", row.Detail);
    }

    [Fact]
    public async Task An_unknown_plugin_or_version_is_404_and_a_malformed_one_400()
    {
        await InstalledAsync("0.1.0");
        using var person = await PersonAsync();

        var (missing, missingBody) = await RemoveAsync(person, "not-here");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("Plugin not-here is not installed.", missingBody.GetProperty("error").GetString());

        var (noVersion, noVersionBody) = await RemoveAsync(person, "sample-echo", "9.9");
        Assert.Equal(HttpStatusCode.NotFound, noVersion);
        Assert.Equal("Plugin sample-echo has no version 9.9 (it has: 0.1.0).", noVersionBody.GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await RemoveAsync(person, "Not_An_Id")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await RemoveAsync(person, "sample-echo", "..")).Status);
        Assert.Empty(await RemovedRowsAsync());
    }

    [Fact]
    public async Task A_managers_credential_cannot_remove_a_plugin()
    {
        await InstalledAsync("0.1.0");
        using var manager = await ManagerAsync();

        var response = await manager.DeleteAsync("/api/plugins/sample-echo", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(Plugins, "sample-echo", "0.1.0")));
    }

    // ---- deleting a team -----------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_team_never_removes_a_plugin_its_members_were_hired_on()
    {
        await InstalledAsync("0.1.0");
        using var person = await PersonAsync();
        await HireAsync(person, "Echo");

        var deleted = await person.DeleteAsync($"/api/teams/{_team}", Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        Assert.True(Directory.Exists(Path.Combine(Plugins, "sample-echo", "0.1.0")));
        Assert.NotNull(Catalog.For("sample-echo"));
        Assert.Empty(await RemovedRowsAsync());

        // With nobody hired any more, it is a person's to remove.
        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(person, "sample-echo")).Status);
    }

    [Fact]
    public async Task A_team_installed_from_a_package_names_it_and_its_plugins_on_its_summary_and_a_hand_made_one_does_not()
    {
        _solutions.Teams[_team] = new TeamSolution("job-tracker", "Job Tracker", "1.0.0", ["job-board"]);
        var other = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("By hand", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        using var person = await PersonAsync();

        var teams = await person.GetFromJsonAsync<JsonElement>("/api/teams", Ct);
        var list = teams.ValueKind == JsonValueKind.Array ? teams : teams.GetProperty("teams");

        var packaged = list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == _team).GetProperty("solution");
        Assert.Equal("Job Tracker", packaged.GetProperty("name").GetString());
        Assert.Equal("1.0.0", packaged.GetProperty("version").GetString());
        Assert.Equal(["job-board"], packaged.GetProperty("plugins").EnumerateArray().Select(p => p.GetString()));

        var byHand = list.EnumerateArray().Single(t => t.GetProperty("id").GetString() == other);
        Assert.True(!byHand.TryGetProperty("solution", out var none) || none.ValueKind == JsonValueKind.Null);
    }
}
