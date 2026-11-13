using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Remote MCP servers in the service. A scripted MCP-over-HTTP server in a FakeHttp; no network, no programs, no OS store.</summary>
public class McpRemoteCallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Url = "https://mcp.example.test/mcp";
    private const string Token = "tok_SUPERSECRET_abcdef123456";

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db = new();
        public MemoryCredentialStore Store = new();
        public ConnectionService Conns = null!;
        public BusEngine Engine = null!;
        public IntegrationService Svc = null!;
        public FakeHttp Http = new();
        public long Team;
        public List<string> Auth = [];     // Authorization header per request that reached the server
        public List<string> ApiKey = [];   // X-Api-Key per request
        public List<string> Calls = [];    // tools/call names that reached the server
        public bool Refuse;                // answer 401 to everything
        public bool Forbid;                // answer 403 to everything
        public string? SessionId;          // handed out by initialize
        public List<(string Url, string Auth, string ApiKey)> Deletes = [];
        public volatile Func<RunContext, CancellationToken, Task>? Script;
        public volatile bool Done;
        public string? Seen;
        public int Changes;

        public static async Task<Rig> MakeAsync()
        {
            var r = new Rig();
            var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
            {
                if (run.Items[0].Payload.Contains("approved")) return RunResult.Ok;
                if (r.Script is { } s) await s(run, ct);
                r.Done = true;
                return RunResult.Ok;
            });
            r.Engine = new BusEngine(r.Db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            r.Conns = new ConnectionService(r.Store, r.Db.ConnectionString);
            r.Conns.Changed = () => r.Changes++;
            r.Svc = new IntegrationService(r.Db.ConnectionString, r.Engine, connections: r.Conns, mcpHandler: () => r.Http);
            r.Engine.Integrations = r.Svc;
            r.Team = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await r.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            r.Http.Add(Url, r.Serve);
            await r.Engine.StartAsync(Ct);
            return r;
        }

        public HttpResponseMessage Serve(HttpRequestMessage req)
        {
            if (req.Method == HttpMethod.Delete)
            {
                Deletes.Add((req.RequestUri!.AbsoluteUri, req.Headers.TryGetValues("Authorization", out var da) ? da.First() : "", req.Headers.TryGetValues("X-Api-Key", out var dk) ? dk.First() : ""));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Auth.Add(req.Headers.TryGetValues("Authorization", out var a) ? a.First() : "");
            ApiKey.Add(req.Headers.TryGetValues("X-Api-Key", out var k) ? k.First() : "");
            if (Forbid) return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("no") };
            if (Refuse)
            {
                var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("denied") };
                denied.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer realm=\"x\"");
                return denied;
            }
            var b = (JsonObject)JsonNode.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            string Reply(string result) => $"{{\"jsonrpc\":\"2.0\",\"id\":{b["id"]},\"result\":{result}}}";
            HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            switch (b["method"]!.GetValue<string>())
            {
                case "initialize":
                    var init = Json(Reply("{\"serverInfo\":{\"name\":\"fake\"}}"));
                    if (SessionId is not null) init.Headers.Add("Mcp-Session-Id", SessionId);
                    return init;
                case "notifications/initialized": return new HttpResponseMessage(HttpStatusCode.Accepted);
                case "tools/list":
                    return Json(Reply("{\"tools\":[{\"name\":\"echo\",\"description\":\"Says it back.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"msg\":{\"type\":\"string\"}}}},"
                        + "{\"name\":\"delete_all\",\"description\":\"Deletes everything.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}]}"));
                case "tools/call":
                    var name = b["params"]!["name"]!.GetValue<string>();
                    Calls.Add(name);
                    var text = name == "echo" ? "echo: " + b["params"]!["arguments"]!["msg"] + (b["params"]!["arguments"]!["_reason"] is null ? "" : " LEAKED") : "would delete everything";
                    return Json(Reply($"{{\"content\":[{{\"type\":\"text\",\"text\":{JsonValue.Create(text)!.ToJsonString()}}}]}}"));
                default: return Json(Reply("{}"));
            }
        }

        public Task<JsonNode?> Rpc(string method, JsonObject? p = null) => Svc.DispatchAsync(method, p, Ct);

        public async Task SaveHttp(string auth = "bearer", string? header = null, string? token = Token)
        {
            var a = new JsonObject { ["type"] = auth };
            if (header is not null) a["header"] = header;
            var p = new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = Url, ["auth"] = a };
            if (token is not null) p["secrets"] = new JsonObject { ["token"] = token };
            await Rpc("integrations.mcpSave", p);
            await Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" });
            await Rpc("integrations.mcpEnable", new JsonObject { ["name"] = "remote", ["teamId"] = Team, ["on"] = true });
        }

        public Task Allow(string pattern, string decision) =>
            Rpc("integrations.mcpSaveRule", new JsonObject { ["name"] = "remote", ["pattern"] = pattern, ["decision"] = decision });

        public static async Task<string> Call(RunContext run, string tool, object args)
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
            };
            var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
            return JsonNode.Parse(reply.Json!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        }

        public async Task RunAgent(Func<RunContext, Task<string>> script)
        {
            Script = async (run, _) => Seen = await script(run);
            await Engine.PersonTellAsync("Alpha", "Ada", "go", ct: Ct);
            await Wait.UntilAsync(() => Done, "Ada's call");
        }

        public async ValueTask DisposeAsync() { await Svc.DisposeAsync(); await Engine.DisposeAsync(); Db.Dispose(); }
    }

    [Fact]
    public async Task RemoteToolsAreOfferedAndAllowedCallRuns()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp();
        await r.Allow("echo", "allow");
        string[]? tools = null;
        await r.RunAgent(async run =>
        {
            var list = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), Ct);
            tools = JsonNode.Parse(list.Json!)!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();
            return await Rig.Call(run, "remote__echo", new { msg = "hi", _reason = "testing" });
        });
        Assert.Contains("remote__echo", tools!);
        Assert.Equal("echo: hi", r.Seen); // the reason never reaches the server
        Assert.Contains("echo", r.Calls);
    }

    [Fact]
    public async Task BearerTokenIsSentOnlyToTheServer()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp("bearer");
        await r.Allow("echo", "allow");
        await r.RunAgent(run => Rig.Call(run, "remote__echo", new { msg = "hi" }));
        Assert.All(r.Auth, a => Assert.Equal("Bearer " + Token, a));
        Assert.NotEmpty(r.Auth);
        Assert.DoesNotContain(Token, r.Seen);
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(r.Db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(coalesce(args, '') || coalesce(integration, ''), ' ') FROM integration_calls;";
        var log = cmd.ExecuteScalar() as string ?? "";
        Assert.DoesNotContain(Token, log);
        Assert.DoesNotContain(Token, (await r.Rpc("integrations.mcpList"))!.ToJsonString());
    }

    [Fact]
    public async Task HeaderSignInUsesTheNamedHeader()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp("header", "X-Api-Key");
        await r.Allow("echo", "allow");
        await r.RunAgent(run => Rig.Call(run, "remote__echo", new { msg = "hi" }));
        Assert.All(r.ApiKey, k => Assert.Equal(Token, k));
        Assert.NotEmpty(r.ApiKey);
        Assert.All(r.Auth, a => Assert.Equal("", a));
    }

    [Fact]
    public async Task UnauthorizedMarksNeedsReconnect_AndTellsTheAgentToStop()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp();
        await r.Allow("echo", "allow");
        r.Refuse = true;
        r.Changes = 0;
        await r.RunAgent(run => Rig.Call(run, "remote__echo", new { msg = "hi" }));
        Assert.Equal("remote needs you to sign in again (Connection remote token): stop and tell the person; do not retry.", r.Seen);
        var conn = Assert.Single(r.Conns.List());
        Assert.Equal("needs_reconnect", conn.Status);
        Assert.Equal("remote refused the sign-in", conn.StatusReason);
        Assert.True(r.Changes > 0);
        var attention = (JsonArray)(await r.Rpc("integrations.connectionAttention"))!;
        Assert.Equal("remote token", attention[0]!["name"]!.GetValue<string>());
        Assert.DoesNotContain(Token, r.Seen);
    }

    [Fact]
    public async Task ASuccessfulAnswerClearsNeedsReconnect_AndAnnouncesOnlyARealChange()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp();
        var id = Assert.Single(r.Conns.List()).Id;
        r.Conns.MarkStatus(id, "needs_reconnect", "x");
        r.Changes = 0;
        await r.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" });
        Assert.Equal("works", r.Conns.List().Single().Status);
        Assert.Equal(1, r.Changes);
        await r.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" });
        Assert.Equal(1, r.Changes); // already works: nothing announced
    }

    [Fact]
    public async Task ACheckThatIsRefusedMarksNeedsReconnectToo()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp();
        r.Refuse = true;
        var e = await Assert.ThrowsAsync<Yawble.Core.Ipc.RpcException>(() => r.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" }));
        Assert.Contains("sign in again", e.Message);
        Assert.Equal("needs_reconnect", Assert.Single(r.Conns.List()).Status);
    }

    [Fact]
    public async Task AServerThatNeedsAConnectionButHasNoneSaysSo()
    {
        await using var r = await Rig.MakeAsync();
        await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote", ["transport"] = "http", ["url"] = Url, ["auth"] = new JsonObject { ["type"] = "oauth" } });
        var e = await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" }));
        Assert.Contains("remote needs a connection: set its sign-in in Settings → Integrations → MCP servers.", e.Message);
        Assert.Empty(r.Http.Requests);
    }

    [Fact]
    public async Task AskedRemoteCallRunsAfterApproval()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp();
        await r.RunAgent(run => Rig.Call(run, "remote__delete_all", new { _reason = "clean up" }));
        Assert.StartsWith("asked:", r.Seen);
        Assert.DoesNotContain("delete_all", r.Calls);
        var ask = Assert.Single(r.Svc.Asks(r.Team, pendingOnly: true));
        Assert.Equal("mcp:remote", ask.Integration);
        await r.Rpc("integrations.approve", new JsonObject { ["id"] = ask.Id });
        await Wait.UntilAsync(() => r.Calls.Contains("delete_all"), "the approved call");
        Assert.Equal("Bearer " + Token, r.Auth[^1]);
    }

    [Fact]
    public async Task RemoveCanDeleteAnUnusedConnection()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp();
        var res = await r.Rpc("integrations.mcpRemove", new JsonObject { ["name"] = "remote", ["removeConnection"] = true });
        Assert.True(res!["connectionRemoved"]!.GetValue<bool>());
        Assert.Empty(r.Conns.List());
    }

    [Fact]
    public async Task RemoveKeepsTheConnectionUnlessAskedOrStillUsed()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp();
        var id = Assert.Single(r.Conns.List()).Id;
        await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "other", ["transport"] = "http", ["url"] = Url, ["auth"] = new JsonObject { ["type"] = "bearer" }, ["connectionId"] = id });
        var res = await r.Rpc("integrations.mcpRemove", new JsonObject { ["name"] = "remote", ["removeConnection"] = true });
        Assert.False(res!["connectionRemoved"]!.GetValue<bool>()); // "other" still uses it
        Assert.Single(r.Conns.List());
        var keep = await r.Rpc("integrations.mcpRemove", new JsonObject { ["name"] = "other" });
        Assert.False(keep!["connectionRemoved"]!.GetValue<bool>());
        Assert.Single(r.Conns.List());
    }

    [Fact]
    public async Task AnEditedServersNewTokenIsNeverSentToItsOldAddress()
    {
        await using var r = await Rig.MakeAsync();
        const string Other = "https://other.example.test/mcp";
        r.Http.Add(Other, r.Serve);
        r.SessionId = "sess-1";
        await r.SaveHttp("bearer"); // the check opens a session at the old address
        await r.Rpc("integrations.mcpSave", new JsonObject
        {
            ["name"] = "remote", ["transport"] = "http", ["url"] = Other, ["auth"] = new JsonObject { ["type"] = "bearer" },
            ["secrets"] = new JsonObject { ["token"] = "tok_NEWTOKEN_zzzzzzzzzzzz" },
        });
        await Wait.UntilAsync(() => r.Deletes.Count > 0, "the old session being ended");
        var (url, auth, key) = Assert.Single(r.Deletes);
        Assert.Equal(Url, url);
        Assert.Equal("", auth);
        Assert.Equal("", key);
        Assert.DoesNotContain(r.Http.Requests, q => q.RequestUri!.AbsoluteUri == Url && q.Headers.TryGetValues("Authorization", out var a) && a.First().Contains("NEWTOKEN"));
    }

    [Fact]
    public async Task AForbiddenCallDoesNotMarkNeedsReconnect()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp();
        await r.Allow("echo", "allow");
        r.Forbid = true;
        await r.RunAgent(run => Rig.Call(run, "remote__echo", new { msg = "hi" }));
        Assert.Equal("remote refused this call (not allowed for this sign-in).", r.Seen);
        Assert.Equal("works", Assert.Single(r.Conns.List()).Status);
    }

    [Fact]
    public async Task AClientReplacedMidCallSaysTheServerWasChanged()
    {
        await using var r = await Rig.MakeAsync();
        await r.SaveHttp();
        await r.Allow("echo", "allow");
        r.Http.AddDelayed(Url, TimeSpan.FromHours(1));
        r.Http.Add(Url, r.Serve);
        var before = r.Http.Requests.Count;
        r.Script = async (run, _) =>
        {
            var call = Rig.Call(run, "remote__echo", new { msg = "hi" });
            await Wait.UntilAsync(() => r.Http.Requests.Count > before, "the call reaching the server");
            await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "remote" }); // replaces the client
            r.Seen = await call;
        };
        await r.Engine.PersonTellAsync("Alpha", "Ada", "go", ct: Ct);
        await Wait.UntilAsync(() => r.Done, "Ada's call");
        Assert.Equal("the MCP server remote was changed while this call ran; try again", r.Seen);
    }

    [Fact]
    public async Task AnAddressSecretStaysScrubbedAfterACall_AndTheListNeverShowsIt()
    {
        await using var r = await Rig.MakeAsync();
        const string Key = "KEYKEY_address_secret_98765";
        var keyed = Url + "?api_key=" + Key;
        r.Http.Add(keyed, r.Serve);
        await r.Rpc("integrations.mcpSave", new JsonObject
        {
            ["name"] = "remote", ["transport"] = "http", ["url"] = keyed, ["auth"] = new JsonObject { ["type"] = "bearer" },
            ["secrets"] = new JsonObject { ["token"] = Token },
        });
        await r.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "remote" });
        await r.Rpc("integrations.mcpEnable", new JsonObject { ["name"] = "remote", ["teamId"] = r.Team, ["on"] = true });
        await r.Allow("echo", "allow");
        await r.RunAgent(run => Rig.Call(run, "remote__echo", new { msg = Key }));
        Assert.NotNull(r.Seen);
        Assert.DoesNotContain(Key, r.Seen);
        Assert.DoesNotContain(Token, r.Seen);
        var list = (await r.Rpc("integrations.mcpList"))!.ToJsonString();
        Assert.DoesNotContain(Key, list);
        Assert.Contains("api_key=", list);
    }
}
