namespace Yawble.Core.Policy;

/// <summary>
/// Path rules for one target OS, independent of the host the code runs on (a Windows request is compiled and
/// tested on Linux). Paths are lexically normalised here; symlinks are resolved by a canonicaliser
/// (<see cref="HostPathCanonicalizer"/> on the real host, identity in golden tests) before any comparison.
/// </summary>
public sealed class PolicyPaths
{
    private readonly Func<string, string> canonicalize;

    public PolicyPaths(TargetOs os, string home, Func<string, string>? canonicalize = null)
    {
        Os = os;
        Home = Normalize(home) ?? throw new ArgumentException($"home must be an absolute path: {home}", nameof(home));
        this.canonicalize = canonicalize ?? (p => p);
    }

    public TargetOs Os { get; }
    public string Home { get; }

    private char Sep => Os == TargetOs.Windows ? '\\' : '/';

    /// <summary>Windows and macOS (APFS default) compare paths case-insensitively; Linux does not.</summary>
    public StringComparison Comparison => Os == TargetOs.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public StringComparer Comparer => Os == TargetOs.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Expands a leading <c>~</c> to the person's home, normalises, then canonicalises.
    /// Returns null for a relative or otherwise unusable path.
    /// </summary>
    public string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        if (p == "~") p = Home;
        else if (p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
            p = Home + Sep + p[2..];
        var normalized = Normalize(p);
        if (normalized is null) return null;
        return Normalize(canonicalize(normalized));
    }

    /// <summary>Lexical normalisation: separators, "." and "..", trailing separators, drive letter case.</summary>
    public string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.IndexOf('\0') >= 0) return null;
        if (Os == TargetOs.Windows)
        {
            var p = path.Replace('/', '\\');
            string root;
            string rest;
            if (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\')
            {
                root = char.ToUpperInvariant(p[0]) + ":\\";
                rest = p[3..];
            }
            else if (p.StartsWith(@"\\", StringComparison.Ordinal) && !p.StartsWith(@"\\?\", StringComparison.Ordinal) && !p.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                var parts = p[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return null;
                root = @"\\" + parts[0] + "\\" + parts[1] + "\\";
                rest = string.Join('\\', parts.Skip(2));
            }
            else
            {
                return null;
            }
            var joined = Collapse(rest.Split('\\'), root);
            return joined;
        }
        if (!path.StartsWith('/')) return null;
        return Collapse(path.Split('/'), "/");
    }

    private string? Collapse(string[] segments, string root)
    {
        var stack = new List<string>();
        foreach (var s in segments)
        {
            if (s.Length == 0 || s == ".") continue;
            if (s == "..")
            {
                if (stack.Count == 0) return null; // above the root
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(s);
        }
        return stack.Count == 0 ? root : root + string.Join(Sep, stack);
    }

    public bool Same(string a, string b) => string.Equals(a, b, Comparison);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies under it.</summary>
    public bool IsWithin(string path, string root)
    {
        if (Same(path, root)) return true;
        var prefix = root.EndsWith(Sep) ? root : root + Sep;
        return path.StartsWith(prefix, Comparison);
    }

    /// <summary>True when either path contains the other (or they are equal).</summary>
    public bool Overlaps(string a, string b) => IsWithin(a, b) || IsWithin(b, a);

    public string Combine(string root, params string[] parts)
    {
        var all = root.TrimEnd(Sep);
        foreach (var part in parts) all += Sep + part;
        return Normalize(all) ?? throw new ArgumentException($"not an absolute path: {all}");
    }
}

/// <summary>
/// Resolves symlinks on the host: the longest existing prefix of the path is resolved component by component
/// (so a link anywhere in the chain is followed), and the missing tail is appended unchanged.
/// </summary>
public static class HostPathCanonicalizer
{
    /// <summary>The most links followed for one path before giving up (as the OS's own ELOOP limit).</summary>
    private const int MaxLinks = 40;

    /// <summary>The canonical path, or the path unchanged when it cannot be canonicalised (use for display and grants).</summary>
    public static string Canonicalize(string path) => TryCanonicalize(path) ?? path;

    /// <summary>
    /// The canonical path, or null when it cannot be canonicalised (an unreadable link, a loop, a bad path). A security
    /// check on a queried path uses this and fails closed on null: comparing an uncanonicalised path against a
    /// canonicalised list misses through any symlink (macOS /var, /tmp, /etc; any symlinked folder on Linux).
    /// On Linux and macOS a ".." is taken as the OS takes it: after a link it goes to the link's real parent, not back
    /// lexically (l -> s/../y with s -> deep/dir is deep/y). Windows resolves ".." lexically, so there it is collapsed first.
    /// </summary>
    public static string? TryCanonicalize(string path) => TryCanonicalize(path, physicalDotDot: !OperatingSystem.IsWindows());

    /// <summary>
    /// <paramref name="physicalDotDot"/> true: the POSIX walk (each ".." in the path or in a link target goes to the real
    /// parent of what has been resolved so far). False: the Windows rule (".." collapsed lexically before links are
    /// followed). Exposed so the POSIX walk is tested on Windows too.
    /// </summary>
    internal static string? TryCanonicalize(string path, bool physicalDotDot)
    {
        try
        {
            return physicalDotDot ? Physical(path) : Lexical(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Lexical(string path)
    {
        var full = Path.GetFullPath(path);
        var links = 0;
        while (true)
        {
            var root = Path.GetPathRoot(full) ?? "";
            var parts = Split(full[root.Length..]);
            var current = root;
            string? restart = null;
            for (var i = 0; i < parts.Length; i++)
            {
                var next = Path.Combine(current, parts[i]);
                var info = Info(next);
                if (info.LinkTarget is null && !info.Exists)
                {
                    // The deepest existing parent is canonical; the missing tail is appended unchanged.
                    return Path.Combine([current, .. parts[i..]]);
                }
                if (info.LinkTarget is { } target)
                {
                    // Follow one link and start again from the root: the target may itself run through links.
                    if (++links > MaxLinks) return null;
                    var resolved = Path.IsPathRooted(target) ? target : Path.Combine(current, target);
                    restart = Path.GetFullPath(Path.Combine([resolved, .. parts[(i + 1)..]]));
                    break;
                }
                current = next;
            }
            if (restart is null) return current;
            full = restart;
        }
    }

    /// <summary>
    /// The POSIX walk: components are taken one at a time from a pending list. A link puts its target's components at
    /// the front of the list (from the root for an absolute target, from the link's own folder for a relative one), and
    /// ".." goes to the parent of the real folder reached so far.
    /// </summary>
    private static string? Physical(string path)
    {
        var full = Path.IsPathFullyQualified(path) ? path
            : Path.IsPathRooted(path) ? Path.GetFullPath(path)
            : Path.Combine(Directory.GetCurrentDirectory(), path);
        var current = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(current)) return null;
        var pending = new List<string>(Split(full[current.Length..]));
        var links = 0;
        while (pending.Count > 0)
        {
            var part = pending[0];
            pending.RemoveAt(0);
            if (part == ".") continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current; // ".." at the root stays at the root
                continue;
            }
            var next = Path.Combine(current, part);
            var info = Info(next);
            if (info.LinkTarget is { } target)
            {
                if (++links > MaxLinks) return null;
                var targetRoot = Path.IsPathRooted(target) ? Path.GetPathRoot(target) ?? "" : "";
                if (targetRoot.Length > 0) current = targetRoot;
                pending.InsertRange(0, Split(target[targetRoot.Length..]));
                continue;
            }
            if (!info.Exists)
            {
                // The deepest existing folder is canonical and the missing tail is appended. A ".." left in the tail
                // (from a link target) is collapsed and the result walked again, so a link it then leads to is followed.
                List<string> tail = [part, .. pending];
                if (!tail.Contains("..")) return Path.Combine([current, .. tail]);
                if (++links > MaxLinks) return null;
                var again = Path.GetFullPath(Path.Combine([current, .. tail]));
                current = Path.GetPathRoot(again);
                if (string.IsNullOrEmpty(current)) return null;
                pending = [.. Split(again[current.Length..])];
                continue;
            }
            current = next;
        }
        return current;
    }

    private static FileSystemInfo Info(string path) => Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

    private static string[] Split(string relative) =>
        relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
}
