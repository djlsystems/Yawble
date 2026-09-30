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
    /// <summary>
    /// The tools <paramref name="text"/>, a whole transcript in <paramref name="format"/>, offered and
    /// called. <paramref name="beside"/> holds the files of <see cref="Beside"/> that could be read, by name.
    /// </summary>
    public static TranscriptToolUse Extract(string format, string text, IReadOnlyDictionary<string, string>? beside = null) => format switch
    {
        LiveView.ClaudeJsonl => ClaudeTranscriptTools.Extract(text),
        LiveView.GrokUpdates => GrokUpdateTools.Extract(text, beside ?? new Dictionary<string, string>()),
        LiveView.CopilotEvents => CopilotEventTools.Extract(text),
        LiveView.CodexRollout => CodexRolloutTools.Extract(text),
        _ => TranscriptToolUse.Unread(format),
    };

    /// <summary>The files a format keeps in its transcript's folder that say what was offered, by name.</summary>
    public static IReadOnlyList<string> Beside(string format) =>
        format == LiveView.GrokUpdates ? [GrokUpdateTools.ToolDefinitions, GrokUpdateTools.Events] : [];

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
/// Grok's <c>updates.jsonl</c> (1.0.44), WITH TWO FILES BESIDE IT, which between them write the whole
/// offer down: <c>tool_definitions.json</c> lists Grok's own tools (<c>[].function.name</c>), and the
/// session's <c>events.jsonl</c> the MCP servers - <c>mcp_config_resolved</c> the servers configured
/// (<c>servers[].name</c>, less <c>disabled</c>) and <c>mcp_server_connected</c> each one's
/// <c>tools</c>. A configured server that never connected is offered as a whole server. The offer is
/// complete only when both files were read and the MCP start-up finished (<c>mcp_init_completed</c>);
/// without them, the offer is only what a <c>search_tool</c> call returned. Calls are named: a
/// <c>tool_call</c> update is Grok's own tool, except <c>use_tool</c>, whose
/// <c>rawInput.tool_name</c> is the MCP tool as <c>&lt;server&gt;__&lt;tool&gt;</c>.
/// </summary>
public static class GrokUpdateTools
{
    public const string ToolDefinitions = "tool_definitions.json";
    public const string Events = "events.jsonl";

    public static TranscriptToolUse Extract(string text, IReadOnlyDictionary<string, string> beside)
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

            var meta = TranscriptTools.Property(update, "_meta");
            var own = TranscriptTools.Text(TranscriptTools.Property(meta, "x.ai/tool"), "name")
                ?? Backend(update, meta)
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

        var complete = Own(beside, offered) & Servers(beside, offered);

        return new TranscriptToolUse(offered, complete, called,
            complete
                ? "the tools offered (its tool definitions and the MCP servers it connected) and every tool called"
                : "the MCP tools a tool search found, and every tool called; its tool definitions or MCP events were not there to read");
    }

    /// <summary>
    /// One of xAI's server-side tools (web search): <c>_meta.backend</c> is true and there is no tool
    /// name, only <c>rawInput.variant</c> (<c>WebSearch</c>), named here as the preset lists it
    /// (<c>web_search</c>). Server-side tools are not in <c>tool_definitions.json</c>; they are xAI's
    /// own, not a server a person added.
    /// </summary>
    private static string? Backend(JsonElement update, JsonElement meta)
    {
        if (TranscriptTools.Property(meta, "backend").ValueKind != JsonValueKind.True
            || TranscriptTools.Text(TranscriptTools.Property(update, "rawInput"), "variant") is not { Length: > 0 } variant)
        {
            return null;
        }

        var name = new System.Text.StringBuilder();
        foreach (var c in variant)
        {
            if (char.IsUpper(c) && name.Length > 0) name.Append('_');
            name.Append(char.ToLowerInvariant(c));
        }

        return name.ToString();
    }

    /// <summary>Grok's own tools from <c>tool_definitions.json</c>; false when it is missing or not a list.</summary>
    private static bool Own(IReadOnlyDictionary<string, string> beside, HashSet<TranscriptTool> offered)
    {
        if (!beside.TryGetValue(ToolDefinitions, out var json)) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var definition in document.RootElement.EnumerateArray())
            {
                if (TranscriptTools.Text(TranscriptTools.Property(definition, "function"), "name") is { Length: > 0 } name)
                {
                    offered.Add(new TranscriptTool(null, name));
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The MCP servers and their tools from <c>events.jsonl</c>; false unless start-up finished.</summary>
    private static bool Servers(IReadOnlyDictionary<string, string> beside, HashSet<TranscriptTool> offered)
    {
        if (!beside.TryGetValue(Events, out var events)) return false;

        var configured = new List<string>();
        var connected = new HashSet<string>(StringComparer.Ordinal);
        var finished = false;

        foreach (var root in TranscriptTools.Objects(events))
        {
            switch (TranscriptTools.Text(root, "type"))
            {
                case "mcp_config_resolved":
                    var disabled = TranscriptTools.Strings(TranscriptTools.Property(root, "disabled")).ToHashSet(StringComparer.Ordinal);
                    if (TranscriptTools.Property(root, "servers") is { ValueKind: JsonValueKind.Array } servers)
                    {
                        configured.AddRange(servers.EnumerateArray()
                            .Select(server => TranscriptTools.Text(server, "name"))
                            .OfType<string>()
                            .Where(name => name.Length > 0 && !disabled.Contains(name)));
                    }

                    break;
                case "mcp_server_connected" when TranscriptTools.Text(root, "server_name") is { Length: > 0 } server:
                    connected.Add(server);
                    foreach (var tool in TranscriptTools.Strings(TranscriptTools.Property(root, "tools"))) offered.Add(new TranscriptTool(server, tool));
                    break;
                case "mcp_init_completed":
                    finished = true;
                    break;
            }
        }

        foreach (var server in configured.Where(server => !connected.Contains(server)))
        {
            offered.Add(new TranscriptTool(server, TranscriptTool.WholeServer));
        }

        return finished;
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
/// Copilot's <c>events.jsonl</c> (1.0.88). A HEADLESS RUN WRITES ITS WHOLE OFFER DOWN: each
/// <c>session.usage_checkpoint</c> lists the tools sent with the model's requests under
/// <c>data.promptCacheBreakState[].models.*.tools[].name</c> - its own tools by name, each MCP tool as
/// <c>&lt;server&gt;-&lt;tool&gt;</c> - and says whether that list was cut short
/// (<c>tools_truncated</c>, <c>tool_count</c>). The offer is complete when a checkpoint lists its
/// tools in full; an interactive session writes no checkpoint. The <c>system.message</c> also carries
/// a <c>&lt;&lt;server&gt;-*&gt;</c> section of instructions for each MCP server that has any, so those
/// servers are offered too. Calls are named: each <c>tool.execution_start</c> gives <c>toolName</c>,
/// and <c>mcpServerName</c> with <c>mcpToolName</c> for an MCP tool.
/// </summary>
public static partial class CopilotEventTools
{
    public static TranscriptToolUse Extract(string text)
    {
        var offered = new HashSet<TranscriptTool>();
        var called = new HashSet<TranscriptTool>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var servers = new HashSet<string>(StringComparer.Ordinal) { AgentIsolation.PlatformServer };
        var complete = false;
        var cut = false;

        foreach (var root in TranscriptTools.Objects(text))
        {
            var data = TranscriptTools.Property(root, "data");
            switch (TranscriptTools.Text(root, "type"))
            {
                case "system.message" when TranscriptTools.Text(data, "content") is { } content:
                    foreach (Match match in ServerSection().Matches(content))
                    {
                        servers.Add(match.Groups[1].Value);
                        offered.Add(new TranscriptTool(match.Groups[1].Value, TranscriptTool.WholeServer));
                    }

                    break;
                case "session.usage_checkpoint" when TranscriptTools.Property(data, "promptCacheBreakState") is { ValueKind: JsonValueKind.Array } states:
                    foreach (var state in states.EnumerateArray())
                    {
                        if (TranscriptTools.Property(state, "models") is not { ValueKind: JsonValueKind.Object } models) continue;

                        foreach (var model in models.EnumerateObject())
                        {
                            if (TranscriptTools.Property(model.Value, "tools") is not { ValueKind: JsonValueKind.Array } tools) continue;

                            var names = tools.EnumerateArray().Select(tool => TranscriptTools.Text(tool, "name")).OfType<string>().ToList();
                            listed.UnionWith(names);

                            var flag = TranscriptTools.Property(model.Value, "tools_truncated");
                            var truncated = flag.ValueKind == JsonValueKind.True
                                || (flag.ValueKind == JsonValueKind.Number && flag.GetDouble() != 0);
                            var count = TranscriptTools.Property(model.Value, "tool_count") is { ValueKind: JsonValueKind.Number } n
                                && n.TryGetInt32(out var c) ? c : names.Count;
                            if (truncated || count != names.Count) cut = true;
                            else complete = true;
                        }
                    }

                    break;
                case "tool.execution_start":
                    if (TranscriptTools.Text(data, "mcpServerName") is { Length: > 0 } server)
                    {
                        servers.Add(server);
                        called.Add(new TranscriptTool(server, TranscriptTools.Text(data, "mcpToolName") ?? TranscriptTools.Text(data, "toolName") ?? "?"));
                    }
                    else if (TranscriptTools.Text(data, "toolName") is { Length: > 0 } tool)
                    {
                        called.Add(new TranscriptTool(null, tool));
                    }

                    break;
            }
        }

        foreach (var name in listed) offered.Add(Named(name, servers));

        complete &= !cut;
        return new TranscriptToolUse(offered, complete, called,
            complete
                ? "the tools offered (its usage checkpoints) and every tool called"
                : "the MCP servers whose instructions are in the system message, and every tool called; "
                    + (cut ? "a usage checkpoint cut its tool list short" : "no usage checkpoint lists the tools offered"));
    }

    /// <summary>
    /// A checkpoint's tool name: Copilot's own tools have no <c>-</c>, an MCP tool is
    /// <c>&lt;server&gt;-&lt;tool&gt;</c>. A server name can hold <c>-</c> itself
    /// (<c>github-mcp-server</c>), so the longest server known from the run is matched first, and an
    /// unknown one is cut at the last <c>-</c>, since tool names use <c>_</c>.
    /// </summary>
    internal static TranscriptTool Named(string name, IReadOnlyCollection<string> servers)
    {
        if (!name.Contains('-')) return new TranscriptTool(null, name);

        var server = servers
            .Where(s => name.Length > s.Length + 1 && name.StartsWith(s + "-", StringComparison.Ordinal))
            .OrderByDescending(s => s.Length)
            .FirstOrDefault();
        if (server is not null) return new TranscriptTool(server, name[(server.Length + 1)..]);

        var at = name.LastIndexOf('-');
        return at <= 0 || at == name.Length - 1
            ? new TranscriptTool(null, name)
            : new TranscriptTool(name[..at], name[(at + 1)..]);
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
