using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Link;

/// <summary>
/// The Cloud Link against a fake cloud site (HttpListener on this computer): pairing by code, the one
/// outbound WebSocket, remote calls through the core's dispatcher after RemoteAccess, pushes while someone views, the
/// audit log, and a detach from the site.
/// </summary>
public class CloudLinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("https://manage.example.com", "https://manage.example.com/")]
    [InlineData("https://manage.example.com/org/", "https://manage.example.com/org/")]
    [InlineData("http://localhost:5280", "http://localhost:5280/")]
    [InlineData("http://127.0.0.1:9000/x", "http://127.0.0.1:9000/x/")]
    public void ASiteAddressIsHttpsOrALocalSite(string typed, string expected) =>
        Assert.Equal(expected, CloudLink.ParseSiteUrl(typed).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("manage.example.com")]
    [InlineData("http://manage.example.com")]
    [InlineData("ftp://manage.example.com")]
    [InlineData("https://user:pw@manage.example.com")]
    public void OtherAddressesAreRefused(string typed) =>
        Assert.Equal("bad_params", Assert.Throws<RpcException>(() => CloudLink.ParseSiteUrl(typed)).Code);

    [Theory]
    [InlineData("app.overview", null, true)]
    [InlineData("teams.create", null, true)]
    [InlineData("terminal.attach", "member:3", true)]
    [InlineData("agents.detect", null, true)]
    [InlineData("agents.test", null, false)]
    [InlineData("agents.stopTest", null, false)]
    [InlineData("credentials.status", null, true)]
    [InlineData("link.forget", null, false)]
    [InlineData("link.pair", null, false)]
    [InlineData("credentials.setAnthropicApiKey", null, false)]
    [InlineData("credentials.remove", null, false)]
    [InlineData("hostTerminal.start", null, false)]
    [InlineData("terminal.input", "host:grok-login", false)]
    [InlineData("terminal.attach", "host:claude-setup-token", false)]
    [InlineData("something.else", null, false)]
    [InlineData("integrations.approve", null, true)]
    [InlineData("integrations.checklist", null, true)]
    [InlineData("integrations.saveChecklist", null, true)]
    [InlineData("integrations.discover", null, true)]
    [InlineData("integrations.openLogin", null, false)]
    [InlineData("integrations.mcpSave", null, false)]
    [InlineData("tasks.save", null, false)]
    [InlineData("tasks.list", null, true)]
    [InlineData("tasks.approve", null, true)]
    public void WhatARemoteViewerMayCall(string method, string? channel, bool allowed)
    {
        var p = channel is null ? null : new JsonObject { ["channel"] = channel };
        Assert.Equal(allowed, RemoteAccess.Refusal(method, p, allowHostTerminals: false) is null);
    }

    [Theory]
    [InlineData("hostBridge.hello")]
    [InlineData("hostBridge.reply")]
    [InlineData("hostBridge.signedIn")]
    public void CloudLinkRefusesHostBridge(string method)
    {
        Assert.NotNull(RemoteAccess.Refusal(method, new JsonObject(), allowHostTerminals: false));
        Assert.NotNull(RemoteAccess.Refusal(method, new JsonObject(), allowHostTerminals: true));
    }

    [Theory]
    [InlineData("integrations.websiteSignIn")]
    [InlineData("integrations.websiteSignInDone")]
    public void CloudLinkRefusesWebsiteSignIn(string method)
    {
        var refusal = RemoteAccess.Refusal(method, new JsonObject { ["connectionId"] = 1 }, allowHostTerminals: true);
        Assert.Equal("sign-ins happen on the computer itself", refusal);
        Assert.NotNull(RemoteAccess.Refusal(method, new JsonObject(), allowHostTerminals: false));
        Assert.NotNull(RemoteAccess.Refusal("integrations.connectionSave", new JsonObject { ["kind"] = "website" }, allowHostTerminals: true));
        Assert.NotNull(RemoteAccess.Refusal("integrations.connectionTest", new JsonObject { ["id"] = 1 }, allowHostTerminals: true));
    }

    [Fact]
    public void CloudLinkRefusesOAuthSignInAndDiscover()
    {
        foreach (var terminals in new[] { false, true })
        {
            Assert.Equal("sign-ins happen on the computer itself",
                RemoteAccess.Refusal("integrations.oauthSignIn", new JsonObject { ["api"] = "notes", ["clientId"] = "x" }, terminals));
            Assert.Equal("sign-ins happen on the computer itself",
                RemoteAccess.Refusal("integrations.oauthDiscover", new JsonObject { ["provider"] = "https://idp.example.test" }, terminals));
            Assert.NotNull(RemoteAccess.Refusal("integrations.oauthAnythingElse", null, terminals));
        }
        // the API list (with its scopes) is still fine to look at
        Assert.Null(RemoteAccess.Refusal("integrations.apiList", null, allowHostTerminals: false));
    }

    [Theory]
    [InlineData("terminal.attach")]
    [InlineData("terminal.input")]
    [InlineData("terminal.resize")]
    public void AnAgentsTerminalSessionIsNeverUsedOverTheCloudLink_HostTerminalsAllowedOrNot(string method)
    {
        var p = new JsonObject { ["channel"] = "agent-test:claude" };
        Assert.NotNull(RemoteAccess.Refusal(method, p, allowHostTerminals: false));
        Assert.NotNull(RemoteAccess.Refusal(method, p, allowHostTerminals: true));
        // A member's terminal stays reachable.
        Assert.Null(RemoteAccess.Refusal(method, new JsonObject { ["channel"] = "member:3" }, allowHostTerminals: false));
    }

    [Fact]
    public void AnAgentsTerminalSessionIsNotStoppedOrPushedOverTheCloudLink()
    {
        Assert.NotNull(RemoteAccess.Refusal("runs.stop", new JsonObject { ["team"] = "_agent-test", ["member"] = "claude" }, allowHostTerminals: true));
        Assert.Null(RemoteAccess.Refusal("runs.stop", new JsonObject { ["team"] = "alpha", ["member"] = "dev1" }, allowHostTerminals: false));
        static JsonObject Push(string ev, string channel) => new() { ["event"] = ev, ["data"] = new JsonObject { ["channel"] = channel } };
        Assert.False(CloudLink.Forwards(Push("terminal.output", "agent-test:claude")));
        Assert.False(CloudLink.Forwards(Push("terminal.state", "agent-test:grok")));
        Assert.True(CloudLink.Forwards(Push("terminal.output", "member:3")));
        Assert.True(CloudLink.Forwards(new JsonObject { ["event"] = "app.changed", ["data"] = null }));
        Assert.False(CloudLink.Forwards(new JsonObject { ["event"] = "companion.changed" }));
    }

    [Fact]
    public void HostTerminalsOnlyWhenThePersonAtTheComputerAllowsThem()
    {
        Assert.Null(RemoteAccess.Refusal("hostTerminal.start", null, allowHostTerminals: true));
        Assert.Null(RemoteAccess.Refusal("terminal.input", new JsonObject { ["channel"] = "host:grok-login" }, allowHostTerminals: true));
        Assert.NotNull(RemoteAccess.Refusal("link.forget", null, allowHostTerminals: true));
        Assert.Null(RemoteAccess.Refusal("integrations.mcpSave", null, allowHostTerminals: true));
        Assert.Null(RemoteAccess.Refusal("tasks.save", null, allowHostTerminals: true));
        Assert.NotNull(RemoteAccess.Refusal("integrations.openLogin", null, allowHostTerminals: true));
    }

    [Fact]
    public void RemoteDetectEnableOrDiscoverOfACliTheAppDoesNotKnow_NeedsHostTerminals()
    {
        foreach (var method in new[] { "integrations.detect", "integrations.enable", "integrations.discover" })
        {
            Assert.NotNull(RemoteAccess.Refusal(method, new JsonObject { ["name"] = "shutdown" }, allowHostTerminals: false));
            Assert.NotNull(RemoteAccess.Refusal(method, new JsonObject { ["name"] = "mytool", ["kind"] = "cli" }, allowHostTerminals: false, isIntegration: _ => false));
            Assert.Null(RemoteAccess.Refusal(method, new JsonObject { ["name"] = "gh" }, allowHostTerminals: false));
            Assert.Null(RemoteAccess.Refusal(method, new JsonObject { ["name"] = "mytool" }, allowHostTerminals: false, isIntegration: n => n == "mytool"));
            Assert.Null(RemoteAccess.Refusal(method, new JsonObject { ["name"] = "mytool" }, allowHostTerminals: true));
        }
        Assert.Null(RemoteAccess.Refusal("integrations.discover", new JsonObject { ["name"] = "files", ["kind"] = "mcp" }, allowHostTerminals: false));
        Assert.Contains("host terminals", RemoteAccess.Refusal("integrations.detect", new JsonObject { ["name"] = "mytool" }, allowHostTerminals: false)!);
    }

    [Fact]
    public void CloudLinkRefusesMcpSignInImportAndSecrets()
    {
        foreach (var terminals in new[] { false, true })
        {
            foreach (var m in new[] { "integrations.mcpImportScan", "integrations.mcpImport", "integrations.mcpSignIn" })
                Assert.Contains("sign-ins and imports happen on the computer itself", RemoteAccess.Refusal(m, new JsonObject { ["name"] = "x" }, terminals)!);
            foreach (var key in new[] { "transport", "secrets", "connectionId", "auth" })
            {
                JsonNode? v = key == "transport" ? "http" : key == "connectionId" ? 3 : new JsonObject { ["token"] = "x" };
                Assert.NotNull(RemoteAccess.Refusal("integrations.mcpSave", new JsonObject { ["name"] = "x", ["command"] = "node", [key] = v }, terminals));
            }
        }
        // a plain local server needs host terminals as before; with them it is fine
        var local = new JsonObject { ["name"] = "x", ["command"] = "node" };
        Assert.NotNull(RemoteAccess.Refusal("integrations.mcpSave", local, false));
        Assert.Null(RemoteAccess.Refusal("integrations.mcpSave", local, true));
    }

    [Fact]
    public void CloudLinkRefusesRepointingARemoteMcp()
    {
        foreach (var terminals in new[] { false, true })
        {
            Assert.NotNull(RemoteAccess.Refusal("integrations.mcpSave", new JsonObject { ["name"] = "r", ["url"] = "https://evil.test/", ["command"] = "x" }, terminals));
            Assert.NotNull(RemoteAccess.Refusal("integrations.mcpSave", new JsonObject { ["name"] = "r", ["command"] = "x" }, terminals, isRemoteMcp: n => n == "r"));
            Assert.NotNull(RemoteAccess.Refusal("integrations.mcpSave", new JsonObject { ["name"] = "r", ["transport"] = "stdio", ["command"] = "x" }, terminals));
        }
        Assert.Null(RemoteAccess.Refusal("integrations.mcpSave", new JsonObject { ["name"] = "l", ["command"] = "x" }, true, isRemoteMcp: n => n == "r"));
    }

    [Fact]
    public void CloudLinkRefusesMcpRemoveOfTheConnection()
    {
        Assert.Contains("connections can only be changed", RemoteAccess.Refusal("integrations.mcpRemove", new JsonObject { ["name"] = "r", ["removeConnection"] = false }, true)!);
        Assert.Null(RemoteAccess.Refusal("integrations.mcpRemove", new JsonObject { ["name"] = "r" }, true));
    }

    [Fact]
    public void CloudLinkRefusesDiscoverOfRemoteMcp()
    {
        var p = new JsonObject { ["name"] = "remote", ["kind"] = "mcp" };
        Assert.NotNull(RemoteAccess.Refusal("integrations.discover", p, false, isRemoteMcp: n => n == "remote"));
        Assert.NotNull(RemoteAccess.Refusal("integrations.discover", p, true, isRemoteMcp: n => n == "remote"));
        Assert.Null(RemoteAccess.Refusal("integrations.discover", new JsonObject { ["name"] = "local", ["kind"] = "mcp" }, false, isRemoteMcp: n => n == "remote"));
        Assert.NotNull(RemoteAccess.Refusal("integrations.mcpCheck", new JsonObject { ["name"] = "remote" }, false, isRemoteMcp: n => n == "remote"));
        Assert.NotNull(RemoteAccess.Refusal("integrations.mcpCheck", new JsonObject { ["name"] = "remote" }, true, isRemoteMcp: n => n == "remote"));
        Assert.Null(RemoteAccess.Refusal("integrations.mcpCheck", new JsonObject { ["name"] = "local" }, true, isRemoteMcp: n => n == "remote"));
        Assert.Null(RemoteAccess.Refusal("integrations.discover", p, false));
    }

    [Fact]
    public void CloudLinkAllowsMcpParse()
    {
        Assert.Null(RemoteAccess.Refusal("integrations.mcpParse", new JsonObject { ["text"] = "{}" }, false));
        Assert.Null(RemoteAccess.Refusal("integrations.mcpParse", new JsonObject { ["text"] = "{}" }, true));
    }

    [Fact]
    public void RemoteDetectOrEnable_IgnoresKind_AsTheHandlersDo()
    {
        // detect and enable never read 'kind': any kind still runs the named CLI
        foreach (var method in new[] { "integrations.detect", "integrations.enable" })
            foreach (JsonNode? kind in new JsonNode?[] { "mcp", "", "other", 5 })
                Assert.NotNull(RemoteAccess.Refusal(method, new JsonObject { ["name"] = "mytool", ["kind"] = kind }, allowHostTerminals: false, isIntegration: _ => false));
    }

    [Fact]
    public void RemoteDiscover_ReadsKindAsTheHandlerDoes_EmptyIsACli()
    {
        foreach (JsonNode? kind in new JsonNode?[] { "", "cli", 5, true })
            Assert.NotNull(RemoteAccess.Refusal("integrations.discover", new JsonObject { ["name"] = "mytool", ["kind"] = kind }, allowHostTerminals: false, isIntegration: _ => false));
        Assert.Null(RemoteAccess.Refusal("integrations.discover", new JsonObject { ["name"] = "mytool", ["kind"] = "mcp" }, allowHostTerminals: false, isIntegration: _ => false));
    }

    [Fact]
    public void RemoteSaveRuleOfAnUnknownCli_NeedsHostTerminals_ItWouldAddTheIntegration()
    {
        var rule = (string name) => new JsonObject { ["name"] = name, ["pattern"] = "x *", ["decision"] = "allow" };
        Assert.Contains("host terminals", RemoteAccess.Refusal("integrations.saveRule", rule("mytool"), allowHostTerminals: false, isIntegration: _ => false)!);
        Assert.NotNull(RemoteAccess.Refusal("integrations.saveRule", rule("mytool"), allowHostTerminals: false));
        Assert.Null(RemoteAccess.Refusal("integrations.saveRule", rule("gh"), allowHostTerminals: false));
        Assert.Null(RemoteAccess.Refusal("integrations.saveRule", rule("mytool"), allowHostTerminals: false, isIntegration: n => n == "mytool"));
        Assert.Null(RemoteAccess.Refusal("integrations.saveRule", rule("mytool"), allowHostTerminals: true));
    }

    [Fact]
    public void RemoteCallsNamingACliWithoutATextName_AreRefused()
    {
        foreach (var method in new[] { "integrations.detect", "integrations.enable", "integrations.discover", "integrations.saveRule" })
            Assert.NotNull(RemoteAccess.Refusal(method, new JsonObject { ["name"] = 5 }, allowHostTerminals: false, isIntegration: _ => true));
    }

    [Fact]
    public async Task KeepsTheFilesFromBeforeTheRename()
    {
        using var dir = new TempDb();
        File.WriteAllText(Path.Combine(dir.Dir, "management-link.json"),
            "{\"url\":\"https://manage.example.com/\",\"instanceId\":\"i1\",\"secret\":\"s\",\"instanceName\":\"DESK\"}");
        File.WriteAllText(Path.Combine(dir.Dir, "management-link-audit.log"), "earlier\n");
        await using var link = new CloudLink(dir.Dir, (_, _, _) => Task.FromResult<JsonNode?>(null), new AppEvents());
        Assert.False(File.Exists(Path.Combine(dir.Dir, "management-link.json")));
        Assert.Equal("earlier\n", File.ReadAllText(Path.Combine(dir.Dir, "cloud-link-audit.log")));
        Assert.Equal("i1", link.Status()["instanceId"]!.GetValue<string>());
    }

    /// <summary>
    /// Opt-in (YAWBLE_REAL_LOOPBACK=1): the fake site is a real HttpListener on a loopback port and the link reaches it over
    /// real HTTP and a real WebSocket (CloudLink has no transport seam for its static HttpClient and ClientWebSocket).
    /// </summary>
    [Fact]
    public async Task PairsByCode_AnswersRemoteCallsThroughTheDispatcher_PushesWhileViewed_AndForgetsWhenDetached()
    {
        Gateway.GatewayFixture.SkipUnlessRealLoopback("a fake cloud site on a real HttpListener, reached over real HTTP and a WebSocket");
        if (!HttpListener.IsSupported) Assert.Skip("HttpListener is not supported here");
        using var dir = new TempDb();
        await using var site = FakeSite.Start();
        var events = new AppEvents();
        var calls = new List<string>();
        await using var link = new CloudLink(dir.Dir, (method, p, _) =>
        {
            lock (calls) calls.Add(method);
            return Task.FromResult<JsonNode?>(new JsonObject { ["echo"] = method, ["teamId"] = p?["teamId"]?.DeepClone() });
        }, events);

        var pairing = await link.StartPairingAsync(site.Url, Ct);
        Assert.Equal("pairing", pairing["state"]!.GetValue<string>());
        Assert.Equal("ABCD-2345", pairing["code"]!.GetValue<string>());
        // The site's pair page, with the code filled in, for the app to open in the person's browser.
        Assert.Equal(new Uri(new Uri(site.Url), "pair?code=ABCD-2345").ToString(), pairing["pairUrl"]!.GetValue<string>());
        Assert.Equal("https://cloud.yawble.ai/", pairing["defaultUrl"]!.GetValue<string>());

        site.Claim(); // the person typed the code on the site
        var ws = await site.Instance.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var hello = await site.ReceiveAsync(ws);
        Assert.Equal("hello", hello["type"]!.GetValue<string>());
        await WaitFor(() => link.Status()["state"]!.GetValue<string>() == "connected");
        Assert.Equal("Platform", link.Status()["workgroup"]!.GetValue<string>());
        // A newer site answers the hello with its version (Settings > System > About shows it).
        Assert.Null(link.CloudVersion);
        await site.SendAsync(ws, new JsonObject { ["type"] = "welcome", ["version"] = "2026.11.12.1" });
        await WaitFor(() => link.CloudVersion == "2026.11.12.1");
        Assert.Equal("2026.11.12.1", link.Status()["cloudVersion"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(dir.Dir, "cloud-link.json")));

        // Someone opens the instance on the site.
        await site.SendAsync(ws, new JsonObject { ["type"] = "viewers", ["users"] = new JsonArray("person@example.com") });
        await WaitFor(() => link.Status()["viewers"]!.AsArray().Count == 1);

        await site.SendAsync(ws, Call("v1", 1, "teams.create", new JsonObject { ["teamId"] = 7 }));
        var answer = await site.ReceiveUntilAsync(ws, m => m["type"]?.GetValue<string>() == "result");
        Assert.Equal("v1", answer["session"]!.GetValue<string>());
        Assert.Equal(1, answer["id"]!.GetValue<int>());
        Assert.Equal("teams.create", answer["result"]!["echo"]!.GetValue<string>());
        Assert.Equal(7, answer["result"]!["teamId"]!.GetValue<int>());

        await site.SendAsync(ws, Call("v1", 2, "link.forget", null));
        var refused = await site.ReceiveUntilAsync(ws, m => m["type"]?.GetValue<string>() == "result");
        Assert.Equal("remote_refused", refused["error"]!["code"]!.GetValue<string>());
        // The yawble companion is local only: its window methods never reach the core from the site.
        foreach (var (method, n) in new[] { ("companion.list", 3), ("companion.decideAction", 4), ("companion.revoke", 5) })
        {
            await site.SendAsync(ws, Call("v1", n, method, new JsonObject { ["id"] = "r1", ["approve"] = true, ["name"] = "cc" }));
            var no = await site.ReceiveUntilAsync(ws, m => m["type"]?.GetValue<string>() == "result" && m["id"]?.GetValue<int>() == n);
            Assert.Equal("remote_refused", no["error"]!["code"]!.GetValue<string>());
        }
        lock (calls) Assert.Equal(["teams.create"], calls);

        // A push from the core goes up while someone views; the companion's own pushes never do.
        events.Publish("companion.changed", null);
        events.Publish("terminal.output", new JsonObject { ["channel"] = "member:1", ["data"] = "aGk=" });
        var sawCompanion = false;
        var pushed = await site.ReceiveUntilAsync(ws, m =>
        {
            if (m["event"]?.GetValue<string>()?.StartsWith("companion.", StringComparison.Ordinal) == true) sawCompanion = true;
            return m["type"]?.GetValue<string>() == "event" && m["event"]?.GetValue<string>() == "terminal.output";
        });
        Assert.False(sawCompanion);
        Assert.Equal("member:1", pushed["data"]!["channel"]!.GetValue<string>());

        var audit = await File.ReadAllLinesAsync(link.AuditFile, Ct);
        Assert.Contains(audit, l => l.Contains("person@example.com\tteams.create\tdone"));
        Assert.Contains(audit, l => l.Contains("link.forget\trefused"));

        // Detached on the site: the core forgets its id and secret.
        await site.SendAsync(ws, new JsonObject { ["type"] = "detached" });
        await WaitFor(() => link.Status()["state"]!.GetValue<string>() == "off");
        Assert.False(File.Exists(Path.Combine(dir.Dir, "cloud-link.json")));
    }

    private static JsonObject Call(string session, int id, string method, JsonObject? p) => new()
    {
        ["type"] = "call", ["session"] = session, ["id"] = id, ["method"] = method, ["params"] = p, ["user"] = "person@example.com",
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>A cloud site of the shape the link expects, on this computer.</summary>
    private sealed class FakeSite : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private volatile bool _claimed;
        public string Url { get; }
        public TaskCompletionSource<WebSocket> Instance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeSite(int port)
        {
            Url = $"http://localhost:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public static FakeSite Start()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return new FakeSite(port);
        }

        public void Claim() => _claimed = true;

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); } catch (Exception) { return; }
                _ = Task.Run(() => HandleAsync(c));
            }
        }

        private async Task HandleAsync(HttpListenerContext c)
        {
            var path = c.Request.Url!.AbsolutePath;
            if (path == "/api/link/pair" && c.Request.HttpMethod == "POST")
            {
                await Json(c, 200, new JsonObject { ["pairingId"] = "p1", ["pollSecret"] = "poll", ["code"] = "ABCD-2345", ["expiresAt"] = "2099-01-01T00:00:00Z" });
            }
            else if (path == "/api/link/pair/p1")
            {
                if (c.Request.Headers["X-Poll-Secret"] != "poll") await Json(c, 404, []);
                else if (!_claimed) await Json(c, 202, []);
                else await Json(c, 200, new JsonObject { ["instanceId"] = "i1", ["secret"] = "s1", ["instanceName"] = "Dev box", ["org"] = "Acme", ["workgroup"] = "Platform" });
            }
            else if (path == "/api/link/ws" && c.Request.IsWebSocketRequest)
            {
                if (c.Request.Headers["Authorization"] != "Bearer i1.s1")
                {
                    c.Response.StatusCode = 401;
                    c.Response.Close();
                    return;
                }
                var ws = await c.AcceptWebSocketAsync(null);
                Instance.TrySetResult(ws.WebSocket);
            }
            else
            {
                await Json(c, 404, []);
            }
        }

        private static async Task Json(HttpListenerContext c, int status, JsonObject body)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            c.Response.StatusCode = status;
            c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }

        public Task SendAsync(WebSocket ws, JsonObject m) =>
            ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(m), WebSocketMessageType.Text, true, Ct);

        public async Task<JsonObject> ReceiveAsync(WebSocket ws)
        {
            var buffer = new byte[1 << 16];
            using var ms = new MemoryStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, timeout.Token);
                ms.Write(buffer, 0, r.Count);
                if (r.EndOfMessage) return (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()))!;
            }
        }

        public async Task<JsonObject> ReceiveUntilAsync(WebSocket ws, Func<JsonObject, bool> match)
        {
            while (true)
            {
                var m = await ReceiveAsync(ws);
                if (match(m)) return m;
            }
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }
}
