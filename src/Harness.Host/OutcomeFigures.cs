using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHAT AN OUTCOME'S WORK COST, READ FROM THE LEDGER, NEVER ESTIMATED.
///
/// <para>
/// A workflow counts toward the outcome its NEWEST link names, followed through <c>merged_into</c>,
/// so a merge moves its figures without rewriting a link. Agent time and waiting are sums over runs -
/// parallel runs add up, because each was paid for. Elapsed is a workflow's own
/// <c>closed_at - root_at</c> and is NEVER summed: the median and the longest are given. A run that
/// reported no usage is counted in <c>unmeasuredRuns</c> and adds nothing to the tokens, never a zero.
/// A team that is gone is named from the snapshot a link or a ledger row kept.
/// </para>
/// </summary>
public static class OutcomeFigures
{
    /// <summary>The name of the entry that carries every unlinked workflow.</summary>
    public const string NoOutcomeName = "No outcome";

    /// <summary>
    /// Every outcome id followed through <c>merged_into</c> to the one holding its figures now.
    /// Bounded, so a hand-made cycle ends.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolution(IReadOnlyList<Outcome> outcomes)
    {
        var byId = outcomes.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var outcome in outcomes)
        {
            var current = outcome;
            for (var hop = 0; hop < 64 && current.Status == OutcomeStatus.Merged
                 && current.MergedInto is { } next && byId.TryGetValue(next, out var target); hop++)
            {
                current = target;
            }

            resolved[outcome.Id] = current.Id;
        }

        return resolved;
    }

    /// <summary>Each workflow's outcome now: its newest link's outcome, resolved. A workflow whose
    /// newest link is an unlink is absent: it counts under No outcome.</summary>
    public static IReadOnlyDictionary<long, string> CurrentOutcomes(
        IReadOnlyList<OutcomeLink> currentLinks, IReadOnlyDictionary<string, string> resolution) =>
        currentLinks.Where(l => l.OutcomeId is not null).ToDictionary(
            l => l.Correlation,
            l => resolution.GetValueOrDefault(l.OutcomeId!, l.OutcomeId!));

    /// <summary>One outcome's (or the unlinked workflows') figures.</summary>
    public sealed record Figures(
        WorkflowCounts Workflows,
        double AgentSeconds,
        double WaitingSeconds,
        Elapsed Elapsed,
        Tokens Tokens,
        int Runs,
        IReadOnlyList<TeamName> Teams,
        DateTimeOffset? LastWorkedAt);

    public sealed record WorkflowCounts(int Open, int Completed, int Closed, int Total);

    /// <summary>Per workflow, never summed: the median and the longest of the closed ones.</summary>
    public sealed record Elapsed(double? MedianSeconds, double? LongestSeconds, int Workflows);

    /// <summary>Measured billable tokens, and how many runs reported none (counted, never zero).</summary>
    public sealed record Tokens(long Billable, int MeasuredRuns, int UnmeasuredRuns);

    public sealed record TeamName(string Id, string Name, bool Deleted);

    /// <summary>One workflow's row on an outcome's detail. Its tokens are the route's as the list's
    /// are: <see cref="BillableTokens"/> over <see cref="MeasuredRuns"/>, and the runs that reported
    /// none counted beside them, so a client never decides "not measured" from a zero.</summary>
    public sealed record WorkflowLine(
        long Correlation,
        TeamName? Team,
        string State,
        DateTimeOffset? StartedAt,
        double? ElapsedSeconds,
        double AgentSeconds,
        long BillableTokens,
        int MeasuredRuns,
        int UnmeasuredRuns,
        string? How,
        string? SetBy,
        string? SetByKind,
        DateTimeOffset? SetAt);

    /// <summary>What the figures are computed over: the workflows in the window, grouped by outcome
    /// (null is "No outcome").</summary>
    public sealed class Book
    {
        public required OutcomeLedgerRows Rows { get; init; }
        public required IReadOnlyDictionary<long, string> OutcomeOf { get; init; }
        public required IReadOnlyDictionary<long, OutcomeLink> LinkOf { get; init; }
        public required IReadOnlySet<long> InWindow { get; init; }
        public required ILookup<long, UsageLedgerRow> RunsOf { get; init; }
        public required IReadOnlyDictionary<long, WorkflowLedgerRow> CloseOf { get; init; }

        public IEnumerable<long> WorkflowsOf(string? outcome) =>
            InWindow.Where(c => outcome is null ? !OutcomeOf.ContainsKey(c) : OutcomeOf.GetValueOrDefault(c) == outcome);
    }

    public static Book Open(
        OutcomeLedgerRows rows, IReadOnlyList<Outcome> outcomes, DateTimeOffset? from, DateTimeOffset? to)
    {
        var resolution = Resolution(outcomes);
        var outcomeOf = CurrentOutcomes(rows.CurrentLinks, resolution);
        var linkOf = rows.CurrentLinks.ToDictionary(l => l.Correlation);
        var closeOf = rows.Closes.ToDictionary(c => c.Correlation);
        var windowed = from is not null || to is not null;

        bool Within(DateTimeOffset at) => (from is null || at >= from) && (to is null || at < to);

        // IN THE WINDOW: a workflow with a run that ended in it, a close in it, or its link made in
        // it. With no window, every workflow the ledger or a link knows.
        var inWindow = new HashSet<long>(rows.Runs.Select(r => r.Correlation));
        inWindow.UnionWith(rows.Closes.Where(c => !windowed || Within(c.ClosedAt)).Select(c => c.Correlation));
        inWindow.UnionWith(rows.CurrentLinks.Where(l => !windowed || Within(l.SetAt)).Select(l => l.Correlation));

        return new Book
        {
            Rows = rows,
            OutcomeOf = outcomeOf,
            LinkOf = linkOf,
            InWindow = inWindow,
            RunsOf = rows.Runs.ToLookup(r => r.Correlation),
            CloseOf = closeOf,
        };
    }

    public static Figures For(Book book, string? outcome, IReadOnlySet<long> open)
    {
        var workflows = book.WorkflowsOf(outcome).ToList();
        var runs = workflows.SelectMany(c => book.RunsOf[c]).ToList();

        var completed = workflows.Count(c => !open.Contains(c)
            && book.CloseOf.TryGetValue(c, out var close) && close.HowClosed == WorkflowLedgerRow.Completed);
        var closed = workflows.Count(c => !open.Contains(c)
            && book.CloseOf.TryGetValue(c, out var close) && close.HowClosed == WorkflowLedgerRow.Closed);

        var elapsed = workflows
            .Where(c => !open.Contains(c))
            .Select(c => book.CloseOf.GetValueOrDefault(c)?.ElapsedSeconds)
            .OfType<double>()
            .Order()
            .ToList();

        var last = runs.Select(r => (DateTimeOffset?)r.EndedAt)
            .Concat(workflows.Select(c => book.LinkOf.GetValueOrDefault(c)?.SetAt))
            .Max();

        return new Figures(
            new WorkflowCounts(workflows.Count(open.Contains), completed, closed, workflows.Count),
            runs.Sum(AgentSeconds),
            runs.Sum(r => r.StartedAt is { } started && r.QueuedAt is { } queued ? Math.Max(0, (started - queued).TotalSeconds) : 0),
            new Elapsed(Median(elapsed), elapsed.Count == 0 ? null : elapsed[^1], elapsed.Count),
            new Tokens(
                runs.Where(r => r.Measured).Sum(r => r.Billable ?? 0),
                runs.Count(r => r.Measured),
                runs.Count(r => !r.Measured)),
            runs.Count,
            TeamsOf(book, workflows, runs),
            last);
    }

    public static WorkflowLine Line(Book book, long correlation, bool isOpen, DateTimeOffset? startedAt)
    {
        var runs = book.RunsOf[correlation].ToList();
        var link = book.LinkOf.GetValueOrDefault(correlation);
        var close = book.CloseOf.GetValueOrDefault(correlation);

        return new WorkflowLine(
            correlation,
            TeamsOf(book, [correlation], runs).FirstOrDefault(),
            isOpen ? "open" : close?.HowClosed ?? "open",
            close?.RootAt ?? startedAt,
            isOpen ? null : close?.ElapsedSeconds,
            runs.Sum(AgentSeconds),
            runs.Where(r => r.Measured).Sum(r => r.Billable ?? 0),
            runs.Count(r => r.Measured),
            runs.Count(r => !r.Measured),
            link?.How,
            link?.SetBy,
            link?.SetByKind,
            link?.SetAt);
    }

    private static double AgentSeconds(UsageLedgerRow run) =>
        run.StartedAt is { } started ? Math.Max(0, (run.EndedAt - started).TotalSeconds) : 0;

    private static double? Median(IReadOnlyList<double> sorted) =>
        sorted.Count == 0
            ? null
            : sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

    /// <summary>Each team the workflows were worked on, named as it is now, or - gone - from the
    /// snapshot a link or a ledger row kept.</summary>
    private static IReadOnlyList<TeamName> TeamsOf(Book book, IReadOnlyList<long> workflows, IReadOnlyList<UsageLedgerRow> runs)
    {
        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in workflows)
        {
            if (book.LinkOf.GetValueOrDefault(c) is { TeamId: { } id } link) snapshots.TryAdd(id, link.TeamNameAtLink ?? id);
            if (book.CloseOf.GetValueOrDefault(c) is { TeamId: { } closeTeam } close) snapshots.TryAdd(closeTeam, close.TeamName ?? closeTeam);
        }

        foreach (var run in runs.Where(r => r.TeamId is not null)) snapshots.TryAdd(run.TeamId!, run.TeamName ?? run.TeamId!);

        return snapshots
            .Select(e => book.Rows.LiveTeamNames.TryGetValue(e.Key, out var live)
                ? new TeamName(e.Key, live, false)
                : new TeamName(e.Key, e.Value, true))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
