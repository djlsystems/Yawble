using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Identity;

/// <summary>
/// <see cref="ITenantSettingsStore"/> over <c>tenant_settings</c> on SQL Server: SQLite's
/// <c>SqliteTenantSettingsStore</c> write for write, whose remarks are the argument. THE ROW AND ITS
/// AUDIT ROW ARE ONE TRANSACTION, the audit row through <see cref="SqlServerTenantAuditRow"/> and built
/// by <see cref="TenantSettingAudits"/>, shared with it: a setting never changes without a record of
/// who changed it.
///
/// <para>
/// LOCK ORDER: A WRITE FIRST TAKES EVERY SETTING IT NAMES, IN ORDINAL ORDER OF THE NAME, under
/// <c>UPDLOCK, HOLDLOCK</c> - a held key range, so a name with no row yet is held too - and only then
/// changes them in the order it was given, appending each change's tenant row as it goes. SQLite runs
/// one write at a time; here two people changing the same two settings in opposite orders would
/// otherwise each hold one and wait for the other. Two writes of one new setting wait for each other
/// at the first lock, and the second updates the row the first made rather than failing on the key.
/// </para>
/// </summary>
public sealed class SqlServerTenantSettingsStore(string connectionString) : ITenantSettingsStore
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task<IReadOnlyList<TenantSettingRow>> ReadAllAsync(CancellationToken ct = default) =>
        SqlServerOpen.RunAsync<IReadOnlyList<TenantSettingRow>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT name, value, updated_at, updated_by FROM tenant_settings ORDER BY name";

                var rows = new List<TenantSettingRow>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(new TenantSettingRow(reader.GetString(0), reader.GetString(1), Instant.Parse(reader.GetString(2)), reader.GetString(3)));
                }

                return rows;
            },
            ct);

    /// <summary>
    /// Writes every change and one <c>tenant_events</c> row per change, atomically. A null new value
    /// removes the row with a reset row; a reset of a setting with no row changes nothing, writes no
    /// audit row and is not returned. Returns the changes as stored.
    /// </summary>
    public Task<IReadOnlyList<TenantSettingWritten>> WriteAsync(
        IReadOnlyList<TenantSettingChange> changes,
        string? actorId,
        string actorEmail,
        CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;
        var stamp = Instant.Stamp(at);

        return SqlServerOpen.RunAsync<IReadOnlyList<TenantSettingWritten>>(
            _connectionString,
            async (connection, token) =>
            {
                var written = new List<TenantSettingWritten>(changes.Count);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

                foreach (var name in changes.Select(c => c.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    await using var hold = connection.CreateCommand();
                    hold.Transaction = transaction;
                    hold.CommandText = "SELECT COUNT(*) FROM tenant_settings WITH (UPDLOCK, HOLDLOCK) WHERE name = @name";
                    hold.Parameters.AddWithValue("@name", name);
                    await hold.ExecuteScalarAsync(token);
                }

                foreach (var change in changes)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.Parameters.AddWithValue("@name", change.Name);

                    if (change.NewValue is null)
                    {
                        command.CommandText = "DELETE FROM tenant_settings WHERE name = @name";
                        if (await command.ExecuteNonQueryAsync(token) == 0) continue;

                        await AppendAuditAsync(connection, transaction, stamp, change, actorId, actorEmail, token);
                        written.Add(new TenantSettingWritten(change.Name, null));
                        continue;
                    }

                    command.CommandText =
                        """
                        MERGE tenant_settings WITH (HOLDLOCK) AS s
                        USING (SELECT @name AS name) AS n ON s.name = n.name
                        WHEN MATCHED THEN UPDATE SET value = @value, updated_at = @at, updated_by = @by
                        WHEN NOT MATCHED THEN INSERT (name, value, updated_at, updated_by) VALUES (@name, @value, @at, @by);
                        """;
                    command.Parameters.AddWithValue("@value", change.NewValue);
                    command.Parameters.AddWithValue("@at", stamp);
                    command.Parameters.AddWithValue("@by", actorEmail);
                    await command.ExecuteNonQueryAsync(token);

                    await AppendAuditAsync(connection, transaction, stamp, change, actorId, actorEmail, token);
                    written.Add(new TenantSettingWritten(
                        change.Name, new TenantSettingRow(change.Name, change.NewValue, at, actorEmail)));
                }

                await transaction.CommitAsync(token);
                return written;
            },
            ct);
    }

    /// <summary>The change's tenant row (<see cref="TenantSettingAudits"/>), stamped as the change is.</summary>
    private static Task AppendAuditAsync(
        SqlConnection connection, SqlTransaction transaction, string stamp, TenantSettingChange change,
        string? actorId, string actorEmail, CancellationToken ct) =>
        SqlServerTenantAuditRow.AppendAtAsync(
            connection, transaction, TenantSettingAudits.For(change, actorId, actorEmail), Instant.Parse(stamp), ct);
}
