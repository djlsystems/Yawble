using System.Text.Json;

namespace Harness.Contracts;

/// <summary>
/// What a run a TRIGGER started does to the Manager when it ends, chosen per trigger.
///
/// The fire's instruction carries the choice as <see cref="PayloadFields.WakeManager"/>, and the
/// run's terminal row carries it on, as one key, only when it is not <see cref="Always"/> - the way
/// <see cref="PayloadFields.Quiet"/> and <see cref="PayloadFields.HandedBack"/> ride on that row. The
/// pump passes over the Manager (a subscriber holding the type in its base set) on a `completed` row
/// that carries <see cref="OnHandbackOrFailure"/> or <see cref="Never"/>, and on a `failed` row that
/// carries <see cref="Never"/>. A hand-back is its own row and wakes as it always has.
///
/// ONLY A TRIGGER'S RUN. An instruction a member sent (a Manager's `tell`, which it is waiting on) or
/// a person's tell never carries the key, and one that says it anyway is not read: see
/// <see cref="OfInstruction"/>.
/// </summary>
public static class WakeManagerPolicy
{
    /// <summary>Every finished run wakes the Manager: the behaviour before the choice existed, and
    /// what every trigger from before it keeps.</summary>
    public const string Always = "always";

    /// <summary>The default for a new trigger: only a hand-back or a failure wakes the Manager; a run
    /// that simply finishes does not.</summary>
    public const string OnHandbackOrFailure = "onHandbackOrFailure";

    /// <summary>Nothing the run does on its own terminal row wakes the Manager, a failure included.
    /// The row is written and shown as ever; a hand-back still wakes on its own row.</summary>
    public const string Never = "never";

    public static IReadOnlyList<string> All { get; } = [Always, OnHandbackOrFailure, Never];

    /// <summary>The stored spelling for <paramref name="value"/>, matched case-insensitively, or
    /// null when it is none of the three.</summary>
    public static string? Parse(string? value) =>
        value is null ? null : All.FirstOrDefault(v => string.Equals(v, value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="source"/> is a trigger's: `schedule:&lt;id&gt;` from the
    /// sweep, `trigger:&lt;id&gt;` from the event arm of the pump.</summary>
    public static bool IsTriggerSource(string? source) =>
        source is not null
        && (source.StartsWith("schedule:", StringComparison.Ordinal)
            || source.StartsWith("trigger:", StringComparison.Ordinal));

    /// <summary>
    /// A trigger's fire: `{"instruction": ...}`, and the choice as a second key only when it is not
    /// <see cref="Always"/>, so a fire of a trigger that keeps today's behaviour is byte for byte
    /// what it was.
    /// </summary>
    public static string InstructionPayload(string instruction, string? policy) =>
        Parse(policy) is { } chosen && chosen != Always
            ? JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [PayloadFields.Instruction] = instruction,
                [PayloadFields.WakeManager] = chosen,
            })
            : JsonSerializer.Serialize(new { instruction });

    /// <summary>
    /// The choice an instruction carries, or null for <see cref="Always"/>: absent, unreadable,
    /// unknown, or on an instruction a trigger did not append. Null is what the terminal row leaves
    /// off, so every row that was not a trigger's is byte for byte what it was.
    /// </summary>
    public static string? OfInstruction(string? source, string? payload)
    {
        if (!IsTriggerSource(source) || string.IsNullOrEmpty(payload)) return null;

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(PayloadFields.WakeManager, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var parsed = Parse(value.GetString());
            return parsed is null or Always ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a subscriber that holds <paramref name="type"/> in its base set is passed over on a
    /// terminal row whose run's trigger chose <paramref name="policy"/> (the row's
    /// <see cref="PayloadFields.WakeManager"/>, null for <see cref="Always"/>).
    /// </summary>
    public static bool PassesOver(string type, string? policy) => policy switch
    {
        OnHandbackOrFailure => string.Equals(type, MessageTypes.Completed, StringComparison.Ordinal),
        Never => string.Equals(type, MessageTypes.Completed, StringComparison.Ordinal)
            || string.Equals(type, MessageTypes.Failed, StringComparison.Ordinal),
        _ => false,
    };
}
