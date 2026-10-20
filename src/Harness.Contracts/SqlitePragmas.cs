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
/// THE DEFAULT IS THE CORRECT ONE AND A CALLER MUST OPT OUT EXPLICITLY: a store gets foreign keys
/// and a busy timeout by writing nothing. Turning them on unconditionally is safe: `tenant_events`,
/// `pending_deliveries` and the skills tables carry no `REFERENCES` clause, so the pragma is a no-op
/// there rather than a change of behaviour.
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

    /// <summary>
    /// Temporary tables and indexes in memory rather than files; an 8 MB page cache per
    /// connection (connections are short-lived and unpooled, so the figure bounds a burst, not a
    /// process); a 256 MB memory map, which readers share through the page cache instead of
    /// copying pages into each connection.
    /// </summary>
    private static string Text(int busyTimeoutMilliseconds, SqliteDurability durability) =>
        $"PRAGMA busy_timeout = {busyTimeoutMilliseconds}; PRAGMA foreign_keys = ON; "
        + $"PRAGMA synchronous = {(int)durability}; "
        + "PRAGMA temp_store = MEMORY; PRAGMA cache_size = -8192; PRAGMA mmap_size = 268435456;";
}
