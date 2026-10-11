using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// ISSUED AGENT CREDENTIALS on the real Host: set, replaced and cleared by a person through
/// <c>/api/agents/{name}/credential</c> or by the operator CLI through its request file, one value per
/// command, each change with its tenant row in the same transaction, none of it when that row cannot
/// be written, none of it by a machine principal, and the value in no answer, row, diagnostic, report
/// or file under the data root.
/// </summary>
public sealed class AgentCredentialsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    // Fake, and long enough that a 12-character fragment of each is distinctive.
    private const string First = "fake-issued-first-Qm7vX2pL9tRw4kZs";
    private const string Second = "fake-issued-second-Hn3cY8bJ1uEo6aGd";
    private const string ByOperator = "fake-issued-operator-Tz5fK0wV2iNq7xMr";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-agent-credentials-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-agent-credentials-out-{Guid.NewGuid():N}");
    private readonly List<string> _responses = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning"));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Creds", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        MemberTempCleanup.Remove(_outside);
        foreach (var directory in new[] { _dataRoot, _outside })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---- set, replace, clear, and the one value per command ------------------------------------------

    [Fact]
    public async Task Setting_replacing_and_clearing_each_land_with_their_tenant_row_naming_the_person()
    {
        var set = await SendAsync(HttpMethod.Put, "/api/agents/claude-headless/credential", new { kind = "apiKey", value = First });
        Assert.Equal(HttpStatusCode.OK, set.Status);
        var body = JsonDocument.Parse(set.Body).RootElement;
        Assert.Equal(["command", "set", "setBy", "setAt"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("claude", body.GetProperty("command").GetString());
        Assert.True(body.GetProperty("set").GetBoolean());
        Assert.Equal(Email, body.GetProperty("setBy").GetString());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/api/agents/claude/credential", new { kind = "token", value = Second })).Status);

        var cleared = await SendAsync(HttpMethod.Delete, "/api/agents/claude/credential", null);
        Assert.Equal(HttpStatusCode.OK, cleared.Status);
        Assert.Equal("""{"command":"claude","set":false,"setBy":null,"setAt":null}""", cleared.Body);

        // Clearing again changes nothing and writes no row.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, "/api/agents/claude/credential", null)).Status);

        var rows = (await TenantRowsAsync()).Where(r => r.Action.StartsWith("agents.credential-", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [TenantActions.AgentCredentialSet, TenantActions.AgentCredentialReplaced, TenantActions.AgentCredentialCleared],
            rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal(Email, r.ActorEmail));
        Assert.All(rows, r => Assert.Equal("claude", r.Subject));
        Assert.Contains("\"variable\":\"ANTHROPIC_API_KEY\"", rows[0].Detail);
        Assert.Contains("\"variable\":\"CLAUDE_CODE_OAUTH_TOKEN\"", rows[1].Detail);

        // The clear row names what was cleared as the set row names what was set.
        Assert.Equal("""{"command":"claude","kind":"token","variable":"CLAUDE_CODE_OAUTH_TOKEN"}""", rows[2].Detail);
    }

    [Fact]
    public async Task A_name_nothing_answers_to_is_refused_in_a_fixed_sentence_that_never_quotes_it()
    {
        // What a person or a script may type where the name goes: a value.
        const string Typed = "fake-typed-as-a-name-Wq4rT8yU2p";

        foreach (var (method, body) in new (HttpMethod, object?)[]
                 {
                     (HttpMethod.Put, new { kind = "apiKey", value = First }), (HttpMethod.Delete, null),
                 })
        {
            var refused = await SendAsync(method, $"/api/agents/{Typed}/credential", body);
            Assert.Equal(HttpStatusCode.NotFound, refused.Status);
            Assert.Equal(AgentCredentials.NotFound, JsonDocument.Parse(refused.Body).RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain("Wq4rT8yU", refused.Body);
        }

        foreach (var request in new object[]
                 {
                     new { action = "set", agent = Typed, kind = "apiKey", value = First },
                     new { action = "clear", agent = Typed },
                     new { action = "status", agent = Typed },
                     new { action = "source", agent = Typed, source = "issued" },
                     new { action = Typed, agent = "claude" },
                 })
        {
            var answer = await ExchangeAsync(request);
            Assert.True(answer.GetProperty("status").GetInt32() is 404 or 400);
            Assert.DoesNotContain("Wq4rT8yU", answer.GetRawText());
        }

        Assert.Equal(404, (await ExchangeAsync(new { action = "status", agent = Typed })).GetProperty("status").GetInt32());
        Assert.Equal(AgentCredentials.NotFound,
            (await ExchangeAsync(new { action = "clear", agent = Typed })).GetProperty("body").GetProperty("error").GetString());
    }

    [Fact]
    public async Task Setting_a_credential_through_one_preset_issues_it_to_every_preset_that_runs_the_same_command()
    {
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/api/agents/claude/credential", new { kind = "apiKey", value = First })).Status);
        await SourcesAsync(new() { ["claude-headless"] = "issued", ["claude"] = "issued" });

        var list = (await GetAsync("/api/agents/credentials")).EnumerateArray().ToList();
        var headless = list.Single(e => e.GetProperty("agent").GetString() == "claude-headless");
        var concierge = list.Single(e => e.GetProperty("agent").GetString() == "claude");

        foreach (var entry in new[] { headless, concierge })
        {
            Assert.Equal("claude", entry.GetProperty("command").GetString());
            Assert.Equal("issued", entry.GetProperty("source").GetString());
            Assert.True(entry.GetProperty("set").GetBoolean());
            Assert.Equal(Email, entry.GetProperty("setBy").GetString());
        }

        Assert.Equal(["claude"], headless.GetProperty("sharedWith").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["claude-headless"], concierge.GetProperty("sharedWith").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("apiKey", headless.GetProperty("issuedCredential").GetProperty("kinds")[0].GetProperty("kind").GetString());

        // The other command is untouched, and still on the home.
        var grok = list.Single(e => e.GetProperty("agent").GetString() == "grok-headless");
        Assert.False(grok.GetProperty("set").GetBoolean());
        Assert.Equal("home", grok.GetProperty("source").GetString());

        // Both presets' runs resolve the one value.
        var credentials = Services.GetRequiredService<IRunCredentials>();
        Assert.Equal(First, (await credentials.ResolveAsync("claude-headless", null, Ct)).Environment["ANTHROPIC_API_KEY"]);
        Assert.Equal(First, (await credentials.ResolveAsync("claude", null, Ct)).Environment["ANTHROPIC_API_KEY"]);

        // Cleared through the other preset, it is gone for both.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, "/api/agents/claude-headless/credential", null)).Status);
        Assert.NotNull((await credentials.ResolveAsync("claude", null, Ct)).Missing);
        Assert.All((await GetAsync("/api/agents/credentials")).EnumerateArray()
            .Where(e => e.GetProperty("command").GetString() == "claude"), e => Assert.False(e.GetProperty("set").GetBoolean()));
    }

    [Fact]
    public async Task None_of_set_replace_or_clear_happens_when_its_tenant_row_cannot_be_written()
    {
        await ExecuteAsync("ALTER TABLE tenant_events RENAME TO tenant_events_away");
        var unrecordedSet = await SendAsync(HttpMethod.Put, "/api/agents/claude/credential", new { kind = "apiKey", value = First });
        await ExecuteAsync("ALTER TABLE tenant_events_away RENAME TO tenant_events");

        Assert.Equal(HttpStatusCode.InternalServerError, unrecordedSet.Status);
        Assert.Contains("could not be written", unrecordedSet.Body);
        Assert.False(await StoredAsync("claude"));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/api/agents/claude/credential", new { kind = "apiKey", value = First })).Status);

        await ExecuteAsync("ALTER TABLE tenant_events RENAME TO tenant_events_away");
        var unrecordedReplace = await SendAsync(HttpMethod.Put, "/api/agents/claude/credential", new { kind = "token", value = Second });
        var unrecordedClear = await SendAsync(HttpMethod.Delete, "/api/agents/claude/credential", null);
        await ExecuteAsync("ALTER TABLE tenant_events_away RENAME TO tenant_events");

        Assert.Equal(HttpStatusCode.InternalServerError, unrecordedReplace.Status);
        Assert.Equal(HttpStatusCode.InternalServerError, unrecordedClear.Status);

        // Still the first value, of the first kind.
        await SourcesAsync(new() { ["claude-headless"] = "issued" });
        var credential = await Services.GetRequiredService<IRunCredentials>().ResolveAsync("claude-headless", null, Ct);
        Assert.Equal(First, credential.Environment["ANTHROPIC_API_KEY"]);
        Assert.Single(await TenantRowsAsync(), r => r.Action.StartsWith("agents.credential-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Over_the_request_file_nothing_is_set_or_cleared_when_its_tenant_row_cannot_be_written()
    {
        await ExecuteAsync("ALTER TABLE tenant_events RENAME TO tenant_events_away");
        var unrecordedSet = await ExchangeAsync(new { action = "set", agent = "claude", kind = "apiKey", value = ByOperator });
        await ExecuteAsync("ALTER TABLE tenant_events_away RENAME TO tenant_events");

        Assert.Equal(500, unrecordedSet.GetProperty("status").GetInt32());
        Assert.Contains("could not be written", unrecordedSet.GetProperty("body").GetProperty("error").GetString());
        Assert.False(await StoredAsync("claude"));
        Assert.DoesNotContain(await TenantRowsAsync(), r => r.Action.StartsWith("agents.credential-", StringComparison.Ordinal));

        Assert.Equal(200, (await ExchangeAsync(new { action = "set", agent = "claude", kind = "apiKey", value = ByOperator }))
            .GetProperty("status").GetInt32());

        await ExecuteAsync("ALTER TABLE tenant_events RENAME TO tenant_events_away");
        var unrecordedClear = await ExchangeAsync(new { action = "clear", agent = "claude" });
        await ExecuteAsync("ALTER TABLE tenant_events_away RENAME TO tenant_events");

        Assert.Equal(500, unrecordedClear.GetProperty("status").GetInt32());
        Assert.True(await StoredAsync("claude"));
        Assert.Single(await TenantRowsAsync(), r => r.Action.StartsWith("agents.credential-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_machine_principal_cannot_set_or_clear_a_credential()
    {
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(_team, "Worker").ToString(), PrincipalKind.Container, _team, Permits.All, ct: Ct);
        using var member = _factory.CreateClient();
        member.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);

        var put = await member.PutAsJsonAsync("/api/agents/claude/credential", new { kind = "apiKey", value = First }, Ct);
        var delete = await member.DeleteAsync("/api/agents/claude/credential", Ct);
        var list = await member.GetAsync("/api/agents/credentials", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Contains(PermitGate.HumansOnlyMessage, await put.Content.ReadAsStringAsync(Ct));
        Assert.False(await StoredAsync("claude"));
    }

    [Fact]
    public async Task A_preset_without_a_declaration_refuses_a_credential_and_the_issued_source()
    {
        var catalog = Services.GetRequiredService<AgentCatalog>();
        catalog.Replace([.. catalog.Definitions, new AgentDefinition("bare", AgentMode.Headless, new AgentLaunch("some-cli", []))]);

        var credential = await SendAsync(HttpMethod.Put, "/api/agents/bare/credential", new { kind = "apiKey", value = First });
        Assert.Equal(HttpStatusCode.BadRequest, credential.Status);
        Assert.Contains("takes no issued credential", credential.Body);
        Assert.DoesNotContain(First, credential.Body);

        var source = await SendAsync(HttpMethod.Put, "/api/tenant/settings",
            new Dictionary<string, object> { [TenantSettings.AgentCredentialSourceName] = new Dictionary<string, string> { ["bare"] = "issued" } });
        Assert.Equal(HttpStatusCode.BadRequest, source.Status);
        Assert.Contains("declares no issued credential", source.Body);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, "/api/agents/nothing-here/credential", new { kind = "apiKey", value = First })).Status);

        // A kind the CLI does not take, an empty value, a two-line value, a form the CLI refuses.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/api/agents/grok/credential", new { kind = "token", value = First })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/api/agents/grok/credential", new { kind = "apiKey", value = " " })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/api/agents/grok/credential", new { kind = "apiKey", value = First + "\nmore" })).Status);
        var classic = await SendAsync(HttpMethod.Put, "/api/agents/copilot/credential", new { value = "ghp_" + First });
        Assert.Equal(HttpStatusCode.BadRequest, classic.Status);
        Assert.DoesNotContain(First, classic.Body);
        Assert.DoesNotContain(await TenantRowsAsync(), r => r.Action.StartsWith("agents.credential-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_auth_report_carries_each_presets_source_and_an_issued_one_reads_set_or_not()
    {
        await SourcesAsync(new() { ["grok-headless"] = "issued" });

        var reports = (await GetAsync("/api/agents/auth")).EnumerateArray().ToList();
        var grok = reports.Single(r => r.GetProperty("agent").GetString() == "grok-headless");
        Assert.Equal("issued", grok.GetProperty("source").GetString());
        Assert.False(grok.GetProperty("authenticated").GetBoolean());
        Assert.Contains("not set", grok.GetProperty("detail").GetString());
        Assert.All(reports.Where(r => r.GetProperty("agent").GetString() != "grok-headless"),
            r => Assert.Equal("home", r.GetProperty("source").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/api/agents/grok/credential", new { value = First })).Status);

        grok = (await GetAsync("/api/agents/auth")).EnumerateArray().Single(r => r.GetProperty("agent").GetString() == "grok-headless");
        Assert.True(grok.GetProperty("authenticated").GetBoolean());
        Assert.Contains("XAI_API_KEY", grok.GetProperty("detail").GetString());
    }

    // ---- the operator CLI ------------------------------------------------------------------------------

    [Fact]
    public async Task The_operator_clis_request_is_recorded_as_the_operator()
    {
        var set = await ExchangeAsync(new { action = "set", agent = "codex-headless", kind = "apiKey", value = ByOperator });
        Assert.Equal(200, set.GetProperty("status").GetInt32());
        Assert.Equal("codex", set.GetProperty("body").GetProperty("command").GetString());
        Assert.Equal("operator", set.GetProperty("body").GetProperty("setBy").GetString());

        var source = await ExchangeAsync(new { action = "source", agent = "codex-headless", source = "issued" });
        Assert.Equal(200, source.GetProperty("status").GetInt32());
        Assert.Equal("issued", source.GetProperty("body").GetProperty("source").GetString());
        Assert.True(source.GetProperty("body").GetProperty("set").GetBoolean());

        var status = await ExchangeAsync(new { action = "status", agent = "codex" });
        Assert.True(status.GetProperty("body").GetProperty("set").GetBoolean());

        var clear = await ExchangeAsync(new { action = "clear", agent = "codex" });
        Assert.False(clear.GetProperty("body").GetProperty("set").GetBoolean());

        var refused = await ExchangeAsync(new { action = "source", agent = "nope", source = "issued" });
        Assert.Equal(404, refused.GetProperty("status").GetInt32());

        var rows = await TenantRowsAsync();
        Assert.Equal("operator", rows.Single(r => r.Action == TenantActions.AgentCredentialSet).ActorEmail);
        Assert.Equal("operator", rows.Single(r => r.Action == TenantActions.AgentCredentialCleared).ActorEmail);
        Assert.Contains(rows, r => r.Action == TenantActions.TenantSettingChanged && r.ActorEmail == "operator");

        Assert.False(File.Exists(Path.Combine(_dataRoot, AgentCredentialRequests.Folder, AgentCredentialRequests.RequestFile)));
    }

    // ---- no leaks ---------------------------------------------------------------------------------------

    [Fact]
    public async Task No_issued_credential_appears_in_a_response_row_diagnostic_or_file_under_the_data_root()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        // A preset launching a fake `claude` that records what it was handed OUTSIDE the data root,
        // and prints the key it was given on stdout and stderr, as a careless CLI might.
        var bin = Directory.CreateDirectory(Path.Combine(_outside, "bin")).FullName;
        var program = Path.Combine(bin, "claude");
        await TestExecutable.WriteAsync(program,
            "#!/bin/sh\n"
            // Only the run (`-p`) records: a launch check (`--version`) or sign-in probe the catalog change
            // starts in the background must not overwrite what the run was handed.
            + $"[ \"$1\" = -p ] && env > '{Path.Combine(_outside, "seen.txt")}'\n"
            + "echo \"key: $ANTHROPIC_API_KEY\"; echo \"key: $ANTHROPIC_API_KEY\" >&2\n");

        var catalog = Services.GetRequiredService<AgentCatalog>();
        var builtIn = catalog.Definition("claude-headless")!;
        catalog.Replace([.. catalog.Definitions, new AgentDefinition("fake-claude", AgentMode.Headless,
            new AgentLaunch(program, ["-p", "{userPrompt}"]), LaunchCheck: ["--version"], IssuedCredential: builtIn.IssuedCredential)]);

        // Set by a person, replaced by the operator, replaced again by a person.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/api/agents/fake-claude/credential", new { kind = "apiKey", value = First })).Status);
        Assert.Equal(200, (await ExchangeAsync(new { action = "set", agent = "claude", kind = "apiKey", value = ByOperator })).GetProperty("status").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/api/agents/claude/credential", new { kind = "apiKey", value = Second })).Status);
        await SourcesAsync(new() { ["fake-claude"] = "issued" });

        // An issued run through the Host's own runner, a sign-in probe and a launch check.
        var workspace = Directory.CreateDirectory(Path.Combine(_outside, "workspace")).FullName;
        var result = await Services.GetRequiredService<ProcessAgentRunner>().RunAsync(
            new AgentInvocation(new ContainerId(_team, "Dev"), "You are a test.", "work", workspace,
                new Dictionary<string, string>(), Agent: "fake-claude"),
            Ct);
        Assert.True(result.Succeeded, result.LaunchError);

        // What the CLI printed comes back with the key it was given replaced, never dropped.
        Assert.DoesNotContain(Second, result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"key: {DiagnosticRedaction.Placeholder}", result.Output);
        var seen = File.ReadAllLines(Path.Combine(_outside, "seen.txt"));
        Assert.Contains($"ANTHROPIC_API_KEY={Second}", seen);
        var temp = seen.Single(l => l.StartsWith("TMPDIR=", StringComparison.Ordinal))[7..];

        // A PUMPED run: a member told through the route, run by the Host's own pump, its rows and
        // payloads written to the log. Its CLI records what it was handed outside the data root and
        // answers without echoing it.
        var quiet = Path.Combine(Directory.CreateDirectory(Path.Combine(_outside, "quiet-bin")).FullName, "claude");
        var quietSeen = Path.Combine(_outside, "quiet-seen.txt");
        await TestExecutable.WriteAsync(quiet, "#!/bin/sh\n" + $"env > '{quietSeen}'\n" + "echo done\n");
        catalog.Replace([.. catalog.Definitions, new AgentDefinition("fake-quiet", AgentMode.Headless,
            new AgentLaunch(quiet, ["-p", "{userPrompt}"]), IssuedCredential: builtIn.IssuedCredential)]);
        await SourcesAsync(new() { ["fake-claude"] = "issued", ["fake-quiet"] = "issued" });

        var added = await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/containers", new { name = "Quiet", agent = "fake-quiet" });
        Assert.True(added.Status == HttpStatusCode.OK, added.Body);
        var told = await SendAsync(HttpMethod.Post, $"/api/teams/{_team}/containers/Quiet/tell", new { instruction = "work" });
        Assert.True(told.Status == HttpStatusCode.OK, told.Body);

        var log = Services.GetRequiredService<IMessageLog>();
        IReadOnlyList<Message> ended = [];
        await WaitUntilAsync(() =>
        {
            ended = log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 100, Ct).GetAwaiter().GetResult()
                .Where(m => m.Source.Contains("Quiet", StringComparison.Ordinal)).ToList();
            return ended.Count > 0;
        });
        Assert.True(File.Exists(quietSeen), string.Join("\n", ended.Select(m => m.Type + " " + m.Payload)));
        Assert.Contains($"ANTHROPIC_API_KEY={Second}", File.ReadAllLines(quietSeen));
        var quietTemp = File.ReadAllLines(quietSeen).Single(l => l.StartsWith("TMPDIR=", StringComparison.Ordinal))[7..];

        // Every row of the log, each payload whole.
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var rows = connection.CreateCommand();
            rows.CommandText = "SELECT type || ' ' || source || ' ' || payload FROM messages";
            await using var reader = await rows.ExecuteReaderAsync(Ct);
            var count = 0;
            while (await reader.ReadAsync(Ct))
            {
                _responses.Add(reader.GetString(0));
                count++;
            }

            Assert.True(count > 0);
        }

        var launch = await Services.GetRequiredService<AgentLaunchChecks>().ReportsAsync(Ct, fresh: true);
        Assert.Equal(AgentLaunchReport.Ok, launch["fake-claude"].Result);

        // Every route a person reads, and the doctor.
        await GetAsync("/api/agents");
        await GetAsync("/api/agents/auth");
        await GetAsync("/api/agents/credentials");
        await GetAsync("/api/tenant/settings");
        await GetAsync("/api/tenant-log");
        await GetAsync("/api/diagnostics");
        await GetAsync("/api/teams");
        _responses.Add(HostDoctor.ToJson(await HostDoctor.ReportAsync(_dataRoot, Ct)));
        _responses.Add(JsonSerializer.Serialize(launch, JsonSerializerOptions.Web));

        string[] secrets = [First, Second, ByOperator];
        string[] fragments = [.. secrets.Select(s => s.Substring(s.Length - 12))];

        foreach (var needle in secrets.Concat(fragments))
        {
            Assert.DoesNotContain(_responses, r => r.Contains(needle, StringComparison.Ordinal));
        }

        // EVERY FILE under the data root - the database and its WAL, the Host's logs, records, the
        // request folder - and in the pumped member's TMPDIR, as Latin-1 and as UTF-16.
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(quietTemp, "*", SearchOption.AllDirectories)))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            var latin = Encoding.Latin1.GetString(bytes);
            var utf16 = Encoding.Unicode.GetString(bytes) + Encoding.Unicode.GetString(bytes.Skip(1).ToArray());
            foreach (var needle in secrets.Concat(fragments))
            {
                Assert.False(latin.Contains(needle, StringComparison.Ordinal) || utf16.Contains(needle, StringComparison.Ordinal),
                    $"{file} holds an issued credential in plain text.");
            }
        }

        // And neither run left a home behind.
        Assert.Empty(Directory.EnumerateDirectories(temp, RunHome.Prefix + "*"));
        Assert.Empty(Directory.EnumerateDirectories(quietTemp, RunHome.Prefix + "*"));
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private async Task SourcesAsync(Dictionary<string, string> sources)
    {
        var written = await SendAsync(HttpMethod.Put, "/api/tenant/settings",
            new Dictionary<string, object> { [TenantSettings.AgentCredentialSourceName] = sources });
        Assert.True(written.Status == HttpStatusCode.OK, written.Body);
    }

    private async Task<bool> StoredAsync(string command)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT COUNT(*) FROM agent_credentials WHERE command = $command";
        select.Parameters.AddWithValue("$command", command);
        return Convert.ToInt64(await select.ExecuteScalarAsync(Ct)) > 0;
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

    private async Task<JsonElement> ExchangeAsync(object body)
    {
        var folder = Path.Combine(_dataRoot, AgentCredentialRequests.Folder);
        var nonce = Guid.NewGuid().ToString("N");
        var node = JsonSerializer.SerializeToNode(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        node["request"] = nonce;

        await WaitUntilAsync(() => Directory.Exists(folder));
        var temporary = Path.Combine(folder, AgentCredentialRequests.RequestFile + ".tmp");
        await File.WriteAllTextAsync(temporary, node.ToJsonString(), Ct);
        File.Move(temporary, Path.Combine(folder, AgentCredentialRequests.RequestFile), overwrite: true);

        var report = Path.Combine(folder, AgentCredentialRequests.ReportFile);
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
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(50, Ct);
        }
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

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
