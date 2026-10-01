using System.Globalization;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>
/// <see cref="IOutcomeStore"/> over <c>outcomes</c> and <c>workflow_outcome_links</c> in the log's
/// own database file.
///
/// <para>
/// EVERY WRITE AND ITS TENANT ROW ARE ONE TRANSACTION. The row is written through
/// <paramref name="audit"/> (the Host hands in <c>TenantAuditRow.AppendAsync</c>, the tenant log's
/// own columns), inside the write's transaction: when it cannot be written, the write does not
/// happen either.
/// </para>
///
/// <para>
/// NOTHING HERE REWRITES A LINK. A rename changes <c>outcomes.name</c> only, a merge sets
/// <c>merged_into</c> only, and each link keeps the outcome id and the name it was made with; reads
/// follow <c>merged_into</c>.
/// </para>
/// </summary>
public sealed class SqliteOutcomeStore(
    string databasePath,
    Func<SqliteConnection, SqliteTransaction, TriggerAudit, CancellationToken, Task> audit) : IOutcomeStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    private const string OutcomeColumns =
        """
        id, name, description, status, merged_into, source, target_metric, target_unit, target_value,
        created_by, created_by_kind, created_at, updated_at, confirmed_by, confirmed_at
        """;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqlitePragmas.Apply(connection);
        return connection;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<Outcome>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {OutcomeColumns} FROM outcomes ORDER BY created_at, id";
        return await ReadOutcomesAsync(command, ct);
    }

    public async Task<Outcome?> FindAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        return await FindAsync(connection, null, id, ct);
    }

    public async Task<Outcome?> FindLiveByNameAsync(string name, CancellationToken ct = default)
    {
        await using var connection = Open();
        return await LiveByNameAsync(connection, null, name, ct);
    }

    public async Task<Outcome?> ResolveLiveAsync(string idOrName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;

        await using var connection = Open();
        var text = idOrName.Trim();

        if (await FindAsync(connection, null, text, ct) is { IsLive: true } byId) return byId;

        // THE EXACT NAME, as a person typed it: the stored name, trimmed, compared ordinally.
        var byName = await LiveByNameAsync(connection, null, text, ct);
        return byName is not null && string.Equals(byName.Name, OutcomeNames.Clean(text), StringComparison.Ordinal)
            ? byName
            : null;
    }

    public async Task<OutcomeWrite> CreateAsync(
        string name, OutcomeEdit fields, OutcomeActor person, TriggerAudit audit_, CancellationToken ct = default)
    {
        if (NameRefusal(name) is { } bad) return OutcomeWrite.Refused(400, bad);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await LiveByNameAsync(connection, transaction, name, ct) is { } taken)
        {
            return OutcomeWrite.Refused(409, Taken(taken));
        }

        var created = await InsertAsync(
            connection, transaction, name, fields, OutcomeStatus.Active, person, confirmed: true, ct);

        await audit(connection, transaction, audit_ with { Subject = created.Id, SubjectName = created.Name }, ct);
        await transaction.CommitAsync(ct);

        return new OutcomeWrite(created, Created: true, Status: 201);
    }

    public async Task<OutcomeWrite> ProposeAsync(
        string name, string? description, OutcomeActor actor, long? correlation, string? team,
        TriggerAudit audit_, CancellationToken ct = default)
    {
        if (NameRefusal(name) is { } bad) return OutcomeWrite.Refused(400, bad);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        // ASKED FIRST, SO A REFUSED LINK CREATES NOTHING.
        if (correlation is { } asked
            && await OutcomeLinks.CurrentAsync(connection, transaction, asked, ct) is { } current
            && !OutcomeLinkHow.AgentMayReplace(current))
        {
            return OutcomeWrite.Refused(409, PersonsLink(asked, current));
        }

        var existing = await LiveByNameAsync(connection, transaction, name, ct);
        var outcome = existing ?? await InsertAsync(
            connection, transaction, name, new OutcomeEdit(Description: description),
            OutcomeStatus.Proposed, actor, confirmed: false, ct);

        OutcomeLink? link = null;
        if (correlation is { } workflow)
        {
            link = await OutcomeLinks.WriteAsync(
                connection, transaction, workflow, outcome.Id, team, actor.Id, actor.Kind, OutcomeLinkHow.Manager, ct);
        }

        // ONE ROW PER WRITE, each naming the member: the outcome it created, and the link it made.
        if (existing is null)
        {
            await audit(connection, transaction, audit_ with { Subject = outcome.Id, SubjectName = outcome.Name }, ct);
        }

        if (link is not null)
        {
            await audit(connection, transaction, audit_ with
            {
                Action = TenantActions.WorkflowOutcomeChanged,
                Subject = outcome.Id,
                SubjectName = outcome.Name,
            }, ct);
        }

        await transaction.CommitAsync(ct);

        return new OutcomeWrite(outcome, link, Created: existing is null, Status: existing is null ? 201 : 200);
    }

    public async Task<OutcomeWrite> LinkAsync(
        long correlation, string outcomeId, string? team, OutcomeActor actor, string how, bool agentRule,
        TriggerAudit audit_, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await OutcomeLinks.ResolveAsync(connection, transaction, outcomeId, ct) is not { } resolved
            || await FindAsync(connection, transaction, resolved.Id, ct) is not { } outcome)
        {
            return OutcomeWrite.Refused(404, $"No outcome '{outcomeId}'.");
        }

        if (!outcome.IsLive)
        {
            return OutcomeWrite.Refused(409,
                $"The outcome \"{outcome.Name}\" is {outcome.Status}; a workflow is linked only to an active or proposed outcome.");
        }

        var current = await OutcomeLinks.CurrentAsync(connection, transaction, correlation, ct);

        if (agentRule && !OutcomeLinkHow.AgentMayReplace(current))
        {
            return OutcomeWrite.Refused(409, PersonsLink(correlation, current!));
        }

        var link = await OutcomeLinks.WriteAsync(
            connection, transaction, correlation, outcome.Id, team, actor.Id, actor.Kind, how, ct);

        await audit(connection, transaction, audit_ with { Subject = outcome.Id, SubjectName = outcome.Name }, ct);
        await transaction.CommitAsync(ct);

        return new OutcomeWrite(outcome, link);
    }

    public async Task<OutcomeWrite> EditAsync(
        string id, OutcomeEdit edit, TriggerAudit renamed, TriggerAudit changed, CancellationToken ct = default)
    {
        if (edit.Name is { } name && NameRefusal(name) is { } bad) return OutcomeWrite.Refused(400, bad);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await FindAsync(connection, transaction, id, ct) is not { } outcome) return Missing(id);

        if (outcome.Status == OutcomeStatus.Merged)
        {
            return OutcomeWrite.Refused(409, $"\"{outcome.Name}\" was merged; edit the outcome it was merged into.");
        }

        var newName = edit.Name is null ? outcome.Name : OutcomeNames.Clean(edit.Name);
        var isRename = !string.Equals(newName, outcome.Name, StringComparison.Ordinal);

        if (isRename && outcome.IsLive
            && await LiveByNameAsync(connection, transaction, newName, ct) is { } taken && taken.Id != outcome.Id)
        {
            return OutcomeWrite.Refused(409, Taken(taken));
        }

        var next = outcome with
        {
            Name = newName,
            Description = edit.Description?.Trim() ?? outcome.Description,
            TargetMetric = Target(edit.TargetMetric, outcome.TargetMetric),
            TargetUnit = Target(edit.TargetUnit, outcome.TargetUnit),
            TargetValue = Target(edit.TargetValue, outcome.TargetValue),
        };

        if (next == outcome)
        {
            return new OutcomeWrite(outcome);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE outcomes SET name = $name, name_key = $key, description = $description,
                    target_metric = $metric, target_unit = $unit, target_value = $value, updated_at = $at
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$name", next.Name);
            update.Parameters.AddWithValue("$key", OutcomeNames.Key(next.Name));
            update.Parameters.AddWithValue("$description", next.Description);
            update.Parameters.AddWithValue("$metric", (object?)next.TargetMetric ?? DBNull.Value);
            update.Parameters.AddWithValue("$unit", (object?)next.TargetUnit ?? DBNull.Value);
            update.Parameters.AddWithValue("$value", (object?)next.TargetValue ?? DBNull.Value);
            update.Parameters.AddWithValue("$at", Now());
            await update.ExecuteNonQueryAsync(ct);
        }

        var row = isRename ? renamed : changed;
        await audit(connection, transaction, row with { Subject = id, SubjectName = next.Name }, ct);
        await transaction.CommitAsync(ct);

        return new OutcomeWrite(await FindAsync(id, ct));
    }

    public Task<OutcomeWrite> ConfirmAsync(string id, OutcomeActor person, TriggerAudit audit_, CancellationToken ct = default) =>
        TransitionAsync(id, audit_, ct, (outcome, _) => outcome.Status == OutcomeStatus.Proposed
                ? null
                : $"\"{outcome.Name}\" is {outcome.Status}; only a proposed outcome is confirmed.",
            OutcomeStatus.Active, confirmedBy: person.Id);

    public Task<OutcomeWrite> RetireAsync(string id, TriggerAudit audit_, CancellationToken ct = default) =>
        TransitionAsync(id, audit_, ct, (outcome, _) => outcome.IsLive
                ? null
                : $"\"{outcome.Name}\" is {outcome.Status}; only an active or proposed outcome is retired.",
            OutcomeStatus.Retired);

    public Task<OutcomeWrite> ReactivateAsync(string id, TriggerAudit audit_, CancellationToken ct = default) =>
        TransitionAsync(id, audit_, ct, (outcome, taken) => outcome.Status != OutcomeStatus.Retired
                ? $"\"{outcome.Name}\" is {outcome.Status}; only a retired outcome is reactivated."
                : taken is not null ? Taken(taken) : null,
            OutcomeStatus.Active);

    public async Task<OutcomeWrite> MergeAsync(string id, string into, TriggerAudit audit_, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await FindAsync(connection, transaction, id, ct) is not { } outcome) return Missing(id);
        if (await FindAsync(connection, transaction, into, ct) is not { } target) return Missing(into);

        if (MergeRefusal(outcome, target) is { } refusal) return OutcomeWrite.Refused(409, refusal);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE outcomes SET status = $merged, merged_into = $into, updated_at = $at WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$into", target.Id);
            update.Parameters.AddWithValue("$merged", OutcomeStatus.Merged);
            update.Parameters.AddWithValue("$at", Now());
            await update.ExecuteNonQueryAsync(ct);
        }

        await audit(connection, transaction, audit_ with { Subject = id, SubjectName = outcome.Name }, ct);
        await transaction.CommitAsync(ct);

        return new OutcomeWrite(await FindAsync(id, ct));
    }

    /// <summary>Why <paramref name="outcome"/> cannot be merged into <paramref name="target"/>, or null.
    /// The preview asks the same.</summary>
    public static string? MergeRefusal(Outcome outcome, Outcome target)
    {
        if (outcome.Id == target.Id) return "An outcome cannot be merged into itself.";
        if (!outcome.IsLive && outcome.Status != OutcomeStatus.Retired)
        {
            return $"\"{outcome.Name}\" is already merged.";
        }

        return target.IsLive
            ? null
            : $"\"{target.Name}\" is {target.Status}; an outcome is merged only into an active or proposed one.";
    }

    public async Task<OutcomeWrite> RejectAsync(string id, TriggerAudit audit_, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await FindAsync(connection, transaction, id, ct) is not { } outcome) return Missing(id);

        if (outcome.Status != OutcomeStatus.Proposed)
        {
            return OutcomeWrite.Refused(409,
                $"\"{outcome.Name}\" is {outcome.Status}; only a proposed outcome is rejected. Retire it instead.");
        }

        var links = await CountAsync(connection, transaction,
            "SELECT COUNT(*) FROM workflow_outcome_links WHERE outcome_id = $id", id, ct);
        var mergedIn = await CountAsync(connection, transaction,
            "SELECT COUNT(*) FROM outcomes WHERE merged_into = $id", id, ct);

        if (links > 0 || mergedIn > 0)
        {
            return OutcomeWrite.Refused(409,
                $"\"{outcome.Name}\" is linked to {links} workflow{(links == 1 ? "" : "s")}"
                + (mergedIn > 0 ? $" and has {mergedIn} outcome{(mergedIn == 1 ? "" : "s")} merged into it" : "")
                + ", so it is not rejected: a link is never rewritten. Merge it into another outcome, or retire it.");
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM outcomes WHERE id = $id";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await audit(connection, transaction, audit_ with { Subject = id, SubjectName = outcome.Name }, ct);
        await transaction.CommitAsync(ct);

        return new OutcomeWrite(outcome);
    }

    /// <summary>
    /// Removes an outcome a failed solution install created, when nothing links to it. The install's
    /// undo, with the install's own tenant row; not a person's reject.
    /// </summary>
    public async Task<bool> UndoCreateAsync(string id, TriggerAudit audit_, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await CountAsync(connection, transaction,
                "SELECT COUNT(*) FROM workflow_outcome_links WHERE outcome_id = $id", id, ct) > 0)
        {
            return false;
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM outcomes WHERE id = $id";
            delete.Parameters.AddWithValue("$id", id);
            if (await delete.ExecuteNonQueryAsync(ct) == 0) return false;
        }

        await audit(connection, transaction, audit_ with { Subject = id }, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<OutcomeLink?> CurrentLinkAsync(long correlation, CancellationToken ct = default)
    {
        await using var connection = Open();
        return await OutcomeLinks.CurrentAsync(connection, null, correlation, ct);
    }

    public async Task<IReadOnlyList<OutcomeLink>> ReadLinksAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {OutcomeLinks.Columns} FROM workflow_outcome_links ORDER BY id";
        return await ReadLinksAsync(command, ct);
    }

    public async Task<IReadOnlyList<TenantEvent>> ReadEventsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        var parameters = ids.Select((id, i) =>
        {
            command.Parameters.AddWithValue($"$id{i}", id);
            return $"$id{i}";
        }).ToList();
        command.CommandText =
            $"""
            SELECT seq, occurred_at, actor_id, actor_email, action, subject, subject_name, detail
            FROM tenant_events
            WHERE action LIKE 'outcome.%' AND subject IN ({string.Join(", ", parameters)})
            ORDER BY seq
            """;

        var rows = new List<TenantEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TenantEvent(
                reader.GetInt64(0), MessageRows.ReadStamp(reader.GetString(1)), Text(reader, 2), Text(reader, 3),
                reader.GetString(4), Text(reader, 5), Text(reader, 6), Text(reader, 7)));
        }

        return rows;
    }

    public async Task<OutcomeLedgerRows> ReadLedgerAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
    {
        await using var connection = Open();

        await using var links = connection.CreateCommand();
        links.CommandText =
            $"""
             SELECT {OutcomeLinks.Columns} FROM workflow_outcome_links l
             WHERE l.id = (SELECT MAX(n.id) FROM workflow_outcome_links n WHERE n.correlation = l.correlation)
             ORDER BY l.correlation
             """;
        var current = await ReadLinksAsync(links, ct);

        await using var runs = connection.CreateCommand();
        runs.CommandText =
            """
            SELECT run_seq, correlation, team_id, team_name, member, member_kind, agent_preset, run_outcome,
                   queued_at, started_at, ended_at, measured, tokens_in, tokens_cached_in, tokens_cache_creation,
                   tokens_out, tokens_combined, billable, trigger_source, trigger_fired_at, backfilled
            FROM usage_ledger
            WHERE ended_at >= $from AND ended_at < $to
            ORDER BY run_seq
            """;
        runs.Parameters.AddWithValue("$from", Stamp(from ?? DateTimeOffset.MinValue));
        runs.Parameters.AddWithValue("$to", Stamp(to ?? DateTimeOffset.MaxValue));

        var runRows = new List<UsageLedgerRow>();
        await using (var reader = await runs.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                runRows.Add(new UsageLedgerRow(
                    reader.GetInt64(0), reader.GetInt64(1), Text(reader, 2), Text(reader, 3), reader.GetString(4),
                    Text(reader, 5), Text(reader, 6), reader.GetString(7), At(reader, 8), At(reader, 9),
                    MessageRows.ReadStamp(reader.GetString(10)), reader.GetInt64(11) != 0, Long(reader, 12),
                    Long(reader, 13), Long(reader, 14), Long(reader, 15), Long(reader, 16), Long(reader, 17),
                    Text(reader, 18), At(reader, 19), reader.GetInt64(20) != 0));
            }
        }

        // EVERY WORKFLOW'S NEWEST CLOSE, whatever the window: whether a workflow is open or completed
        // is its state now.
        await using var closes = connection.CreateCommand();
        closes.CommandText =
            """
            SELECT close_seq, correlation, team_id, team_name, root_at, closed_at, how_closed,
                   outcome_id_at_close, backfilled
            FROM workflow_ledger w
            WHERE w.close_seq = (SELECT MAX(n.close_seq) FROM workflow_ledger n WHERE n.correlation = w.correlation)
            ORDER BY w.correlation
            """;

        var closeRows = new List<WorkflowLedgerRow>();
        await using (var reader = await closes.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                closeRows.Add(new WorkflowLedgerRow(
                    reader.GetInt64(0), reader.GetInt64(1), Text(reader, 2), Text(reader, 3), At(reader, 4),
                    MessageRows.ReadStamp(reader.GetString(5)), reader.GetString(6), Text(reader, 7),
                    reader.GetInt64(8) != 0));
            }
        }

        await using var teams = connection.CreateCommand();
        teams.CommandText = "SELECT id, COALESCE(name, id) FROM teams";
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await teams.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) names[reader.GetString(0)] = reader.GetString(1);
        }

        return new OutcomeLedgerRows(current, runRows, closeRows, names);
    }

    // ---- helpers ----

    private async Task<OutcomeWrite> TransitionAsync(
        string id, TriggerAudit audit_, CancellationToken ct, Func<Outcome, Outcome?, string?> refusal,
        string status, string? confirmedBy = null)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await FindAsync(connection, transaction, id, ct) is not { } outcome) return Missing(id);

        var taken = OutcomeStatus.IsLive(status)
            ? await LiveByNameAsync(connection, transaction, outcome.Name, ct) is { } other && other.Id != id ? other : null
            : null;

        if (refusal(outcome, taken) is { } sentence) return OutcomeWrite.Refused(409, sentence);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = confirmedBy is null
                ? "UPDATE outcomes SET status = $status, updated_at = $at WHERE id = $id"
                : "UPDATE outcomes SET status = $status, updated_at = $at, confirmed_by = $by, confirmed_at = $at WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$at", Now());
            if (confirmedBy is not null) update.Parameters.AddWithValue("$by", confirmedBy);
            await update.ExecuteNonQueryAsync(ct);
        }

        await audit(connection, transaction, audit_ with { Subject = id, SubjectName = outcome.Name }, ct);
        await transaction.CommitAsync(ct);

        return new OutcomeWrite(await FindAsync(id, ct));
    }

    private static async Task<Outcome> InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, string name, OutcomeEdit fields,
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
                updated_at, confirmed_by, confirmed_at)
            VALUES ($id, $name, $key, $description, $status, NULL, 'local', $metric, $unit, $value,
                $by, $kind, $at, $at, $confirmedBy, $confirmedAt)
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$name", clean);
        insert.Parameters.AddWithValue("$key", OutcomeNames.Key(clean));
        insert.Parameters.AddWithValue("$description", fields.Description?.Trim() ?? "");
        insert.Parameters.AddWithValue("$status", status);
        insert.Parameters.AddWithValue("$metric", (object?)Target(fields.TargetMetric, null) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$unit", (object?)Target(fields.TargetUnit, null) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$value", (object?)Target(fields.TargetValue, null) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$by", actor.Id);
        insert.Parameters.AddWithValue("$kind", actor.Kind);
        insert.Parameters.AddWithValue("$at", now);
        insert.Parameters.AddWithValue("$confirmedBy", confirmed ? actor.Id : DBNull.Value);
        insert.Parameters.AddWithValue("$confirmedAt", confirmed ? now : DBNull.Value);
        await insert.ExecuteNonQueryAsync(ct);

        return (await FindAsync(connection, transaction, id, ct))!;
    }

    private static string? Target(string? edited, string? current) =>
        edited is null ? current : string.IsNullOrWhiteSpace(edited) ? null : edited.Trim();

    private static string? NameRefusal(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "An outcome needs a name: the result it produces.";
        return OutcomeNames.Clean(name).Length > OutcomeNames.MaxLength
            ? $"An outcome's name is at most {OutcomeNames.MaxLength} characters."
            : null;
    }

    private static string Taken(Outcome taken) =>
        $"An {taken.Status} outcome is already named \"{taken.Name}\" ({taken.Id}); names are unique among active and proposed outcomes.";

    private static string PersonsLink(long correlation, OutcomeLink current) =>
        $"Workflow {correlation} is linked to \"{current.OutcomeNameAtLink}\" by a {current.How} "
        + $"({current.SetBy}), which a person caused; only a person moves it. A Manager may link a workflow "
        + "with no outcome, or move one an agent linked.";

    private static OutcomeWrite Missing(string id) => OutcomeWrite.Refused(404, $"No outcome '{id}'.");

    private static async Task<Outcome?> FindAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {OutcomeColumns} FROM outcomes WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var rows = await ReadOutcomesAsync(command, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<Outcome?> LiveByNameAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {OutcomeColumns} FROM outcomes WHERE name_key = $key AND status IN ('proposed', 'active')";
        command.Parameters.AddWithValue("$key", OutcomeNames.Key(name));
        var rows = await ReadOutcomesAsync(command, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection, SqliteTransaction transaction, string sql, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<IReadOnlyList<Outcome>> ReadOutcomesAsync(SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<Outcome>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new Outcome(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Text(reader, 4), reader.GetString(5), Text(reader, 6), Text(reader, 7), Text(reader, 8),
                reader.GetString(9), reader.GetString(10), MessageRows.ReadStamp(reader.GetString(11)),
                MessageRows.ReadStamp(reader.GetString(12)), Text(reader, 13), At(reader, 14)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<OutcomeLink>> ReadLinksAsync(SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<OutcomeLink>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(OutcomeLinks.Read(reader));
        return rows;
    }

    private static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? Long(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? At(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : MessageRows.ReadStamp(reader.GetString(ordinal));
}
