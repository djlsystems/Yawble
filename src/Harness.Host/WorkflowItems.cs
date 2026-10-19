using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE ITEM'S CURRENT TITLE ON A WORKFLOW'S PROJECTION - the one place the Host overlays it, used by
/// <c>GET /api/teams/{team}/workflow</c>, <c>/workflows</c> and <c>/api/teams/rollup</c>.
///
/// <para>
/// The message store's projection carries <see cref="TeamWorkflowTiming.Item"/> with the title its
/// <c>backlog.item.dispatched</c> root row was written with, because it cannot read the backlog. Here
/// that title is replaced by the backlog's current one, so a rename shows on the next read. An item
/// since deleted keeps the row's title; a workflow with no item is left alone (a told workflow keeps
/// its subject and <c>item</c> null).
/// </para>
///
/// <para>
/// ONE BACKLOG READ PER REQUEST, whatever the number of workflows or teams
/// (<see cref="IBacklogStore.TitlesAsync"/>): gather every timing of the response first, then
/// <see cref="Apply"/> each.
/// </para>
/// </summary>
public static class WorkflowItems
{
    /// <summary>The current titles of every item <paramref name="timings"/> carry, in one read; no
    /// read at all when none carries one.</summary>
    public static async Task<IReadOnlyDictionary<long, string>> CurrentTitlesAsync(
        IEnumerable<TeamWorkflowTiming> timings, IBacklogStore backlog, CancellationToken ct = default)
    {
        var ids = timings.Where(t => t.Item is not null).Select(t => t.Item!.Id).Distinct().ToList();

        return ids.Count == 0 ? new Dictionary<long, string>() : await backlog.TitlesAsync(ids, ct);
    }

    /// <summary><paramref name="timing"/> with its item's current title, when the backlog still has it.</summary>
    public static TeamWorkflowTiming Apply(TeamWorkflowTiming timing, IReadOnlyDictionary<long, string> titles) =>
        timing.Item is { } item && titles.TryGetValue(item.Id, out var title) && title != item.Title
            ? timing with { Item = item with { Title = title } }
            : timing;

    /// <summary><paramref name="workflows"/> with every row's item carrying its current title.</summary>
    public static TeamWorkflows Apply(TeamWorkflows workflows, IReadOnlyDictionary<long, string> titles) =>
        workflows with { Workflows = workflows.Workflows.Select(w => Apply(w, titles)).ToList() };

    /// <summary>One timing, overlaid in one read.</summary>
    public static async Task<TeamWorkflowTiming> OverlayAsync(
        TeamWorkflowTiming timing, IBacklogStore backlog, CancellationToken ct = default) =>
        Apply(timing, await CurrentTitlesAsync([timing], backlog, ct));

    /// <summary>One team's workflows, overlaid in one read.</summary>
    public static async Task<TeamWorkflows> OverlayAsync(
        TeamWorkflows workflows, IBacklogStore backlog, CancellationToken ct = default) =>
        Apply(workflows, await CurrentTitlesAsync(workflows.Workflows, backlog, ct));

    /// <summary>Every rollup row's two projections, overlaid in one read across all the teams.</summary>
    public static async Task<IReadOnlyList<TeamRollupRow>> OverlayAsync(
        IReadOnlyList<TeamRollupRow> rows, IBacklogStore backlog, CancellationToken ct = default)
    {
        var titles = await CurrentTitlesAsync(
            rows.SelectMany(row => row.OpenWorkflows.Workflows.Prepend(row.Workflow)), backlog, ct);

        return rows
            .Select(row => row with { Workflow = Apply(row.Workflow, titles), OpenWorkflows = Apply(row.OpenWorkflows, titles) })
            .ToList();
    }
}
