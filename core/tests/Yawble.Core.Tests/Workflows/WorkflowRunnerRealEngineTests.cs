using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;
using Yawble.Core.Workflows.Library;
using Yawble.Core.Workflows.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;
using RunStatus = Yawble.Core.Workflows.Running.WorkflowRunStatus;

namespace Yawble.Core.Tests.Workflows;

/// <summary>
/// Workflow runner regressions: the runner through the real workflow engine (SeamWorkflowEngine), the real checker and the design's
/// example workflow. The fake engine in WorkflowRunnerTests let through a triggerId the TriggerFired schema refuses (a number,
/// not a string) and a dry run without the catalog (its sends reached the provider).
/// </summary>
public class WorkflowRunnerRealEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Actions : IWorkflowActionsProvider, IWorkflowActions
    {
        public IWorkflowActionCatalog Catalog { get; } = FakeCatalog.MailAndSms();
        public List<WorkflowActionCall> Calls { get; } = [];
        public IWorkflowActions For(WorkflowCaller caller) => this;

        public Task<JsonNode?> InvokeAsync(WorkflowActionCall call, CancellationToken ct)
        {
            lock (Calls) Calls.Add(call);
            return Task.FromResult<JsonNode?>(call.Use == "mail"
                ? new JsonArray(WorkflowSamples.Email("m1", "Order", "shop@example.com", "2026-11-23T10:00:00Z"))
                : new JsonObject { ["status"] = 201 });
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public readonly Actions Actions = new();
        public BusEngine Bus = null!;
        public WorkflowLibrary Library = null!;
        public WorkflowRunStore Store = null!;
        public WorkflowRunner Runner = null!;
        public long TeamId;
        public long WorkflowId;

        public async Task<Rig> InitAsync()
        {
            WorkflowRunner? runner = null;
            Bus = new BusEngine(Db.OpenStore(), new WorkflowDispatchRunner(new ScriptedRunner(), () => runner), new BusOptions { IdleOffer = false });
            TeamId = await Bus.CreateTeamAsync("Alpha", ct: Ct);
            Library = new WorkflowLibrary(Db.ConnectionString, () => Actions.Catalog); // the real checker
            Store = new WorkflowRunStore(Db.ConnectionString);
            Runner = runner = new WorkflowRunner(Store, Library, Actions, SeamWorkflowEngine.Instance, Path.Combine(Db.Dir, "workflows"));
            Bus.WorkflowMembers = runner;
            var saved = Library.Save(WorkflowSamples.InboxSmsAlert);
            Assert.True(saved.Ok, saved.Check.ToJsonString()); // the design example passes the real check with the mail and SMS catalog
            WorkflowId = saved.Id;
            await Bus.StartAsync(Ct);
            return this;
        }

        public async Task<long> MemberAsync()
        {
            var row = await Bus.AddWorkflowMemberAsync(TeamId, "Poller", Ct);
            Store.SetWorkflowMember(row.Id, WorkflowId, null, Settings(), Bindings(), null);
            return row.Id;
        }

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
        await using var r = await new Rig().InitAsync();
        var bot = await r.MemberAsync();
        var t = await r.Bus.SaveTriggerAsync(new TriggerDraft(null, bot, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Poll.", true, true), Ct);

        await r.Bus.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);
        await r.Runner.WhenIdleAsync();

        var run = Assert.Single(r.Store.RecentRuns(r.TeamId, bot, 10));
        Assert.True(run.Status == RunStatus.Succeeded, Why(run)); // before the fix: $.triggerId: expected string, got number
        Assert.Equal(t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), run.Input!["triggerId"]!.GetValue<string>());
        Assert.Equal(2, r.Actions.Calls.Count(c => c.Use == "sms"));
    }

    [Fact]
    public async Task WorkflowPayloadsTriggerFired_PassesTheBuiltInSchema_InARealRun()
    {
        await using var r = await new Rig().InitAsync();
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
        await using var r = await new Rig().InitAsync();
        var input = WorkflowPayloads.TriggerFired(1, "once", DateTimeOffset.UtcNow);

        var outcome = await r.Runner.TestRunAsync(new WorkflowTestRun(r.WorkflowId, null, input, Settings(), Bindings(), r.TeamId, DryRun: true), Ct);

        Assert.True(outcome.Run.Status == RunStatus.Succeeded, Why(outcome.Run));
        Assert.Contains(r.Actions.Calls, c => c.Use == "mail"); // a read still runs in a dry run
        Assert.DoesNotContain(r.Actions.Calls, c => c.Use == "sms"); // before the fix: both sends reached the provider with DryRun set
        Assert.Equal(2, outcome.Log.Count(l => l.Line.StartsWith("would POST", StringComparison.Ordinal)));
    }
}
