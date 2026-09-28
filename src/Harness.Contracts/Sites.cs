using System.Text.RegularExpressions;

namespace Harness.Contracts;

/// <summary>
/// WHAT A SITE'S NAMES MAY BE, AND HOW MUCH IT MAY HOLD. One place, so the page, an agent and a
/// plugin are refused with the same sentence for the same thing.
/// </summary>
public static partial class SiteRules
{
    /// <summary>A document's JSON text, in UTF-8 bytes.</summary>
    public const int MaxDocumentBytes = 64 * 1024;

    /// <summary>Documents in one collection.</summary>
    public const int MaxDocumentsPerCollection = 10_000;

    /// <summary>Every document of a site together, in UTF-8 bytes.</summary>
    public const long MaxSiteDataBytes = 50L * 1024 * 1024;

    /// <summary>An action's payload, as JSON text in UTF-8 bytes.</summary>
    public const int MaxActionPayloadBytes = 16 * 1024;

    /// <summary>The files one published version may hold, and their total size. Not in the card's
    /// numbers: a bound so a publish from the wrong folder (a whole worktree) is refused rather than
    /// copied.</summary>
    public const int MaxPublishedFiles = 2_000;

    public const long MaxPublishedBytes = 100L * 1024 * 1024;

    /// <summary>How many versions are kept on disk, the live one always among them.</summary>
    public const int KeptVersions = 5;

    /// <summary>The top-level name the capability's API lives under, refused as a site file.</summary>
    public const string ApiSegment = "_api";

    /// <summary>
    /// A site, collection or action name: lower-case letters, digits and single hyphens, 1-63
    /// characters, starting with a letter or digit. Folder-safe and URL-safe, so it needs no escaping
    /// in a path, a CSP source or a filter.
    /// </summary>
    public static bool IsSlug(string? name) => name is not null && SlugPattern().IsMatch(name);

    /// <summary>A document id: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, 1-128 characters,
    /// not starting with a dot.</summary>
    public static bool IsDocumentId(string? id) => id is not null && IdPattern().IsMatch(id);

    public static string NotASlug(string what, string? name) =>
        $"\"{name}\" is not a valid {what} name. Use 1-63 lower-case letters, digits and hyphens, "
        + "starting with a letter or digit.";

    public static string DocumentTooLarge(int bytes) =>
        $"This document is {bytes} bytes, over the limit of {MaxDocumentBytes} bytes (64 KB) for one "
        + "document. Split it into smaller documents.";

    public static string NotAnId(string? id) =>
        $"\"{id}\" is not a valid document id. Use 1-128 letters, digits, '.', '_' and '-', not starting with '.'.";

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z0-9_-][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}

/// <summary>A site: static files owned by one team, with a live version or none (unpublished).</summary>
/// <param name="LiveVersion">The version served, or null when the site is not published.</param>
/// <param name="NextVersion">The number the next publish takes. Never reused.</param>
public sealed record SiteRow(
    string Team,
    string Name,
    int? LiveVersion,
    int NextVersion,
    DateTimeOffset CreatedAt,
    string CreatedBy);

/// <summary>One published copy of a site's files, kept under <c>&lt;dataRoot&gt;/sites/&lt;team&gt;/&lt;site&gt;/v&lt;n&gt;</c>.</summary>
public sealed record SiteVersionRow(
    int Version,
    DateTimeOffset PublishedAt,
    string PublishedBy,
    string Source,
    int Files,
    long Bytes);

/// <summary>One document in a site's collection. <paramref name="Json"/> is its JSON text.</summary>
public sealed record SiteDocument(
    string Collection,
    string Id,
    string Json,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

/// <summary>How much data a site holds.</summary>
public sealed record SiteDataUsage(int Documents, long Bytes);

/// <summary>What a data write came to: done, or refused with a sentence and a status.</summary>
public sealed record SiteWriteOutcome(bool Accepted, string? Refusal = null, int Status = 200)
{
    public static SiteWriteOutcome Ok { get; } = new(true);

    public static SiteWriteOutcome Refused(string sentence, int status = 400) => new(false, sentence, status);
}

/// <summary>
/// SITES, THEIR VERSIONS AND THEIR DATA. Every change to a site or its versions appends its
/// <c>tenant_events</c> row IN THE SAME TRANSACTION and does not happen when that row cannot be
/// written. Data writes are recorded on the document itself (<c>updated_at</c>, <c>updated_by</c>),
/// not in the tenant log: a page may write on every click.
///
/// Team names are compared without case, as everywhere else.
/// </summary>
public interface ISiteStore
{
    Task<IReadOnlyList<SiteRow>> ListAsync(string? team, CancellationToken ct = default);

    Task<SiteRow?> FindAsync(string team, string name, CancellationToken ct = default);

    Task<IReadOnlyList<SiteVersionRow>> VersionsAsync(string team, string name, CancellationToken ct = default);

    /// <summary>False when a site of that name already exists in the team.</summary>
    Task<bool> CreateAsync(SiteRow row, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// Records <paramref name="version"/> and makes it live, takes the next number past it, and forgets
    /// <paramref name="pruned"/> - in one transaction with its tenant row.
    /// </summary>
    Task RecordPublishAsync(
        string team, string name, SiteVersionRow version, IReadOnlyList<int> pruned, TriggerAudit audit,
        CancellationToken ct = default);

    /// <summary>Makes <paramref name="version"/> live, or none when null (unpublish).</summary>
    Task SetLiveAsync(string team, string name, int? version, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>Deletes the site, its versions and its data. False when there was no such site.</summary>
    Task<bool> DeleteAsync(string team, string name, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>Deletes every site of a team with its data, one tenant row per site, in one
    /// transaction. Returns the names deleted.</summary>
    Task<IReadOnlyList<string>> DeleteTeamAsync(
        string team, Func<string, TriggerAudit> audit, CancellationToken ct = default);

    Task<IReadOnlyList<SiteDocument>> ListDocumentsAsync(
        string team, string site, string collection, CancellationToken ct = default);

    Task<IReadOnlyList<string>> CollectionsAsync(string team, string site, CancellationToken ct = default);

    Task<SiteDocument?> GetDocumentAsync(
        string team, string site, string collection, string id, CancellationToken ct = default);

    /// <summary>Writes a document, refusing it with a sentence past a limit, checked in the same
    /// transaction as the write.</summary>
    Task<SiteWriteOutcome> PutDocumentAsync(
        string team, string site, string collection, string id, string json, string by, DateTimeOffset at,
        CancellationToken ct = default);

    /// <summary>False when there was no such document.</summary>
    Task<bool> DeleteDocumentAsync(
        string team, string site, string collection, string id, CancellationToken ct = default);

    Task<SiteDataUsage> UsageAsync(string team, string site, CancellationToken ct = default);
}
