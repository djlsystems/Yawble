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

internal static class WorkflowEndpoints
{
    public static void Map(WebApplication app)
    {
        // How long this team's newest workflow has been going, and how much run time went into it.
        //
        // Projected on the SERVER over `messages`, beside the tokens route and for the same reason: a
        // figure summed in the browser over the board's twenty-message feed would shrink as the team got
        // busier. It returns INSTANTS and never a computed elapsed integer - the client subtracts and ticks
        // it, so a tile counts up every second with no further traffic.
        //
        // IT IS FETCHED AND NEVER PUSHED. ContainerSnapshot rides every SignalR frame to every browser
        // holding the team and already carries `currentCorrelation`, which is all the push side needs.
        app.MapGet("/api/teams/{team}/workflow", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, IMessageLog log, ContainerHost host, IBacklogStore backlog, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            // THE SAME FLOOR THE TOKENS ROUTE COMPUTES, deliberately the same block rather than a second
            // one: the lowest floor any of this team's live containers holds. Per-source floors would mean
            // the messages table joining team_members, which IMessageLog cannot do - see
            // SumUsageForTeamAsync's own remarks for exactly what that trade costs.
            //
            // Without it a team deleted and recreated under the same names reports its PREDECESSOR'S
            // elapsed time and its predecessor's workflow, because members share their qualified ids with
            // whatever came before. `floor_seq` is this repository's worked example of one store of a fact
            // and several readers only some of which consult it; this is the fourth reader and it consults.
            var floor = teams.ContainerIdsOf(stored)
                .Select(host.Find)
                .Where(container => container is not null)
                .Select(container => container!.Snapshot().SinceSeq)
                .DefaultIfEmpty(0)
                .Min();

            // THE ITEM'S CURRENT TITLE, as `/workflows` and the rollup serve it.
            return Results.Ok(await WorkflowItems.OverlayAsync(await log.ElapsedForTeamAsync(stored, floor, ct), backlog, ct));
        })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("How long this team's current or last workflow has taken")
            .WithDescription(
                "A LOG PROJECTION over the team's newest workflow: when it started, whether it is still "
                + "open, and how much per-member run time went into it.\n\n"
                + "Two durations, named differently and never added. ELAPSED is wall clock and is not on "
                + "this payload at all - `startedAt` and `serverNow` are, and the client subtracts them "
                + "and ticks the result with no further traffic. `executionSeconds` is per-member run time "
                + "SUMMED, and it legitimately EXCEEDS elapsed whenever two members overlap; that "
                + "inequality is the pairing working rather than a fault.\n\n"
                + "`serverNow` is here because the browser's clock may be wrong. Without it a machine ten "
                + "minutes fast renders a workflow that started in the future.\n\n"
                + "A run that started and has no terminal partner - a host restart mid-flight - is counted "
                + "in `runsUnfinished` and excluded from the total. Never zero, and never still running.\n\n"
                + "`available` is false for a team that has never run. Render an em dash: zero is a "
                + "measured duration and this is an absent one. 404 for an unknown team.");

        // EVERY WORKFLOW THIS TEAM HAS RUN SINCE ITS FLOOR, open and closed alike - not only the newest
        // and not only the open ones.
        //
        // A member can hold several workflows at once and keep them apart, so "the newest workflow" no
        // longer describes what a team is doing. Same projection as the singular route above, called once
        // per correlation instead of once for the newest - see TeamWorkflows.
        app.MapGet("/api/teams/{team}/workflows", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, IMessageLog log, ContainerHost host, IOutcomeStore outcomes, IWakeStore wakes,
            IBacklogStore backlog, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            // THE SAME FLOOR THE SINGULAR ROUTE COMPUTES, deliberately the same block rather than a second
            // one: the lowest floor any of this team's live containers holds. Without it a team deleted and
            // recreated under the same names reports its PREDECESSOR'S open work, because members share
            // their qualified ids with whatever came before.
            var floor = teams.ContainerIdsOf(stored)
                .Select(host.Find)
                .Where(container => container is not null)
                .Select(container => container!.Snapshot().SinceSeq)
                .DefaultIfEmpty(0)
                .Min();

            // EACH DISPATCH ROW'S ITEM WITH ITS CURRENT TITLE, in one backlog read for the whole list.
            var listed = await WorkflowItems.OverlayAsync(await log.WorkflowsForTeamAsync(stored, floor, ct), backlog, ct);

            // EACH ROW'S OUTCOME, in one query for the whole list: the workflows view's Outcome column.
            var served = await outcomes.CurrentOutcomesAsync(
                listed.Workflows.Where(w => w.Correlation is not null).Select(w => w.Correlation!.Value).ToList(), ct);

            // AND ITS PENDING WAKES, from one read of this team's.
            var pendingWakes = (await wakes.ForTeamAsync(stored, ct))
                .GroupBy(w => w.Workflow)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<WakeView>)g.Select(w => w.View()).ToList());

            return Results.Ok(listed with
            {
                Workflows = listed.Workflows
                    .Select(w => w.Correlation is { } c && served.TryGetValue(c, out var outcome) ? w with { Outcome = outcome } : w)
                    .Select(w => w.Correlation is { } c && pendingWakes.TryGetValue(c, out var waiting) ? w with { Wakes = waiting } : w)
                    .ToList(),
            });
        })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("Every workflow this team has run since its floor")
            .WithDescription(
                "A LOG PROJECTION over EVERY workflow this team has run since its floor - open AND closed, "
                + "not only the newest: bounds, completion, per-member outcomes, declarations, failures and "
                + "run pairing, per workflow. Each row reports its own state, so a finished workflow says "
                + "so rather than being listed as work still in flight.\n\n"
                + "`earliestStartedAt` is the start of the SPAN across every OPEN workflow and is never a "
                + "sum of their durations - three workflows covering the same ten minutes summed would read "
                + "as thirty, a number larger than the time that has actually passed. There is no total "
                + "duration field on this payload and there must not be one. It is null when nothing is "
                + "open, even though `workflows` still lists the finished ones.\n\n"
                + "An open workflow is one with no terminal row (`workflow.completed` or `workflow.closed`) "
                + "that nothing has woken since - see `WorkflowOpenSql` for the one place that predicate "
                + "lives.\n\n"
                + "TWO COUNTS, AND THEY ANSWER DIFFERENT QUESTIONS. `openCount` is how many workflows this "
                + "team HOLDS OPEN, and it is ALLOWED TO GROW: a wake that ends without a declaration or a "
                + "closure leaves a workflow open forever, and this number is what makes that visible. "
                + "`totalCount` is every workflow the team has run since its floor, uncapped - compare "
                + "`workflows.length` against THAT to tell truncation, never against `openCount`.\n\n"
                + "`workflows` lists them newest first, capped at fifty, so a team that just finished "
                + "everything still has something to render rather than an em dash where a completion "
                + "belongs.\n\n"
                + "`available` is false for a team that has never run. Render an em dash: zero is a "
                + "measured count and this is an absent one. 404 for an unknown team.");

        // WHICH WORKFLOWS ARE OPEN UNDER ONE MEMBER - the read-side twin of `workflow-complete`.
        //
        // `/api/teams/{team}/workflows` lists the TEAM'S open workflows and nothing
        // else - not which of them are a Manager's own to declare, and not which of them nobody is
        // working. A Manager can end its last wake holding an open workflow it said it would close, and
        // every fact needed to notice that lives in three places; this route puts them together.
        //
        // IT DECLARES NOTHING AND CHANGES NOTHING, and that is deliberate rather than incidental: a verb
        // CANNOT be the fix, because a skill instructs and cannot enforce. This
        // makes the state legible. It is not a safeguard and must not be built on as one.
        //
        // IT COMPOSES EXISTING PIECES rather than deriving anything of its own:
        //
        //   * `WorkflowsForTeamAsync` - every workflow since the floor, open and closed alike, newest
        //     first, capped at fifty, with the uncapped `openCount` beside it.
        //   * `WorkflowOwner.OfAsync` - who may declare a workflow, with the SAME Manager fallback
        //     `workflow-complete` applies, because two answers to "whose is it" is how a member is told it
        //     may close something the declaring route then refuses.
        //   * `WorkflowBusyState.DescribeAsync` - what is still in flight under a correlation, with the
        //     SAME caller exclusion, for the same reason: "empty here" and "not refused there" have to mean
        //     one thing about one instant, or this route sends a member to a refusal it predicted away.
        //
        // `{name}` IS A FILTER, NOT AN AUTHORITY CLAIM, and the permit is plain Read like every other
        // read route on a member (`GET .../containers/{name}`, `.../messages`). It exposes nothing a team
        // reader could not already assemble from the routes above; what it adds is the assembly. The
        // member it names is the one whose own Running state is excluded from every `working` list, which
        // is a question about WHOSE VIEW this is and not about who is asking - and `{team}` in the route
        // means TeamGate covers the access question structurally, as it does everywhere else.
        //
        // CLOSED WORKFLOWS ARE FILTERED HERE, on `EndedAt is null` against a typed record, not by a client
        // on the far side of the wire: a payload that never carries a closed workflow cannot be rendered
        // wrongly by a client that filters it wrongly.
        app.MapGet("/api/teams/{team}/containers/{name}/workflows", async (
            [Description(Describe.Team)] string team,
            [Description(
                "The member to answer for. A FILTER and not an authority claim: it selects whose view this "
                + "is - whose own Running state is excluded from each `working` list, and which workflows "
                + "come back marked `yours`.")]
            string name,
            TeamRegistry teams, IMessageLog log, ContainerHost host, IPendingDeliveries pending,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            if (host.Find(new ContainerId(stored, name)) is not { } container)
            {
                return Results.NotFound(new { error = $"No member '{name}'." });
            }

            // THE SAME FLOOR THE TWO ROUTES ABOVE COMPUTE, deliberately the same block rather than a
            // fourth spelling: without it a team deleted and recreated under the same names reports its
            // PREDECESSOR'S open work, because members share their qualified ids with whatever came before.
            var floor = teams.ContainerIdsOf(stored)
                .Select(host.Find)
                .Where(found => found is not null)
                .Select(found => found!.Snapshot().SinceSeq)
                .DefaultIfEmpty(0)
                .Min();

            var projection = await log.WorkflowsForTeamAsync(stored, floor, ct);
            var snapshot = container.Snapshot();

            if (!projection.Available)
            {
                // UNAVAILABLE IS NOT "NOTHING IS OPEN", and the two must not collapse into one empty list:
                // a team that has never run cannot answer the question, where a team holding nothing open
                // has answered it. `missing` carries the words, exactly as the sibling routes do.
                return Results.Ok(new MemberOpenWorkflows(
                    false, snapshot.Id, 0, projection.ServerNow, [], projection.Missing));
            }

            var rows = new List<OpenWorkflowRow>();

            foreach (var workflow in projection.Workflows)
            {
                // `EndedAt` IS THE OPEN TEST, not `state` - the predicate the `status` tool, the team
                // tile and `teamKpis.ts` all already settled on. `state` is a seven-value string needing an
                // exclusion list a new state could silently join, and a FAILED workflow is emphatically
                // still open.
                if (workflow.EndedAt is not null) continue;

                // A ROW WITH NO CORRELATION CANNOT BE ASKED ABOUT. The projection types it nullable and a
                // null would make both lookups below match nothing, turning "unattributable" into
                // "nobody is working it" - the one answer this route must never invent.
                if (workflow.Correlation is not { } correlation) continue;

                var owner = await WorkflowOwner.OfAsync(log, correlation, ct)
                    ?? new ContainerId(stored, TeamRegistry.DefaultManagerName);

                var working = await WorkflowBusyState.DescribeAsync(
                    stored, correlation, host, pending, log, container.Id, ct);

                rows.Add(new OpenWorkflowRow(
                    correlation,
                    workflow.Subject,
                    workflow.StartedAt,
                    workflow.LastActivityAt,
                    owner.Name,
                    owner.Equals(container.Id),
                    snapshot.CurrentCorrelation == correlation,
                    [.. workflow.Members.Select(member => member.Member)],
                    working));
            }

            return Results.Ok(new MemberOpenWorkflows(
                true, snapshot.Id, projection.OpenCount, projection.ServerNow, rows));
        })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("Which workflows are open under this member")
            .WithDescription(
                "EVERY OPEN WORKFLOW ON THIS TEAM, seen from one member: what it is called, who may declare "
                + "it complete, and whether anything is still working it.\n\n"
                + "This is the read-side twin of `workflow-complete`. That route answers 'may I declare "
                + "this' for ONE workflow with a 204 or a refusal naming what is busy; this asks the same "
                + "questions of every open workflow at once and answers them in a list. It declares "
                + "nothing, closes nothing and changes nothing.\n\n"
                + "`owner` is which member may declare each workflow - the container the root instruction "
                + "addressed, falling back to this team's Manager when the root addressed nobody (a "
                + "schedule firing, a kanban card event). `yours` is whether that owner is the member named "
                + "in the route; `current` is whether that member is running under the workflow at this "
                + "instant, which is false between invocations.\n\n"
                + "`working` IS THE FIELD THIS ROUTE EXISTS FOR. It carries the same sentences "
                + "`workflow-complete` refuses with, from the same predicate: a member Running under the "
                + "workflow, or a delivery accepted under it. AN EMPTY LIST MEANS NOTHING IS COMING - "
                + "nobody is working that workflow and nobody has accepted work under it, so no event it "
                + "could produce will ever wake anybody about it again. The member named in the route is "
                + "excluded from every list, on the same terms `workflow-complete` excludes its caller, "
                + "because a member reads this from inside its own run.\n\n"
                + "Closed workflows are filtered out server-side on `endedAt`, so nothing here announces "
                + "delivered work as live. `openCount` counts what the TEAM holds open, uncapped - compare "
                + "`workflows.length` against it to tell truncation; the list inherits the fifty-entry cap "
                + "on `GET /api/teams/{team}/workflows` and never adds a second one.\n\n"
                + "`correlation` is for finding the thread (`GET /api/workflows/{correlationId}`). It is "
                + "NOT an argument to `workflow-complete`, which takes its correlation from the container's "
                + "own causation and never from a caller.\n\n"
                + "`available` is false for a team that has never run - which is not the same as nothing "
                + "being open, and `missing` says so in words. 404 for an unknown team or member.");

        // A declaration that this workflow is done, by the container the workflow's root instruction
        // addressed - the Manager when the root was not an addressed instruction (a schedule firing, a
        // kanban card event).
        //
        // Same structural shape as progress/blocked: {team} in the route so TeamGate covers it by
        // declaration, a type named by the server, and correlation stamped from the running container's
        // current cause rather than from an id a caller can supply.
        app.MapPost("/api/teams/{team}/containers/{name}/workflow-complete", async (
            [Description(Describe.Team)] string team,
            [Description(
                "The member declaring completion. Must be the container the workflow's root instruction "
                + "addressed, or this team's Manager when nothing addressed it or when one of its members "
                + "was addressed.")]
            string name,
            ReportWorkflowCompleted request,
            HttpContext context, TeamRegistry teams, ContainerHost host, IMessageLog log,
            IPendingDeliveries pending, IBacklogStore backlog, ITeamPublisher publisher,
            KanbanStore kanban, WorktreeRemoval worktrees, TeamPaths paths, ILoggerFactory loggers,
            SolutionNotice solutionNotice, OutcomeGate outcomeGate, WakeMe wakes,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            var id = new ContainerId(stored, name);

            if (host.Find(id) is not { } container)
            {
                return Results.NotFound(new { error = $"No member '{name}'." });
            }

            // Identity, not permit. A member claiming to be somebody else is refused by this check rather
            // than believed.
            if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.Container } caller
                || !ContainerId.Parse(caller.Id).Equals(container.Id))
            {
                return Results.Json(
                    new { error = "A member may only declare its own workflows completed." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // The ROOT's seq, not the seq that woke THIS container: a follow-up inside an existing
            // workflow is caused by a different row entirely, so reading CurrentCausation here would look
            // up the wrong message and derive the wrong owner, or none.
            var correlation = container.Snapshot().CurrentCorrelation ?? 0;

            // A CONTAINER THAT IS NOT RUNNING A WORKFLOW HAS NO WORKFLOW TO DECLARE.
            //
            // `CurrentCorrelation` is null between invocations, and no message on the log carries correlation
            // 0 - a root settles to its own seq - so a zero here makes every arm of the busy check match
            // nothing and turns the refusal into an unconditional 204. It would also append a row stamped from
            // a null causation, which becomes its OWN root: a `workflow.completed` belonging to a workflow of
            // one message, closing nothing, on the log forever.
            //
            // Refused rather than defaulted, which is this codebase's standing direction for an absent value:
            // null means nobody has chosen, and a guess is refused.
            if (correlation == 0)
            {
                return Results.Conflict(new
                {
                    error = "This member is not currently running a workflow, so there is nothing to "
                        + "declare complete. Call this from inside the run that was given the work, once "
                        + "that invocation has woken with the instruction to close out.",
                });
            }

            // WHOSE WORKFLOW IS THIS. A member holds several at once now, and a workflow addressed to a
            // member is that member's to close - the Manager keeps the right for the workflows it was asked
            // to do, so nothing about today's shapes changes.
            //
            // Null means the root was not an addressed instruction - a schedule, a card event - and the
            // Manager owns it. Falling back rather than refusing is deliberate: a workflow nobody may
            // declare complete is a team that reads UNDECLARED forever.
            var manager = new ContainerId(stored, TeamRegistry.DefaultManagerName);
            var owner = await WorkflowOwner.OfAsync(log, correlation, ct) ?? manager;

            // THE MANAGER MAY DECLARE A WORKFLOW ONE OF ITS OWN MEMBERS OWNS, on the owner's behalf, and on
            // the same rule the owner declares by: the owner and every other member idle in it, nothing
            // queued in it but the Manager's own delivery (the busy check below, with the Manager as the
            // caller). Without it a Manager woken by a member-owned workflow could only tell the owner to
            // declare, and each refusal woke the other again. Any other member is refused, as before.
            var onBehalf = !container.Id.Equals(owner)
                && container.Id.Equals(manager)
                && string.Equals(owner.Team, stored, StringComparison.OrdinalIgnoreCase)
                && host.Find(owner) is not null;

            if (!container.Id.Equals(owner) && !onBehalf)
            {
                return Results.Json(
                    new
                    {
                        error = $"This workflow was addressed to '{owner.Name}', "
                            + "so only that member, or this team's Manager on its behalf, may declare it completed.",
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.Delivered))
            {
                return Results.BadRequest(new { error = "Say what was delivered." });
            }

            // A WORKFLOW ALREADY DECLARED OR CLOSED IS NOT DECLARED AGAIN. With two members able to declare
            // one workflow, the second would otherwise append a second `workflow.completed` to it.
            if (!(await log.OpenWorkflowsAmongAsync([correlation], ct)).Contains(correlation))
            {
                return Results.Conflict(new
                {
                    error = "This workflow is already declared complete or closed, so there is nothing "
                        + "to declare. End your turn; do not retry.",
                });
            }

            // THE OUTCOME GATE, off by default: when a person turns it on, an agent does not declare a
            // workflow that serves no outcome. Read through its delegate on every declaration. A person's
            // close and the platform's own declarations never come through here, so it never gates them.
            if (await outcomeGate.RefusalAsync(correlation, ct) is { } noOutcome)
            {
                return Results.Conflict(new { error = noOutcome });
            }

            // THE OWNER OF A MEMBER-OWNED WORKFLOW IS NOT REFUSED FOR A MANAGER THAT IS ONLY WATCHING -
            // woken by a row the owner or another member wrote here. See `WorkflowBusyState`'s `watcher`.
            // A Manager told to do work here still counts, and every member's run still counts.
            var busy = await WorkflowBusyState.DescribeAsync(
                stored, correlation, host, pending, log, container.Id, ct,
                watcher: container.Id.Equals(manager) ? null : manager);

            if (busy.Count != 0)
            {
                return Results.Conflict(new
                {
                    error = "Workflow completion refused because work is still in flight under this "
                        + "workflow: " + string.Join("; ", busy) + ".",
                });
            }

            // LOOSE ENDS. Declaring the workflow moves every one of its cards to Done, so a card still in
            // To Do, interrupted, failed or blocked would read as delivered. Refused until each is finished
            // or the declaration says, in `dropped`, why it is being left - which goes on the record.
            var looseEnds = await WorkflowLooseEnds.DescribeAsync(kanban, stored, correlation, container.Id.Name);
            var dropped = request.Dropped?.Trim();

            if (looseEnds.Count != 0 && string.IsNullOrEmpty(dropped))
            {
                return Results.Conflict(new
                {
                    error = "Workflow completion refused because these cards are not finished: "
                        + string.Join("; ", looseEnds) + ". Finish or re-send each one, or declare again "
                        + "with `dropped` saying why it is being left undone.",
                });
            }

            // THE WORK IS PUT ON ORIGIN BEFORE THE ROW THAT ACCEPTS IT, AND THE ORDER IS THE GUARANTEE.
            //
            // The append below is what drives a card to `done` - see `KanbanProjector.HandleWorkflowCompleted` -
            // so THAT ROW IS THE ACCEPTANCE. Published after it, this would be an optimisation nobody could
            // rely on; published before it, a card cannot read `done` without the branch having reached
            // origin first. Left to agents, a backlog item can read `implemented` while its work exists
            // only under a team root.
            //
            // IT CANNOT REFUSE AND IS NOT CHECKED. A team with no repository completes normally - the report
            // for one is simply empty - and a push that FAILED is its own row, `repo.pushFailed`, written by
            // the publisher a moment ago and deliberately not folded into this declaration's success or
            // failure: an unreachable origin is transport, not this member's fault, and blaming a run for it
            // is how a durability rule gets switched off. That is also why nothing here reads
            // `TeamPublishReport`: a branch on this result would be a refusal wearing a different name, and
            // refusing the acceptance is deliberately not done - a member out of budget cannot satisfy a
            // refusal.
            //
            // THE SAME CAUSATION AS THE DECLARATION, so the push rows land INSIDE the workflow being
            // accepted rather than rooting one of their own.
            await publisher.PublishAsync(
                stored, teams.ReposFor(stored), container.Id, container.CurrentCausation, ct);

            var declared = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["delivered"] = request.Delivered.Trim(),
                ["dropped"] = looseEnds.Count == 0 ? null : dropped,
                ["looseEnds"] = looseEnds.Count == 0 ? null : looseEnds,
            };

            // WHO DECLARED IT, when that is not the owner: the Manager, on the owner's behalf.
            if (onBehalf)
            {
                declared[WorkflowDeclaration.DeclaredByField] = container.Id.ToString();
                declared[WorkflowDeclaration.OnBehalfOfField] = owner.ToString();
            }

            // A DECLARED WORKFLOW HAS NOTHING LEFT TO WAKE ANYBODY FOR: its pending wakes are cancelled,
            // a row each, BEFORE the declaration, so none can fire into it after - and the answer says so,
            // because the member that asked for one is told nothing else.
            var cancelledWakes = await wakes.CancelWorkflowAsync(
                correlation, container.Id.ToString(), "the workflow was declared complete", ct);

            // THE ROW, ITS BACKLOG ITEM AND ITS SETTLED TREES: one sequence, shared with the platform's
            // declaration for a member that cannot declare (`UndeclarableWorkflows`).
            await WorkflowDeclaration.AppendAsync(
                stored, correlation, container.Id, container.CurrentCausation,
                JsonSerializer.Serialize(declared),
                log, backlog, host, kanban, worktrees, paths, teams.ReposFor(stored),
                loggers.CreateLogger("WorktreeRemoval"), ct, solutionNotice);

            return cancelledWakes.Count == 0
                ? Results.NoContent()
                : Results.Ok(new
                {
                    wakesCancelled = cancelledWakes.Select(w => w.View()).ToList(),
                    notice = WakeMe.CancelledNotice(cancelledWakes),
                });
        })
            .WithTags("Members")
            .RequirePermit(Permits.Progress)
            .WithSummary("Declare a workflow completed")
            .WithDescription(
                "A declaration that a workflow has finished and what it delivered. The caller must be the "
                + "container the workflow's root instruction addressed - this team's Manager when the root "
                + "was not an addressed instruction (a schedule firing, a kanban card event). Correlation is "
                + "stamped from the waking message by the container; the caller never supplies one. The "
                + "declaration is refused while a member is Running or has pending deliveries UNDER THIS "
                + "WORKFLOW - other open workflows on the same team do not block it. When a member owns the "
                + "workflow, a Manager woken only by that member's or another member's row in it is not "
                + "counted against the owner; a Manager told to do work in it is. The Manager may declare "
                + "a workflow one of its members owns on that member's behalf, on the same rule, and the "
                + "row records `declaredBy` and `onBehalfOf`. A workflow already declared or closed is "
                + "refused (409). Declaring cancels the workflow's pending wakes, a `wake.cancelled` row "
                + "each: the answer is then 200 with `wakesCancelled` and a `notice` saying so, and 204 "
                + "when there were none.");

        // A PERSON ENDING A WORKFLOW, WHICH IS NOT A MANAGER DECLARING A DELIVERY.
        //
        // `.HumansOnly()` for the reason team reset is: a member able to close its own workflow could erase
        // the evidence of having failed at it.
        //
        // THE REASON IS OPTIONAL, deliberately, although `workflow-complete` refuses a blank `delivered`
        // because a completion with no result says work ended and not what was delivered. The mitigation is that the
        // CLIENT prefills a suggestion from what the platform knows, so one click still produces a row that
        // accounts for itself - and the row carries WHO and WHEN regardless.
        //
        // {team} in the route so TeamGate covers it structurally, exactly like workflow-complete above; the
        // correlation is the CAUSE of the published row rather than a fresh root, so it lands INSIDE the
        // workflow it closes rather than rooting a new one of its own.
        app.MapPost("/api/teams/{team}/workflows/{correlation:long}/close", async (
            [Description(Describe.Team)] string team,
            [Description(
                "The correlation id shared by every message in the workflow to close, as returned by a "
                + "`tell` or carried on any message in the thread.")]
            long correlation,
            CloseWorkflow request,
            TeamRegistry teams, ContainerHost host, IMessageLog log, HttpContext context, WakeMe wakes,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            // ONE CHECK ANSWERS BOTH "no rows at all" AND "not this team's" - `TeamGate` gates `{team}`
            // structurally, but it cannot ask whether the CORRELATION belongs to that team, so a route
            // identified by a bare id is not gated for its resource at all. See `WorkflowTeamOwnership`'s
            // own doc comment for why the refusal is 404 rather than 403: a 403 would CONFIRM that some
            // other team's correlation is real, which is exactly the tell a caller with no business seeing
            // it must never get. Both cases read identically to an agent or a person: retype the
            // correlation, or check you are on the right team - never "you lack permission", which would
            // point at a permission that was never the problem.
            if (!await WorkflowTeamOwnership.OwnsAsync(teams, host, log, stored, correlation, ct))
            {
                return Results.NotFound(new { error = $"No workflow with correlation {correlation}." });
            }

            // The AUTHENTICATED principal, never a caller-supplied field - see `tell`'s identical comment.
            // `.HumansOnly()` guarantees this is a person, so the id is what the row's Source becomes: a
            // bare id with no `/`, exactly the shape a kanban card event's actor carries.
            var from = PrincipalClaims.From(context.User)?.Id ?? "console";

            var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();

            // A CLOSED WORKFLOW IS WOKEN BY NOTHING IT ASKED FOR: its pending wakes are cancelled first, a
            // row each naming the person, so none fires into it afterwards.
            await wakes.CancelWorkflowAsync(
                correlation,
                context.User.FindFirstValue(ClaimTypes.Email) ?? from,
                "a person closed the workflow",
                ct);

            await log.AppendAsync(
                new NewMessage(
                    MessageTypes.WorkflowClosed,
                    // `team` travels in the payload for the same reason a kanban card event's does - see
                    // MessageTeam.Of. This row is published by a PERSON, whose Source carries no `/`, so
                    // falling through to Source would recover no team and the row would be readable by a
                    // person only - invisible to agents on to the very team that closed it.
                    JsonSerializer.Serialize(new { team = stored, reason }),
                    from,
                    correlation),
                ct);

            return Results.NoContent();
        })
            .WithTags("Activity")
            .HumansOnly()
            .WithSummary("Close one workflow")
            .WithDescription(
                "A person's declaration that a workflow should stop counting as open - NOT the same as "
                + "`workflow-complete`, which is a manager's declaration of what was delivered. Publishing "
                + "`workflow.completed` on a person's behalf would put a claim on the append-only log that "
                + "nobody made, so this writes `workflow.closed` instead.\n\n"
                + "Additive: nothing is deleted, no floor moves, and the workflow's rows stay on the log and "
                + "in the feed - a closed workflow is still fully readable. New activity on the same "
                + "correlation re-opens it exactly as a manager's own declaration would.\n\n"
                + "`reason` is optional, against the recommendation to require it - `workflow-complete` "
                + "refuses a blank `delivered` because a completion with no result says nothing about what "
                + "was delivered, and the same argument applies here. The row still carries who closed it "
                + "and when regardless of whether a reason was given.\n\n"
                + "`.HumansOnly()`: a member able to close its own workflow could erase the evidence of "
                + "having failed at it, for the reason team reset already follows.\n\n"
                + "404 for an unknown team, and a different 404 - naming the correlation - for one with no "
                + "rows on THIS team, whether the correlation has no rows anywhere or belongs to another "
                + "team entirely. Never 403 for the second case: that would confirm another team's "
                + "correlation exists, which a caller with no business seeing it must never be told. Closing "
                + "an already-closed workflow is 204 and harmless: a second terminal row does not reopen "
                + "anything nothing has woken since.");

        // EVERY PENDING WAKE OF ONE TEAM, earliest due first: what a person's screens draw from, beside the
        // `wakes` on each member's snapshot, Kanban card and workflow row.
        app.MapGet("/api/teams/{team}/wakes", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, IWakeStore wakes, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });

            return Results.Ok(new { wakes = (await wakes.ForTeamAsync(stored, ct)).Select(w => w.View()).ToList() });
        })
            .WithTags("Members")
            .RequirePermit(Permits.Read)
            .WithSummary("The team's pending wakes")
            .WithDescription(
                "Each wake a member of this team asked for with `wake_me` and that has not fired or been "
                + "cancelled: `id`, `member`, `workflow`, `dueAt` and `askedAt` (UTC) and `note`. 404 for an "
                + "unknown team.");

        // A PERSON CANCELS ANY PENDING WAKE, from the member's card or the workflow. HumansOnly: a member
        // cancels only its own, through its own route above.
        app.MapPost("/api/teams/{team}/wakes/{id:long}/cancel", async (
            [Description(Describe.Team)] string team,
            [Description("The wake's id.")] long id,
            TeamRegistry teams, WakeMe wakes, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });

            var by = context.User.FindFirstValue(ClaimTypes.Email) ?? PrincipalClaims.From(context.User)?.Id ?? "console";
            var cancelled = await wakes.CancelAsync(id, by, "cancelled by a person", team: stored, ct: ct);

            return cancelled is null
                ? Results.NotFound(new { error = $"No pending wake {id} on this team. It has fired or been cancelled." })
                : Results.Ok(new { wake = cancelled.View(), cancelled = true });
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Cancel a pending wake")
            .WithDescription(
                "A person cancels any member's pending wake on this team. Appends `wake.cancelled` naming the "
                + "person. 404 for a wake that is not pending on this team. A machine principal is refused.");

        // A PERSON WAKING A TEAM'S MANAGER UNDER A SPECIFIC WORKFLOW. A workflow can otherwise only be
        // closed from inside a run already woken under it, so a workflow nobody is inside is a workflow nobody can close; nudge is what puts
        // somebody back inside it.
        //
        // THIS IS `tell` IN EVERY RESPECT EXCEPT WHO IS CALLING IT AND WHAT CAUSATION IT IS GIVEN - see
        // `tell`'s own comment for why an addressed instruction's TYPE (not its Source) is what
        // MessageTeam.Of reads. THE CAUSATION IS THE CORRELATION ITSELF, and that value is never
        // caller-suppliable here: it is exactly the correlation `WorkflowTeamOwnership` has just verified
        // belongs to this team. A nudge that rooted its own workflow (a null causation, the way `tell`
        // does by default) would be a NEW job wearing a rescue's name - the Manager would wake with no
        // history of the thread it is meant to be rescuing, and the projection would never connect the
        // two. (`tell` accepting an arbitrary CALLER-supplied causation is the platform's own separate
        // defect - see WorkflowTeamOwnership's doc comment - and does not apply here: nothing about this
        // value came from the request.)
        //
        // {team} in the route so TeamGate covers it structurally, exactly like close above.
        app.MapPost("/api/teams/{team}/workflows/{correlation:long}/nudge", async (
            [Description(Describe.Team)] string team,
            [Description(
                "The correlation id of the workflow to nudge, as returned by a `tell` or carried on any "
                + "message in the thread.")]
            long correlation,
            TeamRegistry teams, ContainerHost host, IMessageLog log, HttpContext context,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            // The IDENTICAL guard close uses, called rather than re-derived - see WorkflowTeamOwnership's
            // own doc comment for why a bespoke check here would silently reopen the hole it took three
            // rounds to close. Same 404-not-403 reasoning: a 403 would confirm another team's correlation
            // is real.
            if (!await WorkflowTeamOwnership.OwnsAsync(teams, host, log, stored, correlation, ct))
            {
                return Results.NotFound(new { error = $"No workflow with correlation {correlation}." });
            }

            // A TEAM CAN LEGITIMATELY HAVE NO MANAGER - a hire converted with no Agent chosen (see the
            // tenant-agent conversion) gets a team and no Manager, on purpose.
            // Naming the problem, rather than the bare "No workflow..." refusal above, is what tells a
            // reader this is not a typo in the correlation.
            var managerId = new ContainerId(stored, TeamRegistry.DefaultManagerName);

            if (host.Find(managerId) is not { } manager)
            {
                return Results.NotFound(new
                {
                    error = $"'{teams.LabelFor(stored)}' has no {TeamRegistry.DefaultManagerName} to nudge.",
                });
            }

            // The AUTHENTICATED principal, never a caller-supplied field - the same rule `tell` and close
            // follow.
            var from = PrincipalClaims.From(context.User)?.Id ?? "console";

            const string nudgeText =
                "A person nudged this workflow back to life. Look at everything under this thread and "
                + "either continue the work, declare it complete with workflow-complete, or say why it "
                + "should stop.";

            var parts = InstructionText.Split(nudgeText);

            await log.AppendAsync(
                new NewMessage(
                    MessageTypes.InstructionFor(manager.Id),
                    JsonSerializer.Serialize(new
                    {
                        instruction = nudgeText,
                        subject = parts.Subject,
                        body = parts.Body,
                    }),
                    from,
                    correlation),
                ct);

            if (!teams.IsPaused(stored))
            {
                return Results.NoContent();
            }

            return Results.Ok(new
            {
                paused = true,
                pauseNotice = MemberReportEndpoints.PauseNoticeFor(teams.LabelFor(stored)),
            });
        })
            .WithTags("Activity")
            .HumansOnly()
            .WithSummary("Wake this team's Manager under one workflow")
            .WithDescription(
                "Publishes an addressed instruction to the team's Manager whose CAUSATION IS THE "
                + "CORRELATION, so the Manager wakes with that thread's own history rather than the head "
                + "of a new one - the one affordance missing before this route: a workflow nobody is "
                + "currently inside could otherwise only be closed, never picked back up.\n\n"
                + "If the team is paused the instruction is still logged and the response says it will run "
                + "after resume, rather than reading like immediate execution.\n\n"
                + "LIKE `tell`, THIS QUEUES RATHER THAN INTERRUPTS: a 204 means the instruction was "
                + "recorded, not that the Manager has already woken to read it. It joins whatever that "
                + "member's own queue already holds, in order; and if the Manager is at its queue ceiling "
                + "the platform publishes `agentContainer.rejected` on this same workflow instead of accepting "
                + "the nudge - both are the platform's existing shape for every addressed instruction, "
                + "inherited rather than new here.\n\n"
                + "404 for an unknown team; the identical 404 close uses for a correlation with no rows on "
                + "THIS team, never 403 (see close's own description); and a third 404 naming "
                + $"'{TeamRegistry.DefaultManagerName}' when the team has none to wake - a reachable state, "
                + "not a bug.\n\n"
                + "`.HumansOnly()`: a member able to nudge its own team's workflow could splice a fresh "
                + "instruction into a thread on its own authority.");

        // A PERSON RELEASING ONE PAUSED WORKFLOW.
        //
        // THE UNIT IS ONE WORKFLOW AND NEVER THE TEAM. `POST /api/teams/{team}/resume` beside it releases
        // a TEAM; this releases one correlation, so a team with three open workflows that paused one gets
        // that one back and leaves the other two exactly as they were.
        //
        // IT IS A SEPARATE VERB FROM NUDGE ON PURPOSE. Resuming must not require knowing that `nudge`
        // is the verb - a person looking at something that says PAUSED
        // should press Resume. Mechanically the two rows it writes are a nudge plus a state change, but
        // that is an implementation the caller is not asked to know.
        //
        // AND IT CLEARS NO COUNTER, WHICH IS THE WHOLE POINT. There is no counter to clear: the spend
        // window is computed by `GetSpendSinceNudgeAsync` as "since the last addressed instruction whose
        // causation is the correlation", so writing such an instruction MOVES the window as a consequence
        // of what it is. Nothing here zeroes anything, there is no field to reset, and therefore no
        // mechanism a platform actor could reach for. `.HumansOnly()` is the other half: `PermitGate` refuses every machine principal
        // before this handler runs.
        app.MapPost("/api/teams/{team}/workflows/{correlation:long}/resume", async (
            [Description(Describe.Team)] string team,
            [Description(
                "The correlation id of the paused workflow to resume, as carried on every row in the "
                + "thread and returned as `correlation` by the team's `/workflows` route.")]
            long correlation,
            TeamRegistry teams, ContainerHost host, IMessageLog log, HttpContext context,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            // The IDENTICAL guard close, nudge and stop use, called rather than re-derived. Same
            // 404-not-403 reasoning: a 403 would confirm another team's correlation is real.
            if (!await WorkflowTeamOwnership.OwnsAsync(teams, host, log, stored, correlation, ct))
            {
                return Results.NotFound(new { error = $"No workflow with correlation {correlation}." });
            }

            var managerId = new ContainerId(stored, TeamRegistry.DefaultManagerName);

            if (host.Find(managerId) is not { } manager)
            {
                return Results.NotFound(new
                {
                    error = $"'{teams.LabelFor(stored)}' has no {TeamRegistry.DefaultManagerName} to wake.",
                });
            }

            // NOT PAUSED IS A 204 THAT WRITES NOTHING. Idempotent, and the case is reachable rather than
            // theoretical: the Console renders the button off `pausedAt`, which it holds from a fetch that
            // has already happened, so two people looking at the same row can both press it. A resume that
            // spent a manager invocation on a workflow nobody had paused would be the surprise.
            var thread = await log.ReadCorrelationAsync(correlation, ct);

            if (!WorkflowPause.IsPaused(thread))
            {
                return Results.NoContent();
            }

            // The AUTHENTICATED principal, never a caller-supplied field - the same rule `tell`, close and
            // nudge all follow.
            var from = PrincipalClaims.From(context.User)?.Id ?? "console";

            // THE STATE ROW FIRST, THE WAKE SECOND, AND THE ORDER IS LOAD-BEARING. If the instruction
            // landed first the pump could reach it while the correlation still read paused, and
            // `OverBudgetAsync` would refuse the very wake this route exists to deliver - a resume that
            // silently did nothing and burned a `Rejected` row saying so.
            await log.AppendAsync(
                new NewMessage(
                    MessageTypes.WorkflowResumed,

                    // `team` IS WRITTEN BY THE SERVER, from the value TeamGate has already checked and
                    // ExistingName resolved - never from anything the caller typed. `MessageTeam.Of` reads
                    // this field for this type, because the Source here is a person's bare id with no team
                    // in it; a row without it is invisible to the team that resumed the workflow.
                    JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [PayloadFields.Team] = stored,
                    }),
                    from,
                    correlation),
                ct);

            const string resumeText =
                "A person resumed this workflow after it paused for reaching its spend limit. Its spend "
                + "window starts again from here. Look at everything under this thread and either "
                + "continue the work, declare it complete with workflow-complete, or say why it should "
                + "stop.";

            var resumeParts = InstructionText.Split(resumeText);

            await log.AppendAsync(
                new NewMessage(
                    MessageTypes.InstructionFor(manager.Id),
                    JsonSerializer.Serialize(new
                    {
                        instruction = resumeText,
                        subject = resumeParts.Subject,
                        body = resumeParts.Body,
                    }),
                    from,

                    // THE CAUSATION IS THE CORRELATION, and this line is the entire rescue. It is what
                    // `GetSpendSinceNudgeAsync` windows on, so the figure the guard reads starts again
                    // from this row - which is what stops a resumed workflow from re-hitting the limit on
                    // its very next wake. The value is not caller-suppliable: it is the correlation
                    // `WorkflowTeamOwnership` has just verified belongs to this team.
                    correlation),
                ct);

            // AND EVERY OTHER BROWSER HOLDING THIS TEAM IS TOLD TO COME AND RE-READ.
            //
            // NOT FOR THE PERSON WHO PRESSED RESUME - they refetch locally the moment this returns. It is
            // for ANYBODY ELSE watching the same team: the Console has no timer and refetches a team's
            // workflows only from inside its `containerChanged` handler, so a console that was open when
            // the workflow paused goes on rendering PAUSED, and a live Resume button, over a workflow
            // somebody else has already released.
            //
            // The pump writes the pause and republishes there for the same reason - see
            // `ContainerHost.PauseWorkflowAsync`. `RepublishTeam` re-emits this team's snapshots UNCHANGED through the
            // event the hub already forwards: no new field on `ContainerSnapshot`, no new hub event.
            //
            // AFTER BOTH APPENDS, so a console that refetches on this frame reads a workflow that is
            // already released AND already woken, rather than one caught between its two rows. And below
            // the not-paused early return above, which writes nothing: a second press on a workflow
            // nobody had paused must not make every browser on the team refetch an unchanged projection.
            host.RepublishTeam(stored);

            // THE TEAM MAY ALSO BE PAUSED, which is a different thing and this route does not touch it.
            // Mirrors nudge: the rows are still written and the answer says so, rather than a 204 that
            // reads like the Manager is already awake.
            if (!teams.IsPaused(stored))
            {
                return Results.NoContent();
            }

            return Results.Ok(new
            {
                paused = true,
                pauseNotice = MemberReportEndpoints.PauseNoticeFor(teams.LabelFor(stored)),
            });
        })
            .WithTags("Activity")
            .HumansOnly()
            .WithSummary("Resume one paused workflow")
            .WithDescription(
                "Releases a workflow that PAUSED for reaching the per-workflow spend figure in force for "
                + "its team, and wakes the Manager under it.\n\n"
                + "**ONE WORKFLOW, NEVER THE TEAM.** `POST /api/teams/{team}/resume` releases a team; this "
                + "releases one correlation and leaves every sibling workflow exactly as it was.\n\n"
                + "**THE SPEND WINDOW STARTS AGAIN**, because the instruction this writes is caused by the "
                + "correlation - which is what the per-nudge counter measures from. A rescue that "
                + "immediately re-hit the limit would be no rescue. Nothing is reset and there is no "
                + "counter to reset: no platform-generated wake can reach this, because the route is "
                + "`.HumansOnly()` and because causing an instruction by the correlation is the only "
                + "mechanism there is.\n\n"
                + "204 when it resumed, and also 204 when the workflow was not paused - in which case "
                + "NOTHING is written, so pressing it twice costs one invocation rather than two.\n\n"
                + "If the TEAM is paused the rows are still written and the response says they will run "
                + "after the team resumes.\n\n"
                + "404 for an unknown team; the identical 404 close and nudge use for a correlation with "
                + "no rows on THIS team, never 403; and a third 404 naming "
                + $"'{TeamRegistry.DefaultManagerName}' when the team has none to wake.");

        // THE CORRELATION-SCOPED SIBLING OF THE PER-CONTAINER STOP ABOVE. Ends the run of every member
        // currently Running UNDER THIS WORKFLOW and leaves every other member alone - including one
        // Running under a DIFFERENT correlation on the same team, which is not this call's business, for
        // the identical reason `WorkflowBusyState` narrows on `CurrentCorrelation` rather than the team
        // alone.
        app.MapPost("/api/teams/{team}/workflows/{correlation:long}/stop", async (
            [Description(Describe.Team)] string team,
            [Description(
                "The correlation id of the workflow to stop, as returned by a `tell` or carried on any "
                + "message in the thread.")]
            long correlation,
            TeamRegistry teams, ContainerHost host, IMessageLog log, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            if (!await WorkflowTeamOwnership.OwnsAsync(teams, host, log, stored, correlation, ct))
            {
                return Results.NotFound(new { error = $"No workflow with correlation {correlation}." });
            }

            // A snapshot per RUNNING member of this team whose CURRENT workflow is this one - never the
            // team alone, or stopping one workflow would end every member's run on the whole team
            // regardless of what it was doing.
            //
            // A NARROW TOCTOU, ACCEPTED RATHER THAN CLOSED: `Snapshots()` and `Stop()` are two separate
            // reads of a container that keeps running between them, so a member that finishes THIS
            // workflow and picks up a NEW one in that instant has the new run cancelled instead - the
            // description below says so rather than promising more than this code does. Sub-millisecond
            // and triggered only by a person clicking Stop, so closing it needs a
            // stop-if-still-on-this-correlation check taken under the container's own lock - a new
            // synchronisation primitive on `MemberRuntime` for a race this narrow and this recoverable
            // (a second click ends whatever it actually caught). Documented rather than closed, as this
            // codebase does for its other narrow, human-triggered races.
            foreach (var snapshot in host.Snapshots())
            {
                if (!string.Equals(snapshot.Team, stored, StringComparison.OrdinalIgnoreCase)) continue;
                if (snapshot.State != ContainerState.Running) continue;
                if (snapshot.CurrentCorrelation != correlation) continue;

                host.Find(new ContainerId(snapshot.Team, snapshot.Id))?.Stop();
            }

            return Results.NoContent();
        })
            .WithTags("Activity")
            .HumansOnly()
            .WithSummary("Stop everything running under one workflow")
            .WithDescription(
                "Ends the run of every member currently Running under this correlation - the "
                + "correlation-scoped sibling of `POST .../containers/{name}/stop`. A member Running under "
                + "a DIFFERENT workflow, even on the same team, is left untouched, MODULO a narrow race: a "
                + "member observed here that finishes this workflow and starts a new one in the instant "
                + "before the stop reaches it has that NEW run cancelled instead - sub-millisecond, and "
                + "only ever triggered by a person's own click, so a second Stop resolves it.\n\n"
                + "204 whether or not anything was actually running: idempotent by nature, since by the "
                + "time a click reaches the server the workflow it targeted may already have gone quiet.\n\n"
                + "404 for an unknown team, and the identical 404 close and nudge use for a correlation "
                + "with no rows on THIS team, never 403.\n\n"
                + "`.HumansOnly()`, for the identical reason as the per-container stop and workflow close: "
                + "a member able to stop its own workflow's run could erase the evidence of having failed "
                + "at it.");

        // Carries no {team} route value either, and leaks the most if missed: every message of every
        // team, instruction text and agent output included, is what the board's activity feed renders.
        app.MapGet("/api/messages", async (
            [Description(
                "Return only messages after this sequence number. Pass the highest `seq` from the previous "
                + "call to poll forward; omit it to start from the beginning.")]
            long? after,

            [Description(
                "How many of the most recent messages to consider, before the team filter below. Default "
                + "200, clamped to 2000.\n\nA VIEWER-FACING KNOB rather than tuning: the cap is applied "
                + "BEFORE filtering to the teams a caller may see, so a quiet member on a busy team drops "
                + "out of the window entirely and its card renders empty - which is indistinguishable from "
                + "having never done anything. Raising this widens how far back the board can reach.")]
            int? take,

            [Description(
                "One member's history OLDER than this sequence number, newest first - the backward cursor "
                + "under the live window. Pass the last row's `seq` from the previous page. Needs `team` "
                + "and `member`, and cannot be combined with `after`. With it, `take` defaults to 50 and is "
                + "clamped to 1..200.")]
            long? before,

            [Description("With `before`: the member's team.")] string? team,
            [Description("With `before`: the member's current identifier.")] string? member,

            HttpContext context, IMessageLog log, TeamAccess access, TeamRegistry teams,
            CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            if (before is not null || team is not null || member is not null)
            {
                return await MemberHistory.ReadAsync(
                    principal, after, before, team, member, take, log, access, teams, ct);
            }

            // THE CATALOG IS THE SET, not a union over live subscriptions.
            //
            // A union over subscriptions would make the feed's coverage a property of the current ROSTER:
            // card events would reach the feed only because a Manager happened to subscribe to them, so a
            // team with no Manager - a shape this platform supports - could never see one.
            //
            // `EventCatalogCoverageTests` fails the build on a type the catalog does not declare, which is
            // what keeps a type nobody subscribes to from being invisible here.
            var types = FeedTypes.Explicit;

            var effective = await access.EffectiveTeamsAsync(principal, ct);
            var person = principal.Kind == PrincipalKind.User;

            // The NEWEST 200, not the oldest, and that word matters.
            //
            // Filtering by team happens below, after the cap. A window that started at `after` and ran
            // FORWARD would, on a cold load with after=0, be the first two hundred rows the log ever took -
            // so once a log holds 200 rows belonging to teams the caller cannot see (every deleted team
            // qualifies, and they are never coming back) the window would contain nothing else and this
            // route would answer `[]` permanently: a board showing "Nothing yet." over a running team.
            // A cap applied BEFORE a filter degrades to nothing rather than to less.
            //
            // Filtering by team in the query would solve it at the other end, and is refused: MessageTeam.Of
            // reads the TYPE for an instruction and the SOURCE for a container event, and pushing that rule
            // into SQL is a second copy of it in a second language, free to drift. Reading the newest rows gets the behaviour right without it.
            // Clamped rather than trusted. This is a per-viewer preference reaching a query cost, so the
            // ceiling is the server's to set - and the floor stops a zero or negative asking for nothing
            // and rendering an empty board that looks like a fault.
            var window = Math.Clamp(take ?? 200, 50, 2000);

            var messages = await log.ReadLatestAsync(after ?? 0, types, window, ct);

            return Results.Ok(messages.Where(m => MessageTeam.Of(m) switch
            {
                // Unattributable: a caller-supplied `from`, or a row predating qualified identity. Shown
                // to a person, hidden from every machine principal - the conservative reading, and consistent
                // with refusals being byte-identical to nonexistent below.
                //
                // A `kanban.card.*` ROW IS NOT ON THE `null` ARM. Those rows are published by a PERSON, so
                // their source is a bare user id with no `/`; `MessageTeam.Of` has a card arm that
                // resolves the team off the payload, so they are visible to anyone reaching that team,
                // exactly like the instruction and completion rows either side of them on the same feed -
                // rather than PEOPLE-ONLY.
                //
                // THE DIRECTION IS RIGHT AND THE WIDENING IS BOUNDED: it is `effective.Contains(team)`,
                // the same team set every other row on this feed is filtered by, and the team on a card
                // row is the server's own - written from a card resolved inside the `{team}` route value
                // `TeamGate` already checked. What would be wrong is the opposite state: a person edits a card on their own team and cannot see the row they just wrote.
                // Pinned by `FilteredReadTests.A_card_event_reaches_the_teams_members_and_nobody_else`.
                null => person,
                var team => effective.Contains(team),
            }));
        })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("Recent activity across every team you can see")
            .WithDescription(
                "The append-only message log: instructions going in, and what members published coming "
                + "back. This is what the board's activity feed renders.\n\n"
                + "At most 200 rows per call, and the cap applies BEFORE the caller's teams are filtered "
                + "out - so you may receive fewer than 200 and should page on `seq` rather than assume a "
                + "short page means the end.\n\n"
                + "Messages that cannot be attributed to a team are shown only to people.\n\n"
                + "**Backward, one member** (`before`, `team`, `member`): that member's history below "
                + "`before`, NEWEST FIRST - instructions addressed to it and the feed rows it published - "
                + "at most `take` (default 50, clamped to 1..200). The next cursor is the last row's `seq`; "
                + "an empty page is the end, which is the first message the member ever received. A row "
                + "appended meanwhile is above every cursor and never enters or shifts an older page. 400 "
                + "when `before`, `team` and `member` are not all given or `after` is given with them; 404 "
                + "for an unknown team or member, or one the caller cannot see.");

        app.MapGet("/api/workflows/{correlationId:long}", async (
            [Description(
                "The correlation id shared by every message in one workflow, as returned by a `tell` or "
                + "carried on any message in the thread.")]
            long correlationId,
            HttpContext context, IMessageLog log, TeamAccess access, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            var effective = await access.EffectiveTeamsAsync(principal, ct);
            var person = principal.Kind == PrincipalKind.User;

            var messages = await log.ReadCorrelationAsync(correlationId, ct);

            // A workflow belonging to another team and one that never existed both come back `200 []` -
            // there is no second code path distinguishing "not yours" from "not there".
            // `kanban.card.*` rows are on this thread too, and they are team-visible rather than
            // people-only - see the same switch on `/api/activity` for what moved and why.
            var visible = messages.Where(m => MessageTeam.Of(m) switch
            {
                null => person,
                var team => effective.Contains(team),
            }).ToList();

            return Results.Ok(visible);
        })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("Every message in one workflow")
            .WithDescription(
                "One thread of work, in order - the manager's dispatch, the worker's completion, the "
                + "manager's reaction to it, and so on. Correlation is stamped by the containers "
                + "themselves, never by a prompt, so the thread cannot be broken by an agent forgetting to "
                + "carry it.\n\n"
                + "A workflow belonging to another team and a workflow that never existed both answer "
                + "`200 []`. There is deliberately no second code path telling \"not yours\" apart from "
                + "\"not there\".");

        app.MapGet("/api/workflows/{correlationId:long}/wait", async (
            [Description(
                "The correlation id shared by one workflow, as returned by `tell`.")]
            long correlationId,

            [Description(
                "How long to wait in seconds before answering `timeout`. Defaults to 120 and is capped at 900.")]
            int? timeoutSeconds,
            HttpContext context, IMessageLog log, TeamAccess access, MessageWaitRegistry waits,
            ContainerHost host, IPendingDeliveries pending,
            CancellationToken ct) =>
        {
            const int defaultTimeoutSeconds = 120;
            const int maxTimeoutSeconds = 900;

            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            if (timeoutSeconds is <= 0)
            {
                return Results.BadRequest(new { error = "timeoutSeconds must be greater than zero." });
            }

            var effective = await access.EffectiveTeamsAsync(principal, ct);
            var person = principal.Kind == PrincipalKind.User;
            var timeout = Math.Min(timeoutSeconds ?? defaultTimeoutSeconds, maxTimeoutSeconds);
            var terminalTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                // Section 5.1 ("The team that went quiet - v.2"): agentContainer.completed is deliberately
                // excluded. It is published after each leg and would end a wait while the workflow is
                // still in progress, returning confidently wrong.
                MessageTypes.WorkflowCompleted,

                // `WorkflowOpenSql` already treats this as terminal for the open-count projection - without
                // it here, "is this workflow over" has two readers that disagree, and a person closing a
                // STUCK workflow (precisely because nobody will ever finish it) leaves a waiter hanging up
                // to the full timeout instead of ending immediately.
                MessageTypes.WorkflowClosed,
                MessageTypes.Blocked,
                MessageTypes.NeedsDecision,
                MessageTypes.Failed,
            };

            bool VisibleToCaller(Message message) => MessageTeam.Of(message) switch
            {
                null => person,
                var team => effective.Contains(team),
            };

            async Task<Message?> CurrentTerminalAsync()
            {
                var messages = await log.ReadCorrelationAsync(correlationId, ct);

                return messages.LastOrDefault(message =>
                    terminalTypes.Contains(message.Type) && VisibleToCaller(message));
            }

            if (await CurrentTerminalAsync() is { } terminal)
            {
                return Results.Ok(WorkflowWaitReply.From(correlationId, terminal, timeout));
            }

            using var registration = waits.Register(message =>
                message.CorrelationId == correlationId
                && terminalTypes.Contains(message.Type)
                && VisibleToCaller(message));

            if (await CurrentTerminalAsync() is { } settled)
            {
                return Results.Ok(WorkflowWaitReply.From(correlationId, settled, timeout));
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

            try
            {
                var arrived = await registration.WaitAsync(timeoutCts.Token);
                return Results.Ok(WorkflowWaitReply.From(correlationId, arrived, timeout));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var all = await log.ReadCorrelationAsync(correlationId, ct);
                var visible = all.Where(VisibleToCaller).ToArray();
                var team = visible
                    .Select(MessageTeam.Of)
                    .FirstOrDefault(name => name is not null);

                if (team is null)
                {
                    return Results.Ok(WorkflowWaitReply.Timeout(correlationId, timeout, TimeoutVerdict.Working));
                }

                if (visible.Any(message =>
                        string.Equals(message.Type, MessageTypes.WorkflowCompleted, StringComparison.Ordinal)
                        || string.Equals(message.Type, MessageTypes.WorkflowClosed, StringComparison.Ordinal)))
                {
                    return Results.Ok(WorkflowWaitReply.Timeout(correlationId, timeout, TimeoutVerdict.Working));
                }

                var busy = await TeamBusyState.DescribeAsync(team, host, pending, excluded: null, ct);
                if (busy.Count != 0)
                {
                    return Results.Ok(WorkflowWaitReply.Timeout(correlationId, timeout, TimeoutVerdict.Working));
                }

                var snapshots = host.Snapshots()
                    .Where(snapshot => string.Equals(snapshot.Team, team, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (snapshots.Length == 0)
                {
                    return Results.Ok(WorkflowWaitReply.Timeout(correlationId, timeout, TimeoutVerdict.Working));
                }

                var teamTerminals = new HashSet<string>(StringComparer.Ordinal)
                {
                    MessageTypes.Completed,
                    MessageTypes.Blocked,
                    MessageTypes.Failed,
                    MessageTypes.NeedsDecision,
                };
                var latestTerminals = new List<Message>(snapshots.Length);
                foreach (var snapshot in snapshots)
                {
                    var rows = await log.ReadForContainerAsync($"{snapshot.Team}/{snapshot.Id}", 0, 200, ct);
                    var latestTerminal = rows.FirstOrDefault(message => teamTerminals.Contains(message.Type));
                    if (latestTerminal is null || latestTerminal.Type != MessageTypes.Completed)
                    {
                        return Results.Ok(WorkflowWaitReply.Timeout(correlationId, timeout, TimeoutVerdict.Working));
                    }

                    latestTerminals.Add(latestTerminal);
                }

                var idleSince = latestTerminals.OrderByDescending(row => row.Seq).First().OccurredAt;
                return Results.Ok(WorkflowWaitReply.Timeout(
                    correlationId,
                    timeout,
                    TimeoutVerdict.Quiet,
                    idleSince));
            }
        })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("Wait for one workflow to end")
            .WithDescription(
                "Holds one request until this workflow ends, or the timeout returns `timeout`.\n\n"
                + "Workflow-ending rows are `workflow.completed`, `workflow.closed`, `agentContainer.blocked`, "
                + "`agentContainer.needsDecision` and "
                + "`agentContainer.failed` under this correlation.\n\n"
                + "Timeout is a normal answer and returns 200.");
    }
}
