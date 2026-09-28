namespace Harness.Contracts;

/// <summary>A durable team. <paramref name="Name"/> is NULLABLE and NULL means NEVER RELABELLED - the
/// name is the identifier until someone changes it, so renaming back to the identifier removes the
/// row's name (writes NULL) rather than storing a copy of the key. Two representations of "not
/// relabelled" is how a default drifts.</summary>
public sealed record PersistedTeam(
    string Id,
    string? Name,
    /// <summary>Scalar shape, still present on some rows. Writes use <see cref="MemberAgents"/>
    /// below and reads should ignore this column.</summary>
    string? MemberAgent = null,

    /// <summary>
    /// Which Agents this team's NEW members may run, in priority order.
    ///
    /// NULL means nobody has chosen. An empty list is explicit and still refuses a hire, but is a
    /// different state from null: one is "explicitly empty", the other is "never chosen".
    /// </summary>
    IReadOnlyList<string>? MemberAgents = null,

    /// <summary>The person's additional instructions for this team, appended after the
    /// built-in role prompt for its Manager and members under their own heading. NULL or blank
    /// means nothing is appended; it never replaces the role prompt.</summary>
    string? AdditionalInstructions = null,

    /// <summary>Where this team's files live — the folder that CONTAINS `teams/&lt;Id&gt;/`, not the
    /// team folder itself. NULL means the instance root, so a team created in the default place
    /// carries no copy of a path that is resolved at runtime.
    ///
    /// WRITE-ONCE. Moving an existing team's root is refused because it means relocating
    /// the whole tree AND repairing git worktrees, so there is deliberately no setter beside
    /// <see cref="ITeamStore.SetNameAsync"/>.</summary>
    string? Root = null,


    /// <summary>The ordered repository URLs this team owns. An absent database value and an empty
    /// array are both represented as an empty list.</summary>
    IReadOnlyList<string>? Repos = null,

    /// <summary>Named values every member of this team is launched with - the team's shared test
    /// credentials and anything else the whole team must agree on. An absent database value and an
    /// empty object are both represented as an empty dictionary.
    ///
    /// COORDINATION, NOT CONFINEMENT. These sit in SQLite in plaintext and every agent runs with
    /// permissions bypassed, so this decides which variable is in a child's environment - never
    /// which secrets it could obtain. Only values every team admin is already entitled to.</summary>
    IReadOnlyDictionary<string, string>? Env = null,

    /// <summary>Whether this team is paused - no new work is delivered until someone resumes it.
    /// Optional because this is a POSITIONAL record: a field inserted in the middle shifts every
    /// one after it.</summary>
    bool Paused = false,

    /// <summary>
    /// What this team may spend on ONE WORKFLOW, in tokens, IN and OUT together.
    ///
    /// <para>
    /// <b>THREE STATES, AND ONLY ONE FUNCTION IS ALLOWED TO KNOW IT.</b> NULL means the team has
    /// CHOSEN NOTHING and inherits the instance `WorkflowSpendLimit`; 0 means the team explicitly
    /// chose UNLIMITED; a positive value is the figure a person typed. Every consumer asks
    /// `TeamRegistry.EffectiveWorkflowBudgetFor` instead, which collapses the three into one
    /// `long?` where null means unlimited and 0 never appears.
    /// </para>
    ///
    /// <para>
    /// <b>STORE WHAT WAS TYPED.</b> Collapsing 0 to NULL at the write would be wrong, because null
    /// and 0 mean different things: under the one-of rule that collapse turns a person's "unlimited" into the instance
    /// figure, which is the opposite of what they asked for.
    /// </para>
    ///
    /// <para>
    /// IT BOUNDS ONE WORKFLOW AND IS NOT A TEAM-WIDE TOTAL. A team with three open workflows has
    /// three budgets of this size, one each, and nothing is shared between them.
    /// </para>
    ///
    /// Optional and last for the reason <see cref="Paused"/> above it is.
    /// </summary>
    long? BudgetTokens = null);

/// <summary>
/// Tenant-wide Concierge launch settings. The one store of them: a team has no Concierge
/// columns of its own. A NULL <paramref name="Agent"/> means nobody has chosen one; the launcher
/// then picks a default rather than code naming a preset. There is no prompt here: the Concierge
/// always runs the built-in Concierge prompt.
/// </summary>
public sealed record TenantConciergeSettings(string? Agent);

/// <summary>A durable container within a team. <paramref name="SystemPrompt"/> holds the member's
/// own words - the role line it was hired with - never the composed prompt - composition happens at creation and again at restoration, so
/// storing the composed text would freeze the team's name and a manager's roster at creation time.
/// It is NULL for a manager.</summary>
public sealed record PersistedMember(
    string Team,
    string Name,
    string? Label,
    string Agent,
    string? SystemPrompt,
    IReadOnlyCollection<string> Subscribes,
    IReadOnlyCollection<string> Permits,
    long FloorSeq,

    /// <summary>
    /// The tag this member was hired under, or null when none was named.
    ///
    /// Rotation counts by this value and by Agent, so a member hired as `tester` does not affect
    /// `developer` balancing.
    /// </summary>
    string? HiredFor = null,

    /// <summary>
    /// Who last set <see cref="SystemPrompt"/>, or null when nobody is known to have: a member
    /// from before `auth-009`, or a plugin. See <see cref="SystemPromptSetter"/>.
    /// </summary>
    SystemPromptSetter? SystemPromptSetBy = null);

/// <summary>
/// Who last set a member's own instructions, and when. Written at hire - the hiring principal -
/// and by every edit that CHANGES the text; a clear is a change.
/// </summary>
/// <param name="By">A Manager's member id (its identifier on the team, e.g. <c>Manager</c>) when
/// <paramref name="Kind"/> is <see cref="Manager"/>; a person's email, as the tenant log names
/// them, when it is <see cref="Person"/>.</param>
/// <param name="Kind"><see cref="Manager"/> or <see cref="Person"/>.</param>
/// <param name="At">When, in UTC.</param>
public sealed record SystemPromptSetter(string By, string Kind, DateTimeOffset At)
{
    public const string Manager = "manager";
    public const string Person = "person";
}

/// <summary>
/// One team repository's default branch, as stored. <paramref name="Repo"/> is the
/// repository's derived name (the clone's folder under <c>repos/</c>).
///
/// TWO COLUMNS, BECAUSE THEY ARE TWO FACTS WITH DIFFERENT LIFETIMES. <paramref name="FromRemote"/>
/// is what <c>git remote set-head origin --auto</c> last read on a clone or a successful Fetch, and
/// the next successful Fetch replaces it - with NULL when that read fails or origin's HEAD names no
/// branch. <paramref name="SetByPerson"/> is what a person typed in Team settings; no Fetch ever
/// touches it, and it wins until they clear it. NULL in both is NOT KNOWN, and nothing substitutes
/// `main` for it.
/// </summary>
public sealed record RepoDefaultBranch(string Team, string Repo, string? FromRemote, string? SetByPerson)
{
    /// <summary>The branch the host uses: the person's, else the remote's, else null (not known).</summary>
    public string? Branch => SetByPerson ?? FromRemote;
}

/// <summary>
/// One team repository's contributor settings. A repository with an
/// <paramref name="UpstreamUrl"/> is in CONTRIBUTOR MODE: its clone's <c>origin</c> is the fork and
/// <c>upstream</c> the original. Null there is an owned repository.
/// <paramref name="ForkOwner"/> is the account the fork belongs to. <paramref name="DcoSignOff"/>
/// installs the commit-msg hook that signs commits off. <paramref name="ClaSignedNote"/> is what a
/// person recorded about the upstream project's CLA; the platform never signs one.
/// <paramref name="PullRequest"/> is the one pull request recorded for the repository, or null.
/// </summary>
public sealed record RepoContributor(
    string Team,
    string Repo,
    string? UpstreamUrl,
    string? ForkOwner,
    bool DcoSignOff,
    string? ClaSignedNote,
    RepoPullRequest? PullRequest = null)
{
    /// <summary>Whether the repository has an upstream, so its origin is a fork.</summary>
    public bool ContributorMode => UpstreamUrl is not null;
}

/// <summary>
/// A pull request recorded against a team repository: its URL, number, state as last read,
/// and when that was. Stored only; nothing here asks GitHub for it.
/// </summary>
public sealed record RepoPullRequest(string Url, int Number, string State, DateTimeOffset ReadAt);

/// <summary>
/// Teams and their members, durable. <c>TeamRegistry</c> writes through to this on every create and
/// every rename; what it holds in memory - a Dictionary of teams, whose containers live in
/// <c>ContainerHost</c>'s own ConcurrentDictionary - does not survive a restart, and these rows are
/// what restoration reads to rebuild it.
/// </summary>
public interface ITeamStore
{
    Task SaveTeamAsync(PersistedTeam team, CancellationToken ct = default);

    /// <summary>Sets a team's name; <paramref name="name"/> of null means "never relabelled" and
    /// removes any stored name rather than storing a copy of the identifier.</summary>
    Task SetNameAsync(string team, string? name, CancellationToken ct = default);

    /// <summary>
    /// Gets the tenant-wide Concierge settings.
    /// </summary>
    Task<TenantConciergeSettings> ConciergeSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the tenant-wide Concierge settings.
    /// </summary>
    Task SetConciergeSettingsAsync(string? agent, CancellationToken ct = default);

    /// <summary>Replaces a team's ordered repository list.</summary>
    Task SetReposAsync(string team, IReadOnlyList<string> repos, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Replaces a team's environment wholesale.
    ///
    /// REQUIRED, deliberately, where <see cref="SetReposAsync"/> above carries a no-op default. A
    /// defaulted setter means a store that never implements it accepts the call and drops the
    /// write, which is the shape of the defect where the concierge setter updated its cache
    /// and not its row - it worked all session and was gone after a restart.
    /// </summary>
    Task SetEnvAsync(
        string team, IReadOnlyDictionary<string, string> env, CancellationToken ct = default);

    /// <summary>
    /// Sets whether this team is paused.
    ///
    /// REQUIRED, deliberately, for the same reason <see cref="SetEnvAsync"/> above is: a defaulted
    /// setter means a store that never implements it accepts the call and drops the write, which is
    /// exactly the defect a paused team cannot survive - memory saying one thing until restart and
    /// the row saying another.
    ///
    /// A NARROW SINGLE-COLUMN UPDATE, never a re-save of the whole row. That upsert takes an
    /// entire team, so a caller would write every other column back as it read them.
    /// </summary>
    Task SetPausedAsync(string team, bool paused, CancellationToken ct = default);

    /// <summary>
    /// Sets what this team may spend on ONE workflow. See <see cref="PersistedTeam.BudgetTokens"/>
    /// for what the three states mean.
    ///
    /// <b>WRITES WHAT IT IS GIVEN, INCLUDING 0.</b> NULL and 0 are different
    /// answers - NULL inherits the instance figure, 0 is unlimited - so collapsing here silently
    /// turns a person's explicit "unlimited" into a bound. Negative values are refused at the
    /// route, before anything reaches here.
    ///
    /// REQUIRED, deliberately, for the same reason <see cref="SetEnvAsync"/> and
    /// <see cref="SetPausedAsync"/> above are: a defaulted setter means a store that never
    /// implements it accepts the call and drops the write.
    ///
    /// A NARROW SINGLE-COLUMN UPDATE, never a re-save of the whole row - the same argument that
    /// keeps <see cref="SetFloorAsync"/> narrow.
    /// </summary>
    Task SetBudgetAsync(string team, long? budgetTokens, CancellationToken ct = default);


    Task SaveMemberAsync(PersistedMember member, CancellationToken ct = default);

    /// <summary>
    /// Advances ONE member's floor, and nothing else on its row.
    ///
    /// Its own method rather than a re-save through <see cref="SaveMemberAsync"/>, whose ON CONFLICT
    /// clause already writes this column: that one takes a whole <see cref="PersistedMember"/>, so a
    /// caller would compose a record out of a row it read a moment earlier and write every other
    /// column back as it was THEN - which is how a member's prompt gets written to the database while
    /// the live agent goes on being told the old one.
    ///
    /// Case-insensitive on both halves, matching ContainerId equality and every other reader of this
    /// table. A case-varied spelling that quietly matched nothing would answer success and leave the
    /// member on its old floor - and a floor is a guarantee, so a reset that silently did not happen
    /// is worse than one that refused.
    ///
    /// A member that is not there is NOT an error. An UPDATE matching nothing is silent SQL either
    /// way, and throwing would turn a member deleted between opening a dialog and pressing Reset
    /// into a failed reset for everybody else on the team.
    /// </summary>
    Task SetFloorAsync(string team, string name, long floorSeq, CancellationToken ct = default);

    Task<IReadOnlyList<PersistedTeam>> TeamsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PersistedMember>> MembersAsync(CancellationToken ct = default);

    /// <summary>Removes a team and, through the foreign key's ON DELETE CASCADE, every one of its
    /// members.</summary>
    Task DeleteTeamAsync(string team, CancellationToken ct = default);

    /// <summary>
    /// Removes ONE member, leaving its team and every sibling alone. The half a team deletion gets
    /// free from the cascade, and needed on its own because deleting a member is not deleting the
    /// smallest possible team: the manager and the roster survive it.
    /// </summary>
    Task DeleteMemberAsync(string team, string name, CancellationToken ct = default);

    /// <summary>
    /// Sets a team's additional instructions. A NARROW single-column update, for the reason
    /// <see cref="SetPausedAsync"/> gives. Null or blank clears them.
    /// </summary>
    Task SetAdditionalInstructionsAsync(string team, string? text, CancellationToken ct = default);

    /// <summary>Every stored repository default branch. A repository with no row is not known.</summary>
    Task<IReadOnlyList<RepoDefaultBranch>> RepoDefaultBranchesAsync(CancellationToken ct = default);

    /// <summary>
    /// Records what origin's HEAD named on a clone or a successful Fetch; null records not known.
    /// Leaves <see cref="RepoDefaultBranch.SetByPerson"/> alone. REQUIRED, for the reason
    /// <see cref="SetEnvAsync"/> gives.
    /// </summary>
    Task SetRemoteDefaultBranchAsync(string team, string repo, string? branch, CancellationToken ct = default);

    /// <summary>
    /// Sets or (with null) clears a person's choice of default branch. Leaves
    /// <see cref="RepoDefaultBranch.FromRemote"/> alone. REQUIRED, for the reason
    /// <see cref="SetEnvAsync"/> gives.
    /// </summary>
    Task SetPersonDefaultBranchAsync(string team, string repo, string? branch, CancellationToken ct = default);

    /// <summary>Every stored repository's contributor settings. No row is an owned repository.</summary>
    Task<IReadOnlyList<RepoContributor>> RepoContributorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes a repository's contributor SETTINGS - upstream, fork owner, DCO, CLA note - and leaves
    /// its recorded pull request alone. REQUIRED, for the reason <see cref="SetEnvAsync"/> gives.
    /// </summary>
    Task SetRepoContributorAsync(RepoContributor settings, CancellationToken ct = default);

    /// <summary>
    /// Records (or, with null, forgets) a repository's pull request and leaves its settings alone.
    /// REQUIRED, for the reason <see cref="SetEnvAsync"/> gives.
    /// </summary>
    Task SetRepoPullRequestAsync(string team, string repo, RepoPullRequest? pullRequest, CancellationToken ct = default);
}
