using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// The pending wakes (<c>wakes</c>, <c>messages-003</c>) over the log's own file, shaped like
/// <see cref="SqlitePendingDeliveries"/>. Instants are stored as round-trip UTC text of one width,
/// so ordering by the column is ordering by time.
/// </summary>
public sealed class SqliteWakeStore(string databasePath, SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    : IWakeStore
{
    private const string Columns = "id, team, member, workflow, asked_seq, asked_at, due_at, note";

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    public Task<IReadOnlyList<PendingWake>> AllAsync(CancellationToken ct = default) =>
        ReadAsync($"SELECT {Columns} FROM wakes ORDER BY due_at, id", _ => { }, ct);

    public Task<IReadOnlyList<PendingWake>> ForTeamAsync(string team, CancellationToken ct = default) =>
        ReadAsync(
            $"SELECT {Columns} FROM wakes WHERE team = $team ORDER BY due_at, id",
            command => command.Parameters.AddWithValue("$team", team),
            ct);

    public Task<IReadOnlyList<PendingWake>> ForWorkflowAsync(long workflow, CancellationToken ct = default) =>
        ReadAsync(
            $"SELECT {Columns} FROM wakes WHERE workflow = $workflow ORDER BY due_at, id",
            command => command.Parameters.AddWithValue("$workflow", workflow),
            ct);

    public async Task<PendingWake?> FindAsync(long id, CancellationToken ct = default) =>
        (await ReadAsync(
            $"SELECT {Columns} FROM wakes WHERE id = $id",
            command => command.Parameters.AddWithValue("$id", id),
            ct)).FirstOrDefault();

    public Task<IReadOnlyList<PendingWake>> DueAsync(DateTimeOffset now, CancellationToken ct = default) =>
        ReadAsync(
            $"SELECT {Columns} FROM wakes WHERE due_at <= $now ORDER BY due_at, id",
            command => command.Parameters.AddWithValue("$now", Instant(now)),
            ct);

    public async Task<DateTimeOffset?> EarliestDueAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(due_at) FROM wakes";

        return await command.ExecuteScalarAsync(ct) is string text ? Parse(text) : null;
    }

    public async Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM wakes WHERE team = $team";
        command.Parameters.AddWithValue("$team", team);

        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PendingWake>> ForMemberWithinAsync(
        DbConnection connection, DbTransaction transaction, ContainerId member, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM wakes WHERE team = $team AND member = $member ORDER BY due_at, id";
        Add(command, "$team", member.Team);
        Add(command, "$member", member.Name);

        return await ReadAllAsync(command, ct);
    }

    public async Task<PendingWake> InsertWithinAsync(
        DbConnection connection, DbTransaction transaction, PendingWake wake, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO wakes (team, member, workflow, asked_seq, asked_at, due_at, note)
            VALUES ($team, $member, $workflow, $asked, $askedAt, $dueAt, $note)
            RETURNING id
            """;
        Add(command, "$team", wake.Team);
        Add(command, "$member", wake.Member);
        Add(command, "$workflow", wake.Workflow);
        Add(command, "$asked", wake.AskedSeq);
        Add(command, "$askedAt", Instant(wake.AskedAt));
        Add(command, "$dueAt", Instant(wake.DueAt));
        Add(command, "$note", wake.Note);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return wake with { Id = id };
    }

    public async Task SetAskedWithinAsync(
        DbConnection connection, DbTransaction transaction, long id, long askedSeq, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE wakes SET asked_seq = $asked WHERE id = $id";
        Add(command, "$asked", askedSeq);
        Add(command, "$id", id);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<PendingWake?> TakeWithinAsync(
        DbConnection connection, DbTransaction transaction, long id, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM wakes WHERE id = $id RETURNING {Columns}";
        Add(command, "$id", id);

        return (await ReadAllAsync(command, ct)).FirstOrDefault();
    }

    private async Task<IReadOnlyList<PendingWake>> ReadAsync(
        string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);

        return await ReadAllAsync(command, ct);
    }

    private static async Task<IReadOnlyList<PendingWake>> ReadAllAsync(DbCommand command, CancellationToken ct)
    {
        var rows = new List<PendingWake>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PendingWake(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                Parse(reader.GetString(5)),
                Parse(reader.GetString(6)),
                reader.GetString(7)));
        }

        return rows;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Instant(DateTimeOffset at) =>
        at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection, durability: durability);

        return connection;
    }
}
