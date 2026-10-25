using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// WHAT AN OUTCOME OR A LINK ROW SAYS, decided once for every provider: the parts of
/// <see cref="OutcomeLinks"/> and <see cref="SqliteOutcomeStore"/> that are not SQL - which rows
/// link at the root, the inherited item's tenant row, the merge chain, and the row readers. Each
/// provider asks its own database and hands the answers here. No SQL lives in this file.
/// </summary>
public static class OutcomeFacts
{
    /// <summary>The <c>workflow_outcome_links</c> columns, in the order <see cref="ReadLink"/> reads them.</summary>
    public const string LinkColumns =
        """
        id, correlation, outcome_id, team_id, team_name_at_link, outcome_name_at_link, set_by,
        set_by_kind, set_at, how
        """;

    /// <summary>The <c>outcomes</c> columns, in the order <see cref="ReadOutcome"/> reads them.</summary>
    public const string OutcomeColumns =
        """
        id, name, description, status, merged_into, source, target_metric, target_unit, target_value,
        created_by, created_by_kind, created_at, updated_at, confirmed_by, confirmed_at, value_amount
        """;

    /// <summary>The <c>tenant_events</c> columns, in the order <see cref="ReadEvent"/> reads them.</summary>
    public const string EventColumns = "seq, occurred_at, actor_id, actor_email, action, subject, subject_name, detail";

    /// <summary>The longest <c>merged_into</c> chain followed, so a hand-edited cycle ends rather than loops.</summary>
    public const int MaxHops = 64;

    /// <summary>The trigger whose fire <paramref name="stored"/> is - an instruction from
    /// <c>schedule:&lt;id&gt;</c> or <c>trigger:&lt;id&gt;</c> - or null.</summary>
    public static string? TriggerIdOf(Message stored)
    {
        if (!stored.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)) return null;

        var triggerId = stored.Source.StartsWith("schedule:", StringComparison.Ordinal)
            ? stored.Source["schedule:".Length..]
            : stored.Source.StartsWith("trigger:", StringComparison.Ordinal)
                ? stored.Source["trigger:".Length..]
                : null;

        return string.IsNullOrEmpty(triggerId) ? null : triggerId;
    }

    /// <summary>A dispatch row's item and team, or null when its payload does not name an item.</summary>
    public static (long Item, string? Team)? DispatchOf(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("item", out var itemElement) || !itemElement.TryGetInt64(out var item)) return null;
            var team = root.TryGetProperty("team", out var teamElement) ? teamElement.GetString() : null;
            return (item, team);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>backlog.item-outcome-inherited</c> tenant row. A person's row carries their
    /// email; an agent's or the platform's, its id.</summary>
    public static TriggerAudit InheritedAudit(
        string item, string title, string outcomeId, string? outcomeName, long workflow, string by, string byKind,
        long? linkId, string? how)
    {
        var person = byKind == OutcomeActorKind.Person;
        return new TriggerAudit(
            person ? null : by, person ? by : null, TenantActions.BacklogItemOutcomeInherited, item, title,
            JsonSerializer.Serialize(new
            {
                outcomeId,
                outcomeName,
                workflow,
                link = linkId,
                how,
                by,
                byKind,
            }));
    }

    /// <summary>
    /// <paramref name="outcomeId"/> followed through <c>merged_into</c> to the outcome that holds its
    /// figures now: its id and status, or null when there is no such outcome or the chain is longer
    /// than <see cref="MaxHops"/>. <paramref name="at"/> reads one outcome's status and
    /// <c>merged_into</c>, or null when it does not exist.
    /// </summary>
    public static async Task<(string Id, string Status)?> ResolveAsync(
        string outcomeId, Func<string, Task<(string Status, string? MergedInto)?>> at)
    {
        var id = outcomeId;

        for (var hop = 0; hop < MaxHops; hop++)
        {
            if (await at(id) is not { } row) return null;
            if (row.Status != OutcomeStatus.Merged || row.MergedInto is null) return (id, row.Status);

            id = row.MergedInto;
        }

        return null;
    }

    public static OutcomeLink ReadLink(DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            Text(reader, 2),
            Text(reader, 3),
            Text(reader, 4),
            Text(reader, 5),
            reader.GetString(6),
            reader.GetString(7),
            MessageRows.ReadStamp(reader.GetString(8)),
            reader.GetString(9));

    public static Outcome ReadOutcome(DbDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            Text(reader, 4), reader.GetString(5), Text(reader, 6), Text(reader, 7), Text(reader, 8),
            reader.GetString(9), reader.GetString(10), MessageRows.ReadStamp(reader.GetString(11)),
            MessageRows.ReadStamp(reader.GetString(12)), Text(reader, 13), At(reader, 14), Text(reader, 15));

    public static TenantEvent ReadEvent(DbDataReader reader) =>
        new(
            reader.GetInt64(0), MessageRows.ReadStamp(reader.GetString(1)), Text(reader, 2), Text(reader, 3),
            reader.GetString(4), Text(reader, 5), Text(reader, 6), Text(reader, 7));

    /// <summary>One <see cref="OutcomeBacklogItem"/>: id, title, state, archived (an integer), outcome,
    /// then the newest dispatch's team id, team name, correlation, dispatched and landed stamps.</summary>
    public static OutcomeBacklogItem ReadBacklogItem(DbDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), Convert.ToInt64(reader.GetValue(3)) != 0,
            Text(reader, 4), Text(reader, 5), Text(reader, 6), Long(reader, 7), At(reader, 8), At(reader, 9));

    public static async Task<IReadOnlyList<OutcomeLink>> ReadLinksAsync(DbCommand command, CancellationToken ct)
    {
        var rows = new List<OutcomeLink>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadLink(reader));
        return rows;
    }

    public static async Task<IReadOnlyList<Outcome>> ReadOutcomesAsync(DbCommand command, CancellationToken ct)
    {
        var rows = new List<Outcome>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadOutcome(reader));
        return rows;
    }

    private static string? Text(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? Long(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? At(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : MessageRows.ReadStamp(reader.GetString(ordinal));
}

/// <summary>
/// THE OUTCOME STORE'S RULES AND SENTENCES, the same on every provider: what a write is refused for
/// and how the refusal reads, and what an edit leaves an outcome as. A store decides with these
/// inside its own transaction.
/// </summary>
public static class OutcomeRules
{
    public static string? NameRefusal(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "An outcome needs a name: the result it produces.";
        return OutcomeNames.Clean(name).Length > OutcomeNames.MaxLength
            ? $"An outcome's name is at most {OutcomeNames.MaxLength} characters."
            : null;
    }

    /// <summary>A value given that is not a value, or null.</summary>
    public static string? ValueRefusal(string? value) =>
        !string.IsNullOrWhiteSpace(value) && OutcomeValue.Canonical(value) is null ? OutcomeValue.Refusal : null;

    public static string Taken(Outcome taken) =>
        $"An {taken.Status} outcome is already named \"{taken.Name}\" ({taken.Id}); names are unique among active and proposed outcomes.";

    public static string PersonsLink(long correlation, OutcomeLink current) =>
        current.IsUnlink
            ? $"Workflow {correlation} was set to no outcome by a person ({current.SetBy}); only a person "
              + "links it again. A Manager may link a workflow no one has linked, or move one an agent linked."
            : $"Workflow {correlation} is linked to \"{current.OutcomeNameAtLink}\" by a {current.How} "
              + $"({current.SetBy}), which a person caused; only a person moves it. A Manager may link a workflow "
              + "with no outcome, or move one an agent linked.";

    public static OutcomeWrite Missing(string id) => OutcomeWrite.Refused(404, $"No outcome '{id}'.");

    public static string NotLive(Outcome outcome) =>
        $"The outcome \"{outcome.Name}\" is {outcome.Status}; a workflow is linked only to an active or proposed outcome.";

    public static string NothingToUnlink(long correlation) =>
        $"Workflow {correlation} serves no outcome; there is nothing to unlink.";

    public static string MergedEdit(Outcome outcome) =>
        $"\"{outcome.Name}\" was merged; edit the outcome it was merged into.";

    /// <summary>A target field after an edit: unchanged when not given, cleared when blank.</summary>
    public static string? Target(string? edited, string? current) =>
        edited is null ? current : string.IsNullOrWhiteSpace(edited) ? null : edited.Trim();

    /// <summary>The value an insert stores: the canonical amount, or null.</summary>
    public static string? NewValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : OutcomeValue.Canonical(value);

    /// <summary>What <paramref name="outcome"/> is after <paramref name="edit"/>, whose name is
    /// already checked.</summary>
    public static Outcome Edited(Outcome outcome, OutcomeEdit edit) =>
        outcome with
        {
            Name = edit.Name is null ? outcome.Name : OutcomeNames.Clean(edit.Name),
            Description = edit.Description?.Trim() ?? outcome.Description,
            TargetMetric = Target(edit.TargetMetric, outcome.TargetMetric),
            TargetUnit = Target(edit.TargetUnit, outcome.TargetUnit),
            TargetValue = Target(edit.TargetValue, outcome.TargetValue),
            Value = edit.Value is null ? outcome.Value : string.IsNullOrWhiteSpace(edit.Value) ? null : OutcomeValue.Canonical(edit.Value),
        };

    public static string? ConfirmRefusal(Outcome outcome, Outcome? taken) =>
        outcome.Status == OutcomeStatus.Proposed
            ? null
            : $"\"{outcome.Name}\" is {outcome.Status}; only a proposed outcome is confirmed.";

    public static string? RetireRefusal(Outcome outcome, Outcome? taken) =>
        outcome.IsLive
            ? null
            : $"\"{outcome.Name}\" is {outcome.Status}; only an active or proposed outcome is retired.";

    public static string? ReactivateRefusal(Outcome outcome, Outcome? taken) =>
        outcome.Status != OutcomeStatus.Retired
            ? $"\"{outcome.Name}\" is {outcome.Status}; only a retired outcome is reactivated."
            : taken is not null ? Taken(taken) : null;

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

    public static string NotProposed(Outcome outcome) =>
        $"\"{outcome.Name}\" is {outcome.Status}; only a proposed outcome is rejected. Retire it instead.";

    public static string StillNamed(Outcome outcome, long links, long mergedIn) =>
        $"\"{outcome.Name}\" is linked to {links} workflow{(links == 1 ? "" : "s")}"
        + (mergedIn > 0 ? $" and has {mergedIn} outcome{(mergedIn == 1 ? "" : "s")} merged into it" : "")
        + ", so it is not rejected: a link is never rewritten. Merge it into another outcome, or retire it.";

    /// <summary>The unlink's tenant row detail: <c>from</c> the outcome the workflow served, <c>to: null</c>.</summary>
    public static string UnlinkDetail(string? team, long correlation, string? from) =>
        JsonSerializer.Serialize(new { team, workflow = correlation, from, to = (string?)null });
}
