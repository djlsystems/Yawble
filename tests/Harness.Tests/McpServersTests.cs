using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// MCP 1 - ADMIN → MCP SERVERS AND TEAM SETTINGS → MCP SERVERS on the real Host, each check run by the
/// Host's own worker against a FAKE MCP server answering through the worker's HTTP handler: nothing reaches
/// the network. A web address lists its tools on save; a wrong token marks the entry and its connection
/// Needs you; a server that is down fails the check with a sentence and marks neither. A "Yawble runs it"
/// entry with no pinned version is refused. A team sees only what it was given, and removing an entry
/// takes it from every team with one row each. No value or query string is in an answer, a row or a log.
/// </summary>
public sealed class McpServersTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string PersonPassword = "correct horse battery";
    private const string Token = "mcp-right-token-7f3a91";
    private const string Wrong = "mcp-wrong-token-on-purpose";
    private const string QueryKey = "query-secret-c0ffee42";
    private const string Host = "mcp.example.test";
    private static readonly string Url = $"https://{Host}/mcp?key={QueryKey}";

    private static readonly string[] Secrets = [Token, Wrong, QueryKey, $"key={QueryKey}"];

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-test-mcp-{Guid.NewGuid():N}");
    private readonly FakeMcpServer _server = new(Token, QueryKey);
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly List<string> _responses = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Trace")
            .ConfigureLogging(logging => logging.AddProvider(new ListLogger(_logs)))
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton(new ConnectionTester(http: _server, timeout: TimeSpan.FromSeconds(5)));
            }));

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, PersonPassword);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = PersonPassword }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // ---- a web address: its check on save and on Test --------------------------------------------------

    [Fact]
    public async Task A_web_address_lists_its_tools_on_save_and_the_list_shows_them()
    {
        var key = await KeyAsync(Token);

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", Web("github", key));
        Assert.True(status == HttpStatusCode.Created, body);

        var answer = JsonDocument.Parse(body).RootElement;
        var check = answer.GetProperty("check");
        Assert.Equal("ok", check.GetProperty("outcome").GetString());
        Assert.Equal("Connected: github listed 3 tools.", check.GetProperty("sentence").GetString());
        Assert.Equal(["search_issues", "create_issue", "list_repos"], Names(check.GetProperty("tools")));
        Assert.Equal(3, check.GetProperty("toolCount").GetInt32());

        var server = answer.GetProperty("server");
        Assert.Equal($"https://{Host}/mcp", server.GetProperty("url").GetString());
        Assert.Equal("ok", server.GetProperty("health").GetString());

        // THE FAKE SERVER SAW what an MCP client sends: initialize, initialized, then the tools list, with the
        // header the template made and the session it gave.
        Assert.Equal(["initialize", "notifications/initialized", "tools/list"], _server.Methods);
        Assert.All(_server.Authorizations, a => Assert.Equal($"Bearer {Token}", a));
        Assert.All(_server.Sessions.Skip(1), s => Assert.Equal(FakeMcpServer.SessionId, s));

        var listed = Listed(await GetAsync("/api/mcp-servers"), "github");
        Assert.Equal(3, listed.GetProperty("toolCount").GetInt32());
        Assert.Equal(key, listed.GetProperty("connection").GetProperty("id").GetString());

        var rows = await TenantRowsAsync();
        Assert.Single(rows, r => r.Action == TenantActions.McpServerAdded);
        Assert.Contains("\"outcome\":\"ok\"", Assert.Single(rows, r => r.Action == TenantActions.McpServerChecked).Detail);

        await NothingLeakedAsync();
    }

    [Fact]
    public async Task A_server_answering_in_an_event_stream_page_by_page_is_listed_whole()
    {
        _server.EventStream = true;
        _server.PageSize = 2;

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", new { name = "paged", kind = "web", url = $"https://{Host}/mcp?key={QueryKey}" });
        Assert.True(status == HttpStatusCode.Created, body);

        var check = JsonDocument.Parse(body).RootElement.GetProperty("check");
        Assert.Equal("ok", check.GetProperty("outcome").GetString());
        Assert.Equal(["search_issues", "create_issue", "list_repos"], Names(check.GetProperty("tools")));
        Assert.Equal(["initialize", "notifications/initialized", "tools/list", "tools/list"], _server.Methods);
    }

    [Fact]
    public async Task A_wrong_token_marks_the_entry_and_its_connection_needs_you_and_a_right_one_clears_the_entry()
    {
        var key = await KeyAsync(Wrong);

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", Web("github", key));
        Assert.True(status == HttpStatusCode.Created, body);

        var check = JsonDocument.Parse(body).RootElement.GetProperty("check");
        Assert.Equal("refused", check.GetProperty("outcome").GetString());
        Assert.Equal($"{Host} refused the credential (HTTP 401).", check.GetProperty("sentence").GetString());

        var entry = Listed(await GetAsync("/api/mcp-servers"), "github");
        Assert.Equal("needs-you", entry.GetProperty("health").GetString());
        Assert.StartsWith($"{Host} refused the credential (HTTP 401).", entry.GetProperty("healthReason").GetString());

        var connection = (await GetAsync("/api/connections")).EnumerateArray().Single(c => c.GetProperty("id").GetString() == key);
        Assert.Equal("needs-reconnect", connection.GetProperty("status").GetString());
        Assert.Contains("MCP server 'github' was refused", connection.GetProperty("statusReason").GetString());
        Assert.Contains(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionNeedsReconnect && r.Subject == key);

        // THE PERSON FIXES THE KEY, and the entry's Test passes and clears its mark.
        var (edited, editBody) = await SendAsync(HttpMethod.Put, $"/api/connections/{key}/credentials", new { fields = new[] { new { name = "token", value = Token, masked = true } } });
        Assert.True(edited == HttpStatusCode.OK, editBody);

        var id = entry.GetProperty("id").GetString()!;
        var (tested, testBody) = await SendAsync(HttpMethod.Post, $"/api/mcp-servers/{id}/test", null);
        Assert.True(tested == HttpStatusCode.OK, testBody);
        Assert.Equal("ok", JsonDocument.Parse(testBody).RootElement.GetProperty("check").GetProperty("outcome").GetString());
        Assert.Equal("ok", Listed(await GetAsync("/api/mcp-servers"), "github").GetProperty("health").GetString());

        await NothingLeakedAsync();
    }

    [Fact]
    public async Task A_server_that_is_down_fails_the_check_with_a_sentence_and_marks_neither()
    {
        var key = await KeyAsync(Token);
        _server.Down = true;

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", Web("github", key));
        Assert.True(status == HttpStatusCode.Created, body);

        var check = JsonDocument.Parse(body).RootElement.GetProperty("check");
        Assert.Equal("failed", check.GetProperty("outcome").GetString());
        Assert.Equal($"Could not reach {Host}.", check.GetProperty("sentence").GetString());

        var entry = Listed(await GetAsync("/api/mcp-servers"), "github");
        Assert.Equal("unchecked", entry.GetProperty("health").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("healthReason").ValueKind);

        var connection = (await GetAsync("/api/connections")).EnumerateArray().Single(c => c.GetProperty("id").GetString() == key);
        Assert.Equal("ok", connection.GetProperty("status").GetString());
        Assert.DoesNotContain(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionNeedsReconnect);

        // AN ENTRY THAT WAS OK STAYS OK when its server is down at its next Test.
        _server.Down = false;
        var id = entry.GetProperty("id").GetString()!;
        await SendAsync(HttpMethod.Post, $"/api/mcp-servers/{id}/test", null);
        _server.Down = true;
        var (_, downBody) = await SendAsync(HttpMethod.Post, $"/api/mcp-servers/{id}/test", null);
        Assert.Equal("failed", JsonDocument.Parse(downBody).RootElement.GetProperty("check").GetProperty("outcome").GetString());
        Assert.Equal("ok", Listed(await GetAsync("/api/mcp-servers"), "github").GetProperty("health").GetString());

        Assert.Contains("\"outcome\":\"failed\"", (await TenantRowsAsync()).Last(r => r.Action == TenantActions.McpServerChecked).Detail);
        await NothingLeakedAsync();
    }

    // ---- Yawble runs it: stored and validated -----------------------------------------------------------

    [Theory]
    [InlineData("npx", "@modelcontextprotocol/server-github", null)]
    [InlineData("npx", "@modelcontextprotocol/server-github@latest", null)]
    [InlineData("npx", "@modelcontextprotocol/server-github", "latest")]
    [InlineData("npx", "@modelcontextprotocol/server-github", "^1.2.0")]
    [InlineData("npx", "@modelcontextprotocol/server-github", "1.x")]
    [InlineData("npx", "@modelcontextprotocol/server-github", "1")]
    [InlineData("npx", "server-github", ">=1.0.0 <2.0.0")]
    [InlineData("uvx", "mcp-server-fetch", null)]
    [InlineData("uvx", "mcp-server-fetch>=0.6", null)]
    [InlineData("uvx", "mcp-server-fetch", "~=0.6")]
    public async Task An_entry_Yawble_runs_with_no_pinned_version_is_refused_with_a_sentence_saying_so(string command, string package, string? version)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", new { name = "runner", kind = "runs", command, package, version });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("version must be pinned", Error(body), StringComparison.Ordinal);
        Assert.Empty((await GetAsync("/api/mcp-servers")).GetProperty("servers").EnumerateArray());
        Assert.DoesNotContain(await TenantRowsAsync(), r => r.Action == TenantActions.McpServerAdded);
    }

    [Fact]
    public async Task An_entry_Yawble_runs_is_stored_with_its_pinned_version_and_says_it_is_checked_when_MCP_3_is_in_place()
    {
        var key = await KeyAsync(Token);

        var (status, body) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", new
        {
            name = "gh_local", kind = "runs", command = "npx", package = "@modelcontextprotocol/server-github@2025.4.8",
            args = new[] { "--read-only" }, env = new[] { new { name = "GITHUB_TOKEN", connection = key, field = "token" } },
        });
        Assert.True(status == HttpStatusCode.Created, body);

        var answer = JsonDocument.Parse(body).RootElement;
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("check").ValueKind);

        var entry = answer.GetProperty("server");
        Assert.Equal("@modelcontextprotocol/server-github", entry.GetProperty("package").GetString());
        Assert.Equal("2025.4.8", entry.GetProperty("version").GetString());
        Assert.Equal("unchecked", entry.GetProperty("health").GetString());
        Assert.Equal("checked when MCP 3 is in place", entry.GetProperty("healthReason").GetString());
        Assert.Equal("GITHUB_TOKEN", entry.GetProperty("env")[0].GetProperty("name").GetString());
        Assert.Empty(_server.Methods);

        var (tested, testBody) = await SendAsync(HttpMethod.Post, $"/api/mcp-servers/{entry.GetProperty("id").GetString()}/test", null);
        Assert.Equal(HttpStatusCode.BadRequest, tested);
        Assert.Contains("checked when MCP 3 is in place", Error(testBody), StringComparison.Ordinal);

        var uvx = await SendAsync(HttpMethod.Post, "/api/mcp-servers", new { name = "fetch", kind = "runs", command = "uvx", package = "mcp-server-fetch==0.6.2" });
        Assert.True(uvx.Status == HttpStatusCode.Created, uvx.Body);

        var bad = await SendAsync(HttpMethod.Post, "/api/mcp-servers", new
        {
            name = "bad_env", kind = "runs", command = "npx", package = "x", version = "1.0.0",
            env = new[] { new { name = "TOKEN", connection = key, field = "nope" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Contains("has no field `nope`", Error(bad.Body), StringComparison.Ordinal);

        await NothingLeakedAsync();
    }

    // ---- names --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_name_is_letters_digits_dash_and_underscore_at_most_20_and_unique_whatever_its_case()
    {
        foreach (var name in new[] { "", "has space", "dots.not", new string('a', 21), "émoji" })
        {
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", new { name, kind = "web", url = "https://other.example.test/mcp" });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("1 to 20 letters, digits, `-` or `_`", Error(body), StringComparison.Ordinal);
        }

        _server.Down = true;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/api/mcp-servers", new { name = new string('a', 20), kind = "web", url = "https://other.example.test/mcp" })).Status);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/api/mcp-servers", new { name = "Git-Hub_2", kind = "web", url = "https://other.example.test/mcp" })).Status);

        var (taken, takenBody) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", new { name = "git-hub_2", kind = "web", url = "https://other.example.test/mcp" });
        Assert.Equal(HttpStatusCode.Conflict, taken);
        Assert.Contains("already an MCP server named 'git-hub_2'", Error(takenBody), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_writes_its_row_naming_what_changed_and_keeps_the_address_when_none_is_given()
    {
        var key = await KeyAsync(Token);
        var (_, body) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", Web("github", key));
        var id = JsonDocument.Parse(body).RootElement.GetProperty("server").GetProperty("id").GetString()!;

        var (status, edited) = await SendAsync(HttpMethod.Put, $"/api/mcp-servers/{id}", new
        {
            name = "github2", kind = "web", connection = key, header = "X-Api-Key", template = "{value}",
        });
        Assert.True(status == HttpStatusCode.OK, edited);

        var answer = JsonDocument.Parse(edited).RootElement;
        Assert.Equal("github2", answer.GetProperty("server").GetProperty("name").GetString());
        Assert.Equal("ok", answer.GetProperty("check").GetProperty("outcome").GetString());
        Assert.Equal(Token, _server.LastApiKey);

        var row = Assert.Single(await TenantRowsAsync(), r => r.Action == TenantActions.McpServerEdited);
        var changed = JsonDocument.Parse(row.Detail!).RootElement.GetProperty("extra").GetProperty("changed");
        Assert.Equal(["name", "header", "template"], Names(changed));

        await NothingLeakedAsync();
    }

    // ---- teams --------------------------------------------------------------------------------------

    [Fact]
    public async Task An_entry_given_to_team_A_shows_in_As_team_settings_and_not_in_Bs()
    {
        var (a, b) = await TeamsAsync();
        var id = await RunsEntryAsync("tools");

        var (status, body) = await SendAsync(HttpMethod.Post, $"/api/teams/{a}/mcp-servers", new { server = id });
        Assert.True(status == HttpStatusCode.Created, body);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/teams/{a}/mcp-servers", new { server = id })).Status);

        var listed = Assert.Single((await GetAsync($"/api/teams/{a}/mcp-servers")).GetProperty("servers").EnumerateArray());
        Assert.Equal(id, listed.GetProperty("server").GetProperty("id").GetString());
        Assert.Empty((await GetAsync($"/api/teams/{b}/mcp-servers")).GetProperty("servers").EnumerateArray());

        Assert.Equal([a], Names(Listed(await GetAsync("/api/mcp-servers"), "tools").GetProperty("teams")));

        var row = Assert.Single(await TenantRowsAsync(), r => r.Action == TenantActions.TeamMcpServerAssigned);
        Assert.Equal((Email, id), (row.ActorEmail, row.Subject));
        Assert.Equal(a, JsonDocument.Parse(row.Detail!).RootElement.GetProperty("team").GetString());

        // TAKEN OFF A, with its own row; the entry stays.
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/teams/{a}/mcp-servers/{id}", null)).Status);
        Assert.Empty((await GetAsync($"/api/teams/{a}/mcp-servers")).GetProperty("servers").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/teams/{a}/mcp-servers/{id}", null)).Status);
        Assert.Equal("removed from the team",
            JsonDocument.Parse(Assert.Single(await TenantRowsAsync(), r => r.Action == TenantActions.TeamMcpServerRemoved).Detail!).RootElement.GetProperty("reason").GetString());
        Assert.Single((await GetAsync("/api/mcp-servers")).GetProperty("servers").EnumerateArray());
    }

    [Fact]
    public async Task Removing_an_entry_removes_it_from_every_team_that_had_it_with_one_row_per_team()
    {
        var (a, b) = await TeamsAsync();
        var c = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("Gamma", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        var id = await RunsEntryAsync("shared");
        var kept = await RunsEntryAsync("kept");

        foreach (var team in new[] { a, b }) Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, $"/api/teams/{team}/mcp-servers", new { server = id })).Status);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, $"/api/teams/{c}/mcp-servers", new { server = kept })).Status);

        var (status, body) = await SendAsync(HttpMethod.Delete, $"/api/mcp-servers/{id}", null);
        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal(new[] { a, b }.Order(StringComparer.OrdinalIgnoreCase), Names(JsonDocument.Parse(body).RootElement.GetProperty("teams")));

        foreach (var team in new[] { a, b }) Assert.Empty((await GetAsync($"/api/teams/{team}/mcp-servers")).GetProperty("servers").EnumerateArray());
        Assert.Single((await GetAsync($"/api/teams/{c}/mcp-servers")).GetProperty("servers").EnumerateArray());

        var rows = (await TenantRowsAsync()).Where(r => r.Action == TenantActions.TeamMcpServerRemoved).ToList();
        Assert.Equal(2, rows.Count);
        foreach (var team in new[] { a, b })
        {
            var row = Assert.Single(rows, r => JsonDocument.Parse(r.Detail!).RootElement.GetProperty("team").GetString() == team);
            Assert.Equal("shared", row.SubjectName);
            Assert.Equal("removed from the instance", JsonDocument.Parse(row.Detail!).RootElement.GetProperty("reason").GetString());
        }

        Assert.Single(await TenantRowsAsync(), r => r.Action == TenantActions.McpServerRemoved);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/mcp-servers/{id}", null)).Status);
    }

    [Fact]
    public async Task A_team_with_none_lists_none_and_an_unknown_team_or_entry_is_not_found()
    {
        var (a, _) = await TeamsAsync();

        Assert.Empty((await GetAsync($"/api/teams/{a}/mcp-servers")).GetProperty("servers").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/api/teams/nobody/mcp-servers", null)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/api/teams/{a}/mcp-servers", new { server = "mcp-none" })).Status);
    }

    // ---- the scan -----------------------------------------------------------------------------------

    /// <summary>
    /// EVERY ROUTE, with a right token, a wrong one and the URL's query: then no value, no query string, in
    /// any answer, tenant row, log line or file the Host wrote.
    /// </summary>
    [Fact]
    public async Task No_credential_value_or_query_string_is_in_any_answer_row_or_log_line_these_routes_wrote()
    {
        var (a, b) = await TeamsAsync();
        var right = await KeyAsync(Token);
        var wrong = await KeyAsync(Wrong, "Wrong key");

        var (_, added) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", Web("right", right));
        var rightId = JsonDocument.Parse(added).RootElement.GetProperty("server").GetProperty("id").GetString()!;
        var (_, refused) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", Web("wrong", wrong));
        var wrongId = JsonDocument.Parse(refused).RootElement.GetProperty("server").GetProperty("id").GetString()!;

        await SendAsync(HttpMethod.Post, $"/api/mcp-servers/{rightId}/test", null);
        await SendAsync(HttpMethod.Put, $"/api/mcp-servers/{rightId}", Web("right2", right));
        _server.Down = true;
        await SendAsync(HttpMethod.Post, $"/api/mcp-servers/{rightId}/test", null);
        _server.Down = false;

        await SendAsync(HttpMethod.Post, $"/api/teams/{a}/mcp-servers", new { server = rightId });
        await SendAsync(HttpMethod.Post, $"/api/teams/{b}/mcp-servers", new { server = wrongId });
        await GetAsync($"/api/teams/{a}/mcp-servers");
        await GetAsync("/api/mcp-servers");
        await GetAsync("/api/tenant-log?take=200");
        await SendAsync(HttpMethod.Delete, $"/api/teams/{a}/mcp-servers/{rightId}", null);
        await SendAsync(HttpMethod.Delete, $"/api/mcp-servers/{wrongId}", null);

        Assert.Contains(_responses, r => r.Contains($"https://{Host}/mcp", StringComparison.Ordinal));
        await NothingLeakedAsync();
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static object Web(string name, string key) => new { name, kind = "web", url = Url, connection = key, header = "Authorization", template = "Bearer {value}" };

    private async Task NothingLeakedAsync()
    {
        var rows = await TenantRowsAsync();

        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(_responses, r => r.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(_logs, l => l.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(rows, r => $"{r.Subject} {r.SubjectName} {r.Detail}".Contains(secret, StringComparison.Ordinal));
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            var text = Encoding.Latin1.GetString(bytes) + Encoding.Unicode.GetString(bytes);
            foreach (var secret in Secrets)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{Path.GetRelativePath(_dataRoot, file)} holds a secret in plain text.");
            }
        }
    }

    private async Task<string> KeyAsync(string value, string name = "MCP key")
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/credentials", new
        {
            kind = "apikey", preset = "other", name, fields = new[] { new { name = "token", value, masked = true } },
        });
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("connection").GetProperty("id").GetString()!;
    }

    private async Task<string> RunsEntryAsync(string name)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/mcp-servers", new { name, kind = "runs", command = "uvx", package = "mcp-server-time", version = "0.6.2" });
        Assert.True(status == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("server").GetProperty("id").GetString()!;
    }

    private async Task<(string A, string B)> TeamsAsync()
    {
        var registry = Services.GetRequiredService<TeamRegistry>();
        var a = (await registry.CreateAsync("Alpha", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        var b = (await registry.CreateAsync("Beta", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        return (a, b);
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

    private async Task<JsonElement> GetAsync(string path)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, path, null);
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement Listed(JsonElement list, string name) =>
        list.GetProperty("servers").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    private static string[] Names(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static string Error(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()!;

    private async Task<IReadOnlyList<(string Action, string? ActorEmail, string? Subject, string? SubjectName, string? Detail)>> TenantRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT action, actor_email, subject, subject_name, detail FROM tenant_events ORDER BY seq";
        var rows = new List<(string, string?, string?, string?, string?)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            rows.Add((reader.GetString(0), Text(1), Text(2), Text(3), Text(4)));
        }

        return rows;
    }

    /// <summary>
    /// A FAKE MCP SERVER at <see cref="Host"/>, as the worker's HTTP handler: streamable HTTP, a session id on
    /// initialize, three tools (paged when <see cref="PageSize"/> is set, as JSON or an event stream). It
    /// answers 401 unless the request carries the right token, in <c>Authorization</c> or <c>X-Api-Key</c>,
    /// and the right key in the query. <see cref="Down"/> makes it unreachable. Any other host is unreachable too.
    /// </summary>
    private sealed class FakeMcpServer(string token, string queryKey) : HttpMessageHandler
    {
        public const string SessionId = "session-4242";

        private static readonly string[] Tools = ["search_issues", "create_issue", "list_repos"];

        public bool Down { get; set; }

        public bool EventStream { get; set; }

        public int PageSize { get; set; } = 10;

        public List<string> Methods { get; } = [];

        public List<string?> Authorizations { get; } = [];

        public List<string?> Sessions { get; } = [];

        public string? LastApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down || request.RequestUri!.Host != Host) throw new HttpRequestException($"Connection refused ({request.RequestUri!.Host})");

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var method = body["method"]!.GetValue<string>();
            var authorization = request.Headers.Authorization?.ToString();
            var apiKey = request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.First() : null;

            lock (Methods)
            {
                Methods.Add(method);
                if (authorization is not null) Authorizations.Add(authorization);
                Sessions.Add(request.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.First() : null);
                if (apiKey is not null) LastApiKey = apiKey;
            }

            var authorized = authorization == $"Bearer {token}" || apiKey == token || (authorization is null && apiKey is null);
            if (!authorized || request.RequestUri.Query != $"?key={queryKey}")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"bad token"}""") };
            }

            if (body["id"] is null) return new HttpResponseMessage(HttpStatusCode.Accepted);

            JsonNode result;
            if (method == "initialize")
            {
                result = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "fake" } };
            }
            else
            {
                var start = int.TryParse(body["params"]?["cursor"]?.GetValue<string>(), out var cursor) ? cursor : 0;
                var page = new JsonArray([.. Tools.Skip(start).Take(PageSize).Select(n => (JsonNode)new JsonObject { ["name"] = n, ["inputSchema"] = new JsonObject() })]);
                result = new JsonObject { ["tools"] = page };
                if (start + PageSize < Tools.Length) result["nextCursor"] = (start + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = result }.ToJsonString();
            var response = EventStream
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($": keep-alive\n\nevent: message\ndata: {{\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\"}}\n\nevent: message\ndata: {answer}\n\n", Encoding.UTF8, "text/event-stream"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };

            if (method == "initialize") response.Headers.Add("Mcp-Session-Id", SessionId);
            return response;
        }
    }

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
