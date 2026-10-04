using System.Globalization;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>
/// <see cref="IAdmissionHoldLedger"/> over <c>admission_holds</c> (<c>outcome-006</c>) in the log's
/// own database file. Every stamp is written in the "O" form in UTC, so periods compare as text.
/// </summary>
public sealed class SqliteAdmissionHolds(string databasePath) : IAdmissionHoldLedger
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }

    public async Task OpenAsync(
        string team, string member, long? deliverySeq, DateTimeOffset heldAt, string reasonKind, string reason,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // The team's name as it is now, which is as it was when the row is read after a rename or
        // the team's deletion.
        command.CommandText =
            """
            INSERT INTO admission_holds (team_id, team_name, member, delivery_seq, held_at, reason_kind, reason)
            VALUES ($team, COALESCE((SELECT t.name FROM teams t WHERE t.id = $team), $team), $member, $seq, $held,
                $kind, $reason)
            """;
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$member", member);
        command.Parameters.AddWithValue("$seq", (object?)deliverySeq ?? DBNull.Value);
        command.Parameters.AddWithValue("$held", Stamp(heldAt));
        command.Parameters.AddWithValue("$kind", reasonKind);
        command.Parameters.AddWithValue("$reason", reason);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CloseAsync(string team, string member, DateTimeOffset releasedAt, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE admission_holds SET released_at = $released
            WHERE team_id = $team AND member = $member AND released_at IS NULL
            """;
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$member", member);
        command.Parameters.AddWithValue("$released", Stamp(releasedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CloseUnfinishedAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE admission_holds SET released_at = $at, unfinished = 1 WHERE released_at IS NULL";
        command.Parameters.AddWithValue("$at", Stamp(at));
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AdmissionHoldRow>> ReadTeamAsync(
        string team, DateTimeOffset since, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, team_id, team_name, member, delivery_seq, held_at, released_at, reason_kind, reason, unfinished
            FROM admission_holds
            WHERE team_id = $team AND held_at >= $since AND held_at < $to
              AND (released_at IS NULL OR released_at >= $from)
            ORDER BY held_at, id
            """;
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$since", Stamp(since));
        command.Parameters.AddWithValue("$from", Stamp(from));
        command.Parameters.AddWithValue("$to", Stamp(to));

        var rows = new List<AdmissionHoldRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AdmissionHoldRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                MessageRows.ReadStamp(reader.GetString(5)),
                reader.IsDBNull(6) ? null : MessageRows.ReadStamp(reader.GetString(6)),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt64(9) != 0));
        }

        return rows;
    }

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
