using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// Domain allows and grouped asks (B018, card 184): "All of *.digicert.com" at each scope closes only the waiting asks it
/// covers at that scope; one member's asks with one registrable domain are answered together; the per-member cap still
/// counts hosts. Fakes only: a temporary database, fake timers and a recording tell.
/// </summary>
public sealed class NetworkAskDomainTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly FakeTimers _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly PolicyStore _policies;
    private readonly List<(string Team, string Member, string Text)> _told = [];
    private readonly NetworkAskService _asks;
    private readonly long _alpha, _beta, _priya, _bo, _wren;

    public NetworkAskDomainTests()
    {
        _policies = new PolicyStore(_db.ConnectionString);
        var layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
        _asks = new NetworkAskService(_db.ConnectionString,
            new StoreNetworkPolicy(_policies, (_, _) => Task.FromResult(layout), _ => Task.FromResult<RunLayout?>(layout)),
            (team, member, text, _, _) =>
            {
                lock (_told) _told.Add((team, member, text));
                return Task.CompletedTask;
            },
            _time.Clock) { NoticeDelay = _time.Delay };
        _alpha = Team("Alpha");
        _beta = Team("Beta");
        Member(_alpha, "Manager", "manager");
        _priya = Member(_alpha, "Priya", "member");
        _bo = Member(_alpha, "Bo", "member");
        Member(_beta, "Manager", "manager");
        _wren = Member(_beta, "Wren", "member");
    }

    public void Dispose() => _db.Dispose();

    private long Team(string name) => Insert("INSERT INTO teams (name, created_at) VALUES ($n, '2026-11-13T00:00:00Z') RETURNING id;", ("$n", name));

    private long Member(long team, string name, string role) =>
        Insert("INSERT INTO members (team_id, name, role, created_at) VALUES ($t, $n, $r, '2026-11-13T00:00:00Z') RETURNING id;", ("$t", team), ("$n", name), ("$r", role));

    private long Insert(string sql, params (string, object)[] args)
    {
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return (long)cmd.ExecuteScalar()!;
    }

    private long Ask(string member, string host)
    {
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused(member, host, "CONNECT", null));
        return Id(member, host);
    }

    private long Id(string member, string host) => _asks.Asks().Single(a => a.Member == member && a.Host == host).Id;

    private List<string> Waiting() => _asks.Asks().Select(a => $"{a.Member} {a.Host}").ToList();

    private IReadOnlyList<string> Loosen(long member) => _policies.GetMember(member).Loosen.Network?.AllowHosts ?? [];

    /// <summary>Priya: two digicert hosts and nuget; Bo: one digicert host; Beta/Wren: one digicert host.</summary>
    private long Spread()
    {
        var id = Ask("Alpha/Priya", "crl3.digicert.com");
        Ask("Alpha/Priya", "ocsp.digicert.com");
        Ask("Alpha/Priya", "api.nuget.org");
        Ask("Alpha/Bo", "crl3.digicert.com");
        Ask("Beta/Wren", "crl4.digicert.com");
        return id;
    }

    // ------------------------------------------------------------------ what an ask offers

    [Theory]
    [InlineData("crl3.digicert.com", "digicert.com", "*.digicert.com")]
    [InlineData("api.nuget.org", "nuget.org", "*.nuget.org")]
    [InlineData("digicert.com", "digicert.com", null)] // the apex: "*.digicert.com" never covers it
    [InlineData("someone.github.io", "someone.github.io", null)] // a tenant of a shared (PRIVATE) suffix
    [InlineData("bucket.s3.amazonaws.com", "bucket.s3.amazonaws.com", null)]
    public void TheAsk_SaysItsDomain_AndOffersTheDomainAllow_OnlyWhenOneMayBe(string host, string? domain, string? pattern)
    {
        Ask("Alpha/Priya", host);
        var json = NetworkAskService.ToJson(_asks.Asks().Single());
        Assert.Equal(domain, json["domain"]?.GetValue<string>());
        Assert.Equal(pattern, json["domainPattern"]?.GetValue<string>());
    }

    // ------------------------------------------------------------------ a domain allow at each scope

    [Fact]
    public async Task ADomainAllowForTheAgent_ClosesOnlyThatMembersCoveredAsks()
    {
        var id = Spread();
        var r = await _asks.AnswerAsync(id, NetworkAskChoices.Agent, null, Ct, domain: true);

        Assert.Equal("*.digicert.com", r["allowed"]!.GetValue<string>());
        Assert.Equal(["Alpha/Priya api.nuget.org", "Alpha/Bo crl3.digicert.com", "Beta/Wren crl4.digicert.com"], Waiting());
        Assert.Equal(["*.digicert.com"], Loosen(_priya));
        Assert.Empty(Loosen(_bo));
        Assert.Empty(_policies.TeamHosts(_alpha));
        Assert.Empty(_policies.GetGlobal().Network.AllowHosts);

        _time.Advance(TimeSpan.FromSeconds(5));
        var told = Assert.Single(_told);
        Assert.Equal(("Alpha", "Manager"), (told.Team, told.Member));
        Assert.StartsWith("*.digicert.com is now allowed for Priya; retry what was blocked.", told.Text);
    }

    [Fact]
    public async Task ADomainAllowForTheTeam_ClosesTheTeamsCoveredAsks_NotAnotherTeams()
    {
        var id = Spread();
        await _asks.AnswerAsync(id, NetworkAskChoices.Team, null, Ct, domain: true);

        Assert.Equal(["Alpha/Priya api.nuget.org", "Beta/Wren crl4.digicert.com"], Waiting());
        Assert.Equal(["*.digicert.com"], _policies.TeamHosts(_alpha).Select(h => h.Host));
        Assert.Empty(_policies.TeamHosts(_beta));
        Assert.Empty(Loosen(_priya)); // a team answer never writes a loosen block
        Assert.Empty(_policies.GetGlobal().Network.AllowHosts);

        _time.Advance(TimeSpan.FromSeconds(5));
        var told = Assert.Single(_told);
        Assert.Equal("*.digicert.com is now allowed for Priya and Bo; retry what was blocked. The person allowed it for every member of Alpha. " +
                     "It applies to runs that start from now on.", told.Text);
    }

    [Fact]
    public async Task ADomainAllowEverywhere_ClosesEveryTeamsCoveredAsks_OnlyThose()
    {
        var id = Spread();
        await _asks.AnswerAsync(id, NetworkAskChoices.Everywhere, null, Ct, domain: true);

        Assert.Equal(["Alpha/Priya api.nuget.org"], Waiting());
        Assert.Equal(["*.digicert.com"], _policies.GetGlobal().Network.AllowHosts);

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, _told.Count);
        Assert.Contains(_told, t => t is { Team: "Alpha" } && t.Text.StartsWith("*.digicert.com is now allowed for Priya and Bo;", StringComparison.Ordinal));
        Assert.Contains(_told, t => t is { Team: "Beta" } && t.Text.StartsWith("*.digicert.com is now allowed for Wren;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHostAllow_StillClosesOnlyThatHost()
    {
        var id = Spread();
        await _asks.AnswerAsync(id, NetworkAskChoices.Team, null, Ct);
        Assert.Equal(["Alpha/Priya ocsp.digicert.com", "Alpha/Priya api.nuget.org", "Beta/Wren crl4.digicert.com"], Waiting());
        Assert.Equal(["crl3.digicert.com"], _policies.TeamHosts(_alpha).Select(h => h.Host));
    }

    [Fact]
    public async Task AfterADomainAllow_ANewSubdomain_IsAllowedFromTheNextRun_NotAsked()
    {
        await _asks.AnswerAsync(Ask("Alpha/Priya", "crl3.digicert.com"), NetworkAskChoices.Agent, null, Ct, domain: true);
        Assert.Equal(HostAskOutcome.AllowedNextRun, _asks.Refused("Alpha/Priya", "cacerts.digicert.com", "CONNECT", null));
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Priya", "digicert.com", "CONNECT", null)); // not the apex
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Bo", "cacerts.digicert.com", "CONNECT", null));
    }

    [Theory]
    [InlineData("someone.github.io")]
    [InlineData("bucket.s3.amazonaws.com")]
    public async Task ADomainAllow_IsRefused_WhenNoneMayBeOffered_AndTheAskWaits(string host)
    {
        var id = Ask("Alpha/Priya", host);
        var e = await Assert.ThrowsAsync<RpcException>(() => _asks.DispatchAsync("network.answer",
            new JsonObject { ["id"] = id, ["choice"] = NetworkAskChoices.Everywhere, ["domain"] = true }, Ct));
        Assert.Contains("cannot be allowed as a whole domain", e.Message);
        Assert.Single(_asks.Asks());
        Assert.Empty(_policies.GetGlobal().Network.AllowHosts);
    }

    // ------------------------------------------------------------------ grouped asks

    [Fact]
    public async Task AllowOnAGroupedItem_AnswersEveryListedHost_InOneNotice()
    {
        var ids = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "ocsp.digicert.com"), Ask("Alpha/Priya", "crl4.digicert.com") };
        Ask("Alpha/Priya", "api.nuget.org");

        var r = await _asks.DispatchAsync("network.answer",
            new JsonObject { ["id"] = ids[0], ["ids"] = new JsonArray(ids.Select(i => (JsonNode?)i).ToArray()), ["choice"] = NetworkAskChoices.Agent }, Ct);

        Assert.Equal(ids, r!["closed"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Equal(["Alpha/Priya api.nuget.org"], Waiting());
        Assert.Equal(["crl3.digicert.com", "ocsp.digicert.com", "crl4.digicert.com"], Loosen(_priya));
        Assert.Empty(_told);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("crl3.digicert.com, ocsp.digicert.com and crl4.digicert.com are now allowed for Priya; retry what was blocked. " +
                     "They apply to runs that start from now on.", Assert.Single(_told).Text);
    }

    [Fact]
    public async Task ADomainAllowOnAGroupWithTheApex_AllowsTheApexAsItsHost()
    {
        var ids = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "digicert.com"), Ask("Alpha/Priya", "ocsp.digicert.com") };

        var r = await _asks.AnswerGroupAsync(ids[0], ids, NetworkAskChoices.Agent, null, domain: true, Ct);

        Assert.Empty(Waiting());
        Assert.Equal(["*.digicert.com", "digicert.com"], Loosen(_priya));
        Assert.Equal("*.digicert.com, digicert.com", r["allowed"]!.GetValue<string>());
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.StartsWith("*.digicert.com and digicert.com are now allowed for Priya;", Assert.Single(_told).Text);
    }

    [Fact]
    public async Task DenyOnAGroupedItem_DeniesEveryListedHost()
    {
        var ids = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "ocsp.digicert.com") };
        await _asks.AnswerGroupAsync(ids[0], ids, NetworkAskChoices.Deny, "use the offline list", domain: false, Ct);
        Assert.Empty(Waiting());
        Assert.All(_asks.Asks(pendingOnly: false), a => Assert.Equal("denied", a.State));
        Assert.Equal(HostAskOutcome.Denied, _asks.Refused("Alpha/Priya", "ocsp.digicert.com", "CONNECT", null));
    }

    [Theory]
    [InlineData("Alpha/Bo", "ocsp.digicert.com")] // another member
    [InlineData("Alpha/Priya", "api.nuget.org")] // another domain
    public async Task AGroup_IsOneMembersAsksForOneDomain_AnythingElseIsRefused(string member, string host)
    {
        var first = Ask("Alpha/Priya", "crl3.digicert.com");
        var other = Ask(member, host);
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.AnswerGroupAsync(first, [first, other], NetworkAskChoices.Agent, null, false, Ct));
        Assert.Equal(2, _asks.Asks().Count);
        Assert.Empty(Loosen(_priya));
    }

    [Fact]
    public void TheCap_StillCountsHosts_WhenTheyShareADomain()
    {
        for (var i = 1; i <= NetworkAskService.MaxWaitingPerMember; i++) Ask("Alpha/Priya", $"crl{i}.digicert.com");

        Assert.Equal(HostAskOutcome.None, _asks.Refused("Alpha/Priya", "ocsp.digicert.com", "CONNECT", null));
        Assert.Equal(HostAskOutcome.Pending, _asks.Refused("Alpha/Priya", "crl1.digicert.com", "CONNECT", null)); // a repeat still counts up
        Assert.Equal(NetworkAskService.MaxWaitingPerMember, _asks.Asks().Count);
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Bo", "ocsp.digicert.com", "CONNECT", null)); // the cap is per member
    }

    // ------------------------------------------------------------------ the policy editors' check

    [Fact]
    public void CheckHosts_IsTheFloorsHostProblem()
    {
        var r = Yawble.Core.Ui.UiRpc.CheckHosts(new JsonObject { ["hosts"] = new JsonArray("*.digicert.com", "api.nuget.org", "*.com", "*.github.io", "a.*.b", "*.anthropic.com") });
        var problems = r.Select(n => (n!["host"]!.GetValue<string>(), n["problem"]?.GetValue<string>())).ToList();
        Assert.All(problems, p => Assert.Equal(FloorRules.HostProblem(p.Item1), p.Item2));
        Assert.Null(problems[0].Item2);
        Assert.Null(problems[1].Item2);
        Assert.All(problems.Skip(2), p => Assert.NotNull(p.Item2));
    }
}
