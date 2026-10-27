using Harness.Contracts;

namespace Harness.Host;

/// <summary>A member's stored field as its row holds it: the Data Protection ciphertext, who set it and when.</summary>
public sealed record MemberSecretRow(string Field, string ValueProtected, string SetBy, DateTimeOffset SetAt);

/// <summary>
/// A member's named-secret changes as the rows take them: each name to its ciphertext (null removes),
/// and the tenant row each answers for (name, action). Sealed by <see cref="PluginMemberSecretStore"/>.
/// </summary>
public sealed record ProtectedNamedChanges(IReadOnlyDictionary<string, string?> Changes, Func<string, string, TriggerAudit> Audit);

/// <summary>
/// THE <c>plugin_member_secrets</c> AND <c>plugin_member_named_secrets</c> ROWS, and nothing else. ENCRYPTION IS NOT HERE:
/// `PluginMemberSecretStore` protects a value before it reaches these rows and unprotects what they
/// return, so both providers share one cipher and one purpose. Every change lands with its tenant rows
/// in the same transaction, or not at all.
/// </summary>
public interface IPluginMemberSecretRows
{
    /// <summary>Every field the member has a row for.</summary>
    Task<IReadOnlyList<MemberSecretRow>> RowsAsync(ContainerId member, CancellationToken ct = default);

    /// <summary>
    /// Every change (a ciphertext sets or replaces, null clears) in field order, each with the tenant row
    /// <paramref name="audit"/> answers for (field, action), in one transaction. A clear of a field with
    /// no row changes nothing and writes no row.
    /// </summary>
    Task ChangeAsync(
        ContainerId member, IReadOnlyDictionary<string, string?> protectedChanges, string setBy,
        Func<string, string, TriggerAudit> audit, CancellationToken ct = default);

    /// <summary>A settings save and its values together: the member's settings row, every given tenant
    /// row, every change with its own row and every named-secret change (<paramref name="named"/>) with
    /// its own row, in ONE transaction, or nothing.</summary>
    Task SaveWithSettingsAsync(
        ContainerId member, PluginMemberSettings settings, IReadOnlyList<TriggerAudit> audits,
        IReadOnlyDictionary<string, string?> protectedChanges, string setBy,
        Func<string, string, TriggerAudit> audit, CancellationToken ct = default, ProtectedNamedChanges? named = null);

    /// <summary>Every named secret the member holds (<c>Field</c> is the name, as a person spelled it).</summary>
    Task<IReadOnlyList<MemberSecretRow>> NamedRowsAsync(ContainerId member, CancellationToken ct = default);

    /// <summary>
    /// Every named-secret change (a ciphertext sets or replaces, null removes), each with its tenant row,
    /// in one transaction. A name matches ignoring case. Removing a name not held changes nothing and
    /// writes no row.
    /// </summary>
    Task ChangeNamedAsync(ContainerId member, ProtectedNamedChanges named, string setBy, CancellationToken ct = default);

    /// <summary>
    /// The one-time start step: every member field bound to a stored key (`plugin_secrets`) that has no
    /// value of its own takes that key's value, and the step's tenant row (<paramref name="done"/>,
    /// given the member, field and key counts) marks it done in the same transaction. Answers false,
    /// copying nothing, once the mark is there. <paramref name="unprotectKey"/> opens a stored key's
    /// ciphertext (null when it no longer opens); <paramref name="protectForMember"/> seals it for a member.
    /// </summary>
    Task<bool> CopyStoredKeysOnceAsync(
        Func<string, string?> unprotectKey, Func<string, string> protectForMember,
        Func<int, int, int, TriggerAudit> done, CancellationToken ct = default);
}
