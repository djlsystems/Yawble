using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Storage;

namespace Yawble.Core.Tests.Storage;

/// <summary>
/// The disk space of deleted rows is given back when much of a large database file is free, and only with a recent
/// backup. Small sizes stand in for the real floor. Fakes only: a temp SQLite file.
/// </summary>
public sealed class DbCompactionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("yawble-compact-").FullName;
    private string DbPath => Path.Combine(_dir, "yawble.db");
    private string Cs => $"Data Source={DbPath};Pooling=False";
    private readonly List<string> _log = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Exec(string sql)
    {
        using var c = new SqliteConnection(Cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var c = new SqliteConnection(Cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>A migrated database with <paramref name="rows"/> usage rows of about 600 bytes each.</summary>
    private void Filled(int rows)
    {
        SchemaMigrator.Migrate(Cs);
        Exec($"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {rows})
            INSERT INTO usage_events (at, run_key, member, vhost, path, status)
                SELECT 'then', 'r-' || i, 'web/Ada', 'model.yawble.internal', printf('%0600d', i), 200 FROM n;
            """);
        Exec("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    [Fact]
    public void A_file_that_is_mostly_free_pages_is_rewritten_and_gets_smaller()
    {
        Filled(4000);
        Exec("DELETE FROM usage_events WHERE id > 100;");
        Exec("PRAGMA wal_checkpoint(TRUNCATE);");
        var before = new FileInfo(DbPath).Length;
        Assert.True(Scalar("PRAGMA freelist_count;") > Scalar("PRAGMA page_count;") / 5);

        Assert.True(DbCompaction.CompactIfDue(DbPath, () => true, _log.Add, floorBytes: 100_000));

        Assert.Equal(0, Scalar("PRAGMA freelist_count;"));
        Assert.True(new FileInfo(DbPath).Length < before / 2, $"{new FileInfo(DbPath).Length} of {before}");
        Assert.Equal(100, Scalar("SELECT count(*) FROM usage_events;"));
        Assert.Equal("ok", QuickCheck());
        Assert.Equal("wal", JournalMode());
        Assert.Contains(_log, l => l.Contains("pages were free"));
    }

    [Fact]
    public void A_file_with_few_free_pages_is_left_alone()
    {
        Filled(4000);
        Exec("DELETE FROM usage_events WHERE id > 3900;");
        var asked = false;

        Assert.False(DbCompaction.CompactIfDue(DbPath, () => asked = true, _log.Add, floorBytes: 100_000));

        Assert.False(asked);
        Assert.True(Scalar("PRAGMA freelist_count;") > 0);
        Assert.Empty(_log);
    }

    [Fact]
    public void A_file_under_the_floor_is_left_alone()
    {
        Filled(4000);
        Exec("DELETE FROM usage_events WHERE id > 100;");
        var free = Scalar("PRAGMA freelist_count;");

        Assert.False(DbCompaction.CompactIfDue(DbPath, () => true, _log.Add)); // the real floor: 50 MB

        Assert.Equal(free, Scalar("PRAGMA freelist_count;"));
    }

    [Fact]
    public void Without_a_recent_backup_the_file_is_left_alone_and_the_log_says_so()
    {
        Filled(4000);
        Exec("DELETE FROM usage_events WHERE id > 100;");
        var free = Scalar("PRAGMA freelist_count;");

        Assert.False(DbCompaction.CompactIfDue(DbPath, () => false, _log.Add, floorBytes: 100_000));

        Assert.Equal(free, Scalar("PRAGMA freelist_count;"));
        Assert.Contains(_log, l => l.Contains("no recent backup"));
    }

    [Fact]
    public void A_missing_database_is_nothing_to_do()
    {
        Assert.False(DbCompaction.CompactIfDue(Path.Combine(_dir, "none.db"), () => true, _log.Add, floorBytes: 0));
        Assert.False(File.Exists(Path.Combine(_dir, "none.db")));
    }

    /// <summary>The start-up order: the backup is made when none is recent, then the file is rewritten.</summary>
    [Fact]
    public void The_backup_service_makes_the_daily_backup_first_when_none_is_recent()
    {
        Filled(4000);
        Exec("DELETE FROM usage_events WHERE id > 100;");
        var now = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        var backups = new BackupService(_dir, () => now);

        Assert.True(DbCompaction.CompactIfDue(DbPath, backups.EnsureRecent, _log.Add, floorBytes: 100_000));

        Assert.Equal("daily", Assert.Single(backups.List()).Reason);
        now = now.AddHours(1);
        Assert.True(backups.EnsureRecent()); // an hour old: recent, and no second backup
        Assert.Single(backups.List());
    }

    [Fact]
    public void A_backup_that_cannot_be_made_is_not_recent()
    {
        Filled(100);
        var backups = new BackupService(_dir, () => DateTimeOffset.UtcNow) { QuickCheck = _ => "damaged" };

        Assert.False(backups.EnsureRecent());
    }

    private string QuickCheck()
    {
        using var c = new SqliteConnection(Cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        return (string)cmd.ExecuteScalar()!;
    }

    private string JournalMode()
    {
        using var c = new SqliteConnection(Cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return (string)cmd.ExecuteScalar()!;
    }
}
