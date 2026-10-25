using Harness.Host;
using Harness.Host.SqlServer;

namespace Harness.Tests;

/// <summary>
/// A SQLSERVER START THE STORE GUARD REFUSES LEAVES THE DATABASE AS IT FOUND IT: the start steps
/// (<c>instance.id</c>, the ledger's and the backlog's back-fills) are chosen by <see cref="StartRows"/>,
/// which answers none for SQL Server while any required store lacks a SQL Server version, so a build that
/// cannot serve the database runs no step that writes into it.
///
/// <para>
/// Every store has a SQL Server version now, so the Host itself is never refused for one; the choice is
/// asked of a registry with a store taken out, the state of a build the guard refuses.
/// </para>
/// </summary>
public sealed class SqlServerStartGuardTests
{
    private const string Connection = "Server=unused;Database=unused";

    private static readonly DatabaseOptions SqlServer = new(DatabaseProvider.SqlServer, Connection);

    [Fact]
    public void A_refused_sqlserver_start_writes_no_start_rows()
    {
        var refused = SqlServerStoreRegistry.Registered.Where(type => type != typeof(IConnectionStore)).ToList();
        Assert.Equal(["IConnectionStore"], StoreGuard.Missing(StoreContracts.Required, refused));

        var (ledger, outcome) = StartRows.For(SqlServer, "unused.db", refused);

        Assert.Null(ledger);
        Assert.Null(outcome);
    }

    [Fact]
    public void A_sqlserver_start_with_every_store_runs_the_sql_server_start_rows()
    {
        var (ledger, outcome) = StartRows.For(SqlServer, "unused.db", SqlServerStoreRegistry.Registered);

        Assert.IsType<SqlServerLedgerStartRows>(ledger);
        Assert.IsType<SqlServerOutcomeStartRows>(outcome);
    }

    [Fact]
    public void A_sqlite_start_runs_the_sqlite_start_rows_whatever_sql_server_lacks()
    {
        var (ledger, outcome) = StartRows.For(new DatabaseOptions(DatabaseProvider.Sqlite, null), "messages.db", []);

        Assert.IsType<SqliteLedgerStartRows>(ledger);
        Assert.IsType<SqliteOutcomeStartRows>(outcome);
    }

    [Fact]
    public void A_sqlserver_start_with_no_connection_runs_none()
    {
        var (ledger, outcome) = StartRows.For(new DatabaseOptions(DatabaseProvider.SqlServer, null), "unused.db", SqlServerStoreRegistry.Registered);

        Assert.Null(ledger);
        Assert.Null(outcome);
    }
}
