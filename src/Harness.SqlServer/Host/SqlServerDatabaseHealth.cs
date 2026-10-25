using System.Data.Common;
using Harness.Contracts;

namespace Harness.SqlServer.Host;

/// <summary>
/// The SQL Server version of <see cref="IDatabaseHealth"/>: the database opens and answers
/// <c>SELECT 1</c>, through <see cref="SqlServerOpen"/> as every store's call is. A database that cannot
/// be reached answers false, never a throw, as the SQLite file that cannot be opened does: a probe
/// reports a failure, it does not create or repair anything.
/// </summary>
public sealed class SqlServerDatabaseHealth(string connectionString) : IDatabaseHealth
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await SqlServerOpen.RunAsync(
                _connectionString,
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 1";
                    return await command.ExecuteScalarAsync(token);
                },
                ct);

            return true;
        }
        catch (Exception ex) when (ex is DbException or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
