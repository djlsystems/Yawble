using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// Agents call an API through one tool per API, on the host, with the Connection's secret added there.
/// No network (FakeHttp), no OS credential store (MemoryCredentialStore), no real program.
/// </summary>
public class ApiCallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Token = "tok_SECRET_abcdef1234567890";
    private const string Base = "https://api.pets.example/v1";

    private const string Spec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Pets", "version": "1" },
          "servers": [ { "url": "https://api.pets.example/v1" } ],
          "security": [ { "b": [] } ],
          "components": { "securitySchemes": { "b": { "type": "http", "scheme": "bearer", "description": "Paste a token" } } },
          "paths": {
            "/pets": {
              "get": { "operationId": "listPets", "summary": "List pets",
                "parameters": [ { "name": "limit", "in": "query", "schema": { "type": "integer" } },
                                { "name": "X-Trace", "in": "header", "schema": { "type": "string" } } ] },
              "post": { "operationId": "createPet", "summary": "Create a pet",
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "type": "object", "properties": { "name": { "type": "string" } } } } } } }
            },
            "/pets/{petId}": {
              "parameters": [ { "name": "petId", "in": "path", "required": true, "schema": { "type": "string" } } ],
              "get": { "operationId": "getPet", "summary": "Get a pet" },
              "delete": { "operationId": "deletePet", "summary": "Delete a pet" }
            },
            "/tokens": { "get": { "operationId": "listTokens", "summary": "List API tokens" } },
            "/echo": { "get": { "operationId": "echo", "summary": "Echo",
                "parameters": [ { "name": "msg", "in": "query", "required": true, "schema": { "type": "string" } },
                                { "name": "host", "in": "header", "schema": { "type": "string" } },
                                { "name": "cookie", "in": "header", "schema": { "type": "string" } },
                                { "name": "x-note", "in": "header", "schema": { "type": "string" } } ] } }
          }
        }
        """;

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public BusEngine Engine = null!;
        public IntegrationService Svc = null!;
        public ConnectionService Conns = null!;
        public readonly FakeHttp Http = new();
        public long Alpha, Beta, ConnectionId;
        private readonly ConcurrentDictionary<int, (string Tool, object Args, TaskCompletionSource<string> Done)> _calls = new();
        private int _next;

        /// <summary>Alpha/Ada and Beta/Bo; the API "pets" discovered from the spec, offered to Alpha, signed in with a bearer Connection, and
        /// Alpha's checklist saved as the app suggests (GET allowed, POST ask first, DELETE off).</summary>
        public static async Task<Rig> Make()
        {
            var r = new Rig();
            r.Http.Add("https://api.pets.example/openapi.json", Spec);
            r.Engine = new BusEngine(r.Db.OpenStore(), new ScriptedRunner { Default = r.Handle }, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            r.Conns = new ConnectionService(new MemoryCredentialStore(), r.Db.ConnectionString);
            r.Svc = new IntegrationService(r.Db.ConnectionString, r.Engine, connections: r.Conns, httpHandler: () => r.Http, apiCallHandler: () => r.Http);
            r.Engine.Integrations = r.Svc;
            r.Alpha = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await r.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            r.Beta = await r.Engine.CreateTeamAsync("Beta", ct: Ct);
            await r.Engine.AddMemberAsync("Beta", "Bo", ct: Ct);
            await r.Engine.StartAsync(Ct);

            var conn = (await r.Rpc("integrations.connectionSave", new JsonObject
            {
                ["name"] = "Pets token",
                ["fields"] = new JsonArray(new JsonObject { ["key"] = "token", ["label"] = "Token", ["secret"] = true }),
                ["secrets"] = new JsonObject { ["token"] = Token },
            }))!;
            r.ConnectionId = conn["id"]!.GetValue<long>();
            await r.Rpc("integrations.apiSave", new JsonObject
            {
                ["name"] = "pets", ["baseUrl"] = Base, ["docsUrl"] = "https://api.pets.example/openapi.json", ["connectionId"] = r.ConnectionId,
            });
            await r.Discover();
            await r.Rpc("integrations.apiEnable", new JsonObject { ["name"] = "pets", ["teamId"] = r.Alpha });
            await r.Rpc("integrations.saveChecklist", new JsonObject
            {
                ["name"] = "pets", ["kind"] = "api", ["teamId"] = r.Alpha,
                ["choices"] = new JsonArray(
                    new JsonObject { ["path"] = "listPets", ["state"] = "allow" }, new JsonObject { ["path"] = "getPet", ["state"] = "allow" },
                    new JsonObject { ["path"] = "createPet", ["state"] = "ask" }, new JsonObject { ["path"] = "deletePet", ["state"] = "off" },
                    new JsonObject { ["path"] = "echo", ["state"] = "allow" }),
            });
            return r;
        }

        public async Task Discover()
        {
            var s = await Rpc("integrations.discover", new JsonObject { ["name"] = "pets", ["kind"] = "api" });
            Assert.True(s!["started"]!.GetValue<bool>());
            await Wait.UntilAsync(async () => (await Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "pets", ["kind"] = "api" }))!["state"]!.GetValue<string>() != "running", "the spec was read");
            Assert.Equal("done", (await Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "pets", ["kind"] = "api" }))!["state"]!.GetValue<string>());
        }

        public async Task<JsonNode?> Rpc(string method, JsonObject? p = null) => await Svc.DispatchAsync(method, p, Ct);

        private async Task<RunResult> Handle(RunContext run, CancellationToken ct)
        {
            // Every item of the batch: a call told while the member's previous run is still ending waits and can be batched
            // behind another pending message of the workflow (an approval notice, say).
            foreach (var (id, call) in _calls)
            {
                if (!run.Items.Any(m => m.Payload.Contains($"call#{id}#", StringComparison.Ordinal))) continue;
                _calls.TryRemove(id, out _);
                try { call.Done.TrySetResult(await CallTool(run, call.Tool, call.Args)); }
                catch (Exception e) { call.Done.TrySetException(e); }
            }
            return RunResult.Ok;
        }

        private static async Task<string> CallTool(RunContext run, string tool, object args)
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
            };
            var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
            var result = JsonNode.Parse(reply.Json!)!["result"];
            return result?["content"]?[0]?["text"]?.GetValue<string>() ?? JsonNode.Parse(reply.Json!)!["error"]?["message"]?.GetValue<string>() ?? reply.Json!;
        }

        /// <summary>The member makes the call in a run of its own; the text it reads.</summary>
        public async Task<string> CallAs(string team, string member, string tool, object args)
        {
            var id = Interlocked.Increment(ref _next);
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = (tool, args, done);
            await Engine.PersonTellAsync(team, member, $"call#{id}#", ct: Ct);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        public Task<string> Ada(object args) => CallAs("Alpha", "Ada", "api_pets", args);

        /// <summary>Whether the workflow holds a message containing this text (what the agent was told).</summary>
        public bool Told(long workflow, string text)
        {
            using var s = Db.OpenStore();
            return s.Workflow(workflow).Any(m => m.Payload.Contains(text, StringComparison.Ordinal));
        }

        public void Exec(string sql)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public long Scalar(string sql)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public string Text(string sql)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToString(cmd.ExecuteScalar()) ?? "";
        }

        /// <summary>Everything the app wrote down: calls, asks, connections and APIs (to check no secret got there).</summary>
        public string AllStoredText()
        {
            var sb = new StringBuilder();
            foreach (var sql in new[] { "SELECT args || ifnull(rule, '') FROM integration_calls", "SELECT args || reason FROM integration_asks",
                         "SELECT fields || ifnull(status_reason, '') FROM connections", "SELECT ifnull(auth, '') || ifnull(status, '') FROM apis" })
            {
                using var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString);
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) sb.Append(rd.GetString(0)).Append('\n');
            }
            return sb.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await Wait.UntilAsync(() => Engine.IsQuiescentAsync(Ct), "the runs ended");
            await Svc.DisposeAsync();
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Tool_ListedForOfferedTeamOnly()
    {
        await using var r = await Rig.Make();
        var tool = Assert.Single(r.Svc.ToolsFor(r.Alpha), t => t["name"]!.GetValue<string>() == "api_pets");
        Assert.DoesNotContain(r.Svc.ToolsFor(r.Beta), t => t["name"]!.GetValue<string>() == "api_pets");
        Assert.True(r.Svc.IsTool("api_pets"));
        var description = tool["description"]!.GetValue<string>();
        Assert.Contains("never", description); // never gets the sign-in
        Assert.Contains("listPets — List pets", description);
        Assert.Contains("createPet — Create a pet", description); // asks first: still listed
        Assert.DoesNotContain("deletePet", description); // off
        Assert.DoesNotContain("listTokens", description); // a sign-in endpoint: never
        var props = (JsonObject)tool["inputSchema"]!["properties"]!;
        Assert.Equal(["action", "params", "_reason"], props.Select(p => p.Key));
        Assert.DoesNotContain(Token, tool.ToJsonString());

        // Bo's team is not offered it: his call does not reach the service
        var bo = await r.CallAs("Beta", "Bo", "api_pets", new { action = "listPets", @params = new { } });
        Assert.DoesNotContain("HTTP 200", bo);
        Assert.DoesNotContain(r.Http.Urls, u => u.StartsWith(Base, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_AllowedByDefault_SendsBearer_ReturnsScrubbedBody()
    {
        await using var r = await Rig.Make();
        r.Http.Add(Base + "/pets?limit=2", req => Json(HttpStatusCode.OK,
            $"{{\"pets\":[\"Rex\"],\"echo\":\"{req.Headers.Authorization}\",\"trace\":\"{string.Join(",", req.Headers.TryGetValues("X-Trace", out var v) ? v : [])}\"}}"));
        var text = await r.Ada(new JsonObject { ["action"] = "listPets", ["params"] = new JsonObject { ["limit"] = 2, ["X-Trace"] = "t-1" }, ["_reason"] = "see the pets" });

        var sent = r.Http.Requests.Last();
        Assert.Equal(Base + "/pets?limit=2", sent.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer " + Token, sent.Headers.Authorization!.ToString());
        Assert.Equal("t-1", Assert.Single(sent.Headers.GetValues("X-Trace")));
        Assert.StartsWith("HTTP 200 OK\napplication/json", text);
        Assert.Contains("Rex", text);
        Assert.Contains("[redacted]", text);
        Assert.DoesNotContain(Token, text);
        Assert.Equal(1, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:pets' AND decision = 'allow'"));
        Assert.Equal(200, r.Scalar("SELECT exit_code FROM integration_calls WHERE integration = 'api:pets'"));
        Assert.DoesNotContain(Token, r.AllStoredText());
    }

    [Fact]
    public async Task QueryAndBasicSignIns_AddedByTheHost_EchoesScrubbed()
    {
        await using var r = await Rig.Make();
        const string key = "qk_live_0123456789abcdef";
        var qc = (await r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = "Query key", ["fields"] = new JsonArray(new JsonObject { ["key"] = "api_key", ["label"] = "API key", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["api_key"] = key },
        }))!["id"]!.GetValue<long>();
        await r.Rpc("integrations.apiSave", new JsonObject
        {
            ["name"] = "pets", ["baseUrl"] = Base, ["connectionId"] = qc, ["auth"] = new JsonObject { ["type"] = "query", ["name"] = "api_key" },
        });
        r.Http.Add(Base + "/pets?api_key=" + key, req => Json(HttpStatusCode.OK, $"{{\"url\":\"{req.RequestUri!.AbsoluteUri}\"}}"));
        var text = await r.Ada(new { action = "listPets", @params = new { } });
        Assert.StartsWith("HTTP 200", text);
        Assert.DoesNotContain(key, text);
        Assert.Null(r.Http.Requests.Last().Headers.Authorization);

        const string password = "pw_very_secret_987654";
        var bc = (await r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = "Basic", ["fields"] = new JsonArray(new JsonObject { ["key"] = "username", ["label"] = "Username", ["secret"] = false, ["value"] = "ada" },
                new JsonObject { ["key"] = "password", ["label"] = "Password", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["password"] = password },
        }))!["id"]!.GetValue<long>();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["baseUrl"] = Base, ["connectionId"] = bc, ["auth"] = new JsonObject { ["type"] = "basic" } });
        r.Http.Add(Base + "/pets", req => Json(HttpStatusCode.OK, $"{{\"auth\":\"{req.Headers.Authorization!.Parameter}\"}}"));
        var basic = await r.Ada(new { action = "listPets", @params = new { } });
        var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes("ada:" + password));
        Assert.Equal("Basic " + pair, r.Http.Requests.Last().Headers.Authorization!.ToString());
        Assert.DoesNotContain(pair, basic);
        Assert.Contains("[redacted]", basic);
        Assert.DoesNotContain(key, r.AllStoredText());
        Assert.DoesNotContain(password, r.AllStoredText());
    }

    [Fact]
    public async Task Post_Asks_ThenApproveAlways_RemembersAndRuns()
    {
        await using var r = await Rig.Make();
        string? body = null;
        string? auth = null;
        r.Http.Add(Base + "/pets", req =>
        {
            body = req.Content?.ReadAsStringAsync().Result;
            auth = req.Headers.Authorization?.ToString();
            return req.Method == HttpMethod.Post ? Json(HttpStatusCode.Created, "{\"id\":7}") : Json(HttpStatusCode.OK, "[]");
        });
        var asked = await r.Ada(new { action = "createPet", @params = new { body = new { name = "Rex" } }, _reason = "add Rex" });
        Assert.StartsWith("asked:", asked);
        Assert.Null(body); // nothing sent yet
        var ask = Assert.Single(r.Svc.Asks(r.Alpha, pendingOnly: true));
        Assert.Equal("api:pets", ask.Integration);
        Assert.Equal("createPet", ask.Action);
        Assert.Equal("createPet", ask.Args[0]);
        var shown = (JsonObject)Assert.Single((JsonArray)(await r.Rpc("integrations.asks", new JsonObject { ["teamId"] = r.Alpha }))!)!;
        Assert.True(shown["canAlways"]!.GetValue<bool>());
        Assert.Contains("createPet", shown["command"]!.GetValue<string>());

        var approved = await r.Rpc("integrations.approve", new JsonObject { ["id"] = ask.Id, ["remember"] = "always" });
        Assert.Equal("createPet", approved!["remembered"]!.GetValue<string>());
        Assert.True(approved["started"]!.GetValue<bool>()); // sent in the background: the click returns at once
        await Wait.UntilAsync(() => r.Told(ask.Workflow, "HTTP 201"), "Ada told the result");
        Assert.Equal("{\"name\":\"Rex\"}", body);
        Assert.Equal("Bearer " + Token, auth);
        Assert.Equal(1, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:pets' AND decision = 'approved' AND exit_code = 201"));

        // remembered: the next one runs at once
        body = null;
        var again = await r.Ada(new { action = "createPet", @params = new { body = new { name = "Max" } } });
        Assert.StartsWith("HTTP 201", again);
        Assert.Equal("{\"name\":\"Max\"}", body);
        Assert.DoesNotContain(Token, r.AllStoredText());
    }

    [Fact]
    public async Task Decline_Never_TurnsTheActionOff()
    {
        await using var r = await Rig.Make();
        Assert.StartsWith("asked:", await r.Ada(new { action = "createPet", @params = new { body = new { name = "Rex" } }, _reason = "add Rex" }));
        var ask = Assert.Single(r.Svc.Asks(r.Alpha, pendingOnly: true));
        var declined = await r.Rpc("integrations.decline", new JsonObject { ["id"] = ask.Id, ["remember"] = "never" });
        Assert.Equal("createPet", declined!["remembered"]!.GetValue<string>());
        Assert.StartsWith("refused:", await r.Ada(new { action = "createPet", @params = new { body = new { name = "Rex" } }, _reason = "again" }));
        Assert.DoesNotContain(r.Http.Requests, q => q.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Delete_Off_Refused()
    {
        await using var r = await Rig.Make();
        var text = await r.Ada(new { action = "deletePet", @params = new { petId = "1" }, _reason = "clean up" });
        Assert.StartsWith("refused:", text);
        Assert.DoesNotContain(r.Http.Requests, q => q.Method == HttpMethod.Delete);
        Assert.Equal(1, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:pets' AND decision = 'deny'"));

        // a sign-in endpoint is refused whatever the rules say
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(r.Db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO api_rules (api_id, team_id, pattern, decision, created_at) SELECT id, NULL, 'listTokens', 'allow', 'now' FROM apis WHERE name = 'pets';";
            cmd.ExecuteNonQuery();
        }
        Assert.StartsWith("refused:", await r.Ada(new { action = "listTokens", @params = new { } }));
        Assert.DoesNotContain(r.Http.Urls, u => u.Contains("/tokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownAction_Refused()
    {
        await using var r = await Rig.Make();
        var text = await r.Ada(new { action = "stealEverything", @params = new { } });
        Assert.Contains("no action 'stealEverything'; use action list", text);
        // a parameter the action does not have is refused too, before anything is sent
        var extra = await r.Ada(new { action = "listPets", @params = new { evil = "1" } });
        Assert.Contains("evil", extra);
        Assert.DoesNotContain(r.Http.Urls, u => u.StartsWith(Base, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PathParam_CannotEscapeHost()
    {
        await using var r = await Rig.Make();
        await r.Ada(new { action = "getPet", @params = new { petId = "../../admin" } });
        Assert.Equal(Base + "/pets/..%2F..%2Fadmin", r.Http.Requests.Last().RequestUri!.AbsoluteUri);
        await r.Ada(new { action = "getPet", @params = new { petId = "https://evil.example/x" } });
        var last = r.Http.Requests.Last().RequestUri!;
        Assert.Equal("api.pets.example", last.Host);
        Assert.Equal(Base + "/pets/https%3A%2F%2Fevil.example%2Fx", last.AbsoluteUri);
        var count = r.Http.Requests.Count;
        Assert.StartsWith("refused:", await r.Ada(new { action = "getPet", @params = new { petId = ".." } })); // a dot segment would climb out
        Assert.Equal(count, r.Http.Requests.Count);
        Assert.All(r.Http.Requests.Skip(1), q => Assert.Equal("api.pets.example", q.RequestUri!.Host));
        Assert.All(r.Http.Requests.Skip(1), q => Assert.StartsWith("/v1/", q.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task RedirectToOtherHost_NotFollowed()
    {
        await using var r = await Rig.Make();
        r.Http.Add(Base + "/pets/1", _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/steal?t=" + Token) } });
        var text = await r.Ada(new { action = "getPet", @params = new { petId = "1" } });
        Assert.StartsWith("HTTP 302", text);
        Assert.Contains("https://evil.example/steal", text);
        Assert.DoesNotContain(Token, text); // the location is scrubbed too
        Assert.DoesNotContain(r.Http.Urls, u => u.Contains("evil.example", StringComparison.Ordinal));

        // the same host: followed once, with the same sign-in
        r.Http.Add(Base + "/pets/2", _ => new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("/v1/pets/3", UriKind.Relative) } });
        r.Http.Add(Base + "/pets/3", req => Json(HttpStatusCode.OK, req.Headers.Authorization is null ? "{\"auth\":false}" : "{\"pet\":3}"));
        var moved = await r.Ada(new { action = "getPet", @params = new { petId = "2" } });
        Assert.StartsWith("HTTP 200", moved);
        Assert.Contains("{\"pet\":3}", moved);
    }

    [Fact]
    public async Task Unauthorized_MarksNeedsReconnect_TestClearsIt()
    {
        await using var r = await Rig.Make();
        r.Http.Add(Base + "/pets", "{\"error\":\"bad token\"}", status: HttpStatusCode.Unauthorized);
        var text = await r.Ada(new { action = "listPets", @params = new { } });
        Assert.StartsWith("HTTP 401", text);
        Assert.Contains("The connection 'Pets token' needs reconnecting: it now shows under Needs You for the person. Stop using pets until it is reconnected.", text);
        var attention = (JsonArray)(await r.Rpc("integrations.connectionAttention"))!;
        Assert.Equal("Pets token", attention.Single()!["name"]!.GetValue<string>());
        Assert.Equal("Settings → Integrations → Connections", attention.Single()!["where"]!.GetValue<string>());
        Assert.Contains("401 Unauthorized", attention.Single()!["reason"]!.GetValue<string>());
        var view = r.Conns.Get(r.ConnectionId)!;
        Assert.Equal("needs_reconnect", view.Status);
        Assert.Contains("401 Unauthorized", view.StatusReason);
        var listed = (JsonObject)((JsonArray)(await r.Rpc("integrations.apiList"))!).Single()!;
        Assert.Equal("needs_reconnect", listed["connection"]!["status"]!.GetValue<string>()); // the API card shows it

        r.Http.Add(Base + "/pets", "[]");
        var test = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = r.ConnectionId }))!;
        Assert.Equal("works", test["status"]!.GetValue<string>());
        Assert.Equal("works", r.Conns.Get(r.ConnectionId)!.Status);
        Assert.Equal(Base + "/pets", r.Http.Requests.Last().RequestUri!.AbsoluteUri); // the first allowed GET with nothing required
        Assert.Equal(0, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:pets' AND decision <> 'allow'"));

        r.Http.Add(Base + "/pets", "{}", status: HttpStatusCode.Forbidden);
        Assert.Equal("needs_reconnect", (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = r.ConnectionId }))!["status"]!.GetValue<string>());

        // a connection nothing can be tested against
        var lone = (await r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = "Unused", ["fields"] = new JsonArray(new JsonObject { ["key"] = "token", ["label"] = "Token", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["token"] = "another_secret_value" },
        }))!["id"]!.GetValue<long>();
        var none = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = lone }))!;
        Assert.Equal("untested", none["status"]!.GetValue<string>());
        Assert.Equal("nothing to test against yet; status unchanged", none["reason"]!.GetValue<string>());
        Assert.False(none["changed"]!.GetValue<bool>());
    }

    /// <summary>A Test that proves nothing (a 5xx, a timeout, no answer) leaves the status as it was; only a 2xx clears "needs reconnect".</summary>
    [Theory]
    [InlineData("5xx")]
    [InlineData("timeout")]
    [InlineData("network")]
    public async Task Test_Inconclusive_LeavesTheStatusUnchanged(string how)
    {
        await using var r = await Rig.Make();
        r.Conns.MarkStatus(r.ConnectionId, "needs_reconnect", "the service answered 401 Unauthorized earlier");
        switch (how)
        {
            case "5xx": r.Http.Add(Base + "/pets", "{}", status: HttpStatusCode.InternalServerError); break;
            case "timeout": r.Svc.ApiTimeout = TimeSpan.FromMilliseconds(200); r.Http.AddDelayed(Base + "/pets", TimeSpan.FromSeconds(20)); break;
            default: r.Http.Add(Base + "/pets", _ => throw new HttpRequestException("no route to the host")); break;
        }
        var test = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = r.ConnectionId }))!;
        Assert.Equal("needs_reconnect", test["status"]!.GetValue<string>());
        Assert.False(test["changed"]!.GetValue<bool>());
        var reason = test["reason"]!.GetValue<string>();
        Assert.StartsWith("the service did not answer clearly (", reason);
        Assert.EndsWith("); status unchanged", reason);
        Assert.Contains(how switch { "5xx" => "500 Internal Server Error", "timeout" => "timed out", _ => "no route to the host" }, reason);
        var after = r.Conns.Get(r.ConnectionId)!;
        Assert.Equal("needs_reconnect", after.Status);
        Assert.Equal("the service answered 401 Unauthorized earlier", after.StatusReason);
    }

    /// <summary>Removing an API records its pending asks as cancelled ("API removed") and deletes them, so a new API by the same
    /// name never runs an old request.</summary>
    [Fact]
    public async Task RemovingAnApi_CancelsItsPendingAsks_AndANewOneByTheNameRunsNone()
    {
        await using var r = await Rig.Make();
        var asked = await r.Ada(new { action = "createPet", @params = new { body = new { name = "Rex" } }, _reason = "add Rex" });
        Assert.StartsWith("asked:", asked);
        var ask = Assert.Single(r.Svc.Asks(r.Alpha, pendingOnly: true));

        await r.Rpc("integrations.apiRemove", new JsonObject { ["name"] = "pets" });

        Assert.Empty(r.Svc.Asks(null, pendingOnly: false));
        Assert.Equal(0, r.Svc.PendingCount());
        Assert.Equal(1, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:pets' AND decision = 'cancelled' AND rule = 'API removed' AND team = 'Alpha'"));
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["baseUrl"] = Base, ["connectionId"] = r.ConnectionId });
        await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.approve", new JsonObject { ["id"] = ask.Id }));
        Assert.DoesNotContain(r.Http.Requests, q => q.Method == HttpMethod.Post);
    }

    /// <summary>M5: "Add API" (create: true) with a blank name whose host is already an API is refused like a typed duplicate.</summary>
    [Fact]
    public async Task AddApi_BlankNameFromAnExistingHost_IsRefused()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.apiSave", new JsonObject { ["baseUrl"] = "https://api.other.example" });
        var blank = await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.apiSave", new JsonObject { ["baseUrl"] = "https://api.other.example/v2", ["create"] = true }));
        Assert.Equal("an API named api.other.example already exists; edit it instead", blank.Message);
        var typed = await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "PETS", ["baseUrl"] = Base, ["create"] = true }));
        Assert.Equal("an API named pets already exists; edit it instead", typed.Message);
        Assert.Equal("https://api.other.example", r.Text("SELECT base_url FROM apis WHERE name = 'api.other.example'")); // not edited
        // without create (Edit), the same call updates it
        await r.Rpc("integrations.apiSave", new JsonObject { ["baseUrl"] = "https://api.other.example/v2" });
        Assert.Equal("https://api.other.example/v2", r.Text("SELECT base_url FROM apis WHERE name = 'api.other.example'"));
    }

    /// <summary>An API whose spec names no sign-in gets the person's choice (saved through apiSave's auth, marked chosen), then the normal
    /// sign-in box; "None" clears it.</summary>
    [Fact]
    public async Task NoSignInInTheSpec_ThePersonChoosesOne()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "bare", ["baseUrl"] = "https://bare.example" });
        JsonObject Bare(JsonNode? list) => (JsonObject)((JsonArray)list!).Single(a => a!["name"]!.GetValue<string>() == "bare")!;
        var row = Bare(await r.Rpc("integrations.apiList"));
        Assert.Null(row["auth"]);
        Assert.Null(row["signIn"]);

        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "bare", ["auth"] = new JsonObject { ["type"] = "header", ["name"] = "X-API-Key" } });
        row = Bare(await r.Rpc("integrations.apiList"));
        Assert.True(row["auth"]!["chosen"]!.GetValue<bool>());
        Assert.Equal("X-API-Key", row["auth"]!["name"]!.GetValue<string>());
        Assert.Equal("X-API-Key", row["signIn"]!["fields"]![0]!["key"]!.GetValue<string>());

        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "bare", ["auth"] = new JsonObject { ["type"] = "query", ["name"] = "api_key" } });
        Assert.Equal("query", Bare(await r.Rpc("integrations.apiList"))["auth"]!["type"]!.GetValue<string>());
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "bare", ["auth"] = new JsonObject { ["type"] = "bearer" } });
        Assert.Equal("token", Bare(await r.Rpc("integrations.apiList"))["signIn"]!["fields"]![0]!["key"]!.GetValue<string>());
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "bare", ["auth"] = null });
        Assert.Null(Bare(await r.Rpc("integrations.apiList"))["auth"]);
        // the spec's own sign-in is not marked chosen
        var pets = (JsonObject)((JsonArray)(await r.Rpc("integrations.apiList"))!).Single(a => a!["name"]!.GetValue<string>() == "pets")!;
        Assert.False(pets["auth"]!["chosen"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Test_Inconclusive_KeepsWorks()
    {
        await using var r = await Rig.Make();
        r.Http.Add(Base + "/pets", "[]");
        Assert.Equal("works", (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = r.ConnectionId }))!["status"]!.GetValue<string>());
        r.Http.Add(Base + "/pets", "{}", status: HttpStatusCode.BadGateway);
        var test = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = r.ConnectionId }))!;
        Assert.Equal("works", test["status"]!.GetValue<string>());
        Assert.Equal("works", r.Conns.Get(r.ConnectionId)!.Status);
    }

    /// <summary>A Connection the service refused is counted in Needs You and listed with where to fix it; a passing Test takes it out.</summary>
    [Fact]
    public async Task NeedsReconnect_ReachesNeedsYou_AndAChangeIsAnnounced()
    {
        await using var r = await Rig.Make();
        var changes = 0;
        r.Conns.Changed = () => Interlocked.Increment(ref changes);
        Assert.Empty(r.Svc.ConnectionsNeedingReconnect());
        r.Http.Add(Base + "/pets", "{}", status: HttpStatusCode.Unauthorized);
        await r.Ada(new { action = "listPets", @params = new { } });
        var item = Assert.Single(r.Svc.ConnectionsNeedingReconnect());
        Assert.Equal("Pets token", item.Name);
        Assert.True(changes >= 1);
        var before = changes;
        r.Http.Add(Base + "/pets", "[]");
        await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = r.ConnectionId });
        Assert.Empty(r.Svc.ConnectionsNeedingReconnect());
        Assert.True(changes > before);
    }

    /// <summary>A connection made in the Connections tab (key "token") signs an API whose sign-in is an X-API-Key header, and pasting the
    /// same key in the API dialog reuses it instead of making a second one.</summary>
    [Fact]
    public async Task CrossTab_TokenConnection_SignsAHeaderApi_AndIsReused()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["auth"] = new JsonObject { ["type"] = "header", ["name"] = "X-API-Key" } });
        var pasted = (await r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = "pets key",
            ["fields"] = new JsonArray(new JsonObject { ["key"] = "X-API-Key", ["label"] = "API key", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["X-API-Key"] = Token },
            ["reuseIdentical"] = true,
        }))!;
        Assert.True(pasted["reused"]!.GetValue<bool>());
        Assert.Equal(r.ConnectionId, pasted["id"]!.GetValue<long>());
        Assert.Single(r.Conns.List());

        r.Http.Add(Base + "/pets", "[]");
        var text = await r.Ada(new { action = "listPets", @params = new { } });
        Assert.StartsWith("HTTP 200", text);
        var sent = r.Http.Requests.Last();
        Assert.Equal(Token, sent.Headers.GetValues("X-API-Key").Single());
        Assert.False(sent.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task SeveralSecrets_NoneByTheExpectedKey_IsRefusedNamingTheConnection()
    {
        await using var r = await Rig.Make();
        var two = (await r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = "Two keys",
            ["fields"] = new JsonArray(new JsonObject { ["key"] = "a", ["label"] = "A", ["secret"] = true }, new JsonObject { ["key"] = "b", ["label"] = "B", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["a"] = "first_secret_value_1", ["b"] = "second_secret_value_2" },
        }))!["id"]!.GetValue<long>();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["connectionId"] = two, ["auth"] = new JsonObject { ["type"] = "header", ["name"] = "X-API-Key" } });
        var count = r.Http.Requests.Count;
        var text = await r.Ada(new { action = "listPets", @params = new { } });
        Assert.Equal("refused: the connection 'Two keys' holds 2 keys and none of them is the one pets signs in with ('X-API-Key'); " +
            "ask the person to pick a connection with one key in Settings → Integrations → APIs", text);
        Assert.Equal(count, r.Http.Requests.Count);
    }

    [Fact]
    public async Task ConnectionRemoved_CallSaysNeedsConnection()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.connectionRemove", new JsonObject { ["id"] = r.ConnectionId, ["confirm"] = true });
        var text = await r.Ada(new { action = "listPets", @params = new { } });
        Assert.Equal("refused: pets needs a connection; ask the person to pick one in Settings → Integrations → APIs", text);
        Assert.DoesNotContain(r.Http.Urls, u => u.StartsWith(Base, StringComparison.Ordinal));
        Assert.Equal(1, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:pets'"));
    }

    [Fact]
    public async Task ListAndDescribe_NotLoggedAsCalls()
    {
        await using var r = await Rig.Make();
        var list = await r.Ada(new { action = "list", @params = new { filter = "pet" } });
        Assert.Contains("listPets", list);
        Assert.Contains("getPet", list);
        Assert.DoesNotContain("listTokens", list);
        var describe = await r.Ada(new { action = "describe", @params = new { action = "getPet" } });
        Assert.Contains("petId", describe);
        Assert.Contains("\"required\"", describe);
        Assert.Equal(0, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:pets'"));
        Assert.DoesNotContain(r.Http.Urls, u => u.StartsWith(Base, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApiSave_UpdateWithoutDocsUrl_KeepsTheDocsLink()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["baseUrl"] = Base });
        Assert.Equal("https://api.pets.example/openapi.json", r.Text("SELECT docs_url FROM apis WHERE name = 'pets'"));
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["baseUrl"] = Base, ["docsUrl"] = null });
        Assert.Equal("", r.Text("SELECT docs_url FROM apis WHERE name = 'pets'")); // present and null: cleared
    }

    [Fact]
    public async Task ApiSave_RefusesANameThatCollidesWithACliOrMcpToolName()
    {
        await using var r = await Rig.Make();
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(r.Db.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO integrations (kind, name, created_at, updated_at) VALUES ('cli', 'api_stuff', 'now', 'now');";
            cmd.ExecuteNonQuery();
        }
        var cli = await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "stuff", ["baseUrl"] = "https://stuff.example" }));
        Assert.Contains("api_stuff", cli.Message);
        await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "my--thing", ["baseUrl"] = "https://thing.example" }));
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "my-thing", ["baseUrl"] = "https://thing.example" }); // one underscore is fine
    }

    // ------------------------------------------------------------------ fix round 1

    [Fact]
    public async Task RedirectOnTheSameHost_ToASignInRouteOrOutsideTheBasePath_NotFollowed()
    {
        await using var r = await Rig.Make();
        r.Http.Add(Base + "/pets/5", _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/v1/tokens", UriKind.Relative) } });
        r.Http.Add(Base + "/tokens", "{\"tokens\":[\"leak\"]}");
        var tokens = await r.Ada(new { action = "getPet", @params = new { petId = "5" } });
        Assert.StartsWith("HTTP 302", tokens);
        Assert.Contains("/v1/tokens (a sign-in address: not followed)", tokens);
        Assert.DoesNotContain("leak", tokens);

        r.Http.Add(Base + "/pets/6", _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/admin", UriKind.Relative) } });
        r.Http.Add("https://api.pets.example/admin", "{\"admin\":true}");
        var admin = await r.Ada(new { action = "getPet", @params = new { petId = "6" } });
        Assert.StartsWith("HTTP 302", admin);
        Assert.Contains("https://api.pets.example/admin (outside the service URL: not followed)", admin);
        Assert.DoesNotContain(r.Http.Urls, u => u.EndsWith("/tokens", StringComparison.Ordinal) || u.EndsWith("/admin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RedirectOfAPost_NotFollowed()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "pets", ["kind"] = "api", ["teamId"] = r.Alpha, ["choices"] = new JsonArray(new JsonObject { ["path"] = "createPet", ["state"] = "allow" }),
        });
        r.Http.Add(Base + "/pets", _ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("/v1/pets/1", UriKind.Relative) } });
        var text = await r.Ada(new { action = "createPet", @params = new { body = new { name = "Rex" } } });
        Assert.StartsWith("HTTP 307", text);
        Assert.Contains("(redirect of a POST: not followed)", text);
        Assert.DoesNotContain(r.Http.Urls, u => u == Base + "/pets/1");
    }

    [Fact]
    public async Task RedirectOfAGet_OntoAnActionThatIsOff_NotFollowed()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "pets", ["kind"] = "api", ["teamId"] = r.Alpha, ["choices"] = new JsonArray(new JsonObject { ["path"] = "getPet", ["state"] = "off" }),
        });
        r.Http.Add(Base + "/pets", _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/v1/pets/7", UriKind.Relative) } });
        r.Http.Add(Base + "/pets/7", "{\"pet\":7}");
        var text = await r.Ada(new { action = "listPets", @params = new { } });
        Assert.StartsWith("HTTP 302", text);
        Assert.Contains("/v1/pets/7 (getPet is not allowed for your team: not followed)", text);
        Assert.DoesNotContain(r.Http.Urls, u => u == Base + "/pets/7");
    }

    [Fact]
    public async Task McpApproveTwiceAtOnce_OnlyOneWins()
    {
        // no server row: the winning approval fails before anything could start, so no program ever runs
        await using var r = await Rig.Make();
        r.Exec($"INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, action) " +
               $"VALUES ('now', 'mcp:ghost', {r.Alpha}, 'Alpha/Ada', '[\"echo\",\"{{}}\"]', 'testing', 1, 1, 'echo');");
        var id = r.Scalar("SELECT id FROM integration_asks WHERE integration = 'mcp:ghost'");
        var both = new[] { 0, 1 }.Select(_ => Task.Run(async () =>
        {
            try { await r.Rpc("integrations.approve", new JsonObject { ["id"] = id }); return "ran"; }
            catch (RpcException e) { return e.Message; }
        })).ToArray();
        var results = await Task.WhenAll(both);
        Assert.Single(results, m => m.Contains("is no longer there", StringComparison.Ordinal)); // the one that won went on to run
        Assert.Single(results, m => m.Contains("is not waiting", StringComparison.Ordinal));
        Assert.Equal("approved", r.Text($"SELECT state FROM integration_asks WHERE id = {id}"));
    }

    [Fact]
    public async Task ApproveAlways_OnASignInAction_LeavesTheRequestWaiting_AndSavesNothing()
    {
        await using var r = await Rig.Make();
        r.Exec($"INSERT INTO integration_asks (at, integration, team_id, member, args, reason, workflow, causation_seq, action) " +
               $"VALUES ('now', 'api:pets', {r.Alpha}, 'Alpha/Ada', '[\"listTokens\",\"{{}}\"]', 'testing', 1, 1, 'listTokens');");
        var id = r.Scalar("SELECT id FROM integration_asks WHERE action = 'listTokens'");
        var refused = await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.approve", new JsonObject { ["id"] = id, ["remember"] = "always" }));
        Assert.Contains("locked", refused.Message);
        Assert.Equal("pending", r.Text($"SELECT state FROM integration_asks WHERE id = {id}"));
        Assert.Equal(0, r.Scalar("SELECT count(*) FROM api_rules WHERE pattern = 'listTokens'"));
        Assert.DoesNotContain(r.Http.Urls, u => u.Contains("/tokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApproveTwiceAtOnce_SendsOnce()
    {
        await using var r = await Rig.Make();
        var posts = 0;
        r.Http.Add(Base + "/pets", req => { if (req.Method == HttpMethod.Post) Interlocked.Increment(ref posts); return Json(HttpStatusCode.Created, "{}"); });
        Assert.StartsWith("asked:", await r.Ada(new { action = "createPet", @params = new { body = new { name = "Rex" } }, _reason = "add Rex" }));
        var ask = Assert.Single(r.Svc.Asks(r.Alpha, pendingOnly: true));
        var both = new[] { 0, 1 }.Select(_ => Task.Run(async () =>
        {
            try { await r.Rpc("integrations.approve", new JsonObject { ["id"] = ask.Id }); return true; }
            catch (RpcException e) when (e.Message.Contains("is not waiting")) { return false; }
        })).ToArray();
        var results = await Task.WhenAll(both);
        Assert.Single(results, ok => ok);
        await Wait.UntilAsync(() => r.Told(ask.Workflow, "HTTP 201"), "Ada told the result");
        Assert.Equal(1, posts);
        // and after it was decided, approving again is refused without sending
        await Assert.ThrowsAsync<RpcException>(() => r.Rpc("integrations.approve", new JsonObject { ["id"] = ask.Id }));
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task ApproveAfterTheConnectionWasRemoved_SaysItNeedsAConnection()
    {
        await using var r = await Rig.Make();
        Assert.StartsWith("asked:", await r.Ada(new { action = "createPet", @params = new { body = new { name = "Rex" } }, _reason = "add Rex" }));
        var ask = Assert.Single(r.Svc.Asks(r.Alpha, pendingOnly: true));
        await r.Rpc("integrations.connectionRemove", new JsonObject { ["id"] = r.ConnectionId, ["confirm"] = true });
        var approved = await r.Rpc("integrations.approve", new JsonObject { ["id"] = ask.Id });
        Assert.True(approved!["ok"]!.GetValue<bool>());
        await Wait.UntilAsync(() => r.Told(ask.Workflow, "pets needs a connection"), "Ada told it needs a connection");
        Assert.DoesNotContain(r.Http.Requests, q => q.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ReservedHeaderParams_AnyCase_AreDropped_AndALineBreakIsRefused()
    {
        await using var r = await Rig.Make();
        // a spec could declare an authorization header itself; the reader drops it, so put it in the stored schema to be sure the call drops it too
        r.Exec("""
            UPDATE api_commands SET params = json_set(params, '$.properties.authorization', json('{"type":"string","x-in":"header","x-name":"authorization"}'))
            WHERE path = 'echo';
            """);
        r.Http.Add(Base + "/echo?msg=hi", "{}");
        var text = await r.Ada(new JsonObject
        {
            ["action"] = "echo",
            ["params"] = new JsonObject { ["msg"] = "hi", ["host"] = "evil.example", ["cookie"] = "s=1", ["authorization"] = "Bearer agents-own", ["x-note"] = "kept" },
        });
        Assert.StartsWith("HTTP 200", text);
        var sent = r.Http.Requests.Last();
        Assert.Equal("Bearer " + Token, sent.Headers.Authorization!.ToString()); // the app's sign-in, not the agent's
        Assert.Null(sent.Headers.Host);
        Assert.False(sent.Headers.Contains("cookie"));
        Assert.Equal("kept", Assert.Single(sent.Headers.GetValues("x-note")));

        var count = r.Http.Requests.Count;
        var crlf = await r.Ada(new JsonObject { ["action"] = "echo", ["params"] = new JsonObject { ["msg"] = "hi", ["x-note"] = "a\r\nX-Evil: 1" } });
        Assert.StartsWith("refused:", crlf);
        Assert.Contains("line break", crlf);
        Assert.Equal(count, r.Http.Requests.Count);
    }

    [Fact]
    public async Task Timeout_GivesAPlainMessage()
    {
        await using var r = await Rig.Make();
        r.Svc.ApiTimeout = TimeSpan.FromMilliseconds(200);
        r.Http.AddDelayed(Base + "/pets", TimeSpan.FromSeconds(20));
        var text = await r.Ada(new { action = "listPets", @params = new { } });
        Assert.Equal("the call to pets timed out after 0.2 s (stopped)", text);
        Assert.Equal(1, r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'api:pets' AND exit_code = -1"));
    }

    [Fact]
    public async Task AHugeBody_IsReadOnlyUpToTheCap_AndCut()
    {
        await using var r = await Rig.Make();
        r.Http.AddGenerated(Base + "/pets", 8_000_000, "text/plain");
        var text = await r.Ada(new { action = "listPets", @params = new { } });
        Assert.StartsWith("HTTP 200 OK\ntext/plain", text);
        Assert.EndsWith("(output cut at 64 KB)", text);
        Assert.True(text.Length < CliRunner.MaxOutputChars + 200);
        Assert.InRange(r.Http.BytesServed, 1, 1024 * 1024 + 128 * 1024); // about 1 MB read, never the 8 MB

        r.Http.AddGenerated(Base + "/pets/9", 8_000_000, "application/octet-stream");
        var binary = await r.Ada(new { action = "getPet", @params = new { petId = "9" } });
        Assert.EndsWith("(binary, more than 1048576 bytes)", binary);
    }

    [Fact]
    public async Task Test_OnlyCountsApisThatSignInWithTheConnection()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "pets", ["baseUrl"] = Base, ["auth"] = null }); // no sign-in: the secret is never sent
        r.Http.Add(Base + "/pets", "[]");
        var test = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = r.ConnectionId }))!;
        Assert.Equal("untested", test["status"]!.GetValue<string>());
        Assert.Equal("nothing to test against yet; status unchanged", test["reason"]!.GetValue<string>());
        Assert.DoesNotContain(r.Http.Urls, u => u.StartsWith(Base, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_PicksOnlyAGetAllowedForSomeScope()
    {
        await using var r = await Rig.Make();
        // listPets and getPet are not allowed anywhere now (getPet needs a parameter anyway): nothing to test against
        await r.Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "pets", ["kind"] = "api", ["teamId"] = r.Alpha,
            ["choices"] = new JsonArray(new JsonObject { ["path"] = "listPets", ["state"] = "ask" }),
        });
        Assert.Equal("nothing to test against yet; status unchanged", (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = r.ConnectionId }))!["reason"]!.GetValue<string>());
        // allowed for every team (the default scope) is enough
        await r.Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "pets", ["kind"] = "api",
            ["choices"] = new JsonArray(new JsonObject { ["path"] = "listPets", ["state"] = "allow" }),
        });
        await r.Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "pets", ["kind"] = "api", ["teamId"] = r.Alpha,
            ["choices"] = new JsonArray(new JsonObject { ["path"] = "listPets", ["state"] = "default" }),
        });
        r.Http.Add(Base + "/pets", "[]");
        Assert.Equal("works", (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = r.ConnectionId }))!["status"]!.GetValue<string>());
    }

    [Fact]
    public void Scrubber_CatchesLowercasePercentAndFormEncodings_AndBasicCredentials()
    {
        var owner = new object();
        const string secret = "s3cr3t value/with+plus=";
        SecretScrubber.Set(owner, 1, [secret]);
        try
        {
            var lower = Uri.EscapeDataString(secret).Replace("%2F", "%2f").Replace("%2B", "%2b").Replace("%3D", "%3d");
            Assert.Equal("[redacted]", SecretScrubber.Scrub(lower));
            Assert.Equal("[redacted]", SecretScrubber.Scrub(WebUtility.UrlEncode(secret))); // "+" for the space
        }
        finally { SecretScrubber.Forget(owner, 1); }

        using var db = new TempDb();
        var conns = new ConnectionService(new MemoryCredentialStore(), db.ConnectionString);
        const string password = "basic_pw_0123456789";
        var saved = conns.Save(null, "Basic login", [new ConnectionField("username", "Username", false, "ada"), new ConnectionField("password", "Password", true, null)],
            new Dictionary<string, string> { ["password"] = password });
        try
        {
            var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes("ada:" + password));
            Assert.Equal("Authorization: Basic [redacted]", SecretScrubber.Scrub("Authorization: Basic " + pair));
        }
        finally { conns.Remove(saved.Id); }
    }
}
