using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHO IS ACTING ON A SITE: a person (every team), or a member or plugin bound to its own team.
/// <paramref name="BoundTeam"/> is the container binding - set, the actor reaches that team's sites
/// only, and another team's site answers exactly what a missing one does.
/// </summary>
/// <param name="Id">A principal id, recorded as the tenant row's actor.</param>
/// <param name="Email">A person's email, when the actor is a person.</param>
/// <param name="Label">What the site's rows record as who did it: the email, or the member's id.</param>
public sealed record SiteActor(string Id, string? Email, string Label, string? BoundTeam)
{
    public static SiteActor Person(string userId, string email) => new(userId, email, email, null);

    /// <summary>A team's member - an agent or a plugin - bound to its own team.</summary>
    public static SiteActor Member(ContainerId member) =>
        new(member.ToString(), null, member.ToString(), member.Team);

    /// <summary>The Concierge, or a person's key: reaches every team, like a person.</summary>
    public static SiteActor Unbound(string id, string? email) => new(id, email, email ?? id, null);

    internal TriggerAudit Audit(string action, string team, string site, object? detail) =>
        new(Id, Email, action, $"{team}/{site}", site, detail is null ? null : JsonSerializer.Serialize(detail));
}

/// <summary>What a site operation came to: its value, or a sentence and the status to answer it with.</summary>
public sealed record SiteResult<T>(T? Value, string? Refusal = null, int Status = 200)
{
    public bool Ok => Refusal is null;

    public static implicit operator SiteResult<T>(T value) => new(value);
}

/// <summary>Everything a person or an agent may read about one site.</summary>
public sealed record SiteView(
    SiteRow Site,
    IReadOnlyList<SiteVersionRow> Versions,
    SiteDataUsage Data,
    IReadOnlyList<string> Collections,
    string Url);

/// <summary>
/// A TEAM'S SITES: static files the Host serves and backs with a data store and actions. The one
/// service behind the browser routes (<see cref="SiteEndpoints"/>), a plugin's <c>site.put</c> and
/// <c>site.delete</c> records, the team deletion, and - next - the <c>site</c> MCP tool and the
/// person's admin routes. Nothing in a site runs on the Host.
///
/// <para>
/// <b>Files.</b> Publishing copies a folder the team may write (its documents folder or its team
/// root: a worktree, a workspace) into <c>&lt;dataRoot&gt;/sites/&lt;team&gt;/&lt;site&gt;/v&lt;n&gt;</c>,
/// by way of a hidden partial folder, and only then records the version and makes it live, in one
/// transaction with its tenant row. The live version is what the database says, so an agent still
/// editing its working copy, or a copy half done, never changes what a person sees. Symbolic links
/// and dot-files are not copied. The last <see cref="SiteRules.KeptVersions"/> versions are kept.
/// </para>
/// </summary>
/// <param name="teamName">A team's stored spelling, or null when there is no such team -
/// <see cref="TeamRegistry.ExistingName"/> in the Host.</param>
/// <param name="copied">Called once a publish's copy is complete and before it is made live. For
/// tests, which use it to look at the live site in between.</param>
public sealed class SiteService(
    ISiteStore store,
    Func<string, string?> teamName,
    TeamPaths paths,
    IMessageLog log,
    Func<string, int, CancellationToken, Task>? copied = null)
{
    public const string NoSuchSite = "No such site.";

    public const string NotPublished = "This site is not published.";

    /// <summary>Where every team's sites live: <c>&lt;dataRoot&gt;/sites</c>.</summary>
    public string Root => Path.Combine(paths.DataRoot, "sites");

    /// <summary>The folder one version of a site is served from.</summary>
    public string VersionFolder(string team, string site, int version) =>
        Path.Combine(Root, team, site, $"v{version}");

    /// <summary>The path a person opens a site at. Absolute from the app's origin.</summary>
    public static string EntryPath(string team, string site) =>
        $"/sites/{Uri.EscapeDataString(team)}/{site}/";

    /// <summary>
    /// The site in <paramref name="team"/> named <paramref name="name"/>, with the team's stored
    /// spelling - or the refusal, which is the same sentence for a missing team, a missing site and
    /// a team the actor is not bound to.
    /// </summary>
    public async Task<SiteResult<SiteRow>> FindAsync(
        string team, string name, SiteActor? actor, CancellationToken ct = default)
    {
        if (teamName(team) is not { } stored
            || (actor?.BoundTeam is { } bound && !string.Equals(bound, stored, StringComparison.OrdinalIgnoreCase))
            || !SiteRules.IsSlug(name)
            || await store.FindAsync(stored, name, ct) is not { } site)
        {
            return new SiteResult<SiteRow>(null, NoSuchSite, 404);
        }

        return site;
    }

    public async Task<IReadOnlyList<SiteRow>> ListAsync(string? team, CancellationToken ct = default) =>
        team is null
            ? await store.ListAsync(null, ct)
            : teamName(team) is { } stored ? await store.ListAsync(stored, ct) : [];

    public async Task<SiteResult<SiteView>> ShowAsync(
        string team, string name, SiteActor? actor, CancellationToken ct = default)
    {
        var found = await FindAsync(team, name, actor, ct);
        if (found.Value is not { } site) return new(null, found.Refusal, found.Status);

        return new SiteView(
            site,
            await store.VersionsAsync(site.Team, site.Name, ct),
            await store.UsageAsync(site.Team, site.Name, ct),
            await store.CollectionsAsync(site.Team, site.Name, ct),
            EntryPath(site.Team, site.Name));
    }

    public async Task<SiteResult<SiteRow>> CreateAsync(
        string team, string name, SiteActor actor, CancellationToken ct = default)
    {
        if (teamName(team) is not { } stored
            || (actor.BoundTeam is { } bound && !string.Equals(bound, stored, StringComparison.OrdinalIgnoreCase)))
        {
            return new(null, TeamGateMessage, 404);
        }

        if (!SiteRules.IsSlug(name)) return new(null, SiteRules.NotASlug("site", name), 400);

        var row = new SiteRow(stored, name, null, 1, DateTimeOffset.UtcNow, actor.Label);

        if (!await store.CreateAsync(row, actor.Audit(TenantActions.SiteCreated, stored, name, null), ct))
        {
            return new(null, $"The team already has a site named \"{name}\".", 409);
        }

        return row;
    }

    private const string TeamGateMessage = "No such team.";

    /// <summary>
    /// Copies <paramref name="folder"/> into a new version and makes it live once the copy is
    /// complete. A publish that fails leaves the live version, and the folders, as they were.
    /// </summary>
    public async Task<SiteResult<SiteVersionRow>> PublishAsync(
        string team, string name, string folder, SiteActor actor, CancellationToken ct = default)
    {
        var found = await FindAsync(team, name, actor, ct);
        if (found.Value is not { } site) return new(null, found.Refusal, found.Status);

        if (SourceRefusal(site.Team, folder) is { } refusal) return new(null, refusal, 400);

        var source = PluginCatalog.Resolved(folder);
        var gate = Gates.GetOrAdd($"{site.Team.ToLowerInvariant()}/{site.Name}", _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct);

        try
        {
            // Re-read under the gate: the number is the site row's, taken by the publish before.
            site = (await store.FindAsync(site.Team, site.Name, ct))!;
            var version = site.NextVersion;
            var siteFolder = Path.Combine(Root, site.Team, site.Name);
            var partial = Path.Combine(siteFolder, $".v{version}.partial");
            var target = VersionFolder(site.Team, site.Name, version);

            RemoveFolder(partial);
            RemoveFolder(target);
            Directory.CreateDirectory(partial);

            try
            {
                var copy = Copy(source, partial);

                if (copy.Refusal is not null)
                {
                    RemoveFolder(partial);
                    return new(null, copy.Refusal, 400);
                }

                Directory.Move(partial, target);

                if (copied is not null) await copied(site.Name, version, ct);

                var existing = await store.VersionsAsync(site.Team, site.Name, ct);

                // The newest kept, this one among them and the live one always.
                var pruned = existing
                    .Select(v => v.Version)
                    .OrderByDescending(v => v)
                    .Skip(SiteRules.KeptVersions - 1)
                    .Where(v => v != site.LiveVersion)
                    .ToList();

                var row = new SiteVersionRow(version, DateTimeOffset.UtcNow, actor.Label, source, copy.Files, copy.Bytes);

                await store.RecordPublishAsync(
                    site.Team, site.Name, row, pruned,
                    actor.Audit(TenantActions.SitePublished, site.Team, site.Name, new
                    {
                        version, source, files = copy.Files, bytes = copy.Bytes, previous = site.LiveVersion,
                    }),
                    ct);

                foreach (var old in pruned) RemoveFolder(VersionFolder(site.Team, site.Name, old));

                return row;
            }
            catch
            {
                RemoveFolder(partial);
                RemoveFolder(target);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Makes <paramref name="version"/> live, or, when null, the newest kept version older than the
    /// live one.
    /// </summary>
    public async Task<SiteResult<SiteRow>> RollbackAsync(
        string team, string name, int? version, SiteActor actor, CancellationToken ct = default)
    {
        var found = await FindAsync(team, name, actor, ct);
        if (found.Value is not { } site) return found;

        var versions = await store.VersionsAsync(site.Team, site.Name, ct);

        var chosen = version is { } asked
            ? versions.FirstOrDefault(v => v.Version == asked)
            : versions.Where(v => site.LiveVersion is null || v.Version < site.LiveVersion).MaxBy(v => v.Version);

        if (chosen is null)
        {
            return new(null, version is { } missing
                ? $"The site \"{site.Name}\" keeps no version {missing}. It keeps: "
                    + (versions.Count == 0 ? "none" : string.Join(", ", versions.Select(v => v.Version))) + "."
                : $"The site \"{site.Name}\" keeps no version before the live one to roll back to.", 409);
        }

        if (!Directory.Exists(VersionFolder(site.Team, site.Name, chosen.Version)))
        {
            return new(null, $"Version {chosen.Version} of \"{site.Name}\" is recorded but its files are gone. Publish again.", 409);
        }

        await store.SetLiveAsync(site.Team, site.Name, chosen.Version,
            actor.Audit(TenantActions.SiteRolledBack, site.Team, site.Name, new { version = chosen.Version, previous = site.LiveVersion }), ct);

        return site with { LiveVersion = chosen.Version };
    }

    /// <summary>Stops serving the site. Its versions and its data are kept.</summary>
    public async Task<SiteResult<SiteRow>> UnpublishAsync(
        string team, string name, SiteActor actor, CancellationToken ct = default)
    {
        var found = await FindAsync(team, name, actor, ct);
        if (found.Value is not { } site) return found;

        await store.SetLiveAsync(site.Team, site.Name, null,
            actor.Audit(TenantActions.SiteUnpublished, site.Team, site.Name, new { previous = site.LiveVersion }), ct);

        return site with { LiveVersion = null };
    }

    /// <summary>
    /// Deletes the site, every version and all its data. ASKS FIRST: without
    /// <paramref name="confirmed"/> it answers 409 with what would be lost, and changes nothing.
    /// </summary>
    public async Task<SiteResult<SiteRow>> DeleteAsync(
        string team, string name, bool confirmed, SiteActor actor, CancellationToken ct = default)
    {
        var found = await FindAsync(team, name, actor, ct);
        if (found.Value is not { } site) return found;

        var usage = await store.UsageAsync(site.Team, site.Name, ct);

        if (!confirmed)
        {
            return new(null,
                $"Deleting the site \"{site.Name}\" removes its files and its {usage.Documents} document(s) "
                + $"({usage.Bytes} bytes) for good. Confirm to delete it, or unpublish it to keep them.", 409);
        }

        if (!await store.DeleteAsync(site.Team, site.Name,
                actor.Audit(TenantActions.SiteDeleted, site.Team, site.Name, new { documents = usage.Documents, bytes = usage.Bytes }), ct))
        {
            return new(null, NoSuchSite, 404);
        }

        RemoveFolder(Path.Combine(Root, site.Team, site.Name));
        return site;
    }

    /// <summary>A team deletion's step: every site of the team with its data and files, one tenant
    /// row per site. Returns the names removed.</summary>
    public async Task<IReadOnlyList<string>> DeleteTeamAsync(string team, CancellationToken ct = default)
    {
        var names = await store.DeleteTeamAsync(team,
            name => new TriggerAudit(null, null, TenantActions.SiteDeleted, $"{team}/{name}", name,
                JsonSerializer.Serialize(new { reason = "team deleted" })), ct);

        RemoveFolder(Path.Combine(Root, team));
        return names;
    }

    // ---- data ----

    public async Task<SiteResult<IReadOnlyList<SiteDocument>>> ListDocumentsAsync(
        string team, string site, string collection, SiteActor? actor, CancellationToken ct = default)
    {
        var found = await FindAsync(team, site, actor, ct);
        if (found.Value is not { } row) return new(null, found.Refusal, found.Status);
        if (!SiteRules.IsSlug(collection)) return new(null, SiteRules.NotASlug("collection", collection), 400);

        return new SiteResult<IReadOnlyList<SiteDocument>>(await store.ListDocumentsAsync(row.Team, row.Name, collection, ct));
    }

    public async Task<SiteResult<SiteDocument>> GetDocumentAsync(
        string team, string site, string collection, string id, SiteActor? actor, CancellationToken ct = default)
    {
        var found = await FindAsync(team, site, actor, ct);
        if (found.Value is not { } row) return new(null, found.Refusal, found.Status);
        if (Refusal(collection, id) is { } refusal) return new(null, refusal, 400);

        return await store.GetDocumentAsync(row.Team, row.Name, collection, id, ct) is { } document
            ? document
            : new SiteResult<SiteDocument>(null, $"No document \"{id}\" in \"{collection}\".", 404);
    }

    /// <summary>Writes <paramref name="json"/> as the document, which must be JSON; limits are
    /// refused with a sentence.</summary>
    public async Task<SiteResult<SiteDocument>> PutDocumentAsync(
        string team, string site, string collection, string id, string json, SiteActor actor,
        CancellationToken ct = default)
    {
        var found = await FindAsync(team, site, actor, ct);
        if (found.Value is not { } row) return new(null, found.Refusal, found.Status);
        if (Refusal(collection, id) is { } refusal) return new(null, refusal, 400);

        // Measured before parsing, so an oversized body is refused for its size, not its shape.
        if (Encoding.UTF8.GetByteCount(json) is var size && size > SiteRules.MaxDocumentBytes)
        {
            return new(null, SiteRules.DocumentTooLarge(size), 413);
        }

        if (!IsJson(json)) return new(null, "A document must be JSON.", 400);

        var at = DateTimeOffset.UtcNow;
        var written = await store.PutDocumentAsync(row.Team, row.Name, collection, id, json, actor.Label, at, ct);

        return written.Accepted
            ? new SiteDocument(collection, id, json, at, actor.Label)
            : new SiteResult<SiteDocument>(null, written.Refusal, written.Status);
    }

    public async Task<SiteResult<bool>> DeleteDocumentAsync(
        string team, string site, string collection, string id, SiteActor actor, CancellationToken ct = default)
    {
        var found = await FindAsync(team, site, actor, ct);
        if (found.Value is not { } row) return new(false, found.Refusal, found.Status);
        if (Refusal(collection, id) is { } refusal) return new(false, refusal, 400);

        return await store.DeleteDocumentAsync(row.Team, row.Name, collection, id, ct);
    }

    // ---- actions ----

    /// <summary>
    /// A person's click, carried to the team: ONE <c>site.action</c> row on the team's log, source
    /// <c>site:&lt;team&gt;/&lt;site&gt;</c>, no causation - so it roots its own workflow, as a
    /// trigger's fire does - and the event triggers narrowed to it fire. It widens nothing the team
    /// can do: it is an event, and what follows is the ordinary trigger path.
    /// </summary>
    public async Task<SiteResult<Message>> PostActionAsync(
        string team, string site, string action, string payload, string byEmail, CancellationToken ct = default)
    {
        var found = await FindAsync(team, site, null, ct);
        if (found.Value is not { } row) return new(null, found.Refusal, found.Status);
        if (row.LiveVersion is null) return new(null, NotPublished, 404);

        if (!SiteRules.IsSlug(action)) return new(null, SiteRules.NotASlug("action", action), 400);

        var bytes = Encoding.UTF8.GetByteCount(payload);

        if (bytes > SiteRules.MaxActionPayloadBytes)
        {
            return new(null,
                $"This action's payload is {bytes} bytes, over the limit of {SiteRules.MaxActionPayloadBytes} "
                + "bytes (16 KB). Send an id and keep the rest in the site's data.", 413);
        }

        JsonNode? sent;

        try
        {
            sent = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return new(null, "An action's payload must be JSON.", 400);
        }

        return await log.AppendAsync(ActionMessage(row.Team, row.Name, action, sent, byEmail, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>
    /// The one <c>site.action</c> row: <c>{team, site, action, siteAction, payload, by, at}</c>, source
    /// <c>site:&lt;team&gt;/&lt;site&gt;</c>, and NO causation, so the row roots its own workflow.
    /// </summary>
    public static NewMessage ActionMessage(
        string team, string site, string action, JsonNode? payload, string byEmail, DateTimeOffset at)
    {
        var body = new JsonObject
        {
            [PayloadFields.Team] = team,
            [PayloadFields.Site] = site,
            [PayloadFields.Action] = action,
            [PayloadFields.SiteAction] = $"{site}/{action}",
            [PayloadFields.Payload] = payload,
            [PayloadFields.By] = byEmail,
            [PayloadFields.At] = at.ToString("O"),
        };

        return new NewMessage(MessageTypes.SiteAction, body.ToJsonString(), SourceOf(team, site));
    }

    /// <summary>The source every <c>site.action</c> row of a site carries.</summary>
    public static string SourceOf(string team, string site) => $"site:{team}/{site}";

    // ---- files ----

    /// <summary>
    /// The file a live site serves at <paramref name="relative"/>, or null. <c>""</c> and a path
    /// ending in <c>/</c> mean its <c>index.html</c>. Never outside the version folder and never a link.
    /// </summary>
    public string? LiveFile(SiteRow site, string? relative)
    {
        if (site.LiveVersion is not { } version) return null;

        var folder = Path.GetFullPath(VersionFolder(site.Team, site.Name, version));
        relative ??= "";

        if (relative.Length == 0 || relative.EndsWith('/')) relative += "index.html";
        if (relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or ".." || part.StartsWith('.')))
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(folder, relative));
        if (!candidate.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;

        var info = new FileInfo(candidate);
        return info.Exists && info.LinkTarget is null ? candidate : null;
    }

    /// <summary>Why <paramref name="folder"/> cannot be published by <paramref name="team"/>, or null.</summary>
    private string? SourceRefusal(string team, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return "Name the folder to publish by its absolute path.";
        }

        var resolved = PluginCatalog.Resolved(folder);

        if (!Directory.Exists(resolved)) return $"There is no folder at {folder}.";

        var allowed = new[] { paths.DocsFor(team), paths.RootFor(team) }
            .Select(PluginCatalog.Resolved)
            .Select(Path.TrimEndingDirectorySeparator);

        if (!allowed.Any(root => resolved == root || resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            return $"A site is published from a folder the team may write: its documents folder or its team "
                + $"folder (a worktree or a workspace). {folder} is neither.";
        }

        if (!File.Exists(Path.Combine(resolved, "index.html")))
        {
            return $"{folder} has no index.html, which is the page a site opens at.";
        }

        if (Directory.Exists(Path.Combine(resolved, SiteRules.ApiSegment)) || File.Exists(Path.Combine(resolved, SiteRules.ApiSegment)))
        {
            return $"A site may not have a top-level \"{SiteRules.ApiSegment}\": that path is its data and actions.";
        }

        return null;
    }

    private sealed record CopyResult(int Files, long Bytes, string? Refusal);

    /// <summary>Copies files and folders, never a link and never a dot-name, within the publish bounds.</summary>
    private static CopyResult Copy(string source, string target)
    {
        var files = 0;
        long bytes = 0;
        var pending = new Stack<(string From, string To)>();
        pending.Push((source, target));

        while (pending.Count > 0)
        {
            var (from, to) = pending.Pop();

            foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null || entry.Name.StartsWith('.')) continue;

                var destination = Path.Combine(to, entry.Name);

                if (entry is DirectoryInfo directory)
                {
                    Directory.CreateDirectory(destination);
                    pending.Push((directory.FullName, destination));
                    continue;
                }

                if (entry is not FileInfo file) continue;

                files++;
                bytes += file.Length;

                if (files > SiteRules.MaxPublishedFiles || bytes > SiteRules.MaxPublishedBytes)
                {
                    return new(files, bytes,
                        $"The folder holds more than {SiteRules.MaxPublishedFiles} files or "
                        + $"{SiteRules.MaxPublishedBytes / (1024 * 1024)} MB, more than a site may publish. "
                        + "Publish the folder that holds only the site's files.");
                }

                file.CopyTo(destination);
            }
        }

        return new(files, bytes, null);
    }

    private static string? Refusal(string collection, string id) =>
        !SiteRules.IsSlug(collection) ? SiteRules.NotASlug("collection", collection)
        : !SiteRules.IsDocumentId(id) ? SiteRules.NotAnId(id)
        : null;

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A folder the Host itself wrote under <see cref="Root"/>: its own files, so a plain
    /// recursive delete is enough (none of an agent's owner-only folders are ever copied here).</summary>
    private static void RemoveFolder(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>One publish at a time per site, so two cannot take the same version number.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
}
