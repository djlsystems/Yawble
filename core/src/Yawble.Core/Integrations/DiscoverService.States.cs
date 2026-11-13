using System.Globalization;

namespace Yawble.Core.Integrations;

/// <summary>The state of one checklist row, as the person sees it: allowed, ask first, off, never (plus the two "no choice" words).</summary>
public static class RowStates
{
    public const string Allow = "allow", Ask = "ask", Off = "off", Never = "never", Default = "default", Undecided = "undecided";

    /// <summary>The rule decision a state stores. Never, default and undecided are not stored as a rule.</summary>
    public static string ToDecision(string state) => state switch
    {
        Allow => Decisions.Allow,
        Ask => Decisions.Ask,
        Off => Decisions.Deny,
        _ => throw new ArgumentException($"'{state}' is not a state a row can be saved with (allow, ask or off)"),
    };

    public static string FromDecision(string decision) => decision switch
    {
        Decisions.Allow => Allow,
        Decisions.Ask => Ask,
        Decisions.Deny => Off,
        _ => throw new ArgumentException($"'{decision}' is not a decision"),
    };
}

public sealed record RowChoice(string Path, string State);

public sealed partial class DiscoverService
{
    /// <summary>
    /// What the app suggests for a row (shown as a hint, never written): a credential is never, a destructive command off, a read-only
    /// one that the app can vouch for (floor, preset, verb table, annotation) allowed, anything else ask first. A group has no state.
    /// </summary>
    public static string? Suggested(string cls, string source) => cls switch
    {
        CommandClass.Group => null,
        CommandClass.Credential => RowStates.Never,
        CommandClass.Destructive => RowStates.Off,
        CommandClass.ReadOnly when source is ClassSource.Floor or ClassSource.Preset or ClassSource.Verb or ClassSource.Annotation or ClassSource.Method => RowStates.Allow,
        _ => RowStates.Ask,
    };

    /// <summary>Which teams an integration is offered to: every team, or the listed ones.</summary>
    internal (bool All, IReadOnlyList<long> Teams) Offered(long integrationId)
    {
        using var c = _store.Open();
        var all = Convert.ToInt64(DiscoverStore.Scalar(c, null, "SELECT enabled_all FROM integrations WHERE id = $i;", ("$i", integrationId)), CultureInfo.InvariantCulture) == 1;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT team_id FROM integration_teams WHERE integration_id = $i ORDER BY team_id;";
        cmd.Parameters.AddWithValue("$i", integrationId);
        using var r = cmd.ExecuteReader();
        var teams = new List<long>();
        while (r.Read()) teams.Add(r.GetInt64(0));
        return (all, teams);
    }

    /// <summary>
    /// A team may be read or saved only when it is offered the integration, or already has choices or rules on it (so an un-offered
    /// team's overrides stay visible and removable). Throws a sentence the UI shows.
    /// </summary>
    public void CheckTeamOffered(string cliName, long teamId)
    {
        var name = CheckName(cliName);
        var id = IntegrationId(name) ?? throw new ArgumentException($"{name} is not set up yet: detect or enable it first");
        if (!_store.TeamExists(teamId)) throw new ArgumentException($"no team {teamId}");
        var (all, teams) = Offered(id);
        if (all || teams.Contains(teamId)) return;
        if (_store.Choices(id).Any(ch => ch.TeamId == teamId) || _store.Rules(id).Any(r => r.TeamId == teamId)) return;
        using var c = _store.Open();
        var team = Convert.ToString(DiscoverStore.Scalar(c, null, "SELECT name FROM teams WHERE id = $t;", ("$t", teamId)), CultureInfo.InvariantCulture) ?? $"team {teamId}";
        throw new ArgumentException($"{team} is not offered {name}: offer it first");
    }

    /// <summary>
    /// The one validation of a per-row save, for CLIs and MCP servers: every path must be a row, a group is refused, a credential or floor row
    /// is refused for any state, never is refused, "default" is for a team and "undecided" for the default list. Returns path -> state.
    /// </summary>
    private static Dictionary<string, string> PlanChoices(IReadOnlyList<RowChoice> choices, long? teamId, IReadOnlyDictionary<string, (bool HasChildren, string Class)> rows,
        Func<string, bool> floorHit)
    {
        var plan = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ch in choices)
        {
            var path = (ch.Path ?? "").Trim();
            var state = (ch.State ?? "").Trim().ToLowerInvariant();
            if (!rows.TryGetValue(path, out var node)) throw new ArgumentException($"'{path}' is not a command in the list");
            if (node.HasChildren) throw new ArgumentException($"'{path}' is a group: pick its commands");
            if (node.Class == CommandClass.Credential || floorHit(path))
                throw new ArgumentException($"'{path}' is locked: it reveals or changes a sign-in and is never allowed");
            switch (state)
            {
                case RowStates.Never: throw new ArgumentException($"'{path}': never is set by the app for sign-in commands and cannot be chosen");
                case RowStates.Default when teamId is null: throw new ArgumentException($"'{path}': reset to default applies to a team's override");
                case RowStates.Undecided when teamId is not null: throw new ArgumentException($"'{path}': not decided applies to the default list, not to a team");
                case RowStates.Allow or RowStates.Ask or RowStates.Off or RowStates.Default or RowStates.Undecided: break;
                default: throw new ArgumentException($"'{path}': '{ch.State}' is not a state (allowed, ask first, off)");
            }
            plan[path] = state;
        }
        return plan;
    }

    /// <summary>
    /// Saves per-row states for one scope (null team = the default list, "Every team"), in one transaction; rows not named are untouched.
    /// A group is refused, never is refused, and a credential or floor row is refused for any state. "default" (team only) removes the
    /// team's override; "undecided" (default list only) removes the default choice. A team state equal to what the team gets without
    /// it (its other rules, the default list, the preset) is not stored.
    /// </summary>
    public SaveResult SaveChoices(string cliName, long? teamId, IReadOnlyList<RowChoice> choices)
    {
        var name = CheckName(cliName);
        var id = IntegrationId(name) ?? throw new ArgumentException($"{name} is not set up yet");
        if (teamId is { } t && !_store.TeamExists(t)) throw new ArgumentException($"no team {t}");
        var entry = CliCatalog.Of(name) ?? CliCatalog.Generic(name);
        var nodes = _store.Commands(id).ToDictionary(n => n.Path, StringComparer.Ordinal);
        var plan = PlanChoices(choices, teamId, nodes.ToDictionary(n => n.Key, n => (n.Value.HasChildren, n.Value.Class)),
            path => CliRules.Decide(path.Split(' '), entry, [], []) is { Decision: Decisions.Deny, Rule.Layer: "floor" });
        var rules = _store.Rules(id);
        var globalRules = rules.Where(r => r.TeamId is null).Select(r => new CliRule(r.Pattern, r.Decision, "global", r.Id)).ToList();
        int allowed = 0, asked = 0, denied = 0, removed = 0;
        using var c = _store.Open();
        using var tx = c.BeginTransaction();
        var now = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        var existing = _store.Choices(id);
        foreach (var (path, state) in plan)
        {
            var mine = existing.FirstOrDefault(ch => ch.TeamId == teamId && ch.Path == path);
            var ownRule = rules.FirstOrDefault(r => r.TeamId == teamId && DiscoverStore.IsPatternOf(r.Pattern, path));
            // equal to what the team would get without this row's own rule (its other rules, then every team's, the preset): not stored
            var teamRules = teamId is null ? [] : rules.Where(r => r.TeamId == teamId && r.Id != mine?.RuleId && r.Id != ownRule.Id)
                .Select(r => new CliRule(r.Pattern, r.Decision, "team", r.Id)).ToList();
            var reset = state is RowStates.Default or RowStates.Undecided
                || (teamId is not null && RowStates.ToDecision(state) == CliRules.Decide(path.Split(' '), entry, teamRules, globalRules).Decision);
            if (reset)
            {
                if (mine is not null) { _store.RemoveChoice(c, tx, mine); removed++; }
                else if (ownRule.Pattern is not null)
                {
                    DiscoverStore.Run(c, tx, "DELETE FROM integration_rules WHERE id = $id;", ("$id", ownRule.Id));
                    removed++;
                }
                continue;
            }
            var decision = RowStates.ToDecision(state);
            _store.SetChoice(c, tx, id, teamId, path, decision, now);
            if (decision == Decisions.Allow) allowed++;
            else if (decision == Decisions.Ask) asked++;
            else denied++;
        }
        tx.Commit();
        return new SaveResult(allowed, asked, denied, removed);
    }
}
