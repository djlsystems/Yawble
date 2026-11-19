using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Teams;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>
/// Card 472 (B074 verify, TesterKofi): git.commitsResolve through the real dispatcher on two teams' real repositories,
/// for what CommitsResolveTests / Card470CommitsResolveRpcTests (card 470) do not cover:
///  - two teams sharing a commit (the same short ID in two teams), and main + writable extra in one team;
///  - two commits of ONE repository sharing a 7-digit prefix (made with fast-import), and the 8-digit IDs telling them apart;
///  - a branch named like another commit's prefix, a branch/tag named like hex, a blob, a tree and an annotated tag
///    object, mixed in one batch with real commits (no error, the commits still match);
///  - pushed agrees with the Commit view's where.pushed for every match (origin on a non-main branch too);
///  - the cost: the same git processes per repository for 1 ID and for 200 full IDs (the command line holds them);
///  - an extra repository whose folder is gone: an error entry, the other repositories still answer.
/// With CARD472_FIXTURE_OUT set, the real answers (as the dispatcher returns them) are written there for the shell's
/// verify test (shell/tests/unit/fixtures/card472).
/// </summary>
public sealed class Card472CommitsResolveVerifyTests : IAsyncLifetime
{
    private readonly TempDb _db = new();
    private BusEngine _bus = null!;
    private RpcDispatcher _rpc = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var appRoot = Path.Combine(_db.Dir, "app");
        Directory.CreateDirectory(appRoot);
        var terminals = new TerminalHub(new AppEvents());
        var policies = new PolicyStore(_db.ConnectionString);
        BusEngine? busRef = null;
        var runner = new FakePtyRunner(terminals, id => busRef!.MemberAsync(id).GetAwaiter().GetResult()?.Cli ?? "claude", ticks: 1, tick: TimeSpan.FromMilliseconds(10));
        _bus = await BusHost.StartAsync(_db.ConnectionString, _ => null, null, launcher: null, runner, () => 4, appDataRoot: appRoot);
        busRef = _bus;
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var ui = new UiRpc(_bus, policies, new MemberLayouts(Path.Combine(appRoot, "data"), _db.Dir, home), new FakeModelCredentials(),
            new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, _bus), terminals, 16_384);
        _rpc = new RpcDispatcher(new Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
    }

    public async ValueTask DisposeAsync()
    {
        _bus.Repos!.Git.OnRun = null;
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private async Task<JsonObject> Call(string method, JsonObject p) => (await _rpc.DispatchAsync(method, p, Ct))!.AsObject();
    private static JsonArray Ids(IEnumerable<string> ids) => new([.. ids.Select(i => (JsonNode?)i)]);
    private Task<JsonObject> Resolve(JsonObject scope, IEnumerable<string> ids)
    {
        var p = (JsonObject)scope.DeepClone();
        p["ids"] = Ids(ids);
        return Call("git.commitsResolve", p);
    }
    private static JsonArray MatchesOf(JsonObject answer, string id) =>
        answer["results"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == id.ToLowerInvariant())!["matches"]!.AsArray();

    private static readonly Dictionary<string, string> Who = new()
    {
        ["GIT_AUTHOR_NAME"] = "Person", ["GIT_AUTHOR_EMAIL"] = "p@example.invalid",
        ["GIT_COMMITTER_NAME"] = "Person", ["GIT_COMMITTER_EMAIL"] = "p@example.invalid",
    };
    private static string Commit(string repo, string message)
    {
        AgentGit.HostWithEnv(repo, Who, "-c", "commit.gpgSign=false", "commit", "-q", "--allow-empty", "-m", message);
        return AgentGit.Host(repo, "rev-parse", "HEAD");
    }

    /// <summary>A team with its main repository made by the core (git.status), and that repository's folder.</summary>
    private async Task<(long Id, string Repo)> TeamAsync(string name)
    {
        var id = await _bus.CreateTeamAsync(name, "Manager", "claude", null, Ct);
        await _bus.AddMemberAsync(id, "Ada", "claude", Ct);
        await Call("git.status", new JsonObject { ["teamId"] = id });
        return (id, _bus.Repos!.RepoOf(id)!.Path);
    }

    /// <summary>Gives repo the objects of other (alternates) and a branch at other's commit: the same commit in both.</summary>
    private static void Share(string repo, string other, string branch, string sha)
    {
        var alt = Path.Combine(repo, ".git", "objects", "info", "alternates");
        var line = Path.Combine(other, ".git", "objects").Replace('\\', '/') + "\n";
        File.AppendAllText(alt, line);
        AgentGit.Host(repo, "update-ref", "refs/heads/" + branch, sha);
    }

    private static void Fixture(string name, JsonNode json)
    {
        var dir = Environment.GetEnvironmentVariable("CARD472_FIXTURE_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>Runs git fast-import in repo with the stream on stdin (many commits in one process).</summary>
    private static void FastImport(string repo, string stream)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "fast-import", "--quiet" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        p.StandardInput.Write(stream);
        p.StandardInput.Close();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "fast-import: " + err.Result);
        _ = outp.Result;
    }

    [Fact]
    public async Task TwoTeams_ShortAndFull_NotCommits_Ambiguous_Pushed_AsTheCommitViewSays()
    {
        var (webId, web) = await TeamAsync("web");
        var (cloudId, cloud) = await TeamAsync("cloud");

        var pushedSha = Commit(web, "Partial refunds: refund part of a payment");
        // origin has it on a branch that is not main (pushed is "some origin branch has it").
        AgentGit.Host(web, "update-ref", "refs/remotes/origin/feature/refunds", pushedSha);
        var localSha = Commit(web, "Receipts: a new layout");
        // A root commit (no parents, so team cloud gets only it, not web's history) shared by both teams' repositories.
        var emptyTree = AgentGit.Host(web, "mktree");
        var sharedSha = AgentGit.HostWithEnv(web, Who, "commit-tree", emptyTree, "-m", "Shared: the common README");
        AgentGit.Host(web, "update-ref", "refs/heads/shared", sharedSha);
        // The same commit in team cloud's repository, through a bundle (only that commit's objects; alternates would lend
        // cloud every web object, and any object git has is found, reachable or not, as in the Commit view).
        var bundle = Path.Combine(_db.Dir, "shared.bundle");
        AgentGit.Host(web, "bundle", "create", bundle, "refs/heads/shared");
        AgentGit.Host(cloud, "fetch", "-q", bundle, "refs/heads/shared:refs/heads/from-web");
        var cloudOnly = Commit(cloud, "Cloud: retry the webhook");

        // Names and objects that look like commit IDs but are not commits (or not the commit their name says).
        AgentGit.Host(web, "branch", "cafe1234", localSha);
        AgentGit.Host(web, "branch", pushedSha[..9] + "0", localSha); // a branch whose name starts like another commit
        AgentGit.Host(web, "branch", "deadbee1", localSha);
        AgentGit.Host(web, "tag", "beefcafe", localSha);
        File.WriteAllText(Path.Combine(web, "blob.txt"), "a blob for card 472\n");
        var blob = AgentGit.Host(web, "hash-object", "-w", "blob.txt");
        var tree = AgentGit.Host(web, "rev-parse", localSha + "^{tree}");
        AgentGit.HostWithEnv(web, Who, "tag", "-a", "-m", "annotated", "v1", localSha);
        var tagObject = AgentGit.Host(web, "rev-parse", "refs/tags/v1");
        Assert.NotEqual(localSha, tagObject);

        var ids = new List<string>
        {
            pushedSha[..7], pushedSha.ToUpperInvariant(), localSha[..10], sharedSha[..8], cloudOnly[..7],
            "cafe1234", "deadbee1", "beefcafe", blob[..9], tree[..9], tagObject[..9], "abc1234", "zz",
        };

        // Every team (what the shell sends: no team).
        var all = await Resolve(new JsonObject(), ids);
        Assert.False(all.ContainsKey("errors"), all.ToJsonString());
        Assert.Equal(ids.Select(i => i.ToLowerInvariant()), all["results"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));

        var p7 = MatchesOf(all, pushedSha[..7]).Single()!;
        Assert.Equal(pushedSha, p7["fullId"]!.GetValue<string>());
        Assert.Equal("Partial refunds: refund part of a payment", p7["title"]!.GetValue<string>());
        Assert.Equal("web", p7["team"]!.GetValue<string>());
        Assert.True(p7["pushed"]!.GetValue<bool>());
        Assert.Equal(pushedSha, MatchesOf(all, pushedSha).Single()!["fullId"]!.GetValue<string>());
        var local = MatchesOf(all, localSha[..10]).Single()!;
        Assert.False(local["pushed"]!.GetValue<bool>());
        Assert.Equal("Receipts: a new layout", local["title"]!.GetValue<string>());

        // The same short ID in two teams: two matches, one per team, each in its team's main repository.
        var shared = MatchesOf(all, sharedSha[..8]);
        Assert.Equal(["cloud", "web"], shared.Select(m => m!["team"]!.GetValue<string>()).Order());
        Assert.All(shared, m => Assert.Equal(sharedSha, m!["fullId"]!.GetValue<string>()));
        Assert.All(shared, m => Assert.Null(m!["repo"]));
        Assert.Equal("cloud", MatchesOf(all, cloudOnly[..7]).Single()!["team"]!.GetValue<string>());

        // Not commits: hex-named branches/tags, a blob, a tree, the annotated tag OBJECT, a hex word; and an invalid word.
        foreach (var id in new[] { "cafe1234", "deadbee1", "beefcafe", blob[..9], tree[..9], tagObject[..9], "abc1234" })
            Assert.Empty(MatchesOf(all, id));
        Assert.False(all["results"]!.AsArray().Last()!["valid"]!.GetValue<bool>());
        // The branch named like pushedSha's prefix does not make that prefix mean its target.
        Assert.Equal(pushedSha, MatchesOf(all, pushedSha[..7]).Single()!["fullId"]!.GetValue<string>());

        // One team only: the other team's commit is not looked at.
        var webOnly = await Resolve(new JsonObject { ["team"] = "web" }, [sharedSha[..8], cloudOnly[..7]]);
        Assert.Equal("web", MatchesOf(webOnly, sharedSha[..8]).Single()!["team"]!.GetValue<string>());
        Assert.Empty(MatchesOf(webOnly, cloudOnly[..7]));

        // pushed and the Commit view: for every match, git.commitShow (team + repo + fullId) finds it, its shortSha is
        // the match's shortId, and where.pushed is the match's pushed.
        foreach (var r in all["results"]!.AsArray())
            foreach (var m in r!["matches"]!.AsArray())
            {
                var show = await Call("git.commitShow", new JsonObject { ["team"] = m!["team"]!.GetValue<string>(), ["sha"] = m["fullId"]!.GetValue<string>() });
                Assert.True(show["found"]!.GetValue<bool>());
                Assert.Equal(m["shortId"]!.GetValue<string>(), show["shortSha"]!.GetValue<string>());
                Assert.Equal(m["pushed"]!.GetValue<bool>(), show["where"]!["pushed"]!.GetValue<bool>());
                Assert.Equal(m["title"]!.GetValue<string>(), show["title"]!.GetValue<string>());
            }

        Fixture("resolve-two-teams", new JsonObject
        {
            ["shas"] = new JsonObject { ["pushed"] = pushedSha, ["local"] = localSha, ["shared"] = sharedSha, ["cloudOnly"] = cloudOnly, ["blob"] = blob, ["tree"] = tree, ["tagObject"] = tagObject },
            ["teams"] = new JsonObject { ["web"] = webId, ["cloud"] = cloudId },
            ["answer"] = all.DeepClone(),
        });
    }

    [Fact]
    public async Task OneRepository_TwoCommitsSharingASevenDigitPrefix_BothMatch_AndEightDigitsTellThemApart()
    {
        var (_, web) = await TeamAsync("web");
        // ~40,000 empty commits in one fast-import: a 7-digit (28-bit) prefix collision is all but certain.
        var stream = new System.Text.StringBuilder();
        const int n = 40_000;
        for (var i = 1; i <= n; i++)
        {
            var msg = $"bulk {i}\n";
            stream.Append("commit refs/heads/bulk\n").Append($"mark :{i}\n")
                .Append($"committer Person <p@example.invalid> {1_700_000_000 + i} +0000\n")
                .Append($"data {System.Text.Encoding.UTF8.GetByteCount(msg)}\n").Append(msg);
            if (i > 1) stream.Append($"from :{i - 1}\n");
            stream.Append('\n');
        }
        FastImport(web, stream.ToString());
        var all = AgentGit.Host(web, "rev-list", "refs/heads/bulk").Split('\n');
        var pair = all.GroupBy(s => s[..7]).FirstOrDefault(g => g.Count() >= 2 && g.Select(s => s[..8]).Distinct().Count() == g.Count());
        Assert.True(pair is not null, "no 7-digit collision among the bulk commits (try more)");
        var (a, b) = (pair!.ElementAt(0), pair.ElementAt(1));

        var answer = await Resolve(new JsonObject { ["team"] = "web" }, [a[..7], a[..8], b[..8]]);
        var both = MatchesOf(answer, a[..7]);
        Assert.Equal(new[] { a, b }.Order(), both.Select(m => m!["fullId"]!.GetValue<string>()).Order());
        Assert.All(both, m => Assert.StartsWith("bulk ", m!["title"]!.GetValue<string>()));
        Assert.Equal(a, MatchesOf(answer, a[..8]).Single()!["fullId"]!.GetValue<string>());
        Assert.Equal(b, MatchesOf(answer, b[..8]).Single()!["fullId"]!.GetValue<string>());
        Fixture("resolve-one-repo-collision", new JsonObject { ["a"] = a, ["b"] = b, ["answer"] = answer.DeepClone() });
    }

    [Fact]
    public async Task MainAndWritableExtra_TheSameCommitInBoth_TwoMatches_AndAGoneExtraIsAnErrorNotAFailure()
    {
        var (webId, web) = await TeamAsync("web");
        var sha = Commit(web, "Web: fix the refund button");
        var extra = Path.Combine(_db.Dir, "web-cloud");
        Directory.CreateDirectory(extra);
        AgentGit.Host(extra, "init", "-q", "-b", "main");
        Share(extra, web, "main", sha);
        AgentGit.Host(extra, "update-ref", "refs/remotes/origin/main", sha);
        await _bus.Repos!.AddExtraAsync(webId, "web", "web-cloud", extra, null, writable: true, Ct);

        var answer = await Resolve(new JsonObject { ["teamId"] = webId }, [sha[..9]]);
        var two = MatchesOf(answer, sha[..9]);
        Assert.Equal(["main", "web-cloud"], two.Select(m => m!["repo"]?.GetValue<string>() ?? "main"));
        Assert.Equal([false, true], two.Select(m => m!["pushed"]!.GetValue<bool>()));
        // Each opens in the Commit view of its repository with the same pushed.
        foreach (var m in two)
        {
            var p = new JsonObject { ["teamId"] = webId, ["sha"] = sha };
            if (m!["repo"] is { } repo) p["repo"] = repo.GetValue<string>();
            var show = await Call("git.commitShow", p);
            Assert.True(show["found"]!.GetValue<bool>());
            Assert.Equal(m["pushed"]!.GetValue<bool>(), show["where"]!["pushed"]!.GetValue<bool>());
        }
        Fixture("resolve-main-and-extra", new JsonObject { ["sha"] = sha, ["answer"] = answer.DeepClone() });

        // The extra's folder disappears: the main repository still answers; the extra is reported, not thrown.
        Directory.Delete(extra, recursive: true);
        var after = await Resolve(new JsonObject { ["teamId"] = webId }, [sha[..9]]);
        Assert.Null(MatchesOf(after, sha[..9]).Single()!["repo"]);
        var error = after["errors"]!.AsArray().Single()!;
        Assert.Equal("web-cloud", error["repo"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheCost_TheSameGitProcessesFor1IdAnd200FullIds_PerRepository()
    {
        var (_, web) = await TeamAsync("web");
        var (_, cloud) = await TeamAsync("cloud");
        var shas = new List<string>();
        for (var i = 0; i < 12; i++) shas.Add(Commit(web, $"Commit {i}"));
        AgentGit.Host(web, "update-ref", "refs/remotes/origin/main", shas[3]);
        Commit(cloud, "Cloud start");

        async Task<Dictionary<string, int>> Count(IReadOnlyList<string> ids)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _bus.Repos!.Git.OnRun = (dir, _) => { lock (counts) counts[dir ?? ""] = counts.GetValueOrDefault(dir ?? "") + 1; };
            try
            {
                var answer = await Resolve(new JsonObject(), ids);
                Assert.False(answer.ContainsKey("errors"), answer.ToJsonString());
                foreach (var s in shas) if (ids.Contains(s)) Assert.Single(MatchesOf(answer, s));
            }
            finally { _bus.Repos!.Git.OnRun = null; }
            return counts;
        }

        var one = await Count([shas[0]]);
        // 200 distinct FULL IDs (the longest command line): the 12 real ones and 188 made-up ones.
        var many = shas.Concat(Enumerable.Range(0, 200 - shas.Count).Select(i => (i * 7919 + 1).ToString("x8").PadRight(40, 'e'))).ToList();
        Assert.Equal(200, many.Distinct().Count());
        var lots = await Count(many);
        var webKey = one.Keys.Single(k => Path.GetFullPath(k).Equals(Path.GetFullPath(web), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, one[webKey]);              // rev-parse, log, rev-list
        Assert.Equal(one[webKey], lots[webKey]);   // the same for 200 IDs
        Assert.True(one.Values.Sum() <= 3 * one.Count && lots.Values.Sum() <= 3 * lots.Count, string.Join(", ", lots));
    }

    [Fact]
    public void AVisitorMayCallIt_ByTeamOrForEveryTeam_AndItIsNotAudited()
    {
        foreach (var p in new[] { new JsonObject { ["ids"] = Ids(["cafe123"]) }, new JsonObject { ["team"] = "web", ["ids"] = Ids(["cafe123"]) } })
        {
            Assert.True(RemoteAccess.Reads("git.commitsResolve", p));
            Assert.Null(RemoteAccess.Refusal("git.commitsResolve", p, false, RemoteAccess.Read));
        }
        Assert.True(AuditLog.IsReadOnly("git.commitsResolve"));
    }
}
