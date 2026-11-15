using Yawble.Core.Companion;

namespace Yawble.Core.Storage;

/// <summary>
/// Keeps the data folder user-only on Linux and macOS: the folder and every folder in it 0700, every file 0600 (the
/// database and its -wal/-shm, backups and their sidecars, logs, transcripts, the Cloud Link and update files). The core
/// makes what it creates that way (<see cref="UserOnlyFile"/>); at start it tightens what is already there, since a
/// folder made by an older version, a custom --data-dir (YAWBLE_DATA_DIR) or a parent the person loosened would otherwise
/// let other local users read the database and logs. Only group and other bits are taken off, only on what this user
/// owns (chmod refuses the rest), and symlinks are neither followed nor changed. Windows: nothing to do (the data folder
/// inherits the user profile's rules).
/// </summary>
public static class DataFolderAccess
{
    /// <summary>The most changed paths named in the log line; the rest are counted.</summary>
    private const int Named = 5;

    /// <summary>The logs folder, which the core's own log may have made before this runs.</summary>
    private const string FileLogsFolder = Logs.FileLogs.DiagnosticsFolder;

    /// <summary>
    /// Creates <paramref name="dataDir"/> user-only when it is missing and tightens the whole tree; with an app root (the
    /// shell's &lt;userData&gt;, holding data/ and teams/) that folder itself is tightened too (not its contents: teams/
    /// holds the members' clones, which keep their own modes). Says what changed through <paramref name="log"/>.
    /// Returns the number of paths changed. Never throws for one path it cannot change.
    /// </summary>
    public static int Secure(string dataDir, string? appRoot, Action<string> log)
    {
        if (OperatingSystem.IsWindows()) return 0;
        UserOnlyFile.CreatePrivateDirectory(dataDir);
        if (Environment.IsPrivilegedProcess)
        {
            log("data folder: running as root, so its permissions are left as they are");
            return 0;
        }
        // Only a folder that is Yawble's (it holds the database, or nothing yet): a --data-dir pointed by mistake at the
        // home or a shared folder must never have everything in it re-moded.
        var full = Path.GetFullPath(dataDir);
        if (!File.Exists(Path.Combine(full, "yawble.db")) && Directory.EnumerateFileSystemEntries(full).Any(e => Path.GetFileName(e) != FileLogsFolder))
        {
            log($"data folder: {full} holds other files but no yawble.db, so their permissions are left as they are");
            return 0;
        }
        var changed = new List<string>();
        if (appRoot is not null && UserOnlyFile.Tighten(appRoot)) changed.Add(appRoot);
        Walk(full, changed);
        if (changed.Count > 0)
        {
            var named = string.Join(", ", changed.Take(Named));
            var more = changed.Count > Named ? $" and {changed.Count - Named} more" : "";
            log($"data folder: made {changed.Count} path(s) user-only (group and other access removed): {named}{more}");
        }
        return changed.Count;
    }

    private static void Walk(string root, List<string> changed)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if (UserOnlyFile.Tighten(dir)) changed.Add(dir);
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false,
                }).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry.LinkTarget is not null) continue; // never followed, never changed
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else if (UserOnlyFile.Tighten(entry.FullName)) changed.Add(entry.FullName);
            }
        }
    }
}
