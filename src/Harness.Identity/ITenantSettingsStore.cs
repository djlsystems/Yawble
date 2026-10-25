namespace Harness.Identity;

/// <summary>
/// THE INSTANCE-WIDE SETTINGS ROWS (<c>tenant_settings</c>). A change and its <c>tenant_events</c> row
/// land in one transaction, so a setting never changes without a record of who changed it. See
/// <see cref="SqliteTenantSettingsStore"/>.
/// </summary>
public interface ITenantSettingsStore
{
    Task<IReadOnlyList<TenantSettingRow>> ReadAllAsync(CancellationToken ct = default);

    /// <summary>Writes every change and one tenant row per change, atomically. A null new value removes
    /// the row; a reset of a setting with no row changes nothing and is not returned.</summary>
    Task<IReadOnlyList<TenantSettingWritten>> WriteAsync(
        IReadOnlyList<TenantSettingChange> changes, string? actorId, string actorEmail, CancellationToken ct = default);
}
