using Harness.Contracts;
using Harness.SqlServer;
using Microsoft.Data.SqlClient;

namespace Harness.Host.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="IPluginSecretMoveRows"/>: SQLite's
/// <see cref="SqlitePluginSecretMoveRows"/> statement for statement, in one transaction. LOCK ORDER as
/// <see cref="SqlServerPluginMemberSecretRows"/> takes it: the <c>teams</c> row, the <c>team_members</c>
/// row, the settings row (read under <c>UPDLOCK, HOLDLOCK</c>), the connection, the secret rows, then
/// <c>tenant_events</c>. The connection's name is checked under the lock its store takes.
/// </summary>
public sealed class SqlServerPluginSecretMoveRows(string connectionString) : IPluginSecretMoveRows
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task<string?> MoveAsync(SecretMove move, CancellationToken ct = default)
    {
        var settings = move.Connection.Credential ?? throw new ArgumentException("A moved secret makes a credential connection.", nameof(move));

        return SqlServerOpen.RunAsync<string?>(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await SqlServerPluginMemberSettings.LockMemberAsync(connection, transaction, move.Member, token);

                var stored = PluginMemberSettings.None;
                await using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText =
                        $"SELECT config_json, secrets_json, connections_json FROM team_member_config WITH (UPDLOCK, HOLDLOCK) WHERE {SqlServerPluginMemberSettings.MemberMatch}";
                    SqlServerPluginMemberSettings.BindMember(read, move.Member);
                    await using var reader = await read.ExecuteReaderAsync(token);
                    if (await reader.ReadAsync(token)) stored = PluginMemberSettingsRows.Read(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                }

                // ALREADY BOUND, a slot that takes one: nothing is written (disposed uncommitted).
                if (PluginSecretMoves.FreeKey(stored.Connections, move.Slot, move.Multiple) is not { } key) return null;

                await using (var names = connection.CreateCommand())
                {
                    names.Transaction = transaction;
                    names.CommandText = "SELECT name FROM connections WITH (UPDLOCK, HOLDLOCK)";
                    var taken = ConnectionRows.NameKey(move.Connection.Name);
                    await using var reader = await names.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        if (ConnectionRows.NameKey(reader.GetString(0)) == taken) throw new ConnectionNameTakenException(move.Connection.Name);
                    }
                }

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO connections (id, name, provider, account, scopes, connected_at, refreshed_at, status, status_reason, connected_by)
                        VALUES (@id, @name, @provider, @account, '[]', @at, NULL, 'ok', NULL, @by)
                        """;
                    command.Parameters.AddWithValue("@id", move.Connection.Id);
                    command.Parameters.AddWithValue("@name", move.Connection.Name);
                    command.Parameters.AddWithValue("@provider", move.Connection.Provider);
                    command.Parameters.AddWithValue("@account", move.Connection.Account);
                    command.Parameters.AddWithValue("@at", Instant.Stamp(move.Connection.ConnectedAt));
                    command.Parameters.AddWithValue("@by", (object?)move.ConnectedBy ?? DBNull.Value);
                    await command.ExecuteNonQueryAsync(token);
                }

                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO credential_connections (id, preset, fields, secrets_protected) VALUES (@id, @preset, @fields, @secrets)";
                    command.Parameters.AddWithValue("@id", move.Connection.Id);
                    command.Parameters.AddWithValue("@preset", (object?)settings.Preset ?? DBNull.Value);
                    command.Parameters.AddWithValue("@fields", settings.FieldsJson());
                    command.Parameters.AddWithValue("@secrets", move.SealedSecrets);
                    await command.ExecuteNonQueryAsync(token);
                }

                // THE BINDING, which assigns the connection to the member's team in the same write.
                var connections = new Dictionary<string, string>(stored.Connections, StringComparer.Ordinal) { [key] = move.Connection.Id };
                await SqlServerPluginMemberSettings.UpsertAsync(connection, transaction, move.Member, stored with { Connections = connections }, token);

                foreach (var (field, sealedValue) in move.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    await using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText =
                        $"DELETE FROM plugin_member_secrets WHERE {SqlServerPluginMemberSettings.MemberMatch} AND {SqlServerSql.Same("field", "@field")} AND value_protected = @value";
                    SqlServerPluginMemberSettings.BindMember(delete, move.Member);
                    delete.Parameters.AddWithValue("@field", field);
                    delete.Parameters.AddWithValue("@value", sealedValue);
                    if (await delete.ExecuteNonQueryAsync(token) != 1) throw new SecretMoveChangedException(field);
                }

                foreach (var row in move.Audits(key)) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, row, token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return key;
            },
            ct);
    }
}
