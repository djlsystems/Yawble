using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Storage;

namespace Yawble.Core.Tests.Storage;

/// <summary>
/// The migrations that rebuild a table or rewrite rows, each run on a database that holds rows from before it: the
/// database is built with every earlier migration (as an older app left it), seeded with that time's table and column
/// names, and brought up to date. The rows, their ids and what points at them must still be there under today's names.
/// Fakes only: a temp SQLite file.
/// </summary>
public sealed class MigrationUpgradeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("yawble-upgrade-").FullName;
    private string Cs => $"Data Source={Path.Combine(_dir, "yawble.db")};Pooling=False";

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A database with every migration before <paramref name="migration"/> applied, and a team with one member in it.</summary>
    private void Before(string migration)
    {
        Assert.Contains(migration, SchemaMigrator.Available);
        using var c = Open();
        Exec(c, "CREATE TABLE schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT;");
        foreach (var name in SchemaMigrator.Available.Where(n => string.CompareOrdinal(n, migration) < 0))
        {
            using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Yawble.Core.Migrations." + name + ".sql")!;
            Exec(c, new StreamReader(stream).ReadToEnd());
            Exec(c, $"INSERT INTO schema_migrations (name, applied_at) VALUES ('{name}', 'then');");
        }
        Exec(c, """
            INSERT INTO teams (id, name, created_at) VALUES (1, 'Alpha', 'then');
            INSERT INTO members (id, team_id, name, role, created_at) VALUES (10, 1, 'Ada', 'member', 'then');
            """);
    }

    /// <summary>Seeds with foreign keys on, as the app writes its rows.</summary>
    private void Seed(string sql)
    {
        using var c = Open();
        Exec(c, "PRAGMA foreign_keys=ON;");
        Exec(c, sql);
    }

    private void Migrate(string migration) => Assert.Contains(migration, SchemaMigrator.Migrate(Cs));

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

    /// <summary>One row's columns joined with '|' (NULL as "null"), so a whole row is compared at once.</summary>
    private string Row(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"no row: {sql}");
        return string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Migration0013_KeepsEveryConciergeMessage_WhenTheTableIsRebuiltForUpdates()
    {
        const string migration = "0013_concierge_watches";
        Before(migration);
        Seed("""
            INSERT INTO concierge_conversations (id, title, cli, team_id, created_at, updated_at) VALUES ('c-1', 'Release', 'claude', 1, 'then', 'then');
            INSERT INTO concierge_messages (id, conversation_id, role, text, at) VALUES (3, 'c-1', 'user', 'Ship it', 't1');
            INSERT INTO concierge_messages (id, conversation_id, role, text, at, state, error, tools, cli, run_key)
                VALUES (4, 'c-1', 'assistant', 'Shipping', 't2', 'failed', 'no network', '[{"name":"tell"}]', 'claude', 'r-9');
            """);

        Migrate(migration);

        Assert.Equal(2L, Scalar("SELECT count(*) FROM concierge_messages;"));
        Assert.Equal("c-1|user|Ship it|t1|null|null|[]|null|null", Row("SELECT conversation_id, role, text, at, state, error, tools, cli, run_key FROM concierge_messages WHERE id = 3;"));
        Assert.Equal("c-1|assistant|Shipping|t2|failed|no network|[{\"name\":\"tell\"}]|claude|r-9",
            Row("SELECT conversation_id, role, text, at, state, error, tools, cli, run_key FROM concierge_messages WHERE id = 4;"));
        // what the step adds: nothing is unread, the old messages are about no team, and an update can be stored
        Assert.Equal(0L, Scalar("SELECT unread FROM concierge_conversations WHERE id = 'c-1';"));
        Assert.Equal("null|null|null", Row("SELECT team, kind, seq FROM concierge_messages WHERE id = 4;"));
        Seed("INSERT INTO concierge_messages (conversation_id, role, text, at) VALUES ('c-1', 'update', 'Alpha finished', 't3');");
        Assert.Equal(5L, Scalar("SELECT id FROM concierge_messages WHERE role = 'update';"));
        // the messages still go with their conversation
        Seed("DELETE FROM concierge_conversations WHERE id = 'c-1';");
        Assert.Equal(0L, Scalar("SELECT count(*) FROM concierge_messages;"));
    }

    private const string TriggerColumns =
        "team_id, member_id, name, kind, interval_sec, cron, timezone, fire_at, instruction, only_when_idle, enabled, next_due_at, last_fired_at, " +
        "last_outcome, last_reason, last_seq, missed_count, created_at, updated_at, wake_manager, saved_by, saved_via";

    private const string SeedTimeTriggers = """
        INSERT INTO triggers (id, team_id, member_id, name, kind, interval_sec, instruction, only_when_idle, enabled, next_due_at, last_fired_at,
                              last_outcome, last_reason, last_seq, missed_count, created_at, updated_at, wake_manager, saved_by, saved_via)
            VALUES (5, 1, 10, 'tick', 'every', 600, 'Look at the queue.', 0, 1, 'due-1', 'fired-1', 'fired', 'on time', 42, 3, 'c1', 'u1', 'never', 'Alpha/Ada', 'tool');
        INSERT INTO triggers (id, team_id, member_id, name, kind, cron, timezone, instruction, enabled, created_at, updated_at)
            VALUES (6, 1, 10, 'nightly', 'cron', '0 2 * * *', 'Europe/Berlin', 'Run the nightly check.', 0, 'c2', 'u2');
        INSERT INTO triggers (id, team_id, member_id, name, kind, fire_at, instruction, created_at, updated_at)
            VALUES (9, 1, 10, 'launch', 'once', '2030-01-01T09:00:00Z', 'Announce it.', 'c3', 'u3');
        """;

    private void AssertTimeTriggersKept()
    {
        Assert.Equal("1|10|tick|every|600|null|null|null|Look at the queue.|0|1|due-1|fired-1|fired|on time|42|3|c1|u1|never|Alpha/Ada|tool",
            Row($"SELECT {TriggerColumns} FROM triggers WHERE id = 5;"));
        Assert.Equal("1|10|nightly|cron|null|0 2 * * *|Europe/Berlin|null|Run the nightly check.|1|0|null|null|null|null|null|0|c2|u2|always|null|null",
            Row($"SELECT {TriggerColumns} FROM triggers WHERE id = 6;"));
        Assert.Equal("1|10|launch|once|null|null|null|2030-01-01T09:00:00Z|Announce it.|1|1|null|null|null|null|null|0|c3|u3|always|null|null",
            Row($"SELECT {TriggerColumns} FROM triggers WHERE id = 9;"));
    }

    [Fact]
    public void Migration0017_KeepsEveryTrigger_WhenTheTableIsRebuiltForFolderTriggers()
    {
        const string migration = "0017_folder_triggers";
        Before(migration);
        Seed(SeedTimeTriggers);

        Migrate(migration);

        Assert.Equal(3L, Scalar("SELECT count(*) FROM triggers;"));
        AssertTimeTriggersKept();
        Assert.Equal("null|null|null|null|null", Row("SELECT watch_root, watch_path, watch_glob, quiet_sec, watch_state FROM triggers WHERE id = 5;"));
        // a new trigger gets an id after the highest one, and the due index is there
        Seed("INSERT INTO triggers (team_id, member_id, name, kind, interval_sec, instruction, created_at, updated_at) VALUES (1, 10, 'new', 'every', 60, 'x', 'now', 'now');");
        Assert.Equal(10L, Scalar("SELECT id FROM triggers WHERE name = 'new';"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_triggers_due';"));
        // the triggers still go with their member
        Seed("DELETE FROM members WHERE id = 10;");
        Assert.Equal(0L, Scalar("SELECT count(*) FROM triggers;"));
    }

    [Fact]
    public void Migration0021_KeepsEveryTriggerAndItsFolderWatch_WhenTheTableIsRebuiltForEventTriggers()
    {
        const string migration = "0021_events";
        Before(migration);
        Seed(SeedTimeTriggers + """
            INSERT INTO triggers (id, team_id, member_id, name, kind, interval_sec, instruction, created_at, updated_at, watch_root, watch_path, watch_glob, quiet_sec, watch_state)
                VALUES (12, 1, 10, 'inbox', 'folder', 30, 'Read the new files.', 'c4', 'u4', 'documents', 'inbox', '*.pdf', 10, '{"baseline":["a.pdf"]}');
            """);

        Migrate(migration);

        Assert.Equal(4L, Scalar("SELECT count(*) FROM triggers;"));
        AssertTimeTriggersKept();
        Assert.Equal("inbox|folder|30|Read the new files.|documents|inbox|*.pdf|10|{\"baseline\":[\"a.pdf\"]}|null",
            Row("SELECT name, kind, interval_sec, instruction, watch_root, watch_path, watch_glob, quiet_sec, watch_state, event_name FROM triggers WHERE id = 12;"));
        // a folder trigger saved before triggers could choose what fires them fires on any change; other kinds have no choice
        Assert.Equal("any", Scalar("SELECT fire_on FROM triggers WHERE id = 12;"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM triggers WHERE id <> 12 AND fire_on IS NOT NULL;"));
        Seed("INSERT INTO triggers (team_id, member_id, name, kind, event_name, instruction, created_at, updated_at) VALUES (1, 10, 'prs', 'event', 'web.pr*', 'x', 'now', 'now');");
        Assert.Equal(13L, Scalar("SELECT id FROM triggers WHERE kind = 'event';"));
        Seed("DELETE FROM members WHERE id = 10;");
        Assert.Equal(0L, Scalar("SELECT count(*) FROM triggers;"));
    }

    [Fact]
    public void Migration0047_MarksEveryCardThatWasThere_ForATimelineRebuild_AndNoCardMadeAfter()
    {
        const string migration = "0047_timelines";
        Before(migration);
        Seed("""
            INSERT INTO board_cards (id, team, member, item, title, body, status, lane, placed, awaiting_manager, workflow, created_at, updated_at, ended_at, ended_how)
                VALUES (3, 'Alpha', 'Ada', 7, 'Build it', 'the body', 'completed', 'done', 1, 0, 21, 'c1', 'u1', 'e1', 'completed');
            INSERT INTO board_cards (id, team, title, created_at, updated_at) VALUES (4, 'Alpha', 'Plan it', 'c2', 'u2');
            """);

        Migrate(migration);

        Assert.Equal(2L, Scalar("SELECT count(*) FROM board_cards;"));
        Assert.Equal("Alpha|Ada|7|Build it|the body|completed|done|1|0|c1|u1|e1|completed",
            Row("SELECT team, member, item, title, body, status, lane, placed, awaiting_manager, created_at, updated_at, ended_at, ended_how FROM board_cards WHERE id = 3;"));
        Assert.Equal("null|1|0", Row("SELECT started_at, timeline_rebuild, timeline_rebuilt FROM board_cards WHERE id = 3;"));
        Assert.Equal("null|1|0", Row("SELECT started_at, timeline_rebuild, timeline_rebuilt FROM board_cards WHERE id = 4;"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM board_timeline;"));
        Seed("INSERT INTO board_cards (team, title, created_at, updated_at) VALUES ('Alpha', 'New', 'now', 'now');");
        Assert.Equal(0L, Scalar("SELECT timeline_rebuild FROM board_cards WHERE title = 'New';"));
    }

    [Fact]
    public void Migration0057_KeepsEveryConnection_ItsId_AndTheApisAndMcpServersThatUseIt()
    {
        const string migration = "0057_email_connections";
        Before(migration);
        Seed("""
            INSERT INTO connections (id, name, store_key, kind, fields, status, status_reason, tested_at, created_at, updated_at)
                VALUES (2, 'GitHub', 'conn-2', 'secret', '{"note":"token"}', 'works', NULL, 't1', 'c1', 'u1');
            INSERT INTO connections (id, name, store_key, kind, fields, status, status_reason, created_at, updated_at)
                VALUES (5, 'Drive', 'conn-5', 'oauth', '{"client":"abc"}', 'needs_reconnect', 'expired', 'c2', 'u2');
            INSERT INTO connections (id, name, store_key, kind, fields, created_at, updated_at) VALUES (8, 'Shop', 'conn-8', 'website', '{}', 'c3', 'u3');
            INSERT INTO connections (id, name, store_key, kind, fields, created_at, updated_at) VALUES (11, 'Gone', 'conn-11', 'secret', '{}', 'c4', 'u4');
            DELETE FROM connections WHERE id = 11;
            INSERT INTO apis (id, name, base_url, connection_id, created_at, updated_at) VALUES (1, 'github', 'https://api.example.test', 2, 'now', 'now');
            INSERT INTO apis (id, name, base_url, created_at, updated_at) VALUES (2, 'open', 'https://open.example.test', 'now', 'now');
            INSERT INTO mcp_servers (id, name, command, connection_id, created_at, updated_at) VALUES (1, 'drive', 'drive-mcp', 5, 'now', 'now');
            INSERT INTO mcp_servers (id, name, command, created_at, updated_at) VALUES (2, 'local', 'local-mcp', 'now', 'now');
            """);

        Migrate(migration);

        Assert.Equal(3L, Scalar("SELECT count(*) FROM connections;"));
        Assert.Equal("GitHub|conn-2|secret|{\"note\":\"token\"}|works|null|t1|c1|u1",
            Row("SELECT name, store_key, kind, fields, status, status_reason, tested_at, created_at, updated_at FROM connections WHERE id = 2;"));
        Assert.Equal("Drive|conn-5|oauth|{\"client\":\"abc\"}|needs_reconnect|expired|null|c2|u2",
            Row("SELECT name, store_key, kind, fields, status, status_reason, tested_at, created_at, updated_at FROM connections WHERE id = 5;"));
        Assert.Equal("Shop|conn-8|website|{}|untested", Row("SELECT name, store_key, kind, fields, status FROM connections WHERE id = 8;"));
        // dropping the old table clears what points at it: the references are put back, and an unset one stays unset
        Assert.Equal(2L, Scalar("SELECT connection_id FROM apis WHERE id = 1;"));
        Assert.IsType<DBNull>(Scalar("SELECT connection_id FROM apis WHERE id = 2;"));
        Assert.Equal(5L, Scalar("SELECT connection_id FROM mcp_servers WHERE id = 1;"));
        Assert.IsType<DBNull>(Scalar("SELECT connection_id FROM mcp_servers WHERE id = 2;"));
        // an id that was used and deleted stays used, and a mailbox can be saved
        Seed("INSERT INTO connections (name, store_key, kind, fields, created_at, updated_at) VALUES ('Mail', 'conn-m', 'email', '{}', 'now', 'now');");
        Assert.Equal(12L, Scalar("SELECT id FROM connections WHERE name = 'Mail';"));
        // deleting a connection still unsets it where it is used
        Seed("DELETE FROM connections WHERE id = 2;");
        Assert.IsType<DBNull>(Scalar("SELECT connection_id FROM apis WHERE id = 1;"));
    }

    [Fact]
    public void Migration0059_KeepsEveryRunAndItsSteps_WithTheirIds_WhenTheRunsTableIsRebuiltForWaits()
    {
        const string migration = "0059_script_waits";
        Before(migration);
        Seed("""
            INSERT INTO scripts (id, script_key, name, latest_version, created_at, updated_at) VALUES (7, 'poller', 'Poller', 1, 'then', 'then');
            INSERT INTO script_versions (script_id, version, name, document, ok, check_result, created_at) VALUES (7, 1, 'Poller', '{}', 1, '{}', 'then');
            INSERT INTO members (id, team_id, name, role, created_at, kind, script_id, script_version) VALUES (12, 1, 'Poller', 'member', 'then', 'script', 7, 1);
            INSERT INTO script_runs (id, team_id, member_id, script_id, script_version, trigger, input, settings, bindings, status, dry_run, test,
                                     outputs, failed_step_id, reason, checkpoint, started_at, ended_at)
                VALUES (40, 1, 12, 7, 1, 'tell', '{"text":"go"}', '{"a":1}', '{"b":2}', 'failed', 0, 1, '{"out":3}', 'fetch', 'no answer', '{"at":"fetch"}', 's1', 'e1');
            INSERT INTO script_runs (id, team_id, script_id, script_version, trigger, status, started_at) VALUES (41, 1, 7, 1, 'test', 'running', 's2');
            INSERT INTO script_runs (id, team_id, script_id, script_version, trigger, status, started_at) VALUES (45, 1, 7, 1, 'test', 'succeeded', 's3');
            DELETE FROM script_runs WHERE id = 45;
            INSERT INTO script_run_steps (id, run_id, kind, step_id, type, input, output, status, truncated, started_at, ended_at)
                VALUES (100, 40, 'step', 'fetch', 'http', '{"url":"x"}', '{"status":500}', 'failed', 1, 's1', 'e1');
            INSERT INTO script_run_steps (id, run_id, kind, status, started_at) VALUES (101, 40, 'log', 'succeeded', 's1');
            INSERT INTO script_run_steps (id, run_id, kind, step_id, type, status, started_at) VALUES (102, 41, 'step', 'first', 'tell', 'running', 's2');
            INSERT INTO script_run_steps (id, run_id, status, started_at) VALUES (109, 41, 'succeeded', 's2');
            DELETE FROM script_run_steps WHERE id = 109;
            """);

        Migrate(migration);

        // the runs and their steps are under today's names
        Assert.Equal(2L, Scalar("SELECT count(*) FROM workflow_runs;"));
        Assert.Equal("1|12|7|1|tell|{\"text\":\"go\"}|{\"a\":1}|{\"b\":2}|failed|0|1|{\"out\":3}|fetch|no answer|{\"at\":\"fetch\"}|s1|e1",
            Row("SELECT team_id, member_id, workflow_id, workflow_version, trigger, input, settings, bindings, status, dry_run, test, outputs, failed_step_id, reason, checkpoint, started_at, ended_at FROM workflow_runs WHERE id = 40;"));
        Assert.Equal("1|null|test|running|s2|null", Row("SELECT team_id, member_id, trigger, status, started_at, ended_at FROM workflow_runs WHERE id = 41;"));
        Assert.Equal("null|null|null", Row("SELECT wait_kind, wait_deliverable_id, wait_until FROM workflow_runs WHERE id = 40;"));
        // dropping the old runs table takes the steps with it: they are put back with their own ids
        Assert.Equal(3L, Scalar("SELECT count(*) FROM workflow_run_steps;"));
        Assert.Equal("40|step|fetch|http|{\"url\":\"x\"}|{\"status\":500}|failed|1|s1|e1",
            Row("SELECT run_id, kind, step_id, type, input, output, status, truncated, started_at, ended_at FROM workflow_run_steps WHERE id = 100;"));
        Assert.Equal("40|log|succeeded", Row("SELECT run_id, kind, status FROM workflow_run_steps WHERE id = 101;"));
        Assert.Equal("41|step|first|tell|running", Row("SELECT run_id, kind, step_id, type, status FROM workflow_run_steps WHERE id = 102;"));
        // ids that were used and deleted stay used, for runs and for steps
        Seed("INSERT INTO workflow_runs (team_id, workflow_id, workflow_version, trigger, status, started_at) VALUES (1, 7, 1, 'test', 'waiting', 'now');");
        Assert.Equal(46L, Scalar("SELECT max(id) FROM workflow_runs;"));
        Seed("INSERT INTO workflow_run_steps (run_id, status, started_at) VALUES (46, 'running', 'now');");
        Assert.Equal(110L, Scalar("SELECT max(id) FROM workflow_run_steps;"));
        // the steps still go with their run, and a member of the time has no limit of its own on tells
        Assert.IsType<DBNull>(Scalar("SELECT workflow_max_tells FROM members WHERE id = 12;"));
        Seed("DELETE FROM workflow_runs WHERE id = 40;");
        Assert.Equal(1L, Scalar("SELECT count(*) FROM workflow_run_steps WHERE run_id <> 46;"));
    }
}
