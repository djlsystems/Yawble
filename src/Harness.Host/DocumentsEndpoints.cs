using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

internal static class DocumentsEndpoints
{
    public static void Map(WebApplication app)
    {
        // EVERY DOCUMENTS FOLDER ON DISK, WHETHER OR NOT ITS TEAM IS STILL THERE.
        //
        // The first half of what the Documents dialog needs, since it hangs off Projects rather than the
        // active team: it has to offer a team to look at before it can show one, and the set it offers is
        // the set of FOLDERS, which is strictly larger than the set of teams. That difference is the whole
        // item - a document outlives the team that wrote it.
        //
        // NOT UNDER /api/teams/{team}/, deliberately: it is not about one team, and a `{team}` route value
        // is what TeamGate keys on. This one carries none, so it does its own filtering, below and in the
        // open.
        //
        // HumansOnly. This is a picker for a person: it names folders across every team the caller can
        // reach, and no container has a reason to enumerate other teams' documents. The per-folder routes
        // keep RequirePermit(Read) so an agent goes on reading its OWN team's.
        app.MapGet("/api/documents", (TeamDocuments docs, TeamRegistry teams, HttpContext context) =>
        {
            if (PrincipalClaims.From(context.User) is null) return Results.Unauthorized();

            // EVERY FOLDER, retired ones and dead teams' included. HumansOnly, and a person reaches every
            // team and every documents folder - TeamGate's rule 3 is what opens a folder whose team no
            // longer exists, so a list filtered any narrower would hide exactly the work this list exists
            // to keep findable.
            //
            // A NAMED RECORD RATHER THAN AN ANONYMOUS OBJECT, so the wire shape is one the parity gate can
            // see and hold against `web/src/api/types.ts`. `exists` and `label` are the registry's answers
            // and are added HERE rather than on the disk record, which cannot check either - see
            // DocumentsFolder's own summary for why `exists` is not simply "the team is in the list".
            var folders = docs.Folders()
                .Select(folder => new DocumentsFolder(
                    folder.Folder,
                    folder.Team,
                    teams.LabelFor(folder.Team),
                    !folder.Retired && teams.ExistingName(folder.Team) is not null,
                    folder.Retired,
                    folder.Entries,
                    folder.ModifiedAt));

            // THE ROOT, so the dialog can say where a document is as an agent sees it: `HARNESS_SHARED` is
            // this root plus the team.
            return Results.Ok(new { folders, root = docs.TenantRoot });
        })
            .WithTags("Documents")
            .HumansOnly()
            .WithSummary("List the team documents folders that exist")
            .WithDescription(
                "One entry per folder under the tenant documents root, each saying whether that team still "
                + "exists. Documents are NOT removed when a team is deleted, so this list is larger than "
                + "the list of teams and the difference is the work that would otherwise have been lost "
                + "with them.\n\n"
                + "`folder` is the name to put in `/api/teams/{team}/documents` to browse it. For a live "
                + "team that IS the team identifier. For a RETIRED folder it is not and never can be: a "
                + "team created with a dead team's identifier does not inherit its documents - the "
                + "predecessor's folder is relabelled with a suffix no team name may contain, and `team` "
                + "still says whose it was.\n\n"
                + "Every person sees every folder, including those of teams that are gone.");

        // A team's documents. Every path arrives from outside and is resolved against the team's own
        // folder before anything touches disk - see TeamDocuments. A team name is matched
        // case-insensitively and then used in its STORED spelling, so the folder a request reaches is the
        // one the board shows.
        // WithTags only. A group-level WithDescription would be REPLACED on every route that sets its own,
        // which is most of them - see Describe.Documents, which is appended per route instead.
        var documents = app.MapGroup("/api/teams/{team}/documents").WithTags("Documents");

        documents.MapGet("", (
            [Description(Describe.Team)] string team,
            [Description(
                "The folder to list, relative to the team's documents root. Omit it for the root itself.")]
            string? path,
            [Description("List the whole tree beneath `path` rather than just its immediate children.")]
            bool? recursive,
            TeamRegistry teams, TeamDocuments docs) =>
            DocumentsFolderOf(teams, docs, team) is not { } folder
                ? Refusals.NoTeam(team)
                : Documents(() => Results.Ok(docs.List(folder, path, recursive ?? false))))
            .RequirePermit(Permits.Read)
            .WithSummary("List a team's documents")
            .WithDescription(
                "The files and folders a team shares with its members - what a person uploads for an agent "
                + "to read, and what an agent leaves behind." + Describe.Documents);

        documents.MapGet("/content", (
            [Description(Describe.Team)] string team,
            [Description("The file to download, relative to the team's documents root.")] string path,
            TeamRegistry teams, TeamDocuments docs) =>
        {
            if (DocumentsFolderOf(teams, docs, team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            return Documents(() =>
            {
                var file = docs.Resolve(stored, path);

                if (!File.Exists(file)) return Results.NotFound(new { error = "No such document." });

                // A download rather than an inline render: this serves whatever a person or an agent put
                // there, and handing the browser an HTML document to execute on this origin would make an
                // uploaded file into a script running inside the session.
                return Results.File(file, "application/octet-stream", Path.GetFileName(file));
            });
        })
            .RequirePermit(Permits.Read)
            .WithSummary("Download a document")
            .WithDescription(
                "Returns the file's bytes as a download, always as `application/octet-stream` and never "
                + "inline. This serves whatever a person or an agent put there, and handing the browser an "
                + "uploaded HTML document to render would make it a script running on this origin, inside "
                + "the session." + Describe.Documents);

        // The same file as /content, shown rather than downloaded: by extension only,
        // told to the browser exactly (nosniff), and inside a sandboxed CSP that replaces the app's - see
        // DocumentView for why an uploaded HTML page can render on this origin without running.
        documents.MapGet("/view", async (
            [Description(Describe.Team)] string team,
            [Description("The file to show, relative to the team's documents root.")] string path,
            TeamRegistry teams, TeamDocuments docs, HttpContext context, CancellationToken ct) =>
        {
            if (DocumentsFolderOf(teams, docs, team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            return await DocumentsAsync(async () =>
            {
                var file = docs.Resolve(stored, path);

                if (!File.Exists(file)) return Results.NotFound(new { error = "No such document." });

                var name = Path.GetFileName(file);

                if (DocumentView.For(name) is not { } type)
                {
                    return Results.Json(
                        new { error = "This file cannot be shown in the browser. Download it instead." },
                        statusCode: StatusCodes.Status415UnsupportedMediaType);
                }

                context.Items[SecurityHeaders.PolicyOverride] = DocumentView.PolicyFor(type.Kind);
                context.Response.Headers.ContentDisposition =
                    new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline") { FileNameStar = name }.ToString();

                if (type.Kind == DocumentView.Kind.Markdown)
                {
                    var markdown = await File.ReadAllTextAsync(file, ct);
                    return Results.Text(DocumentView.RenderMarkdown(name, markdown), type.ContentType);
                }

                return Results.File(file, type.ContentType);
            });
        })
            .RequirePermit(Permits.Read)
            .WithSummary("Show a document in the browser")
            .WithDescription(
                "Returns the file inline, typed by its extension: Markdown (`.md`, `.markdown`) rendered to "
                + "an HTML page with raw HTML disabled; HTML as `text/html`; text and source files as "
                + "`text/plain; charset=utf-8`; images as their type; PDF as `application/pdf`. Anything "
                + "else answers 415 - download it with `/content`.\n\n"
                + "Every response carries `X-Content-Type-Options: nosniff`, and every one REPLACES the "
                + "app's Content-Security-Policy with a sandboxed one: `" + DocumentView.SandboxPolicy + "`, "
                + "or `" + DocumentView.PdfPolicy + "` for PDF. No script runs and the page has an opaque "
                + "origin, so it cannot read the session or call this API.\n\n"
                + "Like the other read routes it serves a live team's folder, a gone team's, or a retired "
                + "one, named by the folder on disk." + Describe.Documents);

        documents.MapPost("/folders", (
            [Description(Describe.Team)] string team,
            NewFolder request, TeamRegistry teams, TeamDocuments docs) =>
            teams.ExistingName(team) is not { } stored
                ? Refusals.NoTeam(team)
                : Documents(() => Results.Ok(docs.CreateFolder(stored, request.Path ?? ""))))
            .HumansOnly()
            .WithSummary("Create a folder")
            .WithDescription(
                "Creates a folder, and any missing folders above it." + Describe.Documents);

        documents.MapPost("/upload", async (
            [Description(Describe.Team)] string team,
            HttpRequest request, TeamRegistry teams, TeamDocuments docs, FolderWatch folders,
            TenantLogging audit, SolutionWait waiting, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Send a file." });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");

            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Send a file." });

            if (file.Length > TeamDocuments.MaximumUploadBytes)
            {
                return Results.BadRequest(new
                {
                    error = $"That file is larger than {TeamDocuments.MaximumUploadBytes / (1024 * 1024)} MB.",
                });
            }

            // WHAT TO DO ABOUT A NAME ALREADY THERE. Absent, an upload replaces a file as it always has; a
            // drop from the Documents dialog asks first, the way a paste does.
            var onClash = form["onClash"].ToString() is { Length: > 0 } sent ? sent : null;

            if (onClash is not (null or "ask" or TeamDocuments.KeepBoth or TeamDocuments.Replace or TeamDocuments.Skip))
            {
                return Results.BadRequest(new { error = "onClash is ask, keep-both, replace or skip." });
            }

            await using var content = file.OpenReadStream();

            return await DocumentsAsync(async () =>
            {
                var name = file.FileName;

                if (onClash is not null && docs.UploadTarget(stored, form["path"], name) is { } target
                    && (target.IsFile || target.IsFolder))
                {
                    var where = Path.GetDirectoryName(target.Path)?.Replace('\\', '/') is { Length: > 0 } parent ? parent : stored;

                    switch (onClash)
                    {
                        case "ask":
                            return Results.Json(
                                new
                                {
                                    error = $"{target.Name} is already in {where}. Choose Keep both, Replace or Skip.",
                                    clashes = new[] { new DocumentsClash(target.Name, target.Path, target.IsFolder) },
                                },
                                statusCode: StatusCodes.Status409Conflict);
                        case TeamDocuments.Skip:
                            return Results.Ok(new { skipped = true, path = target.Path });
                        case TeamDocuments.Replace when target.IsFolder:
                            // AN UPLOAD WRITES NO ROW BEFORE IT ACTS, so it never removes a tree.
                            return Results.Conflict(new { error = $"{target.Name} is a folder in {where}; an upload replaces only a file." });
                        case TeamDocuments.KeepBoth:
                            var directory = Path.GetDirectoryName(Path.Combine(docs.RootFor(stored), target.Path))!;
                            name = TeamDocuments.FreeName(target.Name, isFolder: false, free => Path.Exists(Path.Combine(directory, free)));
                            break;
                    }
                }

                var saved = await docs.SaveAsync(stored, form["path"], name, content, ct);

                // THE PLATFORM ANNOUNCES ITS OWN WRITE, at once and with no poll: it performed it.
                await folders.AnnounceAsync(
                    stored,
                    Path.GetDirectoryName(saved.Path)?.Replace('\\', '/') ?? "",
                    [saved.Path],
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
                    ct);

                // RECORDED LIKE EVERY OTHER PERSON'S WRITE: who put which file where, and how big it was.
                // Never the contents - this table is readable by every person and kept forever.
                await audit.WriteAsync(
                    context, TenantActions.DocumentUploaded, stored, saved.Path,
                    new { team = stored, path = saved.Path, size = saved.Size, onClash },
                    ct);

                // A MISSING REQUIRED INPUT PROVIDED ends the team's wait now, and its members' marks with it.
                await waiting.WaitingForAsync(stored, ct);

                return Results.Ok(saved);
            });
        })
            .HumansOnly()
            .WithSummary("Upload a document")
            .WithDescription(
                "A multipart form with the file in `file` and an optional destination folder in `path`.\n\n"
                + "Only the LEAF of the uploaded filename is kept, so a name carrying directory separators "
                + "cannot place the file anywhere but where `path` says. 400 for a missing file, an empty "
                + "one, one named for the documents marker (under every `onClash`), "
                + $"or one larger than {TeamDocuments.MaximumUploadBytes / (1024 * 1024)} MB. Audited as "
                + "`document.uploaded`, with the path and size and never the contents.\n\n"
                + "An optional `onClash` says what to do when the name is already taken. Absent, a file of "
                + "that name is replaced, as it always has been. `ask` answers 409 with `clashes` and "
                + "writes nothing; `keep-both` saves under the first free `name (copy).ext`; `replace` "
                + "replaces a file and refuses a folder of that name with 409; `skip` answers "
                + "`{ skipped: true, path }` and writes nothing."
                + Describe.Documents);

        const string PackageClashDescription =
            "An optional `onClash` says what to do when the folder's name is already taken, as for a file "
            + "upload. Absent, a folder of that name takes the files, each replacing a file of its path, "
            + "and a file of that name is 409. `ask` answers 409 with `clashes` and writes nothing; "
            + "`keep-both` makes the first free `name (copy)`; `replace` lets a file of that name give way "
            + "and refuses a folder with 409; `skip` answers `{ skipped: true, path }` and writes nothing. "
            + "A link, a file where a folder is needed or a folder where a file would go is 409, and "
            + "nothing is written. Audited as one `document.uploaded` with the path, file count and size.";

        documents.MapPost("/upload-folder", async (
            [Description(Describe.Team)] string team,
            HttpRequest request, TeamRegistry teams, TeamDocuments docs, FolderWatch folders,
            TenantLogging audit, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Send a folder." });

            var form = await request.ReadFormAsync(ct);
            var files = form.Files.GetFiles("file");
            var relativePaths = form["relativePath"];

            if (files.Count == 0) return Results.BadRequest(new { error = "Send a folder." });

            if (relativePaths.Count != files.Count)
            {
                return Results.BadRequest(new { error = "Send one relativePath for each file, in the same order." });
            }

            return await DocumentsAsync(() => UploadPackageAsync(
                stored, form,
                () => TeamDocuments.FolderUpload(
                    [.. files.Select((file, i) => (relativePaths[i] ?? "", file.Length, (Func<Stream>)file.OpenReadStream))]),
                "folder", docs, folders, audit, context, ct));
        })
            .HumansOnly()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(TeamDocuments.MaximumPackageBytes + 1024 * 1024))
            .WithSummary("Upload a folder")
            .WithDescription(
                "A multipart form with one `file` part per file and, in the same order, one `relativePath` "
                + "per file: its path inside the folder, top folder first, as a browser's folder upload gives "
                + "it (`File.webkitRelativePath`). Every path is in one top folder, which is made in the "
                + "optional destination folder `path` with the subfolders kept.\n\n"
                + $"At most {TeamDocuments.MaximumPackageFiles} files, each at most "
                + $"{TeamDocuments.MaximumUploadBytes / (1024 * 1024)} MB and "
                + $"{TeamDocuments.MaximumPackageBytes / (1024 * 1024)} MB together. A path that is absolute, "
                + "has a `..`, `.` or empty segment, or names the documents marker is refused with 400 and "
                + "nothing is written.\n\n"
                + PackageClashDescription
                + Describe.Documents);

        documents.MapPost("/upload-zip", async (
            [Description(Describe.Team)] string team,
            HttpRequest request, TeamRegistry teams, TeamDocuments docs, FolderWatch folders,
            TenantLogging audit, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            return await UploadZipFormAsync(request, (form, read) => DocumentsAsync(() => UploadPackageAsync(
                stored, form, read, "zip", docs, folders, audit, context, ct)), ct);
        })
            .HumansOnly()
            // THE BODY LIMIT IS THE ZIP'S OWN, plus room for the form around it: one over it is refused
            // before it is read, and one just under it reaches the size check and its sentence.
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(TeamDocuments.MaximumUploadBytes + 1024 * 1024))
            .WithSummary("Upload a .zip and unpack it")
            .WithDescription(
                "A multipart form with the zip in `file` and an optional destination folder in `path`. The "
                + "zip is unpacked into a folder named after it (`job-tracker.zip` makes `job-tracker`); when "
                + "every entry already sits in a folder of that name it is not doubled, and `__MACOSX/` is "
                + "left out.\n\n"
                + "REFUSED WHOLE, with 400 and nothing written, when any entry is a link (or anything but a "
                + "plain file or folder), an absolute path, or a path with a `..` in it, when the zip's name "
                + "is not a plain folder name (`..zip`, `...zip`) or is the documents marker's, and when the zip "
                + $"cannot be read, is over {TeamDocuments.MaximumUploadBytes / (1024 * 1024)} MB, holds more "
                + $"than {TeamDocuments.MaximumPackageFiles} entries, or unpacks to more than "
                + $"{TeamDocuments.MaximumPackageBytes / (1024 * 1024)} MB. A request body more than 1 MB over "
                + "the zip's limit is refused with 413 before it is read.\n\n"
                + PackageClashDescription
                + Describe.Documents);

        // AN UPLOAD INTO THE INSTANCE'S OWN DOCUMENTS, needing no team: a fresh instance has none, and a
        // package uploaded to install must still have somewhere to go. Always `Uploads/<zip name>` under the
        // documents root, as a catalog fetch is always `Marketplace/<id>-<version>`; the zip is checked whole
        // by the same `ZipUpload` as the team route, and the clash choices are the same.
        app.MapPost("/api/documents/upload-zip", (
            HttpRequest request, TeamDocuments docs, TenantLogging audit, HttpContext context, CancellationToken ct) =>
            UploadZipFormAsync(request, (form, read) => DocumentsAsync(() => UploadPackageIntoAsync(
                form, read, InstancePlace(docs, ct),
                skipped: path => new { skipped = true, path, absolutePath = docs.InstancePath(path) },
                saved: async saved =>
                {
                    // ONE ROW FOR THE UPLOAD, as the team route writes it, with no team: where it went, how
                    // many files and how big. Never the contents.
                    await audit.WriteAsync(
                        context, TenantActions.DocumentUploaded, saved.Path, saved.Name,
                        new
                        {
                            team = (string?)null, path = saved.Path, size = saved.Size, files = saved.Files.Count, kind = "zip",
                            onClash = OnClashOf(form),
                        },
                        ct);

                    return Results.Ok(new InstancePackageSaved(
                        saved.Path, saved.Name, saved.IsFolder, saved.Files, saved.Size, docs.InstancePath(saved.Path)));
                })), ct))
            .WithTags("Documents")
            .HumansOnly()
            // THE BODY LIMIT IS THE ZIP'S OWN, plus room for the form around it, as the team route's.
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(TeamDocuments.MaximumUploadBytes + 1024 * 1024))
            .WithSummary("Upload a .zip into the instance's documents and unpack it")
            .WithDescription(
                "The team route's .zip upload, needing no team: a multipart form with the zip in `file` and "
                + $"an optional `onClash`. It is unpacked into `{TeamDocuments.UploadsFolder}/<zip name>` of the "
                + "instance's documents root, by the same rules and refusals as "
                + "`POST /api/teams/{team}/documents/upload-zip`; a link on the way, "
                + $"`{TeamDocuments.UploadsFolder}` itself included, is 409 and nothing is written. The answer "
                + "carries `absolutePath`, the folder as the install routes take it.\n\n"
                + PackageClashDescription);

        documents.MapDelete("", async (
            [Description(Describe.Team)] string team,
            [Description(
                "The file or folder to delete, relative to the team's documents root. Omit it, for a team "
                + "that no longer exists or a retired folder, to delete the whole documents folder.")]
            string? path,
            [Description(
                "Delete a folder and everything in it. Without it a folder that still has something in it "
                + "is refused with 409.")]
            bool? recursive,
            TeamRegistry teams, TeamDocuments docs, FolderWatch folders, FolderRemoval removal, ITenantLog tenantLog,
            HttpContext context, CancellationToken ct) =>
        {
            // RESOLVED LIKE THE READ ROUTES: a gone team's folder and a retired one can be cleared
            // out. A person may delete a record; upload and new folder still refuse to add to one.
            if (DocumentsFolderOf(teams, docs, team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            var live = !TeamPaths.IsRetiredDocumentsFolder(stored) && teams.ExistingName(stored) is not null;

            return await DocumentsAsync(async () =>
            {
                // PLANNED FIRST, so every refusal - outside the folder, the marker, not empty, a live
                // team's root, a whole folder the platform did not make - is answered before anything is
                // recorded or removed.
                var plan = docs.Plan(stored, path, recursive ?? false, wholeFolderAllowed: !live);

                // RECORDED BEFORE REMOVED, and not swallowed the way TenantLogging swallows: the delete
                // does not happen when its row cannot be written. A delete nobody can account for is the
                // one thing a record of deletes exists to rule out.
                try
                {
                    await tenantLog.WriteAsync(
                        context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                        context.User.FindFirstValue(ClaimTypes.Email),
                        TenantActions.DocumentsDeleted,
                        stored,
                        plan.Path.Length == 0 ? stored : plan.Path,
                        JsonSerializer.Serialize(new
                        {
                            folder = stored,
                            path = plan.Path,
                            isFolder = plan.IsFolder,
                            files = plan.Files.Count,
                        }),
                        ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Results.Problem(
                        "The delete could not be recorded, so nothing was deleted.",
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                // THROUGH FolderRemoval, never a recursive delete: an agent's owner-only folder is removed
                // as the agent where the Host switches users, links are never followed, and what cannot go
                // is named with why instead of escaping as an unhandled exception after the row above.
                var root = docs.RootFor(stored);
                var report = await removal.RemoveDocumentsAsync(root, plan.Absolute, TeamPaths.TeamOfDocumentsFolder(stored), ct);
                var removed = plan.Files.Where(file => !TeamDocuments.StillThere(Path.Combine(root, file))).ToList();

                // Announced after the delete, ONE ANNOUNCEMENT PER FOLDER a removed file was in, exactly
                // as a single-file delete is announced - and only the files that went. A gone team has no
                // triggers.
                if (live)
                {
                    var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

                    foreach (var group in removed.GroupBy(
                        file => Path.GetDirectoryName(file)?.Replace('\\', '/') ?? "", StringComparer.Ordinal))
                    {
                        await folders.AnnounceAsync(stored, group.Key, [.. group], actor, ct);
                    }
                }

                if (report.Complete) return Results.NoContent();

                // WHAT WAS LEFT, AND WHY, as a row after the `documents.deleted` above - so the log never
                // claims a deletion that did not happen - and as the answer's sentence.
                var left = report.Refused is { } refused
                    ? [new DocumentLeft(plan.Path.Length == 0 ? "" : plan.Path, refused)]
                    : report.Remaining
                        .Select(path => new DocumentLeft(
                            docs.Relative(root, path) is var relative && relative == "." ? "" : relative,
                            report.Reasons?.GetValueOrDefault(path) ?? "still there after the delete"))
                        .ToList();
                var nothing = removed.Count == 0 && TeamDocuments.StillThere(plan.Absolute);
                var sentence =
                    (nothing
                        ? "Nothing was removed. "
                        : $"The delete did not finish: {removed.Count} of {plan.Files.Count} file(s) were removed. ")
                    + "Still there: "
                    + string.Join("; ", left.Select(l => $"{(l.Path.Length == 0 ? $"the folder {stored}" : l.Path)} ({l.Reason})"))
                    + ". Delete it again once that is fixed.";

                try
                {
                    await tenantLog.WriteAsync(
                        context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                        context.User.FindFirstValue(ClaimTypes.Email),
                        TenantActions.DocumentsDeleteIncomplete,
                        stored,
                        plan.Path.Length == 0 ? stored : plan.Path,
                        JsonSerializer.Serialize(new
                        {
                            folder = stored,
                            path = plan.Path,
                            files = plan.Files.Count,
                            removed = removed.Count,
                            remaining = left.Select(l => new { path = l.Path, reason = l.Reason }),
                        }),
                        ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    sentence += " The record of what was left could not be written.";
                }

                return Results.Json(
                    new { error = sentence, removed = removed.Count, remaining = left },
                    statusCode: StatusCodes.Status409Conflict);
            });
        })
            .HumansOnly()
            .WithSummary("Delete a document or folder")
            .WithDescription(
                "204 on success. A folder that still has something in it is refused with 409 unless "
                + "`recursive=true` - the one refusal here a person is expected to meet and act on.\n\n"
                + "A delete the Host cannot complete answers 409 with a sentence in `error` naming each path "
                + "left and why (permission denied, in use), `remaining` listing them and `removed` counting "
                + "the files that went; one that removed nothing says so. It is followed by a "
                + "`documents.delete-incomplete` tenant event naming the paths left, and deleting again once "
                + "they are removable finishes it.\n\n"
                + "Works for a team that no longer exists and for a retired folder, as the read routes do. "
                + "For those, and only those, omitting `path` deletes the whole documents folder, and only "
                + "when its marker says the platform created it. A live team's documents folder itself "
                + "cannot be deleted.\n\n"
                + "Every delete appends a `documents.deleted` tenant event naming the folder, the path, "
                + "whether it was a folder and the number of files removed; nothing is deleted when that "
                + "row cannot be written." + Describe.Documents);

        // RENAME, MOVE AND COPY: a person's batch changes, one source folder per request. Each is planned
        // whole before anything is recorded, its row is written before anything is done, and a batch that
        // partly fails says path by path what was done - the delete's pattern, kept by DocumentsChangeAsync
        // for all three. Dragging and dropping in the Documents dialog calls these same routes.
        documents.MapPost("/rename", (
            [Description(Describe.Team)] string team,
            DocumentsRename request, TeamRegistry teams, TeamDocuments docs, FolderWatch folders, FolderRemoval removal,
            ITenantLog tenantLog, HttpContext context, CancellationToken ct) =>
            DocumentsChangeAsync(
                DocumentsVerb.Rename, team, request.Items?.Select(item => (item.Path, item.Name)).ToList(), null,
                teams, docs, folders, removal, tenantLog, context, ct))
            .HumansOnly()
            .WithSummary("Rename documents")
            .WithDescription(
                "Gives each item in `items` a new leaf name in the folder it is in. The whole batch is "
                + "checked before anything is renamed: a bad name, a name already there, the marker, a link, "
                + "the folder itself, or anything in a gone team's or retired folder refuses the request "
                + "with a sentence in `error`, and nothing is recorded or renamed.\n\n"
                + Describe.DocumentsChange + Describe.Documents);

        documents.MapPost("/move", (
            [Description(Describe.Team)] string team,
            DocumentsTransfer request, TeamRegistry teams, TeamDocuments docs, FolderWatch folders, FolderRemoval removal,
            ITenantLog tenantLog, HttpContext context, CancellationToken ct) =>
            DocumentsChangeAsync(
                DocumentsVerb.Move, team, request.Items?.Select(item => (item.Path, item.OnClash)).ToList(), request.To,
                teams, docs, folders, removal, tenantLog, context, ct))
            .HumansOnly()
            .WithSummary("Move documents")
            .WithDescription(
                "Moves each item in `items` into the folder `to` names: `to.folder` is a documents folder "
                + "(this one or another team's) and `to.path` a folder in it. A move is one rename on disk "
                + "and never a copy and a delete, so it never half-happens. Moving out of a gone team's or "
                + "retired folder is allowed; moving into one is refused. A folder holding a link does not "
                + "leave its team.\n\n"
                + Describe.DocumentsTransfer + Describe.DocumentsChange + Describe.Documents);

        documents.MapPost("/copy", (
            [Description(Describe.Team)] string team,
            DocumentsTransfer request, TeamRegistry teams, TeamDocuments docs, FolderWatch folders, FolderRemoval removal,
            ITenantLog tenantLog, HttpContext context, CancellationToken ct) =>
            DocumentsChangeAsync(
                DocumentsVerb.Copy, team, request.Items?.Select(item => (item.Path, item.OnClash)).ToList(), request.To,
                teams, docs, folders, removal, tenantLog, context, ct))
            .HumansOnly()
            .WithSummary("Copy documents")
            .WithDescription(
                "Copies each item in `items` into the folder `to` names, as for a move. Copying into the "
                + "folder an item is already in keeps both without asking. Links inside a copied folder are "
                + "never followed: they are left out and named in that item's `notCopied`. A copy that fails "
                + $"part way removes what it made for that item. At most {TeamDocuments.MaximumCopyFiles} "
                + "files in one request.\n\n"
                + Describe.DocumentsTransfer + Describe.DocumentsChange + Describe.Documents);
    }

    /// <summary>
    /// The one place a documents failure becomes a status code.
    ///
    /// Written once rather than per endpoint because the mapping is the contract: a path outside the
    /// team is a 400 and not a 404 (a 404 would answer "does that file exist?" about somewhere the
    /// caller has no business), a missing file is a 404, and a folder that still has something in it
    /// is a 409 - the one refusal a person is expected to hit and act on.
    /// </summary>
    /// <summary>
    /// The documents folder a READ may name: a live team in its stored spelling, or a folder on disk
    /// whose team is gone. Null when there is neither, which the caller turns into the same 404 an
    /// unknown team has always answered.
    ///
    /// **THIS IS WHAT LETS A DELETED TEAM'S DOCUMENTS BE READ AT ALL**, and it is why they need no
    /// second route group: `/api/teams/{team}/documents` names its team EXPLICITLY rather than
    /// implying it from whichever team is active, so the only thing that could stop it serving a
    /// dead team's folder is a lookup insisting the team be live.
    ///
    /// **THE GATE IS UNCHANGED AND IS NOT HERE.** `TeamGate` keys on the route's `{team}` value, so
    /// these routes are gated by structure: a live team is reachable by everyone it binds, and a team
    /// that no longer exists is absent from every effective set - which leaves rule 3 as the only way
    /// through and makes a dead team's folder PEOPLE ONLY. That is the
    /// safe default, and nothing here re-decides it: a helper that widened who may read would
    /// be a second answer sitting underneath the first.
    ///
    /// ADDING WRITES DO NOT USE THIS. Upload and create-folder keep asking `ExistingName` and keep
    /// answering 404 for a team that is gone: a dead team's documents are a record of what happened,
    /// and a record is not added to. DELETE DOES: a person may clear a record out.
    ///
    /// THE FOLDER MUST BE THERE ON DISK for the dead-team arm. Composing the path alone would answer
    /// 200-with-nothing for every misspelling, which reads as "that team had no documents" - a
    /// silent-empty state.
    /// </summary>
    static string? DocumentsFolderOf(TeamRegistry teams, TeamDocuments docs, string team)
    {
        if (teams.ExistingName(team) is { } stored)
        {
            // REPAIRED FOR A LIVE TEAM, and only for a live team. Creating the folder on read is what
            // brings a team whose directory was removed by hand back working; a folder is also
            // reachable by the name of a team that no longer exists, and repairing there would
            // manufacture an empty folder for every name anybody mistyped.
            docs.EnsureFor(stored);

            return stored;
        }

        // Checked before composing, not after: TeamPaths refuses a segment it did not compose, and a
        // route value is the one place a name arrives that ContainerId has never vetted.
        if (!TeamPaths.IsDocumentsFolder(team)) return null;

        return Directory.Exists(docs.RootFor(team)) ? team : null;
    }

    /// <summary>
    /// One rename, move or copy request, start to finish: plan, refuse, record, act, announce, and say
    /// what was left. THE ORDER IS THE CONTRACT, as it is for a delete: nothing is recorded for a
    /// refusal, nothing is done without its row, and a partial result is a 409 with a second row naming
    /// what was not done - never an unhandled error.
    /// </summary>
    static async Task<IResult> DocumentsChangeAsync(
        DocumentsVerb verb,
        string team,
        IReadOnlyList<(string? Path, string? Detail)>? items,
        DocumentsTransferTarget? to,
        TeamRegistry teams,
        TeamDocuments docs,
        FolderWatch folders,
        FolderRemoval removal,
        ITenantLog tenantLog,
        HttpContext context,
        CancellationToken ct)
    {
        // A gone team's folder and a retired one may be a SOURCE, as for a delete: a person may take
        // things out of a record. Nothing may be added to one, or renamed in one.
        if (DocumentsFolderOf(teams, docs, team) is not { } stored)
        {
            return Refusals.NoTeam(team);
        }

        bool Live(string folder) => !TeamPaths.IsRetiredDocumentsFolder(folder) && teams.ExistingName(folder) is not null;

        string? Closed(string folder, string refused) =>
            Live(folder)
                ? null
                : (TeamPaths.IsRetiredDocumentsFolder(folder)
                    ? $"This folder belongs to an earlier team called {TeamPaths.TeamOfDocumentsFolder(folder)}."
                    : $"{teams.LabelFor(TeamPaths.TeamOfDocumentsFolder(folder))} no longer exists.")
                + $" Its documents can be read, copied or moved out, and deleted, but {refused}.";

        var (verbed, noun, action, incomplete) = verb switch
        {
            DocumentsVerb.Rename => ("renamed", "rename", TenantActions.DocumentsRenamed, TenantActions.DocumentsRenameIncomplete),
            DocumentsVerb.Move => ("moved", "move", TenantActions.DocumentsMoved, TenantActions.DocumentsMoveIncomplete),
            _ => ("copied", "copy", TenantActions.DocumentsCopied, TenantActions.DocumentsCopyIncomplete),
        };

        DocumentsChangePlan plan;

        try
        {
            if (verb == DocumentsVerb.Rename)
            {
                if (Closed(stored, "nothing in them can be renamed") is { } closed) return Results.Conflict(new { error = closed });

                plan = docs.PlanRename(stored, items);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(to?.Folder))
                {
                    return Results.BadRequest(new { error = $"Name the folder to {noun} them into in to.folder." });
                }

                if (DocumentsFolderOf(teams, docs, to.Folder) is not { } destination)
                {
                    return Results.NotFound(new { error = $"No documents folder '{to.Folder}'." });
                }

                if (Closed(destination, "nothing can be added to them") is { } closed) return Results.Conflict(new { error = closed });

                plan = docs.PlanTransfer(verb, stored, destination, to.Path, items);
            }
        }
        catch (DocumentsChangeRefusedException refused)
        {
            return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
        }
        catch (DocumentPathException refused)
        {
            return Results.BadRequest(new { error = refused.Message });
        }

        // A CLASH NOBODY CHOSE FOR: asked about, with nothing recorded or done. The person answers per
        // item and the request comes again with `onClash`.
        if (plan.Clashes.Count > 0)
        {
            var into = plan.DestinationPath.Length == 0 ? plan.Destination : plan.DestinationPath;
            var names = string.Join(", ", plan.Clashes.Select(clash => Path.GetFileName(clash.To)));
            var sentence = plan.Clashes.Count == 1
                ? $"{names} is already in {into}. Choose Keep both, Replace or Skip."
                : $"{plan.Clashes.Count} of these are already in {into}: {names}. Choose Keep both, Replace or Skip for each.";

            return Results.Json(new { error = sentence, clashes = plan.Clashes }, statusCode: StatusCodes.Status409Conflict);
        }

        var (actorId, email) = ActorClaims.Of(context.User);
        var first = plan.Items[0].From;

        // RECORDED BEFORE DONE, naming every source, destination and replaced path, and not swallowed:
        // a move whose row cannot be written does not happen, so nothing leaves the documents unrecorded.
        try
        {
            await tenantLog.WriteAsync(
                actorId, email, action, stored, first.Length == 0 ? stored : first,
                verb == DocumentsVerb.Rename
                    ? JsonSerializer.Serialize(new
                    {
                        folder = stored,
                        items = plan.Items.Select(item => new { from = item.From, to = item.To, files = item.Files.Count }),
                    })
                    : JsonSerializer.Serialize(new
                    {
                        folder = stored,
                        to = new { folder = plan.Destination, path = plan.DestinationPath },
                        items = plan.Items.Select(item => new
                        {
                            from = item.From,
                            to = item.To,
                            onClash = item.OnClash,
                            files = item.Files.Count,
                            outcome = item.Settled,
                        }),
                        replaced = plan.Items.Where(item => item.Replaces is not null).Select(item => item.Replaces),
                    }),
                ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.Json(
                new { error = $"The {noun} could not be recorded, so nothing was {verbed}." },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var applied = await docs.ApplyAsync(plan, removal, ct);

        // ANNOUNCED AFTER, one announcement per folder and only what changed: what left the source,
        // what arrived, and what a Replace removed. A gone or retired folder has no triggers.
        var actor = actorId ?? "unknown";
        var changed = applied
            .SelectMany(item => item.Left.Select(path => (Folder: stored, Path: path))
                .Concat(item.Arrived.Concat(item.Removed).Select(path => (Folder: plan.Destination, Path: path))))
            .Where(change => Live(change.Folder));

        foreach (var folder in changed.GroupBy(change => change.Folder, StringComparer.Ordinal))
        {
            foreach (var group in folder.GroupBy(
                change => Path.GetDirectoryName(change.Path)?.Replace('\\', '/') ?? "", StringComparer.Ordinal))
            {
                await folders.AnnounceAsync(folder.Key, group.Key, [.. group.Select(change => change.Path).Distinct(StringComparer.Ordinal)], actor, ct);
            }
        }

        var results = applied.Select(item => item.Result).ToList();
        var failed = results.Where(result => result.Outcome == "failed").ToList();

        if (failed.Count == 0) return Results.Ok(new { results });

        var done = results.Count(result => result.Outcome == "done");
        var said =
            (done == 0
                ? $"Nothing was {verbed}. "
                : $"The {noun} did not finish: {done} of {results.Count} {verbed}. ")
            + $"Not {verbed}: "
            + string.Join("; ", failed.Select(result => $"{(result.From.Length == 0 ? stored : result.From)} ({result.Reason})"))
            + $". {char.ToUpperInvariant(noun[0])}{noun[1..]} them again once that is fixed.";

        // WHAT WAS NOT DONE, AND WHY, as a row after the first - so the log never claims a change that
        // did not happen.
        try
        {
            await tenantLog.WriteAsync(
                actorId, email, incomplete, stored, first.Length == 0 ? stored : first,
                JsonSerializer.Serialize(new
                {
                    folder = stored,
                    to = verb == DocumentsVerb.Rename ? null : new { folder = plan.Destination, path = plan.DestinationPath },
                    done = results.Where(result => result.Outcome == "done").Select(result => result.From),
                    failed = failed.Select(result => new { from = result.From, reason = result.Reason }),
                }),
                ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            said += " The record of what was left could not be written.";
        }

        return Results.Json(new { error = said, results }, statusCode: StatusCodes.Status409Conflict);
    }

    static IResult Documents(Func<IResult> action) =>
        DocumentsAsync(() => Task.FromResult(action())).GetAwaiter().GetResult();

    static async Task<IResult> DocumentsAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (DocumentPathException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound(new { error = "No such document." });
        }
        catch (DirectoryNotFoundException)
        {
            return Results.NotFound(new { error = "No such folder." });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (IOException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    // A FOLDER OR A ZIP, given the clash choices a file upload has, applied to the folder it makes: ask
    // answers 409 naming it, keep-both makes `name (copy)`, replace lets a file of that name give way and
    // refuses a folder (an upload writes no row before it acts, so it never removes a tree), skip writes
    // nothing. Without one, a folder of that name takes the files, as uploading each file would.
    static Task<IResult> UploadPackageAsync(
        string stored, IFormCollection form, Func<PackageUpload> read, string kind, TeamDocuments docs, FolderWatch folders,
        TenantLogging audit, HttpContext context, CancellationToken ct)
    {
        var place = new PackagePlace(
            docs.RootFor(stored),
            stored,
            name => docs.UploadTarget(stored, form["path"], name),
            (name, upload, replaceFile) => docs.SavePackageAsync(stored, form["path"], name, upload, replaceFile, ct));

        return UploadPackageIntoAsync(
            form, read, place,
            skipped: path => new { skipped = true, path },
            saved: async saved =>
            {
                var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

                // ONE ANNOUNCEMENT PER FOLDER that got files, as a recursive delete announces.
                foreach (var group in saved.Files.GroupBy(
                    file => Path.GetDirectoryName(file)?.Replace('\\', '/') ?? "", StringComparer.Ordinal))
                {
                    await folders.AnnounceAsync(stored, group.Key, [.. group], actor, ct);
                }

                // ONE ROW FOR THE UPLOAD: where it went, how many files and how big. Never the contents.
                await audit.WriteAsync(
                    context, TenantActions.DocumentUploaded, stored, saved.Path,
                    new { team = stored, path = saved.Path, size = saved.Size, files = saved.Files.Count, kind, onClash = OnClashOf(form) },
                    ct);

                // A MISSING REQUIRED INPUT PROVIDED ends the team's wait now, as a single file's upload does.
                await context.RequestServices.GetRequiredService<SolutionWait>().WaitingForAsync(stored, ct);

                return Results.Ok(saved);
            });
    }

    static string? OnClashOf(IFormCollection form) => form["onClash"].ToString() is { Length: > 0 } sent ? sent : null;

    // THE INSTANCE'S UPLOADS FOLDER as the place a package goes, for the zip route that needs no team.
    static PackagePlace InstancePlace(TeamDocuments docs, CancellationToken ct) => new(
        docs.InstanceRoot,
        TeamDocuments.UploadsFolder,
        name => docs.InstanceUploadTarget(TeamDocuments.UploadsFolder, name),
        (name, upload, replaceFile) => docs.SaveInstancePackageAsync(
            TeamDocuments.UploadsFolder, name, upload, replaceFile,
            TeamDocuments.MaximumUploadBytes, TeamDocuments.MaximumPackageBytes, ct));

    // A ZIP FROM A FORM, checked as every zip upload checks it before it is opened: a file named .zip,
    // not empty, not over the size, and one that reads as a zip. The archive is open while `upload` runs.
    static async Task<IResult> UploadZipFormAsync(
        HttpRequest request, Func<IFormCollection, Func<PackageUpload>, Task<IResult>> upload, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "Send a .zip file." });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");

        if (file is null || file.Length == 0 || !file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "Send a .zip file." });
        }

        if (file.Length > TeamDocuments.MaximumUploadBytes)
        {
            return Results.BadRequest(new
            {
                error = $"That zip is larger than {TeamDocuments.MaximumUploadBytes / (1024 * 1024)} MB.",
            });
        }

        await using var content = file.OpenReadStream();
        System.IO.Compression.ZipArchive archive;

        try
        {
            archive = new System.IO.Compression.ZipArchive(content, System.IO.Compression.ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest(new { error = "That file is not a zip that can be read." });
        }

        using (archive)
        {
            return await upload(form, () => TeamDocuments.ZipUpload(file.FileName, archive));
        }
    }

    // THE CLASH CHOICES for a package, wherever it goes. `skipped` is the answer to a skip; `saved` does
    // what follows a write (announcements, rows) and answers it.
    static async Task<IResult> UploadPackageIntoAsync(
        IFormCollection form, Func<PackageUpload> read, PackagePlace place,
        Func<string, object> skipped, Func<PackageSaved, Task<IResult>> saved)
    {
        var onClash = OnClashOf(form);

        if (onClash is not (null or "ask" or TeamDocuments.KeepBoth or TeamDocuments.Replace or TeamDocuments.Skip))
        {
            return Results.BadRequest(new { error = "onClash is ask, keep-both, replace or skip." });
        }

        // READ AND CHECKED WHOLE before the clash is even looked at: a refused package writes nothing.
        var upload = read();
        var name = upload.Name;
        var replaceFile = false;

        if (onClash is not null && place.Target(name) is var target && (target.IsFile || target.IsFolder))
        {
            var where = Path.GetDirectoryName(target.Path)?.Replace('\\', '/') is { Length: > 0 } parent ? parent : place.Where;

            switch (onClash)
            {
                case "ask":
                    return Results.Json(
                        new
                        {
                            error = $"{target.Name} is already in {where}. Choose Keep both, Replace or Skip.",
                            clashes = new[] { new DocumentsClash(target.Name, target.Path, target.IsFolder) },
                        },
                        statusCode: StatusCodes.Status409Conflict);
                case TeamDocuments.Skip:
                    return Results.Ok(skipped(target.Path));
                case TeamDocuments.Replace when target.IsFolder:
                    return Results.Conflict(new { error = $"{target.Name} is a folder in {where}; an upload replaces only a file." });
                case TeamDocuments.Replace:
                    replaceFile = true;
                    break;
                case TeamDocuments.KeepBoth:
                    var directory = Path.GetDirectoryName(Path.Combine(place.Root, target.Path))!;
                    name = TeamDocuments.FreeName(target.Name, isFolder: true, free => Path.Exists(Path.Combine(directory, free)));
                    break;
            }
        }

        return await saved(await place.Save(name, upload, replaceFile));
    }
}
