using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Link;
using Yawble.Core.Policy;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;
using Yawble.Core.Workflows.Library;
using Yawble.Core.Workflows.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;
using Yawble.Core.Ui;
using RunStatus = Yawble.Core.Workflows.Running.WorkflowRunStatus;

namespace Yawble.Core.Tests.Workflows;

/// <summary>
/// The runner through the real workflow engine (SeamWorkflowEngine), the real checker and the example workflow, called
/// directly and through the window's RPC. The real TriggerFired schema refuses a triggerId that is a number, and a dry
/// run needs the catalog to tell a read from a send: the fake engine in WorkflowRunnerTests checks neither.
/// </summary>
public class WorkflowRunnerRealEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Mail and SMS actions that record their calls and can hold one kind of call open, or fail every call.</summary>
    private sealed class Actions : IWorkflowActionsProvider, IWorkflowActions
    {
        public IWorkflowActionCatalog Catalog { get; } = FakeCatalog.MailAndSms();
        public List<WorkflowActionCall> Calls { get; } = [];
        public TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Held;
        public bool Hold;
        public string HoldUse = "mail";
        public bool Fail;
        public IWorkflowActions For(WorkflowCaller caller) => this;

        public async Task<JsonNode?> InvokeAsync(WorkflowActionCall call, CancellationToken ct)
        {
            lock (Calls) Calls.Add(call);
            if (call.Use == HoldUse && Hold)
            {
                Interlocked.Increment(ref Held);
                try { await Gate.Task.WaitAsync(ct); } finally { Interlocked.Decrement(ref Held); }
            }
            if (Fail) throw new InvalidOperationException("integration failed");
            return call.Use == "mail"
                ? new JsonArray(WorkflowSamples.Email("m1", "Order", "shop@example.com", "2026-11-23T10:00:00Z"))
                : new JsonObject { ["status"] = 201 };
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public readonly Actions Actions = new();
        public readonly ScriptedRunner Agents = new();
        public BusEngine Bus = null!;
        public WorkflowLibrary Library = null!;
        public WorkflowRunStore Store = null!;
        public WorkflowRunner Runner = null!;
        public UiRpc Ui = null!;
        public long TeamId;
        public long WorkflowId;

        /// <summary>Starts the engine on team Alpha; with <paramref name="saveExample"/> the library already holds the example workflow.</summary>
        public async Task<Rig> InitAsync(bool saveExample = false)
        {
            WorkflowRunner? runner = null;
            Bus = new BusEngine(Db.OpenStore(), new WorkflowDispatchRunner(Agents, () => runner), new BusOptions { IdleOffer = false });
            TeamId = await Bus.CreateTeamAsync("Alpha", ct: Ct);
            Library = new WorkflowLibrary(Db.ConnectionString, () => Actions.Catalog); // the real checker
            Store = new WorkflowRunStore(Db.ConnectionString);
            Runner = runner = new WorkflowRunner(Store, Library, Actions, SeamWorkflowEngine.Instance, Path.Combine(Db.Dir, "workflows"));
            Bus.WorkflowMembers = runner;
            Ui = new UiRpc(Bus, new PolicyStore(Db.ConnectionString), null!, null!, null!, null!, null!) { Workflows = new WorkflowsRpc(Bus, Library, Store, runner) };
            if (saveExample)
            {
                var saved = Library.Save(WorkflowSamples.InboxSmsAlert);
                Assert.True(saved.Ok, saved.Check.ToJsonString()); // the example passes the real check with the mail and SMS catalog
                WorkflowId = saved.Id;
            }
            await Bus.StartAsync(Ct);
            return this;
        }

        public async Task<long> MemberAsync()
        {
            var row = await Bus.AddWorkflowMemberAsync(TeamId, "Poller", Ct);
            Store.SetWorkflowMember(row.Id, WorkflowId, null, Settings(), Bindings(), null);
            return row.Id;
        }

        public Task<JsonNode?> Call(string method, JsonObject? args = null) => Ui.DispatchAsync(method, args, Ct);

        public async ValueTask DisposeAsync()
        {
            await Runner.StopAsync();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static JsonObject Settings() => new() { ["recipients"] = new JsonArray("+15551230001", "+15551230002"), ["fromNumber"] = "+15559990000" };
    private static JsonObject Bindings() => new() { ["mail"] = "mailbox", ["sms"] = "twilio" };

    private static string Why(WorkflowRunRow run) => $"{run.Status} at {run.FailedStepId}: {run.Reason}";

    [Fact]
    public async Task ATriggerFire_GivesTheRealEngineATriggerIdString_AndTheRunSucceeds()
    {
        await using var r = await new Rig().InitAsync(saveExample: true);
        var bot = await r.MemberAsync();
        var t = await r.Bus.SaveTriggerAsync(new TriggerDraft(null, bot, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Poll.", true, true), Ct);

        await r.Bus.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);
        await r.Runner.WhenIdleAsync();

        var run = Assert.Single(r.Store.RecentRuns(r.TeamId, bot, 10));
        Assert.True(run.Status == RunStatus.Succeeded, Why(run)); // the TriggerFired schema refuses a number
        Assert.Equal(t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), run.Input!["triggerId"]!.GetValue<string>());
        Assert.Equal(2, r.Actions.Calls.Count(c => c.Use == "sms"));
    }

    [Fact]
    public async Task WorkflowPayloadsTriggerFired_PassesTheBuiltInSchema_InARealRun()
    {
        await using var r = await new Rig().InitAsync(saveExample: true);
        var bot = await r.MemberAsync();
        var input = WorkflowPayloads.TriggerFired(42, "once", DateTimeOffset.Parse("2026-11-23T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("42", input["triggerId"]!.GetValue<string>());

        var outcome = await r.Runner.RunMemberAsync(bot, "run now", input, Ct);

        Assert.True(outcome.Run.Status == RunStatus.Succeeded, Why(outcome.Run));
        Assert.Equal("2026-11-23T10:00:00Z", r.Store.State(bot).Variables!["lastSeen"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADryTestRun_ReadsMail_ButLogsWouldPostInsteadOfSending()
    {
        await using var r = await new Rig().InitAsync(saveExample: true);
        var input = WorkflowPayloads.TriggerFired(1, "once", DateTimeOffset.UtcNow);

        var outcome = await r.Runner.TestRunAsync(new WorkflowTestRun(r.WorkflowId, null, input, Settings(), Bindings(), r.TeamId, DryRun: true), Ct);

        Assert.True(outcome.Run.Status == RunStatus.Succeeded, Why(outcome.Run));
        Assert.Contains(r.Actions.Calls, c => c.Use == "mail"); // a read still runs in a dry run
        Assert.DoesNotContain(r.Actions.Calls, c => c.Use == "sms"); // a send never reaches the provider in a dry run
        Assert.Equal(2, outcome.Log.Count(l => l.Line.StartsWith("would POST", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExampleWithoutCatalogIsSavedAsDraft()
    {
        await using var r = await new Rig().InitAsync();
        var library = new WorkflowLibrary(r.Db.ConnectionString, () => NoWorkflowActions.Instance);
        var rpc = new WorkflowsRpc(r.Bus, library, r.Store, r.Runner);
        var result = await rpc.DispatchAsync("workflows.save", new JsonObject { ["document"] = JsonNode.Parse(WorkflowSamples.InboxSmsAlert) }, Ct);
        Assert.False(result!["ok"]!.GetValue<bool>());
        Assert.True(result["draft"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExampleThroughUiRpcAndTrigger_RunsRealEngineAndKeepsLayout()
    {
        await using var r = await new Rig().InitAsync();
        var doc = JsonNode.Parse(WorkflowSamples.WithLayout())!.AsObject();
        var save = await r.Call("workflows.save", new JsonObject { ["document"] = doc.DeepClone() });
        Assert.True(save!["ok"]!.GetValue<bool>(), save.ToJsonString());
        var get = await r.Call("workflows.get", new JsonObject { ["workflow"] = "inbox-sms-alert" });
        Assert.True(JsonNode.DeepEquals(doc["layout"], get!["document"]!["layout"]));
        var member = await r.Call("workflows.memberAdd", new JsonObject { ["teamId"] = r.TeamId, ["name"] = "Poller", ["workflow"] = "inbox-sms-alert", ["settings"] = Settings(), ["bindings"] = Bindings() });
        var id = member!["memberId"]!.GetValue<long>();
        var trigger = await r.Bus.SaveTriggerAsync(new TriggerDraft(null, id, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Poll", true, true), Ct);
        using var beforeStore = r.Db.OpenStore();
        var before = beforeStore.AllMessages().Count;
        await r.Bus.SweepTriggersAsync(trigger.NextDueAt!.Value.AddSeconds(1), Ct);
        await r.Runner.WhenIdleAsync();
        var run = Assert.Single(r.Store.RecentRuns(r.TeamId, id, 10));
        Assert.True(run.Status == RunStatus.Succeeded, $"{run.Status}: {run.FailedStepId}: {run.Reason}");
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
        await using var r = await new Rig().InitAsync();
        await r.Call("workflows.save", new JsonObject { ["document"] = JsonNode.Parse(WorkflowSamples.InboxSmsAlert) });
        var result = await r.Call("workflows.testRun", new JsonObject { ["workflow"] = "inbox-sms-alert", ["teamId"] = r.TeamId, ["settings"] = Settings(), ["bindings"] = Bindings(), ["dryRun"] = true });
        Assert.Equal("succeeded", result!["status"]!.GetValue<string>());
        Assert.DoesNotContain(r.Actions.Calls, c => c.Use == "sms");
        Assert.Contains(result["log"]!.AsArray(), l => l!.GetValue<string>().StartsWith("would POST"));
    }

    private static async Task<long> Add(Rig r, long team, string name, string folder, string binding)
    {
        var settings = Settings(); settings["folder"] = folder;
        var bindings = Bindings(); bindings["mail"] = binding;
        var member = await r.Call("workflows.memberAdd", new JsonObject { ["teamId"] = team, ["name"] = name, ["workflow"] = "inbox-sms-alert", ["settings"] = settings, ["bindings"] = bindings });
        return member!["memberId"]!.GetValue<long>();
    }

    [Fact]
    public async Task ConcurrentMembers_PinStartingVersion_AndSkipOnlySameMember()
    {
        await using var r = await new Rig().InitAsync();
        await r.Call("workflows.save", new JsonObject { ["document"] = JsonNode.Parse(WorkflowSamples.InboxSmsAlert) });
        var beta = await r.Bus.CreateTeamAsync("Beta", ct: Ct);
        var ids = new[] { await Add(r, r.TeamId, "One", "A", "mail-a"), await Add(r, r.TeamId, "Two", "B", "mail-b"), await Add(r, beta, "One", "C", "mail-c") };
        var triggers = new List<TriggerRow>();
        foreach (var id in ids)
            triggers.Add(await r.Bus.SaveTriggerAsync(new TriggerDraft(null, id, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Poll", true, true), Ct));
        r.Actions.Hold = true;
        try
        {
            foreach (var trigger in triggers) await r.Bus.RunTriggerNowAsync(trigger.Id, Ct);
            await Wait.UntilAsync(() => Volatile.Read(ref r.Actions.Held) == 3, "three real engine runs held in mail");
            var changed = JsonNode.Parse(WorkflowSamples.InboxSmsAlert)!.AsObject(); changed["description"] = "version two";
            await r.Call("workflows.save", new JsonObject { ["document"] = changed });
            Assert.False(r.Runner.Start(ids[0], "tell", null).Started);
            r.Actions.Gate.TrySetResult(); await r.Runner.WhenIdleAsync();
            var rows = ids.Select(id => Assert.Single(r.Store.RecentRuns(null, id, 10), row => row.Trigger.StartsWith("trigger:"))).ToArray();
            foreach (var (id, row) in ids.Zip(rows))
            {
                Assert.Equal((RunStatus.Succeeded, 1), (row.Status, row.Version));
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
        await using var r = await new Rig().InitAsync();
        await r.Call("workflows.save", new JsonObject { ["document"] = JsonNode.Parse(WorkflowSamples.InboxSmsAlert) });
        var id = await Add(r, r.TeamId, "Poller", "INBOX", "mail");
        r.Runner.Decisions = new BusWorkflowDecisions(r.Bus);
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
        await using var r = await new Rig().InitAsync();
        var doc = JsonNode.Parse(Docs.Clean())!.AsObject();
        doc["steps"] = JsonNode.Parse("""[{"id":"a","type":"file.writeAllText","path":"out.txt","text":"hello","next":null}]""");
        var save = await r.Call("workflows.save", new JsonObject { ["document"] = doc });
        Assert.True(save!["ok"]!.GetValue<bool>(), save.ToJsonString());
        var run = await r.Call("workflows.testRun", new JsonObject { ["workflow"] = "poller", ["teamId"] = r.TeamId, ["dryRun"] = true });
        Assert.Equal("succeeded", run!["status"]!.GetValue<string>());
        var path = Path.Combine(r.Db.Dir, "workflows", "dry-run", run["runId"]!.GetValue<long>().ToString(), "out.txt");
        Assert.Equal("hello", await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task VisitorReadsAndAllChangesAreRefused()
    {
        await using var r = await new Rig().InitAsync();
        Assert.Empty((await r.Call("workflows.list"))!.AsArray());
        foreach (var method in WorkflowsRpc.Reads) Assert.Null(RemoteAccess.Refusal(method, new JsonObject(), false, RemoteAccess.Read));
        foreach (var method in WorkflowsRpc.Changes) Assert.Equal(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal(method, new JsonObject(), false, RemoteAccess.Read));
    }

    [Fact]
    public async Task AgentTriggerStillRunsAgent()
    {
        await using var r = await new Rig().InitAsync();
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
        await using var r = await new Rig().InitAsync();
        await r.Call("workflows.save", new JsonObject { ["document"] = JsonNode.Parse(WorkflowSamples.InboxSmsAlert) });
        var id = await Add(r, r.TeamId, "Poller", "INBOX", "mail");
        r.Actions.HoldUse = "sms"; r.Actions.Hold = true;
        var started = r.Runner.Start(id, "tell", null);
        await Wait.UntilAsync(() => Volatile.Read(ref r.Actions.Held) == 1, "mail action held");
        await r.Runner.StopAsync();
        var row = r.Store.Run(started.RunId!.Value)!;
        Assert.Equal(RunStatus.Running, row.Status);
        Assert.NotNull(row.Checkpoint);
        var completedBefore = r.Store.Steps(row.Id).Where(s => s.Status == "succeeded").Select(s => s.StepId).ToArray();
        Assert.Contains("list", completedBefore);
        r.Actions.Hold = false;
        r.Runner = new WorkflowRunner(r.Store, r.Library, r.Actions, SeamWorkflowEngine.Instance, Path.Combine(r.Db.Dir, "workflows"));
        Assert.Contains(row.Id, r.Runner.ResumeInterrupted());
        await r.Runner.WhenIdleAsync();
        Assert.Equal(RunStatus.Succeeded, r.Store.Run(row.Id)!.Status);
        Assert.Equal("2026-11-23T10:00:00Z", r.Store.State(id).Variables["lastSeen"]!.GetValue<string>());
        Assert.Equal(1, r.Actions.Calls.Count(c => c.Use == "mail"));
    }

    [Fact]
    public async Task BreakingSchemaSaveIsRefusedForRealCheckedWorkflow()
    {
        await using var r = await new Rig().InitAsync();
        var schema = JsonNode.Parse("""{"type":"object","properties":{"label":{"type":"string"}}}""")!;
        await r.Call("schemas.save", new JsonObject { ["name"] = "Thing", ["schema"] = schema });
        var doc = JsonNode.Parse(Docs.Clean())!.AsObject();
        doc["variables"] = JsonNode.Parse("""[{"name":"thing","type":"Thing","scope":"run"}]""");
        doc["steps"] = JsonNode.Parse("""[{"id":"a","type":"log","text":"{vars.thing.label}","next":null}]""");
        var saved = await r.Call("workflows.save", new JsonObject { ["document"] = doc });
        Assert.True(saved!["ok"]!.GetValue<bool>(), saved.ToJsonString());
        var empty = JsonNode.Parse("""{"type":"object","properties":{}}""");
        var e = await Assert.ThrowsAsync<RpcException>(() => r.Call("schemas.save", new JsonObject { ["name"] = "Thing", ["schema"] = empty }));
        Assert.Equal("schema_breaks", e.Code);
        Assert.Contains("poller v1, step a", e.Message);
    }

    [Fact]
    public async Task TriggerDuringRealEngineRunIsSkippedAndNoted()
    {
        await using var r = await new Rig().InitAsync();
        await r.Call("workflows.save", new JsonObject { ["document"] = JsonNode.Parse(WorkflowSamples.InboxSmsAlert) });
        var id = await Add(r, r.TeamId, "Poller", "INBOX", "mail");
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
            Assert.Contains(r.Store.RecentRuns(r.TeamId, id, 10), x => x.Status == RunStatus.Skipped);
        }
        finally { r.Actions.Gate.TrySetResult(); await r.Runner.WhenIdleAsync(); }
    }
}
