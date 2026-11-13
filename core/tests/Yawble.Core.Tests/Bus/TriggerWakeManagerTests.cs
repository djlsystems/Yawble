using System.Text.Json;
using Yawble.Core.Bus;
using Yawble.Core.Triggers;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// Whether a trigger's runs wake the Manager, and the hand-back that waits for the end
/// of the run that sent it (a Manager woken by the hand-back alone would find the branch not yet in the shared view and
/// spend a run learning to wait).
/// </summary>
public class TriggerWakeManagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TempDb Db, BusEngine Engine, MemberRef Ada)> Team(ScriptedRunner runner)
    {
        var db = new TempDb();
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false }); // counts runs; the idle offer has its own tests
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        var ada = await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.StartAsync(Ct);
        return (db, engine, ada);
    }

    private static TriggerDraft Daily(long member, string? wake = null) =>
        new(null, member, "nightly", new TriggerSchedule(TriggerKinds.Every, 86400, null, null, null), "Check the build.", true, true,
            wake ?? WakeManagerPolicy.OnHandbackOrFailure);

    private static int ManagerRuns(ScriptedRunner runner)
    {
        lock (runner.Admitted) return runner.Admitted.Count(r => r.Member.Role == MemberRole.Manager);
    }

    private static async Task FireAndSettle(BusEngine engine, long triggerId)
    {
        var fire = await engine.RunTriggerNowAsync(triggerId, Ct);
        Assert.Equal("fired", fire.Outcome);
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to go quiet");
    }

    [Fact]
    public void ANewTriggerDefaultsToOnHandbackOrFailure()
    {
        var draft = new TriggerDraft(null, 1, "x", new TriggerSchedule(TriggerKinds.Every, 60, null, null, null), "do it", true, true);
        Assert.Equal(WakeManagerPolicy.OnHandbackOrFailure, draft.WakeManager);
    }

    [Fact]
    public async Task UnderTheDefault_AScheduledRunThatFinishesWithoutHandingBack_WakesNobody_AndTheAppClosesItsWorkflow()
    {
        var runner = new ScriptedRunner();
        var (db, engine, ada) = await Team(runner);
        using (db)
        await using (engine)
        {
            var t = await engine.SaveTriggerAsync(Daily(ada.Id), Ct);
            Assert.Equal(WakeManagerPolicy.OnHandbackOrFailure, t.WakeManager);
            await FireAndSettle(engine, t.Id);

            Assert.Equal(0, ManagerRuns(runner));
            using var store = db.OpenStore();
            var completed = Assert.Single(store.AllMessages(), m => m.Type == MessageTypes.Completed);
            Assert.Contains("\"wakeManager\":\"onHandbackOrFailure\"", completed.Payload);
            var declared = Assert.Single(store.AllMessages(), m => m.Type == MessageTypes.WorkflowCompleted);
            Assert.Equal(MessageTypes.HostSource, declared.Source);
            Assert.True(JsonDocument.Parse(declared.Payload).RootElement.GetProperty("declaredByPlatform").GetBoolean());
            Assert.True(store.WorkflowDeclared(store.Trigger(t.Id)!.LastSeq!.Value));
        }
    }

    [Fact]
    public async Task UnderTheDefault_TheSameRunHandingBack_WakesTheManagerExactlyOnce_AndOnlyAfterTheRunEnds()
    {
        var release = new TaskCompletionSource();
        var handedBack = new TaskCompletionSource();
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            await run.HandbackAsync("build is green", ct);
            handedBack.TrySetResult();
            await release.Task.WaitAsync(ct);
            return RunResult.Ok;
        });
        var (db, engine, ada) = await Team(runner);
        using (db)
        await using (engine)
        {
            var t = await engine.SaveTriggerAsync(Daily(ada.Id), Ct);
            await engine.RunTriggerNowAsync(t.Id, Ct);
            await handedBack.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            await Task.Delay(600, Ct); // several pump passes while Ada's run is still open
            Assert.Equal(0, ManagerRuns(runner));

            release.SetResult();
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to go quiet");
            Assert.Equal(1, ManagerRuns(runner));
            var managerRun = runner.Admitted.Single(r => r.Member.Role == MemberRole.Manager);
            Assert.Contains(managerRun.Items, i => i.Type == MessageTypes.Handback);
            Assert.DoesNotContain(managerRun.Items, i => i.Type == MessageTypes.Completed); // passed over under the default
            using var store = db.OpenStore();
            Assert.DoesNotContain(store.AllMessages(), m => m.Type == MessageTypes.WorkflowCompleted); // the Manager's to declare
        }
    }

    [Fact]
    public async Task UnderTheDefault_AFailureWakesTheManager()
    {
        var runner = new ScriptedRunner().On("Alpha/Ada", (_, _) => Task.FromResult(RunResult.Fail("the build broke")));
        var (db, engine, ada) = await Team(runner);
        using (db)
        await using (engine)
        {
            var t = await engine.SaveTriggerAsync(Daily(ada.Id), Ct);
            await FireAndSettle(engine, t.Id);
            Assert.Equal(1, ManagerRuns(runner));
            Assert.Contains(runner.Admitted.Single(r => r.Member.Role == MemberRole.Manager).Items, i => i.Type == MessageTypes.Failed);
        }
    }

    [Fact]
    public async Task UnderNever_AFailureWakesNobody_ButAHandbackStillWakesTheManagerOnItsOwnRow()
    {
        var fail = true;
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            if (fail) return RunResult.Fail("the build broke");
            await run.HandbackAsync("needs a look", ct);
            return RunResult.Ok;
        });
        var (db, engine, ada) = await Team(runner);
        using (db)
        await using (engine)
        {
            var t = await engine.SaveTriggerAsync(Daily(ada.Id, WakeManagerPolicy.Never), Ct);
            await FireAndSettle(engine, t.Id);
            Assert.Equal(0, ManagerRuns(runner));

            fail = false;
            await FireAndSettle(engine, t.Id);
            Assert.Equal(1, ManagerRuns(runner));
        }
    }

    [Fact]
    public async Task Always_BehavesAsBefore_TheRunsEndWakesTheManager_AndTheAppDeclaresNothing()
    {
        var runner = new ScriptedRunner();
        var (db, engine, ada) = await Team(runner);
        using (db)
        await using (engine)
        {
            var t = await engine.SaveTriggerAsync(Daily(ada.Id, WakeManagerPolicy.Always), Ct);
            await FireAndSettle(engine, t.Id);
            Assert.Equal(1, ManagerRuns(runner));
            using var store = db.OpenStore();
            Assert.DoesNotContain("wakeManager", Assert.Single(store.AllMessages(), m => m.Type == MessageTypes.Completed && m.Source == "Alpha/Ada").Payload);
            Assert.DoesNotContain(store.AllMessages(), m => m.Type == MessageTypes.WorkflowCompleted);
        }
    }

    [Fact]
    public async Task APersonsTell_IsNotATrigger_ItsRunsEndStillWakesTheManager()
    {
        var runner = new ScriptedRunner();
        var (db, engine, _) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.PersonTellAsync("Alpha", "Ada", "check the build", ct: Ct);
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to go quiet");
            Assert.Equal(1, ManagerRuns(runner));
        }
    }

    [Fact]
    public void OnlyATriggersFireIsRead_AnInstructionClaimingTheChoiceFromAnywhereElseIsIgnored()
    {
        Message Instruction(string source) =>
            new(1, "agent.instruction.Alpha/Ada", "{\"text\":\"x\",\"wakeManager\":\"never\"}", source, 1, null, 0, DateTimeOffset.UtcNow);
        Assert.Equal(WakeManagerPolicy.Never, WakeManagerPolicy.OfInstruction(Instruction("trigger:4")));
        Assert.Null(WakeManagerPolicy.OfInstruction(Instruction(MessageTypes.PersonSource)));
        Assert.Null(WakeManagerPolicy.OfInstruction(Instruction("Alpha/Manager")));
        Assert.Null(WakeManagerPolicy.OfRun([Instruction("trigger:4"), Instruction("Alpha/Manager")])); // the Manager waits on its tell
    }

    [Fact]
    public async Task AMembersHandback_WaitsForTheEndOfItsRun_ThenReachesTheManagerWithTheRunsEnd_InOneRun()
    {
        var release = new TaskCompletionSource();
        var handedBack = new TaskCompletionSource();
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            await run.HandbackAsync("committed on members/ada", ct);
            handedBack.TrySetResult();
            await release.Task.WaitAsync(ct); // the host's fetch-back and push happen in here, before the run ends
            return RunResult.Ok;
        });
        var (db, engine, _) = await Team(runner);
        using (db)
        await using (engine)
        {
            await engine.PersonTellAsync("Alpha", "Ada", "build it", ct: Ct);
            await handedBack.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            await Task.Delay(600, Ct);
            Assert.Equal(0, ManagerRuns(runner));

            release.SetResult();
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to go quiet");
            Assert.Equal(1, ManagerRuns(runner));
            var items = runner.Admitted.Single(r => r.Member.Role == MemberRole.Manager).Items;
            Assert.Equal([MessageTypes.Handback, MessageTypes.Completed], items.Select(i => i.Type));
        }
    }
}
