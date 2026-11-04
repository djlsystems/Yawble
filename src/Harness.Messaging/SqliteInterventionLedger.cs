using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>The SQLite <see cref="IInterventionLedger"/>: one act in a transaction of its own, through
/// <see cref="InterventionLedgerRow"/>, in the log's own file.</summary>
public sealed class SqliteInterventionLedger(string databasePath) : IInterventionLedger
{
    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    public async Task RecordAsync(InterventionAct act, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        SqlitePragmas.Apply(connection);

        await using var transaction = await connection.BeginTransactionAsync(ct);
        await InterventionLedgerRow.AppendAsync(connection, transaction, act, act.Correlation, null, at, ct);
        await transaction.CommitAsync(ct);
    }
}
