using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Yawble.CodeBrowser.Tests;

/// <summary>A temp folder for one test, removed afterwards. Tests never touch anything outside temp folders.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yawble-cb-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public static string Write(string root, string relative, string text)
    {
        var full = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public void Dispose()
    {
        try
        {
            // Links first, so deleting never follows one out of the temp folder.
            foreach (var d in Directory.EnumerateDirectories(Path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList())
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d);
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Links on the test machine: a junction (Windows, no privilege needed) or a symlink (may need one).</summary>
public static class Links
{
    public static bool TryJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return TrySymlinkDir(link, target);
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        if (Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        return TrySymlinkDir(link, target); // mklink may not run where the tests run: a directory symlink is a reparse point too
    }

    public static bool TrySymlinkDir(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool TrySymlinkFile(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>The host's git for the test repositories: no system or global config, no prompts.</summary>
public sealed class RealGit : IGitReader
{
    private readonly string _emptyConfig;

    [ThreadStatic] private static string? LastError;

    public RealGit(string scratch)
    {
        _emptyConfig = System.IO.Path.Combine(scratch, "empty.gitconfig");
        File.WriteAllText(_emptyConfig, "");
    }

    public static bool Available
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("git", "--version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
                p.WaitForExit(10_000);
                return p.ExitCode == 0;
            }
            catch (Exception) { return false; }
        }
    }

    public Task<GitReadResult> RunAsync(string repository, IReadOnlyList<string> args, CancellationToken ct) => Task.FromResult(Run(repository, args));

    public GitReadResult Run(string cwd, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(key);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = _emptyConfig;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@localhost", "-c", "init.defaultBranch=main", "-c", "core.autocrlf=false", "-c", "core.longpaths=true" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit(60_000);
        LastError = errTask.Result;
        return new GitReadResult(p.ExitCode, outTask.Result.ReplaceLineEndings("\n").TrimEnd());
    }

    public void Ok(string cwd, params string[] args)
    {
        var r = Run(cwd, args);
        if (r.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {r.Stdout} {LastError}");
    }
}

/// <summary>The process-runner fake: records every git command (its working directory and arguments), then runs it for real.</summary>
public sealed class RecordingGit(IGitReader inner) : IGitReader
{
    public List<(string Repository, IReadOnlyList<string> Args)> Calls { get; } = [];

    public Task<GitReadResult> RunAsync(string repository, IReadOnlyList<string> args, CancellationToken ct)
    {
        lock (Calls) Calls.Add((repository, args));
        return inner.RunAsync(repository, args, ct);
    }
}

/// <summary>A host with one member (team "web", member "Ada") whose clone is a temp folder.</summary>
public sealed class FakeHost(string clonePath, BranchSource? branch = null, IGitReader? git = null) : ICodeHost
{
    public const string Key = "1/2";

    public List<(string Name, JsonNode Data)> Events { get; } = [];
    public List<(CodeMember, DateTimeOffset)> Running { get; } = [];
    public Func<string, bool>? Denied { get; set; }

    public CodeMember Member => new(Key, "web", "Ada", clonePath, branch);

    public Task<CodeMember?> FindAsync(MemberQuery query, CancellationToken ct) =>
        Task.FromResult(string.Equals(query.Member, "Ada", StringComparison.OrdinalIgnoreCase) || query.MemberId == 2 ? Member : null);

    public Task<IReadOnlyList<(CodeMember Member, DateTimeOffset StartedAt)>> RunningAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(CodeMember, DateTimeOffset)>>([.. Running]);

    public void Publish(string name, JsonNode data)
    {
        lock (Events) Events.Add((name, data));
    }

    public IGitReader? Git => git;

    public bool PolicyDenied(string fullPath) => Denied?.Invoke(fullPath) == true;
}
