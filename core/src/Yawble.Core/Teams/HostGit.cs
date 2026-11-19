using System.Diagnostics;
using System.Text;

namespace Yawble.Core.Teams;

/// <summary>A git command the host ran that failed.</summary>
public class GitException(string message) : Exception(message);

public sealed record GitResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs the HOST's git, outside every sandbox, only locally.
///
/// Hardened so that no git credential and no network transport is ever involved, whatever the person's own git
/// setup holds:
/// <list type="bullet">
/// <item>Every inherited GIT_* variable is dropped; the system and global config files are not read
///   (GIT_CONFIG_NOSYSTEM, GIT_CONFIG_GLOBAL = an empty file), so no credential helper, url rewrite or hook path
///   from the person's config applies.</item>
/// <item><c>credential.helper</c> is reset to empty, prompts are off (GIT_TERMINAL_PROMPT=0, no askpass).</item>
/// <item><c>protocol.allow=never</c> with only <c>protocol.file.allow=always</c>: the host's git can reach local
///   paths and nothing else, so it cannot push or fetch over the network even if a remote is configured.</item>
/// </list>
/// This class has no push method. The one push (to the team repository's origin, after a run) is
/// <see cref="PersonGitPush"/>.
/// </summary>
public sealed class HostGit
{
    private readonly string _emptyConfig;
    private readonly TimeSpan _timeout;

    public HostGit(string emptyConfigPath, TimeSpan? timeout = null)
    {
        _emptyConfig = emptyConfigPath;
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
        Directory.CreateDirectory(Path.GetDirectoryName(emptyConfigPath)!);
        if (!File.Exists(emptyConfigPath)) File.WriteAllText(emptyConfigPath, "");
    }

    /// <summary>The options put before every subcommand.</summary>
    public static readonly IReadOnlyList<string> SafetyOptions =
    [
        "-c", "credential.helper=",
        "-c", "protocol.allow=never",
        "-c", "protocol.file.allow=always",
        "-c", "core.askPass=",
        "-c", "commit.gpgSign=false",
        "-c", "tag.gpgSign=false",
        "-c", "init.defaultBranch=main",
    ];

    public async Task<GitResult> TryRunAsync(string? workingDirectory, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in psi.Environment.Keys.ToArray())
        {
            if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GCM_", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SSH_ASKPASS", StringComparison.OrdinalIgnoreCase) || key.Equals("SSH_AUTH_SOCK", StringComparison.OrdinalIgnoreCase))
            {
                psi.Environment.Remove(key);
            }
        }
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = _emptyConfig;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["LC_ALL"] = "C";
        foreach (var option in SafetyOptions) psi.ArgumentList.Add(option);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = GitProgram.Start(psi);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new GitException($"git {string.Join(' ', args)} timed out after {_timeout.TotalSeconds:0}s");
        }
        // LF on every OS: callers split on '\n' and compare ref names; a CRLF from a Windows git build (or a wrapper)
        // would otherwise leave "\r" on the last field, and a branch that is ours would look like another one.
        return new GitResult(process.ExitCode, (await stdout).ReplaceLineEndings("\n").TrimEnd(), (await stderr).ReplaceLineEndings("\n").TrimEnd());
    }

    public async Task<string> RunAsync(string? workingDirectory, IEnumerable<string> args, CancellationToken ct = default)
    {
        var list = args.ToList();
        var result = await TryRunAsync(workingDirectory, list, ct);
        if (result.ExitCode != 0)
        {
            throw new GitException($"git {string.Join(' ', list)} failed ({result.ExitCode}): {result.Stderr}");
        }
        return result.Stdout;
    }

    public Task<string> RunAsync(string? workingDirectory, CancellationToken ct, params string[] args) => RunAsync(workingDirectory, args, ct);

    /// <summary>How many times a fetch from a local repository path is tried when its transport fails (<see cref="FetchLocalAsync"/>).</summary>
    public int LocalFetchAttempts { get; init; } = 3;

    /// <summary>The pause before the second try; doubled before each later one.</summary>
    public TimeSpan LocalFetchRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Told each time a fetch from a local path is tried again, with git's error.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// A fetch (or ls-remote) whose source is a repository path on this computer. git reaches a path by starting git-upload-pack
    /// through a shell, and on Windows that shell now and then fails to start when the computer is busy (git says only
    /// "Could not read from remote repository"). A fetch only reads its source and lands where it lands again on a
    /// second try, so a transport failure is tried again, <see cref="LocalFetchAttempts"/> times in all; any other
    /// failure (a ref git refuses, a missing repository) is answered at once.
    /// </summary>
    public Task<GitResult> FetchLocalAsync(string? workingDirectory, IReadOnlyList<string> args, CancellationToken ct = default) =>
        RetryTransportAsync(c => TryRunAsync(workingDirectory, args, c), LocalFetchAttempts, LocalFetchRetryDelay,
            (n, stderr) => Log?.Invoke($"git {string.Join(' ', args)}: transport failed (try {n} of {LocalFetchAttempts}), trying again: {stderr}"), ct);

    /// <summary>
    /// Runs <paramref name="run"/> until it succeeds, fails for a reason other than the transport, or has been tried
    /// <paramref name="attempts"/> times; returns the last result.
    /// </summary>
    public static async Task<GitResult> RetryTransportAsync(Func<CancellationToken, Task<GitResult>> run, int attempts, TimeSpan delay,
        Action<int, string>? retrying, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await run(ct);
            if (result.ExitCode == 0 || attempt >= attempts || !IsTransportFailure(result.Stderr)) return result;
            retrying?.Invoke(attempt, result.Stderr);
            await Task.Delay(delay * Math.Pow(2, attempt - 1), ct);
        }
    }

    /// <summary>git's words when the other side of a fetch went away or never came up (not a refusal of what was asked).</summary>
    public static bool IsTransportFailure(string stderr) =>
        // git adds "Could not read..." after a missing repository too; that one will not come back by itself.
        !stderr.Contains("does not appear to be a git repository", StringComparison.OrdinalIgnoreCase) && (
        stderr.Contains("Could not read from remote repository", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("the remote end hung up unexpectedly", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("early EOF", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Resource temporarily unavailable", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("unable to fork", StringComparison.OrdinalIgnoreCase));

    /// <summary><see cref="FetchLocalAsync"/>, throwing <see cref="GitException"/> (with git's whole error) when it still fails.</summary>
    public async Task FetchLocalOrThrowAsync(string? workingDirectory, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var result = await FetchLocalAsync(workingDirectory, args, ct);
        if (result.ExitCode != 0) throw new GitException($"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.Stderr}");
    }
}
