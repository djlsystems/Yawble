namespace Harness.Contracts;

/// <summary>Who puts a row of this type on the log.</summary>
public enum EventPublisher
{
    /// <summary>The platform itself - a container's run loop, the scheduler, the Host.</summary>
    Platform,

    /// <summary>An agent, through the CLI.</summary>
    Agent,

    /// <summary>A person, through the SPA. The only rows on the log a human publishes directly.</summary>
    Person,
}

/// <summary>`List` is a JSON array of strings - `file.changed`'s `changed`. A filter or an
/// `{event.*}` token reads it as its raw JSON text.</summary>
public enum EventFieldKind { String, Integer, Boolean, List }

/// <param name="Name">The key inside the payload. A <see cref="PayloadFields"/> constant, never a
/// literal.</param>
public sealed record EventField(string Name, EventFieldKind Kind, string Summary);

/// <param name="HighVolume">
/// Whether this type is published once per STATUS LINE rather than once per run.
///
/// The one rule that reads it: a container whose Agent preset is a language model may not subscribe
/// to a high-volume type. A cheap subscriber may - a procedural aggregator over `agentContainer.progress`
/// is an observability sink, not a runaway. The cost is what makes the difference, which is why the
/// flag is HighVolume and not `Subscribable`.
/// </param>
/// <param name="InLedger">
/// Whether this type reaches an agent's context. FALSE for `agentContainer.progress`, so a chatty member
/// does not re-read and re-pay for its own commentary on every later invocation; TRUE for
/// `agentContainer.blocked`, so a manager that gave up reads that back instead of retrying what it
/// abandoned.
///
/// It is an exclusion at the PROJECTION and must never become a reason not to APPEND a row - the
/// ledger is a projection, so excluded rows are still on the log, in the activity feed and in a
/// workflow view. Excluding by not writing would not be reversible.
/// </param>
public sealed record EventDefinition(
    string Type,
    EventPublisher Publisher,
    bool HighVolume,
    bool InLedger,
    IReadOnlyList<EventField> Fields,
    string Summary);

/// <summary>
/// EVERY EVENT TYPE THIS PLATFORM CAN APPEND, AND WHAT IS TRUE OF EACH.
///
/// <para>
/// The knowledge lives here rather than in XML doc comments on <see cref="MessageTypes"/>, because
/// there no check could see it, no rename would move it, and no screen could list it - and a type
/// nobody can list is a type the activity feed silently misses.
/// </para>
///
/// <para>
/// IT NAMES NO TYPE AS A LITERAL. Every <see cref="EventDefinition.Type"/> is a
/// <see cref="MessageTypes"/> constant, and every <see cref="EventField.Name"/> is a
/// <see cref="PayloadFields"/> constant - pinned by `EventCatalogTests`. A catalog that re-spelt
/// either would be a second store of the fact it exists to make checkable.
/// </para>
///
/// <para>
/// EVERY FIELD LIST WAS READ OFF ITS PUBLISHER, NOT OFF `MessageText`'s read-sites - a reader can
/// try several field names as a fallback chain without any of the later ones ever being written, and
/// trusting that shape would declare fields that can never appear. `agentContainer.completed` and
/// `agentContainer.failed` share one JSON object built at exactly one call site
/// (<c>MemberRuntime.RunOneAsync</c>), so they carry IDENTICAL fields, `launchError` included on a
/// success and `output` included on a failure - both usually empty, neither absent.
/// </para>
///
/// <para>
/// PREFIX FAMILIES ARE NOT HERE. `agentContainer.instruction.*` is per-container and reachable by
/// construction, because a container always subscribes to its own.
/// </para>
/// </summary>
public static class EventCatalog
{
    /// <summary>
    /// THE ENVELOPE FIELD, DECLARED ONCE AND SHARED BY EVERY ENTRY - never a `container` field
    /// re-declared type by type. Resolved from <c>Message.Source</c>, never read out of the
    /// payload: see <c>ContainerHost.EventFieldsOf</c>, the one place that fills it in for
    /// `{event.source}` and for <see cref="TriggerFilter"/>. Never the bare word "container" in
    /// its own description - see `EventCatalogTests.No_summary_names_a_bare_container`.
    ///
    /// THE DESCRIPTION MUST BE TRUE FOR EVERY TYPE THAT DECLARES IT, not merely the common case.
    /// `agentContainer.scheduleSkipped`'s `Message.Source` is `schedule:&lt;id&gt;` - neither an
    /// agent container nor a person - which "The agent container (or person) that published the
    /// event" quietly lied about until this was checked type by type. Every other declaring type's
    /// `Source` genuinely is an agent container's own identity (`Started`, `Completed`, `Failed`,
    /// `Rejected`, `Progress`, `Blocked`, `NeedsDecision`, `WorkflowCompleted` all publish as
    /// `container.Id.ToString()`) or a person's bare id (`WorkflowClosed`, every `KanbanCard*` -
    /// published as the authenticated caller's id) - see this file's own field lists for where each
    /// was read off its publisher.
    /// </summary>
    private static readonly EventField Source =
        new(PayloadFields.Source, EventFieldKind.String,
            "What published this event: the agent container, a person, or the schedule or trigger that fired it.");

    private static readonly EventField CardId =
        new(PayloadFields.CardId, EventFieldKind.String, "Which card.");

    private static readonly EventField Actor =
        new(PayloadFields.Actor, EventFieldKind.String, "The person who acted, as a bare user id.");

    private static readonly EventField Team =
        new(PayloadFields.Team, EventFieldKind.String,
            "The team, carried in the payload because the publisher is a person whose Source has none.");

    private static readonly EventField Member =
        new(PayloadFields.Member, EventFieldKind.String, "The member this card belongs to.");

    private static readonly EventField Change =
        new(PayloadFields.Change, EventFieldKind.String, "What changed, composed by the route and carrying both values.");

    private static readonly EventField Note =
        new(PayloadFields.Note, EventFieldKind.String, "What the person typed. Optional.");

    public static IReadOnlyList<EventDefinition> All { get; } =
    [
        new(MessageTypes.Started, EventPublisher.Platform, HighVolume: false, InLedger: true,
            [Source, new(PayloadFields.Trigger, EventFieldKind.String, "What woke this run - the type of the message that caused it.")],
            "A member began a run."),

        // agentContainer.completed and agentContainer.failed share one call site and one JSON object, so
        // their field lists are IDENTICAL - see this file's own doc comment.
        new(MessageTypes.Completed, EventPublisher.Platform, HighVolume: false, InLedger: true,
            [
                Source,
                new(PayloadFields.ExitCode, EventFieldKind.Integer, "The process exit code."),
                new(PayloadFields.Output, EventFieldKind.String, "What the run reported."),
                new(PayloadFields.OutputLength, EventFieldKind.Integer, "The length of the untruncated output."),
                new(PayloadFields.Transcript, EventFieldKind.String, "Where the full transcript was written."),
                new(PayloadFields.AgentTranscript, EventFieldKind.String, "The agent's own session transcript for this run, on the row that carries its usage. Absent when there is none."),
                new(PayloadFields.AgentTranscriptFormat, EventFieldKind.String, "The live view format that transcript is read in. Absent with it."),
                new(PayloadFields.LaunchError, EventFieldKind.String, "Null on an ordinary completion."),
                new(PayloadFields.TokensIn, EventFieldKind.Integer, "Absent, not zero, when usage is unknown."),
                new(PayloadFields.TokensOut, EventFieldKind.Integer, "Absent, not zero, when usage is unknown."),
                new(PayloadFields.TokensCachedIn, EventFieldKind.Integer, "Absent, not zero, when usage is unknown."),
                new(PayloadFields.TokensReasoning, EventFieldKind.Integer, "Absent, not zero, when usage is unknown."),
                new(PayloadFields.TokensSource, EventFieldKind.String, "Which brand's usage format was parsed, if any."),
                new(PayloadFields.UsageCountedOn, EventFieldKind.Integer, "Set when this run answered several deliveries: the causation of the row that carries its token figures. This row carries none."),

                // DECLARED ON BOTH ARMS BECAUSE THERE IS ONE JSON OBJECT. These two entries share a
                // call site, so a field added to one is written - as null - by the other, and
                // `EventCatalogTests.Completed_and_failed_declare_the_same_fields` is what says so.
                // Only the DESCRIPTIONS differ, exactly as `launchError`'s already do.
                new(PayloadFields.FailureClass, EventFieldKind.String, "Null on an ordinary completion."),
                new(PayloadFields.RetryAfter, EventFieldKind.String, "Null on an ordinary completion."),
            ],
            "A member finished a run."),

        new(MessageTypes.Failed, EventPublisher.Platform, HighVolume: false, InLedger: true,
            [
                Source,
                new(PayloadFields.ExitCode, EventFieldKind.Integer, "The process exit code."),
                new(PayloadFields.Output, EventFieldKind.String, "What the run reported, if anything."),
                new(PayloadFields.OutputLength, EventFieldKind.Integer, "The length of the untruncated output."),
                new(PayloadFields.Transcript, EventFieldKind.String, "Where the full transcript was written."),
                new(PayloadFields.AgentTranscript, EventFieldKind.String, "The agent's own session transcript for this run, on the row that carries its usage. Absent when there is none."),
                new(PayloadFields.AgentTranscriptFormat, EventFieldKind.String, "The live view format that transcript is read in. Absent with it."),
                new(PayloadFields.LaunchError, EventFieldKind.String, "Why the run could not start or did not complete."),
                new(PayloadFields.TokensIn, EventFieldKind.Integer, "Absent, not zero, when usage is unknown."),
                new(PayloadFields.TokensOut, EventFieldKind.Integer, "Absent, not zero, when usage is unknown."),
                new(PayloadFields.TokensCachedIn, EventFieldKind.Integer, "Absent, not zero, when usage is unknown."),
                new(PayloadFields.TokensReasoning, EventFieldKind.Integer, "Absent, not zero, when usage is unknown."),
                new(PayloadFields.TokensSource, EventFieldKind.String, "Which brand's usage format was parsed, if any."),
                new(PayloadFields.UsageCountedOn, EventFieldKind.Integer, "Set when this run answered several deliveries: the causation of the row that carries its token figures. This row carries none."),
                new(PayloadFields.FailureClass, EventFieldKind.String,
                    "What KIND of failure this was - quota, rate, transport, agent-fault, timeout, "
                    + "interrupted, or unknown. Absent on every row written before classes existed; "
                    + "`unknown` is the default and is treated exactly as agent-fault."),
                new(PayloadFields.RetryAfter, EventFieldKind.String,
                    "When the provider said to come back, as an ISO-8601 instant. Absent, not "
                    + "invented, when nobody stated one."),
            ],
            "The PLATFORM reporting a run that did not complete - a launch error, a timeout, a Host restart."),

        new(MessageTypes.Rejected, EventPublisher.Platform, HighVolume: false, InLedger: true,
            [
                Source,
                new(PayloadFields.Reason, EventFieldKind.String, "Why the work was refused."),
                new(PayloadFields.Ceiling, EventFieldKind.Integer, "The queue depth it was refused at."),
                new(PayloadFields.Refused, EventFieldKind.String, "The message TYPE that could not be queued."),
                new(PayloadFields.RefusedSeq, EventFieldKind.Integer, "The seq of the message that could not be queued."),
            ],
            "Work refused because the queue was at its ceiling. Nothing was queued."),

        // `text` does not exist; it was inferred, not read off the publisher.
        new(MessageTypes.Progress, EventPublisher.Agent, HighVolume: true, InLedger: false,
            [
                Source,
                new(PayloadFields.Status, EventFieldKind.String, "The status line."),
                new(PayloadFields.WhileIdle, EventFieldKind.Boolean,
                    "Written ONLY when true - a report that arrived after the run it belongs to had already ended."),
            ],
            "What a member says about itself, mid-run. Published once per status line."),

        new(MessageTypes.Blocked, EventPublisher.Agent, HighVolume: false, InLedger: true,
            [
                Source,
                new(PayloadFields.Reason, EventFieldKind.String, "Why the agent stopped."),
                new(PayloadFields.Item, EventFieldKind.Integer,
                    "Which batched prompt item (1-based) is abandoned, when only one is. Optional."),
            ],
            "The AGENT reporting that it has stopped without finishing, and why."),

        new(MessageTypes.NeedsDecision, EventPublisher.Agent, HighVolume: false, InLedger: true,
            [Source, new(PayloadFields.Question, EventFieldKind.String, "What needs answering.")],
            "A member asking a person for a decision before it can continue."),

        // THE HAND-BACK, AND IT SITS BESIDE `blocked` RATHER THAN BESIDE `workflow.completed`
        // because the publisher is the same - a worker, by the `Progress` permit it already holds -
        // and the difference from `blocked` is the CLAIM, not the authority.
        //
        // IN THE LEDGER, for the reason `blocked` is and the mirror of it: a manager must read back
        // that a member handed work over, or it re-dispatches what has already been delivered.
        // `progress` is the type excluded for volume, and one row per finished card is not that.
        //
        // IT REUSES `delivered` RATHER THAN MINTING A FIELD. `workflow.completed` already carries
        // what was delivered under that name and the question this answers is the same one; a
        // second spelling for one idea is how two renderers come to disagree about which field
        // holds the words.
        new(MessageTypes.Handback, EventPublisher.Agent, HighVolume: false, InLedger: true,
            [
                Source,
                new(PayloadFields.Delivered, EventFieldKind.String,
                    "What the member says it finished and is handing back."),
            ],
            "A WORKER saying its own part is done and nothing is owed. A success, distinct from "
            + "`agentContainer.blocked` - which is a member giving up - and it does NOT close the "
            + "workflow: only a Manager declares one complete. The one agent-published type a "
            + "Manager SUBSCRIBES to, so a hand-back wakes it on the same correlation."),

        new(MessageTypes.WorkflowCompleted, EventPublisher.Agent, HighVolume: false, InLedger: true,
            [Source, new(PayloadFields.Delivered, EventFieldKind.String, "What the manager says it delivered.")],
            "A MANAGER's declaration that a workflow is complete, validated against live team state."),

        // `team` IS PRESENT here and is NOT on WorkflowCompleted beside it: this row is published by
        // a PERSON, whose Source carries no team at all, so MessageTeam.Of would recover nothing
        // without it - see PayloadFields.Team. `source` still resolves - it is the person's own id.
        new(MessageTypes.WorkflowClosed, EventPublisher.Person, HighVolume: false, InLedger: true,
            [Source, Team, new(PayloadFields.Reason, EventFieldKind.String, "Why it was closed. Optional.")],
            "A PERSON ending a workflow, which is not a claim that anything was delivered."),

        // ONE WORKFLOW STOPPED BY ITS OWN SPEND FIGURE, and the person's release of it.
        //
        // `team` IS PRESENT ON BOTH, for the reason WorkflowClosed above carries it: the resumed
        // row is published by a PERSON whose Source has no team in it. The paused row's Source is
        // a qualified container id and would resolve without it - it writes the field anyway so
        // MessageTeam.Of has one rule for the pair rather than two that happen to agree.
        //
        // InLedger ON BOTH. A Manager that cannot read back that its workflow was paused
        // re-dispatches into a pause, and one that cannot read the release does not know it may
        // start again. HighVolume false: at most one of each per pause episode.
        new(MessageTypes.WorkflowPaused, EventPublisher.Platform, HighVolume: false, InLedger: true,
            [
                Source,
                Team,
                new(PayloadFields.Reason, EventFieldKind.String,
                    "Why it paused, naming the figure and which of the two bounds it was."),
                new(PayloadFields.Limit, EventFieldKind.Integer,
                    "The per-workflow figure that was in force."),
                new(PayloadFields.Spent, EventFieldKind.Integer,
                    "Tokens spent since the last nudge, which is what the bound was evaluated against."),
                new(PayloadFields.LimitSource, EventFieldKind.String,
                    "Which figure fired: `team` or `instance`."),
            ],
            "ONE WORKFLOW paused because it reached the per-workflow spend figure in force for its "
            + "team. Not a team pause - every other open workflow on that team keeps running - and "
            + "the way back is a person at the resume route."),

        new(MessageTypes.WorkflowResumed, EventPublisher.Person, HighVolume: false, InLedger: true,
            [
                Source,
                Team,
                new(PayloadFields.Reason, EventFieldKind.String, "Why it was resumed. Optional."),
            ],
            "A PERSON releasing one paused workflow. It clears no counter: the spend window moves "
            + "because the same route writes an instruction caused by the correlation, which is "
            + "what a nudge is."),

        // `member` IS NOT A DUPLICATE OF `Source` HERE, unlike every other type's identity field -
        // this row's Source is `schedule:<id>` (see TriggerSweep.SourceOf), never the member the
        // wake was for. `member` is the ONLY carrier of that identity, which is why it is a
        // payload field rather than being left to `Source` the way every other type's is.
        new(MessageTypes.ScheduleSkipped, EventPublisher.Platform, HighVolume: false, InLedger: false,
            [
                Source,
                new(PayloadFields.Member, EventFieldKind.String,
                    "The member whose scheduled wake was skipped."),
                new(PayloadFields.Reason, EventFieldKind.String, "Why the wake was skipped."),
            ],
            "A scheduled wake deliberately not queued because the member was busy and the schedule was idle-only."),

        new(MessageTypes.KanbanCardMoved, EventPublisher.Person, HighVolume: false, InLedger: true,
            [
                Source,
                CardId,
                new(PayloadFields.LaneId, EventFieldKind.String, "The lane the card moved to."),
                Note,
                Actor,
                Team,
                Member,
                Change,
            ],
            "A person moved a card on the board."),

        new(MessageTypes.KanbanCardEdited, EventPublisher.Person, HighVolume: false, InLedger: true,
            [
                Source,
                CardId,
                new(PayloadFields.Title, EventFieldKind.String, "The new title. Null when title did not move."),
                new(PayloadFields.PreviousTitle, EventFieldKind.String, "Null where the title did not move."),
                new(PayloadFields.Status, EventFieldKind.String, "The new status. Null when status did not move."),
                new(PayloadFields.PreviousStatus, EventFieldKind.String, "Null where the status did not move."),
                Note,
                Actor,
                Team,
                Member,
                Change,
            ],
            "A person edited a card on the board."),

        new(MessageTypes.KanbanCardCommented, EventPublisher.Person, HighVolume: false, InLedger: true,
            [Source, CardId, new(PayloadFields.Text, EventFieldKind.String, "The comment."), Actor, Team, Member],
            "A person commented on a card."),

        new(MessageTypes.KanbanCardPlanned, EventPublisher.Person, HighVolume: false, InLedger: true,
            [
                Source,
                new(PayloadFields.BacklogItem, EventFieldKind.Integer,
                    "The backlog item this card is a piece of. Null for a card belonging to none."),
                new(PayloadFields.Title, EventFieldKind.String, "What the piece of work is."),
                new(PayloadFields.Body, EventFieldKind.String, "The rest of it. Empty, never absent."),
                Team,
            ],
            "A card planned with NOBODY on it, so a spec can be cut into pieces before anybody is "
            + "told to do them. The card's id is this row's own seq. Published by a route - never by "
            + "an agent container - which is what makes reading its team out of the payload safe."),

        new(MessageTypes.ContainerRemoved, EventPublisher.Platform, HighVolume: false, InLedger: true,
            [Source],
            "A member was deleted from its team. ONE row however many cards it held: the projection "
            + "unassigns every card naming it, and a row per card would put the projection's own "
            + "output into its input. Its team comes from Source. Nothing subscribes to it."),

        new(MessageTypes.BacklogItemDispatched, EventPublisher.Person, HighVolume: false, InLedger: true,
            [
                Source,
                new(PayloadFields.BacklogItem, EventFieldKind.Integer, "The item that was dispatched."),
                new(PayloadFields.Title, EventFieldKind.String, "Its title, as it read at dispatch."),
                Team,
            ],
            "A backlog item was handed to a team. ITS SEQ IS THE CORRELATION ROOT of the workflow "
            + "that follows, so a dispatch IS a workflow and cards, spend and the completion "
            + "refusal all key on it unchanged."),

        // `Source` IS THE ACCEPTING MEMBER, not the publisher, and this pair takes that shape from
        // `agentContainer.failed` beside it: the PLATFORM publishes both, about a member, as that
        // member's own qualified id - which is also what lets `MessageTeam.Of` recover the team with
        // no payload field and no new arm.
        //
        // OUT OF THE LEDGER, and it is the `agentContainer.progress` argument exactly. This is a
        // receipt for a person and for the feed; a member that re-read "the platform pushed your
        // branch" on every later invocation would pay for it every time and learn nothing it can
        // act on. Its FAILING sibling below is IN the ledger for the mirror-image reason.
        new(MessageTypes.RepoPushed, EventPublisher.Platform, HighVolume: false, InLedger: false,
            [
                Source,
                new(PayloadFields.Repo, EventFieldKind.String,
                    "Which repository, by the folder name derived from its URL - never the URL."),
                new(PayloadFields.Branches, EventFieldKind.String,
                    "The branches that reached origin, comma-separated. Never `main`."),
            ],
            "The platform pushed a team's branches to origin, BEFORE the acceptance that claims the "
            + "work. Not published when nothing needed pushing, so a team with no code stays silent."),

        // IN THE LEDGER, unlike its succeeding sibling above, for the reason `agentContainer.blocked`
        // is: a team whose work did not reach origin must read that back on its next wake rather
        // than carry on believing the card. This is the one arm of the pair anybody can act on.
        new(MessageTypes.RepoPushFailed, EventPublisher.Platform, HighVolume: false, InLedger: true,
            [
                Source,
                new(PayloadFields.Repo, EventFieldKind.String,
                    "Which repository, by the folder name derived from its URL - never the URL."),
                new(PayloadFields.Branches, EventFieldKind.String,
                    "The branches that did NOT reach origin, comma-separated."),
                new(PayloadFields.Reason, EventFieldKind.String,
                    "What git said, with any credential in a remote URL redacted at the write."),
                new(PayloadFields.Retryable, EventFieldKind.Boolean,
                    "True for transport (unreachable origin, refused credential, timeout): nothing "
                    + "moved on origin and the same push can succeed later. False when origin refused "
                    + "the branch because it was rewritten after it was pushed; retrying fails every "
                    + "time until a person reconciles it. On the ROW because the summary beside it and "
                    + "the sentence MessageText composes are neither of them persisted."),
            ],
            "The platform could not publish a team's work to origin. Its own condition rather than a "
            + "run failure. Transport is not an agent fault and is safe to repeat; a refused, "
            + "rewritten branch is not, and does not hold back the team's other branches."),

        // OUT OF THE LEDGER: a person's to act on in the Git dialog, not something a member can fix
        // on its next wake - members never remove trees.
        new(MessageTypes.RepoWorktreeLeft, EventPublisher.Platform, HighVolume: false, InLedger: false,
            [
                Source,
                new(PayloadFields.Repo, EventFieldKind.String,
                    "Which repository, by the folder name derived from its URL - never the URL."),
                new(PayloadFields.Worktree, EventFieldKind.String, "The tree left on disk, as an absolute path."),
                new(PayloadFields.Reason, EventFieldKind.String,
                    "Why it was left: what `git worktree remove` said, or that its commits are not on origin."),
            ],
            "A settled card's worktree was not removed, because removal is never forced: it has "
            + "uncommitted edits, commits not on origin, or git refused it. Source is the member the "
            + "tree belongs to."),

        // OUT OF THE LEDGER for `repo.pushed`'s reason: a receipt for a person, nothing a
        // member acts on. `team` IS IN THE PAYLOAD because the publisher is the person who pressed
        // Bring current, whose Source carries no team.
        new(MessageTypes.RepoForkSynced, EventPublisher.Platform, HighVolume: false, InLedger: false,
            [
                Source,
                Team,
                new(PayloadFields.Repo, EventFieldKind.String,
                    "Which repository, by the folder name derived from its URL - never the URL."),
                new(PayloadFields.Branch, EventFieldKind.String,
                    "The default branch the fork was fast-forwarded on."),
                new(PayloadFields.Count, EventFieldKind.Integer,
                    "How many upstream commits the fork's branch gained."),
            ],
            "A contributor-mode repository's fork was brought level with upstream: its default branch "
            + "was fast-forwarded to upstream's, never forced, by Bring current in the Git dialog. "
            + "Appended after the push succeeded. Source is the person."),

        // OUT OF THE LEDGER for `repo.forkSynced`'s reason. `team` IS IN THE PAYLOAD because
        // the publisher is the person who pressed Open pull request.
        new(MessageTypes.RepoPullRequestOpened, EventPublisher.Platform, HighVolume: false, InLedger: false,
            [
                Source,
                Team,
                new(PayloadFields.Repo, EventFieldKind.String,
                    "Which repository, by the folder name derived from its URL - never the URL."),
                new(PayloadFields.Branch, EventFieldKind.String,
                    "The fork's branch the pull request is from: team/{id}."),
                new(PayloadFields.Url, EventFieldKind.String, "The pull request's URL on the upstream."),
                new(PayloadFields.Number, EventFieldKind.Integer, "The pull request's number on the upstream."),
            ],
            "A person opened a pull request on a contributor-mode repository's upstream from the Git "
            + "dialog. Appended after GitHub opened it. The platform never opens one by itself. Source is the person."),

        // `team` IS IN THE PAYLOAD because neither publisher's Source carries one: a poll
        // publishes as `trigger:<id>` and the Documents dialog as the person. Both are routes or
        // the runner - nothing outside can write this type - which is what lets MessageTeam read it.
        new(MessageTypes.FileChanged, EventPublisher.Platform, HighVolume: false, InLedger: true,
            [
                Source,
                Team,
                new(PayloadFields.Root, EventFieldKind.String,
                    "Where the folder is: `documents`, or `root:<name>` for a file-browser root."),
                new(PayloadFields.Path, EventFieldKind.String,
                    "The folder that changed, relative to the root."),
                new(PayloadFields.Changed, EventFieldKind.List,
                    "The changed files' paths relative to the root, at most 100 of them."),
                new(PayloadFields.Count, EventFieldKind.Integer,
                    "How many files changed, even when the list was cut."),
            ],
            "Files were added, removed or resized in a watched folder, or a person uploaded or "
            + "deleted one through the Documents dialog. Paths and counts only, never contents. "
            + "Source is the folder trigger that polled (`trigger:<id>`) or the person."),
    ];

    public static IReadOnlySet<string> Types { get; } =
        All.Select(e => e.Type).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> ExcludedFromLedger { get; } =
        All.Where(e => !e.InLedger).Select(e => e.Type).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> HighVolumeTypes { get; } =
        All.Where(e => e.HighVolume).Select(e => e.Type).ToHashSet(StringComparer.Ordinal);

    private static readonly Dictionary<string, EventDefinition> ByType =
        All.ToDictionary(e => e.Type, StringComparer.Ordinal);

    /// <summary>
    /// The definition for a type, or NULL.
    ///
    /// NULL RATHER THAN A FALLBACK, which is `AgentCatalog.For`'s rule and exists for the same
    /// reason: a defaulting lookup makes a typo resolve to something plausible, so a misconfigured
    /// subscription looks healthy. A caller that cannot proceed without a definition must say so.
    /// </summary>
    public static EventDefinition? For(string type) =>
        ByType.TryGetValue(type, out var found) ? found : null;
}
