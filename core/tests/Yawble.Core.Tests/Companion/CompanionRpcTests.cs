using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Companion;

/// <summary>
/// Companion task 5: the window's companion.* methods (the person at this computer's window only), refused over the Cloud
/// Link, unreachable from the Concierge's own route; pending companion requests in Needs You; and the core's companion host
/// (a populated or unwritable YAWBLE_HOME leaves the core running without the endpoint, and says why). Every home is a temp
/// folder; the app's calls are fakes; no program is run.
/// </summary>
public class CompanionRpcTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDb _db = new();
    private readonly string _home = Directory.CreateTempSubdirectory("yawble-yhome-").FullName;

    public void Dispose()
    {
        _db.Dispose();
        try { TestFiles.DeleteTree(_home); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly string[] Methods = ["companion.list", "companion.status", "companion.decidePairing", "companion.decideAction", "companion.revoke", "companion.actionStatus"];

    private sealed class Rig : IAsyncDisposable
    {
        public required BusEngine Bus;
        public required CompanionService Service;
        public required CompanionRpc Rpc;
        public required RpcDispatcher Dispatcher;
        public List<string> Routed { get; } = [];
        public Func<string, Task>? OnRoute;

        public async Task<JsonNode?> RouteAsync(string method, JsonObject? p, CancellationToken ct)
        {
            lock (Routed) Routed.Add(method);
            if (OnRoute is { } hook) await hook(method);
            return method switch
            {
                "ops.backups" => JsonNode.Parse("""[{"name":"daily-20261110.db","size":4096,"reason":"daily","time":"2026-11-10T03:00:00Z"}]"""),
                "ops.restore" => BackupRpc.RestoreRefusal(Actor.Current) is { } refusal
                    ? throw new RpcException("forbidden", refusal)
                    : new JsonObject { ["staged"] = new JsonObject { ["name"] = p!["backup"]!.GetValue<string>() }, ["restarting"] = true },
                _ => new JsonObject(),
            };
        }

        public async Task<JsonNode?> As(Actor actor, string method, JsonObject? p = null) =>
            await Dispatcher.DispatchAsAsync(actor, method, p, Ct);

        public Task<JsonNode?> Window(string method, JsonObject? p = null) => As(Actor.Window, method, p);

        /// <summary>Pairs a client through the service and the window's method; returns its plain key.</summary>
        public async Task<string> PairAsync(string name, CompanionLevel level)
        {
            var ticket = Service.BeginPairing(name, level);
            var waiting = Service.WaitPairingAsync(ticket.Id, Ct);
            await Window("companion.decidePairing", new JsonObject { ["id"] = ticket.Id, ["approve"] = true });
            var outcome = await waiting;
            Assert.True(outcome.Approved);
            return outcome.Key!;
        }

        public async ValueTask DisposeAsync() => await Bus.DisposeAsync();
    }

    private async Task<Rig> MakeRigAsync()
    {
        var bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        await bus.StartAsync(Ct);
        await bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        Rig rig = null!;
        var service = new CompanionService(bus, new ConciergeStore(_db.ConnectionString), (m, p, ct) => rig.RouteAsync(m, p, ct),
            (_, _) => null, new CompanionClientStore(_db.ConnectionString), new AuditLog(_db.ConnectionString), new AppEvents());
        var rpc = new CompanionRpc(service, () => new JsonObject { ["endpoint"] = "test-endpoint", ["problem"] = null });
        rig = new Rig
        {
            Bus = bus, Service = service, Rpc = rpc,
            Dispatcher = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], audit: new AuditLog(_db.ConnectionString), companion: rpc),
        };
        return rig;
    }

    // ---------------------------------------------------------------- window only

    [Fact]
    public async Task CompanionMethodsAreWindowOnly()
    {
        await using var rig = await MakeRigAsync();
        var others = new[]
        {
            Actor.CloudViewer("viewer@example.test"),
            Actor.ExternalClient("cc"),
            new Actor(Actor.Concierge, Actor.LocalUser),
            new Actor(Actor.Trigger, null),
            new Actor(Actor.Agent, "alpha/Manager"),
            new Actor(Actor.Host, Actor.LocalUser),
            Actor.ExternalClient("cc") with { ApprovedBy = Actor.LocalUser },
        };
        var ticket = rig.Service.BeginPairing("cc", CompanionLevel.Read);
        foreach (var actor in others)
            foreach (var method in Methods)
            {
                var p = new JsonObject { ["id"] = ticket.Id, ["approve"] = true, ["name"] = "cc" };
                var e = await Assert.ThrowsAsync<RpcException>(() => rig.As(actor, method, p));
                Assert.Equal("forbidden", e.Code);
                Assert.Equal("the yawble companion is managed in the app's window on this computer", e.Message);
            }
        // No actor at all (a call that entered nowhere) is refused too.
        var none = await Assert.ThrowsAsync<RpcException>(() => rig.Dispatcher.DispatchAsync("companion.list", null, Ct));
        Assert.Equal("forbidden", none.Code);

        // Nothing was decided by any of them: the pairing still waits.
        var list = (await rig.Window("companion.list"))!.AsObject();
        Assert.Single(list["pairings"]!.AsArray());
        Assert.Empty(list["clients"]!.AsArray());
        Assert.Equal("test-endpoint", (await rig.Window("companion.status"))!["endpoint"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListNeverCarriesAKeyOrHash()
    {
        await using var rig = await MakeRigAsync();
        var key = await rig.PairAsync("cc", CompanionLevel.Destructive);
        var client = rig.Service.Authenticate("cc", key)!;
        var asked = await rig.Service.CallAsync(client, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct);
        Assert.NotNull(asked.RequestId);
        rig.Service.BeginPairing("other", CompanionLevel.Read);

        var text = (await rig.Window("companion.list"))!.ToJsonString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        Assert.DoesNotContain(key, text);
        Assert.DoesNotContain(Convert.ToHexStringLower(hash), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex("[0-9a-fA-F]{32,}"), text); // no hash or key-shaped hex of any kind
        Assert.DoesNotContain("key", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"cc\"", text);
        Assert.Contains(asked.RequestId!, text);
        Assert.Contains("\"state\":\"pending\"", text);
    }

    [Fact]
    public async Task RevokeThenAuthFails()
    {
        await using var rig = await MakeRigAsync();
        var key = await rig.PairAsync("cc", CompanionLevel.Read);
        Assert.NotNull(rig.Service.Authenticate("cc", key));

        var r = await rig.Window("companion.revoke", new JsonObject { ["name"] = "cc" });
        Assert.True(r!["revoked"]!.GetValue<bool>());
        Assert.Null(rig.Service.Authenticate("cc", key));
        Assert.False((await rig.Window("companion.revoke", new JsonObject { ["name"] = "cc" }))!["revoked"]!.GetValue<bool>());
        var listed = (await rig.Window("companion.list"))!["clients"]!.AsArray().Single()!;
        Assert.NotNull(listed["revokedAt"]?.GetValue<string>());
    }

    [Fact]
    public async Task DecidePairingMayLowerTheLevelButNeverRaiseIt()
    {
        await using var rig = await MakeRigAsync();
        var ticket = rig.Service.BeginPairing("cc", CompanionLevel.Act);
        var raise = await Assert.ThrowsAsync<RpcException>(() => rig.Window("companion.decidePairing",
            new JsonObject { ["id"] = ticket.Id, ["approve"] = true, ["level"] = "destructive" }));
        Assert.Equal("bad_params", raise.Code);
        var bad = await Assert.ThrowsAsync<RpcException>(() => rig.Window("companion.decidePairing",
            new JsonObject { ["id"] = ticket.Id, ["approve"] = true, ["level"] = "root" }));
        Assert.Equal("bad_params", bad.Code);
        var noApprove = await Assert.ThrowsAsync<RpcException>(() => rig.Window("companion.decidePairing", new JsonObject { ["id"] = ticket.Id }));
        Assert.Equal("bad_params", noApprove.Code);

        var waiting = rig.Service.WaitPairingAsync(ticket.Id, Ct);
        await rig.Window("companion.decidePairing", new JsonObject { ["id"] = ticket.Id, ["approve"] = true, ["level"] = "read" });
        var outcome = await waiting;
        Assert.Equal(CompanionLevel.Read, outcome.Level);
        Assert.Equal("read", (await rig.Window("companion.list"))!["clients"]!.AsArray().Single()!["level"]!.GetValue<string>());
    }

    [Fact]
    public async Task DecideActionRunsTheRequestAndReportsRunningWhileItIsUnderWay()
    {
        await using var rig = await MakeRigAsync();
        var key = await rig.PairAsync("cc", CompanionLevel.Destructive);
        var client = rig.Service.Authenticate("cc", key)!;
        var asked = await rig.Service.CallAsync(client, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct);
        var id = asked.RequestId!;

        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        rig.OnRoute = async m =>
        {
            if (m != "ops.restore") return;
            entered.TrySetResult();
            await release.Task;
        };
        // The window's connection serves one call at a time: Approve answers "running" at once, the run goes on by itself.
        var first = (await rig.Window("companion.decideAction", new JsonObject { ["id"] = id, ["approve"] = true }).WaitAsync(TimeSpan.FromSeconds(10), Ct))!;
        Assert.Equal("running", first["state"]!.GetValue<string>());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // A second Approve while it runs runs nothing, and says it is running.
        var second = (await rig.Window("companion.decideAction", new JsonObject { ["id"] = id, ["approve"] = true }))!;
        Assert.Equal("running", second["state"]!.GetValue<string>());
        Assert.Equal(id, second["id"]!.GetValue<string>());
        var listed = (await rig.Window("companion.list"))!["actions"]!.AsArray().Single()!;
        Assert.Equal("running", listed["state"]!.GetValue<string>());
        Assert.Equal(0, rig.Service.PendingCount);

        release.SetResult();
        var done = (await rig.Service.WaitAsync(client, id, TimeSpan.FromSeconds(10), Ct))!;
        Assert.Equal(ActionState.Done, done.State);
        Assert.Contains("staged", done.Result);
        Assert.Contains("plan", first.AsObject().Select(kv => kv.Key));
        Assert.Empty((await rig.Window("companion.list"))!["actions"]!.AsArray());
        Assert.Single(rig.Routed, m => m == "ops.restore");

        var denied = await rig.Service.CallAsync(client, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct);
        var d = (await rig.Window("companion.decideAction", new JsonObject { ["id"] = denied.RequestId, ["approve"] = false }))!;
        Assert.Equal("denied", d["state"]!.GetValue<string>());
        Assert.Single(rig.Routed, m => m == "ops.restore");
    }

    // ---------------------------------------------------------------- the Cloud Link and the Concierge cannot reach it

    [Fact]
    public void RemoteAccessRefusesEveryCompanionMethod()
    {
        foreach (var method in Methods.Concat(["companion.", "companion.anything", "companion.pair"]))
            foreach (var terminals in new[] { false, true })
                Assert.Equal("the yawble companion is paired, approved and revoked on the computer itself",
                    RemoteAccess.Refusal(method, new JsonObject { ["id"] = "p1", ["approve"] = true }, terminals, _ => true, _ => false));
    }

    [Fact]
    public void TheCloudLinkNeverPushesTheCompanionsEvents()
    {
        Assert.False(CloudLink.Forwards(CompanionService.ChangedEvent));
        Assert.False(CloudLink.Forwards("companion.anything"));
        Assert.True(CloudLink.Forwards("terminal.output"));
        Assert.True(CloudLink.Forwards("app.changed"));
    }

    [Fact]
    public async Task TheConciergeRouteCannotReachCompanionMethods()
    {
        // The window's operations as a fake that would answer anything: the route itself must not forward companion.*.
        var reached = new List<string>();
        var route = ConciergeService.Route((m, _, _) => { reached.Add(m); return Task.FromResult<JsonNode?>(new JsonObject()); },
            () => (m, _, _) => { reached.Add("board:" + m); return Task.FromResult<JsonNode?>(new JsonObject()); });
        foreach (var method in Methods)
        {
            using var acting = Actor.Use(Actor.Window); // even a turn the person started at the window
            var e = await Assert.ThrowsAsync<RpcException>(() => route(method, new JsonObject { ["name"] = "cc" }, Ct));
            Assert.Equal("forbidden", e.Code);
        }
        Assert.Empty(reached);
        // The window's own UiRpc does not serve them either.
        Assert.All(Methods, m => Assert.False(UiRpc.Handles(m)));

        // The companion's route adds the backups (list and an approved restore) to the Concierge's, and nothing else.
        var backups = new List<string>();
        var companionRoute = CompanionHost.Route(route, (m, _, _) => { backups.Add(m); return Task.FromResult<JsonNode?>(new JsonArray()); });
        await companionRoute("ops.backups", null, Ct);
        await companionRoute("ops.restore", new JsonObject { ["backup"] = "x" }, Ct);
        await companionRoute("teams.list", null, Ct);
        Assert.Equal(["ops.backups", "ops.restore"], backups);
        Assert.Equal(["teams.list"], reached);
        foreach (var method in Methods)
            Assert.Equal("forbidden", (await Assert.ThrowsAsync<RpcException>(() => companionRoute(method, null, Ct))).Code);
        Assert.Equal(["teams.list"], reached);
    }

    // ---------------------------------------------------------------- Needs You

    [Fact]
    public async Task NeedsYouCountsCompanionRequests()
    {
        await using var rig = await MakeRigAsync();
        var terminals = new TerminalHub(new AppEvents());
        var dataDir = Path.Combine(_db.Dir, "data");
        var ui = new UiRpc(rig.Bus, new PolicyStore(_db.ConnectionString), new MemberLayouts(dataDir, _db.Dir, _db.Dir), new FakeModelCredentials(),
            new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Bus), terminals, 16_384);
        var doctorFails = 2;
        ui.ExtraNeedsYou = CompanionHost.NeedsYou(() => doctorFails, rig.Service);
        async Task<int> NeedsYouAs(Actor actor)
        {
            using (Actor.Use(actor)) return (await ui.DispatchAsync("app.overview", null, Ct))!["needsYou"]!.GetValue<int>();
        }
        Task<int> NeedsYou() => NeedsYouAs(Actor.Window);

        var baseline = await NeedsYou();
        Assert.True(baseline >= doctorFails);

        var key = await rig.PairAsync("cc", CompanionLevel.Destructive);
        Assert.Equal(baseline, await NeedsYou()); // decided: waits for nobody
        var ticket = rig.Service.BeginPairing("reader", CompanionLevel.Read);
        Assert.Equal(baseline + 1, await NeedsYou());

        var client = rig.Service.Authenticate("cc", key)!;
        var asked = await rig.Service.CallAsync(client, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct);
        Assert.Equal(baseline + 2, await NeedsYou());
        // Nothing companion reaches the Cloud Link, not even a count: a remote viewer's overview leaves both out.
        Assert.Equal(baseline, await NeedsYouAs(Actor.CloudViewer("viewer@example.test")));

        await rig.Window("companion.decideAction", new JsonObject { ["id"] = asked.RequestId, ["approve"] = false });
        await rig.Window("companion.decidePairing", new JsonObject { ["id"] = ticket.Id, ["approve"] = false });
        Assert.Equal(baseline, await NeedsYou());
    }

    // ---------------------------------------------------------------- the core's companion host

    private CompanionService ServiceFor(BusEngine bus) =>
        new(bus, new ConciergeStore(_db.ConnectionString), (_, _, _) => Task.FromResult<JsonNode?>(null),
            (_, _) => null, new CompanionClientStore(_db.ConnectionString), new AuditLog(_db.ConnectionString), new AppEvents());

    [Fact]
    public async Task TheHostWritesControlJsonAndRemovesItOnDispose()
    {
        await using var rig = await MakeRigAsync();
        var lines = new List<string>();
        var bin = Path.Combine(_db.Dir, "install", "bin", OperatingSystem.IsWindows() ? "yawble.exe" : "yawble");
        string address;
        await using (var host = CompanionHost.Start(_home, _db.Dir, ServiceFor(rig.Bus), bin, lines.Add))
        {
            Assert.Null(host.Problem);
            address = host.Address!;
            var file = ControlDiscovery.Read(_home);
            Assert.NotNull(file);
            Assert.Equal(address, file.Address);
            Assert.Equal(Environment.ProcessId, file.Pid);
            Assert.True(UserOnlyFile.IsUserOnly(ControlDiscovery.FilePath(_home)));
            var status = host.Status();
            Assert.Equal(address, status["endpoint"]!.GetValue<string>());
            Assert.Equal(ControlDiscovery.FilePath(_home), status["controlFile"]!.GetValue<string>());
            Assert.Equal(bin, status["binaryPath"]!.GetValue<string>());
            Assert.Equal($"claude mcp add --scope user yawble -- \"{bin}\" mcp", status["claudeCommand"]!.GetValue<string>());
            Assert.Null(status["problem"]);
        }
        Assert.Null(ControlDiscovery.Read(_home));
        Assert.False(File.Exists(ControlDiscovery.FilePath(_home)));
    }

    [Fact]
    public async Task APopulatedHomeLeavesTheCoreRunningWithoutTheEndpointAndSaysWhy()
    {
        await using var rig = await MakeRigAsync();
        var populated = Path.Combine(_home, "profile");
        Directory.CreateDirectory(populated);
        File.WriteAllText(Path.Combine(populated, "notes.txt"), "the person's own file");
        var lines = new List<string>();

        await using var host = CompanionHost.Start(populated, _db.Dir, ServiceFor(rig.Bus), null, lines.Add);
        Assert.Null(host.Address);
        Assert.NotNull(host.Problem);
        Assert.Contains("YAWBLE_HOME", host.Problem);
        Assert.False(File.Exists(Path.Combine(populated, ControlDiscovery.FileName)));
        Assert.Equal("the person's own file", File.ReadAllText(Path.Combine(populated, "notes.txt")));
        var status = host.Status();
        Assert.Null(status["endpoint"]);
        Assert.Equal(host.Problem, status["problem"]!.GetValue<string>());
        var line = Assert.Single(lines);
        Assert.StartsWith("companion: ", line);
    }

    [Fact]
    public async Task AnUnwritableHomeLeavesTheCoreRunningWithoutTheEndpoint()
    {
        await using var rig = await MakeRigAsync();
        // A file where the home folder should be: creating the folder fails (IOException), never the core.
        var blocked = Path.Combine(_home, "blocked");
        File.WriteAllText(blocked, "x");
        await using var host = CompanionHost.Start(Path.Combine(blocked, "yawble"), _db.Dir, ServiceFor(rig.Bus), null, _ => { });
        Assert.Null(host.Address);
        Assert.NotNull(host.Problem);
    }

    [Fact]
    public async Task AMalformedYawbleHomeLeavesTheCoreRunningWithoutTheEndpoint()
    {
        await using var rig = await MakeRigAsync();
        var lines = new List<string>();
        static string? Env(string n) => n == ControlDiscovery.HomeVariable ? "bad\0home" : null;
        await using var host = CompanionHost.Start(Env, _db.Dir, ServiceFor(rig.Bus), null, lines.Add);
        Assert.Null(host.Address);
        Assert.NotNull(host.Problem);
        Assert.Contains(ControlDiscovery.HomeVariable, host.Problem);
        var status = host.Status();
        Assert.Null(status["endpoint"]);
        Assert.Null(status["controlFile"]);
        Assert.Equal(host.Problem, status["problem"]!.GetValue<string>());
        Assert.StartsWith("companion: ", Assert.Single(lines));

        var off = CompanionHost.Off(Env, null, "off");
        Assert.Null(off.Status()["controlFile"]);
    }

    [Fact]
    public async Task TheHostResolvesTheHomeFromYawbleHome()
    {
        await using var rig = await MakeRigAsync();
        var home = Path.Combine(_home, "resolved");
        await using (var host = CompanionHost.Start(n => n == ControlDiscovery.HomeVariable ? home : null, _db.Dir, ServiceFor(rig.Bus), null, _ => { }))
        {
            Assert.Null(host.Problem);
            Assert.Equal(host.Address, ControlDiscovery.Read(home)!.Address);
        }
        Assert.Null(ControlDiscovery.Read(home));
    }

    /// <summary>Every test that starts the real core isolates the companion's home (never the real %APPDATA%).</summary>
    [Fact]
    public void EveryRealCoreLaunchInTheTestsIsolatesTheCompanionHome()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        Assert.True(File.Exists(Path.Combine(root, "Yawble.Core.Tests.csproj")), root);
        var launching = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("\"yawble-core.dll\"", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(launching);
        foreach (var file in launching)
        {
            var text = File.ReadAllText(file);
            var launches = text.Split("\"yawble-core.dll\"").Length - 1;
            var isolated = text.Split("CoreLaunch.IsolateCompanionHome(").Length - 1;
            Assert.True(isolated >= launches, $"{Path.GetFileName(file)}: {launches} real-core launch(es), {isolated} CoreLaunch.IsolateCompanionHome call(s)");
        }
    }

    [Fact]
    public void OffHostSaysItIsOff()
    {
        var host = CompanionHost.Off(_home, null, "turned off for this launch (--no-control)");
        var status = host.Status();
        Assert.Null(status["endpoint"]);
        Assert.Null(status["claudeCommand"]);
        Assert.Equal("turned off for this launch (--no-control)", status["problem"]!.GetValue<string>());
    }

    [Fact]
    public void NoControlOptionTurnsTheEndpointOff()
    {
        static string? Env(string n) => n == "YAWBLE_IPC_TOKEN" ? new string('a', 40) : null;
        Assert.True(CoreOptions.Parse(["--ipc", "x", "--data-dir", "d"], Env).Control);
        var off = CoreOptions.Parse(["--ipc", "x", "--data-dir", "d", "--no-control"], Env);
        Assert.Null(off.Error);
        Assert.False(off.Control);
    }

    [Fact]
    public void TheClientBinaryLivesInTheInstallsBinFolder()
    {
        var install = Path.Combine(_db.Dir, "install");
        Assert.Equal(Path.Combine(install, "bin", OperatingSystem.IsWindows() ? "yawble.exe" : "yawble"), CompanionHost.BinaryPathFor(install));
        Assert.Null(CompanionHost.BinaryPathFor(null));
    }

    [Fact]
    public async Task ActionStatusReadsAPendingRequestAndRunsNothing()
    {
        await using var rig = await MakeRigAsync();
        var key = await rig.PairAsync("cc", CompanionLevel.Destructive);
        var client = rig.Service.Authenticate("cc", key)!;
        var asked = await rig.Service.CallAsync(client, "backup_restore", new JsonObject { ["backup"] = "daily-20261110.db" }, Ct);
        var s = (await rig.Window("companion.actionStatus", new JsonObject { ["id"] = asked.RequestId }))!;
        Assert.Equal("pending", s["state"]!.GetValue<string>());
        Assert.Equal(asked.RequestId, s["id"]!.GetValue<string>());
        Assert.Contains("daily-20261110.db", s["plan"]!.GetValue<string>());
        Assert.Equal("pending", (await rig.Window("companion.list"))!["actions"]!.AsArray().Single()!["state"]!.GetValue<string>());
        Assert.DoesNotContain("ops.restore", rig.Routed);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => rig.Window("companion.actionStatus", new JsonObject { ["id"] = "nope" }))).Code);
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => rig.Window("companion.actionStatus", new JsonObject()))).Code);
    }
}
