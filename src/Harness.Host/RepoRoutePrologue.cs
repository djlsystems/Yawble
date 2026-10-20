using System.Security.Claims;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>What a repository route resolved before it acts: the stored team, who asked, the repository's URL and its clone's path.</summary>
internal sealed record RepoRoute(string Stored, string? UserId, string? Email, string RepoUrl, string ClonePath);

/// <summary>
/// THE ONE PROLOGUE OF EVERY REPOSITORY ROUTE: the team must exist, no member of it may be working,
/// the repository must be one of the team's, and the clone's path is derived once. A refusal is the
/// route's answer, and a busy refusal is written to the tenant log under the route's own action, as
/// each route wrote it.
/// </summary>
internal static class RepoRoutePrologue
{
    public static Task<(IResult? Refusal, RepoRoute? Route)> ResolveAsync(
        TeamRegistry teams, ContainerHost host, TeamPaths paths, ITenantLog log, HttpContext httpContext,
        string team, string repo, string action, CancellationToken ct) =>
        ResolveAsync(teams, host, paths, log, httpContext, team, repo, action, httpContext.User.FindFirstValue(ClaimTypes.Email), ct);

    /// <summary>As above, naming the actor's email itself (a merge gate's caller) rather than reading the claim.</summary>
    public static async Task<(IResult? Refusal, RepoRoute? Route)> ResolveAsync(
        TeamRegistry teams, ContainerHost host, TeamPaths paths, ITenantLog log, HttpContext httpContext,
        string team, string repo, string action, string? email, CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored) return (Refusals.NoTeam(team), null);

        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        var busyMembers = GetBusyMembers(teams, host, stored);
        if (busyMembers.Count > 0)
        {
            await log.WriteAsync(userId, email, action, $"{stored}/{repo}", null,
                JsonSerializer.Serialize(new { refused = true, reason = $"{string.Join(", ", busyMembers)} still working." }), ct);
            return (Results.Conflict(new { error = $"{string.Join(", ", busyMembers)} still working.", busy = busyMembers }), null);
        }

        var repoUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (repoUrl is null) return (Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." }), null);

        return (null, new RepoRoute(stored, userId, email, repoUrl, Path.Combine(paths.ReposFor(stored), repo, "main")));
    }

    internal static List<string> GetBusyMembers(TeamRegistry teams, ContainerHost host, string storedTeamId)
    {
        var busy = new List<string>();
        var teamContainers = teams.ContainerIdsOf(storedTeamId);

        foreach (var id in teamContainers)
        {
            if (host.Find(id) is { } container && container.State == ContainerState.Running)
            {
                busy.Add(id.Name);
            }
        }

        return busy;
    }
}
