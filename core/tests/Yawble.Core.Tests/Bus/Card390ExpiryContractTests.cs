using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// Card 390 (B085 verify), gap tests: the agreed B085 contract as the shell reads it, over the real UiRpc dispatcher (the
/// JSON on the wire, not the C# records) with a fake clock. bus.workflows' item keys (the shell fake-core serves the same
/// keys), bus.workflowExtend's reply and already_ended, the expiry warning on the list item and on bus.decisions,
/// teams.workflowExpiry's edge values and AuditLog's get-or-set. Test-only.
/// </summary>
public class Card390ExpiryContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The keys of a bus.workflows item: the shell's WorkflowSummary plus expiryWarning (the contract).</summary>
    public static readonly string[] WorkflowItemKeys =
    [
        "id", "title", "owner", "state", "undeclared", "busy", "ending", "startedAt", "lastAt", "messages",
        "endedAt", "expiredAt", "expiresAt", "expiryWarning", "question",
    ];

    private sealed class Rig : IAsyncDisposable
    {
        public TempDb Db { get; } = new();
        public WorkflowExpiryTests.ManualClock Clock { get; } = new(Start);
        public BusEngine Engine { get; private set; } = null!;
        public RpcDispatcher Rpc { get; private set; } = null!;
        public long TeamId { get; private set; }

        public static async Task<Rig> StartAsync(ScriptedRunner runner)
        {
            var rig = new Rig();
            rig.Engine = new BusEngine(new BusStore(rig.Db.ConnectionString, rig.Clock), runner,
                new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            await rig.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await rig.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await rig.Engine.StartAsync(Ct);
            var terminals = new TerminalHub(new AppEvents());
            var policies = new PolicyStore(rig.Db.ConnectionString);
            var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
            var dataDir = OperatingSystem.IsWindows() ? @"C:\Users\pat\AppData\Local\Yawble\data" : "/home/pat/.local/share/yawble/data";
            var install = OperatingSystem.IsWindows() ? @"C:\Program Files\Yawble" : "/opt/yawble";
            var ui = new UiRpc(rig.Engine, policies, new MemberLayouts(dataDir, install, home), new FakeModelCredentials(),
                new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, rig.Engine), terminals, 16_384);
            rig.Rpc = new RpcDispatcher(new Yawble.Core.Sandboxing.SandboxPlatformInfo(), () => [], ui: ui);
            rig.TeamId = (await rig.Engine.TeamsAsync(Ct)).Single().Id;
            return rig;
        }

        public async Task<JsonNode?> Call(string method, object? args = null) =>
            await Rpc.DispatchAsync(method, args is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(args)!.AsObject(), Ct);

        public async Task<RpcException> Refused(string method, object? args = null) =>
            await Assert.ThrowsAsync<RpcException>(() => Call(method, args));

        public async Task<JsonObject> Item(long workflow) =>
            (await Call("bus.workflows", new { teamId = TeamId }))!.AsArray().Single(w => w!["id"]!.GetValue<long>() == workflow)!.AsObject();

        public DateTimeOffset LastActivity(long id)
        {
            using var s = Db.OpenStore();
            return s.Workflow(id).Where(m => m.Type is not (MessageTypes.WorkflowExpired or BusEngine.WorkflowExpiring)).Max(m => m.OccurredAt);
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    private static DateTimeOffset? At(JsonNode? n) => n is null ? null : DateTimeOffset.Parse(n.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);

    private static ScriptedRunner Asking() => new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
    {
        if (run.Items.All(i => i.CausationSeq is null)) await run.NeedsDecisionAsync("MIT or Apache?", ct);
        return RunResult.Ok;
    });

    [Fact]
    public async Task BusWorkflowsItems_CarryEveryContractKey_OpenAndExpired_OverTheWire()
    {
        await using var rig = await Rig.StartAsync(new ScriptedRunner());
        var root = await rig.Engine.PersonTellAsync("Alpha", "Ada", "look into the flaky test", ct: Ct);
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "quiet");
        var last = rig.LastActivity(root.Seq);

        var open = await rig.Item(root.Seq);
        Assert.Equal(WorkflowItemKeys.Order(), open.Select(kv => kv.Key).Order());
        Assert.Equal("open", open["state"]!.GetValue<string>());
        Assert.Equal(last.AddDays(3), At(open["expiresAt"]));
        Assert.Null(open["endedAt"]);
        Assert.Null(open["expiredAt"]);
        Assert.Null(open["expiryWarning"]);
        Assert.Null(open["question"]);

        rig.Clock.Now = last.AddDays(3).AddMinutes(1);
        Assert.Equal(1, await rig.Engine.SweepExpiryAsync(rig.Clock.Now, Ct));
        var expired = await rig.Item(root.Seq);
        Assert.Equal(WorkflowItemKeys.Order(), expired.Select(kv => kv.Key).Order());
        Assert.Equal("expired", expired["state"]!.GetValue<string>());
        Assert.Equal(last, At(expired["endedAt"]));
        Assert.Equal(rig.Clock.Now, At(expired["expiredAt"]));
        Assert.Null(expired["expiresAt"]);
        Assert.Equal("no activity for 3 days", expired["ending"]!.GetValue<string>());
    }

    [Fact]
    public async Task WorkflowExtend_OverUiRpc_ReturnsTheUpdatedListItem_AndAnEndedOneIsAlreadyEnded()
    {
        await using var rig = await Rig.StartAsync(new ScriptedRunner());
        var root = await rig.Engine.PersonTellAsync("Alpha", "Ada", "an idle one", ct: Ct);
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "quiet");
        var last = rig.LastActivity(root.Seq);

        rig.Clock.Now = last.AddDays(2);
        var extended = (await rig.Call("bus.workflowExtend", new { teamId = rig.TeamId, workflow = root.Seq }))!.AsObject();
        Assert.Equal(WorkflowItemKeys.Order(), extended.Select(kv => kv.Key).Order()); // the list item, not {}
        Assert.Equal(root.Seq, extended["id"]!.GetValue<long>());
        Assert.Equal("open", extended["state"]!.GetValue<string>());
        Assert.Equal(rig.Clock.Now.AddDays(3), At(extended["expiresAt"])); // the clock reset to now
        Assert.Equal(extended.ToJsonString(), (await rig.Item(root.Seq)).ToJsonString()); // same as the list reads next

        Assert.Equal(0, await rig.Engine.SweepExpiryAsync(last.AddDays(3).AddMinutes(1), Ct)); // the old deadline passed: still open
        Assert.Equal(1, await rig.Engine.SweepExpiryAsync(rig.Clock.Now.AddDays(3).AddMinutes(1), Ct));
        Assert.Equal("expired", (await rig.Item(root.Seq))["state"]!.GetValue<string>());

        var refused = await rig.Refused("bus.workflowExtend", new { teamId = rig.TeamId, workflow = root.Seq });
        Assert.Equal("already_ended", refused.Code);

        var closed = await rig.Engine.PersonTellAsync("Alpha", "Ada", "one the person closes", ct: Ct);
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "quiet again");
        await rig.Call("bus.closeWorkflow", new { teamId = rig.TeamId, workflow = closed.Seq });
        Assert.Equal("already_ended", (await rig.Refused("bus.workflowExtend", new { teamId = rig.TeamId, workflow = closed.Seq })).Code);
    }

    [Fact]
    public async Task TheWarning_ShowsOnTheListItemAndOnBusDecisions_AndExtendClearsIt()
    {
        await using var rig = await Rig.StartAsync(Asking());
        var root = await rig.Engine.PersonTellAsync("Alpha", "Ada", "add a licence", ct: Ct);
        await Wait.UntilAsync(() => rig.Engine.IsQuiescentAsync(Ct), "quiet");
        var question = Assert.Single(await rig.Engine.OpenDecisionsAsync("Alpha", Ct));
        var last = rig.LastActivity(root.Seq);
        Assert.Equal(question.Seq, (await rig.Item(root.Seq))["question"]!.GetValue<long>()); // number, not null

        rig.Clock.Now = last.AddDays(2).AddMinutes(1);
        await rig.Engine.SweepExpiryAsync(rig.Clock.Now, Ct);
        var text = $"#{root.Seq} will expire tomorrow; it waits on your answer to question {question.Seq}";
        var item = await rig.Item(root.Seq);
        Assert.Equal(text, item["expiryWarning"]!.GetValue<string>());
        var decision = Assert.Single((await rig.Call("bus.decisions", new { teamId = rig.TeamId }))!.AsArray())!;
        Assert.Equal(text, decision["expiryWarning"]!.GetValue<string>());
        Assert.Equal(At(item["expiresAt"]), At(decision["expiresAt"]));

        await rig.Call("bus.workflowExtend", new { teamId = rig.TeamId, workflow = root.Seq });
        Assert.Null((await rig.Item(root.Seq))["expiryWarning"]);
        Assert.Null(Assert.Single((await rig.Call("bus.decisions", new { teamId = rig.TeamId }))!.AsArray())!["expiryWarning"]);
    }

    [Fact]
    public async Task TeamsWorkflowExpiry_EdgeValues_AndTeamsListCarriesIt()
    {
        await using var rig = await Rig.StartAsync(new ScriptedRunner());
        var teamId = rig.TeamId;
        Assert.Equal(3, (await rig.Call("teams.workflowExpiry", new { teamId }))!["days"]!.GetValue<int>());
        Assert.Equal(3, (await rig.Call("teams.list"))!.AsArray().Single()!["workflowExpiryDays"]!.GetValue<int>());
        Assert.Equal(1, (await rig.Call("teams.workflowExpiry", new { teamId, days = 1 }))!["days"]!.GetValue<int>());
        Assert.Equal(1, (await rig.Call("teams.list"))!.AsArray().Single()!["workflowExpiryDays"]!.GetValue<int>());
        Assert.Equal(12, (await rig.Call("teams.workflowExpiry", new { teamId, days = 12.0 }))!["days"]!.GetValue<int>()); // a whole number
        foreach (var bad in new object[] { true, new[] { 3 }, new { d = 3 }, 1e12, 0.5 })
            Assert.Equal("bad_request", (await rig.Refused("teams.workflowExpiry", new { teamId, days = bad })).Code);
        Assert.Equal(12, (await rig.Call("teams.workflowExpiry", new { teamId }))!["days"]!.GetValue<int>()); // nothing refused changed it
        Assert.Equal("unknown_method", (await rig.Refused("teams.setWorkflowExpiry", new { teamId, days = 5 })).Code);
        Assert.Equal("unknown_method", (await rig.Refused("teams.setWorkflowExpiry", new { teamId, workflowExpiryDays = 5 })).Code);
    }

    [Fact]
    public void AuditLog_AuditsOnlyTheSet_AndExtendIsAudited()
    {
        Assert.True(AuditLog.IsReadOnly("teams.workflowExpiry", new JsonObject { ["teamId"] = 1 }));
        Assert.True(AuditLog.IsReadOnly("teams.workflowExpiry", new JsonObject { ["teamId"] = 1, ["on"] = true })); // "on" is not its key
        Assert.False(AuditLog.IsReadOnly("teams.workflowExpiry", new JsonObject { ["teamId"] = 1, ["days"] = 5 }));
        Assert.False(AuditLog.IsReadOnly("bus.workflowExtend", new JsonObject { ["teamId"] = 1, ["workflow"] = 4 }));
        Assert.True(AuditLog.IsReadOnly("teams.push", new JsonObject { ["teamId"] = 1 })); // the others unchanged
        Assert.False(AuditLog.IsReadOnly("teams.push", new JsonObject { ["teamId"] = 1, ["on"] = true }));
    }
}
