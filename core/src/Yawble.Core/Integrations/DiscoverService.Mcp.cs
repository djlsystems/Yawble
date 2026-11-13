using System.Globalization;
using System.Text.Json.Nodes;

namespace Yawble.Core.Integrations;

/// <summary>The same checklist for the tools of an MCP server: classified from the tool annotations, saved as mcp_choices that own mcp_rules rows.</summary>
public sealed partial class DiscoverService
{
    private static string McpKey(string server) => "mcp:" + server.Trim();

    private (long Id, int Runs, bool OfferedAll, IReadOnlyList<long> Teams, string? Status) McpServerOf(string server) =>
        _store.McpServer(server.Trim()) ?? throw new ArgumentException($"no MCP server named '{server}'");

    /// <summary>Starts listing and merging a server's tools in the background (key "mcp:name"); false if one is already running for it.</summary>
    public bool StartMcp(string server, Func<CancellationToken, Task<JsonArray>> listTools)
    {
        var name = server.Trim();
        var id = McpServerOf(name).Id;
        return StartJob(McpKey(name), async (job, ct) =>
        {
            var tools = await listTools(ct);
            var classes = new Dictionary<string, Classification>(StringComparer.Ordinal);
            foreach (var t in tools.OfType<JsonObject>())
                if (McpClassifier.StringOf(t["name"]) is { Length: > 0 } n) classes[n] = McpClassifier.Classify(t);
            var merge = _store.MergeMcp(id, tools, classes);
            return new DiscoverOutcome(name, classes.Count, false, null, null, merge, 0);
        });
    }

    public DiscoverJob? McpStatus(string server) => JobStatus(McpKey(server), server.Trim());

    public bool McpCancel(string server) => CancelJob(McpKey(server));

    /// <summary>A team may be read or saved only when the server is offered to it, or it already has choices or rules on it.</summary>
    public void CheckMcpTeamOffered(string server, long teamId)
    {
        var s = McpServerOf(server);
        if (!_store.TeamExists(teamId)) throw new ArgumentException($"no team {teamId}");
        if (s.OfferedAll || s.Teams.Contains(teamId)) return;
        if (_store.McpChoices(s.Id).Any(ch => ch.TeamId == teamId) || _store.McpRules(s.Id).Any(r => r.TeamId == teamId)) return;
        using var c = _store.Open();
        var team = Convert.ToString(DiscoverStore.Scalar(c, null, "SELECT name FROM teams WHERE id = $t;", ("$t", teamId)), CultureInfo.InvariantCulture) ?? $"team {teamId}";
        throw new ArgumentException($"{team} is not offered {server.Trim()}: offer it first");
    }

    public Checklist GetMcpChecklist(string server, long? teamId)
    {
        var name = server.Trim();
        var s = McpServerOf(name);
        var nodes = _store.McpCommands(s.Id);
        var choices = _store.McpChoices(s.Id);
        var rules = _store.McpRules(s.Id);
        var first = s.Runs == 1;
        var teamRules = rules.Where(r => r.TeamId == teamId && teamId is not null).Select(r => new CliRule(r.Pattern, r.Decision, "team", r.Id)).ToList();
        var globalRules = rules.Where(r => r.TeamId is null).Select(r => new CliRule(r.Pattern, r.Decision, "global", r.Id)).ToList();

        string? Saved(string path, long? scope) =>
            choices.FirstOrDefault(x => x.TeamId == scope && x.Path == path)?.Decision
            ?? rules.FirstOrDefault(r => r.TeamId == scope && string.Equals(r.Pattern, path, StringComparison.OrdinalIgnoreCase)).Decision;

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
            var (eff, effRule) = CliRules.Decide([n.Path], null, teamRules, globalRules);
            var effective = effRule is { Layer: "floor" } ? RowStates.Never : eff;
            if (!n.Gone)
            {
                if (effective == Decisions.Allow) cAllow++;
                else if (effective == Decisions.Ask && defaultDecision is null && teamDecision is null) cUndecided++;
                else if (effective == Decisions.Ask) cAsk++;
                else cOff++;
            }
            string? from = decision is null ? (preTick ? "default" : null) : teamDecision is not null ? "choice" : teamId is not null ? "all teams" : "choice";
            view.Add(new ChecklistNode(n.Path, "", n.Path, 1, n.Summary, false, n.Class, n.Source, n.Reason, locked ? "locked" : "free", ticked, ticked ? from : null, preTick,
                decision, n.Gone, n.Badge, null, suggested, defaultDecision, teamDecision, effective, n.Origin));
        }
        var known = nodes.Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var other = rules.Where(r => !known.Contains(r.Pattern) && (r.TeamId == teamId || r.TeamId is null))
            .Select(r => new OtherRule(r.Id, r.TeamId, r.Pattern, r.Decision)).ToList();
        var error = s.Status is { } st && st.StartsWith("not working", StringComparison.Ordinal) ? st : null;
        return new Checklist(name, teamId, null, s.Runs > 0 ? nodes.Where(n => !n.Gone).Select(n => n.LastSeenAt).DefaultIfEmpty(null).Max() : null, s.Runs, false, null, error, view, other,
            nodes.Count(n => n.Badge == "new" && !first), nodes.Count(n => n.Badge == "changed"), nodes.Count(n => n.Gone), choices.Count(c => c.TeamId == teamId),
            "mcp", s.OfferedAll, s.Teams, (cAllow, cAsk, cOff, cUndecided));
    }

    /// <summary>The same rules as <see cref="SaveChoices"/> (one shared validation), written as mcp_choices and the mcp_rules rows they own.</summary>
    public SaveResult SaveMcpChoices(string server, long? teamId, IReadOnlyList<RowChoice> choices)
    {
        var s = McpServerOf(server);
        if (teamId is { } t && !_store.TeamExists(t)) throw new ArgumentException($"no team {t}");
        var nodes = _store.McpCommands(s.Id).ToDictionary(n => n.Path, n => (n.HasChildren, n.Class), StringComparer.Ordinal);
        var plan = PlanChoices(choices, teamId, nodes, path => CliRules.Decide([path], null, [], []) is { Decision: Decisions.Deny, Rule.Layer: "floor" });

        var rules = _store.McpRules(s.Id);
        var globalRules = rules.Where(r => r.TeamId is null).Select(r => new CliRule(r.Pattern, r.Decision, "global", r.Id)).ToList();
        int allowed = 0, asked = 0, denied = 0, removed = 0;
        using var c = _store.Open();
        using var tx = c.BeginTransaction();
        var now = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        var existing = _store.McpChoices(s.Id);
        foreach (var (path, state) in plan)
        {
            var mine = existing.FirstOrDefault(ch => ch.TeamId == teamId && ch.Path == path);
            var ownRule = rules.FirstOrDefault(r => r.TeamId == teamId && string.Equals(r.Pattern, path, StringComparison.OrdinalIgnoreCase));
            var teamRules = teamId is null ? [] : rules.Where(r => r.TeamId == teamId && r.Id != mine?.RuleId && r.Id != ownRule.Id)
                .Select(r => new CliRule(r.Pattern, r.Decision, "team", r.Id)).ToList();
            var reset = state is RowStates.Default or RowStates.Undecided
                || (teamId is not null && RowStates.ToDecision(state) == CliRules.Decide([path], null, teamRules, globalRules).Decision);
            if (reset)
            {
                if (mine is not null) { _store.RemoveMcpChoice(c, tx, mine); removed++; }
                else if (ownRule.Pattern is not null)
                {
                    DiscoverStore.Run(c, tx, "DELETE FROM mcp_rules WHERE id = $id;", ("$id", ownRule.Id));
                    removed++;
                }
                continue;
            }
            var decision = RowStates.ToDecision(state);
            _store.SetMcpChoice(c, tx, s.Id, teamId, path, decision, now);
            if (decision == Decisions.Allow) allowed++;
            else if (decision == Decisions.Ask) asked++;
            else denied++;
        }
        tx.Commit();
        return new SaveResult(allowed, asked, denied, removed);
    }
}
