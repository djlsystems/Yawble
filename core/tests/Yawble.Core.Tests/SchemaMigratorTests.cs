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
}
