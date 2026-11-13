using System.Globalization;

namespace Yawble.Core.Integrations;

/// <summary>
/// The same checklist for the operations of an API read from its OpenAPI spec: classified from the HTTP method, saved as api_choices that own
/// api_rules rows. The rule pattern is the exact action id and is matched whole (<see cref="CliRules.DecideExact"/>), never as words.
/// </summary>
public sealed partial class DiscoverService
{
    private static string ApiKey(string api) => "api:" + api.Trim();

    private (long Id, int Runs, bool OfferedAll, IReadOnlyList<long> Teams, string? Status) ApiOf(string api) =>
        _store.Api(api.Trim()) ?? throw new ArgumentException($"no API named '{api}'");

    /// <summary>Starts reading and merging an API's spec in the background (key "api:name"); false if one is already running for it.</summary>
    public bool StartApi(string api, Func<CancellationToken, Task<ApiSpec>> read)
    {
        var name = api.Trim();
        var id = ApiOf(name).Id;
        return StartJob(ApiKey(name), async (job, ct) =>
        {
            var spec = await read(ct);
            var merge = _store.MergeApi(id, spec);
            return new DiscoverOutcome(name, spec.Actions.Count, false, null, null, merge, 0);
        });
    }

    public DiscoverJob? ApiStatus(string api) => JobStatus(ApiKey(api), api.Trim());

    public bool ApiCancel(string api) => CancelJob(ApiKey(api));

    /// <summary>A team may be read or saved only when the API is offered to it, or it already has choices or rules on it.</summary>
    public void CheckApiTeamOffered(string api, long teamId)
    {
        var s = ApiOf(api);
        if (!_store.TeamExists(teamId)) throw new ArgumentException($"no team {teamId}");
        if (s.OfferedAll || s.Teams.Contains(teamId)) return;
        if (_store.ApiChoices(s.Id).Any(ch => ch.TeamId == teamId) || _store.ApiRules(s.Id).Any(r => r.TeamId == teamId)) return;
        using var c = _store.Open();
        var team = Convert.ToString(DiscoverStore.Scalar(c, null, "SELECT name FROM teams WHERE id = $t;", ("$t", teamId)), CultureInfo.InvariantCulture) ?? $"team {teamId}";
        throw new ArgumentException($"{team} is not offered {api.Trim()}: offer it first");
    }

    public Checklist GetApiChecklist(string api, long? teamId)
    {
        var name = api.Trim();
        var s = ApiOf(name);
        var nodes = _store.ApiCommands(s.Id);
        var choices = _store.ApiChoices(s.Id);
        var rules = _store.ApiRules(s.Id);
        var first = s.Runs == 1;
        var teamRules = rules.Where(r => r.TeamId == teamId && teamId is not null).Select(r => new CliRule(r.Pattern, r.Decision, "team", r.Id)).ToList();
        var globalRules = rules.Where(r => r.TeamId is null).Select(r => new CliRule(r.Pattern, r.Decision, "global", r.Id)).ToList();

        string? Saved(string path, long? scope) =>
            choices.FirstOrDefault(x => x.TeamId == scope && x.Path == path)?.Decision
            ?? rules.FirstOrDefault(r => r.TeamId == scope && string.Equals(r.Pattern, path, StringComparison.Ordinal)).Decision;

        int cAllow = 0, cAsk = 0, cOff = 0, cUndecided = 0;
        var view = new List<ChecklistNode>();
        foreach (var n in nodes)
        {
            var locked = n.Class == CommandClass.Credential;
            var defaultDecision = Saved(n.Path, null);
            var teamDecision = teamId is null ? null : Saved(n.Path, teamId);
            var decision = teamDecision ?? defaultDecision;
            var suggested = Suggested(n.Class, n.Source);
            var preTick = !locked && suggested == RowStates.Allow && decision is null && n.Badge != "new" && !n.Gone;
            var ticked = !locked && (decision is not null ? decision == Decisions.Allow : preTick);
            var eff = CliRules.DecideExact(n.Path, teamRules, globalRules).Decision;
            var effective = locked ? RowStates.Never : eff;
            if (!n.Gone)
            {
                if (effective == Decisions.Allow) cAllow++;
                else if (effective == Decisions.Ask && defaultDecision is null && teamDecision is null) cUndecided++;
                else if (effective == Decisions.Ask) cAsk++;
                else cOff++;
            }
            string? from = decision is null ? (preTick ? "default" : null) : teamDecision is not null ? "choice" : teamId is not null ? "all teams" : "choice";
            view.Add(new ChecklistNode(n.Path, "", n.Path, 1, n.Summary, false, n.Class, n.Source, n.Reason, locked ? "locked" : "free", ticked, ticked ? from : null, preTick,
                decision, n.Gone, n.Badge, null, suggested, defaultDecision, teamDecision, effective, n.Origin, n.Tag, n.Method, n.Route));
        }
        var known = nodes.Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
        var other = rules.Where(r => !known.Contains(r.Pattern) && (r.TeamId == teamId || r.TeamId is null))
            .Select(r => new OtherRule(r.Id, r.TeamId, r.Pattern, r.Decision)).ToList();
        var error = s.Status is { } st && st.StartsWith("not working", StringComparison.Ordinal) ? st : null;
        return new Checklist(name, teamId, null, s.Runs > 0 ? nodes.Where(n => !n.Gone).Select(n => n.LastSeenAt).DefaultIfEmpty(null).Max() : null, s.Runs, false, null, error, view, other,
            nodes.Count(n => n.Badge == "new" && !first), nodes.Count(n => n.Badge == "changed"), nodes.Count(n => n.Gone), choices.Count(c => c.TeamId == teamId),
            "api", s.OfferedAll, s.Teams, (cAllow, cAsk, cOff, cUndecided));
    }

    /// <summary>
    /// Saves states by action id for one scope (null team = every team). A state of allow, ask or off is stored; a null state resets a
    /// team's row to the default (and removes the default choice for the every-team list). Refusals as <see cref="SaveApiChoices"/>.
    /// </summary>
    public void SaveApiChecklist(string api, long? teamId, IReadOnlyDictionary<string, string?> states) =>
        SaveApiChoices(api, teamId, [.. states.Select(kv => new RowChoice(kv.Key, kv.Value ?? (teamId is null ? RowStates.Undecided : RowStates.Default)))]);

    /// <summary>The same rules as <see cref="SaveChoices"/> (one shared validation), written as api_choices and the api_rules rows they own.</summary>
    public SaveResult SaveApiChoices(string api, long? teamId, IReadOnlyList<RowChoice> choices)
    {
        var s = ApiOf(api);
        if (teamId is { } t && !_store.TeamExists(t)) throw new ArgumentException($"no team {t}");
        var nodes = _store.ApiCommands(s.Id).ToDictionary(n => n.Path, n => (n.HasChildren, n.Class), StringComparer.Ordinal);
        var plan = PlanChoices(choices, teamId, nodes, _ => false); // a credential row is locked by its class

        var rules = _store.ApiRules(s.Id);
        var globalRules = rules.Where(r => r.TeamId is null).Select(r => new CliRule(r.Pattern, r.Decision, "global", r.Id)).ToList();
        int allowed = 0, asked = 0, denied = 0, removed = 0;
        using var c = _store.Open();
        using var tx = c.BeginTransaction();
        var now = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        var existing = _store.ApiChoices(s.Id);
        foreach (var (path, state) in plan)
        {
            var mine = existing.FirstOrDefault(ch => ch.TeamId == teamId && ch.Path == path);
            var ownRule = rules.FirstOrDefault(r => r.TeamId == teamId && string.Equals(r.Pattern, path, StringComparison.Ordinal));
            var teamRules = teamId is null ? [] : rules.Where(r => r.TeamId == teamId && r.Id != mine?.RuleId && r.Id != ownRule.Id)
                .Select(r => new CliRule(r.Pattern, r.Decision, "team", r.Id)).ToList();
            var reset = state is RowStates.Default or RowStates.Undecided
                || (teamId is not null && RowStates.ToDecision(state) == CliRules.DecideExact(path, teamRules, globalRules).Decision);
            if (reset)
            {
                if (mine is not null) { _store.RemoveApiChoice(c, tx, mine); removed++; }
                else if (ownRule.Pattern is not null)
                {
                    DiscoverStore.Run(c, tx, "DELETE FROM api_rules WHERE id = $id;", ("$id", ownRule.Id));
                    removed++;
                }
                continue;
            }
            var decision = RowStates.ToDecision(state);
            _store.SetApiChoice(c, tx, s.Id, teamId, path, decision, now);
            if (decision == Decisions.Allow) allowed++;
            else if (decision == Decisions.Ask) asked++;
            else denied++;
        }
        tx.Commit();
        return new SaveResult(allowed, asked, denied, removed);
    }

    /// <summary>
    /// The API twin of <see cref="RememberMcp"/>: remembers an approval as a choice. The action must be one of the API's operations
    /// (a credential row is refused for allow and ask; a deny on it needs no rule). Returns the action id.
    /// </summary>
    public string? RememberApi(string api, long? scope, string actionId, string decision)
    {
        if (!Decisions.All.Contains(decision)) throw new ArgumentException("decision is allow, ask or deny");
        var s = ApiOf(api);
        if (scope is { } t && !_store.TeamExists(t)) throw new ArgumentException($"no team {t}");
        var node = _store.ApiCommands(s.Id).FirstOrDefault(n => n.Path == actionId)
            ?? throw new ArgumentException($"'{actionId}' is not an operation of {api.Trim()}");
        if (node.Class == CommandClass.Credential)
        {
            if (decision == Decisions.Deny) return null;
            throw new ArgumentException($"'{actionId}' is locked: it reveals or changes a sign-in and is never allowed");
        }
        var now = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        using var c = _store.Open();
        using var tx = c.BeginTransaction();
        _store.SetApiChoice(c, tx, s.Id, scope, actionId, decision, now, "remembered from an approval");
        tx.Commit();
        return actionId;
    }
}
