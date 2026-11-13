using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Ui;

/// <summary>
/// The UI's IPC methods, through the real dispatcher, bus engine, policy store and terminal hub, with fake
/// runners and credentials. No real credential is used or read.
/// </summary>
public sealed class UiRpcTests : IAsyncLifetime
{
    private readonly TempDb _db = new();
    private readonly List<JsonObject> _events = [];
    private AppEvents _hub = null!;
    private TerminalHub _terminals = null!;
    private FakeInteractiveSessions _interactive = null!;
    private BusEngine _bus = null!;
    private RpcDispatcher _rpc = null!;
    private IDisposable? _subscription;
    private FakePtyRunner _runner = null!;

    public async ValueTask InitializeAsync()
    {
        _hub = new AppEvents();
        _subscription = _hub.Subscribe(e => { lock (_events) _events.Add(e); return Task.CompletedTask; });
        _terminals = new TerminalHub(_hub);
        var policies = new PolicyStore(_db.ConnectionString);
        BusEngine? busRef = null;
        _runner = new FakePtyRunner(_terminals, id => busRef!.MemberAsync(id).GetAwaiter().GetResult()?.Cli ?? "claude",
            ticks: 3, tick: TimeSpan.FromMilliseconds(40));
        _bus = await BusHost.StartAsync(_db.ConnectionString, _ => null, null, _runner, () => policies.GetGlobal().App.MaxConcurrentAgents);
        busRef = _bus;
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
        var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
        var ui = new UiRpc(_bus, policies, new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
            new FakeHostTerminals(_terminals, TimeSpan.FromMilliseconds(5)), _interactive = new FakeInteractiveSessions(_terminals, _bus), _terminals, 16_384);
        _rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
    }

    public async ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        await _bus.DisposeAsync();
        _db.Dispose();
    }

    private async Task<JsonNode?> Call(string method, object? args = null) =>
        await _rpc.DispatchAsync(method, args is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject(), TestContext.Current.CancellationToken);

    private async Task<RpcException> Refused(string method, object? args = null) =>
        await Assert.ThrowsAsync<RpcException>(() => Call(method, args));

    private async Task<long> CreateMixedTeam(string name = "payments")
    {
        var r = await Call("teams.create", new
        {
            name, managerCli = "claude", repoPath = "/home/pat/src/payments",
            members = new[] { new { name = "Ada", cli = "claude" }, new { name = "Ben", cli = "grok" } },
        });
        return r!["teamId"]!.GetValue<long>();
    }

    private async Task<JsonArray> Agents(long teamId) => (await Call("teams.agents", new { teamId }))!.AsArray();

    private static long IdOf(JsonArray agents, string name) =>
        agents.Single(a => a!["member"]!["name"]!.GetValue<string>() == name)!["member"]!["id"]!.GetValue<long>();

    [Fact]
    public async Task TwoTeamsOneMixed_AddAndRemoveAgents()
    {
        var mixed = await CreateMixedTeam();
        var plain = (await Call("teams.create", new { name = "docs", managerCli = "grok", members = Array.Empty<object>() }))!["teamId"]!.GetValue<long>();

        var teams = (await Call("teams.list"))!.AsArray();
        Assert.Equal(["payments", "docs"], teams.Select(t => t!["name"]!.GetValue<string>()));
        Assert.Equal("/home/pat/src/payments", teams[0]!["repoPath"]!.GetValue<string>());

        var agents = await Agents(mixed);
        Assert.Equal(["Manager", "Ada", "Ben"], agents.Select(a => a!["member"]!["name"]!.GetValue<string>()));
        Assert.Equal(["claude", "claude", "grok"], agents.Select(a => a!["member"]!["cli"]!.GetValue<string>()));
        Assert.Equal("manager", agents[0]!["member"]!["role"]!.GetValue<string>());
        Assert.All(agents, a => Assert.Equal("model + bus", a!["net"]!.GetValue<string>()));
        Assert.Equal("grok", (await Agents(plain))[0]!["member"]!["cli"]!.GetValue<string>());

        await Call("members.add", new { teamId = plain, name = "Cleo", cli = "claude" });
        Assert.Equal(2, (await Agents(plain)).Count);
        Assert.Equal("exists", (await Refused("members.add", new { teamId = plain, name = "cleo", cli = "claude" })).Code);
        Assert.Equal("bad_cli", (await Refused("members.add", new { teamId = plain, name = "Dee", cli = "nonesuch" })).Code);

        await Call("members.remove", new { memberId = IdOf(await Agents(plain), "Cleo") });
        Assert.Single(await Agents(plain));
        Assert.Equal("not_allowed", (await Refused("members.remove", new { memberId = IdOf(await Agents(plain), "Manager") })).Code);
        Assert.Equal("exists", (await Refused("teams.create", new { name = "Payments" })).Code);
    }

    [Fact]
    public async Task MemberSettings_CliModelAndCredentialChoice()
    {
        var team = await CreateMixedTeam();
        var ben = IdOf(await Agents(team), "Ben");
        var updated = await Call("members.update", new { memberId = ben, cli = "grok", instructions = "write tests", model = "grok-code", credential = "apiKey" });
        Assert.Equal("apiKey", updated!["credential"]!.GetValue<string>());
        Assert.Equal("grok-code", updated["model"]!.GetValue<string>());
        Assert.Equal("bad_credential", (await Refused("members.update", new { memberId = ben, cli = "grok", credential = "subscription" })).Code);
        var switched = await Call("members.update", new { memberId = ben, cli = "claude", credential = "subscription" });
        Assert.Equal("claude", switched!["cli"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChangeOneAgentsPolicy_TightenSavesWideningIsRefused()
    {
        var team = await CreateMixedTeam();
        var ada = IdOf(await Agents(team), "Ada");

        var saved = await Call("policy.saveOverride", new { memberId = ada, @override = new { compute = new { wallTimeoutSec = 900 } } });
        Assert.Equal(900, saved!["effective"]!["compute"]!["wallTimeoutSec"]!.GetValue<int>());
        Assert.Equal(7200, saved["global"]!["compute"]!["wallTimeoutSec"]!.GetValue<int>());
        Assert.Equal("15 min", (await Agents(team)).Single(a => a!["member"]!["id"]!.GetValue<long>() == ada)!["wallTimeout"]!.GetValue<string>());
        // Only Ada changed.
        Assert.Equal("2 h", (await Agents(team)).Single(a => a!["member"]!["name"]!.GetValue<string>() == "Ben")!["wallTimeout"]!.GetValue<string>());

        var refused = await Refused("policy.saveOverride", new { memberId = ada, @override = new { network = new { allowHosts = new[] { "registry.npmjs.org" } } } });
        Assert.Equal("policy_refused", refused.Code);
        Assert.Contains("only the loosen block can add a host", refused.Message);
        refused = await Refused("policy.saveOverride", new { memberId = ada, @override = new { app = new { maxConcurrentAgents = 9 } } });
        Assert.Contains("never overridable", refused.Message);

        var banner = await Call("terminal.banner", new { memberId = ada });
        Assert.Matches(@"^payments / Ada · sandbox · \w+ · claude · net: model \+ bus$", banner!["title"]!.GetValue<string>());
        Assert.Contains("timeout 15 min", banner["details"]!.AsArray().Select(d => d!.GetValue<string>()));
    }

    [Fact]
    public async Task LoosenBlock_PreviewThenExactConfirmation()
    {
        var team = await CreateMixedTeam();
        var ben = IdOf(await Agents(team), "Ben");
        var loosen = new { network = new { allowHosts = new[] { "registry.npmjs.org" } } };

        var preview = (await Call("policy.previewLoosen", new { memberId = ben, loosen }))!["loosened"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.Equal(["network.allowHosts + registry.npmjs.org"], preview);

        var wrong = await Refused("policy.saveLoosen", new { memberId = ben, loosen, confirmed = Array.Empty<string>() });
        Assert.Contains("does not match", wrong.Message);

        var saved = await Call("policy.saveLoosen", new { memberId = ben, loosen, confirmed = preview });
        Assert.Equal(["registry.npmjs.org"], saved!["effective"]!["allowHosts"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.NotNull(saved["loosenApprovedAt"]);
        var card = (await Agents(team)).Single(a => a!["member"]!["id"]!.GetValue<long>() == ben)!;
        Assert.Equal(["network.allowHosts + registry.npmjs.org"], card["loosened"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("model + bus + registry.npmjs.org", card["net"]!.GetValue<string>());

        var vendor = await Refused("policy.previewLoosen", new { memberId = ben, loosen = new { network = new { allowHosts = new[] { "api.x.ai" } } } });
        Assert.Equal("policy_refused", vendor.Code);
    }

    [Fact]
    public async Task GlobalPolicy_TheRunLimitReachesTheScheduler()
    {
        var global = (await Call("policy.getGlobal"))!.AsObject();
        Assert.Equal(4, (await Call("app.overview"))!["slots"]!["limit"]!.GetValue<int>());
        global["app"]!["maxConcurrentAgents"] = 3;
        global["network"]!["allowHosts"] = new JsonArray("registry.npmjs.org");
        await Call("policy.saveGlobal", new { policy = global });
        Assert.Equal(3, (await Call("app.overview"))!["slots"]!["limit"]!.GetValue<int>());
        Assert.Equal("registry.npmjs.org", (await Call("policy.getGlobal"))!["network"]!["allowHosts"]![0]!.GetValue<string>());

        global["network"]!["allowHosts"] = new JsonArray("api.anthropic.com");
        Assert.Equal("policy_refused", (await Refused("policy.saveGlobal", new { policy = global })).Code);
        global["app"]!["maxConcurrentAgents"] = 0;
        Assert.Equal("policy_refused", (await Refused("policy.saveGlobal", new { policy = global })).Code);
    }

    /// <summary>A Connection a service refused counts in Needs You (the tray and the window), as a count only: no name or value.</summary>
    [Fact]
    public async Task Overview_CountsConnectionsNeedingReconnect()
    {
        var conns = new Yawble.Core.Connections.ConnectionService(new Yawble.Core.Tests.Connections.MemoryCredentialStore(), _db.ConnectionString);
        await using var integrations = new Yawble.Core.Integrations.IntegrationService(_db.ConnectionString, _bus, connections: conns);
        _bus.Integrations = integrations;
        var v = conns.Save(null, "Pets key", [new("token", "Token", true, null)], new Dictionary<string, string> { ["token"] = "overview-secret-123456" });
        try
        {
            var before = await Call("app.overview");
            Assert.Equal(0, before!["needsReconnect"]!.GetValue<int>());
            var baseline = before["needsYou"]!.GetValue<int>();
            conns.MarkStatus(v.Id, "needs_reconnect", "the service answered 401 Unauthorized");
            var after = (await Call("app.overview"))!;
            Assert.Equal(1, after["needsReconnect"]!.GetValue<int>());
            Assert.Equal(baseline + 1, after["needsYou"]!.GetValue<int>());
            Assert.DoesNotContain("Pets key", after.ToJsonString());
        }
        finally { conns.Remove(v.Id); _bus.Integrations = null; }
    }

    [Fact]
    public async Task Credentials_BothSwitches_NoValueEverComesBack()
    {
        const string token = "fake-setup-token-for-tests-only";
        const string anthropicKey = "fake-anthropic-key-for-tests";
        const string xaiKey = "fake-xai-key-for-tests";
        var status = await Call("credentials.status");
        Assert.Equal("fake", status!["implementation"]!.GetValue<string>());
        Assert.False(status["claude"]!["subscription"]!["connected"]!.GetValue<bool>());

        Assert.Equal("not_connected", (await Refused("credentials.setMode", new { cli = "claude", mode = "apiKey" })).Code);
        Assert.Equal("bad_format", (await Refused("credentials.setClaudeSetupToken", new { token = "short" })).Code);

        // Claude: the host pane, then the paste.
        var pane = await Call("hostTerminal.start", new { kind = IHostTerminals.ClaudeSetupToken });
        await WaitFor(() => _terminals.SessionOf(pane!["channel"]!.GetValue<string>()) is { Ended: true });
        await Call("credentials.setClaudeSetupToken", new { token });
        await Call("credentials.setAnthropicApiKey", new { key = anthropicKey });
        status = await Call("credentials.setMode", new { cli = "claude", mode = "apiKey" });
        Assert.Equal("apiKey", status!["claude"]!["mode"]!.GetValue<string>());
        Assert.True(status["claude"]!["subscription"]!["connected"]!.GetValue<bool>());
        Assert.NotNull(status["claude"]!["subscription"]!["expiresAt"]);

        // Grok: the host pane signs in; then the key and the switch.
        await Call("hostTerminal.start", new { kind = IHostTerminals.GrokLogin });
        await WaitFor(async () => (await Call("credentials.status"))!["grok"]!["login"]!["signedIn"]!.GetValue<bool>());
        await Call("credentials.setXaiApiKey", new { key = xaiKey });
        status = await Call("credentials.setMode", new { cli = "grok", mode = "apiKey" });
        Assert.Equal("apiKey", status!["grok"]!["mode"]!.GetValue<string>());
        status = await Call("credentials.setMode", new { cli = "grok", mode = "login" });
        Assert.Equal("login", status!["grok"]!["mode"]!.GetValue<string>());

        string pushed;
        lock (_events) pushed = string.Join("", _events.Select(e => e.ToJsonString()));
        var all = status.ToJsonString() + pushed;
        Assert.DoesNotContain(token, all);
        Assert.DoesNotContain(anthropicKey, all);
        Assert.DoesNotContain(xaiKey, all);
    }

    [Fact]
    public void SetupTokenNearItsYear_AsksToRenew()
    {
        var creds = new FakeModelCredentials { SetupTokenAgeDays = 340 };
        creds.SetClaudeSetupToken("fake-setup-token-old");
        Assert.True(creds.Status().Claude.Subscription.RenewSoon);
        var fresh = new FakeModelCredentials();
        fresh.SetClaudeSetupToken("fake-setup-token-new");
        Assert.False(fresh.Status().Claude.Subscription.RenewSoon);
    }

    [Fact]
    public async Task Terminal_LiveOutputReplayOnReattachAndStop()
    {
        var team = await CreateMixedTeam();
        var manager = IdOf(await Agents(team), "Manager");
        var channel = TerminalHub.MemberChannel(manager);

        await Call("bus.tellManager", new { teamId = team, text = "Split the refund work" });
        // The fake Manager tells Ada and Ben; they hand back; the Manager is woken by the handbacks.
        await WaitFor(() => _terminals.SessionOf(TerminalHub.MemberChannel(IdOf(Agents(team).GetAwaiter().GetResult(), "Ben"))) is { Ended: true });
        await WaitFor(() => _bus.IsQuiescentAsync(TestContext.Current.CancellationToken));
        var first = Text(await Call("terminal.attach", new { channel }));
        Assert.Contains("claude -p (batch of 1", first);
        Assert.Contains("● step 3", first);
        Assert.Contains("run completed", first);
        Assert.Contains("● tell Ada", first);
        Assert.Contains("● tell Ben", first);

        // Reattach (a reopened window): the same bytes come back, and pushes carried them live too.
        var again = Text(await Call("terminal.attach", new { channel }));
        Assert.Equal(first, again);
        string pushed;
        lock (_events)
        {
            pushed = string.Concat(_events.Where(e => e["event"]!.GetValue<string>() == TerminalHub.OutputEvent && e["data"]!["channel"]!.GetValue<string>() == channel)
                .Select(e => Encoding.UTF8.GetString(Convert.FromBase64String(e["data"]!["data"]!.GetValue<string>()))));
        }
        Assert.Equal(first, pushed);

        var card = (await Agents(team)).Single(a => a!["member"]!["id"]!.GetValue<long>() == manager)!;
        Assert.Equal("completed", card["lastRunState"]!.GetValue<string>());
        Assert.Equal("Ada", (await Agents(team))[1]!["member"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Terminal_StopEndsTheRunWithReasonStopped()
    {
        var team = await CreateMixedTeam();
        var ada = IdOf(await Agents(team), "Ada");
        var channel = TerminalHub.MemberChannel(ada);
        _runner.Ticks = 0; // runs until stopped
        await _bus.PersonTellAsync("payments", "Ada", "long job", ct: TestContext.Current.CancellationToken);
        await WaitFor(() => _terminals.SessionOf(channel) is { Ended: false, Kind: "headless" });
        Assert.False((await Call("terminal.banner", new { memberId = ada }))!["interactive"]!.GetValue<bool>()); // a headless run is never ended by the pane's ✕
        Assert.True((await Call("terminal.stop", new { memberId = ada }))!["stopped"]!.GetValue<bool>());
        await WaitFor(async () => (await Agents(team)).Single(a => a!["member"]!["id"]!.GetValue<long>() == ada)!["state"]!.GetValue<string>() == "idle");
        var card = (await Agents(team)).Single(a => a!["member"]!["id"]!.GetValue<long>() == ada)!;
        Assert.Equal("failed", card["lastRunState"]!.GetValue<string>());
        Assert.Equal("stopped: by the person", card["lastRunReason"]!.GetValue<string>());
        Assert.Contains("■ stopped by the person", Text(await Call("terminal.attach", new { channel })));
    }

    [Fact]
    public async Task Interactive_FakeSessionTakesInputAndStops()
    {
        var team = await CreateMixedTeam();
        var ben = IdOf(await Agents(team), "Ben");
        var channel = TerminalHub.MemberChannel(ben);
        var snap = await Call("terminal.openInteractive", new { memberId = ben });
        Assert.True(snap!["session"]!["inputEnabled"]!.GetValue<bool>());
        Assert.Equal("busy", (await Refused("terminal.openInteractive", new { memberId = ben })).Code);
        Assert.True((await Call("terminal.banner", new { memberId = ben }))!["interactive"]!.GetValue<bool>()); // the pane's ✕ may end it
        Assert.True((await Call("terminal.input", new { channel, data = "hello\r" }))!["accepted"]!.GetValue<bool>());
        Assert.Contains("you said \"hello\"", Text(await Call("terminal.attach", new { channel })));
        Assert.Equal("running", (await Refused("members.remove", new { memberId = ben })).Code);
        await Call("terminal.stop", new { memberId = ben });
        // The session holds a bus run slot: Stop cancels it through the bus, which ends the channel.
        await WaitFor(() => _terminals.SessionOf(channel)!.Ended);
        await WaitFor(async () => (await Call("app.overview"))!["slots"]!["running"]!.GetValue<int>() == 0);
        Assert.False((await Call("terminal.input", new { channel, data = "x" }))!["accepted"]!.GetValue<bool>());
        Assert.Equal("bad_params", (await Refused("terminal.attach", new { channel = "../etc" })).Code);
    }

    /// <summary>
    /// The terminal window passes its fitted size when it opens a session, so the CLI's PTY starts at the size the
    /// window draws (a default-sized PTY garbled the Claude and Grok TUIs on Windows); without one, the default.
    /// </summary>
    [Fact]
    public async Task Interactive_StartsAtTheWindowsSize()
    {
        var team = await CreateMixedTeam();
        var agents = await Agents(team);
        var (ada, ben) = (IdOf(agents, "Ada"), IdOf(agents, "Ben"));
        await Call("terminal.openInteractive", new { memberId = ben, cols = 132, rows = 41 });
        Assert.Equal(new PtySize(41, 132), _interactive.LastSize);
        await Call("terminal.stop", new { memberId = ben });
        await WaitFor(() => _terminals.SessionOf(TerminalHub.MemberChannel(ben))!.Ended);

        await Call("terminal.openInteractive", new { memberId = ada });
        Assert.Equal(PtySize.Default, _interactive.LastSize);
        await Call("terminal.stop", new { memberId = ada });
    }

    /// <summary>
    /// The run limit through the screens: an open interactive session takes one of the global run slots, so under a limit
    /// of 1 a member's run waits "Queued · position 1 of 1" behind it, the header and the team list count the
    /// session as running, and Stop on the session gives the slot to the queued run.
    /// </summary>
    [Fact]
    public async Task Interactive_SessionTakesARunSlot_StopGivesItToTheQueue()
    {
        await SaveLimit(1);
        var team = await CreateMixedTeam();
        var agents = await Agents(team);
        var (ada, ben) = (IdOf(agents, "Ada"), IdOf(agents, "Ben"));
        _runner.Ticks = 0; // member runs hold their slot until stopped

        await Call("terminal.openInteractive", new { memberId = ben });
        var slots = (await Call("app.overview"))!["slots"]!;
        Assert.Equal(1, slots["running"]!.GetValue<int>());
        Assert.Equal(1, slots["limit"]!.GetValue<int>());
        Assert.Equal(1, (await Call("teams.list"))!.AsArray().Single()!["running"]!.GetValue<int>());
        Assert.Equal("running", Card(await Agents(team), ben)["state"]!.GetValue<string>());

        await _bus.PersonTellAsync("payments", "Ada", "work", ct: TestContext.Current.CancellationToken);
        // The pump delivers the tell and queues Ada behind the session.
        await WaitFor(async () => Card(await Agents(team), ada)["state"]!.GetValue<string>() != "idle");
        var queued = Card(await Agents(team), ada);
        Assert.Equal("queued", queued["state"]!.GetValue<string>());
        Assert.Equal(1, queued["queuePosition"]!.GetValue<int>());
        Assert.Equal(1, queued["queueLength"]!.GetValue<int>());
        Assert.Equal(1, (await Call("app.overview"))!["slots"]!["queued"]!.GetValue<int>());

        Assert.True((await Call("terminal.stop", new { memberId = ben }))!["stopped"]!.GetValue<bool>());
        await WaitFor(async () => Card(await Agents(team), ada)["state"]!.GetValue<string>() == "running");
        Assert.Equal("idle", Card(await Agents(team), ben)["state"]!.GetValue<string>());
        Assert.Equal(1, (await Call("app.overview"))!["slots"]!["running"]!.GetValue<int>());
        await Call("terminal.stop", new { memberId = ada });
    }

    /// <summary>
    /// A session opened while every slot is taken waits in the same FIFO queue as runs (no jump), and
    /// raising app.maxConcurrentAgents in the policy screen admits it at once.
    /// </summary>
    [Fact]
    public async Task Interactive_WaitsInTheOneQueue_RaisingTheLimitAdmitsAtOnce()
    {
        await SaveLimit(1);
        var team = await CreateMixedTeam();
        var agents = await Agents(team);
        var (ada, ben) = (IdOf(agents, "Ada"), IdOf(agents, "Ben"));
        _runner.Ticks = 0;

        await _bus.PersonTellAsync("payments", "Ada", "work", ct: TestContext.Current.CancellationToken);
        await WaitFor(async () => Card(await Agents(team), ada)["state"]!.GetValue<string>() == "running");
        await Call("terminal.openInteractive", new { memberId = ben });
        var benCard = Card(await Agents(team), ben);
        Assert.Equal("queued", benCard["state"]!.GetValue<string>());
        Assert.Equal(1, benCard["queuePosition"]!.GetValue<int>());
        Assert.Contains("waiting for a run slot (position 1)", Text(await Call("terminal.attach", new { channel = TerminalHub.MemberChannel(ben) })));

        await SaveLimit(2);
        await WaitFor(() => _terminals.SessionOf(TerminalHub.MemberChannel(ben)) is { Ended: false, Kind: "interactive" });
        Assert.Equal(2, (await Call("app.overview"))!["slots"]!["running"]!.GetValue<int>());
        await Call("terminal.stop", new { memberId = ben });
        await Call("terminal.stop", new { memberId = ada });
    }

    private async Task SaveLimit(int limit)
    {
        var global = (await Call("policy.getGlobal"))!.AsObject();
        global["app"]!["maxConcurrentAgents"] = limit;
        await Call("policy.saveGlobal", new { policy = global });
    }

    private static JsonNode Card(JsonArray agents, long memberId) =>
        agents.Single(a => a!["member"]!["id"]!.GetValue<long>() == memberId)!;

    [Fact]
    public void ReplayBuffer_KeepsTheNewestBytes()
    {
        var buffer = new ReplayBuffer(8);
        buffer.Append("abcdef"u8);
        buffer.Append("ghij"u8);
        Assert.Equal("cdefghij", Encoding.ASCII.GetString(buffer.ToArray()));
        buffer.Append("0123456789"u8);
        Assert.Equal("23456789", Encoding.ASCII.GetString(buffer.ToArray()));
    }

    private static string Text(JsonNode? attach) => Encoding.UTF8.GetString(Convert.FromBase64String(attach!["replay"]!.GetValue<string>()));

    private static Task WaitFor(Func<bool> condition) => WaitFor(() => Task.FromResult(condition()));

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met in 10 s");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
