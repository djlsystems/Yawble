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

    private string Built(
        string version, string config = FirstConfig, string secrets = """{"token":{"description":"A demo credential.","required":false}}""",
        string? folder = null, string? connections = null, string id = Id)
    {
        folder ??= Path.Combine(_dataRoot, "teams", "t", "repos", "R", "wt_x", id, "out", version);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        var manifest = PluginInstall.Manifest(id, version, m =>
        {
            m["config"] = JsonNode.Parse(config);
            m["secrets"] = JsonNode.Parse(secrets);
            if (connections is not null) m["connections"] = JsonNode.Parse(connections);
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

    /// <summary>
    /// PLG 3: THE ROLLBACK THE UPGRADE'S WORDS NAME. The copy kept under the plugins folder is refused
    /// (the words no longer point at it); the old version's Marketplace folder,
    /// Documents › Marketplace › &lt;id&gt;-&lt;version&gt;, and the folder it was built into each roll back
    /// with Replace: the old version active, the newer one kept inactive, the member's settings kept.
    /// </summary>
    [Fact]
    public async Task A_rollback_installs_the_old_versions_marketplace_folder_or_build_folder_with_replace_and_never_its_kept_copy()
    {
        using var person = await PersonAsync();
        var documents = Services.GetRequiredService<TeamDocuments>().InstanceRoot;
        var fetched = Built("1.0.0", folder: Path.Combine(documents, Marketplace.Folder, $"{Id}-1.0.0"));
        await InstallAsync(person, fetched);
        await HireAsync(person, "Echo", new { mode = "reverse", size = 7 });

        var upgraded = await InstallAsync(person, Built("1.1.0"));
        Assert.Equal("1.0.0", upgraded.GetProperty("previous").GetString());

        // THE KEPT COPY: refused, as the old words led a person to try.
        var kept = await person.PostAsJsonAsync("/api/plugins/install", new { path = Path.Combine(Plugins, Id, "1.0.0"), replace = true }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, kept.StatusCode);
        Assert.Contains("already under the plugins folder", await kept.Content.ReadAsStringAsync(Ct));
        Assert.Equal("1.1.0\n", await File.ReadAllTextAsync(Path.Combine(Plugins, Id, PluginCatalog.ActiveFile), Ct));

        // THE MARKETPLACE FOLDER, with Replace: rolled back.
        var rolledBack = await InstallAsync(person, fetched, replace: true);
        Assert.Equal(("1.0.0", "1.1.0"), (rolledBack.GetProperty("version").GetString(), rolledBack.GetProperty("previous").GetString()));
        Assert.Equal("1.0.0\n", await File.ReadAllTextAsync(Path.Combine(Plugins, Id, PluginCatalog.ActiveFile), Ct));
        Assert.True(Directory.Exists(Path.Combine(Plugins, Id, "1.1.0")));
        var config = (await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, "Echo"), Ct)).Config;
        Assert.Equal(("reverse", 7), (config["mode"].GetString(), config["size"].GetInt32()));

        // THE BUILD FOLDER, after upgrading again: rolled back the same way.
        await InstallAsync(person, Built("1.1.0"), replace: true);
        var fromBuild = await InstallAsync(person, Built("1.0.0"), replace: true);
        Assert.Equal("1.1.0", fromBuild.GetProperty("previous").GetString());
        Assert.Equal("1.0.0\n", await File.ReadAllTextAsync(Path.Combine(Plugins, Id, PluginCatalog.ActiveFile), Ct));
    }

    // ---- a save after an upgrade ------------------------------------------------------------------

    /// <summary>1.0.0 as Built, with a second optional secret `appSecret1` that 1.1.0 drops.</summary>
    private const string TwoSecrets = """
        {"token":{"description":"A demo credential.","required":false},
         "appSecret1":{"description":"An app secret a later version drops.","required":false}}
        """;

    private async Task<HttpResponseMessage> SaveAsync(HttpClient person, string member, object body) =>
        await person.PutAsJsonAsync($"/api/teams/{_team}/members/{member}/plugin-settings", body, Ct);

    /// <summary>
    /// AN UPGRADE THAT DROPS A BOUND SECRET leaves the member saveable. The settings form's Save omits
    /// `secrets`, so the Host keeps the stored bindings - including `appSecret1`, which 1.1.0 no longer
    /// declares. That kept binding is kept and not used, as the upgrade's attention says, and it does
    /// not refuse the save, as a stored value for a dropped config field does not. A secret 1.1.0 still
    /// declares is still checked, and a binding the save itself introduces for an undeclared secret is
    /// still refused.
    /// </summary>
    [Fact]
    public async Task An_upgrade_that_drops_a_bound_secret_leaves_a_settings_save_that_omits_secrets_accepted()
    {
        using var person = await PersonAsync();
        await InstallAsync(person, Built("1.0.0", secrets: TwoSecrets));
        var hired = await person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo",
            agent = $"plugin:{Id}",
            config = new { mode = "reverse" },
            secrets = new { token = TokenKey, appSecret1 = TokenKey },
        }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));

        // 1.1.0 declares `token` only.
        var upgraded = await InstallAsync(person, Built("1.1.0"));
        Assert.Contains(Attention(upgraded), a => a.Member == "Echo" && a.Fields.Contains("appSecret1"));

        // THE FORM'S SAVE: config only, no `secrets` - accepted, the stored bindings kept as they were.
        var saved = await SaveAsync(person, "Echo", new { config = new { mode = "upper" } });
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        var stored = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, "Echo"), Ct);
        Assert.Equal("upper", stored.Config["mode"].GetString());
        Assert.Equal(TokenKey, stored.Secrets["token"]);
        Assert.Equal(TokenKey, stored.Secrets["appSecret1"]);

        // The same bindings sent back unchanged are accepted too.
        var sentBack = await SaveAsync(person, "Echo", new { config = new { mode = "upper" }, secrets = new { token = TokenKey, appSecret1 = TokenKey } });
        Assert.True(sentBack.StatusCode == HttpStatusCode.OK, await sentBack.Content.ReadAsStringAsync(Ct));

        // A SECRET 1.1.0 STILL DECLARES is still checked: a key of the wrong form is refused.
        var badKey = await SaveAsync(person, "Echo", new { config = new { mode = "upper" }, secrets = new { token = "not a key", appSecret1 = TokenKey } });
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);

        // A BINDING THIS SAVE INTRODUCES for a secret 1.1.0 does not declare is refused, naming it -
        // a new name, and a dropped name bound to another key alike.
        var introduced = await SaveAsync(person, "Echo", new { config = new { mode = "upper" }, secrets = new { token = TokenKey, appSecret2 = TokenKey } });
        Assert.Equal(HttpStatusCode.BadRequest, introduced.StatusCode);
        Assert.Contains("`appSecret2` is not a secret plugin", await introduced.Content.ReadAsStringAsync(Ct));

        var rebound = await SaveAsync(person, "Echo", new { config = new { mode = "upper" }, secrets = new { token = TokenKey, appSecret1 = "OTHER_KEY" } });
        Assert.Equal(HttpStatusCode.BadRequest, rebound.StatusCode);
        Assert.Contains("`appSecret1` is not a secret plugin", await rebound.Content.ReadAsStringAsync(Ct));

        // Nothing refused changed what is stored.
        stored = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, "Echo"), Ct);
        Assert.Equal(TokenKey, stored.Secrets["appSecret1"]);
        Assert.False(stored.Secrets.ContainsKey("appSecret2"));
    }

    /// <summary>
    /// AN UPGRADE THAT DROPS A BOUND CONNECTION SLOT - here its only one, so the form sends no
    /// `connections` and the Host keeps the stored binding - leaves the member saveable by the same
    /// rule; a binding the save introduces for a slot 1.1.0 does not declare is still refused.
    /// </summary>
    [Fact]
    public async Task An_upgrade_that_drops_a_bound_connection_slot_leaves_a_settings_save_that_omits_connections_accepted()
    {
        using var person = await PersonAsync();
        var mailId = $"conn-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await Services.GetRequiredService<ConnectionStore>().InsertMailboxAsync(
            new ConnectionRecord(mailId, "Shop mail", MailboxSettings.Kind, "shop@example.test", [], now, now, ConnectionRecord.Ok, null),
            new MailboxSettings("shop@example.test", new MailServer("imap.example.test", 993, "TLS"), new MailServer("smtp.example.test", 465, "TLS"), "other", true),
            "shop-app-password-0001", Email, new TriggerAudit(null, Email, TenantActions.ConnectionConnected, mailId, null, null), Ct);

        await InstallAsync(person, Built("1.0.0", connections: """{"mail":{"providers":["imap"],"required":false}}"""));
        var hired = await person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo",
            agent = $"plugin:{Id}",
            config = new { mode = "reverse" },
            secrets = new { token = TokenKey },
            connections = new { mail = mailId },
        }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));

        await InstallAsync(person, Built("1.1.0"));

        var saved = await SaveAsync(person, "Echo", new { config = new { mode = "upper" } });
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        var stored = await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, "Echo"), Ct);
        Assert.Equal(mailId, stored.Connections["mail"]);

        var introduced = await SaveAsync(person, "Echo", new { config = new { mode = "upper" }, connections = new { mail = mailId, inbox = mailId } });
        Assert.Equal(HttpStatusCode.BadRequest, introduced.StatusCode);
        Assert.Contains("`inbox` is not a connection slot", await introduced.Content.ReadAsStringAsync(Ct));
    }

    // ---- connection bindings across an upgrade -----------------------------------------------------

    /// <summary>
    /// A SAVE KEEPS EVERY CONNECTION OF A MULTIPLE SLOT THE PERSON DID NOT REMOVE (message 26265's shape):
    /// `appSecrets` holds a mailbox and an API key (Other). A save naming only the API key - what a
    /// single-select form sent - keeps both; a save naming both keeps both. Only an explicit removal
    /// (`removedConnections`) unbinds one, and the other stays. The settings answer the slot as multiple.
    /// </summary>
    [Fact]
    public async Task A_save_keeps_both_connections_of_a_multiple_slot_unless_the_person_removes_one()
    {
        using var person = await PersonAsync();
        var (mail, key) = await MailboxAndApiKeyAsync();
        await InstallAsync(person, Built("1.0.0", connections: AppSecrets));
        await HireBoundAsync(person, new Dictionary<string, string> { ["appSecrets"] = mail, ["appSecrets#2"] = key });
        var both = new Dictionary<string, string> { ["appSecrets"] = mail, ["appSecrets#2"] = key };

        var shown = await person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/members/Echo/plugin-settings", Ct);
        Assert.True(shown.GetProperty("connectionFields").GetProperty("appSecrets").GetProperty("multiple").GetBoolean());
        Assert.Empty(shown.GetProperty("unusableConnections").EnumerateArray());

        // Only the one just picked, as the single-select dropdown sent it: the mailbox stays bound.
        var one = await SaveAsync(person, "Echo", new { config = new { mode = "reverse" }, connections = new { appSecrets = key } });
        Assert.True(one.StatusCode == HttpStatusCode.OK, await one.Content.ReadAsStringAsync(Ct));
        Assert.Equal(both, await BindingsAsync());

        // Both named, in either order: both stay.
        var two = await SaveAsync(person, "Echo", new
        {
            config = new { mode = "reverse" },
            connections = new Dictionary<string, string> { ["appSecrets"] = key, ["appSecrets#2"] = mail },
        });
        Assert.True(two.StatusCode == HttpStatusCode.OK, await two.Content.ReadAsStringAsync(Ct));
        Assert.Equal(both, await BindingsAsync());

        // `{}` names no connection and removes none: a multiple slot is unbound only by removing.
        var none = await SaveAsync(person, "Echo", new { config = new { mode = "reverse" }, connections = new { } });
        Assert.True(none.StatusCode == HttpStatusCode.OK, await none.Content.ReadAsStringAsync(Ct));
        Assert.Equal(both, await BindingsAsync());

        // An explicit removal of the mailbox keeps the API key.
        var removed = await SaveAsync(person, "Echo", new
        {
            config = new { mode = "reverse" },
            connections = new { appSecrets = key },
            removedConnections = new { appSecrets = new[] { mail } },
        });
        Assert.True(removed.StatusCode == HttpStatusCode.OK, await removed.Content.ReadAsStringAsync(Ct));
        Assert.Equal(new Dictionary<string, string> { ["appSecrets"] = key }, await BindingsAsync());
    }

    /// <summary>A mailbox and an API-key connection (preset Other), as a team's appSecrets slot binds them.</summary>
    private async Task<(string Mail, string Key)> MailboxAndApiKeyAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var mailId = $"conn-{Guid.NewGuid():N}";
        var keyId = $"conn-{Guid.NewGuid():N}";
        var store = Services.GetRequiredService<ConnectionStore>();
        await store.InsertMailboxAsync(
            new ConnectionRecord(mailId, "Cloud mailbox", MailboxSettings.Kind, "noreply@example.test", [], now, now, ConnectionRecord.Ok, null),
            new MailboxSettings("noreply@example.test", new MailServer("imap.example.test", 993, "TLS"), new MailServer("smtp.example.test", 465, "TLS"), "other", true),
            "mailbox-app-password-0001", Email, new TriggerAudit(null, Email, TenantActions.ConnectionConnected, mailId, null, null), Ct);
        await store.InsertCredentialAsync(
            new ConnectionRecord(keyId, "Cloud sign-in", ConnectionKinds.ApiKey, "Cloud sign-in", [], now, null, ConnectionRecord.Ok, null,
                Credential: new CredentialSettings(CredentialPresets.Other,
                [
                    new CredentialField("microsoft", "microsoft", true, null, true),
                    new CredentialField("google", "google", true, null, true),
                ])),
            new Dictionary<string, string> { ["microsoft"] = "microsoft-secret-0001", ["google"] = "google-secret-0001" },
            Email, new TriggerAudit(null, Email, TenantActions.ConnectionConnected, keyId, null, null), Ct);
        return (mailId, keyId);
    }

    private const string AppSecrets = """{"appSecrets":{"providers":["imap","login","apikey","website"],"multiple":true,"required":false}}""";

    private async Task HireBoundAsync(HttpClient person, object connections)
    {
        var hired = await person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo",
            agent = $"plugin:{Id}",
            config = new { mode = "reverse" },
            secrets = new { token = TokenKey },
            connections,
        }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
    }

    private async Task<IReadOnlyDictionary<string, string>> BindingsAsync() =>
        (await Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, "Echo"), Ct)).Connections;

    /// <summary>
    /// A MULTIPLE SLOT'S BINDINGS SURVIVE AN UPGRADE (the azure-plugin 3.2.0 -> 3.3.0 shape): a mailbox
    /// under `appSecrets` and an API key under `appSecrets#2`, the slot unchanged but for its words. Both
    /// stay bound, whatever their kind; the upgrade names nothing and nothing holds the member on Needs
    /// you. Upgrading ANOTHER plugin (namecheap's shape) leaves them as they were too.
    /// </summary>
    [Fact]
    public async Task An_upgrade_keeps_every_connection_bound_to_a_multiple_slot_that_still_takes_it_whatever_its_kind()
    {
        using var person = await PersonAsync();
        var (mail, key) = await MailboxAndApiKeyAsync();
        await InstallAsync(person, Built("1.0.0", connections: AppSecrets));
        await InstallAsync(person, Built("1.0.0", id: "other-echo"));
        await HireBoundAsync(person, new Dictionary<string, string> { ["appSecrets"] = mail, ["appSecrets#2"] = key });

        var upgraded = await InstallAsync(person, Built("1.1.0", connections: AppSecrets.Replace("\"multiple\"", "\"description\":\"Now also for secret set.\",\"multiple\"")));
        Assert.Empty(Attention(upgraded));
        Assert.Equal(new Dictionary<string, string> { ["appSecrets"] = mail, ["appSecrets#2"] = key }, await BindingsAsync());
        Assert.Empty(Services.GetRequiredService<PluginSetup>().Holds());

        await InstallAsync(person, Built("1.1.0", id: "other-echo"));
        Assert.Equal(new Dictionary<string, string> { ["appSecrets"] = mail, ["appSecrets#2"] = key }, await BindingsAsync());
        Assert.Empty(Services.GetRequiredService<PluginSetup>().Holds());
    }

    /// <summary>
    /// A BINDING THE NEW VERSION CANNOT USE IS KEPT AND SAID AT ONCE: 1.1.0 drops slot `mail` and its
    /// `appSecrets` no longer takes an API key. Both bindings stay stored; the upgrade's answer names
    /// `appSecrets#2` and `mail` in words; each connection holds the member on Needs you with the same
    /// words. A person's save that unbinds them clears both holds.
    /// </summary>
    [Fact]
    public async Task An_upgrade_that_cannot_use_a_bound_connection_keeps_it_and_says_so_on_the_answer_and_on_Needs_you()
    {
        using var person = await PersonAsync();
        var (mail, key) = await MailboxAndApiKeyAsync();
        var withMail = AppSecrets.Replace("}}", "},\"mail\":{\"providers\":[\"imap\"],\"required\":false}}");
        await InstallAsync(person, Built("1.0.0", connections: withMail));
        await HireBoundAsync(person, new Dictionary<string, string> { ["appSecrets"] = mail, ["appSecrets#2"] = key, ["mail"] = mail });

        var upgraded = await InstallAsync(person, Built("1.1.0", connections: """{"appSecrets":{"providers":["imap"],"multiple":true,"required":false}}"""));

        var echo = Assert.Single(Attention(upgraded));
        Assert.Equal(["appSecrets#2", "mail"], echo.Fields);
        var words = upgraded.GetProperty("attention")[0].GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("field").GetString()!, f => f.GetProperty("reason").GetString()!);
        Assert.Contains("'Cloud sign-in'", words["appSecrets#2"]);
        Assert.Contains($"slot `appSecrets` of {Id} 1.1.0 no longer takes a", words["appSecrets#2"]);
        Assert.Contains($"{Id} 1.1.0 no longer has the connection slot `mail`", words["mail"]);
        Assert.All(words.Values, w => Assert.Contains("kept and not used", w));
        Assert.Equal(new Dictionary<string, string> { ["appSecrets"] = mail, ["appSecrets#2"] = key, ["mail"] = mail }, await BindingsAsync());

        // NEEDS YOU, at once: each connection the member cannot use holds it, with the same words.
        var holds = Services.GetRequiredService<PluginSetup>().Holds();
        Assert.Equal(new[] { key, mail }.Order(StringComparer.Ordinal), holds.Select(h => h.Id).Order(StringComparer.Ordinal));
        Assert.All(holds, h => Assert.Equal([new ContainerId(_team, "Echo")], h.Members));
        Assert.Equal(words["appSecrets#2"], holds.Single(h => h.Id == key).Sentence);

        // THE MEMBER'S SETTINGS say it too, where the person is looking when they open them.
        var shown = await person.GetFromJsonAsync<JsonElement>($"/api/teams/{_team}/members/Echo/plugin-settings", Ct);
        var unusable = shown.GetProperty("unusableConnections").EnumerateArray()
            .ToDictionary(u => u.GetProperty("key").GetString()!, u => u.GetProperty("reason").GetString()!);
        Assert.Equal(words, unusable);

        var needsYou = await person.GetFromJsonAsync<JsonElement>("/api/needs-you", Ct);
        var items = needsYou.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "connection").ToList();
        Assert.Contains(items, i => i.GetProperty("connection").GetProperty("id").GetString() == key
            && i.GetProperty("words").GetString() == words["appSecrets#2"]);

        // A PERSON'S SAVE that unbinds them clears the holds.
        var saved = await SaveAsync(person, "Echo", new
        {
            config = new { mode = "reverse" },
            connections = new { appSecrets = mail },
            removedConnections = new { appSecrets = new[] { key } },
        });
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        Assert.Empty(Services.GetRequiredService<PluginSetup>().Holds());
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
