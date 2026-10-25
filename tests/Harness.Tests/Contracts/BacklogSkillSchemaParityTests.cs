namespace Harness.Tests.Contracts;

/// <summary>
/// THE SKILL AND BACKLOG MODULES' TABLES HAVE THE SAME COLUMNS ON BOTH PROVIDERS: SQLite after every
/// <c>skill-</c> and <c>backlog-</c> step, SQL Server after <c>skill.sqlserver-baseline-1</c> and
/// <c>backlog.sqlserver-baseline-1</c>. The list is every table the two stores use, so a table one of
/// them needs and a baseline forgot fails here by name. The repository keys SQL Server alone has are
/// exempt by name (<see cref="ProviderSchemaComparison.SqlServerOnlyKeys"/>).
/// </summary>
public sealed class BacklogSkillSchemaParityTests
{
    internal static readonly IReadOnlyList<string> Tables =
    [
        "skills", "backlog_id_sequence", "backlog_items", "backlog_dispatches", "backlog_dispatch_tips",
        "backlog_dispatch_bases", "backlog_dispatch_missed_starts",
    ];

    [Fact]
    public async Task Tables_match()
    {
        await using var sqlServer = await SqlServerTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var sqlite = new SqliteContractDatabase();

        await ProviderSchemaComparison.AssertSameColumnsAsync(sqlite.Path, sqlServer.ConnectionString, Tables);
    }
}
