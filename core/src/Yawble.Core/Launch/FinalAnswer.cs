using System.Text;
using System.Text.Json;
using Yawble.Core.Policy;

namespace Yawble.Core.Launch;

/// <summary>
/// Watches a headless run's output for the CLI's FINAL answer: the point after which the agent does nothing more, even
/// if its process has not exited yet. Claude Code can print its result, then wait for a background shell command (a
/// hung build) and keep the run "running" for its whole 2 h limit. Once the answer is out,
/// <see cref="RunSupervisor"/> gives the CLI <see cref="SupervisorOptions.AfterAnswer"/> to exit on its own, then ends
/// the sandbox and the run counts as completed.
/// <list type="bullet">
/// <item>Claude (-p --output-format stream-json): a line that is a JSON object with "type":"result".</item>
/// <item>Grok (-p --output-format json): its one summary object, printed last, from a line "{" to a line "}", carrying
///   "num_turns" or "usage".</item>
/// <item>Codex, Copilot and Antigravity print plain text headless: no reliable marker, so no detection (their runs end
///   when the CLI exits, as before).</item>
/// </list>
/// </summary>
public sealed class FinalAnswer
{
    private readonly AgentCli _cli;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly StringBuilder _line = new();
    private StringBuilder? _object; // Grok: the summary object being collected
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private DateTimeOffset? _at;

    public FinalAnswer(AgentCli cli, TimeProvider? clock = null)
    {
        _cli = cli;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Whether this CLI's headless output has a final-answer marker this class knows.</summary>
    public static bool Detects(AgentCli cli) => cli is AgentCli.Claude or AgentCli.Grok;

    /// <summary>When the final answer was seen; null until then.</summary>
    public DateTimeOffset? At
    {
        get { lock (_lock) return _at; }
    }

    public void Write(ReadOnlyMemory<byte> data)
    {
        lock (_lock)
        {
            if (_at is not null) return;
            var chars = new char[_utf8.GetCharCount(data.Span, flush: false)];
            _utf8.GetChars(data.Span, chars, flush: false);
            foreach (var c in chars)
            {
                if (c == '\n')
                {
                    Line(_line.ToString().TrimEnd('\r'));
                    _line.Clear();
                    if (_at is not null) return;
                }
                else if (_line.Length < 4 * 1024 * 1024)
                {
                    _line.Append(c);
                }
            }
        }
    }

    private void Line(string line)
    {
        if (_cli == AgentCli.Claude)
        {
            if (line.StartsWith('{') && IsObjectWith(line, root => root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "result"))
                _at = _clock.GetUtcNow();
            return;
        }
        if (_cli != AgentCli.Grok) return;
        if (_object is null)
        {
            if (line == "{") _object = new StringBuilder("{\n");
            return;
        }
        _object.Append(line).Append('\n');
        if (line != "}") return;
        var text = _object.ToString();
        _object = null;
        if (IsObjectWith(text, root => root.TryGetProperty("num_turns", out _) || root.TryGetProperty("usage", out _)))
            _at = _clock.GetUtcNow();
    }

    private static bool IsObjectWith(string json, Func<JsonElement, bool> test)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && test(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
