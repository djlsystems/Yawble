using System.IO.Compression;

namespace Harness.Host;

/// <summary>One file of a folder or zip upload: its path inside the uploaded folder, its size, and
/// how to read it.</summary>
public sealed record PackageFile(string Path, long Length, Func<Stream> Open);

/// <summary>
/// A folder or a zip, read and checked whole before anything is written: the name of the folder it
/// makes, its files, and the folders it names (a zip's empty ones), each relative to that folder.
/// </summary>
public sealed record PackageUpload(string Name, IReadOnlyList<PackageFile> Files, IReadOnlyList<string> Folders);

/// <summary>Where a folder or zip upload landed and every file it wrote.</summary>
public sealed record PackageSaved(string Path, string Name, bool IsFolder, IReadOnlyList<string> Files, long Size);

public sealed partial class TeamDocuments
{
    /// <summary>The most files one folder or zip upload carries.</summary>
    public const int MaximumPackageFiles = 1000;

    /// <summary>The most bytes one folder upload carries, or one zip unpacks to.</summary>
    public const long MaximumPackageBytes = 100 * 1024 * 1024;

    /// <summary>A zip tool's own litter, never the package: macOS "Compress" adds resource forks here.</summary>
    private const string MacLitter = "__MACOSX";

    /// <summary>
    /// A folder from the computer, as a browser sends it: each file with its path inside the folder,
    /// top folder first. Every path must sit in one and the same top folder, which names the folder
    /// made. Refused whole with <see cref="DocumentPathException"/>, before anything is written.
    /// </summary>
    public static PackageUpload FolderUpload(IReadOnlyList<(string RelativePath, long Length, Func<Stream> Open)> parts)
    {
        const string refused = "The folder was not uploaded: ";

        if (parts.Count == 0) throw new DocumentPathException("Send a folder.");

        if (parts.Count > MaximumPackageFiles)
        {
            throw new DocumentPathException($"{refused}it holds more than {MaximumPackageFiles} files.");
        }

        string? top = null;
        var files = new List<PackageFile>();

        foreach (var (relativePath, length, open) in parts)
        {
            // A dropped folder's entry path starts with a slash; a picked folder's does not.
            var segments = PackageSegments(relativePath, fromBrowser: true, isFolder: false, refused);

            if (segments.Length < 2)
            {
                throw new DocumentPathException($"{refused}{relativePath} is not inside a folder.");
            }

            top ??= segments[0];

            if (!string.Equals(top, segments[0], StringComparison.Ordinal))
            {
                throw new DocumentPathException($"{refused}{relativePath} is not in {top}; send one folder at a time.");
            }

            files.Add(new PackageFile(string.Join('/', segments[1..]), length, open));
        }

        return Checked(new PackageUpload(top!, files, []), refused);
    }

    /// <summary>
    /// A zip, unpacked into a folder of its own name. Refused whole with
    /// <see cref="DocumentPathException"/>, before anything is written, when ANY entry is a link (or
    /// anything but a plain file or folder), an absolute path, or a path with a <c>..</c> in it.
    /// <c>__MACOSX/</c> is left out, and a zip whose entries all sit in a folder of the zip's own
    /// name - what most zip tools make - is not unpacked into that name twice.
    /// </summary>
    public static PackageUpload ZipUpload(string fileName, ZipArchive archive)
    {
        const string refused = "The zip was not unpacked: ";

        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        name = name[..^".zip".Length].Trim();

        if (name.Length == 0) throw new DocumentPathException("That zip has no name.");

        // THE FOLDER IT MAKES is one plain name: `..zip` would unpack into the folder it was dropped
        // in and `...zip` into the one above, and the marker's name is never a document's.
        if (name is "." or ".." || name.Contains(':'))
        {
            throw new DocumentPathException($"{refused}{name} is not a folder name.");
        }

        if (IsMarker(name)) throw new DocumentPathException($"{refused}{name} is reserved for the folder's marker.");

        if (archive.Entries.Count > MaximumPackageFiles)
        {
            throw new DocumentPathException($"{refused}it holds more than {MaximumPackageFiles} entries.");
        }

        var kept = new List<(string[] Segments, bool IsFolder, ZipArchiveEntry Entry)>();

        foreach (var entry in archive.Entries)
        {
            var raw = entry.FullName;

            // THE ENTRY'S UNIX MODE is the top half of its external attributes; a zip made without
            // one leaves it zero. A link is refused rather than written as a file, so a person never
            // finds a file that was a link in the zip, and nothing in the zip is followed anywhere.
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;

            if (mode == 0xA000) throw new DocumentPathException($"{refused}{raw} is a link.");

            if (mode is not (0 or 0x8000 or 0x4000))
            {
                throw new DocumentPathException($"{refused}{raw} is not a plain file or folder.");
            }

            var isFolder = raw.EndsWith('/') || raw.EndsWith('\\');
            var segments = PackageSegments(raw, fromBrowser: false, isFolder, refused);

            if (segments[0] == MacLitter) continue;

            kept.Add((segments, isFolder, entry));
        }

        if (kept.Count > 0 && kept.All(k => k.Segments[0] == name && (k.Segments.Length > 1 || k.IsFolder)))
        {
            kept = [.. kept.Where(k => k.Segments.Length > 1).Select(k => (k.Segments[1..], k.IsFolder, k.Entry))];
        }

        var files = kept
            .Where(k => !k.IsFolder)
            .Select(k => new PackageFile(string.Join('/', k.Segments), k.Entry.Length, k.Entry.Open))
            .ToList();
        var folders = kept.Where(k => k.IsFolder).Select(k => string.Join('/', k.Segments)).ToList();

        return Checked(new PackageUpload(name, files, folders), refused);
    }

    /// <summary>
    /// Writes a checked package into <paramref name="folder"/> as <paramref name="name"/>. PLANNED
    /// WHOLE FIRST: every place a file or folder would go is walked from the team's root, and a link
    /// on the way, a file where a folder is needed or a folder where a file would go is refused with
    /// <see cref="InvalidOperationException"/> before the first write - a link inside the documents
    /// is the one way out <see cref="Resolve"/> does not see. <paramref name="replaceFile"/> lets a
    /// FILE of that name give way to the folder; a folder of that name takes the files, each one
    /// replacing a file of its path, as an upload of that file would.
    /// </summary>
    public Task<PackageSaved> SavePackageAsync(
        string team, string? folder, string name, PackageUpload upload, bool replaceFile, CancellationToken ct = default)
    {
        var root = EnsureFor(team);
        var directory = Resolve(team, folder);

        return SavePackageUnderAsync(
            root, Relative(root, directory), name, upload, replaceFile, MaximumUploadBytes, MaximumPackageBytes, ct);
    }

    /// <summary>
    /// Writes a checked package into the INSTANCE's documents (the tenant root, outside every team's
    /// folder) at <paramref name="folder"/>/<paramref name="name"/>, by the same plan and the same
    /// checks as <see cref="SavePackageAsync"/>, with the caller's own size bounds.
    /// </summary>
    public Task<PackageSaved> SaveInstancePackageAsync(
        string folder, string name, PackageUpload upload, long fileLimit, long totalLimit, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(paths.TenantDocuments);
        Directory.CreateDirectory(root);

        return SavePackageUnderAsync(root, folder, name, upload, replaceFile: false, fileLimit, totalLimit, ct);
    }

    private async Task<PackageSaved> SavePackageUnderAsync(
        string root, string folder, string name, PackageUpload upload, bool replaceFile,
        long fileLimit, long totalLimit, CancellationToken ct)
    {
        var top = ResolveUnder(root, Path.Combine(folder, name));

        if (top.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new DocumentPathException("A folder needs a name.");

        var files = upload.Files
            .Select(file => (File: file, Absolute: ResolveUnder(root, Relative(root, Path.Combine(top, file.Path)))))
            .ToList();
        var folders = upload.Folders.Select(path => ResolveUnder(root, Relative(root, Path.Combine(top, path)))).Prepend(top).ToList();
        var giveWay = replaceFile && File.Exists(top) && new FileInfo(top).LinkTarget is null ? top : null;

        foreach (var path in folders) CheckWay(root, path, isFolder: true, giveWay);
        foreach (var (_, path) in files) CheckWay(root, path, isFolder: false, giveWay);

        if (giveWay is not null) File.Delete(giveWay);

        // WHAT THIS UPLOAD MADE, so a write that fails part way takes back only that - never a file
        // or folder that was there before it.
        var made = new List<string>();
        var written = new List<string>();
        long size = 0;

        try
        {
            foreach (var path in folders) MakeFolder(path, made);

            foreach (var (file, path) in files)
            {
                MakeFolder(Path.GetDirectoryName(path)!, made);

                if (!File.Exists(path)) made.Add(path);

                await using (var content = file.Open())
                await using (var output = File.Create(path))
                {
                    size += await CopyAtMostAsync(content, output, fileLimit, ct);
                }

                written.Add(Relative(root, path));

                if (size > totalLimit)
                {
                    throw new DocumentPathException($"It is larger than {totalLimit / (1024 * 1024)} MB unpacked.");
                }
            }
        }
        catch
        {
            for (var i = made.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (File.Exists(made[i])) File.Delete(made[i]);
                    else if (Directory.Exists(made[i])) Directory.Delete(made[i], recursive: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Left in place: what the person sees in the folder is still true.
                }
            }

            throw;
        }

        return new PackageSaved(Relative(root, top), Path.GetFileName(top), true, written, size);
    }

    /// <summary>The segments of one path inside a package, refused when it is absolute, leaves the
    /// folder, is not plain, or names the documents marker.</summary>
    private static string[] PackageSegments(string raw, bool fromBrowser, bool isFolder, string refused)
    {
        var path = raw.Replace('\\', '/');

        if (fromBrowser) path = path.TrimStart('/');
        if (isFolder) path = path.TrimEnd('/');

        if (path.StartsWith('/') || path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            throw new DocumentPathException($"{refused}{raw} is an absolute path.");
        }

        var segments = path.Split('/');

        if (segments.Contains("..")) throw new DocumentPathException($"{refused}{raw} leaves the folder.");

        if (segments.Any(segment => segment.Length == 0 || segment == "." || segment.Contains(':')))
        {
            throw new DocumentPathException($"{refused}{raw} is not a plain path.");
        }

        if (segments.Any(segment => string.Equals(segment, TeamPaths.MarkerFileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DocumentPathException($"{refused}{raw} is the documents marker.");
        }

        return segments;
    }

    /// <summary>The limits, and no two entries at one path or a file where a folder also is.</summary>
    private static PackageUpload Checked(PackageUpload upload, string refused)
    {
        if (upload.Files.Count == 0 && upload.Folders.Count == 0) throw new DocumentPathException($"{refused}it is empty.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        long size = 0;

        foreach (var file in upload.Files)
        {
            if (file.Length > MaximumUploadBytes)
            {
                throw new DocumentPathException($"{refused}{file.Path} is larger than {MaximumUploadBytes / (1024 * 1024)} MB.");
            }

            size += file.Length;

            if (!paths.Add(file.Path)) throw new DocumentPathException($"{refused}{file.Path} is in it twice.");
        }

        if (size > MaximumPackageBytes)
        {
            throw new DocumentPathException($"{refused}it is larger than {MaximumPackageBytes / (1024 * 1024)} MB.");
        }

        var above = upload.Files.Select(file => file.Path).Concat(upload.Folders)
            .SelectMany(path => Enumerable.Range(1, path.Count(c => c == '/')).Select(n => string.Join('/', path.Split('/')[..n])));

        foreach (var folder in above.Concat(upload.Folders))
        {
            if (paths.Contains(folder))
            {
                throw new DocumentPathException($"{refused}{folder} is both a file and a folder.");
            }
        }

        return upload;
    }

    /// <summary>Every step from the team's root to <paramref name="path"/>, checked against the disk.</summary>
    private void CheckWay(string root, string path, bool isFolder, string? giveWay)
    {
        var relative = Relative(root, path);
        var step = root;
        var segments = relative.Split('/');

        for (var i = 0; i < segments.Length; i++)
        {
            step = Path.Combine(step, segments[i]);
            var last = i == segments.Length - 1;

            if (new FileInfo(step).LinkTarget is not null)
            {
                throw new InvalidOperationException($"{Relative(root, step)} is a link; nothing was uploaded.");
            }

            if (File.Exists(step) && (!last || isFolder) && step != giveWay)
            {
                throw new InvalidOperationException($"{Relative(root, step)} is a file; nothing was uploaded.");
            }

            if (last && !isFolder && Directory.Exists(step))
            {
                throw new InvalidOperationException($"{Relative(root, step)} is a folder; nothing was uploaded.");
            }
        }
    }

    private static void MakeFolder(string path, List<string> made)
    {
        var missing = new Stack<string>();

        for (var step = path; !Directory.Exists(step); step = Path.GetDirectoryName(step)!) missing.Push(step);

        while (missing.TryPop(out var step))
        {
            Directory.CreateDirectory(step);
            made.Add(step);
        }
    }

    /// <summary>Copies, refusing past <paramref name="limit"/> bytes: a zip's stated size is the
    /// zip's word, and this is what holds it to it.</summary>
    private static async Task<long> CopyAtMostAsync(Stream from, Stream to, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await from.ReadAsync(buffer, ct)) > 0)
        {
            total += read;

            if (total > limit)
            {
                throw new DocumentPathException($"A file in it is larger than {limit / (1024 * 1024)} MB.");
            }

            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return total;
    }
}
