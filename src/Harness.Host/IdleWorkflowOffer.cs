using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE THING THAT NOTICES A WORKFLOW NOBODY IS WORKING.
///
/// <para>
/// A Manager can finish the last item of a workflow, believe it is still waiting for something
/// that has in fact already landed, and end its turn without declaring. Delivery is not broken in
/// that case - the Manager was woken for everything - and no fix belongs there.
/// </para>
///
/// <para>
/// <b>THE GAP IS THAT THE LAST WAKE IS THE LAST CHANCE AND NOTHING MARKS IT.</b> After such a run
/// ends, no member is working under the correlation and none is queued. A workflow with nobody
/// working it generates no events, so it can never wake anybody again: every wake it would ever
/// produce has been spent. `UNDECLARED` reports this to a person, correctly. Without this class,
/// nothing reports it to the Manager, which is the only party that can act.
/// </para>
///
/// <para>
/// <b>THE AGENT ERROR IS REAL AND IS NOT WHAT THIS FIXES.</b> A Manager can misread its own state, and
/// agents will keep doing that; a skill telling one to be careful leans on the faculty that has
/// already failed. What this class does is notice a state that is perfectly detectable and act on
/// it - and it produces an OUTCOME rather than another signal, because a second mark only a human
/// can see would repeat what `UNDECLARED` already does correctly.
/// </para>
///
/// <para>
/// <b>NOTHING NEW HAD TO BE BUILT TO KNOW WHEN TO LOOK OR WHAT TO ASK.</b> The moment is
/// `onRunEnding`, which already carries the member and the correlation and is awaited by both
/// writers of the terminal row - so a restart-resumed run reaches it too. The question is
/// <see cref="WorkflowBusyState"/>, the same predicate `workflow-complete` refuses with, asked from
/// the other side. No new store, no new data, no polling.
/// </para>
///
/// <para>
/// <b>WITH ONE MORE ARM THAN THE BUSY LIST.</b> It is tempting to read an open workflow
/// plus an empty busy list as meaning nothing is coming. That is true of every state the busy list can
/// SEE - and it sees live container state and PENDING DELIVERY ROWS, where a pending row is written
/// when a message is OFFERED. A message that has been APPENDED and not yet handed out is invisible
/// to it, so between a Manager's `tell` and the pump reaching it the busy list is empty on a
/// workflow that is about to wake somebody. <see cref="UndeliveredAsync"/> closes that window and
/// states the whole argument; without it the first legitimate wait is nagged and the one offer the
/// workflow will ever get is spent on it.
/// </para>
///
/// <para>
/// <b>WHAT IT MUST NOT DO, AND DOES NOT.</b> It never closes a workflow: a board claiming
/// everything shipped when one item had not is strictly worse than the stall, because the stall is
/// visible. It changes nothing about `workflow-complete`, which still takes its correlation from the
/// container's own causation and still lets no caller name a workflow. It never refuses a run for
/// holding an open workflow - a member that cannot pause fails silently instead. And it adds no
/// per-team cap and no serialising collection.
/// </para>
///
/// <para>
/// <b>PUBLIC, LIKE <see cref="WorkflowOwner"/> AND FOR ITS REASON.</b> There is no
/// `InternalsVisibleTo` from `Harness.Host` to `Harness.Host.Tests`, so an internal type here
/// would be invisible to `IdleWorkflowOfferTests` and the test project would not compile.
/// </para>
/// </summary>
/// <param name="causationDepthLimit">
/// The platform's `CausationDepthLimit`, applied here exactly as the `tell` route applies it. The
/// offer travels the ordinary offer path so the existing bounds apply with nothing special
/// added - and the depth bound is checked AT `tell` rather than at the append, so an offer written
/// straight to the log would be the one instruction in the product exempt from it. A thread
/// already going round in circles must not be handed one more invocation to go round in.
/// </param>
/// <remarks>
/// <b>THE SPEND FIGURE IS DELIBERATELY NOT A PARAMETER HERE.</b>
///
/// <para>
/// A raw configured figure would read a configured 0 as a bound of ZERO where it means UNLIMITED
/// everywhere else, and it could not see a team's own per-workflow budget: a team whose figure is
/// LOWER than the instance one has a workflow that is paused while its spend is still under the
/// instance figure, so the check would pass and the offer fire into a paused workflow, where it
/// cannot land. Both are the same fault - two sources for one number - so the check asks
/// `TeamRegistry.EffectiveWorkflowBudgetFor`, which is the one function that knows.
/// </para>
/// </remarks>
/// <param name="cursors">
/// Where the pump has got to for each container, and half of <see cref="UndeliveredAsync"/> - the arm
/// this class has beyond the busy list. See that method for why it is needed.
/// </param>
/// <param name="subscriptions">What each container is woken BY. The other half of
/// <see cref="UndeliveredAsync"/>, and what stops that check pinning on a row nobody was ever going to
/// be handed.</param>
public sealed class IdleWorkflowOffer(
    TeamRegistry teams,
    ContainerHost host,
    IMessageLog log,
    IPendingDeliveries pending,
    ICursors cursors,
    ISubscriptions subscriptions,
    Func<int> causationDepthLimit,
    ILogger<IdleWorkflowOffer>? diagnostics = null)
{
    /// <summary>
    /// The marker that makes an offer RECOGNISABLE AS ONE on the thread it joins, and the whole of
    /// the at-most-once bound.
    ///
    /// <para>
    /// Written on the instruction payload rather than inferred from the source or the wording -
    /// `ResumeSweep.AutomaticResumeField`'s argument, unchanged: a person may write anything into
    /// an instruction, and this field is what the platform wrote about itself. It is also a fact on
    /// the APPEND-ONLY LOG rather than a flag in memory, so a Host that restarts does not offer a
    /// second time, and there is no table to keep in step.
    /// </para>
    /// </summary>
    public const string OfferField = "idleWorkflowOffer";

    /// <summary>Which run's ending produced this offer. Read by nobody; it is on the row so a
    /// person reading the thread can see where the platform decided to speak.</summary>
    private const string OfferAfterField = "idleWorkflowOfferAfter";

    /// <summary>
    /// Whether this row is one of these offers. See <see cref="OfferField"/> for why it is read off
    /// the payload and never off the source or the wording.
    /// </summary>
    public static bool IsOffer(Message message)
    {
        if (!message.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var payload = JsonDocument.Parse(message.Payload).RootElement;

            return payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty(OfferField, out var flag)
                && flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            // THE LOG IS APPEND-ONLY. A row written by an older build that will not parse is not an
            // offer, and it is certainly not a reason to take a live path down.
            return false;
        }
    }

    /// <summary>
    /// Called as a run ends, above the terminal row. Returns whether an offer was appended.
    ///
    /// <para>
    /// <b>EVERY REFUSAL BELOW IS A `return false` WITH A REASON BESIDE IT</b>, deliberately rather
    /// than one combined predicate: this method decides whether the platform spends an agent
    /// invocation without being asked, and the next person to widen it should have to delete a
    /// sentence to do it.
    /// </para>
    /// </summary>
    /// <param name="member">The container whose run is ending.</param>
    /// <param name="causation">The seq of the message that run was handling.</param>
    /// <param name="succeeded">
    /// Whether the run FINISHED ITS TURN. See the check in <see cref="DecideAsync"/>: this is the
    /// difference between a Manager that ended its turn holding an open workflow - the incident -
    /// and one whose turn was taken away by a Stop, a spend limit or a crash.
    /// </param>
    public async Task<bool> OnRunEndingAsync(
        ContainerId member, long? causation, bool succeeded, CancellationToken ct)
    {
        try
        {
            return await DecideAsync(member, causation, succeeded, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            // A HANDLER'S FAILURE IS NOT THIS RUN'S - `MemberRuntime` says the same at its own call
            // site, and a terminal row that went missing because this threw would leave a container
            // reading `running` forever. LOGGED rather than swallowed in silence: the whole subject
            // of this class is a condition nobody could otherwise see.
            diagnostics?.LogWarning(
                error, "Could not decide whether to offer an idle workflow for {Member}.", member);

            return false;
        }
    }

    private async Task<bool> DecideAsync(
        ContainerId member, long? causation, bool succeeded, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;

        // A TURN THAT WAS TAKEN AWAY IS NOT A TURN THAT ENDED WITHOUT DECLARING.
        //
        // This class is about a Manager that FINISHED and did not declare. A run stopped from the board,
        // cut off by the spend limit, killed by a restart or failed by its runner leaves
        // `agentContainer.failed` IN THE LEDGER, with a class on it - which is what `ResumeSweep`
        // and a person read, and which the next invocation reads back. Offering here would wake a
        // member a person had just Stopped, and it would spend again on a workflow the budget had
        // already refused to spend on.
        //
        // IT ALSO SPENDS THE ONE OFFER, which is the part that would be hard to see: a workflow
        // nudged after a failure has nothing left for the stall it reaches later.
        if (!succeeded) return false;

        // A RUN THAT WAS HANDLING NOTHING HAS NO WORKFLOW TO OFFER ANYTHING UNDER, and a
        // correlation invented here would append an instruction rooting its own workflow - the same
        // absence `workflow-complete` answers 409 for, and refused rather than guessed for the same
        // reason.
        if (causation is not { } waking) return false;

        // A TEAM THIS HOST NO LONGER HAS is not offered anything. `stored` is the canonical
        // spelling, which is what the busy check and the owner fallback below are both built from.
        if (teams.ExistingName(member.Team) is not { } stored) return false;

        if (await log.FindAsync(waking, ct) is not { } woken) return false;

        var correlation = woken.CorrelationId;

        if (correlation <= 0) return false;

        // WHOSE WORKFLOW IS THIS, by `workflow-complete`'s own rule: the container the root
        // instruction addressed, or this team's Manager when the root addressed nobody (a schedule
        // firing, a kanban card event). Only that container may declare, so only that container is
        // worth waking.
        var owner = await WorkflowOwner.OfAsync(log, correlation, ct)
            ?? new ContainerId(stored, TeamRegistry.DefaultManagerName);

        // A RUN ENDING ON ANY OTHER MEMBER IS NOT THE LAST WAKE, AND THIS IS THE EASY TRAP.
        //
        // The busy list excludes the ending member, and that member's own
        // `agentContainer.completed` has not been appended yet - it is written immediately BELOW
        // this call. So at the instant a worker's run ends, the list is empty on a workflow whose
        // Manager is about to be woken by that very row. Firing there would nag a Manager that has
        // not yet had its turn, which is "the first legitimate wait gets nagged" wearing different
        // clothes. Every non-declarer's run end publishes a terminal row that wakes the declarer
        // anyway, so nothing is lost by waiting for the declarer's own ending.
        if (!member.Equals(owner)) return false;

        // RE-CHECKED AT THE MOMENT OF DECIDING, as `ResumeSweep.FireAsync` does: appending an
        // instruction addressed to a container that does not exist puts a message on the log
        // nothing will ever read.
        if (host.Find(owner) is not { } declarer) return false;

        // AN OFFER ONLY TO A MEMBER THAT CAN ACT ON IT. The offer asks the declarer to call
        // `workflow_complete`, `blocked` or `needs_decision`, all of which need the Progress
        // permit; a member that holds none - a plugin member, reporting through its own stdout -
        // would be handed prose it cannot answer, and run it as work.
        if (!declarer.Permits.Contains(Permits.Progress)) return false;

        // A TRIGGER THAT CHOSE NOT TO BE TOLD OF A FINISHED RUN is not offered another turn on its
        // behalf: its run ending without a hand-back is the end of it, as the trigger said, and an
        // offer would spend a second invocation whose completion wakes the Manager anyway.
        // `UndeclarableWorkflows` declares such a workflow instead.
        if (await UndeclarableWorkflows.RootedByQuietTriggerAsync(log, correlation, ct)) return false;

        // STILL OPEN - `WorkflowOpenSql.NotClosed`, the same predicate the board reads and the same
        // one `UNDECLARED` renders. A workflow somebody declared or closed is finished with, and
        // nothing may nag one.
        if (!(await log.OpenWorkflowsAmongAsync([correlation], ct)).Contains(correlation))
        {
            return false;
        }

        // AT MOST ONCE PER WORKFLOW, AND THAT BOUND IS THE WHOLE SAFETY OF THIS DESIGN. A
        // Manager that ends AGAIN without declaring must not be nudged a second time: the state
        // that fired the first offer is true again the instant that second run ends, so without
        // this the feature is a way to spend forever on a workflow nobody wants finished.
        //
        // Read off the thread rather than remembered, so a Host restart cannot forget it.
        var thread = await log.ReadCorrelationAsync(correlation, ct);

        if (thread.Any(IsOffer)) return false;

        // A PAUSED WORKFLOW IS WAITING ON A PERSON, WHICH IS THE OPPOSITE OF THE SILENCE THIS
        // CLASS EXISTS TO CATCH. It has a known reason and a known way back -
        // the resume route - and that is the same distinction `AWAITING` already draws: waiting on
        // a person is not nobody-is-working-this.
        //
        // AND AN OFFER HERE COULD NOT LAND. The pump's `OverBudgetAsync` refuses to deliver
        // anything on a workflow over its figure, so the offer would be appended, the at-most-once
        // marker burned on it permanently, and the only result a `Rejected` row and a nudge that
        // appears to do nothing - which teaches whoever is watching that the nudge is broken.
        //
        // READ OFF THE THREAD ALREADY IN HAND: no extra query, and it cannot disagree with the
        // projection or with the pump, because all three call `WorkflowPause.IsPaused`.
        if (WorkflowPause.IsPaused(thread)) return false;

        // THE DISCRIMINATOR, AND WHAT MAKES THIS SAFE TO ACT ON AT ALL. A member Running
        // under this correlation means the Manager is correctly waiting; an accepted-but-unstarted
        // delivery means work is coming. Only the empty list means nothing will ever come - and it
        // needs no guess about intent and no timer, being a fact about the queue that is true the
        // instant the run ends.
        //
        // `excluded: member` is the ending run's own in-flight state, which is Running BY
        // DEFINITION, being the thing this call is made from.
        var busy = await WorkflowBusyState.DescribeAsync(
            stored, correlation, host, pending, log, excluded: member, ct);

        if (busy.Count != 0) return false;

        // AND THE HALF THE BUSY LIST CANNOT SEE. See <see cref="UndeliveredAsync"/>: without it
        // the very first legitimate wait gets nagged.
        if (await UndeliveredAsync(stored, thread, member, waking, ct)) return false;

        // THE LOOP BOUND, on the message about to be written rather than on its parent - the `tell`
        // route's own arithmetic. See the `causationDepthLimit` parameter for why it is asked here.
        // `causation.depthLimit`, read now; 0 is no limit, as on the tell route.
        if (causationDepthLimit() is > 0 and var limit && woken.Depth + 1 > limit) return false;

        // THE SPEND BOUND. Unmeasured spend never convicts: a workflow whose runs recorded no usage
        // cannot be refused on this figure however small the limit, exactly as at `tell`.
        //
        // THROUGH THE RESOLVER, NOT THE RAW FIGURE. A team whose own budget is LOWER than
        // the instance one has a workflow that is paused while its spend is still under the
        // instance figure - so read raw, this check passes and the offer fires into a paused
        // workflow. `null` means UNLIMITED and is the only spelling of it that
        // reaches here, which is also why a configured 0 does not read as a bound of zero.
        //
        // IT DOES NOT SEE THE RUN THAT IS ENDING RIGHT NOW, because that run's terminal row - the
        // one carrying its token counts - is appended immediately below this call. The pump asks
        // the same question again at the wake, by which time it has landed, so the bound is not
        // escaped by this ordering; it is only ever answered here on slightly older figures. The
        // PAUSED check above is the correct guard and this is the belt beside it; both, not one.
        var effectiveBudget = teams.EffectiveWorkflowBudgetFor(stored);

        if (effectiveBudget is { } bound)
        {
            var spend = await log.GetSpendSinceNudgeAsync(correlation, ct);

            if (spend.RunsWithMeasuredUsage > 0 && spend.TokensSpent > bound) return false;
        }

        // IT SAYS THREE THINGS - the state, that this is the last wake, and what to do - and it
        // says them to an AGENT, which is why it names the verbs. A Manager woken with "carry on"
        // re-derives the whole thread or, worse, starts again; a Manager not told this is the last
        // thing that will ever wake this workflow cannot judge how much the silence costs.
        // THE SUBJECT IS WRITTEN RATHER THAN CUT, which `InstructionText` asks every caller that
        // knows what the work IS to do. Left to the fallback, this instruction's card title would
        // be cut from the text and stop mid-sentence, on a board where the whole point is that
        // somebody notices.
        const string subject = "This workflow is open and nobody is working it";

        var text =
            subject
            + "\n\nEvery member that was working under it has finished, nothing is queued, and no "
            + "delivery is outstanding - so the run that just ended was the last wake this workflow "
            + "was ever going to get, and the platform is spending one more invocation to say so. "
            + "Read everything under this thread and do ONE of two things: declare it finished with "
            + "the `workflow_complete` tool, saying what was delivered, or, if it is genuinely "
            + "waiting on something, say what with the `blocked` or `needs_decision` tool "
            + "so it is in the ledger. Do not simply end your turn again: this is "
            + "offered ONCE per workflow and nothing will offer it a second time.";

        var parts = InstructionText.Split(text, subject);

        var offer = await log.AppendAsync(
            new NewMessage(
                MessageTypes.InstructionFor(owner),
                JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [PayloadFields.Instruction] = text,
                    ["subject"] = parts.Subject,
                    [PayloadFields.Body] = parts.Body,
                    [OfferField] = true,
                    [OfferAfterField] = waking,
                }),

                // `host`, the source every platform-published instruction already uses. NOT a
                // person's id: nothing here was done by a person, and a thread that read as though
                // somebody had asked would be the log telling a lie about who spent the money.
                "host",

                // THE WAKING SEQ, AND DELIBERATELY NOT THE CORRELATION.
                //
                // `ResumeSweep` causes its nudge by the correlation itself, correctly - a rescue
                // must restart the spend window or the bound refuses the recovery of the very
                // workflow it failed to bound. THIS IS NOT A RESCUE. An instruction whose causation
                // IS the correlation is what `GetSpendSinceNudgeAsync` counts as a nudge, so
                // writing this one that way would hand the workflow a fresh budget in the act of
                // bounding it, and would flatten its depth to 1 however deep the thread had gone.
                // Chaining off the run's own waking message keeps both bounds counting honestly.
                //
                // The one case where the two coincide is a declarer ending the run that handled the
                // ROOT - there the waking seq IS the correlation, and this offer looks like a nudge
                // to the spend window. That is the same surface a `tell` naming the root already
                // has, `AGENTS.md` already records that both bounds are evadable by choosing a
                // causation, and at-most-once means it can happen once per workflow at worst.
                waking),
            ct);

        diagnostics?.LogInformation(
            "Offered {Owner} one further invocation under workflow {Correlation}, which is open "
            + "with nothing working it (seq {Seq}).",
            owner,
            correlation,
            offer.Seq);

        return true;
    }

    /// <summary>
    /// IS THERE A ROW UNDER THIS WORKFLOW THAT THE PUMP HAS NOT HANDED OUT YET.
    ///
    /// <para>
    /// <b>THE BUSY LIST ALONE IS NOT ENOUGH.</b> It looks as though the
    /// question is already answerable - open workflow plus an empty
    /// <see cref="WorkflowBusyState"/> list means nothing is coming - and that is true of every
    /// state the busy list can SEE. It reads live container state and PENDING DELIVERY ROWS, and a
    /// pending row is written when a message is OFFERED. <b>So a message that has been APPENDED and
    /// not yet offered is invisible to it, and during that window the busy list is empty on a
    /// workflow that is about to wake somebody.</b>
    /// </para>
    ///
    /// <para>
    /// That window is not theoretical and it is not narrow enough to wave away. A Manager whose
    /// last act is a `tell` ends its run with the instruction on the log and the pump not yet past
    /// it, and a worker finishing while
    /// the Manager is mid-run leaves a completion row the Manager has not been handed. Both read as
    /// "nothing is busy". Firing on either is the failure the discriminator exists to prevent: the first
    /// legitimate wait gets nagged, AND the one offer this workflow will ever get is spent on it,
    /// so the real stall later arrives with nothing left to notice it.
    /// </para>
    ///
    /// <para>
    /// <b>IT IS THE PUMP'S OWN DELIVERY TEST, ASKED OVER ONE THREAD.</b> A row is still coming if
    /// some live container on this team is subscribed to its type, did not publish it, is in its
    /// team, and has a cursor below it. Deliberately WITHOUT the trigger filters and the budget
    /// refusal the pump also applies - those can only turn a delivery into a non-delivery, so
    /// leaving them out can only make this say "still coming" about something that will not come.
    /// That is over-suppression, and over-suppression degrades to what the platform does
    /// without this class: the workflow reads `UNDECLARED` and a person looks at it.
    /// </para>
    ///
    /// <para>
    /// <b>IT CANNOT PIN A WORKFLOW FOREVER</b>, which the cheaper version of this check can. Asking
    /// only "is any cursor below the highest seq on the thread" would be pinned permanently by the
    /// first row nobody subscribes to - `repo.pushed`, which `TerminalPublish` appends under this
    /// very correlation moments before this runs - and the offer would then never fire on any team
    /// with a repository. Reading the subscription is what makes the answer converge: the pump
    /// advances a cursor over everything it reads, so anything this check can see, it will
    /// eventually see delivered.
    /// </para>
    ///
    /// <para>
    /// <b>THE ENDING RUN'S OWN WAKE WAS DELIVERED, WHATEVER ITS CURSOR SAYS.</b> The pump offers a
    /// message and moves the cursor only after its whole batch, so a run that finishes quickly -
    /// on a loaded host, or a run with nothing to do - ends while its own member's cursor is still
    /// below the row that woke it. Read literally, that row is "still coming" to the very member
    /// it already ran, the check says no, and it is never asked again: the workflow stays open for
    /// good. The pump hands a member its rows in order, so everything up to
    /// <paramref name="wokenBy"/> has been dealt with for <paramref name="ending"/>, and its cursor
    /// counts as at least that far.
    /// </para>
    /// </summary>
    private Task<bool> UndeliveredAsync(
        string team, IReadOnlyList<Message> thread, ContainerId ending, long wokenBy, CancellationToken ct) =>
        UndeliveredAsync(host, subscriptions, cursors, diagnostics, team, thread, ending, wokenBy, ct);

    /// <summary>The same question for a caller holding its own collaborators -
    /// <see cref="UndeclarableWorkflows"/> asks it at the same moment, for the same reason.</summary>
    internal static async Task<bool> UndeliveredAsync(
        ContainerHost host, ISubscriptions subscriptions, ICursors cursors, ILogger? diagnostics,
        string team, IReadOnlyList<Message> thread, ContainerId ending, long wokenBy, CancellationToken ct)
    {
        foreach (var snapshot in host.Snapshots().Where(s =>
                     string.Equals(s.Team, team, StringComparison.OrdinalIgnoreCase)))
        {
            // `Id` AND NOT `Name` - `Name` is the LABEL, equal to the identifier only for a member
            // nobody relabelled, which is how a check like this reads correctly in a test and targets
            // the wrong container the first time somebody gives a member a display name.
            var subscriber = new ContainerId(snapshot.Team, snapshot.Id);
            var types = await subscriptions.ForAsync(subscriber, ct);

            // A CONTAINER SUBSCRIBED TO NOTHING IS SKIPPED BY THE PUMP BEFORE IT EVEN READS A
            // CURSOR, so its cursor never moves. Counting it would pin every workflow on the team.
            if (types.Count == 0) continue;

            var subscribed = new HashSet<string>(types, StringComparer.Ordinal);
            var position = await cursors.PositionAsync(subscriber, ct);
            if (string.Equals(subscriber.ToString(), ending.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                position = Math.Max(position, wokenBy);
            }

            foreach (var row in thread)
            {
                if (row.Seq <= position) continue;
                if (!subscribed.Contains(row.Type)) continue;

                // The pump's own two guards: a container never reacts to its own publications, and
                // a container is woken only by its OWN TEAM. `MessageTeam.Of`, never
                // `message.Source` - a console's instruction carries `Source = "console"` and its
                // team lives in its TYPE.
                if (string.Equals(
                        row.Source, subscriber.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(
                        MessageTeam.Of(row), subscriber.Team, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                diagnostics?.LogDebug(
                    "Workflow {Correlation} still has seq {Seq} ({Type}) to hand to {Subscriber}.",
                    row.CorrelationId,
                    row.Seq,
                    row.Type,
                    subscriber);

                return true;
            }
        }

        return false;
    }
}
