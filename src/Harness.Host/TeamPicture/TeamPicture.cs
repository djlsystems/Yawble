using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE TEAM PICTURE: an optional site per team, under the reserved name <see cref="SiteName"/>, whose
/// page is the platform's shipped template and whose live parts are the platform's own figures.
///
/// <list type="bullet">
/// <item><b>On</b> (<see cref="SetAsync"/>): the site is made when it has none, the shipped template
/// is published when the live version is not it, the row <c>teams.team_picture</c> is set with its
/// <c>team.picture-changed</c> tenant row, and the platform's collections are recomputed.</item>
/// <item><b>Off</b>: the row is cleared and the site unpublished. Its versions and ALL its data are
/// kept, and nothing is recomputed while it is off.</item>
/// <item><b>The template</b> is <c>templates/team-picture/</c> beside the Host. Which version is
/// live is its digest, recorded as the published version's source
/// (<c>platform:team-picture#&lt;digest&gt;</c>). Publishing replaces only the page's files and never
/// a collection, so a Concierge's <c>tp-settings</c> and a Manager's <c>tp-picture</c> survive an
/// upgrade and an off-and-on. At start, every team with the picture on whose live digest is not the
/// shipped one is published again (<see cref="TeamPictureKeeper"/>).</item>
/// <item><b>The live collections</b> (<see cref="TeamPictureRules.PlatformCollections"/>) are
/// recomputed by <see cref="TeamPictureFigures"/> from the Kanban, the usage ledger and the log, off
/// every run's path, when the team's rows move (<see cref="TeamPictureKeeper"/>).</item>
/// </list>
/// </summary>
public sealed class TeamPicture(
    SiteService sites,
    ISiteStore store,
    TeamRegistry teams,
    TeamPictureFigures figures,
    string templateFolder,
    ILogger<TeamPicture>? logger = null)
{
    /// <summary>The site name the platform reserves for every team's Team picture.</summary>
    public const string SiteName = "team-picture";

    /// <summary>Who the platform's own writes are recorded as.</summary>
    public const string PlatformLabel = "platform";

    /// <summary>The prefix of the source a published template version records.</summary>
    public const string SourcePrefix = "platform:team-picture#";

    public const string ReservedName =
        "\"team-picture\" is the Team picture's site name, which the platform keeps. Turn the Team picture on in Team settings instead.";

    public const string PlatformsPage =
        "The Team picture's page is the platform's: turn it on or off in Team settings.";

    public static bool IsReserved(string? name) => string.Equals(name, SiteName, StringComparison.Ordinal);

    /// <summary>The shipped template's folder.</summary>
    public string TemplateFolder => templateFolder;

    /// <summary>The source the shipped template publishes as, or null when no template is shipped.</summary>
    public string? ShippedSource() =>
        Digest(templateFolder) is { } digest ? SourcePrefix + digest : null;

    /// <summary>
    /// Turns the team's Team picture on or off. Answers null when done, or the sentence why not (the
    /// flag is then unchanged).
    /// </summary>
    public async Task<string?> SetAsync(
        string team, bool on, Func<string, TriggerAudit> audit, CancellationToken ct = default)
    {
        var stored = teams.ExistingName(team) ?? throw new InvalidOperationException($"No team '{team}'.");

        if (on)
        {
            // The page first: a flag that reads on with no page behind it is a broken link.
            if (await EnsurePublishedAsync(stored, force: false, ct) is { } refusal) return refusal;

            if (!teams.HasTeamPicture(stored)) await teams.SetTeamPictureAsync(stored, true, audit, ct);

            await figures.RecomputeAsync(stored, ct);
            return null;
        }

        if (teams.HasTeamPicture(stored)) await teams.SetTeamPictureAsync(stored, false, audit, ct);

        if (await store.FindAsync(stored, SiteName, ct) is { LiveVersion: not null })
        {
            var unpublished = await sites.UnpublishAsync(stored, SiteName, SiteActor.Platform(stored), ct);
            if (!unpublished.Ok) return unpublished.Refusal;
        }

        return null;
    }

    /// <summary>
    /// Makes the site when the team has none and publishes the shipped template when the live version
    /// is not it (or always, with <paramref name="force"/>). Answers null when the shipped template is
    /// live, or the sentence why it could not be published.
    /// </summary>
    public async Task<string?> EnsurePublishedAsync(string team, bool force, CancellationToken ct = default)
    {
        if (ShippedSource() is not { } source)
        {
            return "The Team picture's template is not shipped with this instance, so its page cannot be published.";
        }

        var site = await store.FindAsync(team, SiteName, ct);

        if (site is null)
        {
            var made = await sites.CreateAsync(team, SiteName, SiteActor.Platform(team), ct);
            if (!made.Ok) return made.Refusal;
            site = made.Value!;
        }

        if (!force && site.LiveVersion is { } live
            && (await store.VersionsAsync(team, SiteName, ct)).FirstOrDefault(v => v.Version == live) is { } version
            && string.Equals(version.Source, source, StringComparison.Ordinal))
        {
            return null;
        }

        var published = await sites.PublishTemplateAsync(team, SiteName, templateFolder, source, ct);
        if (!published.Ok) return published.Refusal;

        logger?.LogInformation("Published the Team picture's template {Source} for team {Team}.", source, team);
        return null;
    }

    /// <summary>
    /// At start: every team with the picture on gets the shipped template when its live version is not
    /// it, and its collections recomputed. One team's failure is logged and does not stop the rest.
    /// </summary>
    public async Task RepublishAllAsync(CancellationToken ct = default)
    {
        foreach (var team in teams.TeamsWithTeamPicture())
        {
            try
            {
                if (await EnsurePublishedAsync(team, force: false, ct) is { } refusal)
                {
                    logger?.LogWarning("The Team picture of team {Team} was not republished: {Reason}", team, refusal);
                }

                await figures.RecomputeAsync(team, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.LogWarning(exception, "The Team picture of team {Team} was not republished.", team);
            }
        }
    }

    /// <summary>
    /// The template's digest: SHA-256 over every file's relative path and bytes, sorted, never a link
    /// or a dot-name (which a publish does not copy either). First 16 hex characters. Null when there
    /// is no template or it has no index.html.
    /// </summary>
    public static string? Digest(string folder)
    {
        if (!File.Exists(Path.Combine(folder, "index.html"))) return null;

        var files = new List<(string Relative, string Full)>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));

        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null || entry.Name.StartsWith('.')) continue;
                if (entry is DirectoryInfo directory) pending.Push(directory);
                else if (entry is FileInfo file)
                {
                    files.Add((Path.GetRelativePath(folder, file.FullName).Replace('\\', '/'), file.FullName));
                }
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var (relative, full) in files.OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            hash.AppendData(File.ReadAllBytes(full));
            hash.AppendData("\0"u8);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>The tenant row of a change of the flag.</summary>
    public static object Detail(bool on) => new { teamPicture = on };

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
