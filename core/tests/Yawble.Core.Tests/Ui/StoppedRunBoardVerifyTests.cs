using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Board;
using Yawble.Core.Bus;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Scheduling;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>
/// TesterKofi's round 2 check of stopped runs on the board, against StoppedRunTests r2 (b864021): a stopped card has its own status
/// "stopped". Checked independently of the developer's own tests:
/// - a real engine stop: board.list says "stopped" (not running/queued, not Needs You), and a new instruction brings the
///   same card back to queued / running;
/// - only the stopped run's card is marked: the member's older card and other members' cards keep their status;
/// - Done and louder statuses keep theirs (Lanes.StopKeeps);
/// - migration 0048 on a real 0047-era database with data (an upgrade, not a re-run over the new schema).
/// </summary>
public class StoppedRunBoardVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    private static Message Tell(string member, string text, long wf, long seq, int min) =>
        new(seq, "agent.instruction.web/" + member, JsonSerializer.Serialize(new { text }), "web/Manager", wf, null, 0, T0.AddMinutes(min));

    private static Message From(string member, string type, object payload, long wf, long seq, int min) =>
        new(seq, type, JsonSerializer.Serialize(payload), "web/" + member, wf, null, 0, T0.AddMinutes(min));

    private static Message Stop(string member, long wf, long seq, int min) =>
        From(member, MessageTypes.Failed, new { reason = BusEngine.StoppedReason, failureClass = FailureClasses.Stopped }, wf, seq, min);

    // ------------------------------------------------------------------ the real engine, through board.list

    [Fact]
    public async Task ARealStop_ReadsStoppedOnTheBoard_AndTheManagersNewInstructionRunsTheSameCardAgain()
    {
        using var db = new TempDb();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScriptedRunner();
        runner.On("Alpha/stop", async (run, ct) => { await Task.Delay(Timeout.Infinite, ct); return RunResult.Ok; })
            .On("Alpha/fine", (run, ct) => Task.FromResult(RunResult.Ok))
            // The Manager, woken by the stop notice, tells the member again in the same deliverable (once the test has looked).
            .On("Alpha/Manager", async (run, ct) =>
            {
                if (run.Items.Any(i => i.Type == MessageTypes.Failed && i.Source == "Alpha/stop"))
                {
                    await gate.Task.WaitAsync(ct);
                    await run.TellAsync("stop", "carry on", ct);
                }
                return RunResult.Ok;
            });
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { MaxConcurrentAgents = () => 3, SafetyPoll = TimeSpan.FromSeconds(30) });
        try
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            foreach (var n in new[] { "stop", "fine" }) await engine.AddMemberAsync("Alpha", n, ct: Ct);
            foreach (var n in new[] { "stop", "fine" }) await engine.PersonTellAsync("Alpha", n, "job " + n, ct: Ct);
            await engine.StartAsync(Ct);
            await Wait.UntilAsync(() => runner.AdmittedNames().Contains("Alpha/stop"), "stop running");
            var board = new BoardService(new BoardStore(db.ConnectionString), engine);
            await Wait.UntilAsync(async () => (await Of("stop"))["status"]!.GetValue<string>() == "running", "card says running while it runs");
            var id = (await Of("stop"))["id"]!.GetValue<long>();
            // While it runs, board.list SHOWS the queued card running in In Progress (BoardService, display only). This script
            // sends no progress line, so its stored lane stays To Do and that is the lane the stopped card keeps (observed
            // in the tester's verify notes: the card moves back from In Progress to To Do on the stop).
            Assert.Equal(Lanes.InProgress, (await Of("stop"))["lane"]!.GetValue<string>());

            await Wait.UntilAsync(async () => await engine.StopMemberAsync("Alpha", "stop", Ct), "stop asked");
            await Wait.UntilAsync(async () => (await Card(id))["status"]!.GetValue<string>() == "stopped", "card says stopped", TimeSpan.FromSeconds(20));
            await Wait.UntilAsync(() => runner.AdmittedNames().Contains("Alpha/fine"), "fine ran", TimeSpan.FromSeconds(20));

            var stopped = await Card(id);
            Assert.Equal(("stopped", Lanes.Todo), (stopped["status"]!.GetValue<string>(), stopped["lane"]!.GetValue<string>())); // keeps its lane
            Assert.Equal("stopped", stopped["memberState"]!["attention"]!.GetValue<string>());
            await Wait.UntilAsync(async () => (await Of("fine"))["status"]!.GetValue<string>() is "completed" or "done", "fine finished");

            // The Manager's new instruction, same deliverable: the same card leaves "stopped" and runs again.
            gate.SetResult();
            await Wait.UntilAsync(() => runner.AdmittedNames().Count(n => n == "Alpha/stop") == 2, "stop running again", TimeSpan.FromSeconds(20));
            await Wait.UntilAsync(async () => (await Card(id))["status"]!.GetValue<string>() == "running", "card says running again", TimeSpan.FromSeconds(20));
            var again = await Card(id);
            Assert.Equal(Lanes.InProgress, again["lane"]!.GetValue<string>());
            Assert.Single(await All(), n => n["member"]!.GetValue<string>() == "stop"); // no second card

            async Task<List<JsonObject>> All() =>
                (await board.DispatchAsync("board.list", new JsonObject { ["team"] = "Alpha" }, Ct))!["cards"]!.AsArray().Select(n => n!.AsObject()).ToList();
            async Task<JsonObject> Of(string member) => (await All()).Single(n => n["member"]!.GetValue<string>() == member);
            async Task<JsonObject> Card(long cardId) => (await All()).Single(n => n["id"]!.GetValue<long>() == cardId);
        }
        finally { gate.TrySetResult(); await engine.DisposeAsync(); }
    }

    // ------------------------------------------------------------------ the projector

    [Fact]
    public void AStopMarksOnlyThatRunsCard_NotTheMembersOlderCard()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        p.Apply(Tell("Tomas", "Old work", 4, 1, 0));
        p.Apply(From("Tomas", MessageTypes.Progress, new { status = "older" }, 4, 2, 1));   // older card, still running in wf 4
        p.Apply(Tell("Tomas", "New work", 5, 3, 2));
        p.Apply(From("Tomas", MessageTypes.Progress, new { status = "coding" }, 5, 4, 3));
        p.Apply(Tell("Ines", "Other work", 5, 5, 3));
        p.Apply(Stop("Tomas", 5, 6, 4));

        var old = store.CardOf("web", "Tomas", 4)!;
        var cur = store.CardOf("web", "Tomas", 5)!;
        var ines = store.CardOf("web", "Ines", 5)!;
        Assert.Equal(("running", Lanes.InProgress), (old.Status, old.Lane));
        Assert.Equal(("stopped", Lanes.InProgress), (cur.Status, cur.Lane));
        Assert.Equal("queued", ines.Status);
        Assert.Equal("stopped", store.Trail(cur.Id).Last().Kind);
    }

    [Fact]
    public void AStopBeforeAnyProgress_IsStoppedInToDo_AndNewWorkReQueuesThenRuns()
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        p.Apply(Tell("Tomas", "Build it", 5, 1, 0));
        p.Apply(Stop("Tomas", 5, 2, 1));
        var card = store.CardOf("web", "Tomas", 5)!;
        Assert.Equal(("stopped", Lanes.Todo), (card.Status, card.Lane));
        Assert.Equal(Lanes.InProgress, Lanes.Of("stopped"));

        p.Apply(Tell("Tomas", "Carry on", 5, 3, 5));
        card = store.Card(card.Id)!;
        Assert.Equal(("queued", Lanes.Todo), (card.Status, card.Lane));
        p.Apply(From("Tomas", MessageTypes.Progress, new { status = "again" }, 5, 4, 6));
        card = store.Card(card.Id)!;
        Assert.Equal(("running", Lanes.InProgress), (card.Status, card.Lane));
        Assert.Single(store.Cards("web")); // the same card, no new one
    }

    [Theory]
    [InlineData("needs-decision", MessageTypes.NeedsDecision)]
    [InlineData("blocked", MessageTypes.Blocked)]
    public void ALouderStatus_IsNotOverwrittenByAStop(string status, string type)
    {
        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        p.Apply(Tell("Tomas", "Build it", 5, 1, 0));
        p.Apply(From("Tomas", type, new { question = "which?", reason = "no access" }, 5, 2, 1));
        p.Apply(Stop("Tomas", 5, 3, 2));
        var card = store.CardOf("web", "Tomas", 5)!;
        Assert.Equal((status, Lanes.NeedsYou), (card.Status, card.Lane));
    }

    [Fact]
    public void ADoneCard_KeepsItsStatus_OnAStop()
    {
        Assert.True(Lanes.StopKeeps("handback", Lanes.Done));
        Assert.True(Lanes.StopKeeps("failed", Lanes.NeedsYou));
        Assert.False(Lanes.StopKeeps("running", Lanes.InProgress));
        Assert.False(Lanes.StopKeeps("queued", Lanes.Todo));

        using var db = new TempDb();
        var store = new BoardStore(db.ConnectionString);
        var p = new BoardProjector(store);
        p.Apply(Tell("Tomas", "Build it", 5, 1, 0));
        p.Apply(From("Tomas", MessageTypes.Handback, new { delivered = "done" }, 5, 2, 1));
        var before = store.CardOf("web", "Tomas", 5)!;
        p.Apply(Stop("Tomas", 5, 3, 2));
        var after = store.Card(before.Id)!;
        Assert.Equal((before.Status, Lanes.Done), (after.Status, after.Lane));
    }

    // ------------------------------------------------------------------ migration 0048 as an upgrade

    /// <summary>
    /// A database made by the migrations up to 0047 (board_cards as 0014 + 0037/0045/0047 columns, its old CHECK), with
    /// cards, trail lines, timeline rows and a deleted card, upgraded by the real migrator.
    /// </summary>
    [Fact]
    public void Migration0048_UpgradesA0047DatabaseWithData_KeepingEverything()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-0048-").FullName;
        var cs = $"Data Source={Path.Combine(dir, "old.db")};Pooling=False";
        try
        {
            using (var c = Open(cs))
            {
                Exec(c, "CREATE TABLE IF NOT EXISTS schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT;");
                foreach (var name in SchemaMigrator.Available.Where(n => string.CompareOrdinal(n, "0048") < 0))
                {
                    using var tx = c.BeginTransaction();
                    Exec(c, Embedded(name), tx);
                    Exec(c, $"INSERT INTO schema_migrations (name, applied_at) VALUES ('{name}', 't');", tx);
                    tx.Commit();
                }
                // The old CHECK refuses 'stopped': this is the schema before the fix.
                // (Its column is still named workflow here: 0060 renames it to deliverable.)
                Assert.Throws<SqliteException>(() => Exec(c, "INSERT INTO board_cards (team, title, status, created_at, updated_at) VALUES ('w','x','stopped','t','t');"));
                Exec(c, """
                    INSERT INTO board_cards (team, member, item, title, body, status, lane, placed, awaiting_manager, workflow, created_at, updated_at,
                                             cleared_at, ended_at, ended_how, started_at, timeline_rebuild, timeline_rebuilt)
                        VALUES ('web', 'Tomas', 'B007', 'A', 'body a', 'running', 'in-progress', 0, 1, 5, 't0', 't1', NULL, NULL, NULL, 't0', 0, 1),
                               ('web', 'Ines', NULL, 'B', '', 'failed', 'blocked', 1, 0, 5, 't0', 't2', NULL, 't3', 'expired', NULL, 1, 0),
                               ('web', 'Kofi', NULL, 'C', '', 'queued', 'todo', 0, 0, 6, 't0', 't1', NULL, NULL, NULL, NULL, 0, 0),
                               ('web', NULL, NULL, 'D', '', 'done', 'done', 0, 0, NULL, 't0', 't4', 't5', NULL, NULL, NULL, 0, 0);
                    INSERT INTO board_trail (card_id, at, source, kind, text) VALUES
                        (1, 't0', 'Manager', 'told', 'Build it'), (1, 't1', 'Tomas', 'progress', 'coding'),
                        (2, 't1', 'Ines', 'failed', 'timeout'), (3, 't0', 'Manager', 'told', 'Test'), (4, 't0', 'you', 'comment', 'ok');
                    INSERT INTO board_timeline (card_id, workflow, member, kind, at) SELECT 1, 5, 'Tomas', 'state', 't0';
                    DELETE FROM board_cards WHERE id = 3;
                    """);
            }
            string Dump(SqliteConnection c, string sql) => string.Join("\n", Rows(c, sql));
            string cardsBefore, trailBefore, timelineBefore, seqBefore;
            using (var c = Open(cs))
            {
                cardsBefore = Dump(c, "SELECT * FROM board_cards ORDER BY id;");
                trailBefore = Dump(c, "SELECT * FROM board_trail ORDER BY id;");
                timelineBefore = Dump(c, "SELECT * FROM board_timeline ORDER BY id;");
                seqBefore = Dump(c, "SELECT name, seq FROM sqlite_sequence WHERE name LIKE 'board_%' ORDER BY name;");
            }
            Assert.Equal(4, trailBefore.Split('\n').Length); // card 3's trail cascaded on the old schema

            string? firstPending = null;
            var applied = SchemaMigrator.Migrate(cs, (_, name) => firstPending = name);
            Assert.Equal("0048_board_card_stopped", firstPending); // the backup hook runs before the rebuild
            Assert.Contains("0048_board_card_stopped", applied);

            using var db = Open(cs);
            Assert.Equal(cardsBefore, Dump(db, "SELECT * FROM board_cards ORDER BY id;"));
            Assert.Equal(trailBefore, Dump(db, "SELECT * FROM board_trail ORDER BY id;"));
            Assert.Equal(timelineBefore, Dump(db, "SELECT * FROM board_timeline ORDER BY id;"));
            Assert.Equal(seqBefore, Dump(db, "SELECT name, seq FROM sqlite_sequence WHERE name LIKE 'board_%' ORDER BY name;"));
            Assert.Equal(["board_cards_member", "board_cards_team", "board_trail_card"],
                Rows(db, "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name IN ('board_cards', 'board_trail') AND sql IS NOT NULL ORDER BY name;"));
            Assert.Equal(["ok"], Rows(db, "PRAGMA integrity_check;"));
            Assert.Empty(Rows(db, "PRAGMA foreign_key_check;"));
            Assert.Empty(Rows(db, "SELECT name FROM sqlite_master WHERE name LIKE '%0048%';"));
            Assert.Empty(Rows(db, "SELECT name FROM sqlite_temp_master;"));

            // The app's own store and projector work on the upgraded db: a stop saves "stopped".
            var store = new BoardStore(cs);
            var card = store.Card(1)!;
            Assert.Equal(("A", "B007", 5L, true), (card.Title, card.Item, card.Deliverable!.Value, card.AwaitingManager));
            new BoardProjector(store).Apply(Stop("Tomas", 5, 1, 1));
            Assert.Equal(("stopped", Lanes.InProgress), (store.Card(1)!.Status, store.Card(1)!.Lane));
            Assert.Throws<SqliteException>(() => Exec(db, "UPDATE board_cards SET status = 'halted' WHERE id = 1;"));
            Assert.Equal(5L, store.AddCard("web", null, null, "E", "", "queued", Lanes.Todo, null).Id); // id 3 and 4 never reused

            // Running the migrator again changes nothing.
            var again = SchemaMigrator.Migrate(cs);
            Assert.Equal(applied.Count, again.Count);
            Exec(db, "DELETE FROM board_cards WHERE id = 1;");
            Assert.Equal(0L, (long)Scalar(db, "SELECT count(*) FROM board_trail WHERE card_id = 1;")!); // still cascades
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    private static string Embedded(string name)
    {
        using var s = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Yawble.Core.Migrations." + name + ".sql")!;
        return new StreamReader(s).ReadToEnd();
    }

    private static SqliteConnection Open(string cs)
    {
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static List<string> Rows(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "<null>" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return rows;
    }
}
