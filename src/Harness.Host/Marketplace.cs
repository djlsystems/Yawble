using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// Where the package catalog is read from, and where a package's zip is downloaded from. The Host is
/// told the catalog's address (<see cref="Marketplace.CatalogSetting"/>) by whoever starts it, so no
/// repository is named in the code; tests replace this interface and never reach the network.
/// </summary>
public interface IMarketplaceFeed
{
    /// <summary>The catalog's address, or null when none was configured.</summary>
    Uri? Address { get; }

    /// <summary>The catalog's text, as published.</summary>
    Task<string> ReadCatalogAsync(CancellationToken ct);

    /// <summary>The bytes at <paramref name="url"/>, refused with <see cref="MarketplaceRefusal"/>
    /// past <paramref name="maxBytes"/>.</summary>
    Task<byte[]> DownloadAsync(Uri url, long maxBytes, CancellationToken ct);
}

/// <summary>The catalog and its zips over HTTPS, read anonymously: the release is public.</summary>
public sealed class HttpMarketplaceFeed(HttpClient http, string? address) : IMarketplaceFeed
{
    /// <summary>The largest catalog read, in bytes: far above any real one.</summary>
    public const long MaxCatalogBytes = 4 * 1024 * 1024;

    /// <summary>How long one catalog read may take; a download has the client's own, longer, timeout.</summary>
    public static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(30);

    public Uri? Address { get; } =
        Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" ? parsed : null;

    public async Task<string> ReadCatalogAsync(CancellationToken ct)
    {
        if (Address is null) throw new InvalidOperationException("no catalog address was configured");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CatalogTimeout);
        var bytes = await DownloadAsync(Address, MaxCatalogBytes, timeout.Token);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public async Task<byte[]> DownloadAsync(Uri url, long maxBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("package-catalog", BuildVersion.Current.Version.Split('+')[0]));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > maxBytes) throw MarketplaceRefusal.TooLarge(maxBytes);

        await using var content = await response.Content.ReadAsStreamAsync(ct);
        using var kept = new MemoryStream();
        var buffer = new byte[81920];
        int read;

        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            if (kept.Length + read > maxBytes) throw MarketplaceRefusal.TooLarge(maxBytes);
            kept.Write(buffer, 0, read);
        }

        return kept.ToArray();
    }
}

/// <summary>A fetch refused, with the sentence a person reads and the status it answers.</summary>
public sealed class MarketplaceRefusal(string sentence, int status = StatusCodes.Status400BadRequest) : Exception(sentence)
{
    public int Status { get; } = status;

    public static MarketplaceRefusal TooLarge(long maxBytes) =>
        new($"The package is larger than {maxBytes / (1024 * 1024)} MB, so it was not fetched.");
}

/// <summary>One package as the catalog lists it (schema 1), with what it needs already in words, and
/// the catalog's own needs fields beside them.</summary>
public sealed record CatalogPackage(
    string Id, string Kind, string Name, string Summary, string Version, IReadOnlyList<string> Needs,
    Uri DownloadUrl, string Sha256, long Bytes, CatalogNeeds CatalogNeeds);

/// <summary>
/// A package's <c>needs</c> as the catalog writes them, without the <c>why</c> texts (those are in the
/// sentences): the console words a card's short lines from these, so a provider id is never shown to a
/// person as it is.
/// </summary>
public sealed record CatalogNeeds(
    IReadOnlyList<CatalogConnection> Connections,
    IReadOnlyList<CatalogSecret> Secrets,
    IReadOnlyList<CatalogInput> Inputs,
    IReadOnlyList<string> Runtimes)
{
    public static readonly CatalogNeeds None = new([], [], [], []);
}

public sealed record CatalogConnection(string Slot, IReadOnlyList<string> Providers, bool Required);

/// <summary><paramref name="When"/> is the catalog's plain words, "when the sources setting includes
/// adzuna", or null for a secret always needed.</summary>
public sealed record CatalogSecret(string Key, string? When);

/// <summary><paramref name="Kind"/> is <c>documents</c> (the name is a folder) or <c>setting</c>.</summary>
public sealed record CatalogInput(string Name, string Kind, bool Required);

/// <summary>One package of <c>GET /api/marketplace</c>.</summary>
public sealed record MarketplacePackage(
    string Id, string Kind, string Name, string Summary, string Version, IReadOnlyList<string> Needs,
    bool Installed, string? InstalledVersion, IReadOnlyList<string> InstalledOn, bool UpdateAvailable,
    CatalogNeeds CatalogNeeds);

/// <summary>What <c>GET /api/marketplace</c> and its refresh answer. <paramref name="Checked"/> false
/// is "not known", never an empty catalog, and <paramref name="Reason"/> says why.</summary>
public sealed record MarketplaceStatus(
    bool Checked, string? Reason, DateTimeOffset? CheckedAt, IReadOnlyList<MarketplacePackage> Packages);

/// <summary>What a fetch answers: where the package now is, relative to the instance's documents.</summary>
public sealed record MarketplaceFetched(string Id, string Version, string Kind, string Folder);

/// <summary>
/// THE PACKAGE CATALOG, read about twice a day and kept, so Solutions can list what may be added and
/// whether this instance has it; and the FETCH that brings one package's zip into the instance's
/// documents for the install wizard. It never installs: a person reviews and installs as from any
/// other folder.
///
/// - Nothing is guessed: until a read has answered, after one that failed, with no address, or with
///   <c>marketplace.check</c> off, the answer is not checked with the reason - never an empty catalog.
/// - A catalog of a schema this build does not know is refused, not misread.
/// - A zip is downloaded only from the catalog's own origin (the same https scheme, host and port as
///   its address) under <c>/api/marketplace/download/</c>, refused over <see cref="MaxDownloadBytes"/>, and checked against
///   the catalog's sha256 and size before anything is written; it is unpacked by the checks Upload a
///   package (.zip) uses, and only after its <c>marketplace.fetched</c> row is written.
/// </summary>
public sealed partial class Marketplace(
    IMarketplaceFeed feed,
    Func<bool> enabled,
    Func<DateTimeOffset> now,
    Func<IEnumerable<(string Team, TeamSolution Solution)>> solutions,
    Func<string, string?> pluginVersion,
    ILogger<Marketplace> log,
    Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? providerNames = null)
{
    /// <summary>The configuration key (or <c>HARNESS_MARKETPLACE_CATALOG</c>) holding the catalog's
    /// address. The operator CLI sets it on the control container.</summary>
    public const string CatalogSetting = "Marketplace:Catalog";
    public const string CatalogVariable = "HARNESS_MARKETPLACE_CATALOG";

    /// <summary>The catalog format this build reads.</summary>
    public const int Schema = 1;

    /// <summary>How often the background loop reads the catalog.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    /// <summary>The largest zip fetched.</summary>
    public const long MaxDownloadBytes = 100 * 1024 * 1024;

    /// <summary>The most a fetched zip unpacks to, in all and per file: a package's binaries are
    /// larger than a document, and this still bounds a zip that inflates without end.</summary>
    public const long MaxUnpackedBytes = 400 * 1024 * 1024;

    /// <summary>The folder of the instance's documents fetched packages go in.</summary>
    public const string Folder = "Marketplace";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Read? _last;

    private sealed record Read(DateTimeOffset At, IReadOnlyList<CatalogPackage>? Packages, string? Failure);

    /// <summary>The path on the catalog's origin a package's zip is downloaded from.</summary>
    public const string DownloadPath = "/api/marketplace/download/";

    /// <summary>Where a package's zip may be downloaded from: the catalog's own https origin (its
    /// scheme, host and port) followed by <see cref="DownloadPath"/>, or null when the catalog has no
    /// address or is not read over https.</summary>
    public string? DownloadPrefix =>
        feed.Address is { Scheme: "https", UserInfo.Length: 0 } address
            ? address.GetLeftPart(UriPartial.Authority) + DownloadPath
            : null;

    /// <summary>The last answer, never a fresh read.</summary>
    public MarketplaceStatus Status()
    {
        if (!enabled()) return NotChecked($"Reading the package catalog is turned off in {TenantSettings.SystemTab}.", null);
        if (feed.Address is null) return NotChecked("Not checked: this instance was not told where the package catalog is published.", null);

        var last = _last;
        if (last is null) return NotChecked("Not checked yet.", null);
        if (last.Packages is null) return NotChecked($"Not checked: {last.Failure}", last.At);

        var installed = solutions().ToList();
        return new MarketplaceStatus(true, null, last.At, [.. last.Packages.Select(p => WithInstalled(p, installed))]);
    }

    /// <summary>Reads the catalog now and keeps the answer. A failure is kept as the reason, never thrown.</summary>
    public async Task<MarketplaceStatus> CheckAsync(CancellationToken ct)
    {
        if (!enabled() || feed.Address is null) return Status();

        await _gate.WaitAsync(ct);
        try
        {
            try
            {
                var text = await feed.ReadCatalogAsync(ct);
                var names = providerNames is null ? null : await providerNames(ct);
                _last = new Read(now(), Parse(text, names is null ? null : id => names.GetValueOrDefault(id)), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogInformation("Package catalog could not be read from {Address}: {Reason}", feed.Address, ex.Message);
                _last = new Read(now(), null, ex switch
                {
                    TaskCanceledException => "the catalog did not answer in time.",
                    MarketplaceRefusal or CatalogRefusal => ex.Message,
                    _ => Sentence(ex.Message),
                });
            }
        }
        finally
        {
            _gate.Release();
        }

        return Status();
    }

    /// <summary>
    /// Downloads, checks and unpacks one package into <c>Marketplace/&lt;id&gt;-&lt;version&gt;</c> of
    /// the instance's documents. Refused with <see cref="MarketplaceRefusal"/>; nothing is written
    /// before every check has passed and the tenant row is written.
    /// </summary>
    public async Task<MarketplaceFetched> FetchAsync(
        string id, TeamDocuments documents, ITenantLog tenantLog, ClaimsPrincipal person, CancellationToken ct)
    {
        if (Status() is not { Checked: true })
        {
            throw new MarketplaceRefusal("The package catalog has not been read, so nothing can be fetched from it. Refresh it first.",
                StatusCodes.Status409Conflict);
        }

        var package = _last?.Packages?.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
            ?? throw new MarketplaceRefusal($"The package catalog lists no package '{id}'.", StatusCodes.Status404NotFound);

        var url = package.DownloadUrl;
        if (!IsUnderPrefix(url))
        {
            throw new MarketplaceRefusal(
                $"{url} is not a download from the catalog's own address, so it was not fetched.");
        }

        if (package.Bytes > MaxDownloadBytes) throw MarketplaceRefusal.TooLarge(MaxDownloadBytes);

        var name = $"{package.Id}-{package.Version}";
        var folder = $"{Folder}/{name}";
        var target = Path.Combine(Path.GetFullPath(documents.InstanceRoot), Folder, name);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new MarketplaceRefusal(
                $"{folder} is already in the documents: install from that folder, or delete it to fetch it again.",
                StatusCodes.Status409Conflict);
        }

        byte[] zip;
        try
        {
            zip = await feed.DownloadAsync(url, MaxDownloadBytes, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MarketplaceRefusal($"The package could not be downloaded: {Sentence(ex.Message)}", StatusCodes.Status502BadGateway);
        }

        // CHECKED BEFORE ANYTHING IS WRITTEN: the catalog's word on size and content.
        if (zip.LongLength > MaxDownloadBytes) throw MarketplaceRefusal.TooLarge(MaxDownloadBytes);
        if (zip.LongLength != package.Bytes)
        {
            throw new MarketplaceRefusal(
                $"The download is {zip.LongLength} bytes and the catalog says {package.Bytes}, so nothing was written.");
        }

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(zip));
        if (!string.Equals(sha256, package.Sha256, StringComparison.Ordinal))
        {
            throw new MarketplaceRefusal("The download does not match the catalog's sha256, so nothing was written.");
        }

        using var archive = OpenZip(zip);

        // THE SAME CHECKS AS UPLOAD A PACKAGE (.zip): a link, an absolute path or a `..` refuses the
        // whole zip here, before the row and before any write.
        PackageUpload upload;
        try
        {
            upload = TeamDocuments.ZipUpload(name + ".zip", archive);
        }
        catch (DocumentPathException ex)
        {
            throw new MarketplaceRefusal(ex.Message);
        }

        // RECORDED BEFORE UNPACKED, and not swallowed: a fetch nobody can account for does not happen.
        try
        {
            await tenantLog.WriteAsync(
                person.FindFirstValue(ClaimTypes.NameIdentifier),
                person.FindFirstValue(ClaimTypes.Email),
                TenantActions.MarketplaceFetched,
                package.Id,
                package.Name,
                JsonSerializer.Serialize(new { id = package.Id, version = package.Version, kind = package.Kind, sha256, folder }),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "The fetch of {Package} {Version} could not be recorded; nothing was unpacked", package.Id, package.Version);
            throw new MarketplaceRefusal("The fetch could not be recorded, so nothing was unpacked.", StatusCodes.Status500InternalServerError);
        }

        try
        {
            await documents.SaveInstancePackageAsync(Folder, name, upload, MaxUnpackedBytes, MaxUnpackedBytes, ct);
        }
        catch (Exception ex) when (ex is DocumentPathException or InvalidOperationException)
        {
            throw new MarketplaceRefusal(ex.Message);
        }

        return new MarketplaceFetched(package.Id, package.Version, package.Kind, folder);
    }

    private static ZipArchive OpenZip(byte[] zip)
    {
        try
        {
            return new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new MarketplaceRefusal("The download is not a zip that can be read, so nothing was written.");
        }
    }

    /// <summary>Whether <paramref name="url"/> is a plain https address under <see cref="DownloadPrefix"/>:
    /// the catalog's scheme, host and port, and no user, query, fragment, escape or dot segment that a
    /// comparison of text could be fooled by.</summary>
    public bool IsUnderPrefix(Uri url)
    {
        if (DownloadPrefix is not { } prefix || feed.Address is not { } catalog) return false;

        var raw = url.OriginalString;
        return url.IsAbsoluteUri
            && url.Scheme == Uri.UriSchemeHttps
            && url.Scheme == catalog.Scheme
            && string.Equals(url.Host, catalog.Host, StringComparison.OrdinalIgnoreCase)
            && url.Port == catalog.Port
            && url.UserInfo.Length == 0
            && url.Query.Length == 0
            && url.Fragment.Length == 0
            && !raw.Contains('%') && !raw.Contains('\\')
            && !url.AbsolutePath.Split('/').Any(segment => segment is "." or "..")
            && string.Equals(raw, url.AbsoluteUri, StringComparison.Ordinal)
            && raw.StartsWith(prefix, StringComparison.Ordinal)
            && raw.Length > prefix.Length;
    }

    private MarketplacePackage WithInstalled(CatalogPackage package, IReadOnlyList<(string Team, TeamSolution Solution)> installed)
    {
        string? version;
        IReadOnlyList<string> on = [];

        if (package.Kind == "solution")
        {
            var teams = installed.Where(i => string.Equals(i.Solution.Id, package.Id, StringComparison.Ordinal)).ToList();
            on = [.. teams.Select(t => t.Team).Order(StringComparer.Ordinal)];

            // THE OLDEST COPY speaks for the instance: one team behind is an update to offer.
            version = teams.Select(t => t.Solution.Version).Order(Comparer<string>.Create(ReleaseCheck.Compare)).FirstOrDefault();
        }
        else
        {
            version = pluginVersion(package.Id);
        }

        return new MarketplacePackage(
            package.Id, package.Kind, package.Name, package.Summary, package.Version, package.Needs,
            version is not null, version, on,
            version is not null && ReleaseCheck.Compare(package.Version, version) > 0,
            package.CatalogNeeds);
    }

    private static MarketplaceStatus NotChecked(string reason, DateTimeOffset? at) => new(false, reason, at, []);

    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.EndsWith('.') ? trimmed : trimmed + ".";
    }

    /// <summary>A catalog this build will not read, with the sentence why.</summary>
    private sealed class CatalogRefusal(string sentence) : Exception(sentence);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex PlainName();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    /// <summary>Reads a schema 1 catalog, refusing whole what it cannot read faithfully.</summary>
    public static IReadOnlyList<CatalogPackage> Parse(string text, Func<string, string?>? named = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new CatalogRefusal("the catalog is not JSON that can be read.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.Number)
            {
                throw new CatalogRefusal("the catalog does not say which schema it is.");
            }

            if (!schema.TryGetInt32(out var number) || number != Schema)
            {
                throw new CatalogRefusal(
                    $"the catalog is schema {schema.GetRawText()}, which this build does not read (it reads schema {Schema}).");
            }

            if (!root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            {
                throw new CatalogRefusal("the catalog lists no packages array.");
            }

            var read = new List<CatalogPackage>();
            foreach (var entry in packages.EnumerateArray()) read.Add(Package(entry, read.Count, named));
            return read;
        }
    }

    private static CatalogPackage Package(JsonElement entry, int index, Func<string, string?>? named)
    {
        string Text(JsonElement from, string field, string where) =>
            from.ValueKind == JsonValueKind.Object && from.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : throw new CatalogRefusal($"{where} has no {field}.");

        var at = $"package {index + 1}";
        var id = Text(entry, "id", at);
        at = $"package '{id}'";
        if (!PlainName().IsMatch(id) || id.Contains("..", StringComparison.Ordinal)) throw new CatalogRefusal($"{at} has an id that is not a plain name.");

        var kind = Text(entry, "kind", at);
        if (kind is not ("solution" or "plugin")) throw new CatalogRefusal($"{at} is of kind '{kind}', which this build does not know.");

        var version = Text(entry, "version", at);
        if (!PlainName().IsMatch(version) || version.Contains("..", StringComparison.Ordinal)) throw new CatalogRefusal($"{at} has a version that is not a plain name.");

        if (!entry.TryGetProperty("download", out var download) || download.ValueKind != JsonValueKind.Object)
        {
            throw new CatalogRefusal($"{at} has no download.");
        }

        if (!Uri.TryCreate(Text(download, "url", at), UriKind.Absolute, out var url)) throw new CatalogRefusal($"{at} has a download url that is not an address.");

        var sha256 = Text(download, "sha256", at);
        if (!Sha256Hex().IsMatch(sha256)) throw new CatalogRefusal($"{at} has a sha256 that is not 64 lowercase hex digits.");

        if (!download.TryGetProperty("bytes", out var bytesValue) || !bytesValue.TryGetInt64(out var bytes) || bytes <= 0)
        {
            throw new CatalogRefusal($"{at} has no size.");
        }

        return new CatalogPackage(
            id, kind, Text(entry, "name", at), Text(entry, "summary", at), version,
            Needs(entry, named), url, sha256, bytes, Fields(entry));
    }

    /// <summary>The catalog's needs fields as written, each list empty when the catalog has none.</summary>
    public static CatalogNeeds Fields(JsonElement entry)
    {
        if (!entry.TryGetProperty("needs", out var needs) || needs.ValueKind != JsonValueKind.Object) return CatalogNeeds.None;

        IEnumerable<JsonElement> Each(string field) =>
            needs.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
                : [];

        static string? Field(JsonElement from, string field) =>
            from.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        static bool Required(JsonElement from) =>
            from.TryGetProperty("required", out var value) && value.ValueKind == JsonValueKind.True;

        static IReadOnlyList<string> Strings(JsonElement from, string field) =>
            from.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!)]
                : [];

        return new CatalogNeeds(
            [.. Each("connections").Select(c => new CatalogConnection(Field(c, "slot") ?? "", Strings(c, "providers"), Required(c)))],
            [.. Each("secrets").Select(s => new CatalogSecret(Field(s, "key") ?? "", string.IsNullOrWhiteSpace(Field(s, "when")) ? null : Field(s, "when")))],
            [.. Each("inputs").Select(i => new CatalogInput(Field(i, "name") ?? "", Field(i, "kind") ?? "", Required(i)))],
            Strings(needs, "runtimes"));
    }

    /// <summary>What a package needs, each in a sentence a person reads as text. Providers are said in
    /// words (<see cref="ConnectionProviders.Spoken"/>); <paramref name="named"/> is the name the Host
    /// knows for a <c>custom-&lt;id&gt;</c> provider, or null.</summary>
    public static IReadOnlyList<string> Needs(JsonElement entry, Func<string, string?>? named = null)
    {
        var sentences = new List<string>();
        if (!entry.TryGetProperty("needs", out var needs) || needs.ValueKind != JsonValueKind.Object) return sentences;

        static IEnumerable<JsonElement> Each(JsonElement needs, string field) =>
            needs.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                : [];

        static string? Field(JsonElement from, string field) =>
            from.ValueKind == JsonValueKind.Object && from.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        static bool Required(JsonElement from) =>
            from.ValueKind == JsonValueKind.Object && from.TryGetProperty("required", out var value) && value.ValueKind == JsonValueKind.True;

        static string With(string lead, string? why) =>
            string.IsNullOrWhiteSpace(why) ? lead + "." : $"{lead}: {Sentence(why)}";

        static string Or(IReadOnlyList<string> items) =>
            items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1];

        foreach (var connection in Each(needs, "connections"))
        {
            // A SLOT NAMED "account" is not said twice: "an account connected", never "a account account".
            var slot = Field(connection, "slot");
            var account = string.IsNullOrWhiteSpace(slot) ? "account"
                : slot.EndsWith("account", StringComparison.OrdinalIgnoreCase) ? slot
                : $"{slot} account";
            var article = "aeiouAEIOU".Contains(account[0]) ? "an" : "a";
            var providers = connection.TryGetProperty("providers", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToList()
                : [];
            var through = providers.Count > 0 ? $" ({Or([.. providers.Select(p => ConnectionProviders.Spoken(p, named))])})" : "";
            sentences.Add(With($"{(Required(connection) ? "Needs" : "Can use")} {article} {account} connected{through}", Field(connection, "why")));
        }

        foreach (var secret in Each(needs, "secrets"))
        {
            var when = Field(secret, "when");
            sentences.Add(With(
                $"Needs the secret {Field(secret, "key") ?? "(unnamed)"} set on the instance{(string.IsNullOrWhiteSpace(when) ? "" : ", " + when)}",
                Field(secret, "why")));
        }

        foreach (var input in Each(needs, "inputs"))
        {
            var what = Field(input, "kind") == "documents" ? $"documents in {Field(input, "name")}" : $"the setting {Field(input, "name")}";
            sentences.Add(With($"{(Required(input) ? "Needs" : "Can take")} {what} at install", Field(input, "why")));
        }

        foreach (var runtime in Each(needs, "runtimes").Where(r => r.ValueKind == JsonValueKind.String))
        {
            sentences.Add($"Needs {runtime.GetString()} on the instance.");
        }

        return sentences;
    }
}

/// <summary>Reads the catalog shortly after start, then every <see cref="Marketplace.Interval"/>.</summary>
public sealed class MarketplaceLoop(Marketplace marketplace) : BackgroundService
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await marketplace.CheckAsync(stoppingToken);
                await Task.Delay(Marketplace.Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

public static class MarketplaceEndpoints
{
    public const string Route = "/api/marketplace";

    public static void Map(WebApplication app)
    {
        const string Shape =
            "`{ checked, reason, checkedAt, packages }`: `checked` false means not known - never an empty "
            + "catalog - and `reason` says why in a sentence (before a read answered, after one failed, with "
            + "no catalog address, with the tenant setting marketplace.check off, or a schema this build does "
            + "not read). Each package is `{ id, kind (solution|plugin), name, summary, version, needs "
            + "(sentences, as text), catalogNeeds (the catalog's needs fields without their `why`: "
            + "`{ connections: [{ slot, providers, required }], secrets: [{ key, when }], inputs: [{ name, kind, "
            + "required }], runtimes }`), installed, installedVersion, installedOn (the teams a solution is "
            + "installed on), updateAvailable }`; a solution matches by package id on each team, a plugin "
            + "by id and its active version, and `updateAvailable` is true when the catalog's version is newer.";

        app.MapGet(Route, (Marketplace marketplace) => Results.Ok(marketplace.Status()))
            .HumansOnly()
            .WithTags("Solutions")
            .WithSummary("The package catalog, and what this instance has of it")
            .WithDescription("The last answer of the catalog read, read about every 12 hours. " + Shape);

        app.MapPost(Route + "/refresh", async (Marketplace marketplace, CancellationToken ct) =>
            Results.Ok(await marketplace.CheckAsync(ct)))
            .HumansOnly()
            .WithTags("Solutions")
            .WithSummary("Read the package catalog now")
            .WithDescription("Reads the catalog now and answers as GET does. A failed read is kept as the "
                + "reason, never thrown; with checking off it reads nothing. " + Shape);

        app.MapPost(Route + "/{id}/fetch", async (
            string id, Marketplace marketplace, TeamDocuments documents, ITenantLog tenantLog,
            HttpContext context, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await marketplace.FetchAsync(id, documents, tenantLog, context.User, ct));
            }
            catch (MarketplaceRefusal refusal)
            {
                return Results.Json(new { error = refusal.Message }, statusCode: refusal.Status);
            }
        })
            .HumansOnly()
            .WithTags("Solutions")
            .WithSummary("Fetch one package of the catalog into the instance's documents")
            .WithDescription(
                "Downloads the package's zip only from the catalog's own origin (a plain https address on "
                + "the same scheme, host and port the catalog was read from, under "
                + $"`{Marketplace.DownloadPath}`, with no query, escape or dot segment), refuses one over "
                + $"{Marketplace.MaxDownloadBytes / (1024 * 1024)} MB, checks its sha256 and size against "
                + "the catalog before anything is written, appends a `marketplace.fetched` tenant row, and "
                + "only then unpacks it with the checks Upload a package (.zip) uses (no link, no absolute "
                + $"path, no `..`) into `{Marketplace.Folder}/<id>-<version>` of the instance's documents. "
                + "It never installs. 200 `{ id, version, kind, folder }`, `folder` relative to the "
                + "instance's documents, for the install wizard (a solution) or the plugin install dialog "
                + "(a plugin). Refusals answer `{ error }` with a sentence: 404 for a package the catalog "
                + "does not list, 409 before the catalog was read or when that folder is already there, "
                + "400 for a download from anywhere else, too large, of the wrong size or sha256, or a zip "
                + "the checks refuse, 502 when the download fails, 500 when the row cannot be written - "
                + "nothing is written in each case.");
    }
}
