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

        Assert.Equal(["test.a", "test.b"], await migrator.AppliedAsync(TestContext.Current.CancellationToken));
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
        Assert.Equal(["test.a"], await migrator.AppliedAsync(TestContext.Current.CancellationToken));
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
        Assert.Equal(["test.skip"], await migrator.AppliedAsync(TestContext.Current.CancellationToken));
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
}
