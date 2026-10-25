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

    /// <summary>
    /// Runs <paramref name="work"/>, trying again when SQL Server answers with a fault it marks
    /// transient (a failover in progress, a throttled or briefly unavailable database). Anything else,
    /// and the last transient one, propagates unchanged.
    ///
    /// Whole units of work only: the caller passes a call that opens its own connection and, if it
    /// writes, does so in one transaction, so running it again cannot apply half of it twice.
    /// </summary>
    public static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await work(ct);
            }
            catch (SqlException exception) when (exception.IsTransient && attempt < Attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }
    }
}
