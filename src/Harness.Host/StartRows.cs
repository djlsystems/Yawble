using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE ONE PLACE THE PROVIDER CHOOSES THE START STEPS' ROWS: each provider's own, so <c>instance.id</c>
/// and both back-fills run wherever the Host runs. SQL Server's are handed the configured connection
/// string (<see cref="DatabaseOptions"/>), not the SQLite file's path; a provider with neither answers
/// none and both steps are skipped.
///
/// <para>
/// NOT WHILE THE STORE GUARD WOULD REFUSE THE START. The guard runs later, with the stores; a build
/// missing any SQL Server store must leave the database exactly as it found it, not write
/// <c>instance.id</c> and back-fill rows into one it then refuses to serve. So the SQL Server arm asks the
/// guard's own question of <paramref name="registered"/> first: the Host passes
/// <see cref="SqlServerStoreRegistry.Registered"/>, a test any other set.
/// </para>
/// </summary>
public static class StartRows
{
    public static (ILedgerStartRows? Ledger, IOutcomeStartRows? Outcome) For(
        DatabaseOptions options, string database, IReadOnlyCollection<Type> registered) =>
        options.Provider switch
        {
            DatabaseProvider.Sqlite => (new SqliteLedgerStartRows(database), new SqliteOutcomeStartRows(database)),
            DatabaseProvider.SqlServer when options.Connection is { } connection
                && StoreGuard.Missing(StoreContracts.Required, registered).Count == 0 =>
                (new SqlServer.SqlServerLedgerStartRows(connection), new SqlServer.SqlServerOutcomeStartRows(connection)),
            _ => (null, null),
        };
}
