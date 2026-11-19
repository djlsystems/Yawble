using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Card 466 (B073 verify, TesterKofi): what B073PreviewTests (card 463) does not cover, on fixture repositories (base
/// trunk; origin, where there is one, a bare folder whose remote-tracking refs are set as a fetch leaves them):
///  - fast-forward from origin's base when the local one is only behind it: the preview, git.status's count and range,
///    and git.commitRange all name the same commits;
///  - a merge commit: Merge to main makes exactly the tree the preview diffed against, and lands the previewed commits;
///  - a conflict: nothing changes (refs, index, working tree, no MERGE_HEAD), and Merge to main names the same files;
///  - refused (the person's base ahead of origin's): preview and Concierge say so;
///  - F1: base checked out with uncommitted changes: the preview says fast-forward, Merge to main refuses;
///  - delete: a branch origin still has, one half pushed, one with a merge commit; after the delete each recorded
///    commit still opens with where.deletedFrom; the Concierge lists each branch's preview text;
///  - every count opens exactly its commits, and each row's notPushed is the commit view's !where.pushed;
///  - Sync (fast-forward of the team branch): incoming is the team-behind range shown before.
/// With CARD466_FIXTURE_OUT set, the real core's JSON (as UiRpc sends it) is written there (shell/tests/unit/fixtures/card466).
/// </summary>
public class Card466PreviewVerifyTests
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

    private static async Task<Rig> RigAsync(TeamsFixture f)
    {
        var (_, ada, _) = f.Team();
        var repo = Path.Combine(f.Db.Dir, "person-repo");
        Directory.CreateDirectory(repo);
        AgentGit.Host(repo, "init", "-q", "-b", "trunk");
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

    // ------------------------------------------------------------------ the JSON the window gets

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
        var dir = Environment.GetEnvironmentVariable("CARD466_FIXTURE_OUT");
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

    // ------------------------------------------------------------------ Merge to main

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

    /// <summary>
    /// F1 (fault). Merge to main refuses when the base is checked out with uncommitted changes (DirtyCheckoutAsync), but
    /// git.mergePreview never checks it: the preview (and the Concierge's git_merge_preview) say "trunk will fast-forward"
    /// and the dialog enables Merge, which then refuses. The contract says the preview has "the same start, refusals and
    /// outcomes as TeamRepos' merge". This test pins what happens today (flip it when fixed: outcome "refused").
    /// </summary>
    [Fact]
    public async Task F1_BaseCheckedOutWithUncommittedChanges_PreviewSaysFastForward_ButMergeRefuses()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "team/alpha");
        Write(r.Repo, "t.txt", "t\n");
        Commit(r.Repo, "Team");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        Write(r.Repo, "a.txt", "one\nedited, not committed\nthree\n");

        var p = await f.Repos.GitMergePreviewAsync(r.TeamId, "Alpha", Ct);
        var done = await f.Repos.GitMergeTeamToBaseAsync(r.TeamId, "Alpha", Ct);

        Assert.Equal("refused", done.Outcome);
        Assert.Contains("uncommitted changes", done.Message);
        Assert.Equal("fast-forward", p.Outcome); // FAULT: should be "refused", as Merge to main is
        Assert.DoesNotContain("uncommitted", p.Text);
        Emit("f1-merge-dirty", MergeJson(p));
    }

    // ------------------------------------------------------------------ Delete

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

    // ------------------------------------------------------------------ counts, Sync

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
}
