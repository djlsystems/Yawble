using Harness.Contracts;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="IDatabaseTrouble"/>: SQLite's two kinds, in SQL Server's numbers.
///
/// <para>
/// A DEADLOCK IS THE BUSY DATABASE: two writers each holding what the other needs, one chosen as the
/// victim (1205). The stores already retry a victim (<see cref="SqlServerOpen.RetryAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>),
/// so one that reaches a request is contention that outlasted the retries.
/// </para>
///
/// <para>
/// A WAIT THAT RAN OUT IS THE TIMEOUT EXPIRING, as SQLite's <c>busy_timeout</c> running out is: a lock
/// request that waited past <c>LOCK_TIMEOUT</c> (1222), or a command that waited past its command
/// timeout (SqlClient's -2). Either means something held the database for seconds.
/// </para>
///
/// <para>Walks the inner chain, as SQLite's does: a fault reaching a route is usually wrapped.</para>
/// </summary>
public sealed class SqlServerTrouble : IDatabaseTrouble
{
    /// <summary>The transaction was chosen as a deadlock victim.</summary>
    public const int Deadlock = 1205;

    /// <summary>A lock request waited past <c>SET LOCK_TIMEOUT</c>.</summary>
    public const int LockTimeout = 1222;

    /// <summary>SqlClient's own number for a command that ran past its command timeout.</summary>
    public const int CommandTimeout = -2;

    public string? KindOf(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SqlException sql) continue;

            if (sql.Number is LockTimeout or CommandTimeout) return DiagnosticKinds.DatabaseBusyTimeoutExpired;
            if (sql.Number == Deadlock) return DiagnosticKinds.DatabaseBusy;
        }

        return null;
    }
}
