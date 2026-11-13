using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Integrations;

/// <summary>
/// The checklist of an API's operations (api_commands), the saved choices (api_choices) and the api_rules rows they generate: the MCP
/// store's twin. A row's path is the action id (operationId, or "METHOD /path"); a rule's pattern is that exact id.
/// </summary>
public sealed partial class DiscoverStore
{
    public IReadOnlyList<CommandRow> ApiCommands(long apiId)
    {
        using var c = Open();
        return ApiCommands(c, null, apiId);
    }

    private static List<CommandRow> ApiCommands(SqliteConnection c, SqliteTransaction? tx, long apiId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT path, summary, class, class_source, class_reason, first_seen_at, last_seen_at, gone, badge, origin, tag, method, route FROM api_commands WHERE api_id = $s ORDER BY path;";
        cmd.Parameters.AddWithValue("$s", apiId);
        using var r = cmd.ExecuteReader();
        var list = new List<CommandRow>();
        while (r.Read())
            list.Add(new CommandRow(r.GetString(0), "", S(r, 1) ?? "", false, r.GetString(2), r.GetString(3), r.GetString(4), null, r.GetString(5), r.GetString(6),
                r.GetInt64(7) == 1, null, false, S(r, 8), r.GetString(9), S(r, 10), S(r, 11), S(r, 12)));
        return list;
    }

    public IReadOnlyList<ChoiceRow> ApiChoices(long apiId)
    {
        using var c = Open();
        return ApiChoices(c, null, apiId);
    }

    private static List<ChoiceRow> ApiChoices(SqliteConnection c, SqliteTransaction? tx, long apiId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, team_id, path, decision, rule_id FROM api_choices WHERE api_id = $s ORDER BY id;";
        cmd.Parameters.AddWithValue("$s", apiId);
        using var r = cmd.ExecuteReader();
        var list = new List<ChoiceRow>();
        while (r.Read()) list.Add(new ChoiceRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4)));
        return list;
    }

    /// <summary>
    /// Merges a spec's operations (already classified) into api_commands, in one transaction. The first run marks nothing new; later runs
    /// mark new and changed operations. An operation that is missing is marked gone, unless it was found in use rather than listed.
    /// </summary>
    public MergeSummary MergeApi(long apiId, ApiSpec spec)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var now = Now();
        var first = Convert.ToInt64(Scalar(c, tx, "SELECT discover_runs FROM apis WHERE id = $s;", ("$s", apiId)), CultureInfo.InvariantCulture) == 0;
        var existing = ApiCommands(c, tx, apiId).ToDictionary(r => r.Path, StringComparer.Ordinal);
        Run(c, tx, "UPDATE api_commands SET badge = NULL WHERE api_id = $s;", ("$s", apiId));
        int added = 0, changed = 0, unchanged = 0, gone = 0;
        var dropped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in spec.Actions)
        {
            if (string.IsNullOrEmpty(a.Id) || !seen.Add(a.Id)) continue;
            var summary = Clip(a.Summary ?? "");
            var cls = a.Class;
            (string, object?)[] Args(string sm) =>
            [
                ("$s", apiId), ("$p", a.Id), ("$sm", sm), ("$m", a.Method), ("$r", a.Route), ("$t", a.Tag), ("$pa", a.Params.ToJsonString()),
                ("$c", cls.Class), ("$cs", cls.Source), ("$cr", cls.Reason), ("$now", now),
                ("$sc", a.Scopes.Count == 0 ? null : string.Join(' ', a.Scopes)),
            ];
            if (!existing.TryGetValue(a.Id, out var old))
            {
                Run(c, tx,
                    "INSERT INTO api_commands (api_id, path, summary, method, route, tag, params, class, class_source, class_reason, scopes, origin, first_seen_at, last_seen_at, gone, badge) " +
                    "VALUES ($s, $p, $sm, $m, $r, $t, $pa, $c, $cs, $cr, $sc, 'discovered', $now, $now, 0, $b);",
                    [.. Args(summary), ("$b", first ? null : "new")]);
                added++;
                continue;
            }
            var isChange = (summary.Length > 0 && summary != old.Summary) || old.Class != cls.Class;
            Run(c, tx,
                "UPDATE api_commands SET summary = $sm, method = $m, route = $r, tag = $t, params = $pa, class = $c, class_source = $cs, class_reason = $cr, scopes = $sc, " +
                "last_seen_at = $now, gone = 0, origin = 'discovered', badge = $b WHERE api_id = $s AND path = $p;",
                [.. Args(summary.Length > 0 ? summary : old.Summary), ("$b", isChange ? "changed" : null)]);
            if (isChange) changed++; else unchanged++;
            if (old.Class != cls.Class) ReconcileApi(c, tx, apiId, a.Id, old.Class, cls.Class, dropped, now);
        }
        foreach (var old in existing.Values.Where(o => !seen.Contains(o.Path) && !o.Gone && o.Origin == RowOrigin.Discovered))
        {
            Run(c, tx, "UPDATE api_commands SET gone = 1 WHERE api_id = $s AND path = $p;", ("$s", apiId), ("$p", old.Path));
            gone++;
        }
        if (spec.Dropped > 0) dropped.Add($"{spec.Dropped} operations beyond the first {OpenApiReader.MaxActions} were left out");
        var auth = spec.Auth is null ? null : AuthJson(spec.Auth).ToJsonString();
        // auth is kept as it is once set, except that an OAuth 2 spec replaces a spec-read sign-in while nothing is signed in yet (no Connection):
        // a working sign-in is never switched silently, and one the person chose by hand is never replaced.
        Run(c, tx, "UPDATE apis SET discover_runs = discover_runs + 1, title = COALESCE(title, $ti), updated_at = $now, " +
                   "auth = CASE WHEN auth IS NULL OR ($oa = 1 AND connection_id IS NULL AND COALESCE(json_extract(auth, '$.chosen'), 0) = 0) THEN COALESCE($au, auth) ELSE auth END WHERE id = $s;",
            ("$s", apiId), ("$ti", spec.Title), ("$au", auth), ("$oa", spec.Auth?.Type == "oauth2" ? 1 : 0), ("$now", now));
        tx.Commit();
        return new MergeSummary(added, changed, gone, unchanged, dropped, false);
    }

    /// <summary>The apis.auth JSON of a spec's sign-in; an OAuth 2 one adds its addresses and scopes.</summary>
    internal static JsonObject AuthJson(ApiAuth a)
    {
        var o = new JsonObject { ["type"] = a.Type, ["name"] = a.Name, ["description"] = a.Description };
        if (a.Type != "oauth2") return o;
        o["authorizeUrl"] = a.AuthorizeUrl;
        o["tokenUrl"] = a.TokenUrl;
        o["scopes"] = new JsonObject(a.Scopes?.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)) ?? []);
        if (a.OpenIdConnectUrl is { } oidc) o["openIdConnectUrl"] = oidc;
        return o;
    }

    /// <summary>An operation's class changed: a saved allow that is no longer safe goes back to ask (deny for destructive, removed for a credential).</summary>
    private void ReconcileApi(SqliteConnection c, SqliteTransaction tx, long apiId, string action, string oldClass, string newClass, List<string> dropped, string now)
    {
        foreach (var choice in ApiChoices(c, tx, apiId).Where(ch => ch.Path == action && ch.Decision == Decisions.Allow))
        {
            if (newClass == CommandClass.Credential)
            {
                RemoveApiChoice(c, tx, choice);
                dropped.Add($"{action}: was allowed; now it reveals a sign-in and is locked");
            }
            else if (newClass == CommandClass.Destructive)
            {
                SetApiChoice(c, tx, apiId, choice.TeamId, action, Decisions.Deny, now, "was allowed; now classified as destructive");
                dropped.Add($"{action}: was allowed; now classified as destructive and refused");
            }
            else if (Rank(newClass) > Rank(oldClass))
            {
                SetApiChoice(c, tx, apiId, choice.TeamId, action, Decisions.Ask, now, $"was allowed; now classified as {newClass}, please review");
                dropped.Add($"{action}: was allowed; now classified as {newClass}, please review");
            }
        }
    }

    public IReadOnlyList<(long Id, long? TeamId, string Pattern, string Decision)> ApiRules(long apiId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, team_id, pattern, decision FROM api_rules WHERE api_id = $s ORDER BY id;";
        cmd.Parameters.AddWithValue("$s", apiId);
        using var r = cmd.ExecuteReader();
        var list = new List<(long, long?, string, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    /// <summary>The API's id and state: how many times its spec was merged, who it is offered to and the last status line.</summary>
    public (long Id, int Runs, bool OfferedAll, IReadOnlyList<long> Teams, string? Status)? Api(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, discover_runs, enabled_all, status FROM apis WHERE name = $n COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var id = r.GetInt64(0);
        var runs = (int)r.GetInt64(1);
        var all = r.GetInt64(2) == 1;
        var status = S(r, 3);
        r.Close();
        using var t = c.CreateCommand();
        t.CommandText = "SELECT team_id FROM api_teams WHERE api_id = $s ORDER BY team_id;";
        t.Parameters.AddWithValue("$s", id);
        using var tr = t.ExecuteReader();
        var teams = new List<long>();
        while (tr.Read()) teams.Add(tr.GetInt64(0));
        return (id, runs, all, teams, status);
    }

    /// <summary>Writes a choice and the one api_rules row it generates (the pattern is the exact action id), replacing the choice's previous rule.</summary>
    public void SetApiChoice(SqliteConnection c, SqliteTransaction tx, long apiId, long? teamId, string action, string decision, string now, string? note = null)
    {
        var old = ApiChoices(c, tx, apiId).FirstOrDefault(ch => ch.TeamId == teamId && ch.Path == action);
        if (old?.RuleId is { } oldRule) Run(c, tx, "DELETE FROM api_rules WHERE id = $id;", ("$id", oldRule));
        Run(c, tx, "DELETE FROM api_rules WHERE api_id = $s AND team_id IS $t AND pattern = $p;", ("$s", apiId), ("$t", teamId), ("$p", action));
        var ruleId = Convert.ToInt64(Scalar(c, tx,
            "INSERT INTO api_rules (api_id, team_id, pattern, decision, created_at) VALUES ($s, $t, $p, $d, $now) RETURNING id;",
            ("$s", apiId), ("$t", teamId), ("$p", action), ("$d", decision), ("$now", now)), CultureInfo.InvariantCulture);
        Run(c, tx, "DELETE FROM api_choices WHERE api_id = $s AND team_id IS $t AND path = $p;", ("$s", apiId), ("$t", teamId), ("$p", action));
        Run(c, tx, "INSERT INTO api_choices (api_id, team_id, path, decision, rule_id, chosen_at, note) VALUES ($s, $t, $p, $d, $r, $now, $n);",
            ("$s", apiId), ("$t", teamId), ("$p", action), ("$d", decision), ("$r", ruleId), ("$now", now), ("$n", note));
    }

    public void RemoveApiChoice(SqliteConnection c, SqliteTransaction tx, ChoiceRow choice)
    {
        if (choice.RuleId is { } rule) Run(c, tx, "DELETE FROM api_rules WHERE id = $id;", ("$id", rule));
        Run(c, tx, "DELETE FROM api_choices WHERE id = $id;", ("$id", choice.Id));
    }
}
