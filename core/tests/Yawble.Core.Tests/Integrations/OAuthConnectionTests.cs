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
using static Yawble.Core.Tests.Integrations.McpOAuthTests;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// OAuth sign-in Connections in the service, and web APIs signed in with them (bearer access token, refreshed on
/// the host, scopes from the ticked actions). Every address is a FakeHttp route; the browser and the redirect are fakes. No real browser, no
/// port, no network, no OS credential store, no real program.
/// </summary>
public class OAuthConnectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Idp = "https://idp.example.test";
    private const string IdpMeta = "https://idp.example.test/.well-known/oauth-authorization-server";
    private const string Authorize = "https://idp.example.test/oauth/authorize";
    private const string TokenUrl = "https://idp.example.test/oauth/token";
    private const string Base = "https://api.notes.example/v1";
    private const string SpecUrl = "https://api.notes.example/openapi.json";
    private const string At1 = "at_ONE_notes_supersecret_0123";
    private const string At2 = "at_TWO_notes_supersecret_0123";
    private const string Rt1 = "rt_ONE_notes_supersecret_0123";
    private const string Rt2 = "rt_TWO_notes_supersecret_0123";
    private const string ClientSecret = "cs_notes_supersecret_0123456789";
    private const string NeedsAccess =
        "notes's deleteNote needs access the sign-in does not have; ask the person to sign in again in Settings → Integrations → APIs (Sign in again to allow the new actions)";

    private static string Spec(string authorize = Authorize, string token = TokenUrl) => $$"""
        {
          "openapi": "3.0.0",
          "info": { "title": "Notes", "version": "1" },
          "servers": [ { "url": "{{Base}}" } ],
          "security": [ { "o": [ "notes.read" ] } ],
          "components": { "securitySchemes": { "o": { "type": "oauth2", "flows": { "authorizationCode": {
            "authorizationUrl": "{{authorize}}", "tokenUrl": "{{token}}",
            "scopes": { "notes.read": "Read notes", "notes.write": "Write notes", "admin": "Delete anything" } } } } } },
          "paths": {
            "/notes": {
              "get": { "operationId": "listNotes", "summary": "List notes" },
              "post": { "operationId": "createNote", "summary": "Create a note", "security": [ { "o": [ "notes.write" ] } ],
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "type": "object", "properties": { "text": { "type": "string" } } } } } } }
            },
            "/notes/{id}": {
              "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ],
              "delete": { "operationId": "deleteNote", "summary": "Delete a note", "security": [ { "o": [ "admin" ] } ] }
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
        public readonly MemoryCredentialStore Store = new();
        public readonly FakeHttp Http = new();
        public readonly FakeBrowser Browser = new();
        public readonly ManualClock Clock = new();
        public long Alpha;
        public readonly List<Dictionary<string, string>> TokenForms = [];
        public int Refreshes;
        public (HttpStatusCode Status, string Body)? RefreshAnswer;
        public bool GiveRefreshToken = true;
        /// <summary>When set, a refresh waits for it (to make an edit while the refresh is in flight).</summary>
        public SemaphoreSlim? HoldRefresh;
        /// <summary>The access token the API takes (null: any token; "": none).</summary>
        public string? Accept = At1;
        public readonly List<string> ApiAuth = [];
        private readonly ConcurrentDictionary<int, (string Tool, object Args, TaskCompletionSource<string> Done)> _calls = new();
        private int _next;

        /// <summary>Alpha/Ada; the API "notes" discovered from its OAuth 2 spec and offered to Alpha, its checklist as the app suggests
        /// (listNotes allowed, createNote asks first, deleteNote off) for every team and for Alpha. Not signed in yet.</summary>
        public static async Task<Rig> Make(string? spec = null, bool tick = true)
        {
            var r = new Rig();
            r.Http.Add(SpecUrl, spec ?? Spec());
            r.Http.Add(TokenUrl, r.Token);
            r.Http.Add(Base + "/notes", r.Serve);
            r.Http.Add(Base + "/notes/7", r.Serve);
            r.Engine = new BusEngine(r.Db.OpenStore(), new ScriptedRunner { Default = r.Handle }, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            r.Conns = new ConnectionService(r.Store, r.Db.ConnectionString);
            r.Svc = new IntegrationService(r.Db.ConnectionString, r.Engine, clock: r.Clock, connections: r.Conns, httpHandler: () => r.Http, apiCallHandler: () => r.Http,
                mcpHandler: () => r.Http, browser: r.Browser, redirect: () => new FakeReceiver(r.Browser, Answer.Code));
            r.Engine.Integrations = r.Svc;
            r.Alpha = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await r.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await r.Engine.StartAsync(Ct);

            await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "notes", ["baseUrl"] = Base, ["docsUrl"] = SpecUrl });
            await r.Discover();
            await r.Rpc("integrations.apiEnable", new JsonObject { ["name"] = "notes", ["teamId"] = r.Alpha });
            if (!tick) return r;
            // the default list and Alpha's own, as the app suggests
            await r.TickFor(null, ("listNotes", "allow"), ("createNote", "ask"), ("deleteNote", "off"));
            await r.Tick(("listNotes", "allow"), ("createNote", "ask"), ("deleteNote", "off"));
            return r;
        }

        public Task Tick(params (string Action, string State)[] choices) => TickFor(Alpha, choices);

        public async Task TickFor(long? team, params (string Action, string State)[] choices) =>
            await Rpc("integrations.saveChecklist", new JsonObject
            {
                ["name"] = "notes", ["kind"] = "api", ["teamId"] = team,
                ["choices"] = new JsonArray(choices.Select(c => (JsonNode?)new JsonObject { ["path"] = c.Action, ["state"] = c.State }).ToArray()),
            });

        public async Task Discover()
        {
            await Rpc("integrations.discover", new JsonObject { ["name"] = "notes", ["kind"] = "api" });
            await Wait.UntilAsync(async () => (await Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "notes", ["kind"] = "api" }))!["state"]!.GetValue<string>() != "running", "the spec was read");
        }

        /// <summary>Signs "notes" in with its own OAuth app (the spec's addresses and the ticked actions' scopes).</summary>
        public async Task<long> SignIn(string? secret = null, long? connectionId = null)
        {
            var p = new JsonObject { ["api"] = "notes", ["clientId"] = "notes-app" };
            if (secret is not null) p["clientSecret"] = secret;
            if (connectionId is { } id) p["connectionId"] = id;
            var res = await Rpc("integrations.oauthSignIn", p);
            Assert.True(res!["ok"]!.GetValue<bool>());
            return res["connection"]!["id"]!.GetValue<long>();
        }

        private HttpResponseMessage Token(HttpRequestMessage req)
        {
            var form = Form(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            lock (TokenForms) TokenForms.Add(form);
            if (form["grant_type"] == "refresh_token")
            {
                Interlocked.Increment(ref Refreshes);
                HoldRefresh?.Wait();
                Thread.Sleep(150); // concurrent callers arrive while this one is in flight
                if (RefreshAnswer is { } ra) return Json(ra.Body, ra.Status);
                return Json($$"""{"access_token":"{{At2}}","refresh_token":"{{Rt2}}","expires_in":3600,"token_type":"Bearer"}""");
            }
            var o = new JsonObject { ["access_token"] = At1, ["expires_in"] = 3600, ["token_type"] = "bearer" };
            if (GiveRefreshToken) o["refresh_token"] = Rt1;
            return Json(o.ToJsonString());
        }

        private HttpResponseMessage Serve(HttpRequestMessage req)
        {
            var auth = req.Headers.Authorization?.ToString() ?? "";
            lock (ApiAuth) ApiAuth.Add(auth);
            if (Accept is not null && auth != "Bearer " + Accept) return Json("{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized);
            if (req.Method == HttpMethod.Post) return Json("{\"id\":7}", HttpStatusCode.Created);
            if (req.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json($"{{\"notes\":[\"buy milk\"],\"echo\":\"{auth}\"}}");
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

        /// <summary>Ada calls the notes tool in a run of her own; the text she reads.</summary>
        public async Task<string> Ada(object args)
        {
            var id = Interlocked.Increment(ref _next);
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = ("api_notes", args, done);
            await Engine.PersonTellAsync("Alpha", "Ada", $"call#{id}#", ct: Ct);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        public bool Told(long workflow, string text)
        {
            using var s = Db.OpenStore();
            return s.Workflow(workflow).Any(m => m.Payload.Contains(text, StringComparison.Ordinal));
        }

        public string Text(string sql)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToString(cmd.ExecuteScalar()) ?? "";
        }

        /// <summary>Everything the app wrote down: calls, asks, connections and APIs.</summary>
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

    private static JsonObject NotesRow(JsonNode? list) => (JsonObject)((JsonArray)list!).Single(a => a!["name"]!.GetValue<string>() == "notes")!;

    private static string[] Strings(JsonNode? n) => ((JsonArray)n!).Select(x => x!.GetValue<string>()).ToArray();

    // ------------------------------------------------------------------ discovery and sign-in

    [Fact]
    public async Task OAuthDiscoverReturnsAddressesAndScopes()
    {
        await using var r = await Rig.Make();
        r.Http.Add(IdpMeta, new JsonObject
        {
            ["issuer"] = Idp, ["authorization_endpoint"] = Authorize, ["token_endpoint"] = TokenUrl,
            ["scopes_supported"] = new JsonArray("openid", "notes.read", "notes.write"), ["code_challenge_methods_supported"] = new JsonArray("S256"),
        }.ToJsonString());
        var found = (await r.Rpc("integrations.oauthDiscover", new JsonObject { ["provider"] = Idp }))!;
        Assert.True(found["found"]!.GetValue<bool>());
        Assert.Equal(Idp + "/", found["issuer"]!.GetValue<string>());
        Assert.Equal(Authorize, found["authorizeUrl"]!.GetValue<string>());
        Assert.Equal(TokenUrl, found["tokenUrl"]!.GetValue<string>());
        Assert.Equal(["openid", "notes.read", "notes.write"], Strings(found["scopes"]));

        // a provider that publishes no metadata (GitHub-style): not found, so the screen offers the Advanced addresses
        var none = (await r.Rpc("integrations.oauthDiscover", new JsonObject { ["provider"] = "https://github.example.test" }))!;
        Assert.False(none["found"]!.GetValue<bool>());
        // a plain http provider on another computer is refused with a sentence
        var e = await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.oauthDiscover", new JsonObject { ["provider"] = "http://idp.example.test" }));
        Assert.Contains("the sign-in provider address at idp.example.test must start with https://", e.Message);
    }

    [Fact]
    public async Task SignInForAnApiAsksForTheTickedActionsScopes()
    {
        await using var r = await Rig.Make();
        // another team's own rule counts too: Beta asks first for deleteNote
        var beta = await r.Engine.CreateTeamAsync("Beta", ct: Ct);
        await r.Rpc("integrations.apiEnable", new JsonObject { ["name"] = "notes", ["teamId"] = beta });
        await r.Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "notes", ["kind"] = "api", ["teamId"] = beta,
            ["choices"] = new JsonArray(new JsonObject { ["path"] = "listNotes", ["state"] = "off" }, new JsonObject { ["path"] = "createNote", ["state"] = "off" },
                new JsonObject { ["path"] = "deleteNote", ["state"] = "ask" }),
        });
        var id = await r.SignIn();

        var url = Assert.Single(r.Browser.Opened);
        Assert.StartsWith(Authorize + "?", url.AbsoluteUri);
        var q = Query(url);
        Assert.Equal("notes-app", q["client_id"]);
        Assert.Equal(["admin", "notes.read", "notes.write"], q["scope"].Split(' ').Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("openid", q["scope"]);
        var view = r.Conns.Get(id)!;
        Assert.Equal("oauth", view.Kind);
        Assert.Equal("notes sign-in", view.Name);
        Assert.Equal("works", view.Status);
        Assert.Equal(["notes"], view.UsedBy); // the API's connection is set

        // only the ticked actions: without Beta's ask, admin is not asked for
        await r.Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "notes", ["kind"] = "api", ["teamId"] = beta, ["choices"] = new JsonArray(new JsonObject { ["path"] = "deleteNote", ["state"] = "off" }),
        });
        await r.SignIn(connectionId: id);
        Assert.Equal("notes.read notes.write", string.Join(' ', Query(r.Browser.Opened[^1])["scope"].Split(' ').Order()));

        // addresses given for an API must be the spec's (no silent repointing)
        var e = await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.oauthSignIn", new JsonObject
        {
            ["api"] = "notes", ["clientId"] = "notes-app", ["authorizeUrl"] = "https://evil.example.test/authorize", ["tokenUrl"] = TokenUrl,
        }));
        Assert.Contains("spec", e.Message);
        Assert.Equal(2, r.Browser.Opened.Count); // nothing opened for it
    }

    [Fact]
    public async Task SignInWithAProviderSavesItAndAWebsiteNeverTakesOAuth()
    {
        await using var r = await Rig.Make();
        var res = (await r.Rpc("integrations.oauthSignIn", new JsonObject
        {
            ["name"] = "My provider", ["provider"] = "https://github.example.test", ["authorizeUrl"] = Authorize, ["tokenUrl"] = TokenUrl,
            ["clientId"] = "gh-app", ["clientSecret"] = ClientSecret, ["scopes"] = new JsonArray("repo", "read:user"),
        }))!;
        var id = res["connection"]!["id"]!.GetValue<long>();
        Assert.Equal("My provider", res["connection"]!["name"]!.GetValue<string>());
        Assert.Equal("repo read:user", Query(r.Browser.Opened[^1])["scope"]);
        var f = r.Conns.Get(id)!.Fields.ToDictionary(x => x.Key);
        Assert.Equal("https://github.example.test", f["provider"].Shown);
        Assert.Equal("gh-app", f["client_id"].Shown);
        Assert.Equal(ClientSecret, r.Conns.Secrets(id)["client_secret"]);
        Assert.DoesNotContain(ClientSecret, res.ToJsonString());

        await r.Rpc("integrations.apiSave", new JsonObject { ["kind"] = "website", ["name"] = "shop", ["baseUrl"] = "https://shop.example.test" });
        var e = await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.oauthSignIn", new JsonObject { ["api"] = "shop", ["clientId"] = "x" }));
        Assert.Contains("shop is a website: it signs in with a website login, never OAuth", e.Message);
    }

    // ------------------------------------------------------------------ calls

    [Fact]
    public async Task ApiCallSendsTheAccessToken()
    {
        await using var r = await Rig.Make();
        var before = await r.Ada(new { action = "listNotes" });
        Assert.Contains("needs a connection", before); // not signed in yet
        await r.SignIn();

        var text = await r.Ada(new { action = "listNotes" });
        Assert.StartsWith("HTTP 200", text);
        Assert.Equal("Bearer " + At1, r.ApiAuth[^1]);

        // an asked call runs after approval with the bearer token
        var asked = await r.Ada(new { action = "createNote", @params = new { body = new { text = "hi" } }, _reason = "note it" });
        Assert.StartsWith("asked:", asked);
        var ask = Assert.Single(r.Svc.Asks(r.Alpha, pendingOnly: true));
        await r.Rpc("integrations.approve", new JsonObject { ["id"] = ask.Id });
        await Wait.UntilAsync(() => r.Told(ask.Workflow, "HTTP 201"), "Ada told the result");
        Assert.Equal("Bearer " + At1, r.ApiAuth[^1]);
    }

    [Fact]
    public async Task UnauthorizedRefreshesOnceAndRetries()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn();
        r.Accept = At2; // the service revoked the first token early
        var text = await r.Ada(new { action = "listNotes" });
        Assert.StartsWith("HTTP 200", text);
        Assert.Equal(1, r.Refreshes);
        Assert.Equal(["Bearer " + At1, "Bearer " + At2], r.ApiAuth);
        Assert.NotEqual("needs_reconnect", r.Conns.Get(id)!.Status);
    }

    [Fact]
    public async Task SecondUnauthorizedMarksNeedsReconnect()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn();
        r.Accept = "nothing";
        var text = await r.Ada(new { action = "listNotes" });
        Assert.Contains("HTTP 401", text);
        Assert.Contains("needs reconnecting", text);
        Assert.Equal(1, r.Refreshes);
        Assert.Equal(2, r.ApiAuth.Count);
        Assert.Equal("needs_reconnect", r.Conns.Get(id)!.Status);
        Assert.Contains((JsonArray)(await r.Rpc("integrations.connectionAttention"))!, n => n!["id"]!.GetValue<long>() == id);

        // a refresh the provider refuses: needs reconnect too, with the same words for the agent
        await using var r2 = await Rig.Make();
        var id2 = await r2.SignIn();
        r2.Accept = "nothing";
        r2.RefreshAnswer = (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var text2 = await r2.Ada(new { action = "listNotes" });
        Assert.Contains("needs reconnecting", text2);
        Assert.DoesNotContain("MCP", text2);
        Assert.Equal("needs_reconnect", r2.Conns.Get(id2)!.Status);
        Assert.Single(r2.ApiAuth); // no retry without a new token
    }

    [Fact]
    public async Task ActionNeedingAnUngrantedScopeIsRefused()
    {
        await using var r = await Rig.Make();
        await r.SignIn(); // granted notes.read notes.write (what was asked: the token response says no scope)
        await r.Tick(("deleteNote", "allow")); // a new action, needing admin
        var calls = r.ApiAuth.Count;
        var text = await r.Ada(new { action = "deleteNote", @params = new { id = "7" } });
        Assert.Equal("refused: " + NeedsAccess, text);
        Assert.Equal(calls, r.ApiAuth.Count); // nothing reached the service
    }

    [Fact]
    public async Task GrantedActionsKeepWorking()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn();
        await r.Tick(("deleteNote", "allow"));
        Assert.StartsWith("HTTP 200", await r.Ada(new { action = "listNotes" }));

        // a Connection with no granted scopes recorded: the grant is unknown, so nothing is refused for scopes
        var view = r.Conns.Get(id)!;
        r.Conns.Save(id, view.Name, view.Fields.Where(f => f.Key != "granted_scopes").Select(f => new ConnectionField(f.Key, f.Label, f.Secret, f.Secret ? null : f.Shown)).ToList(),
            new Dictionary<string, string>(), keepStatus: true);
        Assert.StartsWith("HTTP 204", await r.Ada(new { action = "deleteNote", @params = new { id = "7" } }));
        Assert.Empty(Strings(NotesRow(await r.Rpc("integrations.apiList"))["oauth"]!["missingScopes"]));
    }

    [Fact]
    public async Task ApiListShowsMissingScopes()
    {
        await using var r = await Rig.Make();
        var row = NotesRow(await r.Rpc("integrations.apiList"));
        var oauth = (JsonObject)row["oauth"]!;
        Assert.Equal(Authorize, oauth["authorizeUrl"]!.GetValue<string>());
        Assert.Equal(TokenUrl, oauth["tokenUrl"]!.GetValue<string>());
        Assert.Equal(["notes.read", "notes.write"], Strings(oauth["neededScopes"]).Order().ToArray());
        Assert.Empty(Strings(oauth["missingScopes"])); // not signed in: nothing to compare with

        await r.SignIn();
        Assert.Empty(Strings(NotesRow(await r.Rpc("integrations.apiList"))["oauth"]!["missingScopes"]));
        await r.Tick(("deleteNote", "ask"));
        oauth = (JsonObject)NotesRow(await r.Rpc("integrations.apiList"))["oauth"]!;
        Assert.Contains("admin", Strings(oauth["neededScopes"]));
        Assert.Equal(["admin"], Strings(oauth["missingScopes"]));
    }

    // ------------------------------------------------------------------ editing and testing the Connection

    private static JsonArray FieldsOf(ConnectionView v, string? clientId = null) => new(v.Fields.Select(f => (JsonNode?)new JsonObject
    {
        ["key"] = f.Key, ["label"] = f.Label, ["secret"] = f.Secret, ["value"] = f.Secret ? null : f.Key == "client_id" && clientId is not null ? clientId : f.Shown,
    }).ToArray());

    [Fact]
    public async Task ChangingTheClientNeedsANewSignIn()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn();
        var view = r.Conns.Get(id)!;
        await r.Rpc("integrations.connectionSave", new JsonObject { ["id"] = id, ["name"] = view.Name, ["fields"] = FieldsOf(view, clientId: "another-app") });

        var after = r.Conns.Get(id)!;
        Assert.Equal("another-app", after.Fields.Single(f => f.Key == "client_id").Shown);
        Assert.Equal("oauth", after.Kind);
        Assert.False(r.Conns.Secrets(id).ContainsKey("access_token"));
        Assert.False(r.Conns.Secrets(id).ContainsKey("refresh_token"));
        Assert.Equal("needs_reconnect", after.Status);
        Assert.Equal("sign in again: the app or the addresses changed", after.StatusReason);

        var calls = r.ApiAuth.Count;
        var forms = r.TokenForms.Count;
        var text = await r.Ada(new { action = "listNotes" });
        Assert.Contains("sign", text);
        Assert.Equal(calls, r.ApiAuth.Count);
        Assert.Equal(forms, r.TokenForms.Count);
        Assert.Equal("sign in again: the app or the addresses changed", r.Conns.Get(id)!.StatusReason);
    }

    [Fact]
    public async Task RenameKeepsTheTokens()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn();
        var view = r.Conns.Get(id)!;
        await r.Rpc("integrations.connectionSave", new JsonObject { ["id"] = id, ["name"] = "Notes login", ["fields"] = FieldsOf(view) });
        var after = r.Conns.Get(id)!;
        Assert.Equal("Notes login", after.Name);
        Assert.Equal(At1, r.Conns.Secrets(id)["access_token"]);
        Assert.Equal(Rt1, r.Conns.Secrets(id)["refresh_token"]);
        Assert.Equal(view.Fields.Select(f => (f.Key, f.Shown)), after.Fields.Select(f => (f.Key, f.Shown)));
        Assert.Equal("works", after.Status);
        Assert.StartsWith("HTTP 200", await r.Ada(new { action = "listNotes" }));
    }

    [Fact]
    public async Task EmptySecretKeepsTheStoredOne()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn(secret: ClientSecret);
        Assert.Equal(ClientSecret, r.TokenForms[^1]["client_secret"]);

        // signing in again with the secret left empty: the stored one is used and kept
        Assert.Equal(id, await r.SignIn(secret: "", connectionId: id));
        Assert.Equal(ClientSecret, r.TokenForms[^1]["client_secret"]);
        Assert.Equal(ClientSecret, r.Conns.Secrets(id)["client_secret"]);

        // and an edit with the secret left empty keeps it too
        var view = r.Conns.Get(id)!;
        await r.Rpc("integrations.connectionSave", new JsonObject
        {
            ["id"] = id, ["name"] = view.Name, ["fields"] = FieldsOf(view), ["secrets"] = new JsonObject { ["client_secret"] = "" },
        });
        Assert.Equal(ClientSecret, r.Conns.Secrets(id)["client_secret"]);
        Assert.Equal(At1, r.Conns.Secrets(id)["access_token"]);
    }

    [Fact]
    public async Task TestRefreshes()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn();
        var res = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id }))!;
        Assert.Equal("works", res["status"]!.GetValue<string>());
        Assert.Equal(1, r.Refreshes);
        Assert.Equal(At2, r.Conns.Secrets(id)["access_token"]);

        r.RefreshAnswer = (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        res = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id }))!;
        Assert.Equal("needs_reconnect", res["status"]!.GetValue<string>());
        Assert.Equal("needs_reconnect", r.Conns.Get(id)!.Status);

        // no refresh token: works while the access token has not expired, then needs reconnect
        await using var r2 = await Rig.Make();
        r2.GiveRefreshToken = false;
        var id2 = await r2.SignIn();
        Assert.Equal("works", (await r2.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id2 }))!["status"]!.GetValue<string>());
        r2.Clock.Now = r2.Clock.Now.AddHours(2);
        Assert.Equal("needs_reconnect", (await r2.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id2 }))!["status"]!.GetValue<string>());
        Assert.Equal(0, r2.Refreshes);
    }

    [Fact]
    public async Task TokenIsNeverInTheLogOrAgentText()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn(secret: ClientSecret);
        var text = await r.Ada(new { action = "listNotes" }); // the service echoes the Authorization header back
        Assert.StartsWith("HTTP 200", text);
        Assert.Contains("[redacted]", text);
        r.Accept = At2;
        var retried = await r.Ada(new { action = "listNotes" });
        Assert.StartsWith("HTTP 200", retried);
        foreach (var secret in new[] { At1, At2, Rt1, Rt2, ClientSecret })
        {
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain(secret, retried);
            Assert.DoesNotContain(secret, r.AllStoredText());
            Assert.DoesNotContain(secret, (await r.Rpc("integrations.connections"))!.ToJsonString());
            Assert.DoesNotContain(secret, (await r.Rpc("integrations.apiList"))!.ToJsonString());
        }
        Assert.Equal("oauth", r.Conns.Get(id)!.Kind);
    }

    // ------------------------------------------------------------------ shared with an MCP server

    private const string Register = "https://idp.example.test/register";

    /// <summary>An MCP server at <paramref name="mcpUrl"/> that signs in at the same provider (its own protected-resource metadata); its sign-in Connection's id and the tokens it got.</summary>
    private static async Task<(long Id, List<string> McpAuth)> McpSignIn(Rig r, string mcpUrl)
    {
        var u = new Uri(mcpUrl);
        r.Http.Add(u.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-protected-resource" + u.AbsolutePath,
            $$"""{"resource":"{{mcpUrl}}","authorization_servers":["{{Idp}}"],"scopes_supported":["notes.read","notes.write"]}""");
        r.Http.Add(IdpMeta, new JsonObject
        {
            ["issuer"] = Idp, ["authorization_endpoint"] = Authorize, ["token_endpoint"] = TokenUrl, ["registration_endpoint"] = Register,
            ["code_challenge_methods_supported"] = new JsonArray("S256"),
        }.ToJsonString());
        r.Http.Add(Register, _ => Json("""{"client_id":"mcp-client"}""", HttpStatusCode.Created));
        var mcpAuth = new List<string>();
        r.Http.Add(mcpUrl, req =>
        {
            if (req.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK);
            var auth = req.Headers.Authorization?.ToString() ?? "";
            lock (mcpAuth) mcpAuth.Add(auth);
            var b = (JsonObject)JsonNode.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            string Reply(string result) => $"{{\"jsonrpc\":\"2.0\",\"id\":{b["id"]},\"result\":{result}}}";
            return b["method"]!.GetValue<string>() switch
            {
                "initialize" => Json(Reply("{\"serverInfo\":{\"name\":\"fake\"}}")),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "tools/list" => Json(Reply("{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\"}}]}")),
                _ => Json(Reply("{}")),
            };
        });
        await r.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "notesmcp", ["transport"] = "http", ["url"] = mcpUrl, ["auth"] = new JsonObject { ["type"] = "none" } });
        var signed = (await r.Rpc("integrations.mcpSignIn", new JsonObject { ["name"] = "notesmcp" }))!;
        return (signed["connection"]!["id"]!.GetValue<long>(), mcpAuth);
    }

    [Fact]
    public async Task ApiAndMcpShareOneRefresh()
    {
        await using var r = await Rig.Make();
        // the service's own MCP server, on the API's origin: its sign-in may sign the API in too
        var (id, mcpAuth) = await McpSignIn(r, "https://api.notes.example/mcp");
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "notes", ["connectionId"] = id });
        r.Accept = null;

        // the access token is due: the API call and the server's check both want a fresh one at once
        r.Clock.Now = r.Clock.Now.AddHours(2);
        var api = r.Ada(new { action = "listNotes" });
        var mcp = r.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "notesmcp" });
        await Task.WhenAll(api, mcp);
        Assert.StartsWith("HTTP 200", await api);
        Assert.Equal(1, r.Refreshes);
        Assert.Equal("Bearer " + At2, r.ApiAuth[^1]);
        Assert.Equal("Bearer " + At2, mcpAuth[^1]);
    }

    [Fact]
    public async Task AServersSignInIsNotSentToAnotherOrigin()
    {
        await using var r = await Rig.Make();
        var (id, _) = await McpSignIn(r, "https://mcp.notes.example/mcp");
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "notes", ["connectionId"] = id });
        r.Accept = null;
        r.Clock.Now = r.Clock.Now.AddHours(2); // due for a refresh: none is made either
        var forms = r.TokenForms.Count;

        var text = await r.Ada(new { action = "listNotes" });
        Assert.Equal("refused: notesmcp sign-in is a sign-in for mcp.notes.example, not for api.notes.example; give notes its own OAuth sign-in in Settings → Integrations → APIs", text);
        Assert.Empty(r.ApiAuth);
        Assert.Equal(forms, r.TokenForms.Count);

        // the same for an API the person set to a bearer token
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "notes", ["auth"] = new JsonObject { ["type"] = "bearer" } });
        Assert.StartsWith("refused: notesmcp sign-in is a sign-in for mcp.notes.example", await r.Ada(new { action = "listNotes" }));
        Assert.Empty(r.ApiAuth);

        // and signing the API in again into the server's Connection is refused: it is the server's
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "notes", ["auth"] = null });
        await r.Discover(); // the spec's OAuth 2 again (no auth set)
        var e = await Assert.ThrowsAnyAsync<Exception>(() => r.SignIn(connectionId: id));
        Assert.Contains("'notesmcp sign-in' is the sign-in of an MCP server; sign in again in Settings → Integrations → MCP servers (Sign in), or sign in as a new connection instead", e.Message);
    }

    // ------------------------------------------------------------------ fix round

    [Fact]
    public async Task WithNoRulesSavedTheScopesFollowTheSuggestion()
    {
        await using var r = await Rig.Make(tick: false);
        Assert.Equal(["notes.read", "notes.write"], Strings(NotesRow(await r.Rpc("integrations.apiList"))["oauth"]!["neededScopes"]).Order(StringComparer.Ordinal).ToArray());
        await r.SignIn();
        Assert.Equal(["notes.read", "notes.write"], Query(r.Browser.Opened[^1])["scope"].Split(' ').Order(StringComparer.Ordinal).ToArray()); // never the destructive one's admin
    }

    [Fact]
    public async Task AnEditMadeDuringARefreshIsNotOverwritten()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn();
        r.Accept = null;
        r.HoldRefresh = new SemaphoreSlim(0);
        r.Clock.Now = r.Clock.Now.AddHours(2);
        var call = r.Ada(new { action = "listNotes" });
        await Wait.UntilAsync(() => Volatile.Read(ref r.Refreshes) == 1, "the refresh started");

        var view = r.Conns.Get(id)!;
        var edit = r.Rpc("integrations.connectionSave", new JsonObject { ["id"] = id, ["name"] = "Notes login", ["fields"] = FieldsOf(view, clientId: "another-app") });
        await Task.Delay(200, Ct);
        Assert.False(edit.IsCompleted); // it waits for the refresh
        r.HoldRefresh.Release();
        await call;
        await edit;

        var after = r.Conns.Get(id)!;
        Assert.Equal("Notes login", after.Name);
        Assert.Equal("another-app", after.Fields.Single(f => f.Key == "client_id").Shown);
        Assert.False(r.Conns.Secrets(id).ContainsKey("access_token"));
        Assert.Equal("needs_reconnect", after.Status);
    }

    [Fact]
    public async Task ChangingAnEndpointNeedsANewSignIn()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn();
        var view = r.Conns.Get(id)!;
        var fields = FieldsOf(view);
        foreach (var f in fields) if (f!["key"]!.GetValue<string>() == "token_endpoint") f["value"] = "https://idp.example.test/oauth/token2";
        await r.Rpc("integrations.connectionSave", new JsonObject { ["id"] = id, ["name"] = view.Name, ["fields"] = fields });
        var after = r.Conns.Get(id)!;
        Assert.Equal("https://idp.example.test/oauth/token2", after.Fields.Single(f => f.Key == "token_endpoint").Shown);
        Assert.False(r.Conns.Secrets(id).ContainsKey("refresh_token"));
        Assert.Equal("sign in again: the app or the addresses changed", after.StatusReason);

        // a plain http address on another computer is refused, and nothing changes
        fields = FieldsOf(after);
        foreach (var f in fields) if (f!["key"]!.GetValue<string>() == "token_endpoint") f["value"] = "http://idp.example.test/token";
        var e = await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.connectionSave", new JsonObject { ["id"] = id, ["name"] = view.Name, ["fields"] = fields }));
        Assert.Contains("must start with https://", e.Message);
        Assert.Equal("https://idp.example.test/oauth/token2", r.Conns.Get(id)!.Fields.Single(f => f.Key == "token_endpoint").Shown);
    }

    [Fact]
    public async Task AGivenTokenUrlMustBeTheSpecs()
    {
        await using var r = await Rig.Make();
        var e = await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.oauthSignIn", new JsonObject
        {
            ["api"] = "notes", ["clientId"] = "notes-app", ["tokenUrl"] = "https://idp.example.test/other/token",
        }));
        Assert.Contains($"the token address must be the one notes's spec gives ({TokenUrl})", e.Message);
        Assert.Empty(r.Browser.Opened);
        // the spec's own address, given back, is fine
        var ok = await r.Rpc("integrations.oauthSignIn", new JsonObject { ["api"] = "notes", ["clientId"] = "notes-app", ["tokenUrl"] = TokenUrl, ["authorizeUrl"] = Authorize });
        Assert.True(ok!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ABearerApiTakesAnOAuthConnection()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn();
        // the person set the API to a bearer token and kept the OAuth sign-in: its access token is the bearer
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "notes", ["auth"] = new JsonObject { ["type"] = "bearer" }, ["connectionId"] = id });
        Assert.StartsWith("HTTP 200", await r.Ada(new { action = "listNotes" }));
        Assert.Equal("Bearer " + At1, r.ApiAuth[^1]);

        // a Connection used by another API is not signed in again for this one
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "notes2", ["baseUrl"] = "https://api.notes.example/v2", ["connectionId"] = id });
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "notes", ["auth"] = null });
        await r.Discover();
        var e = await Assert.ThrowsAnyAsync<Exception>(() => r.SignIn(connectionId: id));
        Assert.Contains("is also used by notes2; sign in as a new connection instead", e.Message);
    }

    // ------------------------------------------------------------------ edge cases

    [Fact]
    public async Task SigningInAgainIntoAServersConnectionIsRefusedWithoutAnApiToo()
    {
        await using var r = await Rig.Make();
        var (id, _) = await McpSignIn(r, "https://mcp.notes.example/mcp");
        var before = r.Conns.Get(id)!.Fields.Select(f => (f.Key, f.Shown)).ToList();
        var opened = r.Browser.Opened.Count;
        var e = await Assert.ThrowsAnyAsync<Exception>(() => r.Rpc("integrations.oauthSignIn", new JsonObject
        {
            ["connectionId"] = id, ["clientId"] = "mcp-client", ["authorizeUrl"] = Authorize, ["tokenUrl"] = TokenUrl,
        }));
        Assert.Contains("'notesmcp sign-in' is the sign-in of an MCP server; sign in again in Settings → Integrations → MCP servers (Sign in)", e.Message);
        Assert.Equal(opened, r.Browser.Opened.Count);
        Assert.Equal(before, r.Conns.Get(id)!.Fields.Select(f => (f.Key, f.Shown)).ToList()); // still bound to its resource
    }

    [Fact]
    public async Task ASignInWithNoScopesAskedOrNamedIsNeverRefusedForScopes()
    {
        await using var r = await Rig.Make();
        var res = (await r.Rpc("integrations.oauthSignIn", new JsonObject { ["clientId"] = "notes-app", ["authorizeUrl"] = Authorize, ["tokenUrl"] = TokenUrl }))!;
        var id = res["connection"]!["id"]!.GetValue<long>();
        Assert.DoesNotContain(r.Conns.Get(id)!.Fields, f => f.Key == "granted_scopes"); // the grant is unknown
        await r.Rpc("integrations.apiSave", new JsonObject { ["name"] = "notes", ["connectionId"] = id });
        await r.Tick(("deleteNote", "allow")); // needs admin
        Assert.StartsWith("HTTP 204", await r.Ada(new { action = "deleteNote", @params = new { id = "7" } }));
    }

    [Fact]
    public async Task SavingOnlyTheNameAndANewSecretKeepsTheSignIn()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn(secret: ClientSecret);
        var before = r.Conns.Get(id)!.Fields.Select(f => (f.Key, f.Shown)).ToList();
        // what the dialog's Save sends: no fields
        await r.Rpc("integrations.connectionSave", new JsonObject { ["id"] = id, ["name"] = "Notes login", ["secrets"] = new JsonObject() });
        Assert.Equal("Notes login", r.Conns.Get(id)!.Name);
        Assert.Equal(before, r.Conns.Get(id)!.Fields.Select(f => (f.Key, f.Shown)).ToList());
        Assert.Equal(ClientSecret, r.Conns.Secrets(id)["client_secret"]);
        await r.Rpc("integrations.connectionSave", new JsonObject { ["id"] = id, ["name"] = "Notes login", ["secrets"] = new JsonObject { ["client_secret"] = "cs_new_supersecret_0123456789" } });
        Assert.Equal("cs_new_supersecret_0123456789", r.Conns.Secrets(id)["client_secret"]);
        Assert.Equal(At1, r.Conns.Secrets(id)["access_token"]);
        Assert.Equal("works", r.Conns.Get(id)!.Status);
    }

    [Fact]
    public async Task TestReportsAFailingCredentialStoreAsAStatus()
    {
        await using var r = await Rig.Make();
        var id = await r.SignIn();
        r.Store.FailWrites = true; // the refresh cannot save its new tokens
        var res = (await r.Rpc("integrations.connectionTest", new JsonObject { ["id"] = id }))!;
        Assert.Equal("works", res["status"]!.GetValue<string>());
        Assert.False(res["changed"]!.GetValue<bool>());
        Assert.Contains("credential store", res["reason"]!.GetValue<string>());
        Assert.DoesNotContain(At2, res.ToJsonString());
    }
}
