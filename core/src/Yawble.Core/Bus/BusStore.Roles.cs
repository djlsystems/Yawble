namespace Yawble.Core.Bus;

/// <summary>A role of the catalog (0059 roles): its one-word name, the job a hire of it gets, and the suggested agent and model (null: the agent's default).</summary>
public sealed record RoleRow(string Name, string Job, string Agent, string? Model);

/// <summary>One row of a team's hire list (0059 team_hire_list): a role its Manager may hire, with the agent and model (null: the agent's default) the hire runs with.</summary>
public sealed record HireListRow(string Role, string Agent, string? Model);

/// <summary>The role catalog and the teams' hire lists (0059).</summary>
public sealed partial class BusStore
{
    public IReadOnlyList<RoleRow> Roles()
    {
        using var cmd = Command("SELECT name, job, agent, model FROM roles ORDER BY id;");
        using var r = cmd.ExecuteReader();
        var list = new List<RoleRow>();
        while (r.Read()) list.Add(new RoleRow(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    public RoleRow? Role(string name) => Roles().FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a role, or (with <paramref name="originalName"/>) edits one, renaming it in every hire list and on every member hired as it.</summary>
    public void SaveRole(RoleRow role, string? originalName)
    {
        if (originalName is null)
        {
            Exec("INSERT INTO roles (name, job, agent, model, updated_at) VALUES ($n, $j, $a, $m, $at);",
                ("$n", role.Name), ("$j", role.Job), ("$a", role.Agent), ("$m", role.Model), ("$at", Now()));
            return;
        }
        Exec("UPDATE roles SET name = $n, job = $j, agent = $a, model = $m, updated_at = $at WHERE name = $o;",
            ("$n", role.Name), ("$j", role.Job), ("$a", role.Agent), ("$m", role.Model), ("$at", Now()), ("$o", originalName));
        Exec("UPDATE team_hire_list SET role = $n WHERE role = $o;", ("$n", role.Name), ("$o", originalName));
        Exec("UPDATE members SET hire_role = $n WHERE hire_role = $o COLLATE NOCASE;", ("$n", role.Name), ("$o", originalName));
    }

    public void DeleteRole(string name) => Exec("DELETE FROM roles WHERE name = $n;", ("$n", name));

    /// <summary>The teams whose hire list has the role, by name.</summary>
    public IReadOnlyList<string> TeamsUsingRole(string name)
    {
        using var cmd = Command("SELECT t.name FROM team_hire_list h JOIN teams t ON t.id = h.team_id WHERE h.role = $n ORDER BY t.name;", ("$n", name));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public IReadOnlyList<HireListRow> HireList(long teamId)
    {
        using var cmd = Command("SELECT role, agent, model FROM team_hire_list WHERE team_id = $t ORDER BY position, role;", ("$t", teamId));
        using var r = cmd.ExecuteReader();
        var list = new List<HireListRow>();
        while (r.Read()) list.Add(new HireListRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return list;
    }

    /// <summary>Replaces the team's hire list with <paramref name="rows"/>, in their order (none: the team has no hire list and may hire any agent).</summary>
    public void SetHireList(long teamId, IReadOnlyList<HireListRow> rows)
    {
        Exec("DELETE FROM team_hire_list WHERE team_id = $t;", ("$t", teamId));
        for (var i = 0; i < rows.Count; i++)
            Exec("INSERT INTO team_hire_list (team_id, role, agent, model, position) VALUES ($t, $r, $a, $m, $p);",
                ("$t", teamId), ("$r", rows[i].Role), ("$a", rows[i].Agent), ("$m", rows[i].Model), ("$p", i));
    }

    /// <summary>The role a member was hired as (null: none).</summary>
    public void SetHireRole(long memberId, string? role) =>
        Exec("UPDATE members SET hire_role = $r WHERE id = $id;", ("$r", role), ("$id", memberId));
}
