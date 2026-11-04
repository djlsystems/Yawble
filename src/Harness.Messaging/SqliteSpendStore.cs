using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>
/// <see cref="ISpendStore"/> over <c>billing_accounts</c> and <c>expenses</c> in the log's own database file.
///
/// <para>
/// EVERY WRITE AND ITS TENANT ROW ARE ONE TRANSACTION, as <see cref="SqliteOutcomeStore"/>'s: the row is
/// written through <paramref name="audit"/> (the Host hands in <c>TenantAuditRow.AppendAsync</c>) inside
/// the write's transaction, so when it cannot be written the account write does not happen. The rules
/// and the sentences are <see cref="BillingAccountRules"/>', shared with SQL Server.
/// </para>
/// </summary>
public sealed class SqliteSpendStore(
    string databasePath,
    Func<SqliteConnection, SqliteTransaction, TriggerAudit, CancellationToken, Task> audit) : ISpendStore
{
    private readonly string _connectionString = SqliteOpen.ConnectionString(databasePath);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }

    public async Task<IReadOnlyList<BillingAccount>> ListAccountsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BillingAccountRules.Columns} FROM billing_accounts ORDER BY created_at, id";
        return await BillingAccountRules.ReadRowsAsync(command, ct);
    }

    public async Task<BillingAccount?> FindAccountAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        return await FindAsync(connection, null, id, ct);
    }

    public async Task<SpendWrite> CreateAccountAsync(
        BillingAccountFields fields, string createdBy, TriggerAudit audit_, CancellationToken ct = default)
    {
        var (draft, refusal) = BillingAccountRules.Read(fields, null);
        if (refusal is not null) return SpendWrite.Refused(400, refusal);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        string? instanceId;
        await using (var setting = connection.CreateCommand())
        {
            setting.Transaction = transaction;
            setting.CommandText = "SELECT value FROM tenant_settings WHERE name = $name";
            setting.Parameters.AddWithValue("$name", BillingAccountRules.InstanceIdSetting);
            instanceId = await setting.ExecuteScalarAsync(ct) as string;
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
                 VALUES ($id, $name, $vendor, $type, $currency, $period, $amount, $from, $to, $active,
                     $description, $scope, $instance, $createdBy, $createdAt, $updatedAt)
                 """;
            Bind(insert, created);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await audit(connection, transaction, Row(audit_, null, created), ct);
        await transaction.CommitAsync(ct);

        return new SpendWrite(created, 201, Written: true);
    }

    public async Task<SpendWrite> EditAccountAsync(
        string id, BillingAccountFields fields, TriggerAudit audit_, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await FindAsync(connection, transaction, id, ct) is not { } current)
        {
            return SpendWrite.Refused(404, BillingAccountRules.Missing(id));
        }

        var (draft, refusal) = BillingAccountRules.Read(fields, current);
        if (refusal is not null) return SpendWrite.Refused(400, refusal);
        if (BillingAccountRules.Unchanged(current, draft!)) return new SpendWrite(current);

        return await WriteAsync(connection, transaction, current, BillingAccountRules.Apply(current, draft!, Instant.Now()), audit_, ct);
    }

    public async Task<SpendWrite> SetAccountActiveAsync(string id, bool active, TriggerAudit audit_, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await FindAsync(connection, transaction, id, ct) is not { } current)
        {
            return SpendWrite.Refused(404, BillingAccountRules.Missing(id));
        }

        if (current.Active == active) return new SpendWrite(current);

        return await WriteAsync(connection, transaction, current, current with { Active = active, UpdatedAt = Instant.Now() }, audit_, ct);
    }

    public async Task<IReadOnlyList<Expense>> ListExpensesAsync(string? accountId = null, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        if (accountId is null)
        {
            command.CommandText = $"{ExpenseRules.Select} ORDER BY e.recorded_at, e.id";
        }
        else
        {
            if (BillingAccountRules.KeyOf(accountId) is not { } key) return [];
            command.CommandText = $"{ExpenseRules.Select} WHERE e.account_id = $account ORDER BY e.recorded_at, e.id";
            command.Parameters.AddWithValue("$account", key);
        }

        return await ExpenseRules.ReadRowsAsync(command, ct);
    }

    public async Task<Expense?> FindExpenseAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        return await FindExpenseAsync(connection, null, id, ct);
    }

    public async Task<ExpenseWrite> RecordExpenseAsync(
        ExpenseFields fields, string recordedBy, TriggerAudit audit_, CancellationToken ct = default)
    {
        var (draft, refusal) = ExpenseRules.Read(fields, null);
        if (refusal is not null) return ExpenseWrite.Refused(400, refusal);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var account = await FindAsync(connection, transaction, draft!.AccountId, ct);
        if (ExpenseRules.Against(draft, account) is { } wrong) return ExpenseWrite.Refused(400, wrong);

        return await InsertAsync(connection, transaction, ExpenseRules.New(draft, ExpenseRules.StatusConfirmed, null, recordedBy), account!, audit_, ct);
    }

    public async Task<ExpenseWrite> VoidExpenseAsync(
        string id, string? note, string recordedBy, TriggerAudit audit_, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await FindExpenseAsync(connection, transaction, id, ct) is not { } target) return ExpenseWrite.Refused(404, ExpenseRules.Missing(id));
        if (ExpenseRules.Uncorrectable(target) is { } no) return ExpenseWrite.Refused(no.Status, no.Sentence);

        var (text, refusal) = ExpenseRules.VoidNote(note);
        if (refusal is not null) return ExpenseWrite.Refused(400, refusal);

        var account = (await FindAsync(connection, transaction, target.AccountId, ct))!;
        return await InsertAsync(connection, transaction, ExpenseRules.Voiding(target, text, recordedBy), account, audit_, ct);
    }

    public async Task<ExpenseWrite> ReplaceExpenseAsync(
        string id, ExpenseFields fields, string recordedBy, TriggerAudit audit_, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await FindExpenseAsync(connection, transaction, id, ct) is not { } target) return ExpenseWrite.Refused(404, ExpenseRules.Missing(id));
        if (ExpenseRules.Uncorrectable(target) is { } no) return ExpenseWrite.Refused(no.Status, no.Sentence);

        var (draft, refusal) = ExpenseRules.Read(fields, target);
        if (refusal is not null) return ExpenseWrite.Refused(400, refusal);

        var account = await FindAsync(connection, transaction, draft!.AccountId, ct);
        if (ExpenseRules.Against(draft, account) is { } wrong) return ExpenseWrite.Refused(400, wrong);

        return await InsertAsync(
            connection, transaction, ExpenseRules.New(draft, ExpenseRules.StatusConfirmed, target.Id, recordedBy), account!, audit_, ct);
    }

    /// <summary>
    /// The row and its tenant row, in the write's transaction. A second correction of one row that got
    /// past the read (the unique index on <c>supersedes</c> refuses it) is answered as the read would
    /// have answered it, with nothing written.
    /// </summary>
    private async Task<ExpenseWrite> InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, Expense row, BillingAccount account,
        TriggerAudit audit_, CancellationToken ct)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = ExpenseRules.Insert('$');
            foreach (var (name, value) in ExpenseRules.Values(row)) insert.Parameters.AddWithValue("$" + name, value);

            try
            {
                await insert.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19 && row.Supersedes is not null)
            {
                return ExpenseWrite.Refused(409, ExpenseRules.AlreadyCorrected);
            }
        }

        await audit(connection, transaction, ExpenseRules.Row(audit_, row, account), ct);
        await transaction.CommitAsync(ct);

        return new ExpenseWrite(row);
    }

    private static async Task<Expense?> FindExpenseAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        if (BillingAccountRules.KeyOf(id) is not { } key) return null;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{ExpenseRules.Select} WHERE e.id = $id";
        command.Parameters.AddWithValue("$id", key);
        return (await ExpenseRules.ReadRowsAsync(command, ct)).FirstOrDefault();
    }

    private async Task<SpendWrite> WriteAsync(
        SqliteConnection connection, SqliteTransaction transaction, BillingAccount before, BillingAccount after,
        TriggerAudit audit_, CancellationToken ct)
    {
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE billing_accounts SET name = $name, vendor = $vendor, billing_type = $type,
                    currency = $currency, billing_period = $period, recurring_amount = $amount,
                    effective_from = $from, effective_to = $to, active = $active, description = $description,
                    updated_at = $updatedAt
                WHERE id = $id
                """;
            Bind(update, after);
            await update.ExecuteNonQueryAsync(ct);
        }

        await audit(connection, transaction, Row(audit_, before, after), ct);
        await transaction.CommitAsync(ct);

        return new SpendWrite(after, Written: true);
    }

    private static TriggerAudit Row(TriggerAudit audit_, BillingAccount? before, BillingAccount after) =>
        audit_ with { Subject = after.Id, SubjectName = after.Name, Detail = BillingAccountRules.Detail(before, after) };

    private static void Bind(SqliteCommand command, BillingAccount account)
    {
        foreach (var (name, value) in BillingAccountRules.Values(account)) command.Parameters.AddWithValue("$" + name, value);
    }

    private static async Task<BillingAccount?> FindAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        if (BillingAccountRules.KeyOf(id) is not { } key) return null;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {BillingAccountRules.Columns} FROM billing_accounts WHERE id = $id";
        command.Parameters.AddWithValue("$id", key);
        return (await BillingAccountRules.ReadRowsAsync(command, ct)).FirstOrDefault();
    }
}
