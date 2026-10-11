using System.Collections.Concurrent;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>A worker's capacity sample names every run it has open, however many one member has.</summary>
public sealed class WorkerCapacitySampleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-worker-sample-").FullName;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        File.WriteAllText(Path.Combine(_root, "go"), "");
        MemberTempCleanup.Remove(_root);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A child still leaving; the temp directory is the system's to clear.
        }
    }

    [Fact]
    public async Task A_sample_lists_every_open_run_of_a_member()
    {
        var member = new ContainerId("alpha", "sampled-twice");
        var events = new Events();
        var heartbeat = new RunHeartbeat();
        var host = new WorkerHost(
            new WorkerId("w1"), events, new RunLauncher(heartbeat, reports: false, lookup: LaunchLookup.Once), heartbeat);

        var go = Path.Combine(_root, "go");
        var script = Path.Combine(_root, "wait.sh");
        await File.WriteAllTextAsync(script, $"i=0; while [ ! -e '{go}' ] && [ $i -lt 400 ]; do sleep 0.05; i=$((i+1)); done\n", Ct);

        var first = Start(member, script);
        var second = Start(member, script);
        await host.ApplyAsync(first, Ct);
        await host.ApplyAsync(second, Ct);
        await host.ApplyAsync(new SampleCapacity(), Ct);

        var sampled = Assert.Single(events.Published.Select(e => e.Event).OfType<WorkerCapacitySampled>());
        Assert.Equal(
            new[] { first.Run, second.Run }.OrderBy(r => r.Nonce),
            sampled.OpenRuns.OrderBy(r => r.Nonce));

        await File.WriteAllTextAsync(go, "", Ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (host.OpenRuns().Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        Assert.Empty(host.OpenRuns());
    }

    [Fact]
    public async Task A_concierge_terminals_memory_is_shown_under_its_worker()
    {
        await using var terminals = await TerminalBed.StartAsync(_root, pid: 7001, residentPages: 1500);
        await terminals.OpenAsync("user-1");

        var sample = await terminals.SampledAsync(hold => hold.ResidentBytes is not null);

        var worker = Assert.Single(sample);
        Assert.Equal("w1", worker.Id);
        var hold = Assert.Single(worker.Terminals);
        Assert.Equal("user-1", hold.User);
        Assert.Equal(1500 * 4096L, hold.ResidentBytes);
        Assert.Equal(1, hold.Processes);
        Assert.NotNull(hold.SampledAt);
        Assert.Empty(worker.Runs);
    }

    [Fact]
    public async Task A_terminal_whose_group_is_unreadable_is_not_measured_never_zero()
    {
        // The fixture /proc holds no process of the terminal's group.
        await using var terminals = await TerminalBed.StartAsync(_root, pid: 7002, residentPages: null);
        await terminals.OpenAsync("user-1");

        for (var i = 0; i < 5; i++) await terminals.Sampler.SampleAsync(Ct);
        var hold = Assert.Single(Assert.Single(terminals.Workers()).Terminals);

        Assert.Equal("user-1", hold.User);
        Assert.Null(hold.ResidentBytes);
        Assert.Null(hold.Processes);
        Assert.Null(hold.SampledAt);
    }

    [Fact]
    public async Task An_ended_terminal_leaves_its_worker_at_the_next_sample()
    {
        await using var terminals = await TerminalBed.StartAsync(_root, pid: 7003, residentPages: 800);
        await terminals.OpenAsync("user-1");
        await terminals.SampledAsync(hold => hold.ResidentBytes is not null);

        await terminals.Store.EndAsync(new ConciergeSessionKey("user-1"));
        FixtureProc.Remove(terminals.Proc, 7003);
        await terminals.Sampler.SampleAsync(Ct);

        Assert.Empty(Assert.Single(terminals.Workers()).Terminals);
        Assert.Empty(terminals.Sampler.TerminalsOn(new WorkerId("w1")));
    }

    /// <summary>
    /// A worker joined to control over a real socket, its terminals on a fake engine whose children
    /// report a fixture pid, measured over a fixture /proc; control's session store, sampler and
    /// workers view over it, as Program wires them.
    /// </summary>
    private sealed class TerminalBed : IAsyncDisposable
    {
        private readonly WorkerStreamBed _bed;

        private TerminalBed(WorkerStreamBed bed, string proc)
        {
            _bed = bed;
            Proc = proc;
            Sampler = new CapacitySampler(
                () => [(bed.Remote, new HeadroomGate(() => 80, () => 0))], bed.Wip, new NoHeavyLease(), () => 80, () => 0);
            bed.Events = Sampler.HandleAsync;
            Store = new ConciergeSessionStore(
                bed.Engine,
                (_, _, _, _) => Task.FromResult(bed.Engine.Stage(new TerminalLaunch(
                    ["fixture-cli"], proc, new Dictionary<string, string>(), [], null, null, null))),
                (_, _) => Task.CompletedTask);
        }

        public string Proc { get; }

        public CapacitySampler Sampler { get; }

        public ConciergeSessionStore Store { get; }

        public static async Task<TerminalBed> StartAsync(string root, int pid, long? residentPages)
        {
            var proc = Directory.CreateDirectory(Path.Combine(root, $"proc-{pid}")).FullName;
            if (residentPages is { } pages) FixtureProc.Write(proc, pid, pid, pages);

            var bed = new WorkerStreamBed(processes: new ProcessGroupReader(proc, 4096));
            bed.Pty.ProcessId = pid;
            var terminals = new TerminalBed(bed, proc);
            await bed.StartAsync();
            return terminals;
        }

        public async Task OpenAsync(string user)
        {
            var count = _bed.Pty.Spawned.Count;
            await Store.AttachAsync(new ConciergeSessionKey(user), "", 80, 24, TestContext.Current.CancellationToken);
            await WorkerStreamBed.Until(() => _bed.Pty.Spawned.Count == count + 1);
        }

        public IReadOnlyList<WorkerSample> Workers() =>
            WorkersView.Of(_bed.Pool, _bed.Wip, "v", worker => ConciergeSessionsView.TerminalsOn(Store, worker, Sampler.Terminal));

        /// <summary>Samples until every terminal listed passes <paramref name="measured"/>; the worker records a terminal just after spawning it.</summary>
        public async Task<IReadOnlyList<WorkerSample>> SampledAsync(Func<TerminalHold, bool> measured)
        {
            for (var i = 0; i < 100; i++)
            {
                await Sampler.SampleAsync(TestContext.Current.CancellationToken);
                var workers = Workers();
                if (workers.SelectMany(w => w.Terminals) is var holds && holds.Any() && holds.All(measured)) return workers;
            }

            throw new TimeoutException("The terminal was never measured.");
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            await _bed.DisposeAsync();
        }
    }

    private StartRun Start(ContainerId member, string script) =>
        new(RunId.For(member), "probe", "You are a probe.", "hello", "", Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName,
            new Dictionary<string, string>(), null,
            new RunLaunch("sh", [script], null, null, null, false, null, null, null, []), null, null, null);

    private sealed class Events : IRunEvents
    {
        public ConcurrentQueue<WorkerEnvelope> Published { get; } = new();

        public Task PublishAsync(WorkerEnvelope envelope, CancellationToken ct = default)
        {
            Published.Enqueue(envelope);
            return Task.CompletedTask;
        }
    }
}
