using System.Data.Common;
using System.Globalization;
using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Messaging;

/// <summary>
/// The pending wakes on SQL Server: <c>SqliteWakeStore</c> statement for statement over the
/// <c>wakes</c> table of <see cref="SqlServerMessagingSchema"/>, whose team and member columns compare
/// without case as SQLite's do. Rows are read, written and stamped by <see cref="WakeRows"/>, as
/// SQLite's are. The <c>...WithinAsync</c> writes take the connection and transaction
/// <see cref="SqlServerMessageStore.AppendWithinAsync"/> hands its callbacks.
/// </summary>
public sealed class SqlServerWakeStore(string connectionString) : IWakeStore
{
    private const string Columns = WakeRows.Columns;

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task<IReadOnlyList<PendingWake>> AllAsync(CancellationToken ct = default) =>
        ReadAsync($"SELECT {Columns} FROM wakes ORDER BY due_at, id", _ => { }, ct);

    public Task<IReadOnlyList<PendingWake>> ForTeamAsync(string team, CancellationToken ct = default) =>
        ReadAsync(
            $"SELECT {Columns} FROM wakes WHERE team = @team ORDER BY due_at, id",
            command => WakeRows.Add(command, "@team", team),
            ct);

    public Task<IReadOnlyList<PendingWake>> ForWorkflowAsync(long workflow, CancellationToken ct = default) =>
        ReadAsync(
            $"SELECT {Columns} FROM wakes WHERE workflow = @workflow ORDER BY due_at, id",
            command => WakeRows.Add(command, "@workflow", workflow),
            ct);

    public async Task<PendingWake?> FindAsync(long id, CancellationToken ct = default) =>
        (await ReadAsync(
            $"SELECT {Columns} FROM wakes WHERE id = @id",
            command => WakeRows.Add(command, "@id", id),
            ct)).FirstOrDefault();

    public Task<IReadOnlyList<PendingWake>> DueAsync(DateTimeOffset now, CancellationToken ct = default) =>
        ReadAsync(
            $"SELECT {Columns} FROM wakes WHERE due_at <= @now ORDER BY due_at, id",
            command => WakeRows.Add(command, "@now", WakeRows.Instant(now)),
            ct);

    public Task<DateTimeOffset?> EarliestDueAsync(CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT MIN(due_at) FROM wakes";
                return await command.ExecuteScalarAsync(token) is string text ? WakeRows.Parse(text) : (DateTimeOffset?)null;
            },
            ct);

    public Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM wakes WHERE team = @team";
                WakeRows.Add(command, "@team", team);
                return await command.ExecuteNonQueryAsync(token);
            },
            ct);

    public async Task<IReadOnlyList<PendingWake>> ForMemberWithinAsync(
        DbConnection connection, DbTransaction transaction, ContainerId member, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM wakes WHERE team = @team AND member = @member ORDER BY due_at, id";
        WakeRows.Add(command, "@team", member.Team);
        WakeRows.Add(command, "@member", member.Name);

        return await WakeRows.ReadAllAsync(command, ct);
    }

    public async Task<PendingWake> InsertWithinAsync(
        DbConnection connection, DbTransaction transaction, PendingWake wake, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO wakes (team, member, workflow, asked_seq, asked_at, due_at, note)
            OUTPUT inserted.id
            VALUES (@team, @member, @workflow, @asked, @askedAt, @dueAt, @note)
            """;
        WakeRows.Add(command, "@team", wake.Team);
        WakeRows.Add(command, "@member", wake.Member);
        WakeRows.Add(command, "@workflow", wake.Workflow);
        WakeRows.Add(command, "@asked", wake.AskedSeq);
        WakeRows.Add(command, "@askedAt", WakeRows.Instant(wake.AskedAt));
        WakeRows.Add(command, "@dueAt", WakeRows.Instant(wake.DueAt));
        WakeRows.Add(command, "@note", wake.Note);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return wake with { Id = id };
    }

    public async Task SetAskedWithinAsync(
        DbConnection connection, DbTransaction transaction, long id, long askedSeq, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE wakes SET asked_seq = @asked WHERE id = @id";
        WakeRows.Add(command, "@asked", askedSeq);
        WakeRows.Add(command, "@id", id);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// ONE STATEMENT, so a wake is taken once: the delete locks the row, and a second taker waits for
    /// the first to commit and then finds nothing - SQLite's <c>DELETE ... RETURNING</c>.
    /// </summary>
    public async Task<PendingWake?> TakeWithinAsync(
        DbConnection connection, DbTransaction transaction, long id, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"DELETE FROM wakes OUTPUT {string.Join(", ", Columns.Split(", ").Select(c => "deleted." + c))} WHERE id = @id";
        WakeRows.Add(command, "@id", id);

        return (await WakeRows.ReadAllAsync(command, ct)).FirstOrDefault();
    }

    private Task<IReadOnlyList<PendingWake>> ReadAsync(string sql, Action<DbCommand> bind, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind(command);
                return await WakeRows.ReadAllAsync(command, token);
            },
            ct);

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);
}
