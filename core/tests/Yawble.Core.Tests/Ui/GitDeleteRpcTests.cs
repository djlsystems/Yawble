using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Teams;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>
/// Card 75: git.deleteBranches and git.status's oldBranches through the real dispatcher, as the Git dialog calls them:
/// the shapes ({plan, token, empty}, {result}), error code "stale", and oldBranches always present. Local repositories only.
/// </summary>
public sealed class GitDeleteRpcTests : IAsyncLifetime
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
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private async Task<JsonObject> Call(string method, JsonObject p) => (await _rpc.DispatchAsync(method, p, Ct))!.AsObject();

    private static JsonObject Args(long teamId, string? confirm = null, params string[] branches)
    {
        var o = new JsonObject { ["teamId"] = teamId, ["branches"] = new JsonArray(branches.Select(b => (JsonNode?)b).ToArray()), ["remote"] = false };
        if (confirm is not null) o["confirm"] = confirm;
        return o;
    }

    [Fact]
    public async Task PlanConfirmAndStale_AsTheGitDialogCallsThem()
    {
        var teamId = await _bus.CreateTeamAsync("web", "Manager", "claude", null, Ct);
        await _bus.AddMemberAsync(teamId, "Ada", "claude", Ct);
        var status = await Call("git.status", new JsonObject { ["teamId"] = teamId });
        Assert.Empty(status["oldBranches"]!.AsArray()); // present, and empty when there are none
        var repo = status["repoPath"]!.GetValue<string>();
        var baseSha = status["baseSha"]!.GetValue<string>();
        AgentGit.Host(repo, "branch", "members/web/ben", baseSha);
        AgentGit.Host(repo, "branch", "members/ben", baseSha);
        var old = Assert.Single((await Call("git.status", new JsonObject { ["teamId"] = teamId }))["oldBranches"]!.AsArray())!;
        Assert.Equal("members/ben", old["name"]!.GetValue<string>());
        Assert.True(old["local"]!.GetValue<bool>());
        Assert.False(old["remote"]!.GetValue<bool>());
        Assert.True(old["merged"]!.GetValue<bool>());

        // Never "every merged branch": the dialog names them.
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => Call("git.deleteBranches", Args(teamId)))).Code);

        var plan = await Call("git.deleteBranches", Args(teamId, null, "members/web/ben", "members/ben"));
        Assert.False(plan["empty"]!.GetValue<bool>());
        Assert.Contains("delete members/web/ben", plan["plan"]!.GetValue<string>());
        Assert.Null(plan["result"]);
        var token = plan["token"]!.GetValue<string>();
        Assert.Equal(baseSha, AgentGit.Host(repo, "rev-parse", "members/web/ben")); // a plan changes nothing

        // The person waits; Ben's branch moves on: the confirm is stale and nothing is deleted.
        var tree = AgentGit.Host(repo, "rev-parse", baseSha + "^{tree}");
        var next = AgentGit.Host(repo, "-c", "user.name=b", "-c", "user.email=b@x", "commit-tree", tree, "-p", baseSha, "-m", "more");
        AgentGit.Host(repo, "update-ref", "refs/heads/members/web/ben", next);
        var stale = await Assert.ThrowsAsync<RpcException>(() => Call("git.deleteBranches", Args(teamId, token, "members/web/ben", "members/ben")));
        Assert.Equal("stale", stale.Code);
        Assert.Equal(next, AgentGit.Host(repo, "rev-parse", "members/web/ben"));
        Assert.Equal(baseSha, AgentGit.Host(repo, "rev-parse", "members/ben"));

        // Shown again, the new plan unmerged-keeps the moved branch and deletes the old-style one.
        var again = await Call("git.deleteBranches", Args(teamId, null, "members/web/ben", "members/ben"));
        Assert.Contains("KEPT", again["plan"]!.GetValue<string>());
        var done = await Call("git.deleteBranches", Args(teamId, again["token"]!.GetValue<string>(), "members/web/ben", "members/ben"));
        Assert.Contains($"deleted members/ben; was {baseSha}", done["result"]!.GetValue<string>());
        Assert.Null(done["plan"]);
        Assert.Equal(next, AgentGit.Host(repo, "rev-parse", "members/web/ben"));
        Assert.Empty((await Call("git.status", new JsonObject { ["teamId"] = teamId }))["oldBranches"]!.AsArray());
    }

    /// <summary>B020 / card 92: git.status's blockingBranches, the contract the Git dialog reads: always present, each
    /// { name, repo, location: "local" | "remote" }, and deleted through git.deleteBranches like any other branch.</summary>
    [Fact]
    public async Task BlockingBranches_AreListedWithNameRepoAndLocation_AndDeleteThroughTheUsualPath()
    {
        var teamId = await _bus.CreateTeamAsync("docs", "Manager", "claude", null, Ct);
        var status = await Call("git.status", new JsonObject { ["teamId"] = teamId });
        Assert.Empty(status["blockingBranches"]!.AsArray());
        var repo = status["repoPath"]!.GetValue<string>();
        var baseSha = status["baseSha"]!.GetValue<string>();
        AgentGit.Host(repo, "branch", "members/docs", baseSha); // another team's old member "Docs"

        var b = Assert.Single((await Call("git.status", new JsonObject { ["teamId"] = teamId }))["blockingBranches"]!.AsArray())!.AsObject();
        Assert.Equal(["location", "name", "repo"], b.Select(p => p.Key).Order());
        Assert.Equal("members/docs", b["name"]!.GetValue<string>());
        Assert.Equal("main", b["repo"]!.GetValue<string>());
        Assert.Equal("local", b["location"]!.GetValue<string>());

        var plan = await Call("git.deleteBranches", Args(teamId, null, "members/docs"));
        var done = await Call("git.deleteBranches", Args(teamId, plan["token"]!.GetValue<string>(), "members/docs"));
        Assert.Contains("deleted members/docs", done["result"]!.GetValue<string>());
        Assert.Empty((await Call("git.status", new JsonObject { ["teamId"] = teamId }))["blockingBranches"]!.AsArray());
    }
}
