using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace Harness.Tests;

/// <summary>
/// A SQLSERVER START THE STORE GUARD REFUSES LEAVES THE DATABASE AS IT FOUND IT: the start steps
/// (<c>instance.id</c>, the ledger's and the backlog's back-fills) run only once every store has a SQL
/// Server version, so a build that cannot serve the database writes nothing into it first.
/// </summary>
public sealed class SqlServerStartGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_refused_sqlserver_start_writes_no_start_rows()
    {
        Assert.NotEmpty(StoreGuard.Missing(StoreContracts.Required, SqlServerStoreRegistry.Registered));

        await using var database = await SqlServerTestDatabase.CreateAsync(Ct);

        // The two tables the ledger's start writes, so a start that ran its steps would leave rows here.
        await ExecuteAsync(database.ConnectionString,
            """
            CREATE TABLE tenant_settings (
                name NVARCHAR(450) NOT NULL PRIMARY KEY, value NVARCHAR(MAX) NOT NULL,
                updated_at NVARCHAR(40) NOT NULL, updated_by NVARCHAR(MAX) NOT NULL);
            CREATE TABLE tenant_events (
                seq BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, occurred_at NVARCHAR(40) NOT NULL,
                actor_id NVARCHAR(MAX) NULL, actor_email NVARCHAR(MAX) NULL, action NVARCHAR(450) NOT NULL,
                subject NVARCHAR(450) NULL, subject_name NVARCHAR(MAX) NULL, detail NVARCHAR(MAX) NULL);
            """);

        var dataRoot = Path.Combine(Path.GetTempPath(), $"harness-startguard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
                .UseSetting("DataRoot", dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting("ScheduleRunnerEnabled", "false")
                .UseSetting("HARNESS_DATABASE", "sqlserver")
                .UseSetting("HARNESS_DATABASE_CONNECTION", database.ConnectionString));

            Assert.ThrowsAny<InvalidOperationException>(() => _ = factory.Services);
        }
        finally
        {
            // The refusal sets a non-zero exit code, as it does for a real start; this process is a test run.
            Environment.ExitCode = 0;
            try { Directory.Delete(dataRoot, recursive: true); }
            catch (IOException) { }
        }

        Assert.Equal(0, await CountAsync(database.ConnectionString, "SELECT COUNT(*) FROM tenant_settings"));
        Assert.Equal(0, await CountAsync(database.ConnectionString, "SELECT COUNT(*) FROM tenant_events"));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<int> CountAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct));
    }
}
