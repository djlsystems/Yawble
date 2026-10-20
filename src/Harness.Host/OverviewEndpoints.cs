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

internal static class OverviewEndpoints
{
    public static void Map(WebApplication app, TenantSettings tenantSettings)
    {
        // RequireAuthorization, not a null check in the handler: the policy is what populates context.User
        // for a key-authenticated request while the cookie is the default scheme. A handler that only
        // checked context.User would answer 401 to a valid key.
        // Permits travel here for DISPLAY, and governing access is not one of them: this set is a claim
        // minted at login into a fourteen-day cookie and can be that stale. A person holds every permit;
        // the SPA reads this rather than knowing that. Do not read it as authority anywhere, here or in
        // the SPA.
        app.MapGet("/api/auth/me", (HttpContext context) =>
            PrincipalClaims.From(context.User) is { } principal
                ? Results.Ok(new
                {
                    principal.Id,
                    kind = principal.Kind.ToString(),
                    email = context.User.FindFirstValue(ClaimTypes.Email),
                    permits = principal.Permits,
                })
                : Results.Unauthorized())
            .RequireAuthorization()
            .WithTags("Account")
            .NoPermitRequired()
            .WithSummary("Who the caller is")
            .WithDescription(
                "Identifies whoever is holding this session or key: their id, whether the principal is a "
                + "person or a machine, and their permits.\n\n"
                + "**`permits` is for DISPLAY only.** It is a claim minted into a fourteen-day cookie at "
                + "sign-in and never reissued, so it can be that stale. What actually governs is read from "
                + "the database on every request. The browser uses this to grey out a control; treating it "
                + "as authority anywhere is a bug.");

        // Carries no {team} route value, so TeamGate's rule 1 passes it through unchecked - filtered here
        // instead of refused there. TeamRegistry.Overview() returns `object` and All() is unfiltered, so
        // the filter has to happen at the route rather than inside the registry.
        app.MapGet("/api/overview", async (
            HttpContext context, TeamRegistry teams, TeamAccess access, ITenantLog tenantLog, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            var effective = await access.EffectiveTeamsAsync(principal, ct);
            var visibleTeams = effective.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var activeFindings = await ReadActiveFindingsAsync(tenantLog, visibleTeams, ct);

            // WHERE A TEAM'S FILES ARE is shown to a person and withheld from a machine principal, and
            // this is the one route that answers it - the Console reads its teams from here, and Team
            // Settings shows the root read-only off the team object it already holds. TeamRegistry.All
            // defaults the flag OFF so every other route that projects a team discloses nothing without
            // deciding to.
            var withRoot = principal.Kind == PrincipalKind.User;

            return Results.Ok(new
            {
                teams = teams.All(withRoot).Where(t => visibleTeams.Contains(t.Id)),
                managerName = TeamRegistry.DefaultManagerName,
                activeFindings,

                // **THE CEILING A TEAM CANNOT RAISE, SENT SO IT IS NOT INVISIBLE.**
                //
                // It is instance-wide, so it rides the one payload the Console already fetches once rather
                // than being repeated per team. It is NOT settable from here and must not become so - a
                // bound a team can raise is not a backstop: a `{team}`-gated ceiling is one anyone on the
                // team could raise.
                //
                // BUT UNREACHABLE AND INVISIBLE ARE DIFFERENT THINGS. A team stopped by a number that
                // appears on no screen, in no dialog and in no log until the refusal, while its tile shows
                // a fraction of a bound that is not operating, cannot tell why it stopped.
                //
                // 0 or absent means no instance ceiling, matching how the team figure reads its own zero.
                //
                // SETTABLE, so read now rather than at start: `workflow.spendLimit`.
                workflowSpendLimit = tenantSettings.WorkflowSpendLimit,
            });
        })
            .WithTags("Teams")
            .RequirePermit(Permits.Read)
            .WithSummary("The teams this caller can see")
            .WithDescription(
                "The Console's starting point: every team the caller reaches, each with its Agent Containers and "
                + "their current state, plus the default manager name.\n\n"
                + "Archived teams are listed too, each with `archived: true`, `archivedAt` and `archivedBy` "
                + "(false and null for an active team): a screen leaves them out of its lists of teams to "
                + "work with.\n\n"
                + "A team carries TWO names and they are not interchangeable. `name` is what a person "
                + "chose and what you should render; `id` is the identifier that keys the documents "
                + "folder and half of every container's identity, and it is what every other "
                + "route in this API expects in its path. Getting them the wrong way round points a request "
                + "at a team that does not exist, and nothing fails loudly.\n\n"
                + "Filtered to the caller's effective teams. Teams and their members are restored from the "
                + "database at startup, so a restart does not empty this list - an empty one means "
                + "the caller reaches no team, not that the host has just come up.");

        // WHICH TEAM THIS PERSON IS WORKING ON. One fact, one owner, read per command.
        //
        // NEITHER OF THESE TWO ROUTES DECLARES {team}, SO NEITHER IS INSIDE TeamGate - the gate keys on the
        // route value, which couples it to structure rather than spelling. That is why the PUT below asks
        // the gate's own questions itself, in the gate's own order. It is also why they cannot be built the
        // other way round: a route that let the SERVER default a team would have to be team-scoped without
        // a {team} segment, and an ungated team-scoped route is the shape this codebase refuses.
        //
        // The value lives on the PERSON, never on the credential, because a browser and the Concierge it
        // drives are two principals for one person - keyed on either, the browser would write something the
        // agent never reads.
        app.MapGet("/api/me/current-team", async (
            HttpContext context, TeamRegistry teams, IUserStore users, TeamDocuments documents,
            CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
            if (CurrentTeam.PersonBehind(principal) is not { } person) return CurrentTeam.NotAPersonsDoor;

            var stored = await users.CurrentTeamForAsync(person, ct);

            if (stored is null)
            {
                return Results.Ok(
                    new { team = (string?)null, name = (string?)null, kind = principal.Kind.ToString(), shared = (string?)null });
            }

            // The team may have been deleted or relabelled since it was chosen. Answered rather than cleared:
            // a caller that knows the name it is pointed at can say what went, where a silently-emptied
            // value looks like the person never chose one.
            var canonical = teams.ExistingName(stored);

            return Results.Ok(new
            {
                team = canonical ?? stored,
                name = canonical is null ? null : teams.LabelFor(canonical),

                // THE CALLER'S PRINCIPAL KIND, so a caller knows whether it may use an implicit team.
                // A Concierge is deliberately given no current team of its own (HARNESS_TEAM absent),
                // and must name the team on team-scoped tools. Only the server knows the principal kind;
                // this field is the single authority for that decision.
                kind = principal.Kind.ToString(),

                // WHERE THIS TEAM KEEPS ITS SHARED WORK, and this is a Concierge's only route to it: an
                // environment variable written once at spawn would be frozen, and this is DERIVED from the
                // current team.
                //
                // FOR A CONCIERGE ONLY, and that bound is a disclosure decision rather than tidiness. This
                // is an absolute path on the Host's filesystem, which `/api/overview` shows to a person
                // only. A Concierge is exempt because it is the one principal that works across every
                // team it opens and needs this value for each - so answering it here is no new disclosure,
                // where answering a browser or a pasted API key would be. Through TeamDocuments rather than
                // composed here, so one place still decides where a team's documents live.
                shared = canonical is not null && principal.Kind == PrincipalKind.TenantConcierge
                    ? documents.RootFor(canonical)
                    : null,
            });
        })
            .WithTags("Teams")
            .RequirePermit(Permits.Read)
            .WithSummary("Which team this caller is currently working on")
            .WithDescription(
                "`team` is NULL when no team is active, which is a real state rather than a "
                + "misconfiguration - it is where somebody on the Teams tab genuinely is.\n\n"
                + "`name` is the label to render and `team` is the identifier to address with. `name` is "
                + "NULL when the stored team no longer exists, and `team` still names it so a caller can "
                + "say which team went.\n\n"
                + "Refused for a container: its team is half its identity and arrives in HARNESS_TEAM.");

        app.MapPut("/api/me/current-team", async (
            SetCurrentTeam request, HttpContext context, TeamRegistry teams, TeamAccess access,
            IUserStore users, IHubContext<ContainerHub> hub, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
            if (CurrentTeam.PersonBehind(principal) is not { } person) return CurrentTeam.NotAPersonsDoor;

            // Clearing needs no authority beyond being a person: "no team is active" names nobody.
            if (string.IsNullOrWhiteSpace(request.Team))
            {
                await users.SetCurrentTeamAsync(person, null, ct);
                await CurrentTeam.AnnounceAsync(hub, person, null, null, ct);

                return Results.NoContent();
            }

            // TeamGate's rule, for its reasons: a person reaches every team and gets the handler's own 404
            // for a name that does not exist; a machine principal is bound to its team and is refused a
            // team that is not its own with the same body a missing team would get.
            if (principal.Kind != PrincipalKind.User
                && !await access.MayActOnAsync(principal, request.Team, ct))
            {
                return Results.Content(
                    TeamGate.NoSuchTeam, "application/json", statusCode: StatusCodes.Status403Forbidden);
            }

            // Those callers reach the handler and get its ordinary 404, exactly as they do on a
            // {team}-scoped route.
            if (teams.ExistingName(request.Team) is not { } canonical)
            {
                return Refusals.NoTeam(request.Team);
            }

            // The CANONICAL spelling, never the caller's. Team identity is case-insensitive and every
            // reader compares ordinally, so storing what was typed makes one team two.
            await users.SetCurrentTeamAsync(person, canonical, ct);
            await CurrentTeam.AnnounceAsync(hub, person, canonical, teams.LabelFor(canonical), ct);

            return Results.NoContent();
        })
            .WithTags("Teams")
            .RequirePermit(Permits.Read)
            .WithSummary("Point this caller at a team, or at none")
            .WithDescription(
                "A null or empty `team` clears it, which is what the Teams tab does: no team is active.\n\n"
                + "403 with the gate's own words for a team the caller does not reach - byte-identical to a "
                + "team that does not exist, so probing cannot tell the two apart. A person naming a "
                + "team that does not exist gets 404 instead, because for them 'not yours' is not a state.\n\n"
                + "Refused for a container: a container's team is half its identity and must not move.");

        app.MapGet("/api/teams", async (
            HttpContext context, TeamRegistry teams, TeamAccess access, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            var effective = await access.EffectiveTeamsAsync(principal, ct);
            var withRoot = principal.Kind == PrincipalKind.User;

            return Results.Ok(teams.All(withRoot).Where(t => effective.Contains(t.Id)));
        })
            .WithTags("Teams")
            .RequirePermit(Permits.Read)
            .WithSummary("List teams this caller can see")
            .WithDescription(
                "Returns this caller's visible teams, already scoped by TeamAccess. A person sees every "
                + "team that exists; a machine principal sees the teams its identity reaches.\n\n"
                + "A team carries two names: `name` is what a person reads, `id` is what routes use. This "
                + "route returns both.");

        app.MapGet("/api/events", () => Results.Ok(EventCatalog.WithPlugins()))
            .WithSummary("Every event type this platform can publish.")
            .WithDescription(
                "The catalog a trigger picker is built from. It describes the platform rather than any "
                + "team's data, which is why it needs no permit and declares no {team}. After the "
                + "platform's own types come those the installed plugins declare, `plugin.<id>.<suffix>`, "
                + "with `publisher` `Plugin`.")
            .NoPermitRequired();

        // `name` here is what a PERSON typed - free text, spaces and accents included. The identifier
        // everything else keys on is derived from it inside TeamRegistry and is never asked for, never
        // returned as a separate field and never shown. See TeamRegistry.CreateAsync.
        // The tenant log. PEOPLE ONLY: it names every account on the instance and what was done to them,
        // which is a roster an agent enumerating it would be reading for no reason of its own.
        app.MapGet("/api/tenant-log", async (
            [Description(
                "Only rows with a seq below this one - the last row's seq from the previous page. Omit it "
                + "for the newest page.")] long? before,
            [Description("How many to return. Default 50, clamped to 1..200.")] int? take,
            ITenantLog log, CancellationToken ct) =>
            Results.Ok(await log.ReadAsync(before, Math.Clamp(take ?? 50, 1, ITenantLog.MaxTake), ct)))
            .WithTags("Tenant")
            .HumansOnly()
            .WithSummary("Read the tenant log")
            .WithDescription(
                "Administrative acts, newest first: who signed in, who created or deleted a team, who "
                + "changed an account's access. **Not the message log** - that is the causal stream Agent "
                + "Containers publish to and read from, holds only `container.*` types, and is what a "
                + "member's own activity feed shows.\n\n"
                + "This log OUTLIVES its subjects deliberately. The row saying a team was deleted survives "
                + "the team, and the row saying an account was removed survives the account, which is why "
                + "the actor's email and the subject's name are stored on the row rather than joined.\n\n"
                + "Answers `{ events, total }`, newest first. Paged by a seq cursor: pass the last row's "
                + "`seq` as `before` for the next, older page; a page shorter than `take` is the end. "
                + "A row appended while somebody scrolls lands above the first page and never shifts an "
                + "older one. `total` counts the whole log regardless of the cursor - a caption.\n\n"
                + "It never contains a credential, a password hash or an Agent definition's `env`.\n\n"
                + "**A person's action.**");
    }

    static bool TryFindingTeam(string subject, out string team)
    {
        team = "";
        if (string.IsNullOrWhiteSpace(subject)) return false;

        var slash = subject.IndexOf('/');
        team = slash < 0 ? subject : subject[..slash];
        return team.Length > 0;
    }

    static async Task<IReadOnlyList<object>> ReadActiveFindingsAsync(
        ITenantLog tenantLog,
        IReadOnlySet<string> visibleTeams,
        CancellationToken ct)
    {
        var reportKinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TenantActions.SweepRunningWithoutProgress] = nameof(QuietTeamSweepFindingKind.RunningWithoutProgress),
            [TenantActions.SweepPendingNeverTerminal] = nameof(QuietTeamSweepFindingKind.PendingAcceptedWithoutTerminal),
            [TenantActions.SweepQuietTeam] = nameof(QuietTeamSweepFindingKind.QuietTeam),
            [TenantActions.SweepWrapUpNotPushed] = nameof(QuietTeamSweepFindingKind.WrapUpNotPushed),
        };
        var clearedToReport = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TenantActions.SweepRunningWithoutProgressCleared] = TenantActions.SweepRunningWithoutProgress,
            [TenantActions.SweepPendingNeverTerminalCleared] = TenantActions.SweepPendingNeverTerminal,
            [TenantActions.SweepQuietTeamCleared] = TenantActions.SweepQuietTeam,
            [TenantActions.SweepWrapUpNotPushedCleared] = TenantActions.SweepWrapUpNotPushed,
        };
        var newestBySubject = new Dictionary<(string Action, string Subject), (long Seq, bool IsReport, string Team, string Kind, string Subject)>();
        var rows = await tenantLog.FindLatestBySubjectAsync(
            [.. reportKinds.Keys, .. clearedToReport.Keys],
            ct);

        foreach (var row in rows)
        {
            if (row.Subject is not { } subject || !TryFindingTeam(subject, out var team))
            {
                continue;
            }

            if (!visibleTeams.Contains(team))
            {
                continue;
            }

            string reportAction;
            bool isReport;

            if (reportKinds.ContainsKey(row.Action))
            {
                reportAction = row.Action;
                isReport = true;
            }
            else if (clearedToReport.TryGetValue(row.Action, out var mapped))
            {
                reportAction = mapped;
                isReport = false;
            }
            else
            {
                continue;
            }

            var key = (reportAction, subject);
            if (!newestBySubject.TryGetValue(key, out var current) || row.Seq > current.Seq)
            {
                newestBySubject[key] = (row.Seq, isReport, team, reportKinds[reportAction], subject);
            }
        }

        return newestBySubject.Values
            .Where(row => row.IsReport)
            .OrderBy(row => row.Team, StringComparer.Ordinal)
            .ThenBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.Subject, StringComparer.Ordinal)
            .Select(row => (object)new
            {
                team = row.Team,
                kind = row.Kind,
            })
            .ToArray();
    }
}
