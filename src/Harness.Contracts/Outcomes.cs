using System.Globalization;
using System.Text;

namespace Harness.Contracts;

/// <summary>
/// AN OUTCOME: the business result a workflow serves - "Maintain a current pipeline of qualified job
/// openings", never "Run the job crawler every 15 minutes". Instance-wide; teams work toward one, and
/// every link records its team. Definitions only: the target fields are text a person edits, and
/// nothing records a measured value against them.
/// </summary>
/// <param name="Id">A GUID, written as <c>Guid.ToString("D")</c>: its hyphens keep it text in a
/// column of any affinity (<c>workflow_ledger.outcome_id_at_close</c> is INTEGER), where a string of
/// only digits would be read back as a number.</param>
/// <param name="Value">The outcome's budget - what it may spend - shown as Budget: a non-negative
/// decimal in the instance's currency (<c>outcomes.currency</c>), as text; null when no one has set
/// one. A person's, edited like the name. It replaces the target fields, which are kept and no longer
/// shown.</param>
/// <param name="MergedInto">Set on a <c>merged</c> outcome only. Reads follow it to the outcome the
/// figures moved to; nothing that links to this one is rewritten.</param>
public sealed record Outcome(
    string Id,
    string Name,
    string Description,
    string Status,
    string? MergedInto,
    string Source,
    string? TargetMetric,
    string? TargetUnit,
    string? TargetValue,
    string CreatedBy,
    string CreatedByKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ConfirmedBy,
    DateTimeOffset? ConfirmedAt,
    string? Value = null,
    string? Presentation = null)
{
    public bool IsLive => OutcomeStatus.IsLive(Status);
}

/// <summary>
/// HOW A PERSON READS AN OUTCOME (<c>outcomes.presentation</c>, <c>outcome-011</c>): a <c>project</c> is
/// read as a whole - cumulative Agent Hours and Estimated Execution Cost, backlog progress, a cumulative
/// chart - and an <c>operational</c> one week by week - cost, throughput (closed workflows) and cost per
/// completed workflow. Stored NULL until a person picks one, and NULL reads as project. One model, two
/// readings: nothing about the figures changes, only how the Outcomes dialog lays them out. A person sets
/// it through the outcome edit route; nothing else writes it.
/// </summary>
public static class OutcomePresentation
{
    public const string Project = "project";
    public const string Operational = "operational";

    /// <summary>The closed set, as the column's CHECK constraint holds it on both providers.</summary>
    public static readonly IReadOnlyList<string> All = [Project, Operational];

    public const string Refusal = "An outcome's presentation is project or operational.";

    /// <summary>Whether <paramref name="value"/> is one of <see cref="All"/>, exactly.</summary>
    public static bool IsKnown(string? value) => value is Project or Operational;

    /// <summary>How a stored value reads: NULL (never picked) is project.</summary>
    public static string Read(string? stored) => stored ?? Project;
}

/// <summary>
/// A workflow's outcome NOW, as a card carries it: its newest link's outcome, followed through
/// <c>merged_into</c>. The name is the outcome's current name, not the link's snapshot, and it is
/// text: a client renders it as text, never HTML.
/// </summary>
public sealed record WorkflowOutcome(string Id, string Name, string Status);

/// <summary>An outcome's four states, as data.</summary>
public static class OutcomeStatus
{
    /// <summary>A Manager's proposal: usable at once, and only a person confirms it.</summary>
    public const string Proposed = "proposed";
    public const string Active = "active";
    public const string Retired = "retired";
    public const string Merged = "merged";

    public static IReadOnlyList<string> All { get; } = [Proposed, Active, Retired, Merged];

    /// <summary>Proposed or active: the outcomes a link may name and a name is unique among.</summary>
    public static bool IsLive(string status) => status is Proposed or Active;
}

/// <summary>
/// One row of <c>workflow_outcome_links</c>. Append-only: a workflow's current outcome is its NEWEST
/// row, and moving a workflow is a new row. <see cref="OutcomeNameAtLink"/> and
/// <see cref="TeamNameAtLink"/> are snapshots a rename or a merge never rewrites.
/// </summary>
/// <param name="OutcomeId">Null on an UNLINK (<c>outcome-004</c>): a person chose no outcome, and the
/// workflow counts under No outcome while this is its newest row.</param>
public sealed record OutcomeLink(
    long Id,
    long Correlation,
    string? OutcomeId,
    string? TeamId,
    string? TeamNameAtLink,
    string? OutcomeNameAtLink,
    string SetBy,
    string SetByKind,
    DateTimeOffset SetAt,
    string How)
{
    /// <summary>A person's "None": this row names no outcome.</summary>
    public bool IsUnlink => OutcomeId is null;
}

/// <summary>How a link was made. Three are a person's cause; two are an agent's.</summary>
public static class OutcomeLinkHow
{
    /// <summary>A backlog dispatch: the item's outcome, attributed to the person who dispatched.</summary>
    public const string Dispatch = "dispatch";

    /// <summary>A trigger's or schedule's fire, attributed to the person who configured it.</summary>
    public const string Trigger = "trigger";

    /// <summary>A <c>tell</c> without causation that named an outcome.</summary>
    public const string Tell = "tell";

    /// <summary>An agent's <c>outcome</c> set or propose (a Manager's, or the Concierge's).</summary>
    public const string Manager = "manager";

    /// <summary>A person's own choice through the workflow's outcome route.</summary>
    public const string Person = "person";

    /// <summary>
    /// A MANAGER NEVER OVERRIDES A LINK A PERSON CAUSED. An agent may link a workflow with no link,
    /// or move one an agent made: a <c>manager</c> link, or a <c>tell</c> the Concierge sent. A link
    /// from a dispatch, a trigger, a person's own <c>tell</c> or a person's own choice is the
    /// person's, and only a person moves it.
    /// </summary>
    public static bool AgentMayReplace(OutcomeLink? current) =>
        current is null
        || current.How == Manager
        || (current.How == Tell && current.SetByKind != OutcomeActorKind.Person);
}

/// <summary>Who made a link or an outcome.</summary>
public static class OutcomeActorKind
{
    public const string Person = "person";
    public const string Member = "member";
    public const string Platform = "platform";
}

/// <summary>The name rule: unique among live outcomes, case- and whitespace-insensitive.</summary>
public static class OutcomeNames
{
    public const int MaxLength = 200;

    /// <summary>The name as the uniqueness rule compares it: trimmed, every run of whitespace one
    /// space, lower-cased invariantly.</summary>
    public static string Key(string name)
    {
        var builder = new StringBuilder(name.Length);
        var space = false;

        foreach (var c in name.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space) builder.Append(' ');
            space = false;
            builder.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>The name as stored: trimmed, inner whitespace runs kept as one space.</summary>
    public static string Clean(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>What a person or an agent is when it writes an outcome or a link.</summary>
/// <param name="Id">The person's email, or the member's id (<c>team/Manager</c>).</param>
public sealed record OutcomeActor(string Id, string Kind, string? ActorId = null, string? ActorEmail = null);

/// <summary>A write's answer: the outcome (and link) it made, or a refusal sentence and the status
/// the route answers with it.</summary>
public sealed record OutcomeWrite(
    Outcome? Outcome, OutcomeLink? Link = null, bool Created = false, string? Refusal = null, int Status = 200)
{
    public bool Ok => Refusal is null;

    public static OutcomeWrite Refused(int status, string sentence) => new(null, Refusal: sentence, Status: status);
}

/// <summary>The fields a person edits on an outcome. Null leaves a field as it is; an empty target
/// or value clears it.</summary>
public sealed record OutcomeEdit(
    string? Name = null, string? Description = null,
    string? TargetMetric = null, string? TargetUnit = null, string? TargetValue = null,
    string? Value = null, string? Presentation = null);

/// <summary>An outcome's value as the store keeps it: a non-negative decimal, invariant, no
/// exponent. The one check, so the route and the store say the same.</summary>
public static class OutcomeValue
{
    public const string Refusal =
        "An outcome's budget is an amount of the instance's currency, 0 or more, such as 20000 or 1250.50.";

    /// <summary>The canonical text of <paramref name="text"/>, or null when it is not a value.</summary>
    public static string? Canonical(string text)
    {
        var trimmed = text.Trim().Replace(",", "", StringComparison.Ordinal);
        if (!decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return null;
        if (amount < 0 || amount > 1_000_000_000_000m) return null;
        return decimal.Round(amount, 2).ToString("0.##", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// THE OUTCOMES AND THEIR LINKS (<c>outcome-002</c>), in the log's own database file so a link is
/// written in the same transaction as the root row it links. Every person's write appends its
/// <c>tenant_events</c> row in the same transaction and does not happen when that row cannot be
/// written; an agent's propose and set append one naming the member.
/// </summary>
public interface IOutcomeStore
{
    Task<IReadOnlyList<Outcome>> ListAsync(CancellationToken ct = default);

    Task<Outcome?> FindAsync(string id, CancellationToken ct = default);

    /// <summary>The live outcome whose name matches under <see cref="OutcomeNames.Key"/>, or null.</summary>
    Task<Outcome?> FindLiveByNameAsync(string name, CancellationToken ct = default);

    /// <summary>An id, or the exact name of a live outcome; null when neither.</summary>
    Task<Outcome?> ResolveLiveAsync(string idOrName, CancellationToken ct = default);

    /// <summary>A person's create: <c>active</c> and confirmed at once.</summary>
    Task<OutcomeWrite> CreateAsync(
        string name, OutcomeEdit fields, OutcomeActor person, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// An agent's proposal: a <c>proposed</c> outcome, linked to <paramref name="correlation"/> when
    /// one is given. A live outcome of that name is linked instead and nothing is created. The link
    /// is refused, and nothing written, when the workflow's link is a person's.
    /// </summary>
    Task<OutcomeWrite> ProposeAsync(
        string name, string? description, OutcomeActor actor, long? correlation, string? team,
        TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// Links <paramref name="correlation"/> to <paramref name="outcomeId"/> as a new row. With
    /// <paramref name="agentRule"/> the write is refused when the current link is a person's
    /// (<see cref="OutcomeLinkHow.AgentMayReplace"/>).
    /// </summary>
    Task<OutcomeWrite> LinkAsync(
        long correlation, string outcomeId, string? team, OutcomeActor actor, string how, bool agentRule,
        TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// A person's "None": appends a link row naming no outcome (an unlink, history kept), with its
    /// <c>workflow.outcome-changed</c> row (<c>from</c> the outcome it served, <c>to: null</c>) in the
    /// same transaction. Refused (409), and nothing written, when the workflow serves no outcome.
    /// </summary>
    Task<OutcomeWrite> UnlinkAsync(
        long correlation, string? team, OutcomeActor person, TriggerAudit audit, CancellationToken ct = default);

    Task<OutcomeWrite> EditAsync(string id, OutcomeEdit edit, TriggerAudit renamed, TriggerAudit changed, CancellationToken ct = default);

    Task<OutcomeWrite> ConfirmAsync(string id, OutcomeActor person, TriggerAudit audit, CancellationToken ct = default);

    Task<OutcomeWrite> RetireAsync(string id, TriggerAudit audit, CancellationToken ct = default);

    Task<OutcomeWrite> ReactivateAsync(string id, TriggerAudit audit, CancellationToken ct = default);

    Task<OutcomeWrite> MergeAsync(string id, string into, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>Rejects a proposed outcome that no link names: the row is removed.</summary>
    Task<OutcomeWrite> RejectAsync(string id, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// A PERSON'S DELETE OF ENDED OUTCOMES (retired or merged): each one's row goes, with every outcome
    /// merged into it; every workflow whose newest link, followed through <c>merged_into</c>, reaches
    /// one gets an unlink row (as <see cref="UnlinkAsync"/> writes it, <c>how: person</c>); each backlog
    /// item and trigger naming one has its outcome cleared. One <paramref name="deleted"/> row per
    /// outcome and one <paramref name="unlinked"/> row per workflow, all in ONE transaction: when a row
    /// cannot be written nothing changes. A live or unknown id refuses the whole request. With
    /// <paramref name="preview"/> the same answer is worked out and nothing is written.
    /// </summary>
    Task<OutcomeDeletion> DeleteAsync(
        IReadOnlyCollection<string> ids, OutcomeActor person, bool preview, TriggerAudit deleted, TriggerAudit unlinked,
        CancellationToken ct = default);

    /// <summary>
    /// Removes an outcome a failed solution install created, when nothing links to it: the install's
    /// undo, with the install's own tenant row in the same transaction, and not a person's reject.
    /// False, and nothing written, when a link names it or it is gone.
    /// </summary>
    Task<bool> UndoCreateAsync(string id, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>The newest link of <paramref name="correlation"/>, or null. An unlink is a link
    /// (<see cref="OutcomeLink.IsUnlink"/>): the workflow serves no outcome.</summary>
    Task<OutcomeLink?> CurrentLinkAsync(long correlation, CancellationToken ct = default);

    /// <summary>
    /// Each of <paramref name="correlations"/> that has a link, with its outcome now (newest link,
    /// followed through <c>merged_into</c>), in ONE query. What the board attaches to its cards.
    /// </summary>
    Task<IReadOnlyDictionary<long, WorkflowOutcome>> CurrentOutcomesAsync(
        IReadOnlyCollection<long> correlations, CancellationToken ct = default);

    /// <summary>Every link, oldest first.</summary>
    Task<IReadOnlyList<OutcomeLink>> ReadLinksAsync(CancellationToken ct = default);

    /// <summary>The tenant rows (<c>outcome.*</c> only) whose subject is one of <paramref name="ids"/>,
    /// oldest first: each outcome's creation, renames, changes, status changes and merges, with who
    /// and when.</summary>
    Task<IReadOnlyList<TenantEvent>> ReadEventsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>What the figures are read from: every workflow's newest link, and the ledger's rows,
    /// narrowed to the window.</summary>
    Task<OutcomeLedgerRows> ReadLedgerAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default);

    /// <summary>
    /// WHAT A RUN'S ESTIMATE IS PRICED FROM: each <c>usage_ledger</c> row LEFT JOIN its
    /// <c>execution_snapshots</c> row on <c>start_seq</c>, with the outcome the snapshot's
    /// <c>link_id</c> names, and every workflow's newest link for a run with no snapshot. Windowed in
    /// the query: only runs whose [started_at, ended_at) overlaps [<paramref name="from"/>,
    /// <paramref name="to"/>), or - with no start - that ended in it. With <paramref name="pricedOnly"/>,
    /// only runs whose snapshot carries a rate: the rows a money total can come from.
    /// </summary>
    Task<ExecutionRunRows> ReadExecutionRunsAsync(
        DateTimeOffset? from, DateTimeOffset? to, bool pricedOnly, CancellationToken ct = default);

    /// <summary>Every backlog item with its outcome and its newest dispatch: what an outcome's
    /// Not started, In progress and Achieved are counted from.</summary>
    Task<IReadOnlyList<OutcomeBacklogItem>> ReadBacklogAsync(CancellationToken ct = default);

    /// <summary>
    /// WHAT THE HUMAN FIGURES ARE COUNTED FROM: every <c>intervention_ledger</c> row whose
    /// <c>occurred_at</c> is in [<paramref name="from"/>, <paramref name="to"/>), oldest first - each row once,
    /// and none of its actor's identity. Read only; nothing writes through this.
    /// </summary>
    Task<IReadOnlyList<InterventionEntry>> ReadInterventionsAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default);
}

/// <summary>
/// One <c>intervention_ledger</c> row as the human figures read it: when, which kind, the team and workflow
/// it was on, the workflow's outcome at that moment (<c>outcome_id_at_event</c>, as stored - a reader follows
/// it through <c>merged_into</c>), and its duration, which counts only when <paramref name="DurationMeasured"/>.
/// The actor is not read: no figure names a person.
/// </summary>
public sealed record InterventionEntry(
    long Id,
    DateTimeOffset OccurredAt,
    string Kind,
    string? TeamId,
    long? Correlation,
    string? OutcomeIdAtEvent,
    long? DurationSeconds,
    bool DurationMeasured)
{
    /// <summary>The columns <see cref="Read"/> takes, in order.</summary>
    public const string Columns =
        "id, occurred_at, kind, team_id, correlation, outcome_id_at_event, duration_seconds, duration_measured";

    /// <summary>One row, read from <see cref="Columns"/>.</summary>
    public static InterventionEntry Read(System.Data.Common.DbDataReader reader) => new(
        reader.GetInt64(0),
        Instant.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        !reader.IsDBNull(7) && Convert.ToInt32(reader.GetValue(7), System.Globalization.CultureInfo.InvariantCulture) == 1);
}

/// <summary>One outcome a delete removes: as it was, the outcome it came with when it was merged into
/// one asked for (<paramref name="CascadedFrom"/>, null for an id asked for), and what it took with
/// it.</summary>
public sealed record DeletedOutcome(
    string Id,
    string Name,
    string Status,
    string? MergedInto,
    string? CascadedFrom,
    int WorkflowsUnlinked,
    IReadOnlyList<string> BacklogItemsCleared,
    IReadOnlyList<string> TriggersCleared);

/// <summary>A delete's answer, or its preview's: every outcome it removes and the totals, or a
/// refusal sentence and its status.</summary>
public sealed record OutcomeDeletion(
    IReadOnlyList<DeletedOutcome> Outcomes, bool Preview, string? Refusal = null, int Status = 200)
{
    public bool Ok => Refusal is null;

    public int WorkflowsUnlinked => Outcomes.Sum(o => o.WorkflowsUnlinked);

    public int BacklogItemsCleared => Outcomes.Sum(o => o.BacklogItemsCleared.Count);

    public int TriggersCleared => Outcomes.Sum(o => o.TriggersCleared.Count);

    public static OutcomeDeletion Refused(int status, string sentence, bool preview) =>
        new([], preview, sentence, status);
}

/// <summary>One backlog item as the outcome figures see it: its own outcome (as stored, not yet
/// followed through merges), its state, whether it is archived, and its newest dispatch - team,
/// workflow, when, and when it landed - or none.</summary>
public sealed record OutcomeBacklogItem(
    string Id,
    string Title,
    string State,
    bool Archived,
    string? OutcomeId,
    string? Team,
    string? TeamName,
    long? Correlation,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? LandedAt);

/// <summary>
/// One run's snapshot as the cost read sees it (<c>outcome-008</c>): the member's kind, the rate it was
/// priced at when it started - canonical decimal text, never "0" - its currency and where it came from,
/// and the link current at start: its id, whether that row still exists, and the outcome it names (null
/// for an unlink).
/// </summary>
public sealed record ExecutionRunSnapshot(
    string? MemberKind,
    string? RateAmount,
    string? RateCurrency,
    string RateSource,
    long? LinkId,
    bool LinkFound,
    string? LinkOutcomeId);

/// <summary>One ledger row and its snapshot, or null for a run from before snapshots.</summary>
public sealed record ExecutionRunRow(UsageLedgerRow Run, long? StartSeq, ExecutionRunSnapshot? Snapshot);

/// <summary>What the cost read answers: the runs, every workflow's newest link, and the tenant rate's
/// history a run from before snapshots is priced from (<see cref="RateTimeline"/>).</summary>
public sealed record ExecutionRunRows(
    IReadOnlyList<OutcomeLink> CurrentLinks, IReadOnlyList<ExecutionRunRow> Runs, RateTimeline? Timeline = null);

/// <summary>
/// The one <c>rate_epochs</c> row (<c>outcome-012</c>): the tenant agent rate in force when that step was
/// applied, canonical decimal text or null, its currency upper-case or null, and where it came from
/// (<c>tenant</c>, <c>configuration</c> or <c>none</c>). Written once and never moved.
/// </summary>
public sealed record RateEpoch(DateTimeOffset RecordedAt, string? RateAmount, string? RateCurrency, string RateSource);

/// <summary>One <c>tenant.settingChanged</c> or <c>tenant.setting-reset</c> row for
/// <c>outcomes.agentHourlyRate</c>: when, its seq, and the value it set - null for a reset.</summary>
public sealed record RateChange(DateTimeOffset At, long Seq, string? NewValue);

/// <summary>
/// THE TENANT AGENT RATE'S HISTORY, as a run from before snapshots is priced from it: every recorded
/// change, oldest first, and the epoch row for a run that started before all of them.
/// </summary>
public sealed record RateTimeline(RateEpoch? Epoch, IReadOnlyList<RateChange> Changes)
{
    public static readonly RateTimeline Empty = new(null, []);
}

/// <summary>The ledger rows the figures are computed from.</summary>
public sealed record OutcomeLedgerRows(
    IReadOnlyList<OutcomeLink> CurrentLinks,
    IReadOnlyList<UsageLedgerRow> Runs,
    IReadOnlyList<WorkflowLedgerRow> Closes,
    IReadOnlyDictionary<string, string> LiveTeamNames);
