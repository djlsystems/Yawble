using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Board;

/// <summary>One card on a team's board (0014_board.sql). ClearedAt (0037): the person hid this Done card from the board view.</summary>
public sealed record CardRow(
    long Id, string Team, string? Member, string? Item, string Title, string Body, string Status, string Lane,
    bool Placed, bool AwaitingManager, long? Workflow, string CreatedAt, string UpdatedAt, string? ClearedAt = null);

/// <summary>A line of a card's history: who did what, oldest first.</summary>
public sealed record TrailRow(long Id, string At, string Source, string Kind, string Text);

/// <summary>A backlog item; its label is B and the id, three digits at least (B007).</summary>
public sealed record ItemRow(
    long Id, string? Team, string Title, string Body, string State, double Position, string? ArchivedAt,
    string? DispatchedTeam, long? DispatchedWorkflow, string? DispatchedAt, string CreatedAt, string UpdatedAt)
{
    public string Label => BoardStore.Label(Id);
}

/// <summary>The board's cards and their trails, the backlog, and how far the projector has read the bus.</summary>
public sealed class BoardStore(string connectionString, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    public static string Label(long itemId) => "B" + itemId.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>B007, b7 or 7 as an item id; null when it is none of those.</summary>
    public static long? ItemId(string? label)
    {
        var s = (label ?? "").Trim();
        if (s.StartsWith('B') || s.StartsWith('b')) s = s[1..];
        return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    // ---------------------------------------------------------------- cards

    private const string CardColumns = "id, team, member, item, title, body, status, lane, placed, awaiting_manager, workflow, created_at, updated_at, cleared_at";

    private static CardRow ReadCard(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8) != 0, r.GetInt64(9) != 0,
        r.IsDBNull(10) ? null : r.GetInt64(10), r.GetString(11), r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13));

    public IReadOnlyList<CardRow> Cards(string? team = null) =>
        Query($"SELECT {CardColumns} FROM board_cards {(team is null ? "" : "WHERE team = $t COLLATE NOCASE")} ORDER BY updated_at DESC, id DESC;",
            ReadCard, team is null ? [] : [("$t", team)]);

    public CardRow? Card(long id) => Query($"SELECT {CardColumns} FROM board_cards WHERE id = $id;", ReadCard, ("$id", id)).FirstOrDefault();

    /// <summary>The member's card in this workflow (the newest, when there are several).</summary>
    public CardRow? CardOf(string team, string member, long workflow) =>
        Query($"SELECT {CardColumns} FROM board_cards WHERE team = $t COLLATE NOCASE AND member = $m COLLATE NOCASE AND workflow = $w ORDER BY id DESC LIMIT 1;",
            ReadCard, ("$t", team), ("$m", member), ("$w", workflow)).FirstOrDefault();

    /// <summary>The member's card it is most likely working on: its newest card not in Done.</summary>
    public CardRow? OpenCardOf(string team, string member) =>
        Query($"SELECT {CardColumns} FROM board_cards WHERE team = $t COLLATE NOCASE AND member = $m COLLATE NOCASE AND lane <> 'done' ORDER BY updated_at DESC, id DESC LIMIT 1;",
            ReadCard, ("$t", team), ("$m", member)).FirstOrDefault();

    public IReadOnlyList<CardRow> CardsOfWorkflow(long workflow) =>
        Query($"SELECT {CardColumns} FROM board_cards WHERE workflow = $w;", ReadCard, ("$w", workflow));

    public CardRow AddCard(string team, string? member, string? item, string title, string body, string status, string lane, long? workflow, string? at = null)
    {
        var now = at ?? Now();
        var id = Scalar(
            """
            INSERT INTO board_cards (team, member, item, title, body, status, lane, workflow, created_at, updated_at)
            VALUES ($t, $m, $i, $title, $body, $s, $l, $w, $now, $now) RETURNING id;
            """,
            ("$t", team), ("$m", (object?)member ?? DBNull.Value), ("$i", (object?)item ?? DBNull.Value), ("$title", Clip(title, 80)),
            ("$body", body), ("$s", status), ("$l", lane), ("$w", (object?)workflow ?? DBNull.Value), ("$now", now));
        return Card(id)!;
    }

    /// <summary>Raised when a cleared card un-clears itself by leaving Done (<see cref="Save"/>): open boards refresh.</summary>
    public Action? Uncleared { get; set; }

    /// <summary>
    /// Writes a card's changeable fields back, and its updated time. A card leaving Done is live work again: it is no
    /// longer cleared from the board (whoever moved it: the person, an agent's board tool, or the projector), and
    /// <see cref="Uncleared"/> says so when it was.
    /// </summary>
    public void Save(CardRow c, string? at = null)
    {
        var uncleared = c.Lane != "done"
            && Exec("UPDATE board_cards SET cleared_at = NULL WHERE id = $id AND cleared_at IS NOT NULL;", ("$id", c.Id)) > 0;
        Exec(
            """
            UPDATE board_cards SET member = $m, item = $i, title = $title, body = $body, status = $s, lane = $l, placed = $p,
                   awaiting_manager = $a, workflow = $w, updated_at = $now WHERE id = $id;
            """,
            ("$m", (object?)c.Member ?? DBNull.Value), ("$i", (object?)c.Item ?? DBNull.Value), ("$title", Clip(c.Title, 80)), ("$body", c.Body),
            ("$s", c.Status), ("$l", c.Lane), ("$p", c.Placed ? 1 : 0), ("$a", c.AwaitingManager ? 1 : 0),
            ("$w", (object?)c.Workflow ?? DBNull.Value), ("$now", at ?? Now()), ("$id", c.Id));
        if (uncleared) Uncleared?.Invoke();
    }

    // ---------------------------------------------------------------- clear done (0037)

    /// <summary>
    /// Hides the given cards from the board view: exactly the ones the person saw in Done. A card that is not in Done
    /// (any more) or is cleared already is left alone. Returns how many were hidden.
    /// </summary>
    public int ClearDone(IEnumerable<long> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return 0;
        var names = list.Select((_, i) => "$c" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        var args = list.Select((id, i) => (names[i], (object)id)).Append(("$now", (object)Now())).ToArray();
        return Exec($"UPDATE board_cards SET cleared_at = $now WHERE lane = 'done' AND cleared_at IS NULL AND id IN ({string.Join(", ", names)});", args);
    }

    /// <summary>Shows a cleared card on the board again. False when there is no such cleared card.</summary>
    public bool Restore(long id) =>
        Exec("UPDATE board_cards SET cleared_at = NULL WHERE id = $id AND cleared_at IS NOT NULL;", ("$id", id)) > 0;

    /// <summary>How many cards the team (or every team) has cleared from the board.</summary>
    public int ClearedCount(string? team = null) =>
        (int)Scalar($"SELECT count(*) FROM board_cards WHERE cleared_at IS NOT NULL {(team is null ? "" : "AND team = $t COLLATE NOCASE")};",
            team is null ? [] : [("$t", team)]);

    /// <summary>The team's cards wait on its Manager, or no longer do.</summary>
    public void SetAwaitingManager(string team, bool awaiting, long? cardId = null) =>
        Exec($"UPDATE board_cards SET awaiting_manager = $a WHERE team = $t COLLATE NOCASE {(cardId is null ? "" : "AND id = $id")};",
            cardId is null ? [("$a", awaiting ? 1 : 0), ("$t", team)] : [("$a", awaiting ? 1 : 0), ("$t", team), ("$id", cardId)]);

    public void AddTrail(long cardId, string source, string kind, string text, string? at = null) =>
        Exec("INSERT INTO board_trail (card_id, at, source, kind, text) VALUES ($c, $at, $s, $k, $x);",
            ("$c", cardId), ("$at", at ?? Now()), ("$s", source), ("$k", kind), ("$x", Clip(text, 2000)));

    public IReadOnlyList<TrailRow> Trail(long cardId) =>
        Query("SELECT id, at, source, kind, text FROM board_trail WHERE card_id = $c ORDER BY id;",
            r => new TrailRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$c", cardId));

    public int TrailCount(long cardId) => (int)Scalar("SELECT count(*) FROM board_trail WHERE card_id = $c;", ("$c", cardId));

    /// <summary>A deleted team's cards and their trails go with it.</summary>
    public void DeleteTeam(string team)
    {
        Exec("DELETE FROM board_trail WHERE card_id IN (SELECT id FROM board_cards WHERE team = $t COLLATE NOCASE);", ("$t", team));
        Exec("DELETE FROM board_cards WHERE team = $t COLLATE NOCASE;", ("$t", team));
    }

    /// <summary>A removed member's cards become unassigned.</summary>
    public void Unassign(string team, string member) =>
        Exec("UPDATE board_cards SET member = NULL WHERE team = $t COLLATE NOCASE AND member = $m COLLATE NOCASE;", ("$t", team), ("$m", member));

    // ---------------------------------------------------------------- backlog

    private const string ItemColumns = "id, team, title, body, state, position, archived_at, dispatched_team, dispatched_workflow, dispatched_at, created_at, updated_at";

    private static ItemRow ReadItem(SqliteDataReader r) => new(
        r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDouble(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10), r.GetString(11));

    public IReadOnlyList<ItemRow> Items(bool archived = false) =>
        Query($"SELECT {ItemColumns} FROM backlog_items WHERE archived_at IS {(archived ? "NOT NULL" : "NULL")} ORDER BY {(archived ? "archived_at DESC" : "position, id")};", ReadItem);

    public ItemRow? Item(long id) => Query($"SELECT {ItemColumns} FROM backlog_items WHERE id = $id;", ReadItem, ("$id", id)).FirstOrDefault();

    public ItemRow? ItemDispatchedAs(long workflow) =>
        Query($"SELECT {ItemColumns} FROM backlog_items WHERE dispatched_workflow = $w;", ReadItem, ("$w", workflow)).FirstOrDefault();

    public ItemRow AddItem(string title, string body, string? team)
    {
        var now = Now();
        var position = ScalarDouble("SELECT coalesce(max(position), 0) + 1 FROM backlog_items;");
        var id = Scalar("INSERT INTO backlog_items (team, title, body, position, created_at, updated_at) VALUES ($t, $title, $b, $p, $now, $now) RETURNING id;",
            ("$t", (object?)team ?? DBNull.Value), ("$title", Clip(title, 200)), ("$b", body), ("$p", position), ("$now", now));
        return Item(id)!;
    }

    public void SaveItem(ItemRow i) =>
        Exec(
            """
            UPDATE backlog_items SET team = $t, title = $title, body = $b, state = $s, position = $p, archived_at = $ar,
                   dispatched_team = $dt, dispatched_workflow = $dw, dispatched_at = $da, updated_at = $now WHERE id = $id;
            """,
            ("$t", (object?)i.Team ?? DBNull.Value), ("$title", Clip(i.Title, 200)), ("$b", i.Body), ("$s", i.State), ("$p", i.Position),
            ("$ar", (object?)i.ArchivedAt ?? DBNull.Value), ("$dt", (object?)i.DispatchedTeam ?? DBNull.Value),
            ("$dw", (object?)i.DispatchedWorkflow ?? DBNull.Value), ("$da", (object?)i.DispatchedAt ?? DBNull.Value), ("$now", Now()), ("$id", i.Id));

    public bool DeleteItem(long id)
    {
        if (Item(id) is not { ArchivedAt: not null }) return false;
        Exec("DELETE FROM backlog_dispatches WHERE item_id = $id;", ("$id", id));
        return Exec("DELETE FROM backlog_items WHERE id = $id AND archived_at IS NOT NULL;", ("$id", id)) > 0;
    }

    /// <summary>Puts a live item between two others (drag and drop): the midpoint of their positions.</summary>
    public void PlaceItem(long id, long? after, long? before)
    {
        var a = after is { } x ? Item(x)?.Position : null;
        var b = before is { } y ? Item(y)?.Position : null;
        var position = (a, b) switch
        {
            ({ } pa, { } pb) => (pa + pb) / 2,
            ({ } pa, null) => pa + 1,
            (null, { } pb) => pb - 1,
            _ => ScalarDouble("SELECT coalesce(max(position), 0) + 1 FROM backlog_items;"),
        };
        Exec("UPDATE backlog_items SET position = $p, updated_at = $now WHERE id = $id;", ("$p", position), ("$now", Now()), ("$id", id));
    }

    // ---------------------------------------------------------------- dispatch history (0023)

    public sealed record DispatchRow(long Id, long ItemId, string Team, long Workflow, string DispatchedAt, string? DispatchedBy);

    public void AddDispatch(long itemId, string team, long workflow, string? by) =>
        Exec("INSERT INTO backlog_dispatches (item_id, team, workflow, dispatched_at, dispatched_by) VALUES ($i, $t, $w, $at, $by);",
            ("$i", itemId), ("$t", team), ("$w", workflow), ("$at", Now()), ("$by", (object?)by ?? DBNull.Value));

    public IReadOnlyList<DispatchRow> Dispatches(long itemId) =>
        Query("SELECT id, item_id, team, workflow, dispatched_at, dispatched_by FROM backlog_dispatches WHERE item_id = $i ORDER BY id DESC;",
            r => new DispatchRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)),
            ("$i", itemId));

    public int DispatchCount(long itemId) => (int)Scalar("SELECT count(*) FROM backlog_dispatches WHERE item_id = $i;", ("$i", itemId));

    /// <summary>What a workflow's runs used: runs, the members who ran, and model tokens (from the gateway's usage records).</summary>
    public sealed record WorkflowUsage(int Runs, IReadOnlyList<string> Members, long InputTokens, long OutputTokens, string? FirstStart, string? LastEnd);

    public WorkflowUsage UsageOf(long workflow)
    {
        var members = Query(
            "SELECT DISTINCT m.name FROM runs r JOIN members m ON m.id = r.member_id WHERE r.correlation_id = $w ORDER BY m.name;",
            r => r.GetString(0), ("$w", workflow));
        var runs = (int)Scalar("SELECT count(*) FROM runs WHERE correlation_id = $w;", ("$w", workflow));
        var tokens = Query(
            """
            SELECT coalesce(sum(u.input_tokens), 0) + coalesce(sum(u.cache_read_tokens), 0) + coalesce(sum(u.cache_write_tokens), 0),
                   coalesce(sum(u.output_tokens), 0)
            FROM usage_events u JOIN runs r ON r.id = u.run_number WHERE r.correlation_id = $w;
            """, r => (r.GetInt64(0), r.GetInt64(1)), ("$w", workflow)).FirstOrDefault();
        var span = Query("SELECT min(started_at), max(ended_at) FROM runs WHERE correlation_id = $w;",
            r => (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)), ("$w", workflow)).FirstOrDefault();
        return new WorkflowUsage(runs, members, tokens.Item1, tokens.Item2, span.Item1, span.Item2);
    }

    // ---------------------------------------------------------------- the projector's cursor

    public long Cursor() => Scalar("SELECT coalesce((SELECT seq FROM board_cursor WHERE id = 1), -1);");

    public void SetCursor(long seq) =>
        Exec("INSERT INTO board_cursor (id, seq) VALUES (1, $s) ON CONFLICT(id) DO UPDATE SET seq = excluded.seq;", ("$s", seq));

    // ---------------------------------------------------------------- plumbing

    private static string Clip(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    private SqliteCommand Command(SqliteConnection c, string sql, (string, object)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    private int Exec(string sql, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        return cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private double ScalarDouble(string sql, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        return Convert.ToDouble(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }
}
