using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yawble.Core.Policy;

namespace Yawble.Core.Concierge;

/// <summary>
/// Turns one Concierge turn's raw output into what the chat shows: the reply so far, the tools it used, and the final
/// answer.
/// <list type="bullet">
/// <item>Claude (stream-json): assistant text blocks and tool_use blocks as they come; the result event's text is the
///   final answer.</item>
/// <item>Grok (json): one summary object at the end; its "text" is the answer.</item>
/// <item>Codex, Copilot, Antigravity: plain text; the whole output (without terminal escapes) is the answer.</item>
/// </list>
/// </summary>
public sealed partial class ConciergeOutput(AgentCli cli)
{
    private readonly Lock _lock = new();
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _plain = new();
    private readonly List<string> _texts = [];
    private readonly List<ConciergeToolUse> _tools = [];
    private StringBuilder? _object; // Grok's summary object

    /// <summary>The CLI's own bookkeeping steps, not something the person needs to see (Claude's deferred-tool lookup).</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal) { "ToolSearch", "TodoWrite" };
    private string? _final;
    private string? _error;

    public void Write(ReadOnlyMemory<byte> data)
    {
        lock (_lock)
        {
            var chars = new char[_utf8.GetCharCount(data.Span, flush: false)];
            _utf8.GetChars(data.Span, chars, flush: false);
            foreach (var c in chars)
            {
                if (c == '\n')
                {
                    Line(_line.ToString().TrimEnd('\r'));
                    _line.Clear();
                }
                else if (_line.Length < 8 * 1024 * 1024)
                {
                    _line.Append(c);
                }
            }
        }
    }

    /// <summary>The reply as it stands (the final answer once there is one).</summary>
    public string Text
    {
        get
        {
            lock (_lock)
            {
                if (_final is not null) return _final;
                if (cli == AgentCli.Claude) return string.Join("\n\n", _texts);
                if (cli == AgentCli.Grok) return "";
                return Clean(_plain.ToString() + _line);
            }
        }
    }

    public IReadOnlyList<ConciergeToolUse> Tools
    {
        get { lock (_lock) return [.. _tools]; }
    }

    /// <summary>What the CLI itself reported as an error (Claude's is_error result, Grok's error), if anything.</summary>
    public string? Error
    {
        get { lock (_lock) return _error; }
    }

    /// <summary>Whether the CLI produced its final answer (Claude's result event, Grok's summary).</summary>
    public bool HasFinal
    {
        get { lock (_lock) return _final is not null; }
    }

    /// <summary>After the run: flushes a last line without a newline.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            if (_line.Length == 0) return;
            Line(_line.ToString().TrimEnd('\r'));
            _line.Clear();
        }
    }

    private void Line(string line)
    {
        switch (cli)
        {
            case AgentCli.Claude:
                ClaudeLine(line);
                break;
            case AgentCli.Grok:
                GrokLine(line);
                break;
            default:
                _plain.Append(line).Append('\n');
                break;
        }
    }

    private void ClaudeLine(string line)
    {
        if (!line.StartsWith('{')) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = Str(root, "type");
            if (type == "assistant" && root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    var kind = Str(block, "type");
                    if (kind == "text" && Str(block, "text") is { Length: > 0 } text) _texts.Add(text.Trim());
                    else if (kind == "tool_use" && Str(block, "name") is { } name && !Hidden.Contains(name))
                        _tools.Add(new ConciergeToolUse(ToolName(name), block.TryGetProperty("input", out var input) ? Detail(input) : null));
                }
            }
            else if (type == "result")
            {
                var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                var result = Str(root, "result");
                if (isError) _error = result ?? Str(root, "subtype") ?? "the agent reported an error";
                _final = (result ?? string.Join("\n\n", _texts)).Trim();
            }
        }
        catch (JsonException)
        {
        }
    }

    private void GrokLine(string line)
    {
        if (_object is null)
        {
            if (line == "{") _object = new StringBuilder("{\n");
            return;
        }
        _object.Append(line).Append('\n');
        if (line != "}") return;
        var json = _object.ToString();
        _object = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Str(root, "error") is { Length: > 0 } error) _error = error;
            if (Str(root, "text") is { } text) _final = text.Trim();
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>mcp__yawble-bus__team_status -> team_status; built-in tools keep their names.</summary>
    internal static string ToolName(string name)
    {
        if (!name.StartsWith("mcp__", StringComparison.Ordinal)) return name;
        var last = name.LastIndexOf("__", StringComparison.Ordinal);
        return last > 4 ? name[(last + 2)..] : name;
    }

    private static string? Detail(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        var parts = new List<string>();
        foreach (var p in input.EnumerateObject())
        {
            var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            parts.Add($"{p.Name}: {(v.Length > 80 ? v[..80] + "…" : v)}");
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Plain output without terminal escapes and carriage-return redraws.</summary>
    internal static string Clean(string s) => Ansi().Replace(s, "").Replace("\r", "").Trim();

    [GeneratedRegex(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex Ansi();
}
