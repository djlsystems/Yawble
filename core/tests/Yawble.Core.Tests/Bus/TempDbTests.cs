using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Storage;

namespace Yawble.Core.Tests.Bus;

/// <summary>The test database every other test starts from: a copy of one migrated template.</summary>
public class TempDbTests
{
    [Fact]
    public void ANewTempDb_HasEveryMigrationApplied_AsOneFileInWalMode()
    {
        using var db = new TempDb();

        Assert.Equal(["yawble.db"], Directory.GetFiles(db.Dir).Select(Path.GetFileName));
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode;"));
            Assert.Equal("ok", Scalar(connection, "PRAGMA integrity_check;"));
        }
        // Nothing is pending: the migrator applies nothing more and reports the whole list.
        Assert.Equal(SchemaMigrator.Available, SchemaMigrator.Migrate(db.ConnectionString));
    }

    [Fact]
    public void TwoTempDbs_ShareNoRows()
    {
        using var first = new TempDb();
        using var second = new TempDb();
        using (var store = first.OpenStore()) store.CreateTeam("Alpha");

        using var connection = new SqliteConnection(second.ConnectionString);
        connection.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT count(*) FROM teams;"));
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
