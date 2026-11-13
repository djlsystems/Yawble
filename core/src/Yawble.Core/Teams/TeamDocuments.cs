using System.Text;
using System.Text.Json.Nodes;

namespace Yawble.Core.Teams;

/// <summary>
/// A team's shared documents folder:&lt;team root&gt;/documents, where the team writes what
/// the person should read (reports, specs, plans) and keeps what it needs between runs. Every member reads and writes it
/// as plain files ($YAWBLE_TEAM_DOCS); the person browses, reads, edits, uploads, renames and deletes here. Paths are always
/// relative to the folder: anything that would leave it, or pass through a link, is refused.
/// </summary>
public sealed class TeamDocuments(TeamRepos repos)
{
    public const string Folder = "documents";
    public const long MaxUploadBytes = 25L * 1024 * 1024;
    public const long MaxInlineBytes = 2L * 1024 * 1024;

    private static readonly HashSet<string> TextKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".tsv", ".json", ".yaml", ".yml", ".xml", ".toml", ".ini", ".cfg", ".conf", ".sh", ".ps1",
        ".bat", ".cmd", ".py", ".js", ".ts", ".tsx", ".jsx", ".vue", ".cs", ".csproj", ".sln", ".java", ".go", ".rs", ".rb",
        ".php", ".c", ".h", ".cpp", ".hpp", ".css", ".scss", ".sql", ".html", ".htm", ".env", ".gitignore",
    };

    private static readonly Dictionary<string, string> ImageKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml", [".bmp"] = "image/bmp",
    };

    /// <summary>The team's documents folder (made when first asked for).</summary>
    public string Root(string team)
    {
        var root = Path.Combine(repos.TeamRoot(team), Folder);
        Directory.CreateDirectory(root);
        return root;
    }

    public JsonObject List(string team, string? path)
    {
        var root = Root(team);
        var dir = Resolve(root, path, mustExist: true);
        if (!Directory.Exists(dir)) throw new DocumentsException("not_a_folder", $"'{path}' is not a folder");
        var entries = new JsonArray();
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos()
                     .Where(i => (i.Attributes & FileAttributes.ReparsePoint) == 0)
                     .OrderBy(i => i is FileInfo).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(new JsonObject
            {
                ["name"] = info.Name,
                ["path"] = Relative(root, info.FullName),
                ["folder"] = info is DirectoryInfo,
                ["size"] = info is FileInfo f ? f.Length : null,
                ["modified"] = info.LastWriteTimeUtc.ToString("O"),
            });
        }
        return new JsonObject { ["path"] = Relative(root, dir), ["entries"] = entries };
    }

    /// <summary>A file as the viewer shows it: markdown and text inline, images as a data URL, anything else described.</summary>
    public JsonObject Read(string team, string path)
    {
        var root = Root(team);
        var file = Resolve(root, path, mustExist: true);
        if (!File.Exists(file)) throw new DocumentsException("not_a_file", $"'{path}' is not a file");
        var info = new FileInfo(file);
        var ext = info.Extension;
        var result = new JsonObject { ["path"] = Relative(root, file), ["name"] = info.Name, ["size"] = info.Length, ["modified"] = info.LastWriteTimeUtc.ToString("O") };
        if (info.Length > MaxInlineBytes)
        {
            result["kind"] = "large";
            return result;
        }
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            result["kind"] = "markdown";
            result["text"] = File.ReadAllText(file);
        }
        else if (ImageKinds.TryGetValue(ext, out var mime))
        {
            result["kind"] = "image";
            result["dataUrl"] = $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}";
        }
        else if (TextKinds.Contains(ext) || ext.Length == 0 || LooksLikeText(file))
        {
            result["kind"] = "text";
            result["text"] = File.ReadAllText(file);
        }
        else
        {
            result["kind"] = "binary";
        }
        return result;
    }

    /// <summary>Writes a text file (a new one, or an edit), creating its folder.</summary>
    public JsonObject Write(string team, string path, string text)
    {
        var root = Root(team);
        var file = Resolve(root, path, mustExist: false);
        if (Directory.Exists(file)) throw new DocumentsException("is_a_folder", $"'{path}' is a folder");
        if (Encoding.UTF8.GetByteCount(text) > MaxUploadBytes) throw new DocumentsException("too_large", "at most 25 MB");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return Read(team, Relative(root, file));
    }

    /// <summary>Adds a file the person uploaded into a folder; a name already there gets " (2)", " (3)"... unless replacing.</summary>
    public JsonObject Upload(string team, string? folder, string name, string base64, bool replace)
    {
        var root = Root(team);
        var dir = Resolve(root, folder, mustExist: true);
        if (!Directory.Exists(dir)) throw new DocumentsException("not_a_folder", $"'{folder}' is not a folder");
        var clean = CleanName(name);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new DocumentsException("bad_params", "the file's content did not arrive whole"); }
        if (bytes.LongLength > MaxUploadBytes) throw new DocumentsException("too_large", $"'{clean}' is larger than 25 MB");
        var target = Path.Combine(dir, clean);
        if (!replace)
        {
            var stem = Path.GetFileNameWithoutExtension(clean);
            var ext = Path.GetExtension(clean);
            for (var i = 2; File.Exists(target) || Directory.Exists(target); i++) target = Path.Combine(dir, $"{stem} ({i}){ext}");
        }
        else if (Directory.Exists(target))
        {
            throw new DocumentsException("is_a_folder", $"'{clean}' is a folder");
        }
        File.WriteAllBytes(target, bytes);
        return new JsonObject { ["path"] = Relative(root, target) };
    }

    public JsonObject MakeFolder(string team, string? parent, string name)
    {
        var root = Root(team);
        var dir = Resolve(root, parent, mustExist: true);
        var target = Path.Combine(dir, CleanName(name));
        if (File.Exists(target) || Directory.Exists(target)) throw new DocumentsException("exists", $"'{name}' is already there");
        Directory.CreateDirectory(target);
        return new JsonObject { ["path"] = Relative(root, target) };
    }

    public JsonObject Rename(string team, string path, string newName)
    {
        var root = Root(team);
        var from = Resolve(root, path, mustExist: true);
        if (PathsEqual(from, root)) throw new DocumentsException("not_allowed", "the documents folder itself cannot be renamed");
        var to = Path.Combine(Path.GetDirectoryName(from)!, CleanName(newName));
        if (PathsEqual(from, to)) return new JsonObject { ["path"] = Relative(root, to) };
        if ((File.Exists(to) || Directory.Exists(to)) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            throw new DocumentsException("exists", $"'{newName}' is already there");
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
        return new JsonObject { ["path"] = Relative(root, to) };
    }

    public void Delete(string team, string path)
    {
        var root = Root(team);
        var target = Resolve(root, path, mustExist: true);
        if (PathsEqual(target, root)) throw new DocumentsException("not_allowed", "the documents folder itself cannot be deleted");
        if (Directory.Exists(target))
        {
            foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.ReadOnly);
            Directory.Delete(target, recursive: true);
        }
        else
        {
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
            File.Delete(target);
        }
    }

    // ------------------------------------------------------------------ path safety

    /// <summary>
    /// A path inside the documents folder: relative, with / or \, never leaving the folder (no "..", no drive or root),
    /// and never passing through a link or junction (which could point anywhere).
    /// </summary>
    internal static string Resolve(string root, string? relative, bool mustExist)
    {
        var rel = (relative ?? "").Replace('\\', '/').Trim().Trim('/');
        if (rel.Length == 0) return root;
        if (Path.IsPathRooted(rel) || rel.Contains(':'))
            throw new DocumentsException("bad_path", $"'{relative}' must be a path inside the team's documents");
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".."))
            throw new DocumentsException("bad_path", $"'{relative}' must be a path inside the team's documents");
        var full = root;
        foreach (var part in parts)
        {
            full = Path.Combine(full, part);
            if ((File.Exists(full) || Directory.Exists(full)) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new DocumentsException("bad_path", $"'{relative}' goes through a link, which is not followed");
        }
        var canonical = Path.GetFullPath(full);
        if (!canonical.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !PathsEqual(canonical, root))
            throw new DocumentsException("bad_path", $"'{relative}' must be a path inside the team's documents");
        if (mustExist && !File.Exists(canonical) && !Directory.Exists(canonical))
            throw new DocumentsException("not_found", $"'{relative}' is not there");
        return canonical;
    }

    private static string CleanName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0 || n is "." or ".." || n.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || n.Length > 200)
            throw new DocumentsException("bad_name", $"'{name}' is not a usable name (no / \\ : * ? \" < > |)");
        return n;
    }

    private static string Relative(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/') is var r && r == "." ? "" : Path.GetRelativePath(root, full).Replace('\\', '/');

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    /// <summary>No NUL in the first 8 KB: shown as text.</summary>
    private static bool LooksLikeText(string file)
    {
        using var s = File.OpenRead(file);
        var buffer = new byte[Math.Min(8192, s.Length)];
        var n = s.Read(buffer, 0, buffer.Length);
        return Array.IndexOf(buffer, (byte)0, 0, n) < 0;
    }
}

public sealed class DocumentsException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
