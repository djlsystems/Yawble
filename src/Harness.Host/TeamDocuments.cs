namespace Harness.Host;

/// <summary>A file or folder in a team's docs area. Paths are relative to that area's root.</summary>
public sealed record DocumentEntry(
    string Name, string Path, bool IsFolder, long Size, DateTimeOffset ModifiedAt, int Children);

/// <summary>
/// What one delete removes, decided before it is removed.
/// </summary>
/// <param name="Folder">The documents folder it is in, as named in the route.</param>
/// <param name="Path">Relative to that folder; empty for the whole folder.</param>
/// <param name="Files">Every file it removes, relative to the folder, the marker excluded.</param>
public sealed record DocumentsDeletion(
    string Folder, string Path, string Absolute, bool IsFolder, bool WholeFolder, IReadOnlyList<string> Files);

/// <summary>Refused because the path pointed outside the team's docs area, or was unusable.</summary>
public sealed class DocumentPathException(string message) : Exception(message);

/// <summary>
/// One documents folder AS IT IS ON DISK - everything that can be known without asking the registry
/// anything. <see cref="DocumentsFolder"/> is the wire shape, and the split is deliberate: whether
/// that team still exists and what it is called are the registry's answers, not the filesystem's,
/// and a type that carried them would be a disk reader holding two facts it cannot check.
/// </summary>
/// <param name="Folder">Its name on disk, and the value to name in a route. For a live team this
/// IS the team identifier; for a retired folder it is not, and cannot ever be one.</param>
/// <param name="Team">The team whose documents these are, which survives the retirement suffix so
/// a person is never shown an anonymous folder.</param>
/// <param name="Retired">Whether a later team of the same id has claimed the name, moving this
/// folder aside. A retired folder's team is by definition gone.</param>
/// <param name="Entries">Immediate children, excluding the marker. -1 when the Host could not
/// read the folder, which is said rather than hidden.</param>
public sealed record DocumentsFolderOnDisk(
    string Folder, string Team, bool Retired, int Entries, DateTimeOffset ModifiedAt);

/// <summary>
/// One documents folder as `GET /api/documents` answers it: what is on disk, plus the two things
/// only the registry can say.
/// </summary>
/// <param name="Label">What that team is CALLED. Answered for a team the registry has never heard
/// of - a documents folder outlives the team it names.</param>
/// <param name="Exists">
/// Whether this is the LIVE folder of a team that still exists.
///
/// FALSE FOR A RETIRED FOLDER whatever the registry says, and that is why this is not simply "the
/// team is in the list". A folder is retired BECAUSE a later team took its identifier, so a team of
/// that name exists by construction - answering true would say a dead team's documents belong to
/// the live team, which is precisely the inheritance the retirement exists to prevent, reappearing
/// in the label.
/// </param>
public sealed record DocumentsFolder(
    string Folder,
    string Team,
    string Label,
    bool Exists,
    bool Retired,
    int Entries,
    DateTimeOffset ModifiedAt);

/// <summary>
/// A team's documents, under the ONE TENANT DOCUMENTS ROOT and outside every team root.
///
/// Every team gets `&lt;dataRoot&gt;/documents/&lt;team&gt;`, claimed with the team and NOT removed
/// with it - see <see cref="TeamPaths.TenantDocuments"/> for why that root is single and
/// unconditional. A manager writes into it and a person uploads into it, so it is a shared surface
/// rather than an agent's scratch space - which is exactly why every path that arrives from outside
/// is resolved and then CHECKED against the root rather than trusted. `..` in a path is the whole
/// attack, and on Windows it is also an ordinary typo.
///
/// **READING NEVER CREATES.** A folder is reachable by the NAME OF A TEAM THAT NO LONGER
/// EXISTS, so a repair on read in <see cref="RootFor"/> would manufacture an empty documents folder
/// for every mistyped team anybody ever browsed - litter that a later claim then has to reason
/// about. The repair is <see cref="EnsureFor"/>, called by the two callers that mean it: an agent being told where to write, and a write arriving.
/// </summary>
public sealed partial class TeamDocuments(
    TeamPaths paths, DocumentFileOps? fileOps = null, int copyLimit = TeamDocuments.MaximumCopyFiles)
{
    private readonly DocumentFileOps _fileOps = fileOps ?? DocumentFileOps.Real;

    /// <summary>Big enough for a document, small enough that a mistake is not a disk-filling one.</summary>
    public const long MaximumUploadBytes = 25 * 1024 * 1024;

    /// <summary>
    /// WHERE a team's documents are. Pure - it composes and never touches disk, so it answers the
    /// same for a live team, a deleted one and a retired folder.
    /// </summary>
    public string RootFor(string team) => Path.GetFullPath(paths.DocsFor(team));

    /// <summary>The instance's own documents: the tenant root every team's folder sits in.</summary>
    public string InstanceRoot => Path.GetFullPath(paths.TenantDocuments);

    /// <summary>
    /// Where a team's documents are, CREATED if they are not there, with the marker that says the
    /// platform made them.
    ///
    /// CREATION IS A REPAIR, NOT THE ANSWER: it exists so that a team whose folder was removed by
    /// hand comes back working. Every failure is swallowed and the path is still answered, because
    /// it is still the truth - an unguarded throw here reaches <c>TeamRegistry.RestoreAsync</c>
    /// through <c>AgentEnvironment.ForContainerAsync</c>, where it becomes
    /// <c>TeamRestorationFailedException</c> and an instance that will not boot, taking every other
    /// team with it. A caller that goes on to READ or WRITE fails on its own terms, naming the file
    /// it wanted.
    ///
    /// **THE MARKER IS WRITTEN HERE TOO, NOT ONLY AT THE CLAIM**, and that is what keeps
    /// <see cref="TeamDocumentsClaim"/>'s "no marker means not ours" honest: a folder this repair
    /// recreated would otherwise fill up with a team's work carrying no evidence the platform made
    /// it, and the next claim on that id would refuse a folder that IS the platform's.
    /// </summary>
    public string EnsureFor(string team)
    {
        var root = RootFor(team);

        TeamPaths.EnsureDocumentsFolder(root, TeamPaths.TeamOfDocumentsFolder(Path.GetFileName(root)));

        return root;
    }

    /// <summary>
    /// The absolute path for a relative one, guaranteed to sit inside this team's folder under the
    /// TENANT DOCUMENTS ROOT. THE ONE PLACE EVERY PATH FROM OUTSIDE GOES THROUGH.
    ///
    /// **IT BOUNDS AGAINST THE TEAM'S FOLDER, NOT THE TENANT ROOT**, and the distinction matters:
    /// the tenant root holds EVERY team's documents, so a check that
    /// admitted anything under it would let one team read another's with `../Beta`. The folder,
    /// which is what <see cref="RootFor"/> answers, is the boundary. The folder NAME itself is
    /// vetted one level up, in <c>TeamPaths</c>, because a separator in that segment would escape
    /// before this method ever saw a relative path.
    ///
    /// Compared after full resolution rather than by inspecting the string for "..": a check that
    /// looks for the characters is defeated by encoding, and one that compares the resolved result
    /// is not. That resolution does NOT defeat a symlink or junction inside the root -
    /// `Path.GetFullPath` is pure string normalisation and never touches the filesystem, so a link
    /// pointing outside the root is a way out this comparison does not see.
    /// </summary>
    public string Resolve(string team, string? relative) => ResolveUnder(RootFor(team), relative);

    /// <summary><see cref="Resolve"/> against any root, for the instance's own documents.</summary>
    private static string ResolveUnder(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return root;

        var cleaned = relative.Replace('\\', '/').Trim().TrimStart('/');

        if (Path.IsPathRooted(cleaned) || cleaned.Contains(':'))
        {
            throw new DocumentPathException("A path here is relative to the team's documents.");
        }

        var combined = Path.GetFullPath(Path.Combine(root, cleaned));

        if (!combined.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DocumentPathException("That path is outside this team's documents.");
        }

        return combined;
    }

    /// <summary>Everything in one folder, folders first. Recursive returns only FILES, each carrying
    /// its path, which is what a flat "pick a document" list wants.</summary>
    public IReadOnlyList<DocumentEntry> List(string team, string? path = null, bool recursive = false)
    {
        var root = RootFor(team);
        var target = Resolve(team, path);

        if (!Directory.Exists(target)) return [];

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // THE MARKER IS NOT A DOCUMENT. It lives in this folder - it is what makes
        // "the platform made this" answerable when a successor team claims the id - and listing it
        // would put a file nobody wrote in front of every person who opens the dialog, one click
        // from a delete that quietly disarms the claim guard. Delete refuses it for the same reason.
        var files = Directory.EnumerateFiles(target, "*", option)
            .Where(file => !IsMarker(file))
            .Select(file => new FileInfo(file))
            .Select(file => new DocumentEntry(
                file.Name, Relative(root, file.FullName), false, file.Length, file.LastWriteTimeUtc, 0));

        if (recursive) return [.. files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];

        var folders = Directory.EnumerateDirectories(target)
            .Select(folder => new DirectoryInfo(folder))
            .Select(folder => new DocumentEntry(
                folder.Name,
                Relative(root, folder.FullName),
                true,
                0,
                folder.LastWriteTimeUtc,
                // What makes a folder removable, so the UI can say why before the refusal does.
                Directory.EnumerateFileSystemEntries(folder.FullName).Count()));

        return
        [
            .. folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            .. files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public DocumentEntry CreateFolder(string team, string path)
    {
        // EnsureFor rather than RootFor, here and at SaveAsync below: a WRITE is one of the two
        // callers the repair is for, and it is what keeps the marker present on a folder somebody
        // removed by hand and an agent then wrote into.
        var root = EnsureFor(team);
        var target = Resolve(team, path);

        if (target.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new DocumentPathException("A folder needs a name.");
        }

        Directory.CreateDirectory(target);

        var info = new DirectoryInfo(target);
        return new DocumentEntry(info.Name, Relative(root, target), true, 0, info.LastWriteTimeUtc, 0);
    }

    /// <summary>
    /// Where an upload of <paramref name="fileName"/> into <paramref name="folder"/> would land,
    /// without writing anything: only the LEAF of the name is kept. For an upload that asks before
    /// it replaces, and for its keep-both name.
    /// </summary>
    public (string Path, string Name, bool IsFile, bool IsFolder) UploadTarget(string team, string? folder, string fileName)
    {
        var root = RootFor(team);
        var safeName = UploadLeaf(fileName);
        var directory = Resolve(team, folder);
        var target = Resolve(team, Path.Combine(Relative(root, directory), safeName));

        return (Relative(root, target), safeName, File.Exists(target), Directory.Exists(target));
    }

    public async Task<DocumentEntry> SaveAsync(
        string team, string? folder, string fileName, Stream content, CancellationToken ct = default)
    {
        var root = EnsureFor(team);
        var safeName = UploadLeaf(fileName);
        var directory = Resolve(team, folder);
        Directory.CreateDirectory(directory);

        var target = Resolve(team, Path.Combine(Relative(root, directory), safeName));

        await using (var file = File.Create(target))
        {
            await content.CopyToAsync(file, ct);
        }

        var info = new FileInfo(target);
        return new DocumentEntry(info.Name, Relative(root, target), false, info.Length, info.LastWriteTimeUtc, 0);
    }

    /// <summary>
    /// Everything a delete WOULD remove, decided before anything is removed - so the caller can
    /// record it, and refuse, first. Throws the same refusals the delete itself would.
    ///
    /// A folder with anything in it is refused unless <paramref name="recursive"/> says the person
    /// was asked: this is a file manager for documents a person and an agent both write into, and a
    /// click that takes a tree with it without saying so is the click nobody can undo.
    ///
    /// **THE WHOLE FOLDER** (an empty <paramref name="path"/>) is offered only when
    /// <paramref name="wholeFolderAllowed"/> - the caller's answer to "is this team gone?" - and only
    /// when its <see cref="TeamPaths.MarkerFileName"/> names the team the folder is for: the same rule
    /// <c>TeamDeletion</c> follows for a team root. The tenant documents root is a folder a person
    /// can reach, and removing a tree on the strength of a name is not a risk worth taking.
    /// </summary>
    public DocumentsDeletion Plan(string team, string? path, bool recursive, bool wholeFolderAllowed = false)
    {
        var root = RootFor(team);
        var target = Resolve(team, path);

        if (target.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if (!wholeFolderAllowed)
            {
                throw new DocumentPathException("The documents folder itself cannot be removed.");
            }

            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("No such folder.");

            if (!MarkerNames(root, TeamPaths.TeamOfDocumentsFolder(Path.GetFileName(root))))
            {
                throw new InvalidOperationException(
                    $"This folder carries no {TeamPaths.MarkerFileName} marker naming its team, so it "
                    + "is not a folder this platform created. Remove it by hand if it is yours.");
            }

            if (!recursive && FilesUnder(root).Count > 0)
            {
                throw new InvalidOperationException("That folder is not empty.");
            }

            return new DocumentsDeletion(team, "", root, true, true, FilesUnder(root).Select(f => Relative(root, f)).ToList());
        }

        if (IsMarker(target))
        {
            throw new DocumentPathException(
                "That file is this folder's own marker, not a document.");
        }

        if (File.Exists(target))
        {
            return new DocumentsDeletion(team, Relative(root, target), target, false, false, [Relative(root, target)]);
        }

        if (!Directory.Exists(target)) throw new FileNotFoundException("No such document.", target);

        if (!recursive && NotKnownEmpty(target))
        {
            throw new InvalidOperationException("That folder is not empty.");
        }

        return new DocumentsDeletion(
            team, Relative(root, target), target, true, false,
            FilesUnder(target).Select(f => Relative(root, f)).ToList());
    }

    /// <summary>
    /// Whether anything is still at <paramref name="absolute"/>, a link counted as itself. A path
    /// that cannot be examined is counted as there: a delete is never reported done on a guess.
    /// </summary>
    public static bool StillThere(string absolute)
    {
        try
        {
            return File.Exists(absolute) || Directory.Exists(absolute) || new FileInfo(absolute).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The files under <paramref name="folder"/>, marker aside, through the shared <see cref="Walk"/>.
    /// A folder the Host cannot list (an agent's owner-only directory, a mode-000 one) is passed
    /// over here, not thrown on: the delete
    /// still reaches <see cref="FolderRemoval"/>, which names it as left and why. A link to a
    /// folder is listed as itself and never walked into.
    /// </summary>
    private static List<string> FilesUnder(string folder) =>
        Walk(folder) is var walk ? [.. walk.Files, .. walk.Links.Where(link => !IsMarker(link))] : [];

    /// <summary>Whether a folder has anything in it; one the Host cannot list counts as not empty.</summary>
    private static bool NotKnownEmpty(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Whether a folder's marker is there and its first line names <paramref name="team"/>.
    /// A retired folder's marker still names the team it belonged to.</summary>
    private static bool MarkerNames(string folder, string team)
    {
        try
        {
            var marker = TeamPaths.MarkerIn(folder);
            if (!File.Exists(marker)) return false;

            var owner = File.ReadLines(marker).FirstOrDefault()?.Trim();
            return string.Equals(owner, team, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string Relative(string root, string absolute) =>
        Path.GetRelativePath(root, absolute).Replace('\\', '/');

    private static bool IsMarker(string path) =>
        string.Equals(
            Path.GetFileName(path), TeamPaths.MarkerFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name an upload is saved under: only the LEAF of what was sent, never empty, and never the
    /// folder's marker - which Delete refuses too, so an upload cannot overwrite what Delete keeps.
    /// </summary>
    private static string UploadLeaf(string fileName)
    {
        var leaf = Path.GetFileName(fileName.Replace('\\', Path.DirectorySeparatorChar));

        if (string.IsNullOrWhiteSpace(leaf)) throw new DocumentPathException("That file has no name.");

        if (IsMarker(leaf)) throw new DocumentPathException($"{leaf} is reserved for the folder's marker.");

        return leaf;
    }

    /// <summary>
    /// Every documents folder on disk, in the order a person would read them.
    ///
    /// **THE FOLDERS, NOT THE TEAMS**, and that is the capability rather than an implementation
    /// detail: a document outlives the team that wrote it, so the list of folders is strictly
    /// larger than the list of teams and the difference is exactly the work that must not be
    /// lost. Whether each one's team still exists is the CALLER's question - it holds the
    /// registry, and it is also what decides who may open the folder - so this answers what is on
    /// disk and says nothing about authority.
    ///
    /// A name this platform does not compose is SKIPPED rather than listed: the tenant documents
    /// root is a folder a person can reach, and something they dropped in it is not a team's
    /// documents. It is skipped silently because there is nothing to fix - it is their folder.
    /// </summary>
    public IReadOnlyList<DocumentsFolderOnDisk> Folders()
    {
        var root = paths.TenantDocuments;

        if (!Directory.Exists(root)) return [];

        var folders = new List<DocumentsFolderOnDisk>();

        foreach (var path in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(path);

            if (!TeamPaths.IsDocumentsFolder(name)) continue;

            var info = new DirectoryInfo(path);
            int entries;

            try
            {
                entries = Directory.EnumerateFileSystemEntries(path)
                    .Count(entry => !IsMarker(entry));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // NAMED WITH AN UNKNOWN COUNT rather than dropped. A folder the Host cannot
                // enumerate is still a folder somebody's work is in, and hiding it would be a silent
                // loss.
                entries = -1;
            }

            folders.Add(new DocumentsFolderOnDisk(
                name,
                TeamPaths.TeamOfDocumentsFolder(name),
                TeamPaths.IsRetiredDocumentsFolder(name),
                entries,
                info.LastWriteTimeUtc));
        }

        return [.. folders.OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)];
    }
}
