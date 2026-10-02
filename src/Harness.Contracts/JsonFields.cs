using System.Text.Json;

namespace Harness.Contracts;

/// <summary>Reading one field of a JSON line, where the line may not have it.</summary>
public static class JsonFields
{
    /// <summary>The string value of <paramref name="property"/>, or null when it is absent or not a string.</summary>
    public static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary><paramref name="name"/>'s value, or the default element when it is absent.</summary>
    public static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
}
