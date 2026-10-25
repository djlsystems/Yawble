using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// EVERY STORE INTERFACE THE HOST RESOLVES, the list a provider must cover before the Host will start
/// on it. In Harness.Host rather than Contracts because one of them (`IPluginMemberSettingsStore`)
/// lives here; the rest are in Contracts.
///
/// `ISecretStore` and `IPluginMemberSecrets` are absent on purpose: they encrypt and are shared by both
/// providers over the rows stores. A new store interface belongs here the moment it exists.
/// </summary>
public static class StoreContracts
{
    public static IReadOnlyList<Type> Required { get; } =
    [
        typeof(IMessageLog),
        typeof(ICursors),
        typeof(ISubscriptions),
        typeof(IUsageLedger),
        typeof(IAdmissionHoldLedger),
        typeof(IOutcomeStore),
        typeof(IPendingDeliveries),
        typeof(IWakeStore),
        typeof(IUserStore),
        typeof(IPrincipalStore),
        typeof(ITeamStore),
        typeof(ITriggerStore),
        typeof(ITenantLog),
        typeof(ISiteStore),
        typeof(IUnfinishedRemovals),
        typeof(ISkillStore),
        typeof(IBacklogStore),
        typeof(IDiagnosticsLog),
        typeof(ILedger),
        typeof(ITeamSolutionStore),
        typeof(IPluginMemberSettingsStore),
    ];
}

/// <summary>
/// The store interfaces that have a SQL Server version. Each task that writes one adds its interface
/// here, beside the registration in `HostComposition.SqlServerStores`. Public and static so a test can
/// compute what a start must report; a store registered but not listed is only ever refused too
/// eagerly, never started half-served.
/// </summary>
public static class SqlServerStoreRegistry
{
    public static IReadOnlyList<Type> Registered { get; } = [];
}

/// <summary>A start on SQL Server that lacks stores: the sentence names them.</summary>
public sealed class StoresMissingException(IReadOnlyList<string> names)
    : InvalidOperationException(
        $"This build cannot run on SQL Server yet: these stores have no SQL Server version: {string.Join(", ", names)}.")
{
    public IReadOnlyList<string> Names { get; } = names;
}

public static class StoreGuard
{
    /// <summary>Throws <see cref="StoresMissingException"/> naming, in the order of
    /// <paramref name="required"/>, each required interface not in <paramref name="registered"/>.</summary>
    public static void Check(IReadOnlyList<Type> required, IReadOnlyCollection<Type> registered)
    {
        var missing = required.Where(type => !registered.Contains(type)).Select(type => type.Name).ToList();

        if (missing.Count > 0) throw new StoresMissingException(missing);
    }
}
