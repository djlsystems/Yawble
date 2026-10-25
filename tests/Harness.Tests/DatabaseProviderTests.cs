using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// WHICH DATABASE THE HOST RUNS ON, and the refusal to start on one it cannot yet serve completely.
/// SQLite is the default and the reference; <c>sqlserver</c> is chosen by configuration and names every
/// store that has no SQL Server version rather than starting half-way.
/// </summary>
[Collection(ProcessConsoleCollection.Name)]
public sealed class DatabaseProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    [Fact]
    public void Sqlite_is_the_default()
    {
        var options = DatabaseOptions.From(Config());

        Assert.Equal(DatabaseProvider.Sqlite, options.Provider);
        Assert.Null(options.Connection);
    }

    [Theory]
    [InlineData("Database")]
    [InlineData("HARNESS_DATABASE")]
    public void The_provider_is_read_from_either_key_in_any_case(string key)
    {
        var options = DatabaseOptions.From(Config((key, "SqlServer"), ("DatabaseConnection", "Server=x")));

        Assert.Equal(DatabaseProvider.SqlServer, options.Provider);
        Assert.Equal("Server=x", options.Connection);
        Assert.Equal(DatabaseProvider.Sqlite, DatabaseOptions.From(Config((key, "SQLITE"))).Provider);
    }

    [Fact]
    public void An_unknown_provider_is_named()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => DatabaseOptions.From(Config(("HARNESS_DATABASE", "postgres"))));

        Assert.Contains("postgres", refused.Message);
        Assert.Contains("sqlite", refused.Message);
        Assert.Contains("sqlserver", refused.Message);
    }

    [Fact]
    public void Sqlserver_needs_a_connection()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => DatabaseOptions.From(Config(("HARNESS_DATABASE", "sqlserver"))));

        Assert.Contains("HARNESS_DATABASE_CONNECTION", refused.Message);
        Assert.Equal("Server=y", DatabaseOptions.From(Config(
            ("HARNESS_DATABASE", "sqlserver"), ("HARNESS_DATABASE_CONNECTION", "Server=y"))).Connection);
    }

    [Fact]
    public void The_guard_names_exactly_the_missing_stores_in_a_stable_order()
    {
        Type[] required = [typeof(IMessageLog), typeof(ICursors), typeof(ISubscriptions), typeof(IUsageLedger)];

        var refused = Assert.Throws<StoresMissingException>(
            () => StoreGuard.Check(required, [typeof(ICursors), typeof(IUsageLedger)]));

        Assert.Equal(["IMessageLog", "ISubscriptions"], refused.Names);
        Assert.Equal(
            "This build cannot run on SQL Server yet: these stores have no SQL Server version: IMessageLog, ISubscriptions.",
            refused.Message);

        StoreGuard.Check(required, required);
    }

    [Fact]
    public void Every_required_store_is_a_distinct_interface()
    {
        Assert.All(StoreContracts.Required, type => Assert.True(type.IsInterface, type.Name));
        Assert.Equal(StoreContracts.Required.Count, StoreContracts.Required.Distinct().Count());
        Assert.Contains(typeof(IMessageLog), StoreContracts.Required);
        Assert.Contains(typeof(IPluginMemberSettingsStore), StoreContracts.Required);
    }

    /// <summary>EVERY STORE THE HOST RESOLVES HAS A SQL SERVER VERSION: the guard's list is complete.</summary>
    [Fact]
    public void Every_required_store_has_a_sql_server_version()
    {
        Assert.Empty(StoreGuard.Missing(StoreContracts.Required, SqlServerStoreRegistry.Registered));
        Assert.Equal(SqlServerStoreRegistry.Registered.Count, SqlServerStoreRegistry.Registered.Distinct().Count());
        Assert.Empty(SqlServerStoreRegistry.Registered.Except(StoreContracts.Required));
    }

    /// <summary>
    /// The Host itself, started with <c>sqlserver</c>: while any required store has no SQL Server version
    /// it says which and stops, the stores named being exactly the required ones SQL Server has not
    /// registered, computed here. Once every one has (the stores are complete), the guard passes and the
    /// Host starts on SQL Server and serves from its stores.
    /// </summary>
    [Fact]
    public async Task A_missing_store_is_named_at_start()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct);
        var dataRoot = Path.Combine(Path.GetTempPath(), $"harness-dbprovider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        var missing = StoreContracts.Required
            .Except(SqlServerStoreRegistry.Registered)
            .Select(type => type.Name)
            .ToArray();
        Assert.Equal(missing, StoreGuard.Missing(StoreContracts.Required, SqlServerStoreRegistry.Registered));

        var previous = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        Exception? failure;
        Type? served = null;

        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
                .UseSetting("DataRoot", dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting("ScheduleRunnerEnabled", "false")
                .UseSetting("HARNESS_DATABASE", "sqlserver")
                .UseSetting("HARNESS_DATABASE_CONNECTION", database.ConnectionString));

            failure = Record.Exception(() => served = factory.Services.GetRequiredService<IMessageLog>().GetType());
        }
        finally
        {
            Console.SetError(previous);

            // A refusal sets a non-zero exit code, as it does for a real start; this process is a test run.
            Environment.ExitCode = 0;
            try { Directory.Delete(dataRoot, recursive: true); }
            catch (IOException) { }
        }

        var said = captured.ToString();

        if (missing.Length > 0)
        {
            Assert.IsAssignableFrom<InvalidOperationException>(failure);
            Assert.Contains(
                "This build cannot run on SQL Server yet: these stores have no SQL Server version: "
                + string.Join(", ", missing) + ".",
                said);
            return;
        }

        // COMPLETE: the guard passes, and the Host starts and serves the log from SQL Server.
        StoreGuard.Check(StoreContracts.Required, SqlServerStoreRegistry.Registered);
        Assert.DoesNotContain("these stores have no SQL Server version", said, StringComparison.Ordinal);
        Assert.Null(failure);
        Assert.Equal(typeof(Harness.SqlServer.Messaging.SqlServerMessageStore), served);
    }
}
