namespace Yawble.Core.Bus;

/// <summary>A member as the UI lists it: the bus row plus its 0006 settings.</summary>
public sealed record MemberRow(
    long Id, long TeamId, string Team, string Name, string Role, string Cli, string Instructions, string Model, string Credential);

public sealed record TeamRow(long Id, string Name, string? RepoPath, string CreatedAt, string? PausedAt = null, string? ArchivedAt = null)
{
    public bool Paused => PausedAt is not null;
    public bool Archived => ArchivedAt is not null;
}

/// <summary>The UI's reads and edits of teams and members (0002_bus plus 0006_ui).</summary>
public sealed partial class BusStore
{
    public IReadOnlyList<TeamRow> Teams()
    {
        using var cmd = Command("SELECT t.id, t.name, s.repo_path, t.created_at, t.paused_at, t.archived_at FROM teams t LEFT JOIN team_settings s ON s.team_id = t.id ORDER BY t.id;");
        using var r = cmd.ExecuteReader();
        var list = new List<TeamRow>();
        while (r.Read())
            list.Add(new TeamRow(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    // ---- team actions (0016) ----

    /// <summary>Teams that take no new runs: paused or archived.</summary>
    public IReadOnlySet<long> HeldTeams()
    {
        using var cmd = Command("SELECT id FROM teams WHERE paused_at IS NOT NULL OR archived_at IS NOT NULL;");
        using var r = cmd.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    }

    public void SetPaused(long teamId, bool paused) =>
        Exec("UPDATE teams SET paused_at = $at WHERE id = $id;", ("$at", paused ? Now() : null), ("$id", teamId));

    public void SetArchived(long teamId, bool archived) =>
        Exec(archived ? "UPDATE teams SET archived_at = $at, paused_at = coalesce(paused_at, $at) WHERE id = $id;" : "UPDATE teams SET archived_at = NULL WHERE id = $id;",
            ("$at", Now()), ("$id", teamId));

    /// <summary>The team's queued runs leave the queue (its work stays undelivered and queues again on resume).</summary>
    public void DequeueTeamRuns(long teamId) =>
        Exec("DELETE FROM run_queue WHERE kind = 'run' AND member_id IN (SELECT id FROM members WHERE team_id = $t);", ("$t", teamId));

    public string TeamEnvJson(long teamId) => Scalar<string?>("SELECT env FROM teams WHERE id = $id;", ("$id", teamId)) ?? "{}";

    public void SetTeamEnv(long teamId, string json) => Exec("UPDATE teams SET env = $e WHERE id = $id;", ("$e", json), ("$id", teamId));

    /// <summary>A member's instructions that have not started: dropped, and its queue place with them. The count.</summary>
    public int DropWaiting(long memberId, string qualified)
    {
        var n = Scalar<long>("SELECT count(*) FROM pending_deliveries WHERE subscriber = $s AND started = 0;", ("$s", qualified));
        Exec("DELETE FROM pending_deliveries WHERE subscriber = $s AND started = 0;", ("$s", qualified));
        Exec("DELETE FROM run_queue WHERE member_id = $m AND kind = 'run';", ("$m", memberId));
        return (int)n;
    }

    /// <summary>Deletes a team and everything of its members on the bus (the message log stays: it is append-only).</summary>
    public void DeleteTeam(long teamId)
    {
        foreach (var m in TeamMembers(teamId))
        {
            Exec("DELETE FROM subscriptions WHERE subscriber = $s;", ("$s", m.Qualified));
            Exec("DELETE FROM cursors WHERE subscriber = $s;", ("$s", m.Qualified));
            Exec("DELETE FROM pending_deliveries WHERE subscriber = $s;", ("$s", m.Qualified));
            Exec("DELETE FROM member_settings WHERE member_id = $id;", ("$id", m.Id));
            Exec("DELETE FROM member_policy WHERE member_id = $id;", ("$id", m.Id));
        }
        Exec("DELETE FROM run_queue WHERE member_id IN (SELECT id FROM members WHERE team_id = $t);", ("$t", teamId));
        Exec("DELETE FROM teams WHERE id = $t;", ("$t", teamId));
    }

    public IReadOnlyList<MemberRow> MemberRows(long? teamId = null, long? memberId = null)
    {
        using var cmd = Command(
            """
            SELECT m.id, m.team_id, t.name, m.name, m.role, m.cli, m.instructions, coalesce(s.model, ''), coalesce(s.credential, 'default')
            FROM members m JOIN teams t ON t.id = m.team_id LEFT JOIN member_settings s ON s.member_id = m.id
            WHERE ($team IS NULL OR m.team_id = $team) AND ($id IS NULL OR m.id = $id)
            ORDER BY m.team_id, CASE m.role WHEN 'manager' THEN 0 ELSE 1 END, m.id;
            """, ("$team", teamId), ("$id", memberId));
        using var r = cmd.ExecuteReader();
        var list = new List<MemberRow>();
        while (r.Read())
        {
            list.Add(new MemberRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.GetString(6), r.GetString(7), r.GetString(8)));
        }
        return list;
    }

    public void SetTeamRepo(long teamId, string? repoPath) =>
        Exec("""
            INSERT INTO team_settings (team_id, repo_path, updated_at) VALUES ($t, $r, $at)
            ON CONFLICT (team_id) DO UPDATE SET repo_path = excluded.repo_path, updated_at = excluded.updated_at;
            """, ("$t", teamId), ("$r", repoPath), ("$at", Now()));

    public void UpdateMember(long memberId, string cli, string instructions, string model, string credential)
    {
        Exec("UPDATE members SET cli = $c, instructions = $i WHERE id = $id;", ("$c", cli), ("$i", instructions), ("$id", memberId));
        Exec("""
            INSERT INTO member_settings (member_id, model, credential, updated_at) VALUES ($id, $m, $cr, $at)
            ON CONFLICT (member_id) DO UPDATE SET model = excluded.model, credential = excluded.credential, updated_at = excluded.updated_at;
            """, ("$id", memberId), ("$m", model), ("$cr", credential), ("$at", Now()));
    }

    /// <summary>
    /// Deletes a member with everything that addresses it: subscriptions, cursor, pending deliveries, queue entry,
    /// runs (cascade), settings and policy. Its messages stay in the log. Its network asks still waiting for the person are
    /// closed (a later hire with the same name starts with none); returns how many.
    /// </summary>
    public int DeleteMember(MemberRef member)
    {
        var closed = Integrations.NetworkAskService.CloseForRemovedMember(_connection, _tx, member.Id, member.TeamId, member.Qualified, Now());
        Exec("DELETE FROM subscriptions WHERE subscriber = $s;", ("$s", member.Qualified));
        Exec("DELETE FROM cursors WHERE subscriber = $s;", ("$s", member.Qualified));
        Exec("DELETE FROM pending_deliveries WHERE subscriber = $s;", ("$s", member.Qualified));
        Exec("DELETE FROM member_settings WHERE member_id = $id;", ("$id", member.Id));
        Exec("DELETE FROM member_policy WHERE member_id = $id;", ("$id", member.Id));
        Exec("DELETE FROM members WHERE id = $id;", ("$id", member.Id));
        return closed;
    }

    /// <summary>
    /// needs_decision messages from a team's members that the person has not answered yet. A question is answered by a
    /// reply to it, or by anything the person told that member later in the same workflow: an answer to one question
    /// also settles the same question asked again earlier (a second run asking twice), and so does a reply sent with
    /// Tell instead of Answer.
    /// </summary>
    public IReadOnlyList<Message> OpenDecisions(string team) =>
        Messages(
            """
            WHERE type = $type AND source LIKE $prefix ESCAPE '\'
              AND NOT EXISTS (SELECT 1 FROM messages a WHERE a.causation_seq = messages.seq AND a.source = $person)
              AND NOT EXISTS (SELECT 1 FROM messages a WHERE a.source = $person AND a.correlation_id = messages.correlation_id
                              AND a.seq > messages.seq AND a.type = $instr || messages.source)
            """,
            ("$type", MessageTypes.NeedsDecision), ("$prefix", team.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "/%"),
            ("$person", MessageTypes.PersonSource), ("$instr", MessageTypes.InstructionPrefix));

    /// <summary>The most recent ended run of a member (its card shows the reason: completed, failed, stopped...).</summary>
    public RunRow? LastRun(long memberId) => Runs().Where(r => r.MemberId == memberId).MaxBy(r => r.Id);
}
