using Harness.Contracts;
using Harness.Messaging;
using Harness.SqlServer;
using Harness.SqlServer.Ledger;
using Harness.SqlServer.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.Host.SqlServer;

/// <summary>
/// The SQL Server version of <see cref="ILedgerStartRows"/>: <see cref="SqliteLedgerStartRows"/>
/// statement for statement - the backfill of both ledgers from the log through the writer a live
/// append uses (<see cref="SqlServerLedgerRows"/>), <c>instance.id</c>, and the rows that mark it
/// done, in one transaction. <see cref="LedgerStart"/> keeps the names, the actor and the rules.
///
/// <para>
/// UNDER THE APPEND LOCK, the first thing the transaction takes. The start runs before anything
/// appends, but a backfill that wrote a run's row beside a live append writing the same run's row
/// would fail one of them on the key; holding the lock makes the backfill the one writer it is on
/// SQLite. The settings are read under <c>UPDLOCK, HOLDLOCK</c> for the same reason: two Hosts
/// started on one database at once must not both create an <c>instance.id</c>.
/// </para>
/// </summary>
public sealed class SqlServerLedgerStartRows(string connectionString) : ILedgerStartRows
{
    private const int Page = 500;

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task<LedgerStartResult> RunAsync(CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                await SqlServerAppendLock.TakeAsync(connection, transaction, token);

                var now = Instant.Now();

                var instanceId = await SettingAsync(connection, transaction, LedgerStart.InstanceIdName, token);
                if (instanceId is null)
                {
                    instanceId = Guid.NewGuid().ToString("D");
                    await WriteSettingAsync(connection, transaction, LedgerStart.InstanceIdName, instanceId, now, token);
                }

                int? runs = null;
                int? workflows = null;

                var startedAt = await SettingAsync(connection, transaction, LedgerStart.LedgerStartedAtName, token);
                if (startedAt is null)
                {
                    (runs, workflows) = await BackfillAsync(connection, transaction, token);

                    startedAt = now;
                    await WriteSettingAsync(connection, transaction, LedgerStart.LedgerStartedAtName, now, now, token);
                    await WriteSettingAsync(
                        connection, transaction, LedgerStart.LedgerBackfilledRunsName,
                        runs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), now, token);
                }

                await SqlServerOpen.CommitAsync(transaction, token);

                return new LedgerStartResult(runs, workflows, instanceId, startedAt);
            },
            ct);

    private static async Task<(int Runs, int Workflows)> BackfillAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        var runs = 0;
        var workflows = 0;
        var after = 0L;

        while (true)
        {
            IReadOnlyList<Message> page;

            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    $"""
                     SELECT TOP (@page) {MessageRows.Columns}
                     FROM messages
                     WHERE seq > @after AND type IN (@completed, @failed, @declared, @closed)
                     ORDER BY seq
                     """;
                read.Parameters.AddWithValue("@after", after);
                read.Parameters.AddWithValue("@completed", MessageTypes.Completed);
                read.Parameters.AddWithValue("@failed", MessageTypes.Failed);
                read.Parameters.AddWithValue("@declared", MessageTypes.WorkflowCompleted);
                read.Parameters.AddWithValue("@closed", MessageTypes.WorkflowClosed);
                read.Parameters.AddWithValue("@page", Page);

                page = await MessageRows.ReadAllAsync(read, ct);
            }

            if (page.Count == 0) break;

            foreach (var row in page)
            {
                if (!await SqlServerLedgerRows.WriteAsync(connection, transaction, row, backfilled: true, ct)) continue;

                if (LedgerRows.IsRunTerminal(row)) runs++;
                else workflows++;
            }

            after = page[^1].Seq;
        }

        return (runs, workflows);
    }

    private static async Task<string?> SettingAsync(
        SqlConnection connection, SqlTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM tenant_settings WITH (UPDLOCK, HOLDLOCK) WHERE name = @name";
        command.Parameters.AddWithValue("@name", name);

        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>A new row and its tenant row. Only ever called for a row that is not there.</summary>
    private static async Task WriteSettingAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string value, string at, CancellationToken ct)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO tenant_settings (name, value, updated_at, updated_by) VALUES (@name, @value, @at, @by)";
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@value", value);
            insert.Parameters.AddWithValue("@at", at);
            insert.Parameters.AddWithValue("@by", LedgerStart.Actor);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await SqlServerTenantAuditRow.AppendAtAsync(
            connection, transaction, LedgerStart.SettingAudit(name, value), Instant.Parse(at), ct);
    }
}
