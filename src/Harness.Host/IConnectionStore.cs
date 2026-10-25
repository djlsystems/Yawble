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

    Task ReconnectAsync(
        string id, IReadOnlyList<string> scopes, ConnectionTokens tokens, TriggerAudit audit, CancellationToken ct = default, string? tenant = null);

    Task StoreRefreshAsync(string id, ConnectionTokens tokens, CancellationToken ct = default);

    Task StoreRotatedRefreshAsync(string id, string? refreshToken, CancellationToken ct = default);

    Task MarkNeedsReconnectAsync(string id, string reason, TriggerAudit audit, CancellationToken ct = default);

    Task<bool> RenameAsync(string id, string name, TriggerAudit audit, CancellationToken ct = default);

    Task<(bool Deleted, IReadOnlyList<ConnectionUse> UsedBy)> DeleteAsync(
        string id, TriggerAudit audit, CancellationToken ct = default);

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
