using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Yawble.Core.Companion;

/// <summary>
/// Folders and files only the current user can open: the companion's control.json, its clients folder and key files.
/// Windows: a protected DACL (nothing inherited) holding exactly one rule, FullControl for the current user's SID, set when
/// the folder or file is created (a file never exists with the folder's inherited rules). POSIX: 0700 folders, 0600 files.
/// A file is written to a temp file beside it and then moved over the old one, so a reader never sees half a file.
/// Link-compiled into yawble-companion: BCL only, AOT-clean.
/// </summary>
public static class UserOnlyFile
{
    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates <paramref name="dir"/> user-only (its parents keep their own rules). An existing folder that is empty or
    /// holds only the companion's own files (control.json, clients\, *.key, its .tmp files) is made user-only (left alone
    /// when it already is); any other existing folder, user-only or not, is refused with
    /// <see cref="InvalidOperationException"/> and left exactly as it was: re-securing it would rewrite the rules of
    /// everything inside (a profile, a repository), and the companion's files do not belong among someone else's.
    /// </summary>
    public static void EnsureDirectory(string dir)
    {
        dir = Path.GetFullPath(dir);
        if (Directory.Exists(dir))
        {
            // What is inside is checked first, whatever the folder's rules: a folder that is already user-only (on POSIX,
            // Directory.CreateTempSubdirectory and ~/.ssh are 0700) but holds someone else's files is refused the same.
            if (!HoldsOnlyCompanionFiles(dir))
                throw new InvalidOperationException(
                    $"the folder '{dir}' already holds other files, so Yawble will not change who can open it; " +
                    "point YAWBLE_HOME at a new or empty folder for the companion's files");
            if (IsUserOnly(dir)) return;
            if (OperatingSystem.IsWindows()) new DirectoryInfo(dir).SetAccessControl(DirectorySecurityForUser());
            else File.SetUnixFileMode(dir, DirMode);
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(dir);
            if (info.Parent is { } parent) Directory.CreateDirectory(parent.FullName);
            info.Create(DirectorySecurityForUser());
        }
        else
        {
            if (Path.GetDirectoryName(dir) is { } parent) Directory.CreateDirectory(parent);
            Directory.CreateDirectory(dir, DirMode);
            File.SetUnixFileMode(dir, DirMode);
        }
    }

    /// <summary>Only control.json, a clients folder, key files and this class's temp files (top level).</summary>
    private static bool HoldsOnlyCompanionFiles(string dir)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            var name = entry.Name;
            var ok = entry is DirectoryInfo
                ? name == "clients"
                : name == "control.json" || name.EndsWith(".key", StringComparison.Ordinal) || (name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.Ordinal));
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Writes <paramref name="path"/> user-only, replacing any file there. Its folder must exist.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path) ?? throw new ArgumentException("the path has no folder", nameof(path));
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6))}.tmp");
        try
        {
            using (var stream = CreateNew(temp))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true); // a same-folder move keeps the temp file's own rules
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, FileMode);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> (a file or folder) is user-only: on Windows a protected DACL with exactly one rule,
    /// Allow FullControl for the current user; on POSIX mode 0700 (folder) or 0600 (file). False when it does not exist.
    /// </summary>
    public static bool IsUserOnly(string path)
    {
        var isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return IsUserOnlyWindows(path, isDir);
        return File.GetUnixFileMode(path) == (isDir ? DirMode : FileMode);
    }

    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute |
        UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit;

    /// <summary>
    /// POSIX: takes the group and other bits (and setuid/setgid/sticky) off <paramref name="path"/>, keeping the user's own
    /// (so a 0644 file becomes 0600, a 0755 folder 0700, and an executable stays executable for its user). A symlink is
    /// never followed or changed, and a file this user does not own is left alone (chmod refuses it). True when the mode
    /// changed. Windows: does nothing (the data folder there inherits the user profile's rules), false.
    /// </summary>
    public static bool Tighten(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) return false;
            if (!info.Exists && !Directory.Exists(path)) return false;
            var mode = File.GetUnixFileMode(path);
            if ((mode & GroupOrOther) == 0) return false;
            File.SetUnixFileMode(path, mode & ~GroupOrOther);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false; // not this user's (EPERM), or gone meanwhile
        }
    }

    /// <summary>Creates <paramref name="dir"/> (and missing parents) 0700 on POSIX; an existing one is left as it is. Windows: an ordinary folder.</summary>
    public static void CreatePrivateDirectory(string dir)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, DirMode);
    }

    /// <summary>Appends <paramref name="text"/> (UTF-8) to <paramref name="path"/>; a file this creates is 0600 on POSIX.</summary>
    public static void AppendAllText(string path, string text)
    {
        if (OperatingSystem.IsWindows())
        {
            File.AppendAllText(path, text);
            return;
        }
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = System.IO.FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read, UnixCreateMode = FileMode,
        });
        stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Creates an empty <paramref name="path"/> 0600 when it does not exist yet (POSIX), so what opens it later keeps that mode.</summary>
    public static void CreateEmptyIfMissing(string path)
    {
        if (OperatingSystem.IsWindows() || File.Exists(path)) return;
        try
        {
            using var _ = new FileStream(path, new FileStreamOptions
            {
                Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite, UnixCreateMode = FileMode,
            });
        }
        catch (IOException) when (File.Exists(path)) { } // made meanwhile by someone else
    }

    private static FileStream CreateNew(string temp)
    {
        if (OperatingSystem.IsWindows())
            return new FileInfo(temp).Create(System.IO.FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Read, FileShare.None, 4096,
                FileOptions.None, FileSecurityForUser());
        return new FileStream(temp, new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = FileMode,
        });
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("the current Windows user has no SID");
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity DirectorySecurityForUser()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity FileSecurityForUser()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsUserOnlyWindows(string path, bool isDir)
    {
        FileSystemSecurity security = isDir ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected) return false;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        if (rules.Count != 1 || rules[0] is not FileSystemAccessRule rule) return false;
        return rule.AccessControlType == AccessControlType.Allow
            && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl
            && rule.IdentityReference is SecurityIdentifier sid && sid == CurrentUser();
    }
}
