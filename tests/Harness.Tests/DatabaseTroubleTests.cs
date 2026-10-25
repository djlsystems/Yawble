using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Harness.SqlServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A BUSY DATABASE IS RECORDED AS ONE ON EITHER PROVIDER. The request pipeline asks the provider's own
/// <see cref="IDatabaseTrouble"/>, so a SQL Server deadlock, lock timeout or command timeout that
/// escapes a request is <c>db.busy</c> or <c>db.busy-timeout-expired</c>, not <c>http.unhandled</c>,
/// and SQLite's busy codes are classified exactly as before.
/// </summary>
public sealed class DatabaseTroubleTests
{
    [Fact]
    public void Sqlite_busy_and_its_expired_timeout_are_classified_as_before()
    {
        var trouble = SqliteDatabaseTrouble.Instance;

        Assert.Equal(DiagnosticKinds.DatabaseBusy, trouble.KindOf(new SqliteException("busy", 5)));
        Assert.Equal(DiagnosticKinds.DatabaseBusy, trouble.KindOf(new SqliteException("locked", 6)));
        Assert.Equal(DiagnosticKinds.DatabaseBusyTimeoutExpired, trouble.KindOf(new SqliteException("busy", 5, 261)));
        Assert.Equal(DiagnosticKinds.DatabaseBusy, trouble.KindOf(new InvalidOperationException("wrapped", new SqliteException("busy", 5))));
        Assert.Null(trouble.KindOf(new SqliteException("constraint", 19)));
        Assert.Null(trouble.KindOf(new InvalidOperationException("not the database")));
        Assert.Null(trouble.KindOf(null));
    }

    [Fact]
    public async Task Sql_server_deadlock_lock_timeout_and_command_timeout_are_the_busy_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqlServerTestDatabase.CreateAsync(ct, applyModules: false);
        var trouble = new SqlServerTrouble();

        await ExecuteAsync(database.ConnectionString, "CREATE TABLE trouble_a (id INT PRIMARY KEY, v INT); CREATE TABLE trouble_b (id INT PRIMARY KEY, v INT); INSERT INTO trouble_a VALUES (1, 0); INSERT INTO trouble_b VALUES (1, 0);", ct);

        // A lock request that waits past LOCK_TIMEOUT: another transaction holds the row.
        await using (var holder = await OpenAsync(database.ConnectionString, ct))
        {
            await using var held = (SqlTransaction)await holder.BeginTransactionAsync(ct);
            await ExecuteAsync(holder, held, "UPDATE trouble_a SET v = 1 WHERE id = 1", ct);

            var lockTimeout = await Assert.ThrowsAsync<SqlException>(() =>
                ExecuteAsync(database.ConnectionString, "SET LOCK_TIMEOUT 100; UPDATE trouble_a SET v = 2 WHERE id = 1", ct));
            Assert.Equal(SqlServerTrouble.LockTimeout, lockTimeout.Number);
            Assert.Equal(DiagnosticKinds.DatabaseBusyTimeoutExpired, trouble.KindOf(lockTimeout));

            // A command that waits past its command timeout on the same held row.
            var commandTimeout = await Assert.ThrowsAsync<SqlException>(() =>
                ExecuteAsync(database.ConnectionString, "UPDATE trouble_a SET v = 3 WHERE id = 1", ct, timeoutSeconds: 1));
            Assert.Equal(SqlServerTrouble.CommandTimeout, commandTimeout.Number);
            Assert.Equal(DiagnosticKinds.DatabaseBusyTimeoutExpired, trouble.KindOf(commandTimeout));
        }

        // A deadlock: each transaction holds one row and asks for the other's. SQL Server chooses a victim.
        await using var first = await OpenAsync(database.ConnectionString, ct);
        await using var second = await OpenAsync(database.ConnectionString, ct);
        await using var firstTx = (SqlTransaction)await first.BeginTransactionAsync(ct);
        await using var secondTx = (SqlTransaction)await second.BeginTransactionAsync(ct);
        await ExecuteAsync(first, firstTx, "UPDATE trouble_a SET v = 10 WHERE id = 1", ct);
        await ExecuteAsync(second, secondTx, "UPDATE trouble_b SET v = 20 WHERE id = 1", ct);

        var crossings = new[]
        {
            ExecuteAsync(first, firstTx, "UPDATE trouble_b SET v = 11 WHERE id = 1", ct),
            ExecuteAsync(second, secondTx, "UPDATE trouble_a SET v = 21 WHERE id = 1", ct),
        };
        var victim = await Assert.ThrowsAsync<SqlException>(async () => await Task.WhenAll(crossings));
        Assert.Equal(SqlServerTrouble.Deadlock, victim.Number);
        Assert.Equal(DiagnosticKinds.DatabaseBusy, trouble.KindOf(victim));
        Assert.Equal(DiagnosticKinds.DatabaseBusy, trouble.KindOf(new InvalidOperationException("wrapped", victim)));

        // Not every SQL Server error is the busy database: a missing table is a bug, not contention.
        var missing = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(database.ConnectionString, "SELECT * FROM no_such_table", ct));
        Assert.Null(trouble.KindOf(missing));

        // Neither classifier claims the other engine's exception.
        Assert.Null(trouble.KindOf(new SqliteException("busy", 5)));
        Assert.Null(SqliteDatabaseTrouble.Instance.KindOf(victim));
    }

    [Fact]
    public async Task The_request_pipeline_records_what_the_registered_classifier_says()
    {
        var failure = new InvalidOperationException("the provider's busy database");

        // The provider's classifier is asked: its kind is the row's, with the database as the source.
        var classified = new RecordingLog();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PipelineThrowing(failure)(ContextFor(classified, new FixedTrouble(DiagnosticKinds.DatabaseBusy))));
        Assert.Equal([(DiagnosticKinds.DatabaseBusy, DiagnosticSources.Database)], classified.Rows);

        // None registered: SQLite's classification, as the pipeline always made it.
        var unregistered = new RecordingLog();
        await Assert.ThrowsAsync<SqliteException>(() =>
            PipelineThrowing(new SqliteException("busy", 5))(ContextFor(unregistered, trouble: null)));
        Assert.Equal([(DiagnosticKinds.DatabaseBusy, DiagnosticSources.Database)], unregistered.Rows);

        // Not the database: an unhandled exception, as before.
        var ordinary = new RecordingLog();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PipelineThrowing(failure)(ContextFor(ordinary, new FixedTrouble(null))));
        Assert.Equal([(DiagnosticKinds.HttpUnhandledException, DiagnosticSources.Http)], ordinary.Rows);
    }

    private static RequestDelegate PipelineThrowing(Exception failure)
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        DiagnosticsMiddleware.Use(app);
        app.Run(_ => throw failure);
        return app.Build();
    }

    private static DefaultHttpContext ContextFor(IDiagnosticsLog log, IDatabaseTrouble? trouble)
    {
        var services = new ServiceCollection().AddSingleton(log).AddSingleton<DiagnosticsRecorder>();
        if (trouble is not null) services.AddSingleton(trouble);

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = "GET";
        context.Request.Path = "/api/teams";
        return context;
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct, int timeoutSeconds = 30)
    {
        await using var connection = await OpenAsync(connectionString, ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class FixedTrouble(string? kind) : IDatabaseTrouble
    {
        public string? KindOf(Exception? exception) => kind;
    }

    private sealed class RecordingLog : IDiagnosticsLog
    {
        public List<(string Kind, string? Source)> Rows { get; } = [];

        public Task WriteAsync(
            DiagnosticSeverity severity, string kind, string? source = null, string? route = null, int? status = null,
            string? exceptionType = null, string? message = null, string? detail = null, CancellationToken ct = default)
        {
            Rows.Add((kind, source));
            return Task.CompletedTask;
        }

        public Task<DiagnosticsPage> ReadAsync(DiagnosticsFilter? filter = null, long? before = null, int take = 50, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool?> HasAnyAsync(CancellationToken ct = default) => Task.FromResult<bool?>(Rows.Count > 0);

        public Task TrimAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
