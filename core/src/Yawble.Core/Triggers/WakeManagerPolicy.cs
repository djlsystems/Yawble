using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;

namespace Yawble.Core.Triggers;

/// <summary>
/// Whether a trigger's runs wake the Manager when they end (triggers.wake_manager):
/// <c>always</c>; <c>onHandbackOrFailure</c> (the default for a new trigger); <c>never</c>. A trigger saved before the
/// choice existed keeps <c>always</c>.
///
/// The fire's instruction carries the choice as <c>wakeManager</c> (only when it is not <c>always</c>), and the run's
/// <c>agent.completed</c> or <c>agent.failed</c> row carries it on, so the pump can pass over the Manager:
/// a <c>completed</c> row under <c>onHandbackOrFailure</c> or <c>never</c>, and a <c>failed</c> row under
/// <c>never</c>. A hand-back is its own row and always wakes the Manager. Only a trigger's own fire (source
/// <c>trigger:&lt;id&gt;</c>) is read: a Manager's tell or a person's never carries the choice, and one that claims it
/// is ignored. Otherwise a member on a short schedule costs a Manager run per fire to learn nothing.
/// </summary>
public static class WakeManagerPolicy
{
    public const string Always = "always";
    public const string OnHandbackOrFailure = "onHandbackOrFailure";
    public const string Never = "never";
    public const string Field = "wakeManager";

    public static readonly IReadOnlyList<string> All = [Always, OnHandbackOrFailure, Never];

    public static bool IsValid(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    public static bool IsTriggerSource(string source) => source.StartsWith(BusEngine.TriggerSourcePrefix, StringComparison.Ordinal);

    /// <summary>A fire's instruction payload: the text, plus the choice when it is not <c>always</c>.</summary>
    public static string InstructionPayload(string text, string wakeManager) =>
        wakeManager == Always ? BatchText.InstructionPayload(text) : JsonSerializer.Serialize(new { text, wakeManager });

    /// <summary>The choice an instruction carries; null for <c>always</c> and for anything not a trigger's fire.</summary>
    public static string? OfInstruction(Message message) =>
        IsTriggerSource(message.Source) ? Read(message.Payload) is { } w && w != Always ? w : null : null;

    /// <summary>
    /// The choice a run's ending row carries: only when every item of the run is a trigger's fire carrying one (a
    /// Manager's tell in the same batch is waiting on the answer). Mixed choices take the louder one.
    /// </summary>
    public static string? OfRun(IReadOnlyList<Message> items)
    {
        if (items.Count == 0) return null;
        var choices = items.Select(OfInstruction).ToList();
        if (choices.Any(c => c is null)) return null;
        return choices.Contains(OnHandbackOrFailure) ? OnHandbackOrFailure : Never;
    }

    /// <summary>Adds the choice to a run's ending row's JSON when there is one.</summary>
    public static string WithChoice(string payload, string? wakeManager)
    {
        if (wakeManager is null) return payload;
        var node = JsonNode.Parse(payload)!.AsObject();
        node[Field] = wakeManager;
        return node.ToJsonString();
    }

    /// <summary>True when the Manager is passed over on this row (it holds the type only as a Manager watch).</summary>
    public static bool PassesOver(Message row) =>
        (row.Type, Read(row.Payload)) switch
        {
            (MessageTypes.Completed, OnHandbackOrFailure or Never) => true,
            (MessageTypes.Failed, Never) => true,
            _ => false,
        };

    private static string? Read(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(Field, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
