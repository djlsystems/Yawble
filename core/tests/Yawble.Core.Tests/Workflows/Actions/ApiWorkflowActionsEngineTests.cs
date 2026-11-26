using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Workflows.Actions;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;
using Yawble.Core.Workflows.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Yawble.Core.Tests.Integrations;
using RunCaller = Yawble.Core.Workflows.Running.WorkflowCaller;
using WorkflowRunStatus = Yawble.Core.Workflows.Engine.WorkflowRunStatus;

namespace Yawble.Core.Tests.Workflows.Actions;

/// <summary>
/// End to end: a workflow's integration step through the real workflow engine, the API workflow actions and the real
/// IntegrationService, against an in-process messaging API shaped like Twilio's Messages endpoint (FakeHttp: no network, no OS
/// credential store, placeholder credentials).
/// </summary>
public sealed class ApiWorkflowActionsEngineTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string AccountId = "AC_TEST_ACCOUNT";
    private const string Token = "test-token";
    private const string Host = "https://api.sms.example";
    private const string Messages = Host + "/2010-04-01/Accounts/" + AccountId + "/Messages.json";

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
                  "type": "object", "properties": { "To": { "type": "string" }, "From": { "type": "string" }, "Body": { "type": "string" } },
                  "required": [ "To" ] } } } } },
              "get": { "operationId": "ListMessage", "summary": "List messages" }
            },
            "/2010-04-01/Accounts/{AccountSid}/Messages/{Sid}.json": {
              "parameters": [ { "name": "AccountSid", "in": "path", "required": true, "schema": { "type": "string" } },
                              { "name": "Sid", "in": "path", "required": true, "schema": { "type": "string" } } ],
              "post": { "operationId": "UpdateMessage", "summary": "Redact a message",
                "requestBody": { "content": { "application/x-www-form-urlencoded": { "schema": { "type": "object", "properties": { "Body": { "type": "string" } } } } } } }
            }
          }
        }
        """;

    private const string Requires = """[ { "name": "sms", "kind": "api", "actions": ["CreateMessage"] } ]""";

    private const string SendSteps = """
        [ { "id": "a", "type": "integration", "use": "sms", "action": "CreateMessage",
            "args": { "To": "+15550000001", "From": "+15550000002", "Body": "Hello from a workflow" }, "into": "resp", "next": "b" },
          { "id": "b", "type": "end", "outputs": { "status": "{vars.resp.status}", "sid": "{vars.resp.body.sid}" } } ]
        """;

    private const string SendVariables = """[ { "name": "resp", "type": "ApiResponse" } ]""";

    private readonly TempDb _db = new();
    private readonly FakeHttp _http = new();
    private readonly TempFolder _temp = new();
    private readonly List<(string? ContentType, string? Body, string? Auth)> _sent = [];
    private BusEngine _engine = null!;
    private IntegrationService _svc = null!;
    private WorkflowActionKinds _kinds = null!;
    private long _alpha;

    public async ValueTask InitializeAsync()
    {
        _http.Add(Host + "/openapi.json", Spec);
        _http.Add(Messages, req =>
        {
            _sent.Add((req.Content?.Headers.ContentType?.MediaType, req.Content?.ReadAsStringAsync().Result, req.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"sid":"SM1","status":"queued"}""", Encoding.UTF8, "application/json"),
            };
        });
        _engine = new BusEngine(_db.OpenStore(), new ScriptedRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        var conns = new ConnectionService(new MemoryCredentialStore(), _db.ConnectionString);
        _svc = new IntegrationService(_db.ConnectionString, _engine, connections: conns, httpHandler: () => _http, apiCallHandler: () => _http);
        _engine.Integrations = _svc;
        _alpha = await _engine.CreateTeamAsync("Alpha", ct: Ct);
        await _engine.CreateTeamAsync("Beta", ct: Ct);
        await _engine.StartAsync(Ct);

        var conn = (await Rpc("integrations.connectionSave", new JsonObject
        {
            ["name"] = "SMS account",
            ["fields"] = new JsonArray(
                new JsonObject { ["key"] = "username", ["label"] = "Username", ["secret"] = false, ["value"] = AccountId },
                new JsonObject { ["key"] = "password", ["label"] = "Auth token", ["secret"] = true }),
            ["secrets"] = new JsonObject { ["password"] = Token },
        }))!;
        await Rpc("integrations.apiSave", new JsonObject
        {
            ["name"] = "sms", ["baseUrl"] = Host, ["docsUrl"] = Host + "/openapi.json", ["connectionId"] = conn["id"]!.GetValue<long>(),
        });
        Assert.True((await Rpc("integrations.discover", new JsonObject { ["name"] = "sms", ["kind"] = "api" }))!["started"]!.GetValue<bool>());
        await Wait.UntilAsync(async () => (await Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "sms", ["kind"] = "api" }))!["state"]!.GetValue<string>() != "running", "the spec was read");
        await Rpc("integrations.apiSave", new JsonObject
        {
            ["name"] = "sms", ["auth"] = new JsonObject { ["type"] = "basic", ["pathParams"] = new JsonObject { ["AccountSid"] = "username" } },
        });
        await Rpc("integrations.apiEnable", new JsonObject { ["name"] = "sms", ["teamId"] = _alpha });
        await Rpc("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "sms", ["kind"] = "api", ["teamId"] = _alpha,
            ["choices"] = new JsonArray(
                new JsonObject { ["path"] = "CreateMessage", ["state"] = "allow" }, new JsonObject { ["path"] = "ListMessage", ["state"] = "allow" },
                new JsonObject { ["path"] = "UpdateMessage", ["state"] = "ask" }),
        });

        _kinds = new WorkflowActionKinds(new Dictionary<string, IWorkflowActionsProvider>
        {
            [ApiWorkflowActionsProvider.Kind] = new ApiWorkflowActionsProvider(new IntegrationWorkflowApi(_db.ConnectionString, _svc)),
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _svc.DisposeAsync();
        await _engine.DisposeAsync();
        _temp.Dispose();
        _db.Dispose();
    }

    private async Task<JsonNode?> Rpc(string method, JsonObject p) => await _svc.DispatchAsync(method, p, Ct);

    private Task<WorkflowRunResult> Run(WorkflowDocument doc, bool dryRun = false, string team = "Alpha")
    {
        var caller = new RunCaller(_alpha, team, 7, "Texter", 42, new Dictionary<string, string> { ["sms"] = "sms" }, dryRun);
        var actions = _kinds.For(caller);
        return WorkflowEngine.RunAsync(new WorkflowRunRequest
        {
            Document = doc,
            Actions = actions,
            Catalog = WorkflowActionKinds.CatalogFor(actions, _kinds),
            Schemas = BuiltInSchemas.Lookup,
            DataFolder = _temp.Sub("data"),
            DryRun = dryRun,
            Input = JsonNode.Parse("""{ "triggerId": "t1", "kind": "once", "firedAt": "2026-11-23T17:59:00Z" }"""),
        }, new RecordingJournal(), Ct);
    }

    private List<(string Member, string Decision, string? Via)> Calls()
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(_db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT member, decision, via FROM integration_calls WHERE integration = 'api:sms' ORDER BY id;";
        using var rd = cmd.ExecuteReader();
        var list = new List<(string, string, string?)>();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
        return list;
    }

    [Fact]
    public void TheWorkflow_PassesTheCheck_AgainstTheIntegrationsCatalog()
    {
        var doc = WorkflowSamples.Workflow(SendSteps, variables: SendVariables, requires: Requires);
        var check = WorkflowChecker.Check(doc, BuiltInSchemas.Lookup, _kinds.Catalog);
        Assert.True(check.Ok, string.Join("; ", check.Issues.Select(i => i.Message)));

        var unknown = WorkflowSamples.Workflow(SendSteps.Replace("CreateMessage", "DeleteMessage"), variables: SendVariables,
            requires: Requires.Replace("CreateMessage", "DeleteMessage"));
        Assert.Contains(WorkflowChecker.Check(unknown, BuiltInSchemas.Lookup, _kinds.Catalog).Issues, i => i.Code == WorkflowCheckCodes.UnknownAction);
    }

    [Fact]
    public async Task AnIntegrationStep_SendsOneSms_ThroughTheRealIntegrationService()
    {
        var r = await Run(WorkflowSamples.Workflow(SendSteps, variables: SendVariables, requires: Requires));

        Assert.True(r.Status == WorkflowRunStatus.Succeeded, $"failed at {r.FailedStepId}: {r.Reason}");
        Assert.Equal(201, (long?)r.Outputs!["status"]);
        Assert.Equal("SM1", (string?)r.Outputs["sid"]);
        var sent = Assert.Single(_sent);
        Assert.Equal("application/x-www-form-urlencoded", sent.ContentType);
        Assert.Equal("To=%2B15550000001&From=%2B15550000002&Body=Hello+from+a+workflow", sent.Body);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(AccountId + ":" + Token)), sent.Auth);
        var logged = Assert.Single(Calls());
        Assert.Equal(("Alpha/Texter", "allow", IntegrationService.WorkflowVia), logged);
    }

    [Fact]
    public async Task ADryRun_SendsNothing_AndLogsWhatItWouldDo()
    {
        var r = await Run(WorkflowSamples.Workflow(SendSteps.Replace("\"status\": \"{vars.resp.status}\", \"sid\": \"{vars.resp.body.sid}\"", ""), variables: SendVariables, requires: Requires), dryRun: true);

        Assert.True(r.Status == WorkflowRunStatus.Succeeded, $"failed at {r.FailedStepId}: {r.Reason}");
        Assert.Empty(_sent);
        Assert.Contains(r.Log, l => l.StartsWith("would CreateMessage on sms", StringComparison.Ordinal));
        Assert.Empty(Calls());
    }

    [Fact]
    public async Task AnAskAction_FailsTheStep_NeedsApproval()
    {
        var requires = """[ { "name": "sms", "kind": "api", "actions": ["UpdateMessage"] } ]""";
        var doc = WorkflowSamples.Workflow("""
            [ { "id": "a", "type": "integration", "use": "sms", "action": "UpdateMessage", "args": { "Sid": "SM1", "Body": "" }, "next": null } ]
            """, requires: requires);
        var r = await Run(doc);

        Assert.Equal(WorkflowRunStatus.Failed, r.Status);
        Assert.Equal("a", r.FailedStepId);
        Assert.Contains("needs approval: allow UpdateMessage for Alpha", r.Reason);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task ATeamWithoutTheApi_FailsTheStep_WithTheRefusal()
    {
        var r = await Run(WorkflowSamples.Workflow(SendSteps, variables: SendVariables, requires: Requires), team: "Beta");

        Assert.Equal(WorkflowRunStatus.Failed, r.Status);
        Assert.Contains("refused: sms is not turned on for Beta", r.Reason);
        Assert.Empty(_sent);
    }
}
