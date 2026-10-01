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

    private static readonly RunMemoryLimit SixtyFour = new(64, "runs.memoryLimitMb is set to 64 MB", Set: true);

    /// <summary>A figure the Host computed rather than a person set: never applied under rlimit.</summary>
    private static readonly RunMemoryLimit Computed = new(1792, "runs.memoryLimitMb is 0, so (8192 MB container limit - 1024 MB for the Host) / 4");

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

    /// <summary>
    /// NO COMPUTED RLIMIT. RLIMIT_DATA counts a runtime's virtual reservations, and the figure below
    /// which one refuses to start depends on the runtime and the architecture, so with
    /// runs.memoryLimitMb at 0 a launch under rlimit carries no data limit and no hard ceiling, and the
    /// start line says nothing is enforced per run and why.
    /// </summary>
    [Fact]
    public void Under_rlimit_with_nothing_set_a_launch_carries_no_prlimit_data_and_the_start_line_says_not_enforced()
    {
        var limits = RunMemoryLimits.Decide(
            new CgroupFacts(null, "the Host's cgroup /sys/fs/cgroup is not writable (Read-only file system)"),
            "/usr/bin/prlimit", () => Computed, () => new RunMemoryLimit(7168, "8192 MB container limit - 1024 MB for the Host"));

        Assert.Equal(RunMemoryMechanism.Rlimit, limits.Mechanism);
        Assert.True(limits.NotEnforced);
        Assert.Equal(
            "Run memory limits: not enforced per run - the cgroup is not writable; admission by measured memory guards the container",
            limits.LogLine);
        Assert.Equal(RunMemoryLimits.NotEnforcedLine, limits.LogLine);

        Assert.Null(limits.Limit().Mb);
        Assert.Null(limits.Ceiling().Mb);
        Assert.Contains("runs.memoryLimitMb is not set", limits.Limit().Source);
        Assert.Empty(limits.Prefix(limits.Limit(), limits.Ceiling()));
        Assert.DoesNotContain(limits.Prefix(limits.Limit(), limits.Ceiling()), a => a.StartsWith("--data", StringComparison.Ordinal));

        // The same rule read from the setting itself: 0 is not set, a figure is.
        var settings = Harness.Tests.Host.TenantSettingsTests.Bare(cpuCount: 5, memoryLimitMb: 8192);
        Assert.False(settings.RunMemoryLimit().Set);
        var fromSettings = RunMemoryLimits.Decide(new CgroupFacts(null, "not writable"), "/usr/bin/prlimit",
            settings.RunMemoryLimit, settings.RunMemoryCeiling);
        Assert.Empty(fromSettings.Prefix(fromSettings.Limit(), fromSettings.Ceiling()));
        Assert.Equal(RunMemoryLimits.NotEnforcedLine, fromSettings.LogLine);

        // A cgroup, where writable, still gets the figure: that path is unchanged.
        var cgroup = RunMemoryLimits.Decide(new CgroupFacts("/sys/fs/cgroup/app", "writable"), "/usr/bin/prlimit", () => Computed);
        Assert.False(cgroup.NotEnforced);
        Assert.Equal(1792, cgroup.Limit().Mb);
    }

    /// <summary>A figure a person set is carried under rlimit as before, soft and hard.</summary>
    [Fact]
    public void Under_rlimit_a_set_figure_is_carried_and_the_start_line_says_rlimit()
    {
        var limits = RunMemoryLimits.Decide(new CgroupFacts(null, "not writable"), "/usr/bin/prlimit",
            () => new RunMemoryLimit(4096, "runs.memoryLimitMb is set to 4096 MB", Set: true),
            () => new RunMemoryLimit(7168, "ceiling"));

        Assert.False(limits.NotEnforced);
        Assert.StartsWith("Run memory limits: rlimit - ", limits.LogLine);
        Assert.Equal(4096, limits.Limit().Mb);
        Assert.Equal(["/usr/bin/prlimit", "--data=4294967296:7516192768", "--"], limits.Prefix(limits.Limit(), limits.Ceiling()));
    }

    [Fact]
    public async Task A_real_launch_under_rlimit_with_nothing_set_has_no_data_limit()
    {
        var prlimit = SystemCommand.Find("prlimit");
        Assert.SkipWhen(!OperatingSystem.IsLinux() || prlimit is null,
            "Needs Linux and prlimit in a root-owned system directory to apply RLIMIT_DATA.");

        var limits = RunMemoryLimits.Decide(new CgroupFacts(null, "the test's cgroup is not used"), prlimit, () => Computed);
        var run = await RunAsync(limits, "#!/bin/sh\ncat >/dev/null\nulimit -d\n");

        // No limit is added: the run has whatever the Host itself has (unlimited, or what its own parent set).
        var host = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", "ulimit -d"])
        {
            RedirectStandardOutput = true,
        })!;
        var hostLimit = (await host.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).Trim();
        await host.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(hostLimit, run.Output.Trim());
        Assert.NotEqual((Computed.Mb!.Value * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture), run.Output.Trim());
    }

    /// <summary>
    /// A CRASHED AGENT'S OWN WORDS REACH THE CARD. A fake CLI writes to stderr and aborts: the run
    /// fails classed crashed - never unknown, never an agent fault - and its error carries the tail
    /// of that stderr, with a secret in it redacted.
    /// </summary>
    [Fact]
    public async Task A_cli_that_writes_to_stderr_and_aborts_fails_crashed_with_its_stderr_tail_and_is_not_an_agent_fault()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Signals are POSIX.");

        var aborts = await RunAsync(RunMemoryLimits.NotAvailable("the test applies none"), """
            #!/bin/sh
            cat >/dev/null
            echo 'starting up' >&2
            echo 'password=hunter2-is-not-for-the-card' >&2
            echo 'fatal: could not reserve the heap, aborting' >&2
            kill -ABRT $$
            """);

        Assert.Equal(134, aborts.ExitCode);
        Assert.Equal(FailureClasses.Crashed, aborts.FailureClass);
        Assert.Contains("signal 6 (SIGABRT)", aborts.LaunchError);
        Assert.Contains("fatal: could not reserve the heap, aborting", aborts.LaunchError);
        Assert.Contains("starting up", aborts.LaunchError);
        Assert.Contains("password=[redacted]", aborts.LaunchError);
        Assert.DoesNotContain("hunter2", aborts.LaunchError);
        Assert.DoesNotContain("memory limit", aborts.LaunchError);
        Assert.DoesNotContain("could say why", aborts.LaunchError, StringComparison.OrdinalIgnoreCase);

        var member = Harness.Host.AgentMemberRunner.ToMemberResult(aborts with { ReachedThePlatform = false });
        Assert.Equal(FailureClasses.Crashed, member.FailureClass);
        Assert.NotEqual(FailureClasses.AgentFault, member.FailureClass);
        Assert.Contains("fatal: could not reserve the heap, aborting", member.FailureReason);

        Assert.True(FailureClasses.IsKnown(FailureClasses.Crashed));
        Assert.False(FailureClasses.ResumesAutomatically(FailureClasses.Crashed));
        Assert.DoesNotContain("could say why", FailureClasses.Sentence(FailureClasses.Crashed), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A crash under a memory limit names the limit in MB and the setting that moves it.</summary>
    [Fact]
    public async Task A_crash_under_a_set_memory_limit_names_the_limit_and_the_setting()
    {
        var prlimit = SystemCommand.Find("prlimit");
        Assert.SkipWhen(!OperatingSystem.IsLinux() || prlimit is null,
            "Needs Linux and prlimit in a root-owned system directory to apply RLIMIT_DATA.");

        var limits = RunMemoryLimits.Decide(new CgroupFacts(null, "the test's cgroup is not used"), prlimit,
            () => new RunMemoryLimit(1024, "runs.memoryLimitMb is set to 1024 MB", Set: true));
        var aborts = await RunAsync(limits, "#!/bin/sh\ncat >/dev/null\necho 'runtime refused to start' >&2\nkill -ABRT $$\n");

        Assert.Equal(FailureClasses.Crashed, aborts.FailureClass);
        Assert.Contains("runtime refused to start", aborts.LaunchError);
        Assert.Contains("memory limit of 1024 MB", aborts.LaunchError);
        Assert.Contains("runs.memoryLimitMb", aborts.LaunchError);
    }

    /// <summary>
    /// A non-zero exit with nothing on stdout is a crash too; one that answered on stdout is left to
    /// the classifier as before.
    /// </summary>
    [Fact]
    public async Task A_non_zero_exit_with_no_output_is_crashed_and_one_with_output_is_not()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake CLI is a shell script.");

        var silent = await RunAsync(RunMemoryLimits.NotAvailable("none"), "#!/bin/sh\ncat >/dev/null\necho 'config file is unreadable' >&2\nexit 3\n");
        Assert.Equal(FailureClasses.Crashed, silent.FailureClass);
        Assert.Contains("exited 3 without an answer", silent.LaunchError);
        Assert.Contains("config file is unreadable", silent.LaunchError);

        var answered = await RunAsync(RunMemoryLimits.NotAvailable("none"), "#!/bin/sh\ncat >/dev/null\necho 'half an answer'\nexit 3\n");
        Assert.Null(answered.FailureClass);
        Assert.Null(answered.LaunchError);
    }

    [Fact]
    public void The_stderr_tail_is_redacted_then_bounded_in_lines_and_characters()
    {
        var many = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var tail = AgentCrash.StderrTail(many);
        Assert.Equal(AgentCrash.TailLines, tail.Split('\n').Length);
        Assert.StartsWith("line 81", tail);
        Assert.EndsWith("line 100", tail);

        var wide = AgentCrash.StderrTail(new string('x', 10_000) + " token=abc");
        Assert.True(wide.Length <= AgentCrash.TailChars);
        Assert.EndsWith("token=[redacted]", wide);

        Assert.Equal(string.Empty, AgentCrash.StderrTail("  \n "));
        Assert.Contains("It wrote nothing to stderr", AgentCrash.Sentence(139, "", null, RunMemoryMechanism.None));
        Assert.Contains("signal 11 (SIGSEGV)", AgentCrash.Sentence(139, "", null, RunMemoryMechanism.None));
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
