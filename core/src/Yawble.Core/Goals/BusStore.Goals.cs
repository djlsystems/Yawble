using Yawble.Core.Goals;

namespace Yawble.Core.Bus;

/// <summary>The goals and which deliverable has which (migration 0061). Each read is one statement.</summary>
public sealed partial class BusStore
{
    /// <summary>Every goal, by name.</summary>
    public IReadOnlyList<Goal> Goals()
    {
        using var cmd = Command("SELECT id, name, description, created_at, updated_at FROM goals ORDER BY name COLLATE NOCASE, id;");
        using var r = cmd.ExecuteReader();
        var list = new List<Goal>();
        while (r.Read()) list.Add(new Goal(r.GetInt64(0), r.GetString(1), r.GetString(2), ParseTime(r.GetString(3)), ParseTime(r.GetString(4))));
        return list;
    }

    /// <summary>One goal; null when there is none with that id.</summary>
    public Goal? GoalById(long id) => Goals().FirstOrDefault(g => g.Id == id);

    /// <summary>A new goal; its id.</summary>
    public long AddGoal(string name, string description)
    {
        var now = Now();
        using var cmd = Command("INSERT INTO goals (name, description, created_at, updated_at) VALUES ($n, $d, $t, $t) RETURNING id;",
            ("$n", name), ("$d", description), ("$t", now));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>A goal's new name and description; false when there is no such goal.</summary>
    public bool UpdateGoal(long id, string name, string description)
    {
        using var cmd = Command("UPDATE goals SET name = $n, description = $d, updated_at = $t WHERE id = $id;",
            ("$n", name), ("$d", description), ("$t", Now()), ("$id", id));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Deletes a goal (its deliverable_goals rows go with it); false when there is no such goal.</summary>
    public bool DeleteGoal(long id)
    {
        using var cmd = Command("DELETE FROM goals WHERE id = $id;", ("$id", id));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Gives a deliverable its goal, or Unassigned (null): a deliverable has at most one.</summary>
    public void SetDeliverableGoal(long deliverable, long? goal)
    {
        if (goal is { } g)
            Exec("INSERT INTO deliverable_goals (deliverable, goal_id, set_at) VALUES ($d, $g, $t) " +
                 "ON CONFLICT (deliverable) DO UPDATE SET goal_id = excluded.goal_id, set_at = excluded.set_at;",
                ("$d", deliverable), ("$g", g), ("$t", Now()));
        else
            Exec("DELETE FROM deliverable_goals WHERE deliverable = $d;", ("$d", deliverable));
    }

    /// <summary>Every deliverable that has a goal, with its goal's id.</summary>
    public IReadOnlyDictionary<long, long> DeliverableGoals()
    {
        using var cmd = Command("SELECT deliverable, goal_id FROM deliverable_goals;");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<long, long>();
        while (r.Read()) map[r.GetInt64(0)] = r.GetInt64(1);
        return map;
    }
}
