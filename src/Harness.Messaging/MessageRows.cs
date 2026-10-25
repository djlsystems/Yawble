using System.Data.Common;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// Reading message rows, shared by the log and the ledger, and by both providers' logs.
///
/// Extracted rather than duplicated: two readers of the same table drifting apart in how they
/// materialise a row is the kind of divergence that shows up as a field being null in one view and
/// populated in another, with nothing failing in between. Over <see cref="DbCommand"/> so the SQL
/// Server log reads a row exactly as the SQLite one does.
/// </summary>
public static class MessageRows
{
    /// <summary>
    /// The column list, in the order <see cref="ReadAllAsync"/> reads by ordinal. Shared so a reader
    /// cannot select a different order from the one it then indexes.
    /// </summary>
    public const string Columns =
        "seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at";

    // Round-trip format, invariant culture -- the same reason writing uses it. "yyyy" renders
    // through the current culture's CALENDAR, so a th-TH machine reads and writes Buddhist years
    // and sorts against everything written anywhere else.
    public static DateTimeOffset ReadStamp(string raw) =>
        Instant.Parse(raw);

    public static async Task<IReadOnlyList<Message>> ReadAllAsync(
        DbCommand command, CancellationToken ct)
    {
        var results = new List<Message>();

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) results.Add(Read(reader));

        return results;
    }

    /// <summary>
    /// One row selected as <see cref="Columns"/>, starting at ordinal 0. Depth is read as a 64-bit
    /// integer and narrowed checked: SQL Server stores it as BIGINT and its reader will not widen or
    /// narrow for you, while SQLite's reader narrows the same way.
    /// </summary>
    public static Message Read(DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            checked((int)reader.GetInt64(6)),
            ReadStamp(reader.GetString(7)));

    /// <summary>
    /// One <see cref="IMessageLog.ReadRunsAsync(ContainerId, long, long, int, RunsWith, CancellationToken)"/>
    /// row: the terminal as <see cref="Columns"/>, then the run's start stamp (or null), whether it
    /// blocked and whether it is in the member's latest run, the two flags as 64-bit integers.
    /// </summary>
    public static RunRow ReadRun(DbDataReader reader) =>
        new(
            Read(reader),
            reader.IsDBNull(8) ? null : ReadStamp(reader.GetString(8)),
            reader.GetInt64(9) != 0,
            reader.GetInt64(10) != 0);

    /// <summary>
    /// One <see cref="IMessageLog.SumUsageForTeamAsync"/> row, every figure a 64-bit integer. The
    /// combined figure is NULL WHERE NO ROW CARRIED ONE, never 0: SUM over no matching rows is SQL
    /// NULL, which is exactly the distinction wanted - a member that reported no combined figure has
    /// not reported a combined figure of nothing.
    /// </summary>
    public static ContainerUsageRow ReadUsage(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            checked((int)reader.GetInt64(3)),
            checked((int)reader.GetInt64(4)),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8));
}
