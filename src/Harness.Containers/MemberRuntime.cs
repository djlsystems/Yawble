using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Contracts;

namespace Harness.Containers;

/// <summary>
/// One member, and everything around it: the MEMBER RUNTIME, formerly `AgentContainer`.
///
/// The runtime is the long-lived thing; what it runs - an agent CLI, a plugin executable - is a
/// process that runs and exits, reached through <see cref="IMemberRunner"/>. That distinction is the
/// whole design: a headless process CANNOT hold a subscription, so the runtime holds it and
/// enqueues an invocation when a subscribed message arrives. Nothing here knows what kind of member
/// it is hosting: turning work into a prompt, reading history, and the agent's own failure words
/// are the agent runner's. Nothing blocks — a manager
/// is not running while a worker works, it is woken afterwards.
///
/// Exactly one invocation runs at a time. Three workers finishing together produce three wakes, and
/// the queue is what stops three copies of the same agent reasoning about the same state
/// concurrently.
///
/// Accepted deliveries are recorded through <see cref="IPendingDeliveries"/>, not held only in the
/// in-memory queue: <see cref="ContainerHost.ResumePendingAsync"/> reads those rows back on the next
/// start, re-offers whatever never began running, and reports whatever WAS running when the process
/// stopped as interrupted rather than silently dropping it. That holds only when the seam is
/// actually wired - <c>pending</c> below is an optional constructor parameter, same as context and
/// transcripts, and null means a container that goes back to silently losing accepted work.
/// </summary>
public sealed class MemberRuntime : IAsyncDisposable
{
    public const int DefaultCeiling = 16;

    private ContainerDefinition _definition;

    /// <summary>
    /// The BASE subscription set - `team_members.subscribes` plus this container's own address,
    /// exactly what <see cref="ContainerHost"/>'s <c>RegisterAsync</c> hands the constructor as
    /// <c>definition.Subscribes</c> - captured ONCE here and never touched again.
    ///
    /// Deliberately a SEPARATE field from <see cref="_definition"/>'s own `Subscribes`, which
    /// <see cref="Resubscribe"/> overwrites with the EFFECTIVE set (base union enabled event
    /// triggers) on every trigger edit. The base set cannot be edited after creation - see
    /// `EffectiveSubscriptions`'s own doc comment - so a field that could drift with the effective
    /// set would stop meaning "the base set" the moment a trigger touched this container.
    ///
    /// What lets <see cref="HasBaseSubscription"/> answer the delivery pump's question - "would
    /// this type have reached this container even with no triggers at all" - without a store read:
    /// a subscription this container already had must never be silently narrowed by an unrelated
    /// trigger naming the same type with a filter of its own.
    /// </summary>
    private readonly IReadOnlySet<string> _baseSubscribes;

    private readonly IMemberRunner _runner;
    private readonly IMessageLog _log;
    private readonly ITranscriptStore? _transcripts;
    private readonly IPendingDeliveries? _pending;
    /// <summary>
    /// CLAIM A SLOT, or be told to wait - the hold that makes a team's cap real.
    ///
    /// A CLAIM RATHER THAN A PREDICATE. This was `Func&lt;ContainerId, bool&gt;`, asked and then
    /// forgotten, and between the yes and this container reaching
    /// <see cref="ContainerState.Running"/> lie the whole batch-growth loop's tens of milliseconds -
    /// during which every other container the same wake woke read the same zero and started too.
    /// The returned claim is disposed in a `finally`, so a run that throws, is cancelled, times out
    /// or is killed cannot leak the slot it took.
    ///
    /// Null is a container nothing bounds, which is what a fixture with no host-level cap wants.
    /// </summary>
    private readonly Func<ContainerId, long, IDisposable?>? _claimStart;

    /// <summary>
    /// WHAT A REFUSED CLAIM WAITS ON: a task that completes when a claim might now succeed -
    /// a slot released, the limit changed, a team resumed. Read BEFORE the claim is asked, so a
    /// release landing between the refusal and the wait is not lost. Null (fixtures that pass only a
    /// claim) falls back to a short delay.
    /// </summary>
    private readonly Func<Task>? _claimSignal;

    /// <summary>Takes this container out of the waiting queue when it stops mid-claim, so a waiter
    /// that will never ask again cannot hold the head of the queue.</summary>
    private readonly Action<ContainerId>? _claimWithdraw;

    /// <summary>True while a message has been taken and its claim is waiting. See
    /// <see cref="ContainerSnapshot.Held"/>.</summary>
    private volatile bool _held;
    private readonly ArtifactLimits _limits;
    /// <summary>Not readonly: <see cref="Refloor"/> moves it. It has exactly two
    /// readers - <see cref="Snapshot"/> and the context build - and both are load-bearing.</summary>
    private long _sinceSeq;
    private readonly Channel<Message> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _consumer;

    /// <summary>
    /// The invocation in flight, or null between runs. Linked to <see cref="_shutdown"/> so the Host
    /// going down still stops it, but SEPARATE so that stopping one run does not end this container:
    /// cancelling `_shutdown` would take the consumer loop with it and the member would never wake
    /// again.
    /// </summary>
    private CancellationTokenSource? _running;

    /// <summary>Whether the run in flight was stopped BY A PERSON, rather than by a clock or by the
    /// Host going down. The three are reported in different words because they are fixed in
    /// different places.</summary>
    private volatile bool _stoppedByHand;

    private int _queueDepth;
    private volatile ContainerState _state = ContainerState.Idle;
    private long? _currentCorrelation;

    /// <summary>
    /// The seq of the message being run, or null when idle.
    ///
    /// Beside _currentCorrelation rather than derived from it: a progress report is CAUSED by the
    /// instruction being worked on, and correlation is inherited from causation at append time, so
    /// passing the seq gets both right with one value. Passing the correlation instead would put
    /// every status line at the head of its own workflow.
    /// </summary>
    private long? _currentSeq;

    /// <summary>
    /// Why this container stopped without finishing, or null. See ContainerSnapshot.Blocked for why
    /// this lives here rather than being derived from the log at read time.
    /// </summary>
    private volatile string? _blocked;

    /// <summary>
    /// Why the PLATFORM did not complete the last run, or null. Beside _blocked rather than folded
    /// into it: that one is the AGENT saying it gave up, and this is the platform saying the run
    /// did not finish. See ContainerSnapshot.Failed, which documents the whole pair.
    /// </summary>
    private volatile string? _failed;

    /// <summary>
    /// WHAT KIND of failure <see cref="_failed"/> was - one of `FailureClasses` - or null.
    ///
    /// BESIDE `_failed` AND NOT INSIDE IT, and cleared on the same line at the next wake. It is a
    /// second value on ONE mark rather than a second mark: a card wearing a class from one run
    /// beside a reason from another would be worse than a card with no class at all.
    /// </summary>
    private volatile string? _failureClass;

    /// <summary>
    /// WHEN THE PLATFORM WILL RESUME THIS MEMBER'S WORKFLOW BY ITSELF, as an ISO-8601 instant, or
    /// null when it will not.
    ///
    /// WRITTEN BY THE SWEEP THAT WOULD FIRE IT rather than by the failing run - see
    /// `ContainerSnapshot.ResumeAt`. The run knows only what the provider said; whether a resume is
    /// still bounded in, still wanted and still this member's last word is a question only the
    /// sweep can answer, and a card that promised one the bound had already spent would be worse
    /// than a card that promised nothing.
    /// </summary>
    private volatile string? _resumeAt;
    private volatile string? _needsDecision;

    /// <summary>
    /// WHAT THIS MEMBER LAST HANDED BACK, or null. Beside the three marks above and governed by the
    /// opposite rule: read <see cref="ContainerSnapshot.HandedBack"/> before touching it. It is NOT
    /// cleared in <see cref="RunOneAsync"/> with `_blocked`, `_failed` and `_needsDecision` - a
    /// delivery does not go stale when this member is given something else to do, and clearing it
    /// there would erase it most reliably in the case it exists for, since a hand-back's purpose is
    /// to wake somebody who then sends more work.
    /// </summary>
    private volatile string? _handedBack;

    /// <summary>
    /// Whether THIS run has handed back, as distinct from <see cref="_handedBack"/>, which is
    /// never cleared. Reset at the start of every run and written onto the run's `completed` row
    /// as <see cref="PayloadFields.HandedBack"/>, so the pump can tell a completion that already
    /// woke the manager from one that has not.
    /// </summary>
    private volatile bool _handedBackThisRun;

    private readonly object _batchGate = new();
    private IReadOnlyList<Message>? _runningBatch;
    private HashSet<long>? _blockedItems;

    /// <summary>The items of the running batch the agent DEFERRED, by seq, with the reason it gave.
    /// Under <see cref="_batchGate"/> with <see cref="_blockedItems"/>.</summary>
    private Dictionary<long, string>? _deferredItems;

    /// <summary>
    /// DEFERRED ITEMS WAITING TO BE DELIVERED AGAIN, each as its own next run, before anything else
    /// is taken. Touched only by the consumer loop: a run's end puts them here and the next take
    /// reads them, on the same task.
    /// </summary>
    private readonly Queue<Message> _redeliveries = new();

    /// <summary>
    /// Which queued seqs are deferred items and the run each was deferred from. A message named here
    /// runs ALONE - it is never grown into a batch nor added to one - and carries the note. Written by
    /// a run's end and by <see cref="Redeliver"/> on a restart, read by the consumer loop.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _redeliverFrom = new();

    /// <summary>
    /// MESSAGES READ FROM THE CHANNEL THAT BELONG TO ANOTHER WORKFLOW than the batch being built.
    ///
    /// A `Channel` reader cannot peek, so the only way to ask "is the next message part of this
    /// thread" is to take it - and a message taken for the wrong thread has to be held rather than
    /// written back, because writing it back would put it BEHIND everything else and reorder one
    /// workflow's own messages against each other.
    ///
    /// TOUCHED ONLY BY THE CONSUMER LOOP, which is single-threaded by construction: `ConsumeAsync`
    /// is one task and nothing else reads or writes this. No lock, deliberately - a lock here would
    /// imply a second writer that does not exist and would invite one.
    ///
    /// A held message is STILL QUEUED in every sense that matters: `_queueDepth` still counts it and
    /// its `pending_deliveries` row still exists and is still un-Started - so a HOST PROCESS THAT
    /// STOPS with something held RE-OFFERS it on the next start rather than reporting it
    /// interrupted, because nothing in memory ever got the chance to run it.
    ///
    /// THAT DOES NOT HOLD FOR THIS CONTAINER'S OWN GRACEFUL SHUTDOWN, ONLY FOR THE PROCESS DYING.
    /// `ConsumeAsync`'s outer loop checks `_deferred.Count > 0` BEFORE `_shutdown.Token` (see its own
    /// comment - deliberate, so a container holding work does not wait forever on a channel that has
    /// nothing new coming). So if `_shutdown` fires while this container is mid-run and messages are
    /// already held, the consumer loop is still alive to take one as the next batch once the current
    /// run ends: it gets marked Started and run against an already-cancelled token, which reports it
    /// interrupted rather than leaving it un-started for a restart to re-offer. Left as it is: the
    /// guard above is deliberate and changing it is out of scope here.
    /// </summary>
    private readonly Queue<Message> _deferred = new();

    /// <summary>
    /// RUN ONE LAST THING BEFORE THIS RUN'S TERMINAL ROW, WHATEVER ENDED IT - see
    /// <see cref="RunOneAsync"/>, where it is awaited immediately above the
    /// `agentContainer.completed`/`agentContainer.failed` append.
    ///
    /// <para>
    /// The platform's durability push has two call sites. The `workflow-complete` route alone
    /// would miss a run killed between doing the work and finishing its turn, leaving origin
    /// untouched; a spend limit, a watchdog kill and a Stop from the board all produce that.
    /// This seam is the OTHER call site: the platform's own observation that a run is over, which no agent has to remember and
    /// no budget can interrupt.
    /// </para>
    ///
    /// <para>
    /// AWAITED BEFORE THE ROW, and the ordering is the same guarantee the acceptance path makes for
    /// `workflow.completed`: a reader that sees this run ended can rely on the publish having been
    /// attempted first, rather than racing it.
    /// </para>
    ///
    /// <para>
    /// A FOREIGN DELEGATE ON THE CONSUMER THREAD, so it must bound its own wait and must not throw.
    /// This class cannot pick that bound - it knows nothing about what the handler does - and the
    /// one production handler (`TerminalPublish` in `Harness.Host`) states and justifies its own.
    /// What this class DOES guarantee is that a handler which throws anyway cannot cost the run its
    /// terminal row: see the catch at the call site.
    /// </para>
    ///
    /// <para>
    /// NULL IS THE ORDINARY CASE FOR A FIXTURE and means a container that publishes nothing but its
    /// own rows.
    /// </para>
    ///
    /// <para>
    /// THE ARGUMENTS ARE: which container, which message its run was handling, and WHETHER THE RUN
    /// SUCCEEDED. The third is there because a handler that offers a member another turn must tell a turn that was FINISHED
    /// from one that was taken away - a Stop from the board, a spend limit, a runner that threw -
    /// because offering a stopped member one more invocation would restart what a person had just
    /// stopped. It is passed rather than inferred because the terminal row that carries the same
    /// fact has not been written yet when this fires.
    /// </para>
    ///
    /// <para>
    /// THE ANSWER IS WHETHER THE PLATFORM DECLARED THE RUN'S WORKFLOW COMPLETE, for an owner that
    /// cannot declare, and it is carried onto the terminal row as
    /// <see cref="PayloadFields.WorkflowDeclared"/>. Asked here because the handler is the one place
    /// that decided it, and the row has not been written yet.
    /// </para>
    /// </summary>
    private readonly Func<ContainerId, long?, bool, CancellationToken, Task<bool>>? _onRunEnding;

    /// <summary>
    /// This member's tree for one card key, per repository, first repository first. Asked
    /// once per invocation with the key of the WAKING message, so two instructions naming two cards
    /// get two trees. Null, or an empty answer, means no worktree variables and no worktree line.
    /// </summary>
    private readonly Func<ContainerId, string, IReadOnlyList<RepoWorktree>>? _worktrees;

    /// <summary>Whether an agent preset can be watched, asked each time a snapshot is made so a
    /// repoint or a catalog edit shows on the next one. Null answers false.</summary>
    private readonly Func<string, bool>? _watchable;

    /// <summary>
    /// Called with a run's FIRST terminal row - the one that carries the run, its usage and its
    /// transcript - right after it is appended, still inside the run. Here so what a run leaves
    /// behind can be read against it before the member is free for its next wake: the Host's
    /// per-run tools check. Null is what a fixture wants. A handler's failure is not the run's.
    /// </summary>
    private readonly Func<Message, CancellationToken, Task>? _onTerminal;

    /// <summary>The member's tree for this card in the team's first repository.</summary>
    public const string WorktreeVariable = "HARNESS_WORKTREE";

    /// <summary>A suggested branch for this card, <c>&lt;member&gt;/&lt;key&gt;</c> in lower case.</summary>
    public const string BranchHintVariable = "HARNESS_BRANCH_HINT";

    /// <param name="context">
    /// Optional so a spec that is not about memory need not construct one. Null means this container
    /// has no memory - which is what every container had before the three artifacts existed.
    /// </param>
    /// <param name="transcripts">Optional for the same reason. Null means output is excerpted but
    /// the full bytes are not kept anywhere.</param>
    /// <param name="pending">Optional for the same reason context and transcripts are, and
    /// carrying the same trap: null means a container that silently LOSES accepted work on a
    /// restart. Program.cs builds ContainerHost by hand and passes it explicitly.</param>
    /// <param name="sinceSeq">
    /// The log's head at creation. This container's memory starts here: a container's identity is
    /// the pair (team, name), and the log outlives any team, so without a floor a new container
    /// would inherit every same-identity predecessor's history.
    /// </param>
    /// <param name="onRunEnding">See <see cref="_onRunEnding"/>. Optional, and null is what a
    /// fixture wants.</param>
    public MemberRuntime(
        ContainerDefinition definition,
        IMemberRunner runner,
        IMessageLog log,
        ITranscriptStore? transcripts = null,
        IPendingDeliveries? pending = null,
        Func<ContainerId, long, IDisposable?>? claimStart = null,
        long sinceSeq = 0,
        Func<ContainerId, long?, bool, CancellationToken, Task<bool>>? onRunEnding = null,
        Func<Task>? claimSignal = null,
        Action<ContainerId>? claimWithdraw = null,
        Func<ContainerId, string, IReadOnlyList<RepoWorktree>>? worktrees = null,
        Func<string, bool>? watchable = null,
        Func<Message, CancellationToken, Task>? onTerminal = null)
    {
        _watchable = watchable;
        _onTerminal = onTerminal;
        _claimSignal = claimSignal;
        _claimWithdraw = claimWithdraw;
        _onRunEnding = onRunEnding;
        _worktrees = worktrees;
        _sinceSeq = sinceSeq;
        _definition = definition;
        _baseSubscribes = new HashSet<string>(definition.Subscribes, StringComparer.Ordinal);
        _runner = runner;
        _log = log;
        _transcripts = transcripts;
        _pending = pending;
        _claimStart = claimStart;
        _limits = definition.Limits ?? ArtifactLimits.Default;
        Ceiling = definition.Ceiling ?? DefaultCeiling;

        _queue = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
        _consumer = Task.Run(ConsumeAsync);
    }

    public ContainerId Id => _definition.Id;

    /// <summary>
    /// What a report from this container right now should be caused BY - the seq of the message it
    /// is running, or null when it is idle.
    ///
    /// Null is a legal answer rather than an error: a status line arriving between runs simply
    /// starts its own thread. Refusing it instead would mean a member losing something it took the
    /// trouble to say, over a race it cannot see.
    /// </summary>
    public long? CurrentCausation => _currentSeq;

    /// <summary>
    /// Says a container did something the BOARD should refetch for, without changing any state.
    ///
    /// It stores nothing. A progress report lives on the message log, which is the one store, and
    /// the card renders it from the activity feed like every other row. A copy of the latest line
    /// held here as well would be two stores of one fact - the shape that produced the per-container
    /// command map this codebase deleted, and its three separate drift bugs.
    ///
    /// So why publish at all? Because the SPA's feed has NO POLL. `IndexPage` refetches messages
    /// only when a `containerChanged` snapshot arrives, so a progress row appended with nothing
    /// pushed would sit on the log unrendered until the next state change - which for a mid-run
    /// status update means it appears when the run ENDS, exactly when it has stopped being useful.
    /// The snapshot this sends is identical to the last one; it is the arrival that matters, not
    /// its contents.
    ///
    /// Deliberately does not touch state: reporting is not running, and a container that says
    /// something between runs is still idle.
    /// </summary>
    public void Republish() => Publish();

    /// <summary>
    /// The environment this container's agent runs under - whatever the <see cref="ContainerDefinition"/>
    /// it was built (or restored) with carries, read back rather than a second copy kept anywhere.
    /// Exists for specs: production never asks a container what it was handed, it just hands it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment => _definition.Environment;

    /// <summary>
    /// The composed text this container is currently running with.
    ///
    /// Read by tests and diagnostics; composition always starts again from the role prompt.
    ///
    /// It is NOT on <see cref="ContainerSnapshot"/> and must not be: a snapshot rides every
    /// SignalR push, and the composed text is deliberately exposed nowhere at all.
    /// </summary>
    public string SystemPrompt => _definition.SystemPrompt;

    /// <summary>What this member may cause through the platform's routes. Empty for one that holds
    /// no credential.</summary>
    public IReadOnlySet<string> Permits => _definition.Permits ?? NoPermits;

    private static readonly IReadOnlySet<string> NoPermits = new HashSet<string>(StringComparer.Ordinal);

    public int Ceiling { get; }

    public int QueueDepth => Volatile.Read(ref _queueDepth);

    public ContainerState State => _state;

    /// <summary>Raised whenever this container's snapshot changed, so the host can push it without
    /// polling. Handlers must not block: this fires on the consumer thread.</summary>
    public event Action<ContainerSnapshot>? Changed;

    public ContainerSnapshot Snapshot() => new(
        _definition.Id.Team, _definition.Id.Name, _definition.Label ?? _definition.Id.Name,
        _definition.Agent, _state, QueueDepth, Ceiling,
        _definition.Subscribes, _currentCorrelation, _sinceSeq, _definition.MissingAgent,
        _blocked, _failed, _needsDecision,
        _definition.UnreachableRoot, _definition.HiredFor,
        UnresolvedAgents: null, FailureClass: _failureClass, ResumeAt: _resumeAt,
        HandedBack: _handedBack, Held: _held,
        Watchable: _watchable?.Invoke(_definition.Agent) ?? false,
        Kind: MemberRef.KindOf(_definition.Agent));

    /// <summary>
    /// Records that this container has stopped without finishing, and pushes it.
    ///
    /// Deliberately does NOT touch <see cref="State"/>. The container is idle - it gave up on the
    /// work, it did not stop being able to take more - and that is exactly why this is a string on
    /// the snapshot rather than a third member of <see cref="ContainerState"/>.
    ///
    /// Cleared in <see cref="RunOneAsync"/>, at the next wake rather than at this run's end. Read
    /// <see cref="ContainerSnapshot.Blocked"/> before changing either half; the pair is the whole
    /// behaviour.
    /// </summary>
    public void MarkBlocked(string reason)
    {
        _blocked = reason;

        // Pushed, or the board never looks. The SPA's feed has no poll - it refetches only when a
        // `containerChanged` snapshot arrives - so a mark written with nothing published would sit
        // unrendered until the next state change, which for a container that has just gone idle is
        // whenever somebody next gives it work. Precisely when it has stopped being useful.
        Publish();
    }

    /// <summary>
    /// Records what this container has HANDED BACK - its part done, nothing owed - and pushes it.
    ///
    /// Like <see cref="MarkBlocked(string)"/> it deliberately does NOT touch <see cref="State"/>:
    /// the container is idle because it finished, which is the same run-loop state as idle because
    /// it gave up, and telling the two apart is the whole reason this is a string on the snapshot.
    ///
    /// UNLIKE <see cref="MarkBlocked(string)"/> IT IS NEVER CLEARED. The trio of trouble marks die
    /// at the next wake in <see cref="RunOneAsync"/>; this one is replaced by the next hand-back
    /// and by nothing else. <see cref="ContainerSnapshot.HandedBack"/> carries the argument, and
    /// the pair is the whole behaviour.
    ///
    /// PUBLISHED ONLY WHEN THE FIELD ACTUALLY MOVES, like the marks beside it - a member that hands
    /// the same words back twice has not changed anything a board needs to redraw for.
    /// </summary>
    public void MarkHandedBack(string delivered)
    {
        _handedBackThisRun = true;

        if (string.Equals(_handedBack, delivered, StringComparison.Ordinal))
        {
            return;
        }

        _handedBack = delivered;

        // Pushed, for MarkBlocked's reason: the SPA's feed has no poll and refetches only when a
        // `containerChanged` snapshot arrives, so a field written with nothing published would sit
        // unrendered until this member next changed state.
        Publish();
    }

    /// <summary>
    /// Records that the PLATFORM did not complete this container's last run, and pushes it.
    ///
    /// Called wherever `container.failed` is published - the arm of <see cref="RunOneAsync"/> that
    /// chooses Failed over Completed, and ContainerHost.ResumePendingAsync for a run the Host was
    /// restarted out from under. Miss one and a card stays silent for that path.
    ///
    /// PUBLISHED ONLY WHEN THE FIELD ACTUALLY MOVES, like every other mark on this record: the
    /// restart path marks a container once per interrupted row, and re-pushing an identical
    /// snapshot per row is a frame the board has to do something with for no news.
    ///
    /// Deliberately does NOT touch <see cref="State"/>, for <see cref="MarkBlocked(string)"/>'s
    /// reason: a container whose run failed is idle and perfectly able to take more work. That is
    /// exactly why this is a string on the snapshot rather than a third ContainerState - and it is
    /// why the mark has to outlive the run, because idle is also what a container that finished
    /// perfectly looks like.
    /// </summary>
    /// <param name="failureClass">
    /// One of `FailureClasses`, or NULL when this caller had no class to give - which is not the
    /// same thing as `unknown` and must not be written as it. The PAYLOAD defaults to `unknown`
    /// because a row on the log is read by things that have to decide what happens next; a MARK on
    /// a card is read by a person, and telling them "unknown" where nobody even asked the question
    /// is noise.
    /// </param>
    public void MarkFailed(string reason, string? failureClass = null)
    {
        if (string.Equals(_failed, reason, StringComparison.Ordinal)
            && string.Equals(_failureClass, failureClass, StringComparison.Ordinal))
        {
            return;
        }

        _failed = reason;
        _failureClass = failureClass;

        // Pushed, or the board never looks. The SPA has no poll - it refetches only when a
        // `containerChanged` snapshot arrives - so a mark written with nothing published sits
        // unrendered until somebody next gives that member work. Precisely when it has stopped
        // being useful.
        Publish();
    }

    /// <summary>
    /// Records that the platform intends to resume this member's workflow BY ITSELF at
    /// <paramref name="at"/>, and pushes it. Null withdraws the promise.
    ///
    /// VISIBLE IS PART OF THE DESIGN RATHER THAN POLISH. A silent
    /// automatic retry is how a quota failure becomes a spend failure; this is the field a person
    /// reads to see one coming, and the same call WITHDRAWS it when the bound is spent or the
    /// member has moved on - so the card never promises a resume nothing is going to fire.
    ///
    /// CALLED BY THE SWEEP, not by the run - see <see cref="ContainerSnapshot.ResumeAt"/>. It does
    /// NOT touch <see cref="State"/> and it is not a mark of its own: a member with a resume
    /// pending is idle, exactly as one whose run merely failed is, and it is cleared with the
    /// failure it belongs to at the next wake.
    ///
    /// PUSHED ONLY WHEN THE VALUE MOVES, like every mark here. The sweep runs on a timer and re-
    /// asserts the same answer on most ticks; a frame per tick is a board doing work for no news.
    /// </summary>
    public void MarkResumePending(DateTimeOffset? at)
    {
        var text = at?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        if (string.Equals(_resumeAt, text, StringComparison.Ordinal))
        {
            return;
        }

        _resumeAt = text;
        Publish();
    }

    /// <summary>
    /// Records that this container is waiting on a decision before it can continue, and pushes it.
    ///
    /// Same lifecycle as <see cref="MarkBlocked(string)"/>: outlives this run, clears on the next
    /// wake in <see cref="RunOneAsync"/>. The field is a mark beside state, not a state value.
    /// </summary>
    public void MarkNeedsDecision(string question)
    {
        if (string.Equals(_needsDecision, question, StringComparison.Ordinal))
        {
            return;
        }

        _needsDecision = question;
        Publish();
    }

    /// <summary>
    /// Marks one item in the running batch as abandoned, addressed by its 1-based number in the
    /// prompt, and returns the seq the blocked publication should be caused by.
    /// </summary>
    public bool TryBlockItem(int item, out long cause, out string? error)
    {
        cause = 0;
        error = null;

        lock (_batchGate)
        {
            if (_runningBatch is null || _runningBatch.Count == 0)
            {
                error = "No batch is running.";
                return false;
            }

            if (item < 1 || item > _runningBatch.Count)
            {
                error = $"Item {item} is out of range for this run (1..{_runningBatch.Count}).";
                return false;
            }

            var seq = _runningBatch[item - 1].Seq;
            _blockedItems ??= [];

            if (_deferredItems?.ContainsKey(seq) == true)
            {
                error = $"Item {item} was already deferred for this run; it is delivered again as its own run.";
                return false;
            }

            if (!_blockedItems.Add(seq))
            {
                error = $"Item {item} was already marked blocked for this run.";
                return false;
            }

            cause = seq;
            return true;
        }
    }

    /// <summary>
    /// DEFERS one item of the running batch, addressed by its 1-based number in the prompt: it is
    /// not closed when this run ends, it is delivered again as its own next run, in the same
    /// workflow, under its own causation. Refused for the only item of a run - there is no later
    /// run to put it in - and for the last item not yet deferred, so a run always answers or blocks
    /// something.
    /// </summary>
    public bool TryDeferItem(int item, string reason, out long seq, out string? error)
    {
        seq = 0;
        error = null;

        lock (_batchGate)
        {
            if (_runningBatch is null || _runningBatch.Count == 0)
            {
                error = "No batch is running.";
                return false;
            }

            if (_runningBatch.Count == 1)
            {
                error = "This run carries only one item, so there is no later run to defer it to: do it, or block it with a reason.";
                return false;
            }

            if (item < 1 || item > _runningBatch.Count)
            {
                error = $"Item {item} is out of range for this run (1..{_runningBatch.Count}).";
                return false;
            }

            var candidate = _runningBatch[item - 1].Seq;
            _deferredItems ??= [];

            if (_blockedItems?.Contains(candidate) == true)
            {
                error = $"Item {item} was already marked blocked for this run.";
                return false;
            }

            if (_deferredItems.ContainsKey(candidate))
            {
                error = $"Item {item} was already deferred for this run.";
                return false;
            }

            if (_deferredItems.Count + 1 >= _runningBatch.Count)
            {
                error = $"Every other item of this run is already deferred. Do item {item} or block it with a reason.";
                return false;
            }

            _deferredItems[candidate] = reason;
            seq = candidate;
            return true;
        }
    }

    /// <summary>
    /// Queues a deferred item found on its pending row when the Host starts, as its own run with
    /// its note, exactly as the run that deferred it would have. Answers false at the ceiling, as
    /// <see cref="OfferAsync"/> does, without a rejection row: the item was accepted long ago.
    /// </summary>
    public bool Redeliver(Message message, long fromRun)
    {
        _redeliverFrom[message.Seq] = fromRun;
        Interlocked.Increment(ref _queueDepth);

        if (!_queue.Writer.TryWrite(message))
        {
            Interlocked.Decrement(ref _queueDepth);
            _redeliverFrom.TryRemove(message.Seq, out _);
            return false;
        }

        Publish();
        return true;
    }

    /// <summary>
    /// Replaces the system prompt - and, optionally, the display label and the AGENT - of a
    /// container that is already running.
    ///
    /// Repointing is safe here and only here. The catalog's per-container entry is what a runner
    /// resolves at launch, so a caller changing <paramref name="agent"/> must set that entry in the
    /// same operation; changing one without the other leaves a member whose card names one preset
    /// and whose process is another, and nothing fails.
    ///
    /// Exists for two callers, still the only things in this codebase that change a container after
    /// creation: renaming a team has to reach that team's manager, whose prompt names the team it
    /// manages, and editing a member's name or prompt has to reach the member itself. Without this
    /// the manager keeps introducing itself by the old name in the console for the rest of the
    /// process's life, and a relabelled member's card keeps showing what it was called at creation -
    /// and the person talking to it is the only one who notices either.
    ///
    /// <paramref name="label"/> defaults to null, meaning NO CHANGE - a team rename does not touch a
    /// member's own label, so that caller passes only a prompt. A caller that IS changing the label
    /// passes the new value; there is no way to clear a label back to the identifier through this
    /// overload because nothing needs to yet.
    ///
    /// The whole definition is swapped rather than a field mutated, because it is a record and a
    /// reference assignment is atomic - a run already in flight finishes against the definition it
    /// started with, and the next invocation picks up the new one. That is the correct boundary:
    /// re-prompting an agent mid-run would change what it was told after it was told it.
    /// </summary>
    public void Reprompt(
        string systemPrompt, string? label = null, string? agent = null)
    {
        // The EFFECTIVE name, not the raw Label: null there means "the identifier", so comparing
        // Label alone reports a change when a caller passes back the very name it just read off a
        // snapshot - which is exactly what a caller that re-prompts without renaming does.
        var before = (_definition.Label ?? _definition.Id.Name, _definition.MissingAgent);

        _definition = _definition with
        {
            SystemPrompt = systemPrompt,
            Label = label ?? _definition.Label,
            Agent = agent ?? _definition.Agent,

            // Cleared on a repoint, and only on a repoint. MissingAgent is set at RESTORE, when a
            // stored preset name no longer resolves; pointing the member at one that does is the
            // whole recovery, so leaving the flag set would keep a working member's card marked
            // broken until the next restart.
            MissingAgent = agent is null ? _definition.MissingAgent : null,
        };

        // Only when something a SNAPSHOT carries moved - the label, the agent, or the missing-agent
        // mark. A viewer with the board already open must see a rename, a repoint or a cleared
        // badge without reloading, the same reason every state transition in ConsumeAsync calls
        // Publish() - but the prompt TEXT is invisible to ContainerSnapshot by design, so
        // publishing an ordinary re-prompt pushes a frame byte-identical to the last one to every
        // board on the team.
        //
        // That is not merely noise. RepromptManager runs on every member add and every team rename,
        // so an unconditional Publish() would make ONE member add deliver TWO containerChanged
        // frames, and HubGroupTests.A_container_change_reaches_only_its_own_teams_group - the test
        // that guards per-team routing, which is a disclosure boundary - would pass or fail on
        // timing.
        //
        // Safe against a run already in flight: RunOneAsync reads `_definition` into the
        // MemberInvocation it builds at the START of a run, before this could race it, and the swap
        // above is a single atomic reference assignment either way - a run that started before this
        // call finishes against the definition it started with, exactly as the class doc promises.
        //
        // A REPOINT is `agent` itself, never a comparison of the old Agent with the new: the pump
        // passes a member's implementation on and never compares it (PumpArchitectureTests), and
        // the Host passes `agent` only when it has already decided the member was repointed.
        if (agent is not null || before != (_definition.Label ?? _definition.Id.Name, _definition.MissingAgent))
        {
            Publish();
        }
    }

    /// <summary>
    /// Moves this container's FLOOR forward - the reset primitive, on demand rather than only at
    /// creation.
    ///
    /// The same value the container was created with, doing the same two jobs it has always done:
    /// it is where the ledger starts, so nothing older is REMEMBERED, and the snapshot carries it
    /// to the browser, so nothing older is SHOWN. The container's CURSOR is somebody else's - the
    /// caller advances that beside this call - because this class knows nothing about stores.
    ///
    /// The second thing in this codebase that changes a container after creation, after
    /// <see cref="Reprompt"/>, and deliberately shaped like it: one named mutation, called from one
    /// place, publishing only when a field a SNAPSHOT carries actually moved.
    ///
    /// FORWARD ONLY. Lowering a floor un-forgets - the container would read a predecessor's history
    /// back into its next context, which is precisely the state the floor exists to make
    /// impossible. Ignored rather than thrown, because a caller passing a head it read a moment ago
    /// against a container that has been quiet since is not making a mistake.
    ///
    /// SAFE against a run already in flight for the reason Reprompt is: RunOneAsync reads the floor
    /// when it BUILDS its context, at the start of a run, so a run that started before this call
    /// finishes against the floor it started with. The caller refuses a Running container anyway;
    /// this is the belt to that braces.
    /// </summary>
    /// <summary>
    /// Records that this container's Agent is no longer in the catalog, and pushes it.
    ///
    /// The THIRD thing in this codebase that changes a container after creation, after
    /// <see cref="Reprompt"/> and <see cref="Refloor"/>, and shaped like both: one named mutation,
    /// called from one place, publishing only when a field a SNAPSHOT carries actually moved.
    ///
    /// IT EXISTS BECAUSE <c>MissingAgent</c> IS OTHERWISE SET ONLY AT RESTORE. A catalog reset that
    /// removes a preset a live member runs leaves that member's card GREEN until the Host next
    /// starts, over a member that will refuse at its very next wake - and the gap to that wake can
    /// be hours. A green card over a container that cannot run is a board lying about what can run.
    ///
    /// NOT a per-container command cache: that would be a second store of WHAT A CONTAINER RUNS,
    /// which every repoint would have to invalidate. This is the existing mark, with a second
    /// write point: it makes the mark less stale rather than more, and it is
    /// cleared by the path that already clears it - <see cref="Reprompt"/> on a real repoint.
    ///
    /// FORWARD-ONLY in the sense that matters: handed the name it already holds, it does nothing and
    /// publishes nothing.
    /// </summary>
    public void MarkMissingAgent(string agent)
    {
        if (string.Equals(_definition.MissingAgent, agent, StringComparison.OrdinalIgnoreCase)) return;

        _definition = _definition with { MissingAgent = agent };

        // Pushed, for Refloor's and MarkBlocked's reason: the SPA's board has no poll, so a mark
        // written with nothing published sits unrendered until the next state change - which for a
        // container nobody is about to wake is never.
        Publish();
    }

    /// <summary>
    /// Replaces what this container's children are launched with, so a change to the team's
    /// environment reaches an agent that is ALREADY RUNNING.
    ///
    /// The fourth thing that changes a container after creation, beside <see cref="Reprompt"/>,
    /// <see cref="Refloor"/> and <see cref="MarkMissingAgent"/>. It belongs with them because it is
    /// ONE TERMINAL FACT - the environment is now this - rather than a stream. Without it, a
    /// rotated credential would reach nobody until the Host restarted - the same shape as an edit
    /// that writes a new prompt to the database and goes on telling the live agent the old one.
    ///
    /// IT PUBLISHES NOTHING, and that is correct rather than forgotten. No snapshot field carries
    /// the environment and none ever may: a <c>ContainerSnapshot</c> rides SignalR to every browser
    /// holding this team, so putting the environment on it would broadcast the team's credentials
    /// to every connected client on every container change.
    /// </summary>
    public void Reenv(IReadOnlyDictionary<string, string> environment)
    {
        _definition = _definition with { Environment = environment };
    }

    /// <summary>
    /// Moves this container's LIVE subscription set to match what
    /// <see cref="Harness.Host.EffectiveSubscriptions.RecomputeAsync"/> just persisted, and pushes
    /// it when it moved.
    ///
    /// THE FIFTH THING THAT CHANGES A CONTAINER AFTER CREATION, and it earns the place beside
    /// <see cref="Reprompt"/>, <see cref="Refloor"/>, <see cref="MarkMissingAgent"/> and
    /// <see cref="Reenv"/>. The standing rule for all four is that each is ONE TERMINAL FACT rather
    /// than a stream, and that a fifth belongs here only on the same terms: this is one fact per
    /// edit - the effective set is now THIS - it moves a field already on the snapshot
    /// (<see cref="ContainerSnapshot.Subscribes"/>), and like the other four it publishes only when
    /// the value actually moved.
    ///
    /// Without it, a trigger is created, its row lands in `subscriptions`, and the delivery pump
    /// starts using the new set immediately - but the card a person already has open goes on
    /// showing the subscriptions this container was CREATED with, which is the state every reader
    /// of that card trusts.
    ///
    /// COMPARED AS A SET, not a sequence: the recompute that calls this builds a <c>HashSet</c>, so
    /// enumeration order is not stable across calls - a sequence comparison would report "moved" on
    /// every recompute, including one a trigger edit triggered that changed nothing about the
    /// effective set, and push a snapshot to every browser holding the team every time.
    /// </summary>
    public void Resubscribe(IReadOnlyCollection<string> types)
    {
        var before = _definition.Subscribes;

        _definition = _definition with { Subscribes = types };

        if (!new HashSet<string>(before, StringComparer.Ordinal).SetEquals(types))
        {
            Publish();
        }
    }

    /// <summary>
    /// Whether <paramref name="type"/> was in this container's BASE set - the one it was created or
    /// restored with, never touched by <see cref="Resubscribe"/>.
    ///
    /// The delivery pump's fast, unconditional path: a member's base subscription must never be
    /// silently narrowed by an unrelated event trigger that happens to name the same type with its
    /// own filter. No store read either - <see cref="_baseSubscribes"/> is already in memory, so a
    /// message this container has always received costs nothing extra to keep receiving.
    /// </summary>
    public bool HasBaseSubscription(string type) => _baseSubscribes.Contains(type);

    public void Refloor(long sinceSeq)
    {
        if (sinceSeq <= _sinceSeq) return;

        _sinceSeq = sinceSeq;

        // Pushed, for MarkBlocked's reason. The SPA's feed has no poll - it refetches only when a
        // `containerChanged` snapshot arrives - so a floor moved with nothing published would leave
        // the last task on the card until somebody next gave this container work. Precisely when
        // the reset has stopped being useful.
        Publish();
    }

    /// <summary>
    /// Offers a message to this container. Returns false when the queue is at its ceiling, having
    /// published a rejection carrying what was refused.
    ///
    /// Rejecting rather than holding is what makes backpressure a SIGNAL. A held command is
    /// indistinguishable from a slow one, and nothing can tell them apart afterwards; a rejection
    /// reaches the sender immediately and lets it decide.
    /// </summary>
    public async Task<bool> OfferAsync(Message message, CancellationToken ct = default)
    {
        if (QueueDepth >= Ceiling)
        {
            await _log.AppendAsync(
                new NewMessage(
                    MessageTypes.Rejected,
                    JsonSerializer.Serialize(new
                    {
                        reason = "queue is at its ceiling",
                        ceiling = Ceiling,
                        refused = message.Type,
                        refusedSeq = message.Seq,
                    }),
                    Id.ToString(),

                    // Caused by the message that was refused, so the rejection lands in the same
                    // workflow the sender is following rather than starting an orphan thread.
                    message.Seq),
                ct);

            return false;
        }

        // BEFORE the enqueue and before the pump advances the cursor past this message. A crash
        // between the row and the advance leaves the work resumable; the reverse order leaves a
        // cursor past a message nothing remembers accepting.
        //
        // A rejection returns above this line and therefore leaves no row - which is the point:
        // the sender already has its refusal and nothing owes it a second delivery.
        //
        // Deliberately NOT swallowed, unlike StartAsync and RemoveAsync below. Swallowing here would
        // defeat the point of the pending record: the container would go on to run the work with no
        // durable record of having accepted it, which is exactly the silent loss it prevents. Left
        // to propagate, this throws out of OfferAsync before _cursors.AdvanceAsync runs, so the
        // message is redelivered on the pump's next tick rather than lost - and PumpService already
        // catches and logs a failed offer. Loud and correct beats consistent with the other two.
        if (_pending is not null) await _pending.AddAsync(Id, message.Seq, ct);

        Interlocked.Increment(ref _queueDepth);

        if (!_queue.Writer.TryWrite(message))
        {
            Interlocked.Decrement(ref _queueDepth);
            return false;
        }

        Publish();
        return true;
    }

    /// <summary>
    /// The next message to consider: whatever a previous batch declined, before anything new.
    ///
    /// HELD MESSAGES FIRST, or a workflow set aside once could be set aside forever while a busier
    /// one keeps arriving.
    /// </summary>
    private bool TryTake(out Message message)
    {
        // A DEFERRED ITEM FIRST: it was promised the next run.
        if (_redeliveries.Count > 0)
        {
            message = _redeliveries.Dequeue();
            return true;
        }

        if (_deferred.Count > 0)
        {
            message = _deferred.Dequeue();
            return true;
        }

        return _queue.Reader.TryRead(out message!);
    }

    private async Task ConsumeAsync()
    {
        try
        {
            // `_deferred.Count > 0 ||` FIRST, and it is not a micro-optimisation. WaitToReadAsync
            // waits on the CHANNEL, so a container holding a set-aside message with nothing new
            // arriving would wait forever on work it is already holding - the workflow would never
            // run and nothing would say why.
            while (_deferred.Count > 0 || _redeliveries.Count > 0
                   || await _queue.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (true)
                {
                    // THE MESSAGE IS TAKEN BEFORE THE SLOT IS CLAIMED, and that order is what makes
                    // the release below unmissable: a container with nothing to run must never be
                    // parked in `WaitToReadAsync` holding a slot nobody can free. Taking first costs
                    // nothing visible - `_queueDepth` is decremented further down, so the card still
                    // shows this message queued for as long as the hold lasts.
                    if (!TryTake(out var first))
                    {
                        break;
                    }

                    // ONE HOLD, TWO REASONS. This was `_isTeamPaused` and answered a team; it now
                    // answers THIS CONTAINER, because the cap holds workers and never the Manager.
                    // A cap enforced anywhere but here would be a second answer to "may work start",
                    // free to disagree with this one.
                    //
                    // AND IT CLAIMS. A poll that only asked let every container woken by one
                    // dispatch read the same "nobody is running yet" and start together; the claim
                    // is taken under the host's own lock, so the (cap+1)-th container waits here
                    // instead.
                    IDisposable? slot = null;

                    //
                    // AND IT WAITS ON A SIGNAL, NOT A CLOCK. The signal is read before the
                    // claim is asked, so a release between the refusal and the wait still wakes this;
                    // the ledger decides who is admitted, in queue order, so there is no race to win.
                    if (_claimStart is not null)
                    {
                        try
                        {
                            while (true)
                            {
                                var signal = _claimSignal?.Invoke();

                                if ((slot = _claimStart(Id, first.Seq)) is not null) break;

                                if (!_held)
                                {
                                    _held = true;
                                    Publish();
                                }

                                await (signal ?? Task.Delay(20, _shutdown.Token))
                                    .WaitAsync(_shutdown.Token);
                            }
                        }
                        catch
                        {
                            _claimWithdraw?.Invoke(Id);
                            _held = false;
                            throw;
                        }

                        // Cleared without a publish of its own: the Running snapshot published a
                        // few lines below carries it.
                        _held = false;
                    }

                    // EVERYTHING FROM HERE TO THE RELEASE IS INSIDE THE `try`. RunOneAsync swallows
                    // its own agent failures, but a cancelled shutdown, a store throwing out of
                    // `BeginBatch`, or anything else unforeseen would otherwise leave the slot held
                    // by a container that is no longer running - permanent under-capacity, which is
                    // worse than a brief overshoot.
                    try
                    {
                        var batch = new List<Message>(DefaultCeiling) { first };
                        var lastGrowthAt = DateTime.UtcNow;

                        // A DEFERRED ITEM RUNS ALONE: it was deferred out of a batch to be its own run.
                        var alone = _redeliverFrom.ContainsKey(first.Seq);

                        while (!alone && batch.Count < DefaultCeiling)
                        {
                            var before = batch.Count;
                            DrainBatch(batch, first.CorrelationId);

                            if (batch.Count >= DefaultCeiling) break;

                            if (batch.Count > before)
                            {
                                lastGrowthAt = DateTime.UtcNow;
                                continue;
                            }

                            if (DateTime.UtcNow - lastGrowthAt >= TimeSpan.FromMilliseconds(5)) break;
                            await Task.Delay(1, _shutdown.Token);
                        }

                        // Busy BEFORE the depth decrement, never after: a reader seeing Idle with depth 0
                        // while a dequeued message has not started yet would report "nothing happening"
                        // at the one moment something is.
                        _state = ContainerState.Running;
                        _currentCorrelation = first.CorrelationId;
                        _currentSeq = first.Seq;
                        BeginBatch(batch);

                        foreach (var message in batch)
                        {
                            // Marked here rather than in RunOneAsync: this is the first instant at which
                            // an agent may have been launched, and a delivery that MIGHT have run is
                            // reported rather than resumed.
                            //
                            // Swallowed like RemoveAsync, not left to propagate like AddAsync:
                            // ConsumeAsync's only catch is OperationCanceledException, so an unguarded
                            // failure here (SQLite busy, locked, disk error) would fall out of the while
                            // loop and kill the background consumer task with nothing observing the fault
                            // - every later message for this container queues forever with no completion,
                            // no failure and no log line. The cost of swallowing is real but
                            // recoverable: the row stays started = 0, so a crash after this point has
                            // the delivery RESUMED rather than reported - it degrades to re-running the
                            // work, which beats a dead consumer.
                            if (_pending is not null)
                            {
                                try
                                {
                                    await _pending.StartAsync(Id, message.Seq, _shutdown.Token);
                                }
                                catch (Exception) when (!_shutdown.IsCancellationRequested)
                                {
                                }
                            }

                            Interlocked.Decrement(ref _queueDepth);
                            Publish();
                        }

                        await RunOneAsync(batch).ConfigureAwait(false);

                        _state = ContainerState.Idle;
                        _currentCorrelation = null;
                        _currentSeq = null;
                        Publish();
                    }
                    finally
                    {
                        slot?.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Grows the batch with messages of THIS WORKFLOW ONLY; anything else is held for a later
    /// invocation.
    ///
    /// IT READS THE CHANNEL AND NEVER `_deferred`. Reading the held queue here would take a message
    /// of another workflow, find it still does not match, and put it straight back - a loop that
    /// never terminates. The held messages are for the OUTER loop, which takes one as the next
    /// batch's `first`.
    /// </summary>
    private void DrainBatch(List<Message> batch, long correlation)
    {
        while (batch.Count < DefaultCeiling && _queue.Reader.TryRead(out var next))
        {
            // A deferred item delivered again on a restart is held like another workflow's message,
            // and taken as a batch of its own.
            if (next.CorrelationId == correlation && !_redeliverFrom.ContainsKey(next.Seq)) batch.Add(next);
            else _deferred.Enqueue(next);
        }
    }

    /// <summary>
    /// This container's environment PLUS the one value that changes per invocation: the seq of the
    /// message being answered.
    ///
    /// It cannot live in <c>AgentEnvironment.ForContainerAsync</c> with the other three, because
    /// that runs ONCE, at creation, and mints a credential. This changes on every wake.
    ///
    /// Correlation propagates automatically INSIDE this process - every message a run causes
    /// passes the waking message as its cause - but a manager dispatches by calling the
    /// <c>tell</c> tool, and that is a fresh request from a child process which was never told what
    /// woke its parent. Without this, every dispatch would start a NEW workflow, and "one
    /// correlation id on both sides of the hop" could not be true however carefully the agent
    /// behaved. Carrying the seq out to the child and back in through <c>tell</c> keeps it true:
    /// the agent never learns that a correlation id exists, so it cannot forget one.
    ///
    /// A COPY, not a mutation of the definition's dictionary - that one is shared across every
    /// invocation, and writing into it would leave the previous run's causation set on a container
    /// woken by something that supplies none.
    /// </summary>
    /// <remarks>
    /// The card's worktree travels the same way and for the same reason: which tree a run
    /// belongs in is a property of the instruction that woke it, not of the member, so it is decided
    /// here per invocation from <paramref name="worktree"/> - never stored on the definition.
    /// </remarks>
    private IReadOnlyDictionary<string, string> EnvironmentFor(long cause, WorktreeFor? worktree)
    {
        // Nothing to add to: a container with no permits gets no HARNESS_ variables at all, and
        // a causation without a credential to send it with is a variable nothing can read.
        if (_definition.Environment.Count == 0) return _definition.Environment;

        var environment = new Dictionary<string, string>(_definition.Environment, StringComparer.Ordinal)
        {
            ["HARNESS_CAUSATION"] = cause.ToString(CultureInfo.InvariantCulture),
        };

        if (worktree is { Trees.Count: > 0 })
        {
            environment[WorktreeVariable] = worktree.Trees[0].Path;
            environment[BranchHintVariable] = worktree.BranchHint;
        }

        return environment;
    }

    /// <summary>The trees for one invocation, and the branch it suggests.</summary>
    private sealed record WorktreeFor(string Key, string BranchHint, IReadOnlyList<RepoWorktree> Trees);

    /// <summary>
    /// The trees for the card the WAKING message is about: its <c>card</c> when it is an
    /// instruction that names one, else <c>w&lt;correlation&gt;</c>. Null when this container has
    /// no worktree seam or its team has no repository.
    /// </summary>
    private WorktreeFor? WorktreeForInvocation(Message first)
    {
        if (_worktrees is null) return null;

        var card = first.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
            ? CardOf(first.Payload)
            : null;
        var key = Worktrees.KeyFor(card, first.CorrelationId);
        var trees = _worktrees(Id, key);

        return trees.Count == 0 ? null : new WorktreeFor(key, Worktrees.BranchHint(Id.Name, key), trees);
    }

    private static string? CardOf(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(PayloadFields.Card, out var card)
                && card.ValueKind == JsonValueKind.String
                    ? card.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void BeginBatch(IReadOnlyList<Message> messages)
    {
        lock (_batchGate)
        {
            _runningBatch = messages;
            _blockedItems = [];
            _deferredItems = [];
        }
    }

    private (HashSet<long> Blocked, Dictionary<long, string> Deferred) EndBatch()
    {
        lock (_batchGate)
        {
            var blocked = _blockedItems ?? [];
            var deferred = _deferredItems ?? [];
            _blockedItems = null;
            _deferredItems = null;
            _runningBatch = null;
            return (blocked, deferred);
        }
    }

    /// <summary>
    /// The message a deferred item is run as: its own, with <see cref="PayloadFields.DeferredFromRun"/>
    /// added so the prompt can say where it came from. The seq, causation and correlation are the
    /// original's, untouched.
    /// </summary>
    private static Message WithDeferredFrom(Message message, long fromRun)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(message.Payload) is System.Text.Json.Nodes.JsonObject payload)
            {
                payload[PayloadFields.DeferredFromRun] = fromRun;
                return message with { Payload = payload.ToJsonString() };
            }
        }
        catch (JsonException)
        {
        }

        return message;
    }

    /// <summary>
    /// Ends the invocation in flight, if there is one. Answers whether there was.
    ///
    /// Deliberately NOT <see cref="DisposeAsync"/> and not a delete: this ends one run and leaves the
    /// container able to take the next instruction. Deleting the member also ends a run, and destroys
    /// a workspace and a row with it, which is far too big a reach for "this one has gone wrong".
    ///
    /// Answering false for an idle container is the honest result rather than an error: by the time
    /// a click reaches the server the run it was aimed at may have finished on its own, and that is
    /// not a failure of anything.
    /// </summary>
    public bool Stop()
    {
        var run = _running;

        if (run is null) return false;

        _stoppedByHand = true;

        try
        {
            run.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended between reading the field and cancelling it. Nothing to stop.
            return false;
        }

        return true;
    }

    private async Task RunOneAsync(IReadOnlyList<Message> batch)
    {
        var first = batch[0];

        // Every message this invocation causes passes the WAKING message as its cause. That is the
        // whole of correlation propagation: the workflow follows automatically
        // and the agent never has to know a correlation id exists, so it cannot forget one.
        var cause = first.Seq;

        // NEW WORK CLEARS THE LAST RUN'S MARKS, and here rather than at the end of the run that set
        // them. Outliving its own run is the entire point of a mark: a container that gave up, one
        // whose run the platform failed and one that stopped to ask a question all go idle, and
        // idle is exactly what a container that finished perfectly also is. What makes a mark a lie
        // is not the run ending, it is this container being given something else to do.
        //
        // ALL THREE DIE TOGETHER AND ON ADJACENT LINES, deliberately, so the trio cannot drift
        // apart. A run can leave two of them - an agent publishes `blocked`, then its process dies
        // and the platform publishes `failed` - and a wake that cleared one of a pair would leave a
        // card wearing half of the last job.
        _blocked = null;
        _failed = null;

        // THE OTHER TWO HALVES OF THE `failed` MARK, on the line beside it for the reason the trio
        // above is adjacent: a class or a promised resume outliving the reason it belongs to is a
        // card describing two different runs at once.
        _failureClass = null;
        _resumeAt = null;
        _needsDecision = null;

        // The per-run flag, unlike the words below it: a new run has not handed anything back yet.
        _handedBackThisRun = false;

        // `_handedBack` IS NOT CLEARED HERE, AND ITS ABSENCE FROM THIS BLOCK IS THE DECISION.
        //
        // Every field above is a mark of TROUBLE, and the argument for clearing them is the comment
        // at the top of this block: what makes a mark a lie is this container being given something
        // else to do. A DELIVERY is not made a lie by that - the member really did hand that back,
        // and it stays true afterwards. Clearing it here would also erase it exactly when it
        // matters most, since a hand-back's whole purpose is to wake somebody who then sends more
        // work: the wake that carries the reply would take the record of what it was replying to.
        //
        // See `ContainerSnapshot.HandedBack` before adding it to the lines above.

        // PER-INVOCATION, linked to the container's own token. Stop cancels THIS and nothing else,
        // so a member whose run was stopped goes idle and takes its next instruction normally;
        // cancelling `_shutdown` would take the consumer loop with it and the member would never
        // wake again.
        //
        // CREATED AT THE TOP OF THE INVOCATION, and that placement matters. The consumer loop marks
        // this container Running BEFORE calling this method, so setting `_running` any later leaves
        // a window in which the card says `running` and Stop answers "it was not running" - which
        // is worse than a plain failure because the button reports success at doing nothing. An
        // agent runner building its ledger context sits inside that window and is not instant.
        using var run = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        _running = run;
        _stoppedByHand = false;

        // A DEFERRED ITEM DELIVERED AGAIN: alone, and told where it came from.
        long? deferredFrom = batch.Count == 1 && _redeliverFrom.TryRemove(first.Seq, out var from) ? from : null;
        IReadOnlyList<Message> work = deferredFrom is { } fromRun ? [WithDeferredFrom(first, fromRun)] : batch;

        // The run's own seq, which an item deferred out of it names. The waking seq stands in when
        // the row could not be written: a deferral must still name something.
        var runSeq = (await SafeAppendAsync(new NewMessage(
            MessageTypes.Started,
            JsonSerializer.Serialize(new { trigger = first.Type }),
            Id.ToString(),
            cause)))?.Seq ?? cause;

        MemberResult result;

        var worktree = WorktreeForInvocation(first);

        try
        {
            // THE WORK AS DATA, NOT AS A PROMPT. The batch, the card's worktrees and the floor go to
            // the runner as they are; an agent runner renders them into prose with its ledger
            // history, a plugin runner hands them to its executable as JSON. Rendering here would
            // be the runtime deciding every member is an agent.
            result = await _runner.RunAsync(
                new MemberInvocation(
                    Id,
                    _definition.Agent,
                    work,
                    _definition.WorkingDirectory,
                    EnvironmentFor(cause, worktree),
                    new MemberRunContext(
                        // `_sinceSeq` is read HERE, at the start of the run, exactly as the context
                        // build always read it: `Refloor` moving it mid-run must not change history
                        // an invocation has already been handed.
                        _sinceSeq,
                        _limits,
                        worktree?.Trees ?? [],
                        worktree?.BranchHint,
                        _definition.UnreachableRoot,
                        _definition.SystemPrompt)),
                run.Token);
        }
        catch (OperationCanceledException) when (_stoppedByHand && !_shutdown.IsCancellationRequested)
        {
            // A PERSON STOPPED THIS RUN, and it must not escape.
            //
            // The clause below deliberately lets OperationCanceledException through, which is right
            // for `_shutdown`: that means the Host is going down and the consumer loop is ending
            // anyway, so propagating is correct.
            //
            // A Stop is a SECOND source of cancellation, and without this arm it would throw
            // straight out of RunOneAsync - so nothing would be published, the container would never
            // return to Idle, and the card would sit on `running` forever while Stop reported
            // success. A test that asserts only refusals cannot see this: an idle container refusing
            // politely and the wrong caller being turned away both pass whether Stop works or does
            // nothing at all.
            //
            // A runner that handles its own cancellation - ProcessAgentRunner does, so it can kill
            // the child and say why - returns a failed result instead and never reaches here. This
            // is for the ones that do not.
            result = MemberResult.NotRun("This run was stopped.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The consumer loop must outlive any single invocation, or one bad message stops every
            // later one for this container.
            result = MemberResult.NotRun(ex.Message);
        }
        finally
        {
            _running = null;
        }

        // SAID IN THE RIGHT WORDS. The runner cannot tell a person's Stop from the Host shutting
        // down - both arrive as the same cancelled token - so the container, which is the thing that
        // was asked, supplies the reason.
        //
        // Only over a FAILURE. Stop races a run that was finishing anyway, and relabelling a
        // successful run as stopped would report a lie about work that was actually delivered.
        if (_stoppedByHand && !result.Succeeded)
        {
            result = MemberResult.NotRun(
                "This run was stopped from the board. Whatever it had done is not recorded.",

                // `interrupted`, WHICH IS THE WORD THAT ALREADY EXISTS. A person pressing Stop cut
                // this run off exactly as a Host restart does, and the spelling is not to be
                // invented twice. It never resumes automatically, which is
                // the only behaviour a class decides and plainly the right one here: the platform
                // must not undo a person's Stop on a timer.
                FailureClasses.Interrupted,

                // What the member did before the Stop is still in its own transcript.
                result.Transcript);
        }

        // Written BEFORE the message is published, so a payload never references a transcript that
        // does not exist yet. Best effort for the same reason the log append is: losing the event
        // because the disk was full would be a far worse outcome than losing the transcript.
        var reference = new TranscriptRef(Id, cause).ToString();

        if (_transcripts is not null)
        {
            try
            {
                var written = await _transcripts.WriteAsync(Id, cause, result.Output, _shutdown.Token);
                reference = written.ToString();
            }
            catch (Exception) when (!_shutdown.IsCancellationRequested)
            {
            }
        }

        // NULL MEANS UNKNOWN, and unknown is written as absent keys rather than zeros. There is no
        // `?? estimate` arm: an Agent whose CLI reports no usage reports none, and the
        // projection already counts a row with no `tokensIn`/`tokensOut` as a run without usage.
        // Zeros here would be indistinguishable from a run that genuinely spent nothing.
        var usage = result.Usage;

        var (blockedItems, deferredItems) = EndBatch();

        // A DEFERRAL HOLDS ONLY OVER A FINISHED RUN. A run that failed or was stopped closes every
        // item it did not block as failed, the way it always has: the Manager woken by that failure
        // decides what to send again, and a Stop from the board is never undone by a redelivery.
        if (!result.Succeeded) deferredItems = [];

        // WHAT BECAME OF EVERY ITEM, for a run that carried more than one: never a bare `completed`
        // copied from another item. Written on each terminal row of the run.
        var items = batch.Count > 1
            ? batch.Select((message, index) => new RunItem(
                    index + 1,
                    message.Seq,
                    blockedItems.Contains(message.Seq) ? ItemOutcomes.Blocked
                    : deferredItems.ContainsKey(message.Seq) ? ItemOutcomes.Deferred
                    : result.Succeeded ? ItemOutcomes.Answered
                    : ItemOutcomes.Failed,
                    deferredItems.TryGetValue(message.Seq, out var why) ? why : null))
                .ToList()
            : null;
        var output = Excerpt.Of(result.Output, _limits.ExcerptChars, reference, failed: !result.Succeeded);

        // THE CARD'S HALF OF `container.failed`, set on the same arm that chooses to publish it.
        //
        // The tile reads the log and the card reads the snapshot, and that is two questions rather
        // than an inconsistency: the tile asks whether this TEAM is dead, over the whole roster,
        // and the card asks whether THIS member's last run failed.
        //
        // THE RUNNER'S SENTENCE, because the runner is the only layer that knows what kind of thing
        // failed: an agent runner says "The agent exited N" or that the run never reached the
        // platform, a plugin runner says what the plugin said. The fallback below is for a runner
        // that failed without a sentence, and says nothing about what it was running.
        var failure = !string.IsNullOrWhiteSpace(result.FailureReason)
            ? result.FailureReason
            : !string.IsNullOrWhiteSpace(result.LaunchError)
                ? result.LaunchError
                : $"This run did not complete (exit {result.ExitCode}).";

        // THE CLASS. What the runner found when it found anything - it is the only layer that saw
        // the implementation's own words - and otherwise UNKNOWN, which is a real class rather than
        // an absent field: a failure nobody could classify is not a guess.
        var failureClass = result.FailureClass ?? FailureClasses.Unknown;

        // THE RUN IS OVER, HOWEVER IT ENDED, AND THIS IS THE LAST MOMENT BEFORE ANYTHING SAYS SO.
        //
        // A run KILLED between doing the work and finishing its turn must still leave its branch on
        // origin. Every arm above reaches this line - a clean finish, a Stop from the board, a spend
        // limit, a runner that threw - because they all converge on `result` and then publish a
        // terminal row. So does a run that dies one step short of pushing.
        //
        // ABOVE THE APPEND, NOT BELOW IT, for the reason `workflow-complete` publishes before
        // `workflow.completed`: anything that reads the terminal row - a test, a sweep, a person -
        // would otherwise be racing the push rather than observing it.
        //
        // `_shutdown.Token`, NOT `run.Token`. A Stop cancels the run, and this must still happen for
        // a run that was stopped: that is the whole case. The Host going down is the one thing that
        // does skip it, and correctly - the process is leaving, and the interrupted-run path in
        // `ContainerHost.ResumePendingAsync` publishes on the way back up.
        var workflowDeclared = false;

        if (_onRunEnding is not null)
        {
            try
            {
                // WHETHER THE TURN WAS FINISHED OR TAKEN AWAY, which is the third thing a handler
                // needs. `TerminalPublish` correctly ignores it - a killed run's branch matters
                // MORE, not less - but the offer of another turn must not wake a
                // member a person has just Stopped, nor nudge a run the budget cut off. The two
                // decisions live in the same moment and disagree about this one fact, so it is
                // passed rather than inferred; every arm above has already converged on `result`.
                //
                // ITS ANSWER IS WHETHER THE PLATFORM DECLARED THIS RUN'S WORKFLOW as it ended, for an
                // owner that cannot declare; the terminal row below says so. See
                // PayloadFields.WorkflowDeclared.
                workflowDeclared = await _onRunEnding(Id, cause, result.Succeeded, _shutdown.Token) && result.Succeeded;
            }
            catch (Exception)
            {
                // A HANDLER'S FAILURE IS NOT THIS RUN'S. Whatever it was doing has its own way of
                // reporting - the publisher writes `repo.pushFailed` - and a run whose terminal row
                // went missing because a push threw would be a container stuck on `running`
                // forever, which is a far worse outcome than an unpublished branch.
            }
        }

        // ONE RUN IS BILLED ONCE. Every delivery in the batch is closed by its own row, but only the
        // first row carries the run's figures; the rest name that row's causation and carry none,
        // and the spend queries skip them. If every row carried the whole run, a Manager that
        // answered two hand-backs in one run would be charged twice.
        long? usageRowCause = null;

        for (var index = 0; index < batch.Count; index++)
        {
            var message = batch[index];

            // NOT CLOSED: delivered again as its own next run, and its pending row says so, so a
            // restart in between re-offers it rather than losing or failing it.
            if (deferredItems.ContainsKey(message.Seq))
            {
                if (_pending is not null)
                {
                    try
                    {
                        await _pending.DeferAsync(Id, message.Seq, runSeq, _shutdown.Token);
                    }
                    catch (Exception) when (!_shutdown.IsCancellationRequested)
                    {
                    }
                }

                _redeliverFrom[message.Seq] = runSeq;
                _redeliveries.Enqueue(message);
                Interlocked.Increment(ref _queueDepth);
                Publish();
                continue;
            }

            if (!blockedItems.Contains(message.Seq))
            {
                var usageCountedOn = usageRowCause;
                var rowUsage = usageCountedOn is null ? usage : null;
                usageRowCause ??= message.Seq;

                var payload = JsonSerializer.Serialize(new
                    {
                        exitCode = result.ExitCode,

                        // Still named `output`: MessageText and the UI card feed both read this
                        // field, and renaming it would silently blank the agent-facing renderer and
                        // the browser at once.
                        output,
                        outputLength = result.Output.Length,
                        transcript = reference,
                        launchError = result.LaunchError,
                        stoppedByPerson = _stoppedByHand && !result.Succeeded,

                        // ONE WAKE PER FINISHED RUN. A worker that handed back has already woken
                        // its manager on that row; this tells the pump not to wake it again on
                        // this one. See PayloadFields.HandedBack.
                        handedBack = _handedBackThisRun,

                        // NEW FIELDS ON THE PAYLOAD THE CALL SITE ALREADY BUILDS, never a second
                        // message type. `completed` and `failed` are ONE JSON object
                        // here, so both carry these keys and a completion writes them null, exactly
                        // as it already writes `launchError` null. Every subscriber and both
                        // renderers go on working without learning anything.
                        failureClass = result.Succeeded ? null : failureClass,
                        retryAfter = result.Succeeded
                            ? null
                            : result.RetryAfter?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                        tokensIn = rowUsage?.TokensIn,
                        tokensOut = rowUsage?.TokensOut,
                        // ONE FIGURE FROM A BRAND THAT REPORTS NO SPLIT, and null for every brand
                        // that does. Its own field rather than a fudged `tokensIn`, so the
                        // projection can sum in/out honestly and still say what a codex run cost.
                        tokensTotal = rowUsage?.Total,
                        tokensCachedIn = rowUsage?.CachedIn,
                        tokensCacheCreation = rowUsage?.CacheCreation,
                        tokensReasoning = rowUsage?.Reasoning,
                        tokensSource = rowUsage?.Source,
                        usageCountedOn,
                    });

                // The agent's own transcript, on the row that carries the run, as ONE key
                // appended to the object above and only when there is one: every other byte of the
                // row is what it was, and a run with none carries no key at all rather than a null.
                if (usageCountedOn is null && result.Transcript is { } agentTranscript)
                {
                    payload = payload[..^1]
                        + $",\"{PayloadFields.AgentTranscript}\":{JsonSerializer.Serialize(agentTranscript.Path)}"
                        + $",\"{PayloadFields.AgentTranscriptFormat}\":{JsonSerializer.Serialize(agentTranscript.Format)}}}";
                }

                // WHAT BECAME OF EACH ITEM, the same way: keys only on a run that carried more than
                // one, so a run of one item writes the row it always wrote.
                if (items is not null)
                {
                    payload = payload[..^1]
                        + $",\"{PayloadFields.Item}\":{index + 1}"
                        + $",\"{PayloadFields.ItemOutcome}\":{JsonSerializer.Serialize(items[index].Outcome)}"
                        + $",\"{PayloadFields.Items}\":{JsonSerializer.Serialize(items, RunItem.Json)}}}";
                }

                // A deferred item's own run says which run it was deferred from.
                if (deferredFrom is { } deferredFromRun)
                {
                    payload = payload[..^1] + $",\"{PayloadFields.DeferredFromRun}\":{deferredFromRun}}}";
                }

                // A QUIET RUN, the same way: one key, only when true, so every other row is byte for
                // byte what it was. The pump wakes nobody on it; see PayloadFields.Quiet.
                //
                // NEVER QUIET TOWARDS A MEMBER THAT IS WAITING. Quiet is for work nobody waits on - a
                // schedule's poll, a trigger, a person's tell. When the delivery this row closes was
                // sent by another member (its source is a member's id: a Manager's `tell`), that
                // member is waiting for the answer, and a quiet row would leave it uninformed and its
                // workflow open. So the key is left off, and the row wakes as any completion does.
                if (result.Succeeded && result.Quiet && !ContainerId.TryParse(message.Source, out _))
                {
                    payload = payload[..^1] + $",\"{PayloadFields.Quiet}\":true}}";
                }

                // A WORKFLOW THE PLATFORM DECLARED AS THIS RUN ENDED, the same way: one key, only
                // when true. It is closed, so the pump passes over the Manager on it; see
                // PayloadFields.WorkflowDeclared. Every item of the run must be in that workflow -
                // a later row of a batch is passed over with the first - and, as for quiet, never
                // towards a member waiting on its own `tell`, nor on a trigger's run, whose own
                // wake choice below governs.
                if (workflowDeclared
                    && batch.All(item => item.CorrelationId == batch[0].CorrelationId)
                    && batch.All(item => !ContainerId.TryParse(item.Source, out _) && !WakeManagerPolicy.IsTriggerSource(item.Source)))
                {
                    payload = payload[..^1] + $",\"{PayloadFields.WorkflowDeclared}\":true}}";
                }

                // THE TRIGGER'S WAKE CHOICE, the same way: one key, only when the delivery this row
                // closes is a trigger's fire that chose other than `always`. The pump passes over
                // the Manager on it; see WakeManagerPolicy. A Manager's `tell` and a person's never
                // carry it, so work somebody is waiting on wakes as it always has.
                if (WakeManagerPolicy.OfInstruction(message.Source, message.Payload) is { } wakeManager)
                {
                    payload = payload[..^1]
                        + $",\"{PayloadFields.WakeManager}\":{JsonSerializer.Serialize(wakeManager)}}}";
                }

                // Set BESIDE the publication rather than instead of it, and only on the arm that
                // chose Failed. Idempotent, so a batch of several messages marks the container once
                // and pushes one frame. BEFORE THE APPEND, not after it and not after the terminal
                // hook: whoever sees the Failed row may read the reason at once. Marked after the
                // append, a reader that polled the row in the moment before this continuation ran -
                // only ever under load - read the row and a null `failed`.
                if (!result.Succeeded) MarkFailed(failure, failureClass);

                var terminal = await SafeAppendAsync(new NewMessage(
                    result.Succeeded ? MessageTypes.Completed : MessageTypes.Failed,
                    payload,
                    Id.ToString(),
                    message.Seq));

                if (usageCountedOn is null && terminal is not null && _onTerminal is not null)
                {
                    try
                    {
                        await _onTerminal(terminal, _shutdown.Token);
                    }
                    catch (Exception) when (!_shutdown.IsCancellationRequested)
                    {
                        // Whatever it was reading has its own way of saying it could not; the run
                        // has already been recorded, and must not be lost to a reader's failure.
                    }
                }
            }

            // AFTER the publish: this window can double-report, and the alternative window goes
            // silent.
            if (_pending is not null)
            {
                try
                {
                    await _pending.RemoveAsync(Id, message.Seq, _shutdown.Token);
                }
                catch (Exception) when (!_shutdown.IsCancellationRequested)
                {
                }
            }
        }
    }

    /// <summary>
    /// Publishing must never break the consumer. A log write that fails would otherwise leave the
    /// container wedged mid-message with its queue stalled - a far worse outcome than a missing
    /// signal, which is recoverable by looking.
    /// </summary>
    private async Task<Message?> SafeAppendAsync(NewMessage message)
    {
        try
        {
            return await _log.AppendAsync(message, _shutdown.Token);
        }
        catch (Exception) when (!_shutdown.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>One entry of <see cref="PayloadFields.Items"/>. <c>reason</c> is written only for a
    /// deferral, which has no row of its own to carry it.</summary>
    private sealed record RunItem(int Item, long Seq, string Outcome, string? Reason)
    {
        public static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    private void Publish()
    {
        try
        {
            Changed?.Invoke(Snapshot());
        }
        catch
        {
            // A throwing subscriber (a UI handler, say) must never break the consumer loop.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _shutdown.CancelAsync();

        try
        {
            await _consumer.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Best effort: a stuck agent must not hang shutdown.
        }

        _shutdown.Dispose();
    }
}
