using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Messaging;
using Harness.Skills;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// STEP 8, PLUGIN SKILLS (assessment §9): an installed plugin's skills are indexed as LOCKED rows of
/// kind <c>plugin</c>, named <c>plugin-&lt;id&gt;[-&lt;name&gt;]</c>, found by <c>skills_search</c>,
/// named in the Manager's roster, listed by <c>hiring</c>, and never in a prompt's
/// "Available skills".
/// </summary>
public sealed class PluginSkillsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("harness-plugin-skills-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Database => Path.Combine(_directory, "messages.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private async Task<SqliteSkillStore> StoreAsync()
    {
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct);
        return new SqliteSkillStore(Database);
    }

    private static SkillDraft Draft(string name, string body = "How to use it.") =>
        new(name, "Use it for things.", ["manager", "member"], body);

    [Theory]
    [InlineData("sample-echo", "sample-echo", "plugin-sample-echo")]
    [InlineData("sample-echo", "plugin-sample-echo", "plugin-sample-echo")]
    [InlineData("sample-echo", "Upper", "plugin-sample-echo-upper")]
    [InlineData("sample-echo", "plugin-sample-echo-upper", "plugin-sample-echo-upper")]
    [InlineData("sample-echo", "manager", "plugin-sample-echo-manager")]
    public void A_plugin_skills_name_is_forced_into_the_plugins_namespace(string id, string declared, string stored)
    {
        Assert.Equal(stored, PluginSkillNames.Forced(id, declared));
    }

    [Fact]
    public async Task The_schema_step_keeps_every_existing_skill_and_its_search()
    {
        await new SchemaMigrator(Database).ApplyAsync([.. SchemaModules.All.Where(s => s.Id != "skill-003")], ct: Ct);

        // Written as skill-002 left the table: no `source` column.
        await using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO skills (id, name, kind, description, roles, body, updated_at, updated_by) VALUES
                  (7, 'zebra-builtin', 'builtin', 'A built-in.', 'member', 'zanzibar', '2026-01-01T00:00:00Z', NULL),
                  (9, 'zebra-custom', 'custom', 'A custom one.', 'member', 'zanzibar too', '2026-01-01T00:00:00Z', 'person');
                """;
            await insert.ExecuteNonQueryAsync(Ct);
        }

        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct);
        var after = new SqliteSkillStore(Database);

        Assert.Equal(9, (await after.GetAsync("zebra-custom", Ct))!.Id);
        Assert.Equal(SkillKind.BuiltIn, (await after.GetAsync("zebra-builtin", Ct))!.Kind);
        Assert.Equal(2, (await after.ListAsync(SkillKindFilter.All, "zanzibar", null, null, 50, Ct)).Count);

        await after.ReplacePluginSkillsAsync("zeta", [Draft("plugin-zeta", "zanzibar three")], DateTimeOffset.UtcNow, Ct);
        Assert.Equal(3, (await after.ListAsync(SkillKindFilter.All, "zanzibar", null, null, 50, Ct)).Count);
    }

    [Fact]
    public async Task Plugin_skills_are_replaced_per_source_and_locked()
    {
        var store = await StoreAsync();
        var at = DateTimeOffset.UtcNow;

        await store.ReplacePluginSkillsAsync("alpha", [Draft("plugin-alpha"), Draft("plugin-alpha-extra")], at, Ct);
        await store.ReplacePluginSkillsAsync("beta", [Draft("plugin-beta")], at, Ct);

        var alpha = (await store.GetAsync("plugin-alpha", Ct))!;
        Assert.Equal(SkillKind.Plugin, alpha.Kind);
        Assert.Equal("alpha", alpha.Source);
        Assert.True(alpha.IsLocked);

        // Replaced, per source: alpha's second skill goes, beta's stays.
        await store.ReplacePluginSkillsAsync("alpha", [Draft("plugin-alpha", "new body")], at, Ct);
        Assert.Null(await store.GetAsync("plugin-alpha-extra", Ct));
        Assert.Equal("new body", (await store.GetAsync("plugin-alpha", Ct))!.Body);
        Assert.NotNull(await store.GetAsync("plugin-beta", Ct));
        Assert.Equal(["alpha", "beta"], await store.PluginSourcesAsync(Ct));

        // A plugin cannot ship a name outside its own namespace.
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplacePluginSkillsAsync("alpha", [Draft("manager")], at, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplacePluginSkillsAsync("alpha", [Draft("plugin-beta")], at, Ct));

        // Locked: not edited, renamed or deleted, and its namespace not taken by a custom skill.
        await Assert.ThrowsAsync<SkillRefusedException>(() => store.UpdateCustomAsync("plugin-alpha", Draft("plugin-alpha"), "p", Ct));
        await Assert.ThrowsAsync<SkillRefusedException>(() => store.DeleteCustomAsync("plugin-alpha", Ct));
        await Assert.ThrowsAsync<SkillRefusedException>(() => store.CreateCustomAsync(Draft("plugin-alpha"), "p", Ct));
        await Assert.ThrowsAsync<SkillRefusedException>(() => store.CreateCustomAsync(Draft("plugin-gamma"), "p", Ct));

        var mine = await store.CreateCustomAsync(Draft("mine"), "p", Ct);
        await Assert.ThrowsAsync<SkillRefusedException>(() => store.UpdateCustomAsync(mine.Name, Draft("plugin-alpha-x"), "p", Ct));

        // Empty removes a source.
        await store.ReplacePluginSkillsAsync("beta", [], at, Ct);
        Assert.Null(await store.GetAsync("plugin-beta", Ct));
    }

    [Fact]
    public void A_skill_file_narrows_its_roles_but_never_widens_them()
    {
        var root = Path.Combine(_directory, "data");
        PluginInstall.Write(root, "roles", manifest: PluginInstall.Manifest("roles", edit: m => m["skills"] = new JsonArray("skills/a.md", "skills/b.md", "skills/c.md")));
        var version = Path.Combine(PluginInstall.PluginsRoot(root), "roles", "0.1.0", "skills");
        Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, "a.md"), "---\nname: roles\ndescription: The main one.\n---\nBody.");
        File.WriteAllText(Path.Combine(version, "b.md"), "---\nname: narrow\ndescription: Managers only.\nroles: manager concierge\n---\nBody.");
        File.WriteAllText(Path.Combine(version, "c.md"), "---\nname: wide\ndescription: Any.\nroles: any\n---\nBody.");

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(root));
        catalog.Rescan();

        var skills = catalog.For("roles")!.Skills.ToDictionary(s => s.Name);
        Assert.Equal(["manager", "member"], skills["plugin-roles"].Roles);
        Assert.Equal(["manager"], skills["plugin-roles-narrow"].Roles);
        Assert.Equal(["manager", "member"], skills["plugin-roles-wide"].Roles);
    }

    [Fact]
    public void A_skill_file_that_cannot_be_read_refuses_the_plugin_by_name()
    {
        var root = Path.Combine(_directory, "data");
        PluginInstall.Write(root, "broken", manifest: PluginInstall.Manifest("broken", edit: m => m["skills"] = new JsonArray("skills/a.md")));
        var skills = Path.Combine(PluginInstall.PluginsRoot(root), "broken", "0.1.0", "skills");
        Directory.CreateDirectory(skills);
        File.WriteAllText(Path.Combine(skills, "a.md"), "no front matter");

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(root));
        catalog.Rescan();

        Assert.Null(catalog.For("broken"));
        Assert.Contains("skills/a.md", Assert.Single(catalog.Refused).Reason);
    }
}

/// <summary>Step 8 on the REAL Host, with sample-echo installed the way docs/plugins.md says.</summary>
public sealed class PluginSkillDiscoveryTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-skills-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Mixed", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private async Task<HttpClient> MemberAsync(string name, IReadOnlySet<string> permits)
    {
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, name).ToString(), PrincipalKind.Container, _team, permits, ct: Ct);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        return client;
    }

    [Fact]
    public async Task A_hired_plugins_skill_is_found_named_listed_absent_and_locked()
    {
        (await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Echo", agent = "plugin:sample-echo" }, Ct))
            .EnsureSuccessStatusCode();

        // INDEXED at load, locked, from the plugin.
        var skill = (await Services.GetRequiredService<ISkillStore>().GetAsync("plugin-sample-echo", Ct))!;
        Assert.Equal(SkillKind.Plugin, skill.Kind);
        Assert.Equal("sample-echo", skill.Source);

        // FOUND BY skills_search, by a Manager and by a member.
        using var manager = await MemberAsync(TeamRegistry.DefaultManagerName, TeamRegistry.ManagerPermits);
        using var worker = await MemberAsync("Dev", new HashSet<string> { Permits.Skills, Permits.Read });
        foreach (var client in new[] { manager, worker })
        {
            var found = await client.GetFromJsonAsync<JsonElement[]>("/api/me/skills?q=deterministic", Ct);
            Assert.Contains(found!, s => s.GetProperty("name").GetString() == "plugin-sample-echo");
        }

        // NAMED IN ITS MANAGER'S ROSTER, with what it is and its skill.
        var prompt = Services.GetRequiredService<ContainerHost>().Find(new ContainerId(_team, TeamRegistry.DefaultManagerName))!.SystemPrompt;
        Assert.Contains(
            "Echo (plugin sample-echo: Deterministic test plugin: transforms each instruction's text (upper-case or reversed) - skill plugin-sample-echo)",
            prompt);

        // LISTED BY hiring.
        using var hiring = JsonDocument.Parse(await manager.GetStringAsync($"/api/teams/{_team}/hiring", Ct));
        var plugin = Assert.Single(hiring.RootElement.GetProperty("plugins").EnumerateArray());
        Assert.Equal("sample-echo", plugin.GetProperty("id").GetString());
        Assert.Equal("plugin:sample-echo", plugin.GetProperty("reference").GetString());
        Assert.Equal("plugin-sample-echo", plugin.GetProperty("skill").GetString());

        // ABSENT FROM EVERY "Available skills" LIST.
        var directory = Services.GetRequiredService<SkillDirectory>();
        foreach (var role in SkillRoles.All)
        {
            Assert.DoesNotContain(directory.For(role), s => s.Name.StartsWith("plugin-", StringComparison.Ordinal));
        }

        Assert.DoesNotContain("- plugin-sample-echo", prompt, StringComparison.Ordinal);

        // REFUSED TO CREATE, EDIT, DELETE OR SHADOW.
        Assert.Equal(HttpStatusCode.Conflict, (await _person.PostAsJsonAsync("/api/skills",
            new { name = "plugin-sample-echo", description = "mine", roles = new[] { "member" }, body = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _person.PostAsJsonAsync("/api/skills",
            new { name = "plugin-sample-echo-extra", description = "mine", roles = new[] { "member" }, body = "x" }, Ct)).StatusCode);
        var edit = await _person.PutAsJsonAsync("/api/skills/plugin-sample-echo", new { body = "changed" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Contains("plugin's skill", await edit.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await _person.DeleteAsync("/api/skills/plugin-sample-echo", Ct)).StatusCode);
        Assert.Equal("plugin", (await _person.GetFromJsonAsync<JsonElement>("/api/skills/plugin-sample-echo", Ct)).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_rescan_indexes_a_newly_installed_plugins_skill_and_drops_an_uninstalled_ones()
    {
        var store = Services.GetRequiredService<ISkillStore>();
        var catalog = Services.GetRequiredService<PluginCatalog>();

        var version = PluginInstall.Write(_dataRoot, "later", manifest: PluginInstall.Manifest("later", edit: m => m["skills"] = new JsonArray("skills/later.md")));
        Directory.CreateDirectory(Path.Combine(version, "skills"));
        File.WriteAllText(Path.Combine(version, "skills", "later.md"), "---\nname: later\ndescription: Installed later.\n---\nBody.");

        Assert.Null(await store.GetAsync("plugin-later", Ct));

        catalog.Rescan();
        Assert.Equal("later", (await store.GetAsync("plugin-later", Ct))!.Source);

        Directory.Delete(Path.Combine(_dataRoot, "plugins", "later"), recursive: true);
        catalog.Rescan();
        Assert.Null(await store.GetAsync("plugin-later", Ct));
        Assert.NotNull(await store.GetAsync("plugin-sample-echo", Ct));
    }
}
