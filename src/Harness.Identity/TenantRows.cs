using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// A <c>tenant_events</c> ROW AS EITHER PROVIDER'S TENANT LOG READS IT: the column list and the
/// reader, shared so the two read one row the same way.
/// </summary>
public static class TenantEventRows
{
    /// <summary>The columns <see cref="Read"/> reads, in its order.</summary>
    public const string Columns = "seq, occurred_at, actor_id, actor_email, action, subject, subject_name, detail";

    public static TenantEvent Read(DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            Instant.Parse(
                reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
}

/// <summary>
/// THE TENANT LOG'S ROW COUNT, COUNTED ONCE PER APPEND: the highest seq (the primary key, one step)
/// says whether anything was appended since the last count, and the table is only ever appended to.
/// One per store, as the store is one per process.
/// </summary>
public sealed class TenantLogTotal
{
    /// <summary>The last count taken, and the highest seq it was taken at.</summary>
    private (long Highest, long Total) _counted = (-1, 0);

    /// <summary>The count taken at <paramref name="highest"/>, or null when it must be counted again.</summary>
    public long? At(long highest)
    {
        var counted = _counted;
        return counted.Highest == highest ? counted.Total : null;
    }

    /// <summary>Remembers <paramref name="total"/> as the count at <paramref name="highest"/>.</summary>
    public void Counted(long highest, long total) => _counted = (highest, total);
}

/// <summary>
/// THE <c>tenant_events</c> ROW EACH SETTING CHANGE LANDS WITH, built the same on both providers: the
/// action, the subject and the JSON detail naming the setting, the old value and the new.
/// </summary>
public static class TenantSettingAudits
{
    /// <summary>The audit row for <paramref name="change"/>: a reset when its new value is null.</summary>
    public static TriggerAudit For(TenantSettingChange change, string? actorId, string actorEmail) =>
        change.NewValue is null
            ? new TriggerAudit(
                actorId, actorEmail, TenantActions.TenantSettingReset, change.Name, change.Name,
                JsonSerializer.Serialize(new
                {
                    setting = change.Name,
                    old = change.OldValue,
                    @new = (string?)null,
                    change = "reset to default",
                }))
            : new TriggerAudit(
                actorId, actorEmail, TenantActions.TenantSettingChanged, change.Name, change.Name,
                JsonSerializer.Serialize(new
                {
                    setting = change.Name,
                    old = change.OldValue,
                    @new = change.NewValue,
                }));
}

/// <summary>
/// AN <c>unfinished_removals</c> ROW AS EITHER PROVIDER WRITES AND READS IT: the column list, the two
/// JSON columns' values and the reader.
/// </summary>
public static class UnfinishedRemovalRows
{
    /// <summary>The columns <see cref="Read"/> reads, in its order.</summary>
    public const string Columns = "path, kind, team, member, remaining, recorded_at, attempts, host_left";

    /// <summary><c>remaining</c>: the paths still on disk, as JSON.</summary>
    public static string Remaining(UnfinishedRemoval removal) => JsonSerializer.Serialize(removal.Remaining);

    /// <summary><c>host_left</c>: what the Host's own attempt left, as JSON, or NULL for nothing.</summary>
    public static object HostLeft(UnfinishedRemoval removal) =>
        removal.HostLeft is { Count: > 0 } left ? JsonSerializer.Serialize(left) : DBNull.Value;

    public static UnfinishedRemoval Read(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [],
            Instant.Parse(reader.GetString(5)),
            checked((int)reader.GetInt64(6)),
            reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(7)));
}
