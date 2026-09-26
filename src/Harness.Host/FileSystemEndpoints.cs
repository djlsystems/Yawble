using System.ComponentModel;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// The picker's four routes - the only routes in this system that report or write anything about
/// the Host's own filesystem outside a team.
///
/// CONTAINMENT IS THE WHOLE BOUNDARY. Any signed-in person may call these routes - there is no
/// tenant-admin gate - so the gate is WHERE, not WHO. WHERE is the entire security model, and <see cref="FileBrowserPolicy"/> owns it: every path arriving from outside is resolved
/// with <c>Path.GetFullPath</c> and then checked against the configured roots, never scanned for
/// <c>..</c>. A path outside every root is refused with 403 before this class does anything else
/// with it. See <see cref="FileBrowserPolicy"/>'s own doc comment for what that check does and does
/// not catch - a symlink or junction inside a root, and POSIX case-folding, are both named there as
/// deliberate gaps rather than oversights this class could close.
///
/// <c>.HumansOnly()</c> is what keeps a machine principal off the Host's filesystem, since WHO is
/// not otherwise gated here - every member and Concierge runs with permissions
/// bypassed, and an agent able to enumerate or write the Host's filesystem is a disclosure nobody
/// asked for. <c>RequirePermit</c> would make these routes reachable by a machine principal, which
/// is exactly the shape <c>HumansOnly</c> exists to refuse.
///
/// FOUR DISTINCT REFUSAL STATUSES, told apart by status alone with no need to parse a body:
/// <b>404</b> - the path does not exist (or is not a directory); <b>403</b> - the caller is not
/// permitted there at all, whether that is a machine principal calling any of these routes or a
/// person naming a path outside every configured root, or a write attempted against a root that does
/// not allow it; <b>423 Locked</b> - the path exists and is reachable, but the Host process cannot
/// read it (a restricted ACL, an unreachable network share). 423 rather than a second 403 or a body
/// discriminator, for the same reason 4.13 chose it: several refusals already answer 403 with an
/// identical <c>{ error: string }</c> shape, so reusing it here would make this refusal
/// structurally indistinguishable from "you may not do that" - readable only by substring-matching
/// prose - while a status is unambiguous by construction.
/// </summary>
public static class FileSystemEndpoints
{
    /// <summary>
    /// How many entries one listing carries. `C:\Windows\WinSxS` holds tens of thousands, and a
    /// folder picker that materialises all of them into one JSON body hangs the browser it is meant
    /// to help. Chosen by what a person can plausibly scroll rather than derived: nobody finds a
    /// folder by reading past a thousand names, and the fix at that point is to type the path.
    /// </summary>
    public const int MaximumEntries = 1000;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/fs/roots", (FileBrowserPolicy policy, TeamPaths teamPaths) =>
            Results.Ok(new
            {
                roots = policy.Roots.Select(root => new
                {
                    name = root.Name,
                    path = root.Path,

                    // WHICH ONE IS THE INSTANCE ROOT, answered ONCE, here. A client needs it to
                    // show what a blank "Place team in" box will actually do, and the only handle
                    // it had was `roots[0]` - true only while the instance root survives
                    // `FileBrowserPolicy`'s constructor, which `--DataRoot=\\?\C:\foo` is enough to
                    // stop. Index nought is then the operator's FIRST CONFIGURED root, and the New
                    // Team dialog showed ITS path as the placeholder while a blank box still put
                    // the team in the real data root - a preview that names the wrong folder.
                    isInstance = IsInstanceRoot(root.Path, teamPaths.DataRoot),

                    allowCreate = root.AllowCreate,
                    allowUpdate = root.AllowUpdate,
                    allowDelete = root.AllowDelete,
                }),
            }))
            .WithTags("FileSystem")
            .HumansOnly()
            .WithSummary("List the host folders the picker may reach")
            .WithDescription(
                "The configured allowlist, normally including this instance's own data folder "
                + "read-only - `isInstance` says which one that is, so a client never has to "
                + "infer it from position. Each root's path is the NORMALISED form actually in "
                + "effect, not a verbatim echo of `appsettings.json` - a root written "
                + "`D:/Projects` comes back `D:\\Projects`, and a root that was blank, "
                + "malformed, a device path, or empty once trimmed is dropped entirely rather "
                + "than echoed back broken. Dropped roots are named once in the Host's startup "
                + "log, not here.\n\n"
                + "**This CAN come back empty**, and a client that renders a blank list is wrong "
                + "to. The instance root is always ADDED, never configurable away - but it is "
                + "normalised like any other, so `--DataRoot=\\\\?\\C:\\foo` drops it along with "
                + "everything else and leaves nothing. This description said the empty case could "
                + "not arise while three places in this codebase already handled it.\n\n"
                + "**A root listed here is not proof it is reachable.** Roots are built from "
                + "configuration without touching the filesystem, and one that has gone away - an "
                + "unplugged drive, a share that is down - is warned about at startup and KEPT, so "
                + "that a share which comes back needs no restart. It appears here and refuses "
                + "when opened: 404 absent, 423 present-but-unreadable.");

        app.MapGet("/api/fs/browse", (
            [Description("An absolute path on the Host's own filesystem to list.")]
            string path,
            FileBrowserPolicy policy) =>
        {
            if (policy.RootFor(path) is not { } root)
            {
                return Forbidden($"'{path}' is outside every folder this Host will show.");
            }

            // Already proven fully-qualified, not a device path and resolvable without throwing -
            // RootFor above would have answered null otherwise - so this cannot throw here.
            var full = Path.GetFullPath(path);

            // DIRECTORY.EXISTS ANSWERS FALSE FOR AN ACCESS FAILURE, and does not distinguish it
            // from absence - so enumeration is asked first and the filesystem says which it is. A
            // genuinely absent path throws DirectoryNotFoundException, caught below as 404;
            // anything else the filesystem refuses falls through to 423.
            //
            // A FILE is the one case enumeration reports as a plain IOException ("the directory
            // name is invalid"), which would land in 423 and read as a permission problem, so it
            // is asked for by name first. File.Exists is false on an access failure too, and that
            // is the right direction here - such a path falls through to the 423 it deserves.
            if (File.Exists(full))
            {
                return Results.NotFound(new { error = $"'{full}' is not a directory." });
            }

            try
            {
                // MATERIALISED INSIDE THE TRY: both calls are lazy, and a bare `.Select()` would
                // defer the throw this catch exists for until serialisation, long after the
                // handler had already answered 200.
                //
                // FILES ARE LISTED NOW, typed - 4.13 answered directories only. Folder mode
                // filters client-side; this route tells the truth about what is actually there.
                var directories = Directory.EnumerateDirectories(full)
                    .Select(entry => (name: Path.GetFileName(entry), type: "dir"));
                var files = Directory.EnumerateFiles(full)
                    .Select(entry => (name: Path.GetFileName(entry), type: "file"));

                var all = directories.Concat(files)
                    .OrderBy(entry => entry.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var entries = all.Take(MaximumEntries)
                    .Select(entry => new { name = entry.name, type = entry.type })
                    .ToList();

                // NULL, not the filesystem's own parent, when that parent sits outside every
                // configured root - browsing a root's own top gives the filesystem's grandparent
                // otherwise, which reads fine but sends an "up" affordance straight into a
                // guaranteed 403. A contract a client can trust: a non-null `parent` here is always
                // itself browsable.
                var upperFull = Directory.GetParent(full)?.FullName;
                var parent = upperFull is not null && policy.RootFor(upperFull) is not null ? upperFull : null;

                return Results.Ok(new
                {
                    path = full,
                    parent,

                    // THE FLAGS OF THE CONTAINING ROOT, so a client knows before it tries whether
                    // New Folder or Upload will do anything here.
                    permissions = new
                    {
                        allowCreate = root.AllowCreate,
                        allowUpdate = root.AllowUpdate,
                        allowDelete = root.AllowDelete,
                    },
                    entries,

                    // SAID, not implied by a count the caller would have to compare against a
                    // constant it does not have. A truncated listing that looks complete is the
                    // same defect as a 200 with `entries: []` for a folder that could not be read.
                    truncated = all.Count > MaximumEntries,
                    total = all.Count,
                });
            }
            catch (DirectoryNotFoundException)
            {
                return Results.NotFound(new { error = $"'{full}' is not a directory." });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Locked(full, ex);
            }
        })
            .WithTags("FileSystem")
            .HumansOnly()
            .WithSummary("List the contents of a server-side path")
            .WithDescription(
                "Backs the folder picker. Entries are typed `dir` or `file` - folder mode filters "
                + "client-side, and this route tells the truth about what is there rather than "
                + "hiding files from every caller.\n\n"
                + $"**Capped at {MaximumEntries} entries, sorted first.** `truncated` says whether "
                + "the cap bit and `total` says how many there really are.\n\n"
                + "**`parent` is `null` when the path listed is itself the top of a root.** A "
                + "non-null `parent` is guaranteed to be itself browsable - the filesystem's own "
                + "parent directory is suppressed to `null` rather than returned when it sits "
                + "outside every configured root, so an \"up\" affordance built on this field never "
                + "leads to a guaranteed 403.\n\n"
                + "**Containment is the boundary.** Every path is resolved with `Path.GetFullPath` "
                + "and checked against the configured roots (`GET /api/fs/roots`); a path outside "
                + "all of them is refused with 403 before anything else runs. `404` - the path does "
                + "not exist, or is not a directory. `423 Locked` - the path exists and is inside "
                + "an allowed root, but the Host process cannot read it (a restricted ACL, an "
                + "unreachable network share).");

        app.MapPost("/api/fs/directory", (CreateHostDirectory request, FileBrowserPolicy policy) =>
        {
            if (string.IsNullOrWhiteSpace(request.Parent) || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "A parent folder and a name are required." });
            }

            if (!IsSingleSegmentName(request.Name, "folder", out var segmentError))
            {
                return Results.BadRequest(new { error = segmentError });
            }

            if (policy.RootFor(request.Parent) is not { } root)
            {
                return Forbidden($"'{request.Parent}' is outside every folder this Host will show.");
            }

            if (!root.AllowCreate)
            {
                return Forbidden($"'{root.Name}' does not allow creating folders.");
            }

            var parentFull = Path.GetFullPath(request.Parent);

            if (!Directory.Exists(parentFull))
            {
                return Results.NotFound(new { error = $"'{parentFull}' is not a directory." });
            }

            // FULLY RESOLVED, not a bare Combine, so the response echoes the exact path that will
            // exist on disk.
            var target = Path.GetFullPath(Path.Combine(parentFull, request.Name));

            // A single-segment name cannot climb out of a directory already proven contained, but
            // this is asked anyway rather than trusted - the same posture FileBrowserPolicy's own
            // doc comment takes about never trusting a caller who has not gone through it.
            if (policy.RootFor(target) is null)
            {
                return Forbidden("That folder would be outside every folder this Host will show.");
            }

            if (File.Exists(target))
            {
                return Results.BadRequest(new { error = $"'{request.Name}' already exists there, as a file." });
            }

            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Locked(target, ex);
            }

            return Results.Ok(new { path = target });
        })
            .WithTags("FileSystem")
            .HumansOnly()
            .WithSummary("Create a directory on the Host's filesystem")
            .WithDescription(
                "Refused with 403 unless the containing root allows it (`allowCreate`). `name` "
                + "must be a single path segment: 400 for a `/`, a NUL character, `.` or `..` - "
                + "what Linux refuses in one name. Nothing else is refused.");

        app.MapPost("/api/fs/upload", async (
            HttpRequest request, FileBrowserPolicy policy, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Send a file." });

            var form = await request.ReadFormAsync(ct);
            var parent = form["parent"].ToString();
            var file = form.Files.GetFile("file");

            if (string.IsNullOrWhiteSpace(parent))
            {
                return Results.BadRequest(new { error = "A destination folder is required." });
            }

            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Send a file." });

            if (file.Length > TeamDocuments.MaximumUploadBytes)
            {
                return Results.BadRequest(new
                {
                    error = $"That file is larger than {TeamDocuments.MaximumUploadBytes / (1024 * 1024)} MB.",
                });
            }

            if (policy.RootFor(parent) is not { } root)
            {
                return Forbidden($"'{parent}' is outside every folder this Host will show.");
            }

            if (!root.AllowCreate)
            {
                return Forbidden($"'{root.Name}' does not allow uploads.");
            }

            var parentFull = Path.GetFullPath(parent);

            if (!Directory.Exists(parentFull))
            {
                return Results.NotFound(new { error = $"'{parentFull}' is not a directory." });
            }

            // ONLY THE LEAF of the supplied filename is kept, so a name carrying directory
            // separators cannot place the file anywhere but the folder named by `parent` - the
            // same rule the team-documents upload route follows, for the same reason.
            var leaf = Path.GetFileName(file.FileName.Replace('\\', Path.DirectorySeparatorChar));

            if (string.IsNullOrWhiteSpace(leaf))
            {
                return Results.BadRequest(new { error = "That file has no name." });
            }

            // SAME VALIDATION AS A DIRECTORY NAME: a leaf that is `.` or `..`, or carries a `/`,
            // would not be a file next to the others, and `File.Exists` below would be asked about
            // the wrong entry - defeating the existing-destination check this route exists to
            // enforce.
            if (!IsSingleSegmentName(leaf, "file", out var leafError))
            {
                return Results.BadRequest(new { error = leafError });
            }

            var target = Path.GetFullPath(Path.Combine(parentFull, leaf));

            if (policy.RootFor(target) is null)
            {
                return Forbidden("That destination would be outside every folder this Host will show.");
            }

            // OVERWRITE IS `allowUpdate`, WHICH HAS NO ROUTE YET - so an existing destination is
            // refused rather than silently replaced. FileMode.CreateNew is the enforcement; this
            // check is only what turns the race into a clean 409 in the ordinary case.
            if (File.Exists(target))
            {
                return Conflict($"'{leaf}' already exists there.");
            }

            try
            {
                await using var content = file.OpenReadStream();
                await using var output = new FileStream(target, FileMode.CreateNew);
                await content.CopyToAsync(output, ct);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Lost a race with another upload of the same name between the check above and
                // FileMode.CreateNew opening the file.
                return Conflict($"'{leaf}' already exists there.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Locked(target, ex);
            }

            return Results.Ok(new { path = target });
        })
            .WithTags("FileSystem")
            .HumansOnly()
            .WithSummary("Upload a file to the Host's filesystem")
            .WithDescription(
                "A multipart form with the file in `file` and the destination folder in `parent`. "
                + "Refused with 403 unless the containing root allows it (`allowCreate`). Only the "
                + "LEAF of the uploaded filename is kept, and that leaf is validated the same way a "
                + "directory name is: 400 for a NUL character, `.` or `..`. 400 also for a missing "
                + "file, an empty one, or one larger than "
                + $"{TeamDocuments.MaximumUploadBytes / (1024 * 1024)} MB. 409 if the destination "
                + "already exists - overwrite is `allowUpdate`, which has no route yet.");
    }

    /// <summary>
    /// Whether this root IS the instance's own data folder - the one a blank "Place team in" box
    /// resolves to, and the one <c>Teams.CreateAsync</c> collapses a typed path back to
    /// <c>null</c> for.
    ///
    /// Compared the same way <c>Teams.IsInstanceRoot</c> compares it - resolved, trailing
    /// separators trimmed, Ordinal-ignore-case - rather than by identity with the seeded root
    /// object, because the two lists are built independently and matching on the NAME would tie
    /// this to a display string (<c>"Harness (core)"</c>) that exists to be read, not matched.
    /// A second copy of that comparison is the one hazard here, and it is the cheaper of the two:
    /// the alternative is <c>FileBrowserRoot</c> growing a flag that every construction site has to
    /// remember to set, including the ones in tests.
    /// </summary>
    private static bool IsInstanceRoot(string rootPath, string dataRoot) =>
        string.Equals(
            TrimSeparators(rootPath),
            TrimSeparators(Path.GetFullPath(dataRoot)),
            StringComparison.OrdinalIgnoreCase);

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// A single path segment, safe to combine onto an already-contained parent and to write back
    /// to disk unchanged. Only what Linux refuses in one name: no `/`, no NUL (unfiltered, it
    /// reaches <c>Path.GetFullPath</c> outside any try/catch and is a 500 rather than a refusal),
    /// and not `.` or `..`. The host runs only on Linux, so Windows rules - `\`, `:` and a trailing
    /// dot or space - are not applied: Linux stores each of them as typed.
    /// `noun` names what is being validated ("folder", "file") only in the message, so the same
    /// check reads correctly for a directory name and an upload leaf alike.
    /// </summary>
    public static bool IsSingleSegmentName(string name, string noun, out string error)
    {
        if (name.Contains('/'))
        {
            error = $"'{name}' may not contain a path separator.";
            return false;
        }

        // An embedded NUL is legal C# but not in a Linux file name - Path.GetFullPath throws
        // ArgumentException on it, and both call sites resolve their target OUTSIDE any
        // try/catch, so an unfiltered NUL was a 500 rather than a refusal on either write route.
        if (name.Contains('\0'))
        {
            error = $"'{name}' may not contain a NUL character.";
            return false;
        }

        if (name is "." or "..")
        {
            error = $"'{name}' is not a legal {noun} name.";
            return false;
        }

        error = "";
        return true;
    }

    private static IResult Forbidden(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult Conflict(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// Deliberately NOT a 404 - the path exists, and "does not exist" and "exists but cannot be
    /// read" are different facts a folder picker needs to tell apart - and deliberately NOT a 200
    /// with an empty result, which would read as "this folder is empty" rather than as the refusal
    /// it actually is. Deliberately NOT 403 either, though every caller reaching here has already
    /// cleared `.HumansOnly()` and the containment check - both of which also answer 403 with the
    /// same `{ error: string }` shape, so a third use here would be indistinguishable from either.
    /// 423 Locked instead: unused elsewhere in this API, and semantically exact - the resource
    /// EXISTS but cannot be accessed, which is precisely this state and neither of the other two.
    ///
    /// The message names the path and stops at <c>ex.Message</c> - never the exception's type or
    /// its stack, which would leak server internals for no gain to someone about to pick another
    /// folder anyway.
    /// </summary>
    private static IResult Locked(string path, Exception ex) =>
        Results.Json(
            new { error = $"'{path}' exists but could not be used: {ex.Message} Pick another folder." },
            statusCode: StatusCodes.Status423Locked);
}

internal sealed record CreateHostDirectory(
    [property: Description("The folder to create the new directory inside - an absolute host path.")]
    string? Parent,
    [property: Description(
        "The new directory's name - a single path segment: no `/` or NUL character, and not `.` "
        + "or `..`.")]
    string? Name);
