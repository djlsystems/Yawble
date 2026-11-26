using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Tests.Workflows.Engine;

/// <summary>
/// The tell step in the engine, with a fake teller and a fake clock: sending without waiting, parking on the deliverable and going on
/// when it ends (done with a checked reply, failed, blocked, the timeout), the tell cap, dry run and made-up replies, and the run
/// timeout that stops while the run waits.
/// </summary>
public sealed class WorkflowTellEngineTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-24T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private readonly TempFolder _temp = new();
    public void Dispose() => _temp.Dispose();

    private sealed class FakeTeller : IWorkflowTeller
    {
        public List<WorkflowTellCall> Calls { get; } = [];
        public long NextDeliverable { get; set; } = 41;
        public string? Refuse { get; set; }

        public Task<WorkflowTellSent> TellAsync(WorkflowTellCall call, CancellationToken ct)
        {
            if (Refuse is { } reason) throw new WorkflowActionException(reason);
            Calls.Add(call);
            return Task.FromResult(new WorkflowTellSent(++NextDeliverable, call.Member, call.Team ?? "Support"));
        }
    }

    private static readonly WorkflowSchema TriageResult = WorkflowSchema.Parse("TriageResult", """
        { "type": "object", "properties": { "refund": { "type": "boolean" } }, "required": ["refund"] }
        """);

    private const string Vars = """[{ "name": "t", "type": "TellResult" }, { "name": "after", "type": "string", "initial": "" }]""";

    private static WorkflowDocument Doc(string tell) => WorkflowSamples.Workflow($$"""
        [ {{tell}},
          { "id": "b", "type": "set", "var": "after", "value": "{vars.t.outcome}:{vars.t.deliverable}", "next": null } ]
        """, Vars);

    private WorkflowRunRequest Request(WorkflowDocument doc, FakeTeller teller, FakeClock clock) => new()
    {
        Document = doc, Actions = new FakeActions(), DataFolder = _temp.Sub("d"), Teller = teller, Team = "Support", Clock = clock,
        Schemas = new WorkflowSchemaSet([TriageResult]),
    };

    private static JsonObject T(WorkflowRunCheckpoint cp) => cp.Vars["t"]!.AsObject();

    [Fact]
    public async Task ATellThatDoesNotWait_SendsOnce_AndIntoGetsSentWithTheDeliverable()
    {
        var teller = new FakeTeller();
        var journal = new RecordingJournal();
        var result = await WorkflowEngine.RunAsync(Request(Doc("""{ "id": "a", "type": "tell", "text": "Look at {vars.after}order 7.", "into": "t", "next": "b" }"""),
            teller, new FakeClock(T0)), journal, Ct);

        Assert.Equal(WorkflowRunStatus.Succeeded, result.Status);
        var call = Assert.Single(teller.Calls);
        Assert.Equal(("a", "Manager", (string?)null, "Look at order 7.", false, (string?)null), (call.StepId, call.Member, call.Team, call.Text, call.Wait, call.ReplySchemaName));
        var t = T(journal.LastCheckpoint);
        Assert.Equal(("sent", 42L, "", true), (t["outcome"]!.GetValue<string>(), t["deliverable"]!.GetValue<long>(), t["reply"]!.GetValue<string>(), t["data"] is null));
        Assert.Equal("sent:42", journal.LastCheckpoint.Vars["after"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATellThatWaits_ParksTheRunAtTheTell_AndGoesOnWhenTheDeliverableIsDone_WithTheCheckedReply()
    {
        var teller = new FakeTeller();
        var clock = new FakeClock(T0);
        var doc = Doc("""{ "id": "a", "type": "tell", "member": "Ava", "team": "Support", "text": "Triage it.", "wait": true, "timeout": "4h", "reply": "TriageResult", "into": "t", "next": "b" }""");
        var journal = new RecordingJournal();
        var parked = await WorkflowEngine.RunAsync(Request(doc, teller, clock), journal, Ct);

        Assert.Equal(WorkflowRunStatus.Waiting, parked.Status);
        Assert.Equal(("deliverable", "a", 42L, T0.AddHours(4)), (parked.Wait!.Kind, parked.Wait.StepId, parked.Wait.Deliverable!.Value, parked.Wait.Until!.Value));
        var call = Assert.Single(teller.Calls);
        Assert.Equal(("Ava", "Support", true, "TriageResult"), (call.Member, call.Team, call.Wait, call.ReplySchemaName));
        Assert.NotNull(call.ReplySchema?["properties"]?["refund"]);
        var waiting = journal.Completed[^1];
        Assert.Equal(("a", "waiting"), (waiting.Record.StepId, waiting.Record.Status));
        var cp = WorkflowRunCheckpoint.FromJson(waiting.Checkpoint);
        Assert.Equal(("a", 1, 42L), (cp.Next, cp.Tells, cp.Wait!.Deliverable!.Value));

        // Still waiting: resumed without a result before the timeout, it stays parked and tells nothing again.
        clock.Now = T0.AddHours(1);
        var still = await WorkflowEngine.RunAsync(Request(doc, teller, clock) with { ResumeFrom = cp }, new RecordingJournal(), Ct);
        Assert.Equal(WorkflowRunStatus.Waiting, still.Status);
        Assert.Single(teller.Calls);

        clock.Now = T0.AddHours(2);
        var after = new RecordingJournal();
        var done = await WorkflowEngine.RunAsync(Request(doc, teller, clock) with
        {
            ResumeFrom = cp, WaitResult = new WorkflowWaitResult("done", "Read it: a refund.\n```json\n{ \"refund\": true }\n```"),
        }, after, Ct);

        Assert.Equal(WorkflowRunStatus.Succeeded, done.Status);
        Assert.Single(teller.Calls); // resuming never tells again
        Assert.Equal(["a", "b"], after.CompletedSteps);
        var t = T(after.LastCheckpoint);
        Assert.Equal(("done", 42L, true), (t["outcome"]!.GetValue<string>(), t["deliverable"]!.GetValue<long>(), t["data"]!["refund"]!.GetValue<bool>()));
        Assert.StartsWith("Read it: a refund.", t["reply"]!.GetValue<string>());
        Assert.Equal("done:42", after.LastCheckpoint.Vars["after"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("failed", "the deliverable was closed")]
    [InlineData("blocked", "needs the shop's password")]
    public async Task AWaitThatFailsOrIsBlocked_GoesOnWithThatOutcome_AndNoData(string outcome, string text)
    {
        var teller = new FakeTeller();
        var clock = new FakeClock(T0);
        var doc = Doc("""{ "id": "a", "type": "tell", "text": "x", "wait": true, "reply": "TriageResult", "into": "t", "next": "b" }""");
        var journal = new RecordingJournal();
        await WorkflowEngine.RunAsync(Request(doc, teller, clock), journal, Ct);
        var after = new RecordingJournal();
        var result = await WorkflowEngine.RunAsync(Request(doc, teller, clock) with { ResumeFrom = journal.LastCheckpoint, WaitResult = new WorkflowWaitResult(outcome, text) }, after, Ct);

        Assert.Equal(WorkflowRunStatus.Succeeded, result.Status);
        var t = T(after.LastCheckpoint);
        Assert.Equal((outcome, text, true), (t["outcome"]!.GetValue<string>(), t["reply"]!.GetValue<string>(), t["data"] is null));
    }

    [Fact]
    public async Task TheTimeout_EndsTheWaitAsTimedOut_EvenWithNoResult()
    {
        var teller = new FakeTeller();
        var clock = new FakeClock(T0);
        var doc = Doc("""{ "id": "a", "type": "tell", "text": "x", "wait": true, "into": "t", "next": "b" }""");
        var journal = new RecordingJournal();
        var parked = await WorkflowEngine.RunAsync(Request(doc, teller, clock), journal, Ct);
        Assert.Equal(T0.AddHours(24), parked.Wait!.Until); // the default timeout

        clock.Now = T0.AddHours(24);
        var after = new RecordingJournal();
        var result = await WorkflowEngine.RunAsync(Request(doc, teller, clock) with { ResumeFrom = journal.LastCheckpoint }, after, Ct);
        Assert.Equal(WorkflowRunStatus.Succeeded, result.Status);
        Assert.Equal("timedOut:42", after.LastCheckpoint.Vars["after"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Done, no JSON.", "does not end with JSON of TriageResult")]
    [InlineData("Done. {\"refund\": \"maybe\"}", "does not match TriageResult")]
    public async Task AReplyThatDoesNotMatchTheSchema_FailsTheStep(string reply, string reason)
    {
        var teller = new FakeTeller();
        var clock = new FakeClock(T0);
        var doc = Doc("""{ "id": "a", "type": "tell", "text": "x", "wait": true, "reply": "TriageResult", "into": "t", "next": "b" }""");
        var journal = new RecordingJournal();
        await WorkflowEngine.RunAsync(Request(doc, teller, clock), journal, Ct);
        var result = await WorkflowEngine.RunAsync(Request(doc, teller, clock) with { ResumeFrom = journal.LastCheckpoint, WaitResult = new WorkflowWaitResult("done", reply) },
            new RecordingJournal(), Ct);

        Assert.Equal((WorkflowRunStatus.Failed, "a"), (result.Status, result.FailedStepId));
        Assert.Contains(reason, result.Reason);
    }

    [Fact]
    public async Task TheRunTimeout_StopsWhileTheRunWaits()
    {
        var teller = new FakeTeller();
        var clock = new FakeClock(T0);
        var doc = Doc("""{ "id": "a", "type": "tell", "text": "x", "wait": true, "timeout": "2d", "into": "t", "next": "b" }""");
        var journal = new RecordingJournal();
        await WorkflowEngine.RunAsync(Request(doc, teller, clock) with { Timeout = TimeSpan.FromMinutes(10) }, journal, Ct);
        Assert.True(journal.LastCheckpoint.ActiveMs < TimeSpan.FromMinutes(10).TotalMilliseconds);

        clock.Now = T0.AddDays(1); // a day of waiting is far past the 10-minute run timeout
        var result = await WorkflowEngine.RunAsync(Request(doc, teller, clock) with
        {
            Timeout = TimeSpan.FromMinutes(10), ResumeFrom = journal.LastCheckpoint, WaitResult = new WorkflowWaitResult("done", "ok"),
        }, new RecordingJournal(), Ct);
        Assert.Equal(WorkflowRunStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task TheTellCap_FailsTheRunAtTheNextTell()
    {
        var teller = new FakeTeller();
        var doc = WorkflowSamples.Workflow("""
            [ { "id": "a", "type": "forEach", "items": "{vars.l}", "as": "x", "body": "b", "then": null },
              { "id": "b", "type": "tell", "text": "item {x}", "next": null } ]
            """, """[{ "name": "l", "type": "list<int>", "initial": [1, 2, 3, 4] }]""");
        var result = await WorkflowEngine.RunAsync(Request(doc, teller, new FakeClock(T0)) with { MaxTells = 3 }, new RecordingJournal(), Ct);

        Assert.Equal((WorkflowRunStatus.Failed, "b"), (result.Status, result.FailedStepId));
        Assert.Contains("a run may tell at most 3 times (the member's maxTells is 3); this would be tell 4", result.Reason);
        Assert.Equal(["item 1", "item 2", "item 3"], teller.Calls.Select(c => c.Text));
    }

    [Fact]
    public async Task TheDefaultCap_IsFive()
    {
        var teller = new FakeTeller();
        var doc = WorkflowSamples.Workflow("""
            [ { "id": "a", "type": "forEach", "items": "{vars.l}", "as": "x", "body": "b", "then": null },
              { "id": "b", "type": "tell", "text": "item {x}", "next": null } ]
            """, """[{ "name": "l", "type": "list<int>", "initial": [1, 2, 3, 4, 5, 6] }]""");
        var result = await WorkflowEngine.RunAsync(Request(doc, teller, new FakeClock(T0)), new RecordingJournal(), Ct);
        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Equal(5, teller.Calls.Count);
    }

    [Fact]
    public async Task ADryRun_NeverTells_LogsWhatItWouldTell_AndIntoGetsDryRun()
    {
        var teller = new FakeTeller();
        var journal = new RecordingJournal();
        var doc = Doc("""{ "id": "a", "type": "tell", "member": "Ava", "text": "Refund order 7.", "wait": true, "into": "t", "next": "b" }""");
        var result = await WorkflowEngine.RunAsync(Request(doc, teller, new FakeClock(T0)) with { DryRun = true }, journal, Ct);

        Assert.Equal(WorkflowRunStatus.Succeeded, result.Status); // a dry tell does not wait
        Assert.Empty(teller.Calls);
        Assert.Contains("would tell Ava of Support: Refund order 7.", journal.Lines);
        Assert.Equal("dryRun:0", journal.LastCheckpoint.Vars["after"]!.GetValue<string>());
    }

    [Fact]
    public async Task DryTells_ATestRunsMadeUpReply_IsTheReply_TextOrJson()
    {
        var teller = new FakeTeller();
        var doc = Doc("""{ "id": "a", "type": "tell", "team": "Billing", "text": "x", "wait": true, "reply": "TriageResult", "into": "t", "next": "b" }""");
        var asText = new RecordingJournal();
        await WorkflowEngine.RunAsync(Request(doc, teller, new FakeClock(T0)) with
        {
            DryTells = true, TellReplies = new JsonObject { ["a"] = "Looked. {\"refund\": true}" },
        }, asText, Ct);
        var t = T(asText.LastCheckpoint);
        Assert.Equal(("dryRun", "Looked. {\"refund\": true}", true), (t["outcome"]!.GetValue<string>(), t["reply"]!.GetValue<string>(), t["data"]!["refund"]!.GetValue<bool>()));
        Assert.Contains("would tell Manager of Billing: x", asText.Lines);

        var asJson = new RecordingJournal();
        await WorkflowEngine.RunAsync(Request(doc, teller, new FakeClock(T0)) with
        {
            DryTells = true, TellReplies = new JsonObject { ["a"] = new JsonObject { ["refund"] = false } },
        }, asJson, Ct);
        Assert.False(T(asJson.LastCheckpoint)["data"]!["refund"]!.GetValue<bool>());

        var wrong = await WorkflowEngine.RunAsync(Request(doc, teller, new FakeClock(T0)) with
        {
            DryTells = true, TellReplies = new JsonObject { ["a"] = new JsonObject { ["refund"] = "yes" } },
        }, new RecordingJournal(), Ct);
        Assert.Equal((WorkflowRunStatus.Failed, "a"), (wrong.Status, wrong.FailedStepId));
        Assert.Contains("does not match TriageResult", wrong.Reason);
        Assert.Empty(teller.Calls);
    }

    [Fact]
    public async Task ARefusedTell_FailsTheStepWithTheReason()
    {
        var teller = new FakeTeller { Refuse = "'Support' has no agent member named 'Zed'" };
        var doc = Doc("""{ "id": "a", "type": "tell", "member": "Zed", "text": "x", "into": "t", "next": "b" }""");
        var result = await WorkflowEngine.RunAsync(Request(doc, teller, new FakeClock(T0)), new RecordingJournal(), Ct);
        Assert.Equal((WorkflowRunStatus.Failed, "a"), (result.Status, result.FailedStepId));
        Assert.Contains("no agent member named 'Zed'", result.Reason);
    }

    [Fact]
    public async Task WithoutATeller_ATellFails()
    {
        var doc = Doc("""{ "id": "a", "type": "tell", "text": "x", "next": "b" }""");
        var request = Request(doc, new FakeTeller(), new FakeClock(T0)) with { Teller = null };
        var result = await WorkflowEngine.RunAsync(request, new RecordingJournal(), Ct);
        Assert.Equal(WorkflowRunStatus.Failed, result.Status);
        Assert.Contains("no team to tell", result.Reason);
    }
}
