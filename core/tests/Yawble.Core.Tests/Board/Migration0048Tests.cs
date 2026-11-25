using Microsoft.Data.Sqlite;
using Yawble.Core.Storage;
using Xunit;

namespace Yawble.Core.Tests.Board;

/// <summary>
/// 0048 rebuilds board_cards so its status may be 'stopped'. Run again over a database with cards and
/// trail lines (its schema_migrations row removed), it keeps every card, trail line, id and counter, and the trail still
/// cascades with its card.
/// </summary>
public class Migration0048Tests
{
    [Fact]
    public void TheRebuild_KeepsCardsTrailsIdsAndCounters_AndAllowsStopped()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-db-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False";
        try
        {
            SchemaMigrator.Migrate(cs);
            using (var c = Open(cs))
            {
                Exec(c, """
                    INSERT INTO board_cards (team, member, title, status, lane, created_at, updated_at, started_at, timeline_rebuild)
                        VALUES ('web', 'Tomas', 'A', 'running', 'in-progress', 't0', 't1', 't0', 1),
                               ('web', 'Ines', 'B', 'failed', 'blocked', 't0', 't1', NULL, 0),
                               ('web', 'Kofi', 'C', 'queued', 'todo', 't0', 't1', NULL, 0);
                    INSERT INTO board_trail (card_id, at, source, kind, text) VALUES (1, 't0', 'Manager', 'told', 'Build it'),
                        (1, 't1', 'Tomas', 'progress', 'coding'), (2, 't1', 'Ines', 'failed', 'timeout'), (3, 't0', 'Manager', 'told', 'Test');
                    DELETE FROM board_trail WHERE card_id = 3;
                    DELETE FROM board_cards WHERE id = 3;
                    DELETE FROM schema_migrations WHERE name = '0048_board_card_stopped';
                    """);
            }

            SchemaMigrator.Migrate(cs);

            using var db = Open(cs);
            Assert.Equal(2L, Scalar(db, "SELECT count(*) FROM board_cards;"));
            Assert.Equal(3L, Scalar(db, "SELECT count(*) FROM board_trail;"));
            Assert.Equal("t0|1", Scalar(db, "SELECT started_at || '|' || timeline_rebuild FROM board_cards WHERE id = 1;"));
            Assert.Equal(3L, Scalar(db, "SELECT seq FROM sqlite_sequence WHERE name = 'board_cards';"));
            Assert.Equal(4L, Scalar(db, "SELECT seq FROM sqlite_sequence WHERE name = 'board_trail';"));
            Assert.Equal(2L, Scalar(db, "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name IN ('board_cards_team', 'board_cards_member');"));
            Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM sqlite_master WHERE name LIKE '%0048%';"));

            Exec(db, "UPDATE board_cards SET status = 'stopped' WHERE id = 1;");
            Assert.Throws<SqliteException>(() => Exec(db, "UPDATE board_cards SET status = 'halted' WHERE id = 1;"));
            Exec(db, "INSERT INTO board_cards (team, title, created_at, updated_at) VALUES ('web', 'D', 't2', 't2');");
            Assert.Equal(4L, Scalar(db, "SELECT max(id) FROM board_cards;")); // id 3 is not reused
            Exec(db, "DELETE FROM board_cards WHERE id = 1;");
            Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM board_trail;")); // the trail still cascades
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    private static SqliteConnection Open(string cs)
    {
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
