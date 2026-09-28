using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// CONNECTIONS, on the real Host with a FAKED provider - no test calls a real one. A person
/// sets Google's client, connects an account through the web flow (start, the provider's redirect
/// back to the callback), binds it to a plugin member, and the member's run receives a fresh access
/// token on stdin; refreshes, rotation, a refused refresh, the binding rules, the callback's refusals
/// and the no-leak rule are each pinned here.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ConnectionsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string ClientSecret = "gsecret-never-leaves-the-host-7Qx";
    private const string MailScope = "https://mail.google.com/";
    private const string Account = "mailbox@example.com";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-connections-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-connections-out-{Guid.NewGuid():N}");
    private readonly FakeProvider _provider = new();
    private readonly Clock _clock = new();
    private readonly List<string> _responses = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private HttpClient _anonymous = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Requests => Path.Combine(_outside, "requests.jsonl");

    /// <summary>What the plugin saw in its environment and its argv, run by run.</summary>
    private string Environment => Path.Combine(_outside, "environment.txt");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        // A PLUGIN WITH ONE SLOT, that writes the request it was handed OUTSIDE the data root (so the
        // no-leak scan does not find the plugin's own copy) and ALSO prints it: a plugin echoing its
        // token must have it redacted from everything the Host stores.
        PluginInstall.Write(_dataRoot, "mailer",
            script: $"env >> '{Environment}'; printf '%s\\n' \"$0 $*\" >> '{Environment}'; req=$(cat); printf '%s\\n' \"$req\" >> '{Requests}'; printf '%s\\n' \"$req\"; echo '{{\"t\":\"result\",\"ok\":true,\"output\":\"read\"}}'",
            manifest: PluginInstall.Manifest("mailer", edit: m => m["connections"] = JsonNode.Parse(
                """{"mail":{"description":"The mailbox.","providers":["google","microsoft"],"scopes":{"google":["MAIL_SCOPE"]},"required":true}}"""
                    .Replace("MAIL_SCOPE", MailScope))));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IOAuthEndpoints>(_provider);
                services.AddSingleton(sp => new Connections(
                    sp.GetRequiredService<ConnectionStore>(), _provider, _clock, sp.GetRequiredService<IUserStore>()));
            }));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Mail", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        // The provider's redirect carries no session: the cookie is SameSite=Strict.
        _anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var saved = await SendAsync(HttpMethod.Put, "/api/connections/providers/google", new { clientId = "client-123.apps.googleusercontent.com", clientSecret = ClientSecret });
        Assert.Equal(HttpStatusCode.OK, saved.Status);
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

    // ---- web flow, end to end ---------------------------------------------------------------------

    [Fact]
    public async Task A_google_account_connected_through_the_web_flow_gives_a_bound_plugin_run_its_access_token()
    {
        var start = await StartAsync([MailScope]);
        var url = new Uri(start.GetProperty("authorizationUrl").GetString()!);
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.StartsWith("https://accounts.google.com/", url.ToString());
        Assert.Equal("client-123.apps.googleusercontent.com", query["client_id"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Contains(MailScope, query["scope"]!.Split(' '));
        Assert.EndsWith(Connections.CallbackPath, query["redirect_uri"]);
        Assert.DoesNotContain(ClientSecret, url.ToString());

        var id = await CallbackAsync(start.GetProperty("state").GetString()!);

        // The Host did the exchange, with the client secret and the verifier it kept.
        var exchange = Assert.Single(_provider.Exchanges);
        Assert.Equal(ClientSecret, exchange.Secret);
        Assert.Equal(query["redirect_uri"], exchange.RedirectUri);
        Assert.Equal(query["code_challenge"], Challenge(exchange.Verifier));

        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal(id, listed.GetProperty("id").GetString());
        Assert.Equal(Account, listed.GetProperty("account").GetString());
        Assert.Equal("ok", listed.GetProperty("status").GetString());
        Assert.Contains(MailScope, listed.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));

        await HireAsync("Inbox", id);

        var row = await TellAndAwaitAsync("Inbox");
        Assert.Equal(MessageTypes.Completed, row.Type);

        var request = LastRequest();
        var slot = request["connections"]!["mail"]!;
        Assert.Equal("google", (string?)slot["provider"]);
        Assert.Equal(Account, (string?)slot["account"]);
        Assert.Equal(_provider.Issued[0].Access, (string?)slot["accessToken"]);
        Assert.NotNull((string?)slot["expiresAt"]);
        Assert.Contains(MailScope, slot["scopes"]!.AsArray().Select(s => (string?)s));
        Assert.Null(slot["refreshToken"]);

        // ON STDIN ONLY: never in the child's environment or its argv.
        var environment = File.ReadAllText(Environment);
        Assert.DoesNotContain(_provider.Issued[0].Access, environment);
        Assert.DoesNotContain(_provider.Issued[0].Refresh, environment);
        Assert.DoesNotContain(ClientSecret, environment);

        // Within its life the token is reused: no refresh.
        Assert.Empty(_provider.Refreshes);
    }

    [Fact]
    public async Task A_second_run_past_expiry_gets_a_refreshed_token()
    {
        var id = await ConnectAsync();
        await HireAsync("Inbox", id);

        await TellAndAwaitAsync("Inbox");
        var first = (string?)LastRequest()["connections"]!["mail"]!["accessToken"];

        // 58 minutes on: the 60-minute token is inside the 5-minute early window.
        _clock.Advance(TimeSpan.FromMinutes(58));
        await TellAndAwaitAsync("Inbox");
        var second = (string?)LastRequest()["connections"]!["mail"]!["accessToken"];

        Assert.NotEqual(first, second);
        var refresh = Assert.Single(_provider.Refreshes);
        Assert.Equal(_provider.Issued[0].Refresh, refresh.RefreshToken);
        Assert.Equal(ClientSecret, refresh.Secret);
        Assert.Equal(_provider.Issued[^1].Access, second);

        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, listed.GetProperty("refreshedAt").ValueKind);
    }

    [Fact]
    public async Task A_rotating_providers_new_refresh_token_is_stored_before_the_run_starts()
    {
        _provider.Rotates = true;
        var id = await ConnectAsync();
        await HireAsync("Inbox", id);

        _clock.Advance(TimeSpan.FromHours(2));
        await TellAndAwaitAsync("Inbox");

        var store = Services.GetRequiredService<ConnectionStore>();
        Assert.Equal(_provider.Issued[^1].Refresh, (await store.TokensAsync(id, Ct))!.RefreshToken);
        Assert.NotEqual(_provider.Issued[0].Refresh, _provider.Issued[^1].Refresh);

        // STORED BEFORE HANDED OUT: when the rotation cannot be written, no run gets the token.
        await ExecuteAsync("CREATE TRIGGER refuse_refresh BEFORE UPDATE ON connections BEGIN SELECT RAISE(ABORT, 'refused by the test'); END");
        var linesBefore = File.ReadAllLines(Requests).Length;

        _clock.Advance(TimeSpan.FromHours(2));
        var refused = await TellAndAwaitAsync("Inbox");

        Assert.Equal(MessageTypes.Failed, refused.Type);
        Assert.Contains("could not be stored", Words(refused));
        Assert.Equal(linesBefore, File.ReadAllLines(Requests).Length);

        await ExecuteAsync("DROP TRIGGER refuse_refresh");

        // The next refresh presents the refresh token the Host did store.
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Inbox")).Type);
        Assert.Equal(_provider.Issued[1].Refresh, _provider.Refreshes[^1].RefreshToken);
    }

    [Fact]
    public async Task Two_members_sharing_a_connection_refresh_it_once()
    {
        _provider.Rotates = true;
        _provider.RefreshDelay = TimeSpan.FromMilliseconds(300);
        var id = await ConnectAsync();
        _clock.Advance(TimeSpan.FromHours(2));

        var connections = Services.GetRequiredService<Connections>();
        var grants = await Task.WhenAll(
            connections.GrantAsync(id, "mail", Ct),
            connections.GrantAsync(id, "mail", Ct));

        Assert.All(grants, g => Assert.Null(g.Refusal));
        Assert.Single(_provider.Refreshes);
        Assert.Equal(grants[0].Grant!.AccessToken, grants[1].Grant!.AccessToken);
    }

    [Fact]
    public async Task No_agent_credential_reaches_a_connections_route()
    {
        var id = await ConnectAsync();
        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, await ManagerKeyAsync());

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/connections"),
            (HttpMethod.Get, "/api/connections/providers"),
            (HttpMethod.Post, "/api/connections/start"),
            (HttpMethod.Post, "/api/connections/complete"),
            (HttpMethod.Delete, $"/api/connections/{id}"),
            (HttpMethod.Patch, $"/api/connections/{id}"),
            (HttpMethod.Put, "/api/connections/providers/google"),
            (HttpMethod.Delete, "/api/connections/providers/custom-acme"),
        })
        {
            using var request = new HttpRequestMessage(method, path);
            if (method != HttpMethod.Get && method != HttpMethod.Delete) request.Content = JsonContent.Create(new { });
            using var response = await manager.SendAsync(request, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {response.StatusCode}");
        }

        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
    }

    // ---- refused refresh --------------------------------------------------------------------------

    [Fact]
    public async Task A_refused_refresh_marks_the_connection_and_blocks_the_run_until_a_reconnect_clears_it()
    {
        var id = await ConnectAsync();
        await HireAsync("Inbox", id);

        _provider.RefuseRefresh = true;
        _clock.Advance(TimeSpan.FromHours(2));
        var linesBefore = File.Exists(Requests) ? File.ReadAllLines(Requests).Length : 0;

        var row = await TellAndAwaitAsync("Inbox");
        Assert.Equal(MessageTypes.Failed, row.Type);

        var reason = Words(row);
        Assert.Contains($"Connection '{Account}' needs to be reconnected", reason);
        Assert.Contains("invalid_grant: Token has been expired or revoked.", reason);
        Assert.Contains("Reconnect it from Admin", reason);
        Assert.Equal(linesBefore, File.Exists(Requests) ? File.ReadAllLines(Requests).Length : 0);

        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal("needs-reconnect", listed.GetProperty("status").GetString());
        Assert.Equal("invalid_grant: Token has been expired or revoked.", listed.GetProperty("statusReason").GetString());
        Assert.Contains(await TenantActionsAsync(), a => a == TenantActions.ConnectionNeedsReconnect);

        // A second run does not ask the provider again: it says the same thing.
        var refreshesSoFar = _provider.Refreshes.Count;
        Assert.Equal(MessageTypes.Failed, (await TellAndAwaitAsync("Inbox")).Type);
        Assert.Equal(refreshesSoFar, _provider.Refreshes.Count);

        // RECONNECT: the same account, and the status is ok again.
        _provider.RefuseRefresh = false;
        var reconnect = await StartAsync([], reconnectId: id);
        var location = await CallbackLocationAsync(reconnect.GetProperty("state").GetString()!);
        Assert.Contains("connection=reconnected", location);

        listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal("ok", listed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("statusReason").ValueKind);
        Assert.Contains(MailScope, listed.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Inbox")).Type);
        Assert.Equal(_provider.Issued[^1].Access, (string?)LastRequest()["connections"]!["mail"]!["accessToken"]);
    }

    [Fact]
    public async Task A_reconnect_that_signs_in_as_another_account_is_refused_and_changes_nothing()
    {
        var id = await ConnectAsync();
        var reconnect = await StartAsync([], reconnectId: id);

        _provider.Account = "someone-else@example.com";
        var location = await CallbackLocationAsync(reconnect.GetProperty("state").GetString()!);

        Assert.Contains("connection=refused", location);
        Assert.Contains("someone-else%40example.com", location);
        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal(Account, listed.GetProperty("account").GetString());
    }

    // ---- binding ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_manager_hire_naming_a_connection_no_person_bound_on_its_team_is_refused()
    {
        var id = await ConnectAsync();
        var tools = ManagerTools(await ManagerKeyAsync());

        var refused = await tools.Hire("Sneaky", plugin: "mailer", connections: new() { ["mail"] = id }, cancellationToken: Ct);

        Assert.StartsWith("HTTP 400", refused);
        Assert.Contains($"The connection '{id}' named for slot `mail` is not one a person has bound on this team.", refused);
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Sneaky")));
    }

    [Fact]
    public async Task A_manager_hire_naming_a_connection_a_person_bound_on_its_team_is_accepted()
    {
        var id = await ConnectAsync();
        await HireAsync("Inbox", id);

        var hiring = await GetAsync($"/api/teams/{_team}/hiring");
        var offered = Assert.Single(hiring.GetProperty("connections").EnumerateArray());
        Assert.Equal(id, offered.GetProperty("id").GetString());

        var tools = ManagerTools(await ManagerKeyAsync());
        var hired = await tools.Hire("Second", plugin: "mailer", connections: new() { ["mail"] = id }, cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", hired);

        var settings = await GetAsync($"/api/teams/{_team}/members/Second/plugin-settings");
        Assert.Equal(id, settings.GetProperty("connections").GetProperty("mail").GetString());

        // ANOTHER team's person-bound connection is not this team's.
        var other = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("Other", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        var otherTools = ManagerTools(await ManagerKeyAsync(other), other);
        var refused = await otherTools.Hire("Third", plugin: "mailer", connections: new() { ["mail"] = id }, cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", refused);
    }

    [Fact]
    public async Task A_binding_without_a_required_scope_is_refused_offering_reconnect()
    {
        var id = await ConnectAsync(scopes: []);

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Inbox",
            agent = "plugin:mailer",
            connections = new Dictionary<string, string> { ["mail"] = id },
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, hired.StatusCode);
        var body = JsonDocument.Parse(await hired.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Contains($"was not granted the scope `{MailScope}` that slot `mail` needs. Reconnect it from Admin", body.GetProperty("error").GetString());
        Assert.Equal(id, body.GetProperty("reconnect").GetProperty("connectionId").GetString());
        Assert.Equal([MailScope], body.GetProperty("reconnect").GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));

        // Reconnecting with that scope makes the same binding acceptable.
        var reconnect = await StartAsync([MailScope], reconnectId: id);
        await CallbackLocationAsync(reconnect.GetProperty("state").GetString()!);
        await HireAsync("Inbox", id);
    }

    [Fact]
    public async Task An_unbound_required_slot_blocks_the_run_with_a_sentence_naming_it()
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Inbox", agent = "plugin:mailer" }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);

        var row = await TellAndAwaitAsync("Inbox");
        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.Contains("no connection bound for the plugin's required slot `mail`", Words(row));
    }

    [Fact]
    public async Task A_binding_change_lands_with_its_tenant_row_and_a_disconnect_is_refused_while_bound()
    {
        var id = await ConnectAsync();
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Inbox", agent = "plugin:mailer" }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);

        var put = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Inbox/plugin-settings", new
        {
            config = new { },
            secrets = new { },
            connections = new Dictionary<string, string> { ["mail"] = id },
        });
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Contains(await TenantActionsAsync(), a => a == TenantActions.MemberConnectionsChanged);

        // A save that says nothing of connections keeps the binding.
        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Inbox/plugin-settings", new { config = new { }, secrets = new { } });
        Assert.Equal(id, (await GetAsync($"/api/teams/{_team}/members/Inbox/plugin-settings")).GetProperty("connections").GetProperty("mail").GetString());

        var delete = await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null);
        Assert.Equal(HttpStatusCode.Conflict, delete.Status);
        Assert.Contains($"{_team}/Inbox (slot mail)", delete.Body);

        // Not written when its tenant row cannot be.
        await ExecuteAsync("ALTER TABLE tenant_events RENAME TO tenant_events_away");
        var unrecorded = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Inbox/plugin-settings", new
        {
            config = new { }, secrets = new { }, connections = new Dictionary<string, string>(),
        });
        await ExecuteAsync("ALTER TABLE tenant_events_away RENAME TO tenant_events");
        Assert.Equal(HttpStatusCode.InternalServerError, unrecorded.Status);
        Assert.Equal(id, (await GetAsync($"/api/teams/{_team}/members/Inbox/plugin-settings")).GetProperty("connections").GetProperty("mail").GetString());

        // Unbound, it disconnects: revoked at Google, and the tokens are gone.
        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Inbox/plugin-settings", new
        {
            config = new { }, secrets = new { }, connections = new Dictionary<string, string>(),
        });
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null)).Status);
        Assert.Equal(_provider.Issued[0].Refresh, Assert.Single(_provider.Revoked));
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Null(await Services.GetRequiredService<ConnectionStore>().TokensAsync(id, Ct));
    }

    [Fact]
    public async Task A_manifest_with_a_bad_connection_slot_is_refused_with_a_reason_and_one_without_is_unchanged()
    {
        var (unknown, why) = PluginManifest.Parse(PluginInstall.Manifest("bad", edit: m => m["connections"] = JsonNode.Parse(
            """{"mail":{"providers":["yahoo"]}}""")).ToJsonString());
        Assert.Null(unknown);
        Assert.Contains("'yahoo', which is not a provider this Host knows", why);

        var (malformed, reason) = PluginManifest.Parse(PluginInstall.Manifest("bad", edit: m => m["connections"] = JsonNode.Parse(
            """{"mail":{"providers":["google"],"scopes":{"microsoft":["x"]}}}""")).ToJsonString());
        Assert.Null(malformed);
        Assert.Contains("`connections.mail.scopes` has an entry for 'microsoft'", reason);

        var (plain, _) = PluginManifest.Parse(PluginInstall.Manifest("plain").ToJsonString());
        Assert.Empty(plain!.Connections);

        PluginInstall.Write(_dataRoot, "bad", manifest: PluginInstall.Manifest("bad", edit: m => m["connections"] = JsonNode.Parse(
            """{"mail":{"providers":[]}}""")));
        var listing = JsonDocument.Parse((await SendAsync(HttpMethod.Post, "/api/plugins/rescan", null)).Body).RootElement;
        var refused = Assert.Single(listing.GetProperty("refused").EnumerateArray(), r => r.GetProperty("id").GetString() == "bad");
        Assert.Contains("`connections.mail.providers` must be a non-empty array", refused.GetProperty("reason").GetString());

        var mailer = listing.GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "mailer");
        Assert.Equal("needs a Google or Microsoft connection", mailer.GetProperty("connections").GetProperty("mail").GetProperty("summary").GetString());
    }

    // ---- callback ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_callback_with_an_unknown_reused_or_expired_state_is_refused_and_stores_nothing()
    {
        // Unknown.
        Assert.Contains("connection=refused", await CallbackLocationAsync("never-issued"));
        Assert.Empty(_provider.Exchanges);

        // Reused: the first is accepted, the second refused.
        var start = await StartAsync([MailScope]);
        var state = start.GetProperty("state").GetString()!;
        Assert.Contains("connection=connected", await CallbackLocationAsync(state));
        Assert.Contains("connection=refused", await CallbackLocationAsync(state));
        Assert.Single(_provider.Exchanges);
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());

        // Expired: 11 minutes after it was issued.
        var late = await StartAsync([MailScope]);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Contains("connection=refused", await CallbackLocationAsync(late.GetProperty("state").GetString()!));
        Assert.Single(_provider.Exchanges);
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());

        // A provider refusal consumes the state and stores nothing.
        var denied = await StartAsync([MailScope]);
        var location = await CallbackLocationAsync(denied.GetProperty("state").GetString()!, error: "access_denied");
        Assert.Contains("connection=refused", location);
        Assert.Contains("access_denied", location);
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Fact]
    public async Task The_cli_flow_completes_through_the_operator_exchange_and_not_through_the_web()
    {
        var folder = Path.Combine(_dataRoot, ConnectRequests.Folder);
        await WaitUntilAsync(() => Directory.Exists(folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));

        var started = await ExchangeAsync(new { op = "start", provider = "google", scopes = new[] { MailScope }, name = "Work mail", redirectUri = "http://127.0.0.1:53121/" });
        Assert.Equal(200, started.GetProperty("status").GetInt32());
        var state = started.GetProperty("start").GetProperty("state").GetString()!;
        Assert.Equal("http://127.0.0.1:53121/", started.GetProperty("start").GetProperty("redirectUri").GetString());

        // A person's web complete cannot spend the operator's state, and it stays usable.
        var web = await SendAsync(HttpMethod.Post, "/api/connections/complete", new { state, code = FakeProvider.GoodCode });
        Assert.Equal(HttpStatusCode.BadRequest, web.Status);

        // Nor can the web callback.
        var other = await ExchangeAsync(new { op = "start", provider = "google", scopes = new[] { MailScope }, redirectUri = "http://127.0.0.1:53122/" });
        Assert.Contains("connection=refused", await CallbackLocationAsync(other.GetProperty("start").GetProperty("state").GetString()!));

        var completed = await ExchangeAsync(new { op = "complete", state, code = FakeProvider.GoodCode });
        Assert.Equal(200, completed.GetProperty("status").GetInt32());
        Assert.Equal("Work mail", completed.GetProperty("connection").GetProperty("name").GetString());
        Assert.Equal("http://127.0.0.1:53121/", _provider.Exchanges[^1].RedirectUri);

        var list = await ExchangeAsync(new { op = "list" });
        Assert.Single(list.GetProperty("connections").EnumerateArray());

        var removed = await ExchangeAsync(new { op = "remove", id = "Work mail" });
        Assert.Equal(204, removed.GetProperty("status").GetInt32());
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());

        var rows = await TenantRowsAsync();
        Assert.Contains(rows, r => r.Action == TenantActions.ConnectionConnected && r.ActorEmail == ConnectionActor.Operator.Email);
    }

    // ---- races and failures -----------------------------------------------------------------------

    [Fact]
    public async Task A_refresh_in_flight_across_a_reconnect_neither_overwrites_its_tokens_nor_marks_it_refused()
    {
        var id = await ConnectAsync();
        var store = Services.GetRequiredService<ConnectionStore>();
        var connections = Services.GetRequiredService<Connections>();

        foreach (var refused in new[] { true, false })
        {
            _provider.RefuseRefresh = refused;
            _provider.Rotates = !refused;
            _clock.Advance(TimeSpan.FromHours(2));

            // The refresh reaches the provider with the old refresh token, and is held there.
            var held = _provider.HoldRefresh();
            var grant = Task.Run(() => connections.GrantAsync(id, "mail", Ct), Ct);
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            // Meanwhile a person reconnects: the exchange is done, the write waits for the refresh.
            var reconnect = await StartAsync([], reconnectId: id);
            var exchanges = _provider.Exchanges.Count;
            var callback = Task.Run(() => CallbackLocationAsync(reconnect.GetProperty("state").GetString()!), Ct);
            await WaitUntilAsync(() => _provider.Exchanges.Count > exchanges);
            await Task.Delay(200, Ct);
            var reconnected = _provider.Issued[^1];

            held.Release.SetResult();
            await grant;
            Assert.Contains("connection=reconnected", await callback);

            var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
            Assert.Equal("ok", listed.GetProperty("status").GetString());

            var tokens = (await store.TokensAsync(id, Ct))!;
            Assert.Equal(reconnected.Access, tokens.AccessToken);
            Assert.Equal(reconnected.Refresh, tokens.RefreshToken);
        }
    }

    [Fact]
    public async Task A_disconnect_deletes_first_and_then_revokes_recording_the_result()
    {
        var id = await ConnectAsync();
        var store = Services.GetRequiredService<ConnectionStore>();
        _provider.OnRevoke = () => store.GetAsync(id, Ct).GetAwaiter().GetResult() is null ? "gone" : "still stored";

        // Its tenant row cannot be written: nothing is deleted, and nothing is revoked.
        await ExecuteAsync("ALTER TABLE tenant_events RENAME TO tenant_events_away");
        var unrecorded = await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null);
        await ExecuteAsync("ALTER TABLE tenant_events_away RENAME TO tenant_events");
        Assert.Equal(HttpStatusCode.InternalServerError, unrecorded.Status);
        Assert.Empty(_provider.Revoked);
        Assert.NotNull(await store.TokensAsync(id, Ct));

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null)).Status);
        Assert.Equal(_provider.Issued[0].Refresh, Assert.Single(_provider.Revoked));
        Assert.Equal(["gone"], _provider.RevokedWhen);

        var rows = (await TenantRowsAsync()).Select(r => r.Action).ToList();
        var disconnected = rows.IndexOf(TenantActions.ConnectionDisconnected);
        Assert.True(disconnected >= 0 && rows.IndexOf(TenantActions.ConnectionRevoked) > disconnected, string.Join(", ", rows));

        // A provider that refuses the revoke changes nothing for the person: the result is recorded.
        var second = await ConnectAsync();
        _provider.RevokeRefusal = "the provider answered 503";
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/connections/{second}", null)).Status);
        Assert.Contains(await TenantDetailsAsync(TenantActions.ConnectionRevoked), d => d.Contains("the provider answered 503"));
    }

    [Fact]
    public async Task A_callback_whose_connection_cannot_be_stored_redirects_with_the_reason()
    {
        await ExecuteAsync("CREATE TRIGGER refuse_insert BEFORE INSERT ON connections BEGIN SELECT RAISE(ABORT, 'refused by the test'); END");

        var start = await StartAsync([MailScope]);
        var location = await CallbackLocationAsync(start.GetProperty("state").GetString()!);

        Assert.StartsWith("/console?connection=refused&reason=", location);
        Assert.Contains("could not be stored", Uri.UnescapeDataString(location));
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Fact]
    public async Task A_database_failure_other_than_storing_a_refresh_blocks_the_run_with_a_generic_sentence()
    {
        var id = await ConnectAsync();
        await HireAsync("Inbox", id);

        // The refused refresh's needs-reconnect mark is the write that fails, not a refresh's store.
        _provider.RefuseRefresh = true;
        _clock.Advance(TimeSpan.FromHours(2));
        await ExecuteAsync("CREATE TRIGGER refuse_mark BEFORE UPDATE ON connections BEGIN SELECT RAISE(ABORT, 'refused by the test'); END");

        var row = await TellAndAwaitAsync("Inbox");

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.Contains("could not be read or updated", Words(row));
        Assert.DoesNotContain("was refreshed", Words(row));
    }

    // ---- tenant rows ------------------------------------------------------------------------------

    [Fact]
    public async Task Every_connections_write_lands_with_its_tenant_row_naming_the_person()
    {
        var saved = await SendAsync(HttpMethod.Put, "/api/connections/providers/custom-acme", new
        {
            clientId = "acme-client", clientSecret = "acme-secret-value", authorizeUrl = "https://acme.example/authorize",
            tokenUrl = "https://acme.example/token", defaultScopes = new[] { "read" },
        });
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/api/connections/providers/custom-acme", null)).Status);

        var id = await ConnectAsync();
        var reconnect = await StartAsync([], reconnectId: id);
        Assert.Contains("connection=reconnected", await CallbackLocationAsync(reconnect.GetProperty("state").GetString()!));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Patch, $"/api/connections/{id}", new { name = "Work mail" })).Status);

        await HireAsync("Inbox", id);
        var clone = await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/clone", new { name = "Mail copy" });
        Assert.True((int)clone.Status is >= 200 and < 300, clone.Body);

        var rows = await TenantRowsAsync();

        foreach (var action in new[]
        {
            TenantActions.ConnectionProviderSaved, TenantActions.ConnectionProviderRemoved, TenantActions.ConnectionConnected,
            TenantActions.ConnectionReconnected, TenantActions.ConnectionRenamed,
        })
        {
            // The web callback carries no session; the row still names the person the state was issued to.
            Assert.Contains(rows, r => r.Action == action && r.ActorEmail == Email);
        }

        var bindings = rows.Where(r => r.Action == TenantActions.MemberConnectionsChanged).ToList();
        Assert.Contains(bindings, r => r.ActorEmail == Email && r.Subject == $"{_team}/Inbox");
        var copy = JsonDocument.Parse(clone.Body).RootElement.GetProperty("team").GetProperty("id").GetString()!;
        Assert.Contains(bindings, r => r.Subject == $"{copy}/Inbox");

        // The clone carries the binding: the same connection id, never a token.
        var cloned = await GetAsync($"/api/teams/{copy}/members/Inbox/plugin-settings");
        Assert.Equal(id, cloned.GetProperty("connections").GetProperty("mail").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null)).Status);
        foreach (var team in new[] { _team, copy })
        {
            await SendAsync(HttpMethod.Put, $"/api/teams/{team}/members/Inbox/plugin-settings", new
            {
                config = new { }, secrets = new { }, connections = new Dictionary<string, string>(),
            });
        }

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null)).Status);
        Assert.Contains(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionDisconnected && r.ActorEmail == Email);
    }

    // ---- binding, more ----------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_hired_with_connections_is_refused()
    {
        var id = await ConnectAsync();

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Helper",
            agent = "claude-headless",
            connections = new Dictionary<string, string> { ["mail"] = id },
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, hired.StatusCode);
        Assert.Contains("an Agent gets no connection and no token", await hired.Content.ReadAsStringAsync(Ct));
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Helper")));
    }

    [Fact]
    public async Task The_hiring_view_names_bound_connections_without_their_account()
    {
        var id = await ConnectAsync();
        await HireAsync("Inbox", id);

        // A connection's name starts as its account; a person names it something else here.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Patch, $"/api/connections/{id}", new { name = "Team mailbox" })).Status);

        var (status, body) = await SendAsync(HttpMethod.Get, $"/api/teams/{_team}/hiring", null);
        Assert.Equal(HttpStatusCode.OK, status);

        var offered = Assert.Single(JsonDocument.Parse(body).RootElement.GetProperty("connections").EnumerateArray());
        Assert.Equal(id, offered.GetProperty("id").GetString());
        Assert.Equal("Team mailbox", offered.GetProperty("name").GetString());
        Assert.False(offered.TryGetProperty("account", out _));
        Assert.DoesNotContain(Account, body);
    }

    [Fact]
    public void A_manifest_with_a_malformed_slot_name_is_refused()
    {
        var (manifest, why) = PluginManifest.Parse(PluginInstall.Manifest("bad", edit: m => m["connections"] = JsonNode.Parse(
            """{"my mail!":{"providers":["google"]}}""")).ToJsonString());

        Assert.Null(manifest);
        Assert.Contains("`connections.my mail!` is not a usable slot name", why);
    }

    [Fact]
    public async Task A_state_started_by_one_person_cannot_be_completed_by_another()
    {
        const string Other = "other@example.test";
        await Services.GetRequiredService<IUserStore>().CreateAsync(Other, Password, ct: Ct);
        using var other = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await other.PostAsJsonAsync("/api/auth/login", new { email = Other, password = Password }, Ct)).EnsureSuccessStatusCode();

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { provider = "google", scopes = new[] { MailScope }, redirectUri = "http://127.0.0.1:53121/" });
        Assert.Equal(HttpStatusCode.OK, status);
        var state = JsonDocument.Parse(body).RootElement.GetProperty("state").GetString()!;

        var stolen = await other.PostAsJsonAsync("/api/connections/complete", new { state, code = FakeProvider.GoodCode }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);
        Assert.Empty(_provider.Exchanges);
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());

        // Still the first person's to finish.
        var mine = await SendAsync(HttpMethod.Post, "/api/connections/complete", new { state, code = FakeProvider.GoodCode });
        Assert.True(mine.Status == HttpStatusCode.OK, mine.Body);
    }

    // ---- the operator exchange's folder -----------------------------------------------------------

    [Fact]
    public async Task A_connect_request_in_a_folder_others_can_write_or_through_a_link_is_not_answered()
    {
        var root = Path.Combine(_outside, "exchange");
        var logger = new Warnings();
        var exchange = new ConnectRequests(Services.GetRequiredService<Connections>(), root, logger);
        exchange.Prepare();

        var folder = exchange.Root;
        var report = Path.Combine(folder, ConnectRequests.ReportFile);
        async Task Ask(string path) => await File.WriteAllTextAsync(path, $$"""{"request":"{{Guid.NewGuid():N}}","op":"list"}""", Ct);

        // Group-writable: refused, and said once however often it is looked at.
        File.SetUnixFileMode(folder, File.GetUnixFileMode(folder) | UnixFileMode.GroupWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        await Ask(Path.Combine(folder, ConnectRequests.RequestFile));
        Assert.False(await exchange.AnswerAsync(Ct));
        Assert.False(await exchange.AnswerAsync(Ct));
        Assert.False(File.Exists(report));
        Assert.Contains("writable by others", Assert.Single(logger.Lines));

        // Put right, it is answered.
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.True(await exchange.AnswerAsync(Ct));
        Assert.True(File.Exists(report));
        File.Delete(report);

        // A request that is a link to a file elsewhere is not read.
        var elsewhere = Path.Combine(_outside, "planted.json");
        await Ask(elsewhere);
        File.CreateSymbolicLink(Path.Combine(folder, ConnectRequests.RequestFile), elsewhere);
        Assert.False(await exchange.AnswerAsync(Ct));
        Assert.False(File.Exists(report));
        File.Delete(Path.Combine(folder, ConnectRequests.RequestFile));

        // Nor is the folder itself a link to one another user made.
        var planted = Path.Combine(_outside, "planted-folder");
        Directory.CreateDirectory(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(folder, recursive: true);
        Directory.CreateSymbolicLink(folder, planted);
        await Ask(Path.Combine(planted, ConnectRequests.RequestFile));
        Assert.False(await exchange.AnswerAsync(Ct));
        Assert.False(File.Exists(Path.Combine(planted, ConnectRequests.ReportFile)));
        Assert.Contains(logger.Lines, l => l.Contains("symbolic link"));
    }

    [Fact]
    public async Task Removing_by_a_name_two_connections_share_is_refused_naming_the_id_way()
    {
        var first = await ConnectAsync();
        var second = await ConnectAsync();
        Assert.NotEqual(first, second);

        var removed = await ExchangeAsync(new { op = "remove", id = Account });

        Assert.Equal(409, removed.GetProperty("status").GetInt32());
        Assert.Contains("remove it by id", removed.GetProperty("error").GetString());
        Assert.Equal(2, (await GetAsync("/api/connections")).GetArrayLength());
    }

    // ---- no leaks ---------------------------------------------------------------------------------

    [Fact]
    public async Task No_client_secret_refresh_token_or_access_token_appears_in_a_response_row_diagnostic_or_plugin_output()
    {
        _provider.Rotates = true;
        var id = await ConnectAsync();
        await HireAsync("Inbox", id);

        var first = await TellAndAwaitAsync("Inbox");
        _clock.Advance(TimeSpan.FromHours(2));
        var second = await TellAndAwaitAsync("Inbox");

        // The plugin DID print its token: redaction is what keeps it out.
        Assert.Contains(_provider.Issued[^1].Access, File.ReadAllText(Requests));
        Assert.Contains("[redacted]", second.Payload, StringComparison.OrdinalIgnoreCase);

        // Every route a person or a Manager reads.
        await GetAsync("/api/connections/providers");
        await GetAsync("/api/connections");
        await GetAsync("/api/plugins");
        await GetAsync($"/api/teams/{_team}/hiring");
        await GetAsync($"/api/teams/{_team}/members/Inbox/plugin-settings");
        await GetAsync($"/api/teams/{_team}/members/Inbox/runs");
        await GetAsync("/api/teams");
        await GetAsync("/api/diagnostics");
        await GetAsync("/api/tenant-log");
        await SendAsync(HttpMethod.Patch, $"/api/connections/{id}", new { name = "Renamed" });

        string[] secrets =
        [
            ClientSecret,
            .. _provider.Issued.Select(t => t.Access),
            .. _provider.Issued.Select(t => t.Refresh),
        ];

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(_responses, r => r.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, first.Payload);
            Assert.DoesNotContain(secret, second.Payload);
        }

        // EVERY FILE the Host wrote under its data root - the database and its WAL (tenant rows, the
        // message log, diagnostics), logs, reports - holds none of them in plain text.
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

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<string> ConnectAsync(IReadOnlyList<string>? scopes = null)
    {
        var start = await StartAsync(scopes ?? [MailScope]);
        return await CallbackAsync(start.GetProperty("state").GetString()!);
    }

    private async Task<JsonElement> StartAsync(IReadOnlyList<string> scopes, string? reconnectId = null)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/start",
            new { provider = reconnectId is null ? "google" : null, scopes, reconnectId });
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<string> CallbackAsync(string state)
    {
        var location = await CallbackLocationAsync(state);
        Assert.Contains("connection=connected", location);
        return HttpUtility.ParseQueryString(new Uri(new Uri("http://host"), location).Query)["id"]!;
    }

    private async Task<string> CallbackLocationAsync(string state, string? error = null)
    {
        var url = $"{Connections.CallbackPath}?state={Uri.EscapeDataString(state)}"
            + (error is null ? $"&code={FakeProvider.GoodCode}" : $"&error={error}");
        using var response = await _anonymous.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        _responses.Add(location);
        return location;
    }

    private async Task HireAsync(string name, string connectionId)
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name,
            agent = "plugin:mailer",
            connections = new Dictionary<string, string> { ["mail"] = connectionId },
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

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{member}/tell", new { instruction = "read the mail" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Rows()).FirstOrDefault(m => m.Seq > after) is { } row)
            {
                // Idle again before the next tell, so the next run is a new one.
                var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
                while (host.Find(who)!.State != ContainerState.Idle && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
                return row;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{who} wrote no terminal row.");
    }

    /// <summary>Every string in a row's payload, unescaped: what a person reads.</summary>
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

    private JsonObject LastRequest() => JsonNode.Parse(File.ReadAllLines(Requests).Last(l => l.Length > 0))!.AsObject();

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

    private async Task<JsonElement> ExchangeAsync(object body)
    {
        var folder = Path.Combine(_dataRoot, ConnectRequests.Folder);
        var nonce = Guid.NewGuid().ToString("N");
        var node = JsonSerializer.SerializeToNode(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        node["request"] = nonce;

        var temporary = Path.Combine(folder, ConnectRequests.RequestFile + ".tmp");
        await File.WriteAllTextAsync(temporary, node.ToJsonString(), Ct);
        File.Move(temporary, Path.Combine(folder, ConnectRequests.RequestFile), overwrite: true);

        var report = Path.Combine(folder, ConnectRequests.ReportFile);
        JsonElement? answer = null;

        await WaitUntilAsync(() =>
        {
            if (!File.Exists(report)) return false;
            try
            {
                var element = JsonDocument.Parse(File.ReadAllText(report)).RootElement;
                if (element.GetProperty("request").GetString() != nonce) return false;
                answer = element.Clone();
                return true;
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                return false;
            }
        });

        _responses.Add(answer!.Value.GetRawText());
        return answer.Value;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never held.");
            await Task.Delay(50, Ct);
        }
    }

    private Task<string> ManagerKeyAsync(string? team = null) =>
        Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(team ?? _team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container,
            team ?? _team, TeamRegistry.ManagerPermits, ct: Ct);

    /// <summary>The MCP tools as a team's Manager holds them. SYNCHRONOUS on purpose: the accessor's
    /// context is an AsyncLocal, and one set inside an async helper does not flow back.</summary>
    private PlatformMcpTools ManagerTools(string key, string? team = null)
    {
        var managerId = new ContainerId(team ?? _team, TeamRegistry.DefaultManagerName).ToString();

        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(
            new Principal(managerId, PrincipalKind.Container, TeamRegistry.ManagerPermits.ToHashSet()), "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context }, Services.GetRequiredService<IPrincipalStore>(),
            Services.GetRequiredService<AgentCatalog>(), new ServerClients(_factory));
    }

    private sealed class ServerClients(WebApplicationFactory<Program> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(factory.Server.CreateHandler(), disposeHandler: true) { BaseAddress = factory.Server.BaseAddress };
    }

    private async Task<IReadOnlyList<string>> TenantActionsAsync() => [.. (await TenantRowsAsync()).Select(r => r.Action)];

    private async Task<IReadOnlyList<string>> TenantDetailsAsync(string action) =>
        [.. (await TenantRowsAsync()).Where(r => r.Action == action).Select(r => r.Detail ?? "")];

    private async Task<IReadOnlyList<(string Action, string? ActorEmail, string? Subject, string? Detail)>> TenantRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT action, actor_email, subject, detail FROM tenant_events ORDER BY seq";
        var rows = new List<(string, string?, string?, string?)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), Nullable(reader, 1), Nullable(reader, 2), Nullable(reader, 3)));
        }

        return rows;

        static string? Nullable(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
    }

    /// <summary>The warnings a service logged, as a person would read them.</summary>
    private sealed class Warnings : Microsoft.Extensions.Logging.ILogger<ConnectRequests>
    {
        public readonly List<string> Lines = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning) Lines.Add(formatter(state, exception));
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static string Challenge(string verifier) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A clock the test moves; the rest of the Host keeps the system's.</summary>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// A PROVIDER THAT IS NOT GOOGLE: it accepts <see cref="GoodCode"/>, issues 60-minute tokens,
    /// names <see cref="Account"/> in its ID token, rotates refresh tokens when asked, and refuses a
    /// refresh with <c>invalid_grant</c> when asked.
    /// </summary>
    private sealed class FakeProvider : IOAuthEndpoints
    {
        public const string GoodCode = "good-code";

        public string Account = ConnectionsTests.Account;
        public bool Rotates;
        public bool RefuseRefresh;
        public TimeSpan RefreshDelay = TimeSpan.Zero;
        public string? RevokeRefusal;
        public Func<string>? OnRevoke;
        public readonly List<string> RevokedWhen = [];

        private Held? _held;

        /// <summary>A refresh held at the provider until released: one in flight, on purpose.</summary>
        public sealed class Held
        {
            public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Held HoldRefresh() => _held = new Held();

        public readonly List<(string Access, string Refresh)> Issued = [];
        public readonly List<(string? Secret, string RedirectUri, string Verifier)> Exchanges = [];
        public readonly List<(string? Secret, string RefreshToken)> Refreshes = [];
        public readonly List<string> Revoked = [];

        private readonly Lock _lock = new();

        public Task<OAuthTokenAnswer> ExchangeCodeAsync(
            OAuthProvider provider, string? clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct)
        {
            lock (_lock)
            {
                if (code != GoodCode) return Task.FromResult(new OAuthTokenAnswer(null, null, null, null, null, "invalid_grant", "Bad code.", true));

                Exchanges.Add((clientSecret, redirectUri, codeVerifier));
                var (access, refresh) = Issue(newRefresh: true);
                return Task.FromResult(new OAuthTokenAnswer(access, refresh, 3600, null, IdToken(), null, null, false));
            }
        }

        public async Task<OAuthTokenAnswer> RefreshAsync(OAuthProvider provider, string? clientSecret, string refreshToken, CancellationToken ct)
        {
            if (RefreshDelay > TimeSpan.Zero) await Task.Delay(RefreshDelay, ct);

            if (Interlocked.Exchange(ref _held, null) is { } held)
            {
                held.Entered.SetResult();
                await held.Release.Task.WaitAsync(ct);
            }

            lock (_lock)
            {
                Refreshes.Add((clientSecret, refreshToken));

                if (RefuseRefresh)
                {
                    return new OAuthTokenAnswer(null, null, null, null, null, "invalid_grant", "Token has been expired or revoked.", true);
                }

                var (access, refresh) = Issue(newRefresh: Rotates, current: refreshToken);
                return new OAuthTokenAnswer(access, Rotates ? refresh : null, 3600, null, null, null, null, false);
            }
        }

        public Task<JsonElement?> UserInfoAsync(OAuthProvider provider, string accessToken, CancellationToken ct) =>
            Task.FromResult<JsonElement?>(null);

        public Task<string?> RevokeAsync(OAuthProvider provider, string token, CancellationToken ct)
        {
            lock (_lock)
            {
                Revoked.Add(token);
                if (OnRevoke is not null) RevokedWhen.Add(OnRevoke());
            }

            return Task.FromResult(RevokeRefusal);
        }

        private (string Access, string Refresh) Issue(bool newRefresh, string? current = null)
        {
            var access = $"ya29.access-{Guid.NewGuid():N}";
            var refresh = newRefresh || current is null ? $"1//refresh-{Guid.NewGuid():N}" : current;
            Issued.Add((access, refresh));
            return (access, refresh);
        }

        private string IdToken()
        {
            static string Part(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            return $"{Part(new { alg = "none" })}.{Part(new { email = Account, sub = "1234" })}.";
        }
    }
}
