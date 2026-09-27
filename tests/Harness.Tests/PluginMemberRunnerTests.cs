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

    public void Dispose()
    {
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
    }

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
