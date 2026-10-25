namespace Harness.Host;

/// <summary>
/// WHAT RUNNING MEMBERS' WORKTREES DID (<c>file_changes</c>), recorded for the Monitor's replay. A
/// consumer depends on this, not on the SQLite class, so a provider is a different implementation and
/// never a different caller. See <see cref="WorkspaceFileChangeStore"/>.
/// </summary>
public interface IWorkspaceFileChangeStore
{
    /// <summary>Records one change, with the run (<paramref name="correlation"/>) it belongs to when known.</summary>
    Task AppendAsync(WorkspaceChange change, long? correlation, CancellationToken ct = default);

    /// <summary>The rows after <paramref name="after"/> within <paramref name="from"/> ..
    /// <paramref name="to"/>, oldest first, at most <paramref name="take"/>, scoped by each team's
    /// creation in <paramref name="since"/>.</summary>
    Task<IReadOnlyList<RecordedWorkspaceChange>> ReadAsync(
        DateTimeOffset from, DateTimeOffset to, long after, int take,
        IReadOnlyDictionary<string, DateTimeOffset> since, CancellationToken ct = default);

    /// <summary>Removes every row of <paramref name="team"/>; answers how many went.</summary>
    Task<int> DeleteForTeamAsync(string team, CancellationToken ct = default);

    /// <summary>Removes the rows of these members of <paramref name="team"/>; answers how many went.</summary>
    Task<int> DeleteForMembersAsync(string team, IReadOnlyCollection<string> members, CancellationToken ct = default);
}
