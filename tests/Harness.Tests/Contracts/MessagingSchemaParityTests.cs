namespace Harness.Tests.Contracts;

/// <summary>
/// THE MESSAGE LOG'S TABLES HAVE THE SAME COLUMNS ON BOTH PROVIDERS: SQLite after every
/// <c>messages-</c> step, SQL Server after <c>messages.sqlserver-baseline-1</c>.
/// </summary>
public sealed class MessagingSchemaParityTests
{
    /// <summary>Every table of the messaging module's steps.</summary>
    internal static readonly IReadOnlyList<string> Tables =
        ["messages", "cursors", "subscriptions", "pending_deliveries", "wakes", "file_changes", "run_steps"];

    [Fact]
    public async Task Tables_match()
    {
        await using var sqlServer = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var sqlite = new SqliteContractDatabase();

        await ProviderSchemaComparison.AssertSameColumnsAsync(sqlite.Path, sqlServer.ConnectionString, Tables);
    }
}
