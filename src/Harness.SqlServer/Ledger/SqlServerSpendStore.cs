using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// <see cref="ISpendStore"/> over <c>billing_accounts</c> on SQL Server: SQLite's
/// <c>SqliteSpendStore</c> write for write. Every write and its tenant row are one transaction (written
/// through <paramref name="audit"/>, which the Host hands <see cref="SqlServerTenantAuditRow.AppendAsync"/>),
/// and the read a write decides from holds its row (<c>UPDLOCK, HOLDLOCK</c>) until the transaction ends,
/// so two edits of one account decide one after the other. The rules and the sentences are
/// <see cref="BillingAccountRules"/>', shared with it.
/// </summary>
public sealed class SqlServerSpendStore(
    string connectionString,
    Func<SqlConnection, SqlTransaction, TriggerAudit, CancellationToken, Task> audit) : ISpendStore
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    /// <summary>One write: its own transaction, committed only when <paramref name="work"/> wrote.</summary>
    private Task<SpendWrite> WriteAsync(
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<SpendWrite>> work, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                var write = await work(connection, transaction, token);
                if (write.Written) await SqlServerOpen.CommitAsync(transaction, token);
                return write;
            },
            ct);

    public Task<IReadOnlyList<BillingAccount>> ListAccountsAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<BillingAccount>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {BillingAccountRules.Columns} FROM billing_accounts ORDER BY created_at, id";
                return await BillingAccountRules.ReadRowsAsync(command, token);
            },
            ct);

    public Task<BillingAccount?> FindAccountAsync(string id, CancellationToken ct = default) =>
        RunAsync((connection, token) => FindAsync(connection, null, id, token), ct);

    public Task<SpendWrite> CreateAccountAsync(
        BillingAccountFields fields, string createdBy, TriggerAudit audit_, CancellationToken ct = default)
    {
        var (draft, refusal) = BillingAccountRules.Read(fields, null);
        if (refusal is not null) return Task.FromResult(SpendWrite.Refused(400, refusal));

        return WriteAsync(
            async (connection, transaction, token) =>
            {
                string? instanceId;
                await using (var setting = connection.CreateCommand())
                {
                    setting.Transaction = transaction;
                    setting.CommandText = "SELECT value FROM tenant_settings WHERE name = @name";
                    setting.Parameters.AddWithValue("@name", BillingAccountRules.InstanceIdSetting);
                    instanceId = await setting.ExecuteScalarAsync(token) as string;
                }

                if (string.IsNullOrWhiteSpace(instanceId)) return SpendWrite.Refused(409, BillingAccountRules.NoInstanceId);

                var now = Instant.Now();
                var created = BillingAccountRules.Apply(
                    new BillingAccount(
                        Guid.NewGuid().ToString("D"), "", "", "", "", "", null, "", null, Active: true, null,
                        BillingAccountRules.ScopeTenant, instanceId, createdBy, now, now),
                    draft!, now);

                await using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        $"""
                         INSERT INTO billing_accounts ({BillingAccountRules.Columns})
                         VALUES (@id, @name, @vendor, @type, @currency, @period, @amount, @from, @to, @active,
                             @description, @scope, @instance, @createdBy, @createdAt, @updatedAt)
                         """;
                    Bind(insert, created);
                    await insert.ExecuteNonQueryAsync(token);
                }

                await audit(connection, transaction, Row(audit_, null, created), token);
                return new SpendWrite(created, 201, Written: true);
            },
            ct);
    }

    public Task<SpendWrite> EditAccountAsync(
        string id, BillingAccountFields fields, TriggerAudit audit_, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                if (await FindAsync(connection, transaction, id, token) is not { } current)
                {
                    return SpendWrite.Refused(404, BillingAccountRules.Missing(id));
                }

                var (draft, refusal) = BillingAccountRules.Read(fields, current);
                if (refusal is not null) return SpendWrite.Refused(400, refusal);
                if (BillingAccountRules.Unchanged(current, draft!)) return new SpendWrite(current);

                return await UpdateAsync(
                    connection, transaction, current, BillingAccountRules.Apply(current, draft!, Instant.Now()), audit_, token);
            },
            ct);

    public Task<SpendWrite> SetAccountActiveAsync(string id, bool active, TriggerAudit audit_, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                if (await FindAsync(connection, transaction, id, token) is not { } current)
                {
                    return SpendWrite.Refused(404, BillingAccountRules.Missing(id));
                }

                if (current.Active == active) return new SpendWrite(current);

                return await UpdateAsync(
                    connection, transaction, current, current with { Active = active, UpdatedAt = Instant.Now() }, audit_, token);
            },
            ct);

    private async Task<SpendWrite> UpdateAsync(
        SqlConnection connection, SqlTransaction transaction, BillingAccount before, BillingAccount after,
        TriggerAudit audit_, CancellationToken ct)
    {
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE billing_accounts SET name = @name, vendor = @vendor, billing_type = @type,
                    currency = @currency, billing_period = @period, recurring_amount = @amount,
                    effective_from = @from, effective_to = @to, active = @active, description = @description,
                    updated_at = @updatedAt
                WHERE id = @id
                """;
            Bind(update, after);
            await update.ExecuteNonQueryAsync(ct);
        }

        await audit(connection, transaction, Row(audit_, before, after), ct);
        return new SpendWrite(after, Written: true);
    }

    private static TriggerAudit Row(TriggerAudit audit_, BillingAccount? before, BillingAccount after) =>
        audit_ with { Subject = after.Id, SubjectName = after.Name, Detail = BillingAccountRules.Detail(before, after) };

    private static void Bind(SqlCommand command, BillingAccount account)
    {
        foreach (var (name, value) in BillingAccountRules.Values(account)) command.Parameters.AddWithValue("@" + name, value);
    }

    private static async Task<BillingAccount?> FindAsync(
        SqlConnection connection, SqlTransaction? transaction, string id, CancellationToken ct)
    {
        if (BillingAccountRules.KeyOf(id) is not { } key) return null;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = transaction is null
            ? $"SELECT {BillingAccountRules.Columns} FROM billing_accounts WHERE id = @id"
            : $"SELECT {BillingAccountRules.Columns} FROM billing_accounts WITH (UPDLOCK, HOLDLOCK) WHERE id = @id";
        command.Parameters.AddWithValue("@id", key);
        return (await BillingAccountRules.ReadRowsAsync(command, ct)).FirstOrDefault();
    }
}
