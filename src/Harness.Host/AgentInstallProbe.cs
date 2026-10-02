using System.ComponentModel;
using System.Text.Json.Serialization;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// The FOURTH state, and the whole reason it is not folded into one of the three that already
/// exist.
///
/// `NoPromptChosen` means nobody has chosen; `MissingPrompt` and `MissingAgent` mean the CATALOG
/// has no such entry. All three are fixed inside the product, on a screen. This one means the
/// catalog entry is perfectly correct and the MACHINE lacks the CLI - fixed in a terminal, by a
/// person, and by nothing anyone can do in Harness. Telling somebody "agent missing" when their
/// catalog is right sends them to the Agents screen to repair something that is not broken, which
/// is the identical mistake `NoPromptChosen` was split from `MissingPrompt` to avoid.
///
/// A STRING AND NEVER A `ContainerState`, the rule <see cref="Contracts.ContainerSnapshot.MissingAgent"/>,
/// `Blocked` and `UnreachableRoot` all follow: the enum crosses TWO serialisers as a name -
/// `ConfigureHttpJsonOptions` configures one and SignalR's `AddJsonProtocol` the other - and the
/// SPA compares the string it receives. Adding a value means auditing both wires, and getting it
/// wrong is silent in the worst direction: HTTP answers a name while every push carries a number.
/// </summary>
public static class AgentInstallStates
{
    /// <summary>
    /// What a preset whose command does not resolve on this machine's PATH reports.
    ///
    /// The value is the WIRE FORM. It is compared by the SPA (`web/src/lib/agentInstall.ts` holds
    /// the only copy on that side) and asserted against by name in the suite, so it is a contract
    /// rather than a label: renaming it is a wire change.
    /// </summary>
    public const string NotInstalled = "AgentNotInstalled";
}

/// <summary>
/// What the probe answers for one preset - the payload the fourth state rides on.
///
/// A SIBLING of the catalog rather than a field inside <see cref="AgentDefinition"/>, and that
/// separation is load-bearing: `PUT /api/agents` REPLACES the catalog wholesale from a body the
/// Agents dialog composes out of what `GET` handed it, so anything that reads like part of a
/// definition is something a save will try to write back. This is a MEASUREMENT of the machine,
/// not configuration; it has no business in `agents.json`.
/// </summary>
public sealed record AgentInstallation(
    [property: Description("The preset this describes, as `GET /api/agents` names it.")]
    string Agent,

    [property: Description(
        "The command this preset launches - `AgentLaunch.FileName`. Several presets share one "
        + "command (`claude` and `claude-headless` both run `claude`), and the probe answers about "
        + "the COMMAND, so those rows always agree.")]
    string Command,

    [property: Description(
        "NULL when the command resolves, and `AgentNotInstalled` when it does not.\n\n"
        + "A nullable STRING and deliberately not a new `ContainerState`: that enum crosses two "
        + "serialisers as a name and the SPA compares it. It is also NOT `MissingAgent`, which "
        + "keeps meaning 'the catalog has no such entry' - a different fact with a different "
        + "remedy. This one is fixed in a terminal, by a person, and by nothing on any screen "
        + "here.")]
    string? State,

    [property: Description(
        "Where the command resolved to, or null when it did not. Included because this is already "
        + "a person's screen and it is the one thing that answers 'which of the two "
        + "`claude`s on this box am I actually running'. It is not returned on the redacted shape "
        + "everyone else reads.")]
    string? ResolvedPath,

    [property: Description(
        "Whether any team REFERENCES this preset - a `team_members.agent` row, an entry in a "
        + "team's `member_agents` allowlist, or the tenant's `interactive_agent`. The Agents screen "
        + "lists the state for every preset regardless; the ribbon badge counts only the "
        + "referenced ones, because a badge that is always lit stops being read.")]
    bool Referenced,

    [property: Description(
        "What a person is told, in words that claim exactly what was checked and no more: that a "
        + "command of this name does or does not resolve on this machine's PATH. It does NOT "
        + "establish that the binary runs, that it is the right version, or that it is "
        + "authenticated.")]
    string Message,

    [property: Description(
        "Where to go to install this Agent, from the preset's own `install` - or null when the "
        + "preset carries none, in which case the client renders the same sentence with no link "
        + "and NEVER a constructed one.")]
    AgentInstall? Install,

    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: Description(
        "In control only: each placeable worker's answer for this command - `worker`, `installed` and "
        + "`at` - which is where `state` and `message` come from. Empty: no such worker has answered, "
        + "so the command is not measured. Absent: this Host answered from its own PATH.")]
    IReadOnlyList<InstallMeasurement>? MeasuredOn = null);

/// <summary>
/// Whether the CLI a preset names is on this machine at all.
///
/// THIS IS NOT A SECOND RESOLVER. <see cref="PathSearch"/> is what `ProcessAgentRunner` resolves
/// through, so a probe answering by any other rule would eventually disagree with the thing it
/// claims to predict. PATH is read on every miss rather than captured, so a person who fixed their
/// PATH and pressed refresh is answered from the PATH as it is now.
///
/// IT RUNS NOTHING. It resolves a name to a path and stops. Executing a candidate to see whether it
/// works would run an unverified binary off the PATH on every load of an administration screen,
/// which is a far larger thing than the question deserves - and it still would not answer whether
/// the CLI is authenticated, so it would buy a risk and no certainty.
/// </summary>
public sealed class AgentInstallProbe
{
    /// <summary>
    /// How long one answer is reused. SHORT ON PURPOSE, and both directions matter.
    ///
    /// Cached for the process lifetime, this would go on reporting a problem the person has just
    /// fixed - the recovery for this state is "install it, then look again", so an answer that
    /// cannot change until a restart is worse than not checking at all: it teaches people the check
    /// is wrong, and a check nobody believes is a check nobody reads.
    ///
    /// Not cached at all, one load of the Agents screen would walk PATH once per preset per render.
    /// A few seconds coalesces a screen load and is gone long before anybody could finish an
    /// install and press refresh.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(5);

    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _window;
    private readonly WorkerInstalls? _workers;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Answer> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Answer(string? ResolvedPath, DateTimeOffset TakenAt);

    /// <param name="now">
    /// The clock, injected for the same reason: a spec states the passage of time rather than
    /// sleeping through it. `ReapIdleAsync` takes `now` for this reason and so does this.
    /// </param>
    /// <param name="window">How long an answer is reused; <see cref="DefaultWindow"/> when null.</param>
    /// <param name="workers">
    /// In <c>control</c>, what the workers measured: every answer is theirs and this machine's PATH is
    /// never looked at, because control has no agent CLI on it. Null in <c>all</c>, where the runs are
    /// this machine's and so is the PATH.
    /// </param>
    public AgentInstallProbe(
        Func<DateTimeOffset>? now = null,
        TimeSpan? window = null,
        WorkerInstalls? workers = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _window = window ?? DefaultWindow;
        _workers = workers;
    }

    /// <summary>
    /// Where a command of this name resolves to, or null.
    ///
    /// The PATH is read from the environment on every miss rather than captured, for the reason the
    /// class comment gives. A blank command answers null, and a preset with no executable is
    /// refused at the write anyway.
    /// </summary>
    public string? ResolvedPathOf(string? command)
    {
        var name = (command ?? string.Empty).Trim();

        if (name.Length == 0) return null;

        var now = _now();

        lock (_gate)
        {
            if (_cache.TryGetValue(name, out var cached) && now - cached.TakenAt < _window)
            {
                return cached.ResolvedPath;
            }
        }

        var resolved = PathSearch.Find(name);

        lock (_gate)
        {
            _cache[name] = new Answer(resolved, now);
        }

        return resolved;
    }

    /// <summary>Whether a command of this name resolves on this machine's PATH - and nothing
    /// more than that. See the class comment: resolution is not execution. In <c>control</c>: whether
    /// every worker that counts measured it installed, and at least one did.</summary>
    public bool Resolves(string? command) =>
        _workers is null ? ResolvedPathOf(command) is not null : MeasuredOn(command) is true;

    /// <summary>
    /// One preset's answer, told whether any team references it.
    ///
    /// `referenced` is passed in rather than worked out here: which presets a team is on is a
    /// question about the team store, and <see cref="AgentReferences"/> is the one place that
    /// answers it. Two places deciding what "referenced" means is two answers waiting to disagree,
    /// and the badge is the thing that would quietly go wrong.
    /// </summary>
    public AgentInstallation Probe(AgentDefinition definition, bool referenced = false)
    {
        // `Launch` is a non-nullable constructor parameter that System.Text.Json does not enforce
        // at runtime, so a hand-edited agents.json reaches here with none - the same guard
        // `AgentCatalog.Launch` carries for the same reason. An empty command resolves to nothing,
        // which is the honest answer for a preset that could never launch either way.
        var command = definition.Launch?.FileName ?? string.Empty;
        if (_workers is not null) return OnWorkers(definition, command, referenced, _workers);

        var resolved = ResolvedPathOf(command);

        return new AgentInstallation(
            definition.Name,
            command,
            resolved is null ? AgentInstallStates.NotInstalled : null,
            resolved,
            referenced,
            MessageFor(command, resolved is not null),
            LinkableInstall(definition.Install));
    }

    /// <summary>
    /// The answer in <c>control</c>, from what the workers measured and nothing else.
    ///
    /// Installed on every worker that answered: no state. Missing on any: <c>AgentNotInstalled</c>,
    /// because a worker MEASURED it missing and a run placed there would fail - and when another
    /// worker has it, the message names both sides and picks neither. No answer from a worker that
    /// counts: no state and no warning, and the message says it has not been measured and why.
    /// </summary>
    private AgentInstallation OnWorkers(AgentDefinition definition, string command, bool referenced, WorkerInstalls workers)
    {
        var measured = command.Trim().Length == 0 ? [] : workers.For(command.Trim());
        var installed = measured.Where(m => m.Installed).Select(m => m.Worker).ToList();
        var missing = measured.Where(m => !m.Installed).Select(m => m.Worker).ToList();

        var message = measured.Count == 0 ? $"{command} has not been measured: {workers.NotMeasuredBecause()}."
            : missing.Count == 0 ? $"{command} is installed on {ListOf(installed)}."
            : installed.Count == 0 ? $"{command} is not installed on {ListOf(missing)}."
            : $"{command} is installed on {ListOf(installed)} but not on {ListOf(missing)}: the workers disagree.";

        return new AgentInstallation(
            definition.Name,
            command,
            missing.Count > 0 ? AgentInstallStates.NotInstalled : null,
            null,
            referenced,
            message,
            LinkableInstall(definition.Install),
            measured);
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string ListOf(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    /// <summary>
    /// Whether every worker that counts measured <paramref name="command"/> installed (true), any
    /// measured it missing (false), or none has answered (null). Only in <c>control</c>.
    /// </summary>
    private bool? MeasuredOn(string? command)
    {
        var name = (command ?? string.Empty).Trim();
        var measured = name.Length == 0 ? [] : _workers!.For(name);
        return measured.Count == 0 ? null : measured.All(m => m.Installed);
    }

    /// <summary>
    /// Whether the preset's CLI is installed where its runs go: on this machine's PATH in <c>all</c>,
    /// and in <c>control</c> as the workers measured it - null when none that counts has answered.
    /// </summary>
    public bool? Measured(AgentDefinition definition) =>
        _workers is null ? ResolvedPathOf(definition.Launch?.FileName) is not null : MeasuredOn(definition.Launch?.FileName);

    /// <summary>
    /// Whether the preset's CLI is KNOWN to be installed where its runs go. Not measured is not
    /// installed: a default chosen on that would be a guess.
    /// </summary>
    public bool Installed(AgentDefinition definition) => Measured(definition) is true;

    /// <summary>
    /// Every preset's answer, in catalog order, including the hidden ones.
    ///
    /// HIDDEN PRESETS ARE INCLUDED, because hiding is a RENDER rule and this is not a render: the
    /// client filters with `visibleAgents` exactly as it does the catalog beside it, and a list
    /// that arrived pre-filtered could not be joined to the one that did not.
    /// </summary>
    public IReadOnlyList<AgentInstallation> ProbeAll(
        IEnumerable<AgentDefinition> definitions, IReadOnlySet<string> referenced) =>
        [.. definitions.Select(d => Probe(d, referenced.Contains(d.Name)))];

    /// <summary>
    /// What a person is told, and it says EXACTLY what was checked.
    ///
    /// Not "installed" and not "working": the probe asked whether a command of that name resolves
    /// on this machine's PATH, and a sentence claiming more would be a sentence the check cannot
    /// support. A command that resolves may still be broken, the wrong version or unauthenticated,
    /// and the wording must leave room for all three.
    ///
    /// NO BACKTICKS. The spec writes the command in code style because it is a markdown document;
    /// this string is rendered into a list row, where a literal backtick is a stray character. The
    /// client sets the command in mono instead.
    /// </summary>
    public static string MessageFor(string command, bool resolves) =>
        resolves
            ? $"{command} resolves on this machine's PATH."
            : $"{command} was not found on this machine's PATH.";

    /// <summary>
    /// The install guidance, or null when there is nothing to link to.
    ///
    /// A PRESENT-BUT-BLANK URL IS AN ABSENT ONE. `Url` is a non-nullable constructor parameter that
    /// System.Text.Json does not enforce, so `"install": {}` in a hand-edited catalog deserialises
    /// to a null URL - and a client rendering an anchor around it produces a link to nowhere, which
    /// is the "guessed or constructed URL" failure arriving by the back door. Nothing here ever
    /// composes a URL from a preset's name.
    /// </summary>
    private static AgentInstall? LinkableInstall(AgentInstall? install) =>
        install is { Url: var url } && !string.IsNullOrWhiteSpace(url) ? install : null;
}
