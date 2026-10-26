using Harness.Contracts;
using Harness.SqlServer;
using Microsoft.Data.SqlClient;

namespace Harness.Host.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="IPluginMemberSecretRows"/>: <c>plugin_member_secrets</c>, SQLite's
/// <see cref="SqlitePluginMemberSecretRows"/> statement for statement, every change with its tenant rows in
/// the same transaction through <see cref="SqlServerTenantAuditRow"/>. The ciphertext is the text
/// <see cref="PluginMemberSecretStore"/> sealed, stored and answered unchanged, so a value either provider
/// stored opens through the other.
///
/// <para>
/// A TEAM AND A MEMBER FOLD CASE, a field does not, as on SQLite; every match also takes the length
/// (<see cref="SqlServerSql.Same"/>), so a trailing space finds nothing.
/// </para>
///
/// <para>
/// LOCK ORDER: THE <c>teams</c> ROW, THE <c>team_members</c> ROW, THE SECRET ROWS, THEN
/// <c>tenant_events</c>, as <see cref="SqlServerPluginMemberSettings"/> takes them and for its reason (the
/// rows reference their member and die with it). A save with settings takes the settings row between the
/// member and the secrets. Each set reads whether its field is stored under <c>UPDLOCK, HOLDLOCK</c> and
/// then writes, so two first sets of one field at once make one row, one <c>member.secret-set</c> and one
/// <c>member.secret-replaced</c>, not two sets and a key violation.
/// </para>
///
/// <para>
/// THE ONE-TIME COPY RUNS UNDER ITS OWN APPLICATION LOCK (<see cref="CopyLock"/>), the first lock it
/// takes: two Hosts started on one database at once must not both copy and both mark it done. Under it
/// the mark is read, and each member it writes is locked as above before its row, so a team's deletion
/// racing the start waits or is waited for, in one order.
/// </para>
/// </summary>
public sealed class SqlServerPluginMemberSecretRows(string connectionString) : IPluginMemberSecretRows
{
    /// <summary>The copy's lock, taken by nothing else.</summary>
    public const string CopyLock = "plugin-member-secrets.copy";

    private static readonly string FieldMatch =
        $"{SqlServerPluginMemberSettings.MemberMatch} AND {SqlServerSql.Same("field", "@field")}";

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task<IReadOnlyList<MemberSecretRow>> RowsAsync(ContainerId member, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync<IReadOnlyList<MemberSecretRow>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var select = connection.CreateCommand();
                select.CommandText =
                    $"SELECT field, value_protected, set_by, set_at FROM plugin_member_secrets WHERE {SqlServerPluginMemberSettings.MemberMatch}";
                SqlServerPluginMemberSettings.BindMember(select, member);

                var rows = new List<MemberSecretRow>();
                await using var reader = await select.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(new MemberSecretRow(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2), Instant.Parse(reader.GetString(3))));
                }

                return rows;
            },
            ct);

    public Task ChangeAsync(
        ContainerId member, IReadOnlyDictionary<string, string?> protectedChanges, string setBy,
        Func<string, string, TriggerAudit> audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await SqlServerPluginMemberSettings.LockMemberAsync(connection, transaction, member, token);
                await ApplyAsync(connection, transaction, member, protectedChanges, setBy, audit, token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    public Task SaveWithSettingsAsync(
        ContainerId member, PluginMemberSettings settings, IReadOnlyList<TriggerAudit> audits,
        IReadOnlyDictionary<string, string?> protectedChanges, string setBy,
        Func<string, string, TriggerAudit> audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await SqlServerPluginMemberSettings.LockMemberAsync(connection, transaction, member, token);
                await SqlServerPluginMemberSettings.UpsertAsync(connection, transaction, member, settings, token);
                foreach (var row in audits) await SqlServerTenantAuditRow.AppendAsync(connection, transaction, row, token);
                await ApplyAsync(connection, transaction, member, protectedChanges, setBy, audit, token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    public Task<bool> CopyStoredKeysOnceAsync(
        Func<string, string?> unprotectKey, Func<string, string> protectForMember,
        Func<int, int, int, TriggerAudit> done, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var take = connection.CreateCommand())
                {
                    take.Transaction = transaction;
                    take.CommandText =
                        $"""
                        DECLARE @granted INT;
                        EXEC @granted = sp_getapplock @Resource = N'{CopyLock}', @LockMode = N'Exclusive', @LockOwner = N'Transaction';
                        IF @granted = -3 THROW {SqlServerOpen.LockDeadlockVictim}, N'The member secrets copy lock request was chosen as a deadlock victim.', 1;
                        IF @granted < 0 THROW 51000, N'The member secrets copy could not take its lock.', 1;
                        """;
                    await take.ExecuteNonQueryAsync(token);
                }

                await using (var marked = connection.CreateCommand())
                {
                    marked.Transaction = transaction;
                    marked.CommandText = "SELECT COUNT(*) FROM tenant_events WHERE action = @action";
                    marked.Parameters.AddWithValue("@action", TenantActions.MemberSecretsCopied);
                    if ((int)(await marked.ExecuteScalarAsync(token))! > 0)
                    {
                        await transaction.RollbackAsync(token);
                        return false;
                    }
                }

                var stored = new Dictionary<string, (string Value, string SetBy)>(StringComparer.Ordinal);
                await using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT [key], value_protected, set_by FROM plugin_secrets";
                    await using var reader = await select.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        if (unprotectKey(reader.GetString(1)) is { } value) stored[reader.GetString(0)] = (value, reader.GetString(2));
                    }
                }

                var bindings = new List<(string Team, string Name, string Field, string Key)>();
                await using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT team, name, secrets_json FROM team_member_config";
                    await using var reader = await select.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        foreach (var (field, key) in PluginMemberSettingsRows.SecretBindings(reader.GetString(2)))
                        {
                            bindings.Add((reader.GetString(0), reader.GetString(1), field, key));
                        }
                    }
                }

                var tally = new MemberSecretCopyTally();
                var at = Instant.Stamp(DateTimeOffset.UtcNow);

                foreach (var (team, name, field, key) in bindings)
                {
                    if (!stored.TryGetValue(key, out var value)) continue;

                    await SqlServerPluginMemberSettings.LockMemberAsync(connection, transaction, team, name, token);

                    // SQLite's ON CONFLICT DO NOTHING: a field with a value of its own keeps it.
                    await using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO plugin_member_secrets (team, name, field, value_protected, set_by, set_at)
                        SELECT @team, @name, @field, @value, @by, @at
                        WHERE NOT EXISTS (
                            SELECT 1 FROM plugin_member_secrets WITH (UPDLOCK, HOLDLOCK)
                            WHERE team = @team AND name = @name AND field = @field)
                        """;
                    SqlServerPluginMemberSettings.BindMember(insert, team, name);
                    insert.Parameters.AddWithValue("@field", field);
                    insert.Parameters.AddWithValue("@value", protectForMember(value.Value));
                    insert.Parameters.AddWithValue("@by", value.SetBy);
                    insert.Parameters.AddWithValue("@at", at);

                    if (await insert.ExecuteNonQueryAsync(token) > 0) tally.Copied(team, name, key);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, tally.Done(done), token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    private static async Task ApplyAsync(
        SqlConnection connection, SqlTransaction transaction, ContainerId member,
        IReadOnlyDictionary<string, string?> protectedChanges, string setBy, Func<string, string, TriggerAudit> audit,
        CancellationToken ct)
    {
        var at = Instant.Stamp(DateTimeOffset.UtcNow);

        foreach (var (field, value) in protectedChanges.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            string action;

            if (value is null)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM plugin_member_secrets WHERE {FieldMatch}";
                Bind(delete, member, field);

                // Nothing stored: nothing changes and no row.
                if (await delete.ExecuteNonQueryAsync(ct) == 0) continue;
                action = TenantActions.MemberSecretCleared;
            }
            else
            {
                bool existed;
                await using (var exists = connection.CreateCommand())
                {
                    exists.Transaction = transaction;
                    exists.CommandText = $"SELECT COUNT(*) FROM plugin_member_secrets WITH (UPDLOCK, HOLDLOCK) WHERE {FieldMatch}";
                    Bind(exists, member, field);
                    existed = (int)(await exists.ExecuteScalarAsync(ct))! > 0;
                }

                await using var write = connection.CreateCommand();
                write.Transaction = transaction;
                write.CommandText = existed
                    ? $"UPDATE plugin_member_secrets SET value_protected = @value, set_by = @by, set_at = @at WHERE {FieldMatch}"
                    : """
                      INSERT INTO plugin_member_secrets (team, name, field, value_protected, set_by, set_at)
                      VALUES (@team, @name, @field, @value, @by, @at)
                      """;
                Bind(write, member, field);
                write.Parameters.AddWithValue("@value", value);
                write.Parameters.AddWithValue("@by", setBy);
                write.Parameters.AddWithValue("@at", at);
                await write.ExecuteNonQueryAsync(ct);

                action = existed ? TenantActions.MemberSecretReplaced : TenantActions.MemberSecretSet;
            }

            await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit(field, action), ct);
        }
    }

    private static void Bind(SqlCommand command, ContainerId member, string field)
    {
        SqlServerPluginMemberSettings.BindMember(command, member);
        command.Parameters.AddWithValue("@field", field);
    }
}
