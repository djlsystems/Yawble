using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// THE SIGN-IN PAGE'S ROWS ARE THE TABLE SETTING TYPE'S, on the real Host with a fixture plugin whose
/// <c>entries</c> table has a Site URL, extra hosts, Active, a <c>sign-in</c> row action on
/// <c>method = interactive</c> and a masked read-only <c>session</c> column. The Host's own
/// <see cref="ISignInRows"/> answers only an active interactive row, and keeps a session as that row's
/// masked secret cell in <see cref="PluginMemberSecretStore"/>: never in any settings answer, a tenant
/// row or a file in plain text, handed to the plugin's next run under <c>secrets</c>, and redacted from
/// what it writes.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SignInRowsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private const string Entries = """
        {
          "type": "table",
          "key": "name",
          "columns": [
            { "name": "name", "type": "string", "required": true },
            { "name": "siteUrl", "type": "string" },
            { "name": "hosts", "type": "string" },
            { "name": "method", "type": "enum", "enum": ["password", "token", "interactive"], "required": true },
            { "name": "active", "type": "bool", "default": true },
            { "name": "password", "type": "secret" },
            { "name": "session", "type": "secret", "readOnly": true }
          ],
          "rowActions": [ { "action": "sign-in", "label": "Sign in", "when": { "column": "method", "equals": "interactive" } } ]
        }
        """;

    private const string Rows = """
        [
          {"name":"site-admin","siteUrl":"https://app.example.test/home","hosts":"www.example.test, https://API.example.test/x","method":"interactive"},
          {"name":"off","siteUrl":"https://app.example.test","method":"interactive","active":false},
          {"name":"pw","siteUrl":"https://app.example.test","method":"password"},
          {"name":"nowhere","siteUrl":"ftp://app.example.test","method":"interactive"}
        ]
        """;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-sign-in-rows-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-sign-in-rows-out-{Guid.NewGuid():N}");
    private readonly List<string> _responses = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Requests => Path.Combine(_outside, "requests.jsonl");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        // ECHOES its request, so a kept session's way into the run (and out of its output) is seen.
        var script = $"req=$(cat); printf '%s\\n' \"$req\" >> '{Requests}'; printf '%s\\n' \"$req\"; echo '{{\"t\":\"result\",\"ok\":true,\"output\":\"checked\"}}'";
        PluginInstall.Write(_dataRoot, "webcreds", script: script, manifest: PluginInstall.Manifest("webcreds", edit: m =>
            m["config"] = new JsonObject { ["entries"] = JsonNode.Parse(Entries) }));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Signers", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Creds", agent = "plugin:webcreds" }, Ct);
        Assert.True(hired.StatusCode == HttpStatusCode.OK, await hired.Content.ReadAsStringAsync(Ct));

        var saved = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Creds/plugin-settings", new
        {
            config = new Dictionary<string, JsonElement> { ["entries"] = JsonDocument.Parse(Rows).RootElement.Clone() },
        });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { _dataRoot, _outside })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private ContainerId Creds => new(_team, "Creds");

    [Fact]
    public async Task Only_an_active_interactive_row_with_a_site_url_is_an_entry_with_its_hosts_and_session_column()
    {
        var rows = Services.GetRequiredService<ISignInRows>();
        Assert.IsType<PluginSignInRows>(rows);

        var entry = await rows.EntryAsync(Creds, "entries", "SITE-ADMIN", Ct);
        Assert.NotNull(entry);
        Assert.Equal("https://app.example.test/home", entry.SiteUrl);
        Assert.Equal(["www.example.test", "api.example.test"], entry.Hosts);
        Assert.Equal("session", entry.SessionColumn);

        foreach (var row in new[] { "off", "pw", "nowhere", "ghost" })
        {
            Assert.Null(await rows.EntryAsync(Creds, "entries", row, Ct));
        }

        Assert.Null(await rows.EntryAsync(Creds, "password", "site-admin", Ct));
        Assert.Null(await rows.EntryAsync(new ContainerId(_team, "Nobody"), "entries", "site-admin", Ct));
    }

    [Fact]
    public async Task A_kept_session_is_the_rows_masked_secret_cell_never_answered_and_reaches_the_next_run_redacted()
    {
        var state = $$"""{"cookies":[{"name":"sid","value":"{{Guid.NewGuid():N}}","domain":"app.example.test"}],"origins":[]}""";
        var marker = JsonNode.Parse(state)!["cookies"]![0]!["value"]!.GetValue<string>();

        await Services.GetRequiredService<ISignInRows>().SaveSessionAsync(
            Creds, "entries", "site-admin", "session", new SignInStorageState(state), new CredentialActor("u1", Email), Ct);

        // Kept as the row's cell, through the member's own secret store.
        Assert.Equal(state, await Services.GetRequiredService<PluginMemberSecretStore>().CellValueAsync(Creds, "entries", "site-admin", "session", Ct));

        // Every settings answer says it is set, by whom - never its value.
        var settings = await GetAsync($"/api/teams/{_team}/members/Creds/plugin-settings");
        var cell = settings.GetProperty("tableSecretStates").GetProperty("entries").GetProperty("site-admin").GetProperty("session");
        Assert.True(cell.GetProperty("set").GetBoolean());
        Assert.Equal(Email, cell.GetProperty("setBy").GetString());
        Assert.False(cell.TryGetProperty("value", out _));
        await GetAsync($"/api/teams/{_team}/members/Creds/secrets");
        await GetAsync("/api/plugins");

        // The plugin gets it on its next run under secrets, and what it echoes back is redacted.
        var run = await TellAndAwaitAsync("Creds");
        var request = File.ReadAllLines(Requests).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).Last();
        Assert.Equal(state, (string?)request["secrets"]!["entries"]!["site-admin"]!["session"]);
        Assert.DoesNotContain(marker, request["config"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, run.Payload, StringComparison.Ordinal);

        Assert.All(_responses, body => Assert.DoesNotContain(marker, body, StringComparison.Ordinal));

        // Its tenant row names the row and column, never the value.
        var details = await ColumnAsync("SELECT COALESCE(detail, '') FROM tenant_events WHERE action LIKE 'member.secret-%'");
        Assert.Contains(details, d => d.Contains("\"row\":\"site-admin\"", StringComparison.Ordinal) && d.Contains("\"column\":\"session\"", StringComparison.Ordinal));
        Assert.All(details, d => Assert.DoesNotContain(marker, d, StringComparison.Ordinal));

        // Nothing under the data root holds it in plain text.
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) < 0, $"A sign-in session is in {Path.GetRelativePath(_dataRoot, file)}.");
        }
    }

    [Fact]
    public async Task A_session_is_refused_for_a_row_that_is_not_an_entry_and_a_person_may_not_set_it()
    {
        var rows = Services.GetRequiredService<ISignInRows>();
        var state = new SignInStorageState("""{"cookies":[],"origins":[]}""");
        var actor = new CredentialActor("u1", Email);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rows.SaveSessionAsync(Creds, "entries", "pw", "session", state, actor, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rows.SaveSessionAsync(Creds, "entries", "site-admin", "password", state, actor, Ct));
        Assert.Null(await Services.GetRequiredService<PluginMemberSecretStore>().CellValueAsync(Creds, "entries", "pw", "session", Ct));

        // The session column is the platform's: a person's settings save cannot write it.
        var forged = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Creds/plugin-settings", new
        {
            config = new Dictionary<string, JsonElement> { ["entries"] = JsonDocument.Parse(Rows).RootElement.Clone() },
            tableSecretValues = new { entries = new Dictionary<string, object> { ["site-admin"] = new { session = "forged-session-value" } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, forged.Status);
        Assert.Null(await Services.GetRequiredService<PluginMemberSecretStore>().CellValueAsync(Creds, "entries", "site-admin", "session", Ct));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("a.test b.test,a.test", new[] { "a.test", "b.test" })]
    [InlineData("https://Login.Example.test/path; host! ~x", new[] { "login.example.test" })]
    public void A_hosts_cell_is_hosts_by_name_or_url(string? cell, string[] hosts) =>
        Assert.Equal(hosts, PluginSignInRows.Hosts(cell));

    private async Task<Message> TellAndAwaitAsync(string member)
    {
        var who = new ContainerId(_team, member);
        var log = Services.GetRequiredService<IMessageLog>();

        async Task<IReadOnlyList<Message>> Terminal() =>
            [.. (await log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct)).Where(m => m.Source == who.ToString())];

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{member}/tell", new { instruction = "check" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Terminal()).FirstOrDefault() is { } row)
            {
                Assert.True(row.Type == MessageTypes.Completed, row.Payload);
                return row;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{who} wrote no terminal row.");
    }

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

    private async Task<IReadOnlyList<string>> ColumnAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }
}
