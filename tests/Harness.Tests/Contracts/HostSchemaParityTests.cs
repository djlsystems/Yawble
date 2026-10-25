namespace Harness.Tests.Contracts;

/// <summary>
/// THE HOST'S PLUGIN AND CONNECTION TABLES THAT <c>auth.sqlserver-baseline-3</c> HOLDS HAVE THE SAME
/// COLUMNS ON BOTH PROVIDERS: SQLite after every <c>auth-</c> step, SQL Server after the baselines. The
/// list is every table the member settings, member secrets, plugin key and connection stores use beyond
/// the earlier baselines', so a table one of them needs and the step forgot fails here by name. The
/// Monitor's <c>file_changes</c> and <c>run_steps</c> are the messaging baseline's, compared there.
/// </summary>
public sealed class HostSchemaParityTests
{
    internal static readonly IReadOnlyList<string> Tables =
    [
        "team_member_config", "plugin_member_secrets", "plugin_secrets",
        "oauth_providers", "connections", "connection_flows", "imap_connections",
    ];

    [Fact]
    public async Task Tables_match()
    {
        await using var sqlServer = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var sqlite = new SqliteContractDatabase();

        await ProviderSchemaComparison.AssertSameColumnsAsync(sqlite.Path, sqlServer.ConnectionString, Tables);
    }
}
