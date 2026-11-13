using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Yawble.Core.Companion;

/// <summary>
/// Folders and files only the current user can open: the companion's control.json, its clients folder and key files.
/// Windows: a protected DACL (nothing inherited) holding exactly one rule, FullControl for the current user's SID, set when
/// the folder or file is created (a file never exists with the folder's inherited rules). POSIX: 0700 folders, 0600 files.
/// A file is written to a temp file beside it and then moved over the old one, so a reader never sees half a file.
/// Link-compiled into the yawble client: BCL only, AOT-clean.
/// </summary>
public static class UserOnlyFile
{
    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates <paramref name="dir"/> user-only (its parents keep their own rules). An existing folder that is already
    /// user-only is left alone; one that is empty or holds only the companion's own files (control.json, clients\, *.key,
    /// its .tmp files) is made user-only; any other existing folder is refused with <see cref="InvalidOperationException"/>
    /// and left exactly as it was: re-securing it would rewrite the rules of everything inside (a profile, a repository).
    /// </summary>
    public static void EnsureDirectory(string dir)
    {
        dir = Path.GetFullPath(dir);
        if (Directory.Exists(dir))
        {
            if (IsUserOnly(dir)) return;
            if (!HoldsOnlyCompanionFiles(dir))
                throw new InvalidOperationException(
                    $"the folder '{dir}' already holds other files, so Yawble will not change who can open it; " +
                    "point YAWBLE_HOME at a new or empty folder for the companion's files");
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
