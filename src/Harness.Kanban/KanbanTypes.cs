namespace Harness.Kanban;

/// <summary>
/// A card represents a workflow instruction to a member, derived from log events.
/// Card identity: stable id from (tenant, correlationId, member).
/// </summary>
/// <remarks>
/// NOT SEALED, so <see cref="KanbanCardDetail"/> can BE a card rather than carry one. The client
/// reads a card's fields off the detail directly (`KanbanCardDetail extends KanbanCard`), and the
/// alternative shapes both cost more than the seal is worth: nesting the card under a property
/// changes the wire contract for a field nobody asked to move, and restating all thirteen
/// properties is two records that have to be edited together forever.
/// </remarks>
public record KanbanCard(
    string Id,
    long WorkflowSeq,
    string Team,

    /// <summary>
    /// Who is on this card NOW, or null when nobody is.
    ///
    /// NULLABLE, AND THE `Body` PRECEDENT BELOW DOES NOT TRANSFER - that is the whole argument.
    /// `Body` is empty-never-null because a card whose instruction fits in its title genuinely has
    /// ONE state, so a null check would be bought for nothing. A member has genuinely TWO: never
    /// assigned, and assigned. A client that cannot tell them apart cannot render a Todo lane, which
    /// is the lane this whole feature exists to fill. Same rule, opposite answer, because the number
    /// of real states differs.
    ///
    /// IT IS THE CURRENT ASSIGNMENT AND NOT THE HISTORY. When a member is deleted every card naming
    /// it returns to null, and nothing is lost by that: the card's member answers "who is on this
    /// now" and the TRAIL answers "who did what". That split already exists here -
    /// `ContainerSnapshot.Blocked` clears at the next wake while the `blocked` row stays on the log
    /// forever - and `MemberDeletion` already takes the same side by keeping transcripts.
    /// </summary>
    string? Member,

    /// <summary>
    /// The backlog item this card is part of, or null for a card that belongs to no item.
    ///
    /// THE CARD NAMES THE ITEM AND THE ITEM NEVER HOLDS A LIST OF CARDS. A list is a fact that has
    /// to be REWRITTEN every time it grows, and the message log is append-only, so child-names-parent
    /// is the only shape that survives it. There is no migration back from a link recorded the wrong
    /// way round.
    /// </summary>
    long? Item,
    string Title,

    /// <summary>
    /// The rest of the instruction - what the title did not have room for.
    ///
    /// EMPTY, NEVER NULL. A card whose whole instruction fits in its title genuinely has no body,
    /// and making a client distinguish that from an absent field is a null check bought for
    /// nothing. It is also NOT a copy of the title: the two are read together, so a body that
    /// repeated the subject would render the same words twice.
    /// </summary>
    string Body,
    string Status,
    string LaneId,
    string Color,
    IReadOnlyList<ProgressItem> Progress,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool AwaitingManager,

    /// <summary>
    /// Whether the WORKFLOW this card belongs to is paused for its spend.
    ///
    /// <para>
    /// <b>A FLAG AND NOT A `Status` VALUE</b>, and that is a decision rather than a shortcut.
    /// `Status` is this card's own run outcome, on a loudness ladder that `container.started`
    /// resets - so folding pause into it would ERASE a member's `blocked` mark, and would need a
    /// new lane and a new colour. A flag beside
    /// <see cref="AwaitingManager"/> is the shape this board already has for "a fact about this
    /// card that is not its run outcome", and it needs no lane change at all.
    /// </para>
    ///
    /// <para>
    /// NOT NULLABLE. There are two states and no third: this card's workflow is paused or it is
    /// not. Every card in a paused workflow carries it, and every one of them clears together
    /// when a person resumes.
    /// </para>
    /// </summary>
    bool Paused = false,

    /// <summary>
    /// EVERY WORKFLOW THIS CARD WAS PLANNED, CLAIMED OR TOLD IN, oldest first - so its first entry is
    /// <see cref="WorkflowSeq"/>. Null only on a card built outside the projection.
    ///
    /// <para>
    /// <see cref="WorkflowSeq"/> is where the card was BORN and never moves, but a card
    /// stopped in workflow A and resumed with <c>tell --card</c> in workflow C is C's work too:
    /// declaring C must refuse while it is open, and must settle it (and its tree) once it is not.
    /// Ask <see cref="BelongsTo"/> rather than comparing <see cref="WorkflowSeq"/>.
    /// </para>
    /// </summary>
    IReadOnlyList<long>? Workflows = null,

    /// <summary>
    /// The OPEN workflow this card belongs to, and that workflow's latest row, or null when every
    /// workflow in <see cref="Workflows"/> has ended.
    ///
    /// <para>
    /// SET BY THE HOST AT THE FETCH, never by the projection: whether a workflow is open is the
    /// log's own predicate, which a pure walk of rows does not ask. It is here so a Concierge asked
    /// to resume a card continues the workflow the card belongs to - `LatestSeq` is the causation
    /// that joins it - instead of working that out from the log or rooting a new one.
    /// </para>
    /// </summary>
    CardWorkflow? OpenWorkflow = null,

    /// <summary>
    /// What the per-run tools check found on this card's runs, the strongest mark so far:
    /// <c>called</c> (a run called a tool the platform did not give it), <c>offered</c> (a run was
    /// offered one and called none), <c>notMeasured</c> (a run could not be checked),
    /// <c>notVerified</c> (a run's preset declares no allowed tools, so it was not verified), or null when
    /// every run checked clean or none has been checked. A FLAG, like <see cref="Paused"/>, not a status.
    /// </summary>
    string? ForeignTools = null)
{
    /// <summary>Whether this card is work of the workflow <paramref name="correlation"/>: born in it,
    /// or claimed or told in it since. See <see cref="Workflows"/>.</summary>
    public bool BelongsTo(long correlation) =>
        WorkflowSeq == correlation || (Workflows?.Contains(correlation) ?? false);
}

/// <summary>
/// An open workflow a card belongs to. <paramref name="Workflow"/> is its correlation id, the number
/// a person and `workflow_show` use; <paramref name="LatestSeq"/> is its newest row, what a `tell`
/// passes as causation to join it.
/// </summary>
public sealed record CardWorkflow(long Workflow, long LatestSeq);

public sealed record ProgressItem(DateTime At, string Text);

/// <summary>
/// One row of a card's activity trail: a message this card is made of, said in a sentence.
/// </summary>
/// <param name="Seq">The message's own seq - its identity on the log, and the trail's sort key.</param>
/// <param name="Type">What happened, verbatim. The panel renders it, so it is not prettified here.</param>
/// <param name="Source">Who published it: a container's qualified id, a user id, `console`.</param>
/// <param name="OccurredAt">
/// A <see cref="DateTimeOffset"/> rather than the naked <see cref="DateTime"/> the card's own
/// timestamps use, because this one is READ AGAINST A WALL CLOCK. A UTC instant serialised with no
/// offset is parsed as local time by every browser, so a trail row would be wrong by the viewer's
/// offset - visible only to someone who knows when the thing actually happened.
/// </param>
/// <param name="Text">
/// What the row SAYS - the note, the comment, the progress line, the output. Never null: a row
/// whose message carries no prose is an empty string, and the type is what tells the reader what it
/// was. This is the whole point of the trail, so a summary that drops the text a person typed is
/// the failure this feature exists to remove.
/// </param>
public sealed record KanbanTrailEntry(
    long Seq,
    string Type,
    string Source,
    DateTimeOffset OccurredAt,
    string Text);

/// <summary>
/// A card WITH its trail: what one card's own messages said, oldest-first.
///
/// SEPARATE FROM <see cref="KanbanCard"/> BECAUSE THE BOARD MUST NOT CARRY IT. A trail grows with
/// the log, and a board fetch returns every card of every team the caller reaches - so folding the
/// trail into the card would make the cheapest, most frequent read carry history nothing on that
/// screen renders. One card is fetched when somebody opens it, which is where the cost belongs.
/// </summary>
public sealed record KanbanCardDetail : KanbanCard
{
    /// <param name="card">The projected card, copied whole - see the base record's copy constructor.</param>
    /// <param name="trail">This card's messages, oldest-first.</param>
    public KanbanCardDetail(KanbanCard card, IReadOnlyList<KanbanTrailEntry> trail)
        : base(card)
        => Trail = trail;

    /// <summary>
    /// This card's messages, OLDEST-FIRST. A trail is a story: the tell that began the workflow,
    /// then what happened to it. Newest-first is right for a feed you glance at and wrong for this.
    ///
    /// EMPTY, NEVER NULL. A card always has at least the instruction that made it, so an empty
    /// trail is not reachable through the board today - but a client that has to tell an absent
    /// list from an empty one is being asked a question with one answer.
    /// </summary>
    public IReadOnlyList<KanbanTrailEntry> Trail { get; init; }
}

/// <param name="WipLimit">
/// The lane's WIP limit, or null for none. The lane that holds RUNNING work shows
/// `wip.maxRunning` - the instance-wide admission limit, enforced at process start; every other
/// lane's comes from `kanban.wipLimits` and is advisory: a person moves cards there, so nothing is
/// blocked and the header only turns amber over it. Set by the Host at the fetch;
/// <see cref="KanbanLanes.All"/> never carries one.
/// </param>
public sealed record Lane(string Id, string Title, int? WipLimit = null);

/// <summary>
/// The board state: lanes, cards, and filter.
/// </summary>
public sealed record KanbanBoard(
    IReadOnlyList<Lane> Lanes,
    IReadOnlyList<KanbanCard> Cards,
    KanbanFilter Filters);

/// <summary>
/// Query filters for the board: TEAM, MEMBER, STATUS, and deliberately nothing else.
///
/// <para>
/// No <c>From</c>, <c>To</c> or <c>Workflow</c>: three more controls on a bar that is
/// read on a phone, and nobody reaches for them.
/// </para>
///
/// <para>
/// <c>Workflow</c> IS THE ONE WORTH PAUSING OVER. A correlation id is how this product addresses a
/// thread everywhere else - `nudge`, `stop`, `wait`, the feed - so leaving it off one surface is a
/// real asymmetry rather than tidying. The judgement is that nobody arrives at a BOARD holding one.
/// If that is wrong it comes as its own item with a reason, and adding it here is the small part: the flag, the route parameter and the
/// seeded skill text are what have to come with it.
/// </para>
///
/// <para>
/// Searching a card's CONTENTS serves instead, as free text over the fetched board rather than a
/// filter on this record - which is why there is no `Text` field here. A client-side narrowing that
/// never reaches the server does not belong in the record the server is sent, or somebody sends it.
/// </para>
/// </summary>
public sealed record KanbanFilter(
    string? Team = null,
    string? Member = null,
    string? Status = null);