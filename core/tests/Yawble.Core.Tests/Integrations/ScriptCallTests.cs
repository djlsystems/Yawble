using System.Collections.Concurrent;
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

/// <summary>
/// Card 586 (B132, SC-2): a script member's API call (<see cref="IntegrationService.CallForScriptAsync"/>), form-encoded bodies (script and agent
/// paths), path parameters filled from the Connection, and the catalog the script adapter reads (<see cref="IntegrationService.ListScriptOperations"/>).
/// A messaging API shaped like Twilio's Messages endpoint, served by FakeHttp: no network, no OS credential store.
/// </summary>
public class ScriptCallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Sid = "AC_TEST_ACCOUNT";
    private const string AuthToken = "TEST_AUTH_TOKEN";
    private const string Host = "https://api.sms.example";
    private const string Messages = Host + "/2010-04-01/Accounts/" + Sid + "/Messages.json";

    private const string Spec = """
        {
          "openapi": "3.0.1",
          "info": { "title": "SMS", "version": "1" },
          "servers": [ { "url": "https://api.sms.example" } ],
          "security": [ { "accountSid_authToken": [] } ],
          "components": { "securitySchemes": { "accountSid_authToken": { "type": "http", "scheme": "basic" } } },
          "paths": {
            "/2010-04-01/Accounts/{AccountSid}/Messages.json": {
              "parameters": [ { "name": "AccountSid", "in": "path", "required": true, "schema": { "type": "string" } } ],
              "post": { "operationId": "CreateMessage", "summary": "Send a message",
                "requestBody": { "content": { "application/x-www-form-urlencoded": { "schema": {
                  "type": "object", "title": "CreateMessageRequest",
                  "properties": { "To": { "type": "string" }, "From": { "type": "string" }, "Body": { "type": "string" }, "SendAsMms": { "type": "boolean" } },
                  "required": [ "To" ] } } } } },
              "get": { "operationId": "ListMessage", "summary": "List messages",
                "parameters": [ { "name": "PageSize", "in": "query", "schema": { "type": "integer" } } ] }
            },
            "/2010-04-01/Accounts/{AccountSid}/Messages/{Sid}.json": {
              "parameters": [ { "name": "AccountSid", "in": "path", "required": true, "schema": { "type": "string" } },
                              { "name": "Sid", "in": "path", "required": true, "schema": { "type": "string" } } ],
              "delete": { "operationId": "DeleteMessage", "summary": "Delete a message" },
              "post": { "operationId": "UpdateMessage", "summary": "Redact a message",
                "requestBody": { "content": { "application/x-www-form-urlencoded": { "schema": { "type": "object", "properties": { "Body": { "type": "string" } } } } } } }
            }
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
        public readonly List<(string? ContentType, string? Body, string? Auth, string Url)> Sent = [];
        private readonly ConcurrentDictionary<int, (object Args, TaskCompletionSource<string> Done)> _calls = new();
        private int _next;

        /// <summary>
        /// Teams Alpha (Ada, an agent) and Beta (Bo); the API "sms" read from the spec, offered to Alpha, signed in with Basic from a Connection
        /// whose user name (not secret) is the account SID. Alpha's checklist: CreateMessage and ListMessage allowed, UpdateMessage ask, DeleteMessage off.
        /// </summary>
        public static async Task<Rig> Make(bool bindAccountSid = true, string userLabel = "Username")
        {
            var r = new Rig();
            r.Http.Add(Host + "/openapi.json", Spec);
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
                ["name"] = "SMS account",
                ["fields"] = new JsonArray(
                    new JsonObject { ["key"] = "username", ["label"] = userLabel, ["secret"] = false, ["value"] = Sid },
                    new JsonObject { ["key"] = "password", ["label"] = "Auth token", ["secret"] = true }),
                ["secrets"] = new JsonObject { ["password"] = AuthToken },
            }))!;
            r.ConnectionId = conn["id"]!.GetValue<long>();
            await r.Rpc("integrations.apiSave", new JsonObject
            {
                ["name"] = "sms", ["baseUrl"] = Host, ["docsUrl"] = Host + "/openapi.json", ["connectionId"] = r.ConnectionId,
            });
            await r.Discover();
            var auth = new JsonObject { ["type"] = "basic" };
            if (bindAccountSid) auth["pathParams"] = new JsonObject { ["AccountSid"] = "username" };
            await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "sms", ["auth"] = auth });
            await r.Rpc("integrations.apiEnable", new JsonObject { ["name"] = "sms", ["teamId"] = r.Alpha });
            await r.Rpc("integrations.saveChecklist", new JsonObject
            {
                ["name"] = "sms", ["kind"] = "api", ["teamId"] = r.Alpha,
                ["choices"] = new JsonArray(
                    new JsonObject { ["path"] = "CreateMessage", ["state"] = "allow" }, new JsonObject { ["path"] = "ListMessage", ["state"] = "allow" },
                    new JsonObject { ["path"] = "UpdateMessage", ["state"] = "ask" }, new JsonObject { ["path"] = "DeleteMessage", ["state"] = "off" }),
            });
            r.Answer(Messages, HttpStatusCode.Created, """{"sid":"SM1","status":"queued"}""");
            return r;
        }

        /// <summary>The URL answers with this status and JSON body, and every request to it is recorded with its body and Authorization header.</summary>
        public void Answer(string url, HttpStatusCode status, string json) =>
            Http.Add(url, req =>
            {
                Sent.Add((req.Content?.Headers.ContentType?.MediaType, req.Content?.ReadAsStringAsync().Result, req.Headers.Authorization?.ToString(), req.RequestUri!.AbsoluteUri));
                return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            });

        public async Task Discover()
        {
            var s = await Rpc("integrations.discover", new JsonObject { ["name"] = "sms", ["kind"] = "api" });
            Assert.True(s!["started"]!.GetValue<bool>());
            await Wait.UntilAsync(async () => (await Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "sms", ["kind"] = "api" }))!["state"]!.GetValue<string>() != "running", "the spec was read");
            Assert.Equal("done", (await Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "sms", ["kind"] = "api" }))!["state"]!.GetValue<string>());
        }

        public async Task<JsonNode?> Rpc(string method, JsonObject? p = null) => await Svc.DispatchAsync(method, p, Ct);

        public Task<ScriptApiResult> Script(string action, JsonObject args, bool dryRun = false, string team = "Alpha", string integration = "sms") =>
            Svc.CallForScriptAsync(new ScriptCaller(team, "Texter", "run-42", dryRun), integration, action, args, Ct);

        private async Task<RunResult> Handle(RunContext run, CancellationToken ct)
        {
            foreach (var (id, call) in _calls)
            {
                if (!run.Items.Any(m => m.Payload.Contains($"call#{id}#", StringComparison.Ordinal))) continue;
                _calls.TryRemove(id, out _);
                try
                {
                    var body = new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                        ["params"] = new JsonObject { ["name"] = "api_sms", ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(call.Args) },
                    };
                    var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
                    var result = JsonNode.Parse(reply.Json!)!["result"];
                    call.Done.TrySetResult(result?["content"]?[0]?["text"]?.GetValue<string>() ?? reply.Json!);
                }
                catch (Exception e) { call.Done.TrySetException(e); }
            }
            return RunResult.Ok;
        }

        /// <summary>Ada (an agent of Alpha) calls the API's tool in a run of her own; the text she reads.</summary>
        public async Task<string> Ada(object args)
        {
            var id = Interlocked.Increment(ref _next);
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = (args, done);
            await Engine.PersonTellAsync("Alpha", "Ada", $"call#{id}#", ct: Ct);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        public List<(string Member, string Args, string Decision, string? Via)> Calls()
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT member, args, decision, via FROM integration_calls WHERE integration = 'api:sms' ORDER BY id;";
            using var rd = cmd.ExecuteReader();
            var list = new List<(string, string, string, string?)>();
            while (rd.Read()) list.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3)));
            return list;
        }

        public async ValueTask DisposeAsync()
        {
            await Svc.DisposeAsync();
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static string BasicHeader => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Sid + ":" + AuthToken));

    // ---- the agent's path ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Agent_FormOperation_SendsAFormBody_WithBasicSignIn_AndTheAccountFromTheConnection()
    {
        await using var r = await Rig.Make();
        var text = await r.Ada(new { action = "CreateMessage", @params = new { body = new { To = "+15551230000", From = "+15559870000", Body = "Hi there & bye" } } });
        Assert.StartsWith("HTTP 201", text);
        var sent = Assert.Single(r.Sent);
        Assert.Equal("application/x-www-form-urlencoded", sent.ContentType);
        Assert.Equal("To=%2B15551230000&From=%2B15559870000&Body=Hi+there+%26+bye", sent.Body);
        Assert.Equal(BasicHeader, sent.Auth);
        Assert.Equal(Messages, sent.Url);
        Assert.DoesNotContain(AuthToken, text);
    }

    [Fact]
    public async Task Agent_FormFields_MayBeGivenFlat_AsTheOperationsParameters()
    {
        await using var r = await Rig.Make();
        var text = await r.Ada(new { action = "CreateMessage", @params = new { To = "+15551230000", Body = "Hi", SendAsMms = true } });
        Assert.StartsWith("HTTP 201", text);
        Assert.Equal("To=%2B15551230000&Body=Hi&SendAsMms=true", Assert.Single(r.Sent).Body);
    }

    [Fact]
    public async Task Agent_DescribeSaysTheAccountComesFromTheConnection()
    {
        await using var r = await Rig.Make();
        var text = await r.Ada(new { action = "describe", @params = new { action = "CreateMessage" } });
        Assert.Contains("filled from the connection: AccountSid", text);
        Assert.Empty(r.Sent);
    }

    // ---- the script's path ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Script_SendsAFormBody_SignedIn_AndLogsTheScriptMemberAsTheCaller()
    {
        await using var r = await Rig.Make();
        var result = await r.Script("CreateMessage", new JsonObject { ["To"] = "+15551230000", ["From"] = "+15559870000", ["Body"] = "Hello" });
        Assert.Equal(201, result.Status);
        Assert.True(result.Sent);
        Assert.Equal("application/json", result.ContentType);
        Assert.Equal("""{"sid":"SM1","status":"queued"}""", result.Body);
        var sent = Assert.Single(r.Sent);
        Assert.Equal("application/x-www-form-urlencoded", sent.ContentType);
        Assert.Equal("To=%2B15551230000&From=%2B15559870000&Body=Hello", sent.Body);
        Assert.Equal(BasicHeader, sent.Auth);
        Assert.Equal(Messages, sent.Url);

        var call = Assert.Single(r.Calls());
        Assert.Equal("Alpha/Texter", call.Member);
        Assert.Equal("allow", call.Decision);
        Assert.Equal("script", call.Via);
        Assert.Contains("CreateMessage", call.Args);
        Assert.Contains("script run run-42", call.Args);
        Assert.DoesNotContain(AuthToken, call.Args);
    }

    [Fact]
    public async Task Script_TheApiByItsIntegrationName_AndABodyObject_Work_Too()
    {
        await using var r = await Rig.Make();
        var result = await r.Script("CreateMessage", new JsonObject { ["body"] = new JsonObject { ["To"] = "+1555" } }, integration: "api:sms");
        Assert.Equal(201, result.Status);
        Assert.Equal("To=%2B1555", Assert.Single(r.Sent).Body);
    }

    [Fact]
    public async Task Script_AccountFromTheConnection_IsNeverTakenFromTheCaller()
    {
        await using var r = await Rig.Make();
        var e = await Assert.ThrowsAsync<ScriptApiRefusedException>(() =>
            r.Script("CreateMessage", new JsonObject { ["AccountSid"] = "AC_other", ["To"] = "+1555" }));
        Assert.Contains("filled from the connection", e.Message);
        Assert.Empty(r.Sent);
        // the same value as the Connection's is accepted
        Assert.Equal(201, (await r.Script("CreateMessage", new JsonObject { ["AccountSid"] = Sid, ["To"] = "+1555" })).Status);
    }

    [Fact]
    public async Task Script_WithoutABinding_APathParameterIsNeverFilledByAFieldsName()
    {
        // a field named or labelled like the parameter fills nothing on its own (GitHub's /users/{username} must not become the sign-in's user)
        await using var r = await Rig.Make(bindAccountSid: false, userLabel: "AccountSid");
        var e = await Assert.ThrowsAsync<ScriptApiRefusedException>(() => r.Script("CreateMessage", new JsonObject { ["To"] = "+1555" }));
        Assert.Contains("'AccountSid' is required", e.Message);
        Assert.Equal(201, (await r.Script("CreateMessage", new JsonObject { ["AccountSid"] = Sid, ["To"] = "+1555" })).Status);
        Assert.Equal(Messages, Assert.Single(r.Sent).Url);
    }

    [Fact]
    public async Task Binding_IsKept_WhenTheSignInIsSavedAgainWithoutIt_AndShownInTheApiList()
    {
        await using var r = await Rig.Make();
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "sms", ["auth"] = new JsonObject { ["type"] = "basic" } });
        var listed = (await r.Rpc("integrations.apiList"))!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "sms")!;
        Assert.Equal("username", listed["auth"]!["pathParams"]!["AccountSid"]!.GetValue<string>());
        Assert.Equal(201, (await r.Script("CreateMessage", new JsonObject { ["To"] = "+1555" })).Status);
    }

    [Fact]
    public async Task Script_ASecretFieldNeverFillsAPathParameter()
    {
        await using var r = await Rig.Make(bindAccountSid: false);
        // bound to the secret field: refused when saved
        var e = await Assert.ThrowsAnyAsync<Exception>(() =>
            r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "sms", ["auth"] = new JsonObject { ["type"] = "basic", ["pathParams"] = new JsonObject { ["AccountSid"] = "password" } } }));
        Assert.Contains("secret", e.Message);
        // and unbound, the caller must give it
        var missing = await Assert.ThrowsAsync<ScriptApiRefusedException>(() => r.Script("CreateMessage", new JsonObject { ["To"] = "+1555" }));
        Assert.Contains("AccountSid", missing.Message);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public async Task Script_DryRun_SendsNothingForAPost_ButSendsAGet()
    {
        await using var r = await Rig.Make();
        var would = await r.Script("CreateMessage", new JsonObject { ["To"] = "+1555", ["Body"] = "x" }, dryRun: true);
        Assert.False(would.Sent);
        Assert.Equal("would POST /2010-04-01/Accounts/" + Sid + "/Messages.json", would.Body);
        Assert.Empty(r.Sent);
        Assert.Empty(r.Calls());

        r.Answer(Messages + "?PageSize=5", HttpStatusCode.OK, """{"messages":[]}""");
        var got = await r.Script("ListMessage", new JsonObject { ["PageSize"] = 5 }, dryRun: true);
        Assert.True(got.Sent);
        Assert.Equal(200, got.Status);
        Assert.Single(r.Sent);
    }

    [Fact]
    public async Task Script_AskAndDeny_AreRefused_AndNothingIsSent()
    {
        await using var r = await Rig.Make();
        var ask = await Assert.ThrowsAsync<ScriptApiRefusedException>(() => r.Script("UpdateMessage", new JsonObject { ["Sid"] = "SM1", ["Body"] = "" }));
        Assert.Equal("needs approval: allow UpdateMessage for Alpha", ask.Message);
        var deny = await Assert.ThrowsAsync<ScriptApiRefusedException>(() => r.Script("DeleteMessage", new JsonObject { ["Sid"] = "SM1" }));
        Assert.Contains("not allowed", deny.Message);
        Assert.Empty(r.Sent);
        Assert.Equal(["ask", "deny"], r.Calls().Select(c => c.Decision));
        // no approval was raised for the person
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(r.Db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM integration_asks;";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public async Task Script_ApiNotOnForTheTeam_OrUnknown_IsRefused()
    {
        await using var r = await Rig.Make();
        var off = await Assert.ThrowsAsync<ScriptApiRefusedException>(() => r.Script("CreateMessage", new JsonObject { ["To"] = "+1" }, team: "Beta"));
        Assert.Contains("not turned on for Beta", off.Message);
        await Assert.ThrowsAsync<ScriptApiRefusedException>(() => r.Script("CreateMessage", new JsonObject(), integration: "nope"));
        await Assert.ThrowsAsync<ScriptApiRefusedException>(() => r.Script("NoSuchAction", new JsonObject()));
        await Assert.ThrowsAsync<ScriptApiRefusedException>(() => r.Script("CreateMessage", new JsonObject { ["To"] = "+1" }, team: "Nobody"));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public async Task Script_Non2xx_CarriesTheStatusAndTheScrubbedBody()
    {
        await using var r = await Rig.Make();
        r.Answer(Messages, HttpStatusCode.BadRequest, $$"""{"code":21211,"message":"bad To","echo":"{{AuthToken}}"}""");
        var result = await r.Script("CreateMessage", new JsonObject { ["To"] = "nope" });
        Assert.Equal(400, result.Status);
        Assert.True(result.Sent);
        Assert.Contains("21211", result.Body);
        Assert.DoesNotContain(AuthToken, result.Body);
        Assert.Contains("[redacted]", result.Body);
    }

    // ---- the catalog ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListScriptOperations_GivesTheAllowedOperations_WithoutConnectionFilledParameters()
    {
        await using var r = await Rig.Make();
        var ops = r.Svc.ListScriptOperations("Alpha", "sms");
        Assert.Equal(["CreateMessage", "ListMessage", "UpdateMessage"], ops.Select(o => o.Action).Order());
        var create = ops.Single(o => o.Action == "CreateMessage");
        Assert.Equal("POST", create.Method);
        Assert.Equal("/2010-04-01/Accounts/{AccountSid}/Messages.json", create.Route);
        Assert.Equal("application/x-www-form-urlencoded", create.BodyMediaType);
        Assert.Equal("allow", create.Decision);
        Assert.DoesNotContain(create.Parameters, p => p.Name == "AccountSid");
        var to = create.Parameters.Single(p => p.Name == "To");
        Assert.Equal(("body", true, "string"), (to.In, to.Required, to.Type));
        Assert.Equal(("body", false, "boolean"), create.Parameters.Single(p => p.Name == "SendAsMms") is var m ? (m.In, m.Required, m.Type) : default);
        var list = ops.Single(o => o.Action == "ListMessage");
        Assert.Null(list.BodyMediaType);
        Assert.Equal(("query", false, "integer"), list.Parameters.Single(p => p.Name == "PageSize") is var q ? (q.In, q.Required, q.Type) : default);
        var update = ops.Single(o => o.Action == "UpdateMessage");
        Assert.Equal("ask", update.Decision);
        Assert.Equal(("path", true, "string"), update.Parameters.Single(p => p.Name == "Sid") is var s ? (s.In, s.Required, s.Type) : default);

        Assert.Throws<ScriptApiRefusedException>(() => r.Svc.ListScriptOperations("Beta", "sms"));
    }

    [Fact]
    public async Task PathParams_SavedAlone_KeepTheSpecSignIn_AndTheListNamesThePathParameters()
    {
        await using var r = await Rig.Make(bindAccountSid: false);
        var before = (await r.Rpc("integrations.apiList"))!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "sms")!;
        Assert.Equal(["AccountSid", "Sid"], before["pathParamNames"]!.AsArray().Select(n => n!.GetValue<string>()));
        var chosenBefore = before["auth"]!["chosen"]!.GetValue<bool>();

        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "sms", ["pathParams"] = new JsonObject { ["AccountSid"] = "username" } });
        var listed = (await r.Rpc("integrations.apiList"))!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "sms")!;
        Assert.Equal("username", listed["auth"]!["pathParams"]!["AccountSid"]!.GetValue<string>());
        Assert.Equal("basic", listed["auth"]!["type"]!.GetValue<string>());
        Assert.Equal(chosenBefore, listed["auth"]!["chosen"]!.GetValue<bool>());
        Assert.Equal(201, (await r.Script("CreateMessage", new JsonObject { ["To"] = "+1555" })).Status);
        Assert.Equal(Messages, Assert.Single(r.Sent).Url);

        var e = await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "sms", ["pathParams"] = new JsonObject { ["AccountSid"] = "password" } }));
        Assert.Contains("secret", e.Message);

        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "sms", ["pathParams"] = null });
        listed = (await r.Rpc("integrations.apiList"))!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "sms")!;
        Assert.Null(listed["auth"]!["pathParams"]);
    }

    [Fact]
    public async Task Peek_ReadsTheSpecAtTheAddress_WithoutSavingAnything()
    {
        await using var r = await Rig.Make();
        var p = (await r.Rpc("integrations.apiPeek", new JsonObject { ["url"] = Host + "/openapi.json" }))!;
        Assert.True(p["found"]!.GetValue<bool>());
        Assert.Equal(Host + "/openapi.json", p["specUrl"]!.GetValue<string>());
        Assert.Equal(Host, p["serviceUrl"]!.GetValue<string>());
        Assert.Equal("SMS", p["name"]!.GetValue<string>());
        Assert.Equal(4, p["actions"]!.GetValue<int>());
        Assert.Equal("basic", p["signIn"]!.GetValue<string>());

        var none = (await r.Rpc("integrations.apiPeek", new JsonObject { ["url"] = "https://api.nothing.example/v1" }))!;
        Assert.False(none["found"]!.GetValue<bool>());
        Assert.Equal("https://api.nothing.example/v1", none["serviceUrl"]!.GetValue<string>());
        Assert.Equal("nothing.example", none["name"]!.GetValue<string>());
        Assert.Single((await r.Rpc("integrations.apiList"))!.AsArray());
    }

    [Theory]
    [InlineData("Twilio - Api", "https://api.twilio.com", "Twilio")]
    [InlineData("Swagger Petstore", "https://petstore.example", "Swagger Petstore")]
    [InlineData("GitHub v3 REST API", "https://api.github.com", "GitHub v3 REST")]
    [InlineData(null, "https://api.stripe.com", "stripe.com")]
    [InlineData("   ", "https://www.example.org", "example.org")]
    [InlineData("API", "https://api.example.org", "API")]
    public void SuggestedName_IsTheTitleWithoutATrailingApi_OrTheHost(string? title, string host, string expected) =>
        Assert.Equal(expected, IntegrationService.SuggestApiName(title, new Uri(host)));
}
