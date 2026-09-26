namespace Harness.Host;

/// <summary>
/// The headers every response carries, whatever produced it: the SPA bundle, an API answer, a
/// refusal from a gate, /healthz. Stamped in OnStarting rather than before <c>next</c>, so a
/// middleware that resets the response on the way out cannot leave a page without them.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Self only, directive by directive, so nothing this page loads can come from anywhere else.
    ///
    /// <c>style-src-elem 'unsafe-inline'</c> IS THE ONE LOOSENING, and it was measured in headless
    /// Chromium rather than assumed. With <c>style-src 'self'</c> alone the landing page, the
    /// sign-in page and every console dialog log nothing: Quasar and Vue's
    /// <c>:style</c> bindings position things through the CSSOM (<c>el.style.x = ...</c>), which CSP
    /// does not govern, and Vite ships every component's CSS as a file. Opening the Concierge
    /// panel logs "Applying inline style violates ... style-src 'self'" as
    /// <c>style-src-elem</c>: xterm injects <c>&lt;style&gt;</c> elements for its cell metrics, with
    /// contents that change with the font and size, so neither a hash nor a nonce can name them.
    /// The terminal renders unstyled without this.
    ///
    /// Only ELEMENTS are loosened. <c>style-src-attr</c> falls back to <c>style-src 'self'</c>, so
    /// markup carrying <c>style="..."</c> - what an injected string would look like - is still
    /// refused; nothing in the app was seen to need it. A browser too old for
    /// <c>style-src-elem</c> (pre-2022) falls back to <c>style-src 'self'</c> and gets an unstyled
    /// terminal, which fails safe. Styles cannot run code either way: <c>script-src</c> is
    /// <c>'self'</c> with no inline and no eval.
    ///
    /// <c>connect-src 'self'</c> covers the SignalR and terminal WebSockets: CSP Level 3 matches
    /// ws:/wss: on the page's own host for <c>'self'</c>, and the probe saw the hub connect.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; "
        + "script-src 'self'; "
        + "style-src 'self'; "
        + "style-src-elem 'self' 'unsafe-inline'; "
        + "img-src 'self'; "
        + "font-src 'self'; "
        + "connect-src 'self'; "
        + "object-src 'none'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'";

    /// <summary>
    /// The one way a route replaces the app's CSP: it puts a policy under this key in
    /// <c>HttpContext.Items</c>, and the stamp below uses it instead. Only <c>documents/view</c>
    /// does, and only with a STRICTER policy (<see cref="DocumentView"/>). Setting the header
    /// itself would not work: OnStarting callbacks run last-registered first, so this stamp,
    /// registered before any route, runs after the route's and would overwrite it.
    /// </summary>
    public const string PolicyOverride = "Harness.SecurityHeaders.ContentSecurityPolicy";

    public static void Use(IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "same-origin";

            // Both: frame-ancestors is the standard, X-Frame-Options is what an older browser
            // reads. Nothing here is meant to be framed.
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy =
                context.Items[PolicyOverride] as string ?? ContentSecurityPolicy;
            return Task.CompletedTask;
        });

        await next();
    });
}
