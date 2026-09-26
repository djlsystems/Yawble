using System.Text.Json;

namespace Harness.Contracts;

/// <summary>
/// The first non-empty STRING field from one payload, in caller-provided order.
///
/// NOT null-coalescing (`??`). An empty string is still a value, so `??` stops and hides later
/// fields. Failure payloads routinely carry `output: ""` with the reason in `launchError`.
/// </summary>
public static class FailurePayloadText
{
    public static string? FirstNonEmpty(JsonElement? payload, params string[] orderedFields)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        foreach (var name in orderedFields)
        {
            if (!element.TryGetProperty(name, out var value)
                || value.ValueKind is not JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }
}
