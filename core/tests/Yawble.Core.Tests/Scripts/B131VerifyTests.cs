using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;
using Yawble.Core.Ui;

namespace Yawble.Core.Tests.Scripts;

public class B131VerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Actions : IScriptActionsProvider, IScriptActions
    {
        public IScriptActionCatalog Catalog { get; } = FakeCatalog.MailAndSms();
        public List<ScriptActionCall> Calls { get; } = [];
        public IScriptActions For(ScriptCaller caller) => this;
        public Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct)
        {
            lock (Calls) Calls.Add(call);
            return Task.FromResult<JsonNode?>(call.Use == "mail"
                ? new JsonArray(ScriptSamples.Email("m1", "Order", "shop@example.com", "2026-11-23T10:00:00Z"))
                : new JsonObject { ["status"] = 201 });
        }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db = new();
        public BusEngine Bus = null!;
        public ScriptRunner Runner = null!;
        public ScriptRunStore Store = null!;
        public UiRpc Ui = null!;
        public Actions Actions = new();
        public long Team;
        public async Task Init()
        {
            ScriptRunner? runner = null;
            Bus = new BusEngine(Db.OpenStore(), new ScriptDispatchRunner(new ScriptedRunner(), () => runner), new BusOptions { IdleOffer = false });
            Team = await Bus.CreateTeamAsync("Alpha", ct: Ct);
            var library = new ScriptLibrary(Db.ConnectionString, () => Actions.Catalog);
            Store = new ScriptRunStore(Db.ConnectionString);
            Runner = runner = new ScriptRunner(Store, library, Actions, SeamScriptEngine.Instance, Path.Combine(Db.Dir, "scripts"));
            Bus.ScriptMembers = runner;
            Ui = new UiRpc(Bus, new PolicyStore(Db.ConnectionString), null!, null!, null!, null!, null!) { Scripts = new ScriptsRpc(Bus, library, Store, runner) };
            await Bus.StartAsync(Ct);
        }
        public Task<JsonNode?> Call(string method, JsonObject? args = null) => Ui.DispatchAsync(method, args, Ct);
        public async ValueTask DisposeAsync() { await Runner.StopAsync(); await Bus.DisposeAsync(); Db.Dispose(); }
    }
    private static JsonObject Settings() => new() { ["recipients"] = new JsonArray("+15551230001", "+15551230002"), ["fromNumber"] = "+15559990000" };
    private static JsonObject Bindings() => new() { ["mail"] = "mailbox", ["sms"] = "twilio" };

    [Fact]
    public async Task ExampleWithoutCatalogIsSavedAsDraft()
    {
        await using var r = new Rig(); await r.Init();
        var library = new ScriptLibrary(r.Db.ConnectionString, () => NoScriptActions.Instance);
        var rpc = new ScriptsRpc(r.Bus, library, r.Store, r.Runner);
        var result = await rpc.DispatchAsync("scripts.save", new JsonObject { ["document"] = JsonNode.Parse(ScriptSamples.InboxSmsAlert) }, Ct);
        Assert.False(result!["ok"]!.GetValue<bool>());
        Assert.True(result["draft"]!.GetValue<bool>());
    }
    [Fact]
    public async Task ExampleThroughUiRpcAndTrigger_RunsRealEngineAndKeepsLayout()
    {
        await using var r = new Rig(); await r.Init();
        var doc = JsonNode.Parse(ScriptSamples.WithLayout())!.AsObject();
        var save = await r.Call("scripts.save", new JsonObject { ["document"] = doc.DeepClone() });
        Assert.True(save!["ok"]!.GetValue<bool>(), save.ToJsonString());
        var get = await r.Call("scripts.get", new JsonObject { ["script"] = "inbox-sms-alert" });
        Assert.True(JsonNode.DeepEquals(doc["layout"], get!["document"]!["layout"]));
        var member = await r.Call("scripts.memberAdd", new JsonObject { ["teamId"] = r.Team, ["name"] = "Poller", ["script"] = "inbox-sms-alert", ["settings"] = Settings(), ["bindings"] = Bindings() });
        var id = member!["memberId"]!.GetValue<long>();
        var trigger = await r.Bus.SaveTriggerAsync(new TriggerDraft(null, id, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Poll", true, true), Ct);
        using var beforeStore = r.Db.OpenStore();
        var before = beforeStore.AllMessages().Count;
        await r.Bus.SweepTriggersAsync(trigger.NextDueAt!.Value.AddSeconds(1), Ct);
        await r.Runner.WhenIdleAsync();
        var run = Assert.Single(r.Store.RecentRuns(r.Team, id, 10));
        Assert.True(run.Status == Yawble.Core.Scripts.Running.ScriptRunStatus.Succeeded, $"{run.Status}: {run.FailedStepId}: {run.Reason}");
        Assert.Equal(trigger.Id, run.Input!["triggerId"]!.GetValue<long>());
        Assert.Equal("every", run.Input["kind"]!.GetValue<string>());
        Assert.NotNull(run.Input["firedAt"]);
        Assert.Equal(2, r.Actions.Calls.Count(c => c.Use == "sms"));
        Assert.Equal("2026-11-23T10:00:00Z", r.Store.State(id).Variables!["lastSeen"]!.GetValue<string>());
        Assert.Contains("1 new, 2 texts", await File.ReadAllTextAsync(Path.Combine(r.Runner.MemberFolder(id), "run.log"), Ct));
        using var afterStore = r.Db.OpenStore();
        Assert.Equal(before, afterStore.AllMessages().Count);
    }

    [Fact]
    public async Task ExampleDryRunThroughUiRpc_ReadsButDoesNotSend()
    {
        await using var r = new Rig(); await r.Init();
        await r.Call("scripts.save", new JsonObject { ["document"] = JsonNode.Parse(ScriptSamples.InboxSmsAlert) });
        var result = await r.Call("scripts.testRun", new JsonObject { ["script"] = "inbox-sms-alert", ["teamId"] = r.Team, ["settings"] = Settings(), ["bindings"] = Bindings(), ["dryRun"] = true });
        Assert.Equal("succeeded", result!["status"]!.GetValue<string>());
        Assert.DoesNotContain(r.Actions.Calls, c => c.Use == "sms");
        Assert.Contains(result["log"]!.AsArray(), l => l!.GetValue<string>().StartsWith("would POST"));
    }
}



