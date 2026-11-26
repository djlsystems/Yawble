using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Concierge;
using Yawble.Core.Integrations;
using Yawble.Core.Workflows;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// workflow_trigger_set and integration_action_allow through the Concierge's dispatcher, onto the real triggers.save,
/// workflows.* and the API rule editor's save (<see cref="TriggerAndActionRig"/>). Turning a workflow member's trigger on, and allowing an API action, need
/// the person's yes in a later turn (ConciergeActions.ConfirmAsync); turning one off and an action already allowed need none.
/// </summary>
public sealed class TriggerAndActionToolsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private TriggerAndActionRig _r = null!;

    public async ValueTask InitializeAsync() => _r = await TriggerAndActionRig.StartAsync();

    public async ValueTask DisposeAsync() => await _r.DisposeAsync();

    private async Task<string> Ok(string tool, JsonObject args, ConciergeTools? tools = null)
    {
        var (text, isError) = await (tools ?? _r.Tools()).CallToolAsync(tool, args, Ct);
        Assert.False(isError, text);
        return text;
    }

    private async Task<string> Refused(string tool, JsonObject args, ConciergeTools? tools = null)
    {
        var (text, isError) = await (tools ?? _r.Tools()).CallToolAsync(tool, args, Ct);
        Assert.True(isError, text);
        return text;
    }

    private static string Token(string text) => Regex.Match(text, "confirm=\"([0-9a-f]+)\"").Groups[1].Value;

    private static JsonObject With(JsonObject args, string key, JsonNode? value)
    {
        var copy = args.DeepClone().AsObject();
        copy[key] = value;
        return copy;
    }

    private static JsonObject Every(int seconds) => new() { ["kind"] = "every", ["intervalSec"] = seconds };

    private static JsonObject Trigger(string member = "Poller", JsonObject? schedule = null, bool? enabled = null, long? id = null)
    {
        var a = new JsonObject { ["team"] = "Alpha", ["member"] = member, ["name"] = "every quarter", ["schedule"] = schedule ?? Every(900) };
        if (enabled is { } e) a["enabled"] = e;
        if (id is { } i) a["id"] = i;
        return a;
    }

    private static JsonObject Allow(string action = "CreateMessage", string team = "Alpha", string integration = "sms") =>
        new() { ["team"] = team, ["integration"] = integration, ["action"] = action };

    // ------------------------------------------------------------------ the list

    [Fact]
    public async Task ToolsList_OffersBoth_ActingWithTheirApprovals_AndTheirInputSchemas()
    {
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" }.ToJsonString());
        var reply = await _r.Tools().HandleAsync(body, Ct);
        var tools = JsonNode.Parse(reply.Json!)!["result"]!["tools"]!.AsArray().OfType<JsonObject>().ToList();
        foreach (var n in new[] { WorkflowTools.WorkflowTriggerSet, WorkflowTools.IntegrationActionAllow })
        {
            Assert.Contains(tools, t => t["name"]!.GetValue<string>() == n);
            Assert.Contains(n, ConciergeTools.ToolNames);
            Assert.Contains(n, ConciergeTools.Acting);
        }
        Assert.Contains(WorkflowTools.IntegrationActionAllow, ConciergeTools.LocalOnly);
        Assert.DoesNotContain(WorkflowTools.WorkflowTriggerSet, ConciergeTools.LocalOnly); // triggers.save is allowed over the Cloud Link
        Assert.Equal(WorkflowToolApproval.EnabledTrigger, WorkflowTools.ApprovalOf(WorkflowTools.WorkflowTriggerSet));
        Assert.Equal(WorkflowToolApproval.PersonsYes, WorkflowTools.ApprovalOf(WorkflowTools.IntegrationActionAllow));

        var set = tools.Single(t => t["name"]!.GetValue<string>() == WorkflowTools.WorkflowTriggerSet)["inputSchema"]!;
        Assert.Equal(["team", "member", "name", "schedule"], set["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["team", "member", "name", "schedule", "enabled", "id", "only_when_idle", "confirm"], set["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(WorkflowTools.ScheduleFields, set["properties"]!["schedule"]!["properties"]!.AsObject().Select(p => p.Key));
        var allow = tools.Single(t => t["name"]!.GetValue<string>() == WorkflowTools.IntegrationActionAllow)["inputSchema"]!;
        Assert.Equal(["team", "integration", "action"], allow["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["team", "integration", "action", "confirm"], allow["properties"]!.AsObject().Select(p => p.Key));
    }

    // ------------------------------------------------------------------ workflow_trigger_set

    [Fact]
    public async Task CreatingAnEnabledTrigger_IsAsked_AndRefusedWithoutTheYes()
    {
        var shown = await Ok(WorkflowTools.WorkflowTriggerSet, Trigger());
        Assert.StartsWith("NOT DONE", shown);
        Assert.Contains("Turn on the trigger \"every quarter\" of workflow member Poller of team Alpha (a new trigger): it fires every 15 minutes.", shown);
        Assert.Contains("Each fire runs the workflow poller, the latest clean version (now 1; it follows new ones)", shown);
        Assert.Equal(0, _r.Count("triggers.save"));
        Assert.Empty(await _r.TriggersListed());

        // The same turn that showed the plan cannot say yes; a made-up token is unknown.
        var sameTurn = _r.Tools();
        var first = await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(), sameTurn);
        Assert.Contains("the person has not said yes yet", await Refused(WorkflowTools.WorkflowTriggerSet, With(Trigger(), "confirm", Token(first)), sameTurn));
        Assert.StartsWith("NOT DONE. That confirmation is unknown", await Ok(WorkflowTools.WorkflowTriggerSet, With(Trigger(), "confirm", "0123456789abcdef")));
        Assert.Equal(0, _r.Count("triggers.save"));
        Assert.Empty(await _r.TriggersListed());
    }

    [Fact]
    public async Task WithTheYes_TheTriggerIsSaved_AndTriggersListShowsItEnabledAimedAtTheWorkflowMember()
    {
        var shown = await Ok(WorkflowTools.WorkflowTriggerSet, Trigger());
        var done = await Ok(WorkflowTools.WorkflowTriggerSet, With(Trigger(), "confirm", Token(shown)));
        Assert.Contains("saved trigger", done);
        Assert.Contains("of workflow member Alpha/Poller: on, it fires every 15 minutes.", done);
        Assert.Equal(1, _r.Count("triggers.save"));
        var p = _r.Calls.Single(c => c.Method == "triggers.save").Params;
        Assert.Equal(_r.PollerId, p["memberId"]!.GetValue<long>());
        Assert.Equal("every", p["kind"]!.GetValue<string>());
        Assert.Equal(900, p["intervalSec"]!.GetValue<long>());

        var row = Assert.Single(await _r.TriggersListed());
        Assert.Equal(_r.PollerId, row["memberId"]!.GetValue<long>());
        Assert.True(row["enabled"]!.GetValue<bool>());
        Assert.Equal("every quarter", row["name"]!.GetValue<string>());
        Assert.NotNull(row["nextDueAt"]);

        // The token is used up.
        Assert.StartsWith("NOT DONE. That confirmation is unknown", await Ok(WorkflowTools.WorkflowTriggerSet, With(Trigger(), "confirm", Token(shown))));
        Assert.Single(await _r.TriggersListed());
    }

    [Fact]
    public async Task AChangedArgument_VoidsTheYes()
    {
        // Each yes is for the plan it was shown with: a changed call with its token is refused, and the token is spent.
        foreach (var changed in new[]
                 {
                     Trigger(schedule: Every(600)),
                     With(Trigger(), "name", "every ten"),
                     Trigger(member: "Other"),
                     Trigger(schedule: new JsonObject { ["kind"] = "cron", ["cron"] = "0 9 * * 1-5", ["timezone"] = "UTC" }),
                 })
        {
            var token = Token(await Ok(WorkflowTools.WorkflowTriggerSet, Trigger()));
            Assert.StartsWith("NOT DONE. THE LIST CHANGED", await Ok(WorkflowTools.WorkflowTriggerSet, With(changed, "confirm", token)));
            Assert.StartsWith("NOT DONE. That confirmation is unknown", await Ok(WorkflowTools.WorkflowTriggerSet, With(Trigger(), "confirm", token)));
        }
        Assert.Equal(0, _r.Count("triggers.save"));
        Assert.Empty(await _r.TriggersListed());

        var cron = await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(schedule: new JsonObject { ["kind"] = "cron", ["cron"] = "0 9 * * 1-5", ["timezone"] = "UTC" }));
        Assert.Contains("it fires on the cron schedule \"0 9 * * 1-5\" (UTC)", cron);
        var ev = await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(schedule: new JsonObject { ["kind"] = "event", ["eventName"] = "web.pr" }));
        Assert.Contains("it fires whenever the event web.pr is published", ev);
        var folder = await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(schedule: new JsonObject { ["kind"] = "folder", ["watchRoot"] = "documents", ["watchPath"] = "inbox", ["watchGlob"] = "*.csv" }));
        Assert.Contains("it fires when files are added in the team's documents/inbox matching *.csv (looked at every 30 seconds)", folder);
        var pinned = await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(member: "Other"));
        Assert.Contains("Each fire runs the workflow poller version 1 (pinned)", pinned);
    }

    [Fact]
    public async Task SavingOff_AndTurningAnEnabledTriggerOff_NeedNoYes_ButTurningItBackOnDoes()
    {
        var off = await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(enabled: false));
        Assert.StartsWith("ok: saved trigger", off);
        Assert.Contains(": off, it does not fire", off);
        var row = Assert.Single(await _r.TriggersListed());
        Assert.False(row["enabled"]!.GetValue<bool>());
        var id = row["id"]!.GetValue<long>();

        // Turning it on is asked; with the yes it is on.
        var on = await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(id: id));
        Assert.Contains($"(trigger {id}, now off)", on);
        await Ok(WorkflowTools.WorkflowTriggerSet, With(Trigger(id: id), "confirm", Token(on)));
        Assert.True(Assert.Single(await _r.TriggersListed())["enabled"]!.GetValue<bool>());

        // Saved again unchanged while on: no yes. Its schedule changing is asked.
        Assert.StartsWith("ok: saved trigger", await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(id: id)));
        var moved = await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(id: id, schedule: Every(3600)));
        Assert.StartsWith("NOT DONE", moved);
        Assert.Contains($"(trigger {id}, its schedule changes): it fires every hour.", moved);

        // Turning it off: no yes.
        var saves = _r.Count("triggers.save");
        Assert.StartsWith("ok: saved trigger", await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(id: id, enabled: false)));
        Assert.Equal(saves + 1, _r.Count("triggers.save"));
        row = Assert.Single(await _r.TriggersListed());
        Assert.False(row["enabled"]!.GetValue<bool>());
        Assert.Null(row["nextDueAt"]);
    }

    [Fact]
    public async Task AnAgentMember_AnUnknownMember_AndAnIdOfAnotherMember_AreEachRefusedWithTheReason()
    {
        Assert.Contains("Alpha/Ada is an agent member, not a workflow member: workflow_trigger_set is for workflow members only",
            await Refused(WorkflowTools.WorkflowTriggerSet, Trigger(member: "Ada")));
        Assert.Contains("Alpha has no member 'Nobody'", await Refused(WorkflowTools.WorkflowTriggerSet, Trigger(member: "Nobody")));
        // Off needs no yes, and is still refused for an agent.
        Assert.Contains("is an agent member", await Refused(WorkflowTools.WorkflowTriggerSet, Trigger(member: "Ada", enabled: false)));

        await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(member: "Other", enabled: false));
        var others = Assert.Single(await _r.TriggersListed())["id"]!.GetValue<long>();
        Assert.Contains($"trigger {others} is not one of Alpha/Poller's triggers", await Refused(WorkflowTools.WorkflowTriggerSet, Trigger(id: others, enabled: false)));
        Assert.Contains("trigger 9999 is not one of Alpha/Poller's triggers", await Refused(WorkflowTools.WorkflowTriggerSet, Trigger(id: 9999, enabled: false)));
        Assert.Contains("'schedule' has no field 'every'", await Refused(WorkflowTools.WorkflowTriggerSet, Trigger(schedule: new JsonObject { ["every"] = 5 }, enabled: false)));
        Assert.Single(await _r.TriggersListed());
        Assert.Equal(1, _r.Count("triggers.save"));
    }

    [Fact]
    public async Task AReviewTurn_SetsNoTrigger_AndAllowsNoAction()
    {
        var review = _r.Tools(byPerson: false);
        Assert.Contains("was not started by a message from the person", await Refused(WorkflowTools.WorkflowTriggerSet, Trigger(enabled: false), review));
        Assert.Contains("was not started by a message from the person", await Refused(WorkflowTools.IntegrationActionAllow, Allow(), review));
        Assert.Equal(0, _r.Count("triggers.save") + _r.Count("integrations.saveChecklist"));
    }

    // ------------------------------------------------------------------ integration_action_allow

    [Fact]
    public async Task ActionAllow_ChangedActionVoidsAndConsumesTheYes_WithoutWritingARule()
    {
        var token = Token(await Ok(WorkflowTools.IntegrationActionAllow, Allow()));
        Assert.NotEmpty(token);
        Assert.StartsWith("NOT DONE. THE LIST CHANGED", await Ok(WorkflowTools.IntegrationActionAllow, With(Allow("ListMessages"), "confirm", token)));
        Assert.StartsWith("NOT DONE. That confirmation is unknown", await Ok(WorkflowTools.IntegrationActionAllow, With(Allow(), "confirm", token)));
        Assert.Empty(_r.Rules());
        Assert.Equal(0, _r.Count("integrations.saveChecklist"));
    }

    [Fact]
    public async Task AllowingAnAction_IsAsked_AndRefusedWithoutTheYes()
    {
        Assert.Equal("ask", await _r.CatalogDecision(_r.Alpha, "CreateMessage"));
        var shown = await Ok(WorkflowTools.IntegrationActionAllow, Allow());
        Assert.StartsWith("NOT DONE", shown);
        Assert.Contains("Allow the action CreateMessage of the API sms for team Alpha: POST /messages (Send a message).", shown);
        Assert.Contains("Now it is \"ask\" for Alpha.", shown);
        Assert.Empty(_r.Rules());

        var sameTurn = _r.Tools();
        var first = await Ok(WorkflowTools.IntegrationActionAllow, Allow(), sameTurn);
        Assert.Contains("the person has not said yes yet", await Refused(WorkflowTools.IntegrationActionAllow, With(Allow(), "confirm", Token(first)), sameTurn));
        Assert.StartsWith("NOT DONE. THE LIST CHANGED", await Ok(WorkflowTools.IntegrationActionAllow, With(Allow("ListMessages"), "confirm", Token(shown))));
        var again = await Ok(WorkflowTools.IntegrationActionAllow, Allow());
        Assert.StartsWith("NOT DONE. THE LIST CHANGED", await Ok(WorkflowTools.IntegrationActionAllow, With(Allow(team: "Beta"), "confirm", Token(again))));
        Assert.Empty(_r.Rules());
        Assert.Equal(0, _r.Count("integrations.saveChecklist"));
    }

    [Fact]
    public async Task WithTheYes_OneTeamRule_AWorkflowCallGoesPastNeedsApproval_TheCatalogSaysAllow_AndAnotherTeamIsStillAsk()
    {
        var before = await Assert.ThrowsAsync<WorkflowApiRefusedException>(() =>
            _r.Svc.CallForWorkflowAsync(new WorkflowCaller("Alpha", "Poller", "run-1", DryRun: true), "sms", "CreateMessage", new JsonObject { ["body"] = new JsonObject { ["to"] = "placeholder" } }, Ct));
        Assert.Equal("needs approval: allow CreateMessage for Alpha", before.Message);

        var shown = await Ok(WorkflowTools.IntegrationActionAllow, Allow());
        var done = await Ok(WorkflowTools.IntegrationActionAllow, With(Allow(), "confirm", Token(shown)));
        Assert.Contains("allowed sms's CreateMessage (POST /messages) for Alpha: one team rule.", done);

        var rule = Assert.Single(_r.Rules());
        Assert.Equal((_r.Alpha, "CreateMessage", "allow"), (rule.Team, rule.Pattern, rule.Decision));
        Assert.Equal(1, _r.Count("integrations.saveChecklist"));

        var after = await _r.Svc.CallForWorkflowAsync(new WorkflowCaller("Alpha", "Poller", "run-2", DryRun: true), "sms", "CreateMessage", new JsonObject { ["body"] = new JsonObject { ["to"] = "placeholder" } }, Ct);
        Assert.False(after.Sent);
        Assert.StartsWith("would POST", after.Body);
        Assert.Equal("allow", await _r.CatalogDecision(_r.Alpha, "CreateMessage"));

        Assert.Equal("ask", await _r.CatalogDecision(_r.Beta, "CreateMessage"));
        var beta = await Assert.ThrowsAsync<WorkflowApiRefusedException>(() =>
            _r.Svc.CallForWorkflowAsync(new WorkflowCaller("Beta", "Someone", "run-3", DryRun: true), "sms", "CreateMessage", new JsonObject { ["body"] = new JsonObject { ["to"] = "placeholder" } }, Ct));
        Assert.Equal("needs approval: allow CreateMessage for Beta", beta.Message);

        // Now it is allowed: answered without a yes, nothing written.
        Assert.StartsWith("already allowed: sms's CreateMessage is already allowed for Alpha", await Ok(WorkflowTools.IntegrationActionAllow, Allow(integration: "api:sms")));
        Assert.Single(_r.Rules());
        Assert.Equal(1, _r.Count("integrations.saveChecklist"));
    }

    [Fact]
    public async Task UnknownApi_ApiNotOnForTheTeam_UnknownAction_AndAFloorAction_AreEachRefused_NothingWritten()
    {
        Assert.Contains("no API named 'nope'", await Refused(WorkflowTools.IntegrationActionAllow, Allow(integration: "nope")));
        Assert.Contains("sms is not turned on for Gamma", await Refused(WorkflowTools.IntegrationActionAllow, Allow(team: "Gamma")));
        Assert.Contains("sms has no action 'Teleport'", await Refused(WorkflowTools.IntegrationActionAllow, Allow("Teleport")));
        Assert.Contains("sms's GetToken reveals or changes a sign-in: it is never allowed", await Refused(WorkflowTools.IntegrationActionAllow, Allow("GetToken")));
        Assert.Contains("there is no team 'Omega'", await Refused(WorkflowTools.IntegrationActionAllow, Allow(team: "Omega")));
        Assert.Empty(_r.Rules());
        Assert.Equal(0, _r.Count("integrations.saveChecklist"));
    }

    [Fact]
    public async Task AlreadyAllowed_IsAnsweredWithoutAYes()
    {
        await _r.Svc.DispatchAsync("integrations.saveChecklist", new JsonObject
        {
            ["name"] = "sms", ["kind"] = "api", ["teamId"] = _r.Alpha,
            ["choices"] = new JsonArray(new JsonObject { ["path"] = "ListMessages", ["state"] = "allow" }),
        }, Ct);
        var text = await Ok(WorkflowTools.IntegrationActionAllow, Allow("ListMessages"));
        Assert.Equal("already allowed: sms's ListMessages is already allowed for Alpha; nothing was changed.", text);
        Assert.Single(_r.Rules());
        Assert.Equal(0, _r.Count("integrations.saveChecklist"));
    }

    // ------------------------------------------------------------------ the Cloud Link

    [Fact]
    public async Task OverTheCloudLink_AllowingAnActionIsRefused_WhileATriggerStillAsksForTheYes()
    {
        var remote = _r.Tools(remote: true);
        Assert.Contains("refused: integration_action_allow is only for the person at this computer", await Refused(WorkflowTools.IntegrationActionAllow, Allow(), remote));
        // Even with a token the person made at the computer.
        var shown = await Ok(WorkflowTools.IntegrationActionAllow, Allow());
        Assert.Contains("only for the person at this computer", await Refused(WorkflowTools.IntegrationActionAllow, With(Allow(), "confirm", Token(shown)), _r.Tools(remote: true)));
        Assert.Empty(_r.Rules());

        Assert.True(WorkflowTools.RemoteRefused(WorkflowTools.IntegrationActionAllow, Allow()));
        Assert.False(WorkflowTools.RemoteRefused(WorkflowTools.WorkflowTriggerSet, Trigger()));
        Assert.StartsWith("NOT DONE", await Ok(WorkflowTools.WorkflowTriggerSet, Trigger(), remote));
        Assert.Equal(0, _r.Count("triggers.save"));
    }
}
