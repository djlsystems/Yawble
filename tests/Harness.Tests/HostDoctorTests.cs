using System.Text.Json;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

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
}
