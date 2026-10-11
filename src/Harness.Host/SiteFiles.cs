using System.Runtime.InteropServices;
using Harness.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Harness.Host;

/// <summary>What opening a site's file came to: the file held open, or why not.</summary>
public enum SiteFileOutcome
{
    Served,

    /// <summary>Missing, a link at any level, a folder, or not a regular file. One answer for all of
    /// them, so a page learns nothing about what a link points at.</summary>
    NotFound,

    /// <summary>There, but the platform may not read it.</summary>
    Unreadable,
}

/// <summary>A site's file, opened: <paramref name="Stream"/> is set only when it is
/// <see cref="SiteFileOutcome.Served"/>, and the caller disposes it.</summary>
public sealed record SiteFileOpened(SiteFileOutcome Outcome, FileStream? Stream = null);

/// <summary>
/// THE FILES A TEAM MAKES FOR ONE OF ITS SITES, in the team's documents at
/// <c>sites/&lt;site&gt;/files/</c>. See <c>docs/sites.md</c>.
///
/// <list type="bullet">
/// <item><b>In the documents folder</b>, so they outlive the team as documents do, a person finds
/// and deletes them in Documents, and an agent or plugin writes them where it already may.</item>
/// <item><b>Created with the site</b> and repaired whenever an agent or a plugin is told the folder;
/// best effort and swallowing, as the documents folder's own repair is. Reading never creates.</item>
/// <item><b>Kept when the site or the team is deleted</b>, unless empty: <see cref="PruneIfEmpty"/>
/// removes only empty folders, one level at a time, and never through a link.</item>
/// <item><b>Served only from <c>files/</c></b>, by <see cref="Open"/>: every component from the
/// team's documents folder down is opened without following a link, relative to the folder held
/// open above it, so a link swapped in after a check changes nothing the Host reads.</item>
/// </list>
/// </summary>
public sealed class SiteFiles(TeamPaths paths)
{
    public const string SitesFolder = "sites";

    public const string FilesFolder = "files";

    /// <summary>
    /// The content type a download is answered with, by extension. Anything not listed - every type
    /// a browser could run (HTML, SVG, XML, script) among them - is
    /// <see cref="FallbackContentType"/>, so a browser that ignored <c>attachment</c> would still
    /// not render it. Nothing is refused for its type.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = "text/plain; charset=utf-8",
            [".csv"] = "text/csv; charset=utf-8",
            [".json"] = "application/json",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".zip"] = "application/zip",
        };

    public const string FallbackContentType = "application/octet-stream";

    public static string ContentTypeFor(string name) =>
        ContentTypes.TryGetValue(Path.GetExtension(name), out var type) ? type : FallbackContentType;

    /// <summary>TESTS ONLY: run with the leaf's folder path after every folder is held open and just
    /// before the file itself is opened, so a test can swap a folder for a link in that window. Null
    /// in the Host.</summary>
    public Action<string>? BeforeLeafOpen { get; set; }

    /// <summary>Where <paramref name="site"/>'s files are. Pure: composes and never touches disk.</summary>
    public string FolderFor(string team, string site) =>
        Path.Combine(Path.GetFullPath(paths.DocsFor(team)), SitesFolder, site, FilesFolder);

    /// <summary>
    /// The site's files folder, CREATED if it is not there, with the documents folder and its marker.
    /// Best effort: every failure is swallowed and the path is answered anyway. A level that is a link
    /// is not created through.
    /// </summary>
    public string Ensure(string team, string site)
    {
        var folder = FolderFor(team, site);
        if (!SiteRules.IsSlug(site)) return folder;

        var docs = Path.GetFullPath(paths.DocsFor(team));

        try
        {
            if (!TeamPaths.EnsureDocumentsFolder(docs, TeamPaths.TeamOfDocumentsFolder(Path.GetFileName(docs)))) return folder;

            var at = docs;

            foreach (var name in new[] { SitesFolder, site, FilesFolder })
            {
                at = Path.Combine(at, name);
                if (IsLink(at)) return folder;
                Directory.CreateDirectory(at);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return folder;
    }

    /// <summary>
    /// Removes the site's files folder when it is empty, then the site's folder and <c>sites</c> when
    /// they are left empty, stopping at the first that is not. Never recursive, never through a link:
    /// a level that is a link is left as it is, and so is everything above it.
    /// </summary>
    public void PruneIfEmpty(string team, string site)
    {
        if (!SiteRules.IsSlug(site)) return;

        var docs = Path.GetFullPath(paths.DocsFor(team));
        if (IsLink(docs)) return;

        var sites = Path.Combine(docs, SitesFolder);
        var own = Path.Combine(sites, site);

        // A link at any level above files/ means files/ is not where it seems: touch nothing.
        if (IsLink(sites) || IsLink(own)) return;

        foreach (var folder in new[] { Path.Combine(own, FilesFolder), own, sites })
        {
            if (IsLink(folder) || !Directory.Exists(folder)) return;

            try
            {
                Directory.Delete(folder, recursive: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    /// <summary>How many files the site's files folder holds, at any depth; 0 when it is missing or a
    /// link. Links inside are not followed and not counted.</summary>
    public int CountFiles(string team, string site)
    {
        var folder = FolderFor(team, site);
        if (!SiteRules.IsSlug(site) || IsLink(folder) || !Directory.Exists(folder)) return 0;

        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
                .Count();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// <paramref name="relative"/> in the site's files folder, opened for reading. The path must
    /// pass <see cref="SiteRules.IsFilePath"/>; the caller has checked it. On Linux x64 and arm64
    /// the team's documents folder, <c>sites</c>, the site, <c>files</c> and every folder of the
    /// path are each opened with <c>O_NOFOLLOW | O_DIRECTORY</c> relative to the one above, and the
    /// leaf with <c>O_NOFOLLOW | O_NONBLOCK</c>, so no link is followed at any level and a FIFO
    /// cannot hang the request. Elsewhere (development only) every component is checked by path
    /// first, which narrows the window but does not close it.
    /// </summary>
    public SiteFileOpened Open(string team, string site, string relative)
    {
        if (!SiteRules.IsSlug(site) || !SiteRules.IsFilePath(relative)) return new(SiteFileOutcome.NotFound);

        var docs = Path.GetFullPath(paths.DocsFor(team));
        var names = relative.Split('/');
        string[] folders = [SitesFolder, site, FilesFolder, .. names[..^1]];

        return Flags is { } f ? OpenHeld(f, docs, folders, names[^1]) : OpenByPath(docs, folders, names[^1]);
    }

    private SiteFileOpened OpenHeld(LinuxOpenFlags f, string docs, string[] folders, string leaf)
    {
        var directory = f.Directory | f.Nofollow | f.Cloexec;
        var fd = open(docs, directory);
        if (fd < 0) return Failed(Marshal.GetLastPInvokeError());

        var at = docs;

        foreach (var name in folders)
        {
            var next = openat(fd, name, directory, 0);
            var errno = Marshal.GetLastPInvokeError();
            _ = close(fd);
            if (next < 0) return Failed(errno);

            fd = next;
            at = Path.Combine(at, name);
        }

        BeforeLeafOpen?.Invoke(at);

        var file = openat(fd, leaf, f.Nofollow | Nonblock | f.Cloexec, 0);
        var failed = Marshal.GetLastPInvokeError();
        _ = close(fd);
        if (file < 0) return Failed(failed);

        var handle = new SafeFileHandle(file, ownsHandle: true);
        Span<byte> stat = stackalloc byte[StatxSize];

        // stx_mode at 28; the file type is its top four bits.
        if (statx(file, "", AtEmptyPath, StatxType, ref MemoryMarshal.GetReference(stat)) != 0
            || (BitConverter.ToUInt16(stat.Slice(28, 2)) & SIfmt) != SIfreg)
        {
            handle.Dispose();
            return new(SiteFileOutcome.NotFound);
        }

        return new(SiteFileOutcome.Served, new FileStream(handle, FileAccess.Read));
    }

    /// <summary>EACCES is the platform's to say; every other failure (ENOENT, ELOOP for a link,
    /// ENOTDIR) is the one not-found answer.</summary>
    private static SiteFileOpened Failed(int errno) =>
        new(errno == Eacces ? SiteFileOutcome.Unreadable : SiteFileOutcome.NotFound);

    private SiteFileOpened OpenByPath(string docs, string[] folders, string leaf)
    {
        var at = docs;

        foreach (var name in (string[])["", .. folders])
        {
            at = name.Length == 0 ? at : Path.Combine(at, name);
            if (IsLink(at) || !Directory.Exists(at)) return new(SiteFileOutcome.NotFound);
        }

        BeforeLeafOpen?.Invoke(at);

        var path = Path.Combine(at, leaf);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null) return new(SiteFileOutcome.NotFound);

        try
        {
            return new(SiteFileOutcome.Served, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        catch (UnauthorizedAccessException)
        {
            return new(SiteFileOutcome.Unreadable);
        }
        catch (IOException)
        {
            return new(SiteFileOutcome.NotFound);
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // The open flags come from ConciergeAttachments.LinuxOpenFlagsFor (they differ between x64 and
    // arm64); O_RDONLY is 0 and O_NONBLOCK 0x800 on both, as are the errno and AT_* values here.
    private static readonly LinuxOpenFlags? Flags =
        OperatingSystem.IsLinux() ? ConciergeAttachments.LinuxOpenFlagsFor(RuntimeInformation.ProcessArchitecture) : null;

    private const int Nonblock = 0x800, Eacces = 13, AtEmptyPath = 0x1000, StatxSize = 256;
    private const uint StatxType = 0x1;
    private const int SIfmt = 0xF000, SIfreg = 0x8000;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int dirfd, string path, int flags, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, string path, int flags, uint mask, ref byte buffer);
}
