using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Messaging;

/// <summary>
/// THE ONE APPLICATION LOCK EVERY WRITE TO THE LOG'S ROWS TAKES: each append (so seqs commit in the
/// order they are allocated - see <see cref="SqlServerMessageStore"/>), the log's one delete, and the
/// ledger's backfill at start. SQLite has one writer at a time and every reader of the log relies on
/// it; this is that writer, for the statements that need it.
/// </summary>
public static class SqlServerAppendLock
{
    /// <summary>The lock's name, the same for every taker.</summary>
    public const string Resource = "messages.append";

    /// <summary>
    /// TAKES THE APPEND LOCK for the rest of <paramref name="transaction"/>. Re-entrant: a second take
    /// in the same transaction is granted at once, so an insert can take it whether or not its caller
    /// already has. It is the first lock a taker takes, so a transaction waiting on it holds nothing
    /// another could be waiting for, and it cannot be part of a deadlock.
    /// </summary>
    public static async Task TakeAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            DECLARE @granted INT;
            EXEC @granted = sp_getapplock @Resource = N'{Resource}', @LockMode = N'Exclusive', @LockOwner = N'Transaction';
            IF @granted < 0 THROW 51000, N'The message log could not take its append lock.', 1;
            """;

        await command.ExecuteNonQueryAsync(ct);
    }
}
