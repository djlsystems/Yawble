using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// <see cref="ISpendStore"/> over <c>billing_accounts</c> and <c>expenses</c> on SQL Server: SQLite's
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

    public Task<IReadOnlyList<Expense>> ListExpensesAsync(string? accountId = null, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<Expense>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                if (accountId is null)
                {
                    command.CommandText = $"{ExpenseRules.Select} ORDER BY e.recorded_at, e.id";
                }
                else
                {
                    if (BillingAccountRules.KeyOf(accountId) is not { } key) return [];
                    command.CommandText = $"{ExpenseRules.Select} WHERE e.account_id = @account ORDER BY e.recorded_at, e.id";
                    command.Parameters.AddWithValue("@account", key);
                }

                return await ExpenseRules.ReadRowsAsync(command, token);
            },
            ct);

    public Task<Expense?> FindExpenseAsync(string id, CancellationToken ct = default) =>
        RunAsync((connection, token) => FindExpenseAsync(connection, null, id, token), ct);

    public Task<ExpenseWrite> RecordExpenseAsync(
        ExpenseFields fields, string recordedBy, TriggerAudit audit_, CancellationToken ct = default)
    {
        var (draft, refusal) = ExpenseRules.Read(fields, null);
        if (refusal is not null) return Task.FromResult(ExpenseWrite.Refused(400, refusal));

        return ExpenseWriteAsync(
            async (connection, transaction, token) =>
            {
                var account = await FindAsync(connection, transaction, draft!.AccountId, token);
                if (ExpenseRules.Against(draft, account) is { } wrong) return ExpenseWrite.Refused(400, wrong);

                return await InsertAsync(
                    connection, transaction, ExpenseRules.New(draft, ExpenseRules.StatusConfirmed, null, recordedBy), account!, audit_, token);
            },
            ct);
    }

    public Task<ExpenseWrite> VoidExpenseAsync(
        string id, string? note, string recordedBy, TriggerAudit audit_, CancellationToken ct = default) =>
        ExpenseWriteAsync(
            async (connection, transaction, token) =>
            {
                if (await FindExpenseAsync(connection, transaction, id, token) is not { } target)
                {
                    return ExpenseWrite.Refused(404, ExpenseRules.Missing(id));
                }

                if (ExpenseRules.Uncorrectable(target) is { } no) return ExpenseWrite.Refused(no.Status, no.Sentence);

                var (text, refusal) = ExpenseRules.VoidNote(note);
                if (refusal is not null) return ExpenseWrite.Refused(400, refusal);

                var account = (await FindAsync(connection, transaction, target.AccountId, token))!;
                return await InsertAsync(connection, transaction, ExpenseRules.Voiding(target, text, recordedBy), account, audit_, token);
            },
            ct);

    public Task<ExpenseWrite> ReplaceExpenseAsync(
        string id, ExpenseFields fields, string recordedBy, TriggerAudit audit_, CancellationToken ct = default) =>
        ExpenseWriteAsync(
            async (connection, transaction, token) =>
            {
                if (await FindExpenseAsync(connection, transaction, id, token) is not { } target)
                {
                    return ExpenseWrite.Refused(404, ExpenseRules.Missing(id));
                }

                if (ExpenseRules.Uncorrectable(target) is { } no) return ExpenseWrite.Refused(no.Status, no.Sentence);

                var (draft, refusal) = ExpenseRules.Read(fields, target);
                if (refusal is not null) return ExpenseWrite.Refused(400, refusal);

                var account = await FindAsync(connection, transaction, draft!.AccountId, token);
                if (ExpenseRules.Against(draft, account) is { } wrong) return ExpenseWrite.Refused(400, wrong);

                return await InsertAsync(
                    connection, transaction, ExpenseRules.New(draft, ExpenseRules.StatusConfirmed, target.Id, recordedBy), account!, audit_, token);
            },
            ct);

    /// <summary>One expense write: its own transaction, committed only when <paramref name="work"/> wrote.</summary>
    private Task<ExpenseWrite> ExpenseWriteAsync(
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<ExpenseWrite>> work, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                var write = await work(connection, transaction, token);
                if (write.Ok) await SqlServerOpen.CommitAsync(transaction, token);
                return write;
            },
            ct);

    /// <summary>
    /// The row and its tenant row, in the write's transaction. A second correction of one row that got
    /// past the read (the filtered unique index on <c>supersedes</c> refuses it: 2601) is answered as
    /// the read would have answered it, with nothing written.
    /// </summary>
    private async Task<ExpenseWrite> InsertAsync(
        SqlConnection connection, SqlTransaction transaction, Expense row, BillingAccount account,
        TriggerAudit audit_, CancellationToken ct)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = ExpenseRules.Insert('@');
            foreach (var (name, value) in ExpenseRules.Values(row)) insert.Parameters.AddWithValue("@" + name, value);

            try
            {
                await insert.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException e) when (e.Number is 2601 or 2627 && row.Supersedes is not null)
            {
                return ExpenseWrite.Refused(409, ExpenseRules.AlreadyCorrected);
            }
        }

        await audit(connection, transaction, ExpenseRules.Row(audit_, row, account), ct);
        return new ExpenseWrite(row);
    }

    /// <summary>The row with <paramref name="id"/>; inside a write, its row and the range of rows that
    /// could supersede it are held (<c>UPDLOCK, HOLDLOCK</c>) until the transaction ends, so two
    /// corrections of one row decide one after the other.</summary>
    private static async Task<Expense?> FindExpenseAsync(
        SqlConnection connection, SqlTransaction? transaction, string id, CancellationToken ct)
    {
        if (BillingAccountRules.KeyOf(id) is not { } key) return null;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = transaction is null
            ? $"{ExpenseRules.Select} WHERE e.id = @id"
            : $"{ExpenseRules.Select.Replace("FROM expenses e LEFT JOIN expenses s", "FROM expenses e WITH (UPDLOCK, HOLDLOCK) LEFT JOIN expenses s WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal)} WHERE e.id = @id";
        command.Parameters.AddWithValue("@id", key);
        return (await ExpenseRules.ReadRowsAsync(command, ct)).FirstOrDefault();
    }

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
