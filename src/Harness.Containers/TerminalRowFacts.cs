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
public sealed record TerminalRowFacts(
    bool LaterRowOfOneRun, bool HandedBack, bool Quiet, bool WorkflowDeclared, string? WakeManager)
{
    public static readonly TerminalRowFacts None = new(false, false, false, false, null);

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
                root.TryGetProperty(PayloadFields.WakeManager, out var wake) && wake.ValueKind == JsonValueKind.String ? wake.GetString() : null);
        }
        catch (JsonException)
        {
            return None;
        }
    }

    private static bool True(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;
}
