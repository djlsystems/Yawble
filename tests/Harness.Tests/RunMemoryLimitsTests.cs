using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A RUN'S OWN MEMORY LIMIT: which mechanism the Host picks from its own cgroup, the start log line
/// that names it, and a run over its limit failing <c>out-of-memory</c> with the limit and the setting
/// in its words, never as an agent fault. The cgroup branch is walked over fixture trees; the rlimit
/// branch runs for real wherever <c>prlimit</c> is in a system directory.
/// </summary>
public sealed class RunMemoryLimitsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-run-memory-").FullName;

    public void Dispose()
    {
        MemberTempCleanup.Remove(_root);
        Directory.Delete(_root, recursive: true);
    }

    private static readonly RunMemoryLimit SixtyFour = new(64, "runs.memoryLimitMb is set to 64 MB");

    private static RunMemoryLimit Fixed() => SixtyFour;

    [Fact]
    public void A_delegated_writable_cgroup_is_chosen_first()
    {
        var limits = RunMemoryLimits.Decide(new CgroupFacts("/sys/fs/cgroup/app", "writable"), "/usr/bin/prlimit", Fixed);

        Assert.Equal(RunMemoryMechanism.Cgroup, limits.Mechanism);
        Assert.StartsWith("Run memory limits: cgroup - ", limits.LogLine);
        Assert.Equal("/sys/fs/cgroup/app", limits.CgroupDirectory);
        Assert.Empty(limits.Prefix(SixtyFour));
    }

    [Fact]
    public void Without_a_writable_cgroup_prlimit_gives_each_process_rlimit_data_and_says_it_is_per_process()
    {
        var limits = RunMemoryLimits.Decide(
            new CgroupFacts(null, "the Host's cgroup /sys/fs/cgroup is not writable (Read-only file system)"),
            "/usr/bin/prlimit", Fixed);

        Assert.Equal(RunMemoryMechanism.Rlimit, limits.Mechanism);
        Assert.StartsWith("Run memory limits: rlimit - the Host's cgroup /sys/fs/cgroup is not writable", limits.LogLine);
        Assert.Contains("per process", limits.LogLine);
        Assert.Equal(["/usr/bin/prlimit", "--data=67108864:67108864", "--"], limits.Prefix(SixtyFour));

        // No figure, no prefix: a container with no limit and no setting runs as before.
        Assert.Empty(limits.Prefix(new RunMemoryLimit(null, "no limit")));
    }

    [Fact]
    public void With_neither_it_is_not_available_and_says_why()
    {
        var limits = RunMemoryLimits.Decide(new CgroupFacts(null, "the memory controller is not delegated"), null, Fixed);

        Assert.Equal(RunMemoryMechanism.None, limits.Mechanism);
        Assert.StartsWith("Run memory limits: not available — the memory controller is not delegated", limits.LogLine);
        Assert.Contains("prlimit is not in a root-owned system directory", limits.LogLine);
        Assert.Empty(limits.Prefix(SixtyFour));
        Assert.Null(limits.BeginRun(SixtyFour));
        Assert.False(limits.StoppedBy(SixtyFour, 1, "out of memory", null));
    }

    /// <summary>
    /// The decision on the machine the suite runs on, written to the test output, so the start log
    /// line is seen for this engine. Probe only: a writable cgroup is never prepared from a test.
    /// </summary>
    [Fact]
    public void On_this_machine_the_start_line_names_one_mechanism_and_its_reason()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "The cgroup and prlimit are Linux's.");

        var limits = RunMemoryLimits.Decide(
            RunMemoryLimits.ProbeCgroup("/sys/fs/cgroup", "/proc/self/cgroup"), SystemCommand.Find("prlimit"), Fixed);

        TestContext.Current.TestOutputHelper?.WriteLine(limits.LogLine);
        Assert.Matches("^Run memory limits: (cgroup - |rlimit - |not available — ).+", limits.LogLine);
    }

    [Fact]
    public void The_cgroup_probe_reads_the_hosts_own_group_and_names_each_missing_fact()
    {
        var tree = Path.Combine(_root, "cgroup");
        var self = Path.Combine(_root, "self-cgroup");
        Directory.CreateDirectory(tree);
        File.WriteAllText(self, "0::/app\n");

        Assert.Contains("not a cgroup v2 hierarchy", RunMemoryLimits.ProbeCgroup(tree, self).Why);

        File.WriteAllText(Path.Combine(tree, "cgroup.controllers"), "cpu memory pids\n");
        Assert.Contains("is not visible inside the container", RunMemoryLimits.ProbeCgroup(tree, self).Why);

        var app = Directory.CreateDirectory(Path.Combine(tree, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "cgroup.controllers"), "cpu pids\n");
        Assert.Contains("memory controller is not delegated", RunMemoryLimits.ProbeCgroup(tree, self).Why);

        File.WriteAllText(Path.Combine(app, "cgroup.controllers"), "cpu memory pids\n");
        var facts = RunMemoryLimits.ProbeCgroup(tree, self);
        Assert.Equal(app, facts.Usable);
        Assert.Empty(Directory.GetDirectories(app));

        if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
        {
            File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                var readOnly = RunMemoryLimits.ProbeCgroup(tree, self);
                Assert.Null(readOnly.Usable);
                Assert.Contains("is not writable", readOnly.Why);
            }
            finally
            {
                File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public void Preparing_a_delegated_group_moves_its_processes_to_a_leaf_and_enables_memory_for_runs()
    {
        var app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        File.WriteAllText(Path.Combine(app, "cgroup.subtree_control"), "");
        File.WriteAllText(Path.Combine(app, "cgroup.procs"), "1\n42\n");

        Assert.Null(RunMemoryLimits.Prepare(app));

        Assert.Equal("142", File.ReadAllText(Path.Combine(app, RunMemoryLimits.HostLeaf, "cgroup.procs")));
        Assert.Equal("+memory", File.ReadAllText(Path.Combine(app, "cgroup.subtree_control")));
    }

    [Fact]
    public void A_run_cgroup_carries_the_limit_and_its_oom_kill_count_is_what_says_it_was_stopped()
    {
        var app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        var limits = RunMemoryLimits.Decide(new CgroupFacts(app, "writable"), null, Fixed);

        using var run = limits.BeginRun(SixtyFour);
        Assert.NotNull(run);
        Assert.StartsWith(RunMemoryLimits.RunPrefix, Path.GetFileName(run.Directory));
        Assert.Equal("67108864", File.ReadAllText(Path.Combine(run.Directory, "memory.max")));
        Assert.Equal("1", File.ReadAllText(Path.Combine(run.Directory, "memory.oom.group")));
        Assert.True(run.Add(4242));
        Assert.Equal("4242", File.ReadAllText(Path.Combine(run.Directory, "cgroup.procs")));

        var events = Path.Combine(run.Directory, "memory.events");
        File.WriteAllText(events, "low 0\nhigh 0\nmax 3\noom 1\noom_kill 0\n");
        Assert.False(limits.StoppedBy(SixtyFour, 137, string.Empty, run));

        File.WriteAllText(events, "low 0\nhigh 0\nmax 9\noom 2\noom_kill 1\n");
        Assert.True(limits.StoppedBy(SixtyFour, 137, string.Empty, run));
        Assert.False(limits.StoppedBy(SixtyFour, 0, string.Empty, run));
    }

    [Fact]
    public void The_out_of_memory_class_is_its_own_never_resumed_and_names_the_setting()
    {
        Assert.Equal("out-of-memory", FailureClasses.OutOfMemory);
        Assert.True(FailureClasses.IsKnown(FailureClasses.OutOfMemory));
        Assert.False(FailureClasses.ResumesAutomatically(FailureClasses.OutOfMemory));
        Assert.Contains("runs.memoryLimitMb", FailureClasses.Sentence(FailureClasses.OutOfMemory));
        Assert.Contains("not an agent fault", FailureClasses.Sentence(FailureClasses.OutOfMemory));
    }

    [Fact]
    public async Task A_run_over_its_rlimit_fails_out_of_memory_with_the_limit_and_the_setting_and_is_not_an_agent_fault()
    {
        var prlimit = SystemCommand.Find("prlimit");
        Assert.SkipWhen(!OperatingSystem.IsLinux() || prlimit is null,
            "Needs Linux and prlimit in a root-owned system directory to apply RLIMIT_DATA.");

        var limits = RunMemoryLimits.Decide(new CgroupFacts(null, "the test's cgroup is not used"), prlimit, Fixed);

        // Holds a 200 MB line in memory under a 64 MB limit: refused memory, it says so and exits non-zero.
        var greedy = await RunAsync(limits, "#!/bin/sh\ncat >/dev/null\nhead -c 200000000 /dev/zero | tail -n 1 >/dev/null\n");

        Assert.NotEqual(0, greedy.ExitCode);
        Assert.Equal(FailureClasses.OutOfMemory, greedy.FailureClass);
        Assert.Contains("memory limit of 64 MB", greedy.LaunchError);
        Assert.Contains("runs.memoryLimitMb", greedy.LaunchError);
        Assert.Contains("not an agent fault", greedy.LaunchError);

        var member = Harness.Host.AgentMemberRunner.ToMemberResult(greedy with { ReachedThePlatform = false });
        Assert.Equal(FailureClasses.OutOfMemory, member.FailureClass);
        Assert.NotEqual(FailureClasses.AgentFault, member.FailureClass);

        // The same limit, a run that stays under it: it succeeds, and the limit really was applied.
        var modest = await RunAsync(limits, "#!/bin/sh\ncat >/dev/null\nulimit -d\n");
        Assert.Equal(0, modest.ExitCode);
        Assert.Null(modest.FailureClass);
        Assert.Equal((64 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture), modest.Output.Trim());
    }

    /// <summary>
    /// THE HOST NEVER CALLS AN ENGINE: it measures and limits from inside the container, the same
    /// on Podman and Docker. Only the operator CLI names one. A literal that is exactly an engine's
    /// program name is how a call would start (a start info, a PATH lookup), so none may be in src/.
    /// </summary>
    [Fact]
    public void No_code_in_the_host_starts_podman_or_docker()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);

        var engine = new System.Text.RegularExpressions.Regex("\"(podman|docker)(\\.exe)?\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var found = Directory.EnumerateFiles(Path.Combine(directory.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, line, i)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && engine.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(directory.FullName, x.file)}:{x.i + 1}")
            .ToList();

        Assert.Empty(found);
    }

    private async Task<AgentResult> RunAsync(RunMemoryLimits limits, string script)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        var program = Path.Combine(bin, "probe");
        await TestExecutable.WriteAsync(program, script);

        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var catalog = new AgentCatalog(
            [new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch(program, [], LanguageModel: false))]);

        return await new ProcessAgentRunner(catalog, new RunHeartbeat(), memory: limits).RunAsync(
            new AgentInvocation(
                new ContainerId("alpha", "worker"), "You are a probe.", "hello", workspace,
                new Dictionary<string, string>(), Agent: "probe"),
            TestContext.Current.CancellationToken);
    }
}
