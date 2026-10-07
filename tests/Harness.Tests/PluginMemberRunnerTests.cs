using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// Protocol harness.member/1, through the real member runtime: a plugin is a /bin/sh fixture that
/// ignores or reads its stdin and prints JSON Lines. Fast and independent of the sample plugin,
/// which <see cref="PluginMemberEndToEndTests"/> runs.
/// </summary>
public sealed class PluginMemberRunnerTests : IDisposable
{
    private static readonly ContainerId Plug = new("alpha", "plug");

    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-plugin-run-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The fixture's declared events join the catalog for the test, as a Host's do.</summary>
    private readonly List<IDisposable> _registrations = [];

    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Secrets(Dictionary<string, string> values) : ISecretStore
    {
        public string? TryGet(string logicalKey) => values.GetValueOrDefault(logicalKey);
    }

    private sealed class Settings(PluginMemberSettings settings) : IPluginMemberSettings
    {
        public Task<PluginMemberSettings> ForAsync(ContainerId member, CancellationToken ct = default) => Task.FromResult(settings);
    }

    private async Task<(ContainerTestBed Bed, MemberRuntime Member, Message Row)> RunAsync(
        string script, Action<JsonObject>? manifest = null, string instruction = "hello",
        IPluginMemberSettings? settings = null, ISecretStore? secrets = null)
    {
        PluginInstall.Write(_dataRoot, "fixture", script: script, manifest: PluginInstall.Manifest("fixture", edit: manifest));

        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();
        Assert.NotNull(catalog.For("fixture"));
        _registrations.Add(EventCatalog.Register(catalog));

        var bed = new ContainerTestBed();
        var heartbeat = new RunHeartbeat();
        var plugins = new PluginMemberRunner(catalog, new MemberReports(bed.Host, bed.Store, heartbeat), heartbeat, null, settings, secrets);

        var member = await bed.Host.AddAsync(
            ContainerTestBed.Definition(Plug) with { Agent = "plugin:fixture", WorkingDirectory = _dataRoot },
            new MemberRunnerRouter(bed.Runner, plugins), Ct);

        await bed.Store.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Plug), JsonSerializer.Serialize(new { instruction }), "alpha/manager"), Ct);

        Assert.True(await bed.PumpUntilAsync(async () =>
            (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 10, Ct)).Count > 0, attempts: 400));

        var row = (await bed.Store.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], 10, Ct)).Single();
        return (bed, member, row);
    }

    private static readonly string[] EveryRunType =
    [
        MessageTypes.Started, MessageTypes.Progress, MessageTypes.Handback, MessageTypes.Completed,
        MessageTypes.Failed, MessageTypes.Blocked, "plugin.fixture.tick",
    ];

    private static string Output(Message row) => JsonDocument.Parse(row.Payload).RootElement.GetProperty("output").GetString()!;

    [Fact]
    public async Task A_result_record_is_the_output_of_a_completed_run()
    {
        var (bed, member, row) = await RunAsync("""cat >/dev/null; echo '{"t":"result","ok":true,"output":"HELLO"}'""");
        await using var _ = bed;

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.Equal("HELLO", Output(row));
        Assert.Null(member.Snapshot().Failed);
    }

    [Fact]
    public async Task The_request_carries_the_work_as_data()
    {
        var (bed, _, row) = await RunAsync(
            """req=$(cat); printf '%s\n' "$req" > request.json; echo '{"t":"result","ok":true,"output":"read"}'""",
            instruction: "do the thing");
        await using var _ = bed;

        var request = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dataRoot, "request.json"), Ct))!;
        Assert.Equal(PluginManifest.ProtocolV1, (string?)request["protocol"]);
        Assert.Equal("alpha/plug", (string?)request["member"]!["id"]);
        var work = Assert.Single(request["work"]!.AsArray());
        Assert.Equal("do the thing", (string?)work!["instruction"]);
        Assert.Equal(MessageTypes.InstructionFor(Plug), (string?)work["type"]);
        Assert.Equal((long)request["causation"]!, row.CausationSeq);
    }

    [Fact]
    public async Task A_list_setting_is_delivered_in_the_requests_config_and_defaults_to_empty()
    {
        var bound = new Dictionary<string, JsonElement>
        {
            ["allow"] = JsonSerializer.SerializeToElement(new[] { "a@example.test", "b@example.test" }),
        };

        var (bed, _, row) = await RunAsync(
            """req=$(cat); printf '%s\n' "$req" > request.json; echo '{"t":"result","ok":true,"output":"read"}'""",
            manifest: m => m["config"] = JsonNode.Parse("""{"allow":{"type":"list"},"scopes":{"type":"list","enum":["read","send"]}}"""),
            settings: new Settings(new PluginMemberSettings(bound, new Dictionary<string, string>())));
        await using var _ = bed;

        Assert.Equal(MessageTypes.Completed, row.Type);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dataRoot, "request.json"), Ct))!["config"]!;
        Assert.Equal("""["a@example.test","b@example.test"]""", config["allow"]!.ToJsonString());
        Assert.Equal("[]", config["scopes"]!.ToJsonString());
    }

    [Fact]
    public async Task Reports_on_stdout_have_the_same_effects_as_the_routes()
    {
        var (bed, member, _) = await RunAsync("""
            cat >/dev/null
            echo '{"t":"progress","status":"working on it"}'
            echo '{"t":"needsDecision","question":"which bucket?"}'
            echo '{"t":"handback","delivered":"wrote the blob"}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """);
        await using var _ = bed;

        Assert.Equal("""{"status":"working on it"}""", Assert.Single(await bed.OfTypeAsync(MessageTypes.Progress)).Payload);
        Assert.Equal("which bucket?", member.Snapshot().NeedsDecision);
        Assert.Equal("wrote the blob", member.Snapshot().HandedBack);
        Assert.Single(await bed.OfTypeAsync(MessageTypes.Handback));
    }

    [Fact]
    public async Task A_blocked_record_marks_the_member()
    {
        var (bed, member, _) = await RunAsync("""cat >/dev/null; echo '{"t":"blocked","reason":"no credentials"}'; echo '{"t":"result","ok":true,"output":""}'""");
        await using var _ = bed;

        Assert.Equal("no credentials", member.Snapshot().Blocked);
    }

    [Fact]
    public async Task A_missing_result_is_a_failure()
    {
        var (bed, member, row) = await RunAsync("cat >/dev/null; echo just text");
        await using var _ = bed;

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.Equal("The plugin exited 0 without a result record.", member.Snapshot().Failed);
        Assert.Equal(FailureClasses.Unknown, member.Snapshot().FailureClass);
        Assert.Contains("just text", Output(row));
    }

    [Fact]
    public async Task A_result_that_is_not_ok_is_a_failure_in_the_plugins_words()
    {
        var (bed, member, row) = await RunAsync("""cat >/dev/null; echo '{"t":"result","ok":false,"error":"the bucket does not exist"}'; exit 1""");
        await using var _ = bed;

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.Equal("the bucket does not exist", member.Snapshot().Failed);

        // THE ROW SAYS IT TOO, not only the live card: the board after a reload, the trail and an
        // agent reading the log all take a failure's words from the row.
        Assert.Equal("the bucket does not exist", Output(row));
        Assert.Equal("the bucket does not exist", new ContainerMarks(null, null, row).FailureReason);
    }

    [Fact]
    public async Task A_failure_reason_the_output_already_says_is_not_repeated_and_one_it_does_not_leads_it()
    {
        var (bed, _, row) = await RunAsync("""cat >/dev/null; echo 'tried the bucket'; echo '{"t":"result","ok":false,"output":"the bucket does not exist","error":"the bucket does not exist"}'""");
        await using var _ = bed;
        Assert.Equal(1, CountOf(Output(row), "the bucket does not exist"));

        var (other, _, failed) = await RunAsync("""cat >/dev/null; echo 'tried the bucket'; echo '{"t":"result","ok":false,"error":"no access"}'""");
        await using var __ = other;
        Assert.StartsWith("no access\n", Output(failed), StringComparison.Ordinal);
        Assert.Contains("tried the bucket", Output(failed), StringComparison.Ordinal);
    }

    private static int CountOf(string text, string words) =>
        (text.Length - text.Replace(words, "", StringComparison.Ordinal).Length) / words.Length;

    [Fact]
    public async Task A_non_zero_exit_fails_even_after_an_ok_result()
    {
        var (bed, member, _) = await RunAsync("""cat >/dev/null; echo '{"t":"result","ok":true,"output":"x"}'; exit 4""");
        await using var _ = bed;

        Assert.Equal("The plugin exited 4.", member.Snapshot().Failed);
    }

    [Fact]
    public async Task Lines_that_are_not_records_are_kept_as_text()
    {
        var (bed, _, row) = await RunAsync("""cat >/dev/null; echo 'plain words'; echo '{not json'; echo '{"t":"unknown-kind"}'; echo '{"t":"result","ok":true,"output":"out"}'""");
        await using var _ = bed;

        var output = Output(row);
        Assert.StartsWith("out", output);
        Assert.Contains("plain words", output);
        Assert.Contains("{not json", output);
        Assert.Contains("""{"t":"unknown-kind"}""", output);
    }

    [Fact]
    public async Task A_silent_plugin_is_stopped_by_its_own_idle_clock_and_its_group_killed()
    {
        var pidFile = Path.Combine(_dataRoot, "child.pid");
        var (bed, member, _) = await RunAsync(
            $"cat >/dev/null; sleep 300 & echo $! > '{pidFile}'; wait",
            manifest: m => m["timeoutSeconds"] = 1);
        await using var _ = bed;

        Assert.Equal(FailureClasses.Timeout, member.Snapshot().FailureClass);
        Assert.Contains("`timeoutSeconds`", member.Snapshot().Failed);

        var pid = int.Parse((await File.ReadAllTextAsync(pidFile, Ct)).Trim());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists($"/proc/{pid}/stat") && !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z", StringComparison.Ordinal)
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(!File.Exists($"/proc/{pid}/stat") || File.ReadAllText($"/proc/{pid}/stat").Contains(") Z", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Progress_records_push_the_idle_clock_out()
    {
        var (bed, _, row) = await RunAsync(
            """
            cat >/dev/null
            for i in 1 2 3 4; do echo '{"t":"progress","status":"tick"}'; sleep 0.5; done
            echo '{"t":"result","ok":true,"output":"survived"}'
            """,
            manifest: m => m["timeoutSeconds"] = 1);
        await using var _ = bed;

        Assert.Equal(MessageTypes.Completed, row.Type);
        Assert.Equal("survived", Output(row));
    }

    [Fact]
    public async Task The_plugin_inherits_only_the_allow_listed_environment()
    {
        var (bed, _, row) = await RunAsync(
            """cat >/dev/null; env | cut -d= -f1 | sort | tr '\n' ' ' > env.txt; echo '{"t":"result","ok":true,"output":"ok"}'""");
        await using var _ = bed;

        var names = (await File.ReadAllTextAsync(Path.Combine(_dataRoot, "env.txt"), Ct))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        string[] shell = ["PWD", "SHLVL", "_", "OLDPWD"];
        Assert.All(names, name => Assert.True(
            PluginMemberRunner.InheritedVariables.Contains(name) || name == PluginMemberRunner.CausationVariable || shell.Contains(name),
            $"{name} reached the plugin"));
        Assert.Contains(PluginMemberRunner.CausationVariable, names);
        Assert.DoesNotContain(names, n => n.StartsWith("CLAUDE", StringComparison.Ordinal) || n.StartsWith("GROK_", StringComparison.Ordinal)
            || n is "HARNESS_KEY" or "HARNESS_URL" || AgentEnvironment.ProviderVariables.Contains(n));
        Assert.Equal(MessageTypes.Completed, row.Type);
    }

    [Fact]
    public async Task A_bound_secret_reaches_stdin_and_never_argv_the_environment_or_the_row()
    {
        const string value = "s3cr3t-value-9f2";
        var settings = new Settings(new PluginMemberSettings(
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" }));

        var (bed, _, row) = await RunAsync(
            """
            req=$(cat)
            case "$req" in *'"token":"s3cr3t-value-9f2"'*) seen=yes;; *) seen=no;; esac
            env > env.txt; echo "$0 $*" > argv.txt
            echo "{\"t\":\"result\",\"ok\":true,\"output\":\"seen=$seen and echoed s3cr3t-value-9f2\"}"
            """,
            manifest: m => m["secrets"] = JsonNode.Parse("""{"token":{"description":"demo","required":true}}"""),
            settings: settings,
            secrets: new Secrets(new() { ["FIXTURE_TOKEN"] = value }));
        await using var _ = bed;

        Assert.Equal("seen=yes and echoed [redacted]", Output(row));
        Assert.DoesNotContain(value, row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(value, await File.ReadAllTextAsync(Path.Combine(_dataRoot, "env.txt"), Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(value, await File.ReadAllTextAsync(Path.Combine(_dataRoot, "argv.txt"), Ct), StringComparison.Ordinal);
    }

    /// <summary>D1: a bound secret written into a REPORT record - not only
    /// the result - must not reach the ledger, the snapshot or the Manager it wakes.</summary>
    [Theory]
    [InlineData("progress", "status")]
    [InlineData("blocked", "reason")]
    [InlineData("needsDecision", "question")]
    [InlineData("handback", "delivered")]
    public async Task A_bound_secret_in_a_report_record_is_redacted_before_it_is_reported(string record, string field)
    {
        const string value = "s3cr3tvalue";
        var settings = new Settings(new PluginMemberSettings(
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" }));

        var (bed, member, _) = await RunAsync(
            $$"""
            cat >/dev/null
            echo '{"t":"{{record}}","{{field}}":"leaked s3cr3tvalue here"}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """,
            manifest: m => m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}"""),
            settings: settings,
            secrets: new Secrets(new() { ["FIXTURE_TOKEN"] = value }));
        await using var _ = bed;

        var rows = await bed.Store.ReadAfterAsync(0,
            [MessageTypes.Progress, MessageTypes.Blocked, MessageTypes.NeedsDecision, MessageTypes.Handback, MessageTypes.Completed, MessageTypes.Failed],
            int.MaxValue, Ct);
        Assert.Contains(rows, r => r.Payload.Contains("leaked [redacted] here", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Payload.Contains(value, StringComparison.Ordinal));

        var snapshot = member.Snapshot();
        Assert.DoesNotContain(value, JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        if (record == "handback") Assert.Equal("leaked [redacted] here", snapshot.HandedBack);
        if (record == "needsDecision") Assert.Equal("leaked [redacted] here", snapshot.NeedsDecision);
        if (record == "blocked") Assert.Equal("leaked [redacted] here", snapshot.Blocked);
    }

    [Fact]
    public async Task Diagnostic_redaction_covers_the_output_the_error_and_stderr()
    {
        const string shaped = "Ab3dEf6hIj9kLm2nOp5qRs8tUv1wXy4zAb7dEf0";
        var (bed, member, row) = await RunAsync(
            $$"""
            cat >/dev/null
            echo 'password=hunter2' >&2
            echo '{"t":"result","ok":false,"output":"key {{shaped}}","error":"token: xyzzy-plugh"}'
            """);
        await using var _ = bed;

        Assert.Equal(MessageTypes.Failed, row.Type);
        Assert.DoesNotContain(shaped, row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", row.Payload, StringComparison.Ordinal);
        Assert.Contains("password=[redacted]", Output(row));
        Assert.Equal("token: [redacted]", member.Snapshot().Failed);
    }

    private static readonly Action<JsonObject> DeclaresTick = m => m["events"] = JsonNode.Parse(
        """{"publishes":[{"type":"tick","summary":"A tick.","fields":[{"name":"n","kind":"number"},{"name":"note"}]}]}""");

    /// <summary>C: a declared event is appended as the member, in the run's workflow, every string in
    /// its payload redacted; the same undeclared type twice is ONE warning row.</summary>
    [Fact]
    public async Task A_declared_publish_is_appended_and_an_undeclared_one_warned_once()
    {
        var (bed, _, row) = await RunAsync(
            """
            cat >/dev/null
            echo '{"t":"publish","type":"tick","payload":{"n":2,"note":"has s3cr3tvalue in it"}}'
            echo '{"t":"publish","type":"plugin.fixture.tick","payload":{"n":3}}'
            echo '{"t":"publish","type":"tock"}'
            echo '{"t":"publish","type":"tock"}'
            echo '{"t":"publish","type":"agentContainer.completed","payload":{}}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """,
            manifest: m =>
            {
                DeclaresTick(m);
                m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}""");
            },
            settings: new Settings(new PluginMemberSettings(
                new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" })),
            secrets: new Secrets(new() { ["FIXTURE_TOKEN"] = "s3cr3tvalue" }));
        await using var _ = bed;

        var ticks = await bed.OfTypeAsync("plugin.fixture.tick");
        Assert.Equal(2, ticks.Count);
        Assert.All(ticks, t => Assert.Equal("alpha/plug", t.Source));
        Assert.All(ticks, t => Assert.Equal(row.CausationSeq, t.CausationSeq));
        Assert.Equal("""{"n":2,"note":"has [redacted] in it"}""", ticks[0].Payload);

        var warnings = (await bed.OfTypeAsync(MessageTypes.Progress)).Select(p => JsonDocument.Parse(p.Payload).RootElement.GetProperty("status").GetString()).ToList();
        Assert.Single(warnings, w => w!.Contains("`tock` was dropped", StringComparison.Ordinal));
        Assert.Single(warnings, w => w!.Contains("`agentContainer.completed` was dropped", StringComparison.Ordinal));
        Assert.Single(await bed.OfTypeAsync(MessageTypes.Completed));
    }

    [Fact]
    public async Task A_publish_over_the_members_artifact_limit_is_dropped()
    {
        var big = new string('x', ArtifactLimits.Default.ExcerptChars + 10);
        var (bed, _, _) = await RunAsync(
            $$$"""
            cat >/dev/null
            echo '{"t":"publish","type":"tick","payload":{"note":"{{{big}}}"}}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """,
            manifest: DeclaresTick);
        await using var _ = bed;

        Assert.Empty(await bed.OfTypeAsync("plugin.fixture.tick"));
        Assert.Contains(await bed.OfTypeAsync(MessageTypes.Progress),
            p => JsonDocument.Parse(p.Payload).RootElement.GetProperty("status").GetString()!.Contains("over this member's limit", StringComparison.Ordinal));
    }

    private static Settings TwoTokens() => new(new PluginMemberSettings(
        new Dictionary<string, JsonElement>(),
        new Dictionary<string, string> { ["a"] = "FIXTURE_A", ["b"] = "FIXTURE_B" }));

    /// <summary>A1 (R2-1): when one bound value is a prefix of another, the longer is replaced
    /// first, so no tail of it survives - in a report record and in the result alike.</summary>
    [Fact]
    public async Task Overlapping_secrets_are_redacted_longest_first()
    {
        var (bed, member, row) = await RunAsync(
            """
            cat >/dev/null
            echo '{"t":"handback","delivered":"x abcd1234WXYZ y"}'
            echo '{"t":"result","ok":true,"output":"abcd1234WXYZ"}'
            """,
            manifest: m => m["secrets"] = JsonNode.Parse("""{"a":{"required":true},"b":{"required":true}}"""),
            settings: TwoTokens(),
            secrets: new Secrets(new() { ["FIXTURE_A"] = "abcd1234", ["FIXTURE_B"] = "abcd1234WXYZ" }));
        await using var _ = bed;

        Assert.Equal("x [redacted] y", member.Snapshot().HandedBack);
        Assert.Equal("[redacted]", Output(row));
        Assert.DoesNotContain(await bed.Store.ReadAfterAsync(0, [MessageTypes.Handback, MessageTypes.Completed], int.MaxValue, Ct),
            r => r.Payload.Contains("WXYZ", StringComparison.Ordinal));
    }

    /// <summary>A2 (R2-2): a raw line keeps a value as the plugin serialised it, and the default
    /// JSON encoder writes <c>+</c> as <c>\u002B</c> - that form is redacted too.</summary>
    [Fact]
    public async Task A_secret_json_escaped_in_a_raw_line_is_redacted()
    {
        var (bed, _, row) = await RunAsync(
            """
            cat >/dev/null
            cat <<'EOF'
            {"t":"note","v":"tok\u002Ben-value"}
            plain tok+en-value
            EOF
            echo '{"t":"result","ok":true,"output":"done"}'
            """,
            manifest: m => m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}"""),
            settings: new Settings(new PluginMemberSettings(
                new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" })),
            secrets: new Secrets(new() { ["FIXTURE_TOKEN"] = "tok+en-value" }));
        await using var _ = bed;

        var output = Output(row);
        Assert.Contains("""{"t":"note","v":"[redacted]"}""", output);
        Assert.Contains("plain [redacted]", output);
        Assert.DoesNotContain("en-value", output, StringComparison.Ordinal);
    }

    /// <summary>E4: a value written in another case - an <c>upper</c> transform - is still matched.</summary>
    [Fact]
    public async Task A_secret_is_redacted_whatever_its_case()
    {
        var (bed, member, row) = await RunAsync(
            """
            cat >/dev/null
            echo '{"t":"progress","status":"sent Demo-Token-2026"}'
            echo '{"t":"result","ok":true,"output":"DEMO-TOKEN-2026"}'
            """,
            manifest: m => m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}"""),
            settings: new Settings(new PluginMemberSettings(
                new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" })),
            secrets: new Secrets(new() { ["FIXTURE_TOKEN"] = "demo-token-2026" }));
        await using var _ = bed;

        Assert.Equal("[redacted]", Output(row));
        Assert.Equal("""{"status":"sent [redacted]"}""", Assert.Single(await bed.OfTypeAsync(MessageTypes.Progress)).Payload);
    }

    /// <summary>R1 (round 2): a secret used as a payload PROPERTY NAME, at any depth and in any
    /// case, or as a NUMBER whose text matches a bound value, refuses the publish with one warning
    /// row that names neither. A clean publish of the same type still lands.</summary>
    [Fact]
    public async Task A_secret_as_a_publish_key_or_number_refuses_the_publish()
    {
        var (bed, _, _) = await RunAsync(
            """
            cat >/dev/null
            echo '{"t":"publish","type":"tick","payload":{"s3cr3tvalue":1}}'
            echo '{"t":"publish","type":"tick","payload":{"n":{"S3CR3TVALUE":2}}}'
            echo '{"t":"publish","type":"tick","payload":{"n":[20261234]}}'
            echo '{"t":"publish","type":"tick","payload":{"n":2}}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """,
            manifest: m =>
            {
                DeclaresTick(m);
                m["secrets"] = JsonNode.Parse("""{"a":{"required":true},"b":{"required":true}}""");
            },
            settings: TwoTokens(),
            secrets: new Secrets(new() { ["FIXTURE_A"] = "s3cr3tvalue", ["FIXTURE_B"] = "20261234" }));
        await using var _ = bed;

        Assert.Equal("""{"n":2}""", Assert.Single(await bed.OfTypeAsync("plugin.fixture.tick")).Payload);
        var warning = Assert.Single(await bed.OfTypeAsync(MessageTypes.Progress),
            p => p.Payload.Contains("was dropped", StringComparison.Ordinal));
        Assert.Contains("a property name in its payload holds a secret", warning.Payload);

        var rows = await bed.Store.ReadAfterAsync(0, EveryRunType, int.MaxValue, Ct);
        Assert.DoesNotContain(rows, r => r.Payload.Contains("s3cr3tvalue", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rows, r => r.Payload.Contains("20261234", StringComparison.Ordinal));
    }

    /// <summary>R1 (round 2): the number rule on its own - refused, not rewritten, so a
    /// subscriber never sees a number turn into a string.</summary>
    [Fact]
    public async Task A_number_matching_a_secret_refuses_the_publish()
    {
        var (bed, _, _) = await RunAsync(
            """
            cat >/dev/null
            echo '{"t":"publish","type":"tick","payload":{"n":20261234}}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """,
            manifest: m =>
            {
                DeclaresTick(m);
                m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}""");
            },
            settings: new Settings(new PluginMemberSettings(
                new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" })),
            secrets: new Secrets(new() { ["FIXTURE_TOKEN"] = "20261234" }));
        await using var _ = bed;

        Assert.Empty(await bed.OfTypeAsync("plugin.fixture.tick"));
        Assert.Single(await bed.OfTypeAsync(MessageTypes.Progress),
            p => p.Payload.Contains("a number in its payload matches a secret", StringComparison.Ordinal));
    }

    /// <summary>R1-c (part 2 round 2): the same number in another notation - an exponent, a
    /// trailing zero - is the same value to a subscriber, so it is refused as R1 is.</summary>
    [Theory]
    [InlineData("2.0261234e7")]
    [InlineData("20261234.0")]
    [InlineData("-2.0261234E+7")]
    public async Task A_number_equal_in_value_to_a_secret_refuses_the_publish(string number)
    {
        var (bed, _, _) = await RunAsync(
            $$$"""
            cat >/dev/null
            echo '{"t":"publish","type":"tick","payload":{"n":{{{number}}}}}'
            echo '{"t":"publish","type":"tick","payload":{"n":2.0261235e7}}'
            echo '{"t":"result","ok":true,"output":"done"}'
            """,
            manifest: m =>
            {
                DeclaresTick(m);
                m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}""");
            },
            settings: new Settings(new PluginMemberSettings(
                new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" })),
            secrets: new Secrets(new() { ["FIXTURE_TOKEN"] = "20261234" }));
        await using var _ = bed;

        // Only the other number is published; the secret's value never reaches a row.
        Assert.Equal("""{"n":2.0261235e7}""", Assert.Single(await bed.OfTypeAsync("plugin.fixture.tick")).Payload);
        Assert.Single(await bed.OfTypeAsync(MessageTypes.Progress),
            p => p.Payload.Contains("a number in its payload matches a secret", StringComparison.Ordinal));
        var rows = await bed.Store.ReadAfterAsync(0, EveryRunType, int.MaxValue, Ct);
        Assert.DoesNotContain(rows, r => r.Payload.Contains(number, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>R2 (round 2): PHP and some Java encoders write <c>/</c> as <c>\/</c>; that form
    /// of a bound value is redacted too.</summary>
    [Fact]
    public async Task A_secret_with_an_escaped_solidus_in_a_raw_line_is_redacted()
    {
        var (bed, _, row) = await RunAsync(
            """
            cat >/dev/null
            cat <<'EOF'
            {"t":"note","v":"abcd\/efgh"}
            EOF
            echo '{"t":"result","ok":true,"output":"done"}'
            """,
            manifest: m => m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}"""),
            settings: new Settings(new PluginMemberSettings(
                new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" })),
            secrets: new Secrets(new() { ["FIXTURE_TOKEN"] = "abcd/efgh" }));
        await using var _ = bed;

        Assert.Contains("""{"t":"note","v":"[redacted]"}""", Output(row));
        Assert.DoesNotContain(await bed.Store.ReadAfterAsync(0, EveryRunType, int.MaxValue, Ct),
            r => r.Payload.Contains("efgh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_secret_too_short_to_redact_is_refused_before_launch()
    {
        var settings = new Settings(new PluginMemberSettings(
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" }));

        var (bed, member, _) = await RunAsync(
            "touch ran.txt; cat >/dev/null",
            manifest: m => m["secrets"] = JsonNode.Parse("""{"token":{"required":false}}"""),
            settings: settings, secrets: new Secrets(new() { ["FIXTURE_TOKEN"] = "abc" }));
        await using var _ = bed;

        Assert.Contains($"shorter than {PluginMemberRunner.MinimumSecretLength} characters", member.Snapshot().Failed);
        Assert.False(File.Exists(Path.Combine(_dataRoot, "ran.txt")));
    }

    [Fact]
    public void A_secret_too_short_to_redact_is_refused_at_hire_when_it_is_already_set()
    {
        PluginInstall.Write(_dataRoot, "fixture", manifest: PluginInstall.Manifest("fixture",
            edit: m => m["secrets"] = JsonNode.Parse("""{"token":{"required":false}}""")));
        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();

        var refusal = PluginMemberRunner.SettingsRefusal(
            catalog.For("fixture")!.Manifest,
            new PluginMemberSettings(new Dictionary<string, JsonElement>(), new Dictionary<string, string> { ["token"] = "SHORT_ONE" }),
            new Secrets(new() { ["SHORT_ONE"] = "abc" }));

        Assert.Contains("shorter than", refusal);
    }

    [Fact]
    public async Task A_required_secret_that_is_not_set_is_refused_before_launch()
    {
        var settings = new Settings(new PluginMemberSettings(
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, string> { ["token"] = "NOT_SET_ANYWHERE" }));

        var (bed, member, _) = await RunAsync(
            "touch ran.txt; cat >/dev/null",
            manifest: m => m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}"""),
            settings: settings, secrets: new Secrets([]));
        await using var _ = bed;

        Assert.Contains("`NOT_SET_ANYWHERE`", member.Snapshot().Failed);
        Assert.False(File.Exists(Path.Combine(_dataRoot, "ran.txt")));
    }

    [Fact]
    public async Task A_plugin_uninstalled_since_hire_is_refused_on_the_wake()
    {
        PluginInstall.Write(_dataRoot, "fixture");
        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();

        await using var bed = new ContainerTestBed();
        var heartbeat = new RunHeartbeat();
        var member = await bed.Host.AddAsync(
            ContainerTestBed.Definition(Plug) with { Agent = "plugin:fixture" },
            new MemberRunnerRouter(bed.Runner, new PluginMemberRunner(catalog, new MemberReports(bed.Host, bed.Store, heartbeat), heartbeat)), Ct);

        Directory.Delete(Path.Combine(PluginInstall.PluginsRoot(_dataRoot), "fixture"), recursive: true);
        catalog.Rescan();

        await bed.Store.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Plug), """{"instruction":"x"}""", "console"), Ct);
        Assert.True(await bed.PumpUntilAsync(() => member.Snapshot().Failed is not null));

        Assert.Contains("'plugin:fixture' is not installed on this Host", member.Snapshot().Failed);
    }
}
