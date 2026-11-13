using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Integrations;

/// <summary>The checklist of an MCP server's tools (mcp_commands), the saved choices (mcp_choices) and the mcp_rules rows they generate.</summary>
public sealed partial class DiscoverStore
{
    public IReadOnlyList<CommandRow> McpCommands(long serverId)
    {
        using var c = Open();
        return McpCommands(c, null, serverId);
    }

    private static List<CommandRow> McpCommands(SqliteConnection c, SqliteTransaction? tx, long serverId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT path, summary, class, class_source, class_reason, first_seen_at, last_seen_at, gone, badge, origin FROM mcp_commands WHERE server_id = $s ORDER BY path;";
        cmd.Parameters.AddWithValue("$s", serverId);
        using var r = cmd.ExecuteReader();
        var list = new List<CommandRow>();
        while (r.Read())
            list.Add(new CommandRow(r.GetString(0), "", S(r, 1) ?? "", false, r.GetString(2), r.GetString(3), r.GetString(4), null, r.GetString(5), r.GetString(6),
                r.GetInt64(7) == 1, null, false, S(r, 8), r.GetString(9)));
        return list;
    }

    public IReadOnlyList<ChoiceRow> McpChoices(long serverId)
    {
        using var c = Open();
        return McpChoices(c, null, serverId);
    }

    private static List<ChoiceRow> McpChoices(SqliteConnection c, SqliteTransaction? tx, long serverId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, team_id, path, decision, rule_id FROM mcp_choices WHERE server_id = $s ORDER BY id;";
        cmd.Parameters.AddWithValue("$s", serverId);
        using var r = cmd.ExecuteReader();
        var list = new List<ChoiceRow>();
        while (r.Read()) list.Add(new ChoiceRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4)));
        return list;
    }

    /// <summary>
    /// Merges a server's tools/list (already classified, by tool name) into mcp_commands, in one transaction. The first run marks nothing new;
    /// later runs mark new and changed tools. A tool that is missing is marked gone, unless it was found in use rather than listed.
    /// </summary>
    public MergeSummary MergeMcp(long serverId, JsonArray tools, IReadOnlyDictionary<string, Classification> classes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var now = Now();
        var first = Convert.ToInt64(Scalar(c, tx, "SELECT discover_runs FROM mcp_servers WHERE id = $s;", ("$s", serverId)), CultureInfo.InvariantCulture) == 0;
        var existing = McpCommands(c, tx, serverId).ToDictionary(r => r.Path, StringComparer.Ordinal);
        Run(c, tx, "UPDATE mcp_commands SET badge = NULL WHERE server_id = $s;", ("$s", serverId));
        int added = 0, changed = 0, unchanged = 0, gone = 0;
        var dropped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tools.OfType<JsonObject>())
        {
            if (McpClassifier.StringOf(t["name"]) is not { Length: > 0 } name || !seen.Add(name)) continue;
            var summary = Clip(McpClassifier.StringOf(t["description"]) ?? "");
            var cls = classes.TryGetValue(name, out var k) ? k : new Classification(CommandClass.Unknown, ClassSource.None, "not classified");
            if (!existing.TryGetValue(name, out var old))
            {
                Run(c, tx,
                    "INSERT INTO mcp_commands (server_id, path, summary, class, class_source, class_reason, origin, first_seen_at, last_seen_at, gone, badge) " +
                    "VALUES ($s, $p, $sm, $c, $cs, $cr, 'discovered', $now, $now, 0, $b);",
                    ("$s", serverId), ("$p", name), ("$sm", summary), ("$c", cls.Class), ("$cs", cls.Source), ("$cr", cls.Reason), ("$now", now), ("$b", first ? null : "new"));
                added++;
                continue;
            }
            var isChange = (summary.Length > 0 && summary != old.Summary) || old.Class != cls.Class;
            Run(c, tx,
                "UPDATE mcp_commands SET summary = $sm, class = $c, class_source = $cs, class_reason = $cr, last_seen_at = $now, gone = 0, origin = 'discovered', badge = $b " +
                "WHERE server_id = $s AND path = $p;",
                ("$s", serverId), ("$p", name), ("$sm", summary.Length > 0 ? summary : old.Summary), ("$c", cls.Class), ("$cs", cls.Source), ("$cr", cls.Reason), ("$now", now),
                ("$b", isChange ? "changed" : null));
            if (isChange) changed++; else unchanged++;
            if (old.Class != cls.Class) ReconcileMcp(c, tx, serverId, name, old.Class, cls.Class, dropped, now);
        }
        foreach (var old in existing.Values.Where(o => !seen.Contains(o.Path) && !o.Gone && o.Origin == RowOrigin.Discovered))
        {
            Run(c, tx, "UPDATE mcp_commands SET gone = 1 WHERE server_id = $s AND path = $p;", ("$s", serverId), ("$p", old.Path));
            gone++;
        }
        Run(c, tx, "UPDATE mcp_servers SET discover_runs = discover_runs + 1 WHERE id = $s;", ("$s", serverId));
        tx.Commit();
        return new MergeSummary(added, changed, gone, unchanged, dropped, false);
    }

    /// <summary>A tool's class changed: a saved allow that is no longer safe goes back to ask (deny for destructive, removed for a credential), as Reconcile does for a CLI.</summary>
    private void ReconcileMcp(SqliteConnection c, SqliteTransaction tx, long serverId, string tool, string oldClass, string newClass, List<string> dropped, string now)
    {
        foreach (var choice in McpChoices(c, tx, serverId).Where(ch => ch.Path == tool && ch.Decision == Decisions.Allow))
        {
            if (newClass == CommandClass.Credential)
            {
                RemoveMcpChoice(c, tx, choice);
                dropped.Add($"{tool}: was allowed; now it reveals a sign-in and is locked");
            }
            else if (newClass == CommandClass.Destructive)
            {
                SetMcpChoice(c, tx, serverId, choice.TeamId, tool, Decisions.Deny, now, "was allowed; now classified as destructive");
                dropped.Add($"{tool}: was allowed; now classified as destructive and refused");
            }
            else if (Rank(newClass) > Rank(oldClass))
            {
                SetMcpChoice(c, tx, serverId, choice.TeamId, tool, Decisions.Ask, now, $"was allowed; now classified as {newClass}, please review");
                dropped.Add($"{tool}: was allowed; now classified as {newClass}, please review");
            }
        }
    }

    public IReadOnlyList<(long Id, long? TeamId, string Pattern, string Decision)> McpRules(long serverId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, team_id, pattern, decision FROM mcp_rules WHERE server_id = $s ORDER BY id;";
        cmd.Parameters.AddWithValue("$s", serverId);
        using var r = cmd.ExecuteReader();
        var list = new List<(long, long?, string, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    /// <summary>The server's id and its state: how many times its tools were merged, who it is offered to and the last status line.</summary>
    public (long Id, int Runs, bool OfferedAll, IReadOnlyList<long> Teams, string? Status)? McpServer(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, discover_runs, enabled_all, status FROM mcp_servers WHERE name = $n COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var id = r.GetInt64(0);
        var runs = (int)r.GetInt64(1);
        var all = r.GetInt64(2) == 1;
        var status = S(r, 3);
        r.Close();
        using var t = c.CreateCommand();
        t.CommandText = "SELECT team_id FROM mcp_server_teams WHERE server_id = $s ORDER BY team_id;";
        t.Parameters.AddWithValue("$s", id);
        using var tr = t.ExecuteReader();
        var teams = new List<long>();
        while (tr.Read()) teams.Add(tr.GetInt64(0));
        return (id, runs, all, teams, status);
    }

    /// <summary>Adds a tool row that no listing produced (found in use). An existing row is left as it is.</summary>
    public void UpsertMcpRow(long serverId, string tool, string summary, Classification cls, string origin)
    {
        using var c = Open();
        UpsertMcpRow(c, null, serverId, tool, summary, cls, origin);
    }

    /// <summary><see cref="UpsertMcpRow(long, string, string, Classification, string)"/> inside a caller's transaction.</summary>
    public void UpsertMcpRow(SqliteConnection c, SqliteTransaction? tx, long serverId, string tool, string summary, Classification cls, string origin)
    {
        Run(c, tx,
            "INSERT INTO mcp_commands (server_id, path, summary, class, class_source, class_reason, origin, first_seen_at, last_seen_at, gone) " +
            "VALUES ($s, $p, $sm, $c, $cs, $cr, $o, $now, $now, 0) ON CONFLICT (server_id, path) DO NOTHING;",
            ("$s", serverId), ("$p", tool), ("$sm", summary), ("$c", cls.Class), ("$cs", cls.Source), ("$cr", cls.Reason), ("$o", origin), ("$now", Now()));
    }

    /// <summary>The tool's entry in the server's last tools/list (name, description, annotations), or null.</summary>
    public JsonObject? McpTool(long serverId, string tool)
    {
        using var c = Open();
        var json = Scalar(c, null, "SELECT tools FROM mcp_servers WHERE id = $s;", ("$s", serverId)) as string;
        if (json is null || JsonNode.Parse(json) is not JsonArray tools) return null;
        return tools.OfType<JsonObject>().FirstOrDefault(t => McpClassifier.StringOf(t["name"]) == tool);
    }

    /// <summary>Writes a choice and the one mcp_rules row it generates (the pattern is the exact tool name), replacing the choice's previous rule.</summary>
    public void SetMcpChoice(SqliteConnection c, SqliteTransaction tx, long serverId, long? teamId, string tool, string decision, string now, string? note = null)
    {
        var old = McpChoices(c, tx, serverId).FirstOrDefault(ch => ch.TeamId == teamId && ch.Path == tool);
        if (old?.RuleId is { } oldRule) Run(c, tx, "DELETE FROM mcp_rules WHERE id = $id;", ("$id", oldRule));
        Run(c, tx, "DELETE FROM mcp_rules WHERE server_id = $s AND team_id IS $t AND pattern = $p COLLATE NOCASE;", ("$s", serverId), ("$t", teamId), ("$p", tool));
        var ruleId = Convert.ToInt64(Scalar(c, tx,
            "INSERT INTO mcp_rules (server_id, team_id, pattern, decision, created_at) VALUES ($s, $t, $p, $d, $now) RETURNING id;",
            ("$s", serverId), ("$t", teamId), ("$p", tool), ("$d", decision), ("$now", now)), CultureInfo.InvariantCulture);
        Run(c, tx, "DELETE FROM mcp_choices WHERE server_id = $s AND team_id IS $t AND path = $p;", ("$s", serverId), ("$t", teamId), ("$p", tool));
        Run(c, tx, "INSERT INTO mcp_choices (server_id, team_id, path, decision, rule_id, chosen_at, note) VALUES ($s, $t, $p, $d, $r, $now, $n);",
            ("$s", serverId), ("$t", teamId), ("$p", tool), ("$d", decision), ("$r", ruleId), ("$now", now), ("$n", note));
    }

    public void RemoveMcpChoice(SqliteConnection c, SqliteTransaction tx, ChoiceRow choice)
    {
        if (choice.RuleId is { } rule) Run(c, tx, "DELETE FROM mcp_rules WHERE id = $id;", ("$id", rule));
        Run(c, tx, "DELETE FROM mcp_choices WHERE id = $id;", ("$id", choice.Id));
    }

    private static string Clip(string s)
    {
        s = s.Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}
