using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// The Concierge's action tools. Fakes for the app's calls (ui, board, gh); git runs for real but only on
/// temp repositories (a bare "origin", a working repo, member clones), never on anything of the person's.
/// </summary>
public class ConciergeActionTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("yawble-cact-").FullName;
    private readonly TempDb _db = new();
    private readonly HostGit _git;
    private readonly PersonGitPush _pusher;
    /// <summary>The only global git config the tests' git sees: empty (the person's own hooks and helpers never apply).</summary>
    private string TestGitConfig => Path.Combine(_root, "test.gitconfig");
    private string Origin => Path.Combine(_root, "origin.git");
    private string Work => Path.Combine(_root, "work");
    private string Clone => Path.Combine(_root, "clone-ines");

    public ConciergeActionTests()
    {
        _git = new HostGit(Path.Combine(_root, "empty.gitconfig"));
        File.WriteAllText(TestGitConfig, "");
        _pusher = new PersonGitPush(globalConfig: TestGitConfig);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var f in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)) try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    // ---------------------------------------------------------------- rig

    private sealed class Rig
    {
        public List<(string Method, JsonObject? P)> Ui { get; } = [];
        public List<(string Method, JsonObject? P)> Board { get; } = [];
        public List<string[]> GhCalls { get; } = [];
        public string GhView = """{"number":7,"title":"Add x","headRefName":"members/ines","baseRefName":"main","state":"OPEN","isDraft":false}""";
        public JsonObject Env = new() { ["KEEP"] = "s3cret-old" };
        public ConfirmationBook Book = new();
        public required BusEngine Bus;
        public required ActionRepo Repo;
        public required ConciergeGit Git;
        public bool WithGh = true;

        /// <summary>The actions for one turn: by default a new turn of conversation "c1", started by the person at this computer.</summary>
        public ConciergeActions Actions(ConciergeTurn? turn = null) => new(Bus,
            (m, p, _) => { Ui.Add((m, p)); return Task.FromResult<JsonNode?>(m == "teams.env" ? Env.DeepClone() : new JsonObject()); },
            (m, p, _) => { Board.Add((m, p)); return Task.FromResult<JsonNode?>(new JsonObject { ["label"] = "B006" }); },
            Git, (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([Repo]), Book,
            WithGh ? (_, a, _) => { GhCalls.Add([.. a]); return Task.FromResult(new GitResult(0, a[1] switch { "view" => GhView, "list" => GhList, _ => "https://example.test/pr/7" }, "")); } : null,
            turn: turn ?? new ConciergeTurn("c1", byPerson: true, remote: false));

        public string GhList = """[{"number":7,"title":"Add x","headRefName":"members/ines"},{"number":9,"title":"Docs","headRefName":"members/ben"}]""";
    }

    private async Task<Rig> MakeRigAsync(params string[] clones)
    {
        var bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        await bus.StartAsync(Ct);
        await bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        Run(_root, "init", "--bare", "-b", "main", Origin);
        Run(_root, "clone", Origin, Work);
        Config(Work);
        Commit(Work, "base.txt", "base");
        Run(Work, "push", "origin", "HEAD:main");
        return new Rig { Bus = bus, Git = new ConciergeGit(_git, _pusher), Repo = new ActionRepo("main", Work, "main", clones) };
    }

    private static string Call(string tool, ConciergeActions a, JsonObject args) => Run(a, tool, args).GetAwaiter().GetResult();

    private static async Task<string> Run(ConciergeActions a, string tool, JsonObject args)
    {
        try { return await a.TryCallAsync(tool, args, Ct) ?? throw new InvalidOperationException("not an action tool"); }
        catch (ArgumentException e) { return "REFUSED: " + e.Message; }
    }

    private static string Token(string text) => System.Text.RegularExpressions.Regex.Match(text, "confirm=\"([0-9a-f]+)\"").Groups[1].Value;

    // ---------------------------------------------------------------- git helpers (temp repos only)

    /// <summary>The test's own git: hermetic (no inherited GIT_*, no system config, an empty global config).</summary>
    private string Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(key);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = TestGitConfig;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {e}");
        return o.Trim();
    }

    private void Config(string dir) { Run(dir, "config", "user.name", "T"); Run(dir, "config", "user.email", "t@example.test"); Run(dir, "config", "commit.gpgsign", "false"); }

    private string Commit(string dir, string file, string text)
    {
        File.WriteAllText(Path.Combine(dir, file), text);
        Run(dir, "add", "-A");
        Run(dir, "commit", "-m", text);
        return Run(dir, "rev-parse", "HEAD");
    }

    /// <summary>A branch off main with one commit; merged into main when asked.</summary>
    private string Branch(string name, bool merged)
    {
        Run(Work, "checkout", "-q", "-b", name, "main");
        var sha = Commit(Work, name.Replace('/', '-') + ".txt", name);
        Run(Work, "checkout", "-q", "main");
        if (merged) Run(Work, "merge", "--no-ff", "-q", "-m", "merge " + name, name);
        return sha;
    }

    private string Sha(string reference) => Run(Work, "rev-parse", reference);
    private bool Has(string branch) => Run(Work, "branch", "--list", branch).Length > 0;

    // ---------------------------------------------------------------- backlog and board

    [Fact]
    public async Task BacklogAndBoardActionsGoThroughTheBoard()
    {
        var rig = await MakeRigAsync();
        var a = rig.Actions();
        Assert.StartsWith("ok:", Call("backlog_edit", a, new() { ["item"] = "B006", ["title"] = "New", ["team"] = "alpha" }));
        Assert.StartsWith("ok:", Call("backlog_ready", a, new() { ["item"] = "B006" }));
        Assert.StartsWith("ok:", Call("backlog_dispatch", a, new() { ["item"] = "B006", ["team"] = "alpha" }));
        Assert.StartsWith("ok:", Call("backlog_dispatch", a, new() { ["item"] = "B006", ["new_team"] = "beta" }));
        Assert.StartsWith("ok:", Call("backlog_archive", a, new() { ["item"] = "B006" }));
        Assert.StartsWith("ok:", Call("backlog_restore", a, new() { ["item"] = "B006" }));
        Assert.StartsWith("ok:", Call("board_move", a, new() { ["card"] = 38, ["lane"] = "done", ["note"] = "shipped" }));
        Assert.StartsWith("ok:", Call("board_comment", a, new() { ["card"] = 38, ["text"] = "thanks" }));
        Assert.Equal(["backlog.edit", "backlog.edit", "backlog.dispatch", "backlog.dispatchNew", "backlog.archive", "backlog.restore", "board.move", "board.comment"],
            rig.Board.Select(b => b.Method));
        Assert.Equal(6, rig.Board[0].P!["id"]!.GetValue<long>());
        Assert.Equal("alpha", rig.Board[0].P!["team"]!.GetValue<string>());
        Assert.Equal("ready", rig.Board[1].P!["state"]!.GetValue<string>());
        Assert.NotNull(rig.Board[2].P!["teamId"]);
    }

    [Fact]
    public async Task BacklogAndBoardActionsRefuseWhatIsMissing()
    {
        var rig = await MakeRigAsync();
        var a = rig.Actions();
        Assert.Contains("give a new title", Call("backlog_edit", a, new() { ["item"] = "B006" }));
        Assert.Contains("is a backlog label", Call("backlog_ready", a, new() { ["item"] = "soon" }));
        Assert.Contains("there is no team 'nope'", Call("backlog_dispatch", a, new() { ["item"] = "B006", ["team"] = "nope" }));
        Assert.Contains("'card' is required", Call("board_move", a, new() { ["lane"] = "done" }));
        Assert.Empty(rig.Board);
    }

    // ---------------------------------------------------------------- confirm-before-destructive

    [Fact]
    public async Task DeleteShowsTheExactListFirst_ThenNeedsAYesInALaterTurn()
    {
        var rig = await MakeRigAsync();
        var shaOld = Branch("members/old", merged: true);
        Branch("members/live", merged: false);
        var turn1 = rig.Actions();

        var shown = Call("git_branches_delete", turn1, new() { ["team"] = "alpha" });
        Assert.StartsWith("NOT DONE", shown);
        Assert.Contains($"delete members/old at {shaOld}", shown);
        Assert.Contains("members/live", shown);
        Assert.Contains("KEPT, not merged", shown);
        Assert.True(Has("members/old")); // nothing deleted by asking

        var token = Token(shown);
        // The same turn cannot say yes for the person.
        Assert.Contains("has not said yes yet", Call("git_branches_delete", turn1, new() { ["team"] = "alpha", ["confirm"] = token }));
        Assert.True(Has("members/old"));

        var turn2 = rig.Actions(); // the person's yes: a later turn
        var done = Call("git_branches_delete", turn2, new() { ["team"] = "alpha", ["confirm"] = token });
        Assert.StartsWith("ok:", done);
        Assert.False(Has("members/old"));
        Assert.True(Has("members/live"));
        Assert.True(Has("main"));
        // The SHA is recorded for the restore, in the answer and in the log.
        Assert.Contains($"was {shaOld}; restore with: git branch members/old {shaOld}", done);
        Assert.Contains(shaOld, File.ReadAllText(Path.Combine(Work, ".git", ConciergeGit.DeletedLog)));
        // Restoring works from what was recorded.
        Run(Work, "branch", "members/old", shaOld);
        Assert.True(Has("members/old"));
        // One use only.
        Assert.StartsWith("NOT DONE", Call("git_branches_delete", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = token }));
    }

    [Fact]
    public async Task AYesDoesNotCoverAListThatChanged()
    {
        var rig = await MakeRigAsync();
        Branch("members/a", merged: true);
        var token = Token(Call("git_branches_delete", rig.Actions(), new() { ["team"] = "alpha" }));
        Branch("members/b", merged: true); // another merged branch appears after the list was shown
        var again = Call("git_branches_delete", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = token });
        Assert.StartsWith("NOT DONE", again);
        Assert.Contains("THE LIST CHANGED", again);
        Assert.Contains("members/b", again);
        Assert.True(Has("members/a") && Has("members/b"));
    }

    [Fact]
    public async Task NeverDeletesTheBaseABranchACloneHasCheckedOut_OrAnUnmergedOne()
    {
        // A member's clone with members/ines checked out (its HEAD is only read, never run in).
        var rig = await MakeRigAsync(Clone);
        Branch("members/ines", merged: true);
        Run(_root, "clone", Origin, Clone);
        Run(Clone, "checkout", "-q", "-b", "members/ines");
        Branch("members/unmerged", merged: false);

        var named = Call("git_branches_delete", rig.Actions(), new() { ["team"] = "alpha", ["branches"] = "members/ines, main, members/unmerged, members/ghost" });
        Assert.Contains("members/ines", named); Assert.Contains($"checked out in {Clone}", named);
        Assert.Contains("main: REFUSED, it is the base", named);
        Assert.Contains("members/unmerged", named); Assert.Contains("KEPT, not merged", named);
        Assert.Contains("members/ghost: REFUSED, no such branch", named);
        Assert.StartsWith("nothing to do", named); // nothing deletable: no token, no deletion
        Assert.True(Has("members/ines") && Has("members/unmerged") && Has("main"));

        // Naming the unmerged one explicitly makes it deletable (after the yes); the checked-out one still is not.
        var args = new JsonObject { ["team"] = "alpha", ["branches"] = "members/ines,members/unmerged", ["unmerged_ok"] = "members/unmerged" };
        var shown = Call("git_branches_delete", rig.Actions(), args);
        Assert.Contains("delete members/unmerged", shown); Assert.Contains("UNMERGED, you named it", shown);
        Assert.Contains("members/ines", shown); Assert.Contains("REFUSED, checked out", shown);
        args["confirm"] = Token(shown);
        Assert.StartsWith("ok:", Call("git_branches_delete", rig.Actions(), args));
        Assert.False(Has("members/unmerged"));
        Assert.True(Has("members/ines"));
    }

    [Fact]
    public async Task ABranchCheckedOutBetweenTheListAndTheYesIsKept()
    {
        var rig = await MakeRigAsync(Clone);
        Branch("members/ines", merged: true);
        Run(_root, "clone", Origin, Clone);
        var token = Token(Call("git_branches_delete", rig.Actions(), new() { ["team"] = "alpha" }));
        Run(Clone, "checkout", "-q", "-b", "members/ines");
        var again = Call("git_branches_delete", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = token });
        // The list is different now: its only branch is checked out, so nothing is deletable and the yes runs nothing.
        Assert.StartsWith("nothing to do", again);
        Assert.Contains("REFUSED, checked out", again);
        Assert.True(Has("members/ines"));
    }

    [Fact]
    public async Task DeleteOnOriginRecordsTheShaToo()
    {
        var rig = await MakeRigAsync();
        var sha = Branch("members/shipped", merged: true);
        Run(Work, "push", "origin", "members/shipped", "main");
        var args = new JsonObject { ["team"] = "alpha", ["branches"] = "members/shipped", ["remote"] = true };
        var shown = Call("git_branches_delete", rig.Actions(), args);
        Assert.Contains($"delete origin/members/shipped at {sha}", shown);
        args["confirm"] = Token(shown);
        var done = Call("git_branches_delete", rig.Actions(), args);
        Assert.Contains($"git push origin {sha}:refs/heads/members/shipped", done);
        Assert.Equal("", Run(Origin, "branch", "--list", "members/shipped"));
    }

    [Fact]
    public async Task ARemoteBranchThatMovedOnOriginSinceTheListIsKept()
    {
        var rig = await MakeRigAsync();
        Branch("members/shipped", merged: true);
        Run(Work, "push", "origin", "members/shipped", "main");
        var args = new JsonObject { ["team"] = "alpha", ["branches"] = "members/shipped", ["remote"] = true };
        var shown = Call("git_branches_delete", rig.Actions(), args);
        args["confirm"] = Token(shown);
        // Someone else pushes to the branch on origin after the list was shown (this clone's origin/ ref is not refreshed).
        var other = Path.Combine(_root, "other");
        Run(_root, "clone", "-q", Origin, other);
        Run(other, "checkout", "-q", "members/shipped");
        Run(other, "-c", "user.name=o", "-c", "user.email=o@e.x", "commit", "-q", "--allow-empty", "-m", "newer work");
        Run(other, "push", "-q", "origin", "members/shipped");
        var done = Call("git_branches_delete", rig.Actions(), args);
        Assert.Contains("origin/members/shipped: kept", done);
        Assert.NotEqual("", Run(Origin, "branch", "--list", "members/shipped"));
    }

    [Fact]
    public async Task PruneListsFirstAndRecordsShas()
    {
        var rig = await MakeRigAsync();
        var sha = Branch("members/gone", merged: true);
        Run(Work, "push", "origin", "members/gone");
        Run(Origin, "branch", "-D", "members/gone"); // gone on origin, still remembered here
        var shown = Call("git_prune", rig.Actions(), new() { ["team"] = "alpha" });
        Assert.Contains("origin/members/gone", shown);
        Assert.Contains(sha, shown);
        Assert.Contains("origin/members/gone", Run(Work, "branch", "-r"));
        var done = Call("git_prune", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = Token(shown) });
        Assert.StartsWith("ok:", done);
        Assert.Contains(sha, done);
        Assert.DoesNotContain("members/gone", Run(Work, "branch", "-r"));
        Assert.True(Has("members/gone")); // the local branch is never touched
    }

    // ---------------------------------------------------------------- push, sync, PRs

    [Fact]
    public async Task PushNeverForces_AndNamesTheSha()
    {
        var rig = await MakeRigAsync();
        var sha = Branch("members/p", merged: false);
        var a = rig.Actions();
        var ok = Call("git_push_branch", a, new() { ["team"] = "alpha", ["branch"] = "members/p" });
        Assert.Contains($"pushed members/p at {sha}", ok);
        Assert.Equal(sha, Run(Origin, "rev-parse", "members/p"));

        // origin moves on; a rewritten local branch is a non-fast-forward: refused, origin unchanged.
        Run(Work, "checkout", "-q", "members/p");
        Run(Work, "reset", "-q", "--hard", "main");
        Commit(Work, "other.txt", "other");
        Run(Work, "checkout", "-q", "main");
        var refused = Call("git_push_branch", a, new() { ["team"] = "alpha", ["branch"] = "members/p" });
        Assert.StartsWith("REFUSED", refused);
        Assert.Contains("never forces", refused);
        Assert.Equal(sha, Run(Origin, "rev-parse", "members/p"));
        Assert.StartsWith("REFUSED", Call("git_push_branch", a, new() { ["team"] = "alpha", ["branch"] = "nope" }));
    }

    [Fact]
    public async Task SyncMergesTheBase_RefusesConflictsAndACheckedOutBranch()
    {
        var rig = await MakeRigAsync(Clone);
        var tip = Branch("members/s", merged: false);
        Commit(Work, "later.txt", "later"); // main moves on
        var a = rig.Actions();
        var ok = Call("git_sync_branch", a, new() { ["team"] = "alpha", ["branch"] = "members/s" });
        Assert.StartsWith("ok:", ok);
        Assert.Contains(tip, ok); // the old tip is named so it can be restored
        Assert.Equal(0, int.Parse(Run(Work, "rev-list", "--count", "main", "^members/s")));
        Assert.Contains("already has everything", Call("git_sync_branch", a, new() { ["team"] = "alpha", ["branch"] = "members/s" }));

        // A conflict changes nothing.
        Run(Work, "checkout", "-q", "-b", "members/c", "main~1");
        Commit(Work, "later.txt", "different");
        Run(Work, "checkout", "-q", "main");
        var before = Sha("members/c");
        var conflict = Call("git_sync_branch", a, new() { ["team"] = "alpha", ["branch"] = "members/c" });
        Assert.Contains("does not merge cleanly", conflict);
        Assert.Contains("later.txt", conflict);
        Assert.Equal(before, Sha("members/c"));

        // A branch some clone has checked out is left alone.
        Run(_root, "clone", Origin, Clone);
        Run(Clone, "checkout", "-q", "-b", "members/s2");
        Run(Work, "branch", "members/s2", "main~1");
        var held = Call("git_sync_branch", a, new() { ["team"] = "alpha", ["branch"] = "members/s2" });
        Assert.Contains("checked out", held);
        Assert.StartsWith("REFUSED", Call("git_sync_branch", a, new() { ["team"] = "alpha", ["branch"] = "main" }));
    }

    [Fact]
    public async Task PullRequests_OpenDirectly_MergeAfterYes_NeverWithoutGh()
    {
        var rig = await MakeRigAsync();
        Run(Work, "remote", "set-url", "origin", "https://github.com/acme/app.git"); // pull requests are GitHub's; gh is a fake
        var a = rig.Actions();
        var opened = Call("git_pr_open", a, new() { ["team"] = "alpha", ["branch"] = "members/ines", ["title"] = "Add x" });
        Assert.StartsWith("ok:", opened);
        Assert.Equal(["pr", "create", "--head", "members/ines", "--base", "main", "--title", "Add x", "--body", ""], rig.GhCalls[0]);

        var shown = Call("git_pr_merge", a, new() { ["team"] = "alpha", ["pr"] = 7 });
        Assert.StartsWith("NOT DONE", shown);
        Assert.DoesNotContain(rig.GhCalls, c => c[1] == "merge");
        var done = Call("git_pr_merge", rig.Actions(), new() { ["team"] = "alpha", ["pr"] = 7, ["confirm"] = Token(shown) });
        Assert.StartsWith("ok: merged", done);
        Assert.Equal(["pr", "merge", "7", "--merge"], rig.GhCalls.Last()); // no --admin, no --delete-branch

        rig.GhView = rig.GhView.Replace("\"main\"", "\"release\"");
        Assert.Contains("targets release", Call("git_pr_merge", rig.Actions(), new() { ["team"] = "alpha", ["pr"] = 7 }));

        rig.WithGh = false;
        var none = Call("git_pr_open", rig.Actions(), new() { ["team"] = "alpha", ["branch"] = "b", ["title"] = "t" });
        Assert.Contains("gh is not available", none);
        Assert.Contains("git_push_branch", none); // the closest thing it can do
    }

    [Fact]
    public async Task PullRequests_ForARepositoryNotOnGitHub_AreRefusedWithWhatToDoInstead_AndGhNeverRuns()
    {
        var rig = await MakeRigAsync();
        var local = Call("git_pr_open", rig.Actions(), new() { ["team"] = "alpha", ["branch"] = "members/ines", ["title"] = "Add x" });
        Assert.Contains("only for repositories hosted on GitHub", local);
        Assert.Contains("a local folder", local);

        Run(Work, "remote", "set-url", "origin", "https://gitlab.com/acme/app.git");
        var gitlab = Call("git_pr_open", rig.Actions(), new() { ["team"] = "alpha", ["branch"] = "members/ines", ["title"] = "Add x" });
        Assert.Contains("on gitlab.com", gitlab);
        Assert.Contains("Push the branch", gitlab);
        Assert.Contains("on gitlab.com", Call("git_pr_merge", rig.Actions(), new() { ["team"] = "alpha", ["pr"] = 7 }));

        Run(Work, "remote", "remove", "origin");
        Assert.Contains("has no remote", Call("git_pr_open", rig.Actions(), new() { ["team"] = "alpha", ["branch"] = "members/ines", ["title"] = "Add x" }));
        Assert.Empty(rig.GhCalls);
    }

    // ---------------------------------------------------------------- doctor fixes

    private static ConciergeActions DoctorActions(Rig rig, List<JsonObject> calls, ConciergeTurn? turn = null) => new(rig.Bus,
        (m, p, _) =>
        {
            calls.Add(new JsonObject { ["method"] = m, ["p"] = p?.DeepClone() });
            var fix = p?["fix"]?.GetValue<string>();
            var confirmed = p?["confirm"]?.GetValue<bool>() == true;
            return Task.FromResult<JsonNode?>(fix == "remove-old" && !confirmed
                ? new JsonObject { ["done"] = false, ["needsConfirm"] = true, ["message"] = "Removes 2 old folders." }
                : new JsonObject { ["done"] = true, ["needsConfirm"] = false, ["message"] = fix == "remove-old" ? "Removed 2." : "Logs rotated." });
        },
        (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()), rig.Git, (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([rig.Repo]), rig.Book,
        turn: turn ?? new ConciergeTurn("c1", byPerson: true, remote: false));

    [Fact]
    public async Task DoctorFix_RunsAPlainFixAtOnce_AndAsksTheYesForADestructiveOne()
    {
        var rig = await MakeRigAsync();
        var calls = new List<JsonObject>();

        Assert.Equal("ok: Logs rotated.", Call("doctor_fix", DoctorActions(rig, calls), new() { ["fix"] = "rotate-logs" }));

        var shown = Call("doctor_fix", DoctorActions(rig, calls), new() { ["fix"] = "remove-old" });
        Assert.StartsWith("NOT DONE", shown);
        Assert.Contains("Removes 2 old folders.", shown);
        Assert.DoesNotContain(calls, c => c["p"]?["confirm"] is not null); // nothing ran with confirm yet

        // the same turn cannot say yes for the person
        var same = DoctorActions(rig, calls);
        var first = Call("doctor_fix", same, new() { ["fix"] = "remove-old" });
        Assert.Contains("has not said yes yet", Call("doctor_fix", same, new() { ["fix"] = "remove-old", ["confirm"] = Token(first) }));
        Assert.DoesNotContain(calls, c => c["p"]?["confirm"] is not null);

        // a later turn of the person, with the token, runs it with confirm
        Assert.Equal("ok: Removed 2.", Call("doctor_fix", DoctorActions(rig, calls), new() { ["fix"] = "remove-old", ["confirm"] = Token(shown) }));
        Assert.Contains(calls, c => c["p"]?["confirm"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task DoctorFix_IsRefusedInAReviewTurn_AndOverTheCloudLink_AndRunsNothing()
    {
        var rig = await MakeRigAsync();
        var calls = new List<JsonObject>();
        Assert.Contains("not started by a message from the person", Call("doctor_fix", DoctorActions(rig, calls, new ConciergeTurn("c1", byPerson: false, remote: false)), new() { ["fix"] = "rotate-logs" }));
        Assert.Contains("only for the person at this computer", Call("doctor_fix", DoctorActions(rig, calls, new ConciergeTurn("c1", byPerson: true, remote: true)), new() { ["fix"] = "rotate-logs" }));
        Assert.Empty(calls);
    }

    private sealed class NoSystem : Yawble.Core.Ops.IDoctorSystem
    {
        public string QuickCheck() => "ok";
        public IReadOnlyList<string> AppliedMigrations() => [];
        public IReadOnlyList<string> ShippedMigrations() => [];
        public long FreeBytesOnDataDrive() => long.MaxValue;
        public IReadOnlyDictionary<string, long> TeamFolderSizes() => new Dictionary<string, long>();
        public long LogBytes() => 0;
        public Yawble.Core.Ops.SandboxState Sandbox() => new(null, "done", null, null);
        public Yawble.Core.Ops.LinkState Link() => new("off", null);
    }

    private sealed class NoConnections : Yawble.Core.Ops.IConnectionSource
    {
        public IReadOnlyList<Yawble.Core.Ops.ConnectionInfo> List() => [];
    }

    /// <summary>doctor_fix wired to the real doctor through ops.doctorFix (DoctorRpc), over an in-memory store and ledger.</summary>
    private static (Func<ConciergeActions> Actions, Tests.Ops.MemoryCredentialStore Store, Tests.Ops.MemoryLedger Ledger, List<JsonObject> Calls) RealDoctor(Rig rig, params string[] orphans)
    {
        var store = new Tests.Ops.MemoryCredentialStore();
        var ledger = new Tests.Ops.MemoryLedger();
        foreach (var k in orphans) { store.Set(k, "SECRET"); ledger.Record(k); }
        var rpc = new Yawble.Core.Ops.DoctorRpc(new Yawble.Core.Ops.DoctorService(new NoSystem(), null, null, new NoConnections(), store, null, null, null, ledger));
        var calls = new List<JsonObject>();
        ConciergeActions Actions() => new(rig.Bus,
            (m, p, ct) => { calls.Add(new JsonObject { ["method"] = m, ["p"] = p?.DeepClone() }); return rpc.DispatchAsync(m, p, ct); },
            (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()), rig.Git, (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([rig.Repo]), rig.Book,
            turn: new ConciergeTurn("c1", byPerson: true, remote: false));
        return (Actions, store, ledger, calls);
    }

    [Fact]
    public async Task DoctorFix_AnOrphanThatAppearsAfterTheYesVoidsIt_NothingIsRemovedAndTheNewListIsAsked()
    {
        var rig = await MakeRigAsync();
        var (actions, store, ledger, calls) = RealDoctor(rig, "connection/a/token", "connection/b/token");
        var fix = new JsonObject { ["fix"] = Yawble.Core.Ops.DoctorService.RemoveOrphansFix };

        var shown = Call("doctor_fix", actions(), (JsonObject)fix.DeepClone());
        Assert.StartsWith("NOT DONE", shown);
        Assert.Contains("connection/a/token, connection/b/token", shown);

        store.Set("connection/c/token", "SECRET");
        ledger.Record("connection/c/token");

        var yes = (JsonObject)fix.DeepClone();
        yes["confirm"] = Token(shown);
        var again = Call("doctor_fix", actions(), yes);
        Assert.StartsWith("NOT DONE", again);
        Assert.Contains("THE LIST CHANGED", again);
        Assert.Contains("connection/c/token", again);
        Assert.DoesNotContain(calls, c => c["p"]?["confirm"] is not null);
        Assert.NotNull(store.Get("connection/a/token"));
        Assert.NotNull(store.Get("connection/b/token"));
        Assert.NotNull(store.Get("connection/c/token"));

        var second = (JsonObject)fix.DeepClone();
        second["confirm"] = Token(again);
        Assert.StartsWith("ok:", Call("doctor_fix", actions(), second));
        Assert.Null(store.Get("connection/c/token"));
    }

    [Fact]
    public async Task DoctorFix_AnUnchangedListRemovesExactlyTheKeysShown()
    {
        var rig = await MakeRigAsync();
        var (actions, store, _, calls) = RealDoctor(rig, "connection/a/token", "connection/b/token");
        store.Set("connection/foreign/token", "SECRET"); // not issued here: never offered, never removed
        var fix = new JsonObject { ["fix"] = Yawble.Core.Ops.DoctorService.RemoveOrphansFix };

        var shown = Call("doctor_fix", actions(), (JsonObject)fix.DeepClone());
        var yes = (JsonObject)fix.DeepClone();
        yes["confirm"] = Token(shown);
        Assert.Equal("ok: removed 2 unused credential(s)", Call("doctor_fix", actions(), yes));
        Assert.Null(store.Get("connection/a/token"));
        Assert.Null(store.Get("connection/b/token"));
        Assert.NotNull(store.Get("connection/foreign/token"));
        // the confirmed call carries the list that was shown, so the doctor can refuse a list that moved in between
        var confirmed = calls.Single(c => c["p"]?["confirm"] is not null);
        Assert.Equal(["connection/a/token", "connection/b/token"], confirmed["p"]!["expect"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ---------------------------------------------------------------- team settings

    [Fact]
    public async Task ArchiveAndDeleteNeedAYes_UnarchiveDoesNot()
    {
        var rig = await MakeRigAsync();
        var shown = Call("team_archive", rig.Actions(), new() { ["team"] = "alpha" });
        Assert.StartsWith("NOT DONE", shown);
        Assert.Empty(rig.Ui);
        Assert.StartsWith("ok:", Call("team_archive", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = Token(shown) }));
        Assert.Equal("teams.archive", rig.Ui.Single().Method);

        Assert.StartsWith("ok:", Call("team_unarchive", rig.Actions(), new() { ["team"] = "alpha" }));

        var del = Call("team_delete", rig.Actions(), new() { ["team"] = "alpha" });
        Assert.StartsWith("NOT DONE", del);
        // A token for one tool does not work for another, and using it so voids it: the delete must be shown and said yes to again.
        Assert.StartsWith("NOT DONE", Call("team_archive", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = Token(del) }));
        var voided = Call("team_delete", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = Token(del) });
        Assert.StartsWith("NOT DONE", voided);
        Assert.Contains("unknown or expired", voided);
        Assert.DoesNotContain(rig.Ui, u => u.Method == "teams.delete");
        Assert.StartsWith("ok:", Call("team_delete", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = Token(voided) }));
        var call = rig.Ui.Last();
        Assert.Equal("teams.delete", call.Method);
        Assert.Equal("alpha", call.P!["confirm"]!.GetValue<string>());
    }

    [Fact]
    public async Task EnvSet_NeverEchoesAValue_AndReplacingNeedsAYes()
    {
        var rig = await MakeRigAsync();
        var created = Call("team_env_set", rig.Actions(), new() { ["team"] = "alpha", ["name"] = "NEW_VAR", ["value"] = "tok-123456" });
        Assert.StartsWith("ok:", created);
        Assert.DoesNotContain("tok-123456", created);
        var set = rig.Ui.Last(u => u.Method == "teams.setEnv").P!["entries"]!.AsArray();
        Assert.Contains(set, e => e!["name"]!.GetValue<string>() == "KEEP" && e["value"]!.GetValue<string>() == "s3cret-old"); // others preserved
        Assert.Contains(set, e => e!["name"]!.GetValue<string>() == "NEW_VAR");

        var args = new JsonObject { ["team"] = "alpha", ["name"] = "KEEP", ["value"] = "s3cret-new" };
        var shown = Call("team_env_set", rig.Actions(), args);
        Assert.StartsWith("NOT DONE", shown);
        Assert.DoesNotContain("s3cret", shown);
        args["confirm"] = Token(shown);
        var done = Call("team_env_set", rig.Actions(), args);
        Assert.StartsWith("ok:", done);
        Assert.DoesNotContain("s3cret", done);
        Assert.StartsWith("REFUSED", Call("team_env_set", rig.Actions(), new() { ["team"] = "alpha", ["name"] = "ABSENT", ["remove"] = true }));
    }

    [Fact]
    public async Task AgentChange_KeepsTheRestOfTheMember()
    {
        var rig = await MakeRigAsync();
        await rig.Bus.AddMemberAsync((await rig.Bus.TeamsAsync(Ct)).Single().Id, "Ines", "claude", Ct);
        Assert.StartsWith("ok:", Call("team_agent_change", rig.Actions(), new() { ["team"] = "alpha", ["member"] = "ines", ["cli"] = "codex" }));
        var u = rig.Ui.Single(x => x.Method == "members.update").P!;
        Assert.Equal("codex", u["cli"]!.GetValue<string>());
        Assert.Equal("default", u["credential"]!.GetValue<string>());
        Assert.StartsWith("REFUSED", Call("team_agent_change", rig.Actions(), new() { ["team"] = "alpha", ["member"] = "nobody", ["cli"] = "codex" }));
    }

    // ---------------------------------------------------------------- registration and output hygiene

    [Fact]
    public async Task ToolsAreOfferedThroughTheConciergeEndpoint_AndNoOutputHoldsASecret()
    {
        var rig = await MakeRigAsync();
        var store = new ConciergeStore(_db.ConnectionString);
        var tools = new ConciergeTools(rig.Bus, store, (_, _, _) => Task.FromResult<JsonNode?>(null), () => null, "c1", null, rig.Actions());
        var list = JsonNode.Parse((await tools.HandleAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), Ct)).Json!)!["result"]!["tools"]!.AsArray();
        var names = list.Select(t => t!["name"]!.GetValue<string>()).ToList();
        foreach (var n in ConciergeActions.ToolNames) Assert.Contains(n, names);
        Assert.Contains("team_list", names); // the existing tools are still there
        Assert.Equal(names.Count, names.Distinct().Count());

        // Called through the endpoint: a refusal says why.
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "git_push_branch", ["arguments"] = new JsonObject { ["team"] = "alpha", ["branch"] = "ghost" } } };
        var reply = JsonNode.Parse((await tools.HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct)).Json!)!["result"]!;
        Assert.True(reply["isError"]!.GetValue<bool>());
        Assert.Contains("has no branch ghost", reply["content"]![0]!["text"]!.GetValue<string>());

        // A remote URL's credential is redacted from whatever git says. The origin is a file:// address of a LOCAL folder
        // that does not exist, whose path holds a URL with a credential: git quotes it in its error, nothing on the
        // network (no host, no SMB share) is tried.
        var local = Path.Combine(_root, "no-such").Replace('\\', '/');
        Run(Work, "remote", "set-url", "origin", "file://" + (local.StartsWith('/') ? "" : "/") + local + "/https://user:hunter2@x.invalid/none.git");
        Branch("members/x", merged: false);
        var failed = Call("git_push_branch", rig.Actions(), new() { ["team"] = "alpha", ["branch"] = "members/x" });
        Assert.StartsWith("REFUSED", failed);
        Assert.Contains("https://***@x.invalid", failed); // git did quote it, and it was redacted
        Assert.DoesNotContain("hunter2", failed);
    }

    // ---------------------------------------------------------------- Cloud Link: what stays on the computer

    [Fact]
    public async Task ATurnFromTheCloudLinkIsRefusedEveryLocalOnlyAction_SaysWhy_AndChangesNothing()
    {
        var rig = await MakeRigAsync();
        var sha = Branch("members/old", merged: true);
        Run(Work, "push", "origin", "members/old");
        var originBefore = Run(Origin, "for-each-ref", "--format=%(refname) %(objectname)");
        var remote = new ConciergeTurn("c1", byPerson: true, remote: true);
        var cases = new (string Tool, JsonObject Args)[]
        {
            ("git_branches_delete", new() { ["team"] = "alpha", ["branches"] = "members/old", ["remote"] = true }),
            ("git_prune", new() { ["team"] = "alpha" }),
            ("git_push_branch", new() { ["team"] = "alpha", ["branch"] = "members/old" }),
            ("git_sync_branch", new() { ["team"] = "alpha", ["branch"] = "members/old" }),
            ("git_pr_open", new() { ["team"] = "alpha", ["branch"] = "members/old", ["title"] = "t" }),
            ("git_pr_merge", new() { ["team"] = "alpha", ["pr"] = 7 }),
            ("team_archive", new() { ["team"] = "alpha" }),
            ("team_delete", new() { ["team"] = "alpha" }),
            ("team_env_set", new() { ["team"] = "alpha", ["name"] = "NEW_VAR", ["value"] = "v" }),
            ("team_env_set", new() { ["team"] = "alpha", ["name"] = "KEEP", ["remove"] = true }),
        };
        foreach (var (tool, args) in cases)
        {
            var said = Call(tool, rig.Actions(remote), args);
            Assert.StartsWith("REFUSED", said);
            Assert.Contains($"{tool} is only for the person at this computer", said);
            Assert.Contains("Cloud Link", said);
            Assert.DoesNotContain("confirm=", said); // not even a list to say yes to
        }
        Assert.Empty(rig.Ui);
        Assert.Empty(rig.GhCalls);
        Assert.True(Has("members/old"));
        Assert.Equal(sha, Sha("members/old"));
        Assert.Equal(originBefore, Run(Origin, "for-each-ref", "--format=%(refname) %(objectname)"));

        // Backlog and board actions stay allowed from the Cloud Link.
        Assert.StartsWith("ok:", Call("backlog_ready", rig.Actions(remote), new() { ["item"] = "B006" }));
        Assert.StartsWith("ok:", Call("board_comment", rig.Actions(remote), new() { ["card"] = 38, ["text"] = "from the phone" }));
        // And the same actions from the computer still work.
        Assert.StartsWith("ok:", Call("git_push_branch", rig.Actions(), new() { ["team"] = "alpha", ["branch"] = "members/old" }));
        Assert.StartsWith("NOT DONE", Call("team_archive", rig.Actions(), new() { ["team"] = "alpha" }));
    }

    [Fact]
    public async Task AYesGivenThroughTheCloudLinkDoesNotRedeemAListShownAtTheComputer()
    {
        var rig = await MakeRigAsync();
        var shown = Call("team_delete", rig.Actions(), new() { ["team"] = "alpha" });
        var remoteYes = Call("team_delete", rig.Actions(new ConciergeTurn("c1", byPerson: true, remote: true)), new() { ["team"] = "alpha", ["confirm"] = Token(shown) });
        Assert.StartsWith("REFUSED", remoteYes);
        Assert.Contains("Cloud Link", remoteYes);
        Assert.DoesNotContain(rig.Ui, u => u.Method == "teams.delete");
        // The person at the computer can still say yes to it.
        Assert.StartsWith("ok:", Call("team_delete", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = Token(shown) }));
    }

    // ---------------------------------------------------------------- the yes: this conversation, the person, in time

    [Fact]
    public async Task AConfirmationWorksOnlyInTheConversationThatShowedIt_AndAnotherConversationVoidsIt()
    {
        var rig = await MakeRigAsync();
        var shown = Call("team_archive", rig.Actions(new ConciergeTurn("c1", true, false)), new() { ["team"] = "alpha" });
        var elsewhere = Call("team_archive", rig.Actions(new ConciergeTurn("c2", true, false)), new() { ["team"] = "alpha", ["confirm"] = Token(shown) });
        Assert.StartsWith("NOT DONE", elsewhere);
        Assert.Contains("another conversation", elsewhere);
        Assert.Empty(rig.Ui);
        // Tried elsewhere, it is void even where it was shown.
        var back = Call("team_archive", rig.Actions(new ConciergeTurn("c1", true, false)), new() { ["team"] = "alpha", ["confirm"] = Token(shown) });
        Assert.StartsWith("NOT DONE", back);
        Assert.Empty(rig.Ui);
        // The list shown in c2 is c2's own, and c2's next person turn can say yes to it.
        Assert.StartsWith("ok:", Call("team_archive", rig.Actions(new ConciergeTurn("c2", true, false)), new() { ["team"] = "alpha", ["confirm"] = Token(elsewhere) }));
        Assert.Equal("teams.archive", rig.Ui.Single().Method);
    }

    [Fact]
    public async Task AReviewTurnWithoutAMessageFromThePersonCannotSayYes_TheirNextMessageCan()
    {
        var rig = await MakeRigAsync();
        var shown = Call("team_delete", rig.Actions(new ConciergeTurn("c1", true, false)), new() { ["team"] = "alpha" });
        var review = Call("team_delete", rig.Actions(new ConciergeTurn("c1", byPerson: false, remote: false)), new() { ["team"] = "alpha", ["confirm"] = Token(shown) });
        Assert.StartsWith("REFUSED", review);
        Assert.Contains("not started by a message from the person", review);
        Assert.DoesNotContain(rig.Ui, u => u.Method == "teams.delete");
        Assert.StartsWith("ok:", Call("team_delete", rig.Actions(new ConciergeTurn("c1", true, false)), new() { ["team"] = "alpha", ["confirm"] = Token(shown) }));
    }

    [Fact]
    public async Task AReviewTurnRunsNoActionAtAll_TheReasonIsSaid_AndAPersonTurnStillDoes()
    {
        var rig = await MakeRigAsync();
        var sha = Branch("members/r", merged: true);
        var review = new ConciergeTurn("c1", byPerson: false, remote: false);
        var cases = new (string Tool, JsonObject Args)[]
        {
            ("git_push_branch", new() { ["team"] = "alpha", ["branch"] = "members/r" }),
            ("git_sync_branch", new() { ["team"] = "alpha", ["branch"] = "members/r" }),
            ("git_pr_open", new() { ["team"] = "alpha", ["branch"] = "members/r", ["title"] = "t" }),
            ("git_pr_merge", new() { ["team"] = "alpha", ["pr"] = 7 }),
            ("git_branches_delete", new() { ["team"] = "alpha" }),
            ("git_prune", new() { ["team"] = "alpha" }),
            ("team_env_set", new() { ["team"] = "alpha", ["name"] = "NEW_VAR", ["value"] = "v" }),
            ("team_archive", new() { ["team"] = "alpha" }),
            ("team_delete", new() { ["team"] = "alpha" }),
            ("team_unarchive", new() { ["team"] = "alpha" }),
            ("team_agent_change", new() { ["team"] = "alpha", ["member"] = "Manager", ["cli"] = "codex" }),
            ("backlog_dispatch", new() { ["item"] = "B006", ["team"] = "alpha" }),
            ("backlog_edit", new() { ["item"] = "B006", ["title"] = "x" }),
            ("board_move", new() { ["card"] = 38, ["lane"] = "done" }),
        };
        foreach (var (tool, args) in cases)
        {
            var said = Call(tool, rig.Actions(review), args);
            Assert.StartsWith("REFUSED", said);
            Assert.Contains("not started by a message from the person", said);
            Assert.Contains(tool, said);
        }
        Assert.Empty(rig.Ui);
        Assert.Empty(rig.Board);
        Assert.Empty(rig.GhCalls);
        Assert.Equal("", Run(Origin, "for-each-ref", "--format=%(objectname)", "refs/heads/members/r"));
        Assert.Equal(sha, Sha("members/r"));

        // The person's own turn runs them.
        Assert.Contains("pushed members/r", Call("git_push_branch", rig.Actions(), new() { ["team"] = "alpha", ["branch"] = "members/r" }));
        Assert.StartsWith("ok:", Call("team_env_set", rig.Actions(), new() { ["team"] = "alpha", ["name"] = "NEW_VAR", ["value"] = "v" }));
        Assert.StartsWith("ok:", Call("board_move", rig.Actions(), new() { ["card"] = 38, ["lane"] = "done" }));
    }

    [Fact]
    public async Task TheOlderConciergeToolsActOnlyInAPersonsTurn_AndTheLocalOnlyOnesOnlyAtTheComputer()
    {
        var rig = await MakeRigAsync();
        var store = new ConciergeStore(_db.ConnectionString);
        store.Create("c1", "t", "claude", null);
        var calls = new List<string>();
        ConciergeTools Tools(ConciergeTurn turn) => new(rig.Bus, store, (m, _, _) =>
            {
                lock (calls) calls.Add(m);
                return Task.FromResult<JsonNode?>(new JsonObject { ["seq"] = 5, ["label"] = "B001", ["name"] = "docs", ["path"] = "p", ["teamId"] = 2 });
            }, () => null, "c1", null, rig.Actions(turn), turn.Remote, turn);
        var other = Path.Combine(_root, "someone-elses-repo");
        var acting = new (string Tool, JsonObject Args)[]
        {
            ("tell", new() { ["team"] = "alpha", ["instruction"] = "do it" }),
            ("answer", new() { ["team"] = "alpha", ["question"] = 1, ["answer"] = "yes" }),
            ("team_create", new() { ["name"] = "beta", ["repo_path"] = other }),
            ("team_pause", new() { ["team"] = "alpha" }),
            ("team_resume", new() { ["team"] = "alpha" }),
            ("workflow_nudge", new() { ["team"] = "alpha", ["workflow"] = 1 }),
            ("workflow_close", new() { ["team"] = "alpha", ["workflow"] = 1 }),
            ("backlog_add", new() { ["title"] = "steered" }),
            ("team_repo_add", new() { ["team"] = "alpha", ["name"] = "docs", ["repo_path"] = other }),
            ("team_repo_remove", new() { ["team"] = "alpha", ["name"] = "docs" }),
            ("backup_now", new()),
        };

        // A review turn (a watch's follow-up: no message from the person) acts on nothing.
        var review = new ConciergeTurn("c1", byPerson: false, remote: false);
        foreach (var (tool, args) in acting)
        {
            var said = await CallTool(Tools(review), tool, args);
            Assert.Contains("not started by a message from the person", said);
            Assert.Contains(tool, said);
        }
        Assert.Empty(calls);
        Assert.Single(await rig.Bus.TeamsAsync(Ct));
        // It may still read, keep its notes and its watches, and recommend.
        Assert.DoesNotContain("refused", await CallTool(Tools(review), "team_list", []));
        Assert.StartsWith("ok", await CallTool(Tools(review), "notes_write", new() { ["text"] = "alpha is waiting for a yes" }));
        Assert.StartsWith("ok: watch", await CallTool(Tools(review), "watch", new() { ["team"] = "alpha", ["what"] = "x", ["hours"] = 1 }));
        Assert.Empty(calls);

        // Through the Cloud Link: making a team, adding or removing a repository and closing a workflow stay on the computer.
        var remote = new ConciergeTurn("c1", byPerson: true, remote: true);
        foreach (var tool in new[] { "team_create", "team_repo_add", "team_repo_remove", "workflow_close" })
            Assert.Contains("only for the person at this computer", await CallTool(Tools(remote), tool, acting.Single(a => a.Tool == tool).Args));
        Assert.Empty(calls);
        // ... while tell, answer, pause/resume and backlog_add stay allowed for a remote person.
        foreach (var tool in new[] { "tell", "answer", "team_pause", "team_resume", "backlog_add" })
            Assert.StartsWith("ok", await CallTool(Tools(remote), tool, acting.Single(a => a.Tool == tool).Args));
        Assert.Equal(["bus.tellManager", "bus.answer", "teams.pause", "teams.resume", "backlog.add"], calls);

        // At the computer, the person's own turn runs the local-only ones.
        var local = new ConciergeTurn("c1", byPerson: true, remote: false);
        Assert.StartsWith("ok", await CallTool(Tools(local), "team_repo_remove", new() { ["team"] = "alpha", ["name"] = "docs" }));
        Assert.StartsWith("ok", await CallTool(Tools(local), "workflow_close", new() { ["team"] = "alpha", ["workflow"] = 1 }));
        Assert.Equal(["teams.removeRepo", "bus.closeWorkflow"], calls.Skip(5));
    }

    [Fact]
    public async Task AConfirmationExpiresAfterTenMinutes_AndIsUsedOnce()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var rig = await MakeRigAsync();
        rig.Book = new ConfirmationBook(clock);
        Assert.Equal(TimeSpan.FromMinutes(10), ConfirmationBook.Life);
        var shown = Call("team_archive", rig.Actions(), new() { ["team"] = "alpha" });
        clock.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);
        var late = Call("team_archive", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = Token(shown) });
        Assert.StartsWith("NOT DONE", late);
        Assert.Contains("unknown or expired", late);
        Assert.Empty(rig.Ui);
        clock.Now += TimeSpan.FromMinutes(9);
        Assert.StartsWith("ok:", Call("team_archive", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = Token(late) }));
        Assert.StartsWith("NOT DONE", Call("team_archive", rig.Actions(), new() { ["team"] = "alpha", ["confirm"] = Token(late) }));
        Assert.Single(rig.Ui);
    }

    // ---------------------------------------------------------------- the service: who started the turn

    [Fact]
    public async Task TheServiceGivesACloudTurnItsScope_AndAReviewTurnNoYes()
    {
        var rig = await MakeRigAsync();
        var store = new ConciergeStore(_db.ConnectionString);
        var launched = new List<ConciergeTools>();
        var service = new ConciergeService(store, rig.Bus,
            (request, _) =>
            {
                lock (launched) launched.Add((ConciergeTools)request.Tools!);
                return Task.FromResult(new Yawble.Core.Launch.LaunchOutcome("r-x", Yawble.Core.Launch.RunEnd.StartFailed, "no agents in this test", null, []));
            },
            new Yawble.Core.Launch.RunRegistry(), new Yawble.Core.Ui.AppEvents(), (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()),
            _ => Task.FromResult("claude"), followUpAfter: TimeSpan.Zero) { Actions = rig.Actions() };
        Branch("members/p", merged: false);

        async Task<(ConciergeTools Tools, string Id)> SendAsync(string? id, Yawble.Core.Provenance.Actor who)
        {
            int before;
            lock (launched) before = launched.Count;
            using (Yawble.Core.Provenance.Actor.Use(who))
                id = (await service.DispatchAsync("concierge.send", new JsonObject { ["id"] = id, ["text"] = "hello" }, Ct))!["id"]!.GetValue<string>();
            await Wait.UntilAsync(() => store.Messages(id).Count(m => m.Role == "assistant") == store.Messages(id).Count(m => m.Role == "user"), "the turn ended");
            lock (launched) return (launched[before], id);
        }

        // From the Cloud Link: the push is refused as local-only.
        var (cloud, _) = await SendAsync(null, Yawble.Core.Provenance.Actor.CloudViewer("viewer@example.test"));
        var cloudSaid = await CallTool(cloud, "git_push_branch", new() { ["team"] = "alpha", ["branch"] = "members/p" });
        Assert.Contains("only for the person at this computer", cloudSaid);
        Assert.NotEqual(Sha("members/p"), Run(Origin, "for-each-ref", "--format=%(objectname)", "refs/heads/members/p"));

        // From anywhere but the window on this computer (here: a trigger, or the Concierge itself): treated as remote.
        foreach (var other in new[] { new Yawble.Core.Provenance.Actor(Yawble.Core.Provenance.Actor.Trigger, "x"),
                                      new Yawble.Core.Provenance.Actor(Yawble.Core.Provenance.Actor.Concierge, "x") })
        {
            var (tools, _) = await SendAsync(null, other);
            Assert.Contains("only for the person at this computer", await CallTool(tools, "git_push_branch", new() { ["team"] = "alpha", ["branch"] = "members/p" }));
        }
        Assert.NotEqual(Sha("members/p"), Run(Origin, "for-each-ref", "--format=%(objectname)", "refs/heads/members/p"));

        // From the window: it runs.
        var (local, id) = await SendAsync(null, Yawble.Core.Provenance.Actor.Window);
        Assert.Contains("pushed members/p", await CallTool(local, "git_push_branch", new() { ["team"] = "alpha", ["branch"] = "members/p" }));
        var shown = await CallTool(local, "team_archive", new() { ["team"] = "alpha" });
        Assert.StartsWith("NOT DONE", shown);

        // A review turn (a watch's follow-up, no message from the person) cannot redeem it.
        Assert.StartsWith("ok: watch", await CallTool(local, "watch", new() { ["team"] = "alpha", ["what"] = "anything", ["follow_up"] = true, ["hours"] = 1 }));
        using (var writer = _db.OpenStore())
            writer.Append(new NewMessage(MessageTypes.Failed, System.Text.Json.JsonSerializer.Serialize(new { reason = "tests failed" }), "alpha/dev", null));
        int count;
        lock (launched) count = launched.Count;
        await service.TickAsync(Ct);
        await Wait.UntilAsync(() => { lock (launched) return launched.Count == count + 1; }, "the review turn");
        ConciergeTools review;
        lock (launched) review = launched[^1];
        var refused = await CallTool(review, "team_archive", new() { ["team"] = "alpha", ["confirm"] = Token(shown) });
        Assert.Contains("not started by a message from the person", refused);
        Assert.DoesNotContain(rig.Ui, u => u.Method == "teams.archive");
        await Wait.UntilAsync(() => store.Messages(id).Count(m => m.Role == "assistant") == 2, "the review ended");

        // The person's next message can.
        var (next, _) = await SendAsync(id, Yawble.Core.Provenance.Actor.Window);
        Assert.StartsWith("ok:", await CallTool(next, "team_archive", new() { ["team"] = "alpha", ["confirm"] = Token(shown) }));
    }

    private static async Task<string> CallTool(ConciergeTools tools, string name, JsonObject args)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() } };
        var reply = JsonNode.Parse((await tools.HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct)).Json!)!["result"]!;
        return reply["content"]![0]!["text"]!.GetValue<string>();
    }

    // ---------------------------------------------------------------- open pull requests for team_git

    [Fact]
    public async Task OpenPullRequestsAreReadThroughGh_AndNotAtAllWithoutIt()
    {
        var rig = await MakeRigAsync();
        var target = new GitRepoTarget("alpha", "main", Work, "main", []);
        var prs = await rig.Actions().OpenPullRequestsAsync(target, Ct);
        Assert.NotNull(prs);
        Assert.Equal("#7 Add x", prs["members/ines"]);
        Assert.Equal("#9 Docs", prs["members/ben"]);
        Assert.Equal(["pr", "list", "--state", "open", "--limit", "200", "--json", "number,title,headRefName"], rig.GhCalls.Single());

        rig.GhList = "not json";
        Assert.Null(await rig.Actions().OpenPullRequestsAsync(target, Ct));
        rig.WithGh = false;
        Assert.Null(await rig.Actions().OpenPullRequestsAsync(target, Ct));
    }
}
