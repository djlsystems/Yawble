using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>
/// THE ONE WRITER OF <c>workflow_outcome_links</c> (see <see cref="OutcomeSchema"/>), inside the
/// caller's transaction.
///
/// <para>
/// LINKING AT THE ROOT, FROM THE APPEND. A backlog dispatch's <c>backlog.item.dispatched</c> row and
/// a trigger's or schedule's fire (an instruction whose source is <c>schedule:&lt;id&gt;</c> or
/// <c>trigger:&lt;id&gt;</c>) link their workflow here, called from <c>SqliteMessageStore</c>'s one
/// insert - so the link lands in the same transaction as the row that roots the workflow, whichever
/// path appended it (the sweep, Run now, a run at install, an event), and none can skip it. A
/// <c>tell</c> naming an outcome links from its route, through <see cref="WriteAsync"/>, inside the
/// same append's transaction.
/// </para>
///
/// <para>
/// A ROOT LINK NEVER REPLACES ONE. A fire joining a workflow that already has an outcome (an event
/// trigger fires inside the event's workflow) leaves it as it is.
/// </para>
/// </summary>
public static class OutcomeLinks
{
    private const string LinkColumns =
        """
        id, correlation, outcome_id, team_id, team_name_at_link, outcome_name_at_link, set_by,
        set_by_kind, set_at, how
        """;

    /// <summary>The root links <paramref name="stored"/> makes, when it is a dispatch or a trigger's
    /// fire whose item or trigger names an outcome. Nothing otherwise.</summary>
    public static async Task OnAppendAsync(
        SqliteConnection connection, SqliteTransaction transaction, Message stored, CancellationToken ct)
    {
        if (stored.Type == MessageTypes.BacklogItemDispatched)
        {
            await DispatchAsync(connection, transaction, stored, ct);
            return;
        }

        if (!stored.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)) return;

        var triggerId = stored.Source.StartsWith("schedule:", StringComparison.Ordinal)
            ? stored.Source["schedule:".Length..]
            : stored.Source.StartsWith("trigger:", StringComparison.Ordinal)
                ? stored.Source["trigger:".Length..]
                : null;

        if (string.IsNullOrEmpty(triggerId)) return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // SET_BY IS THE PERSON'S EMAIL, as on every other link and tenant row a person causes:
        // the configurer's email the trigger snapshotted (`auth-018`), which outlives their user.
        // A trigger from before that step has none, so its `created_by` (a user id) is resolved
        // here, and kept as it is only when no user of that id is left.
        command.CommandText = """
            SELECT t.outcome_id, COALESCE(t.configured_by_email, u.email, t.created_by), t.team
            FROM triggers t LEFT JOIN users u ON u.id = t.created_by
            WHERE t.id = $id
            """;
        command.Parameters.AddWithValue("$id", triggerId);

        string? outcome, configuredBy, team;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(0)) return;
            outcome = reader.GetString(0);
            configuredBy = reader.GetString(1);
            team = reader.GetString(2);
        }

        if (await CurrentAsync(connection, transaction, stored.CorrelationId, ct) is not null) return;

        await WriteLiveAsync(
            connection, transaction, stored.CorrelationId, outcome, team, configuredBy,
            OutcomeActorKind.Person, OutcomeLinkHow.Trigger, ct);
    }

    private static async Task DispatchAsync(
        SqliteConnection connection, SqliteTransaction transaction, Message dispatched, CancellationToken ct)
    {
        long item;
        string? team;

        try
        {
            using var document = JsonDocument.Parse(dispatched.Payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("item", out var itemElement) || !itemElement.TryGetInt64(out item)) return;
            team = root.TryGetProperty("team", out var teamElement) ? teamElement.GetString() : null;
        }
        catch (JsonException)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT outcome_id FROM backlog_items WHERE id = $id";
        command.Parameters.AddWithValue("$id", PlatformBacklogId.Format(item));

        if (await command.ExecuteScalarAsync(ct) is not string outcome) return;

        await WriteLiveAsync(
            connection, transaction, dispatched.CorrelationId, outcome, team, dispatched.Source,
            OutcomeActorKind.Person, OutcomeLinkHow.Dispatch, ct);
    }

    /// <summary>Links to <paramref name="outcomeId"/> followed through <c>merged_into</c>, when that
    /// outcome is live; a retired one, or one that is gone, links nothing.</summary>
    private static async Task WriteLiveAsync(
        SqliteConnection connection, SqliteTransaction transaction, long correlation, string outcomeId,
        string? team, string setBy, string setByKind, string how, CancellationToken ct)
    {
        if (await ResolveAsync(connection, transaction, outcomeId, ct) is not { } resolved) return;
        if (!OutcomeStatus.IsLive(resolved.Status)) return;

        await WriteAsync(connection, transaction, correlation, resolved.Id, team, setBy, setByKind, how, ct);
    }

    /// <summary>
    /// Appends one link row: the outcome's name and the team's name as they are now, which nothing
    /// ever rewrites. A null <paramref name="outcomeId"/> is an unlink: the row names no outcome.
    /// </summary>
    public static async Task<OutcomeLink> WriteAsync(
        SqliteConnection connection, SqliteTransaction transaction, long correlation, string? outcomeId,
        string? team, string setBy, string setByKind, string how, CancellationToken ct)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
             INSERT INTO workflow_outcome_links (
                 correlation, outcome_id, team_id, team_name_at_link, outcome_name_at_link,
                 set_by, set_by_kind, set_at, how)
             VALUES (
                 $correlation, $outcome, $team,
                 CASE WHEN $team IS NULL THEN NULL
                      ELSE COALESCE((SELECT t.name FROM teams t WHERE t.id = $team), $team) END,
                 (SELECT o.name FROM outcomes o WHERE o.id = $outcome),
                 $setBy, $kind, $at, $how)
             RETURNING {LinkColumns}
             """;
        insert.Parameters.AddWithValue("$correlation", correlation);
        insert.Parameters.AddWithValue("$outcome", (object?)outcomeId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$team", (object?)team ?? DBNull.Value);
        insert.Parameters.AddWithValue("$setBy", setBy);
        insert.Parameters.AddWithValue("$kind", setByKind);
        insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$how", how);

        await using var reader = await insert.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    /// <summary>The newest link of <paramref name="correlation"/>, or null.</summary>
    public static async Task<OutcomeLink?> CurrentAsync(
        SqliteConnection connection, SqliteTransaction? transaction, long correlation, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT {LinkColumns} FROM workflow_outcome_links
             WHERE correlation = $correlation
             ORDER BY id DESC
             LIMIT 1
             """;
        command.Parameters.AddWithValue("$correlation", correlation);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>
    /// <paramref name="outcomeId"/> followed through <c>merged_into</c> to the outcome that holds its
    /// figures now: its id and status, or null when there is no such outcome. A chain is bounded, so
    /// a hand-edited cycle ends rather than loops.
    /// </summary>
    public static async Task<(string Id, string Status)?> ResolveAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string outcomeId, CancellationToken ct)
    {
        var id = outcomeId;

        for (var hop = 0; hop < 64; hop++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT status, merged_into FROM outcomes WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            var status = reader.GetString(0);
            if (status != OutcomeStatus.Merged || reader.IsDBNull(1)) return (id, status);

            id = reader.GetString(1);
        }

        return null;
    }

    public static OutcomeLink Read(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            MessageRows.ReadStamp(reader.GetString(8)),
            reader.GetString(9));

    public const string Columns = LinkColumns;
}
