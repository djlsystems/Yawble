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
}

/// <summary>What <see cref="LocalRepoDeletion.DeleteAsync"/> did, with the sentence a refusal is said in.</summary>
public sealed record LocalRepoDeleteResult(
    LocalRepoDeleteOutcome Outcome,
    string? Error = null,
    IReadOnlyList<string>? Teams = null)
{
    public bool Deleted => Outcome == LocalRepoDeleteOutcome.Deleted;
}

/// <summary>
/// THE ONE WAY A LOCAL REPOSITORY IS DELETED: Admin -> Repositories' delete
/// (<c>DELETE /api/local-repos/{name}</c>) and a team deletion that was asked to take its local
/// repository with it (B0020) both come here, so neither can skip the refusal while a team uses it
/// or the <c>local-repo.deleted</c> row that comes first.
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

        try
        {
            await repos.DeleteAsync(name, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(LocalRepoDeleteOutcome.Failed, $"'{name}' could not be deleted: {exception.Message}");
        }

        return new(LocalRepoDeleteOutcome.Deleted);
    }
}
