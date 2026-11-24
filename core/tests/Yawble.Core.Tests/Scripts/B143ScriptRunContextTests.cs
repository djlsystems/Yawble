using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Integrations;
using Yawble.Core.Launch;
using Yawble.Core.Logs;
using Yawble.Core.Scripts.Actions;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;
using IntegrationCaller = Yawble.Core.Integrations.ScriptCaller;
using RunCaller = Yawble.Core.Scripts.Running.ScriptCaller;
using RunStatus = Yawble.Core.Scripts.Running.ScriptRunStatus;

namespace Yawble.Core.Tests.Scripts;

/// <summary>
/// Card 613 (B143 part A): a script run says what failed. A trigger's fire carries the script run id; a failed step's row keeps its reason; an
/// API call's failure names the integration, action, method, route and (for no answer) the host and the wait; the Runs log shows a script run's
/// script, trigger, failed step and script run id, and the Script runs log lists the runs. Through the real script engine, fakes for the actions.
/// </summary>
public class B143ScriptRunContextTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The mail and SMS actions; the SMS send fails with <see cref="SmsFailure"/> when set.</summary>
    private sealed class Actions : IScriptActionsProvider, IScriptActions
    {
        public IScriptActionCatalog Catalog { get; } = FakeCatalog.MailAndSms();
        public string? SmsFailure { get; set; }
        public IScriptActions For(RunCaller caller) => this;

        public Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct)
        {
            if (call.Use == "sms" && SmsFailure is { } why) throw new ScriptActionException(why);
            return Task.FromResult<JsonNode?>(call.Use == "mail"
                ? new JsonArray(ScriptSamples.Email("m1", "Order", "shop@example.com", "2026-11-23T10:00:00Z"))
                : new JsonObject { ["status"] = 201 });
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public readonly Actions Actions = new();
        public BusEngine Bus = null!;
        public ScriptLibrary Library = null!;
        public ScriptRunStore Store = null!;
        public ScriptRunner Runner = null!;
        public long TeamId;
        public long ScriptId;

        public async Task<Rig> InitAsync()
        {
            ScriptRunner? runner = null;
            Bus = new BusEngine(Db.OpenStore(), new ScriptDispatchRunner(new ScriptedRunner(), () => runner), new BusOptions { IdleOffer = false });
            TeamId = await Bus.CreateTeamAsync("Alpha", ct: Ct);
            Library = new ScriptLibrary(Db.ConnectionString, () => Actions.Catalog);
            Store = new ScriptRunStore(Db.ConnectionString);
            Runner = runner = new ScriptRunner(Store, Library, Actions, SeamScriptEngine.Instance, Path.Combine(Db.Dir, "scripts"));
            Bus.ScriptMembers = runner;
            var saved = Library.Save(ScriptSamples.InboxSmsAlert);
            Assert.True(saved.Ok, saved.Check.ToJsonString());
            ScriptId = saved.Id;
            await Bus.StartAsync(Ct);
            return this;
        }

        public async Task<long> MemberAsync()
        {
            var row = await Bus.AddScriptMemberAsync(TeamId, "Poller", Ct);
            Store.SetScriptMember(row.Id, ScriptId, null,
                new JsonObject { ["recipients"] = new JsonArray("+15551230001"), ["fromNumber"] = "+15559990000" },
                new JsonObject { ["mail"] = "mailbox", ["sms"] = "twilio" }, null);
            return row.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Runner.StopAsync();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static List<JsonObject> Rows(JsonObject page) => page["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();

    [Fact]
    public async Task ATriggersFire_CarriesTheScriptRunId()
    {
        await using var r = await new Rig().InitAsync();
        var bot = await r.MemberAsync();
        var t = await r.Bus.SaveTriggerAsync(new TriggerDraft(null, bot, "poll", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "Poll.", true, true), Ct);

        var fire = await r.Bus.RunTriggerNowAsync(t.Id, Ct);
        await r.Runner.WhenIdleAsync();

        Assert.Equal("fired", fire.Outcome);
        var run = Assert.Single(r.Store.RecentRuns(r.TeamId, bot, 10));
        Assert.Equal(run.Id, fire.ScriptRunId);
    }

    [Fact]
    public async Task AFailedStepsRow_KeepsItsReason_AndTheRunsLogsSayWhatFailed()
    {
        await using var r = await new Rig().InitAsync();
        var bot = await r.MemberAsync();
        r.Actions.SmsFailure = "twilio CreateMessage (POST /2010-04-01/Accounts/{AccountSid}/Messages.json): HTTP 400: invalid To";
        var input = ScriptPayloads.TriggerFired(7, "every", DateTimeOffset.Parse("2026-11-24T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        var outcome = await r.Runner.RunMemberAsync(bot, "trigger:7", input, Ct);

        Assert.Equal(RunStatus.Failed, outcome.Run.Status);
        var failedStep = outcome.Run.FailedStepId;
        Assert.NotNull(failedStep);
        var row = Assert.Single(r.Store.Steps(outcome.Run.Id), s => s.StepId == failedStep && s.Status == "failed");
        Assert.Contains("HTTP 400: invalid To", (string?)row.Output!["error"]); // before: the failed step's output was empty
        Assert.Contains("twilio CreateMessage (POST", outcome.Run.Reason);

        var logs = new LogService(r.Db.Dir, r.Db.ConnectionString);
        var runs = Rows(logs.Query(new LogQuery("runs", Team: "Alpha")));
        var script = Assert.Single(runs, x => (string?)x["kind"] == "script");
        Assert.Equal("inbox-sms-alert@1", (string?)script["script"]);
        Assert.Equal("trigger:7", (string?)script["trigger"]);
        Assert.Equal(failedStep, (string?)script["failedStep"]);
        Assert.Equal(outcome.Run.Id, (long?)script["scriptRun"]);

        var scriptRuns = Rows(logs.Query(new LogQuery("scripts", Member: "Alpha/Poller")));
        var listed = Assert.Single(scriptRuns);
        Assert.Equal(("Alpha", "Poller", "inbox-sms-alert@1", "failed", failedStep), ((string?)listed["team"], (string?)listed["member"],
            (string?)listed["script"], (string?)listed["status"], (string?)listed["failedStep"]));
        Assert.Contains("HTTP 400", (string?)listed["reason"]);

        // the member's activity names the script run too
        var activity = Rows(logs.Query(new LogQuery(LogSources.Activity, Member: "Alpha/Poller")));
        Assert.Contains(activity, a => (string?)a["source"] == "Runs" && ((string?)a["details"])!.Contains($"inbox-sms-alert@1 (script run {outcome.Run.Id}, trigger:7) at step {failedStep}", StringComparison.Ordinal));
    }

    [Fact]
    public void TheToolCallsLog_ShowsTheStoredColumns_AndExitIsExitOrHttp()
    {
        var tools = LogSources.Find("tools")!;
        Assert.Equal(["at", "team", "member", "integration", "args", "decision", "rule", "exit", "duration", "bytes", "via", "workflow"], tools.Columns.Select(c => c.Key));
        Assert.Equal("Exit/HTTP", tools.Columns.Single(c => c.Key == "exit").Label);
    }

    // ------------------------------------------------------------------ API failures name the call

    private sealed class OneAnswerApi(ScriptApiResult answer) : IScriptApi
    {
        public Task<ScriptApiResult> CallAsync(IntegrationCaller caller, string integration, string action, JsonObject args, CancellationToken ct) => Task.FromResult(answer);
        public IReadOnlyList<string> Teams() => ["Alpha"];
        public IReadOnlyList<ScriptOperation> Operations(string team) => [new("CreateMessage", "post", "/from-the-catalog", "", "allow", [], null)];
    }

    private static Task<JsonNode?> CallSms(ScriptApiResult answer) =>
        new ApiScriptActionsProvider(new OneAnswerApi(answer)).For(new RunCaller(1, "Alpha", 7, "Texter", 42, new Dictionary<string, string> { ["sms"] = "twilio" }, false))
            .InvokeAsync(new ScriptActionCall("sms", "api", "CreateMessage", new JsonObject { ["To"] = "+15550000001" }, false), Ct);

    [Fact]
    public async Task NoAnswer_NamesTheIntegrationActionMethodRouteHostAndTheWait()
    {
        var e = await Assert.ThrowsAsync<ScriptActionException>(() => CallSms(new ScriptApiResult(0, "", "the call to twilio timed out after 60 s (stopped)")
        {
            Method = "POST", Route = "/2010-04-01/Accounts/{AccountSid}/Messages.json", Host = "api.sms.example", TimeoutSeconds = 60,
        }));
        Assert.Equal("twilio CreateMessage (POST /2010-04-01/Accounts/{AccountSid}/Messages.json): no answer from api.sms.example (waited up to 60 s): "
            + "the call to twilio timed out after 60 s (stopped)", e.Message);
    }

    [Fact]
    public async Task AnHttpError_NamesTheCall_FromTheCatalogWhenTheAnswerDoesNotSay()
    {
        var e = await Assert.ThrowsAsync<ScriptActionException>(() => CallSms(new ScriptApiResult(503, "text/plain", "down")));
        Assert.Equal("twilio CreateMessage (POST /from-the-catalog): HTTP 503: down", e.Message);
    }
}
