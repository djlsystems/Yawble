using System.Globalization;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;

namespace Harness.Tests;

/// <summary>
/// THE HEAVY ALLOWANCE: a run holding the <c>heavy</c> lease is raised to the measured headroom and
/// lowered when it lets go - never while it is over its own limit - on cgroup (memory.max in place)
/// and on rlimit (the soft limit of every process in its session, under the hard limit it started
/// with). The lease moves it through <see cref="LeaseActions"/>, whichever call released it. And a
/// process in a run that the limit stopped is named in the run's progress, with the limit and the
/// setting, even when the agent carries on. Walked over fixture cgroup and proc trees; the rlimit
/// raise also runs for real wherever <c>prlimit</c> is in a system directory.
/// </summary>
public sealed class RunAllowancesTests : IDisposable
{
    private const long Mb = 1024 * 1024;
    private const long Page = 4096;

    private static readonly ContainerId Run = new("alpha", "worker");
    private static readonly ContainerId Other = new("beta", "Tester");
    private static readonly string Key = LeaseOwner.For(Run).Key;

    private static readonly RunMemoryLimit SixtyFour = new(64, "runs.memoryLimitMb is set to 64 MB");
    private static readonly RunMemoryLimit Ceiling = new(256, "256 MB ceiling");

    private readonly string _root = Directory.CreateTempSubdirectory("harness-run-allowance-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        MemberTempCleanup.Remove(_root);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Under_rlimit_every_run_starts_with_its_own_soft_limit_under_the_ceiling_as_its_hard_limit()
    {
        var limits = RunMemoryLimits.Decide(new CgroupFacts(null, "not writable"), "/usr/bin/prlimit", () => SixtyFour, () => Ceiling);

        Assert.Equal(["/usr/bin/prlimit", "--data=67108864:268435456", "--"], limits.Prefix(SixtyFour, limits.Ceiling()));

        // A ceiling at or below the limit is the limit: soft and hard the same, as before.
        Assert.Equal(["/usr/bin/prlimit", "--data=67108864:67108864", "--"], limits.Prefix(SixtyFour, new RunMemoryLimit(32, "lower")));
        Assert.Contains("a run holding the heavy lease gets", limits.LogLine);
        Assert.Contains("which the agent cannot raise", limits.LogLine);

        var cgroup = RunMemoryLimits.Decide(new CgroupFacts("/sys/fs/cgroup/app", "writable"), null, () => SixtyFour, () => Ceiling);
        Assert.Contains("a run holding the heavy lease gets", cgroup.LogLine);
        Assert.Contains("memory.max raised in place", cgroup.LogLine);
    }

    [Fact]
    public async Task On_cgroup_the_holder_gets_the_measured_headroom_in_place_and_is_lowered_only_once_it_fits_its_own_limit()
    {
        var proc = Directory.CreateDirectory(Path.Combine(_root, "proc")).FullName;
        Proc(proc, 700, session: 700, residentPages: 30 * Mb / Page);
        Proc(proc, 701, session: 700, residentPages: 20 * Mb / Page);
        var groups = new RunProcessGroups();
        using var other = groups.Register(700, Other);

        var (memory, run) = CgroupRun();
        List<string> holders = [];
        (long Mb, int Runs)? measured = null;
        var allowances = new RunAllowances(memory, (mb, runs) =>
        {
            measured = (mb, runs);
            return new RunMemoryLimit(1000 - mb, $"1000 MB - {mb} MB measured");
        }, () => holders, groups: groups, procRoot: proc, pageSize: Page, retry: TimeSpan.FromHours(1));

        using var allowance = allowances.Begin(Run, 4242, SixtyFour, Ceiling, run);
        Assert.NotNull(allowance);
        File.WriteAllText(Path.Combine(run.Directory, "memory.current"), (40 * Mb).ToString(CultureInfo.InvariantCulture));

        holders = [Key];
        await allowances.ReconcileAsync(Ct);

        // The other run's group was measured from proc: 30 + 20 MB in one run.
        Assert.Equal((50L, 1), measured);
        Assert.Equal((950 * Mb).ToString(CultureInfo.InvariantCulture), MemoryMax(run));
        Assert.Equal(950, allowance.Current.Mb);

        // Released while the run uses 300 MB it cannot give back: it keeps the raise.
        holders = [];
        File.WriteAllText(Path.Combine(run.Directory, "memory.current"), (400 * Mb).ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(run.Directory, "memory.stat"), $"anon {300 * Mb}\nfile {100 * Mb}\n");
        await allowances.ReconcileAsync(Ct);
        Assert.Equal((950 * Mb).ToString(CultureInfo.InvariantCulture), MemoryMax(run));
        Assert.True(allowance.Raised);

        // Back inside its own limit (file cache does not count): lowered.
        File.WriteAllText(Path.Combine(run.Directory, "memory.stat"), $"anon {20 * Mb}\nfile {380 * Mb}\n");
        await allowances.ReconcileAsync(Ct);
        Assert.Equal((64 * Mb).ToString(CultureInfo.InvariantCulture), MemoryMax(run));
        Assert.False(allowance.Raised);
        Assert.Equal(64, allowance.Current.Mb);
    }

    [Fact]
    public async Task On_rlimit_every_process_in_the_holders_session_gets_the_soft_raise_up_to_its_hard_limit_and_is_lowered_once_it_fits()
    {
        var proc = Directory.CreateDirectory(Path.Combine(_root, "proc")).FullName;
        Proc(proc, 500, session: 500, dataPages: 10 * Mb / Page, soft: 64 * Mb);
        Proc(proc, 501, session: 500, dataPages: 10 * Mb / Page, soft: 64 * Mb);
        Proc(proc, 502, session: 999, dataPages: 10 * Mb / Page, soft: 64 * Mb);

        var memory = RunMemoryLimits.Decide(new CgroupFacts(null, "not writable"), "/usr/bin/prlimit", () => SixtyFour, () => Ceiling);
        List<string> holders = [];
        List<(int Pid, long Bytes)> written = [];
        var allowances = new RunAllowances(memory, (_, _) => new RunMemoryLimit(512, "512 MB measured"), () => holders,
            groups: new RunProcessGroups(), procRoot: proc, pageSize: Page, retry: TimeSpan.FromHours(1),
            writeSoft: (pid, bytes, _) =>
            {
                written.Add((pid, bytes));
                Soft(proc, pid, bytes);
                return Task.FromResult(true);
            });

        using var allowance = allowances.Begin(Run, 500, SixtyFour, Ceiling, null);
        Assert.NotNull(allowance);

        holders = [Key];
        await allowances.ReconcileAsync(Ct);

        // 512 MB measured, held to the 256 MB hard limit the run started with; another session untouched.
        Assert.Equal([(500, 256 * Mb), (501, 256 * Mb)], written.OrderBy(w => w.Pid));
        Assert.Equal(256, allowance.Current.Mb);
        Assert.Contains("held to the 256 MB hard limit", allowance.Current.Source);

        // Released while 501 is at 100 MB of data: 500 goes back, 501 keeps the raise rather than fail its next allocation.
        written.Clear();
        Proc(proc, 501, session: 500, dataPages: 100 * Mb / Page, soft: 256 * Mb);
        holders = [];
        await allowances.ReconcileAsync(Ct);
        Assert.Equal([(500, 64 * Mb)], written);
        Assert.True(allowance.Raised);

        // 501 shrank: the retry lowers it.
        written.Clear();
        Proc(proc, 501, session: 500, dataPages: 10 * Mb / Page, soft: 256 * Mb);
        await allowances.ReconcileAsync(Ct);
        Assert.Equal([(501, 64 * Mb)], written);
        Assert.False(allowance.Raised);
    }

    [Fact]
    public async Task The_lease_moves_the_limit_whichever_call_moved_the_lease()
    {
        var (memory, alpha) = CgroupRun();
        var beta = memory.BeginRun(SixtyFour)!;
        var leases = new InstanceLeases(() => 1);
        var allowances = new RunAllowances(memory, (_, _) => new RunMemoryLimit(900, "900 MB measured"),
            () => leases.Holders(InstanceLeases.Heavy).Select(o => o.Key).ToList(),
            groups: new RunProcessGroups(), procRoot: Path.Combine(_root, "no-proc"), retry: TimeSpan.FromHours(1));
        var actions = new LeaseActions(leases, new RunHeartbeat(), new NoReports(), allowances);

        foreach (var run in new[] { alpha, beta }) File.WriteAllText(Path.Combine(run.Directory, "memory.current"), (10 * Mb).ToString(CultureInfo.InvariantCulture));
        using var a = allowances.Begin(Run, 1, SixtyFour, Ceiling, alpha);
        using var b = allowances.Begin(Other, 2, SixtyFour, Ceiling, beta);

        await actions.AcquireAsync("heavy", LeaseOwner.For(Run), Ct);
        Assert.Equal((900 * Mb).ToString(CultureInfo.InvariantCulture), MemoryMax(alpha));

        // Queued, then handed the lease when the holder releases: one lowered, the other raised.
        await actions.AcquireAsync("heavy", LeaseOwner.For(Other), Ct);
        Assert.Equal((64 * Mb).ToString(CultureInfo.InvariantCulture), MemoryMax(beta));
        await actions.ReleaseAsync("heavy", LeaseOwner.For(Run), Ct);
        Assert.Equal((64 * Mb).ToString(CultureInfo.InvariantCulture), MemoryMax(alpha));
        Assert.Equal((900 * Mb).ToString(CultureInfo.InvariantCulture), MemoryMax(beta));

        // A run's end (and any release path through EndedAsync) restores the normal limit too.
        await actions.EndedAsync(LeaseOwner.For(Other), Ct);
        Assert.Equal((64 * Mb).ToString(CultureInfo.InvariantCulture), MemoryMax(beta));
    }

    [Fact]
    public async Task A_real_run_that_takes_heavy_gets_the_higher_rlimit_for_what_it_starts_and_for_what_is_running_and_cannot_raise_its_hard_limit()
    {
        var prlimit = SystemCommand.Find("prlimit");
        Assert.SkipWhen(!OperatingSystem.IsLinux() || prlimit is null,
            "Needs Linux and prlimit in a root-owned system directory to apply RLIMIT_DATA.");

        var memory = RunMemoryLimits.Decide(new CgroupFacts(null, "the test's cgroup is not used"), prlimit, () => SixtyFour, () => Ceiling);
        List<string> holders = [];
        var allowances = new RunAllowances(memory, (_, _) => new RunMemoryLimit(192, "192 MB measured"), () => holders,
            groups: new RunProcessGroups(), retry: TimeSpan.FromHours(1));

        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var running = RunAsync(memory, allowances, null, """
            #!/bin/sh
            cat >/dev/null
            echo "before $(sh -c 'ulimit -d')"
            : > ready
            while [ ! -f go ]; do sleep 0.05; done
            echo "self $(ulimit -d)"
            echo "new $(sh -c 'ulimit -d')"
            if (ulimit -H -d unlimited) 2>/dev/null; then echo "hard raised"; else echo "hard held"; fi
            : > raised
            while [ ! -f back ]; do sleep 0.05; done
            echo "released $(ulimit -d) $(sh -c 'ulimit -d')"
            """, workspace);

        await Until(() => File.Exists(Path.Combine(workspace, "ready")), running);
        holders = [Key];
        await allowances.ReconcileAsync(Ct);
        File.WriteAllText(Path.Combine(workspace, "go"), "");

        await Until(() => File.Exists(Path.Combine(workspace, "raised")), running);
        holders = [];
        await allowances.ReconcileAsync(Ct);
        File.WriteAllText(Path.Combine(workspace, "back"), "");

        var result = await running;
        Assert.Equal(0, result.ExitCode);
        var lines = result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("before 65536", lines);
        Assert.Contains("self 196608", lines);   // the shell already running got the raise
        Assert.Contains("new 196608", lines);    // and what it started after it inherited it
        Assert.Contains("hard held", lines);     // the agent cannot raise its hard limit
        Assert.Contains("released 65536 65536", lines);
    }

    [Theory]
    [InlineData("""{"type":"user","toolUseResult":{"stdout":"Passed 1374\nOut of memory.\n","stderr":""}}""", "Out of memory.")]
    [InlineData("""{"content":"make: fork: Cannot allocate memory"}""", "make: fork: Cannot allocate memory")]
    [InlineData("""{"content":"[x]\r\nout of memory\r\n"}""", "out of memory")]
    [InlineData("""{"content":"    Assert.Contains(\"Out of memory.\", line);\n"}""", null)]
    [InlineData("""{"content":"The test host ran out of memory and stopped.\n"}""", null)]
    [InlineData("""{"content":"cannot allocate memory|out of memory|memory exhausted"}""", null)]
    [InlineData("""{"content":"nothing to see"}""", null)]
    public void A_transcript_line_counts_only_when_a_line_in_it_is_the_refusal_itself(string line, string? expected)
    {
        Assert.Equal(expected, RunMemoryLimits.ChildOutOfMemory(line));
    }

    [Fact]
    public async Task Under_rlimit_a_process_that_printed_out_of_memory_is_named_with_the_limit_and_the_setting()
    {
        var memory = RunMemoryLimits.Decide(new CgroupFacts(null, "not writable"), "/usr/bin/prlimit", () => SixtyFour);
        List<string> reported = [];

        await RunAllowances.WatchAsync(memory, () => SixtyFour, null,
            () => Lines("""{"stdout":"Passed 1374\nOut of memory.\n"}""", """{"content":"fine"}"""),
            () => true, sentence => { reported.Add(sentence); return Task.CompletedTask; }, TimeSpan.FromMilliseconds(10), Ct);

        var sentence = Assert.Single(reported);
        Assert.Contains("it printed \"Out of memory.\"", sentence);
        Assert.Contains("64 MB for each of its processes", sentence);
        Assert.Contains("runs.memoryLimitMb", sentence);
        Assert.Contains("heavy lease", sentence);
        Assert.Contains("not an agent fault", sentence);
    }

    [Fact]
    public async Task On_cgroup_an_oom_kill_in_a_run_that_carries_on_is_named_in_its_progress_with_the_limit_and_the_setting()
    {
        var (memory, _) = CgroupRun(begin: false);
        var app = memory.CgroupDirectory!;
        var reports = new NoReports();

        // The agent carries on: an OOM kill is counted in its cgroup, and it exits 0 a second later.
        var result = await RunAsync(memory, null, reports, $"""
            #!/bin/sh
            cat >/dev/null
            sleep 0.3
            for d in {app}/run-*; do printf 'low 0\noom 1\noom_kill 1\n' > "$d/memory.events"; done
            sleep 1
            echo carried on
            """, Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.FailureClass);
        var (member, sentence) = Assert.Single(reports.Lines);
        Assert.Equal(Run, member);
        Assert.Contains("the kernel OOM-killed 1 process in the run's cgroup", sentence);
        Assert.Contains("64 MB for the whole run", sentence);
        Assert.Contains("runs.memoryLimitMb", sentence);
    }

    private (RunMemoryLimits Memory, RunCgroup Run) CgroupRun(bool begin = true)
    {
        var app = Directory.CreateDirectory(Path.Combine(_root, "app-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        var memory = RunMemoryLimits.Decide(new CgroupFacts(app, "writable"), null, () => SixtyFour, () => Ceiling);
        return (memory, begin ? memory.BeginRun(SixtyFour)! : null!);
    }

    private static string MemoryMax(RunCgroup run) => File.ReadAllText(Path.Combine(run.Directory, "memory.max"));

    private static void Proc(string proc, int pid, int session, long residentPages = 1, long dataPages = 1, long? soft = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture))).FullName;
        File.WriteAllText(Path.Combine(dir, "stat"), $"{pid} (probe) S 1 {session} {session} 0 -1 0 0 0 0 0 5 5\n");
        File.WriteAllText(Path.Combine(dir, "statm"), $"1000 {residentPages} 0 0 0 {dataPages} 0\n");
        if (soft is { } bytes) Soft(proc, pid, bytes);
    }

    private static void Soft(string proc, int pid, long bytes) =>
        File.WriteAllText(Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture), "limits"),
            "Limit                     Soft Limit           Hard Limit           Units\n"
            + $"Max data size             {bytes}            {256 * Mb}            bytes\n");

    private static async IAsyncEnumerable<string> Lines(params string[] lines)
    {
        foreach (var line in lines)
        {
            await Task.Yield();
            yield return line;
        }
    }

    private static async Task Until(Func<bool> condition, Task running)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (running.IsCompleted) await running;
            Assert.True(DateTime.UtcNow < deadline, "The probe did not reach its next step in 30 seconds.");
            await Task.Delay(50, Ct);
        }
    }

    private async Task<AgentResult> RunAsync(RunMemoryLimits memory, RunAllowances? allowances, IMemberReports? reports, string script, string workspace)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        var program = Path.Combine(bin, "probe");
        await TestExecutable.WriteAsync(program, script.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

        var catalog = new AgentCatalog(
            [new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch(program, [], LanguageModel: false))]);

        return await new ProcessAgentRunner(catalog, new RunHeartbeat(), reports: reports, memory: memory,
                allowances: allowances, oomPoll: TimeSpan.FromMilliseconds(50))
            .RunAsync(
                new AgentInvocation(Run, "You are a probe.", "hello", workspace, new Dictionary<string, string>(), Agent: "probe"),
                Ct);
    }

    private sealed class NoReports : IMemberReports
    {
        public List<(ContainerId, string)> Lines { get; } = [];

        public Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default)
        {
            lock (Lines) Lines.Add((member, status));
            return Task.FromResult(MemberReportOutcome.Ok);
        }

        public Task<MemberReportOutcome> BlockedAsync(ContainerId member, string reason, int? item = null, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> DeferAsync(ContainerId member, int item, string reason, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);
    }
}
