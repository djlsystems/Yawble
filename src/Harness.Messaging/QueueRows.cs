using System.Data.Common;
using System.Globalization;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// Reading <c>pending_deliveries</c> rows, shared by both providers. Each reader selects
/// <c>[subscriber,] seq, started, deferred_from_run</c>, the integers as 64-bit.
/// </summary>
public static class PendingRows
{
    public static PendingDelivery Read(DbDataReader reader) =>
        new(reader.GetInt64(0), reader.GetInt64(1) != 0, reader.IsDBNull(2) ? null : reader.GetInt64(2));

    public static TeamPendingDelivery ReadTeam(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2) != 0,
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
}

/// <summary>
/// Reading and writing <c>wakes</c> rows, shared by both providers. Instants are stored as round-trip
/// UTC text of one width, so ordering by the column is ordering by time.
/// </summary>
public static class WakeRows
{
    public const string Columns = "id, team, member, workflow, asked_seq, asked_at, due_at, note";

    public static async Task<IReadOnlyList<PendingWake>> ReadAllAsync(DbCommand command, CancellationToken ct)
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

    public static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    public static string Instant(DateTimeOffset at) =>
        at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
