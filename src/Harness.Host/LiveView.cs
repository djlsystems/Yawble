using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Watching a headless run as it happens, by following the transcript the agent CLI writes
/// for itself. The launch, the usage parser and the completion output are untouched: when a CLI
/// changes where or how it writes, only this view goes dark. Nothing here writes to the database
/// or the message log.
/// </summary>
public static class LiveView
{
    /// <summary>Claude Code's session jsonl, one JSON object per line.</summary>
    public const string ClaudeJsonl = LiveViewNames.ClaudeJsonl;

    /// <summary>Grok's <c>updates.jsonl</c>: one ACP <c>session/update</c> per line.</summary>
    public const string GrokUpdates = LiveViewNames.GrokUpdates;

    /// <summary>Copilot's <c>events.jsonl</c> under <c>session-state/&lt;id&gt;/</c>.</summary>
    public const string CopilotEvents = LiveViewNames.CopilotEvents;

    /// <summary>Codex's <c>rollout-*.jsonl</c> under <c>sessions/YYYY/MM/DD/</c>.</summary>
    public const string CodexRollout = LiveViewNames.CodexRollout;

    /// <summary>Every format something renders.</summary>
    public static IReadOnlyList<string> Formats => LiveViewNames.Formats;

    /// <summary><see cref="AgentLiveViewFind.CwdFrom"/>: the first folder's name, percent-decoded (Grok).</summary>
    public const string CwdFromFolderName = LiveViewNames.CwdFromFolderName;

    /// <summary><see cref="AgentLiveViewFind.CwdFrom"/>: <c>workspace.yaml</c>'s <c>cwd:</c> beside the file (Copilot).</summary>
    public const string CwdFromWorkspaceYaml = LiveViewNames.CwdFromWorkspaceYaml;

    /// <summary><see cref="AgentLiveViewFind.CwdFrom"/>: the first line's <c>cwd</c> (Codex).</summary>
    public const string CwdFromFirstLine = LiveViewNames.CwdFromFirstLine;

    public static IReadOnlyList<string> CwdRules => LiveViewNames.CwdRules;

    /// <summary>The display line's bound, in characters, the ellipsis included.</summary>
    public const int MaxLine = LiveViewNames.MaxLine;

    /// <summary>How long after launch the Host looks for a transcript it has to find.</summary>
    public static readonly TimeSpan FindFor = LiveViewNames.FindFor;

    /// <summary>
    /// The transcript path for one run, or null when <paramref name="view"/> is not one this Host
    /// will read: a path that is not under the agent's home, climbs out with <c>..</c>, or names a
    /// format nothing renders. Null too for a view found after launch (<see cref="AgentLiveView.Find"/>).
    /// </summary>
    public static string? Resolve(AgentLiveView view, string home, string workingDirectory, string sessionId)
    {
        if (Refusal(view) is not null || string.IsNullOrEmpty(home) || view.Path is not { } path) return null;

        var relative = path[2..]
            .Replace("{sessionId}", sessionId, StringComparison.Ordinal)
            .Replace("{workspaceDashed}", Dashed(workingDirectory), StringComparison.Ordinal)
            .Replace("{workspaceEncoded}", Encoded(workingDirectory), StringComparison.Ordinal);

        return Path.Combine(home, relative);
    }

    /// <summary>Why a catalog entry's live view is refused, or null when it is usable.</summary>
    public static string? Refusal(AgentLiveView view)
    {
        if (!Formats.Contains(view.Format, StringComparer.Ordinal))
        {
            return $"its live view format '{view.Format}' is not one this platform reads ({string.Join(", ", Formats)})";
        }

        if ((view.Path is null) == (view.Find is null))
        {
            return "its live view must name exactly one of path and find";
        }

        if (view.Path is { } path && !UnderHome(path))
        {
            return "its live view path must start with ~/ and may not contain ..";
        }

        if (view.Find is { } find)
        {
            if (!UnderHome(find.Folder))
            {
                return "its live view find folder must start with ~/ and may not contain ..";
            }

            if (string.IsNullOrWhiteSpace(find.Pattern) || find.Pattern.StartsWith('/')
                || find.Pattern.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                return "its live view find pattern must be relative, one name per level, with no . or ..";
            }

            if (!CwdRules.Contains(find.CwdFrom, StringComparer.Ordinal))
            {
                return $"its live view find cwdFrom '{find.CwdFrom}' is not one of {string.Join(", ", CwdRules)}";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a member on this preset can be watched here: it names a live view this Host
    /// accepts, and the Host can read the agent's files.
    /// </summary>
    public static bool Watchable(AgentLiveView? view, AgentLaunchUser? runAs) =>
        view is not null && Refusal(view) is null && LiveTranscriptReader.Refusal(runAs) is null;

    /// <summary>How Claude names a project folder: the working directory with every <c>/</c> and every <c>.</c> as <c>-</c>.</summary>
    public static string Dashed(string workingDirectory) => LiveViewNames.Dashed(workingDirectory);

    /// <summary>
    /// How Grok names a session's workspace folder: the working directory percent-encoded as one
    /// segment, <c>/</c> as <c>%2F</c> (as of Grok 1.0.41).
    /// </summary>
    public static string Encoded(string workingDirectory) => LiveViewNames.Encoded(workingDirectory);

    /// <summary>The time a transcript line carries, as the live and past-run routes send it: UTC, milliseconds, or empty.</summary>
    public static string When(DateTimeOffset? at) =>
        at is { } stamp ? stamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : "";

    private static bool UnderHome(string path) => LiveViewNames.UnderHome(path);
}

/// <summary>
/// The runs in flight right now, by member, and for each whether it can be watched. The runner
/// adds one when its child starts and removes it when the run ends; the live route only reads.
/// </summary>
public sealed class LiveRuns
{
    private readonly ConcurrentDictionary<ContainerId, LiveRun> _runs = new();

    /// <summary>
    /// Records a run that started. Dispose the result when the run ends. A run whose transcript is
    /// looked for after launch passes <paramref name="finding"/> true and a null
    /// <paramref name="transcript"/>, and the runner calls <see cref="LiveRun.Found"/> once it knows.
    /// </summary>
    public LiveRun Begin(ContainerId container, string? transcript, string? format, string reason, bool finding = false)
    {
        var run = new LiveRun(this, container, transcript, format, reason, finding);
        _runs[container] = run;
        return run;
    }

    public LiveRun? Find(ContainerId container) => _runs.GetValueOrDefault(container);

    internal void End(LiveRun run) => _runs.TryRemove(new KeyValuePair<ContainerId, LiveRun>(run.Container, run));
}

/// <summary>
/// One run in flight. <see cref="Transcript"/> is null when its preset has no live view, and then
/// <see cref="Reason"/> says so, or while a transcript found after launch is still being looked
/// for (<see cref="Finding"/>). <see cref="Ended"/> fires when the run ends.
/// </summary>
public sealed class LiveRun : IDisposable
{
    private readonly LiveRuns _owner;
    private readonly CancellationTokenSource _ended = new();
    private readonly TaskCompletionSource<string?> _located = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal LiveRun(LiveRuns owner, ContainerId container, string? transcript, string? format, string reason, bool finding)
    {
        _owner = owner;
        Container = container;
        Transcript = transcript;
        Format = format;
        Reason = reason;
        Finding = finding;
        if (!finding) _located.TrySetResult(transcript);
    }

    public ContainerId Container { get; }

    public string? Transcript { get; private set; }

    public string? Format { get; }

    public string Reason { get; private set; }

    /// <summary>True while the transcript is looked for after launch.</summary>
    public bool Finding { get; private set; }

    /// <summary>The transcript once it is known, or null when there is none; completes at once for a run that was not looking.</summary>
    public Task<string?> Located => _located.Task;

    public CancellationToken Ended => _ended.Token;

    /// <summary>The search is over: <paramref name="transcript"/> is the file, or null with <paramref name="reason"/>.</summary>
    public void Found(string? transcript, string reason = "")
    {
        Transcript = transcript;
        if (transcript is null) Reason = reason;
        Finding = false;
        _located.TrySetResult(transcript);
    }

    // The source is not disposed: a watcher may still hold the token after the run ends.
    // A run that ends while its transcript is still being looked for (the runner's exception path
    // disposes without a last look) says why a watcher gets none, never an empty reason.
    public void Dispose()
    {
        _owner.End(this);
        if (Finding && Transcript is null)
        {
            Reason = "The run ended before its agent's session transcript was found.";
            Finding = false;
        }

        _located.TrySetResult(Transcript);
        _ended.Cancel();
    }
}

/// <summary>
/// Follows a transcript from its first byte. The file is the agent's, mode 600, and stays that
/// way: when the Host switches users the reader is <c>tail -c +1 -F</c> started through the
/// same <c>setpriv</c> prefix as every agent child, and when it cannot switch it reads the file
/// itself, as it already can.
/// </summary>
public static class LiveTranscriptReader
{
    /// <summary>How long after the run ends the reader keeps collecting lines the agent wrote last.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    /// <summary>The command line that reads <paramref name="path"/> as the agent, or null when this Host reads it directly.</summary>
    public static IReadOnlyList<string>? ReaderCommand(AgentLaunchUser? runAs, string path) =>
        runAs is { Switches: true } && SystemCommand.Find("tail") is { } tail
            ? runAs.Wrap([tail, "-c", "+1", "-F", path])
            : null;

    /// <summary>Why this Host cannot follow a transcript at all, or null when it can.</summary>
    public static string? Refusal(AgentLaunchUser? runAs) =>
        runAs is { Switches: true } && SystemCommand.Find("tail") is null
            ? $"tail is not in a root-owned system directory ({string.Join(", ", SystemCommand.Directories)}), so the transcript cannot be read as the agent."
            : null;

    /// <summary>
    /// Each complete line of the transcript, from the start, then each new one, until
    /// <paramref name="runEnded"/> fires and what was written before it has been read, or
    /// <paramref name="ct"/> (the watcher leaving) fires.
    /// </summary>
    public static IAsyncEnumerable<string> LinesAsync(
        string path, AgentLaunchUser? runAs, CancellationToken runEnded, CancellationToken ct) =>
        ReaderCommand(runAs, path) is { } command
            ? FollowCommandAsync(command, runEnded, ct)
            : FollowDirectlyAsync(path, runEnded, ct);

    /// <summary>
    /// The lines <paramref name="command"/> prints (<see cref="ReaderCommand"/>'s tail), stopped
    /// when the watcher leaves or a little after the run ends. Public so the suite can drive the
    /// tail half where it cannot switch users.
    /// </summary>
    public static async IAsyncEnumerable<string> FollowCommandAsync(
        IReadOnlyList<string> command, CancellationToken runEnded, [EnumeratorCancellation] CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The transcript reader did not start.");

        // tail -F says "cannot open" until the agent creates the file. Drained, never shown.
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

        void Stop()
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { }
        }

        // The watcher leaving stops the reader at once; the run ending stops it once the agent's
        // last lines have had time to arrive. What is already in the pipe is still read below.
        using var left = ct.Register(Stop);
        using var ended = runEnded.Register(() => _ = Task.Delay(Settle, CancellationToken.None).ContinueWith(_ => Stop(), TaskScheduler.Default));

        try
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                ct.ThrowIfCancellationRequested();
                yield return line;
            }
        }
        finally
        {
            Stop();
        }
    }

    private static async IAsyncEnumerable<string> FollowDirectlyAsync(
        string path, CancellationToken runEnded, [EnumeratorCancellation] CancellationToken ct)
    {
        // The agent may not have created it yet.
        while (!File.Exists(path))
        {
            if (runEnded.IsCancellationRequested) yield break;
            await Task.Delay(Poll, ct);
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var pending = new List<byte>();
        var buffer = new byte[16 * 1024];

        while (true)
        {
            // Read before looking at the clock would lose the last lines: a run that ended is read
            // to its end once more, then stops.
            var last = runEnded.IsCancellationRequested;

            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                var from = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;

                    pending.AddRange(buffer.AsSpan(from, i - from));
                    yield return Encoding.UTF8.GetString([.. pending]);
                    pending.Clear();
                    from = i + 1;
                }

                pending.AddRange(buffer.AsSpan(from, read - from));
            }

            if (last)
            {
                if (pending.Count > 0) yield return Encoding.UTF8.GetString([.. pending]);
                yield break;
            }

            try { await Task.Delay(Poll, runEnded); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();
        }
    }
}

/// <summary>
/// One readable line per Claude transcript event. What a person sees is a line, not the
/// raw event: a tool result can be a whole file. A line that does not parse is shown raw, clipped,
/// never dropped. Bookkeeping records (queue operations, the last prompt, thinking) show nothing.
/// </summary>
public static class ClaudeTranscriptLines
{
    public static IReadOnlyList<string> Render(string raw)
    {
        raw = raw.TrimEnd('\r');
        if (string.IsNullOrWhiteSpace(raw)) return [];

        try
        {
            using var document = JsonDocument.Parse(raw);
            return Render(document.RootElement) ?? [Clip(raw)];
        }
        catch (JsonException)
        {
            return [Clip(raw)];
        }
    }

    /// <summary>
    /// When the event happened: its <c>timestamp</c>, which Claude writes on every event. Null when
    /// the line is not JSON, names no time, or names one that does not parse - the line still shows,
    /// with no time beside it.
    /// </summary>
    public static DateTimeOffset? Timestamp(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw.TrimEnd('\r'));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && Text(document.RootElement, "timestamp") is { } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Null when the event is not in a shape this knows, so the caller shows it raw.</summary>
    private static IReadOnlyList<string>? Render(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        return Text(root, "type") switch
        {
            "assistant" => Assistant(root),
            "user" => User(root),
            "attachment" => Attachment(root),
            null => null,
            _ => [],
        };
    }

    private static IReadOnlyList<string>? Assistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(content.GetString()) ? [] : [Clip(content.GetString()!)];
        }

        if (content.ValueKind != JsonValueKind.Array) return null;

        var lines = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            switch (Text(block, "type"))
            {
                case "text" when Text(block, "text") is { } text && !string.IsNullOrWhiteSpace(text):
                    lines.Add(Clip(text));
                    break;
                case "tool_use":
                    lines.Add(Clip(Tool(Text(block, "name") ?? "?", block.TryGetProperty("input", out var input) ? input : default)));
                    break;
            }
        }

        return lines;
    }

    private static string Tool(string name, JsonElement input)
    {
        if (name == "Read") return "Read " + (Text(input, "file_path") ?? Text(input, "path") ?? "");
        if (name == "Bash") return "Bash: " + (Text(input, "command") ?? "");

        // mcp__<server>__<tool>: the tool is what a person recognises.
        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return "MCP: " + name[(name.LastIndexOf("__", StringComparison.Ordinal) + 2)..];
        }

        var shortInput = ShortInput(input);
        return string.IsNullOrEmpty(shortInput) ? name : $"{name}: {shortInput}";
    }

    private static readonly string[] Preferred =
        ["file_path", "pattern", "path", "command", "query", "url", "skill", "description", "prompt"];

    internal static string ShortInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";

        foreach (var key in Preferred)
        {
            if (Text(input, key) is { Length: > 0 } value) return value;
        }

        foreach (var property in input.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        var json = input.GetRawText();
        return json == "{}" ? "" : json;
    }

    private static IReadOnlyList<string>? User(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String) return [Clip("User: " + FirstLine(content.GetString()!))];
        if (content.ValueKind != JsonValueKind.Array) return null;

        var lines = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            switch (Text(block, "type"))
            {
                case "tool_result":
                    lines.Add(Result(block.TryGetProperty("content", out var result) ? result : default));
                    break;
                case "text" when Text(block, "text") is { } text:
                    lines.Add(Clip("User: " + FirstLine(text)));
                    break;
            }
        }

        return lines;
    }

    /// <summary>The first line and the size, clipped so the size always shows.</summary>
    private static string Result(JsonElement content)
    {
        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
                .Where(b => Text(b, "type") == "text")
                .Select(b => Text(b, "text") ?? "")),
            _ => "",
        };

        return ResultLine(text);
    }

    /// <summary>A tool result as one line: its first line and its size, clipped so the size always shows.</summary>
    internal static string ResultLine(string text)
    {
        var first = FirstLine(text);
        var size = $" ({Encoding.UTF8.GetByteCount(text)} bytes)";

        return Clip(first.Length == 0 ? "(no text)" : first, LiveView.MaxLine - size.Length) + size;
    }

    private static IReadOnlyList<string>? Attachment(JsonElement root)
    {
        if (!root.TryGetProperty("attachment", out var attachment) || attachment.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = Text(attachment, "filename") ?? Text(attachment, "displayPath") ?? Text(attachment, "name");
        var first = name
            ?? new[] { "text", "content", "prompt" }.Select(key => Text(attachment, key))
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => FirstLine(value!)).FirstOrDefault()
            ?? Text(attachment, "type")
            ?? "";

        return [Clip("Attachment: " + first)];
    }

    internal static string? Text(JsonElement element, string property) => JsonFields.Text(element, property);

    internal static string FirstLine(string text) =>
        text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";

    /// <summary>One line, at most <paramref name="max"/> characters, ending with … when clipped.</summary>
    public static string Clip(string text, int max = LiveView.MaxLine)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (line.Length <= max) return line;

        var keep = max - 1;
        if (char.IsHighSurrogate(line[keep - 1])) keep--;
        return line[..keep] + "…";
    }
}
