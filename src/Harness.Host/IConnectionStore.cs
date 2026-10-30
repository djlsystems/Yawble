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

    Task InsertFlowAsync(ConnectionFlow flow, CancellationToken ct = default);

    Task<ConnectionFlow?> ConsumeFlowAsync(string state, DateTimeOffset now, string? userId, CancellationToken ct = default);
}
