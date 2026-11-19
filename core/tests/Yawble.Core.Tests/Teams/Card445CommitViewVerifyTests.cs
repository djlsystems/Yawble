using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Card 445 (B072 verify, TesterKofi): what CommitViewTests (card 443) does not cover, on a fixture repository whose base
/// is "main" and whose origin is a bare folder whose branches are moved as a push would (see Push):
///  - pushed vs not pushed, the bare origin's branch moved too; main never in where.branches; the team branch first even though
///    "members/..." sorts before "team/...";
///  - the where facts behind the wording: team branch only, member branch only, pushed while not in main;
///  - a merge nobody can name: intoBranch / fromBranch are JSON null (the shell must not print "null"), made absent;
///  - F1: a merge made on main with git's default message ("Merge branch 'x'", git leaves out "into main") is into main
///    even when the team branch also has it (round 1 found "team/alpha"; fixed in f669165, checked in card 460);
///  - F2 (core side of a shell wording fault): a commit on a branch that is neither the team's nor a member's (the
///    person's own feature branch) has where.branches empty and where.otherBranches ["feature/x"] (round 2), so the view
///    can tell it from a deleted branch's commit (both empty);
///  - the core reads the team's repository, never an agent's clone (a commit only in Ada's clone is "not found");
///  - a file name with spaces and non-ASCII, deleted, opens its diff;
///  - M1: a branch whose name is 4+ hex digits is not read as a commit ID (round 1 found it was; fixed in f669165).
/// With CARD445_FIXTURE_OUT set, the real core's JSON for each case is written there (shell/tests/unit/fixtures/card445)
/// for the shell's card445 test to render.
/// </summary>
public class Card445CommitViewVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Commit(string repo, string message, string author = "Ada")
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = author, ["GIT_AUTHOR_EMAIL"] = author.ToLowerInvariant() + "@example.invalid",
            ["GIT_COMMITTER_NAME"] = author, ["GIT_COMMITTER_EMAIL"] = author.ToLowerInvariant() + "@example.invalid",
        };
        AgentGit.HostWithEnv(repo, env, "add", "-A");
        AgentGit.HostWithEnv(repo, env, "-c", "commit.gpgSign=false", "commit", "-q", "--allow-empty", "-m", message);
        return AgentGit.Host(repo, "rev-parse", "HEAD");
    }

    private static string Merge(string repo, string from, string? message)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Person", ["GIT_AUTHOR_EMAIL"] = "p@example.invalid", ["GIT_COMMITTER_NAME"] = "Person", ["GIT_COMMITTER_EMAIL"] = "p@example.invalid",
        };
        if (message is null) AgentGit.HostWithEnv(repo, env, "-c", "commit.gpgSign=false", "merge", "-q", "--no-ff", "--no-edit", from);
        else AgentGit.HostWithEnv(repo, env, "-c", "commit.gpgSign=false", "merge", "-q", "--no-ff", "-m", message, from);
        return AgentGit.Host(repo, "rev-parse", "HEAD");
    }

    private static void Write(string repo, string file, string text)
    {
        var full = Path.Combine(repo, file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private sealed record Rig(TeamsFixture F, long TeamId, string Repo, string Root, Yawble.Core.Bus.MemberRef Ada);

    /// <summary>Team Alpha on the person's repository, base main, origin a bare folder that main was pushed to.</summary>
    private static async Task<Rig> RigAsync(TeamsFixture f)
    {
        var (_, ada, _) = f.Team();
        var repo = Path.Combine(f.Db.Dir, "person-repo");
        Directory.CreateDirectory(repo);
        AgentGit.Host(repo, "init", "-q", "-b", "main");
        Write(repo, "README.md", "one\ntwo\n");
        var root = Commit(repo, "Start", "Person");
        var bare = Path.Combine(f.Db.Dir, "origin.git");
        AgentGit.Host(f.Db.Dir, "init", "-q", "--bare", bare);
        AgentGit.Host(repo, "remote", "add", "origin", bare);
        Push(repo, "main");
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", repo, Ct);
        return new Rig(f, ada.TeamId, repo, root, ada);
    }

    /// <summary>
    /// A push to the bare origin, done as git push leaves it: the bare's branch moved to the commit (its objects reached
    /// through alternates) and the repository's refs/remotes/origin/&lt;branch&gt; updated. git push itself cannot run in
    /// the agent sandbox (spawning receive-pack fails: "Could not read from remote repository").
    /// </summary>
    private static void Push(string repo, string branch)
    {
        var bare = AgentGit.Host(repo, "remote", "get-url", "origin");
        var alternates = Path.Combine(bare, "objects", "info", "alternates");
        Directory.CreateDirectory(Path.GetDirectoryName(alternates)!);
        File.WriteAllText(alternates, Path.Combine(repo, ".git", "objects").Replace('\\', '/') + "\n");
        var sha = AgentGit.Host(repo, "rev-parse", "refs/heads/" + branch);
        AgentGit.Host(bare, "update-ref", "refs/heads/" + branch, sha);
        AgentGit.Host(repo, "update-ref", "refs/remotes/origin/" + branch, sha);
        Assert.Equal(sha, AgentGit.Host(bare, "rev-parse", "refs/heads/" + branch));
    }

    private static Task<CommitInfo> Show(Rig r, string sha) => r.F.Repos.GitCommitShowAsync(r.TeamId, "Alpha", sha, Ct);

    /// <summary>Writes the real core's JSON for the shell's card445 test, when CARD445_FIXTURE_OUT names a folder.</summary>
    private static void Emit(string name, JsonObject json)
    {
        var dir = Environment.GetEnvironmentVariable("CARD445_FIXTURE_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    [Fact]
    public async Task PushedOnlyAfterAPush_MainIsNeverListed_AndTheTeamBranchComesFirst()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        Write(r.Repo, "README.md", "one\nTWO\n");
        var local = Commit(r.Repo, "Local only", "Person");
        AgentGit.Host(r.Repo, "branch", "-f", "team/alpha", "main");
        AgentGit.Host(r.Repo, "branch", "-f", "members/alpha/ada", "main");

        var before = await Show(r, local);
        Assert.True(before.Where!.InMain);
        Assert.False(before.Where.Pushed);
        // "members/..." sorts before "team/...": the team branch is still first, and main is never listed.
        Assert.Equal(["team/alpha", "members/alpha/ada"], before.Where.Branches);
        Assert.DoesNotContain("main", before.Where.Branches);
        Emit("in-main-not-pushed", before.ToJson());

        Push(r.Repo, "main");
        var after = await Show(r, local);
        Assert.True(after.Where!.Pushed);
        Assert.True(after.Where.InMain);
        Emit("in-main-pushed", after.ToJson());

        Assert.True((await Show(r, r.Root)).Where!.Pushed);
    }

    [Fact]
    public async Task TheWhereFacts_TeamBranchOnly_MemberBranchOnly_AndPushedBeforeMain()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha", "main");
        Write(r.Repo, "team.txt", "team\n");
        var teamOnly = Commit(r.Repo, "Team work");
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "members/alpha/ada", "team/alpha");
        Write(r.Repo, "ada.txt", "ada\n");
        var adaOnly = Commit(r.Repo, "Ada's work");
        AgentGit.Host(r.Repo, "switch", "-q", "main");

        var t = await Show(r, teamOnly);
        Assert.False(t.Where!.InMain);
        Assert.Equal(["team/alpha", "members/alpha/ada"], t.Where.Branches);
        Assert.Null(t.Where.OnlyOnMember);
        Assert.False(t.Where.Pushed);
        Emit("team-branch-only", t.ToJson());

        var a = await Show(r, adaOnly);
        Assert.False(a.Where!.InMain);
        Assert.Equal(["members/alpha/ada"], a.Where.Branches);
        Assert.Equal("Ada", a.Where.OnlyOnMember);
        // Not recorded by the app, but only Ada's branch has it.
        Assert.Equal("Ada", a.Made!.Member);
        Emit("member-branch-only", a.ToJson());

        Push(r.Repo, "team/alpha");
        var pushed = await Show(r, teamOnly);
        Assert.True(pushed.Where!.Pushed);
        Assert.False(pushed.Where.InMain);
        Emit("team-branch-only-pushed", pushed.ToJson());
    }

    [Fact]
    public async Task AMergeNobodyCanName_HasNullBranches_AndNoMade_InTheJson()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "side", "main");
        Write(r.Repo, "side.txt", "side\n");
        Commit(r.Repo, "Side work", "Person");
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "tmp", "main");
        var merge = Merge(r.Repo, "side", "Integrate the work");
        AgentGit.Host(r.Repo, "switch", "-q", "main");
        AgentGit.Host(r.Repo, "branch", "-q", "-D", "tmp", "side");

        var m = await Show(r, merge);
        Assert.True(m.Found);
        Assert.True(m.IsMerge);
        Assert.Equal(new CommitMerge(null, null, null, 1), m.Merge);
        Assert.Null(m.Made);
        Assert.Empty(m.Where!.Branches);
        var json = m.ToJson();
        Assert.True(json["merge"]!.AsObject().ContainsKey("intoBranch"));
        Assert.Null(json["merge"]!["intoBranch"]);
        Assert.Null(json["merge"]!["fromBranch"]);
        Assert.False(json.ContainsKey("made"));
        Emit("merge-unnamed", json);
    }

    /// <summary>
    /// F1. git's own message for a merge into main is "Merge branch 'feature/login'" (no "into main"). Round 1 (954cde7)
    /// found the core fell back to the team branch first, so the merge read "into team/alpha" once the team branch had
    /// main. Round 2 (card 460, fix f669165): into is the branch whose first-parent line holds the merge, so it is main.
    /// </summary>
    [Fact]
    public async Task F1_AMergeMadeOnMain_WithGitsDefaultMessage_IsIntoMain_EvenWhenTheTeamBranchHasIt()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/login", "main");
        Write(r.Repo, "login.txt", "login\n");
        Commit(r.Repo, "Login", "Person");
        AgentGit.Host(r.Repo, "switch", "-q", "main");
        var merge = Merge(r.Repo, "feature/login", null);
        Assert.Equal("Merge branch 'feature/login'", AgentGit.Host(r.Repo, "log", "-1", "--format=%s", merge));

        // Before the team branch has it: into main, right.
        Assert.Equal("main", (await Show(r, merge)).Merge!.IntoBranch);

        AgentGit.Host(r.Repo, "branch", "-f", "team/alpha", "main");
        var m = await Show(r, merge);
        Assert.Equal("feature/login", m.Merge!.FromBranch);
        Assert.Equal("main", m.Merge.IntoBranch); // F1 fixed (round 1 said "team/alpha")
        Emit("f1-merge-on-main", m.ToJson());

        // The team branch moved past main by its own commit still has the merge only by containing it: still main.
        AgentGit.Host(r.Repo, "switch", "-q", "team/alpha");
        Commit(r.Repo, "Team work after");
        AgentGit.Host(r.Repo, "switch", "-q", "main");
        Assert.Equal("main", (await Show(r, merge)).Merge!.IntoBranch);
    }

    /// <summary>F1, the other way: a merge made on the team branch (git's default message) is into the team branch.</summary>
    [Fact]
    public async Task F1_AMergeMadeOnTheTeamBranch_WithGitsDefaultMessage_IsIntoTheTeamBranch()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "members/alpha/ada", "main");
        Write(r.Repo, "ada.txt", "ada\n");
        Commit(r.Repo, "Ada's work");
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha", "main");
        var merge = Merge(r.Repo, "members/alpha/ada", null);
        AgentGit.Host(r.Repo, "switch", "-q", "main");
        var m = await Show(r, merge);
        Assert.Equal("team/alpha", m.Merge!.IntoBranch);
        Assert.Equal("members/alpha/ada", m.Merge.FromBranch);
    }

    /// <summary>
    /// F2 (core side). A commit on the person's own branch (neither team/ nor members/) has where.branches empty, as
    /// before; round 2 (fix f669165): where.otherBranches lists feature/x, so the shell can tell it from a deleted branch,
    /// whose commit has otherBranches empty. otherBranches is always in the JSON, sorted, never base/team/members.
    /// </summary>
    [Fact]
    public async Task F2_ACommitOnThePersonsOwnBranch_IsInOtherBranches_ADeletedBranchsIsNot()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/x", "main");
        Write(r.Repo, "x.txt", "x\n");
        var x = Commit(r.Repo, "Feature x", "Person");
        AgentGit.Host(r.Repo, "switch", "-q", "main");
        Assert.Contains("feature/x", AgentGit.Host(r.Repo, "branch", "--contains", x));

        var c = await Show(r, x);
        Assert.True(c.Found);
        Assert.False(c.Where!.InMain);
        Assert.Empty(c.Where.Branches);
        Assert.Null(c.Where.OnlyOnMember);
        Assert.Equal(["feature/x"], c.Where.OtherBranches);
        var cj = c.ToJson();
        Assert.Equal(["feature/x"], cj["where"]!["otherBranches"]!.AsArray().Select(n => n!.GetValue<string>()));
        Emit("f2-own-branch", cj);

        // A deleted member branch's commit: branches and otherBranches both empty (and otherBranches still present).
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "members/alpha/ben", "main");
        Write(r.Repo, "ben.txt", "ben\n");
        var ben = Commit(r.Repo, "Ben's", "Ben");
        AgentGit.Host(r.Repo, "switch", "-q", "main");
        AgentGit.Host(r.Repo, "branch", "-q", "-D", "members/alpha/ben");
        var gone = await Show(r, ben);
        Assert.Empty(gone.Where!.Branches);
        var gj = gone.ToJson();
        Assert.True(gj["where"]!.AsObject().ContainsKey("otherBranches"));
        Assert.Empty(gj["where"]!["otherBranches"]!.AsArray());
        Emit("deleted-branch", gj);

        // Several other branches, sorted; base, team and member branches that hold it are never in otherBranches.
        AgentGit.Host(r.Repo, "branch", "zeta", x);
        AgentGit.Host(r.Repo, "branch", "alpha-topic", x);
        AgentGit.Host(r.Repo, "branch", "team/alpha", x);
        AgentGit.Host(r.Repo, "branch", "members/alpha/ada", x);
        var many = await Show(r, x);
        Assert.Equal(["alpha-topic", "feature/x", "zeta"], many.Where!.OtherBranches);
        Assert.Equal(["team/alpha", "members/alpha/ada"], many.Where.Branches);
        Assert.DoesNotContain("main", many.Where.OtherBranches!);
    }

    [Fact]
    public async Task TheCoreReadsTheTeamRepository_NeverAnAgentsClone()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        // Ada's clone where the app keeps it (CheckoutOf). PrepareAsync would clone it, but a local clone/fetch cannot run
        // in the agent sandbox, so it is made by hand: a repository on Ada's branch whose objects start as the team's.
        var checkout = f.Repos.CheckoutOf(r.Ada, f.Repos.RepoOf(r.TeamId)!);
        Assert.NotEqual(Path.GetFullPath(r.Repo), Path.GetFullPath(checkout.ClonePath));
        Directory.CreateDirectory(checkout.ClonePath);
        AgentGit.Host(checkout.ClonePath, "init", "-q", "-b", checkout.Branch);
        File.WriteAllText(Path.Combine(checkout.ClonePath, ".git", "objects", "info", "alternates"), Path.Combine(r.Repo, ".git", "objects").Replace('\\', '/') + "\n");
        AgentGit.Host(checkout.ClonePath, "reset", "-q", "--hard", r.Root);
        Write(checkout.ClonePath, "clone-only.txt", "only in Ada's clone\n");
        var inClone = Commit(checkout.ClonePath, "Only in the clone");

        var c = await Show(r, inClone);
        Assert.False(c.Found);
        Assert.StartsWith($"git has no commit {inClone[..12]} in the team's repository.", c.GoneReason);
        var json = c.ToJson();
        Assert.Equal(["found", "goneReason", "sha"], json.Select(p => p.Key).Order());
        Emit("not-found", json);
        var refused = await Assert.ThrowsAsync<TeamGitRefusal>(() => f.Repos.GitCommitDiffAsync(r.TeamId, "Alpha", inClone, "clone-only.txt", Ct));
        Assert.Equal("gone", refused.Code);

        // The team repository's own commit is found, from the team repository.
        Assert.True((await Show(r, r.Root)).Found);
    }

    [Fact]
    public async Task AFileWithSpacesAndNonAscii_DeletedAndAdded_OpensItsDiff()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        const string name = "docs/my notes Ã©.txt";
        Write(r.Repo, name, "a\nb\n");
        var add = Commit(r.Repo, "Notes", "Person");
        File.Delete(Path.Combine(r.Repo, name));
        var del = Commit(r.Repo, "No notes", "Person");

        var a = await Show(r, add);
        Assert.Equal([new CommitFileChange(name, null, "added", 2, 0, false)], a.Files);
        var d = await Show(r, del);
        Assert.Equal([new CommitFileChange(name, null, "deleted", 0, 2, false)], d.Files);
        var diff = await f.Repos.GitCommitDiffAsync(r.TeamId, "Alpha", del, name, Ct);
        var h = Assert.Single(diff.Hunks);
        Assert.Equal(["a", "b"], h.Lines.Select(l => l.Text));
        Assert.All(h.Lines, l => Assert.Equal("deleted", l.Kind));
    }

    /// <summary>
    /// M1: 4+ hex digits that are also a branch's name. Round 1 found them read as that branch (git prefers the ref);
    /// round 2 (fix f669165): IDs resolve only as objects, so "cafe" is not found and its diff is "gone".
    /// </summary>
    [Fact]
    public async Task Minor_AHexBranchName_IsNotFound_AndItsDiffIsGone()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        Write(r.Repo, "z.txt", "z\n");
        var tip = Commit(r.Repo, "Tip", "Person");
        AgentGit.Host(r.Repo, "branch", "cafe", r.Root);
        AgentGit.Host(r.Repo, "tag", "beef", r.Root);
        var objects = AgentGit.Host(r.Repo, "rev-list", "--all", "--objects");
        Assert.SkipWhen(objects.Split('\n').Any(l => l.StartsWith("cafe", StringComparison.Ordinal) || l.StartsWith("beef", StringComparison.Ordinal)),
            "an object ID starts with cafe or beef in this run");

        var c = await Show(r, "cafe");
        Assert.False(c.Found);
        Assert.Equal("cafe", c.Sha);
        var refused = await Assert.ThrowsAsync<TeamGitRefusal>(() => f.Repos.GitCommitDiffAsync(r.TeamId, "Alpha", "cafe", "README.md", Ct));
        Assert.Equal("gone", refused.Code);
        Assert.False((await Show(r, "beef")).Found); // a tag named like an ID is not read either

        // The real commit IDs still resolve, by a 4-digit prefix too.
        var byPrefix = await Show(r, tip[..7]);
        Assert.True(byPrefix.Found);
        Assert.Equal(tip, byPrefix.Sha);
    }
}
