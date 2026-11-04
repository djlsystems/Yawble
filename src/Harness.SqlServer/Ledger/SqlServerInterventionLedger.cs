using Harness.Contracts;

namespace Harness.SqlServer.Ledger;

/// <summary>The SQL Server <see cref="IInterventionLedger"/>: one act in a transaction of its own, through
/// <see cref="SqlServerInterventionLedgerRow"/>, retried whole as every SQL Server store's call is.</summary>
public sealed class SqlServerInterventionLedger(string connectionString) : IInterventionLedger
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task RecordAsync(InterventionAct act, DateTimeOffset at, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                await SqlServerInterventionLedgerRow.AppendAsync(connection, transaction, act, act.Correlation, null, at, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return 0;
            },
            ct);
}
