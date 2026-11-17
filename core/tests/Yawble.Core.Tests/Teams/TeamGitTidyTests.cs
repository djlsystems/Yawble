using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// B067 (card 361): the tidy plan (every branch in one class), "contained in base" deleting like a merged branch, the realign
/// of a member branch whose clone was reset, and publish fast-forwarding origin's member branches that are behind. Local
/// repositories and bare origins in temp folders only, no network.
/// </summary>
public class TeamGitTidyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? Rev(string repo, string reference)
    {
        var r = AgentGit.Run(repo, Path.GetTempPath(), "rev-parse", "--verify", "-q", reference);
        return r.ExitCode == 0 ? r.Stdout : null;
    }

    private static readonly string[] Id = ["-c", "user.name=Person", "-c", "user.email=p@example.invalid"];

    /// <summary>A commit in the person's repository (its checkout), on whatever is checked out.</summary>
    private static string PersonCommit(string repo, string file, string text)
    {
        File.WriteAllText(Path.Combine(repo, file), text);
        AgentGit.Host(repo, "add", file);
        AgentGit.Host(repo, [.. Id, "commit", "-q", "-m", file]);
        return AgentGit.Host(repo, "rev-parse", "HEAD");
    }

    private static string CloneCommit(MemberCheckout c, string file)
    {
        File.WriteAllText(Path.Combine(c.ClonePath, file), file);
        AgentGit.Ok(c, "add", file);
        AgentGit.Ok(c, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "-m", file);
        return AgentGit.Ok(c, "rev-parse", "HEAD");
    }

    private sealed record Rig(TeamsFixture F, MemberRef Ada, MemberRef Ben, string Picked, string Bare, MemberCheckout AdaClone, MemberCheckout BenClone, string Trunk);

    /// <summary>Team Alpha on the person's repository (base trunk, with an origin); Ada and Ben have clones.</summary>
    private static async Task<Rig> RigAsync(TeamsFixture f)
    {
        var (_, ada, ben) = f.Team();
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        var adaClone = await f.Repos.PrepareAsync(ada, Ct);
        var benClone = await f.Repos.PrepareAsync(ben, Ct);
        return new Rig(f, ada, ben, picked, bare, adaClone, benClone, AgentGit.Host(picked, "rev-parse", "trunk"));
    }

    /// <summary>Ada commits and her work comes back and is published; then her clone is reset to trunk (her work went in by cherry-pick, say).</summary>
    private static async Task<string> AdaWorkThenResetAsync(Rig r)
    {
        var mine = CloneCommit(r.AdaClone, "ada.txt");
        Assert.True((await r.F.Repos.FetchBackAsync(r.Ada, Ct)).Fetched);
        Assert.Contains("members/alpha/ada", (await r.F.Repos.PublishAsync(r.Ada, Ct)).Branches);
        AgentGit.Ok(r.AdaClone, "reset", "-q", "--hard", r.Trunk);
        return mine;
    }

    private static TidyBranch Branch(TeamGitTidyPlan plan, string name) =>
        Assert.Single(Assert.Single(plan.Repos).Branches, b => b.Name == name);

    /// <summary>
    /// The person's repository without an origin and no clones: nothing here needs git's local transport (which a
    /// sandboxed test run cannot start), so the classes and the proof are checked everywhere.
    /// </summary>
    [Fact]
    public async Task WithoutOriginOrClones_MergedContainedAndOwnWorkAreTold_AndContainedDeletesWithItsProof()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var picked = Path.Combine(f.Db.Dir, "local-repo");
        Directory.CreateDirectory(picked);
        AgentGit.Host(picked, "init", "-q", "-b", "trunk");
        var trunk = PersonCommit(picked, "README.md", "# service\n");
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);

        AgentGit.Host(picked, "branch", "old/merged", trunk);
        AgentGit.Host(picked, "switch", "-q", "-c", "feature/picked");
        var pick = PersonCommit(picked, "picked.txt", "picked");
        AgentGit.Host(picked, "switch", "-q", "-c", "feature/picked-merged", trunk);
        var pick2 = PersonCommit(picked, "picked2.txt", "picked2");
        AgentGit.Host(picked, "switch", "-q", "-c", "feature/own", trunk);
        var own = PersonCommit(picked, "own.txt", "own");
        AgentGit.Host(picked, "switch", "-q", "trunk");
        AgentGit.Host(picked, [.. Id, "cherry-pick", "-x", pick]);
        AgentGit.Host(picked, [.. Id, "cherry-pick", "-x", pick2]);
        AgentGit.Host(picked, "switch", "-q", "feature/picked-merged");
        AgentGit.Host(picked, [.. Id, "merge", "-q", "--no-edit", "trunk"]);
        AgentGit.Host(picked, "switch", "-q", "trunk");

        var plan = await f.Repos.GitTidyPlanAsync(ada.TeamId, "Alpha", Ct);
        Assert.Equal(TidyClasses.Merged, Branch(plan, "old/merged").Class);
        var contained = Branch(plan, "feature/picked");
        Assert.Equal(TidyClasses.Contained, contained.Class);
        Assert.Contains($"git cherry trunk {pick[..12]}: every commit has an equivalent in trunk: {pick[..12]} picked.txt", contained.Proof);
        var pickedMerged = Branch(plan, "feature/picked-merged");
        Assert.Equal(TidyClasses.Contained, pickedMerged.Class);
        Assert.Contains($"{pick2[..12]} picked2.txt = trunk", pickedMerged.Proof);
        var ownWork = Branch(plan, "feature/own");
        Assert.Equal(TidyClasses.OwnWork, ownWork.Class);
        Assert.Equal([new TidyCommit(own, "own.txt")], ownWork.Commits);
        Assert.Equal(["local"], ownWork.Where);

        // Delete: the contained ones go without unmerged_ok (the plan line carries the proof); own work is kept.
        string[] names = ["feature/picked", "feature/picked-merged", "feature/own"];
        var del = await f.Repos.GitDeleteBranchesAsync(ada.TeamId, "Alpha", ["Manager", "Ada", "Ben"], "main", names, false, [], null, Ct);
        Assert.Contains($"delete feature/picked at {pick} (contained in base: git cherry trunk", del.Plan);
        Assert.Contains("delete feature/picked-merged at", del.Plan);
        Assert.Contains("has the same change in trunk", del.Plan);
        Assert.Contains($"feature/own ({own[..12]}): KEPT, not merged", del.Plan);
        var done = await f.Repos.GitDeleteBranchesAsync(ada.TeamId, "Alpha", ["Manager", "Ada", "Ben"], "main", names, false, [], del.Token, Ct);
        Assert.False(done.Stale);
        Assert.Contains($"deleted feature/picked; was {pick}", done.Result);
        Assert.Null(Rev(picked, "refs/heads/feature/picked"));
        Assert.Null(Rev(picked, "refs/heads/feature/picked-merged"));
        Assert.Equal(own, Rev(picked, "refs/heads/feature/own"));
    }

    [Fact]
    public async Task ThePlan_PutsEveryBranchInOneClass()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var adaWork = await AdaWorkThenResetAsync(r);

        // Ben's working branch, merged (it is trunk).
        AgentGit.Host(r.Picked, "branch", "members/alpha/ben", r.Trunk);
        // Merged and nobody's.
        AgentGit.Host(r.Picked, "branch", "old/merged", r.Trunk);
        // Cherry-picked into trunk: contained (git cherry says "-").
        AgentGit.Host(r.Picked, "switch", "-q", "-c", "feature/picked");
        var picked = PersonCommit(r.Picked, "picked.txt", "picked");
        // Own work, then a branch whose commit was picked and which merged trunk since (git cherry alone cannot tell).
        AgentGit.Host(r.Picked, "switch", "-q", "-c", "feature/own", r.Trunk);
        var own = PersonCommit(r.Picked, "own.txt", "own");
        AgentGit.Host(r.Picked, "switch", "-q", "-c", "feature/picked-merged", r.Trunk);
        var picked2 = PersonCommit(r.Picked, "picked2.txt", "picked2");
        AgentGit.Host(r.Picked, "switch", "-q", "trunk");
        AgentGit.Host(r.Picked, [.. Id, "cherry-pick", "-x", picked]);
        AgentGit.Host(r.Picked, [.. Id, "cherry-pick", "-x", picked2]);
        AgentGit.Host(r.Picked, "switch", "-q", "feature/picked-merged");
        AgentGit.Host(r.Picked, [.. Id, "merge", "-q", "--no-edit", "trunk"]);
        AgentGit.Host(r.Picked, "switch", "-q", "trunk");
        // Only on origin, with work of its own.
        AgentGit.Host(r.Picked, "push", "-q", "origin", "feature/own:refs/heads/someone/else");
        // Ada's scratch branch, only in her clone.
        AgentGit.Ok(r.AdaClone, "branch", "scratch/try");

        var plan = await f.Repos.GitTidyPlanAsync(r.Ada.TeamId, "Alpha", Ct);
        var repo = Assert.Single(plan.Repos);
        Assert.Equal("main", repo.Repo);
        Assert.Empty(repo.Notes);
        Assert.DoesNotContain(repo.Branches, b => b.Name == "trunk");
        Assert.Equal(repo.Branches.Count, repo.Branches.Select(b => b.Name).Distinct().Count()); // each branch once

        var moved = Branch(plan, "members/alpha/ada");
        Assert.Equal(TidyClasses.Moved, moved.Class);
        Assert.Equal("Ada", moved.Member);
        Assert.Equal(adaWork, moved.TeamSha);
        Assert.Equal(r.Trunk, moved.CloneSha);
        Assert.Equal(adaWork, Assert.Single(moved.Commits!).Sha);
        Assert.Contains("local", moved.Where);
        Assert.Contains("origin", moved.Where);
        Assert.Contains("clone:Ada", moved.Where);

        var ben = Branch(plan, "members/alpha/ben");
        Assert.Equal(TidyClasses.Working, ben.Class);
        Assert.Equal("Ben", ben.Member);
        Assert.Contains("working branch", ben.Proof);

        Assert.Equal(TidyClasses.Merged, Branch(plan, "old/merged").Class);

        var contained = Branch(plan, "feature/picked");
        Assert.Equal(TidyClasses.Contained, contained.Class);
        Assert.Contains("git cherry trunk", contained.Proof);
        Assert.Contains(picked[..12], contained.Proof);

        var pickedMerged = Branch(plan, "feature/picked-merged");
        Assert.Equal(TidyClasses.Contained, pickedMerged.Class);
        Assert.Contains("same change in trunk", pickedMerged.Proof);

        var ownWork = Branch(plan, "feature/own");
        Assert.Equal(TidyClasses.OwnWork, ownWork.Class);
        Assert.Equal(own, Assert.Single(ownWork.Commits!).Sha);

        var originOnly = Branch(plan, "someone/else");
        Assert.Equal(TidyClasses.OriginOnly, originOnly.Class);
        Assert.Equal(["origin"], originOnly.Where);
        Assert.Equal(own, Assert.Single(originOnly.Commits!).Sha);

        var scratch = Branch(plan, "scratch/try");
        Assert.Equal(TidyClasses.Scratch, scratch.Class);
        Assert.Equal(["clone:Ada"], scratch.Where);
        Assert.Equal("Ada", scratch.Member);

        // The window's contract (git.tidyPlan).
        var json = Yawble.Core.Ui.UiRpc.TidyPlanJson(plan);
        var row = json["repos"]![0]!["branches"]!.AsArray().First(b => b!["name"]!.GetValue<string>() == "members/alpha/ada")!;
        Assert.Equal("moved", row["class"]!.GetValue<string>());
        Assert.Equal(adaWork, row["teamSha"]!.GetValue<string>());
        Assert.Equal(r.Trunk, row["cloneSha"]!.GetValue<string>());
        Assert.Equal("Ada", row["member"]!.GetValue<string>());
        Assert.Equal(adaWork, row["commits"]![0]!["sha"]!.GetValue<string>());
        Assert.Contains("clone:Ada", row["where"]!.AsArray().Select(w => w!.GetValue<string>()));

        // Planning changed nothing anywhere.
        Assert.Equal(adaWork, Rev(r.Picked, "refs/heads/members/alpha/ada"));
        Assert.Equal(adaWork, Rev(r.Bare, "refs/heads/members/alpha/ada"));
    }

    [Fact]
    public async Task AContainedBranch_DeletesWithoutUnmergedOk_AndThePlanShowsTheProof()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Picked, "switch", "-q", "-c", "feature/picked");
        var picked = PersonCommit(r.Picked, "picked.txt", "picked");
        AgentGit.Host(r.Picked, "push", "-q", "origin", "feature/picked");
        AgentGit.Host(r.Picked, "switch", "-q", "trunk");
        AgentGit.Host(r.Picked, [.. Id, "cherry-pick", "-x", picked]);

        var tidy = await f.Repos.GitTidyPlanAsync(r.Ada.TeamId, "Alpha", Ct);
        Assert.Equal(TidyClasses.Contained, Branch(tidy, "feature/picked").Class);

        var plan = await f.Repos.GitDeleteBranchesAsync(r.Ada.TeamId, "Alpha", ["Manager", "Ada", "Ben"], "main", ["feature/picked"], true, [], null, Ct);
        Assert.False(plan.Empty);
        Assert.Contains($"delete feature/picked at {picked} (contained in base: git cherry trunk", plan.Plan);
        Assert.Contains($"delete origin/feature/picked at {picked} (contained in base", plan.Plan);
        Assert.DoesNotContain("KEPT", plan.Plan);

        var done = await f.Repos.GitDeleteBranchesAsync(r.Ada.TeamId, "Alpha", ["Manager", "Ada", "Ben"], "main", ["feature/picked"], true, [], plan.Token, Ct);
        Assert.Contains($"deleted feature/picked; was {picked}", done.Result);
        Assert.Contains($"deleted origin/feature/picked; was {picked}", done.Result);
        Assert.Null(Rev(r.Picked, "refs/heads/feature/picked"));
        Assert.Null(Rev(r.Bare, "refs/heads/feature/picked"));

        // Own work is still kept without unmerged_ok.
        AgentGit.Host(r.Picked, "switch", "-q", "-c", "feature/own");
        PersonCommit(r.Picked, "own.txt", "own");
        AgentGit.Host(r.Picked, "switch", "-q", "trunk");
        var kept = await f.Repos.GitDeleteBranchesAsync(r.Ada.TeamId, "Alpha", ["Manager", "Ada", "Ben"], null, ["feature/own"], false, [], null, Ct);
        Assert.True(kept.Empty);
        Assert.Contains("KEPT, not merged", kept.Plan);
    }

    [Fact]
    public async Task Realign_IsRefusedWhileTheMemberRuns_AndChangesNothing()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var adaWork = await AdaWorkThenResetAsync(r);

        var refused = await Assert.ThrowsAsync<TeamGitRefusal>(() =>
            f.Repos.GitBranchRealignAsync(r.Ada.TeamId, "Alpha", "main", "Ada", _ => Task.FromResult<string?>("run 7"), ct: Ct));
        Assert.Equal("running", refused.Code);
        Assert.Contains("Ada is running", refused.Message);
        Assert.Equal(adaWork, Rev(r.Picked, "refs/heads/members/alpha/ada"));
        Assert.Equal(adaWork, Rev(r.Bare, "refs/heads/members/alpha/ada"));
        Assert.False(File.Exists(Path.Combine(r.Picked, ".git", TeamRepos.RealignLog)));
    }

    [Fact]
    public async Task Realign_MovesTheTeamsAndOriginsCopyToTheClone_AndRecordsTheOldCommit()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var adaWork = await AdaWorkThenResetAsync(r);

        // The plan only says what would happen.
        var plan = await f.Repos.GitBranchRealignAsync(r.Ada.TeamId, "Alpha", null, "Ada", _ => Task.FromResult<string?>(null), planOnly: true, ct: Ct);
        Assert.True(plan.Planned);
        Assert.Equal(adaWork, plan.OldSha);
        Assert.Equal(r.Trunk, plan.NewSha);
        Assert.Equal(adaWork, Rev(r.Picked, "refs/heads/members/alpha/ada"));

        // Stale: the person saw other commits.
        var stale = await Assert.ThrowsAsync<TeamGitRefusal>(() =>
            f.Repos.GitBranchRealignAsync(r.Ada.TeamId, "Alpha", "main", "Ada", null, expectOld: r.Trunk, expectNew: r.Trunk, ct: Ct));
        Assert.Equal("stale", stale.Code);

        var done = await f.Repos.GitBranchRealignAsync(r.Ada.TeamId, "Alpha", "main", "Ada", _ => Task.FromResult<string?>(null), expectOld: adaWork, expectNew: r.Trunk, ct: Ct);
        Assert.True(done.Changed);
        Assert.True(done.OriginUpdated);
        Assert.Equal(adaWork, done.OldSha);
        Assert.Equal(r.Trunk, done.NewSha);
        Assert.Equal(adaWork, done.OriginOldSha);
        Assert.Equal(r.Trunk, Rev(r.Picked, "refs/heads/members/alpha/ada"));
        Assert.Equal(r.Trunk, Rev(r.Bare, "refs/heads/members/alpha/ada"));
        Assert.Contains($"restore with: git update-ref refs/heads/members/alpha/ada {adaWork}", done.Message);
        var log = File.ReadAllText(Path.Combine(r.Picked, ".git", TeamRepos.RealignLog));
        Assert.Contains($"realigned members/alpha/ada {adaWork} -> {r.Trunk}", log);
        Assert.Contains($"origin {adaWork} -> {r.Trunk}", log);
        Assert.Null(Rev(r.Picked, TeamRepos.RealignStagingPrefix + "members/alpha/ada"));

        // Now the plan calls it Ada's working branch, and a second realign has nothing to do.
        Assert.Equal(TidyClasses.Working, Branch(await f.Repos.GitTidyPlanAsync(r.Ada.TeamId, "Alpha", Ct), "members/alpha/ada").Class);
        var again = await f.Repos.GitBranchRealignAsync(r.Ada.TeamId, "Alpha", null, "Ada", null, ct: Ct);
        Assert.False(again.Changed);
        Assert.Contains("already", again.Message);
    }

    [Fact]
    public async Task Publish_FastForwardsAnOriginMemberBranchBehindTheTeamsCopy_ButNeverForces()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var adaWork = CloneCommit(r.AdaClone, "ada.txt");
        Assert.True((await f.Repos.FetchBackAsync(r.Ada, Ct)).Fetched);
        Assert.Contains("members/alpha/ada", (await f.Repos.PublishAsync(r.Ada, Ct)).Branches);

        // Her work is merged into trunk with one more commit, and trunk reaches origin; the team's copy of her branch is
        // brought up to trunk (as a sync does). Every commit is on origin already, but origin's members/alpha/ada is behind.
        AgentGit.Host(r.Picked, [.. Id, "merge", "-q", "--ff-only", adaWork]);
        var next = PersonCommit(r.Picked, "next.txt", "next");
        AgentGit.Host(r.Picked, "push", "-q", "origin", "trunk");
        AgentGit.Host(r.Picked, "update-ref", "refs/heads/members/alpha/ada", next, adaWork);

        var report = await f.Repos.PublishAsync(r.Ada, Ct);
        Assert.Contains("members/alpha/ada", report.Branches);
        Assert.Equal(next, Rev(r.Bare, "refs/heads/members/alpha/ada"));

        // A team copy that does not contain origin's (diverged) is not pushed: that is Realign's, with the person's yes.
        var tree = AgentGit.Host(r.Picked, "rev-parse", "trunk^{tree}");
        var other = AgentGit.Host(r.Picked, [.. Id, "commit-tree", tree, "-p", r.Trunk, "-m", "other"]);
        AgentGit.Host(r.Picked, "push", "-q", "origin", other + ":refs/heads/members/alpha/ben");
        AgentGit.Host(r.Picked, "fetch", "-q", "origin");
        AgentGit.Host(r.Picked, "branch", "members/alpha/ben", next);
        var second = await f.Repos.PublishAsync(r.Ada, Ct);
        Assert.DoesNotContain("members/alpha/ben", second.Published ?? []);
        Assert.Equal(other, Rev(r.Bare, "refs/heads/members/alpha/ben"));
    }
}
