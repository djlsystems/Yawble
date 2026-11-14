using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// B012 end-to-end checks the developers' NetworkAskTests leave open (card 62): more addresses and private names that
/// never become asks (by the filter and through a run's gateway), a policy refusal that leaves the ask waiting and tells
/// no one, the window's push on an answer, and "Allow everywhere" for a member whose override narrows the host list.
/// Fakes only: a temporary database, a fake clock, a recording tell, a fake policy; no network and no system program.
/// </summary>
public sealed class NetworkAskGapTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly PolicyStore _policies;
    private readonly RunLayout _layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
    private readonly List<(string Team, string Member, string Text)> _told = [];
    private readonly long _alpha, _priya;

    public NetworkAskGapTests()
    {
        _policies = new PolicyStore(_db.ConnectionString);
        _alpha = Insert("INSERT INTO teams (name, created_at) VALUES ('Alpha', '2026-11-13T00:00:00Z') RETURNING id;");
        Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({_alpha}, 'Manager', 'manager', '2026-11-13T00:00:00Z') RETURNING id;");
        _priya = Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({_alpha}, 'Priya', 'member', '2026-11-13T00:00:00Z') RETURNING id;");
    }

    public void Dispose() => _db.Dispose();

    private long Insert(string sql)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private NetworkAskService Service(INetworkPolicy? policy = null) =>
        new(_db.ConnectionString, policy ?? new StoreNetworkPolicy(_policies, (_, _) => Task.FromResult(_layout), _ => Task.FromResult<RunLayout?>(_layout)),
            (team, member, text, _, _) =>
            {
                lock (_told) _told.Add((team, member, text));
                return Task.CompletedTask;
            },
            _clock);

    // ------------------------------------------------------------------ never asked: more addresses and private names

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("172.16.0.1")]
    [InlineData("2130706433")] // 127.0.0.1 as one number
    [InlineData("fe80::1")]
    [InlineData("[::ffff:10.0.0.1]")]
    [InlineData("LOCALHOST.")]
    [InlineData("box.internal")]
    [InlineData("files.corp")]
    [InlineData("nas.localdomain")]
    [InlineData("https://api.nuget.org/v3/index.json")] // a URL from the gateway is not a host name
    [InlineData("user@api.nuget.org")]
    public void MoreAddressesAndPrivateNames_NeverBecomeAsks(string host)
    {
        var asks = Service();
        Assert.NotNull(NetworkAskFilter.Problem(host));
        Assert.Equal(HostAskOutcome.None, asks.Refused("Alpha/Priya", host, "CONNECT", null));
        Assert.Empty(asks.Asks(pendingOnly: false));
    }

    [Fact]
    public async Task TheGateway_LoopbackPrivateAndAppHosts_StayPlainRefusals()
    {
        await using var f = await GatewayFixture.StartAsync();
        var team = InsertIn(f.Db, "INSERT INTO teams (name, created_at) VALUES ('Alpha', '2026-11-13T00:00:00Z') RETURNING id;");
        InsertIn(f.Db, $"INSERT INTO members (team_id, name, role, created_at) VALUES ({team}, 'm5', 'member', '2026-11-13T00:00:00Z') RETURNING id;");
        var policies = new PolicyStore(f.Db.ConnectionString);
        var asks = new NetworkAskService(f.Db.ConnectionString,
            new StoreNetworkPolicy(policies, (_, _) => Task.FromResult(_layout), _ => Task.FromResult<RunLayout?>(_layout)), (_, _, _, _, _) => Task.CompletedTask, f.Clock);
        f.Host.Refusals = asks;
        await using var gateway = f.StartRun(5);

        foreach (var target in new[] { "localhost:443", "api.localhost:443", "10.0.0.5:443", "[::1]:443", "printer.local:443", "nas.lan:443", "cloud.yawble.ai:443" })
        {
            var (status, stream, client) = await ProxyClient.RawAsync(gateway, $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n");
            using (client)
            {
                Assert.StartsWith("HTTP/1.1 403", status);
                using var reader = new StreamReader(stream);
                Assert.DoesNotContain("request_host", await reader.ReadToEndAsync(Ct));
            }
        }
        Assert.Empty(asks.Asks(pendingOnly: false));
    }

    // ------------------------------------------------------------------ a policy refusal leaves the ask waiting

    private sealed class RefusingPolicy : INetworkPolicy
    {
        public bool AllowedFor(long memberId, string host) => false;
        public Task AllowForMemberAsync(long memberId, string host, CancellationToken ct) => throw new PolicyException(["the sandbox dry run refused it"]);
        public Task AllowEverywhereAsync(string host, CancellationToken ct) => throw new PolicyException(["the sandbox dry run refused it"]);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("team")]
    [InlineData("everywhere")]
    public async Task APolicyStoreRefusal_LeavesTheAskWaiting_AndTellsNoOne(string choice)
    {
        var asks = Service(new RefusingPolicy());
        var changed = 0;
        asks.Changed = () => changed++;
        asks.Refused("Alpha/Priya", "api.nuget.org", "CONNECT", null);
        var id = Assert.Single(asks.Asks()).Id;
        changed = 0;

        var e = await Assert.ThrowsAsync<RpcException>(() => asks.DispatchAsync("network.answer", new JsonObject { ["id"] = id, ["choice"] = choice }, Ct));

        Assert.Equal("policy_refused", e.Code);
        Assert.Contains("dry run refused", e.Message);
        Assert.Equal("pending", Assert.Single(asks.Asks()).State);
        Assert.Empty(_told);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task AnAnswer_PushesTheChange_SoNeedsYouRefreshes()
    {
        var asks = Service();
        var changed = 0;
        asks.Changed = () => changed++;
        asks.Refused("Alpha/Priya", "api.nuget.org", "CONNECT", null);
        Assert.Equal(1, changed);
        await asks.AnswerAsync(Assert.Single(asks.Asks()).Id, NetworkAskChoices.Deny, null, Ct);
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task Deny_ThenTheGatewaysNextRefusal_IsDenied_NotANewAsk_AndTheAgentIsToldSo()
    {
        var asks = Service();
        asks.Refused("Alpha/Priya", "example.com", "CONNECT", null);
        await asks.AnswerAsync(Assert.Single(asks.Asks()).Id, NetworkAskChoices.Deny, null, Ct);
        var outcome = asks.Refused("Alpha/Priya", "example.com", "CONNECT", null);
        Assert.Equal(HostAskOutcome.Denied, outcome);
        Assert.Contains("denied example.com", RunGateway.AllowlistText("example.com", outcome));
        Assert.DoesNotContain("request_host", RunGateway.AllowlistText("example.com", outcome));
    }

    // ------------------------------------------------------------------ "Allow everywhere" and a narrowing override

    /// <summary>
    /// Was a fault (card 62, fixed on card 60): a member whose override narrows network.allowHosts (Settings: a member's own list, a subset of the
    /// global one) is still refused the host after "Allow everywhere", because the override filters the global list
    /// (PolicyMerger.Merge, StoreNetworkPolicy.EffectiveHosts); yet the ask closes and the Manager is told "<host> is now
    /// allowed for Priya; retry what was blocked". The retry is refused again and a new ask is raised.
    /// Expected: either the member is allowed (the host also goes in that member's loosen block, or the override), or
    /// the answer says so and the Manager is not told it is allowed for that member.
    /// </summary>
    [Fact]
    public async Task AllowEverywhere_ForAMemberWhoseOverrideNarrowsTheHosts_ActuallyAllowsIt()
    {
        _policies.SaveGlobal(_policies.GetGlobal() with { Network = new NetworkPolicy(["github.com"]) }, _layout);
        _policies.SaveOverride(_priya, new PolicyPatch(Network: new NetworkPatch(["github.com"])), _layout);
        var asks = Service();
        asks.Refused("Alpha/Priya", "pypi.org", "CONNECT", null);

        await asks.AnswerAsync(Assert.Single(asks.Asks()).Id, NetworkAskChoices.Everywhere, null, Ct);

        var allowed = StoreNetworkPolicy.EffectiveHosts(_policies.GetGlobal(), _policies.GetMember(_priya)).Contains("pypi.org");
        var toldAllowed = _told.Any(t => t.Text.StartsWith("pypi.org is now allowed for Priya", StringComparison.Ordinal));
        Assert.True(allowed || !toldAllowed, "the Manager was told pypi.org is allowed for Priya, but Priya's override still filters it out");
        Assert.True(allowed);
        Assert.True(toldAllowed);
        Assert.Contains("pypi.org", _policies.GetGlobal().Network.AllowHosts);
        Assert.Contains("pypi.org", _policies.GetMember(_priya).Loosen.Network?.AllowHosts ?? []);
        Assert.Equal(HostAskOutcome.AllowedNextRun, asks.Refused("Alpha/Priya", "pypi.org", "CONNECT", null));
    }

    [Fact]
    public async Task AllowEverywhere_AlsoReachesAnotherTeamsWaitingMember_WhoseOverrideNarrowsTheHosts()
    {
        var beta = Insert("INSERT INTO teams (name, created_at) VALUES ('Beta', '2026-11-13T00:00:00Z') RETURNING id;");
        Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({beta}, 'Manager', 'manager', '2026-11-13T00:00:00Z') RETURNING id;");
        var omar = Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({beta}, 'Omar', 'member', '2026-11-13T00:00:00Z') RETURNING id;");
        _policies.SaveGlobal(_policies.GetGlobal() with { Network = new NetworkPolicy(["github.com"]) }, _layout);
        _policies.SaveOverride(omar, new PolicyPatch(Network: new NetworkPatch(["github.com"])), _layout);
        var asks = Service();
        asks.Refused("Alpha/Priya", "pypi.org", "CONNECT", null);
        asks.Refused("Beta/Omar", "pypi.org", "CONNECT", null);

        var result = await asks.AnswerAsync(asks.Asks().First(a => a.Member == "Alpha/Priya").Id, NetworkAskChoices.Everywhere, null, Ct);

        Assert.Equal(2, result["closed"]!.AsArray().Count);
        Assert.Contains("pypi.org", StoreNetworkPolicy.EffectiveHosts(_policies.GetGlobal(), _policies.GetMember(_priya)));
        Assert.Contains("pypi.org", StoreNetworkPolicy.EffectiveHosts(_policies.GetGlobal(), _policies.GetMember(omar)));
        Assert.Contains(_told, t => t.Team == "Beta" && t.Text.StartsWith("pypi.org is now allowed for Omar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllowForTheTeam_ReachesAMemberWhoseOwnOverrideNarrowsTheHosts()
    {
        var omar = Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({_alpha}, 'Omar', 'member', '2026-11-13T00:00:00Z') RETURNING id;");
        _policies.SaveGlobal(_policies.GetGlobal() with { Network = new NetworkPolicy(["github.com"]) }, _layout);
        _policies.SaveOverride(_priya, new PolicyPatch(Network: new NetworkPatch(["github.com"])), _layout);
        var asks = Service();
        asks.Refused("Alpha/Priya", "pypi.org", "CONNECT", null);

        await asks.AnswerAsync(Assert.Single(asks.Asks()).Id, NetworkAskChoices.Team, null, Ct);

        Assert.Contains("pypi.org", StoreNetworkPolicy.EffectiveHosts(_policies.GetGlobal(), _policies.GetMember(_priya)));
        Assert.Contains("pypi.org", StoreNetworkPolicy.EffectiveHosts(_policies.GetGlobal(), _policies.GetMember(omar)));
        Assert.DoesNotContain("pypi.org", _policies.GetGlobal().Network.AllowHosts);
        Assert.Contains(_told, t => t.Text.StartsWith("pypi.org is now allowed for Priya", StringComparison.Ordinal));
        Assert.Equal(HostAskOutcome.AllowedNextRun, asks.Refused("Alpha/Priya", "pypi.org", "CONNECT", null));
    }

    /// <summary>A policy that saves without error but whose effective hosts still lack the host.</summary>
    private sealed class SilentlyIneffectivePolicy : INetworkPolicy
    {
        public bool AllowedFor(long memberId, string host) => false;
        public Task AllowForMemberAsync(long memberId, string host, CancellationToken ct) => Task.CompletedTask;
        public Task AllowEverywhereAsync(string host, CancellationToken ct) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("team")]
    [InlineData("everywhere")]
    public async Task AnAllowThatDoesNotReachTheMember_LeavesTheAskWaiting_AndTellsNoOne(string choice)
    {
        var asks = Service(new SilentlyIneffectivePolicy());
        asks.Refused("Alpha/Priya", "pypi.org", "CONNECT", null);
        var id = Assert.Single(asks.Asks()).Id;

        var e = await Assert.ThrowsAsync<RpcException>(() => asks.DispatchAsync("network.answer", new JsonObject { ["id"] = id, ["choice"] = choice }, Ct));

        Assert.Equal("policy_refused", e.Code);
        Assert.Contains("still not allowed for Alpha/Priya", e.Message);
        Assert.Equal("pending", Assert.Single(asks.Asks()).State);
        Assert.Empty(_told);
    }

    // ------------------------------------------------------------------ re-check of the fix (card 62): partial failure and a late ask

    /// <summary>The real store policy, with a hook before each member write (it may refuse or do something meanwhile).</summary>
    private sealed class HookedPolicy(INetworkPolicy inner, Func<long, Task> beforeMember) : INetworkPolicy
    {
        public bool AllowedFor(long memberId, string host) => inner.AllowedFor(memberId, host);
        public async Task AllowForMemberAsync(long memberId, string host, CancellationToken ct)
        {
            await beforeMember(memberId);
            await inner.AllowForMemberAsync(memberId, host, ct);
        }
        public Task AllowEverywhereAsync(string host, CancellationToken ct) => inner.AllowEverywhereAsync(host, ct);
    }

    private StoreNetworkPolicy Store() => new(_policies, (_, _) => Task.FromResult(_layout), _ => Task.FromResult<RunLayout?>(_layout));

    [Fact]
    public async Task AllowEverywhere_GlobalSavedButAnotherMembersLoosenRefused_StaysWaitingTellsNoOne_AndARetryCompletes()
    {
        var beta = Insert("INSERT INTO teams (name, created_at) VALUES ('Beta', '2026-11-13T00:00:00Z') RETURNING id;");
        Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({beta}, 'Manager', 'manager', '2026-11-13T00:00:00Z') RETURNING id;");
        var omar = Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({beta}, 'Omar', 'member', '2026-11-13T00:00:00Z') RETURNING id;");
        _policies.SaveGlobal(_policies.GetGlobal() with { Network = new NetworkPolicy(["github.com"]) }, _layout);
        _policies.SaveOverride(omar, new PolicyPatch(Network: new NetworkPatch(["github.com"])), _layout);
        var refuse = true;
        var asks = Service(new HookedPolicy(Store(), m => refuse && m == omar ? throw new PolicyException(["the sandbox dry run refused it"]) : Task.CompletedTask));
        asks.Refused("Alpha/Priya", "pypi.org", "CONNECT", null);
        asks.Refused("Beta/Omar", "pypi.org", "CONNECT", null);
        var id = asks.Asks().First(a => a.Member == "Alpha/Priya").Id;

        var e = await Assert.ThrowsAsync<RpcException>(() => asks.DispatchAsync("network.answer", new JsonObject { ["id"] = id, ["choice"] = "everywhere" }, Ct));

        Assert.Equal("policy_refused", e.Code);
        Assert.All(asks.Asks(pendingOnly: false), a => Assert.Equal("pending", a.State));
        Assert.Equal(2, asks.Asks().Count);
        Assert.Empty(_told);
        // the global write stays (not rolled back); Omar still lacks it, so he is not told anything
        Assert.Contains("pypi.org", _policies.GetGlobal().Network.AllowHosts);
        Assert.DoesNotContain("pypi.org", StoreNetworkPolicy.EffectiveHosts(_policies.GetGlobal(), _policies.GetMember(omar)));

        // the retry is safe: no duplicate host anywhere, both close, both Managers told once
        refuse = false;
        var result = await asks.AnswerAsync(id, NetworkAskChoices.Everywhere, null, Ct);
        Assert.Equal(2, result["closed"]!.AsArray().Count);
        Assert.Single(_policies.GetGlobal().Network.AllowHosts, h => h == "pypi.org");
        Assert.Single(_policies.GetMember(omar).Loosen.Network?.AllowHosts ?? [], h => h == "pypi.org");
        Assert.Contains("pypi.org", StoreNetworkPolicy.EffectiveHosts(_policies.GetGlobal(), _policies.GetMember(omar)));
        Assert.Single(_told, t => t.Team == "Alpha" && t.Text.StartsWith("pypi.org is now allowed for Priya", StringComparison.Ordinal));
        Assert.Single(_told, t => t.Team == "Beta" && t.Text.StartsWith("pypi.org is now allowed for Omar", StringComparison.Ordinal));
    }

    /// <summary>
    /// An ask for the same host that arrives while the answer is writing the policy (after the answer listed the members it
    /// reaches) is closed by the answer's "every pending ask for the host" update and its Manager told it is allowed,
    /// though that member's override still filters the host out. Expected: the close covers only the asks whose members
    /// the answer checked (or re-checks every closed ask's member), so a late ask stays waiting or is reached too.
    /// </summary>
    [Fact(Skip = "Fault (card 62 re-check): AnswerAsync re-lists the covered asks after the policy writes, so an ask raised meanwhile closes and its Manager is told allowed without the member being reached")]
    public async Task AllowEverywhere_AnAskArrivingDuringTheWrite_IsNotToldAllowed_UnlessItsMemberHasTheHost()
    {
        var beta = Insert("INSERT INTO teams (name, created_at) VALUES ('Beta', '2026-11-13T00:00:00Z') RETURNING id;");
        Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({beta}, 'Manager', 'manager', '2026-11-13T00:00:00Z') RETURNING id;");
        var omar = Insert($"INSERT INTO members (team_id, name, role, created_at) VALUES ({beta}, 'Omar', 'member', '2026-11-13T00:00:00Z') RETURNING id;");
        _policies.SaveGlobal(_policies.GetGlobal() with { Network = new NetworkPolicy(["github.com"]) }, _layout);
        _policies.SaveOverride(_priya, new PolicyPatch(Network: new NetworkPatch(["github.com"])), _layout);
        _policies.SaveOverride(omar, new PolicyPatch(Network: new NetworkPatch(["github.com"])), _layout);
        NetworkAskService? asks = null;
        asks = Service(new HookedPolicy(Store(), m =>
        {
            // Omar's run is refused pypi.org while Priya's loosen block is being saved
            if (m == _priya) asks!.Refused("Beta/Omar", "pypi.org", "CONNECT", null);
            return Task.CompletedTask;
        }));
        asks.Refused("Alpha/Priya", "pypi.org", "CONNECT", null);

        await asks.AnswerAsync(Assert.Single(asks.Asks()).Id, NetworkAskChoices.Everywhere, null, Ct);

        var omarHas = StoreNetworkPolicy.EffectiveHosts(_policies.GetGlobal(), _policies.GetMember(omar)).Contains("pypi.org");
        var omarTold = _told.Any(t => t.Team == "Beta" && t.Text.StartsWith("pypi.org is now allowed for Omar", StringComparison.Ordinal));
        Assert.True(omarHas || !omarTold, "Beta's Manager was told pypi.org is allowed for Omar, but Omar's override still filters it out");
    }

    private static long InsertIn(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
