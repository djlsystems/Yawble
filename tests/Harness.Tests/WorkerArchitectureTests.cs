using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE RUN BOUNDARY, MADE STRUCTURAL. Only the runtime worker (<c>Harness.Worker</c>) starts a
/// process, reads <c>/proc</c>, touches the cgroup filesystem or calls <c>setpriv</c> or
/// <c>prlimit</c>; control reaches runs through the run protocol. This reads the COMPILED IL of every
/// other assembly under <c>src/</c> and names each method that does one of those things, or calls a
/// worker method that does, by its rule. What remains is an allow-list: each entry names its caller
/// and why it stays for now, and an entry the scan no longer finds fails too, so the list only
/// shrinks.
///
/// Lambdas, local functions and state machines count against the method that wrote them. A path
/// passed as a defaulted parameter counts against the CALLER, because that is where the compiler
/// loads the string.
///
/// What it cannot see: a path built at runtime (<c>"/pr" + "oc"</c>), reflection, <c>dynamic</c>, and
/// interface, virtual or delegate dispatch - an interface call into a primitive is named at the
/// implementation, not at its caller. It is a ratchet against ordinary code, not a sandbox.
/// </summary>
public sealed class WorkerArchitectureTests
{
    public const string StartsAProcess = "starts a process";
    public const string ReadsProc = "reads /proc";
    public const string TouchesACgroup = "touches a cgroup";
    public const string CallsSetprivOrPrlimit = "calls setpriv or prlimit";
    public const string SignalsAProcessGroup = "signals a process group";
    public const string ReachesAWorkerLauncher = "reaches a worker launcher";

    /// <summary>Why an entry may stay: what a later change moves, or what control keeps for good.</summary>
    private static readonly string[] Categories =
    [
        "Concierge PTY", "sign-in probe", "tool pre-flight", "launch check", "CLI updates",
        "live transcript reader", "FolderRemoval's agent pass", "git", "gh", "agent user", "settings default",
        "pending",
    ];

    /// <summary>Every caller outside the worker that still does one of these things, and why.</summary>
    private static readonly Dictionary<string, string> AllowList = new(StringComparer.Ordinal)
    {
        ["PortaPtyEngine.SpawnAsync: " + StartsAProcess] =
            "Concierge PTY: a person's terminal session is spawned here; a later change moves it to a worker.",
        ["AgentAuthProbe.ProbeCommandAsync: " + StartsAProcess] =
            "sign-in probe: asks each CLI whether it is signed in; a later change moves it to a worker.",
        ["CliListingRunner.RunAsync: " + StartsAProcess] =
            "tool pre-flight: lists each CLI's configured tools; a later change moves it to a worker.",
        ["ForeignToolsCheck.CheckAsync: " + ReachesAWorkerLauncher] =
            "tool pre-flight: reads the agent's tool files as the agent; a later change moves it to a worker.",
        ["ForeignToolsCheck.BesideAsync: " + ReachesAWorkerLauncher] =
            "tool pre-flight: reads the agent's tool files as the agent; a later change moves it to a worker.",
        ["AgentCliUpdater.RunAsync: " + StartsAProcess] =
            "CLI updates: a person's update of a shared CLI install; a later change moves it to a worker.",
        ["LiveViewEndpoints.WatchAsync: " + ReachesAWorkerLauncher] =
            "live transcript reader: follows a run's transcript as the agent for a watcher; a later change moves it to a worker.",
        ["LiveViewEndpoints.RunTranscriptAsync: " + ReachesAWorkerLauncher] =
            "live transcript reader: reads a past run's transcript as the agent; a later change moves it to a worker.",
        ["FolderRemoval.RunAsync: " + StartsAProcess] =
            "FolderRemoval's agent pass: removes what the agent owns, as the agent; a later change moves it to a worker.",
        ["GitRunner.ExecuteGitAsync: " + StartsAProcess] =
            "git: a person's and a team's git stays in control.",
        ["GhContributor.RunAsync: " + StartsAProcess] =
            "gh: the GitHub CLI for a person's contributions stays in control.",
        ["ProcessAgentRunner.CheckLaunchAsync: " + ReachesAWorkerLauncher] =
            "launch check: starts a preset's free invocation through the worker's own launch, not a run; a later change sends it as a message.",
        ["HostDoctor.AgentsAsync: " + ReachesAWorkerLauncher] =
            "agent user: the doctor resolves who agent children would run as, to report it; a later change reads it from the worker.",
        ["TenantSettings..ctor: " + TouchesACgroup] =
            "settings default: the run limit's default is derived from the container's cgroup limits, read only; a later change takes them from the worker's capacity sample.",

        // Moved behind the protocol by the commits after this one, each deleting its own entries.
        ["Program.<Main>$: " + ReadsProc] = "pending: moves in this item, with the capacity sampler's readers.",
        ["Program.<Main>$: " + TouchesACgroup] = "pending: moves in this item, with the capacity sampler's readers.",
        ["CapacitySampler.Sample: " + ReachesAWorkerLauncher] = "pending: moves in this item, to SampleCapacity.",
        ["PluginMemberRunner.RunPluginAsync: " + ReachesAWorkerLauncher] = "pending: moves in this item, to StartRun.",
    };

    /// <summary>The worker types control may compose and talk to: the protocol's in-process ends.</summary>
    private static readonly HashSet<string> ProtocolSurface = new(StringComparer.Ordinal)
    {
        "InProcessWorker", "InProcessTransport", "WorkerHost",
    };

    private static readonly Assembly Worker = typeof(ChildProcess).Assembly;

    [Fact]
    public void Nothing_outside_the_worker_starts_a_process_reads_proc_or_touches_a_cgroup()
    {
        var found = Violations(Scanned());
        var unexpected = found.Where(v => !AllowList.ContainsKey(v)).ToList();

        Assert.True(unexpected.Count == 0,
            "Outside Harness.Worker, these start a process, read /proc, touch a cgroup, call setpriv or prlimit, "
            + "or call a worker method that does. Move them behind the run protocol, or allow-list them with a reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, unexpected));
    }

    [Fact]
    public void Every_allow_list_entry_is_still_needed()
    {
        var found = Violations(Scanned()).ToHashSet(StringComparer.Ordinal);
        var stale = AllowList.Keys.Where(key => !found.Contains(key)).ToList();

        Assert.True(stale.Count == 0,
            "These allow-list entries are no longer found by the scan; remove them:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Fact]
    public void Every_allow_list_entry_says_why()
    {
        Assert.All(AllowList, entry =>
        {
            Assert.Contains(": ", entry.Key, StringComparison.Ordinal);
            Assert.Contains(Categories, category => entry.Value.StartsWith(category + ":", StringComparison.Ordinal));
            Assert.True(entry.Value.Length > entry.Value.IndexOf(':') + 10, $"{entry.Key} gives no reason");
        });
    }

    [Fact]
    public void The_worker_references_only_the_contracts()
    {
        var harness = Worker.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("Harness.", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["Harness.Contracts"], harness);
    }

    [Fact]
    public void Every_project_but_the_worker_is_scanned()
    {
        var projects = Directory.GetDirectories(Path.Combine(RepoRoot(), "src"))
            .Where(d => Directory.GetFiles(d, "*.csproj").Length > 0)
            .Select(Path.GetFileName)
            .Where(name => name != Worker.GetName().Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(projects, Scanned().Select(a => a.GetName().Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// THE SCAN CATCHES WHAT IT FORBIDS, AND NAMES THE RIGHT METHOD AND RULE: each probe yields
    /// exactly one violation, its own.
    /// </summary>
    [Theory]
    [InlineData(nameof(PlantsAProcessStart), StartsAProcess)]
    [InlineData(nameof(PlantsAProcRead), ReadsProc)]
    [InlineData(nameof(PlantsACgroupWrite), TouchesACgroup)]
    [InlineData(nameof(PlantsAPrlimit), CallsSetprivOrPrlimit)]
    [InlineData(nameof(PlantsALauncherCall), ReachesAWorkerLauncher)]
    [InlineData(nameof(PlantsADefaultedProcRead), ReadsProc)]
    [InlineData(nameof(kill), SignalsAProcessGroup)]
    public void The_scan_catches_a_planted_violation(string probe, string rule)
    {
        var violations = Violations(Probes(probe));

        Assert.Equal([$"{nameof(WorkerArchitectureTests)}.{probe}: {rule}"], violations);
    }

    /// <summary>A defaulted path is the caller's: the method that declares the default loads nothing.</summary>
    [Fact]
    public void A_defaulted_path_counts_against_its_caller_not_the_method_declaring_it()
    {
        Assert.Empty(Violations(Probes(nameof(ReadsDefault))));
    }

    [Fact]
    public void The_scan_ignores_text_that_is_not_a_path()
    {
        Assert.Empty(Violations(Probes(nameof(HoldsTextThatIsNotAPath))));
    }

    // ---------------------------------------------------------------------------------------------
    // The scan.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every assembly built from <c>src/</c> except the worker.</summary>
    private static IReadOnlyList<Assembly> Scanned() =>
        [.. Directory.GetDirectories(Path.Combine(RepoRoot(), "src"))
            .Where(d => Directory.GetFiles(d, "*.csproj").Length > 0)
            .Select(Path.GetFileName)
            .Where(name => name != Worker.GetName().Name)
            .Select(name => Assembly.Load(new AssemblyName(name!)))];

    private static IEnumerable<MethodBase> Probes(params string[] names) =>
        names.Select(name => (MethodBase)typeof(WorkerArchitectureTests).GetMethod(
            name, BindingFlags.NonPublic | BindingFlags.Static)!);

    private static List<string> Violations(IEnumerable<Assembly> assemblies) =>
        Violations(assemblies.SelectMany(IlScan.Methods));

    /// <summary>Each <c>Owner: rule</c> the methods break, once, in order.</summary>
    private static List<string> Violations(IEnumerable<MethodBase> methods)
    {
        var launchers = Launchers.Value;
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var method in methods)
        {
            if (method.DeclaringType?.Assembly == Worker) continue;

            var owner = IlScan.Owner(method);
            if (IsKill(method)) found.Add($"{owner}: {SignalsAProcessGroup}");

            foreach (var instruction in IlScan.Decode(method))
            {
                foreach (var rule in Rules(instruction)) found.Add($"{owner}: {rule}");

                if (instruction.Method is { } target
                    && target.DeclaringType?.Assembly == Worker
                    && !ProtocolSurface.Contains(OuterType(target.DeclaringType).Name)
                    && launchers.Contains(IlScan.Owner(target)))
                {
                    found.Add($"{owner}: {ReachesAWorkerLauncher}");
                }
            }
        }

        return [.. found];
    }

    /// <summary>The primitives one instruction is.</summary>
    private static IEnumerable<string> Rules(IlScan.Instruction instruction)
    {
        if (instruction.Method is { } target && StartsProcess(target)) yield return StartsAProcess;

        if (instruction.String is not { } text) yield break;

        if (text == "/proc" || text.StartsWith("/proc/", StringComparison.Ordinal)) yield return ReadsProc;

        if (text == "/sys/fs/cgroup" || text.StartsWith("/sys/fs/cgroup/", StringComparison.Ordinal)
            || text is "cgroup.procs" or "memory.max" or "cgroup.subtree_control" or "memory.oom.group")
        {
            yield return TouchesACgroup;
        }

        if (text is "setpriv" or "prlimit"
            || text.EndsWith("/setpriv", StringComparison.Ordinal) || text.EndsWith("/prlimit", StringComparison.Ordinal))
        {
            yield return CallsSetprivOrPrlimit;
        }
    }

    private static bool StartsProcess(MethodBase target) =>
        (target.DeclaringType == typeof(Process) && (target.Name == nameof(Process.Start) || target is ConstructorInfo))
        || (target.DeclaringType == typeof(ProcessStartInfo) && target is ConstructorInfo)
        || (target.DeclaringType?.Name == "PtyProvider" && target.Name == "SpawnAsync");

    private static bool IsKill(MethodBase method) =>
        method.Attributes.HasFlag(MethodAttributes.PinvokeImpl)
        && (method.GetCustomAttribute<DllImportAttribute>()?.EntryPoint ?? method.Name) == "kill";

    private static Type OuterType(Type type)
    {
        while (type.IsNested) type = type.DeclaringType!;
        return type;
    }

    /// <summary>
    /// The worker's methods, by owner, that do one of these things themselves or call a worker method
    /// that does, to a fixed point.
    /// </summary>
    private static readonly Lazy<HashSet<string>> Launchers = new(() =>
    {
        var calls = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var reaching = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in IlScan.Methods(Worker))
        {
            var owner = IlScan.Owner(method);
            if (!calls.TryGetValue(owner, out var callees)) calls[owner] = callees = new(StringComparer.Ordinal);
            if (IsKill(method)) reaching.Add(owner);

            foreach (var instruction in IlScan.Decode(method))
            {
                if (Rules(instruction).Any()) reaching.Add(owner);
                if (instruction.Method is { } target && target.DeclaringType?.Assembly == Worker) callees.Add(IlScan.Owner(target));
            }
        }

        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (owner, callees) in calls)
            {
                if (!reaching.Contains(owner) && callees.Overlaps(reaching)) changed = reaching.Add(owner) | changed;
            }
        }

        return reaching;
    });

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }

    // ---------------------------------------------------------------------------------------------
    // Probes: never called, only scanned.
    // ---------------------------------------------------------------------------------------------

    private static void PlantsAProcessStart() => Process.Start("true")?.Dispose();

    private static string PlantsAProcRead() => File.ReadAllText("/proc/self/status");

    private static void PlantsACgroupWrite() => File.WriteAllText("/sys/fs/cgroup/x/memory.max", "1");

    private static string[] PlantsAPrlimit() => ["/usr/bin/prlimit", "--data=1:1", "--"];

    private static object? PlantsALauncherCall() => ChildProcess.StartInfo("true", "/", runAs: null);

    private static string PlantsADefaultedProcRead() => ReadsDefault();

    private static string ReadsDefault(string path = "/proc/self/stat") => File.ReadAllText(path);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private static string[] HoldsTextThatIsNotAPath() =>
        ["/processes", "/proc-like", "see the process group in /proc, every 5 s", "prlimit is a command", "/sys/fs/cgroupish"];
}
