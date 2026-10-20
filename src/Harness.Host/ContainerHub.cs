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

/// <summary>
/// Carries nothing inbound except which groups a connection belongs to. Instructions still go
/// through the API, so there is one way IN; this is only about which of the API's own outputs a
/// given socket is allowed to overhear.
/// </summary>
internal sealed class ContainerHub(
    TeamAccess access,
    DiagnosticsRecorder? diagnostics = null,
    TransportWatch? watch = null,
    WorkspaceFileWatch? fileWatch = null) : Hub
{
    /// <summary>
    /// A CONNECTION ARRIVING, recorded only when it is a RECONNECT.
    ///
    /// Every socket this product opens goes through here, so recording each one would put a row on
    /// the instance's busiest store for every tab anybody opens. A reconnect is the interesting
    /// half: it means a connection dropped and the client came back, which is the shape of a
    /// reconnect storm that a console can neither recover from nor describe.
    ///
    /// The DROP itself is always recorded, below. This pairs with it.
    /// </summary>
    /// <summary>
    /// Every connection a PERSON holds, and no machine principal's: what only people may read
    /// (the capacity sample, as <c>GET /api/capacity</c> is <c>HumansOnly</c>) is pushed here.
    /// </summary>
    public const string PeopleGroup = "people";

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        if (Context.User is not null && PrincipalClaims.From(Context.User) is { Kind: PrincipalKind.User })
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, PeopleGroup);
        }

        if (diagnostics is null || watch is null) return;
        if (PrincipalClaims.From(Context.User!) is not { } principal) return;
        if (!watch.WasRecentlyDropped(principal.Id)) return;

        await diagnostics.WriteAsync(
            DiagnosticSeverity.Warning,
            DiagnosticKinds.TransportReconnecting,
            DiagnosticSources.Transport,
            message: principal.Id,
            detail: new { principal = principal.Id, kind = principal.Kind.ToString() },
            ct: Context.ConnectionAborted);
    }

    /// <summary>
    /// A CONNECTION DROPPING. Transport drops are otherwise invisible to the platform, and this is
    /// the only place the server learns of one.
    ///
    /// <para>
    /// AN EXCEPTION MAKES IT AN ERROR AND ITS ABSENCE DOES NOT. A null exception is a client that
    /// said goodbye - a closed tab, a navigation - and is ordinary; a non-null one is a socket that
    /// broke, which is the case worth finding in a filter. Both are recorded, because the RATE of
    /// ordinary disconnects is itself the signal when a page is reloading in a loop.
    /// </para>
    ///
    /// <para>
    /// THE ROW NAMES THE PRINCIPAL, NEVER THE CONNECTION ID. A connection id is meaningless an hour
    /// later and a principal id is the thing a reader can join to a person or a container.
    /// </para>
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);

        fileWatch?.Leave(Context.ConnectionId);

        if (diagnostics is null) return;
        if (Context.User is null || PrincipalClaims.From(Context.User) is not { } principal) return;

        watch?.NoteDropped(principal.Id);

        await diagnostics.WriteAsync(
            exception is null ? DiagnosticSeverity.Info : DiagnosticSeverity.Error,
            DiagnosticKinds.TransportDisconnected,
            DiagnosticSources.Transport,
            exceptionType: exception is null ? null : DiagnosticsRecorder.TypeNameOf(exception),
            message: exception?.Message ?? principal.Id,
            detail: new { principal = principal.Id, kind = principal.Kind.ToString() },

            // CancellationToken.None: the connection has already gone, so
            // `Context.ConnectionAborted` is cancelled by definition and passing it would cancel
            // every write on this path - the one row this method exists to produce.
            ct: CancellationToken.None);
    }

    /// <summary>
    /// Joining is an AUTHORITY decision, not plumbing. The hub has no `{team}` route value for
    /// TeamGate to see, so without a check here a client could join any team's group by naming it,
    /// a side channel around every route the gate protects. It looks and feels like transport
    /// wiring, which is exactly why it gets missed.
    ///
    /// It asks the SAME questions as TeamGate, in the same order, and that ordering is the point
    /// rather than a stylistic echo - see the person check below. Asking `MayActOnAsync` alone would
    /// be asymmetric: effective teams is intersected with what TeamRegistry currently holds, so a
    /// team that does not exist is absent from it for EVERYONE, and teams live in memory. After a
    /// restart the routes correctly answer a person 404 while this would refuse them, and the SPA
    /// turns that refusal into "live updates are unavailable for
    /// this team - reload to try again" - a transport-shaped complaint about a team that is merely
    /// gone. `hub.ts` makes it reachable rather than theoretical: an auto-reconnect rejoins with
    /// the team name the board was already holding, BEFORE `onReconnected` refreshes it, so the
    /// stale name is the first thing the reconnected socket asks for.
    /// </summary>
    public async Task JoinTeam(string team)
    {
        var principal = PrincipalClaims.From(Context.User!)
            ?? throw new HubException(TeamGate.NoSuchTeamMessage);

        // TeamGate's rule, and it must stay in step with the gate. A person reaches every team
        // and joins the group for a name that does not exist and simply hears nothing on it - a
        // group is not a capability, and nothing is published to a team that is not there.
        if (principal.Kind == PrincipalKind.User)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, team, Context.ConnectionAborted);
            return;
        }

        if (!await access.MayActOnAsync(principal, team, Context.ConnectionAborted))
        {
            // Byte-identical to a team that does not exist, matching the gate and the filtered read
            // routes: a refusal must not tell an unprivileged caller whether the name they tried is
            // real. Shares TeamGate.NoSuchTeamMessage rather than a hand-typed copy - see that
            // constant's own doc comment for why the two staying identical is structural, not a
            // coincidence to maintain by discipline.
            throw new HubException(TeamGate.NoSuchTeamMessage);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, team, Context.ConnectionAborted);
    }

    /// <summary>No authority check needed to leave a group - it can only narrow what a connection
    /// receives, never widen it.</summary>
    public async Task LeaveTeam(string team) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, team, Context.ConnectionAborted);

    /// <summary>
    /// The group a person's own pushes go to - every connection they hold, on every device.
    ///
    /// A PERSON, NOT A TEAM, and it cannot be a team group: the message is "your current team is now
    /// X", and the teams it moves between are exactly the groups the connection may not be in yet.
    ///
    /// NO AUTHORITY CHECK IS NEEDED because there is no authority to check: the name comes from the
    /// connection's OWN principal rather than from an argument, so a caller cannot name somebody
    /// else's. That is the whole reason it takes no parameter.
    /// </summary>
    public static string PersonGroup(string userId) => $"user:{userId}";

    public async Task JoinMe()
    {
        if (PrincipalClaims.From(Context.User!) is not { } principal) return;

        await Groups.AddToGroupAsync(
            Context.ConnectionId, PersonGroup(principal.Id), Context.ConnectionAborted);
    }

    /// <summary>
    /// Start hearing <c>workspaceChanged</c>: what a running member's worktrees did, read only while
    /// somebody is in this group. A PERSON ONLY. The group is its own rather than a team's, because a
    /// member's key can join its team's group, and a machine principal is refused outright.
    /// </summary>
    public async Task JoinFileWatch()
    {
        if (fileWatch is null
            || Context.User is null
            || PrincipalClaims.From(Context.User) is not { Kind: PrincipalKind.User })
        {
            throw new HubException("Only a person can watch a member's files.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, WorkspaceFileWatch.Group, Context.ConnectionAborted);
        fileWatch.Join(Context.ConnectionId);
    }

    /// <summary>Stop hearing <c>workspaceChanged</c>. With nobody left, nothing is read.</summary>
    public async Task LeaveFileWatch()
    {
        fileWatch?.Leave(Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkspaceFileWatch.Group, Context.ConnectionAborted);
    }
}
