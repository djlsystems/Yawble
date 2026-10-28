using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A PLUGIN'S <c>table</c> SETTING, on the real Host with a fixture plugin whose manifest declares an
/// <c>entries</c> table (a key, an enum, a bool <c>active</c>, a masked secret <c>password</c> and a
/// read-only <c>status</c>). The plugin echoes its request and writes <c>row.status</c> records. A
/// person's save stores the rows and each secret cell as the member's own secret; no secret cell is in
/// any answer, row or file under the data root, or in what the run wrote; the run receives the rows in
/// <c>config</c> and the cells under <c>secrets.entries.&lt;row&gt;.&lt;column&gt;</c>. Only a person
/// adds rows; <c>row.status</c> writes only the read-only cells of a row the member has.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PluginTableSettingsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private const string Entries = """
        {
          "type": "table",
          "key": "name",
          "columns": [
            { "name": "name", "type": "string", "required": true },
            { "name": "method", "type": "enum", "enum": ["password", "token", "interactive"], "required": true },
            { "name": "active", "type": "bool", "default": true },
            { "name": "password", "type": "secret" },
            { "name": "status", "type": "string", "readOnly": true }
          ],
          "rowActions": [ { "action": "sign-in", "label": "Sign in", "when": { "column": "method", "equals": "interactive" } } ]
        }
        """;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-table-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-table-out-{Guid.NewGuid():N}");
    private readonly List<string> _responses = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    private string Requests => Path.Combine(_outside, "requests.jsonl");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        // ECHOES its request, then writes four row.status records: a status for `alpha` (taken), a try at
        // flipping `active` (ignored), an unknown column (dropped), and a row nobody added (dropped).
        var records = string.Join("; ", new[]
        {
            """{"t":"row.status","field":"entries","row":"ALPHA","values":{"status":"Verified"}}""",
            """{"t":"row.status","field":"entries","row":"alpha","values":{"active":false,"colour":"red"}}""",
            """{"t":"row.status","field":"entries","row":"ghost","values":{"status":"Verified"}}""",
        }.Select(r => $"echo '{r}'"));
        var script = $"req=$(cat); printf '%s\\n' \"$req\" >> '{Requests}'; printf '%s\\n' \"$req\"; {records}; echo '{{\"t\":\"result\",\"ok\":true,\"output\":\"checked\"}}'";

        PluginInstall.Write(_dataRoot, "tabled", script: script, manifest: PluginInstall.Manifest("tabled", edit: m =>
            m["config"] = new JsonObject { ["entries"] = JsonNode.Parse(Entries) }));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Tablers", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
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

    [Fact]
    public async Task A_persons_rows_and_secret_cells_reach_the_run_with_each_cell_under_secrets_and_never_in_config()
    {
        await HireAsync("Keeper");
        var password = Value("pw");

        var saved = await SaveAsync("Keeper",
            """[{"name":"alpha","method":"password"},{"name":"beta","method":"token","active":false}]""",
            new() { ["entries"] = new() { ["alpha"] = new() { ["password"] = password } } });
        Assert.Equal(HttpStatusCode.OK, saved.Status);

        await TellAndAwaitAsync("Keeper");
        var request = LastRequest("Keeper");

        var rows = request["config"]!["entries"]!.AsArray();
        Assert.Equal(["alpha", "beta"], rows.Select(r => (string?)r!["name"]));
        Assert.True((bool)rows[0]!["active"]!);
        Assert.False((bool)rows[1]!["active"]!);
        Assert.DoesNotContain(password, request["config"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(password, (string?)request["secrets"]!["entries"]!["alpha"]!["password"]);
        Assert.Null(request["secrets"]!["entries"]!["beta"]);
    }

    [Fact]
    public async Task A_secret_cell_is_never_answered_logged_or_kept_in_plain_text_and_the_run_output_is_redacted()
    {
        await HireAsync("Leaky");
        var password = Value("leak");

        await SaveAsync("Leaky", """[{"name":"alpha","method":"password"}]""",
            new() { ["entries"] = new() { ["alpha"] = new() { ["password"] = password } } });
        var run = await TellAndAwaitAsync("Leaky");

        var settings = await GetAsync($"/api/teams/{_team}/members/Leaky/plugin-settings");
        var state = settings.GetProperty("tableSecretStates").GetProperty("entries").GetProperty("alpha").GetProperty("password");
        Assert.True(state.GetProperty("set").GetBoolean());
        Assert.Equal(Email, state.GetProperty("setBy").GetString());
        Assert.False(state.TryGetProperty("value", out _));
        await GetAsync("/api/plugins");

        // The plugin was handed the value (so the scans below are not vacuous) and echoed it.
        Assert.Contains(password, await File.ReadAllTextAsync(Requests, Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(password, run.Payload, StringComparison.Ordinal);
        Assert.All(_responses, body => Assert.DoesNotContain(password, body, StringComparison.Ordinal));

        var details = await ColumnAsync("SELECT COALESCE(detail, '') FROM tenant_events WHERE action LIKE 'member.secret-%'", "");
        Assert.NotEmpty(details);
        Assert.Contains(details, d => d.Contains("\"row\":\"alpha\"", StringComparison.Ordinal) && d.Contains("\"column\":\"password\"", StringComparison.Ordinal));
        Assert.All(details, d => Assert.DoesNotContain(password, d, StringComparison.Ordinal));

        SqliteConnection.ClearAllPools();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            scanned++;
            Assert.True(
                bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(password)) < 0 && bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(password)) < 0,
                $"A table secret cell is in {Path.GetRelativePath(_dataRoot, file)}.");
        }

        Assert.True(scanned > 0);
    }

    [Fact]
    public async Task An_edit_that_leaves_a_cell_untouched_keeps_it_a_null_clears_it_and_a_deleted_row_takes_its_cells()
    {
        await HireAsync("Editor");
        var password = Value("kept");
        await SaveAsync("Editor", """[{"name":"alpha","method":"password"},{"name":"beta","method":"password"}]""",
            new()
            {
                ["entries"] = new()
                {
                    ["alpha"] = new() { ["password"] = password },
                    ["beta"] = new() { ["password"] = Value("beta") },
                },
            });

        // Untouched: a save naming no cell keeps both.
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync("Editor", """[{"name":"alpha","method":"token"},{"name":"beta","method":"password"}]""")).Status);
        Assert.Equal(password, await CellAsync("Editor", "alpha", "password"));
        Assert.NotNull(await CellAsync("Editor", "beta", "password"));

        // Deleting `beta` takes its cell; null clears `alpha`'s.
        await SaveAsync("Editor", """[{"name":"alpha","method":"token"}]""",
            new() { ["entries"] = new() { ["alpha"] = new() { ["password"] = null } } });
        Assert.Null(await CellAsync("Editor", "alpha", "password"));
        Assert.Null(await CellAsync("Editor", "beta", "password"));

        // A row added back under the old name inherits nothing.
        await SaveAsync("Editor", """[{"name":"alpha","method":"token"},{"name":"beta","method":"token"}]""");
        Assert.Null(await CellAsync("Editor", "beta", "password"));
    }

    [Fact]
    public async Task A_secret_cell_in_config_an_unknown_row_or_an_invalid_row_is_refused_and_nothing_is_saved()
    {
        await HireAsync("Strict");
        var password = Value("in-config");

        var inConfig = await SaveAsync("Strict", $$"""[{"name":"alpha","method":"password","password":"{{password}}"}]""");
        Assert.Equal(HttpStatusCode.BadRequest, inConfig.Status);
        Assert.Contains("is a secret column", inConfig.Body);
        Assert.DoesNotContain(password, inConfig.Body, StringComparison.Ordinal);

        var ghost = await SaveAsync("Strict", """[{"name":"alpha","method":"password"}]""",
            new() { ["entries"] = new() { ["ghost"] = new() { ["password"] = Value("ghost") } } });
        Assert.Equal(HttpStatusCode.BadRequest, ghost.Status);
        Assert.Contains("has no row 'ghost'", ghost.Body);

        var duplicate = await SaveAsync("Strict", """[{"name":"alpha","method":"token"},{"name":"ALPHA","method":"token"}]""");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.Status);
        Assert.Contains("must be unique", duplicate.Body);

        var stored = await GetAsync($"/api/teams/{_team}/members/Strict/plugin-settings");
        Assert.False(stored.GetProperty("config").TryGetProperty("entries", out _));
    }

    [Fact]
    public async Task An_agents_hire_and_a_manager_cannot_add_rows_flip_active_or_set_a_secret_cell()
    {
        var managerKey = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, _team, TeamRegistry.ManagerPermits, ct: Ct);
        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, managerKey);

        // A Manager's hire carrying a row is refused, and no member is made.
        var withRows = await manager.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Planted",
            agent = "plugin:tabled",
            config = new Dictionary<string, JsonElement> { ["entries"] = Rows("""[{"name":"evil","method":"token"}]""") },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, withRows.StatusCode);
        Assert.Contains("is a table only a person fills", await withRows.Content.ReadAsStringAsync(Ct));
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Planted")));

        // Empty is allowed.
        var empty = await manager.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Empty",
            agent = "plugin:tabled",
            config = new Dictionary<string, JsonElement> { ["entries"] = Rows("[]") },
        }, Ct);
        Assert.True(empty.IsSuccessStatusCode, await empty.Content.ReadAsStringAsync(Ct));

        // A person adds a row; the Manager cannot flip its Active, add a row or set its secret.
        await SaveAsync("Empty", """[{"name":"alpha","method":"token","active":true}]""");
        foreach (var body in new object[]
        {
            new { config = new Dictionary<string, JsonElement> { ["entries"] = Rows("""[{"name":"alpha","method":"token","active":false}]""") } },
            new { config = new Dictionary<string, JsonElement> { ["entries"] = Rows("""[{"name":"alpha","method":"token"},{"name":"evil","method":"token"}]""") } },
            new
            {
                config = new Dictionary<string, JsonElement> { ["entries"] = Rows("""[{"name":"alpha","method":"token"}]""") },
                tableSecretValues = new Dictionary<string, object> { ["entries"] = new Dictionary<string, object> { ["alpha"] = new Dictionary<string, string> { ["password"] = Value("planted") } } },
            },
        })
        {
            var refused = await manager.PutAsJsonAsync($"/api/teams/{_team}/members/Empty/plugin-settings", body, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        var rows = (await GetAsync($"/api/teams/{_team}/members/Empty/plugin-settings")).GetProperty("config").GetProperty("entries");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.True(rows[0].GetProperty("active").GetBoolean());
        Assert.Null(await CellAsync("Empty", "alpha", "password"));
    }

    [Fact]
    public async Task Row_status_writes_only_the_read_only_cells_of_a_row_the_member_has_and_a_persons_save_keeps_them()
    {
        await HireAsync("Checker");
        await SaveAsync("Checker", """[{"name":"alpha","method":"password","active":true}]""");

        await TellAndAwaitAsync("Checker");

        var rows = (await GetAsync($"/api/teams/{_team}/members/Checker/plugin-settings")).GetProperty("config").GetProperty("entries");
        Assert.Equal(1, rows.GetArrayLength());
        var alpha = rows[0];
        Assert.Equal("Verified", alpha.GetProperty("status").GetString());
        Assert.True(alpha.GetProperty("active").GetBoolean());
        Assert.False(alpha.TryGetProperty("colour", out _));

        // A person's save cannot forge the status, and keeps the plugin's.
        await SaveAsync("Checker", """[{"name":"alpha","method":"token","status":"Forged"}]""");
        alpha = (await GetAsync($"/api/teams/{_team}/members/Checker/plugin-settings")).GetProperty("config").GetProperty("entries")[0];
        Assert.Equal("Verified", alpha.GetProperty("status").GetString());
        Assert.Equal("token", alpha.GetProperty("method").GetString());
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static string Value(string what) => $"{what}-{Guid.NewGuid():N}";

    private static JsonElement Rows(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private JsonNode LastRequest(string member) =>
        File.ReadAllLines(Requests).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!)
            .Last(r => (string?)r["member"]?["name"] == member);

    private Task<string?> CellAsync(string member, string row, string column) =>
        Services.GetRequiredService<PluginMemberSecretStore>().CellValueAsync(new ContainerId(_team, member), "entries", row, column, Ct);

    private async Task HireAsync(string name)
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name, agent = "plugin:tabled" }, Ct);
        var body = await hired.Content.ReadAsStringAsync(Ct);
        _responses.Add(body);
        Assert.True(hired.StatusCode == HttpStatusCode.OK, body);
    }

    private Task<(HttpStatusCode Status, string Body)> SaveAsync(
        string member, string rows, Dictionary<string, Dictionary<string, Dictionary<string, string?>>>? cells = null) =>
        SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/{member}/plugin-settings", new
        {
            config = new Dictionary<string, JsonElement> { ["entries"] = Rows(rows) },
            tableSecretValues = cells,
        });

    private async Task<Message> TellAndAwaitAsync(string member)
    {
        var who = new ContainerId(_team, member);
        var log = Services.GetRequiredService<IMessageLog>();

        async Task<IReadOnlyList<Message>> Terminal() =>
            [.. (await log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct)).Where(m => m.Source == who.ToString())];

        var after = (await Terminal()).Select(m => m.Seq).DefaultIfEmpty(0).Max();

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{member}/tell", new { instruction = "check" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Terminal()).FirstOrDefault(m => m.Seq > after) is { } row)
            {
                Assert.True(row.Type == MessageTypes.Completed, row.Payload);
                var host = Services.GetRequiredService<Harness.Containers.ContainerHost>();
                while (host.Find(who)!.State != ContainerState.Idle && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
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

    private async Task<IReadOnlyList<string>> ColumnAsync(string sql, string key)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }
}
