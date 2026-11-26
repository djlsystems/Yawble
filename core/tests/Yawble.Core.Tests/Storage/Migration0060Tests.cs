using Microsoft.Data.Sqlite;
using Xunit;
using Yawble.Core.Storage;
using Yawble.Core.Workflows.Running;

namespace Yawble.Core.Tests.Storage;

/// <summary>
/// Migration 0060: deliverables (were workflows) and workflows (were scripts). A database at 0059 with old rows in it is
/// brought up to date: tables and columns renamed, the two rebuilt tables keep their rows, ids, counters and children,
/// and stored kinds, event types and keys are rewritten. Fakes only: a temp SQLite file.
/// </summary>
public sealed class Migration0060Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("yawble-0060-").FullName;
    private string Cs => $"Data Source={Path.Combine(_dir, "yawble.db")};Pooling=False";

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A database with every migration before 0060 applied (as an older app left it).</summary>
    private void At0059()
    {
        using var c = Open();
        Exec(c, "CREATE TABLE schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT;");
        foreach (var name in SchemaMigrator.Available.Where(n => string.CompareOrdinal(n, "0060") < 0))
        {
            using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Yawble.Core.Migrations." + name + ".sql")!;
            Exec(c, new StreamReader(stream).ReadToEnd());
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

    private void Seed()
    {
        using var c = Open();
        Exec(c, "PRAGMA foreign_keys=ON;");
        Exec(c, """
            INSERT INTO teams (id, name, created_at) VALUES (1, 'Alpha', 'now');
            INSERT INTO scripts (id, script_key, name, latest_version, created_at, updated_at) VALUES (7, 'poller', 'Poller', 1, 'now', 'now');
            INSERT INTO script_versions (script_id, version, name, document, ok, check_result, created_at)
                VALUES (7, 1, 'Poller', '{"format":"yawble-script/1","steps":[{"text":"${steps.ask.workflow}"}]}', 1, '{}', 'now');
            INSERT INTO members (id, team_id, name, role, created_at) VALUES (10, 1, 'Ada', 'member', 'now');
            INSERT INTO members (id, team_id, name, role, created_at, kind, script_id, script_version, script_timeout_min, script_max_tells)
                VALUES (12, 1, 'Poller', 'member', 'now', 'script', 7, 1, 5, 3);
            INSERT INTO script_member_state (member_id, variables, updated_at) VALUES (12, '{"after":"9"}', 'now');
            INSERT INTO script_runs (id, team_id, member_id, script_id, script_version, trigger, status, started_at, wait_kind, wait_workflow_id)
                VALUES (40, 1, 12, 7, 1, 'tell', 'waiting', 'now', 'workflow', 812);
            INSERT INTO script_run_steps (run_id, kind, step_id, type, output, status, started_at)
                VALUES (40, 'step', 'ask', 'tell', '{"outcome":"sent","workflow":812}', 'succeeded', 'now');
            INSERT INTO run_records (run_key, team, member, cli, kind, backend, started_at) VALUES ('r-1', 'Alpha', 'Ada', 'claude', 'headless', 'x', 'now');
            INSERT INTO run_records (run_key, team, member, cli, kind, backend, started_at) VALUES ('script:40', 'Alpha', 'Poller', 'script', 'script', 'script', 'now');
            INSERT INTO messages (type, payload, source, depth, occurred_at) VALUES ('workflow.completed', '{"summary":"the workflow is done"}', 'Alpha/Manager', 0, 'now');
            INSERT INTO messages (type, payload, source, depth, occurred_at)
                VALUES ('agent.needs_decision', '{"kind":"workflow.closeOverFail","idleWorkflowOffer":true}', 'Alpha/Manager', 0, 'now');
            INSERT INTO team_settings (team_id, workflow_expiry_days, updated_at) VALUES (1, 9, 'now');
            INSERT INTO app_defaults (key, value, updated_at) VALUES ('scripts.tellOtherTeams', 'true', 'now');
            """);
        // ids that were used and deleted stay used: the AUTOINCREMENT counter is past them
        Exec(c, "INSERT INTO members (id, team_id, name, role, created_at) VALUES (30, 1, 'Gone', 'member', 'now'); DELETE FROM members WHERE id = 30;");
    }

    [Fact]
    public void RenamesTablesAndColumns_KeepsEveryRowAndChild_AndRewritesStoredKinds()
    {
        At0059();
        Seed();
        var membersSeq = Scalar("SELECT seq FROM sqlite_sequence WHERE name = 'members';");

        var applied = SchemaMigrator.Migrate(Cs);
        Assert.Contains("0060_deliverables_and_workflows", applied);

        foreach (var gone in new[] { "scripts", "script_versions", "script_runs", "script_run_steps", "script_member_state", "workflow_expiry" })
            Assert.Equal(0L, Scalar($"SELECT count(*) FROM sqlite_master WHERE name = '{gone}';"));
        foreach (var table in new[] { "workflows", "workflow_versions", "workflow_runs", "workflow_run_steps", "workflow_member_state", "deliverable_expiry" })
            Assert.Equal(1L, Scalar($"SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_master WHERE name LIKE 'script%' OR name LIKE '%\\_script%' ESCAPE '\\' OR name LIKE '%workflow_expiry%' OR name LIKE '%timeline_workflow%';"));

        // the rebuilt members table: rows, ids, kind, renamed columns, the counter, and its child row (no cascade)
        Assert.Equal(2L, Scalar("SELECT count(*) FROM members;"));
        Assert.Equal("workflow", Scalar("SELECT kind FROM members WHERE id = 12;"));
        Assert.Equal(7L, Scalar("SELECT workflow_id FROM members WHERE id = 12;"));
        Assert.Equal(3L, Scalar("SELECT workflow_max_tells FROM members WHERE id = 12;"));
        Assert.Equal(membersSeq, Scalar("SELECT seq FROM sqlite_sequence WHERE name = 'members';"));
        Assert.Equal("{\"after\":\"9\"}", Scalar("SELECT variables FROM workflow_member_state WHERE member_id = 12;"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM workflow_runs WHERE member_id = 12;"));

        // run records
        Assert.Equal("workflow", Scalar("SELECT kind FROM run_records WHERE member = 'Poller';"));
        Assert.Equal("workflow:40", Scalar("SELECT run_key FROM run_records WHERE member = 'Poller';"));
        Assert.Equal("headless", Scalar("SELECT kind FROM run_records WHERE member = 'Ada';"));

        // a waiting run waits on a deliverable now; the tell's result names it
        Assert.Equal("deliverable", Scalar("SELECT wait_kind FROM workflow_runs WHERE id = 40;"));
        Assert.Equal(812L, Scalar("SELECT wait_deliverable_id FROM workflow_runs WHERE id = 40;"));
        Assert.Equal("{\"outcome\":\"sent\",\"deliverable\":812}", Scalar("SELECT output FROM workflow_run_steps WHERE run_id = 40;"));
        Assert.Equal("{\"format\":\"yawble-workflow/1\",\"steps\":[{\"text\":\"${steps.ask.deliverable}\"}]}",
            Scalar("SELECT document FROM workflow_versions WHERE workflow_id = 7;"));

        // stored event types, decision kinds and keys; free text stays as written
        Assert.Equal(1L, Scalar("SELECT count(*) FROM messages WHERE type = 'deliverable.completed';"));
        Assert.Equal("{\"summary\":\"the workflow is done\"}", Scalar("SELECT payload FROM messages WHERE type = 'deliverable.completed';"));
        Assert.Equal("{\"kind\":\"deliverable.closeOverFail\",\"idleDeliverableOffer\":true}", Scalar("SELECT payload FROM messages WHERE type = 'agent.needs_decision';"));
        Assert.Equal(9L, Scalar("SELECT deliverable_expiry_days FROM team_settings WHERE team_id = 1;"));
        Assert.Equal("true", Scalar("SELECT value FROM app_defaults WHERE key = 'workflows.tellOtherTeams';"));

        // the keys are back on and hold: a member's run may not name a missing workflow
        using var c = Open();
        Exec(c, "PRAGMA foreign_keys=ON;");
        Assert.Throws<SqliteException>(() => Exec(c, "INSERT INTO workflow_runs (team_id, member_id, workflow_id, workflow_version, trigger, status, started_at) VALUES (1, 12, 99, 1, 'x', 'running', 'now');"));
        Assert.Equal("ok", Scalar("PRAGMA integrity_check;"));
    }

    [Fact]
    public void TheOldWorkflowFilesFolderMovesOnce_AndNeverOverANewerOne()
    {
        var old = Directory.CreateDirectory(Path.Combine(_dir, "scripts", "members", "12")).FullName;
        File.WriteAllText(Path.Combine(old, "state.txt"), "kept");
        Assert.True(WorkflowFolders.MoveOldFolder(_dir));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(_dir, "workflows", "members", "12", "state.txt")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "scripts")));

        Directory.CreateDirectory(Path.Combine(_dir, "scripts"));
        Assert.False(WorkflowFolders.MoveOldFolder(_dir)); // the new folder is there: the old one is left alone
        Assert.True(Directory.Exists(Path.Combine(_dir, "scripts")));
    }
}
