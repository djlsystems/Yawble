using System.Net;
using System.Net.Sockets;

namespace Harness.Host;

/// <summary>
/// WHERE A SIGN-IN BROWSER MAY GO, and what of it is kept. The entry's own hosts are always allowed.
/// Another host is reached only as a sign-in flow goes there: a top-level navigation from an allowed
/// page that is OAuth-, OpenID- or SAML-shaped, or any top-level navigation a provider's own page makes
/// (its sign-in steps hop between its hosts). Each host admitted that way is a provider, recorded in
/// the order it was first reached. Everything else is refused. A private or loopback address is never
/// a provider unless an entry host is one too (a local test site). Requests a page makes for its parts
/// - scripts, images, a CAPTCHA's frame - are not limited by host, only kept off private addresses;
/// what is SAVED is pruned to the entry's hosts and the providers (<see cref="KeepsCookie"/>,
/// <see cref="KeepsOrigin"/>).
/// </summary>
public sealed class SignInHosts
{
    private readonly HashSet<string> _entry;
    private readonly List<string> _providers = [];
    private readonly Lock _gate = new();
    private readonly bool _entryIsPrivate;

    public SignInHosts(IEnumerable<string> entryHosts)
    {
        _entry = new HashSet<string>(entryHosts.Select(Normalize).Where(h => h.Length > 0), StringComparer.Ordinal);
        if (_entry.Count == 0) throw new ArgumentException("A sign-in browser needs at least one entry host.", nameof(entryHosts));
        _entryIsPrivate = _entry.Any(IsPrivate);
    }

    /// <summary>What one navigation may do.</summary>
    public enum Verdict
    {
        /// <summary>Allowed: an entry host, a provider already admitted, or a part of a page.</summary>
        Allow,

        /// <summary>Allowed, and its host is now a provider.</summary>
        Admit,

        /// <summary>Not allowed.</summary>
        Refuse,
    }

    /// <summary>The entry's own hosts.</summary>
    public IReadOnlyCollection<string> Entry => _entry;

    /// <summary>The providers admitted so far, in the order they were first reached.</summary>
    public IReadOnlyList<string> Providers
    {
        get
        {
            lock (_gate) return [.. _providers];
        }
    }

    /// <summary>The entry's hosts, then the providers: what a save keeps.</summary>
    public IReadOnlyList<string> Kept => [.. _entry.Order(StringComparer.Ordinal), .. Providers];

    /// <summary>
    /// A top-level navigation to <paramref name="url"/>, made while the page showed
    /// <paramref name="fromHost"/> (null for a new page with nothing on it yet, which takes its
    /// opener's host). <paramref name="form"/> is the posted body, if any, as the page sent it.
    /// </summary>
    public Verdict TopLevel(Uri url, string? fromHost, string? form = null)
    {
        if (url.Scheme is not ("http" or "https")) return url.AbsoluteUri == "about:blank" ? Verdict.Allow : Verdict.Refuse;

        var host = Normalize(url.Host);
        if (_entry.Contains(host)) return Verdict.Allow;

        lock (_gate)
        {
            if (_providers.Contains(host)) return Verdict.Allow;
            if (IsPrivate(host) && !_entryIsPrivate) return Verdict.Refuse;

            var from = fromHost is null ? null : Normalize(fromHost);
            var fromProvider = from is not null && _providers.Contains(from);
            var fromEntry = from is not null && _entry.Contains(from);
            if (!fromProvider && !(fromEntry && SignInShaped(url, form))) return Verdict.Refuse;

            _providers.Add(host);
            return Verdict.Admit;
        }
    }

    /// <summary>A request for a part of a page, or a frame inside it: any host but a private address.</summary>
    public Verdict Part(Uri url)
    {
        if (url.Scheme is not ("http" or "https")) return Verdict.Allow;

        var host = Normalize(url.Host);
        if (_entry.Contains(host)) return Verdict.Allow;
        return IsPrivate(host) && !_entryIsPrivate ? Verdict.Refuse : Verdict.Allow;
    }

    /// <summary>Whether a cookie set for <paramref name="domain"/> is kept: it is sent to an entry host or a provider.</summary>
    public bool KeepsCookie(string domain)
    {
        var d = Normalize(domain.TrimStart('.'));
        if (d.Length == 0) return false;
        return Kept.Any(host => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    /// <summary>Whether local storage of <paramref name="origin"/> is kept: its host is an entry host or a provider.</summary>
    public bool KeepsOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && Kept.Contains(Normalize(uri.Host));

    /// <summary>A host as compared: lower case, with no trailing dot and no brackets.</summary>
    public static string Normalize(string host) => host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();

    /// <summary>
    /// Whether a request looks like a step of a sign-in flow: OAuth 2.0 or OpenID Connect
    /// (<c>client_id</c> with <c>redirect_uri</c> or <c>response_type</c>), SAML (<c>SAMLRequest</c>
    /// or <c>SAMLResponse</c>) or WS-Federation (<c>wa</c> with <c>wtrealm</c>), in the query or the form.
    /// </summary>
    public static bool SignInShaped(Uri url, string? form = null)
    {
        var names = Names(url.Query.TrimStart('?')).Concat(Names(form ?? "")).ToHashSet(StringComparer.Ordinal);
        return (names.Contains("client_id") && (names.Contains("redirect_uri") || names.Contains("response_type")))
            || names.Contains("SAMLRequest") || names.Contains("SAMLResponse")
            || (names.Contains("wa") && names.Contains("wtrealm"));
    }

    /// <summary>A loopback, private, link-local or unspecified address, or <c>localhost</c>.</summary>
    public static bool IsPrivate(string host)
    {
        host = Normalize(host);
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] == 0;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
    }

    private static IEnumerable<string> Names(string encoded) =>
        encoded.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => Uri.UnescapeDataString(pair.Split('=', 2)[0].Replace('+', ' ')));
}
