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

internal static class MemberReportEndpoints
{
    public static void Map(WebApplication app, TenantSettings tenantSettings)
    {
        /// Telling a container something is publishing an addressed instruction. There is no other way in,
        /// which is what keeps a container event-driven rather than something with two doors.
        ///
        /// TEAM-SCOPED, because a container is identified by (team, name) - an unqualified route could not
        /// say which team's Manager it meant. It also puts the route under /api/teams/{team}/ with
        /// everything else a team owns, so a per-team gate covers it BY PATH rather than needing a special
        /// case inside this handler.
        app.MapPost("/api/teams/{team}/containers/{name}/tell", async (
            [Description(Describe.Team)] string team,
            [Description(
                "The member's name within that team, matched case-insensitively. A team's manager is "
                + "called `Manager` unless it has been relabelled.")]
            string name,
            Tell request, TeamRegistry teams, ContainerHost host,
            IMessageLog log, IPendingDeliveries pending, IOutcomeStore outcomes, HttpContext context, CancellationToken ct) =>
        {
            // The STORED spelling, so the id built here is the one the container was registered under
            // however the caller capitalised the team.
            if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });

            // AN ARCHIVED TEAM DOES NO WORK: nothing is appended, and the sentence names Unarchive.
            if (teams.IsArchived(stored)) return Results.Conflict(new { error = TeamArchive.Refusal(teams.LabelFor(stored)) });

            // Checked BEFORE the id is constructed: ContainerId's constructor THROWS on an illegal name, and
            // an unhandled ArgumentException from a route segment is a 500 where a 404 is correct.
            if (!ContainerId.IsLegalName(name)) return Results.NotFound(new { error = $"No member '{name}'." });

            var id = new ContainerId(stored, name);

            // The FOUND container, captured rather than discarded, because the type below must be built from
            // ITS id and not from the caller's spelling. ContainerId equality is case-insensitive, so
            // `tell DEV1` finds a container named `dev1` - but SqliteMessageStore.ReadAfterAsync matches
            // `type IN (...)` under SQLite's default BINARY collation, which is not. Building the type from
            // `id` would append `agentContainer.instruction.Alpha/DEV1` against a subscription row reading
            // `agentContainer.instruction.Alpha/dev1`, so the call would return 200, write a row, and wake
            // nothing. A success reported for work that silently did not happen is the worst outcome available:
            // the person has no reason to look.
            if (host.Find(id) is not { } container) return Results.NotFound(new { error = $"No member '{name}'." });
            if (string.IsNullOrWhiteSpace(request.Instruction)) return Results.BadRequest(new { error = "Say something." });

            // WORK GOES THROUGH THE COORDINATOR, AND THE PLATFORM SAYS SO RATHER THAN A SKILL.
            // A skill can ask for this but cannot enforce it, so without this one ambiguous instruction
            // pasted into a Concierge would be enough to root a whole workflow at a member while the
            // Manager - idle, subscribed and able - knew nothing about it.
            //
            // AUTHORITY IS IDENTITY, NOT THE PERMIT, the shape `workflow.completed` already uses. The
            // permit still says a Concierge MAY tell; this bounds WHOM, exactly as the hiring allowlist
            // bounds what a manager's hire may RUN.
            //
            // A USER FALLS THROUGH, AND SO DOES A TICKET WITH NO READABLE KIND. A person addressing a
            // member directly to debug or unstick it is legitimate and is the one caller who can see the
            // whole board. An unreadable kind cannot reach here anyway - permits ride the same claims, so
            // `PermitGate` refused it already - and if that ever changes, this must be revisited rather
            // than left to the pattern below.
            //
            // ASKED THROUGH `host.Find`, THE SAME LOOKUP THAT FOUND THE TARGET FOUR LINES UP. Whether a
            // container is here has one answer; reading the registry's summaries for the Manager while
            // reading the host for the target is two, and they can disagree. It is also the safe direction:
            // a Manager the host does not hold cannot be told either, so refusing on its behalf would
            // refuse a dispatch that has no working alternative.
            if (PrincipalClaims.From(context.User)
                is { Kind: PrincipalKind.Concierge or PrincipalKind.Container } caller)
            {
                var manager = new ContainerId(stored, TeamRegistry.DefaultManagerName);

                // A Manager telling its own members is the whole point of the platform, so it is excluded
                // by identity rather than by kind. Short-circuited: only a Container has a parseable
                // ContainerId, and a Concierge's id is not one.
                var callerIsManager = caller.Kind == PrincipalKind.Container
                    && ContainerId.Parse(caller.Id).Equals(manager);

                // A team with NO Manager is excluded first: the concierge skill documents that shape and
                // tells a Concierge to sequence the job itself, and only a hand-built team produces one.
                if (host.Find(manager) is not null && !container.Id.Equals(manager) && !callerIsManager)
                {
                    return Results.Json(
                        new { error = TellRefusals.SkipsManager },
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }

            // The AUTHENTICATED principal, never `request.From`. That field is caller-supplied and
            // unvalidated, so it could name any container on any team - including one the caller holds no
            // authority over - which is exactly why MessageTeam.Of reads an instruction's TYPE for its team and
            // never its Source.
            //
            // That rule STAYS even though this line makes it less load-bearing: a future caller-supplied
            // path would reopen the hole in silence, and the rule is the only thing written down about it.
            var from = PrincipalClaims.From(context.User)?.Id ?? "console";

            // THE HOP'S OWN THREAD. Correlation is inherited from the causing message at append time, so
            // this one value is what makes a manager's dispatch part of the workflow it is answering rather
            // than the head of a new one. Without it every dispatch would start a fresh correlation and
            // "one correlation id on both sides of the hop" would not be something any amount of care by the
            // agent could achieve - each tool call is a fresh request from a child process.
            //
            // Blank is absent, not zero: an unset HARNESS_CAUSATION reaches the tool as null and a cleared
            // one as "", and the two mean the same thing here.
            long? causation = null;

            // The workflow this instruction will join, known only when it answers something: an
            // instruction with no causation heads a new workflow, where nothing can already be queued.
            long? joins = null;

            // A team container that names no causation dispatches inside the run it is in. See
            // `TellCausation`; the depth and budget checks below then apply to it like any other.
            var callerPrincipal = PrincipalClaims.From(context.User);
            var requestedCausation = TellCausation.Resolve(
                request.Causation,
                callerPrincipal?.Kind,
                callerPrincipal is { Kind: PrincipalKind.Container } machine
                    && host.Find(ContainerId.Parse(machine.Id)) is { } callerContainer
                        ? callerContainer.CurrentCausation
                        : null);

            if (!string.IsNullOrWhiteSpace(requestedCausation))
            {
                // Refused rather than ignored. Silently dropping an unparseable value would put the
                // instruction at the head of its own workflow - and it would look identical to it
                // working.
                if (!long.TryParse(
                        requestedCausation, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
                {
                    return Results.BadRequest(new { error = $"'{requestedCausation}' is not a message seq." });
                }

                // The message must EXIST, and this is a real check rather than belt and braces:
                // `messages.causation_seq` is a foreign key, so an unknown seq would otherwise surface as a
                // constraint violation out of the append - a 500 for what is a caller error.
                if (await log.FindAsync(seq, ct) is not { } found)
                {
                    return Results.BadRequest(new { error = $"No message #{seq} to answer." });
                }

                // THE LOOP BOUND. The causing message's depth plus one is what this instruction would be
                // stored at, so the check is on the message about to be written rather than on its parent.
                if (tenantSettings.CausationTooDeep(found.Depth + 1))
                {
                    // 409 rather than 400: nothing about the request is malformed, and the caller is not
                    // going to fix it by sending different JSON. The body is written to be READ BY AN
                    // AGENT, because it is - the tool hands the refusal back verbatim - so it says
                    // what to do instead rather than only what went wrong.
                    return Results.Conflict(new { error = WorkflowBounds.DepthRefusal(found.Depth + 1) });
                }

                // THE SPEND BOUND. Check if this workflow's cumulative spend exceeds the limit.
                // Unmeasured spend never convicts: if all runs in this workflow have no usage recorded,
                // the check cannot refuse, even if the limit is 0. Partial measurement bounds only the
                // measured part.
                // SINCE THE LAST NUDGE, matching what the wake asks. Two bounds reading two different
                // figures would refuse a dispatch the pump would have allowed, or the reverse.
                //
                // AND THROUGH THE SAME RESOLVER THE WAKE USES. Reading the raw instance figure here would
                // ignore the team's own per-workflow budget; a team whose figure is LOWER than the
                // instance one would then have a dispatch accepted here and refused at the wake -
                // precisely the disagreement the paragraph above warns about, only from the other side.
                // `null` is UNLIMITED and is the only spelling of it that reaches here.
                var effectiveBudget = teams.EffectiveWorkflowBudgetFor(stored);
                var spend = await log.GetSpendSinceNudgeAsync(found.CorrelationId, ct);

                if (effectiveBudget is { } bound
                    && spend.RunsWithMeasuredUsage > 0 && spend.TokensSpent > bound)
                {
                    return Results.Conflict(new { error = WorkflowBounds.BudgetRefusal(spend.TokensSpent, bound) });
                }

                causation = seq;
                joins = found.CorrelationId;
            }

            // A DUPLICATE IS NAMED, NOT HELD. A Manager that re-sends an instruction already queued to a
            // busy member when its predecessor is accepted pays a member run and a Manager wake to hear it
            // repeated. The reply names the queued seq so the sender learns that - but the row is
            // still appended below, because `tell` is never held: text that matches is not proof the sender
            // meant the same thing, and a refusal the sender misreads loses work silently.
            var duplicate = joins is { } correlation
                ? await QueuedInstructions.DuplicateOfAsync(pending, log, container.Id, correlation, request.Instruction, ct)
                : null;

            // THE OUTCOME OF A NEW WORKFLOW, named at its root: an id, or the exact name of a live outcome.
            // An instruction that joins a workflow has none to name - that workflow's outcome is its own.
            Outcome? rootOutcome = null;
            if (!string.IsNullOrWhiteSpace(request.Outcome))
            {
                if (causation is not null)
                {
                    return Results.BadRequest(new
                    {
                        error = $"`outcome` names the outcome of a NEW workflow, and this instruction joins workflow {joins}. "
                            + "Set that workflow's outcome with the `outcome` tool instead.",
                    });
                }

                rootOutcome = await outcomes.ResolveLiveAsync(request.Outcome, ct);
                if (rootOutcome is null)
                {
                    return Results.BadRequest(new { error = TriggerCreation.OutcomeRefusal(request.Outcome.Trim()) });
                }
            }

            // Three fields, and `instruction` is the one that must never leave.
            //
            // It is what every reader reads, and the log is APPEND-ONLY and REPLAYED: older rows may carry
            // it alone, so a projection meets a mixed history and needs both shapes anyway. Writing the split as well only means the board and the browser do
            // not have to re-derive it row by row - it does not make `instruction` redundant, and dropping
            // it would silently rewrite a history nothing can rewrite.
            //
            // VERBATIM and COMPLETE, not the trimmed or split text: the agent is handed this field, and an
            // agent told less than the person wrote fails in the way nothing can see.
            var parts = InstructionText.Split(request.Instruction, request.Subject);

            var instructionRow = new NewMessage(
                    MessageTypes.InstructionFor(container.Id),
                    JsonSerializer.Serialize(new
                    {
                        instruction = request.Instruction,
                        subject = parts.Subject,
                        body = parts.Body,

                        // ABSENT, NOT NULL, when no card is claimed. The projector asks whether the field
                        // is a non-empty string, so a null would behave the same - but every row on this
                        // append-only log is replayed forever, and writing `"card":null` onto every
                        // instruction ever sent would put a field nobody reads on the whole history.
                        card = string.IsNullOrWhiteSpace(request.Card) ? null : request.Card.Trim(),
                    }),
                    from,
                    causation);

            // THE ROOT, ITS LINK AND ITS TENANT ROW IN ONE TRANSACTION (`how: tell`): a person's own tell
            // is theirs, and the Concierge's is an agent's, which a Manager may later move. The
            // `workflow.outcome-changed` row names the person, or the member for an agent's tell; when it
            // cannot be written, neither the instruction nor the link lands.
            var tellerIsPerson = callerPrincipal?.Kind == PrincipalKind.User;
            var message = rootOutcome is null
                ? await log.AppendAsync(instructionRow, ct)
                : (await log.AppendWithinAsync(
                    (_, _, _) => Task.FromResult<NewMessage?>(instructionRow),
                    async (connection, transaction, stored, token) =>
                    {
                        var sqlite = (Microsoft.Data.Sqlite.SqliteConnection)connection;
                        var within = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;

                        await OutcomeLinks.WriteAsync(
                            sqlite, within, stored.CorrelationId, rootOutcome.Id, container.Id.Team,
                            tellerIsPerson ? context.User.FindFirstValue(ClaimTypes.Email) ?? from : from,
                            tellerIsPerson ? OutcomeActorKind.Person : OutcomeActorKind.Member,
                            OutcomeLinkHow.Tell,
                            token);

                        var detail = new { team = container.Id.Team, workflow = stored.CorrelationId, how = OutcomeLinkHow.Tell };
                        await TenantAuditRow.AppendAsync(sqlite, within,
                            tellerIsPerson
                                ? TenantLogging.Row(context, TenantActions.WorkflowOutcomeChanged, rootOutcome.Id, rootOutcome.Name, detail)
                                : new TriggerAudit(from, null, TenantActions.WorkflowOutcomeChanged, rootOutcome.Id, rootOutcome.Name,
                                    JsonSerializer.Serialize(detail)),
                            token);
                    },
                    ct))!;
            var paused = teams.IsPaused(stored);
            return Results.Ok(new
            {
                message.Seq,
                message.CorrelationId,
                paused,
                pauseNotice = paused ? PauseNoticeFor(teams.LabelFor(stored)) : null,
                duplicateOf = duplicate?.Seq,
                duplicateNotice = duplicate is null ? null : QueuedInstructions.DuplicateNotice(container.Id.Name, duplicate.Seq),
            });
        })
            .WithTags("Members")
            .RequirePermit(Permits.Tell)
            .WithSummary("Tell a member to do something")
            .WithDescription(
                "The only way into a member, which is what keeps it event-driven rather than something with "
                + "two doors. It appends an addressed instruction to the message log; the member's "
                + "container is woken by the delivery pump and enqueues an invocation of its agent.\n\n"
                + "When the team is paused the row is still appended and the 200 says so, because queued "
                + "work that does not run yet must not read as though it already had.\n\n"
                + "**Nothing blocks and there is no `wait`.** The 200 means the instruction was recorded, "
                + "never that the work is done - by design, since a caller that could wait on a worker is a "
                + "caller that will. Follow the returned `correlationId` through `GET "
                + "/api/workflows/{correlationId}` to see what happened; every message the instruction goes "
                + "on to cause carries it.\n\n"
                + "When the text matches an instruction already queued to this member in the same workflow, "
                + "`duplicateOf` names that queued seq and `duplicateNotice` says so in a sentence. The "
                + "instruction is appended all the same: a match is reported, never held.\n\n"
                + "400 for an empty instruction; 404 for an unknown team or member.");

        // What a member says about ITSELF while it works. The one route a permit-less-by-default member
        // can reach, and the reason every member gets a credential at all.
        //
        // It carries {team} deliberately, even though the server derives everything it trusts from the
        // authenticated principal: that declaration is what puts this inside TeamGate structurally rather
        // than by anyone remembering. TeamGate's own comment is explicit that a route naming its team any
        // other way is ungated, silently.
        app.MapPost("/api/teams/{team}/containers/{name}/progress", async (
            [Description(Describe.Team)] string team,
            [Description("The member reporting. Must be the caller itself.")] string name,
            ReportProgress request,
            HttpContext context, TeamRegistry teams, ContainerHost host, IMemberReports reports,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            var id = new ContainerId(stored, name);

            // The FOUND container's id, never the caller's spelling - a message type is matched under
            // SQLite's BINARY collation while ContainerId equality folds case, so building a type from what
            // was typed is how a call answers 200 and nothing ever happens.
            if (host.Find(id) is not { } container)
            {
                return Results.NotFound(new { error = $"No member '{name}'." });
            }

            // THE SPOOF CHECK, and the permit does not cover it: every member holds Progress, and a
            // container narrating another container's work would appear on that container's card with
            // nothing to say where it came from. Compared under ContainerId equality, which folds case on
            // both halves, so a member is not refused for capitalising its own name differently.
            if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.Container } caller
                || !ContainerId.Parse(caller.Id).Equals(container.Id))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.Status))
            {
                // Refused rather than recorded. A blank line on a card is indistinguishable from "it has
                // not said", which is the exact ambiguity progress reporting exists to remove.
                return Results.BadRequest(new { error = "Say what you are doing." });
            }

            // The seq of the message being run. Correlation is inherited from causation at append time,
            // so this one value puts the report inside the workflow that produced it; passing the
            // correlation instead would head a new one.
            // THE EFFECTS LIVE IN MemberReports, which a plugin member's stdout reaches too: the row
            // (flagged `whileIdle` from a process that outlived its run), the card push, the idle clock.
            await reports.ProgressAsync(container.Id, request.Status, ct);

            return Results.NoContent();
        })
            .WithTags("Members")
            .RequirePermit(Permits.Progress)
            .WithSummary("Say what you are doing")
            .WithDescription(
                "A member narrating its own run, so the board can say more than `running`. Each line joins "
                + "the card's activity feed and stays there; nothing replaces anything. "
                + "**Only about yourself.** The caller must BE the member it names; anything else is 403. "
                + "Every member holds this permit, so the identity check rather than the permit is what "
                + "stops one member narrating another's work. "
                + "400 for an empty status; 404 for an unknown team or member.");

        // The other half of what a member may say about itself: that it has STOPPED without finishing.
        //
        // Everything structural here is the progress route's, on purpose. It carries {team} so TeamGate
        // covers it by declaration rather than by memory; it builds the type from the FOUND container's id,
        // because a type is matched under BINARY collation while ContainerId equality folds case; and it
        // asserts the caller IS the container it names, because the permit cannot - every member holds
        // Progress.
        //
        // It reuses that permit rather than minting a Blocked one. The permit bounds "may this container
        // narrate its own state on its own card", and both verbs are exactly that. A new permit would need
        // every existing member's `permits` column migrated, and a member that could not say it had given
        // up would fail SILENTLY - this feature failing in the precise way it exists to prevent.
        // STOP THIS RUN. The only way to end a run that has gone wrong without deleting the
        // member, which would also destroy a workspace and a row.
        //
        // HumansOnly, deliberately. A manager holding this could stop its colleagues' work, and the blast
        // radius of a mistake there is somebody else's half-finished job; a person clicking a button on a
        // card they are looking at is the case this exists for. It is inside TeamGate by declaring {team},
        // so the person must hold the team as well.
        //
        // It is NOT idempotent-by-pretending: an idle container answers 200 saying nothing was running,
        // because by the time a click arrives the run may have finished on its own and that is not an error.
        app.MapPost("/api/teams/{team}/containers/{name}/stop", async (
            [Description(Describe.Team)] string team,
            [Description("The member whose current run should be ended.")] string name,
            TeamRegistry teams, ContainerHost host, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            // The FOUND container, never the caller's spelling - the same rule every other route on this
            // path follows, and for the same reason.
            if (host.Find(new ContainerId(stored, name)) is not { } container)
            {
                return Results.NotFound(new { error = $"No member '{name}'." });
            }

            var stopped = container.Stop();

            await Task.CompletedTask;

            return Results.Ok(new
            {
                stopped,
                message = stopped
                    ? $"{container.Id.Name} was stopped."
                    : $"{container.Id.Name} was not running.",
            });
        })
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Stop the run in flight")
            .WithDescription(
                "Ends the invocation this member is currently running and leaves the member able to take "
                + "its next instruction. The run is reported as `agentContainer.failed`, saying a person stopped "
                + "it - which is deliberately different wording from a timeout or a Host shutdown, because "
                + "the three are fixed in different places.\n\n"
                + "**Not a delete.** Removing the member also ends a run, and destroys a workspace and a row "
                + "with it.\n\n"
                + "`stopped` is false when nothing was running. That is a 200, not an error: by the time a "
                + "click reaches the server the run it was aimed at may have finished on its own.")
            .Produces<object>();

        app.MapPost("/api/teams/{team}/containers/{name}/blocked", async (
            [Description(Describe.Team)] string team,
            [Description("The member reporting. Must be the caller itself.")] string name,
            ReportBlocked request,
            HttpContext context, TeamRegistry teams, ContainerHost host, IMemberReports reports,
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

            if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.Container } caller
                || !ContainerId.Parse(caller.Id).Equals(container.Id))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                // "Stopped, and here is nothing" is the least useful thing a card can say: a reader has to
                // go to the log anyway, which is the trip this whole route removes.
                return Results.BadRequest(new { error = "Say why you stopped." });
            }

            // A DEFERRAL NAMES ITS ITEM: deferring "the run" means nothing.
            if (request.Defer == true && request.Item is null)
            {
                return Results.BadRequest(new { error = "Name the item to defer: its number in this run's prompt." });
            }

            // THE EFFECTS LIVE IN MemberReports: one batch item closed by its own row, one deferred to its
            // own next run, or the whole run marked. An `item` that is not in this run is refused there, for
            // every caller, and so is deferring the only item of a run.
            var outcome = request.Defer == true
                ? await reports.DeferAsync(container.Id, request.Item!.Value, request.Reason, ct)
                : await reports.BlockedAsync(container.Id, request.Reason, request.Item, ct);

            if (!outcome.Accepted) return Results.BadRequest(new { error = outcome.Refusal });

            return Results.NoContent();
        })
            .WithTags("Members")
            .RequirePermit(Permits.Progress)
            .WithSummary("Say you have stopped without finishing")
            .WithDescription(
                "A member reporting that it is giving up on the work it was given, and why. Distinct from "
                + "`agentContainer.failed`, which is the PLATFORM reporting a run that did not complete: this is "
                + "a run that finished normally and produced no result. Without it, a member that abandons "
                + "a job and one that delivers are both idle with exit code 0. `item` is optional and, when "
                + "present, names one batched prompt item (1-based) to abandon instead of the whole run. "
                + "`defer` true with an `item` DEFERS that item instead: it is not closed when this run ends "
                + "but delivered again as its own next run, in the same workflow, and the run's terminal rows "
                + "list it as `deferred` with the reason. Deferring the only item of a run is refused.\n\n"
                + "The reason stays on the card until something wakes this member again. "
                + "**Only about yourself.** The caller must BE the member it names; anything else is 403. "
                + "Every member holds this permit, so the identity check rather than the permit is what "
                + "stops one member marking another. "
                + "400 for an empty reason, an invalid `item`, or a deferral that is refused; 404 for an "
                + "unknown team or member.");

        // THE HAND-BACK. A worker saying its own part is done and nothing is owed.
        //
        // MODELLED ON `blocked` ABOVE AND AUTHORISED THE SAME WAY - the `Progress` permit every member
        // already holds, with the identity check rather than the permit stopping one member speaking for
        // another. That precedent is deliberate: a permit nobody's column carries is a
        // verb nobody can reach, and this one has to be reachable by every worker on its worst day.
        //
        // IT IS NOT `workflow-complete` AND DOES NOT BECOME IT. That route is a MANAGER'S declaration,
        // asserted on identity, refused while the team is still working, and nothing here touches it - a
        // member that hands back is still refused a declaration by exactly the same words. This hands work
        // over; only a Manager ends a workflow.
        //
        // WHAT IS NEW IS THAT SOMETHING IS SUBSCRIBED TO THE ROW. `blocked` and `progress` wake nobody;
        // `agentContainer.handback` is in `TeamRegistry.ManagerSubscriptions`, so the append below wakes
        // this team's Manager through the ordinary pump - on the SAME correlation, because the row is
        // caused by `container.CurrentCausation` like every other thing this run publishes. Both of the
        // pump's guards still do their work unchanged: a Manager that hands back does not wake itself (a
        // container never reacts to its own publications) and no other team hears this at all (a container
        // is woken only by its own team).
        app.MapPost("/api/teams/{team}/containers/{name}/handback", async (
            [Description(Describe.Team)] string team,
            [Description("The member handing back. Must be the caller itself.")] string name,
            ReportHandback request,
            HttpContext context, TeamRegistry teams, ContainerHost host, IMemberReports reports,
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

            // Identity, not permit - `blocked`'s rule exactly. Every member holds `Progress`, so what stops
            // one member handing back on another's behalf is that the caller must BE the member it names.
            if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.Container } caller
                || !ContainerId.Parse(caller.Id).Equals(container.Id))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.Delivered))
            {
                // "Done, and here is nothing" is the hand-back's version of the empty blocked reason, and
                // it is worse: this row WAKES a manager, which then has to go to the log to find out what
                // it was woken for. Refused for the reason `workflow-complete` refuses a blank `delivered`.
                return Results.BadRequest(new { error = "Say what you finished." });
            }

            // THE EFFECTS LIVE IN MemberReports: the row that wakes the Manager, and the last-handback
            // words that are never cleared.
            await reports.HandbackAsync(container.Id, request.Delivered, ct);

            return Results.NoContent();
        })
            .WithTags("Members")
            .RequirePermit(Permits.Progress)
            .WithSummary("Hand your finished part back")
            .WithDescription(
                "A member reporting that the work it was given is DONE and nothing is owed. A success, and "
                + "the counterpart of `blocked`: that one says the agent gave up, and a member that uses it "
                + "on finished work teaches its manager the opposite of the truth and marks its card as a "
                + "team in trouble.\n\n"
                + "**It does not end the workflow.** `workflow-complete` is a Manager's declaration and "
                + "stays refused to a worker, unchanged. This hands the work over: it wakes this team's "
                + "Manager on the same workflow, which then decides what to declare.\n\n"
                + "The words are kept as this member's last hand-back and, unlike a blocked reason, are NOT "
                + "cleared when it is next woken - a delivery does not go stale.\n\n"
                + "**Only about yourself.** The caller must BE the member it names; anything else is 403. "
                + "Every member holds this permit, so the identity check rather than the permit is what "
                + "stops one member handing back for another. "
                + "400 for empty words; 404 for an unknown team or member.");

        app.MapPost("/api/teams/{team}/containers/{name}/needs-decision", async (
            [Description(Describe.Team)] string team,
            [Description("The member reporting. Must be the caller itself.")] string name,
            ReportNeedsDecision request,
            HttpContext context, TeamRegistry teams, ContainerHost host, IMemberReports reports,
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

            if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.Container } caller
                || !ContainerId.Parse(caller.Id).Equals(container.Id))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.Question))
            {
                return Results.BadRequest(new { error = "Say what decision you need." });
            }

            // THE EFFECTS LIVE IN MemberReports: the row and the needs-decision mark.
            await reports.NeedsDecisionAsync(container.Id, request.Question, ct);

            return Results.NoContent();
        })
            .WithTags("Members")
            .RequirePermit(Permits.Progress)
            .WithSummary("Say you are waiting for a decision")
            .WithDescription(
                "A member reporting that it has paused on purpose to ask a question before continuing. "
                + "This is not `agentContainer.completed`: the step is not done. It is also not "
                + "`agentContainer.blocked`: the agent has not given up. The question stays on the card until "
                + "something wakes this member again.\n\n"
                + "**Only about yourself.** The caller must BE the member it names; anything else is 403. "
                + "Every member holds this permit, so the identity check rather than the permit is what "
                + "stops one member posting as another. 400 for an empty question; 404 for an unknown team "
                + "or member.");

        // `wake_me`: A MEMBER ASKS TO BE WOKEN ONCE, LATER, IN THE WORKFLOW IT IS WORKING. Shaped like
        // progress and blocked: {team} in the route so TeamGate covers it, the caller must BE the member it
        // names, and the workflow is the one its running row belongs to - never a number the caller sends.
        // See `WakeMe` for every limit and why.
        app.MapPost("/api/teams/{team}/containers/{name}/wake", async (
            [Description(Describe.Team)] string team,
            [Description("The member asking. Must be the caller itself: a wake is only ever for yourself.")] string name,
            AskWake request,
            HttpContext context, TeamRegistry teams, ContainerHost host, WakeMe wakes, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });
            if (!ContainerId.IsLegalName(name) || host.Find(new ContainerId(stored, name)) is not { } container)
            {
                return Results.NotFound(new { error = $"No member '{name}'." });
            }

            if (WakeCallerRefusal(context, container) is { } refused) return refused;

            if (teams.IsArchived(stored)) return Results.Conflict(new { error = TeamArchive.Refusal(teams.LabelFor(stored)) });

            var (wake, refusal) = await wakes.AskAsync(container.Id, container.CurrentCausation, request.In, request.Note, ct);
            if (refusal is not null) return Results.Json(new { error = refusal.Error }, statusCode: refusal.Status);

            return Results.Ok(new
            {
                wake = wake!.Id,
                workflow = wake.Workflow,
                dueAt = wake.DueAt.ToUniversalTime(),
                dueLocal = WakeMe.Both(wake.DueAt),
                note = wake.Note,
                notice = wakes.AskedNotice(wake),
            });
        })
            .WithTags("Members")
            .RequirePermit(Permits.Progress)
            .WithSummary("Ask to be woken once, later, in this workflow")
            .WithDescription(
                "A member or Manager asks to be woken `in` a delay from now (`90s`, `10m`, `2h`, `1h30m`) "
                + "with a one-line `note` of what to look at. At that time one instruction is appended to it, "
                + "source `wake:<id>`, caused by the `wake.asked` row this writes, in the same workflow, and "
                + "delivered as any instruction is. **Only about yourself**: the caller must BE the member it "
                + "names; the Concierge and any other caller are 403. At least 30 seconds and at most the "
                + "`wake.maxDelay` setting away; at most 3 pending per member and 1 per workflow; refused "
                + "over the workflow's budget or the chain limit. Each refusal is a sentence in `error`. The "
                + "200 carries `wake` (the id), `workflow`, `dueAt` (UTC), `dueLocal` (the Host's clock and "
                + "UTC), `note` and `notice`.");

        app.MapPost("/api/teams/{team}/containers/{name}/wake/{id:long}/cancel", async (
            [Description(Describe.Team)] string team,
            [Description("The member that asked. Must be the caller itself.")] string name,
            [Description("The wake's id, as the ask answered it.")] long id,
            HttpContext context, TeamRegistry teams, ContainerHost host, WakeMe wakes, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Results.NotFound(new { error = $"No team '{team}'." });
            if (!ContainerId.IsLegalName(name) || host.Find(new ContainerId(stored, name)) is not { } container)
            {
                return Results.NotFound(new { error = $"No member '{name}'." });
            }

            if (WakeCallerRefusal(context, container) is { } refused) return refused;

            var cancelled = await wakes.CancelAsync(
                id, container.Id.ToString(), "cancelled by the member that asked for it", owner: container.Id, ct: ct);

            return cancelled is null
                ? Results.NotFound(new { error = $"You have no pending wake {id}. It has fired, been cancelled, or was never yours." })
                : Results.Ok(new { wake = cancelled.Id, cancelled = true, notice = $"Cancelled {WakeMe.Describe(cancelled)}." });
        })
            .WithTags("Members")
            .RequirePermit(Permits.Progress)
            .WithSummary("Cancel one of your own pending wakes")
            .WithDescription(
                "The caller's own pending wake only: the caller must BE the member it names, and a wake that "
                + "is not its own answers exactly as a missing one, 404. Appends `wake.cancelled`.");
    }

    internal static string PauseNoticeFor(string teamLabel) =>
        $"Team '{teamLabel}' is paused. This instruction was queued and will run when the team resumes.";

    // A WAKE IS ONLY EVER FOR ITSELF: the caller must be the member the route names, read from its
    // credential. The Concierge has a person in front of it and sets none; a plugin member holds no
    // credential to ask with.
    static IResult? WakeCallerRefusal(HttpContext context, MemberRuntime container)
    {
        var caller = PrincipalClaims.From(context.User);

        if (caller is { Kind: PrincipalKind.Container } member && ContainerId.Parse(member.Id).Equals(container.Id))
        {
            return null;
        }

        var error = caller?.Kind switch
        {
            PrincipalKind.Container =>
                $"A wake is addressed only to the member asking for it, from its own credential, so you "
                + $"cannot set or cancel one for '{container.Id.Name}'. Ask for your own with wake_me.",
            PrincipalKind.Concierge or PrincipalKind.TenantConcierge =>
                "wake_me is for a team's members and Managers. The Concierge has a person in front of it "
                + "and sets no wakes: say what you are waiting for and tell the person when to look again.",
            _ => "wake_me is a member's own request, made from its own run. A person cancels a wake "
                + "from the member's card or the workflow instead.",
        };

        return Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden);
    }
}
