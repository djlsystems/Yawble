namespace Harness.Tests.Contracts;

/// <summary>
/// THE IDENTITY MODULE'S TABLES THAT <c>auth.sqlserver-baseline-1</c> HOLDS HAVE THE SAME COLUMNS ON
/// BOTH PROVIDERS: SQLite after every <c>auth-</c> step, SQL Server after the baseline. The list is
/// every table the accounts, principals, teams, tenant log, tenant settings and unfinished-removal
/// stores use, so a table one of them needs and the baseline forgot fails here by name.
/// </summary>
public sealed class IdentitySchemaParityTests
{
    internal static readonly IReadOnlyList<string> Tables =
    [
        "users", "principals", "teams", "team_members", "tenant_interactive_agent_settings", "tenant_events",
        "tenant_settings", "team_repo_default_branches", "team_repo_contributors", "unfinished_removals",
    ];

    [Fact]
    public async Task Tables_match()
    {
        await using var sqlServer = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var sqlite = new SqliteContractDatabase();

        await ProviderSchemaComparison.AssertSameColumnsAsync(sqlite.Path, sqlServer.ConnectionString, Tables);
    }
}
