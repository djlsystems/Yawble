using System.Text.Json;
using Harness.Contracts;

namespace Harness.Containers;

/// <summary>
/// WHAT THE WAKE RULES READ OF A TERMINAL ROW, parsed once. The pump asked five questions of a
/// completed row's payload - a later row of one run, handed back, quiet, declared by the platform,
/// the trigger's wake choice - each with its own parse, for every subscriber the row reached.
/// </summary>
/// <param name="LaterRowOfOneRun">A batched run's later item: carries <see cref="PayloadFields.UsageCountedOn"/> as a number.</param>
/// <param name="HandedBack">The run handed back before it completed.</param>
/// <param name="Quiet">A plugin's quiet completion.</param>
/// <param name="WorkflowDeclared">The run's workflow was declared as it ended.</param>
/// <param name="WakeManager">The trigger's wake choice carried on the row, or null.</param>
/// <param name="AnswersTell">The member whose `tell` the row answers, or null. See <see cref="PayloadFields.AnswersTell"/>.</param>
/// <param name="AskerTold">The run already told the asker its answer as an instruction. See <see cref="PayloadFields.AskerTold"/>.</param>
/// <param name="NeedsPerson">A `failed` row of class `needs-person`.</param>
public sealed record TerminalRowFacts(
    bool LaterRowOfOneRun, bool HandedBack, bool Quiet, bool WorkflowDeclared, string? WakeManager,
    string? AnswersTell = null, bool NeedsPerson = false, bool AskerTold = false)
{
    public static readonly TerminalRowFacts None = new(false, false, false, false, null);

    /// <summary>
    /// Whether <paramref name="subscriber"/> is the member this row answers and is woken by it whether
    /// or not it subscribes to the type: not when the run already told it the answer
    /// (<see cref="AskerTold"/>), which woke it on its own row.
    /// </summary>
    public bool Answers(ContainerId subscriber) => !AskerTold && Asked(subscriber);

    private bool Asked(ContainerId subscriber) =>
        AnswersTell is not null && string.Equals(AnswersTell, subscriber.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a subscriber holding the row's type in its BASE set (the Manager) is passed over
    /// because the row answers ANOTHER member's `tell`, which that member is woken by. A
    /// `needs-person` failure is not passed over: only a person can fix it, and the Manager is told.
    /// </summary>
    public bool AnswersSomeoneElse(ContainerId subscriber) =>
        AnswersTell is not null && !Asked(subscriber) && !NeedsPerson;

    /// <summary>The facts of <paramref name="message"/>; <see cref="None"/> for a row that is not terminal or whose payload is not an object.</summary>
    public static TerminalRowFacts Of(Message message)
    {
        if (message.Type is not (MessageTypes.Completed or MessageTypes.Failed)) return None;

        try
        {
            using var document = JsonDocument.Parse(message.Payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return None;

            return new TerminalRowFacts(
                root.TryGetProperty(PayloadFields.UsageCountedOn, out var counted) && counted.ValueKind == JsonValueKind.Number,
                True(root, PayloadFields.HandedBack),
                True(root, PayloadFields.Quiet),
                True(root, PayloadFields.WorkflowDeclared),
                root.TryGetProperty(PayloadFields.WakeManager, out var wake) && wake.ValueKind == JsonValueKind.String ? wake.GetString() : null,
                root.TryGetProperty(PayloadFields.AnswersTell, out var asker) && asker.ValueKind == JsonValueKind.String ? asker.GetString() : null,
                message.Type == MessageTypes.Failed
                    && root.TryGetProperty(PayloadFields.FailureClass, out var failure)
                    && failure.ValueKind == JsonValueKind.String
                    && failure.GetString() == FailureClasses.NeedsPerson,
                True(root, PayloadFields.AskerTold));
        }
        catch (JsonException)
        {
            return None;
        }
    }

    private static bool True(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;
}
