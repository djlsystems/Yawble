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
/// A PLUGIN MEMBER'S NAMED SECRETS, on the real Host with a fixture plugin whose manifest declares a
/// <c>namedSecrets</c> block (at most 3) beside a masked <c>token</c> field, and one that declares none.
/// A person names a secret and types its value; each member's run receives only its own on stdin as
/// <c>namedSecrets</c> and <c>namedSecretNames</c>, and a plugin without the block receives neither.
/// Every write is a person's and lands with its tenant row or not at all; no value appears in any
/// answer, row or file under the data root, and every value is redacted from what the run wrote. A
/// refused name or value is said in one sentence naming the name, never the value. The rows go with the
/// member and its team, and a clone copies none.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PluginNamedSecretsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-named-secrets-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-named-secrets-out-{Guid.NewGuid():N}");
    private readonly List<string> _responses = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    /// <summary>Each request a plugin was handed on stdin, one per line, kept OUTSIDE the data root so
    /// the no-leak scan does not find the plugin's own copy.</summary>
    private string Requests => Path.Combine(_outside, "requests.jsonl");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        // Each plugin ECHOES its request: every value it prints must be redacted from what the Host keeps.
        var echo = $"req=$(cat); printf '%s\\n' \"$req\" >> '{Requests}'; printf '%s\\n' \"$req\"; echo '{{\"t\":\"result\",\"ok\":true,\"output\":\"read\"}}'";
        PluginInstall.Write(_dataRoot, "named", script: echo, manifest: PluginInstall.Manifest("named", edit: m =>
        {
            m["secrets"] = JsonNode.Parse("""{ "token": { "description": "The service's token." } }""");
            m["namedSecrets"] = JsonNode.Parse("""{ "label": "App secrets", "description": "The name is how the team refers to it.", "max": 3 }""");
        }));
        PluginInstall.Write(_dataRoot, "plain", script: echo, manifest: PluginInstall.Manifest("plain", edit: m =>
            m["secrets"] = JsonNode.Parse("""{ "token": { "description": "The service's token." } }""")));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Namers", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

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
    public async Task Two_members_of_one_plugin_each_hold_ApiKey_and_each_run_receives_only_its_own()
    {
        var first = Value("first");
        var second = Value("second");

        await HireAsync("One", new() { ["ApiKey"] = first });
        await HireAsync("Two", new() { ["ApiKey"] = second, ["Webhook"] = Value("hook") });

        await TellAndAwaitAsync("One");
        await TellAndAwaitAsync("Two");
        Assert.Equal(first, LastNamed("One", "ApiKey"));
        Assert.Equal(second, LastNamed("Two", "ApiKey"));
        Assert.Equal(["ApiKey"], Names(LastRequest("One")));
        Assert.Equal(["ApiKey", "Webhook"], Names(LastRequest("Two")));

        // From the screen: the second member's value is replaced; the first member's run is unchanged.
        var replaced = Value("second-replaced");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Two/named-secrets/ApiKey", new { value = replaced })).Status);

        await TellAndAwaitAsync("One");
        await TellAndAwaitAsync("Two");
        Assert.Equal(first, LastNamed("One", "ApiKey"));
        Assert.Equal(replaced, LastNamed("Two", "ApiKey"));
        Assert.Equal(["ApiKey"], ((JsonObject)LastRequest("One")["namedSecrets"]!).Select(p => p.Key));
    }

    [Fact]
    public async Task A_plugin_that_declares_no_block_receives_neither_and_its_routes_say_so()
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Plain", agent = "plugin:plain" }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);
        await TellAndAwaitAsync("Plain");

        var request = LastRequest("Plain").AsObject();
        Assert.False(request.ContainsKey("namedSecrets"));
        Assert.False(request.ContainsKey("namedSecretNames"));

        // A member of the block's plugin holding none receives both, empty.
        await HireAsync("Empty", new());
        await TellAndAwaitAsync("Empty");
        Assert.Empty(LastRequest("Empty")["namedSecrets"]!.AsObject());
        Assert.Empty(Names(LastRequest("Empty")));

        var read = await SendAsync(HttpMethod.Get, $"/api/teams/{_team}/members/Plain/named-secrets", null);
        Assert.Equal(HttpStatusCode.BadRequest, read.Status);
        Assert.Contains("declares no named secrets", Json(read.Body).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Plain/named-secrets/ApiKey", new { value = Value("x") })).Status);

        var save = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Plain/plugin-settings", new
        {
            config = new { },
            namedSecretValues = new Dictionary<string, string?> { ["ApiKey"] = Value("x") },
        });
        Assert.Equal(HttpStatusCode.BadRequest, save.Status);
        Assert.Empty(await ColumnAsync("SELECT secret FROM plugin_member_named_secrets", ""));

        var settings = await GetAsync($"/api/teams/{_team}/members/Plain/plugin-settings");
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("namedSecretsField").ValueKind);
        Assert.Empty(settings.GetProperty("namedSecretStates").EnumerateArray());
        var plain = (await GetAsync("/api/plugins")).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "plain");
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("namedSecrets").ValueKind);
    }

    [Fact]
    public async Task The_reads_answer_the_block_and_each_name_with_who_set_it_and_never_a_value()
    {
        var value = Value("read");
        await HireAsync("Reader", new() { ["Webhook"] = Value("hook"), ["ApiKey"] = value });

        var read = await GetAsync($"/api/teams/{_team}/members/Reader/named-secrets");
        Assert.Equal(("App secrets", "The name is how the team refers to it.", 3),
            (read.GetProperty("label").GetString(), read.GetProperty("description").GetString(), read.GetProperty("max").GetInt32()));
        Assert.Equal((_team, "Reader"), (read.GetProperty("team").GetString(), read.GetProperty("member").GetString()));
        var names = read.GetProperty("namedSecrets").EnumerateArray().ToList();
        Assert.Equal(["ApiKey", "Webhook"], names.Select(n => n.GetProperty("name").GetString()));
        Assert.All(names, n =>
        {
            Assert.Equal(Email, n.GetProperty("setBy").GetString());
            Assert.True(n.TryGetProperty("setAt", out _));
            Assert.Equal(3, n.EnumerateObject().Count());
        });

        var settings = await GetAsync($"/api/teams/{_team}/members/Reader/plugin-settings");
        Assert.Equal(3, settings.GetProperty("namedSecretsField").GetProperty("max").GetInt32());
        Assert.Equal("App secrets", settings.GetProperty("namedSecretsField").GetProperty("label").GetString());
        Assert.Equal(["ApiKey", "Webhook"], settings.GetProperty("namedSecretStates").EnumerateArray().Select(n => n.GetProperty("name").GetString()));
        var plugin = (await GetAsync("/api/plugins")).GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "named");
        Assert.Equal("App secrets", plugin.GetProperty("namedSecrets").GetProperty("label").GetString());

        // PUT and DELETE answer as GET does.
        var removed = await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/members/Reader/named-secrets/webhook", null);
        Assert.Equal(HttpStatusCode.OK, removed.Status);
        Assert.Equal(["ApiKey"], Json(removed.Body).GetProperty("namedSecrets").EnumerateArray().Select(n => n.GetProperty("name").GetString()));

        Assert.All(_responses, body => Assert.DoesNotContain(value, body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_value_appears_in_any_response_tenant_row_or_file_under_the_data_root_and_every_value_is_redacted_from_the_runs_output()
    {
        var values = new[] { Value("leak-hire"), Value("leak-route"), Value("leak-save") };

        await HireAsync("Leaky", new() { ["ApiKey"] = values[0] });
        var run = await TellAndAwaitAsync("Leaky");
        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Leaky/named-secrets/Webhook", new { value = values[1] });
        await TellAndAwaitAsync("Leaky");
        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Leaky/plugin-settings", new
        {
            config = new { },
            namedSecretValues = new Dictionary<string, string?> { ["ApiKey"] = values[2] },
        });
        var last = await TellAndAwaitAsync("Leaky");
        await GetAsync($"/api/teams/{_team}/members/Leaky/named-secrets");
        await GetAsync($"/api/teams/{_team}/members/Leaky/plugin-settings");
        await GetAsync("/api/plugins");
        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Leaky/named-secrets/ApiKey", new { value = "" });

        // The plugin was handed every value (so the scans below are not vacuous) and echoed it, names and all.
        var requests = await File.ReadAllTextAsync(Requests, Ct);
        Assert.All(values, value => Assert.Contains(value, requests, StringComparison.Ordinal));

        // What the run wrote is kept with each value redacted, and the names it echoed are kept as names.
        Assert.DoesNotContain(values[0], run.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(values[2], last.Payload, StringComparison.Ordinal);
        Assert.Contains("namedSecretNames", last.Payload, StringComparison.Ordinal);
        Assert.Contains("Webhook", last.Payload, StringComparison.Ordinal);

        foreach (var value in values)
        {
            Assert.All(_responses, body => Assert.DoesNotContain(value, body, StringComparison.Ordinal));
        }

        var details = await ColumnAsync("SELECT COALESCE(detail, '') FROM tenant_events WHERE action LIKE 'member.named-secret-%'", "");
        Assert.Equal(3, details.Count);
        Assert.All(details, detail => Assert.All(values, value => Assert.DoesNotContain(value, detail, StringComparison.Ordinal)));

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
                    $"A member's named secret is in {Path.GetRelativePath(_dataRoot, file)}.");
            }
        }

        Assert.True(scanned > 0);
    }

    [Fact]
    public async Task Set_replace_and_remove_each_write_their_row_in_the_same_transaction_and_change_nothing_when_it_cannot_be_written()
    {
        var first = Value("kept");
        await HireAsync("Writer", new() { ["ApiKey"] = first });
        Assert.Equal([TenantActions.MemberNamedSecretSet], await NamedRowsAsync("Writer"));

        await RefuseTenantRowAsync(TenantActions.MemberNamedSecretReplaced);
        var replaced = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Writer/named-secrets/ApiKey", new { value = Value("lost") });
        Assert.Equal(HttpStatusCode.InternalServerError, replaced.Status);
        Assert.Contains("`ApiKey` was not changed", Json(replaced.Body).GetProperty("error").GetString());
        Assert.Equal(first, await NamedValueAsync("Writer", "ApiKey"));

        // The settings Save carries named secrets in the same transaction as the settings: nothing lands.
        var save = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Writer/plugin-settings", new
        {
            config = new { },
            secretValues = new Dictionary<string, string?> { ["token"] = Value("field-save") },
            namedSecretValues = new Dictionary<string, string?> { ["ApiKey"] = Value("lost-save") },
        });
        Assert.Equal(HttpStatusCode.InternalServerError, save.Status);
        Assert.Equal(first, await NamedValueAsync("Writer", "ApiKey"));
        Assert.Empty(await Services.GetRequiredService<PluginMemberSecretStore>().ValuesAsync(new ContainerId(_team, "Writer"), Ct));
        Assert.DoesNotContain(TenantActions.MemberPluginSettingsChanged, await MemberRowsAsync("Writer"));

        await RefuseTenantRowAsync(TenantActions.MemberNamedSecretRemoved);
        var removed = await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/members/Writer/named-secrets/ApiKey", null);
        Assert.Equal(HttpStatusCode.InternalServerError, removed.Status);
        Assert.Equal(first, await NamedValueAsync("Writer", "ApiKey"));

        await RefuseTenantRowAsync(TenantActions.MemberNamedSecretSet);
        var added = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Writer/named-secrets/Webhook", new { value = Value("lost-add") });
        Assert.Equal(HttpStatusCode.InternalServerError, added.Status);
        Assert.Null(await NamedValueAsync("Writer", "Webhook"));

        // A hire whose named secrets cannot be kept makes no member.
        var hire = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Lost",
            agent = "plugin:named",
            namedSecretValues = new Dictionary<string, string> { ["ApiKey"] = Value("lost-hire") },
        }, Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, hire.StatusCode);
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Lost")));
        Assert.Empty(await ColumnAsync("SELECT name FROM team_members WHERE name = $key", "Lost"));
        Assert.Empty(await ColumnAsync("SELECT name FROM plugin_member_named_secrets WHERE name = $key", "Lost"));

        await AllowTenantRowsAsync();
        var second = Value("replaced");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Writer/named-secrets/ApiKey", new { value = second })).Status);
        Assert.Equal(second, await NamedValueAsync("Writer", "ApiKey"));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/members/Writer/named-secrets/ApiKey", null)).Status);

        // Removing a name not held writes no row.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/members/Writer/named-secrets/ApiKey", null)).Status);
        Assert.Equal(
            [TenantActions.MemberNamedSecretSet, TenantActions.MemberNamedSecretReplaced, TenantActions.MemberNamedSecretRemoved],
            await NamedRowsAsync("Writer"));

        // Each row names team, member, plugin and name - never the value.
        Assert.All(
            await ColumnAsync("SELECT detail FROM tenant_events WHERE subject = $key AND action LIKE 'member.named-secret-%'", $"{_team}/Writer"),
            detail => Assert.Equal(JsonSerializer.Serialize(new { team = _team, member = "Writer", plugin = "named", name = "ApiKey" }), detail));
    }

    [Fact]
    public async Task A_machine_principal_is_refused_every_route_and_an_agents_hire_carrying_named_values_is_refused()
    {
        var first = Value("person");
        await HireAsync("Guarded", new() { ["ApiKey"] = first });

        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container,
            _team, Permits.All, ct: Ct));

        var routes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"/api/teams/{_team}/members/Guarded/named-secrets", null),
            (HttpMethod.Put, $"/api/teams/{_team}/members/Guarded/named-secrets/ApiKey", new { value = Value("machine") }),
            (HttpMethod.Delete, $"/api/teams/{_team}/members/Guarded/named-secrets/ApiKey", null),
            (HttpMethod.Put, $"/api/teams/{_team}/members/Guarded/plugin-settings", new { config = new { }, namedSecretValues = new Dictionary<string, string?> { ["ApiKey"] = Value("machine-save") } }),
        };

        foreach (var (method, path, body) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await manager.SendAsync(request, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {response.StatusCode}");
            Assert.Equal(PermitGate.HumansOnlyMessage, Json(await response.Content.ReadAsStringAsync(Ct)).GetProperty("error").GetString());
        }

        Assert.Equal(first, await NamedValueAsync("Guarded", "ApiKey"));

        // An agent's hire carrying a named value is refused and makes nothing.
        var hire = await manager.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Steered",
            agent = "plugin:named",
            namedSecretValues = new Dictionary<string, string> { ["ApiKey"] = Value("steered") },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, hire.StatusCode);
        Assert.Contains("An agent's hire cannot carry a secret value", await hire.Content.ReadAsStringAsync(Ct));
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Steered")));
        Assert.Equal([TenantActions.MemberNamedSecretSet], await NamedRowsAsync("Guarded"));

        // And an Agent hired with it is refused too: named secrets belong to a plugin member.
        var agent = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Agent",
            agent = "claude-headless",
            namedSecretValues = new Dictionary<string, string> { ["ApiKey"] = Value("agent") },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, agent.StatusCode);
        Assert.Contains("`namedSecretValues` belong to a plugin member", await agent.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The member holds <c>ApiKey</c> and <c>Second</c> of at most 3.</summary>
    [Theory]
    [InlineData("1Key", "real-value-1234", "`1Key` is not a usable secret name")]
    [InlineData("api_key-x", "real-value-1234", "`api_key-x` is not a usable secret name")]
    [InlineData("apikey", "real-value-1234", "`apikey`: this member already holds a secret named `ApiKey`")]
    [InlineData("TOKEN", "real-value-1234", "`TOKEN` is the name of the plugin's secret field `token`")]
    [InlineData("Third", "abc", "`Third`: the value is shorter than 4")]
    [InlineData("Third", " padded-value", "`Third`: the value begins or ends with white space")]
    [InlineData("Third", "two\nlines", "`Third`: the value holds a line break")]
    [InlineData("Third", "Second", "`Third`: the value is the name of a secret")]
    public async Task A_name_or_value_that_cannot_be_kept_is_refused_naming_the_name_and_never_the_value(string name, string value, string words)
    {
        await HireAsync("Checked", new() { ["ApiKey"] = Value("ok"), ["Second"] = Value("ok") });

        var put = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Checked/named-secrets/{Uri.EscapeDataString(name)}", new { value });
        var save = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Checked/plugin-settings", new
        {
            config = new { },
            namedSecretValues = new Dictionary<string, string?> { [name] = value },
        });

        foreach (var (status, body) in new[] { put, save })
        {
            Assert.Equal(HttpStatusCode.BadRequest, status);
            var error = Json(body).GetProperty("error").GetString()!;
            Assert.Contains(words, error);
            if (value != "Second") Assert.DoesNotContain(value.Trim(), error);
            Assert.DoesNotContain("\n", error);
        }

        Assert.Equal(["ApiKey", "Second"], await NamesHeldAsync("Checked"));

        // A person's hire carrying it is refused as well, and makes nothing.
        var hire = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Refused",
            agent = "plugin:named",
            namedSecretValues = new Dictionary<string, string> { ["ApiKey"] = Value("ok"), ["Second"] = Value("ok"), [name] = value },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, hire.StatusCode);
        Assert.Null(Services.GetRequiredService<Harness.Containers.ContainerHost>().Find(new ContainerId(_team, "Refused")));
    }

    [Fact]
    public async Task One_more_than_the_blocks_max_is_refused_naming_the_name_and_the_limit()
    {
        await HireAsync("Full", new() { ["ApiKey"] = Value("ok"), ["Second"] = Value("ok") });

        var save = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Full/plugin-settings", new
        {
            config = new { },
            namedSecretValues = new Dictionary<string, string?> { ["Fourth"] = Value("fourth"), ["Third"] = Value("third") },
        });
        Assert.Equal(HttpStatusCode.BadRequest, save.Status);
        Assert.Equal(
            "`Third` would be this member's secret number 4; plugin 'named' holds at most 3 named secrets per member. Remove one first.",
            Json(save.Body).GetProperty("error").GetString());
        Assert.Equal(["ApiKey", "Second"], await NamesHeldAsync("Full"));

        // Removing one in the same save makes room; a third PUT fills it; a fourth is refused.
        var swap = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Full/plugin-settings", new
        {
            config = new { },
            namedSecretValues = new Dictionary<string, string?> { ["Second"] = null, ["Third"] = Value("third"), ["Fourth"] = Value("fourth") },
        });
        Assert.Equal(HttpStatusCode.OK, swap.Status);
        Assert.Equal(["ApiKey", "Fourth", "Third"], await NamesHeldAsync("Full"));

        var over = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Full/named-secrets/Fifth", new { value = Value("fifth") });
        Assert.Equal(HttpStatusCode.BadRequest, over.Status);
        Assert.Contains("`Fifth` would be this member's secret number 4", Json(over.Body).GetProperty("error").GetString());

        // Replacing one it holds is not one more.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Full/named-secrets/Third", new { value = Value("again") })).Status);
    }

    [Fact]
    public async Task A_value_equal_to_a_key_name_the_run_is_told_is_refused_before_the_run()
    {
        var key = $"NAMED_SECRETS_TEST_{Guid.NewGuid():N}".ToUpperInvariant();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/secrets/{key}", new { value = Value("stored-key") })).Status);
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Told",
            agent = "plugin:named",
            secrets = new Dictionary<string, string> { ["token"] = key },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, hired.StatusCode);

        // A key's name is not checked when the value is typed; it is before the run, which never starts.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Told/named-secrets/Other", new { value = key })).Status);
        var before = File.Exists(Requests) ? File.ReadAllLines(Requests).Length : 0;

        var row = await TellAndAwaitAsync("Told", expectCompleted: false);
        Assert.Equal(MessageTypes.Failed, row.Type);
        var failed = Json(row.Payload);
        Assert.StartsWith("The value of this member's secret `Other` is the name of a secret", failed.GetProperty("launchError").GetString());
        Assert.DoesNotContain(key, failed.GetProperty("launchError").GetString());
        Assert.Equal(FailureClasses.NeedsPerson, failed.GetProperty("failureClass").GetString());
        Assert.Equal(before, File.Exists(Requests) ? File.ReadAllLines(Requests).Length : 0);
    }

    [Fact]
    public async Task A_removal_and_a_team_deletion_take_the_named_secrets_a_same_name_successor_inherits_none_and_a_clone_copies_none()
    {
        await HireAsync("Gone", new() { ["ApiKey"] = Value("gone") });
        Assert.Single(await ColumnAsync("SELECT secret FROM plugin_member_named_secrets WHERE name = $key", "Gone"));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/teams/{_team}/containers/Gone", null)).Status);
        Assert.Empty(await ColumnAsync("SELECT secret FROM plugin_member_named_secrets WHERE name = $key", "Gone"));

        // A member hired again under the same name holds none.
        await HireAsync("Gone", new());
        Assert.Empty(await NamesHeldAsync("Gone"));

        // A clone copies none.
        await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Gone/named-secrets/ApiKey", new { value = Value("not-cloned") });
        var clone = await Services.GetRequiredService<TeamRegistry>().CloneAsync(_team, "Namers Copy", Ct);
        var cloned = clone.Team.Id;
        Assert.Empty(await ColumnAsync("SELECT secret FROM plugin_member_named_secrets WHERE team = $key", cloned));
        Assert.Empty((await GetAsync($"/api/teams/{cloned}/members/Gone/named-secrets")).GetProperty("namedSecrets").EnumerateArray());

        // Deleting the team takes every named secret of its members.
        Assert.NotEmpty(await ColumnAsync("SELECT secret FROM plugin_member_named_secrets WHERE team = $key", _team));
        Assert.True((await _person.DeleteAsync($"/api/teams/{_team}", Ct)).IsSuccessStatusCode);
        Assert.Empty(await ColumnAsync("SELECT secret FROM plugin_member_named_secrets WHERE team = $key", _team));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static string Value(string what) => $"{what}-{Guid.NewGuid():N}";

    private JsonNode LastRequest(string member) =>
        File.ReadAllLines(Requests).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!)
            .Last(r => (string?)r["member"]?["name"] == member);

    private string? LastNamed(string member, string name) => (string?)LastRequest(member)["namedSecrets"]?[name];

    private static List<string?> Names(JsonNode request) => [.. request["namedSecretNames"]!.AsArray().Select(n => (string?)n)];

    private async Task<string?> NamedValueAsync(string member, string name) =>
        (await Services.GetRequiredService<PluginMemberSecretStore>().NamedValuesAsync(new ContainerId(_team, member), Ct)).GetValueOrDefault(name);

    private async Task<IReadOnlyList<string>> NamesHeldAsync(string member) =>
        [.. (await Services.GetRequiredService<PluginMemberSecretStore>().NamedStatesAsync(new ContainerId(_team, member), Ct)).Select(s => s.Name)];

    private async Task HireAsync(string name, Dictionary<string, string> named)
    {
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name,
            agent = "plugin:named",
            namedSecretValues = named,
        }, Ct);
        var body = await hired.Content.ReadAsStringAsync(Ct);
        _responses.Add(body);
        Assert.True(hired.StatusCode == HttpStatusCode.OK, body);
    }

    private async Task<Message> TellAndAwaitAsync(string member, bool expectCompleted = true)
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
                if (expectCompleted) Assert.True(row.Type == MessageTypes.Completed, row.Payload);
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

    private async Task<IReadOnlyList<string>> NamedRowsAsync(string member) =>
        [.. (await MemberRowsAsync(member)).Where(a => a.StartsWith("member.named-secret-", StringComparison.Ordinal))];

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
