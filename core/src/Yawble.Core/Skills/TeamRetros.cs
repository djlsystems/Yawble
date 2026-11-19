using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Skills;

/// <summary>
/// B100: a team's lessons, the team-scoped custom skill team_retros (roles: any). The Manager writes it with
/// team_retros_write after a workflow's retrospective; the person reads and edits it in Settings > Skills; every run of the
/// team gets it in its prompt (Teams/TeamPrompt.cs). Every write is a version (skill_versions, 0052).
/// </summary>
public static class TeamRetros
{
    public const string Name = "team_retros";

    /// <summary>The most the Manager may write (the text goes into every run's prompt of the team).</summary>
    public const int MaxChars = 4000;

    /// <summary>The writer of a save or restore in Settings > Skills.</summary>
    public const string PersonWriter = "person";

    /// <summary>The versions kept per team: the oldest beyond this are dropped on a write.</summary>
    public const int KeptVersions = 100;

    public const string Description = "Lessons from this team's earlier work, kept by the Manager's retrospectives. Already in every run's prompt.";

    public const string PromptHeading = "Lessons from this team's earlier work";

    /// <summary>A lesson is a list line ("- ..." or "* ...").</summary>
    public static int CountLessons(string? body) =>
        (body ?? "").ReplaceLineEndings("\n").Split('\n').Count(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal) || l.TrimStart().StartsWith("* ", StringComparison.Ordinal));
}

/// <summary>One kept write of a team skill: who (a member, or "person"), in which workflow, when.</summary>
public sealed record SkillVersion(long Id, long TeamId, string Name, int Version, string Body, string Writer, long? Workflow, long? RestoredFrom, string WrittenAt);

/// <summary>Team settings' line: how many lessons, and the last write.</summary>
public sealed record TeamLessons(int Count, int Version, string? UpdatedAt, string? Writer, long? Workflow, long? SkillId);

/// <summary>The current team_retros of a team as a run reads it: its text (null when there is none) and its version.</summary>
public sealed record TeamRetrosState(string? Body, int Version);

/// <summary>A team_retros write refused: too long, or the text changed since the writer read it.</summary>
public sealed class TeamRetrosRefusal(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

public sealed partial class SkillCatalog
{
    private sealed record VersionStamp(string Writer, long? Workflow, long? RestoredFrom);

    /// <summary>The team's own team_retros skill (never a global one of that name), or null.</summary>
    public Skill? TeamRetrosSkill(long teamId) => Custom(teamId).FirstOrDefault(s => s.Name == TeamRetros.Name);

    /// <summary>The team's lessons now: the text a run's prompt carries and the version it was read at.</summary>
    public TeamRetrosState TeamRetrosNow(long teamId)
    {
        var body = TeamRetrosSkill(teamId)?.Body;
        return new TeamRetrosState(string.IsNullOrWhiteSpace(body) ? null : body, CurrentVersion(teamId, TeamRetros.Name));
    }

    /// <summary>
    /// team_retros_write: the whole new text of the team's team_retros, written by <paramref name="writer"/> (the Manager)
    /// in <paramref name="workflow"/>. Refused above <see cref="TeamRetros.MaxChars"/>, and when the text changed since the
    /// writer read it (<paramref name="seenVersion"/>: the version in its prompt or its last skills_get), so a person's edit
    /// in Settings is never overwritten unread. Keeps the skill's description and roles when it exists (the person may have
    /// changed them), creates it (roles: any) on the first write.
    /// </summary>
    public SkillVersion WriteTeamRetros(long teamId, string text, string writer, long? workflow, int? seenVersion)
    {
        var body = (text ?? "").ReplaceLineEndings("\n").Trim();
        if (body.Length == 0)
            throw new TeamRetrosRefusal("empty", "team_retros_write takes the whole new text of team_retros; it cannot be empty. To keep the lessons as they are, do not write.");
        if (body.Length > TeamRetros.MaxChars)
            throw new TeamRetrosRefusal("too_long", $"team_retros is at most {TeamRetros.MaxChars} characters; this text is {body.Length}. " +
                "Merge or drop lessons (keep the ones a later run should act on) and write it again.");
        var existing = TeamRetrosSkill(teamId);
        Save(TeamRetros.Name, existing?.Description ?? TeamRetros.Description, existing?.Roles ?? ["any"], body, teamId,
            new VersionStamp(writer, workflow, null), (c, tx) =>
            {
                var now = CurrentVersion(c, tx, teamId, TeamRetros.Name);
                if (now == 0 || seenVersion == now) return;
                var last = Versions(c, tx, teamId, TeamRetros.Name, 1).FirstOrDefault();
                var who = last?.Writer == TeamRetros.PersonWriter ? "the person (in Settings > Skills)" : last?.Writer ?? "someone";
                throw new TeamRetrosRefusal("changed", seenVersion is null
                    ? $"team_retros is at version {now} and you have not read it in this run: load it with skills_get team_retros, merge your lessons into that text and write the whole text again."
                    : $"team_retros changed since you read it (version {seenVersion}, now {now}, written by {who}): load it again with skills_get team_retros, " +
                      "keep that text (never drop the person's edits) and merge your lessons into it, then write the whole text again.");
            });
        return Versions(teamId, TeamRetros.Name, 1)[0];
    }

    /// <summary>The kept versions of a team skill, newest first.</summary>
    public IReadOnlyList<SkillVersion> Versions(long teamId, string name, int limit = TeamRetros.KeptVersions)
    {
        using var c = Open();
        return Versions(c, null, teamId, name, limit);
    }

    /// <summary>Settings > Skills' Restore: the version's text becomes the current text again, as the person's new version.</summary>
    public Skill Restore(long versionId)
    {
        SkillVersion v;
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"{VersionColumns} WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", versionId);
            using var r = cmd.ExecuteReader();
            v = r.Read() ? ReadVersion(r) : throw new ArgumentException($"no skill version {versionId}");
        }
        var existing = Custom(v.TeamId).FirstOrDefault(s => s.Name == v.Name);
        return Save(v.Name, existing?.Description ?? TeamRetros.Description, existing?.Roles ?? ["any"], v.Body, v.TeamId,
            new VersionStamp(TeamRetros.PersonWriter, null, v.Version));
    }

    /// <summary>Team settings: "Lessons: n, last updated &lt;date&gt; by ...".</summary>
    public TeamLessons Lessons(long teamId)
    {
        var skill = TeamRetrosSkill(teamId);
        var last = Versions(teamId, TeamRetros.Name, 1).FirstOrDefault();
        return new TeamLessons(TeamRetros.CountLessons(skill?.Body), last?.Version ?? 0, last?.WrittenAt ?? skill?.UpdatedAt,
            last?.Writer, last?.Workflow, skill?.Id);
    }

    private int CurrentVersion(long teamId, string name)
    {
        using var c = Open();
        return CurrentVersion(c, null, teamId, name);
    }

    private static int CurrentVersion(SqliteConnection c, SqliteTransaction? tx, long teamId, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM skill_versions WHERE team_id = $t AND name = $n;";
        cmd.Parameters.AddWithValue("$t", teamId);
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private const string VersionColumns = "SELECT id, team_id, name, version, body, writer, workflow_id, restored_from, written_at FROM skill_versions";

    private static SkillVersion ReadVersion(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
        r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7), r.GetString(8));

    private static List<SkillVersion> Versions(SqliteConnection c, SqliteTransaction? tx, long teamId, string name, int limit)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{VersionColumns} WHERE team_id = $t AND name = $n ORDER BY version DESC LIMIT $l;";
        cmd.Parameters.AddWithValue("$t", teamId);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<SkillVersion>();
        while (r.Read()) list.Add(ReadVersion(r));
        return list;
    }

    private static void AddVersion(SqliteConnection c, SqliteTransaction tx, long teamId, string name, string body, VersionStamp stamp)
    {
        var next = CurrentVersion(c, tx, teamId, name) + 1;
        using (var ins = c.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO skill_versions (team_id, name, version, body, writer, workflow_id, restored_from, written_at) " +
                              "VALUES ($t, $n, $v, $b, $w, $wf, $r, $at);";
            ins.Parameters.AddWithValue("$t", teamId);
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$v", next);
            ins.Parameters.AddWithValue("$b", body);
            ins.Parameters.AddWithValue("$w", stamp.Writer);
            ins.Parameters.AddWithValue("$wf", (object?)stamp.Workflow ?? DBNull.Value);
            ins.Parameters.AddWithValue("$r", (object?)stamp.RestoredFrom ?? DBNull.Value);
            ins.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            ins.ExecuteNonQuery();
        }
        using var prune = c.CreateCommand();
        prune.Transaction = tx;
        prune.CommandText = "DELETE FROM skill_versions WHERE team_id = $t AND name = $n AND version <= $cut;";
        prune.Parameters.AddWithValue("$t", teamId);
        prune.Parameters.AddWithValue("$n", name);
        prune.Parameters.AddWithValue("$cut", next - TeamRetros.KeptVersions);
        prune.ExecuteNonQuery();
    }
}
