using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Integrations;

public sealed record CommandRow(string Path, string ParentPath, string Summary, bool HasChildren, string Class, string Source, string Reason,
    string? SeenVersion, string FirstSeenAt, string LastSeenAt, bool Gone, string? HelpHash, bool HelpUnreadable, string? Badge, string Origin = RowOrigin.Discovered,
    string? Tag = null, string? Method = null, string? Route = null);

/// <summary>Where a checklist row came from: a walk of the help, a sighting in use, or shipped with the app.</summary>
public static class RowOrigin
{
    public const string Discovered = "discovered", Found = "found", Builtin = "builtin";
}

public sealed record ChoiceRow(long Id, long? TeamId, string Path, string Decision, long? RuleId);

public sealed record DiscoveryRow(string DiscoveredAt, string? Version, string? HelpFlag, int Runs, int CommandCount, bool Partial, string? PartialReason, string? Error);

/// <summary>What a re-discover did: new / changed / gone / unchanged nodes, and the saved ticks it had to take back.</summary>
public sealed record MergeSummary(int New, int Changed, int Gone, int Unchanged, IReadOnlyList<string> Dropped, bool Partial);

/// <summary>
/// The stored tree (integration_commands), the saved choices (integration_choices) and the rules they generated. Merging a
/// walk never deletes a choice; a node that got less safe takes its allow back to ask (or deny / nothing), and says so.
/// </summary>
public sealed partial class DiscoverStore(string connectionString, TimeProvider clock)
{
    private string Now() => clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        Run(c, null, "PRAGMA foreign_keys=ON;");
        return c;
    }

    // ------------------------------------------------------------------ reading

    public IReadOnlyList<CommandRow> Commands(long integrationId)
    {
        using var c = Open();
        return Commands(c, null, integrationId);
    }

    private static List<CommandRow> Commands(SqliteConnection c, SqliteTransaction? tx, long integrationId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT path, parent_path, summary, has_children, class, class_source, class_reason, seen_version, first_seen_at, last_seen_at, gone, help_hash, help_unreadable, badge, origin " +
            "FROM integration_commands WHERE integration_id = $i ORDER BY path;";
        cmd.Parameters.AddWithValue("$i", integrationId);
        using var r = cmd.ExecuteReader();
        var list = new List<CommandRow>();
        while (r.Read())
            list.Add(new CommandRow(r.GetString(0), r.GetString(1), S(r, 2) ?? "", r.GetInt64(3) == 1, r.GetString(4), r.GetString(5), r.GetString(6), S(r, 7),
                r.GetString(8), r.GetString(9), r.GetInt64(10) == 1, S(r, 11), r.GetInt64(12) == 1, S(r, 13), r.GetString(14)));
        return list;
    }

    public IReadOnlyList<ChoiceRow> Choices(long integrationId)
    {
        using var c = Open();
        return Choices(c, null, integrationId);
    }

    private static List<ChoiceRow> Choices(SqliteConnection c, SqliteTransaction? tx, long integrationId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, team_id, path, decision, rule_id FROM integration_choices WHERE integration_id = $i ORDER BY id;";
        cmd.Parameters.AddWithValue("$i", integrationId);
        using var r = cmd.ExecuteReader();
        var list = new List<ChoiceRow>();
        while (r.Read()) list.Add(new ChoiceRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4)));
        return list;
    }

    public DiscoveryRow? Discovery(long integrationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT discovered_at, version, help_flag, runs, command_count, partial, partial_reason, error FROM integration_discovery WHERE integration_id = $i;";
        cmd.Parameters.AddWithValue("$i", integrationId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new DiscoveryRow(r.GetString(0), S(r, 1), S(r, 2), (int)r.GetInt64(3), (int)r.GetInt64(4), r.GetInt64(5) == 1, S(r, 6), S(r, 7)) : null;
    }

    public IReadOnlyList<(long Id, long? TeamId, string Pattern, string Decision)> Rules(long integrationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, team_id, pattern, decision FROM integration_rules WHERE integration_id = $i ORDER BY id;";
        cmd.Parameters.AddWithValue("$i", integrationId);
        using var r = cmd.ExecuteReader();
        var list = new List<(long, long?, string, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public bool TeamExists(long teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM teams WHERE id = $t;";
        cmd.Parameters.AddWithValue("$t", teamId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    // ------------------------------------------------------------------ re-discover merge

    /// <summary>Merges a walk (already classified) into the stored tree, in one transaction. Choices are never deleted.</summary>
    public MergeSummary Merge(long integrationId, string? version, WalkResult walk, IReadOnlyDictionary<string, Classification> classes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var now = Now();
        var existing = Commands(c, tx, integrationId).ToDictionary(r => r.Path, StringComparer.Ordinal);
        Run(c, tx, "UPDATE integration_commands SET badge = NULL WHERE integration_id = $i;", ("$i", integrationId));
        int added = 0, changed = 0, unchanged = 0;
        var dropped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in walk.Commands)
        {
            seen.Add(w.Path);
            var cls = classes[w.Path];
            var cl = w.HasChildren && cls.Class is not (CommandClass.Credential or CommandClass.Destructive) ? new Classification(CommandClass.Group, ClassSource.None, "a group of commands: only its commands are classified") : cls;
            if (!existing.TryGetValue(w.Path, out var old))
            {
                Run(c, tx,
                    "INSERT INTO integration_commands (integration_id, path, parent_path, summary, has_children, class, class_source, class_reason, seen_version, first_seen_at, last_seen_at, gone, help_hash, help_unreadable, badge) " +
                    "VALUES ($i, $p, $pp, $s, $h, $c, $cs, $cr, $v, $now, $now, 0, $hh, $hu, 'new');",
                    ("$i", integrationId), ("$p", w.Path), ("$pp", w.ParentPath), ("$s", w.Summary), ("$h", w.HasChildren ? 1 : 0), ("$c", cl.Class), ("$cs", cl.Source),
                    ("$cr", cl.Reason), ("$v", version), ("$now", now), ("$hh", w.HelpHash), ("$hu", w.HelpUnreadable ? 1 : 0));
                added++;
                continue;
            }
            // a node whose help was not read this time (a limit, an error) keeps what was known of it
            var hash = w.HelpHash ?? old.HelpHash;
            var hashChanged = w.HelpHash is not null && old.HelpHash is not null && w.HelpHash != old.HelpHash;
            var summaryChanged = w.Summary.Length > 0 && w.Summary != old.Summary;
            // a model answer is kept while the verb table is still silent and the help is the same
            if (old.Source == ClassSource.Model && cls.Source == ClassSource.None && !hashChanged && cl.Class == CommandClass.Unknown) cl = new Classification(old.Class, old.Source, old.Reason);
            var isChange = hashChanged || summaryChanged || old.Class != cl.Class;
            Run(c, tx,
                "UPDATE integration_commands SET parent_path = $pp, summary = $s, has_children = $h, class = $c, class_source = $cs, class_reason = $cr, seen_version = $v, last_seen_at = $now, " +
                "gone = 0, origin = 'discovered', help_hash = $hh, help_unreadable = $hu, badge = $b WHERE integration_id = $i AND path = $p;",
                ("$i", integrationId), ("$p", w.Path), ("$pp", w.ParentPath), ("$s", w.Summary.Length > 0 ? w.Summary : old.Summary), ("$h", w.HasChildren ? 1 : 0), ("$c", cl.Class),
                ("$cs", cl.Source), ("$cr", cl.Reason), ("$v", version), ("$now", now), ("$hh", hash), ("$hu", w.HelpUnreadable ? 1 : 0), ("$b", isChange ? "changed" : null));
            if (isChange) changed++; else unchanged++;
            if (old.Class != cl.Class) Reconcile(c, tx, integrationId, w.Path, old.Class, cl.Class, dropped, now);
        }
        var gone = 0;
        if (!walk.Partial)
        {
            foreach (var old in existing.Values.Where(o => !seen.Contains(o.Path) && !o.Gone && o.Origin == RowOrigin.Discovered))
            {
                Run(c, tx, "UPDATE integration_commands SET gone = 1 WHERE integration_id = $i AND path = $p;", ("$i", integrationId), ("$p", old.Path));
                gone++;
            }
        }
        var runs = Convert.ToInt64(Scalar(c, tx, "SELECT COALESCE((SELECT runs FROM integration_discovery WHERE integration_id = $i), 0);", ("$i", integrationId)), CultureInfo.InvariantCulture);
        Run(c, tx, "DELETE FROM integration_discovery WHERE integration_id = $i;", ("$i", integrationId));
        Run(c, tx,
            "INSERT INTO integration_discovery (integration_id, discovered_at, version, help_flag, runs, command_count, partial, partial_reason, error) VALUES ($i, $now, $v, $f, $r, $n, $p, $pr, NULL);",
            ("$i", integrationId), ("$now", now), ("$v", version), ("$f", walk.HelpFlag), ("$r", runs + 1), ("$n", walk.Commands.Count), ("$p", walk.Partial ? 1 : 0), ("$pr", walk.PartialReason));
        tx.Commit();
        return new MergeSummary(added, changed, gone, unchanged, dropped, walk.Partial);
    }

    /// <summary>Adds a row that no walk produced (found in use, or built in). An existing row is left as it is: origin and badge never change here.</summary>
    public void UpsertRow(long integrationId, string path, string parentPath, string summary, Classification cls, string origin)
    {
        using var c = Open();
        UpsertRow(c, null, integrationId, path, parentPath, summary, cls, origin);
    }

    /// <summary><see cref="UpsertRow(long, string, string, string, Classification, string)"/> inside a caller's transaction.</summary>
    public void UpsertRow(SqliteConnection c, SqliteTransaction? tx, long integrationId, string path, string parentPath, string summary, Classification cls, string origin)
    {
        Run(c, tx,
            "INSERT INTO integration_commands (integration_id, path, parent_path, summary, has_children, class, class_source, class_reason, first_seen_at, last_seen_at, gone, origin) " +
            "VALUES ($i, $p, $pp, $s, 0, $c, $cs, $cr, $now, $now, 0, $o) ON CONFLICT (integration_id, path) DO NOTHING;",
            ("$i", integrationId), ("$p", path), ("$pp", parentPath), ("$s", summary), ("$c", cls.Class), ("$cs", cls.Source), ("$cr", cls.Reason), ("$now", Now()), ("$o", origin));
    }

    /// <summary>Records a walk that failed (the tree stays as it was).</summary>
    public void RecordFailure(long integrationId, string? version, string error)
    {
        using var c = Open();
        var runs = Convert.ToInt64(Scalar(c, null, "SELECT COALESCE((SELECT runs FROM integration_discovery WHERE integration_id = $i), 0);", ("$i", integrationId)), CultureInfo.InvariantCulture);
        var count = Convert.ToInt64(Scalar(c, null, "SELECT count(*) FROM integration_commands WHERE integration_id = $i;", ("$i", integrationId)), CultureInfo.InvariantCulture);
        Run(c, null, "DELETE FROM integration_discovery WHERE integration_id = $i;", ("$i", integrationId));
        Run(c, null, "INSERT INTO integration_discovery (integration_id, discovered_at, version, help_flag, runs, command_count, partial, partial_reason, error) VALUES ($i, $now, $v, NULL, $r, $n, 0, NULL, $e);",
            ("$i", integrationId), ("$now", Now()), ("$v", version), ("$r", runs), ("$n", count), ("$e", error));
    }

    private static int Rank(string cls) => cls switch
    {
        CommandClass.ReadOnly => 0, CommandClass.Unknown => 1, CommandClass.Group => 2, CommandClass.Change => 2, CommandClass.Destructive => 3, _ => 4,
    };

    /// <summary>A node's class changed: a saved tick that is no longer safe goes back to ask (deny for destructive, nothing for a credential).</summary>
    private void Reconcile(SqliteConnection c, SqliteTransaction tx, long integrationId, string path, string oldClass, string newClass, List<string> dropped, string now)
    {
        foreach (var choice in Choices(c, tx, integrationId).Where(ch => ch.Path == path && ch.Decision == Decisions.Allow))
        {
            if (newClass == CommandClass.Credential)
            {
                RemoveChoice(c, tx, choice);
                dropped.Add($"{path}: was allowed; now it reveals a sign-in and is locked");
            }
            else if (newClass == CommandClass.Destructive)
            {
                SetChoice(c, tx, integrationId, choice.TeamId, path, Decisions.Deny, now, "was allowed; now classified as destructive");
                dropped.Add($"{path}: was allowed; now classified as destructive and refused");
            }
            else if (Rank(newClass) > Rank(oldClass))
            {
                SetChoice(c, tx, integrationId, choice.TeamId, path, Decisions.Ask, now, $"was allowed; now classified as {newClass}, please review");
                dropped.Add($"{path}: was allowed; now classified as {newClass}, please review");
            }
        }
    }

    // ------------------------------------------------------------------ choices -> rules

    /// <summary>The rule a checklist row's choice writes: its words, alone or followed by more, never a longer word ("pr lock *").</summary>
    public static string PatternOf(string path) => path + " *";

    /// <summary>Whether a rule is a row's own: "path *", or "path*" (an older saved form).</summary>
    public static bool IsPatternOf(string pattern, string path) =>
        string.Equals(pattern, path + " *", StringComparison.OrdinalIgnoreCase) || string.Equals(pattern, path + "*", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes a choice and the one rule it generates, replacing the previous rule of that choice (same transaction).</summary>
    public void SetChoice(SqliteConnection c, SqliteTransaction tx, long integrationId, long? teamId, string path, string decision, string now, string? note = null)
    {
        var pattern = PatternOf(path);
        var old = Choices(c, tx, integrationId).FirstOrDefault(ch => ch.TeamId == teamId && ch.Path == path);
        if (old?.RuleId is { } oldRule) Run(c, tx, "DELETE FROM integration_rules WHERE id = $id;", ("$id", oldRule));
        Run(c, tx, "DELETE FROM integration_rules WHERE integration_id = $i AND team_id IS $t AND (pattern = $p COLLATE NOCASE OR pattern = $legacy COLLATE NOCASE);",
            ("$i", integrationId), ("$t", teamId), ("$p", pattern), ("$legacy", path + "*"));
        var ruleId = Convert.ToInt64(Scalar(c, tx,
            "INSERT INTO integration_rules (integration_id, team_id, pattern, decision, created_at) VALUES ($i, $t, $p, $d, $now) RETURNING id;",
            ("$i", integrationId), ("$t", teamId), ("$p", pattern), ("$d", decision), ("$now", now)), CultureInfo.InvariantCulture);
        Run(c, tx, "DELETE FROM integration_choices WHERE integration_id = $i AND team_id IS $t AND path = $p;", ("$i", integrationId), ("$t", teamId), ("$p", path));
        Run(c, tx, "INSERT INTO integration_choices (integration_id, team_id, path, decision, rule_id, chosen_at, note) VALUES ($i, $t, $p, $d, $r, $now, $n);",
            ("$i", integrationId), ("$t", teamId), ("$p", path), ("$d", decision), ("$r", ruleId), ("$now", now), ("$n", note));
    }

    /// <summary>A choice that adopts a rule the person typed by hand (the rule stays; the choice owns it from now on).</summary>
    public void AdoptRule(SqliteConnection c, SqliteTransaction tx, long integrationId, long? teamId, string path, string decision, long ruleId, string now) =>
        Run(c, tx, "INSERT INTO integration_choices (integration_id, team_id, path, decision, rule_id, chosen_at, note) VALUES ($i, $t, $p, $d, $r, $now, $n) ON CONFLICT DO NOTHING;",
            ("$i", integrationId), ("$t", teamId), ("$p", path), ("$d", decision), ("$r", ruleId), ("$now", now), ("$n", "from a rule typed by hand"));

    public void RemoveChoice(SqliteConnection c, SqliteTransaction tx, ChoiceRow choice)
    {
        if (choice.RuleId is { } rule) Run(c, tx, "DELETE FROM integration_rules WHERE id = $id;", ("$id", rule));
        Run(c, tx, "DELETE FROM integration_choices WHERE id = $id;", ("$id", choice.Id));
    }

    // ------------------------------------------------------------------ helpers

    public static void Run(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) if (sql.Contains(n, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) if (sql.Contains(n, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
