using Harness.Contracts;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Host;

/// <summary>
/// The SQL Server version of <see cref="IPluginRemovalRows"/>: the tenant row in a transaction that stays
/// open across the folder move, as SQLite's <c>SqlitePluginRemovalRows</c> keeps its own. The row and the
/// move land together or neither does.
///
/// <para>
/// NOT RETRIED. The unit includes the caller's file move, which is not a database write a second attempt
/// could repeat; a fault before the move leaves nothing done, and one at the commit moves the folder back
/// (the interface's contract), as on SQLite. The open transaction holds only its own new
/// <c>tenant_events</c> row, so nothing else waits on the move.
/// </para>
/// </summary>
public sealed class SqlServerPluginRemovalRows(string connectionString) : IPluginRemovalRows
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public async Task<bool> RecordWhileAsync(TriggerAudit row, Func<bool> moveFiles, Action undoMove, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        await SqlServerTenantAuditRow.AppendAsync(connection, transaction, row, ct);

        // The transaction is disposed uncommitted when the move fails.
        if (!moveFiles()) return false;

        try
        {
            await transaction.CommitAsync(ct);
        }
        catch
        {
            undoMove();
            throw;
        }

        return true;
    }
}
