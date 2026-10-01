using System.ComponentModel;

namespace Harness.Contracts;

/// <summary>
/// How long a team's workflow has been going, and how much run time went into it.
///
/// A PROJECTION over <c>messages</c>, sibling to <see cref="TeamTokenTotals"/> and built for the
/// same reason: a figure summed in the browser over the board's twenty-message feed would SHRINK
/// as the team got busier. Nothing in this record is a second store of anything - the log is the
/// record, this is a read of it.
///
/// IT CROSSES HTTP ONLY. It is fetched, never pushed: <see cref="ContainerSnapshot"/> rides every
/// SignalR message to every browser holding the team and already carries
/// <see cref="ContainerSnapshot.CurrentCorrelation"/>, which is all the push side needs. Adding
/// this to that record would put a team-level query on a per-container frame.
///
/// TWO DURATIONS, NAMED DIFFERENTLY, NEVER ADDED. <em>Elapsed</em> is wall clock and is not on this
/// record at all - the client subtracts <see cref="StartedAt"/> from now and ticks it, which is
/// what makes a tile count up with no further traffic. <see cref="ExecutionSeconds"/> is per-member
/// run time SUMMED, and it legitimately EXCEEDS elapsed because members run concurrently. Nothing
/// anywhere sums them, averages them or derives a percentage from them: a ratio of the two is a
/// concurrency figure nobody asked for and reads as progress.
/// </summary>
/// <param name="Available">
/// Whether this team has a workflow to report at all. False for a team that has never run - and
/// the caller must render an ABSENCE (an em dash) rather than <c>0m</c>. Zero is a measured
/// duration and this is an unmeasured one; the rule that an unknown delta must not render as a
/// zero delta is the same rule <see cref="TeamTokenTotals.Available"/> follows.
/// </param>
/// <param name="Correlation">
/// Which workflow this describes - the seq of the message that began it. The CURRENT one while the
/// team is running and the LAST one while it is idle, which is the same thing here: it is the
/// newest correlation any of this team's own rows carry above the floor.
/// </param>
/// <param name="State">
/// What the LOG says this workflow is, or null when there is none.
///
/// A STRING and never an enum, the rule <see cref="ContainerSnapshot.MissingAgent"/>,
/// <see cref="ContainerSnapshot.Blocked"/> and <see cref="ContainerSnapshot.UnreachableRoot"/> all
/// follow: an enum crosses two serialisers as a name and the SPA compares it, so adding a value
/// means auditing both wires.
///
/// <c>"Failed"</c>, <c>"Blocked"</c>, <c>"Awaiting"</c>, <c>"Completed"</c>, <c>"Closed"</c>,
/// <c>"Undeclared"</c> or
/// <c>"Running"</c>. <c>"Closed"</c> IS A PERSON'S <c>workflow.closed</c> AND IS NEVER
/// <c>"Completed"</c> - a person clicking Close says "stop counting this", not "this was
/// delivered", and a declaration outranks a close when a workflow carries both.
/// THE LOG CANNOT SEE A ROSTER, so <c>"Running"</c> here means only "open, and
/// no member's last terminal fact says otherwise" - whether anybody is actually Running, and
/// whether any queue has depth, is a question about live containers and is the CLIENT'S half of
/// the decision. Equally, <c>"Undeclared"</c> here carries only the clauses the log can answer (the
/// workflow is open and every member that ran ended <c>completed</c>); the caller adds the ones it
/// owns - nothing Running, no queue depth, and the badge's grace period measured from
/// <see cref="LastActivityAt"/>.
///
/// AN OPEN WORKFLOW IS A FIRST-CLASS STATE, NOT AN ERROR. <c>workflow.completed</c> is a manager's
/// declaration and most workflows never get one, so a design treating "no end marker" as an
/// anomaly would render the anomaly almost always.
/// </param>
/// <param name="StartedAt">
/// When this workflow's FIRST message was published - its root, not the oldest row belonging to a
/// member. Elapsed is measured from here, so a workflow with a long gap in the middle reports the
/// whole gap, which is the honest number.
/// </param>
/// <param name="EndedAt">
/// When its LAST message was published, or null while it is open. Null is the ordinary answer.
/// </param>
/// <param name="ServerNow">
/// The server's clock at the moment this was read.
///
/// ON THE PAYLOAD FOR ONE REASON: THE BROWSER'S CLOCK MAY BE WRONG. The client takes the offset
/// between this and its own clock once per fetch and applies it to every tick. Without it, a
/// machine ten minutes fast shows a workflow that started ten minutes in the future - a negative
/// duration, which is the kind of visible nonsense that gets diagnosed as a server fault.
/// </param>
/// <param name="ExecutionSeconds">
/// Per-member run time SUMMED across every run in this workflow that has a partner.
///
/// Each <c>agentContainer.started</c> is paired with the next <c>agentContainer.completed</c> /
/// <c>agentContainer.failed</c> / <c>agentContainer.blocked</c> from the SAME source within the correlation.
/// Wall time per run, not CPU and not billed time - a member waiting on a network call is counted
/// as executing, because that is what the log records and inventing anything finer would be a
/// guess.
/// </param>
/// <param name="Partial">
/// Whether some runs in this workflow could not be measured - <see cref="RunsUnfinished"/> above
/// zero. The same flag <see cref="TeamTokenTotals.Partial"/> is and for the same reason: the total
/// is real, and it is not the whole story.
/// </param>
/// <param name="RunsCounted">How many runs are actually inside <see cref="ExecutionSeconds"/>.</param>
/// <param name="RunsUnfinished">
/// Runs that started and have no partner - a Host restart mid-flight, which happened repeatedly
/// during this project.
///
/// COUNTED AS UNKNOWN, NEVER AS ZERO AND NEVER AS "STILL RUNNING". This is the
/// <see cref="ContainerUsageRow.RunsWithoutUsage"/> idiom moved one field over: the run really
/// happened and really took time, and nothing here knows how much.
/// </param>
/// <param name="BlockedBy">
/// The member that gave up, when a member's last terminal fact in this workflow is
/// <c>agentContainer.blocked</c> - null otherwise. Named rather than counted, because
/// <c>agentContainer.blocked</c> is an AGENT'S decision to stop and the useful thing to say is whose.
/// </param>
/// <param name="RunsFailed">
/// How many <c>agentContainer.failed</c> rows this workflow holds. A COUNT of runs and not of members:
/// four runs can fail across two members within seconds, and both numbers are worth saying.
/// </param>
/// <param name="FailedMembers">
/// The members whose LAST terminal fact in this workflow is <c>agentContainer.failed</c>, in the order
/// their failures were published. A run that failed and was then re-run successfully is not here,
/// which is what "and nothing has run since" means.
/// </param>
/// <param name="Missing">
/// What the log does not carry, in words a person can act on - null when it carries enough. Set
/// only when <see cref="Available"/> is false, exactly as <see cref="TeamTokenTotals.Missing"/> is.
/// </param>
/// <param name="Members">Per-member execution, so the dialog can say where the time went.</param>
/// <param name="LastActivityAt">
/// The newest message in this workflow, however it was published - the same instant
/// <see cref="EndedAt"/> reports for a CLOSED workflow, and the one it deliberately does not
/// report for an open one.
///
/// HERE FOR THE `UNDECLARED` STATE. The badge is gated on how long a team has been SILENT, and the client owns both the
/// grace period and the clock - the constant lives in `web/src/lib/teamKpis.ts` and there must go
/// on being exactly one of it. Sending the instant rather than a server-side verdict is what keeps
/// that true.
/// </param>
/// <param name="AwaitingFrom">
/// The member waiting on a person, when a member's last terminal fact in this workflow is
/// <c>agentContainer.needsDecision</c> - null otherwise.
///
/// The peer of <see cref="BlockedBy"/> and added for the same reason
/// <see cref="LastActivityAt"/> was: `AWAITING` postdates the record's original shape. It is the
/// one state that describes the platform working exactly as intended and still needing the board
/// to say so, which is why it carries a NAME and an imperative - "Manager is waiting on you" -
/// rather than an observation.
/// </param>
/// <param name="Subject">
/// The subject of the instruction that BEGAN this workflow - the ROOT message's own subject, never
/// re-derived by a caller. Null when the root is not an addressed instruction (a workflow can be
/// rooted in something else entirely - see <see cref="MessageTypes.InstructionPrefix"/>) or when it
/// carries no subject at all.
///
/// READ, NOT RE-SPLIT. <see cref="InstructionText"/> already decides what a subject is - the tell
/// route (`POST /api/teams/{team}/containers/{name}/tell`) writes it, and `KanbanProjector` reads
/// the identical stored field back for a card's title. A third derivation here would be a third
/// answer waiting to disagree with the other two about the same workflow's own name.
///
/// Multi-workflow tracking needs a label a person can read, not a bare correlation seq.
/// </param>
public sealed record TeamWorkflowTiming(
    [property: Description(
        "Whether this team has a workflow to report at all. False for a team that has never run - "
        + "render an em dash rather than 0m, because zero is a measured duration and this is an "
        + "absent one.")]
    bool Available,

    [property: Description(
        "Which workflow this describes - the seq of the message that began it. The current one "
        + "while the team is running, the last one while it is idle.")]
    long? Correlation,

    [property: Description(
        "What the LOG says this workflow is: Completed, Closed, Paused, Failed, Blocked, Awaiting, "
        + "Undeclared or Running, in that order of precedence. A string and never an enum. Running "
        + "here means only that the workflow is open and no member's last terminal fact says "
        + "otherwise - whether anybody is actually running is a question about live containers, "
        + "which the log cannot see. Paused is DERIVED FROM pausedAt below, on the server, and must "
        + "never be re-derived by a client: it outranks Failed because a paused workflow will not "
        + "run again whatever its last run said, so sending a reader to the failure would send them "
        + "to the wrong thing.")]
    string? State,

    [property: Description(
        "When this workflow's first message was published - its ROOT, not the oldest row belonging "
        + "to a member. Elapsed is measured from here.")]
    DateTimeOffset? StartedAt,

    [property: Description(
        "When its last message was published, or null while the workflow is open. Null is the "
        + "ordinary answer: workflow.completed is a manager's declaration and most workflows never "
        + "get one.")]
    DateTimeOffset? EndedAt,

    [property: Description(
        "The server's clock when this was read. The browser's clock may be wrong, so the client "
        + "takes the offset between this and its own once per fetch and applies it to every tick.")]
    DateTimeOffset ServerNow,

    [property: Description(
        "Per-member run time summed across every run that has a partner. It legitimately EXCEEDS "
        + "wall-clock elapsed, because members run concurrently. Never added to elapsed and never "
        + "divided by it.")]
    long ExecutionSeconds,

    [property: Description(
        "Whether some runs in this workflow could not be measured - runsUnfinished above zero.")]
    bool Partial,

    [property: Description("How many runs are actually inside executionSeconds.")]
    int RunsCounted,

    [property: Description(
        "Runs that started and have no terminal partner - a host restart mid-flight. Counted as "
        + "unknown, never as zero and never as still running.")]
    int RunsUnfinished,

    [property: Description(
        "The member that gave up, when a member's last terminal fact here is agentContainer.blocked. "
        + "Null otherwise.")]
    string? BlockedBy,

    [property: Description(
        "how many agentContainer.failed rows this workflow holds. A count of RUNS, not of members.")]
    int RunsFailed,

    [property: Description(
        "The members whose last terminal fact here is agentContainer.failed, in publication order. A "
        + "member that failed and has since run again is not listed.")]
    IReadOnlyList<string> FailedMembers,

    [property: Description(
        "What the log does not carry, in words a person can act on. Null when it carries enough.")]
    string? Missing,

    [property: Description("Per-member execution, so the dialog can say where the time went.")]
    IReadOnlyList<MemberExecution> Members,

    [property: Description(
        "The newest message in this workflow, however published. Unlike endedAt it is present "
        + "while the workflow is open, which is what lets the client measure how long a quiet team "
        + "has been silent without a second constant on the server.")]
    DateTimeOffset? LastActivityAt = null,

    [property: Description(
        "The member waiting on a person, when a member's last terminal fact here is "
        + "agentContainer.needsDecision. Null otherwise.")]
    string? AwaitingFrom = null,

    [property: Description(
        "The subject of the instruction that began this workflow - read from the root message, "
        + "never re-derived. Null when the root is not an addressed instruction or carries no "
        + "subject.")]
    string? Subject = null,

    [property: Description(
        "This workflow's total spend in tokens for budget checking. Null when the workflow has no "
        + "completed or failed runs. Never convicts on unmeasured spend.")]
    WorkflowSpend? Spend = null,

    [property: Description(
        "When this ONE workflow was paused for reaching the per-workflow spend figure in force for "
        + "its team. **NULL MEANS NOT PAUSED** and is the single source of truth for the state - "
        + "`state` is derived from it, not the other way round, and there is deliberately no "
        + "boolean beside it for the two to disagree about. A paused workflow is still OPEN: "
        + "`endedAt` stays null, so it keeps appearing in the open list, which is where the person "
        + "who has to resume it will look.")]
    DateTimeOffset? PausedAt = null,

    [property: Description(
        "Why it paused, in the pump's own words - naming what was spent, the figure, and whether "
        + "that figure was the team's or the instance's. Null when not paused.")]
    string? PausedReason = null,

    [property: Description(
        "The per-workflow figure that was in force when it paused, so a reader can be told what it "
        + "hit without re-resolving the team setting. Null when not paused.")]
    long? PausedLimit = null,

    [property: Description(
        "The same measurement as `spend` above, counted only SINCE THE LAST NUDGE - which is what "
        + "the budget is actually evaluated against.\n\n"
        + "**THIS IS THE FIGURE A BAR MUST MEASURE AGAINST, not `spend`.** `spend` is the "
        + "whole-workflow total and never comes back down, so after a person nudges or resumes a "
        + "workflow the cumulative figure still reads over the limit while the guard is perfectly "
        + "happy - a bar drawn from it goes on alarming about a workflow that is running again, "
        + "which teaches a reader to ignore it. The rule runs both ways: a bar that reassures "
        + "wrongly is worse than no bar, and so is one that alarms wrongly.\n\n"
        + "`spend` stays and is still the right figure for **what did this workflow cost** - which "
        + "is what the backlog execution record reports, and it must not become a window. Two "
        + "questions, two fields, each named for what it answers.\n\n"
        + "Null under exactly the same condition `spend` is null: no completed or failed runs.")]
    WorkflowSpend? SpendSinceNudge = null,

    [property: Description(
        "The outcome this workflow serves now: its newest link, followed through `mergedInto` "
        + "(id, name, status). Null for none. Set by the team workflows route, not the projection.")]
    WorkflowOutcome? Outcome = null);

/// <summary>
/// One member's run time inside a workflow.
/// </summary>
/// <param name="Member">The member's name - the second half of its qualified id.</param>
/// <param name="Runs">Every run this member started in this workflow, finished or not.</param>
/// <param name="ExecutionSeconds">
/// Summed over the runs that have a partner, or NULL when none of them do.
///
/// Null rather than zero, for <see cref="TeamWorkflowTiming.RunsUnfinished"/>'s reason: a member
/// whose only run was cut short by a Host restart spent real time, and reporting 0s would be a
/// measurement of something that never happened.
/// </param>
/// <param name="Unfinished">How many of this member's runs have no partner.</param>
public sealed record MemberExecution(
    [property: Description("The member's name - the second half of its qualified id.")]
    string Member,

    [property: Description("Every run this member started in this workflow, finished or not.")]
    int Runs,

    [property: Description(
        "Summed over the runs that have a terminal partner, or null when none of them do - never "
        + "zero, which would be a measurement of something that never happened.")]
    long? ExecutionSeconds,

    [property: Description("How many of this member's runs have no terminal partner.")]
    int Unfinished);

/// <summary>
/// One team's workflow projection, inside a rollup - both the singular and the plural, so the
/// Teams table can answer "what is this team doing" for every team it lists, not only the one a
/// person happens to have made active.
/// </summary>
/// <param name="Team">The team's IDENTIFIER, which is what every client keys on.</param>
/// <param name="Workflow">The same projection <c>GET /api/teams/{team}/workflow</c> answers.</param>
/// <param name="OpenWorkflows">
/// The same projection <c>GET /api/teams/{team}/workflows</c> answers - EVERY workflow this team has
/// run since its floor, OPEN AND CLOSED ALIKE, not only the newest and not only the open ones. See
/// <see cref="TeamWorkflows"/> for what each field on it counts; <see cref="TeamWorkflows.OpenCount"/>
/// is the only figure there that is still about open work alone.
///
/// NAMED <c>OpenWorkflows</c> RATHER THAN <c>Workflows</c> DELIBERATELY, so it cannot be mistaken
/// for a typo of the singular <see cref="Workflow"/> field beside it at a call site: the two answer
/// different questions (one workflow's timing versus a whole team's list), and a name that differs
/// only by a trailing "s" is exactly the pair a reviewer's eye slides past.
///
/// THE NAME IS ABOUT THAT PAIR AND IS NOT A CLAIM THAT THE LIST IS OPEN-ONLY. The projection
/// carries closed workflows too, and this is a WIRE name (<c>openWorkflows</c>) that clients key
/// on. Read the payload's own doc, never this field's name, for what is in it.
///
/// Computed from the SAME <c>floor</c> the singular field above uses, in the same loop, for
/// <c>ElapsedForTeamAsync</c>'s reason: a mismatched floor makes a team deleted and recreated under
/// the same names report its PREDECESSOR'S open work.
/// </param>
/// <param name="Running">How many of this team's containers hold an instance-wide run slot now,
/// from <c>WipLedger.View()</c>.</param>
/// <param name="Waiting">How many of this team's containers are queued for a slot.</param>
/// <param name="Held">The members (container names) queued for a slot, in queue order. Empty when
/// none.</param>
/// <param name="SlotStatus">"waiting for a slot" when any member of this team is queued for a slot,
/// else null. A team whose members are all idle but whose Manager has a delivered wake held by the
/// limit reads this, never "undeclared" or idle: the wake is real and has not started yet.</param>
public sealed record TeamRollupRow(
    string Team,
    TeamWorkflowTiming Workflow,
    TeamWorkflows OpenWorkflows,
    int Running = 0,
    int Waiting = 0,
    IReadOnlyList<string>? Held = null,
    string? SlotStatus = null)
{
    /// <summary>The words <see cref="SlotStatus"/> carries.</summary>
    public const string WaitingForASlot = "waiting for a slot";
}

/// <summary>
/// EVERY WORKFLOW A TEAM HAS RUN SINCE ITS FLOOR, open and closed alike, and one duration over the
/// open ones.
///
/// A member works several workflows at once - `KanbanProjector` keys a card by correlation AND
/// member - and this projection describes all of them.
///
/// THE LIST IS DELIBERATELY NOT FILTERED TO OPEN WORKFLOWS. A not-closed filter with a
/// `LIMIT 1` fallback for a blank tile would render a team that finished three pieces of work as ONE
/// row, under a heading claiming the team held it open. There is no mode flag and no second route;
/// the list carries closed workflows too, so a team that has ever run always has a first row for
/// the tile to take.
///
/// <see cref="EarliestStartedAt"/> IS A SPAN AND NEVER A SUM, and this is the trap the record exists
/// to close. Per-member execution time already carries the identical rule ("legitimately EXCEEDS
/// wall-clock elapsed, because members run concurrently. Never added to elapsed and never divided by
/// it"). Three workflows covering the same ten minutes SUMMED reads as thirty - a number larger than
/// the time that has actually passed, rendered confidently. The client subtracts this instant from
/// now; there is no total anywhere on this record and there must not be one.
///
/// IT CROSSES HTTP ONLY, exactly as <see cref="TeamWorkflowTiming"/> does and for the same reason:
/// <see cref="ContainerSnapshot"/> rides SignalR to every browser holding the team, and this is a
/// team-level query.
/// </summary>
/// <param name="Available">
/// Whether this team has anything to report. False for a team that has never run - render an
/// ABSENCE (an em dash), never <c>0</c>: zero is a measured count and this is an unmeasured one.
/// </param>
/// <param name="OpenCount">
/// How many workflows this team HOLDS OPEN - carrying no terminal row
/// (<c>workflow.completed</c> or <c>workflow.closed</c>) that nothing has woken since; see
/// `WorkflowOpenSql` in `Harness.Messaging` for the one place that predicate lives - UNCAPPED.
///
/// IT IS NOT THE TRUNCATION SIGNAL AND MUST NOT BE READ AS ONE. <see cref="Workflows"/> carries
/// closed workflows as well, so a team with three finished workflows and nothing open reports
/// <c>0</c> here beside three entries - compare against <see cref="TotalCount"/> instead.
///
/// IT ANSWERS ITS OWN QUESTION. The team tile and the `status` tool both read it for "how much is
/// this team still carrying", which is not "how much has it done" and is not derivable from a list
/// that is not open-only.
///
/// ALLOWED TO GROW, and that is the design rather than an oversight. A wake that ended without a
/// declaration leaves a workflow open forever, and this number is what makes that visible - it is a
/// defect this project has already paid for once, where a whole role's skill never named the verb
/// and every team of that kind read STALLED permanently with nothing saying why. The remedy is the
/// skills' "nothing to do" close, not a projection that quietly forgets.
/// </param>
/// <param name="TotalCount">
/// EVERY WORKFLOW THIS TEAM HAS RUN SINCE ITS FLOOR, open and closed alike - UNCAPPED, and the
/// number to compare <see cref="Workflows"/>'s length against to tell truncation.
///
/// <see cref="Workflows"/> is capped at fifty entries so the tile stays cheap to fetch; this is
/// not. A team that has run sixty workflows reports <c>60</c> here with fifty entries in the list,
/// and a client rendering "N of M" reads M from here and N from <see cref="Workflows"/>.Count.
///
/// A SIBLING OF <see cref="OpenCount"/> RATHER THAN A REPLACEMENT: the two answer different
/// questions and both have readers. Since the floor, for the reason every figure on this record is
/// - a team deleted and recreated under the same names must not inherit its predecessor's history.
/// </param>
/// <param name="EarliestStartedAt">
/// When the OLDEST open workflow began - the start of the SPAN. OPEN ONES ONLY, so it is null for a
/// team whose every workflow is finished even though <see cref="Workflows"/> still lists them.
/// UNCAPPED like the two counts and for the same reason: the fifty-entry cap on
/// <see cref="Workflows"/> drops the OLDEST correlations first, which is exactly where this instant
/// is most likely to live, so it cannot be derived from the capped list.
/// </param>
/// <param name="ServerNow">The server's clock when this was read. The browser's may be wrong.</param>
/// <param name="Workflows">
/// One entry per workflow this team has run since its floor, newest first - OPEN AND CLOSED ALIKE,
/// each reporting its own state. A team that has just finished everything therefore still has
/// something to render rather than an em dash where a completion belongs, and it gets that from
/// this list rather than from a fallback query.
///
/// CAPPED AT FIFTY. <see cref="TotalCount"/> is not, so a shorter list than THAT count is how a
/// client tells it was truncated - render "50 of 63", never a bare 50. Not
/// <see cref="OpenCount"/>, which counts something else entirely.
/// </param>
/// <param name="Missing">
/// What the log does not carry, in words a person can act on - null when it carries enough. Set only
/// when <see cref="Available"/> is false.
/// </param>
public sealed record TeamWorkflows(
    [property: Description(
        "Whether this team has anything to report. False for a team that has never run - render an "
        + "em dash rather than 0.")]
    bool Available,

    [property: Description(
        "How many workflows this team HOLDS OPEN, uncapped. NOT the truncation signal and not the "
        + "length of `workflows`, which lists closed workflows too - a team with three finished "
        + "workflows and nothing open reports 0 here beside three entries. Compare "
        + "`workflows.length` against `totalCount` instead. Allowed to grow: an undeclared workflow "
        + "stays open, and this number is what makes that visible.")]
    int OpenCount,

    [property: Description(
        "Every workflow this team has run since its floor, uncapped - compare `workflows.length` "
        + "against this to tell truncation.")]
    int TotalCount,

    [property: Description(
        "When the OLDEST OPEN workflow began - the start of the SPAN the tile renders, uncapped like "
        + "the counts and for the same reason: the fifty-entry cap on `workflows` drops the oldest "
        + "correlations first. Null when nothing is open, even though `workflows` still lists the "
        + "closed ones. Never a sum of the open workflows' durations, which would exceed the time "
        + "that has actually passed.")]
    DateTimeOffset? EarliestStartedAt,

    [property: Description("The server's clock when this was read.")]
    DateTimeOffset ServerNow,

    [property: Description(
        "One entry per workflow this team has run since its floor, newest first - open and closed "
        + "alike, each reporting its own state. Capped at fifty - compare its length against "
        + "totalCount to tell truncation.")]
    IReadOnlyList<TeamWorkflowTiming> Workflows,

    [property: Description(
        "What the log does not carry, in words a person can act on. Null when it carries enough.")]
    string? Missing = null);

/// <summary>
/// Every reachable team's workflow projection, in one read.
///
/// IT CARRIES ONLY WHAT A CLIENT LACKS. Names and container snapshots are already on
/// `/api/overview`, which the Console fetches on load and after every reconnect - repeating them
/// here would be a second source for a team's name, and two sources for one fact is how two screens
/// start disagreeing about which team you are looking at.
///
/// Both `TeamWorkflowTiming` and `TeamWorkflows` are reused verbatim, each carrying its own
/// per-row `ServerNow`: the client takes its clock offset from that field already, and hoisting a
/// copy to the envelope would be a second spelling of one value.
/// </summary>
public sealed record TeamRollup(IReadOnlyList<TeamRollupRow> Teams);
