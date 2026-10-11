namespace Harness.Host;

/// <summary>
/// WHICH HOST A MEMBER'S CLI CALLS BACK ON - the value handed to every container as
/// <c>HARNESS_URL</c>.
///
/// IT IS DERIVED FROM THE ADDRESS THIS HOST IS SERVING, NEVER A LITERAL PORT. A literal told a
/// member of a host on another port to call a host that had never minted its key, and the failure
/// is SILENT in both directions: the run's exit code is not the tool call's, so the card stays green
/// while the feed stays empty. So it is derived, and what was chosen is PRINTED.
///
/// The <c>MemberBaseAddress</c> setting still WINS when it is set, because a host behind a reverse proxy or on
/// a container network is reachable at an address it cannot see from its own binding.
/// </summary>
public static class MemberBaseAddress
{
    /// <summary>The last resort, when nothing else answers.</summary>
    public const string Fallback = "http://127.0.0.1:8090";

    /// <summary>
    /// Resolves what a member should be told, from the explicit setting and the host's own URLs.
    /// </summary>
    /// <param name="configured">The `MemberBaseAddress` setting, or null when nobody has set it.</param>
    /// <param name="urls">
    /// The host's own binding - `ASPNETCORE_URLS` or the `urls` key - which may carry several
    /// addresses separated by semicolons.
    /// </param>
    public static string Resolve(string? configured, string? urls)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        // THE FIRST binding, because a host that serves both http and https is reachable on either
        // and the CLI needs one answer. Ordering is the operator's, expressed in their own URL list.
        var first = (urls ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (first is null || !Uri.TryCreate(Loopback(first), UriKind.Absolute, out var parsed))
        {
            return Fallback;
        }

        // Trailing slash removed: this is concatenated with route paths that begin with one, and
        // `//api/...` is a different path to every router that has ever had to parse it.
        return parsed.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// A WILDCARD BINDING IS NOT AN ADDRESS ANYTHING CAN DIAL. `http://*:8113` and its siblings mean
    /// "every interface"; a member has to be handed something it can actually connect to, and the
    /// member is on this machine by construction - it is a child process of this host.
    /// </summary>
    private static string Loopback(string url) =>
        url.Replace("://*:", "://127.0.0.1:", StringComparison.Ordinal)
            .Replace("://+:", "://127.0.0.1:", StringComparison.Ordinal)
            .Replace("://0.0.0.0:", "://127.0.0.1:", StringComparison.Ordinal)
            .Replace("://[::]:", "://127.0.0.1:", StringComparison.Ordinal);
}
