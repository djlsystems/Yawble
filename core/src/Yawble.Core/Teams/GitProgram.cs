using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Yawble.Core.Teams;

/// <summary>git is not installed on this computer, or not on PATH.</summary>
public sealed class GitMissingException() : GitException(GitProgram.MissingMessage);

/// <summary>
/// The host's git program: where it is, and starting it. Yawble needs git (any host or none: git works on local
/// folders alone); it does not ship one. Every place the core starts git goes through <see cref="Start"/>, so a
/// missing git reads as one clear message instead of the operating system's "file not found".
/// </summary>
public static partial class GitProgram
{
    /// <summary>The oldest git the core works with: merges use <c>git merge-tree --write-tree --name-only --no-messages</c> (2.38).</summary>
    public static readonly Version Minimum = new(2, 38);

    public const string DownloadUrl = "https://git-scm.com/downloads";

    public static readonly string MissingMessage =
        $"git is not installed on this computer, or is not on PATH. Yawble needs git {Minimum} or later to run teams: " +
        $"use Install Git at the top of the Yawble window, or get it from {DownloadUrl}";

    /// <summary>Starts git; a git that cannot be found throws <see cref="GitMissingException"/>.</summary>
    public static Process Start(ProcessStartInfo psi)
    {
        try { return Process.Start(psi) ?? throw new GitMissingException(); }
        catch (Win32Exception) { throw new GitMissingException(); }
    }

    /// <summary>The version in <c>git --version</c> output ("git version 2.45.1.windows.1"), or null.</summary>
    public static Version? ParseVersion(string text)
    {
        var m = VersionPattern().Match(text);
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    [GeneratedRegex(@"git version (\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();

    /// <summary>Whether git's error text says it does not know an option or command the core used: a git older than <see cref="Minimum"/>.</summary>
    public static bool LooksTooOld(string stderr) =>
        stderr.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("usage: git merge-tree", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("is not a git command", StringComparison.OrdinalIgnoreCase);

    public static string TooOldMessage(string what) =>
        $"{what} failed because this git is too old: Yawble needs git {Minimum} or later (Settings > Health shows the version found)";

    /// <summary>The program's file name on this OS.</summary>
    public static string FileName(bool windows) => windows ? "git.exe" : "git";

    /// <summary>The first <paramref name="file"/> in the folders of <paramref name="pathVariable"/>, or null.</summary>
    public static string? FindOnPath(string? pathVariable, string file, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim().Trim('"');
            if (d.Length == 0) continue;
            string full;
            try { full = Path.Combine(d, file); }
            catch (ArgumentException) { continue; }
            if (exists(full)) return full;
        }
        return null;
    }

    /// <summary>
    /// Folders git is installed in when this process's PATH does not name it yet: Windows reads the person's and the
    /// machine's PATH as they are now (an install after this process started is in them) and Git for Windows' own
    /// folders (per user, then per machine); macOS and Linux the usual system and Homebrew folders.
    /// </summary>
    public static IReadOnlyList<string> KnownFolders(bool windows, Func<string, string?> env, Func<string>? userPath = null, Func<string>? machinePath = null)
    {
        var list = new List<string>();
        if (windows)
        {
            foreach (var p in new[] { userPath?.Invoke(), machinePath?.Invoke() })
                if (!string.IsNullOrEmpty(p)) list.AddRange(p.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(Expand));
            if (env("LOCALAPPDATA") is { Length: > 0 } local) list.Add(Path.Combine(local, "Programs", "Git", "cmd"));
            foreach (var v in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
                if (env(v) is { Length: > 0 } pf) list.Add(Path.Combine(pf, "Git", "cmd"));
        }
        else
        {
            list.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"]);
        }
        return list.Distinct(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
    }

    private static string Expand(string folder) => Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'));

    private static readonly Lock PathLock = new();

    /// <summary>
    /// Where git is, making sure this process (and every program it starts, the sandboxed runs included) finds it by
    /// name: a git found only in <see cref="KnownFolders"/> has its folder put first on this process's PATH. Null when
    /// there is none.
    /// </summary>
    public static string? EnsureOnPath()
    {
        var windows = OperatingSystem.IsWindows();
        var file = FileName(windows);
        lock (PathLock)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (FindOnPath(path, file) is { } onPath) return onPath;
            var known = KnownFolders(windows, Environment.GetEnvironmentVariable,
                windows ? () => Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "" : null,
                windows ? () => Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "" : null);
            var found = FindOnPath(string.Join(Path.PathSeparator, known), file);
            if (found is null) return null;
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(found) + Path.PathSeparator + path);
            return found;
        }
    }

    /// <summary>Runs a program with a time limit and returns what it printed. A program that cannot start is exit code -1.</summary>
    public static async Task<GitResult> RunProgramAsync(string exe, IReadOnlyList<string> args, TimeSpan limit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (Win32Exception e) { return new GitResult(-1, "", e.Message); }
        if (p is null) return new GitResult(-1, "", $"{exe} could not be started");
        using (p)
        {
            p.StandardInput.Close();
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return new GitResult(-1, "", $"{Path.GetFileName(exe)} did not finish within {limit.TotalMinutes:0} minutes and was stopped");
            }
            return new GitResult(p.ExitCode, (await o).ReplaceLineEndings("\n").Trim(), (await e).ReplaceLineEndings("\n").Trim());
        }
    }
}
