namespace Harness.Contracts;

/// <summary>What an unfinished removal was removing, which decides how a retry finishes it.</summary>
public static class RemovalKinds
{
    /// <summary>A deleted team's root. Finished marker-last, and only while it carries the marker.</summary>
    public const string TeamRoot = "team-root";

    /// <summary>A deleted member's workspace. Finished only while no member of that name is live.</summary>
    public const string Workspace = "workspace";

    /// <summary>A folder a reset emptied and kept. A retry removes only the paths it named.</summary>
    public const string Emptied = "emptied";
}

/// <summary>
/// A folder whose removal did not finish: <paramref name="Remaining"/> are the paths still on disk
/// when it was last attempted. <paramref name="Member"/> is set only for a workspace.
/// <paramref name="HostLeft"/> is, for an emptied folder, the modification time (UTC ticks) the last
/// attempt's own deletes left on each directory it had judged nothing was written in since the
/// reset: one still at exactly that time has had nothing written in it since either.
/// </summary>
public sealed record UnfinishedRemoval(
    string Path,
    string Kind,
    string Team,
    string? Member,
    IReadOnlyList<string> Remaining,
    DateTimeOffset RecordedAt,
    int Attempts,
    IReadOnlyDictionary<string, long>? HostLeft = null);

/// <summary>
/// The folders a deletion or reset could not finish removing (<c>unfinished_removals</c>, auth-012).
/// Written when a removal leaves something behind, forgotten when a retry finishes it. The Host
/// retries every row at start, and a person can retry one on request.
/// </summary>
public interface IUnfinishedRemovals
{
    /// <summary>Records, or updates, the row for <paramref name="removal"/>'s path. A row already
    /// there keeps its first <c>RecordedAt</c> and counts one more attempt.</summary>
    Task RecordAsync(UnfinishedRemoval removal, CancellationToken ct = default);

    Task<IReadOnlyList<UnfinishedRemoval>> ListAsync(CancellationToken ct = default);

    Task<UnfinishedRemoval?> FindAsync(string path, CancellationToken ct = default);

    Task ForgetAsync(string path, CancellationToken ct = default);
}
