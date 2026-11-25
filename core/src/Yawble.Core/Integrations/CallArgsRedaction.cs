using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Integrations;

/// <summary>
/// What the Tool calls log (integration_calls.args) keeps of a call's arguments, for every kind of integration.
/// A web API's, a website's, an Email integration's or an MCP server's arguments (args[1], a JSON object): every argument's
/// name stays; a value stays only when it is not message content:
/// - a field named like content (body text, message, subject, html, description, note, ...) keeps only its length: "(57 chars)";
///   an object or list under such a name keeps its field names, each value as its length;
/// - a phone number keeps its last 4 digits: "...0001" (a "whatsapp:" or other prefix stays);
/// - any other text longer than <see cref="MaxValue"/> characters, or with a line break, keeps only its length;
/// - numbers, true/false and null stay.
/// "body" holding an object is the request's body (its fields are judged by name); "body" holding text is content.
/// A program's command line (gh, az, a watch): every word stays except the value of a content option (--body, --title, -m, ...,
/// also as --body=..., and gh's -f/-F name=value judged by the name), which keeps its length; a phone number keeps its last 4 digits;
/// a word with a line break or longer than <see cref="MaxArgvValue"/> keeps its length.
/// The action and the status are kept as they are (args[0], exit_code: a program's exit code or a web API's HTTP status). The secret
/// scrubbing before this stays. Old rows are not rewritten.
/// </summary>
public static partial class CallArgsRedaction
{
    public const int MaxValue = 120;
    public const int MaxArgvValue = 300;
    private const int MaxItems = 20;

    /// <summary>Options of a program whose value is what is said (an issue's body, a commit message, a title).</summary>
    private static readonly HashSet<string> ContentOptions = new(StringComparer.Ordinal)
    {
        "--body", "--title", "--message", "--subject", "--text", "--description", "--notes", "--comment", "--content", "--html",
        "--caption", "--summary", "--prompt", "-b", "-t", "-m",
    };

    /// <summary>Options whose value is "name=value" (gh api): the value is judged by its name.</summary>
    private static readonly HashSet<string> FieldOptions = new(StringComparer.Ordinal) { "-f", "-F", "--field", "--raw-field" };

    private static readonly HashSet<string> ContentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "body", "text", "message", "messages", "msg", "subject", "content", "contents", "html", "plain", "plaintext", "snippet", "preview",
        "description", "comment", "comments", "note", "notes", "caption", "title", "summary", "prompt", "query", "q", "payload", "data",
        "attachment", "attachments", "mediaurl", "template", "markdown", "blocks",
    };

    /// <summary>The integrations whose args[1] is a JSON object of arguments: web APIs and websites ("api:"), Email ("email:") and MCP servers ("mcp:").</summary>
    public static bool Applies(string integration) =>
        integration.StartsWith("api:", StringComparison.Ordinal) || integration.StartsWith(IntegrationService.EmailIntegrationPrefix, StringComparison.Ordinal)
        || integration.StartsWith("mcp:", StringComparison.Ordinal);

    /// <summary>A program's command line (a CLI or its watch): an integration named without a "kind:" prefix.</summary>
    public static bool IsCommandLine(string integration) => !integration.Contains(':', StringComparison.Ordinal);

    /// <summary>
    /// The call's args as the log keeps them: for a JSON-arguments integration args[1] redacted when it is a JSON object and the rest as
    /// they are; for a program its command line redacted (see the class); anything else as it is.
    /// </summary>
    public static IReadOnlyList<string> ForLog(string integration, IReadOnlyList<string> args)
    {
        if (IsCommandLine(integration)) return CommandLine(args);
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

    /// <summary>A program's command line as the log keeps it (see the class); the same list when nothing changes.</summary>
    public static IReadOnlyList<string> CommandLine(IReadOnlyList<string> args)
    {
        string[]? kept = null;
        for (var i = 0; i < args.Count; i++)
        {
            var word = args[i] ?? "";
            string next;
            if (i > 0 && ContentOptions.Contains(args[i - 1] ?? "")) next = Chars(word);
            else if (i > 0 && FieldOptions.Contains(args[i - 1] ?? "")) next = Field(word);
            else if (word.StartsWith("--", StringComparison.Ordinal) && word.IndexOf('=', StringComparison.Ordinal) is var eq and > 2)
                next = ContentOptions.Contains(word[..eq]) ? word[..(eq + 1)] + Chars(word[(eq + 1)..])
                    : FieldOptions.Contains(word[..eq]) ? word[..(eq + 1)] + Field(word[(eq + 1)..])
                    : word[..(eq + 1)] + Word(word[(eq + 1)..]);
            else next = Word(word);
            if (next == word) continue;
            kept ??= [.. args];
            kept[i] = next;
        }
        return kept ?? args;
    }

    /// <summary>gh's name=value: content's value as its length, any other value as a plain word.</summary>
    private static string Field(string word)
    {
        var eq = word.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0) return Word(word);
        var name = word[..eq];
        return name + "=" + (IsContentName(name) ? Chars(word[(eq + 1)..]) : Word(word[(eq + 1)..]));
    }

    private static string Word(string word) =>
        Phone(word)?.GetValue<string>() ?? (word.Length > MaxArgvValue || word.IndexOfAny(['\r', '\n']) >= 0 ? Chars(word) : word);

    private static string Chars(string s) => $"({s.Length} chars)";

    private static JsonValue Length(string s) => JsonValue.Create(Chars(s));

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
