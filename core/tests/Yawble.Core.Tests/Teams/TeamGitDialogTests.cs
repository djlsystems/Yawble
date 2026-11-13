using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The Git dialog's actions (Yawble RepoEndpoints: status, fetch, bring current, merge to main, ask the team) against
/// a person's repository with an origin. Never forced, rebased or stashed; a conflict changes nothing.
/// </summary>
public class TeamGitDialogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TeamsFixture F, MemberRef Manager, MemberRef Ada, string Picked, string Bare)> PickedTeam()
    {
        var f = new TeamsFixture();
        var (manager, ada, _) = f.Team();
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        return (f, manager, ada, picked, bare);
    }

    /// <summary>A commit on the member's branch, fetched back into the team repository as a run's end does.</summary>
    private static async Task Commit(TeamsFixture f, MemberRef member, string file, string content)
    {
        var checkout = await f.Repos.PrepareAsync(member, Ct);
        File.WriteAllText(Path.Combine(checkout.ClonePath, file), content);
        AgentGit.Ok(checkout, "add", file);
        AgentGit.Ok(checkout, "commit", "-q", "-m", file);
        Assert.True((await f.Repos.FetchBackAsync(member, Ct)).Fetched);
    }

    /// <summary>Someone else pushes a commit to origin's trunk.</summary>
    private static void PushedElsewhere(TeamsFixture f, string bare, string file, string content)
    {
        var other = Path.Combine(f.Db.Dir, "elsewhere-" + Guid.NewGuid().ToString("N")[..6]);
        AgentGit.Host(f.Db.Dir, "clone", "-q", "-b", "trunk", bare, other);
        File.WriteAllText(Path.Combine(other, file), content);
        AgentGit.Host(other, "add", file);
        AgentGit.Host(other, "-c", "user.name=Other", "-c", "user.email=o@example.invalid", "commit", "-q", "-m", "elsewhere " + file);
        AgentGit.Host(other, "push", "-q", "origin", "trunk");
    }

    private static string Rev(string repo, string reference) => AgentGit.Host(repo, "rev-parse", reference);

    [Fact]
    public async Task TheStatusShowsTheTeamBranch_ItsMembersBranches_AndThatItWouldFastForward()
    {
        var (f, manager, ada, _, _) = await PickedTeam();
        using (f)
        {
            await Commit(f, ada, "a.txt", "a");
            await Commit(f, manager, "m.txt", "m");
            var s = await f.Repos.GitStatusAsync(ada.TeamId, "Alpha", Ct);
            Assert.Equal("trunk", s.BaseBranch);
            Assert.Equal("trunk", s.CheckedOut);
            Assert.NotNull(s.Origin);
            Assert.Equal("team/alpha", s.TeamBranch);
            Assert.Equal(1, s.TeamAheadOfBase);
            Assert.Equal("fast-forward", s.Merge);
            var a = Assert.Single(s.Members, b => b.Branch == "members/ada");
            Assert.Equal(1, a.AheadOfBase);
            Assert.False(a.InTeamBranch);
        }
    }

    [Fact]
    public async Task MergeToMain_FastForwardsOriginAndTheCheckedOutBase_WhenTheTeamBranchContainsIt()
    {
        var (f, manager, _, picked, bare) = await PickedTeam();
        using (f)
        {
            await Commit(f, manager, "m.txt", "m");
            var team = Rev(picked, "refs/heads/team/alpha");
            var r = await f.Repos.GitMergeTeamToBaseAsync(manager.TeamId, "Alpha", Ct);
            Assert.Equal("fast-forward", r.Outcome);
            Assert.True(r.Changed);
            Assert.Equal(team, Rev(bare, "refs/heads/trunk"));
            Assert.Equal(team, Rev(picked, "refs/heads/trunk"));
            Assert.True(File.Exists(Path.Combine(picked, "m.txt"))); // the checked-out files followed
            Assert.Equal("up-to-date", r.Status!.Merge);
            // The shared view now holds the new trunk, so the Manager can merge it from the bundle.
            Assert.Equal(team, Rev(f.Repos.SharedRepoPath("Alpha"), "refs/heads/trunk"));
        }
    }

    [Fact]
    public async Task MergeToMain_MakesAMergeCommitWithTheTeamsTipAsSecondParent_WhenOriginMovedOn()
    {
        var (f, manager, _, picked, bare) = await PickedTeam();
        using (f)
        {
            await Commit(f, manager, "m.txt", "m");
            PushedElsewhere(f, bare, "other.txt", "o");
            var originBefore = Rev(bare, "refs/heads/trunk");
            var team = Rev(picked, "refs/heads/team/alpha");

            var r = await f.Repos.GitMergeTeamToBaseAsync(manager.TeamId, "Alpha", Ct);
            Assert.Equal("merged", r.Outcome);
            var merged = Rev(bare, "refs/heads/trunk");
            Assert.Equal(r.Sha, merged);
            Assert.Equal($"{merged} {originBefore} {team}", AgentGit.Host(bare, "rev-list", "--parents", "-n", "1", merged));
            Assert.Equal(merged, Rev(picked, "refs/heads/trunk"));
            Assert.True(File.Exists(Path.Combine(picked, "other.txt")));
            Assert.True(File.Exists(Path.Combine(picked, "m.txt")));
            Assert.Equal(team, Rev(picked, "refs/heads/team/alpha")); // the team branch is never rewritten
        }
    }

    [Fact]
    public async Task AConflict_IsRefusedNamingTheFiles_AndChangesNothing_AndAskTheTeamTellsTheManagerHow()
    {
        var (f, manager, _, picked, bare) = await PickedTeam();
        using (f)
        {
            await Commit(f, manager, "README.md", "# team version\n");
            PushedElsewhere(f, bare, "README.md", "# someone else's version\n");
            var originBefore = Rev(bare, "refs/heads/trunk");
            var localBefore = Rev(picked, "refs/heads/trunk");

            var r = await f.Repos.GitMergeTeamToBaseAsync(manager.TeamId, "Alpha", Ct);
            Assert.Equal("conflict", r.Outcome);
            Assert.False(r.Changed);
            Assert.Equal(["README.md"], r.Files);
            Assert.Equal(originBefore, Rev(bare, "refs/heads/trunk"));
            Assert.Equal(localBefore, Rev(picked, "refs/heads/trunk"));
            Assert.Equal("", AgentGit.Host(picked, "status", "--porcelain", "--untracked-files=no"));
            Assert.Equal("conflict", r.Status!.Merge);

            // Ask the team: the Manager is told how to bring its branch current from the bundle, naming the files.
            // Bring current first, so the shared view holds origin's trunk.
            Assert.Equal("brought-current", (await f.Repos.GitBringBaseCurrentAsync(manager.TeamId, "Alpha", Ct)).Outcome);
            var text = await f.Repos.GitAskTeamTextAsync(manager.TeamId, "Alpha", Ct);
            Assert.Contains("README.md", text);
            Assert.Contains("git fetch \"$env:YAWBLE_TEAM_BUNDLE\" trunk", text);
            Assert.Contains("Never rebase", text);
            Assert.Equal(originBefore, Rev(f.Repos.SharedRepoPath("Alpha"), "refs/heads/trunk"));
        }
    }

    [Fact]
    public async Task UncommittedChangesOnTheCheckedOutBase_AreNeverTouched_TheMergeIsRefused()
    {
        var (f, manager, _, picked, bare) = await PickedTeam();
        using (f)
        {
            await Commit(f, manager, "m.txt", "m");
            File.WriteAllText(Path.Combine(picked, "README.md"), "# my edit in progress\n");
            var originBefore = Rev(bare, "refs/heads/trunk");
            var r = await f.Repos.GitMergeTeamToBaseAsync(manager.TeamId, "Alpha", Ct);
            Assert.Equal("refused", r.Outcome);
            Assert.Contains("uncommitted changes", r.Message);
            Assert.Equal(originBefore, Rev(bare, "refs/heads/trunk"));
            Assert.Equal("# my edit in progress\n", File.ReadAllText(Path.Combine(picked, "README.md")));
        }
    }

    [Fact]
    public async Task ThePersonsOwnUnpushedCommitsOnTheBase_AreNeverPushedForThem()
    {
        var (f, manager, _, picked, bare) = await PickedTeam();
        using (f)
        {
            await Commit(f, manager, "m.txt", "m");
            File.WriteAllText(Path.Combine(picked, "mine.txt"), "mine");
            AgentGit.Host(picked, "add", "mine.txt");
            AgentGit.Host(picked, "-c", "user.name=p", "-c", "user.email=p@x", "commit", "-q", "-m", "mine");
            var originBefore = Rev(bare, "refs/heads/trunk");
            var r = await f.Repos.GitMergeTeamToBaseAsync(manager.TeamId, "Alpha", Ct);
            Assert.Equal("refused", r.Outcome);
            Assert.Contains("never pushes your own commits", r.Message);
            Assert.Equal(originBefore, Rev(bare, "refs/heads/trunk"));
            Assert.Equal(1, r.Status!.BaseAheadOfOrigin);
        }
    }

    [Fact]
    public async Task WithoutAnOrigin_TheLocalBaseIsMerged()
    {
        using var f = new TeamsFixture();
        var (manager, _, _) = f.Team();
        await Commit(f, manager, "m.txt", "m");
        var repo = f.Repos.RepoOf(manager.TeamId)!;
        Assert.True(repo.Managed);
        var r = await f.Repos.GitMergeTeamToBaseAsync(manager.TeamId, "Alpha", Ct);
        Assert.Equal("fast-forward", r.Outcome);
        Assert.Equal(Rev(repo.Path, "refs/heads/team/alpha"), Rev(repo.Path, "refs/heads/" + repo.BaseBranch));
        Assert.Equal("refused", (await f.Repos.GitFetchAsync(manager.TeamId, "Alpha", Ct)).Outcome); // nothing to fetch
    }

    [Fact]
    public async Task BringCurrent_FastForwardsTheBaseToOrigins_AndRefusesWhenItHasCommitsOfItsOwn()
    {
        var (f, manager, _, picked, bare) = await PickedTeam();
        using (f)
        {
            PushedElsewhere(f, bare, "other.txt", "o");
            var r = await f.Repos.GitBringBaseCurrentAsync(manager.TeamId, "Alpha", Ct);
            Assert.Equal("brought-current", r.Outcome);
            Assert.Equal(Rev(bare, "refs/heads/trunk"), Rev(picked, "refs/heads/trunk"));
            Assert.True(File.Exists(Path.Combine(picked, "other.txt")));
            Assert.Equal("up-to-date", (await f.Repos.GitBringBaseCurrentAsync(manager.TeamId, "Alpha", Ct)).Outcome);

            PushedElsewhere(f, bare, "other2.txt", "o2");
            File.WriteAllText(Path.Combine(picked, "mine.txt"), "mine");
            AgentGit.Host(picked, "add", "mine.txt");
            AgentGit.Host(picked, "-c", "user.name=p", "-c", "user.email=p@x", "commit", "-q", "-m", "mine");
            var mine = Rev(picked, "refs/heads/trunk");
            var refused = await f.Repos.GitBringBaseCurrentAsync(manager.TeamId, "Alpha", Ct);
            Assert.Equal("refused", refused.Outcome);
            Assert.Equal(mine, Rev(picked, "refs/heads/trunk"));
        }
    }

    [Fact]
    public void NoGitDialogCodeReachesForAForcePushOrARebase()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Yawble.Core", "Teams"));
        foreach (var file in new[] { "TeamRepos.Git.cs", "TeamPublish.cs" })
        {
            var code = File.ReadAllText(Path.Combine(root, file));
            var lines = code.Split('\n').Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal) && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
            var body = string.Join('\n', lines);
            Assert.DoesNotContain("--force", body);
            Assert.DoesNotContain("\"-f\"", body);
            Assert.DoesNotContain("\"rebase\"", body);
            Assert.DoesNotContain("\"stash\"", body);
            Assert.DoesNotContain("\"reset\"", body);
        }
    }
}
