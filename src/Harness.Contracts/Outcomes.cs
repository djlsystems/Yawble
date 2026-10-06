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
    string? Value = null)
{
    public bool IsLive => OutcomeStatus.IsLive(Status);
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
    string? Value = null);

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

    /// <summary>Every backlog item with its outcome and its newest dispatch: what an outcome's
    /// Not started, In progress and Achieved are counted from.</summary>
    Task<IReadOnlyList<OutcomeBacklogItem>> ReadBacklogAsync(CancellationToken ct = default);
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

/// <summary>The ledger rows the figures are computed from.</summary>
public sealed record OutcomeLedgerRows(
    IReadOnlyList<OutcomeLink> CurrentLinks,
    IReadOnlyList<UsageLedgerRow> Runs,
    IReadOnlyList<WorkflowLedgerRow> Closes,
    IReadOnlyDictionary<string, string> LiveTeamNames);
