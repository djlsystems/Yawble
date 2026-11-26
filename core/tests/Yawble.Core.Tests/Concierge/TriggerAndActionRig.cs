using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Policy;
using Yawble.Core.Workflows.Library;
using Yawble.Core.Workflows.Running;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Connections;
using Yawble.Core.Tests.Integrations;
using Yawble.Core.Tests.Workflows;
using Yawble.Core.Tests.Teams;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// The real pieces behind workflow_trigger_set and integration_action_allow, with fakes only at the edges. A real bus,
/// the real triggers.* (UiRpc), the real workflows.* (WorkflowsRpc over a library whose directory is the real IntegrationService), and the
/// real IntegrationService with the API "sms" read from a spec FakeHttp serves (no network, no sign-in, placeholder names only).
/// Teams Alpha (agent Ada, workflow members Poller and Other), Beta and Gamma; sms is turned on for Alpha and Beta, not Gamma.
/// sms: CreateMessage (POST /messages), ListMessages (GET /messages), GetToken (GET /tokens, a sign-in action: the floor).
/// </summary>
internal sealed class TriggerAndActionRig : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public const string Host = "https://api.sms.example";

    private const string Spec = """
        {
          "openapi": "3.0.1",
          "info": { "title": "SMS", "version": "1" },
          "servers": [ { "url": "https://api.sms.example" } ],
          "paths": {
            "/messages": {
              "post": { "operationId": "CreateMessage", "summary": "Send a message",
                "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "to": { "type": "string" } } } } } } },
              "get": { "operationId": "ListMessages", "summary": "List messages" }
            },
            "/tokens": { "get": { "operationId": "GetToken", "summary": "Read the access token" } }
          }
        }
        """;

    public readonly TeamsFixture F = new();
    public readonly FakeHttp Http = new();
    public BusEngine Bus = null!;
    public IntegrationService Svc = null!;
    public WorkflowRunner Runner = null!;
    public WorkflowsRpc Workflows = null!;
    public UiRpc Ui = null!;
    public readonly ConfirmationBook Book = new();
    public long Alpha, Beta, Gamma, PollerId, OtherId;

    /// <summary>Every call the tools made onto the app's methods, in order.</summary>
    public readonly List<(string Method, JsonObject Params)> Calls = [];

    public static async Task<TriggerAndActionRig> StartAsync()
    {
        var r = new TriggerAndActionRig();
        r.Http.Add(Host + "/openapi.json", Spec);
        r.Bus = new BusEngine(r.F.Db.OpenStore(), new ScriptedRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        r.Svc = new IntegrationService(r.F.Db.ConnectionString, r.Bus, connections: new ConnectionService(new MemoryCredentialStore(), r.F.Db.ConnectionString),
            httpHandler: () => r.Http, apiCallHandler: () => r.Http);
        r.Bus.Integrations = r.Svc;
        r.Alpha = await r.Bus.CreateTeamAsync("Alpha", ct: Ct);
        await r.Bus.AddMemberAsync("Alpha", "Ada", ct: Ct);
        r.Beta = await r.Bus.CreateTeamAsync("Beta", ct: Ct);
        r.Gamma = await r.Bus.CreateTeamAsync("Gamma", ct: Ct);

        var catalog = new FakeActionCatalog();
        var library = new WorkflowLibrary(r.F.Db.ConnectionString, () => catalog, FakeChecker.Check,
            integrations: new IntegrationWorkflowDirectory(r.F.Db.ConnectionString, Task.FromResult(r.Svc)));
        var store = new WorkflowRunStore(r.F.Db.ConnectionString);
        r.Runner = new WorkflowRunner(store, library, catalog, new FakeEngine(), Path.Combine(r.F.Db.Dir, "workflows"));
        r.Bus.WorkflowMembers = r.Runner;
        r.Workflows = new WorkflowsRpc(r.Bus, library, store, r.Runner);
        var terminals = new TerminalHub(new AppEvents());
        var home = Path.Combine(r.F.Db.Dir, "home");
        r.Ui = new UiRpc(r.Bus, new PolicyStore(r.F.Db.ConnectionString), new MemberLayouts(Path.Combine(r.F.Db.Dir, "data"), Path.Combine(r.F.Db.Dir, "install"), home),
            new FakeModelCredentials(), new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, r.Bus), terminals, 16_384)
        { Workflows = r.Workflows };
        await r.Bus.StartAsync(Ct);

        await r.Svc.DispatchAsync("integrations.apiSave", new JsonObject { ["name"] = "sms", ["baseUrl"] = Host, ["docsUrl"] = Host + "/openapi.json" }, Ct);
        var started = await r.Svc.DispatchAsync("integrations.discover", new JsonObject { ["name"] = "sms", ["kind"] = "api" }, Ct);
        Assert.True(started!["started"]!.GetValue<bool>());
        await Wait.UntilAsync(async () => (await r.Svc.DispatchAsync("integrations.discoverStatus", new JsonObject { ["name"] = "sms", ["kind"] = "api" }, Ct))!["state"]!.GetValue<string>() != "running", "the spec was read");
        await r.Svc.DispatchAsync("integrations.apiEnable", new JsonObject { ["name"] = "sms", ["teamId"] = r.Alpha }, Ct);
        await r.Svc.DispatchAsync("integrations.apiEnable", new JsonObject { ["name"] = "sms", ["teamId"] = r.Beta }, Ct);

        await r.Workflows.DispatchAsync("workflows.save", new JsonObject { ["document"] = Docs.Clean() }, Ct);
        r.PollerId = (await r.Workflows.DispatchAsync("workflows.memberAdd", new JsonObject { ["teamId"] = r.Alpha, ["name"] = "Poller", ["workflow"] = "poller" }, Ct))!["memberId"]!.GetValue<long>();
        r.OtherId = (await r.Workflows.DispatchAsync("workflows.memberAdd", new JsonObject { ["teamId"] = r.Alpha, ["name"] = "Other", ["workflow"] = "poller", ["version"] = 1 }, Ct))!["memberId"]!.GetValue<long>();
        return r;
    }

    /// <summary>The app's methods as the Concierge and the companion reach them: integrations.* to the service, the rest to the window's RPC.</summary>
    public async Task<JsonNode?> Route(string method, JsonObject? p, CancellationToken ct)
    {
        lock (Calls) Calls.Add((method, (JsonObject?)p?.DeepClone() ?? []));
        return method.StartsWith("integrations.", StringComparison.Ordinal) ? await Svc.DispatchAsync(method, p, ct) : await Ui.DispatchAsync(method, p, ct);
    }

    public int Count(string method) { lock (Calls) return Calls.Count(c => c.Method == method); }

    /// <summary>A new turn of conversation c1 each call (the person's yes is a later turn than the plan).</summary>
    public ConciergeTools Tools(bool remote = false, bool byPerson = true)
    {
        var turn = new ConciergeTurn("c1", byPerson, remote);
        var empty = new Func<string, JsonObject?, CancellationToken, Task<JsonNode?>>((_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()));
        var git = new HostGit(Path.Combine(F.Db.Dir, "empty.gitconfig"));
        var actions = new ConciergeActions(Bus, empty, empty, new ConciergeGit(git, F.Repos.Pusher!), (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>([]), Book, turn: turn);
        return new ConciergeTools(Bus, new ConciergeStore(F.Db.ConnectionString), Route, () => null, "c1", null, actions, remote, turn);
    }

    /// <summary>The trigger rows of Alpha as triggers.list gives them to the window.</summary>
    public async Task<List<JsonObject>> TriggersListed(long? team = null) =>
        [.. (await Ui.DispatchAsync("triggers.list", new JsonObject { ["teamId"] = team ?? Alpha }, Ct))!.AsArray().OfType<JsonObject>()];

    /// <summary>The api_rules rows of sms (team id, pattern, decision).</summary>
    public List<(long? Team, string Pattern, string Decision)> Rules()
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(F.Db.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT r.team_id, r.pattern, r.decision FROM api_rules r JOIN apis a ON a.id = r.api_id WHERE a.name = 'sms' ORDER BY r.id;";
        using var rd = cmd.ExecuteReader();
        var list = new List<(long?, string, string)>();
        while (rd.Read()) list.Add((rd.IsDBNull(0) ? null : rd.GetInt64(0), rd.GetString(1), rd.GetString(2)));
        return list;
    }

    /// <summary>workflows.catalog's decision on an sms action for a team: "ask" (marked ask), "allow" (listed, not marked), null (not listed).</summary>
    public async Task<string?> CatalogDecision(long team, string action)
    {
        var catalog = (await Workflows.DispatchAsync("workflows.catalog", new JsonObject { ["teamId"] = team }, Ct))!.AsObject();
        var sms = catalog["integrations"]!.AsArray().OfType<JsonObject>().Single(i => i["name"]!.GetValue<string>() == "sms");
        var a = sms["actions"]!.AsArray().OfType<JsonObject>().FirstOrDefault(x => x["name"]?.GetValue<string>() == action);
        return a is null ? null : a["ask"] is JsonValue v && v.GetValue<bool>() ? "ask" : "allow";
    }

    public async ValueTask DisposeAsync()
    {
        await Runner.StopAsync();
        await Svc.DisposeAsync();
        await Bus.DisposeAsync();
        F.Dispose();
    }
}
