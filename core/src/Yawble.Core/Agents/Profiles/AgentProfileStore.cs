using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;

using Yawble.Core.Sandboxing;

namespace Yawble.Core.Agents.Profiles;

/// <summary>A custom agent (a duplicate of a built-in) or a built-in's own settings row (Custom = false: only DefaultModel counts).</summary>
public sealed record AgentProfile(
    long Id, string Name, AgentCli BaseCli, bool Custom, string DefaultModel,
    IReadOnlyList<string> ArgsBefore, IReadOnlyList<string> ArgsAfter, IReadOnlyList<string> RemovedArgs, string CreatedAt, string UpdatedAt);

/// <summary>Something that runs on an agent: a member, a Manager or the Concierge, with the model it has set ("" = follows the agent's default).</summary>
public sealed record AgentUser(string Kind, string Subject, string Label, string Model);

/// <summary>Who runs on which agent; the members/Manager/Concierge stores implement it. Move rewrites them to another agent.</summary>
public interface IAgentUsage
{
    IReadOnlyList<AgentUser> UsersOf(string agent);
    void Move(AgentUser user, string toAgent);
}

public sealed class AgentProfileException(string message, IReadOnlyList<ArgProblem>? problems = null) : Exception(message)
{
    public IReadOnlyList<ArgProblem> Problems { get; } = problems ?? [];
}

/// <summary>
/// The custom agents and the built-ins' default models (0035_agent_profiles.sql). Saving validates the argument lists (locked ones are
/// refused with their reason); every change of a default model or of a custom agent's arguments writes its agent_changes row.
/// </summary>
public sealed class AgentProfileStore(string connectionString, AgentChangeLog log, IAgentUsage? usage = null, TimeProvider? clock = null, AgentDefaultsStore? defaults = null) : IAgentResolver
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly AgentDefaultsStore _defaults = defaults ?? new AgentDefaultsStore(connectionString, clock);

    /// <summary>The built-in agents' names (the CLI's name, lower case).</summary>
    public static string BuiltInName(AgentCli cli) => cli.ToString().ToLowerInvariant();

    public static AgentCli? BuiltIn(string name) =>
        Enum.GetValues<AgentCli>().Cast<AgentCli?>().FirstOrDefault(c => string.Equals(BuiltInName(c!.Value), name, StringComparison.OrdinalIgnoreCase));

    private string Now() => _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- reading

    public IReadOnlyList<AgentProfile> Custom() => Query("custom = 1 ORDER BY name");

    public AgentProfile? Get(string name) => BuiltIn(name) is { } cli ? BuiltInRow(cli) : Query("custom = 1 AND name = $n", ("$n", name)).FirstOrDefault();

    public AgentProfile? Get(long id) => Query("id = $i", ("$i", id)).FirstOrDefault();

    /// <summary>Every agent a person can choose: the built-ins (with their default model) then the custom agents.</summary>
    public IReadOnlyList<AgentProfile> Choices() => [.. Enum.GetValues<AgentCli>().Select(BuiltInRow), .. Custom()];

    // A built-in's default model lives in agent_defaults (0033, AgentDefaultsStore): the one place the launcher reads too.
    private AgentProfile BuiltInRow(AgentCli cli) =>
        new(0, BuiltInName(cli), cli, false, _defaults.Get(cli), [], [], [], "", "");

    /// <summary>A store that only reads (the resolver of a site that has a connection string and nothing else).</summary>
    public static AgentProfileStore ForReading(string connectionString) => new(connectionString, new AgentChangeLog(connectionString));

    public ResolvedAgent? Find(string name) =>
        Get((name ?? "").Trim()) is { } p ? new ResolvedAgent(p.Custom ? p.Name : BuiltInName(p.BaseCli), p.BaseCli, p.Custom, p.DefaultModel, Customization(p)) : null;

    public IReadOnlyList<string> Names() => [.. Choices().Select(c => c.Name)];

    /// <summary>The CLI behind an agent name (built-in or custom); null when no such agent.</summary>
    public AgentCli? CliOf(string agent) => Get(agent)?.BaseCli;

    /// <summary>The model a run uses: the member's own, else the agent's default, else "" (the CLI's default).</summary>
    public string ResolveModel(string agent, string memberModel) =>
        !string.IsNullOrWhiteSpace(memberModel) ? memberModel.Trim() : Get(agent)?.DefaultModel ?? "";

    // ---------------------------------------------------------------- custom agents

    /// <summary>Duplicates a built-in into a named custom agent that inherits the CLI's detection, sign-in, credential mode, grants and hosts (all by base_cli).</summary>
    public AgentProfile Duplicate(AgentCli baseCli, string name, string? defaultModel = null)
    {
        name = CheckName(name, null);
        var model = defaultModel ?? BuiltInRow(baseCli).DefaultModel;
        var now = Now();
        Exec("INSERT INTO agent_profiles (name, base_cli, custom, default_model, created_at, updated_at) VALUES ($n, $c, 1, $m, $t, $t);",
            ("$n", name), ("$c", baseCli.ToString()), ("$m", model), ("$t", now));
        return Get(name)!;
    }

    /// <summary>
    /// Saves a custom agent's name, default model and argument lists. Refuses locked arguments (<see cref="AgentProfileException.Problems"/>).
    /// A changed default model and changed arguments each write one agent_changes row.
    /// </summary>
    public AgentProfile Update(long id, string name, string defaultModel, IReadOnlyList<string> before, IReadOnlyList<string> after,
        IReadOnlyList<string> removed, string how = AgentChangeLog.HowSettings, Actor? actor = null)
    {
        var old = Get(id) is { Custom: true } p ? p : throw new AgentProfileException("no such custom agent");
        name = CheckName(name, id);
        if (name != old.Name && usage?.UsersOf(old.Name) is { Count: > 0 } users)
            throw new AgentProfileException($"{old.Name} is in use by {string.Join(", ", users.Select(u => u.Label))}; it cannot be renamed while they run on it");
        defaultModel = (defaultModel ?? "").Trim();
        var problems = AgentArgsValidator.Validate(old.BaseCli, before, after, removed);
        if (problems.Count > 0) throw new AgentProfileException(string.Join(" ", problems.Select(x => x.Reason)), problems);

        Exec("UPDATE agent_profiles SET name = $n, default_model = $m, args_before = $b, args_after = $a, removed_args = $r, updated_at = $t WHERE id = $i;",
            ("$n", name), ("$m", defaultModel), ("$b", Json(before)), ("$a", Json(after)), ("$r", Json(removed)), ("$t", Now()), ("$i", id));

        if (old.DefaultModel != defaultModel) log.RecordDefaultModelChange(name, old.DefaultModel, defaultModel, how, actor);
        var changed = new List<string>();
        if (!old.ArgsBefore.SequenceEqual(before)) changed.Add("before");
        if (!old.ArgsAfter.SequenceEqual(after)) changed.Add("after");
        if (!old.RemovedArgs.SequenceEqual(removed)) changed.Add("removed");
        if (changed.Count > 0) log.RecordArgsChange(name, defaultModel, "changed: " + string.Join(", ", changed), how, actor);
        return Get(id)!;
    }

    /// <summary>Sets a built-in agent's default model ("" = the CLI's default). One row in the log when it changed.</summary>
    public AgentProfile SetDefaultModel(AgentCli cli, string model, string how = AgentChangeLog.HowSettings, Actor? actor = null)
    {
        model = (model ?? "").Trim();
        var old = BuiltInRow(cli);
        _defaults.Set(cli, model);
        log.RecordDefaultModelChange(BuiltInName(cli), old.DefaultModel, model, how, actor);
        return BuiltInRow(cli);
    }

    /// <summary>
    /// Deletes a custom agent. While members, Managers or the Concierge run on it the delete is refused naming them, unless
    /// <paramref name="moveToBase"/>: they move to the base agent first (one log row each).
    /// </summary>
    public void Delete(long id, bool moveToBase = false, string how = AgentChangeLog.HowSettings, Actor? actor = null)
    {
        var p = Get(id) is { Custom: true } found ? found : throw new AgentProfileException("no such custom agent");
        var users = usage?.UsersOf(p.Name) ?? [];
        if (users.Count > 0)
        {
            if (!moveToBase)
                throw new AgentProfileException($"{p.Name} is in use by {string.Join(", ", users.Select(u => u.Label))}; move them to {BuiltInName(p.BaseCli)} or choose another agent for them first");
            var to = BuiltInName(p.BaseCli);
            foreach (var u in users)
            {
                usage!.Move(u, to);
                var kind = u.Kind switch { "manager" => AgentChangeLog.KindManagerAgent, "concierge" => AgentChangeLog.KindConciergeAgent, _ => AgentChangeLog.KindMemberAgent };
                log.RecordSubjectChange(kind, u.Subject, new AgentSetting(p.Name, u.Model), new AgentSetting(to, u.Model), how, actor);
            }
        }
        Exec("DELETE FROM agent_profiles WHERE id = $i AND custom = 1;", ("$i", id));
    }

    // ---------------------------------------------------------------- argv

    /// <summary>
    /// The profile's argument changes for RunSpec.Custom / AgentPresets.Arguments (the one argv function): null for a built-in
    /// (or no profile) and for a custom agent with no changes. Removed items are a flag, or "flag value" for a pair.
    /// </summary>
    public static ArgvCustomization? Customization(AgentProfile? profile) =>
        profile is { Custom: true } && new ArgvCustomization(profile.ArgsBefore, profile.ArgsAfter, profile.RemovedArgs) is { IsEmpty: false } c ? c : null;

    /// <summary>The customization of the named agent (null for a built-in or an unknown name).</summary>
    public ArgvCustomization? CustomizationOf(string agent) => Customization(Get(agent));

    // ---------------------------------------------------------------- plumbing

    private string CheckName(string name, long? ownId)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl)) throw new AgentProfileException("an agent's name is 1 to 80 characters, without control characters");
        if (BuiltIn(name) is not null) throw new AgentProfileException($"{name} is a built-in agent's name; choose another");
        if (Query("custom = 1 AND name = $n", ("$n", name)).FirstOrDefault() is { } other && other.Id != ownId)
            throw new AgentProfileException($"an agent named {name} already exists");
        return name;
    }

    private static string Json(IReadOnlyList<string> list) => JsonSerializer.Serialize(list);

    private static IReadOnlyList<string> List(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];

    private IReadOnlyList<AgentProfile> Query(string where, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, base_cli, custom, default_model, args_before, args_after, removed_args, created_at, updated_at FROM agent_profiles WHERE " + where + ";";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<AgentProfile>();
        while (r.Read())
            list.Add(new AgentProfile(r.GetInt64(0), r.GetString(1), Enum.Parse<AgentCli>(r.GetString(2)), r.GetInt64(3) != 0, r.GetString(4),
                List(r.GetString(5)), List(r.GetString(6)), List(r.GetString(7)), r.GetString(8), r.GetString(9)));
        return list;
    }

    private void Exec(string sql, params (string, object)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }
}

