using Harness.Contracts;

namespace Harness.Host;

internal sealed partial class HostComposition
{
    /// <summary>
    /// THE SQL SERVER STORES, registered one module at a time as each is written. Each registration
    /// adds its interface to <see cref="SqlServerStoreRegistry.Registered"/> beside its
    /// `AddSingleton`; the guard at the end then refuses to start while any interface the Host
    /// resolves (<see cref="StoreContracts.Required"/>) is still without one.
    /// </summary>
    private void SqlServerStores()
    {
        StoreGuard.Check(StoreContracts.Required, SqlServerStoreRegistry.Registered);
    }
}
