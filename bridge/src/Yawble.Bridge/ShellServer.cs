using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Yawble.Bridge;

/// <summary>
/// `yawble-bridge --shell`: a local stdio MCP server with one tool, run_shell, for agent CLIs whose own command tool
/// cannot work inside the sandbox. Antigravity runs every command through its exebox, which calls
/// CreateDesktop, and an app container may not create a desktop. This server runs inside the same sandbox as the
/// CLI and starts the shell as a plain child process (PowerShell on Windows, /bin/sh elsewhere), so it has exactly
/// the CLI's own rights: the sandbox stays the guardrail. AOT-safe: Utf8JsonWriter / JsonDocument only.
/// </summary>
public sealed class ShellServer(string workingDirectory)
{
    public const string ToolName = "run_shell";
    private const int MaxOutputChars = 60_000;
    private const int DefaultTimeoutSec = 600;

    public async Task RunAsync(TextReader input, TextWriter output, TextWriter log, CancellationToken ct)
    {
        string? line;
        while ((line = await input.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string? reply;
            try
            {
                reply = await HandleAsync(line, ct);
            }
            catch (JsonException e)
            {
                await log.WriteLineAsync("yawble-bridge --shell: unreadable message: " + e.Message);
                continue;
            }
            if (reply is null) continue;
            await output.WriteLineAsync(reply);
            await output.FlushAsync(ct);
        }
    }

    private async Task<string?> HandleAsync(string line, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idElement)) return null; // a notification
        var id = idElement.GetRawText();
        var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
        var p = root.TryGetProperty("params", out var pe) ? pe : default;
        switch (method)
        {
            case "initialize":
                var version = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var v) ? v.GetString() : null;
                return Reply(id, w =>
                {
                    w.WriteString("protocolVersion", version ?? "2025-06-18");
                    w.WriteStartObject("capabilities");
                    w.WriteStartObject("tools");
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteStartObject("serverInfo");
                    w.WriteString("name", "yawble-shell");
                    w.WriteString("version", "1");
                    w.WriteEndObject();
                });
            case "ping":
                return Reply(id, _ => { });
            case "tools/list":
                return Reply(id, WriteTools);
            case "tools/call":
                var name = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name != ToolName) return Error(id, -32602, $"unknown tool: {name}");
                var args = p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
                var command = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("command", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(command)) return Error(id, -32602, "run_shell needs a command");
                var cwd = args.TryGetProperty("cwd", out var d) && d.GetString() is { Length: > 0 } dir ? dir : workingDirectory;
                var timeout = args.TryGetProperty("timeout_sec", out var t) && t.TryGetInt32(out var s) && s > 0 ? Math.Min(s, 3600) : DefaultTimeoutSec;
                var (text, failed) = await RunCommandAsync(command, cwd, timeout, ct);
                return Reply(id, w =>
                {
                    w.WriteStartArray("content");
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("text", text);
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteBoolean("isError", failed);
                });
            default:
                return Error(id, -32601, $"method not found: {method}");
        }
    }

    private static void WriteTools(Utf8JsonWriter w)
    {
        var shell = OperatingSystem.IsWindows() ? "Windows PowerShell (powershell.exe -NoProfile -Command)" : "/bin/sh -c";
        w.WriteStartArray("tools");
        w.WriteStartObject();
        w.WriteString("name", ToolName);
        w.WriteString("description",
            $"Run a shell command in your working directory with {shell} and return its combined output and exit code. "
            + "Use this for every command (build, test, git, file listing). It has no input: never run an interactive program.");
        w.WriteStartObject("inputSchema");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        w.WriteStartObject("command");
        w.WriteString("type", "string");
        w.WriteString("description", "The command line to run.");
        w.WriteEndObject();
        w.WriteStartObject("cwd");
        w.WriteString("type", "string");
        w.WriteString("description", "Directory to run it in (default: your working directory).");
        w.WriteEndObject();
        w.WriteStartObject("timeout_sec");
        w.WriteString("type", "integer");
        w.WriteString("description", $"Stop the command after this many seconds (default {DefaultTimeoutSec}, at most 3600).");
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteStartArray("required");
        w.WriteStringValue("command");
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndArray();
    }

    private static async Task<(string Text, bool Failed)> RunCommandAsync(string command, string cwd, int timeoutSec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            // UTF-8 output so non-ASCII text survives the pipe.
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command })
                psi.ArgumentList.Add(arg);
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("the shell did not start");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or DirectoryNotFoundException)
        {
            return ($"could not start the shell: {e.Message}", true);
        }
        using (process)
        {
            process.StandardInput.Close();
            var text = new StringBuilder();
            var gate = new object();
            void Append(string? data)
            {
                if (data is null) return;
                lock (gate) text.Append(data).Append('\n');
            }
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                process.WaitForExit(); // drains the redirected streams
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (!timedOut) throw;
            }
            string body;
            lock (gate) body = text.ToString();
            if (body.Length > MaxOutputChars)
                body = body[..(MaxOutputChars / 2)] + $"\n... [{body.Length - MaxOutputChars} characters cut] ...\n" + body[^(MaxOutputChars / 2)..];
            if (timedOut) return (body + $"\n[timed out after {timeoutSec} s; the command was stopped]", true);
            return (body + $"\n[exit code {process.ExitCode}]", process.ExitCode != 0);
        }
    }

    private static string Reply(string rawId, Action<Utf8JsonWriter> result) => Message(rawId, w =>
    {
        w.WriteStartObject("result");
        result(w);
        w.WriteEndObject();
    });

    private static string Error(string rawId, int code, string message) => Message(rawId, w =>
    {
        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    private static string Message(string rawId, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            w.WriteRawValue(rawId);
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
