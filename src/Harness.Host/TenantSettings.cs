using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Identity;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>What a setting's value is, which decides how it is parsed, validated and sent.</summary>
public enum TenantSettingKind
{
    /// <summary>A whole number, sent as a JSON number.</summary>
    Integer,

    /// <summary>A <see cref="TimeSpan"/>, sent as a string in constant format (<c>08:00:00</c>,
    /// <c>1.00:00:00</c>).</summary>
    Duration,

    /// <summary>A JSON object mapping a kanban lane id to a whole-number limit.</summary>
    LaneLimits,

    /// <summary>One of a fixed set of strings.</summary>
    Choice,

    /// <summary>A JSON array of OS package names, each checked by <see cref="SystemPackages.IsValidName"/>.</summary>
    PackageList,

    /// <summary>A JSON object mapping a name to a list of tags (strings). <c>agents.tags</c>.</summary>
    TagMap,
}

/// <summary>One setting the Tenant Settings dialog can change.</summary>
public sealed record TenantSettingDefinition(
    string Name,
    TenantSettingKind Kind,
    string BuiltInDefault,
    string? ConfigKey,
    string Description,
    long Min = 0,
    long Max = long.MaxValue,
    TimeSpan? MinDuration = null,
    TimeSpan? MaxDuration = null,
    IReadOnlyList<string>? Choices = null);

/// <summary>A value a PUT refused, naming the field.</summary>
public sealed class TenantSettingRejected(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// INSTANCE-WIDE SETTINGS, CHANGED WITHOUT A RESTART.
///
/// <para>
/// Each setting is read, in order, from its <c>tenant_settings</c> row, then from
/// <c>appsettings.json</c> (the setting's configuration key), then from a built-in default.
/// The table is read once at start and kept in memory; a write goes to the table first and then to
/// memory, so every reader - each asks on use, never at construction - sees it at its next use.
/// </para>
///
/// <para>
/// A reader that cached one of these values in a constructor would bring the restart back. Each
/// consumer is handed a delegate onto this class, not a number.
/// </para>
/// </summary>
public sealed class TenantSettings
{
    public const string WipMaxRunningName = "wip.maxRunning";
    public const string WipMemoryPerRunMbName = "wip.memoryPerRunMb";
    public const string AdmissionMemoryPercentName = "admission.memoryPercent";
    public const string AdmissionMemoryPressurePercentName = "admission.memoryPressurePercent";
    public const string RunsMemoryLimitMbName = Harness.Contracts.SettingNames.RunsMemoryLimitMb;
    public const string WorkflowSpendLimitName = "workflow.spendLimit";
    public const string ConciergeIdleTimeoutName = "concierge.idleTimeout";
    public const string QuietWindowName = "quiet.window";
    public const string ResumeMaxAutomaticName = "resume.maxAutomatic";
    public const string CausationDepthLimitName = "causation.depthLimit";
    public const string KanbanWipLimitsName = "kanban.wipLimits";
    public const string ThemeDefaultName = "theme.default";
    public const string SystemPackagesName = "system.packages";
    public const string AgentTagsName = "agents.tags";
    public const string OutcomesRequireForCompletionName = "outcomes.requireForCompletion";
    public const string LeasesHeavyHoldersName = "leases.heavy.holders";

    private readonly SqliteTenantSettingsStore _store;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly Dictionary<string, TenantSettingDefinition> _definitions;
    private readonly Dictionary<string, string> _fallbacks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _configured = new(StringComparer.Ordinal);
    private readonly int? _cpus;
    private readonly long? _memoryLimitMb;
    private volatile IReadOnlyDictionary<string, TenantSettingRow> _rows =
        new Dictionary<string, TenantSettingRow>(StringComparer.Ordinal);

    /// <param name="cpuCount">CPUs the container may use; null reads the cgroup, 0 is not known.</param>
    /// <param name="memoryLimitMb">The container's memory limit in MB; null reads the cgroup, 0 is
    /// no limit.</param>
    public TenantSettings(
        SqliteTenantSettingsStore store, IConfiguration configuration, int? cpuCount = null, long? memoryLimitMb = null)
    {
        _store = store;
        _configuration = configuration;
        _cpus = (cpuCount ?? CgroupCpus()) is > 0 and var cpus ? cpus : null;
        _memoryLimitMb = (memoryLimitMb ?? CgroupMemoryMb()) is > 0 and var mb ? mb : null;

        // The running limit's built-in default depends on wip.memoryPerRunMb, which can change at
        // runtime, so it is computed on read (RunLimit); this figure is only the definition's.
        var wipDefault = Bounds(DefaultMemoryPerRunMb).Limit;

        Definitions =
        [
            new(WipMaxRunningName, TenantSettingKind.Integer,
                wipDefault.ToString(CultureInfo.InvariantCulture), "Wip:MaxRunning",
                "Agents running at once, across all teams, Managers included. Work over this number "
                + "waits its turn; nothing is refused. A Manager may use one slot above it, so a pool "
                + "full of members never starves the Manager that would free them. 0 means no limit. "
                + "Takes effect immediately: raising it starts waiting work, lowering it stops nothing "
                + "that is running. Default: the smaller of a CPU bound, max(1, CPUs in the container's "
                + "cgroup - 1) or 3 when the CPUs are not known, and, when the container has a memory "
                + "limit, a memory bound, floor(limit / wip.memoryPerRunMb), at least 1.",
                Min: 0, Max: 1000),
            new(WipMemoryPerRunMbName, TenantSettingKind.Integer,
                DefaultMemoryPerRunMb.ToString(CultureInfo.InvariantCulture), "Wip:MemoryPerRunMb",
                "Megabytes of the container's memory limit counted for each agent run - the CLI, its "
                + "builds and its test hosts - when working out how many runs fit. It caps no run: "
                + "nothing is stopped for using more. When the container has a memory limit, the default "
                + "for wip.maxRunning is at most the limit divided by this. It changes only that default: "
                + "a value set for wip.maxRunning wins. Takes effect immediately.",
                Min: 1, Max: 1_048_576),
            new(AdmissionMemoryPercentName, TenantSettingKind.Integer, "80", "Admission:MemoryPercent",
                "A run waits to start while the container's memory in use (anonymous memory and shmem, "
                + "which the kernel cannot reclaim) is at or above this percentage of its memory limit, "
                + "even when wip.maxRunning has room. It waits as it would for a slot, and is never "
                + "refused. When the container's memory cannot be measured, only wip.maxRunning applies. "
                + "0 turns this check off. Takes effect immediately.",
                Min: 0, Max: 100),
            new(AdmissionMemoryPressurePercentName, TenantSettingKind.Integer, "10", "Admission:MemoryPressurePercent",
                "A run waits to start while the container's memory pressure (the share of the last "
                + "10 seconds some work stalled waiting for memory) is at or above this percentage, "
                + "even when wip.maxRunning has room. It waits as it would for a slot, and is never "
                + "refused. When pressure cannot be measured, only wip.maxRunning applies. 0 turns "
                + "this check off. Takes effect immediately.",
                Min: 0, Max: 100),
            new(RunsMemoryLimitMbName, TenantSettingKind.Integer, "0", "Runs:MemoryLimitMb",
                "Megabytes of a per-process data limit on each of an agent run's processes, applied "
                + "where the Host cannot write its cgroup. It caps the memory a process reserves, not the "
                + "memory it uses, and some runtimes reserve far more than they use: below a few GB some "
                + "refuse to start at all. 0 (the default) sets no per-run cap there; admission by "
                + "measured memory guards the container instead. Where the Host can write its cgroup, "
                + "each run gets its own cgroup instead, limited to this figure, or when it is 0 to (the "
                + $"container's memory limit - {HostReserveMb} MB kept for the Host) / wip.maxRunning, at "
                + $"least {MinRunMemoryLimitMb} MB. Which applies is decided at start and logged as \"Run "
                + "memory limits\". A run holding the heavy lease gets more while it holds it, when it has "
                + $"a limit at all: the container's memory limit - {HostReserveMb} MB for the Host - what the "
                + "other running runs are measured to use at that moment, never less than this limit; it "
                + "goes back to this limit when the lease is released, once the run is back inside it. "
                + "Applies to the next run.",
                Min: 0, Max: 1_048_576),
            new(WorkflowSpendLimitName, TenantSettingKind.Integer, "100000000", "WorkflowSpendLimit",
                "The instance's per-workflow token ceiling, the backstop under every team's own "
                + "budget. 0 means none. Settable from the product: there is no admin tier "
                + "and every person can raise their team's figure, so this is a visible, audited "
                + "setting - every change is in the tenant log with the person who made it. Applies "
                + "from the next delivery.",
                Min: 0),
            new(ConciergeIdleTimeoutName, TenantSettingKind.Duration, "08:00:00", "ConciergeIdleTimeout",
                "How long a Concierge session may sit unattended before it is ended. Applies at the "
                + "next sweep.",
                MinDuration: TimeSpan.FromMinutes(1), MaxDuration: TimeSpan.FromDays(30)),
            new(QuietWindowName, TenantSettingKind.Duration, "00:30:00", "QuietSweepWindow",
                "How long a team may be quiet with work open before the quiet-team sweep acts on it. "
                + "Applies at the next sweep.",
                MinDuration: TimeSpan.FromMinutes(1), MaxDuration: TimeSpan.FromDays(30)),
            new(ResumeMaxAutomaticName, TenantSettingKind.Integer,
                ResumeSweep.DefaultMaxAutomaticResumes.ToString(CultureInfo.InvariantCulture),
                "ResumeMaxAutomatic",
                "How many times the platform resumes one workflow by itself after a provider limit "
                + "before leaving it to a person. 0 turns automatic resumes off. Applies at the next "
                + "sweep.",
                Min: 0, Max: 100),
            new(CausationDepthLimitName, TenantSettingKind.Integer, "25", "CausationDepthLimit",
                "How deep one chain of tells may go before the next tell is refused - a backstop "
                + "against agents answering each other in a loop. 0 means no limit. Applies to the "
                + "next tell.",
                Min: 0, Max: 10000),
            new(KanbanWipLimitsName, TenantSettingKind.LaneLimits, "{}", "Kanban:WipLimits",
                "Advisory per-lane limits for the kanban board, lane id to limit. The header turns "
                + "amber over the limit; nothing is blocked. The in-progress lane's limit is "
                + "wip.maxRunning and is not set here. Applies at the next board fetch.",
                Min: 0, Max: 10000),
            new(ThemeDefaultName, TenantSettingKind.Choice, "auto", "Theme:Default",
                "The theme a browser uses until the person picks one. Applies at the next load.",
                Choices: ["auto", "light", "dark"]),
            new(SystemPackagesName, TenantSettingKind.PackageList, "[]", "SystemPackages",
                "OS packages the container installs with apt-get as root at every start, before it "
                + "drops to the host's own user. Agents are not root and cannot install one "
                + "themselves. " + Harness.Host.SystemPackages.RestartSentence + " Applies at the next start: the "
                + "host writes the list to the data root, where the entrypoint reads it.",
                Max: Harness.Host.SystemPackages.MaxCount),
            new(AgentTagsName, TenantSettingKind.TagMap, "{}", null,
                "The operator's tags for built-in Agent presets, preset name to its list of tags "
                + "A hire asking for a tag picks among the team's allowed presets that carry "
                + "it. Only a built-in preset's tags are overridden here: everything else about it "
                + "comes from the build, and a custom preset's tags are edited on the preset. A preset "
                + "absent from the map carries the build's tags; removing its entry resets it. An "
                + "entry naming no built-in preset is ignored, and GET /api/agents lists it under "
                + "ignoredTagOverrides. Applies to the next hire.",
                Max: 64),
            new(OutcomesRequireForCompletionName, TenantSettingKind.Choice, "off", "Outcomes:RequireForCompletion",
                "Whether an agent's declaration that a workflow is complete is refused while the workflow "
                + "has no outcome. `off` (the default) refuses nothing: unlinked work is counted as "
                + "\"No outcome\". `on` refuses the declaration with a sentence naming the `outcome` tool. "
                + "A person's close and the platform's own declarations are never refused. Applies to the "
                + "next declaration.",
                Choices: ["off", "on"]),
            new(LeasesHeavyHoldersName, TenantSettingKind.Integer, "1", "Leases:Heavy:Holders",
                "How many runs may hold the `heavy` lease at once, across all teams. An agent takes it "
                + "before anything it knows to be heavy - the repository's full test command, a full "
                + "build, an image build - and the rest wait their turn in order. Applies at the next "
                + "acquire or release.",
                Min: 1, Max: 1000),
        ];

        _definitions = Definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);

        foreach (var definition in Definitions)
        {
            var configured = FromConfiguration(definition);
            if (configured is not null) _configured.Add(definition.Name);
            _fallbacks[definition.Name] = configured ?? definition.BuiltInDefault;
        }
    }

    /// <summary>The built-in per-run memory allowance, in MB.</summary>
    public const int DefaultMemoryPerRunMb = 2048;

    /// <summary>Megabytes of the container's limit the computed per-run limit leaves to the Host.</summary>
    public const int HostReserveMb = 1024;

    /// <summary>The floor of the computed per-run limit: below it an agent CLI cannot start.</summary>
    public const int MinRunMemoryLimitMb = 512;

    /// <summary>The CPU bound when the cgroup does not say how many CPUs there are.</summary>
    public const int UnknownCpuBound = 3;

    public IReadOnlyList<TenantSettingDefinition> Definitions { get; }

    /// <summary>Raised after a write or a reset, once per changed setting, with the setting's name.</summary>
    public event Action<string>? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var rows = new Dictionary<string, TenantSettingRow>(StringComparer.Ordinal);

        foreach (var row in await _store.ReadAllAsync(ct))
        {
            // A row this build does not know, or one that does not validate, is skipped rather
            // than refusing to start: the fallback applies, and the row stays for a build that does.
            if (!_definitions.TryGetValue(row.Name, out var definition)) continue;
            if (!TryCanonical(definition, row.Value, out _)) continue;
            rows[row.Name] = row;
        }

        _rows = rows;
    }

    // ---- Typed readers. Each is read on use; none may be cached by a consumer. ----

    /// <summary><c>wip.maxRunning</c>. 0 is unlimited.</summary>
    public int WipMaxRunning => (int)Integer(WipMaxRunningName);

    /// <summary><c>wip.memoryPerRunMb</c>: the memory allowance one run is counted at.</summary>
    public int WipMemoryPerRunMb => (int)Integer(WipMemoryPerRunMbName);

    /// <summary><c>admission.memoryPercent</c>: memory in use, as a percentage of the limit, at which a run waits. 0 is off.</summary>
    public int AdmissionMemoryPercent => (int)Integer(AdmissionMemoryPercentName);

    /// <summary><c>admission.memoryPressurePercent</c>: memory pressure some avg10 at which a run waits. 0 is off.</summary>
    public int AdmissionMemoryPressurePercent => (int)Integer(AdmissionMemoryPressurePercentName);

    /// <summary>
    /// The running limit in force and which bound decided it: a <c>tenant_settings</c> row
    /// (<c>setting</c>), an appsettings value (<c>configuration</c>), or the built-in default's
    /// <c>cpu</c> or <c>memory</c> bound, whichever is smaller. Read on use, like every setting.
    /// </summary>
    public WipRunLimit RunLimit()
    {
        var computed = Bounds(WipMemoryPerRunMb);

        if (_rows.TryGetValue(WipMaxRunningName, out var row))
        {
            var value = int.Parse(row.Value, CultureInfo.InvariantCulture);
            return computed with
            {
                Limit = value,
                Bound = "setting",
                Reason = $"wip.maxRunning is set to {value} in the Tenant Settings; without it the "
                    + $"default would be {computed.Limit} ({computed.Reason})",
            };
        }

        if (_configured.Contains(WipMaxRunningName))
        {
            var value = int.Parse(_fallbacks[WipMaxRunningName], CultureInfo.InvariantCulture);
            return computed with
            {
                Limit = value,
                Bound = "configuration",
                Reason = $"Wip:MaxRunning is {value} in the Host's configuration; without it the "
                    + $"default would be {computed.Limit} ({computed.Reason})",
            };
        }

        return computed;
    }

    private WipRunLimit Bounds(int memoryPerRunMb)
    {
        var cpuBound = _cpus is { } cpus ? Math.Max(1, cpus - 1) : UnknownCpuBound;
        var cpuText = _cpus is { } c
            ? $"CPU bound {cpuBound} = {c} CPUs - 1"
            : $"CPU bound {cpuBound}, as the container's CPUs are not known";

        if (_memoryLimitMb is not { } limitMb)
        {
            return new WipRunLimit(cpuBound, "cpu", cpuBound, _cpus, null, null, memoryPerRunMb,
                $"{cpuText}; the container has no memory limit");
        }

        var memoryBound = (int)Math.Max(1, Math.Min(int.MaxValue, limitMb / Math.Max(1, memoryPerRunMb)));
        var memoryText = $"memory bound {memoryBound} = {limitMb} MB / {memoryPerRunMb} MB per run";

        return memoryBound < cpuBound
            ? new WipRunLimit(memoryBound, "memory", cpuBound, _cpus, memoryBound, limitMb, memoryPerRunMb,
                $"{memoryText}, below the {cpuText}: the memory bound applies")
            : new WipRunLimit(cpuBound, "cpu", cpuBound, _cpus, memoryBound, limitMb, memoryPerRunMb,
                $"{cpuText}, not above the {memoryText}: the CPU bound applies");
    }

    /// <summary>
    /// The memory one run may use, and where the figure came from: <c>runs.memoryLimitMb</c> when it
    /// is set, else (the container's limit - <see cref="HostReserveMb"/>) / the running limit in force,
    /// at least <see cref="MinRunMemoryLimitMb"/>. Null megabytes when nothing sets one (no setting
    /// and no container limit). Read on use, like every setting.
    /// </summary>
    public RunMemoryLimit RunMemoryLimit()
    {
        var set = Integer(RunsMemoryLimitMbName);
        if (set > 0)
        {
            return new RunMemoryLimit(set, $"{RunsMemoryLimitMbName} is set to {set} MB", Set: true);
        }

        if (_memoryLimitMb is not { } containerMb)
        {
            return new RunMemoryLimit(null,
                $"{RunsMemoryLimitMbName} is 0 and the container has no memory limit to divide");
        }

        var runs = RunLimit().Limit;
        var share = (containerMb - HostReserveMb) / Math.Max(1, runs);
        var mb = Math.Max(MinRunMemoryLimitMb, share);
        return new RunMemoryLimit(mb,
            $"{RunsMemoryLimitMbName} is 0, so ({containerMb} MB container limit - {HostReserveMb} MB for the Host) / "
            + (runs > 0 ? $"{runs} (wip.maxRunning)" : "1 (wip.maxRunning is unlimited)")
            + (mb != share ? $", raised to the {MinRunMemoryLimitMb} MB floor" : string.Empty));
    }

    /// <summary>
    /// THE HEAVY ALLOWANCE: what a run holding the <c>heavy</c> lease may use. The container's limit
    /// less <see cref="HostReserveMb"/> less what the other running runs are MEASURED to use now
    /// (<paramref name="othersMb"/>, their resident memory), and never below the run's own limit.
    /// Measured rather than a second setting: the memory a heavy run needs is the memory nobody else
    /// is using, which only a reading knows; a fixed figure is either too low to help or a promise
    /// the others may already have spent. Nothing is estimated: with no container limit there is
    /// nothing to measure against, and a heavy run keeps its own limit.
    /// </summary>
    /// <param name="othersMb">Resident memory of every other running run, summed, in MB.</param>
    /// <param name="others">How many other runs that sum covers, for the sentence.</param>
    public RunMemoryLimit HeavyRunMemoryLimit(long othersMb, int others)
    {
        var normal = RunMemoryLimit();
        if (_memoryLimitMb is not { } containerMb)
        {
            return normal with
            {
                Source = $"the container has no memory limit to measure headroom against, so a run holding the heavy lease keeps its own limit ({normal.Source})",
            };
        }

        var headroom = containerMb - HostReserveMb - othersMb;
        var said = $"the run holds the heavy lease, so {containerMb} MB container limit - {HostReserveMb} MB for the Host - "
            + $"{othersMb} MB measured in use by {others} other running run{(others == 1 ? "" : "s")}";

        return normal.Mb is { } own && headroom <= own
            ? new RunMemoryLimit(own, $"{said} leaves no more than its own limit, so it keeps {own} MB ({normal.Source})")
            : new RunMemoryLimit(headroom, said);
    }

    /// <summary>
    /// The most any run could be raised to: the container's limit less <see cref="HostReserveMb"/>,
    /// never below the run's own limit. Under rlimit this is each process's HARD limit from launch,
    /// so the Host can raise its soft limit in place when it takes the heavy lease (raising a hard
    /// limit needs a capability the Host does not hold). Equal to the run's own limit when the
    /// container has none.
    /// </summary>
    public RunMemoryLimit RunMemoryCeiling()
    {
        var normal = RunMemoryLimit();
        if (_memoryLimitMb is not { } containerMb || normal.Mb is not { } own || containerMb - HostReserveMb <= own)
        {
            return normal;
        }

        return new RunMemoryLimit(containerMb - HostReserveMb,
            $"{containerMb} MB container limit - {HostReserveMb} MB for the Host, the most a run holding the heavy lease can be given");
    }

    /// <summary><c>workflow.spendLimit</c> in tokens. 0 is none.</summary>
    public long WorkflowSpendLimit => Integer(WorkflowSpendLimitName);

    public TimeSpan ConciergeIdleTimeout => Duration(ConciergeIdleTimeoutName);

    public TimeSpan QuietWindow => Duration(QuietWindowName);

    public int ResumeMaxAutomatic => (int)Integer(ResumeMaxAutomaticName);

    /// <summary><c>causation.depthLimit</c>. 0 is no limit.</summary>
    public int CausationDepthLimit => (int)Integer(CausationDepthLimitName);

    /// <summary><c>leases.heavy.holders</c>: runs that may hold the heavy lease at once.</summary>
    public int LeasesHeavyHolders => (int)Integer(LeasesHeavyHoldersName);

    /// <summary>Whether a chain at <paramref name="depth"/> is past the causation limit.</summary>
    public bool CausationTooDeep(int depth) => CausationDepthLimit > 0 && depth > CausationDepthLimit;

    public IReadOnlyDictionary<string, int> KanbanWipLimits =>
        ParseLaneLimits(Current(KanbanWipLimitsName));

    public string ThemeDefault => Current(ThemeDefaultName);

    /// <summary><c>outcomes.requireForCompletion</c>: whether an agent's declaration needs an outcome.</summary>
    public bool OutcomesRequireForCompletion => Current(OutcomesRequireForCompletionName) == "on";

    /// <summary><c>system.packages</c>: what the entrypoint installs at the next start.</summary>
    public IReadOnlyList<string> SystemPackages =>
        JsonSerializer.Deserialize<string[]>(Current(SystemPackagesName)) ?? [];

    /// <summary><c>agents.tags</c>: the operator's tags for a built-in preset, by preset name
    /// (case-insensitive). A preset absent from it carries the build's tags.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AgentTags =>
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(Current(AgentTagsName)) is { } map
            ? map.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The limit a kanban lane shows: <c>wip.maxRunning</c> for the lane that holds
    /// running work (null when unlimited), else <c>kanban.wipLimits</c>, else none.</summary>
    public int? LaneLimit(string laneId)
    {
        if (string.Equals(laneId, KanbanLanes.Running, StringComparison.OrdinalIgnoreCase))
        {
            return WipMaxRunning > 0 ? WipMaxRunning : null;
        }

        return KanbanWipLimits.TryGetValue(laneId, out var limit) ? limit : null;
    }

    // ---- The API's view. ----

    /// <summary>The stored (canonical) value in force for <paramref name="name"/>.</summary>
    public string Current(string name) =>
        _rows.TryGetValue(name, out var row) ? row.Value : Fallback(name);

    public TenantSettingRow? Row(string name) => _rows.GetValueOrDefault(name);

    /// <summary>The value without a row: appsettings, else the built-in default. The running
    /// limit's built-in default is computed now, from the current <c>wip.memoryPerRunMb</c>.</summary>
    public string Fallback(string name) =>
        name == WipMaxRunningName && !_configured.Contains(name)
            ? Bounds(WipMemoryPerRunMb).Limit.ToString(CultureInfo.InvariantCulture)
            : _fallbacks[name];

    /// <summary>Where <see cref="Fallback"/> comes from: <c>appsettings</c> when the Host's
    /// configuration sets it, else <c>builtIn</c>. What a reset would fall back to, named.</summary>
    public string FallbackSource(string name) => _configured.Contains(name) ? "appsettings" : "builtIn";

    /// <summary>A setting's description as the dialog shows it. The running limit's says which
    /// bound applies now, and why.</summary>
    public string DescriptionOf(string name)
    {
        if (name != WipMaxRunningName) return _definitions[name].Description;

        var limit = RunLimit();
        return $"{_definitions[name].Description} Now {limit.Limit}: {limit.Reason}.";
    }

    public TenantSettingDefinition? Definition(string name) => _definitions.GetValueOrDefault(name);

    /// <summary>A canonical stored value as the JSON the API sends.</summary>
    public JsonNode? ToJson(string name, string canonical) => _definitions[name].Kind switch
    {
        TenantSettingKind.Integer => JsonValue.Create(long.Parse(canonical, CultureInfo.InvariantCulture)),
        TenantSettingKind.LaneLimits or TenantSettingKind.PackageList or TenantSettingKind.TagMap =>
            JsonNode.Parse(canonical),
        _ => JsonValue.Create(canonical),
    };

    /// <summary>
    /// Validates every entry of a partial map, then writes the ones that change - all or none - and
    /// applies them. A JSON <c>null</c> resets the setting: its row is removed, so appsettings.json,
    /// then the built-in default, applies; a reset of a setting with no row changes nothing. Throws
    /// <see cref="TenantSettingRejected"/> naming the first field that fails. Returns the names written.
    /// </summary>
    public async Task<IReadOnlyList<string>> WriteAsync(
        IReadOnlyDictionary<string, JsonElement> changes, string? actorId, string actorEmail,
        CancellationToken ct = default)
    {
        var parsed = new List<(string Name, string? Value)>();

        foreach (var (name, element) in changes)
        {
            if (!_definitions.TryGetValue(name, out var definition))
            {
                throw new TenantSettingRejected(name, $"'{name}' is not a setting that can be changed here.");
            }

            parsed.Add((name, element.ValueKind == JsonValueKind.Null ? null : Validate(definition, element)));
        }

        await _writes.WaitAsync(ct);

        try
        {
            var pending = parsed
                .Where(p => p.Value is null
                    ? _rows.ContainsKey(p.Name)
                    : !(_rows.TryGetValue(p.Name, out var row) && row.Value == p.Value))
                .Select(p => new TenantSettingChange(p.Name, Current(p.Name), p.Value))
                .ToList();

            if (pending.Count == 0) return [];

            var written = await _store.WriteAsync(pending, actorId, actorEmail, ct);

            var rows = new Dictionary<string, TenantSettingRow>(_rows, StringComparer.Ordinal);
            foreach (var change in written)
            {
                if (change.Row is { } row) rows[change.Name] = row;
                else rows.Remove(change.Name);
            }

            _rows = rows;

            foreach (var change in written) Changed?.Invoke(change.Name);

            return written.Select(change => change.Name).ToList();
        }
        finally
        {
            _writes.Release();
        }
    }

    // ---- Parsing. ----

    private long Integer(string name) => long.Parse(Current(name), CultureInfo.InvariantCulture);

    private TimeSpan Duration(string name) => TimeSpan.ParseExact(Current(name), "c", CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, int> ParseLaneLimits(string canonical) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(canonical) is { } map
            ? new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <param name="bounded">False for a value from appsettings.json or an existing row: its type
    /// is checked, but a duration outside the dialog's range is a deployment's choice and stands.</param>
    private static string Validate(TenantSettingDefinition definition, JsonElement element, bool bounded = true)
    {
        var name = definition.Name;

        switch (definition.Kind)
        {
            case TenantSettingKind.Integer:
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var number))
                {
                    throw new TenantSettingRejected(name, $"{name} must be a whole number.");
                }

                if (number < definition.Min || number > definition.Max)
                {
                    throw new TenantSettingRejected(name, definition.Max == long.MaxValue
                        ? $"{name} must be at least {definition.Min}."
                        : $"{name} must be between {definition.Min} and {definition.Max}.");
                }

                return number.ToString(CultureInfo.InvariantCulture);

            case TenantSettingKind.Duration:
                if (element.ValueKind != JsonValueKind.String
                    || !TimeSpan.TryParse(element.GetString(), CultureInfo.InvariantCulture, out var span))
                {
                    throw new TenantSettingRejected(name, $"{name} must be a duration such as \"00:30:00\".");
                }

                if (bounded && (span < definition.MinDuration || span > definition.MaxDuration))
                {
                    throw new TenantSettingRejected(name,
                        $"{name} must be between {definition.MinDuration:c} and {definition.MaxDuration:c}.");
                }

                return span.ToString("c", CultureInfo.InvariantCulture);

            case TenantSettingKind.LaneLimits:
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new TenantSettingRejected(name, $"{name} must be an object of lane id to limit.");
                }

                var limits = new SortedDictionary<string, int>(StringComparer.Ordinal);

                foreach (var lane in element.EnumerateObject())
                {
                    var laneId = lane.Name.Trim();

                    if (laneId.Length == 0)
                    {
                        throw new TenantSettingRejected(name, $"{name} has an empty lane id.");
                    }

                    if (string.Equals(laneId, KanbanLanes.Running, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new TenantSettingRejected(name,
                            $"{name}: the '{laneId}' lane's limit is wip.maxRunning; set that instead.");
                    }

                    if (KanbanLanes.Find(laneId) is null)
                    {
                        throw new TenantSettingRejected(name,
                            $"{name}: there is no '{laneId}' lane. The lanes are "
                            + string.Join(", ", KanbanLanes.All.Select(l => l.Id)) + ".");
                    }

                    if (lane.Value.ValueKind != JsonValueKind.Number
                        || !lane.Value.TryGetInt32(out var limit)
                        || limit < definition.Min || limit > definition.Max)
                    {
                        throw new TenantSettingRejected(name,
                            $"{name}: the limit for '{laneId}' must be a whole number between {definition.Min} and {definition.Max}.");
                    }

                    limits[laneId.ToLowerInvariant()] = limit;
                }

                return JsonSerializer.Serialize(limits);

            case TenantSettingKind.Choice:
                var choice = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim().ToLowerInvariant() : null;

                if (choice is null || definition.Choices?.Contains(choice) != true)
                {
                    throw new TenantSettingRejected(name,
                        $"{name} must be one of {string.Join(", ", definition.Choices ?? [])}.");
                }

                return choice;

            case TenantSettingKind.PackageList:
                if (element.ValueKind != JsonValueKind.Array)
                {
                    throw new TenantSettingRejected(name, $"{name} must be a list of package names.");
                }

                var packages = new List<string>();

                foreach (var item in element.EnumerateArray())
                {
                    var package = item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : null;

                    if (package is null || !Harness.Host.SystemPackages.IsValidName(package))
                    {
                        throw new TenantSettingRejected(name,
                            $"{name}: '{(package ?? item.GetRawText())}' is not a package name. A name is lower-case "
                            + "letters, digits and + - . only, starting with a letter or digit.");
                    }

                    if (!packages.Contains(package, StringComparer.Ordinal)) packages.Add(package);
                }

                if (packages.Count > definition.Max)
                {
                    throw new TenantSettingRejected(name, $"{name} may name at most {definition.Max} packages.");
                }

                return JsonSerializer.Serialize(packages);

            case TenantSettingKind.TagMap:
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new TenantSettingRejected(name, $"{name} must be an object of preset name to a list of tags.");
                }

                // Keys are not checked against the catalog: an entry for a preset that is not there
                // is ignored where it is read, never refused, so a preset removed later strands no
                // setting. Sorted, so an unchanged map compares equal and writes nothing.
                var map = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

                foreach (var entry in element.EnumerateObject())
                {
                    var key = entry.Name.Trim();

                    if (key.Length == 0)
                    {
                        throw new TenantSettingRejected(name, $"{name} has an empty preset name.");
                    }

                    if (map.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new TenantSettingRejected(name, $"{name} names '{key}' twice.");
                    }

                    if (entry.Value.ValueKind != JsonValueKind.Array)
                    {
                        throw new TenantSettingRejected(name, $"{name}: the tags for '{key}' must be a list of words.");
                    }

                    var tags = new List<string>();

                    foreach (var item in entry.Value.EnumerateArray())
                    {
                        var tag = item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : null;

                        if (string.IsNullOrEmpty(tag) || tag.Length > definition.Max)
                        {
                            throw new TenantSettingRejected(name,
                                $"{name}: '{(tag ?? item.GetRawText())}' for '{key}' is not a tag. A tag is "
                                + $"a word of 1 to {definition.Max} characters.");
                        }

                        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
                    }

                    map[key] = tags;
                }

                return JsonSerializer.Serialize(map);

            default:
                throw new TenantSettingRejected(name, $"{name} cannot be set.");
        }
    }

    private static bool TryCanonical(TenantSettingDefinition definition, string stored, out string canonical)
    {
        canonical = stored;

        try
        {
            using var document = definition.Kind switch
            {
                TenantSettingKind.Integer => JsonDocument.Parse(stored),
                TenantSettingKind.LaneLimits => JsonDocument.Parse(stored),
                TenantSettingKind.PackageList => JsonDocument.Parse(stored),
                TenantSettingKind.TagMap => JsonDocument.Parse(stored),
                _ => JsonDocument.Parse(JsonSerializer.Serialize(stored)),
            };

            canonical = Validate(definition, document.RootElement, bounded: false);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or TenantSettingRejected)
        {
            return false;
        }
    }

    /// <summary>The appsettings.json value for a setting, canonicalised, or null when absent or
    /// invalid (the built-in default then applies).</summary>
    private string? FromConfiguration(TenantSettingDefinition definition)
    {
        if (definition.ConfigKey is null) return null;

        if (definition.Kind == TenantSettingKind.PackageList)
        {
            // An array in appsettings.json (children 0, 1, ...) or one string of names.
            var section = _configuration.GetSection(definition.ConfigKey);
            var names = section.GetChildren().Any()
                ? section.GetChildren().Select(child => child.Value ?? "").ToArray()
                : (section.Value ?? "").Split([' ', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length == 0) return null;

            return TryCanonical(definition, JsonSerializer.Serialize(names), out var list) ? list : null;
        }

        if (definition.Kind == TenantSettingKind.LaneLimits)
        {
            var section = _configuration.GetSection(definition.ConfigKey);
            if (!section.GetChildren().Any()) return null;

            var map = section.GetChildren().ToDictionary(child => child.Key, child => (object?)(
                int.TryParse(child.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : child.Value));
            return TryCanonical(definition, JsonSerializer.Serialize(map), out var lanes) ? lanes : null;
        }

        var raw = _configuration[definition.ConfigKey];
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return TryCanonical(definition, raw.Trim(), out var canonical) ? canonical : null;
    }

    /// <summary>The container's memory limit in MB, from <c>/sys/fs/cgroup/memory.max</c> (bytes, or
    /// <c>max</c> for none). Null when unbounded or unreadable.</summary>
    public static long? CgroupMemoryMb(string path = "/sys/fs/cgroup/memory.max")
    {
        try
        {
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path).Trim();

            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0
                ? bytes / (1024 * 1024)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>CPUs the container's cgroup allows, from <c>/sys/fs/cgroup/cpu.max</c>
    /// (<c>quota period</c>, or <c>max</c> for none), rounded up. Null when unbounded or unreadable.</summary>
    public static int? CgroupCpus(string path = "/sys/fs/cgroup/cpu.max")
    {
        try
        {
            if (!File.Exists(path)) return null;

            var parts = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length < 2
                || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quota)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var period)
                || quota <= 0 || period <= 0)
            {
                return null;
            }

            return (int)Math.Ceiling((double)quota / period);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// The running limit in force and why: <c>Bound</c> is <c>setting</c> (a tenant row),
/// <c>configuration</c> (appsettings), <c>cpu</c> or <c>memory</c>. <c>CpuBound</c> and
/// <c>MemoryBound</c> are what the built-in default would allow; <c>MemoryBound</c> is null when the
/// container has no memory limit, and <c>Cpus</c> when its CPU count is not known.
/// </summary>
public sealed record WipRunLimit(
    int Limit, string Bound, int CpuBound, int? Cpus, int? MemoryBound, long? MemoryLimitMb,
    int MemoryPerRunMb, string Reason);
