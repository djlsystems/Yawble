using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Logs;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Yawble.Core.Tests.Policy;
using Xunit;
using MemberRef = Yawble.Core.Bus.MemberRef;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// Network asks (B012): a host refused for not being on the allowlist raises one ask per (team, member, host) for the
/// person; the four answers change the policy through the policy store; the Manager (or, after a deny, the agent) is told.
/// Fakes only: a temporary database, a fake clock and a recording tell; no network and no system program.
/// </summary>
public sealed class NetworkAskTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDb _db = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly PolicyStore _policies;
    private readonly List<(string Team, string Member, string Text, long? Causation)> _told = [];
    private readonly NetworkAskService _asks;
    private int _changed;
    private readonly long _alpha, _beta, _alphaManager, _priya, _bo, _betaManager, _wren;

    public NetworkAskTests()
    {
        _policies = new PolicyStore(_db.ConnectionString);
        var layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
        _asks = new NetworkAskService(_db.ConnectionString,
            new StoreNetworkPolicy(_policies, (_, _) => Task.FromResult(layout), _ => Task.FromResult<RunLayout?>(layout)),
            (team, member, text, causation, _) =>
            {
                lock (_told) _told.Add((team, member, text, causation));
                return Task.CompletedTask;
            },
            _clock) { Changed = () => Interlocked.Increment(ref _changed) };
        _alpha = Team("Alpha");
        _beta = Team("Beta");
        _alphaManager = Member(_alpha, "Manager", "manager");
        _priya = Member(_alpha, "Priya", "member");
        _bo = Member(_alpha, "Bo", "member");
        _betaManager = Member(_beta, "Manager", "manager");
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

    private bool Allowed(long member, string host) =>
        StoreNetworkPolicy.EffectiveHosts(_policies.GetGlobal(), _policies.GetMember(member), _policies.TeamHostsOfMember(member)).Contains(host);

    private static MemberRef Ref(long id, long team, string teamName, string name) => new(id, team, teamName, name, MemberRole.Member);

    // ------------------------------------------------------------------ the filters

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.20")]
    [InlineData("::1")]
    [InlineData("[fd00::1]")]
    [InlineData("localhost")]
    [InlineData("api.localhost")]
    [InlineData("printer.local")]
    [InlineData("nas.lan")]
    [InlineData("router.home.arpa")]
    [InlineData("intranet")]
    [InlineData("cloud.yawble.ai")]
    [InlineData("yawble.ai")]
    [InlineData("bus.yawble.internal")]
    [InlineData("api.anthropic.com")]
    [InlineData("console.anthropic.com")]
    [InlineData("auth.x.ai")]
    [InlineData("*.nuget.org")]
    [InlineData("api.nuget.org:443")]
    [InlineData("")]
    [InlineData("pypí.org")] // looks like pypi.org, resolves elsewhere
    [InlineData("аpi.nuget.org")] // a Cyrillic a
    [InlineData("xn--pypi-xxa.org")]
    [InlineData("api.nuget.org.")] // the gateway compares the name as sent
    [InlineData("api_nuget.org")]
    public void HostsThatAreNeverAskedAbout_StayPlainRefusals(string host)
    {
        Assert.NotNull(NetworkAskFilter.Problem(host));
        Assert.Equal(HostAskOutcome.None, _asks.Refused("Alpha/Priya", host, "CONNECT", null));
        Assert.Empty(_asks.Asks(pendingOnly: false));
        Assert.Equal(0, _changed);
    }

    [Theory]
    [InlineData("api.nuget.org", "NuGet")]
    [InlineData("registry.npmjs.org", "npm")]
    [InlineData("pypi.org", "pip")]
    [InlineData("files.pythonhosted.org", "pip")]
    [InlineData("example.com", null)]
    public void HostNamesAreAsked_WithARegistryHint(string host, string? hint)
    {
        Assert.Null(NetworkAskFilter.Problem(host));
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Priya", host, "CONNECT", null));
        if (hint is null) Assert.Null(NetworkAskFilter.Hint(host));
        else Assert.StartsWith(hint, NetworkAskFilter.Hint(host));
    }

    [Fact]
    public void AnUnknownMember_IsNotAsked() =>
        Assert.Equal(HostAskOutcome.None, _asks.Refused("Alpha/Nobody", "api.nuget.org", "CONNECT", null));

    // ------------------------------------------------------------------ dedup

    [Fact]
    public void RepeatsWhileWaiting_OnlyCountUp_OnePerTeamMemberAndHost()
    {
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Priya", "api.nuget.org", "CONNECT", null));
        Assert.Equal(HostAskOutcome.Pending, _asks.Refused("Alpha/Priya", "API.NuGet.org", "CONNECT", null));
        Assert.Equal(HostAskOutcome.Pending, _asks.Refused("Alpha/Priya", "api.nuget.org", "GET", "/v3/index.json"));
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Bo", "api.nuget.org", "CONNECT", null));
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Beta/Wren", "api.nuget.org", "CONNECT", null));

        var asks = _asks.Asks();
        Assert.Equal(3, asks.Count);
        var priya = Assert.Single(asks, a => a.Member == "Alpha/Priya");
        Assert.Equal("api.nuget.org", priya.Host);
        Assert.Equal(3, priya.Count);
        // B014: a plain-http refusal's method wins over CONNECT's, so the card can say only https is forwarded
        Assert.Equal("GET", priya.Method);
        Assert.Equal("/v3/index.json", priya.PathSample);
        Assert.True(priya.PlainHttp);
        Assert.Equal(3, _changed); // the repeats pushed nothing
        Assert.Equal(3, _asks.PendingCount());
        Assert.Empty(_told); // no message for an ask
    }

    [Fact]
    public void AtMostFiveHostsWaitPerMember_ARepeatStillCountsUp()
    {
        for (var n = 1; n <= NetworkAskService.MaxWaitingPerMember; n++)
            Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Priya", $"h{n}.example.com", "CONNECT", null));
        Assert.Equal(HostAskOutcome.None, _asks.Refused("Alpha/Priya", "h6.example.com", "CONNECT", null));
        Assert.Contains("already wait", _asks.RequestFromAgent(Ref(_priya, _alpha, "Alpha", "Priya"), "h7.example.com", "need it", 1, 2));
        Assert.Equal(HostAskOutcome.Pending, _asks.Refused("Alpha/Priya", "h1.example.com", "CONNECT", null));
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Bo", "h6.example.com", "CONNECT", null)); // another member has their own
        Assert.Equal(NetworkAskService.MaxWaitingPerMember + 1, _asks.Asks().Count);
    }

    [Fact]
    public void APlainHttpPath_IsKeptWithoutItsQuery()
    {
        _asks.Refused("Alpha/Priya", "pypi.org", "GET", "/simple/requests/?token=secret");
        var ask = Assert.Single(_asks.Asks());
        Assert.Equal("GET", ask.Method);
        Assert.Equal("/simple/requests/", ask.PathSample);
    }

    // ------------------------------------------------------------------ the four answers

    [Fact]
    public async Task AllowForThisAgent_LoosensOnlyThatMember_AndTellsTheManager()
    {
        _asks.Refused("Alpha/Priya", "api.nuget.org", "CONNECT", null);
        var id = _asks.Asks().Single().Id;

        var result = await _asks.AnswerAsync(id, NetworkAskChoices.Agent, null, Ct);

        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.True(Allowed(_priya, "api.nuget.org"));
        Assert.False(Allowed(_bo, "api.nuget.org"));
        Assert.DoesNotContain("api.nuget.org", _policies.GetGlobal().Network.AllowHosts);
        // through the policy store's loosen block, with its confirmation summary
        Assert.Equal(["network.allowHosts + api.nuget.org"], _policies.GetMember(_priya).LoosenSummary);

        var closed = Assert.Single(_asks.Asks(pendingOnly: false));
        Assert.Equal("allowed", closed.State);
        Assert.Equal(NetworkAskChoices.Agent, closed.Scope);
        Assert.Equal(Actor.LocalUser, closed.DecidedBy);
        Assert.NotNull(closed.DecidedAt);

        var told = Assert.Single(_told);
        Assert.Equal(("Alpha", "Manager"), (told.Team, told.Member));
        Assert.StartsWith("api.nuget.org is now allowed for Priya; retry what was blocked.", told.Text);

        // The current run is still refused (its allowlist was fixed at start): no new ask, the agent is told it is allowed next run.
        Assert.Equal(HostAskOutcome.AllowedNextRun, _asks.Refused("Alpha/Priya", "api.nuget.org", "CONNECT", null));
        Assert.Empty(_asks.Asks());
    }

    [Fact]
    public async Task AllowForThisTeam_LoosensEveryMemberOfTheTeam_AndClosesTheTeamsOtherAsks()
    {
        _asks.Refused("Alpha/Priya", "registry.npmjs.org", "CONNECT", null);
        _asks.Refused("Alpha/Bo", "registry.npmjs.org", "CONNECT", null);
        _asks.Refused("Beta/Wren", "registry.npmjs.org", "CONNECT", null);
        var id = _asks.Asks().Single(a => a.Member == "Alpha/Priya").Id;

        var result = await _asks.AnswerAsync(id, NetworkAskChoices.Team, null, Ct);

        foreach (var m in new[] { _alphaManager, _priya, _bo }) Assert.True(Allowed(m, "registry.npmjs.org"));
        foreach (var m in new[] { _betaManager, _wren }) Assert.False(Allowed(m, "registry.npmjs.org"));
        Assert.Equal(2, result["closed"]!.AsArray().Count);
        Assert.Equal("Beta/Wren", Assert.Single(_asks.Asks()).Member); // another team's ask still waits
        Assert.All(_asks.Asks(pendingOnly: false).Where(a => a.Team == "Alpha"), a => Assert.Equal(NetworkAskChoices.Team, a.Scope));

        var told = Assert.Single(_told);
        Assert.Equal(("Alpha", "Manager"), (told.Team, told.Member));
        Assert.StartsWith("registry.npmjs.org is now allowed for Priya and Bo; retry what was blocked.", told.Text);
        Assert.Contains("every member of Alpha", told.Text);
    }

    [Fact]
    public async Task AllowEverywhere_AddsItToTheGlobalAllowlist_AndTellsEachTeamsManager()
    {
        _asks.Refused("Alpha/Priya", "pypi.org", "CONNECT", null);
        _asks.Refused("Beta/Wren", "pypi.org", "CONNECT", null);
        _asks.Refused("Beta/Wren", "files.pythonhosted.org", "CONNECT", null);
        var id = _asks.Asks().Single(a => a.Member == "Alpha/Priya").Id;

        await _asks.AnswerAsync(id, NetworkAskChoices.Everywhere, null, Ct);

        Assert.Contains("pypi.org", _policies.GetGlobal().Network.AllowHosts);
        Assert.DoesNotContain("files.pythonhosted.org", _policies.GetGlobal().Network.AllowHosts); // exact host only
        foreach (var m in new[] { _alphaManager, _priya, _bo, _betaManager, _wren }) Assert.True(Allowed(m, "pypi.org"));
        Assert.Equal("files.pythonhosted.org", Assert.Single(_asks.Asks()).Host);

        Assert.Equal(2, _told.Count);
        Assert.Contains(_told, t => t.Team == "Alpha" && t.Member == "Manager" && t.Text.StartsWith("pypi.org is now allowed for Priya; retry what was blocked.", StringComparison.Ordinal));
        Assert.Contains(_told, t => t.Team == "Beta" && t.Member == "Manager" && t.Text.StartsWith("pypi.org is now allowed for Wren; retry what was blocked.", StringComparison.Ordinal));
        Assert.All(_told, t => Assert.Contains("every agent on this computer", t.Text));
    }

    [Fact]
    public async Task Deny_TellsTheAgent_AndTheHostIsNotAskedAgainForThatMemberFor24Hours()
    {
        _asks.Refused("Alpha/Priya", "example.com", "CONNECT", null);
        var id = _asks.Asks().Single().Id;

        await _asks.AnswerAsync(id, NetworkAskChoices.Deny, "not for this project", Ct);

        Assert.False(Allowed(_priya, "example.com"));
        Assert.Empty(_policies.GetMember(_priya).LoosenSummary);
        var denied = Assert.Single(_asks.Asks(pendingOnly: false));
        Assert.Equal("denied", denied.State);
        Assert.Null(denied.Scope);
        var told = Assert.Single(_told);
        Assert.Equal(("Alpha", "Priya"), (told.Team, told.Member));
        Assert.Contains("denied network access to example.com", told.Text);
        Assert.Contains("not for this project", told.Text);

        // 23 hours later: still a plain refusal, no new ask, nothing pushed.
        var changed = _changed;
        _clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(HostAskOutcome.Denied, _asks.Refused("Alpha/Priya", "example.com", "CONNECT", null));
        Assert.StartsWith("refused: the person denied example.com", _asks.RequestFromAgent(Ref(_priya, _alpha, "Alpha", "Priya"), "example.com", "I really need it", 1, 1));
        Assert.Empty(_asks.Asks());
        Assert.Equal(changed, _changed);
        // Another member is asked as usual.
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Bo", "example.com", "CONNECT", null));

        // After 24 hours the person is asked again.
        _clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Priya", "example.com", "CONNECT", null));
    }

    [Fact]
    public async Task OneAnswerWins_ASecondFindsNothingWaiting()
    {
        _asks.Refused("Alpha/Priya", "api.nuget.org", "CONNECT", null);
        var id = _asks.Asks().Single().Id;
        await _asks.AnswerAsync(id, NetworkAskChoices.Agent, null, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => _asks.AnswerAsync(id, NetworkAskChoices.Deny, null, Ct));
        Assert.Single(_told);
    }

    [Fact]
    public async Task TwoAnswersAtOnce_OnlyOneChangesThePolicy()
    {
        for (var round = 0; round < 20; round++)
        {
            var host = $"r{round}.example.com";
            _asks.Refused("Alpha/Priya", host, "CONNECT", null);
            var id = _asks.Asks().Single(a => a.Host == host).Id;
            var allow = Task.Run(() => _asks.AnswerAsync(id, NetworkAskChoices.Everywhere, null, Ct));
            var deny = Task.Run(() => _asks.AnswerAsync(id, NetworkAskChoices.Deny, null, Ct));
            var results = await Task.WhenAll(allow.ContinueWith(t => t.IsCompletedSuccessfully), deny.ContinueWith(t => t.IsCompletedSuccessfully));
            Assert.Equal(1, results.Count(ok => ok));
            // The policy says what the answer that won says.
            Assert.Equal(results[0], _policies.GetGlobal().Network.AllowHosts.Contains(host));
        }
    }

    [Fact]
    public async Task OnlyThePersonAnswers_NeverAnAgentOrTheConcierge()
    {
        _asks.Refused("Alpha/Priya", "api.nuget.org", "CONNECT", null);
        var id = _asks.Asks().Single().Id;
        foreach (var via in new[] { Actor.Concierge, Actor.Agent, Actor.External, Actor.Trigger })
        {
            using (Actor.Use(new Actor(via, "someone")))
                await Assert.ThrowsAsync<ArgumentException>(() => _asks.AnswerAsync(id, NetworkAskChoices.Everywhere, null, Ct));
        }
        Assert.Single(_asks.Asks());
        Assert.DoesNotContain("api.nuget.org", _policies.GetGlobal().Network.AllowHosts);

        // A Cloud Link viewer answers like the window (as other policy changes over the Cloud Link).
        using (Actor.Use(Actor.CloudViewer("person@example.com")))
            await _asks.AnswerAsync(id, NetworkAskChoices.Agent, null, Ct);
        var closed = Assert.Single(_asks.Asks(pendingOnly: false));
        Assert.Equal(("person@example.com", Actor.Cloud), (closed.DecidedBy, closed.DecidedVia));
    }

    // ------------------------------------------------------------------ request_host

    [Fact]
    public void RequestHost_AsksWithTheAgentsReason_AndJoinsAnAskTheGatewayRaised()
    {
        Assert.Equal(HostAskOutcome.Asked, _asks.Refused("Alpha/Priya", "api.nuget.org", "CONNECT", null));
        var text = _asks.RequestFromAgent(Ref(_priya, _alpha, "Alpha", "Priya"), "https://api.nuget.org/v3/index.json",
            "restore NuGet packages for the xUnit test project", workflow: 40, causation: 41);

        Assert.StartsWith("asked: the person is asked to allow api.nuget.org", text);
        Assert.Contains("blocked on api.nuget.org", text);
        Assert.Contains("Do not retry in a loop or try another site", text);
        var ask = Assert.Single(_asks.Asks());
        Assert.Equal("restore NuGet packages for the xUnit test project", ask.Reason);
        Assert.Equal(2, ask.Count);
        Assert.Equal((40L, 41L), (ask.Workflow, ask.CausationSeq));
        Assert.Contains("restore NuGet packages", Assert.Single(_asks.PendingLines()));
    }

    [Fact]
    public async Task RequestHost_ItsWorkflowGetsTheManagersMessage()
    {
        _asks.RequestFromAgent(Ref(_priya, _alpha, "Alpha", "Priya"), "api.nuget.org", "dotnet restore", workflow: 7, causation: 9);
        await _asks.AnswerAsync(_asks.Asks().Single().Id, NetworkAskChoices.Agent, null, Ct);
        Assert.Equal(9, Assert.Single(_told).Causation);
    }

    [Fact]
    public void RequestHost_FilteredHostsAndAMissingReasonAreRefused()
    {
        var priya = Ref(_priya, _alpha, "Alpha", "Priya");
        Assert.StartsWith("refused:", _asks.RequestFromAgent(priya, "127.0.0.1", "local server", 1, 1));
        Assert.StartsWith("refused:", _asks.RequestFromAgent(priya, "api.anthropic.com", "the model", 1, 1));
        Assert.StartsWith("refused:", _asks.RequestFromAgent(priya, "*.nuget.org", "all of NuGet", 1, 1));
        Assert.Throws<ArgumentException>(() => _asks.RequestFromAgent(priya, "api.nuget.org", " ", 1, 1));
        Assert.Empty(_asks.Asks(pendingOnly: false));
    }

    [Fact]
    public void TheBusOffersRequestHost_WithHostAndReason()
    {
        Assert.Contains("request_host", McpBusServer.ToolNames);
        var tool = McpBusServer.ToolList().Select(n => n!.AsObject()).Single(o => o["name"]!.GetValue<string>() == "request_host");
        Assert.Equal(["host", "reason"], tool["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
        Assert.Contains("never try another site", tool["description"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ the RPCs, the remote rule, the logs

    [Fact]
    public async Task TheAskListAndTheAnswer_AsTheWindowCallsThem()
    {
        _asks.Refused("Alpha/Priya", "api.nuget.org", "CONNECT", null);
        var list = (await _asks.DispatchAsync("network.asks", null, Ct))!.AsArray();
        var item = Assert.Single(list)!.AsObject();
        Assert.Equal("Alpha/Priya", item["member"]!.GetValue<string>());
        Assert.Equal("Priya", item["memberName"]!.GetValue<string>());
        Assert.Equal("Alpha", item["team"]!.GetValue<string>());
        Assert.Equal("api.nuget.org", item["host"]!.GetValue<string>());
        Assert.StartsWith("NuGet", item["hint"]!.GetValue<string>());
        Assert.Equal(1, item["count"]!.GetValue<int>());
        Assert.Equal(["agent", "team", "everywhere", "deny"], item["choices"]!.AsArray().Select(n => n!.GetValue<string>()));

        var bad = await Assert.ThrowsAsync<RpcException>(() => _asks.DispatchAsync("network.answer", new JsonObject { ["id"] = item["id"]!.GetValue<long>(), ["choice"] = "*.nuget.org" }, Ct));
        Assert.Equal("bad_params", bad.Code);
        var answer = (await _asks.DispatchAsync("network.answer", new JsonObject { ["id"] = item["id"]!.GetValue<long>(), ["choice"] = "agent" }, Ct))!.AsObject();
        Assert.Equal("api.nuget.org", answer["host"]!.GetValue<string>());
        Assert.Equal("Alpha/Manager", Assert.Single(answer["told"]!.AsArray())!.GetValue<string>());
        Assert.Empty((await _asks.DispatchAsync("network.asks", null, Ct))!.AsArray());
        Assert.Single((await _asks.DispatchAsync("network.asks", new JsonObject { ["all"] = true }, Ct))!.AsArray());
    }

    [Fact]
    public void TheCloudLinkMayListAndAnswer_LikeOtherPolicyChanges()
    {
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("network.asks", null, allowHostTerminals: false));
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("network.answer", new JsonObject { ["id"] = 1, ["choice"] = "agent" }, allowHostTerminals: false));
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("policy.saveLoosen", null, allowHostTerminals: false));
        // The answer is audited (a change); the list is a read.
        Assert.False(AuditLog.IsReadOnly("network.answer"));
        Assert.True(AuditLog.IsReadOnly("network.asks"));
    }

    [Fact]
    public async Task EachAskAndItsAnswer_AreInTheApprovalsLog()
    {
        _asks.RequestFromAgent(Ref(_priya, _alpha, "Alpha", "Priya"), "api.nuget.org", "dotnet restore", 1, 1);
        _asks.Refused("Alpha/Bo", "pypi.org", "CONNECT", null);
        await _asks.AnswerAsync(_asks.Asks().Single(a => a.Host == "api.nuget.org").Id, NetworkAskChoices.Team, null, Ct);

        var logs = new LogService(_db.Dir, _db.ConnectionString, _clock);
        var rows = logs.Query(new LogQuery("approvals"))["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        var allowed = Assert.Single(rows, r => r["args"]!.GetValue<string>().StartsWith("api.nuget.org", StringComparison.Ordinal));
        Assert.Equal("network", allowed["integration"]!.GetValue<string>());
        Assert.Equal("allowed for team", allowed["state"]!.GetValue<string>());
        Assert.Equal("Alpha", allowed["team"]!.GetValue<string>());
        Assert.Contains(Actor.LocalUser, allowed["by"]!.GetValue<string>());
        Assert.Contains("dotnet restore", allowed["args"]!.GetValue<string>());
        var waiting = Assert.Single(rows, r => r["args"]!.GetValue<string>().StartsWith("pypi.org", StringComparison.Ordinal));
        Assert.Equal("pending", waiting["state"]!.GetValue<string>());
        Assert.Contains("refused CONNECT", waiting["args"]!.GetValue<string>());

        var activity = logs.Query(new LogQuery(LogSources.Activity))["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Contains(activity, r => r["source"]!.GetValue<string>() == "Approvals" && r["event"]!.GetValue<string>() == "network allowed for team");
    }

    [Fact]
    public void TheConciergeStatusLines_NameTheMemberHostAndReason()
    {
        _asks.Refused("Alpha/Bo", "registry.npmjs.org", "CONNECT", null);
        _asks.Refused("Alpha/Bo", "registry.npmjs.org", "CONNECT", null);
        var line = Assert.Single(_asks.PendingLines());
        Assert.Contains("Alpha/Bo asks for registry.npmjs.org (npm", line);
        Assert.Contains("refused CONNECT", line);
        Assert.Contains("2 time(s)", line);
    }

    // ------------------------------------------------------------------ the gateway

    [Fact]
    public async Task TheGateway_RaisesTheAskOnARefusal_AndItsTextNamesRequestHost()
    {
        await using var f = await GatewayFixture.StartAsync();
        var team = InsertIn(f.Db, "INSERT INTO teams (name, created_at) VALUES ('Alpha', '2026-11-13T00:00:00Z') RETURNING id;");
        InsertIn(f.Db, $"INSERT INTO members (team_id, name, role, created_at) VALUES ({team}, 'm5', 'member', '2026-11-13T00:00:00Z') RETURNING id;");
        var policies = new PolicyStore(f.Db.ConnectionString);
        var layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Claude);
        var asks = new NetworkAskService(f.Db.ConnectionString,
            new StoreNetworkPolicy(policies, (_, _) => Task.FromResult(layout), _ => Task.FromResult<RunLayout?>(layout)), (_, _, _, _, _) => Task.CompletedTask, f.Clock);
        f.Host.Refusals = asks;
        await using var gateway = f.StartRun(5);

        async Task<string> Body(string request)
        {
            var (status, stream, client) = await ProxyClient.RawAsync(gateway, request);
            using (client)
            {
                Assert.StartsWith("HTTP/1.1 403", status);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync(Ct);
            }
        }

        var first = await Body("CONNECT api.nuget.org:443 HTTP/1.1\r\nHost: api.nuget.org:443\r\n\r\n");
        Assert.Contains("allowlist", first); // the JSON body escapes the apostrophe
        Assert.Contains("request_host", first);
        await Body("CONNECT api.nuget.org:443 HTTP/1.1\r\nHost: api.nuget.org:443\r\n\r\n");
        // Never an ask: an IP literal, a vendor host (a floor rule) or a port that is not allowed.
        var ip = await Body("CONNECT 169.254.169.254:443 HTTP/1.1\r\nHost: 169.254.169.254:443\r\n\r\n");
        Assert.DoesNotContain("request_host", ip);
        await Body("CONNECT api.anthropic.com:443 HTTP/1.1\r\nHost: api.anthropic.com:443\r\n\r\n");
        await Body("CONNECT registry.npmjs.org:22 HTTP/1.1\r\nHost: registry.npmjs.org:22\r\n\r\n");
        // Plain http to a host name off the allowlist is reported too (allowed, it is reachable over https).
        var plain = await Body("GET http://pypi.org/simple/?k=secret HTTP/1.1\r\nHost: pypi.org\r\nConnection: close\r\n\r\n");
        Assert.Contains("request_host", plain);

        var open = asks.Asks();
        Assert.Equal(2, open.Count);
        var nuget = Assert.Single(open, a => a.Host == "api.nuget.org");
        Assert.Equal(("Alpha/m5", 2, "CONNECT"), (nuget.Member, nuget.Count, nuget.Method));
        var pypi = Assert.Single(open, a => a.Host == "pypi.org");
        Assert.Equal(("GET", "/simple/"), (pypi.Method, pypi.PathSample));

        // The gateway log keeps the refusal as before.
        Assert.Contains(f.Log.Read(5), e => e.Kind == GatewayEventKinds.ConnectDenied && e.Target == "api.nuget.org:443" && e.Detail == RunGateway.NotOnAllowlist);
    }

    [Fact]
    public void TheRefusalText_ByWhatBecameOfTheAsk()
    {
        Assert.Equal("host not on this agent's allowlist", RunGateway.AllowlistText("10.0.0.1", HostAskOutcome.None));
        Assert.Contains("request_host", RunGateway.AllowlistText("api.nuget.org", HostAskOutcome.Asked));
        Assert.Contains("request_host", RunGateway.AllowlistText("api.nuget.org", HostAskOutcome.Pending));
        Assert.Contains("denied api.nuget.org", RunGateway.AllowlistText("api.nuget.org", HostAskOutcome.Denied));
        Assert.Contains("next run", RunGateway.AllowlistText("api.nuget.org", HostAskOutcome.AllowedNextRun));
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
