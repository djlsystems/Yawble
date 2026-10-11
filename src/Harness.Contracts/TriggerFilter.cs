using System.Text.Json;

namespace Harness.Contracts;

public enum FilterOperator { Eq, Contains }

/// <summary>
/// WHETHER A MESSAGE IS WORTH A WAKE, decided before one is spent.
///
/// <para>
/// A wake costs an agent invocation. A filter costs nothing. That asymmetry is the entire reason
/// this type exists rather than the instruction simply saying "ignore this unless it is about dev1":
/// an agent told to ignore something has already been paid for.
/// </para>
///
/// <para>
/// THE GRAMMAR IS DELIBERATELY MINIMAL - `field op value`, two operators, no boolean combinators.
/// Anything richer is a later decision rather than an omission to fix in passing, and every addition
/// is a new way for a filter to silently never match.
/// </para>
///
/// <para>
/// IT NEVER THROWS ON EVALUATION. The log is append-only and replayed, so a row written by an older
/// build may lack a field, carry a different shape, or not parse at all - and this runs inside the
/// delivery pump, where an exception stops every container's deliveries rather than one trigger's.
/// Refusal happens at the WRITE, through <see cref="RefusalFor"/>, where a person can read it.
/// </para>
/// </summary>
public sealed record TriggerFilter(string Field, FilterOperator Op, string Value)
{
    public static TriggerFilter? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;

        var op = parts[1].ToLowerInvariant() switch
        {
            "eq" => (FilterOperator?)FilterOperator.Eq,
            "contains" => FilterOperator.Contains,
            _ => null,
        };

        return op is null ? null : new TriggerFilter(parts[0], op.Value, parts[2]);
    }

    /// <summary>
    /// Why this filter cannot be saved for this event type, or NULL when it can.
    ///
    /// NAMES THE FIELDS THAT ARE AVAILABLE, not only the one that is wrong - a caller told merely
    /// "unknown field" guesses again. A refusal names the fix.
    /// </summary>
    public static string? RefusalFor(string? text, string eventType)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parsed = Parse(text);

        if (parsed is null)
        {
            return "A filter reads `field op value`, where op is `eq` or `contains` - "
                + $"for example `{PayloadFields.Source} eq Alpha/dev1`.";
        }

        // A CLOCK TRIGGER HAS NO EVENT PAYLOAD TO FILTER. Both POST and PATCH pass
        // `request.EventType ?? ""` here regardless of `kind`, so an empty `eventType` is what a
        // filter on a `cron`/`every`/`once` trigger looks like by the time it reaches this method -
        // never a real event type nothing declares. Naming that case before falling into the generic
        // "not an event this platform publishes" branch is what stops the refusal reading
        // `"" is not an event this platform publishes`, which names no recovery a person can act on.
        if (string.IsNullOrEmpty(eventType))
        {
            return "A filter belongs to an event trigger. Set kind to \"event\" and name an "
                + "eventType, or remove the filter.";
        }

        if (EventCatalog.For(eventType) is not { } definition)
        {
            return $"\"{eventType}\" is not an event this platform publishes.";
        }

        if (definition.Fields.Any(f => string.Equals(f.Name, parsed.Field, StringComparison.Ordinal)))
        {
            return null;
        }

        var available = string.Join(", ", definition.Fields.Select(f => f.Name));

        return $"\"{eventType}\" carries no field named \"{parsed.Field}\". "
            + $"It carries: {available}.";
    }

    /// <param name="payloadJson">The message's payload, as JSON text.</param>
    /// <param name="source">The message's envelope source (<c>Message.Source</c>) - matched only
    /// when this filter names <see cref="PayloadFields.Source"/>, never read out of the payload.
    /// A null or empty source does not match and does not throw, exactly like every other absent
    /// field: this runs inside the delivery pump, where an exception would stop every container's
    /// deliveries rather than costing one trigger its match.</param>
    public bool Matches(string? payloadJson, string? source)
    {
        if (string.Equals(Field, PayloadFields.Source, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(source)) return false;

            return Op switch
            {
                FilterOperator.Eq => string.Equals(source, Value, StringComparison.OrdinalIgnoreCase),
                FilterOperator.Contains => source.Contains(Value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        // `JsonDocument.Parse(string)` throws `ArgumentNullException` on a null argument, which the
        // catch below does not cover - it catches `JsonException` only. Not reachable today (nothing
        // calls `Matches` yet), but this is the first caller's guard: a null payload is exactly the
        // "nothing to match" case every other malformed shape already returns false for.
        if (payloadJson is null) return false;

        string? actual;

        try
        {
            using var document = JsonDocument.Parse(payloadJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty(Field, out var element)) return false;

            // MATCH `ContainerHost.EventFieldsOf`'s STRINGIFICATION EXACTLY, or a filter and a
            // `{event.<field>}` token would show different text for the same field. Returning false
            // for ANY non-String kind would make `exitCode eq 0` on `container.completed` (an
            // Integer field) accept, save, display as configured, and fire never - indistinguishable
            // from "that event hasn't happened yet", the exact silent-never-fires class this feature
            // exists to close.
            //
            // String reads its own text. Null/Undefined is "no value" - `EventFieldsOf` skips both
            // when building its dictionary, so this must reach the same "field absent" answer as a
            // missing property, not "matches the literal word null". Everything else - Integer,
            // Boolean, Array, Object - takes its raw JSON text, which already equals "true"/"false"
            // for a boolean without a special case: `GetRawText()` on a JSON `true`/`false` literal
            // IS the text "true"/"false".
            actual = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => element.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return false;
        }

        if (actual is null) return false;

        return Op switch
        {
            // IGNORE-CASE, LIKE `Contains` BESIDE IT - and like every container identity comparison
            // in this codebase. A container's identity is case-insensitive on both parts, so
            // `container eq alpha/dev1` is the filter most people will actually type, and it must
            // match a payload carrying `Alpha/dev1`. Getting this wrong is silent in the worse
            // direction than over-matching: a filter that matches too much is visibly wrong the
            // first time it fires, but one that never matches looks identical to "it has not
            // happened yet" - nothing distinguishes a filter that is broken from one that is merely
            // patient.
            FilterOperator.Eq => string.Equals(actual, Value, StringComparison.OrdinalIgnoreCase),
            FilterOperator.Contains => actual.Contains(Value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
