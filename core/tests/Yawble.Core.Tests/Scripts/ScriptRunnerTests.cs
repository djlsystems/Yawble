using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;

namespace Yawble.Core.Tests.Scripts;

/// <summary>
/// Card 584 (B131): running script members. Dispatch (agents untouched), structured trigger payloads with no workflow per
/// fire, one run at a time, saved variables only on success, three failures one decision, resume after a restart, Test runs.
/// </summary>
public class ScriptRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World : IAsyncDisposable
    {
        public required TempDb Db { get; init; }
        public required BusEngine Bus { get; init; }
        public required ScriptLibrary Library { get; init; }
        public required ScriptRunStore Store { get; init; }
        public required ScriptRunner Runner { get; set; }
        public required FakeEngine Engine { get; init; }
        public required FakeDecisions Decisions { get; init; }
        public required FakeActionCatalog Actions { get; init; }
        public required ScriptedRunner Agents { get; init; }
        public long TeamId { get; init; }
        public long ScriptId { get; set; }

        public ScriptRunner NewRunner() => new(Store, Library, Actions, Engine, Path.Combine(Db.Dir, "scripts")) { Decisions = Decisions };

        public async Task<long> ScriptMemberAsync(string name, JsonObject? settings = null)
        {
            var row = await Bus.AddScriptMemberAsync(TeamId, name, Ct);
            Store.SetScriptMember(row.Id, ScriptId, null, settings ?? [], [], null);
            return row.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Runner.StopAsync();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static async Task<World> WorldAsync(bool start = true)
    {
        var db = new TempDb();
        var agents = new ScriptedRunner();
        ScriptRunner? runner = null;
        var bus = new BusEngine(db.OpenStore(), new ScriptDispatchRunner(agents, () => runner), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) });
        var teamId = await bus.CreateTeamAsync("Alpha", ct: Ct);
        await bus.AddMemberAsync("Alpha", "Ada", ct: Ct);
        var library = new ScriptLibrary(db.ConnectionString, () => new FakeActionCatalog(), FakeChecker.Check);
        var store = new ScriptRunStore(db.ConnectionString);
        var engine = new FakeEngine();
        var decisions = new FakeDecisions();
        var actions = new FakeActionCatalog();
        runner = new ScriptRunner(store, library, actions, engine, Path.Combine(db.Dir, "scripts")) { Decisions = decisions };
        bus.ScriptMembers = runner;
        var world = new World
        {
            Db = db, Bus = bus, Library = library, Store = store, Runner = runner, Engine = engine, Decisions = decisions, Actions = actions,
            Agents = agents, TeamId = teamId,
        };
        world.ScriptId = library.Save(Docs.Clean()).Id;
        if (start) await bus.StartAsync(Ct);
        return world;
    }

    private static TriggerDraft Every(long member) =>
        new(null, member, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Poll.", true, true);

    // ------------------------------------------------------------------ dispatch and triggers

    [Fact]
    public async Task ATriggerAimedAtAScriptMember_RunsItsScriptWithTriggerFired_AndStartsNoWorkflow()
    {
        await using var w = await WorldAsync();
        var bot = await w.ScriptMemberAsync("Poller");
        var t = await w.Bus.SaveTriggerAsync(Every(bot), Ct);
        var before = w.Db.OpenStore().AllMessages().Count;
        await w.Bus.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);
        await w.Runner.WhenIdleAsync();

        var run = Assert.Single(w.Store.RecentRuns(w.TeamId, bot, 10));
        Assert.Equal((ScriptRunStatus.Succeeded, $"trigger:{t.Id}", false), (run.Status, run.Trigger, run.Test));
        Assert.Equal(t.Id, run.Input!["triggerId"]!.GetValue<long>());
        Assert.Equal("every", run.Input["kind"]!.GetValue<string>());
        Assert.NotNull(run.Input["firedAt"]);
        Assert.Equal(["a", "b", "c"], w.Store.Steps(run.Id).Select(s => s.StepId));
        Assert.Equal("INBOX", w.Engine.Requests.Single().Settings!["folder"]!.GetValue<string>()); // the declared default

        using var store = w.Db.OpenStore();
        Assert.Equal(before, store.AllMessages().Count); // no instruction, no workflow, no Manager woken
        Assert.Equal("fired", store.Trigger(t.Id)!.LastOutcome);
        Assert.Empty(w.Agents.Admitted);
        Assert.Contains(new Yawble.Core.Launch.RunRecordStore(w.Db.ConnectionString).All(), r => r.Kind == "script" && r.Member == "Poller" && r.EndClass == "completed");
    }

    [Fact]
    public async Task AnAgentsTriggerIsToldExactlyAsBefore_AndItsRunGoesToTheAgentRunner()
    {
        await using var w = await WorldAsync();
        await w.ScriptMemberAsync("Poller");
        using var s0 = w.Db.OpenStore();
        var ada = s0.Member("Alpha", "Ada")!;
        var t = await w.Bus.SaveTriggerAsync(Every(ada.Id), Ct);
        await w.Bus.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);
        using var store = w.Db.OpenStore();
        var m = store.Find(store.Trigger(t.Id)!.LastSeq!.Value)!;
        Assert.Equal(MessageTypes.Instruction(ada), m.Type);
        Assert.StartsWith("Scheduled work (trigger \"poll\"", Payload.Field(m, "text"));
        await Wait.UntilAsync(() => w.Agents.Admitted.Count == 1, "Ada's run on the agent runner");
        Assert.Empty(w.Store.RecentRuns(null, null, 10));
    }

    [Fact]
    public async Task AnEventTriggerGivesTheScriptMemberEventItem_Unflattened()
    {
        await using var w = await WorldAsync();
        var bot = await w.ScriptMemberAsync("Poller");
        await w.Bus.SaveTriggerAsync(new TriggerDraft(null, bot, "on pr", new TriggerSchedule(TriggerKinds.Event, null, null, null, null), "Look.", true, true,
            EventName: "web.pr"), Ct);
        var item = new JsonObject { ["number"] = 7, ["author"] = new JsonObject { ["login"] = "someone" } };
        Assert.Equal(1, await w.Bus.PublishEventAsync(w.TeamId, "web.pr", item, "watch", Ct));
        await w.Runner.WhenIdleAsync();
        var run = Assert.Single(w.Store.RecentRuns(w.TeamId, bot, 10));
        Assert.Equal("web.pr", run.Input!["event"]!.GetValue<string>());
        Assert.Equal("someone", run.Input["item"]!["author"]!["login"]!.GetValue<string>());
    }

    [Fact]
    public void FolderChangePayloadHasRootAndTheThreeLists()
    {
        var p = ScriptPayloads.FolderChange("C:/in", ["a.pdf"], [], ["b.pdf"]);
        Assert.Equal("""{"root":"C:/in","added":["a.pdf"],"removed":[],"changed":["b.pdf"]}""", p.ToJsonString());
        Assert.Equal("""{"text":"hi"}""", ScriptPayloads.Tell("hi").ToJsonString());
    }

    [Fact]
    public async Task ATellToAScriptMemberRunsItsScriptWithText_ThroughTheDispatchingRunner()
    {
        await using var w = await WorldAsync();
        var bot = await w.ScriptMemberAsync("Poller");
        await w.Bus.PersonTellAsync("Alpha", "Poller", "run once please", ct: Ct);
        await Wait.UntilAsync(() => w.Store.RecentRuns(null, bot, 1).FirstOrDefault()?.Status == ScriptRunStatus.Succeeded, "the tell's script run");
        var run = w.Store.RecentRuns(null, bot, 1).Single();
        Assert.Equal("tell", run.Trigger);
        Assert.Contains("run once please", run.Input!["text"]!.GetValue<string>());
        Assert.DoesNotContain(w.Agents.Admitted, a => a.Member.Id == bot); // never an agent CLI (Ada's own runs may race in)
    }

    // ------------------------------------------------------------------ one at a time, saved variables, failures

    [Fact]
    public async Task AFireWhileARunIsGoingIsSkipped_AndNoted()
    {
        await using var w = await WorldAsync();
        var bot = await w.ScriptMemberAsync("Poller");
        w.Engine.HoldAt = "b";
        var t = await w.Bus.SaveTriggerAsync(Every(bot), Ct);
        await w.Bus.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);
        await w.Engine.Holding.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.True(w.Runner.IsRunning(bot));

        await w.Bus.RunTriggerNowAsync(t.Id, Ct);
        using (var store = w.Db.OpenStore())
        {
            var row = store.Trigger(t.Id)!;
            Assert.Equal("skipped", row.LastOutcome);
            Assert.Contains("one run at a time", row.LastReason);
        }
        var skipped = w.Store.RecentRuns(null, bot, 10).First();
        Assert.Equal((ScriptRunStatus.Skipped, $"trigger:{t.Id}"), (skipped.Status, skipped.Trigger));

        w.Engine.Gate.SetResult();
        await w.Runner.WhenIdleAsync();
        Assert.Equal([ScriptRunStatus.Skipped, ScriptRunStatus.Succeeded], w.Store.RecentRuns(null, bot, 10).Select(r => r.Status));
        Assert.Single(w.Engine.Requests);
    }

    [Fact]
    public async Task TheSameScriptRunsAtOnceAsMembersOfTwoTeams_EachWithItsOwnState_AndASaveDuringTheRunsChangesNone()
    {
        await using var w = await WorldAsync(start: false);
        var betaId = await w.Bus.CreateTeamAsync("Beta", ct: Ct);
        async Task<long> Add(long team, string name, string folder, string binding)
        {
            var row = await w.Bus.AddScriptMemberAsync(team, name, Ct);
            w.Store.SetScriptMember(row.Id, w.ScriptId, null, new JsonObject { ["folder"] = folder }, new JsonObject { ["mail"] = binding }, null);
            return row.Id;
        }
        // The same script as a member of Alpha, a second member of Alpha, and a member (same name) of Beta.
        var a1 = await Add(w.TeamId, "Poller", "A1", "alpha-mail");
        var a2 = await Add(w.TeamId, "Poller2", "A2", "alpha-mail-2");
        var b1 = await Add(betaId, "Poller", "B1", "beta-mail");
        (long Id, long Team, string Folder, string Binding)[] members = [(a1, w.TeamId, "A1", "alpha-mail"), (a2, w.TeamId, "A2", "alpha-mail-2"), (b1, betaId, "B1", "beta-mail")];

        // Phase 1: all three overlap at step b; none is skipped.
        w.Engine.KeepsLastSeen = true;
        w.Engine.HoldAt = "b";
        var started = members.Select(m => w.Runner.Start(m.Id, "trigger:1", null)).ToList();
        Assert.All(started, s => Assert.True(s.Started, s.Reason));
        await Wait.UntilAsync(() => w.Engine.Held == 3, "all three runs going at once");
        Assert.All(members, m => Assert.True(w.Runner.IsRunning(m.Id)));

        // A save of a new version while they run, and a second fire at one member: only that member's fire is skipped.
        Assert.Equal(2, w.Library.Save(Docs.Clean(description: "Version two.")).Version);
        var again = w.Runner.Start(a1, "trigger:2", null);
        Assert.False(again.Started);
        Assert.Equal(3, w.Engine.Held);

        w.Engine.Gate.SetResult();
        await w.Runner.WhenIdleAsync();
        foreach (var (m, s) in members.Zip(started))
        {
            var run = w.Store.Run(s.RunId!.Value)!;
            Assert.Equal((ScriptRunStatus.Succeeded, m.Team, (long?)m.Id, 1), (run.Status, run.TeamId, run.MemberId, run.Version));
            Assert.Equal(m.Folder, run.Settings!["folder"]!.GetValue<string>());
            Assert.Equal(m.Binding, run.Bindings!["mail"]!.GetValue<string>());
            Assert.Equal($"{m.Folder}:1", w.Store.State(m.Id).Variables["lastSeen"]!.GetValue<string>());
            Assert.Equal([$"{m.Folder} Polls and logs."], File.ReadAllLines(Path.Combine(w.Runner.MemberFolder(m.Id), "run.log")));
            var caller = w.Actions.Callers.Single(c => c.RunId == run.Id);
            Assert.Equal((m.Team, (long?)m.Id, m.Binding), (caller.TeamId, caller.MemberId, caller.Bindings["mail"]));
            Assert.Equal(ScriptRunStatus.Succeeded, w.Store.RecentRuns(null, m.Id, 10).Single(r => r.Id == run.Id).Status);
        }
        Assert.Equal(3, members.Select(m => w.Runner.MemberFolder(m.Id)).Distinct().Count());
        Assert.All(w.Engine.Requests, r => Assert.Equal("Polls and logs.", FakeEngine.DocDescription(r.Document)));
        Assert.Equal([ScriptRunStatus.Skipped, ScriptRunStatus.Succeeded], w.Store.RecentRuns(null, a1, 10).Select(r => r.Status));
        Assert.Single(w.Store.RecentRuns(null, a2, 10));
        Assert.Single(w.Store.RecentRuns(betaId, b1, 10));

        // Phase 2: runs on version 2 are cut off by a restart, version 3 is saved meanwhile (and a2 is pinned to 1); the
        // resumed runs use the version each started with, not the newest one nor the member's new pin.
        w.Engine.HoldAt = "c";
        w.Engine.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Engine.Holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = members.Select(m => w.Runner.Start(m.Id, "trigger:3", null).RunId!.Value).ToList();
        await Wait.UntilAsync(() => w.Engine.Held == 3, "all three version 2 runs going");
        await w.Runner.StopAsync();
        Assert.Equal(3, w.Library.Save(Docs.Clean(description: "Version three.")).Version);
        w.Store.SetScriptMember(a2, w.ScriptId, 1, new JsonObject { ["folder"] = "A2" }, new JsonObject { ["mail"] = "alpha-mail-2" }, null);
        Assert.All(cut, id => Assert.Equal((ScriptRunStatus.Running, 2), (w.Store.Run(id)!.Status, w.Store.Run(id)!.Version)));

        w.Engine.HoldAt = null;
        var before = w.Engine.Requests.Count;
        w.Runner = w.NewRunner();
        Assert.Equal(cut.Order(), w.Runner.ResumeInterrupted().Order());
        await w.Runner.WhenIdleAsync();
        var resumed = w.Engine.Requests.Skip(before).ToList();
        Assert.Equal(3, resumed.Count);
        Assert.All(resumed, r => Assert.Equal("Version two.", FakeEngine.DocDescription(r.Document)));
        foreach (var (m, id) in members.Zip(cut))
        {
            Assert.Equal((ScriptRunStatus.Succeeded, 2), (w.Store.Run(id)!.Status, w.Store.Run(id)!.Version));
            Assert.Equal($"{m.Folder}:2", w.Store.State(m.Id).Variables["lastSeen"]!.GetValue<string>());
            Assert.Equal([$"{m.Folder} Polls and logs.", $"{m.Folder} Version two."], File.ReadAllLines(Path.Combine(w.Runner.MemberFolder(m.Id), "run.log")));
        }
    }

    [Fact]
    public async Task SavedVariablesMoveOnlyWhenARunSucceeds()
    {
        await using var w = await WorldAsync(start: false);
        var bot = await w.ScriptMemberAsync("Poller");
        Assert.True((await w.Runner.RunMemberAsync(bot, "test-ok", null, Ct)).Succeeded);
        Assert.Equal(1, w.Store.State(bot).Variables["runs"]!.GetValue<int>());

        w.Engine.FailAt = "b";
        var failed = await w.Runner.RunMemberAsync(bot, "test-fail", null, Ct);
        Assert.Equal((ScriptRunStatus.Failed, "b"), (failed.Run.Status, failed.Run.FailedStepId));
        Assert.Contains("step b failed", failed.Run.Reason);
        Assert.Equal(1, w.Store.State(bot).Variables["runs"]!.GetValue<int>()); // untouched by the failed run
        Assert.Equal(1, w.Engine.Requests[1].SavedVariables!["runs"]!.GetValue<int>());

        Assert.True(w.Store.ResetVariable(bot, "runs"));
        Assert.False(w.Store.ResetVariable(bot, "runs"));
        Assert.False(w.Store.State(bot).Variables.ContainsKey("runs"));
    }

    [Fact]
    public async Task ThreeFailuresInARowRaiseOneDecision_NotOnePerRun_AndASuccessStartsTheCountAgain()
    {
        await using var w = await WorldAsync(start: false);
        var bot = await w.ScriptMemberAsync("Poller");
        w.Engine.FailAt = "a";
        for (var i = 0; i < 2; i++) await w.Runner.RunMemberAsync(bot, "t", null, Ct);
        Assert.Empty(w.Decisions.Opened);
        for (var i = 0; i < 4; i++) await w.Runner.RunMemberAsync(bot, "t", null, Ct);
        var (member, summary) = Assert.Single(w.Decisions.Opened);
        Assert.Equal(bot, member);
        Assert.Contains("failed 3 runs in a row", summary);
        Assert.Equal(6, w.Store.State(bot).ConsecutiveFailures);

        w.Engine.FailAt = null;
        await w.Runner.RunMemberAsync(bot, "t", null, Ct);
        Assert.Equal((0, (long?)null), (w.Store.State(bot).ConsecutiveFailures, w.Store.State(bot).FailureDecisionSeq));
        w.Engine.FailAt = "a";
        for (var i = 0; i < 3; i++) await w.Runner.RunMemberAsync(bot, "t", null, Ct);
        Assert.Equal(2, w.Decisions.Opened.Count);
    }

    [Fact]
    public async Task TheBusDecisionComesFromTheMember_InNeedsYou_AndItsAnswerRunsNoScript()
    {
        await using var w = await WorldAsync();
        var bot = await w.ScriptMemberAsync("Poller");
        w.Runner.Decisions = new BusScriptDecisions(w.Bus);
        w.Engine.FailAt = "a";
        for (var i = 0; i < 3; i++) await w.Runner.RunMemberAsync(bot, "t", null, Ct);
        var open = Assert.Single(await w.Bus.OpenDecisionsAsync("Alpha", Ct));
        Assert.Equal("Alpha/Poller", open.Source);
        Assert.Equal(open.Seq, w.Store.State(bot).FailureDecisionSeq);
        var runs = w.Engine.Requests.Count;

        await w.Bus.PersonTellAsync("Alpha", "Poller", "Keep running", open.Seq, Ct);
        await Wait.UntilAsync(() => w.Store.State(bot).ConsecutiveFailures == 0, "the answer clears the count");
        Assert.Equal(runs, w.Engine.Requests.Count);
    }

    [Fact]
    public async Task AMemberOnLatestRunsTheNewestCleanVersion_NeverADraft()
    {
        await using var w = await WorldAsync(start: false);
        var bot = await w.ScriptMemberAsync("Poller");
        w.Library.Save(Docs.Draft());
        var outcome = await w.Runner.RunMemberAsync(bot, "t", null, Ct);
        Assert.Equal(1, outcome.Run.Version);
        Assert.True(outcome.Succeeded);

        // Pinned to the draft: refused at run time too.
        w.Store.SetScriptMember(bot, w.ScriptId, 2, [], [], null);
        var pinned = await w.Runner.RunMemberAsync(bot, "t", null, Ct);
        Assert.Equal(ScriptRunStatus.Failed, pinned.Run.Status);
        Assert.Contains("draft", pinned.Run.Reason);
    }

    [Fact]
    public async Task TheMembersTimeoutAndLimitsReachTheEngine()
    {
        await using var w = await WorldAsync(start: false);
        var bot = await w.ScriptMemberAsync("Poller");
        await w.Runner.RunMemberAsync(bot, "t", null, Ct);
        w.Store.SetScriptMember(bot, w.ScriptId, null, [], [], 3);
        await w.Runner.RunMemberAsync(bot, "t", null, Ct);
        Assert.Equal(TimeSpan.FromMinutes(10), w.Engine.Requests[0].Timeout);
        Assert.Equal(TimeSpan.FromMinutes(3), w.Engine.Requests[1].Timeout);
        Assert.Equal(10_000, w.Engine.Requests[0].StepBudget);
        Assert.False(w.Engine.Requests[0].DryRun);
        Assert.Equal(w.Runner.MemberFolder(bot), w.Engine.Requests[0].DataFolder);
        var caller = w.Actions.Callers.Last();
        Assert.Equal((w.TeamId, (long?)bot, "Poller"), (caller.TeamId, caller.MemberId, caller.Member));
    }

    // ------------------------------------------------------------------ restart

    [Fact]
    public async Task ARunCutOffByARestartResumesFromTheLastCompletedStep_InTheSameRun()
    {
        await using var w = await WorldAsync(start: false);
        var bot = await w.ScriptMemberAsync("Poller");
        w.Engine.HoldAt = "c";
        var started = w.Runner.Start(bot, "trigger:1", new JsonObject { ["triggerId"] = 1 });
        await w.Engine.Holding.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await w.Runner.StopAsync(); // the app stops: the run is left running, a and b journaled
        var cut = w.Store.Run(started.RunId!.Value)!;
        Assert.Equal(ScriptRunStatus.Running, cut.Status);
        Assert.NotNull(cut.Checkpoint);

        // The next start: a new runner over the same database.
        w.Engine.HoldAt = null;
        w.Runner = w.NewRunner();
        Assert.Equal([cut.Id], w.Runner.ResumeInterrupted());
        await w.Runner.WhenIdleAsync();
        var done = w.Store.Run(cut.Id)!;
        Assert.Equal(ScriptRunStatus.Succeeded, done.Status);
        Assert.Equal("c", Seam.NextOf(w.Engine.Requests.Last().ResumeFrom!));
        Assert.Equal(1, w.Engine.Requests.Last().Input!["triggerId"]!.GetValue<int>());
        Assert.Equal(["a", "b", "c"], w.Store.Steps(cut.Id).Where(s => s.Status == "succeeded").Select(s => s.StepId));
        Assert.Contains(w.Store.LogLines(cut.Id), l => l.Line.Contains("resumed after a restart"));
        Assert.Equal(1, w.Store.State(bot).Variables["runs"]!.GetValue<int>());
    }

    [Fact]
    public async Task ATestRunLeftByARestartIsInterrupted_NotResumed()
    {
        await using var w = await WorldAsync(start: false);
        w.Engine.HoldAt = "b";
        var test = w.Runner.TestRunAsync(new ScriptTestRun(w.ScriptId, null, null, null, null, w.TeamId), Ct);
        await w.Engine.Holding.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await w.Runner.StopAsync();
        Assert.Equal(ScriptRunStatus.Running, (await test).Run.Status); // cut off, left for the next start
        w.Runner = w.NewRunner();
        Assert.Empty(w.Runner.ResumeInterrupted());
        Assert.Equal(ScriptRunStatus.Interrupted, w.Store.RecentRuns(null, null, 1).Single().Status);
    }

    // ------------------------------------------------------------------ Test run

    [Fact]
    public async Task ATestRunIsADryRunByDefault_RecordedAsATest_WithNoSavedVariables()
    {
        await using var w = await WorldAsync(start: false);
        var bot = await w.ScriptMemberAsync("Poller");
        var o = await w.Runner.TestRunAsync(new ScriptTestRun(w.ScriptId, null, new JsonObject { ["text"] = "x" }, new JsonObject { ["folder"] = "Sent" }, null, w.TeamId), Ct);
        Assert.True(o.Succeeded);
        Assert.True(o.Run.Test);
        Assert.True(o.Run.DryRun);
        Assert.Null(o.Run.MemberId);
        Assert.Equal(3, o.Steps.Count);
        Assert.Equal(["did a", "did b", "did c"], o.Log.Select(l => l.Line));
        var req = w.Engine.Requests.Single();
        Assert.True(req.DryRun);
        Assert.NotNull(req.DryRunFolder);
        Assert.Equal("Sent", req.Settings!["folder"]!.GetValue<string>());
        Assert.Empty(req.SavedVariables!);
        Assert.False(w.Store.State(bot).Variables.ContainsKey("runs"));

        // A draft can be tried in a Test run.
        var draft = w.Library.Save(Docs.Draft());
        Assert.Equal(draft.Version, (await w.Runner.TestRunAsync(new ScriptTestRun(w.ScriptId, draft.Version, null, null, null, w.TeamId), Ct)).Run.Version);
    }

    [Fact]
    public void StepFieldsAreScrubbedAndTruncated()
    {
        var big = new JsonObject { ["text"] = new string('x', ScriptRunStore.MaxField + 10) };
        var (text, truncated) = ScriptRunStore.Field(big);
        Assert.True(truncated);
        Assert.True(text!.Length <= ScriptRunStore.MaxField + 32); // the cut text as a JSON string
        Assert.Equal((null, false), ScriptRunStore.Field(null));
        Assert.Equal(("""{"a":1}""", false), ScriptRunStore.Field(new JsonObject { ["a"] = 1 }));
    }

    [Fact]
    public void SettingsAndBindingsAreCheckedAgainstTheScript()
    {
        var doc = JsonNode.Parse(Docs.Clean(requires: new JsonArray(new JsonObject { ["name"] = "mail", ["kind"] = "email", ["actions"] = new JsonArray("list") })))!.AsObject();
        Assert.Empty(ScriptSettings.Problems(doc, [], new JsonObject { ["mail"] = "Gmail" }));
        var problems = ScriptSettings.Problems(doc, new JsonObject { ["folder"] = 3, ["nope"] = "x" }, new JsonObject { ["sms"] = "Twilio" });
        Assert.Contains("setting 'folder' must be a string", problems);
        Assert.Contains("the script has no setting 'nope'", problems);
        Assert.Contains("the script requires no 'sms' to bind", problems);
        Assert.Contains("'mail' needs a binding to one of the team's integrations", problems);
    }
}
