using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Board;

/// <summary>
/// One card on a team's board (0014_board.sql). ClearedAt (0037): the person hid this Done card from the board view.
/// EndedAt / EndedHow (0045): when its deliverable's end ended it, and how (completed, closed or expired); null while it is live.
/// StartedAt / Rebuilt (0047): when it first had a member and was queued or told; its timeline was rebuilt from its trail.
/// </summary>
public sealed record CardRow(
    long Id, string Team, string? Member, string? Item, string Title, string Body, string Status, string Lane,
    bool Placed, bool AwaitingManager, long? Deliverable, string CreatedAt, string UpdatedAt, string? ClearedAt = null,
    string? EndedAt = null, string? EndedHow = null, string? StartedAt = null, bool Rebuilt = false);

/// <summary>
/// A line of a card's (or a card-less member's) timeline (0047): a state, a stall, a hold or a marker.
/// </summary>
public sealed record TimelineLine(long Id, long? CardId, long? Deliverable, string? Member, string Kind, string At, string? Until,
    string? Category, bool Done, string? Marker, bool Rebuilt);

/// <summary>A line of a card's history: who did what, oldest first.</summary>
public sealed record TrailRow(long Id, string At, string Source, string Kind, string Text);

/// <summary>A backlog item; its label is B and the id, three digits at least (B007).</summary>
public sealed record ItemRow(
    long Id, string? Team, string Title, string Body, string State, double Position, string? ArchivedAt,
    string? DispatchedTeam, long? DispatchedDeliverable, string? DispatchedAt, string CreatedAt, string UpdatedAt)
{
    public string Label => BoardStore.Label(Id);
}

/// <summary>The board's cards and their trails, the backlog, and how far the projector has read the bus.</summary>
public sealed partial class BoardStore(string connectionString, TimeProvider? clock = null)
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

    private const string CardColumns = "id, team, member, item, title, body, status, lane, placed, awaiting_manager, deliverable, created_at, updated_at, cleared_at, ended_at, ended_how, started_at, timeline_rebuilt";

    private static CardRow ReadCard(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8) != 0, r.GetInt64(9) != 0,
        r.IsDBNull(10) ? null : r.GetInt64(10), r.GetString(11), r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13),
        r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15),
        r.IsDBNull(16) ? null : r.GetString(16), r.GetInt64(17) != 0);

    public IReadOnlyList<CardRow> Cards(string? team = null) =>
        Query($"SELECT {CardColumns} FROM board_cards {(team is null ? "" : "WHERE team = $t COLLATE NOCASE")} ORDER BY updated_at DESC, id DESC;",
            ReadCard, team is null ? [] : [("$t", team)]);

    public CardRow? Card(long id) => Query($"SELECT {CardColumns} FROM board_cards WHERE id = $id;", ReadCard, ("$id", id)).FirstOrDefault();

    /// <summary>The member's card in this deliverable (the newest, when there are several).</summary>
    public CardRow? CardOf(string team, string member, long deliverable) =>
        Query($"SELECT {CardColumns} FROM board_cards WHERE team = $t COLLATE NOCASE AND member = $m COLLATE NOCASE AND deliverable = $w ORDER BY id DESC LIMIT 1;",
            ReadCard, ("$t", team), ("$m", member), ("$w", deliverable)).FirstOrDefault();

    /// <summary>The member's card it is most likely working on: its newest card not in Done.</summary>
    public CardRow? OpenCardOf(string team, string member) =>
        Query($"SELECT {CardColumns} FROM board_cards WHERE team = $t COLLATE NOCASE AND member = $m COLLATE NOCASE AND lane <> 'done' ORDER BY updated_at DESC, id DESC LIMIT 1;",
            ReadCard, ("$t", team), ("$m", member)).FirstOrDefault();

    public IReadOnlyList<CardRow> CardsOfDeliverable(long deliverable) =>
        Query($"SELECT {CardColumns} FROM board_cards WHERE deliverable = $w;", ReadCard, ("$w", deliverable));

    public CardRow AddCard(string team, string? member, string? item, string title, string body, string status, string lane, long? deliverable, string? at = null)
    {
        var now = at ?? Now();
        var id = Scalar(
            """
            INSERT INTO board_cards (team, member, item, title, body, status, lane, deliverable, created_at, updated_at)
            VALUES ($t, $m, $i, $title, $body, $s, $l, $w, $now, $now) RETURNING id;
            """,
            ("$t", team), ("$m", (object?)member ?? DBNull.Value), ("$i", (object?)item ?? DBNull.Value), ("$title", Clip(title, 80)),
            ("$body", body), ("$s", status), ("$l", lane), ("$w", (object?)deliverable ?? DBNull.Value), ("$now", now));
        var card = Card(id)!;
        RecordState(card, now);
        return Card(id)!;
    }

    /// <summary>Raised when a cleared card un-clears itself by leaving Done (<see cref="Save"/>): open boards refresh.</summary>
    public Action? Uncleared { get; set; }

    /// <summary>
    /// Writes a card's changeable fields back, and its updated time. A card leaving Done is live work again: it is no
    /// longer cleared from the board (whoever moved it: the person, an agent's board tool, or the projector), and
    /// <see cref="Uncleared"/> says so when it was. A change of the card's time category is a line of its timeline (0047),
    /// at <paramref name="stateAt"/> when given (an expired deliverable's cards end at its last activity), else at the save's time.
    /// </summary>
    public void Save(CardRow c, string? at = null, string? stateAt = null)
    {
        EnsureTimeline(c.Id);
        var uncleared = c.Lane != "done"
            && Exec("UPDATE board_cards SET cleared_at = NULL WHERE id = $id AND cleared_at IS NOT NULL;", ("$id", c.Id)) > 0;
        Exec(
            """
            UPDATE board_cards SET member = $m, item = $i, title = $title, body = $body, status = $s, lane = $l, placed = $p,
                   awaiting_manager = $a, deliverable = $w, updated_at = $now WHERE id = $id;
            """,
            ("$m", (object?)c.Member ?? DBNull.Value), ("$i", (object?)c.Item ?? DBNull.Value), ("$title", Clip(c.Title, 80)), ("$body", c.Body),
            ("$s", c.Status), ("$l", c.Lane), ("$p", c.Placed ? 1 : 0), ("$a", c.AwaitingManager ? 1 : 0),
            ("$w", (object?)c.Deliverable ?? DBNull.Value), ("$now", at ?? Now()), ("$id", c.Id));
        RecordState(c, stateAt ?? at ?? Now());
        if (uncleared) Uncleared?.Invoke();
    }

    // ---------------------------------------------------------------- timelines (0047)

    private const string TimelineColumns = "id, card_id, deliverable, member, kind, at, until, category, done, marker, rebuilt";

    private static TimelineLine ReadLine(SqliteDataReader r) => new(
        r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.GetInt64(8) != 0, r.IsDBNull(9) ? null : r.GetString(9), r.GetInt64(10) != 0);

    /// <summary>
    /// The card's category now, as a state line, when it differs from its last one (a Scheduled line in between does not
    /// count: saving the same state again leaves the wake standing). Also its started time: the first save with a member
    /// and queued.
    /// </summary>
    private void RecordState(CardRow c, string at)
    {
        var category = TimeCategories.OfState(c.Member, c.Status, c.Lane);
        var done = c.Lane == Lanes.Done;
        var lastBase = Query($"SELECT {TimelineColumns} FROM board_timeline WHERE card_id = $c AND kind = 'state' AND category <> $s ORDER BY at DESC, id DESC LIMIT 1;",
            ReadLine, ("$c", c.Id), ("$s", TimeCategories.Scheduled)).FirstOrDefault();
        if (lastBase is null || lastBase.Category != category || lastBase.Done != done
            || !string.Equals(lastBase.Member, c.Member, StringComparison.OrdinalIgnoreCase))
            AddLine(c.Id, c.Deliverable, c.Member, "state", at, null, category, done, null, false);
        if (c.Member is not null && c.Status == "queued")
            Exec("UPDATE board_cards SET started_at = $at WHERE id = $id AND started_at IS NULL;", ("$at", at), ("$id", c.Id));
    }

    /// <summary>The card waits for its wake (wake_me) until <paramref name="until"/>: Scheduled; then Done again when it is in Done.</summary>
    public void AddScheduled(CardRow c, string at, string until) =>
        AddLine(c.Id, c.Deliverable, c.Member, "state", at, until, TimeCategories.Scheduled, c.Lane == Lanes.Done, null, false);

    /// <summary>A run's stall or lease wait (kind stall or hold), on a card or, with no card, on the member's work in the deliverable.</summary>
    public void AddSpan(long? cardId, long? deliverable, string? member, string kind, string start, string end)
    {
        if (cardId is { } id) EnsureTimeline(id);
        AddLine(cardId, deliverable, member, kind, start, end, null, false, null, false);
    }

    public void AddMarker(long? cardId, long? deliverable, string? member, string marker, string at)
    {
        if (cardId is { } id) EnsureTimeline(id);
        AddLine(cardId, deliverable, member, "marker", at, null, null, false, marker, false);
    }

    private void AddLine(long? cardId, long? deliverable, string? member, string kind, string at, string? until, string? category, bool done, string? marker, bool rebuilt) =>
        Exec("INSERT INTO board_timeline (card_id, deliverable, member, kind, at, until, category, done, marker, rebuilt) VALUES ($c, $w, $m, $k, $at, $u, $cat, $d, $mk, $r);",
            ("$c", (object?)cardId ?? DBNull.Value), ("$w", (object?)deliverable ?? DBNull.Value), ("$m", (object?)member ?? DBNull.Value),
            ("$k", kind), ("$at", at), ("$u", (object?)until ?? DBNull.Value), ("$cat", (object?)category ?? DBNull.Value),
            ("$d", done ? 1 : 0), ("$mk", (object?)marker ?? DBNull.Value), ("$r", rebuilt ? 1 : 0));

    /// <summary>A card's timeline lines, oldest first (filled in from its trail first, for a card made before 0047).</summary>
    public IReadOnlyList<TimelineLine> Timeline(long cardId)
    {
        EnsureTimeline(cardId);
        return Query($"SELECT {TimelineColumns} FROM board_timeline WHERE card_id = $c ORDER BY at, id;", ReadLine, ("$c", cardId));
    }

    /// <summary>The lines of members' work in the deliverable that is on none of its cards (card_id NULL).</summary>
    public IReadOnlyList<TimelineLine> LooseTimeline(long deliverable) =>
        Query($"SELECT {TimelineColumns} FROM board_timeline WHERE card_id IS NULL AND deliverable = $w ORDER BY at, id;", ReadLine, ("$w", deliverable));

    /// <summary>
    /// Fills in a card's timeline from its board_trail lines once, for a card made before 0047 (<see cref="TimelineRebuild"/>):
    /// its lines are marked rebuilt, and so is the card. Claimed and written in one transaction, so it happens once.
    /// </summary>
    public void EnsureTimeline(long cardId)
    {
        using var lease = Use();
        var c = lease.Connection;
        using (var probe = Command(c, "SELECT timeline_rebuild FROM board_cards WHERE id = $id;", [("$id", cardId)]))
            if (probe.ExecuteScalar() is not long pending || pending == 0) return;
        using var work = lease.Begin("ensure_timeline");
        var tx = work.Transaction;
        using (var claim = Command(c, "UPDATE board_cards SET timeline_rebuild = 0, timeline_rebuilt = 1 WHERE id = $id AND timeline_rebuild = 1;", [("$id", cardId)]))
        {
            claim.Transaction = tx;
            if (claim.ExecuteNonQuery() == 0) { work.Rollback(); return; }
        }
        CardRow? card;
        using (var read = Command(c, $"SELECT {CardColumns} FROM board_cards WHERE id = $id;", [("$id", cardId)]))
        {
            read.Transaction = tx;
            using var r = read.ExecuteReader();
            card = r.Read() ? ReadCard(r) : null;
        }
        var trail = new List<TrailRow>();
        using (var read = Command(c, "SELECT id, at, source, kind, text FROM board_trail WHERE card_id = $c ORDER BY id;", [("$c", cardId)]))
        {
            read.Transaction = tx;
            using var r = read.ExecuteReader();
            while (r.Read()) trail.Add(new TrailRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        }
        if (card is null) { work.Rollback(); return; }
        var rebuilt = TimelineRebuild.FromTrail(card, trail);
        foreach (var l in rebuilt.Lines)
        {
            using var insert = Command(c,
                "INSERT INTO board_timeline (card_id, deliverable, member, kind, at, until, category, done, marker, rebuilt) VALUES ($c, $w, $m, $k, $at, $u, $cat, $d, $mk, 1);",
                [("$c", cardId), ("$w", (object?)card.Deliverable ?? DBNull.Value), ("$m", (object?)l.Member ?? DBNull.Value), ("$k", l.Kind), ("$at", l.At),
                 ("$u", (object?)l.Until ?? DBNull.Value), ("$cat", (object?)l.Category ?? DBNull.Value), ("$d", l.Done ? 1 : 0), ("$mk", (object?)l.Marker ?? DBNull.Value)]);
            insert.Transaction = tx;
            insert.ExecuteNonQuery();
        }
        if (rebuilt.StartedAt is { } started)
            using (var set = Command(c, "UPDATE board_cards SET started_at = $at WHERE id = $id AND started_at IS NULL;", [("$at", started), ("$id", cardId)]))
            {
                set.Transaction = tx;
                set.ExecuteNonQuery();
            }
        work.Commit();
    }

    /// <summary>The runs of a deliverable: who, queued, started, ended (null while running; an interrupted one ends where it started).</summary>
    public IReadOnlyList<RunSpan> RunsOf(long deliverable) =>
        Query(
            "SELECT m.name, r.queued_at, r.started_at, r.ended_at, r.state FROM runs r JOIN members m ON m.id = r.member_id WHERE r.correlation_id = $w ORDER BY r.id;",
            r =>
            {
                var queued = Time(r.GetString(1));
                var started = Time(r.GetString(2));
                DateTimeOffset? ended = r.IsDBNull(3) ? (r.GetString(4) == "running" ? null : started) : Time(r.GetString(3));
                return new RunSpan(r.GetString(0), queued < started ? queued : started, started, ended);
            }, ("$w", deliverable));

    public DateTimeOffset UtcNow() => _clock.GetUtcNow();

    public static DateTimeOffset Time(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // ---------------------------------------------------------------- a card's end (0045)

    /// <summary>
    /// Its deliverable ended: every card of it not ended yet records the end's time and how it ended (completed, closed
    /// or expired). Returns how many were ended.
    /// </summary>
    public int EndCardsOf(long deliverable, string how, string at) =>
        Exec("UPDATE board_cards SET ended_at = $at, ended_how = $how WHERE deliverable = $w AND ended_at IS NULL;", ("$at", at), ("$how", how), ("$w", deliverable));

    /// <summary>New work on the card (it is told again in its deliverable): it is live again.</summary>
    public void Unend(long cardId) => Exec("UPDATE board_cards SET ended_at = NULL, ended_how = NULL WHERE id = $id AND ended_at IS NOT NULL;", ("$id", cardId));

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
        Exec("DELETE FROM board_timeline WHERE card_id IN (SELECT id FROM board_cards WHERE team = $t COLLATE NOCASE);", ("$t", team));
        Exec("DELETE FROM board_cards WHERE team = $t COLLATE NOCASE;", ("$t", team));
    }

    /// <summary>A removed member's cards become unassigned.</summary>
    public void Unassign(string team, string member) =>
        Exec("UPDATE board_cards SET member = NULL WHERE team = $t COLLATE NOCASE AND member = $m COLLATE NOCASE;", ("$t", team), ("$m", member));

    // ---------------------------------------------------------------- backlog

    private const string ItemColumns = "id, team, title, body, state, position, archived_at, dispatched_team, dispatched_deliverable, dispatched_at, created_at, updated_at";

    private static ItemRow ReadItem(SqliteDataReader r) => new(
        r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetDouble(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10), r.GetString(11));

    public IReadOnlyList<ItemRow> Items(bool archived = false) =>
        Query($"SELECT {ItemColumns} FROM backlog_items WHERE archived_at IS {(archived ? "NOT NULL" : "NULL")} ORDER BY {(archived ? "archived_at DESC" : "position, id")};", ReadItem);

    public ItemRow? Item(long id) => Query($"SELECT {ItemColumns} FROM backlog_items WHERE id = $id;", ReadItem, ("$id", id)).FirstOrDefault();

    public ItemRow? ItemDispatchedAs(long deliverable) =>
        Query($"SELECT {ItemColumns} FROM backlog_items WHERE dispatched_deliverable = $w;", ReadItem, ("$w", deliverable)).FirstOrDefault();

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
                   dispatched_team = $dt, dispatched_deliverable = $dw, dispatched_at = $da, updated_at = $now WHERE id = $id;
            """,
            ("$t", (object?)i.Team ?? DBNull.Value), ("$title", Clip(i.Title, 200)), ("$b", i.Body), ("$s", i.State), ("$p", i.Position),
            ("$ar", (object?)i.ArchivedAt ?? DBNull.Value), ("$dt", (object?)i.DispatchedTeam ?? DBNull.Value),
            ("$dw", (object?)i.DispatchedDeliverable ?? DBNull.Value), ("$da", (object?)i.DispatchedAt ?? DBNull.Value), ("$now", Now()), ("$id", i.Id));

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

    public sealed record DispatchRow(long Id, long ItemId, string Team, long Deliverable, string DispatchedAt, string? DispatchedBy);

    public void AddDispatch(long itemId, string team, long deliverable, string? by) =>
        Exec("INSERT INTO backlog_dispatches (item_id, team, deliverable, dispatched_at, dispatched_by) VALUES ($i, $t, $w, $at, $by);",
            ("$i", itemId), ("$t", team), ("$w", deliverable), ("$at", Now()), ("$by", (object?)by ?? DBNull.Value));

    public IReadOnlyList<DispatchRow> Dispatches(long itemId) =>
        Query("SELECT id, item_id, team, deliverable, dispatched_at, dispatched_by FROM backlog_dispatches WHERE item_id = $i ORDER BY id DESC;",
            r => new DispatchRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)),
            ("$i", itemId));

    public int DispatchCount(long itemId) => (int)Scalar("SELECT count(*) FROM backlog_dispatches WHERE item_id = $i;", ("$i", itemId));

    /// <summary>What a deliverable's runs used: runs, the members who ran, and model tokens (from the gateway's usage records).</summary>
    public sealed record DeliverableUsage(int Runs, IReadOnlyList<string> Members, long InputTokens, long OutputTokens, string? FirstStart, string? LastEnd);

    public DeliverableUsage UsageOf(long deliverable)
    {
        var members = Query(
            "SELECT DISTINCT m.name FROM runs r JOIN members m ON m.id = r.member_id WHERE r.correlation_id = $w ORDER BY m.name;",
            r => r.GetString(0), ("$w", deliverable));
        var runs = (int)Scalar("SELECT count(*) FROM runs WHERE correlation_id = $w;", ("$w", deliverable));
        var tokens = Query(
            """
            SELECT coalesce(sum(u.input_tokens), 0) + coalesce(sum(u.cache_read_tokens), 0) + coalesce(sum(u.cache_write_tokens), 0),
                   coalesce(sum(u.output_tokens), 0)
            FROM usage_events u JOIN runs r ON r.id = u.run_number WHERE r.correlation_id = $w;
            """, r => (r.GetInt64(0), r.GetInt64(1)), ("$w", deliverable)).FirstOrDefault();
        var span = Query("SELECT min(started_at), max(ended_at) FROM runs WHERE correlation_id = $w;",
            r => (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)), ("$w", deliverable)).FirstOrDefault();
        return new DeliverableUsage(runs, members, tokens.Item1, tokens.Item2, span.Item1, span.Item2);
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

    // A batch: one connection and one transaction for many writes (the projector's page of bus messages), so the page
    // is one commit and one disk flush instead of one per statement. Only the thread that opened it uses it; every
    // other caller keeps opening its own connection, as before.
    private SqliteConnection? _batch;
    private SqliteTransaction? _batchTx;
    private int _batchThread;

    /// <summary>
    /// Runs <paramref name="work"/> with every statement this store runs on this thread in one transaction, committed
    /// at the end (rolled back if it throws). A batch inside a batch joins it.
    /// </summary>
    public void InBatch(Action work)
    {
        if (InThisBatch) { work(); return; }
        using var c = Open();
        using var tx = c.BeginTransaction();
        _batch = c; _batchTx = tx; _batchThread = Environment.CurrentManagedThreadId;
        try
        {
            work();
            tx.Commit();
        }
        finally { _batch = null; _batchTx = null; _batchThread = 0; }
    }

    /// <summary>
    /// Runs <paramref name="work"/> as one unit of this thread's batch (a savepoint): when it throws, everything it
    /// wrote is undone, the batch stays as it was before the unit and goes on, and the exception goes to the caller.
    /// Outside a batch it is a batch of its own.
    /// </summary>
    public void InUnit(string name, Action work)
    {
        if (!InThisBatch) { InBatch(work); return; }
        var tx = _batchTx!;
        tx.Save(name);
        try
        {
            work();
        }
        catch
        {
            tx.Rollback(name); // back to the savepoint, which stays; the release below takes it away
            tx.Release(name);
            throw;
        }
        tx.Release(name);
    }

    private bool InThisBatch => _batch is not null && _batchThread == Environment.CurrentManagedThreadId;

    /// <summary>The connection a statement runs on: this thread's batch, or a new one of its own.</summary>
    private Lease Use() => InThisBatch ? new Lease(_batch!, _batchTx, false) : new Lease(Open(), null, true);

    private readonly struct Lease(SqliteConnection connection, SqliteTransaction? batch, bool owned) : IDisposable
    {
        public SqliteConnection Connection => connection;

        /// <summary>A unit of work: its own transaction, or a savepoint on the batch's (so it can still roll back alone).</summary>
        public Work Begin(string name) => batch is null ? new Work(connection.BeginTransaction(), null) : new Work(batch, name);

        public void Dispose() { if (owned) connection.Dispose(); }
    }

    private sealed class Work : IDisposable
    {
        private readonly string? _savepoint;
        private bool _done;

        public Work(SqliteTransaction tx, string? savepoint)
        {
            Transaction = tx;
            _savepoint = savepoint;
            if (savepoint is not null) tx.Save(savepoint);
        }

        public SqliteTransaction Transaction { get; }

        public void Commit() { if (_savepoint is null) Transaction.Commit(); else Transaction.Release(_savepoint); _done = true; }

        public void Rollback() { if (_savepoint is null) Transaction.Rollback(); else Transaction.Rollback(_savepoint); _done = true; }

        public void Dispose()
        {
            if (_savepoint is null) Transaction.Dispose();
            else if (!_done) Transaction.Rollback(_savepoint);
        }
    }

    private long _queries;

    /// <summary>How many statements this store has run (tests count a read's queries: the no-N+1 check).</summary>
    public long Queries => Interlocked.Read(ref _queries);

    private SqliteCommand Command(SqliteConnection c, string sql, (string, object)[] args)
    {
        Interlocked.Increment(ref _queries);
        var cmd = c.CreateCommand();
        if (_batch is not null && ReferenceEquals(c, _batch)) cmd.Transaction = _batchTx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    private int Exec(string sql, params (string, object)[] args)
    {
        using var lease = Use();
        using var cmd = Command(lease.Connection, sql, args);
        return cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql, params (string, object)[] args)
    {
        using var lease = Use();
        using var cmd = Command(lease.Connection, sql, args);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private double ScalarDouble(string sql, params (string, object)[] args)
    {
        using var lease = Use();
        using var cmd = Command(lease.Connection, sql, args);
        return Convert.ToDouble(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string, object)[] args)
    {
        using var lease = Use();
        using var cmd = Command(lease.Connection, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }
}
