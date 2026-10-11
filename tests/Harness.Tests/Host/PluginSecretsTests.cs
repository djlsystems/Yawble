using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A PLUGIN KEY SET FROM THE SCREEN, on the real Host with a plugin that reads one required secret.
/// <c>PUT</c> and <c>DELETE /api/secrets/{key}</c> are a person's: a key set there reaches the
/// member's next run on stdin with no restart, wins over the Host's environment, and clearing it
/// falls back to the environment. Each write lands with its tenant row or not at all, a machine
/// principal is refused, and no value appears in any response, row or file under the data root.
/// </summary>
public sealed class PluginSecretsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-secrets-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-plugin-secrets-out-{Guid.NewGuid():N}");
    private readonly List<string> _responses = [];
    private readonly List<string> _environmentKeys = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    /// <summary>Each request the plugin was handed on stdin, one per line, kept OUTSIDE the data root
    /// so the no-leak scan does not find the plugin's own copy.</summary>
    private string Requests => Path.Combine(_outside, "requests.jsonl");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        // The plugin also ECHOES its request: a value it prints must be redacted from what the Host keeps.
        PluginInstall.Write(_dataRoot, "keyed",
            script: $"req=$(cat); printf '%s\\n' \"$req\" >> '{Requests}'; printf '%s\\n' \"$req\"; echo '{{\"t\":\"result\",\"ok\":true,\"output\":\"read\"}}'",
            manifest: PluginInstall.Manifest("keyed", edit: m => m["secrets"] = JsonNode.Parse("""{"token":{"description":"The service's token.","required":true}}""")));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Keys", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _environmentKeys) Environment.SetEnvironmentVariable(key, null);
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
    public async Task A_key_set_from_the_route_reaches_the_next_plugin_run_on_stdin_with_no_restart()
    {
        var key = Key();
        var first = Value("first");
        var second = Value("second");

        var set = await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = first });
        Assert.Equal(HttpStatusCode.OK, set.Status);
        var answer = Json(set.Body);
        Assert.True(answer.GetProperty("set").GetBoolean());
        Assert.Equal("stored", answer.GetProperty("source").GetString());
        Assert.Equal(Email, answer.GetProperty("setBy").GetString());
        Assert.True(answer.GetProperty("setAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));

        await HireAsync("Reader", key);
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Reader")).Type);
        Assert.Equal(first, LastToken());

        // Replaced on the same Host: the next run reads the new value.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = second })).Status);
        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync("Reader")).Type);
        Assert.Equal(second, LastToken());

        Assert.Equal([TenantActions.SecretSet, TenantActions.SecretReplaced], await TenantActionsAsync(key));
        Assert.All(await TenantDetailsAsync(key), detail => Assert.Equal(JsonSerializer.Serialize(new { key }), detail));
    }

    [Fact]
    public async Task The_stored_key_wins_over_the_environment_and_clearing_it_falls_back_to_the_environment()
    {
        var key = Key();
        var fromEnvironment = Value("environment");
        var fromScreen = Value("screen");
        SetEnvironment(key, fromEnvironment);

        var before = await GetAsync($"/api/secrets/{key}");
        Assert.True(before.GetProperty("set").GetBoolean());
        Assert.Equal("environment", before.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("setBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("setAt").ValueKind);

        await HireAsync("Reader", key);
        await TellAndAwaitAsync("Reader");
        Assert.Equal(fromEnvironment, LastToken());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = fromScreen })).Status);
        Assert.Equal("stored", (await GetAsync($"/api/secrets/{key}")).GetProperty("source").GetString());
        await TellAndAwaitAsync("Reader");
        Assert.Equal(fromScreen, LastToken());

        var cleared = await SendAsync(HttpMethod.Delete, $"/api/secrets/{key}", null);
        Assert.Equal(HttpStatusCode.OK, cleared.Status);
        var after = Json(cleared.Body);
        Assert.True(after.GetProperty("set").GetBoolean());
        Assert.Equal("environment", after.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("setBy").ValueKind);
        await TellAndAwaitAsync("Reader");
        Assert.Equal(fromEnvironment, LastToken());

        // A second clear finds nothing stored: the same answer, and no row.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/secrets/{key}", null)).Status);
        Assert.Equal([TenantActions.SecretSet, TenantActions.SecretCleared], await TenantActionsAsync(key));
    }

    [Fact]
    public async Task No_value_appears_in_any_response_row_or_file_under_the_data_root()
    {
        var key = Key();
        var values = new[] { Value("leak-first"), Value("leak-second") };

        await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = values[0] });
        await HireAsync("Reader", key);
        await TellAndAwaitAsync("Reader");
        await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = values[1] });
        await TellAndAwaitAsync("Reader");
        await GetAsync($"/api/secrets/{key}");
        await GetAsync($"/api/teams/{_team}/members/Reader/plugin-settings");
        await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = "" });
        await SendAsync(HttpMethod.Delete, $"/api/secrets/{key}", null);

        // The plugin was handed both (so the scan below is not vacuous) and echoed them.
        var requests = await File.ReadAllTextAsync(Requests, Ct);
        Assert.All(values, value => Assert.Contains(value, requests, StringComparison.Ordinal));

        foreach (var value in values)
        {
            Assert.All(_responses, body => Assert.DoesNotContain(value, body, StringComparison.Ordinal));
        }

        SqliteConnection.ClearAllPools();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            scanned++;
            foreach (var value in values)
            {
                Assert.True(
                    bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) < 0
                    && bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(value)) < 0,
                    $"A key's value is in {Path.GetRelativePath(_dataRoot, file)}.");
            }
        }

        Assert.True(scanned > 0);
    }

    [Fact]
    public async Task A_machine_principal_is_refused_and_nothing_changes()
    {
        var key = Key();
        var value = Value("machine");
        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container,
            _team, Permits.All, ct: Ct));

        var put = await manager.PutAsJsonAsync($"/api/secrets/{key}", new { value }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(PermitGate.HumansOnlyMessage, Json(await put.Content.ReadAsStringAsync(Ct)).GetProperty("error").GetString());
        Assert.Null(Store.TryGet(key));

        // Set by a person, then a machine's clear is refused and the key stays.
        await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value });
        var delete = await manager.DeleteAsync($"/api/secrets/{key}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(value, Store.TryGet(key));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/secrets/{key}", Ct)).StatusCode);

        Assert.Equal([TenantActions.SecretSet], await TenantActionsAsync(key));
    }

    [Fact]
    public async Task A_write_whose_tenant_row_cannot_land_changes_nothing()
    {
        var key = Key();
        var first = Value("kept");

        await RefuseTenantRowAsync(TenantActions.SecretSet);
        var set = await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = first });
        Assert.Equal(HttpStatusCode.InternalServerError, set.Status);
        Assert.Contains("was not changed", Json(set.Body).GetProperty("error").GetString());
        Assert.False(Json(set.Body).GetProperty("set").GetBoolean());
        Assert.Null(Store.TryGet(key));
        Assert.Empty(await StoredKeysAsync(key));

        await AllowTenantRowsAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = first })).Status);
        var stored = Store.Status(key);

        await RefuseTenantRowAsync(TenantActions.SecretReplaced);
        Assert.Equal(HttpStatusCode.InternalServerError, (await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = Value("lost") })).Status);
        Assert.Equal(first, Store.TryGet(key));
        Assert.Equal(stored, Store.Status(key));

        await RefuseTenantRowAsync(TenantActions.SecretCleared);
        var cleared = await SendAsync(HttpMethod.Delete, $"/api/secrets/{key}", null);
        Assert.Equal(HttpStatusCode.InternalServerError, cleared.Status);
        Assert.Equal("stored", Json(cleared.Body).GetProperty("source").GetString());
        Assert.Equal(first, Store.TryGet(key));

        await AllowTenantRowsAsync();
        Assert.Equal([TenantActions.SecretSet], await TenantActionsAsync(key));
    }

    [Theory]
    [InlineData("HARNESS_URL", "platform's own")]
    [InlineData("HARNESS_ANYTHING_NEW", "platform's own")]
    [InlineData("ANTHROPIC_API_KEY", "model provider's credential")]
    [InlineData("lower_case", "not a secret key")]
    public async Task A_refused_name_answers_400_with_its_refusal_and_stores_nothing(string key, string refusal)
    {
        foreach (var (method, body) in new (HttpMethod, object?)[] { (HttpMethod.Put, new { value = Value("refused") }), (HttpMethod.Delete, null) })
        {
            var answer = await SendAsync(method, $"/api/secrets/{key}", body);
            Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
            var json = Json(answer.Body);
            Assert.Contains(refusal, json.GetProperty("refusal").GetString());
            Assert.Equal(json.GetProperty("refusal").GetString(), json.GetProperty("error").GetString());
            Assert.False(json.GetProperty("set").GetBoolean());
        }

        Assert.Empty(await StoredKeysAsync(key));
        Assert.Empty(await TenantActionsAsync(key));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("abc", "shorter than 4")]
    [InlineData(" padded-value", "white space")]
    [InlineData("two\nlines", "line break")]
    public async Task A_value_that_cannot_be_kept_is_refused_with_a_sentence_that_does_not_repeat_it(string value, string words)
    {
        var key = Key();

        var answer = await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value });

        Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
        var error = Json(answer.Body).GetProperty("error").GetString()!;
        Assert.Contains(words, error);
        if (value.Trim().Length > 0) Assert.DoesNotContain(value.Trim(), error);
        Assert.Empty(await StoredKeysAsync(key));
    }

    [Fact]
    public async Task Set_with_offers_the_screen_first_and_the_operator_cli_second()
    {
        var key = Key();

        var setWith = (await GetAsync($"/api/secrets/{key}")).GetProperty("setWith").GetString()!;

        var screen = setWith.IndexOf("member's settings on the screen", StringComparison.Ordinal);
        var cli = setWith.IndexOf($"`secret set {key}`", StringComparison.Ordinal);
        Assert.True(screen >= 0 && cli > screen, setWith);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private PluginSecretStore Store => Services.GetRequiredService<PluginSecretStore>();

    private static string Key() => $"PLUGIN_SECRETS_TEST_{Guid.NewGuid():N}".ToUpperInvariant();

    private static string Value(string what) => $"{what}-{Guid.NewGuid():N}";

    private void SetEnvironment(string key, string value)
    {
        _environmentKeys.Add(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    private string? LastToken()
    {
        var request = JsonNode.Parse(File.ReadAllLines(Requests).Last(l => l.Length > 0))!;
        return (string?)request["secrets"]?["token"];
    }

    private async Task HireAsync(string name, string key)
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name,
            agent = "plugin:keyed",
            secrets = new Dictionary<string, string> { ["token"] = key },
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

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{member}/tell", new { instruction = "read" }, Ct);
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

    private async Task<JsonElement> GetAsync(string path)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, path, null);
        Assert.True(status == HttpStatusCode.OK, $"{path}: {status} {body}");
        return Json(body);
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

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement.Clone();

    private async Task<IReadOnlyList<string>> TenantActionsAsync(string key) =>
        await ColumnAsync("SELECT action FROM tenant_events WHERE subject = $key ORDER BY seq", key);

    private async Task<IReadOnlyList<string>> TenantDetailsAsync(string key) =>
        await ColumnAsync("SELECT detail FROM tenant_events WHERE subject = $key ORDER BY seq", key);

    private async Task<IReadOnlyList<string>> StoredKeysAsync(string key) =>
        await ColumnAsync("SELECT key FROM plugin_secrets WHERE key = $key", key);

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

    /// <summary>A tenant row of <paramref name="action"/> cannot be written from now on.</summary>
    private Task RefuseTenantRowAsync(string action) =>
        ExecuteAsync(
            "DROP TRIGGER IF EXISTS refuse_row; "
            + $"CREATE TRIGGER refuse_row BEFORE INSERT ON tenant_events WHEN NEW.action = '{action}' "
            + "BEGIN SELECT RAISE(ABORT, 'the disk is full'); END;");

    private Task AllowTenantRowsAsync() => ExecuteAsync("DROP TRIGGER IF EXISTS refuse_row");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
