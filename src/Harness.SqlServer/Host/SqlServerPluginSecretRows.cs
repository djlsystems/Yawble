using Harness.Contracts;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Host;

/// <summary>
/// The SQL Server version of <see cref="IPluginSecretRows"/>: <c>plugin_secrets</c>, each write with its
/// tenant row in the same transaction through <see cref="SqlServerTenantAuditRow"/>, SQLite's
/// <c>SqlitePluginSecretRows</c> statement for statement. The ciphertext is the text <c>PluginSecretStore</c>
/// sealed, stored and answered unchanged, so a key either provider stored opens through the other.
///
/// <para>
/// A KEY IS MATCHED EXACTLY, its length included (<see cref="SqlServerSql.Same"/>): SQL Server's <c>=</c>
/// pads with spaces, and SQLite's does not. The column is <c>[key]</c>, a reserved word in T-SQL.
/// </para>
///
/// <para>
/// SET READS WHETHER THE KEY IS STORED UNDER <c>UPDLOCK, HOLDLOCK</c>, then writes. SQLite's one writer
/// makes the read and the upsert one step; here two first sets at once would both read "none", both
/// write a <c>secret.set</c> row, and one insert would fail on the key. The held range makes the second
/// wait, read the first's row and write <c>secret.replaced</c>. One table, then <c>tenant_events</c>: the
/// one lock order.
/// </para>
///
/// <para>
/// <see cref="Find"/> is synchronous, as the interface's is (a run's key lookup is), so it opens its own
/// pooled connection without the async retry.
/// </para>
/// </summary>
public sealed class SqlServerPluginSecretRows(string connectionString) : IPluginSecretRows
{
    private static readonly string Match = SqlServerSql.Same("[key]", "@key");

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public StoredSecret? Find(string logicalKey)
    {
        using var connection = new SqlConnection(_connectionString);
        connection.Open();
        using var select = connection.CreateCommand();
        select.CommandText = $"SELECT value_protected, set_by, set_at FROM plugin_secrets WHERE {Match}";
        select.Parameters.AddWithValue("@key", logicalKey);

        using var reader = select.ExecuteReader();

        return reader.Read()
            ? new StoredSecret(reader.GetString(0), reader.GetString(1), Instant.Parse(reader.GetString(2)))
            : null;
    }

    public Task SetAsync(
        string logicalKey, string valueProtected, string setBy, DateTimeOffset at, Func<bool, TriggerAudit> audit,
        CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                bool existed;
                await using (var exists = connection.CreateCommand())
                {
                    exists.Transaction = transaction;
                    exists.CommandText = $"SELECT COUNT(*) FROM plugin_secrets WITH (UPDLOCK, HOLDLOCK) WHERE {Match}";
                    exists.Parameters.AddWithValue("@key", logicalKey);
                    existed = (int)(await exists.ExecuteScalarAsync(token))! > 0;
                }

                await using (var write = connection.CreateCommand())
                {
                    write.Transaction = transaction;
                    write.CommandText = existed
                        ? $"UPDATE plugin_secrets SET value_protected = @value, set_by = @by, set_at = @at WHERE {Match}"
                        : "INSERT INTO plugin_secrets ([key], value_protected, set_by, set_at) VALUES (@key, @value, @by, @at)";
                    write.Parameters.AddWithValue("@key", logicalKey);
                    write.Parameters.AddWithValue("@value", valueProtected);
                    write.Parameters.AddWithValue("@by", setBy);
                    write.Parameters.AddWithValue("@at", Instant.Stamp(at));
                    await write.ExecuteNonQueryAsync(token);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit(existed), token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    public Task ClearAsync(string logicalKey, TriggerAudit audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM plugin_secrets WHERE {Match}";
                    delete.Parameters.AddWithValue("@key", logicalKey);

                    // Nothing stored: nothing changes and no row.
                    if (await delete.ExecuteNonQueryAsync(token) == 0)
                    {
                        await transaction.RollbackAsync(token);
                        return false;
                    }
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);
}
