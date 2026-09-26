using System.Data.Common;

namespace Harness.Contracts;

/// <summary>
/// How hard a write is pushed at the disk before it is called done -- SQLite's `synchronous`.
///
/// UNDER WAL THIS IS FLUSH FREQUENCY RATHER THAN A JOURNALLING STRATEGY: `journal_mode = WAL` is
/// already set on the file, and `synchronous` decides how often the WAL is flushed.
///
/// THE NAMES SAY WHAT IS LOST, NOT WHAT IS GAINED. Nobody chooses "fast"; they choose to give
/// something up, and the enum should make them read what.
/// </summary>
public enum SqliteDurability
{
    /// <summary>Survives a power cut. SQLite's `FULL`, and the default here for that reason.</summary>
    SurvivesPowerLoss = 2,

    /// <summary>
    /// Survives a process crash but not a power cut. SQLite's `NORMAL`, which under WAL is already
    /// most of the win and gives up less than `OFF`.
    /// </summary>
    SurvivesCrashOnly = 1,

    /// <summary>Survives neither. SQLite's `OFF` -- a corrupted file is a possible outcome.</summary>
    SurvivesNothing = 0,
}

/// <summary>
/// WHAT A CONNECTION TO THIS DATABASE NEEDS, IN ONE PLACE.
///
/// A pragma is per-CONNECTION and `Pooling=false` makes every `Open()` a fresh one, so these cannot
/// be migration steps: a step runs once, and every connection opened after it would have foreign
/// keys off - under which `ON DELETE CASCADE` still parses, still applies as schema, and still
/// deletes nothing.
///
/// THE DUPLICATION THIS REPLACES HAD ALREADY DRIFTED, which is the whole argument for consolidating
/// rather than adding a ninth copy. Eight sites carried the text and it had become FOUR different
/// spellings: `busy_timeout = 5000` with foreign keys, the same without them, the same again with
/// no spaces around the `=`, and the migrator's deliberate `0`. Nobody chose that; it is what one
/// fact stored in eight places becomes.
///
/// SO THE DEFAULT IS THE CORRECT ONE AND A CALLER MUST OPT OUT EXPLICITLY. The ninth store somebody
/// adds gets foreign keys and a busy timeout by writing nothing, which is the opposite of what
/// happened here - three stores had drifted to opening without foreign keys at all.
///
/// It is safe to turn them on unconditionally: `tenant_events`, `pending_deliveries` and the skills
/// tables carry no `REFERENCES` clause at all, so the pragma is a no-op there rather than a change
/// of behaviour. It was verified before this was written, not assumed.
///
/// TYPED ON <see cref="DbConnection"/> RATHER THAN `SqliteConnection`, so this can live in
/// `Contracts` - which every store project already references - WITHOUT giving the shared contract
/// surface a database driver. The pragma text is SQLite's; the parameter type is the BCL's.
///
/// `journal_mode` IS NOT HERE. It is a property of the FILE rather than of a connection, it is set
/// once by <c>SchemaMigrator</c>, and running it on every open would buy nothing.
/// </summary>
public static class SqlitePragmas
{
    /// <summary>How long a connection waits on a locked database before giving up.</summary>
    public const int DefaultBusyTimeoutMilliseconds = 5000;

    /// <summary>
    /// Applies the pragmas an open connection needs.
    ///
    /// <paramref name="busyTimeoutMilliseconds"/> is a parameter for ONE caller: the migrator's
    /// restore probe passes 0 because it is a probe rather than a wait, and the ordinary 5000 would
    /// make every restore pause for it on the happy path. Everything else takes the default.
    /// </summary>
    public static void Apply(
        DbConnection connection,
        int busyTimeoutMilliseconds = DefaultBusyTimeoutMilliseconds,
        SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Text(busyTimeoutMilliseconds, durability);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc cref="Apply(DbConnection, int)"/>
    public static async Task ApplyAsync(
        DbConnection connection,
        int busyTimeoutMilliseconds = DefaultBusyTimeoutMilliseconds,
        SqliteDurability durability = SqliteDurability.SurvivesPowerLoss,
        CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Text(busyTimeoutMilliseconds, durability);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Text(int busyTimeoutMilliseconds, SqliteDurability durability) =>
        $"PRAGMA busy_timeout = {busyTimeoutMilliseconds}; PRAGMA foreign_keys = ON; "
        + $"PRAGMA synchronous = {(int)durability};";
}
