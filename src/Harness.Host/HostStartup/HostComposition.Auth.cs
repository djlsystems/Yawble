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
    internal void Auth()
    {
        // The key ring that encrypts every session ticket, IN the data root and filed under this instance's
        // name. Without this the Host configures neither, and the isolation between two instances is
        // ACCIDENTAL: ASP.NET Core derives its discriminator from the CONTENT ROOT PATH, so two worktrees
        // happen to differ while two hosts sharing a content root - a copied tree, a published build, one
        // image run twice - cross-decrypt each other's tickets. The second host then answers 200 OK,
        // authenticated as a user id its database has never heard of, which presents as a permission bug
        // rather than as the wrong session.
        //
        // Pinning it here makes the key ring obey the rule everything else here obeys - one data root, one
        // database, one process - so isolation is a property of the code rather than of where the tree
        // happens to sit on disk. It is independent of the cookie name below and worth having alone.
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys")))
            .SetApplicationName(instance);

        // AT REST the key ring is plaintext under the data root. The container boundary and the file mode are the isolation, and docs/architecture/secrets.md is the
        // plan for narrowing who inside the container can read it.

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                // NAMED FOR THE INSTANCE, because a cookie is keyed by (host, path, name) and port is not
                // part of that key - RFC 6265 §8.5, by design, in every browser. A constant name would let
                // two instances on different ports share one slot in one jar, so signing in to either
                // would sign you out of the other.
                options.Cookie.Name = InstanceIdentity.CookieNameFor(dataRoot);
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;

                // Sent over the reverse-proxy tunnel as well as loopback, so let the request decide rather
                // than pinning Always (which would drop the cookie on plain-http localhost). SameAsRequest
                // reads Request.IsHttps, which is only the truth about the tunnel because UseForwardedHeaders
                // is wired below - see the comment there. The two are one mechanism; neither works alone.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;

                // An API must answer 401, not 302 to a login page that does not exist. Without these two
                // the SPA's fetch() follows the redirect and parses HTML as JSON.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };

                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.Scheme, _ => { });

        // The policy names its schemes explicitly, and every scheme it names must already be registered or
        // the policy throws when it is evaluated. The default authentication scheme is the cookie, so
        // UseAuthentication alone only populates context.User from the cookie - a key-authenticated request
        // gets its principal because this policy names that scheme too and the authorization middleware
        // authenticates it.
        //
        // The DEFAULT policy applies where authorization was ASKED for; the FALLBACK applies where nothing
        // was asked at all - which is every endpoint here. That is what covers the console WebSocket and the
        // SignalR hub by their being routes, rather than by anyone remembering them.
        policy = new AuthorizationPolicyBuilder(
                CookieAuthenticationDefaults.AuthenticationScheme,
                ApiKeyAuthenticationHandler.Scheme)
            .RequireAuthenticatedUser()
            .Build();

        builder.Services.AddAuthorizationBuilder()
            .SetDefaultPolicy(policy)
            .SetFallbackPolicy(policy);

        // Registered ABOVE every consumer. One place decides where a team's files are; composing any of
        // these paths a second time from dataRoot is two stores of one fact.
        teamPaths = new TeamPaths(dataRoot);
        builder.Services.AddSingleton(teamPaths);

        // The picker's reach. Effective() adds the instance root to whatever is configured - it is never
        // written into appsettings.json, because the data root is a runtime value.
        var configuredFileBrowser = builder.Configuration.GetSection(FileBrowserOptions.Section).Get<FileBrowserOptions>();

        // `Effective` labels the instance root from the resolved data root itself.
        var effectiveFileBrowserRoots = FileBrowserOptions.Effective(configuredFileBrowser, dataRoot);

        fileBrowser = new FileBrowserPolicy(effectiveFileBrowserRoots);

        // BOTH stages, including the implied instance root - see ReportDrops's own doc comment for why a
        // diff against only the raw configured list would miss an instance root the POLICY drops
        // (`--DataRoot=\\?\C:\foo`, a device path), leaving a picker with zero roots and nothing saying why.
        FileBrowserOptions.ReportDrops(configuredFileBrowser, effectiveFileBrowserRoots, fileBrowser, Console.Error);

        builder.Services.AddSingleton(fileBrowser);

        // The ledger (`ILedger`) is the provider's, registered with its stores.
        builder.Services.AddSingleton<ITranscriptStore>(new FileTranscriptStore(teamPaths));
        builder.Services.AddSingleton<IContextBuilder>(sp =>
            new OutcomeNudge(
                new LedgerContextBuilder(sp.GetRequiredService<ILedger>()),
                sp.GetRequiredService<IOutcomeStore>()));
    }
}
