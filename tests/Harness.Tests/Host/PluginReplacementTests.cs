using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A RENAMED PLUGIN SAYS WHAT IT REPLACES, on the real Host: <c>fixture-old</c> 1.1.4 and
/// <c>fixture-new</c> 3.0.0 share the name "Fixture Cloud", and only the new one's manifest names the old
/// id in <c>replaces</c>. Members of two teams run the old id, each with settings (one the new manifest
/// no longer declares) and an own secret value.
///
/// <list type="bullet">
/// <item>Installing the new one answers the old id, its version and every member on it, team by team; an
/// id it names that is not installed is not listed; the listing says the same both ways. Nothing moves
/// and nothing is removed by the install.</item>
/// <item>Removing the old plugin is refused while a member runs it.</item>
/// <item>The person's move (<c>HumansOnly</c>) repoints each member with its settings - the dropped one too
/// - and own secret values kept, and one <c>member.changed</c> row naming from and to; then the old plugin
/// can be removed.</item>
/// <item>When the row cannot be written, that member is not moved.</item>
/// <item>A plugin that does not say it replaces the id takes no members, and one that is not installed is 404.</item>
/// </list>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PluginReplacementTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string Old = "fixture-old";
    private const string New = "fixture-new";
    private const string Name = "Fixture Cloud";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-replaces-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _alpha = "";
    private string _beta = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    private static string Value(string what) => $"{what}-{Guid.NewGuid():N}";

    private static JsonObject Manifest(string id, string version, Action<JsonObject>? edit = null) =>
        PluginInstall.Manifest(id, version, m =>
        {
            m["name"] = Name;
            m["secrets"] = JsonNode.Parse("""{ "token": { "description": "The service's token." } }""");
            edit?.Invoke(m);
        });

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        PluginInstall.Write(_dataRoot, Old, "1.1.4", manifest: Manifest(Old, "1.1.4", m =>
            m["config"] = JsonNode.Parse("""{ "region": { "type": "string" }, "legacy": { "type": "string" } }""")));

        // A plugin that names nothing in `replaces`, sharing nothing: it must take no members.
        PluginInstall.Write(_dataRoot, "fixture-other", "1.0.0", manifest: Manifest("fixture-other", "1.0.0", m => m["name"] = "Other Cloud"));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        var teams = Services.GetRequiredService<TeamRegistry>();
        _alpha = (await teams.CreateAsync("Alpha Replaces", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        _beta = (await teams.CreateAsync("Beta Replaces", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Hires <paramref name="name"/> on the old plugin with both settings and an own token; answers the token.</summary>
    private async Task<string> HireOnOldAsync(string team, string name)
    {
        var token = Value("token");
        var hired = await _person.PostAsJsonAsync($"/api/teams/{team}/containers", new
        {
            name,
            agent = MemberRef.ForPlugin(Old),
            config = new { region = "eu-west", legacy = "kept-" + name },
            secretValues = new Dictionary<string, string> { ["token"] = token },
        }, Ct);
        Assert.True(hired.StatusCode == HttpStatusCode.OK, await hired.Content.ReadAsStringAsync(Ct));
        return token;
    }

    /// <summary>Installs the new plugin from a built folder inside the instance, as the Marketplace does; answers the body.</summary>
    private async Task<JsonElement> InstallNewAsync(params string[] replaces)
    {
        var build = Path.Combine(_dataRoot, "build", Guid.NewGuid().ToString("N"));
        var folder = PluginInstall.Write(build, New, "3.0.0", manifest: Manifest(New, "3.0.0", m =>
        {
            m["config"] = JsonNode.Parse("""{ "region": { "type": "string" } }""");
            m["replaces"] = new JsonArray([.. replaces.Select(r => (JsonNode)r)]);
        }));

        var response = await _person.PostAsJsonAsync("/api/plugins/install", new { path = folder, replace = false }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> MoveAsync(string onto, string from, object? body = null)
    {
        using var response = await _person.PostAsJsonAsync($"/api/plugins/{onto}/replaces/{from}/move", body ?? new { }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string> AgentOfAsync(string team, string member) =>
        (await Services.GetRequiredService<TeamRegistry>().MemberAsync(team, member, Ct)).Agent;

    private async Task<List<TenantEvent>> RowsAsync(string action, string subject)
    {
        var rows = new List<TenantEvent>();
        long? before = null;

        while (true)
        {
            var page = await Services.GetRequiredService<ITenantLog>().ReadAsync(before, ITenantLog.MaxTake, Ct);
            if (page.Events.Count == 0) return rows;
            rows.AddRange(page.Events.Where(e => e.Action == action && e.Subject == subject));
            before = page.Events[^1].Seq;
        }
    }

    private static string[] MemberNames(JsonElement members) =>
        [.. members.EnumerateArray().Select(m => $"{m.GetProperty("teamName").GetString()} / {m.GetProperty("memberName").GetString()}")];

    [Fact]
    public async Task Installing_a_replacement_names_the_old_id_its_version_and_every_member_on_it_and_moves_nothing()
    {
        await HireOnOldAsync(_alpha, "Builder");
        await HireOnOldAsync(_beta, "Deployer");

        var answer = await InstallNewAsync(Old, "never-installed-here");

        Assert.Equal(Name, answer.GetProperty("name").GetString());

        // Only the installed id, with its version and its members, team by team.
        var replaced = Assert.Single(answer.GetProperty("replaces").EnumerateArray());
        Assert.Equal(Old, replaced.GetProperty("id").GetString());
        Assert.Equal("1.1.4", replaced.GetProperty("version").GetString());
        Assert.Equal(Name, replaced.GetProperty("name").GetString());
        Assert.Equal(["Alpha Replaces / Builder", "Beta Replaces / Deployer"], MemberNames(replaced.GetProperty("members")));

        // NOTHING MOVED, NOTHING REMOVED by the install.
        Assert.Equal(MemberRef.ForPlugin(Old), await AgentOfAsync(_alpha, "Builder"));
        Assert.Equal(MemberRef.ForPlugin(Old), await AgentOfAsync(_beta, "Deployer"));
        Assert.NotNull(Services.GetRequiredService<PluginCatalog>().For(Old));

        // The listing says it both ways; the third plugin, which names nothing, is in neither.
        var list = JsonDocument.Parse(await _person.GetStringAsync("/api/plugins", Ct)).RootElement;
        JsonElement Plugin(string id) => list.GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == id);

        var listed = Assert.Single(Plugin(New).GetProperty("replaces").EnumerateArray());
        Assert.Equal(["Alpha Replaces / Builder", "Beta Replaces / Deployer"], MemberNames(listed.GetProperty("members")));
        var by = Assert.Single(Plugin(Old).GetProperty("replacedBy").EnumerateArray());
        Assert.Equal((New, "3.0.0", Name), (by.GetProperty("id").GetString(), by.GetProperty("version").GetString(), by.GetProperty("name").GetString()));
        Assert.Empty(Plugin("fixture-other").GetProperty("replaces").EnumerateArray());
        Assert.Empty(Plugin("fixture-other").GetProperty("replacedBy").EnumerateArray());
        Assert.Empty(Plugin(New).GetProperty("replacedBy").EnumerateArray());

        // THE HIRING LIST TELLS THE TWO SAME-NAMED PLUGINS APART; a name nobody shares reads as before.
        var hiring = JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{_alpha}/hiring", Ct)).RootElement;
        var labels = hiring.GetProperty("plugins").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("label").GetString());
        Assert.Equal($"{Name} (plugin, {Old} 1.1.4)", labels[Old]);
        Assert.Equal($"{Name} (plugin, {New} 3.0.0)", labels[New]);
        Assert.Equal("Other Cloud (plugin)", labels["fixture-other"]);
    }

    [Fact]
    public async Task A_persons_move_keeps_settings_and_own_secrets_writes_member_changed_and_only_then_the_old_plugin_can_go()
    {
        var builderToken = await HireOnOldAsync(_alpha, "Builder");
        var deployerToken = await HireOnOldAsync(_beta, "Deployer");
        await InstallNewAsync(Old);

        var builder = new ContainerId(_alpha, "Builder");
        var settingsBefore = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(builder, Ct);

        // THE OLD PLUGIN IS REFUSED REMOVAL while a member runs it, naming them.
        using (var refused = await _person.DeleteAsync($"/api/plugins/{Old}", Ct))
        {
            var text = await refused.Content.ReadAsStringAsync(Ct);
            Assert.True(refused.StatusCode == HttpStatusCode.Conflict, text);
            Assert.Contains("Builder", text);
        }

        var (status, body) = await MoveAsync(New, Old);
        Assert.True(status == HttpStatusCode.OK, body.ToString());
        Assert.Equal(["moved", "moved"], body.GetProperty("moves").EnumerateArray().Select(m => m.GetProperty("outcome").GetString()));
        Assert.Empty(body.GetProperty("remaining").EnumerateArray());

        Assert.Equal(MemberRef.ForPlugin(New), await AgentOfAsync(_alpha, "Builder"));
        Assert.Equal(MemberRef.ForPlugin(New), await AgentOfAsync(_beta, "Deployer"));

        // SETTINGS KEPT - `legacy`, which the new manifest no longer declares, too - and own secrets kept.
        var settingsAfter = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(builder, Ct);
        Assert.Equal(JsonSerializer.Serialize(settingsBefore.Config), JsonSerializer.Serialize(settingsAfter.Config));
        Assert.Equal("kept-Builder", settingsAfter.Config["legacy"].GetString());
        var secrets = Services.GetRequiredService<PluginMemberSecretStore>();
        Assert.Equal(builderToken, (await secrets.ValuesAsync(builder, Ct))["token"]);
        Assert.Equal(deployerToken, (await secrets.ValuesAsync(new ContainerId(_beta, "Deployer"), Ct))["token"]);

        // ONE member.changed row each, naming from and to.
        foreach (var subject in new[] { $"{_alpha}/Builder", $"{_beta}/Deployer" })
        {
            var detail = JsonDocument.Parse(Assert.Single(await RowsAsync(TenantActions.MemberChanged, subject)).Detail!).RootElement;
            Assert.Equal(MemberRef.ForPlugin(Old), detail.GetProperty("from").GetString());
            Assert.Equal(MemberRef.ForPlugin(New), detail.GetProperty("to").GetString());
            Assert.Equal(Old, detail.GetProperty("replaces").GetString());
        }

        // Still installed until the person removes it; now nothing uses it, so it can go.
        Assert.NotNull(Services.GetRequiredService<PluginCatalog>().For(Old));
        using var removed = await _person.DeleteAsync($"/api/plugins/{Old}", Ct);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Null(Services.GetRequiredService<PluginCatalog>().For(Old));
    }

    [Fact]
    public async Task A_move_of_named_members_moves_only_those()
    {
        await HireOnOldAsync(_alpha, "Builder");
        await HireOnOldAsync(_beta, "Deployer");
        await InstallNewAsync(Old);

        var (status, body) = await MoveAsync(New, Old, new { members = new[] { new { team = _beta, member = "Deployer" } } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Deployer", Assert.Single(body.GetProperty("moves").EnumerateArray()).GetProperty("member").GetString());
        Assert.Equal(["Alpha Replaces / Builder"], MemberNames(body.GetProperty("remaining")));
        Assert.Equal(MemberRef.ForPlugin(Old), await AgentOfAsync(_alpha, "Builder"));
        Assert.Equal(MemberRef.ForPlugin(New), await AgentOfAsync(_beta, "Deployer"));
    }

    [Fact]
    public async Task When_the_member_changed_row_cannot_be_written_the_member_is_not_moved()
    {
        var token = await HireOnOldAsync(_alpha, "Builder");
        await InstallNewAsync(Old);
        var builder = new ContainerId(_alpha, "Builder");
        var settingsBefore = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(builder, Ct);

        await ExecuteAsync(
            "CREATE TRIGGER refuse_row BEFORE INSERT ON tenant_events WHEN NEW.action = 'member.changed' "
            + "BEGIN SELECT RAISE(ABORT, 'the disk is full'); END;");

        var (status, body) = await MoveAsync(New, Old);

        Assert.Equal(HttpStatusCode.OK, status);
        var move = Assert.Single(body.GetProperty("moves").EnumerateArray());
        Assert.Equal("failed", move.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(move.GetProperty("reason").GetString()));
        Assert.Equal(["Alpha Replaces / Builder"], MemberNames(body.GetProperty("remaining")));

        // NOTHING CHANGED: still on the old plugin, settings and own secret as they were, no row.
        Assert.Equal(MemberRef.ForPlugin(Old), await AgentOfAsync(_alpha, "Builder"));
        Assert.Equal(MemberRef.ForPlugin(Old), Services.GetRequiredService<TeamRegistry>().All()
            .Single(t => t.Id == _alpha).Containers.Single(c => c.Id == "Builder").Agent);
        var settingsAfter = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(builder, Ct);
        Assert.Equal(JsonSerializer.Serialize(settingsBefore.Config), JsonSerializer.Serialize(settingsAfter.Config));
        Assert.Equal(token, (await Services.GetRequiredService<PluginMemberSecretStore>().ValuesAsync(builder, Ct))["token"]);
        Assert.Empty(await RowsAsync(TenantActions.MemberChanged, $"{_alpha}/Builder"));

        // Once the row can be written, the same press moves it.
        await ExecuteAsync("DROP TRIGGER refuse_row");
        Assert.Equal("moved", Assert.Single((await MoveAsync(New, Old)).Body.GetProperty("moves").EnumerateArray()).GetProperty("outcome").GetString());
        Assert.Equal(MemberRef.ForPlugin(New), await AgentOfAsync(_alpha, "Builder"));
    }

    [Fact]
    public async Task Only_an_installed_plugin_that_says_it_replaces_the_id_takes_its_members_and_only_by_a_persons_route()
    {
        await HireOnOldAsync(_alpha, "Builder");

        // SAME NAME OR NOT, A PLUGIN WHOSE MANIFEST DOES NOT NAME THE OLD ID takes nothing.
        var (unrelated, refusal) = await MoveAsync("fixture-other", Old);
        Assert.Equal(HttpStatusCode.Conflict, unrelated);
        Assert.Contains("does not say it replaces", refusal.GetProperty("error").GetString());

        var (missing, _) = await MoveAsync(New, Old);
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal(MemberRef.ForPlugin(Old), await AgentOfAsync(_alpha, "Builder"));

        // A PERSON'S ROUTE.
        var route = Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/plugins/{id}/replaces/{old}/move");
        Assert.NotNull(route.Metadata.GetMetadata<HumansOnlyMarker>());
        Assert.Equal(["POST"], route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
