using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Storage;

namespace Yawble.Core.Tests.Storage;

/// <summary>
/// Migration 0062: indexes for the lookups that grow with history, the unused indexes gone, and usage_events without
/// its raw usage text. A database at 0061 with rows in it is brought up to date and keeps its rows. Fakes only: a temp
/// SQLite file.
/// </summary>
public sealed class Migration0062Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("yawble-0062-").FullName;
    private string Cs => $"Data Source={Path.Combine(_dir, "yawble.db")};Pooling=False";

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A database with every migration before 0062 applied (as an older app left it).</summary>
    private void At0061()
    {
        using var c = Open();
        Exec(c, "CREATE TABLE schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT;");
        foreach (var name in SchemaMigrator.Available.Where(n => string.CompareOrdinal(n, "0062") < 0))
        {
            using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Yawble.Core.Migrations." + name + ".sql")!;
            var sql = new StreamReader(stream).ReadToEnd();
            var keysOff = sql.StartsWith(SchemaMigrator.ForeignKeysOffMarker, StringComparison.Ordinal);
            if (keysOff) Exec(c, "PRAGMA foreign_keys=OFF;");
            Exec(c, sql);
            if (keysOff) Exec(c, "PRAGMA foreign_keys=ON;");
            Exec(c, $"INSERT INTO schema_migrations (name, applied_at) VALUES ('{name}', 'then');");
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(Cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private List<string> Column(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read()) list.Add(reader.GetString(reader.FieldCount - 1));
        return list;
    }

    private void Seed()
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO messages (type, payload, source, correlation_id, depth, occurred_at) VALUES ('agent.instruction.Alpha/Ada', '{}', 'person', 1, 0, 'now');
            INSERT INTO messages (type, payload, source, correlation_id, causation_seq, depth, occurred_at) VALUES ('agent.needsDecision', '{}', 'Alpha/Ada', 1, 1, 1, 'now');
            INSERT INTO usage_events (at, run_key, run_number, member, vhost, path, status, model, input_tokens, output_tokens, usage)
                VALUES ('now', 'r-1', 5, 'Alpha/Ada', 'model.yawble.internal', '/v1/messages', 200, 'opus', 1200, 300, '{"input_tokens":1200}');
            INSERT INTO usage_events (at, run_key, member, vhost, path, status, usage)
                VALUES ('now', 'r-2', 'Alpha/Ada', 'model.yawble.internal', '/v1/messages', 200, '"not read"');
            INSERT INTO board_cards (team, member, deliverable, title, lane, created_at, updated_at) VALUES ('Alpha', 'Ada', 1, 'A card', 'todo', 'now', 'now');
            INSERT INTO gateway_events (at, kind, guardrail) VALUES ('now', 'denied', 1);
            INSERT INTO run_records (run_key, bus_run_id, team, member, cli, kind, backend, started_at) VALUES ('r-1', 5, 'Alpha', 'Ada', 'claude', 'headless', 'x', 'now');
            """);
    }

    [Fact]
    public void A_database_with_rows_keeps_them_and_gets_the_new_indexes()
    {
        At0061();
        Seed();

        SchemaMigrator.Migrate(Cs);

        Assert.Equal(2L, Scalar("SELECT count(*) FROM messages;"));
        Assert.Equal(2L, Scalar("SELECT count(*) FROM usage_events;"));
        Assert.Equal(1200L, Scalar("SELECT input_tokens FROM usage_events WHERE run_key = 'r-1';"));
        Assert.Equal(5L, Scalar("SELECT run_number FROM usage_events WHERE run_key = 'r-1';"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM board_cards WHERE title = 'A card';"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM gateway_events WHERE guardrail = 1;"));
        Assert.Equal(5L, Scalar("SELECT bus_run_id FROM run_records WHERE run_key = 'r-1';"));
        Assert.DoesNotContain("usage", Column("SELECT name FROM pragma_table_info('usage_events');"));

        var indexes = Column("SELECT name FROM sqlite_master WHERE type = 'index';");
        foreach (var added in new[]
                 {
                     "ix_messages_causation", "ix_messages_type_source", "ix_messages_head", "ix_runs_correlation",
                     "ix_usage_events_run_number", "ix_board_cards_deliverable", "ix_board_cards_team_member",
                     "ix_backlog_dispatches_deliverable",
                 })
            Assert.Contains(added, indexes);
        foreach (var dropped in new[]
                 {
                     "ix_messages_correlation", "board_cards_member", "board_cards_team", "ix_messages_seq_type",
                     "ix_messages_origin", "ix_gateway_events_guardrail", "ix_run_records_bus_run",
                 })
            Assert.DoesNotContain(dropped, indexes);
    }

    [Fact]
    public void A_new_database_has_the_same_indexes_as_an_upgraded_one()
    {
        At0061();
        SchemaMigrator.Migrate(Cs);
        var upgraded = Column("SELECT name FROM sqlite_master WHERE type = 'index' ORDER BY name;");

        var fresh = $"Data Source={Path.Combine(_dir, "fresh.db")};Pooling=False";
        SchemaMigrator.Migrate(fresh);
        using var c = new SqliteConnection(fresh);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));

        Assert.Equal(upgraded, names);
    }

    /// <summary>
    /// The unanswered-questions lookup reads questions by type and each question's replies by what they answer. Without
    /// those two indexes every unanswered question costs a pass over all messages.
    /// </summary>
    [Fact]
    public void The_open_decisions_lookup_searches_by_type_and_by_causation()
    {
        SchemaMigrator.Migrate(Cs);

        var plan = string.Join("\n", Column("""
            EXPLAIN QUERY PLAN
            SELECT seq FROM messages
            WHERE type = 'agent.needsDecision' AND source LIKE 'Alpha/%' ESCAPE '\'
              AND NOT EXISTS (SELECT 1 FROM messages a WHERE a.causation_seq = messages.seq AND a.source = 'person')
              AND NOT EXISTS (SELECT 1 FROM messages a WHERE a.source = 'person' AND a.correlation_id = messages.correlation_id
                              AND a.seq > messages.seq AND a.type = 'agent.instruction.' || messages.source)
            ORDER BY seq;
            """));

        Assert.Contains("SEARCH messages USING INDEX ix_messages_type_source", plan);
        Assert.Contains("USING INDEX ix_messages_causation (causation_seq=?)", plan);
        Assert.DoesNotContain("SCAN", plan);
    }

    [Theory]
    [InlineData("SELECT id FROM runs WHERE correlation_id = 7 ORDER BY id;", "ix_runs_correlation")]
    [InlineData("SELECT sum(u.input_tokens) FROM usage_events u JOIN runs r ON r.id = u.run_number WHERE r.correlation_id = 7;", "ix_usage_events_run_number")]
    [InlineData("SELECT id FROM board_cards WHERE deliverable = 7;", "ix_board_cards_deliverable")]
    [InlineData("SELECT id FROM board_cards WHERE team = 'a' COLLATE NOCASE AND member = 'b' COLLATE NOCASE AND deliverable = 7;", "ix_board_cards_team_member")]
    [InlineData("SELECT item_id FROM backlog_dispatches WHERE deliverable = 7;", "ix_backlog_dispatches_deliverable")]
    public void The_lookups_of_one_deliverable_use_their_index(string sql, string index)
    {
        SchemaMigrator.Migrate(Cs);

        var plan = string.Join("\n", Column("EXPLAIN QUERY PLAN " + sql));

        Assert.Contains(index, plan);
    }
}
