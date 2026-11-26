using Microsoft.Data.Sqlite;

namespace Yawble.Core.Storage;

/// <summary>
/// Gives the disk space of deleted rows back. Deleting rows (a log kept to its retention, a dropped column) leaves
/// free pages inside the database file; the file itself does not get smaller. When a large share of a large file is
/// free, the database is rewritten without them (SQLite's VACUUM).
/// <para>
/// This runs at start, after the migrator and before any store opens the database: nothing else is connected, so no
/// run is active, nothing waits on the rewrite, and the file can be made shorter (on Windows a file another
/// connection has mapped cannot). It runs only when a recent backup exists.
/// </para>
/// </summary>
public static class DbCompaction
{
    /// <summary>A database smaller than this is left alone: there is little to gain.</summary>
    public const long DefaultFloorBytes = 50L * 1024 * 1024;

    /// <summary>The share of the file's pages that must be free before it is rewritten.</summary>
    public const double DefaultFreeShare = 0.20;

    /// <summary>
    /// Rewrites the database at <paramref name="dbPath"/> when it is over <paramref name="floorBytes"/> and more than
    /// <paramref name="freeShare"/> of its pages are free, and <paramref name="backupIsRecent"/> says a recent backup
    /// exists (it may make one). True when the database was rewritten. Never throws: a failure is logged and the
    /// database stays as it is.
    /// </summary>
    public static bool CompactIfDue(string dbPath, Func<bool> backupIsRecent, Action<string>? log = null,
        long floorBytes = DefaultFloorBytes, double freeShare = DefaultFreeShare)
    {
        try
        {
            if (!File.Exists(dbPath)) return false;
            var before = new FileInfo(dbPath).Length;
            if (before < floorBytes) return false;
            // Its own connection, not pooled and with no memory map: closed when this returns.
            using var c = Db.OpenBare($"Data Source={dbPath};Pooling=False");
            Exec(c, "PRAGMA busy_timeout=5000;");
            var pages = Scalar(c, "PRAGMA page_count;");
            var free = Scalar(c, "PRAGMA freelist_count;");
            if (pages <= 0 || free <= pages * freeShare) return false;
            if (!backupIsRecent())
            {
                log?.Invoke($"database: {free} of {pages} pages are free, but there is no recent backup, so the file was not made smaller");
                return false;
            }
            Exec(c, "VACUUM;");
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);"); // the rewritten pages reach the file, and it is cut to its new length
            c.Close();
            var after = new FileInfo(dbPath).Length;
            log?.Invoke($"database: {free} of {pages} pages were free; the file went from {before} to {after} bytes");
            return true;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"database: making the file smaller failed, it stays as it is: {e.Message}");
            return false;
        }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandTimeout = 600; // a rewrite of a large file takes longer than the default 30 seconds
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
