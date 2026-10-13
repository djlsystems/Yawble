using System.Diagnostics;

namespace Harness.Host;

/// <summary>
/// WHAT A WORKER'S REMOVAL AS THE AGENT NEEDS, with nothing of control's: whether a path is strictly
/// inside its boundary with no symbolic link on the way, and a way to run <c>rm</c> through
/// <see cref="AgentLaunchUser.Prefix"/>, so it can do nothing the agent could not.
/// </summary>
public static class AgentPassRemoval
{
    /// <summary>
    /// Whether <paramref name="path"/> is strictly inside <paramref name="boundary"/> with no symbolic
    /// link on the way - the boundary included. A directory that cannot be examined counts as a link.
    /// </summary>
    public static bool Confined(string boundary, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundary));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        if (LinkOrUnknown(root)) return false;

        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
        {
            if (LinkOrUnknown(dir)) return false;
        }

        return true;
    }

    private static bool LinkOrUnknown(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal static async Task RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return;

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(output, error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not startable: what is still there is named by the caller's own check that follows.
        }
    }
}
