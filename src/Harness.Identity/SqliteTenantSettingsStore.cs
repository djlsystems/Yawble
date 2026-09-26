using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Identity;

/// <summary>One stored instance-wide setting: a <c>tenant_settings</c> row.</summary>
public sealed record TenantSettingRow(string Name, string Value, DateTimeOffset UpdatedAt, string UpdatedBy);

/// <summary>One change to write: the setting, the value it had before, and the value it gets.</summary>
public sealed record TenantSettingChange(string Name, string? OldValue, string NewValue);

/// <summary>
/// <c>tenant_settings</c> over the same SQLite file the accounts and the tenant log use.
///
/// <b>THE ROW AND ITS AUDIT ROW ARE ONE TRANSACTION.</b> Elsewhere a failure to write the tenant log
/// is swallowed so it cannot fail the act it records; here the act IS a row in the same file, so the
/// two land together or neither does - a setting that changed with no record of who changed it is
/// the one outcome this table must not produce.
/// </summary>
public sealed class SqliteTenantSettingsStore(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    public async Task<IReadOnlyList<TenantSettingRow>> ReadAllAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, value, updated_at, updated_by FROM tenant_settings ORDER BY name";

        var rows = new List<TenantSettingRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TenantSettingRow(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// Writes every change and one <c>tenant_events</c> row per change, atomically. Returns the
    /// rows as stored.
    /// </summary>
    public async Task<IReadOnlyList<TenantSettingRow>> WriteAsync(
        IReadOnlyList<TenantSettingChange> changes,
        string? actorId,
        string actorEmail,
        CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;
        var stamp = at.ToString("O");
        var written = new List<TenantSettingRow>(changes.Count);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        foreach (var change in changes)
        {
            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText =
                    """
                    INSERT INTO tenant_settings (name, value, updated_at, updated_by)
                    VALUES ($name, $value, $at, $by)
                    ON CONFLICT(name) DO UPDATE SET
                        value = excluded.value, updated_at = excluded.updated_at, updated_by = excluded.updated_by
                    """;
                upsert.Parameters.AddWithValue("$name", change.Name);
                upsert.Parameters.AddWithValue("$value", change.NewValue);
                upsert.Parameters.AddWithValue("$at", stamp);
                upsert.Parameters.AddWithValue("$by", actorEmail);
                await upsert.ExecuteNonQueryAsync(ct);
            }

            await using (var audit = connection.CreateCommand())
            {
                audit.Transaction = transaction;
                audit.CommandText =
                    """
                    INSERT INTO tenant_events
                        (occurred_at, actor_id, actor_email, action, subject, subject_name, detail)
                    VALUES ($at, $actorId, $actorEmail, $action, $subject, $subject, $detail)
                    """;
                audit.Parameters.AddWithValue("$at", stamp);
                audit.Parameters.AddWithValue("$actorId", (object?)actorId ?? DBNull.Value);
                audit.Parameters.AddWithValue("$actorEmail", actorEmail);
                audit.Parameters.AddWithValue("$action", TenantActions.TenantSettingChanged);
                audit.Parameters.AddWithValue("$subject", change.Name);
                audit.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new
                {
                    setting = change.Name,
                    old = change.OldValue,
                    @new = change.NewValue,
                }));
                await audit.ExecuteNonQueryAsync(ct);
            }

            written.Add(new TenantSettingRow(change.Name, change.NewValue, at, actorEmail));
        }

        await transaction.CommitAsync(ct);
        return written;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }
}
