using System.Collections.Concurrent;
using Harness.Contracts;

namespace Harness.Host.Solutions;

/// <summary>
/// <c>team_solutions</c>, held in memory as well: every write goes through to the store and then
/// the copy, so <see cref="For"/> - read on every team summary - answers with no database round trip.
/// Loaded once at start. The one registration of both <see cref="ITeamSolutionStore"/> and
/// <see cref="ITeamSolutions"/> in the Host.
/// </summary>
public sealed class TeamSolutionIndex(ITeamSolutionStore store) : ITeamSolutionStore, ITeamSolutions
{
    private readonly ConcurrentDictionary<string, TeamSolutionRow> _rows = new(StringComparer.OrdinalIgnoreCase);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _rows.Clear();
        foreach (var row in await store.ListAsync(ct)) _rows[row.Team] = row;
    }

    /// <summary>Which package a team came from, for its summary; null for a team made by hand.</summary>
    public TeamSolution? For(string team) =>
        _rows.TryGetValue(team, out var row)
            ? new TeamSolution(row.PackageId, row.Name, row.Version, [.. row.Plugins.Keys.Order(StringComparer.Ordinal)])
            : null;

    public Task<TeamSolutionRow?> FindAsync(string team, CancellationToken ct = default) =>
        Task.FromResult(_rows.GetValueOrDefault(team));

    public Task<IReadOnlyList<TeamSolutionRow>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TeamSolutionRow>>([.. _rows.Values.OrderBy(r => r.Team, StringComparer.Ordinal)]);

    public async Task SaveAsync(TeamSolutionRow row, TriggerAudit audit, CancellationToken ct = default)
    {
        await store.SaveAsync(row, audit, ct);
        _rows[row.Team] = row;
    }

    public async Task<bool> DeleteAsync(string team, TriggerAudit? audit, CancellationToken ct = default)
    {
        var deleted = await store.DeleteAsync(team, audit, ct);
        _rows.TryRemove(team, out _);
        return deleted;
    }
}
