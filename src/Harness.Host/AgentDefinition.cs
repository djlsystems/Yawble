using System.ComponentModel;

namespace Harness.Host;

/// <summary>How one Agent is launched. The same shape for headless and interactive, because the
/// difference between them is the arguments rather than the mechanism.</summary>
/// <remarks>
/// <para>
/// <c>LanguageModel</c> DEFAULTS TO TRUE, and that default is load-bearing. A custom preset in
/// `agents.json` written before the key existed has no such key - a bare bool
/// defaulting to false would silently mark every existing preset a program, turning off the
/// credential probe, suppressing usage and permitting firehose subscriptions. The safe value falls
/// out of the type rather than being remembered, which is `SqliteUserStore`'s hashIterations rule:
/// absent means strong.
/// </para>
/// <para>
/// It is NOT called <c>AICost</c>. Cost in this product is MEASURED, never inferred - there is no
/// estimator, and `UsageSource.ExcludedEstimate` exists only so the projection can exclude such rows. A
/// field named for spend, one record away from all that, would read as authoritative and the first
/// dashboard would sum against it. This names what the preset IS; cost stays derived.
/// </para>
/// <para>
/// It is NOT a third <see cref="AgentMode"/>. That enum answers which of the two WORLDS a preset
/// serves - headless for a member woken by a message, interactive for a Concierge - and a procedural
/// container is still headless. One enum answering two questions, across two serialisers, with the
/// SPA comparing it as a string, is the trap.
/// </para>
/// </remarks>
public sealed record AgentLaunch(
    [property: Description("The executable to run - `claude`, `bash`, and so on.")]
    string FileName,
    [property: Description(
        "Arguments passed on every launch, before any system-prompt argument. This is where THE "
        + "WORK reaches the agent - its history and the message that woke it - which is a different "
        + "thing from the system prompt: `{userPrompt}` puts that text on the command line, "
        + "`{userPromptFile}` writes it to a temp file and substitutes the path, and a preset with "
        + "neither sends it on stdin.")]
    IReadOnlyList<string> Arguments,
    [property: Description(
        "How this Agent is handed THE PROMPT CHOSEN FOR THE MEMBER - composed, with its tokens "
        + "already resolved - with `{systemPromptFile}` substituted for a temp file the "
        + "runner writes. Omit it when the Agent has no such mechanism - the system prompt is then "
        + "not passed at all, which is honest, rather than prepended to the user prompt where it "
        + "comes back out in everything the Agent echoes.")]
    IReadOnlyList<string>? SystemPromptArguments = null,
    [property: Description(
        "The file, relative to the container's own workspace, that this Agent reads its instructions "
        + "from - `AGENTS.md` for codex, copilot and grok. Set it when the Agent has no "
        + "system-prompt argument, which is the case for every CLI here except Claude Code: the "
        + "runner writes the composed prompt there before launch, so a member still knows its name "
        + "and its team. Omit it when SystemPromptArguments carries the prompt, or the Agent is "
        + "handed the same text twice.")]
    string? InstructionsFile = null,
    [property: Description(
        "How this Agent reports usage, when it does. Null means this launch has no measured usage "
        + "format and the runner leaves Usage unknown.")]
    string? UsageFormat = null,
    [property: Description(
        "Whether this preset launches a LANGUAGE MODEL. False for a preset that runs an ordinary "
        + "program - a build script, a CI tool - which an Agent Container can host and wake exactly "
        + "like any other. Defaults to true, so a catalog that omits it launches a model.")]
    bool LanguageModel = true)
{
    public AgentCommand ToCommand() =>
        new(FileName, Arguments, SystemPromptArguments, InstructionsFile, UsageFormat, LanguageModel);
}

/// <summary>
/// Which of the two worlds a preset belongs to.
///
/// A Team Member is ALWAYS headless and the Concierge is ALWAYS interactive, so a preset
/// serves one or the other and never both: no member is interactive and no Concierge is woken by
/// a message.
/// </summary>
public enum AgentMode
{
    Headless,
    Interactive,
}

/// <summary>
/// One Agent preset: a configured way of running a program, not a program.
///
/// Several presets may name the same command line and differ only in what they say - which is what
/// makes `Manager` possible, and why an Agent is not simply "a coding-agent program".
/// </summary>
public sealed record AgentDefinition(
    [property: Description(
        "This preset's name, as a member's `agent` and a team's `Concierge` name it. Matched "
        + "case-insensitively and must pass the same allowlist a container's own name does - "
        + "letters, digits, `-` and `_`, starting alphanumeric, at most 32 characters.")]
    string Name,
    [property: Description(
        "Headless for a Team Member woken by a message; Interactive mode for a team's Concierge. "
        + "A preset serves one; the picker for each offers only its own mode.")]
    AgentMode Mode,
    [property: Description("How this preset launches. Required - a preset that cannot launch is refused.")]
    AgentLaunch Launch,
    [property: Description(
        "Variables merged into the launch environment BEFORE the platform's own. A key beginning "
        + "HARNESS_ is refused: it would let a preset hand a container another principal's "
        + "credential.")]
    IReadOnlyDictionary<string, string>? Env = null,
    [property: Description(
        "How long one run of this Agent may take, in seconds, before the platform stops it and "
        + "reports the member as failed. NULL means UNBOUNDED, which is the default and today's "
        + "behaviour: a number invented by the platform would cut off real work on an instance "
        + "whose owner never asked for one. Ignored for an Interactive preset - a terminal has no "
        + "run to bound, and its own idle reaper ends unattended sessions.")]
    int? TimeoutSeconds = null,

    [property: Description(
        "Whether this preset is kept out of every screen a person chooses from. `echo` and `shell` "
        + "are test fixtures: the suites launch them, and no operator should ever be offered them.\n\n"
        + "NOT a permission and not a redaction - unlike `systemPrompt` and `env`, which are "
        + "withheld from any machine principal. `GET /api/agents` returns a hidden preset "
        + "FLAGGED rather than withholding it, and it must: `PUT /api/agents` replaces the catalog "
        + "wholesale, so the editor cannot send back an entry it never received. Clients filter at "
        + "render; a client that filters what it SUBMITS deletes every hidden preset on its next "
        + "save.\n\n"
        + "Defaults false, so an agents.json that omits it reads as visible - the catalog is a "
        + "FILE, not schema.")]
    bool Hidden = false,

    [property: Description(
        "Free-text advisory tags for this Agent, tenant-wide. Compared case-insensitively; null and "
        + "an empty list are treated the same way.")]
    IReadOnlyList<string>? Tags = null,

    [property: Description(
        "Where a person goes to install this Agent's CLI, for when the probe reports that its "
        + "command is not on this machine's PATH.\n\n"
        + "OPTIONAL, AND ITS ABSENCE IS AN ORDINARY STATE rather than a fault. A preset without one "
        + "renders the same sentence with no link, and NEVER a guessed or constructed URL - a "
        + "lookup table in the Host mapping a preset's name to a vendor's documentation would work "
        + "everywhere immediately and is precisely the change this refuses, because nothing in code "
        + "may name an Agent: a name in code is a reference nothing wrote down, so no check sees it "
        + "and no rename moves it, and a vendor moving its documentation would leave a dead link "
        + "only the Host could fix.\n\n"
        + "A built-in preset's link comes from the build, so a change reaches every instance on "
        + "its next start; a custom preset shows the no-link form until somebody adds one. A "
        + "preset with none is deliberately NOT named at startup the way a headless preset with no "
        + "usage format is: a hand-added preset without an install link is ordinary, and a warning "
        + "for it would be noise.")]
    AgentInstall? Install = null,

    [property: Description(
        "Where this Agent writes its own session transcript as it works, and how to read it, so a "
        + "person can watch a run. Null means this preset has no live view, and the live "
        + "route says so rather than streaming nothing. Read-only: the launch, its usage parser "
        + "and its completion output are the same with or without it.")]
    AgentLiveView? LiveView = null)
{
    /// <summary>Whether this preset is compiled into the build. Computed from the name, never
    /// stored: a file cannot make a custom preset built-in, or a built-in custom.</summary>
    [Description(
        "True for a preset compiled into this build. It is listed read-only: only a custom preset "
        + "can be added, edited or deleted.")]
    public bool BuiltIn => AgentCatalogFile.IsBuiltIn(Name);
}

/// <summary>
/// Where to get the CLI a preset launches: a URL, and optionally one line of extra guidance.
///
/// DATA IN THE CATALOG, never a table in code - see <see cref="AgentDefinition.Install"/> for the
/// argument. Being a catalog field is also what makes it editable and removable by a person
/// who owns the catalog, which is the same standing every other field here has.
/// </summary>
public sealed record AgentInstall(
    [property: Description(
        "The page a person goes to. Rendered as a link exactly as it is stored - nothing composes, "
        + "completes or corrects it. A blank one is treated as absent, so a hand-edited "
        + "`\"install\": {}` degrades to the no-link form rather than producing a link to nowhere.")]
    string Url,

    [property: Description(
        "One line beside the link - in practice the install command. Optional: the link alone is "
        + "the whole of what this feature promises, and a hint that goes stale is worse than none.")]
    string? Hint = null);

/// <summary>
/// A preset's live view: the transcript its CLI writes for itself while it runs, which the
/// Host reads as the agent, read-only, while a person watches. Named either by
/// <see cref="Path"/>, known before launch, or by <see cref="Find"/>, looked for after it.
/// </summary>
public sealed record AgentLiveView(
    [property: Description(
        "The transcript's path, when it is known before launch. It must start with `~/`, the agent's "
        + "home, and may not contain `..`. `{sessionId}` is this run's session id, the same one the "
        + "launch token passes; `{workspaceDashed}` is the run's working directory with every `/` "
        + "and every `.` as `-`; `{workspaceEncoded}` is the working directory percent-encoded as one "
        + "path segment (`/` as `%2F`). Null when `find` names it instead; exactly one of the two.")]
    string? Path,
    [property: Description(
        "How each line is read: `claude-jsonl`, `grok-updates`, `copilot-events` or `codex-rollout`.")]
    string Format,
    [property: Description(
        "How to find the transcript after launch, for an agent that cannot be told its "
        + "session id. Null when `path` names it.")]
    AgentLiveViewFind? Find = null);

/// <summary>
/// Where an agent that picks its own session id leaves its transcript: the newest file under
/// <see cref="Folder"/> matching <see cref="Pattern"/>, written at or after the launch, whose
/// session belongs to the run's workspace by <see cref="CwdFrom"/>'s rule.
/// </summary>
public sealed record AgentLiveViewFind(
    [property: Description("The folder searched. It must start with `~/` and may not contain `..`.")]
    string Folder,
    [property: Description(
        "The file, relative to the folder, one `/`-separated segment per level, each a name that "
        + "may use `*` and `?` (`*/*/*/rollout-*.jsonl`). May not contain `..`.")]
    string Pattern,
    [property: Description(
        "How a candidate's workspace is read: `folder-name` (the first folder under `folder`, "
        + "percent-decoded), `workspace-yaml` (the `cwd:` line of `workspace.yaml` beside the file) "
        + "or `first-line-cwd` (the `cwd` of the file's first JSON line, or of its `payload`).")]
    string CwdFrom);
