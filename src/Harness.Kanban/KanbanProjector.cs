using System.Globalization;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Kanban;

/// <summary>
/// Pure projection: IReadOnlyList<Message> → board snapshot.
/// Takes messages from the event log and produces card state.
/// Replayable and deterministic - no side effects.
/// </summary>
public static class KanbanProjector
{
    /// <summary>
    /// Whether a row of this type changes the board: exactly the types <c>ProcessMessage</c> reads.
    /// The host raises <c>kanbanChanged</c> for these, so an open board refreshes whichever team's
    /// card moved. Keep it in step with <c>ProcessMessage</c>.
    /// </summary>
    public static bool MovesTheBoard(string type) =>
        type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
        || type.StartsWith(MessageTypes.KanbanCardPrefix, StringComparison.Ordinal)
        || type is MessageTypes.Started or MessageTypes.Progress or MessageTypes.Blocked
            or MessageTypes.Failed or MessageTypes.Completed or MessageTypes.WorkflowCompleted
            or MessageTypes.WorkflowPaused or MessageTypes.WorkflowResumed or MessageTypes.NeedsDecision
            or "needs-decision" or MessageTypes.Handback or MessageTypes.ContainerRemoved
            or MessageTypes.RepoDefaultBranchMoved or MessageTypes.AgentForeignTools;

    /// <summary>
    /// Project a sequence of messages into a board snapshot.
    /// </summary>
    public static KanbanBoard Project(
        IReadOnlyList<Message> messages,
        KanbanFilter? filter = null)
    {
        filter ??= new KanbanFilter();

        return new KanbanBoard(
            KanbanLanes.All,
            Matching(messages, filter).Select(state => state.ToCard()).ToList(),
            filter);
    }

    /// <summary>
    /// ONE card, with its TRAIL - the messages that card is made of, oldest-first.
    ///
    /// THE SAME PROJECTION THE BOARD RUNS, and deliberately not a second walk of the log beside it.
    /// The trail is accumulated by the very handlers that decide a card's lane and status, so there
    /// is no way for the two to disagree about which messages belong to which card - which is the
    /// failure a separate "fetch this card's messages" query would eventually have, silently, for
    /// the console-issued instructions whose member is on the TYPE and nowhere else.
    ///
    /// <paramref name="filter"/> is applied exactly as the board applies it, so a card outside the
    /// filtered scope answers NULL rather than being found. The endpoint's team scoping depends on
    /// that: null becomes a 404, never another team's card.
    /// </summary>
    public static KanbanCardDetail? ProjectCard(
        IReadOnlyList<Message> messages,
        string cardId,
        KanbanFilter? filter = null)
    {
        return Matching(messages, filter ?? new KanbanFilter())
            .FirstOrDefault(state => string.Equals(state.Id, cardId, StringComparison.Ordinal))
            ?.ToDetail();
    }

    /// <summary>
    /// Every card the filter admits, as projection state.
    ///
    /// THE FILTER LIVES HERE AND NOWHERE ELSE. There is more than one reader, and a reader that
    /// filters for itself is how one of them ends up admitting a team the other refuses.
    /// </summary>
    private static IEnumerable<CardState> Matching(
        IReadOnlyList<Message> messages,
        KanbanFilter filter)
    {
        var cardStates = new Dictionary<string, CardState>();

        foreach (var msg in messages)
        {
            ProcessMessage(msg, cardStates);
        }

        var matching = States(cardStates);

        // Each filter is a list (`KanbanFilter.Values`): ANY of its values keeps a card.
        if (KanbanFilter.Values(filter.Team) is { Count: > 0 } teams)
        {
            var wanted = teams.ToHashSet(StringComparer.OrdinalIgnoreCase);
            matching = matching.Where(c => c.Team is not null && wanted.Contains(c.Team));
        }

        if (KanbanFilter.Values(filter.Member) is { Count: > 0 } members)
        {
            var wanted = members.ToHashSet(StringComparer.OrdinalIgnoreCase);
            matching = matching.Where(c => c.Member is not null && wanted.Contains(c.Member));
        }

        if (KanbanFilter.Values(filter.Status) is { Count: > 0 } statuses)
        {
            var wanted = statuses.ToHashSet(StringComparer.OrdinalIgnoreCase);
            matching = matching.Where(c => c.Status is not null && wanted.Contains(c.Status));
        }

        // No date range and no correlation id; see `KanbanFilter` for why, and for what
        // adding one costs.
        return matching.ToList();
    }

    /// <summary>
    /// One message, onto the board.
    ///
    /// ONLY THE INSTRUCTION ARM NEEDS A TEAM, and that is the whole shape of this method. A
    /// team is what a NEW card is filed under, and an instruction is the only message that makes
    /// one - see <see cref="CardForInstruction"/>. Every arm below it updates a card that already
    /// exists and takes that card's team from the instruction that created it, so a message whose
    /// team cannot be recovered is not a message that has to be dropped: it simply finds a card or
    /// does not.
    /// </summary>
    private static void ProcessMessage(
        Message msg,
        Dictionary<string, CardState> cardStates)
    {
        var msgType = msg.Type;

        // Handle instructions - check for InstructionPrefix
        if (msgType.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
        {
            // A team that cannot be recovered is a card that could not be filed. Nothing else here
            // creates one, so this is the only arm that has to ask.
            if (MessageTeam.Of(msg) is { } team)
            {
                HandleInstruction(msg, cardStates, team);
            }
        }
        else if (msgType == MessageTypes.Started)
        {
            HandleContainerStarted(msg, cardStates);
        }
        else if (msgType == MessageTypes.Progress)
        {
            HandleContainerProgress(msg, cardStates);
        }
        else if (msgType == MessageTypes.Blocked)
        {
            HandleContainerBlocked(msg, cardStates);
        }
        else if (msgType == MessageTypes.Failed)
        {
            HandleContainerFailed(msg, cardStates);
        }
        else if (msgType == MessageTypes.Completed)
        {
            HandleContainerCompleted(msg, cardStates);
        }
        else if (msgType == MessageTypes.WorkflowCompleted)
        {
            HandleWorkflowCompleted(msg, cardStates);
        }
        else if (msgType == MessageTypes.WorkflowPaused)
        {
            HandleWorkflowPaused(msg, cardStates, paused: true);
        }
        else if (msgType == MessageTypes.WorkflowResumed)
        {
            HandleWorkflowPaused(msg, cardStates, paused: false);
        }
        else if (msgType == MessageTypes.NeedsDecision || msgType == "needs-decision")
        {
            HandleNeedsDecision(msg, cardStates);
        }
        else if (msgType == MessageTypes.Handback)
        {
            HandleContainerHandback(msg, cardStates);
        }
        else if (msgType == MessageTypes.KanbanCardPlanned)
        {
            // BEFORE the prefix arm below, which UPDATES a card that already exists. This one
            // CREATES, so it cannot go through a lookup with no creating arm.
            if (MessageTeam.Of(msg) is { } plannedTeam)
            {
                HandleCardPlanned(msg, cardStates, plannedTeam);
            }
        }
        else if (msgType.StartsWith(MessageTypes.KanbanCardPrefix, StringComparison.Ordinal))
        {
            HandleKanbanCardEvent(msg, cardStates);
        }
        else if (msgType == MessageTypes.ContainerRemoved)
        {
            HandleContainerRemoved(msg, cardStates);
        }
        else if (msgType == MessageTypes.RepoDefaultBranchMoved)
        {
            // On the Manager's card, as a trail row; the status is the run's, not this report's.
            if (CardFor(msg, cardStates) is { } moved)
            {
                Record(moved, msg, FirstStringField(msg, PayloadFields.Reason));
                moved.UpdatedAt = msg.OccurredAt.DateTime;
            }
        }
        else if (msgType == MessageTypes.AgentForeignTools)
        {
            // On the member's card, as a trail row and a mark; the status is the run's. The mark
            // only ever rises - called over offered over not measured - since a later clean run does
            // not undo what reached an earlier one.
            if (CardFor(msg, cardStates) is { } checkedCard)
            {
                Record(checkedCard, msg, FirstStringField(msg, PayloadFields.Text));
                checkedCard.ForeignTools = KanbanForeignTools.Worse(checkedCard.ForeignTools, KanbanForeignTools.Of(msg));
            }
        }
    }

    /// <summary>
    /// THE SECOND WAY A CARD IS BORN, AND THE ONLY OTHER ONE.
    ///
    /// <para>
    /// Otherwise a card is creatable only by an INSTRUCTION - "a lane holds requested work; a run
    /// nobody asked for is not pending work". That rule stands; this is a different claim. A Manager
    /// cutting a spec into six pieces is REQUESTING six pieces of work before it has anybody to do
    /// them, and this is how it says so.
    /// </para>
    ///
    /// <para>
    /// THE CARD'S ID IS THE SEQ OF ITS OWN ROW. No generated identifier, no collision question,
    /// stable forever - the same trick <c>correlation_id</c> already uses, and the reason the
    /// `messages` schema gives for it applies unchanged. It is a bare number, which tells it apart
    /// at a glance from an instruction card's <c>{correlation}_{member}</c> form; the two really are different
    /// kinds of card.
    /// </para>
    ///
    /// <para>
    /// ITS MEMBER IS NULL AND STAYS NULL until somebody claims it with <c>tell --card</c>.
    /// </para>
    /// </summary>
    private static void HandleCardPlanned(
        Message msg,
        Dictionary<string, CardState> cardStates,
        string team)
    {
        var cardId = msg.Seq.ToString(CultureInfo.InvariantCulture);

        if (cardStates.ContainsKey(cardId)) return;

        var title = StringField(msg, PayloadFields.Title) is { Length: > 0 } t ? t : "Untitled";
        var body = StringField(msg, PayloadFields.Body) ?? "";

        var state = new CardState
        {
            Id = cardId,
            CorrelationId = msg.CorrelationId,
            Member = null,
            Item = LongField(msg, PayloadFields.BacklogItem),
            Team = team,
            Title = title,
            Body = body,
            Status = "queued",
            LaneId = KanbanLanes.ForStatus("queued"),
            Color = KanbanLanes.ColourForStatus("queued"),
            Progress = new List<ProgressItem>(),
            Trail = new List<KanbanTrailEntry>(),
            CreatedAt = msg.OccurredAt.DateTime,
            UpdatedAt = msg.OccurredAt.DateTime,
            AwaitingManager = false,
            Created = true,
        };
        state.JoinWorkflow(msg.CorrelationId);

        cardStates[cardId] = state;

        Record(state, msg, body.Length == 0 ? title : title + "\n" + body);
    }

    /// <summary>
    /// A MEMBER WAS DELETED, SO EVERY CARD NAMING IT GOES BACK TO UNASSIGNED.
    ///
    /// <para>
    /// ONE ROW DOES ALL OF THEM, which is why this loops rather than reading a card id: a row per
    /// card would put the projection's own output into its input. The member comes from
    /// <c>Source</c>, the existing rule for container events, so nothing here reads a payload.
    /// </para>
    ///
    /// <para>
    /// NOTHING IS LOST. The card's member answers "who is on this now"; the TRAIL answers "who did
    /// what", and every row of it stays. That split is the same one <c>ContainerSnapshot.Blocked</c>
    /// makes against the `blocked` row that outlives it.
    /// </para>
    /// </summary>
    private static void HandleContainerRemoved(Message msg, Dictionary<string, CardState> cardStates)
    {
        if (ExtractMember(msg) is not { Length: > 0 } member) return;

        foreach (var state in States(cardStates))
        {
            if (!string.Equals(state.Member, member, StringComparison.OrdinalIgnoreCase)) continue;

            state.Member = null;
            state.UpdatedAt = msg.OccurredAt.DateTime;

            Record(state, msg, "");
        }
    }

    private static void HandleInstruction(
        Message msg,
        Dictionary<string, CardState> cardStates,
        string team)
    {
        var member = ExtractMember(msg);

        // ASKED BEFORE THE RESOLUTION BELOW, because that is what sets the member - afterwards there
        // is no way to tell a first claim from an instruction to somebody already on the card.
        var claiming = StringField(msg, PayloadFields.Card) is { Length: > 0 } wanted
            && cardStates.TryGetValue(wanted, out var target)
            && !string.Equals(target.Member, member, StringComparison.OrdinalIgnoreCase);

        var state = CardForInstruction(msg, cardStates, team, member ?? "unknown");

        var (subject, body) = ExtractTitleAndBody(msg);

        // ASSIGNMENT IS VISIBLE IN THE TRAIL, in words. Its own
        // row rather than a prefix on the instruction's: the instruction row carries what the person
        // WROTE and must go on doing so - a board may summarise, a worker may not - and folding two
        // facts into one sentence makes a trail that cannot be read back as a sequence of events.
        if (claiming)
        {
            Record(state, msg, $"Assigned to {member ?? "(unknown)"}.");
        }

        if (!state.Created)
        {
            state.Status = "queued";
            state.LaneId = KanbanLanes.ForStatus("queued");
            state.Color = KanbanLanes.ColourForStatus("queued");
            state.Created = true;
            state.CreatedAt = msg.OccurredAt.DateTime;
            (state.Title, state.Body) = (subject, body);
        }

        // THE WHOLE INSTRUCTION, not the card's title. The title is a CUT of this text and the
        // header already shows it; a trail row repeating it would open the story by saying nothing.
        Record(state, msg, StringField(msg, PayloadFields.Instruction) is { Length: > 0 } whole
            ? whole
            : body.Length == 0 ? subject : $"{subject}\n{body}");

        state.UpdatedAt = msg.OccurredAt.DateTime;
    }

    private static void HandleContainerStarted(
        Message msg,
        Dictionary<string, CardState> cardStates)
    {
        if (CardFor(msg, cardStates) is not { } state) return;

        state.Status = "running";
        state.LaneId = KanbanLanes.ForStatus("running");
        state.Color = KanbanLanes.ColourForStatus("running");

        // No prose, and none invented. The payload carries the trigger's type and nothing a person
        // wants read back; the row's TYPE and its time are the whole of what happened.
        Record(state, msg, "");
        state.UpdatedAt = msg.OccurredAt.DateTime;
    }

    private static void HandleContainerProgress(
        Message msg,
        Dictionary<string, CardState> cardStates)
    {
        if (CardFor(msg, cardStates) is not { } state) return;

        var text = ExtractProgressText(msg);
        if (!string.IsNullOrEmpty(text))
        {
            state.Progress.Add(new ProgressItem(msg.OccurredAt.DateTime, text));
            Record(state, msg, text);
        }

        state.UpdatedAt = msg.OccurredAt.DateTime;
    }

    private static void HandleContainerBlocked(
        Message msg,
        Dictionary<string, CardState> cardStates)
    {
        if (CardFor(msg, cardStates) is not { } state) return;

        if (MayReplace(state, "blocked"))
        {
            state.Status = "blocked";
            state.LaneId = KanbanLanes.ForStatus("blocked");
            state.Color = KanbanLanes.ColourForStatus("blocked");
        }
        Record(state, msg, FirstStringField(msg, PayloadFields.Reason, PayloadFields.Item));
        state.UpdatedAt = msg.OccurredAt.DateTime;
    }

    /// <summary>
    /// A WORKER'S PART FINISHED, AND THE ONE THING THIS MUST NOT DO IS LOOK LIKE A GIVE-UP.
    ///
    /// <para>
    /// `agentContainer.handback` is a SUCCESS. Its own status rather than `completed`,
    /// because the two are different claims about different things - `completed` is the PLATFORM
    /// reporting that a run exited, and this is the MEMBER saying the work it was given is done.
    /// A member hands back and its run then exits seconds later, so both land; they agree, and
    /// `GetLaneForStatus` deliberately routes them to the SAME lane, so the card does not move
    /// twice for one ending.
    /// </para>
    ///
    /// <para>
    /// NOT IN <see cref="IsWaitingOnAPerson"/> AND NOT IN <see cref="Loudness"/>. Those two rank
    /// the human-action statuses - `failed`, `blocked`, `needs-decision` - and this is not one of
    /// them: nobody has to do anything about a card that was handed back. Its lane is the one
    /// `completed` gets, never the blocked lane.
    /// </para>
    ///
    /// <para>
    /// IT WILL NOT OVERWRITE A LIVE HUMAN-ACTION STATUS, and that guard is
    /// `HandleContainerCompleted`'s, copied here on purpose. `blocked` KEEPS MEANING GAVE-UP: a
    /// member that gave up and then handed back must not launder its own card green, exactly as a
    /// member's own `completed` is not allowed to. The row is recorded either way,
    /// so nothing is lost from the card's story - only the status is withheld.
    /// </para>
    /// </summary>
    private static void HandleContainerHandback(
        Message msg,
        Dictionary<string, CardState> cardStates)
    {
        if (CardFor(msg, cardStates) is not { } state) return;

        if (!IsWaitingOnAPerson(state))
        {
            state.Status = "handback";
            state.LaneId = KanbanLanes.ForStatus("handback");
            state.Color = KanbanLanes.ColourForStatus("handback");
        }

        Record(state, msg, FirstStringField(msg, PayloadFields.Delivered, PayloadFields.Output));
        state.UpdatedAt = msg.OccurredAt.DateTime;
    }

    private static void HandleContainerFailed(
        Message msg,
        Dictionary<string, CardState> cardStates)
    {
        if (CardFor(msg, cardStates) is not { } state) return;

        if (MayReplace(state, "failed"))
        {
            state.Status = "failed";
            state.LaneId = KanbanLanes.ForStatus("failed");
            state.Color = KanbanLanes.ColourForStatus("failed");
        }

        // `launchError` FIRST, for the reason MessageText reads it first: a run that never started
        // has an output field carrying nothing, and a trail that showed that instead would lose the
        // one sentence naming why.
        Record(state, msg, FirstStringField(msg, PayloadFields.LaunchError, PayloadFields.Output));
        state.UpdatedAt = msg.OccurredAt.DateTime;
    }

    private static void HandleContainerCompleted(
        Message msg,
        Dictionary<string, CardState> cardStates)
    {
        if (CardFor(msg, cardStates) is not { } state) return;

        // THE ROW IS RECORDED EITHER WAY; ONLY THE STATUS IS WITHHELD. An agent that gives up
        // mid-run is still a run that then EXITS, and the platform publishes this the moment it
        // does - seconds later, every time. So a member's own `completed` is not evidence that
        // anything it asked for was answered, and letting it move the card would make every give-up
        // invisible within seconds of being published.
        //
        // Its trail entry belongs to this card and stays: what the run printed on its way out is
        // part of this card's story, and dropping it would leave a card that gave up with no record
        // of having finished at all. `HandleWorkflowCompleted` deliberately does the opposite and
        // skips its Record - see there.
        if (!IsWaitingOnAPerson(state))
        {
            state.Status = "completed";
            state.LaneId = KanbanLanes.ForStatus("completed");
            state.Color = KanbanLanes.ColourForStatus("completed");
        }

        Record(state, msg, FirstStringField(msg, PayloadFields.Output));
        state.UpdatedAt = msg.OccurredAt.DateTime;
    }

    /// <summary>
    /// A LIVE GIVE-UP, WHICH THE ROWS THAT ORDINARILY FOLLOW ONE MUST NOT ERASE.
    ///
    /// <para>
    /// `blocked` and `needs-decision` are not outcomes at all: the agent publishes them MID-RUN and
    /// the platform then publishes `completed` when the run exits. Letting that `completed` win
    /// would make a card say `done` about a member that had given up - and say it twice over, once
    /// from the member's own `completed` and again from the manager's sweep. The LOG projection
    /// follows the same rule.
    /// </para>
    ///
    /// <para>
    /// THE SET IS THE TILE'S RANKING. `SqliteMessageStore` orders these facts
    /// <c>Failed › Blocked › Awaiting › Completed</c>; a board that ranked them differently would
    /// contradict the tile above it about one member. Cleared by `container.started` and nothing else - see `HandleContainerStarted`.
    /// </para>
    /// </summary>
    private static bool IsWaitingOnAPerson(CardState state) =>
        state.Status is "blocked" or "needs-decision" or "failed";

    /// <summary>
    /// HOW LOUD EACH OF THE THREE IS - `Failed › Blocked › NeedsDecision`, and every step of it is an
    /// argument rather than an ordering.
    ///
    /// <para>
    /// A platform failure outranks an agent's decision to stop, which outranks an agent's decision
    /// to ask, because a run that died after asking did not get its question answered either. Those
    /// words are `SqliteMessageStore`'s, and this is the FOURTH surface to use them: the workflow
    /// tile, `containerMark()` on a member's card and the tab chip are the other three, and all four
    /// are documented as having to agree. A card that ranked them differently would contradict the
    /// tile directly above it about the same member - which is the whole defect this branch exists
    /// to close.
    /// </para>
    /// </summary>
    private static int Loudness(string status) => status switch
    {
        "failed" => 3,
        "blocked" => 2,
        "needs-decision" => 1,
        _ => 0,
    };

    /// <summary>
    /// WHETHER A NEW HUMAN-ACTION STATUS MAY REPLACE THE ONE THE CARD ALREADY WEARS.
    ///
    /// <para>
    /// Found on `os-kanban`: Dev-Copilot-2 published `blocked` at 13:41 and `needs-decision` at
    /// 13:42, and last-write-wins left the card reading NEEDS DECISION under a tile reading BLOCKED.
    /// Both were true. Only one of them was ranking.
    /// </para>
    ///
    /// <para>
    /// NOT A RATCHET, AND THAT MATTERS AS MUCH AS THE GUARD. This orders facts within one run;
    /// `container.started` resets the card to `running`, so a member told again starts clean rather
    /// than carrying its predecessor's worst moment forever. `>=` rather than `&gt;` so an identical
    /// status re-published still refreshes the card's lane and colour.
    /// </para>
    /// </summary>
    private static bool MayReplace(CardState state, string status) =>
        Loudness(status) >= Loudness(state.Status);

    private static void HandleWorkflowCompleted(
        Message msg,
        Dictionary<string, CardState> cardStates)
    {
        // workflow.completed marks all cards in this workflow as done - EXCEPT the ones still
        // waiting on a person. A manager's declaration is a claim about what it DELIVERED, and
        // sweeping a member's unanswered give-up into `done` propagates that claim over a card
        // whose member never got what it asked for. A manager that genuinely worked around the
        // block has a way to say so: tell the member again, which clears the card at its next wake.
        //
        // THE TRAIL ROW IS SKIPPED TOO, unlike `HandleContainerCompleted` above, and the asymmetry
        // is the point: that row is the member's own run and belongs to this card, while this one
        // says "delivered: ..." about the WORKFLOW. Written onto a card still waiting on a person,
        // it reads as a claim that THIS card was delivered - the very claim being withheld one line
        // up. The declaration is still on the team feed and on every card it does apply to.
        var correlationId = msg.CorrelationId;

        // A card RESUMED in this workflow is this workflow's card too: the declaration was
        // refused while it was open, see `KanbanCard.Workflows`.
        foreach (var state in States(cardStates).Where(s =>
                     s.Workflows.Contains(correlationId) && !IsWaitingOnAPerson(s)))
        {
            state.Status = "done";
            state.LaneId = KanbanLanes.ForStatus("done");
            state.Color = KanbanLanes.ColourForStatus("done");
            Record(state, msg, FirstStringField(msg, PayloadFields.Delivered, PayloadFields.Output));
            state.UpdatedAt = msg.OccurredAt.DateTime;
        }
    }

    /// <summary>
    /// EVERY CARD IN THIS WORKFLOW IS MARKED PAUSED, OR UNMARKED, TOGETHER.
    ///
    /// <para>
    /// Correlation-scoped, the same loop <see cref="HandleWorkflowCompleted"/> above uses, because
    /// the pause is a fact about the WORKFLOW rather than about one card - a team whose workflow
    /// spent its budget has every card under that thread stopped, and one of them saying so while
    /// its siblings do not would be worse than none of them saying so.
    /// </para>
    ///
    /// <para>
    /// <b>NO `IsWaitingOnAPerson` EXCLUSION, unlike the completed handler beside it</b>, and the
    /// difference is what the two rows CLAIM. That one withholds `done` from a card whose member
    /// never got its answer, because marking it done asserts a delivery nobody made. This asserts
    /// nothing about the work: the workflow is stopped, so a card waiting on a person is stopped
    /// too, and telling its reader otherwise would be the error.
    /// </para>
    ///
    /// <para>
    /// <b>`Status` IS UNTOUCHED.</b> See <see cref="KanbanCard.Paused"/> - a member's `blocked`
    /// mark survives a pause and is still there when the workflow resumes.
    /// </para>
    /// </summary>
    private static void HandleWorkflowPaused(
        Message msg,
        Dictionary<string, CardState> cardStates,
        bool paused)
    {
        var correlationId = msg.CorrelationId;

        foreach (var state in States(cardStates).Where(s => s.CorrelationId == correlationId))
        {
            state.Paused = paused;
            Record(state, msg, FirstStringField(msg, PayloadFields.Reason));
            state.UpdatedAt = msg.OccurredAt.DateTime;
        }
    }

    private static void HandleNeedsDecision(
        Message msg,
        Dictionary<string, CardState> cardStates)
    {
        if (CardFor(msg, cardStates) is not { } state) return;

        if (MayReplace(state, "needs-decision"))
        {
            state.Status = "needs-decision";
            state.LaneId = KanbanLanes.ForStatus("needs-decision");
            state.Color = KanbanLanes.ColourForStatus("needs-decision");
        }
        Record(state, msg, FirstStringField(msg, PayloadFields.Question, PayloadFields.Reason, PayloadFields.Output));
        state.UpdatedAt = msg.OccurredAt.DateTime;
    }

    /// <summary>
    /// A move, an edit or a comment on a card, applied.
    ///
    /// THE INPUT THAT DOES NOT ARRIVE FROM A RUN, which is why the projection has to carry it:
    /// setting `awaitingManager` alone left a moved card sitting in the lane it started in, and a
    /// board that answers a drag by rendering the card back where it was is indistinguishable from
    /// a route that did nothing.
    ///
    /// IT IS NOT "A HUMAN'S EDIT". These three routes are reached by whoever holds
    /// `Progress` on the team, which includes its own containers - a Manager answering a card wake
    /// comments on the card or moves it back, exactly as its seeded skill instructs. So the actor
    /// is a question this method has to ask rather than assume.
    /// </summary>
    private static void HandleKanbanCardEvent(
        Message msg,
        Dictionary<string, CardState> cardStates)
    {
        var cardId = ExtractCardIdFromKanbanMessage(msg);
        if (cardId is null || !cardStates.TryGetValue(cardId, out var state))
        {
            return;
        }

        // What the row SAYS, which for these three is the whole reason they are worth keeping. The
        // change is named first and the person's own words follow it, because a note read without
        // the change it accompanied is half a sentence.
        var said = new List<string>();

        if (msg.Type == MessageTypes.KanbanCardMoved)
        {
            if (StringField(msg, PayloadFields.LaneId) is { Length: > 0 } laneId)
            {
                // A row naming a lane the board does not have is still said on the trail but moves nothing: the card stays in the lane
                // its status gives it rather than vanishing from every column.
                if (KanbanLanes.Find(laneId) is { } lane) state.LaneId = lane;
                said.Add($"Moved to {laneId}");
            }
        }
        else if (msg.Type == MessageTypes.KanbanCardEdited)
        {
            if (StringField(msg, PayloadFields.Title) is { Length: > 0 } title)
            {
                // A HAND-TYPED TITLE OBEYS THE SAME 80-CHARACTER CAP AS AN INSTRUCTION-DERIVED ONE,
                // and this line is the decision rather than an oversight.
                //
                // The cap is a fact about the BOARD, not about the author: a title is one line at a
                // glance in a lane a few centimetres wide, and a card does not get more room because
                // a person rather than a projection filled the box. Exempting a human title buys a
                // board whose layout depends on who last touched a card, which is the sort of
                // difference nobody can attribute when they meet it.
                //
                // IT IS NOT LOSSY, WHICH IS WHAT MAKES IT CHEAP: the edit is a row on an append-only
                // log and the trail row immediately below renders that row IN FULL, so the whole of
                // what was typed is on the same screen as the title cut from it.
                //
                // `InstructionText.Split` rather than a clamp of its own, for the reason
                // `ExtractTitleAndBody` gives: a second copy of the rule is how a stored subject and
                // a rendered title start disagreeing. The BODY is untouched - the body is what the
                // instruction said, and renaming a card did not rewrite the work it describes.
                state.Title = InstructionText.Split(title).Subject;
                said.Add($"Title: {title}");
            }

            if (StringField(msg, PayloadFields.Status) is { Length: > 0 } status)
            {
                state.Status = status;
                state.LaneId = KanbanLanes.ForStatus(status);
                state.Color = KanbanLanes.ColourForStatus(status);
                said.Add($"Status: {status}");
            }
        }
        else if (msg.Type == MessageTypes.KanbanCardCommented)
        {
            // NOTHING ELSE MOVES. A comment is not an edit and must not become one - it changes no
            // lane, no status and no title. Until this arm existed it fell through both branches
            // above and the text a person typed was projected NOWHERE.
            said.Add(StringField(msg, PayloadFields.Text) ?? "");
        }

        // THE NOTE, ON BOTH WRITE ROUTES. Move and edit write a `note` into their payloads, and
        // without this read the panel's *"Note (goes with the edit or the move)"* box would go to
        // the log and stop there.
        if (StringField(msg, PayloadFields.Note) is { Length: > 0 } note)
        {
            said.Add(note);
        }

        Record(state, msg, string.Join(" - ", said.Where(part => part.Length > 0)));

        // WHO TOUCHED THE CARD DECIDES THE RING, AND IT IS SET RATHER THAN RAISED.
        //
        // The flag means *"a person changed this card and no container of its team has answered
        // yet"*, which is what the badge on the card says and what the client documents. Raising it
        // on every row of this family was harmless while nothing woke - the flag was decorative and
        // a person was only being shown that their own edit was recent.
        //
        // THE WAKE MADE IT A CLAIM THE BOARD COULD NEVER RETRACT. A Manager woken by an edit
        // answers the way its seeded skill tells it to - it comments, or moves the card back - and
        // that answer is another `kanban.card.*` row. So the ring went straight back up on the
        // Manager's own reply, and every card a Manager had ever touched read as waiting for it.
        //
        // A person publishes as a bare user id; a container publishes its own qualified
        // `Team/Name`. So the question is the CARD'S OWN TEAM: an answer from a container of this
        // team clears it, and anything else - a person, or a container of some other team - raises
        // it. Not keyed on the name `Manager`: a member told to update a card has answered just as
        // much, and a projection has no business knowing `TeamRegistry.DefaultManagerName`.
        state.AwaitingManager = !FromContainerOf(msg, state.Team);
        state.UpdatedAt = msg.OccurredAt.DateTime;
    }

    /// <summary>
    /// Whether this message was published by a container OF <paramref name="team"/>.
    ///
    /// Read off <c>Message.Source</c>, which for a container event is its own qualified id and is
    /// authoritative - see <see cref="MessageTeam.Of"/>, which makes the same split for the same
    /// reason. A person's row has no <c>/</c> at all and so is never one of these.
    ///
    /// Case-insensitive, because <see cref="ContainerId"/> equality is.
    /// </summary>
    private static bool FromContainerOf(Message msg, string team)
    {
        var separator = msg.Source.LastIndexOf('/');

        return separator > 0
            && separator < msg.Source.Length - 1
            && string.Equals(msg.Source[..separator], team, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The card a message is ABOUT, or null when nothing ever requested one.
    ///
    /// A CARD IS A PIECE OF REQUESTED WORK, so an instruction makes one and nothing else does -
    /// which is why this lookup has no creating arm and <see cref="CardForInstruction"/> is the
    /// only place a card is born. Everything that reaches here is an OBSERVATION of a run already
    /// under way: `container.started`, the progress lines and the terminal rows.
    ///
    /// WITHOUT THIS, A BLANK CARD WOULD BE THE COMMONEST CARD ON A LIVE BOARD. A Manager woken by a
    /// human's card edit inherits the EDITED card's correlation, so a creating arm here would open a
    /// second card for its `container.started`, keyed on (that correlation, `Manager`). Nothing
    /// addressed the Manager, so no instruction row exists for it, nothing would ever name that card,
    /// and it would render with a blank header.
    ///
    /// IT IS SAFE BECAUSE THE PROJECTION ALWAYS REPLAYS THE WHOLE LOG. Both read routes call
    /// <c>ReadRangeAsync(0, int.MaxValue)</c>, so the instruction that created a card is always
    /// inside the window - a card can never be missed because the message that made it scrolled
    /// away. A projection over a WINDOW would have to answer this differently, and this comment is
    /// the warning for whoever writes one.
    /// </summary>
    private static CardState? CardFor(Message msg, Dictionary<string, CardState> cardStates) =>
        cardStates.TryGetValue(CreateCardId(msg.CorrelationId, ExtractMember(msg) ?? "unknown"), out var state)
            ? state
            : null;

    /// <summary>
    /// The DISTINCT cards in the projection state.
    ///
    /// Planned cards can also be looked up by the composite `(correlation, member)` key once they
    /// are claimed; that is how later container rows find them. The lookup aliases all point at the
    /// same <see cref="CardState"/>, so any scan over the board state deduplicates on the card's
    /// own id rather than walking every alias as if it were another card.
    /// </summary>
    private static IEnumerable<CardState> States(Dictionary<string, CardState> cardStates) =>
        cardStates.Values.DistinctBy(state => state.Id);

    /// <summary>
    /// The card an instruction is for, made if this is the first message on it.
    ///
    /// THE ONE PLACE A CARD IS CREATED. An instruction is a REQUEST, and a lane holds requested
    /// work; a run that nobody asked for is not pending work and does not belong on the board. See
    /// <see cref="CardFor"/> for what that removed and why replaying the whole log makes it safe.
    ///
    /// The empty <c>Title</c> seeded here is overwritten by the caller on the same pass, out of
    /// <see cref="ExtractTitleAndBody"/> - which answers "Untitled" rather than blank for a row it
    /// cannot name. It is a seed and never a rendered value.
    /// </summary>
    private static CardState CardForInstruction(
        Message msg,
        Dictionary<string, CardState> cardStates,
        string team,
        string member)
    {
        // A CLAIM RESOLVES AN EXISTING CARD AND MINTS NOTHING. `tell --card <id>` puts the card on
        // the instruction; the card keeps its identity and gains a member, and moves out of Todo on
        // the `container.started` that follows.
        //
        // AN INSTRUCTION THAT NAMES NO CARD BEHAVES EXACTLY AS IT ALWAYS HAS, which is the whole of
        // what keeps a team that never touches the backlog unchanged - and what stops the
        // twenty-three acknowledgement-shaped instructions on a workflow like 1400 each becoming a
        // card. The new behaviour is opt-in at the call site, which is also where the judgement is.
        //
        // A CARD MAY BE CLAIMED MORE THAN ONCE AND BY DIFFERENT MEMBERS, because work handed back
        // and re-assigned is ordinary. The member is the LATEST claim; the trail is the history.
        //
        // An unknown card id falls through to the ordinary path rather than being refused here: a
        // projection replays whatever the log holds and cannot reject it, and the route that
        // accepted the instruction is where a bad id belongs.
        if (StringField(msg, PayloadFields.Card) is { Length: > 0 } claimed
            && cardStates.TryGetValue(claimed, out var planned))
        {
            planned.Member = member;
            planned.Team = team;
            planned.JoinWorkflow(msg.CorrelationId);
            cardStates[CreateCardId(msg.CorrelationId, member)] = planned;

            return planned;
        }

        var cardId = CreateCardId(msg.CorrelationId, member);

        if (!cardStates.TryGetValue(cardId, out var state))
        {
            state = new CardState
            {
                Id = cardId,
                CorrelationId = msg.CorrelationId,
                Member = member,
                Team = team,
                Title = "",
                Body = "",
                Status = "queued",
                LaneId = "todo",
                Color = "slate",
                Progress = new List<ProgressItem>(),
                Trail = new List<KanbanTrailEntry>(),
                CreatedAt = msg.OccurredAt.DateTime,
                UpdatedAt = msg.OccurredAt.DateTime,
                AwaitingManager = false,
            };
            state.JoinWorkflow(msg.CorrelationId);
            cardStates[cardId] = state;
        }

        return state;
    }

    /// <summary>
    /// Which member a message is about - the other half of what <see cref="MessageTeam.Of"/> reads,
    /// out of the same qualified id and for the same reason.
    ///
    /// An addressed instruction carries the container on its TYPE
    /// (<c>container.instruction.Team/Name</c>) and nowhere else: a live tell's payload is
    /// <c>{ "instruction": "…" }</c> alone, and its source is whatever the caller supplied - for a
    /// CLI tell that is the bare word <c>console</c>. Reading the source, or a payload `member`
    /// field nothing writes, made every console-issued card `unknown`, which is one card per
    /// workflow all colliding on one id.
    ///
    /// A container event needs no such care: it publishes its own qualified id as the source, which
    /// is authoritative.
    /// </summary>
    private static string? ExtractMember(Message msg)
    {
        var qualified = msg.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
            ? msg.Type[MessageTypes.InstructionPrefix.Length..]
            : msg.Source;

        var separator = qualified.LastIndexOf('/');

        return separator <= 0 || separator == qualified.Length - 1
            ? null
            : qualified[(separator + 1)..];
    }

    /// <summary>
    /// The card's title and its body, out of a tell's payload.
    ///
    /// TWO SHAPES, BOTH LIVE. A row may carry `subject` and `body` beside the instruction, or
    /// `instruction` alone. The log is append-only and this is a REPLAY, so a projection meets both
    /// on the same board - the fallback is permanent.
    ///
    /// The fallback is <see cref="InstructionText.Split"/> rather than a clamp of its own, so a card
    /// built from either shape reads identically for the same text. A
    /// second copy of the rule here is how a stored subject and a rendered title start disagreeing.
    /// </summary>
    private static (string Title, string Body) ExtractTitleAndBody(Message msg)
    {
        if (StringField(msg, "subject") is { Length: > 0 } subject)
        {
            return (subject, StringField(msg, "body") ?? "");
        }

        if (StringField(msg, PayloadFields.Instruction) is { Length: > 0 } text)
        {
            var parts = InstructionText.Split(text);
            return (parts.Subject, parts.Body);
        }

        // What an absent subject renders as is this projection's decision and nobody else's, which
        // is why InstructionText answers an empty string and not this word.
        return ("Untitled", "");
    }

    /// <summary>`harness progress "…"` puts the line on the payload's `status` field.</summary>
    private static string ExtractProgressText(Message msg) => StringField(msg, PayloadFields.Status) ?? "";

    private static string? ExtractCardIdFromKanbanMessage(Message msg) => StringField(msg, PayloadFields.CardId);

    /// <summary>
    /// One message, onto the card it belongs to, as a row of that card's trail.
    ///
    /// EVERY HANDLER RECORDS ITS OWN, which is what keeps the trail and the card's lane, status and
    /// progress deciding from the SAME message. A trail assembled afterwards - by correlation, or
    /// by member - would have to re-derive which card a message belongs to, and for a console-issued
    /// instruction that answer lives on the message TYPE and nowhere else. Two derivations of one
    /// fact is how a card ends up showing somebody else's history.
    /// </summary>
    private static void Record(CardState state, Message msg, string text) =>
        state.Trail.Add(new KanbanTrailEntry(msg.Seq, msg.Type, msg.Source, msg.OccurredAt, text));

    /// <summary>
    /// The first of <paramref name="names"/> the payload carries as a non-empty string, or "".
    ///
    /// ORDERED, and the order is a decision each caller makes - see the failure handler, where a
    /// launch error must beat an output that is empty precisely because the run never began.
    /// </summary>
    private static string FirstStringField(Message msg, params string[] names)
    {
        foreach (var name in names)
        {
            if (StringField(msg, name) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return "";
    }

    /// <summary>One string field out of a payload, or null - including when the payload is not JSON
    /// at all. The log holds the payload opaquely, so a projection cannot assume its shape.</summary>
    private static string? StringField(Message msg, string name)
    {
        if (string.IsNullOrEmpty(msg.Payload))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(msg.Payload);

            return json.RootElement.ValueKind is JsonValueKind.Object
                && json.RootElement.TryGetProperty(name, out var field)
                && field.ValueKind is JsonValueKind.String
                    ? field.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One number out of a payload, or null. Guarded on <c>ValueKind</c> rather than trusting the
    /// sender: a renderer over a wire payload does not get to assume the sender's nullability, and
    /// a projection reads payloads it did not write.
    /// </summary>
    private static long? LongField(Message msg, string name)
    {
        if (string.IsNullOrEmpty(msg.Payload)) return null;

        try
        {
            using var json = JsonDocument.Parse(msg.Payload);

            return json.RootElement.ValueKind is JsonValueKind.Object
                && json.RootElement.TryGetProperty(name, out var field)
                && field.ValueKind is JsonValueKind.Number
                && field.TryGetInt64(out var value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CreateCardId(long correlationId, string member) =>
        $"{correlationId}_{member}";

    /// <summary>
    /// Internal state for a card during projection.
    /// </summary>
    private sealed class CardState
    {
        public required string Id { get; set; }
        public required long CorrelationId { get; set; }

        /// <summary>
        /// Who is on this card now. Null on a PLANNED card nobody has claimed, and set back to null
        /// when the member it names is deleted - see <see cref="KanbanCard.Member"/>.
        /// </summary>
        public required string? Member { get; set; }

        /// <summary>The backlog item this card belongs to, or null. See <see cref="KanbanCard.Item"/>.</summary>
        public long? Item { get; set; }
        public required string Team { get; set; }
        public required string Title { get; set; }

        /// <summary>
        /// Everything the instruction said that its subject did not. EMPTY, NEVER NULL: a card
        /// whose whole instruction fits in its title has no body, and a client that has to tell
        /// an absent body from an empty one is being asked a question with one answer.
        /// </summary>
        public required string Body { get; set; }
        public required string Status { get; set; }
        public required string LaneId { get; set; }
        public required string Color { get; set; }
        public required List<ProgressItem> Progress { get; set; }

        /// <summary>
        /// Every message this card is made of, in the order the handlers met them. Ordered ON READ
        /// rather than trusted here - see <see cref="ToDetail"/>.
        /// </summary>
        public required List<KanbanTrailEntry> Trail { get; set; }
        public required DateTime CreatedAt { get; set; }
        public required DateTime UpdatedAt { get; set; }
        public required bool AwaitingManager { get; set; }

        /// <summary>Whether this card's WORKFLOW is paused for its spend. Not `required`: every
        /// construction site would otherwise have to write `Paused = false`, which is the default
        /// and says nothing.</summary>
        public bool Paused { get; set; }

        public bool Created { get; set; }

        /// <summary>The card's foreign tools mark, or null. See <see cref="KanbanCard.ForeignTools"/>.</summary>
        public string? ForeignTools { get; set; }

        /// <summary>Every workflow this card was planned, claimed or told in, oldest first. See
        /// <see cref="KanbanCard.Workflows"/>.</summary>
        public List<long> Workflows { get; } = [];

        public void JoinWorkflow(long correlation)
        {
            if (!Workflows.Contains(correlation)) Workflows.Add(correlation);
        }

        /// <summary>
        /// The card WITH its trail, oldest-first.
        ///
        /// SORTED BY SEQ HERE rather than relying on the order the messages arrived in. The board
        /// reads the log in order today, so this is usually a no-op - but "oldest-first" is the
        /// whole readability of a trail, and a projection that inherits it from its caller has a
        /// guarantee only for as long as nobody changes how the log is read. Seq is the message's
        /// identity and is monotonic per append, so it is the one key that cannot disagree with the
        /// order things happened in; a clock can, across a restart or a machine.
        /// </summary>
        public KanbanCardDetail ToDetail() =>
            new(ToCard(), Trail.OrderBy(entry => entry.Seq).ToList());

        public KanbanCard ToCard() => new(
            Id,
            CorrelationId,
            Team,
            Member,
            Item,
            Title,
            Body,
            Status,
            LaneId,
            Color,
            Progress,
            CreatedAt,
            UpdatedAt,
            AwaitingManager,
            Paused,
            Workflows.ToList(),
            ForeignTools: ForeignTools);
    }
}

/// <summary>The marks an <c>agent.foreignTools</c> row puts on a card, strongest first.</summary>
public static class KanbanForeignTools
{
    /// <summary>A run on this card CALLED a tool the platform did not give it.</summary>
    public const string Called = "called";

    /// <summary>A run on this card was offered one, and called none.</summary>
    public const string Offered = "offered";

    /// <summary>A run on this card could not be checked: its transcript does not list what was offered.</summary>
    public const string NotMeasured = "notMeasured";

    /// <summary>A run on this card offered no foreign server, but its preset declares no allowed tools.</summary>
    public const string NotVerified = "notVerified";

    private static int Rank(string? mark) => mark switch
    {
        Called => 4,
        Offered => 3,
        NotMeasured => 2,
        NotVerified => 1,
        _ => 0,
    };

    public static string? Worse(string? a, string? b) => Rank(b) > Rank(a) ? b : a;

    /// <summary>The mark one row makes.</summary>
    public static string? Of(Message msg)
    {
        try
        {
            using var document = JsonDocument.Parse(msg.Payload);
            var root = document.RootElement;
            if (!root.TryGetProperty(PayloadFields.ForeignToolsStatus, out var status)) return null;

            return status.GetString() switch
            {
                "foreign" => root.TryGetProperty(PayloadFields.ForeignCalled, out var called)
                    && called.ValueKind == JsonValueKind.Array && called.GetArrayLength() > 0
                        ? Called
                        : Offered,
                "notMeasured" => NotMeasured,
                "notVerified" => NotVerified,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
