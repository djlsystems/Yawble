using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Policy;
using Yawble.Core.Tests.Ui;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// B014 test (card 73) gap tests, beside TeamNetworkAllowTests: the team list survives a member's narrowing override and
/// is per team; Remove matches the host however it is typed; a removed member's older ask (no member_id) closes, another
/// team's member with the same name keeps its own; a re-hire with the same name gets a fresh ask; a name resolving to a
/// public address as well as a private one is still asked about. Fakes only: a temporary database, fake runners, an
/// in-memory network; no real network, no system programs.
/// </summary>
public sealed class TeamNetworkAllowGapTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly RunLayout _layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
    private TerminalHub _terminals = null!;
    private BusEngine _bus = null!;
    private RpcDispatcher _rpc = null!;
    private PolicyStore _policies = null!;
    private NetworkAskService _asks = null!;

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
        _asks = new NetworkAskService(_db.ConnectionString, Store(_policies), (_, _, _, _, _) => Task.CompletedTask);
        _bus.NetworkAsks = _asks;
        _rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui, audit: new AuditLog(_db.ConnectionString), networkAsks: _asks);
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private StoreNetworkPolicy Store(PolicyStore store) => new(store, (_, _) => Task.FromResult(_layout), _ => Task.FromResult<RunLayout?>(_layout));

    private async Task<JsonNode?> Call(string method, object? args = null) =>
        await _rpc.DispatchAsAsync(Actor.Window, method, args is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

    private async Task<long> CreateTeam(string name, params string[] members)
    {
        var r = await Call("teams.create", new { name, managerCli = "claude", members = members.Select(m => new { name = m, cli = "claude" }).ToArray() });
        return r!["teamId"]!.GetValue<long>();
    }

    private async Task<long> IdOf(long teamId, string name) =>
        (await Call("teams.agents", new { teamId }))!.AsArray().Single(a => a!["member"]!["name"]!.GetValue<string>() == name)!["member"]!["id"]!.GetValue<long>();

    private async Task<long> Hire(long teamId, string name) =>
        (await Call("members.add", new { teamId, name, cli = "claude" }))!["id"]!.GetValue<long>();

    /// <summary>The team's real member list, each with the test layout (what AllowForTeamAsync builds).</summary>
    private List<(long, RunLayout)> Members(long teamId) => _policies.TeamMemberIds(teamId).Select(id => (id, _layout)).ToList();

    private long Scalar(string sql)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private void Exec(string sql)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ the team list at launch

    [Fact]
    public async Task ATeamHost_ReachesAMemberWhoseOverrideNarrowsTheGlobalList_AsTheLoosenBlockDoes()
    {
        var team = await CreateTeam("payments", "Ada");
        var ada = await IdOf(team, "Ada");
        _policies.SaveOverride(ada, PolicyPatch.Empty with { Network = new NetworkPatch([]) }, _layout);
        _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);

        await _asks.AnswerAsync(Assert.Single(_asks.Asks()).Id, NetworkAskChoices.Team, null, Ct);

        var merged = _policies.Effective(ada, _layout);
        Assert.Empty(merged.Violations);
        Assert.Contains("pypi.org", merged.Policy.AllowHosts);
        Assert.Contains("network.allowHosts + pypi.org (team)", merged.Policy.Loosened);
        var view = (await Call("policy.getMember", new { memberId = ada }))!;
        Assert.Equal(["pypi.org"], view["teamHosts"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.Contains("network.allowHosts + pypi.org (team)", view["effective"]!["loosened"]!.AsArray().Select(h => h!.GetValue<string>()));
    }

    [Fact]
    public async Task TwoTeamsWithTheSameHost_RemoveFromOne_LeavesTheOther_AndRemoveMatchesTheHostHoweverTyped()
    {
        var a = await CreateTeam("payments", "Ada");
        var b = await CreateTeam("docs", "Dee");
        _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);
        _asks.Refused("docs/Dee", "pypi.org", "CONNECT", null);
        foreach (var ask in _asks.Asks().ToList()) await _asks.AnswerAsync(ask.Id, NetworkAskChoices.Team, null, Ct);

        var after = await Call("policy.removeTeamHost", new { teamId = a, host = "  PyPI.org " });

        Assert.Empty(after!["hosts"]!.AsArray());
        Assert.Equal(a, after["teamId"]!.GetValue<long>());
        Assert.DoesNotContain("pypi.org", _policies.Effective(await IdOf(a, "Ada"), _layout).Policy.AllowHosts);
        Assert.Contains("pypi.org", _policies.Effective(await IdOf(b, "Dee"), _layout).Policy.AllowHosts);
        Assert.Equal(["pypi.org"], _policies.TeamHosts(b).Select(h => h.Host));
    }

    [Fact]
    public async Task TeamHosts_ListsOldestFirst_AndANoSuchTeamIsRefused()
    {
        var team = await CreateTeam("payments", "Ada");
        _policies.AddTeamHost(team, "pypi.org", Members(team), "pat", Actor.App);
        await Task.Delay(15, Ct);
        _policies.AddTeamHost(team, "registry.npmjs.org", Members(team), "pat", Actor.App);
        Assert.False(_policies.AddTeamHost(team, "pypi.org", Members(team), "pat", Actor.App)); // a repeat adds nothing

        var hosts = (await Call("policy.teamHosts", new { teamId = team }))!["hosts"]!.AsArray();

        Assert.Equal(["pypi.org", "registry.npmjs.org"], hosts.Select(h => h!["host"]!.GetValue<string>()));
        await Assert.ThrowsAsync<RpcException>(() => Call("policy.teamHosts", new { teamId = 99_999 }));
        await Assert.ThrowsAsync<RpcException>(() => Call("policy.removeTeamHost", new { teamId = 99_999, host = "pypi.org" }));
    }

    // ------------------------------------------------------------------ B022 (card 127): a member hired mid-add is checked

    /// <summary>
    /// A store whose fake dry run hires Cleo (as members.add writes her row) on its first check - after AllowForTeamAsync
    /// built its list, before the write - and refuses any member whose override narrows the network to nothing.
    /// </summary>
    private (PolicyStore Store, Func<long?> Cleo) HiringStore(long team, bool cleoRefuses)
    {
        long? cleo = null;
        var store = new PolicyStore(_db.ConnectionString, (_, o, _, _) =>
        {
            if (cleo is null)
            {
                cleo = Scalar($"INSERT INTO members (team_id, name, role, created_at) VALUES ({team}, 'Cleo', 'member', '2026-11-14T00:00:00Z') RETURNING id;");
                if (cleoRefuses) _policies.SaveOverride(cleo.Value, PolicyPatch.Empty with { Network = new NetworkPatch([]) }, _layout);
            }
            return o?.Network?.AllowHosts is { Count: 0 } ? ["the sandbox refused the compiled request: no hosts for this member"] : [];
        });
        return (store, () => cleo);
    }

    [Fact]
    public async Task AllowForTeam_AMemberHiredMidAdd_IsCheckedToo_AndTheirRefusalStopsTheAdd()
    {
        var team = await CreateTeam("payments", "Ada");
        var (store, cleo) = HiringStore(team, cleoRefuses: true);
        var layoutsFor = new List<long>();
        var policy = new StoreNetworkPolicy(store, (id, _) => { lock (layoutsFor) layoutsFor.Add(id); return Task.FromResult(_layout); }, _ => Task.FromResult<RunLayout?>(_layout));

        var e = await Assert.ThrowsAsync<PolicyException>(() => policy.AllowForTeamAsync(team, "pypi.org", Ct));

        Assert.IsNotType<TeamMembersChangedException>(e);
        Assert.Equal(["the sandbox refused the compiled request: no hosts for this member"], e.Errors);
        Assert.Contains(cleo()!.Value, layoutsFor); // the list was built again, with Cleo's run layout
        Assert.Empty(_policies.TeamHosts(team));
    }

    [Fact]
    public async Task AllowForTeam_AMemberHiredMidAdd_WhoAccepts_TheHostIsWrittenAfterTheirCheck()
    {
        var team = await CreateTeam("payments", "Ada");
        var (store, cleo) = HiringStore(team, cleoRefuses: false);
        var layoutsFor = new List<long>();
        var policy = new StoreNetworkPolicy(store, (id, _) => { lock (layoutsFor) layoutsFor.Add(id); return Task.FromResult(_layout); }, _ => Task.FromResult<RunLayout?>(_layout));

        await policy.AllowForTeamAsync(team, "pypi.org", Ct);

        Assert.Contains(cleo()!.Value, layoutsFor);
        Assert.Equal(["pypi.org"], _policies.TeamHosts(team).Select(h => h.Host));
        Assert.Contains("pypi.org", _policies.Effective(cleo()!.Value, _layout).Policy.AllowHosts);
    }

    [Fact]
    public async Task AllowForTeam_MembersThatKeepChanging_AreRefusedClearly_NothingWritten()
    {
        var team = await CreateTeam("payments", "Ada");
        // One hire per list: the first check after each list is built hires someone new.
        var hires = 0;
        var armed = false;
        var store = new PolicyStore(_db.ConnectionString, (_, _, _, _) =>
        {
            if (armed)
            {
                armed = false;
                Scalar($"INSERT INTO members (team_id, name, role, created_at) VALUES ({team}, 'Hire{++hires}', 'member', '2026-11-14T00:00:00Z') RETURNING id;");
            }
            return [];
        });
        var policy = new StoreNetworkPolicy(store, (_, _) => { armed = true; return Task.FromResult(_layout); }, _ => Task.FromResult<RunLayout?>(_layout));

        var e = await Assert.ThrowsAsync<PolicyException>(() => policy.AllowForTeamAsync(team, "pypi.org", Ct));

        Assert.Contains("kept changing", e.Errors[0]);
        Assert.Equal(StoreNetworkPolicy.TeamMembersAttempts, hires); // one list per attempt, then refused
        Assert.Empty(_policies.TeamHosts(team));
    }

    // ------------------------------------------------------------------ B022 verify (card 128): removals, the snapshot check, the limit's edge

    [Fact]
    public async Task AllowForTeam_AMemberRemovedAfterTheChecks_BeforeTheWrite_TheListIsBuiltAgainWithoutThem()
    {
        var team = await CreateTeam("payments", "Ada", "Ben");
        var ben = await IdOf(team, "Ben");
        var removed = false;
        var checkedIds = new List<long>();
        // The dry run runs after the snapshot and before BEGIN IMMEDIATE: removing Ben there is caught inside the transaction.
        var store = new PolicyStore(_db.ConnectionString, (_, _, _, _) =>
        {
            if (!removed) { removed = true; Call("members.remove", new { memberId = ben }).GetAwaiter().GetResult(); }
            return [];
        });
        var layoutsFor = new List<long>();
        var policy = new StoreNetworkPolicy(store, (id, _) => { layoutsFor.Add(id); return Task.FromResult(_layout); }, _ => Task.FromResult<RunLayout?>(_layout));

        await policy.AllowForTeamAsync(team, "pypi.org", Ct);

        Assert.True(removed);
        Assert.DoesNotContain(ben, _policies.TeamMemberIds(team));
        Assert.Equal(1, layoutsFor.Count(id => id == ben)); // only the first list had Ben; the second was built without him
        var ada = await IdOf(team, "Ada");
        Assert.Equal(2, layoutsFor.Count(id => id == ada)); // the first list was not written: the transaction refused it and a second was built
        Assert.Equal(["pypi.org"], _policies.TeamHosts(team).Select(h => h.Host));
    }

    [Fact]
    public async Task AllowForTeam_AHireWhileTheListIsBuilt_IsCaughtByTheSnapshot_NoMemberCheckedAgainstTheOldList()
    {
        var team = await CreateTeam("payments", "Ada");
        long? cleo = null;
        var checks = 0;
        var store = new PolicyStore(_db.ConnectionString, (_, _, _, _) => { checks++; return []; });
        // The hire lands inside the list build (after TeamMemberIds was read): AddTeamHost's snapshot must refuse before any check.
        var policy = new StoreNetworkPolicy(store, async (id, _) =>
        {
            if (cleo is null) cleo = await Hire(team, "Cleo");
            return _layout;
        }, _ => Task.FromResult<RunLayout?>(_layout));

        await policy.AllowForTeamAsync(team, "pypi.org", Ct);

        var now = _policies.TeamMemberIds(team);
        Assert.Contains(cleo!.Value, now);
        Assert.Equal(now.Count, checks); // one check per member of the second list: none ran against the list without Cleo
        Assert.Equal(["pypi.org"], _policies.TeamHosts(team).Select(h => h.Host));
    }

    [Fact]
    public async Task AllowForTeam_ChangesOnEveryAttemptButTheLast_IsStillWritten()
    {
        var team = await CreateTeam("payments", "Ada");
        // One hire per list on every list but the last one allowed: the last list matches and is written.
        var hires = 0;
        var armed = false;
        var store = new PolicyStore(_db.ConnectionString, (_, _, _, _) =>
        {
            if (armed && hires < StoreNetworkPolicy.TeamMembersAttempts - 1)
            {
                armed = false;
                Scalar($"INSERT INTO members (team_id, name, role, created_at) VALUES ({team}, 'Hire{++hires}', 'member', '2026-11-14T00:00:00Z') RETURNING id;");
            }
            return [];
        });
        var policy = new StoreNetworkPolicy(store, (_, _) => { armed = true; return Task.FromResult(_layout); }, _ => Task.FromResult<RunLayout?>(_layout));

        await policy.AllowForTeamAsync(team, "pypi.org", Ct);

        Assert.Equal(StoreNetworkPolicy.TeamMembersAttempts - 1, hires);
        Assert.Equal(["pypi.org"], _policies.TeamHosts(team).Select(h => h.Host));
    }

    [Fact]
    public async Task AllowForTeam_RemovalsThatKeepHappening_AreRefusedAtTheLimit_WithTheStoreReason_NothingWritten()
    {
        var team = await CreateTeam("payments", "Ada", "Ben", "Cleo", "Dee");
        var toRemove = new Queue<long>([await IdOf(team, "Ben"), await IdOf(team, "Cleo"), await IdOf(team, "Dee")]);
        var removals = 0;
        var armed = false;
        var store = new PolicyStore(_db.ConnectionString, (_, _, _, _) =>
        {
            if (armed && toRemove.Count > 0)
            {
                armed = false;
                removals++;
                Call("members.remove", new { memberId = toRemove.Dequeue() }).GetAwaiter().GetResult();
            }
            return [];
        });
        var policy = new StoreNetworkPolicy(store, (_, _) => { armed = true; return Task.FromResult(_layout); }, _ => Task.FromResult<RunLayout?>(_layout));

        var e = await Assert.ThrowsAsync<PolicyException>(() => policy.AllowForTeamAsync(team, "pypi.org", Ct));

        Assert.IsNotType<TeamMembersChangedException>(e);
        Assert.Equal(StoreNetworkPolicy.TeamMembersAttempts, removals);
        Assert.Equal(2, e.Errors.Count);
        Assert.Contains("kept changing", e.Errors[0]);
        Assert.Contains("members changed", e.Errors[1]);
        Assert.Empty(_policies.TeamHosts(team));
    }

    // ------------------------------------------------------------------ a removed member's asks

    [Fact]
    public async Task RemovingAMember_ClosesItsOlderAskWithoutAMemberId_ButNotAnotherTeamsSameNamedMembers()
    {
        var payments = await CreateTeam("payments", "Ada");
        var docs = await CreateTeam("docs", "Ada");
        _asks.Refused("docs/Ada", "pypi.org", "CONNECT", null);
        // An ask from before 0041: no member_id.
        Exec($"INSERT INTO network_asks (at, team_id, member, host, count, last_seen) VALUES ('2026-11-13T00:00:00Z', {payments}, 'payments/Ada', 'example.org', 1, '2026-11-13T00:00:00Z');");
        Assert.Contains(_asks.Asks(), x => x.Member == "payments/Ada" && x.MemberId is null);

        await Call("members.remove", new { memberId = await IdOf(payments, "Ada") });

        var waiting = Assert.Single(_asks.Asks());
        Assert.Equal("docs/Ada", waiting.Member);
        Assert.Equal("closed", _asks.Asks(pendingOnly: false).Single(x => x.Member == "payments/Ada").State);
    }

    [Fact]
    public async Task ARehireWithTheSameName_GetsAFreshAsk_TheClosedOneStaysClosed()
    {
        var team = await CreateTeam("payments", "Ada");
        _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);
        _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);
        var old = Assert.Single(_asks.Asks());
        Assert.Equal(2, old.Count);

        await Call("members.remove", new { memberId = await IdOf(team, "Ada") });
        var newAda = await Hire(team, "Ada");

        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null));
        var fresh = Assert.Single(_asks.Asks());
        Assert.NotEqual(old.Id, fresh.Id);
        Assert.Equal(1, fresh.Count);
        Assert.Equal(newAda, fresh.MemberId);
        Assert.Equal("closed", _asks.Asks(pendingOnly: false).Single(x => x.Id == old.Id).State);
        // The person can answer the new one for the new member.
        await _asks.AnswerAsync(fresh.Id, NetworkAskChoices.Agent, null, Ct);
        Assert.Contains("pypi.org", _policies.Effective(newAda, _layout).Policy.AllowHosts);
    }

    // ------------------------------------------------------------------ the answer's lookup

    [Fact]
    public async Task TheAllow_APublicAddressBesideAPrivateOne_OrNoAnswer_IsAllowed_MappedAndUlaAreNot()
    {
        await using var f = await GatewayFixture.StartAsync();
        long team;
        using (var c = new SqliteConnection(f.Db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO teams (name, created_at) VALUES ('Alpha', '2026-11-13T00:00:00Z') RETURNING id;";
            team = (long)cmd.ExecuteScalar()!;
            cmd.CommandText = $"INSERT INTO members (team_id, name, role, created_at) VALUES ({team}, 'm6', 'member', '2026-11-13T00:00:00Z');";
            cmd.ExecuteNonQuery();
        }
        var asks = new NetworkAskService(f.Db.ConnectionString, Store(new PolicyStore(f.Db.ConnectionString)), (_, _, _, _, _) => Task.CompletedTask, f.Clock);
        f.Host.Refusals = asks;
        f.Dialer!.AddName("mixed.example.com", IPAddress.Parse("10.0.0.7"), IPAddress.Parse("93.184.216.34"));
        f.Dialer.AddName("mapped.example.com", IPAddress.Parse("::ffff:127.0.0.1"));
        f.Dialer.AddName("ula.example.com", IPAddress.Parse("fd00::1"));
        asks.Resolve = f.Dialer.ResolveAsync;
        await using var gateway = f.StartRun(6);
        foreach (var name in new[] { "mixed.example.com", "mapped.example.com", "ula.example.com", "unknown.example.com" })
            await RefusedAsync(gateway, $"CONNECT {name}:443 HTTP/1.1\r\nHost: {name}:443\r\n\r\n");
        Assert.Empty(f.Dialer.Resolved);
        long Id(string h) => asks.Asks().Single(a => a.Host == h).Id;

        foreach (var name in new[] { "mapped.example.com", "ula.example.com" })
            await Assert.ThrowsAsync<ArgumentException>(() => asks.AnswerAsync(Id(name), NetworkAskChoices.Agent, null, Ct));
        await asks.AnswerAsync(Id("mixed.example.com"), NetworkAskChoices.Agent, null, Ct);
        await asks.AnswerAsync(Id("unknown.example.com"), NetworkAskChoices.Agent, null, Ct);
        Assert.Equal(["mapped.example.com", "ula.example.com"], asks.Asks().Select(a => a.Host).Order());
    }

    private static async Task<string> RefusedAsync(RunGateway gateway, string request)
    {
        var (status, stream, client) = await ProxyClient.RawAsync(gateway, request);
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 403", status);
            using var reader = new StreamReader(stream);
            var text = new char[4096];
            var read = await reader.ReadAsync(text, Ct);
            return new string(text, 0, read);
        }
    }
}
