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
/// git.commitShow and git.commitDiff through the real dispatcher, as the window calls them:
/// a malformed or missing sha is bad_params, a commit git does not have is exactly {found:false, goneReason, sha},
/// its diff is "gone", a missing path is bad_params, and a found commit says codeShown true locally.
/// </summary>
public sealed class CommitViewRpcTests : IAsyncLifetime
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

    [Fact]
    public async Task TheWindowsCalls_GiveTheContractsShapesAndCodes()
    {
        var teamId = await _bus.CreateTeamAsync("web", "Manager", "claude", null, Ct);
        await _bus.AddMemberAsync(teamId, "Ada", "claude", Ct);
        var status = await Call("git.status", new JsonObject { ["teamId"] = teamId });
        var baseSha = status["baseSha"]!.GetValue<string>();

        var found = await Call("git.commitShow", new JsonObject { ["teamId"] = teamId, ["sha"] = baseSha });
        Assert.True(found["found"]!.GetValue<bool>());
        Assert.True(found["codeShown"]!.GetValue<bool>());
        Assert.Equal(baseSha, found["sha"]!.GetValue<string>());
        Assert.DoesNotContain("main", found["where"]!["branches"]!.AsArray().Select(b => b!.GetValue<string>()));
        // By team name too.
        Assert.True((await Call("git.commitShow", new JsonObject { ["team"] = "web", ["sha"] = baseSha[..7] }))["found"]!.GetValue<bool>());

        // Not found: only found, goneReason and sha.
        const string unknown = "0123456789abcdef0123456789abcdef01234567";
        var gone = await Call("git.commitShow", new JsonObject { ["teamId"] = teamId, ["sha"] = unknown });
        Assert.Equal(["found", "goneReason", "sha"], gone.Select(p => p.Key).Order());
        Assert.False(gone["found"]!.GetValue<bool>());
        Assert.Equal(unknown, gone["sha"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(gone["goneReason"]!.GetValue<string>()));

        // Malformed or missing sha: bad_params, for both methods.
        foreach (var bad in new[] { "HEAD", "--all", "main", "abc", "12345678 --", "g1234567", "", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0" })
        {
            Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => Call("git.commitShow", new JsonObject { ["teamId"] = teamId, ["sha"] = bad }))).Code);
            Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() =>
                Call("git.commitDiff", new JsonObject { ["teamId"] = teamId, ["sha"] = bad, ["path"] = "README.md" }))).Code);
        }
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => Call("git.commitShow", new JsonObject { ["teamId"] = teamId }))).Code);
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => Call("git.commitDiff", new JsonObject { ["teamId"] = teamId, ["sha"] = baseSha }))).Code);

        // A commit git does not have: its diff is "gone"; a file the commit does not change: no_such_file.
        Assert.Equal("gone", (await Assert.ThrowsAsync<RpcException>(() =>
            Call("git.commitDiff", new JsonObject { ["teamId"] = teamId, ["sha"] = unknown, ["path"] = "README.md" }))).Code);
        Assert.Equal("no_such_file", (await Assert.ThrowsAsync<RpcException>(() =>
            Call("git.commitDiff", new JsonObject { ["teamId"] = teamId, ["sha"] = baseSha, ["path"] = "no/such/file.txt" }))).Code);
    }
}
