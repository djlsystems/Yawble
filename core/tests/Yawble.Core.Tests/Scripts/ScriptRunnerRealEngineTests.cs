using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Bus;
using Yawble.Core.Launch;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;
using Yawble.Core.Scripts.Library;
using Yawble.Core.Scripts.Running;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Triggers;
using RunStatus = Yawble.Core.Scripts.Running.ScriptRunStatus;

namespace Yawble.Core.Tests.Scripts;

/// <summary>
/// Card 584 regressions: the runner through the real script engine (SeamScriptEngine), the real checker and the design's
/// example script. The fake engine in ScriptRunnerTests let through a triggerId the TriggerFired schema refuses (a number,
/// not a string) and a dry run without the catalog (its sends reached the provider).
/// </summary>
public class ScriptRunnerRealEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Actions : IScriptActionsProvider, IScriptActions
    {
        public IScriptActionCatalog Catalog { get; } = FakeCatalog.MailAndSms();
        public List<ScriptActionCall> Calls { get; } = [];
        public IScriptActions For(ScriptCaller caller) => this;

        public Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct)
        {
            lock (Calls) Calls.Add(call);
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
            Library = new ScriptLibrary(Db.ConnectionString, () => Actions.Catalog); // the real checker
            Store = new ScriptRunStore(Db.ConnectionString);
            Runner = runner = new ScriptRunner(Store, Library, Actions, SeamScriptEngine.Instance, Path.Combine(Db.Dir, "scripts"));
            Bus.ScriptMembers = runner;
            var saved = Library.Save(ScriptSamples.InboxSmsAlert);
            Assert.True(saved.Ok, saved.Check.ToJsonString()); // the design example passes the real check with the mail and SMS catalog
            ScriptId = saved.Id;
            await Bus.StartAsync(Ct);
            return this;
        }

        public async Task<long> MemberAsync()
        {
            var row = await Bus.AddScriptMemberAsync(TeamId, "Poller", Ct);
            Store.SetScriptMember(row.Id, ScriptId, null, Settings(), Bindings(), null);
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

    private static string Why(ScriptRunRow run) => $"{run.Status} at {run.FailedStepId}: {run.Reason}";

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
    public async Task ScriptPayloadsTriggerFired_PassesTheBuiltInSchema_InARealRun()
    {
        await using var r = await new Rig().InitAsync();
        var bot = await r.MemberAsync();
        var input = ScriptPayloads.TriggerFired(42, "once", DateTimeOffset.Parse("2026-11-23T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("42", input["triggerId"]!.GetValue<string>());

        var outcome = await r.Runner.RunMemberAsync(bot, "run now", input, Ct);

        Assert.True(outcome.Run.Status == RunStatus.Succeeded, Why(outcome.Run));
        Assert.Equal("2026-11-23T10:00:00Z", r.Store.State(bot).Variables!["lastSeen"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADryTestRun_ReadsMail_ButLogsWouldPostInsteadOfSending()
    {
        await using var r = await new Rig().InitAsync();
        var input = ScriptPayloads.TriggerFired(1, "once", DateTimeOffset.UtcNow);

        var outcome = await r.Runner.TestRunAsync(new ScriptTestRun(r.ScriptId, null, input, Settings(), Bindings(), r.TeamId, DryRun: true), Ct);

        Assert.True(outcome.Run.Status == RunStatus.Succeeded, Why(outcome.Run));
        Assert.Contains(r.Actions.Calls, c => c.Use == "mail"); // a read still runs in a dry run
        Assert.DoesNotContain(r.Actions.Calls, c => c.Use == "sms"); // before the fix: both sends reached the provider with DryRun set
        Assert.Equal(2, outcome.Log.Count(l => l.Line.StartsWith("would POST", StringComparison.Ordinal)));
    }
}
