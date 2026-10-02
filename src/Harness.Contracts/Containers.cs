using System.Text.Json.Serialization;

namespace Harness.Contracts;

/// <summary>
/// The message types the runtime itself publishes and reacts to. Constants rather than strings at
/// call sites, so a rename is a compile error rather than a container that silently never wakes.
/// </summary>
public static class MessageTypes
{
    /// <summary>
    /// An instruction addressed at ONE container. The only "do this" on the log; everything else
    /// here is "this happened".
    ///
    /// Addressing rides on the type rather than the payload — `agentContainer.instruction.Alpha/Manager` —
    /// so delivery is the subscription mechanism doing its ordinary job. The alternative, one shared
    /// type filtered by a payload field, would need every container to receive every instruction and
    /// discard most of them, and would put routing logic somewhere it could disagree with the
    /// subscription that was supposed to express it.
    /// </summary>
    public static string InstructionFor(ContainerId container) => $"agentContainer.instruction.{container}";

    /// <summary>The prefix all addressed instructions share, for a reader that wants any of them.</summary>
    public const string InstructionPrefix = "agentContainer.instruction.";
    public const string WorkflowPrefix = "workflow.";

    /// <summary>
    /// The namespace every agent-container event lives under - every constant in this class below
    /// <see cref="InstructionPrefix"/>. Exists so a caller that has to ask "is this type one of
    /// THESE" (the CLI's `Trigger` renderer, deciding whether to strip a namespace off a waking type
    /// before printing it) asks it against a constant rather than a hand-typed `"agentContainer."`.
    ///
    /// A hand-typed copy here is the exact defect this file exists to prevent, and a subtler one
    /// than a full type value going stale: the day this namespace is relabelled, a literal
    /// `"agentContainer."` StartsWith check does not throw and does not fail a test - it simply
    /// stops matching, and the caller's "strip the namespace" branch silently stops firing. Named for
    /// what the namespace IS rather than its current spelling, so a future rename changes only this
    /// one value and every call site follows for free.
    /// </summary>
    public const string AgentContainerPrefix = "agentContainer.";

    public const string Started = "agentContainer.started";
    public const string Completed = "agentContainer.completed";
    public const string Failed = "agentContainer.failed";

    /// <summary>Work refused because the queue was at its ceiling. Carries what was refused, so the
    /// sender can retry, wait or shed rather than discovering a silence.</summary>
    public const string Rejected = "agentContainer.rejected";

    /// <summary>
    /// What a container says about ITSELF, mid-run. Nothing subscribes to it and nothing ever
    /// should: a manager holding this type would be woken by every status line every worker
    /// writes, which is a runaway wearing the costume of observability.
    ///
    /// It is on the log rather than held in memory so it correlates, appears in the activity feed,
    /// and survives the process. It is EXCLUDED from the ledger projection (SqliteLedger) so it
    /// never reaches a prompt - the transcript/ledger split exists to keep volume out of context,
    /// and a chatty member would otherwise re-read and re-pay for its own status lines on every
    /// later invocation.
    /// </summary>
    public const string Progress = "agentContainer.progress";

    /// <summary>
    /// A container saying it has STOPPED without finishing, and why.
    ///
    /// Different from <see cref="Failed"/> beside it, and the difference is who is speaking:
    /// `failed` is the PLATFORM reporting that a run did not complete - a launch error, a timeout,
    /// a Host shutdown - while this is the AGENT reporting that its run completed fine and it is
    /// giving up on the work. Workflow #102 is why it exists: a manager that abandons a job and a
    /// manager that delivers one both publish <see cref="Completed"/> with exit code 0, so a board
    /// showing two idle green cards could not tell them apart and neither could the person reading
    /// it.
    ///
    /// Nothing subscribes to it, for the reason nothing subscribes to <see cref="Progress"/>: a
    /// manager holding this type would be woken by every worker that reports a blockage. It does
    /// not need to be - a blocked member still publishes <see cref="Completed"/> at the end of its
    /// run, which wakes its manager through the subscription it already has.
    ///
    /// UNLIKE Progress it is NOT excluded from the ledger projection, and that is chosen rather
    /// than overlooked. A manager that gave up should read that back on its next wake, or it will
    /// cheerfully retry what it just abandoned. Progress is excluded because a chatty member would
    /// re-read and re-pay for its own commentary on every later invocation; one row per abandoned
    /// run carries none of that cost.
    /// </summary>
    public const string Blocked = "agentContainer.blocked";
    public const string NeedsDecision = "agentContainer.needsDecision";

    /// <summary>
    /// A container saying ITS OWN PART IS DONE and nothing is owed - the hand-back.
    ///
    /// NOT A FLAVOUR OF <see cref="Blocked"/>, and everything here turns on that. `blocked` is the
    /// AGENT GIVING UP; it is in the ledger precisely so a manager reads back that a member
    /// abandoned something and does not retry it. A member that has FINISHED cannot declare
    /// `workflow-complete` - that is a manager's declaration - and publishing `blocked` instead
    /// would teach the manager the opposite of the truth, mark the team as in trouble, and re-bill
    /// the row into the manager's context on every later invocation. This type is the word for
    /// what the member actually needs to say.
    ///
    /// NOT <see cref="WorkflowCompleted"/> EITHER, and that is the other half. This hands work
    /// back; it does not close a workflow. Only a Manager declares one complete, asserted on
    /// IDENTITY rather than on a permit - a member that hands back is refused a declaration.
    ///
    /// SUBSCRIBED TO, UNLIKE `blocked` AND `progress`, which is the one behaviour that needs a new
    /// type rather than a field. A manager holds it in `TeamRegistry.ManagerSubscriptions`, so a
    /// hand-back WAKES the manager on the same correlation - which is what a member that reaches
    /// for `workflow-complete` actually wants. The delivery pump's two guards make that safe: a
    /// container never reacts to its own publications, so
    /// a Manager that hands back does not wake itself, and a container is woken only by its own
    /// team, so one team's hand-back cannot reach another's.
    ///
    /// IN THE LEDGER, for `blocked`'s reason read the other way up: a manager that has been handed
    /// work back must read that on its next wake, or it re-dispatches something already delivered.
    /// One row per finished card carries none of the volume `progress` was excluded for.
    ///
    /// SPELLED `agentContainer.handback` AND NOT `container.handback`, like every other constant in
    /// this class. Team derivation does not depend on it: `MessageTeam.Of` has no arm for this
    /// namespace and falls through to the message's SOURCE, splitting on the last `/`, the same
    /// path `blocked` and `progress` take. The reason is <see cref="AgentContainerPrefix"/>: it is
    /// what the CLI's `Trigger` renderers key on to strip a namespace before printing a waking
    /// type, so a type outside it would print inconsistently against every sibling.
    /// </summary>
    public const string Handback = "agentContainer.handback";
    public const string WorkflowCompleted = "workflow.completed";

    /// <summary>
    /// A PERSON ending a workflow, as opposed to a manager DECLARING what it delivered.
    ///
    /// Never `workflow.completed`. That type is a manager's declaration of a delivery, and a person
    /// clicking Close is not claiming one - most closures are of work that finished and simply was
    /// never declared. Publishing `completed` on their behalf would put a claim on the append-only
    /// log that nobody made, and `MessageText` feeds these rows into other agents' prompts, so the
    /// fiction would reach the next invocation's context.
    ///
    /// It closes a workflow exactly as a declaration does, and new work re-opens it exactly as it
    /// re-opens a declaration - see `WorkflowOpenSql`. Closing says STOP COUNTING THIS; new activity
    /// is precisely what should make it count again.
    /// </summary>
    public const string WorkflowClosed = "workflow.closed";

    /// <summary>
    /// ONE WORKFLOW stopped because it reached the per-workflow spend figure in force for its team.
    /// Published by the PUMP, whose source is a qualified container id.
    ///
    /// NOT `SetPausedAsync`, which pauses a TEAM. A team with three open workflows must not lose
    /// all three because one spent its budget, so this is keyed on the CORRELATION and nothing
    /// else reads it as a team fact.
    ///
    /// A ROW RATHER THAN A COLUMN, for the reason every other per-workflow fact here is one:
    /// `workflow.completed` and <see cref="WorkflowClosed"/> beside it are rows, the log is the
    /// record, and a second store would give "is this paused" two answers the moment they drifted.
    ///
    /// WRITTEN ONCE PER PAUSE EPISODE, never once per refused delivery - the pump checks the thread
    /// before appending. `agentContainer.rejected` stays as the per-message refusal receipt.
    ///
    /// NOTHING SUBSCRIBES TO THIS AND NOTHING SHOULD. A Manager woken by its own workflow's pause
    /// is a wake the budget has just refused to pay for.
    /// </summary>
    public const string WorkflowPaused = "workflow.paused";

    /// <summary>
    /// A PERSON releasing one paused workflow. Published by the `.HumansOnly()` resume route, so
    /// its source is a bare principal id with no team in it - which is why
    /// <see cref="WorkflowClosed"/>'s payload-reading arm in `MessageTeam.Of` covers this type too.
    ///
    /// A TYPE OF ITS OWN RATHER THAN "ANY LATER INSTRUCTION CLEARS THE PAUSE". `WorkflowOpenSql`
    /// already treats a later instruction as re-opening a CLOSED workflow, but an instruction can
    /// be written by an AGENT through `tell` - so the looser rule would let a runaway release its
    /// own pause by telling a teammate, which is precisely what the `.HumansOnly()` marker on the
    /// nudge route exists to prevent.
    ///
    /// IT CLEARS NO COUNTER. The spend window moves because the route also writes an addressed
    /// instruction whose causation IS the correlation - the same act a person pressing Nudge
    /// performs - and that is the only mechanism. There is deliberately nothing here to reset.
    /// </summary>
    public const string WorkflowResumed = "workflow.resumed";

    /// <summary>
    /// A scheduled wake that was deliberately NOT queued because the target member was busy and the
    /// schedule was idle-only.
    ///
    /// Nothing subscribes to this type and nothing ever should. Like <see cref="Progress"/>, it is
    /// a card/feed signal for people rather than an event an agent should react to.
    /// </summary>
    public const string ScheduleSkipped = "agentContainer.scheduleSkipped";

    /// <summary>
    /// What a PERSON did to a card on the board, and the only types here published by a human
    /// rather than by the platform or an agent.
    ///
    /// Constants because a Manager SUBSCRIBES to them - see `TeamRegistry.ManagerSubscriptions` -
    /// and a subscription is matched under SQLite's BINARY collation, so a near-miss spelling is a
    /// container that never wakes and nothing that says so.
    ///
    /// Their team comes from the PAYLOAD - see <see cref="MessageTeam"/> - because the actor is a
    /// user id and carries no team at all.
    /// </summary>
    public const string KanbanCardPrefix = "kanban.card.";

    public const string KanbanCardMoved = "kanban.card.moved";
    public const string KanbanCardEdited = "kanban.card.edited";
    public const string KanbanCardCommented = "kanban.card.commented";

    /// <summary>
    /// A card a Manager PLANNED, before anybody has been told to do it.
    ///
    /// <para>
    /// Otherwise a card is creatable only by an INSTRUCTION, and that rule protects the board from
    /// filling with blank-headed runs nobody asked for. But a Manager must be able to write six Todo
    /// cards before telling anybody, which is exactly what cutting a spec into pieces is. This type
    /// is the second way a card is born, and the only one; an instruction makes one and nothing
    /// else does.
    /// </para>
    ///
    /// <para>
    /// IT IS PUBLISHED BY A ROUTE AND NOT BY A CONTAINER, which is what keeps
    /// <see cref="MessageTeam"/>'s payload-reading arm safe: the route writes `team` from the
    /// `{team}` route value <c>TeamGate</c> already checked, so the value traces back to something
    /// the server built rather than to anything a caller supplied.
    /// </para>
    /// </summary>
    public const string KanbanCardPlanned = "kanban.card.planned";

    /// <summary>
    /// A member was deleted from its team.
    ///
    /// <para>
    /// IT EXISTS SO THAT DELETION PUBLISHES SOMETHING. Cards are a projection of this log, so
    /// without a row the projector cannot see a deletion and no amount of client work would
    /// show it - and a card still naming a member who is gone is a board lying about who is on the
    /// work. The answer must NOT be to join the projection to `team_members`: that is the coupling
    /// `ILedger` already forbids, and it would make a board read depend on a store the projector has
    /// no business knowing about.
    /// </para>
    ///
    /// <para>
    /// ONE ROW, NOT ONE PER CARD. The projector unassigns every card currently naming that member; a
    /// row per card would put the projection's own output into its input.
    /// </para>
    ///
    /// <para>
    /// ITS TEAM COMES FROM <c>Source</c> - the member's qualified `Team/Name` - which is the existing
    /// rule for container events, so no payload field names a team and <see cref="MessageTeam"/>
    /// needs no new arm. Nothing subscribes to it and nothing should, exactly as with
    /// <see cref="Progress"/>.
    /// </para>
    /// </summary>
    public const string ContainerRemoved = "agentContainer.removed";

    /// <summary>
    /// A backlog item was dispatched to a team. ITS SEQ IS THE CORRELATION ROOT of the workflow that
    /// follows, so a dispatch IS a workflow and everything downstream - cards, spend, the budget
    /// bound, the completion refusal - keys on it unchanged. Nothing new is invented for correlation.
    /// </summary>
    public const string BacklogItemDispatched = "backlog.item.dispatched";

    /// <summary>
    /// THE PLATFORM PUBLISHED THIS TEAM'S WORK TO ORIGIN, and it did so BEFORE the acceptance row
    /// that this run's work is now claimed under.
    ///
    /// <para>
    /// Work that exists only under a team root is destroyed when the team is deleted.
    /// Pushing cannot be left to an AGENT'S judgement at the end of a run - precisely the moment a
    /// spend limit or a kill interrupts - so the Host does it. This row is the receipt for that, and it is the only
    /// evidence anybody has that a card marked done has anything behind it.
    /// </para>
    ///
    /// <para>
    /// IT IS NOT PUBLISHED WHEN NOTHING WAS PUSHED. A team with no repository, and a team whose
    /// every branch is already on origin, complete in silence - a durability receipt that fires on
    /// teams with no code is a row that teaches its readers to ignore the type.
    /// </para>
    ///
    /// <para>
    /// A PUSH IS NOT A MERGE, and `main` is never its destination. This publishes member branches so
    /// the work exists off the machine; the Git dialog's ladder stays the integration path.
    /// </para>
    ///
    /// <para>
    /// Nothing subscribes to it and nothing should, for the reason nothing subscribes to
    /// <see cref="Progress"/>: a manager woken by every member's durability receipt is a runaway
    /// wearing the costume of observability.
    /// </para>
    /// </summary>
    public const string RepoPushed = "repo.pushed";

    /// <summary>
    /// THE NAMED, RETRYABLE CONDITION FOR WORK THE PLATFORM COULD NOT PUBLISH - an unreachable
    /// origin, a refused credential, a rejected ref, a push that timed out.
    ///
    /// <para>
    /// IT IS ITS OWN TYPE PRECISELY SO IT IS NOT FOLDED INTO THE RUN'S SUCCESS OR FAILURE. An
    /// unreachable origin is TRANSPORT, not an agent fault: publishing it as
    /// <see cref="Failed"/> would blame a member for a network it cannot reach, and swallowing it
    /// into the workflow's own success would lose work silently. The acceptance still
    /// lands - it is not refused, because a member out of budget cannot satisfy a refusal - and this row lands FIRST, so a reader finds the failure attached
    /// to the acceptance rather than having to infer it from an absence.
    /// </para>
    ///
    /// <para>
    /// RETRYABLE BECAUSE NOTHING MOVED. A push that fails changes nothing on origin, so repeating it
    /// is safe; the Git dialog's push button is the manual form of exactly this operation.
    /// </para>
    ///
    /// <para>
    /// ITS <see cref="PayloadFields.Reason"/> IS GIT'S OWN WORDS, THROUGH
    /// <c>GitOutputRedaction</c>. git quotes the remote URL into its failures, and a credential in
    /// that URL's userinfo would otherwise reach an append-only log that is read back into other
    /// agents' prompts. Redaction belongs at the WRITE; see that type.
    /// </para>
    /// </summary>
    public const string RepoPushFailed = "repo.pushFailed";

    /// <summary>
    /// A CARD'S WORKTREE THE PLATFORM WOULD HAVE REMOVED AND DID NOT. Written when a settled
    /// card's tree is tidied - the workflow was declared complete, the member was deleted, or a
    /// person pressed "clean up worktrees" - and <c>git worktree remove</c> refused it (uncommitted
    /// edits), or its commits are not on origin. The platform NEVER forces a removal, so the tree
    /// stays on disk and this row names it and why. Its Source is the member the tree belongs to.
    /// </summary>
    public const string RepoWorktreeLeft = "repo.worktreeLeft";

    /// <summary>
    /// A MEMBER'S BRANCH A RESET WOULD HAVE DELETED AND DID NOT. Written when a person resets a
    /// team's repositories and one of a ticked member's local branches holds commits on no remote,
    /// or is still checked out in a tree that was kept. Branches are never force-deleted, so it
    /// stays and this row names it and why. Its Source is the member the branch belongs to.
    /// </summary>
    public const string RepoBranchKept = "repo.branchKept";

    /// <summary>
    /// A CONTRIBUTOR-MODE FORK'S DEFAULT BRANCH WAS FAST-FORWARDED TO UPSTREAM'S, by Bring
    /// current in the Git dialog: <c>git push origin upstream/&lt;default&gt;:refs/heads/&lt;default&gt;</c>,
    /// never forced. The one push of a default branch the platform makes. Appended AFTER the push
    /// succeeded, never before - the ordering <c>repo.pushed</c> keeps. The team is in the payload
    /// because a person pressed the button, and a person's Source carries none.
    /// </summary>
    public const string RepoForkSynced = "repo.forkSynced";

    /// <summary>
    /// A MANAGER'S RUN LEFT THE CLONE'S DEFAULT BRANCH WHERE ORIGIN'S IS NOT: the stored default
    /// branch in <c>repos/&lt;Repo&gt;/main</c> holds commits <c>origin/&lt;default&gt;</c> lacks.
    /// The Manager's work belongs on <c>team/{id}</c>; the Git dialog lands it from there. Checked
    /// at the end of every Manager run and appended before its terminal row, so it lands on the
    /// Manager's card and in the feed. The platform reports it and resets nothing. Source is the
    /// Manager.
    /// </summary>
    public const string RepoDefaultBranchMoved = "repo.defaultBranchMoved";

    /// <summary>
    /// A PERSON OPENED A PULL REQUEST UPSTREAM from the Git dialog's Open pull request, for
    /// team/{id} on a contributor-mode repository's fork. Appended AFTER GitHub opened it, never
    /// before. The platform never opens one by itself, and <c>workflow_complete</c> does not.
    /// </summary>
    public const string RepoPullRequestOpened = "repo.pullRequestOpened";

    /// <summary>
    /// Files changed in a watched folder. Published by the PLATFORM only: by a folder
    /// trigger's poll (source `trigger:&lt;id&gt;`) or by the Documents dialog's upload and delete
    /// routes (source the person). Carries paths and counts, never contents.
    /// </summary>
    public const string FileChanged = "file.changed";

    /// <summary>
    /// A PERSON CLICKED SOMETHING ON A TEAM'S SITE: the page called <c>site.action(name, payload)</c>.
    /// Published by the PLATFORM only, by the site action route, with source
    /// <c>site:&lt;team&gt;/&lt;site&gt;</c> and no causation, so each one roots a workflow as a
    /// trigger's fire does. Carries <c>team</c>, because the source is not a container id.
    /// </summary>
    public const string SiteAction = "site.action";

    /// <summary>
    /// THE PLATFORM CHECKED A SOLUTION PACKAGE THE TEAM WROTE. When a workflow is declared complete,
    /// every folder holding <c>solution.json</c> directly in the team's documents folder and written
    /// during that workflow is checked, and one of these is appended per package, inside the
    /// workflow, with the declarer as its Source. It passed: it carries the install link. It failed:
    /// it names each problem by file and field. A notice for people, so out of the ledger.
    /// </summary>
    public const string SolutionChecked = "solution.checked";

    /// <summary>
    /// A MEMBER'S RUN MET A TOOL THE PLATFORM DID NOT GIVE IT, OR COULD NOT BE CHECKED. After every
    /// member run that ended, the platform reads the agent's own transcript and names each tool it
    /// was offered or called that is outside <c>harness</c> and its preset's allowed list: an account
    /// connector, a home MCP server, a plugin. <c>foreign</c>: tools named, called apart from offered.
    /// <c>notMeasured</c>: the format cannot list what was offered, and nothing foreign was seen.
    /// <c>notVerified</c>: the offer was read whole and held no foreign server, but the preset declares
    /// no allowed tools. Neither is ever reported clean. A clean run writes nothing. Source
    /// is the member and causation the run's terminal row, so it lands on the member's card. The
    /// Concierge is not a member and is never checked.
    /// </summary>
    public const string AgentForeignTools = "agent.foreignTools";

    /// <summary>
    /// The exact skip reason published in <see cref="ScheduleSkipped"/> rows for idle-only schedules.
    /// Kept as one constant so every producer and assertion says the same sentence.
    /// </summary>
    public const string ScheduleSkippedBusyReason =
        "Skipped because this schedule is idle-only and the member is busy.";

    /// <summary>
    /// The exact skip reason published in <see cref="ScheduleSkipped"/> rows for a paused team.
    /// Kept beside <see cref="ScheduleSkippedBusyReason"/> so every producer and assertion says the
    /// same sentence.
    /// </summary>
    public const string ScheduleSkippedPausedReason =
        "Skipped because this team is paused.";

    /// <summary>
    /// The skip reason published in <see cref="ScheduleSkipped"/> rows, and written on the
    /// `tenant_events` row, for a trigger whose runs have spent its daily token cap - once a day.
    /// An event or folder trigger's reason is exactly this; a schedule's adds "; resumes at
    /// &lt;time&gt;", the first occurrence of the next day it sleeps until.
    /// </summary>
    public const string ScheduleSkippedCapReason = "daily token cap reached";
}

/// <summary>
/// What a container is doing. At-ceiling is deliberately NOT here: a ceiling rejects rather than
/// holds, so "full" is a property of the queue at an instant, not a state the container sits in.
/// The UI derives it from <see cref="ContainerSnapshot.QueueDepth"/> against the ceiling.
/// </summary>
public enum ContainerState
{
    Idle,
    Running,
}

/// <summary>What the UI renders and the API returns. A card, not a terminal.</summary>
/// <param name="SinceSeq">
/// The log's head when this container was created. Everything at or below it belongs to whatever
/// came before and is not this container's business — the UI filters its activity feed by it, for
/// the same reason the ledger does.
/// </param>
/// <param name="Name">
/// What a person calls this container, which is NOT its <paramref name="Id"/>. The id is an
/// identifier under the same allowlist a team's is - a directory segment and half of the dotted
/// event type - so it cannot hold a space. The name can hold anything. Equal to the id for a
/// container nobody has given a separate one, which is every container today: nothing renames one.
/// Render this; address with Id.
/// </param>
public sealed record ContainerSnapshot(
    string Team,
    string Id,
    string Name,
    string Agent,
    ContainerState State,
    int QueueDepth,
    int Ceiling,
    IReadOnlyCollection<string> Subscribes,
    long? CurrentCorrelation,
    long SinceSeq = 0,

    /// <summary>
    /// The Agent this container names, when the catalog has no such Agent - null when it resolves.
    ///
    /// A nullable STRING rather than a new ContainerState: the state enum describes the run loop,
    /// this describes configuration, and the enum crosses HTTP as a name while the SPA compares
    /// `snapshot.state === 'Running'`, so extending it means auditing both wires (HubGroupTests
    /// pins that). It is reachable because the catalog is a file that is not restored with the
    /// database - see the Agent catalog design, decision 2.
    /// </summary>
    string? MissingAgent = null,



    /// <summary>
    /// Why this container stopped without finishing, or null when it did not.
    ///
    /// A nullable STRING and never a <see cref="ContainerState"/>, for the reason the three marks
    /// above are not either: the enum describes the run LOOP, this describes the OUTCOME of a run,
    /// and the enum crosses HTTP as a name while the SPA compares it as a string - so extending it
    /// means auditing both wires. This field crosses both too, and is pinned on the raw JsonElement
    /// in HubGroupTests beside `state` for exactly that reason.
    ///
    /// Set when the container publishes `container.blocked`; cleared where `container.started` is
    /// published. Outliving the run is the entire point - a container that gave up is idle, and
    /// idle is what a container that finished perfectly also is. Dying at the next wake is what
    /// stops a container that has been given new work from wearing a mark about the last job.
    ///
    /// THIS IS NOT the two-stores mistake `MemberRuntime.Republish`'s comment warns about. That
    /// comment is right about a STREAM: a status line every few seconds, copied into a field beside
    /// the log, is two stores of one fact and the shape that produced the per-container command map
    /// this codebase deleted. This is one terminal fact per run, and it is the container's own
    /// run-loop state - exactly as State, QueueDepth and CurrentCorrelation are, all three of which
    /// are equally derivable from the log and all three of which are here. A snapshot IS the
    /// projection of a container's state; the log is the history.
    ///
    /// Deriving it client-side from the feed would be wrong: a card holds a
    /// SLICE of messages, so a member that narrated its run would scroll its own blocked row out of
    /// the window and the mark would vanish. A mark that is usually right is worse than no mark.
    /// </summary>
    string? Blocked = null,

    /// <summary>
    /// Why the PLATFORM did not complete this container's last run, or null when it did.
    ///
    /// Shaped EXACTLY like <see cref="Blocked"/> beside it, and that field's documentation
    /// pre-argues every objection to this one - read it before changing either. A nullable STRING
    /// and never a <see cref="ContainerState"/>: the enum describes the run LOOP, this describes
    /// the OUTCOME of a run, and the enum crosses HTTP as a name while the SPA compares it as a
    /// string, so extending it means auditing both wires. This one crosses both too and is pinned
    /// on the raw JsonElement in HubGroupTests beside `state` and `blocked`.
    ///
    /// DIFFERENT FROM <see cref="Blocked"/> IN WHO IS SPEAKING, which is the same distinction
    /// `container.failed` and `container.blocked` already carry: this is the platform reporting
    /// that a run did not complete - a launch error, a non-zero exit, a Host restart mid-flight -
    /// while `Blocked` is the AGENT reporting that its run completed fine and it is giving up.
    ///
    /// Set where `container.failed` is published, which is BOTH publishers: the arm of
    /// <c>MemberRuntime.RunOneAsync</c> that chooses `Failed` over `Completed`, and
    /// <c>ContainerHost.ResumePendingAsync</c> for a run the Host was restarted out from under.
    /// Cleared at the NEXT WAKE, on the line beside where `Blocked` and `NeedsDecision` are
    /// cleared - deliberately adjacent so the three cannot drift apart.
    ///
    /// OUTLIVING THE RUN IS THE ENTIRE POINT. A container whose run failed is idle, and idle is
    /// what a container that finished perfectly also is: without this field, a team whose every
    /// member's run failed within seconds would render as healthy idle cards with nothing to do.
    ///
    /// WHEN THIS AND <see cref="Blocked"/> ARE BOTH SET, THE CARD SAYS FAILED. A run can leave
    /// both - an agent gives up and publishes `blocked`, then its process dies and the platform
    /// publishes `failed` - and both are cleared at the same next wake, so the card must choose.
    /// It shows the failure, matching the ranking `teamStatus` gives the tab chip: a platform
    /// failure outranks an agent's own decision to stop, because one is recoverable by the team
    /// and the other is not. The full precedence is
    /// <c>Failed › Blocked › <see cref="NeedsDecision"/></c>, and the tile and the card must order
    /// these identically or a chip and the card beneath it disagree about one member.
    /// </summary>
    string? Failed = null,

    /// <summary>
    /// What this container needs answered before it can continue, or null when it does not.
    ///
    /// The same shape and lifecycle as <see cref="Blocked"/> beside it: a nullable string carrying
    /// what a person needs to decide, set when the member publishes `container.needs-decision`,
    /// and cleared on the next wake where `container.started` is published.
    ///
    /// Still not a <see cref="ContainerState"/> for the same reason: the enum describes the run
    /// loop, and this describes the outcome of the previous run.
    /// </summary>
    string? NeedsDecision = null,


    /// <summary>
    /// This team's ROOT FOLDER, when the Host could not reach it at startup - null when it is
    /// there.
    ///
    /// A team can be PLACED, on another volume or a network share, and a machine that reboots with
    /// that drive unplugged has a team whose files are simply not present. The team still exists,
    /// is still visible, and its members must NOT run: <c>ProcessAgentRunner</c> silently falls
    /// back to the Host's own current directory when a workspace is missing - which for anyone
    /// running from a clone is the source tree - so an unmarked member would write its
    /// <c>AGENTS.md</c> and its work product into somebody's repository.
    ///
    /// A nullable STRING and never a <see cref="ContainerState"/>, exactly as the three marks above
    /// are not: the enum describes the run loop, this describes an absent substrate, and the enum
    /// crosses HTTP as a name while the SPA compares it as a string. It carries the PATH, which is
    /// the thing a reader has to go and reconnect.
    ///
    /// It is NOT cleared by <c>Reprompt</c> and there is nothing on any settings screen that fixes
    /// it: the recovery is making the folder reachable and restarting, because restoration is what
    /// reads it. Saying so is the whole of the message the runner refuses with.
    /// </summary>
    string? UnreachableRoot = null,

    /// <summary>
    /// The tag this member was hired under, or null when none was named.
    /// </summary>
    string? HiredFor = null,

    /// <summary>
    /// Any chosen Agents that did not resolve on this machine's PATH at the instant this snapshot
    /// was read. Omitted when the route did not probe, or when every chosen Agent resolved.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<UnresolvedAgent>? UnresolvedAgents = null,

    /// <summary>
    /// WHAT KIND of failure <see cref="Failed"/> was - one of <see cref="FailureClasses"/> - or
    /// null when this member's last run did not fail, or failed without a class.
    ///
    /// THE CARD'S HALF OF `payload.failureClass`, set on the same call that sets
    /// <see cref="Failed"/> and cleared on the same line at the next wake. Read
    /// <see cref="Failed"/>'s own documentation before changing either: this is a second value on
    /// one mark, not a second mark, and the pair has to live and die together or a card wears a
    /// class from one run beside a reason from another.
    ///
    /// A nullable STRING, like every mark beside it, and for the same reason: the enum crosses HTTP
    /// as a name while the SPA compares it as a string.
    /// </summary>
    string? FailureClass = null,

    /// <summary>
    /// WHEN THE PLATFORM WILL RESUME THIS MEMBER'S WORKFLOW BY ITSELF, as an ISO-8601 instant - or
    /// null when it will not, which is the ordinary case and every class but quota and rate.
    ///
    /// VISIBLE IS PART OF THE DESIGN RATHER THAN POLISH: a silent
    /// automatic retry is how a quota failure becomes a spend failure, and this is the field a
    /// person reads to see one coming.
    ///
    /// WRITTEN BY THE SWEEP THAT WOULD ACTUALLY FIRE IT, never by the failing run - which is the
    /// whole reason it is not simply a copy of `payload.retryAfter`. The run knows what the
    /// provider said; only the sweep knows whether a resume is still BOUNDED IN, still wanted, and
    /// still this member's last word. A card that promised a resume the bound had already spent
    /// would be worse than a card that promised nothing.
    /// </summary>
    string? ResumeAt = null,

    /// <summary>
    /// WHAT THIS MEMBER LAST HANDED BACK - the words on its most recent
    /// <c>agentContainer.handback</c> - or null when it has never handed anything back.
    ///
    /// DELIBERATELY NOT SHAPED LIKE <see cref="Blocked"/>, <see cref="Failed"/> AND
    /// <see cref="NeedsDecision"/>, and this is the one place the hand-back is most likely to be got wrong.
    /// Those three are MARKS OF TROUBLE with a self-expiring lifecycle: each is cleared at the NEXT
    /// WAKE, on adjacent lines in <c>MemberRuntime.RunOneAsync</c>, because - in that method's own
    /// words - "what makes a mark a lie is not the run ending, it is this container being given
    /// something else to do". A warning that outlived the job it warned about would show a team in
    /// trouble that is not.
    ///
    /// A SUCCESS HAS NO SUCH PROBLEM AND MUST NOT INHERIT THAT LIFECYCLE. A delivery does not go
    /// stale: this member really did hand that back, and it stays true after it is given something
    /// else to do. Cleared on the next wake, it would vanish the instant anything woke this member
    /// - a schedule, another member's `tell`, the manager's own reply to the hand-back itself -
    /// and the member's last delivery would be readable nowhere on the board. Worse, it would be
    /// gone MOST RELIABLY in the case it exists for, because a hand-back's whole purpose is to wake
    /// somebody who then gives this member more work.
    ///
    /// SO IT IS A LAST-X FIELD, not a mark: written by <c>MemberRuntime.MarkHandedBack</c>,
    /// replaced only by the next hand-back, and never cleared. That is why every reader words it as
    /// `last handback` rather than as a present-tense condition - it is accurate beside a member
    /// that is Running something new, where "handed back" alone would not be.
    ///
    /// IT RANKS BELOW ALL THREE TROUBLE MARKS wherever one row has to choose - see
    /// `StatusCommand`'s note chain, which appends this only when `failed`, `blocked` and
    /// `needsDecision` are all absent. A member in trouble is not described by an older success.
    ///
    /// NOT RESTORED ACROSS A HOST RESTART, unlike the three marks, which <c>LiveMarksForAsync</c>
    /// re-derives from the log. That reader exists for marks with the started-since predicate and a
    /// `floor_seq`, neither of which this field has; the hand-back itself is durable on the
    /// append-only log and on the card, which is where a reader goes after a restart. Stated rather
    /// than left to be discovered.
    /// </summary>
    string? HandedBack = null,

    /// <summary>
    /// Whether this container has work taken off its queue and is WAITING FOR A SLOT - its claim on
    /// the instance-wide WIP limit was refused, or its team is paused, and the run has not started.
    /// True only for as long as the claim is waiting; cleared the moment the run starts or the
    /// container stops. Lets the member card, the kanban card and the Teams table say
    /// "waiting for a slot" instead of showing an idle container with nothing to say.
    ///
    /// A BOOL beside <see cref="State"/> rather than a new <see cref="ContainerState"/>, for the
    /// reason every mark above is: the enum crosses HTTP as a name the SPA compares as a string.
    /// </summary>
    bool Held = false,

    /// <summary>
    /// Whether a person can watch this member: its agent's preset has a live view this Host
    /// can read. True whether or not it is running, so the card shows the eye on an idle member to
    /// open its earlier runs; false shows no eye at all, rather than one that opens onto "no live
    /// view". Decided from the member's agent, where the snapshot is made.
    /// </summary>
    bool Watchable = false,

    /// <summary>
    /// WHAT KIND OF MEMBER THIS IS: <c>agent</c> for a coding-agent CLI, <c>plugin</c> for an
    /// installed plugin executable - read from <see cref="Agent"/> through <c>MemberRef</c>, so it
    /// cannot disagree with what the member runs.
    ///
    /// ADDITIVE, AND THE ONLY FIELD THE MEMBER REFACTOR ADDED: every field above keeps its name
    /// and its value for an agent member, which the step-0 golden in MemberGoldenTests pins. A
    /// STRING rather than an enum for the reason every mark here is one: the SPA compares it as a
    /// string.
    /// </summary>
    string Kind = MemberRef.AgentKind);

/// <summary>
/// A chosen Agent that did not resolve on this machine's PATH.
/// </summary>
public sealed record UnresolvedAgent(
    string Agent,
    string Command,
    string Message);

/// <summary>
/// One invocation of the contained agent. The agent receives a prompt and a place to work and
/// nothing else — it does not know Harness exists, which is the line that keeps platform
/// awareness inside the container.
/// </summary>
/// <param name="SystemPrompt">
/// Kept SEPARATE from the prompt rather than glued in front of it. Agents take a system prompt
/// through their own mechanism - a flag, a file - and concatenating the two into one blob means
/// whatever the agent echoes, logs or is asked to summarise contains its own instructions. How to
/// deliver it is the runner's business, which is the only thing that knows what it is launching.
/// </param>
/// <param name="Context">
/// What happened before, derived from this container's ledger - the artifact that stops an agent
/// being amnesiac between wakes.
///
/// Separate from the prompt for the same reason the system prompt is, and delivered by the runner
/// for the same reason. Empty for a container with no history, which is every container's first
/// invocation.
/// </param>
public sealed record AgentInvocation(
    ContainerId Container,
    string SystemPrompt,
    string Prompt,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string Context = "",

    /// <summary>
    /// The Agent this container names - not necessarily one the catalog can resolve. Carried so a
    /// runner that fails to find a command can name what it was LOOKING for, not only the container
    /// that asked: "no agent configured for Alpha/Worker" tells a reader something is broken and not
    /// what to put back.
    /// </summary>
    string Agent = "",


    /// <summary>
    /// This team's root folder, when the Host could not reach it at startup - null when it is
    /// there.
    ///
    /// Carried this far for the same reason <see cref="MissingAgent"/> is: the refusal has to
    /// name the recovery that actually works, and here it is neither the catalog nor a settings
    /// screen but the folder itself. Without it the runner would see only a
    /// <see cref="WorkingDirectory"/> that does not exist, which it treats as "run somewhere else"
    /// - and somewhere else is the Host's own current directory.
    /// </summary>
    string? UnreachableRoot = null,

    /// <summary>
    /// The credential this run starts with, resolved at run start (<c>RunCredentials.ResolveAsync</c>).
    /// Null when the caller resolved none: the runner then asks the same resolver itself, so an
    /// invocation built without one never falls back to the shared home for an issued preset.
    /// </summary>
    RunCredential? Credential = null);

/// <param name="ReachedThePlatform">
/// Whether this run's agent authenticated ANY request with the member's own credential.
///
/// FALSE MEANS THE RUN DID NOT BEGIN. Every seeded prompt opens with
/// `harness skills get &lt;role&gt;`, and the CLI is the only way a member reaches the system at
/// all - so a run that served no request made no tool call that could have mattered.
///
/// It exists so a run that did nothing is not reported as a success: an agent whose shell tools are
/// all denied by a fault in the agent CLI never loads its role skill, writes nothing, and exits 0 -
/// and without this the card goes green and the Manager pays for correction after correction
/// before the absence of commits gives it away.
///
/// NULL IS "NOT MEASURED" AND IS NOT A FAILURE. A runner that does not probe reports null, and so
/// does every test that does not set it; reading null as "did nothing" would fail every one
/// of them. The check below is `!= false` rather than `== true` for exactly that reason, and the
/// asymmetry is the point rather than a slip.
/// </param>
public sealed record AgentResult(
    int ExitCode,
    string Output,
    string? LaunchError = null,
    InvocationUsage? Usage = null,
    int? ProcessId = null,
    bool? ReachedThePlatform = null,

    /// <summary>
    /// WHAT KIND of failure this was - one of <see cref="FailureClasses"/> - or null when this
    /// runner did not classify.
    ///
    /// NULL IS "NOT CLASSIFIED", NOT "NOT A FAILURE", and the two are different in the way that
    /// matters: a runner that does not classify - and every test that does not set it -
    /// reports null, and the container turns that into <see cref="FailureClasses.Unknown"/> on the
    /// payload. The asymmetry is deliberate and is <see cref="ReachedThePlatform"/>'s, one field
    /// along: absent means nobody measured, never that the measurement came back clean.
    ///
    /// Filled in by `EnvelopeFailure`, which tells a 429 from a crash.
    /// </summary>
    string? FailureClass = null,

    /// <summary>
    /// WHEN THE PROVIDER SAID TO COME BACK, or null whenever it did not say - which is most
    /// failures, and every class but quota and rate.
    ///
    /// An INSTANT rather than the provider's sentence. "your session limit resets 12am
    /// (America/New_York)" is a sentence nothing can wait until; the words stay in <see cref="Output"/>, where a person still reads them.
    /// </summary>
    DateTimeOffset? RetryAfter = null,

    /// <summary>
    /// The session transcript the agent CLI wrote for this run, or null when its preset has
    /// no live view or the file was not found. Recorded on the run's terminal row for a person to
    /// read later; nothing about the run itself depends on it.
    /// </summary>
    AgentTranscript? AgentTranscript = null)
{
    /// <summary>
    /// THREE TERMS: a run can fail by doing nothing as well as by exiting non-zero or failing to
    /// launch.
    /// </summary>
    public bool Succeeded => LaunchError is null && ExitCode == 0 && ReachedThePlatform != false;

    /// <summary>
    /// What a card and a manager are told when a run never reached the platform.
    ///
    /// IT NAMES THE VERB, for the reason every refusal here names its fix: an agent
    /// told only that something failed retries the same thing. The first question its manager will
    /// have is what the agent was supposed to have done.
    ///
    /// A CONSTANT rather than a literal at the one call site, because a test asserting on this
    /// sentence and the code producing it drifting apart is how a reason stops meaning anything.
    /// </summary>
    public const string DidNothing =
        "This run made no call to `harness`, so the agent did not reach the platform and probably "
        + "did not load its role skill. Check the member's branch for what was run.";
}

/// <summary>A run's own session transcript: the absolute path the agent CLI wrote, and the
/// live view format it is read in.</summary>
public sealed record AgentTranscript(string Path, string Format);

/// <summary>
/// Runs the contained agent. The seam that lets the container be tested without spawning a process,
/// and later lets one container hold something that is not a process at all.
/// </summary>
public interface IAgentRunner
{
    Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default);
}

/// <summary>Everything that varies between containers. All data - which is why a container is
/// configuration rather than code, and why no plugin loader is needed to add one.</summary>
/// <param name="Limits">How much of an artifact may travel for this container. Null takes the
/// global default, exactly as <see cref="Ceiling"/> does.</param>
/// <param name="Label">
/// What a person calls this container. Null means "the same as the name", which is the right
/// default rather than a silent gap: unlike the artifact seams above, where null means a container
/// that quietly does not remember, a container with no separate label is fully described by its
/// name and nothing is lost. <see cref="ContainerSnapshot.Name"/> resolves it.
/// </param>
/// <param name="Permits">
/// What this container's agent may cause through the CLI. Null and empty both mean NONE, which is
/// a worker: it is minted no credential and given no HARNESS_* environment at all, because a
/// worker agent should not know Harness exists and a variable it can read is knowledge.
///
/// Data rather than a role enum, deliberately. There is no role field on a container and a manager
/// is identified by what it subscribes to, not by a flag - keeping permits data is what stops a
/// second, disagreeing notion of "what kind of container is this" growing here.
/// </param>
public sealed record ContainerDefinition(
    ContainerId Id,
    string Agent,
    string SystemPrompt,
    string WorkingDirectory,
    IReadOnlyCollection<string> Subscribes,
    IReadOnlyDictionary<string, string> Environment,
    int? Ceiling = null,
    ArtifactLimits? Limits = null,
    string? Label = null,
    IReadOnlySet<string>? Permits = null,

    /// <summary>
    /// The Agent this container names, when the catalog has no such Agent at RESTORE time - null
    /// otherwise. See <see cref="ContainerSnapshot.MissingAgent"/>, which this feeds.
    /// </summary>
    string? MissingAgent = null,




    /// <summary>This team's root folder, when restoration could not reach it. See
    /// <see cref="ContainerSnapshot.UnreachableRoot"/>, which this feeds, and
    /// <see cref="AgentInvocation.UnreachableRoot"/>, which carries it as far as the runner so a
    /// member of a team whose volume is unplugged refuses instead of running in whatever directory
    /// the Host happens to have been started from.</summary>
    string? UnreachableRoot = null,

    /// <summary>The tag this member was hired under, if any.</summary>
    string? HiredFor = null);
