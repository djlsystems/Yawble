using System.Text.Json;

namespace Harness.Host;

/// <summary>
/// One renderer per live view format, each turning one transcript line into the same
/// display lines <see cref="ClaudeTranscriptLines"/> does - <c>Read &lt;path&gt;</c>,
/// <c>Bash: &lt;command&gt;</c>, <c>MCP: &lt;tool&gt;</c>, <c>&lt;Tool&gt;: &lt;short input&gt;</c>,
/// the assistant's text, a tool result's first line and size, <c>User: &lt;first line&gt;</c> -
/// and reading the event's time. A line that does not parse, or is not in a shape its format
/// knows, is shown raw and clipped, never dropped; bookkeeping shows nothing. Each was written
/// against a real run's transcript, kept as its test fixture.
/// </summary>
public static class TranscriptLines
{
    /// <summary>The display lines for one raw transcript line in <paramref name="format"/>.</summary>
    public static IReadOnlyList<string> Render(string format, string raw) => format switch
    {
        LiveView.GrokUpdates => GrokUpdateLines.Render(raw),
        LiveView.CopilotEvents => CopilotEventLines.Render(raw),
        LiveView.CodexRollout => CodexRolloutLines.Render(raw),
        _ => ClaudeTranscriptLines.Render(raw),
    };

    /// <summary>When the event happened, or null when the line names no time this format reads.</summary>
    public static DateTimeOffset? Timestamp(string format, string raw) => format switch
    {
        LiveView.GrokUpdates => GrokUpdateLines.Timestamp(raw),
        _ => ClaudeTranscriptLines.Timestamp(raw),
    };

    /// <summary>Every display line of one raw line, as the routes send it: <c>&lt;when&gt;</c> TAB <c>&lt;line&gt;</c> NEWLINE.</summary>
    public static string Wire(string format, string raw)
    {
        var lines = Render(format, raw);
        if (lines.Count == 0) return "";

        var at = LiveView.When(Timestamp(format, raw));
        return string.Concat(lines.Select(line => $"{at}\t{line}\n"));
    }

    /// <summary>
    /// Parses <paramref name="raw"/> and hands its root object to <paramref name="render"/>; null
    /// from it, a line that is not a JSON object, or one that does not parse, is the raw line.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string raw, Func<JsonElement, IReadOnlyList<string>?> render)
    {
        raw = raw.TrimEnd('\r');
        if (string.IsNullOrWhiteSpace(raw)) return [];

        try
        {
            using var document = JsonDocument.Parse(raw);
            return (document.RootElement.ValueKind == JsonValueKind.Object ? render(document.RootElement) : null)
                ?? [ClaudeTranscriptLines.Clip(raw)];
        }
        catch (JsonException)
        {
            return [ClaudeTranscriptLines.Clip(raw)];
        }
    }

    internal static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>A tool line in the shape Claude's are: <c>&lt;name&gt;: &lt;short input&gt;</c>, or the name alone.</summary>
    internal static string Tool(string name, JsonElement input)
    {
        var shortInput = ClaudeTranscriptLines.ShortInput(input);
        return ClaudeTranscriptLines.Clip(string.IsNullOrEmpty(shortInput) ? name : $"{name}: {shortInput}");
    }

    internal static string Text(string text) => ClaudeTranscriptLines.Clip(text);

    internal static string User(string text) => ClaudeTranscriptLines.Clip("User: " + ClaudeTranscriptLines.FirstLine(text));

    /// <summary>The text of an array of content blocks, each with a string <c>text</c>, joined.</summary>
    internal static string? Blocks(JsonElement content) =>
        content.ValueKind == JsonValueKind.Array
            ? string.Join("\n", content.EnumerateArray()
                .Select(block => ClaudeTranscriptLines.Text(block, "text")
                    ?? ClaudeTranscriptLines.Text(Property(block, "content"), "text"))
                .Where(text => text is not null))
            : null;
}

/// <summary>
/// Grok's <c>updates.jsonl</c> (Grok 1.0.41): <c>{"timestamp":&lt;unix seconds&gt;,"method":"session/update",
/// "params":{"update":{"sessionUpdate":...}}}</c>. Followed rather than <c>events.jsonl</c>, which
/// names a tool but not its input, or <c>chat_history.jsonl</c>, which carries no time.
/// </summary>
public static class GrokUpdateLines
{
    public static IReadOnlyList<string> Render(string raw) => TranscriptLines.Parse(raw, Render);

    /// <summary>The line's <c>timestamp</c>, whole seconds since the epoch.</summary>
    public static DateTimeOffset? Timestamp(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw.TrimEnd('\r'));
            return TranscriptLines.Property(document.RootElement, "timestamp") is { ValueKind: JsonValueKind.Number } seconds
                && seconds.TryGetInt64(out var value)
                    ? DateTimeOffset.FromUnixTimeSeconds(value)
                    : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? Render(JsonElement root)
    {
        var update = TranscriptLines.Property(TranscriptLines.Property(root, "params"), "update");
        var content = TranscriptLines.Property(update, "content");

        return ClaudeTranscriptLines.Text(update, "sessionUpdate") switch
        {
            "user_message_chunk" when ClaudeTranscriptLines.Text(content, "text") is { } text => [TranscriptLines.User(text)],
            "agent_message_chunk" when ClaudeTranscriptLines.Text(content, "text") is { } text =>
                string.IsNullOrWhiteSpace(text) ? [] : [TranscriptLines.Text(text)],
            "tool_call" => [Tool(update)],
            "tool_call_update" => Result(update),
            "user_message_chunk" or "agent_message_chunk" or null => null,

            // Thinking, the plan, a tool call's title changing, the turn's usage: bookkeeping.
            _ => [],
        };
    }

    private static string Tool(JsonElement update)
    {
        var name = ClaudeTranscriptLines.Text(update, "title") ?? "?";
        var input = TranscriptLines.Property(update, "rawInput");

        return name switch
        {
            "read_file" => ClaudeTranscriptLines.Clip("Read " + (ClaudeTranscriptLines.Text(input, "target_file") ?? "")),
            "run_terminal_command" => ClaudeTranscriptLines.Clip("Bash: " + (ClaudeTranscriptLines.Text(input, "command") ?? "")),

            // `harness__status`: the tool is what a person recognises.
            "use_tool" when ClaudeTranscriptLines.Text(input, "tool_name") is { } tool =>
                ClaudeTranscriptLines.Clip("MCP: " + tool[(tool.LastIndexOf("__", StringComparison.Ordinal) is var at and >= 0 ? at + 2 : 0)..]),
            _ => TranscriptLines.Tool(name, input),
        };
    }

    /// <summary>Only a finished call says anything: its text, or what its raw output carries.</summary>
    private static IReadOnlyList<string> Result(JsonElement update)
    {
        if (ClaudeTranscriptLines.Text(update, "status") is not ("completed" or "failed")) return [];

        var text = TranscriptLines.Blocks(TranscriptLines.Property(update, "content")) is { Length: > 0 } blocks
            ? blocks
            : FirstString(TranscriptLines.Property(update, "rawOutput")) ?? "";

        return [ClaudeTranscriptLines.ResultLine(text)];
    }

    /// <summary>
    /// What a raw output says: its <c>output</c> when it has one (an MCP call's
    /// <c>output.OkayOutput</c>), else the first string in it that is not its <c>type</c> (a todo
    /// list's <c>TodosUpdated.summary_for_prompt</c>).
    /// </summary>
    private static string? FirstString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (element.TryGetProperty("output", out var output) && FirstString(output) is { } said) return said;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is "type" or "output") continue;
            if (FirstString(property.Value) is { } found) return found;
        }

        return null;
    }
}

/// <summary>
/// Copilot's <c>events.jsonl</c> (Copilot CLI 1.0.88): <c>{"type":"...","data":{...},"timestamp":"..."}</c>.
/// A tool call is shown from <c>tool.execution_start</c>, which names an MCP tool by its own name;
/// the assistant message that requested it carries only its text here.
/// </summary>
public static class CopilotEventLines
{
    public static IReadOnlyList<string> Render(string raw) => TranscriptLines.Parse(raw, Render);

    private static IReadOnlyList<string>? Render(JsonElement root)
    {
        var data = TranscriptLines.Property(root, "data");

        return ClaudeTranscriptLines.Text(root, "type") switch
        {
            "user.message" when ClaudeTranscriptLines.Text(data, "content") is { } text => [TranscriptLines.User(text)],
            "assistant.message" when ClaudeTranscriptLines.Text(data, "content") is { } text =>
                string.IsNullOrWhiteSpace(text) ? [] : [TranscriptLines.Text(text)],
            "tool.execution_start" when ClaudeTranscriptLines.Text(data, "toolName") is { } name => [Tool(name, data)],
            "tool.execution_complete" => Result(data),
            "session.error" when ClaudeTranscriptLines.Text(data, "message") is { } message =>
                [ClaudeTranscriptLines.Clip("Error: " + ClaudeTranscriptLines.FirstLine(message))],
            "user.message" or "assistant.message" or "tool.execution_start" or "session.error" or null => null,
            _ => [],
        };
    }

    private static string Tool(string name, JsonElement data)
    {
        var input = TranscriptLines.Property(data, "arguments");

        if (ClaudeTranscriptLines.Text(data, "mcpServerName") is not null)
        {
            return ClaudeTranscriptLines.Clip("MCP: " + (ClaudeTranscriptLines.Text(data, "mcpToolName") ?? name));
        }

        return name switch
        {
            "view" => ClaudeTranscriptLines.Clip("Read " + (ClaudeTranscriptLines.Text(input, "path") ?? "")),
            "bash" => ClaudeTranscriptLines.Clip("Bash: " + (ClaudeTranscriptLines.Text(input, "command") ?? "")),
            _ => TranscriptLines.Tool(name, input),
        };
    }

    private static IReadOnlyList<string>? Result(JsonElement data) =>
        ClaudeTranscriptLines.Text(TranscriptLines.Property(data, "result"), "content") is { } text
            ? [ClaudeTranscriptLines.ResultLine(text)]
            : null;
}

/// <summary>
/// Codex's rollout (codex-cli 0.157.0): <c>{"timestamp":"...","type":"...","payload":{...}}</c>. The
/// steps a person reads are the <c>event_msg</c> <c>item_completed</c> items; the
/// <c>response_item</c> rows repeat them as the model saw them, and the rest is bookkeeping.
/// </summary>
public static class CodexRolloutLines
{
    public static IReadOnlyList<string> Render(string raw) => TranscriptLines.Parse(raw, Render);

    private static IReadOnlyList<string>? Render(JsonElement root)
    {
        var payload = TranscriptLines.Property(root, "payload");

        return ClaudeTranscriptLines.Text(root, "type") switch
        {
            "event_msg" when ClaudeTranscriptLines.Text(payload, "type") == "item_completed" =>
                Item(TranscriptLines.Property(payload, "item")),
            null => null,
            _ => [],
        };
    }

    private static IReadOnlyList<string>? Item(JsonElement item)
    {
        switch (ClaudeTranscriptLines.Text(item, "type"))
        {
            case "UserMessage" when TranscriptLines.Blocks(TranscriptLines.Property(item, "content")) is { } text:
                return [TranscriptLines.User(text)];

            case "AgentMessage" when TranscriptLines.Blocks(TranscriptLines.Property(item, "content")) is { } text:
                return string.IsNullOrWhiteSpace(text) ? [] : [TranscriptLines.Text(text)];

            case "CommandExecution":
                return [Command(item), ClaudeTranscriptLines.ResultLine(ClaudeTranscriptLines.Text(item, "aggregated_output") ?? "")];

            case "McpToolCall" when ClaudeTranscriptLines.Text(item, "tool") is { } tool:
                return
                [
                    ClaudeTranscriptLines.Clip("MCP: " + tool),
                    ClaudeTranscriptLines.ResultLine(
                        TranscriptLines.Blocks(TranscriptLines.Property(TranscriptLines.Property(item, "result"), "content")) ?? ""),
                ];

            case null:
                return null;

            // An item this was not measured on still says that something happened.
            case { } other when other is not ("UserMessage" or "AgentMessage" or "McpToolCall"):
                return [ClaudeTranscriptLines.Clip(other)];

            default:
                return null;
        }
    }

    /// <summary><c>Read &lt;path&gt;</c> when Codex parsed the command as one read, else <c>Bash: &lt;command&gt;</c>.</summary>
    private static string Command(JsonElement item)
    {
        var parsed = TranscriptLines.Property(item, "parsed_cmd");
        if (parsed.ValueKind == JsonValueKind.Array && parsed.GetArrayLength() == 1
            && ClaudeTranscriptLines.Text(parsed[0], "type") == "read"
            && ClaudeTranscriptLines.Text(parsed[0], "path") is { } path)
        {
            return ClaudeTranscriptLines.Clip("Read " + path);
        }

        // ["/bin/bash", "-lc", "<command>"]: the command is the last element.
        var command = TranscriptLines.Property(item, "command");
        var text = command.ValueKind == JsonValueKind.Array && command.GetArrayLength() > 0
            && command[command.GetArrayLength() - 1].ValueKind == JsonValueKind.String
                ? command[command.GetArrayLength() - 1].GetString()
                : ClaudeTranscriptLines.Text(item, "command");

        return ClaudeTranscriptLines.Clip("Bash: " + (text ?? ""));
    }
}
