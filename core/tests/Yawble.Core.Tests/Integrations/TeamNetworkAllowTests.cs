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
/// B014 (card 71): the team's network allow list (0041) and the network-ask fixes. "Allow for this team" writes the
/// team's list in one write; every member's run of that team (later hires too) gets it through the policy merger; Remove
/// takes it away for the next run. A name resolving only to private addresses raises no ask; a removed member's waiting
/// ask closes; the 24-hour deny is the member's id, so a new member with an old name is asked again; a plain-http ask
/// carries plainHttp. Fakes only: a temporary database, fake runners and credentials, a fake resolver; no network.
/// </summary>
public sealed class TeamNetworkAllowTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly RunLayout _layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
    private readonly List<(string Team, string Member, string Text)> _told = [];
    private TerminalHub _terminals = null!;
    private BusEngine _bus = null!;
    private RpcDispatcher _rpc = null!;
    private PolicyStore _policies = null!;
    private NetworkAskService _asks = null!;
    private int _changed;

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
        _asks = new NetworkAskService(_db.ConnectionString, Store(_policies), (team, member, text, _, _) =>
        {
            lock (_told) _told.Add((team, member, text));
            return Task.CompletedTask;
        }) { Changed = () => Interlocked.Increment(ref _changed) };
        _bus.NetworkAsks = _asks;
        _rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui, audit: new AuditLog(_db.ConnectionString), networkAsks: _asks);
    }

    public async ValueTask DisposeAsync()
    {
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private StoreNetworkPolicy Store(PolicyStore store) => new(store, (_, _) => Task.FromResult(_layout), _ => Task.FromResult<RunLayout?>(_layout));

    /// <summary>A call as the person at the window makes it (audited by the dispatcher).</summary>
    private async Task<JsonNode?> Call(string method, object? args = null) =>
        await _rpc.DispatchAsAsync(Actor.Window, method, args is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

    private async Task<RpcException> Refused(string method, object? args = null) => await Assert.ThrowsAsync<RpcException>(() => Call(method, args));

    private async Task<long> CreateTeam(string name, params string[] members)
    {
        var r = await Call("teams.create", new { name, managerCli = "claude", members = members.Select(m => new { name = m, cli = "claude" }).ToArray() });
        return r!["teamId"]!.GetValue<long>();
    }

    private async Task<long> IdOf(long teamId, string name) =>
        (await Call("teams.agents", new { teamId }))!.AsArray().Single(a => a!["member"]!["name"]!.GetValue<string>() == name)!["member"]!["id"]!.GetValue<long>();

    private async Task<long> Hire(long teamId, string name) =>
        (await Call("members.add", new { teamId, name, cli = "claude" }))!["id"]!.GetValue<long>();

    private async Task<IReadOnlyList<string>> EffectiveHosts(long memberId) =>
        (await Call("policy.getMember", new { memberId }))!["effective"]!["allowHosts"]!.AsArray().Select(h => h!.GetValue<string>()).ToList();

    private async Task<IReadOnlyList<string>> TeamHosts(long teamId) =>
        (await Call("policy.teamHosts", new { teamId }))!["hosts"]!.AsArray().Select(h => h!["host"]!.GetValue<string>()).ToList();

    // ------------------------------------------------------------------ §1 the team's allow list

    [Fact]
    public async Task AllowForThisTeam_ThenHire_TheNewMembersMergedPolicyAllowsTheHost_AndNoOtherTeams()
    {
        var payments = await CreateTeam("payments", "Ada");
        var docs = await CreateTeam("docs", "Dee");
        _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);

        await _asks.AnswerAsync(Assert.Single(_asks.Asks()).Id, NetworkAskChoices.Team, null, Ct);

        // One row in the team's list, with who added it; no member's loosen block was written.
        var listed = (await Call("policy.teamHosts", new { teamId = payments }))!["hosts"]!.AsArray();
        var row = Assert.Single(listed)!;
        Assert.Equal("pypi.org", row["host"]!.GetValue<string>());
        Assert.Equal(Actor.LocalUser, row["addedBy"]!.GetValue<string>());
        Assert.Equal(Actor.App, row["addedVia"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(row["addedAt"]!.GetValue<string>()));
        Assert.Null(_policies.GetMember(await IdOf(payments, "Ada")).Loosen.Network);

        // A member hired afterwards gets it with no extra step, as its run's merged policy (what the launcher uses).
        var cleo = await Hire(payments, "Cleo");
        Assert.Contains("pypi.org", await EffectiveHosts(cleo));
        Assert.Contains("pypi.org", _policies.Effective(cleo, _layout).Policy.AllowHosts);
        Assert.Contains("network.allowHosts + pypi.org (team)", _policies.Effective(cleo, _layout).Policy.Loosened);
        Assert.Equal(HostAskOutcome.AllowedNextRun, _asks.Refused("payments/Cleo", "pypi.org", "CONNECT", null));
        // A team host never widens another team, including a later hire there.
        Assert.DoesNotContain("pypi.org", await EffectiveHosts(await IdOf(docs, "Dee")));
        Assert.DoesNotContain("pypi.org", await EffectiveHosts(await Hire(docs, "Eve")));
        Assert.Empty(await TeamHosts(docs));
    }

    [Fact]
    public async Task OldPerMemberEntries_StayAsTheyAre_ThroughATeamAllowAndItsRemove()
    {
        var team = await CreateTeam("payments", "Ada", "Ben");
        var ben = await IdOf(team, "Ben");
        // What the old "Allow for this team" left: a host in a member's own loosen block.
        var loosen = new { network = new { allowHosts = new[] { "registry.npmjs.org" } } };
        await Call("policy.saveLoosen", new { memberId = ben, loosen, confirmed = new[] { "network.allowHosts + registry.npmjs.org" } });
        _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);

        await _asks.AnswerAsync(Assert.Single(_asks.Asks()).Id, NetworkAskChoices.Team, null, Ct);
        Assert.Equal(["registry.npmjs.org"], _policies.GetMember(ben).Loosen.Network!.AllowHosts);
        Assert.Equal(["registry.npmjs.org", "pypi.org"], await EffectiveHosts(ben));

        await Call("policy.removeTeamHost", new { teamId = team, host = "pypi.org" });
        Assert.Equal(["registry.npmjs.org"], _policies.GetMember(ben).Loosen.Network!.AllowHosts);
        Assert.Equal(["registry.npmjs.org"], await EffectiveHosts(ben));
    }

    [Theory]
    [InlineData("api.anthropic.com")]    // a vendor host
    [InlineData("console.anthropic.com")] // under a vendor domain
    [InlineData("grok.com")]
    [InlineData("*.example.com")]         // a wildcard
    [InlineData("Example.com")]           // not lower case
    [InlineData("10.0.0.5")]              // an address
    [InlineData("gw.yawble.internal")]    // the gateway's own
    public async Task AFloorRejectedHost_IsNeverWrittenToTheTeamList(string host)
    {
        var team = await CreateTeam("payments", "Ada");
        var ada = await IdOf(team, "Ada");

        var e = Assert.Throws<PolicyException>(() => _policies.AddTeamHost(team, host, [(ada, _layout)], "pat", Actor.App));

        Assert.NotEmpty(e.Errors);
        Assert.Empty(_policies.TeamHosts(team));
        Assert.DoesNotContain(host, await EffectiveHosts(ada));
    }

    [Fact]
    public async Task ADryRunRefusal_WritesNothing_AndTheAskStaysWaiting_NoMemberHalfLoosened()
    {
        var team = await CreateTeam("payments", "Ada", "Ben");
        // The sandbox dry run passes the first member's run and refuses the next: the old per-member writes would have left
        // the first one loosened and the ask waiting.
        var seen = new List<RunLayout>();
        var checking = new PolicyStore(_db.ConnectionString, (_, _, _, layout) =>
        {
            seen.Add(layout);
            return seen.Count >= 2 ? ["the sandbox refused the compiled request: test"] : [];
        });
        var asks = new NetworkAskService(_db.ConnectionString, Store(checking), (_, _, _, _, _) => Task.CompletedTask);
        asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);

        var e = await Assert.ThrowsAsync<RpcException>(() => asks.DispatchAsync("network.answer", new JsonObject { ["id"] = Assert.Single(asks.Asks()).Id, ["choice"] = "team" }, Ct));

        Assert.Equal("policy_refused", e.Code);
        Assert.Contains("sandbox refused", e.Message);
        Assert.True(seen.Count >= 2); // every member's run was checked before the one write
        Assert.Empty(_policies.TeamHosts(team));
        Assert.All(_policies.TeamMemberIds(team), m => Assert.Null(_policies.GetMember(m).Loosen.Network));
        Assert.Equal("pending", Assert.Single(asks.Asks()).State);
    }

    [Fact]
    public async Task Remove_TakesTheHostAwayForTheNextRun_IsAudited_AndARepeatIsNotFound()
    {
        var team = await CreateTeam("payments", "Ada");
        var ada = await IdOf(team, "Ada");
        _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);
        await _asks.AnswerAsync(Assert.Single(_asks.Asks()).Id, NetworkAskChoices.Team, null, Ct);
        Assert.Contains("pypi.org", _policies.Effective(ada, _layout).Policy.AllowHosts);

        var after = await Call("policy.removeTeamHost", new { teamId = team, host = "pypi.org" });

        Assert.Empty(after!["hosts"]!.AsArray());
        Assert.DoesNotContain("pypi.org", _policies.Effective(ada, _layout).Policy.AllowHosts);
        Assert.DoesNotContain("pypi.org", await EffectiveHosts(ada));
        Assert.Equal("not_found", (await Refused("policy.removeTeamHost", new { teamId = team, host = "pypi.org" })).Code);
        var audit = new AuditLog(_db.ConnectionString).Recent();
        Assert.Contains(audit, r => r.Method == "policy.removeTeamHost" && r.Outcome == "done" && r.Via == Actor.App);
        Assert.Contains(audit, r => r.Method == "policy.removeTeamHost" && r.Outcome.StartsWith("refused: not_found", StringComparison.Ordinal));
        Assert.DoesNotContain(audit, r => r.Method == "policy.teamHosts"); // a read
        // The Cloud Link may list and remove, as it may change the member policy.
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("policy.teamHosts", null, allowHostTerminals: false));
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("policy.removeTeamHost", null, allowHostTerminals: false));
    }

    [Fact]
    public void TheMerger_DropsAFloorRefusedTeamHostWithANote_NeverAViolation()
    {
        var merged = PolicyMerger.Merge(GlobalPolicy.Default, null, null, _layout, null, null, ["pypi.org", "api.anthropic.com"]);

        Assert.Empty(merged.Violations);
        Assert.Contains("pypi.org", merged.Policy.AllowHosts);
        Assert.DoesNotContain("api.anthropic.com", merged.Policy.AllowHosts);
        Assert.Contains(merged.Policy.Notes, n => n.Contains("api.anthropic.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingTheTeam_ForgetsItsList()
    {
        var team = await CreateTeam("payments", "Ada");
        _policies.AddTeamHost(team, "pypi.org", [], "pat", Actor.App);
        new IntegrationService(_db.ConnectionString, _bus).ForgetTeam(team, "payments");
        Assert.Empty(_policies.TeamHosts(team));
    }

    // ------------------------------------------------------------------ §3 departed members and the deny

    [Fact]
    public async Task RemovingAMember_ClosesTheirWaitingAsk_AndNeedsYouRefreshes()
    {
        var team = await CreateTeam("payments", "Ada", "Ben");
        _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);
        _asks.Refused("payments/Ben", "pypi.org", "CONNECT", null);
        var adaAsk = _asks.Asks().Single(a => a.MemberName == "Ada").Id;
        var before = _changed;

        await Call("members.remove", new { memberId = await IdOf(team, "Ada") });

        Assert.Equal("payments/Ben", Assert.Single(_asks.Asks()).Member);
        var closed = _asks.Asks(pendingOnly: false).Single(a => a.Id == adaAsk);
        Assert.Equal("closed", closed.State);
        Assert.Contains("removed from the team", closed.Note);
        Assert.NotNull(closed.DecidedAt);
        Assert.True(_changed > before);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => _asks.AnswerAsync(adaAsk, NetworkAskChoices.Agent, null, Ct));
        Assert.Contains("not waiting", e.Message);
    }

    [Fact]
    public async Task ANewMemberWithAnOldMembersName_IsAskedAgain_NotQuietenedByTheOldDeny()
    {
        var team = await CreateTeam("payments", "Ada");
        _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null);
        await _asks.AnswerAsync(Assert.Single(_asks.Asks()).Id, NetworkAskChoices.Deny, null, Ct);
        Assert.Equal(HostAskOutcome.Denied, _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null));

        await Call("members.remove", new { memberId = await IdOf(team, "Ada") });
        var newAda = await Hire(team, "Ada");

        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("payments/Ada", "pypi.org", "CONNECT", null));
        Assert.Equal(newAda, Assert.Single(_asks.Asks()).MemberId);
    }

    [Fact]
    public async Task APlainHttpRefusal_CarriesPlainHttp_ACONNECTOneDoesNot()
    {
        await CreateTeam("payments", "Ada", "Ben");
        _asks.Refused("payments/Ada", "example.com", "GET", "/index.html?token=x");
        _asks.Refused("payments/Ben", "example.com", "CONNECT", null);
        // a CONNECT first, then plain http: the ask then says plain http too
        _asks.Refused("payments/Ben", "example.org", "CONNECT", null);
        _asks.Refused("payments/Ben", "example.org", "POST", "/form");

        var json = (await Call("network.asks"))!.AsArray().Select(a => a!.AsObject()).ToList();

        bool Plain(string member, string host) => json.Single(a => a["member"]!.GetValue<string>() == member && a["host"]!.GetValue<string>() == host)["plainHttp"]!.GetValue<bool>();
        Assert.True(Plain("payments/Ada", "example.com"));
        Assert.False(Plain("payments/Ben", "example.com"));
        Assert.True(Plain("payments/Ben", "example.org"));
    }

    [Fact]
    public void Migration0041_KeepsEveryAsk_AndFillsMemberIdFromTheMembersStillThere()
    {
        using var db = new TempDb();
        // Back to the 0040 shape: no team list, network_asks without member_id or the closed state.
        Exec(db, """
            DROP TABLE team_network_allows;
            DROP TABLE network_asks;
            CREATE TABLE network_asks (
                id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, team_id INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
                member TEXT NOT NULL, host TEXT NOT NULL, reason TEXT NULL, method TEXT NULL, path_sample TEXT NULL,
                count INTEGER NOT NULL DEFAULT 1, last_seen TEXT NOT NULL, workflow INTEGER NULL, causation_seq INTEGER NULL,
                state TEXT NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'allowed', 'denied')),
                scope TEXT NULL CHECK (scope IS NULL OR scope IN ('agent', 'team', 'everywhere')),
                decided_by TEXT NULL, decided_via TEXT NULL, decided_at TEXT NULL, note TEXT NULL) STRICT;
            DELETE FROM schema_migrations WHERE name LIKE '0041%';
            INSERT INTO teams (id, name, created_at) VALUES (7, 'Alpha', 't');
            INSERT INTO members (id, team_id, name, role, created_at) VALUES (70, 7, 'Priya', 'member', 't');
            INSERT INTO network_asks (at, team_id, member, host, count, last_seen, state, decided_at) VALUES
                ('t', 7, 'Alpha/Priya', 'pypi.org', 1, 't', 'denied', 't'),
                ('t', 7, 'Alpha/Gone', 'pypi.org', 2, 't', 'pending', NULL);
            """);

        Yawble.Core.Storage.SchemaMigrator.Migrate(db.ConnectionString);

        var asks = new NetworkAskService(db.ConnectionString, Store(new PolicyStore(db.ConnectionString)), (_, _, _, _, _) => Task.CompletedTask).Asks(pendingOnly: false);
        Assert.Equal(2, asks.Count);
        Assert.Equal(70, asks.Single(a => a.MemberName == "Priya").MemberId);
        Assert.Null(asks.Single(a => a.MemberName == "Gone").MemberId);
        Assert.Equal(2, asks.Single(a => a.MemberName == "Gone").Count);
        Assert.Empty(new PolicyStore(db.ConnectionString).TeamHosts(7));
    }

    private static void Exec(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ §3 the gateway resolves before asking

    [Fact]
    public async Task TheGateway_ANameResolvingOnlyToPrivateAddresses_RaisesNoAsk_APublicOneDoes()
    {
        await using var f = await GatewayFixture.StartAsync();
        var team = InsertIn(f.Db, "INSERT INTO teams (name, created_at) VALUES ('Alpha', '2026-11-13T00:00:00Z') RETURNING id;");
        InsertIn(f.Db, $"INSERT INTO members (team_id, name, role, created_at) VALUES ({team}, 'm5', 'member', '2026-11-13T00:00:00Z') RETURNING id;");
        var asks = new NetworkAskService(f.Db.ConnectionString, Store(new PolicyStore(f.Db.ConnectionString)), (_, _, _, _, _) => Task.CompletedTask, f.Clock);
        f.Host.Refusals = asks;
        f.Dialer!.AddName("localtest.me", IPAddress.Loopback);
        f.Dialer.AddName("x.nip.io", IPAddress.Parse("10.1.2.3"));
        f.Dialer.AddName("link.example.net", IPAddress.Parse("169.254.169.254"), IPAddress.Parse("fe80::1"));
        f.Dialer.AddName("pkgs.example.com", IPAddress.Parse("93.184.216.34"));
        await using var gateway = f.StartRun(5);

        foreach (var name in new[] { "localtest.me", "x.nip.io", "link.example.net" })
        {
            Assert.DoesNotContain("request_host", await RefusedAsync(gateway, $"CONNECT {name}:443 HTTP/1.1\r\nHost: {name}:443\r\n\r\n"));
            Assert.DoesNotContain("request_host", await RefusedAsync(gateway, $"GET http://{name}/ HTTP/1.1\r\nHost: {name}\r\n\r\n"));
        }
        Assert.Empty(asks.Asks(pendingOnly: false));

        // the control: a name with a public address is asked about, and a plain-http refusal says only https is forwarded
        Assert.Contains("request_host", await RefusedAsync(gateway, "CONNECT pkgs.example.com:443 HTTP/1.1\r\nHost: pkgs.example.com:443\r\n\r\n"));
        var plain = await RefusedAsync(gateway, "GET http://pkgs.example.com/simple/ HTTP/1.1\r\nHost: pkgs.example.com\r\n\r\n");
        Assert.Contains("Only https is forwarded", plain);
        var ask = Assert.Single(asks.Asks());
        Assert.Equal("pkgs.example.com", ask.Host);
        Assert.True(ask.PlainHttp);
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

    private static long InsertIn(TempDb db, string sql)
    {
        using var c = new SqliteConnection(db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
