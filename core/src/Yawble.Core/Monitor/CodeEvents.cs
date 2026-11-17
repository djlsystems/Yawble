using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Monitor;

/// <summary>A file a code event touched: its path and status (A added, M modified, D deleted, R renamed from <see cref="OldPath"/>).</summary>
public sealed record CodeFile(string Path, string Status, string? OldPath = null)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["path"] = Path, ["status"] = Status };
        if (OldPath is not null) o["oldPath"] = OldPath;
        return o;
    }
}

/// <summary>
/// One event of monitor.codeEvents. <see cref="Kind"/>: <c>commit</c>, <c>merge-team</c>, <c>landed</c> or <c>push</c>
/// (see <see cref="CodeEvents"/>). <see cref="Into"/>: the branch a merge-team or landed event merged into.
/// <see cref="Branches"/>: every branch a push event's publish pushed.
/// </summary>
public sealed record CodeEvent(
    string Kind, string Team, string? Member, string Branch, string Sha, string Message, IReadOnlyList<CodeFile> Files,
    DateTimeOffset At, long? WorkflowId = null, string? Into = null, IReadOnlyList<string>? Branches = null)
{
    public const string Commit = "commit";
    public const string MergeTeam = "merge-team";
    public const string Landed = "landed";
    public const string Push = "push";

    /// <summary>The store's id (0 before it is stored): increasing in recording order.</summary>
    public long Id { get; init; }

    /// <summary>When the host recorded it.</summary>
    public DateTimeOffset RecordedAt { get; init; }

    public JsonObject ToJson()
    {
        var files = new JsonArray();
        foreach (var f in Files) files.Add(f.ToJson());
        var o = new JsonObject
        {
            ["id"] = Id,
            ["kind"] = Kind,
            ["team"] = Team,
            ["member"] = Member,
            ["branch"] = Branch,
            ["sha"] = Sha,
            ["message"] = Message,
            ["files"] = files,
            ["at"] = At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["recordedAt"] = RecordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["workflowId"] = WorkflowId,
        };
        if (Into is not null) o["into"] = Into;
        if (Branches is not null) o["branches"] = new JsonArray([.. Branches.Select(b => (JsonNode?)b)]);
        return o;
    }
}

/// <summary>
/// THE MONITOR'S CODE EVENTS (monitor.codeEvents): what really reached the team repository, recorded by the host when
/// it moves a branch there (Teams/TeamRepos.CodeEvents). Commit diffs are the truth for a file's existence and status;
/// the step feed's transcript steps are only the immediate, provisional signal. Stored (0044), so a restart or a remote
/// viewer sees the same history.
/// </summary>
public sealed class CodeEvents(string connectionString, TimeProvider? clock = null)
{
    public const int MaxLimit = 5000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private static string Text(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Stores the events; one already stored (same kind, team, branch and sha) is skipped. Returns how many were new.</summary>
    public int Add(IEnumerable<CodeEvent> events)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        var added = 0;
        var now = Text(_clock.GetUtcNow());
        foreach (var e in events)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO code_events (kind, team, member, branch, sha, message, files, at, recorded_at, workflow_id, extra)
                VALUES ($k, $t, $m, $b, $s, $msg, $f, $at, $r, $w, $x);
                """;
            var extra = new JsonObject();
            if (e.Into is not null) extra["into"] = e.Into;
            if (e.Branches is not null) extra["branches"] = new JsonArray([.. e.Branches.Select(b => (JsonNode?)b)]);
            cmd.Parameters.AddWithValue("$k", e.Kind);
            cmd.Parameters.AddWithValue("$t", e.Team);
            cmd.Parameters.AddWithValue("$m", (object?)e.Member ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$b", e.Branch);
            cmd.Parameters.AddWithValue("$s", e.Sha);
            cmd.Parameters.AddWithValue("$msg", e.Message);
            cmd.Parameters.AddWithValue("$f", new JsonArray([.. e.Files.Select(f => (JsonNode)f.ToJson())]).ToJsonString());
            cmd.Parameters.AddWithValue("$at", Text(e.At));
            cmd.Parameters.AddWithValue("$r", now);
            cmd.Parameters.AddWithValue("$w", (object?)e.WorkflowId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$x", extra.Count == 0 ? DBNull.Value : extra.ToJsonString());
            added += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return added;
    }

    /// <summary>
    /// The events of <paramref name="team"/> (every team when null) whose time is in [<paramref name="since"/>,
    /// <paramref name="until"/>] (either open), oldest first (by time, then by recording order), at most
    /// <paramref name="limit"/>. <paramref name="after"/>: only events recorded after that id (a poll's cursor).
    /// </summary>
    public IReadOnlyList<CodeEvent> Read(string? team = null, DateTimeOffset? since = null, DateTimeOffset? until = null, long after = 0, int limit = 2000)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        var where = new List<string> { "id > $after" };
        cmd.Parameters.AddWithValue("$after", after);
        if (team is not null) { where.Add("team = $t"); cmd.Parameters.AddWithValue("$t", team); }
        // The times are all written as UTC round-trip strings, so they compare as text.
        if (since is { } s) { where.Add("at >= $since"); cmd.Parameters.AddWithValue("$since", Text(s)); }
        if (until is { } u) { where.Add("at <= $until"); cmd.Parameters.AddWithValue("$until", Text(u)); }
        cmd.CommandText = $"""
            SELECT id, kind, team, member, branch, sha, message, files, at, recorded_at, workflow_id, extra FROM code_events
            WHERE {string.Join(" AND ", where)} ORDER BY at, id LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<CodeEvent>();
        while (r.Read())
        {
            string? into = null;
            IReadOnlyList<string>? branches = null;
            if (!r.IsDBNull(11) && JsonNode.Parse(r.GetString(11)) is JsonObject x)
            {
                into = (string?)x["into"];
                branches = x["branches"] is JsonArray a ? [.. a.Select(n => (string)n!)] : null;
            }
            list.Add(new CodeEvent(r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5),
                r.GetString(6), ParseFiles(r.GetString(7)), Parse(r.GetString(8)), r.IsDBNull(10) ? null : r.GetInt64(10), into, branches)
            {
                Id = r.GetInt64(0),
                RecordedAt = Parse(r.GetString(9)),
            });
        }
        return list;
    }

    private static DateTimeOffset Parse(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static IReadOnlyList<CodeFile> ParseFiles(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray a) return [];
            return [.. a.OfType<JsonObject>().Select(o => new CodeFile((string)o["path"]!, (string)o["status"]!, (string?)o["oldPath"]))];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
