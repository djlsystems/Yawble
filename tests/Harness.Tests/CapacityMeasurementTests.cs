using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// MEASURE: the container's own cgroup files, v2 and v1, and each run's process group in /proc, over
/// fixture trees. A figure whose file is absent is NOT MEASURED - null and named - never 0.
/// </summary>
public sealed class CapacityMeasurementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-capacity-{Guid.NewGuid():N}");

    public CapacityMeasurementTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A cgroup v2 root as an engine mounts it inside the container.</summary>
    internal static string WriteV2(
        string root,
        string memoryMax = "12884901888",
        long anon = 9_400_000_000,
        long shmem = 1_800_000_000,
        double memorySome10 = 0.5)
    {
        Directory.CreateDirectory(root);
        Write(root, "cgroup.controllers", "cpu io memory pids");
        Write(root, "cpu.max", "800000 100000");
        Write(root, "cpu.stat", "usage_usec 4835781301\nuser_usec 4382900114\nsystem_usec 452881186\nnr_periods 16623\nnr_throttled 1821\nthrottled_usec 99000000\n");
        Write(root, "memory.max", memoryMax);
        Write(root, "memory.current", "12000000000");
        Write(root, "memory.stat", $"anon {anon}\nfile 3879620608\nkernel 1000\nshmem {shmem}\n");
        Write(root, "cpu.pressure", "some avg10=2.50 avg60=1.00 avg300=0.20 total=36734\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\n");
        Write(root, "memory.pressure",
            $"some avg10={memorySome10.ToString(System.Globalization.CultureInfo.InvariantCulture)} avg60=3.00 avg300=1.00 total=500000\nfull avg10=1.00 avg60=0.50 avg300=0.10 total=200000\n");
        Write(root, "pids.current", "312");
        Write(root, "pids.max", "max");
        return root;
    }

    /// <summary>A cgroup v1 root: one directory per controller, cpu and cpuacct mounted together.</summary>
    private static string WriteV1(string root)
    {
        Write(root, "memory/memory.limit_in_bytes", "8589934592");
        Write(root, "memory/memory.usage_in_bytes", "6000000000");
        Write(root, "memory/memory.stat", "cache 100\nrss 200\nshmem 300\ntotal_cache 2000000000\ntotal_rss 3000000000\ntotal_shmem 500000000\n");
        Write(root, "cpu,cpuacct/cpu.cfs_quota_us", "400000");
        Write(root, "cpu,cpuacct/cpu.cfs_period_us", "100000");
        Write(root, "cpu,cpuacct/cpu.stat", "nr_periods 10\nnr_throttled 4\nthrottled_time 7000000\n");
        Write(root, "cpu,cpuacct/cpuacct.usage", "123456789000");
        Write(root, "pids/pids.current", "42");
        Write(root, "pids/pids.max", "4096");
        return root;
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void A_v2_tree_gives_each_figure()
    {
        var figures = new CgroupReader(WriteV2(_root)).Read();

        Assert.Equal("v2", figures.Version);
        Assert.Empty(figures.NotMeasured);

        Assert.Equal(8.0, figures.CpuLimit);
        Assert.False(figures.CpuUnlimited);
        Assert.Equal(4835781301, figures.CpuUsageUsec);
        Assert.Equal(1821, figures.CpuThrottledPeriods);
        Assert.Equal(99000000, figures.CpuThrottledUsec);

        Assert.Equal(12884901888, figures.MemoryLimitBytes);
        Assert.Equal(12000000000, figures.MemoryCurrentBytes);
        Assert.Equal(9_400_000_000, figures.MemoryAnonBytes);
        Assert.Equal(3879620608, figures.MemoryFileBytes);
        Assert.Equal(1_800_000_000, figures.MemoryShmemBytes);
        Assert.Equal(11_200_000_000, figures.MemoryInUseBytes);

        Assert.Equal(2.5, figures.CpuPressure!.Some.Avg10);
        Assert.Equal(0.5, figures.MemoryPressure!.Some.Avg10);
        Assert.Equal(1.0, figures.MemoryPressure.Full!.Avg10);
        Assert.Equal(500000, figures.MemoryPressure.Some.TotalUsec);

        Assert.Equal(312, figures.PidsCurrent);
        Assert.Null(figures.PidsLimit);
        Assert.True(figures.PidsUnlimited);
    }

    [Fact]
    public void A_v2_limit_of_max_is_unlimited_and_measured()
    {
        var figures = new CgroupReader(WriteV2(_root, memoryMax: "max")).Read();

        Assert.Null(figures.MemoryLimitBytes);
        Assert.True(figures.MemoryUnlimited);
        Assert.DoesNotContain("memory.limit", figures.NotMeasured);
    }

    [Theory]
    [InlineData("cpu.max", "cpu.limit")]
    [InlineData("cpu.stat", "cpu.usage")]
    [InlineData("memory.max", "memory.limit")]
    [InlineData("memory.current", "memory.current")]
    [InlineData("memory.stat", "memory.anon")]
    [InlineData("cpu.pressure", "cpu.pressure")]
    [InlineData("memory.pressure", "memory.pressure")]
    [InlineData("pids.current", "pids.current")]
    [InlineData("pids.max", "pids.limit")]
    public void A_v2_file_that_is_absent_is_not_measured_never_zero(string file, string figure)
    {
        WriteV2(_root);
        File.Delete(Path.Combine(_root, file));

        var figures = new CgroupReader(_root).Read();

        Assert.Contains(figure, figures.NotMeasured);
        object? value = figure switch
        {
            "cpu.limit" => figures.CpuLimit,
            "cpu.usage" => figures.CpuUsageUsec,
            "memory.limit" => figures.MemoryLimitBytes,
            "memory.current" => figures.MemoryCurrentBytes,
            "memory.anon" => figures.MemoryAnonBytes,
            "cpu.pressure" => figures.CpuPressure,
            "memory.pressure" => figures.MemoryPressure,
            "pids.current" => figures.PidsCurrent,
            "pids.limit" => figures.PidsLimit,
            _ => throw new InvalidOperationException(figure),
        };
        Assert.Null(value);

        // An absent limit is not measured, which is not the same as unlimited.
        Assert.False(figure == "memory.limit" && figures.MemoryUnlimited);
        Assert.False(figure == "cpu.limit" && figures.CpuUnlimited);
        Assert.False(figure == "pids.limit" && figures.PidsUnlimited);
    }

    [Fact]
    public void Memory_in_use_is_not_measured_when_memory_stat_lacks_shmem()
    {
        WriteV2(_root);
        File.WriteAllText(Path.Combine(_root, "memory.stat"), "anon 9400000000\nfile 3879620608\n");

        var figures = new CgroupReader(_root).Read();

        Assert.Equal(9_400_000_000, figures.MemoryAnonBytes);
        Assert.Null(figures.MemoryShmemBytes);
        Assert.Null(figures.MemoryInUseBytes);
        Assert.Contains("memory.shmem", figures.NotMeasured);
    }

    [Fact]
    public void A_v1_tree_gives_each_figure_and_no_pressure()
    {
        var figures = new CgroupReader(WriteV1(_root)).Read();

        Assert.Equal("v1", figures.Version);
        Assert.Equal(4.0, figures.CpuLimit);
        Assert.Equal(123456789, figures.CpuUsageUsec);
        Assert.Equal(4, figures.CpuThrottledPeriods);
        Assert.Equal(7000, figures.CpuThrottledUsec);

        Assert.Equal(8589934592, figures.MemoryLimitBytes);
        Assert.Equal(6000000000, figures.MemoryCurrentBytes);
        Assert.Equal(3000000000, figures.MemoryAnonBytes);
        Assert.Equal(2000000000, figures.MemoryFileBytes);
        Assert.Equal(500000000, figures.MemoryShmemBytes);

        Assert.Equal(42, figures.PidsCurrent);
        Assert.Equal(4096, figures.PidsLimit);

        // v1 has no pressure for a cgroup, and the machine's /proc/pressure is not this container's.
        Assert.Null(figures.CpuPressure);
        Assert.Null(figures.MemoryPressure);
        Assert.Equal(["cpu.pressure", "memory.pressure"], figures.NotMeasured);
    }

    [Fact]
    public void A_v1_unbounded_limit_is_unlimited_and_an_absent_file_is_not_measured()
    {
        WriteV1(_root);
        File.WriteAllText(Path.Combine(_root, "memory/memory.limit_in_bytes"), "9223372036854771712");
        File.WriteAllText(Path.Combine(_root, "cpu,cpuacct/cpu.cfs_quota_us"), "-1");
        File.Delete(Path.Combine(_root, "memory/memory.usage_in_bytes"));
        File.Delete(Path.Combine(_root, "pids/pids.current"));

        var figures = new CgroupReader(_root).Read();

        Assert.True(figures.MemoryUnlimited);
        Assert.Null(figures.MemoryLimitBytes);
        Assert.True(figures.CpuUnlimited);
        Assert.Null(figures.CpuLimit);
        Assert.Null(figures.MemoryCurrentBytes);
        Assert.Null(figures.PidsCurrent);
        Assert.Contains("memory.current", figures.NotMeasured);
        Assert.Contains("pids.current", figures.NotMeasured);
        Assert.DoesNotContain("memory.limit", figures.NotMeasured);
    }

    [Fact]
    public void No_cgroup_at_all_is_not_measured_in_every_figure()
    {
        var figures = new CgroupReader(Path.Combine(_root, "absent")).Read();

        Assert.Null(figures.Version);
        Assert.Equal(CgroupReader.AllFigures, figures.NotMeasured);
        Assert.Null(figures.MemoryLimitBytes);
        Assert.False(figures.MemoryUnlimited);
        Assert.Null(figures.MemoryInUseBytes);
    }

    /// <summary>A /proc entry: <c>stat</c> with its group and CPU ticks, <c>statm</c> with resident pages.</summary>
    private void WriteProcess(string proc, int pid, string comm, int group, long utime, long stime, long residentPages)
    {
        Write(proc, $"{pid}/stat",
            $"{pid} ({comm}) S 1 {group} {group} 0 -1 4194560 100 0 0 0 {utime} {stime} 0 0 20 0 1 0 12345 1000000 {residentPages}\n");
        Write(proc, $"{pid}/statm", $"9000 {residentPages} 100 10 0 500 0\n");
    }

    [Fact]
    public void A_fake_proc_sums_a_runs_process_group_and_nothing_else()
    {
        var proc = Path.Combine(_root, "proc");
        WriteProcess(proc, 100, "agent-cli", group: 100, utime: 30, stime: 20, residentPages: 1000);
        WriteProcess(proc, 101, "sh", group: 100, utime: 5, stime: 5, residentPages: 200);

        // A command name holding spaces and parentheses: fields are counted from the last ")".
        WriteProcess(proc, 102, "odd (name) x", group: 100, utime: 1, stime: 1, residentPages: 50);

        // Another run's group, and a process in no run.
        WriteProcess(proc, 200, "other", group: 200, utime: 7, stime: 0, residentPages: 300);
        WriteProcess(proc, 300, "stray", group: 300, utime: 99, stime: 99, residentPages: 9999);
        Write(proc, "self/stat", "not a process");
        Write(proc, "meminfo", "MemTotal: 1 kB");

        var sums = new ProcessGroupReader(proc, pageSize: 4096).Read([100, 200, 400]);

        Assert.Equal(new ProcessGroupFigures(3, (1000 + 200 + 50) * 4096L, 30 + 20 + 5 + 5 + 1 + 1), sums[100]);
        Assert.Equal(new ProcessGroupFigures(1, 300 * 4096L, 7), sums[200]);

        // A group with no process in /proc is not measured, never zero.
        Assert.Null(sums[400]);
        Assert.False(sums.ContainsKey(300));
    }

    [Fact]
    public async Task A_terminals_process_group_is_measured_alongside_the_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var proc = Path.Combine(_root, "proc");
        WriteProcess(proc, 100, "agent-cli", group: 100, utime: 30, stime: 20, residentPages: 1000);
        WriteProcess(proc, 500, "concierge-cli", group: 500, utime: 7, stime: 3, residentPages: 2000);
        WriteProcess(proc, 501, "node", group: 500, utime: 1, stime: 1, residentPages: 400);

        var groups = new RunProcessGroups();
        using var run = groups.Register(100, new ContainerId("Alpha", "Developer"));
        var events = new Events();
        var heartbeat = new RunHeartbeat();
        var host = new WorkerHost(
            new WorkerId("w1"), events, new RunLauncher(heartbeat, reports: false), heartbeat,
            processes: new ProcessGroupReader(proc, 4096), groups: groups,
            streaming: new WorkerStreaming(new Sink(), new FakePtyEngine { ProcessId = 500 }, null));

        await host.ApplyAsync(new StartTerminal("terminal:t1", new TerminalLaunch(
            ["fixture-cli"], _root, new Dictionary<string, string>(), [], null, null, null), 80, 24), ct);
        await host.ApplyAsync(new SampleCapacity(), ct);

        var published = events.Published.Select(e => e.Event).ToList();
        var measuredRun = Assert.Single(published.OfType<RunMeasured>());
        Assert.Equal(100, measuredRun.Group);
        var terminal = Assert.Single(published.OfType<TerminalMeasured>());
        Assert.Equal(new TerminalMeasured("terminal:t1", 500, 2, 2400 * 4096L, 12, terminal.At), terminal);

        // Both before the sample that closes them.
        Assert.IsType<WorkerCapacitySampled>(published[^1]);
        await host.ApplyAsync(new StopTerminal("terminal:t1"), ct);
    }

    private sealed class Events : IRunEvents
    {
        public System.Collections.Concurrent.ConcurrentQueue<WorkerEnvelope> Published { get; } = new();

        public Task PublishAsync(WorkerEnvelope envelope, CancellationToken ct = default)
        {
            Published.Enqueue(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class Sink : IRunStreamSink
    {
        public ValueTask<bool> StreamAsync(StreamChunk chunk, CancellationToken ct = default) => ValueTask.FromResult(true);
    }

    [Fact]
    public void The_sampler_names_each_run_by_team_and_member_and_rates_wait_for_a_second_sample()
    {
        var proc = Path.Combine(_root, "proc");
        WriteProcess(proc, 100, "agent-cli", group: 100, utime: 100, stime: 0, residentPages: 1000);
        WriteProcess(proc, 200, "agent-cli", group: 200, utime: 100, stime: 0, residentPages: 3000);

        var groups = new RunProcessGroups();
        using var alpha = groups.Register(100, new ContainerId("Alpha", "Developer"));
        using var beta = groups.Register(200, new ContainerId("Beta", "Tester"));

        var clock = new SteppedClock(DateTimeOffset.Parse("2026-10-01T10:00:00Z"));
        var gate = new HeadroomGate(() => 80, () => 10, clock);
        var wip = new WipLedger(3, gate.Reason);
        using var running = wip.TryEnter(new ContainerId("Alpha", "Developer"));

        var sampler = new CapacitySampler(
            new CgroupReader(WriteV2(Path.Combine(_root, "cgroup"))), new ProcessGroupReader(proc, 4096), groups, wip, gate,
            new NoHeavyLease(), () => 80, () => 10, clock: clock);

        var first = sampler.Sample();
        Assert.Equal(["Beta", "Alpha"], first.TopByMemory.Select(run => run.Team));
        Assert.Equal("Tester", first.TopByMemory[0].Member);
        Assert.Equal(3000 * 4096L, first.TopByMemory[0].ResidentBytes);
        Assert.Null(first.TopByMemory[0].CpuPercent);
        Assert.Empty(first.TopByCpu);
        Assert.Null(first.Cpu.CpusInUse);
        Assert.Equal(3, first.Runs.Limit);
        Assert.Equal(1, first.Runs.ManagerReserved);
        Assert.Equal("Alpha", Assert.Single(first.Runs.Running).Team);
        Assert.Null(first.HeavyLease);

        // Ten seconds later Alpha used 5 s of CPU (500 ticks): 50% of one CPU. Beta used none.
        clock.Advance(TimeSpan.FromSeconds(10));
        WriteProcess(proc, 100, "agent-cli", group: 100, utime: 400, stime: 200, residentPages: 1000);
        File.WriteAllText(Path.Combine(_root, "cgroup", "cpu.stat"), "usage_usec 4855781301\nnr_throttled 1821\nthrottled_usec 99000000\n");

        var second = sampler.Sample();
        Assert.Equal("Alpha", second.TopByCpu[0].Team);
        Assert.Equal(50.0, second.TopByCpu[0].CpuPercent!.Value, 3);
        Assert.Equal(0.0, second.TopByCpu[1].CpuPercent!.Value, 3);
        Assert.Equal(2.0, second.Cpu.CpusInUse!.Value, 3);
        Assert.Equal(25.0, second.Cpu.PercentOfLimit!.Value, 3);

        Assert.Equal(2, sampler.History().Count);
        Assert.Same(second, sampler.Latest);
    }

    [Fact]
    public void The_history_keeps_about_ten_minutes()
    {
        var clock = new SteppedClock(DateTimeOffset.Parse("2026-10-01T10:00:00Z"));
        var gate = new HeadroomGate(() => 80, () => 10, clock);
        var sampler = new CapacitySampler(
            new CgroupReader(Path.Combine(_root, "absent")), new ProcessGroupReader(Path.Combine(_root, "noproc")),
            new RunProcessGroups(), new WipLedger(2, gate.Reason), gate, new NoHeavyLease(), () => 80, () => 10,
            clock: clock);

        for (var i = 0; i < 200; i++)
        {
            sampler.Sample();
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        var history = sampler.History();
        Assert.InRange(history.Count, 120, 121);
        Assert.True(history[^1].At - history[0].At <= CapacitySampler.HistorySpan);
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class SteppedClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
