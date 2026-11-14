using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Policy;
using Yawble.Core.Tests.Ui;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// B022 (card 109): "Allow for this team" checks the host against every member the write lands on. The team's member ids
/// are read again inside the write's transaction; a list that is not exactly them (a member hired while the layouts were
/// built, or a list missing one) is refused as stale, never as a host refusal, and AllowForTeamAsync rebuilds the list and
/// tries again a bounded number of times. Fakes only: a temporary database, fake runners and credentials; no network.
/// </summary>
public sealed class TeamNetworkStaleListTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly RunLayout _layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
    private TerminalHub _terminals = null!;
    private BusEngine _bus = null!;
    private RpcDispatcher _rpc = null!;
    private PolicyStore _policies = null!;

    public async ValueTask InitializeAsync()
    {
        var hub = new AppEvents();
        _terminals = new TerminalHub(hub);
        _policies = new PolicyStore(_db.ConnectionString);
        BusEngine? busRef = null;
        var runner = new FakePtyRunner(_terminals, id => busRef!.MemberAsync(id).GetAwaiter().GetResult()?.Cli ?? "claude", ticks: 3, tick: TimeSpan.FromMilliseconds(40));
        _bus = await BusHost.StartAsync(_db.ConnectionString, _ => null, null, runner, () => _policies.GetGlobal().App.MaxConcurrentAgents);
        busRef = _bus;
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
        var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
        var ui = new UiRpc(_bus, _policies, new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
            new FakeHostTerminals(_terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(_terminals, _bus), _terminals, 16_384);
        _rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui, audit: new AuditLog(_db.ConnectionString));
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private async Task<JsonNode?> Call(string method, object? args = null) =>
        await _rpc.DispatchAsAsync(Actor.Window, method, args is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

    private async Task<long> CreateTeam(string name, params string[] members)
    {
        var r = await Call("teams.create", new { name, managerCli = "claude", members = members.Select(m => new { name = m, cli = "claude" }).ToArray() });
        return r!["teamId"]!.GetValue<long>();
    }

    private async Task<long> Hire(long teamId, string name) =>
        (await Call("members.add", new { teamId, name, cli = "claude" }))!["id"]!.GetValue<long>();

    private List<(long, RunLayout)> Members(long teamId) => _policies.TeamMemberIds(teamId).Select(id => (id, _layout)).ToList();

    // ------------------------------------------------------------------ a member hired during the add is checked

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMemberHiredWhileTheLayoutsAreBuilt_IsCheckedBeforeTheWrite(bool newMemberRefuses)
    {
        var team = await CreateTeam("payments", "Ada");
        // The new hire's run is told apart by its layout (its own instance); the dry run refuses only that run when asked to.
        var cleoLayout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
        var checkedRuns = new List<RunLayout>();
        var checking = new PolicyStore(_db.ConnectionString, (_, _, _, layout) =>
        {
            lock (checkedRuns) checkedRuns.Add(layout);
            return newMemberRefuses && ReferenceEquals(layout, cleoLayout) ? ["the sandbox refused the compiled request: test"] : [];
        });
        long? cleo = null;
        var policy = new StoreNetworkPolicy(checking, async (id, ct) =>
        {
            // Hired after the add listed the team's members and before its write: the first list misses Cleo.
            cleo ??= await Hire(team, "Cleo");
            return id == cleo ? cleoLayout : _layout;
        }, _ => Task.FromResult<RunLayout?>(_layout));

        if (newMemberRefuses)
        {
            var e = await Assert.ThrowsAsync<PolicyException>(() => policy.AllowForTeamAsync(team, "pypi.org", Ct));
            Assert.Contains(e.Errors, x => x.Contains("sandbox refused", StringComparison.Ordinal));
            Assert.Empty(_policies.TeamHosts(team));
            Assert.DoesNotContain("pypi.org", _policies.Effective(cleo!.Value, _layout).Policy.AllowHosts);
        }
        else
        {
            await policy.AllowForTeamAsync(team, "pypi.org", Ct);
            Assert.Equal(["pypi.org"], _policies.TeamHosts(team).Select(h => h.Host));
            Assert.Contains("pypi.org", _policies.Effective(cleo!.Value, _layout).Policy.AllowHosts);
        }
        Assert.Contains(checkedRuns, l => ReferenceEquals(l, cleoLayout)); // the new member's run was checked
    }

    // ------------------------------------------------------------------ AddTeamHost refuses a stale list itself

    [Fact]
    public async Task AddTeamHost_RefusesAListMissingACurrentMember_AsStale_NotAsAHostRefusal_AndWritesNothing()
    {
        var team = await CreateTeam("payments", "Ada", "Ben");
        var cleo = await Hire(team, "Cleo");
        var missing = Members(team).Where(m => m.Item1 != cleo).ToList();

        var e = Assert.Throws<StaleMemberListException>(() => _policies.AddTeamHost(team, "pypi.org", missing, "pat", Actor.App));

        Assert.Equal(team, e.TeamId);
        Assert.Contains(cleo, e.CurrentIds);
        Assert.DoesNotContain(cleo, e.CheckedIds);
        Assert.Empty(_policies.TeamHosts(team));
        // An empty list for a team with members is stale too; the full list is written.
        Assert.Throws<StaleMemberListException>(() => _policies.AddTeamHost(team, "pypi.org", [], "pat", Actor.App));
        Assert.Empty(_policies.TeamHosts(team));
        Assert.True(_policies.AddTeamHost(team, "pypi.org", Members(team), "pat", Actor.App));
    }

    [Fact]
    public async Task AddTeamHost_RefusesAListWithAMemberNoLongerOnTheTeam()
    {
        var team = await CreateTeam("payments", "Ada");
        var cleo = await Hire(team, "Cleo");
        var before = Members(team);
        await Call("members.remove", new { memberId = cleo });

        var e = Assert.Throws<StaleMemberListException>(() => _policies.AddTeamHost(team, "pypi.org", before, "pat", Actor.App));

        Assert.Contains(cleo, e.CheckedIds);
        Assert.DoesNotContain(cleo, e.CurrentIds);
        Assert.Empty(_policies.TeamHosts(team));
    }

    [Fact]
    public async Task AFloorRefusedHost_IsStillAPolicyException_WithAStaleList()
    {
        var team = await CreateTeam("payments", "Ada");
        Assert.Throws<PolicyException>(() => _policies.AddTeamHost(team, "api.anthropic.com", [], "pat", Actor.App));
        Assert.Empty(_policies.TeamHosts(team));
    }

    // ------------------------------------------------------------------ the retry is bounded

    [Fact]
    public async Task AMemberListThatKeepsChanging_GivesUpAfterTheBound_WritesNothing_AndTheAskStaysWaiting()
    {
        var team = await CreateTeam("payments", "Ada");
        var attempts = 0;
        string[] names = ["Cleo", "Dana", "Eve", "Finn", "Gus", "Hal"];
        long? first = null;
        // Every attempt lists the members (lowest id first), then a new one is hired before its write.
        var policy = new StoreNetworkPolicy(_policies, async (id, ct) =>
        {
            first ??= id;
            if (id == first)
            {
                attempts++;
                await Hire(team, names[attempts - 1]);
            }
            return _layout;
        }, _ => Task.FromResult<RunLayout?>(_layout));
        var asks = new NetworkAskService(_db.ConnectionString, policy, (_, _, _, _, _) => Task.CompletedTask);
        asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);

        var e = await Assert.ThrowsAsync<RpcException>(() => asks.DispatchAsync("network.answer", new JsonObject { ["id"] = Assert.Single(asks.Asks()).Id, ["choice"] = "team" }, Ct));

        Assert.Equal("members_changed", e.Code);
        Assert.Contains("pypi.org", e.Message);
        Assert.Contains($"{StoreNetworkPolicy.TeamAddAttempts} tries", e.Message);
        Assert.Equal(StoreNetworkPolicy.TeamAddAttempts, attempts);
        Assert.Empty(_policies.TeamHosts(team));
        Assert.Equal("pending", Assert.Single(asks.Asks()).State);
    }

    [Fact]
    public async Task OneChangeDuringTheAdd_IsRetried_AndTheHostIsAddedOnce()
    {
        var team = await CreateTeam("payments", "Ada");
        var calls = 0;
        var hired = false;
        var policy = new StoreNetworkPolicy(_policies, async (_, ct) =>
        {
            calls++;
            if (!hired) { hired = true; await Hire(team, "Cleo"); }
            return _layout;
        }, _ => Task.FromResult<RunLayout?>(_layout));

        await policy.AllowForTeamAsync(team, "pypi.org", Ct);

        Assert.Equal(["pypi.org"], _policies.TeamHosts(team).Select(h => h.Host));
        // the first list (without Cleo) and the rebuilt one (with Cleo) both had their layouts built
        Assert.True(calls > _policies.TeamMemberIds(team).Count);
    }
}
