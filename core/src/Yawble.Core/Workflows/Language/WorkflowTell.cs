using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Workflows.Language;

/// <summary>
/// The <c>tell</c> step (design section 5.1): its defaults and limits, its timeout's text, its result type and how a reply's
/// JSON is read from the agent's closing summary.
/// </summary>
public static partial class WorkflowTell
{
    public const string StepType = "tell";
    public const string DefaultMember = "Manager";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(7);

    /// <summary>The tells one run may make by default (a member setting, at most <see cref="MaxTellsLimit"/>).</summary>
    public const int DefaultMaxTells = 5;
    public const int MaxTellsLimit = 50;

    /// <summary>The most files one tell attaches, and the largest one.</summary>
    public const int MaxAttachments = 20;
    public const long MaxAttachmentBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Whether a file is a HEIC/HEIF image (by its extension). These are attached as they are: .NET has no HEIF decoder, and
    /// Windows decodes HEIF only with the optional HEIF and HEVC extensions from the Store, so converting to JPEG would need a new
    /// dependency. The step log and the instruction say so.
    /// </summary>
    public static bool IsHeif(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif" or ".hif";

    /// <summary>What the step log and the instruction add after a HEIC/HEIF attachment.</summary>
    public const string HeifNote = "a HEIC/HEIF image, copied as it is (not converted to JPEG); an agent's image tools may not open it";

    /// <summary>The instruction's closing lines naming the attached copies (in the team's documents) for the agent to open.</summary>
    public static string AttachmentsText(IReadOnlyList<string> copies)
    {
        if (copies.Count == 0) return "";
        var sb = new System.Text.StringBuilder(copies.Count == 1
            ? "The workflow attached a file, copied into the team's documents. Open it with your file-reading tool:"
            : $"The workflow attached {copies.Count} files, copied into the team's documents. Open each with your file-reading tool:");
        foreach (var c in copies) sb.Append("\n- ").Append(c).Append(IsHeif(c) ? $" ({HeifNote})" : "");
        return sb.ToString();
    }

    public static class Outcomes
    {
        public const string Sent = "sent", Done = "done", Failed = "failed", Blocked = "blocked", TimedOut = "timedOut", DryRun = "dryRun";
        public static readonly IReadOnlyList<string> All = [Sent, Done, Failed, Blocked, TimedOut, DryRun];
    }

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*(d|h|m|s)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Part();

    /// <summary>
    /// A timeout as a workflow writes it: <c>4h</c>, <c>30m</c>, <c>2d</c>, <c>1h 30m</c>, <c>90s</c>, ISO 8601 (<c>PT4H</c>),
    /// <c>hh:mm:ss</c>, or a number of hours.
    /// </summary>
    public static bool TryParseTimeout(JsonNode? node, out TimeSpan value)
    {
        value = default;
        if (node is not JsonValue v) return false;
        if (v.GetValueKind() == JsonValueKind.Number)
        {
            value = TimeSpan.FromHours(v.GetValue<double>());
            return true;
        }
        if (v.GetValueKind() != JsonValueKind.String) return false;
        var s = v.GetValue<string>().Trim();
        if (s.Length == 0) return false;
        if (WorkflowValues.TryParseDuration(s, out value)) return true;
        var total = TimeSpan.Zero;
        var rest = s;
        while (rest.Length > 0)
        {
            var m = Part().Match(rest);
            if (!m.Success) return false;
            var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += char.ToLowerInvariant(m.Groups[2].Value[0]) switch
            {
                'd' => TimeSpan.FromDays(n),
                'h' => TimeSpan.FromHours(n),
                'm' => TimeSpan.FromMinutes(n),
                _ => TimeSpan.FromSeconds(n),
            };
            rest = rest[m.Length..];
        }
        value = total;
        return true;
    }

    /// <summary>
    /// The step's result type: TellResult, its <c>data</c> typed by the reply schema when the step names one (the name stays
    /// TellResult, so it goes into a variable declared TellResult).
    /// </summary>
    public static WorkflowType ResultType(string? replySchema)
    {
        var schema = (JsonObject)BuiltInSchemas.TellResult.Schema.DeepClone();
        if (replySchema is not null)
            schema["properties"]!["data"] = new JsonObject { ["$ref"] = replySchema, ["description"] = $"The reply's JSON, a {replySchema}." };
        return WorkflowType.ObjectOf(BuiltInSchemas.TellResult.Name, schema);
    }

    /// <summary>A TellResult value.</summary>
    public static JsonObject Result(string outcome, long deliverable, string? reply, JsonNode? data) => new()
    {
        ["outcome"] = outcome, ["deliverable"] = deliverable, ["reply"] = reply ?? "", ["data"] = data?.DeepClone(),
    };

    /// <summary>
    /// The JSON a reply ends with: the last fenced block (<c>```json ... ```</c>), else the longest JSON object or array that runs
    /// to the end of the text. Null when the text does not end with JSON.
    /// </summary>
    public static JsonNode? ReplyJson(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var text = reply.TrimEnd();
        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            var open = text.LastIndexOf("```", text.Length - 4, StringComparison.Ordinal);
            if (open >= 0)
            {
                var inner = text[(open + 3)..^3];
                var nl = inner.IndexOf('\n');
                if (nl >= 0 && inner[..nl].Trim() is "" or "json" or "JSON") inner = inner[(nl + 1)..];
                if (TryParse(inner, out var fenced)) return fenced;
            }
            text = text[..^3].TrimEnd();
        }
        if (text.Length == 0 || text[^1] is not ('}' or ']')) return null;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('{' or '[')) continue;
            if (TryParse(text[i..], out var node)) return node;
        }
        return null;
    }

    private static bool TryParse(string text, out JsonNode? node)
    {
        node = null;
        try
        {
            node = JsonNode.Parse(text);
            return node is JsonObject or JsonArray;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>What the agent is asked to end its answer with, when the step names a reply schema.</summary>
    public static string ReplyInstruction(string schemaName, JsonObject schema) =>
        $"End your closing summary (your hand-back or deliverable_complete text) with one JSON object of the schema {schemaName}, "
        + "as the last thing in it, in a ```json block. The workflow reads it and checks it against this JSON Schema:\n"
        + "```json\n" + schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n```";
}
