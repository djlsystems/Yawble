using Harness.Contracts;
using Harness.Messaging;
using Harness.SqlServer;
using Microsoft.Data.SqlClient;

namespace Harness.Tests;

/// <summary>
/// THE SQL SERVER MIGRATOR KEEPS THE SQLITE ONE'S PROMISES: each step runs once, with its record, or
/// not at all; a database from a newer build is refused before anything moves. It adds one of its
/// own - the database must compare names by ordinal, because the SQLite stores do.
/// </summary>
public sealed class SqlServerMigratorTests
{
    private static readonly MigrationStep A = new("test.a", "CREATE TABLE t1 (x INT)");
    private static readonly MigrationStep B = new("test.b", "CREATE TABLE t3 (x INT)");

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = @name";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) > 0;
    }

    [Fact]
    public async Task Steps_apply_once_and_are_recorded()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken, applyModules: false);
        var migrator = new SqlServerMigrator(database.ConnectionString);

        Assert.Null(await migrator.ApplyAsync([A, B], TestContext.Current.CancellationToken));
        // CREATE TABLE t1 would throw if it ran again.
        Assert.Null(await migrator.ApplyAsync([A, B], TestContext.Current.CancellationToken));

        Assert.Equal(["test.a", "test.b"], await migrator.AppliedIfAnyAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>What the Host reads to say, once, that the schema moved: the count of steps the last
    /// apply ran, none on a start with nothing pending.</summary>
    [Fact]
    public async Task Applied_counts_the_steps_the_last_apply_ran()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken, applyModules: false);
        var migrator = new SqlServerMigrator(database.ConnectionString);

        await migrator.ApplyAsync([A], TestContext.Current.CancellationToken);
        Assert.Equal(1, migrator.Applied);

        await migrator.ApplyAsync([A, B], TestContext.Current.CancellationToken);
        Assert.Equal(1, migrator.Applied);

        await migrator.ApplyAsync([A, B], TestContext.Current.CancellationToken);
        Assert.Equal(0, migrator.Applied);
    }

    /// <summary>
    /// TWO APPLIES AT ONCE ON A FRESH DATABASE - a Host start and an operator command - both succeed and
    /// every step runs once: the second waits for the first's migrate lock and then finds the steps
    /// applied. Without it both read an empty ledger, and the second's step or record failed on the
    /// first's.
    /// </summary>
    [Fact]
    public async Task Two_applies_at_once_on_a_fresh_database_each_run_every_step_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqlServerTestDatabase.CreateAsync(ct, applyModules: false);
        MigrationStep slow = new("test.slow", "WAITFOR DELAY '00:00:02'; CREATE TABLE slow_t (x INT);");

        var first = new SqlServerMigrator(database.ConnectionString);
        var second = new SqlServerMigrator(database.ConnectionString);

        var running = first.ApplyAsync([slow, A], ct);
        await Task.Delay(500, ct);
        await second.ApplyAsync([slow, A], ct);
        await running;

        Assert.Equal(2, first.Applied + second.Applied);
        Assert.Equal(["test.a", "test.slow"], await first.AppliedIfAnyAsync(ct));
    }

    [Fact]
    public async Task A_failed_step_rolls_back_and_is_not_recorded()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken, applyModules: false);
        var migrator = new SqlServerMigrator(database.ConnectionString);
        var bad = new MigrationStep("test.bad", "CREATE TABLE t2 (x INT); SELECT 1/0");

        var failure = await Assert.ThrowsAsync<MigrationFailedException>(() => migrator.ApplyAsync([A, bad], TestContext.Current.CancellationToken));

        Assert.Equal("test.bad", failure.StepId);
        Assert.False(await TableExistsAsync(database.ConnectionString, "t2"));
        Assert.Equal(["test.a"], await migrator.AppliedIfAnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_steps_refuse_to_start()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken, applyModules: false);
        var migrator = new SqlServerMigrator(database.ConnectionString);
        await migrator.ApplyAsync([A, B], TestContext.Current.CancellationToken);

        var refusal = await Assert.ThrowsAsync<SchemaFromTheFutureException>(() => migrator.ApplyAsync([A], TestContext.Current.CancellationToken));

        Assert.Equal(["test.b"], refusal.UnknownSteps);
    }

    [Fact]
    public async Task A_step_whose_probe_answers_non_zero_is_recorded_without_running()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken, applyModules: false);
        var migrator = new SqlServerMigrator(database.ConnectionString);
        var skipped = new MigrationStep("test.skip", "CREATE TABLE never (x INT)", SkipWhen: "SELECT 1");

        await migrator.ApplyAsync([skipped], TestContext.Current.CancellationToken);

        Assert.False(await TableExistsAsync(database.ConnectionString, "never"));
        Assert.Equal(["test.skip"], await migrator.AppliedIfAnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_case_insensitive_database_is_refused()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            TestContext.Current.CancellationToken, collation: "SQL_Latin1_General_CP1_CI_AS", applyModules: false);

        var refusal = await Assert.ThrowsAsync<WrongCollationException>(
            () => new SqlServerMigrator(database.ConnectionString).ApplyAsync([A], TestContext.Current.CancellationToken));

        Assert.Contains("SQL_Latin1_General_CP1_CI_AS", refusal.Message);
        Assert.False(await TableExistsAsync(database.ConnectionString, "schema_steps"));
    }

    [Fact]
    public async Task A_database_without_read_committed_snapshot_is_refused()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            TestContext.Current.CancellationToken, applyModules: false, readCommittedSnapshot: false);

        var refusal = await Assert.ThrowsAsync<ReadCommittedSnapshotOffException>(
            () => new SqlServerMigrator(database.ConnectionString).ApplyAsync([A], TestContext.Current.CancellationToken));

        Assert.Contains("READ_COMMITTED_SNAPSHOT", refusal.Message);
        Assert.Contains($"ALTER DATABASE [{database.Name}] SET READ_COMMITTED_SNAPSHOT ON", refusal.Message);
        Assert.False(await TableExistsAsync(database.ConnectionString, "schema_steps"));
        Assert.False(await TableExistsAsync(database.ConnectionString, "t1"));
    }

    [Fact]
    public async Task Two_names_differing_only_in_case_are_two_rows()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE names (name NVARCHAR(450) PRIMARY KEY);
            INSERT INTO names (name) VALUES (N'Alpha'), (N'alpha');
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        command.CommandText = "SELECT COUNT(*) FROM names";
        Assert.Equal(2, Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void The_connection_string_gains_retry_and_pooling_unless_configured()
    {
        var plain = SqlServerOpen.ConnectionString("Server=x;Database=y");
        Assert.Contains("Connect Retry Count=3", plain);
        Assert.Contains("Pooling=True", plain);

        var own = SqlServerOpen.ConnectionString("Server=x;Database=y;ConnectRetryCount=7;Pooling=False");
        Assert.Contains("Connect Retry Count=7", own);
        Assert.Contains("Pooling=False", own);
    }

    [Fact]
    public async Task A_deadlock_victim_is_retried_and_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqlServerTestDatabase.CreateAsync(ct);
        await Execute(database.ConnectionString,
            "CREATE TABLE d1 (x INT); INSERT INTO d1 VALUES (0); CREATE TABLE d2 (x INT); INSERT INTO d2 VALUES (0);", ct);

        var aHolds = new TaskCompletionSource();
        var bHolds = new TaskCompletionSource();
        var attempts = 0;

        // A locks d1 then wants d2. On its first attempt it waits until B holds d2, so the cycle is
        // certain; on a retry nothing is waited for.
        var a = SqlServerOpen.RetryAsync<int>(async token =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
            await Run(connection, transaction, "UPDATE d1 SET x = x + 1", token);
            if (attempt == 1)
            {
                aHolds.SetResult();
                await bHolds.Task.WaitAsync(token);
            }
            await Run(connection, transaction, "UPDATE d2 SET x = x + 1", token);
            await transaction.CommitAsync(token);
            return attempt;
        }, ct);

        // B locks d2 then wants d1, and has the higher deadlock priority so A is the victim.
        var b = Task.Run(async () =>
        {
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(ct);
            await Execute(connection, "SET DEADLOCK_PRIORITY HIGH", ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            await Run(connection, transaction, "UPDATE d2 SET x = x + 10", ct);
            bHolds.SetResult();
            await aHolds.Task.WaitAsync(ct);
            await Run(connection, transaction, "UPDATE d1 SET x = x + 10", ct);
            await transaction.CommitAsync(ct);
        }, ct);

        await b;
        Assert.Equal(2, await a);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_fault_that_is_not_retryable_is_thrown_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqlServerTestDatabase.CreateAsync(ct, applyModules: false);
        var calls = 0;

        var failure = await Assert.ThrowsAsync<SqlException>(() => SqlServerOpen.RetryAsync<int>(async token =>
        {
            calls++;
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1/0";
            return Convert.ToInt32(await command.ExecuteScalarAsync(token));
        }, ct));

        Assert.Equal(8134, failure.Number);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_retryable_fault_is_attempted_three_times_and_then_thrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;

        await Assert.ThrowsAsync<TimeoutException>(() => SqlServerOpen.RetryAsync<int>(
            _ => { calls++; throw new TimeoutException(); },
            ct, attempts: 3, retryable: (e, _) => e is TimeoutException, delayStep: TimeSpan.Zero));

        Assert.Equal(3, calls);
    }

    private static async Task Execute(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await Execute(connection, sql, ct);
    }

    private static async Task Execute(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task Run(SqlConnection connection, SqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
