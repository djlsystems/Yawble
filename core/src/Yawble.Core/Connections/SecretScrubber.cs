using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yawble.Core.Connections;

/// <summary>
/// A process-wide set of known secret values (from Connections). <see cref="Scrub"/> removes them from any text that
/// might reach an agent, a log or a call record, in one pass (longest value first), so a replacement is never matched
/// again and overlapping values leave no fragment. Each value is trimmed and also registered in its JSON-escaped and
/// URL-escaped forms; forms shorter than <see cref="MinLength"/> are ignored. Entries are scoped by owner (a service
/// instance) because connection ids are only unique per database. Thread-safe.
/// </summary>
public static class SecretScrubber
{
    public const int MinLength = 6;
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Lock Gate = new();
    private static readonly Dictionary<(object Owner, long Id), string[]> Entries = [];
    private static Regex? _pattern;

    public static void Set(object owner, long connectionId, IEnumerable<string> values)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            var v = raw.Trim();
            var escaped = Uri.EscapeDataString(v);
            var form = System.Net.WebUtility.UrlEncode(v); // a form's encoding: "+" for a space
            foreach (var f in new[] { v, JsonSerializer.Serialize(v)[1..^1], JsonSerializer.Serialize(v, Relaxed)[1..^1], escaped, LowerHex(escaped), form, LowerHex(form) })
                if (f.Length >= MinLength) forms.Add(f);
        }
        lock (Gate)
        {
            if (forms.Count == 0) Entries.Remove((owner, connectionId)); else Entries[(owner, connectionId)] = [.. forms];
            Rebuild();
        }
    }

    /// <summary>The same percent-encoding with lowercase hex digits (%2f instead of %2F), as some services echo it.</summary>
    private static string LowerHex(string encoded) => Regex.Replace(encoded, "%[0-9A-F]{2}", m => m.Value.ToLowerInvariant());

    public static void Forget(object owner, long connectionId)
    {
        lock (Gate)
        {
            if (Entries.Remove((owner, connectionId))) Rebuild();
        }
    }

    public static string Scrub(string text)
    {
        var pattern = Volatile.Read(ref _pattern);
        return pattern is null || text.Length == 0 ? text : pattern.Replace(text, "[redacted]");
    }

    private static void Rebuild()
    {
        var all = Entries.Values.SelectMany(x => x).Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToArray();
        Volatile.Write(ref _pattern, all.Length == 0 ? null : new Regex(string.Join('|', all.Select(Regex.Escape)), RegexOptions.CultureInvariant));
    }
}
