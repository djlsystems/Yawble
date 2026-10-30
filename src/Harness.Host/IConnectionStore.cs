using System.Data.Common;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE OAUTH AND MAILBOX CONNECTIONS the Host holds for plugins: providers, connections, sign-in flows
/// and mailboxes. Every credential is Data Protection ciphertext and every person's write lands with
/// its tenant row in the same transaction. A consumer depends on this, not on the SQLite class, so a
/// provider is a different implementation and never a different caller. See <see cref="ConnectionStore"/>.
/// </summary>
public interface IConnectionStore
{
    Task<IReadOnlyList<StoredProvider>> ProvidersAsync(CancellationToken ct = default);

    Task<StoredProvider?> ProviderAsync(string id, CancellationToken ct = default);

    Task<string?> ClientSecretAsync(string id, CancellationToken ct = default);

    Task SaveProviderAsync(StoredProvider provider, string? secret, TriggerAudit audit, CancellationToken ct = default);

    Task<(bool Removed, IReadOnlyList<string> Connections)> DeleteProviderAsync(string id, TriggerAudit audit, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectionRecord>> ListAsync(CancellationToken ct = default);

    Task<ConnectionRecord?> GetAsync(string id, CancellationToken ct = default);

    Task<ConnectionTokens?> TokensAsync(string id, CancellationToken ct = default);

    Task InsertAsync(
        ConnectionRecord record, ConnectionTokens tokens, string? connectedBy, TriggerAudit audit, CancellationToken ct = default);

    /// <param name="account">The account signed in as: another account under the same name replaces it.
    /// Null keeps the stored one.</param>
    Task ReconnectAsync(
        string id, IReadOnlyList<string> scopes, ConnectionTokens tokens, TriggerAudit audit, CancellationToken ct = default, string? tenant = null,
        string? account = null);

    Task StoreRefreshAsync(string id, ConnectionTokens tokens, CancellationToken ct = default);

    Task StoreRotatedRefreshAsync(string id, string? refreshToken, CancellationToken ct = default);

    Task MarkNeedsReconnectAsync(string id, string reason, TriggerAudit audit, CancellationToken ct = default);

    Task<bool> RenameAsync(string id, string name, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// Deletes a connection and its credentials. With no <paramref name="unbind"/> it is refused (the
    /// users) while any member binds it. With one, every slot bound to it is unbound in the same
    /// transaction, and the rows <paramref name="unbind"/> makes of the users found there land with the
    /// connection's own: all of it, or none.
    /// </summary>
    Task<(bool Deleted, IReadOnlyList<ConnectionUse> UsedBy)> DeleteAsync(
        string id, TriggerAudit audit, CancellationToken ct = default,
        Func<IReadOnlyList<ConnectionUse>, IReadOnlyList<TriggerAudit>>? unbind = null);

    /// <summary>Whether a connection other than <paramref name="exceptId"/> is called
    /// <paramref name="name"/>, ignoring case. Every write that names a connection checks it again
    /// in its own transaction and throws <see cref="ConnectionNameTakenException"/>.</summary>
    Task<bool> NameTakenAsync(string name, string? exceptId = null, CancellationToken ct = default);

    /// <summary>A new credential connection (<see cref="ConnectionRecord.Credential"/> set): its row, its
    /// fields, its masked values as ciphertext and the tenant row, in one transaction.</summary>
    Task InsertCredentialAsync(
        ConnectionRecord record, IReadOnlyDictionary<string, string> secrets, string? connectedBy, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>A credential connection's masked values, decrypted, for a run. Never answered by a route.</summary>
    Task<IReadOnlyDictionary<string, string>?> CredentialSecretsAsync(string id, CancellationToken ct = default);

    /// <summary>An edit: the fields and masked values replaced, the account with them. False when there
    /// is no such credential connection.</summary>
    Task<bool> UpdateCredentialAsync(
        string id, string account, CredentialSettings settings, IReadOnlyDictionary<string, string> secrets, TriggerAudit audit,
        CancellationToken ct = default);

    /// <summary>A mailbox's edit after a good login: its account, username and servers, the password when
    /// one is given (null keeps it), the status back to <c>ok</c>. False when there is no such mailbox.</summary>
    Task<bool> UpdateMailboxAsync(
        string id, string account, MailboxSettings settings, string? password, DateTimeOffset verifiedAt, TriggerAudit audit,
        CancellationToken ct = default);

    Task RecordAsync(TriggerAudit audit, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectionUse>> UsedByAsync(string id, CancellationToken ct = default);

    Task<ILookup<string, ConnectionUse>> AllUsesAsync(CancellationToken ct = default);

    Task InsertMailboxAsync(
        ConnectionRecord record, MailboxSettings settings, string password, string? connectedBy, TriggerAudit audit, CancellationToken ct = default);

    Task<string?> MailboxPasswordAsync(string id, CancellationToken ct = default);

    Task<bool> UpdateMailboxPasswordAsync(
        string id, string password, DateTimeOffset verifiedAt, TriggerAudit audit, CancellationToken ct = default);

    Task StoreMailboxVerifiedAsync(string id, DateTimeOffset at, CancellationToken ct = default);

    // ---- which teams may use a connection (team_connections, auth-032) ---------------------------

    /// <summary>The connections assigned to <paramref name="team"/>, oldest first. A plugin member of the
    /// team binds only one of these.</summary>
    Task<IReadOnlyList<TeamConnection>> AssignedAsync(string team, CancellationToken ct = default);

    /// <summary>Every assignment on the instance: connection id to the teams it is assigned to.</summary>
    Task<ILookup<string, string>> AllAssignmentsAsync(CancellationToken ct = default);

    /// <summary>
    /// Assigns <paramref name="connectionId"/> to <paramref name="team"/> with its tenant row, in one
    /// transaction. <see cref="AssignOutcome.Already"/> and <see cref="AssignOutcome.NoConnection"/>
    /// write nothing.
    /// </summary>
    Task<AssignOutcome> AssignAsync(string team, string connectionId, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// A new team's assignments, each with its tenant row, written on <paramref name="connection"/> inside
    /// <paramref name="transaction"/> - the one the team store writes the team's row in
    /// (<see cref="ITeamStore.SaveTeamAsync(PersistedTeam, Func{DbConnection, DbTransaction, CancellationToken, Task}, CancellationToken)"/>),
    /// so the row and every assignment commit together or not at all. Never commits. One already there
    /// is skipped, with no row.
    /// </summary>
    Task AssignAllAsync(
        DbConnection connection, DbTransaction transaction, string team,
        IReadOnlyList<(string ConnectionId, TriggerAudit Audit)> assignments, CancellationToken ct = default);

    /// <summary>
    /// Removes <paramref name="connectionId"/> from <paramref name="team"/>. Every member of the team that
    /// binds it loses those slots in the same transaction, and the rows <paramref name="unbind"/> makes of
    /// them land with the removal's own: all of it, or none. False, with nothing written, when it was not
    /// assigned.
    /// </summary>
    Task<(bool Removed, IReadOnlyList<ConnectionUse> Unbound)> UnassignAsync(
        string team, string connectionId, TriggerAudit audit,
        Func<IReadOnlyList<ConnectionUse>, IReadOnlyList<TriggerAudit>> unbind, CancellationToken ct = default);

    /// <summary>A clone's assignments: every connection assigned to <paramref name="from"/> assigned to
    /// <paramref name="to"/> too, each with the row <paramref name="audit"/> makes of it, in one
    /// transaction. Answers the ids assigned.</summary>
    Task<IReadOnlyList<string>> CopyAssignmentsAsync(
        string from, string to, Func<string, TriggerAudit> audit, CancellationToken ct = default);

    Task InsertFlowAsync(ConnectionFlow flow, CancellationToken ct = default);

    Task<ConnectionFlow?> ConsumeFlowAsync(string state, DateTimeOffset now, string? userId, CancellationToken ct = default);
}

/// <summary>One connection assigned to one team.</summary>
public sealed record TeamConnection(string Team, string ConnectionId, DateTimeOffset AssignedAt);

/// <summary>What an assignment came to.</summary>
public enum AssignOutcome
{
    Assigned,
    Already,
    NoConnection,
}
