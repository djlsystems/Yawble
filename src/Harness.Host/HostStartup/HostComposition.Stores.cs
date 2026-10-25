using Harness.Contracts;

namespace Harness.Host;

internal sealed partial class HostComposition
{
    /// <summary>
    /// THE STORES, from the provider the configuration chose (SQLite unless `Database` says otherwise).
    /// A SQL Server start that lacks any store's SQL Server version is REFUSED WITH A SENTENCE naming
    /// them, printed and exited non-zero like the schema refusals: a half-served instance would fail at
    /// the first request that reached the missing store, far from the setting that caused it.
    /// </summary>
    internal async Task<bool> StoresAsync()
    {
        if (databaseOptions.Provider == DatabaseProvider.Sqlite) return await SqliteStoresAsync();

        try
        {
            SqlServerStores();
            return true;
        }
        catch (StoresMissingException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
            return false;
        }
    }
}
