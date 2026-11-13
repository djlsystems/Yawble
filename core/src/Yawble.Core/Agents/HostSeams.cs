using System.Diagnostics;
using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

// The detector's view of the host, kept small so the unit tests can describe every OS's layouts as a fake
// filesystem and a fake process runner, from Linux (Agents/FakeHost in the tests).

/// <summary>
/// The filesystem as detection needs it. <see cref="ReadHead"/> is only ever called on launchers and programs
/// (to tell a native binary from a script and to read npm's .cmd shims), never on a CLI's config or credential files.
/// </summary>
public interface IHostFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>The raw target of <paramref name="path"/> itself when it is a symlink or junction (relative or absolute); null otherwise.</summary>
    string? LinkTarget(string path);

    /// <summary>The first bytes of a program or launcher; null when it cannot be read.</summary>
    byte[]? ReadHead(string path, int maxBytes);
}

/// <summary>One "&lt;cli&gt; --version" probe.</summary>
public sealed record CliProbeCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout);

/// <summary>What the probe printed. StartError: it never started (with the OS's reason).</summary>
public sealed record CliProbeResult(int? ExitCode, string Stdout, string Stderr, bool TimedOut, string? StartError = null);

public interface ICliProcessRunner
{
    Task<CliProbeResult> RunAsync(CliProbeCommand command, CancellationToken ct);
}

/// <summary>The environment detection reads: OS, home, PATH (and PATHEXT on Windows) and a few variables.</summary>
public sealed record HostEnvironment(
    TargetOs Os,
    string Home,
    IReadOnlyList<string> PathDirectories,
    IReadOnlyList<string> PathExtensions,
    IReadOnlyDictionary<string, string> Variables)
{
    public string? Var(string name) =>
        Variables.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v
        : Os == TargetOs.Windows ? Variables.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value)).Value
        : null;

    /// <summary>Read fresh on every call: a CLI installed while the app is open is found on the next detect.</summary>
    public static HostEnvironment Current()
    {
        var os = TargetOsExtensions.Host;
        var variables = new Dictionary<string, string>(os == TargetOs.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            variables[(string)e.Key] = (string?)e.Value ?? "";
        var sep = os == TargetOs.Windows ? ';' : ':';
        var path = variables.TryGetValue("PATH", out var p) ? p : "";
        var pathExt = os == TargetOs.Windows
            ? (variables.TryGetValue("PATHEXT", out var pe) && pe.Length > 0 ? pe : ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = variables.TryGetValue("HOME", out var h) ? h : "/";
        return new HostEnvironment(os, home, path.Split(sep, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim('"')).ToList(), pathExt, variables);
    }
}

/// <summary>The real filesystem.</summary>
public sealed class RealHostFileSystem : IHostFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? LinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public byte[]? ReadHead(string path, int maxBytes)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[maxBytes];
            var n = f.ReadAtLeast(buffer, maxBytes, throwOnEndOfStream: false);
            return buffer[..n];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Runs a probe with a throwaway HOME (and USERPROFILE/APPDATA/LOCALAPPDATA on Windows): "--version" must not
/// touch the person's config, credentials or update state, and it shows the program runs without them.
/// The whole process tree is killed at the timeout.
/// </summary>
public sealed class RealCliProcessRunner : ICliProcessRunner
{
    public async Task<CliProbeResult> RunAsync(CliProbeCommand command, CancellationToken ct)
    {
        var scratch = Directory.CreateTempSubdirectory("yawble-cli-probe-");
        try
        {
            var start = new ProcessStartInfo(command.FileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = scratch.FullName,
            };
            foreach (var a in command.Arguments) start.ArgumentList.Add(a);
            start.Environment["HOME"] = scratch.FullName;
            if (OperatingSystem.IsWindows())
            {
                start.Environment["USERPROFILE"] = scratch.FullName;
                start.Environment["APPDATA"] = Directory.CreateDirectory(Path.Combine(scratch.FullName, "AppData", "Roaming")).FullName;
                start.Environment["LOCALAPPDATA"] = Directory.CreateDirectory(Path.Combine(scratch.FullName, "AppData", "Local")).FullName;
            }
            foreach (var (k, v) in command.Environment) start.Environment[k] = v;

            using var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) return new CliProbeResult(null, "", "", false, "the process did not start");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new CliProbeResult(null, "", "", false, ex.Message);
            }
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(command.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return new CliProbeResult(null, "", "", true);
            }
            return new CliProbeResult(process.ExitCode, await stdout, await stderr, false);
        }
        finally
        {
            try { scratch.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
