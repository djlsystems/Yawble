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
/// EACH PLUGIN MEMBER OWNS ITS SECRETS, on the real Host with a fixture plugin that declares a masked
/// required <c>token</c> and an unmasked <c>account</c>. A value a person types for one member reaches
/// that member's runs on stdin and no other's; it wins over a key binding, and clearing it falls back
/// to the key (stored, then the environment). Every write is a person's and lands with its tenant row
/// or not at all; no masked value appears in any answer, row or file under the data root; an unmasked
/// value is answered. The rows go with the member and its team, and a clone copies none.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PluginMemberSecretsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-member-secrets-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-member-secrets-out-{Guid.NewGuid():N}");
    private readonly List<string> _responses = [];
    private readonly List<string> _environmentKeys = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    /// <summary>The fixture plugin's description: two paragraphs, answered whole.</summary>
    private const string Description = "Reads one account of a fixture service.\n\nEach member reads its own account.";

    /// <summary>Each request the plugin was handed on stdin, one per line, kept OUTSIDE the data root
    /// so the no-leak scan does not find the plugin's own copy.</summary>
    private string Requests => Path.Combine(_outside, "requests.jsonl");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        // The plugin ECHOES its request: a masked value it prints must be redacted from what the Host keeps.
        PluginInstall.Write(_dataRoot, "owned",
            script: $"req=$(cat); printf '%s\\n' \"$req\" >> '{Requests}'; printf '%s\\n' \"$req\"; echo '{{\"t\":\"result\",\"ok\":true,\"output\":\"read\"}}'",
            manifest: PluginInstall.Manifest("owned", edit: m =>
            {
                m["description"] = Description;
                m["secrets"] = JsonNode.Parse(
                    """
                    {
                      "token": { "description": "The service's token.", "required": true, "label": "Service token" },
                      "account": { "description": "The account's name.", "masked": false, "label": "Account name" }
                    }
                    """);
            }));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Owners", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

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
    public async Task Two_members_of_one_plugin_each_receive_their_own_value_and_changing_one_leaves_the_other_unchanged()
    {
        var first = Value("first-account");
        var second = Value("second-account");

        await HireAsync("One", new() { ["token"] = first });
        await HireAsync("Two", new() { ["token"] = second });

        await TellAndAwaitAsync("One");
        await TellAndAwaitAsync("Two");
        Assert.Equal(first, LastSecret("One", "token"));
        Assert.Equal(second, LastSecret("Two", "token"));

        // From the screen: the second member's value is replaced; the first member's run is unchanged.
        var replaced = Value("second-replaced");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Two/secrets/token", new { value = replaced })).Status);

        await TellAndAwaitAsync("One");
        await TellAndAwaitAsync("Two");
        Assert.Equal(first, LastSecret("One", "token"));
        Assert.Equal(replaced, LastSecret("Two", "token"));

        // A field set only by the member's value is named by the field on stdin.
        Assert.Equal("token", (string?)LastRequest("One")["secretKeys"]?["token"]);
    }

    [Fact]
    public async Task A_member_value_wins_over_its_key_and_clearing_it_falls_back_to_the_key_then_the_environment()
    {
        var key = Key();
        var fromEnvironment = Value("environment");
        var fromKey = Value("stored-key");
        var own = Value("member-own");
        SetEnvironment(key, fromEnvironment);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = fromKey })).Status);

        await HireAsync("Bound", new() { ["token"] = own }, bindings: new() { ["token"] = key });
        await TellAndAwaitAsync("Bound");
        Assert.Equal(own, LastSecret("Bound", "token"));
        Assert.Equal(key, (string?)LastRequest("Bound")["secretKeys"]?["token"]);
        Assert.Equal("member", State(await GetAsync($"/api/teams/{_team}/members/Bound/secrets"), "token").GetProperty("source").GetString());

        var cleared = await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/members/Bound/secrets/token", null);
        Assert.Equal(HttpStatusCode.OK, cleared.Status);
        Assert.Equal("key", Json(cleared.Body).GetProperty("source").GetString());
        await TellAndAwaitAsync("Bound");
        Assert.Equal(fromKey, LastSecret("Bound", "token"));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/secrets/{key}", null)).Status);
        Assert.Equal("environment", State(await GetAsync($"/api/teams/{_team}/members/Bound/secrets"), "token").GetProperty("source").GetString());
        await TellAndAwaitAsync("Bound");
        Assert.Equal(fromEnvironment, LastSecret("Bound", "token"));
    }

    [Fact]
    public async Task No_masked_value_appears_in_any_response_row_or_file_under_the_data_root_and_an_unmasked_value_is_answered()
    {
        var masked = new[] { Value("leak-hire"), Value("leak-route"), Value("leak-save") };
        var account = Value("account-name");

        await HireAsync("Reader", new() { ["token"] = masked[0], ["account"] = account });
        await TellAndAwaitAsync("Reader");
        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Reader/secrets/token", new { value = masked[1] });
        await TellAndAwaitAsync("Reader");
        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Reader/plugin-settings", new
        {
            config = new { },
            secretValues = new Dictionary<string, string?> { ["token"] = masked[2] },
        });
        await TellAndAwaitAsync("Reader");

        var read = await GetAsync($"/api/teams/{_team}/members/Reader/secrets");
        var settings = await GetAsync($"/api/teams/{_team}/members/Reader/plugin-settings");
        await GetAsync("/api/plugins");
        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Reader/secrets/token", new { value = "" });

        // The unmasked value IS answered, by the read and by the settings read; the masked one never is.
        var accountState = State(read, "account");
        Assert.Equal(account, accountState.GetProperty("value").GetString());
        Assert.False(accountState.GetProperty("masked").GetBoolean());
        Assert.Equal("Account name", accountState.GetProperty("label").GetString());
        Assert.False(State(read, "token").TryGetProperty("value", out _));
        Assert.Equal(Email, State(read, "token").GetProperty("setBy").GetString());
        Assert.Equal(account, settings.GetProperty("secretStates").EnumerateArray().Single(e => e.GetProperty("field").GetString() == "account").GetProperty("value").GetString());
        Assert.Equal("Service token", settings.GetProperty("secretFields").GetProperty("token").GetProperty("label").GetString());
        Assert.True(settings.GetProperty("secretFields").GetProperty("token").GetProperty("masked").GetBoolean());

        // The plugin was handed every masked value (so the scan below is not vacuous) and echoed it.
        var requests = await File.ReadAllTextAsync(Requests, Ct);
        Assert.All(masked, value => Assert.Contains(value, requests, StringComparison.Ordinal));

        foreach (var value in masked)
        {
            Assert.All(_responses, body => Assert.DoesNotContain(value, body, StringComparison.Ordinal));
        }

        // The new table holds ciphertext only.
        Assert.NotEmpty(await ColumnAsync("SELECT value_protected FROM plugin_member_secrets", ""));
        Assert.All(await ColumnAsync("SELECT value_protected FROM plugin_member_secrets", ""), cipher =>
            Assert.All(masked.Append(account), value => Assert.DoesNotContain(value, cipher, StringComparison.Ordinal)));

        SqliteConnection.ClearAllPools();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            scanned++;
            foreach (var value in masked)
            {
                Assert.True(
                    bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) < 0
                    && bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(value)) < 0,
                    $"A member's masked value is in {Path.GetRelativePath(_dataRoot, file)}.");
            }
        }

        Assert.True(scanned > 0);
    }

    [Fact]
    public async Task Set_replace_and_clear_each_write_their_row_in_the_same_transaction_and_change_nothing_when_it_cannot_be_written()
    {
        var first = Value("kept");
        await HireAsync("Writer", new() { ["token"] = first });
        Assert.Equal([TenantActions.MemberSecretSet], await SecretRowsAsync("Writer"));

        await RefuseTenantRowAsync(TenantActions.MemberSecretReplaced);
        var replaced = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Writer/secrets/token", new { value = Value("lost") });
        Assert.Equal(HttpStatusCode.InternalServerError, replaced.Status);
        Assert.Contains("was not changed", Json(replaced.Body).GetProperty("error").GetString());
        Assert.Equal(first, await OwnValueAsync("Writer", "token"));

        // The settings Save carries values in the same transaction as the settings: nothing lands.
        var save = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Writer/plugin-settings", new
        {
            config = new { },
            secretValues = new Dictionary<string, string?> { ["token"] = Value("lost-save") },
        });
        Assert.Equal(HttpStatusCode.InternalServerError, save.Status);
        Assert.Equal(first, await OwnValueAsync("Writer", "token"));
        Assert.DoesNotContain(TenantActions.MemberPluginSettingsChanged, await MemberRowsAsync("Writer"));

        await RefuseTenantRowAsync(TenantActions.MemberSecretCleared);
        var cleared = await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/members/Writer/secrets/token", null);
        Assert.Equal(HttpStatusCode.InternalServerError, cleared.Status);
        Assert.Equal("member", Json(cleared.Body).GetProperty("source").GetString());
        Assert.Equal(first, await OwnValueAsync("Writer", "token"));

        // A hire whose values cannot be kept makes no member.
        await RefuseTenantRowAsync(TenantActions.MemberSecretSet);
        var hire = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Lost",
            agent = "plugin:owned",
            secretValues = new Dictionary<string, string> { ["token"] = Value("lost-hire") },
        }, Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, hire.StatusCode);
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Lost")));
        Assert.Empty(await ColumnAsync("SELECT name FROM team_members WHERE name = $key", "Lost"));
        Assert.Empty(await ColumnAsync("SELECT name FROM plugin_member_secrets WHERE name = $key", "Lost"));

        await AllowTenantRowsAsync();
        var second = Value("replaced");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Writer/secrets/token", new { value = second })).Status);
        Assert.Equal(second, await OwnValueAsync("Writer", "token"));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/members/Writer/secrets/token", null)).Status);

        // Clearing with nothing stored writes no row.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/members/Writer/secrets/token", null)).Status);
        Assert.Equal(
            [TenantActions.MemberSecretSet, TenantActions.MemberSecretReplaced, TenantActions.MemberSecretCleared],
            await SecretRowsAsync("Writer"));

        // Each row names team, member, plugin and field - never the value.
        Assert.All(
            await ColumnAsync("SELECT detail FROM tenant_events WHERE subject = $key AND action LIKE 'member.secret-%'", $"{_team}/Writer"),
            detail => Assert.Equal(JsonSerializer.Serialize(new { team = _team, member = "Writer", plugin = "owned", field = "token" }), detail));
    }

    [Fact]
    public async Task A_first_set_on_a_member_with_no_value_yet_that_cannot_write_its_row_keeps_no_value_and_no_row()
    {
        var key = Key();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = Value("stored-key") })).Status);
        await HireAsync("Fresh", new(), bindings: new() { ["token"] = key });
        Assert.Null(await OwnValueAsync("Fresh", "token"));
        Assert.Empty(await ColumnAsync("SELECT name FROM plugin_member_secrets WHERE name = $key", "Fresh"));

        await RefuseTenantRowAsync(TenantActions.MemberSecretSet);
        var set = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Fresh/secrets/token", new { value = Value("lost-first") });
        Assert.Equal(HttpStatusCode.InternalServerError, set.Status);
        Assert.Null(await OwnValueAsync("Fresh", "token"));
        Assert.Empty(await ColumnAsync("SELECT name FROM plugin_member_secrets WHERE name = $key", "Fresh"));
        Assert.Empty(await SecretRowsAsync("Fresh"));
        Assert.Equal("key", State(await GetAsync($"/api/teams/{_team}/members/Fresh/secrets"), "token").GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_machine_principal_is_refused_every_route_and_an_agents_hire_cannot_carry_a_value()
    {
        var first = Value("person");
        await HireAsync("Guarded", new() { ["token"] = first });

        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container,
            _team, Permits.All, ct: Ct));

        var routes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"/api/teams/{_team}/members/Guarded/secrets", null),
            (HttpMethod.Put, $"/api/teams/{_team}/members/Guarded/secrets/token", new { value = Value("machine") }),
            (HttpMethod.Delete, $"/api/teams/{_team}/members/Guarded/secrets/token", null),
            (HttpMethod.Get, $"/api/teams/{_team}/members/Guarded/plugin-settings", null),
            (HttpMethod.Put, $"/api/teams/{_team}/members/Guarded/plugin-settings", new { config = new { }, secretValues = new Dictionary<string, string?> { ["token"] = Value("machine-save") } }),
        };

        foreach (var (method, path, body) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await manager.SendAsync(request, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {response.StatusCode}");
            Assert.Equal(PermitGate.HumansOnlyMessage, Json(await response.Content.ReadAsStringAsync(Ct)).GetProperty("error").GetString());
        }

        Assert.Equal(first, await OwnValueAsync("Guarded", "token"));

        // An agent's hire carrying a value is refused and makes nothing.
        var hire = await manager.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Steered",
            agent = "plugin:owned",
            secretValues = new Dictionary<string, string> { ["token"] = Value("steered") },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, hire.StatusCode);
        Assert.Contains("An agent's hire cannot carry a secret value", await hire.Content.ReadAsStringAsync(Ct));
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Steered")));
        Assert.Equal([TenantActions.MemberSecretSet], await SecretRowsAsync("Guarded"));
    }

    [Theory]
    [InlineData("nope", "real-value-1234", "`nope` is not a secret plugin 'owned' names")]
    [InlineData("token", "abc", "`token`: the value is shorter than 4")]
    [InlineData("token", " padded-value", "`token`: the value begins or ends with white space")]
    [InlineData("token", "two\nlines", "`token`: the value holds a line break")]
    [InlineData("account", "two\nlines", "`account`: the value holds a line break")]
    public async Task A_value_that_cannot_be_kept_is_refused_naming_the_field_and_never_repeating_the_value(string field, string value, string words)
    {
        await HireAsync("Checked", new() { ["token"] = Value("ok") });

        var answer = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Checked/secrets/{field}", new { value });
        Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
        var error = Json(answer.Body).GetProperty("error").GetString()!;
        Assert.Contains(words, error);
        Assert.DoesNotContain(value.Trim(), error);

        var hire = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Refused",
            agent = "plugin:owned",
            secretValues = new Dictionary<string, string> { ["token"] = Value("ok"), [field] = value },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, hire.StatusCode);
        Assert.Contains(words, await hire.Content.ReadAsStringAsync(Ct));
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Refused")));
    }

    [Fact]
    public async Task A_removal_and_a_team_deletion_take_the_values_a_same_name_successor_inherits_none_and_a_clone_copies_none()
    {
        var key = Key();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = Value("shared-key") })).Status);
        await HireAsync("Gone", new() { ["token"] = Value("gone") });
        Assert.Single(await ColumnAsync("SELECT field FROM plugin_member_secrets WHERE name = $key", "Gone"));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/containers/Gone", null)).Status);
        Assert.Empty(await ColumnAsync("SELECT field FROM plugin_member_secrets WHERE name = $key", "Gone"));

        // A member hired again under the same name starts with no value of its own.
        await HireAsync("Gone", new(), bindings: new() { ["token"] = key });
        Assert.Equal("key", State(await GetAsync($"/api/teams/{_team}/members/Gone/secrets"), "token").GetProperty("source").GetString());

        // A clone copies the binding and no value.
        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Gone/secrets/token", new { value = Value("not-cloned") });
        var clone = await Services.GetRequiredService<TeamRegistry>().CloneAsync(_team, "Owners Copy", Ct);
        var cloned = clone.Team.Id;
        Assert.Empty(await ColumnAsync("SELECT field FROM plugin_member_secrets WHERE team = $key", cloned));
        Assert.Equal("key", State(await GetAsync($"/api/teams/{cloned}/members/Gone/secrets"), "token").GetProperty("source").GetString());

        // Deleting the team takes every value of its members.
        Assert.NotEmpty(await ColumnAsync("SELECT field FROM plugin_member_secrets WHERE team = $key", _team));
        Assert.True((await _person.DeleteAsync($"/api/teams/{_team}", Ct)).IsSuccessStatusCode);
        Assert.Empty(await ColumnAsync("SELECT field FROM plugin_member_secrets WHERE team = $key", _team));
    }

    [Fact]
    public async Task The_settings_read_answers_the_plugins_description_and_no_marketplace_page_when_no_catalog_lists_the_plugin()
    {
        await HireAsync("Plain", new() { ["token"] = Value("plain") });

        var settings = await GetAsync($"/api/teams/{_team}/members/Plain/plugin-settings");
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("marketplaceUrl").ValueKind);
        Assert.Equal(Description, settings.GetProperty("description").GetString());
        var plugin = (await GetAsync("/api/plugins")).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "owned");
        Assert.Equal(JsonValueKind.Null, plugin.GetProperty("marketplaceUrl").ValueKind);
        Assert.Equal("Account name", plugin.GetProperty("secrets").GetProperty("account").GetProperty("label").GetString());
        Assert.False(plugin.GetProperty("secrets").GetProperty("account").GetProperty("masked").GetBoolean());
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static string Key() => $"MEMBER_SECRETS_TEST_{Guid.NewGuid():N}".ToUpperInvariant();

    private static string Value(string what) => $"{what}-{Guid.NewGuid():N}";

    private void SetEnvironment(string key, string value)
    {
        _environmentKeys.Add(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    private JsonNode LastRequest(string member) =>
        File.ReadAllLines(Requests).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!)
            .Last(r => (string?)r["member"]?["name"] == member);

    private string? LastSecret(string member, string field) => (string?)LastRequest(member)["secrets"]?[field];

    private static JsonElement State(JsonElement read, string field) =>
        read.GetProperty("secrets").EnumerateArray().Single(e => e.GetProperty("field").GetString() == field);

    private async Task<string?> OwnValueAsync(string member, string field) =>
        (await Services.GetRequiredService<PluginMemberSecretStore>().ValuesAsync(new ContainerId(_team, member), Ct)).GetValueOrDefault(field);

    private async Task HireAsync(string name, Dictionary<string, string> values, Dictionary<string, string>? bindings = null)
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name,
            agent = "plugin:owned",
            secrets = bindings ?? new Dictionary<string, string>(),
            secretValues = values,
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

    private Task<IReadOnlyList<string>> MemberRowsAsync(string member) =>
        ColumnAsync("SELECT action FROM tenant_events WHERE subject = $key ORDER BY seq", $"{_team}/{member}");

    private async Task<IReadOnlyList<string>> SecretRowsAsync(string member) =>
        [.. (await MemberRowsAsync(member)).Where(a => a.StartsWith("member.secret-", StringComparison.Ordinal))];

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
