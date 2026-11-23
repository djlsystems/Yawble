namespace Yawble.Core.Scripts.Engine;

/// <summary>
/// Where a file step's path points. A relative path is under the member's data folder; an absolute one must be inside
/// the data folder or a folder the team granted (a write needs a writable grant). In a dry run a write goes to the
/// dry-run folder instead, and a read sees what the dry run wrote before it falls back to the real file.
/// </summary>
internal sealed class ScriptFiles
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _data;
    private readonly IReadOnlyList<ScriptFolderGrant> _grants;
    private readonly string? _dry;

    public ScriptFiles(string dataFolder, IReadOnlyList<ScriptFolderGrant> grants, string? dryRunFolder)
    {
        _data = Path.GetFullPath(dataFolder);
        _grants = grants.Select(g => g with { Path = Path.GetFullPath(g.Path) }).ToList();
        _dry = dryRunFolder is null ? null : Path.GetFullPath(dryRunFolder);
    }

    public bool DryRun => _dry is not null;

    /// <summary>The real file a path names (null root: the data folder; otherwise the grant's index).</summary>
    public (string Full, int Root, string Relative) Locate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ScriptStepException("the path is empty");
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_data, path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ScriptStepException($"'{path}' is not a valid path: {e.Message}");
        }
        if (Inside(full, _data, out var rel)) return (full, -1, rel);
        for (var i = 0; i < _grants.Count; i++)
            if (Inside(full, _grants[i].Path, out rel)) return (full, i, rel);
        throw new ScriptStepException($"'{path}' is outside the member's data folder and the team's folder grants");
    }

    /// <summary>The file to read: in a dry run, the copy the dry run wrote when there is one.</summary>
    public string ForRead(string path)
    {
        var (full, root, rel) = Locate(path);
        if (_dry is not null)
        {
            var copy = DryPath(root, rel);
            if (File.Exists(copy) || Tombstone(copy)) return copy;
        }
        return full;
    }

    /// <summary>The file to write: the real one (needs a writable folder), or its dry-run copy.</summary>
    public (string Target, string Real) ForWrite(string path)
    {
        var (full, root, rel) = Locate(path);
        if (root >= 0 && !_grants[root].CanWrite) throw new ScriptStepException($"'{path}' is in a folder the team granted read-only");
        if (rel.Length == 0) throw new ScriptStepException($"'{path}' is a folder, not a file");
        if (_dry is null) return (full, full);
        var copy = DryPath(root, rel);
        // A dry-run append adds to the real file's text, as the live run would.
        if (!File.Exists(copy) && !Tombstone(copy) && File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(full, copy);
        }
        if (Tombstone(copy)) File.Delete(copy + TombstoneSuffix);
        return (copy, full);
    }

    /// <summary>A dry-run delete: the copy goes, and a marker hides the real file from later reads in this dry run.</summary>
    public void DryDelete(string path)
    {
        var (_, root, rel) = Locate(path);
        if (root >= 0 && !_grants[root].CanWrite) throw new ScriptStepException($"'{path}' is in a folder the team granted read-only");
        var copy = DryPath(root, rel);
        if (File.Exists(copy)) File.Delete(copy);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy + TombstoneSuffix, "");
    }

    public void CheckDeletable(string path)
    {
        var (_, root, _) = Locate(path);
        if (root >= 0 && !_grants[root].CanWrite) throw new ScriptStepException($"'{path}' is in a folder the team granted read-only");
    }

    private const string TombstoneSuffix = ".dry-run-deleted";

    private static bool Tombstone(string copy) => File.Exists(copy + TombstoneSuffix);

    private string DryPath(int root, string rel) =>
        Path.Combine(_dry!, root < 0 ? "" : $"granted-{root + 1}", rel);

    private static bool Inside(string full, string root, out string relative)
    {
        relative = "";
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), r, PathComparison)) return true;
        if (!full.StartsWith(r + Path.DirectorySeparatorChar, PathComparison)) return false;
        relative = full[(r.Length + 1)..];
        return true;
    }
}

/// <summary>A step that cannot do what it says (a path outside the folders, a missing file); fails the step with its message.</summary>
public sealed class ScriptStepException(string message) : Exception(message);
