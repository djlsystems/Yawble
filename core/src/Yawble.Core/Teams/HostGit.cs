using System.Diagnostics;
using System.Text;

namespace Yawble.Core.Teams;

/// <summary>A git command the host ran that failed.</summary>
public sealed class GitException(string message) : Exception(message);

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

        using var process = Process.Start(psi) ?? throw new GitException("git could not be started: is git installed?");
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
}
