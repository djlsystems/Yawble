using System.Collections.Concurrent;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A team as the Console sees it.
///
/// <paramref name="Id"/> is the IDENTIFIER and <paramref name="Name"/> is what a person reads.
/// They are equal until someone renames the team, and only the name ever moves: the id keys the
/// documents folder on disk and half of every container's identity, so
/// changing it would mean moving both atomically - which is a different and much larger piece
/// of work, deliberately not this one. Anything addressing a team (an event type, a route, a
/// path) uses Id; anything showing one to a human uses Name.
/// </summary>
public sealed record TeamSummary(
    string Id,
    string Name,
    IReadOnlyCollection<ContainerSnapshot> Containers,

    /// <summary>
    /// Which Agent preset this team's Concierge launches.
    ///
    /// Here rather than behind its own route because it IS part of what a team is, and a second
    /// route would be a second answer to that.
    ///
    /// SAFE TO ADD, AND NOT BECAUSE A TeamSummary CROSSES HTTP ONLY - it does NOT: `Program.cs`
    /// pushes this whole record over SignalR as `teamChanged`, so every field here reaches every
    /// browser holding this team the moment anything about it changes. What makes a field safe is
    /// that the push is GROUP-ROUTED to that team and that the field carries nothing a member of
    /// it may not see. A secret, a filesystem path or another team's business is a different
    /// answer - see <see cref="Root"/>, which is withheld for exactly that reason.
    ///
    /// NULL when nobody has chosen one; the launcher then picks via ConciergeAgentDefault.
    /// </summary>
    string? Concierge = null,

    /// <summary>
    /// Scalar view of <see cref="MemberAgents"/> for callers still reading one value.
    /// Returns the first allowlist entry when present.
    /// </summary>
    string? MemberAgent = null,

    /// <summary>
    /// Which Agents this team's NEW members may run, in tie-break order.
    ///
    /// NULL rather than a copy of the platform default: "has not chosen" and "chose the default" are
    /// different states and a screen that cannot tell them apart cannot show the first honestly.
    ///
    /// Both are here because they are STORED: a stored value no screen can see makes choosing
    /// another Agent for new members read as not sticking.
    /// </summary>
    IReadOnlyList<string>? MemberAgents = null,

    /// <summary>The person's additional instructions for this team, appended after the built-in
    /// role prompt for its Manager and members; null when there are none.</summary>
    string? AdditionalInstructions = null,

    /// <summary>
    /// WHERE THIS TEAM'S FILES ARE - the resolved absolute team folder, not the stored `teams.root`
    /// column. A default-rooted team answers `&lt;dataRoot&gt;/teams/&lt;Id&gt;` exactly as a
    /// placed one answers under its own root, because "where are my files" has one answer and the
    /// NULL-means-the-instance-root convention is a storage decision rather than something a screen
    /// should have to reassemble.
    ///
    /// NULL FOR A MACHINE PRINCIPAL. An absolute path on the Host's own filesystem is not an
    /// agent's business, and it is the same shape as `GET /api/agents` withholding a
    /// preset's `env`: the field is simply absent rather than blanked, so a reader cannot tell "you
    /// may not see this" from "there is nothing here" - which is fine, because for this field there
    /// is no second state. <see cref="TeamRegistry.All"/> takes a flag and DEFAULTS IT OFF, so a
    /// route that never thought about it is the harmless one.
    ///
    /// THIS RECORD RIDES THE HUB, which is the other half of why the field is withheld rather
    /// than merely omitted from one route (see <see cref="Concierge"/> above): the push is built with `withRoot: false` (see
    /// <see cref="TeamRegistry.SummaryFor"/>) precisely so an absolute path on the Host's
    /// filesystem never reaches a browser.
    /// </summary>
    string? Root = null,

    /// <summary>The ordered Git repository URLs configured for this team.</summary>
    IReadOnlyList<string>? Repos = null,

    /// <summary>Whether this TEAM is paused - no new work is delivered and no queued batch is
    /// taken, while any run already in flight is left alone. A different thing entirely from ONE
    /// WORKFLOW being paused for its spend, which is a fact about a correlation and is
    /// carried on `TeamWorkflowTiming`, not here: a team with three open workflows keeps two
    /// running when one spends its budget.</summary>
    bool Paused = false,

    /// <summary>
    /// What this team has CHOSEN to allow one workflow to spend, in tokens, IN and OUT together -
    /// the raw three-state value, exactly as stored.
    ///
    /// <para>
    /// NULL means the team has chosen nothing; 0 means it chose UNLIMITED; a positive value is the
    /// figure a person typed, which may legitimately be ABOVE the instance `WorkflowSpendLimit`.
    /// THE DIALOG IS THE ONLY READER THAT SHOULD CARE about the distinction - it has to render an
    /// explicit 0 as 0 and an unchosen team prefilled with the instance figure. Everything that
    /// ENFORCES anything reads <see cref="EffectiveWorkflowBudget"/> below instead.
    /// </para>
    ///
    /// <para>
    /// IT BOUNDS ONE WORKFLOW AND IS NOT A TEAM-WIDE TOTAL. A team with three workflows open has
    /// three budgets of this size. The team is only where the number is configured.
    /// </para>
    ///
    /// Safe to ride the hub for the reason <see cref="Concierge"/> states: a spend bound is
    /// something every member of the team may see, and a visible control beats an invisible
    /// guarantee.
    /// </summary>
    long? BudgetTokens = null,

    /// <summary>
    /// What ACTUALLY bounds one of this team's workflows, with the three states above already
    /// resolved - <b>null means UNLIMITED, and 0 never appears here</b>.
    ///
    /// <para>
    /// THE FIGURE THE KPI BAR MUST MEASURE AGAINST. A bar which reassures wrongly is
    /// worse than no bar, so it has to measure against the number actually in force rather than
    /// the one the team happens to have typed - which is absent on every team that has chosen
    /// nothing.
    /// </para>
    ///
    /// <para>
    /// RESOLVED ON THE SERVER, IN ONE FUNCTION - <see cref="TeamRegistry.EffectiveWorkflowBudgetFor"/>.
    /// A browser that re-derived it would be a second place that knows 0 means unlimited, and
    /// two places drift.
    /// </para>
    /// </summary>
    long? EffectiveWorkflowBudget = null,

    /// <summary>
    /// Each configured repository's default branch, in list order. `branch` is null when
    /// it is not known; `setByPerson` is a person's choice from Team settings and `fromRemote` what
    /// origin's HEAD last named. Safe to ride the hub: a branch name is nothing a member may not see.
    /// </summary>
    IReadOnlyList<TeamRepoDefaultBranch>? DefaultBranches = null,

    /// <summary>
    /// Each configured repository's contributor settings, in list order: its upstream (null
    /// for an owned repository), the fork's owner, whether commits are signed off (DCO), and the
    /// CLA note. The same trust as <see cref="Repos"/>, whose URLs ride the hub the same way.
    /// </summary>
    IReadOnlyList<TeamRepoContributor>? Contributors = null);

/// <summary>One repository's default branch as a screen sees it. See <see cref="RepoDefaultBranch"/>.</summary>
public sealed record TeamRepoDefaultBranch(string Repo, string? Branch, string? FromRemote, string? SetByPerson);

/// <summary>One repository's contributor settings as a screen sees them. See <see cref="RepoContributor"/>.</summary>
public sealed record TeamRepoContributor(
    string Repo, string? UpstreamUrl, string? ForkOwner, bool DcoSignOff, string? ClaSignedNote);

/// <summary>One team whose root could not be reached when the Host started. A WARNING, never a
/// refusal - see <see cref="TeamRegistry.RestoreAsync"/>.</summary>
public sealed record UnreachableTeamRoot(string TeamId, string Root, string Reason);

/// <summary>
/// One stored team that could not be restored at all - as against one whose ROOT could not be
/// reached, which comes back and is marked. A WARNING for the same reason that one is, and it is
/// the louder of the two: the row is left exactly where it is and a person decides what to do
/// with it.
/// </summary>
public sealed record UnrestorableTeam(string TeamId, string Reason);

/// <summary>
/// What a clone carried, and what it could not.
///
/// The counts are what the SOURCE gave this team, never what it now holds: a clone that carried no
/// env still has the credentials every team is born with, and reporting those as carried would say
/// the source handed over a password it did not. A step named in <paramref name="Failures"/>
/// carried NOTHING and its count says 0 - the route writes these numbers into a tenant-log row
/// that is kept forever, and an overstated one asserts a transfer that never happened.
///
/// <paramref name="Failures"/> is how a clone reports a member it could not hire. It is a LIST
/// rather than a throw because the clone is already made and visible - see <c>CloneAsync</c>, which
/// does not roll back.
/// </summary>
public sealed record TeamCloneResult(
    TeamSummary Team, int Repos, int EnvKeys, int Members, IReadOnlyList<string> Failures);

/// <summary>What a restoration could not reach. Both lists are empty on every ordinary start.</summary>
public sealed record RestoreReport(
    IReadOnlyList<UnreachableTeamRoot> UnreachableRoots,
    IReadOnlyList<UnrestorableTeam> Unrestorable);

/// <summary>
/// A team already answers to that name.
///
/// Its own exception type rather than InvalidOperationException so the route can turn it into a 409
/// carrying <see cref="ExistingLabel"/>, and cannot accidentally catch a different failure from the
/// same call and report it as a duplicate. The existing team is named by its LABEL because that is
/// the only name the person has ever been shown - telling them "PlatformEngineering already exists"
/// when their board reads "Platform Engineering" sends them looking for a second team.
/// </summary>
public sealed class TeamNameTakenException(string existingLabel)
    : InvalidOperationException($"A team called '{existingLabel}' already exists.")
{
    public string ExistingLabel { get; } = existingLabel;
}

/// <summary>
/// A member or a console named an Agent this tenant cannot launch that way - either because no
/// Agent answers to the name, or because the one that does carries no launch of the kind the
/// reference needs. Both launches are optional by design, so "exists" is not the question anyone
/// actually has: `echo` has no interactive command and `shell` has no headless one.
///
/// Carries the alternatives because the caller is a person at a dialog: "no such Agent" alone makes
/// them go and look, and the list is three words the server already has. The list is the Agents
/// that WOULD work for that reference, not every name in the catalog - offering `echo` to someone
/// choosing a Concierge is offering the refusal again.
/// </summary>
/// <summary>
/// A team that has not said what its new members RUN, met when something tries to hire without
/// naming an Agent.
///
/// Unreachable for a newly created team - creation requires the setting. It answers for a stored
/// row that carries no member Agent: refusing is right, because a default in code would be a
/// catalog name a person may rename at any moment, and therefore a reference no rename could move
/// and no reference check could see.
/// </summary>
public sealed class TeamHasNoMemberAgentException(string team)
    : InvalidOperationException(
        $"Team '{team}' has no Agent for new members, so '{team}' cannot hire. Choose one under "
        + "Dynamic members in Team Settings, or name one with --agent.")
{
    public string Team { get; } = team;
}

/// <summary>A plugin member's configuration or secret bindings that its manifest refuses. The
/// sentence names the field or the key.</summary>
public sealed class PluginSettingsException(string message) : InvalidOperationException(message);

public sealed class NoSuchAgentException : InvalidOperationException
{
    public NoSuchAgentException(string agent, IReadOnlyList<string> available)
        : this(
            agent,
            available,
            $"'{agent}' is not an Agent this tenant has. Available: {string.Join(", ", available)}.")
    {
    }

    /// <summary>A <c>plugin:&lt;id&gt;</c> reference that cannot be hired, in the plugin's own words.
    /// The same exception, so every route that refuses an unknown Agent refuses this the same way.</summary>
    public static NoSuchAgentException ForPlugin(string reference, string sentence) =>
        new(reference, [], sentence);

    private NoSuchAgentException(string agent, IReadOnlyList<string> available, string message)
        : base(message)
    {
        Agent = agent;
        Available = available;
    }

    public string Agent { get; }

    public IReadOnlyList<string> Available { get; }

    /// <summary>
    /// An Agent that exists but has no interactive command, named as such rather than as "no such
    /// Agent" - a person who can see `echo` in the catalog and is told it does not exist has been
    /// sent to look for a different problem.
    /// </summary>
    public static NoSuchAgentException WithNoInteractiveCommand(
        string agent, IReadOnlyList<string> available) =>
        new(
            agent,
            available,
            $"'{agent}' has no interactive command, so it cannot be a team's Concierge. "
            + $"Available: {string.Join(", ", available)}.");
}


/// <summary>
/// A team's own folder could not be reached while serving a REQUEST - hiring a member, or opening
/// a Concierge - as opposed to at startup, which is <see cref="RestoreReport"/>'s job.
///
/// Neither of those two is dangerous the way an unmarked member is: nothing runs in the wrong
/// directory, the call simply fails. Without this it would be LOUD AND UNNAMED - a raw
/// <c>IOException</c> out of <c>Directory.CreateDirectory</c>, arriving as a bare 500 or as a
/// devtools entry, for exactly the condition a member of the same team explains in one sentence on
/// its card. Two answers to one question, and the useless one the one a person meets first.
///
/// The wording tracks <c>ProcessAgentRunner</c>'s refusal deliberately: same folder, same recovery,
/// because they are the same fault met through different doors. There is no setter for a team's
/// root, so the recovery is the folder itself and a restart - restoration is what reads
/// it - and a message shaped like the missing-Agent one would send the reader to a settings screen
/// with nothing wrong on it.
/// </summary>
public sealed class TeamRootUnreachableException(string folder, string consequence, Exception inner)
    : InvalidOperationException(
        $"This team's folder '{folder}' could not be reached, so {consequence}. Reconnect the drive "
        + "or share and restart the Host.", inner)
{
    /// <summary>The team's own folder, never the leaf that happened to fail underneath it: that is
    /// what a person reconnects, and it is what the card and the runner both name.</summary>
    public string Folder { get; } = folder;
}

/// <summary>
/// A team, or one of its members, could not be brought back by <see cref="TeamRegistry.RestoreAsync"/>.
///
/// Its own type for the same reason <c>MigrationFailedException</c> is: restoration turns what would
/// otherwise be a per-request failure (an unwritable <c>Directory.CreateDirectory</c>, an
/// <c>AgentEnvironment.ForContainerAsync</c> that cannot mint a credential) into a STARTUP failure,
/// so <c>Program.cs</c> has to be able to print one sentence naming what was being restored rather
/// than let the raw exception scroll thirty lines of trace and read as "the host crashed".
/// </summary>
public sealed class TeamRestorationFailedException(string team, Exception inner)
    : InvalidOperationException(
        $"Could not restore team '{team}': {inner.Message} The host has not started serving; fix "
        + "the cause and start again.", inner)
{
    public string Team { get; } = team;
}

/// <summary>
/// A container whose Agent is a LANGUAGE MODEL tried to hold a subscription that is published once
/// per status line rather than once per run - see <see cref="EventCatalog.HighVolumeTypes"/> and
/// <see cref="TeamRegistry.FirehoseRefusal"/> for the reasoning and the exact wording.
///
/// Its own type rather than a bare <see cref="ArgumentException"/>, matching every other refusal
/// <see cref="TeamRegistry.AddContainerAsync"/> already makes (<see cref="NoSuchAgentException"/>,
/// <see cref="NoSuchPromptException"/>, and so on): a route maps a NAMED type to 400, so a second
/// site throwing a plain exception of a type nothing catches would surface as an unmapped 500
/// rather than the sentence this exists to deliver. A program may hold the same subscription -
/// this is refused only for a language model, which is the whole point of the rule.
/// </summary>
public sealed class FirehoseSubscriptionException(string type, string agent)
    : InvalidOperationException(TeamRegistry.FirehoseRefusal(type, agent))
{
    public string Type { get; } = type;

    public string Agent { get; } = agent;
}

/// <summary>
/// Teams, and the containers in them.
///
/// **Every team is created with a manager**, named "Manager" by default. A team without one would
/// have no door: a console instructs the manager, so a team lacking one could be created and then
/// never spoken to. Making that structural rather than a step someone remembers is the point.
/// </summary>
/// <param name="workflowSpendLimit">
/// The instance `WorkflowSpendLimit` - what bounds one workflow on a team that has chosen nothing.
///
/// HERE BECAUSE THE RESOLUTION LIVES HERE, in <see cref="TeamRegistry.EffectiveWorkflowBudgetFor"/>,
/// and one function has to know both halves of it or the knowledge is in two places. It is already
/// parsed once in `Program.cs` and handed to `ContainerHost` and `IdleWorkflowOffer` from the same
/// variable, so this is a third reader of one value rather than a second source of it.
///
/// OPTIONAL, AND NULL MEANS UNLIMITED, which is what a fixture that never configured one wants.
/// `Program.cs` always supplies it.
/// </param>
public sealed class TeamRegistry(
    ContainerHost host, AgentCatalog agents, IMemberRunner runner, TeamPaths paths,
    ITeamStore teams, AgentEnvironment environment, FileBrowserPolicy fileBrowser, IMessageLog log,
    EffectiveSubscriptions effective, IRepoClone cloner, long? workflowSpendLimit = null,
    Func<long>? workflowSpendLimitNow = null, SkillDirectory? skillDirectory = null,
    Func<RepoContributor, string, CancellationToken, Task>? prepareClone = null,
    PluginCatalog? plugins = null,
    IPluginMemberSettingsStore? pluginSettings = null,
    ISecretStore? secrets = null)
{
    /// <summary>What each role is offered, for the "Available skills" list every prompt carries.
    /// A registry built without one lists the built-ins.</summary>
    private readonly SkillDirectory _skills = skillDirectory ?? new SkillDirectory();

    public const string DefaultManagerName = "Manager";

    public event Action<TeamSummary>? TeamChanged;


    /// <summary>Which Agent this team's new members run by default, or null when none is chosen.
    /// The first entry of <see cref="MemberAgentsFor"/>.</summary>
    public string? MemberAgentFor(string team) => MemberAgentsFor(team)?.FirstOrDefault();

    /// <summary>Which Agents this team's new members may run, in tie-break order.</summary>
    public IReadOnlyList<string>? MemberAgentsFor(string team) => _memberAgents.GetValueOrDefault(team);

    /// <summary>
    /// Sets it, refusing an Agent that is missing or INTERACTIVE by the same rule every other member
    /// reference follows - a member is woken by a message and never typed at, so offering `shell`
    /// here would be offering a refusal one step later.
    ///
    /// The row FIRST and the cache second: a setter that writes only memory works perfectly all
    /// session and is gone on the next restart.
    /// </summary>
    public async Task SetMemberAgentAsync(string team, string agent, CancellationToken ct = default)
    {
        await SetMemberAgentsAsync(team, [agent], ct);
    }

    /// <summary>
    /// Sets which Agents this team's new members may run, in tie-break order.
    /// </summary>
    public async Task SetMemberAgentsAsync(
        string team, IReadOnlyList<string> memberAgents, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");
        var normalized = memberAgents
            .Where(agent => !string.IsNullOrWhiteSpace(agent))
            .Select(agent => agent.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "A team needs at least one Agent for its new members.", nameof(memberAgents));
        }

        foreach (var agent in normalized)
        {
            if (agents.For(agent) is null)
            {
                throw new NoSuchAgentException(
                    agent,
                    [.. agents.Definitions.Where(d => d.Mode == AgentMode.Headless).Select(d => d.Name)]);
            }
        }

        // THROWS rather than defaulting to a catalog name in code, which would stand in for a row
        // that cannot legitimately be missing: the
        // registry holds this team, and CreateAsync writes the row before the dictionaries. If it IS
        // missing, that is worth naming rather than papering over with an Agent this tenant may not
        // even have.
        var row = (await teams.TeamsAsync(ct))
            .FirstOrDefault(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No stored row for team '{stored}'.");

        await teams.SaveTeamAsync(
            new PersistedTeam(
                stored,
                row.Name,
                normalized[0],
                normalized,
                row.AdditionalInstructions,
                row.Root,
                row.Repos,
                row.Env),
            ct);

        _memberAgents[stored] = normalized;
    }

    /// <summary>This team's additional instructions, or null when it has none.</summary>
    public string? AdditionalInstructionsFor(string team) => _instructions.GetValueOrDefault(team);

    public IReadOnlyList<string> ReposFor(string team) => _repos.GetValueOrDefault(team) ?? [];

    /// <summary>This team's own environment - empty when it has set none.</summary>
    public IReadOnlyDictionary<string, string> EnvFor(string team) =>
        _env.GetValueOrDefault(team) ?? new Dictionary<string, string>(StringComparer.Ordinal);


    /// <summary>
    /// What this team has CHOSEN to allow one workflow to spend, exactly as stored - the RAW
    /// three-state value.
    ///
    /// <para>
    /// <b>NULL MEANS THE TEAM HAS CHOSEN NOTHING. 0 MEANS IT CHOSE UNLIMITED.</b> They are
    /// different answers and this method does not collapse them. Only the dialog wants this -
    /// it has to render an explicit 0 as 0 and an unchosen team prefilled with the instance
    /// figure. <b>Anything that ENFORCES anything must call
    /// <see cref="EffectiveWorkflowBudgetFor"/> instead.</b>
    /// </para>
    /// </summary>
    /// <remarks>
    /// TryGetValue AND NOT GetValueOrDefault. This dictionary holds a non-nullable long, so
    /// GetValueOrDefault answers 0 for a team that is not in it - which is the one value that
    /// means something else entirely here, and would report every team that has chosen nothing
    /// as having chosen unlimited.
    /// </remarks>
    public long? BudgetFor(string team) =>
        _budgets.TryGetValue(team, out var chosen) ? chosen : null;

    /// <summary>
    /// WHAT ACTUALLY BOUNDS ONE OF THIS TEAM'S WORKFLOWS.
    ///
    /// <para>
    /// <b>NULL MEANS UNLIMITED. THIS NEVER RETURNS 0.</b> That is the whole contract, and it is
    /// what lets every consumer ask `is null` and nothing anywhere repeat `is null or 0`.
    /// </para>
    ///
    /// <para>
    /// <b>IT IS ONE-OF AND NOT THE LOWER OF TWO</b>:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>the team chose a figure (&gt; 0) → that figure, <b>even when it is ABOVE the instance
    /// one</b>;</item>
    /// <item>the team chose 0 → null, unlimited;</item>
    /// <item>the team has chosen nothing (null) → the instance `WorkflowSpendLimit`.</item>
    /// </list>
    ///
    /// <para>
    /// A `{team}`-gated route means anyone who reaches that team can raise their own ceiling,
    /// so this is not a backstop against a team that wants to spend. That is deliberate: a limit
    /// nobody can see is one nobody can reason about, and this product chooses the visible control
    /// over the invisible guarantee. Recorded here so it is not mistaken for a bug.
    /// </para>
    ///
    /// <para>
    /// IT BOUNDS ONE WORKFLOW. A team with three open workflows has three budgets of this size,
    /// one each; nothing is shared and nothing is drawn down together.
    /// </para>
    ///
    /// <para>
    /// A DICTIONARY LOOKUP RATHER THAN A ROW READ, because the pump asks this on EVERY wake -
    /// and never cached on a container, because a figure a person has just changed must take
    /// effect on the next wake, which is the moment they are watching for.
    /// </para>
    /// </summary>
    public long? EffectiveWorkflowBudgetFor(string team)
    {
        long? chosen = _budgets.TryGetValue(team, out var value) ? value : null;

        return chosen switch
        {
            // The instance figure is settable at runtime, so it is read here, per wake.
            null => (workflowSpendLimitNow?.Invoke() ?? workflowSpendLimit) is > 0 and var instance
                ? instance
                : null,
            0 => null,
            _ => chosen,
        };
    }

    /// <summary>
    /// Sets what this team may spend on ONE workflow.
    ///
    /// <b>NULL AND 0 ARE DIFFERENT AND BOTH ARE STORED.</b> Null returns the team to the instance
    /// figure; 0 is an explicit "do not stop this workflow". Collapsing 0 to null at the write
    /// would, under the one-of rule, turn a person's "unlimited" into a bound -
    /// see <see cref="ITeamStore.SetBudgetAsync"/>. A negative value is refused at the route.
    ///
    /// ROW FIRST, CACHE SECOND: a setter that writes its dictionary and not its row is correct
    /// all session and gone on the next restart.
    ///
    /// IT PUBLISHES A <see cref="TeamSummary"/> AND NOT A CONTAINER SNAPSHOT. The figure is a team
    /// fact, the summary already rides the hub group-routed to this team, and a snapshot is
    /// per-container - which is the same reason `Reenv` publishes nothing.
    /// </summary>
    public async Task SetBudgetAsync(string team, long? budgetTokens, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");

        await teams.SetBudgetAsync(stored, budgetTokens, ct);

        // ABSENT FROM THE DICTIONARY IS "CHOSEN NOTHING", which is what null means in the column.
        // An explicit 0 is KEPT as 0 - removing it here would read back as "chosen nothing" and
        // resolve to the instance figure, which is the collapse this whole item is written against.
        if (budgetTokens is { } chosen) _budgets[stored] = chosen;
        else _budgets.Remove(stored);

        TeamChanged?.Invoke(SummaryFor(stored, withRoot: false));
    }

    public bool IsPaused(string team) =>
        ExistingName(team) is { } stored && host.IsPaused(stored);

    public async Task SetPausedAsync(string team, bool paused, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");

        await teams.SetPausedAsync(stored, paused, ct);
        await host.SetPausedAsync(stored, paused);

        host.RepublishTeam(stored);
        TeamChanged?.Invoke(SummaryFor(stored, withRoot: false));
    }

    /// <summary>
    /// Sets this team's additional instructions and re-prompts its Manager and members, so the next
    /// wake of each reads them after its role prompt. Null or blank clears them. The row FIRST and
    /// the cache second: a setter that writes only memory works all session and is gone on the next
    /// restart.
    /// </summary>
    public async Task SetAdditionalInstructionsAsync(string team, string? text, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");
        var trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        await teams.SetAdditionalInstructionsAsync(stored, trimmed, ct);

        if (trimmed is null) _instructions.Remove(stored);
        else _instructions[stored] = trimmed;

        await RepromptTeamAsync(stored, ct);

        TeamChanged?.Invoke(SummaryFor(stored, withRoot: false));
    }

    private static string BranchKey(string team, string repo) => $"{team}/{repo}";

    /// <summary>
    /// This repository's stored default branch. <see cref="RepoDefaultBranch.Branch"/> is
    /// null when it is not known, and every caller that needs the branch says so and stops rather
    /// than assuming `main`.
    /// </summary>
    public RepoDefaultBranch DefaultBranchFor(string team, string repo)
    {
        var stored = ExistingName(team) ?? team;
        return _defaultBranches.GetValueOrDefault(BranchKey(stored, repo))
            ?? new RepoDefaultBranch(stored, repo, null, null);
    }

    /// <summary>One entry per configured repository, in list order, known or not.</summary>
    public IReadOnlyList<RepoDefaultBranch> DefaultBranchesFor(string team) =>
        [.. ReposFor(team).Select(url => DefaultBranchFor(team, RepoUrls.DeriveName(url)))];

    /// <summary>
    /// Records what origin's HEAD named after a clone or a successful Fetch - null when that read
    /// failed or named no branch, which records not known. A person's choice is left alone.
    /// </summary>
    public async Task RecordRemoteDefaultBranchAsync(
        string team, string repo, string? branch, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");
        var current = DefaultBranchFor(stored, repo);
        if (current.FromRemote == branch) return;

        await teams.SetRemoteDefaultBranchAsync(stored, current.Repo, branch, ct);
        _defaultBranches[BranchKey(stored, repo)] = current with { FromRemote = branch };

        TeamChanged?.Invoke(SummaryFor(stored, withRoot: false));
    }

    /// <summary>
    /// A person sets (or, with null or blank, clears) a repository's default branch. The value is
    /// kept across every later Fetch and is what the host uses until it is cleared.
    /// </summary>
    /// <exception cref="InvalidOperationException">No such team, or no such repository on it.</exception>
    /// <exception cref="ArgumentException">Not a name git accepts for a branch.</exception>
    public async Task SetPersonDefaultBranchAsync(
        string team, string repo, string? branch, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");
        var name = ReposFor(stored).Select(RepoUrls.DeriveName)
            .FirstOrDefault(n => string.Equals(n, repo, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No repo '{repo}' on team '{team}'.");

        var chosen = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
        if (chosen is not null && !BranchNames.IsValid(chosen))
        {
            throw new ArgumentException($"'{chosen}' is not a branch name git accepts.");
        }

        await teams.SetPersonDefaultBranchAsync(stored, name, chosen, ct);
        _defaultBranches[BranchKey(stored, name)] = DefaultBranchFor(stored, name) with { SetByPerson = chosen };

        TeamChanged?.Invoke(SummaryFor(stored, withRoot: false));
    }

    /// <summary>
    /// This repository's contributor settings. An owned repository - no row, or no
    /// upstream - answers <see cref="RepoContributor.ContributorMode"/> false.
    /// </summary>
    public RepoContributor ContributorFor(string team, string repo)
    {
        var stored = ExistingName(team) ?? team;
        return _contributors.GetValueOrDefault(BranchKey(stored, repo))
            ?? new RepoContributor(stored, repo, null, null, false, null);
    }

    /// <summary>One entry per configured repository, in list order, owned or not.</summary>
    public IReadOnlyList<RepoContributor> ContributorsFor(string team) =>
        [.. ReposFor(team).Select(url => ContributorFor(team, RepoUrls.DeriveName(url)))];

    /// <summary>
    /// Every remote URL the host has on record for a team: each repository's origin and, in
    /// contributor mode, its upstream. What decides whether a git operation gets GH_TOKEN.
    /// </summary>
    public IReadOnlyList<string> RemotesFor(string team) =>
        [.. ReposFor(team), .. ContributorsFor(team).Select(c => c.UpstreamUrl).OfType<string>()];

    /// <summary>
    /// A person sets a repository's contributor settings. A blank upstream makes it owned
    /// again and clears the fork owner with it; a blank fork owner is read from the origin URL's
    /// owner segment. Stores only: the clone's remotes and hook are the caller's to bring in line.
    /// The recorded pull request is left alone.
    /// </summary>
    /// <exception cref="InvalidOperationException">No such team, or no such repository on it.</exception>
    /// <exception cref="ArgumentException">An upstream that is not an http(s) URL or is the origin
    /// itself, a fork owner that is not an account name, or a CLA note too long to be a note.</exception>
    public async Task<RepoContributor> SetContributorAsync(
        string team, string repo, string? upstreamUrl, string? forkOwner, bool dcoSignOff, string? claSignedNote,
        CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");
        var originUrl = ReposFor(stored)
            .FirstOrDefault(u => string.Equals(RepoUrls.DeriveName(u), repo, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No repo '{repo}' on team '{team}'.");
        var name = RepoUrls.DeriveName(originUrl);

        var settings = ContributorSettings.Validate(stored, name, originUrl, upstreamUrl, forkOwner, dcoSignOff, claSignedNote);
        var current = ContributorFor(stored, name);
        var next = settings with { PullRequest = current.PullRequest };

        await teams.SetRepoContributorAsync(next, ct);
        _contributors[BranchKey(stored, name)] = next;

        TeamChanged?.Invoke(SummaryFor(stored, withRoot: false));
        return next;
    }

    /// <summary>
    /// Records (or, with null, forgets) a repository's pull request. Stored only; its
    /// settings are left alone.
    /// </summary>
    public async Task RecordPullRequestAsync(
        string team, string repo, RepoPullRequest? pullRequest, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");
        var current = ContributorFor(stored, repo);

        await teams.SetRepoPullRequestAsync(stored, current.Repo, pullRequest, ct);
        _contributors[BranchKey(stored, repo)] = current with { PullRequest = pullRequest };
    }

    /// <summary>
    /// Re-composes every container's prompt, for when what EVERY role is told changed - a custom
    /// skill added, edited or removed changes the "Available skills" list.
    /// </summary>
    public async Task RepromptAllAsync(CancellationToken ct = default)
    {
        foreach (var team in _teams.Keys.ToList())
        {
            await RepromptTeamAsync(team, ct);
        }
    }

    /// <summary>Re-composes the prompt of every container on one team from its stored member row.</summary>
    private async Task RepromptTeamAsync(string team, CancellationToken ct)
    {
        var members = (await teams.MembersAsync(ct))
            .Where(m => string.Equals(m.Team, team, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var id in _teams.GetValueOrDefault(team)?.ToList() ?? [])
        {
            if (host.Find(id) is not { } container) continue;

            container.Reprompt(
                ComposePrompt(
                    id,
                    container.Snapshot().Agent,
                    container.Snapshot().Name,
                    members.GetValueOrDefault(id.Name)?.SystemPrompt,
                    container.Environment));
        }
    }

    /// <summary>
    /// What a manager may cause. Read and Tell so it can see the board and dispatch;
    /// CreateContainer so it can spin up a worker when a human asks for one.
    ///
    /// NOT CreateTeam - outside a manager's remit, and the permit that widens blast radius
    /// fastest.
    /// </summary>
    public static IReadOnlySet<string> ManagerPermits { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Permits.Read, Permits.Tell, Permits.CreateContainer,
            Permits.Skills,
            // It dispatches and then waits. "Dispatched, waiting on Helen Morse" is exactly
            // what a person looking at the board wants to read off the manager's own card.
            Permits.Progress,
        };

    /// <summary>
    /// A ceiling on a display name, because it is rendered in a ribbon heading that ellipsises
    /// rather than wraps - a label long enough to fill the strip would leave the block unreadable
    /// and every command under it unlabelled. Generous rather than tuned: it exists to refuse a
    /// pasted paragraph, not to shape anyone's naming.
    /// </summary>
    public const int MaximumLabelLength = 60;

    // Case-INSENSITIVE. "Test Team" and "test team" are the same team to a human, and treating them
    // as different would produce two teams whose managers are both called "Manager" - and since the team
    // is half of a container's identity, that is the same identity everywhere downstream. Comparison
    // is ordinal rather than culture-aware on purpose: a team name is an identifier, and a Turkish
    // dotless i deciding whether two teams are the same is not a behaviour anyone wants.
    //
    // The members are qualified IDS, not bare names. A list of names would have to be resolved
    // against the host by name, and then one container can come back as the card for two
    // different teams.
    private readonly Dictionary<string, List<ContainerId>> _teams = new(StringComparer.OrdinalIgnoreCase);
    // Tenant-wide: one Concierge setting set for the whole tenant, then reused for every
    // team launch. NULL until somebody chooses: the launcher then picks the default.
    private string? _Concierge;

    /// <summary>Which Agent each team's NEW members run. Absent only for a stored row that carries
    /// none, which is a refusal at the hire.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _memberAgents =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each team's additional instructions. Absent means none.</summary>
    private readonly Dictionary<string, string> _instructions = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IReadOnlyList<string>> _repos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each team repository's stored default branch, write-through cached like
    /// <see cref="_repos"/>, keyed by <see cref="BranchKey"/>. Absent means not known.</summary>
    private readonly Dictionary<string, RepoDefaultBranch> _defaultBranches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each team repository's contributor settings, cached like
    /// <see cref="_defaultBranches"/> and keyed the same way. Absent means owned.</summary>
    private readonly Dictionary<string, RepoContributor> _contributors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A team's own named values, write-through cached like <see cref="_repos"/> beside it.
    /// Keyed case-insensitively on the TEAM, whose identifier folds case; the VALUES inside are
    /// ordinal, because an environment variable name does not.</summary>
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _env =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What each team has CHOSEN to allow one workflow to spend.
    ///
    /// <para>
    /// <b>ABSENT AND 0 ARE DIFFERENT HERE.</b> Absent means the team has chosen nothing and
    /// inherits the instance figure; a stored 0 means it chose UNLIMITED. The `long` is not
    /// nullable because "chosen nothing" is absence from the dictionary rather than a null value
    /// in it - two spellings of the same state is how one of them gets forgotten.
    /// </para>
    ///
    /// <para>
    /// A plain dictionary rather than a <c>ConcurrentDictionary</c>: it is read once per WAKE,
    /// which is the traffic every cache in this region was written for, and written only from
    /// request threads. <see cref="EffectiveWorkflowBudgetFor"/> is its only resolving reader.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, long> _budgets = new(StringComparer.OrdinalIgnoreCase);

    // A WRITE-THROUGH CACHE of the `teams` table's name column, not the record of truth. Filled
    // once by RestoreAsync at startup and kept in step by every writer below.
    //
    // A cache rather than a read per call because All() is synchronous and on the path of every
    // overview request and every container snapshot push; making it async to reach the database
    // would turn one design decision into a signature change across the whole Host.
    //
    // It holds a team only once someone has relabelled it - ABSENCE means "the label is the name" -
    // so the default cannot drift and renaming back to the name clears both this entry and the
    // row's name column rather than storing a redundant copy of a string that already exists.
    //
    // It deliberately holds labels for teams the REGISTRY does not currently hold. Restoration
    // keeps the two aligned across restarts, but they can still diverge - a team deleted while
    // its documents folder survives is the ordinary case - and a screen has to render such a team
    // by a label rather than by an identifier the reader has never been shown.
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The predecessor documents folder a team's CREATE moved aside, when it moved one. Written by
    /// <see cref="CreateAsync"/> and read by the create route, which is what puts the collision in
    /// the response body and in the tenant log.
    ///
    /// A FACT ABOUT ONE CREATE and nothing else, which is why it is not on <c>TeamSummary</c>: the
    /// board renders that on every poll, and a team is not permanently "the one that displaced
    /// something". Not persisted for the same reason - the durable record is the tenant row.
    /// </summary>
    private readonly Dictionary<string, string> _retiredDocuments =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The predecessor documents folder this team's creation retired, or null. See
    /// <see cref="_retiredDocuments"/>.</summary>
    public string? RetiredDocumentsFor(string team) =>
        ExistingName(team) is { } stored ? _retiredDocuments.GetValueOrDefault(stored) : null;

    // The re-entry guard for RestoreAsync. NOT `_teams.Count > 0` - that reads "has anything been
    // restored", not "has RestoreAsync run", and the two diverge against a database with zero teams,
    // where a second call would sail through unrefused.
    private bool _restored;

    /// <summary>
    /// Brings back every team and every member from the store, and is what makes a restart
    /// invisible.
    ///
    /// AWAITED by <c>Program.cs</c> before anything can serve a request and before the pump takes a
    /// pass, because a registry that has not been restored is not
    /// wrong, only empty - which presents as "my teams are gone" rather than as a startup fault,
    /// and would have every team the database still holds refused while it lasted.
    ///
    /// Separate from the constructor because it is I/O and the constructor is called from the DI
    /// container.
    ///
    /// It REBUILDS rather than reads. Prompts are composed here through
    /// <see cref="ComposePrompt"/> - the same call creation makes - from the name the team has NOW
    /// and the roster it has now, which is why a manager stores no prompt at all and a member
    /// stores only the half a person typed.
    /// </summary>
    public async Task<RestoreReport> RestoreAsync(CancellationToken ct = default)
    {
        // ONCE. A second call does not merely fail: the team loop below replaces every roster with
        // an empty list, and only then does ContainerHost refuse the first container it is handed
        // again - leaving the registry holding teams with no members while the host still holds the
        // containers. All() would render empty teams and the next RepromptManager would strip a
        // manager's roster. A refusal that changes nothing is strictly better than that.
        //
        // A FLAG, not `_teams.Count > 0` - the count is zero for a legitimately restored empty
        // database too, and a second call must be refused just as surely there.
        if (_restored)
        {
            throw new InvalidOperationException("The registry has already been restored.");
        }

        _restored = true;

        var restored = await teams.TeamsAsync(ct);
        var interactive = await teams.ConciergeSettingsAsync(ct);
        _Concierge = interactive.Agent;

        // The teams whose root could not be reached on THIS start. Keyed the way every other team
        // comparison in this codebase is, and filled by the teams pass so the members pass can mark
        // every container that belongs to one.
        var unreachable = new Dictionary<string, UnreachableTeamRoot>(StringComparer.OrdinalIgnoreCase);

        // The stored teams this start could not rebuild at all. See the guard at the top of the
        // loop below.
        var unrestorable = new List<UnrestorableTeam>();

        // TEAMS FIRST, all of them, because a member cannot be placed on a team the registry does
        // not hold - and the label has to be in the cache before any prompt is composed, or every
        // restored member is introduced to a team by its identifier.
        //
        // WRAPPED per team, matching MigrationFailedException's own precedent: the team id is known
        // only at this seam, a catch-all in Program.cs would swallow an unrelated startup failure
        // and report it as a restoration problem, and cancellation passes through untouched - a
        // shutdown is not a restoration failure and must not be dressed as one.
        foreach (var team in restored)
        {
            // A ROW WHOSE ID CANNOT BE A CONTAINER ID, SKIPPED AND SAID OUT LOUD.
            //
            // Every team here is about to become the team half of at least one `ContainerId` - its
            // members' below, and its manager's in the re-prompt pass at the end, which builds the
            // id before it checks whether there IS a manager. That constructor THROWS, the throw is
            // outside this loop's own try (and would be no better inside it: Program.cs turns
            // `TeamRestorationFailedException` into `Environment.ExitCode = 1`, which is the
            // restart loop rather than an escape from it), so one such row would take the whole
            // instance down and every other team with it.
            //
            // A row gets here when a `teams` row is MANUFACTURED with an illegal id - which is what
            // a SQL restatement of `IsLegalName` that drifted wider than this rule would produce.
            // This is the guard for the class rather than for any one instance of it.
            //
            // WARNS AND SERVES, the same direction taken for an unreachable team root a few lines
            // below, and for the same reason: the instance starts,
            // says what it could not restore, and every other team is up. The ROW IS NOT TOUCHED -
            // nothing here repairs or deletes a team's data on its behalf.
            if (!ContainerId.IsLegalName(team.Id))
            {
                unrestorable.Add(new UnrestorableTeam(
                    team.Id,
                    $"'{team.Id}' is not a legal team identifier, so none of its containers can be "
                    + "addressed. The row and its data are untouched."));

                continue;
            }

            try
            {
                _teams[team.Id] = [];

                if (team.MemberAgents is not null)
                {
                    _memberAgents[team.Id] = team.MemberAgents;
                }

                if (team.AdditionalInstructions is { Length: > 0 } storedInstructions)
                {
                    _instructions[team.Id] = storedInstructions;
                }

                if (team.Repos is { Count: > 0 }) _repos[team.Id] = team.Repos;
                if (team.Env is { Count: > 0 }) _env[team.Id] = team.Env;

                // `is { } chosen` AND NOT `is > 0`. A stored 0 is an explicit "unlimited" and must
                // come back through a restart as one; testing `is > 0` here would silently return
                // a team that chose unlimited to the instance figure.
                if (team.BudgetTokens is { } chosenBudget) _budgets[team.Id] = chosenBudget;

                await host.SetPausedAsync(team.Id, team.Paused);

                // Only the teams that HAVE a name. A null name is "never relabelled", and copying the
                // identifier into the cache for those would give "not relabelled" a second
                // representation - the very thing SetNameAsync(null) exists to avoid.
                if (team.Name is { } name) _labels[team.Id] = name;

                // REGISTERED BEFORE ANY PATH IS COMPOSED, and in the TEAMS pass rather than the
                // members pass below - a member's workspace path is composed during its own
                // restore, and RootFor throws for a team nobody has registered yet.
                paths.Register(team.Id, team.Root);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new TeamRestorationFailedException(team.Id, exception);
            }

            // OUTSIDE THE REFUSAL ABOVE, AND THAT IS THE WHOLE POINT OF THE SPLIT.
            //
            // Recreated when missing, NEVER deleted - a restoration that recreated unconditionally,
            // or that keyed either tree by the team's Name rather than its Id, would strand a team's
            // documents in a folder nothing looks in. An empty listing is a legal state for a team
            // that never had a document, so the loss would be silent. EnsureSkeletonAsync is
            // idempotent and rewrites no existing marker, which is exactly what a call on every
            // start needs.
            //
            // A TEAM CAN BE PLACED, so this path can be on another volume or a network
            // share - and a machine that reboots with the drive unplugged throws
            // DirectoryNotFoundException here. Inside the try above that would become
            // TeamRestorationFailedException, the Host would exit 1, its supervisor would restart it
            // into the same refusal forever, and EVERY OTHER TEAM WOULD BE DOWN TOO, default-rooted
            // ones included - with no way back, because `teams.root` has no setter, no dialog field
            // and no operator switch.
            //
            // So it WARNS AND SERVES: failing closed turns "your files are
            // exactly where they have always been" into "your instance will not boot", for a
            // condition in which nothing is wrong with the data. The team is still restored and
            // still visible; what it must not do is RUN, because ProcessAgentRunner falls back to
            // the Host's own current directory for a missing workspace - the source tree, for
            // anyone running from a clone. Every member of it is marked below and refuses at its
            // wake instead.
            try
            {
                await TeamPaths.EnsureSkeletonAsync(paths.RootFor(team.Id), team.Id, ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unreachable[team.Id] = new UnreachableTeamRoot(
                    team.Id, paths.RootFor(team.Id), exception.Message);
            }
        }

        foreach (var branch in await teams.RepoDefaultBranchesAsync(ct))
        {
            if (ExistingName(branch.Team) is { } owner)
            {
                _defaultBranches[BranchKey(owner, branch.Repo)] = branch with { Team = owner };
            }
        }

        foreach (var contributor in await teams.RepoContributorsAsync(ct))
        {
            if (ExistingName(contributor.Team) is { } owner)
            {
                _contributors[BranchKey(owner, contributor.Repo)] = contributor with { Team = owner };
            }
        }

        foreach (var member in await teams.MembersAsync(ct))
        {
            // A member whose team is gone. The foreign key makes it unreachable, and skipping is
            // still the right answer: a container with no team has no identity worth rebuilding.
            if (!_teams.TryGetValue(member.Team, out var roster)) continue;

            var id = new ContainerId(member.Team, member.Name);

            // Its team's root was not there when the teams pass ran. The path is still composed -
            // the container needs a working directory and the honest one is where its workspace
            // WOULD be - but nothing is created under it and the container is marked, so it refuses
            // at its wake rather than running in the Host's own directory.
            var unreachableRoot = unreachable.GetValueOrDefault(member.Team)?.Root;

            try
            {
                var workspace = paths.WorkspaceFor(id);

                // Skipped for an unreachable root, and skipping is the point rather than an
                // optimisation: this throws for a drive that is not there, and inside this try that
                // is TeamRestorationFailedException and an instance that will not boot - the very
                // refusal the teams pass above is split apart to avoid.
                if (unreachableRoot is null) Directory.CreateDirectory(workspace);

                // NOT a refusal, and never a reason the Host will not boot. The catalog is
                // configuration and the database is data, so the two can
                // legitimately disagree after a restore. The container comes back, says which Agent
                // it cannot find, and refuses when something wakes it.
                //
                // Looked up only to decide whether to MARK it. Nothing is cached from this: the
                // runner resolves the command by name on every invocation, so a preset added back
                // afterwards works on the next wake with no restart.
                //
                // A PLUGIN MEMBER IS NOT A MISSING AGENT. Whether its plugin is installed is the
                // plugin runner's question, asked on every wake, exactly as this one is.
                var isPlugin = MemberRef.IsPlugin(member.Agent);
                var command = isPlugin ? null : agents.For(member.Agent);

                var permits = new HashSet<string>(member.Permits, StringComparer.Ordinal);

                // Built BEFORE ComposePrompt, not after: C# evaluates constructor arguments
                // left to right, and ComposePrompt is one of them, so composing the prompt
                // first would hand it an empty environment and leave any `{env:...}` token
                // resolving against nothing. Reused for the definition's own Environment field
                // below rather than minted twice.
                //
                // Re-minted, never restored: a credential is stored as a SHA-256 hash and
                // cannot be read back. MintAsync upserts on the principal id, so the old one
                // stops working and no rows accumulate.
                var containerEnvironment = isPlugin
                    ? PluginEnvironment
                    : await environment.ForContainerAsync(
                    id, member.Agent, permits, EnvFor(member.Team), ReposFor(member.Team), ct);

                // RestoreAsync, never AddAsync. AddAsync advances the cursor to the head of the log,
                // which is right for something that did not exist a moment ago and destructive here:
                // it would discard everything published while this host was down. The stored floor
                // is the one this container was created with and is the one it comes back on.
                var container = await host.RestoreAsync(
                    new ContainerDefinition(
                        id, member.Agent,
                        ComposePrompt(
                            id, member.Agent, member.Label ?? member.Name, member.SystemPrompt,
                            containerEnvironment),
                        ResolveMemberWorkingDirectory(id, workspace), member.Subscribes,
                        containerEnvironment,
                        Label: member.Label,
                        Permits: permits,
                        MissingAgent: command is null && !isPlugin ? member.Agent : null,

                        // Marked the same way the Agent above is, and for the same reason: the
                        // container comes back, says what it cannot reach, and refuses when
                        // something wakes it. Unlike that one there is nothing on a settings
                        // screen that clears it - a team's root has no setter - so the recovery the
                        // runner names is the folder itself and a restart.
                        UnreachableRoot: unreachableRoot,
                        HiredFor: member.HiredFor),
                    runner,
                    member.FloorSeq,
                    ct);

                // RestoreAsync, never a direct write - ContainerHost does not persist a
                // subscriptions row at all. `member.Subscribes` is the base, and this member's own
                // enabled event triggers (there may be none yet, or several the host was down for)
                // are unioned in from the store, exactly as they would be on any other edit.
                await effective.RecomputeAsync(id, ct);

                // A RESTART IS NOT A WAKE, SO IT MUST NOT CLEAR WHAT ONLY A WAKE CLEARS.
                //
                // `Blocked`, `NeedsDecision` and `Failed` are fields on the live container and
                // nothing writes them down, so every one of them dies with the previous process -
                // while the ROWS that produced them sit in the append-only log. The team tile reads
                // that log and the member's card reads this snapshot, so without this the two
                // surfaces disagree about one member for as long as the host has been up:
                // BLOCKED on the tile, idle with nothing to say on the card.
                //
                // DERIVED, NEVER PERSISTED - see `IMessageLog.LiveMarksForAsync`. A column would be
                // a second store of a fact the log already holds.
                //
                // THE MEMBER'S OWN FLOOR, not the team minimum: this reader is per-member so it can
                // afford the exact answer, and a member must not come back wearing a give-up its
                // team was reset out of.
                //
                // AHEAD OF `ResumePendingAsync`, which runs later and marks the runs this restart
                // actually interrupted. That is NEWER information about the same field and has to
                // win; `MarkFailed` publishes only when the value moves, so an unchanged mark
                // costs nothing.
                var marks = await log.LiveMarksForAsync(id, member.FloorSeq, ct);

                if (marks.Any)
                {
                    // ALL THREE, NOT THE HIGHEST-RANKED ONE. The snapshot carries three separate
                    // fields and the RANKING lives in the client - `containerMark()` orders them
                    // `failed > blocked > needs-decision` and the tab chip orders them identically,
                    // in two places documented as having to agree. Choosing one here would be a
                    // third ranking, on the server, silently overriding both.
                    if (marks.FailureReason is { } failure) container.MarkFailed(failure);
                    if (marks.BlockedReason is { } reason) container.MarkBlocked(reason);
                    if (marks.Question is { } question) container.MarkNeedsDecision(question);
                }

                roster.Add(id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new TeamRestorationFailedException(member.Team, exception);
            }
        }

        // MANAGERS LAST, and once each. A manager's prompt is composed from its team's roster, so
        // one restored before its members comes back believing the team is empty - and it would
        // only be corrected by the next member somebody adds. Re-prompting as each member arrives
        // would do the same job N times over; this is the same information for one call per team.
        //
        // A no-op for a team whose manager is absent, which is what Reprompt already does.
        //
        // Over what was REGISTERED, not over what was read: a row skipped above has no roster, no
        // path and no legal id, and this call would build one anyway - the id is composed on the
        // first line of RepromptManagerAsync, ahead of its own no-op guard.
        foreach (var team in restored.Where(team => _teams.ContainsKey(team.Id)))
        {
            await RepromptManagerAsync(team.Id, ct);
        }

        // RETURNED rather than printed. This is a module and the console belongs to its caller -
        // the same split SchemaMigrator makes with its pre-migration backup path.
        return new RestoreReport([.. unreachable.Values], unrestorable);
    }

    /// <summary>
    /// Which agent this team's console runs, or null when nobody has chosen one. It is never the
    /// team's own member agent: `echo` is a fine container probe and a useless console.
    /// </summary>
    /// <remarks>
    /// The setter is <see cref="SetConciergeAsync"/>. It writes through <see cref="ITeamStore"/>
    /// first, the way <see cref="SetLabelAsync"/> does - a setter that wrote only the dictionary
    /// above would see the setting vanish on the next restart.
    /// </remarks>
    public string? ConciergeFor(string team) =>
        ExistingName(team) is null
            ? throw new InvalidOperationException($"No team '{team}'.")
            : _Concierge;

    /// <summary>
    /// THE CONCIERGE, ASKED WITHOUT NAMING A TEAM -- which is the only honest way to ask, since
    /// there is one for the whole instance.
    ///
    /// The reader above takes a team and ignores it beyond checking that it exists. Reached only
    /// that way, an instance with no teams at all could not read its own Concierge, so the one
    /// screen that could fix a broken setting would be unreachable exactly when nothing else
    /// worked either.
    /// </summary>
    public TenantConciergeSettings Concierge() => new(_Concierge);

    /// <summary>Every container on a team, manager included. Empty for a team this registry does not
    /// hold, which is not an error - a caller deleting a team asks before it knows.</summary>
    public IReadOnlyList<ContainerId> ContainerIdsOf(string team) =>
        _teams.TryGetValue(team, out var ids) ? [.. ids] : [];

    /// <summary>
    /// WHERE THIS TEAM'S HISTORY STARTS: the LOWEST <c>floor_seq</c> any of its live containers
    /// holds. Rows below it belong to a previous incarnation of this team and are not its work.
    ///
    /// <para>
    /// ONE STORE OF A FACT AND SEVERAL READERS: a reader that ignores `floor_seq` hands a
    /// recreated team its predecessor's status tail, spend or kanban cards with nothing failing.
    /// This method exists so every reader asks rather than re-deriving.
    /// </para>
    ///
    /// <para>
    /// A TEAM FLOOR RATHER THAN A PER-MEMBER ONE, and it is the same trade
    /// <c>SumUsageForTeamAsync</c> already makes for the identical reason: a per-source floor means
    /// joining `messages` to `team_members`, which <c>ILedger</c> deliberately forbids.
    /// </para>
    ///
    /// <para>
    /// THE MINIMUM, NOT THE MAXIMUM, AND THAT IS WHAT KEEPS A DELETED MEMBER'S CARDS. A member you
    /// removed from the CURRENT team wrote its rows above this floor, so they survive - which the
    /// board requires, since such a card returns to unassigned rather than vanishing. Taking the maximum
    /// would erase exactly the history the board is for.
    /// </para>
    ///
    /// <para>
    /// <c>DefaultIfEmpty</c> rather than a <c>Min</c> over nothing: a team with no live containers
    /// has no rows to scope either way, and <c>Min()</c> on an empty sequence throws.
    /// </para>
    /// </summary>
    public long FloorFor(string team) =>
        ContainerIdsOf(team)
            .Select(host.Find)
            .Where(container => container is not null)
            .Select(container => container!.Snapshot().SinceSeq)
            .DefaultIfEmpty(0)
            .Min();

    /// <summary>
    /// Removes one member's ROW, this registry's memory of it, and then re-prompts the manager -
    /// the three things that have to move together for a team to be internally consistent about who
    /// is on it. Everything else a member owns is <c>MemberDeletion</c>'s, which is the only caller
    /// and does it in an order that matters.
    ///
    /// The three are here rather than there because they are the registry's own state, and because
    /// the re-prompt is private: a caller that could delete the row without it would leave the
    /// manager holding a roster naming somebody who is gone. THE ORDER WITHIN IS FIXED - the id
    /// leaves <c>_teams</c> BEFORE the re-prompt, because <c>MembersFor</c> composes the roster out
    /// of exactly that list, so re-prompting first rebuilds the prompt it was called to change.
    ///
    /// Silent when there is no such team or no such member: this runs after the container has been
    /// removed from the host, so "not there" is the state it is working towards.
    /// </summary>
    public async Task RemoveContainerAsync(string team, string name, CancellationToken ct = default)
    {
        if (ExistingName(team) is not { } stored) return;

        await teams.DeleteMemberAsync(stored, name, ct);

        if (_teams.TryGetValue(stored, out var ids))
        {
            // ContainerId equality folds case on both halves, so this removes the member however
            // the caller happened to spell it.
            ids.RemoveAll(id => id == new ContainerId(stored, name));
        }

        await RepromptManagerAsync(stored, ct);
    }

    /// <summary>
    /// Drops a team from THIS registry's memory. The rows, the processes and the directories are
    /// somebody else's job - see <c>TeamDeletion</c>, which is the only caller and does them in an
    /// order that matters.
    /// </summary>
    public void Forget(string team)
    {
        if (ExistingName(team) is not { } stored) return;

        _teams.Remove(stored);
        _labels.Remove(stored);
        _memberAgents.Remove(stored);
        _instructions.Remove(stored);
        _repos.Remove(stored);
        _env.Remove(stored);
        foreach (var key in _defaultBranches.Keys.Where(k => k.StartsWith(stored + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _defaultBranches.Remove(key);
        }
        foreach (var key in _contributors.Keys.Where(k => k.StartsWith(stored + "/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _contributors.Remove(key);
        }

        // A CACHE A FORGOTTEN TEAM LEAVES BEHIND IS INHERITED BY ITS SUCCESSOR. Delete a team
        // whose figure was 5,000,000, recreate the identifier, and without this line
        // `EffectiveWorkflowBudgetFor` goes on answering 5,000,000 against a row that is NULL -
        // until a restart quietly makes it the instance figure instead.
        _budgets.Remove(stored);

        // Same rule, and this one names a PATH: a successor of this identifier that retires nothing
        // must not be told it displaced the folder its predecessor's create displaced.
        _retiredDocuments.Remove(stored);
    }

    /// <summary>
    /// Points this team's Concierge at another preset.
    /// </summary>
    /// <remarks>
    /// The preset must be INTERACTIVE. <c>AgentCatalog.Interactive</c> returns null both for a
    /// preset this tenant does not have and for a headless one, and the two are told apart before
    /// refusing so the message names which it was: "there is no such Agent" and "that Agent cannot
    /// be typed at" send a person to different places.
    ///
    /// The STORE first, then the cache. A setter that writes only the cache is undone by the next
    /// restart. A write that cannot reach the database must not leave this process serving a
    /// setting the next restart would silently undo.
    ///
    /// Nothing is re-prompted or restarted here: a team's Concierge is launched fresh when
    /// somebody opens the panel, so the next one opened picks this up. A session already attached
    /// keeps the process it started with, which is the same rule everything else on this seam
    /// follows - a run in flight finishes against what it began with.
    /// </remarks>
    public Task SetInteractiveAsync(string team, string? agent, CancellationToken ct = default)
    {
        _ = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");
        return SetInteractiveAsync(agent, ct);
    }

    /// <remarks>A NULL <paramref name="agent"/> keeps what is stored, which is also what code
    /// carrying the live setting across (a catalog reset repointing the Prompt) wants. A blank one
    /// stores NULL - nobody has chosen - and the launcher falls back to ConciergeAgentDefault.</remarks>
    public async Task SetInteractiveAsync(string? agent, CancellationToken ct = default)
    {
        // The Agent carries three states: null keeps what is stored,
        // blank CLEARS the choice (NULL is stored and the launcher falls back to
        // ConciergeAgentDefault, as on an instance nobody has configured), anything else is checked
        // and stored.
        var trimmed = agent is null ? _Concierge
            : string.IsNullOrWhiteSpace(agent) ? null
            : agent.Trim();

        if (agent is not null && trimmed is not null && agents.Interactive(trimmed) is null)
        {
            var offerable = agents.Definitions
                .Where(d => d.Mode == AgentMode.Interactive)
                .Select(d => d.Name)
                .ToArray();

            throw agents.Definition(trimmed) is null
                ? new NoSuchAgentException(trimmed, offerable)
                : NoSuchAgentException.WithNoInteractiveCommand(trimmed, offerable);
        }

        await teams.SetConciergeSettingsAsync(trimmed, ct);

        _Concierge = trimmed;
    }

    /// <summary>
    /// Every team with its own containers' cards.
    ///
    /// Resolved through the qualified ID rather than the bare name: a `host.Find(name)` would hand
    /// the same card to two teams.
    /// </summary>
    /// <param name="withRoot">Whether to fill <see cref="TeamSummary.Root"/>. DEFAULTS TO FALSE,
    /// which is the safe direction and the same shape as a container's permits: a caller that never
    /// thought about it discloses nothing. Only a route that has established the caller is a tenant
    /// admin passes true.</param>
    /// <remarks>
    /// IT FILTERS NOTHING: every human login sees every team. An exclusion here would make "which
    /// teams exist" a question with two answers - and every caller that forgot the second one would
    /// fail silently, since TeamAccess builds its effective set from here. There is no second
    /// enumeration to remember: this is the only one.
    /// </remarks>
    public IReadOnlyCollection<TeamSummary> All(bool withRoot = false) =>
        SummariesFor(_teams.Keys, withRoot);

    private IReadOnlyCollection<TeamSummary> SummariesFor(
        IEnumerable<string> teamIds, bool withRoot)
    {
        var projected = teamIds.Select(team => SummaryFor(team, withRoot));

        // Ordered by NAME, not by label. The order a list is in should not change under someone
        // because a team was relabelled, and the name is the stable half.
        return [.. projected.OrderBy(t => t.Id, StringComparer.Ordinal)];
    }

    private TeamSummary SummaryFor(string team, bool withRoot) =>
        new(
            team,
            // The key IS the stored spelling, so this is a direct lookup rather than a call
            // through ExistingName - which scans every key, and would make this whole projection
            // quadratic for nothing.
            _labels.GetValueOrDefault(team, team),
            [.. (_teams.GetValueOrDefault(team) ?? [])
                .Select(id => host.Find(id)?.Snapshot())
                .OfType<ContainerSnapshot>()],
            ConciergeFor(team),
            MemberAgentFor(team),
            MemberAgentsFor(team),
            AdditionalInstructionsFor(team),
            withRoot ? paths.RootFor(team) : null,
            _repos.GetValueOrDefault(team) ?? [],
            IsPaused(team),

            // BOTH, AND THEY ARE NOT THE SAME NUMBER. The first is what the team CHOSE, which the
            // settings dialog needs so it can tell "typed 0" from "never touched it". The second
            // is what is actually IN FORCE, which is what the KPI bar must measure against - a bar
            // that reassures against a figure nobody is bounded by is worse than no bar.
            BudgetFor(team),
            EffectiveWorkflowBudgetFor(team),
            DefaultBranchesFor(team).Select(b => new TeamRepoDefaultBranch(b.Repo, b.Branch, b.FromRemote, b.SetByPerson)).ToList(),
            ContributorsFor(team).Select(c => new TeamRepoContributor(c.Repo, c.UpstreamUrl, c.ForkOwner, c.DcoSignOff, c.ClaSignedNote)).ToList());

    /// <summary>
    /// What this team is CALLED - its label, or its identifier when it has never been relabelled.
    ///
    /// Answers for a team that does not currently exist, which is the case that matters: a
    /// documents folder outlives the team it names, so a screen asks this about teams the registry
    /// has never heard of and must still get back something a person recognises.
    /// </summary>
    public string LabelFor(string team)
    {
        var stored = ExistingName(team) ?? team;

        return _labels.GetValueOrDefault(stored, stored);
    }

    /// <summary>
    /// Whether some OTHER team already answers to this label.
    ///
    /// Two teams a person cannot tell apart is the whole failure this prevents, and it has to
    /// consider unlabelled teams too - their label IS their identifier, so renaming team `Beta` to
    /// "Alpha" collides with team `Alpha` even though nothing is in the label table for it.
    /// </summary>
    public string? TeamAnsweringTo(string label, string? except = null) =>
        _teams.Keys.FirstOrDefault(name =>
            !string.Equals(name, except, StringComparison.OrdinalIgnoreCase)
            && string.Equals(LabelFor(name), label, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Renames the LABEL. The identifier is untouched - see <see cref="TeamSummary"/>.
    ///
    /// Setting it to the team's own identifier (in any casing) REMOVES the row rather than storing a
    /// copy of it, so "never relabelled" and "relabelled back" are the same state. Stored under the team's
    /// own spelling so a caller who reached this route with different casing cannot create a second
    /// entry the lookup would then miss.
    ///
    /// Re-prompts the team's manager on the way out. The manager's system prompt names the team it
    /// manages, so without this it goes on introducing itself by the old name in the console for the
    /// rest of the process - visible to the person talking to it and to nothing else.
    ///
    /// THE STORE FIRST, then the cache - the same ordering CreateAsync uses and for the same reason:
    /// a failed <see cref="ITeamStore.SetNameAsync"/> must not leave the cache (and so the board)
    /// relabelled while the row is not, which is a rename that silently reverts at the next restart.
    /// </summary>
    public async Task SetLabelAsync(string team, string label, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");

        var trimmed = label.Trim();

        if (string.Equals(trimmed, stored, StringComparison.OrdinalIgnoreCase))
        {
            // NULL, never a copy of the identifier - "never relabelled" and "relabelled back" are one
            // state in the row exactly as they are in the cache below.
            await teams.SetNameAsync(stored, null, ct);
            _labels.Remove(stored);
        }
        else
        {
            await teams.SetNameAsync(stored, trimmed, ct);
            _labels[stored] = trimmed;
        }

        await RepromptManagerAsync(stored, ct);
    }

    /// <summary>
    /// Rebuilds the manager's system prompt from what is true now.
    ///
    /// THREE callers, and they are the only three things that change what a manager needs to know: a
    /// rename changes the team it manages, and adding or DELETING a member changes who it can
    /// dispatch to. Member deletion is the one whose omission leaves
    /// nothing to find - the team goes on living, every row is right, and the manager alone still
    /// believes it can dispatch to somebody who is gone.
    /// <see cref="MemberRuntime.Reprompt"/> exists for this, and is still the only thing in this
    /// codebase that alters a container after creation.
    ///
    /// A no-op when the manager is not there - which is the ordinary state for the instant during
    /// CreateAsync before it has been added, and after a restart for a team nobody has recreated.
    /// </summary>
    private async Task RepromptManagerAsync(string team, CancellationToken ct)
    {
        var manager = new ContainerId(team, DefaultManagerName);

        // No environment is built at a re-prompt the way AddContainerAsync and RestoreAsync build
        // one - there is no credential being minted here, only a rename or a roster change. Reading
        // it back from the live container (MemberRuntime.Environment) rather than
        // passing an empty dictionary is what keeps `{env:...}` resolving the same on every
        // re-prompt as it did at creation; an empty dictionary would resolve it once and then leave
        // it literal forever, silently. Its Agent comes from the same snapshot, for the same
        // reason: a team's manager is not always running the `Manager` preset (a test, or an
        // operator, may have chosen another), and ComposePrompt needs to know which preset's
        // default it is falling back to.
        if (host.Find(manager) is not { } container) return;

        // The container's OWN label, never the constant its identifier happens to be. A manager
        // can be relabelled, so passing `DefaultManagerName` here would make `{member}` flip between
        // the label and "Manager" depending on which composition site ran last - the other three
        // all pass the real label. The token is offered in the editor, so this matters even though
        // the seeded `Manager` text does not use it.
        var snapshot = container.Snapshot();

        // The manager's STORED override, read fresh - not null. Passing null here would make a
        // manager's own prompt survive only until the next rename or member add and then silently
        // revert to its preset's default. Every composition site passes the stored value.
        var member = (await teams.MembersAsync(ct)).FirstOrDefault(
            m => string.Equals(m.Team, team, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.Name, DefaultManagerName, StringComparison.OrdinalIgnoreCase));

        container.Reprompt(
            ComposePrompt(
                manager, snapshot.Agent, snapshot.Name, member?.SystemPrompt, container.Environment));
    }

    /// <summary>
    /// Every member of a team except its manager, as the tools need to address them.
    ///
    /// Read from the live ContainerHost rather than a second map, so the roster a manager is handed
    /// cannot disagree with the cards on the board - the same reason LabelForContainer reads from
    /// there.
    /// </summary>
    private IReadOnlyList<string> MembersFor(string team) =>
        _teams.TryGetValue(team, out var members)
            ? [.. members
                .Where(m => !string.Equals(m.Name, DefaultManagerName, StringComparison.OrdinalIgnoreCase))
                .Select(m => LabelForContainer(m) is var label && string.Equals(label, m.Name, StringComparison.Ordinal)
                    ? m.Name
                    : $"{m.Name} (called \"{label}\")")]
            : [];

    public bool Exists(string team) => _teams.ContainsKey(team);

    // A general trap worth naming: an `Ensure…` that returns early when its row exists writes NEW
    // fields for new installs only, so a new field never reaches a single existing instance. The
    // code that creates a thing is not the code that upgrades it.

    /// <summary>
    /// The STORED spelling of a team matching case-insensitively, or null.
    ///
    /// Exists so a refusal can name the team the board actually shows. Telling someone
    /// "'test team' already exists" when the board reads "Test Team" invites them to go looking for
    /// a second team that is not there.
    /// </summary>
    public string? ExistingName(string team) =>
        _teams.Keys.FirstOrDefault(k => string.Equals(k, team, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An identifier for a team called <paramref name="label"/>, or null when one already exists.
    ///
    /// Derivation first, and a GENERATED name as the fallback - "Team3", say. That fallback is not a
    /// failure path: a label in a non-Latin script has no ASCII to derive from, and the identifier
    /// is never shown to anyone, so inventing one is strictly better than refusing to create a team
    /// because of how its name is spelt. What it must never do is silently resolve a COLLISION -
    /// two labels deriving to one identifier is a real ambiguity and the caller refuses it, naming
    /// the team already in the way by ITS label rather than by an id nobody has seen.
    /// </summary>
    private string? IdentifierFor(string label)
    {
        if (ContainerId.DeriveName(label) is { } derived)
        {
            return _teams.ContainsKey(derived) ? null : derived;
        }

        // Nothing derivable. Walk upwards rather than counting teams: a generated name has to be
        // free, and `_teams.Count + 1` collides the moment anything was ever created by derivation.
        for (var n = 1; ; n++)
        {
            var generated = $"Team{n}";

            if (!_teams.ContainsKey(generated)) return generated;
        }
    }

    /// <summary>
    /// Creates a team called <paramref name="label"/> - what a person types, spaces and accents and
    /// all - and derives the identifier that everything else keys on.
    ///
    /// The person never supplies an identifier and is never shown one. That is the whole point: the
    /// identifier is a directory name, a route value, a SignalR group and half of every container's
    /// id, and asking someone to invent one that satisfies all four would make them do the
    /// machinery's job at the very first step.
    ///
    /// A caller combining creation with a dispatch may supply <paramref name="handleRepoSetup"/>.
    /// It receives completed clone outcomes and returns true if it owns notifying the Manager.
    /// Absent a handler, or when it returns false, creation writes the usual repo instruction.
    /// </summary>
    public async Task<TeamSummary> CreateAsync(
        string label, string agent,
        string? additionalInstructions = null,
        string? memberAgent = null, IReadOnlyList<string>? memberAgents = null,
        string? root = null, IReadOnlyList<string>? repos = null,
        long? budgetTokens = null,
        CancellationToken ct = default,
        Func<IReadOnlyList<RepoCloneOutcome>, bool>? handleRepoSetup = null,
        IReadOnlyDictionary<string, string>? upstreams = null)
    {
        var trimmed = label.Trim();
        var validatedRepos = RepoUrls.Validate(repos);

        // Each repository's upstream, keyed by its URL in `repos`, checked BEFORE anything is
        // created. The team name is not known yet, so the rows are finished below.
        var contributors = new List<RepoContributor>();
        foreach (var (url, upstream) in upstreams ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(upstream)) continue;

            var origin = validatedRepos.FirstOrDefault(r => string.Equals(r, url.Trim(), StringComparison.Ordinal))
                ?? throw new ArgumentException($"The upstream '{upstream}' is for '{url}', which is not one of the team's repositories.");
            contributors.Add(ContributorSettings.Validate(
                "", RepoUrls.DeriveName(origin), origin, upstream, forkOwner: null, dcoSignOff: false, claSignedNote: null));
        }

        // NO DEFAULTS for any of the three Agents, exactly as there are none for the three Prompts.
        // A default here would be a catalog entry a person may rename or remove, and once it was
        // gone the New Team dialog's own preselect - reading the same name - would show an empty
        // picker with nothing saying why.
        if (string.IsNullOrWhiteSpace(agent))
        {
            throw new ArgumentException("A team needs an Agent for its manager.", nameof(agent));
        }

        // NO CONCIERGE HERE, DELIBERATELY. There is ONE Concierge for the tenant --
        // `ConciergeSessionKey` is the USER alone, one per person serving every team they reach --
        // so a per-team argument would describe something that does not exist.
        //
        // It would be worse than misleading: the setter writes the TENANT-WIDE row, so creating one
        // team would silently repoint the Concierge for every existing team and nothing would say
        // so.
        //
        // The setting lives on its own route, `PUT /api/concierge`, reached from the Admin ribbon
        // rather than from anything team-shaped.

        var memberAgentNames = (memberAgents ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (memberAgentNames.Count == 0 && !string.IsNullOrWhiteSpace(memberAgent))
        {
            memberAgentNames.Add(memberAgent.Trim());
        }

        // Checked here with the other two rather than at hire time, so a team is never stored
        // unable to hire - the same rule its Prompt follows.
        if (memberAgentNames.Count == 0)
        {
            throw new ArgumentException(
                "A team needs at least one Agent for its new members.", nameof(memberAgents));
        }

        // FIRST, before the team row, the dictionaries or the docs directory below - all of which
        // AddContainerAsync's own guard is too late to protect, because it only runs once this
        // method has already written every one of them for the team the Manager is about to join.
        // Without this, a refused Agent leaves a team with a row, a roster and a docs folder on
        // disk and no manager at all - visible on /api/overview and indistinguishable from a team
        // nobody has added anyone to yet.
        if (agents.For(agent) is null)
        {
            throw new NoSuchAgentException(
                agent, [.. agents.Definitions.Where(d => d.Mode == AgentMode.Headless).Select(d => d.Name)]);
        }


        // No prompt is chosen here: the Manager and every member are told their job by the
        // built-in role prompt. What a person may add is the team's additional instructions.
        var instructions = string.IsNullOrWhiteSpace(additionalInstructions)
            ? null
            : additionalInstructions.Trim();

        // THE CONSOLE'S Agent, checked here too and for a second reason beyond the obvious one. The
        // obvious one is that a team created naming an Agent it has no console command for can
        // never open its console, and nothing says so until someone tries. The second is worse and
        // is why this cannot be left to the Agents screen: PUT /api/agents refuses any catalog that
        // leaves a team's Concierge without an `interactive`, so a team stored pointing at
        // one that never had it makes every future catalog write refuse, permanently, naming a team
        // whose setting nothing can change.
        // THE CONCIERGE IS NOT CHECKED HERE. `_Concierge` is one field for the whole instance,
        // restored from `ConciergeSettingsAsync`, and a team row has no Concierge columns.
        //
        // NOT CHECKING IT IS THE POINT rather than an omission. A check on `_Concierge` would refuse
        // a team for the state of a TENANT setting its creator
        // did not choose and may not be able to change -- and it would fire on a whole instance at
        // once, since every team reads the same field. A person with a catalog that has no
        // interactive preset would find they could not create teams at all, with the refusal
        // naming an Agent nobody mentioned.
        //
        // The real hazard -- a stored name with no `interactive` command making every future
        // catalog write refuse -- has ONE cause and ONE cure for the instance: `SetConciergeAsync` asks exactly this question before it writes, and the
        // Concierge screen is where it is fixed. Creating a team is not the place to discover it.
        // Checked before the row, for the reason every other reference on this path is: a team
        // stored naming an Agent it cannot hire on is only discovered the first time a manager
        // tries. HEADLESS, because a member always is - `AgentCatalog.For` answers null for a
        // missing preset and an interactive one alike, so one lookup covers both.
        foreach (var memberAgentName in memberAgentNames)
        {
            if (agents.For(memberAgentName) is null)
            {
                throw new NoSuchAgentException(
                    memberAgentName,
                    [.. agents.Definitions.Where(d => d.Mode == AgentMode.Headless).Select(d => d.Name)]);
            }
        }

        if (TeamAnsweringTo(trimmed) is { } answering)
        {
            throw new TeamNameTakenException(LabelFor(answering));
        }

        var team = IdentifierFor(trimmed)
            ?? throw new TeamNameTakenException(LabelFor(ContainerId.DeriveName(trimmed)!));

        // Belt and braces over IdentifierFor, which is the only producer: a name becomes a real
        // directory four lines down, and this guard is what stops "../.." being a team you can
        // create. A derivation bug should be a refusal here rather than a path escape there.
        if (!ContainerId.IsLegalName(team))
        {
            throw new ArgumentException($"'{team}' is not a legal team name.", nameof(label));
        }

        // THE ROOT, checked here rather than by the route: `team` - the derived identifier - only
        // exists inside this method, and every check on this path runs before the row is written for
        // the same reason the Agent and Prompt checks above do. A bad root must leave nothing behind.
        // CHOOSING THE DEFAULT LOCATION EXPLICITLY IS INDISTINGUISHABLE FROM LEAVING THE BOX BLANK,
        // and that collapse is deliberate rather than tidy. The field says "Place team in" and
        // defaults to the instance root, so a person who opens the picker and confirms what is
        // already there is the OBVIOUS case - and storing it verbatim pins the team to an absolute
        // copy of a runtime value, which is the second store of one fact `teams.root` is NULL to
        // avoid: move, rename or re-instance the data root and that row points at a folder that is
        // not there.
        //
        // Compared with trailing separators trimmed, so `/data` and `/data/` are the same root.
        var resolvedRoot = root is { } chosen ? Path.GetFullPath(chosen) : null;

        if (resolvedRoot is { } typed && IsInstanceRoot(typed)) resolvedRoot = null;

        if (resolvedRoot is { } placed)
        {
            // THE SAME BOUNDARY THE PICKER IS BOUND BY, enforced HERE rather than trusted from the
            // dialog. Without this, the allowlist is a decoration on the picker: anyone who can
            // reach this route can TYPE a path the picker refuses to show and the team lands there
            // anyway. The instance root itself never reaches here: `IsInstanceRoot` above already
            // collapsed it to `null`, and `FileBrowserOptions.Effective` always seeds it as an
            // allowed root regardless.
            //
            // FIRST, ABOVE EXISTS AND WRITABLE, AND THAT ORDER MATTERS. Checked last, three things
            // go wrong at once. The probe below WRITES A FILE into a folder this host is about to
            // declare out of bounds, and its clean-up swallows an IOException - so on a share that
            // permits create but not delete, a `.harness-probe-<guid>` would be left there
            // permanently. The refusal would NAME THE WRONG PROBLEM: `/etc` answers "is not
            // writable", so a person opens up write access and comes back to a second refusal
            // saying it was never allowed at all. And the pair together would be an
            // existence-and-writability ORACLE over the whole filesystem. The three refusals order
            // by cost: not allowed, then does not
            // exist, then not writable - and nothing is written before the boundary is checked.
            if (!fileBrowser.Allows(placed))
            {
                throw new ArgumentException(
                    $"'{placed}' is not a location this host allows. Add it under FileBrowser:Roots "
                    + "in appsettings.json.",
                    nameof(root));
            }

            if (!Directory.Exists(placed))
            {
                throw new ArgumentException($"'{placed}' does not exist.", nameof(root));
            }

            // WRITABLE IS PROBED, NOT INFERRED. Attributes lie about network shares, junctions and
            // permission-inherited folders; the only reliable answer is to try.
            //
            // THE WRITE AND THE DELETE HAVE SEPARATE TRIES, and that is not fastidiousness: sharing
            // one would report a failed CLEAN-UP as "is not writable" - which is the opposite
            // of what just happened - while leaving `.harness-probe-<guid>` behind in somebody's
            // folder, refusing the create, and giving them no reason to go looking for it.
            var probe = Path.Combine(placed, $".harness-probe-{Guid.NewGuid():N}");

            try
            {
                await File.WriteAllTextAsync(probe, "", ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ArgumentException($"'{placed}' is not writable.", nameof(root));
            }

            // Best effort, and it must NOT refuse the create: the folder is writable - that is what
            // the line above just proved - and a leftover probe file is a nuisance where a refusal
            // here would be a team nobody can make.
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // THE GUARD DELETION DEPENDS ON, AND IT IS NOT CONDITIONAL ON A ROOT HAVING BEEN SUPPLIED.
        // Refusing an existing folder is what makes "the platform only ever removes a directory it
        // made" true - the marker is worthless if the platform can adopt somebody else's folder
        // here. `<root>/teams/` itself is created if missing (below, via EnsureSkeletonAsync) - it
        // is the platform's container, not this team's, so an existing one is expected rather than
        // refused.
        //
        // IT IS OUTSIDE THE `if` ABOVE, so a DEFAULT-rooted team is checked too - and that is the
        // reachable case rather than the exotic one. Deleting team `Alpha` can answer 200 with
        // `failures` naming a directory it could not remove, which is the documented usual
        // outcome when a child process has not finished exiting; without this, recreating "Alpha"
        // would adopt the dead team's root WHOLESALE - its documents, its workspaces, its
        // transcripts and its marker - and hand the new manager a stranger's documents as
        // HARNESS_SHARED.
        var container = TeamPaths.ContainerOf(resolvedRoot ?? paths.DataRoot, team);

        if (Directory.Exists(container))
        {
            throw new ArgumentException(
                $"'{container}' already exists. Choose another root, or another team name.",
                nameof(root));
        }

        // THE DOCUMENTS FOLDER IS CLAIMED BEFORE THE ROW IS WRITTEN, and that ordering is the
        // point. Documents outlive their team and team ids are reusable, so this is
        // where a recreated team either gets a folder of its own or refuses - and it must refuse
        // BEFORE anything exists, or the failure leaves a team whose HARNESS_SHARED is a
        // stranger's documents. See TeamDocumentsClaim for the four states and why a collision is
        // retired rather than refused.
        //
        // A collision is REPORTED, which is what makes it not-silent. It is reported
        // through the tenant log and the create's own response rather than through this method's
        // return, which is a TeamSummary the board renders: a retirement is a fact about the
        // CREATE, not a property of the team, and the tenant row is the durable record.
        var documentsClaim = await TeamDocumentsClaim.ForAsync(paths, team, ct);

        if (documentsClaim.Retired is { } retiredDocuments)
        {
            _retiredDocuments[team] = retiredDocuments;
        }

        // THE ROW FIRST, before the three dictionaries below and before AddContainerAsync writes
        // the manager.
        //
        // Before the member because team_members references teams(id), and a member written first
        // has nothing to point at. Before the DICTIONARIES because a failed write would otherwise
        // leave a team the registry holds and the database does not - /api/overview would show it,
        // adding a container to it would work, and the next restart would forget it. Surfaces
        // disagreeing about whether a team exists is strictly
        // worse than the create failing outright. Nothing below needs the row.
        //
        // The name is NULL when the person's name IS the identifier: one representation of "never
        // relabelled", matching the cache below and SetLabelAsync's reset.
        // A TEAM IS BORN WITH TEST CREDENTIALS, so nobody has to remember to give it any.
        //
        // The failure this prevents is not that a credential leaks - it is that one is INVENTED. A
        // test account on a disposable instance is a coordination token rather than a secret:
        // nobody needs to protect it, everybody needs to agree on it. So the platform decides it
        // once, at creation, and every member holds identical values from its first second. There
        // is no window in which a member meets an un-bootstrapped instance and has to make
        // something up.
        //
        // GENERATED HERE AND NEVER AGAIN. RestoreAsync reads the stored row and does not come
        // through this path, which is what it must not do: a password regenerated on every restart
        // would lock the team out of the very instance it bootstrapped before the restart - a
        // failure that would look exactly like the invented-credential one.
        var seededEnv = TeamEnv.SeedFor(team);

        await teams.SaveTeamAsync(
            new PersistedTeam(
                team,
                string.Equals(trimmed, team, StringComparison.OrdinalIgnoreCase) ? null : trimmed,
                AdditionalInstructions: instructions,
                MemberAgent: memberAgentNames[0],
                MemberAgents: memberAgentNames,
                Root: resolvedRoot,
                Repos: validatedRepos,
                Env: seededEnv),
            ct);

        // NOTHING TENANT-WIDE IS WRITTEN BY CREATING A TEAM. Writing the Concierge setting here would
        // change every other team's Concierge as a side effect of a create, with no audit line
        // telling that apart from a deliberate change.

        // REGISTERED BEFORE THE TEAM IS VISIBLE, and that ordering is load-bearing twice over.
        //
        // RootFor throws for an unregistered team, so every path composed below needs this. It has
        // to precede `_teams[team]` as well: `All(withRoot: true)` resolves a root for every team in that dictionary,
        // so a team registered afterwards leaves a window in which a concurrent read by a person
        // of `GET /api/teams` meets a KeyNotFoundException about a team it can see. Registering
        // first closes it; the reverse cannot be closed by making All tolerant, which would answer
        // a null root for a team that has one.
        paths.Register(team, resolvedRoot);

        _teams[team] = [];
        _env[team] = seededEnv;

        // BEFORE the Manager is built below, so its first prompt already carries them.
        if (instructions is not null) _instructions[team] = instructions;

        // Always, because creation requires it - "has not chosen" is a state only a stored row that
        // carries no member Agent can be in.
        _memberAgents[team] = memberAgentNames;
        if (validatedRepos.Count > 0) _repos[team] = validatedRepos;

        // Only when the two differ. A team whose label is already its identifier caches nothing,
        // which is what keeps "never relabelled" a single state - see the cache's own remarks.
        if (!string.Equals(trimmed, team, StringComparison.OrdinalIgnoreCase))
        {
            _labels[team] = trimmed;
        }

        // THROUGH THE NARROW SETTER, even during creation: SaveTeamAsync deliberately does not
        // write this column, so anything that wants it stored must go through its one writer.
        // BEFORE any wake this create may do below, so a team created with a per-workflow budget is
        // born holding it rather than spending its first manager run under the instance figure.
        //
        // `is not null` AND NOT `is > 0`: an explicit 0 is a choice - unlimited - and skipping the
        // write for it would leave the row NULL, which means the OPPOSITE. Absent stays absent, so
        // a team nobody chose a figure for is not written to at all.
        if (budgetTokens is not null) await SetBudgetAsync(team, budgetTokens, ct);

        // Contributor settings. Stored BEFORE the clone below, so the clone is made with its upstream remote.
        foreach (var contributor in contributors)
        {
            var row = contributor with { Team = team };
            await teams.SetRepoContributorAsync(row, ct);
            _contributors[BranchKey(team, row.Repo)] = row;
        }

        // Every folder a team owns AND the marker, made synchronously, because a team whose folders
        // are made later and best-effort is a team that can exist with nothing reporting it has no
        // workspace. ONE call rather than an inline list: restoration builds the same skeleton,
        // and two copies of six strings plus a file format is the shape that drifts.
        await TeamPaths.EnsureSkeletonAsync(paths.RootFor(team), team, ct);

        // No system prompt is passed, and that is not an omission. A manager's prompt is COMPOSED,
        // not supplied: ComposePrompt starts from the built-in Manager prompt and resolves it
        // against the team's LABEL, never its identifier, and its current roster, which at this
        // instant is empty because this is the first container on the team.
        await AddContainerAsync(
            team, DefaultManagerName, agent, "", ManagerSubscriptions(), ManagerPermits, ct: ct);

        if (validatedRepos.Count > 0)
            await WakeManagerForReposAsync(team, validatedRepos, ct, handleRepoSetup);

        return All().Single(t => string.Equals(t.Id, team, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A new team carrying an existing one's configuration.
    ///
    /// IT COMPOSES THE ORDINARY OPERATIONS and invents nothing: create, then env, then a hire per
    /// member. Everything creation can express travels through <see cref="CreateAsync"/>, so the
    /// clone meets every refusal a hand-made team meets - including the allowlist check on the root
    /// it inherits, which is the one moment a placed root is re-examined at all.
    ///
    /// IT DOES NOT ROLL BACK. A half-clone is reported and recoverable; a rollback means running
    /// the most destructive operation in the product unattended, on a team a person can already
    /// see. Deletion makes the same trade when it answers 200 with what it could not remove.
    ///
    /// NO FILES AND NO HISTORY ARE COPIED. The clone gets an empty skeleton and its members floor
    /// at the head of the log like any new container: a clone is a team that starts work, not one
    /// that remembers somebody else's.
    /// </summary>
    public async Task<TeamCloneResult> CloneAsync(
        string source, string name, CancellationToken ct = default)
    {
        var stored = ExistingName(source) ?? throw new InvalidOperationException($"No team '{source}'.");

        // BY IDENTIFIER, never by label. `DefaultManagerName` is a CONTAINER NAME that `IsManager`,
        // `MemberDeletion` and `/api/overview` all key on and nobody can rename; only the LABEL
        // moves. Matching on the label would miss a manager somebody called "Lead" and then hire it
        // a second time in the roster loop below, onto a team that already has one.
        var managerId = ContainerIdsOf(stored)
            .FirstOrDefault(id => string.Equals(id.Name, DefaultManagerName, StringComparison.OrdinalIgnoreCase));
        var manager = managerId is null ? null : await MemberAsync(stored, managerId.Name, ct);

        // A TEAM WITH NO MANAGER CANNOT BE CLONED, AND IT MUST SAY SO IN THOSE WORDS. It is a
        // reachable state, and anything reading a roster has to cope with an empty one.
        //
        // Left to `CreateAsync`, `manager?.Agent ?? ""` would meet its empty-agent guard, which
        // answers "A team needs an Agent for its manager." - a field the CALLER left blank, sending
        // a person to a dialog to choose something on a request that has no such field. The
        // source's missing Manager is what is actually absent.
        //
        // An ArgumentException so the refusal keeps the route arm it already had: that arm turns
        // this into a 400 carrying the sentence, and nothing here is a platform fault.
        if (manager is null)
        {
            throw new ArgumentException(
                $"'{LabelFor(stored)}' has no {DefaultManagerName}, so there is nothing to clone "
                + "its manager from. Every team is created with one.");
        }

        var sourceSummary = All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase));

        var created = await CreateAsync(
            name,
            manager.Agent,

            additionalInstructions: AdditionalInstructionsFor(stored),
            memberAgent: null,
            memberAgents: MemberAgentsFor(stored),

            // THE STORED PARENT, so the clone's folder is a SIBLING of the source's under one root.
            // The source's own team folder would ask the platform to adopt a directory it did not
            // create, which creation refuses.
            root: paths.StoredRootFor(stored),
            repos: ReposFor(stored),

            // THE SOURCE'S OWN CHOICE, NOT THE FIGURE IN FORCE FOR IT. A source that has chosen
            // nothing clones to a team that has chosen nothing, which goes on tracking the
            // instance figure - copying the RESOLVED value would freeze today's instance figure
            // onto a team nobody set one for, and it would stop tracking the moment an operator
            // changed it. An explicit 0 clones as 0, for the same reason.
            budgetTokens: BudgetFor(stored),
            ct: ct);

        var failures = new List<string>();

        // THE SEEDED KEYS STAY THE CLONE'S OWN. TeamEnv.SeedFor mints a per-team admin address and
        // a fresh random password at creation; carrying the source's over would hand two teams one
        // password, under an address naming the wrong team - a credential copied by a feature
        // nobody would describe as copying credentials.
        var carried = EnvFor(stored)
            .Where(pair => pair.Key != TeamEnv.AdminEmail && pair.Key != TeamEnv.AdminPassword)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        // WHAT THE SOURCE ACTUALLY GAVE THE CLONE, which is not the same number as what it was
        // asked to give. `TeamCloneResult` says the counts are what the source handed over, the
        // route puts this number in a `team.cloned` row, and that row is kept forever - so
        // reporting `carried.Count` on a path that reported `env:` as a FAILURE in the same
        // response asserts a transfer that did not happen, permanently, in the one place somebody
        // goes to ask whether a clone came out whole.
        var envCarried = 0;

        if (carried.Count > 0)
        {
            // COLLECTED LIKE A ROSTER FAILURE, and that is the whole contract rather than caution
            // about this one call. EVERY STEP AFTER `CreateAsync` RETURNS RUNS AGAINST A TEAM THAT
            // ALREADY EXISTS, so a throw here becomes a 400 over a team a person can already see -
            // indistinguishable at the route from the 400 that refuses an empty name and creates
            // nothing, and a flat contradiction of "it reports what it could not do rather than
            // rolling back". It is REACHABLE: the seeded pair is deliberately overwritable, so a
            // source holding 64 entries of its own carries 64 and merges to 66 over the clone's own
            // seed, which `TeamEnv.Validate` refuses at the cap.
            try
            {
                // MERGED OVER THE CLONE'S OWN ENVIRONMENT, never handed the carried keys alone.
                // SetEnvAsync REPLACES the whole map, so passing `carried` by itself would delete
                // the credentials `CreateAsync` minted for this team seconds ago - the very pair the
                // exclusion above exists to protect, removed by the line that protects it.
                var next = new Dictionary<string, string>(EnvFor(created.Id), StringComparer.Ordinal);

                foreach (var (key, value) in carried) next[key] = value;

                // A DELTA over each live container, never a rebuild: ForContainerAsync mints, so
                // rebuilding hands every running agent of this team a fresh key. SetEnvAsync is
                // where that rule lives, which is why this goes through it rather than writing the
                // row. It VALIDATES BEFORE IT WRITES, so a refusal here leaves the clone holding
                // its own seed rather than half a map.
                await SetEnvAsync(created.Id, next, ct);

                // AFTER the write, never before it. `SetEnvAsync` validates before it writes, so a
                // refusal leaves the clone holding its own seed and nothing of the source's.
                envCarried = carried.Count;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"env: {exception.Message}");
            }
        }

        var hired = 0;

        foreach (var id in ContainerIdsOf(stored))
        {
            if (string.Equals(id.Name, DefaultManagerName, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var member = await MemberAsync(stored, id.Name, ct);

                // THE LABEL, so the clone's member derives the SAME identifier - `AddContainerAsync`
                // derives from what it is given, and passing the identifier would lose a name a
                // person typed. `member.Label` is null exactly when the two are already equal.
                await HireMemberAsync(
                    created.Id, member.Label ?? member.Name, member.Agent,
                    member.SystemPrompt ?? "", member.Subscribes,
                    hiredFor: member.HiredFor, ct: ct);

                hired += 1;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // REPORTED, NEVER FATAL. A member whose Agent has since left the catalog must not
                // cost the person the whole clone - they can see what is missing and hire it.
                //
                // CANCELLATION IS EXCLUDED and that exclusion is not tidiness: an aborted request
                // would otherwise be recorded as this member's own failure and the loop would go on
                // to try every remaining one against the same dead token, producing a report that
                // names the roster as broken when nothing about it is.
                failures.Add($"{id.Name}: {exception.Message}");
            }
        }

        // THE MANAGER'S LABEL AND ITS OVERRIDE, in one call, and the LABEL is why the condition is
        // an OR rather than the override alone. Every member's label travels through the roster
        // loop's `member.Label ?? member.Name`, and a manager somebody relabelled to "Lead" must not
        // clone back to "Manager". Guarded inside the override's own test, it could not run at all
        // for a manager that was relabelled and never given added instructions, which is the
        // ordinary case.
        //
        // AFTER creation, because CreateAsync deliberately passes no system prompt - a manager's
        // text is COMPOSED, and handing one in would let creation and re-prompting disagree.
        //
        // BOTH ARGUMENTS ARE PASSED RAW, because null is "leave it alone" on each. `SystemPrompt`
        // is null or non-empty and never blank (AddContainerAsync collapses blank to null), so the
        // three-state rule's middle case - blank CLEARS the override - is unreachable from here and
        // the clone cannot be handed an explicit clear it never asked for.
        if (manager.Label is { Length: > 0 } || manager.SystemPrompt is { Length: > 0 })
        {
            // COLLECTED, not thrown, for the reason the env step above is: the team exists by now.
            try
            {
                await UpdateContainerAsync(
                    created.Id, DefaultManagerName,
                    label: manager.Label, systemPrompt: manager.SystemPrompt, ct: ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"{DefaultManagerName}: {exception.Message}");
            }
        }

        return new TeamCloneResult(
            // RE-READ rather than reusing `created`: the roster hires and the manager's override
            // above all changed this team after creation returned, and a summary taken before them
            // would show a clone with no members.
            All().Single(t => string.Equals(t.Id, created.Id, StringComparison.OrdinalIgnoreCase)),
            ReposFor(stored).Count, envCarried, hired, failures);
    }

    public async Task SetReposAsync(string team, IReadOnlyList<string>? repos, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");
        var validated = RepoUrls.Validate(repos);
        var current = _repos.GetValueOrDefault(stored) ?? [];

        if (current.SequenceEqual(validated, StringComparer.Ordinal))
        {
            return;
        }

        await teams.SetReposAsync(stored, validated, ct);
        if (validated.Count == 0) _repos.Remove(stored);
        else _repos[stored] = validated;

        // GH_TOKEN follows the team's remote: gained when a GitHub remote is added, taken away when
        // the last one goes. A delta, as SetEnvAsync's is, because a rebuild would re-mint.
        var gitHubToken = AgentEnvironment.GitHubTokenFor(validated);
        foreach (var id in _teams.GetValueOrDefault(stored) ?? [])
        {
            if (host.Find(id) is not { } container) continue;

            // A plugin member's environment is its runner's, built per run; the team's variables
            // and tokens are an agent's.
            if (MemberRef.IsPlugin(container.Snapshot().Agent)) continue;

            var next = new Dictionary<string, string>(container.Environment, StringComparer.Ordinal);
            if (gitHubToken is null)
            {
                // A team that set GH_TOKEN in its own env keeps it.
                if (!EnvFor(stored).ContainsKey(AgentEnvironment.GitHubVariable)) next.Remove(AgentEnvironment.GitHubVariable);
            }
            else next[AgentEnvironment.GitHubVariable] = gitHubToken;

            container.Reenv(next);
        }

        await WakeManagerForReposAsync(stored, validated, ct);
    }

    /// <summary>
    /// Replaces a team's environment, and reaches the containers already running on it.
    ///
    /// ROW FIRST, CACHE SECOND. The reverse sets the value, works all session, and loses it on the
    /// next restart.
    ///
    /// Then every live container of the team is re-environed, because a rotated credential that
    /// reached nobody until the Host restarted would be "the database says one thing and the
    /// running agent is told another".
    /// </summary>
    public async Task SetEnvAsync(
        string team, IReadOnlyDictionary<string, string>? env, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");
        var validated = TeamEnv.Validate(env);
        var previous = _env.GetValueOrDefault(stored)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        await teams.SetEnvAsync(stored, validated, ct);

        if (validated.Count == 0) _env.Remove(stored);
        else _env[stored] = validated;

        // A DELTA over each container's existing environment, never a rebuild through
        // ForContainerAsync. That method MINTS A CREDENTIAL: calling it again here would hand every
        // running agent a new HARNESS_KEY mid-invocation and write one principal row per edit,
        // so an operator rotating a test password would quietly rotate the platform's own
        // credentials too.
        //
        // Keys the team USED to set and no longer does are removed; everything else the container
        // holds - its credential, its Agent definition's own variables, PATH - is untouched.
        // `validated` cannot carry an HARNESS_ key, because TeamEnv.Validate refuses one.
        foreach (var id in _teams.GetValueOrDefault(stored) ?? [])
        {
            if (host.Find(id) is not { } container) continue;

            // A plugin member's environment is its runner's, built per run; the team's variables
            // and tokens are an agent's.
            if (MemberRef.IsPlugin(container.Snapshot().Agent)) continue;

            var next = new Dictionary<string, string>(container.Environment, StringComparer.Ordinal);

            foreach (var key in previous.Keys)
            {
                if (!validated.ContainsKey(key)) next.Remove(key);
            }

            foreach (var (key, value) in validated) next[key] = value;

            container.Reenv(next);
        }
    }

    /// <summary>
    /// THE CLONE HAPPENS HERE, BEFORE THE MANAGER IS TOLD ANYTHING - it is not the Manager's job.
    ///
    /// The worktrees skill has NO ARM for a clone that does not exist yet: its manager section is
    /// "bring the main clone current, and nothing else", written in `git -C "&lt;path&gt;/main" …`
    /// commands that all presuppose the directory, and its member section says a member does not
    /// clone. Left to the agents, whether a team got a clone would depend on whether one improvised
    /// the command - and when none does, the developer blocks on authority and the Manager blocks
    /// behind it.
    ///
    /// <see cref="RepoClone"/> argues the placement. What matters here is the ORDER: clone first,
    /// then tell the Manager what is true. A wake that arrives before the clone finishes is a
    /// Manager looking at a directory that is halfway through being made.
    /// </summary>
    private async Task WakeManagerForReposAsync(
        string team, IReadOnlyList<string> repos, CancellationToken ct,
        Func<IReadOnlyList<RepoCloneOutcome>, bool>? handleRepoSetup = null)
    {
        var targets = repos
            .Select(url => (
                Url: url,
                Path: Path.Combine(paths.ReposFor(team), RepoUrls.DeriveName(url), "main")))
            .ToList();

        var outcomes = await cloner.EnsureAllAsync(targets, ct);

        // A clone records what origin's HEAD named, known or not.
        foreach (var cloned in outcomes.Where(o => o.Result is RepoCloneResult.Cloned))
        {
            await RecordRemoteDefaultBranchAsync(team, RepoUrls.DeriveName(cloned.Url), cloned.DefaultBranch, ct);

            // A repository whose contributor settings outlived its clone gets its upstream
            // remote and sign-off hook back before any member works in it.
            if (prepareClone is not null)
            {
                await prepareClone(ContributorFor(team, RepoUrls.DeriveName(cloned.Url)), cloned.Path, ct);
            }
        }

        // A create-and-dispatch can own the notification, but only AFTER setup finishes.
        // Returning false leaves the ordinary repo wake intact. The outcomes, rather than
        // rendered text, let that caller distinguish failure without parsing a prompt.
        if (handleRepoSetup?.Invoke(outcomes) == true) return;

        var instruction = RepoSetupMessage.For(outcomes);

        // THE FAILED PATHS RIDE ON THE ROW, so the workflow this roots can be answered when a
        // person makes the clone later. Without them the Manager's block would stay open for good
        // after the clone existed. See RepoReadyNotice.
        var repoNotReady = outcomes
            .Where(o => o.Result is RepoCloneResult.Failed)
            .Select(o => o.Path)
            .ToList();

        await log.AppendAsync(
            new NewMessage(
                MessageTypes.InstructionFor(new ContainerId(team, DefaultManagerName)),
                repoNotReady.Count > 0
                    ? JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        [PayloadFields.Instruction] = instruction,
                        [PayloadFields.RepoNotReady] = repoNotReady,
                    })
                    : JsonSerializer.Serialize(new { instruction }),
                "host"),
            ct);
    }

    /// <summary>
    /// Whether an explicitly typed root IS the instance data root - in which case it is stored as
    /// NULL, so choosing the default location by hand is the same row as leaving the box blank.
    ///
    /// Trailing separators are trimmed on both sides, because `/data` and `/data/` are one
    /// folder and a picker produces either. Ordinal-ignore-case, matching every other path
    /// comparison in this codebase.
    /// </summary>
    private bool IsInstanceRoot(string full) =>
        string.Equals(
            TrimSeparators(full),
            TrimSeparators(Path.GetFullPath(paths.DataRoot)),
            StringComparison.OrdinalIgnoreCase);

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Completions, failures and rejections - from its OWN TEAM - plus its own addressed-instruction
    /// type, which <see cref="ContainerHost.AddAsync"/> appends for every container.
    ///
    /// SAFE ONLY BECAUSE ContainerHost.PumpOnceAsync FILTERS DELIVERY BY THE PUBLISHER'S TEAM, so a
    /// manager hears its own workers and no others. Delete that filter and this list creates a
    /// runaway - the list is what makes it reachable.
    ///
    /// The runaway, kept because it is the argument against ever subscribing unscoped: these are
    /// GLOBAL types, and PumpOnceAsync suppresses only a container's OWN publications, so with two
    /// teams the two managers would wake each other without end: Alpha/Manager publishes
    /// `container.completed`, Beta/Manager is subscribed and the source differs so it wakes, it
    /// publishes its own `container.completed`, which wakes Alpha/Manager. `Ceiling` bounds QUEUE
    /// depth rather than causation depth, so it would only convert the runaway into
    /// `container.rejected`, which is also subscribed.
    ///
    /// Without these subscriptions a manager could dispatch and hear nothing back - dispatch
    /// working while the return leg is silent. So the wake is scoped by the PUBLISHER'S TEAM, in
    /// the delivery pump, rather than the list being emptied.
    ///
    /// The intra-team loop is bounded on hops by CausationDepthLimit (checked at tell route) and
    /// on cost by the workflow spend limit (also at tell), both refused with 409. Both bounds are
    /// evadable by an agent that omits its causation, which starts a fresh chain at depth 0 and
    /// spend 0.
    /// </summary>
    /// <remarks>
    /// THE THREE KANBAN TYPES ARE A HUMAN SPEAKING, and they are here because a card edit that
    /// wakes nobody is a board that reads like a conversation and is a monologue. A person changing
    /// a card's title, moving it, or commenting on it is telling the team something; the Manager is
    /// woken and decides what to do about it from its `kanban` skill.
    ///
    /// Safe alongside the global types above for the same reason and by the same mechanism: the
    /// delivery pump scopes every wake to the publisher's team. `MessageTeam.Of` recovers that team
    /// from the card event's PAYLOAD, because its source is the acting person's user id and carries
    /// no team at all - the second half of this, and the half that fails silently on its own.
    ///
    /// A Manager can publish these itself (the `kanban` tool), and does not wake on its
    /// own: the pump's first guard suppresses a container's own publications. That is what stops it
    /// commenting itself awake in a loop, and `ManagerSubscriptionTests` pins it.
    ///
    /// CHANGING THIS LIST REACHES NEW TEAMS ONLY. A restored container's subscriptions are written
    /// from `team_members.subscribes`, the row it was CREATED with, so every Manager that already
    /// exists needs its rows changed by a new schema step.
    /// </remarks>
    private static string[] ManagerSubscriptions() =>
    [
        MessageTypes.Completed,
        MessageTypes.Failed,
        MessageTypes.Rejected,

        // THE HAND-BACK, AND THE ONLY AGENT-PUBLISHED TYPE A MANAGER HOLDS THAT IS NOT A RUN
        // OUTCOME. `progress` and `blocked` are deliberately unsubscribed - a manager holding
        // either is woken by every status line or every blockage its workers report - and this is
        // neither: it is one row per finished card, published once, and being woken by it IS the
        // point. Without it, members reach for `workflow-complete` because nothing else would wake
        // the manager on the correlation they are finishing.
        //
        // Safe alongside the global types above by the same mechanism and no new one: the delivery
        // pump scopes every wake to the publisher's team, and suppresses a container's own
        // publications - so a Manager that hands back does not wake itself, which is the loop the
        // guard exists for. `MessageTeam.Of` needs no new arm for it - and NOT because of the
        // namespace, which that function does not key on anywhere. A hand-back's team comes from
        // its SOURCE, published as the qualified container id, through the same fall-through
        // `blocked` and `progress` already reach. See `MessageTypes.Handback` for its spelling.
        MessageTypes.Handback,

        MessageTypes.KanbanCardMoved,
        MessageTypes.KanbanCardEdited,
        MessageTypes.KanbanCardCommented,
    ];

    /// <summary>
    /// <see cref="ManagerSubscriptions"/>, exposed ONLY so a test can assert the seeded Manager
    /// never holds a <see cref="EventCatalog.HighVolumeTypes"/> entry - the one subscription set
    /// this platform writes for itself, and the one the firehose rule would be most embarrassing to
    /// break. Forwards to the private list rather than the test holding a second copy of it: two
    /// stores of one fact is how they drift.
    /// </summary>
    public static IReadOnlyList<string> ManagerSubscriptionsForTests => ManagerSubscriptions();

    /// <summary>
    /// Why a subscription to a high-volume type was refused, NAMING THE RECOVERY rather than only
    /// the problem: a caller told only "invalid" retries the same spelling.
    ///
    /// Shared rather than composed at each call site, because this is one of four places that must
    /// agree - this one and the three write paths (the repoint paths and
    /// `PUT /api/agents`) - and two copies of one sentence is how they drift apart.
    /// </summary>
    public static string FirehoseRefusal(string type, string agent) =>
        $"'{type}' is high-volume - it is published once per status line, so a member holding "
        + $"it is woken by every one. The Agent '{agent}' is a language model, and only a preset "
        + "declaring `languageModel: false` may subscribe to a high-volume type.";

    /// <summary>
    /// THE SHARED HIRE SEAM for the member route and the clone path.
    ///
    /// `POST /api/teams/{team}/containers` carries route-only refusals - empty names, unknown
    /// teams, a manager naming an out-of-allowlist Agent - and then both it and `CloneAsync` reach
    /// the same lower-level member-addition guards here. Keeping that handoff named gives the test
    /// suite one symbol to pin, so a future hire refusal with clone parity consequences has a
    /// natural home and one missing call site is visible in the build.
    /// </summary>
    public Task<ContainerSnapshot> HireMemberAsync(
        string team, string label, string agent, string systemPrompt, IReadOnlyCollection<string> subscribes,
        string? hiredFor = null,
        CancellationToken ct = default,
        PluginMemberSettings? settings = null) =>
        AddContainerAsync(
            team, label, agent, systemPrompt, subscribes, hiredFor: hiredFor, ct: ct, settings: settings);

    /// <summary>
    /// Adds a container called <paramref name="label"/>, deriving its identifier the same way a
    /// team's is derived from the name someone typed - see <see cref="CreateAsync"/>. A container
    /// name is under the same allowlist as a team's and for the same reasons, so "Data Ingest" is a
    /// label and `DataIngest` is what the path and the message type carry.
    /// </summary>
    /// <param name="permits">What this container's agent may cause. Null is the DEFAULT and means
    /// none, which is a worker - so a container added without anyone thinking about permits is the
    /// harmless one rather than the powerful one.</param>
    public async Task<ContainerSnapshot> AddContainerAsync(
        string team, string label, string agent, string systemPrompt, IReadOnlyCollection<string> subscribes,
        IReadOnlySet<string>? permits = null,
        string? hiredFor = null,
        CancellationToken ct = default,
        PluginMemberSettings? settings = null)
    {
        if (!_teams.TryGetValue(team, out var members)) throw new InvalidOperationException($"No team '{team}'.");

        // FIRST, before anything is created. A refusal that has already made a directory, a
        // container or a row is a refusal that changed something, which is the failure CreateAsync's
        // ordering avoids too.
        // WHAT IT RUNS: an Agent preset, or `plugin:<id>` - see MemberRef.
        var isPlugin = MemberRef.IsPlugin(agent, out var pluginId);

        if (isPlugin && PluginRefusal(agent, pluginId!) is { } refusal)
        {
            throw NoSuchAgentException.ForPlugin(agent, refusal);
        }

        // CONFIGURATION IS A PLUGIN'S, checked against its manifest HERE, before anything is made,
        // so a person hears a missing field or an unset secret at hire rather than at the first run.
        if (!isPlugin && settings is { } agentSettings
            && (agentSettings.Config.Count > 0 || agentSettings.Secrets.Count > 0))
        {
            throw new PluginSettingsException("`config` and `secrets` are for plugin members; an Agent takes its settings from its preset.");
        }

        if (isPlugin && plugins?.For(pluginId!) is { } installed
            && PluginMemberRunner.SettingsRefusal(installed.Manifest, settings ?? PluginMemberSettings.None, secrets) is { } settingsRefusal)
        {
            throw new PluginSettingsException(settingsRefusal);
        }

        var command = isPlugin
            ? null
            : agents.For(agent)
                ?? throw new NoSuchAgentException(
                    agent, [.. agents.Definitions.Where(d => d.Mode == AgentMode.Headless).Select(d => d.Name)]);

        // A LANGUAGE MODEL MAY NOT SUBSCRIBE TO A FIREHOSE.
        //
        // The rule is about COST, not about the type being unfit to react to - which is why the
        // catalog flag is HighVolume rather than `Subscribable`. A procedural container over
        // `container.progress` is an observability sink and is allowed; only a language model is
        // refused, because that is the one that is woken by every status line every worker writes.
        //
        // Checked BEFORE any row is written, and this is one of four sites that must agree - the
        // others are the repoint paths and `PUT /api/agents`. A check in one is a check the next
        // one forgets.
        //
        // `command is { LanguageModel: true }` is deliberate: `agents.For(agent)` already refused a
        // null above, so `command` cannot be null here, and this must not also fire on that arm and
        // mask it with a different sentence.
        if (command is { LanguageModel: true })
        {
            foreach (var type in subscribes)
            {
                if (EventCatalog.HighVolumeTypes.Contains(type))
                {
                    throw new FirehoseSubscriptionException(type, agent);
                }
            }
        }

        var trimmed = label.Trim();

        // Built from the STORED spelling of the team, never the caller's and never by re-scanning
        // the keys. That is what keeps the workspace directory and the message type identical no matter how the team was capitalised in the request.
        var stored = ExistingName(team)!;

        if (members.Any(m => string.Equals(LabelForContainer(m), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new TeamNameTakenException(trimmed);
        }

        var name = ContainerId.DeriveName(trimmed) ?? NextContainerName(members, stored);
        var id = new ContainerId(stored, name);

        // ContainerId equality is case-insensitive, so this refuses `manager` alongside `Manager`.
        // An ordinal name check against a case-insensitive ContainerHost would let one container
        // appear twice in a team's list.
        if (members.Contains(id)) throw new TeamNameTakenException(LabelForContainer(id));


        // Under this team's own `teams/<Team>/workspaces/<Name>` - never a bare `teams/<Id>/<Name>`,
        // whose GRANDPARENT holds every OTHER team, so `../..` would be the whole tenant - from an
        // agent that runs with permissions bypassed. One level up is this team's OTHER containers,
        // never another team's anything.
        //
        // This is LAYOUT, not enforcement. Nothing refuses a climb; there is simply nothing worth
        // reaching above a container now. Real enforcement needs a process boundary.
        //
        // Restoration recreates a missing workspace on start - only when missing, and never
        // deleting: an existing one holds whatever a previous run's agent wrote.
        var workspace = paths.WorkspaceFor(id);

        // NAMED rather than raw. A placed team's root can be a volume or a share that is not there
        // - the condition restoration warns about - and this line is where
        // that arrives when somebody hires into such a team: an IOException naming a workspace path
        // nobody chose, as a bare 500, for a team whose own manager explains the same fault in one
        // sentence on its card.
        try
        {
            Directory.CreateDirectory(workspace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TeamRootUnreachableException(
                paths.RootFor(stored), "this member was not added", exception);
        }

        // Progress ALWAYS, whatever the caller asked for - not a default an explicit set
        // replaces. A caller naming permits has decided about dispatch and hiring; it has not
        // decided that this member should be invisible on the board, and a member that cannot
        // say what it is doing fails silently rather than loudly.
        //
        // This is the exception to "null permits is the default and means none". See
        // MemberProgressPermitTests for why it is affordable:
        // PermitGate makes Progress reach exactly one route out of forty.
        // A PLUGIN MEMBER HOLDS NO PERMITS AND NO CREDENTIAL. The three forced below exist so an
        // AGENT can call its MCP tools; a plugin reports through its own stdout, which the runtime
        // reads, so it is minted nothing and given no HARNESS_* environment at all.
        var memberPermits = isPlugin ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(
            permits ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal)
        {
            Permits.Progress,
            Permits.Skills,

            // READ ALWAYS, ON THE SAME TERMS - without it a member could WRITE the board it could
            // not READ: it could comment on cards and move them while the `status` tool answered
            // "This credential may not Read.", so a member whose role skill tells it to read team
            // state would be told to do something the platform refused, and give up.
            //
            // WHAT MAKES IT AFFORDABLE IS NOT THE PERMIT, IT IS `TeamAccess`. Read gates twenty-two
            // routes: the ones declaring `{team}` are bounded structurally by `TeamGate`, and the
            // eight that do not each resolve effective teams, which for a Container principal is its
            // own team column alone. So this widens what a member can see about ITS OWN team and
            // nothing else - another team's workflow answers `200 []`.
            //
            // This reaches members created from here on; the stored `permits` column is what
            // `RestoreAsync` reads for everyone who already exists.
            Permits.Read,
        };

        // Built before ComposePrompt for the same reason RestoreAsync's local is: C# evaluates
        // constructor arguments left to right, and composing the prompt first would hand it an
        // empty dictionary rather than the credential this container is actually given. Reused
        // below rather than minted twice.
        //
        // Filling the environment is what gives a container its own credential, and so what lets a
        // manager call the platform at all - which is also why a manager's subscriptions must be
        // team-scoped. See ManagerSubscriptions below.
        var containerEnvironment = isPlugin
            ? PluginEnvironment
            : await environment.ForContainerAsync(
            id, agent, memberPermits, EnvFor(id.Team), ReposFor(id.Team), ct);

        // The member's own words - the role line it was hired with. Blank is stored as NOTHING
        // ADDED; they are appended after the built-in role prompt, never in place of it.
        var override_ = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim();

        // The COLLAPSED override, not the caller's text. Composing from what was sent while storing
        // null would make creation and the very next re-prompt produce two different texts, which is
        // the one property the single composition site exists to guarantee.
        var composed = ComposePrompt(id, agent, trimmed, override_, containerEnvironment);

        // THE CONTAINER FIRST here, unlike CreateAsync's team row. Not an oversight: the floor
        // below is read back from the container's own snapshot after host.AddAsync has assigned it,
        // so there is no row to write until the container exists. The consequence is the one
        // CreateAsync's ordering exists to avoid - a failed SaveMemberAsync below leaves a container
        // live in the host and in `members`, absent from the database: the route answers 500, and a
        // retry meets a 409 from TeamNameTakenException because the label is already taken in
        // memory. Accepted here because the read-back makes the reverse order impossible, not
        // because the asymmetry is free.
        var container = await host.AddAsync(
            new ContainerDefinition(
                id, agent, composed, ResolveMemberWorkingDirectory(id, workspace), subscribes,
                containerEnvironment,
                // Null when the label IS the identifier, so a container nobody gave a separate name
                // carries no second copy of its own - the same rule teams follow.
                Label: string.Equals(trimmed, name, StringComparison.Ordinal) ? null : trimmed,
                Permits: memberPermits,
                HiredFor: string.IsNullOrWhiteSpace(hiredFor) ? null : hiredFor.Trim()),
            runner,
            ct);

        members.Add(id);

        await teams.SaveMemberAsync(
            new PersistedMember(
                id.Team, id.Name,

                // Null when the label IS the identifier, matching the definition above.
                string.Equals(trimmed, name, StringComparison.Ordinal) ? null : trimmed,
                agent,

                // The member's own words, ADDED after its role prompt - never the composed text.
                override_,
                subscribes,
                memberPermits,

                // The floor the host actually gave it, read back rather than recomputed: computing
                // it twice is how the two disagree.
                container.Snapshot().SinceSeq,
                string.IsNullOrWhiteSpace(hiredFor) ? null : hiredFor.Trim()),
            ct);

        // AFTER SaveMemberAsync, not before: RecomputeAsync reads the member back from `teams`, and
        // a call made any earlier would find no row and compute an empty base set. This member has
        // no triggers of its own yet - it cannot, it did not exist a moment ago - but this is still
        // the one and only write of its subscriptions row; ContainerHost does not write one.
        if (isPlugin && pluginSettings is not null)
        {
            await pluginSettings.SaveAsync(id, settings ?? PluginMemberSettings.None, ct);
        }

        await effective.RecomputeAsync(id, ct);

        // AFTER the list is updated, so the roster it builds includes the member just added. Skipped
        // for the manager itself, which is created by CreateAsync with an empty roster and would
        // otherwise be re-prompted with the same text it was just given.
        if (!IsManager(id))
        {
            await RepromptManagerAsync(id.Team, ct);
        }

        return container.Snapshot();
    }

    /// <summary>
    /// A member's process always starts in its own workspace. With a tree per card there is
    /// no single tree to start in, and the one for THIS instruction is handed over per invocation as `HARNESS_WORKTREE` instead.
    /// </summary>
    private static string ResolveMemberWorkingDirectory(ContainerId id, string workspace) => workspace;

    /// <summary>
    /// This member's tree for one card in every repository of its team, first repository first -
    /// the paths `HARNESS_WORKTREE` and the instruction text name. Read live, so a repository added
    /// to the team after the member was created is included on its next wake. Empty for a team with
    /// no repositories.
    /// </summary>
    public IReadOnlyList<RepoWorktree> WorktreesFor(ContainerId id, string key)
    {
        if (_repos.GetValueOrDefault(id.Team) is not { Count: > 0 } repos) return [];

        // A team whose root is not registered (mid-deletion) has no tree to name; the run goes on
        // without the variables rather than failing on a path it would not use.
        try { _ = paths.ReposFor(id.Team); }
        catch (KeyNotFoundException) { return []; }

        return repos
            .Select(RepoUrls.DeriveName)
            .Select(repo => new RepoWorktree(
                repo,
                Path.Combine(paths.ReposFor(id.Team), repo, "main"),
                paths.WorktreeFor(id, repo, key)))
            .ToList();
    }

    /// <summary>
    /// The stored row behind a member - PersistedMember, not ContainerSnapshot. Its `SystemPrompt`
    /// is the OVERRIDE as written (or null, when this member tracks its preset's current default),
    /// never the composed text `ComposePrompt` hands the agent - that text is not exposed by any
    /// route, deliberately, for the reasons the GET route's own comment gives.
    ///
    /// Looks a member up the same way `UpdateContainerAsync` does, immediately below - resolve the
    /// team's stored spelling first, confirm the container actually exists (a member can be
    /// PERSISTED without a live container only mid-restore, which this treats as "not there yet"
    /// rather than surfacing a row nothing can act on), then find its row.
    /// </summary>
    public async Task<PersistedMember> MemberAsync(
        string team, string name, CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");

        if (host.Find(new ContainerId(stored, name)) is null)
        {
            throw new InvalidOperationException($"No member '{name}'.");
        }

        return (await teams.MembersAsync(ct)).Single(
            m => string.Equals(m.Team, stored, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Changes what a member is CALLED, what it is TOLD, and what it RUNS.
    ///
    /// Repointing is ONE write - the stored row - because nothing caches a container's resolved
    /// command: `ProcessAgentRunner` looks the preset up by name on every invocation. A second
    /// store of that fact is what would make repointing dangerous.
    ///
    /// The new preset must be HEADLESS. `AgentCatalog.For` returns null for a missing preset and for
    /// an interactive one alike, so both are refused by the same lookup.
    /// </summary>
    public async Task<ContainerSnapshot> UpdateContainerAsync(
        string team, string name, string? label, string? systemPrompt, string? agent = null,
        CancellationToken ct = default)
    {
        var stored = ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");
        var id = new ContainerId(stored, name);
        var container = host.Find(id) ?? throw new InvalidOperationException($"No member '{name}'.");
        var member = (await teams.MembersAsync(ct)).Single(
            m => string.Equals(m.Team, stored, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        // A repoint, when one was asked for. Absent and blank both mean "leave it alone", the same
        // two-state rule the label follows - there is no such thing as clearing a member's Agent.
        //
        // Refused rather than accepted-and-broken when the preset is missing or INTERACTIVE: a
        // member is always headless, and `AgentCatalog.For` returns null for both cases, which is
        // why one lookup covers them and the refusal has to say which it was.
        var repointed = !string.IsNullOrWhiteSpace(agent)
            && !string.Equals(agent.Trim(), member.Agent, StringComparison.OrdinalIgnoreCase);

        var newAgent = repointed ? agent!.Trim() : member.Agent;

        // NOT ACROSS KINDS. An agent member holds a credential, permits and an environment minted
        // for an agent, and a plugin member holds none; repointing one into the other would keep
        // the wrong set. Hiring a new member is the honest way to change what kind it is.
        if (repointed && MemberRef.IsPlugin(newAgent) != MemberRef.IsPlugin(member.Agent))
        {
            throw NoSuchAgentException.ForPlugin(
                newAgent,
                $"'{member.Label ?? member.Name}' cannot be repointed between an Agent and a plugin. "
                + "Hire a new member for the other kind instead.");
        }

        if (repointed && MemberRef.IsPlugin(newAgent, out var repointedPlugin)
            && PluginRefusal(newAgent, repointedPlugin) is { } pluginRefusal)
        {
            throw NoSuchAgentException.ForPlugin(newAgent, pluginRefusal);
        }

        // Resolved only to REFUSE, never to store: nothing caches a container's command,
        // so a repoint is one write - the row - and the runner picks the new preset up by name on
        // the next wake. Held rather than discarded: the firehose check right below reuses this
        // same lookup instead of calling `agents.For` a second time.
        var target = MemberRef.IsPlugin(newAgent) ? null : agents.For(newAgent);

        if (repointed && target is null && !MemberRef.IsPlugin(newAgent))
        {
            throw new NoSuchAgentException(
                newAgent,
                [.. agents.Definitions.Where(d => d.Mode == AgentMode.Headless).Select(d => d.Name)]);
        }

        // A REPOINT CAN MAKE A LEGAL PAIR ILLEGAL, which `AddContainerAsync`'s check cannot see: the
        // subscription was written when the member ran a program, and nothing looks at it again.
        // One of four sites that must agree - see `EventCatalog.HighVolumeTypes` and
        // `TeamRegistry.FirehoseRefusal`.
        //
        // Gated on `repointed` DELIBERATELY: the stored pair is not guaranteed to have been legal
        // when written. `FirehoseHolders` exists precisely because a stored row - one whose preset
        // was later flipped back to `languageModel: true`, say - can already hold an illegal pair. An unconditional
        // check reads `target` as the member's OWN CURRENT preset when nothing was repointed and
        // refuses a label- or prompt-only edit on exactly that stored data - the one direction this
        // route must not fail in, since there is no route that lets the caller fix the subscription
        // instead. Only a REPOINT can make a legal pair illegal, so only a repoint is checked here.
        //
        // READS EFFECTIVE SUBSCRIPTIONS, NOT ONLY `member.Subscribes`. An event trigger contributes
        // a subscription of its own - see `EffectiveSubscriptions` - so a member holding
        // `container.progress` only through a trigger, never in the base set, would be repointable
        // onto a language model with nothing refusing it if this loop read `member.Subscribes`
        // alone, because it could not see what the trigger adds.
        if (repointed && target is { LanguageModel: true })
        {
            var holds = await effective.EffectiveTypesAsync(id, member.Subscribes, ct);

            foreach (var type in holds)
            {
                if (EventCatalog.HighVolumeTypes.Contains(type))
                {
                    throw new FirehoseSubscriptionException(type, newAgent);
                }
            }
        }


        // ONE state, not the three `systemPrompt` carries below: absent and blank BOTH leave the
        // label exactly as it is, which is what the route's own field description says ("Omit or
        // send blank to leave it unchanged"). There is deliberately no way to clear a label here -
        // a member always has one, and the identifier is not something a person is shown.
        //
        // Blank does NOT reset to the identifier; nothing below does that.
        var requestedLabel = string.IsNullOrWhiteSpace(label) ? member.Label : label.Trim();
        var trimmedLabel = string.Equals(requestedLabel, container.Id.Name, StringComparison.Ordinal)
            ? null
            : requestedLabel;

        // `systemPrompt` carries THREE states a client can send, and they are not the two `label`
        // carries just above - collapsing them would let a label-only PATCH (where `systemPrompt` is
        // simply absent from the JSON body and deserialises to null) fall through IsNullOrWhiteSpace
        // exactly like an explicit clear and silently reset a written-by-hand override to the preset
        // default.
        //   - null      -> the field was not sent at all; leave the stored override exactly as it is.
        //   - "" / blank -> an explicit clear; reset to the preset's default (NULL).
        //   - anything else -> store it, unless it is Ordinal-equal to the preset's current default,
        //     in which case it still collapses to NULL so an untouched, prefilled editor keeps
        //     tracking the preset rather than freezing a copy of it.
        string? override_;

        if (systemPrompt is null)
        {
            override_ = member.SystemPrompt;
        }
        else
        {
            // Blank CLEARS what was added; anything else is stored exactly as written. There is no
            // comparison against the System Prompt: added words start empty, so matching text is
            // somebody choosing to say it twice and discarding it would be the surprise.
            override_ = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt.Trim();
        }


        await teams.SaveMemberAsync(
            member with
            {
                Label = trimmedLabel,
                SystemPrompt = override_,
                Agent = newAgent,
            },
            ct);

        // Read back from the LIVE container's own Environment, never rebuilt and never an empty
        // dictionary - the same reason RepromptManager reads it rather than passing {}. An empty
        // dictionary would resolve `{env:...}` once, here, and leave it literal on every later wake.
        // `promptName`, NEVER `member.Prompt`. `member` is the row as it was read BEFORE this edit,
        // so composing from it would write the new Prompt to the database and go on telling the live
        // agent the previous one - two stores of one fact, drifting, until the next restart happened
        // to reconcile them.
        container.Reprompt(
            ComposePrompt(
                id, newAgent, trimmedLabel ?? container.Id.Name, override_,
                container.Environment),
            trimmedLabel,
            repointed ? newAgent : null);

        return container.Snapshot();
    }

    /// <summary>
    /// The system prompt a container's agent is actually handed. THE one composition site, and the
    /// only one: creation, re-prompting and restoration all come through here, so the three cannot
    /// produce three different texts.
    ///
    /// Built from the ROLE's built-in prompt (<see cref="BuiltInPrompts"/>), then the member's own
    /// words, then the team's additional instructions, then the skills the role is offered.
    /// </summary>
    /// <param name="id">The container's identity. Its Team is resolved to a label here rather than
    /// by the caller, so no caller can pass the wrong one of a team's two names.</param>
    /// <param name="label">What this member is called - the name a person typed, not its
    /// identifier. Resolves `{member}`.</param>
    /// <param name="systemPrompt">The member's own words - the role line it was hired with - or
    /// null/blank for none.</param>
    /// <param name="environment">What `{env:NAME}` resolves against - the composed environment for
    /// THIS container, never an empty dictionary. An empty one would resolve `{env:...}` once, at
    /// creation, and leave it literal on every later re-prompt.</param>
    private string ComposePrompt(
        ContainerId id, string agent, string label, string? systemPrompt,
        IReadOnlyDictionary<string, string> environment)
    {
        // A PLUGIN MEMBER HAS NO SYSTEM PROMPT. A role prompt is instructions to a language model;
        // a plugin is an executable with its own protocol, and handing it one would be the
        // registry deciding every member is an agent. Its standing instructions are empty.
        if (MemberRef.IsPlugin(agent)) return string.Empty;

        // CHOSEN BY ROLE, never by a person or by the Agent it runs: the team's Manager gets
        // the Manager prompt and everyone else the Member prompt. The member's own words, the
        // team's additional instructions and the skills its role is offered are ADDED after it.
        var role = IsManager(id) ? SkillRoles.Manager : SkillRoles.Member;

        var template = BuiltInPrompts.Compose(
            role, systemPrompt, AdditionalInstructionsFor(id.Team), _skills.For(role));

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["team"] = LabelFor(id.Team),
            ["teamId"] = id.Team,
            ["member"] = label,

            // Roster(), not a bare join of the names: the manager prompt's text sits around a bare
            // `{members}` token and relies on this exact rendering, "no members yet" sentence
            // included, or a manager's prompt silently changes shape.
            ["members"] = Roster(MembersFor(id.Team)),
        };

        // Where the TEAM's work goes, read back from the environment this container was actually
        // GIVEN rather than composed a second time from dataRoot. The environment is built before
        // this method is called - see the comment at both call sites, which explains that C#
        // evaluates constructor arguments left to right - so the value is already decided, and
        // reading it here keeps TeamDocuments the single place that knows where a team's documents
        // live. Two stores of one path drift.
        //
        // Added CONDITIONALLY, so a container that was not given the variable leaves `{shared}`
        // VERBATIM under the unknown-token rule rather than resolving it to an empty string. That
        // is the whole reason the variable can sit below the no-permits return: a prompt reading
        // "put it in " is a silent wrong answer, and one reading "put it in {shared}" is an agent
        // that says something is wrong.
        if (environment.TryGetValue(AgentEnvironment.SharedVariable, out var shared))
        {
            values["shared"] = shared;
        }

        return PromptTokens.Resolve(template, values, environment);
    }

    /// <summary>A plugin member's definition environment: empty. See AddContainerAsync.</summary>
    private static readonly IReadOnlyDictionary<string, string> PluginEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Why <paramref name="reference"/> cannot be hired, or null when it can. A malformed id is
    /// refused here; whether the plugin is installed is asked of the plugin catalog when one is
    /// wired.
    /// </summary>
    private string? PluginRefusal(string reference, string pluginId) =>
        !MemberRef.IsValidPluginId(pluginId)
            ? $"'{reference}' is not a plugin id: use lowercase letters, digits and hyphens, as in 'plugin:sample-echo'."
            : plugins?.RefusalFor(pluginId);

    /// <summary>Whether this id is its team's manager. Case-insensitive, matching ContainerId's own
    /// equality - `manager` and `Manager` are one container.</summary>
    private static bool IsManager(ContainerId id) =>
        string.Equals(id.Name, DefaultManagerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A container's label, or its name when it has none. Read from the live container
    /// rather than a second map, so there is one answer and it is the one the card shows.</summary>
    private string LabelForContainer(ContainerId id) => host.Find(id)?.Snapshot().Name ?? id.Name;

    /// <summary>The fallback identifier for a container whose label derives to nothing, mirroring
    /// <see cref="IdentifierFor"/>'s. Scoped to the team, because a container name only has to be
    /// unique within one.</summary>
    private string NextContainerName(List<ContainerId> members, string team)
    {
        for (var n = 1; ; n++)
        {
            var generated = $"Member{n}";

            if (!members.Contains(new ContainerId(team, generated))) return generated;
        }
    }

    /// <summary>
    /// The members a manager may dispatch to, named the way the tools need them.
    ///
    /// IDENTIFIERS, not labels, and this is the one place that distinction inverts. A person is never
    /// shown an identifier - but the `tell` tool takes a route value, so a manager handed only
    /// "Data Ingest" would address a container that does not exist. The label rides along in
    /// parentheses when the two differ, so the manager can still call it what its human calls it
    /// when reporting back.
    /// </summary>
    private static string Roster(IReadOnlyList<string> members) =>
        members.Count == 0
            // THREE PATHS, AND THE THIRD IS NAMED SO IT CAN BE REFUSED. The last sentence is a
            // 404-guard: it stops a manager inventing a member and addressing a `tell` to nobody.
            // Alone it does not say what to do INSTEAD, and a manager with an empty roster then
            // takes the only path its other instructions offer and writes the product code
            // itself. A manager that has not hired has
            // exactly two moves - hire, or say it cannot - and implementing is the one that has to
            // be named to be refused, because an inherited skill will otherwise name it first.
            //
            // The hire is spelled with `--for <tag>` and NOT `--agent`, deliberately: the team's
            // allowlist decides what a hire runs, and a prompt that named an Agent would both be a
            // preset name in code and a spend decision made on the owner's behalf.
            ? "This team has no members yet. Hire one with the `member` tool (a name and a work "
              + "tag) and dispatch the work to it - you do not do the work yourself, whatever "
              + "else you have been told. If you cannot hire, say so rather than pretending it was "
              + "done."
            : $"Your members: {string.Join(", ", members)}.";

    /// <summary>Everything the UI needs in one shot, so a page load is a single request.</summary>
    public object Overview() => new { teams = All(), managerName = DefaultManagerName };

    public static string Describe(object value) => JsonSerializer.Serialize(value);
}
