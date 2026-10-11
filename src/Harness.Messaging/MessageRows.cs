using System.Globalization;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// Reading message rows, shared by the log and the ledger.
///
/// Extracted rather than duplicated: two readers of the same table drifting apart in how they
/// materialise a row is the kind of divergence that shows up as a field being null in one view and
/// populated in another, with nothing failing in between.
/// </summary>
internal static class MessageRows
{
    /// <summary>
    /// The column list, in the order <see cref="ReadAllAsync"/> reads by ordinal. Shared so a reader
    /// cannot select a different order from the one it then indexes.
    /// </summary>
    internal const string Columns =
        "seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at";

    // Round-trip format, invariant culture -- the same reason writing uses it. "yyyy" renders
    // through the current culture's CALENDAR, so a th-TH machine reads and writes Buddhist years
    // and sorts against everything written anywhere else.
    internal static DateTimeOffset ReadStamp(string raw) =>
        DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static async Task<IReadOnlyList<Message>> ReadAllAsync(
        SqliteCommand command, CancellationToken ct)
    {
        var results = new List<Message>();

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            results.Add(new Message(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt32(6),
                ReadStamp(reader.GetString(7))));
        }

        return results;
    }
}
