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
    internal void Push()
    {
        // AFTER UseAuthorization, so context.User is populated, and BEFORE the endpoints run. One gate
        // keyed on the route's team value - see TeamGate's own doc comment for why it is preferred over a
        // per-route filter.
        TeamGate.Use(app);

        // AFTER TeamGate, deliberately. "No such team." must still win, so a caller who cannot see a team
        // learns nothing new about it from a permit refusal that never runs - the byte-identity property
        // that refusal goes to trouble for holds.
        PermitGate.Use(app);

        // Snapshots pushed rather than polled. One multiplexed connection carries every container, which is
        // the whole reason for a hub here: a browser allows about six concurrent connections per origin, and
        // one stream per container would exhaust that as soon as a team is big enough to be interesting.
        var hub = app.Services.GetRequiredService<IHubContext<ContainerHub>>();
        var containers = app.Services.GetRequiredService<ContainerHost>();
        var registryForPush = app.Services.GetRequiredService<TeamRegistry>();

        // Group-routed, not Clients.All. Clients.All would be every connection receiving every team's
        // container state, which is a side channel around every gate the routes above enforce - a browser
        // would learn about another team's queue depth and current instruction the moment anything
        // happened there, regardless of what /api/overview and /api/messages are willing to show.
        // COALESCED PER MEMBER: a changed snapshot goes at once, a progress line's unchanged one is held
        // for a quarter of a second and the newest sent once. See SnapshotPushCoalescer.
        var containerPushes = new SnapshotPushCoalescer(
            snapshot => hub.Clients.Group(snapshot.Team).SendAsync("containerChanged", snapshot),
            SnapshotPushCoalescer.DefaultWindow,
            app.Services.GetRequiredService<TimeProvider>(),
            app.Logger);
        containers.Changed += containerPushes.Offer;
        registryForPush.TeamChanged += team =>
            Observed.Forget(hub.Clients.Group(team.Id).SendAsync("teamChanged", team), app.Logger, "teamChanged push");
    }
}
