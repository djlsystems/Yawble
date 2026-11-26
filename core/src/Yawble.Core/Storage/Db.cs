using Microsoft.Data.Sqlite;

namespace Yawble.Core.Storage;

/// <summary>
/// Opens every connection to the app's database with the same settings. The settings belong to a connection, not to
/// the file, and a pooled connection is handed from one store to the next, so each open sets all of them.
/// <list type="bullet">
/// <item>foreign_keys ON: a row never points at a parent that is gone.</item>
/// <item>busy_timeout 5 s: a write that meets another connection's commit waits for it instead of failing.</item>
/// <item>journal_size_limit 64 MB: after a large write the write-ahead log file shrinks back to that.</item>
/// <item>mmap_size 256 MB: reads of the first 256 MB of the file come from mapped memory.</item>
/// <item>synchronous: FULL (every commit is on disk before it returns), or NORMAL for a log (see
/// <see cref="OpenLog"/>).</item>
/// </list>
/// </summary>
public static class Db
{
    private const string Shared =
        "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_size_limit=67108864; PRAGMA mmap_size=268435456;";

    /// <summary>The settings of <see cref="Open"/>, as one statement batch.</summary>
    public const string Settings = Shared + " PRAGMA synchronous=FULL;";

    /// <summary>The settings of <see cref="OpenLog"/>, as one statement batch.</summary>
    public const string LogSettings = Shared + " PRAGMA synchronous=NORMAL;";

    /// <summary>An open connection for the app's state: every commit is flushed to disk before it returns.</summary>
    public static SqliteConnection Open(string connectionString) => Open(connectionString, Settings);

    /// <summary>
    /// An open connection for a log (network events, model usage, the audit log, transcripts, log upkeep). Commits are
    /// flushed to disk at the next checkpoint, not one by one: the database stays consistent, and a power cut can lose
    /// the last log rows.
    /// </summary>
    public static SqliteConnection OpenLog(string connectionString) => Open(connectionString, LogSettings);

    /// <summary>
    /// An open connection with no settings applied: for the migrator (which sets its own before the schema exists), for
    /// reading a backup file, and for a database that may be damaged.
    /// </summary>
    public static SqliteConnection OpenBare(string connectionString)
    {
        var c = new SqliteConnection(connectionString);
        try { c.Open(); }
        catch { c.Dispose(); throw; }
        return c;
    }

    private static SqliteConnection Open(string connectionString, string settings)
    {
        var c = OpenBare(connectionString);
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = settings;
            cmd.ExecuteNonQuery();
        }
        catch { c.Dispose(); throw; }
        return c;
    }

    /// <summary>
    /// Lets SQLite refresh the statistics its query planner uses, for the tables whose content changed enough to need
    /// it. Each index is sampled (400 rows), so this stays quick on a large database. Never throws.
    /// </summary>
    public static void Optimize(string connectionString, Action<string>? log = null)
    {
        try
        {
            using var c = Open(connectionString);
            using var cmd = c.CreateCommand();
            // 0x10002: also the tables this connection did not read itself (it is opened only for this).
            cmd.CommandText = "PRAGMA analysis_limit=400; PRAGMA optimize=0x10002;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException e) { log?.Invoke($"database: refreshing the query statistics failed: {e.Message}"); }
    }
}
