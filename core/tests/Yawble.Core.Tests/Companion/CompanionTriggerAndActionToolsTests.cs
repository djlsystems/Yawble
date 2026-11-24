using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Provenance;
using Yawble.Core.Scripts;
using Yawble.Core.Tests.Concierge;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Companion;

/// <summary>
/// Card 634 (B139): script_trigger_set and integration_action_allow for a paired companion client, at the destructive level like
/// script_member_add. A call that needs the person's yes becomes a request whose plan names the details; Approve in the window runs the
/// stored call once (a second Approve runs nothing), Deny writes nothing. Turning a trigger off runs at once. The real pieces of
/// <see cref="TriggerAndActionRig"/>; no credentials.
/// </summary>
public sealed class CompanionTriggerAndActionToolsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TriggerAndActionRig _r = null!;
    private CompanionClientStore _clients = null!;
    private AuditLog _audit = null!;
    private CompanionService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _r = await TriggerAndActionRig.StartAsync();
        _clients = new CompanionClientStore(_r.F.Db.ConnectionString);
        _audit = new AuditLog(_r.F.Db.ConnectionString);
        _service = new CompanionService(_r.Bus, new ConciergeStore(_r.F.Db.ConnectionString), _r.Route, (_, _) => null, _clients, _audit, new AppEvents());
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        await _r.DisposeAsync();
    }

    private CompanionClient Client(CompanionLevel level, string name = "cc") => _clients.Add(name, level, "tester", out _);

    private static JsonObject Trigger(bool? enabled = null) =>
        enabled is { } e
            ? new() { ["team"] = "Alpha", ["member"] = "Poller", ["name"] = "hourly", ["schedule"] = new JsonObject { ["kind"] = "every", ["intervalSec"] = 3600 }, ["enabled"] = e }
            : new() { ["team"] = "Alpha", ["member"] = "Poller", ["name"] = "hourly", ["schedule"] = new JsonObject { ["kind"] = "every", ["intervalSec"] = 3600 } };

    private static JsonObject Allow() => new() { ["team"] = "Alpha", ["integration"] = "sms", ["action"] = "CreateMessage" };

    private async Task<ActionStatus> Decide(string id, bool approve)
    {
        ActionStatus s;
        using (Actor.Use(Actor.Window)) s = await _service.DecideActionAsync(id, approve, Ct);
        return s.State == ActionState.Running && _service.RunOf(id) is { } run ? await run.WaitAsync(TimeSpan.FromSeconds(30), Ct) : s;
    }

    private async Task<string> RequestStatus(CompanionClient c, string id)
    {
        var r = await _service.CallAsync(c, "request_status", new JsonObject { ["id"] = id }, Ct);
        Assert.False(r.IsError, r.Text);
        return r.Text;
    }

    [Fact]
    public async Task TriggerSet_IsARequestNamingTheDetails_ApproveRunsItOnce_ASecondApproveRunsNothing()
    {
        var cc = Client(CompanionLevel.Destructive);
        var r = await _service.CallAsync(cc, ScriptTools.ScriptTriggerSet, Trigger(), Ct);

        Assert.False(r.IsError, r.Text);
        Assert.NotNull(r.RequestId);
        Assert.StartsWith($"NOT DONE: waiting for the person's approval in the Yawble window (request {r.RequestId}): ", r.Text);
        Assert.Contains("Turn on the trigger \"hourly\" of script member Poller of team Alpha (a new trigger): it fires every hour.", r.Text);
        Assert.Contains("Each fire runs the script poller, the latest clean version (now 1", r.Text);
        Assert.Equal(0, _r.Count("triggers.save"));
        Assert.Contains("pending", await RequestStatus(cc, r.RequestId!));

        var s = await Decide(r.RequestId!, approve: true);

        Assert.Equal(ActionState.Done, s.State);
        Assert.Equal(1, _r.Count("triggers.save"));
        var row = Assert.Single(await _r.TriggersListed());
        Assert.True(row["enabled"]!.GetValue<bool>());
        Assert.Equal(_r.PollerId, row["memberId"]!.GetValue<long>());
        Assert.Contains("of script member Alpha/Poller: on, it fires every hour", await RequestStatus(cc, r.RequestId!));

        await Decide(r.RequestId!, approve: true);
        Assert.Equal(1, _r.Count("triggers.save"));
        Assert.Single(await _r.TriggersListed());
    }

    [Fact]
    public async Task TriggerSet_Denied_WritesNothing_AndTurningOneOffRunsAtOnce()
    {
        var cc = Client(CompanionLevel.Destructive);
        var r = await _service.CallAsync(cc, ScriptTools.ScriptTriggerSet, Trigger(), Ct);
        Assert.Equal(ActionState.Denied, (await Decide(r.RequestId!, approve: false)).State);
        Assert.Equal(0, _r.Count("triggers.save"));
        Assert.Empty(await _r.TriggersListed());

        var off = await _service.CallAsync(cc, ScriptTools.ScriptTriggerSet, Trigger(enabled: false), Ct);
        Assert.False(off.IsError, off.Text);
        Assert.Null(off.RequestId);
        Assert.StartsWith("ok: saved trigger", off.Text);
        Assert.False(Assert.Single(await _r.TriggersListed())["enabled"]!.GetValue<bool>());
        Assert.Equal(0, _service.PendingCount);
        Assert.Contains(_audit.Recent(100), a => a.Method == "companion.script_trigger_set" && a.Outcome == "ok");
    }

    [Fact]
    public async Task ActionAllow_IsARequestNamingTheDetails_ApproveRunsItOnce_ASecondApproveRunsNothing()
    {
        var cc = Client(CompanionLevel.Destructive);
        var r = await _service.CallAsync(cc, ScriptTools.IntegrationActionAllow, Allow(), Ct);

        Assert.False(r.IsError, r.Text);
        Assert.NotNull(r.RequestId);
        Assert.Contains("Allow the action CreateMessage of the API sms for team Alpha: POST /messages (Send a message).", r.Text);
        Assert.Empty(_r.Rules());

        Assert.Equal(ActionState.Done, (await Decide(r.RequestId!, approve: true)).State);
        Assert.Equal((_r.Alpha, "CreateMessage", "allow"), Assert.Single(_r.Rules()));
        Assert.Equal("allow", await _r.CatalogDecision(_r.Alpha, "CreateMessage"));
        Assert.Equal(1, _r.Count("integrations.saveChecklist"));

        await Decide(r.RequestId!, approve: true);
        Assert.Equal(1, _r.Count("integrations.saveChecklist"));
        Assert.Single(_r.Rules());

        // Allowed now: answered at once, no request.
        var again = await _service.CallAsync(cc, ScriptTools.IntegrationActionAllow, Allow(), Ct);
        Assert.Null(again.RequestId);
        Assert.StartsWith("already allowed", again.Text);
    }

    [Fact]
    public async Task ActionAllow_Denied_WritesNothing()
    {
        var cc = Client(CompanionLevel.Destructive);
        var r = await _service.CallAsync(cc, ScriptTools.IntegrationActionAllow, Allow(), Ct);
        Assert.Equal(ActionState.Denied, (await Decide(r.RequestId!, approve: false)).State);
        Assert.Empty(_r.Rules());
        Assert.Equal(0, _r.Count("integrations.saveChecklist"));
        Assert.Equal("ask", await _r.CatalogDecision(_r.Alpha, "CreateMessage"));
    }

    [Fact]
    public async Task InvalidTargetsAreRefusedWithoutWrites()
    {
        var cc = Client(CompanionLevel.Destructive);
        var agent = Trigger();
        agent["member"] = "Ada";
        var agentResult = await _service.CallAsync(cc, ScriptTools.ScriptTriggerSet, agent, Ct);
        Assert.True(agentResult.IsError, agentResult.Text);
        foreach (var (team, integration, action) in new[]
        {
            ("Alpha", "sms", "GetToken"), ("Alpha", "sms", "Teleport"), ("Gamma", "sms", "CreateMessage")
        })
        {
            var args = new JsonObject { ["team"] = team, ["integration"] = integration, ["action"] = action };
            var result = await _service.CallAsync(cc, ScriptTools.IntegrationActionAllow, args, Ct);
            Assert.True(result.IsError, result.Text);
        }
        Assert.Equal(0, _r.Count("triggers.save") + _r.Count("integrations.saveChecklist"));
        Assert.Empty(_r.Rules());
        Assert.Equal(0, _service.PendingCount);
    }

    [Fact]
    public async Task ApprovedActionAllowsLiveScriptCallOnlyForThatTeam()
    {
        var cc = Client(CompanionLevel.Destructive);
        var request = await _service.CallAsync(cc, ScriptTools.IntegrationActionAllow, Allow(), Ct);
        Assert.Equal(ActionState.Done, (await Decide(request.RequestId!, approve: true)).State);
        var sent = 0;
        _r.Http.Add(TriggerAndActionRig.Host + "/messages", req =>
        {
            sent++;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        });
        var args = new JsonObject { ["body"] = new JsonObject { ["to"] = "placeholder" } };
        var result = await _r.Svc.CallForScriptAsync(new Yawble.Core.Integrations.ScriptCaller("Alpha", "Poller", "run-live", DryRun: false), "sms", "CreateMessage", args, Ct);
        Assert.True(result.Sent);
        Assert.Equal(1, sent);
        var refused = await Assert.ThrowsAsync<Yawble.Core.Integrations.ScriptApiRefusedException>(() =>
            _r.Svc.CallForScriptAsync(new Yawble.Core.Integrations.ScriptCaller("Beta", "Someone", "run-beta", DryRun: false), "sms", "CreateMessage", args, Ct));
        Assert.Contains("needs approval", refused.Message);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task DistinctActionsForOneTeamRemainSeparateRequests()
    {
        var cc = Client(CompanionLevel.Destructive);
        var first = await _service.CallAsync(cc, ScriptTools.IntegrationActionAllow, Allow(), Ct);
        var secondArgs = Allow();
        secondArgs["action"] = "ListMessages";
        var second = await _service.CallAsync(cc, ScriptTools.IntegrationActionAllow, secondArgs, Ct);
        Assert.NotNull(first.RequestId);
        Assert.NotNull(second.RequestId);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Contains("pending", await RequestStatus(cc, first.RequestId!));
        Assert.Contains("pending", await RequestStatus(cc, second.RequestId!));
        Assert.Equal(2, _service.PendingCount);
        Assert.Empty(_r.Rules());
    }

    [Fact]
    public async Task BothAreAbsentBelowTheDestructiveLevel()
    {
        Assert.Equal(CompanionLevel.Destructive, CompanionTools.LevelOf(ScriptTools.ScriptTriggerSet, Trigger()));
        Assert.Equal(CompanionLevel.Destructive, CompanionTools.LevelOf(ScriptTools.ScriptTriggerSet, Trigger(enabled: false)));
        Assert.Equal(CompanionLevel.Destructive, CompanionTools.LevelOf(ScriptTools.IntegrationActionAllow, Allow()));
        static HashSet<string> Names(CompanionLevel level) => [.. CompanionTools.Definitions(level).Select(d => d!["name"]!.GetValue<string>())];
        foreach (var level in new[] { CompanionLevel.Read, CompanionLevel.Act })
        {
            Assert.DoesNotContain(ScriptTools.ScriptTriggerSet, Names(level));
            Assert.DoesNotContain(ScriptTools.IntegrationActionAllow, Names(level));
        }
        Assert.Contains(ScriptTools.ScriptTriggerSet, Names(CompanionLevel.Destructive));
        Assert.Contains(ScriptTools.IntegrationActionAllow, Names(CompanionLevel.Destructive));

        var actor = Client(CompanionLevel.Act, "actor");
        foreach (var (tool, args) in new[] { (ScriptTools.ScriptTriggerSet, Trigger(enabled: false)), (ScriptTools.IntegrationActionAllow, Allow()) })
        {
            var r = await _service.CallAsync(actor, tool, args, Ct);
            Assert.True(r.IsError);
            Assert.Contains("needs the destructive level", r.Text);
        }
        Assert.Equal(0, _r.Count("triggers.save") + _r.Count("integrations.saveChecklist"));
        Assert.Empty(_r.Rules());
        Assert.Equal(0, _service.PendingCount);

        // Offered without confirm, and without the Concierge's token wording; the window's approval is named.
        foreach (var def in CompanionTools.Definitions(CompanionLevel.Destructive).OfType<JsonObject>()
                     .Where(d => d["name"]!.GetValue<string>() is ScriptTools.ScriptTriggerSet or ScriptTools.IntegrationActionAllow))
        {
            Assert.False(def["inputSchema"]!["properties"]!.AsObject().ContainsKey("confirm"));
            Assert.DoesNotContain("token", def.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("request_status", def["description"]!.GetValue<string>());
        }
    }
}
