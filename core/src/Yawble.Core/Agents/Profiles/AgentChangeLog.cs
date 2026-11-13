using System.Globalization;
using Microsoft.Data.Sqlite;
using Yawble.Core.Provenance;

namespace Yawble.Core.Agents.Profiles;

/// <summary>One row of the agent_changes log (0035_agent_profiles.sql).</summary>
public sealed record AgentChange(
    long Id, string At, string Kind, string Subject, string? FromAgent, string? ToAgent, string? FromModel, string? ToModel,
    string EffectiveModel, string ActorVia, string? ActorUser, string How, string? Detail);

/// <summary>What a subject (a member, a Manager, the Concierge) runs with: an agent (built-in or custom name) and a model ("" = follow the agent's default).</summary>
public sealed record AgentSetting(string Agent, string Model);

/// <summary>
/// The append-only log of every change of agent or model, so cost can be priced by what a run used at the time. One row per
/// change; rows are never edited or deleted (triggers refuse it). Local only: nothing here goes over the Cloud Link.
/// </summary>
public sealed class AgentChangeLog(string connectionString, TimeProvider? clock = null, Func<string, string, string>? resolveModel = null)
{
    public const string KindMemberAgent = "member-agent", KindManagerAgent = "manager-agent", KindConciergeAgent = "concierge-agent",
        KindAgentDefaultModel = "agent-default-model", KindCustomAgentArgs = "custom-agent-args";

    /// <summary>How a change came in.</summary>
    public const string HowSettings = "settings", HowConciergeTool = "concierge-tool", HowTeamCreate = "team_create", HowMigration = "migration",
        HowManagerHire = "manager-hire", HowCompanion = "companion";

    /// <summary>What the model means when nothing names one: the CLI picks.</summary>
    public const string CliDefault = "cli-default";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static string MemberSubject(string team, string member) => $"member:{team}/{member}";
    public static string ManagerSubject(string team) => $"manager:{team}";
    public static string ConciergeSubject(string conversation) => $"concierge:{conversation}";
    public static string AgentSubject(string agent) => $"agent:{agent}";

    /// <summary>
    /// A member's, Manager's or Concierge's agent or model changed. Writes exactly one row, with the model resolved through the
    /// agent defaults; writes none when nothing changed. <paramref name="kind"/> is one of the three Kind...Agent constants.
    /// </summary>
    public AgentChange? RecordSubjectChange(string kind, string subject, AgentSetting from, AgentSetting to, string how, Actor? actor = null)
    {
        if (string.Equals(from.Agent, to.Agent, StringComparison.OrdinalIgnoreCase) && from.Model == to.Model) return null;
        return Append(kind, subject, from.Agent, to.Agent, from.Model, to.Model, Resolve(to.Agent, to.Model), how, actor, null);
    }

    /// <summary>An agent's default model changed (which changes every subject following it): one row for the agent.</summary>
    public AgentChange? RecordDefaultModelChange(string agent, string fromModel, string toModel, string how, Actor? actor = null) =>
        fromModel == toModel ? null
            : Append(KindAgentDefaultModel, AgentSubject(agent), agent, agent, fromModel, toModel, string.IsNullOrEmpty(toModel) ? CliDefault : toModel, how, actor, null);

    /// <summary>A custom agent's arguments changed: one row, the detail says which lists.</summary>
    public AgentChange RecordArgsChange(string agent, string model, string detail, string how, Actor? actor = null) =>
        Append(KindCustomAgentArgs, AgentSubject(agent), agent, agent, model, model, Resolve(agent, model), how, actor, detail);

    private string Resolve(string agent, string model)
    {
        var resolved = resolveModel?.Invoke(agent, model) ?? model;
        return string.IsNullOrEmpty(resolved) ? CliDefault : resolved;
    }

    private AgentChange Append(string kind, string subject, string? fromAgent, string? toAgent, string? fromModel, string? toModel,
        string effective, string how, Actor? actor, string? detail)
    {
        var who = actor ?? Actor.Current ?? new Actor(Actor.Host, null);
        var at = _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_changes (at, kind, subject, from_agent, to_agent, from_model, to_model, effective_model, actor_via, actor_user, how, detail)
            VALUES ($at, $kind, $subject, $fa, $ta, $fm, $tm, $eff, $via, $user, $how, $detail) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$at", at);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$subject", subject);
        cmd.Parameters.AddWithValue("$fa", (object?)fromAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ta", (object?)toAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fm", (object?)fromModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tm", (object?)toModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$eff", effective);
        cmd.Parameters.AddWithValue("$via", who.Via);
        cmd.Parameters.AddWithValue("$user", (object?)who.User ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$how", how);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new AgentChange(id, at, kind, subject, fromAgent, toAgent, fromModel, toModel, effective, who.Via, who.User, how, detail);
    }

    // ---------------------------------------------------------------- history (newest first)

    private const string Columns = "id, at, kind, subject, from_agent, to_agent, from_model, to_model, effective_model, actor_via, actor_user, how, detail";

    /// <summary>Every change, newest first.</summary>
    public IReadOnlyList<AgentChange> All(int limit = 200) => Query("1 = 1", limit);

    /// <summary>The history of one member.</summary>
    public IReadOnlyList<AgentChange> HistoryOfMember(string team, string member, int limit = 200) =>
        Query("subject = $s", limit, ("$s", MemberSubject(team, member)));

    /// <summary>The history of one subject, by its full subject string (a Manager, a Concierge conversation).</summary>
    public IReadOnlyList<AgentChange> HistoryOfSubject(string subject, int limit = 200) => Query("subject = $s", limit, ("$s", subject));

    /// <summary>The history of one agent: its own settings and every subject that moved to or from it.</summary>
    public IReadOnlyList<AgentChange> HistoryOfAgent(string agent, int limit = 200) =>
        Query("(subject = $s OR from_agent = $a COLLATE NOCASE OR to_agent = $a COLLATE NOCASE)", limit, ("$s", AgentSubject(agent)), ("$a", agent));

    private IReadOnlyList<AgentChange> Query(string where, int limit, params (string, object)[] args)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM agent_changes WHERE {where} ORDER BY id DESC LIMIT $limit;";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var r = cmd.ExecuteReader();
        var list = new List<AgentChange>();
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
            list.Add(new AgentChange(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), S(4), S(5), S(6), S(7), r.GetString(8),
                r.GetString(9), S(10), r.GetString(11), S(12)));
        return list;
    }
}
