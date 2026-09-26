using System.ComponentModel;

namespace Harness.Contracts;

/// <summary>
/// WHICH WORKFLOWS ARE OPEN UNDER ONE MEMBER, AND WHETHER ANYTHING IS STILL WORKING EACH ONE.
///
/// The read-side twin of <c>workflow-complete</c>. That route answers "may I declare this" with a
/// 204 or a refusal naming what is busy; this one asks the identical questions of every open
/// workflow at once and ANSWERS THEM IN A LIST, declaring nothing and changing nothing. A Manager
/// holding three threads sees which of them are its own to close and which of them nobody is
/// working, not only a per-TEAM list of subjects.
///
/// IT IS BUILT TO BE READ AND MUST NOT BECOME SOMETHING ANYTHING RELIES ON. A
/// skill INSTRUCTS and cannot ENFORCE, and a Manager can SAY it is closing an item and then not. Making the state visible is worth
/// doing on its own terms; it is not the mechanism, and a design that starts treating "the Manager
/// could have looked" as the safeguard has mistaken this for one.
///
/// NOTHING HERE IS A SECOND STORE. Every field is derived per request from the message log, the
/// live container snapshots and the pending-delivery table - the same three sources
/// <c>workflow-complete</c> reads, in the same order.
///
/// IT CROSSES HTTP ONLY, like <see cref="TeamWorkflows"/> and for the same reason: it is a
/// team-level query, and <see cref="ContainerSnapshot"/> rides SignalR to every browser holding the
/// team.
/// </summary>
/// <param name="Available">
/// Whether this team has anything to report at all, carried straight through from
/// <see cref="TeamWorkflows.Available"/>. False for a team that has never run - and false is not
/// "nothing is open", which is <see cref="OpenCount"/> at zero with <see cref="Available"/> true.
/// The two have different fixes and a reader must be able to tell them apart.
/// </param>
/// <param name="Member">
/// The member these workflows are open UNDER - the bare name, echoed back so a reader can never
/// mistake whose view this is. It is the member whose own Running state and started pending row are
/// excluded from every <see cref="OpenWorkflowRow.Working"/> list below.
/// </param>
/// <param name="OpenCount">
/// How many workflows the TEAM holds open, uncapped - <see cref="TeamWorkflows.OpenCount"/> passed
/// through unchanged.
///
/// IT COUNTS THE TEAM'S OPEN WORKFLOWS AND NOT THIS MEMBER'S, which is why it can exceed
/// <see cref="Workflows"/>'s length for a reason that has nothing to do with truncation. It is here
/// for the one thing it can honestly say: how many open workflows the fifty-entry cap on
/// <see cref="TeamWorkflows.Workflows"/> was measured against, so a renderer can print "N of M"
/// rather than a bare N it cannot vouch for. Compare the two; never subtract them.
/// </param>
/// <param name="ServerNow">
/// The server's clock when this was read - on the envelope rather than per row, because every row
/// was derived from one read. A caller subtracts <see cref="OpenWorkflowRow.StartedAt"/> from it
/// rather than from its own clock, for the reason <see cref="TeamWorkflowTiming.ServerNow"/> gives.
/// </param>
/// <param name="Workflows">
/// One entry per OPEN workflow, newest first. Closed workflows are filtered out HERE, on the
/// server, where the test is <c>EndedAt is null</c> on a typed record rather than a probe into a
/// JSON payload - so a renderer of this route cannot show a list that carries closed workflows
/// under a heading that counts open ones.
///
/// IT INHERITS THE FIFTY-ENTRY CAP on <see cref="TeamWorkflows.Workflows"/> rather than adding a
/// second one, because it is a filter over that list and not a second query. The cap keeps the
/// newest fifty workflows of ANY state, so this list can be shorter than <see cref="OpenCount"/>.
/// </param>
/// <param name="Missing">
/// What the log does not carry, in words a person can act on - null when it carries enough. Set
/// only when <see cref="Available"/> is false, exactly as <see cref="TeamWorkflows.Missing"/> is.
/// </param>
public sealed record MemberOpenWorkflows(
    [property: Description(
        "Whether this team has anything to report at all. False for a team that has never run - "
        + "which is NOT the same as nothing being open, which is openCount 0 with available true.")]
    bool Available,

    [property: Description(
        "The member these workflows are open under - the bare name. Its own Running state and its "
        + "own started pending row are what every `working` list below excludes.")]
    string Member,

    [property: Description(
        "How many workflows the TEAM holds open, uncapped. Compare `workflows.length` against it to "
        + "tell truncation; never subtract them.")]
    int OpenCount,

    [property: Description("The server's clock when this was read, for the whole envelope.")]
    DateTimeOffset ServerNow,

    [property: Description(
        "One entry per OPEN workflow, newest first. Closed workflows are filtered out here on the "
        + "server, so a renderer cannot announce delivered work as live.")]
    IReadOnlyList<OpenWorkflowRow> Workflows,

    [property: Description(
        "What the log does not carry, in words a person can act on. Null when it carries enough.")]
    string? Missing = null);

/// <summary>
/// ONE OPEN WORKFLOW, SEEN FROM ONE MEMBER.
///
/// Named <c>OpenWorkflowRow</c> rather than <c>MemberOpenWorkflow</c> deliberately, following
/// <see cref="TeamRollupRow"/>: a row type differing from its envelope only by a trailing "s" is
/// exactly the pair a reviewer's eye slides past, which is the rule
/// <see cref="TeamRollupRow.OpenWorkflows"/>'s own doc comment states.
/// </summary>
/// <param name="Correlation">
/// The seq of the message that began this workflow - its identity. Carried so a reader can find the
/// thread (<c>GET /api/workflows/{correlationId}</c>), and NEVER so a caller can hand it back to
/// <c>workflow-complete</c>: that route takes its correlation from the container's own causation
/// and must go on doing so, or the hole that rule closes is reopened from the read side.
/// </param>
/// <param name="Subject">
/// The subject of the instruction that began it, read from the root - see
/// <see cref="TeamWorkflowTiming.Subject"/>, which is where this comes from. Null when the root was
/// not an addressed instruction or carried no subject; a renderer says so in words rather than
/// showing a bare seq, because a number is not something a Manager can recognise its own work by.
/// </param>
/// <param name="StartedAt">When this workflow's ROOT message was published.</param>
/// <param name="LastActivityAt">
/// The newest message in it, however published. With <see cref="MemberOpenWorkflows.ServerNow"/> it
/// says how long a workflow has been SILENT - a different question from how long it has been open,
/// and the one that matters for a workflow nobody is working.
/// </param>
/// <param name="Owner">
/// WHICH MEMBER MAY DECLARE THIS WORKFLOW COMPLETE - the bare name, derived exactly as
/// <c>workflow-complete</c> derives it: the container the root instruction addressed, falling back
/// to this team's Manager when the root addressed nobody (a schedule firing, a kanban card event).
/// Never null, because the fallback is total - see <c>WorkflowOwner</c> for why a workflow nobody
/// may declare is the defect that fallback exists to prevent.
/// </param>
/// <param name="Yours">
/// Whether <see cref="Owner"/> IS the member this view belongs to - the thing a reader actually
/// wants, and the one a name comparison gets subtly wrong: <c>ContainerId</c> equality is
/// case-insensitive and a string comparison in a renderer need not be. Derived once, here, on the
/// side that owns the rule.
/// </param>
/// <param name="Current">
/// Whether this is the workflow that member is running under AT THIS INSTANT - its
/// <c>CurrentCorrelation</c>. False for every row between invocations, which is correct and is why
/// it must not be read as "this member's workflow": <see cref="Yours"/> answers that.
/// </param>
/// <param name="Members">
/// Every member with a run in this workflow, so a reader can tell whose thread it is. A record of
/// who HAS run, not of who is running - <see cref="Working"/> is the only field that says that.
/// </param>
/// <param name="Working">
/// WHAT IS STILL IN FLIGHT UNDER THIS WORKFLOW, in the same sentences <c>workflow-complete</c>
/// refuses with and from the same predicate: a member Running under this correlation, and a
/// delivery accepted under it.
///
/// AN EMPTY LIST IS THE WHOLE POINT OF THIS RECORD. A Manager legitimately
/// waiting on a member is told apart from one waiting for something that will never arrive by
/// exactly this list, and both a Running member and an accepted-but-unstarted delivery must appear
/// in it or the first legitimate wait is misreported as an abandoned workflow. Empty means nothing
/// is coming - no member is working it and none has accepted work under it.
///
/// THE VIEWING MEMBER IS EXCLUDED, and that is what makes the empty case answerable at all. A
/// member reads this from inside its own run, so its own Running state is true by construction and
/// would mask every row it asks about. The exclusion is the one <c>workflow-complete</c> applies to
/// its caller - the Running field and a STARTED pending row, never an unstarted one - so "empty
/// here" and "not refused there" mean the same thing about the same instant.
///
/// PROSE, NOT A STRUCTURE, because it is the same prose the refusal uses and a second shape would
/// be a second description of one fact. Read it as text; do not parse it.
/// </param>
public sealed record OpenWorkflowRow(
    [property: Description(
        "The seq of the message that began this workflow. For finding the thread - never for "
        + "handing back to workflow-complete, which takes its correlation from the container's own "
        + "causation and never from a caller.")]
    long Correlation,

    [property: Description(
        "The subject of the instruction that began it, read from the root. Null when the root was "
        + "not an addressed instruction or carried no subject.")]
    string? Subject,

    [property: Description("When this workflow's root message was published.")]
    DateTimeOffset? StartedAt,

    [property: Description(
        "The newest message in it, however published - with serverNow, how long it has been SILENT, "
        + "which is not the same question as how long it has been open.")]
    DateTimeOffset? LastActivityAt,

    [property: Description(
        "Which member may declare this workflow complete - the container the root instruction "
        + "addressed, falling back to this team's Manager when it addressed nobody.")]
    string Owner,

    [property: Description("Whether that owner is the member this view belongs to.")]
    bool Yours,

    [property: Description(
        "Whether this is the workflow that member is running under at this instant. False between "
        + "invocations, which is correct - `yours` is what says whose workflow it is.")]
    bool Current,

    [property: Description("Every member with a run in this workflow.")]
    IReadOnlyList<string> Members,

    [property: Description(
        "What is still in flight under this workflow, in the sentences workflow-complete refuses "
        + "with - a member Running under it, or a delivery accepted under it. EMPTY MEANS NOTHING "
        + "IS COMING. The viewing member is excluded, on the same terms workflow-complete excludes "
        + "its caller. Prose: read it, do not parse it.")]
    IReadOnlyList<string> Working);
