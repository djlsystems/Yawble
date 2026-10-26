using System.Text.Json;
using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// <see cref="IOutcomeStore"/> over <c>outcomes</c> and <c>workflow_outcome_links</c> on SQL Server:
/// SQLite's <c>SqliteOutcomeStore</c> write for write, whose remarks are the argument - every write and
/// its tenant row are one transaction (written through <paramref name="audit"/>, which the Host hands
/// <see cref="SqlServerTenantAuditRow.AppendAsync"/>), and nothing rewrites a link. The rules and the
/// sentences are <see cref="OutcomeRules"/>', shared with it.
///
/// <para>
/// EVERY READ A WRITE DECIDES FROM HOLDS WHAT IT READ. SQLite has one writer at a time, so a write
/// that reads an outcome, a live name or a workflow's newest link and then writes cannot interleave
/// with another. Here each such read takes <c>UPDLOCK, HOLDLOCK</c> until the transaction ends: two
/// creates of one name, two unlinks of one workflow, or a link racing a retire wait for each other and
/// the second decides from what the first wrote. A deadlock between two such writes (a merge of A into
/// B against one of B into A) is run again whole by <see cref="SqlServerOpen.RetryAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>.
/// </para>
/// </summary>
public sealed class SqlServerOutcomeStore(
    string connectionString,
    Func<SqlConnection, SqlTransaction, TriggerAudit, CancellationToken, Task> audit) : IOutcomeStore
{
    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    private const string OutcomeColumns = OutcomeFacts.OutcomeColumns;

    private const string Locked = "WITH (UPDLOCK, HOLDLOCK)";

    private static string Now() => Instant.Now();

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    /// <summary>One write: its own transaction, committed only when <paramref name="work"/> answers a
    /// write that did not refuse; a refusal rolls back whatever was read under lock.</summary>
    private Task<OutcomeWrite> WriteAsync(
        Func<SqlConnection, SqlTransaction, CancellationToken, Task<OutcomeWrite>> work, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                var write = await work(connection, transaction, token);
                if (write.Ok) await SqlServerOpen.CommitAsync(transaction, token);
                return write;
            },
            ct);

    public Task<IReadOnlyList<Outcome>> ListAsync(CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {OutcomeColumns} FROM outcomes ORDER BY created_at, id";
                return await OutcomeFacts.ReadOutcomesAsync(command, token);
            },
            ct);

    public Task<Outcome?> FindAsync(string id, CancellationToken ct = default) =>
        RunAsync((connection, token) => FindAsync(connection, null, id, token), ct);

    public Task<Outcome?> FindLiveByNameAsync(string name, CancellationToken ct = default) =>
        RunAsync((connection, token) => LiveByNameAsync(connection, null, name, token), ct);

    public async Task<Outcome?> ResolveLiveAsync(string idOrName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;

        var text = idOrName.Trim();

        return await RunAsync(
            async (connection, token) =>
            {
                if (await FindAsync(connection, null, text, token) is { IsLive: true } byId) return byId;

                // THE EXACT NAME, as a person typed it: the stored name, trimmed, compared ordinally.
                var byName = await LiveByNameAsync(connection, null, text, token);
                return byName is not null && string.Equals(byName.Name, OutcomeNames.Clean(text), StringComparison.Ordinal)
                    ? byName
                    : null;
            },
            ct);
    }

    public Task<OutcomeWrite> CreateAsync(
        string name, OutcomeEdit fields, OutcomeActor person, TriggerAudit audit_, CancellationToken ct = default)
    {
        if (OutcomeRules.NameRefusal(name) is { } bad) return Task.FromResult(OutcomeWrite.Refused(400, bad));
        if (OutcomeRules.ValueRefusal(fields.Value) is { } badValue) return Task.FromResult(OutcomeWrite.Refused(400, badValue));

        return WriteAsync(
            async (connection, transaction, token) =>
            {
                if (await LiveByNameAsync(connection, transaction, name, token) is { } taken)
                {
                    return OutcomeWrite.Refused(409, OutcomeRules.Taken(taken));
                }

                var created = await InsertAsync(
                    connection, transaction, name, fields, OutcomeStatus.Active, person, confirmed: true, token);

                await audit(connection, transaction, audit_ with { Subject = created.Id, SubjectName = created.Name }, token);

                return new OutcomeWrite(created, Created: true, Status: 201);
            },
            ct);
    }

    public Task<OutcomeWrite> ProposeAsync(
        string name, string? description, OutcomeActor actor, long? correlation, string? team,
        TriggerAudit audit_, CancellationToken ct = default)
    {
        if (OutcomeRules.NameRefusal(name) is { } bad) return Task.FromResult(OutcomeWrite.Refused(400, bad));

        return WriteAsync(
            async (connection, transaction, token) =>
            {
                // ASKED FIRST, SO A REFUSED LINK CREATES NOTHING.
                if (correlation is { } asked
                    && await SqlServerOutcomeLinks.CurrentAsync(connection, transaction, asked, token, forUpdate: true) is { } current
                    && !OutcomeLinkHow.AgentMayReplace(current))
                {
                    return OutcomeWrite.Refused(409, OutcomeRules.PersonsLink(asked, current));
                }

                var existing = await LiveByNameAsync(connection, transaction, name, token);
                var outcome = existing ?? await InsertAsync(
                    connection, transaction, name, new OutcomeEdit(Description: description),
                    OutcomeStatus.Proposed, actor, confirmed: false, token);

                OutcomeLink? link = null;
                if (correlation is { } workflow)
                {
                    link = await SqlServerOutcomeLinks.WriteAsync(
                        connection, transaction, workflow, outcome.Id, team, actor.Id, actor.Kind, OutcomeLinkHow.Manager, token);
                }

                // ONE ROW PER WRITE, each naming the member: the outcome it created, and the link it made.
                if (existing is null)
                {
                    await audit(connection, transaction, audit_ with { Subject = outcome.Id, SubjectName = outcome.Name }, token);
                }

                if (link is not null)
                {
                    await audit(connection, transaction, audit_ with
                    {
                        Action = TenantActions.WorkflowOutcomeChanged,
                        Subject = outcome.Id,
                        SubjectName = outcome.Name,
                    }, token);
                }

                return new OutcomeWrite(outcome, link, Created: existing is null, Status: existing is null ? 201 : 200);
            },
            ct);
    }

    public Task<OutcomeWrite> LinkAsync(
        long correlation, string outcomeId, string? team, OutcomeActor actor, string how, bool agentRule,
        TriggerAudit audit_, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                if (await SqlServerOutcomeLinks.ResolveAsync(connection, transaction, outcomeId, token, forUpdate: true) is not { } resolved
                    || await FindAsync(connection, transaction, resolved.Id, token, forUpdate: true) is not { } outcome)
                {
                    return OutcomeRules.Missing(outcomeId);
                }

                if (!outcome.IsLive) return OutcomeWrite.Refused(409, OutcomeRules.NotLive(outcome));

                var current = await SqlServerOutcomeLinks.CurrentAsync(connection, transaction, correlation, token, forUpdate: true);

                if (agentRule && !OutcomeLinkHow.AgentMayReplace(current))
                {
                    return OutcomeWrite.Refused(409, OutcomeRules.PersonsLink(correlation, current!));
                }

                var link = await SqlServerOutcomeLinks.WriteAsync(
                    connection, transaction, correlation, outcome.Id, team, actor.Id, actor.Kind, how, token);

                await audit(connection, transaction, audit_ with { Subject = outcome.Id, SubjectName = outcome.Name }, token);

                return new OutcomeWrite(outcome, link);
            },
            ct);

    public Task<OutcomeWrite> UnlinkAsync(
        long correlation, string? team, OutcomeActor person, TriggerAudit audit_, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                if (await SqlServerOutcomeLinks.CurrentAsync(connection, transaction, correlation, token, forUpdate: true)
                    is not { IsUnlink: false } current)
                {
                    return OutcomeWrite.Refused(409, OutcomeRules.NothingToUnlink(correlation));
                }

                var link = await SqlServerOutcomeLinks.WriteAsync(
                    connection, transaction, correlation, null, team, person.Id, person.Kind, OutcomeLinkHow.Person, token);

                // THE ROW SAYS WHAT IT LEFT: `from` the outcome the workflow served, `to: null`.
                var from = await FindAsync(connection, transaction, current.OutcomeId!, token);
                await audit(connection, transaction, audit_ with
                {
                    Subject = current.OutcomeId,
                    SubjectName = from?.Name ?? current.OutcomeNameAtLink,
                    Detail = OutcomeRules.UnlinkDetail(team, correlation, current.OutcomeId),
                }, token);

                return new OutcomeWrite(null, link);
            },
            ct);

    public async Task<OutcomeWrite> EditAsync(
        string id, OutcomeEdit edit, TriggerAudit renamed, TriggerAudit changed, CancellationToken ct = default)
    {
        if (edit.Name is { } name && OutcomeRules.NameRefusal(name) is { } bad) return OutcomeWrite.Refused(400, bad);

        var unchanged = false;

        var write = await WriteAsync(
            async (connection, transaction, token) =>
            {
                unchanged = false;

                if (await FindAsync(connection, transaction, id, token, forUpdate: true) is not { } outcome)
                {
                    return OutcomeRules.Missing(id);
                }

                if (outcome.Status == OutcomeStatus.Merged) return OutcomeWrite.Refused(409, OutcomeRules.MergedEdit(outcome));

                var newName = edit.Name is null ? outcome.Name : OutcomeNames.Clean(edit.Name);
                var isRename = !string.Equals(newName, outcome.Name, StringComparison.Ordinal);

                if (isRename && outcome.IsLive
                    && await LiveByNameAsync(connection, transaction, newName, token) is { } taken && taken.Id != outcome.Id)
                {
                    return OutcomeWrite.Refused(409, OutcomeRules.Taken(taken));
                }

                if (OutcomeRules.ValueRefusal(edit.Value) is { } badValue) return OutcomeWrite.Refused(400, badValue);

                var next = OutcomeRules.Edited(outcome, edit);

                if (next == outcome)
                {
                    unchanged = true;
                    return new OutcomeWrite(outcome);
                }

                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText =
                        """
                        UPDATE outcomes SET name = @name, name_key = @key, description = @description,
                            target_metric = @metric, target_unit = @unit, target_value = @value,
                            value_amount = @amount, updated_at = @at
                        WHERE id = @id
                        """;
                    update.Parameters.AddWithValue("@id", id);
                    update.Parameters.AddWithValue("@name", next.Name);
                    update.Parameters.AddWithValue("@key", OutcomeNames.Key(next.Name));
                    update.Parameters.AddWithValue("@description", next.Description);
                    update.Parameters.AddWithValue("@metric", (object?)next.TargetMetric ?? DBNull.Value);
                    update.Parameters.AddWithValue("@unit", (object?)next.TargetUnit ?? DBNull.Value);
                    update.Parameters.AddWithValue("@value", (object?)next.TargetValue ?? DBNull.Value);
                    update.Parameters.AddWithValue("@amount", (object?)next.Value ?? DBNull.Value);
                    update.Parameters.AddWithValue("@at", Now());
                    await update.ExecuteNonQueryAsync(token);
                }

                var row = isRename ? renamed : changed;
                await audit(connection, transaction, row with { Subject = id, SubjectName = next.Name }, token);

                return new OutcomeWrite(next);
            },
            ct);

        // As SQLite answers it: the outcome read again after the commit, unless nothing changed.
        return write.Ok && !unchanged ? new OutcomeWrite(await FindAsync(id, ct)) : write;
    }

    public Task<OutcomeWrite> ConfirmAsync(string id, OutcomeActor person, TriggerAudit audit_, CancellationToken ct = default) =>
        TransitionAsync(id, audit_, ct, OutcomeRules.ConfirmRefusal, OutcomeStatus.Active, confirmedBy: person.Id);

    public Task<OutcomeWrite> RetireAsync(string id, TriggerAudit audit_, CancellationToken ct = default) =>
        TransitionAsync(id, audit_, ct, OutcomeRules.RetireRefusal, OutcomeStatus.Retired);

    public Task<OutcomeWrite> ReactivateAsync(string id, TriggerAudit audit_, CancellationToken ct = default) =>
        TransitionAsync(id, audit_, ct, OutcomeRules.ReactivateRefusal, OutcomeStatus.Active);

    public async Task<OutcomeWrite> MergeAsync(string id, string into, TriggerAudit audit_, CancellationToken ct = default)
    {
        var write = await WriteAsync(
            async (connection, transaction, token) =>
            {
                if (await FindAsync(connection, transaction, id, token, forUpdate: true) is not { } outcome) return OutcomeRules.Missing(id);
                if (await FindAsync(connection, transaction, into, token, forUpdate: true) is not { } target) return OutcomeRules.Missing(into);

                if (OutcomeRules.MergeRefusal(outcome, target) is { } refusal) return OutcomeWrite.Refused(409, refusal);

                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE outcomes SET status = @merged, merged_into = @into, updated_at = @at WHERE id = @id";
                    update.Parameters.AddWithValue("@id", id);
                    update.Parameters.AddWithValue("@into", target.Id);
                    update.Parameters.AddWithValue("@merged", OutcomeStatus.Merged);
                    update.Parameters.AddWithValue("@at", Now());
                    await update.ExecuteNonQueryAsync(token);
                }

                await audit(connection, transaction, audit_ with { Subject = id, SubjectName = outcome.Name }, token);

                return new OutcomeWrite(outcome);
            },
            ct);

        return write.Ok ? new OutcomeWrite(await FindAsync(id, ct)) : write;
    }

    public Task<OutcomeWrite> RejectAsync(string id, TriggerAudit audit_, CancellationToken ct = default) =>
        WriteAsync(
            async (connection, transaction, token) =>
            {
                if (await FindAsync(connection, transaction, id, token, forUpdate: true) is not { } outcome) return OutcomeRules.Missing(id);

                if (outcome.Status != OutcomeStatus.Proposed) return OutcomeWrite.Refused(409, OutcomeRules.NotProposed(outcome));

                var links = await CountAsync(connection, transaction,
                    $"SELECT COUNT_BIG(*) FROM workflow_outcome_links {Locked} WHERE outcome_id = @id", id, token);
                var mergedIn = await CountAsync(connection, transaction,
                    $"SELECT COUNT_BIG(*) FROM outcomes {Locked} WHERE merged_into = @id", id, token);

                if (links > 0 || mergedIn > 0) return OutcomeWrite.Refused(409, OutcomeRules.StillNamed(outcome, links, mergedIn));

                await DeleteAsync(connection, transaction, id, token);
                await audit(connection, transaction, audit_ with { Subject = id, SubjectName = outcome.Name }, token);

                return new OutcomeWrite(outcome);
            },
            ct);

    /// <summary>
    /// Removes an outcome a failed solution install created, when nothing links to it: SQLite's
    /// <c>SqliteOutcomeStore.UndoCreateAsync</c>, the install's undo with its own tenant row.
    /// </summary>
    public Task<bool> UndoCreateAsync(string id, TriggerAudit audit_, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                if (await CountAsync(connection, transaction,
                        $"SELECT COUNT_BIG(*) FROM workflow_outcome_links {Locked} WHERE outcome_id = @id", id, token) > 0)
                {
                    return false;
                }

                if (await DeleteAsync(connection, transaction, id, token) == 0) return false;

                await audit(connection, transaction, audit_ with { Subject = id }, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    public Task<OutcomeLink?> CurrentLinkAsync(long correlation, CancellationToken ct = default) =>
        RunAsync((connection, token) => SqlServerOutcomeLinks.CurrentAsync(connection, null, correlation, token), ct);

    /// <summary>
    /// ONE STATEMENT, as SQLite's: each correlation's newest link, its <c>merged_into</c> chain walked
    /// in a recursive CTE (at most <see cref="OutcomeFacts.MaxHops"/> hops), the last outcome on the
    /// chain the answer. The correlations go as one JSON parameter, past SQL Server's 2,100-parameter cap.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, WorkflowOutcome>> CurrentOutcomesAsync(
        IReadOnlyCollection<long> correlations, CancellationToken ct = default)
    {
        var asked = correlations.Distinct().ToList();
        if (asked.Count == 0) return new Dictionary<long, WorkflowOutcome>();

        return await RunAsync<IReadOnlyDictionary<long, WorkflowOutcome>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     WITH newest (correlation, outcome_id) AS (
                         SELECT l.correlation, l.outcome_id FROM workflow_outcome_links l
                         WHERE l.correlation IN (SELECT CAST(value AS BIGINT) FROM OPENJSON(@correlations))
                           AND l.id = (SELECT MAX(n.id) FROM workflow_outcome_links n WHERE n.correlation = l.correlation)
                     ),
                     chain (correlation, id, hop) AS (
                         SELECT correlation, outcome_id, 0 FROM newest
                         UNION ALL
                         SELECT c.correlation, o.merged_into, c.hop + 1
                         FROM chain c JOIN outcomes o ON o.id = c.id
                         WHERE o.status = N'{OutcomeStatus.Merged}' AND o.merged_into IS NOT NULL AND c.hop < {OutcomeFacts.MaxHops}
                     )
                     SELECT c.correlation, o.id, o.name, o.status
                     FROM chain c JOIN outcomes o ON o.id = c.id
                     ORDER BY c.correlation, c.hop
                     """;
                command.Parameters.Add("@correlations", System.Data.SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(asked);

                // Ordered by hop, so the last row read for a correlation is the end of its chain.
                var found = new Dictionary<long, WorkflowOutcome>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    found[reader.GetInt64(0)] = new WorkflowOutcome(reader.GetString(1), reader.GetString(2), reader.GetString(3));
                }

                return found;
            },
            ct);
    }

    public Task<IReadOnlyList<OutcomeLink>> ReadLinksAsync(CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {OutcomeFacts.LinkColumns} FROM workflow_outcome_links ORDER BY id";
                return await OutcomeFacts.ReadLinksAsync(command, token);
            },
            ct);

    public async Task<IReadOnlyList<TenantEvent>> ReadEventsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];

        return await RunAsync<IReadOnlyList<TenantEvent>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     SELECT {OutcomeFacts.EventColumns}
                     FROM tenant_events
                     WHERE action LIKE N'outcome.%' {SqlServerSql.Like}
                       AND subject IN (SELECT value FROM OPENJSON(@ids))
                     ORDER BY seq
                     """;
                command.Parameters.Add("@ids", System.Data.SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(ids);

                var rows = new List<TenantEvent>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(OutcomeFacts.ReadEvent(reader));
                return rows;
            },
            ct);
    }

    public Task<IReadOnlyList<OutcomeBacklogItem>> ReadBacklogAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<OutcomeBacklogItem>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT i.id, i.title, i.state, CASE WHEN i.archived_at IS NOT NULL THEN 1 ELSE 0 END, i.outcome_id,
                           d.team_id, d.team_name, d.correlation, d.dispatched_at, d.landed_at
                    FROM backlog_items i
                    LEFT JOIN backlog_dispatches d ON d.id = (SELECT MAX(n.id) FROM backlog_dispatches n WHERE n.item = i.id)
                    ORDER BY i.id
                    """;

                var rows = new List<OutcomeBacklogItem>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(OutcomeFacts.ReadBacklogItem(reader));
                return rows;
            },
            ct);

    public Task<OutcomeLedgerRows> ReadLedgerAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var links = connection.CreateCommand();
                links.CommandText =
                    $"""
                     SELECT {OutcomeFacts.LinkColumns} FROM workflow_outcome_links l
                     WHERE l.id = (SELECT MAX(n.id) FROM workflow_outcome_links n WHERE n.correlation = l.correlation)
                     ORDER BY l.correlation
                     """;
                var current = await OutcomeFacts.ReadLinksAsync(links, token);

                await using var runs = connection.CreateCommand();
                runs.CommandText =
                    $"""
                     SELECT {LedgerFacts.RunColumns}
                     FROM usage_ledger
                     WHERE ended_at >= @from AND ended_at < @to
                     ORDER BY run_seq
                     """;
                runs.Parameters.AddWithValue("@from", Instant.Stamp(from ?? DateTimeOffset.MinValue));
                runs.Parameters.AddWithValue("@to", Instant.Stamp(to ?? DateTimeOffset.MaxValue));
                var runRows = await LedgerFacts.ReadRunsAsync(runs, token);

                // EVERY WORKFLOW'S NEWEST CLOSE, whatever the window.
                await using var closes = connection.CreateCommand();
                closes.CommandText =
                    $"""
                     SELECT {LedgerFacts.WorkflowColumns}
                     FROM workflow_ledger w
                     WHERE w.close_seq = (SELECT MAX(n.close_seq) FROM workflow_ledger n WHERE n.correlation = w.correlation)
                     ORDER BY w.correlation
                     """;
                var closeRows = await LedgerFacts.ReadWorkflowsAsync(closes, token);

                await using var teams = connection.CreateCommand();
                // The id is a case-insensitive column and the name is not, so the fallback names the
                // collation the answer is compared in, or SQL Server cannot choose between the two.
                teams.CommandText = $"SELECT id, COALESCE(name, id COLLATE {SqlServerMigrator.RequiredCollation}) FROM teams";
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                await using (var reader = await teams.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token)) names[reader.GetString(0)] = reader.GetString(1);
                }

                return new OutcomeLedgerRows(current, runRows, closeRows, names);
            },
            ct);

    // ---- helpers ----

    private async Task<OutcomeWrite> TransitionAsync(
        string id, TriggerAudit audit_, CancellationToken ct, Func<Outcome, Outcome?, string?> refusal,
        string status, string? confirmedBy = null)
    {
        var write = await WriteAsync(
            async (connection, transaction, token) =>
            {
                if (await FindAsync(connection, transaction, id, token, forUpdate: true) is not { } outcome) return OutcomeRules.Missing(id);

                var taken = OutcomeStatus.IsLive(status)
                    ? await LiveByNameAsync(connection, transaction, outcome.Name, token) is { } other && other.Id != id ? other : null
                    : null;

                if (refusal(outcome, taken) is { } sentence) return OutcomeWrite.Refused(409, sentence);

                await using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = confirmedBy is null
                        ? "UPDATE outcomes SET status = @status, updated_at = @at WHERE id = @id"
                        : "UPDATE outcomes SET status = @status, updated_at = @at, confirmed_by = @by, confirmed_at = @at WHERE id = @id";
                    update.Parameters.AddWithValue("@id", id);
                    update.Parameters.AddWithValue("@status", status);
                    update.Parameters.AddWithValue("@at", Now());
                    if (confirmedBy is not null) update.Parameters.AddWithValue("@by", confirmedBy);
                    await update.ExecuteNonQueryAsync(token);
                }

                await audit(connection, transaction, audit_ with { Subject = id, SubjectName = outcome.Name }, token);

                return new OutcomeWrite(outcome);
            },
            ct);

        return write.Ok ? new OutcomeWrite(await FindAsync(id, ct)) : write;
    }

    private static async Task<Outcome> InsertAsync(
        SqlConnection connection, SqlTransaction transaction, string name, OutcomeEdit fields,
        string status, OutcomeActor actor, bool confirmed, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("D");
        var clean = OutcomeNames.Clean(name);
        var now = Now();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO outcomes (id, name, name_key, description, status, merged_into, source,
                target_metric, target_unit, target_value, created_by, created_by_kind, created_at,
                updated_at, confirmed_by, confirmed_at, value_amount)
            VALUES (@id, @name, @key, @description, @status, NULL, N'local', @metric, @unit, @value,
                @by, @kind, @at, @at, @confirmedBy, @confirmedAt, @amount)
            """;
        insert.Parameters.AddWithValue("@id", id);
        insert.Parameters.AddWithValue("@name", clean);
        insert.Parameters.AddWithValue("@key", OutcomeNames.Key(clean));
        insert.Parameters.AddWithValue("@description", fields.Description?.Trim() ?? "");
        insert.Parameters.AddWithValue("@status", status);
        insert.Parameters.AddWithValue("@metric", (object?)OutcomeRules.Target(fields.TargetMetric, null) ?? DBNull.Value);
        insert.Parameters.AddWithValue("@unit", (object?)OutcomeRules.Target(fields.TargetUnit, null) ?? DBNull.Value);
        insert.Parameters.AddWithValue("@value", (object?)OutcomeRules.Target(fields.TargetValue, null) ?? DBNull.Value);
        insert.Parameters.AddWithValue("@amount", (object?)OutcomeRules.NewValue(fields.Value) ?? DBNull.Value);
        insert.Parameters.AddWithValue("@by", actor.Id);
        insert.Parameters.AddWithValue("@kind", actor.Kind);
        insert.Parameters.AddWithValue("@at", now);
        insert.Parameters.AddWithValue("@confirmedBy", confirmed ? actor.Id : DBNull.Value);
        insert.Parameters.AddWithValue("@confirmedAt", confirmed ? now : DBNull.Value);
        await insert.ExecuteNonQueryAsync(ct);

        return (await FindAsync(connection, transaction, id, ct))!;
    }

    private static async Task<Outcome?> FindAsync(
        SqlConnection connection, SqlTransaction? transaction, string id, CancellationToken ct, bool forUpdate = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {OutcomeColumns} FROM outcomes {(forUpdate ? Locked : "")} WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        var rows = await OutcomeFacts.ReadOutcomesAsync(command, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// The live outcome of <paramref name="name"/>'s key. Inside a write it holds the key's range,
    /// so a second write of the same name waits and then finds the first's row instead of failing on
    /// the unique index.
    /// </summary>
    private static async Task<Outcome?> LiveByNameAsync(
        SqlConnection connection, SqlTransaction? transaction, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {OutcomeColumns} FROM outcomes {(transaction is null ? "" : Locked)} WHERE name_key = @key AND status IN (N'proposed', N'active')";
        command.Parameters.AddWithValue("@key", OutcomeNames.Key(name));
        var rows = await OutcomeFacts.ReadOutcomesAsync(command, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<long> CountAsync(
        SqlConnection connection, SqlTransaction transaction, string sql, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", id);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<int> DeleteAsync(SqlConnection connection, SqlTransaction transaction, string id, CancellationToken ct)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM outcomes WHERE id = @id";
        delete.Parameters.AddWithValue("@id", id);
        return await delete.ExecuteNonQueryAsync(ct);
    }
}
