namespace Harness.Contracts;

/// <summary>
/// The durable, append-only, ordered log. The substrate everything else sits on.
///
/// It knows nothing about Agent Containers, teams or agents — it stores messages and hands them back
/// in order. Subscription semantics live in <see cref="ISubscriptions"/>; who has read what lives in
/// the cursor. Keeping those three apart is what lets a projection, an audit view or a future
/// consumer be added without the log learning anything new.
/// </summary>
public interface IMessageLog
{
    /// <summary>
    /// Appends and returns the stored message, with the seq, correlation and depth the log assigned.
    /// </summary>
    Task<Message> AppendAsync(NewMessage message, CancellationToken ct = default);

    /// <summary>
    /// The next messages after <paramref name="afterSeq"/> whose type is in <paramref name="types"/>,
    /// oldest first, at most <paramref name="max"/>.
    ///
    /// Filtering by type in the QUERY rather than after it matters: a subscriber interested in one
    /// rare type must not have to read — and pay for — every message published by every other
    /// container to find it.
    /// </summary>
    Task<IReadOnlyList<Message>> ReadAfterAsync(
        long afterSeq, IReadOnlyCollection<string> types, int max, CancellationToken ct = default);

    /// <summary>
    /// The LAST <paramref name="max"/> messages after <paramref name="afterSeq"/> whose type is in
    /// <paramref name="types"/> — still returned oldest first.
    ///
    /// The other end of the same window, and a separate method rather than a flag because the two
    /// callers want opposite things and one of them must never change. The delivery pump reads
    /// forward from a cursor and advances it: it has to see the OLDEST first, in order, or work is
    /// skipped. A person watching a board wants the PRESENT.
    ///
    /// Reading the oldest for a viewer is not merely stale, it degrades to nothing: the route that
    /// does this filters by team after the read, so once a log holds `max` rows belonging to teams
    /// the caller cannot see, the window never contains anything else and the feed is empty
    /// forever: "the board shows nothing" on a team that is actively running.
    ///
    /// THE TRADE: a caller more than one window behind skips the middle rather than catching up
    /// through it. That is the right way round for a live feed — the alternative is a board that
    /// faithfully renders last week — and the SPA refetches on every snapshot push rather than on
    /// a timer, so falling a window behind takes a burst nothing here can produce.
    ///
    /// ALSO MATCHES AN ADDRESSED INSTRUCTION (`container.instruction.*`), regardless of
    /// <paramref name="types"/> — the one prefix family too large to enumerate: one distinct type per
    /// live container, so it cannot be a member of the finite set this method otherwise filters on.
    /// No subscription names a container's own instruction type, so this is what delivers it.
    /// This is a deliberate widening of the contract's own words above, not an oversight — see
    /// the implementation for why.
    /// </summary>
    Task<IReadOnlyList<Message>> ReadLatestAsync(
        long afterSeq, IReadOnlyCollection<string> types, int max, CancellationToken ct = default);

    /// <summary>
    /// The newest <paramref name="max"/> messages this container PUBLISHED, newest first.
    ///
    /// A SIBLING of ReadLatestAsync rather than a filter over it, for the reason that method exists
    /// at all: a cap applied before a filter degrades to nothing rather than to less. Filtering a
    /// 200-row window in memory answers empty for any member quieter than its team.
    ///
    /// Matches on SOURCE alone, which is exact here - a container event publishes its own qualified
    /// id as Source and that is authoritative. It deliberately does NOT also match instructions
    /// addressed to the container: those are somebody else's publication, they are already in the
    /// team feed, and matching them would need the type/source disjunction MessageTeam.Of owns,
    /// which must not be copied into SQL.
    ///
    /// Case-insensitive, because ContainerId equality is.
    ///
    /// FLOORED by <paramref name="sinceSeq"/>: everything at or below it belongs to whatever
    /// same-(team, name) container came before this one and is not this one's business. `>` and not
    /// `>=`, matching SqliteLedger, which reads the same floor the same way.
    ///
    /// Required rather than defaulted, and that is the point. A floor a caller can forget is a
    /// floor that is simply ABSENT wherever nobody remembered to ask - and then this method hands a
    /// recreated team its predecessor's tail with nothing failing and no test able to see it. `floor_seq` has one writer, and the rule is enforced by a
    /// PARAMETER rather than by structure, so the parameter has to be one nobody can omit.
    /// </summary>
    Task<IReadOnlyList<Message>> ReadForContainerAsync(
        string qualifiedId, long sinceSeq, int max, CancellationToken ct = default);

    /// <summary>
    /// One member's history BEFORE <paramref name="beforeSeq"/>, newest first, at most
    /// <paramref name="max"/> - the backward cursor under the board's live window. The next
    /// page's cursor is the last row's seq.
    ///
    /// A member's history is BOTH halves the ledger reads: what it was TOLD (its addressed
    /// instruction type, matched exactly, which is why the caller hands in the FOUND container) and
    /// what it PUBLISHED (its own source, case-insensitively) - the latter narrowed to
    /// <paramref name="types"/>, so an older page holds the same kinds of row as the live feed above
    /// it. The filter and the floor are IN the query and the cap after them, for the reason
    /// <see cref="ReadForContainerAsync"/> gives: a cap before a filter degrades to nothing.
    ///
    /// FLOORED by <paramref name="sinceSeq"/>, required for the same reason it is there: everything
    /// at or below it belongs to a same-named predecessor. Walking the cursor down therefore ends
    /// at the first row THIS member ever received, and no further.
    /// </summary>
    Task<IReadOnlyList<Message>> ReadMemberBeforeAsync(
        ContainerId container, long sinceSeq, long beforeSeq, IReadOnlyCollection<string> types,
        int max, CancellationToken ct = default);

    /// <summary>
    /// Token in/out totals for every completed or failed run this team published, grouped by source.
    ///
    /// A projection of payload fields via json_extract, not a second store and not a slice of a
    /// feed. Source is the container's qualified id (<c>Team/Name</c>); the team filter is that
    /// prefix because completed/failed events publish the container's own id as Source.
    ///
    /// Rows whose payload lacks <c>tokensIn</c>/<c>tokensOut</c> are counted as runs-without-usage
    /// rather than as zeros.
    ///
    /// FLOORED by <paramref name="sinceSeq"/>, which the caller computes as the MINIMUM floor
    /// across the team's live containers. ONE value for a whole team, though floors are per member,
    /// and that is a trade rather than an oversight: a per-source floor would mean this query
    /// joining <c>team_members</c>, and this interface deliberately knows nothing about containers -
    /// which is why the ledger is a SECOND reader over the same table rather than a method added
    /// here.
    ///
    /// What the trade costs, stated so nobody rediscovers it as a bug: it excludes everything
    /// published before the EARLIEST current member existed, which is what a team deleted and
    /// recreated under the same names needs, and it does NOT exclude one member deleted and re-hired
    /// mid-life - whose spend the team genuinely did incur.
    ///
    /// It also bounds the one unbounded aggregate in the system. This is a json_extract over every
    /// completed/failed row a team has ever published, and a floor is the only thing that stops it
    /// growing with the log forever.
    /// </summary>
    Task<IReadOnlyList<ContainerUsageRow>> SumUsageForTeamAsync(
        string team, long sinceSeq, CancellationToken ct = default);

    /// <summary>
    /// One member's finished runs that recorded the agent's own transcript, newest first,
    /// with <c>seq</c> below <paramref name="beforeSeq"/>, at most <paramref name="max"/>: its
    /// `completed` and `failed` rows carrying <c>agentTranscript</c>, each with the start of the run
    /// that row closed and whether that run published `blocked`. Rows with no
    /// <c>agentTranscript</c> are not returned.
    ///
    /// FLOORED by <paramref name="sinceSeq"/> for <see cref="ReadMemberBeforeAsync"/>'s reason: what
    /// is at or below it belongs to a same-named predecessor. Source matched case-insensitively.
    /// </summary>
    Task<IReadOnlyList<RunRow>> ReadRunsAsync(
        ContainerId container, long sinceSeq, long beforeSeq, int max, CancellationToken ct = default);

    /// <summary>
    /// <see cref="ReadRunsAsync(ContainerId, long, long, int, CancellationToken)"/>, choosing which
    /// runs: <see cref="RunsWith.Transcript"/> is that method; <see cref="RunsWith.AnyRun"/> is every
    /// finished run, one row per run - the terminal row that carries the run (no
    /// <c>usageCountedOn</c>), not one per batched message. A plugin member's runs record no
    /// transcript, so they are read this way.
    /// </summary>
    Task<IReadOnlyList<RunRow>> ReadRunsAsync(
        ContainerId container, long sinceSeq, long beforeSeq, int max, RunsWith which, CancellationToken ct = default);

    /// <summary>
    /// How long this team's newest workflow has been going, and how much run time went into it.
    ///
    /// A projection, exactly as <see cref="SumUsageForTeamAsync"/> is, and a FOURTH READER of
    /// <c>floor_seq</c> - which is the point. That column is one store of a fact with several
    /// readers, and every reader must consult it: a reader that ignored it would make the tile it
    /// feeds show a reset team its PREDECESSOR'S elapsed time and its predecessor's workflow.
    ///
    /// <paramref name="sinceSeq"/> IS REQUIRED AND POSITIONAL for that reason, exactly as it is
    /// above: a floor a caller can forget is a floor that is simply absent wherever nobody
    /// remembered to ask. The caller computes it as the MINIMUM floor across the team's live
    /// containers - ONE value for a whole team, the same trade
    /// <see cref="SumUsageForTeamAsync"/> documents and for the same reason, since a per-source
    /// floor would mean joining <c>team_members</c> and this interface deliberately knows nothing
    /// about containers. The cost is identical and is not restated here as a new limit.
    ///
    /// Team membership is the <c>Team/</c> prefix on <c>Source</c>, exactly as the usage query
    /// establishes it: container events publish the container's own qualified id.
    ///
    /// READING THE CLOCK HERE IS CORRECT AND IS NOT A LAPSE. "Read <c>causation_seq</c>, never the
    /// clock" governs CAUSALITY - whether one message woke another. Measuring a duration is the one
    /// job <c>OccurredAt</c> exists for. Stated so nobody deletes it as a violation.
    ///
    /// It returns INSTANTS and never a computed elapsed integer: a server-computed number would
    /// need polling to move, and polling a figure a subtraction can produce is the wrong trade.
    /// </summary>
    Task<TeamWorkflowTiming> ElapsedForTeamAsync(
        string team, long sinceSeq, CancellationToken ct = default);

    /// <summary>
    /// EVERY WORKFLOW THIS TEAM HAS RUN SINCE ITS FLOOR, newest first - open and closed alike, not
    /// only the newest and not only the open ones. See <see cref="TeamWorkflows"/>.
    ///
    /// A member can hold several workflows at once and keep them apart, so "the newest workflow"
    /// does not describe what a team is doing. This calls the same per-correlation
    /// projection <see cref="ElapsedForTeamAsync"/> uses once per correlation instead of once for
    /// the newest.
    ///
    /// NAMED FOR WHAT IT RETURNS: open and closed workflows alike. A name that said "open" would
    /// be actively misleading beside <see cref="TeamWorkflows.OpenCount"/> on the answer.
    ///
    /// <paramref name="sinceSeq"/> IS REQUIRED AND POSITIONAL for the identical reason it is on
    /// <see cref="ElapsedForTeamAsync"/> and <see cref="SumUsageForTeamAsync"/>: a floor a caller
    /// can forget is a floor that is simply absent wherever nobody remembered to ask. The caller
    /// computes it as the MINIMUM floor across the team's live containers - the same trade the
    /// other two document and for the same reason. It is what makes "every workflow this team has
    /// run" mean THIS team's: a team deleted and recreated under the same names must not show its
    /// predecessor's history.
    /// </summary>
    Task<TeamWorkflows> WorkflowsForTeamAsync(
        string team, long sinceSeq, CancellationToken ct = default);

    /// <summary>
    /// WHICH OF THESE WORKFLOWS ARE STILL OPEN, in one query - the same predicate
    /// <see cref="WorkflowsForTeamAsync"/> counts <see cref="TeamWorkflows.OpenCount"/> with, asked
    /// about a caller's own list of correlations instead of a team's.
    ///
    /// <para>
    /// Exists for the backlog list, which knows the correlation of every item's current dispatch
    /// and has to say which are in flight. The team-scoped projection cannot answer it: it is capped
    /// at fifty entries, it costs a timing projection per open workflow the list does not need, and
    /// a dispatch's correlation is already exact. One membership question, one query, no cap.
    /// </para>
    ///
    /// <para>
    /// NO FLOOR, DELIBERATELY. A correlation is a seq and is globally unique, so it can never belong
    /// to a predecessor of the team that holds it; whether it sits BELOW a recreated team's floor is
    /// the caller's question, asked before it gets here - the caller holds the registry and the
    /// live containers, and this store holds neither.
    /// </para>
    ///
    /// <para>An empty request is an empty answer without a query. Unknown correlations are absent.</para>
    /// </summary>
    Task<IReadOnlySet<long>> OpenWorkflowsAmongAsync(
        IReadOnlyCollection<long> correlations, CancellationToken ct = default);

    /// <summary>
    /// ONE MEMBER'S LIVE MARKS - what its last word to a person still is, according to the log.
    ///
    /// <para>
    /// EXISTS SO A RESTART DOES NOT ERASE THEM. `ContainerSnapshot.Blocked`, `.NeedsDecision`
    /// and `.Failed` are fields on a live `MemberRuntime` and nothing writes them down, so without
    /// this a Host restart would take every one of them while the rows that produced them stay in the
    /// append-only log - and the team tile, a projection over that log, would report BLOCKED over a
    /// card showing an idle member with nothing to say.
    /// </para>
    ///
    /// <para>
    /// DERIVED RATHER THAN PERSISTED, and that choice is the reason this method exists instead of a
    /// `team_members` column. A column would be a SECOND STORE of a fact the log already holds -
    /// the defect class `floor_seq` illustrates - and it would need writing at four call sites that
    /// only publish.
    /// </para>
    ///
    /// <para>
    /// TWO PREDICATES, BECAUSE THE FACTS DIFFER IN KIND. `blocked` and `needs-decision` are
    /// published MID-RUN by the agent through the CLI, and the platform publishes `completed` when
    /// the run then exits - so "is there a later row" is always yes and always wrong. They are live
    /// unless the member has STARTED AGAIN. `failed` is a run OUTCOME, so it and `completed` are
    /// alternatives and the member's LAST of the two decides; see
    /// <see cref="TeamWorkflowTiming.State"/>.
    /// </para>
    ///
    /// <para>
    /// NOT SCOPED TO A CORRELATION, unlike <see cref="ElapsedForTeamAsync"/>, which describes one
    /// workflow. This describes a MEMBER: the question is whether it has been woken since it spoke,
    /// and a wake counts whatever workflow it belongs to. Scoping to the current workflow would
    /// clear a member's unanswered give-up by giving somebody ELSE a job.
    /// </para>
    ///
    /// <para>
    /// IT RANKS NOTHING. All three can come back set, and the caller chooses - because the SPA's
    /// `containerMark()` and the tile's own ladder already order these facts in two places that
    /// must agree, and a store that pre-ranked them would be a silent third.
    /// </para>
    ///
    /// <paramref name="sinceSeq"/> IS REQUIRED AND POSITIONAL, for the reason it is on
    /// <see cref="ElapsedForTeamAsync"/>, <see cref="SumUsageForTeamAsync"/> and
    /// <see cref="WorkflowsForTeamAsync"/>: a floor a caller can forget is a floor that is
    /// absent wherever nobody remembered. Here it is the MEMBER'S OWN `floor_seq` rather than a
    /// team minimum - this reader is per-member, so it can afford the exact answer, and a member
    /// must not come back from a restart wearing a give-up its team was reset out of.
    /// </summary>
    Task<ContainerMarks> LiveMarksForAsync(
        ContainerId member, long sinceSeq, CancellationToken ct = default);

    Task<Message?> FindAsync(long seq, CancellationToken ct = default);

    /// <summary>
    /// The highest seq on the log, or 0 when it is empty.
    ///
    /// Exists so a NEW subscriber can start at the end rather than the beginning. Starting at zero
    /// would replay every message ever published at something that did not exist when they happened,
    /// so a worker created now would wake up owing a day of other people's work.
    /// </summary>
    Task<long> HighestSeqAsync(CancellationToken ct = default);

    /// <summary>Every message in one workflow, oldest first. What the UI groups by and a human follows.</summary>
    Task<IReadOnlyList<Message>> ReadCorrelationAsync(long correlationId, CancellationToken ct = default);

    /// <summary>
    /// One workflow's total spend for budget checking.
    ///
    /// A projection of payload fields via json_extract, over completed/failed messages filtered on
    /// correlation_id (no join). Returns TokensSpent = sum of (tokensIn + tokensOut + tokensTotal),
    /// one figure combining all three to account for brands that report a combined total (codex).
    /// Rows whose source is <see cref="UsageSource.ExcludedEstimate"/> are excluded.
    ///
    /// Unmeasured spend never convicts: if RunsWithoutUsage > 0 (indicating rows with no usage
    /// measurement), the refusal check must have an explicit arm to make it impossible to refuse
    /// on this figure when no usage was measured. Partial measurement bounds only the measured part.
    ///
    /// Returns TokensSpent = 0 if there are no completed/failed rows under this correlation.
    /// </summary>
    Task<WorkflowSpend> GetWorkflowSpendAsync(long correlationId, CancellationToken ct = default);

    /// <summary>
    /// The same figures, counted only since the last NUDGE on this workflow — which is what a
    /// budget is evaluated against, where <see cref="GetWorkflowSpendAsync"/> is what a screen
    /// shows.
    ///
    /// A NUDGE RESTARTS THE OTHER BOUND AND THIS MAKES SPEND MATCH IT. A nudge appends
    /// with the CORRELATION as its causation, and depth is the parent's depth plus one — so a nudge
    /// lands at depth 1 however deep the thread went. `CausationDepthLimit` therefore cannot stop a
    /// rescue. If spend did not reset too, the cap would refuse the rescue of the very workflow it
    /// had failed to bound.
    ///
    /// IT DOES NOT WEAKEN THE BOUND, because a runaway cannot nudge itself: the nudge route is
    /// `.HumansOnly()`. The bound stops UNATTENDED spending, and a person pressing nudge is
    /// attendance.
    ///
    /// THE WINDOW STARTS AT THE LAST INSTRUCTION WHOSE CAUSATION IS THE CORRELATION ITSELF, which is
    /// what a nudge is by construction, and at the root when there has been no nudge. A `tell` that
    /// names the root as its causation looks the same — the identical evasion surface depth already
    /// has, and `AGENTS.md` already records that both bounds are evadable by choosing a causation.
    /// </summary>
    Task<WorkflowSpend> GetSpendSinceNudgeAsync(long correlationId, CancellationToken ct = default);

    /// <summary>
    /// Every message after <paramref name="afterSeq"/>, oldest first, at most
    /// <paramref name="max"/>, REGARDLESS OF TYPE.
    ///
    /// The enumeration a purge needs. <see cref="ReadAfterAsync"/> cannot serve: it takes a type
    /// list, and an EMPTY type list matches NOTHING rather than everything - so counting with one
    /// proves only that the query returns nothing, before and after.
    ///
    /// Untyped and unfiltered by team on purpose. Which team a message belongs to is
    /// <see cref="MessageTeam.Of"/>'s source/type disjunction, and that rule must not be copied into
    /// SQL - the caller pages through this and decides in C#.
    /// </summary>
    Task<IReadOnlyList<Message>> ReadRangeAsync(
        long afterSeq, int max, CancellationToken ct = default);

    /// <summary>
    /// Removes these messages, EXCEPT any still cited by a message that is not going.
    ///
    /// The one mutation on an otherwise append-only log, and it exists for one caller: an opt-in,
    /// irreversible purge, for content that should be GONE rather than merely unreachable -
    /// <c>payload.output</c> carries a bounded excerpt of real agent output, which is a
    /// data-retention question rather than an aesthetic one.
    ///
    /// <c>causation_seq REFERENCES messages(seq)</c> with no ON DELETE clause, so deleting a cited
    /// row is an error. This never orphans one and NEVER NULLS one: nulling to make a delete succeed
    /// destroys the only field that answers what woke what, and "read causation_seq, never the
    /// clock" is a rule of this codebase.
    ///
    /// It deletes only rows nothing currently cites and REPEATS until a pass removes nothing, so a
    /// causation chain wholly inside the set goes whole while one pinned by a survivor is retained
    /// as far as the pin. Correct whether or not foreign keys are enforced on the connection - which
    /// matters here specifically, because this log's store sets no such pragma at all, so
    /// enforcement rests on the native library's compile default rather than on anything this
    /// codebase does.
    /// </summary>
    Task<PurgeReport> DeleteAsync(
        IReadOnlyCollection<long> seqs, CancellationToken ct = default);
}

/// <summary>
/// What a purge actually removed.
///
/// <paramref name="Retained"/> is NOT a failure: a row that a SURVIVING message cites cannot go
/// without destroying that message's causation, which is the one diagnostic that answers what woke
/// what. Reported so a caller can say "purged 2,847 of 2,903 - 56 retained" rather than claiming
/// everything went.
///
/// WHOSE messages those are is NOT KNOWN HERE and must not be guessed downstream. This method is
/// handed a set of sequence numbers and nothing else; "cited by a survivor" is the whole of what it
/// can report. "Cited by other teams" would be a guess, and a wrong one: the citers can be a
/// DELETED MEMBER of the same team, or the Concierge, on a tenant holding one team.
/// </summary>
public readonly record struct PurgeReport(int Purged, int Retained);

/// <summary>
/// Where each subscriber has read up to.
///
/// Held per subscriber, which is the reason more than one consumer is possible at all. A position
/// kept as the message's own status column works for exactly one consumer and has no answer for
/// "whose progress does it describe?" the moment there are two.
///
/// With the position held per subscriber: a slow container cannot block another, one that
/// was restarting catches up rather than silently missing what fired while it was down, and
/// retention stops being a correctness question — safe to purge becomes "behind every cursor"
/// rather than a global age.
/// </summary>
public interface ICursors
{
    /// <summary>The last seq this subscriber has been given. 0 when it has never read.</summary>
    Task<long> PositionAsync(ContainerId subscriber, CancellationToken ct = default);

    Task AdvanceAsync(ContainerId subscriber, long seq, CancellationToken ct = default);

    /// <summary>
    /// The lowest position across all known subscribers — everything at or below it has been
    /// delivered everywhere and is a retention decision rather than a correctness one. Null when
    /// there are no subscribers, which is NOT the same as zero: with nobody reading, nothing is safe
    /// to purge, and returning 0 would be indistinguishable from "a subscriber has read nothing".
    /// </summary>
    Task<long?> SlowestAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes this subscriber's cursor entirely, for a container that has been DELETED.
    /// </summary>
    /// <remarks>
    /// Not the same as advancing it, and the difference is what <see cref="SlowestAsync"/> makes of
    /// it: a cursor left behind for a container nobody will ever restore holds the slowest position
    /// at whatever it last read, forever, so retention can never move past it. A row outliving its
    /// subscriber is also how a recreated container inherits a predecessor's position and silently
    /// skips everything published in between.
    /// </remarks>
    Task ForgetAsync(ContainerId subscriber, CancellationToken ct = default);

    /// <summary>
    /// Removes EVERY cursor belonging to a team, whatever the container is called. Returns how many
    /// rows went.
    /// </summary>
    /// <remarks>
    /// BY TEAM, NOT BY ENUMERATED CONTAINER, and that is the whole point. <see cref="ForgetAsync"/>
    /// can only be called for a container something can still name, and a deletion sweep built on
    /// that misses every row whose container the registry cannot enumerate: one skipped by
    /// restoration, or one whose cleanup threw mid-loop. Those rows are
    /// exactly the ones a recreated team inherits, so the sweep must not depend on the enumeration.
    ///
    /// REQUIRED rather than defaulted, for the reason <c>ITeamStore.SetEnvAsync</c> is: a defaulted
    /// purge lets a store accept the call and drop it, which is this defect wearing an interface.
    /// </remarks>
    Task<int> ForgetTeamAsync(string team, CancellationToken ct = default);
}

/// <summary>Which message types each subscriber wants. Declared by a container in code, not authored
/// as a rule: there is no matching engine here, and deliberately so.</summary>
public interface ISubscriptions
{
    Task SetAsync(ContainerId subscriber, IReadOnlyCollection<string> types, CancellationToken ct = default);

    Task<IReadOnlyCollection<string>> ForAsync(ContainerId subscriber, CancellationToken ct = default);

    Task<IReadOnlyCollection<ContainerId>> SubscribersAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes EVERY subscription belonging to a team, whatever the container is called. Returns how
    /// many rows went. By team rather than by enumerated container - see
    /// <see cref="ICursors.ForgetTeamAsync"/> for why that distinction matters.
    /// </summary>
    Task<int> ClearTeamAsync(string team, CancellationToken ct = default);
}

public enum RunsWith
{
    /// <summary>Only runs that recorded the agent's own transcript.</summary>
    Transcript,

    /// <summary>Every finished run, transcript or not - including one that blocked every item of
    /// its batch, whose last item <c>blocked</c> row stands as its terminal.</summary>
    AnyRun,
}

/// <summary>
/// One finished run, as <see cref="IMessageLog.ReadRunsAsync(ContainerId, long, long, int, CancellationToken)"/> reads it: the terminal row
/// that closed it, when the run started (its `started` row, null when none is found), and whether
/// it published `blocked` between the two. A run that blocked every item has a `blocked` terminal
/// (<see cref="RunsWith.AnyRun"/> only), and is Blocked.
///
/// <paramref name="InLatestRun"/> is set on such a <c>blocked</c> terminal when no <c>started</c>
/// follows it. The log alone cannot tell that run from one still working whose own completed row
/// has not landed yet, so a caller that knows the member is running holds it back.
/// </summary>
public sealed record RunRow(Message Terminal, DateTimeOffset? StartedAt, bool Blocked, bool InLatestRun = false);
