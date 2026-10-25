namespace Harness.Contracts;

/// <summary>A stored plugin key as its row holds it: the Data Protection ciphertext, who set it and when.</summary>
public sealed record StoredSecret(string ValueProtected, string SetBy, DateTimeOffset SetAt);

/// <summary>
/// THE <c>plugin_secrets</c> ROWS, and nothing else. ENCRYPTION IS NOT HERE: `PluginSecretStore`
/// protects a value before it reaches these rows and unprotects what they return, so both providers
/// share one cipher and one purpose and a key stored on one provider reads the same on the other.
/// Every write lands with its tenant row in the same transaction, or not at all.
/// </summary>
public interface IPluginSecretRows
{
    /// <summary>The stored row for <paramref name="logicalKey"/>, or null. Synchronous because the
    /// key lookup a plugin run makes is.</summary>
    StoredSecret? Find(string logicalKey);

    /// <summary>
    /// Sets or replaces the key's row with its tenant row, in one transaction. <paramref name="audit"/>
    /// is told whether a row existed (true: replaced) and answers the tenant row to write.
    /// </summary>
    Task SetAsync(
        string logicalKey, string valueProtected, string setBy, DateTimeOffset at, Func<bool, TriggerAudit> audit,
        CancellationToken ct = default);

    /// <summary>Deletes the key's row with <paramref name="audit"/> in the same transaction. Nothing
    /// stored changes nothing and writes no row.</summary>
    Task ClearAsync(string logicalKey, TriggerAudit audit, CancellationToken ct = default);
}
