using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// One tool as a transcript names it. <see cref="Server"/> is the MCP server it comes from, or null
/// for the CLI's own tool. <see cref="Name"/> is <see cref="WholeServer"/> when the transcript names
/// the server but not its tools: one still being authorised, or one whose instructions are all the
/// transcript carries.
/// </summary>
public sealed record TranscriptTool(string? Server, string Name)
{
    public const string WholeServer = "*";

    /// <summary>As a person reads it: <c>server/tool</c>, <c>server</c> for a whole server, or the tool alone.</summary>
    public override string ToString() =>
        Server is null ? Name : Name == WholeServer ? Server : $"{Server}/{Name}";
}

/// <summary>
/// What one run's transcript says about tools: the ones the agent was offered and the ones it called.
/// <see cref="OfferedComplete"/> is true only when the format writes the whole offer down; when it
/// is false, <see cref="Offered"/> holds what the transcript happens to name and nothing more, so an
/// absence there proves nothing. <see cref="Measured"/> says, in words, what the format records.
/// </summary>
public sealed record TranscriptToolUse(
    IReadOnlyCollection<TranscriptTool> Offered,
    bool OfferedComplete,
    IReadOnlyCollection<TranscriptTool> Called,
    string Measured)
{
    /// <summary>A format nothing here reads: no offer, no calls, measured by nothing.</summary>
    public static TranscriptToolUse Unread(string format) =>
        new([], false, [], $"nothing: '{format}' is not a format whose tools this platform reads");
}

/// <summary>
/// One extractor per live view format, beside <see cref="TranscriptLines"/>: from a run's whole
/// recorded transcript, the tools and MCP servers it was offered and the ones it called. Each was
/// written against real transcripts of the CLI version installed here, kept as test fixtures.
/// A line that does not parse is skipped: this reads, it never shows.
/// </summary>
public static class TranscriptTools
{
    /// <summary>The tools <paramref name="text"/>, a whole transcript in <paramref name="format"/>, offered and called.</summary>
    public static TranscriptToolUse Extract(string format, string text) => format switch
    {
        LiveView.ClaudeJsonl => ClaudeTranscriptTools.Extract(text),
        LiveView.GrokUpdates => GrokUpdateTools.Extract(text),
        LiveView.CopilotEvents => CopilotEventTools.Extract(text),
        LiveView.CodexRollout => CodexRolloutTools.Extract(text),
        _ => TranscriptToolUse.Unread(format),
    };

    /// <summary><c>&lt;server&gt;&lt;separator&gt;&lt;tool&gt;</c> split at the first separator, or null when it has none.</summary>
    internal static TranscriptTool? Split(string name, string separator)
    {
        var at = name.IndexOf(separator, StringComparison.Ordinal);
        return at <= 0 || at + separator.Length >= name.Length
            ? null
            : new TranscriptTool(name[..at], name[(at + separator.Length)..]);
    }

    /// <summary>Each line of <paramref name="text"/> that is a JSON object, parsed; the rest skipped.</summary>
    internal static IEnumerable<JsonElement> Objects(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (root.ValueKind == JsonValueKind.Object) yield return root;
        }
    }

    internal static IEnumerable<string> Strings(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : [];

    internal static string? Text(JsonElement element, string property) => ClaudeTranscriptLines.Text(element, property);

    internal static JsonElement Property(JsonElement element, string name) => TranscriptLines.Property(element, name);
}

/// <summary>
/// Claude Code's session jsonl (2.1.285). THE WHOLE OFFER IS WRITTEN DOWN: a <c>prompt_snapshot</c>
/// attachment lists the tools sent with every request (<c>tools[].name</c>), and each
/// <c>deferred_tools_delta</c> attachment the tools loaded on demand (<c>addedNames</c>, less
/// <c>removedNames</c>) - every MCP tool among them as <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> - and
/// the servers still waiting to be authorised or connected, which are offered too
/// (<c>needsAuthMcpServers</c>, <c>pendingMcpServers</c>). A call is an assistant <c>tool_use</c>.
/// A transcript with neither attachment names no offer and is not complete.
/// </summary>
public static class ClaudeTranscriptTools
{
    public static TranscriptToolUse Extract(string text)
    {
        var offered = new HashSet<TranscriptTool>();
        var called = new HashSet<TranscriptTool>();
        var listed = false;

        foreach (var root in TranscriptTools.Objects(text))
        {
            switch (TranscriptTools.Text(root, "type"))
            {
                case "attachment":
                    var attachment = TranscriptTools.Property(root, "attachment");
                    switch (TranscriptTools.Text(attachment, "type"))
                    {
                        case "prompt_snapshot" when TranscriptTools.Property(attachment, "tools") is { ValueKind: JsonValueKind.Array } tools:
                            listed = true;
                            foreach (var tool in tools.EnumerateArray())
                            {
                                if (TranscriptTools.Text(tool, "name") is { Length: > 0 } name) offered.Add(Named(name));
                            }

                            break;
                        case "deferred_tools_delta":
                            listed = true;
                            foreach (var name in TranscriptTools.Strings(TranscriptTools.Property(attachment, "addedNames"))) offered.Add(Named(name));
                            foreach (var name in TranscriptTools.Strings(TranscriptTools.Property(attachment, "removedNames"))) offered.Remove(Named(name));
                            foreach (var key in new[] { "needsAuthMcpServers", "pendingMcpServers" })
                            {
                                foreach (var server in TranscriptTools.Strings(TranscriptTools.Property(attachment, key)))
                                {
                                    offered.Add(new TranscriptTool(server, TranscriptTool.WholeServer));
                                }
                            }

                            break;
                    }

                    break;
                case "assistant" when TranscriptTools.Property(TranscriptTools.Property(root, "message"), "content") is { ValueKind: JsonValueKind.Array } content:
                    foreach (var block in content.EnumerateArray())
                    {
                        if (TranscriptTools.Text(block, "type") == "tool_use" && TranscriptTools.Text(block, "name") is { Length: > 0 } name)
                        {
                            called.Add(Named(name));
                        }
                    }

                    break;
            }
        }

        return new TranscriptToolUse(offered, listed, called,
            listed
                ? "the tools offered (prompt snapshot and deferred tools) and every tool called"
                : "every tool called; this transcript lists no offer");
    }

    /// <summary><c>mcp__&lt;server&gt;__&lt;tool&gt;</c> as its server and tool; anything else is Claude's own.</summary>
    internal static TranscriptTool Named(string name) =>
        name.StartsWith("mcp__", StringComparison.Ordinal) && TranscriptTools.Split(name[5..], "__") is { } mcp
            ? mcp
            : new TranscriptTool(null, name);
}

/// <summary>
/// Grok's <c>updates.jsonl</c> (1.0.44). IT NAMES THE OFFER ONLY IN PART: the tool definitions are in
/// a file beside it and MCP tools are found at run time with <c>search_tool</c>, so the offer here is
/// only what a search returned. Calls are named: a <c>tool_call</c> update is Grok's own tool, except
/// <c>use_tool</c>, whose <c>rawInput.tool_name</c> is the MCP tool as <c>&lt;server&gt;__&lt;tool&gt;</c>.
/// </summary>
public static class GrokUpdateTools
{
    public static TranscriptToolUse Extract(string text)
    {
        var offered = new HashSet<TranscriptTool>();
        var called = new HashSet<TranscriptTool>();

        foreach (var root in TranscriptTools.Objects(text))
        {
            var update = TranscriptTools.Property(TranscriptTools.Property(root, "params"), "update");
            if (TranscriptTools.Text(update, "sessionUpdate") == "tool_call_update")
            {
                Found(update, offered);
                continue;
            }

            if (TranscriptTools.Text(update, "sessionUpdate") != "tool_call") continue;

            var own = TranscriptTools.Text(TranscriptTools.Property(TranscriptTools.Property(update, "_meta"), "x.ai/tool"), "name")
                ?? TranscriptTools.Text(update, "title");
            if (own is not { Length: > 0 }) continue;

            if (own == "use_tool"
                && TranscriptTools.Text(TranscriptTools.Property(update, "rawInput"), "tool_name") is { Length: > 0 } name)
            {
                called.Add(TranscriptTools.Split(name, "__") ?? new TranscriptTool(null, name));
            }
            else
            {
                called.Add(new TranscriptTool(null, own));
            }
        }

        return new TranscriptToolUse(offered, false, called,
            "the MCP tools a tool search found, and every tool called; not every tool offered");
    }

    /// <summary>
    /// A finished <c>search_tool</c> call's result: <c>{"results":[{"server":..,"tools":[{"tool_name":..}]}]}</c>
    /// in its content text. Each tool it names was on offer.
    /// </summary>
    private static void Found(JsonElement update, HashSet<TranscriptTool> offered)
    {
        if (TranscriptTools.Property(update, "content") is not { ValueKind: JsonValueKind.Array } content) return;

        foreach (var block in content.EnumerateArray())
        {
            if (TranscriptTools.Text(TranscriptTools.Property(block, "content"), "text") is not { } text
                || !text.TrimStart().StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                if (TranscriptTools.Property(document.RootElement, "results") is not { ValueKind: JsonValueKind.Array } results) continue;

                foreach (var result in results.EnumerateArray())
                {
                    var server = TranscriptTools.Text(result, "server");
                    if (TranscriptTools.Property(result, "tools") is not { ValueKind: JsonValueKind.Array } tools) continue;

                    foreach (var tool in tools.EnumerateArray())
                    {
                        if (TranscriptTools.Text(tool, "tool_name") is not { Length: > 0 } name) continue;

                        offered.Add(TranscriptTools.Split(name, "__") is { } split
                            ? split
                            : new TranscriptTool(server, name));
                    }
                }
            }
            catch (JsonException)
            {
            }
        }
    }
}

/// <summary>
/// Copilot's <c>events.jsonl</c> (1.0.88). THE OFFER IS NAMED ONLY IN PART: the <c>system.message</c>
/// carries a <c>&lt;&lt;server&gt;-*&gt;</c> section of instructions for each MCP server that has any
/// (the built-in <c>github-mcp-server</c> does), so those servers are offered; a server with no
/// instructions, and every tool by name, is not written down. Calls are named: each
/// <c>tool.execution_start</c> gives <c>toolName</c>, and <c>mcpServerName</c> with
/// <c>mcpToolName</c> for an MCP tool.
/// </summary>
public static partial class CopilotEventTools
{
    public static TranscriptToolUse Extract(string text)
    {
        var offered = new HashSet<TranscriptTool>();
        var called = new HashSet<TranscriptTool>();

        foreach (var root in TranscriptTools.Objects(text))
        {
            var data = TranscriptTools.Property(root, "data");
            switch (TranscriptTools.Text(root, "type"))
            {
                case "system.message" when TranscriptTools.Text(data, "content") is { } content:
                    foreach (Match match in ServerSection().Matches(content))
                    {
                        offered.Add(new TranscriptTool(match.Groups[1].Value, TranscriptTool.WholeServer));
                    }

                    break;
                case "tool.execution_start":
                    if (TranscriptTools.Text(data, "mcpServerName") is { Length: > 0 } server)
                    {
                        called.Add(new TranscriptTool(server, TranscriptTools.Text(data, "mcpToolName") ?? TranscriptTools.Text(data, "toolName") ?? "?"));
                    }
                    else if (TranscriptTools.Text(data, "toolName") is { Length: > 0 } tool)
                    {
                        called.Add(new TranscriptTool(null, tool));
                    }

                    break;
            }
        }

        return new TranscriptToolUse(offered, false, called,
            "the MCP servers whose instructions are in the system message, and every tool called; not every tool offered");
    }

    /// <summary>The opening tag of one server's instructions, as Copilot 1.0.88 writes it: <c>&lt;github-mcp-server-*&gt;</c>.</summary>
    [GeneratedRegex(@"^<([A-Za-z0-9][A-Za-z0-9_.-]*)-\*>$", RegexOptions.Multiline)]
    private static partial Regex ServerSection();
}

/// <summary>
/// Codex's <c>rollout-*.jsonl</c> (0.157.0). IT NAMES NO OFFER: neither the session meta, the turn
/// context nor the world state lists tools, and a model can reach any of them from inside its
/// <c>exec</c> script, so what Codex was offered is not measured here. Calls are named: an
/// <c>McpToolCall</c> item (<c>server</c>, <c>tool</c>) for each MCP call, and a
/// <c>function_call</c> or <c>custom_tool_call</c> response item for each of Codex's own.
/// </summary>
public static class CodexRolloutTools
{
    public static TranscriptToolUse Extract(string text)
    {
        var called = new HashSet<TranscriptTool>();

        foreach (var root in TranscriptTools.Objects(text))
        {
            var payload = TranscriptTools.Property(root, "payload");
            switch (TranscriptTools.Text(payload, "type"))
            {
                case "item_completed" or "item_started":
                    var item = TranscriptTools.Property(payload, "item");
                    if (TranscriptTools.Text(item, "type") == "McpToolCall"
                        && TranscriptTools.Text(item, "server") is { Length: > 0 } server)
                    {
                        called.Add(new TranscriptTool(server, TranscriptTools.Text(item, "tool") ?? "?"));
                    }

                    break;
                case "function_call" or "custom_tool_call" when TranscriptTools.Text(payload, "name") is { Length: > 0 } name:
                    called.Add(new TranscriptTool(null, name));
                    break;
            }
        }

        return new TranscriptToolUse([], false, called, "every tool called; Codex's transcript lists no offer");
    }
}
