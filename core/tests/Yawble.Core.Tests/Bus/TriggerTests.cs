using Yawble.Core.Bus;
using Yawble.Core.Triggers;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>Triggers and wakes (Yawble TriggerSweep / Triggers / WakeMe, first slice).</summary>
public class TriggerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ schedule maths

    [Fact]
    public void EveryN_WithoutAnAnchor_CountsFromWhenItIsArmed_AndWithOneKeepsItsRhythm()
    {
        var every = new TriggerSchedule(TriggerKinds.Every, 600, null, null, null);
        Assert.Equal(T0.AddMinutes(10), TriggerScheduling.NextOccurrence(every, T0));
        var anchored = every with { FireAt = T0.AddMinutes(3) };
        Assert.Equal(T0.AddMinutes(3), TriggerScheduling.NextOccurrence(anchored, T0));
        Assert.Equal(T0.AddMinutes(13), TriggerScheduling.NextOccurrence(anchored, T0.AddMinutes(4)));
    }

    [Fact]
    public void Cron_TakesFiveOrSixFields_InItsTimezone()
    {
        var weekdayNine = new TriggerSchedule(TriggerKinds.Cron, null, "0 9 * * 1-5", "UTC", null);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero), TriggerScheduling.NextOccurrence(weekdayNine, T0)); // Fri noon -> Mon 9:00
        var seconds = weekdayNine with { Cron = "30 0 9 * * 1-5" };
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 9, 0, 30, TimeSpan.Zero), TriggerScheduling.NextOccurrence(seconds, T0));
        var tokyo = weekdayNine with { Timezone = "Asia/Tokyo" };
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero), TriggerScheduling.NextOccurrence(tokyo, T0)); // Mon 9:00 JST
    }

    [Theory]
    [InlineData("every", 5, null, null, false, "at least 10 seconds")]
    [InlineData("cron", null, "not a cron", "UTC", false, "invalid")]
    [InlineData("cron", null, "0 9 * * *", null, false, "timezone")]
    [InlineData("cron", null, "0 9 * * *", "Mars/Olympus", false, "Unknown timezone")]
    [InlineData("once", null, null, null, true, "in the future")]
    [InlineData("hourly", null, null, null, false, "kind must be")]
    public void InvalidSchedulesSayWhy(string kind, int? interval, string? cron, string? tz, bool past, string reason)
    {
        var s = new TriggerSchedule(kind, interval, cron, tz, past ? T0.AddMinutes(-1) : null);
        Assert.Contains(reason, TriggerScheduling.Validate(s, T0));
    }

    [Fact]
    public void PreviewListsTheNextFires()
    {
        var p = TriggerScheduling.Preview(new TriggerSchedule(TriggerKinds.Every, 3600, null, null, null), T0, 3);
        Assert.Equal([T0.AddHours(1), T0.AddHours(2), T0.AddHours(3)], p);
    }

    // ------------------------------------------------------------------ the sweep

    private static async Task<(TempDb Db, BusEngine Engine, MemberRef Ada)> Team(ScriptedRunner? runner = null)
    {
        var db = new TempDb();
        var engine = new BusEngine(db.OpenStore(), runner ?? new ScriptedRunner(), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        var ada = await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        return (db, engine, ada);
    }

    private static TriggerDraft Every(long member, int seconds, bool onlyWhenIdle = true) =>
        new(null, member, "nightly check", new TriggerSchedule(TriggerKinds.Every, seconds, null, null, null), "Check the build.", onlyWhenIdle, true);

    [Fact]
    public async Task ADueTriggerTellsItsMemberAsANewWorkflow_AndArmsTheNextFire()
    {
        var (db, engine, ada) = await Team();
        using (db)
        await using (engine)
        {
            var t = await engine.SaveTriggerAsync(Every(ada.Id, 60), Ct);
            Assert.NotNull(t.NextDueAt);
            await engine.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);

            using var store = db.OpenStore();
            var told = Assert.Single(store.Triggers(), r => r.Id == t.Id);
            Assert.Equal("fired", told.LastOutcome);
            Assert.Equal(t.NextDueAt.Value.AddSeconds(60), told.NextDueAt);
            var m = store.Find(told.LastSeq!.Value)!;
            Assert.Equal(MessageTypes.Instruction(ada), m.Type);
            Assert.Equal($"trigger:{t.Id}", m.Source);
            Assert.Null(m.CausationSeq); // a new workflow
            Assert.Contains("Check the build.", m.Payload);
        }
    }

    [Fact]
    public async Task FiresThatPassedWhileTheAppWasDown_AreRecordedAsMissed_NotCaughtUp()
    {
        var (db, engine, ada) = await Team();
        using (db)
        await using (engine)
        {
            var t = await engine.SaveTriggerAsync(Every(ada.Id, 60), Ct);
            var late = t.NextDueAt!.Value.AddMinutes(10).AddSeconds(5);
            await engine.SweepTriggersAsync(late, Ct);
            using var store = db.OpenStore();
            var row = store.Trigger(t.Id)!;
            Assert.Equal("missed", row.LastOutcome);
            Assert.Equal(11, row.MissedCount);
            Assert.True(row.NextDueAt > late);
            Assert.Null(row.LastSeq);
            Assert.DoesNotContain(store.AllMessages(), m => m.Source.StartsWith("trigger:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task OnlyWhenIdle_SkipsAFireWhileTheMemberIsBusy()
    {
        var release = new TaskCompletionSource<RunResult>();
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) => release.Task);
        var (db, engine, ada) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            await engine.PersonTellAsync("Alpha", "Ada", "long job", ct: Ct);
            await Wait.UntilAsync(() => runner.Admitted.Count == 1, "Ada running");
            var t = await engine.SaveTriggerAsync(Every(ada.Id, 60), Ct);
            await engine.SweepTriggersAsync(t.NextDueAt!.Value.AddSeconds(1), Ct);
            using (var store = db.OpenStore())
            {
                var row = store.Trigger(t.Id)!;
                Assert.Equal("skipped", row.LastOutcome);
                Assert.Contains("busy", row.LastReason);
            }
            release.SetResult(RunResult.Ok);
        }
    }

    [Fact]
    public async Task AOnceThatPassedFiresLate_Once()
    {
        var (db, engine, ada) = await Team();
        using (db)
        await using (engine)
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(5);
            var t = await engine.SaveTriggerAsync(new TriggerDraft(null, ada.Id, "once", new TriggerSchedule(TriggerKinds.Once, null, null, null, at), "Do it.", true, true), Ct);
            await engine.SweepTriggersAsync(at.AddHours(3), Ct);
            using var store = db.OpenStore();
            var row = store.Trigger(t.Id)!;
            Assert.Equal("fired", row.LastOutcome);
            Assert.Null(row.NextDueAt);
            await engine.SweepTriggersAsync(at.AddHours(4), Ct);
            Assert.Single(store.AllMessages(), m => m.Source == $"trigger:{t.Id}");
        }
    }

    [Fact]
    public async Task RunNowFiresOnce_WithoutMovingTheNextFire()
    {
        var (db, engine, ada) = await Team();
        using (db)
        await using (engine)
        {
            var t = await engine.SaveTriggerAsync(Every(ada.Id, 3600), Ct);
            var fire = await engine.RunTriggerNowAsync(t.Id, Ct);
            Assert.Equal("fired", fire.Outcome);
            using var store = db.OpenStore();
            Assert.Equal(t.NextDueAt, store.Trigger(t.Id)!.NextDueAt);
        }
    }

    [Fact]
    public async Task WakeMe_TellsTheSameMemberItsNote_InsideTheSameWorkflow()
    {
        var runner = new ScriptedRunner();
        DateTimeOffset? due = null;
        runner.On("Alpha/Ada", async (run, ct) =>
        {
            if (due is null) due = await run.WakeMeAsync(30, "see if the deploy finished", ct);
            return RunResult.Ok;
        });
        var (db, engine, _) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.StartAsync(Ct);
            var root = await engine.PersonTellAsync("Alpha", "Ada", "deploy it", ct: Ct);
            await Wait.UntilAsync(() => due is not null, "the wake asked for");
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiet");
            await engine.SweepTriggersAsync(due!.Value.AddSeconds(1), Ct);
            using var store = db.OpenStore();
            var woken = Assert.Single(store.AllMessages(), m => m.Source == BusEngine.WakeSource);
            Assert.Equal(root.Seq, woken.CorrelationId);
            Assert.Contains("see if the deploy finished", woken.Payload);
            await Assert.ThrowsAsync<BusRefusal>(() => runner.Admitted[0].WakeMeAsync(0, "x", Ct));
        }
    }
}
