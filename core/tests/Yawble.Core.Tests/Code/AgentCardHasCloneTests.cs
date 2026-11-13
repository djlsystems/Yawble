using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Code;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Code;

/// <summary>
/// teams.agents sends each card's hasClone, true when the member's clone folder is on disk. The shell
/// shows the Code icon only then, and it is the same test code.open's clonePresent makes.
/// </summary>
public sealed class AgentCardHasCloneTests : IAsyncLifetime
{
    private readonly TempDb _db = new();
    private string _appRoot = null!;
    private BusEngine _bus = null!;
    private RpcDispatcher _rpc = null!;

    public async ValueTask InitializeAsync()
    {
        _appRoot = Path.Combine(_db.Dir, "app");
        Directory.CreateDirectory(_appRoot);
        var terminals = new TerminalHub(new AppEvents());
        var policies = new PolicyStore(_db.ConnectionString);
        BusEngine? busRef = null;
        var runner = new FakePtyRunner(terminals, id => busRef!.MemberAsync(id).GetAwaiter().GetResult()?.Cli ?? "claude", ticks: 1, tick: TimeSpan.FromMilliseconds(10));
        _bus = await BusHost.StartAsync(_db.ConnectionString, _ => null, null, launcher: null, runner, () => 4, appDataRoot: _appRoot);
        busRef = _bus;
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var ui = new UiRpc(_bus, policies, new MemberLayouts(Path.Combine(_appRoot, "data"), _db.Dir, home), new FakeModelCredentials(),
            new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, _bus), terminals, 16_384);
        _rpc = new RpcDispatcher(new Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private async Task<JsonArray> Agents(long teamId) =>
        (await _rpc.DispatchAsync("teams.agents", new JsonObject { ["teamId"] = teamId }, TestContext.Current.CancellationToken))!.AsArray();

    private static bool HasClone(JsonArray agents, string name) =>
        agents.Single(a => a!["member"]!["name"]!.GetValue<string>() == name)!["hasClone"]!.GetValue<bool>();

    [Fact]
    public async Task HasCloneIsTrueOnlyForAMemberWhoseCloneIsOnDisk()
    {
        var teamId = await _bus.CreateTeamAsync("web", "Manager", "claude", null, TestContext.Current.CancellationToken);
        await _bus.AddMemberAsync(teamId, "Ada", "claude", TestContext.Current.CancellationToken);
        await _bus.AddMemberAsync(teamId, "Ben", "claude", TestContext.Current.CancellationToken);

        var before = await Agents(teamId);
        Assert.All(before, a => Assert.False(a!["hasClone"]!.GetValue<bool>()));

        // Ada's clone appears where the run would make it: <member root>/repo.
        Directory.CreateDirectory(Path.Combine(_bus.Repos!.MemberRoot("web", "Ada"), "repo"));
        var after = await Agents(teamId);
        Assert.True(HasClone(after, "Ada"));
        Assert.False(HasClone(after, "Ben"));
        Assert.False(HasClone(after, "Manager"));
    }

    [Fact]
    public void WithoutTeamRepositoriesNoOneHasAClone()
    {
        var row = new MemberRow(1, 1, "web", "Ada", "member", "claude", "", "", "");
        Assert.False(CodeHost.HasClone(null, row));
    }
}
