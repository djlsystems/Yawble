using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Skills;

/// <summary>Where a skill comes from: compiled into the core, or written by the person.</summary>
public static class SkillKinds
{
    public const string BuiltIn = "builtin";
    public const string Custom = "custom";
}

/// <summary>
/// One skill: a name, a one-line description, the roles it is offered to (manager, member,
/// or any) and a markdown body. A custom skill may be limited to one team (<see cref="TeamId"/>).
/// </summary>
public sealed record Skill(
    string Name,
    string Description,
    IReadOnlyList<string> Roles,
    string Body,
    string Kind,
    long? TeamId = null,
    string? Team = null,
    long? Id = null,
    string? UpdatedAt = null)
{
    public bool OfferedTo(string role) => Roles.Contains("any") || Roles.Contains(role);
}

/// <summary>
/// The skills an agent can load (skills_get / skills_search over the platform's own MCP server, never files in
/// the CLI's skills folder, so every CLI gets the same skills the same way). Built-in skills are embedded in the core
/// (Teams/Roles: manager, member; Teams/Skills: the rest); the person's own are in the database (0009). For a role in a
/// team the offer is: built-ins, then global custom skills, then the team's own; a later one of the same name wins, so
/// the person can replace a built-in.
/// </summary>
public sealed class SkillCatalog(string connectionString)
{
    public static readonly IReadOnlyList<string> Roles = ["manager", "member", "any"];

    /// <summary>The built-in skills, in the order the UI lists them.</summary>
    public static IReadOnlyList<Skill> BuiltIns { get; } = LoadBuiltIns();

    /// <summary>The skills offered to <paramref name="role"/> in <paramref name="teamId"/>, by name.</summary>
    public IReadOnlyList<Skill> Offered(string role, long teamId)
    {
        var byName = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in BuiltIns.Concat(Custom(null)).Concat(Custom(teamId))) byName[s.Name] = s;
        return byName.Values.Where(s => s.OfferedTo(role)).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>One skill offered to the role, or why not.</summary>
    public Skill Get(string name, string role, long teamId)
    {
        var wanted = (name ?? "").Trim();
        var offered = Offered(role, teamId);
        return offered.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"no skill named '{wanted}' is offered to a {role}. Offered: {string.Join(", ", offered.Select(s => s.Name))}");
    }

    /// <summary>Every skill, for the Skills dialog: the built-ins, then each custom one (global, then per team).</summary>
    public IReadOnlyList<Skill> All() => [.. BuiltIns, .. Custom(null), .. CustomOfEveryTeam()];

    /// <summary>Adds or replaces a custom skill (by name, in its scope). The name is a lower-case slug.</summary>
    public Skill Save(string name, string description, IEnumerable<string> roles, string body, long? teamId)
    {
        var n = CheckName(name);
        var d = (description ?? "").Trim();
        if (d.Length == 0 || d.Contains('\n')) throw new ArgumentException("a skill's description is one non-empty line");
        if (d.Length > 300) throw new ArgumentException("a skill's description is at most 300 characters");
        var r = (roles ?? []).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        if (r.Count == 0) throw new ArgumentException("a skill is offered to at least one role (manager, member or any)");
        if (r.FirstOrDefault(x => !Roles.Contains(x)) is { } bad) throw new ArgumentException($"'{bad}' is not a role (manager, member or any)");
        var b = (body ?? "").ReplaceLineEndings("\n").Trim();
        if (b.Length == 0) throw new ArgumentException("a skill's body is the instructions themselves; it cannot be empty");
        if (b.Length > 64 * 1024) throw new ArgumentException("a skill's body is at most 64 KB");

        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = teamId is null
                ? "DELETE FROM skills WHERE name = $n AND team_id IS NULL;"
                : "DELETE FROM skills WHERE name = $n AND team_id = $t;";
            del.Parameters.AddWithValue("$n", n);
            if (teamId is not null) del.Parameters.AddWithValue("$t", teamId);
            del.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO skills (name, description, roles, body, team_id, updated_at) VALUES ($n, $d, $r, $b, $t, $u);";
            ins.Parameters.AddWithValue("$n", n);
            ins.Parameters.AddWithValue("$d", d);
            ins.Parameters.AddWithValue("$r", string.Join(' ', r));
            ins.Parameters.AddWithValue("$b", b);
            ins.Parameters.AddWithValue("$t", (object?)teamId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return (teamId is null ? Custom(null) : Custom(teamId)).Single(s => s.Name == n);
    }

    /// <summary>Removes a custom skill by its id. Built-ins cannot be removed.</summary>
    public bool Delete(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM skills WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public static string CheckName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > 64 || !n.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') || n.StartsWith('-') || n.EndsWith('-'))
            throw new ArgumentException("a skill name is 1-64 lower-case letters, digits and hyphens, e.g. 'release-notes'");
        return n;
    }

    private IReadOnlyList<Skill> Custom(long? teamId) => Query(teamId is null ? "s.team_id IS NULL" : "s.team_id = $t", teamId);

    private IReadOnlyList<Skill> CustomOfEveryTeam() => Query("s.team_id IS NOT NULL", null);

    private IReadOnlyList<Skill> Query(string where, long? teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT s.id, s.name, s.description, s.roles, s.body, s.team_id, t.name, s.updated_at FROM skills s " +
                          $"LEFT JOIN teams t ON t.id = s.team_id WHERE {where} ORDER BY t.name, s.name;";
        if (teamId is not null) cmd.Parameters.AddWithValue("$t", teamId);
        using var r = cmd.ExecuteReader();
        var list = new List<Skill>();
        while (r.Read())
        {
            list.Add(new Skill(r.GetString(1), r.GetString(2), r.GetString(3).Split(' ', StringSplitOptions.RemoveEmptyEntries), r.GetString(4),
                SkillKinds.Custom, r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(0), r.GetString(7)));
        }
        return list;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    // ------------------------------------------------------------------ built-ins

    private static IReadOnlyList<Skill> LoadBuiltIns()
    {
        var list = new List<Skill>
        {
            new("manager", "Your role as a team's Manager: hire the team, split the work, judge hand-backs, finish.", ["manager"],
                Teams.RoleTexts.Manager, SkillKinds.BuiltIn),
            new("member", "Your role as a member: do the card in your own clone, commit, report and hand back.", ["member"],
                Teams.RoleTexts.Member, SkillKinds.BuiltIn),
        };
        var asm = typeof(SkillCatalog).Assembly;
        const string prefix = "Yawble.Core.Skills.";
        foreach (var resource in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            list.Add(SkillFile.Parse(reader.ReadToEnd(), SkillKinds.BuiltIn));
        }
        return list;
    }
}

/// <summary>
/// The SKILL.md form: a front matter block between "---" lines with name, description and an
/// optional roles list (default: manager and member), then the body.
/// </summary>
public static class SkillFile
{
    public static Skill Parse(string text, string kind)
    {
        var t = text.ReplaceLineEndings("\n");
        if (!t.StartsWith("---\n", StringComparison.Ordinal)) throw new FormatException("a skill file starts with a '---' front matter block");
        var end = t.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new FormatException("a skill file's front matter ends with a '---' line");
        string? name = null, description = null;
        var roles = new List<string>();
        foreach (var line in t[4..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"');
            switch (key)
            {
                case "name": name = value; break;
                case "description": description = value; break;
                case "roles": roles.AddRange(value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
            }
        }
        if (name is null || description is null) throw new FormatException("a skill file names its name and description");
        return new Skill(SkillCatalog.CheckName(name), description, roles.Count == 0 ? ["manager", "member"] : roles, t[(end + 5)..].Trim(), kind);
    }
}
