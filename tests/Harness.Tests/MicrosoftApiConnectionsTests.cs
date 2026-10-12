using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Web;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// ONE MICROSOFT CONNECTION, SEVERAL APIS, on the real Host with a FAKED Microsoft - no test calls
/// a real one. A person signs in once with a slot's scopes spanning Azure management, Azure SQL and
/// Azure Storage; the bound member's run gets one token per API on stdin, each redeemed from the one
/// refresh token and refreshed on its own, a rotation stored first; an API the account is refused
/// blocks the run naming it; no token reaches a response, a row or a file; and a one-API slot's stdin
/// is today's with <c>tokens</c> added.
/// </summary>
public sealed class MicrosoftApiConnectionsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string ClientSecret = "msecret-never-leaves-the-host-4Rv";
    private const string Account = "ops@contoso.example";
    private const string MicrosoftClientId = "8f3c2a71-0b6e-4d2a-9c11-5e7f4a2b9d03";

    private const string Management = "https://management.azure.com/";
    private const string Sql = "https://database.windows.net/";
    private const string Storage = "https://storage.azure.com/";
    private const string ManagementScope = Management + "user_impersonation";
    private const string SqlScope = Sql + "user_impersonation";
    private const string StorageScope = Storage + "user_impersonation";
    private const string GraphMail = "https://graph.microsoft.com/Mail.Read";

    private static readonly string[] AzureScopes = [ManagementScope, SqlScope, StorageScope];

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-msapis-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-msapis-out-{Guid.NewGuid():N}");
    private readonly FakeMicrosoft _microsoft = new();
    private readonly Clock _clock = new();
    private readonly List<string> _responses = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private HttpClient _anonymous = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Requests(string plugin) => Path.Combine(_outside, $"requests-{plugin}.jsonl");

    private string Environment => Path.Combine(_outside, "environment.txt");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        // Each plugin writes the request it was handed OUTSIDE the data root, exactly as it read it,
        // and prints it too: redaction is what keeps the tokens out of what the Host stores.
        Install("azure", "azure", AzureScopes);
        Install("graphmail", "mail", [GraphMail]);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IOAuthEndpoints>(_microsoft);
                services.AddSingleton(sp => new Connections(
                    sp.GetRequiredService<ConnectionStore>(), _microsoft, _clock, sp.GetRequiredService<IUserStore>(),
                    delay: (wait, _) =>
                    {
                        _clock.Advance(wait);
                        return Task.CompletedTask;
                    }));
            }));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Ops", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        _anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        // Microsoft under Advanced: the redirect flow, with a secret.
        var saved = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft",
            new { clientId = "advanced-client", clientSecret = ClientSecret, tenant = "contoso.onmicrosoft.com" });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        _anonymous.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { _dataRoot, _outside })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private void Install(string id, string slot, IReadOnlyList<string> scopes) =>
        PluginInstall.Write(_dataRoot, id,
            script: $"env >> '{Environment}'; printf '%s\\n' \"$0 $*\" >> '{Environment}'; req=$(cat); printf '%s\\n' \"$req\" >> '{Requests(id)}'; printf '%s\\n' \"$req\"; echo '{{\"t\":\"result\",\"ok\":true,\"output\":\"read\"}}'",
            manifest: PluginInstall.Manifest(id, edit: m => m["connections"] = new JsonObject
            {
                [slot] = new JsonObject
                {
                    ["description"] = "The account.",
                    ["providers"] = new JsonArray("microsoft"),
                    ["scopes"] = new JsonObject { ["microsoft"] = new JsonArray([.. scopes.Select(s => (JsonNode?)s)]) },
                    ["required"] = true,
                },
            }));

    // ---- one sign-in, three tokens ------------------------------------------------------------------

    [Fact]
    public async Task A_slot_naming_three_apis_gives_a_bound_plugin_run_three_tokens_on_stdin_from_one_sign_in()
    {
        // ONE SIGN-IN: its authorization request names every API's scopes.
        var start = await StartAsync(AzureScopes);
        var asked = HttpUtility.ParseQueryString(new Uri(start.GetProperty("authorizationUrl").GetString()!).Query)["scope"]!.Split(' ');
        Assert.All(AzureScopes, s => Assert.Contains(s, asked));

        // Microsoft's token answers name the first API only; the connection still holds every API's scopes.
        _microsoft.ExchangeScope = ManagementScope + " openid offline_access";
        var id = await CallbackAsync(start.GetProperty("state").GetString()!);
        Assert.Single(_microsoft.Exchanges);

        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.All(AzureScopes, s => Assert.Contains(s, listed.GetProperty("scopes").EnumerateArray().Select(e => e.GetString())));

        await HireAsync("Azure", "azure", "azure", id);
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Azure")).Type);

        var slot = LastRequest("azure")["connections"]!["azure"]!;
        var tokens = slot["tokens"]!.AsObject();
        Assert.Equal([Management, Sql, Storage], tokens.Select(t => t.Key));

        // Each API's token is the one Microsoft issued for that API, redeemed with that API's scopes.
        foreach (var (api, scope) in new[] { (Management, ManagementScope), (Sql, SqlScope), (Storage, StorageScope) })
        {
            var issued = Assert.Single(_microsoft.Issued, i => i.Api == api);
            Assert.Equal(issued.Access, (string?)tokens[api]!["accessToken"]);
            Assert.NotNull((string?)tokens[api]!["expiresAt"]);
            Assert.Equal([scope], tokens[api]!["scopes"]!.AsArray().Select(s => (string?)s));
            Assert.Equal([scope, "offline_access"], Assert.Single(_microsoft.Refreshes, r => r.Api == api).Scopes);
        }

        Assert.Equal(3, tokens.Select(t => (string?)t.Value!["accessToken"]).Distinct().Count());

        // `accessToken` is the first API's, as a one-API slot's is its only one.
        Assert.Equal((string?)tokens[Management]!["accessToken"], (string?)slot["accessToken"]);
        Assert.Equal((string?)tokens[Management]!["expiresAt"], (string?)slot["expiresAt"]);
        Assert.Null(slot["refreshToken"]);

        // Every one of them on stdin only, and redacted from what the run left behind.
        var environment = File.ReadAllText(Environment);
        foreach (var issued in _microsoft.Issued)
        {
            Assert.DoesNotContain(issued.Access, environment);
            Assert.DoesNotContain(issued.Refresh, environment);
        }

        // Within their life the tokens are reused: a second run redeems nothing.
        var redeemed = _microsoft.Refreshes.Count;
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Azure")).Type);
        Assert.Equal(redeemed, _microsoft.Refreshes.Count);
    }

    [Fact]
    public async Task Each_api_is_refreshed_on_its_own_and_a_rotation_is_stored_before_any_token_is_handed_out()
    {
        _microsoft.Rotates = true;
        _microsoft.ExpiresIn[Sql] = 1800;
        var id = await ConnectAsync(AzureScopes);
        await HireAsync("Azure", "azure", "azure", id);

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Azure")).Type);
        var first = LastRequest("azure")["connections"]!["azure"]!["tokens"]!.AsObject();
        Assert.Equal(3, _microsoft.Refreshes.Count);

        // Each redemption presented the refresh token the one before it rotated and the Host stored.
        Assert.Equal(_microsoft.Issued[0].Refresh, _microsoft.Refreshes[0].RefreshToken);
        Assert.Equal(_microsoft.Issued[1].Refresh, _microsoft.Refreshes[1].RefreshToken);
        Assert.Equal(_microsoft.Issued[2].Refresh, _microsoft.Refreshes[2].RefreshToken);
        var store = Services.GetRequiredService<ConnectionStore>();
        Assert.Equal(_microsoft.Issued[^1].Refresh, (await store.TokensAsync(id, Ct))!.RefreshToken);

        // 26 minutes on, Azure SQL's 30-minute token is inside the 5-minute window; the others are not.
        _clock.Advance(TimeSpan.FromMinutes(26));
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Azure")).Type);
        var second = LastRequest("azure")["connections"]!["azure"]!["tokens"]!.AsObject();

        Assert.Equal(4, _microsoft.Refreshes.Count);
        Assert.Equal(Sql, _microsoft.Refreshes[^1].Api);
        Assert.NotEqual((string?)first[Sql]!["accessToken"], (string?)second[Sql]!["accessToken"]);
        Assert.Equal((string?)first[Management]!["accessToken"], (string?)second[Management]!["accessToken"]);
        Assert.Equal((string?)first[Storage]!["accessToken"], (string?)second[Storage]!["accessToken"]);
        Assert.Equal(_microsoft.Issued[^1].Refresh, (await store.TokensAsync(id, Ct))!.RefreshToken);

        // STORED BEFORE HANDED OUT: when the rotation cannot be written, no run gets any token.
        await ExecuteAsync("CREATE TRIGGER refuse_refresh BEFORE UPDATE ON connections BEGIN SELECT RAISE(ABORT, 'refused by the test'); END");
        var linesBefore = File.ReadAllLines(Requests("azure")).Length;

        _clock.Advance(TimeSpan.FromMinutes(26));
        var refused = await TellAndAwaitAsync("Azure");

        Assert.Equal(MessageTypes.Failed, refused.Type);
        Assert.Contains("could not be stored", Words(refused));
        Assert.Equal(linesBefore, File.ReadAllLines(Requests("azure")).Length);

        await ExecuteAsync("DROP TRIGGER refuse_refresh");

        // The next redemption presents the refresh token the Host did store.
        var stored = (await store.TokensAsync(id, Ct))!.RefreshToken;
        var redeemed = _microsoft.Refreshes.Count;
        _clock.Advance(TimeSpan.FromMinutes(26));
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Azure")).Type);
        Assert.Equal(stored, _microsoft.Refreshes[redeemed].RefreshToken);
    }

    [Fact]
    public async Task Two_members_sharing_a_connection_redeem_each_api_once()
    {
        _microsoft.Rotates = true;
        _microsoft.RefreshDelay = TimeSpan.FromMilliseconds(100);
        var id = await ConnectAsync(AzureScopes);

        var connections = Services.GetRequiredService<Connections>();
        var grants = await Task.WhenAll(
            connections.GrantAsync(id, "azure", Ct, AzureScopes),
            connections.GrantAsync(id, "azure", Ct, AzureScopes));

        Assert.All(grants, g => Assert.Null(g.Refusal));
        Assert.Equal(3, _microsoft.Refreshes.Count);
        Assert.Equal(grants[0].Grant!.Tokens!.Select(t => t.AccessToken), grants[1].Grant!.Tokens!.Select(t => t.AccessToken));
    }

    // ---- failures -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_api_the_account_is_refused_blocks_the_run_naming_the_connection_the_api_and_where_to_fix_it()
    {
        var id = await ConnectAsync(AzureScopes);
        await HireAsync("Azure", "azure", "azure", id);

        _microsoft.RefuseApi[Sql] = ("invalid_grant", "AADSTS65001: The user or administrator has not consented to use the application.");
        var row = await TellAndAwaitAsync("Azure");

        Assert.Equal(MessageTypes.Failed, row.Type);
        var reason = Words(row);
        Assert.Contains($"Connection '{Account}' cannot get a token for Azure SQL ({Sql})", reason);
        Assert.Contains("AADSTS65001", reason);
        Assert.Contains("Admin → Connections", reason);
        Assert.False(File.Exists(Requests("azure")));

        // The API's refusal is not the refresh token's: the connection stays connected.
        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal("ok", listed.GetProperty("status").GetString());
        Assert.DoesNotContain(TenantActions.ConnectionNeedsReconnect, await TenantActionsAsync());

        // Granted, the next run gets all three.
        _microsoft.RefuseApi.Clear();
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Azure")).Type);
        Assert.Equal(3, LastRequest("azure")["connections"]!["azure"]!["tokens"]!.AsObject().Count);
    }

    [Fact]
    public async Task A_refused_refresh_token_marks_the_connection_needs_reconnect_and_blocks_the_run()
    {
        var id = await ConnectAsync(AzureScopes);
        await HireAsync("Azure", "azure", "azure", id);

        _microsoft.RefuseAll = ("invalid_grant", "AADSTS70008: The provided authorization code or refresh token has expired.");
        var row = await TellAndAwaitAsync("Azure");

        Assert.Equal(MessageTypes.Failed, row.Type);
        var reason = Words(row);
        Assert.Contains($"Connection '{Account}' needs to be reconnected", reason);
        Assert.Contains("Reconnect it from Admin → Connections", reason);

        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal("needs-reconnect", listed.GetProperty("status").GetString());
        Assert.Contains(TenantActions.ConnectionNeedsReconnect, await TenantActionsAsync());
    }

    [Fact]
    public async Task A_binding_checks_every_apis_scopes_and_names_the_api_of_each_one_missing()
    {
        // Signed in for two of the three APIs.
        var id = await ConnectAsync([ManagementScope, StorageScope]);

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Azure",
            agent = "plugin:azure",
            connections = new Dictionary<string, string> { ["azure"] = id },
        }, Ct);
        var body = await hired.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, hired.StatusCode);
        Assert.Contains($"`{SqlScope}` (Azure SQL)", body);
        Assert.DoesNotContain(ManagementScope, body);
        Assert.DoesNotContain(StorageScope, body);
    }

    // ---- no token anywhere --------------------------------------------------------------------------

    [Fact]
    public async Task No_api_token_or_refresh_token_appears_in_a_response_row_diagnostic_or_file()
    {
        _microsoft.Rotates = true;
        var id = await ConnectAsync(AzureScopes);
        await HireAsync("Azure", "azure", "azure", id);

        var first = await TellAndAwaitAsync("Azure");
        _clock.Advance(TimeSpan.FromHours(2));
        var second = await TellAndAwaitAsync("Azure");

        // The plugin DID print every token: redaction is what keeps them out.
        var printed = File.ReadAllText(Requests("azure"));
        Assert.All(_microsoft.Issued.Where(i => i.Api is not null), i => Assert.Contains(i.Access, printed));
        Assert.Contains("[redacted]", second.Payload, StringComparison.OrdinalIgnoreCase);

        await GetAsync("/api/connections/providers");
        await GetAsync("/api/connections");
        await GetAsync("/api/connections/needs?provider=microsoft");
        await GetAsync("/api/plugins");
        await GetAsync($"/api/teams/{_team}/hiring");
        await GetAsync($"/api/teams/{_team}/members/Azure/plugin-settings");
        await GetAsync($"/api/teams/{_team}/members/Azure/runs");
        await GetAsync("/api/diagnostics");
        await GetAsync("/api/tenant-log");

        string[] secrets = [ClientSecret, .. _microsoft.Issued.Select(t => t.Access), .. _microsoft.Issued.Select(t => t.Refresh)];

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(_responses, r => r.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, first.Payload);
            Assert.DoesNotContain(secret, second.Payload);
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(PluginInstall.PluginsRoot(_dataRoot), StringComparison.Ordinal)) continue;

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            var text = Encoding.Latin1.GetString(bytes);
            foreach (var secret in secrets)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{Path.GetRelativePath(_dataRoot, file)} holds a credential in plain text.");
            }
        }
    }

    // ---- needs, words and the plugin check ----------------------------------------------------------

    [Theory]
    [InlineData("https://management.azure.com/user_impersonation", Management)]
    [InlineData("https://management.azure.com/.default", Management)]
    [InlineData("https://database.windows.net//.default", Sql)]
    [InlineData("https://database.windows.net/user_impersonation", Sql)]
    [InlineData("https://storage.azure.com/user_impersonation", Storage)]
    [InlineData("https://vault.azure.net/user_impersonation", "https://vault.azure.net/")]
    [InlineData("https://api.loganalytics.io/Data.Read", "https://api.loganalytics.io/")]
    [InlineData("api://8f3c2a71-0b6e-4d2a-9c11-5e7f4a2b9d03/access_as_user", "api://8f3c2a71-0b6e-4d2a-9c11-5e7f4a2b9d03/")]
    [InlineData(GraphMail, MicrosoftApis.Graph)]
    [InlineData("Mail.Read", MicrosoftApis.Graph)]
    [InlineData("offline_access", MicrosoftApis.Graph)]
    [InlineData("openid", MicrosoftApis.Graph)]
    public void A_scopes_api_is_the_part_before_its_last_path_segment_and_a_bare_scope_is_graphs(string scope, string api) =>
        Assert.Equal(api, MicrosoftApis.ApiOf(scope));

    [Fact]
    public void The_sign_in_scopes_go_with_every_api_and_are_graphs_token_only_when_nothing_else_is_named()
    {
        Assert.Equal(new[] { Management, Sql }, MicrosoftApis.Tokens(["openid", "offline_access", ManagementScope, SqlScope]).Select(a => a.Api));
        Assert.Equal(new[] { MicrosoftApis.Graph, Management }, MicrosoftApis.Tokens(["openid", GraphMail, ManagementScope]).Select(a => a.Api));
        Assert.Equal(new[] { MicrosoftApis.Graph }, MicrosoftApis.Tokens(["openid", "offline_access"]).Select(a => a.Api));
        Assert.False(MicrosoftApis.Several(["openid", "offline_access", GraphMail]));
        Assert.True(MicrosoftApis.Several([GraphMail, ManagementScope]));
    }

    [Fact]
    public async Task The_needs_list_each_microsoft_apis_scopes_in_words_and_google_is_unchanged()
    {
        var needs = await GetAsync("/api/connections/needs?provider=microsoft");
        var apis = needs.GetProperty("microsoftApis").EnumerateArray().ToList();

        Assert.Equal(new[] { Management, Sql, Storage, MicrosoftApis.Graph }, apis.Select(a => a.GetProperty("api").GetString()));
        Assert.Equal(new[] { "Azure management", "Azure SQL", "Azure Storage", "Microsoft Graph" }, apis.Select(a => a.GetProperty("name").GetString()));

        string? Words(int api) => Assert.Single(apis[api].GetProperty("scopes").EnumerateArray()).GetProperty("words").GetString();
        Assert.Equal("Manage your Azure resources as you", Words(0));
        Assert.Equal("Use your Azure SQL databases as you", Words(1));
        Assert.Equal("Read and change your Azure Storage data as you", Words(2));
        Assert.Equal("Read your mail", Words(3));
        Assert.Equal(new[] { "azure" }, apis[0].GetProperty("scopes")[0].GetProperty("plugins").EnumerateArray().Select(p => p.GetString()));

        // The words table has Key Vault and Log Analytics too; a scope it does not know is null, never guessed.
        Assert.Equal("Use your Key Vault keys, secrets and certificates as you", ConnectionNeeds.WordsFor("https://vault.azure.net/user_impersonation"));
        Assert.Equal("Read your Log Analytics data", ConnectionNeeds.WordsFor("https://api.loganalytics.io/Data.Read"));
        Assert.Equal("Key Vault", MicrosoftApis.NameOf("https://vault.azure.net/"));
        Assert.Equal("Log Analytics", MicrosoftApis.NameOf("https://api.loganalytics.io/"));
        Assert.Null(ConnectionNeeds.WordsFor("https://management.azure.com/.default"));

        // One slot answers its own APIs.
        var slot = await GetAsync("/api/connections/needs?provider=microsoft&plugin=graphmail&slot=mail");
        Assert.Equal(new[] { MicrosoftApis.Graph }, slot.GetProperty("microsoftApis").EnumerateArray().Select(a => a.GetProperty("api").GetString()));

        // Google's answer is as it was: no Microsoft APIs in it.
        Assert.False((await GetAsync("/api/connections/needs?provider=google")).TryGetProperty("microsoftApis", out _));
    }

    [Fact]
    public async Task The_plugin_check_names_each_api_a_slot_needs_a_token_for()
    {
        var plugins = (await GetAsync("/api/plugins")).GetProperty("plugins").EnumerateArray().ToList();

        var azure = plugins.Single(p => p.GetProperty("id").GetString() == "azure").GetProperty("connections").GetProperty("azure");
        Assert.Equal("needs a Microsoft connection for Azure management, Azure SQL and Azure Storage", azure.GetProperty("summary").GetString());
        Assert.Equal(new[] { Management, Sql, Storage }, azure.GetProperty("microsoftApis").EnumerateArray().Select(a => a.GetProperty("api").GetString()));
        Assert.Equal(new[] { SqlScope }, azure.GetProperty("microsoftApis")[1].GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));

        // One API: the line is as it was.
        var mail = plugins.Single(p => p.GetProperty("id").GetString() == "graphmail").GetProperty("connections").GetProperty("mail");
        Assert.Equal("needs a Microsoft connection", mail.GetProperty("summary").GetString());
    }

    // ---- one API, as before -------------------------------------------------------------------------

    [Fact]
    public async Task A_single_api_slots_stdin_is_byte_identical_to_todays_but_for_the_added_tokens()
    {
        var id = await ConnectAsync([GraphMail]);
        await HireAsync("Mail", "graphmail", "mail", id);

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Mail")).Type);

        // One API: no redemption with scopes, the sign-in's own token, as before.
        Assert.Empty(_microsoft.Refreshes);
        var issued = Assert.Single(_microsoft.Issued);

        var line = File.ReadAllLines(Requests("graphmail")).Last(l => l.Length > 0);
        var slot = JsonNode.Parse(line)!["connections"]!["mail"]!;
        var expiresAt = (string)slot["expiresAt"]!;
        var scopes = slot["scopes"]!.ToJsonString();
        Assert.Equal(["openid", "email", "offline_access", GraphMail], slot["scopes"]!.AsArray().Select(s => (string?)s));

        // TODAY'S SLOT, member for member and byte for byte, then `tokens` as its last member.
        var today = $"{{\"provider\":\"microsoft\",\"account\":\"{Account}\",\"accessToken\":\"{issued.Access}\",\"expiresAt\":\"{expiresAt}\",\"scopes\":{scopes}";
        var tokens = $"\"tokens\":{{\"https://graph.microsoft.com/\":{{\"accessToken\":\"{issued.Access}\",\"expiresAt\":\"{expiresAt}\",\"scopes\":{scopes}}}}}";
        Assert.Contains($"\"mail\":{today},{tokens}}}", line);
    }

    // ---- the sign-in with a code --------------------------------------------------------------------

    [Fact]
    public async Task A_sign_in_with_a_code_names_every_apis_scopes_and_names_the_first_only_where_microsoft_refuses_several()
    {
        var (status, body) = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft", new { clientId = MicrosoftClientId, audience = "common" });
        Assert.True(status == HttpStatusCode.OK, body);

        // Where Microsoft takes every API's scopes in one sign-in, it is asked once, naming all.
        var flowId = await StartDeviceAsync(AzureScopes);
        Assert.All(AzureScopes, s => Assert.Contains(s, Assert.Single(_microsoft.DeviceStarts)));
        _microsoft.DeviceAnswers.Writer.TryWrite("approve");
        var id = (await FlowAsync(flowId, until: "done")).GetProperty("connection").GetProperty("id").GetString()!;

        var (grant, refusal) = await Services.GetRequiredService<Connections>().GrantAsync(id, "azure", Ct, AzureScopes);
        Assert.Null(refusal);
        Assert.Equal([Management, Sql, Storage], grant!.Tokens!.Select(t => t.Api));

        // Where it refuses a sign-in naming several, the sign-in names the first API's and the run
        // redeems the others: the person still signs in once.
        _microsoft.RefuseSeveralApisAtSignIn = true;
        _microsoft.DeviceStarts.Clear();
        var second = await StartDeviceAsync(AzureScopes, name: "Second");
        Assert.Equal(2, _microsoft.DeviceStarts.Count);
        Assert.DoesNotContain(SqlScope, _microsoft.DeviceStarts[1]);
        Assert.Contains(ManagementScope, _microsoft.DeviceStarts[1]);
        _microsoft.DeviceAnswers.Writer.TryWrite("approve");
        var secondId = (await FlowAsync(second, until: "done")).GetProperty("connection").GetProperty("id").GetString()!;

        var connection = (await Services.GetRequiredService<ConnectionStore>().GetAsync(secondId, Ct))!;
        Assert.All(AzureScopes, s => Assert.Contains(s, connection.Scopes));
        (grant, refusal) = await Services.GetRequiredService<Connections>().GrantAsync(secondId, "azure", Ct, AzureScopes);
        Assert.Null(refusal);
        Assert.Equal(3, grant!.Tokens!.Count);
    }

    [Fact]
    public async Task A_several_api_code_microsoft_will_not_redeem_finishes_on_a_second_code_naming_the_first_api_and_the_run_gets_a_token_per_api()
    {
        var (status, body) = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft", new { clientId = MicrosoftClientId, audience = "common" });
        Assert.True(status == HttpStatusCode.OK, body);
        _microsoft.RefuseSeveralApisAtPoll = true;

        // The first code names every API's scopes, so the person consents to all of them at once.
        // The poll starts with the flow and may already have asked for the second code by the time
        // the start answers, so this reads the first code and leaves the count for after the wait.
        var flowId = await StartDeviceAsync(AzureScopes, name: "Estate");
        var first = _microsoft.Starts()[0];
        Assert.All(AzureScopes, s => Assert.Contains(s, first));

        // Its poll is refused for naming more than one resource: the same flow waits on a second code
        // naming the first API's scopes and the sign-in scopes only.
        var waiting = await FlowAsync(flowId, until: "waiting", userCode: "U2-WXYZ");
        var starts = _microsoft.Starts();
        Assert.Equal(2, starts.Count);
        var second = starts[1];
        Assert.Contains(ManagementScope, second);
        Assert.DoesNotContain(SqlScope, second);
        Assert.DoesNotContain(StorageScope, second);
        Assert.All(first.Where(ConnectionProviders.IdentityScopes.Contains), s => Assert.Contains(s, second));
        Assert.Equal("https://microsoft.com/devicelogin", waiting.GetProperty("verificationUri").GetString());
        Assert.Equal(Connections.SecondCodeSentence("Microsoft", "https://microsoft.com/devicelogin"), waiting.GetProperty("sentence").GetString());
        Assert.DoesNotContain("AADSTS", waiting.GetProperty("sentence").GetString());

        // The open list shows the code to enter now.
        var open = Assert.Single((await GetAsync("/api/connections/flows/open")).EnumerateArray());
        Assert.Equal("U2-WXYZ", open.GetProperty("userCode").GetString());
        Assert.Equal("waiting", open.GetProperty("state").GetString());

        // Approved, it stores ONE connection with every API's scopes asked, with its tenant row.
        _microsoft.DeviceAnswers.Writer.TryWrite("approve");
        var done = await FlowAsync(flowId, until: "done");
        var id = done.GetProperty("connection").GetProperty("id").GetString()!;
        var connection = (await Services.GetRequiredService<ConnectionStore>().GetAsync(id, Ct))!;
        Assert.Equal("Estate", connection.Name);
        Assert.All(AzureScopes, s => Assert.Contains(s, connection.Scopes));
        Assert.Single(await TenantActionsAsync(), a => a == TenantActions.ConnectionConnected);
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());

        // Never a third code; the first code was polled once.
        Assert.Equal(2, _microsoft.DeviceStarts.Count);
        var codes = _microsoft.DeviceCodes.Keys.ToList();
        Assert.Equal(1, _microsoft.DevicePolls.Count(p => p == codes[0]));

        // A bound plugin run gets a token per API, redeemed from the one refresh token.
        await HireAsync("Azure", "azure", "azure", id);
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Azure")).Type);
        var tokens = LastRequest("azure")["connections"]!["azure"]!["tokens"]!.AsObject();
        Assert.Equal([Management, Sql, Storage], tokens.Select(t => t.Key));
        Assert.Equal(3, tokens.Select(t => (string?)t.Value!["accessToken"]).Distinct().Count());

        // Neither device code reaches an answer, a row or a file.
        await AssertNowhereAsync(codes);
    }

    [Fact]
    public async Task A_second_code_microsoft_refuses_or_that_expires_ends_the_flow_and_stores_nothing()
    {
        var (status, body) = await SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft", new { clientId = MicrosoftClientId, audience = "common" });
        Assert.True(status == HttpStatusCode.OK, body);
        _microsoft.RefuseSeveralApisAtPoll = true;

        // The second code expires: the flow ends expired.
        var expiring = await StartDeviceAsync(AzureScopes);
        await FlowAsync(expiring, until: "waiting", userCode: "U2-WXYZ");
        _microsoft.DeviceAnswers.Writer.TryWrite("expire");
        var expired = await FlowAsync(expiring, until: "expired");
        Assert.Contains("Nothing was stored", expired.GetProperty("sentence").GetString());
        Assert.Equal(2, _microsoft.DeviceStarts.Count);

        // The second authorization is refused: the flow ends refused, and no third is asked.
        _microsoft.RefuseSecondCode = true;
        _microsoft.DeviceStarts.Clear();
        _microsoft.DeviceCodes.Clear();
        var refusedFlow = await StartDeviceAsync(AzureScopes);
        var refused = await FlowAsync(refusedFlow, until: "refused");
        Assert.Contains("did not start the second code", refused.GetProperty("sentence").GetString());
        Assert.Contains("Nothing was stored", refused.GetProperty("sentence").GetString());
        Assert.Equal(2, _microsoft.DeviceStarts.Count);

        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Empty((await GetAsync("/api/connections/flows/open")).EnumerateArray());
        Assert.DoesNotContain(TenantActions.ConnectionConnected, await TenantActionsAsync());
        Assert.Empty(await Services.GetRequiredService<ConnectionStore>().ListAsync(Ct));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    /// <summary>None of <paramref name="secrets"/> in any answer the test read, nor in any file under
    /// the data root.</summary>
    private async Task AssertNowhereAsync(IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(_responses, r => r.Contains(secret, StringComparison.Ordinal));
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            var text = Encoding.Latin1.GetString(bytes);
            foreach (var secret in secrets)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{Path.GetRelativePath(_dataRoot, file)} holds a device code.");
            }
        }
    }

    private async Task<string> StartDeviceAsync(IReadOnlyList<string> scopes, string? name = null)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/start", new { provider = "microsoft", scopes, name, flow = "device" });
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("flowId").GetString()!;
    }

    private async Task<JsonElement> FlowAsync(string flowId, string until, string? userCode = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (true)
        {
            var flow = await GetAsync($"/api/connections/flows/{flowId}");
            if (flow.GetProperty("state").GetString() == until
                && (userCode is null || flow.GetProperty("userCode").GetString() == userCode)) return flow;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The flow stayed {flow.GetProperty("state").GetString()}, not {until}.");
            await Task.Delay(20, Ct);
        }
    }

    private async Task<string> ConnectAsync(IReadOnlyList<string> scopes)
    {
        var start = await StartAsync(scopes);
        return await CallbackAsync(start.GetProperty("state").GetString()!);
    }

    private async Task<JsonElement> StartAsync(IReadOnlyList<string> scopes)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/start", new { provider = "microsoft", scopes });
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<string> CallbackAsync(string state)
    {
        using var response = await _anonymous.GetAsync($"{Connections.CallbackPath}?state={Uri.EscapeDataString(state)}&code={FakeMicrosoft.GoodCode}", Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        _responses.Add(location);
        Assert.Contains("connection=connected", location);
        return HttpUtility.ParseQueryString(new Uri(new Uri("http://host"), location).Query)["id"]!;
    }

    private async Task HireAsync(string name, string plugin, string slot, string connectionId)
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name,
            agent = $"plugin:{plugin}",
            connections = new Dictionary<string, string> { [slot] = connectionId },
        }, Ct);
        var body = await hired.Content.ReadAsStringAsync(Ct);
        _responses.Add(body);
        Assert.True(hired.StatusCode == HttpStatusCode.OK, body);
    }

    private async Task<Message> TellAndAwaitAsync(string member)
    {
        var who = new ContainerId(_team, member);
        var log = Services.GetRequiredService<IMessageLog>();

        async Task<IReadOnlyList<Message>> Rows() =>
            [.. (await log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct)).Where(m => m.Source == who.ToString())];

        var after = (await Rows()).Select(m => m.Seq).DefaultIfEmpty(0).Max();

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{member}/tell", new { instruction = "check the estate" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Rows()).FirstOrDefault(m => m.Seq > after) is { } row)
            {
                var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
                while (host.Find(who)!.State != ContainerState.Idle && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
                return row;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{who} wrote no terminal row.");
    }

    private static string Words(Message row)
    {
        static IEnumerable<string> Strings(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => [e.GetString()!],
            JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Strings(p.Value)),
            JsonValueKind.Array => e.EnumerateArray().SelectMany(Strings),
            _ => [],
        };

        return string.Join("\n", Strings(JsonDocument.Parse(row.Payload).RootElement));
    }

    private JsonObject LastRequest(string plugin) => JsonNode.Parse(File.ReadAllLines(Requests(plugin)).Last(l => l.Length > 0))!.AsObject();

    private async Task<JsonElement> GetAsync(string path)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, path, null);
        Assert.True(status == HttpStatusCode.OK, $"{path}: {status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _person.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        _responses.Add(text);
        return (response.StatusCode, text);
    }

    private async Task<IReadOnlyList<string>> TenantActionsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT action FROM tenant_events ORDER BY seq";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>A clock the test moves; the rest of the Host keeps the system's.</summary>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// MICROSOFT, FAKED, as its token endpoint behaves: one API's token per request. The code
    /// exchange answers <see cref="ExchangeScope"/>; a refresh naming scopes answers a token for
    /// their API, expiring per <see cref="ExpiresIn"/> (60 minutes otherwise), rotating the refresh
    /// token when asked, and refusing an API in <see cref="RefuseApi"/> or every refresh with
    /// <see cref="RefuseAll"/>. The device endpoint refuses scopes of several APIs when
    /// <see cref="RefuseSeveralApisAtSignIn"/> is set; with <see cref="RefuseSeveralApisAtPoll"/> it
    /// takes them and refuses to redeem that code (AADSTS28000), as a real tenant does, and with
    /// <see cref="RefuseSecondCode"/> it refuses every authorization after the first.
    /// </summary>
    private sealed class FakeMicrosoft : IOAuthEndpoints
    {
        public const string GoodCode = "good-code";

        public string? ExchangeScope;
        public bool Rotates;
        public bool RefuseSeveralApisAtSignIn;
        public TimeSpan RefreshDelay = TimeSpan.Zero;
        public readonly Dictionary<string, int> ExpiresIn = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (string Error, string Description)> RefuseApi = new(StringComparer.Ordinal);
        public (string Error, string Description)? RefuseAll;

        /// <summary>Every token issued, with the API it is for (null for the sign-in's own).</summary>
        public readonly List<(string Access, string Refresh, string? Api)> Issued = [];
        public readonly List<string> Exchanges = [];
        public readonly List<(string RefreshToken, IReadOnlyList<string>? Scopes, string? Api)> Refreshes = [];
        public readonly List<IReadOnlyList<string>> DeviceStarts = [];
        public bool RefuseSeveralApisAtPoll;
        public bool RefuseSecondCode;

        /// <summary>Every device code issued, with the scopes it named, and every poll's code.</summary>
        public readonly Dictionary<string, IReadOnlyList<string>> DeviceCodes = new(StringComparer.Ordinal);
        public readonly List<string> DevicePolls = [];
        public readonly Channel<string> DeviceAnswers = Channel.CreateUnbounded<string>();

        private readonly Lock _lock = new();

        /// <summary>The device authorizations asked so far, read under the lock the poll writes them under.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Starts()
        {
            lock (_lock) return [.. DeviceStarts];
        }

        public Task<OAuthTokenAnswer> ExchangeCodeAsync(
            OAuthProvider provider, string? clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct)
        {
            lock (_lock)
            {
                if (code != GoodCode) return Task.FromResult(new OAuthTokenAnswer(null, null, null, null, null, "invalid_grant", "Bad code.", true));

                Exchanges.Add(code);
                var (access, refresh) = Issue(null, newRefresh: true);
                return Task.FromResult(new OAuthTokenAnswer(access, refresh, 3600, ExchangeScope, IdToken(), null, null, false));
            }
        }

        public async Task<OAuthTokenAnswer> RefreshAsync(
            OAuthProvider provider, string? clientSecret, string refreshToken, CancellationToken ct, IReadOnlyList<string>? scopes = null)
        {
            if (RefreshDelay > TimeSpan.Zero) await Task.Delay(RefreshDelay, ct);

            lock (_lock)
            {
                var api = scopes?.Where(s => !ConnectionProviders.IdentityScopes.Contains(s)).Select(MicrosoftApis.ApiOf).FirstOrDefault();
                Refreshes.Add((refreshToken, scopes, api));

                if (RefuseAll is { } all) return new OAuthTokenAnswer(null, null, null, null, null, all.Error, all.Description, true);
                if (api is not null && RefuseApi.TryGetValue(api, out var refused))
                {
                    return new OAuthTokenAnswer(null, null, null, null, null, refused.Error, refused.Description, true);
                }

                var (access, refresh) = Issue(api, newRefresh: Rotates, current: refreshToken);
                return new OAuthTokenAnswer(
                    access, Rotates ? refresh : null, api is not null && ExpiresIn.TryGetValue(api, out var seconds) ? seconds : 3600,
                    scopes is null ? null : string.Join(' ', scopes), null, null, null, false);
            }
        }

        public Task<DeviceAuthorizationAnswer> DeviceAuthorizationAsync(OAuthProvider provider, IReadOnlyList<string> scopes, CancellationToken ct)
        {
            lock (_lock)
            {
                DeviceStarts.Add(scopes);

                if (RefuseSeveralApisAtSignIn && MicrosoftApis.Several(scopes))
                {
                    return Task.FromResult(new DeviceAuthorizationAnswer(null, null, null, null, null, "invalid_scope",
                        "AADSTS70011: The provided value for the input parameter 'scope' is not valid."));
                }

                if (RefuseSecondCode && DeviceCodes.Count > 0)
                {
                    return Task.FromResult(new DeviceAuthorizationAnswer(null, null, null, null, null, "invalid_client",
                        "AADSTS700016: Application was not found in the directory."));
                }

                var code = $"devicecode-{Guid.NewGuid():N}";
                DeviceCodes[code] = scopes;
                return Task.FromResult(new DeviceAuthorizationAnswer(
                    code, $"U{DeviceStarts.Count}-WXYZ", "https://microsoft.com/devicelogin", 900, 5, null, null));
            }
        }

        public async Task<OAuthTokenAnswer> DeviceTokenAsync(OAuthProvider provider, string? clientSecret, string deviceCode, CancellationToken ct)
        {
            lock (_lock)
            {
                DevicePolls.Add(deviceCode);

                // AS MEASURED ON A REAL TENANT: the consent to every API is taken at the code, and
                // redeeming that code is refused for naming more than one resource.
                if (RefuseSeveralApisAtPoll && DeviceCodes.TryGetValue(deviceCode, out var named) && MicrosoftApis.Several(named))
                {
                    return new OAuthTokenAnswer(null, null, null, null, null, "invalid_request",
                        "AADSTS28000: Provided value for the input parameter scope is not valid because it contains more than one resource. Scope "
                        + string.Join(' ', named) + " is not valid.", true);
                }
            }

            var answer = await DeviceAnswers.Reader.ReadAsync(ct);

            lock (_lock)
            {
                if (answer == "expire") return new OAuthTokenAnswer(null, null, null, null, null, "expired_token", "AADSTS70020: The provided authorization code or refresh token has expired.", true);
                if (answer != "approve") return new OAuthTokenAnswer(null, null, null, null, null, "authorization_pending", null, true);

                var (access, refresh) = Issue(null, newRefresh: true);
                return new OAuthTokenAnswer(access, refresh, 3600, null, IdToken(), null, null, false);
            }
        }

        public Task<JsonElement?> UserInfoAsync(OAuthProvider provider, string accessToken, CancellationToken ct) =>
            Task.FromResult<JsonElement?>(null);

        public Task<string?> RevokeAsync(OAuthProvider provider, string token, CancellationToken ct) =>
            Task.FromResult<string?>("the provider has no revocation endpoint");

        private (string Access, string Refresh) Issue(string? api, bool newRefresh, string? current = null)
        {
            var access = $"eyJ0eXAi.access-{Guid.NewGuid():N}";
            var refresh = newRefresh || current is null ? $"0.AXcA-refresh-{Guid.NewGuid():N}" : current;
            Issued.Add((access, refresh, api));
            return (access, refresh);
        }

        private static string IdToken()
        {
            static string Part(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            return $"{Part(new { alg = "none" })}.{Part(new { preferred_username = Account, sub = "1234" })}.";
        }
    }
}
