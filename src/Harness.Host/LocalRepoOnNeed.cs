using System.Collections.Concurrent;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>What <see cref="LocalRepoOnNeed.CreateAsync"/> made: the repository, and the clone to work in.</summary>
public sealed record LocalRepoMade(
    TeamLocalRepository Repository, string ClonePath, bool Cloned, string DefaultBranch, string TeamBranch);

/// <summary>
/// A TEAM'S LOCAL REPOSITORY, MADE WHEN ITS WORK FIRST NEEDS ONE: the <c>repo</c> tool's
/// <c>create-local</c>, behind <c>POST /api/teams/{team}/local-repo</c> for a team's Manager, its
/// members and the Concierge. A team is created with no repository; this is the one way an agent gives
/// it one, and only while it has NONE - a team with any repository is refused naming it, and nothing
/// changes.
///
/// <para>
/// The same path as a person's "Create a local repository": <see cref="TeamRepoSetup.EnsureLocalAsync"/>
/// (an unused repository of the team's name is reused, one a team uses gets <c>-2</c>), then
/// <see cref="TeamRegistry.SetReposAsync"/>, which clones it and re-composes the Manager's prompt so
/// its next run lists the clone. No ready notice is sent: the caller is told by its answer.
/// </para>
///
/// <para>
/// <b>ONE MAKE PER TEAM AT A TIME.</b> A second call while the first is making it waits for that make
/// and answers the same; a call after it has finished finds the repository and is refused.
/// </para>
/// </summary>
public sealed class LocalRepoOnNeed(TeamRegistry teams, TeamRepoSetup setup, TeamPaths paths, TenantLogging audit)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<LocalRepoMade>>> _making =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Awaited as each make starts; null in the Host. A test holds a make open with it, to
    /// call again while the first is making the repository.</summary>
    public Func<Task>? BeforeMake { get; set; }

    /// <summary>The refusal for a team that already has a repository, naming it.</summary>
    public static string HasRepository(string team, IReadOnlyList<string> repos) =>
        $"{team} already has a repository: {string.Join(", ", repos.Select(RepoUrls.DeriveName))}. "
        + "Work in it; a person adds another in Team settings.";

    /// <summary>
    /// Makes, attaches and clones <paramref name="team"/>'s local repository, or joins the make
    /// already running for it. Null with <paramref name="refusal"/> set when the team has a repository.
    /// Throws <see cref="ArgumentException"/> naming the reason when the repository cannot be made.
    /// </summary>
    public async Task<(LocalRepoMade? Made, string? Refusal)> CreateAsync(
        string team, string? actorId, string? actorEmail, string agent)
    {
        if (_making.TryGetValue(team, out var running)) return (await running.Value, null);

        var current = teams.ReposFor(team);
        if (current.Count > 0) return (null, HasRepository(team, current));

        var make = _making.GetOrAdd(team, _ => new Lazy<Task<LocalRepoMade>>(() => MakeAsync(team, actorId, actorEmail, agent)));
        try
        {
            return (await make.Value, null);
        }
        finally
        {
            _making.TryRemove(new KeyValuePair<string, Lazy<Task<LocalRepoMade>>>(team, make));
        }
    }

    // NOT the caller's token: a second caller waits on this same make, and one going away must not
    // cancel the other's.
    private async Task<LocalRepoMade> MakeAsync(string team, string? actorId, string? actorEmail, string agent)
    {
        await Task.Yield();
        if (BeforeMake is { } hold) await hold();

        TeamLocalRepository? made = null;
        var attached = false;
        try
        {
            made = await setup.EnsureLocalAsync(team, CancellationToken.None);
            await teams.SetReposAsync(team, [made.Reference], CancellationToken.None, quietWhenCloned: true);
            attached = true;
        }
        finally
        {
            if (!attached) await setup.ForgetAsync(made, CancellationToken.None);
        }

        var clonePath = Path.Combine(paths.ReposFor(team), made.Name, "main");
        var defaultBranch = teams.DefaultBranchFor(team, made.Name).Branch ?? LocalRepos.InitialBranch;

        await audit.WriteAsAsync(
            actorId, actorEmail, TenantActions.LocalRepoCreated, made.Name, made.Name,
            new
            {
                reference = made.Reference,
                defaultBranch,
                team,
                agent,
                created = made.Created,
            },
            CancellationToken.None);

        return new LocalRepoMade(
            made, clonePath, Directory.Exists(Path.Combine(clonePath, ".git")), defaultBranch,
            RepositoryReset.TeamBranchOf(team));
    }

    /// <summary>
    /// The agent a machine principal is, as the tenant row names it: a team member by its name, the
    /// Concierge as itself. Null for any other machine principal, which may not make one.
    /// </summary>
    public static string? AgentOf(Principal principal) => principal.Kind switch
    {
        PrincipalKind.Container when ContainerId.TryParse(principal.Id, out var id) => id.Name,
        PrincipalKind.Concierge or PrincipalKind.TenantConcierge => "Concierge",
        _ => null,
    };
}
