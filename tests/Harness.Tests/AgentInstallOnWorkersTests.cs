using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// IN CONTROL, WHETHER AN AGENT CLI IS INSTALLED IS THE WORKERS' ANSWER. The probe answers from what
/// each placeable worker measured (<see cref="WorkerInstalls"/>) and never from control's own PATH:
/// installed on the workers that said so, not installed on the ones that did not - both named when
/// they disagree - and not measured when no counted worker has answered. In <c>all</c> it answers
/// from this machine's PATH, as before.
///
/// EVERY COMMAND HERE INVERTS CONTROL'S OWN PATH: a worker that measured a command installed measures
/// one that is on no PATH (<see cref="Absent"/>), and one that measured it missing measures <c>sh</c>,
/// which is on every PATH. An answer that followed control's own PATH reads the opposite. No CLI is
/// run: the workers are recorded answers or a fake that answers the sign-in probe.
/// </summary>
public sealed class AgentInstallOnWorkersTests
{
    /// <summary>On no PATH, here or as root.</summary>
    private const string Absent = "no-such-cli-7f3a9c";

    /// <summary>On every PATH.</summary>
    private const string OnPath = "sh";

    private static readonly WorkerId W1 = new("worker-1");
    private static readonly WorkerId W2 = new("worker-2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void PathIsAsAssumed()
    {
        Assert.NotNull(PathSearch.Find(OnPath));
        Assert.Null(PathSearch.Find(Absent));
    }

    private static AgentDefinition Preset(string name, string command, bool model = true) =>
        new(name, AgentMode.Headless, new AgentLaunch(command, [], LanguageModel: model));

    private static WorkerInstalls Connected(params WorkerId[] workers) => new(() => workers);

    private static AgentInstallProbe Control(WorkerInstalls installs) => new(workers: installs);

    [Fact]
    public void A_command_every_connected_worker_measured_installed_reads_installed_on_them()
    {
        PathIsAsAssumed();
        var installs = Connected(W1, W2);
        installs.Record(W1, [(Absent, true)]);
        installs.Record(W2, [(Absent, true)]);

        var answer = Control(installs).Probe(Preset("absent-headless", Absent));

        Assert.Null(answer.State);
        Assert.Null(answer.ResolvedPath);
        Assert.Equal($"{Absent} is installed on worker-1 and worker-2.", answer.Message);
        Assert.Equal([("worker-1", true), ("worker-2", true)], answer.MeasuredOn!.Select(m => (m.Worker, m.Installed)));
    }

    [Fact]
    public void A_command_a_worker_measured_missing_reads_not_installed_on_that_worker()
    {
        PathIsAsAssumed();
        var installs = Connected(W1);
        installs.Record(W1, [(OnPath, false)]);

        var answer = Control(installs).Probe(Preset("sh-headless", OnPath));

        Assert.Equal(AgentInstallStates.NotInstalled, answer.State);
        Assert.Null(answer.ResolvedPath);
        Assert.Equal("sh is not installed on worker-1.", answer.Message);
    }

    [Fact]
    public void Two_connected_workers_that_disagree_are_both_named()
    {
        PathIsAsAssumed();
        var installs = Connected(W2, W1);
        installs.Record(W1, [(OnPath, true)]);
        installs.Record(W2, [(OnPath, false)]);

        var answer = Control(installs).Probe(Preset("sh-headless", OnPath));

        Assert.Equal(AgentInstallStates.NotInstalled, answer.State);
        Assert.Equal("sh is installed on worker-1 but not on worker-2: the workers disagree.", answer.Message);
        Assert.Equal([("worker-1", true), ("worker-2", false)], answer.MeasuredOn!.Select(m => (m.Worker, m.Installed)));
    }

    [Fact]
    public void A_dropped_workers_answer_no_longer_counts()
    {
        PathIsAsAssumed();
        WorkerId[] connected = [W1, W2];
        var installs = new WorkerInstalls(() => connected);
        installs.Record(W1, [(Absent, true)]);
        installs.Record(W2, [(Absent, false)]);
        var probe = Control(installs);
        Assert.Equal(AgentInstallStates.NotInstalled, probe.Probe(Preset("absent-headless", Absent)).State);

        connected = [W1];

        var answer = probe.Probe(Preset("absent-headless", Absent));
        Assert.Null(answer.State);
        Assert.Equal($"{Absent} is installed on worker-1.", answer.Message);
    }

    [Fact]
    public void A_newer_answer_from_the_same_worker_replaces_the_older()
    {
        PathIsAsAssumed();
        var installs = Connected(W1);
        installs.Record(W1, [(Absent, false)]);
        installs.Record(W1, [(Absent, true)]);

        var answer = Control(installs).Probe(Preset("absent-headless", Absent));

        Assert.Null(answer.State);
        Assert.Equal([("worker-1", true)], answer.MeasuredOn!.Select(m => (m.Worker, m.Installed)));
    }

    [Fact]
    public void A_connected_worker_that_has_not_answered_reads_not_measured()
    {
        PathIsAsAssumed();
        var installs = Connected(W1);
        installs.Record(W1, [("some-other-cli", true)]);
        var probe = Control(installs);

        foreach (var command in new[] { OnPath, Absent })
        {
            var answer = probe.Probe(Preset(command + "-headless", command));
            Assert.Null(answer.State);
            Assert.Null(answer.ResolvedPath);
            Assert.Empty(answer.MeasuredOn!);
            Assert.Equal($"{command} has not been measured: no worker has reported whether it is installed.", answer.Message);
        }
    }

    [Fact]
    public void With_no_worker_connected_every_command_reads_not_measured()
    {
        PathIsAsAssumed();
        var installs = Connected();
        installs.Record(W1, [(OnPath, false), (Absent, true)]);
        var probe = Control(installs);

        foreach (var command in new[] { OnPath, Absent })
        {
            var answer = probe.Probe(Preset(command + "-headless", command));
            Assert.Null(answer.State);
            Assert.Null(answer.ResolvedPath);
            Assert.Empty(answer.MeasuredOn!);
            Assert.Equal($"{command} has not been measured: no worker is connected.", answer.Message);
            Assert.Null(probe.Measured(Preset(command + "-headless", command)));
            Assert.False(probe.Installed(Preset(command + "-headless", command)));
        }
    }

    [Fact]
    public void Over_a_real_worker_pool_a_dropped_worker_stops_counting_and_counts_again_when_it_rejoins()
    {
        PathIsAsAssumed();
        var pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0));
        var installs = WorkerInstalls.Over(pool);
        var probe = Control(installs);
        var sh = Preset("sh-headless", OnPath);

        Assert.Equal("sh has not been measured: no worker is connected.", probe.Probe(sh).Message);

        var w1 = new ScriptedCliWorker("worker-1");
        pool.Join(w1, new WorkerInfo(W1, "v", 4, (long)8e9, DateTimeOffset.UtcNow));
        installs.Record(W1, [(OnPath, false)]);
        Assert.Equal("sh is not installed on worker-1.", probe.Probe(sh).Message);

        pool.Drop(W1);
        Assert.Null(probe.Probe(sh).State);
        Assert.Equal("sh has not been measured: no worker is available to run agents.", probe.Probe(sh).Message);

        pool.Back(W1);
        Assert.Equal("sh is not installed on worker-1.", probe.Probe(sh).Message);

        pool.Leave(w1);
        Assert.Equal("sh has not been measured: no worker is connected.", probe.Probe(sh).Message);

        pool.Join(w1, new WorkerInfo(W1, "v", 4, (long)8e9, DateTimeOffset.UtcNow));
        Assert.Equal("sh is not installed on worker-1.", probe.Probe(sh).Message);
    }

    /// <summary>
    /// A DRAINING WORKER STOPS COUNTING: runs will not land on it, so its PATH says nothing about where
    /// the next run executes. With every worker draining, nothing is measured - and the words do not
    /// claim that no worker is connected.
    /// </summary>
    [Fact]
    public void A_draining_workers_answer_stops_counting_and_the_words_do_not_say_no_worker_is_connected()
    {
        PathIsAsAssumed();
        var pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0));
        var installs = WorkerInstalls.Over(pool);
        var probe = Control(installs);
        var sh = Preset("sh-headless", OnPath);
        pool.Join(new ScriptedCliWorker("worker-1"), new WorkerInfo(W1, "v", 4, (long)8e9, DateTimeOffset.UtcNow));
        pool.Join(new ScriptedCliWorker("worker-2"), new WorkerInfo(W2, "v", 4, (long)8e9, DateTimeOffset.UtcNow));
        installs.Record(W1, [(OnPath, true)]);
        installs.Record(W2, [(OnPath, false)]);
        Assert.Equal("sh is installed on worker-1 but not on worker-2: the workers disagree.", probe.Probe(sh).Message);

        pool.Drain(W2, true);
        Assert.Null(probe.Probe(sh).State);
        Assert.Equal("sh is installed on worker-1.", probe.Probe(sh).Message);

        pool.Drain(W1, true);
        var answer = probe.Probe(sh);
        Assert.Null(answer.State);
        Assert.Empty(answer.MeasuredOn!);
        Assert.Equal("sh has not been measured: no worker is available to run agents.", answer.Message);
        Assert.DoesNotContain("no worker is connected", answer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void In_all_the_probe_answers_from_this_machines_path_as_before()
    {
        PathIsAsAssumed();
        var probe = new AgentInstallProbe();

        var sh = probe.Probe(Preset("sh-headless", OnPath));
        Assert.Null(sh.State);
        Assert.Equal(PathSearch.Find(OnPath), sh.ResolvedPath);
        Assert.Equal("sh resolves on this machine's PATH.", sh.Message);
        Assert.Null(sh.MeasuredOn);

        var absent = probe.Probe(Preset("absent-headless", Absent));
        Assert.Equal(AgentInstallStates.NotInstalled, absent.State);
        Assert.Equal($"{Absent} was not found on this machine's PATH.", absent.Message);
        Assert.Null(absent.MeasuredOn);
    }

    // ---------------------------------------------------------------------------------------------
    // The solution install's default agent.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_solution_default_is_the_first_model_preset_a_worker_measured_installed()
    {
        PathIsAsAssumed();
        var catalog = new AgentCatalog([Preset("first-headless", OnPath), Preset("second-headless", Absent)]);
        var installs = Connected(W1);
        installs.Record(W1, [(OnPath, false), (Absent, true)]);

        Assert.Equal("second-headless", SolutionDefaultAgent.Of(catalog, Control(installs)));
    }

    [Fact]
    public void With_nothing_measured_the_solution_default_is_the_first_model_preset_as_before()
    {
        PathIsAsAssumed();
        var catalog = new AgentCatalog([Preset("first-headless", Absent), Preset("second-headless", OnPath)]);

        Assert.Equal("first-headless", SolutionDefaultAgent.Of(catalog, Control(Connected(W1))));
    }

    [Fact]
    public void In_all_the_solution_default_resolves_on_this_machines_path_as_before()
    {
        PathIsAsAssumed();
        var catalog = new AgentCatalog([Preset("absent-headless", Absent), Preset("sh-headless", OnPath)]);

        Assert.Equal("sh-headless", SolutionDefaultAgent.Of(catalog, new AgentInstallProbe()));
    }

    // ---------------------------------------------------------------------------------------------
    // The measurement taken as a worker joins. Wire is called directly with a fake worker: no Host
    // and no real CLI.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_worker_that_joins_is_asked_about_every_command_the_catalog_launches_including_issued_ones()
    {
        // `issued-cli` is launched only by a preset that signs in with an issued credential: the
        // sign-in probe never asks about it, so only the join measures it.
        var catalog = new AgentCatalog(
        [
            Preset("home-headless", OnPath),
            new AgentDefinition("home", AgentMode.Interactive, new AgentLaunch(OnPath, [])),
            Preset("issued-headless", "issued-cli"),
        ]);
        var commands = MeasureInstallsWhenAWorkerJoins.Commands(catalog);
        Assert.Equal([OnPath, "issued-cli"], commands);

        var worker = new ScriptedCliWorker("worker-2", message => message is ProbeSignIn probe
            ? new SignInProbed(probe.Request, [.. probe.Commands.Select(c => new SignInProbeResult(c.Command, c.Command == "issued-cli", null, "from worker-2"))])
            : null);
        var asks = new WorkerAsks(() => worker);
        worker.Asks = asks;
        var installs = Connected(W1, W2);
        Action<WorkerId>? joined = null;

        Assert.True(MeasureInstallsWhenAWorkerJoins.Wire(
            control: true, onJoined: j => joined += j, id => id == W2 ? worker : null, () => commands, asks, installs));
        joined!(W2);

        await Until(() => installs.For("issued-cli").Count > 0);
        Assert.Equal([("worker-2", true)], installs.For("issued-cli").Select(m => (m.Worker, m.Installed)));
        Assert.Equal([("worker-2", false)], installs.For(OnPath).Select(m => (m.Worker, m.Installed)));

        var asked = Assert.IsType<ProbeSignIn>(Assert.Single(worker.Sent));
        Assert.Equal([OnPath, "issued-cli"], asked.Commands.Select(c => c.Command));
    }

    [Fact]
    public async Task A_joining_worker_that_does_not_answer_records_nothing_and_an_earlier_answer_stands()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var silent = new ScriptedCliWorker("worker-2");
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        silent.Received += _ => sent.TrySetResult();
        var asks = new WorkerAsks(() => silent, clock);
        silent.Asks = asks;
        var installs = Connected(W2);
        installs.Record(W2, [(OnPath, true)]);

        var measuring = MeasureInstallsWhenAWorkerJoins.MeasureAsync(silent, [OnPath, Absent], asks, installs, ct: Ct);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        clock.Advance(AgentAuthProbe.Bound(2) + TimeSpan.FromSeconds(1));
        await measuring.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal([("worker-2", true)], installs.For(OnPath).Select(m => (m.Worker, m.Installed)));
        Assert.Empty(installs.For(Absent));
    }

    [Fact]
    public void In_all_nothing_is_wired_for_a_join()
    {
        var wired = false;

        Assert.False(MeasureInstallsWhenAWorkerJoins.Wire(
            control: false, onJoined: _ => wired = true, _ => null, () => [OnPath],
            new WorkerAsks(() => throw new InvalidOperationException("none")), Connected()));
        Assert.False(wired);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The join measurement never recorded an answer.");
            await Task.Delay(20, Ct);
        }
    }
}
