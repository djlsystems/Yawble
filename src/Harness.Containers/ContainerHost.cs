using System.Collections.Concurrent;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Containers;

/// <summary>
/// What <see cref="ContainerHost.ResumePendingAsync"/> did, in the only two shapes it comes in.
///
/// TWO numbers rather than one, because they are two different pieces of news for whoever reads the
/// line: <paramref name="Reoffered"/> is work that never started and is now queued again, and
/// <paramref name="Interrupted"/> is work that HAD started, cannot be re-run, and has been reported
/// as `container.failed` instead. Returning only the first made the second silent - a restart that
/// cut five runs short answered 0 and printed nothing.
/// </summary>
public readonly record struct ResumeReport(int Reoffered, int Interrupted);

/// <summary>
/// Owns the containers and feeds them from the log.
///
/// One pump for all containers rather than a loop each: there is a single process and a single
/// database, so a plain pass over the subscribers is a complete delivery mechanism. No broker, no
/// leases, and that is a fit rather than a compromise.
///
/// Each container has its own cursor, which is what lets a slow one fall behind without holding up
/// any other, and what lets one that was down catch up rather than missing what fired.
/// </summary>
public sealed class ContainerHost : IAsyncDisposable
{
    /// <summary>How many messages one container may be handed in a single pass. Bounded so a
    /// container far behind cannot monopolise a pass and starve the others.</summary>
    private const int BatchSize = 32;

    /// <summary>
    /// What an interrupted run says, in one place because it is said TWICE: once as the `output`
    /// on the `container.failed` row, and once as the mark on the member's own card. Two spellings
    /// of one fact is a feed and a card quietly disagreeing about the same run.
    /// </summary>
    public const string InterruptedByRestart =
        "Interrupted by a host restart; this run did not complete.";

    // ContainerId carries its own case-insensitive equality - see ContainerId.Equals - so this needs
    // no comparer. A container's identity IS the pair: it is what the addressed-instruction type is
    // built from and what the ledger filters on.
    private readonly ConcurrentDictionary<ContainerId, MemberRuntime> _containers = new();
    private readonly IMessageLog _log;
    private readonly ICursors _cursors;
    private readonly ISubscriptions _subscriptions;
    private readonly ITranscriptStore? _transcripts;
    private readonly IPendingDeliveries? _pending;
    private readonly ITriggerStore? _triggers;
    private readonly Func<ContainerId, CancellationToken, Task>? _onRegistered;

    /// <summary>
    /// HANDED TO EVERY CONTAINER THIS HOST CREATES, and called by this class itself for the one
    /// terminal row a container is not alive to publish - see
    /// <see cref="MemberRuntime"/>'s own field of the same name for what it is and why it runs
    /// BEFORE the row rather than after.
    ///
    /// <para>
    /// The two callers are the two writers of `agentContainer.completed`/`agentContainer.failed`,
    /// and there are exactly two: <c>MemberRuntime.RunOneAsync</c> for a run this process saw end,
    /// and <see cref="ResumePendingAsync"/> for one a RESTART cut short. A restart ends a run
    /// just as a spend limit and a kill do, so a seam wired only to the first would be absent in a
    /// third of the cases it exists for.
    /// </para>
    /// </summary>
    private readonly Func<ContainerId, long?, bool, CancellationToken, Task>? _onRunEnding;
    private readonly WipLedger? _wip;
    private readonly ConcurrentDictionary<string, byte> _pausedTeams =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What one workflow may spend before this host stops WAKING containers under it, or null for
    /// no bound.
    ///
    /// A BOUND CHECKED ONLY AT `tell` CANNOT SEE THE SPENDING. The `tell` route checks only
    /// when that tell carries a causation - so a Manager woken by its members' completions would
    /// spend without limit, and the cap would fire once, on the next dispatch, long after the money
    /// is gone, with the provider's own quota as the only real stop.
    ///
    /// A WAKE IS WHERE THE COST IS INCURRED, so a wake is where the question belongs. `tell` keeps
    /// its own check: refusing a dispatch before it is written is better than refusing the wake it
    /// would have caused, and the two answers agree because they read the same figure.
    ///
    /// OPTIONAL, LIKE EVERY OTHER SEAM HERE. Null is a host that does not bound spend - which is
    /// what every fixture wants, and what an operator gets by setting the limit to 0.
    /// </summary>
    private readonly long? _workflowSpendLimit;

    /// <summary>The instance figure READ AT EACH WAKE (`workflow.spendLimit` is settable at
    /// runtime). Null falls back to the constructor's figure, which is what fixtures pass.</summary>
    private readonly Func<long>? _workflowSpendLimitNow;

    /// <summary>The instance spend limit in force now; null is unlimited, as 0 is.</summary>
    private long? WorkflowSpendLimit =>
        _workflowSpendLimitNow is { } now ? (now() is > 0 and var limit ? limit : null) : _workflowSpendLimit;

    /// <summary>
    /// WHAT ACTUALLY BOUNDS ONE OF THIS TEAM'S WORKFLOWS, already resolved - null means UNLIMITED
    /// and 0 never comes back from it.
    ///
    /// <para>
    /// A DELEGATE RATHER THAN AN `ITeamStore`. This project must not take a dependency on where
    /// teams are persisted - `Program.cs` already owns that - and the question here is one number
    /// for one team id. It is also the seam that keeps every fixture working: null is a host that
    /// knows about no team settings at all, which is what a container test wants.
    /// </para>
    ///
    /// <para>
    /// <b>IN THE HOST IT IS ALWAYS SUPPLIED, SO <see cref="_workflowSpendLimit"/> BESIDE IT IS
    /// DEAD IN PRODUCTION.</b> That field stays as the no-delegate fallback because this project's
    /// own tests construct a host with no `TeamRegistry` anywhere in the picture. Two sources for
    /// one figure is how the two start disagreeing, so it is worth saying plainly which one
    /// decides: if the delegate is here, it is the only answer.
    /// </para>
    ///
    /// <para>
    /// ASKED PER WAKE, NEVER CACHED. A cache keyed on a container has produced three drift bugs in
    /// this codebase, each changing nothing until a restart - and a figure a person has just
    /// changed must take effect on the next wake, which is the moment they are watching for.
    /// </para>
    /// </summary>
    private readonly Func<string, CancellationToken, ValueTask<long?>>? _effectiveWorkflowBudget;

    /// <summary>A member's tree for one card, per repository - see
    /// <c>MemberRuntime</c>'s field of the same name. Null means no worktree variables.</summary>
    private readonly Func<ContainerId, string, IReadOnlyList<RepoWorktree>>? _worktrees;

    /// <summary>Handed to every container, for <see cref="ContainerSnapshot.Watchable"/>.</summary>
    private readonly Func<string, bool>? _watchable;


    /// <param name="transcripts">Handed to every container this host creates.</param>
    /// <param name="pending">Handed to every container this host creates. Null means a container
    /// that silently loses accepted work on a restart - see MemberRuntime's own doc comment.</param>
    /// <param name="triggers">Read on EVERY delivery, in <see cref="ResolveDeliveryAsync"/> - never
    /// cached on the container. Null means the pump cannot see event triggers at all, which is only
    /// correct for a fixture nothing drives them through: every message then reaches this container
    /// through the base set alone.</param>
    /// <param name="onRegistered">
    /// Called once <see cref="RegisterAsync"/> has added the container to <see cref="_containers"/>,
    /// on BOTH <see cref="AddAsync"/> and <see cref="RestoreAsync"/> - the seam that lets something
    /// outside this class populate <c>subscriptions</c> for it. This class stopped writing that
    /// store itself in "Subscriptions are derived from triggers, by one writer": <c>EffectiveSubscriptions</c>,
    /// in <c>Harness.Host</c>, is now the ONLY production caller of <c>ISubscriptions.SetAsync</c>,
    /// and <c>Harness.Containers</c> cannot reference it without an assembly cycle - so this
    /// parameter is the only door a <c>ContainerHost</c> BUILT OUTSIDE <c>Program.cs</c> has to that
    /// mechanism at all.
    ///
    /// OPTIONAL, and null is correct for <c>Program.cs</c>'s own singleton, NOT merely tolerated:
    /// wiring it there to <c>EffectiveSubscriptions.RecomputeAsync</c> would run it from INSIDE
    /// <c>AddAsync</c>, which is always called before <c>TeamRegistry.AddContainerAsync</c> writes
    /// the member's row - so it would recompute against a member `RecomputeAsync` cannot yet find,
    /// persisting an empty base set moments before the correct one. `TeamRegistry` already calls
    /// `RecomputeAsync` itself, AFTER that row lands - see `EffectiveSubscriptions`'s and
    /// `AddContainerAsync`'s own comments - so wiring it here too would not add a second production
    /// writer (still exactly one), only a redundant, wrongly-timed extra call every registration.
    ///
    /// Left null, a host built standalone - this project's own tests, which construct a
    /// <c>ContainerHost</c> with no <c>TeamRegistry</c> anywhere in the picture - registers
    /// containers that the persisted `subscriptions` store has never heard of, and
    /// <see cref="PumpOnceAsync"/> reads that store first and skips a container with nothing in it:
    /// silent, permanent non-delivery, which this parameter lets such a fixture avoid without a
    /// second write path in production. A fixture that
    /// means to exercise delivery MUST supply one.
    /// </param>
    public ContainerHost(
        IMessageLog log,
        ICursors cursors,
        ISubscriptions subscriptions,
        ITranscriptStore? transcripts = null,
        IPendingDeliveries? pending = null,
        ITriggerStore? triggers = null,
        Func<ContainerId, CancellationToken, Task>? onRegistered = null,
        long? workflowSpendLimit = null,
        Func<string, CancellationToken, ValueTask<long?>>? effectiveWorkflowBudget = null,
        Func<ContainerId, long?, bool, CancellationToken, Task>? onRunEnding = null,
        WipLedger? wip = null,
        Func<long>? workflowSpendLimitNow = null,
        Func<ContainerId, string, IReadOnlyList<RepoWorktree>>? worktrees = null,
        Func<string, bool>? watchable = null)
    {
        _watchable = watchable;
        _workflowSpendLimitNow = workflowSpendLimitNow;
        _worktrees = worktrees;
        _wip = wip;
        _onRunEnding = onRunEnding;
        _effectiveWorkflowBudget = effectiveWorkflowBudget;
        _log = log;
        _cursors = cursors;
        _subscriptions = subscriptions;
        _transcripts = transcripts;
        _pending = pending;
        _triggers = triggers;
        _onRegistered = onRegistered;

        // 0 IS UNLIMITED, the same answer as absent. A person who clears the setting and a person
        // who types 0 both mean "do not stop this", and making those differ would be a trap.
        _workflowSpendLimit = workflowSpendLimit is > 0 ? workflowSpendLimit : null;
    }

    /// <summary>Any container's snapshot changed. The host's one outbound signal, which the API
    /// forwards to the browser.</summary>
    public event Action<ContainerSnapshot>? Changed;

    public IReadOnlyCollection<ContainerSnapshot> Snapshots() =>
        [.. _containers.Values.Select(c => c.Snapshot())
            .OrderBy(s => s.Team, StringComparer.Ordinal)
            .ThenBy(s => s.Id, StringComparer.Ordinal)];

    public MemberRuntime? Find(ContainerId id) => _containers.GetValueOrDefault(id);

    /// <summary>
    /// Hands a LIVE container the effective subscription set <c>EffectiveSubscriptions.RecomputeAsync</c>
    /// just persisted, so the in-memory snapshot a browser is already looking at agrees with the
    /// database. See <see cref="MemberRuntime.Resubscribe"/> for why this is the fifth thing that
    /// changes a container after creation and for the set-comparison that keeps it from publishing
    /// on every recompute.
    ///
    /// A no-op when this host does not hold the container - a defensive read rather than a path this
    /// codebase expects to hit, since every caller of <c>RecomputeAsync</c> registers or restores the
    /// container first. On BOTH of those paths the container is already in <see cref="_containers"/>
    /// by the time this runs, so it is not merely defensive there: a restored container is registered
    /// carrying only its base set, and this is what folds in whatever triggers it already held before
    /// the restart, publishing exactly when that changed anything a snapshot carries.
    /// </summary>
    public void Resubscribe(ContainerId container, IReadOnlyCollection<string> types) =>
        _containers.GetValueOrDefault(container)?.Resubscribe(types);

    /// <summary>
    /// Registers a NEW container and records what it listens to.
    ///
    /// Its own addressed-instruction type is added automatically. A container that could not be told
    /// anything directly would be unreachable, and making that an opt-in someone can forget is a
    /// worse default than making it structural.
    /// </summary>
    public async Task<MemberRuntime> AddAsync(
        ContainerDefinition definition, IMemberRunner runner, CancellationToken ct = default) =>
        await RegisterAsync(definition, runner, await _log.HighestSeqAsync(ct), advanceCursor: true, ct);

    /// <summary>
    /// Brings back a container that already exists, at the floor it already had.
    ///
    /// The difference from <see cref="AddAsync"/> is the cursor: AddAsync advances the cursor to the head of the log, which is correct for
    /// something that did not exist a moment ago and destructive for something that did - it would
    /// discard every message published while this host was down. A restored container is the SAME
    /// container: its cursor is authoritative and its ledger floor is the one it was created with.
    ///
    /// Subscriptions are not written here at all - see the note in RegisterAsync. The
    /// restored container's effective set is recomputed and pushed to it separately, through
    /// <see cref="Resubscribe"/>, once this method returns.
    /// </summary>
    public Task<MemberRuntime> RestoreAsync(
        ContainerDefinition definition, IMemberRunner runner, long floorSeq,
        CancellationToken ct = default) =>
        RegisterAsync(definition, runner, floorSeq, advanceCursor: false, ct);

    private async Task<MemberRuntime> RegisterAsync(
        ContainerDefinition definition, IMemberRunner runner, long floorSeq, bool advanceCursor,
        CancellationToken ct)
    {
        // Refused, not overwritten. `_containers[id] = container` would silently replace the first
        // team's Manager with the second team's and leave both team member lists pointing at one object.
        // This check precedes the subscription and cursor writes so a refusal leaves no rows behind.
        if (_containers.ContainsKey(definition.Id))
        {
            throw new InvalidOperationException($"Container '{definition.Id}' already exists.");
        }

        // Kept for the SNAPSHOT this container reports - not written to the persisted
        // `subscriptions` store. `EffectiveSubscriptions` owns that write: it is the
        // ONLY caller of `ISubscriptions.SetAsync` in the solution, so that a trigger created after
        // this container exists can still change what wakes it. `TeamRegistry.AddContainerAsync` and
        // `RestoreAsync` call `EffectiveSubscriptions.RecomputeAsync` once this method returns, on
        // both paths - see `EffectiveSubscriptions`'s own doc comment for why one writer, recomputing
        // the whole set, is the point. `_onRegistered`, below, is the same allowance for a host that
        // has no `TeamRegistry` to make that call for it - see the constructor's doc comment.
        var types = definition.Subscribes
            .Append(MessageTypes.InstructionFor(definition.Id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // The container's FLOOR: everything at or below this belongs to whatever came before it.
        //
        // The same value serves two jobs that must agree. It is where the cursor starts, so
        // nothing old is DELIVERED - and it is where the ledger starts, so nothing old is
        // REMEMBERED. Without the second, a newly created container would inherit every
        // same-(team, name) predecessor's history into its context, and one team's manager could
        // quote another team's instruction.
        //
        // A NEW container starts at the end of the log, not the beginning. Starting at zero would
        // replay every message ever published at something that did not exist when they happened -
        // a worker created now must not wake up owing a day of other people's work.
        //
        // UNCONDITIONAL for a new container. A guard such as `if (PositionAsync(...) == 0)` is made
        // false by a surviving cursor row, so recreating a team would find its predecessor's row,
        // skip the floor entirely, and hand the new container everything published since, another
        // team's completions included.
        //
        // Safe to do every time AddAsync calls this because AdvanceAsync is
        // `MAX(position, excluded.position)`, so it cannot move a cursor backwards no matter who
        // calls it. Sweeping stale rows at store-construction time is NOT an alternative: no team
        // exists at that point, so every row would look orphaned.
        //
        // Because teams persist, a surviving cursor row can also belong to the SAME container coming
        // back rather than to a predecessor, and advancing it to the head would discard everything
        // published while this host was down. advanceCursor is how the two callers differ: AddAsync passes true because it always means "did not
        // exist a moment ago"; RestoreAsync passes false and hands its own floorSeq as-is, because the
        // cursor it already has is authoritative.
        if (advanceCursor) await _cursors.AdvanceAsync(definition.Id, floorSeq, ct);

        var container = new MemberRuntime(
            definition with { Subscribes = types }, runner, _log, _transcripts,
            pending: _pending, claimStart: TryClaimStart, sinceSeq: floorSeq,
            onRunEnding: _onRunEnding, claimSignal: ClaimSignal, claimWithdraw: WithdrawClaim, worktrees: _worktrees,
            watchable: _watchable);
        container.Changed += OnChanged;

        // The re-check that makes the ContainsKey guard above correct rather than merely fast:
        // _containers is a ConcurrentDictionary, so two concurrent creations of the same identity
        // could both pass ContainsKey. TryAdd is what actually enforces uniqueness.
        if (!_containers.TryAdd(definition.Id, container))
        {
            await container.DisposeAsync();
            throw new InvalidOperationException($"Container '{definition.Id}' already exists.");
        }

        // AFTER TryAdd, so a seam calling back into this host - Find, Resubscribe - sees the
        // container it was just handed rather than nothing. See the constructor's own doc comment
        // for why null is correct in Program.cs and what leaving this uncalled would mean for a
        // standalone host.
        if (_onRegistered is not null) await _onRegistered(definition.Id, ct);

        OnChanged(container.Snapshot());

        return container;
    }

    /// <summary>
    /// Hands every restored container what it had accepted and not finished. Returns BOTH counts,
    /// so a caller can say what happened either way.
    ///
    /// Called ONCE, after every container has been restored and before the pump starts. Its two
    /// halves answer different questions: work that never started is still owed and is re-offered
    /// through <see cref="MemberRuntime.OfferAsync"/> — the ceiling applies exactly as it would at
    /// any other moment, so a queue at capacity refuses a resumed delivery out loud rather than
    /// silently admitting it. Work that HAD started cannot be re-run, because nothing knows how far
    /// into it the agent got, so it is reported instead — as `container.failed`, not a new type, so
    /// a manager (already subscribed to Failed) learns its dispatched work was cut off and both
    /// renderers show it with no change.
    ///
    /// It returned only the first count, which made the second silent: a restart that reported five
    /// interrupted runs answered 0, so the caller printed nothing at all. Reporting an interruption
    /// and telling nobody it happened is the failure this whole path exists to prevent.
    /// </summary>
    /// <summary>
    /// Stops a container and forgets it. Returns false when this host never held it.
    /// </summary>
    /// <remarks>
    /// Removed from the dictionary FIRST, then disposed. The delivery pump walks
    /// <see cref="_containers"/>, so a container still listed while its queue is being torn down is
    /// one the pump can hand work to on the way out - and that work is accepted by something that
    /// will never run it.
    ///
    /// Disposal is what actually ends the child process and the consumer loop; the rows a deleted
    /// container leaves behind - its cursor, its subscriptions, its pending deliveries - belong to
    /// the caller, because this class knows nothing about teams or stores.
    /// </remarks>
    public async Task<bool> RemoveAsync(ContainerId id)
    {
        if (!_containers.TryRemove(id, out var container)) return false;

        container.Changed -= OnChanged;

        await container.DisposeAsync();

        return true;
    }

    public async Task<ResumeReport> ResumePendingAsync(CancellationToken ct = default)
    {
        if (_pending is null) return new ResumeReport(0, 0);

        var resumed = 0;
        var interrupted = 0;

        foreach (var container in _containers.Values)
        {
            var rows = await _pending.ForAsync(container.Id, ct);
            if (rows.Count == 0) continue;

            // The container's FLOOR, read once. A pending row is written when a message is OFFERED,
            // and nothing below the floor is ever delivered, so a row at or under it cannot belong
            // to this container - it belongs to a PREDECESSOR of the same (team, name).
            //
            // `pending_deliveries` is keyed on Team/Name with no foreign key, exactly as cursor rows
            // are, so recreating a deleted team's Manager otherwise inherits its predecessor's
            // interrupted deliveries and publishes a `container.failed` for work it never accepted -
            // the same resurrection RegisterAsync's own floor comment documents for cursors.
            var floor = container.Snapshot().SinceSeq;

            foreach (var row in rows)
            {
                // IGNORED, not removed. Clearing rows on the strength of a floor is a destructive
                // act taken on an inference, and this costs nothing to leave: it is skipped again on
                // the next start. Sweeping what a deleted team left behind belongs with the delete.
                if (row.Seq <= floor) continue;

                if (row.Started)
                {
                    // THE SAME LAST MOMENT, FOR THE RUN NOBODY WAS ALIVE TO END. This row is the
                    // terminal transition of a run the previous process was in the middle of, and
                    // its work is sitting in the team's clone exactly as a killed run's is, so a restart
                    // is treated like a spend limit or a kill. Above the append and
                    // guarded, for the reasons `MemberRuntime` states at its own call site.
                    if (_onRunEnding is not null)
                    {
                        try
                        {
                            // `succeeded: false`, AND IT IS THE HONEST ANSWER RATHER THAN A
                            // DEFAULT. The row immediately below this is an
                            // `agentContainer.failed` with `failureClass = interrupted`: this run
                            // did not finish its turn, the Host was restarted out from under it,
                            // and nothing here knows how far the agent got. The publish still
                            // happens for exactly that reason - a killed run's branch matters more,
                            // not less - and the resume offer correctly does not.
                            await _onRunEnding(container.Id, row.Seq, false, ct);
                        }
                        catch (Exception) when (!ct.IsCancellationRequested)
                        {
                        }
                    }

                    await _log.AppendAsync(
                        new NewMessage(
                            MessageTypes.Failed,
                            JsonSerializer.Serialize(new
                            {
                                exitCode = (int?)null,

                                // Still `output`: MessageText and the SPA's summarise both read
                                // this field, and a different name blanks both at once.
                                output = InterruptedByRestart,
                                outputLength = 0,
                                transcript = (string?)null,
                                launchError = (string?)null,
                                // UNKNOWN, not zero. This run was STARTED - that is why it is being
                                // failed rather than re-offered - so it genuinely spent tokens
                                // before the host went down, and nothing here knows how many.
                                // Recording zeros would be an estimate, in the one place the
                                // platform actually knows it has no idea.
                                tokensIn = (int?)null,
                                tokensOut = (int?)null,
                                tokensCachedIn = (int?)null,
                                tokensReasoning = (int?)null,
                                tokensSource = (string?)null,

                                // `interrupted`: one spelling for a run the Host was restarted out
                                // from under, shared with the failure classifier rather than
                                // spelt a second way. It never resumes
                                // automatically - nothing here knows how far the agent got, which
                                // is the same reason the unstarted/STARTED split below exists and
                                // is untouched.
                                failureClass = FailureClasses.Interrupted,
                                retryAfter = (string?)null,
                            }),
                            container.Id.ToString(),
                            row.Seq),
                        ct);

                    // THE CARD'S HALF OF THE SAME FACT, in the SAME SENTENCE the payload above
                    // carries. This is the second publisher of `container.failed`, and a mark set
                    // only at the other one leaves a restarted Host's interrupted members looking
                    // exactly like members that finished - which is the state this mark exists for.
                    container.MarkFailed(InterruptedByRestart, FailureClasses.Interrupted);

                    await _pending.RemoveAsync(container.Id, row.Seq, ct);
                    interrupted++;
                    continue;
                }

                var message = await _log.FindAsync(row.Seq, ct);

                // A row whose message is gone is not a reason to refuse to start. It cannot happen
                // through any path here — the log is append-only — so treat it as bookkeeping to
                // clear rather than as a fault to raise.
                if (message is null)
                {
                    await _pending.RemoveAsync(container.Id, row.Seq, ct);
                    continue;
                }

                if (await container.OfferAsync(message, ct))
                {
                    resumed++;
                }
                else
                {
                    // Refused at the ceiling. OfferAsync already published the container.rejected
                    // that IS the answer - exactly as it is for a live delivery - but unlike a live
                    // delivery this row was written before this start ever ran, and OfferAsync's
                    // ceiling branch returns before touching a row that already exists. Left here it
                    // would be re-offered and re-rejected on every subsequent start, appending a
                    // fresh container.rejected forever.
                    await _pending.RemoveAsync(container.Id, row.Seq, ct);
                }
            }

            // AFTER the re-offer, and only ever forwards: AdvanceAsync is MAX(...), so this closes
            // the gap between a pending row and a cursor that never got past it without ever
            // moving a cursor back. Unconditional even when every row above was skipped as
            // below-floor: a restored container's cursor is already >= its floor (RegisterAsync's
            // own invariant), so rows[^1].Seq is then <= the cursor too and MAX makes this a no-op
            // rather than a regression.
            await _cursors.AdvanceAsync(container.Id, rows[^1].Seq, ct);
        }

        return new ResumeReport(resumed, interrupted);
    }

    private void OnChanged(ContainerSnapshot snapshot)
    {
        try
        {
            Changed?.Invoke(snapshot);
        }
        catch
        {
        }
    }

    /// <summary>
    /// One delivery pass over every container. Returns how many messages were handed out, so a
    /// caller can idle when there was nothing rather than spinning.
    ///
    /// Separated from the loop so it can be driven deterministically by a test - the alternative,
    /// asserting against a background timer, is how a suite becomes load-sensitive.
    /// </summary>
    public async Task<int> PumpOnceAsync(CancellationToken ct = default)
    {
        var delivered = 0;

        foreach (var container in _containers.Values)
        {
            if (IsPaused(container.Id.Team)) continue;

            var types = await _subscriptions.ForAsync(container.Id, ct);
            if (types.Count == 0) continue;

            var position = await _cursors.PositionAsync(container.Id, ct);
            var messages = await _log.ReadAfterAsync(position, types, BatchSize, ct);

            foreach (var message in messages)
            {
                // TWO guards, answering different questions. Both are load-bearing and neither
                // implies the other.
                //
                // The first: a container never reacts to its own publications. Without it, a
                // container subscribing to a type it also emits feeds itself forever - and depth
                // would climb until the loop bound caught it, which is a guard doing a design's job.
                //
                // Source is now the qualified form, so this is an identity comparison rather than a
                // name one. It was `StringComparison.Ordinal` against a dictionary keyed
                // case-insensitively - `Manager` publishing would not suppress delivery to `manager`.
                //
                // The second: a container is woken only by its OWN TEAM. Completion types are
                // GLOBAL, so without this two teams' managers wake each other without end - Alpha
                // publishes `container.completed`, Beta is subscribed and the source differs so it
                // wakes, it publishes its own, which wakes Alpha. The intra-team loop is bounded on
                // hops by CausationDepthLimit (checked at tell route) and on cost by the workflow
                // spend limit - which is checked at tell AND HERE, at the wake. Checked at
                // tell only, a Manager woken by completions would spend without any bound at all,
                // with the provider stopping the team rather than this product. Both are evadable by omitting causation. Ceiling bounds
                // QUEUE depth rather than causation or cost.
                //
                // This is what makes ManagerSubscriptions able to name global types at all. Delete
                // it and that list reinstates the loop exactly.
                //
                // MessageTeam.Of, NEVER message.Source. A console's instruction carries
                // Source = "console" with no team in it at all, and its team lives in its TYPE.
                // Reading Source here would give every instruction a null team, drop it, and make
                // every manager silently unreachable - an absence, so no cross-team test would
                // notice.
                //
                // A null team therefore fails CLOSED. The only rows producing one carry an
                // unqualified container identity or a caller-supplied `from` with no team in it.
                var team = MessageTeam.Of(message);

                // What the cursor moves past for THIS message. Ordinarily message.Seq - see the
                // comment below the loop - but ResolveDeliveryAsync can append a second row (a
                // trigger firing), and the cursor has to clear that one too or the next pass reads
                // it back unguarded by either check above and delivers it a second time.
                var advanceTo = message.Seq;

                // ONE WAKE PER FINISHED RUN. A Manager holds both `handback` and `completed`. A
                // worker that handed back already woke it on that row, and the `completed` row the
                // platform appends when the same run ends would wake it a second time - two Manager
                // runs for one delivery, the second of which finds its own `workflow-complete`
                // refused because the first run's completion is still a pending delivery. The completion says whether its run handed back
                // (PayloadFields.HandedBack); a subscriber that ALSO holds `handback` is passed
                // over on it. One that holds only `completed` still wakes: it was never told.
                //
                // A QUIET RUN WAKES NOBODY ON ITS COMPLETION. A member polling on a schedule finishes
                // `quiet` when it found nothing; its `completed` row is still written and shown, but
                // every subscriber that would be woken by it is woken only because it is a
                // `completed` row, so all are passed over. A failure is never quiet, and what the run
                // published or handed back wakes on its own row. See PayloadFields.Quiet.
                var isCompleted = string.Equals(message.Type, MessageTypes.Completed, StringComparison.Ordinal);
                var alreadyWoken = isCompleted
                    && ((types.Contains(MessageTypes.Handback) && CompletionSays(message.Payload, PayloadFields.HandedBack))
                        || CompletionSays(message.Payload, PayloadFields.Quiet));

                if (!alreadyWoken
                    && !string.Equals(message.Source, container.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(team, container.Id.Team, StringComparison.OrdinalIgnoreCase))
                {
                    // THE THIRD CHECK, and it is per-TRIGGER where the two above are per-CONTAINER -
                    // placed AFTER both, so a container that reacts to nobody but itself or to
                    // another team never reaches a trigger's filter at all.
                    //
                    // A wake costs an agent invocation; a filter costs nothing. So the message this
                    // container is actually handed depends on WHY its type is in `types` at all:
                    //
                    // - The BASE set (`team_members.subscribes`) names it: delivered UNCONDITIONALLY,
                    //   without even reading a trigger. A subscription this container already had
                    //   must never be silently narrowed by an unrelated trigger that happens to name
                    //   the same type with a filter of its own - a Manager holding
                    //   `container.failed` in its base set stays woken by every member's failure
                    //   even after someone adds a trigger filtered to one of them.
                    // - ONLY an enabled event trigger names it: delivery is a match against ANY of
                    //   them - filterless or matching - and a match FIRES, publishing what the
                    //   member is TOLD (the trigger's own Instruction, verbatim - {event.*}
                    //   substitution is a later task) rather than handing over the raw event
                    //   envelope, the same shape TriggerSweep already uses to fire a schedule. Every
                    //   governing trigger refusing the message spends nothing: nothing is offered.
                    //
                    // Triggers are resolved through ITriggerStore on EVERY delivery, never cached on
                    // the container - a cache keyed on an Agent Container has already produced three
                    // drift bugs in this codebase, each changing nothing until restart. The base-set
                    // check needs no store read at all, so it is asked FIRST - faster for the common
                    // case as well as correct.
                    var resolution = await ResolveDeliveryAsync(container, message, ct);
                    var toDeliver = resolution.Message;

                    // THE BUDGET, ASKED WHERE THE COST IS INCURRED.
                    //
                    // Refused BEFORE the offer, so nothing is queued and nothing runs. The cursor
                    // still advances - `advanceTo` is set from `message.Seq` above, before any
                    // delivery decision - so a refused wake is passed over once rather than
                    // retried forever.
                    //
                    // A NUDGE RESTARTS THE WINDOW, which is what makes a rescue possible at all:
                    // `GetSpendSinceNudgeAsync` counts from the last instruction caused by the
                    // correlation itself. Without it the bound refuses the recovery of the very
                    // workflow it failed to bound.
                    if (toDeliver is not null && await OverBudgetAsync(toDeliver, container, ct))
                    {
                        toDeliver = null;
                    }

                    if (toDeliver is not null)
                    {
                        await container.OfferAsync(toDeliver, ct);
                        delivered++;
                        advanceTo = Math.Max(advanceTo, toDeliver.Seq);

                        // AFTER the offer, never before - the same ordering the pending-delivery
                        // rows already follow: recording a fire the member was never actually
                        // handed is invisible and wrong, where recording nothing for a fire that
                        // DID reach the member is a gap the dialog already renders honestly (null
                        // stays "never fired") and the next fire repairs. Only an EVENT trigger
                        // produces a FiredTrigger - a schedule's own outcome is TriggerSweep's job,
                        // recorded from its own clock-driven pass, not this one.
                        if (resolution.FiredTrigger is { } trigger && _triggers is not null)
                        {
                            // Best effort, the same shape MemberRuntime uses for a transcript
                            // write: the member has already been woken, so a failure to record
                            // this trigger's OWN outcome must not undo that - the worst case is
                            // the dialog keeps saying "never fired" for a trigger that plainly
                            // did, not that the wake is lost. Cancellation still propagates: a
                            // host going down is not a write failure to swallow.
                            try
                            {
                                // A FOLDER WATCH IS BOTH: woken by its own file.changed event, but
                                // its next_due_at is its next poll. Clearing it as below would stop
                                // the runner from ever polling the folder again, so only
                                // the fire itself is recorded.
                                if (string.Equals(trigger.Kind, nameof(TriggerKind.FolderChange), StringComparison.OrdinalIgnoreCase))
                                {
                                    await _triggers.RecordFireAsync(
                                        trigger.Id, DateTimeOffset.UtcNow, "fired", message.Seq, ct);
                                }
                                else
                                {
                                    await _triggers.RecordOutcomeAsync(
                                        trigger.Id,
                                        firedAt: DateTimeOffset.UtcNow,

                                        // MEANINGLESS for an event trigger - it is not scheduled, so
                                        // there is no next occurrence to name. null is honest, and
                                        // renderNextDue(null) already renders "not scheduled" rather
                                        // than a stale date from the trigger's creation.
                                        nextDueAt: null,
                                        outcome: "fired",

                                        // The WAKING message's own seq - the event that matched, not
                                        // the freshly appended instruction row `toDeliver` carries.
                                        // That is what a reader of the trigger's card wants: which
                                        // event caused this, not the synthetic row minted to phrase it.
                                        seq: message.Seq,

                                        // Unchanged: an event trigger cannot miss the way a schedule
                                        // can, there being no due time to fall behind.
                                        missed: trigger.MissedCount,
                                        ct);
                                }
                            }
                            catch (Exception) when (!ct.IsCancellationRequested)
                            {
                            }
                        }
                    }
                }

                // Advanced whether the offer was accepted, rejected, or never made because the third
                // check refused it. A rejection is terminal for that delivery - the rejected message
                // IS the answer - and a refusal is terminal for the same reason a filter is cheap:
                // there is nothing to retry, so retrying it would refuse the same work forever and
                // never move on.
                await _cursors.AdvanceAsync(container.Id, advanceTo, ct);
            }
        }

        return delivered;
    }

    public bool IsPaused(string team) => _pausedTeams.ContainsKey(team);

    /// <summary>Whether a `completed` row carries <paramref name="field"/> as true
    /// (<see cref="PayloadFields.HandedBack"/>, <see cref="PayloadFields.Quiet"/>). A payload that
    /// does not parse, or predates the field, answers false: the wake happens, which is the
    /// behaviour before the field existed.</summary>
    private static bool CompletionSays(string payload, string field)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public Task SetPausedAsync(string team, bool paused)
    {
        if (paused)
        {
            _pausedTeams[team] = 0;
        }
        else
        {
            _pausedTeams.TryRemove(team, out _);
        }

        // Wakes every waiting claim: a paused team's waiters withdraw from the queue, a resumed
        // team's rejoin it.
        PulsePauseChanged();

        return Task.CompletedTask;
    }

    /// <summary>
    /// WHETHER THIS CONTAINER MAY BEGIN A RUN, and the disposable it hands back is the seam a bound
    /// hooks into.
    ///
    /// <para>
    /// <b>THERE IS NO PER-TEAM WORKER CAP.</b> Resource control is instance-wide - see
    /// admission control (<c>WipLedger</c>). A cap that bounds ONE team is not a bound on this machine: five
    /// teams at six workers each is thirty.
    /// </para>
    ///
    /// <para>
    /// <b>THE SHAPE IS KEPT, DELIBERATELY.</b> This answers null for a paused team and returns an
    /// <see cref="IDisposable"/> the caller disposes when the run ends - so a bound has a place to
    /// stand, and PAUSE does not have to be rebuilt to get there. Deleting the seam and putting it
    /// back is how a gate acquires a hole; widening what it permits is one function.
    /// </para>
    ///
    /// <para>
    /// PAUSE IS THE ONLY REFUSAL, and it is not a resource bound - it is a person saying stop.
    /// </para>
    /// </summary>
    public IDisposable? TryClaimStart(ContainerId id)
    {
        if (IsPaused(id.Team))
        {
            // A PAUSED TEAM TAKES NO SLOT AND HOLDS NO PLACE. Leaving it at the head of the FIFO
            // queue would hold every other team behind a team a person has stopped.
            _wip?.Withdraw(id);
            return null;
        }

        // No ledger means unlimited, which is what every fixture that does not pass one gets.
        // A configured ledger is the instance-wide cap. Null from it means "held", and the
        // consumer waits rather than failing the run.
        return _wip is null ? ExemptSlot.Instance : _wip.TryEnter(id);
    }


    /// <summary>
    /// Completes when a refused claim might now succeed: the ledger released a slot, a waiter
    /// withdrew or the limit changed, or a team's pause changed. The claim loop reads this BEFORE
    /// asking <see cref="TryClaimStart"/> and waits on it after a refusal - it never polls.
    /// </summary>
    public Task ClaimSignal()
    {
        var paused = Volatile.Read(ref _pauseChanged).Task;
        return _wip is null ? paused : Task.WhenAny(paused, _wip.Changed);
    }

    private void WithdrawClaim(ContainerId id) => _wip?.Withdraw(id);

    private TaskCompletionSource _pauseChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void PulsePauseChanged() =>
        Interlocked.Exchange(
                ref _pauseChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();

    /// <summary>
    /// One held worker slot. DISPOSE IS IDEMPOTENT - the caller's `finally` is the only intended
    /// path, but a claim released twice must not free a slot somebody else is holding.
    /// </summary>
    /// <summary>
    /// A permission that frees nothing. Shared, because it holds no state: what it exists for is to
    /// keep "you may start" a single SHAPE at the call site, so a bound can be added without every
    /// caller changing - see <see cref="TryClaimStart"/> for why that seam is kept.
    /// </summary>
    private sealed class ExemptSlot : IDisposable
    {
        public static readonly ExemptSlot Instance = new();

        public void Dispose()
        {
        }
    }


    /// <summary>
    /// RE-EMITS THIS TEAM'S SNAPSHOTS UNCHANGED - the platform's one "something happened on this
    /// team, come and re-read" signal.
    ///
    /// <para>
    /// It carries no new information itself, and that is the point: the Console has no timer and
    /// refetches a team's projections only from inside its `containerChanged` handler, so a fact
    /// that changed OUTSIDE any container - a team pause, a workflow pause, a resume - reaches an
    /// already-open browser only if something pushes a frame. This is that push, and using it
    /// costs no new field on <see cref="ContainerSnapshot"/> and no second hub event.
    /// </para>
    ///
    /// <para>
    /// CALL IT ON THE STATE CHANGE, NEVER ON THE ATTEMPT. Every frame costs every browser on the
    /// team a refetch, so a caller that republishes on a repeated refusal rather than on the
    /// transition turns one person's runaway into everybody's traffic. See
    /// <see cref="PauseWorkflowAsync"/>, which sits inside its own at-most-once guard for exactly
    /// that reason.
    /// </para>
    /// </summary>
    public void RepublishTeam(string team)
    {
        foreach (var snapshot in Snapshots().Where(snapshot =>
                     string.Equals(snapshot.Team, team, StringComparison.OrdinalIgnoreCase)))
        {
            OnChanged(snapshot);
        }
    }

    /// <summary>
    /// What, if anything, <paramref name="container"/> is actually handed for <paramref name="message"/>
    /// - the third check, evaluated AFTER both guards in <see cref="PumpOnceAsync"/> and per-TRIGGER
    /// where those two are per-container. See the call site for the full reasoning; this method is
    /// the mechanism.
    ///
    /// <b>THE BASE SET DELIVERS UNCONDITIONALLY, and that is asked FIRST, before any trigger is
    /// read.</b> A subscription `team_members.subscribes` already holds must never be narrowed by
    /// an unrelated event trigger that happens to name the same type with a filter of its own - a
    /// Manager holds `container.failed` in its base set structurally, and adding a trigger filtered
    /// to one member's failures must not quietly stop it hearing every other member's. Losing a
    /// delivery it already had is the class of bug this codebase spends the most effort preventing;
    /// it is worse than the alternative (a member woken more often than a filter's wording alone
    /// suggests - visible, and arguable on its own terms). <see cref="MemberRuntime.HasBaseSubscription"/>
    /// answers this from memory, so the common case never reaches a store at all.
    ///
    /// Only once the base set says no does a trigger get to say yes: a type reaches a container's
    /// subscribed set only two ways - see `EffectiveSubscriptions` - the base set or an ENABLED
    /// event trigger naming it, so a type that is NOT in the base set is here only because of a
    /// trigger. Delivery is then a match against ANY governing trigger - filterless or matching -
    /// and a match FIRES rather than forwarding the raw event: it publishes what the member is TOLD
    /// (the trigger's own `Instruction`, verbatim - `{event.*}` substitution is a later task), the
    /// same shape `TriggerSweep` already uses to fire a schedule, so the member is told something
    /// rather than handed a payload to parse. Every governing trigger refusing the message spends
    /// nothing: nothing is offered.
    ///
    /// <see cref="_triggers"/> IS NULL for a fixture nothing drives an event trigger through - a
    /// message not in the base set then falls through to "deliver unchanged" too, so no bystander test has to learn about triggers to keep passing.
    ///
    /// Returns the trigger that actually fired alongside the message, when one did - PumpOnceAsync
    /// needs that identity to record the trigger's own outcome, and nothing else in this method's
    /// three "deliver unchanged" arms is a fire at all.
    /// </summary>
    private async Task<DeliveryResolution> ResolveDeliveryAsync(MemberRuntime container, Message message, CancellationToken ct)
    {
        if (container.HasBaseSubscription(message.Type)) return new DeliveryResolution(message, null);

        if (_triggers is null) return new DeliveryResolution(message, null);

        var governing = (await _triggers.ListForContainerAsync(container.Id.Team, container.Id.Name, ct))
            .Where(t => t.Enabled
                && string.Equals(t.EventType, message.Type, StringComparison.Ordinal))
            .ToArray();

        // Nothing claims this type and it is not in the base set either - not reachable today,
        // since the pump only sees types already in this container's subscribed set, and every one
        // of those comes from the base set or a trigger. Deliver rather than drop: failing open
        // here matches every other guard in this codebase that cannot tell "not configured" from
        // "briefly out of step with a row that just changed".
        if (governing.Length == 0) return new DeliveryResolution(message, null);

        foreach (var trigger in governing)
        {
            // Never throws: TriggerFilter.Matches already swallows a payload it cannot read (and
            // a null source too), because an exception here would stop every container's deliveries
            // rather than one trigger's.
            var filter = TriggerFilter.Parse(trigger.Filter);
            if (filter is not null && !filter.Matches(message.Payload, message.Source)) continue;

            // A FOLDER TRIGGER IS ALSO GOVERNED BY ITS FOLDER: every `file.changed` on the
            // team names this type, and only the rows about this trigger's folder are its to fire
            // on. See FolderWatchScope.Covers for why a poll's row is its own trigger's alone.
            if (string.Equals(trigger.Kind, nameof(TriggerKind.FolderChange), StringComparison.OrdinalIgnoreCase)
                && !FolderWatchScope.Covers(trigger, message.Payload, message.Source))
            {
                continue;
            }

            // FIRES. Sourced as a trigger rather than as the container itself - a container never
            // reacts to its own publications (the first guard above), and this message is meant to
            // reach it. Caused by the event that fired it, so it lands in the same workflow.
            //
            // `{event.<field>}` resolves here, through PromptTokens - the same one-pass, unknown-
            // stays-verbatim function every system prompt already uses, not a second scanner beside
            // it. `EventFieldsOf` is what keeps a payload field's own value from being rescanned for
            // tokens: it is looked up once by PromptTokens' MatchEvaluator and the result is written
            // straight into the replacement, which .NET's Regex.Replace never revisits.
            var instruction = PromptTokens.ResolveEventTokens(trigger.Instruction, EventFieldsOf(message));

            var appended = await _log.AppendAsync(
                new NewMessage(
                    MessageTypes.InstructionFor(container.Id),
                    JsonSerializer.Serialize(new { instruction }),
                    $"trigger:{trigger.Id}",
                    message.Seq),
                ct);

            return new DeliveryResolution(appended, trigger);
        }

        // Every governing trigger refused this message. Nothing is offered, and nothing is spent -
        // and nothing fired, so there is no outcome to record either.
        return new DeliveryResolution(null, null);
    }

    /// <summary>
    /// What <see cref="ResolveDeliveryAsync"/> resolved a message to, and - only when an EVENT
    /// trigger is what produced it - which trigger fired. The three "deliver unchanged" arms and
    /// the "every governing trigger refused" arm all carry a null <see cref="FiredTrigger"/>: a
    /// message reaching a container through its base subscription, or because no trigger governs
    /// it, or because every governing trigger's filter refused it, is not a trigger firing and must
    /// not be recorded as one.
    /// </summary>
    private readonly record struct DeliveryResolution(Message? Message, TriggerRow? FiredTrigger);

    private static readonly IReadOnlyDictionary<string, string> NoEventFields =
        new Dictionary<string, string>();

    /// <summary>
    /// `{event.&lt;field&gt;}`'s lookup source: the waking message's own payload, narrowed to the
    /// fields <see cref="EventCatalog"/> DECLARES for that type - plus the envelope's own `source`,
    /// which is resolved from <c>message.Source</c> rather than read out of the payload, because the
    /// catalog declares no payload copy of it.
    ///
    /// AN UNDECLARED FIELD IS AN UNKNOWN TOKEN, NOT AN ERROR. A field the payload happens to carry
    /// that the catalog never named for this type is simply never added here, so
    /// <see cref="PromptTokens.ResolveEventTokens"/> leaves that token exactly as written - the same
    /// "unknown stays verbatim" rule every other token family already follows. `EventCatalog.For`
    /// answering null (an event type nothing declared) produces the same empty result.
    ///
    /// NEVER THROWS. `TriggerFilter.Matches` already swallows a payload it cannot read for the
    /// identical reason: an exception here would stop every container's delivery rather than costing
    /// one trigger its substitution.
    /// </summary>
    private static IReadOnlyDictionary<string, string> EventFieldsOf(Message message)
    {
        var definition = EventCatalog.For(message.Type);
        if (definition is null || definition.Fields.Count == 0) return NoEventFields;

        try
        {
            using var payload = JsonDocument.Parse(message.Payload);
            if (payload.RootElement.ValueKind != JsonValueKind.Object) return NoEventFields;

            Dictionary<string, string>? fields = null;

            foreach (var field in definition.Fields)
            {
                // THE ENVELOPE FIELD - resolved from the message itself, never the payload, because
                // no publisher writes it there. Every other field below reads the payload.
                if (string.Equals(field.Name, PayloadFields.Source, StringComparison.Ordinal))
                {
                    fields ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    fields[field.Name] = message.Source;
                    continue;
                }

                if (!payload.RootElement.TryGetProperty(field.Name, out var value)) continue;
                if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;

                fields ??= new Dictionary<string, string>(StringComparer.Ordinal);
                fields[field.Name] = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => value.GetRawText(),
                };
            }

            return fields ?? NoEventFields;
        }
        catch (JsonException)
        {
            return NoEventFields;
        }
    }

    /// <summary>
    /// Whether this workflow has spent what it is allowed to, and the wake must not happen.
    ///
    /// UNMEASURED SPEND NEVER CONVICTS - the rule the `tell` check already follows, and the reason
    /// `RunsWithMeasuredUsage` is on the record at all. A workflow whose runs carry no usage (a
    /// preset with no `UsageFormat`, every row predating capture) is not a workflow that has spent
    /// nothing; it is one nobody measured, and a bound that refused on it would stop honest work
    /// wherever a brand happens to be silent about its tokens.
    ///
    /// A MESSAGE WITH NO CORRELATION IS NOT PART OF A WORKFLOW and cannot be over its budget.
    ///
    /// THE REFUSAL IS PUBLISHED, never silent. A wake that simply did not happen is indistinguishable
    /// from a team with nothing to do, and an overspend nobody can see is one only a provider's
    /// refusal would reveal.
    ///
    /// <para>
    /// <b>AND IT PAUSES THE WORKFLOW.</b> A `Rejected` row alone would be a stall wearing a
    /// refusal: nothing on the board would say PAUSED, the team would simply go quiet, and the way
    /// back would be knowing that `nudge` is the verb. The pause row is a fact a reader can see and a person can
    /// undo. Both rows are written - the rejection is the per-message receipt, the pause is the
    /// per-episode state - and the pause is written AT MOST ONCE per episode.
    /// </para>
    ///
    /// <para>
    /// <b>IT PAUSES ONE WORKFLOW AND NEVER THE TEAM.</b> `SetPausedAsync` beside it pauses a team,
    /// and a team with three open workflows must not lose all three because one spent its budget.
    /// Everything here is keyed on <c>message.CorrelationId</c>.
    /// </para>
    /// </summary>
    private async Task<bool> OverBudgetAsync(
        Message message, MemberRuntime container, CancellationToken ct)
    {
        if (message.CorrelationId <= 0) return false;

        // TWO FIGURES CAN BOUND THIS, AND THE RESOLUTION IS NOT HERE. The team may set its own
        // per-workflow budget, the instance carries `WorkflowSpendLimit`, and which one
        // applies - along with the fact that a stored 0 means unlimited and an unset team inherits
        // the instance figure - is decided in exactly one function,
        // `TeamRegistry.EffectiveWorkflowBudgetFor`, reached through the delegate. Repeating any
        // part of that rule here would be the second place that knows it.
        //
        // IT IS ONE-OF AND NOT THE LOWER OF TWO. A team's higher number WINS, because a field that
        // silently refuses what a person typed is a worse lie than a number they can change.
        //
        // THE FALLBACK IS FOR FIXTURES. In the Host the delegate is always wired, so
        // `_workflowSpendLimit` is only ever read by a test that built a host with no team
        // settings at all.
        var limit = _effectiveWorkflowBudget is { } resolve
            ? await resolve(container.Id.Team, ct)
            : WorkflowSpendLimit;

        // NULL IS UNLIMITED, and it is the only spelling of it that reaches here.
        //
        // WHAT THE BOUND IS FOR, WHICHEVER FIGURE IT CAME FROM: it stops UNATTENDED spending. The
        // figure is counted since the last NUDGE and the nudge route is `.HumansOnly()`, so a
        // runaway cannot clear its own counter and a person pressing nudge is attendance. Nothing
        // else covers it - a nudge lands at depth 1 however deep the thread went, so
        // `CausationDepthLimit` cannot bound the cost of anything that has been rescued (see
        // `IMessageLog.GetSpendSinceNudgeAsync`), and without this an intra-team loop has no cost
        // bound at all.
        if (limit is not { } bound) return false;

        var spend = await _log.GetSpendSinceNudgeAsync(message.CorrelationId, ct);

        if (spend.RunsWithMeasuredUsage <= 0) return false;

        if (spend.TokensSpent <= bound) return false;

        // WHICH FIGURE FIRED, because "you are over budget" and "the platform stopped you" are
        // different sentences to the person reading them, and they lead to different screens. The
        // team's own figure is the one it can change; the instance one has no screen at all.
        //
        // Asked of the delegate's OWN answer rather than re-resolved: if the resolver handed back
        // the instance figure, the team chose nothing and this is the backstop firing.
        //
        // A TEAM THAT TYPES EXACTLY THE INSTANCE FIGURE READS AS THE INSTANCE ONE, accepted
        // rather than fixed. The alternative is a second delegate whose only job is to say which
        // of two equal numbers a reader should be sent to change - and in that state both screens
        // show the same bound, so the sentence is true either way and only the suggested next
        // click is off by one screen.
        var fromTeam = _effectiveWorkflowBudget is not null && bound != WorkflowSpendLimit;

        var reason =
            $"this workflow has spent {spend.TokensSpent:N0} tokens, which is over "
            + (fromTeam
                ? $"{container.Id.Team}'s per-workflow budget of {bound:N0}."
                : $"this instance's workflow spend limit of {bound:N0}.")
            + " It is PAUSED until a person resumes it.";

        await _log.AppendAsync(
            new NewMessage(
                MessageTypes.Rejected,
                JsonSerializer.Serialize(new
                {
                    reason,
                    refused = message.Type,
                    refusedSeq = message.Seq,
                }),
                container.Id.ToString(),

                // Caused by the message that was refused, so the refusal lands IN the workflow it
                // is about - the same choice the ceiling rejection makes, and what lets the
                // Workflows dialog show a person why their team went quiet.
                message.Seq),
            ct);

        await PauseWorkflowAsync(message, container, reason, bound, spend, fromTeam, ct);

        return true;
    }

    /// <summary>
    /// Writes the `workflow.paused` row for this correlation, ONCE per pause episode, and tells
    /// every browser already holding this team to come and re-read.
    ///
    /// <para>
    /// <b>AT MOST ONCE, AND THE CHECK IS THE THREAD.</b> <see cref="OverBudgetAsync"/> runs once
    /// per delivery attempt per container, so an unguarded append would put two hundred identical
    /// rows in front of whoever opens the thread to find out what happened. The shape is the one
    /// `IdleWorkflowOffer` already uses for its own at-most-once marker: read the correlation, ask
    /// a predicate over it. <see cref="WorkflowPause.IsPaused"/> is that predicate and is the same
    /// one the projection and `IdleWorkflowOffer` ask, which is what stops two of the three from
    /// drifting.
    /// </para>
    ///
    /// <para>
    /// EPISODE, NOT WORKFLOW. A resumed workflow that spends its way over again pauses AGAIN -
    /// `IsPaused` is newest-pause-wins-unless-resumed-after, so the second episode writes its own
    /// row rather than being swallowed by the first.
    /// </para>
    ///
    /// <para>
    /// A CORRELATION-SCOPED READ ON A REFUSED WAKE, which is a query this path did not do before.
    /// It is bounded by the one thread and only ever runs on a delivery that has ALREADY been
    /// refused - so it costs nothing on the path everything else takes, and the alternative is the
    /// row it guards being written on every single refusal.
    /// </para>
    ///
    /// <para>
    /// BEST EFFORT IN THE SAME SENSE THE TRIGGER OUTCOME WRITE BESIDE IT IS. The wake has already
    /// been refused and the `Rejected` row already written; failing to ALSO record the pause must
    /// not turn a refusal into an exception out of the pump. Cancellation still propagates - a
    /// host going down is not a write failure to swallow.
    /// </para>
    /// </summary>
    private async Task PauseWorkflowAsync(
        Message message, MemberRuntime container, string reason, long bound,
        WorkflowSpend spend, bool fromTeam, CancellationToken ct)
    {
        try
        {
            var thread = await _log.ReadCorrelationAsync(message.CorrelationId, ct);

            if (WorkflowPause.IsPaused(thread)) return;

            await _log.AppendAsync(
                new NewMessage(
                    MessageTypes.WorkflowPaused,
                    JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        // SERVER-RESOLVED, from the container's own id and never from anything a
                        // caller supplied - the rule `MessageTeam.Of`'s payload-reading arm
                        // depends on.
                        [PayloadFields.Team] = container.Id.Team,
                        [PayloadFields.Reason] = reason,
                        [PayloadFields.Limit] = bound,
                        [PayloadFields.Spent] = spend.TokensSpent,
                        [PayloadFields.LimitSource] =
                            fromTeam ? LimitSources.Team : LimitSources.Instance,
                    }),
                    container.Id.ToString(),

                    // CAUSED BY THE CORRELATION, not by the refused message.
                    //
                    // This is a fact about the WORKFLOW rather than about one refused delivery -
                    // the `Rejected` row above already carries that, caused by the message it
                    // refused. Causing this one by the root keeps it at depth 1 and puts it beside
                    // the workflow's own other state rows.
                    //
                    // AND IT IS NOT AN ADDRESSED INSTRUCTION, so it does NOT move the spend
                    // window. `GetSpendSinceNudgeAsync` matches `causation_seq = correlation_id`
                    // AND `type LIKE 'agentContainer.instruction.%'`; this type fails the second
                    // clause. A pause row that reset the counter would release the workflow in the
                    // act of stopping it.
                    message.CorrelationId),
                ct);

            // TELL EVERY BROWSER ALREADY HOLDING THIS TEAM TO COME AND RE-READ.
            //
            // THE DATA WAS ALWAYS RIGHT ON THE WIRE; WHAT WAS MISSING WAS THE PUSH. The Console
            // has no timer - `web/src/stores/console.ts` says so in as many words - and refetches
            // a team's workflows only from inside its `containerChanged` handler. This path asks
            // `OverBudgetAsync` BEFORE the offer and then touches no container at all, so without
            // this line nothing moves, no frame is emitted, and a console that was already open
            // goes on rendering the pre-pause projection - no PAUSED, no Resume button - until
            // somebody reloads or switches teams.
            //
            // THE EXISTING SIGNAL, NOT A NEW ONE. `RepublishTeam` re-emits this team's snapshots
            // UNCHANGED through the `Changed` event the API already forwards; it is the same seam
            // `TeamRegistry.SetPausedAsync` uses for the TEAM pause. No new field on
            // `ContainerSnapshot`, no new hub event, no new payload - which is what lets a
            // per-workflow fact reach a per-container frame without riding on one.
            //
            // INSIDE THE at-most-once GUARD, and that placement is the point: `OverBudgetAsync`
            // runs on every delivery attempt, so a republish above the `IsPaused` early return
            // would push a frame to every browser on the team for every message a runaway goes on
            // producing - each one costing a console a refetch of a projection that has not
            // moved. The push belongs to the state CHANGE, exactly as the row does.
            //
            // AND IT IS SWALLOWED WITH THE APPEND, DELIBERATELY. Two reasons. The wake has already
            // been refused and the `Rejected` row already written, so a failure to ALSO announce
            // the pause must not become an exception out of the pump - the same best-effort rule
            // the trigger-outcome write follows. And ordering it after the append inside the same
            // `try` means a failed append never announces: there is then nothing for a browser to
            // come and read, and inviting it to re-read an unchanged projection would teach it the
            // opposite of the truth. `OnChanged` already swallows a subscriber's own exception, so
            // the only thing this can actually throw is the snapshot enumeration itself.
            RepublishTeam(container.Id.Team);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers.Values) await container.DisposeAsync();
        _containers.Clear();
    }
}
