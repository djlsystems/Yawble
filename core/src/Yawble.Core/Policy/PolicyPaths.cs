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
    public static string Canonicalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? "";
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                var next = Path.Combine(current, parts[i]);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (!info.Exists)
                {
                    return Path.Combine([current, .. parts[i..]]);
                }
                var target = info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true);
                current = target is null ? next : Path.GetFullPath(target.FullName);
            }
            return current;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }
}
