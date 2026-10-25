using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// <see cref="IAdmissionHoldLedger"/> over <c>admission_holds</c> on SQL Server: SQLite's
/// <c>SqliteAdmissionHolds</c> statement for statement. Every stamp is written in the "O" form in
/// UTC, so periods compare as text.
///
/// <para>
/// NO READ-THEN-WRITE TO LOCK. An open is one INSERT; a close and the start's close of what is left
/// open are each one UPDATE whose WHERE is evaluated against the row it updates under that row's
/// update lock, so two closes of one hold cannot both set it, and the close-only trigger refuses a
/// second close anyway.
/// </para>
/// </summary>
public sealed class SqlServerAdmissionHolds(string connectionString) : IAdmissionHoldLedger
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task OpenAsync(
        string team, string member, long? deliverySeq, DateTimeOffset heldAt, string reasonKind, string reason,
        CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            """
            INSERT INTO admission_holds (team_id, team_name, member, delivery_seq, held_at, reason_kind, reason)
            VALUES (@team, COALESCE((SELECT t.name FROM teams t WHERE t.id = @team), @team), @member, @seq, @held,
                @kind, @reason)
            """,
            command =>
            {
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@member", member);
                command.Parameters.AddWithValue("@seq", (object?)deliverySeq ?? DBNull.Value);
                command.Parameters.AddWithValue("@held", Instant.Stamp(heldAt));
                command.Parameters.AddWithValue("@kind", reasonKind);
                command.Parameters.AddWithValue("@reason", reason);
            },
            ct);

    public Task CloseAsync(string team, string member, DateTimeOffset releasedAt, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            """
            UPDATE admission_holds SET released_at = @released
            WHERE team_id = @team AND member = @member AND released_at IS NULL
            """,
            command =>
            {
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@member", member);
                command.Parameters.AddWithValue("@released", Instant.Stamp(releasedAt));
            },
            ct);

    public Task<int> CloseUnfinishedAsync(DateTimeOffset at, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            "UPDATE admission_holds SET released_at = @at, unfinished = 1 WHERE released_at IS NULL",
            command => command.Parameters.AddWithValue("@at", Instant.Stamp(at)),
            ct);

    public Task<IReadOnlyList<AdmissionHoldRow>> ReadTeamAsync(
        string team, DateTimeOffset since, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync<IReadOnlyList<AdmissionHoldRow>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     SELECT {LedgerFacts.HoldColumns}
                     FROM admission_holds
                     WHERE team_id = @team AND held_at >= @since AND held_at < @to
                       AND (released_at IS NULL OR released_at >= @from)
                     ORDER BY held_at, id
                     """;
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@since", Instant.Stamp(since));
                command.Parameters.AddWithValue("@from", Instant.Stamp(from));
                command.Parameters.AddWithValue("@to", Instant.Stamp(to));

                var rows = new List<AdmissionHoldRow>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(LedgerFacts.ReadHold(reader));

                return rows;
            },
            ct);
}
