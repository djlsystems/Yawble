using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Monitor;
using Yawble.Core.Policy;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Teams;
using Yawble.Core.Ui;
using Xunit;
using MemberRef = Yawble.Core.Bus.MemberRef;

namespace Yawble.Core.Tests.Monitor;

/// <summary>
/// Commits, merges into the team branch, merges into main and pushes, recorded in the team repository as the
/// Monitor's code events, and read with monitor.codeEvents (in the window and over the Cloud Link the same).
/// </summary>
public class CodeEventsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(string name) => string.Join('\n', Enumerable.Range(1, 12).Select(i => $"{name} line {i}")) + "\n";

    private static string Commit(MemberCheckout c, string message, Action<string> change)
    {
        change(c.ClonePath);
        AgentGit.Ok(c, "add", "-A");
        AgentGit.Ok(c, "commit", "-q", "-m", message);
        return AgentGit.Ok(c, "rev-parse", "HEAD");
    }

    private static string Files(CodeEvent e) => string.Join(", ", e.Files.OrderBy(f => f.Path, StringComparer.Ordinal)
        .Select(f => f.OldPath is null ? $"{f.Status} {f.Path}" : $"{f.Status} {f.OldPath}->{f.Path}"));

    [Fact]
    public async Task AFetchBackWithTwoNewCommits_GivesTwoCommitEvents_WithTheirFilesAndStatus()
    {
        SandboxGit.RequireLocalTransport();
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var c = await f.Repos.PrepareAsync(ada, Ct);
        var first = Commit(c, "add a, touch readme", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), Text("a"));
            File.AppendAllText(Path.Combine(dir, "README.md"), "more\n");
        });
        var second = Commit(c, "rename a, drop readme", dir =>
        {
            File.Move(Path.Combine(dir, "a.txt"), Path.Combine(dir, "b.txt"));
            File.Delete(Path.Combine(dir, "README.md"));
        });

        Assert.True((await f.Repos.FetchBackAsync(ada, Ct, deliverableId: 42)).Fetched);

        var events = f.Repos.CodeEvents.Read("Alpha");
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(CodeEvent.Commit, e.Kind);
            Assert.Equal("Ada", e.Member);
            Assert.Equal("members/alpha/ada", e.Branch);
            Assert.Equal(42, e.DeliverableId);
        });
        Assert.Equal([first, second], events.Select(e => e.Sha));
        Assert.Equal(["add a, touch readme", "rename a, drop readme"], events.Select(e => e.Message));
        Assert.Equal("A README.md, A a.txt", Files(events[0])); // the team repository starts empty: the first commit adds README.md
        Assert.Equal("D README.md, R a.txt->b.txt", Files(events[1]));

        // A fetch-back with nothing new records nothing; the next commit is one more event, never a repeat.
        await f.Repos.FetchBackAsync(ada, Ct, deliverableId: 43);
        Assert.Equal(2, f.Repos.CodeEvents.Read("Alpha").Count);
        var third = Commit(c, "c", dir => File.WriteAllText(Path.Combine(dir, "c.txt"), "c"));
        await f.Repos.FetchBackAsync(ada, Ct, deliverableId: 43);
        var after = f.Repos.CodeEvents.Read("Alpha");
        Assert.Equal([first, second, third], after.Select(e => e.Sha));
        Assert.Equal(43, after[2].DeliverableId);
        Assert.Equal("A c.txt", Files(after[2]));
    }

    [Fact]
    public async Task TheTeamBranch_AMergeOfAMemberBranch_AFastForward_AndTheManagersOwnCommit()
    {
        SandboxGit.RequireLocalTransport();
        using var f = new TeamsFixture();
        var (manager, ada, ben) = f.Team();
        var a = await f.Repos.PrepareAsync(ada, Ct);
        var m = await f.Repos.PrepareAsync(manager, Ct);
        var b = await f.Repos.PrepareAsync(ben, Ct);
        var bundle = Path.Combine(f.Repos.SharedRepoPath("Alpha"), TeamRepos.SharedBundleFile);

        // A fast-forward of the team branch to Ada's tip: one merge-team event, no commit of the Manager's.
        var adaTip = Commit(a, "ada's work", dir => File.WriteAllText(Path.Combine(dir, "a.txt"), "a"));
        await f.Repos.FetchBackAsync(ada, Ct, deliverableId: 5);
        AgentGit.Ok(m, "fetch", "-q", bundle, "members/alpha/ada");
        AgentGit.Ok(m, "merge", "-q", "--ff-only", "FETCH_HEAD");
        await f.Repos.FetchBackAsync(manager, Ct, deliverableId: 5);
        var ff = f.Repos.CodeEvents.Read("Alpha").Where(e => e.Kind == CodeEvent.MergeTeam).ToList();
        var single = Assert.Single(ff);
        Assert.Equal(("members/alpha/ada", "Ada", adaTip, "team/alpha", "A a.txt", (long?)5),
            (single.Branch, single.Member, single.Sha, single.Into, Files(single), single.DeliverableId));

        // A real merge of Ben's branch, then a commit of the Manager's own on the team branch.
        Commit(b, "ben's work", dir => File.WriteAllText(Path.Combine(dir, "b.txt"), "b"));
        await f.Repos.FetchBackAsync(ben, Ct, deliverableId: 6);
        AgentGit.Ok(m, "fetch", "-q", bundle, "members/alpha/ben");
        AgentGit.Ok(m, "merge", "-q", "--no-ff", "-m", "Merge members/alpha/ben", "FETCH_HEAD");
        var merge = AgentGit.Ok(m, "rev-parse", "HEAD");
        var own = Commit(m, "manager's notes", dir => File.WriteAllText(Path.Combine(dir, "notes.md"), "n"));
        await f.Repos.FetchBackAsync(manager, Ct, deliverableId: 6);

        var events = f.Repos.CodeEvents.Read("Alpha");
        var merged = events.Single(e => e.Kind == CodeEvent.MergeTeam && e.Sha == merge);
        Assert.Equal(("members/alpha/ben", "Ben", "team/alpha", "A b.txt", "Merge members/alpha/ben"),
            (merged.Branch, merged.Member, merged.Into, Files(merged), merged.Message));
        var mine = events.Single(e => e.Sha == own);
        Assert.Equal((CodeEvent.Commit, "Manager", "team/alpha", "A notes.md"), (mine.Kind, mine.Member, mine.Branch, Files(mine)));
        // Ada's and Ben's commits are each one commit event on their own branch; none is repeated for the team branch.
        Assert.Equal(2, events.Count(e => e.Kind == CodeEvent.Commit && e.Branch.StartsWith("members/", StringComparison.Ordinal)));
        Assert.DoesNotContain(events, e => e.Kind == CodeEvent.Commit && e.Branch == "team/alpha" && e.Sha != own);
    }

    [Fact]
    public async Task AMergeIntoMain_AndAPushToOrigin_EachGiveTheirEvent()
    {
        using var f = new TeamsFixture();
        var (manager, ada, _) = f.Team();
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        var a = await f.Repos.PrepareAsync(ada, Ct);
        var m = await f.Repos.PrepareAsync(manager, Ct);
        Commit(a, "ada's work", dir => File.WriteAllText(Path.Combine(dir, "a.txt"), "a"));
        await f.Repos.FetchBackAsync(ada, Ct, deliverableId: 9);
        AgentGit.Ok(m, "fetch", "-q", Path.Combine(f.Repos.SharedRepoPath("Alpha"), TeamRepos.SharedBundleFile), "members/alpha/ada");
        AgentGit.Ok(m, "merge", "-q", "--no-ff", "-m", "Merge members/alpha/ada", "FETCH_HEAD");
        await f.Repos.FetchBackAsync(manager, Ct, deliverableId: 9);

        var report = await f.Repos.PublishAsync(manager, Ct, deliverableId: 9);
        Assert.Equal(TeamPublishResult.Published, report.Result);
        var pushes = f.Repos.CodeEvents.Read("Alpha").Where(e => e.Kind == CodeEvent.Push).ToList();
        Assert.Equal(["members/alpha/ada", "team/alpha"], pushes.Select(p => p.Branch).Order(StringComparer.Ordinal));
        Assert.All(pushes, p =>
        {
            Assert.Equal(AgentGit.Host(bare, "rev-parse", "refs/heads/" + p.Branch), p.Sha);
            Assert.Equal(9, p.DeliverableId);
            Assert.Equal(["members/alpha/ada", "team/alpha"], p.Branches!.Order(StringComparer.Ordinal));
            Assert.Empty(p.Files);
        });
        Assert.Equal("Ada", pushes.Single(p => p.Branch == "members/alpha/ada").Member);

        var result = await f.Repos.GitMergeTeamToBaseAsync(ada.TeamId, "Alpha", Ct);
        Assert.True(result.Changed, result.Message);
        var trunk = AgentGit.Host(picked, "rev-parse", "refs/heads/trunk");
        var events = f.Repos.CodeEvents.Read("Alpha");
        var landed = Assert.Single(events, e => e.Kind == CodeEvent.Landed);
        Assert.Equal(("team/alpha", "trunk", trunk, "A a.txt", (string?)null), (landed.Branch, landed.Into, landed.Sha, Files(landed), landed.Member));
        var pushedTrunk = Assert.Single(events, e => e.Kind == CodeEvent.Push && e.Branch == "trunk");
        Assert.Equal(AgentGit.Host(bare, "rev-parse", "refs/heads/trunk"), pushedTrunk.Sha);
    }

    // ------------------------------------------------------------------ the same, built in one repository
    // git's local transport (fetch, push, clone from a path) cannot start in the agents' Windows sandbox, so the tests
    // above run only where it can. These build the same histories in the team repository itself (branch, commit, merge:
    // no transport) and call the recorder where the fetch-back, the publish and the Merge to main call it.

    private sealed class InRepo : IDisposable
    {
        public TeamsFixture F { get; } = new();
        public MemberRef Manager { get; }
        public MemberRef Ada { get; }
        public MemberRef Ben { get; }
        public string Repo { get; }

        public InRepo()
        {
            (Manager, Ada, Ben) = F.Team();
            Repo = Path.Combine(F.Db.Dir, "persons-repo");
            Directory.CreateDirectory(Repo);
            AgentGit.Host(Repo, "init", "-q", "-b", "trunk");
            File.WriteAllText(Path.Combine(Repo, "README.md"), "# service\n");
            Git("add", "README.md");
            Git("commit", "-q", "-m", "initial");
            F.Repos.UseExistingRepoAsync(Ada.TeamId, "Alpha", Repo, Ct).GetAwaiter().GetResult();
            // The members' clones as PrepareAsync records them (only their branch matters here).
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(F.Db.ConnectionString);
            c.Open();
            foreach (var (m, branch) in new[] { (Manager, "team/alpha"), (Ada, "members/alpha/ada"), (Ben, "members/alpha/ben") })
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO member_clones (member_id, path, branch, created_at) VALUES ($m, $p, $b, '2026-11-17T00:00:00Z');";
                cmd.Parameters.AddWithValue("$m", m.Id);
                cmd.Parameters.AddWithValue("$p", Path.Combine(F.Db.Dir, "clone-" + m.Name));
                cmd.Parameters.AddWithValue("$b", branch);
                cmd.ExecuteNonQuery();
            }
        }

        public string Git(params string[] args) => AgentGit.Host(Repo, ["-c", "user.name=Agent", "-c", "user.email=agent@example.invalid", .. args]);

        /// <summary>Commits on <paramref name="branch"/> (made from trunk when new), then goes back to trunk.</summary>
        public string CommitOn(string branch, string message, Action<string> change)
        {
            var exists = AgentGit.Run(Repo, Path.GetTempPath(), "rev-parse", "--verify", "-q", "refs/heads/" + branch).ExitCode == 0;
            Git(exists ? ["checkout", "-q", branch] : ["checkout", "-q", "-b", branch, "trunk"]);
            change(Repo);
            Git("add", "-A");
            Git("commit", "-q", "-m", message);
            var sha = Git("rev-parse", "HEAD");
            Git("checkout", "-q", "trunk");
            return sha;
        }

        public string? Rev(string branch) => AgentGit.Run(Repo, Path.GetTempPath(), "rev-parse", "--verify", "-q", "refs/heads/" + branch) is { ExitCode: 0 } r ? r.Stdout : null;

        public void Dispose() => F.Dispose();
    }

    [Fact]
    public async Task InRepo_TwoNewCommitsOnAMemberBranch_AreTwoCommitEvents_WithTheirFilesAndStatus()
    {
        using var r = new InRepo();
        var first = r.CommitOn("members/alpha/ada", "add a, touch readme", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), Text("a"));
            File.AppendAllText(Path.Combine(dir, "README.md"), "more\n");
        });
        var second = r.CommitOn("members/alpha/ada", "rename a, drop readme", dir =>
        {
            File.Move(Path.Combine(dir, "a.txt"), Path.Combine(dir, "b.txt"));
            File.Delete(Path.Combine(dir, "README.md"));
        });
        await r.F.Repos.RecordFetchedAsync(r.Ada, r.Repo, "trunk", "members/alpha/ada", null, second, 42, Ct);

        var events = r.F.Repos.CodeEvents.Read("Alpha");
        Assert.Equal([first, second], events.Select(e => e.Sha));
        Assert.All(events, e => Assert.Equal((CodeEvent.Commit, "Ada", "members/alpha/ada", (long?)42), (e.Kind, e.Member, e.Branch, e.DeliverableId)));
        Assert.Equal("M README.md, A a.txt", Files(events[0]));
        Assert.Equal("D README.md, R a.txt->b.txt", Files(events[1]));
        Assert.Equal(DateTimeOffset.Parse(r.Git("show", "-s", "--format=%cI", first), System.Globalization.CultureInfo.InvariantCulture), events[0].At);

        // The next landing records only what is new; the same range again records nothing.
        var third = r.CommitOn("members/alpha/ada", "c", dir => File.WriteAllText(Path.Combine(dir, "c.txt"), "c"));
        await r.F.Repos.RecordFetchedAsync(r.Ada, r.Repo, "trunk", "members/alpha/ada", second, third, 43, Ct);
        await r.F.Repos.RecordFetchedAsync(r.Ada, r.Repo, "trunk", "members/alpha/ada", second, third, 43, Ct);
        Assert.Equal([first, second, third], r.F.Repos.CodeEvents.Read("Alpha").Select(e => e.Sha));
    }

    [Fact]
    public async Task InRepo_TheTeamBranch_AFastForward_AMerge_AndTheManagersOwnCommit()
    {
        using var r = new InRepo();
        var adaTip = r.CommitOn("members/alpha/ada", "ada's work", dir => File.WriteAllText(Path.Combine(dir, "a.txt"), "a"));
        await r.F.Repos.RecordFetchedAsync(r.Ada, r.Repo, "trunk", "members/alpha/ada", null, adaTip, 5, Ct);
        // The Manager fast-forwards the team branch to Ada's tip.
        r.Git("branch", "team/alpha", adaTip);
        await r.F.Repos.RecordFetchedAsync(r.Manager, r.Repo, "trunk", "team/alpha", null, adaTip, 5, Ct);
        var ff = Assert.Single(r.F.Repos.CodeEvents.Read("Alpha"), e => e.Kind == CodeEvent.MergeTeam);
        Assert.Equal(("members/alpha/ada", "Ada", adaTip, "team/alpha", "A a.txt", (long?)5), (ff.Branch, ff.Member, ff.Sha, ff.Into, Files(ff), ff.DeliverableId));

        // Ben's work merged with a merge commit, then the Manager's own commit.
        var benTip = r.CommitOn("members/alpha/ben", "ben's work", dir => File.WriteAllText(Path.Combine(dir, "b.txt"), "b"));
        await r.F.Repos.RecordFetchedAsync(r.Ben, r.Repo, "trunk", "members/alpha/ben", null, benTip, 6, Ct);
        r.Git("checkout", "-q", "team/alpha");
        r.Git("merge", "-q", "--no-ff", "-m", "Merge members/alpha/ben", "members/alpha/ben");
        var merge = r.Git("rev-parse", "HEAD");
        r.Git("checkout", "-q", "trunk");
        var own = r.CommitOn("team/alpha", "manager's notes", dir => File.WriteAllText(Path.Combine(dir, "notes.md"), "n"));
        await r.F.Repos.RecordFetchedAsync(r.Manager, r.Repo, "trunk", "team/alpha", adaTip, own, 6, Ct);

        var events = r.F.Repos.CodeEvents.Read("Alpha");
        var merged = Assert.Single(events, e => e.Kind == CodeEvent.MergeTeam && e.Sha == merge);
        Assert.Equal(("members/alpha/ben", "Ben", "team/alpha", "A b.txt", "Merge members/alpha/ben", (long?)6),
            (merged.Branch, merged.Member, merged.Into, Files(merged), merged.Message, merged.DeliverableId));
        var mine = Assert.Single(events, e => e.Sha == own);
        Assert.Equal((CodeEvent.Commit, "Manager", "team/alpha", "A notes.md"), (mine.Kind, mine.Member, mine.Branch, Files(mine)));
        Assert.Equal(2, events.Count(e => e.Kind == CodeEvent.Commit && e.Branch.StartsWith("members/", StringComparison.Ordinal)));
        Assert.Equal(5, events.Count); // Ada's and Ben's commits, the fast-forward, Ben's merge, the Manager's commit
    }

    [Fact]
    public async Task InRepo_AMergeIntoMain_IsALandedEvent_AndAPushIsAPushEvent()
    {
        using var r = new InRepo();
        r.CommitOn("members/alpha/ada", "ada's work", dir => File.WriteAllText(Path.Combine(dir, "a.txt"), "a"));
        r.Git("branch", "team/alpha", "trunk");
        r.Git("checkout", "-q", "team/alpha");
        r.Git("merge", "-q", "--no-ff", "-m", "Merge members/alpha/ada", "members/alpha/ada");
        r.Git("checkout", "-q", "trunk");
        var start = r.Rev("trunk");

        // The Merge to main itself (no origin here, so nothing is pushed and there is no push event for it).
        var result = await r.F.Repos.GitMergeTeamToBaseAsync(r.Ada.TeamId, "Alpha", Ct);
        Assert.True(result.Changed, result.Message);
        var trunk = r.Rev("trunk")!;
        var events = r.F.Repos.CodeEvents.Read("Alpha");
        var landed = Assert.Single(events);
        Assert.Equal((CodeEvent.Landed, "team/alpha", "trunk", trunk, "A a.txt", (string?)null), (landed.Kind, landed.Branch, landed.Into, landed.Sha, Files(landed), landed.Member));
        Assert.NotEqual(start, trunk);

        // What a publish records for the branches it pushed (the push itself needs git's transport).
        await r.F.Repos.RecordPushedAsync(r.Manager, r.Repo, ["members/alpha/ada", "team/alpha"], 9, Ct);
        var pushes = r.F.Repos.CodeEvents.Read("Alpha").Where(e => e.Kind == CodeEvent.Push).ToList();
        Assert.Equal(["members/alpha/ada", "team/alpha"], pushes.Select(p => p.Branch).Order(StringComparer.Ordinal));
        Assert.All(pushes, p =>
        {
            Assert.Equal(r.Rev(p.Branch), p.Sha);
            Assert.Equal(9, p.DeliverableId);
            Assert.Equal(["members/alpha/ada", "team/alpha"], p.Branches!);
            Assert.Empty(p.Files);
        });
        Assert.Equal("Ada", pushes.Single(p => p.Branch == "members/alpha/ada").Member);
        Assert.Null(pushes.Single(p => p.Branch == "team/alpha").Member);

        // A landed Merge to main with an origin also records the base branch's push.
        await r.F.Repos.RecordLandedAsync(r.F.Repos.RepoOf(r.Ada.TeamId)!, "Alpha", "team/alpha", start, trunk, pushed: true, Ct);
        Assert.Single(r.F.Repos.CodeEvents.Read("Alpha"), e => e.Kind == CodeEvent.Push && e.Branch == "trunk" && e.Sha == trunk);
    }

    [Fact]
    public void TheRead_IsOldestFirst_AndFiltersByTeamSinceUntilAndCursor()
    {
        using var db = new TempDb();
        var store = new CodeEvents(db.ConnectionString);
        var t0 = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        CodeEvent E(string sha, int minutes, string team = "Alpha") =>
            new(CodeEvent.Commit, team, "Ada", "members/alpha/ada", sha, sha, [new CodeFile(sha + ".txt", "A")], t0.AddMinutes(minutes), 1);
        // Recorded out of time order (a long run's early commit is fetched back last).
        Assert.Equal(4, store.Add([E("c", 20), E("a", 0), E("other", 5, "Beta"), E("b", 10)]));
        Assert.Equal(0, store.Add([E("a", 0)])); // the same event twice is one

        Assert.Equal(["a", "b", "c"], store.Read("Alpha").Select(e => e.Sha));
        Assert.Equal(["a", "other", "b", "c"], store.Read().Select(e => e.Sha));
        Assert.Equal(["b", "c"], store.Read("Alpha", since: t0.AddMinutes(10)).Select(e => e.Sha));
        Assert.Equal(["a", "b"], store.Read("Alpha", until: t0.AddMinutes(10)).Select(e => e.Sha));
        Assert.Equal(["b"], store.Read("Alpha", since: t0.AddMinutes(1), until: t0.AddMinutes(19)).Select(e => e.Sha));
        var c = store.Read("Alpha").Single(e => e.Sha == "c");
        Assert.Equal(["a", "b"], store.Read("Alpha", after: c.Id).Select(e => e.Sha)); // c was recorded first
        Assert.Equal(["a"], store.Read("Alpha", limit: 1).Select(e => e.Sha));

        var json = c.ToJson();
        Assert.Equal("commit", (string?)json["kind"]);
        Assert.Equal("2026-11-17T10:20:00.0000000+00:00", (string?)json["at"]);
        Assert.Equal("c.txt", (string?)json["files"]![0]!["path"]);
        Assert.Equal("A", (string?)json["files"]![0]!["status"]);
        Assert.Equal(1, (long?)json["deliverableId"]);
    }

    [Fact]
    public void NameStatus_ParsesAddsModifiesDeletesRenamesAndOddPaths()
    {
        var files = TeamRepos.ParseNameStatus("A\0new file.txt\0M\0src/x.cs\0D\0gone.md\0R087\0old/a.ts\0new/a.ts\0C100\0x\0y\0T\0link\0");
        Assert.Equal(
            [new CodeFile("new file.txt", "A"), new CodeFile("src/x.cs", "M"), new CodeFile("gone.md", "D"), new CodeFile("new/a.ts", "R", "old/a.ts"),
             new CodeFile("y", "A"), new CodeFile("link", "M")], files);
        Assert.Empty(TeamRepos.ParseNameStatus(""));
    }

    [Fact]
    public async Task TheRemoteRead_ReturnsTheSameEvents_ForAReadOnlyViewerToo()
    {
        using var db = new TempDb();
        var appRoot = Path.Combine(db.Dir, "app");
        Directory.CreateDirectory(appRoot);
        var terminals = new TerminalHub(new AppEvents());
        BusEngine? busRef = null;
        var runner = new FakePtyRunner(terminals, id => busRef!.MemberAsync(id).GetAwaiter().GetResult()?.Cli ?? "claude", ticks: 1, tick: TimeSpan.FromMilliseconds(10));
        await using var bus = await BusHost.StartAsync(db.ConnectionString, _ => null, null, launcher: null, runner, () => 4, appDataRoot: appRoot);
        busRef = bus;
        var store = new CodeEvents(db.ConnectionString);
        var t0 = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        store.Add([
            new CodeEvent(CodeEvent.Commit, "Alpha", "Ada", "members/alpha/ada", "s1", "one", [new CodeFile("a.txt", "A")], t0, 3),
            new CodeEvent(CodeEvent.MergeTeam, "Alpha", "Ada", "members/alpha/ada", "s2", "Merge", [new CodeFile("a.txt", "A")], t0.AddMinutes(1), 3, Into: "team/alpha"),
            new CodeEvent(CodeEvent.Push, "Alpha", null, "team/alpha", "s2", "Pushed", [], t0.AddMinutes(2), 3, Branches: ["team/alpha"]),
        ]);
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var ui = new UiRpc(bus, new PolicyStore(db.ConnectionString), new MemberLayouts(Path.Combine(appRoot, "data"), db.Dir, home), new FakeModelCredentials(),
            new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, bus), terminals, 16_384, codeEvents: store);
        var rpc = new RpcDispatcher(new Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
        var p = new JsonObject { ["team"] = "Alpha", ["since"] = t0.ToString("O") };

        var local = (await rpc.DispatchAsync("monitor.codeEvents", (JsonObject)p.DeepClone(), Ct))!.AsArray();
        Assert.Equal(["commit", "merge-team", "push"], local.Select(e => (string?)e!["kind"]));
        Assert.Equal("team/alpha", (string?)local[1]!["into"]);

        Assert.True(RemoteAccess.Reads("monitor.codeEvents", null));
        var data = Path.Combine(db.Dir, "link");
        Directory.CreateDirectory(data);
        var link = new CloudLink(data, (method, args, ct) => rpc.DispatchAsync(method, args, ct), new AppEvents());
        foreach (var access in new[] { RemoteAccess.Full, RemoteAccess.Read })
        {
            JsonObject? reply = null;
            var call = new JsonObject { ["method"] = "monitor.codeEvents", ["params"] = p.DeepClone(), ["id"] = 1, ["session"] = "s", ["user"] = "viewer", ["access"] = access };
            await link.AnswerAsync(call, r => { reply = r; return Task.CompletedTask; }, Ct);
            Assert.Null(reply!["error"]);
            Assert.True(JsonNode.DeepEquals(local, reply["result"]), reply.ToJsonString());
        }
    }
}
