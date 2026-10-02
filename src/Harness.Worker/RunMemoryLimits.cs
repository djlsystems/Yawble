using Harness.Contracts;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>How a run's own memory limit is applied on this machine.</summary>
public enum RunMemoryMechanism
{
    /// <summary>Nothing can be applied; the admission threshold is the only guard.</summary>
    None,

    /// <summary>A child cgroup per run with <c>memory.max</c>, where a writable cgroup is delegated.</summary>
    Cgroup,

    /// <summary><c>RLIMIT_DATA</c> on each of the run's processes, through <c>prlimit</c> in the launch prefix.</summary>
    Rlimit,
}

/// <summary>
/// HOW MUCH MEMORY ONE RUN MAY USE, AND HOW THAT IS ENFORCED. Decided once, when the Host starts,
/// and logged as "Run memory limits: cgroup | rlimit | not available — &lt;reason&gt;", the way
/// <see cref="AgentLaunchUser"/> logs who agents run as.
///
/// <para>
/// FROM INSIDE THE CONTAINER ONLY. The Host never asks Podman or Docker; it reads its own cgroup.
/// The best mechanism the machine offers wins:
/// </para>
/// <list type="number">
/// <item><b>cgroup</b>: cgroup v2, the memory controller available, and the Host's own cgroup
/// writable (measured by making and removing a child). The engine delegated a cgroup, so each run
/// gets a child with <c>memory.max</c>, <c>memory.oom.group</c> and no swap; a run the kernel
/// OOM-kills there is read back from <c>memory.events</c>.</item>
/// <item><b>rlimit</b>: otherwise, when <c>prlimit</c> is in a root-owned system directory, and
/// ONLY WHEN A PERSON SET <c>runs.memoryLimitMb</c> (<see cref="RunMemoryLimit.Set"/>). With the
/// setting at 0 a run gets no data limit and no hard ceiling at all, and the start log says
/// <see cref="NotEnforcedLine"/>: <c>RLIMIT_DATA</c> counts a runtime's virtual reservations, not the
/// memory it uses, and the figure below which a runtime refuses to start depends on the runtime and
/// the CPU architecture, so no computed figure is safe. Admission by measured memory guards the
/// container instead. With a figure set, the launch prefix gives the agent <c>RLIMIT_DATA</c> (soft
/// and hard), which every process it starts inherits. No capability is needed to lower one's own limit, and it runs AFTER the
/// <see cref="AgentLaunchUser"/> prefix, so the agent - which holds no capability - cannot raise
/// it again. It bounds EACH PROCESS, not the run's sum: a run with three processes may use three
/// times it. <c>RLIMIT_DATA</c> rather than <c>RLIMIT_AS</c>: the address space counts reservations
/// nothing has touched (a .NET process reserves tens of GB it never uses), while the data limit
/// counts writable private memory, which is close to what a process really uses.</item>
/// <item><b>not available</b>: neither, said plainly. The Host keeps its headroom by admission
/// alone.</item>
/// </list>
///
/// <para>
/// HOW MUCH is <c>TenantSettings.RunMemoryLimit</c>, read through a delegate at every launch
/// so a change applies to the next run without a restart.
/// </para>
/// <para>
/// A RUN HOLDING THE HEAVY LEASE GETS MORE (<see cref="RunAllowances"/>): its cgroup's
/// <c>memory.max</c> is raised in place, or under rlimit its processes' soft limit is raised up to
/// the hard limit every run is launched with, <see cref="Ceiling"/>.
/// </para>
/// </summary>
public sealed partial class RunMemoryLimits
{
    /// <summary>The start of the Host log line, and of what doctor reports.</summary>
    public const string LogPrefix = "Run memory limits";

    /// <summary>
    /// The start log line under rlimit with no figure set: nothing is enforced per run, and why.
    /// </summary>
    public const string NotEnforcedLine =
        LogPrefix + ": not enforced per run - the cgroup is not writable; admission by measured memory guards the container";

    /// <summary>The name of the leaf the Host's own processes move to when it enables the memory controller.</summary>
    public const string HostLeaf = "host";

    /// <summary>The start of every run's cgroup name.</summary>
    public const string RunPrefix = "run-";

    private readonly Func<RunMemoryLimit> _limit;
    private readonly Func<RunMemoryLimit> _ceiling;

    /// <param name="ceiling">The most a run holding the heavy lease can be raised to
    /// (<c>TenantSettings.RunMemoryCeiling</c>); the run's own limit when not given.</param>
    public RunMemoryLimits(
        RunMemoryMechanism mechanism, string reason, Func<RunMemoryLimit> limit,
        string? prlimitPath = null, string? cgroupDirectory = null, Func<RunMemoryLimit>? ceiling = null)
    {
        Mechanism = mechanism;
        Reason = reason;
        _limit = limit;
        _ceiling = ceiling ?? limit;
        PrlimitPath = prlimitPath;
        CgroupDirectory = cgroupDirectory;
    }

    public RunMemoryMechanism Mechanism { get; }

    /// <summary>Why this mechanism, in words.</summary>
    public string Reason { get; }

    /// <summary><c>prlimit</c>, from a system directory, when <see cref="Mechanism"/> is rlimit.</summary>
    public string? PrlimitPath { get; }

    /// <summary>The cgroup each run's child is made in, when <see cref="Mechanism"/> is cgroup.</summary>
    public string? CgroupDirectory { get; }

    /// <summary>What the Host logs at start, and what doctor reports.</summary>
    public string LogLine => NotEnforced ? NotEnforcedLine : Mechanism switch
    {
        RunMemoryMechanism.Cgroup => $"{LogPrefix}: cgroup - {Reason}; {HeavyWords}, its memory.max raised in place",
        RunMemoryMechanism.Rlimit => $"{LogPrefix}: rlimit - {Reason}; {HeavyWords}, its processes' soft limit "
            + "raised in place up to the hard limit every run starts with (container limit - Host reserve), "
            + "which the agent cannot raise",
        _ => $"{LogPrefix}: not available — {Reason}",
    };

    /// <summary>What a heavy run gets, in the log line's words.</summary>
    public const string HeavyWords =
        "a run holding the heavy lease gets the container's limit - the Host reserve - what the other running runs "
        + "are measured to use, never less than its own limit";

    /// <summary>
    /// Whether runs go without a limit of their own because the mechanism is rlimit and nobody set
    /// <c>runs.memoryLimitMb</c>. Read through the setting's delegate, so setting it applies to the next run.
    /// </summary>
    public bool NotEnforced => Mechanism == RunMemoryMechanism.Rlimit && !_limit().Set;

    /// <summary>
    /// The limit in force for the next run. Under rlimit only a figure a person set: a computed one
    /// is never applied, and the run gets none.
    /// </summary>
    public RunMemoryLimit Limit()
    {
        var limit = _limit();
        return Mechanism == RunMemoryMechanism.Rlimit && !limit.Set ? Unset() : limit;
    }

    /// <summary>
    /// The most a run can be raised to while it holds the heavy lease; under rlimit, each process's
    /// hard limit, and none when no figure is set - no voluntary ceiling either.
    /// </summary>
    public RunMemoryLimit Ceiling()
    {
        var limit = _limit();
        return Mechanism == RunMemoryMechanism.Rlimit && !limit.Set ? Unset() : _ceiling();
    }

    /// <summary>
    /// WHAT THE NEXT RUN GETS, as <c>GET /api/wip</c> states it in <c>runMemory</c>: the mechanism
    /// and the figure <see cref="Prefix"/> and <see cref="BeginRun"/> apply, read from <see cref="Limit"/>
    /// as a launch reads it, never worked out again elsewhere. <c>none</c> with no figure whenever
    /// nothing is applied - no mechanism, or a mechanism with no limit (under rlimit, nobody set one).
    /// </summary>
    public RunMemoryReport Report()
    {
        var limit = Limit();
        if (Mechanism == RunMemoryMechanism.None || limit.Mb is not { } mb)
        {
            return new RunMemoryReport(RunMemoryReport.None, null,
                NotEnforced ? NotEnforcedLine
                : Mechanism == RunMemoryMechanism.None ? LogLine
                : $"{LogPrefix}: not enforced per run - {limit.Source}");
        }

        return new RunMemoryReport(
            Mechanism == RunMemoryMechanism.Cgroup ? RunMemoryReport.Cgroup : RunMemoryReport.Rlimit, mb,
            $"{LogLine} (each run now: {mb} MB, as {limit.Source})");
    }

    private static RunMemoryLimit Unset() => new(null,
        $"{SettingNames.RunsMemoryLimitMb} is not set and the cgroup is not writable, so no per-run limit is "
        + "applied; admission by measured memory guards the container");

    /// <summary>Nothing is applied: what a runner without this service, and the suite, gets.</summary>
    public static RunMemoryLimits NotAvailable(string reason) =>
        new(RunMemoryMechanism.None, reason, () => new RunMemoryLimit(null, reason));

    /// <summary>
    /// The decision, with every fact about the machine passed in. <see cref="Resolve"/> gathers them;
    /// this is where the rule lives, so a test can walk each branch.
    /// </summary>
    /// <param name="cgroup">What the Host's own cgroup offers (see <see cref="ProbeCgroup"/>).</param>
    /// <param name="prlimit"><c>prlimit</c> in a system directory, or null.</param>
    public static RunMemoryLimits Decide(
        CgroupFacts cgroup, string? prlimit, Func<RunMemoryLimit> limit, Func<RunMemoryLimit>? ceiling = null)
    {
        if (cgroup.Usable is { } directory)
        {
            return new RunMemoryLimits(RunMemoryMechanism.Cgroup,
                $"the Host's cgroup {directory} is delegated and writable, so each run gets a child cgroup with memory.max",
                limit, cgroupDirectory: directory, ceiling: ceiling);
        }

        if (prlimit is not null)
        {
            return new RunMemoryLimits(RunMemoryMechanism.Rlimit,
                $"{cgroup.Why}, so each of a run's processes gets RLIMIT_DATA through {prlimit}: the limit "
                + "is per process, not the run's total",
                limit, prlimitPath: prlimit, ceiling: ceiling);
        }

        return new RunMemoryLimits(RunMemoryMechanism.None,
            $"{cgroup.Why}, and prlimit is not in a root-owned system directory "
            + $"({string.Join(", ", SystemCommand.Directories)}); the Host keeps its headroom by admission alone",
            limit, ceiling: ceiling);
    }

    /// <summary>Reads this machine and decides. Never throws: a fact it cannot read counts against its mechanism.</summary>
    public static RunMemoryLimits Resolve(
        Func<RunMemoryLimit> limit, string cgroupRoot = "/sys/fs/cgroup", string procSelfCgroup = "/proc/self/cgroup",
        Func<RunMemoryLimit>? ceiling = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new RunMemoryLimits(RunMemoryMechanism.None, "the Host is not running on Linux", limit, ceiling: ceiling);
        }

        var cgroup = ProbeCgroup(cgroupRoot, procSelfCgroup);
        if (cgroup.Usable is not null && Prepare(cgroup.Usable) is { } failed)
        {
            cgroup = cgroup with { Usable = null, Why = failed };
        }

        return Decide(cgroup, SystemCommand.Find("prlimit"), limit, ceiling);
    }

    /// <summary>
    /// What the Host's own cgroup offers. <see cref="CgroupFacts.Usable"/> is the directory when it
    /// is cgroup v2 with the memory controller and a child can be made there; otherwise null, and
    /// <see cref="CgroupFacts.Why"/> says which fact failed.
    /// </summary>
    public static CgroupFacts ProbeCgroup(string cgroupRoot, string procSelfCgroup)
    {
        try
        {
            if (!File.Exists(Path.Combine(cgroupRoot, "cgroup.controllers")))
            {
                return new CgroupFacts(null, $"{cgroupRoot} is not a cgroup v2 hierarchy (no cgroup.controllers)");
            }

            // "0::/path" is the v2 line; a v1-only or hybrid host has other lines first.
            var line = File.ReadLines(procSelfCgroup).FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal));
            if (line is null)
            {
                return new CgroupFacts(null, $"{procSelfCgroup} names no cgroup v2 group for the Host");
            }

            var relative = line[3..].Trim().TrimStart('/');
            var directory = relative.Length == 0 ? cgroupRoot : Path.Combine(cgroupRoot, relative);
            if (!Directory.Exists(directory))
            {
                return new CgroupFacts(null, $"the Host's cgroup {directory} is not visible inside the container");
            }

            var controllers = File.ReadAllText(Path.Combine(directory, "cgroup.controllers"))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!controllers.Contains("memory"))
            {
                return new CgroupFacts(null, $"the memory controller is not delegated to the Host's cgroup {directory}");
            }

            var probe = Path.Combine(directory, "run-memory-probe-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CgroupFacts(null, $"the Host's cgroup {directory} is not writable ({ex.Message})");
            }

            try { Directory.Delete(probe); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new CgroupFacts(directory, $"the Host's cgroup {directory} is writable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CgroupFacts(null, $"the cgroup could not be read ({ex.Message})");
        }
    }

    /// <summary>
    /// Makes <paramref name="directory"/> able to hold run cgroups with a memory limit: the memory
    /// controller enabled for its children. cgroup v2 allows that only in a group with no process of
    /// its own, so every process in it moves to a <see cref="HostLeaf"/> child first. Null when done;
    /// otherwise the sentence saying which step failed (the caller falls back).
    /// </summary>
    public static string? Prepare(string directory)
    {
        try
        {
            var subtree = Path.Combine(directory, "cgroup.subtree_control");
            if (File.ReadAllText(subtree).Split(' ', StringSplitOptions.TrimEntries).Contains("memory")) return null;

            var leaf = Directory.CreateDirectory(Path.Combine(directory, HostLeaf)).FullName;
            foreach (var pid in File.ReadAllLines(Path.Combine(directory, "cgroup.procs")).Where(l => l.Length > 0))
            {
                // A process that ended in between is not an error.
                try { File.AppendAllText(Path.Combine(leaf, "cgroup.procs"), pid); } catch (IOException) { }
            }

            File.AppendAllText(subtree, "+memory");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the Host's cgroup {directory} is writable but the memory controller could not be enabled for runs ({ex.Message})";
        }
    }

    /// <summary>
    /// What goes in front of the agent's command for <paramref name="limit"/>: <c>prlimit --data</c>
    /// when the mechanism is rlimit and there is a limit, nothing otherwise. The soft limit is
    /// <paramref name="limit"/>; the hard limit is <paramref name="ceiling"/> when it is higher, so
    /// the Host can raise the soft limit in place for a run that takes the heavy lease. Raising a
    /// hard limit needs CAP_SYS_RESOURCE in the machine's first user namespace, which no rootless
    /// engine gives and the Host does not hold; the agent, which holds no capability, cannot raise
    /// the hard limit either. It can raise its own soft limit up to that hard limit - that is the
    /// price of an in-place raise without a capability, and the hard limit still bounds it.
    /// </summary>
    public IReadOnlyList<string> Prefix(RunMemoryLimit limit, RunMemoryLimit? ceiling = null)
    {
        if (Mechanism != RunMemoryMechanism.Rlimit || PrlimitPath is not { } prlimit || limit.Mb is not { } mb) return [];

        var hard = ceiling?.Mb is { } top && top > mb ? top : mb;
        return [prlimit, $"--data={Bytes(mb)}:{Bytes(hard)}", "--"];
    }

    /// <summary>
    /// A cgroup for one run, made before it starts, when the mechanism is cgroup and there is a limit.
    /// Null otherwise, or when it could not be made (the run then starts without one, and the caller says so).
    /// </summary>
    public RunCgroup? BeginRun(RunMemoryLimit limit)
    {
        if (Mechanism != RunMemoryMechanism.Cgroup || CgroupDirectory is not { } parent || limit.Mb is not { } mb) return null;
        return RunCgroup.Create(parent, Bytes(mb));
    }

    /// <summary>
    /// Whether a run that applied <paramref name="limit"/> and ended this way was stopped by it. With
    /// a cgroup it is the kernel's own count (<c>oom_kill</c>); with an rlimit the process is refused
    /// memory and says so, so it is a non-zero exit whose error output says memory ran out.
    /// </summary>
    public bool StoppedBy(RunMemoryLimit limit, int exitCode, string errors, RunCgroup? cgroup)
    {
        if (limit.Mb is null || exitCode == 0) return false;

        return Mechanism switch
        {
            RunMemoryMechanism.Cgroup => cgroup?.OomKilled() == true,
            RunMemoryMechanism.Rlimit => OutOfMemoryWords().IsMatch(errors),
            _ => false,
        };
    }

    /// <summary>The run's error: what the limit was, where it came from, and the setting that raises it.</summary>
    public string Sentence(RunMemoryLimit limit) =>
        $"This run went over its memory limit of {limit.Mb} MB and was stopped ({limit.Source}; applied as "
        + (Mechanism == RunMemoryMechanism.Cgroup ? "a cgroup for the whole run" : "a limit on each of its processes")
        + $"). Raise {SettingNames.RunsMemoryLimitMb} in the Tenant Settings, or make the work use less. "
        + "This is not an agent fault.";

    /// <summary>
    /// The progress line for a process in the run that the limit stopped while the agent carried on:
    /// which process signature was seen, the limit in force, where it came from, and the setting.
    /// </summary>
    public string ChildSentence(RunMemoryLimit limit, string seen) =>
        $"A process in this run was stopped by the run's memory limit ({seen}): the limit is {limit.Mb} MB "
        + (Mechanism == RunMemoryMechanism.Cgroup ? "for the whole run" : "for each of its processes")
        + $" ({limit.Source}). The agent carried on, but that process's work did not finish. Raise "
        + $"{SettingNames.RunsMemoryLimitMb} in the Tenant Settings, or take the heavy lease before heavy work: "
        + "a run holding it gets the container's memory less the Host's reserve and what the other runs use. "
        + "This is not an agent fault.";

    /// <summary>
    /// The text a process refused memory under an rlimit leaves in the run's transcript, or null.
    /// Strict, because the transcript also holds every file the agent read: only a line that IS the
    /// message counts - the runtime's own "Out of memory.", or the operating system's ENOMEM words
    /// after a program's name - never a line that merely mentions it. JSON escapes are undone and
    /// both a newline and a string's quotes end a line, so a tool result's text is read as printed.
    /// </summary>
    public static string? ChildOutOfMemory(string transcriptLine)
    {
        if (!transcriptLine.Contains("memory", StringComparison.OrdinalIgnoreCase)) return null;

        var text = transcriptLine
            .Replace("\\\\", "\u0001", StringComparison.Ordinal)
            .Replace("\\\"", "'", StringComparison.Ordinal)
            .Replace("\\r", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", " ", StringComparison.Ordinal);

        foreach (var segment in text.Split('\n', '"', '\r'))
        {
            var line = segment.Trim();
            if (line.Length is > 0 and < 160 && ChildOutOfMemoryLine().IsMatch(line)) return line;
        }

        return null;
    }

    internal static long Bytes(long mb) => mb * 1024 * 1024;

    /// <summary>A whole line that is a refusal of memory: "Out of memory.", or "prog: fork: Cannot allocate memory".</summary>
    [GeneratedRegex(@"^(?:[^\s:'][^:']{0,80}: ){0,3}(?:out of memory|cannot allocate memory|memory exhausted)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ChildOutOfMemoryLine();

    /// <summary>
    /// What a program refused memory says. The operating system's own words for ENOMEM and the
    /// phrases runtimes and tools print for it; nothing names a language or tool.
    /// </summary>
    [GeneratedRegex(@"cannot allocate memory|out of memory|memory exhausted|allocation failed", RegexOptions.IgnoreCase)]
    private static partial Regex OutOfMemoryWords();
}

/// <summary>
/// <c>GET /api/wip</c>'s <c>runMemory</c>: <see cref="Mechanism"/> is <c>cgroup</c>, <c>rlimit</c> or
/// <c>none</c>; <see cref="PerRunMb"/> the megabytes each run is held to, null when nothing is enforced;
/// <see cref="Detail"/> the Host's own sentence. See <see cref="RunMemoryLimits.Report"/>.
/// </summary>
public sealed record RunMemoryReport(string Mechanism, long? PerRunMb, string Detail)
{
    public const string Cgroup = "cgroup";
    public const string Rlimit = "rlimit";
    public const string None = "none";
}

/// <summary>What the Host's own cgroup offers: the directory when runs can get a child there, and why or why not.</summary>
public sealed record CgroupFacts(string? Usable, string Why);

/// <summary>
/// One run's cgroup: made with its limit before the run starts, the run's process added once it
/// exists, read for an OOM kill when it ends, and removed after.
/// </summary>
public sealed class RunCgroup : IDisposable
{
    private RunCgroup(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>A new child of <paramref name="parent"/> limited to <paramref name="bytes"/>, or null when it could not be made.</summary>
    public static RunCgroup? Create(string parent, long bytes)
    {
        var directory = Path.Combine(parent, RunMemoryLimits.RunPrefix + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "memory.max"), bytes.ToString(CultureInfo.InvariantCulture));

            // Whole group at once, and no swap to hide in. Either may be absent; neither is required.
            TryWrite(Path.Combine(directory, "memory.oom.group"), "1");
            TryWrite(Path.Combine(directory, "memory.swap.max"), "0");
            return new RunCgroup(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { System.IO.Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    /// <summary>
    /// Puts <paramref name="pid"/> in this cgroup. The launch chain (setsid, setpriv, prlimit, the
    /// agent) execs in place, so the pid is the agent's; what it forks after this is inside too.
    /// </summary>
    public bool Add(int pid) => TryWrite(Path.Combine(Directory, "cgroup.procs"), pid.ToString(CultureInfo.InvariantCulture));

    /// <summary>Whether the kernel OOM-killed anything in this cgroup (<c>memory.events</c> <c>oom_kill</c> above 0).</summary>
    public bool OomKilled() => OomKills() > 0;

    /// <summary>
    /// Sets this run's <c>memory.max</c> in place: how a run that takes the heavy lease is raised and
    /// lowered again. The kernel applies it to the processes already in the group.
    /// </summary>
    public bool SetMax(long bytes) => TryWrite(Path.Combine(Directory, "memory.max"), bytes.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// What the run uses that the kernel cannot give back by dropping file cache: <c>memory.current</c>
    /// less <c>memory.stat</c>'s <c>file</c>. Lowering <c>memory.max</c> below this would OOM-kill the
    /// run, so it is lowered only when this fits. Null when it cannot be read.
    /// </summary>
    public long? Unreclaimable()
    {
        try
        {
            if (!long.TryParse(File.ReadAllText(Path.Combine(Directory, "memory.current")).Trim(), CultureInfo.InvariantCulture, out var current))
            {
                return null;
            }

            long file = 0;
            var stat = Path.Combine(Directory, "memory.stat");
            if (File.Exists(stat))
            {
                foreach (var line in File.ReadLines(stat))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts is ["file", var bytes] && long.TryParse(bytes, CultureInfo.InvariantCulture, out var n)) file = n;
                }
            }

            return Math.Max(0, current - file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>How many processes the kernel has OOM-killed in this cgroup (<c>memory.events</c> <c>oom_kill</c>).</summary>
    public long OomKills()
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(Directory, "memory.events")))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts is ["oom_kill", var count] && long.TryParse(count, CultureInfo.InvariantCulture, out var n)) return n;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return 0;
    }

    /// <summary>Removes the cgroup; its processes are gone by now (the run's group is killed when it ends).</summary>
    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool TryWrite(string path, string value)
    {
        try
        {
            File.WriteAllText(path, value);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
