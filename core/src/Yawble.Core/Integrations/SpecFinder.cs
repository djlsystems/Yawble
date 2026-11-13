using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Yawble.Core.Integrations;

/// <summary>
/// Finds an API's OpenAPI spec and downloads it through an injected handler: the documentation URL itself, then spec links on that page,
/// then the usual places under the service URL. Reads at most 20 MB per download with a 15 s timeout. Sends no sign-in of any kind.
/// </summary>
public sealed partial class SpecFinder(HttpMessageHandler handler)
{
    public const int MaxBytes = 20 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    public const string NotFound = "no OpenAPI spec found at the documentation URL or the usual places; paste the link to the spec itself (often ends in openapi.json)";
    private static readonly string[] UsualPaths = ["/openapi.json", "/openapi.yaml", "/swagger.json", "/v3/api-docs", "/.well-known/openapi.json"];

    [GeneratedRegex("""(?:href|src)\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    private sealed record Fetched(string Text, bool Html, bool SpecLike, Uri FinalUrl);

    public async Task<(string Text, Uri Url)> FindAsync(Uri? docsUrl, Uri serviceUrl, CancellationToken ct)
    {
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var tried = new HashSet<string>(StringComparer.Ordinal);
        string? problem = null; // the first reason a candidate was unusable (too large, hostile), shown if nothing else works

        async Task<(string, Uri)?> Try(Uri url, bool allowLinks)
        {
            if (!IsHttp(url) || !tried.Add(url.AbsoluteUri)) return null;
            Fetched? got;
            try { got = await FetchAsync(http, url, ct); }
            catch (ArgumentException e) { problem ??= e.Message; return null; }
            if (got is null) return null;
            try
            {
                if (OpenApiReader.LooksLikeSpec(got.Text)) return (got.Text, got.FinalUrl);
            }
            catch (ArgumentException e)
            {
                if (got.SpecLike) problem ??= e.Message; // only a response that claimed to be a spec explains itself
                return null;
            }
            if (allowLinks && got.Html)
                foreach (var link in SpecLinks(got.Text, got.FinalUrl).Take(5))
                    if (await Try(link, false) is { } hit) return hit;
            return null;
        }

        if (docsUrl is not null && await Try(docsUrl, true) is { } fromDocs) return fromDocs;
        // scheme, host, port and path only: a query or fragment on the service URL must not end up in the middle of the candidate
        var root = new UriBuilder(serviceUrl) { Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        foreach (var path in UsualPaths)
            if (Uri.TryCreate(root + path, UriKind.Absolute, out var u) && await Try(u, false) is { } found) return found;
        throw new ArgumentException(problem ?? NotFound);
    }

    private static bool IsHttp(Uri u) => u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps;

    /// <summary>Links on an HTML page to a .json, .yaml or .yml file whose path mentions openapi or swagger, in page order, without repeats.</summary>
    internal static IEnumerable<Uri> SpecLinks(string html, Uri page)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in LinkPattern().Matches(html))
        {
            var href = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
            if (!Uri.TryCreate(page, href, out var u) || !IsHttp(u)) continue;
            var path = u.AbsolutePath;
            var ext = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
            if (!ext || !(path.Contains("openapi", StringComparison.OrdinalIgnoreCase) || path.Contains("swagger", StringComparison.OrdinalIgnoreCase))) continue;
            if (seen.Add(u.AbsoluteUri)) yield return u;
        }
    }

    /// <summary>One GET; null for a non-success status or a network failure. Throws ArgumentException when the body is over the cap.</summary>
    private static async Task<Fetched?> FetchAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json, application/yaml, text/yaml, text/html;q=0.8, */*;q=0.5");
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Yawble", "1"));
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var finalUrl = resp.RequestMessage?.RequestUri ?? url;
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            var specLike = IsSpecLike(type, finalUrl) || IsSpecLike(type, url);
            if (resp.Content.Headers.ContentLength > MaxBytes) return specLike ? throw TooLarge() : null;
            await using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + n > MaxBytes) return specLike ? throw TooLarge() : null;
                buffer.Write(chunk, 0, n);
            }
            var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var html = type.Contains("html", StringComparison.OrdinalIgnoreCase) || text.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal);
            return new Fetched(text, html, specLike, finalUrl);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // the 15 s timeout
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; } // a connection dropped while the body was read
    }

    /// <summary>A response that says it is JSON or YAML, or whose address ends that way (not an HTML docs page).</summary>
    private static bool IsSpecLike(string mediaType, Uri url) =>
        mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase)
        || url.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || url.AbsolutePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    private static ArgumentException TooLarge() => new("the spec is larger than 20 MB, which is more than the app reads; paste the link to a smaller spec");
}
