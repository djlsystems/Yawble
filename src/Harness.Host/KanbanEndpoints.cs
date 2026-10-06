using System.ComponentModel;
using System.Text.Json;
using Harness.Contracts;
using Harness.Kanban;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// HTTP endpoints for the Kanban board API.
/// </summary>
public static class KanbanEndpoints
{
    public static void Map(WebApplication app)
    {
        // THE TENANT-WIDE BOARD, AND THE ONE ROUTE HERE THAT DECLARES NO `{team}`.
        //
        // Everything below is addressed under a team and gated by that declaration, which is
        // correct and is why those routes take no `team=` query parameter. But a per-team address
        // cannot represent the board's own TEAM FILTER: choosing a different team in the filter bar
        // would change the query string and nothing on screen. The board is a per-TENANT view of
        // the work, so it has a per-tenant route.
        //
        // So this follows `/api/overview` and `/api/teams/rollup` instead: no `{team}`, therefore
        // outside `TeamGate`, therefore it asks the authority question itself - `PrincipalClaims`,
        // then `TeamAccess.EffectiveTeamsAsync`, then a projection per team in that set. There is
        // no second branch for a person here, and there must not be one: the effective set
        // already answers "everything that exists" for a person, and a route naming no team
        // cannot hit the ordering trap `/api/me/current-team` has to be careful about.
        //
        // WITH NO PATH SEGMENT TO CONTRADICT IT, `team` IS AN HONEST NARROWING FILTER. Absent means
        // every team the caller reaches; present means that one; and a team they do not reach is
        // simply not in the union - NO CARDS, never a 403. A refusal there would answer "does this
        // team exist" to somebody not entitled to ask.
        app.MapGet("/api/kanban/board", GetTenantBoardAsync)
            .RequirePermit(Permits.Read)
            .WithTags("Kanban")
            .WithSummary("Get the board across every team the caller reaches")
            .WithDescription(
                "The board as a per-TENANT view: one payload carrying the cards of every team in "
                + "the caller's effective set.\n\n"
                + "`team` NARROWS this to one team and never widens it. A team the caller does not "
                + "reach contributes no cards and is not refused - the filter bounds what they may "
                + "already see, and a refusal would say which teams exist.\n\n"
                + "FOUR QUERY PARAMETERS. What narrows a board "
                + "here is `team`, `member`, `status` and `outcome` (see `KanbanFilter`) - the last "
                + "an outcome's id, or `none`, matched against the `outcome` each card carries; searching a card's "
                + "CONTENTS is free text over the fetched board and reaches no route.\n\n"
                + "The per-team route `/api/teams/{team}/kanban/board` is what the CLI and a "
                + "container use; this is what the console's board reads.");

        // EVERY OTHER ROUTE DECLARES `{team}`, AND THAT DECLARATION IS THE SECURITY MODEL.
        //
        // `TeamGate` keys on the ROUTE VALUE, so a route without one is not gated at all. A
        // `/api/kanban/...` route with `team` as an OPTIONAL QUERY PARAMETER used as a filter is the
        // hazard `../AGENTS.md` names outright, *"a route naming a team another way is not gated,
        // silently"*. Omitting it would project the whole message log, and a card's title is drawn
        // from instruction and output text, so one team's `Read` would return another's working
        // language verbatim.
        //
        // The same declaration that lets a handler read a team is what puts it inside the gate, so
        // a route that can see a team is a route that was checked for it. Nothing below re-asks the
        // authority question; it is answered before the handler runs.
        //
        // THE ROUTE ABOVE IS NOT A HOLE, AND THE REASON IS NOT THE SPELLING. A tenant route whose
        // `team` DEFAULTED TO NULL with nothing else bounding the projection would read the whole
        // log when it was omitted. This one bounds the projection by the caller's effective set
        // FIRST and treats `team` only as a narrowing of that set - so the parameter cannot widen
        // anything.
        app.MapGet("/api/teams/{team}/kanban/board", GetBoardAsync)
            .RequirePermit(Permits.Read)
            .WithTags("Kanban")
            .WithSummary("Get the team kanban board")
            .WithDescription(
                "Returns this team's board, with optional filtering by member, status or outcome (an "
                + "outcome's id, or `none` for cards with no outcome). Each card carries its `outcome`: "
                + "its open workflow's, else its latest workflow's, or null.\n\n"
                + "Searching a card's CONTENTS is done over the fetched board, not here.");

        // THE ONE ROUTE THAT ANSWERS WITH A TRAIL, and the only place a note or a comment is read
        // back. A BARE card - the record the board hands out - carries neither, so without this
        // `kanban.card.commented` and the `note` on a move or an edit would be written to the log
        // and read by nothing. The console's panel renders the Trail section from this.
        app.MapGet("/api/teams/{team}/kanban/cards/{id}", GetCardAsync)
            .RequirePermit(Permits.Read)
            .WithTags("Kanban")
            .WithSummary("Get a specific kanban card")
            .WithDescription(
                "Returns one card plus its TRAIL: the messages that card is made of, oldest-first "
                + "- the instruction that began the workflow, what the member reported as it ran, "
                + "every move, edit and comment a person made, and the note each carried.\n\n"
                + "The board's cards do not carry a trail. It grows with the log and the board "
                + "renders none of it, so it is fetched when somebody opens a card.");

        app.MapPost("/api/teams/{team}/kanban/cards/{id}/move", MoveCardAsync)
            .RequirePermit(Permits.Progress)
            .WithTags("Kanban")
            .WithSummary("Move a card to a different lane")
            .WithDescription(
                "Appends a kanban.card.moved message to the log, which WAKES this team's "
                + "Manager IF IT HAS ONE - the message carries the card's team, and the Manager "
                + "subscribes to it. What it does about the move is its own decision, from its "
                + "kanban skill.\n\n"
                + "A team with no Manager records the move and wakes nobody. That is a real shape "
                + "rather than a hypothetical - a converted tenant-agent row leaves one - so the "
                + "recording is what this route promises and the wake is what it usually does.");

        app.MapPost("/api/teams/{team}/kanban/cards/{id}/edit", EditCardAsync)
            .RequirePermit(Permits.Progress)
            .WithTags("Kanban")
            .WithSummary("Edit a kanban card")
            .WithDescription(
                "Appends a kanban.card.edited message to the log, which WAKES this team's "
                + "Manager IF IT HAS ONE. This is how a person hands work back to the Manager from "
                + "the board; there is no separate 'send instruction' field, and there never "
                + "usefully was.\n\n"
                + "A team with no Manager records the edit and wakes nobody - see the move route.");

        app.MapPost("/api/teams/{team}/kanban/cards/{id}/comment", CommentCardAsync)
            .RequirePermit(Permits.Progress)
            .WithTags("Kanban")
            .WithSummary("Add a comment to a card")
            .WithDescription(
                "Appends a kanban.card.commented message to the log, which WAKES this team's "
                + "Manager IF IT HAS ONE. A Manager commenting does not wake itself: the delivery "
                + "pump never hands a container its own publication.\n\n"
                + "A team with no Manager records the comment and wakes nobody - see the move "
                + "route.");
    }

    /// <summary>
    /// This team's board, and the ONE place the team scope is applied.
    ///
    /// Every handler projects through here rather than searching the log itself, so there is one
    /// place to apply a filter, not one per handler. The team comes from the ROUTE VALUE and is not
    /// overridable by a caller: a `team` query parameter is a convenience for narrowing what you may
    /// already see, never a widening of it, so it does not exist here.
    /// </summary>
    private static async Task<KanbanBoard> BoardAsync(
        IMessageLog log, TeamRegistry teams, IOutcomeStore outcomes, string team, KanbanFilter? filter = null,
        CancellationToken ct = default)
    {
        // FROM THIS TEAM'S FLOOR, NOT FROM ZERO. See `AboveTheirFloors` - a board that reads the
        // whole log shows a recreated team the cards of the team it replaced.
        var messages = AboveTheirFloors(
            await log.ReadRangeAsync(0, int.MaxValue),
            _ => teams.FloorFor(team));

        // `with` rather than a fresh record, so every other filter the caller asked for survives
        // while the team is replaced by the gated one.
        var scoped = (filter ?? new KanbanFilter()) with { Team = team };

        var board = KanbanProjector.Project(messages, scoped);

        // EACH CARD NAMES ITS OPEN WORKFLOW, so a Concierge resuming it joins that workflow, and
        // then the outcome that workflow serves - which is what the Outcome filter reads.
        var cards = await CardOutcomes.AttachAsync(
            await CardOpenWorkflows.AttachAsync(board.Cards, messages, log, ct), outcomes, ct);

        return board with { Cards = await CardOutcomes.MatchingAsync(cards, scoped.Outcome, outcomes, ct) };
    }

    /// <summary>
    /// One card of THIS team, WITH ITS TRAIL, or null. Null becomes 404 - never another team's card.
    ///
    /// The team scope is applied exactly where <see cref="BoardAsync"/> applies it: by handing the
    /// projector a filter whose <c>Team</c> is the gated route value. A card outside it is not found
    /// rather than found and withheld, so there is no second refusal to keep in step with the first.
    ///
    /// THE TRAIL COSTS NOTHING EXTRA HERE. It is accumulated by the same handlers that decide the
    /// card's lane and status, on the walk this call was already making; what would have cost
    /// something is putting it on every card of every BOARD fetch, which is why the board's cards
    /// carry none.
    /// </summary>
    private static async Task<KanbanCardDetail?> CardAsync(
        IMessageLog log, TeamRegistry teams, string team, string id, CancellationToken ct = default,
        IOutcomeStore? outcomes = null)
    {
        var messages = AboveTheirFloors(
            await log.ReadRangeAsync(0, int.MaxValue),
            _ => teams.FloorFor(team));

        if (KanbanProjector.ProjectCard(messages, id, new KanbanFilter(Team: team)) is not { } card)
            return null;

        // THE OPEN WORKFLOW, as the board's cards carry it, and its outcome when asked.
        var attached = await CardOpenWorkflows.AttachAsync([card], messages, log, ct);
        return (outcomes is null ? attached : await CardOutcomes.AttachAsync(attached, outcomes, ct))[0];
    }

    /// <summary>
    /// The rows that belong to the CURRENT incarnation of each team, dropping those a previous one
    /// wrote.
    ///
    /// <para>
    /// A TEAM IS ITS ID, AND AN ID CAN BE REUSED. Deleting a team does not touch the message log -
    /// deliberately, because the log is append-only and other teams' messages cite it - so a team
    /// created again with the same name IS the same id and inherits every row its predecessor
    /// wrote - its predecessor's members would show cards in Needs You. `floor_seq` records where a
    /// team's history starts, and the board asks it.
    /// </para>
    ///
    /// <para>
    /// A MESSAGE WHOSE TEAM CANNOT BE RECOVERED IS KEPT, and that is safe rather than lax. Only an
    /// INSTRUCTION creates a card; every other arm updates one that already exists, by looking it up
    /// on <c>{correlation}_{member}</c>. So a `container.started` from below the floor finds no card
    /// - its instruction was dropped here - and does nothing. Dropping those rows instead would risk
    /// losing an arm whose team is genuinely underivable on a card that is legitimately in scope.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Message> AboveTheirFloors(
        IReadOnlyList<Message> messages, Func<string, long> floorFor)
    {
        // Cached per call: `FloorFor` walks a team's containers, and the tenant board asks about
        // the same handful of teams once per message.
        var floors = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        return messages
            .Where(message =>
            {
                if (MessageTeam.Of(message) is not { } team) return true;

                if (!floors.TryGetValue(team, out var floor))
                {
                    floor = floorFor(team);
                    floors[team] = floor;
                }

                return message.Seq > floor;
            })
            .ToList();
    }

    private const string OutcomeFilterDescription =
        "Only cards whose outcome is this one (its id; a merged outcome's work is its target's), "
        + "or `none` for cards with no outcome; several, comma-separated, keep a card serving any of them. A card's outcome is its open workflow's, else its "
        + "latest workflow's, and each card carries it as `outcome` (id, name, status).";

    /// <summary>
    /// Stamps each lane's <see cref="Lane.WipLimit"/> from the tenant settings, read at this fetch.
    /// The lane that holds RUNNING work shows `wip.maxRunning`; the rest show
    /// `kanban.wipLimits`.
    /// </summary>

    private static KanbanBoard WithLaneLimits(KanbanBoard board, TenantSettings settings) =>
        board with
        {
            Lanes = board.Lanes.Select(lane => lane with { WipLimit = settings.LaneLimit(lane.Id) }).ToList(),
        };

    private static async Task<IResult> GetBoardAsync(
        string team,
        HttpContext context,
        IMessageLog log,
        TeamRegistry teams,
        TenantSettings settings,
        IOutcomeStore outcomes,
        [Description("Filter by member; several, comma-separated, keep a card of any of them.")] string? member = null,
        [Description("Filter by status; several, comma-separated, keep a card in any of them.")] string? status = null,
        [Description(OutcomeFilterDescription)] string? outcome = null,
        CancellationToken ct = default)
    {
        try
        {
            var filter = new KanbanFilter(Member: member, Status: status, Outcome: outcome);

            return Results.Ok(WithLaneLimits(await BoardAsync(log, teams, outcomes, team, filter, ct), settings));
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    /// <summary>
    /// The tenant-wide board: the union of every reachable team's projection, in one payload.
    ///
    /// <para>
    /// THE LOG IS READ ONCE AND PROJECTED ONCE, and both halves of that had to be said. Reusing
    /// <see cref="BoardAsync"/> per team would have been the shorter code and it reads the whole
    /// log once per team; hoisting the READ out of the loop left the PROJECTION inside it, which on
    /// a tenant with a dozen teams is a dozen full walks of the log - each one building every
    /// card's trail only for <c>ToCard()</c> to throw it away - for one board fetch, repeated on
    /// every hub push while the tab is open.
    /// </para>
    ///
    /// <para>
    /// THE ANSWER IS IDENTICAL, and the reason is where the team filter lives. It is applied by
    /// <c>KanbanProjector.Matching</c> to FINISHED cards, not while they are being built: the card
    /// states come from every message either way, and the filter only decides which survive. So
    /// projecting once with no team and keeping the cards whose team is in scope is the same set
    /// the per-team loop produced, in a different order - and neither route promises an order.
    /// Pinned by <c>KanbanTenantBoardTests.The_tenant_board_holds_exactly_what_the_per_team_boards_hold</c>,
    /// against the per-team route, which is the address the CLI and every container use.
    /// </para>
    ///
    /// <para>
    /// The scope is matched case-insensitively for the reason the projector's own filter is: a team
    /// id compares the way <c>ContainerId</c> compares one, and a scope check that folded case
    /// differently from the filter it replaces would drop a team's cards silently.
    /// </para>
    ///
    /// <para>
    /// The LANES are the board's fixed set rather than off any one team's board, because a caller who
    /// reaches no team still gets a board shape: an empty board with no columns renders as a
    /// failure, and this is a legitimate empty.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetTenantBoardAsync(
        HttpContext context,
        IMessageLog log,
        TeamRegistry teams,
        TeamAccess access,
        TenantSettings settings,
        IOutcomeStore outcomes,
        [Description("Narrow to a team, or several comma-separated. Absent means every team the caller reaches.")] string? team = null,
        [Description("Filter by member; several, comma-separated, keep a card of any of them.")] string? member = null,
        [Description("Filter by status; several, comma-separated, keep a card in any of them.")] string? status = null,
        [Description(OutcomeFilterDescription)] string? outcome = null,
        CancellationToken ct = default)
    {
        if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

        try
        {
            var effective = await access.EffectiveTeamsAsync(principal, ct);

            var scope = teams.All()
                .Select(t => t.Id)
                .Where(effective.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // NARROWING ONLY. An intersection rather than a replacement, so a `team` the caller
            // does not reach leaves an EMPTY scope - no cards - instead of reaching outside the
            // set, and instead of a refusal that would say whether that team exists.
            // SEVERAL MAY BE NAMED, comma-separated: the scope is the teams named that the caller reaches.
            if (KanbanFilter.Values(team) is { Count: > 0 } named)
            {
                var asked = named.ToHashSet(StringComparer.OrdinalIgnoreCase);
                scope = scope.Where(asked.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            var filter = new KanbanFilter(Member: member, Status: status, Outcome: outcome);

            // PER-TEAM FLOORS HERE, because this projection spans every team the caller reaches and
            // each one's history starts in a different place.
            var messages = AboveTheirFloors(
                await log.ReadRangeAsync(0, int.MaxValue), teams.FloorFor);

            // ONE PROJECTION, THEN THE SCOPE. The filter carries no team - every other narrowing
            // the caller asked for still runs inside the projection, exactly where it did.
            var cards = await CardOutcomes.AttachAsync(
                await CardOpenWorkflows.AttachAsync(
                    KanbanProjector.Project(messages, filter).Cards
                        .Where(card => scope.Contains(card.Team))
                        .ToList(),
                    messages,
                    log,
                    ct),
                outcomes,
                ct);

            // The filter is echoed back as the caller ASKED it, `team` included - which is how the
            // console can show what the board is actually narrowed to. It is not the resolved
            // scope: naming every team a caller reaches back at them is a different fact.
            cards = await CardOutcomes.MatchingAsync(cards, filter.Outcome, outcomes, ct);

            return Results.Ok(WithLaneLimits(
                new KanbanBoard(KanbanLanes.All, cards, filter with { Team = team }), settings));
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    private static async Task<IResult> GetCardAsync(
        string team,
        string id,
        IMessageLog log,
        TeamRegistry teams,
        IOutcomeStore outcomes,
        CancellationToken ct = default)
    {
        try
        {
            var card = await CardAsync(log, teams, team, id, ct, outcomes);

            if (card is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(card);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    private sealed record MoveRequest(string LaneId, string? Note = null);
    private sealed record EditRequest(string? Title = null, string? Status = null, string? Note = null);
    private sealed record CommentRequest(string Text);

    private static async Task<IResult> MoveCardAsync(
        string team,
        string id,
        MoveRequest request,
        HttpContext context,
        IMessageLog log,
        TeamRegistry teams,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.LaneId))
        {
            return Results.BadRequest(new { error = "laneId is required" });
        }

        // ONLY A LANE THE BOARD HAS. A card moved anywhere else vanished from every column.
        if (KanbanLanes.Find(request.LaneId) is not { } laneId)
        {
            return Results.BadRequest(new
            {
                error = $"There is no lane '{request.LaneId}'. The lanes are {string.Join(", ", KanbanLanes.All.Select(lane => lane.Id))}.",
            });
        }

        try
        {
            var from = PrincipalClaims.From(context.User)?.Id ?? "console";
            var card = await CardAsync(log, teams, team, id);

            if (card is null)
            {
                return Results.NotFound();
            }

            var payload = JsonSerializer.Serialize(new
            {
                cardId = id,
                laneId,
                note = request.Note,
                actor = from,
                team = card.Team,
                member = card.Member,
                change = $"Moved to {laneId}",
            });

            await log.AppendAsync(
                new NewMessage(
                    MessageTypes.KanbanCardMoved,
                    payload,
                    from,
                    card.WorkflowSeq),
                ct);

            return Results.Ok(new { cardId = id, laneId, message = "Card move recorded" });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    /// <summary>
    /// The refusal an edit that would change nothing gets, and the whole of what it may say.
    ///
    /// It names the three things that count as a change, because a caller told only "bad request"
    /// retries the same body, and it says that nothing was recorded - because the caller's next
    /// question is whether it half-happened.
    /// </summary>
    private const string NothingToChange =
        "Nothing to change: give a title or a status different from the card's, or a note. "
        + "Nothing was recorded and the team's manager was not woken.";

    /// <summary>
    /// The longest a value may be RENDERED at inside <c>change</c>. The FIELDS are never cut.
    ///
    /// The composed sentence is the one part of this row that reaches a prompt - <c>MessageText</c>
    /// interpolates it verbatim - and a headless CLI re-sends its whole conversation every turn, so
    /// a pair of 80-character titles in a wake is context billed for the rest of the run. The
    /// payload carries both values in full for anything that wants them, which is what makes
    /// cutting the prose cheap rather than lossy. The cut is MARKED: a reader who cannot see that
    /// text was dropped has no reason to go looking for the rest.
    /// </summary>
    private const int MaxRenderedValue = 60;

    private static string Rendered(string value)
    {
        if (value.Length <= MaxRenderedValue) return value;

        // NEVER BETWEEN THE HALVES OF ONE CHARACTER. A title whose emoji straddles the boundary
        // would leave a LONE SURROGATE - invalid UTF-16 - in a string that is about to be
        // serialised as JSON and then read by an agent, and it arrives as a replacement character.
        // The board's own cut is not exposed to this: `InstructionText` cuts at a word boundary.
        var cut = char.IsHighSurrogate(value[MaxRenderedValue - 1])
            ? MaxRenderedValue - 1
            : MaxRenderedValue;

        return value[..cut] + "...";
    }

    /// <summary>
    /// One field's movement, as a clause: <c>status from queued to blocked</c>, or
    /// <c>title from 'Cache the slow queries' to 'Cache the two slowest queries'</c>.
    ///
    /// FREE TEXT IS QUOTED AND AN IDENTIFIER IS NOT, so a title containing the word "to" cannot be
    /// read as the end of the clause.
    /// </summary>
    private static string Moved(string field, string before, string after, bool quoted)
    {
        string Show(string value) => quoted ? $"'{Rendered(value)}'" : Rendered(value);

        // A card with an empty title has no "before" worth naming, and `from  to X` reads as a
        // word that went missing rather than one that was never there.
        return string.IsNullOrWhiteSpace(before)
            ? $"{field} set to {Show(after)}"
            : $"{field} from {Show(before)} to {Show(after)}";
    }

    /// <summary>A submitted value, or null for one that is absent or only whitespace.</summary>
    private static string? Given(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// A person's edit: refused unless it would actually change something, and recorded with BOTH
    /// the old value and the new one.
    ///
    /// <para>
    /// AN EDIT THAT CHANGES NOTHING IS REFUSED, BECAUSE APPENDING ONE COSTS MONEY. This row wakes
    /// the team's Manager, which is a billed agent invocation. The panel posts only the fields a
    /// person altered, so clearing the title box and pressing Save posts <c>{}</c>, and
    /// an API caller sending no fields posts the same. The guard is HERE rather than in the panel
    /// because every other client reaches this identical route, and a client-side check leaves that
    /// half open.
    /// </para>
    ///
    /// <para>
    /// IT IS ONE GUARD AND IT SITS AFTER THE LOOKUP, though an empty body could be refused without
    /// projecting anything. Two refusals saying the same thing is two places to disagree - and
    /// refusing first would answer "nothing to change" about a card id that does not exist, where
    /// the 404 is the honest answer and must win.
    /// </para>
    ///
    /// <para>
    /// 400 RATHER THAN 409, though a same-valued edit is well formed. There is nothing here for a
    /// caller to wait out: an unchanged request is refused identically for as long as the card
    /// stays as it is. This is a request carrying no change, not a state that is busy.
    /// </para>
    ///
    /// <para>
    /// THE PREVIOUS VALUES COME FROM THE PROJECTED CARD AND NEVER FROM THE CLIENT. The log is
    /// append-only and a Manager woken by this row has NO EARLIER VERSION OF THE CARD to diff
    /// against - whatever the row does not tell it, it cannot work out. A client-supplied "before"
    /// would be a client-supplied claim about the one fact this row exists to establish.
    /// </para>
    /// </summary>
    private static async Task<IResult> EditCardAsync(
        string team,
        string id,
        EditRequest request,
        HttpContext context,
        IMessageLog log,
        TeamRegistry teams,
        CancellationToken ct = default)
    {
        try
        {
            var from = PrincipalClaims.From(context.User)?.Id ?? "console";
            var card = await CardAsync(log, teams, team, id);

            if (card is null)
            {
                return Results.NotFound();
            }

            var title = Given(request?.Title);
            var status = Given(request?.Status);
            var note = Given(request?.Note);

            // COMPARED AS THE BOARD WILL STORE IT. The projector cuts a hand-typed title through
            // `InstructionText.Split`, so the card's title is already a cut of whatever was typed,
            // and re-sending that same long title is not a change. Split here too rather than a
            // second copy of the 80-character rule, for the reason that file gives.
            var titleMoved = title is not null
                && !string.Equals(InstructionText.Split(title).Subject, card.Title, StringComparison.Ordinal);

            var statusMoved = status is not null
                && !string.Equals(status, card.Status, StringComparison.Ordinal);

            if (!titleMoved && !statusMoved && note is null)
            {
                return Results.BadRequest(new { error = NothingToChange });
            }

            // Named in the order the dialog lists them, and EMPTY when only a note was written -
            // which is a real shape rather than a gap. A note names no field, so there is no change
            // to summarise and `MessageText` drops the clause whole.
            var parts = new List<string>();
            if (titleMoved) parts.Add(Moved("title", card.Title, title!, quoted: true));
            if (statusMoved) parts.Add(Moved("status", card.Status, status!, quoted: false));

            var change = string.Join("; ", parts);

            var payload = JsonSerializer.Serialize(new
            {
                cardId = id,

                // THE NORMALISED LOCAL, not the raw request value - the same one the guard and
                // `change` above already used. A whitespace-only title clears to null through
                // `Given`, so it must not reach the payload as three spaces: the projector's edit
                // arm treats any non-empty string as a new title and would blank the card on a
                // request the guard and the sentence both read as "title did not move".
                title,

                // NULL WHERE THE FIELD DID NOT MOVE, so the row never asserts what a field "was"
                // for a field nobody changed.
                previousTitle = titleMoved ? card.Title : null,
                status,
                previousStatus = statusMoved ? card.Status : null,
                note,
                actor = from,
                team = card.Team,
                member = card.Member,
                change,
            });

            await log.AppendAsync(
                new NewMessage(
                    MessageTypes.KanbanCardEdited,
                    payload,
                    from,
                    card.WorkflowSeq),
                ct);

            return Results.Ok(new
            {
                cardId = id,
                title = request?.Title,
                status = request?.Status,
                change,
                message = "Card edit recorded",
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    private static async Task<IResult> CommentCardAsync(
        string team,
        string id,
        CommentRequest request,
        HttpContext context,
        IMessageLog log,
        TeamRegistry teams,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.BadRequest(new { error = "text is required" });
        }

        try
        {
            var from = PrincipalClaims.From(context.User)?.Id ?? "console";
            var card = await CardAsync(log, teams, team, id);

            if (card is null)
            {
                return Results.NotFound();
            }

            var payload = JsonSerializer.Serialize(new
            {
                cardId = id,
                text = request.Text,
                actor = from,
                team = card.Team,
                member = card.Member,
            });

            await log.AppendAsync(
                new NewMessage(
                    MessageTypes.KanbanCardCommented,
                    payload,
                    from,
                    card.WorkflowSeq),
                ct);

            return Results.Ok(new { cardId = id, text = request.Text, message = "Comment recorded" });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }
}