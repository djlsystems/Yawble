using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A MEMBER RUN'S OWN CREDENTIAL NEVER REACHES WHAT THE HOST STORES OR SERVES, on either source. A
/// real Host pumps a member whose CLI is a fake named as a built-in preset's command: it prints the
/// key it was given - plain, JSON-escaped and upper-cased - on stdout and stderr, in its transcript
/// and in its launch check, and a second run crashes with it on stderr. The test, acting as the
/// agent, posts it in progress, blocked and hand-back reports and watches the live view. Then every
/// row, every response and every file the Host writes under the data root is searched for it, in
/// any case, and the marker is found where the key was. The CLI's own kept transcript is not the
/// Host's and is not searched. A run whose environment carries no credential is returned byte for
/// byte. Presets and variables are read from the catalog; none is named here.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class RunOutputRedactionTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string Marker = DiagnosticRedaction.Placeholder;
    private const string Member = "Leaky";

    // Fake, long, and holding `+` and `/` so its JSON-escaped forms differ from it.
    private const string Value = "fake-run-key+Qm7vX2pL/9tRw4kZs8Hn3";
    private const string Other = "fake-other-key+Zt5mW1qR/8vYb3nLc6Dp";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-run-redaction-").FullName;
    private readonly List<string> _responses = [];
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _person;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DataRoot => Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;

    private string Outside => Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _person?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        MemberTempCleanup.Remove(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public Task An_issued_credential_a_cli_prints_reaches_no_row_route_transcript_read_or_file_under_the_data_root() =>
        ScanAsync(issued: true);

    [Fact]
    public Task A_host_provider_key_a_home_run_prints_reaches_no_row_route_transcript_read_or_file_under_the_data_root() =>
        ScanAsync(issued: false);

    [Fact]
    public async Task A_run_whose_environment_carries_no_credential_returns_its_output_byte_for_byte()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        // Text the credential-shape rules WOULD rewrite, and the marker's own word.
        var printed = "password=hunter2 token " + "Qm7vX2pL9tRw4kZs8Hn3Yb6dJ1fA5gC0eT7uI2oP4sW" + " redacted [redacted]";
        Assert.NotEqual(printed, DiagnosticRedaction.RedactWithoutLimit(printed));

        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var program = Path.Combine(bin, "fake-plain-cli");
        await TestExecutable.WriteAsync(program, "#!/bin/sh\n" + $"printf '%s' '{printed}'\n");

        var catalog = new AgentCatalog(
            [new AgentDefinition("fake-plain", AgentMode.Headless, new AgentLaunch(program, ["-p", "{userPrompt}"]))]);

        // The Host holds no credential variable at all.
        using var none = new EnvironmentScope(RunSecrets.CredentialNames(new AgentCatalog(AgentCatalogFile.BuiltIns()))
            .Select(n => new KeyValuePair<string, string>(n, "")));

        var secrets = new RunSecrets(catalog);
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat(), secrets: secrets);
        var member = new ContainerId("Alpha", "Plain");
        var result = await runner.RunAsync(
            new AgentInvocation(member, "You are a test.", "work",
                Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName,
                new Dictionary<string, string>(), Agent: "fake-plain"),
            Ct);

        Assert.True(result.Succeeded, result.LaunchError);
        Assert.Equal(printed, result.Output);
        Assert.Same(ValueRedactor.Empty, secrets.For(member));
    }

    // ---- the scan ---------------------------------------------------------------------------------

    private async Task ScanAsync(bool issued)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        // The first built-in headless preset with a declaration, and the variable a run of it reads
        // its key from: issued, its first kind; home, the first kind the run keeps from the Host.
        var builtIns = new AgentCatalog(AgentCatalogFile.BuiltIns());
        var preset = builtIns.Definition(HomeLaunchBed.HeadlessPresets().First().Data)!;
        var command = RunCredentials.CommandOf(preset);
        var declaration = preset.IssuedCredential!;
        var own = AgentEnvironment.ProviderVariableFor(command);
        var variable = issued
            ? declaration.Kinds[0].Variable
            : declaration.Kinds.Select(k => k.Variable).First(v => v == own || !AgentEnvironment.ProviderVariables.Contains(v));

        // Home: another command's declared variable, handed in by the team's env and so kept.
        var other = issued
            ? null
            : builtIns.Definitions
                .Where(d => !string.Equals(RunCredentials.CommandOf(d), command, StringComparison.OrdinalIgnoreCase))
                .SelectMany(HomeLaunchBed.Declared)
                .Except(HomeLaunchBed.Declared(preset))
                .Order(StringComparer.Ordinal)
                .First();

        // The Host's own HOME and the CLI's config folders point into this test, never at real ones.
        var home = Directory.CreateDirectory(Path.Combine(_root, "host-home")).FullName;
        var hostEnvironment = new List<KeyValuePair<string, string>> { new("HOME", home) };
        hostEnvironment.AddRange(builtIns.Definitions.SelectMany(d => d.IssuedCredential?.HomeVariables ?? []).Distinct()
            .Select(n => new KeyValuePair<string, string>(n, Directory.CreateDirectory(Path.Combine(_root, "config", n)).FullName)));
        if (!issued) hostEnvironment.Add(new(variable, Value));
        using var scope = new EnvironmentScope(hostEnvironment);

        var program = await FakeCliAsync(command, variable, other);
        var manager = Path.Combine(Path.GetDirectoryName(program)!, "fake-manager-cli");
        await TestExecutable.WriteAsync(manager, "#!/bin/sh\necho ok\n");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", DataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning"));
        var services = _factory.Services;

        var catalog = services.GetRequiredService<AgentCatalog>();
        catalog.Replace([.. catalog.Definitions,
            new AgentDefinition("fake-run", AgentMode.Headless,
                new AgentLaunch(program, ["-p", "{userPrompt}", "--session-id", "{sessionId}"]),
                LaunchCheck: ["--version"],
                IssuedCredential: declaration,
                LiveView: new AgentLiveView("~/t/{sessionId}.jsonl", LiveView.ClaudeJsonl)),
            new AgentDefinition("fake-manager", AgentMode.Headless, new AgentLaunch(manager, ["-p", "{userPrompt}"]))]);

        var team = (await services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Redaction", "fake-manager", memberAgent: "fake-run", ct: Ct)).Id;

        await services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        if (issued)
        {
            await OkAsync(HttpMethod.Put, "/api/agents/fake-run/credential", new { kind = declaration.Kinds[0].Kind, value = Value });
            await OkAsync(HttpMethod.Put, "/api/tenant/settings",
                new Dictionary<string, object> { [TenantSettings.AgentCredentialSourceName] = new Dictionary<string, string> { ["fake-run"] = "issued" } });
        }
        else
        {
            // Not captured: a team's env is the person's own record of what they handed in.
            using var put = await _person.PutAsJsonAsync($"/api/teams/{team}/env", new Dictionary<string, string> { [other!] = Other }, Ct);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        await OkAsync(HttpMethod.Post, $"/api/teams/{team}/containers", new { name = Member, agent = "fake-run" });
        await OkAsync(HttpMethod.Post, $"/api/teams/{team}/containers/{Member}/tell", new { instruction = "work" });

        // The CLI has written its transcript and waits. The test is the agent now.
        await WaitUntilAsync(() => File.Exists(Path.Combine(Outside, "ready")), "the fake CLI to start");
        var seen = File.ReadAllLines(Path.Combine(Outside, "env.txt"))
            .Where(l => l.Contains('=')).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(Value, seen[variable]);
        if (other is not null) Assert.Equal(Other, seen[other]);

        var live = await LiveAsync(team);

        using (var agent = _factory.CreateClient())
        {
            agent.DefaultRequestHeaders.Add("X-Api-Key", seen["HARNESS_KEY"]);
            foreach (var (route, body) in new (string, object)[]
                     {
                         ("progress", new { status = $"printed {Value} and {Escaped(Value)} {Other}" }),
                         ("handback", new { delivered = $"used {Value.ToUpperInvariant()} {Other}" }),
                         ("blocked", new { reason = $"stuck on {Slashed(Value)} {Other}" }),
                     })
            {
                using var posted = await agent.PostAsJsonAsync($"/api/teams/{team}/containers/{Member}/{route}", body, Ct);
                var answer = await posted.Content.ReadAsStringAsync(Ct);
                _responses.Add(answer);
                Assert.True(posted.IsSuccessStatusCode, $"{route}: {posted.StatusCode} {answer}");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(Outside, "go"), "", Ct);
        var first = await TerminalAsync(1);

        // A second run crashes, with the key on stderr only.
        await OkAsync(HttpMethod.Post, $"/api/teams/{team}/containers/{Member}/tell", new { instruction = "again" });
        var crashed = await TerminalAsync(2);

        // A fresh launch check, through the Host's own checks.
        var launch = await services.GetRequiredService<AgentLaunchChecks>().ReportsAsync(Ct, fresh: true);
        _responses.Add(JsonSerializer.Serialize(launch, JsonSerializerOptions.Web));

        // Every route a person reads.
        var runs = await GetAsync($"/api/teams/{team}/members/{Member}/runs");
        var transcript = await GetAsync($"/api/teams/{team}/members/{Member}/runs/{first.Seq}/transcript");
        await GetAsync("/api/teams");
        await GetAsync("/api/agents/auth");
        await GetAsync("/api/diagnostics");
        await GetAsync("/api/tenant-log");
        _responses.Add(live);

        // Every row of the log, each whole.
        var rows = new List<string>();
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(DataRoot, "messages.db")};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT type || ' ' || source || ' ' || payload FROM messages";
            await using var reader = await select.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        }

        // Replaced, never dropped.
        Assert.Contains(Marker, Field(first.Payload, PayloadFields.Output));
        Assert.Contains(Marker, Field(crashed.Payload, PayloadFields.LaunchError));
        Assert.Contains(rows, r => r.StartsWith(MessageTypes.Progress + " ", StringComparison.Ordinal) && r.Contains(Marker, StringComparison.Ordinal));
        Assert.Contains(Marker, transcript);
        Assert.Contains(Marker, live);
        Assert.Contains("transcript-written", transcript);
        Assert.Contains(Marker, launch["fake-run"].StderrTail);
        Assert.Contains(JsonDocument.Parse(runs).RootElement.GetProperty("runs").EnumerateArray(),
            run => run.GetProperty("seq").GetInt64() == first.Seq);

        var needles = Needles(Value).Concat(other is null ? [] : Needles(Other)).ToList();
        foreach (var needle in needles)
        {
            Assert.DoesNotContain(rows, r => r.Contains(needle, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(_responses, r => r.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        // EVERY FILE the Host writes under the data root, as Latin-1 and as UTF-16 at both
        // alignments. Not the CLI's own kept transcripts in the member's temporary folder, which hold
        // what the CLI wrote; and not the team's env value, which the person stored there.
        SqliteConnection.ClearAllPools();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + RunHome.TranscriptsFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            scanned++;
            var latin = Encoding.Latin1.GetString(bytes);
            var utf16 = Encoding.Unicode.GetString(bytes) + Encoding.Unicode.GetString(bytes.Skip(1).ToArray());
            foreach (var needle in Needles(Value))
            {
                Assert.False(
                    latin.Contains(needle, StringComparison.OrdinalIgnoreCase) || utf16.Contains(needle, StringComparison.OrdinalIgnoreCase),
                    $"{file} holds the run's credential.");
            }
        }

        Assert.True(scanned > 0);
    }

    /// <summary>
    /// The fake CLI. `--version` prints the key on stderr and fails; a launch with no session - a tool
    /// listing - answers and exits. A first member run records its
    /// environment outside the data root, writes its transcript, waits for the test, then prints the
    /// key in each form on stdout and stderr. A later run prints it on stderr only and exits 1.
    /// </summary>
    private async Task<string> FakeCliAsync(string command, string variable, string? other)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var program = Path.Combine(bin, command);
        var o = other is null ? "" : "$" + other;
        await TestExecutable.WriteAsync(program,
            "#!/bin/sh\n"
            + $"V=\"${variable}\"; O=\"{o}\"\n"
            + "esc() { printf '%s' \"$1\" | sed -e 's,+,\\\\u002B,g'; }\n"
            + "slash() { printf '%s' \"$1\" | sed -e 's,/,\\\\/,g'; }\n"
            + "up() { printf '%s' \"$1\" | tr '[:lower:]' '[:upper:]'; }\n"
            + "all() { printf '%s\\n' \"$1 $V $(esc \"$V\") $(slash \"$V\") $(up \"$V\") $O $(esc \"$O\") $(up \"$O\")\"; }\n"
            + "if [ \"$1\" = --version ]; then all version >&2; exit 3; fi\n"
            + "session=''; previous=''\n"
            + "for a in \"$@\"; do if [ \"$previous\" = --session-id ]; then session=\"$a\"; fi; previous=\"$a\"; done\n"
            // Anything that is not a member run - a tool listing - answers as a CLI with no plugins.
            + "if [ -z \"$session\" ]; then if [ \"$1\" = plugin ]; then echo '[]'; fi; exit 0; fi\n"
            + $"n=$(cat '{Outside}/count' 2>/dev/null || echo 0); n=$((n+1)); echo $n > '{Outside}/count'\n"
            + "if [ $n -gt 1 ]; then all crashed >&2; exit 1; fi\n"
            + $"env > '{Outside}/env.txt'\n"
            + "mkdir -p \"$HOME/t\"\n"
            + "{ printf '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"escaped %s %s\"}]}}\\n' \"$(esc \"$V\")\" \"$(esc \"$O\")\"\n"
            + "  echo \"plain $V $O\"; echo \"upper $(up \"$V\") $(up \"$O\")\"; echo transcript-written; } > \"$HOME/t/$session.jsonl\"\n"
            + $"touch '{Outside}/ready'\n"
            + $"i=0; while [ ! -e '{Outside}/go' ] && [ $i -lt 300 ]; do sleep 0.1; i=$((i+1)); done\n"
            + "all out; all err >&2\n"
            + "exit 0\n");
        return program;
    }

    /// <summary>The value, its JSON-escaped forms, and a 12-character fragment of it.</summary>
    private static IEnumerable<string> Needles(string value) =>
        [value, Escaped(value), Slashed(value), value[^12..]];

    private static string Escaped(string value) => JsonSerializer.Serialize(value)[1..^1];

    private static string Slashed(string value) => value.Replace("/", "\\/", StringComparison.Ordinal);

    private async Task<string> LiveAsync(string team)
    {
        using var response = await _person!.GetAsync(
            $"/api/teams/{team}/members/{Member}/live", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString());

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(20));

        var chunk = new StringBuilder();
        while (await reader.ReadLineAsync(bounded.Token) is { } line)
        {
            chunk.AppendLine(line);
            if (line.Contains("transcript-written", StringComparison.Ordinal)) break;
        }

        Assert.Contains("transcript-written", chunk.ToString());
        return chunk.ToString();
    }

    /// <summary>The member's <paramref name="count"/>th terminal row.</summary>
    private async Task<Message> TerminalAsync(int count)
    {
        var log = _factory!.Services.GetRequiredService<IMessageLog>();
        IReadOnlyList<Message> ended = [];
        await WaitUntilAsync(() =>
        {
            ended = log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 100, Ct).GetAwaiter().GetResult()
                .Where(m => m.Source.EndsWith("/" + Member, StringComparison.OrdinalIgnoreCase)).ToList();
            return ended.Count >= count;
        }, $"run {count} to end");
        return ended[count - 1];
    }

    private static string Field(string payload, string name) =>
        JsonDocument.Parse(payload).RootElement.TryGetProperty(name, out var value) ? value.ToString() : "";

    private async Task OkAsync(HttpMethod method, string path, object body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await _person!.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        _responses.Add(text);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {response.StatusCode} {text}");
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await _person!.GetAsync(path, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        _responses.Add(text);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {text}");
        return text;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50, Ct);
        }
    }
}
