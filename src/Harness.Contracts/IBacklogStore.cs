namespace Harness.Contracts;

/// <summary>
/// One backlog item. THE ITEM IS THE SPEC - <see cref="Body"/> is the markdown itself, and
/// Harness is the store of record for it. Rejected at design: holding a path to a file, and
/// holding a body copied out to disk at dispatch. One store of record means one copy, and a copy
/// cannot go stale.
/// </summary>
/// <param name="Id">
/// Minted by the platform and rendered to people as <c>B000H</c> (see <see cref="PlatformBacklogId"/>).
/// The whole point of this object is that a person can say "do B000H" to a Concierge and a Manager
/// can quote it back. Never reused - see
/// the <c>AUTOINCREMENT</c> note on the schema.
/// </param>
/// <param name="Team">
/// A VISIBILITY LINK AND NOTHING ELSE, nullable exactly as <c>skills.team_id</c> is: null means the
/// tenant's. It is NOT where the item runs - that is the dispatch - and it is NOT a confidentiality
/// boundary, because dispatching discloses the item to the team it ran on whatever this says.
/// </param>
/// <param name="Position">
/// Order over the WHOLE backlog. A reorder writes the midpoint between new neighbours, so it touches
/// ONE row rather than renumbering - which is what lets two people reorder concurrently without each
/// rewriting the rows the other touched.
/// </param>
/// <param name="ArchivedAt">
/// THE SECOND AXIS, independent of <see cref="State"/>. An item may be archived at any state, which
/// is why archiving is not a third state value: folding them would make "archived" and "implemented"
/// mutually exclusive.
/// </param>
/// <param name="CreatedBy">
/// DENORMALISED, for the reason <c>tenant_events.actor_email</c> is: the item answers questions about
/// work whose author may be gone.
/// </param>
public sealed record BacklogItem(
    long Id,
    string? Team,
    string Title,
    string Body,
    string State,
    double Position,
    string? ArchivedAt,
    string CreatedAt,
    string UpdatedAt,
    string CreatedBy)
{
    /// <summary>The outcome this item serves (<c>backlog-003</c>), or null. A dispatch links its
    /// workflow to it.</summary>
    public string? OutcomeId { get; init; }
}

/// <summary>
/// The four states an item can be in, as DATA rather than an enum - the same call
/// <c>ContainerDefinition</c>'s permits make, and for the same reason: an enum crossing two
/// serialisers as a name is a contract the SPA compares by string anyway.
///
/// <para>
/// THIS CLASS IS THE ONE STORE OF THE RULE. Every other statement of it - the PATCH refusal, the
/// dispatch gate at both doors, the CLI's <c>--state</c>, the seeded skills - is a copy that must
/// move in step, and the copies are written in terms of these constants for exactly that reason.
/// </para>
///
/// <para>
/// THE STORED STATE IS ONE AXIS AND IT IS NOT THE ONLY THING A READER SEES. Whether the work has
/// LANDED - a branch merged, pushed and unmerged, or never pushed at all - is DERIVED and never
/// stored, and it is spelled out beside this value on the wire rather than folded into it. Two
/// facts, two fields: what a person or a Manager SAID, and what the repository SHOWS.
/// </para>
/// </summary>
public static class BacklogStates
{
    /// <summary>
    /// THE DEFAULT, AND IT MEANS "NOT REVIEWED". An item arrives here from <c>backlog add</c>, from
    /// the Concierge writing one during a planning conversation, and from a person typing one in.
    /// It does NOT mean "reviewed, decided, ready to go" - that is a separate state, so dispatch can
    /// tell a spec from a line somebody typed.
    /// </summary>
    public const string Pending = "pending";

    /// <summary>
    /// A DELIBERATE HUMAN ACT - somebody has read the body and judges that a Manager could cut it
    /// into cards without follow-up questions. NOTHING INFERS IT: not a body over N characters, not
    /// an acceptance section, not an agent judging its own draft, because anything inferred makes
    /// the claim "a person read this" false while looking identical.
    ///
    /// <para>
    /// It is the ONLY state that may be dispatched, and it SURVIVES the dispatch: an item in flight
    /// is neither pending nor declared, and the dispatch record already carries the in-flight fact,
    /// so a state MEANING "in flight" would derive from the log what the log already says. That
    /// refusal still stands and <see cref="Declared"/> is not a breach of it - being in flight is
    /// still read off the dispatch, and `declared` says the opposite thing: the flight is OVER and
    /// somebody CLAIMED a delivery.
    /// </para>
    /// </summary>
    public const string Ready = "ready";

    /// <summary>
    /// A MANAGER SAID IT WAS DELIVERED, AND THAT IS ALL THIS SAYS. Written by
    /// <c>BacklogExecutionRecord.OnWorkflowCompletedAsync</c> when a workflow dispatched against
    /// this item is declared complete - a CLAIM, made by an agent, about a workflow.
    ///
    /// <para>
    /// IT EXISTS BECAUSE A DECLARATION IS NOT A LANDING. A Manager can truthfully declare a
    /// DUPLICATE dispatch of one item complete while other developers are still running on that
    /// item. "An agent says it is done" and "the work is in the product" are two different claims,
    /// and `implemented` is read as the second, so the first gets a state of its own.
    /// </para>
    ///
    /// <para>
    /// WHAT MOVES AN ITEM OUT OF HERE WHEN THE WORK NEVER LANDS: NOTHING AUTOMATIC. There is no
    /// sweeper, no timeout and no expiry. A PERSON reopens it - back to <see cref="Pending"/>
    /// through the Backlog screen, the PATCH route or <c>backlog edit</c> - or a person marks it
    /// <see cref="Implemented"/> themselves. Declared-and-never-landed is a real and permanent
    /// reading, and it is meant to be: an item that sat here for a month with nothing on main
    /// should say so on the screen forever rather than quietly tidy itself up.
    /// </para>
    ///
    /// <para>
    /// A SECOND DISPATCH DOES NOT RESET IT, and cannot arrive by accident: the dispatch gate
    /// (<c>RefuseUnlessReady</c>, asked at both doors) still admits ONLY <see cref="Ready"/>, so a
    /// declared item is not dispatchable until a person has moved it back themselves. Where an item
    /// does hold several dispatches, every derived reading takes the LATEST - the rule
    /// <c>BacklogInFlightState</c> already follows.
    /// </para>
    ///
    /// <para>
    /// WHAT IT READS AS WHILE A BRANCH IS PUSHED BUT NOT MERGED IS NOT THIS FIELD'S BUSINESS. That
    /// is the derived `landed` vocabulary, computed from the repository and never stored; this
    /// value stays `declared` throughout, and the two COMPOSE - "declared, pushed" and "declared,
    /// local" are different situations and a reader needs both words to tell them apart.
    /// </para>
    /// </summary>
    public const string Declared = "declared";

    /// <summary>
    /// THE WORK IS IN THE PRODUCT. A PERSON'S VALUE, and nothing else writes it: a
    /// declaration stops at <see cref="Declared"/>, and landing on main is what makes an item
    /// implemented.
    ///
    /// <para>
    /// It remains a fully legal stored value reachable from the PATCH route, the CLI and the
    /// Backlog screen, and an item a person set here BY HAND IS NEVER OVERWRITTEN by a later
    /// declaration - see <c>BacklogExecutionRecord.OnWorkflowCompletedAsync</c>, which treats it
    /// exactly as it treats <see cref="Declared"/>: already at or past where this would have put
    /// it, so not a write and not a failure.
    /// </para>
    /// </summary>
    public const string Implemented = "implemented";

    public static bool IsLegal(string? state) =>
        string.Equals(state, Pending, StringComparison.Ordinal)
        || string.Equals(state, Ready, StringComparison.Ordinal)
        || string.Equals(state, Declared, StringComparison.Ordinal)
        || string.Equals(state, Implemented, StringComparison.Ordinal);
}

/// <summary>
/// One dispatch of an item to a team. AN ITEM MAY BE DISPATCHED MORE THAN ONCE - a second team, or a
/// re-run after a team was deleted mid-spec - so an item has SEVERAL of these over its life and the
/// rollup is against the LATEST.
/// </summary>
/// <param name="TeamName">
/// DENORMALISED BECAUSE IT IS THE ONE THING THAT DIES WITH THE TEAM. Members, workflow seqs, token
/// totals and outcomes all stay derivable from <see cref="Correlation"/> after the team is gone,
/// because team deletion never touches the message log. <c>teams.name</c> is not on the log at all -
/// the log carries the team ID inside a container's source - so "deleted by (unknown)" is the
/// failure this column exists to avoid.
/// </param>
/// <param name="FrozenStats">
/// THERE IS A SECOND ERASER AND THIS IS THE ANSWER TO IT. <c>IMessageLog.DeleteAsync</c> purges rows
/// nothing cites, so retention takes the history quietly, long after anybody was watching. Stats are
/// LIVE while an item is in the backlog and are FROZEN here when it is archived - archiving being
/// already the moment a person says "keep this for the record". Restoring clears it and the item
/// goes back to deriving.
/// </param>
/// <param name="LandedAt">
/// LANDED, ONCE PROVEN, IS KEPT. Set the first time the work was proven reachable from
/// origin's default branch - by ancestry, or by the Git dialog's Merge to main - and never cleared
/// or moved afterwards, so a dispatch whose branch, clone and team are all gone still reads
/// <c>landed</c>. Null until then. A later rewrite of the default branch that drops the commit is
/// out of scope.
/// </param>
/// <param name="LandedSha">The commit proven on the default branch. With several repositories,
/// <c>Repo sha</c> pairs joined by <c>, </c>.</param>
/// <param name="LandedBranch">The default branch it was read on, as stored for the repository at
/// the time - never assumed. With several repositories, <c>Repo branch</c> pairs.</param>
public sealed record BacklogDispatch(
    long Id,
    long Item,
    string TeamId,
    string TeamName,
    long Correlation,
    string DispatchedAt,
    string DispatchedBy,
    string? FrozenAt,
    string? FrozenStats,
    string? LandedAt = null,
    string? LandedSha = null,
    string? LandedBranch = null);

/// <summary>
/// THE WORK'S TIP IN ONE REPOSITORY, as the team's publish last pushed it. One row per
/// dispatch and repository, replaced by the newer tip on every publish. It is what lets
/// <c>landed</c> be read after the team's clone is gone: the sha is checked against another clone
/// of the same repository.
/// </summary>
/// <param name="Repo">The folder name derived from the URL - never the URL, which is where a
/// credential lives.</param>
public sealed record BacklogDispatchTip(long Dispatch, string Repo, string Sha, string RecordedAt);

/// <summary>
/// WHERE A DISPATCH STARTED IN ONE REPOSITORY: origin's default branch, and the team branch when it
/// had one, as they stood when the dispatch was made. Recorded once. The dispatch's OWN work is what
/// is beyond both, and only that is ever stored as landed - a fresh team branch sitting on the
/// default branch, or a branch whose earlier work landed for an earlier item, proves nothing about
/// this one.
/// </summary>
/// <param name="Repo">The folder name derived from the URL, as on <see cref="BacklogDispatchTip"/>.</param>
public sealed record BacklogDispatchBase(long Dispatch, string Repo, string DefaultSha, string? TeamSha, string RecordedAt);

/// <summary>
/// A START THAT COULD NOT BE READ WHEN THE DISPATCH WAS MADE, in one repository: the clone was
/// missing, the default branch was not known, or the fetch failed or ran over its budget. It is
/// retried on each backlog read of the item and on the team's publish, and succeeds only while the
/// team branch is unchanged since the dispatch - which is what the two shas taken then, without a
/// fetch, are for. Once the team has committed the start can no longer be told apart from its work,
/// and <paramref name="StoppedAt"/> ends the retry for good.
/// </summary>
/// <param name="Repo">The folder name derived from the URL, as on <see cref="BacklogDispatchTip"/>.</param>
/// <param name="Reason">Why it is not recorded, as a clause a person reads: the last failure, or why
/// the retry stopped.</param>
/// <param name="TeamSha">The team branch's tip in the clone when the dispatch was made; null when it
/// had none.</param>
/// <param name="DefaultSha">origin's default branch as the clone last knew it then, unfetched; null
/// when it could not be read. A team branch that was absent then and is cut here since has no
/// commits of its own.</param>
public sealed record BacklogDispatchMissedStart(
    long Dispatch, string Repo, string Reason, string? TeamSha, string? DefaultSha, string? StoppedAt, string RecordedAt);

/// <summary>
/// The backlog's store. Its own module for the reason <see cref="BacklogItem"/>'s schema records.
/// </summary>
public interface IBacklogStore
{
    /// <summary>
    /// Every item, in <c>position</c> order, optionally narrowed.
    ///
    /// <para>
    /// <paramref name="teams"/> IS AN HONEST NARROWING FILTER, exactly as
    /// <c>/api/kanban/board</c>'s is - the model that solved this problem already. Null means no
    /// narrowing at all (a person); a set means items linked to one of those teams, plus
    /// unlinked ones only when <paramref name="includeUnlinked"/> says so. An item a caller may not
    /// see is ABSENT, never refused: a 404 and a 403 answer different questions and only one of them
    /// is safe to answer.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<BacklogItem>> ListAsync(
        IReadOnlySet<string>? teams,
        bool includeUnlinked,
        bool archived,
        CancellationToken ct = default);

    /// <summary>
    /// One page of the ARCHIVE, newest id first: archived items with an id below
    /// <paramref name="before"/> (or from the highest when it is null), narrowed exactly as
    /// <see cref="ListAsync"/> narrows, at most <paramref name="take"/>.
    ///
    /// <para>
    /// BY ID, NOT BY POSITION. Position is the backlog's order and is rewritten by every drag; an
    /// id is minted once and never reused, so it is the only key a cursor can stand on without a
    /// reorder moving rows across the seam. The next page's cursor is the last item's id.
    /// </para>
    ///
    /// <para>
    /// THE NARROWING IS APPLIED BEFORE THE CAP, so a machine principal's page is full of items it
    /// may see rather than a page of the tenant's archive with the invisible ones removed.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<BacklogItem>> ListArchivedAsync(
        IReadOnlySet<string>? teams,
        bool includeUnlinked,
        long? before,
        int take,
        CancellationToken ct = default);

    /// <summary>One item by id, or null. Visibility is the CALLER's to apply - this answers the row.</summary>
    Task<BacklogItem?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Appends an item at the end of the order. Answers the row as stored, including the id the
    /// platform minted.
    /// </summary>
    Task<BacklogItem> CreateAsync(
        string? team, string title, string body, string createdBy, CancellationToken ct = default);

    /// <summary>
    /// NARROW UPDATES, NEVER A WHOLE-ROW UPSERT. Reusing a record-shaped save is the change to
    /// refuse: that takes an entire item, so a caller writes every other column back as it read
    /// them - which is how a concurrent edit to the body is lost by somebody renaming the title.
    /// Null leaves a field alone.
    /// </summary>
    Task UpdateAsync(
        long id,
        string? title = null,
        string? body = null,
        string? state = null,
        CancellationToken ct = default);

    /// <summary>
    /// A PERSON'S EDIT, WITH ITS TENANT ROWS, IN ONE TRANSACTION: the fields (null leaves one alone,
    /// as <see cref="UpdateAsync"/>), the outcome when <paramref name="setOutcome"/> (null clears it),
    /// and every row in <paramref name="audit"/>. A row that cannot be written throws and nothing is
    /// changed.
    /// </summary>
    Task EditAsync(
        long id,
        string? title,
        string? body,
        string? state,
        bool setOutcome,
        string? outcomeId,
        IReadOnlyList<TriggerAudit> audit,
        CancellationToken ct = default);

    /// <summary>Repoints the visibility link. Null clears it, which only a person may do.</summary>
    Task SetTeamAsync(long id, string? team, CancellationToken ct = default);

    /// <summary>Sets or clears (null) the outcome the item serves.</summary>
    Task SetOutcomeAsync(long id, string? outcomeId, CancellationToken ct = default);

    /// <summary>Writes ONE row's position. See <see cref="BacklogItem.Position"/>.</summary>
    Task SetPositionAsync(long id, double position, CancellationToken ct = default);

    /// <summary>
    /// Renumbers every item to consecutive integers, in current order. The bounded escape from
    /// <see cref="SetPositionAsync"/>'s midpoint running out of double precision - roughly fifty
    /// insertions into one gap - whose symptom is two items that can no longer be told apart and
    /// stop being re-orderable.
    /// </summary>
    Task<IReadOnlyList<BacklogItem>> RenumberAsync(CancellationToken ct = default);

    /// <summary>Sets or clears <c>archived_at</c>. Available at any state.</summary>
    Task SetArchivedAsync(long id, string? archivedAt, CancellationToken ct = default);

    /// <summary>Permanent, and reachable only from the archive. Takes the dispatch records with it.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Every dispatch of one item, oldest first. The CURRENT one is the last.</summary>
    Task<IReadOnlyList<BacklogDispatch>> DispatchesAsync(long item, CancellationToken ct = default);

    /// <summary>
    /// THE CURRENT DISPATCH OF EVERY ITEM THAT HAS ONE - the last by id per item - in ONE read.
    ///
    /// <para>
    /// Exists for the backlog LIST, which has to say whether each item is in flight and lists
    /// everything. Asking <see cref="DispatchesAsync"/> per row would be one query per item on a
    /// screen that shows all of them; this is one query for the screen, and the caller narrows it to
    /// the rows it is showing. An item never dispatched is simply absent.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<BacklogDispatch>> LatestDispatchesAsync(CancellationToken ct = default);

    /// <summary>Records a dispatch. <paramref name="correlation"/> is the seq of the row that began it.</summary>
    Task<BacklogDispatch> AddDispatchAsync(
        long item,
        string teamId,
        string teamName,
        long correlation,
        string dispatchedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Freezes a dispatch's stats, or clears them when <paramref name="frozenAt"/> is null. See
    /// <see cref="BacklogDispatch.FrozenStats"/>.
    /// </summary>
    Task FreezeDispatchAsync(
        long dispatchId, string? frozenAt, string? frozenStats, CancellationToken ct = default);

    /// <summary>
    /// Which item, if any, a correlation was dispatched under. How <c>workflow.completed</c> finds
    /// the item to move to <c>declared</c>.
    /// </summary>
    Task<BacklogDispatch?> DispatchForCorrelationAsync(long correlation, CancellationToken ct = default);

    /// <summary>
    /// Records <paramref name="sha"/> as the dispatch's tip in <paramref name="repo"/>, replacing
    /// any earlier one. See <see cref="BacklogDispatchTip"/>.
    /// </summary>
    Task RecordTipAsync(long dispatchId, string repo, string sha, CancellationToken ct = default);

    /// <summary>The dispatch's recorded tips, one per repository, by repository name.</summary>
    Task<IReadOnlyList<BacklogDispatchTip>> TipsAsync(long dispatchId, CancellationToken ct = default);

    /// <summary>
    /// Records where the dispatch started in <paramref name="repo"/>, ONLY when nothing is recorded
    /// for it yet: the start does not move. See <see cref="BacklogDispatchBase"/>.
    /// </summary>
    Task RecordBaseAsync(
        long dispatchId, string repo, string defaultSha, string? teamSha, CancellationToken ct = default);

    /// <summary>The dispatch's recorded starting points, one per repository, by repository name.</summary>
    Task<IReadOnlyList<BacklogDispatchBase>> BasesAsync(long dispatchId, CancellationToken ct = default);

    /// <summary>
    /// Notes that the dispatch's start in <paramref name="repo"/> could not be read. The first call
    /// keeps the team branch and default branch as they stood; a later one only replaces the
    /// reason, and never reopens a stopped retry. See <see cref="BacklogDispatchMissedStart"/>.
    /// </summary>
    Task RecordMissedStartAsync(
        long dispatchId, string repo, string reason, string? teamSha, string? defaultSha, CancellationToken ct = default);

    /// <summary>Ends the retry of the dispatch's start in <paramref name="repo"/> for good, saying why.</summary>
    Task StopStartRetryAsync(long dispatchId, string repo, string reason, CancellationToken ct = default);

    /// <summary>The dispatch's starts that could not be read, one per repository, by repository name.</summary>
    Task<IReadOnlyList<BacklogDispatchMissedStart>> MissedStartsAsync(long dispatchId, CancellationToken ct = default);

    /// <summary>
    /// EVERY START STILL NOT RECORDED, across all dispatches, in one read: the missed starts whose
    /// repository has no recorded start since. Stopped ones included. For the backlog list, which says
    /// of every row whether its start was recorded.
    /// </summary>
    Task<IReadOnlyList<BacklogDispatchMissedStart>> UnrecordedStartsAsync(CancellationToken ct = default);

    /// <summary>Every dispatch with at least one recorded start, in one read.</summary>
    Task<IReadOnlySet<long>> DispatchesWithStartsAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores landed on the dispatch, ONLY when it has none yet: a stored landed is never
    /// downgraded, cleared or moved. Answers whether this call stored it.
    /// </summary>
    Task<bool> RecordLandedAsync(
        long dispatchId, string sha, string branch, CancellationToken ct = default);
}
