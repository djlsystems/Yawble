using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Link;
using Yawble.Core.Monitor;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The commits behind each count and the previews before Merge to main, Delete and Sync. Fixture
/// repositories built here (base trunk); origin, where there is one, is a bare folder whose refs are set as a fetch would
/// leave them (no transport, no network). Covers: the merge preview's fast-forward, merge commit and conflict text (and
/// that nothing moved); the delete preview of a merged, a cherry-picked and a unique branch; range lists equal to the
/// counts; Sync's incoming commits; the one not-pushed flag; the deleted branch's commit still opening in the commit view;
/// the Concierge's text equal to the dialog's; the RPC shapes through the real dispatcher. Also: a fast-forward from
/// origin's base when the local one is only behind it; Merge to main making exactly the tree the preview diffed; a
/// conflict and a refusal changing nothing (refs, index, working tree, no MERGE_HEAD); a branch origin still has, one
/// half pushed and one with a merge commit; every count opening exactly its commits; teams.syncToBase's behind range.
/// With GIT_PREVIEWS_FIXTURE_OUT set, the core's JSON (as UiRpc sends it) is written to that folder
/// (shell/tests/unit/fixtures/git-previews) for the shell's tests to render.
/// </summary>
public class MergeDeletePreviewTests
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

    private static void Write(string repo, string file, string text) => File.WriteAllText(Path.Combine(repo, file), text);

    private static string Rev(string repo, string reference) => AgentGit.Host(repo, "rev-parse", reference);

    private sealed record Rig(TeamsFixture F, long TeamId, string Repo, string Root);

    /// <summary>Team Alpha on the person's repository, base trunk, one root commit (README.md, a.txt).</summary>
    private static async Task<Rig> RigAsync(TeamsFixture f)
    {
        var (_, ada, _) = f.Team();
        var repo = Path.Combine(f.Db.Dir, "person-repo");
        Directory.CreateDirectory(repo);
        AgentGit.Host(repo, "init", "-q", "-b", "trunk");
        // The merge's dirty check runs the person's git without the system config: keep both gits on the same line endings.
        AgentGit.Host(repo, "config", "core.autocrlf", "false");
        Write(repo, "README.md", "readme\n");
        Write(repo, "a.txt", "one\ntwo\nthree\n");
        var root = Commit(repo, "Start", author: "Person");
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", repo, Ct);
        return new Rig(f, ada.TeamId, repo, root);
    }

    private static void AddOrigin(Rig r, params (string Branch, string Sha)[] refs)
    {
        var bare = Path.Combine(r.F.Db.Dir, "origin.git");
        AgentGit.Host(r.F.Db.Dir, "init", "-q", "--bare", bare);
        AgentGit.Host(r.Repo, "remote", "add", "origin", bare);
        foreach (var (b, sha) in refs) AgentGit.Host(r.Repo, "update-ref", "refs/remotes/origin/" + b, sha);
    }

    /// <summary>Every ref, the index tree, the working tree status and MERGE_HEAD: what "nothing changes" means.</summary>
    private static string Snapshot(string repo) =>
        AgentGit.Host(repo, "for-each-ref", "--format=%(refname) %(objectname)") + "\n#index " + AgentGit.Host(repo, "write-tree")
        + "\n#status " + AgentGit.Host(repo, "status", "--porcelain") + "\n#head " + AgentGit.Host(repo, "symbolic-ref", "-q", "HEAD")
        + "\n#merge " + File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD"));

    private static JsonObject S<T>(T value) => JsonSerializer.SerializeToNode(value, RpcDispatcher.Json)!.AsObject();

    /// <summary>git.mergePreview as UiRpc sends it (files through CommitFileChange.ToJson).</summary>
    private static JsonObject MergeJson(MergePreview p)
    {
        var o = S(p);
        o["files"] = new JsonArray([.. p.Files.Select(f => (JsonNode)f.ToJson())]);
        return o;
    }

    private static void Emit(string name, JsonObject json)
    {
        var dir = Environment.GetEnvironmentVariable("GIT_PREVIEWS_FIXTURE_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>Each row opens the commit view, and its notPushed is the commit view's !where.pushed (one flag).</summary>
    private static async Task EachRowOpens(Rig r, IEnumerable<CommitRow> rows)
    {
        foreach (var c in rows)
        {
            var shown = await r.F.Repos.GitCommitShowAsync(r.TeamId, "Alpha", c.Sha, Ct);
            Assert.True(shown.Found, c.Sha);
            Assert.Equal(c.Sha, shown.Sha);
            Assert.Equal(c.ShortSha, shown.ShortSha);
            Assert.Equal(!c.NotPushed, shown.Where!.Pushed);
        }
    }

    private async Task<string> ConciergeMergeText(Rig r)
    {
        await using var bus = new BusEngine(r.F.Db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false, Repos = r.F.Repos });
        await bus.StartAsync(Ct);
        return (await new ConciergeReadTools(bus, (_, _, _) => Task.FromResult<JsonNode?>(null), r.F.Db.ConnectionString, remote: false)
            .CallAsync("git_merge_preview", new JsonObject { ["team"] = "Alpha" }, Ct))!;
    }

    // ------------------------------------------------------------------ Merge to main preview

    [Fact]
    public async Task MergePreview_FastForward_SaysWhereMainMoves_ListsWhatLands_AndTheNotPushedFlag()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha");
        Write(r.Repo, "b.txt", "bee\n");
        var first = Commit(r.Repo, "Add b");
        Write(r.Repo, "a.txt", "one\nTWO\nthree\n");
        var second = Commit(r.Repo, "Louder two");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        // origin has trunk at the root and the team branch at its first commit (as of the last fetch).
        AddOrigin(r, ("trunk", r.Root), ("team/alpha", first));

        var p = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);

        Assert.Equal("fast-forward", p.Outcome);
        Assert.Equal($"trunk will fast-forward to {second[..12]}: 2 commits land.", p.Summary);
        Assert.Equal((r.Root, second, second), (p.From, p.To, p.NewSha));
        Assert.Equal([second, first], p.Commits.Select(c => c.Sha));
        Assert.Equal(2, p.Count);
        Assert.False(p.Truncated);
        Assert.True(p.HasOrigin);
        // The one flag: origin's team/alpha has the first commit, not the second.
        Assert.Equal([true, false], p.Commits.Select(c => c.NotPushed));
        Assert.Equal(new BranchPush("team/alpha", true, 1), p.TeamPush);
        Assert.Equal(["a.txt:modified:1:1", "b.txt:added:1:0"], p.Files.Select(x => $"{x.Path}:{x.Status}:{x.Added}:{x.Deleted}").Order());
        Assert.Equal((r.Root, second), (p.DiffFrom, p.DiffTo));
        Assert.StartsWith(p.Summary + "\n2 commits will land on trunk:\n", p.Text);
        Assert.Contains($"  - {second[..12]} Louder two (Ada), not pushed", p.Text);
        Assert.Contains($"  - {first[..12]} Add b (Ada)\n", p.Text);
        Assert.Contains("  modified a.txt (+1 -1)", p.Text);

        var diff = await f.Repos.GitMergeDiffAsync(r.TeamId, "Alpha", p.DiffFrom!, p.DiffTo!, "a.txt", Ct);
        Assert.Contains(diff.Hunks.SelectMany(h => h.Lines), l => l.Kind == CommitDiffLine.Added && l.Text == "TWO");
        await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.GitMergeDiffAsync(r.TeamId, "Alpha", "HEAD", p.DiffTo!, "a.txt", Ct));
        Assert.Equal("no_such_file", (await Assert.ThrowsAsync<TeamGitRefusal>(() => f.Repos.GitMergeDiffAsync(r.TeamId, "Alpha", p.DiffFrom!, p.DiffTo!, "README.md", Ct))).Code);

        // Nothing moved; and Merge to main itself lands exactly the previewed commits.
        Assert.Equal(r.Root, Rev(r.Repo, "refs/heads/trunk"));
        AgentGit.Host(r.Repo, "remote", "remove", "origin");
        AgentGit.Host(r.Repo, "switch", "-q", "--detach");
        var landed = await f.Repos.GitMergeTeamToBaseAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("fast-forward", landed.Outcome);
        Assert.Equal([second, first], landed.Commits!.Select(c => c.Sha));
        Assert.Equal(new CommitRangeRef(r.Root, second), landed.Range);
        Assert.Equal("up-to-date", (await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct)).Outcome);
    }

    [Fact]
    public async Task MergePreview_BaseCheckedOutWithUncommittedChanges_RefusesWithMergesSentence()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha");
        Write(r.Repo, "t.txt", "t\n");
        var team = Commit(r.Repo, "Team");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        Write(r.Repo, "a.txt", "one\nedited, not committed\nthree\n");
        var before = Snapshot(r.Repo);
        await using var bus = new BusEngine(f.Db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false, Repos = f.Repos });

        var p = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        var said = await new ConciergeReadTools(bus, (_, _, _) => Task.FromResult<JsonNode?>(null), f.Db.ConnectionString, remote: false)
            .CallAsync("git_merge_preview", new JsonObject { ["team"] = "Alpha" }, Ct);
        var done = await f.Repos.GitMergeTeamToBaseAsync(r.TeamId, "Alpha", Ct);

        Assert.Equal("refused", done.Outcome);
        Assert.Equal("refused", p.Outcome);
        Assert.Equal(done.Message, p.Summary);
        Assert.Equal(done.Message, p.Text);
        Assert.Equal(p.Text, said);
        Assert.StartsWith($"trunk is checked out in {r.Repo} with uncommitted changes.", p.Text);
        Assert.Null(p.NewSha);
        Assert.Empty(p.Commits);
        Assert.Equal(new BranchPush("team/alpha", false, 2), p.TeamPush);
        Assert.Equal(r.Root, Rev(r.Repo, "refs/heads/trunk"));
        Assert.Equal("one\nedited, not committed\nthree\n", File.ReadAllText(Path.Combine(r.Repo, "a.txt")));
        Assert.Equal(before, Snapshot(r.Repo));
        Emit("f1-merge-dirty", MergeJson(p));

        // Clean again: the preview and Merge agree on fast-forward. An untracked file never blocks (the merge ignores those too).
        AgentGit.Host(r.Repo, "checkout", "--", "a.txt");
        Write(r.Repo, "scratch.txt", "untracked\n");
        Assert.Equal("fast-forward", (await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct)).Outcome);
        Assert.Equal("fast-forward", (await f.Repos.GitMergeTeamToBaseAsync(r.TeamId, "Alpha", Ct)).Outcome);
        Assert.Equal(team, Rev(r.Repo, "refs/heads/trunk"));
    }

    [Fact]
    public async Task MergePreview_MergeCommit_AndConflict_SayItInWords_AndChangeNothing()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        Assert.Equal("There is no team/alpha yet: nothing to merge.", (await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct)).Summary);

        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha");
        Write(r.Repo, "team.txt", "team\n");
        var team = Commit(r.Repo, "Team work");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        Write(r.Repo, "README.md", "readme, by the person\n");
        var trunk = Commit(r.Repo, "Person's own", author: "Person");

        var merge = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("merge", merge.Outcome);
        Assert.Equal("A merge commit will be made: team/alpha brings 1 commit, and trunk has 1 commit team/alpha lacks.", merge.Summary);
        Assert.Null(merge.NewSha);
        Assert.Equal([team], merge.Commits.Select(c => c.Sha));
        Assert.True(merge.Commits[0].NotPushed); // no origin: local only
        Assert.False(merge.HasOrigin);
        Assert.Equal(["team.txt"], merge.Files.Select(x => x.Path)); // the combined diff: what lands on trunk
        Assert.Equal(trunk, merge.DiffFrom);
        Assert.Equal("tree", AgentGit.Host(r.Repo, "cat-file", "-t", merge.DiffTo!));
        Assert.Contains("  - " + team[..12] + " Team work (Ada), local only", merge.Text);

        // Both sides change a.txt's second line: a conflict.
        AgentGit.Host(r.Repo, "switch", "-q", "team/alpha");
        Write(r.Repo, "a.txt", "one\nteam two\nthree\n");
        var teamTip = Commit(r.Repo, "Team two");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        Write(r.Repo, "a.txt", "one\nperson two\nthree\n");
        var trunkTip = Commit(r.Repo, "Person two", author: "Person");

        var conflict = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("conflict", conflict.Outcome);
        Assert.Equal("Merging team/alpha would conflict in a.txt: nothing changes.", conflict.Summary);
        Assert.Equal(["a.txt"], conflict.ConflictFiles);
        Assert.Equal(2, conflict.Count);
        Assert.Equal(["a.txt", "team.txt"], conflict.Files.Select(x => x.Path).Order());
        Assert.Contains("2 commits on team/alpha not in trunk:", conflict.Text);
        Assert.Equal((trunkTip, teamTip), (Rev(r.Repo, "refs/heads/trunk"), Rev(r.Repo, "refs/heads/team/alpha")));
        Assert.Equal("one\nperson two\nthree\n", File.ReadAllText(Path.Combine(r.Repo, "a.txt")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task FastForward_FromOriginsBase_WhenLocalIsOnlyBehind_PreviewStatusAndRangeAgree()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        // origin's trunk is one commit past the local one (fetched, not yet brought current).
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "tmp");
        Write(r.Repo, "o.txt", "origin\n");
        var o1 = Commit(r.Repo, "On origin", author: "Person");
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha");
        Write(r.Repo, "t1.txt", "1\n");
        var t1 = Commit(r.Repo, "Team 1");
        Write(r.Repo, "t2.txt", "2\n");
        var t2 = Commit(r.Repo, "Team 2");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        AgentGit.Host(r.Repo, "branch", "-q", "-D", "tmp");
        AddOrigin(r, ("trunk", o1), ("team/alpha", t1));

        var s = await f.Repos.GitStatusAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal((0, 1), (s.BaseAheadOfOrigin, s.BaseBehindOrigin));
        Assert.Equal(new CommitRangeRef(r.Root, o1), s.Ranges.BaseBehindOrigin);
        var behind = await f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", s.Ranges.BaseBehindOrigin!.From, s.Ranges.BaseBehindOrigin.To, ct: Ct);
        Assert.Equal([o1], behind.Commits.Select(c => c.Sha));
        Assert.False(behind.Commits[0].NotPushed);

        var p = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("fast-forward", p.Outcome);
        Assert.Equal(o1, p.From); // origin's base, not the local one
        Assert.Equal($"trunk will fast-forward to {t2[..12]}: 2 commits land.", p.Summary);
        Assert.Equal(s.TeamAheadOfBase, p.Count);
        Assert.Equal(new CommitRangeRef(p.From!, p.To!), s.Ranges.TeamAheadOfBase);
        var ahead = await f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", s.Ranges.TeamAheadOfBase!.From, s.Ranges.TeamAheadOfBase.To, ct: Ct);
        Assert.Equal(p.Commits.Select(c => c.Sha), ahead.Commits.Select(c => c.Sha));
        Assert.Equal([t2, t1], p.Commits.Select(c => c.Sha));
        Assert.Equal([true, false], p.Commits.Select(c => c.NotPushed));
        Assert.Equal(new BranchPush("team/alpha", true, 1), p.TeamPush);
        await EachRowOpens(r, p.Commits.Concat(behind.Commits));
        Assert.EndsWith("(origin as of the last fetch; Merge to main fetches first.)", p.Text);
        Assert.Equal(p.Text, await ConciergeMergeText(r));

        Emit("status-ff-origin", S(s));
        Emit("range-base-behind-origin", S(behind));
        Emit("range-team-ahead", S(ahead));
        Emit("merge-ff", MergeJson(p));
    }

    [Fact]
    public async Task MergeCommit_MergeToMainMakesThePreviewedTree_AndLandsThePreviewedCommits()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha");
        Write(r.Repo, "team.txt", "team\n");
        var team1 = Commit(r.Repo, "Team work");
        Write(r.Repo, "a.txt", "one\ntwo\nthree\nfour\n");
        var team2 = Commit(r.Repo, "Four");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        Write(r.Repo, "README.md", "readme, by the person\n");
        var trunk = Commit(r.Repo, "Person's own", author: "Person");

        var p = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("merge", p.Outcome);
        Assert.Equal("A merge commit will be made: team/alpha brings 2 commits, and trunk has 1 commit team/alpha lacks.", p.Summary);
        Assert.Equal([team2, team1], p.Commits.Select(c => c.Sha));
        Assert.All(p.Commits, c => Assert.True(c.NotPushed)); // no origin: every commit local only
        Assert.False(p.HasOrigin);
        Assert.Equal(["a.txt", "team.txt"], p.Files.Select(x => x.Path).Order());
        Assert.Equal(p.Text, await ConciergeMergeText(r));
        await EachRowOpens(r, p.Commits);
        var diff = await f.Repos.GitMergeDiffAsync(r.TeamId, "Alpha", p.DiffFrom!, p.DiffTo!, "a.txt", Ct);
        Assert.Equal(["four"], diff.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == CommitDiffLine.Added).Select(l => l.Text));
        Emit("merge-commit", MergeJson(p));

        AgentGit.Host(r.Repo, "switch", "-q", "--detach");
        var done = await f.Repos.GitMergeTeamToBaseAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("merged", done.Outcome);
        var made = Rev(r.Repo, "refs/heads/trunk");
        Assert.Equal(p.DiffTo, Rev(r.Repo, made + "^{tree}")); // the tree the preview diffed is the one Merge made
        Assert.Equal($"{made} {trunk} {team2}", AgentGit.Host(r.Repo, "log", "-1", "--format=%H %P", made));
        Assert.Equal(p.Commits.Select(c => c.Sha), done.Commits!.Select(c => c.Sha));
        Assert.Equal((2, false), (done.CommitCount, done.CommitsTruncated));
        Assert.Equal(new CommitRangeRef(trunk, team2), done.Range);
        Emit("merge-commit-done", S(done));
    }

    [Fact]
    public async Task Conflict_NothingChanges_AndMergeToMainNamesTheSameFiles()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha");
        Write(r.Repo, "a.txt", "one\nteam two\nthree\n");
        Write(r.Repo, "README.md", "team readme\n");
        Commit(r.Repo, "Team two");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        Write(r.Repo, "a.txt", "one\nperson two\nthree\n");
        Write(r.Repo, "README.md", "person readme\n");
        Commit(r.Repo, "Person two", author: "Person");
        // An uncommitted file the person is editing on another path: kept as it is.
        Write(r.Repo, "notes.txt", "mine\n");
        var before = Snapshot(r.Repo);

        var p = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("conflict", p.Outcome);
        Assert.Equal(["README.md", "a.txt"], p.ConflictFiles.Order(StringComparer.Ordinal));
        Assert.Matches(@"^Merging team/alpha would conflict in (README\.md, a\.txt|a\.txt, README\.md): nothing changes\.$", p.Summary);
        Assert.Null(p.NewSha);
        Assert.Equal(before, Snapshot(r.Repo));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(r.Repo, "notes.txt")));
        Assert.Equal(p.Text, await ConciergeMergeText(r));
        Assert.Contains("1 commit on team/alpha not in trunk:", p.Text);
        Emit("merge-conflict", MergeJson(p));

        var s = await f.Repos.GitStatusAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal(p.ConflictFiles.Order(), s.ConflictFiles.Order());
        var done = await f.Repos.GitMergeTeamToBaseAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("conflict", done.Outcome);
        Assert.Equal(p.ConflictFiles.Order(), done.Files!.Order());
        Assert.Null(done.Commits);
        Assert.Equal(before, Snapshot(r.Repo));
    }

    [Fact]
    public async Task Refused_UpToDate_AndNone_SayItAndTheConciergeSaysTheSame()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var none = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("none", none.Outcome);
        Emit("merge-none", MergeJson(none));

        AgentGit.Host(r.Repo, "branch", "team/alpha", r.Root);
        var upToDate = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal(("up-to-date", "trunk already has everything on team/alpha: nothing to merge."), (upToDate.Outcome, upToDate.Summary));
        Emit("merge-up-to-date", MergeJson(upToDate));

        AgentGit.Host(r.Repo, "switch", "-q", "team/alpha");
        Write(r.Repo, "t.txt", "t\n");
        Commit(r.Repo, "Team");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        Write(r.Repo, "p.txt", "p\n");
        Commit(r.Repo, "Person's unpushed", author: "Person");
        AddOrigin(r, ("trunk", r.Root));
        var refused = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("refused", refused.Outcome);
        Assert.StartsWith("Your trunk has 1 commit that origin does not have, so Merge to main would refuse", refused.Summary);
        Assert.Empty(refused.Commits);
        Assert.Equal(refused.Text, await ConciergeMergeText(r));
        Emit("merge-refused", MergeJson(refused));
    }

    // ------------------------------------------------------------------ Delete preview

    [Fact]
    public async Task DeletePreview_MergedCherryPickedAndUnique_AndTheDeletedCommitStillOpens()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        // merged: an ancestor of trunk.
        AgentGit.Host(r.Repo, "branch", "feature/merged", r.Root);
        // picked: its one commit is cherry-picked onto trunk.
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/picked");
        Write(r.Repo, "picked.txt", "picked\n");
        var pick = Commit(r.Repo, "Picked work");
        // mixed: one commit picked to trunk too, one of its own.
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "members/alpha/ada", "trunk");
        Write(r.Repo, "shared.txt", "shared\n");
        var shared = Commit(r.Repo, "Shared fix");
        Write(r.Repo, "own.txt", "own\n");
        var own = Commit(r.Repo, "Ada's own idea");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        AgentGit.Host(r.Repo, "-c", "user.name=Person", "-c", "user.email=p@example.invalid", "cherry-pick", pick);
        AgentGit.Host(r.Repo, "-c", "user.name=Person", "-c", "user.email=p@example.invalid", "cherry-pick", shared);

        var merged = await f.Repos.GitDeletePreviewAsync(r.TeamId, "Alpha", "feature/merged", Ct);
        Assert.True(merged.Exists);
        Assert.True(merged.Merged);
        Assert.Empty(merged.Commits);
        Assert.Equal("Nothing is lost: every commit of feature/merged is in trunk.", merged.Lost);
        Assert.Equal($"feature/merged at {r.Root[..12]}: {merged.Lost}", merged.Text);

        var picked = await f.Repos.GitDeletePreviewAsync(r.TeamId, "Alpha", "feature/picked", Ct);
        Assert.False(picked.Merged);
        Assert.Equal((0, 1), (picked.Own, picked.InBase));
        Assert.Equal(CherryCommitRow.InBase, Assert.Single(picked.Commits).State);
        Assert.Equal("Nothing is lost: its 1 commit not in trunk already has the same change there.", picked.Lost);
        Assert.Contains($"  - {pick[..12]} Picked work (Ada): already in trunk, local only", picked.Text);

        var mixed = await f.Repos.GitDeletePreviewAsync(r.TeamId, "Alpha", "members/alpha/ada", Ct);
        Assert.Equal((1, 1), (mixed.Own, mixed.InBase));
        Assert.Equal([(own, CherryCommitRow.Own), (shared, CherryCommitRow.InBase)], mixed.Commits.Select(c => (c.Sha, c.State)));
        Assert.Equal($"Deleting it loses 1 commit of its own ({own[..12]}); this repository has no origin, so it is nowhere else.", mixed.Lost);
        Assert.Equal(
            $"members/alpha/ada at {own[..12]}: {mixed.Lost}\n2 commits not in trunk:\n"
            + $"  - {own[..12]} Ada's own idea (Ada): work of its own, local only\n"
            + $"  - {shared[..12]} Shared fix (Ada): already in trunk, local only", mixed.Text);
        // No origin: every commit of the branch (the root too) is local only.
        Assert.Equal(new BranchPush("members/alpha/ada", false, 3), mixed.Push);

        var none = await f.Repos.GitDeletePreviewAsync(r.TeamId, "Alpha", "no/such", Ct);
        Assert.False(none.Exists);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.GitDeletePreviewAsync(r.TeamId, "Alpha", "--all", Ct));

        // The dialog's Delete plan shows the same text under the branch; the delete records its commits.
        var plan = await f.Repos.GitDeleteBranchesAsync(r.TeamId, "Alpha", [], null, ["members/alpha/ada"], false, ["members/alpha/ada"], null, Ct);
        Assert.Contains(string.Join('\n', mixed.Text.Split('\n').Select(l => "    " + l)), plan.Plan.ReplaceLineEndings("\n"));
        var done = await f.Repos.GitDeleteBranchesAsync(r.TeamId, "Alpha", [], null, ["members/alpha/ada"], false, ["members/alpha/ada"], plan.Token, Ct);
        Assert.Contains("deleted members/alpha/ada", done.Result);

        var shown = await f.Repos.GitCommitShowAsync(r.TeamId, "Alpha", own, Ct);
        Assert.True(shown.Found);
        Assert.Equal("members/alpha/ada", shown.Where!.DeletedFrom);
        Assert.Equal("members/alpha/ada", shown.ToJson()["where"]!["deletedFrom"]!.GetValue<string>());
        Assert.Equal("Ada", shown.Made?.Member);
        // A commit also in trunk says nothing of the deleted branch; the Monitor's feed never carries these records.
        Assert.Null((await f.Repos.GitCommitShowAsync(r.TeamId, "Alpha", r.Root, Ct)).Where!.DeletedFrom);
        Assert.DoesNotContain(f.Repos.CodeEvents.Read("Alpha"), e => e.Kind == CodeEvent.BranchDeleted);
    }

    // ------------------------------------------------------------------ the commits behind a count

    [Fact]
    public async Task CommitRange_ListsMatchEveryCount_AndRefusesWhatIsNotACommitOrBranch()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "members/alpha/ada");
        Write(r.Repo, "ada.txt", "1\n");
        Commit(r.Repo, "Ada 1");
        Write(r.Repo, "ada.txt", "2\n");
        Commit(r.Repo, "Ada 2");
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha");
        Write(r.Repo, "t.txt", "t\n");
        Commit(r.Repo, "Team 1");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        Write(r.Repo, "p.txt", "p\n");
        Commit(r.Repo, "Person 1", author: "Person");
        Write(r.Repo, "p.txt", "pp\n");
        var trunk = Commit(r.Repo, "Person 2", author: "Person");
        AddOrigin(r, ("trunk", r.Root));

        var s = await f.Repos.GitStatusAsync(r.TeamId, "Alpha", Ct);
        async Task Matches(int count, CommitRangeRef? range)
        {
            Assert.NotNull(range);
            var list = await f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", range!.From, range.To, ct: Ct);
            Assert.Equal(count, list.Count);
            Assert.Equal(count, list.Commits.Count);
            Assert.Equal((range.From, range.To), (list.FromSha, list.ToSha));
        }
        // trunk is 2 ahead of origin's; local trunk is not behind origin's, so Merge to main starts from local trunk.
        Assert.Equal((2, 0), (s.BaseAheadOfOrigin, s.BaseBehindOrigin));
        await Matches(s.BaseAheadOfOrigin!.Value, s.Ranges.BaseAheadOfOrigin);
        await Matches(s.BaseBehindOrigin!.Value, s.Ranges.BaseBehindOrigin);
        Assert.Equal((3, 2), (s.TeamAheadOfBase, s.TeamBehindBase));
        await Matches(s.TeamAheadOfBase, s.Ranges.TeamAheadOfBase);
        await Matches(s.TeamBehindBase, s.Ranges.TeamBehindBase);
        var ada = Assert.Single(s.Members, m => m.Branch == "members/alpha/ada");
        await Matches(ada.AheadOfBase, ada.AheadRange);
        Assert.Equal(new BranchPush("members/alpha/ada", false, 2), ada.Push);

        var behind = await f.Repos.BehindBaseAsync(r.TeamId, "Alpha", DateTimeOffset.UtcNow, Ct);
        Assert.Equal(2, behind!.Behind);
        await Matches(behind.Behind, behind.Range);

        // By branch name too, newest first, with the limit.
        var named = await f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", "refs/heads/trunk", "refs/heads/team/alpha", 2, Ct);
        Assert.Equal((3, true), (named.Count, named.Truncated));
        Assert.Equal(["Team 1", "Ada 2"], named.Commits.Select(c => c.Subject));
        var byShortId = await f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", "refs/remotes/origin/trunk", trunk[..8], ct: Ct);
        Assert.Equal((r.Root, trunk, 2), (byShortId.FromSha, byShortId.ToSha, byShortId.Count));
        foreach (var bad in new[] { "HEAD~1", "--all", "trunk", "refs/heads/../x", "refs/tags/v1" })
            await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", bad, "refs/heads/trunk", ct: Ct));
        Assert.Equal("gone", (await Assert.ThrowsAsync<TeamGitRefusal>(() => f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", "refs/heads/nope", "refs/heads/trunk", ct: Ct))).Code);
    }

    // ------------------------------------------------------------------ Sync

    [Fact]
    public async Task Sync_ListsTheCommitsThatCameIn()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha");
        Write(r.Repo, "team.txt", "team\n");
        var before = Commit(r.Repo, "Team work");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        Write(r.Repo, "p1.txt", "1\n");
        var p1 = Commit(r.Repo, "Person 1", author: "Person");
        Write(r.Repo, "p2.txt", "2\n");
        var p2 = Commit(r.Repo, "Person 2", author: "Person");

        var synced = await f.Repos.SyncNowAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("synced", synced.Outcome);
        Assert.Equal([p2, p1], synced.Incoming.Select(c => c.Sha));
        Assert.Equal((2, false), (synced.IncomingCount, synced.IncomingTruncated));
        Assert.Equal(new CommitRangeRef(before, p2), synced.IncomingRange);
        Assert.All(synced.Incoming, c => Assert.Equal("Person", c.Author.Name));

        var again = await f.Repos.SyncNowAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("in-step", again.Outcome);
        Assert.Empty(again.Incoming);
        Assert.Null(again.IncomingRange);
    }

    // ------------------------------------------------------------------ the Concierge says the same

    [Fact]
    public async Task Concierge_DeleteListAndMergePreview_AreTheDialogsText()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/own");
        Write(r.Repo, "own.txt", "own\n");
        var own = Commit(r.Repo, "Own work");
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha", "trunk");
        Write(r.Repo, "t.txt", "t\n");
        Commit(r.Repo, "Team work");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");

        await using var bus = new BusEngine(f.Db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false, Repos = f.Repos });
        await bus.StartAsync(Ct);

        var dialogMerge = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        var said = await new ConciergeReadTools(bus, (_, _, _) => Task.FromResult<JsonNode?>(null), f.Db.ConnectionString, remote: false)
            .CallAsync("git_merge_preview", new JsonObject { ["team"] = "Alpha" }, Ct);
        Assert.Equal(dialogMerge.Text, said);
        Assert.Contains("git_merge_preview", ConciergeReadTools.Names);

        var dialogDelete = await f.Repos.GitDeletePreviewAsync(r.TeamId, "Alpha", "feature/own", Ct);
        var empty = new Func<string, JsonObject?, CancellationToken, Task<JsonNode?>>((_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()));
        var book = new ConfirmationBook();
        var repo = new ActionRepo("main", r.Repo, "trunk", []);
        ConciergeActions Actions() => new(bus, empty, empty, new ConciergeGit(f.Repos.Git, f.Repos.Pusher!),
            (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([repo]), book, turn: new ConciergeTurn("c1", byPerson: true, remote: false));
        var args = new JsonObject { ["team"] = "Alpha", ["branches"] = "feature/own", ["unmerged_ok"] = "feature/own" };
        var shown = await Actions().TryCallAsync("git_branches_delete", args.DeepClone().AsObject(), Ct);
        Assert.StartsWith("NOT DONE", shown);
        Assert.Contains(string.Join('\n', dialogDelete.Text.Split('\n').Select(l => "    " + l)), shown!.ReplaceLineEndings("\n"));

        // The person's yes (a later turn): deleted, and the commit view still says where its commit was.
        var yes = args.DeepClone().AsObject();
        yes["confirm"] = System.Text.RegularExpressions.Regex.Match(shown, "confirm=\"([0-9a-f]+)\"").Groups[1].Value;
        Assert.StartsWith("ok:", await Actions().TryCallAsync("git_branches_delete", yes, Ct));
        Assert.Equal("feature/own", (await f.Repos.GitCommitShowAsync(r.TeamId, "Alpha", own, Ct)).Where!.DeletedFrom);
    }

    // ------------------------------------------------------------------ Delete: what origin has, and the commits after the delete

    [Fact]
    public async Task DeletePreview_PushedHalfPushedAndWithAMerge_AndEveryRecordedCommitStillOpens()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        // pushed: origin has its tip.
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/pushed");
        Write(r.Repo, "p1.txt", "1\n");
        var p1 = Commit(r.Repo, "Pushed 1");
        Write(r.Repo, "p2.txt", "2\n");
        var p2 = Commit(r.Repo, "Pushed 2");
        // half: origin has its first commit only.
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/half", "trunk");
        Write(r.Repo, "h1.txt", "1\n");
        var h1 = Commit(r.Repo, "Half 1");
        Write(r.Repo, "h2.txt", "2\n");
        var h2 = Commit(r.Repo, "Half 2");
        // ben: own work, a side branch merged in, and a commit cherry-picked to trunk.
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "side", "trunk");
        Write(r.Repo, "side.txt", "side\n");
        var side = Commit(r.Repo, "Side work", author: "Ben");
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "members/alpha/ben", "trunk");
        Write(r.Repo, "ben.txt", "ben\n");
        var ben = Commit(r.Repo, "Ben's own", author: "Ben");
        Write(r.Repo, "fix.txt", "fix\n");
        var fix = Commit(r.Repo, "Shared fix", author: "Ben");
        AgentGit.Host(r.Repo, "-c", "user.name=Ben", "-c", "user.email=ben@example.invalid", "-c", "commit.gpgSign=false", "merge", "-q", "--no-ff", "-m", "Merge side", "side");
        var merge = Rev(r.Repo, "HEAD");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        AgentGit.Host(r.Repo, "-c", "user.name=Person", "-c", "user.email=p@example.invalid", "cherry-pick", fix);
        AgentGit.Host(r.Repo, "branch", "-q", "-D", "side");
        AddOrigin(r, ("trunk", r.Root), ("feature/pushed", p2), ("feature/half", h1));

        var pushed = await f.Repos.GitDeletePreviewAsync(r.TeamId, "Alpha", "feature/pushed", Ct);
        Assert.Equal((2, 0), (pushed.Own, pushed.InBase));
        Assert.Equal($"Deleting it loses 2 commits of its own ({p2[..12]}, {p1[..12]}); origin still has them.", pushed.Lost);
        Assert.Equal(new BranchPush("feature/pushed", true, 0), pushed.Push);
        Assert.All(pushed.Commits, c => Assert.False(c.NotPushed));
        Emit("delete-pushed", S(pushed));

        var half = await f.Repos.GitDeletePreviewAsync(r.TeamId, "Alpha", "feature/half", Ct);
        Assert.Equal($"Deleting it loses 2 commits of its own ({h2[..12]}, {h1[..12]}); 1 of them is not on origin either.", half.Lost);
        Assert.Equal([true, false], half.Commits.Select(c => c.NotPushed));
        Assert.Equal(new BranchPush("feature/half", true, 1), half.Push);
        Assert.Contains($"  - {h2[..12]} Half 2 (Ada): work of its own, not pushed", half.Text);
        Emit("delete-half-pushed", S(half));

        var b = await f.Repos.GitDeletePreviewAsync(r.TeamId, "Alpha", "members/alpha/ben", Ct);
        Assert.Equal(new[] { (merge, CherryCommitRow.Merge), (fix, CherryCommitRow.InBase), (ben, CherryCommitRow.Own), (side, CherryCommitRow.Own) }.OrderBy(x => x.Item1, StringComparer.Ordinal),
            b.Commits.Select(c => (c.Sha, c.State)).OrderBy(x => x.Sha, StringComparer.Ordinal));
        Assert.Equal(merge, b.Commits[0].Sha); // newest first
        Assert.True(b.Commits.Single(c => c.Sha == merge).IsMerge);
        Assert.Equal((2, 1), (b.Own, b.InBase));
        Assert.StartsWith("Deleting it loses 2 commits of its own (", b.Lost);
        Assert.EndsWith("; they are not on origin either.", b.Lost);
        Assert.Contains($"  - {merge[..12]} Merge side (Ben): a merge, not pushed", b.Text);
        Assert.Contains($"  - {fix[..12]} Shared fix (Ben): already in trunk, not pushed", b.Text);
        Emit("delete-with-merge", S(b));
        await EachRowOpens(r, b.Commits.Concat(half.Commits).Concat(pushed.Commits));

        // The Concierge's delete list shows each branch's preview text under it.
        await using var bus = new BusEngine(f.Db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false, Repos = f.Repos });
        await bus.StartAsync(Ct);
        var empty = new Func<string, JsonObject?, CancellationToken, Task<JsonNode?>>((_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()));
        var repo = new ActionRepo("main", r.Repo, "trunk", []);
        var shown = await new ConciergeActions(bus, empty, empty, new ConciergeGit(f.Repos.Git, f.Repos.Pusher!),
                (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([repo]), new ConfirmationBook(), turn: new ConciergeTurn("c1", byPerson: true, remote: false))
            .TryCallAsync("git_branches_delete", new JsonObject { ["team"] = "Alpha", ["branches"] = "feature/half, feature/pushed", ["unmerged_ok"] = "feature/half, feature/pushed" }, Ct);
        Assert.StartsWith("NOT DONE", shown);
        foreach (var t in new[] { half.Text, pushed.Text })
            Assert.Contains(string.Join('\n', t.Split('\n').Select(l => "    " + l)), shown!.ReplaceLineEndings("\n"));

        // The dialog's delete of ben: every commit the preview listed (tip, merge, the merged side commit, the picked one) opens after.
        var plan = await f.Repos.GitDeleteBranchesAsync(r.TeamId, "Alpha", [], null, ["members/alpha/ben"], false, ["members/alpha/ben"], null, Ct);
        Assert.Contains(string.Join('\n', b.Text.Split('\n').Select(l => "    " + l)), plan.Plan.ReplaceLineEndings("\n"));
        var done = await f.Repos.GitDeleteBranchesAsync(r.TeamId, "Alpha", [], null, ["members/alpha/ben"], false, ["members/alpha/ben"], plan.Token, Ct);
        Assert.Contains("deleted members/alpha/ben", done.Result);
        Assert.Null(await new GitPreviews(f.Repos.Git).RevAsync(r.Repo, "refs/heads/members/alpha/ben", Ct));
        foreach (var sha in new[] { merge, ben, fix, side })
        {
            var c = await f.Repos.GitCommitShowAsync(r.TeamId, "Alpha", sha, Ct);
            Assert.True(c.Found, sha);
            Assert.False(c.Where!.InMain);
            Assert.Equal("members/alpha/ben", c.Where.DeletedFrom);
        }
        Emit("commit-deleted-tip", (await f.Repos.GitCommitShowAsync(r.TeamId, "Alpha", merge, Ct)).ToJson());
        // The cherry-picked copy in trunk is a different commit: in main, no deletedFrom.
        var picked = Rev(r.Repo, "refs/heads/trunk");
        Assert.Null((await f.Repos.GitCommitShowAsync(r.TeamId, "Alpha", picked, Ct)).Where!.DeletedFrom);
    }

    [Fact]
    public async Task DeletePreview_FullyMergedBranch_ItsTipStillOpens_InMain_WithNoDeletedFrom()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "members/alpha/ada");
        Write(r.Repo, "x.txt", "x\n");
        var x = Commit(r.Repo, "Merged work");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        AgentGit.Host(r.Repo, "merge", "-q", "--ff-only", "members/alpha/ada");
        var p = await f.Repos.GitDeletePreviewAsync(r.TeamId, "Alpha", "members/alpha/ada", Ct);
        Assert.True(p.Merged);
        Assert.Equal("Nothing is lost: every commit of members/alpha/ada is in trunk.", p.Lost);
        Emit("delete-merged", S(p));
        var plan = await f.Repos.GitDeleteBranchesAsync(r.TeamId, "Alpha", [], null, ["members/alpha/ada"], false, [], null, Ct);
        await f.Repos.GitDeleteBranchesAsync(r.TeamId, "Alpha", [], null, ["members/alpha/ada"], false, [], plan.Token, Ct);
        var c = await f.Repos.GitCommitShowAsync(r.TeamId, "Alpha", x, Ct);
        Assert.True(c.Found);
        Assert.True(c.Where!.InMain);
        Assert.Null(c.Where.DeletedFrom);
    }

    // ------------------------------------------------------------------ every count against its commits; Sync as a fast-forward

    [Fact]
    public async Task EveryCount_OpensExactlyItsCommits_AndEachRowsMarkIsTheCommitViews()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "members/alpha/ada");
        Write(r.Repo, "ada.txt", "1\n");
        var ada1 = Commit(r.Repo, "Ada 1");
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha");
        Write(r.Repo, "t.txt", "t\n");
        Commit(r.Repo, "Team 1");
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "members/alpha/ben", "trunk");
        Write(r.Repo, "ben.txt", "b\n");
        Commit(r.Repo, "Ben 1", author: "Ben");
        Write(r.Repo, "ben.txt", "bb\n");
        Commit(r.Repo, "Ben 2", author: "Ben");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        Write(r.Repo, "p.txt", "p\n");
        Commit(r.Repo, "Person 1", author: "Person");
        // origin has trunk at the root and Ada's first commit; nothing else.
        AddOrigin(r, ("trunk", r.Root), ("members/alpha/ada", ada1));

        var s = await f.Repos.GitStatusAsync(r.TeamId, "Alpha", Ct);
        var counts = new List<(string Name, int Count, CommitRangeRef? Range)>
        {
            ("baseAheadOfOrigin", s.BaseAheadOfOrigin ?? 0, s.Ranges.BaseAheadOfOrigin),
            ("baseBehindOrigin", s.BaseBehindOrigin ?? 0, s.Ranges.BaseBehindOrigin),
            ("teamAheadOfBase", s.TeamAheadOfBase, s.Ranges.TeamAheadOfBase),
            ("teamBehindBase", s.TeamBehindBase, s.Ranges.TeamBehindBase),
        };
        counts.AddRange(s.Members.Select(m => ($"member {m.Branch}", m.AheadOfBase, m.AheadRange)));
        Assert.Equal([1, 0, 2, 1, 1, 2], counts.Select(c => c.Count));
        foreach (var (name, count, range) in counts)
        {
            Assert.NotNull(range);
            var list = await f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", range!.From, range.To, ct: Ct);
            Assert.True(count == list.Count && count == list.Commits.Count, $"{name}: count {count}, list {list.Count}/{list.Commits.Count}");
            var direct = AgentGit.Host(r.Repo, "rev-list", range.To, "^" + range.From).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(direct, list.Commits.Select(c => c.Sha));
            await EachRowOpens(r, list.Commits);
        }
        var ada = s.Members.Single(m => m.Branch == "members/alpha/ada");
        Assert.Equal(new BranchPush("members/alpha/ada", true, 0), ada.Push);
        var benPush = s.Members.Single(m => m.Branch == "members/alpha/ben").Push;
        Assert.Equal(new BranchPush("members/alpha/ben", false, 2), benPush);
        Emit("status-counts", S(s));
        Emit("range-member-ben", S(await f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", s.Members.Single(m => m.Branch == "members/alpha/ben").AheadRange!.From,
            s.Members.Single(m => m.Branch == "members/alpha/ben").AheadRange!.To, ct: Ct)));
    }

    [Fact]
    public async Task Sync_FastForwardOfTheTeamBranch_IncomingIsTheBehindRangeShownBefore()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "branch", "team/alpha", r.Root);
        Write(r.Repo, "p1.txt", "1\n");
        Commit(r.Repo, "Person 1", author: "Person");
        Write(r.Repo, "p2.txt", "2\n");
        var p2 = Commit(r.Repo, "Person 2", author: "Person");
        // origin has trunk at the root: both commits that come in are not pushed (the shell must mark them as in any list).
        AddOrigin(r, ("trunk", r.Root));

        var s = await f.Repos.GitStatusAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal(2, s.TeamBehindBase);
        var shownBefore = await f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", s.Ranges.TeamBehindBase!.From, s.Ranges.TeamBehindBase.To, ct: Ct);

        var synced = await f.Repos.SyncNowAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal("synced", synced.Outcome);
        Assert.Equal(p2, Rev(r.Repo, "refs/heads/team/alpha")); // a fast-forward: no merge commit
        Assert.Equal(shownBefore.Commits.Select(c => c.Sha), synced.Incoming.Select(c => c.Sha));
        Assert.Equal(shownBefore.Count, synced.IncomingCount);
        Assert.Equal(new CommitRangeRef(r.Root, p2), synced.IncomingRange);
        Assert.All(synced.Incoming, c => Assert.True(c.NotPushed));
        await EachRowOpens(r, synced.Incoming);
        Emit("status-before-sync", S(s));
        Emit("range-team-behind", S(shownBefore));
        Emit("sync-ff", S(synced));
    }

    // ------------------------------------------------------------------ teams.syncToBase behind.from / to

    /// <summary>teams.syncToBase exactly as UiRpc sends it (the RpcDispatcher a window calls).</summary>
    private static async Task<JsonObject> SyncToBaseJson(Rig r)
    {
        await using var bus = new BusEngine(r.F.Db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false, Repos = r.F.Repos });
        await bus.StartAsync(Ct);
        var terminals = new Yawble.Core.Ui.TerminalHub(new Yawble.Core.Ui.AppEvents());
        var ui = new Yawble.Core.Ui.UiRpc(bus, new Yawble.Core.Policy.PolicyStore(r.F.Db.ConnectionString),
            new Yawble.Core.Ui.MemberLayouts(Path.Combine(r.F.Db.Dir, "data"), r.F.Db.Dir, r.F.Db.Dir), new Yawble.Core.Ui.FakeModelCredentials(),
            new Yawble.Core.Ui.FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new Yawble.Core.Ui.FakeInteractiveSessions(terminals, bus), terminals, 16_384);
        var rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
        return (await rpc.DispatchAsync("teams.syncToBase", new JsonObject { ["teamId"] = r.TeamId }, Ct))!.AsObject();
    }

    [Fact]
    public async Task SyncToBase_Behind_FromIsTeamTip_ToIsLocalBase_RangeIsExactlyTheCount()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "branch", "team/alpha", r.Root);
        Write(r.Repo, "p1.txt", "1\n");
        Commit(r.Repo, "Person 1", author: "Person");
        Write(r.Repo, "p2.txt", "2\n");
        var p2 = Commit(r.Repo, "Person 2", author: "Person");

        var j = await SyncToBaseJson(r);
        var b = j["behind"]!.AsObject();
        Assert.Equal("team/alpha", (string?)b["teamBranch"]);
        Assert.Equal(2, (int)b["count"]!);
        Assert.Equal("team/alpha is 2 commits behind trunk", (string?)b["text"]);
        Assert.True(b.ContainsKey("from") && b.ContainsKey("to"));
        Assert.Equal(r.Root, (string?)b["from"]);
        Assert.Equal(p2, (string?)b["to"]);
        // the same pair git.status gives for the team-behind count, and git.commitRange of it lists exactly count commits.
        var s = await f.Repos.GitStatusAsync(r.TeamId, "Alpha", Ct);
        Assert.Equal(new CommitRangeRef(r.Root, p2), s.Ranges.TeamBehindBase);
        var range = await f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", (string)b["from"]!, (string)b["to"]!, ct: Ct);
        Assert.Equal(2, range.Count);
        Assert.Equal(AgentGit.Host(r.Repo, "rev-list", p2, "^" + r.Root).Split('\n', StringSplitOptions.RemoveEmptyEntries),
            range.Commits.Select(c => c.Sha));
        Emit("synctobase-behind", j);
        Emit("synctobase-range", S(range));

        // after Sync now the team branch is level: behind is null (no from / to sent at all).
        await f.Repos.SyncNowAsync(r.TeamId, "Alpha", Ct);
        var after = await SyncToBaseJson(r);
        Assert.Null(after["behind"]);
        Emit("synctobase-level", after);
    }

    [Fact]
    public async Task SyncToBase_Behind_ToIsOriginsBase_WhenLocalIsOnlyBehindIt()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "branch", "team/alpha", r.Root);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "tmp");
        Write(r.Repo, "o.txt", "origin\n");
        var o1 = Commit(r.Repo, "On origin", author: "Person");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        AddOrigin(r, ("trunk", o1));

        var b = (await SyncToBaseJson(r))["behind"]!.AsObject();
        Assert.Equal(1, (int)b["count"]!);
        Assert.Equal(r.Root, (string?)b["from"]);
        Assert.Equal(o1, (string?)b["to"]); // origin's base, as Merge to main reads it
        var range = await f.Repos.GitCommitRangeAsync(r.TeamId, "Alpha", (string)b["from"]!, (string)b["to"]!, ct: Ct);
        Assert.Equal([o1], range.Commits.Select(c => c.Sha));
    }

    // ------------------------------------------------------------------ the window's calls

    [Fact]
    public async Task TheWindowsCalls_GiveTheContractsShapes_AndAreReads()
    {
        using var db = new TempDb();
        var appRoot = Path.Combine(db.Dir, "app");
        Directory.CreateDirectory(appRoot);
        var terminals = new TerminalHub(new AppEvents());
        BusEngine? busRef = null;
        var runner = new FakePtyRunner(terminals, id => busRef!.MemberAsync(id).GetAwaiter().GetResult()?.Cli ?? "claude", ticks: 1, tick: TimeSpan.FromMilliseconds(10));
        await using var bus = await BusHost.StartAsync(db.ConnectionString, _ => null, null, launcher: null, runner, () => 4, appDataRoot: appRoot);
        busRef = bus;
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var ui = new UiRpc(bus, new PolicyStore(db.ConnectionString), new MemberLayouts(Path.Combine(appRoot, "data"), db.Dir, home), new FakeModelCredentials(),
            new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, bus), terminals, 16_384);
        var rpc = new RpcDispatcher(new Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
        async Task<JsonObject> Call(string method, JsonObject p) => (await rpc.DispatchAsync(method, p, Ct))!.AsObject();

        var teamId = await bus.CreateTeamAsync("web", "Manager", "claude", null, Ct);
        var status = await Call("git.status", new JsonObject { ["teamId"] = teamId });
        var path = status["repoPath"]!.GetValue<string>();
        var baseSha = status["baseSha"]!.GetValue<string>();
        // A team branch one commit ahead, made without a working tree.
        var tree = AgentGit.Host(path, "rev-parse", baseSha + "^{tree}");
        var teamSha = AgentGit.Host(path, "-c", "user.name=M", "-c", "user.email=m@example.invalid", "commit-tree", tree, "-p", baseSha, "-m", "Team work");
        AgentGit.Host(path, "update-ref", "refs/heads/team/web", teamSha);

        status = await Call("git.status", new JsonObject { ["teamId"] = teamId });
        var ahead = status["ranges"]!["teamAheadOfBase"]!.AsObject();
        Assert.Equal((baseSha, teamSha), (ahead["from"]!.GetValue<string>(), ahead["to"]!.GetValue<string>()));
        Assert.Null(status["ranges"]!["baseAheadOfOrigin"]);

        var range = await Call("git.commitRange", new JsonObject { ["teamId"] = teamId, ["from"] = baseSha, ["to"] = teamSha });
        Assert.Equal(1, range["count"]!.GetValue<int>());
        var row = range["commits"]![0]!.AsObject();
        Assert.Equal(["sha", "shortSha", "subject", "author", "date", "isMerge", "notPushed"], row.Select(kv => kv.Key));
        Assert.Equal(["name", "email"], row["author"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal((teamSha, "Team work", true), (row["sha"]!.GetValue<string>(), row["subject"]!.GetValue<string>(), row["notPushed"]!.GetValue<bool>()));

        var merge = await Call("git.mergePreview", new JsonObject { ["team"] = "web" });
        Assert.Equal("fast-forward", merge["outcome"]!.GetValue<string>());
        foreach (var key in new[] { "summary", "text", "from", "to", "newSha", "conflictFiles", "commits", "count", "truncated", "files", "diffFrom", "diffTo", "hasOrigin", "teamPush" })
            Assert.True(merge.ContainsKey(key), key);
        Assert.Empty(merge["files"]!.AsArray()); // no file changed: no diff ends either
        Assert.Null(merge["diffFrom"]);

        var del = await Call("git.deletePreview", new JsonObject { ["teamId"] = teamId, ["branch"] = "team/web" });
        Assert.Equal("own", del["commits"]![0]!["state"]!.GetValue<string>());
        foreach (var key in new[] { "branch", "baseBranch", "exists", "sha", "merged", "own", "inBase", "truncated", "lost", "text", "hasOrigin", "push" })
            Assert.True(del.ContainsKey(key), key);

        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => Call("git.commitRange", new JsonObject { ["teamId"] = teamId, ["from"] = "HEAD", ["to"] = teamSha }))).Code);
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => Call("git.deletePreview", new JsonObject { ["teamId"] = teamId }))).Code);
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => Call("git.mergeDiff", new JsonObject { ["teamId"] = teamId, ["from"] = "abc", ["to"] = teamSha, ["path"] = "x" }))).Code);

        foreach (var m in new[] { "git.commitRange", "git.mergePreview", "git.mergeDiff", "git.deletePreview" })
        {
            Assert.True(AuditLog.IsReadOnly(m), m);
            Assert.Null(RemoteAccess.Refusal(m, new JsonObject(), allowHostTerminals: false, RemoteAccess.Read, showCode: true));
        }
        Assert.NotNull(RemoteAccess.Refusal("git.mergeDiff", new JsonObject(), allowHostTerminals: false, RemoteAccess.Read, showCode: false));
        Assert.Null(RemoteAccess.Refusal("git.mergePreview", new JsonObject(), allowHostTerminals: false, RemoteAccess.Read, showCode: false));
    }
}
