using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Yawble.Core.Integrations;

public sealed record CliOutput(int ExitCode, string Stdout, string Stderr, long Bytes, TimeSpan Duration, bool TimedOut, bool Truncated);

/// <summary>
/// Runs a person's CLI on this computer, outside any sandbox, for an agent: never through a shell (the arguments are
/// passed as they are; a .cmd/.bat, which Windows can only start through cmd.exe, is refused any argument cmd.exe would
/// read as syntax), stdin closed, no prompt, a time limit, output capped, and anything shaped like a token redacted
/// before it reaches the agent or the log.
/// </summary>
public static partial class CliRunner
{
    public const int MaxOutputChars = 64 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}|(?i:bearer)\s+[A-Za-z0-9._~+/=-]{16,})")]
    private static partial Regex Secrets();

    [GeneratedRegex(@"[&|<>^%!""\r\n]")]
    private static partial Regex CmdSyntax();

    public static string Redact(string text) => Secrets().Replace(Yawble.Core.Connections.SecretScrubber.Scrub(text), "[redacted]");

    /// <summary>The executable for a name on PATH (with the PATHEXT extensions on Windows), or null.</summary>
    public static string? Find(string name)
    {
        if (Path.IsPathFullyQualified(name)) return File.Exists(name) ? name : null;
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name + ext.ToLowerInvariant());
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    /// <summary>
    /// The Azure CLI on Windows, run without its az.cmd: the batch file starts the bundled Python isolated (-I), which
    /// ignores PYTHONIOENCODING/PYTHONUTF8, so its output is the console code page (cp1252) and a character outside it
    /// (npm's ✓ in a streamed `acr build` log) crashed az after the build had succeeded. Python's own UTF-8 mode
    /// (-X utf8) is a flag, which -I does not ignore. Also avoids cmd.exe's argument quoting. Null: run the path as is.
    /// </summary>
    public static (string Path, IReadOnlyList<string> Args)? AzureCliDirect(string path, IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows() || !Path.GetFileName(path).Equals("az.cmd", StringComparison.OrdinalIgnoreCase)) return null;
        var python = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path) ?? "", "..", "python.exe"));
        return File.Exists(python) ? (python, ["-X", "utf8", "-IBm", "azure.cli", .. args]) : null;
    }

    /// <summary>
    /// The start info for a program, never through a shell: a .cmd/.bat (which Windows can only start through cmd.exe) is
    /// refused any argument cmd.exe reads as syntax (<see cref="ArgumentException"/>). Streams are redirected, stdin is
    /// for the caller to close, no window; the environment is the caller's to set.
    /// </summary>
    public static ProcessStartInfo Prepare(string path, IReadOnlyList<string> args)
    {
        if (AzureCliDirect(path, args) is var (python, pythonArgs)) (path, args) = (python, pythonArgs);
        var batch = path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo psi;
        if (batch)
        {
            if (args.Any(a => CmdSyntax().IsMatch(a)))
                throw new ArgumentException("an argument holds a character the command shell would read as syntax (& | < > ^ % ! \" or a line break); leave it out");
            psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            var line = string.Join(' ', new[] { Quote(path) }.Concat(args.Select(Quote)));
            psi.Arguments = $"/d /s /c \"{line}\"";
        }
        else
        {
            psi = new ProcessStartInfo(path);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        return psi;
    }

    public static async Task<CliOutput> RunAsync(string path, IReadOnlyList<string> args, string? cwd, IReadOnlyDictionary<string, string>? env,
        TimeSpan? timeout, CancellationToken ct)
    {
        var psi = Prepare(path, args);
        if (cwd is not null && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["CI"] = "1";
        psi.Environment["TERM"] = "dumb";
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;

        var watch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        long bytes = 0;
        var truncated = false;
        void Take(StringBuilder into, string? line)
        {
            if (line is null) return;
            Interlocked.Add(ref bytes, line.Length + 1);
            lock (into)
            {
                if (into.Length < MaxOutputChars) into.AppendLine(line);
                else truncated = true;
            }
        }
        process.OutputDataReceived += (_, e) => Take(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Take(stderr, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!timedOut) throw;
        }
        process.WaitForExit(2000);
        return new CliOutput(timedOut ? -1 : process.ExitCode, Redact(stdout.ToString()), Redact(stderr.ToString()), bytes, watch.Elapsed, timedOut, truncated);
    }

    private static string Quote(string a) => a.Length > 0 && a.IndexOfAny([' ', '\t']) < 0 ? a : $"\"{a}\"";
}
