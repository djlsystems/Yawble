using System.Text.Json;

namespace Harness.Contracts;

/// <summary>
/// WHY A RUN OR A WORKFLOW WAS INTERRUPTED, as one code from a closed set: the <c>reasonCode</c> beside
/// the free-text <c>reason</c> on <c>agentContainer.blocked</c>, <c>agentContainer.needsDecision</c>,
/// <c>agentContainer.rejected</c>, <c>workflow.paused</c> and <c>agentContainer.scheduleSkipped</c>,
/// and <c>usage_ledger.reason_code</c> on a run that ended interrupted. Modelled on
/// <see cref="FailureClasses"/>, and the one place these words are spelled.
///
/// <para>
/// CHOSEN BY THE PLATFORM, NEVER READ FROM FREE TEXT. Each publisher passes the code for what it knows
/// caused the interruption (a token cap, a workflow budget, an archived team ...), and the store stamps
/// every interruption row through <see cref="Stamped"/> on both providers: a code outside the set is
/// refused, a second one is refused, and a row that names none gets the default for its type, read from
/// its structured fields only. So every interruption row holds exactly one code from this set.
/// </para>
///
/// <para>
/// <c>failed</c> GAINS NO FIELD: its code is <see cref="ForFailureClass"/> of its <c>failureClass</c>.
/// <c>workflow.resumed</c> is not an interruption and has no code.
/// </para>
/// </summary>
public static class InterruptionReasons
{
    /// <summary>The team's daily token cap is reached, so a scheduled or triggered fire was skipped.</summary>
    public const string TokenCap = "token_cap";

    /// <summary>The workflow spent over its per-workflow budget (team or instance): the delivery was
    /// rejected and the workflow paused until a person resumes it.</summary>
    public const string WorkflowBudget = "workflow_budget";

    /// <summary>Something only a person supplies is missing: a key or connection to bind or reconnect
    /// (the <c>needs-person</c> failure class), or a required input a solution team waits for.</summary>
    public const string Auth = "auth";

    /// <summary>A permission was refused. In the set for later publishers; nothing writes it yet.</summary>
    public const string Permission = "permission";

    /// <summary>A person's approval is required. In the set for later publishers; nothing writes it yet.</summary>
    public const string ApprovalRequired = "approval_required";

    /// <summary>A member asked a person for a decision (<c>needs_decision</c>).</summary>
    public const string DecisionRequired = "decision_required";

    /// <summary>A person paused the team, so a scheduled or triggered fire was skipped.</summary>
    public const string PausedByPerson = "paused_by_person";

    /// <summary>The team is archived, so a scheduled or triggered fire was skipped.</summary>
    public const string TeamArchived = "team_archived";

    /// <summary>An idle-only trigger's member was busy, so its fire was skipped.</summary>
    public const string Busy = "busy";

    /// <summary>Work was refused at admission: the member's queue was at its ceiling.</summary>
    public const string Admission = "admission";

    /// <summary>
    /// The run failed or gave up and nothing more precise is known: every generic failure class, and the
    /// default for a member's own <c>blocked</c> call.
    /// </summary>
    public const string Failure = "failure";

    /// <summary>Every code, for a test or a reader that wants to enumerate them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        TokenCap, WorkflowBudget, Auth, Permission, ApprovalRequired, DecisionRequired,
        PausedByPerson, TeamArchived, Busy, Admission, Failure,
    ];

    /// <summary>The message types that are interruptions and carry a <c>reasonCode</c>.</summary>
    public static IReadOnlyList<string> CarriedOn { get; } =
    [
        MessageTypes.Blocked, MessageTypes.NeedsDecision, MessageTypes.Rejected,
        MessageTypes.WorkflowPaused, MessageTypes.ScheduleSkipped,
    ];

    /// <summary>Whether <paramref name="code"/> is one of <see cref="All"/>, spelled exactly.</summary>
    public static bool IsKnown(string? code) =>
        code is not null && All.Contains(code, StringComparer.Ordinal);

    /// <summary>Whether a row of <paramref name="type"/> carries a <c>reasonCode</c>.</summary>
    public static bool IsInterruption(string? type) =>
        type is not null && CarriedOn.Contains(type, StringComparer.Ordinal);

    /// <summary>
    /// The code itself when it is in the set; anything else is refused with one sentence. What a
    /// publisher calls before it writes a code it chose.
    /// </summary>
    public static string Require(string? code) =>
        IsKnown(code)
            ? code!
            : throw new ArgumentException(
                $"'{code}' is not an interruption reason; it must be one of {string.Join(", ", All)}.",
                nameof(code));

    /// <summary>
    /// THE FAILURE MAPPING: the code a <c>failed</c> row (or a <c>blocked</c> row carrying a failure
    /// class) stands for. <c>needs-person</c> waits for a person to set a key or a connection, so it is
    /// <see cref="Auth"/>; every other class, an unknown spelling and no class at all are
    /// <see cref="Failure"/>.
    /// </summary>
    public static string ForFailureClass(string? failureClass) =>
        failureClass == FailureClasses.NeedsPerson ? Auth : Failure;

    /// <summary>
    /// THE CODE A ROW STANDS FOR, read back: an interruption row's own code when it holds a known one,
    /// else its type's default (a row from before codes existed); a <c>failed</c> row's mapped class;
    /// null for every other type.
    /// </summary>
    public static string? Of(string type, string? payload)
    {
        if (type == MessageTypes.Failed) return ForFailureClass(Text(payload, PayloadFields.FailureClass));
        if (!IsInterruption(type)) return null;

        var stored = Text(payload, PayloadFields.ReasonCode);
        return IsKnown(stored) ? stored : DefaultFor(type, payload);
    }

    /// <summary>
    /// THE STORE'S RULE, on both providers: the payload an interruption row is written with. A known code
    /// is kept as sent; a code outside the set, a code that is not a string and a second code are refused
    /// (the row is not written); no code at all gets the type's default from <see cref="DefaultFor"/>.
    /// Every other type, and a payload that is not a JSON object, is returned unchanged.
    /// </summary>
    public static string Stamped(string type, string payload)
    {
        if (!IsInterruption(type)) return payload;

        var text = string.IsNullOrWhiteSpace(payload) ? "{}" : payload;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return payload;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return payload;

            var codes = document.RootElement.EnumerateObject()
                .Where(property => property.NameEquals(PayloadFields.ReasonCode))
                .ToArray();

            if (codes.Length > 1)
            {
                throw new ArgumentException("An interruption row carries exactly one reasonCode, and this one has more.");
            }

            if (codes.Length == 1)
            {
                var value = codes[0].Value;
                Require(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
                return payload;
            }
        }

        // INSERTED, NOT RE-SERIALISED, so every byte the publisher wrote stays as it was.
        var code = JsonSerializer.Serialize(DefaultFor(type, text));
        var open = text.IndexOf('{', StringComparison.Ordinal);
        var rest = text[(open + 1)..];
        var separator = rest.TrimStart().StartsWith('}') ? "" : ",";
        return $"{text[..(open + 1)]}\"{PayloadFields.ReasonCode}\":{code}{separator}{rest}";
    }

    /// <summary>
    /// THE DEFAULT FOR A ROW THAT NAMES NO CODE, from its type and its structured fields only, never its
    /// free text: <c>needsDecision</c> is <see cref="DecisionRequired"/>; <c>blocked</c> maps its
    /// <c>failureClass</c> (a member's own blocked call has none, so <see cref="Failure"/>); a
    /// <c>workflow.paused</c> with a <c>limitSource</c> is <see cref="WorkflowBudget"/>; anything else is
    /// <see cref="Failure"/>.
    /// </summary>
    public static string DefaultFor(string type, string? payload) => type switch
    {
        MessageTypes.NeedsDecision => DecisionRequired,
        MessageTypes.Blocked => ForFailureClass(Text(payload, PayloadFields.FailureClass)),
        MessageTypes.WorkflowPaused when Text(payload, PayloadFields.LimitSource) is not null => WorkflowBudget,
        _ => Failure,
    };

    private static string? Text(string? payload, string field)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
