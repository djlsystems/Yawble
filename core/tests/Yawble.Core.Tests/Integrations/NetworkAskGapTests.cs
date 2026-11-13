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
    /// FAULT (card 62): a member whose override narrows network.allowHosts (Settings: a member's own list, a subset of the
    /// global one) is still refused the host after "Allow everywhere", because the override filters the global list
    /// (PolicyMerger.Merge, StoreNetworkPolicy.EffectiveHosts); yet the ask closes and the Manager is told "<host> is now
    /// allowed for Priya; retry what was blocked". The retry is refused again and a new ask is raised.
    /// Expected: either the member is allowed (the host also goes in that member's loosen block, or the override), or
    /// the answer says so and the Manager is not told it is allowed for that member.
    /// </summary>
    [Fact(Skip = "fault reported on card 62: Allow everywhere does not reach a member whose override narrows network.allowHosts")]
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
