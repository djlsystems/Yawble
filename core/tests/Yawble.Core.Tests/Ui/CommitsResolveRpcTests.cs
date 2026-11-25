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
/// git.commitsResolve through the real dispatcher, as the window calls it (by team id, by team
/// name, and for every team), its bad_params, and that a read-only viewer (Visitor) may call it remotely and the audit
/// log counts it as a read.
/// </summary>
public sealed class CommitsResolveRpcTests : IAsyncLifetime
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

    private static JsonArray Ids(params string[] ids) => new([.. ids.Select(i => (JsonNode?)i)]);

    [Fact]
    public async Task ByTeamIdNameOrEveryTeam_TheContractsShape()
    {
        var teamId = await _bus.CreateTeamAsync("web", "Manager", "claude", null, Ct);
        await _bus.AddMemberAsync(teamId, "Ada", "claude", Ct);
        var baseSha = (await Call("git.status", new JsonObject { ["teamId"] = teamId }))["baseSha"]!.GetValue<string>();

        foreach (var p in new[]
                 {
                     new JsonObject { ["teamId"] = teamId, ["ids"] = Ids(baseSha[..7], "cafe123", "zz") },
                     new JsonObject { ["team"] = "web", ["ids"] = Ids(baseSha[..7], "cafe123", "zz") },
                     new JsonObject { ["ids"] = Ids(baseSha[..7], "cafe123", "zz") },
                 })
        {
            var done = await Call("git.commitsResolve", p);
            Assert.Equal(200, done["max"]!.GetValue<int>());
            var results = done["results"]!.AsArray();
            Assert.Equal([baseSha[..7], "cafe123", "zz"], results.Select(x => x!["id"]!.GetValue<string>()));
            var match = results[0]!["matches"]!.AsArray().Single()!.AsObject();
            Assert.Equal("web", match["team"]!.GetValue<string>());
            Assert.Equal(teamId, match["teamId"]!.GetValue<long>());
            Assert.Null(match["repo"]);
            Assert.Equal(baseSha, match["fullId"]!.GetValue<string>());
            Assert.False(match["pushed"]!.GetValue<bool>());
            Assert.Empty(results[1]!["matches"]!.AsArray());
            Assert.False(results[2]!["valid"]!.GetValue<bool>());

            // The match opens in the Commit view as given.
            Assert.True((await Call("git.commitShow", new JsonObject { ["teamId"] = teamId, ["sha"] = match["fullId"]!.GetValue<string>() }))["found"]!.GetValue<bool>());
        }

        Assert.Empty((await Call("git.commitsResolve", new JsonObject { ["teamId"] = teamId, ["ids"] = new JsonArray() }))["results"]!.AsArray());
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => Call("git.commitsResolve", new JsonObject { ["teamId"] = teamId }))).Code);
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() =>
            Call("git.commitsResolve", new JsonObject { ["teamId"] = teamId, ["ids"] = new JsonArray(1, 2) }))).Code);
        var tooMany = Ids([.. Enumerable.Range(0, 201).Select(i => i.ToString("x8"))]);
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() =>
            Call("git.commitsResolve", new JsonObject { ["teamId"] = teamId, ["ids"] = tooMany }))).Code);
        Assert.Equal("no_such_team", (await Assert.ThrowsAsync<RpcException>(() =>
            Call("git.commitsResolve", new JsonObject { ["team"] = "nope", ["ids"] = Ids("cafe123") }))).Code);
    }

    [Fact]
    public void AVisitorMayCallIt_AndItIsAuditedAsARead()
    {
        var p = new JsonObject { ["ids"] = Ids("cafe123") };
        Assert.True(RemoteAccess.Reads("git.commitsResolve", p));
        Assert.Null(RemoteAccess.Refusal("git.commitsResolve", p, false, RemoteAccess.Read));
        Assert.Null(RemoteAccess.Refusal("git.commitsResolve", p, false, RemoteAccess.Full));
        Assert.True(AuditLog.IsReadOnly("git.commitsResolve"));
    }
}
