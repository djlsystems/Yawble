using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Yawble.CodeBrowser;

/// <summary>One entry of a folder listing, before the change markers are added.</summary>
public sealed record TreeEntry(string Name, string Path, string Kind, long Size, DateTimeOffset Mtime, bool Hidden);

/// <summary>
/// A member's clone, read only and only from inside. The clone is the AGENT's to write, so every path from the
/// shell is checked here before anything is opened:
/// <list type="bullet">
/// <item>paths are clone-relative with forward slashes; <c>..</c>, <c>.</c>, empty segments, absolute paths, drive
///   letters and (Windows) <c>:</c>, trailing dots and spaces are refused;</item>
/// <item>each segment is matched against the folder's REAL entry names (so a Windows 8.3 short name or another case
///   cannot reach <c>.git</c> or a hidden file by another spelling);</item>
/// <item>symlinks, junctions and every other reparse point are never followed: they are listed as links and refused;</item>
/// <item><c>.git</c> (at any depth) is not listed and not browsable;</item>
/// <item>likely secrets (the deny list, plus the paths the global guardrail policy denies) are listed greyed out and
///   never read;</item>
/// <item>a file is opened, then its final path (Windows: from the open handle) must still be under the root.</item>
/// </list>
/// Nothing here writes to the clone.
/// </summary>
public sealed class SafeTree
{
    /// <summary>Most entries one listing returns (the rest are cut).</summary>
    public const int MaxListEntries = 10_000;

    private static readonly string[] DeniedPatterns =
        [".env*", "*.pem", "*.key", "*.pfx", "*.p12", "id_rsa*", "*.kdbx", "credentials*", "secrets.*"];

    /// <summary>Build output and tool folders: browsable, but never in the change log and never marked.</summary>
    public static readonly IReadOnlySet<string> NoiseFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "out", "target", ".vs", ".idea", ".vscode-test", "__pycache__",
        ".venv", "venv", ".next", ".nuxt", ".svelte-kit", "coverage", ".gradle", ".pytest_cache", ".mypy_cache", ".tox",
        "TestResults", ".turbo", ".parcel-cache", ".cache",
    };

    private readonly Func<string, bool>? _policyDenied;
    private readonly StringComparison _names = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private string? _finalRoot;

    /// <param name="root">The clone's folder (absolute).</param>
    /// <param name="policyDenied">Whether an absolute path is one the global guardrail policy denies (hidden as a secret).</param>
    public SafeTree(string root, Func<string, bool>? policyDenied = null)
    {
        Root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        _policyDenied = policyDenied;
    }

    public string Root { get; }

    public bool Exists => Directory.Exists(Root);

    /// <summary>Whether a file or folder name is on the deny list (likely secrets).</summary>
    public static bool IsDeniedName(string name)
    {
        foreach (var pattern in DeniedPatterns)
            if (Glob(pattern, name)) return true;
        return false;
    }

    /// <summary>Whether a clone-relative path lies in a noise folder (or is .git): no markers, no change log.</summary>
    public static bool IsNoise(string relative)
    {
        foreach (var segment in relative.Split('/'))
            if (NoiseFolders.Contains(segment)) return true;
        return false;
    }

    /// <summary>Whether a relative path (as the watcher gives it) is hidden by name: any segment denied.</summary>
    public bool IsHidden(string relative)
    {
        foreach (var segment in relative.Split('/'))
            if (IsDeniedName(segment)) return true;
        return _policyDenied?.Invoke(Full(relative)) == true;
    }

    /// <summary>The absolute path of a relative one (no checks: for paths this class produced).</summary>
    internal string Full(string relative) =>
        relative.Length == 0 ? Root : System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>The path as the contract spells it: forward slashes, no leading or trailing slash, "" for the root. Refuses what can never be inside.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.Replace('\\', '/');
        if (p.StartsWith('/') || System.IO.Path.IsPathRooted(path) || p.Contains('\0'))
            throw CodeBrowserException.Refuse("only paths inside the clone can be opened");
        if (OperatingSystem.IsWindows() && p.Contains(':'))
            throw CodeBrowserException.Refuse("only paths inside the clone can be opened");
        if (p.EndsWith('/')) p = p[..^1];
        var segments = p.Split('/');
        foreach (var s in segments)
        {
            if (s.Length == 0 || s is "." or "..")
                throw CodeBrowserException.Refuse("only paths inside the clone can be opened ('..' and '.' are not allowed)");
            if (OperatingSystem.IsWindows() && (s.EndsWith('.') || s.EndsWith(' ') || s.IndexOfAny(['*', '?', '<', '>', '|', '"']) >= 0))
                throw CodeBrowserException.Refuse("this path cannot be opened");
            if (s.Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw CodeBrowserException.Refuse(".git is not browsable");
        }
        return p;
    }

    /// <summary>
    /// Walks a relative path from the root, segment by segment, over the folders' real entries. Returns the entry the path
    /// names (the root's own info for ""), with its real relative path. Refuses links, .git, hidden entries, missing ones.
    /// </summary>
    public (FileSystemInfo Info, string Relative) Resolve(string? path, bool allowHidden = false)
    {
        var p = Normalize(path);
        if (!Exists) throw new CodeBrowserException(CodeBrowserException.NoClone, "this member has no clone");
        FileSystemInfo current = new DirectoryInfo(Root);
        if (IsLink(current)) throw CodeBrowserException.Refuse("the clone's folder is a link; it is not opened");
        if (p.Length == 0) return (current, "");
        var real = new List<string>();
        foreach (var segment in p.Split('/'))
        {
            if (current is not DirectoryInfo dir) throw CodeBrowserException.Refuse($"'{string.Join('/', real)}' is not a folder");
            var match = Find(dir, segment) ?? throw CodeBrowserException.Refuse($"'{p}' is not there");
            real.Add(match.Name);
            if (match.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) throw CodeBrowserException.Refuse(".git is not browsable");
            if (IsLink(match)) throw CodeBrowserException.Refuse($"'{string.Join('/', real)}' is a link; links are not opened");
            if (!allowHidden && (IsDeniedName(match.Name) || _policyDenied?.Invoke(match.FullName) == true))
                throw CodeBrowserException.Refuse($"'{string.Join('/', real)}' is hidden: it may hold secrets");
            current = match;
        }
        var relative = string.Join('/', real);
        var full = System.IO.Path.GetFullPath(current.FullName);
        if (!IsUnder(full, Root)) throw CodeBrowserException.Refuse("only paths inside the clone can be opened");
        return (current, relative);
    }

    /// <summary>A folder's entries: folders first, then files and links, by name; .git left out; links never followed.</summary>
    public IReadOnlyList<TreeEntry> List(string? path)
    {
        var (info, relative) = Resolve(path);
        if (info is not DirectoryInfo dir) throw CodeBrowserException.Refuse($"'{relative}' is not a folder");
        // The folder is enumerated by its path, so a folder swapped for a link while it is listed could show another
        // folder's names: it is opened before and after the listing, and both handles must name the same folder, under
        // the root (final paths from the handles), or nothing is shown.
        var before = FolderIdentity(dir.FullName);
        var listed = SafeEnumerate(dir);
        AfterEnumerate?.Invoke(dir.FullName);
        var after = FolderIdentity(dir.FullName);
        if (before != after) throw CodeBrowserException.Refuse($"'{relative}' changed while it was listed; it is not shown");
        var entries = new List<TreeEntry>();
        foreach (var e in listed)
        {
            if (e.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            if (entries.Count >= MaxListEntries) break;
            var rel = relative.Length == 0 ? e.Name : relative + "/" + e.Name;
            var link = IsLink(e);
            var kind = link ? "link" : e is DirectoryInfo ? "dir" : "file";
            var hidden = IsDeniedName(e.Name) || _policyDenied?.Invoke(e.FullName) == true;
            long size = 0;
            DateTimeOffset mtime;
            try
            {
                size = !link && e is FileInfo f ? f.Length : 0;
                mtime = new DateTimeOffset(e.LastWriteTimeUtc, TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                mtime = DateTimeOffset.UnixEpoch;
            }
            entries.Add(new TreeEntry(e.Name, rel, kind, size, mtime, hidden));
        }
        return entries
            .OrderBy(e => e.Kind == "dir" ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Opens a file for reading (shared, so the agent can keep writing). After opening, the handle's final path must
    /// still be under the clone (a file swapped for a link between the check and the open is refused).
    /// </summary>
    public (FileStream Stream, string Relative) OpenFile(string? path)
    {
        var (info, relative) = Resolve(path);
        if (info is not FileInfo file) throw CodeBrowserException.Refuse($"'{relative}' is not a file");
        FileStream stream;
        try
        {
            stream = new FileStream(file.FullName, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.SequentialScan,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw CodeBrowserException.Refuse($"'{relative}' cannot be read: {e.Message}");
        }
        try
        {
            CheckOpened(stream, file.FullName);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        return (stream, relative);
    }

    private void CheckOpened(FileStream stream, string fullName)
    {
        if (OperatingSystem.IsWindows())
        {
            var final = HandlePath(stream.SafeFileHandle);
            var root = FinalRoot();
            if (final is null || root is null || !IsUnder(final, root) || final.Length <= root.Length)
                throw CodeBrowserException.Refuse("the file is not inside the clone any more");
            // The file actually opened (not only the path asked for) must not be in .git or look like a secret.
            foreach (var segment in final[(root.Length + 1)..].Split(System.IO.Path.DirectorySeparatorChar))
            {
                if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase)) throw CodeBrowserException.Refuse(".git is not browsable");
                if (IsDeniedName(segment)) throw CodeBrowserException.Refuse("the file is hidden: it may hold secrets");
            }
            if (_policyDenied?.Invoke(final) == true) throw CodeBrowserException.Refuse("the file is hidden: it may hold secrets");
            // A hard link is the same file as one somewhere else (outside the clone, or a secret): never read.
            if (LinkCount(stream.SafeFileHandle) is not 1)
                throw CodeBrowserException.Refuse("the file is a hard link to another file; it is not opened");
        }
        else
        {
            // No handle path on every POSIX: the entry must still be a plain file (not swapped for a link) after the open.
            var again = new FileInfo(fullName);
            if (!again.Exists || IsLink(again)) throw CodeBrowserException.Refuse("the file changed into a link; it is not opened");
        }
    }

    /// <summary>Every file under the clone that can carry markers: no noise folders, no links, no hidden entries. Bounded.</summary>
    public IEnumerable<(string Relative, FileInfo Info)> WalkFiles(int max = 50_000)
    {
        if (!Exists) yield break;
        var stack = new Stack<(DirectoryInfo Dir, string Rel)>();
        stack.Push((new DirectoryInfo(Root), ""));
        var count = 0;
        while (stack.Count > 0)
        {
            var (dir, rel) = stack.Pop();
            foreach (var e in SafeEnumerate(dir))
            {
                if (NoiseFolders.Contains(e.Name) || IsLink(e) || IsDeniedName(e.Name)) continue;
                var childRel = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
                if (_policyDenied?.Invoke(e.FullName) == true) continue;
                if (e is DirectoryInfo d) stack.Push((d, childRel));
                else if (e is FileInfo f)
                {
                    if (++count > max) yield break;
                    yield return (childRel, f);
                }
            }
        }
    }

    private FileSystemInfo? Find(DirectoryInfo dir, string name)
    {
        FileSystemInfo? caseInsensitive = null;
        foreach (var e in SafeEnumerate(dir))
        {
            if (string.Equals(e.Name, name, StringComparison.Ordinal)) return e;
            if (_names == StringComparison.OrdinalIgnoreCase && caseInsensitive is null && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                caseInsensitive = e;
        }
        return caseInsensitive;
    }

    private static IEnumerable<FileSystemInfo> SafeEnumerate(DirectoryInfo dir)
    {
        IEnumerable<FileSystemInfo> all;
        try
        {
            all = dir.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false,
                MatchType = MatchType.Simple,
            }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return all;
    }

    /// <summary>A symlink, a junction or any other reparse point: never followed.</summary>
    internal static bool IsLink(FileSystemInfo e)
    {
        try
        {
            return e.Attributes.HasFlag(FileAttributes.ReparsePoint) || e.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal static bool IsUnder(string path, string root)
    {
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var r = System.IO.Path.TrimEndingDirectorySeparator(root);
        return path.Equals(r, cmp) || path.StartsWith(r + System.IO.Path.DirectorySeparatorChar, cmp);
    }

    /// <summary>A '*'-only glob on a name, case-insensitive.</summary>
    internal static bool Glob(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n])) { p++; n++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = n; }
            else if (star >= 0) { p = star + 1; n = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    // ------------------------------------------------------------------ seams (tests)

    /// <summary>The final path of an open handle (tests stand in a swapped folder here).</summary>
    internal Func<SafeFileHandle, string?> HandlePath { get; set; } = FinalPath;

    /// <summary>How many names an open file has (tests may stand in a hard link here).</summary>
    internal Func<SafeFileHandle, uint?> LinkCount { get; set; } = NumberOfLinks;

    /// <summary>Runs between a folder's enumeration and its second check (tests swap the folder here).</summary>
    internal Action<string>? AfterEnumerate { get; set; }

    private string? FinalRoot()
    {
        if (_finalRoot is not null) return _finalRoot;
        using var handle = OpenFolder(Root);
        return _finalRoot = handle.IsInvalid ? null : FinalPath(handle);
    }

    /// <summary>
    /// The folder a path names now, opened (links followed): its final path, which must be under the root and not in
    /// .git, and its file id. Not Windows: the entry must still be a folder, not a link.
    /// </summary>
    private (string Path, ulong Volume, ulong Index) FolderIdentity(string full)
    {
        if (!OperatingSystem.IsWindows())
        {
            var d = new DirectoryInfo(full);
            if (!d.Exists || IsLink(d)) throw CodeBrowserException.Refuse("the folder changed into a link; it is not listed");
            return (full, 0, 0);
        }
        using var handle = OpenFolder(full);
        var root = FinalRoot();
        var final = handle.IsInvalid ? null : HandlePath(handle);
        if (final is null || root is null || !IsUnder(final, root))
            throw CodeBrowserException.Refuse("the folder is not inside the clone any more");
        if (final.Length > root.Length
            && final[(root.Length + 1)..].Split(System.IO.Path.DirectorySeparatorChar).Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw CodeBrowserException.Refuse(".git is not browsable");
        if (!GetFileInformationByHandle(handle, out var fi)) throw CodeBrowserException.Refuse("the folder cannot be read");
        return (final, fi.VolumeSerialNumber, ((ulong)fi.FileIndexHigh << 32) | fi.FileIndexLow);
    }

    private static SafeFileHandle OpenFolder(string path) =>
        CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);

    private static uint? NumberOfLinks(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var fi) ? fi.NumberOfLinks : null;

    // ------------------------------------------------------------------ Windows: the final path of an open handle

    private static string? FinalPath(SafeFileHandle handle)
    {
        var sb = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
        if (n == 0) return null;
        if (n >= sb.Capacity)
        {
            sb = new StringBuilder((int)n + 1);
            n = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
            if (n == 0) return null;
        }
        return StripPrefix(sb.ToString());
    }

    private static string StripPrefix(string p) =>
        p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + p[8..]
        : p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..]
        : p;

    private const uint BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode, IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
