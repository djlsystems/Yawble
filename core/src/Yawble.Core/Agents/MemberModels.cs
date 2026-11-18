using Microsoft.Data.Sqlite;
using Yawble.Core.Agents.Profiles;
using Yawble.Core.Bus;

namespace Yawble.Core.Agents;

/// <summary>
/// The model a member pane names, and where the name comes from. Source <c>member</c> / <c>agent</c>: the model a run
/// is launched with (<see cref="AgentModels.Resolve"/>, the same order). Source <c>observed</c>: the CLI picks its own
/// model, and this is the one the member's last run used most (the gateway's usage_events, by output tokens, so a CLI's
/// small side-task model does not win); <see cref="At"/> is that run's last request. Source <c>cli</c>: the CLI's own
/// default and no run reported a model yet; <see cref="Model"/> is empty.
/// </summary>
public sealed record MemberModel(string Model, string Source, string? At = null);

public static class MemberModelSources
{
    public const string Observed = "observed";
}

/// <summary>Resolves <see cref="MemberModel"/> for the team page's member cards (teams.agents).</summary>
public sealed class MemberModels(string connectionString, IAgentResolver? agents, AgentDefaultsStore? defaults)
{
    public MemberModel For(MemberRow member)
    {
        ResolvedAgent agent;
        try { agent = agents.Resolve(member.Cli); }
        catch (ArgumentException) { return new MemberModel("", ModelSources.Cli); }
        var set = AgentModels.Resolve(member.Model, agent.Custom ? agent.DefaultModel : defaults?.Get(agent.Cli));
        if (set.Source != ModelSources.Cli) return new MemberModel(set.Model, set.Source);
        return LastRunModel($"{member.Team}/{member.Name}") is { } seen
            ? new MemberModel(seen.Model, MemberModelSources.Observed, seen.At)
            : new MemberModel("", ModelSources.Cli);
    }

    /// <summary>The model the member's latest run (with a reported model) used most, by output tokens.</summary>
    private (string Model, string At)? LastRunModel(string qualified)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            WITH last AS (
                SELECT run_key FROM usage_events
                WHERE member = $m AND model IS NOT NULL AND model <> '' AND run_key IS NOT NULL
                ORDER BY id DESC LIMIT 1)
            SELECT model, (SELECT max(at) FROM usage_events WHERE member = $m AND run_key = (SELECT run_key FROM last)) FROM usage_events
            WHERE member = $m AND run_key = (SELECT run_key FROM last) AND model IS NOT NULL AND model <> ''
            GROUP BY model
            ORDER BY sum(coalesce(output_tokens, 0)) DESC, max(id) DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$m", qualified);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
    }

    public static System.Text.Json.Nodes.JsonObject Json(MemberModel m) => new()
    {
        ["name"] = m.Model,
        ["source"] = m.Source,
        ["at"] = m.At,
    };
}
