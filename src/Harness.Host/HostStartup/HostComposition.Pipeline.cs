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

internal sealed partial class HostComposition
{
    internal void Pipeline()
    {
        // FIRST IN THE PIPELINE, so it sees what every other middleware does.
        //
        // Without this a 500 reaches the console the Host was started from and nowhere else, and is
        // unanalysable afterwards. An exception thrown in authentication,
        // in TeamGate or in a static-file handler is still a 500 nobody can explain, so a seam placed after
        // them would be blind to exactly the failures that are hardest to reason about.
        //
        // IT RECORDS AND RE-THROWS. It does not answer the request, does not set a status and does not
        // swallow: everything downstream of an unhandled exception behaves exactly as it would without it,
        // and the only addition is a row. See DiagnosticsMiddleware for what it will and will not
        // record, and why that set is closed.
        DiagnosticsMiddleware.Use(app);

        // BEFORE THE HEADERS: a request on a published preview port is the app's, not the Host's, and is
        // forwarded with every cookie the Host issues removed, its answer passed through with nothing of
        // the Host's added. Nothing below sees it. See PreviewForwarder.
        if (previewPorts.Count > 0)
        {
            var forwarder = new PreviewForwarder(previewPorts, InstanceIdentity.CookieNameFor(dataRoot));
            app.Lifetime.ApplicationStopped.Register(forwarder.Dispose);
            forwarder.Use(app);
        }

        // SECOND, so every response carries them - the bundle, an API answer, a gate's refusal, a 404.
        SecurityHeaders.Use(app);

        // THIRD, before the console's pages and files are served: the worker key is refused everywhere but
        // the worker connection, with a sentence, before anything else reads it.
        workerKeys.Use(app);

        // AND BEFORE ANY HANDLER: a change or a WebSocket whose Origin is another origin - a preview
        // port's page is the same SITE as the Host, so its requests carry the cookie - is refused.
        OriginGate.Use(app);

        app.UseDefaultFiles();

        // no-cache, not no-store: ETag/Last-Modified stay in play, so an unchanged file still answers a
        // cheap 304 instead of a full re-download on every navigation. Without this header the default
        // static-file middleware stamps only those two and leaves freshness to the browser's heuristics,
        // which lets a browser serve a stale index.html with no round-trip at all - a new build would go on
        // looking like the previous one until a hard refresh. Applies
        // to everything under wwwroot EXCEPT /assets: nothing else there carries an explicit cache policy
        // today, and the same staleness risk exists for any other hand-edited runtime file this instance
        // might carry.
        //
        // /assets is carved out because it is Vite's content-addressed output - the filename itself
        // changes when the bytes do, so the file at a given URL never changes and no-cache's conditional
        // GET can only ever come back 304. That round trip is free on loopback but not on the ngrok tunnel
        // this is served over, where every asset on every reload pays 50-200ms for a revalidation that was
        // always going to say "unchanged". immutable is honest here in a way it would not be for
        // index.html, which DOES change without a new filename.
        // THE SPA FALLBACK, AND IT IS A REWRITE RATHER THAN AN ENDPOINT. The alternatives are instructive
        // enough to keep.
        //
        // WHY IT IS NEEDED: `UseStaticFiles` serves files that EXIST and `UseDefaultFiles` rewrites
        // only `/`, so without this a browser asking the server for `/console`, `/login` or `/board` would
        // match no endpoint at all - and a request matching no endpoint is exactly what the authorization
        // FALLBACK policy refuses, with **401 and an empty body**. It bites only on a deep link, a refresh
        // or a pasted URL, because ordinary use loads `/` once and the client router never asks the server
        // again. `routes.ts` redirects `/board` to `/console` so a bookmark lands, which needs this too.
        //
        // WHY NOT `MapFallbackToFile`: a catch-all endpoint matches every path AND EVERY VERB, and both
        // halves break something the suite guards.
        //   - Every path: `/api/kanban/templates` must answer 404, and
        //     `KanbanTeamScopeTests.The_ungated_routes_are_gone` would see 200 with HTML.
        //   - Every verb: with an endpoint under `/api`, a POST to a GET-only path would stop being
        //     **405 Method Not Allowed** and become 404, because routing reports a method mismatch only
        //     when no endpoint claims the request. `TeamAccessViewTests` guards that.
        // A `regex(^(?!api/|hub/).*$)` route constraint serves nothing at all. So the rule belongs BEFORE
        // routing, where it costs the machine surface nothing.
        //
        // THE RULE: a GET, for an extensionless path, outside `/api`, `/hub` and `/sites` (a site is served by its own routes), is a client-side route.
        // That is exact here because nothing on this server renders HTML - every page is the one bundle.
        //   - GET only, so a mistyped POST still gets a refusal rather than a page to parse.
        //   - Extensionless, so a missing `/assets/...` chunk still 404s as itself instead of silently
        //     answering HTML, which is the failure mode that makes a bad deploy look like a router bug.
        //   - BEFORE `UseStaticFiles`, so the rewritten request is served by it and short-circuits there -
        //     the authorization fallback policy is never reached, which is what the 401 would be.
        //
        // The bundle is not the secret, the API is - the same argument `UseAuthentication`'s placement
        // comment below already makes. The client router still guards `/console` and sends a signed-out
        // visitor to the landing page.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            if (HttpMethods.IsGet(context.Request.Method)
                && path.HasValue
                && !Path.HasExtension(path.Value)
                && !path.StartsWithSegments("/api")
                && !path.StartsWithSegments("/hub")
                && !path.StartsWithSegments(SiteEndpoints.Prefix)
                && !path.StartsWithSegments(HealthEndpoints.Route))
            {
                context.Request.Path = "/index.html";
            }

            await next();
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
                context.Context.Response.Headers.CacheControl =
                    context.Context.Request.Path.StartsWithSegments("/assets")
                        ? "public,max-age=31536000,immutable"
                        : "no-cache",
        });

        // BEFORE UseAuthentication, because the cookie's SecurePolicy is SameAsRequest and that reads
        // Request.IsHttps. A tunnel terminates TLS at the proxy and forwards plain HTTP to this host, so
        // without this the request looks insecure, the login cookie ships with NO Secure attribute, and the
        // one deployment the SecurePolicy comment reasons about is the one it fails on. Pinning Always
        // instead would fix the tunnel and drop the cookie on plain-http loopback, which is the ordinary
        // local case - so both pieces are needed, and removing either re-opens one of the two.
        //
        // PROTO ONLY, and only from THIS MACHINE. X-Forwarded-For and -Host are deliberately not consumed:
        // nothing here authorises on client IP or host, so accepting them would widen what a forged header
        // controls for no gain. The known-proxy list is left at its default of ::1 PLUS an explicit
        // 127.0.0.1 - the default covers only the IPv6 loopback, and a tunnel agent (ngrok, cloudflared)
        // runs on this machine and dials Kestrel's IPv4 loopback, so without the addition the headers are
        // silently ignored and this middleware does nothing at all. Clearing the lists - the usual
        // make-it-work advice - would let anyone who can reach the port claim any scheme, so it is not done.
        //
        // AND FROM THIS MACHINE'S OWN ADDRESSES, not loopback alone. In the container, pasta delivers the
        // tunnel's connection from the container's own eth0 address, so loopback-only would log every tunnel
        // request as http://. See `LocalProxies`.
        var forwarded = LocalProxies.Options(LocalProxies.OwnAddresses());

        app.UseForwardedHeaders(forwarded);

        // AFTER the static-file middleware, not before: ASP.NET's authorization fallback policy applies to
        // requests that match NO endpoint at all, which is exactly what a static file request is. Putting
        // this first would mean the SPA bundle itself needs a key under the fallback policy - and there
        // would be no page left on which to log in. The bundle is not the secret; the API is.
        app.UseAuthentication();
        app.UseAuthorization();

        // EVERY ROUTE THAT BINDS A JSON BODY, in one place: a wrong type or malformed JSON is answered 400
        // with a sentence naming the field and the type expected, never an empty body.
        JsonBodyErrors.Use(app);

        // AFTER UseAuthorization, which is what populates context.User for a key-authenticated request.
        PrincipalLogScope.Use(app);

        // A call under a Concierge session's own credential is that session's activity while it runs,
        // /mcp included. AFTER UseAuthorization for the same reason.
        ConciergeCallClock.Use(app);
    }
}
