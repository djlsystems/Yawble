namespace Harness.Tests.Contracts;

/// <summary>
/// THE OUTCOME MODULE'S TABLES HAVE THE SAME COLUMNS ON BOTH PROVIDERS: SQLite after every
/// <c>outcome-</c> step, SQL Server after <c>outcome.sqlserver-baseline-1</c>.
/// </summary>
public sealed class OutcomeSchemaParityTests
{
    /// <summary>Every table of the outcome module's steps.</summary>
    internal static readonly IReadOnlyList<string> Tables =
    [
        "usage_ledger", "workflow_ledger", "outcomes", "workflow_outcome_links", "delivery_ledger", "nudge_ledger",
        "admission_holds",
    ];

    [Fact]
    public async Task Tables_match()
    {
        await using var sqlServer = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var sqlite = new SqliteContractDatabase();

        await ProviderSchemaComparison.AssertSameColumnsAsync(sqlite.Path, sqlServer.ConnectionString, Tables);
    }
}
