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

internal static class ConciergeEndpoints
{
    public static void Map(WebApplication app)
    {
        // Its OWN route because this is tenant-wide Concierge launch configuration, not team
        // metadata at all.
        app.MapGet("/api/concierge", async (
            TeamRegistry teams, AgentCatalog catalog, AgentAuthProbe probe, ConciergeSessionStore consoles,
            CapacitySampler capacity, TenantSettings settings, IUserStore accounts, CancellationToken ct) =>
            Results.Ok(await ConciergeView.ReadAsync(
                teams.Concierge(), catalog, probe,
                await ConciergeSessionsView.ReadAsync(
                    consoles, settings.ConciergeIdleTimeout, capacity.Terminal,
                    async (user, token) => (await accounts.FindByIdAsync(user, token))?.Email, ct),
                ct)))
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("What the tenant Concierge launches")
            .WithDescription(
                "One setting for the whole instance. NO TEAM IS NAMED, and that is the point: reading it through a team read as though the setting belonged to that team.\n\n"
                + "There is no prompt setting: the Concierge always runs the built-in Concierge prompt.\n\n"
                + "`effective` is what the launcher will actually use after defaults, from the same code the launcher runs: `agent` with `agentSource` (`chosen` or `default`), and `auth`, the auth probe's verdict for that agent. `agent` is NULL when no agent can start. `auth.signedIn` is true only when the probe measured a sign-in, and `auth.detail` says what it found.\n\n"
                + "`sessions` is every running Concierge session, any person's, oldest first: `user` and `email`; `worker`, the worker its terminal runs on; `startedAt`; `viewer`, whether a browser has it open now, and `lastViewerAt`, null while one does, otherwise when the last left (its start, when nobody ever opened it); `lastActivityAt` and `lastActivity` (`started`, `output`, `typed`, `attached` or `call`), now while `callsInFlight` - platform calls under its own credential - is above 0; `outputFloor`, the bytes in one minute its output must exceed to count as activity, with `source` `declared` (its preset's measured floor, `measuredWith` saying what was measured) or `default` (the platform's, never a measurement); `outputPerMinute`, the bytes it printed in each of its last 60 minutes, oldest first, which is how a floor is measured; `wouldEndAt`, null while viewed, otherwise the later of `lastViewerAt` and `lastActivityAt` plus `concierge.idleTimeout` - it is ended at the first sweep at or after it; and `memory`, the resident bytes and processes of its process group on its worker at the last capacity sample, null when not measured.\n\n"
                + "**A person's action.**");

        app.MapPut("/api/concierge", async (
            SetConcierge request, TeamRegistry teams, HttpContext context,
            CancellationToken ct) =>
        {
            try
            {
                // One transaction with its tenant_events row.
                await teams.SetInteractiveAsync(
                    request.Agent, ct,
                    TenantLogging.Row(context, TenantActions.ConciergeChanged, null, null, new { agent = request.Agent }));

                return Results.NoContent();
            }
            catch (NoSuchAgentException exception)
            {
                // 400, not 404: the TEAM is there, it is the Agent that is missing or cannot be typed at.
                return Results.BadRequest(new { error = exception.Message });
            }
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Choose which Agent the tenant Concierge launches")
            .WithDescription(
                "The preset must be INTERACTIVE - a headless one has no command that can be typed at, and "
                + "is refused with a message that says which of the two problems it was.\n\n"
                + "Takes effect on the next Concierge opened by any user. A session already "
                + "attached keeps the process it started with.\n\n"
                + "400 when the Agent is unknown or is headless.\n\n"
                + "**A person's action.**");

        // cols/rows are on the CONNECT request, not left to the first resize frame. The child prints its
        // banner the instant it spawns, so a size that arrives afterwards means those bytes were laid out for
        // a guessed default width - and a terminal transcript has its width baked in, so replaying them into
        // the real terminal is scrambled rather than merely narrow.
        //
        // One Concierge per PERSON, not per team, so the route names no team. Outside TeamGate because the
        // template has no {team}.
        //
        // MUST NOT call SetCurrentTeamAsync. Opening the Concierge must leave the current team as it is.
        // Setting it here would be picking a team on the person's behalf.
        app.MapGet("/api/concierge/ws", async (
            int? cols, int? rows, string? tz, HttpContext context,
            ConciergeSessionStore consoles, WorkerPool workers, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } caller) return Results.Unauthorized();

            // A console is a HUMAN's door. A machine principal has no person to belong to: its id is not a
            // user id, so minting would fail the owner foreign key and surface as a 500 on a WebSocket
            // upgrade - a confusing answer to a request that should simply be refused.
            if (caller.Kind != PrincipalKind.User)
            {
                return Results.Content(PermitGate.HumansOnlyBody, "application/json", statusCode: 403);
            }

            // MUST NOT call SetCurrentTeamAsync.

            var key = new ConciergeSessionKey(caller.Id);

            var width = PtyDimensionLimits.ClampOrDefault(cols, PtySpecDefaults.Cols);
            var height = PtyDimensionLimits.ClampOrDefault(rows, PtySpecDefaults.Rows);

            if (!context.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest(new { error = "This endpoint speaks WebSocket." });
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();

            // NO WORKER, NO TERMINAL: a Concierge starts on a worker. The person is told once and the socket
            // waits for one to connect; nothing is minted meanwhile, so leaving now leaves nothing behind. A
            // person with a live session reattaches to it whatever the workers do.
            if (!consoles.Has(key) && workers.Workers.Count == 0)
            {
                try
                {
                    await socket.SendAsync(
                        Encoding.UTF8.GetBytes(WorkerPtyEngine.WaitingText + "\r\n"), WebSocketMessageType.Binary, true, ct);
                    while (workers.Workers.Count == 0) await workers.ConnectedAsync(ct);
                }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
                {
                    return Results.Empty;
                }
            }

            ConciergeSession console;

            try
            {
                // Attached AFTER the upgrade so that a launch failure can be reported in the terminal. A
                // refused upgrade shows an empty panel and puts the reason in devtools.
                // THE ADDRESS THIS BROWSER REACHED US ON becomes the Concierge's HARNESS_PUBLIC_URL. The
                // scheme is the tunnel's when one forwarded it (UseForwardedHeaders); the host is what the
                // browser asked for. THE ZONE THE BROWSER NAMED (`tz`) is the one the Concierge states times
                // in, so they read as the page does; the launch checks it.
                console = await consoles.AttachAsync(
                    key, "", width, height, ct,
                    new ConciergeBrowser(
                        $"{context.Request.Scheme}://{context.Request.Host.Value}{context.Request.PathBase.Value}", tz));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var message = Encoding.UTF8.GetBytes(
                    $"\u001b[31mThe console could not start: {ex.Message}\u001b[0m\r\n");

                await socket.SendAsync(message, WebSocketMessageType.Binary, true, ct);
                await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "launch failed", ct);
                return Results.Empty;
            }

            await PtyWebSocket.PumpAsync(
                socket, console.Record, console.Session, console.Attachment, width, height, ct);

            // Deliberately nothing here: the pump returning means this CLIENT went away, and the session
            // keeps running for whoever attaches next. ConciergeReaper bounds a session nobody returns to.
            return Results.Empty;
        })
            // A WebSocket upgrade wearing a GET, so it is described out of the API reference rather than
            // rendered there with a Send button that can only ever fail.
            .ExcludeFromDescription()
            .HumansOnly();

        // AN IMAGE FOR THE PERSON'S OWN CONCIERGE, saved into its working folder so the page can paste the
        // path into the prompt. A person's only: a machine principal is refused by the marker, and no member
        // has a route, tool or folder for this. See ConciergeAttachments for the rules it keeps.
        app.MapPost("/api/concierge/attachments", async (
            HttpRequest request, HttpContext context, ConciergeAttachments attachments, ConciergeSessionStore consoles,
            ITenantLog log, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } caller) return Results.Unauthorized();

            if (caller.Kind != PrincipalKind.User)
            {
                return Results.Content(PermitGate.HumansOnlyBody, "application/json", statusCode: 403);
            }

            // NO CLI, NO FILE. Nothing would read the path, and the file would wait for the next clean-up.
            // Before the form is read, so nothing of the image is kept. The panel shows this sentence as it
            // stands, word for word; a CLI that exited has ended its session, so it counts too.
            if (!consoles.Has(new ConciergeSessionKey(caller.Id)))
            {
                return Results.Json(
                    new { error = "The image was not attached: the Concierge is not running. Start it, then attach the image again." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Send the image as a form, in the field \"file\"." });
            }

            IFormCollection form;

            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Results.Json(
                    new { error = $"That image is larger than {attachments.MaxBytes / (1024 * 1024)} MB, so it was not attached." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            if (form.Files.GetFile("file") is not { } file)
            {
                return Results.BadRequest(new { error = "Send the image as a form, in the field \"file\"." });
            }

            try
            {
                await using var content = file.OpenReadStream();

                var saved = await attachments.SaveAsync(
                    caller.Id, content, file.Length,
                    // NOT TenantLogging.WriteAsync, which swallows: this row is the condition of the file.
                    // Size and type only - never the image, never the name it was uploaded under.
                    (saved, token) => log.WriteAsync(
                        context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                        context.User.FindFirstValue(ClaimTypes.Email),
                        TenantActions.ConciergeAttachmentAdded, null, null,
                        JsonSerializer.Serialize(new { size = saved.Size, type = saved.Type }),
                        token),
                    DateTimeOffset.UtcNow, ct);

                return Results.Ok(new { path = saved.Path, size = saved.Size, type = saved.Type });
            }
            catch (ConciergeAttachmentRefused refused)
            {
                return Results.Json(new { error = refused.Message }, statusCode: refused.Status);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(
                    new { error = "The image was not attached: it could not be saved and recorded, so nothing was kept." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
            .WithTags("Concierge")
            .HumansOnly()
            .WithSummary("Attach an image to this person's Concierge")
            .WithDescription(
                "A multipart form with the image in `file`. Saved as `attachments/<utc-timestamp>-<n>.<ext>` in "
                + "the person's own Concierge working folder, readable by the agent; the uploaded name is never "
                + "used. Answers `path` (absolute), `size` and `type`.\n\n"
                + "Only PNG, JPEG, GIF and WebP, told by their content and never by name or Content-Type: 415 "
                + "for anything else. 413 over the size cap (10 MB unless `ConciergeAttachmentMaxBytes` says "
                + "otherwise). 409 when the person has no Concierge running, or no Concierge folder yet. Recorded as "
                + "`concierge.attachment-added` with the size and type, never the image; a file whose row "
                + "cannot be written is removed. Attachments are removed when the session ends, and those older "
                + "than the retention (7 days unless `ConciergeAttachmentRetention` says otherwise) at start.\n\n"
                + "**A person's action.**");

        // Ending a session is explicit, and separate from closing the panel. There is no close-that-kills on
        // the panel chrome at all - this is what a confirmed action in settings calls.
        // The session is keyed on the person, so ending it needs no team in the path. MUST NOT call
        // SetCurrentTeamAsync.
        app.MapDelete("/api/concierge", async (
            [Description(
                "Whose console to end. Omit it to end your own; any person may end anyone's.")]
            string? user,
            HttpContext context, ConciergeSessionStore consoles) =>
        {
            if (PrincipalClaims.From(context.User) is not { } caller) return Results.Unauthorized();

            await consoles.EndAsync(new ConciergeSessionKey(user ?? caller.Id));

            return Results.NoContent();
        })
            .WithTags("Concierge")
            .HumansOnly()
            .WithSummary("End this person's Concierge session")
            .WithDescription(
                "Kills the Concierge's process. This is separate from closing the panel on purpose: "
                + "detaching from a session and destroying it are different intentions and only one of "
                + "them is recoverable.\n\n"
                + "The session is keyed on the person, so this names no team and does not point the "
                + "person at one. A session nobody returns to is ended on a timer as well.\n\n"
                + "Always 204 when it is allowed at all, whether or not a session was running.");

        // Read, because that is honestly what a hub connection is: the board, pushed. Nothing
        // machine-shaped subscribes to SignalR today - a container reaches the system through MCP tools -
        // but declaring HumansOnly would be a bound justified by "nothing does this yet", which is the
        // weakest reason there is and the one that stops being true without anybody noticing.
        app.MapHub<ContainerHub>("/hub/containers").RequirePermit(Permits.Read);
    }
}
