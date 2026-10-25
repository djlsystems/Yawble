using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Ledger;

/// <summary>
/// THE ONE WRITER OF <c>workflow_outcome_links</c> ON SQL SERVER, inside the caller's transaction:
/// SQLite's <c>OutcomeLinks</c> statement for statement, whose remarks are the argument (a root links
/// from the append, a root link never replaces one, an item inherits its dispatch workflow's outcome).
/// What a row means is <see cref="OutcomeFacts"/>'s, shared with it.
///
/// <para>
/// THE NEWEST LINK IS READ UNDER <c>UPDLOCK, HOLDLOCK</c> whenever a write follows from it
/// (<see cref="CurrentAsync"/> with <c>forUpdate</c>): it holds the range of that correlation's rows,
/// so a second writer deciding from the same link waits for the first to commit and then sees its
/// row, as SQLite's one writer would. The root links in <see cref="OnAppendAsync"/> hold them too: the
/// log's append lock serialises appends, but the outcome store's writes never take it.
/// </para>
/// </summary>
public static class SqlServerOutcomeLinks
{
    private const string LinkColumns = OutcomeFacts.LinkColumns;

    /// <summary>The root links <paramref name="stored"/> makes, when it is a dispatch or a trigger's
    /// fire whose item or trigger names an outcome. Nothing otherwise.</summary>
    public static async Task OnAppendAsync(
        SqlConnection connection, SqlTransaction transaction, Message stored, CancellationToken ct)
    {
        if (stored.Type == MessageTypes.BacklogItemDispatched)
        {
            await DispatchAsync(connection, transaction, stored, ct);
            return;
        }

        if (OutcomeFacts.TriggerIdOf(stored) is not { } triggerId) return;

        string outcome, configuredBy, team;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT t.outcome_id, COALESCE(t.configured_by_email, u.email, t.created_by), t.team
                FROM triggers t LEFT JOIN users u ON u.id = t.created_by
                WHERE t.id = @id
                """;
            command.Parameters.AddWithValue("@id", triggerId);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(0)) return;
            outcome = reader.GetString(0);
            configuredBy = reader.GetString(1);
            team = reader.GetString(2);
        }

        // A ROOT LINK NEVER REPLACES ONE, and the fire may join a workflow a person is linking right
        // now: the outcome is held first and then the workflow's newest link, the order the store's own
        // link takes them, so the two writers decide one after the other and the person's link stays.
        if (await ResolveLiveAsync(connection, transaction, outcome, ct) is not { } resolved) return;
        if (await CurrentAsync(connection, transaction, stored.CorrelationId, ct, forUpdate: true) is not null) return;

        await WriteAsync(
            connection, transaction, stored.CorrelationId, resolved, team, configuredBy,
            OutcomeActorKind.Person, OutcomeLinkHow.Trigger, ct);
    }

    private static async Task DispatchAsync(
        SqlConnection connection, SqlTransaction transaction, Message dispatched, CancellationToken ct)
    {
        if (OutcomeFacts.DispatchOf(dispatched.Payload) is not { } dispatch) return;
        var (item, team) = dispatch;

        string? outcome;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT outcome_id FROM backlog_items WHERE id = @id";
            command.Parameters.AddWithValue("@id", PlatformBacklogId.Format(item));
            outcome = await command.ExecuteScalarAsync(ct) as string;
        }

        if (outcome is null) return;
        if (await ResolveLiveAsync(connection, transaction, outcome, ct) is not { } resolved) return;

        await WriteAsync(
            connection, transaction, dispatched.CorrelationId, resolved, team, dispatched.Source,
            OutcomeActorKind.Person, OutcomeLinkHow.Dispatch, ct);
    }

    /// <summary>
    /// <paramref name="outcomeId"/> followed through <c>merged_into</c>, when that outcome is live; null
    /// for a retired one or one that is gone. Held until the append commits, so a retire or merge
    /// running beside it waits, and the link is never made to an outcome already taken out of use.
    /// </summary>
    private static async Task<string?> ResolveLiveAsync(
        SqlConnection connection, SqlTransaction transaction, string outcomeId, CancellationToken ct) =>
        await ResolveAsync(connection, transaction, outcomeId, ct, forUpdate: true) is { } resolved
        && OutcomeStatus.IsLive(resolved.Status)
            ? resolved.Id
            : null;

    /// <summary>
    /// Appends one link row: the outcome's name and the team's name as they are now, which nothing
    /// ever rewrites. A null <paramref name="outcomeId"/> is an unlink. Read back by its identity
    /// rather than with OUTPUT, which SQL Server refuses on a table with triggers.
    /// </summary>
    public static async Task<OutcomeLink> WriteAsync(
        SqlConnection connection, SqlTransaction transaction, long correlation, string? outcomeId,
        string? team, string setBy, string setByKind, string how, CancellationToken ct)
    {
        OutcomeLink link;

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                $"""
                 INSERT INTO workflow_outcome_links (
                     correlation, outcome_id, team_id, team_name_at_link, outcome_name_at_link,
                     set_by, set_by_kind, set_at, how)
                 VALUES (
                     @correlation, @outcome, @team,
                     CASE WHEN @team IS NULL THEN NULL
                          ELSE COALESCE((SELECT t.name FROM teams t WHERE t.id = @team), @team) END,
                     (SELECT o.name FROM outcomes o WHERE o.id = @outcome),
                     @setBy, @kind, @at, @how);

                 SELECT {LinkColumns} FROM workflow_outcome_links WHERE id = CAST(SCOPE_IDENTITY() AS BIGINT);
                 """;
            insert.Parameters.AddWithValue("@correlation", correlation);
            insert.Parameters.AddWithValue("@outcome", (object?)outcomeId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@team", (object?)team ?? DBNull.Value);
            insert.Parameters.AddWithValue("@setBy", setBy);
            insert.Parameters.AddWithValue("@kind", setByKind);
            insert.Parameters.AddWithValue("@at", Instant.Now());
            insert.Parameters.AddWithValue("@how", how);

            await using var reader = await insert.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            link = OutcomeFacts.ReadLink(reader);
        }

        if (outcomeId is not null) await InheritAsync(connection, transaction, link, ct);

        return link;
    }

    /// <summary>A backlog item takes the outcome its dispatch workflow is linked to, only when it has
    /// none, with its tenant row - see SQLite's <c>OutcomeLinks.InheritAsync</c>.</summary>
    private static async Task InheritAsync(
        SqlConnection connection, SqlTransaction transaction, OutcomeLink link, CancellationToken ct)
    {
        var items = new List<(string Id, string Title)>();

        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText =
                """
                SELECT i.id, i.title FROM backlog_items i WITH (UPDLOCK, HOLDLOCK)
                WHERE i.outcome_id IS NULL
                  AND EXISTS (SELECT 1 FROM backlog_dispatches d WHERE d.item = i.id AND d.correlation = @correlation)
                """;
            find.Parameters.AddWithValue("@correlation", link.Correlation);

            await using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (id, title) in items)
        {
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE backlog_items SET outcome_id = @outcome WHERE id = @id AND outcome_id IS NULL";
                update.Parameters.AddWithValue("@outcome", link.OutcomeId!);
                update.Parameters.AddWithValue("@id", id);
                await update.ExecuteNonQueryAsync(ct);
            }

            await AppendInheritedRowAsync(
                connection, transaction, id, title, link.OutcomeId!, link.OutcomeNameAtLink, link.Correlation,
                link.SetBy, link.SetByKind, link.Id, link.How, ct);
        }
    }

    /// <summary>The <c>backlog.item-outcome-inherited</c> tenant row, in the caller's transaction.</summary>
    public static Task AppendInheritedRowAsync(
        SqlConnection connection, SqlTransaction transaction, string item, string title, string outcomeId,
        string? outcomeName, long workflow, string by, string byKind, long? linkId, string? how, CancellationToken ct) =>
        SqlServerTenantAuditRow.AppendAsync(
            connection, transaction,
            OutcomeFacts.InheritedAudit(item, title, outcomeId, outcomeName, workflow, by, byKind, linkId, how),
            ct);

    /// <summary>
    /// The newest link of <paramref name="correlation"/>, or null. <paramref name="forUpdate"/> holds
    /// that correlation's rows until the transaction ends, for a caller that writes from the answer.
    /// </summary>
    public static async Task<OutcomeLink?> CurrentAsync(
        SqlConnection connection, SqlTransaction? transaction, long correlation, CancellationToken ct, bool forUpdate = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT TOP 1 {LinkColumns} FROM workflow_outcome_links {(forUpdate ? "WITH (UPDLOCK, HOLDLOCK)" : "")}
             WHERE correlation = @correlation
             ORDER BY id DESC
             """;
        command.Parameters.AddWithValue("@correlation", correlation);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? OutcomeFacts.ReadLink(reader) : null;
    }

    /// <summary>
    /// <paramref name="outcomeId"/> followed through <c>merged_into</c> (<see cref="OutcomeFacts.ResolveAsync"/>).
    /// <paramref name="forUpdate"/> holds each outcome read, so a link a caller writes from the answer
    /// cannot land on an outcome a concurrent retire or merge has just taken out of use.
    /// </summary>
    public static Task<(string Id, string Status)?> ResolveAsync(
        SqlConnection connection, SqlTransaction? transaction, string outcomeId, CancellationToken ct, bool forUpdate = false) =>
        OutcomeFacts.ResolveAsync(outcomeId, async id =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT status, merged_into FROM outcomes {(forUpdate ? "WITH (UPDLOCK, HOLDLOCK)" : "")} WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        });
}
