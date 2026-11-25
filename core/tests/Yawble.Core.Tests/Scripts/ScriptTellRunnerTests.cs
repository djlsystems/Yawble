using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using RunStatus = Yawble.Core.Scripts.Running.ScriptRunStatus;

namespace Yawble.Core.Tests.Scripts;

/// <summary>
/// The tell step end to end: the real engine, checker and bus (agents are a fake runner, the clock a fake), through the bus teller.
/// Telling the Manager and another agent member, refusing script members and names that are not agent members, waiting and going on
/// when the workflow is done, fails or is blocked, after a restart and at the timeout, triggers skipped while a run waits, cancelling,
/// the other-teams switch, the tell cap, a Test run's made-up reply, and the RPC shapes the shell reads.
/// </summary>
public class ScriptTellRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-24T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private const string Triage = """
        { "type": "object", "properties": { "refund": { "type": "boolean" } }, "required": ["refund"] }
        """;

    /// <summary>A script whose one tell goes to settings.member of settings.team, waiting or not, then logs what came back.</summary>
    private static string TellScript(string id, bool wait) => $$"""
        { "format": "yawble-script/1", "id": "{{id}}", "name": "{{id}}", "trigger": { "accepts": ["once"] },
          "settings": [ { "name": "member", "type": "string", "default": "Manager" }, { "name": "team", "type": "string", "default": "Alpha" } ],
          "variables": [ { "name": "t", "type": "TellResult" } ],
          "start": "a",
          "steps": [
            { "id": "a", "type": "tell", "member": "{settings.member}", "team": "{settings.team}", "text": "Look at order 7.",
              {{(wait ? "\"wait\": true, \"timeout\": \"2h\", \"reply\": \"Triage\"," : "")}} "into": "t", "next": "b" },
            { "id": "b", "type": "log", "text": "tell: {vars.t.outcome} {vars.t.workflow} {vars.t.data.refund} {vars.t.reply}", "next": "c" },
            { "id": "c", "type": "end" } ] }
        """;

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public readonly FakeClock Clock = new(T0);
        public BusEngine Bus = null!;
        public ScriptLibrary Library = null!;
        public ScriptRunStore Store = null!;
        public ScriptRunner Runner = null!;
        public BusScriptTells Tells = null!;
        public long Alpha, Beta, GoScript, WaitScript;

        public async Task<Rig> InitAsync()
        {
            ScriptRunner? runner = null;
            Bus = new BusEngine(Db.OpenStore(), new ScriptDispatchRunner(new ScriptedRunner(), () => runner), new BusOptions { IdleOffer = false });
            Alpha = await Bus.CreateTeamAsync("Alpha", ct: Ct);
            await Bus.AddMemberAsync("Alpha", "Ada", ct: Ct);
            Beta = await Bus.CreateTeamAsync("Beta", ct: Ct);
            Library = new ScriptLibrary(Db.ConnectionString, () => FakeCatalog.MailAndSms()); // the real checker
            Library.SaveSchema("Triage", "A refund decision.", Triage);
            Store = new ScriptRunStore(Db.ConnectionString);
            Tells = new BusScriptTells(Bus, Store);
            Runner = runner = NewRunner();
            Bus.ScriptMembers = runner;
            var go = Library.Save(TellScript("tell-go", wait: false));
            var wait = Library.Save(TellScript("tell-wait", wait: true));
            Assert.True(go.Ok && wait.Ok, go.Check.ToJsonString() + wait.Check.ToJsonString());
            (GoScript, WaitScript) = (go.Id, wait.Id);
            await Bus.StartAsync(Ct);
            return this;
        }

        public ScriptRunner NewRunner() =>
            new(Store, Library, NoScriptActions.Instance, SeamScriptEngine.Instance, Path.Combine(Db.Dir, "scripts"), clock: Clock)
            {
                Tellers = Tells, WaitSources = [Tells],
            };

        public async Task<long> MemberAsync(long script, JsonObject? settings = null, int? maxTells = null, string name = "Bot")
        {
            var row = await Bus.AddScriptMemberAsync(Alpha, name, Ct);
            Store.SetScriptMember(row.Id, script, null, settings ?? [], [], null, maxTells);
            return row.Id;
        }

        public Task<ScriptRunOutcome> RunAsync(long member) =>
            Runner.RunMemberAsync(member, "run now", Yawble.Core.Scripts.Running.ScriptPayloads.TriggerFired(1, "once", T0), Ct);

        public ScriptsRpc Rpc() => new(Bus, Library, Store, Runner);

        public IReadOnlyList<Message> Instructions(string qualified)
        {
            using var s = Db.OpenStore();
            return s.AllMessages().Where(m => m.Type == MessageTypes.InstructionPrefix + qualified).ToList();
        }

        /// <summary>Something happens in the workflow, as the bus would record it (a completion, a block, a close).</summary>
        public void InWorkflow(long workflow, string type, JsonObject payload, string source)
        {
            using var s = Db.OpenStore();
            s.Append(new NewMessage(type, payload.ToJsonString(), source, workflow));
        }

        public async ValueTask DisposeAsync()
        {
            await Runner.StopAsync();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static string Why(ScriptRunRow run) => $"{run.Status} at {run.FailedStepId}: {run.Reason}";

    private static string Logged(ScriptRunOutcome o) => string.Join("\n", o.Log.Select(l => l.Line));

    // ------------------------------------------------------------------ sending

    [Fact]
    public async Task ATellToTheManager_StartsAWorkflowAsThePersonsTellDoes_AndSaysTheScriptMemberAndRun()
    {
        await using var r = await new Rig().InitAsync();
        var bot = await r.MemberAsync(r.GoScript);

        var outcome = await r.RunAsync(bot);

        Assert.True(outcome.Succeeded, Why(outcome.Run));
        var told = Assert.Single(r.Instructions("Alpha/Manager"));
        Assert.Equal((MessageTypes.PersonSource, (long?)null, 0), (told.Source, told.CausationSeq, told.Depth)); // a new workflow, as bus.tellManager
        var text = Payload.Field(told, "text")!;
        Assert.Contains($"From the script tell-go, run by the script member Bot of Alpha (script run {outcome.Run.Id})", text);
        Assert.EndsWith("Look at order 7.", text);
        var from = JsonNode.Parse(told.Payload)!["script"]!;
        Assert.Equal((outcome.Run.Id, bot, "a"), (from["run"]!.GetValue<long>(), from["memberId"]!.GetValue<long>(), from["step"]!.GetValue<string>()));
        Assert.Contains($"tell: sent {told.CorrelationId}", Logged(outcome));
    }

    [Fact]
    public async Task ATellToAnotherAgentMember_GoesStraightToIt_AsItsOwnWorkflow()
    {
        await using var r = await new Rig().InitAsync();
        var bot = await r.MemberAsync(r.GoScript, new JsonObject { ["member"] = "Ada" });

        var outcome = await r.RunAsync(bot);

        Assert.True(outcome.Succeeded, Why(outcome.Run));
        var told = Assert.Single(r.Instructions("Alpha/Ada")); // to Ada, not through the Manager's planning
        Assert.Null(told.CausationSeq);
        Assert.Empty(r.Instructions("Alpha/Manager"));
        var row = await r.Bus.WorkflowRowAsync(told.CorrelationId, Ct); // the Manager sees it in the team's workflows
        Assert.NotNull(row);
        Assert.Equal("Alpha", row.Team);
    }

    [Theory]
    [InlineData("Helper", "is a script member")]
    [InlineData("person", "has no agent member named 'person'")]
    [InlineData("Nobody", "has no agent member named 'Nobody'")]
    public async Task AScriptMember_ThePerson_OrAnUnknownName_CannotBeTold_AndTheStepFails(string member, string reason)
    {
        await using var r = await new Rig().InitAsync();
        await r.MemberAsync(r.GoScript, name: "Helper");
        var bot = await r.MemberAsync(r.GoScript, new JsonObject { ["member"] = member });

        var outcome = await r.RunAsync(bot);

        Assert.Equal((RunStatus.Failed, "a"), (outcome.Run.Status, outcome.Run.FailedStepId));
        Assert.Contains(reason, outcome.Run.Reason);
        using var s = r.Db.OpenStore();
        Assert.DoesNotContain(s.AllMessages(), m => m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheOtherTeamsSwitch_AllowsAnotherTeamByDefault_AndOffFailsTheStepWithThatReason()
    {
        await using var r = await new Rig().InitAsync();
        var bot = await r.MemberAsync(r.GoScript, new JsonObject { ["team"] = "Beta" });
        Assert.True(r.Store.TellOtherTeams());

        Assert.True((await r.RunAsync(bot)).Succeeded);
        Assert.Single(r.Instructions("Beta/Manager"));

        r.Store.SetTellOtherTeams(false);
        var off = await r.RunAsync(bot);
        Assert.Equal((RunStatus.Failed, "a"), (off.Run.Status, off.Run.FailedStepId));
        Assert.Contains("telling another team (Beta) is turned off in Settings > Scripts", off.Run.Reason);
        Assert.Single(r.Instructions("Beta/Manager"));

        var own = await r.MemberAsync(r.GoScript, name: "Own"); // its own team is never "another team"
        Assert.True((await r.RunAsync(own)).Succeeded);
    }

    [Fact]
    public async Task TheMembersMaxTells_CapsTheRunsTells()
    {
        await using var r = await new Rig().InitAsync();
        var bot = await r.MemberAsync(r.GoScript, maxTells: 0);
        Assert.Equal(0, r.Store.Member(bot)!.MaxTells);

        var outcome = await r.RunAsync(bot);

        Assert.Equal(RunStatus.Failed, outcome.Run.Status);
        Assert.Contains("maxTells is 0", outcome.Run.Reason);
        Assert.Empty(r.Instructions("Alpha/Manager"));
        Assert.Equal(5, r.Store.Member(await r.MemberAsync(r.GoScript, name: "Default"))!.MaxTells);
    }

    // ------------------------------------------------------------------ waiting

    private static async Task<(long Bot, ScriptRunOutcome Parked, long Workflow)> ParkedAsync(Rig r)
    {
        var bot = await r.MemberAsync(r.WaitScript);
        var parked = await r.RunAsync(bot);
        Assert.True(parked.Run.Status == RunStatus.Waiting, Why(parked.Run));
        var told = Assert.Single(r.Instructions("Alpha/Manager"));
        Assert.Equal(told.CorrelationId, parked.Run.WaitWorkflowId);
        return (bot, parked, told.CorrelationId);
    }

    [Fact]
    public async Task AWaitingTell_SavesTheRunWaitingWithItsWorkflow_HoldsNoSlot_AndGoesOnWhenTheWorkflowIsDone()
    {
        await using var r = await new Rig().InitAsync();
        var (bot, parked, workflow) = await ParkedAsync(r);

        Assert.False(r.Runner.IsRunning(bot)); // no run slot while it waits
        Assert.Contains("End your closing summary", Payload.Field(Assert.Single(r.Instructions("Alpha/Manager")), "text"));
        var row = ScriptsRpc.Run(r.Store.Run(parked.Run.Id)!);
        Assert.Equal(("waiting", workflow), (row["status"]!.GetValue<string>(), row["waitWorkflowId"]!.GetValue<long>()));
        Assert.Contains(r.Store.Steps(parked.Run.Id), s => s.StepId == "a" && s.Status == "waiting");

        Assert.Empty(await r.Runner.ResolveWaitsAsync(Ct)); // nothing has happened in the workflow yet

        r.InWorkflow(workflow, MessageTypes.WorkflowCompleted, new JsonObject { ["delivered"] = "Refund it. {\"refund\": true}" }, "Alpha/Manager");
        Assert.Equal([parked.Run.Id], await r.Runner.ResolveWaitsAsync(Ct));
        await r.Runner.WhenIdleAsync();

        var done = r.Store.Run(parked.Run.Id)!;
        Assert.True(done.Status == RunStatus.Succeeded, Why(done));
        Assert.Null(ScriptsRpc.Run(done)["waitWorkflowId"]);
        Assert.Contains(r.Store.LogLines(done.Id), l => l.Line == $"tell: done {workflow} true Refund it. {{\"refund\": true}}");
        Assert.Single(r.Instructions("Alpha/Manager")); // told once
        Assert.Equal(["start", "a", "b", "c"], r.Store.Steps(done.Id).Select(s => s.StepId));
        Assert.All(r.Store.Steps(done.Id), s => Assert.Equal("succeeded", s.Status));
    }

    [Theory]
    [InlineData(MessageTypes.WorkflowClosed, "reason", "not needed", "failed")]
    [InlineData(MessageTypes.Blocked, "reason", "needs the shop password", "blocked")]
    public async Task AWaitingTell_GoesOnWhenTheWorkflowFailsOrIsBlocked(string type, string field, string text, string outcome)
    {
        await using var r = await new Rig().InitAsync();
        var (_, parked, workflow) = await ParkedAsync(r);

        r.InWorkflow(workflow, type, new JsonObject { [field] = text }, type == MessageTypes.Blocked ? "Alpha/Manager" : MessageTypes.PersonSource);
        await r.Runner.ResolveWaitsAsync(Ct);
        await r.Runner.WhenIdleAsync();

        var run = r.Store.Run(parked.Run.Id)!;
        Assert.True(run.Status == RunStatus.Succeeded, Why(run));
        Assert.Contains(r.Store.LogLines(run.Id), l => l.Line == $"tell: {outcome} {workflow}  {text}");
    }

    [Fact]
    public async Task AMemberThatIsToldAndHandsBack_EndsTheWaitAsDone()
    {
        await using var r = await new Rig().InitAsync();
        var bot = await r.MemberAsync(r.WaitScript, new JsonObject { ["member"] = "Ada" });
        var parked = await r.RunAsync(bot);
        var workflow = parked.Run.WaitWorkflowId!.Value;

        r.InWorkflow(workflow, MessageTypes.Handback, new JsonObject { ["delivered"] = "Checked. {\"refund\": false}" }, "Alpha/Ada");
        await r.Runner.ResolveWaitsAsync(Ct);
        await r.Runner.WhenIdleAsync();

        Assert.Contains(r.Store.LogLines(parked.Run.Id), l => l.Line.StartsWith($"tell: done {workflow} false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReplyThatDoesNotMatchItsSchema_FailsTheStep()
    {
        await using var r = await new Rig().InitAsync();
        var (_, parked, workflow) = await ParkedAsync(r);

        r.InWorkflow(workflow, MessageTypes.WorkflowCompleted, new JsonObject { ["delivered"] = "Done, see the card." }, "Alpha/Manager");
        await r.Runner.ResolveWaitsAsync(Ct);
        await r.Runner.WhenIdleAsync();

        var run = r.Store.Run(parked.Run.Id)!;
        Assert.Equal((RunStatus.Failed, "a"), (run.Status, run.FailedStepId));
        Assert.Contains("does not end with JSON of Triage", run.Reason);
    }

    [Fact]
    public async Task AWaitingRun_ResumesAfterARestart()
    {
        await using var r = await new Rig().InitAsync();
        var (_, parked, workflow) = await ParkedAsync(r);
        await r.Runner.StopAsync();

        // The app starts again: a new runner over the same store. A waiting run is not an interrupted one.
        r.Runner = r.NewRunner();
        r.Bus.ScriptMembers = r.Runner;
        Assert.Empty(r.Runner.ResumeInterrupted());
        Assert.Equal(RunStatus.Waiting, r.Store.Run(parked.Run.Id)!.Status);

        r.InWorkflow(workflow, MessageTypes.WorkflowCompleted, new JsonObject { ["delivered"] = "{\"refund\": true}" }, "Alpha/Manager");
        Assert.Equal([parked.Run.Id], await r.Runner.ResolveWaitsAsync(Ct));
        await r.Runner.WhenIdleAsync();
        var run = r.Store.Run(parked.Run.Id)!;
        Assert.True(run.Status == RunStatus.Succeeded, Why(run));
    }

    [Fact]
    public async Task TheTellTimeout_EndsTheWaitAsTimedOut()
    {
        await using var r = await new Rig().InitAsync();
        var (_, parked, workflow) = await ParkedAsync(r);

        r.Clock.Now = T0.AddHours(1);
        Assert.Empty(await r.Runner.ResolveWaitsAsync(Ct));
        r.Clock.Now = T0.AddHours(2).AddSeconds(1); // the step's 2h
        Assert.Equal([parked.Run.Id], await r.Runner.ResolveWaitsAsync(Ct));
        await r.Runner.WhenIdleAsync();

        var run = r.Store.Run(parked.Run.Id)!;
        Assert.True(run.Status == RunStatus.Succeeded, Why(run));
        Assert.Contains(r.Store.LogLines(run.Id), l => l.Line == $"tell: timedOut {workflow}  ");
        Assert.Contains(r.Store.LogLines(run.Id), l => l.Line == "the wait timed out: the run goes on");
    }

    [Fact]
    public async Task WhileARunWaits_TheMembersTriggersAreSkippedAndNoted()
    {
        await using var r = await new Rig().InitAsync();
        var (bot, parked, workflow) = await ParkedAsync(r);

        var fire = r.Runner.Start(bot, "trigger:9", Yawble.Core.Scripts.Running.ScriptPayloads.TriggerFired(9, "every", T0));

        Assert.False(fire.Started);
        Assert.Contains($"(script run {parked.Run.Id}) was waiting on workflow {workflow}", fire.Reason);
        var skipped = r.Store.Run(fire.RunId!.Value)!;
        Assert.Equal((RunStatus.Skipped, "trigger:9"), (skipped.Status, skipped.Trigger));
    }

    [Fact]
    public async Task CancellingAWaitingRun_EndsIt_AndLeavesTheWorkflowAlone()
    {
        await using var r = await new Rig().InitAsync();
        var (bot, parked, workflow) = await ParkedAsync(r);
        using (var s0 = r.Db.OpenStore()) Assert.True(s0.AllMessages().Count > 0);
        int before;
        using (var s1 = r.Db.OpenStore()) before = s1.AllMessages().Count(m => m.CorrelationId == workflow);

        var cancelled = await r.Rpc().DispatchAsync("scripts.cancelRun", new JsonObject { ["id"] = parked.Run.Id }, Ct);

        Assert.True(cancelled!["cancelled"]!.GetValue<bool>());
        var run = r.Store.Run(parked.Run.Id)!;
        Assert.Equal(RunStatus.Interrupted, run.Status);
        Assert.Contains($"workflow {workflow}; the workflow goes on", run.Reason);
        using (var s2 = r.Db.OpenStore())
        {
            Assert.Equal(before, s2.AllMessages().Count(m => m.CorrelationId == workflow)); // nothing told, nothing closed
            Assert.DoesNotContain(s2.AllMessages(), m => m.Type == MessageTypes.WorkflowClosed);
        }

        r.InWorkflow(workflow, MessageTypes.WorkflowCompleted, new JsonObject { ["delivered"] = "{\"refund\": true}" }, "Alpha/Manager");
        Assert.Empty(await r.Runner.ResolveWaitsAsync(Ct)); // a cancelled run does not come back
        Assert.False((await r.Rpc().DispatchAsync("scripts.cancelRun", new JsonObject { ["id"] = parked.Run.Id }, Ct))!["cancelled"]!.GetValue<bool>());
        Assert.True(r.Runner.Start(bot, "run now", null).Started); // the member runs again
    }

    // ------------------------------------------------------------------ Test run

    [Fact]
    public async Task ATestRun_NeverTells_AndTakesAMadeUpReply()
    {
        await using var r = await new Rig().InitAsync();

        var outcome = await r.Runner.TestRunAsync(new ScriptTestRun(r.WaitScript, null, Yawble.Core.Scripts.Running.ScriptPayloads.TriggerFired(1, "once", T0), null, null,
            r.Alpha, DryRun: false, TellReplies: new JsonObject { ["a"] = "Made up. {\"refund\": true}" }), Ct);

        Assert.True(outcome.Succeeded, Why(outcome.Run));
        Assert.Contains("would tell Manager of Alpha: Look at order 7.", Logged(outcome));
        Assert.Contains("tell: dryRun 0 true Made up. {\"refund\": true}", Logged(outcome));
        using var s = r.Db.OpenStore();
        Assert.DoesNotContain(s.AllMessages(), m => m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTestRunRpc_PassesTellReplies()
    {
        await using var r = await new Rig().InitAsync();
        var result = await r.Rpc().DispatchAsync("scripts.testRun", new JsonObject
        {
            ["script"] = "tell-wait", ["teamId"] = r.Alpha, ["input"] = Yawble.Core.Scripts.Running.ScriptPayloads.TriggerFired(1, "once", T0),
            ["tellReplies"] = new JsonObject { ["a"] = new JsonObject { ["refund"] = false } },
        }, Ct);
        Assert.Equal("succeeded", result!["status"]!.GetValue<string>());
        Assert.Contains(result["log"]!.AsArray(), l => l!.GetValue<string>().StartsWith("tell: dryRun 0 false", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the RPC the shell reads

    [Fact]
    public async Task TheCatalog_ListsTellTargets_OwnTeamFirst_AgentMembersOnly_ManagerFirst_AndFollowsTheSwitch()
    {
        await using var r = await new Rig().InitAsync();
        await r.MemberAsync(r.GoScript); // a script member is no tell target
        var rpc = r.Rpc();

        var catalog = (await rpc.DispatchAsync("scripts.catalog", new JsonObject { ["teamId"] = r.Beta }, Ct))!.AsObject();
        Assert.True(catalog["tellOtherTeams"]!.GetValue<bool>());
        var targets = catalog["tellTargets"]!.AsArray().Select(t => (t!["teamId"]!.GetValue<long>(), t["team"]!.GetValue<string>(),
            string.Join(",", t["members"]!.AsArray().Select(m => m!.GetValue<string>())))).ToList();
        Assert.Equal([(r.Beta, "Beta", "Manager"), (r.Alpha, "Alpha", "Manager,Ada")], targets);

        Assert.False((await rpc.DispatchAsync("scripts.setSettings", new JsonObject { ["tellOtherTeams"] = false }, Ct))!["tellOtherTeams"]!.GetValue<bool>());
        Assert.False((await rpc.DispatchAsync("scripts.settings", null, Ct))!["tellOtherTeams"]!.GetValue<bool>());
        catalog = (await rpc.DispatchAsync("scripts.catalog", new JsonObject { ["teamId"] = r.Alpha }, Ct))!.AsObject();
        Assert.False(catalog["tellOtherTeams"]!.GetValue<bool>());
        Assert.Equal(["Alpha"], catalog["tellTargets"]!.AsArray().Select(t => t!["team"]!.GetValue<string>()));

        var withoutTeam = (await rpc.DispatchAsync("scripts.catalog", null, Ct))!.AsObject();
        Assert.Null(withoutTeam["tellTargets"]);
        await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("scripts.setSettings", new JsonObject { ["tellOtherTeams"] = "no" }, Ct));
        Assert.Contains("scripts.settings", ScriptsRpc.Reads);
        Assert.Contains("scripts.setSettings", ScriptsRpc.Changes);
        Assert.Contains("scripts.cancelRun", ScriptsRpc.Changes);
    }

    [Fact]
    public async Task MaxTells_IsAMemberSetting_ZeroToFifty()
    {
        await using var r = await new Rig().InitAsync();
        var bot = await r.MemberAsync(r.GoScript);
        var rpc = r.Rpc();

        Assert.Equal(5, (await rpc.DispatchAsync("scripts.memberGet", new JsonObject { ["memberId"] = bot }, Ct))!["maxTells"]!.GetValue<int>());
        Assert.Equal(12, (await rpc.DispatchAsync("scripts.memberUpdate", new JsonObject { ["memberId"] = bot, ["maxTells"] = 12 }, Ct))!["maxTells"]!.GetValue<int>());
        Assert.Equal(12, r.Store.Member(bot)!.MaxTells);
        Assert.Equal(12, (await rpc.DispatchAsync("scripts.memberUpdate", new JsonObject { ["memberId"] = bot, ["timeoutMinutes"] = 5 }, Ct))!["maxTells"]!.GetValue<int>());
        var e = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("scripts.memberUpdate", new JsonObject { ["memberId"] = bot, ["maxTells"] = 51 }, Ct));
        Assert.Contains("maxTells is 0 to 50", e.Message);
    }

    [Fact]
    public async Task ScriptCheckAndSave_AcceptTheTellStep()
    {
        await using var r = await new Rig().InitAsync();
        var rpc = r.Rpc();
        var check = (await rpc.DispatchAsync("scripts.check", new JsonObject { ["document"] = TellScript("tell-check", wait: true) }, Ct))!;
        Assert.True(check["ok"]!.GetValue<bool>(), check.ToJsonString());
        var saved = (await rpc.DispatchAsync("scripts.save", new JsonObject { ["document"] = TellScript("tell-check", wait: true) }, Ct))!;
        Assert.True(saved["ok"]!.GetValue<bool>(), saved.ToJsonString());

        var bad = TellScript("tell-bad", wait: false).Replace("\"into\": \"t\"", "\"reply\": \"Triage\", \"into\": \"t\"", StringComparison.Ordinal);
        var badCheck = (await rpc.DispatchAsync("scripts.check", new JsonObject { ["document"] = bad }, Ct))!;
        Assert.False(badCheck["ok"]!.GetValue<bool>());
        Assert.Contains("a reply goes with wait: true", badCheck.ToJsonString());
    }
}
