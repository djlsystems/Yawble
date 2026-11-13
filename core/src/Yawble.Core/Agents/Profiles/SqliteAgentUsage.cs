using Microsoft.Data.Sqlite;

namespace Yawble.Core.Agents.Profiles;

/// <summary>
/// Who runs on an agent, read from the database: members and Managers (members.cli, with their own model from member_settings)
/// and the Concierge's conversations (concierge_conversations.cli). Move rewrites them to another agent.
/// </summary>
public sealed class SqliteAgentUsage(string connectionString) : IAgentUsage
{
    public IReadOnlyList<AgentUser> UsersOf(string agent)
    {
        using var c = Open();
        var list = new List<AgentUser>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT t.name, m.name, m.role, COALESCE(s.model, '')
                FROM members m JOIN teams t ON t.id = m.team_id LEFT JOIN member_settings s ON s.member_id = m.id
                WHERE m.cli = $a COLLATE NOCASE ORDER BY m.id;
                """;
            cmd.Parameters.AddWithValue("$a", agent);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var team = r.GetString(0);
                var manager = r.GetString(2) == "manager";
                list.Add(manager
                    ? new AgentUser("manager", AgentChangeLog.ManagerSubject(team), $"{team}/{r.GetString(1)} (Manager)", r.GetString(3))
                    : new AgentUser("member", AgentChangeLog.MemberSubject(team, r.GetString(1)), $"{team}/{r.GetString(1)}", r.GetString(3)));
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title FROM concierge_conversations WHERE cli = $a COLLATE NOCASE ORDER BY id;";
            cmd.Parameters.AddWithValue("$a", agent);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AgentUser("concierge", AgentChangeLog.ConciergeSubject(r.GetString(0)), $"Concierge chat \"{r.GetString(1)}\"", ""));
        }
        return list;
    }

    public void Move(AgentUser user, string toAgent)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (user.Kind == "concierge")
        {
            cmd.CommandText = "UPDATE concierge_conversations SET cli = $to WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", user.Subject[(user.Subject.IndexOf(':') + 1)..]);
        }
        else
        {
            // member:<team>/<name> or manager:<team> (the team's one Manager).
            var rest = user.Subject[(user.Subject.IndexOf(':') + 1)..];
            if (user.Kind == "manager")
            {
                cmd.CommandText = "UPDATE members SET cli = $to WHERE role = 'manager' AND team_id = (SELECT id FROM teams WHERE name = $team COLLATE NOCASE);";
                cmd.Parameters.AddWithValue("$team", rest);
            }
            else
            {
                var slash = rest.IndexOf('/');
                cmd.CommandText = "UPDATE members SET cli = $to WHERE name = $name COLLATE NOCASE AND team_id = (SELECT id FROM teams WHERE name = $team COLLATE NOCASE);";
                cmd.Parameters.AddWithValue("$team", rest[..slash]);
                cmd.Parameters.AddWithValue("$name", rest[(slash + 1)..]);
            }
        }
        cmd.Parameters.AddWithValue("$to", toAgent);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }
}
