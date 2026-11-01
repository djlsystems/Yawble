using System.Text;
using System.Text.Json;

namespace Harness.Contracts;

/// <summary>
/// WHAT AN ID TOKEN SAYS OF THE ACCOUNT, read on the Host when a sign-in is saved and on a worker when
/// it is tested. Its claims are read, not verified: the token came straight from the provider's token
/// endpoint over TLS, in answer to a refresh token only this connection holds.
/// </summary>
public static class IdTokens
{
    /// <summary>The claims that name a Microsoft account, in the order they are preferred.</summary>
    public static readonly IReadOnlyList<string> MicrosoftAccountClaims = ["preferred_username", "email", "upn"];

    /// <summary>The claims of <paramref name="idToken"/>, or null when it is not a JWT with a JSON payload.</summary>
    public static JsonElement? Claims(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.Clone();
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The first of <paramref name="keys"/> that <paramref name="claims"/> carry as a non-empty
    /// string or a number, trimmed; null with none.</summary>
    public static string? Claim(JsonElement? claims, IEnumerable<string> keys)
    {
        if (claims is not { ValueKind: JsonValueKind.Object } found) return null;

        foreach (var key in keys)
        {
            if (found.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) return text.Trim();
                if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
            }
        }

        return null;
    }
}
