using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>What the start's ledger step did: the runs a backfill recovered (null when the ledger had
/// already begun and nothing was backfilled), the instance's id and when the ledger began - what the
/// export names itself by.</summary>
public sealed record LedgerStartResult(
    int? BackfilledRuns, int? BackfilledWorkflows, string InstanceId, string? LedgerStartedAt);

/// <summary>
/// THE LEDGER'S ONE-TIME START, and the instance's id. Run once per boot, after the schema is
/// applied and before anything appends.
///
/// <para>
/// THE BACKFILL RUNS ONCE, at the start that applies <c>outcome-001</c>: it builds a ledger row from
/// every run's terminal row and every <c>workflow.completed</c> and <c>workflow.closed</c> still on
/// the log, through <see cref="LedgerRows"/> - the writer a live append uses - marked
/// <c>backfilled = 1</c>, and records <c>ledger.startedAt</c> (when the ledger began) and
/// <c>ledger.backfilledRuns</c> (how many runs it recovered) in the same transaction. That row is
/// what marks it done, so a start that failed part way tries again, and a second start adds nothing.
/// <c>run_seq</c> and <c>close_seq</c> are keys, so a row is never recovered twice either way. Spend
/// a Reset had already deleted from the log is not on it, and nothing here claims to recover it.
/// </para>
///
/// <para>
/// <c>instance.id</c> IS A GUID WRITTEN ONCE, at the first start after this change, and never
/// changed. It is not <c>InstanceIdentity</c>, which hashes the data root's path and so is shared by
/// two instances that mount a volume at the same path.
/// </para>
///
/// <para>
/// All three are <c>tenant_settings</c> rows with their <c>tenant_events</c> row in the same
/// transaction, like every settings write; none is a setting a person can change -
/// <see cref="TenantSettings"/> does not define them, so a PUT naming one is refused.
/// </para>
/// </summary>
public static class LedgerStart
{
    public const string InstanceIdName = "instance.id";
    public const string LedgerStartedAtName = "ledger.startedAt";
    public const string LedgerBackfilledRunsName = "ledger.backfilledRuns";

    /// <summary>Who the tenant log says wrote these rows: the platform itself, at start.</summary>
    public const string Actor = "platform";

    private const int Page = 500;

    public static async Task<LedgerStartResult> RunAsync(string databasePath, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await SqlitePragmas.ApplyAsync(connection, ct: ct);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var instanceId = await SettingAsync(connection, transaction, InstanceIdName, ct);
        if (instanceId is null)
        {
            instanceId = Guid.NewGuid().ToString("D");
            await WriteSettingAsync(connection, transaction, InstanceIdName, instanceId, now, ct);
        }

        int? runs = null;
        int? workflows = null;

        var startedAt = await SettingAsync(connection, transaction, LedgerStartedAtName, ct);
        if (startedAt is null)
        {
            (runs, workflows) = await BackfillAsync(connection, transaction, ct);

            startedAt = now;
            await WriteSettingAsync(connection, transaction, LedgerStartedAtName, now, now, ct);
            await WriteSettingAsync(
                connection, transaction, LedgerBackfilledRunsName,
                runs.Value.ToString(CultureInfo.InvariantCulture), now, ct);
        }

        await transaction.CommitAsync(ct);

        return new LedgerStartResult(runs, workflows, instanceId, startedAt);
    }

    private static async Task<(int Runs, int Workflows)> BackfillAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var runs = 0;
        var workflows = 0;
        var after = 0L;

        while (true)
        {
            var page = new List<Message>();

            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    """
                    SELECT seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at
                    FROM messages
                    WHERE seq > $after AND type IN ($completed, $failed, $declared, $closed)
                    ORDER BY seq
                    LIMIT $page
                    """;
                read.Parameters.AddWithValue("$after", after);
                read.Parameters.AddWithValue("$completed", MessageTypes.Completed);
                read.Parameters.AddWithValue("$failed", MessageTypes.Failed);
                read.Parameters.AddWithValue("$declared", MessageTypes.WorkflowCompleted);
                read.Parameters.AddWithValue("$closed", MessageTypes.WorkflowClosed);
                read.Parameters.AddWithValue("$page", Page);

                await using var reader = await read.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    page.Add(new Message(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.GetInt32(6),
                        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
                }
            }

            if (page.Count == 0) break;

            foreach (var row in page)
            {
                if (!await LedgerRows.WriteAsync(connection, transaction, row, backfilled: true, ct)) continue;

                if (LedgerRows.IsRunTerminal(row)) runs++;
                else workflows++;
            }

            after = page[^1].Seq;
        }

        return (runs, workflows);
    }

    private static async Task<string?> SettingAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM tenant_settings WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);

        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>A new row and its tenant row. Only ever called for a row that is not there.</summary>
    private static async Task WriteSettingAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, string value, string at,
        CancellationToken ct)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO tenant_settings (name, value, updated_at, updated_by) VALUES ($name, $value, $at, $by)";
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$value", value);
            insert.Parameters.AddWithValue("$at", at);
            insert.Parameters.AddWithValue("$by", Actor);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText =
            """
            INSERT INTO tenant_events (occurred_at, actor_id, actor_email, action, subject, subject_name, detail)
            VALUES ($at, NULL, $by, $action, $name, $name, $detail)
            """;
        audit.Parameters.AddWithValue("$at", at);
        audit.Parameters.AddWithValue("$by", Actor);
        audit.Parameters.AddWithValue("$action", TenantActions.TenantSettingChanged);
        audit.Parameters.AddWithValue("$name", name);
        audit.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new { setting = name, old = (string?)null, @new = value }));
        await audit.ExecuteNonQueryAsync(ct);
    }
}
