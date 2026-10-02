using Microsoft.Extensions.Logging;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Runs an agent as a child process, prompt on stdin, output captured - ON A RUNTIME WORKER, through
/// the run protocol. This is control's side: it resolves everything the run needs from the catalog
/// (the preset's launch with its isolation and update-off, its live view, the run's memory figures
/// and temporary folder) into a <see cref="StartRun"/>, sends it, and turns what the worker says back
/// into the <see cref="AgentResult"/> its callers have always been given. The launch itself is the
/// worker's <see cref="RunLauncher"/>.
/// </summary>
/// <remarks>
/// <paramref name="diagnostics"/> is OPTIONAL and null means a runner that records nothing, which
/// is the shape every other seam in this codebase uses for an artifact store - see
/// `MemberRuntime`'s transcript and ledger. It is optional so the specs that construct this class
/// by hand do not each have to supply one, and because a diagnostics store is an observer: a runner
/// without one must go on running agents exactly as it did.
///
/// THE CONSTRUCTOR THAT TAKES THE WORKER'S PARTS builds a worker of its own, in this process, from
/// them, and talks to it through the protocol all the same: a runner built by hand runs exactly what
/// the Host's does.
/// </remarks>
public sealed class ProcessAgentRunner : IAgentRunner, IRunWorkerClient
{
    private readonly AgentCatalog _catalog;
    private readonly IRunWorker _worker;
    private readonly RunDirectory _directory;
    private readonly RunLauncher _launcher;
    private readonly Func<RunMemoryAllowance?> _memory;
    private readonly string? _tempRoot;

    public ProcessAgentRunner(
        AgentCatalog catalog,
        RunHeartbeat heartbeat,
        ILogger<ProcessAgentRunner>? log = null,
        IDiagnosticsLog? diagnostics = null,
        AgentLaunchUser? runAs = null,
        LiveRuns? live = null,
        IMemberReports? reports = null,
        LaunchLookup? lookup = null,
        AgentUpdateGate? updates = null,
        RunMemoryLimits? memory = null,
        MemberTempRoot? temp = null,
        RunAllowances? allowances = null,
        TimeSpan? oomPoll = null)
    {
        var launcher = new RunLauncher(heartbeat, log, runAs, reports is not null, lookup, updates, memory, allowances, oomPoll);
        _directory = new RunDirectory(reports, diagnostics, live);
        var worker = InProcessWorker.Connect(
            WorkerId.Local,
            events => new WorkerHost(WorkerId.Local, events, launcher, heartbeat, allowances, log: log),
            _directory.HandleAsync);

        _catalog = catalog;
        _worker = worker.Worker;
        _launcher = launcher;
        _memory = () => memory is null ? null : Allowance(memory.Settings());
        _tempRoot = temp?.Path;
    }

    /// <summary>The worker this runner starts its runs on.</summary>
    public IRunWorker Worker => _worker;

    /// <summary>
    /// The launch error for a program that never appeared: what was looked for, for how long, and
    /// that re-sending tries again. It asks nobody to repair anything.
    /// </summary>
    public static string LaunchMissingText(string fileName, LaunchLookup lookup) => RunLauncher.LaunchMissingText(fileName, lookup);

    /// <summary>What a launch held behind an update says, once, on its card: held, never failed.</summary>
    public static string HeldText(string command) => RunLauncher.HeldText(command);

    /// <summary>
    /// Linux's bound on one argument (MAX_ARG_STRLEN), terminating NUL included. A prompt at or
    /// over it is handed over as a file.
    /// </summary>
    public const int MaxArgumentBytes = RunLauncher.MaxArgumentBytes;

    /// <summary>How long the launch check waits for a free invocation before it is killed and read as failed.</summary>
    public static readonly TimeSpan LaunchCheckTimeout = RunLauncher.LaunchCheckTimeout;

    /// <summary>
    /// The run, on the worker. Resolved from the CATALOG, by name, on every invocation - never from
    /// a per-container binding captured when the member was created: the answer is derivable from
    /// `invocation.Agent`, which is read off the container's own definition on every wake.
    /// </summary>
    public Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default) =>
        _directory.RunAsync(_worker, Start(invocation), ct);

    /// <summary>
    /// THE LAUNCH CHECK FOR ONE PRESET: its declared free invocation (<see cref="AgentDefinition.LaunchCheck"/>)
    /// started the way a member run starts the CLI, by the worker's own launch
    /// (<see cref="RunLauncher.CheckLaunchAsync"/>). No free invocation reads not checked, never ok.
    /// </summary>
    public async Task<AgentLaunchReport> CheckLaunchAsync(string agent, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (_catalog.Definition(agent) is not { } definition)
        {
            return AgentLaunchReport.Unchecked($"'{agent}' is not an Agent this tenant has.");
        }

        if (_catalog.For(agent) is not { } command)
        {
            return AgentLaunchReport.Unchecked("An interactive preset: no member run launches it.");
        }

        if (definition.LaunchCheck is not { Count: > 0 } check)
        {
            return AgentLaunchReport.Unchecked(
                "This preset declares no free invocation (launchCheck), so nothing was started: a launch that "
                + "might send a prompt or spend is never run to check it.");
        }

        var environment = AgentToolPreflight.LaunchShape(definition).Environment;
        return await _launcher.CheckLaunchAsync(
            Launch(command, definition.TimeoutSeconds, environment),
            check,
            definition.Updates?.Arguments ?? [],
            environment,
            _memory(),
            ct,
            timeout);
    }

    /// <summary>Everything the worker needs for this invocation, resolved now.</summary>
    private StartRun Start(AgentInvocation invocation)
    {
        var definition = _catalog.Definition(invocation.Agent);

        return new StartRun(
            RunId.For(invocation.Container),
            invocation.Agent,
            invocation.SystemPrompt,
            invocation.Prompt,
            invocation.Context,
            invocation.WorkingDirectory,
            invocation.Environment,
            invocation.UnreachableRoot,
            _catalog.For(invocation.Agent) is { } command
                ? Launch(command, definition?.TimeoutSeconds, invocation.Environment)
                : null,
            _memory(),
            _tempRoot,
            definition?.LiveView is { } view ? LiveView.ToRun(view) : null);
    }

    /// <summary>
    /// The preset's launch as the protocol carries it, with what the child must not inherit: the
    /// names that must be absent, and every provider key that is not this command's own and was not
    /// handed in with <paramref name="environment"/>.
    /// </summary>
    private static RunLaunch Launch(AgentCommand command, int? timeoutSeconds, IReadOnlyDictionary<string, string> environment) =>
        new(
            command.FileName,
            command.Arguments,
            command.SystemPromptArguments,
            command.InstructionsFile,
            command.UsageFormat,
            command.LanguageModel,
            command.IsolationEnvironment,
            command.UpdateEnvironment,
            timeoutSeconds,
            [.. AgentEnvironment.MustBeAbsent, .. AgentEnvironment.ProviderKeysToRemove(command.FileName, environment)]);

    /// <summary>The settings' memory figures for a run's start.</summary>
    public static RunMemoryAllowance Allowance((RunMemoryLimit Limit, RunMemoryLimit Ceiling) settings) =>
        new(Figure(settings.Limit), Figure(settings.Ceiling));

    private static MemoryFigure Figure(RunMemoryLimit limit) => new(limit.Mb, limit.Source, limit.Set);
}

/// <param name="SystemPromptArguments">
/// How this agent takes a system prompt, with <c>{systemPromptFile}</c> substituted for a temp file the
/// runner writes. Null means it has no such mechanism and the system prompt is not passed at all -
/// which is honest, and far better than the alternative of prepending it to the user prompt, where
/// it comes back out in everything the agent echoes.
/// </param>
public sealed record AgentCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string>? SystemPromptArguments = null,
    string? InstructionsFile = null,
    string? UsageFormat = null,
    bool LanguageModel = true,
    // A member's isolation variables (AgentIsolation.Env), set LAST at the spawn site so no preset
    // or team env can switch isolation back on. Null for the Concierge and an undeclared preset.
    IReadOnlyDictionary<string, string>? IsolationEnvironment = null,
    // What turns the CLI's own updater off (AgentUpdates.Env), set LAST at every spawn site, the
    // Concierge's included, so no preset or team env can turn self-update back on.
    IReadOnlyDictionary<string, string>? UpdateEnvironment = null);

/// <summary>
/// Which command each container's agent is. Configuration, not code - the reason a container is data
/// and adding one needs no plugin loader.
/// </summary>
public sealed class AgentCatalog(
    IReadOnlyList<AgentDefinition> definitions,
    Func<IReadOnlyDictionary<string, IReadOnlyList<string>>>? tagOverrides = null)
{
    // The definitions and NOTHING ELSE - in particular no per-container map of resolved commands.
    // Every reader can resolve the answer from a member's own agent name, and a second store of
    // that fact drifts apart from the first without anything failing. Do not add one: a cache keyed
    // on a container is a cache that has to be invalidated by every path that changes what a
    // container runs, and there is no such thing as remembering to do that forever.
    //
    // Built-in presets and the person's custom ones, in one list. Prompts are not here: the
    // prompt is chosen by role (BuiltInPrompts), never by the preset or a person.
    private IReadOnlyList<AgentDefinition> _definitions = definitions;

    // THE OPERATOR'S TAGS FOR A BUILT-IN, the tenant setting `agents.tags`. A delegate,
    // asked on every read and never captured, so a change reaches the next hire with no restart.
    // Only a built-in is overridden: a custom preset carries its own tags in agents.json.
    private readonly Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> _tagOverrides =
        tagOverrides ?? (() => new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>Every preset, built-in and custom, a built-in carrying the operator's tags when
    /// it has them: what the Agents screen renders.</summary>
    public IReadOnlyList<AgentDefinition> Definitions
    {
        get
        {
            var overrides = _tagOverrides();
            return overrides.Count == 0 ? _definitions : [.. _definitions.Select(d => WithOverride(d, overrides))];
        }
    }

    /// <summary>Whether this preset's tags are the operator's rather than the build's.</summary>
    public bool TagsFromOperator(string agent) =>
        AgentCatalogFile.IsBuiltIn(agent) && OverrideFor(agent, _tagOverrides()) is not null;

    /// <summary>The tags a built-in preset carries in the build, or null for a custom one.</summary>
    public IReadOnlyList<string>? BuildTags(string agent) =>
        AgentCatalogFile.BuiltIns()
            .FirstOrDefault(d => string.Equals(d.Name, agent, StringComparison.OrdinalIgnoreCase))
            ?.Tags ?? (AgentCatalogFile.IsBuiltIn(agent) ? [] : null);

    /// <summary>Entries of <c>agents.tags</c> that name no built-in preset. Ignored, never an
    /// error: a preset removed from the build strands its entry rather than failing a read.</summary>
    public IReadOnlyList<string> IgnoredTagOverrides =>
        [.. _tagOverrides().Keys.Where(name => !AgentCatalogFile.IsBuiltIn(name)).Order(StringComparer.Ordinal)];

    /// <summary>The custom presets alone: what agents.json holds and a PUT replaces.</summary>
    public IReadOnlyList<AgentDefinition> Custom =>
        [.. _definitions.Where(d => !AgentCatalogFile.IsBuiltIn(d.Name))];

    public void Replace(IReadOnlyList<AgentDefinition> definitions)
    {
        _definitions = definitions;
        Changed?.Invoke();
    }

    /// <summary>Raised after <see cref="Replace"/>: the pre-flight lists the CLIs again.</summary>
    public event Action? Changed;

    /// <summary>
    /// The headless command for a preset, or null.
    ///
    /// TWO reasons for null and both are refusals: this tenant has no such preset, or it has one
    /// that is INTERACTIVE. The mode check is what stops `shell` being offered as a member's Agent
    /// and `echo` as a Concierge - failures the previous shape could only refuse at launch,
    /// after someone had already chosen.
    ///
    /// This was a `switch` whose default arm returned `claude`, so any name it did not recognise
    /// launched Claude Code: a typo was an Agent that appeared to work, running a program nobody
    /// asked for. Null is what lets every caller refuse by name instead.
    /// </summary>
    public AgentCommand? For(string agent) => Launch(agent, AgentMode.Headless);

    /// <summary>
    /// The interactive command, or null on the same two conditions. Not the headless command with a
    /// flag removed: `claude -p` prints one answer and exits, which is right for an AC woken by a
    /// message and useless for a person at a terminal.
    /// </summary>
    public AgentCommand? Interactive(string agent) => Launch(agent, AgentMode.Interactive);

    // Guards `Launch` being null in ADDITION to `LoadCustom` refusing to hand out a definition
    // shaped that way - defence in depth for one `is not null`, matching the write side's own
    // guard in AgentEndpoints.RefusalForLaunch. `Replace` and the two-arg AgentCatalog
    // constructor both take a raw IReadOnlyList<AgentDefinition> with no validation of their
    // own, so a caller that bypasses LoadCustom - a test, or code added later - can still hand
    // this a definition with a null Launch, and an NRE here is the failure this guards.
    private AgentCommand? Launch(string agent, AgentMode mode)
    {
        var definition = Definition(agent);

        if (definition is null || definition.Mode != mode || definition.Launch is not { } launch) return null;

        // ONLY THE HEADLESS COMMAND IS ISOLATED. A member gets the platform's tools and its CLI's
        // own; the Concierge is the person's session and launches exactly as it always has.
        // THE UPDATE-OFF ON EVERY LAUNCH, member and Concierge alike: the install is shared.
        return AgentUpdates.Apply(
            mode == AgentMode.Headless
                ? AgentIsolationPolicy.Apply(launch.ToCommand(), definition.Isolation)
                : launch.ToCommand(),
            definition.Updates);
    }

    /// <summary>
    /// The tools a run of <paramref name="agent"/> may be offered, or null for a preset this tenant
    /// does not have: `harness` plus the preset's declared servers and local tools, or
    /// <see cref="IsolationState.NotVerified"/> for a headless preset with no declaration. What the
    /// per-run foreign-tool check and the pre-flight report read.
    /// </summary>
    public ToolAllowance? Allowance(string agent) => AgentIsolationPolicy.For(Definition(agent));

    /// <summary>Case-insensitive, matching how a stored `team_members.agent` is read back.</summary>
    public AgentDefinition? Definition(string agent) =>
        _definitions.FirstOrDefault(
            d => string.Equals(d.Name, agent, StringComparison.OrdinalIgnoreCase)) is { } definition
            ? WithOverride(definition, _tagOverrides())
            : null;

    private static AgentDefinition WithOverride(
        AgentDefinition definition, IReadOnlyDictionary<string, IReadOnlyList<string>> overrides) =>
        AgentCatalogFile.IsBuiltIn(definition.Name) && OverrideFor(definition.Name, overrides) is { } tags
            ? definition with { Tags = tags }
            : definition;

    private static IReadOnlyList<string>? OverrideFor(
        string agent, IReadOnlyDictionary<string, IReadOnlyList<string>> overrides) =>
        overrides.FirstOrDefault(e => string.Equals(e.Key, agent, StringComparison.OrdinalIgnoreCase)).Value;

}
