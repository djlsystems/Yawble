using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Link;
using Yawble.Core.Workflows.Library;
using Yawble.Core.Workflows.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;
using Yawble.Core.Ui;

namespace Yawble.Core.Tests.Workflows;

/// <summary>workflows.delete (refused in_use while a member uses it, a change a Visitor may not make) and WorkflowMember.nextTriggerAt.</summary>
public class WorkflowsDeleteAndNextTriggerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public required TempDb Db { get; init; }
        public required BusEngine Bus { get; init; }
        public required WorkflowsRpc Rpc { get; init; }
        public required WorkflowRunner Runner { get; init; }
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
        var library = new WorkflowLibrary(db.ConnectionString, () => catalog, FakeChecker.Check);
        var store = new WorkflowRunStore(db.ConnectionString);
        var runner = new WorkflowRunner(store, library, catalog, new FakeEngine(), Path.Combine(db.Dir, "workflows"));
        bus.WorkflowMembers = runner;
        return new Rig { Db = db, Bus = bus, Rpc = new WorkflowsRpc(bus, library, store, runner), Runner = runner, TeamId = teamId };
    }

    private static JsonObject Doc() => JsonNode.Parse(Docs.Clean())!.AsObject();

    private static async Task<long> AddMemberAsync(Rig r, string name = "Poller") =>
        (await r.CallAsync("workflows.memberAdd", new JsonObject { ["teamId"] = r.TeamId, ["name"] = name, ["workflow"] = "poller" }))!["memberId"]!.GetValue<long>();

    [Fact]
    public async Task Delete_IsRefusedInUse_WhileAMemberUsesTheWorkflow_AndTheMessageNamesTheTeamAndMember()
    {
        await using var r = await RigAsync();
        await r.CallAsync("workflows.save", new JsonObject { ["document"] = Doc() });
        var memberId = await AddMemberAsync(r);

        var e = await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("workflows.delete", new JsonObject { ["workflow"] = "poller" }));
        Assert.Equal("in_use", e.Code);
        Assert.Contains("Alpha/Poller", e.Message);
        Assert.Single((await r.CallAsync("workflows.list"))!.AsArray()); // nothing deleted

        // Once the member is gone the workflow can be deleted.
        await r.Bus.RemoveMemberAsync(memberId, Ct);
        Assert.True((await r.CallAsync("workflows.delete", new JsonObject { ["workflow"] = "poller" }))!["deleted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Delete_OfAnUnusedWorkflow_RemovesEveryVersionAndItsTestRuns()
    {
        await using var r = await RigAsync();
        var saved = (await r.CallAsync("workflows.save", new JsonObject { ["document"] = Doc() }))!.AsObject();
        await r.CallAsync("workflows.save", new JsonObject { ["document"] = Docs.Draft() });
        await r.CallAsync("workflows.save", new JsonObject { ["document"] = Doc() });
        var test = (await r.CallAsync("workflows.testRun", new JsonObject { ["workflow"] = "poller", ["teamId"] = r.TeamId }))!.AsObject();

        var deleted = (await r.CallAsync("workflows.delete", new JsonObject { ["workflow"] = saved["id"]!.GetValue<long>() }))!.AsObject();
        Assert.True(deleted["deleted"]!.GetValue<bool>());

        Assert.Empty((await r.CallAsync("workflows.list"))!.AsArray());
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("workflows.get", new JsonObject { ["workflow"] = "poller", ["version"] = 1 }))).Code);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("workflows.run", new JsonObject { ["id"] = test["runId"]!.GetValue<long>() }))).Code);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("workflows.delete", new JsonObject { ["workflow"] = "poller" }))).Code);

        // The key is free again: a new save starts at version 1.
        Assert.Equal(1, (await r.CallAsync("workflows.save", new JsonObject { ["document"] = Doc() }))!["version"]!.GetValue<int>());
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("workflows.delete", new JsonObject()))).Code);
    }

    [Fact]
    public void Delete_IsAChange_RefusedForAVisitor()
    {
        Assert.Contains("workflows.delete", WorkflowsRpc.Changes);
        Assert.DoesNotContain("workflows.delete", WorkflowsRpc.Reads);
        Assert.Equal(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal("workflows.delete", new JsonObject { ["workflow"] = "poller" }, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Null(RemoteAccess.Refusal("workflows.delete", new JsonObject { ["workflow"] = "poller" }, allowHostTerminals: false, RemoteAccess.Full));
    }

    [Fact]
    public async Task TeamsAgents_SaysWhichMembersAreWorkflowMembers()
    {
        await using var r = await RigAsync();
        await r.CallAsync("workflows.save", new JsonObject { ["document"] = Doc() });
        await AddMemberAsync(r);
        var home = OperatingSystem.IsWindows() ? @"C:\Users\pat" : "/home/pat";
        var terminals = new TerminalHub(new AppEvents());
        var ui = new UiRpc(r.Bus, new Yawble.Core.Policy.PolicyStore(r.Db.ConnectionString), new MemberLayouts(Path.Combine(r.Db.Dir, "data"), r.Db.Dir, home),
            new FakeModelCredentials(), new FakeHostTerminals(terminals, TimeSpan.FromMilliseconds(5)), new FakeInteractiveSessions(terminals, r.Bus), terminals, 16_384) { Workflows = r.Rpc };
        var cards = (await ui.DispatchAsync("teams.agents", new JsonObject { ["teamId"] = r.TeamId }, Ct))!.AsArray();
        var kinds = cards.ToDictionary(c => c!["member"]!["name"]!.GetValue<string>(), c => c!["kind"]!.GetValue<string>());
        Assert.Equal("workflow", kinds["Poller"]);
        Assert.Equal("agent", kinds["Manager"]);
    }

    [Fact]
    public async Task NextTriggerAt_IsNullWithoutAScheduledTrigger_AndTheSoonestEnabledScheduledFireOtherwise()
    {
        await using var r = await RigAsync();
        await r.CallAsync("workflows.save", new JsonObject { ["document"] = Doc() });
        var added = (await r.CallAsync("workflows.memberAdd", new JsonObject { ["teamId"] = r.TeamId, ["name"] = "Poller", ["workflow"] = "poller" }))!.AsObject();
        var memberId = added["memberId"]!.GetValue<long>();
        Assert.True(added.ContainsKey("nextTriggerAt"));
        Assert.Null(added["nextTriggerAt"]);

        // An event trigger is not a schedule; a disabled one does not fire.
        await r.Bus.SaveTriggerAsync(new TriggerDraft(null, memberId, "on mail", new TriggerSchedule(TriggerKinds.Event, null, null, null, null), "go", false, true,
            EventName: "mail.new"), Ct);
        var soon = DateTimeOffset.UtcNow.AddHours(1);
        await r.Bus.SaveTriggerAsync(new TriggerDraft(null, memberId, "off", new TriggerSchedule(TriggerKinds.Once, null, null, null, soon), "go", false, Enabled: false), Ct);
        Assert.Null((await r.CallAsync("workflows.memberGet", new JsonObject { ["memberId"] = memberId }))!["nextTriggerAt"]);

        var later = DateTimeOffset.UtcNow.AddDays(2);
        var sooner = DateTimeOffset.UtcNow.AddHours(3);
        await r.Bus.SaveTriggerAsync(new TriggerDraft(null, memberId, "later", new TriggerSchedule(TriggerKinds.Once, null, null, null, later), "go", false, true), Ct);
        await r.Bus.SaveTriggerAsync(new TriggerDraft(null, memberId, "sooner", new TriggerSchedule(TriggerKinds.Once, null, null, null, sooner), "go", false, true), Ct);

        var got = (await r.CallAsync("workflows.memberGet", new JsonObject { ["memberId"] = memberId }))!["nextTriggerAt"]!.GetValue<string>();
        var at = DateTimeOffset.Parse(got, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(sooner.ToUnixTimeMilliseconds(), at.ToUnixTimeMilliseconds());
        Assert.EndsWith("Z", got);

        // memberUpdate returns it too; another member's triggers are not this one's.
        var updated = (await r.CallAsync("workflows.memberUpdate", new JsonObject { ["memberId"] = memberId, ["timeoutMinutes"] = 3 }))!.AsObject();
        Assert.Equal(got, updated["nextTriggerAt"]!.GetValue<string>());
        var other = await AddMemberAsync(r, "Other");
        Assert.Null((await r.CallAsync("workflows.memberGet", new JsonObject { ["memberId"] = other }))!["nextTriggerAt"]);
    }
}
