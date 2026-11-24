using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Email;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Scripts.Actions;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using IntegrationCaller = Yawble.Core.Integrations.ScriptCaller;
using RunStatus = Yawble.Core.Scripts.Running.ScriptRunStatus;

namespace Yawble.Core.Tests.Scripts;

/// <summary>
/// A script's steps name the person's integrations by their real names: requires is derived from the steps, a member may
/// replace an integration with another of the same kind turned on for its team, and documents in the alias form (requires
/// names bound per member) keep checking and running. Through the real checker, the real engine, the real Email and API
/// actions over fakes (no mailbox, no API), and the script RPCs.
/// </summary>
public sealed class ScriptRealNamesTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Support = "Yawble DistinctID Support";
    private const string Other = "Other inbox";
    private const string Twilio = "Twilio";

    private readonly TempDb _db = new();
    private readonly FakeMail _mail = new();
    private readonly FakeSms _sms = new();
    private BusEngine _bus = null!;
    private ScriptLibrary _library = null!;
    private ScriptRunStore _store = null!;
    private ScriptRunner _runner = null!;
    private ScriptsRpc _rpc = null!;
    private long _alpha, _beta, _gamma;
    private readonly List<ScriptIntegrationInfo> _integrations = [];

    private async Task InitAsync()
    {
        _bus = new BusEngine(_db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        _alpha = await _bus.CreateTeamAsync("Alpha", ct: Ct);
        _beta = await _bus.CreateTeamAsync("Beta", ct: Ct);
        _gamma = await _bus.CreateTeamAsync("Gamma", ct: Ct);
        // One mailbox turned on for two teams, another for Alpha only, and the SMS API for every team.
        _integrations.Add(new ScriptIntegrationInfo(Support, "email", false, [_alpha, _beta]));
        _integrations.Add(new ScriptIntegrationInfo(Other, "email", false, [_alpha]));
        _integrations.Add(new ScriptIntegrationInfo(Twilio, "api", true, []));
        var directory = new ScriptIntegrationSet(_integrations, (api, team) => api == Twilio ? FakeSms.Ops : []);
        var actions = new ScriptActionKinds(new Dictionary<string, IScriptActionsProvider>
        {
            [EmailActions.Kind] = new EmailScriptActionsProvider(_mail),
            [ApiScriptActionsProvider.Kind] = new ApiScriptActionsProvider(_sms),
        });
        _library = new ScriptLibrary(_db.ConnectionString, () => actions.Catalog, integrations: directory);
        _store = new ScriptRunStore(_db.ConnectionString);
        _runner = new ScriptRunner(_store, _library, actions, SeamScriptEngine.Instance, Path.Combine(_db.Dir, "scripts"));
        _rpc = new ScriptsRpc(_bus, _library, _store, _runner);
    }

    public async ValueTask DisposeAsync()
    {
        if (_runner is not null) await _runner.StopAsync();
        if (_bus is not null) await _bus.DisposeAsync();
        _db.Dispose();
    }

    private Task<JsonNode?> Call(string method, JsonObject p) => _rpc.DispatchAsync(method, p, Ct);

    private static JsonObject Settings() => new() { ["recipients"] = new JsonArray("+15550000001"), ["fromNumber"] = "+15550000000" };

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>The Inbox SMS alert with its steps naming the integrations, and requires left empty (the save derives it).</summary>
    private static JsonObject RealNamed(string mail = Support)
    {
        var doc = Parse(AliasInboxSmsAlert.Replace("\"use\": \"mail\"", $"\"use\": \"{mail}\"", StringComparison.Ordinal)
            .Replace("\"use\": \"sms\"", $"\"use\": \"{Twilio}\"", StringComparison.Ordinal));
        doc["requires"] = new JsonArray();
        return doc;
    }

    private async Task<long> AddMemberAsync(long teamId, string name, JsonObject? bindings = null, int? version = null)
    {
        var p = new JsonObject { ["teamId"] = teamId, ["name"] = name, ["script"] = "inbox-sms-alert", ["settings"] = Settings() };
        if (bindings is not null) p["bindings"] = bindings;
        if (version is { } v) p["version"] = v;
        return (await Call("scripts.memberAdd", p))!["memberId"]!.GetValue<long>();
    }

    private Task<ScriptRunOutcome> Run(long member) =>
        _runner.RunMemberAsync(member, "run now", ScriptPayloads.TriggerFired(1, "once", DateTimeOffset.Parse("2026-11-23T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), Ct);

    private static string Why(ScriptRunRow run) => $"{run.Status} at {run.FailedStepId}: {run.Reason}";

    // ------------------------------------------------------------------ the document

    [Fact]
    public async Task AStepNamesTheRealIntegration_AndRequiresIsDerived_AStaleOneIsAWarning()
    {
        await InitAsync();
        var doc = RealNamed();
        doc["requires"] = new JsonArray(new JsonObject { ["name"] = Twilio, ["kind"] = "api", ["actions"] = new JsonArray("CreateMessage", "ListMessages") });

        var check = (await Call("scripts.check", new JsonObject { ["document"] = doc.DeepClone() }))!.AsObject();
        Assert.True(check["ok"]!.GetValue<bool>(), check.ToJsonString());
        var warning = Assert.Single(check["warnings"]!.AsArray())!;
        Assert.Equal(ScriptLibrary.StaleRequires, warning["code"]!.GetValue<string>());

        var saved = (await Call("scripts.save", new JsonObject { ["document"] = doc.DeepClone() }))!.AsObject();
        Assert.True(saved["ok"]!.GetValue<bool>());
        var stored = _library.Get(saved["id"]!.GetValue<long>()).Document;
        var expected = JsonNode.Parse($$"""
            [ { "name": "{{Support}}", "kind": "email", "actions": ["list"] },
              { "name": "{{Twilio}}", "kind": "api", "actions": ["CreateMessage"] } ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, stored["requires"]), stored["requires"]!.ToJsonString());

        // Derived and current: no warning.
        var again = (await Call("scripts.check", new JsonObject { ["document"] = stored.DeepClone() }))!.AsObject();
        Assert.Empty(again["warnings"]!.AsArray());
        Assert.True(again["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ANameThatIsNoIntegrationAndNotInRequires_IsAnIssue()
    {
        await InitAsync();
        var check = (await Call("scripts.check", new JsonObject { ["document"] = RealNamed(mail: "No such inbox") }))!.AsObject();
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Contains(check["issues"]!.AsArray(), i => i!["code"]!.GetValue<string>() == ScriptCheckCodes.UnknownRequirement
            && i["message"]!.GetValue<string>().Contains("'No such inbox' is not in requires", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ members

    [Fact]
    public async Task OneIntegrationServesEveryTeamItIsTurnedOnFor_AndAnotherTeamIsRefusedWithTheReason()
    {
        await InitAsync();
        await Call("scripts.save", new JsonObject { ["document"] = RealNamed() });

        var a = await AddMemberAsync(_alpha, "Watcher");
        var b = await AddMemberAsync(_beta, "Watcher");
        foreach (var member in new[] { a, b })
        {
            var outcome = await Run(member);
            Assert.True(outcome.Run.Status == RunStatus.Succeeded, Why(outcome.Run));
        }
        Assert.Equal([Support, Support], _mail.Used.Select(u => u.Integration));
        Assert.Equal(["Alpha", "Beta"], _mail.Used.Select(u => u.Team));

        var refused = await Assert.ThrowsAsync<RpcException>(() => AddMemberAsync(_gamma, "Watcher"));
        Assert.Equal("bad_script", refused.Code);
        Assert.Contains($"the script uses {Support}, which is not turned on for Gamma", refused.Message);
    }

    [Fact]
    public async Task AnOverride_IsShown_UsedAtRunTime_AndClearedAgain()
    {
        await InitAsync();
        await Call("scripts.save", new JsonObject { ["document"] = RealNamed() });
        var member = await AddMemberAsync(_alpha, "Watcher", new JsonObject { [Support] = Other });

        var got = (await Call("scripts.memberGet", new JsonObject { ["memberId"] = member }))!.AsObject();
        var expected = JsonNode.Parse($$"""
            [ { "name": "{{Support}}", "kind": "email", "uses": "{{Other}}", "overridden": true },
              { "name": "{{Twilio}}", "kind": "api", "uses": "{{Twilio}}", "overridden": false } ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, got["integrations"]), got["integrations"]!.ToJsonString());

        var outcome = await Run(member);
        Assert.True(outcome.Run.Status == RunStatus.Succeeded, Why(outcome.Run));
        Assert.Equal(Other, Assert.Single(_mail.Used).Integration);
        Assert.Equal(Twilio, Assert.Single(_sms.Used));

        // Cleared: the member uses the integration the steps name.
        var cleared = (await Call("scripts.memberUpdate", new JsonObject { ["memberId"] = member, ["bindings"] = new JsonObject() }))!.AsObject();
        Assert.False(cleared["integrations"]![0]!["overridden"]!.GetValue<bool>());
        Assert.Equal(Support, cleared["integrations"]![0]!["uses"]!.GetValue<string>());
        await Run(member);
        Assert.Equal(Support, _mail.Used[^1].Integration);
    }

    [Fact]
    public async Task AnOverrideOfAnotherKind_OrNotTurnedOnForTheTeam_IsRefusedWithTheReason()
    {
        await InitAsync();
        await Call("scripts.save", new JsonObject { ["document"] = RealNamed() });

        var kind = await Assert.ThrowsAsync<RpcException>(() => AddMemberAsync(_alpha, "Watcher", new JsonObject { [Support] = Twilio }));
        Assert.Contains($"'{Support}' cannot be replaced by {Twilio}: {Support} is an Email integration and {Twilio} is an API", kind.Message);

        var off = await Assert.ThrowsAsync<RpcException>(() => AddMemberAsync(_beta, "Watcher", new JsonObject { [Support] = Other }));
        Assert.Contains($"'{Support}' cannot be replaced by {Other}: {Other} is not turned on for Beta", off.Message);

        var none = await Assert.ThrowsAsync<RpcException>(() => AddMemberAsync(_alpha, "Watcher", new JsonObject { [Support] = "Nowhere" }));
        Assert.Contains("there is no integration of that name", none.Message);

        // And on an update too: the member keeps what it had.
        var member = await AddMemberAsync(_alpha, "Watcher");
        await Assert.ThrowsAsync<RpcException>(() => Call("scripts.memberUpdate", new JsonObject { ["memberId"] = member, ["bindings"] = new JsonObject { [Support] = Twilio } }));
        Assert.Empty(_store.Member(member)!.Bindings);
    }

    // ------------------------------------------------------------------ the alias form

    [Fact]
    public async Task TheLiveInboxWatcherShape_V3WithBindings_ChecksAndRunsUnchanged()
    {
        await InitAsync();
        // Three saves of the alias form; the member is pinned to v3 and binds mail and sms to real integrations.
        for (var i = 0; i < 3; i++)
        {
            var saved = (await Call("scripts.save", new JsonObject { ["document"] = Parse(AliasInboxSmsAlert) }))!.AsObject();
            Assert.True(saved["ok"]!.GetValue<bool>(), saved["check"]!.ToJsonString());
            Assert.Empty(saved["check"]!["warnings"]!.AsArray());
        }
        var v3 = _library.Get(_library.ScriptId("inbox-sms-alert"), 3);
        Assert.True(JsonNode.DeepEquals(Parse(AliasInboxSmsAlert), v3.Document)); // saved as written
        var member = await AddMemberAsync(_alpha, "InboxWatcher", new JsonObject { ["mail"] = Support, ["sms"] = Twilio }, version: 3);

        var got = (await Call("scripts.memberGet", new JsonObject { ["memberId"] = member }))!.AsObject();
        Assert.Equal("mail", got["integrations"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(Support, got["integrations"]![0]!["uses"]!.GetValue<string>());
        Assert.False(got["integrations"]![0]!["overridden"]!.GetValue<bool>());

        var outcome = await Run(member);
        Assert.True(outcome.Run.Status == RunStatus.Succeeded, Why(outcome.Run));
        Assert.Equal(3, outcome.Run.Version);
        Assert.Equal(Support, Assert.Single(_mail.Used).Integration);
        Assert.Equal([Twilio], _sms.Used);
        Assert.Equal("m2", _store.State(member).Variables["lastId"]!.GetValue<string>());

        // An alias still needs its binding.
        var missing = await Assert.ThrowsAsync<RpcException>(() => AddMemberAsync(_alpha, "Second", new JsonObject { ["sms"] = Twilio }, version: 3));
        Assert.Contains("'mail' needs a binding", missing.Message);
    }

    [Fact]
    public async Task UseRealNames_RewritesAnAliasDocument_AndSaysWhatItCouldNotResolve()
    {
        await InitAsync();
        var result = (await Call("scripts.useRealNames", new JsonObject
        {
            ["document"] = AliasInboxSmsAlert, ["bindings"] = new JsonObject { ["mail"] = Support },
        }))!.AsObject();
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["mail"] = Support }, result["renamed"]));
        Assert.Equal(["sms"], result["unresolved"]!.AsArray().Select(n => n!.GetValue<string>()));
        var doc = result["document"]!.AsObject();
        Assert.Equal(Support, doc["steps"]![0]!["use"]!.GetValue<string>());
        Assert.Equal("sms", doc["steps"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == "text")!["use"]!.GetValue<string>());

        // With both bound (sms by an alias binding to Twilio): nothing left, and the result checks clean with requires derived.
        var both = (await Call("scripts.useRealNames", new JsonObject
        {
            ["document"] = Parse(AliasInboxSmsAlert), ["bindings"] = new JsonObject { ["mail"] = Support, ["sms"] = "twilio" },
        }))!.AsObject();
        Assert.Empty(both["unresolved"]!.AsArray());
        Assert.Equal(Twilio, both["renamed"]!["sms"]!.GetValue<string>()); // as Settings > Integrations names it
        var rewritten = both["document"]!.AsObject();
        Assert.True(JsonNode.DeepEquals(RealNamed()["steps"], rewritten["steps"]));
        var expected = JsonNode.Parse($$"""
            [ { "name": "{{Support}}", "kind": "email", "actions": ["list"] },
              { "name": "{{Twilio}}", "kind": "api", "actions": ["CreateMessage"] } ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, rewritten["requires"]), rewritten["requires"]!.ToJsonString());
        var check = (await Call("scripts.check", new JsonObject { ["document"] = rewritten.DeepClone() }))!.AsObject();
        Assert.True(check["ok"]!.GetValue<bool>(), check.ToJsonString());
        Assert.Empty(check["warnings"]!.AsArray());
    }

    // ------------------------------------------------------------------ the catalog

    [Fact]
    public async Task TheCatalog_GivesEmailActionsWithTypedParams_TheApisAllowedAndAskOperations_AndTheFunctions()
    {
        await InitAsync();
        var catalog = (await Call("scripts.catalog", new JsonObject { ["teamId"] = _beta }))!.AsObject();
        var integrations = catalog["integrations"]!.AsArray();
        Assert.Equal([Support, Other, Twilio], integrations.Select(i => i!["name"]!.GetValue<string>()));
        Assert.Equal([true, false, true], integrations.Select(i => i!["enabledForTeam"]!.GetValue<bool>()));

        var email = integrations[0]!;
        Assert.Equal("email", email["kind"]!.GetValue<string>());
        Assert.Equal(EmailActions.All.Select(a => a.Name), email["actions"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()));
        var list = email["actions"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "list")!;
        Assert.Equal("EmailList", list["result"]!.GetValue<string>());
        var folder = list["params"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "folder")!;
        Assert.Equal(("string", true), (folder["type"]!.GetValue<string>(), folder["required"]!.GetValue<bool>()));
        Assert.Contains("INBOX", folder["description"]!.GetValue<string>());
        var max = list["params"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "max")!;
        Assert.Equal(("int", false), (max["type"]!.GetValue<string>(), max["required"]!.GetValue<bool>()));
        var since = list["params"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "since")!;
        Assert.Equal("datetime", since["type"]!.GetValue<string>());
        Assert.Equal("list<EmailMessage>", email["actions"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "search")!["result"]!.GetValue<string>());

        var api = integrations[2]!["actions"]!.AsArray();
        Assert.Equal(["CreateMessage", "ListMessages"], api.Select(a => a!["name"]!.GetValue<string>()));
        Assert.Null(api[0]!["ask"]);
        Assert.True(api[1]!["ask"]!.GetValue<bool>());
        var to = api[0]!["params"]!.AsArray()[0]!;
        Assert.Equal(("To", "string", true, "The phone to text."),
            (to["name"]!.GetValue<string>(), to["type"]!.GetValue<string>(), to["required"]!.GetValue<bool>(), to["description"]!.GetValue<string>()));
        Assert.Equal("int", api[1]!["params"]!.AsArray()[0]!["type"]!.GetValue<string>());

        var functions = catalog["functions"]!.AsArray();
        Assert.Equal(ScriptExpressionParser.Functions.Keys.Order(StringComparer.Ordinal),
            functions.Select(f => f!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal("count(list) -> int", functions.Single(f => f!["name"]!.GetValue<string>() == "count")!["signature"]!.GetValue<string>());

        // Without a team: whether each is turned on is not said.
        var any = (await Call("scripts.catalog", new JsonObject()))!["integrations"]!.AsArray();
        Assert.All(any, i => Assert.Null(i!["enabledForTeam"]));
    }

    // ------------------------------------------------------------------ schemas

    [Fact]
    public async Task SchemasGet_GivesABuiltInsFullJsonSchema_AndUsageListsTheScriptsVariablesAndSteps()
    {
        await InitAsync();
        var message = (await Call("schemas.get", new JsonObject { ["name"] = "EmailMessage" }))!.AsObject();
        Assert.True(message["builtIn"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(BuiltInSchemas.EmailMessage.Schema, message["schema"]));
        Assert.Equal("EmailAddress", message["schema"]!["properties"]!["from"]!["$ref"]!.GetValue<string>());
        Assert.Equal("An email message, without its attachments.", message["description"]!.GetValue<string>());

        await Call("scripts.save", new JsonObject { ["document"] = Parse(AliasInboxSmsAlert) });
        var usage = (await Call("schemas.usage", new JsonObject { ["name"] = "EmailMessage" }))!["scripts"]!.AsArray();
        var use = Assert.Single(usage)!;
        Assert.Equal(("Inbox SMS alert", 1), (use["name"]!.GetValue<string>(), use["version"]!.GetValue<int>()));
        Assert.Equal(["last"], use["variables"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Contains("mark", use["steps"]!.AsArray().Select(s => s!.GetValue<string>()));
        var list = Assert.Single((await Call("schemas.usage", new JsonObject { ["name"] = "EmailList" }))!["scripts"]!.AsArray())!;
        Assert.Equal(["res"], list["variables"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Empty((await Call("schemas.usage", new JsonObject { ["name"] = "FolderChange" }))!["scripts"]!.AsArray());
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => Call("schemas.usage", new JsonObject { ["name"] = "Nope" }))).Code);
    }

    // ------------------------------------------------------------------ fakes

    /// <summary>The Inbox SMS alert in the alias form, as the live InboxWatcher member runs it (v3).</summary>
    private const string AliasInboxSmsAlert = """
        {
          "format": "yawble-script/1",
          "id": "inbox-sms-alert",
          "name": "Inbox SMS alert",
          "description": "Texts the subject and sender of every new email to a list of phones.",
          "trigger": { "accepts": ["every", "cron", "once"] },
          "settings": [
            { "name": "recipients", "type": "list<string>", "label": "Phones to text" },
            { "name": "fromNumber", "type": "string", "label": "The SMS number that sends" },
            { "name": "folder", "type": "string", "label": "Mail folder", "default": "INBOX" },
            { "name": "since", "type": "datetime", "label": "First run: mail received after", "default": "2026-11-20T00:00:00Z" }
          ],
          "requires": [
            { "name": "mail", "kind": "email", "actions": ["list"] },
            { "name": "sms", "kind": "api", "actions": ["CreateMessage"] }
          ],
          "variables": [
            { "name": "lastId", "type": "string", "scope": "saved" },
            { "name": "res", "type": "EmailList", "scope": "run" },
            { "name": "last", "type": "EmailMessage", "scope": "run" },
            { "name": "sent", "type": "int", "scope": "run", "initial": 0 }
          ],
          "start": "list",
          "steps": [
            { "id": "list", "type": "integration", "use": "mail", "action": "list",
              "args": { "folder": "{settings.folder}", "after": "{vars.lastId}", "since": "{settings.since}", "max": 200 }, "into": "res", "next": "any" },
            { "id": "any", "type": "if", "when": { "left": "{count(vars.res.messages)}", "op": "gt", "right": 0 }, "then": "each", "else": "done" },
            { "id": "each", "type": "forEach", "items": "{vars.res.messages}", "as": "m", "body": "phones", "then": "keep" },
            { "id": "phones", "type": "forEach", "items": "{settings.recipients}", "as": "to", "body": "text", "then": null },
            { "id": "text", "type": "integration", "use": "sms", "action": "CreateMessage",
              "args": { "To": "{to}", "From": "{settings.fromNumber}", "Body": "New email: {m.subject} ({m.from.address})" }, "next": "count" },
            { "id": "count", "type": "set", "var": "sent", "value": "{vars.sent + 1}", "next": null },
            { "id": "keep", "type": "set", "var": "last", "value": "{last(vars.res.messages)}", "next": "mark" },
            { "id": "mark", "type": "set", "var": "lastId", "value": "{vars.last.id}", "next": "done" },
            { "id": "done", "type": "end" }
          ]
        }
        """;

    /// <summary>A mailbox that answers every list with one new message the first time and two after; records which integration each call used.</summary>
    private sealed class FakeMail : IScriptEmail
    {
        public List<(string Team, string Integration)> Used { get; } = [];

        public Task<T> UseAsync<T>(IntegrationCaller caller, string integration, string action, JsonObject args, Func<EmailMailbox, Task<T>> work, CancellationToken ct)
        {
            lock (Used) Used.Add((caller.Team, integration));
            JsonNode list = new JsonObject
            {
                ["messages"] = new JsonArray(ScriptSamples.Email("m2", "Order", "shop@example.com", "2026-11-23T10:00:00Z")),
                ["cursorReset"] = false,
            };
            return Task.FromResult((T)(object)list);
        }
    }

    /// <summary>The SMS API: CreateMessage (allowed) and ListMessages (ask); records which integration each call used.</summary>
    private sealed class FakeSms : IScriptApi
    {
        public static readonly IReadOnlyList<ScriptOperation> Ops =
        [
            new("CreateMessage", "POST", "/Messages.json", "Send a message", "allow",
                [new("To", "body", true, "string", "The phone to text."), new("From", "body", true, "string"), new("Body", "body", true, "string")],
                "application/x-www-form-urlencoded"),
            new("ListMessages", "GET", "/Messages.json", "List messages", "ask", [new("PageSize", "query", false, "integer")], null),
        ];

        public List<string> Used { get; } = [];

        public Task<ScriptApiResult> CallAsync(IntegrationCaller caller, string integration, string action, JsonObject args, CancellationToken ct)
        {
            lock (Used) Used.Add(integration);
            return Task.FromResult(new ScriptApiResult(201, "application/json", "{\"sid\":\"test-sid\"}"));
        }

        public IReadOnlyList<string> Teams() => ["Alpha", "Beta", "Gamma"];

        public IReadOnlyList<ScriptOperation> Operations(string team) => Ops;
    }
}
