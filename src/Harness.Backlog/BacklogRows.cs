using System.Data.Common;
using System.Text.Json.Nodes;
using Harness.Contracts;

namespace Harness.Backlog;

/// <summary>
/// WHAT EVERY BACKLOG STORE READS AND DECIDES THE SAME WAY, whichever database holds the rows: the
/// column lists, the readers, the stored key, the fields an edit sets, the team filter, the archive's
/// page and the tenant row a create names. One copy, so the SQLite store and the SQL Server one cannot
/// drift apart on what a row means.
/// </summary>
public static class BacklogRows
{
    /// <summary>
    /// The gap a fresh item is appended with. Integers by default, so the common case - append,
    /// append, append - never needs a midpoint at all and the positions stay readable in a dump.
    /// </summary>
    public const double AppendGap = 1024d;

    /// <summary>Every column <see cref="Read"/> reads, in its order.</summary>
    public const string ItemColumns =
        "id, team, title, body, state, position, archived_at, created_at, updated_at, created_by, outcome_id";

    /// <summary>Every column <see cref="ReadDispatch"/> reads, in its order.</summary>
    public const string DispatchColumns =
        "id, item, team_id, team_name, correlation, dispatched_at, dispatched_by, frozen_at, frozen_stats,"
        + " landed_at, landed_sha, landed_branch";

    /// <summary><see cref="DispatchColumns"/>, each named through <paramref name="alias"/>.</summary>
    public static string DispatchColumnsOf(string alias) =>
        string.Join(", ", DispatchColumns.Split(',').Select(column => alias + "." + column.Trim()));

    /// <summary>The refusal of a tenant row asked of a store made without the writer for one.</summary>
    public static InvalidOperationException NoWriter() =>
        new("This backlog store was made without a tenant row writer.");

    /// <summary>
    /// NULL LEAVES A FIELD ALONE, so the SET list is composed from what was actually passed, each
    /// field to a parameter named after it with <paramref name="prefix"/>. The alternative - a
    /// whole-record save - is the change to refuse: it writes every other column back as the caller
    /// read them, so renaming a title silently reverts a concurrent body edit.
    /// </summary>
    public static List<string> Sets(char prefix, string? title, string? body, string? state, bool setOutcome)
    {
        var sets = new List<string>();

        if (title is not null) sets.Add($"title = {prefix}title");
        if (body is not null) sets.Add($"body = {prefix}body");
        if (state is not null) sets.Add($"state = {prefix}state");
        if (setOutcome) sets.Add($"outcome_id = {prefix}outcome");

        return sets;
    }

    /// <summary>
    /// FILTERED IN MEMORY RATHER THAN IN SQL, and that is a choice worth stating: the caller's
    /// effective teams are resolved per request and are a small set, while composing an IN clause
    /// from them is the shape that invites a caller-supplied fragment. At this scale the read is
    /// the cost and the filter is free.
    /// </summary>
    public static IReadOnlyList<BacklogItem> Visible(
        List<BacklogItem> rows, IReadOnlySet<string>? teams, bool includeUnlinked)
    {
        if (teams is null) return rows;

        return rows
            .Where(item => item.Team is null
                ? includeUnlinked
                : teams.Contains(item.Team))
            .ToList();
    }

    /// <summary>
    /// ORDERED ON THE NUMBER, IN MEMORY, never on the stored key in SQL. The key is a citation
    /// (`B001F`) that sorts by name only while it is four characters wide; past that, and for
    /// any row stored in another spelling, string order and id order part company, and a
    /// cursor compared as text would skip or repeat rows at exactly that seam.
    /// </summary>
    public static IReadOnlyList<BacklogItem> ArchivedPage(IReadOnlyList<BacklogItem> archived, long? before, int take) =>
        archived
            .Where(item => before is not { } cursor || item.Id < cursor)
            .OrderByDescending(item => item.Id)
            .Take(take)
            .ToList();

    /// <summary>
    /// THE CITATION IS MINTED IN THE CREATE'S TRANSACTION, so the caller cannot name it: the row gets
    /// the citation as its subject and the id in its detail, as the Admin Log and FindLatestAsync read it.
    /// </summary>
    public static TriggerAudit Naming(TriggerAudit audit, long id)
    {
        var detail = audit.Detail is null ? new JsonObject() : JsonNode.Parse(audit.Detail) as JsonObject;
        if (detail is not null && !detail.ContainsKey("id")) detail["id"] = id;
        return audit with { Subject = audit.Subject ?? Key(id), Detail = detail?.ToJsonString() ?? audit.Detail };
    }

    public static BacklogItem Read(DbDataReader reader) =>
        new(
            Id(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9))
        {
            OutcomeId = reader.IsDBNull(10) ? null : reader.GetString(10),
        };

    public static BacklogDispatch ReadDispatch(DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            Id(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));

    /// <summary>A dispatch's columns, then its item's title, state and whether it is archived.</summary>
    public static BacklogCurrentDispatch ReadCurrent(DbDataReader reader) =>
        new(ReadDispatch(reader), reader.GetString(12), reader.GetString(13), !reader.IsDBNull(14));

    /// <summary>dispatch, repo, sha, recorded_at.</summary>
    public static BacklogDispatchTip ReadTip(DbDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));

    /// <summary>dispatch, repo, default_sha, team_sha, recorded_at.</summary>
    public static BacklogDispatchBase ReadBase(DbDataReader reader) =>
        new(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4));

    /// <summary>dispatch, repo, reason, team_sha, default_sha, stopped_at, recorded_at.</summary>
    public static BacklogDispatchMissedStart ReadMissed(DbDataReader reader) =>
        new(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6));

    /// <summary>The stored key for an item: the citation itself, <c>B001F</c>. The contract
    /// keeps the number, so the conversion lives here and nowhere else.</summary>
    public static string Key(long id) => PlatformBacklogId.Format(id);

    public static long Id(string key) =>
        PlatformBacklogId.TryParse(key, out var id)
            ? id
            : throw new InvalidOperationException($"Backlog key '{key}' is not a backlog id.");
}
