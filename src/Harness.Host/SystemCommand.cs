using System.Runtime.InteropServices;

namespace Harness.Host;

/// <summary>
/// WHERE THE HOST FINDS A PROGRAM IT RUNS ITSELF. From fixed system directories,
/// never from PATH.
///
/// <para>
/// In the image the PATH begins with folders the agent owns (<c>/data/bin</c>,
/// <c>/data/npm-global/bin</c>, ...), because that is where an agent installs its tools. The Host
/// runs as <c>harness</c> and holds CAP_SETUID, CAP_SETGID and CAP_KILL as ambient capabilities,
/// which pass to any child not started through <see cref="AgentLaunchUser.Prefix"/>. A
/// <c>git</c>, <c>setpriv</c> or <c>setsid</c> an agent dropped into <c>/data/bin</c> and the
/// Host found on PATH would run as <c>harness</c> with those capabilities, and from there as root.
/// </para>
///
/// <para>
/// A directory counts only when it, and every directory above it, is owned by root and writable
/// by nobody else; the same holds for the file (after symlinks). <c>/usr/local/bin</c> is on the
/// list and passes only when it is root's.
/// </para>
/// </summary>
public static class SystemCommand
{
    /// <summary>Searched in this order. Nothing else is.</summary>
    public static IReadOnlyList<string> Directories { get; } =
        ["/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/local/bin"];

    /// <summary>
    /// <see cref="Directories"/> that pass the ownership check, joined as a PATH. Handed to a child
    /// the Host starts without the agent prefix, so what that child starts by name is a system
    /// program too.
    /// </summary>
    public static string SafePath => string.Join(':', Directories.Where(IsTrusted));

    /// <summary>
    /// The absolute path of <paramref name="command"/>, or null. A command carrying a '/' is an
    /// operator's own setting (<c>GitExecutable</c>) and is returned as given when it is
    /// executable. A bare name is looked up only in <paramref name="directories"/>
    /// (default <see cref="Directories"/>) and only where the directory and the file are root's.
    /// </summary>
    public static string? Find(string? command, IReadOnlyList<string>? directories = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var name = command.Trim();

        if (name.Contains('/')) return IsExecutable(name) ? name : null;

        foreach (var directory in directories ?? Directories)
        {
            var candidate = Path.Combine(directory, name);
            if (IsTrusted(directory) && IsExecutable(candidate) && IsRootsAlone(candidate)
                && IsTrusted(Path.GetDirectoryName(Target(candidate))!))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> and every directory above it are owned by root and not
    /// writable by group or other. A path that cannot be read counts as not trusted.
    /// </summary>
    public static bool IsTrusted(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;

        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (!IsRootsAlone(current)) return false;
        }

        return true;
    }

    /// <summary>Where a symlink finally points, so the folder that really holds the file is checked.</summary>
    private static string Target(string path) =>
        new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;

    private static bool IsExecutable(string path) =>
        File.Exists(path) &&
        (File.GetUnixFileMode(path) &
         (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    /// <summary>Owned by uid 0 and writable by nobody else, following symlinks.</summary>
    private static bool IsRootsAlone(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;

        // struct statx is the same on every architecture, unlike struct stat, which is why this
        // reads it and not stat(2). stx_uid is at offset 20 and stx_mode at 28.
        var buffer = new byte[256];
        if (statx(AtFdCwd, path, 0, StatxBasicStats, buffer) != 0) return false;

        var uid = BitConverter.ToUInt32(buffer, 20);
        var mode = BitConverter.ToUInt16(buffer, 28);
        const int groupOrOtherWrite = 0b000_010_010;
        return uid == 0 && (mode & groupOrOtherWrite) == 0;
    }

    private const int AtFdCwd = -100;
    private const uint StatxBasicStats = 0x7ff;

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, string pathname, int flags, uint mask, byte[] buffer);
}
