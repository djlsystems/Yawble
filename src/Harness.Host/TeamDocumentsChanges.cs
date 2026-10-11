using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>Which of the three batch changes a request asks for.</summary>
public enum DocumentsVerb
{
    Rename,
    Move,
    Copy,
}

/// <summary>
/// A rename, move or copy refused while it was being planned: nothing was recorded or done. Carries
/// the status it answers with, because the refusals of one request span 400, 404 and 409.
/// </summary>
public sealed class DocumentsChangeRefusedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// How a rename, move or copy touches the disk, one file or folder at a time. A seam, shaped like
/// <see cref="FolderRemoval.HostDeletes"/>, so the suite can make one path fail where the product's
/// Host would (an agent's owner-only folder, a file in use, another drive) without root or chmod.
/// </summary>
public sealed record DocumentFileOps(
    Action<string, string> MoveFile,
    Action<string, string> MoveFolder,
    Action<string, string> CopyFile)
{
    /// <summary>Never overwriting: a clash that appears after planning fails that item instead.</summary>
    public static DocumentFileOps Real { get; } = new(
        (from, to) => File.Move(from, to, overwrite: false),
        Directory.Move,
        (from, to) => File.Copy(from, to, overwrite: false));
}

/// <summary>What one item of a batch will do, decided before anything is recorded or done.</summary>
/// <param name="From">Relative to the source folder; empty for the whole folder (a copy only).</param>
/// <param name="To">Relative to the destination folder, the keep-both name already applied.</param>
/// <param name="Replaces">The destination entry a Replace removes first, relative to the destination folder.</param>
/// <param name="Files">Every file under the item, relative to the item ("" for a file itself).</param>
/// <param name="Folders">Every folder under the item, relative to the item, for a copy to make.</param>
/// <param name="Settled">"skipped" or "failed" when planning already decided the item, with <paramref name="Reason"/>.</param>
internal sealed record PlannedChange(
    string From,
    string To,
    bool IsFolder,
    string? OnClash,
    string? Replaces,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Folders,
    IReadOnlyList<DocumentLeft> NotCopied,
    string? Settled = null,
    string? Reason = null);

/// <summary>A whole batch, planned: what each item does, and the clashes nobody chose for.</summary>
internal sealed record DocumentsChangePlan(
    DocumentsVerb Verb,
    string Source,
    string Destination,
    string DestinationPath,
    IReadOnlyList<PlannedChange> Items,
    IReadOnlyList<DocumentsClash> Clashes);

/// <summary>One item's outcome on the wire: done, skipped or failed, and why.</summary>
internal sealed record DocumentsChangeResult(
    string From, string To, string Outcome, string? Reason = null, IReadOnlyList<DocumentLeft>? NotCopied = null);

/// <summary>A name already taken at the destination, which the person has not said what to do about.</summary>
internal sealed record DocumentsClash(string From, string To, bool IsFolder);

/// <summary>One item acted on: its result, and the files that changed, for the notices.</summary>
/// <param name="Left">Files that left the source folder, relative to it.</param>
/// <param name="Arrived">Files that arrived in the destination folder, relative to it.</param>
/// <param name="Removed">Files a Replace removed from the destination folder, relative to it.</param>
internal sealed record AppliedChange(
    DocumentsChangeResult Result,
    IReadOnlyList<string> Left,
    IReadOnlyList<string> Arrived,
    IReadOnlyList<string> Removed);

/// <summary>Everything under a folder, the marker aside, links listed as themselves and never walked into.</summary>
/// <param name="Unlistable">Folders the Host could not list: what is in them is not known.</param>
internal sealed record DocumentsWalk(
    List<string> Files, List<string> Folders, List<string> Links, List<string> Unlistable);

/// <summary>
/// RENAME, MOVE AND COPY: a person's batch changes to documents. Each batch is PLANNED WHOLE before
/// anything is written, so every refusal is answered with nothing recorded and nothing done; the
/// caller records the plan, then acts on it.
///
/// No change here removes a tree by itself: a move is one <c>rename(2)</c>, never a copy and a
/// delete, so it never half-happens; the only removals are a Replace and a failed copy's own
/// leftovers, and both go through <see cref="FolderRemoval.RemoveDocumentsAsync"/>.
/// </summary>
public sealed partial class TeamDocuments
{
    /// <summary>Items in one rename, move or copy request.</summary>
    public const int MaximumBatchItems = 1000;

    /// <summary>Files in one copy: as many as a folder notice will name.</summary>
    public const int MaximumCopyFiles = 10_000;

    public const string KeepBoth = "keep-both";
    public const string Replace = "replace";
    public const string Skip = "skip";

    private static readonly Regex CopySuffix = new(@"^(.*) \(copy(?: (\d+))?\)$", RegexOptions.CultureInvariant);

    /// <summary>The tenant documents root, as an agent sees it: `HARNESS_SHARED` is this plus the team.</summary>
    public string TenantRoot => Path.GetFullPath(paths.TenantDocuments);

    /// <summary>
    /// The first free keep-both name: <c>name (copy).ext</c>, then <c>name (copy 2).ext</c> and on.
    /// The suffix goes before the LAST extension; a folder and a dot-name (<c>.env</c>) have none. A
    /// name already ending in a copy suffix is not stacked: <c>a (copy).md</c> becomes
    /// <c>a (copy 2).md</c>. Pure: <paramref name="taken"/> says what is in use, on disk and earlier
    /// in the same batch.
    /// </summary>
    public static string FreeName(string name, bool isFolder, Func<string, bool> taken)
    {
        var dot = isFolder ? -1 : name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");

        if (CopySuffix.Match(stem) is { Success: true } match) stem = match.Groups[1].Value;

        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{stem} (copy){extension}" : $"{stem} (copy {n}){extension}";

            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Everything under <paramref name="folder"/>: files (the marker aside), folders, links (listed
    /// as themselves, never walked into) and the folders that could not be listed. THE ONE
    /// ENUMERATION a delete, a rename, a move and a copy plan with.
    /// </summary>
    internal static DocumentsWalk Walk(string folder)
    {
        var walk = new DocumentsWalk([], [], [], []);
        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.TryPop(out var directory))
        {
            List<FileSystemInfo> entries;

            try
            {
                entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos()];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                walk.Unlistable.Add(directory);
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.LinkTarget is not null)
                {
                    walk.Links.Add(entry.FullName);
                }
                else if (entry is DirectoryInfo)
                {
                    walk.Folders.Add(entry.FullName);
                    pending.Push(entry.FullName);
                }
                else if (!IsMarker(entry.FullName))
                {
                    walk.Files.Add(entry.FullName);
                }
            }
        }

        return walk;
    }

    /// <summary>
    /// A rename batch, planned: each item keeps its parent and takes a new leaf name. Throws
    /// <see cref="DocumentsChangeRefusedException"/> (or <see cref="DocumentPathException"/> for a
    /// path outside the folder) for the first refusal.
    /// </summary>
    internal DocumentsChangePlan PlanRename(string team, IReadOnlyList<(string? Path, string? Name)>? items)
    {
        CheckCount(items?.Count ?? 0);

        var root = RootFor(team);
        var planned = new List<PlannedChange>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, name) in items!)
        {
            var source = Source(team, root, path, DocumentsVerb.Rename);
            var from = Relative(root, source);

            if (!sources.Add(source)) throw Refused(400, $"{from} is named twice.");

            var leaf = name?.Trim() ?? "";

            if (leaf.Length == 0 || leaf is "." or ".." || leaf.IndexOfAny(['/', '\\', ':']) >= 0)
            {
                throw Refused(400, "A name cannot be empty, \".\" or \"..\", or contain / \\ or :.");
            }

            if (IsMarker(leaf)) throw Refused(400, $"{leaf} is reserved for the folder's marker.");

            var parent = Path.GetDirectoryName(source)!;
            var target = Path.Combine(parent, leaf);
            var to = Relative(root, target);
            var isFolder = Directory.Exists(source);

            if (targets.TryGetValue(to, out var earlier))
            {
                throw Refused(400, $"{earlier} and {from} would both become {to}.");
            }

            targets[to] = from;

            if (string.Equals(source, target, StringComparison.Ordinal))
            {
                planned.Add(new PlannedChange(from, to, isFolder, null, null, [], [], [], "skipped", $"{from} is already named {leaf}."));
                continue;
            }

            // A CASE-ONLY CHANGE IS NOT A CLASH: on a case-insensitive disk the "other" entry is this one.
            var caseOnly = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);

            if (!caseOnly && Present(target))
            {
                throw Refused(409, $"There is already {leaf} in {ParentName(team, root, parent)}.");
            }

            planned.Add(new PlannedChange(from, to, isFolder, null, null, Contents(source, isFolder), [], []));
        }

        return new DocumentsChangePlan(DocumentsVerb.Rename, team, team, "", planned, []);
    }

    /// <summary>
    /// A move or copy batch, planned against what is on disk now. <paramref name="destination"/> is
    /// a folder the caller has already found live (a gone or retired one is the caller's refusal,
    /// since only the registry can say). Clashes with no choice are COLLECTED, not thrown, so the
    /// answer can name every one; the caller refuses the request when there are any.
    /// </summary>
    internal DocumentsChangePlan PlanTransfer(
        DocumentsVerb verb,
        string team,
        string destination,
        string? destinationPath,
        IReadOnlyList<(string? Path, string? OnClash)>? items)
    {
        CheckCount(items?.Count ?? 0);

        foreach (var (_, onClash) in items!)
        {
            if (onClash is not (null or KeepBoth or Replace or Skip))
            {
                throw Refused(400, "onClash is keep-both, replace or skip.");
            }
        }

        var root = RootFor(team);
        var destinationRoot = RootFor(destination);
        var into = Resolve(destination, destinationPath);
        var intoRelative = into.Equals(destinationRoot, StringComparison.OrdinalIgnoreCase)
            ? ""
            : Relative(destinationRoot, into);
        var intoName = intoRelative.Length == 0 ? destination : intoRelative;

        if (into.Equals(destinationRoot, StringComparison.OrdinalIgnoreCase)
                ? Link(destinationRoot)
                : !FolderRemoval.Confined(destinationRoot, into) || Link(into))
        {
            throw Refused(409, $"{intoName} is reached through a link, and a link is never followed.");
        }

        if (File.Exists(into)) throw Refused(409, $"{intoName} is a file, not a folder.");

        if (!Directory.Exists(into)) throw Refused(404, $"No such folder: {intoName}.");

        var sameFolder = string.Equals(root, destinationRoot, StringComparison.Ordinal);
        var verbed = verb == DocumentsVerb.Move ? "moved" : "copied";
        var planned = new List<PlannedChange>();
        var clashes = new List<DocumentsClash>();
        var sources = new List<string>();
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copying = 0;

        foreach (var (path, onClash) in items)
        {
            var source = Source(team, root, path, verb);
            var from = source.Equals(root, StringComparison.Ordinal) ? "" : Relative(root, source);
            var named = from.Length == 0 ? team : from;

            if (sources.Contains(source, StringComparer.Ordinal)) throw Refused(400, $"{named} is named twice.");

            sources.Add(source);

            var isFolder = Directory.Exists(source);

            if (isFolder && (into.Equals(source, StringComparison.Ordinal)
                || into.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                throw Refused(409, $"{named} cannot be {verbed} into itself.");
            }

            var leaf = from.Length == 0 ? TeamPaths.TeamOfDocumentsFolder(team) : Path.GetFileName(source);
            var target = Path.Combine(into, leaf);
            var parent = Path.GetDirectoryName(source);

            if (verb == DocumentsVerb.Move && sameFolder && string.Equals(parent, into, StringComparison.Ordinal))
            {
                planned.Add(new PlannedChange(from, from, isFolder, onClash, null, [], [], [], "skipped", "already there"));
                continue;
            }

            var crossing = !sameFolder;
            var (files, folders, links, unlistable) = isFolder ? Walk(source) : new DocumentsWalk([source], [], [], []);

            if (verb == DocumentsVerb.Move && crossing && links.Count > 0)
            {
                throw Refused(409,
                    $"{named} holds a link ({Relative(root, links[0])}). A link is never moved into another "
                    + "team's documents; delete it first.");
            }

            var relativeFiles = files.Select(file => isFolder ? Relative(source, file) : "").ToList();
            var relativeFolders = folders.Select(folder => Relative(source, folder)).ToList();
            var notCopied = verb == DocumentsVerb.Copy
                ? links.Select(link => new DocumentLeft(Relative(root, link), "a link, never followed")).ToList()
                : [];

            // NOT ACTED ON UNCHECKED: a folder the Host cannot list could hide files a copy would
            // leave behind while saying "done", or a link a move would carry into another team.
            var cannotCheck = (verb == DocumentsVerb.Copy || crossing) && unlistable.Count > 0
                ? $"permission denied ({Relative(root, unlistable[0])} cannot be listed)"
                : null;

            if (cannotCheck is null && verb == DocumentsVerb.Copy) copying += files.Count;

            var chosen = onClash;
            var to = Relative(destinationRoot, target);
            string? replaces = null;
            string? settled = cannotCheck is null ? null : "failed";
            var reason = cannotCheck;

            var copyHere = verb == DocumentsVerb.Copy && sameFolder && string.Equals(parent, into, StringComparison.Ordinal);
            var clashOnDisk = Present(target);
            var clashInBatch = targets.ContainsKey(to);

            if (copyHere)
            {
                // A DUPLICATE, as Finder and Explorer make one: copying into its own folder always keeps both.
                chosen = KeepBoth;
            }

            if (copyHere || clashOnDisk || clashInBatch)
            {
                switch (chosen)
                {
                    case null when clashInBatch && !clashOnDisk:
                        throw Refused(400, $"{targets[to]} and {named} would both become {to}.");
                    case null:
                        clashes.Add(new DocumentsClash(from, to, Directory.Exists(target)));
                        break;
                    case KeepBoth:
                        leaf = FreeName(leaf, isFolder, name =>
                            Present(Path.Combine(into, name)) || targets.ContainsKey(Relative(destinationRoot, Path.Combine(into, name))));
                        target = Path.Combine(into, leaf);
                        to = Relative(destinationRoot, target);
                        break;
                    case Replace when clashInBatch && !clashOnDisk:
                        throw Refused(400, $"{targets[to]} and {named} would both become {to}.");
                    case Replace:
                        if (source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        {
                            throw Refused(409, $"{named} is inside {to}, which it would replace.");
                        }

                        replaces = to;
                        break;
                    case Skip:
                        settled ??= "skipped";
                        reason ??= $"Skipped: {to} is already there.";
                        break;
                }
            }

            targets.TryAdd(to, named);
            planned.Add(new PlannedChange(
                from, to, isFolder, chosen, settled is null ? replaces : null,
                relativeFiles, relativeFolders, notCopied, settled, reason));
        }

        // A FOLDER AND SOMETHING INSIDE IT: the second would be gone, or copied twice, by the time it came up.
        foreach (var inner in sources)
        {
            if (sources.FirstOrDefault(outer => inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.Ordinal)) is { } outer)
            {
                throw Refused(400, $"{Relative(root, inner)} is inside {(outer == root ? team : Relative(root, outer))}, which is named too.");
            }
        }

        if (verb == DocumentsVerb.Copy && copying > copyLimit)
        {
            throw Refused(409, $"That is {copying} files; at most {copyLimit} can be copied at a time.");
        }

        return new DocumentsChangePlan(verb, team, destination, intoRelative, planned, clashes);
    }

    /// <summary>
    /// Carries out a plan already recorded. NEVER THROWS for one item: an item that cannot be done
    /// is answered failed with why ("permission denied", "in use", "on another drive", "something
    /// with that name appeared in ...") and the rest go on.
    /// </summary>
    internal async Task<IReadOnlyList<AppliedChange>> ApplyAsync(
        DocumentsChangePlan plan, FolderRemoval removal, CancellationToken ct)
    {
        var root = RootFor(plan.Source);
        var destinationRoot = RootFor(plan.Destination);
        var applied = new List<AppliedChange>();

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Settled is { } settled)
            {
                applied.Add(new AppliedChange(
                    new DocumentsChangeResult(item.From, item.To, settled, item.Reason, NotCopiedOrNull(item)), [], [], []));
                continue;
            }

            var source = item.From.Length == 0 ? root : Path.Combine(root, item.From);
            var target = Path.Combine(destinationRoot, item.To);
            var parentName = Path.GetDirectoryName(item.To) is { Length: > 0 } parent ? parent : plan.Destination;
            List<string> removed = [];

            try
            {
                if (item.Replaces is not null)
                {
                    List<string> replaced = Directory.Exists(target) && !Link(target)
                        ? [.. Walk(target) is var walk ? walk.Files.Concat(walk.Links).Select(file => Relative(destinationRoot, file)) : []]
                        : [item.To];
                    var report = await removal.RemoveDocumentsAsync(
                        destinationRoot, target, TeamPaths.TeamOfDocumentsFolder(plan.Destination), ct);
                    removed = [.. replaced.Where(file => !StillThere(Path.Combine(destinationRoot, file)))];

                    if (!report.Complete)
                    {
                        var why = report.Refused
                            ?? report.Reasons?.Values.FirstOrDefault()
                            ?? "still there after the delete";
                        applied.Add(Failed(item, $"{item.To} could not be replaced: {why}", removed));
                        continue;
                    }
                }

                // NEVER OVERWRITTEN: something that appeared since the plan was made fails this item.
                var caseOnly = plan.Verb == DocumentsVerb.Rename
                    && string.Equals(source, target, StringComparison.OrdinalIgnoreCase);

                if (!caseOnly && Present(target))
                {
                    applied.Add(Failed(item, $"something with that name appeared in {parentName}", removed));
                    continue;
                }

                var before = item.Files.Select(file => Under(item.From, file)).ToList();
                var after = item.Files.Select(file => Under(item.To, file)).ToList();

                switch (plan.Verb)
                {
                    case DocumentsVerb.Rename when caseOnly:
                        var aside = Path.Combine(Path.GetDirectoryName(source)!, $".{Guid.NewGuid():N}.renaming");
                        MoveEntry(source, aside, item.IsFolder);
                        MoveEntry(aside, target, item.IsFolder);
                        break;
                    case DocumentsVerb.Rename or DocumentsVerb.Move:
                        MoveEntry(source, target, item.IsFolder);
                        break;
                    case DocumentsVerb.Copy:
                        var failure = await CopyAsync(item, source, target, destinationRoot, plan.Destination, removal, ct);

                        if (failure is null) break;

                        applied.Add(Failed(item, failure, removed));
                        continue;
                }

                applied.Add(new AppliedChange(
                    new DocumentsChangeResult(item.From, item.To, "done", null, NotCopiedOrNull(item)),
                    plan.Verb == DocumentsVerb.Copy ? [] : before,
                    after,
                    removed));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                applied.Add(Failed(item, FolderRemoval.Why(exception), removed));
            }
        }

        return applied;
    }

    /// <summary>A copy of one item, folders made first; on failure what it made is removed. Null when done.</summary>
    private async Task<string?> CopyAsync(
        PlannedChange item, string source, string target, string destinationRoot, string destination,
        FolderRemoval removal, CancellationToken ct)
    {
        try
        {
            if (!item.IsFolder)
            {
                _fileOps.CopyFile(source, target);
                return null;
            }

            Directory.CreateDirectory(target);

            foreach (var folder in item.Folders) Directory.CreateDirectory(Path.Combine(target, folder));

            foreach (var file in item.Files) _fileOps.CopyFile(Path.Combine(source, file), Path.Combine(target, file));

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // NOTHING HALF-MADE: what this item made goes again, through the one documents removal.
            if (Present(target)) await removal.RemoveDocumentsAsync(destinationRoot, target, TeamPaths.TeamOfDocumentsFolder(destination), ct);

            return FolderRemoval.Why(exception);
        }
    }

    private void MoveEntry(string from, string to, bool isFolder)
    {
        if (isFolder) _fileOps.MoveFolder(from, to);
        else _fileOps.MoveFile(from, to);
    }

    private static AppliedChange Failed(PlannedChange item, string reason, IReadOnlyList<string> removed) =>
        new(new DocumentsChangeResult(item.From, item.To, "failed", reason), [], [], removed);

    private static IReadOnlyList<DocumentLeft>? NotCopiedOrNull(PlannedChange item) =>
        item.NotCopied.Count == 0 ? null : item.NotCopied;

    private static string Under(string item, string relative) =>
        relative.Length == 0 ? item : item.Length == 0 ? relative : $"{item}/{relative}";

    /// <summary>
    /// One source path, checked: inside the folder, not reached through or being a link, there, and
    /// neither the marker nor (for a rename or move) the folder itself.
    /// </summary>
    private string Source(string team, string root, string? path, DocumentsVerb verb)
    {
        var source = Resolve(team, path);
        var named = string.IsNullOrWhiteSpace(path) ? team : path.Replace('\\', '/').Trim().Trim('/');

        if (source.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if (verb != DocumentsVerb.Copy) throw Refused(400, "The documents folder itself cannot be renamed or moved.");

            return root;
        }

        if (IsMarker(source)) throw Refused(400, "That file is this folder's own marker, not a document.");

        if (!FolderRemoval.Confined(root, source))
        {
            throw Refused(409, $"{named} is reached through a link, and a link is never followed.");
        }

        if (Link(source))
        {
            throw Refused(409, $"{named} is a link. A link is never followed, renamed, moved or copied.");
        }

        if (!File.Exists(source) && !Directory.Exists(source)) throw Refused(404, $"No such document: {named}.");

        return source;
    }

    /// <summary>What a renamed item carries, relative to it: "" for a file, every file and link under a folder.</summary>
    private List<string> Contents(string source, bool isFolder)
    {
        if (!isFolder) return [""];

        var walk = Walk(source);
        return [.. walk.Files.Concat(walk.Links).Select(file => Relative(source, file))];
    }

    private string ParentName(string team, string root, string parent) =>
        parent.Equals(root, StringComparison.Ordinal) ? team : Relative(root, parent);

    private static void CheckCount(int count)
    {
        if (count == 0) throw Refused(400, "Name at least one document.");

        if (count > MaximumBatchItems) throw Refused(400, $"At most {MaximumBatchItems} documents at a time.");
    }

    private static DocumentsChangeRefusedException Refused(int status, string sentence) => new(status, sentence);

    /// <summary>Whether anything is at the path, a link (even a broken one) counted as itself.</summary>
    private static bool Present(string path) =>
        File.Exists(path) || Directory.Exists(path) || Link(path);

    private static bool Link(string path)
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
}
