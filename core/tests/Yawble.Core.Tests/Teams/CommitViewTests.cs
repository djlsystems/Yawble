using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Link;
using Yawble.Core.Monitor;
using Yawble.Core.Provenance;
using Yawble.Core.Teams;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The commit view's core: git.commitShow and git.commitDiff on a fixture repository built here (the
/// person's repository, base trunk): an ordinary commit, a renamed file, a binary file, a merge (first-parent diff, its
/// commit count), an unpushed commit against one in origin's refs, a commit in tags, a deleted branch's commit (still
/// there, then gone after git cleans up), and the Cloud Link's view with members' code shown and hidden. Local
/// repositories only (origin is a bare folder whose refs are set as a fetch would): no network, no git transport.
/// The last section uses a second repository (base main, origin a bare folder whose branches move as a push moves
/// them) for pushed against not pushed, the where facts, merges nobody can name and the team's repository against an
/// agent's clone. With COMMIT_VIEW_FIXTURE_OUT set, those tests write the core's JSON for each case to that folder
/// (shell/tests/unit/fixtures/commit-view), which the shell's commit view test renders.
/// </summary>
public class CommitViewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A commit of everything staged, as <paramref name="author"/>, committed by <paramref name="committer"/> (the author when null).</summary>
    private static string Commit(string repo, string message, string author = "Ada", string? committer = null)
    {
        var who = committer ?? author;
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = author, ["GIT_AUTHOR_EMAIL"] = author.ToLowerInvariant() + "@example.invalid",
            ["GIT_COMMITTER_NAME"] = who, ["GIT_COMMITTER_EMAIL"] = who.ToLowerInvariant() + "@example.invalid",
        };
        AgentGit.HostWithEnv(repo, env, "add", "-A");
        AgentGit.HostWithEnv(repo, env, "-c", "commit.gpgSign=false", "commit", "-q", "--allow-empty", "-m", message);
        return AgentGit.Host(repo, "rev-parse", "HEAD");
    }

    private static void Write(string repo, string file, string text)
    {
        var full = Path.Combine(repo, file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private sealed record Rig(TeamsFixture F, long TeamId, string Repo, string Root, string Ordinary, string Renamed, string Binary, string Merge,
        string AdaOnly, string BenGone);

    /// <summary>
    /// Team Alpha on the person's repository (base trunk, origin's trunk at the root commit). Ada's branch: an ordinary
    /// commit (a different committer), a rename, a binary file; merged into team/alpha with a merge commit; then a large
    /// file only on her branch. Ben's branch: one commit, recorded by the app, whose branch is then deleted. Tags: v1.0
    /// on the root, v2.0 (annotated) on the merge.
    /// </summary>
    private static async Task<Rig> RigAsync(TeamsFixture f)
    {
        var (_, ada, _) = f.Team();
        var repo = Path.Combine(f.Db.Dir, "person-repo");
        Directory.CreateDirectory(repo);
        AgentGit.Host(repo, "init", "-q", "-b", "trunk");
        Write(repo, "README.md", "line1\nline2\nline3\n");
        var root = Commit(repo, "Start", author: "Person");
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", repo, Ct);

        AgentGit.Host(repo, "switch", "-q", "-c", "members/alpha/ada");
        Write(repo, "README.md", "line1\nLINE2\nline3\n");
        Write(repo, "a.txt", string.Join('\n', Enumerable.Range(1, 20).Select(i => $"alpha line {i}")) + "\n");
        var ordinary = Commit(repo, "Card 5: README line 2\n\nThe second line, louder.", author: "Ada", committer: "Reviewer");
        AgentGit.Host(repo, "mv", "a.txt", "b.txt");
        Write(repo, "b.txt", string.Join('\n', Enumerable.Range(1, 20).Select(i => i == 20 ? "changed last" : $"alpha line {i}")) + "\n");
        var renamed = Commit(repo, "Rename a to b");
        File.WriteAllBytes(Path.Combine(repo, "img.bin"), [0x89, 0x50, 0x4E, 0x47, 0, 0, 1, 2, 3, 0, 255]);
        var binary = Commit(repo, "Add an image");

        AgentGit.Host(repo, "switch", "-q", "-c", "team/alpha", "trunk");
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Manager", ["GIT_AUTHOR_EMAIL"] = "m@example.invalid", ["GIT_COMMITTER_NAME"] = "Manager", ["GIT_COMMITTER_EMAIL"] = "m@example.invalid",
        };
        AgentGit.HostWithEnv(repo, env, "-c", "commit.gpgSign=false", "merge", "-q", "--no-ff", "-m", "Merge branch 'members/alpha/ada' into team/alpha", "members/alpha/ada");
        var merge = AgentGit.Host(repo, "rev-parse", "HEAD");
        AgentGit.HostWithEnv(repo, env, "-c", "tag.gpgSign=false", "tag", "-a", "v2.0", "-m", "release 2", merge);
        AgentGit.Host(repo, "tag", "v1.0", root);

        AgentGit.Host(repo, "switch", "-q", "members/alpha/ada");
        Write(repo, "big.txt", string.Join('\n', Enumerable.Range(1, TeamRepos.MaxCommitDiffLines + 1).Select(i => $"row {i}")) + "\n");
        var adaOnly = Commit(repo, "A large file");

        AgentGit.Host(repo, "switch", "-q", "-c", "members/alpha/ben", "trunk");
        Write(repo, "ben.txt", "ben\n");
        var benGone = Commit(repo, "Ben's work", author: "Ben");
        AgentGit.Host(repo, "switch", "-q", "trunk");
        AgentGit.Host(repo, "branch", "-q", "-D", "members/alpha/ben");

        // origin: a bare folder, with its trunk at the root as a fetch would have left it.
        var bare = Path.Combine(f.Db.Dir, "origin.git");
        AgentGit.Host(f.Db.Dir, "init", "-q", "--bare", bare);
        AgentGit.Host(repo, "remote", "add", "origin", bare);
        AgentGit.Host(repo, "update-ref", "refs/remotes/origin/trunk", root);

        // The app's records: Ada's ordinary commit in deliverable 41 (card on the board), Ben's in deliverable 42, the merge in 43.
        f.Repos.CodeEvents.Add([
            new CodeEvent(CodeEvent.Commit, "Alpha", "Ada", "members/alpha/ada", ordinary, "Card 5: README line 2", [], DateTimeOffset.UtcNow, 41),
            new CodeEvent(CodeEvent.Commit, "Alpha", "Ben", "members/alpha/ben", benGone, "Ben's work", [], DateTimeOffset.UtcNow, 42),
            new CodeEvent(CodeEvent.MergeTeam, "Alpha", "Ada", "members/alpha/ada", merge, "Merge", [], DateTimeOffset.UtcNow, 43, Into: "team/alpha"),
        ]);
        using (var c = new SqliteConnection(f.Db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO board_cards (team, member, title, deliverable, created_at, updated_at) VALUES ('Alpha', 'Ada', 'README', 41, '2026-01-01', '2026-01-01');
                INSERT INTO board_cards (team, member, title, deliverable, created_at, updated_at) VALUES ('Alpha', 'Ben', 'Ben', 42, '2026-01-01', '2026-01-01');
                """;
            cmd.ExecuteNonQuery();
        }
        return new Rig(f, ada.TeamId, repo, root, ordinary, renamed, binary, merge, adaOnly, benGone);
    }

    private static Task<CommitInfo> Show(Rig r, string sha) => r.F.Repos.GitCommitShowAsync(r.TeamId, "Alpha", sha, Ct);

    private static Task<CommitFileDiff> Diff(Rig r, string sha, string path) => r.F.Repos.GitCommitDiffAsync(r.TeamId, "Alpha", sha, path, Ct);

    private static long CardId(TeamsFixture f, string member)
    {
        using var c = new SqliteConnection(f.Db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM board_cards WHERE member = $m;";
        cmd.Parameters.AddWithValue("$m", member);
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task AnOrdinaryCommit_SaysWhatItIs_WhereItIs_AndWhoMadeIt()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var c = await Show(r, r.Ordinary[..10]);

        Assert.True(c.Found);
        Assert.Equal(r.Ordinary, c.Sha);
        Assert.Equal(r.Ordinary[..12], c.ShortSha);
        Assert.Equal("Card 5: README line 2", c.Title);
        Assert.Equal("The second line, louder.", c.Body);
        Assert.Equal(("Ada", "ada@example.invalid"), (c.Author!.Name, c.Author.Email));
        Assert.True(DateTimeOffset.TryParse(c.Author.Date, out _));
        Assert.Equal("Reviewer", c.Committer!.Name);
        Assert.False(c.IsMerge);
        Assert.Equal([r.Root], c.Parents);
        Assert.Null(c.Merge);
        Assert.Equal(new CommitMade("Ada", CardId(f, "Ada"), 41), c.Made);

        // In the team branch and Ada's, not in trunk, not on origin; in the tag on the merge.
        Assert.False(c.Where!.InMain);
        Assert.Equal(["team/alpha", "members/alpha/ada"], c.Where.Branches);
        Assert.Null(c.Where.OnlyOnMember);
        Assert.False(c.Where.Pushed);
        Assert.Equal(["v2.0"], c.Where.Tags);

        Assert.Equal([new CommitFileChange("README.md", null, "modified", 1, 1, false), new CommitFileChange("a.txt", null, "added", 20, 0, false)], c.Files);

        var json = c.ToJson();
        Assert.True((bool)json["codeShown"]!);
        Assert.Equal("Reviewer", (string)json["committer"]!["name"]!);
        Assert.False(json["where"]!.AsObject().ContainsKey("onlyOnMember"));
        Assert.Equal(41, (long)json["made"]!["deliverable"]!);
    }

    [Fact]
    public async Task TheSameAuthorAndCommitter_HasNoCommitter_AndTheRootIsInMainPushedAndTagged()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var root = await Show(r, r.Root);
        Assert.Null(root.Committer);
        Assert.False(root.ToJson().ContainsKey("committer"));
        Assert.True(root.Where!.InMain);
        Assert.True(root.Where.Pushed);
        Assert.Equal(["v1.0", "v2.0"], root.Where.Tags.Order());
        Assert.Empty(root.Parents!);
        Assert.Equal([new CommitFileChange("README.md", null, "added", 3, 0, false)], root.Files);
        Assert.Null(root.Made);
    }

    [Fact]
    public async Task AnOrdinaryCommitsDiff_IsHunksOfNumberedLines()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var d = await Diff(r, r.Ordinary, "README.md");
        Assert.False(d.Binary);
        Assert.False(d.TooLarge);
        Assert.Null(d.Summary);
        var h = Assert.Single(d.Hunks);
        Assert.Equal((1, 3, 1, 3), (h.OldStart, h.OldCount, h.NewStart, h.NewCount));
        Assert.StartsWith("@@ -1,3 +1,3 @@", h.Header);
        Assert.Equal(
        [
            new CommitDiffLine("context", 1, 1, "line1"),
            new CommitDiffLine("deleted", 2, null, "line2"),
            new CommitDiffLine("added", null, 2, "LINE2"),
            new CommitDiffLine("context", 3, 3, "line3"),
        ], h.Lines);

        var added = await Diff(r, r.Ordinary, "a.txt");
        Assert.Equal("added", added.Status);
        var all = Assert.Single(added.Hunks);
        Assert.Equal(20, all.Lines.Count);
        Assert.All(all.Lines, l => Assert.Equal("added", l.Kind));
        Assert.Equal(20, all.Lines[^1].New);

        var json = d.ToJson();
        Assert.False(json.ContainsKey("summary"));
        Assert.False(json["hunks"]![0]!["lines"]![1]!.AsObject().ContainsKey("new"));
    }

    [Fact]
    public async Task ARenamedFile_HasItsOldPath_AndItsDiffIsTheChangeOnly()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var c = await Show(r, r.Renamed);
        Assert.Equal([new CommitFileChange("b.txt", "a.txt", "renamed", 1, 1, false)], c.Files);

        var d = await Diff(r, r.Renamed, "b.txt");
        Assert.Equal(("b.txt", "a.txt", "renamed"), (d.Path, d.OldPath, d.Status));
        var h = Assert.Single(d.Hunks);
        Assert.Contains(h.Lines, l => l is { Kind: "deleted", Old: 20, Text: "alpha line 20" });
        Assert.Contains(h.Lines, l => l is { Kind: "added", New: 20, Text: "changed last" });
        // The old name opens the same file.
        Assert.Equal("b.txt", (await Diff(r, r.Renamed, "a.txt")).Path);
        Assert.Equal("a.txt", (string)d.ToJson()["oldPath"]!);
    }

    [Fact]
    public async Task ABinaryFile_IsListedAsBinary_AndItsDiffIsASummaryOnly()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var c = await Show(r, r.Binary);
        Assert.Equal([new CommitFileChange("img.bin", null, "added", 0, 0, true)], c.Files);
        var d = await Diff(r, r.Binary, "img.bin");
        Assert.True(d.Binary);
        Assert.False(d.TooLarge);
        Assert.Empty(d.Hunks);
        Assert.Equal("Binary file added; its content is not shown.", d.Summary);
    }

    [Fact]
    public async Task ALargeFile_IsASummaryOnly_AndACommitOnlyOnAMembersBranchSaysWhose()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var c = await Show(r, r.AdaOnly);
        Assert.Equal(["members/alpha/ada"], c.Where!.Branches);
        Assert.Equal("Ada", c.Where.OnlyOnMember);
        Assert.False(c.Where.InMain);
        Assert.Empty(c.Where.Tags);
        // Not recorded by the app, but only Ada's branch has it.
        Assert.Equal(new CommitMade("Ada", null, null), c.Made);

        var d = await Diff(r, r.AdaOnly, "big.txt");
        Assert.True(d.TooLarge);
        Assert.False(d.Binary);
        Assert.Empty(d.Hunks);
        Assert.Equal($"Too large to show here: {TeamRepos.MaxCommitDiffLines + 1} lines added, 0 deleted.", d.Summary);
    }

    [Fact]
    public async Task AMerge_SaysWhatItBroughtIn_AndItsDiffIsAgainstItsFirstParent()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var m = await Show(r, r.Merge);
        Assert.True(m.IsMerge);
        Assert.Equal([r.Root, r.Binary], m.Parents);
        Assert.Equal(new CommitMerge("team/alpha", "members/alpha/ada", "Ada", 3), m.Merge);
        // The Manager's fetch-back recorded it: the Manager made the merge.
        Assert.Equal("Manager", m.Made!.Member);
        Assert.Equal(43, m.Made.Deliverable);
        Assert.Equal(["team/alpha"], m.Where!.Branches);
        Assert.Equal(["v2.0"], m.Where.Tags);
        // Against trunk (the first parent): everything Ada's branch brought, a.txt's rename folded into b.txt added.
        Assert.Equal(
        [
            new CommitFileChange("README.md", null, "modified", 1, 1, false),
            new CommitFileChange("b.txt", null, "added", 20, 0, false),
            new CommitFileChange("img.bin", null, "added", 0, 0, true),
        ], m.Files!.OrderBy(x => x.Path, StringComparer.Ordinal));

        var d = await Diff(r, r.Merge, "README.md");
        var h = Assert.Single(d.Hunks);
        Assert.Contains(h.Lines, l => l is { Kind: "added", Text: "LINE2" });
        var json = m.ToJson();
        Assert.Equal(3, (int)json["merge"]!["commitCount"]!);
        Assert.Equal("Ada", (string)json["merge"]!["fromMember"]!);
        Assert.True((bool)json["isMerge"]!);
    }

    [Fact]
    public async Task AMergeTheAppDidNotRecord_IsReadFromItsMessage()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/x", "trunk");
        Write(r.Repo, "x.txt", "x\n");
        Commit(r.Repo, "x one");
        Write(r.Repo, "x.txt", "x\ny\n");
        Commit(r.Repo, "x two");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        AgentGit.Host(r.Repo, "-c", "user.name=P", "-c", "user.email=p@x", "-c", "commit.gpgSign=false", "merge", "-q", "--no-ff", "-m", "Merge pull request #12 from someone/feature-x", "feature/x");
        var sha = AgentGit.Host(r.Repo, "rev-parse", "HEAD");
        var m = await Show(r, sha);
        Assert.Equal(new CommitMerge("trunk", "someone/feature-x", null, 2), m.Merge);
        Assert.True(m.Where!.InMain);
    }

    [Fact]
    public async Task ADeletedBranchsCommit_OpensWhileGitHasIt_ThenSaysItIsGone()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var c = await Show(r, r.BenGone);
        Assert.True(c.Found);
        Assert.Empty(c.Where!.Branches);
        Assert.False(c.Where.InMain);
        Assert.Equal(new CommitMade("Ben", CardId(f, "Ben"), 42), c.Made);
        Assert.Single((await Diff(r, r.BenGone, "ben.txt")).Hunks);

        AgentGit.Host(r.Repo, "reflog", "expire", "--expire=now", "--all");
        AgentGit.Host(r.Repo, "gc", "-q", "--prune=now");

        var gone = await Show(r, r.BenGone);
        Assert.False(gone.Found);
        Assert.Equal(r.BenGone, gone.Sha);
        Assert.Equal($"Commit {r.BenGone[..12]} was on members/alpha/ben (Ben's branch), and git no longer has it: that branch was deleted and git has since cleaned up its commits.",
            gone.GoneReason);
        var json = gone.ToJson();
        Assert.False((bool)json["found"]!);
        Assert.Equal(gone.GoneReason, (string)json["goneReason"]!);
        var refused = await Assert.ThrowsAsync<TeamGitRefusal>(() => Diff(r, r.BenGone, "ben.txt"));
        Assert.Equal("gone", refused.Code);
    }

    [Fact]
    public async Task AnUnknownCommit_IsGonePlainly_AndABadIdOrFileIsRefused()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var unknown = await Show(r, "0123456789abcdef0123456789abcdef01234567");
        Assert.False(unknown.Found);
        Assert.StartsWith("git has no commit 0123456789ab in the team's repository.", unknown.GoneReason);

        foreach (var bad in new[] { "HEAD", "--all", "trunk", "abc", "12345678 --", "" })
            await Assert.ThrowsAsync<ArgumentException>(() => Show(r, bad));
        var noFile = await Assert.ThrowsAsync<TeamGitRefusal>(() => Diff(r, r.Ordinary, "nope.txt"));
        Assert.Equal("no_such_file", noFile.Code);
        // A tree's or blob's ID is not a commit.
        var blob = AgentGit.Host(r.Repo, "rev-parse", r.Ordinary + ":README.md");
        Assert.False((await Show(r, blob)).Found);
    }

    // ------------------------------------------------------------------ the unified diff reader

    [Fact]
    public void TheDiffReader_KeepsLineNumbers_DropsNoNewlineMarks_AndReadsATrimmedLastContextLine()
    {
        var text = string.Join('\n',
            "diff --git a/f b/f", "index 1..2 100644", "--- a/f", "+++ b/f",
            "@@ -1,2 +1,2 @@ heading", "-old", "\\ No newline at end of file", "+new", " keep",
            "@@ -10 +10,2 @@", " ten", "+eleven", "",
            "@@ -20,2 +21,2 @@", "-gone", "+here", " ");
        // The last hunk's last context line was " " (an empty line) and the core's output trimming removed it.
        var hunks = TeamRepos.ParseUnifiedDiff(text.TrimEnd());
        Assert.Equal(3, hunks.Count);
        Assert.Equal("@@ -1,2 +1,2 @@ heading", hunks[0].Header);
        Assert.Equal([new CommitDiffLine("deleted", 1, null, "old"), new CommitDiffLine("added", null, 1, "new"), new CommitDiffLine("context", 2, 2, "keep")], hunks[0].Lines);
        Assert.Equal((10, 1, 10, 2), (hunks[1].OldStart, hunks[1].OldCount, hunks[1].NewStart, hunks[1].NewCount));
        Assert.Equal([new CommitDiffLine("context", 10, 10, "ten"), new CommitDiffLine("added", null, 11, "eleven")], hunks[1].Lines);
        Assert.Equal([new CommitDiffLine("deleted", 20, null, "gone"), new CommitDiffLine("added", null, 21, "here"), new CommitDiffLine("context", 21, 22, "")], hunks[2].Lines);
    }

    [Fact]
    public void TheNumstatReader_ReadsRenamesAndBinaries()
    {
        var map = TeamRepos.ParseNumstat("\n3\t1\tsrc/a.cs\0-\t-\timg.png\x00" + "2\t2\t\0old name.txt\0new name.txt\0");
        Assert.Equal((3, 1, false), map["src/a.cs"]);
        Assert.Equal((0, 0, true), map["img.png"]);
        Assert.Equal((2, 2, false), map["new name.txt"]);
        Assert.Equal(3, map.Count);
    }

    // ------------------------------------------------------------------ the Cloud Link's view

    [Fact]
    public void BothAreReads_ForAVisitorToo_AndTheDiffIsRefusedWhenCodeIsHidden()
    {
        foreach (var m in new[] { "git.commitShow", "git.commitDiff" })
        {
            Assert.True(RemoteAccess.Reads(m, new JsonObject()));
            Assert.Null(RemoteAccess.Refusal(m, new JsonObject(), allowHostTerminals: false, RemoteAccess.Read, showCode: true));
            Assert.True(AuditLog.IsReadOnly(m));
        }
        Assert.Null(RemoteAccess.Refusal("git.commitShow", new JsonObject(), allowHostTerminals: false, RemoteAccess.Read, showCode: false));
        Assert.Equal(RemoteAccess.CodeHidden, RemoteAccess.Refusal("git.commitDiff", new JsonObject(), allowHostTerminals: false, RemoteAccess.Read, showCode: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverTheLink_TheCommitAndItsFilesAreShown_AndItsDiffsFollowTheSetting(bool showCode)
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        var data = Path.Combine(f.Db.Dir, "link");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "cloud-link.json"),
            $$"""{"url":"https://cloud.example.test/","instanceId":"i1","secret":"s","instanceName":"pc","org":"o","workgroup":"w","allowHostTerminals":false,"showCode":{{(showCode ? "true" : "false")}}}""");
        var dispatched = new List<string>();
        await using var link = new CloudLink(data, async (method, p, ct) =>
        {
            dispatched.Add(method);
            var sha = (string)p!["sha"]!;
            return method == "git.commitShow"
                ? (await f.Repos.GitCommitShowAsync(r.TeamId, "Alpha", sha, ct)).ToJson()
                : (await f.Repos.GitCommitDiffAsync(r.TeamId, "Alpha", sha, (string)p["path"]!, ct)).ToJson();
        }, new AppEvents());

        async Task<JsonObject> Call(string method, JsonObject p)
        {
            JsonObject? sent = null;
            await link.AnswerAsync(new JsonObject
            {
                ["type"] = "call", ["session"] = "s1", ["id"] = 1, ["method"] = method, ["params"] = p, ["user"] = "visitor@example.com", ["access"] = "read",
            }, m => { sent = m; return Task.CompletedTask; }, Ct);
            return sent!;
        }

        var show = await Call("git.commitShow", new JsonObject { ["team"] = "Alpha", ["sha"] = r.Ordinary });
        var commit = show["result"]!.AsObject();
        Assert.Equal(showCode, (bool)commit["codeShown"]!);
        Assert.Equal("Card 5: README line 2", (string)commit["title"]!);
        Assert.Equal(2, commit["files"]!.AsArray().Count);

        var diff = await Call("git.commitDiff", new JsonObject { ["team"] = "Alpha", ["sha"] = r.Ordinary, ["path"] = "README.md" });
        if (showCode)
        {
            Assert.Equal(4, diff["result"]!["hunks"]![0]!["lines"]!.AsArray().Count);
            Assert.Equal(["git.commitShow", "git.commitDiff"], dispatched);
        }
        else
        {
            Assert.Equal("remote_refused", (string)diff["error"]!["code"]!);
            Assert.Equal(RemoteAccess.CodeHidden, (string)diff["error"]!["message"]!);
            Assert.Equal(["git.commitShow"], dispatched);
        }
    }

    [Fact]
    public void ShapingLeavesEveryOtherAnswerAlone()
    {
        var other = new JsonObject { ["codeShown"] = true };
        RemoteAccess.ShapeResult("git.status", other, showCode: false);
        Assert.True((bool)other["codeShown"]!);
        var gone = new JsonObject { ["found"] = false, ["sha"] = "abcd" };
        RemoteAccess.ShapeResult("git.commitShow", gone, showCode: false);
        Assert.False(gone.ContainsKey("codeShown"));
    }

    /// <summary>A merge with git's own message (no "into": git leaves it out for main, or for any branch with merge.suppressDest).</summary>
    private static string MergeDefault(string repo, string branch, string? suppressDest = null)
    {
        string[] dest = suppressDest is null ? [] : ["-c", "merge.suppressDest=" + suppressDest];
        AgentGit.Host(repo, ["-c", "user.name=P", "-c", "user.email=p@example.invalid", "-c", "commit.gpgSign=false", .. dest, "merge", "-q", "--no-ff", "--no-edit", branch]);
        return AgentGit.Host(repo, "rev-parse", "HEAD");
    }

    [Fact]
    public async Task AMergeOnTheBase_WithGitsDefaultMessage_IsIntoTheBase_EvenWhenTheTeamBranchHasIt()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/login", "trunk");
        Write(r.Repo, "login.txt", "login\n");
        Commit(r.Repo, "Login", author: "Person");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        var merge = MergeDefault(r.Repo, "feature/login", suppressDest: "trunk");
        Assert.Equal("Merge branch 'feature/login'", AgentGit.Host(r.Repo, "log", "-1", "--format=%s", merge));
        Assert.Equal(new CommitMerge("trunk", "feature/login", null, 1), (await Show(r, merge)).Merge);

        // The team branch moved up to trunk: it contains the merge, but the merge was made on trunk.
        AgentGit.Host(r.Repo, "branch", "-f", "team/alpha", "trunk");
        var m = await Show(r, merge);
        Assert.Equal(new CommitMerge("trunk", "feature/login", null, 1), m.Merge);
        Assert.Equal("trunk", (string)m.ToJson()["merge"]!["intoBranch"]!);
    }

    [Fact]
    public async Task AMergeOnTheTeamBranch_WithNoInto_IsIntoTheTeamBranch_AndOneOnNoBranchsLineSaysNone()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/y", "trunk");
        Write(r.Repo, "y.txt", "y\n");
        Commit(r.Repo, "Y", author: "Person");
        AgentGit.Host(r.Repo, "switch", "-q", "team/alpha");
        var onTeam = MergeDefault(r.Repo, "feature/y", suppressDest: "team/alpha");
        Assert.Equal("Merge branch 'feature/y'", AgentGit.Host(r.Repo, "log", "-1", "--format=%s", onTeam));
        // trunk does not have it; the team branch has it on its own line.
        Assert.Equal("team/alpha", (await Show(r, onTeam)).Merge!.IntoBranch);

        // A merge on a branch that was then deleted, later merged into another branch on its second-parent side: no
        // branch has it on its own line, so the core does not guess one.
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/side", "trunk");
        Write(r.Repo, "side.txt", "s\n");
        Commit(r.Repo, "Side", author: "Person");
        var sideMerge = MergeDefault(r.Repo, "feature/y", suppressDest: "feature/side");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");
        MergeDefault(r.Repo, "feature/side", suppressDest: "trunk");
        AgentGit.Host(r.Repo, "branch", "-q", "-D", "feature/side");
        var side = await Show(r, sideMerge);
        Assert.Null(side.Merge!.IntoBranch);
        Assert.Equal("feature/y", side.Merge.FromBranch);
        var json = side.ToJson()["merge"]!.AsObject();
        Assert.True(json.ContainsKey("intoBranch"));
        Assert.Null(json["intoBranch"]);
    }

    [Fact]
    public async Task ACommitOnThePersonsOwnBranch_IsListedInOtherBranches_WhichIsAlwaysThere()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/x", "trunk");
        Write(r.Repo, "x.txt", "x\n");
        var x = Commit(r.Repo, "Feature x", author: "Person");
        AgentGit.Host(r.Repo, "switch", "-q", "trunk");

        var c = await Show(r, x);
        Assert.False(c.Where!.InMain);
        Assert.Empty(c.Where.Branches);
        Assert.Null(c.Where.OnlyOnMember);
        Assert.Equal(["feature/x"], c.Where.OtherBranches);
        var where = c.ToJson()["where"]!;
        Assert.Equal(["feature/x"], where["otherBranches"]!.AsArray().Select(n => (string)n!));
        Assert.Empty(where["branches"]!.AsArray());

        // A deleted member branch's commit: no other branch either, so the two read differently.
        var gone = (await Show(r, r.BenGone)).ToJson()["where"]!;
        Assert.Empty(gone["branches"]!.AsArray());
        Assert.Empty(gone["otherBranches"]!.AsArray());

        // Never the base, the team branch or a member's branch; sorted; branches still lists the team's and the member's.
        AgentGit.Host(r.Repo, "branch", "zeta", r.Ordinary);
        AgentGit.Host(r.Repo, "branch", "feature/a", r.Ordinary);
        var o = await Show(r, r.Ordinary);
        Assert.Equal(["team/alpha", "members/alpha/ada"], o.Where!.Branches);
        Assert.Equal(["feature/a", "zeta"], o.Where.OtherBranches);
        var root = (await Show(r, r.Root)).ToJson()["where"]!;
        Assert.Equal(["feature/a", "feature/x", "zeta"], root["otherBranches"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public async Task AHexIdIsNeverABranchName()
    {
        using var f = new TeamsFixture();
        var r = await RigAsync(f);
        // Branches named like other commits' IDs (short and full), pointing at the root: the IDs still mean the commits.
        var shortId = r.Ordinary[..8];
        AgentGit.Host(r.Repo, "branch", shortId, r.Root);
        AgentGit.Host(r.Repo, "branch", r.Binary, r.Root);
        Assert.Equal(r.Ordinary, (await Show(r, shortId)).Sha);
        Assert.Equal(r.Ordinary, (await Show(r, shortId.ToUpperInvariant())).Sha);
        Assert.Equal(r.Binary, (await Show(r, r.Binary)).Sha);
        Assert.Single((await Diff(r, shortId, "README.md")).Hunks);

        // A branch whose hex name is no object's ID: not found, never the branch's commit.
        var objects = AgentGit.Host(r.Repo, "rev-parse", "--disambiguate=cafe");
        Assert.True(string.IsNullOrWhiteSpace(objects), "the fixture happens to have an object starting with cafe");
        AgentGit.Host(r.Repo, "branch", "cafe", r.Root);
        var c = await Show(r, "cafe");
        Assert.False(c.Found);
        Assert.Equal("cafe", c.Sha);
        Assert.Equal("gone", (await Assert.ThrowsAsync<TeamGitRefusal>(() => Diff(r, "cafe", "README.md"))).Code);

        // A tag whose hex name is no object's ID is not read either.
        if (string.IsNullOrWhiteSpace(AgentGit.Host(r.Repo, "rev-parse", "--disambiguate=beef")))
        {
            AgentGit.Host(r.Repo, "tag", "beef", r.Root);
            Assert.False((await Show(r, "beef")).Found);
        }

        // An annotated tag's ID opens its commit.
        var tag = AgentGit.Host(r.Repo, "rev-parse", "v2.0");
        Assert.Equal(r.Merge, (await Show(r, tag)).Sha);
    }

    // ------------------------------------------------------------------ base main, origin pushed to; the shell's fixtures

    private sealed record MainRig(TeamsFixture F, long TeamId, string Repo, string Root, Yawble.Core.Bus.MemberRef Ada);

    /// <summary>Team Alpha on the person's repository, base main, origin a bare folder that main was pushed to.</summary>
    private static async Task<MainRig> MainRigAsync(TeamsFixture f)
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
        return new MainRig(f, ada.TeamId, repo, root, ada);
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

    private static Task<CommitInfo> Show(MainRig r, string sha) => r.F.Repos.GitCommitShowAsync(r.TeamId, "Alpha", sha, Ct);

    /// <summary>Writes the real core's JSON for the shell's commit view test, when COMMIT_VIEW_FIXTURE_OUT names a folder.</summary>
    private static void Emit(string name, JsonObject json)
    {
        var dir = Environment.GetEnvironmentVariable("COMMIT_VIEW_FIXTURE_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
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


    [Fact]
    public async Task PushedOnlyAfterAPush_MainIsNeverListed_AndTheTeamBranchComesFirst()
    {
        using var f = new TeamsFixture();
        var r = await MainRigAsync(f);
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
        var r = await MainRigAsync(f);
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
        var r = await MainRigAsync(f);
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
    /// git's own message for a merge into main is "Merge branch 'feature/login'" (no "into main"). Into is the branch
    /// whose first-parent line holds the merge, so it is main, also once the team branch contains the merge.
    /// </summary>
    [Fact]
    public async Task AMergeMadeOnMain_WithGitsDefaultMessage_IsIntoMain_EvenWhenTheTeamBranchHasIt()
    {
        using var f = new TeamsFixture();
        var r = await MainRigAsync(f);
        AgentGit.Host(r.Repo, "switch", "-q", "-c", "feature/login", "main");
        Write(r.Repo, "login.txt", "login\n");
        Commit(r.Repo, "Login", "Person");
        AgentGit.Host(r.Repo, "switch", "-q", "main");
        var merge = Merge(r.Repo, "feature/login", null);
        Assert.Equal("Merge branch 'feature/login'", AgentGit.Host(r.Repo, "log", "-1", "--format=%s", merge));

        // Before the team branch has it: into main.
        Assert.Equal("main", (await Show(r, merge)).Merge!.IntoBranch);

        AgentGit.Host(r.Repo, "branch", "-f", "team/alpha", "main");
        var m = await Show(r, merge);
        Assert.Equal("feature/login", m.Merge!.FromBranch);
        Assert.Equal("main", m.Merge.IntoBranch);
        Emit("f1-merge-on-main", m.ToJson());

        // The team branch moved past main by its own commit still has the merge only by containing it: still main.
        AgentGit.Host(r.Repo, "switch", "-q", "team/alpha");
        Commit(r.Repo, "Team work after");
        AgentGit.Host(r.Repo, "switch", "-q", "main");
        Assert.Equal("main", (await Show(r, merge)).Merge!.IntoBranch);
    }


    /// <summary>
    /// A commit on the person's own branch (neither team/ nor members/) has where.branches empty and where.otherBranches
    /// listing feature/x, so the window can tell it from a deleted branch's commit, whose otherBranches is empty.
    /// otherBranches is always in the JSON, sorted, and never holds the base, the team branch or a member's branch.
    /// </summary>
    [Fact]
    public async Task ACommitOnThePersonsOwnBranch_IsInOtherBranches_ADeletedBranchsIsNot()
    {
        using var f = new TeamsFixture();
        var r = await MainRigAsync(f);
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
        var r = await MainRigAsync(f);
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
        var r = await MainRigAsync(f);
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
}
