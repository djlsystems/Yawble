using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Integrations;

/// <summary>
/// B143: what the Tool calls log (integration_calls.args) keeps of a web API's or an Email integration's arguments. Every argument's
/// name stays; a value stays only when it is not message content:
/// - a field named like content (body text, message, subject, html, description, note, ...) keeps only its length: "(57 chars)";
///   an object or list under such a name keeps its field names, each value as its length;
/// - a phone number keeps its last 4 digits: "...0001" (a "whatsapp:" or other prefix stays);
/// - any other text longer than <see cref="MaxValue"/> characters, or with a line break, keeps only its length;
/// - numbers, true/false and null stay.
/// "body" holding an object is the request's body (its fields are judged by name); "body" holding text is content. The action and the
/// HTTP status are kept elsewhere in the row (args[0], exit_code). The secret scrubbing before this stays. Old rows are not rewritten.
/// </summary>
public static partial class CallArgsRedaction
{
    public const int MaxValue = 120;
    private const int MaxItems = 20;

    private static readonly HashSet<string> ContentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "body", "text", "message", "messages", "msg", "subject", "content", "contents", "html", "plain", "plaintext", "snippet", "preview",
        "description", "comment", "comments", "note", "notes", "caption", "title", "summary", "prompt", "query", "q", "payload", "data",
        "attachment", "attachments", "mediaurl", "template", "markdown", "blocks",
    };

    /// <summary>The integrations whose args[1] is a JSON object of arguments: web APIs and websites ("api:"), and Email ("email:").</summary>
    public static bool Applies(string integration) =>
        integration.StartsWith("api:", StringComparison.Ordinal) || integration.StartsWith(IntegrationService.EmailIntegrationPrefix, StringComparison.Ordinal);

    /// <summary>The call's args as the log keeps them: args[1] redacted when it is a JSON object; the rest as they are.</summary>
    public static IReadOnlyList<string> ForLog(string integration, IReadOnlyList<string> args)
    {
        if (!Applies(integration) || args.Count < 2) return args;
        JsonObject? given;
        try { given = JsonNode.Parse(args[1]) as JsonObject; }
        catch (JsonException) { given = null; }
        if (given is null) return args;
        var list = args.ToList();
        list[1] = Redact(given).ToJsonString();
        return list;
    }

    /// <summary>The argument names, with the values that are not message content (see the class).</summary>
    public static JsonObject Redact(JsonObject args)
    {
        var o = new JsonObject();
        foreach (var (key, value) in args) o[key] = Value(key, value, content: false, top: true);
        return o;
    }

    private static JsonNode? Value(string key, JsonNode? value, bool content, bool top)
    {
        // "body" holding an object at the top is the request's body: its fields are judged by their own names
        var isContent = content || (IsContentName(key) && !(top && value is JsonObject && Normal(key) == "body"));
        switch (value)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var o = new JsonObject();
                foreach (var (k, v) in obj) o[k] = Value(k, v, isContent, top: false);
                return o;
            }
            case JsonArray arr:
            {
                var a = new JsonArray();
                foreach (var item in arr.Take(MaxItems)) a.Add(Value(key, item, isContent, top: false));
                if (arr.Count > MaxItems) a.Add(JsonValue.Create($"(+{arr.Count - MaxItems} more)"));
                return a;
            }
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
            {
                var s = v.GetValue<string>();
                if (isContent) return Length(s);
                if (Phone(s) is { } phone) return phone;
                return s.Length > MaxValue || s.IndexOfAny(['\r', '\n']) >= 0 ? Length(s) : JsonValue.Create(s);
            }
            default:
                // numbers, true/false: kept (content's too: a count or a flag is not what was said)
                return value.DeepClone();
        }
    }

    private static JsonValue Length(string s) => JsonValue.Create($"({s.Length} chars)");

    /// <summary>A content name, or one that ends in one of the plainest (html_body, MessageBody, bodyText, email_subject).</summary>
    private static bool IsContentName(string key)
    {
        var n = Normal(key);
        return ContentNames.Contains(n) || ContentEndings.Any(e => n.Length > e.Length && n.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] ContentEndings = ["body", "text", "message", "subject", "content", "html"];

    private static string Normal(string key) => key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    /// <summary>
    /// A phone number to its last 4 digits: "+" and 7 to 15 digits (E.164, spaces dots dashes or brackets allowed) or "(555) 123-4567",
    /// after an optional "scheme:" prefix (whatsapp:, tel:). A date, an id or a count is not one (no "+" or bracket first).
    /// </summary>
    internal static JsonValue? Phone(string s)
    {
        var m = PhonePattern().Match(s.Trim());
        if (!m.Success) return null;
        var digits = new string(m.Groups["number"].Value.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is < 7 or > 15) return null;
        return JsonValue.Create(m.Groups["prefix"].Value + "..." + digits[^4..]);
    }

    [GeneratedRegex(@"^(?<prefix>[A-Za-z][A-Za-z0-9+.-]{0,20}:)?(?<number>\+[0-9][0-9 ().-]{5,24}|\([0-9]{3}\)[0-9 .-]{6,12})$")]
    private static partial Regex PhonePattern();
}
