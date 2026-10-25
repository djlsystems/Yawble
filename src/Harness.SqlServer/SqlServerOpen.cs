using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer;

/// <summary>
/// THE ONE CONNECTION STRING EVERY SQL SERVER STORE OPENS WITH, and the one retry around a call
/// that can meet a transient fault.
///
/// <para>
/// The SQLite stores open unpooled because a file handle is process state; SQL Server is the
/// opposite, where a pooled connection is the cheap path and an unpooled one costs a login per call.
/// So pooling is on, and the driver is told to reconnect (<c>ConnectRetryCount=3</c>) when a pooled
/// connection turns out to be dead, which a failover or a firewall idle-timeout makes routine.
/// Either is left alone when the operator's own string sets it - their word on their server wins.
/// </para>
///
/// <para>
/// Managed identity is the connection string's own <c>Authentication=...</c>; nothing here reads a
/// credential.
/// </para>
/// </summary>
public static class SqlServerOpen
{
    private const int Attempts = 3;

    /// <summary>
    /// The configured string with retry and pooling defaults added where it does not say. Checked
    /// against the raw keys rather than the typed builder, whose indexer answers a default for a key
    /// nobody wrote and so cannot tell "unset" from "set to the default".
    /// </summary>
    public static string ConnectionString(string configured)
    {
        var written = new DbConnectionStringBuilder { ConnectionString = configured };
        var builder = new SqlConnectionStringBuilder(configured);

        if (!written.ContainsKey("ConnectRetryCount") && !written.ContainsKey("Connect Retry Count"))
        {
            builder.ConnectRetryCount = 3;
        }

        if (!written.ContainsKey("Pooling"))
        {
            builder.Pooling = true;
        }

        return builder.ConnectionString;
    }

    /// <summary>SQL Server's own error number for <paramref name="exception"/>, or null when it is not a
    /// SQL Server error: what a sentence naming a database fault says on this provider.</summary>
    public static int? ErrorNumber(Exception exception) => exception is SqlException sql ? sql.Number : null;

    /// <summary>The error number SQL Server gives a deadlock victim: its transaction was rolled back
    /// and running the work again is the documented remedy.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>
    /// Runs <paramref name="work"/>, trying again (three attempts in all) when SQL Server chose it as
    /// a deadlock victim (error 1205) or answers with a fault the driver marks transient (a failover
    /// in progress, a throttled or briefly unavailable database). Anything else, and the last
    /// retryable one, propagates unchanged.
    ///
    /// Whole units of work only: the caller passes a call that opens its own connection and, if it
    /// writes, does so in one transaction, so running it again cannot apply half of it twice.
    /// </summary>
    public static Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) =>
        RetryAsync(work, ct, Attempts, IsRetryable, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// ONE UNIT OF WORK ON ITS OWN POOLED CONNECTION, retried whole by <see cref="RetryAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>:
    /// the shape every SQL Server store's call takes, here once so no store keeps its own copy.
    /// <paramref name="connectionString"/> is the store's, already through <see cref="ConnectionString"/>.
    /// </summary>
    public static Task<T> RunAsync<T>(
        string connectionString, Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        RetryAsync(
            async token =>
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(token);
                return await work(connection, token);
            },
            ct);

    /// <summary>One statement as one unit of work: the rows it changed.</summary>
    public static Task<int> ExecuteAsync(
        string connectionString, string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        RunAsync(
            connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind(command);
                return await command.ExecuteNonQueryAsync(token);
            },
            ct);

    private static bool IsRetryable(Exception exception) =>
        exception is SqlException { Number: DeadlockVictim } or SqlException { IsTransient: true };

    /// <summary>
    /// The loop with its policy as arguments, so a test can pin the attempt cap and the
    /// give-up-at-once behaviour without waiting out real delays. The public overload is the only
    /// caller outside tests and fixes all three.
    /// </summary>
    internal static async Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> work, CancellationToken ct,
        int attempts, Func<Exception, bool> retryable, TimeSpan delayStep)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await work(ct);
            }
            catch (Exception exception) when (retryable(exception) && attempt < attempts)
            {
                await Task.Delay(delayStep * attempt, ct);
            }
        }
    }
}
