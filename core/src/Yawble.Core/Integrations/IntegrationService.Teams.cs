namespace Yawble.Core.Integrations;

public sealed partial class IntegrationService
{
    /// <summary>
    /// A team is being deleted: every ask still waiting for the person (CLI, mcp: and task:) is recorded as cancelled in
    /// the call history, then the team's offers, choices, rules, watches, asks and network allow list go, all in one transaction. Written out
    /// rather than left to the cascade, which a connection with foreign keys off would skip. Integrations, their default
    /// lists, call history and host-task run records stay.
    /// </summary>
    public void ForgetTeam(long teamId, string teamName)
    {
        var pending = Asks(teamId, true);
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var a in pending)
            Insert(a.Integration, teamName, a.Member, a.Workflow, a.Args, "cancelled", "team deleted", null, null, c, tx);
        foreach (var sql in new[]
        {
            "DELETE FROM integration_asks WHERE team_id = $t;",
            "DELETE FROM integration_choices WHERE team_id = $t;",
            "DELETE FROM integration_rules WHERE team_id = $t;",
            "DELETE FROM integration_teams WHERE team_id = $t;",
            "DELETE FROM mcp_choices WHERE team_id = $t;",
            "DELETE FROM mcp_rules WHERE team_id = $t;",
            "DELETE FROM mcp_server_teams WHERE team_id = $t;",
            "DELETE FROM api_choices WHERE team_id = $t;",
            "DELETE FROM api_rules WHERE team_id = $t;",
            "DELETE FROM api_teams WHERE team_id = $t;",
            "DELETE FROM watches WHERE team_id = $t;",
            // Host tasks (0027): the team's definitions and their approvals; run records stay (task_id goes NULL).
            "DELETE FROM host_task_approvals WHERE task_id IN (SELECT id FROM host_tasks WHERE team_id = $t);",
            "DELETE FROM host_tasks WHERE team_id = $t;",
            // Network asks (0039): waiting or answered, they were about this team's members.
            "DELETE FROM network_asks WHERE team_id = $t;",
            // The team's network allow list (0041): a later team with the same name starts with none.
            "DELETE FROM team_network_allows WHERE team_id = $t;",
            // Tool asks (0040) and what happened to them.
            "DELETE FROM tool_ask_events WHERE ask_id IN (SELECT id FROM tool_asks WHERE team_id = $t);",
            "DELETE FROM tool_asks WHERE team_id = $t;",
        })
            Exec(c, tx, sql, ("$t", teamId));
        tx.Commit();
    }

    /// <summary>
    /// An integration is being removed: every ask of it still waiting for the person is recorded as cancelled (<paramref name="why"/>) in
    /// the call history and deleted, in the same transaction as <paramref name="delete"/>, so an old ask can never run against a later
    /// integration that reuses the name. As <see cref="ForgetTeam"/> does for a team.
    /// </summary>
    private void RemoveWithPendingAsks(string integration, string why, string delete, params (string, object?)[] args)
    {
        var pending = Asks(null, true).Where(a => string.Equals(a.Integration, integration, StringComparison.OrdinalIgnoreCase)).ToList();
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var a in pending)
        {
            var slash = a.Member.IndexOf('/');
            Insert(a.Integration, slash > 0 ? a.Member[..slash] : null, a.Member, a.Workflow, a.Args, "cancelled", why, null, null, c, tx);
            Exec(c, tx, "DELETE FROM integration_asks WHERE id = $id AND state = 'pending';", ("$id", a.Id));
        }
        Exec(c, tx, delete, args);
        tx.Commit();
    }
}
