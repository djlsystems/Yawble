using System.Diagnostics;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Scheduling;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Scheduling;

/// <summary>
/// Teams under one global run limit, against fake runners whose every
/// admitted run starts a real stand-in sandbox process. The same checks under real sandboxes are
/// RealSandboxTeamsUnderOneLimitTests.
/// </summary>
public class TeamsUnderOneLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Done-when: two teams with three agents each (a Manager and two members), given work at the same time under a
    /// limit of 3, never have more than 3 sandboxes alive by the OS process list; the waiting agents show
    /// "Queued · position N of M"; they are admitted oldest first across both teams; both workflows complete.
    /// </summary>
    [Fact]
    public async Task TwoTeamsOfThreeUnderLimitThree_NeverExceedThreeSandboxes_AdmitOldestFirst_AndBothWorkflowsComplete()
    {
        using var db = new TempDb();
        await using var sandboxes = new StandInSandboxes();
        var runner = new ScriptedRunner { Default = (run, ct) => TeamScript(run, sandboxes, ct) };
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { MaxConcurrentAgents = () => 3, SafetyPoll = TimeSpan.FromMilliseconds(200) });
        try
        {
            foreach (var team in new[] { "Alpha", "Beta" })
            {
                await engine.CreateTeamAsync(team, ct: Ct);
                await engine.AddMemberAsync(team, "ada", ct: Ct);
                await engine.AddMemberAsync(team, "ben", ct: Ct);
            }
            // Work for both teams at the same time, before the first pump.
            var alpha = await engine.PersonTellAsync("Alpha", "Manager", "add a /health endpoint and its test", ct: Ct);
            var beta = await engine.PersonTellAsync("Beta", "Manager", "add a /version endpoint and its test", ct: Ct);

            // Watch what the UI would show while the teams contend.
            var labels = new List<QueuedEntry>();
            var maxRunning = 0;
            using var watching = new CancellationTokenSource();
            var watcher = Task.Run(async () =>
            {
                while (!watching.IsCancellationRequested)
                {
                    var snap = await engine.SchedulerSnapshotAsync(Ct);
                    maxRunning = Math.Max(maxRunning, snap.Running);
                    Assert.Equal(3, snap.Limit);
                    Assert.True(snap.Running <= snap.Limit, $"scheduler reports {snap.Running} running over the limit {snap.Limit}");
                    Assert.Equal(Enumerable.Range(1, snap.Queue.Count), snap.Queue.Select(q => q.Position));
                    Assert.Equal(snap.Running, snap.Teams.Sum(t => t.Running));
                    Assert.Equal(snap.Queued, snap.Teams.Sum(t => t.Queued));
                    lock (labels) labels.AddRange(snap.Queue);
                    try { await Task.Delay(10, watching.Token); } catch (OperationCanceledException) { }
                }
            }, Ct);

            await engine.StartAsync(Ct);
            using var probe = db.OpenStore();
            await Wait.UntilAsync(() => probe.WorkflowDeclared(alpha.Seq) && probe.WorkflowDeclared(beta.Seq), "both workflows declared", TimeSpan.FromSeconds(60));
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to settle");
            await watching.CancelAsync();
            await watcher;

            // Never more than 3 sandboxes alive, by the OS process list, and the limit was actually reached.
            TestContext.Current.SendDiagnosticMessage(
                $"stand-in sandboxes started {sandboxes.Started}, max alive at once {sandboxes.MaxLive} (OS process list, {sandboxes.Samples} samples); scheduler max running {maxRunning}");
            Assert.Equal(4, sandboxes.Started); // four member runs; Manager runs hold a slot but start no stand-in here
            Assert.InRange(sandboxes.MaxLive, 1, 3);
            Assert.Equal(3, maxRunning);
            Assert.Equal(3, runner.MaxConcurrent);

            // Waiting agents were shown "Queued · position N of M", and agents of both teams waited.
            List<QueuedEntry> seen;
            lock (labels) seen = [.. labels];
            Assert.NotEmpty(seen);
            Assert.All(seen, q => Assert.Equal($"Queued · position {q.Position} of {q.Of}", q.Label));
            Assert.Contains(seen, q => q.Team == "Alpha");
            Assert.Contains(seen, q => q.Team == "Beta");
            foreach (var q in seen.DistinctBy(q => q.Label)) TestContext.Current.SendDiagnosticMessage($"seen: {q.Team}/{q.Member}: {q.Label}");

            // Oldest first across both teams: admission order (run id) is queue order (queue id) and enqueue time.
            var runs = probe.Runs();
            foreach (var r in runs)
            {
                TestContext.Current.SendDiagnosticMessage(
                    $"run {r.Id} {probe.Member(r.MemberId)!.Qualified} queue={r.QueueId} queued={r.QueuedAt} started={r.StartedAt} {r.State}");
            }
            Assert.Equal(runs.Select(r => r.QueueId).Order(), runs.Select(r => r.QueueId));
            Assert.Equal(runs.Select(r => r.QueuedAt).Order(StringComparer.Ordinal), runs.Select(r => r.QueuedAt));
            Assert.Contains(runs, r => string.CompareOrdinal(r.StartedAt, r.QueuedAt) > 0); // somebody really waited
            Assert.All(runs, r => Assert.Equal("completed", r.State));
            var teamsSeenInOrder = runs.Select(r => probe.Member(r.MemberId)!.Team).ToArray();
            Assert.Equal(["Alpha", "Beta"], teamsSeenInOrder.Take(2)); // the two Managers, in the order they were told

            // Both workflows complete, with the right chain: tells at depth 1, handbacks at depth 2.
            foreach (var root in new[] { alpha, beta })
            {
                var flow = probe.Workflow(root.Seq);
                Assert.Equal(2, flow.Count(m => m.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal) && m.Depth == 1));
                Assert.Equal(2, flow.Count(m => m.Type == MessageTypes.Handback && m.Depth == 2));
                Assert.Single(flow, m => m.Type == MessageTypes.WorkflowCompleted);
            }

            var final = await engine.SchedulerSnapshotAsync(Ct);
            Assert.Equal((0, 0), (final.Running, final.Queued));
            Assert.All(final.Teams, t => Assert.Equal(runs.Count(r => probe.Member(r.MemberId)!.Team == t.Team), t.Admitted));
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    /// <summary>A Manager splits the task between ada and ben, then declares; members hold a stand-in sandbox.</summary>
    private static async Task<RunResult> TeamScript(RunContext run, StandInSandboxes sandboxes, CancellationToken ct)
    {
        if (run.Member.Role == MemberRole.Manager)
        {
            if (run.Items.Any(i => i.Source == MessageTypes.PersonSource))
            {
                await run.TellAsync("ada", "part 1: the endpoint", ct);
                await run.TellAsync("ben", "part 2: its test", ct);
                return RunResult.Ok;
            }
            try
            {
                await run.WorkflowCompleteAsync("both parts handed back", ct);
            }
            catch (BusRefusal r) when (r.Code is "busy" or "already_declared")
            {
                // Not finished yet: the next notice wakes the Manager again.
            }
            return RunResult.Ok;
        }
        var sandbox = sandboxes.Start();
        try
        {
            await Task.Delay(300, ct);
            await run.HandbackAsync($"{run.Member.Name} committed on members/{run.Member.Name}", ct);
            return RunResult.Ok;
        }
        finally
        {
            StandInSandboxes.End(sandbox);
        }
    }

    /// <summary>
    /// An interactive session takes a slot (decided Q-D) and waits in the same FIFO with no jump; while a member's
    /// session is open its instructions wait; Stop on the session gives its slot to the oldest queued entry.
    /// </summary>
    [Fact]
    public async Task AnInteractiveSessionTakesASlot_QueuesInFifoOrder_AndHoldsTheMembersInstructions()
    {
        using var db = new TempDb();
        await using var sandboxes = new StandInSandboxes();
        var gates = new Gates();
        var runner = new ScriptedRunner
        {
            Default = async (run, ct) =>
            {
                if (run.Member.Role == MemberRole.Manager) return RunResult.Ok; // woken by run endings; holds no sandbox
                var sandbox = sandboxes.Start();
                try
                {
                    await gates.Of(run.Member.Qualified).WaitAsync(ct);
                    return RunResult.Ok;
                }
                finally
                {
                    StandInSandboxes.End(sandbox);
                }
            },
        };
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { MaxConcurrentAgents = () => 2, SafetyPoll = TimeSpan.FromMilliseconds(100) });
        try
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            await engine.CreateTeamAsync("Beta", ct: Ct);
            foreach (var n in new[] { "a1", "a2", "a3" }) await engine.AddMemberAsync("Alpha", n, ct: Ct);
            await engine.AddMemberAsync("Beta", "b1", ct: Ct);
            await engine.StartAsync(Ct);

            Func<CancellationToken, Task> Session(string who) => async token =>
            {
                var sandbox = sandboxes.Start();
                try
                {
                    await gates.Of("session:" + who).WaitAsync(token);
                }
                finally
                {
                    StandInSandboxes.End(sandbox);
                }
            };

            // a1 opens its interactive CLI: admitted at once, holding a slot.
            var a1Session = await engine.RequestSessionAsync("Alpha", "a1", Session("a1"), Ct);
            Assert.True(await a1Session.Admission.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.Null(await a1Session.PositionAsync());
            await engine.PersonTellAsync("Alpha", "a2", "job a2", ct: Ct);
            await Wait.UntilAsync(() => MemberRuns(runner).Count == 1, "a2 admitted");
            // Both slots are taken: a3's run and b1's session queue, in that order.
            await engine.PersonTellAsync("Alpha", "a3", "job a3", ct: Ct);
            await Wait.UntilAsync(async () => (await engine.SchedulerSnapshotAsync(Ct)).Queued == 1, "a3 queued");
            var b1Session = await engine.RequestSessionAsync("Beta", "b1", Session("b1"), Ct);
            Assert.False(b1Session.Admission.IsCompleted);
            Assert.Equal(2, await b1Session.PositionAsync());

            // a1 is told something while its session is open: the instruction waits; a1 does not queue a run.
            await engine.PersonTellAsync("Alpha", "a1", "job a1", ct: Ct);
            // Delivery is pumped off the caller's thread: wait until it lands (slow machines take longer than any
            // fixed delay), then give the scheduler time to (wrongly) queue a run for a1 before checking it did not.
            await Wait.UntilAsync(async () => (await engine.TeamStatusAsync("Alpha", Ct)).Single(s => s.Name == "a1").QueuedInstructions == 1,
                "a1's instruction delivered and held");
            await Task.Delay(300, Ct);
            var snap = await engine.SchedulerSnapshotAsync(Ct);
            Assert.Equal(2, snap.Running);
            Assert.Equal(
                [("Alpha", "a3", "run", "Queued · position 1 of 2"), ("Beta", "b1", "interactive", "waiting for a run slot (position 2)")],
                snap.Queue.Select(q => (q.Team, q.Member, q.Kind, q.Label)));
            Assert.Equal(["interactive", "run"], snap.Live.Select(l => l.Kind).Order());
            var a1 = (await engine.TeamStatusAsync("Alpha", Ct)).Single(s => s.Name == "a1");
            Assert.Equal(("session", 1), (a1.State, a1.QueuedInstructions));
            Assert.Equal(2, sandboxes.CountLive());

            // A member that is queued, running or already in a session cannot open another.
            await Assert.ThrowsAsync<BusRefusal>(() => engine.RequestSessionAsync("Alpha", "a3", Session("x"), Ct));
            await Assert.ThrowsAsync<BusRefusal>(() => engine.RequestSessionAsync("Alpha", "a2", Session("x"), Ct));
            await Assert.ThrowsAsync<BusRefusal>(() => engine.RequestSessionAsync("Alpha", "a1", Session("x"), Ct));

            // Stop on a1's session: its slot goes to the oldest entry (a3's run, not b1's session); a1's held
            // instruction now joins the back of the queue, behind b1.
            Assert.True(await engine.StopMemberAsync("Alpha", "a1", Ct));
            await a1Session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await Wait.UntilAsync(() => MemberRuns(runner).Count == 2, "a3 admitted");
            Assert.Equal(["Alpha/a2", "Alpha/a3"], MemberRuns(runner));
            await Wait.UntilAsync(async () => (await engine.SchedulerSnapshotAsync(Ct)).Queued == 2, "a1 queued behind b1");
            snap = await engine.SchedulerSnapshotAsync(Ct);
            Assert.Equal([("Beta", "b1"), ("Alpha", "a1")], snap.Queue.Select(q => (q.Team, q.Member)));

            // a2 ends: b1's session is admitted before a1's run.
            gates.Open("Alpha/a2");
            Assert.True(await b1Session.Admission.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.Equal(["Alpha/a2", "Alpha/a3"], MemberRuns(runner));
            // b1's session ends normally (the CLI exits): a1's run takes the slot.
            gates.Open("session:b1");
            await b1Session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await Wait.UntilAsync(() => MemberRuns(runner).Count == 3, "a1 admitted");
            Assert.Equal(["Alpha/a2", "Alpha/a3", "Alpha/a1"], MemberRuns(runner));
            gates.Open("Alpha/a3");
            gates.Open("Alpha/a1");
            await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the bus to settle");

            Assert.InRange(sandboxes.MaxLive, 1, 2);
            using var probe = db.OpenStore();
            var sessions = probe.Sessions();
            Assert.Equal([("stopped", BusEngine.StoppedReason), ("ended", (string?)null)], sessions.Select(s => (s.State, s.Reason)));
            Assert.True(string.CompareOrdinal(sessions[1].StartedAt, sessions[1].QueuedAt) > 0, "b1's session waited for its slot");
            var load = (await engine.SchedulerSnapshotAsync(Ct)).Teams.Single(t => t.Team == "Beta");
            Assert.Equal(1, load.Admitted);
            Assert.True(load.MaxWaitMs > 0);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    /// <summary>
    /// A slot is given back however a sandbox ends: Stop, the wall-clock timeout, the idle watchdog (both reported by
    /// the launcher's supervisor, stood in for here), a crash (the runner throws). The card's failure class is kept.
    /// </summary>
    [Fact]
    public async Task StopTimeoutIdleAndCrashEachFreeTheSlot_AndRecordTheFailureClass()
    {
        using var db = new TempDb();
        await using var sandboxes = new StandInSandboxes();
        var runner = new ScriptedRunner();
        async Task<RunResult> Hold(RunContext run, CancellationToken ct, Func<RunResult> after, TimeSpan? holdFor = null)
        {
            var sandbox = sandboxes.Start();
            try
            {
                if (holdFor is { } h) await Task.Delay(h, ct);
                else await Task.Delay(Timeout.Infinite, ct);
                return after();
            }
            finally
            {
                StandInSandboxes.End(sandbox); // the launcher kills the sandbox tree before the run ends
            }
        }
        runner.On("Alpha/stop", (run, ct) => Hold(run, ct, () => RunResult.Ok))
            .On("Alpha/slow", (run, ct) => Hold(run, ct, () => new RunResult(false, "wall-clock timeout (15 min)", FailureClasses.Timeout), TimeSpan.FromMilliseconds(200)))
            .On("Alpha/quiet", (run, ct) => Hold(run, ct, () => new RunResult(false, "idle: no progress for 10 min", FailureClasses.Idle), TimeSpan.FromMilliseconds(200)))
            .On("Alpha/crash", (run, ct) => Hold(run, ct, () => throw new InvalidOperationException("the sandbox died"), TimeSpan.FromMilliseconds(200)))
            .On("Alpha/last", (run, ct) => Hold(run, ct, () => RunResult.Ok, TimeSpan.FromMilliseconds(100)));
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { MaxConcurrentAgents = () => 1, SafetyPoll = TimeSpan.FromSeconds(30) });
        try
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            var order = new[] { "stop", "slow", "quiet", "crash", "last" };
            foreach (var n in order) await engine.AddMemberAsync("Alpha", n, ct: Ct);
            foreach (var n in order) await engine.PersonTellAsync("Alpha", n, "job " + n, ct: Ct);
            await engine.StartAsync(Ct);

            await Wait.UntilAsync(() => runner.AdmittedNames().Count == 1 && sandboxes.CountLive() == 1, "stop running");
            Assert.False(await engine.StopMemberAsync("Alpha", "last", Ct)); // queued, not running: nothing to stop
            Assert.True(await engine.StopMemberAsync("Alpha", "stop", Ct));
            // The safety poll is 30 s: every hand-over below is the run's own end freeing the slot at once.
            await Wait.UntilAsync(async () => await engine.IsQuiescentAsync(Ct) && runner.AdmittedNames().Count(n => n != "Alpha/Manager") == 5,
                "all five ran", TimeSpan.FromSeconds(20));

            Assert.True(sandboxes.MaxLive <= 1, $"{sandboxes.MaxLive} stand-in sandboxes alive at once under a limit of 1");
            Assert.Equal(0, sandboxes.CountLive());
            using var probe = db.OpenStore();
            var byMember = probe.Runs().Where(r => probe.Member(r.MemberId)!.Name != "Manager")
                .ToDictionary(r => probe.Member(r.MemberId)!.Name, r => (r.State, Class: probe.FailureClassOf(r.Id), r.Reason));
            Assert.Equal(("failed", FailureClasses.Stopped, BusEngine.StoppedReason), byMember["stop"]);
            Assert.Equal(("failed", FailureClasses.Timeout, "wall-clock timeout (15 min)"), byMember["slow"]);
            Assert.Equal(("failed", FailureClasses.Idle, "idle: no progress for 10 min"), byMember["quiet"]);
            Assert.Equal(("failed", FailureClasses.Failed, "the runner failed: the sandbox died"), byMember["crash"]);
            Assert.Equal(("completed", (string?)null, (string?)null), byMember["last"]);
            // The Manager's notices carry the same class.
            var failed = probe.AllMessages().Where(m => m.Type == MessageTypes.Failed).Select(m => Payload.Field(m, "failureClass")).ToArray();
            Assert.Equal([FailureClasses.Stopped, FailureClasses.Timeout, FailureClasses.Idle, FailureClasses.Failed], failed);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    /// <summary>Raising the limit admits at once without disturbing running sandboxes; lowering it stops nobody.</summary>
    [Fact]
    public async Task ChangingTheLimitTakesEffectAtOnce_AndNeverStopsARunningSandbox()
    {
        using var db = new TempDb();
        var limit = 3;
        var gates = new Gates();
        var cancelled = 0;
        var runner = new ScriptedRunner
        {
            Default = async (run, ct) =>
            {
                try
                {
                    await gates.Of(run.Member.Qualified).WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    throw;
                }
                return RunResult.Ok;
            },
        };
        // A 30 s safety poll: only Reschedule can make the raised limit admit within the test's wait.
        var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { MaxConcurrentAgents = () => Volatile.Read(ref limit), SafetyPoll = TimeSpan.FromSeconds(30) });
        try
        {
            await engine.CreateTeamAsync("Alpha", ct: Ct);
            await engine.CreateTeamAsync("Beta", ct: Ct);
            foreach (var (team, m) in new[] { ("Alpha", "a1"), ("Beta", "b1"), ("Alpha", "a2"), ("Beta", "b2"), ("Alpha", "a3"), ("Beta", "b3") })
            {
                await engine.AddMemberAsync(team, m, ct: Ct);
                await engine.PersonTellAsync(team, m, "job " + m, ct: Ct);
            }
            await engine.StartAsync(Ct);
            await Wait.UntilAsync(() => runner.AdmittedNames().Count == 3, "3 admitted");
            var header = await engine.SchedulerSnapshotAsync(Ct);
            Assert.Equal((3, 3, 3), (header.Running, header.Limit, header.Queued));
            Assert.Equal([("Alpha", 2, 1), ("Beta", 1, 2)], header.Teams.Select(t => (t.Team, t.Running, t.Queued))); // running a1,a2,b1; queued b2,a3,b3

            Volatile.Write(ref limit, 4);
            engine.Reschedule();
            await Wait.UntilAsync(() => runner.AdmittedNames().Count == 4, "one more admitted at once", TimeSpan.FromSeconds(5));
            Assert.Equal(["Alpha/a1", "Beta/b1", "Alpha/a2", "Beta/b2"], runner.AdmittedNames());
            Assert.Equal(0, cancelled);

            Volatile.Write(ref limit, 2);
            engine.Reschedule();
            await Task.Delay(300, Ct);
            Assert.Equal(4, (await engine.SchedulerSnapshotAsync(Ct)).Running); // over the new limit, and nobody stopped
            Assert.Equal(0, cancelled);
            gates.Open("Alpha/a1");
            gates.Open("Beta/b1");
            await Task.Delay(300, Ct);
            Assert.Equal(4, runner.AdmittedNames().Count); // 2 still running = the new limit: nobody admitted
            gates.Open("Alpha/a2");
            await Wait.UntilAsync(() => runner.AdmittedNames().Count == 5, "a3 admitted under the new limit");
            Assert.Equal("Alpha/a3", runner.AdmittedNames()[4]);
        }
        finally
        {
            gates.OpenAll();
            await engine.DisposeAsync();
        }
    }

    /// <summary>
    /// Restart: queued runs keep their order; a queued interactive session is dropped (its window is gone); an open
    /// session and an open run are recorded interrupted.
    /// </summary>
    [Fact]
    public async Task ARestartKeepsQueuedRunsInOrder_DropsQueuedSessions_AndInterruptsOpenOnes()
    {
        using var db = new TempDb();
        var hang = new ScriptedRunner { Default = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return RunResult.Ok; } };
        var engine = new BusEngine(db.OpenStore(), hang, new BusOptions { MaxConcurrentAgents = () => 2, SafetyPoll = TimeSpan.FromMilliseconds(100) });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        foreach (var n in new[] { "s1", "r1", "s2", "r2", "r3" }) await engine.AddMemberAsync("Alpha", n, ct: Ct);
        await engine.StartAsync(Ct);
        var s1 = await engine.RequestSessionAsync("Alpha", "s1", t => Task.Delay(Timeout.Infinite, t), Ct);
        await s1.Admission.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await engine.PersonTellAsync("Alpha", "r1", "job r1", ct: Ct);
        await Wait.UntilAsync(() => hang.AdmittedNames().Count == 1, "r1 admitted");
        var s2 = await engine.RequestSessionAsync("Alpha", "s2", t => Task.Delay(Timeout.Infinite, t), Ct);
        await engine.PersonTellAsync("Alpha", "r2", "job r2", ct: Ct);
        await engine.PersonTellAsync("Alpha", "r3", "job r3", ct: Ct);
        await Wait.UntilAsync(async () => (await engine.SchedulerSnapshotAsync(Ct)).Queued == 3, "s2, r2, r3 queued");
        await engine.DisposeAsync(); // as a process exit: nothing recorded

        Assert.False(await s2.Admission);
        using var probe = db.OpenStore();
        Assert.Equal([("s2", "interactive"), ("r2", "run"), ("r3", "run")], probe.Queue().Select(q => (probe.Member(q.MemberId)!.Name, q.Kind)));

        var after = new ScriptedRunner { Default = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return RunResult.Ok; } };
        engine = new BusEngine(db.OpenStore(), after, new BusOptions { MaxConcurrentAgents = () => 1, SafetyPoll = TimeSpan.FromMilliseconds(100) });
        try
        {
            await engine.StartAsync(Ct);
            await Wait.UntilAsync(() => after.AdmittedNames().Count == 1, "first after restart");
            Assert.Equal(["Alpha/r2"], after.AdmittedNames());
            var snap = await engine.SchedulerSnapshotAsync(Ct);
            // Behind r2: r3, then the Manager woken by r1's interruption.
            Assert.Equal(["r3", "Manager"], snap.Queue.Select(q => q.Member));
            var session = Assert.Single(probe.Sessions());
            Assert.Equal(("interrupted", "s1"), (session.State, probe.Member(session.MemberId)!.Name));
            var r1 = probe.Runs().Single(r => probe.Member(r.MemberId)!.Name == "r1");
            Assert.Equal(("interrupted", FailureClasses.Interrupted), (r1.State, probe.FailureClassOf(r1.Id)));
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    /// <summary>
    /// The real core over IPC: scheduler.getState gives the shell the header, the queue labels and per-team counts.
    /// The core runs the bus with fake runners (YAWBLE_RUNNER=fake-hang); its limit is the saved global policy's
    /// app.maxConcurrentAgents (3). The card's Stop (runs.stop) frees a slot for the head of the queue.
    /// </summary>
    [Fact]
    public async Task TheCoreServesTheSchedulerStateOverIpc_WithThePolicysLimit_AndStopFreesASlot()
    {
        using var db = new TempDb();
        new Yawble.Core.Policy.PolicyStore(db.ConnectionString).SaveGlobal(
            Yawble.Core.Policy.GlobalPolicy.Default with { App = new Yawble.Core.Policy.AppPolicy(3, "default") });
        var seeding = new BusEngine(db.OpenStore(), new UnavailableRunner());
        await seeding.CreateTeamAsync("Alpha", ct: Ct);
        await seeding.CreateTeamAsync("Beta", ct: Ct);
        foreach (var (team, m) in new[] { ("Alpha", "a1"), ("Beta", "b1"), ("Alpha", "a2"), ("Beta", "b2"), ("Alpha", "a3"), ("Beta", "b3") })
        {
            await seeding.AddMemberAsync(team, m, ct: Ct);
            await seeding.PersonTellAsync(team, m, "job " + m, ct: Ct);
        }
        await seeding.DisposeAsync();

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var address = IpcEndpoint.NewAddress(db.Dir);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "yawble-core.dll"), "--ipc", address, "--data-dir", db.Dir },
            Environment = { ["YAWBLE_IPC_TOKEN"] = token, [BusHost.RunnerVariable] = "fake-hang" },
        };
        CoreLaunch.IsolateCompanionHome(start); // never the real %APPDATA%\yawble-companion
        using var core = Process.Start(start)!;
        core.ErrorDataReceived += (_, e) => { if (e.Data is not null) TestContext.Current.SendDiagnosticMessage("core: " + e.Data); };
        core.BeginErrorReadLine();
        try
        {
            var ready = await core.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal($"YAWBLE-READY {address}", ready);
            await using var client = await TestIpcClient.ConnectAsync(address, Ct);
            await client.AuthAsync(token, Ct);

            JsonNode state = null!;
            await Wait.UntilAsync(async () =>
            {
                state = (await client.CallAsync("scheduler.getState", null, Ct))!["result"]!;
                return state["running"]!.GetValue<int>() == 3 && state["queued"]!.GetValue<int>() == 3;
            }, "3 running, 3 queued over IPC", TimeSpan.FromSeconds(30));
            TestContext.Current.SendDiagnosticMessage("scheduler.getState: " + state.ToJsonString());

            Assert.Equal(3, state["limit"]!.GetValue<int>());
            static IEnumerable<(string, string, string)> Queue(JsonNode s) => s["queue"]!.AsArray()
                .Select(q => (q!["team"]!.GetValue<string>(), q["member"]!.GetValue<string>(), q["label"]!.GetValue<string>()));
            Assert.Equal(
                [("Beta", "b2", "Queued · position 1 of 3"), ("Alpha", "a3", "Queued · position 2 of 3"), ("Beta", "b3", "Queued · position 3 of 3")],
                Queue(state));
            var teams = state["teams"]!.AsArray().Select(t => (t!["team"]!.GetValue<string>(), t["running"]!.GetValue<int>(), t["queued"]!.GetValue<int>()));
            Assert.Equal([("Alpha", 2, 1), ("Beta", 1, 2)], teams);
            Assert.Equal(["a1", "b1", "a2"], state["live"]!.AsArray().Select(l => l!["member"]!.GetValue<string>()));

            // Stop on a1's card: its slot goes to the head of the queue (b2), and everyone behind moves up.
            var stop = (await client.CallAsync("runs.stop", new JsonObject { ["team"] = "Alpha", ["member"] = "a1" }, Ct))!["result"]!;
            Assert.True(stop["stopped"]!.GetValue<bool>());
            await Wait.UntilAsync(async () =>
            {
                state = (await client.CallAsync("scheduler.getState", null, Ct))!["result"]!;
                return state["live"]!.AsArray().Any(l => l!["member"]!.GetValue<string>() == "b2");
            }, "b2 admitted after Stop", TimeSpan.FromSeconds(30));
            TestContext.Current.SendDiagnosticMessage("after Stop: " + state.ToJsonString());
            Assert.Equal(["b1", "a2", "b2"], state["live"]!.AsArray().Select(l => l!["member"]!.GetValue<string>()));
            // a1's stopped run is a notice to Alpha's Manager, which joins the back of the one queue.
            Assert.Equal(
                [("Alpha", "a3", "Queued · position 1 of 3"), ("Beta", "b3", "Queued · position 2 of 3"), ("Alpha", "Manager", "Queued · position 3 of 3")],
                Queue(state));

            core.StandardInput.Close();
            await core.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);
        }
        finally
        {
            if (!core.HasExited) core.Kill(entireProcessTree: true);
        }
    }

    private static IReadOnlyList<string> MemberRuns(ScriptedRunner runner) =>
        runner.AdmittedNames().Where(n => !n.EndsWith("/Manager", StringComparison.Ordinal)).ToArray();

    private sealed class Gates
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);

        public Task Of(string key)
        {
            lock (_gates) return (_gates.TryGetValue(key, out var g) ? g : _gates[key] = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        public void Open(string key)
        {
            Of(key);
            lock (_gates) _gates[key].TrySetResult();
        }

        public void OpenAll()
        {
            lock (_gates) foreach (var g in _gates.Values) g.TrySetResult();
        }
    }
}
