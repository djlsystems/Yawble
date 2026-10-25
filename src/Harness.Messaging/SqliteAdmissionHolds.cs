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
    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

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
        command.Parameters.AddWithValue("$held", Instant.Stamp(heldAt));
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
        command.Parameters.AddWithValue("$released", Instant.Stamp(releasedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CloseUnfinishedAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE admission_holds SET released_at = $at, unfinished = 1 WHERE released_at IS NULL";
        command.Parameters.AddWithValue("$at", Instant.Stamp(at));
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AdmissionHoldRow>> ReadTeamAsync(
        string team, DateTimeOffset since, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {LedgerFacts.HoldColumns}
            FROM admission_holds
            WHERE team_id = $team AND held_at >= $since AND held_at < $to
              AND (released_at IS NULL OR released_at >= $from)
            ORDER BY held_at, id
            """;
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$since", Instant.Stamp(since));
        command.Parameters.AddWithValue("$from", Instant.Stamp(from));
        command.Parameters.AddWithValue("$to", Instant.Stamp(to));

        var rows = new List<AdmissionHoldRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) rows.Add(LedgerFacts.ReadHold(reader));

        return rows;
    }

}
