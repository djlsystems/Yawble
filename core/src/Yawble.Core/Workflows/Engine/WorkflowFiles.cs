namespace Yawble.Core.Workflows.Engine;

/// <summary>
/// Where a file step's path points. A relative path is under the member's data folder; an absolute one must be inside
/// the data folder or a folder the team granted (a write needs a writable grant). In a dry run a write goes to the
/// dry-run folder instead, and a read sees what the dry run wrote before it falls back to the real file.
/// A path that starts <c>docs:/</c> is under the team's Documents folder (writable), when the run has one.
/// </summary>
internal sealed class WorkflowFiles
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The prefix of a path in the team's Documents folder: <c>docs:/smoke/smokesessions.json</c>.</summary>
    public const string DocsPrefix = "docs:/";

    /// <summary>The root index of the team's Documents folder (-1 is the data folder, 0 and up the grants).</summary>
    private const int DocsRoot = -2;

    /// <summary>Where a dry run keeps its copies of the Documents folder's files, under the dry-run folder.</summary>
    private const string DryDocsFolder = "team-docs";

    private readonly string _data;
    private readonly IReadOnlyList<WorkflowFolderGrant> _grants;
    private readonly string? _dry;
    private readonly string? _docs;

    public WorkflowFiles(string dataFolder, IReadOnlyList<WorkflowFolderGrant> grants, string? dryRunFolder, string? teamDocuments = null)
    {
        _data = Path.GetFullPath(dataFolder);
        _grants = grants.Select(g => g with { Path = Path.GetFullPath(g.Path) }).ToList();
        _dry = dryRunFolder is null ? null : Path.GetFullPath(dryRunFolder);
        _docs = string.IsNullOrWhiteSpace(teamDocuments) ? null : Path.GetFullPath(teamDocuments);
    }

    public bool DryRun => _dry is not null;

    /// <summary>Whether a path names the team's Documents folder (it starts "docs:", in any case), well formed or not.</summary>
    public static bool IsDocsPath(string? path) => path is not null && path.StartsWith("docs:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What is wrong with a "docs:" path, or null when it is well formed: it must start "docs:/", the rest is a path inside the
    /// folder (no "..", no drive, not absolute), and a step that needs a file cannot name the folder itself.
    /// </summary>
    public static string? DocsPathProblem(string path, bool needsFile)
    {
        if (!path.StartsWith(DocsPrefix, StringComparison.OrdinalIgnoreCase))
            return $"'{path}' is not a Documents path: write docs:/ and then the path inside the team's Documents folder (e.g. docs:/smoke/smokesessions.json)";
        var rest = path[DocsPrefix.Length..];
        if (rest.StartsWith('/') || rest.StartsWith('\\') || rest.Contains(':') || Path.IsPathRooted(rest))
            return $"'{path}': after docs:/ comes a path inside the team's Documents folder, not an absolute path";
        if (rest.Split('/', '\\').Any(s => s == ".."))
            return $"'{path}': a Documents path cannot go up with '..'";
        if (needsFile && rest.Trim('/', '\\').Length == 0)
            return $"'{path}' is the Documents folder itself, not a file (e.g. docs:/smoke/smokesessions.json)";
        return null;
    }

    /// <summary>The real file a path names (root -1: the data folder; -2: the team's Documents folder; otherwise the grant's index).</summary>
    public (string Full, int Root, string Relative) Locate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new WorkflowStepException("the path is empty");
        if (IsDocsPath(path)) return LocateDocs(path);
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_data, path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkflowStepException($"'{path}' is not a valid path: {e.Message}");
        }
        if (Inside(full, _data, out var rel)) return NoLinks(path, full, -1, rel);
        if (_docs is not null && Inside(full, _docs, out rel)) return NoLinks(path, full, DocsRoot, rel);
        for (var i = 0; i < _grants.Count; i++)
            if (Inside(full, _grants[i].Path, out rel)) return NoLinks(path, full, i, rel);
        throw new WorkflowStepException($"'{path}' is outside the member's data folder and the team's folder grants");
    }

    /// <summary>
    /// The located path, once no part of it below its root is a link or junction: one in a folder (Documents above all, which
    /// agents write) could lead anywhere, so it is refused, never followed.
    /// </summary>
    private (string Full, int Root, string Relative) NoLinks(string path, string full, int root, string rel)
    {
        var rootPath = root == -1 ? _data : root == DocsRoot ? _docs! : _grants[root].Path;
        if (Ops.PathLinks.FirstLink(rootPath, rel) is not null)
            throw new WorkflowStepException($"'{path}' goes through a link, which is not followed");
        return (full, root, rel);
    }

    private (string Full, int Root, string Relative) LocateDocs(string path)
    {
        if (DocsPathProblem(path, needsFile: false) is { } problem) throw new WorkflowStepException(problem);
        if (_docs is null)
            throw new WorkflowStepException($"'{path}' is in the team's Documents folder, and this run has none (docs:/ paths work in a team's workflow members)");
        string full;
        try { full = Path.GetFullPath(Path.Combine(_docs, path[DocsPrefix.Length..])); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkflowStepException($"'{path}' is not a valid path: {e.Message}");
        }
        if (!Inside(full, _docs, out var rel)) throw new WorkflowStepException($"'{path}' leaves the team's Documents folder");
        return NoLinks(path, full, DocsRoot, rel);
    }

    /// <summary>The file to read: in a dry run, the copy the dry run wrote when there is one.</summary>
    public string ForRead(string path)
    {
        var (full, root, rel) = Locate(path);
        return ReadAt(full, root, rel);
    }

    private string ReadAt(string full, int root, string rel)
    {
        if (_dry is not null)
        {
            var copy = DryPath(root, rel);
            if (File.Exists(copy) || Tombstone(copy)) return copy;
        }
        return full;
    }

    /// <summary>
    /// The file a move or copy takes: it must exist (in a dry run, as the dry run sees it) and be in a writable folder.
    /// <c>Read</c> is the file to read its bytes from (a dry-run copy when there is one), <c>Real</c> the real path.
    /// </summary>
    public (string Read, string Real) ForSource(string path)
    {
        var (full, root, rel) = Locate(path);
        RequireWritable(path, root);
        if (rel.Length == 0 || FolderAt(full, root, rel)) throw new WorkflowStepException($"'{path}' is a folder, not a file");
        var read = ReadAt(full, root, rel);
        if (!File.Exists(read)) throw new WorkflowStepException($"'{path}' does not exist");
        return (read, full);
    }

    /// <summary>
    /// The real path a move or copy of <paramref name="sourceReal"/> goes to. A <paramref name="to"/> that ends in a separator or
    /// names an existing folder means the source's name inside it. A file already there, without <paramref name="overwrite"/>,
    /// gives the first free "name (1).ext", "name (2).ext"... A move onto itself stays where it is; a copy onto itself takes a new name.
    /// </summary>
    public string ForTarget(string to, string sourceReal, bool overwrite, bool move)
    {
        var (full, root, rel) = Locate(to);
        RequireWritable(to, root);
        if (to.EndsWith('/') || to.EndsWith('\\') || rel.Length == 0 || FolderAt(full, root, rel))
            (full, root, rel) = Locate(Path.Combine(full, Path.GetFileName(sourceReal)));
        if (string.Equals(full, sourceReal, PathComparison))
        {
            if (move) return full;
        }
        else if (overwrite || !TakenAt(full, root, rel)) return full;

        var folder = Path.GetDirectoryName(full)!;
        var stem = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            var (_, r, rl) = Locate(candidate);
            if (!TakenAt(candidate, r, rl)) return candidate;
        }
    }

    /// <summary>A dry-run move or copy: the bytes go to the target's dry-run copy, which later steps in this dry run see.</summary>
    public string DryPut(string sourceRead, string targetReal)
    {
        var (_, root, rel) = Locate(targetReal);
        var copy = DryPath(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        if (Tombstone(copy)) File.Delete(copy + TombstoneSuffix);
        if (!string.Equals(Path.GetFullPath(sourceRead), copy, PathComparison)) File.Copy(sourceRead, copy, overwrite: true);
        return copy;
    }

    /// <summary>
    /// The files in a folder whose names match one of the patterns (* and ?, any case), in its subfolders too when recursive,
    /// sorted by path. In a dry run the list is what the dry run sees: its copies added, the files it deleted or moved away gone.
    /// </summary>
    public IReadOnlyList<(string Path, FileInfo Info)> List(string path, IReadOnlyList<string> patterns, bool recursive)
    {
        var (full, root, rel) = Locate(path);
        var dryFolder = _dry is null ? null : DryPath(root, rel);
        var realDir = Directory.Exists(full);
        var dryDir = dryFolder is not null && Directory.Exists(dryFolder);
        if (!realDir && !dryDir)
            throw new WorkflowStepException(File.Exists(full) ? $"'{path}' is a file, not a folder" : $"the folder '{path}' does not exist");

        bool Matches(string name) => patterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));
        var found = new Dictionary<string, FileInfo>(PathComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        if (realDir)
            foreach (var f in Files(full, recursive))
                if (Matches(f.Name) && (_dry is null || !Inside(f.FullName, _dry, out _))) found[f.FullName] = f;
        if (dryDir)
            foreach (var f in Files(dryFolder!, recursive))
            {
                var inner = Path.GetRelativePath(dryFolder!, f.FullName);
                // The dry run keeps granted folders' copies under granted-N in its root: they are not the data folder's files.
                if (root == -1 && rel.Length == 0 && inner.Contains(Path.DirectorySeparatorChar)
                    && (inner.StartsWith("granted-", StringComparison.Ordinal) || inner.StartsWith(DryDocsFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal))) continue;
                if (f.Name.EndsWith(TombstoneSuffix, StringComparison.Ordinal)) found.Remove(Path.Combine(full, inner[..^TombstoneSuffix.Length]));
                else if (Matches(f.Name)) found[Path.Combine(full, inner)] = f;
            }
        return found.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value)).ToList();
    }

    /// <summary>
    /// The files in a folder (its subfolders too when recursive). Links and junctions are neither listed nor entered (one could lead
    /// out of the folder, or loop); hidden and system files are not listed either.
    /// </summary>
    private static IEnumerable<FileInfo> Files(string folder, bool recursive) =>
        new System.IO.Enumeration.FileSystemEnumerable<FileInfo>(folder, (ref System.IO.Enumeration.FileSystemEntry e) => (FileInfo)e.ToFileSystemInfo(),
            new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            })
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => !e.IsDirectory && !Ops.PathLinks.IsLink(ref e),
            ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => !Ops.PathLinks.IsLink(ref e),
        };

    private void RequireWritable(string path, int root)
    {
        if (root >= 0 && !_grants[root].CanWrite) throw new WorkflowStepException($"'{path}' is in a folder the team granted read-only");
    }

    /// <summary>Whether a file or folder is at the path, as this run sees it.</summary>
    private bool TakenAt(string full, int root, string rel) => FileAt(full, root, rel) || FolderAt(full, root, rel);

    private bool FileAt(string full, int root, string rel)
    {
        if (_dry is not null)
        {
            var copy = DryPath(root, rel);
            if (File.Exists(copy)) return true;
            if (Tombstone(copy)) return false;
        }
        return File.Exists(full);
    }

    private bool FolderAt(string full, int root, string rel) =>
        Directory.Exists(full) || (_dry is not null && rel.Length > 0 && Directory.Exists(DryPath(root, rel)));

    /// <summary>The file to write: the real one (needs a writable folder), or its dry-run copy.</summary>
    public (string Target, string Real) ForWrite(string path)
    {
        var (full, root, rel) = Locate(path);
        if (root >= 0 && !_grants[root].CanWrite) throw new WorkflowStepException($"'{path}' is in a folder the team granted read-only");
        if (rel.Length == 0) throw new WorkflowStepException($"'{path}' is a folder, not a file");
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
        if (root >= 0 && !_grants[root].CanWrite) throw new WorkflowStepException($"'{path}' is in a folder the team granted read-only");
        var copy = DryPath(root, rel);
        if (File.Exists(copy)) File.Delete(copy);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy + TombstoneSuffix, "");
    }

    public void CheckDeletable(string path)
    {
        var (_, root, _) = Locate(path);
        if (root >= 0 && !_grants[root].CanWrite) throw new WorkflowStepException($"'{path}' is in a folder the team granted read-only");
    }

    private const string TombstoneSuffix = ".dry-run-deleted";

    private static bool Tombstone(string copy) => File.Exists(copy + TombstoneSuffix);

    private string DryPath(int root, string rel) =>
        Path.Combine(_dry!, root == DocsRoot ? DryDocsFolder : root < 0 ? "" : $"granted-{root + 1}", rel);

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
public sealed class WorkflowStepException(string message) : Exception(message);
