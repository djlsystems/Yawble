namespace Harness.Contracts;

/// <summary>How a principal proved who it is. A machine principal is never a person acting.</summary>
public enum PrincipalKind
{
    User,
    Container,

    /// <summary>A credential minted for one Concierge session. Acts as its owner,
    /// intersected with the team the session is bound to.</summary>
    Concierge,

    /// <summary>
    /// A tenant-wide Concierge credential. Acts as its owner and is gated per request by
    /// the team route value, not by a team bound into the credential.
    /// </summary>
    TenantConcierge,

    /// <summary>A credential a person minted and carried away to an outside tool. Like an
    /// Concierge and unlike a Container, it acts as its owner rather than in its own
    /// right.</summary>
    ApiKey,

    /// <summary>The instance's WORKER KEY: what a worker process connects to control with. It
    /// holds no permit and is taken on the worker connection alone; every other route refuses it
    /// with a sentence.</summary>
    Worker,
}

/// <summary>
/// Who is making this request, resolved once and read by everything downstream. A cookie and an
/// API key both produce one of these, so no handler branches on which arrived.
/// </summary>
/// <param name="OwnerUserId">
/// The user this credential ACTS AS, or null when it has authority of its own.
///
/// Null is a container: one team, permits by role, answerable to nobody. Set is a console session
/// or an API key, and <see cref="IUserStore"/> is then the authority - resolution checks the owner
/// still exists FRESH on every request rather than trusting a copy taken at mint, and a key outlives
/// a cookie by any measure.
///
/// It never makes a machine principal a person. <c>HumansOnly</c> refuses every machine kind, and
/// that is what keeps a pasted key away from user administration whoever owns it.
/// </param>
public sealed record Principal(
    string Id,
    PrincipalKind Kind,
    IReadOnlySet<string> Permits,
    string? OwnerUserId = null)
{
    public bool May(string permit) => Permits.Contains(permit);
}

/// <summary>
/// Everything a principal can cause, one per governable verb.
///
/// NOT called a ceiling: `Ceiling` already means an MemberRuntime's queue-depth bound in this
/// codebase, it is on ContainerSnapshot and it is rendered on the card. Two meanings for one word
/// would collide exactly when someone is reasoning about a runaway.
/// </summary>
public static class Permits
{
    public const string Read = "Read";
    public const string Tell = "Tell";
    public const string CreateContainer = "CreateContainer";
    public const string CreateTeam = "CreateTeam";

    /// <summary>
    /// Saying what you are doing, about YOURSELF. The narrowest verb here by a wide margin: it
    /// writes one row naming its own container, nothing subscribes to it, and the ledger projection
    /// excludes it so it never reaches a prompt.
    ///
    /// That narrowness is what makes it affordable as the DEFAULT a member is created with. A
    /// member holding this reaches exactly one route out of forty; without enforced permits,
    /// handing a member any credential would hand it `tell` and hiring.
    /// </summary>
    public const string Progress = "Progress";
    public const string Skills = "Skills";
    public const string SkillsGated = "SkillsGated";

    /// <summary>
    /// Changing a team's SITES: create, publish, roll back, unpublish, and write or delete their
    /// data. Its own verb rather than Read, Progress or Skills stretched to cover it: each of those
    /// is narrow on purpose, and a page a person opens is a different thing to be able to change.
    /// Reading a site is Read. Deleting one is a person's action and no permit reaches it.
    ///
    /// Granted by default to a team's Manager, to every agent member, and to the Concierge - all
    /// bounded to their own team by <c>TeamGate</c> like every other <c>{team}</c> route.
    /// </summary>
    public const string Sites = "Sites";

    /// <summary>
    /// Proposing an outcome and linking a workflow to one: <c>POST /api/outcomes/propose</c> and
    /// <c>PUT /api/teams/{team}/workflows/{correlation}/outcome</c>. Its own verb, folded into
    /// nothing else, as <see cref="Sites"/> is. Granted to a team's Manager (restored on it) and to
    /// the Concierge; NEVER to a member, which works a card and does not decide what a workflow is
    /// for. Confirming, renaming, merging, retiring, reactivating and rejecting are a person's, and
    /// no permit reaches them.
    /// </summary>
    public const string Outcomes = "Outcomes";

    /// <summary>
    /// Merge to main, and Bring current and merge, for the CONCIERGE: its own verb, folded into
    /// nothing else. Held by <c>ConciergeLaunchFactory.ConciergePermits</c> alone and taken from
    /// every other principal at authentication however its key was made, so a Manager, a member, a
    /// plugin or a pasted key never reaches a default branch with it. Holding it is not enough: the
    /// routes also ask the tenant setting <c>concierge.mayMerge</c>, off unless a person turns it on.
    ///
    /// NOT IN <see cref="All"/>, which is what a member key or an API key can be minted with.
    /// </summary>
    public const string Merge = "Merge";

    /// <summary>
    /// Archiving and unarchiving a team, for the CONCIERGE: its own verb, folded into nothing else,
    /// exactly as <see cref="Merge"/> is. Held by <c>ConciergeLaunchFactory.ConciergePermits</c> alone
    /// and taken from every other principal at authentication however its key was made; the routes
    /// also ask the tenant setting <c>concierge.mayArchive</c>, off unless a person turns it on.
    /// Deleting a team stays a person's, and no permit reaches it.
    ///
    /// NOT IN <see cref="All"/>, which is what a member key or an API key can be minted with.
    /// </summary>
    public const string Archive = "Archive";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Read, Tell, CreateContainer, CreateTeam, Progress,
        Skills, SkillsGated, Sites, Outcomes,
    };
}

/// <summary>
/// One API key as a ROSTER ROW: enough to recognise a key, and nothing that could be used as one.
///
/// There is no credential field, and there is no shape of this record that could carry one. Only a
/// SHA-256 hash is stored, so show-once is a property of the store rather than a promise a dialog
/// makes - and `Prefix` is the eight characters captured at mint that let a person tell which key a
/// row is without being able to use it.
/// </summary>
public sealed record ApiKeySummary(
    string Id,
    string? Label,
    string Prefix,
    string OwnerUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);

public sealed record StoredUser(string Id, string Email);

public interface IUserStore
{
    Task<bool> AnyAsync(CancellationToken ct = default);

    Task<StoredUser?> FindAsync(string email, CancellationToken ct = default);

    /// <summary>By id, because that is what a cookie carries. An email can change underneath a
    /// live session, so a handler that looked the caller up by the email in its claims would be
    /// reading a stale key the moment the profile endpoint succeeds.</summary>
    Task<StoredUser?> FindByIdAsync(string id, CancellationToken ct = default);

    Task<StoredUser> CreateAsync(string email, string password, CancellationToken ct = default);

    /// <summary>The created user, or null when the email already belongs to another account - the
    /// UNIQUE index deciding via the SqliteException a duplicate raises, not a SELECT-first that
    /// could race it, exactly like TryUpdateEmailAsync below. CreateAsync above stays
    /// exception-throwing: every existing caller already knows its email is unique (AnyAsync gates
    /// /api/auth/admin; every test helper creates exactly once), so only a caller that accepts an
    /// arbitrary, externally-supplied email - POST /api/users - needs the non-throwing form. The
    /// catch belongs here, one layer down from the Host, which composes modules and does not reach
    /// into their internals - not in the endpoint, which has no business knowing what
    /// SqliteErrorCode 19 means.</summary>
    Task<StoredUser?> TryCreateAsync(
        string email, string password, CancellationToken ct = default);

    /// <summary>False when the address already belongs to another account. Not an exception,
    /// because a taken email is an ordinary answer to an ordinary request rather than a fault.</summary>
    Task<bool> TryUpdateEmailAsync(string id, string email, CancellationToken ct = default);

    Task UpdatePasswordAsync(string id, string password, CancellationToken ct = default);

    /// <summary>The user, or null when the email is unknown OR the password is wrong. Deliberately
    /// one answer for both: telling them apart is an account-enumeration oracle.</summary>
    Task<StoredUser?> VerifyAsync(string email, string password, CancellationToken ct = default);

    Task<IReadOnlyList<StoredUser>> ListAsync(CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Which team this person is CURRENTLY working on, or null when none is.
    ///
    /// ON THE PERSON rather than on a credential, because a browser and the Concierge it drives are
    /// two principals for one person: keyed on either credential, the browser would write something
    /// the agent never reads.
    ///
    /// NULL is a real state and not an absence to be filled - "no team is active" is where somebody
    /// on the Teams tab genuinely is. A deliberate departure from the NULL-means-nobody-has-chosen
    /// rule that governs Prompt and Agent names, and the reason the CLI's refusal for it is worded
    /// as a choice not yet made rather than as a misconfiguration.
    /// </summary>
    Task<string?> CurrentTeamForAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Points a person at a team, or at nothing when <paramref name="team"/> is null.
    /// </summary>
    /// <remarks>
    /// REQUIRED rather than defaulted, for the reason <see cref="IPrincipalStore.RevokeForTeamAsync"/>
    /// states: a defaulted setter lets a store accept the call and drop it, and a current team that
    /// silently does not move is this feature failing in exactly the way it exists to prevent.
    /// </remarks>
    Task SetCurrentTeamAsync(string userId, string? team, CancellationToken ct = default);
}

public interface IPrincipalStore
{
    /// <summary>
    /// Mints a credential and returns it ONCE. Only its hash is kept, so this value can never be
    /// read back - which is what stops a store that leaks every credential when read.
    /// </summary>
    /// <param name="credential">An explicit value instead of a generated one. Used by the host
    /// bootstrap credential, which a test or a developer may want to fix in configuration so it is
    /// the same across restarts. Null generates one, which is the normal case.</param>
    /// <param name="ownerUserId">The user this credential acts as - see
    /// <see cref="Principal.OwnerUserId"/>. Null for a container, which has authority of its
    /// own.</param>
    /// <param name="label">What a human called this credential, for the roster that renders it.
    /// Null for a container and a console session, which nobody ever sees.</param>
    /// <param name="team">Where this credential was born, or NULL when it was not born anywhere.
    /// A container and a Concierge session are both bounded by this column; a user API key
    /// is bounded by its owner alone, so it has nothing to put here and says so rather
    /// than storing a sentinel that reads as a forgotten argument.</param>
    Task<string> MintAsync(
        string id,
        PrincipalKind kind,
        string? team,
        IReadOnlySet<string> permits,
        string? credential = null,
        string? ownerUserId = null,
        string? label = null,
        CancellationToken ct = default);

    Task<Principal?> ResolveAsync(string credential, CancellationToken ct = default);

    /// <summary>The team stored on this principal's row, or null. NULL is now AMBIGUOUS and
    /// deliberately left so: it means either "no such id" or "this principal is bounded by no team"
    /// - which is every API key since auth-022. Nothing has to tell them apart, because the one
    /// caller, TeamAccess, asks only for a Container or an Concierge and both always carry
    /// one. A caller that ever needs the distinction should ask a new question rather than widen
    /// this answer.</summary>
    Task<string?> TeamForAsync(string id, CancellationToken ct = default);

    Task RevokeAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Revokes EVERY credential bound to a team - whatever its id and whatever its kind. Returns how
    /// many rows went.
    /// </summary>
    /// <remarks>
    /// BY TEAM, NOT BY ID, and the two are not interchangeable. <see cref="RevokeAsync"/> is called
    /// with a container's qualified name, so a deletion sweep built on it revokes containers and
    /// nothing else: an API key a person minted against this team survives, as does any row whose id
    /// does not happen to be a ContainerId. A live credential naming a team that no longer exists
    /// must not survive the team.
    ///
    /// The team column is the authority here rather than the id's shape, because the shape is a
    /// convention and the column is a fact.
    ///
    /// REQUIRED rather than defaulted - a defaulted revoke lets a store accept the call and drop it,
    /// which for a credential is the worst direction to fail in.
    /// </remarks>
    Task<int> RevokeForTeamAsync(string team, CancellationToken ct = default);

    /// <summary>Every API key owned by one person, newest first.</summary>
    Task<IReadOnlyList<ApiKeySummary>> ListApiKeysForAsync(
        string ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// Every API key in the tenant, newest first.
    ///
    /// Its OWN method rather than a nullable owner argument on the one above, deliberately: the two
    /// have different authorities behind them, and a filter that WIDENS when it is forgotten is the
    /// wrong shape for a disclosure.
    /// </summary>
    Task<IReadOnlyList<ApiKeySummary>> ListAllApiKeysAsync(CancellationToken ct = default);

    /// <summary>
    /// The user an API key acts as, or null when the id is unknown OR names a principal that is not
    /// an API key.
    ///
    /// The kind filter is load-bearing rather than tidy: this is the question
    /// DELETE /api/keys/{id} asks to decide whether the caller may revoke, and answering it for a
    /// console session or a container would turn that route into a way to kill a running Agent
    /// Container's credential - which surfaces much later as an unrelated permission error.
    /// </summary>
    Task<string?> OwnerOfApiKeyAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// When this credential was last used to authenticate a request, or null when it never has been
    /// - which is also the answer for an id nobody minted.
    ///
    /// The roster renders it to tell a live credential from an abandoned one. It has a second reader
    /// now: bracketing ONE RUN with two of these answers says whether the agent reached the platform
    /// at all, which is how a run that did nothing is told from a run that decided there was nothing
    /// to do. See CredentialUseRunner.
    ///
    /// READ BEFORE AND AFTER, NEVER "is it still null". MintAsync's upsert nulls this column, so a
    /// null check LOOKS like it would work without a before-value - but a container's credential is
    /// minted at CREATION and at RESTORE, not per invocation, so by its second run it is never null
    /// again. Both answers being null is one of the ways they can be equal.
    /// </summary>
    Task<DateTimeOffset?> LastUsedAtAsync(string id, CancellationToken ct = default);
}
