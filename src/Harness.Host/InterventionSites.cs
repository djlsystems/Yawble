using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// EVERY PLACE A PERSON'S ACT IS RECORDED in <c>intervention_ledger</c>, and the kinds nothing records
/// yet. A site missing from here is a person's act silently lost, so <c>InterventionSitesTests</c> holds
/// each listed route to existing and to writing exactly one row of its kind when a person calls it, and
/// every kind in <see cref="InterventionKinds.All"/> to a site or to <see cref="NoActToday"/>.
/// </summary>
public static class InterventionSites
{
    /// <summary>One route that records <paramref name="Kind"/>, and the log row it records it with.</summary>
    public sealed record Site(string Kind, string Method, string Route, string WrittenWith);

    /// <summary>Every route that records an act. A route may record one of two kinds (a tell).</summary>
    public static IReadOnlyList<Site> Routes { get; } =
    [
        new(InterventionKinds.Dispatch, "POST", "/api/teams/{team}/backlog/{id:long}/dispatch", MessageTypes.BacklogItemDispatched),
        new(InterventionKinds.Dispatch, "POST", "/api/backlog/{id:long}/dispatch-to-new", MessageTypes.BacklogItemDispatched),
        new(InterventionKinds.Instruction, "POST", "/api/teams/{team}/containers/{name}/tell", "agentContainer.instruction.<member>"),
        new(InterventionKinds.DecisionAnswered, "POST", "/api/teams/{team}/containers/{name}/tell", "agentContainer.instruction.<member>"),
        new(InterventionKinds.Resume, "POST", "/api/teams/{team}/workflows/{correlation:long}/resume", MessageTypes.WorkflowResumed),
        new(InterventionKinds.Resume, "POST", "/api/teams/{team}/resume", TenantActions.TeamResumed),
        new(InterventionKinds.Nudge, "POST", "/api/teams/{team}/workflows/{correlation:long}/nudge", "agentContainer.instruction.<team>/Manager"),
        new(InterventionKinds.Close, "POST", "/api/teams/{team}/workflows/{correlation:long}/close", MessageTypes.WorkflowClosed),
        new(InterventionKinds.OutcomeChanged, "PUT", "/api/teams/{team}/workflows/{correlation:long}/outcome", TenantActions.WorkflowOutcomeChanged),
        new(InterventionKinds.OutcomeChanged, "DELETE", "/api/teams/{team}/workflows/{correlation:long}/outcome", TenantActions.WorkflowOutcomeChanged),
        new(InterventionKinds.CredentialSupplied, "POST", "/api/connections/credentials", TenantActions.ConnectionConnected),
        new(InterventionKinds.CredentialSupplied, "PUT", "/api/connections/{id}/credentials", TenantActions.ConnectionCredentialsChanged),
        new(InterventionKinds.CredentialSupplied, "POST", "/api/connections/imap", TenantActions.ConnectionConnected),
        new(InterventionKinds.CredentialSupplied, "PUT", "/api/connections/{id}/mailbox", TenantActions.ConnectionCredentialsChanged),
        new(InterventionKinds.CredentialSupplied, "PUT", "/api/connections/{id}/password", TenantActions.ConnectionPasswordUpdated),
        new(InterventionKinds.CredentialSupplied, "POST", "/api/connections/complete", TenantActions.ConnectionConnected),
        new(InterventionKinds.CredentialSupplied, "GET", Connections.CallbackPath, TenantActions.ConnectionConnected),
        new(InterventionKinds.CredentialSupplied, "PUT", "/api/teams/{team}/members/{member}/secrets/{field}", TenantActions.MemberSecretSet),
        new(InterventionKinds.CredentialSupplied, "PUT", "/api/teams/{team}/members/{member}/named-secrets/{name}", TenantActions.MemberNamedSecretSet),
        new(InterventionKinds.CredentialSupplied, "PUT", "/api/teams/{team}/members/{member}/plugin-settings", TenantActions.MemberSecretSet),
        new(InterventionKinds.CredentialSupplied, "PUT", "/api/secrets/{key}", TenantActions.SecretSet),
    ];

    /// <summary>
    /// THE CONCIERGE SESSION IS RECORDED BY THE HOST, not a route: one row when the session ends, measured
    /// from its open (<see cref="ConciergeSessionStore"/>). It has no log row to go with.
    /// </summary>
    public const string ConciergeSessionSite = "ConciergeSessionStore: session end";

    /// <summary>
    /// Kinds in the set with no act in the product today: nothing writes them. <c>access_resolved</c>:
    /// no run waits on an access grant a person resolves (a member's missing connection is assigned to its
    /// team in Team settings, which is a setting, not a resolved wait).
    /// </summary>
    public static IReadOnlyList<string> NoActToday { get; } = [InterventionKinds.AccessResolved];

    /// <summary>
    /// The tenant actions whose row records a <c>credential_supplied</c> act when a person made it: a
    /// connection's value saved (connected, reconnected, password, credentials) and a secret a member runs
    /// with set or replaced. A clear, a rename or a test supplies nothing.
    /// </summary>
    public static IReadOnlyList<string> CredentialActions { get; } =
    [
        TenantActions.ConnectionConnected, TenantActions.ConnectionReconnected,
        TenantActions.ConnectionPasswordUpdated, TenantActions.ConnectionCredentialsChanged,
        TenantActions.MemberSecretSet, TenantActions.MemberSecretReplaced,
        TenantActions.MemberNamedSecretSet, TenantActions.MemberNamedSecretReplaced,
        TenantActions.SecretSet, TenantActions.SecretReplaced,
    ];

    /// <summary>Whether a person's tenant row with <paramref name="action"/> supplies a credential.</summary>
    public static bool SuppliesCredential(string action) => CredentialActions.Contains(action, StringComparer.Ordinal);
}
