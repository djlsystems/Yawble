using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// A MAILBOX CONNECTION (kind <c>imap</c>): an account at a mail server reached with an app
/// password, on the real Host against a FAKE IMAP and SMTP server in this process - no test reaches
/// a real account or the network. A person adds one with a login test, binds it to a plugin slot
/// that admits <c>imap</c>, and the member's run receives it on stdin; a later refused login, Update
/// password, the binding and tenant-row rules, and the no-leak rule are each pinned here.
/// </summary>
public sealed class ImapConnectionsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string PersonPassword = "correct horse battery";
    private const string Account = "mailbox@example.com";
    private const string AppPassword = "abcd efgh ijkl mnop";
    private const string NewAppPassword = "qrst uvwx yzab cdef";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-imap-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-imap-out-{Guid.NewGuid():N}");
    private readonly FakeMailServer _server = new(imapSecurity: "TLS", smtpSecurity: "STARTTLS") { Password = AppPassword };
    private readonly Clock _clock = new();
    private readonly List<string> _responses = [];
    private readonly ConcurrentQueue<string> _logs = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
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

        // A PLUGIN WITH ONE MAILBOX SLOT, that writes the request it was handed OUTSIDE the data root
        // (so the no-leak scan does not find the plugin's own copy) and ALSO prints it: a plugin
        // echoing its password must have it redacted from everything the Host stores.
        PluginInstall.Write(_dataRoot, "mailer",
            script: $"env >> '{Environment}'; printf '%s\\n' \"$0 $*\" >> '{Environment}'; req=$(cat); printf '%s\\n' \"$req\" >> '{Requests}'; printf '%s\\n' \"$req\"; echo '{{\"t\":\"result\",\"ok\":true,\"output\":\"read\"}}'",
            manifest: PluginInstall.Manifest("mailer", edit: m => m["connections"] = JsonNode.Parse(
                """{"mail":{"description":"The mailbox.","providers":["imap"],"required":true}}""")));

        // A plugin whose slot takes Google only: a mailbox does not suit it.
        PluginInstall.Write(_dataRoot, "drive", manifest: PluginInstall.Manifest("drive", edit: m => m["connections"] = JsonNode.Parse(
            """{"files":{"providers":["google"],"scopes":["https://www.googleapis.com/auth/drive.readonly"],"required":true}}""")));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Information")
            .ConfigureLogging(logging => logging.AddProvider(new ListLogger(_logs)))
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());

                // THE LOGIN, over a real socket to the fake servers, trusting only their certificate.
                var login = new SocketMailLogin(_server.Trust);
                services.AddSingleton<IMailLogin>(login);
                services.AddSingleton(sp => new Connections(
                    sp.GetRequiredService<ConnectionStore>(), sp.GetRequiredService<IOAuthEndpoints>(), _clock,
                    sp.GetRequiredService<IUserStore>(), mail: login));
            }));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Mail", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, PersonPassword);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = PersonPassword }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { _dataRoot, _outside })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    // ---- presets ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_presets_are_gmail_icloud_yahoo_and_other_with_their_published_servers()
    {
        var presets = (await GetAsync("/api/connections/imap/presets")).EnumerateArray().ToList();

        Assert.Equal(["gmail", "icloud", "yahoo", "other"], presets.Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(["Gmail", "iCloud", "Yahoo", "Other"], presets.Select(p => p.GetProperty("name").GetString()));

        static string Server(JsonElement preset, string which) =>
            preset.GetProperty(which) is { ValueKind: JsonValueKind.Object } s
                ? $"{s.GetProperty("host").GetString()}:{s.GetProperty("port").GetInt32()} {s.GetProperty("security").GetString()}"
                : "none";

        Assert.Equal(
            [
                "imap.gmail.com:993 TLS / smtp.gmail.com:465 TLS",
                "imap.mail.me.com:993 TLS / smtp.mail.me.com:587 STARTTLS",
                "imap.mail.yahoo.com:993 TLS / smtp.mail.yahoo.com:465 TLS",
                "none / none",
            ],
            presets.Select(p => $"{Server(p, "imap")} / {Server(p, "smtp")}"));
    }

    // ---- the login test at save -------------------------------------------------------------------

    [Fact]
    public async Task A_mailbox_with_a_good_app_password_is_saved_with_its_tenant_row_and_the_login_sentence()
    {
        var (status, body) = await AddAsync();
        Assert.True(status == HttpStatusCode.OK, body);

        var answer = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Connected: 1,240 messages in Inbox", answer.GetProperty("sentence").GetString());

        var connection = answer.GetProperty("connection");
        Assert.Equal("imap", connection.GetProperty("kind").GetString());
        Assert.Equal("imap", connection.GetProperty("provider").GetString());
        Assert.Equal("imap", connection.GetProperty("providerKind").GetString());
        Assert.Equal("Work mail", connection.GetProperty("name").GetString());
        Assert.Equal(Account, connection.GetProperty("account").GetString());
        Assert.Equal(Account, connection.GetProperty("username").GetString());
        Assert.True(connection.GetProperty("passwordSet").GetBoolean());
        Assert.False(connection.TryGetProperty("password", out _));
        Assert.Equal("127.0.0.1", connection.GetProperty("imap").GetProperty("host").GetString());
        Assert.Equal(_server.ImapPort, connection.GetProperty("imap").GetProperty("port").GetInt32());
        Assert.Equal("TLS", connection.GetProperty("imap").GetProperty("security").GetString());
        Assert.Equal(_server.SmtpPort, connection.GetProperty("smtp").GetProperty("port").GetInt32());
        Assert.Equal("STARTTLS", connection.GetProperty("smtp").GetProperty("security").GetString());
        Assert.Equal("ok", connection.GetProperty("status").GetString());

        // Both servers were asked: an IMAP login and an SMTP authentication.
        Assert.Equal([$"imap ok {Account}", $"smtp ok {Account}"], _server.Logins);

        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal(connection.GetProperty("id").GetString(), listed.GetProperty("id").GetString());
        Assert.True(listed.GetProperty("passwordSet").GetBoolean());

        var row = Assert.Single(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionConnected);
        Assert.Equal(Email, row.ActorEmail);
        Assert.Contains("\"provider\":\"imap\"", row.Detail);

        // THE PASSWORD IS CIPHERTEXT: what the column holds is not it, and the Host's key ring opens it.
        var stored = await ScalarAsync("SELECT password_protected FROM imap_connections");
        Assert.NotEqual(AppPassword, stored);
        Assert.Equal(AppPassword, Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Harness.Connections.v1").Unprotect(stored!));
    }

    [Fact]
    public async Task A_refused_first_login_saves_nothing_and_says_what_to_fix()
    {
        var (status, body) = await AddAsync(password: "wrong wrong wrong", preset: "gmail");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(
            "The server refused the password. For Gmail, make an app password: it needs 2-Step Verification.",
            JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());

        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM imap_connections"));
        Assert.DoesNotContain(await TenantRowsAsync(), r => r.Action.StartsWith("connections.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_smtp_authentication_saves_nothing_and_says_so()
    {
        // iCloud takes the name part for IMAP and only the full address for SMTP: a name-only username
        // reaches SMTP as the account's full address, and a wrong one there is refused by SMTP alone.
        _server.Username = "mailbox";
        _server.SmtpUsername = Account;

        var (saved, savedBody) = await AddAsync(username: "mailbox", preset: "icloud");
        Assert.True(saved == HttpStatusCode.OK, savedBody);
        Assert.Equal(["imap ok mailbox", $"smtp ok {Account}"], _server.Logins);

        _server.SmtpUsername = "someone-else@example.com";
        var (status, body) = await AddAsync(username: "mailbox", preset: "icloud", name: "Second");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        var error = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()!;
        Assert.StartsWith("The outgoing (SMTP) server refused the password.", error);
        Assert.Contains("For iCloud", error);
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Fact]
    public async Task An_unreachable_server_saves_nothing_and_says_so()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        var (status, body) = await AddAsync(imapPort: port);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal($"Could not reach 127.0.0.1 on port {port}.", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Fact]
    public async Task A_mailbox_missing_a_field_is_refused_naming_it_before_any_login()
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/imap", new
        {
            preset = "other", account = Account, password = AppPassword,
            imap = new { host = "", port = 993, security = "TLS" },
            smtp = new { host = "127.0.0.1", port = 465, security = "SSL3" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("`imap.host`", body);
        Assert.Empty(_server.Logins);
    }

    // ---- the run ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_bound_plugin_run_receives_the_imap_grant_on_stdin_and_never_in_its_environment_or_argv()
    {
        var id = await AddedAsync();
        await HireAsync("Inbox", id);

        var row = await TellAndAwaitAsync("Inbox");
        Assert.Equal(MessageTypes.Completed, row.Type);

        var slot = LastRequest()["connections"]!["mail"]!;
        Assert.Equal("imap", (string?)slot["kind"]);
        Assert.Equal(Account, (string?)slot["account"]);
        Assert.Equal(Account, (string?)slot["username"]);
        Assert.Equal(AppPassword, (string?)slot["password"]);
        Assert.Equal("127.0.0.1", (string?)slot["imap"]!["host"]);
        Assert.Equal(_server.ImapPort, (int)slot["imap"]!["port"]!);
        Assert.Equal("TLS", (string?)slot["imap"]!["security"]);
        Assert.Equal("127.0.0.1", (string?)slot["smtp"]!["host"]);
        Assert.Equal(_server.SmtpPort, (int)slot["smtp"]!["port"]!);
        Assert.Equal("STARTTLS", (string?)slot["smtp"]!["security"]);

        // ON STDIN ONLY: never in the child's environment or its argv.
        Assert.DoesNotContain(AppPassword, File.ReadAllText(Environment));

        // The plugin printed it; redaction kept it out of the row.
        Assert.DoesNotContain(AppPassword, row.Payload);
        Assert.Contains("[redacted]", row.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_later_refused_login_marks_needs_reconnect_and_blocks_the_run_until_update_password_clears_it()
    {
        var id = await AddedAsync(name: "Work mail");
        await HireAsync("Inbox", id);

        // The app password is revoked at the provider.
        _server.Password = NewAppPassword;
        _clock.Advance(Connections.MailboxRecheck + TimeSpan.FromMinutes(1));
        var linesBefore = File.Exists(Requests) ? File.ReadAllLines(Requests).Length : 0;

        var row = await TellAndAwaitAsync("Inbox");
        Assert.Equal(MessageTypes.Failed, row.Type);
        var words = Words(row);
        Assert.Contains($"Connection 'Work mail' ({Account})", words);
        Assert.Contains("The server refused the password.", words);
        Assert.Contains("Admin → Connections", words);
        Assert.Equal(linesBefore, File.Exists(Requests) ? File.ReadAllLines(Requests).Length : 0);

        var listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal("needs-reconnect", listed.GetProperty("status").GetString());
        Assert.StartsWith("The server refused the password.", listed.GetProperty("statusReason").GetString());
        Assert.Contains(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionNeedsReconnect);

        // A second run does not log in again: it says the same thing.
        var tried = _server.Logins.Count;
        Assert.Equal(MessageTypes.Failed, (await TellAndAwaitAsync("Inbox")).Type);
        Assert.Equal(tried, _server.Logins.Count);

        // UPDATE PASSWORD with the new one: a login, then ok again, with its tenant row.
        var (status, body) = await SendAsync(HttpMethod.Put, $"/api/connections/{id}/password", new { password = NewAppPassword });
        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal("Connected: 1,240 messages in Inbox", JsonDocument.Parse(body).RootElement.GetProperty("sentence").GetString());

        listed = Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal("ok", listed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("statusReason").ValueKind);
        Assert.Contains(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionPasswordUpdated && r.ActorEmail == Email);

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Inbox")).Type);
        Assert.Equal(NewAppPassword, (string?)LastRequest()["connections"]!["mail"]!["password"]);
    }

    [Fact]
    public async Task Update_password_with_a_refused_password_changes_nothing_and_says_what_to_fix()
    {
        var id = await AddedAsync();
        var before = await ScalarAsync("SELECT password_protected FROM imap_connections");

        var (status, body) = await SendAsync(HttpMethod.Put, $"/api/connections/{id}/password", new { password = "not the password" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.StartsWith("The server refused the password.", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.Equal(before, await ScalarAsync("SELECT password_protected FROM imap_connections"));
        Assert.DoesNotContain(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionPasswordUpdated);
    }

    // ---- writes, bindings, principals --------------------------------------------------------------

    [Fact]
    public async Task Each_mailbox_write_is_refused_and_changes_nothing_when_its_tenant_row_cannot_be_written()
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Inbox", agent = "plugin:mailer" }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);

        // ADD.
        await WithoutTenantEventsAsync(async () =>
        {
            var (status, _) = await AddAsync();
            Assert.Equal(HttpStatusCode.InternalServerError, status);
        });
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM imap_connections"));

        var id = await AddedAsync(name: "Work mail");
        var password = await ScalarAsync("SELECT password_protected FROM imap_connections");

        // UPDATE PASSWORD, RENAME, BINDING.
        await WithoutTenantEventsAsync(async () =>
        {
            _server.Password = NewAppPassword;
            Assert.Equal(HttpStatusCode.InternalServerError,
                (await SendAsync(HttpMethod.Put, $"/api/connections/{id}/password", new { password = NewAppPassword })).Status);
            Assert.Equal(HttpStatusCode.InternalServerError,
                (await SendAsync(HttpMethod.Patch, $"/api/connections/{id}", new { name = "Renamed" })).Status);
            Assert.Equal(HttpStatusCode.InternalServerError,
                (await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Inbox/plugin-settings", new
                {
                    config = new { }, secrets = new { }, connections = new Dictionary<string, string> { ["mail"] = id },
                })).Status);
        });

        Assert.Equal(password, await ScalarAsync("SELECT password_protected FROM imap_connections"));
        Assert.Equal("Work mail", Assert.Single((await GetAsync("/api/connections")).EnumerateArray()).GetProperty("name").GetString());
        Assert.False((await GetAsync($"/api/teams/{_team}/members/Inbox/plugin-settings")).GetProperty("connections").TryGetProperty("mail", out _));

        // DISCONNECT.
        await WithoutTenantEventsAsync(async () =>
            Assert.Equal(HttpStatusCode.InternalServerError, (await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null)).Status));
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
        Assert.Equal("1", await ScalarAsync("SELECT COUNT(*) FROM imap_connections"));

        // And with the row writable, each lands with it.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/connections/{id}/password", new { password = NewAppPassword })).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Patch, $"/api/connections/{id}", new { name = "Renamed" })).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null)).Status);
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM imap_connections"));

        var actions = (await TenantRowsAsync()).Where(r => r.ActorEmail == Email).Select(r => r.Action).ToList();
        Assert.Equal(
            [TenantActions.ConnectionConnected, TenantActions.ConnectionPasswordUpdated, TenantActions.ConnectionRenamed, TenantActions.ConnectionDisconnected],
            actions.Where(a => a.StartsWith("connections.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_disconnect_is_refused_while_a_member_is_bound_to_the_mailbox()
    {
        var id = await AddedAsync();
        await HireAsync("Inbox", id);
        Assert.Contains(await TenantRowsAsync(), r => r.Action == TenantActions.MemberConnectionsChanged && r.Subject == $"{_team}/Inbox");

        var (status, body) = await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains($"{_team}/Inbox (slot mail)", body);
        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());

        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Inbox/plugin-settings", new
        {
            config = new { }, secrets = new { }, connections = new Dictionary<string, string>(),
        });
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/connections/{id}", null)).Status);
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM imap_connections"));
    }

    [Fact]
    public async Task A_machine_principal_is_refused_every_mailbox_route()
    {
        var id = await AddedAsync();
        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, await ManagerKeyAsync());

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/connections/imap/presets"),
            (HttpMethod.Post, "/api/connections/imap"),
            (HttpMethod.Put, $"/api/connections/{id}/password"),
            (HttpMethod.Patch, $"/api/connections/{id}"),
            (HttpMethod.Delete, $"/api/connections/{id}"),
            (HttpMethod.Get, "/api/connections"),
        })
        {
            using var request = new HttpRequestMessage(method, path);
            if (method != HttpMethod.Get && method != HttpMethod.Delete) request.Content = JsonContent.Create(new { password = AppPassword });
            using var response = await manager.SendAsync(request, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {response.StatusCode}");
        }

        Assert.Single((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Fact]
    public async Task An_agents_hire_may_name_only_a_mailbox_a_person_bound_on_its_team()
    {
        var id = await AddedAsync();
        var tools = ManagerTools(await ManagerKeyAsync());

        var refused = await tools.Hire("Sneaky", plugin: "mailer", connections: new() { ["mail"] = id }, cancellationToken: Ct);
        Assert.StartsWith("HTTP 400", refused);
        Assert.Contains($"The connection '{id}' named for slot `mail` is not one a person has bound on this team.", refused);

        await HireAsync("Inbox", id);
        var hired = await tools.Hire("Second", plugin: "mailer", connections: new() { ["mail"] = id }, cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", hired);
    }

    [Fact]
    public async Task A_slot_may_name_imap_scopes_do_not_apply_to_it_and_a_slot_without_imap_refuses_a_mailbox()
    {
        var (both, error) = PluginManifest.Parse(PluginInstall.Manifest("both", edit: m => m["connections"] = JsonNode.Parse(
            """{"mail":{"providers":["google","imap"],"scopes":["https://mail.google.com/"]}}""")).ToJsonString());
        Assert.True(both is not null, error);
        Assert.Equal(["google", "imap"], both.Connections["mail"].Providers);
        Assert.Empty(both.Connections["mail"].ScopesFor("imap"));
        Assert.Equal("may use a Google or mailbox (IMAP) connection", both.Connections["mail"].Summary);

        var (keyed, why) = PluginManifest.Parse(PluginInstall.Manifest("keyed", edit: m => m["connections"] = JsonNode.Parse(
            """{"mail":{"providers":["imap"],"scopes":{"imap":["INBOX"]}}}""")).ToJsonString());
        Assert.Null(keyed);
        Assert.Contains("scopes do not apply to an imap connection", why);

        var id = await AddedAsync();
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Files", agent = "plugin:drive", connections = new Dictionary<string, string> { ["files"] = id },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, hired.StatusCode);
        Assert.Contains("Slot `files` takes a Google connection", await hired.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_operator_exchange_lists_a_mailbox_with_its_servers_and_never_its_password()
    {
        var id = await AddedAsync(name: "Work mail");

        var folder = Path.Combine(_dataRoot, ConnectRequests.Folder);
        await WaitUntilAsync(() => Directory.Exists(folder));

        var list = await ExchangeAsync(new { op = "list" });
        var listed = Assert.Single(list.GetProperty("connections").EnumerateArray());
        Assert.Equal(id, listed.GetProperty("id").GetString());
        Assert.Equal("imap", listed.GetProperty("kind").GetString());
        Assert.Equal("127.0.0.1", listed.GetProperty("imap").GetProperty("host").GetString());
        Assert.True(listed.GetProperty("passwordSet").GetBoolean());
        Assert.DoesNotContain(AppPassword, list.GetRawText());
    }

    // ---- no leak ----------------------------------------------------------------------------------

    [Fact]
    public async Task No_app_password_appears_in_a_response_row_diagnostic_log_or_file_under_the_data_root()
    {
        var id = await AddedAsync();
        await HireAsync("Inbox", id);
        var first = await TellAndAwaitAsync("Inbox");

        // A refused login and an Update password, so their sentences and rows are scanned too.
        _server.Password = NewAppPassword;
        _clock.Advance(Connections.MailboxRecheck + TimeSpan.FromMinutes(1));
        var second = await TellAndAwaitAsync("Inbox");
        Assert.Equal(MessageTypes.Failed, second.Type);
        await SendAsync(HttpMethod.Put, $"/api/connections/{id}/password", new { password = "refused-on-purpose" });
        await SendAsync(HttpMethod.Put, $"/api/connections/{id}/password", new { password = NewAppPassword });
        var third = await TellAndAwaitAsync("Inbox");

        // The plugin DID print its password: redaction is what keeps it out.
        Assert.Contains(NewAppPassword, File.ReadAllText(Requests));

        await GetAsync("/api/connections");
        await GetAsync("/api/connections/imap/presets");
        await GetAsync("/api/plugins");
        await GetAsync($"/api/teams/{_team}/hiring");
        await GetAsync($"/api/teams/{_team}/members/Inbox/plugin-settings");
        await GetAsync($"/api/teams/{_team}/members/Inbox/runs");
        await GetAsync("/api/teams");
        await GetAsync("/api/diagnostics");
        await GetAsync("/api/tenant-log");
        await SendAsync(HttpMethod.Patch, $"/api/connections/{id}", new { name = "Renamed" });

        string[] secrets = [AppPassword, NewAppPassword, "refused-on-purpose"];

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(_responses, r => r.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(_logs, l => l.Contains(secret, StringComparison.Ordinal));
            foreach (var row in new[] { first, second, third }) Assert.DoesNotContain(secret, row.Payload);
            Assert.DoesNotContain(await TenantRowsAsync(), r => (r.Detail ?? "").Contains(secret, StringComparison.Ordinal));
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

            foreach (var secret in secrets)
            {
                Assert.False(Encoding.Latin1.GetString(bytes).Contains(secret, StringComparison.Ordinal)
                    || Encoding.Unicode.GetString(bytes).Contains(secret, StringComparison.Ordinal),
                    $"{Path.GetRelativePath(_dataRoot, file)} holds an app password in plain text.");
            }
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private Task<(HttpStatusCode Status, string Body)> AddAsync(
        string password = AppPassword, string preset = "other", string? username = null, string name = "Work mail", int? imapPort = null) =>
        SendAsync(HttpMethod.Post, "/api/connections/imap", new
        {
            preset,
            name,
            account = Account,
            username = username ?? Account,
            password,
            imap = new { host = "127.0.0.1", port = imapPort ?? _server.ImapPort, security = _server.ImapSecurity },
            smtp = new { host = "127.0.0.1", port = _server.SmtpPort, security = _server.SmtpSecurity },
        });

    private async Task<string> AddedAsync(string name = "Work mail")
    {
        var (status, body) = await AddAsync(name: name);
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("connection").GetProperty("id").GetString()!;
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

    private Task<string> ManagerKeyAsync() =>
        Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container,
            _team, TeamRegistry.ManagerPermits, ct: Ct);

    /// <summary>The MCP tools as the team's Manager holds them. SYNCHRONOUS on purpose: the accessor's
    /// context is an AsyncLocal, and one set inside an async helper does not flow back.</summary>
    private PlatformMcpTools ManagerTools(string key)
    {
        var managerId = new ContainerId(_team, TeamRegistry.DefaultManagerName).ToString();

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

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Runs <paramref name="act"/> while no tenant row can be written.</summary>
    private async Task WithoutTenantEventsAsync(Func<Task> act)
    {
        await ExecuteAsync("ALTER TABLE tenant_events RENAME TO tenant_events_away");
        try { await act(); }
        finally { await ExecuteAsync("ALTER TABLE tenant_events_away RENAME TO tenant_events"); }
    }

    /// <summary>A clock the test moves; the rest of the Host keeps the system's.</summary>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>Every line the Host logged, at any level, kept for the no-leak scan.</summary>
    private sealed class ListLogger(ConcurrentQueue<string> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception) + (exception is null ? "" : "\n" + exception));

        public void Dispose() { }
    }
}
