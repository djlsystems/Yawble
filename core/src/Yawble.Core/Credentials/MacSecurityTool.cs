using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Yawble.Core.Credentials;

/// <summary>
/// What one run of macOS's <c>/usr/bin/security</c> gave back. <see cref="StandardOutput"/> may hold a secret (the
/// password of <c>find-generic-password -w</c>): <see cref="ToString"/> never shows it, and it is never logged. The
/// tool's stderr is not kept at all (it can echo what it read).
/// </summary>
public sealed class SecurityToolResult(int exitCode, string standardOutput, bool timedOut = false)
{
    public int ExitCode { get; } = exitCode;

    public string StandardOutput { get; } = standardOutput;

    /// <summary>The tool did not finish within the timeout and was killed.</summary>
    public bool TimedOut { get; } = timedOut;

    public override string ToString() => TimedOut ? "security: timed out" : $"security: exit code {ExitCode}";
}

/// <summary>Runs <c>/usr/bin/security</c> with an argv list. Tests replace it with a fake; nothing else runs the tool.</summary>
public interface ISecurityTool
{
    SecurityToolResult Run(IReadOnlyList<string> arguments, TimeSpan timeout);
}

/// <summary>
/// The real tool: the absolute path <see cref="Path"/> only (no PATH lookup), the arguments as an argv list (no shell),
/// stdin closed, stdout read in memory, stderr drained and dropped, the process tree killed on timeout.
/// </summary>
public sealed class ProcessSecurityTool : ISecurityTool
{
    public const string Path = "/usr/bin/security";

    public SecurityToolResult Run(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(Path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new CredentialStoreException($"{Path} could not be started");
        }
        if (process is null) throw new CredentialStoreException($"{Path} could not be started");
        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
                return EndTimedOut(() => process.Kill(entireProcessTree: true), () => process.WaitForExit(1000));
            process.WaitForExit(); // the output streams are complete after this
            _ = stderr.Result;
            return new SecurityToolResult(process.ExitCode, stdout.Result);
        }
    }

    /// <summary>
    /// The tool did not answer in time: kill its tree and wait a moment. Killing can fail (it already exited:
    /// InvalidOperationException; a member of the tree cannot be ended: Win32Exception); the result is "timed out" either
    /// way, never an exception. Tests pass fakes for both steps.
    /// </summary>
    internal static SecurityToolResult EndTimedOut(Action kill, Action waitBriefly)
    {
        try { kill(); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
        try { waitBriefly(); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
        return new SecurityToolResult(-1, "", timedOut: true);
    }
}

/// <summary>
/// The Keychain items of other programs that Yawble only ever READS (Claude Code's own sign-in). No code path writes,
/// modifies, deletes or changes the access of one: the app's own credential store refuses such a service name, and the
/// reader runs only <c>security find-generic-password</c>.
/// </summary>
public static class ForeignKeychainItems
{
    public static IReadOnlyList<string> Services { get; } = [ClaudeHostSession.KeychainService];

    public static bool IsForeign(string service) => Services.Contains(service, StringComparer.Ordinal);

    /// <summary>Throws when <paramref name="service"/> is another program's item; <paramref name="operation"/> names the refused write.</summary>
    public static void EnsureWritable(string service, string operation)
    {
        if (IsForeign(service))
            throw new CredentialStoreException($"refused: {operation} on the Keychain item \"{service}\" (another program's item; Yawble only reads it)");
    }
}
