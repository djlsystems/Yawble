using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// One retried removal: what it was, whether it finished, what is still on disk, and why it was
/// set aside when it was (<paramref name="Note"/>).
/// </summary>
public sealed record RemovalRetried(
    string Path,
    string Kind,
    string Team,
    string? Member,
    bool Finished,
    IReadOnlyList<string> Remaining,
    string? Note = null);

/// <summary>
/// RETRIES THE REMOVALS A DELETION OR RESET COULD NOT FINISH: every row at Host start, and one row
/// (or all) when a person asks from the delete dialog. Each goes through <see cref="FolderRemoval"/>,
/// so a retry follows the same rules as the first attempt: the marker last, a root without one
/// never deleted, agent content removed as the agent, links never followed.
///
/// A folder that is live again is not touched and its row is forgotten: a team root whose team
/// exists again (its creation finished the removal first, so this is only a stale row), or a
/// workspace whose member has been added back.
///
/// A retry of every row also FINDS the <c>.deleting-&lt;guid&gt;</c> folders a local repository
/// delete left with no row - from before a delete recorded what it left - and removes them the same
/// way, recording what still remains (<see cref="LocalRepos.DeletesLeft"/>).
/// </summary>
public sealed class UnfinishedRemovalRetry(
    FolderRemoval removal,
    IUnfinishedRemovals store,
    TeamRegistry teams,
    ContainerHost host,
    TeamPaths paths,
    LocalRepos? localRepos = null)
{
    public Task<IReadOnlyList<UnfinishedRemoval>> ListAsync(CancellationToken ct = default) => store.ListAsync(ct);

    /// <summary>Retries the row for <paramref name="path"/>, or every row when it is null. An
    /// unknown path retries nothing.</summary>
    public async Task<IReadOnlyList<RemovalRetried>> RetryAsync(string? path = null, CancellationToken ct = default)
    {
        var rows = path is null
            ? await store.ListAsync(ct)
            : await store.FindAsync(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), ct) is { } one ? [one] : [];

        var retried = new List<RemovalRetried>(rows.Count);

        if (path is null && localRepos is not null)
        {
            var recorded = rows.Select(row => row.Path).ToHashSet(StringComparer.Ordinal);

            foreach (var left in localRepos.DeletesLeft().Where(left => !recorded.Contains(left)))
            {
                var report = await removal.RemoveLocalRepositoryAsync(localRepos.Root, left, ct);
                retried.Add(new RemovalRetried(
                    left, RemovalKinds.LocalRepo, "", null, report.Complete, report.Remaining, report.Refused));
            }
        }

        foreach (var row in rows)
        {
            if (LiveAgain(row) is { } why)
            {
                await store.ForgetAsync(row.Path, ct);
                retried.Add(new RemovalRetried(row.Path, row.Kind, row.Team, row.Member, false, [], why));
                continue;
            }

            FolderRemovalReport report;

            try
            {
                report = await removal.RetryAsync(row, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report = new FolderRemovalReport(row.Remaining, ex.Message);
            }

            retried.Add(new RemovalRetried(
                row.Path, row.Kind, row.Team, row.Member, report.Complete, report.Remaining, report.Refused));
        }

        return retried;
    }

    private string? LiveAgain(UnfinishedRemoval row)
    {
        if (teams.ExistingName(row.Team) is not { } live) return null;

        switch (row.Kind)
        {
            case RemovalKinds.TeamRoot:
                return string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.RootFor(live))), row.Path, StringComparison.Ordinal)
                    ? $"{row.Path} belongs to the live team '{live}' again, so it was left alone."
                    : null;

            case RemovalKinds.Workspace when row.Member is { } member && host.Find(new ContainerId(live, member)) is not null:
                return $"{row.Path} is the workspace of the live member '{live}/{member}' again, so it was left alone.";

            default:
                return null;
        }
    }
}
