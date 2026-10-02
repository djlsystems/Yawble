using System.Text.Json;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Harness.Tests;

/// <summary>
/// `--doctor` REPORTS AND NEVER WRITES. A volume the Host has not booted has no database and the
/// doctor says so without creating one; a database from a newer build is named, not migrated.
/// </summary>
public sealed class HostDoctorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-doctor-").FullName;

    private string Database => Path.Combine(_root, "messages.db");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_data_root_with_no_database_is_reported_and_no_database_is_created()
    {
        var report = await HostDoctor.ReportAsync(_root, TestContext.Current.CancellationToken);

        Assert.Equal(_root, report.DataRoot.Path);
        Assert.True(report.DataRoot.Writable);
        Assert.False(report.Database.Exists);
        Assert.Null(report.Database.Schema);
        Assert.False(File.Exists(Database));
        Assert.Equal(0, report.Backups.DailyCount);
        Assert.Null(report.Backups.NewestDailyAt);
    }

    [Fact]
    public async Task A_migrated_database_is_accepted_with_nothing_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct);

        var report = await HostDoctor.ReportAsync(_root, ct);

        Assert.True(report.Database.Exists);
        var schema = Assert.IsType<DoctorSchema>(report.Database.Schema);
        Assert.Equal(SchemaModules.All.Select(s => s.Id).Order(StringComparer.Ordinal), schema.Applied);
        Assert.Empty(schema.Pending);
        Assert.Empty(schema.Unknown);
        Assert.True(schema.Accepted);
    }

    [Fact]
    public async Task A_database_from_a_newer_build_is_named_and_not_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct);
        await using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO schema_migrations (id, applied_at) VALUES ('zzz-999', '2026-09-23T00:00:00Z')";
            await insert.ExecuteNonQueryAsync(ct);
        }

        var report = await HostDoctor.ReportAsync(_root, ct);

        var schema = Assert.IsType<DoctorSchema>(report.Database.Schema);
        Assert.Equal(["zzz-999"], schema.Unknown);
        Assert.False(schema.Accepted);
    }

    [Fact]
    public async Task A_database_that_is_not_sqlite_is_reported_with_its_error_and_the_switch_still_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        // The state the doctor exists for: a file where the database should be that SQLite refuses.
        await File.WriteAllTextAsync(Database, "this is not a database\n", ct);
        var output = new StringWriter();

        var report = await HostDoctor.ReportAsync(_root, ct);
        var outcome = await OperatorCommands.TryRunAsync(["--doctor"], _root, output, ct);

        Assert.True(report.Database.Exists);
        Assert.Null(report.Database.Schema);
        Assert.False(string.IsNullOrWhiteSpace(report.Database.Error));
        Assert.Equal(OperatorOutcome.Completed, outcome);
        var last = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
        using var document = JsonDocument.Parse(last);
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("database").GetProperty("error").GetString()));
    }

    [Fact]
    public async Task The_newest_daily_backup_is_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct);
        var backups = new DailyBackup(Database, Path.Combine(_root, "backups"));
        await backups.TakeAsync(new DateTimeOffset(2026, 9, 20, 3, 0, 0, TimeSpan.Zero), ct);
        await backups.TakeAsync(new DateTimeOffset(2026, 9, 21, 3, 0, 0, TimeSpan.Zero), ct);

        var report = await HostDoctor.ReportAsync(_root, ct);

        Assert.Equal(2, report.Backups.DailyCount);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 3, 0, 0, TimeSpan.Zero), report.Backups.NewestDailyAt);
    }

    [Fact]
    public async Task Every_command_in_the_probe_file_is_reported_and_a_missing_one_is_not_measured()
    {
        var report = await HostDoctor.ReportAsync(_root, TestContext.Current.CancellationToken);

        var expected = AgentAuthProbe.LoadSpecs().Keys.Order(StringComparer.Ordinal);
        Assert.Equal(expected, report.Agents.Select(a => a.Agent).Order(StringComparer.Ordinal));
        Assert.Contains(report.Agents, a => a.Agent == "claude");
        Assert.DoesNotContain(report.Agents, a => a.Agent == "agy");

        // No version file, so nothing is claimed about versions.
        Assert.Null(report.VersionsRecordedAt);
        Assert.All(report.Agents, a => Assert.Null(a.Version));

        // Whatever is or is not on this machine's PATH, a command that is not installed is null,
        // never false: not measured is not a failure.
        Assert.All(report.Agents.Where(a => !a.Installed), a => Assert.Null(a.Authenticated));
    }

    [Fact]
    public async Task Versions_come_from_the_newest_recorded_start_and_a_corrupt_line_is_skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllLinesAsync(Path.Combine(_root, CliVersionHistory.FileName),
        [
            """{"at":"2026-09-20T01:00:00Z","versions":{"claude":"1.0.0","codex":null}}""",
            """{"at":"2026-09-22T01:00:00Z","versions":{"claude":"2.1.3","codex":"0.51.0","grok":null}}""",
            "{not json",
        ], ct);

        var report = await HostDoctor.ReportAsync(_root, ct);

        Assert.Equal(new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero), report.VersionsRecordedAt);
        Assert.Equal("2.1.3", Assert.Single(report.Agents, a => a.Agent == "claude").Version);
        Assert.Equal("0.51.0", Assert.Single(report.Agents, a => a.Agent == "codex").Version);
        Assert.Null(Assert.Single(report.Agents, a => a.Agent == "grok").Version);
    }

    [Fact]
    public async Task The_doctor_switch_prints_the_report_as_the_last_line_and_completes()
    {
        var output = new StringWriter();

        var outcome = await OperatorCommands.TryRunAsync(["--doctor"], _root, output, TestContext.Current.CancellationToken);

        Assert.Equal(OperatorOutcome.Completed, outcome);
        var last = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
        using var document = JsonDocument.Parse(last);
        Assert.Equal(_root, document.RootElement.GetProperty("dataRoot").GetProperty("path").GetString());
        Assert.False(File.Exists(Database));
    }

    [Fact]
    public async Task The_doctor_switch_answers_while_a_host_holds_the_data_root()
    {
        var ct = TestContext.Current.CancellationToken;
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct);
        using var held = DataRootLock.Acquire(_root);
        var output = new StringWriter();

        var outcome = await OperatorCommands.TryRunAsync(["--DataRoot=" + _root, "--doctor"], _root, output, ct);

        Assert.Equal(OperatorOutcome.Completed, outcome);
        var last = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
        using var document = JsonDocument.Parse(last);
        Assert.True(document.RootElement.GetProperty("database").GetProperty("schema").GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task Each_agent_names_the_variable_that_signs_it_in()
    {
        var report = await HostDoctor.ReportAsync(_root, TestContext.Current.CancellationToken);

        var specs = AgentAuthProbe.LoadSpecs();
        Assert.All(report.Agents, a => Assert.Equal(specs[a.Agent].CredentialVariable, a.CredentialVariable));
        Assert.Equal("ANTHROPIC_API_KEY", Assert.Single(report.Agents, a => a.Agent == "claude").CredentialVariable);
    }

    [Fact]
    public async Task Each_agent_reports_its_credential_source()
    {
        var ct = TestContext.Current.CancellationToken;

        // No database: every command is on the home, and nothing is created.
        var bare = await HostDoctor.ReportAsync(_root, ct);
        Assert.All(bare.Agents, a => Assert.Equal("home", a.CredentialSource));
        Assert.All(bare.Agents, a => Assert.Null(a.IssuedSet));
        Assert.False(File.Exists(Database));

        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct);
        await using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $$"""
                INSERT INTO tenant_settings (name, value, updated_at, updated_by)
                VALUES ('{{TenantSettings.AgentCredentialSourceName}}', '{"claude-headless":"issued","grok-headless":"issued","codex":"home"}', '2026-10-02T00:00:00Z', 'person@example.test');
                INSERT INTO agent_credentials (command, kind, value_protected, set_by, set_at)
                VALUES ('claude', 'apiKey', 'not-a-value-only-ciphertext-would-be-here', 'person@example.test', '2026-10-02T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        var report = await HostDoctor.ReportAsync(_root, ct);
        var claude = Assert.Single(report.Agents, a => a.Agent == "claude");
        var grok = Assert.Single(report.Agents, a => a.Agent == "grok");
        var codex = Assert.Single(report.Agents, a => a.Agent == "codex");

        Assert.Equal(("issued", (bool?)true), (claude.CredentialSource, claude.IssuedSet));
        Assert.Equal(("issued", (bool?)false), (grok.CredentialSource, grok.IssuedSet));
        Assert.Equal(("home", (bool?)null), (codex.CredentialSource, codex.IssuedSet));

        using var json = JsonDocument.Parse(HostDoctor.ToJson(report));
        var first = json.RootElement.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("agent").GetString() == "claude");
        Assert.Equal("issued", first.GetProperty("credentialSource").GetString());
        Assert.True(first.GetProperty("issuedSet").GetBoolean());
    }

    [Fact]
    public async Task The_json_is_one_line_in_camel_case()
    {
        var report = await HostDoctor.ReportAsync(_root, TestContext.Current.CancellationToken);

        var json = HostDoctor.ToJson(report);

        Assert.DoesNotContain('\n', json);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("dataRoot", out var dataRoot));
        Assert.Equal(_root, dataRoot.GetProperty("path").GetString());
        Assert.False(document.RootElement.GetProperty("database").GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task With_nothing_recorded_launch_and_wip_are_null_never_ok()
    {
        var report = await HostDoctor.ReportAsync(_root, TestContext.Current.CancellationToken);

        Assert.All(report.Agents, a => Assert.Null(a.Launch));
        Assert.Null(report.Wip);

        using var document = JsonDocument.Parse(HostDoctor.ToJson(report));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("wip").ValueKind);
        Assert.All(document.RootElement.GetProperty("agents").EnumerateArray(),
            a => Assert.Equal(JsonValueKind.Null, a.GetProperty("launch").ValueKind));
    }

    [Fact]
    public async Task Each_agent_carries_the_hosts_recorded_launch_check_and_a_failed_preset_is_its_answer()
    {
        var commands = AgentAuthProbe.LoadSpecs().Keys.ToArray();
        var (checkedCommand, unstarted) = (commands[0], commands[1]);

        // Two presets start the same command: one starts, one aborts under the run memory limit.
        new AgentLaunchChecksRecord(DateTimeOffset.UtcNow,
        [
            new PresetLaunchCheck("starts", "/usr/local/bin/" + checkedCommand,
                new AgentLaunchReport(AgentLaunchReport.Ok, 0, null, "ran --version")),
            new PresetLaunchCheck("aborts", checkedCommand,
                new AgentLaunchReport(AgentLaunchReport.Failed, 134, "fatal: cannot reserve the heap", "exited 134 under 64 MB")),
            new PresetLaunchCheck("terminal", null, AgentLaunchReport.Unchecked("An interactive preset")),
        ]).Write(_root);

        var report = await HostDoctor.ReportAsync(_root, TestContext.Current.CancellationToken);

        var launch = Assert.Single(report.Agents, a => a.Agent == checkedCommand).Launch!;
        Assert.Equal(AgentLaunchReport.Failed, launch.Result);
        Assert.Equal(134, launch.ExitCode);
        Assert.Equal("fatal: cannot reserve the heap", launch.StderrTail);
        Assert.Equal("aborts: exited 134 under 64 MB", launch.Detail);

        // A command no preset starts was never started: not checked, never ok.
        var none = Assert.Single(report.Agents, a => a.Agent == unstarted).Launch!;
        Assert.Equal(AgentLaunchReport.NotChecked, none.Result);
        Assert.Null(none.ExitCode);

        // The contract the operator CLI reads.
        using var document = JsonDocument.Parse(HostDoctor.ToJson(report));
        var json = document.RootElement.GetProperty("agents").EnumerateArray()
            .Single(a => a.GetProperty("agent").GetString() == checkedCommand).GetProperty("launch");
        Assert.Equal(["result", "exitCode", "stderrTail", "detail"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("failed", json.GetProperty("result").GetString());
        Assert.Equal(134, json.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Wip_is_the_limit_and_run_memory_the_host_recorded_from_its_own_decision()
    {
        var settings = new TenantSettings(null!, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Wip:MaxRunning"] = "4",
        }).Build(), cpuCount: 8, memoryLimitMb: 8192);
        var memory = RunMemoryLimits.Decide(new CgroupFacts("/sys/fs/cgroup/app", "writable"), null, settings.RunMemoryLimit);

        WipRecord.Keep(_root, settings, memory);
        var report = await HostDoctor.ReportAsync(_root, TestContext.Current.CancellationToken);

        Assert.NotNull(report.Wip);
        Assert.Equal(settings.RunLimit(), report.Wip.Limit);
        Assert.Equal(memory.Report(), report.Wip.RunMemory);
        Assert.Equal(new RunMemoryReport(RunMemoryReport.Cgroup, (8192 - 1024) / 4, memory.Report().Detail), report.Wip.RunMemory);

        using var document = JsonDocument.Parse(HostDoctor.ToJson(report));
        var wip = document.RootElement.GetProperty("wip");
        Assert.Equal(
            ["limit", "bound", "cpuBound", "cpus", "memoryBound", "memoryLimitMb", "memoryPerRunMb", "reason"],
            wip.GetProperty("limit").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["mechanism", "perRunMb", "detail"], wip.GetProperty("runMemory").EnumerateObject().Select(p => p.Name));
        Assert.Equal("cgroup", wip.GetProperty("runMemory").GetProperty("mechanism").GetString());
        Assert.Equal(1792, wip.GetProperty("runMemory").GetProperty("perRunMb").GetInt32());
    }
}
