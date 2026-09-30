using System.ComponentModel;
using Harness.Containers;
using Harness.Contracts;
using Harness.Kanban;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>What a caller may see, resolved once per request.</summary>
/// <param name="Teams">
/// Null for a caller who may administer the tenant backlog - a person, or a TenantConcierge whose
/// owner still exists - and otherwise their effective set. Null is NOT "none": it is the same "no
/// narrowing" that <see cref="IBacklogStore.ListAsync"/> takes, and conflating the two would show
/// a backlog administrator an empty backlog.
/// </param>
/// <param name="Unlinked">Whether items with no team are theirs to see. Backlog administrators only.</param>
internal sealed record BacklogView(IReadOnlySet<string>? Teams, bool Unlinked);

/// <summary>
/// The tenant backlog.
///
/// <para>
/// THESE ROUTES DECLARE NO <c>{team}</c>, so <c>TeamGate</c> does not cover them and they ask the
/// gate's own questions THEMSELVES, IN THE GATE'S ORDER - person first, effective set second.
/// That order is not a style choice. Reversed, a person gets 403 for a team that does not
/// exist, and in the moments after a restart every team is briefly "not there" for everybody. It is
/// the same care <c>/api/me/current-team</c> has to take, and the model is
/// <c>/api/kanban/board</c>, which solved this exact problem.
/// </para>
///
/// <para>
/// AN ITEM A CALLER MAY NOT SEE IS ABSENT, NEVER REFUSED. A 404 and a 403 answer different
/// questions and only one of them is safe to answer - a refusal would tell somebody not entitled to
/// ask whether an item exists.
/// </para>
///
/// <para>
/// NOT <c>.HumansOnly()</c>, and the reason is scheduled rather than speculative: the next spec puts
/// the Concierge into the planning conversation, and a marker excluding it would have to be removed
/// by that spec - which is how a gate acquires a hole, one exemption at a time. Admitting the one
/// non-human principal that is a person's own door is the smaller and more honest statement. A
/// Concierge's effective teams are its owner's, so it sees exactly what the person driving it sees.
/// </para>
/// </summary>
public static class BacklogEndpoints
{
    /// <summary>
    /// The two principal kinds that may reach the tenant backlog at all: a person and the tenant
    /// Concierge. A member container reaches NONE of this - see
    /// the team-scoped read below for how a Manager gets the one item it was given.
    /// </summary>
    private static bool MayReach(Principal principal) =>
        principal.Kind is PrincipalKind.User or PrincipalKind.TenantConcierge;

    /// <summary>
    /// WHAT THE KIND REFUSAL SAYS. A bare 403 with no body reads to a member as "wrong
    /// credential", and it goes looking for another one. There is no credential that puts a
    /// member on the tenant backlog, and this says so in the refusal rather than leaving it to be
    /// discovered.
    /// </summary>
    private const string NotReachable =
        "The tenant backlog is a person's to change, and a member reaches none of it. "
        + PermitGate.NeedsAPerson;

    /// <summary>
    /// ONE REFUSAL, NINE CALL SITES. Nine hand-typed copies is nine places for the wording to
    /// drift, and the wording is the thing that stops a member searching for a credential.
    /// </summary>
    private static IResult RefuseKind() =>
        Results.Json(new { error = NotReachable }, statusCode: StatusCodes.Status403Forbidden);

    private const int ArchiveDefaultTake = 50;
    private const int ArchiveMaxTake = 200;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/backlog", async (
            HttpContext context, IBacklogStore backlog, TeamRegistry teams, TeamAccess access,
            IMessageLog log, ContainerHost host, TeamPaths paths, GitRunner git,
            BacklogLandedCache landedCache, PullRequestStateReader pullRequests, KanbanStore kanban,
            [Description("Archived items instead of the backlog. The two tabs on the screen.")]
            bool archived = false,
            [Description(
                "With `archived=true`: only items with an id below this one - the last item's id "
                + "from the previous page. Omit it for the newest page.")]
            long? before = null,
            [Description("With `archived=true`: how many to return. Default 50, clamped to 1..200.")]
            int? take = null,
            CancellationToken ct = default) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
            if (!MayReach(principal)) return RefuseKind();

            // THE LIVE BACKLOG IS NOT PAGED. It is an ordered list a person drags to reorder, and a
            // cursor over it would be a cursor over positions every drag rewrites.
            if (!archived && (before is not null || take is not null))
            {
                return Results.BadRequest(new
                {
                    error = "Only the archive is paged; `before` and `take` need `archived=true`.",
                });
            }

            var view = await ViewFor(principal, access, ct);
            var items = archived
                ? await backlog.ListArchivedAsync(
                    view.Teams, view.Unlinked, before,
                    Math.Clamp(take ?? ArchiveDefaultTake, 1, ArchiveMaxTake), ct)
                : await backlog.ListAsync(view.Teams, view.Unlinked, archived: false, ct);

            // IN-FLIGHT STATE FOR THE WHOLE LIST IN TWO QUERIES, never one per row: every item's
            // current dispatch in one store read, narrowed to the rows being shown, then one log
            // question over those correlations. See BacklogInFlightState for what it costs.
            var shown = items.Select(item => item.Id).ToHashSet();

            // KEYED BY ITEM AND READ TWICE, not read twice from the store. `inFlight` keeps a
            // dispatch only while its workflow is open, and the Team column has to name the team
            // afterwards too - so the same one query answers both, and neither answer costs a row.
            var latest = (await backlog.LatestDispatchesAsync(ct))
                .Where(d => shown.Contains(d.Item))
                .ToDictionary(d => d.Item);

            var inFlight = await BacklogInFlightState.ForAsync(latest.Values, teams, host, log, ct);

            // AND WHICH OF THOSE WERE LEFT BEHIND: open, Blocked or Failed, with their work done in
            // a later workflow. Asked only of in-flight items. See BacklogStrandedState.
            var stranded = await BacklogStrandedState.ForAsync(inFlight, teams, log, kanban, ct);

            // AND WHETHER THE WORK ACTUALLY LANDED. Off the SAME one store read, so this
            // adds no query; what it adds is git, which is why it is bounded per TEAM and cached
            // rather than asked per row. See BacklogLandedState for what it costs and what caps it.
            var landed = await BacklogLandedState.ForAsync(
                latest.Values, teams, paths, git, landedCache, ct, pullRequests, backlog);

            return Results.Ok(items
                .Select(item => Render(
                    item, teams, inFlight.GetValueOrDefault(item.Id), latest.GetValueOrDefault(item.Id),
                    landed.GetValueOrDefault(item.Id), stranded.GetValueOrDefault(item.Id)))
                .ToList());
        })
            .RequirePermit(Permits.Read)
            .WithTags("Backlog")
            .WithSummary("The backlog, or the archive")
            .WithDescription(
                "Ordered by position over the WHOLE backlog - filtering to a team shows the global "
                + "order restricted to those items, never a per-filter order that would be several "
                + "orders disagreeing.\n\n"
                + "A person - or a tenant Concierge acting for one - sees every item, linked "
                + "and unlinked. A machine principal sees items linked to teams it reaches. An item they "
                + "may not see is ABSENT rather than refused.\n\n"
                + "The `#` a person reads on the screen is computed CLIENT-SIDE from this order. It "
                + "is never sent, never stored, and never addresses anything - the `id` is what "
                + "does, rendered `B000H` (four zero-padded Crockford base-32 characters, so ids sort "
                + "by name; the `id` field itself stays an integer and this route takes the integer).\n\n"
                + "THE ARCHIVE IS PAGED AND ORDERED BY `id`, NEWEST FIRST, not by position: "
                + "`archived=true&before=<id>&take=<n>` answers archived items with an id below "
                + "`before` (omit it for the newest page), at most `take` (default 50, clamped to "
                + "1..200). The next cursor is the last item's `id`; an empty page is the end. The "
                + "live backlog is not paged, and `before`/`take` without `archived=true` is a "
                + "400.\n\n"
                + "`inFlight` names the team WORKING an item and the workflow, while that "
                + "workflow is open - derived from the current dispatch and the log, never stored. "
                + "It is not the team LINK, which says who may see the item and is never rewritten "
                + "by a dispatch, and it is not a state: an item in flight is still `ready`, which "
                + "is the state it had to be in to be dispatched at all.\n\n"
                + "`dispatchedTeam` and `dispatchedTeamName` name the team the CURRENT dispatch "
                + "went to, whether or not its workflow is still open - the third of those facts "
                + "and the one the Team column shows. `inFlight` cannot answer it, because it is "
                + "dropped the moment the workflow closes, and most dispatched work is finished "
                + "work. Both are null for an item never dispatched. A dispatch whose team has "
                + "SINCE BEEN DELETED is still reported, under the name the record kept, with "
                + "`dispatchedTeamGone` - while `inFlight` is null, because a team that is gone is "
                + "working nothing.\n\n"
                + "`stranded` is set when the current dispatch's workflow is still open and Blocked "
                + "or Failed while a LATER workflow on the same team took one of its cards to Done: "
                + "`notice` says \"The work continued in workflow N.\", and `close` names the "
                + "existing person-only close route for the original workflow with a suggested "
                + "reason. It is an offer. Nothing is closed, declared or marked until a person "
                + "calls that route. Null otherwise.\n\n"
                + "`landed` says whether the CURRENT dispatch's work reached origin's default "
                + "branch, derived from that team's clone. Once `landed` is proven it is STORED on "
                + "the dispatch and answered from there, with `landedAt`, even after the branch, the "
                + "clone and the team are gone; it is never downgraded. With the team's clone unable "
                + "to say, the tip its publish recorded is checked in a clone of the same repository "
                + "on this instance, after a fetch. `landed.state` is one "
                + "of `landed` (on origin's default branch), `pushed` (on a remote branch, not yet "
                + "on origin's default branch), `local` (commits on no remote at all - the state "
                + "work is in when it exists on one disk only) and `unknown`. `unknown` IS AN ANSWER: the team is gone, the clone is "
                + "missing, git could not be read, the clone holds nothing traceable to the item, "
                + "or a rebase has broken ancestry so reachability cannot settle it. `detail` says "
                + "which, in a sentence. Null for an item never dispatched.\n\n"
                + "For a repository in contributor mode (it has an upstream), landed means the "
                + "recorded pull request was merged, asked of GitHub at most once a minute per pull "
                + "request: `in-review` (open), `declined` (closed without merging, never landed) and "
                + "`landed` (merged). GitHub unreachable or the token refused is `unknown`. `readAt` "
                + "is when GitHub last answered; null for an answer read from the clone.");

        app.MapGet("/api/backlog/{id:long}", async (
            long id, HttpContext context, IBacklogStore backlog, TeamRegistry teams,
            TeamAccess access, IMessageLog log, ContainerHost host, TeamPaths paths, GitRunner git,
            BacklogLandedCache landedCache, PullRequestStateReader pullRequests, KanbanStore kanban,
            ITenantLog tenantLog, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
            if (!MayReach(principal)) return RefuseKind();

            var view = await ViewFor(principal, access, ct);

            if (await backlog.GetAsync(id, ct) is not { } item || !Visible(item, view))
            {
                return Results.NotFound(new { error = $"No backlog item {PlatformBacklogId.Format(id)}." });
            }

            var dispatches = await backlog.DispatchesAsync(id, ct);

            var records = new List<BacklogExecutionStats>();
            foreach (var d in dispatches) records.Add(await BacklogExecutionRecord.ForAsync(d, log, ct));

            // THE SAME ANSWER THE LIST GAVE, from the current dispatch - the last - so a person who
            // opened the row does not read a different team than the row showed. The history is
            // already in hand, so both answers come out of it and neither is a second round trip.
            var current = dispatches.Count == 0 ? null : dispatches[^1];

            var inFlight = current is null
                ? null
                : (await BacklogInFlightState.ForAsync([current], teams, host, log, ct))
                    .GetValueOrDefault(id);

            var stranded = inFlight is null
                ? null
                : (await BacklogStrandedState.ForAsync(
                    new Dictionary<long, BacklogInFlight> { [id] = inFlight }, teams, log, kanban, ct))
                    .GetValueOrDefault(id);

            // THE SAME ANSWER THE LIST GAVE, for the same reason `inFlight` is recomputed here: a
            // person who opened the row must not read a different verdict than the row showed. The
            // team's measurement is cached, so opening a row the list has just rendered is free.
            var landed = current is null
                ? null
                : (await BacklogLandedState.ForAsync([current], teams, paths, git, landedCache, ct, pullRequests, backlog))
                    .GetValueOrDefault(id);

            // WHO SAID THE WORK IS IN THE PRODUCT, while it is marked so. The row outlives the person
            // and the team; a person's word when landed could not be proven reads the same way.
            var implemented = item.State == BacklogStates.Implemented
                ? await tenantLog.FindLatestAsync(TenantActions.BacklogItemImplemented, PlatformBacklogId.Format(id), ct)
                : null;

            return Results.Ok(new
            {
                item = Render(item, teams, inFlight, current, landed, stranded),
                implementedBy = implemented is null
                    ? null
                    : new
                    {
                        by = implemented.ActorEmail ?? implemented.ActorId,
                        at = implemented.OccurredAt,
                        viaConcierge = ViaConcierge(implemented.Detail),
                    },
                dispatches = dispatches.Select(d => new
                {
                    d.Id,
                    d.TeamId,
                    d.TeamName,
                    d.Correlation,
                    d.DispatchedAt,
                    d.DispatchedBy,
                    d.FrozenAt,
                    teamGone = teams.ExistingName(d.TeamId) is null,
                }).ToList(),
                stats = records,
            });
        })
            .RequirePermit(Permits.Read)
            .WithTags("Backlog")
            .WithSummary("One item, with its dispatch history")
            .WithDescription(
                "An item may be dispatched MORE THAN ONCE - a second team, or a re-run after a team "
                + "was deleted mid-spec. The CURRENT dispatch is the last, and the rollup is "
                + "against that one.\n\n"
                + "`teamGone` says the team a dispatch ran on no longer exists. The record keeps its "
                + "NAME regardless, which is the one field the message log cannot answer for.\n\n"
                + "The item's own `dispatchedTeam` is the CURRENT dispatch - the last of this list "
                + "- so a person who opened a row reads the same team the row showed.\n\n"
                + "`implementedBy` says who marked the item implemented - `by` (their email), `at`, "
                + "and `viaConcierge` when a Concierge wrote it on the person's word - while it is "
                + "implemented; null otherwise, or for an item marked before it was recorded.");

        app.MapPost("/api/backlog", async (
            CreateBacklogItem request, HttpContext context, IBacklogStore backlog,
            TeamRegistry teams, TeamAccess access, IUserStore users, TenantLogging audit,
            CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
            if (!MayReach(principal)) return RefuseKind();

            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { error = "An item needs a title." });
            }

            var admin = await access.MayAdministerBacklogAsync(principal, ct);
            var requested = string.IsNullOrWhiteSpace(request.Team) ? null : request.Team;

            // A MACHINE PRINCIPAL CANNOT CREATE AN UNLINKED ITEM, AND CANNOT LINK ONE TO A TEAM IT
            // DOES NOT REACH. The same defect from two directions, refused here in one place: an
            // unlinked item is the TENANT's, and creating one would be a credential writing into a
            // backlog it cannot otherwise see.
            //
            // REFUSED RATHER THAN SILENTLY LINKED TO SOMETHING. A create that quietly attached the
            // item to a team the caller happens to hold would be the platform choosing on their
            // behalf, which is the shape this codebase refuses for an Agent allowlist too.
            if (!admin)
            {
                if (requested is null)
                {
                    return Results.BadRequest(new
                    {
                        error = principal.Kind == PrincipalKind.TenantConcierge
                            ? "Choose a team for this item, or ask the person to create it."
                            : "Choose a team for this item. Only a person creates items that belong to no team.",
                    });
                }

                if (!await access.MayActOnAsync(principal, requested, ct))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }

            var stored = requested is null ? null : teams.ExistingName(requested) ?? requested;
            var actor = await ActorOfAsync(context, principal, users, ct);

            var item = await backlog.CreateAsync(
                stored, request.Title.Trim(), request.Body ?? "", actor, ct);

            await WriteAuditAsync(
                context, principal, users, audit, TenantActions.BacklogItemCreated, PlatformBacklogId.Format(item.Id),
                item.Title, new { item.Id, team = stored }, ct);

            return Results.Created($"/api/backlog/{item.Id}", Render(item, teams));
        })
            .RequirePermit(Permits.Read)
            .WithTags("Backlog")
            .WithSummary("Add an item")
            .WithDescription(
                "A person - or a tenant Concierge acting for one - creates items with NO team; "
                + "a machine principal must name a team it reaches, chosen from a pre-made team rather than "
                + "typed.\n\n"
                + "THE TEAM LINK IS A VISIBILITY LINK AND NOT A CONFIDENTIALITY BOUNDARY. "
                + "Dispatching an item to a team puts its title and body on that team's board and "
                + "lets its Manager read it, whatever this says.");

        app.MapPatch("/api/backlog/{id:long}", async (
            long id, UpdateBacklogItem request, HttpContext context, IBacklogStore backlog,
            TeamRegistry teams, TeamAccess access, IUserStore users, TenantLogging audit,
            CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
            if (!MayReach(principal)) return RefuseKind();

            var view = await ViewFor(principal, access, ct);

            if (await backlog.GetAsync(id, ct) is not { } item || !Visible(item, view))
            {
                return Results.NotFound(new { error = $"No backlog item {PlatformBacklogId.Format(id)}." });
            }

            // THE SECOND COPY OF `BacklogStates.IsLegal`, AND IT MOVES IN STEP WITH IT. The check is
            // the class's; this text is the only thing a person sees, so it is written from the same
            // constants rather than spelled out again.
            if (request.State is { } state && !BacklogStates.IsLegal(state))
            {
                return Results.BadRequest(new
                {
                    error =
                        $"An item is '{BacklogStates.Pending}', '{BacklogStates.Ready}', "
                        + $"'{BacklogStates.Declared}' or '{BacklogStates.Implemented}'.",
                });
            }

            // THE CONCIERGE MAY MARK AN ITEM READY, even though `ready` claims a person read the spec:
            // the person groups a wave and tells the Concierge which items to mark, so the
            // Concierge acts on the person's reading, with the person's authority, and the audit row
            // names it. A team's own credential never reaches this route (`MayReach`), so a Manager
            // still cannot clear its own drafts for dispatch.

            await backlog.UpdateAsync(id, request.Title, request.Body, request.State, ct);

            await WriteAuditAsync(
                context, principal, users, audit, TenantActions.BacklogItemEdited, PlatformBacklogId.Format(id),
                request.Title ?? item.Title, new { id, state = request.State }, ct);

            if (request.State == BacklogStates.Implemented && item.State != BacklogStates.Implemented)
            {
                await WriteAuditAsync(
                    context, principal, users, audit, TenantActions.BacklogItemImplemented, PlatformBacklogId.Format(id),
                    request.Title ?? item.Title,
                    new { id, from = item.State, viaConcierge = principal.Kind == PrincipalKind.TenantConcierge }, ct);
            }

            return Results.Ok(Render((await backlog.GetAsync(id, ct))!, teams));
        })
            .RequirePermit(Permits.Read)
            .WithTags("Backlog")
            .WithSummary("Edit an item")
            .WithDescription(
                "Absent fields are left alone. State is `pending`, `ready`, `declared` or "
                + "`implemented` and a PERSON is the authority on it: `workflow.completed` moves it "
                + "to `declared` once, and this is how they move it on or back.\n\n"
                + "`declared` means A MANAGER SAID IT WAS DELIVERED and nothing more. Nothing "
                + "automatic moves an item out of it - no sweeper, no timeout - so an item declared "
                + "and never landed reads that way until a person reopens it to `pending` or marks "
                + "it `implemented` themselves. `implemented` means THE WORK IS IN THE PRODUCT, and "
                + "only a person writes it: a value set by hand is never overwritten by "
                + "a later declaration.\n\n"
                + "`pending` means NOT REVIEWED - a draft nobody has read - and is where every item "
                + "starts. `ready` means somebody has read the body and judges that a Manager could "
                + "cut it into cards without follow-up questions, and it is the ONLY state that "
                + "dispatches. Nothing infers it: a person sets it, or a Concierge sets it when the "
                + "person tells it which items to mark. A team's credential cannot reach this route.");

        app.MapPost("/api/backlog/{id:long}/position", async (
            long id, MoveBacklogItem request, HttpContext context, IBacklogStore backlog,
            TeamAccess access, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
            if (!MayReach(principal)) return RefuseKind();

            var view = await ViewFor(principal, access, ct);

            if (await backlog.GetAsync(id, ct) is not { } item || !Visible(item, view))
            {
                return Results.NotFound(new { error = $"No backlog item {PlatformBacklogId.Format(id)}." });
            }

            var all = await backlog.ListAsync(null, includeUnlinked: true, archived: false, ct);

            if (Midpoint(all, request.After, request.Before) is { } position)
            {
                await backlog.SetPositionAsync(id, position, ct);
                return Results.NoContent();
            }

            // THE CEILING IS REAL AND IS HANDLED RATHER THAN DISCOVERED. Repeatedly halving the same
            // gap exhausts double precision after roughly fifty insertions in one place, and the
            // symptom is two items that can no longer be told apart and stop being re-orderable.
            // When the computed midpoint is not STRICTLY between its neighbours, renumber the whole
            // list to integers and retry ONCE - a bounded retry rather than a loop.
            var renumbered = await backlog.RenumberAsync(ct);

            if (Midpoint(renumbered, request.After, request.Before) is not { } retried)
            {
                return Results.Problem(
                    "Could not place this item between those two. Renumbering did not help, which "
                    + "should not be possible - report it.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            await backlog.SetPositionAsync(id, retried, ct);

            return Results.NoContent();
        })
            .RequirePermit(Permits.Read)
            .WithTags("Backlog")
            .WithSummary("Move an item in the order")
            .WithDescription(
                "`after` and `before` are the ids of the item's NEW NEIGHBOURS; either may be "
                + "absent for an end of the list. The item lands at their midpoint, so a reorder "
                + "writes ONE row rather than renumbering - which is what lets two people reorder "
                + "concurrently without each rewriting the rows the other touched.\n\n"
                + "Order is a property of the WHOLE backlog, never of a filtered view.");

        app.MapPost("/api/backlog/{id:long}/archive", async (
            long id, HttpContext context, IBacklogStore backlog, TeamAccess access,
            IUserStore users, IMessageLog log, TenantLogging audit, CancellationToken ct) =>
            await SetArchivedAsync(
                id, archived: true, context, backlog, access, users, log, audit, ct))
            .RequirePermit(Permits.Read)
            .WithTags("Backlog")
            .WithSummary("Archive an item")
            .WithDescription(
                "ARCHIVE IS THE NORMAL PATH AND DELETE IS THE EXCEPTION. Available at ANY state, "
                + "including `pending` - archiving and being implemented are two independent axes, "
                + "which is why archiving is not a third state value.\n\n"
                + "It also FREEZES the execution record. There is a second eraser besides team "
                + "deletion: log retention purges rows nothing cites, quietly, long after anybody "
                + "was watching. Archiving is already the moment a person says keep this for the "
                + "record, so it is the right moment to stop deriving and start storing.");

        app.MapPost("/api/backlog/{id:long}/restore", async (
            long id, HttpContext context, IBacklogStore backlog, TeamAccess access,
            IUserStore users, IMessageLog log, TenantLogging audit, CancellationToken ct) =>
            await SetArchivedAsync(
                id, archived: false, context, backlog, access, users, log, audit, ct))
            .RequirePermit(Permits.Read)
            .WithTags("Backlog")
            .WithSummary("Restore an archived item")
            .WithDescription(
                "Returns it to the backlog IN ITS POSITION rather than at an end, and un-freezes "
                + "the execution record so it goes back to deriving from the log.");

        app.MapDelete("/api/backlog/{id:long}", async (
            long id, HttpContext context, IBacklogStore backlog, TeamAccess access,
            IUserStore users, TenantLogging audit, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
            if (!MayReach(principal)) return RefuseKind();

            var view = await ViewFor(principal, access, ct);

            if (await backlog.GetAsync(id, ct) is not { } item || !Visible(item, view))
            {
                return Results.NotFound(new { error = $"No backlog item {PlatformBacklogId.Format(id)}." });
            }

            // DELETE IS REACHABLE ONLY FROM THE ARCHIVE, and the server enforces that rather than
            // trusting the screen to hide the button. ONE delete control that behaved differently
            // depending on whether the item had ever been dispatched would be the same control
            // doing two different things on state the person cannot see, which is the failure this
            // codebase refuses everywhere else. Archive-first answers the same worry.
            if (item.ArchivedAt is null)
            {
                return Results.Conflict(new
                {
                    error = "Archive this item first. Deleting is permanent and is offered from the archive only.",
                });
            }

            await backlog.DeleteAsync(id, ct);

            await WriteAuditAsync(
                context, principal, users, audit, TenantActions.BacklogItemDeleted, PlatformBacklogId.Format(id),
                item.Title, new { id }, ct);

            return Results.NoContent();
        })
            .RequirePermit(Permits.Read)
            .WithTags("Backlog")
            .WithSummary("Delete an archived item permanently")
            .WithDescription(
                "Permanent, and it takes the execution record with it.\n\n"
                + "THE CARDS OUTLIVE IT AND THAT IS ACCEPTED. A dispatched item's planned cards name "
                + "it as their parent on the APPEND-ONLY log, so they cannot be removed. A card "
                + "whose parent item is gone renders `B000H (deleted)` rather than a blank header.");
    }

    /// <summary>
    /// The three routes that DECLARE <c>{team}</c>, so <c>TeamGate</c> covers them structurally -
    /// the same declaration that lets a handler read a team is what puts it inside the gate.
    ///
    /// <para>
    /// That declaration is the whole access-control argument for the Manager's read: it gets the one
    /// item it was given and cannot enumerate or read the tenant backlog. There is deliberately no
    /// listing route under a team.
    /// </para>
    /// </summary>
    public static void MapTeamScoped(WebApplication app)
    {
        app.MapPost("/api/teams/{team}/backlog/{id:long}/dispatch", async (
            [Description(Describe.Team)] string team,
            long id,
            HttpContext context,
            IBacklogStore backlog,
            TeamRegistry teams,
            TeamAccess access,
            IUserStore users,
            ContainerHost host,
            IMessageLog log,
            TenantLogging audit,
            CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
            if (!MayReach(principal)) return RefuseKind();

            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            var view = await ViewFor(principal, access, ct);

            if (await backlog.GetAsync(id, ct) is not { } item || !Visible(item, view))
            {
                return Results.NotFound(new { error = $"No backlog item {PlatformBacklogId.Format(id)}." });
            }

            if (item.ArchivedAt is not null)
            {
                return Results.Conflict(new
                {
                    error = $"{PlatformBacklogId.Format(id)} is archived. Restore it before dispatching it.",
                });
            }

            if (RefuseUnlessReady(id, item) is { } notReady) return notReady;

            if (host.Find(new ContainerId(stored, TeamRegistry.DefaultManagerName)) is null)
            {
                return Results.Conflict(new
                {
                    error = $"Team '{teams.LabelFor(stored)}' has no Manager to hand this to.",
                });
            }

            var (correlation, dispatchId) = await DispatchIntoAsync(
                stored, item, context, principal, backlog, teams, users, log, audit, ct);

            return Results.Ok(new { correlation, dispatch = dispatchId });
        })
            .RequirePermit(Permits.Tell)
            .WithTags("Backlog")
            .WithSummary("Hand an item to a team")
            .WithDescription(
                "ONLY A `ready` ITEM IS DISPATCHED. Anything else is 409, naming the state the item "
                + "is in and how to move it - the same refusal `dispatch-to-new` gives, from one "
                + "place, because a boundary a route call can bypass is not a boundary. `ready` "
                + "means a PERSON read the body; the state SURVIVES the dispatch.\n\n"
                + "Writes a `backlog.item.dispatched` row, tells that team's Manager CAUSED BY it, and "
                + "records the dispatch. The row's seq is the workflow's correlation root.\n\n"
                + "DISPATCHING DISCLOSES THE ITEM to the team it ran on - the planned cards carry "
                + "its title and body onto that board and the Manager reads the item itself - "
                + "whatever the item's team link says. The link governs one screen and is not a "
                + "confidentiality boundary.\n\n"
                + "An item may be dispatched MORE THAN ONCE, which starts a second workflow. The "
                + "link is NOT rewritten: a link that moved with execution would be worse than one "
                + "that never claimed to track it.");

        // DISPATCHING INTO A TEAM THAT DOES NOT EXIST YET, BUILT FROM THE SAME REMEMBERED DEFAULTS
        // THE NEW TEAM DIALOG USES.
        //
        // NO `{team}` IN THE TEMPLATE, so - like every other route in this file - it asks the gate's
        // own questions ITSELF, IN THE GATE'S ORDER: person first, effective set second. That
        // order is not a style choice; see this class's summary for what reversing it does.
        //
        // IT DOES NOT CLONE A SOURCE TEAM. Cloning is the wrong DEFAULT for starting a spec: it
        // needs a team to already exist - unusable on an instance with none, which is exactly
        // where somebody is most likely to be starting one - and it inherits the source's ROSTER,
        // so a team assembled for one job arrives staffed for another. The remembered
        // defaults are the same set `POST /api/teams` already takes, so the two paths make the same
        // kind of team.
        //
        // TWO CAPABILITIES, ONE MARKER, AND THE SECOND IS ASSERTED IN THE HANDLER. This creates a
        // team AND dispatches into it, so it needs `CreateTeam` and `Tell`. `RouteTableTests` allows
        // exactly one marker per route, so the stronger is declared and the other checked below -
        // the shape the progress route uses when its permit cannot express the whole question.
        app.MapPost("/api/backlog/{id:long}/dispatch-to-new", async (
            long id,
            NewTeamDispatch request,
            HttpContext context,
            IBacklogStore backlog,
            TeamRegistry teams,
            TeamAccess access,
            IUserStore users,
            IMessageLog log,
            TenantLogging audit,
            TeamRepoSetup repoSetup,
            CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
            if (!MayReach(principal)) return RefuseKind();

            // THE SECOND PERMIT. Bounds machine principals only, exactly as `PermitGate` does - a
            // person reaches every team, which `MayReach` and the checks below settle.
            //
            // AND IT REFUSES IN `PermitGate`'S OWN WORDS, because it is asking `PermitGate`'s own
            // question a route later. A bare 403 here would be indistinguishable from the kind
            // refusal above it, which is a different problem with a different answer.
            if (principal.Kind is not PrincipalKind.User
                && !principal.Permits.Contains(Permits.Tell, StringComparer.Ordinal))
            {
                return Results.Json(
                    new { error = PermitGate.Missing(Permits.Tell) },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var view = await ViewFor(principal, access, ct);

            if (await backlog.GetAsync(id, ct) is not { } item || !Visible(item, view))
            {
                return Results.NotFound(new { error = $"No backlog item {PlatformBacklogId.Format(id)}." });
            }

            if (item.ArchivedAt is not null)
            {
                return Results.Conflict(new
                {
                    error = $"{PlatformBacklogId.Format(id)} is archived. Restore it before dispatching it.",
                });
            }

            // ASKED BEFORE THE TEAM IS MADE, exactly as the archived refusal above is: this route
            // creates AND dispatches, so a gate asked after the create would leave a team behind
            // every time it refused.
            if (RefuseUnlessReady(id, item) is { } notReady) return notReady;

            // THE NAME IS REQUIRED AND IS NOT DERIVED HERE. `newTeamNameFor` in `lib/backlog.ts`
            // owns the default, because the dialog has to PREFILL the field for a person to edit
            // before committing. A second derivation behind this route would be two stores of one
            // fact. What this end owns is whether the name it was handed is LEGAL.
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new
                {
                    error = "A new team needs a name. The screen derives one from the item's title.",
                });
            }

            if (!ContainerId.IsLegalName(request.Name))
            {
                return Results.BadRequest(new
                {
                    error =
                        $"'{request.Name}' cannot be a team name. Letters, digits, '-' and '_', "
                        + $"starting with a letter or digit, at most {ContainerId.MaxNameLength} "
                        + "characters.",
                });
            }

            // THE SETTINGS ARE THE CALLER'S, exactly as `POST /api/teams` takes them - the screen
            // reads them from the same remembered values the New Team dialog does. NULL IS PASSED
            // THROUGH RATHER THAN SUBSTITUTED: `CreateAsync` refuses a blank Agent BY
            // NAME, because null means nobody has chosen, and a dispatch that quietly filled one in
            // would manufacture a reference nothing wrote down.
            if (string.IsNullOrWhiteSpace(request.Agent))
            {
                return Results.BadRequest(new
                {
                    error =
                        "A new team needs a Manager Agent. Create a team once so the screen can "
                        + "remember your choices, or dispatch to a team that already exists.",
                });
            }

            // THE SAME REPOSITORY PATH AS `POST /api/teams` (B001F), asked before the team is made:
            // every URL read with `git ls-remote`, one that cannot be read refused with the caller's
            // choices, a team left with no repository given its local one - and an agent never let
            // create on GitHub or attach anyway. A refusal here leaves no team, row, folder or clone.
            NewTeamRepos newRepos;
            try
            {
                newRepos = await repoSetup.PlanNewTeamAsync(
                    request.Repos, upstreams: null, request.RepoChoices,
                    person: principal.Kind is PrincipalKind.User, request.LocalRepository, ct);
            }
            catch (RepoSetupRefusedException refused)
            {
                return Results.Json(refused.Body, statusCode: refused.Status);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            TeamSummary created;
            string? repoSetupInstruction = null;
            bool repoSetupFailed = false;
            var teamCreated = false;

            try
            {
                created = await teams.CreateAsync(
                    request.Name,
                    request.Agent,
                    additionalInstructions: request.AdditionalInstructions,
                    memberAgent: null,
                    memberAgents: request.MemberAgents,
                    root: request.Root,
                    repos: newRepos.Repos,
                    ct: ct,
                    handleRepoSetup: outcomes =>
                    {
                        // Choose a standalone failure wake and stop the dispatch: folding failure
                        // behind the spec hides the need for a person and invites endless retries.
                        // Returning false preserves the complete repo failure instruction.
                        repoSetupFailed = outcomes.Any(o => o.Result is RepoCloneResult.Failed);
                        if (repoSetupFailed) return false;
                        repoSetupInstruction = RepoSetupMessage.For(outcomes);
                        return true;
                    },

                    // ONE UNIT WITH THE TEAM, as on `POST /api/teams`: a repository that cannot be
                    // made refuses the create, naming why.
                    addRepo: newRepos.AddRepoAsync);
                teamCreated = true;
                await newRepos.LogAsync(audit, context, created.Id, ct);
            }
            catch (RepoSetupRefusedException refused)
            {
                return Results.Json(refused.Body, statusCode: refused.Status);
            }
            catch (TeamNameTakenException)
            {
                return Results.Conflict(new { error = $"A team called '{request.Name}' already exists." });
            }
            catch (ArgumentException ex)
            {
                // `CreateAsync` refuses a missing Agent by name, an illegal root, and a
                // repo URL it cannot parse. All of those are answerable by the caller, so 400.
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                await newRepos.ForgetUnlessCreatedAsync(teamCreated);
            }

            if (repoSetupFailed)
            {
                // Keep the created team visible for recovery; never roll creation back.
                return Results.Conflict(new
                {
                    team = created.Id,
                    teamName = created.Name,
                    error = "Repository setup failed. The team was created and its Manager notified, "
                        + "but the item was not dispatched. A person must resolve repository setup before dispatching to this team.",
                });
            }

            // THE TEAM IS NOT REMOVED IF THE DISPATCH FAILS, AND THAT IS THE RIGHT DIRECTION. An
            // empty team is visible on the Teams tab and a person can delete it; a create that undid
            // itself would be the destructive direction, and deletion is this product's most
            // destructive operation. What must not happen is the reverse order - telling a Manager
            // that does not exist yet - and that is what this is.
            var (correlation, dispatchId) = await DispatchIntoAsync(
                created.Id, item, context, principal, backlog, teams, users, log, audit, ct,
                repoSetupInstruction);

            return Results.Ok(new
            {
                team = created.Id,
                teamName = created.Name,
                correlation,
                dispatch = dispatchId,
                localRepository = newRepos.LocalRepository is { } local
                    ? new { name = local.Name, reference = local.Reference, created = local.Created }
                    : null,
                createdOnGitHub = newRepos.CreatedOnGitHub.Count == 0 ? null : newRepos.CreatedOnGitHub,
            });
        })
            .RequirePermit(Permits.CreateTeam)
            .WithTags("Backlog")
            .WithSummary("Hand an item to a team created for it")
            .WithDescription(
                "ONLY A `ready` ITEM IS DISPATCHED, asked BEFORE the team is made and in the same "
                + "words the team route uses - one gate, two doors, because a boundary a route call "
                + "can bypass is not a boundary. A refusal here leaves no team behind.\n\n"
                + "Creates a team from the settings in the body and dispatches the item into it, in "
                + "ONE request - so there is no window where the team exists and the item never "
                + "reached it.\n\n"
                + "The settings are the same ones `POST /api/teams` takes, and the screen fills them "
                + "from the values it remembers from the last team you made. Nothing is defaulted "
                + "here: a missing Agent is refused BY NAME, because null means nobody has "
                + "chosen rather than 'pick something sensible'.\n\n"
                + "The NAME is required and is not derived here. The screen derives a default from "
                + "the item's title so a person can edit it before committing.\n\n"
                + "**Repositories (B001F), exactly as `POST /api/teams`.** Every URL in `repos` is read with "
                + "`git ls-remote` before the team is made; one that cannot be read is refused with 422 "
                + "`{ error, code: \"repo-check-failed\", repos: [{ url, failure, reason, choices }] }` and no team "
                + "is created or item dispatched. Answer it with `repoChoices`; an agent is offered `use-local` "
                + "only and refused `create-on-github` and `attach-anyway` with 403. A team left with no "
                + "repository gets `local:<team id>` unless `localRepository` is false, reported as "
                + "`localRepository`; when it cannot be made, no team is created.\n\n"
                + "If the dispatch fails after the team is made, THE TEAM IS LEFT. An empty team is "
                + "visible and deletable; a create that undid itself would be the destructive "
                + "direction.");

        app.MapGet("/api/teams/{team}/backlog/{id:long}", async (
            [Description(Describe.Team)] string team,
            long id,
            IBacklogStore backlog,
            TeamRegistry teams,
            ContainerHost host,
            IMessageLog log,
            KanbanStore kanban,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            if (await backlog.GetAsync(id, ct) is not { } item)
            {
                return Results.NotFound(new { error = $"No backlog item {PlatformBacklogId.Format(id)}." });
            }

            // THE ITEM ONLY IF IT WAS DISPATCHED TO THIS TEAM. A Manager reads what it was GIVEN;
            // it cannot read an item somebody else's team is working on, and there is no listing
            // route under a team for it to enumerate with.
            var dispatches = await backlog.DispatchesAsync(id, ct);

            if (!dispatches.Any(d => string.Equals(d.TeamId, stored, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.NotFound(new { error = $"{PlatformBacklogId.Format(id)} was not dispatched to this team." });
            }

            // THE OPEN WORKFLOW OF THIS TEAM'S LATEST DISPATCH, so a reader continues the workflow the
            // work belongs to rather than working it out from the log. Null once it has ended.
            var ours = dispatches.Last(d => string.Equals(d.TeamId, stored, StringComparison.OrdinalIgnoreCase));
            var inFlight = await BacklogInFlightState.ForAsync([ours], teams, host, log, ct);
            var stranded = await BacklogStrandedState.ForAsync(inFlight, teams, log, kanban, ct);

            return Results.Ok(new
            {
                item.Id,
                item.Title,
                item.Body,
                item.State,
                workflow = inFlight.GetValueOrDefault(id)?.Correlation,
                stranded = stranded.GetValueOrDefault(id),
            });
        })
            .RequirePermit(Permits.Read)
            .WithTags("Backlog")
            .WithSummary("Read the item this team was given")
            .WithDescription(
                "A Manager reads its item THROUGH THE PLATFORM rather than through a copy pushed "
                + "into its instruction: one store of record means one copy, and a copy cannot go "
                + "stale.\n\n"
                + "`{team}` in the route, so TeamGate covers this structurally - that declaration is "
                + "the whole access-control argument. An item not dispatched to this team is a 404.\n\n"
                + "`workflow` is the open workflow of this team's latest dispatch of the item, null "
                + "once it has ended. `stranded` is as on `GET /api/backlog`: the workflow is Blocked "
                + "or Failed and its work continued in a later one, and only a person may close it.");

        app.MapPost("/api/teams/{team}/kanban/plan", async (
            [Description(Describe.Team)] string team,
            PlanCard request,
            HttpContext context,
            TeamRegistry teams,
            IMessageLog log,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { error = "A card needs a title." });
            }

            // AUTHORITY IS IDENTITY, NOT THE PERMIT - the pattern `workflow.completed` already uses
            // and chosen here for the same reason: a separate permit would need every member's
            // permit column migrated to carry it, and every member already holds `Progress`.
            //
            // A PERSON MAY ALSO PLAN, which is why this asks only that a CONTAINER caller be the
            // Manager. A person cutting an item up by hand is an ordinary thing to want, and
            // refusing it would make the screen able to show cards it could not create.
            if (PrincipalClaims.From(context.User) is { Kind: PrincipalKind.Container } caller
                && !ContainerId.Parse(caller.Id)
                    .Equals(new ContainerId(stored, TeamRegistry.DefaultManagerName)))
            {
                return Results.Json(
                    new { error = "Only this team's Manager plans cards." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // `team` FROM THE ROUTE VALUE THE GATE ALREADY CHECKED, never from the payload.
            // `MessageTeam.Of` reads a card row's team out of its PAYLOAD, and that is safe only
            // because every writer of one is a route that takes it from something the server built.
            // A caller able to name the team here would defeat the delivery pump's team guard.
            var row = await log.AppendAsync(
                new NewMessage(
                    MessageTypes.KanbanCardPlanned,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        team = stored,
                        item = request.Item,
                        title = request.Title.Trim(),
                        body = request.Body ?? "",
                    }),
                    PrincipalClaims.From(context.User)?.Id ?? "console",
                    request.Causation),
                ct);

            // THE CARD'S ID IS THIS ROW'S SEQ, which is why it is answered here: the caller needs it
            // to pass to `tell --card`, and deriving it anywhere else would be a second store of the
            // same fact.
            return Results.Ok(new { card = row.Seq.ToString(), row.CorrelationId });
        })
            .RequirePermit(Permits.Progress)
            .WithTags("Kanban")
            .WithSummary("Plan a card nobody is on yet")
            .WithDescription(
                "Writes a Todo card with NO member, so a Manager can cut an item into pieces before "
                + "it has anybody to do them - the one thing the card model could not represent.\n\n"
                + "The card's id is the seq of the row that planned it. Pass it to "
                + "`tell --card <id>` to claim it for a member.\n\n"
                + "AUTHORITY IS IDENTITY: a container caller must be this team's Manager. The permit "
                + "is `Progress`, which every member already holds, so no permit column is migrated.");
    }

    /// <summary>
    /// ONLY A `ready` ITEM IS DISPATCHED, AND BOTH DOORS ASK IT FROM HERE.
    ///
    /// <para>
    /// A boundary a route call can bypass is not a boundary, so the gate is ONE method rather than
    /// the same words written twice - the rule <c>CreateAsync</c> already follows by re-asking the
    /// allowlist itself. <c>null</c> means dispatch, the way the archived check above reads.
    /// </para>
    ///
    /// <para>
    /// THE REFUSAL NAMES THE STATE THE ITEM IS ACTUALLY IN AND HOW TO MOVE IT, which is the archived
    /// refusal's shape: an item's body costs a whole Manager run to dispatch, so a person who is
    /// refused needs to know what is true now and where they go next, not that something failed.
    /// </para>
    /// </summary>
    private static IResult? RefuseUnlessReady(long id, BacklogItem item) =>
        string.Equals(item.State, BacklogStates.Ready, StringComparison.Ordinal)
            ? null
            : Results.Conflict(new
            {
                error =
                    $"{PlatformBacklogId.Format(id)} is '{item.State}', and only a "
                    + $"'{BacklogStates.Ready}' item is dispatched. A Manager cuts the body into "
                    + "cards, so somebody reads it first: mark it ready on the Backlog screen, or "
                    + $"with the `backlog` tool (action edit, id {PlatformBacklogId.Format(id)}, "
                    + $"state {BacklogStates.Ready}).",
            });

    /// <summary>
    /// Archive and restore are one operation with a flag, deliberately: they are exact inverses and
    /// two copies of the visibility check is two places for it to drift.
    /// </summary>
    private static async Task<IResult> SetArchivedAsync(
        long id,
        bool archived,
        HttpContext context,
        IBacklogStore backlog,
        TeamAccess access,
        IUserStore users,
        IMessageLog log,
        TenantLogging audit,
        CancellationToken ct)
    {
        if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();
        if (!MayReach(principal)) return RefuseKind();

        var view = await ViewFor(principal, access, ct);

        if (await backlog.GetAsync(id, ct) is not { } item || !Visible(item, view))
        {
            return Results.NotFound(new { error = $"No backlog item {PlatformBacklogId.Format(id)}." });
        }

        await backlog.SetArchivedAsync(
            id, archived ? DateTimeOffset.UtcNow.ToString("O") : null, ct);

        // FREEZING IS THE SECOND HALF OF ARCHIVING AND NOT A SEPARATE FEATURE. Team deletion is not
        // the only eraser: log retention purges rows nothing cites, so an archived item deriving
        // its history from the log would quietly lose it. Restoring thaws, and re-archiving freezes
        // again from whatever the log still holds.
        await BacklogExecutionRecord.FreezeAsync(id, archived, backlog, log, ct);

        await WriteAuditAsync(
            context,
            principal,
            users,
            audit,
            archived ? TenantActions.BacklogItemArchived : TenantActions.BacklogItemRestored,
            PlatformBacklogId.Format(id),
            item.Title,
            new { id },
            ct);

        return Results.NoContent();
    }

    /// <summary>
    /// THE GATE'S OWN QUESTIONS, IN THE GATE'S ORDER: person first, effective set second.
    /// Reversed, a person gets 403 for a team that does not exist, and after a restart every
    /// team is briefly "not there" for everybody.
    /// </summary>
    private static async Task<BacklogView> ViewFor(
        Principal principal, TeamAccess access, CancellationToken ct)
    {
        if (await access.MayAdministerBacklogAsync(principal, ct)) return new BacklogView(null, true);

        return new BacklogView(await access.EffectiveTeamsAsync(principal, ct), false);
    }

    private static bool Visible(BacklogItem item, BacklogView view) =>
        view.Teams is null
        || (item.Team is null ? view.Unlinked : view.Teams.Contains(item.Team));

    /// <summary>
    /// The position strictly between two neighbours, or null when there is no such double left -
    /// which is the caller's signal to renumber and try once more.
    /// </summary>
    private static double? Midpoint(IReadOnlyList<BacklogItem> all, long? after, long? before)
    {
        var lower = after is { } a ? all.FirstOrDefault(i => i.Id == a)?.Position : null;
        var upper = before is { } b ? all.FirstOrDefault(i => i.Id == b)?.Position : null;

        if (lower is null && upper is null)
        {
            return all.Count == 0 ? 1024d : all[0].Position - 1024d;
        }

        if (lower is null) return upper!.Value - 1024d;
        if (upper is null) return lower.Value + 1024d;

        var midpoint = lower.Value + ((upper.Value - lower.Value) / 2);

        // STRICTLY between, which is the whole check. `>` and `<` rather than a tolerance: two
        // adjacent doubles average to one of themselves, and that is exactly the exhausted state.
        return midpoint > lower.Value && midpoint < upper.Value ? midpoint : null;
    }

    /// <summary>
    /// The email that names the PERSON behind a backlog write.
    ///
    /// A Concierge ticket carries no email claim: ApiKeyAuthenticationHandler rebuilds the ticket
    /// from the resolved principal and PrincipalClaims.ToClaimsPrincipal emits only the owner id for
    /// a machine principal. That keeps the claim set small and fresh, but a permanent backlog row
    /// or tenant event must still name the person rather than `concierge-...`, so the owner is
    /// resolved here when needed.
    /// </summary>
    private static async Task<string?> ActorEmailOfAsync(
        HttpContext context, Principal principal, IUserStore users, CancellationToken ct)
    {
        var claim = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        if (!string.IsNullOrWhiteSpace(claim)) return claim;

        if (principal.OwnerUserId is { } owner)
        {
            return (await users.FindByIdAsync(owner, ct))?.Email;
        }

        return null;
    }

    /// <summary>
    /// Who did it, for a denormalised `created_by`. The ticket's claim is first when it exists; a
    /// Concierge falls back to its owner's email so the stored actor keeps naming the person.
    /// </summary>
    /// <summary>Whether a <c>backlog.item-implemented</c> row was written by a Concierge acting on
    /// the person's word. A row whose detail cannot be read says no.</summary>
    private static bool ViaConcierge(string? detail)
    {
        if (detail is null) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detail);
            return doc.RootElement.TryGetProperty("viaConcierge", out var via) && via.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static async Task<string> ActorOfAsync(
        HttpContext context, Principal principal, IUserStore users, CancellationToken ct) =>
        await ActorEmailOfAsync(context, principal, users, ct)
        ?? context.User.Identity?.Name
        ?? principal.Id;

    private static async Task WriteAuditAsync(
        HttpContext context,
        Principal principal,
        IUserStore users,
        TenantLogging audit,
        string action,
        string? subject,
        string? subjectName,
        object? detail,
        CancellationToken ct) =>
        await audit.WriteAsAsync(
            principal.Id,
            await ActorEmailOfAsync(context, principal, users, ct),
            action,
            subject,
            subjectName,
            detail,
            ct);

    /// <summary>
    /// HANDING ONE ITEM TO ONE TEAM'S MANAGER - the four writes that make a dispatch, in the one
    /// order that makes them a workflow.
    ///
    /// <para>
    /// EXTRACTED SO TWO ROUTES CANNOT DRIFT. Dispatching to an existing team and dispatching into a
    /// team created for the purpose differ only in where the team came from; everything after that
    /// is identical, and a second copy of this would be two answers to "what is a dispatch" waiting
    /// to disagree about correlation, about what the Manager is told, or about what is audited.
    /// </para>
    ///
    /// <para>
    /// THE FIRST ROW IS THE CORRELATION ROOT, so a dispatch IS a workflow and everything downstream
    /// - cards, spend, the budget bound, the completion refusal - keys on it unchanged. Nothing new
    /// is invented for correlation: a row with NO causation self-references, which is exactly what a
    /// head needs. The Manager is then told CAUSED BY that row, which is what puts the instruction
    /// and everything it leads to inside the dispatch's own workflow rather than at the head of a
    /// new one.
    /// </para>
    /// </summary>
    private static async Task<(long Correlation, long Dispatch)> DispatchIntoAsync(
        string stored,
        BacklogItem item,
        HttpContext context,
        Principal principal,
        IBacklogStore backlog,
        TeamRegistry teams,
        IUserStore users,
        IMessageLog log,
        TenantLogging audit,
        CancellationToken ct,
        string? repoSetupInstruction = null)
    {
        var actor = await ActorOfAsync(context, principal, users, ct);

        var dispatched = await log.AppendAsync(
            new NewMessage(
                MessageTypes.BacklogItemDispatched,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    item = item.Id,
                    team = stored,
                    title = item.Title,
                }),
                actor),
            ct);

        var record = await backlog.AddDispatchAsync(
            item.Id, stored, teams.LabelFor(stored), dispatched.Seq, actor, ct);

        var manager = new ContainerId(stored, TeamRegistry.DefaultManagerName);

        await log.AppendAsync(
            new NewMessage(
                MessageTypes.InstructionFor(manager),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    instruction =
                        $"Backlog item {PlatformBacklogId.Format(item.Id)} has been dispatched to your team: {item.Title}"
                        + "\n\n"
                        + $"Read it with the `backlog` tool (action show, id {item.Id}, team {stored}), "
                        + "cut it into cards with the `kanban` tool (action plan), then `tell` each "
                        + "member with the card id. Your role skill has the whole sequence."
                        + (repoSetupInstruction is null ? "" : "\n\n" + repoSetupInstruction),
                    subject = $"{PlatformBacklogId.Format(item.Id)}: {item.Title}",
                    body = "",
                }),
                actor,
                dispatched.Seq),
            ct);

        await WriteAuditAsync(
            context, principal, users, audit, TenantActions.BacklogItemDispatched, PlatformBacklogId.Format(item.Id),
            item.Title, new { item = item.Id, team = stored, correlation = dispatched.Seq }, ct);

        return (dispatched.Seq, record.Id);
    }

    /// <summary>
    /// What one item looks like on the wire.
    ///
    /// <para>
    /// A TEAM LINK IS NEVER RE-CHECKED AGAINST WHETHER THAT TEAM STILL EXISTS, in the same spirit as
    /// a stored team root never being re-checked against the allowlist. The item survives its team,
    /// so a linked item whose team is gone is reachable by a person only - and the payload
    /// SAYS the team is gone rather than showing a bare id. Silently promoting it to everyone, or
    /// silently hiding it, are both worse: one widens access nobody decided on, the other loses work.
    /// </para>
    /// </summary>
    /// <param name="inFlight">
    /// Where the item is being worked, or null. THE TWO READS COMPUTE IT and the writes do not: a
    /// create has no dispatch to speak of, and an edit's response answering null without asking
    /// is harmless because the screen re-reads the list after every write. Asking on every write
    /// would put two log queries on paths that need none.
    /// </param>
    /// <param name="dispatched">
    /// THE CURRENT DISPATCH, OPEN OR CLOSED - what the screen's Team column reads, and the reason
    /// it cannot read <paramref name="inFlight"/> instead.
    ///
    /// <para>
    /// The two are DIFFERENT FACTS and stay different fields. `inFlight` is dropped the moment the
    /// workflow closes, which is correct for "being worked right now" and useless for "was handed
    /// to" - and most dispatched work is finished work, so a column read from it would be blank for
    /// nearly every item that had ever been dispatched. Folding them into one field is the same mistake as
    /// folding either into the team LINK, which says who may SEE the item.
    /// </para>
    ///
    /// <para>
    /// COSTS NOTHING. Both readers already hold this record: the list reads every shown item's
    /// latest dispatch in one query for `inFlight`, and the detail reads the whole history and
    /// takes the last of it. The writes pass null for the same reason they pass no `inFlight`.
    /// </para>
    /// </param>
    /// <param name="landed">
    /// WHETHER THE WORK REACHED <c>main</c>, or null.
    ///
    /// <para>
    /// COMPUTED BY THE TWO READS AND BY NEITHER WRITE, on exactly the reasoning `inFlight` gives
    /// above and with more force: this one shells out to git, and putting that on a create or an
    /// edit would make every keystroke on the Backlog screen spawn processes for an answer the
    /// following list read recomputes anyway.
    /// </para>
    ///
    /// <para>
    /// IT IS NOT THE ITEM'S STATE AND IT IS NOT A REPLACEMENT FOR ONE. `state` is what a person -
    /// or a Manager's declaration - says about the item; this is what the disk says about the work.
    /// Those are two facts, and the screen shows both.
    /// </para>
    /// </param>
    private static object Render(
        BacklogItem item,
        TeamRegistry teams,
        BacklogInFlight? inFlight = null,
        BacklogDispatch? dispatched = null,
        BacklogLanded? landed = null,
        BacklogStranded? stranded = null) => new
    {
        item.Id,
        item.Team,
        teamName = item.Team is null ? null : teams.ExistingName(item.Team) is { } stored
            ? teams.LabelFor(stored)
            : null,
        teamGone = item.Team is not null && teams.ExistingName(item.Team) is null,
        item.Title,
        item.Body,
        item.State,
        item.ArchivedAt,
        item.CreatedAt,
        item.UpdatedAt,
        item.CreatedBy,
        inFlight,

        // THE OPEN WORKFLOW LEFT BEHIND, when its work finished in a later one. Only ever set beside
        // an `inFlight`, and only ever an offer - see BacklogStranded.
        stranded,

        // BESIDE `inFlight`, NOT INSTEAD OF IT, and the pair is the point. `inFlight` says the work
        // is happening; this says where the work ENDED UP. An item can be neither, either or both:
        // finished and landed, finished and sitting unpushed on one disk - which is the state the
        // Backlog once rendered as `implemented` - or in flight with nothing on a remote yet.
        landed,

        dispatchedTeam = dispatched?.TeamId,

        // THE CURRENT LABEL WHERE THERE IS ONE, exactly as `teamName` above resolves it, so one
        // team reads under one name everywhere on the screen. Where the team is GONE it falls
        // back to the name the dispatch record kept - the one field the log cannot answer for,
        // and the same name the detail's history has always shown - because a dispatch to a
        // deleted team still happened and a bare id is not what it says on the record.
        dispatchedTeamName = dispatched is null
            ? null
            : teams.ExistingName(dispatched.TeamId) is { } dispatchedTo
                ? teams.LabelFor(dispatchedTo)
                : dispatched.TeamName,

        // AND IT IS MARKED GONE the way every other gone team on this wire is marked - the item's
        // own `teamGone` above, and each row of the detail's history. Reported, not dropped:
        // `inFlight` drops it because a team that is gone is working nothing, which is a different
        // question from where the item went.
        dispatchedTeamGone = dispatched is not null && teams.ExistingName(dispatched.TeamId) is null,
    };
}

/// <param name="Team">
/// Absent or empty means unlinked, which only a person - or a tenant Concierge acting for one -
/// may ask for. It is a team a person CHOSE from the ones that exist, never free text.
/// </param>
/// <summary>The one field dispatch-to-a-new-team needs that dispatch-to-an-existing-one does not.</summary>
/// <summary>
/// A team to make, and the item to hand it. The settings are exactly those <see cref="CreateTeam"/>
/// takes - the screen fills them from the values it remembers from the last team a person made, so
/// the two paths produce the same kind of team.
/// </summary>
internal sealed record NewTeamDispatch(
    [property: Description("The new team's name. Derived from the item's title by the screen, and editable there.")]
    string? Name = null,
    [property: Description("The Manager's Agent. Refused when absent - null means nobody has chosen.")]
    string? Agent = null,
    [property: Description(
        "Optional instructions from the person for this team, appended after the built-in role "
        + "prompt for its Manager and members.")]
    string? AdditionalInstructions = null,
    [property: Description("The ordered allowlist of Agents a hire may run.")]
    IReadOnlyList<string>? MemberAgents = null,
    [property: Description("Where the team's folder goes. Null is under the instance root.")]
    string? Root = null,
    [property: Description(
        "Repositories the platform clones for the team. Each URL is read with `git ls-remote` first, "
        + "exactly as on `POST /api/teams`.")]
    IReadOnlyList<string>? Repos = null,
    [property: Description(
        "Whether a team left with no repository gets a local repository named after it, attached as "
        + "`local:<team id>`. Omitted means true. Ignored when `repos` names any repository.")]
    bool? LocalRepository = null,
    [property: Description(
        "What to do with a URL in `repos` that `git ls-remote` could not read, keyed by that URL: "
        + "`use-local`, `create-on-github` (a person only; github.com only) or `attach-anyway` (a person "
        + "only; a network failure only). A URL with no choice that cannot be read refuses the dispatch "
        + "and no team is created.")]
    IReadOnlyDictionary<string, string>? RepoChoices = null);

internal sealed record CreateBacklogItem(
    [property: Description("What the work is, in a line.")] string Title,
    [property: Description("The spec itself, as markdown. Harness is the store of record for it.")] string? Body = null,
    [property: Description("The team this item is visible to. A person may omit it.")] string? Team = null);

/// <summary>Absent fields are left alone - see <see cref="IBacklogStore.UpdateAsync"/>.</summary>
internal sealed record UpdateBacklogItem(
    [property: Description("Absent leaves it alone.")] string? Title = null,
    [property: Description("Absent leaves it alone.")] string? Body = null,
    [property: Description("`pending`, `ready`, `declared` or `implemented`. Absent leaves it alone.")]
    string? State = null);

/// <param name="After">The id of the item this one now sits BELOW, or absent for the top.</param>
/// <param name="Before">The id of the item this one now sits ABOVE, or absent for the bottom.</param>
internal sealed record MoveBacklogItem(long? After = null, long? Before = null);

/// <param name="Item">The backlog item this card is a piece of. Null for a card that belongs to no
/// item, which a person planning by hand may want.</param>
/// <param name="Causation">
/// The seq of the message the planner is answering, so the card joins that workflow rather than
/// heading a new one. A Manager sends it from `HARNESS_CAUSATION`; a person omits it.
/// </param>
internal sealed record PlanCard(
    [property: Description("What the piece of work is, in a line.")] string Title,
    [property: Description("The rest of it. Empty rather than absent when there is none.")] string? Body = null,
    [property: Description("The backlog item this card is a piece of.")] long? Item = null,
    long? Causation = null);
