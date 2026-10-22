namespace Harness.Host;

/// <summary>
/// A STATE-CHANGING REQUEST FROM ANOTHER ORIGIN IS REFUSED, before any handler runs. A page a
/// preview port serves (<see cref="PreviewForwarder"/>) is on the Host's own host name, so it is the
/// SAME SITE as the Host: <c>SameSite=Strict</c> does not hold the session cookie back from its
/// requests, and code a team wrote could otherwise post to the Host as the person.
///
/// <para>
/// Refused: every method but <c>GET</c>, <c>HEAD</c>, <c>OPTIONS</c> and <c>TRACE</c>, and every
/// upgrade (the hub's and the Concierge's WebSockets), whose <c>Origin</c> is present and is not the
/// Host's own. <c>Origin: null</c> is refused too: a redirect across origins sends it, and the
/// redirect started on a preview's page still carries the cookie. A request with no <c>Origin</c> -
/// the CLI, an agent's key, a worker - is unchanged.
/// </para>
///
/// <para>
/// THE HOST'S OWN ORIGIN is the host and port the request was sent to (its <c>Host</c>), whatever the
/// scheme: behind a tunnel that ends TLS the request may arrive as http while the page is https, and
/// another port on the same host is exactly what this refuses. A <c>Host</c> with no port accepts an
/// <c>Origin</c> on its scheme's default port.
/// </para>
///
/// <para>
/// One exemption: a site's capability routes (<c>/sites/{team}/{site}/_c/{capability}/...</c>). Their
/// page runs in an opaque origin and sends <c>Origin: null</c>, and what authorizes them is the
/// capability in the path - they never read the cookie (see <see cref="SiteEndpoints"/>).
/// </para>
/// </summary>
public static class OriginGate
{
    /// <summary>What a refused request is answered with.</summary>
    public const string Refusal =
        "Refused: this request came from a page on another origin (a preview port or another site). "
        + "This platform accepts changes only from its own pages.";

    /// <summary>After the worker key's gate, before routing reaches any handler.</summary>
    public static void Use(IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (Refuses(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = Refusal }, context.RequestAborted);
            return;
        }

        await next();
    });

    /// <summary>Whether <paramref name="request"/> changes state, carries an Origin, and that Origin is not the Host's own.</summary>
    public static bool Refuses(HttpRequest request)
    {
        if (!ChangesState(request)) return false;

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0) return false;
        if (IsCapabilityRoute(request.Path)) return false;

        return !IsOwn(origin, request.Host);
    }

    private static bool ChangesState(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
          || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        || request.Headers.Upgrade.Count > 0;

    /// <summary>Whether <paramref name="origin"/> names the host and port <paramref name="host"/> does.</summary>
    public static bool IsOwn(string origin, HostString host)
    {
        if (!host.HasValue
            || !Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || parsed.AbsolutePath != "/" || parsed.Query.Length > 0)
        {
            return false;
        }

        if (!parsed.IdnHost.Equals(host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
            && !parsed.Host.Trim('[', ']').Equals(host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return host.Port is { } port ? parsed.Port == port : parsed.IsDefaultPort;
    }

    private static bool IsCapabilityRoute(PathString path)
    {
        if (!path.StartsWithSegments(SiteEndpoints.Prefix, out var rest)) return false;

        // {team}/{site}/_c/{capability}/...
        var segments = rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return segments.Length >= 4 && segments[2] == SiteEndpoints.CapabilitySegment;
    }
}
