using System.Diagnostics;

namespace Harness.Host;

/// <summary>
/// HOW A WORKER IN A PROCESS OF ITS OWN REMOVES A RUN'S HOME, with nothing of control's: the
/// home must be strictly inside its parent with no symbolic link on the way, or it is left alone;
/// the worker's own pass removes what it can, entry by entry, links removed and never followed;
/// when agent children run as another user, what is left is removed as the agent
/// (<c>rm -rf --one-file-system</c> through <see cref="AgentLaunchUser.Prefix"/>, so it can do
/// nothing the agent could not), and the worker's pass runs again for what the agent emptied.
/// The rules are control's <c>FolderRemoval</c>'s for a folder agents write in; a Host that runs
/// its runs itself keeps using that.
/// </summary>
public static class RunHomeRemoval
{
    /// <summary>The removal <see cref="RunHomes"/> is given in a worker process.</summary>
    public static Func<string, string, Task> For(AgentLaunchUser? runAs) =>
        (parent, home) => RemoveAsync(parent, home, runAs);

    /// <summary>
    /// Removes <paramref name="home"/> from inside <paramref name="parent"/>. Returns every path still
    /// there, or the home itself when it was left alone because it is not confined. Never throws.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RemoveAsync(
        string parent, string home, AgentLaunchUser? runAs, CancellationToken ct = default)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));

        if (!Exists(home)) return [];
        if (!Confined(parent, home)) return [home];

        var left = new List<string>();
        RemoveAsWorker(home, left);

        if (left.Count > 0 && runAs is { Switches: true } && SystemCommand.Find("rm") is { } rm)
        {
            await RunAsync([.. runAs.Prefix, rm, "-rf", "--one-file-system", "--", home], ct);
            left.Clear();
            RemoveAsWorker(home, left);
        }

        return left;
    }

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

    /// <summary>Removes <paramref name="path"/> bottom up, a link as a link; what cannot go is named in <paramref name="left"/>.</summary>
    private static void RemoveAsWorker(string path, List<string> left)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null || !Directory.Exists(path))
            {
                File.Delete(path);
                return;
            }

            List<string> entries;
            try
            {
                entries = [.. Directory.EnumerateFileSystemEntries(path)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(path);
                return;
            }

            foreach (var entry in entries) RemoveAsWorker(entry, left);
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            left.Add(path);
        }
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

    private static bool Exists(string path) =>
        Directory.Exists(path) || new FileInfo(path) is { } file && (file.Exists || file.LinkTarget is not null);

    private static async Task RunAsync(IReadOnlyList<string> command, CancellationToken ct)
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
            // Not startable: the worker's own pass that follows names what is still there.
        }
    }
}
