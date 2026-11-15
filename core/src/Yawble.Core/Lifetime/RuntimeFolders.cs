using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Yawble.Core.Lifetime;

/// <summary>
/// The shell's per-launch IPC folders on Linux and macOS: mkdtemp("yawble-XXXXXX") in $XDG_RUNTIME_DIR (or the temp
/// folder), holding core.sock and the companion's control socket. The core removes its own on a clean exit, and at startup
/// sweeps the ones crashed launches left behind.
///
/// Only ever removed: socket files (*.sock) that no longer answer, yawble-* folders nested inside (the companion's), and
/// then the folder itself when it is empty (a non-recursive delete). A folder with any other file in it, or any socket that
/// answers, is never touched; nor is one changed in the last <see cref="MinimumAge"/> (a shell that has just made its
/// folder while its core is still starting). Windows uses named pipes and has no such folders.
/// </summary>
public sealed partial class RuntimeFolders(Func<string, bool> socketAnswers, Func<DateTime>? utcNow = null, Action<string>? log = null)
{
    public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(2);

    [GeneratedRegex("^yawble-[A-Za-z0-9]{6}$")]
    private static partial Regex LaunchFolderName();

    [GeneratedRegex("^yawble-[A-Za-z0-9]+$")]
    private static partial Regex NestedFolderName();

    /// <summary>
    /// The shell's launch folder that holds <paramref name="socketPath"/>: its parent when that is named like mkdtemp's
    /// yawble-XXXXXX; null otherwise (a socket somewhere else, as in the tests, is never this code's to clean).
    /// </summary>
    public static string? LaunchFolderOf(string socketPath)
    {
        if (OperatingSystem.IsWindows() && socketPath.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(socketPath));
        return dir is not null && LaunchFolderName().IsMatch(Path.GetFileName(dir)) ? dir : null;
    }

    /// <summary>On a clean exit: removes this launch's folder once its sockets are gone (non-recursive; never a file).</summary>
    public static bool RemoveOwn(string launchFolder)
    {
        try
        {
            Directory.Delete(launchFolder, recursive: false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Sweeps the stale yawble-XXXXXX folders in <paramref name="baseDir"/>, never <paramref name="own"/>. Returns the
    /// folders removed.
    /// </summary>
    public IReadOnlyList<string> SweepStale(string baseDir, string? own)
    {
        var removed = new List<string>();
        string[] candidates;
        try { candidates = Directory.GetDirectories(baseDir, "yawble-*"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return removed; }
        var ownFull = own is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(own));
        foreach (var dir in candidates)
        {
            if (!LaunchFolderName().IsMatch(Path.GetFileName(dir))) continue;
            if (ownFull is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)), ownFull, StringComparison.Ordinal)) continue;
            try
            {
                if (IsSymlink(dir) || !IsStale(dir)) continue;
                if (Clear(dir))
                {
                    removed.Add(dir);
                    log?.Invoke($"removed a stale IPC folder: {dir}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>Old enough, and nothing in it (or in its nested yawble-* folders) is a socket that answers or anything but a socket.</summary>
    private bool IsStale(string dir)
    {
        var now = (utcNow ?? (() => DateTime.UtcNow))();
        if (now - Directory.GetLastWriteTimeUtc(dir) < MinimumAge) return false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (IsSymlink(entry)) return false;
            if (Directory.Exists(entry))
            {
                if (!NestedFolderName().IsMatch(Path.GetFileName(entry)) || !IsStale(entry)) return false;
            }
            else if (!entry.EndsWith(".sock", StringComparison.Ordinal) || socketAnswers(entry))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Deletes the dead sockets and nested folders, then the folder; false if anything stayed.</summary>
    private static bool Clear(string dir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir).ToList())
        {
            if (Directory.Exists(entry)) { if (!Clear(entry)) return false; }
            else File.Delete(entry);
        }
        return RemoveOwn(dir);
    }

    private static bool IsSymlink(string path) => new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>The real probe: a Unix socket answers when a connect to it succeeds.</summary>
    public static bool UnixSocketAnswers(string path)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable || !File.Exists(path))
        {
            return false; // nobody listens on it (a crashed core's socket), or it is gone
        }
        catch (Exception)
        {
            return true; // anything else (permission, a timeout): treat it as alive, never remove it
        }
    }
}
