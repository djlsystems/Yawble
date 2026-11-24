using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// B018 verify (card 185, TesterWren): gaps found while checking DeveloperMarlo's and DeveloperInes's delivery. Fakes
/// only: a temporary database, fake timers and a recording tell; no real network, no real program.
/// </summary>
public sealed class HostAskGroupingGapTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly FakeTimers _time = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly PolicyStore _policies;
    private readonly StoreNetworkPolicy _policy;
    private readonly List<(string Team, string Member, string Text)> _told = [];
    private readonly NetworkAskService _asks;
    private readonly long _alpha, _priya, _bo;

    public HostAskGroupingGapTests()
    {
        _policies = new PolicyStore(_db.ConnectionString);
        var layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
        _policy = new StoreNetworkPolicy(_policies, (_, _) => Task.FromResult(layout), _ => Task.FromResult<RunLayout?>(layout));
        _asks = new NetworkAskService(_db.ConnectionString, _policy,
            (team, member, text, _, _) =>
            {
                lock (_told) _told.Add((team, member, text));
                return Task.CompletedTask;
            },
            _time.Clock) { NoticeDelay = _time.Delay };
        _alpha = Team("Alpha");
        Member(_alpha, "Manager", "manager");
        _priya = Member(_alpha, "Priya", "member");
        _bo = Member(_alpha, "Bo", "member");
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
        return _asks.Asks().Single(a => a.Member == member && a.Host == host).Id;
    }

    private List<string> Waiting() => _asks.Asks().Select(a => $"{a.Member} {a.Host}").ToList();

    private IReadOnlyList<string> Loosen(long member) => _policies.GetMember(member).Loosen.Network?.AllowHosts ?? [];

    private bool NothingWritten() =>
        Loosen(_priya).Count == 0 && Loosen(_bo).Count == 0 && _policies.TeamHosts(_alpha).Count == 0 && _policies.GetGlobal().Network.AllowHosts.Count == 0;

    // ------------------------------------------------------------------ apex vs subdomain

    [Fact]
    public async Task ADomainAllow_LeavesTheApexAndALookalike_Waiting_AndNotAllowed()
    {
        var crl3 = Ask("Alpha/Priya", "crl3.digicert.com");
        Ask("Alpha/Priya", "digicert.com");
        Ask("Alpha/Priya", "evil-digicert.com");

        // One ask answered alone (not as a group): the apex is not covered by *.digicert.com.
        var r = await _asks.AnswerAsync(crl3, NetworkAskChoices.Agent, null, Ct, domain: true);

        Assert.Equal("*.digicert.com", r["allowed"]!.GetValue<string>());
        Assert.Equal([crl3], r["closed"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Equal(["Alpha/Priya digicert.com", "Alpha/Priya evil-digicert.com"], Waiting());
        Assert.True(_policy.AllowedFor(_priya, "cacerts.digicert.com"));
        Assert.True(_policy.AllowedFor(_priya, "a.b.digicert.com"));
        Assert.False(_policy.AllowedFor(_priya, "digicert.com"));
        Assert.False(_policy.AllowedFor(_priya, "evil-digicert.com"));
        Assert.False(_policy.AllowedFor(_bo, "cacerts.digicert.com"));
    }

    [Fact]
    public async Task AGroupedTeamDomainAllow_WithTheApex_WritesTheApexAsItsOwnHost_AndClosesTheTeamsCoveredAsks()
    {
        var ids = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "digicert.com") };
        Ask("Alpha/Bo", "ocsp.digicert.com");
        Ask("Alpha/Bo", "digicert.com");
        Ask("Alpha/Bo", "evil-digicert.com");

        await _asks.AnswerGroupAsync(ids[0], ids, NetworkAskChoices.Team, null, domain: true, Ct);

        // The pattern never stands in for the apex: the apex is its own entry, and the lookalike stays waiting.
        Assert.Equal(["*.digicert.com", "digicert.com"], _policies.TeamHosts(_alpha).Select(h => h.Host).Order());
        Assert.Equal(["Alpha/Bo evil-digicert.com"], Waiting());
        Assert.False(_policy.AllowedFor(_bo, "evil-digicert.com"));
        Assert.Empty(Loosen(_priya));
    }

    [Fact]
    public async Task AGroupOfTheApexAlone_WithDomainTrue_AllowsOnlyTheApex()
    {
        var apex = Ask("Alpha/Priya", "digicert.com");
        Ask("Alpha/Priya", "crl3.digicert.com");
        // The apex offers no domain allow (domainPattern null); a domain:true answer for it writes the host alone.
        Assert.Null(NetworkAskService.DomainPattern("digicert.com"));
        var r = await _asks.AnswerAsync(apex, NetworkAskChoices.Agent, null, Ct, domain: true);
        Assert.Equal("digicert.com", r["allowed"]!.GetValue<string>());
        Assert.Equal(["digicert.com"], Loosen(_priya));
        Assert.Equal(["Alpha/Priya crl3.digicert.com"], Waiting());
    }

    // ------------------------------------------------------------------ a refused mixed group

    [Fact]
    public async Task AMixedGroup_SeveralMembersAndDomains_IsRefusedBeforeAnyWrite_AndTellsNoOne()
    {
        var first = Ask("Alpha/Priya", "crl3.digicert.com");
        var ok = Ask("Alpha/Priya", "ocsp.digicert.com");
        var bo = Ask("Alpha/Bo", "crl4.digicert.com");
        var nuget = Ask("Alpha/Priya", "api.nuget.org");

        // The mismatches are listed last: the whole group is checked before the first answer writes anything.
        var e = await Assert.ThrowsAsync<RpcException>(() => _asks.DispatchAsync("network.answer", new JsonObject
        {
            ["id"] = first, ["ids"] = new JsonArray(first, ok, bo, nuget), ["choice"] = NetworkAskChoices.Everywhere, ["domain"] = true,
        }, Ct));

        Assert.Equal("bad_params", e.Code);
        Assert.Equal(4, _asks.Asks().Count);
        Assert.True(NothingWritten());
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(_told);
    }

    [Fact]
    public async Task AMixedGroup_IsRefused_ForDenyToo()
    {
        var first = Ask("Alpha/Priya", "crl3.digicert.com");
        var bo = Ask("Alpha/Bo", "crl3.digicert.com");
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.AnswerGroupAsync(first, [first, bo], NetworkAskChoices.Deny, null, false, Ct));
        Assert.Equal(2, _asks.Asks().Count);
        Assert.Empty(_told);
    }

    // ------------------------------------------------------------------ Deny with domain

    [Fact]
    public async Task DenyWithDomainTrue_IsIgnoredAsADomain_WritesNothing_AndSaysNoAllowed()
    {
        var crl3 = Ask("Alpha/Priya", "crl3.digicert.com");
        Ask("Alpha/Priya", "ocsp.digicert.com");
        var shared = Ask("Alpha/Priya", "someone.github.io"); // no domain allow may be offered: still no error for a Deny

        var r = await _asks.DispatchAsync("network.answer", new JsonObject { ["id"] = crl3, ["choice"] = NetworkAskChoices.Deny, ["domain"] = true }, Ct);
        var r2 = await _asks.DispatchAsync("network.answer", new JsonObject { ["id"] = shared, ["choice"] = NetworkAskChoices.Deny, ["domain"] = true }, Ct);

        Assert.Null(r!["allowed"]);
        Assert.Null(r2!["allowed"]);
        Assert.Equal([crl3], r["closed"]!.AsArray().Select(n => n!.GetValue<long>())); // a deny closes only its own ask
        Assert.Equal(["Alpha/Priya ocsp.digicert.com"], Waiting());
        Assert.True(NothingWritten());
    }

    // ------------------------------------------------------------------ the member forward

    [Fact]
    public async Task ThreeAllowsInTwoSeconds_TheOnlyWakeIsTheManagersOne_NoMemberIsToldByTheApp()
    {
        foreach (var host in new[] { "crl3.digicert.com", "ocsp.digicert.com", "crl4.digicert.com" })
        {
            await _asks.AnswerAsync(Ask("Alpha/Priya", host), NetworkAskChoices.Agent, null, Ct);
            _time.Advance(TimeSpan.FromMilliseconds(900));
        }
        _time.Advance(TimeSpan.FromSeconds(5));

        // The core tells only the Manager; the member forward is the Manager's own tell (manager.md), from this one message.
        var told = Assert.Single(_told);
        Assert.Equal(("Alpha", "Manager"), (told.Team, told.Member));
        Assert.Contains("crl3.digicert.com, ocsp.digicert.com and crl4.digicert.com are now allowed for Priya", told.Text);
    }

    [Fact]
    public void TheManagersRoleText_SaysToForwardAMergedNotice_OncePerMember()
    {
        using var s = typeof(NetworkAskService).Assembly.GetManifestResourceStream("Yawble.Core.Roles.manager.md");
        Assert.NotNull(s);
        var text = new StreamReader(s).ReadToEnd().ReplaceLineEndings(" ");
        Assert.Contains("tell each member named once, listing all of its hosts, not once per host", text);
    }

    /// <summary>
    /// B018 finding F1, fixed by B039 (card 227): Deny on a grouped item of three hosts tells the member once, and that
    /// one message names every host. Each host is still denied with its own 24-hour quiet, and nothing is written.
    /// </summary>
    [Fact]
    public async Task DenyOnAGroupOfThree_TellsTheMemberOnce_ListingEveryHost()
    {
        var ids = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "ocsp.digicert.com"), Ask("Alpha/Priya", "crl4.digicert.com") };
        var r = await _asks.AnswerGroupAsync(ids[0], ids, NetworkAskChoices.Deny, null, false, Ct);
        _time.Advance(TimeSpan.FromSeconds(10));

        var told = Assert.Single(_told);
        Assert.Equal(("Alpha", "Priya"), (told.Team, told.Member));
        Assert.Contains("denied network access to crl3.digicert.com, ocsp.digicert.com and crl4.digicert.com", told.Text);
        Assert.Contains("hand back blocked on crl3.digicert.com, ocsp.digicert.com and crl4.digicert.com", told.Text);
        Assert.Equal(["Alpha/Priya"], r["told"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(ids.Order(), r["closed"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Null(r["allowed"]);
        Assert.Empty(_asks.Asks());
        Assert.True(NothingWritten());
        // Every host is quiet for 24 hours: asking again is refused at once, told nothing more.
        foreach (var host in new[] { "crl3.digicert.com", "ocsp.digicert.com", "crl4.digicert.com" })
            Assert.Equal(HostAskOutcome.Denied, _asks.Refused("Alpha/Priya", host, "CONNECT", null));
        Assert.Single(_told);
    }

    [Fact]
    public async Task TwoMembersDenyingTheirGroups_AreEachToldOnce_WithOnlyTheirOwnHosts()
    {
        var priya = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "ocsp.digicert.com") };
        var bo = new[] { Ask("Alpha/Bo", "crl4.digicert.com"), Ask("Alpha/Bo", "cacerts.digicert.com"), Ask("Alpha/Bo", "ocsp.digicert.com") };

        await _asks.AnswerGroupAsync(priya[0], priya, NetworkAskChoices.Deny, "not needed", false, Ct);
        await _asks.AnswerGroupAsync(bo[0], bo, NetworkAskChoices.Deny, null, false, Ct);
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(2, _told.Count);
        var p = Assert.Single(_told, t => t.Member == "Priya").Text;
        var b = Assert.Single(_told, t => t.Member == "Bo").Text;
        Assert.Contains($"denied network access to crl3.digicert.com and ocsp.digicert.com (network requests {priya[0]} and {priya[1]}): not needed.", p);
        Assert.DoesNotContain("crl4", p);
        Assert.Contains("denied network access to crl4.digicert.com, cacerts.digicert.com and ocsp.digicert.com", b);
        Assert.DoesNotContain("crl3", b);
        Assert.DoesNotContain("not needed", b);
        Assert.Empty(_asks.Asks());
        Assert.True(NothingWritten());
    }

    [Fact]
    public async Task ASingleDeny_StillTellsTheMemberOnce_AsBefore()
    {
        var id = Ask("Alpha/Priya", "api.nuget.org");
        await _asks.AnswerGroupAsync(id, [id], NetworkAskChoices.Deny, null, false, Ct);
        var told = Assert.Single(_told);
        Assert.Equal($"The person denied network access to api.nuget.org (network request {id}). It is not asked again for you for 24 hours. " +
                     "Do not try another site or a way around it: carry on without it, or hand back blocked on api.nuget.org.", told.Text);
    }

    // ------------------------------------------------------------------ B039 verify (card 228, TesterKofi): gaps

    [Fact]
    public async Task TwoMembersGroupedDeny_EachHostIsQuietOnlyForItsOwnMember()
    {
        var priya = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "ocsp.digicert.com") };
        var bo = new[] { Ask("Alpha/Bo", "crl4.digicert.com"), Ask("Alpha/Bo", "cacerts.digicert.com") };
        await _asks.AnswerGroupAsync(priya[0], priya, NetworkAskChoices.Deny, null, false, Ct);
        await _asks.AnswerGroupAsync(bo[0], bo, NetworkAskChoices.Deny, null, false, Ct);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, _told.Count);

        foreach (var host in new[] { "crl3.digicert.com", "ocsp.digicert.com" })
            Assert.Equal(HostAskOutcome.Denied, _asks.Refused("Alpha/Priya", host, "CONNECT", null));
        foreach (var host in new[] { "crl4.digicert.com", "cacerts.digicert.com" })
            Assert.Equal(HostAskOutcome.Denied, _asks.Refused("Alpha/Bo", host, "CONNECT", null));
        // The quiet belongs to the member: Bo was never denied crl3, so Bo asking for it is a new ask.
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Bo", "crl3.digicert.com", "CONNECT", null));
        Assert.True(NothingWritten());
        Assert.Equal(2, _told.Count);
    }

    [Fact]
    public async Task GroupedDeny_QuietEndsAfter24Hours_ForEveryHost()
    {
        var ids = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "ocsp.digicert.com"), Ask("Alpha/Priya", "crl4.digicert.com") };
        await _asks.AnswerGroupAsync(ids[0], ids, NetworkAskChoices.Deny, null, false, Ct);
        _time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(HostAskOutcome.Denied, _asks.Refused("Alpha/Priya", "ocsp.digicert.com", "CONNECT", null));
        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        foreach (var host in new[] { "crl3.digicert.com", "ocsp.digicert.com", "crl4.digicert.com" })
            Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Priya", host, "CONNECT", null));
    }

    [Fact]
    public async Task GroupedDeny_AnAskAnsweredMeanwhile_IsLeftOutOfTheOneNotice()
    {
        var ids = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "ocsp.digicert.com"), Ask("Alpha/Priya", "crl4.digicert.com") };
        await _asks.AnswerAsync(ids[1], NetworkAskChoices.Deny, null, Ct); // answered on its own first: told once for it
        Assert.Single(_told);

        var r = await _asks.AnswerGroupAsync(ids[0], ids, NetworkAskChoices.Deny, null, false, Ct);
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(2, _told.Count);
        var group = _told[1].Text;
        Assert.Contains($"denied network access to crl3.digicert.com and crl4.digicert.com (network requests {ids[0]} and {ids[2]})", group);
        Assert.DoesNotContain("ocsp", group);
        Assert.Equal([ids[0], ids[2]], r["closed"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Empty(_asks.Asks());
    }

    [Fact]
    public async Task GroupedDeny_ThroughTheRpc_TellsOnce_WithTheNote()
    {
        var ids = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "ocsp.digicert.com"), Ask("Alpha/Priya", "crl4.digicert.com") };
        var r = await _asks.DispatchAsync("network.answer", new JsonObject
        {
            ["id"] = ids[0], ["ids"] = new JsonArray(ids[0], ids[1], ids[2]), ["choice"] = NetworkAskChoices.Deny, ["note"] = "  use the cache  ",
        }, Ct);
        _time.Advance(TimeSpan.FromSeconds(10));

        var told = Assert.Single(_told);
        Assert.Equal("The person denied network access to crl3.digicert.com, ocsp.digicert.com and crl4.digicert.com " +
                     $"(network requests {ids[0]}, {ids[1]} and {ids[2]}): use the cache. They are not asked again for you for 24 hours. " +
                     "Do not try another site or a way around them: carry on without them, or hand back blocked on crl3.digicert.com, ocsp.digicert.com and crl4.digicert.com.",
                     told.Text);
        Assert.Null(r!["allowed"]);
        Assert.True(NothingWritten());
    }

    [Fact]
    public async Task GroupedAllow_IsUnchanged_OneManagerNotice_NoMemberTell()
    {
        var ids = new[] { Ask("Alpha/Priya", "crl3.digicert.com"), Ask("Alpha/Priya", "ocsp.digicert.com"), Ask("Alpha/Priya", "crl4.digicert.com") };
        var r = await _asks.AnswerGroupAsync(ids[0], ids, NetworkAskChoices.Agent, null, false, Ct);
        _time.Advance(TimeSpan.FromSeconds(10));

        var told = Assert.Single(_told);
        Assert.Equal(("Alpha", "Manager"), (told.Team, told.Member));
        Assert.Contains("crl3.digicert.com, ocsp.digicert.com and crl4.digicert.com are now allowed for Priya", told.Text);
        Assert.Equal("crl3.digicert.com, ocsp.digicert.com, crl4.digicert.com", r["allowed"]!.GetValue<string>());
        Assert.Equal(["crl3.digicert.com", "crl4.digicert.com", "ocsp.digicert.com"], Loosen(_priya).Order());
        Assert.Empty(_asks.Asks());
    }

    // ------------------------------------------------------------------ policy.checkHosts

    [Fact]
    public void CheckHosts_IsARead_NotAudited_AndAllowedToAReadOnlyRemoteViewer()
    {
        Assert.True(AuditLog.IsReadOnly("policy.checkHosts", new JsonObject { ["hosts"] = new JsonArray("*.digicert.com") }));
        Assert.Equal(RemoteAccess.Kind.Reads, RemoteAccess.Kinds["policy.checkHosts"]);
        Assert.Null(RemoteAccess.Refusal("policy.checkHosts", null, allowHostTerminals: false));
        Assert.Null(RemoteAccess.Refusal("policy.checkHosts", null, allowHostTerminals: false, RemoteAccess.Read));
        // a change next to it is still refused for a read-only viewer
        Assert.Equal(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal("policy.removeTeamHost", null, allowHostTerminals: false, RemoteAccess.Read));
    }

    [Fact]
    public void CheckHosts_KeepsTheOrder_AndAnswersBadParams()
    {
        var r = Yawble.Core.Ui.UiRpc.CheckHosts(new JsonObject { ["hosts"] = new JsonArray("*.com", "*.digicert.com", "digicert.com", "*.co.uk", "*.bbc.co.uk", "*") });
        Assert.Equal(["*.com", "*.digicert.com", "digicert.com", "*.co.uk", "*.bbc.co.uk", "*"], r.Select(n => n!["host"]!.GetValue<string>()));
        Assert.Equal([false, true, true, false, true, false], r.Select(n => n!["problem"] is null));
        Assert.Equal("bad_params", Assert.Throws<RpcException>(() => Yawble.Core.Ui.UiRpc.CheckHosts(new JsonObject())).Code);
    }
}
