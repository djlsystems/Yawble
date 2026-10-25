using System.Text.Json;
using Harness.Host;
using Harness.SqlServer;
using Harness.SqlServer.Host;
using Harness.SqlServer.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.Tests;

/// <summary>
/// THE OPERATOR SWITCHES ON SQL SERVER. Each one either does its job through the store interfaces or
/// refuses with one sentence, and none of them creates, opens or migrates a <c>messages.db</c>:
/// <list type="bullet">
/// <item><c>--backup</c> and <c>--restore</c> refuse, naming point-in-time restore: the database is not a
/// file this Host can copy or replace, and the server's own restore is the backup.</item>
/// <item><c>--reset-password</c> and <c>--list-users</c> move the SQL Server schema forward and use the
/// SQL Server user store.</item>
/// <item><c>--doctor</c> reports <c>database.provider: sqlserver</c>, the server and database names, the
/// applied steps and no backup files, writes nothing, and never prints the connection string.</item>
/// <item><c>--solution-check</c> reads no database on either provider.</item>
/// </list>
/// </summary>
public sealed class SqlServerOperatorCommandTests : IDisposable
{
    private const string Email = "operator@example.test";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-sqlserver-operator-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Backup_and_restore_on_sqlserver_say_point_in_time_restore()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct);
        var options = new DatabaseOptions(DatabaseProvider.SqlServer, database.ConnectionString);

        // --backup, with and without a destination: refused, one sentence, nothing written anywhere.
        var destination = Path.Combine(_root, "copy.db");
        foreach (var args in new[] { new[] { "--backup" }, ["--backup", destination] })
        {
            var (outcome, lines) = await RunAsync(args, options);
            Assert.Equal(OperatorOutcome.Refused, outcome);
            var line = Assert.Single(lines);
            Assert.EndsWith(SqlServerDatabaseMaintenance.PointInTimeRestore, line, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(destination));

        // --restore, of a file that exists and with no file at all: refused the same way, the file untouched.
        var backup = Path.Combine(_root, "messages-20261001-000000-manual.db");
        await File.WriteAllTextAsync(backup, "a copy", Ct);
        foreach (var args in new[] { new[] { "--restore", backup }, ["--restore"] })
        {
            var (outcome, lines) = await RunAsync(args, options);
            Assert.Equal(OperatorOutcome.Refused, outcome);
            var line = Assert.Single(lines);
            Assert.EndsWith(SqlServerDatabaseMaintenance.PointInTimeRestore, line, StringComparison.Ordinal);
        }

        Assert.Equal("a copy", await File.ReadAllTextAsync(backup, Ct));
        Assert.Equal([backup], Directory.GetFiles(_root));

        // --reset-password works, on the SQL Server account: the old password stops, the printed one signs in.
        var users = new SqlServerUserStore(database.ConnectionString);
        await users.CreateAsync(Email, "the old password", Ct);

        var (reset, said) = await RunAsync(["--reset-password", Email], options);
        Assert.Equal(OperatorOutcome.Completed, reset);
        Assert.Equal($"The password for {Email} is now:", said[^2]);
        Assert.NotNull(await users.VerifyAsync(Email, said[^1], Ct));
        Assert.Null(await users.VerifyAsync(Email, "the old password", Ct));

        Assert.False(HasSqliteFile);
    }

    [Fact]
    public async Task Reset_password_and_list_users_move_an_empty_sqlserver_database_forward_first()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);
        var options = new DatabaseOptions(DatabaseProvider.SqlServer, database.ConnectionString);

        // The first command to reach the empty database migrates it, saying once that no copy was taken.
        var (listed, lines) = await RunAsync(["--list-users"], options);
        Assert.Equal(OperatorOutcome.Completed, listed);
        Assert.Equal(
            [
                $"Schema updated. No backup was taken first: {SqlServerDatabaseMaintenance.PointInTimeRestore}",
                "No accounts. This instance has not been bootstrapped.",
            ],
            lines);
        Assert.Equal(
            SqlServerSchemaModules.All.Select(s => s.Id).Order(StringComparer.Ordinal),
            await new SqlServerMigrator(database.ConnectionString).AppliedAsync(Ct));

        // A reset for an account that is not there names the database, not a data root.
        var (missing, said) = await RunAsync(["--reset-password", Email], options);
        Assert.Equal(OperatorOutcome.Refused, missing);
        Assert.Equal(
            [
                $"There is no account for '{Email}' - this database has no accounts at all.",
                "That usually means the wrong database, not a missing account. Check HARNESS_DATABASE_CONNECTION.",
            ],
            said);

        // With accounts: listed by email, and a reset for a missing one lists them.
        var users = new SqlServerUserStore(database.ConnectionString);
        await users.CreateAsync("b@example.test", "pw-b-long-enough", Ct);
        await users.CreateAsync("a@example.test", "pw-a-long-enough", Ct);

        Assert.Equal(["a@example.test", "b@example.test"], (await RunAsync(["--list-users"], options)).Lines);
        var (refused, named) = await RunAsync(["--reset-password", Email], options);
        Assert.Equal(OperatorOutcome.Refused, refused);
        Assert.Equal($"There is no account for '{Email}'. The accounts here are:", named[0]);
        Assert.Equal(["  a@example.test", "  b@example.test"], named[1..].Order(StringComparer.Ordinal));

        Assert.False(HasSqliteFile);
    }

    [Fact]
    public async Task A_sqlserver_database_the_host_would_refuse_is_one_sentence_naming_no_secret()
    {
        // The wrong collation: the migrator's own sentence, and the database is left as found.
        await using (var database = await SqlServerTestDatabase.CreateAsync(Ct, collation: "SQL_Latin1_General_CP1_CI_AS", applyModules: false))
        {
            var (outcome, lines) = await RunAsync(["--list-users"], new DatabaseOptions(DatabaseProvider.SqlServer, database.ConnectionString));
            Assert.Equal(OperatorOutcome.Refused, outcome);
            Assert.Contains($"COLLATE {SqlServerMigrator.RequiredCollation}", Assert.Single(lines), StringComparison.Ordinal);
        }

        // A server that is not there: named, the password never.
        var secret = $"operator-secret-{Guid.NewGuid():N}";
        var unreachable = $"Server=harness-no-such-server.invalid;Database=harness;User ID=someone;Password={secret};Connect Timeout=3;ConnectRetryCount=0;Encrypt=False";
        var (refused, said) = await RunAsync(["--reset-password", Email], new DatabaseOptions(DatabaseProvider.SqlServer, unreachable));
        Assert.Equal(OperatorOutcome.Refused, refused);
        var sentence = Assert.Single(said);
        Assert.Contains("harness-no-such-server.invalid", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, sentence, StringComparison.Ordinal);

        Assert.False(HasSqliteFile);
    }

    [Fact]
    public async Task Solution_check_on_sqlserver_reads_no_database()
    {
        // A server that cannot be reached: a command that opened the database would fail or wait on it.
        var options = new DatabaseOptions(DatabaseProvider.SqlServer, "Server=harness-no-such-server.invalid;Database=harness;Connect Timeout=1;ConnectRetryCount=0");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "package")).FullName;

        var (outcome, lines) = await RunAsync(["--solution-check", folder], options);

        Assert.Equal(OperatorOutcome.Completed, outcome);
        using var _ = JsonDocument.Parse(lines[^1]);
        Assert.False(HasSqliteFile);
    }

    [Fact]
    public async Task Doctor_names_the_provider()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct);
        var options = new DatabaseOptions(DatabaseProvider.SqlServer, database.ConnectionString);
        var builder = new SqlConnectionStringBuilder(database.ConnectionString);

        var (outcome, lines) = await RunAsync(["--doctor"], options);
        Assert.Equal(OperatorOutcome.Completed, outcome);

        using var json = JsonDocument.Parse(lines[^1]);
        var reported = json.RootElement.GetProperty("database");
        Assert.Equal("sqlserver", reported.GetProperty("provider").GetString());
        Assert.Equal(builder.DataSource, reported.GetProperty("server").GetString());
        Assert.Equal(database.Name, reported.GetProperty("name").GetString());
        Assert.True(reported.GetProperty("exists").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("error").ValueKind);

        var schema = reported.GetProperty("schema");
        Assert.Equal(
            SqlServerSchemaModules.All.Select(s => s.Id).Order(StringComparer.Ordinal),
            schema.GetProperty("applied").EnumerateArray().Select(e => e.GetString()!));
        Assert.Empty(schema.GetProperty("pending").EnumerateArray());
        Assert.Empty(schema.GetProperty("unknown").EnumerateArray());
        Assert.True(schema.GetProperty("accepted").GetBoolean());

        // NO BACKUP-FILE ROWS: the server keeps the backups, so there is no folder of copies to count.
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("backups").ValueKind);

        Assert.False(HasSqliteFile);
    }

    [Fact]
    public async Task The_doctor_never_prints_the_connection_string_or_its_password()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct);
        var password = new SqlConnectionStringBuilder(database.ConnectionString).Password;

        var (_, lines) = await RunAsync(["--doctor"], new DatabaseOptions(DatabaseProvider.SqlServer, database.ConnectionString));
        var text = string.Join('\n', lines);
        Assert.DoesNotContain(database.ConnectionString, text, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(password)) Assert.DoesNotContain(password, text, StringComparison.Ordinal);

        // A database it cannot reach is the report's error, naming the server and never the password.
        var secret = $"doctor-secret-{Guid.NewGuid():N}";
        var unreachable = $"Server=harness-no-such-server.invalid;Database=harness;User ID=someone;Password={secret};Connect Timeout=3;ConnectRetryCount=0;Encrypt=False";
        var (outcome, failed) = await RunAsync(["--doctor"], new DatabaseOptions(DatabaseProvider.SqlServer, unreachable));
        Assert.Equal(OperatorOutcome.Completed, outcome);
        Assert.DoesNotContain(secret, string.Join('\n', failed), StringComparison.Ordinal);

        using var json = JsonDocument.Parse(failed[^1]);
        var reported = json.RootElement.GetProperty("database");
        Assert.Equal("harness-no-such-server.invalid", reported.GetProperty("server").GetString());
        Assert.Equal("harness", reported.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("schema").ValueKind);
        Assert.Contains("harness-no-such-server.invalid", reported.GetProperty("error").GetString(), StringComparison.Ordinal);

        // A string SqlClient cannot read at all names no value from it either.
        var (_, unreadable) = await RunAsync(["--doctor"], new DatabaseOptions(DatabaseProvider.SqlServer, $"Server=x;Colour={secret}"));
        Assert.DoesNotContain(secret, string.Join('\n', unreadable), StringComparison.Ordinal);

        Assert.False(HasSqliteFile);
    }

    [Fact]
    public async Task The_doctor_on_an_empty_sqlserver_database_says_every_step_is_pending_and_writes_nothing()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);

        var report = await HostDoctor.ReportAsync(_root, Ct, database: new DatabaseOptions(DatabaseProvider.SqlServer, database.ConnectionString));

        Assert.Equal("sqlserver", report.Database.Provider);
        var schema = Assert.IsType<DoctorSchema>(report.Database.Schema);
        Assert.Empty(schema.Applied);
        Assert.Equal(SqlServerSchemaModules.All.Select(s => s.Id), schema.Pending);
        Assert.True(schema.Accepted);
        Assert.Null(report.BackupFiles);

        // Read, never written: not even the migrator's ledger was created.
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables";
        Assert.Equal(0, (int)(await command.ExecuteScalarAsync(Ct))!);

        Assert.False(HasSqliteFile);
    }

    [Fact]
    public async Task The_doctor_on_sqlite_reports_as_it_always_has()
    {
        // No provider, server or name on SQLite: the report the operator CLI has always read, byte for byte.
        var (outcome, lines) = await RunAsync(["--doctor"], new DatabaseOptions(DatabaseProvider.Sqlite, null));
        Assert.Equal(OperatorOutcome.Completed, outcome);

        using var json = JsonDocument.Parse(lines[^1]);
        Assert.Equal(
            ["path", "exists", "schema", "error"],
            json.RootElement.GetProperty("database").EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["directory", "dailyCount", "newestDailyAt"],
            json.RootElement.GetProperty("backups").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task The_host_process_hands_its_operator_switches_the_provider()
    {
        // Through Program.cs, as an operator runs it: the Host reads HARNESS_DATABASE and the switches
        // act on SQL Server, not on a messages.db beside the data root.
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct);

        var (backupExit, backupLines, _) = await RunHostAsync(database.ConnectionString, "--backup");
        Assert.Equal(1, backupExit);
        Assert.Contains($"No copy was written: {SqlServerDatabaseMaintenance.PointInTimeRestore}", backupLines);

        var (doctorExit, doctorLines, doctorErrors) = await RunHostAsync(database.ConnectionString, "--doctor");
        Assert.True(doctorExit == 0, doctorErrors);
        using var json = JsonDocument.Parse(doctorLines[^1]);
        Assert.Equal("sqlserver", json.RootElement.GetProperty("database").GetProperty("provider").GetString());

        Assert.False(HasSqliteFile);
    }

    private async Task<(int ExitCode, string[] Lines, string Stderr)> RunHostAsync(string connection, string operatorSwitch)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Harness.Host.dll"));
        start.ArgumentList.Add("--DataRoot=" + _root);
        start.ArgumentList.Add(operatorSwitch);
        start.Environment.Remove("HARNESS_DATA_ROOT");
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["HARNESS_DATABASE"] = "sqlserver";
        start.Environment["HARNESS_DATABASE_CONNECTION"] = connection;

        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"The Host kept running on {operatorSwitch} instead of answering and exiting.");
        }

        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
        return (process.ExitCode, lines, await stderr);
    }

    private bool HasSqliteFile => Directory.GetFiles(_root, "messages.db*", SearchOption.AllDirectories).Length > 0;

    private async Task<(OperatorOutcome Outcome, string[] Lines)> RunAsync(string[] args, DatabaseOptions options)
    {
        using var output = new StringWriter();
        var outcome = await OperatorCommands.TryRunAsync(args, _root, output, Ct, database: options);
        return (outcome, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
