using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHETHER THE CLONE'S DEFAULT BRANCH HAS MOVED AWAY FROM ORIGIN'S, AND THE ONE SENTENCE THAT SAYS SO.
///
/// <para>
/// The Manager's delivery rule is: merge members' branches into <c>team/{id}</c>, push that when the
/// work is ready, and never commit to, merge into or move the clone's default branch. The Git
/// dialog lands work from the team branch, so work left on the clone's default branch is work the
/// dialog cannot see as the team's. This detects that state; it never repairs it.
/// </para>
///
/// <para>
/// MOVED MEANS HOLDING COMMITS ORIGIN'S LACKS: the stored default branch is at a commit other than
/// <c>origin/&lt;default&gt;</c> and is not an ancestor of it. A clone merely BEHIND origin (a Fetch
/// moved origin's ref and nothing brought the branch current yet) is the ordinary state, not a move.
/// Measured against <c>origin</c> in contributor mode too, where origin is the fork.
/// </para>
///
/// <para>
/// THE DEFAULT BRANCH IS THE STORED ONE, NEVER ASSUMED. Not known, no clone, or no
/// <c>origin/&lt;default&gt;</c> ref is not measured, and not measured reports nothing.
/// </para>
/// </summary>
public static class DefaultBranchMove
{
    /// <summary>The report's own words, the same on the card, in the feed and in the Git dialog.</summary>
    public static string Sentence(string branch, string commit, string team) =>
        $"moved {branch} in the clone; the work is on {commit}; the team branch is team/{team}";

    /// <summary>
    /// The full sha the clone's <paramref name="branch"/> is at when it holds commits
    /// <c>origin/&lt;branch&gt;</c> lacks, or null when it does not or that could not be measured.
    /// Local git only; no network.
    /// </summary>
    public static async Task<string?> MovedToAsync(
        GitRunner git, string clonePath, string? branch, CancellationToken ct)
    {
        if (branch is null || !Directory.Exists(clonePath)) return null;

        var local = await git.RunGitAsync(clonePath, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], ct);
        var origin = await git.RunGitAsync(clonePath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}"], ct);
        if (local.ExitCode != 0 || origin.ExitCode != 0) return null;

        var localSha = local.Stdout.Trim();
        if (localSha.Length == 0 || localSha == origin.Stdout.Trim()) return null;

        // Behind is not moved: only a branch holding commits origin's lacks is reported.
        var behind = await git.RunGitAsync(
            clonePath, ["merge-base", "--is-ancestor", localSha, $"refs/remotes/origin/{branch}"], ct);
        return behind.ExitCode == 0 ? null : localSha;
    }

    /// <summary>
    /// AT THE END OF A MANAGER'S RUN: one <c>repo.defaultBranchMoved</c> row per repository whose
    /// clone's default branch has moved, with the Manager as Source and the run's causation, so it
    /// lands on the Manager's card and in the feed. Anyone else's run is not checked. Resets
    /// nothing. NEVER THROWS except on <paramref name="ct"/>: the terminal row that follows must not
    /// depend on git.
    /// </summary>
    public static async Task OnRunEndingAsync(
        TeamRegistry teams,
        TeamPaths paths,
        GitRunner git,
        IMessageLog log,
        ContainerId member,
        long? causation,
        CancellationToken ct)
    {
        if (!string.Equals(member.Name, TeamRegistry.DefaultManagerName, StringComparison.OrdinalIgnoreCase)) return;
        if (teams.ExistingName(member.Team) is not { } stored) return;

        foreach (var url in teams.ReposFor(stored))
        {
            try
            {
                var repo = RepoUrls.DeriveName(url);
                var branch = teams.DefaultBranchFor(stored, repo).Branch;
                var clonePath = Path.Combine(paths.ReposFor(stored), repo, "main");

                if (await MovedToAsync(git, clonePath, branch, ct) is not { } commit) continue;

                var payload = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [PayloadFields.Repo] = repo,
                    [PayloadFields.Branch] = branch!,
                    [PayloadFields.Commit] = commit,
                    [PayloadFields.TeamBranch] = $"team/{stored}",
                    [PayloadFields.Reason] = Sentence(branch!, commit, stored),
                });

                await log.AppendAsync(
                    new NewMessage(MessageTypes.RepoDefaultBranchMoved, payload, member.ToString(), causation), ct);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested
                && exception is OperationCanceledException or IOException or UriFormatException
                    or KeyNotFoundException or UnauthorizedAccessException)
            {
                // Not measured is not reported, and never costs the run its terminal row.
            }
        }
    }
}
