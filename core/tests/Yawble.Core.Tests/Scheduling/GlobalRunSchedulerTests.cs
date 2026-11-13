using Yawble.Core.Bus;
using Yawble.Core.Scheduling;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Scheduling;

/// <summary>
/// Two teams of fake runners under maxConcurrentAgents = 2. Never more than 2
/// runs at once, the rest admitted in FIFO order across teams, and the queue survives a restart in order.
/// </summary>
public class GlobalRunSchedulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AdmitsOldestFirstUpToTheFreeSlots()
    {
        Assert.Equal([1, 2], RunScheduler.Admit(0, 2, [1, 2, 3]));
        Assert.Equal([1], RunScheduler.Admit(1, 2, [1, 2, 3]));
        Assert.Empty(RunScheduler.Admit(2, 2, [1, 2, 3]));
        Assert.Empty(RunScheduler.Admit(3, 2, [1, 2, 3])); // the limit was lowered: nobody is stopped, nobody admitted
        Assert.Equal(3, RunScheduler.PositionOf([7, 8, 9], x => x == 9));
        Assert.Null(RunScheduler.PositionOf([7, 8, 9], x => x == 1));
    }

    [Fact]
    public async Task TwoTeamsShareTwoSlotsInFifoOrder_AndTheQueueSurvivesARestartInOrder()
    {
        using var db = new TempDb();
        using var probe = db.OpenStore();
        var engineRunningMax = 0;
        void ProbeEngineCount()
        {
            lock (probe) engineRunningMax = Math.Max(engineRunningMax, (int)probe.RunningCount());
        }

        // ---- first process lifetime: member runs hold their slot until the test releases them
        var gates = new Dictionary<string, TaskCompletionSource>();
        TaskCompletionSource GateOf(string who)
        {
            lock (gates) return gates.TryGetValue(who, out var g) ? g : gates[who] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var first = new ScriptedRunner
        {
            Default = async (run, ct) =>
            {
                ProbeEngineCount();
                if (run.Member.Role == MemberRole.Manager) return RunResult.Ok;
                await GateOf(run.Member.Qualified).Task.WaitAsync(ct);
                await run.HandbackAsync("done", ct);
                return RunResult.Ok;
            },
        };
        var engine = new BusEngine(db.OpenStore(), first, new BusOptions { MaxConcurrentAgents = () => 2, SafetyPoll = TimeSpan.FromMilliseconds(100) });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.CreateTeamAsync("Beta", ct: Ct);
        foreach (var n in new[] { "a1", "a2", "a3" }) await engine.AddMemberAsync("Alpha", n, ct: Ct);
        foreach (var n in new[] { "b1", "b2", "b3" }) await engine.AddMemberAsync("Beta", n, ct: Ct);
        // Work arrives interleaved across the two teams, all before the first pump.
        foreach (var (team, member) in new[] { ("Alpha", "a1"), ("Beta", "b1"), ("Alpha", "a2"), ("Beta", "b2"), ("Alpha", "a3"), ("Beta", "b3") })
        {
            await engine.PersonTellAsync(team, member, $"job for {member}", ct: Ct);
        }
        await engine.StartAsync(Ct);

        await Wait.UntilAsync(() => first.AdmittedNames().Count == 2, "two admitted");
        await Task.Delay(300, Ct); // a third admission now would be a bug
        Assert.Equal(["Alpha/a1", "Beta/b1"], first.AdmittedNames());
        Assert.Equal(["Alpha/a2", "Beta/b2", "Alpha/a3", "Beta/b3"], QueueNames(probe));
        var b2 = (await engine.TeamStatusAsync("Beta", Ct)).Single(s => s.Name == "b2");
        Assert.Equal(("queued", 2), (b2.State, b2.QueuePosition!.Value));

        GateOf("Alpha/a1").SetResult(); // a1 ends: a2 takes the slot; Alpha's Manager, woken by a1, joins the back
        await Wait.UntilAsync(() => first.AdmittedNames().Count == 3, "a2 admitted");
        await Wait.UntilAsync(() => QueueNames(probe).Count == 4, "Alpha/Manager queued");
        Assert.Equal(["Alpha/a1", "Beta/b1", "Alpha/a2"], first.AdmittedNames());
        var queueBefore = QueueNames(probe);
        Assert.Equal(["Beta/b2", "Alpha/a3", "Beta/b3", "Alpha/Manager"], queueBefore);

        // ---- the core stops with b1 and a2 running and four entries queued
        await engine.DisposeAsync();
        Assert.Equal(queueBefore, QueueNames(probe));
        Assert.Equal(2, probe.Runs("running").Count);

        // ---- second lifetime: everything completes after a short overlap
        var second = new ScriptedRunner
        {
            Default = async (run, ct) =>
            {
                ProbeEngineCount();
                await Task.Delay(60, ct);
                if (run.Member.Role == MemberRole.Member) await run.HandbackAsync("done", ct);
                return RunResult.Ok;
            },
        };
        await using (var restarted = new BusEngine(db.OpenStore(), second, new BusOptions { MaxConcurrentAgents = () => 2, SafetyPoll = TimeSpan.FromMilliseconds(100) }))
        {
            await restarted.StartAsync(Ct);
            await Wait.UntilAsync(() => restarted.IsQuiescentAsync(Ct), "everything done");
        }

        var admitted = second.AdmittedNames();
        TestContext.Current.SendDiagnosticMessage("after restart, admitted in order: " + string.Join(", ", admitted));
        Assert.Equal(queueBefore, admitted.Take(4)); // the queue kept its order across the restart
        Assert.True(first.MaxConcurrent <= 2 && second.MaxConcurrent <= 2, $"runner concurrency {first.MaxConcurrent}/{second.MaxConcurrent}");
        Assert.Equal(2, second.MaxConcurrent); // and the two slots were really used
        Assert.True(engineRunningMax <= 2, $"engine had {engineRunningMax} runs open at once");

        // The two runs open at the stop are interrupted, each reported once on the bus, caused by what it was running.
        var runs = probe.Runs();
        var interrupted = runs.Where(r => r.State == "interrupted").ToList();
        Assert.Equal(["Alpha/a2", "Beta/b1"], interrupted.Select(r => probe.Member(r.MemberId)!.Qualified).Order());
        foreach (var run in interrupted)
        {
            var failed = Assert.Single(probe.AllMessages(), m => m.Type == MessageTypes.Failed && m.CausationSeq == run.CausationSeq);
            Assert.Equal(FailureClasses.Interrupted, Payload.Field(failed, "failureClass"));
        }
        // Strict FIFO over the whole history, across both teams and the restart: admission order is queue order.
        var queueIds = runs.Select(r => r.QueueId).ToList();
        Assert.Equal(queueIds.Order(), queueIds);
        Assert.Equal(queueIds.Count, queueIds.Distinct().Count());
        Assert.All(runs, r => Assert.True(string.CompareOrdinal(r.QueuedAt, r.StartedAt) <= 0));
        Assert.Empty(probe.Runs("running"));
    }

    private static List<string> QueueNames(BusStore store)
    {
        lock (store) return store.Queue().Select(q => store.Member(q.MemberId)!.Qualified).ToList();
    }
}
