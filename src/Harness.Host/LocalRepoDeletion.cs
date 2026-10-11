using System.Security.Claims;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>How one local repository delete ended.</summary>
public enum LocalRepoDeleteOutcome
{
    Deleted,
    IllegalName,
    NotFound,
    InUse,
    Unrecorded,
    Failed,

    /// <summary>The name is gone but files remain in its <c>.deleting-</c> folder: named, recorded, retried.</summary>
    Incomplete,
}

/// <summary>What <see cref="LocalRepoDeletion.DeleteAsync"/> did, with the sentence a refusal is said in.</summary>
public sealed record LocalRepoDeleteResult(
    LocalRepoDeleteOutcome Outcome,
    string? Error = null,
    IReadOnlyList<string>? Teams = null,
    string? Folder = null,
    IReadOnlyList<string>? Remaining = null)
{
    public bool Deleted => Outcome == LocalRepoDeleteOutcome.Deleted;
}

/// <summary>
/// THE ONE WAY A LOCAL REPOSITORY IS DELETED: Admin -> Repositories' delete
/// (<c>DELETE /api/local-repos/{name}</c>) and a team deletion that was asked to take its local
/// repository with it both come here, so neither can skip the refusal while a team uses it
/// or the <c>local-repo.deleted</c> row that comes first.
///
/// <para>
/// A DELETE THAT LEFT FILES IS NOT DELETED. When the moved-aside <c>.deleting-&lt;guid&gt;</c> folder
/// cannot be fully removed, the answer is <see cref="LocalRepoDeleteOutcome.Incomplete"/>, naming the
/// folder and each path still in it; a <c>local-repo.delete-incomplete</c> row says the same; and the
/// folder is an unfinished removal (<c>unfinished_removals</c>), retried at every start and on request
/// until it is gone.
/// </para>
/// </summary>
public sealed class LocalRepoDeletion(LocalRepos repos, TeamRegistry teams, ITenantLog log)
{
    public async Task<LocalRepoDeleteResult> DeleteAsync(string name, ClaimsPrincipal person, CancellationToken ct)
    {
        if (!LocalRepos.IsLegalName(name))
        {
            return new(LocalRepoDeleteOutcome.IllegalName, LocalRepos.IllegalName(name));
        }

        if (!repos.Exists(name))
        {
            return new(LocalRepoDeleteOutcome.NotFound, $"No local repository '{name}'.");
        }

        var teamsUsing = teams.TeamsUsingLocalRepo(name);
        if (teamsUsing.Count > 0)
        {
            return new(
                LocalRepoDeleteOutcome.InUse,
                $"'{name}' is used by {string.Join(", ", teamsUsing)}, so it was not deleted. Remove "
                + $"{LocalRepos.ReferenceFor(name)} from {(teamsUsing.Count == 1 ? "that team's" : "those teams'")} "
                + "repositories first.",
                teamsUsing);
        }

        // THE ROW FIRST: a repository removed with no record of who removed it is the one
        // outcome this refuses, so a row that cannot be written stops the delete.
        try
        {
            await log.WriteAsync(
                person.FindFirstValue(ClaimTypes.NameIdentifier),
                person.FindFirstValue(ClaimTypes.Email),
                TenantActions.LocalRepoDeleted, name, name,
                JsonSerializer.Serialize(new { reference = LocalRepos.ReferenceFor(name) }), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(
                LocalRepoDeleteOutcome.Unrecorded,
                $"'{name}' was not deleted: its tenant log row could not be written ({exception.Message}).");
        }

        LocalRepoRemoval? removal;

        try
        {
            removal = await repos.DeleteAsync(name, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(LocalRepoDeleteOutcome.Failed, $"'{name}' could not be deleted: {exception.Message}");
        }

        if (removal is null or { Complete: true }) return new(LocalRepoDeleteOutcome.Deleted);

        var sentence =
            $"'{name}' was not fully deleted: {removal.Aside} is still on disk with {removal.Remaining.Count} "
            + $"path(s) left ({string.Join(", ", removal.Remaining.Select(p => removal.Reasons?.GetValueOrDefault(p) is { } why ? $"{p}: {why}" : p))}). "
            + "It is recorded as an unfinished removal and retried at every start and from the retry of unfinished removals.";

        // AFTER the attempt, since it records what the attempt left. The leftover is already
        // recorded for retry whether or not this row can be written, and the answer says which.
        try
        {
            await log.WriteAsync(
                person.FindFirstValue(ClaimTypes.NameIdentifier),
                person.FindFirstValue(ClaimTypes.Email),
                TenantActions.LocalRepoDeleteIncomplete, name, name,
                JsonSerializer.Serialize(new
                {
                    reference = LocalRepos.ReferenceFor(name),
                    folder = removal.Aside,
                    remaining = removal.Remaining,
                }), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            sentence += $" Its {TenantActions.LocalRepoDeleteIncomplete} row could not be written ({exception.Message}).";
        }

        return new(LocalRepoDeleteOutcome.Incomplete, sentence, Folder: removal.Aside, Remaining: removal.Remaining);
    }
}
