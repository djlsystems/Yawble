using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;
using Yawble.Core.Ui;

namespace Yawble.Core.Tests.Scripts;

public class ScriptRealEngineVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Actions : IScriptActionsProvider, IScriptActions
    {
        public IScriptActionCatalog Catalog { get; } = FakeCatalog.MailAndSms();
        public List<ScriptActionCall> Calls { get; } = [];
        public TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Held;
        public bool Hold;
        public string HoldUse = "mail";
        public bool Fail;
        public IScriptActions For(ScriptCaller caller) => this;
        public async Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct)
        {
            lock (Calls) Calls.Add(call);
            if (call.Use == HoldUse && Hold) { Interlocked.Increment(ref Held); try { await Gate.Task.WaitAsync(ct); } finally { Interlocked.Decrement(ref Held); } }
            if (Fail) throw new InvalidOperationException("integration failed");
            return call.Use == "mail"
                ? new JsonArray(ScriptSamples.Email("m1", "Order", "shop@example.com", "2026-11-23T10:00:00Z"))
                : new JsonObject { ["status"] = 201 };
        }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db = new();
        public BusEngine Bus = null!;
        public ScriptRunner Runner = null!;
        public ScriptRunStore Store = null!;
        public ScriptLibrary Library = null!;
        public ScriptedRunner Agents = new();
        public UiRpc Ui = null!;
        public Actions Actions = new();
        public long Team;
        public async Task Init()
        {
            ScriptRunner? runner = null;
            Bus = new BusEngine(Db.OpenStore(), new ScriptDispatchRunner(Agents, () => runner), new BusOptions { IdleOffer = false });
            Team = await Bus.CreateTeamAsync("Alpha", ct: Ct);
            var library = Library = new ScriptLibrary(Db.ConnectionString, () => Actions.Catalog);
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
        Assert.Equal(trigger.Id.ToString(CultureInfo.InvariantCulture), run.Input!["triggerId"]!.GetValue<string>());
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
    private static async Task<long> Add(Rig r, long team, string name, string folder, string binding)
    {
        var settings = Settings(); settings["folder"] = folder;
        var bindings = Bindings(); bindings["mail"] = binding;
        var member = await r.Call("scripts.memberAdd", new JsonObject { ["teamId"] = team, ["name"] = name, ["script"] = "inbox-sms-alert", ["settings"] = settings, ["bindings"] = bindings });
        return member!["memberId"]!.GetValue<long>();
    }

    [Fact]
    public async Task ConcurrentMembers_PinStartingVersion_AndSkipOnlySameMember()
    {
        await using var r = new Rig(); await r.Init();
        await r.Call("scripts.save", new JsonObject { ["document"] = JsonNode.Parse(ScriptSamples.InboxSmsAlert) });
        var beta = await r.Bus.CreateTeamAsync("Beta", ct: Ct);
        var ids = new[] { await Add(r, r.Team, "One", "A", "mail-a"), await Add(r, r.Team, "Two", "B", "mail-b"), await Add(r, beta, "One", "C", "mail-c") };
        var triggers = new List<TriggerRow>();
        foreach (var id in ids)
            triggers.Add(await r.Bus.SaveTriggerAsync(new TriggerDraft(null, id, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Poll", true, true), Ct));
        r.Actions.Hold = true;
        try
        {
            foreach (var trigger in triggers) await r.Bus.RunTriggerNowAsync(trigger.Id, Ct);
            await Wait.UntilAsync(() => Volatile.Read(ref r.Actions.Held) == 3, "three real engine runs held in mail");
            var changed = JsonNode.Parse(ScriptSamples.InboxSmsAlert)!.AsObject(); changed["description"] = "version two";
            await r.Call("scripts.save", new JsonObject { ["document"] = changed });
            Assert.False(r.Runner.Start(ids[0], "tell", null).Started);
            r.Actions.Gate.TrySetResult(); await r.Runner.WhenIdleAsync();
            var rows = ids.Select(id => Assert.Single(r.Store.RecentRuns(null, id, 10), row => row.Trigger.StartsWith("trigger:"))).ToArray();
            foreach (var (id, row) in ids.Zip(rows))
            {
                Assert.Equal((Yawble.Core.Scripts.Running.ScriptRunStatus.Succeeded, 1), (row.Status, row.Version));
                Assert.Equal("2026-11-23T10:00:00Z", r.Store.State(id).Variables["lastSeen"]!.GetValue<string>());
                Assert.True(File.Exists(Path.Combine(r.Runner.MemberFolder(id), "run.log")));
            }
            Assert.Equal(3, ids.Select(r.Runner.MemberFolder).Distinct().Count());
            Assert.Equal(new[] { "A", "B", "C" }, rows.Select(x => x.Settings!["folder"]!.GetValue<string>()));
            Assert.Equal(new[] { "mail-a", "mail-b", "mail-c" }, rows.Select(x => x.Bindings!["mail"]!.GetValue<string>()));
        }
        finally { r.Actions.Gate.TrySetResult(); }
    }

    [Fact]
    public async Task RealEngineFailurePreservesStateAndRaisesOneDecision()
    {
        await using var r = new Rig(); await r.Init();
        await r.Call("scripts.save", new JsonObject { ["document"] = JsonNode.Parse(ScriptSamples.InboxSmsAlert) });
        var id = await Add(r, r.Team, "Poller", "INBOX", "mail");
        r.Runner.Decisions = new BusScriptDecisions(r.Bus);
        Assert.True((await r.Runner.RunMemberAsync(id, "tell", null, Ct)).Succeeded);
        r.Actions.Fail = true;
        for (var i = 0; i < 4; i++) Assert.False((await r.Runner.RunMemberAsync(id, "tell", null, Ct)).Succeeded);
        Assert.Equal("2026-11-23T10:00:00Z", r.Store.State(id).Variables["lastSeen"]!.GetValue<string>());
        var decision = Assert.Single(await r.Bus.OpenDecisionsAsync("Alpha", Ct));
        Assert.Equal("Alpha/Poller", decision.Source);
    }

    [Fact]
    public async Task DryRunFileWriteUsesDryRunFolder()
    {
        await using var r = new Rig(); await r.Init();
        var doc = JsonNode.Parse(Docs.Clean())!.AsObject();
        doc["steps"] = JsonNode.Parse("""[{"id":"a","type":"file.writeAllText","path":"out.txt","text":"hello","next":null}]""");
        var save = await r.Call("scripts.save", new JsonObject { ["document"] = doc });
        Assert.True(save!["ok"]!.GetValue<bool>(), save.ToJsonString());
        var run = await r.Call("scripts.testRun", new JsonObject { ["script"] = "poller", ["teamId"] = r.Team, ["dryRun"] = true });
        Assert.Equal("succeeded", run!["status"]!.GetValue<string>());
        var path = Path.Combine(r.Db.Dir, "scripts", "dry-run", run["runId"]!.GetValue<long>().ToString(), "out.txt");
        Assert.Equal("hello", await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task VisitorReadsAndAllChangesAreRefused()
    {
        await using var r = new Rig(); await r.Init();
        Assert.Empty((await r.Call("scripts.list"))!.AsArray());
        foreach (var method in ScriptsRpc.Reads) Assert.Null(RemoteAccess.Refusal(method, new JsonObject(), false, RemoteAccess.Read));
        foreach (var method in ScriptsRpc.Changes) Assert.Equal(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal(method, new JsonObject(), false, RemoteAccess.Read));
    }

    [Fact]
    public async Task AgentTriggerStillRunsAgent()
    {
        await using var r = new Rig(); await r.Init();
        await r.Bus.AddMemberAsync("Alpha", "Ada", ct: Ct);
        using var store = r.Db.OpenStore();
        var ada = store.Member("Alpha", "Ada")!;
        var t = await r.Bus.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Poll", true, true), Ct);
        await r.Bus.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);
        await Wait.UntilAsync(() => r.Agents.Admitted.Count == 1, "agent admitted");
        Assert.Empty(r.Store.RecentRuns(null, ada.Id, 10));
    }

    [Fact]
    public async Task InterruptedRealEngineRunResumesFromCheckpoint()
    {
        await using var r = new Rig(); await r.Init();
        await r.Call("scripts.save", new JsonObject { ["document"] = JsonNode.Parse(ScriptSamples.InboxSmsAlert) });
        var id = await Add(r, r.Team, "Poller", "INBOX", "mail");
        r.Actions.HoldUse = "sms"; r.Actions.Hold = true;
        var started = r.Runner.Start(id, "tell", null);
        await Wait.UntilAsync(() => Volatile.Read(ref r.Actions.Held) == 1, "mail action held");
        await r.Runner.StopAsync();
        var row = r.Store.Run(started.RunId!.Value)!;
        Assert.Equal(Yawble.Core.Scripts.Running.ScriptRunStatus.Running, row.Status);
        Assert.NotNull(row.Checkpoint);
        var completedBefore = r.Store.Steps(row.Id).Where(s => s.Status == "succeeded").Select(s => s.StepId).ToArray();
        Assert.Contains("list", completedBefore);
        r.Actions.Hold = false;
        r.Runner = new ScriptRunner(r.Store, r.Library, r.Actions, SeamScriptEngine.Instance, Path.Combine(r.Db.Dir, "scripts"));
        Assert.Contains(row.Id, r.Runner.ResumeInterrupted());
        await r.Runner.WhenIdleAsync();
        Assert.Equal(Yawble.Core.Scripts.Running.ScriptRunStatus.Succeeded, r.Store.Run(row.Id)!.Status);
        Assert.Equal("2026-11-23T10:00:00Z", r.Store.State(id).Variables["lastSeen"]!.GetValue<string>());
        Assert.Equal(1, r.Actions.Calls.Count(c => c.Use == "mail"));
    }

    [Fact]
    public async Task BreakingSchemaSaveIsRefusedForRealCheckedScript()
    {
        await using var r = new Rig(); await r.Init();
        var schema = JsonNode.Parse("""{"type":"object","properties":{"label":{"type":"string"}}}""")!;
        await r.Call("schemas.save", new JsonObject { ["name"] = "Thing", ["schema"] = schema });
        var doc = JsonNode.Parse(Docs.Clean())!.AsObject();
        doc["variables"] = JsonNode.Parse("""[{"name":"thing","type":"Thing","scope":"run"}]""");
        doc["steps"] = JsonNode.Parse("""[{"id":"a","type":"log","text":"{vars.thing.label}","next":null}]""");
        var saved = await r.Call("scripts.save", new JsonObject { ["document"] = doc });
        Assert.True(saved!["ok"]!.GetValue<bool>(), saved.ToJsonString());
        var empty = JsonNode.Parse("""{"type":"object","properties":{}}""");
        var e = await Assert.ThrowsAsync<RpcException>(() => r.Call("schemas.save", new JsonObject { ["name"] = "Thing", ["schema"] = empty }));
        Assert.Equal("schema_breaks", e.Code);
        Assert.Contains("poller v1, step a", e.Message);
    }

    [Fact]
    public async Task TriggerDuringRealEngineRunIsSkippedAndNoted()
    {
        await using var r = new Rig(); await r.Init();
        await r.Call("scripts.save", new JsonObject { ["document"] = JsonNode.Parse(ScriptSamples.InboxSmsAlert) });
        var id = await Add(r, r.Team, "Poller", "INBOX", "mail");
        var t = await r.Bus.SaveTriggerAsync(new TriggerDraft(null, id, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Poll", true, true), Ct);
        r.Actions.Hold = true;
        try
        {
            Assert.True(r.Runner.Start(id, "tell", null).Started);
            await Wait.UntilAsync(() => Volatile.Read(ref r.Actions.Held) == 1, "real engine run held");
            await r.Bus.RunTriggerNowAsync(t.Id, Ct);
            using var store = r.Db.OpenStore();
            Assert.Equal("skipped", store.Trigger(t.Id)!.LastOutcome);
            Assert.Contains("one run at a time", store.Trigger(t.Id)!.LastReason);
            Assert.Contains(r.Store.RecentRuns(r.Team, id, 10), x => x.Status == Yawble.Core.Scripts.Running.ScriptRunStatus.Skipped);
        }
        finally { r.Actions.Gate.TrySetResult(); await r.Runner.WhenIdleAsync(); }
    }

}



