using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Companion;

/// <summary>
/// Companion task 4: the control endpoint (its own pipe or socket on a temp address), per-client key auth, the tool-level
/// methods only; and the user-only control.json discovery file under a temp YAWBLE_HOME (never the real %APPDATA%\yawble-companion).
/// The app's calls are a fake route; no program is run.
/// </summary>
public class CompanionServerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string ShellToken = "0123456789abcdef0123456789abcdef-test";
    private readonly TempDb _db = new();
    private readonly string _home = Directory.CreateTempSubdirectory("yawble-yhome-").FullName;

    public void Dispose()
    {
        _db.Dispose();
        try { TestFiles.DeleteTree(_home); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- rig

    private sealed class Rig : IAsyncDisposable
    {
        public required CompanionService Service;
        public required CompanionClientStore Clients;
        public required CompanionServer Server;
        public required BusEngine Bus;
        public List<string> Routed { get; } = [];
        public Func<string, Exception?> Throw = _ => null;

        public async Task<JsonNode?> RouteAsync(string method, JsonObject? p, CancellationToken ct)
        {
            lock (Routed) Routed.Add(method);
            if (Throw(method) is { } e) throw e;
            await Task.Yield();
            return method switch
            {
                "ops.backups" => JsonNode.Parse("""[{"name":"daily-20261110.db","size":4096,"reason":"daily","time":"2026-11-10T03:00:00Z"}]"""),
                "ops.restore" => BackupRpc.RestoreRefusal(Actor.Current) is { } refusal
                    ? throw new RpcException("forbidden", refusal)
                    : new JsonObject { ["staged"] = new JsonObject { ["name"] = p!["backup"]!.GetValue<string>() }, ["restarting"] = true },
                _ => new JsonObject(),
            };
        }

        public string Pair(string name, CompanionLevel level)
        {
            Clients.Add(name, level, "tester", out var key);
            return key;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Bus.DisposeAsync();
        }
    }

    private async Task<Rig> StartAsync(CompanionServer.Limits? limits = null)
    {
        var bus = new BusEngine(_db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        await bus.StartAsync(Ct);
        await bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        var clients = new CompanionClientStore(_db.ConnectionString);
        Rig rig = null!;
        var service = new CompanionService(bus, new ConciergeStore(_db.ConnectionString), (m, p, ct) => rig.RouteAsync(m, p, ct),
            (_, _) => null, clients, new AuditLog(_db.ConnectionString), new AppEvents());
        var endpoint = IpcEndpoint.Create(CompanionServer.NewAddress(Path.GetTempPath()));
        var server = limits is { } l ? CompanionServer.Start(endpoint, service, l) : CompanionServer.Start(endpoint, service);
        rig = new Rig { Service = service, Clients = clients, Server = server, Bus = bus };
        return rig;
    }

    private static Task<TestIpcClient> ConnectAsync(Rig rig) => TestIpcClient.ConnectAsync(rig.Server.Address, Ct);

    private static string? Code(JsonObject? reply) => reply?["error"]?["code"]?.GetValue<string>();

    private static async Task<TestIpcClient> AuthedAsync(Rig rig, string name, string key)
    {
        var client = await ConnectAsync(rig);
        var ok = await client.CallAsync(CompanionProtocol.Auth, new JsonObject { ["client"] = name, ["key"] = key }, Ct);
        Assert.True(ok!["result"]!["authenticated"]!.GetValue<bool>(), ok.ToJsonString());
        return client;
    }

    // ---------------------------------------------------------------- the endpoint

    [Fact]
    public void TheAddressIsItsOwn_RandomPerLaunch()
    {
        var a = CompanionServer.NewAddress(Path.GetTempPath());
        var b = CompanionServer.NewAddress(Path.GetTempPath());
        Assert.NotEqual(a, b);
        if (OperatingSystem.IsWindows()) Assert.Matches(@"^\\\\\.\\pipe\\yawble-ctl-[0-9a-f]{16}$", a);
        else Assert.Matches("yawble-ctl-[0-9a-f]{16}\\.sock$", a);
    }

    [Fact]
    public async Task RefusesAnyMethodBeforeAuthExceptHelloAndPairing()
    {
        await using var rig = await StartAsync();

        // hello answers, and the connection stays open for auth or pairing
        await using (var client = await ConnectAsync(rig))
        {
            var hello = (await client.CallAsync(CompanionProtocol.Hello, null, Ct))!["result"]!;
            Assert.Equal("yawble", hello["product"]!.GetValue<string>());
            Assert.Equal(CoreInfo.Version, hello["version"]!.GetValue<string>());
            Assert.Equal(CompanionProtocol.Version, hello["protocol"]!.GetValue<int>());
            Assert.Equal("unauthorized", Code(await client.CallAsync(CompanionProtocol.Tools, null, Ct)));
            Assert.True(await client.IsClosedAsync(Ct));
        }

        foreach (var method in new[] { CompanionProtocol.WhoAmI, CompanionProtocol.Tools, CompanionProtocol.Call, CompanionProtocol.Request, "core.getInfo", "teams.delete", "nope" })
        {
            await using var client = await ConnectAsync(rig);
            var denied = await client.CallAsync(method, new JsonObject { ["tool"] = "team_list", ["args"] = new JsonObject() }, Ct);
            Assert.Equal("unauthorized", Code(denied));
            Assert.True(await client.IsClosedAsync(Ct), method + " left the connection open");
        }

        // not even a JSON object: closed without an answer
        await using (var client = await ConnectAsync(rig))
        {
            await client.WriteRawAsync(FrameKind.Json, Encoding.UTF8.GetBytes("[1,2"), Ct);
            Assert.True(await client.IsClosedAsync(Ct));
        }
        Assert.Empty(rig.Routed);
    }

    [Fact]
    public async Task TheShellsLaunchTokenIsNotAKeyHere()
    {
        await using var rig = await StartAsync();
        rig.Pair("cc", CompanionLevel.Destructive);

        await using (var client = await ConnectAsync(rig))
        {
            Assert.Equal("unauthorized", Code(await client.AuthAsync(ShellToken, Ct))); // the shell's {token} form
            Assert.True(await client.IsClosedAsync(Ct));
        }
        await using (var client = await ConnectAsync(rig))
        {
            var denied = await client.CallAsync(CompanionProtocol.Auth, new JsonObject { ["client"] = "cc", ["key"] = ShellToken, ["token"] = ShellToken }, Ct);
            Assert.Equal("unauthorized", Code(denied));
            Assert.True(await client.IsClosedAsync(Ct));
        }
    }

    [Fact]
    public async Task AnOversizedFirstFrameClosesTheConnection()
    {
        await using var rig = await StartAsync();
        var key = rig.Pair("cc", CompanionLevel.Read);

        // a valid auth padded past 4 KB: never parsed, never answered
        var big = new JsonObject { ["id"] = 1, ["method"] = CompanionProtocol.Auth, ["params"] = new JsonObject { ["client"] = "cc", ["key"] = key, ["pad"] = new string('x', CompanionProtocol.MaxUnauthenticatedFrame) } };
        await using (var client = await ConnectAsync(rig))
        {
            // The server reads the header, sees the length and closes: it may do so before the body is written, so a
            // broken pipe on the write is the same outcome as reading the close afterwards.
            var brokeOnWrite = false;
            try { await client.WriteRawAsync(FrameKind.Json, Encoding.UTF8.GetBytes(big.ToJsonString()), Ct); }
            catch (IOException) { brokeOnWrite = true; }
            Assert.True(brokeOnWrite || await client.IsClosedAsync(Ct));
        }

        // exactly at the limit is still read (and answered)
        var hello = new JsonObject { ["id"] = 1, ["method"] = CompanionProtocol.Hello, ["params"] = new JsonObject { ["pad"] = "" } };
        var pad = CompanionProtocol.MaxUnauthenticatedFrame - Encoding.UTF8.GetByteCount(hello.ToJsonString());
        hello["params"]!["pad"] = new string('x', pad);
        var bytes = Encoding.UTF8.GetBytes(hello.ToJsonString());
        Assert.Equal(CompanionProtocol.MaxUnauthenticatedFrame, bytes.Length);
        await using (var client = await ConnectAsync(rig))
        {
            await client.WriteRawAsync(FrameKind.Json, bytes, Ct);
            Assert.NotNull((await client.ReadReplyAsync(1, Ct))!["result"]);
        }

        // a PTY frame is not this protocol
        await using (var client = await ConnectAsync(rig))
        {
            await client.WriteRawAsync(FrameKind.Pty, [0, 0, 0, 1], Ct);
            Assert.True(await client.IsClosedAsync(Ct));
        }
    }

    [Fact]
    public async Task AnIdleConnectionBeforeAuthIsClosed()
    {
        await using var rig = await StartAsync(CompanionServer.Limits.Default with { PreAuthIdle = TimeSpan.FromMilliseconds(300) });
        await using var client = await ConnectAsync(rig);
        var watch = Stopwatch.StartNew();
        Assert.True(await client.IsClosedAsync(Ct));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task AuthWithAPairedKeyListsToolsForItsLevel_AWrongKeyCloses()
    {
        await using var rig = await StartAsync();
        var key = rig.Pair("reader", CompanionLevel.Read);

        await using (var client = await AuthedAsync(rig, "reader", key))
        {
            var who = (await client.CallAsync(CompanionProtocol.WhoAmI, null, Ct))!["result"]!;
            Assert.Equal("reader", who["client"]!.GetValue<string>());
            Assert.Equal("read", who["level"]!.GetValue<string>());

            var tools = (await client.CallAsync(CompanionProtocol.Tools, null, Ct))!["result"]!["tools"]!.AsArray();
            Assert.Equal(CompanionTools.Read.Order().ToList(), tools.Select(t => t!["name"]!.GetValue<string>()).Order().ToList());

            var list = (await client.CallAsync(CompanionProtocol.Call, new JsonObject { ["tool"] = "team_list", ["args"] = new JsonObject() }, Ct))!["result"]!;
            Assert.False(list["isError"]!.GetValue<bool>(), list.ToJsonString());
            Assert.Contains("alpha", list["text"]!.GetValue<string>());

            var refused = (await client.CallAsync(CompanionProtocol.Call, new JsonObject { ["tool"] = "tell", ["args"] = new JsonObject { ["team"] = "alpha", ["text"] = "hi" } }, Ct))!["result"]!;
            Assert.True(refused["isError"]!.GetValue<bool>());
            Assert.Contains("needs the act level", refused["text"]!.GetValue<string>());

            Assert.Equal("bad_params", Code(await client.CallAsync(CompanionProtocol.Call, new JsonObject { ["args"] = new JsonObject() }, Ct)));
        }

        foreach (var (name, presented) in new[] { ("reader", key + "x"), ("reader", ""), ("nobody", key), ("READER", key) })
        {
            await using var client = await ConnectAsync(rig);
            Assert.Equal("unauthorized", Code(await client.CallAsync(CompanionProtocol.Auth, new JsonObject { ["client"] = name, ["key"] = presented }, Ct)));
            Assert.True(await client.IsClosedAsync(Ct));
        }
    }

    [Fact]
    public async Task DispatcherMethodsAreNotReachable()
    {
        await using var rig = await StartAsync();
        var key = rig.Pair("cc", CompanionLevel.Destructive);
        await using var client = await AuthedAsync(rig, "cc", key);

        foreach (var method in new[] { "core.getInfo", "teams.delete", "ops.restore", "ops.backups", "companion.list", "companion.decideAction", "auth", "hello", CompanionProtocol.PairRequest })
            Assert.Equal("unknown_method", Code(await client.CallAsync(method, new JsonObject { ["name"] = "alpha", ["backup"] = "daily-20261110.db", ["confirm"] = true }, Ct)));
        // a dispatcher method name as a tool is not a tool either
        var asTool = (await client.CallAsync(CompanionProtocol.Call, new JsonObject { ["tool"] = "ops.restore", ["args"] = new JsonObject { ["backup"] = "daily-20261110.db" } }, Ct))!["result"]!;
        Assert.True(asTool["isError"]!.GetValue<bool>());

        Assert.Empty(rig.Routed);
        // and the connection is still served
        Assert.Equal("cc", (await client.CallAsync(CompanionProtocol.WhoAmI, null, Ct))!["result"]!["client"]!.GetValue<string>());
    }

    [Fact]
    public async Task PairingPushesTheCodeThenTheKeyAfterApproval()
    {
        await using var rig = await StartAsync();

        await using var client = await ConnectAsync(rig);
        Assert.NotNull((await client.CallAsync(CompanionProtocol.Hello, null, Ct))!["result"]);
        var id = await client.SendAsync(CompanionProtocol.PairRequest, new JsonObject { ["name"] = "claude-code", ["level"] = "act" }, Ct);
        var pushed = (await client.ReadEventAsync(Ct))!;
        Assert.Equal(CompanionProtocol.PairCode, pushed["event"]!.GetValue<string>());
        var pairing = pushed["data"]!["id"]!.GetValue<string>();
        var code = pushed["data"]!["code"]!.GetValue<string>();
        Assert.Matches("^[A-Z2-9]{6}$", code);

        // the window shows the same code
        var shown = Assert.Single(rig.Service.ListForWindow()["pairings"]!.AsArray())!;
        Assert.Equal(code, shown["code"]!.GetValue<string>());
        Assert.Equal("claude-code", shown["name"]!.GetValue<string>());

        using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(pairing, approve: true, level: null);

        var reply = (await client.ReadReplyAsync(id, Ct))!["result"]!;
        Assert.True(reply["approved"]!.GetValue<bool>());
        Assert.Equal("act", reply["level"]!.GetValue<string>());
        var key = reply["key"]!.GetValue<string>();
        Assert.True(key.Length >= 32);
        Assert.True(await client.IsClosedAsync(Ct)); // pairing ends the connection

        // the db holds the hash only
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        using (var c = new SqliteConnection(_db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT key_hash FROM companion_clients WHERE name = 'claude-code';";
            Assert.Equal(hash, cmd.ExecuteScalar() as string);
        }
        Assert.DoesNotContain(EveryStoredText(), s => s.Contains(key, StringComparison.Ordinal));

        // the key works on a fresh connection
        await using var authed = await AuthedAsync(rig, "claude-code", key);
        Assert.Equal("act", (await authed.CallAsync(CompanionProtocol.WhoAmI, null, Ct))!["result"]!["level"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADeniedOrBadPairingAnswersAndCloses()
    {
        await using var rig = await StartAsync();

        await using (var client = await ConnectAsync(rig))
        {
            var id = await client.SendAsync(CompanionProtocol.PairRequest, new JsonObject { ["name"] = "cc", ["level"] = "read" }, Ct);
            var pairing = (await client.ReadEventAsync(Ct))!["data"]!["id"]!.GetValue<string>();
            using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(pairing, approve: false, level: null);
            var reply = (await client.ReadReplyAsync(id, Ct))!["result"]!;
            Assert.False(reply["approved"]!.GetValue<bool>());
            Assert.Null(reply["key"]);
            Assert.Contains("denied", reply["reason"]!.GetValue<string>());
            Assert.True(await client.IsClosedAsync(Ct));
        }

        foreach (var p in new[] { new JsonObject { ["name"] = "Bad Name", ["level"] = "read" }, new JsonObject { ["name"] = "cc", ["level"] = "root" }, new JsonObject() })
        {
            await using var client = await ConnectAsync(rig);
            Assert.Equal("bad_params", Code(await client.CallAsync(CompanionProtocol.PairRequest, p, Ct)));
            Assert.True(await client.IsClosedAsync(Ct));
        }
        Assert.Empty(rig.Service.ListForWindow()["pairings"]!.AsArray());
    }

    [Fact]
    public async Task APairingWhoseClientHangsUpIsNotLeftHoldingAKey()
    {
        await using var rig = await StartAsync();
        string pairing;
        await using (var client = await ConnectAsync(rig))
        {
            await client.SendAsync(CompanionProtocol.PairRequest, new JsonObject { ["name"] = "cc", ["level"] = "read" }, Ct);
            pairing = (await client.ReadEventAsync(Ct))!["data"]!["id"]!.GetValue<string>();
        }
        // The client went away. The server stops waiting for it (its session ends); a later approval's key is never handed to anyone.
        await WaitUntilAsync(() => rig.Server.SessionCount == 0);
        using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(pairing, approve: true, level: null);
        var outcome = await rig.Service.WaitPairingAsync(pairing, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // the first (and only) waiter now is this test: the server never took the key
        Assert.True(outcome.Approved);
        Assert.NotNull(outcome.Key);
    }

    [Fact]
    public async Task RevokingAClientClosesItsOpenSession()
    {
        await using var rig = await StartAsync();
        var key = rig.Pair("cc", CompanionLevel.Read);
        var other = rig.Pair("other", CompanionLevel.Read);
        await using var client = await AuthedAsync(rig, "cc", key);
        await using var second = await AuthedAsync(rig, "cc", key);
        await using var bystander = await AuthedAsync(rig, "other", other);
        Assert.NotNull((await client.CallAsync(CompanionProtocol.WhoAmI, null, Ct))!["result"]);

        // revoked from another thread, as the event may be raised on one
        await Task.Run(() => Assert.True(rig.Service.Revoke("cc")), Ct);

        Assert.True(await client.IsClosedAsync(Ct));
        Assert.True(await second.IsClosedAsync(Ct));
        Assert.Equal("other", (await bystander.CallAsync(CompanionProtocol.WhoAmI, null, Ct))!["result"]!["client"]!.GetValue<string>());

        // and the old key opens nothing
        await using var again = await ConnectAsync(rig);
        Assert.Equal("unauthorized", Code(await again.CallAsync(CompanionProtocol.Auth, new JsonObject { ["client"] = "cc", ["key"] = key }, Ct)));
    }

    [Fact]
    public async Task RequestWaitReturnsWhenThePersonDecides()
    {
        await using var rig = await StartAsync();
        var key = rig.Pair("cc", CompanionLevel.Destructive);
        await using var client = await AuthedAsync(rig, "cc", key);

        var asked = (await client.CallAsync(CompanionProtocol.Call, new JsonObject { ["tool"] = "backup_restore", ["args"] = new JsonObject { ["backup"] = "daily-20261110.db" } }, Ct))!["result"]!;
        var request = asked["requestId"]!.GetValue<string>();
        Assert.DoesNotContain("ops.restore", rig.Routed);

        // no wait: where it stands now
        var now = (await client.CallAsync(CompanionProtocol.Request, new JsonObject { ["id"] = request }, Ct))!["result"]!;
        Assert.Equal("pending", now["state"]!.GetValue<string>());
        Assert.Contains("daily-20261110.db", now["plan"]!.GetValue<string>());

        var watch = Stopwatch.StartNew();
        var id = await client.SendAsync(CompanionProtocol.Request, new JsonObject { ["id"] = request, ["waitSeconds"] = 60 }, Ct);
        await Task.Delay(300, Ct);
        using (Actor.Use(Actor.Window)) await rig.Service.DecideActionAsync(request, approve: true, Ct);
        var done = (await client.ReadReplyAsync(id, Ct))!["result"]!;
        Assert.Equal("done", done["state"]!.GetValue<string>());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.Contains("ops.restore", rig.Routed);

        // another client's request id, an unknown id, a bad wait
        var other = rig.Pair("other", CompanionLevel.Destructive);
        await using var stranger = await AuthedAsync(rig, "other", other);
        Assert.Equal("not_found", Code(await stranger.CallAsync(CompanionProtocol.Request, new JsonObject { ["id"] = request }, Ct)));
        Assert.Equal("not_found", Code(await client.CallAsync(CompanionProtocol.Request, new JsonObject { ["id"] = "nope" }, Ct)));
        Assert.Equal("bad_params", Code(await client.CallAsync(CompanionProtocol.Request, new JsonObject { ["id"] = request, ["waitSeconds"] = -1 }, Ct)));
        Assert.Equal("bad_params", Code(await client.CallAsync(CompanionProtocol.Request, new JsonObject { ["id"] = request, ["waitSeconds"] = 61 }, Ct)));
    }

    [Fact]
    public async Task AnExceptionInsideACallAnswersAnErrorAndKeepsTheConnection()
    {
        await using var rig = await StartAsync();
        var key = rig.Pair("cc", CompanionLevel.Destructive);
        await using var client = await AuthedAsync(rig, "cc", key);
        rig.Throw = m => m == "ops.backups" ? new InvalidOperationException("disk on fire, confirm=0123456789abcdef") : null;

        var failed = await client.CallAsync(CompanionProtocol.Call, new JsonObject { ["tool"] = "backup_restore", ["args"] = new JsonObject { ["backup"] = "daily-20261110.db" } }, Ct);
        Assert.Equal("failed", Code(failed));
        Assert.DoesNotContain("0123456789abcdef", failed!.ToJsonString());

        rig.Throw = _ => null;
        Assert.Equal("cc", (await client.CallAsync(CompanionProtocol.WhoAmI, null, Ct))!["result"]!["client"]!.GetValue<string>());
    }

    [Fact]
    public async Task DisposeFinishes_WithClientsConnectedAndAPairingWaiting()
    {
        var rig = await StartAsync();
        var key = rig.Pair("cc", CompanionLevel.Read);
        await using var authed = await AuthedAsync(rig, "cc", key);
        await using var pairing = await ConnectAsync(rig);
        await pairing.SendAsync(CompanionProtocol.PairRequest, new JsonObject { ["name"] = "later", ["level"] = "read" }, Ct);
        await pairing.ReadEventAsync(Ct);

        await rig.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(await authed.IsClosedAsync(Ct));
        Assert.True(await pairing.IsClosedAsync(Ct));
    }

    // ---------------------------------------------------------------- fix round 1

    [Fact]
    public void ClosingASessionRacingItsOwnEnd_NeverThrows()
    {
        // The session's own loop releases it (disposing its token source) right after another thread claimed the close.
        var session = new CompanionServer.Session(new MemoryStream(), CancellationToken.None);
        session.AfterClaim = session.Release;
        session.Close();
        session.Close();
        session.Release();
    }

    [Fact]
    public async Task RevokeAndDisposeRacingSessionsEnding_NeverThrow()
    {
        var rig = await StartAsync();
        for (var round = 0; round < 10; round++)
        {
            var name = "cc" + round;
            var key = rig.Pair(name, CompanionLevel.Read);
            var clients = new List<TestIpcClient>();
            for (var i = 0; i < 4; i++) clients.Add(await AuthedAsync(rig, name, key));
            var hangUps = clients.Select(c => Task.Run(async () => await c.DisposeAsync(), Ct)).ToList();
            var revoke = Task.Run(() => rig.Service.Revoke(name), Ct);
            await Task.WhenAll(hangUps);
            Assert.True(await revoke);
        }
        var last = rig.Pair("last", CompanionLevel.Read);
        var open = new List<TestIpcClient>();
        for (var i = 0; i < 4; i++) open.Add(await AuthedAsync(rig, "last", last));
        var ends = open.Select(c => Task.Run(async () => await c.DisposeAsync(), Ct)).ToList();
        await rig.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.WhenAll(ends);
        Assert.Equal(0, rig.Server.UnexpectedFaults);
    }

    [Fact]
    public async Task ADuplicateKeyFrameClosesCleanly()
    {
        await using var rig = await StartAsync();
        var key = rig.Pair("cc", CompanionLevel.Read);
        foreach (var text in new[]
                 {
                     """{"id":1,"method":"hello","method":"auth"}""",
                     """{"id":1,"id":2,"method":"hello"}""",
                     "{\"id\":1,\"method\":\"auth\",\"params\":{\"client\":\"cc\",\"client\":\"cc\",\"key\":\"" + key + "\"}}",
                     """{"id":1,"method":"pair.request","params":{"name":"x","level":"read","level":"act"}}""",
                 })
        {
            await using var client = await ConnectAsync(rig);
            await client.WriteRawAsync(FrameKind.Json, Encoding.UTF8.GetBytes(text), Ct);
            Assert.True(await client.IsClosedAsync(Ct), text);
        }
        // after auth too
        await using (var authed = await AuthedAsync(rig, "cc", key))
        {
            await authed.WriteRawAsync(FrameKind.Json, Encoding.UTF8.GetBytes("""{"id":9,"method":"companion.call","params":{"tool":"team_list","args":{"a":1,"a":2}}}"""), Ct);
            Assert.True(await authed.IsClosedAsync(Ct));
        }
        await WaitUntilAsync(() => rig.Server.SessionCount == 0);
        Assert.Equal(0, rig.Server.UnexpectedFaults);
        Assert.Empty(rig.Service.ListForWindow()["pairings"]!.AsArray());
        // and the endpoint still serves
        await using var fresh = await ConnectAsync(rig);
        Assert.NotNull((await fresh.CallAsync(CompanionProtocol.Hello, null, Ct))!["result"]);
    }

    [Fact]
    public void ADuplicateKeyControlFileReadsAsNone()
    {
        var home = Path.Combine(_home, "dup");
        UserOnlyFile.EnsureDirectory(home);
        File.WriteAllText(ControlDiscovery.FilePath(home),
            """{"version":1,"address":"a","address":"b","pid":1,"coreVersion":"x","startedAt":"2026-11-11T00:00:00Z"}""");
        Assert.Null(ControlDiscovery.Read(home));
        ControlDiscovery.RemoveIfOurs(home, "a"); // no throw either
    }

    [Fact]
    public void APopulatedExistingHomeIsNotReAcled_AnEmptyOneIsSecured()
    {
        var populated = Path.Combine(_home, "profile");
        Directory.CreateDirectory(Path.Combine(populated, "projects"));
        File.WriteAllText(Path.Combine(populated, "notes.txt"), "mine");
        var before = Snapshot(populated);

        var e = Assert.Throws<InvalidOperationException>(() => ControlDiscovery.Write(populated, new ControlFile(1, "a", 1, "x", DateTimeOffset.UtcNow)));
        Assert.Contains(populated, e.Message);
        Assert.Equal(before, Snapshot(populated));
        Assert.False(File.Exists(ControlDiscovery.FilePath(populated)));
        Assert.False(UserOnlyFile.IsUserOnly(populated));

        var empty = Path.Combine(_home, "empty");
        Directory.CreateDirectory(empty);
        Assert.False(UserOnlyFile.IsUserOnly(empty));
        ControlDiscovery.Write(empty, new ControlFile(1, "a", 1, "x", DateTimeOffset.UtcNow));
        Assert.True(UserOnlyFile.IsUserOnly(empty));

        // a home holding only the companion's own files is secured again (e.g. after its rules were loosened)
        var own = Path.Combine(_home, "own");
        Directory.CreateDirectory(Path.Combine(own, "clients"));
        File.WriteAllText(Path.Combine(own, "control.json"), "{}");
        File.WriteAllText(Path.Combine(own, "clients", "cc.key"), "k");
        UserOnlyFile.EnsureDirectory(own);
        Assert.True(UserOnlyFile.IsUserOnly(own));
        UserOnlyFile.EnsureDirectory(Path.Combine(own, "clients"));
        Assert.True(UserOnlyFile.IsUserOnly(Path.Combine(own, "clients")));

        // a clients folder holding anything else is refused too
        var odd = Path.Combine(_home, "odd");
        Directory.CreateDirectory(Path.Combine(odd, "sub"));
        Assert.Throws<InvalidOperationException>(() => UserOnlyFile.EnsureDirectory(odd));
    }

    /// <summary>The folder's rules (Windows SDDL) or mode, and each child's.</summary>
    private static List<string> Snapshot(string dir)
    {
        static string Of(string p)
        {
            if (OperatingSystem.IsWindows())
                return Directory.Exists(p)
                    ? new DirectoryInfo(p).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access)
                    : new FileInfo(p).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
            return File.GetUnixFileMode(p).ToString();
        }
        return [Of(dir), .. Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Order().Select(p => p + "=" + Of(p))];
    }

    [Fact]
    public async Task AKeyThatCouldNotBeDeliveredIsRevoked_AndTheNameIsFree()
    {
        await using var rig = await StartAsync();
        var client = await ConnectAsync(rig);
        // the client goes away right after the approval, before the reply is written
        rig.Server.BeforePairReply = _ => client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        await client.SendAsync(CompanionProtocol.PairRequest, new JsonObject { ["name"] = "cc", ["level"] = "read" }, Ct);
        var pairing = (await client.ReadEventAsync(Ct))!["data"]!["id"]!.GetValue<string>();

        using (Actor.Use(Actor.Window)) rig.Service.DecidePairing(pairing, approve: true, level: null);

        await WaitUntilAsync(() => rig.Clients.List().Single(c => c.Name == "cc").RevokedAt is not null);
        rig.Server.BeforePairReply = null;
        Assert.NotNull(rig.Service.BeginPairing("cc", CompanionLevel.Read)); // the name pairs again
    }

    [Fact]
    public async Task PreAuthHasATotalDeadline_EvenWithHellos()
    {
        await using var rig = await StartAsync(CompanionServer.Limits.Default with { PreAuthIdle = TimeSpan.FromSeconds(5), PreAuthTotal = TimeSpan.FromMilliseconds(800) });
        await using var client = await ConnectAsync(rig);
        var watch = Stopwatch.StartNew();
        var closed = false;
        while (!closed && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            JsonObject? reply;
            try { reply = await client.CallAsync(CompanionProtocol.Hello, null, Ct); }
            catch (IOException) { reply = null; }
            closed = reply is null;
            if (!closed) await Task.Delay(100, Ct);
        }
        Assert.True(closed, "hellos kept an unauthenticated connection open");
    }

    [Fact]
    public async Task AtMostSoManyConnectionsWaitBeforeAuth()
    {
        await using var rig = await StartAsync(CompanionServer.Limits.Default with { MaxPreAuth = 2 });
        var key = rig.Pair("cc", CompanionLevel.Read);
        await using var a = await ConnectAsync(rig);
        await using var b = await ConnectAsync(rig);
        await WaitUntilAsync(() => rig.Server.SessionCount == 2);

        await using (var c = await ConnectAsync(rig)) Assert.True(await c.IsClosedAsync(Ct)); // turned away

        // authenticating frees a place
        Assert.True((await a.CallAsync(CompanionProtocol.Auth, new JsonObject { ["client"] = "cc", ["key"] = key }, Ct))!["result"]!["authenticated"]!.GetValue<bool>());
        await using var d = await ConnectAsync(rig);
        Assert.NotNull((await d.CallAsync(CompanionProtocol.Hello, null, Ct))!["result"]);
        Assert.NotNull((await b.CallAsync(CompanionProtocol.Hello, null, Ct))!["result"]);
    }

    [Fact]
    public async Task AClientThatNeverReadsIsClosedByTheWriteDeadline()
    {
        await using var rig = await StartAsync(CompanionServer.Limits.Default with { WriteDeadline = TimeSpan.FromMilliseconds(300) });
        var key = rig.Pair("cc", CompanionLevel.Destructive);
        var client = await AuthedAsync(rig, "cc", key);
        for (var i = 0; i < 200; i++)
            if (!await WriteMayBreakAsync(client, FrameKind.Json, Encoding.UTF8.GetBytes($$"""{"id":{{100 + i}},"method":"companion.tools"}"""))) break;
        await WaitUntilAsync(() => rig.Server.SessionCount == 0);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task OversizedFramesClose_AfterAuthAboveAMebibyte_BeforeAuthOnTheSecondFrameToo()
    {
        await using var rig = await StartAsync();
        var key = rig.Pair("cc", CompanionLevel.Read);

        await using (var authed = await AuthedAsync(rig, "cc", key))
        {
            await WriteMayBreakAsync(authed, FrameKind.Json, new byte[CompanionProtocol.MaxFrame + 1]);
            Assert.True(await authed.IsClosedAsync(Ct));
        }
        await using (var client = await ConnectAsync(rig))
        {
            Assert.NotNull((await client.CallAsync(CompanionProtocol.Hello, null, Ct))!["result"]);
            await WriteMayBreakAsync(client, FrameKind.Json, new byte[CompanionProtocol.MaxUnauthenticatedFrame + 1]);
            Assert.True(await client.IsClosedAsync(Ct));
        }
    }

    [Fact]
    public async Task ErrorMessagesAreMaskedToo()
    {
        await using var rig = await StartAsync();
        var key = rig.Pair("cc", CompanionLevel.Read);
        await using var client = await AuthedAsync(rig, "cc", key);
        var reply = await client.CallAsync(CompanionProtocol.Request, new JsonObject { ["id"] = "0123456789abcdef0123" }, Ct);
        Assert.Equal("not_found", Code(reply));
        Assert.DoesNotContain("0123456789abcdef", reply!.ToJsonString());
    }

    // ---------------------------------------------------------------- discovery and user-only files

    [Fact]
    public void ControlFileIsUserOnlyAndHoldsNoSecret()
    {
        var home = Path.Combine(_home, "yawble");
        var started = new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.Zero);
        var file = new ControlFile(CompanionProtocol.Version, @"\\.\pipe\yawble-ctl-0011223344556677", 4242, "0.1.0", started);
        ControlDiscovery.Write(home, file);

        var path = ControlDiscovery.FilePath(home);
        Assert.Equal(Path.Combine(home, "control.json"), path);
        Assert.True(UserOnlyFile.IsUserOnly(home), "home is not user-only");
        Assert.True(UserOnlyFile.IsUserOnly(path), "control.json is not user-only");
        Assert.Equal(file, ControlDiscovery.Read(home));

        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["address", "coreVersion", "pid", "startedAt", "version"], json.Select(p => p.Key).Order().ToArray());
        Assert.DoesNotContain("key", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(home, "*.tmp")); // written by replace, nothing left behind

        // a rewrite replaces it and stays user-only
        ControlDiscovery.Write(home, file with { Pid = 7 });
        Assert.Equal(7, ControlDiscovery.Read(home)!.Pid);
        Assert.True(UserOnlyFile.IsUserOnly(path));

        // the clients folder and a key file are user-only too
        var clients = ControlDiscovery.ClientsDir(home);
        Assert.Equal(Path.Combine(home, "clients"), clients);
        UserOnlyFile.EnsureDirectory(clients);
        var keyFile = Path.Combine(clients, "cc.key");
        UserOnlyFile.WriteAllBytes(keyFile, "k"u8);
        Assert.True(UserOnlyFile.IsUserOnly(clients));
        Assert.True(UserOnlyFile.IsUserOnly(keyFile));
        Assert.Equal("k", File.ReadAllText(keyFile));

        // a file made the ordinary way (inheriting the temp folder's rules) is not user-only
        var ordinary = Path.Combine(_home, "ordinary.txt");
        File.WriteAllText(ordinary, "x");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ordinary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.False(UserOnlyFile.IsUserOnly(ordinary));
        Assert.False(UserOnlyFile.IsUserOnly(Path.Combine(_home, "missing.txt")));
    }

    [Fact]
    public void AMalformedOrMissingControlFileReadsAsNone()
    {
        var home = Path.Combine(_home, "y2");
        Assert.Null(ControlDiscovery.Read(home));
        UserOnlyFile.EnsureDirectory(home);
        foreach (var text in new[] { "", "{", "[]", """{"version":1}""", """{"version":1,"address":"","pid":1,"coreVersion":"x","startedAt":"2026-11-11T00:00:00Z"}""",
                     """{"version":"1","address":"a","pid":1,"coreVersion":"x","startedAt":"2026-11-11T00:00:00Z"}""" })
        {
            File.WriteAllText(ControlDiscovery.FilePath(home), text);
            Assert.Null(ControlDiscovery.Read(home));
        }
    }

    [Fact]
    public void ControlFileIsRemovedOnlyIfStillOurs()
    {
        var home = Path.Combine(_home, "yawble");
        var ours = CompanionServer.NewAddress(Path.GetTempPath());
        var theirs = CompanionServer.NewAddress(Path.GetTempPath());
        ControlDiscovery.Write(home, new ControlFile(1, theirs, 2, "0.1.0", DateTimeOffset.UtcNow));

        ControlDiscovery.RemoveIfOurs(home, ours); // another core wrote it last: keep
        Assert.Equal(theirs, ControlDiscovery.Read(home)!.Address);

        ControlDiscovery.Write(home, new ControlFile(1, ours, 1, "0.1.0", DateTimeOffset.UtcNow));
        ControlDiscovery.RemoveIfOurs(home, ours);
        Assert.False(File.Exists(ControlDiscovery.FilePath(home)));

        ControlDiscovery.RemoveIfOurs(home, ours); // nothing there: no error
        ControlDiscovery.RemoveIfOurs(Path.Combine(_home, "never"), ours);
    }

    /// <summary>
    /// The default home is a folder of its own (yawble-companion), never a folder that may already hold other files: the
    /// populated-folder guard would rightly refuse to re-secure it, so the companion would never start.
    /// </summary>
    [Fact]
    public void TheDefaultHomeIsAFolderOfItsOwn()
    {
        var appdata = Path.Combine(_home, "AppData", "Roaming");
        var userHome = Path.Combine(_home, "me");
        var home = ControlDiscovery.Home(n => n switch { "APPDATA" => appdata, "HOME" => userHome, _ => null });
        Assert.Equal("yawble-companion", Path.GetFileName(home));
        Assert.Equal("yawble-companion", Path.GetFileName(ControlDiscovery.Home(_ => null)));
        Assert.Equal("yawble-companion", ControlDiscovery.DefaultFolderName);
    }

    [Fact]
    public void HomeHonoursYawbleHome()
    {
        var custom = Path.Combine(_home, "custom");
        Assert.Equal(Path.GetFullPath(custom), ControlDiscovery.Home(n => n == "YAWBLE_HOME" ? custom : null));
        // an empty YAWBLE_HOME is unset
        var appdata = Path.Combine(_home, "AppData", "Roaming");
        var userHome = Path.Combine(_home, "me");
        var xdg = Path.Combine(_home, "xdg");
        string? Env(string n) => n switch { "YAWBLE_HOME" => " ", "APPDATA" => appdata, "HOME" => userHome, "XDG_CONFIG_HOME" => xdg, _ => null };
        var expected = OperatingSystem.IsWindows() ? Path.Combine(appdata, "yawble-companion")
            : OperatingSystem.IsMacOS() ? Path.Combine(userHome, "Library", "Application Support", "yawble-companion")
            : Path.Combine(xdg, "yawble-companion");
        Assert.Equal(expected, ControlDiscovery.Home(Env));
        if (OperatingSystem.IsLinux())
            Assert.Equal(Path.Combine(userHome, ".config", "yawble-companion"), ControlDiscovery.Home(n => n == "HOME" ? userHome : null));
        // Home only computes a path: nothing is created
        Assert.False(Directory.Exists(expected));
    }

    // ---------------------------------------------------------------- helpers

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TestIpcClient.Limit, "the condition never held");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<bool> WriteMayBreakAsync(TestIpcClient client, FrameKind kind, byte[] payload)
    {
        try { await client.WriteRawAsync(kind, payload, Ct); return true; }
        catch (IOException) { return false; } // the server closed while the frame was still being written
    }


    private List<string> EveryStoredText()
    {
        var all = new List<string>();
        using var c = new SqliteConnection(_db.ConnectionString);
        c.Open();
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        foreach (var t in tables)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{t}\";";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                    if (!r.IsDBNull(i) && r.GetValue(i) is string s) all.Add(s);
        }
        return all;
    }
}
