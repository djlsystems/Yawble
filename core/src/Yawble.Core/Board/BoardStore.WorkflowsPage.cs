using System.Text.Json;

namespace Yawble.Core.Board;

/// <summary>The Workflows page's board reads for many workflows at once, each one statement.</summary>
public sealed partial class BoardStore
{
    /// <summary>The cards of these workflows. One statement.</summary>
    public IReadOnlyList<CardRow> CardsOfWorkflows(IReadOnlyCollection<long> workflows) =>
        workflows.Count == 0 ? []
        : Query($"SELECT {CardColumns} FROM board_cards WHERE workflow IN (SELECT value FROM json_each($ids));", ReadCard, ("$ids", Ids(workflows)));

    /// <summary>The runs of these workflows, by workflow (as <see cref="RunsOf"/>). One statement.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<RunSpan>> RunsOfWorkflows(IReadOnlyCollection<long> workflows)
    {
        if (workflows.Count == 0) return new Dictionary<long, IReadOnlyList<RunSpan>>();
        var rows = Query(
            """
            SELECT r.correlation_id, m.name, r.queued_at, r.started_at, r.ended_at, r.state FROM runs r JOIN members m ON m.id = r.member_id
            WHERE r.correlation_id IN (SELECT value FROM json_each($ids)) ORDER BY r.id;
            """,
            r =>
            {
                var queued = Time(r.GetString(2));
                var started = Time(r.GetString(3));
                DateTimeOffset? ended = r.IsDBNull(4) ? (r.GetString(5) == "running" ? null : started) : Time(r.GetString(4));
                return (Workflow: r.GetInt64(0), Run: new RunSpan(r.GetString(1), queued < started ? queued : started, started, ended));
            }, ("$ids", Ids(workflows)));
        return rows.GroupBy(x => x.Workflow).ToDictionary(g => g.Key, g => (IReadOnlyList<RunSpan>)[.. g.Select(x => x.Run)]);
    }

    /// <summary>
    /// The timeline lines of these workflows (cards' and card-less), oldest first. Cards still to be rebuilt from their
    /// trail (made before 0047) are rebuilt first: one statement to find them, and only those are rebuilt.
    /// </summary>
    public IReadOnlyList<TimelineLine> TimelinesOfWorkflows(IReadOnlyCollection<long> workflows)
    {
        if (workflows.Count == 0) return [];
        var ids = Ids(workflows);
        foreach (var card in Query("SELECT id FROM board_cards WHERE timeline_rebuild = 1 AND workflow IN (SELECT value FROM json_each($ids));", r => r.GetInt64(0), ("$ids", ids)))
            EnsureTimeline(card);
        return Query(
            $"""
             SELECT {TimelineColumns} FROM board_timeline
             WHERE workflow IN (SELECT value FROM json_each($ids))
                OR card_id IN (SELECT id FROM board_cards WHERE workflow IN (SELECT value FROM json_each($ids)))
             ORDER BY at, id;
             """, ReadLine, ("$ids", ids));
    }

    /// <summary>
    /// The backlog item each of these workflows was dispatched as (the newest dispatch of it, backlog_dispatches), by
    /// workflow. One statement.
    /// </summary>
    public IReadOnlyDictionary<long, ItemRow> ItemsOfWorkflows(IReadOnlyCollection<long> workflows)
    {
        var map = new Dictionary<long, ItemRow>();
        if (workflows.Count == 0) return map;
        var columns = string.Join(", ", ItemColumns.Split(", ").Select(c => "i." + c));
        foreach (var (w, item) in Query(
                     $"SELECT d.workflow, {columns} FROM backlog_dispatches d JOIN backlog_items i ON i.id = d.item_id WHERE d.workflow IN (SELECT value FROM json_each($ids)) ORDER BY d.id;",
                     r => (r.GetInt64(0), ReadItemAt(r, 1)), ("$ids", Ids(workflows))))
            map[w] = item; // the newest dispatch wins
        return map;
    }

    /// <summary>These backlog items, by id. One statement.</summary>
    public IReadOnlyDictionary<long, ItemRow> ItemsById(IReadOnlyCollection<long> ids) =>
        ids.Count == 0 ? new Dictionary<long, ItemRow>()
        : Query($"SELECT {ItemColumns} FROM backlog_items WHERE id IN (SELECT value FROM json_each($ids));", ReadItem, ("$ids", Ids(ids))).ToDictionary(i => i.Id);

    private static ItemRow ReadItemAt(Microsoft.Data.Sqlite.SqliteDataReader r, int o) => new(
        r.GetInt64(o), r.IsDBNull(o + 1) ? null : r.GetString(o + 1), r.GetString(o + 2), r.GetString(o + 3), r.GetString(o + 4), r.GetDouble(o + 5),
        r.IsDBNull(o + 6) ? null : r.GetString(o + 6), r.IsDBNull(o + 7) ? null : r.GetString(o + 7), r.IsDBNull(o + 8) ? null : r.GetInt64(o + 8),
        r.IsDBNull(o + 9) ? null : r.GetString(o + 9), r.GetString(o + 10), r.GetString(o + 11));

    private static string Ids(IEnumerable<long> ids) => JsonSerializer.Serialize(ids.Distinct());
}
