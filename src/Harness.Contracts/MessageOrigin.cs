namespace Harness.Contracts;

/// <summary>
/// WHAT KIND OF ACTOR A WORKFLOW BEGAN WITH: the closed set of <c>messages.origin_kind</c>, copied onto
/// a run's <c>execution_snapshots</c> and <c>usage_ledger</c> rows. The one place these words are spelled.
/// </summary>
public static class OriginKinds
{
    /// <summary>A person signed in, or an API key a person minted (stored as its owner).</summary>
    public const string Person = "person";

    /// <summary>A team member, or a Concierge acting for its owner.</summary>
    public const string Agent = "agent";

    /// <summary>A schedule, an event or folder trigger, or a wake a member asked for.</summary>
    public const string Trigger = "trigger";

    /// <summary>The Host itself, or a source no principal stands behind.</summary>
    public const string System = "system";

    public static IReadOnlyList<string> All { get; } = [Person, Agent, Trigger, System];
}

/// <summary>
/// WHAT KIND OF ACTOR PUBLISHED ONE ROW: the closed set of <c>messages.publisher_kind</c>. Always the
/// row's own publisher, never inherited. The one place these words are spelled.
/// </summary>
public static class PublisherKinds
{
    public const string Person = "person";
    public const string Agent = "agent";
    public const string Host = "host";

    public static IReadOnlyList<string> All { get; } = [Person, Agent, Host];
}

/// <summary>
/// THE AUTHENTICATED PRINCIPAL BEHIND ONE APPEND, handed to <see cref="IMessageLog"/> as a server-side
/// argument. Built only from an endpoint's <c>ClaimsPrincipal</c> (<c>AppendContexts.From</c> in the
/// Host), never from a payload, a <see cref="NewMessage"/> field or a header a client controls - which
/// is why <see cref="NewMessage"/> carries no origin field at all.
/// </summary>
/// <param name="Kind">How the principal proved who it is.</param>
/// <param name="PrincipalId">The principal's own id: a user id, an API key's id, a member's ContainerId.</param>
/// <param name="OwnerUserId">The user a key or a Concierge acts as; null for a user or a member.</param>
/// <param name="Email">A signed-in person's email claim, when the ticket carries one.</param>
public sealed record AppendContext(
    PrincipalKind Kind,
    string PrincipalId,
    string? OwnerUserId = null,
    string? Email = null)
{
    /// <summary>
    /// THE PERSON'S ACT THIS APPEND RECORDS, or null: the store writes its <c>intervention_ledger</c> row in
    /// the append's own transaction, naming the row as <c>related_seq</c> and its workflow. Set only by a
    /// route, server side, through <see cref="Intervening"/> - and null for any principal that is not a
    /// person (<see cref="InterventionAct.Of"/>).
    /// </summary>
    public InterventionAct? Intervention { get; init; }

    /// <summary>This principal, recording <paramref name="kind"/> on <paramref name="team"/> with the row
    /// it is appended with. A principal that is no person records nothing.</summary>
    public AppendContext Intervening(string kind, string? team) =>
        this with { Intervention = InterventionAct.Of(kind, this, team) };
}

/// <summary>
/// A ROW'S DERIVED ORIGIN AND ITS PUBLISHER'S KIND, as the store writes them.
/// <see cref="ActorEmail"/> may still need resolving from <c>users</c> when <see cref="ResolveEmailOf"/>
/// is set: the store does that inside the append's transaction, so a key's owner is stored as they stood
/// at append and the row still reads after the key or the user is gone.
/// </summary>
public sealed record MessageOrigin(
    string Kind,
    string? ActorId,
    string? ActorEmail,
    string? Trigger,
    string PublisherKind)
{
    /// <summary>The user id whose email the store reads from <c>users</c>, when <see cref="ActorEmail"/> is not known.</summary>
    public string? ResolveEmailOf { get; init; }

    /// <summary>The source prefixes a trigger, a schedule and a wake publish under.</summary>
    public static IReadOnlyList<string> TriggerSourcePrefixes { get; } = ["schedule:", "trigger:", "wake:"];

    /// <summary>
    /// THE ROOT RULE: who began a workflow whose first row is this one. A principal, when the append was
    /// handed one, decides; with none, the row's source does. A payload is never read.
    /// </summary>
    public static MessageOrigin ForRoot(string source, AppendContext? context)
    {
        if (context is not null) return OfPrincipal(context);

        if (IsTriggerSource(source))
        {
            return new MessageOrigin(OriginKinds.Trigger, null, null, source, PublisherKinds.Host);
        }

        return IsMemberSource(source)
            ? new MessageOrigin(OriginKinds.Agent, source, null, null, PublisherKinds.Agent)
            : new MessageOrigin(OriginKinds.System, null, null, null, PublisherKinds.Host);
    }

    /// <summary>
    /// A ROW'S OWN PUBLISHER: the principal it was appended for, else what its source names. Never
    /// inherited from the root.
    /// </summary>
    public static string PublisherOf(string source, AppendContext? context) =>
        context is not null ? OfPrincipal(context).PublisherKind : ForRoot(source, null).PublisherKind;

    /// <summary>A caused row: the root's origin as it was stored, with this row's own publisher.</summary>
    public static MessageOrigin Inherited(MessageOrigin root, string source, AppendContext? context) =>
        root with { PublisherKind = PublisherOf(source, context), ResolveEmailOf = null };

    private static MessageOrigin OfPrincipal(AppendContext context) => context.Kind switch
    {
        PrincipalKind.User => new MessageOrigin(
            OriginKinds.Person, context.PrincipalId, context.Email, null, PublisherKinds.Person)
        {
            ResolveEmailOf = context.Email is null ? context.PrincipalId : null,
        },

        // A key acts as its owner: the owner is stored, not the key, and the email is read at append.
        PrincipalKind.ApiKey => new MessageOrigin(
            OriginKinds.Person, context.OwnerUserId, null, null, PublisherKinds.Person)
        {
            ResolveEmailOf = context.OwnerUserId,
        },

        // A Concierge is an agent a person drives: an agent's act, accountable to its owner.
        PrincipalKind.Concierge or PrincipalKind.TenantConcierge => new MessageOrigin(
            OriginKinds.Agent, context.OwnerUserId ?? context.PrincipalId, null, null, PublisherKinds.Agent)
        {
            ResolveEmailOf = context.OwnerUserId,
        },

        PrincipalKind.Container => new MessageOrigin(
            OriginKinds.Agent, context.PrincipalId, null, null, PublisherKinds.Agent),

        _ => new MessageOrigin(OriginKinds.System, null, null, null, PublisherKinds.Host),
    };

    private static bool IsTriggerSource(string source) =>
        TriggerSourcePrefixes.Any(prefix => source.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>A member's own source, <c>team/member</c>: a slash, and no <c>kind:</c> prefix before it
    /// (a site's <c>site:team/name</c> is not a member).</summary>
    private static bool IsMemberSource(string source)
    {
        var slash = source.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && source.IndexOf(':', 0, slash) < 0;
    }
}
