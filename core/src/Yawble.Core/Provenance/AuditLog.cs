using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Provenance;

/// <summary>
/// Every call that changes something, from the window or a cloud viewer (0015_provenance.sql audit_log): who, how,
/// which call, what it was about, and whether it worked. Calls that only read are left out (the screens poll several
/// every second). Only identifying parameters are kept: never a key, a token, a message or keystrokes.
/// </summary>
public sealed class AuditLog(string connectionString, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The parameters that say what a call was about; every other parameter is left out.</summary>
    private static readonly string[] TargetKeys =
        ["team", "teamId", "member", "memberId", "name", "id", "card", "item", "lane", "workflow", "conversationId", "trigger", "cli", "mode", "country"];

    private static readonly HashSet<string> ReadVerbs = new(StringComparer.Ordinal)
    {
        "list", "get", "status", "card", "messages", "decisions", "overview", "agents", "detect", "read", "preview", "attach",
        "banner", "input", "resize", "previewLoosen", "getInfo", "getAvailableBackends", "getPlatformSupport", "getState",
        "getGlobal", "getMember", "version",
        // The screens' polls and lookups: what is waiting, the Monitor's steps, workflows, settings shown, catalogs.
        "asks", "steps", "workflows", "item", "connectionAttention", "connections", "versionStatus", "models", "repos",
        "mcpList", "apiList", "emailList", "checklist", "discoverStatus", "watches", "approvals", "env", "quiet", "runs", "runRecords",
        "history", "check", "commandLine", "testSessions", "actionStatus", "backups", "doctor", "keepDaily", "restoreStatus",
        "defaultModels", "sources", "query", "values", "retention", "prunePreview", "who", "catalog",
        // The team's network allow list as shown (its Remove, policy.removeTeamHost, is audited).
        "teamHosts",
        // The policy editors' host check (FloorRules.HostProblem for each entry typed).
        "checkHosts",
        // Settings > Teams and Settings > Concierge as saved (teams.defaults, concierge.defaults); setDefaults and generateNames are audited.
        "defaults",
        // B100: a team skill's kept versions (skills.versions) and Team settings' "Lessons" line (teams.lessons); skills.restore is audited.
        "versions", "lessons",
    };

    /// <summary>Calls that read a setting, or set it when they carry its value: by method, the field that sets it.</summary>
    private static readonly Dictionary<string, string> GetOrSet = new(StringComparer.Ordinal)
    {
        ["teams.push"] = "on", ["teams.syncToBase"] = "on",
        // B085: { teamId } reads the team's workflowExpiryDays, { teamId, days } sets it.
        ["teams.workflowExpiry"] = "days",
        // B119: { teamId } reads "Allow localhost (macOS)", { teamId, on } sets it.
        ["teams.localhost"] = "on",
    };

    /// <summary>A call that only reads: not audited. A get-or-set call reads when it carries no value to set.</summary>
    public static bool IsReadOnly(string method, JsonObject? parameters) =>
        GetOrSet.TryGetValue(method, out var key) ? parameters?[key] is null : IsReadOnly(method);

    /// <summary>
    /// Calls that only read although their last word is not a read verb. Every call the core answers is either a read
    /// (here or by <see cref="ReadVerbs"/>) or a change listed in AuditClassificationTests: a new call must be put in
    /// one or the other, so a screen's poll can never fill the audit again.
    /// </summary>
    private static readonly HashSet<string> ReadMethods = new(StringComparer.Ordinal)
    {
        // The Monitor's code map, polled about once a second.
        "monitor.codeEvents",
        // The Logs dialog's live tail, started and stopped as the dialog opens and closes (locally or from the cloud).
        "logs.tail", "logs.untail",
        // The cloud's org summary: one snapshot, and the push started and stopped with a viewer's page.
        "org.summary", "org.summary.watch", "org.summary.unwatch",
        // Integrations read-outs: recent calls and task runs, a pasted MCP config parsed, the configs found on this
        // computer, and a server's tools listed (a check that saves nothing).
        "integrations.calls", "integrations.taskRuns", "integrations.mcpParse", "integrations.mcpImportScan", "integrations.mcpCheck",
        // Which model a member's run would use, and whether an agent CLI has a newer version (installing one is audited).
        "agents.resolveModel", "agents.checkForUpdates",
        // The tidy plan (what Tidy would do; doing it is audited).
        "git.tidyPlan",
        // B075: a web link only reads the repository's refs. B072: the commit view reads a commit and a file's diff.
        "git.webLink", "git.commitShow", "git.commitDiff",
        // B074: resolving a batch of hex words to commits only reads.
        "git.commitsResolve",
        // B073: the commits behind a count and the Merge / Delete previews change nothing.
        "git.commitRange", "git.mergePreview", "git.mergeDiff", "git.deletePreview",
        // B104: the Workflows page's list of every team's workflows, and one workflow in full.
        "workflows.list", "workflows.get",
        // Opening and closing the read-only Code view change nothing (a download is recorded).
        "code.open", "code.close",
        // B131: a script or schema exported as a file, a script member and its saved state, and one script run in full.
        "scripts.export", "schemas.export", "scripts.memberGet", "scripts.memberState", "scripts.memberFiles", "scripts.run",
        // The designer's action catalog, a document rewritten to real integration names (nothing saved) and where a schema is used.
        "scripts.catalog", "scripts.useRealNames", "schemas.usage",
    };

    /// <summary>A call that only reads, by its name alone.</summary>
    public static bool IsReadOnly(string method)
    {
        var dot = method.LastIndexOf('.');
        var verb = dot < 0 ? method : method[(dot + 1)..];
        return ReadVerbs.Contains(verb) || ReadMethods.Contains(method) || method.StartsWith("git.status", StringComparison.Ordinal);
    }

    /// <summary>What a call was about, from its identifying parameters only ("team=web member=DeveloperPriya").</summary>
    public static string? Target(JsonObject? parameters)
    {
        if (parameters is null) return null;
        var parts = new List<string>();
        foreach (var key in TargetKeys)
        {
            if (parameters[key] is JsonValue v && v.ToString() is { Length: > 0 } s)
                parts.Add($"{key}={(s.Length > 80 ? s[..80] + "\u2026" : s)}");
        }
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// The names a call's ids stand for, appended to its target so the row stays readable after a delete (B101):
    /// teamId= gets " team=&lt;Team&gt;", memberId= gets " member=&lt;Team&gt;/&lt;Name&gt;", and a per-member action
    /// addressed by its own id (a trigger, an asked tool call, a network or host tool ask) gets " memberId=… member=…".
    /// Read before the call runs (a delete removes what it names); null when there is nothing to add or nothing found.
    /// </summary>
    public string? Names(string method, JsonObject? parameters)
    {
        if (parameters is null || IsReadOnly(method, parameters)) return null;
        var hasTeam = Text(parameters, "team") is not null;
        var hasMember = Text(parameters, "member") is not null;
        var teamId = Id(parameters, "teamId");
        var memberId = Id(parameters, "memberId");
        var ownerSql = memberId is null && !hasMember && Id(parameters, "id") is not null ? OwnerSql(method) : null;
        if ((teamId is null || hasTeam) && (memberId is null || hasMember) && ownerSql is null) return null;
        try
        {
            using var c = new SqliteConnection(connectionString);
            c.Open();
            var parts = new List<string>();
            if (teamId is not null && !hasTeam && Scalar(c, "SELECT name FROM teams WHERE id = $id;", teamId.Value) is { } team)
                parts.Add("team=" + team);
            if (memberId is not null && !hasMember
                && Scalar(c, "SELECT tm.name || '/' || m.name FROM members m JOIN teams tm ON tm.id = m.team_id WHERE m.id = $id;", memberId.Value) is { } key)
                parts.Add("member=" + key);
            if (ownerSql is not null && Scalar(c, ownerSql, Id(parameters, "id")!.Value) is { } owner)
            {
                var id = Scalar(c, "SELECT m.id FROM members m JOIN teams tm ON tm.id = m.team_id WHERE tm.name || '/' || m.name = $id COLLATE NOCASE;", owner);
                parts.Add((id is null ? "" : $"memberId={id} ") + "member=" + owner);
            }
            return parts.Count == 0 ? null : string.Join(' ', parts);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>The member ("Team/Name") a per-member action's own id belongs to, by method; null: the id is not a member's thing.</summary>
    private static string? OwnerSql(string method) => method switch
    {
        _ when method.StartsWith("triggers.", StringComparison.Ordinal) =>
            "SELECT tm.name || '/' || m.name FROM triggers g JOIN members m ON m.id = g.member_id JOIN teams tm ON tm.id = m.team_id WHERE g.id = $id;",
        "integrations.approve" or "integrations.decline" => "SELECT member FROM integration_asks WHERE id = $id;",
        "network.answer" => "SELECT member FROM network_asks WHERE id = $id;",
        "folder.answer" => "SELECT member FROM folder_asks WHERE id = $id;",
        _ when method.StartsWith("tools.", StringComparison.Ordinal) => "SELECT member FROM tool_asks WHERE id = $id;",
        _ => null,
    };

    private static string? Scalar(SqliteConnection c, string sql, object id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is { } v and not DBNull ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    }

    private static string? Text(JsonObject p, string key) => p[key] is JsonValue v && v.ToString() is { Length: > 0 } s ? s : null;

    private static long? Id(JsonObject p, string key) =>
        p[key] is JsonValue v && long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// A target's key=value pairs (the keys of <see cref="TargetKeys"/>, the names appended at write time among them). A
    /// value runs to the next " key=", so a team name may hold spaces; the first of a key repeated wins.
    /// </summary>
    public static Dictionary<string, string> ParseTarget(string? target)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(target)) return found;
        var starts = new List<(int At, string Key)>();
        foreach (var key in TargetKeys)
        {
            var probe = key + "=";
            for (var i = target.IndexOf(probe, StringComparison.Ordinal); i >= 0; i = target.IndexOf(probe, i + 1, StringComparison.Ordinal))
                if (i == 0 || target[i - 1] == ' ') starts.Add((i, key));
        }
        starts.Sort((a, b) => a.At.CompareTo(b.At));
        for (var n = 0; n < starts.Count; n++)
        {
            var (at, key) = starts[n];
            var end = n + 1 < starts.Count ? starts[n + 1].At - 1 : target.Length;
            if (end < at + key.Length + 1) continue;
            found.TryAdd(key, target[(at + key.Length + 1)..end]);
        }
        return found;
    }

    /// <summary>The activity log's key of an audit row about no team and no member: team empty, member "app" (B101).</summary>
    public const string AppKey = "/app";

    /// <summary>
    /// Who an audit row is about, as the activity log keys it (B101): "Team/Name" for a member; "Team/&lt;Team&gt;" for a
    /// team; "/app" for neither. <paramref name="joinedMember"/> and <paramref name="joinedTeam"/>: what the target's
    /// memberId= and teamId= name today (null when gone); else the names written into the target. An id whose name is
    /// lost (a row written before names were kept, about something deleted since): null.
    /// </summary>
    public static string? WhoOf(string? target, string? method, string? joinedMember, string? joinedTeam)
    {
        if (joinedMember is not null) return joinedMember;
        var t = ParseTarget(target);
        var team = joinedTeam
            ?? (t.TryGetValue("team", out var named) && named.Length > 0 ? named : null)
            ?? (method == "teams.create" && t.TryGetValue("name", out var created) && created.Length > 0 ? created : null);
        if (t.TryGetValue("member", out var member) && member.Length > 0)
        {
            if (member.Contains('/')) return member;
            if (team is not null) return team + "/" + member;
        }
        if (t.ContainsKey("memberId")) return null;
        if (team is not null) return $"{team}/<{team}>";
        return t.ContainsKey("teamId") ? null : AppKey;
    }

    public void Record(Actor actor, string method, JsonObject? parameters, string outcome, string? names = null)
    {
        if (IsReadOnly(method, parameters)) return;
        names ??= Names(method, parameters);
        var target = Target(parameters);
        if (names is not null) target = target is null ? names : target + " " + names;
        try
        {
            using var c = new SqliteConnection(connectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO audit_log (at, via, user_name, client, method, target, outcome) VALUES ($at, $via, $user, $client, $method, $target, $outcome);";
            cmd.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$via", actor.Via);
            cmd.Parameters.AddWithValue("$user", (object?)actor.User ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$client", (object?)actor.Client ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$method", method);
            cmd.Parameters.AddWithValue("$target", (object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outcome", outcome.Length > 300 ? outcome[..300] : outcome);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // The audit is a record, not a gate: a write that fails never fails the call it describes.
        }
    }

    public sealed record Row(string At, string Via, string? User, string Method, string? Target, string Outcome, string? Client = null);

    public IReadOnlyList<Row> Recent(int limit = 100)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at, via, user_name, method, target, outcome, client FROM audit_log ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var rows = new List<Row>();
        while (r.Read())
            rows.Add(new Row(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return rows;
    }
}
