namespace Harness.Host;

/// <summary>
/// Whether a string is a branch name the host will hand to git. A conservative reading of
/// <c>git check-ref-format --branch</c>: a person's typing reaches refspecs and rev-list ranges,
/// so anything git could read as an option, a range or a revision expression is refused.
/// </summary>
public static class BranchNames
{
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255) return false;
        if (name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.')) return false;
        if (name.EndsWith(".lock", StringComparison.Ordinal) || name == "HEAD" || name == "@") return false;
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal)
            || name.Contains("@{", StringComparison.Ordinal)) return false;

        foreach (var c in name)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                return false;
        }

        return name.Split('/').All(part => part.Length > 0 && !part.StartsWith('.'));
    }
}

/// <summary>
/// What an operation that needs a repository's default branch says when it is not known.
/// It stops; it never guesses `main`.
/// </summary>
public static class DefaultBranchNotKnown
{
    public static string Message(string repo) =>
        $"{repo}'s default branch is not known, so nothing was changed. Fetch the repository to "
        + "read it from origin, or set it in Team settings.";
}

/// <summary>
/// Reads origin's HEAD after a clone or a successful Fetch and records it. A failed Fetch
/// never reaches here, so it leaves the stored value - or not known - as it was.
/// </summary>
public static class DefaultBranchRecorder
{
    /// <summary>Reads and records the remote's branch, then answers the branch the host uses now.</summary>
    public static async Task<string?> RecordFromOriginAsync(
        TeamRegistry teams, GitRunner git, string team, string repo, string clonePath, CancellationToken ct)
    {
        var fromRemote = await git.ReadOriginHeadBranchAsync(clonePath, ct);
        await teams.RecordRemoteDefaultBranchAsync(team, repo, fromRemote, ct);
        return teams.DefaultBranchFor(team, repo).Branch;
    }
}

/// <summary>
/// Every team clone whose default branch was never read from its
/// remote (the read failed, or never ran for it) is read once at start, from the
/// <c>refs/remotes/origin/HEAD</c> the clone already holds, with no network. Without it every such
/// clone would read "not known" - the Git dialog refusing, and every backlog item's landed mark
/// saying `unknown` - until a person pressed Fetch on it. The next Fetch refreshes it from the
/// remote; a person's choice is never touched.
/// </summary>
internal sealed class DefaultBranchAtStart(
    TeamRegistry teams, TeamPaths paths, GitRunner git, ILogger<DefaultBranchAtStart> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var team in teams.All())
        {
            foreach (var url in teams.ReposFor(team.Id))
            {
                var repo = RepoUrls.DeriveName(url);
                var clonePath = Path.Combine(paths.ReposFor(team.Id), repo, "main");
                if (teams.DefaultBranchFor(team.Id, repo).FromRemote is not null || !Directory.Exists(clonePath))
                {
                    continue;
                }

                try
                {
                    if (await git.ReadRecordedOriginHeadBranchAsync(clonePath, stoppingToken) is { } branch)
                    {
                        await teams.RecordRemoteDefaultBranchAsync(team.Id, repo, branch, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // One unreadable clone does not stop the others; it stays not known until a Fetch.
                    logger.LogWarning(exception, "Could not read the default branch of {Team}/{Repo} at start", team.Id, repo);
                }
            }
        }
    }
}
