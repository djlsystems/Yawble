using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;
using Yawble.Core.Ui;

namespace Yawble.Core.Tests.Scripts;

/// <summary>Card 598 (B134): scripts.delete (refused in_use while a member uses it, a change a Visitor may not make) and ScriptMember.nextTriggerAt.</summary>
public class B134ScriptsRpcTests
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

    private static JsonObject Doc() => JsonNode.Parse(Docs.Clean())!.AsObject();

    private static async Task<long> AddMemberAsync(Rig r, string name = "Poller") =>
        (await r.CallAsync("scripts.memberAdd", new JsonObject { ["teamId"] = r.TeamId, ["name"] = name, ["script"] = "poller" }))!["memberId"]!.GetValue<long>();

    [Fact]
    public async Task Delete_IsRefusedInUse_WhileAMemberUsesTheScript_AndTheMessageNamesTheTeamAndMember()
    {
        await using var r = await RigAsync();
        await r.CallAsync("scripts.save", new JsonObject { ["document"] = Doc() });
        var memberId = await AddMemberAsync(r);

        var e = await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("scripts.delete", new JsonObject { ["script"] = "poller" }));
        Assert.Equal("in_use", e.Code);
        Assert.Contains("Alpha/Poller", e.Message);
        Assert.Single((await r.CallAsync("scripts.list"))!.AsArray()); // nothing deleted

        // Once the member is gone the script can be deleted.
        await r.Bus.RemoveMemberAsync(memberId, Ct);
        Assert.True((await r.CallAsync("scripts.delete", new JsonObject { ["script"] = "poller" }))!["deleted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Delete_OfAnUnusedScript_RemovesEveryVersionAndItsTestRuns()
    {
        await using var r = await RigAsync();
        var saved = (await r.CallAsync("scripts.save", new JsonObject { ["document"] = Doc() }))!.AsObject();
        await r.CallAsync("scripts.save", new JsonObject { ["document"] = Docs.Draft() });
        await r.CallAsync("scripts.save", new JsonObject { ["document"] = Doc() });
        var test = (await r.CallAsync("scripts.testRun", new JsonObject { ["script"] = "poller", ["teamId"] = r.TeamId }))!.AsObject();

        var deleted = (await r.CallAsync("scripts.delete", new JsonObject { ["script"] = saved["id"]!.GetValue<long>() }))!.AsObject();
        Assert.True(deleted["deleted"]!.GetValue<bool>());

        Assert.Empty((await r.CallAsync("scripts.list"))!.AsArray());
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("scripts.get", new JsonObject { ["script"] = "poller", ["version"] = 1 }))).Code);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("scripts.run", new JsonObject { ["id"] = test["runId"]!.GetValue<long>() }))).Code);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("scripts.delete", new JsonObject { ["script"] = "poller" }))).Code);

        // The key is free again: a new save starts at version 1.
        Assert.Equal(1, (await r.CallAsync("scripts.save", new JsonObject { ["document"] = Doc() }))!["version"]!.GetValue<int>());
        Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => r.CallAsync("scripts.delete", new JsonObject()))).Code);
    }

    [Fact]
    public void Delete_IsAChange_RefusedForAVisitor()
    {
        Assert.Contains("scripts.delete", ScriptsRpc.Changes);
        Assert.DoesNotContain("scripts.delete", ScriptsRpc.Reads);
        Assert.Equal(RemoteAccess.ReadOnlyRefusal, RemoteAccess.Refusal("scripts.delete", new JsonObject { ["script"] = "poller" }, allowHostTerminals: false, RemoteAccess.Read));
        Assert.Null(RemoteAccess.Refusal("scripts.delete", new JsonObject { ["script"] = "poller" }, allowHostTerminals: false, RemoteAccess.Full));
    }

    [Fact]
    public async Task NextTriggerAt_IsNullWithoutAScheduledTrigger_AndTheSoonestEnabledScheduledFireOtherwise()
    {
        await using var r = await RigAsync();
        await r.CallAsync("scripts.save", new JsonObject { ["document"] = Doc() });
        var added = (await r.CallAsync("scripts.memberAdd", new JsonObject { ["teamId"] = r.TeamId, ["name"] = "Poller", ["script"] = "poller" }))!.AsObject();
        var memberId = added["memberId"]!.GetValue<long>();
        Assert.True(added.ContainsKey("nextTriggerAt"));
        Assert.Null(added["nextTriggerAt"]);

        // An event trigger is not a schedule; a disabled one does not fire.
        await r.Bus.SaveTriggerAsync(new TriggerDraft(null, memberId, "on mail", new TriggerSchedule(TriggerKinds.Event, null, null, null, null), "go", false, true,
            EventName: "mail.new"), Ct);
        var soon = DateTimeOffset.UtcNow.AddHours(1);
        await r.Bus.SaveTriggerAsync(new TriggerDraft(null, memberId, "off", new TriggerSchedule(TriggerKinds.Once, null, null, null, soon), "go", false, Enabled: false), Ct);
        Assert.Null((await r.CallAsync("scripts.memberGet", new JsonObject { ["memberId"] = memberId }))!["nextTriggerAt"]);

        var later = DateTimeOffset.UtcNow.AddDays(2);
        var sooner = DateTimeOffset.UtcNow.AddHours(3);
        await r.Bus.SaveTriggerAsync(new TriggerDraft(null, memberId, "later", new TriggerSchedule(TriggerKinds.Once, null, null, null, later), "go", false, true), Ct);
        await r.Bus.SaveTriggerAsync(new TriggerDraft(null, memberId, "sooner", new TriggerSchedule(TriggerKinds.Once, null, null, null, sooner), "go", false, true), Ct);

        var got = (await r.CallAsync("scripts.memberGet", new JsonObject { ["memberId"] = memberId }))!["nextTriggerAt"]!.GetValue<string>();
        var at = DateTimeOffset.Parse(got, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(sooner.ToUnixTimeMilliseconds(), at.ToUnixTimeMilliseconds());
        Assert.EndsWith("Z", got);

        // memberUpdate returns it too; another member's triggers are not this one's.
        var updated = (await r.CallAsync("scripts.memberUpdate", new JsonObject { ["memberId"] = memberId, ["timeoutMinutes"] = 3 }))!.AsObject();
        Assert.Equal(got, updated["nextTriggerAt"]!.GetValue<string>());
        var other = await AddMemberAsync(r, "Other");
        Assert.Null((await r.CallAsync("scripts.memberGet", new JsonObject { ["memberId"] = other }))!["nextTriggerAt"]);
    }
}
