using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;

namespace Yawble.Core.Tests.Scripts;

/// <summary>Card 584 (B131): every scripts. and schemas. RPC method, and a Visitor's refusal of every change.</summary>
public class ScriptsRpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public required TempDb Db { get; init; }
        public required BusEngine Bus { get; init; }
        public required ScriptsRpc Rpc { get; init; }
        public required ScriptRunner Runner { get; init; }
        public long TeamId { get; init; }

        public Task<JsonNode?> CallAsync(string method, JsonObject? p = null) => Rpc.DispatchAsync(method, p, Ct);

        public async ValueTask DisposeAsync()
        {
            await Runner.StopAsync();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static async Task<Rig> RigAsync()
    {
        var db = new TempDb();
        var bus = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) });
        var teamId = await bus.CreateTeamAsync("Alpha", ct: Ct);
        var catalog = new FakeActionCatalog(("email", "list"));
        var library = new ScriptLibrary(db.ConnectionString, () => catalog, FakeChecker.Check);
        var store = new ScriptRunStore(db.ConnectionString);
        var runner = new ScriptRunner(store, library, catalog, new FakeEngine(), Path.Combine(db.Dir, "scripts"));
        bus.ScriptMembers = runner;
        return new Rig { Db = db, Bus = bus, Rpc = new ScriptsRpc(bus, library, store, runner), Runner = runner, TeamId = teamId };
    }

    private static JsonObject Doc(bool withMail = false) => JsonNode.Parse(withMail
        ? Docs.Clean(requires: new JsonArray(new JsonObject { ["name"] = "mail", ["kind"] = "email", ["actions"] = new JsonArray("list") }))
        : Docs.Clean())!.AsObject();

    [Fact]
    public async Task TheDocumentsLayoutSection_SurvivesSaveGetExportAndImport_Unchanged()
    {
        await using var r = await RigAsync();
        // The script editor keeps Vue Flow node positions in a top-level layout section the library does not interpret.
        var layout = new JsonObject
        {
            ["nodes"] = new JsonObject
            {
                ["a"] = new JsonObject { ["x"] = 120.5, ["y"] = -40, ["collapsed"] = false },
                ["c"] = new JsonObject { ["x"] = 0, ["y"] = 300, ["note"] = "end here", ["extra"] = new JsonArray(1, "two", null) },
            },
            ["viewport"] = new JsonObject { ["x"] = 10, ["y"] = 20, ["zoom"] = 0.75 },
        };
        var doc = Doc();
        doc["layout"] = layout.DeepClone();
        await r.CallAsync("scripts.save", new JsonObject { ["document"] = doc });

        var get = (await r.CallAsync("scripts.get", new JsonObject { ["script"] = "poller" }))!.AsObject();
        Assert.True(JsonNode.DeepEquals(layout, get["document"]!["layout"]));
        Assert.True(JsonNode.DeepEquals(doc, get["document"]));

        var export = (await r.CallAsync("scripts.export", new JsonObject { ["script"] = "poller" }))!.AsObject();
        await r.CallAsync("scripts.import", new JsonObject { ["json"] = export["json"]!.GetValue<string>() });
        var v2 = (await r.CallAsync("scripts.get", new JsonObject { ["script"] = "poller", ["version"] = 2 }))!.AsObject();
        Assert.True(JsonNode.DeepEquals(layout, v2["document"]!["layout"]));
    }

    [Fact]
    public async Task TheLibraryMethods_SaveListGetCheckVersionsExportImport()
    {
        await using var r = await RigAsync();
        var saved = (await r.CallAsync("scripts.save", new JsonObject { ["document"] = Doc() }))!.AsObject();
        Assert.Equal((1, true, false), (saved["version"]!.GetValue<int>(), saved["ok"]!.GetValue<bool>(), saved["draft"]!.GetValue<bool>()));
        var draft = (await r.CallAsync("scripts.save", new JsonObject { ["document"] = Docs.Draft() }))!.AsObject(); // JSON text works too
        Assert.True(draft["draft"]!.GetValue<bool>());

        var list = (await r.CallAsync("scripts.list"))!.AsArray();
        var item = Assert.Single(list)!.AsObject();
        Assert.Equal(("poller", 2, 1), (item["key"]!.GetValue<string>(), item["latestVersion"]!.GetValue<int>(), item["runnableVersion"]!.GetValue<int>()));

        var get = (await r.CallAsync("scripts.get", new JsonObject { ["script"] = "poller", ["version"] = 1 }))!.AsObject();
        Assert.Equal("Poller", get["document"]!["name"]!.GetValue<string>());
        Assert.True(get["check"]!["ok"]!.GetValue<bool>());
        var check = (await r.CallAsync("scripts.check", new JsonObject { ["document"] = Docs.Draft() }))!.AsObject();
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Equal(2, (await r.CallAsync("scripts.versions", new JsonObject { ["script"] = saved["id"]!.GetValue<long>() }))!.AsArray().Count);

        var export = (await r.CallAsync("scripts.export", new JsonObject { ["script"] = "poller", ["version"] = 1 }))!.AsObject();
        Assert.Equal("poller.json", export["fileName"]!.GetValue<string>());
        var imported = (await r.CallAsync("scripts.import", new JsonObject { ["json"] = export["json"]!.GetValue<string>() }))!.AsObject();
        Assert.Equal(3, imported["version"]!.GetValue<int>());

        var e = await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("scripts.get", new JsonObject { ["script"] = "nope" }));
        Assert.Equal("not_found", e.Code);
        Assert.Equal("bad_script", (await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("scripts.save", new JsonObject { ["document"] = "nope" }))).Code);
    }

    [Fact]
    public async Task TheSchemaMethods_AndABreakingSaveIsRefused()
    {
        await using var r = await RigAsync();
        var schema = JsonNode.Parse("""{"type":"object","properties":{"label":{"type":"string"}}}""")!.AsObject();
        Assert.Equal(1, (await r.CallAsync("schemas.save", new JsonObject { ["name"] = "Thing", ["description"] = "a thing", ["schema"] = schema }))!["version"]!.GetValue<int>());
        var doc = Doc();
        doc["_refs"] = new JsonArray(new JsonObject { ["schema"] = "Thing", ["fieldPath"] = "label", ["stepId"] = "b" });
        await r.CallAsync("scripts.save", new JsonObject { ["document"] = doc });

        var empty = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        var check = (await r.CallAsync("schemas.check", new JsonObject { ["name"] = "Thing", ["schema"] = empty }))!.AsObject();
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Equal("b", check["breaks"]![0]!["stepId"]!.GetValue<string>());
        var e = await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("schemas.save", new JsonObject { ["name"] = "Thing", ["schema"] = empty.DeepClone() }));
        Assert.Equal("schema_breaks", e.Code);
        Assert.Contains("poller v1, step b", e.Message);

        var names = (await r.CallAsync("schemas.list"))!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Thing", names);
        Assert.Contains("EmailMessage", names);
        Assert.Equal("a thing", (await r.CallAsync("schemas.get", new JsonObject { ["name"] = "Thing" }))!["description"]!.GetValue<string>());
        Assert.Single((await r.CallAsync("schemas.versions", new JsonObject { ["name"] = "Thing" }))!.AsArray());
        var export = (await r.CallAsync("schemas.export", new JsonObject { ["name"] = "Thing" }))!.AsObject();
        Assert.Equal(2, (await r.CallAsync("schemas.import", new JsonObject { ["json"] = export["json"]!.GetValue<string>() }))!["version"]!.GetValue<int>());
        Assert.Equal("read_only", (await Assert.ThrowsAsync<RpcException>(() =>
            r.CallAsync("schemas.save", new JsonObject { ["name"] = "TriggerFired", ["schema"] = schema.DeepClone() }))).Code);
    }

    [Fact]
    public async Task ScriptMembers_AddGetUpdateState_ResetAVariable_AndRuns()
    {
        await using var r = await RigAsync();
        await r.CallAsync("scripts.save", new JsonObject { ["document"] = Doc(withMail: true) });

        // Bindings are required for every requires entry; a draft-only script is refused.
        var missing = await Assert.ThrowsAsync<RpcException>(() =>
            r.CallAsync("scripts.memberAdd", new JsonObject { ["teamId"] = r.TeamId, ["name"] = "Poller", ["script"] = "poller" }));
        Assert.Contains("'mail' needs a binding", missing.Message);

        var added = (await r.CallAsync("scripts.memberAdd", new JsonObject
        {
            ["teamId"] = r.TeamId, ["name"] = "Poller", ["script"] = "poller", ["settings"] = new JsonObject { ["folder"] = "Sent" },
            ["bindings"] = new JsonObject { ["mail"] = "Gmail" }, ["timeoutMinutes"] = 5,
        }))!.AsObject();
        var memberId = added["memberId"]!.GetValue<long>();
        Assert.Equal(("poller", 5, 1), (added["script"]!.GetValue<string>(), added["timeoutMinutes"]!.GetValue<int>(), added["runsVersion"]!.GetValue<int>()));
        Assert.Null(added["version"]);
        Assert.Equal("Gmail", added["bindings"]!["mail"]!.GetValue<string>());

        var listed = Assert.Single((await r.CallAsync("scripts.list"))!.AsArray())!["usedBy"]!.AsArray();
        Assert.Equal("Poller", listed.Single()!["member"]!.GetValue<string>());

        var updated = (await r.CallAsync("scripts.memberUpdate", new JsonObject { ["memberId"] = memberId, ["version"] = 1, ["timeoutMinutes"] = 7 }))!.AsObject();
        Assert.Equal((1, 7, "Sent"), (updated["version"]!.GetValue<int>(), updated["timeoutMinutes"]!.GetValue<int>(), updated["settings"]!["folder"]!.GetValue<string>()));
        Assert.Equal(7, (await r.CallAsync("scripts.memberGet", new JsonObject { ["memberId"] = memberId }))!["timeoutMinutes"]!.GetValue<int>());

        // Pinning a draft is refused.
        var draftDoc = Doc(withMail: true);
        draftDoc["requires"]![0]!["actions"] = new JsonArray("teleport");
        await r.CallAsync("scripts.save", new JsonObject { ["document"] = draftDoc });
        Assert.Equal("not_runnable", (await Assert.ThrowsAsync<RpcException>(() =>
            r.CallAsync("scripts.memberUpdate", new JsonObject { ["memberId"] = memberId, ["version"] = 2 }))).Code);

        // A run, then its state, the run list and one run in full.
        await r.Runner.RunMemberAsync(memberId, "tell", null, Ct);
        var state = (await r.CallAsync("scripts.memberState", new JsonObject { ["memberId"] = memberId }))!.AsObject();
        Assert.Equal(1, state["variables"]!["runs"]!.GetValue<int>());
        Assert.True((await r.CallAsync("scripts.memberResetVariable", new JsonObject { ["memberId"] = memberId, ["name"] = "runs" }))!["reset"]!.GetValue<bool>());
        Assert.Empty((await r.CallAsync("scripts.memberState", new JsonObject { ["memberId"] = memberId }))!["variables"]!.AsObject());

        var runs = (await r.CallAsync("scripts.runs", new JsonObject { ["teamId"] = r.TeamId }))!.AsArray();
        var run = Assert.Single(runs)!.AsObject();
        Assert.Equal(("succeeded", "Poller"), (run["status"]!.GetValue<string>(), run["member"]!.GetValue<string>()));
        var full = (await r.CallAsync("scripts.run", new JsonObject { ["id"] = run["id"]!.GetValue<long>() }))!.AsObject();
        Assert.Equal(3, full["steps"]!.AsArray().Count);
        Assert.Equal("did a", full["log"]![0]!["line"]!.GetValue<string>());
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("scripts.memberGet", new JsonObject { ["memberId"] = 9999 }))).Code);
    }

    [Fact]
    public async Task TestRun_ReturnsTheRunIdStepsAndLog_DryRunByDefault()
    {
        await using var r = await RigAsync();
        await r.CallAsync("scripts.save", new JsonObject { ["document"] = Doc() });
        var o = (await r.CallAsync("scripts.testRun", new JsonObject
        {
            ["script"] = "poller", ["input"] = new JsonObject { ["triggerId"] = 1 }, ["settings"] = new JsonObject(), ["teamId"] = r.TeamId,
        }))!.AsObject();
        Assert.Equal("succeeded", o["status"]!.GetValue<string>());
        Assert.Equal(3, o["steps"]!.AsArray().Count);
        Assert.Equal("did c", o["log"]![2]!.GetValue<string>());
        var run = (await r.CallAsync("scripts.run", new JsonObject { ["id"] = o["runId"]!.GetValue<long>() }))!.AsObject();
        Assert.True(run["dryRun"]!.GetValue<bool>());
        Assert.True(run["test"]!.GetValue<bool>());
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() =>
            r.CallAsync("scripts.testRun", new JsonObject { ["script"] = "poller", ["teamId"] = 999 }))).Code);
    }

    [Fact]
    public void AVisitorMayReadButNeverChangeOrRun()
    {
        foreach (var method in ScriptsRpc.Reads)
            Assert.Null(RemoteAccess.Refusal(method, new JsonObject(), allowHostTerminals: false, RemoteAccess.Read));
        foreach (var method in ScriptsRpc.Changes)
        {
            Assert.Equal(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal(method, new JsonObject(), allowHostTerminals: false, RemoteAccess.Read));
            Assert.Null(RemoteAccess.Refusal(method, new JsonObject(), allowHostTerminals: false, RemoteAccess.Full)); // a full viewer may
        }
        Assert.Contains("scripts.", UiRpc.Prefixes);
        Assert.Contains("schemas.", UiRpc.Prefixes);
    }

    [Fact]
    public async Task UiRpcHandsScriptsMethodsToScriptsRpc()
    {
        await using var r = await RigAsync();
        var ui = new UiRpc(r.Bus, new Yawble.Core.Policy.PolicyStore(r.Db.ConnectionString), null!, null!, null!, null!, null!);
        Assert.Equal("unavailable", (await Assert.ThrowsAsync<RpcException>(() => ui.DispatchAsync("scripts.list", null, Ct))).Code);
        ui.Scripts = r.Rpc;
        Assert.Empty((await ui.DispatchAsync("scripts.list", null, Ct))!.AsArray());
        Assert.True(UiRpc.Handles("schemas.list"));
    }
}
