using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// PLG 2: a person upgrades an installed plugin (<c>POST /api/plugins/install</c> with a newer version).
/// Every member hired on it moves onto the new version with its settings kept by field name, and the
/// answer's <c>attention</c> names each member whose saved settings do not fit the new version, with
/// every field that does not - never a value. Any install that is not an upgrade answers <c>[]</c>.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PluginUpgradeAttentionTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string TokenKey = "PLUGIN_UPGRADE_ATTENTION_TOKEN";
    private const string TokenValue = "a-real-secret-value-for-upgrade";
    private const string Id = "upgrade-echo";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-upgrade-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Environment.SetEnvironmentVariable(TokenKey, TokenValue);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _team = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("Upgrades", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(TokenKey, null);
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>1.0.0 declares `mode` and `size`; <paramref name="config"/> replaces that for a later version.</summary>
    private const string FirstConfig = """
        {"mode":{"type":"string","enum":["upper","reverse"],"default":"upper"},
         "size":{"type":"number","min":1,"max":100,"default":10}}
        """;

    private string Built(string version, string config = FirstConfig, string secrets = """{"token":{"description":"A demo credential.","required":false}}""")
    {
        var folder = Path.Combine(_dataRoot, "teams", "t", "repos", "R", "wt_x", Id, "out", version);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        var manifest = PluginInstall.Manifest(Id, version, m =>
        {
            m["config"] = JsonNode.Parse(config);
            m["secrets"] = JsonNode.Parse(secrets);
        });
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), manifest.ToJsonString());
        File.WriteAllText(Path.Combine(folder, "bin", "run"), "#!/bin/sh\ncat >/dev/null; echo '{\"t\":\"result\",\"ok\":true,\"output\":\"ok\"}'\n");
        return folder;
    }

    private async Task<HttpClient> PersonAsync()
    {
        var person = _factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return person;
    }

    private static async Task<JsonElement> InstallAsync(HttpClient client, string path, bool replace = false)
    {
        var response = await client.PostAsJsonAsync("/api/plugins/install", new { path, replace }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.DoesNotContain(TokenValue, text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private async Task HireAsync(HttpClient person, string name, object config)
    {
        var hired = await person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name,
            agent = $"plugin:{Id}",
            config,
            secrets = new { token = TokenKey },
        }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
    }

    private string Plugins => PluginInstall.PluginsRoot(_dataRoot);

    private static (string Member, string[] Fields)[] Attention(JsonElement body) =>
        [.. body.GetProperty("attention").EnumerateArray().Select(a => (
            a.GetProperty("member").GetString()!,
            a.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()!).ToArray()))];

    // ---- the route ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_upgrade_that_adds_a_required_field_and_drops_another_names_each_member_and_both_fields_and_keeps_everything()
    {
        using var person = await PersonAsync();
        Assert.Empty((await InstallAsync(person, Built("1.0.0"))).GetProperty("attention").EnumerateArray());
        await HireAsync(person, "Echo", new { mode = "reverse" });
        await HireAsync(person, "Other", new { mode = "upper", size = 50 });

        // 1.1.0 drops `mode` and adds a required `region` with no default.
        var body = await InstallAsync(person, Built("1.1.0", """
            {"size":{"type":"number","min":1,"max":100,"default":10},
             "region":{"type":"string","required":true}}
            """));

        Assert.True(body.GetProperty("installed").GetBoolean(), body.GetRawText());
        Assert.Equal("1.0.0", body.GetProperty("previous").GetString());

        var attention = Attention(body);
        Assert.Equal(["Echo", "Other"], attention.Select(a => a.Member));
        Assert.All(attention, a => Assert.Equal(["mode", "region"], a.Fields));

        var echo = body.GetProperty("attention").EnumerateArray().First();
        Assert.Equal(_team, echo.GetProperty("team").GetString());
        Assert.Equal("Upgrades", echo.GetProperty("teamName").GetString());
        Assert.Equal("Echo", echo.GetProperty("memberName").GetString());
        var reasons = echo.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("field").GetString()!, f => f.GetProperty("reason").GetString()!);
        Assert.Contains("no longer has this setting", reasons["mode"]);
        Assert.Contains("required", reasons["region"]);
        Assert.DoesNotContain("reverse", body.GetRawText(), StringComparison.Ordinal);

        // 1.1.0 active, 1.0.0 kept on disk inactive, every member still there with its settings.
        Assert.Equal("1.1.0\n", await File.ReadAllTextAsync(Path.Combine(Plugins, Id, PluginCatalog.ActiveFile), Ct));
        Assert.True(Directory.Exists(Path.Combine(Plugins, Id, "1.0.0")));
        Assert.Equal("1.1.0", Services.GetRequiredService<PluginCatalog>().For(Id)!.Manifest.Version);

        var team = Services.GetRequiredService<TeamRegistry>().All().Single(t => t.Id == _team);
        Assert.Contains(team.Containers, c => c.Id == "Echo" && c.Agent == $"plugin:{Id}");
        Assert.Contains(team.Containers, c => c.Id == "Other" && c.Agent == $"plugin:{Id}");

        var stored = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, "Echo"), Ct);
        Assert.Equal("reverse", stored.Config["mode"].GetString());
        Assert.Equal(TokenKey, stored.Secrets["token"]);

        // A run fails naming the field, as before - the member is not stopped or removed.
        var (_, refusal) = PluginMemberRunner.EffectiveConfig(Services.GetRequiredService<PluginCatalog>().For(Id)!.Manifest, stored);
        Assert.Contains("`region`", refusal);
    }

    [Fact]
    public async Task An_upgrade_every_member_fits_answers_no_attention()
    {
        using var person = await PersonAsync();
        await InstallAsync(person, Built("1.0.0"));
        await HireAsync(person, "Echo", new { mode = "reverse", size = 20 });

        var body = await InstallAsync(person, Built("1.1.0", """
            {"mode":{"type":"string","enum":["upper","reverse","lower"],"default":"upper"},
             "size":{"type":"number","min":1,"max":100,"default":10},
             "extra":{"type":"bool","default":false}}
            """));

        Assert.True(body.GetProperty("installed").GetBoolean(), body.GetRawText());
        Assert.Empty(body.GetProperty("attention").EnumerateArray());
    }

    [Fact]
    public async Task A_replace_of_the_same_version_and_a_rollback_to_an_older_one_are_not_upgrades_and_answer_no_attention()
    {
        using var person = await PersonAsync();
        await InstallAsync(person, Built("1.1.0", """{"region":{"type":"string","required":true,"default":"eu"}}"""));
        await HireAsync(person, "Echo", new { region = "us" });

        // Replace 1.1.0 with a build that no longer has `region`: not an upgrade.
        var replaced = await InstallAsync(person, Built("1.1.0", FirstConfig), replace: true);
        Assert.True(replaced.GetProperty("replaced").GetBoolean());
        Assert.Empty(replaced.GetProperty("attention").EnumerateArray());

        // Roll back to 1.0.0, which does not have `region` either: not an upgrade.
        var rolledBack = await InstallAsync(person, Built("1.0.0"));
        Assert.Equal("1.1.0", rolledBack.GetProperty("previous").GetString());
        Assert.Empty(rolledBack.GetProperty("attention").EnumerateArray());
        Assert.Equal("1.0.0\n", await File.ReadAllTextAsync(Path.Combine(Plugins, Id, PluginCatalog.ActiveFile), Ct));
    }

    // ---- the check ---------------------------------------------------------------------------------

    [Fact]
    public void Every_problem_of_a_member_is_gathered_by_field_with_a_sentence_and_never_a_value()
    {
        var (manifest, refusal) = PluginManifest.Parse(PluginInstall.Manifest("checked", "2.0.0", m =>
        {
            m["config"] = JsonNode.Parse("""
                {"mode":{"type":"string","enum":["upper","reverse"]},
                 "size":{"type":"number","min":1,"max":100},
                 "region":{"type":"string","required":true},
                 "zone":{"type":"string","required":true,"default":"a"},
                 "label":{"type":"string"}}
                """);
            m["secrets"] = JsonNode.Parse("""{"token":{"description":"A credential.","required":false}}""");
        }).ToJsonString());
        Assert.Null(refusal);

        var bound = new PluginMemberSettings(
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["mode"] = JsonSerializer.SerializeToElement(7),
                ["size"] = JsonSerializer.SerializeToElement(500),
                ["dropped"] = JsonSerializer.SerializeToElement("secret-looking-value"),
                ["label"] = JsonSerializer.SerializeToElement("fine"),
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "TOKEN_KEY", ["password"] = "PASSWORD_KEY" });

        var problems = PluginUpgradeAttention.Problems(manifest!, bound, ["token", "oldKey"]);

        Assert.Equal(["dropped", "mode", "oldKey", "password", "region", "size"], problems.Select(p => p.Field));
        var reasons = problems.ToDictionary(p => p.Field, p => p.Reason);
        Assert.Contains("must be a string", reasons["mode"]);
        Assert.Contains("at most 100", reasons["size"]);
        Assert.Contains("no value", reasons["region"]);
        Assert.Contains("no longer has this setting", reasons["dropped"]);
        Assert.Contains("bound to a secret", reasons["password"]);
        Assert.Contains("own secret value", reasons["oldKey"]);
        Assert.All(problems, p =>
        {
            Assert.DoesNotContain("secret-looking-value", p.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("PASSWORD_KEY", p.Reason, StringComparison.Ordinal);
        });

        Assert.Empty(PluginUpgradeAttention.Problems(manifest!, new PluginMemberSettings(
            new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["region"] = JsonSerializer.SerializeToElement("eu") },
            new Dictionary<string, string>(StringComparer.Ordinal)), ["token"]));
    }
}
