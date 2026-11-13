using System.Globalization;
using System.Text.Json;

namespace Yawble.Core.Monitor;

/// <summary>One thing an agent did, as the Monitor draws it: its kind, the tool's name and what it acted on.</summary>
public readonly record struct ParsedStep(string Kind, string Tool, string? Target);

/// <summary>What a transcript says about itself as it is read: the clone its run worked in (init's cwd).</summary>
public sealed class TranscriptContext(string team, string member)
{
    public string Team { get; } = team;
    public string Member { get; } = member;
    public string? Cwd { get; set; }
}

/// <summary>
/// THE STEPS IN A TRANSCRIPT: each line of a headless run's output that says the agent did something, as a step.
/// Claude Code's stream-json is read fully: each tool_use in an assistant event is a step (Read is "read", Edit
/// "edit", Write "create", Bash and PowerShell "run", Grep and Glob "search", WebFetch "web", the bus's MCP tools
/// "bus"...), a thinking block is "think" and a text block "say". Codex's exec --json items are read as far as they
/// go (commands, file changes, MCP calls, web searches). Anything else, an interactive session's PTY stream among
/// them, is passed over: a line that is not a JSON object is never parsed.
///
/// A step carries none of the transcript's words but the target: a file's path (relative to the member's clone when
/// it is in it), a command's first line, a search's pattern, a URL, the member a tell went to; each clipped.
/// </summary>
public static class TranscriptSteps
{
    public const int TargetMax = 120;

    /// <summary>The steps of one line, and its own time when it carries one ("timestamp").</summary>
    public static IReadOnlyList<ParsedStep> Parse(string line, TranscriptContext context, out DateTimeOffset? at)
    {
        at = null;
        var text = line.AsSpan().Trim();
        if (text.Length < 2 || text[0] != '{') return [];
        // Cheap: most bytes of a transcript are tool results (the user events), which say nothing the Monitor draws.
        if (!text.Contains("\"assistant\"", StringComparison.Ordinal) && !text.Contains("\"init\"", StringComparison.Ordinal)
            && !text.Contains("\"item.", StringComparison.Ordinal)) return [];
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return []; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (Str(root, "timestamp") is { } stamp && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) at = t;
            var type = Str(root, "type");
            if (type == "system" && Str(root, "subtype") == "init")
            {
                if (Str(root, "cwd") is { } cwd) context.Cwd = cwd;
                return [];
            }
            if (type == "assistant" && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var steps = new List<ParsedStep>();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    switch (Str(block, "type"))
                    {
                        case "tool_use":
                            var input = block.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                            steps.Add(ClaudeTool(Str(block, "name") ?? "", input, context));
                            break;
                        case "thinking":
                        case "redacted_thinking":
                            steps.Add(new ParsedStep("think", "", null));
                            break;
                        case "text":
                            if (!string.IsNullOrWhiteSpace(Str(block, "text"))) steps.Add(new ParsedStep("say", "", null));
                            break;
                    }
                }
                return steps;
            }
            if (type == "item.completed" && root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                return CodexItem(item, context);
            return [];
        }
    }

    /// <summary>A Claude Code tool call as a step.</summary>
    internal static ParsedStep ClaudeTool(string name, JsonElement input, TranscriptContext context)
    {
        string? In(string field) => input.ValueKind == JsonValueKind.Object ? Str(input, field) : null;
        string? File(params string[] fields)
        {
            foreach (var f in fields) if (In(f) is { } p) return Relative(p, context);
            return null;
        }
        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = name.Split("__", 3);
            var server = parts.Length > 1 ? parts[1] : "";
            var tool = parts.Length > 2 ? parts[2] : "";
            if (server == "bus")
            {
                var to = In("member") ?? In("to");
                var card = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("card", out var c) && c.ValueKind == JsonValueKind.Number ? $"card {c}" : null;
                var target = string.Join(" · ", new[] { to, card ?? (tool == "board" ? In("action") : null) }.Where(s => !string.IsNullOrEmpty(s)));
                return new ParsedStep("bus", tool, target.Length > 0 ? Clip(target) : null);
            }
            return new ParsedStep("tool", $"{server}/{tool}", null);
        }
        return name switch
        {
            "Read" or "NotebookRead" => new ParsedStep("read", name, File("file_path", "notebook_path", "path")),
            "Edit" or "MultiEdit" or "NotebookEdit" => new ParsedStep("edit", name, File("file_path", "notebook_path")),
            "Write" => new ParsedStep("create", name, File("file_path")),
            "Bash" or "PowerShell" => new ParsedStep("run", name, Clip(FirstLine(In("command")))),
            "Grep" or "Glob" => new ParsedStep("search", name, Clip(In("pattern"))),
            "LS" => new ParsedStep("search", name, File("path")),
            "WebFetch" => new ParsedStep("web", name, Clip(In("url"))),
            "WebSearch" => new ParsedStep("web", name, Clip(In("query"))),
            "Task" or "Agent" => new ParsedStep("agent", name, Clip(In("description"))),
            "TodoWrite" => new ParsedStep("plan", name, null),
            "Skill" => new ParsedStep("skill", name, Clip(In("skill") ?? In("name"))),
            _ => new ParsedStep("tool", name, null),
        };
    }

    /// <summary>A Codex exec --json item as its steps: best effort, the shapes Codex has used.</summary>
    private static IReadOnlyList<ParsedStep> CodexItem(JsonElement item, TranscriptContext context)
    {
        switch (Str(item, "type") ?? Str(item, "item_type"))
        {
            case "command_execution":
                return [new ParsedStep("run", "shell", Clip(FirstLine(Unwrap(Str(item, "command")))))];
            case "file_change":
                if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) return [];
                var steps = new List<ParsedStep>();
                foreach (var change in changes.EnumerateArray())
                {
                    if (change.ValueKind != JsonValueKind.Object || Str(change, "path") is not { } path) continue;
                    // A deleted file is drawn as an edit: the Monitor has no "delete" mark.
                    var kind = Str(change, "kind") == "add" ? "create" : "edit";
                    steps.Add(new ParsedStep(kind, "apply_patch", Relative(path, context)));
                }
                return steps;
            case "mcp_tool_call":
                var server = Str(item, "server") ?? "";
                var tool = Str(item, "tool") ?? "";
                return [server == "bus" ? new ParsedStep("bus", tool, null) : new ParsedStep("tool", $"{server}/{tool}", null)];
            case "web_search":
                return [new ParsedStep("web", "web_search", Clip(Str(item, "query")))];
            case "reasoning":
                return [new ParsedStep("think", "", null)];
            case "agent_message":
                return [new ParsedStep("say", "", null)];
            case "todo_list":
                return [new ParsedStep("plan", "todo_list", null)];
            default:
                return [];
        }
    }

    /// <summary>
    /// A path as the person reads it: relative to the member's clone when it is in it, else relative to the team's
    /// folder (a team document: "documents/plan.md"), else its last two parts. Always with forward slashes.
    /// </summary>
    public static string Relative(string path, TranscriptContext context)
    {
        var p = path.Replace('\\', '/');
        if (context.Cwd is { Length: > 0 } cwd)
        {
            var root = cwd.Replace('\\', '/').TrimEnd('/') + "/";
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Clip(p[root.Length..])!;
            if (!IsRooted(p)) return Clip(p.TrimStart('.', '/'))!;
        }
        var team = $"/teams/{context.Team}/";
        var at = p.IndexOf(team, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var rest = p[(at + team.Length)..];
            // Another clone of the team: members/<name>/repo/<path>.
            var parts = rest.Split('/');
            if (parts.Length > 3 && parts[0].Equals("members", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("repo", StringComparison.OrdinalIgnoreCase))
                rest = string.Join('/', parts[3..]);
            return Clip(rest)!;
        }
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Clip(segments.Length <= 2 ? string.Join('/', segments) : string.Join('/', segments[^2..]))!;
    }

    private static bool IsRooted(string p) => p.StartsWith('/') || (p.Length > 2 && p[1] == ':');

    /// <summary>Codex wraps commands as ["bash", "-lc", "..."] or "bash -lc '...'": the command itself.</summary>
    private static string? Unwrap(string? command)
    {
        if (command is null) return null;
        foreach (var shell in new[] { "bash -lc ", "/bin/bash -lc ", "sh -c ", "pwsh -Command ", "powershell.exe -Command " })
            if (command.StartsWith(shell, StringComparison.Ordinal))
                return command[shell.Length..].Trim().Trim('\'', '"');
        return command;
    }

    private static string? FirstLine(string? s)
    {
        if (s is null) return null;
        foreach (var line in s.Split('\n'))
            if (line.Trim() is { Length: > 0 } t) return t;
        return null;
    }

    internal static string? Clip(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var one = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= TargetMax ? one : one[..(TargetMax - 1)] + "…";
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
