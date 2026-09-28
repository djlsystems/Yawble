using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>What a person sends to create a local repository.</summary>
public sealed record CreateLocalRepo(
    [property: Description(
        "The repository's name: 1 to 100 letters, digits, '.', '_' or '-', starting with a letter or "
        + "digit. A team then names it as `local:<name>`.")]
    string? Name);

/// <summary>
/// THE INSTANCE'S LOCAL REPOSITORIES, AS A PERSON MANAGES THEM: list, create, delete. Every route is
/// <c>.HumansOnly()</c> - a Manager's or a member's credential is refused, and agents get no tool
/// for any of this. Create and delete append a <c>tenant_events</c> row; delete is refused while any
/// team names the repository.
/// </summary>
public static class LocalRepoEndpoints
{
    public static object Wire(LocalRepoInfo info) => new
    {
        name = info.Name,
        reference = info.Reference,
        sizeBytes = info.SizeBytes,
        defaultBranch = info.DefaultBranch,
        lastCommit = info.LastCommit is { } commit
            ? new { sha = commit.Sha, subject = commit.Subject, committedAt = commit.CommittedAt }
            : null,
        teams = info.Teams,

        // No team names it: kept when its team was deleted (B001F), and a person's to delete.
        unused = info.Teams.Count == 0,
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/local-repos", async (LocalRepos repos, TeamRegistry teams, CancellationToken ct) =>
        {
            var listed = new List<object>();
            foreach (var name in repos.Names())
            {
                if (await repos.DescribeAsync(name, teams.TeamsUsingLocalRepo(name), ct) is { } info) listed.Add(Wire(info));
            }

            return Results.Ok(listed);
        })
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("The instance's local repositories")
            .WithDescription(
                "Every bare repository under `<dataRoot>/repos`, with its `reference` (`local:<name>`, what a "
                + "team's repository list names), size on disk in bytes, default branch (its HEAD), last "
                + "commit on that branch, the teams whose repositories name it, and `unused` - no team names it, as "
                + "after its team was deleted, which keeps it.\n\n**A person's view.**");

        app.MapPost("/api/local-repos", async (
            CreateLocalRepo request, LocalRepos repos, TeamRegistry teams, TenantLogging audit,
            HttpContext context, CancellationToken ct) =>
        {
            var name = request.Name?.Trim() ?? string.Empty;
            if (!LocalRepos.IsLegalName(name))
            {
                return Results.BadRequest(new { error = LocalRepos.IllegalName(name) });
            }

            if (await repos.CreateAsync(name, ct) is { } refusal)
            {
                return refusal.StartsWith("A local repository", StringComparison.Ordinal)
                    ? Results.Conflict(new { error = refusal })
                    : Results.Json(new { error = refusal }, statusCode: StatusCodes.Status500InternalServerError);
            }

            await audit.WriteAsync(
                context, TenantActions.LocalRepoCreated, name, name,
                new { reference = LocalRepos.ReferenceFor(name), defaultBranch = LocalRepos.InitialBranch }, ct);

            var info = await repos.DescribeAsync(name, teams.TeamsUsingLocalRepo(name), ct);
            return Results.Created($"/api/local-repos/{name}", info is null ? null : Wire(info));
        })
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("Create a local repository")
            .WithDescription(
                "Body `{ \"name\": \"widget\" }`. Creates the bare repository `<dataRoot>/repos/widget.git`, owned "
                + "by the Host and readable, never writable, by agents, with default branch `main` and one empty "
                + "commit on it, so a team can clone and branch from it at once. A team then names it as "
                + "`local:widget` in its repository list. Appends `local-repo.created` to the tenant log. 400 for "
                + "a name that is not legal, 409 when one of that name (in any case) exists.\n\n**A person's action.**");

        app.MapDelete("/api/local-repos/{name}", async (
            [Description("The local repository's name, as `GET /api/local-repos` lists it")] string name,
            LocalRepos repos, TeamRegistry teams, ITenantLog log, HttpContext context, CancellationToken ct) =>
        {
            if (!LocalRepos.IsLegalName(name))
            {
                return Results.BadRequest(new { error = LocalRepos.IllegalName(name) });
            }

            if (!repos.Exists(name))
            {
                return Results.NotFound(new { error = $"No local repository '{name}'." });
            }

            var teamsUsing = teams.TeamsUsingLocalRepo(name);
            if (teamsUsing.Count > 0)
            {
                return Results.Conflict(new
                {
                    error = $"'{name}' is used by {string.Join(", ", teamsUsing)}, so it was not deleted. Remove "
                        + $"{LocalRepos.ReferenceFor(name)} from {(teamsUsing.Count == 1 ? "that team's" : "those teams'")} "
                        + "repositories first.",
                    teams = teamsUsing,
                });
            }

            // THE ROW FIRST: a repository removed with no record of who removed it is the one
            // outcome this refuses, so a row that cannot be written stops the delete.
            try
            {
                await log.WriteAsync(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                    context.User.FindFirstValue(ClaimTypes.Email),
                    TenantActions.LocalRepoDeleted, name, name,
                    JsonSerializer.Serialize(new { reference = LocalRepos.ReferenceFor(name) }), ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(
                    new { error = $"'{name}' was not deleted: its tenant log row could not be written ({exception.Message})." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            await repos.DeleteAsync(name, ct);
            return Results.NoContent();
        })
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("Delete a local repository")
            .WithDescription(
                "Removes `<dataRoot>/repos/<name>.git` and everything in it. Refused with 409, naming the teams, "
                + "while any team's repository list names `local:<name>`. Appends `local-repo.deleted` to the "
                + "tenant log first; when that row cannot be written nothing is deleted. 404 for an unknown "
                + "name, 400 for one that is not legal. The web app asks before it sends this.\n\n**A person's action.**");
    }
}
