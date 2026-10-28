using System.Text;
using System.Text.Json;

namespace Harness.Host;

/// <summary>
/// A SIGN-IN BROWSER'S STORAGE STATE, AS PLAYWRIGHT'S <c>storageState</c> READS IT, pruned to what
/// <see cref="SignInHosts"/> keeps: the cookies sent to an entry host or a provider, and the local
/// storage of those hosts' origins. Everything else the browser picked up on the way - an analytics
/// cookie, a CAPTCHA's frame - is dropped.
/// </summary>
public static class SignInStorage
{
    /// <summary>One origin's local storage, as read from the browser.</summary>
    public sealed record Origin(string Name, IReadOnlyList<KeyValuePair<string, string>> Items);

    /// <summary>
    /// The storage state's JSON: <paramref name="cookies"/> as CDP's <c>Storage.getCookies</c> answers
    /// them, kept by <see cref="SignInHosts.KeepsCookie"/>, and <paramref name="origins"/> kept by
    /// <see cref="SignInHosts.KeepsOrigin"/>, each with at least one item.
    /// </summary>
    public static string Build(SignInHosts hosts, IEnumerable<JsonElement> cookies, IEnumerable<Origin> origins)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("cookies");
            foreach (var cookie in cookies)
            {
                var domain = String(cookie, "domain");
                if (domain is null || !hosts.KeepsCookie(domain)) continue;

                writer.WriteStartObject();
                writer.WriteString("name", String(cookie, "name") ?? "");
                writer.WriteString("value", String(cookie, "value") ?? "");
                writer.WriteString("domain", domain);
                writer.WriteString("path", String(cookie, "path") ?? "/");
                var session = cookie.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.True;
                writer.WriteNumber("expires", !session && cookie.TryGetProperty("expires", out var e) && e.TryGetDouble(out var at) ? at : -1);
                writer.WriteBoolean("httpOnly", cookie.TryGetProperty("httpOnly", out var h) && h.ValueKind == JsonValueKind.True);
                writer.WriteBoolean("secure", cookie.TryGetProperty("secure", out var sec) && sec.ValueKind == JsonValueKind.True);
                writer.WriteString("sameSite", String(cookie, "sameSite") switch { "Strict" => "Strict", "None" => "None", _ => "Lax" });
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("origins");
            foreach (var origin in origins.Where(o => o.Items.Count > 0 && hosts.KeepsOrigin(o.Name)).DistinctBy(o => o.Name))
            {
                writer.WriteStartObject();
                writer.WriteString("origin", origin.Name);
                writer.WriteStartArray("localStorage");
                foreach (var (name, value) in origin.Items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    writer.WriteString("value", value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
