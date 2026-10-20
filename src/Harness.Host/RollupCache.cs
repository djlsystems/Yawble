using System.Collections.Concurrent;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE ROLLUP'S TWO TIMING READS PER TEAM, KEPT UNTIL THAT TEAM'S ROWS MOVE. The Teams table re-reads
/// the rollup on every container push, and each team's part cost a dozen statements per workflow
/// listed; most pushes are one team's progress, so every other team's answer was computed again
/// for nothing. A team's answer is a function of its rows above its floor (the timings count rows,
/// never the clock - nothing is estimated), so it is kept against the team's highest seq (one
/// indexed scalar read) and its floor, and only <c>ServerNow</c> is refreshed on a hit. A purge is
/// told through <see cref="Invalidate"/>, since a row that went does not move the highest seq.
/// </summary>
public sealed class RollupCache(IMessageLog log)
{
    private readonly ConcurrentDictionary<string, Entry> _teams = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(long Floor, long Highest, TeamWorkflowTiming Elapsed, TeamWorkflows Open);

    public async Task<(TeamWorkflowTiming Elapsed, TeamWorkflows Open)> ReadAsync(string team, long floor, CancellationToken ct = default)
    {
        var highest = await log.TeamHighestSeqAsync(team, ct);
        if (_teams.TryGetValue(team, out var held) && held.Floor == floor && held.Highest == highest)
        {
            var now = DateTimeOffset.UtcNow;
            return (
                held.Elapsed with { ServerNow = now },
                held.Open with { ServerNow = now, Workflows = held.Open.Workflows.Select(w => w with { ServerNow = now }).ToList() });
        }

        var elapsed = await log.ElapsedForTeamAsync(team, floor, ct);
        var open = await log.WorkflowsForTeamAsync(team, floor, ct);
        _teams[team] = new Entry(floor, highest, elapsed, open);
        return (elapsed, open);
    }

    /// <summary>Forgets every team's answer: after a purge, which removes rows without moving any team's highest seq.</summary>
    public void Invalidate() => _teams.Clear();
}
