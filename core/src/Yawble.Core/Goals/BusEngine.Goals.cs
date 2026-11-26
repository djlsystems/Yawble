using Yawble.Core.Goals;

namespace Yawble.Core.Bus;

/// <summary>One goal on the Goals page: its deliverables (newest activity first), how many are open, and their time together.</summary>
public sealed record GoalEntry(Goal Goal, IReadOnlyList<long> Deliverables, int Open, GoalTime Time)
{
    public int Total => Deliverables.Count;
}

/// <summary>The deliverables with no goal, as a <see cref="GoalEntry"/> counts them.</summary>
public sealed record GoalTotals(IReadOnlyList<long> Deliverables, int Open, GoalTime Time)
{
    public int Total => Deliverables.Count;
}

/// <summary>goals.list's answer: every goal by name, the Unassigned deliverables, and the core's now.</summary>
public sealed record GoalsOverview(IReadOnlyList<GoalEntry> Goals, GoalTotals Unassigned, DateTimeOffset Now);

/// <summary>
/// Goals: the person's names for what deliverables are for. A deliverable has at most one goal, or none (Unassigned); the
/// person, the Concierge (when the person asks) and paired companion apps set it. A goal's time is the calendar time its
/// deliverables cover together. Every read here is a fixed number of statements however many deliverables there are.
/// </summary>
public sealed partial class BusEngine
{
    /// <summary>Every goal with its deliverables, and the Unassigned ones. Only deliverables of a team count (as on the Deliverables page).</summary>
    public Task<GoalsOverview> GoalsOverviewAsync(CancellationToken ct = default) => WithGate(() =>
    {
        var now = _store.Clock.GetUtcNow();
        var spans = DeliverableSpans();
        var assigned = _store.DeliverableGoals();
        var byGoal = spans.Where(s => assigned.ContainsKey(s.Id)).GroupBy(s => assigned[s.Id]).ToDictionary(g => g.Key, g => g.ToList());
        var entries = _store.Goals().Select(g =>
        {
            var mine = byGoal.GetValueOrDefault(g.Id) ?? [];
            return new GoalEntry(g, [.. mine.Select(s => s.Id)], mine.Count(s => s.Open), GoalTime.Of(mine, now));
        }).ToList();
        var none = spans.Where(s => !assigned.ContainsKey(s.Id)).ToList();
        return new GoalsOverview(entries, new GoalTotals([.. none.Select(s => s.Id)], none.Count(s => s.Open), GoalTime.Of(none, now)), now);
    }, ct);

    /// <summary>Which goal each deliverable has (deliverable id to goal), and every goal by id: what a Deliverables page row shows.</summary>
    public Task<(IReadOnlyDictionary<long, long> Assigned, IReadOnlyDictionary<long, Goal> Goals)> DeliverableGoalsAsync(CancellationToken ct = default) =>
        WithGate(() => (_store.DeliverableGoals(), (IReadOnlyDictionary<long, Goal>)_store.Goals().ToDictionary(g => g.Id)), ct);

    /// <summary>A new goal (<paramref name="id"/> null) or a goal's new name and description. Names are unique ignoring case.</summary>
    public Task<GoalEntry> SaveGoalAsync(long? id, string? name, string? description, CancellationToken ct = default) => WithGate(() =>
    {
        var n = GoalRules.Name(name);
        var existing = id is { } i ? _store.GoalById(i) ?? throw new BusRefusal("no_such_goal", $"there is no goal {i}") : null;
        var d = description is null && existing is not null ? existing.Description : GoalRules.Description(description);
        // One rule for "the same name" everywhere (GoalRules.SameName; the shell folds the same way): the table's NOCASE
        // only folds ASCII letters, so the check is here, under the gate.
        if (_store.Goals().Any(g => g.Id != id && GoalRules.SameName(g.Name, n))) throw new BusRefusal("bad_params", $"there is a goal named '{n}' already");
        var saved = existing is null ? _store.AddGoal(n, d) : _store.UpdateGoal(existing.Id, n, d) ? existing.Id : throw new BusRefusal("no_such_goal", $"there is no goal {existing.Id}");
        return EntryOf(saved);
    }, ct);

    /// <summary>
    /// Deletes a goal; how many of its deliverables are Unassigned now. Counted as goals.list counts them (deliverables of a
    /// team), so the delete's plan and its result say the same number; the rows of deliverables no team has go with it.
    /// </summary>
    public Task<int> DeleteGoalAsync(long id, CancellationToken ct = default) => WithGate(() =>
    {
        var assigned = _store.DeliverableGoals();
        var live = DeliverableSpans().Count(s => assigned.TryGetValue(s.Id, out var g) && g == id);
        return _store.DeleteGoal(id) ? live : throw new BusRefusal("no_such_goal", $"there is no goal {id}");
    }, ct);

    /// <summary>Gives a deliverable its goal, or Unassigned (<paramref name="goal"/> null); the goal it has now.</summary>
    public Task<Goal?> AssignGoalAsync(long deliverable, long? goal, CancellationToken ct = default) => WithGate(() =>
    {
        if (DeliverableSpans(only: deliverable).Count == 0) throw new BusRefusal("no_such_deliverable", $"there is no deliverable {deliverable}");
        var g = goal is { } gid ? _store.GoalById(gid) ?? throw new BusRefusal("no_such_goal", $"there is no goal {gid}") : null;
        _store.SetDeliverableGoal(deliverable, g?.Id);
        return g;
    }, ct);

    /// <summary>One goal as goals.list shows it. Under the gate.</summary>
    private GoalEntry EntryOf(long goal)
    {
        var g = _store.GoalById(goal) ?? throw new BusRefusal("no_such_goal", $"there is no goal {goal}");
        var assigned = _store.DeliverableGoals();
        var mine = DeliverableSpans().Where(s => assigned.TryGetValue(s.Id, out var x) && x == goal).ToList();
        return new GoalEntry(g, [.. mine.Select(s => s.Id)], mine.Count(s => s.Open), GoalTime.Of(mine, _store.Clock.GetUtcNow()));
    }

    /// <summary>
    /// Every team's deliverable (or just <paramref name="only"/>) with its overall time's span, newest activity first: from its
    /// first message to its end (an expired one ends at its last activity), open ones to now. Under the gate.
    /// </summary>
    private IReadOnlyList<DeliverableSpan> DeliverableSpans(long? only = null)
    {
        var teams = _store.Teams();
        var members = _store.AllMembers();
        var byQualified = members.GroupBy(m => m.Qualified, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var heads = HeadsOfTeams(_store.DeliverableHeads(), teams.ToDictionary(t => t.Id), teams.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase),
            byQualified, teamId: null, only);
        return [.. heads.Select(h => new DeliverableSpan(h.Head.Id, h.Head.First, h.Head.Ended ? h.Head.SpanEnd ?? h.Head.EndAt ?? h.Head.Last : null))];
    }
}
