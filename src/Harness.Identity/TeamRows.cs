using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// WHAT A TEAM STORE WRITES INTO A COLUMN AND READS BACK OUT OF ONE, whichever database holds the
/// rows: the column lists, the value each parameter takes and the row readers. Both providers' team
/// stores call these, so a row either writes reads the same on the other; only the SQL differs.
///
/// Readers take <see cref="DbDataReader"/> and read every integer with <c>GetInt64</c>, narrowing
/// where the record holds an <c>int</c>: SQLite's INTEGER and SQL Server's BIGINT both answer it.
/// </summary>
public static class TeamRows
{
    /// <summary>The columns <see cref="ReadTeam"/> reads, in its order.</summary>
    public const string TeamColumns =
        "id, name, member_agent, member_agents, additional_instructions, root, repos, env, paused, "
        + "budget_tokens, archived_at, archived_by";

    /// <summary>The columns <see cref="ReadMember"/> reads, in its order.</summary>
    public const string MemberColumns =
        "team, name, label, agent, system_prompt, subscribes, permits, floor_seq, hired_for, "
        + "system_prompt_set_by, system_prompt_set_by_kind, system_prompt_set_at";

    /// <summary>The columns <see cref="ReadContributor"/> reads, in its order.</summary>
    public const string ContributorColumns =
        "team, repo, upstream_url, fork_owner, dco_sign_off, cla_signed_note, pr_url, pr_number, pr_state, pr_read_at";

    /// <summary><c>teams.member_agent</c>: the named one, else the first of the list, else NULL.</summary>
    public static object MemberAgent(PersistedTeam team) =>
        team.MemberAgent is { } memberAgent
            ? memberAgent
            : team.MemberAgents?.FirstOrDefault() is { } first
                ? first
                : DBNull.Value;

    /// <summary><c>teams.member_agents</c>: the list as JSON, or NULL when the team names none.</summary>
    public static object MemberAgents(IReadOnlyList<string>? memberAgents) =>
        memberAgents is null ? DBNull.Value : JsonSerializer.Serialize(memberAgents);

    /// <summary><c>teams.additional_instructions</c>: NULL for blank text, else the text as given.</summary>
    public static object Instructions(string? text) =>
        string.IsNullOrWhiteSpace(text) ? DBNull.Value : text;

    /// <summary><c>teams.repos</c>: the list as JSON, empty for none.</summary>
    public static string Repos(IReadOnlyList<string>? repos) => JsonSerializer.Serialize(repos ?? []);

    /// <summary><c>teams.env</c>: the environment as a JSON object, empty for none.</summary>
    public static string Env(IReadOnlyDictionary<string, string>? env) =>
        JsonSerializer.Serialize(env ?? new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>A JSON array of <paramref name="values"/>: a member's subscribes and permits.</summary>
    public static string List(IReadOnlyCollection<string> values) => JsonSerializer.Serialize(values.ToArray());

    /// <summary>A nullable text value as a parameter takes it.</summary>
    public static object Text(string? value) => (object?)value ?? DBNull.Value;

    /// <summary>A nullable instant as a parameter takes it: round-trip text, or NULL.</summary>
    public static object Stamp(DateTimeOffset? at) => at is { } value ? Instant.Stamp(value) : DBNull.Value;

    public static PersistedTeam ReadTeam(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3)
                ? null
                : JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [],
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6)
                ? []
                : JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
            reader.IsDBNull(7)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7))
                  ?? new Dictionary<string, string>(StringComparer.Ordinal),
            // `member_agent` IS NOT UNREAD: it is selected above and Teams.cs reads it back, so it
            // is not a drop candidate.
            !reader.IsDBNull(8) && reader.GetInt64(8) != 0,

            // The budget is WRITTEN by a narrow setter that does not go through
            // SaveTeamAsync, so a column missing from this SELECT is a setting that works all
            // session and is gone at the next restart. NULL IS NOT 0: null means the team has
            // chosen nothing, 0 means it chose unlimited.
            reader.IsDBNull(9) ? null : reader.GetInt64(9),

            // Written by the narrow archive setter only, so read here or lost at a restart.
            reader.IsDBNull(10)
                ? null
                : Instant.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11));

    public static PersistedMember ReadMember(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
            JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) || reader.IsDBNull(10) || reader.IsDBNull(11)
                ? null
                : new SystemPromptSetter(reader.GetString(9), reader.GetString(10), Instant.Parse(reader.GetString(11))));

    /// <summary>A <c>team_repo_default_branches</c> row: team, repo, from_remote, set_by_person.</summary>
    public static RepoDefaultBranch ReadDefaultBranch(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));

    /// <summary>A <c>team_repo_contributors</c> row, from the columns <see cref="ContributorColumns"/> names.</summary>
    public static RepoContributor ReadContributor(DbDataReader reader)
    {
        // A pull request is recorded whole or not at all.
        var pullRequest = reader.IsDBNull(6) || reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9)
            ? null
            : new RepoPullRequest(
                reader.GetString(6),
                checked((int)reader.GetInt64(7)),
                reader.GetString(8),
                Instant.Parse(reader.GetString(9)));

        return new RepoContributor(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4) != 0,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            pullRequest);
    }
}
