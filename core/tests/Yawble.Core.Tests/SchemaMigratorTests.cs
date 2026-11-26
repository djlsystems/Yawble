using Microsoft.Data.Sqlite;
using Yawble.Core.Storage;
using Xunit;

namespace Yawble.Core.Tests;

public class SchemaMigratorTests
{
    [Fact]
    public void AppliesEveryMigrationOnceInWalMode()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-db-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False";
        try
        {
            Assert.Equal(SchemaMigrator.Available, SchemaMigrator.Migrate(cs));
            Assert.Equal(SchemaMigrator.Available, SchemaMigrator.Migrate(cs)); // idempotent

            using var c = new SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", cmd.ExecuteScalar());
            cmd.CommandText = "SELECT value FROM app_meta WHERE key = 'created_by_schema';";
            Assert.Equal("0001_baseline", cmd.ExecuteScalar());
            cmd.CommandText = "SELECT count(*) FROM schema_migrations;";
            Assert.Equal((long)SchemaMigrator.Available.Count, cmd.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MigrationNamesAreOrderedAndNumbered()
    {
        Assert.NotEmpty(SchemaMigrator.Available);
        Assert.All(SchemaMigrator.Available, n => Assert.Matches(@"^\d{4}_[a-z0-9_]+$", n));
        Assert.Equal(SchemaMigrator.Available.Order(StringComparer.Ordinal), SchemaMigrator.Available);
    }

    /// <summary>
    /// A database a newer version of the app wrote (it holds a migration this build does not ship) is not opened: the
    /// start stops with a message for the person, nothing is applied and nothing is backed up.
    /// </summary>
    [Fact]
    public void ADatabaseFromANewerVersionIsRefused_InPlainWords_AndLeftAsItIs()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-db-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False";
        try
        {
            SchemaMigrator.Migrate(cs);
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO schema_migrations (name, applied_at) VALUES ('9998_from_the_future', 'then'), ('9999_more', 'then');";
                cmd.ExecuteNonQuery();
            }
            var backedUp = false;

            var e = Assert.Throws<DatabaseNewerException>(() => SchemaMigrator.Migrate(cs, (_, _) => backedUp = true));

            Assert.Equal(["9998_from_the_future", "9999_more"], e.Unknown);
            Assert.Contains("older version of Yawble", e.Message);
            Assert.Contains("Install the newest version", e.Message);
            Assert.Contains("9998_from_the_future", e.Message);
            Assert.False(backedUp);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// A database that is behind and also holds an unknown migration is refused before anything is applied to it.
    /// </summary>
    [Fact]
    public void ADatabaseFromANewerVersionIsRefused_BeforeAnyPendingMigrationRuns()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-db-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False";
        try
        {
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT; INSERT INTO schema_migrations (name, applied_at) VALUES ('9999_from_the_future', 'then');";
                cmd.ExecuteNonQuery();
            }

            Assert.Throws<DatabaseNewerException>(() => SchemaMigrator.Migrate(cs));

            using var check = new SqliteConnection(cs);
            check.Open();
            using var q = check.CreateCommand();
            q.CommandText = "SELECT count(*) FROM schema_migrations;";
            Assert.Equal(1L, q.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The numbers three pairs of migrations share. No other number may be used twice.</summary>
    private static readonly string[] KnownSharedNumbers = ["0044", "0048", "0059"];

    [Fact]
    public void EachMigrationHasItsOwnNumber_ExceptTheKnownSharedOnes()
    {
        var shared = SchemaMigrator.Available.GroupBy(n => n[..4]).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.Equal(KnownSharedNumbers, shared);
        // From 0062 on every number is used once, so the files apply in one order on every database.
        Assert.All(shared, n => Assert.True(string.CompareOrdinal(n, "0062") < 0, $"{n} is used by more than one migration"));
    }

    [Fact]
    public void MigrationNumbersHaveNoGaps()
    {
        var numbers = SchemaMigrator.Available.Select(n => int.Parse(n[..4], System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToList();

        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }
}
