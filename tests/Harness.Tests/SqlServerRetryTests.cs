using System.Reflection;
using Harness.Contracts;
using Harness.SqlServer;
using Harness.SqlServer.Ledger;
using Harness.SqlServer.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.Tests;

/// <summary>
/// WHAT A SQL SERVER STORE RUNS AGAIN, AND WHEN IT MUST NOT. The production predicate is read with
/// SqlExceptions built here (SqlClient has no public constructor for one), and the commit boundary
/// with the production policy, so a list or a rule changed in <see cref="SqlServerOpen"/> fails here.
/// </summary>
public sealed class SqlServerRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1205)]
    [InlineData(40613)]
    [InlineData(40197)]
    [InlineData(40501)]
    [InlineData(49918)]
    [InlineData(10928)]
    [InlineData(10929)]
    [InlineData(4060)]
    [InlineData(4221)]
    [InlineData(233)]
    [InlineData(64)]
    [InlineData(10053)]
    [InlineData(10054)]
    [InlineData(10060)]
    [InlineData(SqlServerOpen.LockDeadlockVictim)]
    public void A_deadlock_victim_and_each_documented_transient_fault_are_retryable(int number)
    {
        Assert.True(SqlServerOpen.IsRetryable(Fault(number)), $"{number} should be retryable");
    }

    [Theory]
    [InlineData(8134)]
    [InlineData(2627)]
    [InlineData(-2)]
    [InlineData(1222)]
    [InlineData(51000)]
    public void A_fault_that_is_not_transient_is_not_retryable(int number)
    {
        Assert.False(SqlServerOpen.IsRetryable(Fault(number)), $"{number} should not be retryable");
        Assert.False(SqlServerOpen.IsRetryable(new TimeoutException()));
    }

    /// <summary>
    /// THE COMMIT BOUNDARY, on the production policy: a deadlock victim is retried wherever it
    /// happens; a transient fault only before the connection opened or inside a transaction whose
    /// commit was not sent.
    /// </summary>
    [Fact]
    public void A_transient_fault_is_retried_only_where_nothing_can_have_committed()
    {
        var transient = Fault(40613);
        var deadlock = Fault(1205);

        Assert.True(SqlServerOpen.Retries(transient, SqlServerOpen.Sent.Nothing));
        Assert.True(SqlServerOpen.Retries(transient, SqlServerOpen.Sent.Transaction));
        Assert.False(SqlServerOpen.Retries(transient, SqlServerOpen.Sent.Statements));
        Assert.False(SqlServerOpen.Retries(transient, SqlServerOpen.Sent.Commit));

        foreach (var sent in Enum.GetValues<SqlServerOpen.Sent>())
        {
            Assert.True(SqlServerOpen.Retries(deadlock, sent), sent.ToString());
            Assert.False(SqlServerOpen.Retries(Fault(8134), sent), sent.ToString());
        }
    }

    /// <summary>An application lock's deadlock victim that outlasts the retries is recorded as the
    /// busy database, as a 1205 is.</summary>
    [Fact]
    public void A_lock_deadlock_victim_is_the_busy_database_like_a_deadlock()
    {
        var trouble = new SqlServerTrouble();

        Assert.Equal(Harness.Contracts.DiagnosticKinds.DatabaseBusy, trouble.KindOf(Fault(SqlServerOpen.LockDeadlockVictim)));
        Assert.Equal(Harness.Contracts.DiagnosticKinds.DatabaseBusy, trouble.KindOf(Fault(1205)));
        Assert.Null(trouble.KindOf(Fault(51000)));
    }

    /// <summary>
    /// A TRANSIENT FAULT ON THE COMMIT IS THROWN, NOT RUN AGAIN: the commit landed (the row is there)
    /// and its answer was lost, which a retry would turn into a second row.
    /// </summary>
    [Fact]
    public async Task A_transient_fault_on_the_commit_is_thrown_and_the_unit_is_not_run_again()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);
        await Execute(database.ConnectionString, "CREATE TABLE appended (x INT NOT NULL)");
        var calls = 0;

        var thrown = await Assert.ThrowsAsync<SqlException>(() => SqlServerOpen.RunAsync<int>(
            database.ConnectionString,
            async (connection, token) =>
            {
                calls++;
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                await Run(connection, transaction, "INSERT INTO appended (x) VALUES (1)", token);
                await SqlServerOpen.CommitAsync(transaction, token);

                // The commit's answer lost in a failover: the fault arrives after the row landed.
                throw Fault(40613);
            },
            Ct));

        Assert.Equal(40613, thrown.Number);
        Assert.Equal(1, calls);
        Assert.Equal(1, await CountAsync(database.ConnectionString));
    }

    /// <summary>A transient fault inside a transaction before its commit leaves nothing applied, so
    /// the unit runs again and lands once.</summary>
    [Fact]
    public async Task A_transient_fault_before_the_commit_runs_the_unit_again_and_it_lands_once()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);
        await Execute(database.ConnectionString, "CREATE TABLE appended (x INT NOT NULL)");
        var calls = 0;

        await SqlServerOpen.RunAsync(
            database.ConnectionString,
            async (connection, token) =>
            {
                calls++;
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                await Run(connection, transaction, "INSERT INTO appended (x) VALUES (1)", token);
                if (calls == 1) throw Fault(40613);
                await SqlServerOpen.CommitAsync(transaction, token);
                return 0;
            },
            Ct);

        Assert.Equal(2, calls);
        Assert.Equal(1, await CountAsync(database.ConnectionString));
    }

    /// <summary>A statement outside a transaction commits itself, so a transient fault after it is
    /// thrown rather than running the write again.</summary>
    [Fact]
    public async Task A_transient_fault_after_a_statement_outside_a_transaction_is_thrown()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);
        await Execute(database.ConnectionString, "CREATE TABLE appended (x INT NOT NULL)");
        var calls = 0;

        await Assert.ThrowsAsync<SqlException>(() => SqlServerOpen.RunAsync<int>(
            database.ConnectionString,
            async (connection, token) =>
            {
                calls++;
                await Run(connection, null, "INSERT INTO appended (x) VALUES (1)", token);
                throw Fault(10054);
            },
            Ct));

        Assert.Equal(1, calls);
        Assert.Equal(1, await CountAsync(database.ConnectionString));
    }

    /// <summary>
    /// WHAT <see cref="IMessageLog.AppendWithinAsync"/> WARNS ITS CALLERS OF: a deadlock victim runs the
    /// whole unit again, both callbacks with it, after rolling back what the first attempt wrote - so
    /// the message is appended once and each callback ran twice.
    /// </summary>
    [Fact]
    public async Task An_append_within_chosen_as_a_deadlock_victim_runs_its_callbacks_again_and_appends_once()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct);
        var log = new SqlServerMessageStore(database.ConnectionString, new SqlServerAppendRows());
        var befores = 0;
        var afters = 0;

        var appended = await log.AppendWithinAsync(
            (_, _, _) =>
            {
                befores++;
                return Task.FromResult<NewMessage?>(new NewMessage(MessageTypes.Progress, """{"status":"once"}""", "console"));
            },
            (_, _, _, _) => ++afters == 1 ? throw Fault(1205) : Task.CompletedTask,
            Ct);

        Assert.Equal(2, befores);
        Assert.Equal(2, afters);
        var stored = await log.ReadAfterAsync(0, [MessageTypes.Progress], 10, Ct);
        Assert.Equal(appended!.Seq, Assert.Single(stored).Seq);
    }

    /// <summary>
    /// EVERY SQL SERVER TRANSACTION BEGINS AND COMMITS THROUGH <see cref="SqlServerOpen"/>, so the
    /// retry always knows when a commit was sent. A store that called SqlClient's own would have a
    /// fault on its commit retried as a transient one.
    /// </summary>
    [Fact]
    public void Every_sql_server_transaction_begins_and_commits_through_sql_server_open()
    {
        var root = SolutionSamples.RepoRoot();
        string[] folders = [Path.Combine(root, "src", "Harness.SqlServer"), Path.Combine(root, "src", "Harness.Host", "SqlServer")];

        var offenders = folders
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && Path.GetFileName(file) != "SqlServerOpen.cs")
            .SelectMany(file => File.ReadLines(file).Select((line, at) => (file, line, at)))
            .Where(x => x.line.Contains("BeginTransactionAsync(", StringComparison.Ordinal)
                        || x.line.Contains("transaction.CommitAsync(", StringComparison.Ordinal))
            .Select(x => $"{Path.GetRelativePath(root, x.file)}:{x.at + 1}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await Run(connection, null, sql, Ct);
    }

    private static async Task Run(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> CountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM appended";
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
    }

    /// <summary>
    /// A SqlException carrying one error of <paramref name="number"/>, built the way SqlClient builds
    /// one: an internal SqlError in an internal collection, handed to the internal factory.
    /// </summary>
    internal static SqlException Fault(int number)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var errorCtor = typeof(SqlError).GetConstructors(Any)
            .OrderByDescending(c => c.GetParameters().Length)
            .First(c => c.GetParameters().FirstOrDefault()?.ParameterType == typeof(int));

        var error = (SqlError)errorCtor.Invoke(
            [.. errorCtor.GetParameters().Select(p => p.Position == 0 ? number : Default(p))]);

        var errors = (SqlErrorCollection)typeof(SqlErrorCollection).GetConstructors(Any).Single(c => c.GetParameters().Length == 0).Invoke(null);
        typeof(SqlErrorCollection).GetMethod("Add", Any, [typeof(SqlError)])!.Invoke(errors, [error]);

        var create = typeof(SqlException).GetMethods(Any)
            .Where(m => m.Name == "CreateException")
            .OrderBy(m => m.GetParameters().Length)
            .First(m => m.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));

        var exception = (SqlException)create.Invoke(null, [.. create.GetParameters().Select(p => p.Position == 0 ? errors : Default(p))])!;

        Assert.Equal(number, exception.Number);

        return exception;
    }

    private static object? Default(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        if (parameter.ParameterType == typeof(string)) return "test";
        return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
    }
}
