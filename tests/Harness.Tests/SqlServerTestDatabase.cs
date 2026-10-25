using Harness.SqlServer;
using Microsoft.Data.SqlClient;

namespace Harness.Tests;

/// <summary>
/// A throwaway SQL Server database, created with the collation the product requires and dropped on
/// dispose, so each test owns its own schema and none sees another's tables.
///
/// The server comes from <c>HARNESS_TEST_SQLSERVER</c> (a login that may create databases, pointed at
/// master). Unset, the test is SKIPPED rather than failed: a developer without a SQL Server still
/// gets a green run of everything else, and the container script is what guarantees the variable is
/// set where the suite is a gate.
/// </summary>
internal sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private const string Variable = "HARNESS_TEST_SQLSERVER";

    private readonly string _master;
    private readonly string _name;

    private SqlServerTestDatabase(string master, string name, string connectionString)
    {
        _master = master;
        _name = name;
        ConnectionString = connectionString;
    }

    /// <summary>A connection string for the new database.</summary>
    public string ConnectionString { get; }

    /// <summary>The database's generated name.</summary>
    public string Name => _name;

    /// <summary>
    /// Creates the database and, unless <paramref name="applyModules"/> is false, applies every
    /// SQL Server schema module to it. <paramref name="collation"/> exists so the refusal of a
    /// case-insensitive database can be tested; every other caller takes the default.
    /// <paramref name="readCommittedSnapshot"/> is on, as the migrator requires and Azure SQL Database
    /// runs by default; off exists only so the migrator's refusal of it can be tested.
    /// </summary>
    public static async Task<SqlServerTestDatabase> CreateAsync(
        CancellationToken ct = default,
        string collation = SqlServerMigrator.RequiredCollation,
        bool applyModules = true,
        bool readCommittedSnapshot = true)
    {
        var master = Environment.GetEnvironmentVariable(Variable);

        if (string.IsNullOrWhiteSpace(master))
        {
            Assert.Skip($"{Variable} is not set");
        }

        var name = $"harness_test_{Guid.NewGuid():N}";

        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync(ct);
            await using var create = connection.CreateCommand();
            // The name is generated here from a GUID and the collation is a constant or a literal
            // from a test, neither of which can carry a quote; DDL takes no parameters anyway.
            create.CommandText = $"CREATE DATABASE [{name}] COLLATE {collation}";
            await create.ExecuteNonQueryAsync(ct);

            // Nothing is connected to the new database yet, so the change needs no rollback of others.
            if (readCommittedSnapshot)
            {
                create.CommandText = $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON";
                await create.ExecuteNonQueryAsync(ct);
            }
        }

        var builder = new SqlConnectionStringBuilder(master) { InitialCatalog = name };
        var database = new SqlServerTestDatabase(master, name, SqlServerOpen.ConnectionString(builder.ConnectionString));

        if (applyModules)
        {
            await new SqlServerMigrator(database.ConnectionString).ApplyAsync(SqlServerSchemaModules.All, ct);
        }

        return database;
    }

    public async ValueTask DisposeAsync()
    {
        // The pool would keep a connection to the database open and make the drop wait or fail.
        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText =
            $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];";
        await drop.ExecuteNonQueryAsync();
    }
}
