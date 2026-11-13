using System.Text.Json;
using System.Threading.Channels;
using Yawble.Core.Scheduling;
using Yawble.Core.Triggers;

namespace Yawble.Core.Bus;

public sealed class BusOptions
{
    /// <summary>app.maxConcurrentAgents, read at every admission. The core passes Scheduling.PolicyRunLimit (the global policy).</summary>
    public Func<int> MaxConcurrentAgents { get; init; } = () => RunScheduler.DefaultMaxConcurrentAgents;

    /// <summary>app.heavyWorkAtOnce: how many runs may hold the heavy-work lease at once (1-8), read at every lease call.</summary>
    public Func<int> HeavyWorkAtOnce { get; init; } = () => 1;

    /// <summary>The causation depth limit. 0 turns it off.</summary>
    public int DepthLimit { get; init; } = 200;

    /// <summary>Tell a workflow's owner, once, when its run ends with the workflow open and nobody working it (on by default).</summary>
    public bool IdleOffer { get; init; } = true;

    /// <summary>The most deliveries of one workflow in one run.</summary>
    public int BatchLimit { get; init; } = 16;

    /// <summary>The pump is woken in process after every commit; this poll is only the safety net.</summary>
    public TimeSpan SafetyPoll { get; init; } = TimeSpan.FromSeconds(2);

    public Action<string>? Log { get; init; }

    /// <summary>
    /// The agent CLIs on this computer and whether each can run now (the Agents dialog's detection), for the Manager's
    /// <c>hiring</c> tool. Null in tests and when the core has no detector: then every preset is offered unchecked.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<AgentChoice>>>? AvailableAgents { get; init; }

    /// <summary>The team repositories (the Git dialog's actions). Null in a core without an app data folder.</summary>
    public Teams.TeamRepos? Repos { get; init; }

    /// <summary>The skills agents load with skills_list / skills_get. Null: only the manager and member role texts.</summary>
    public Skills.SkillCatalog? Skills { get; init; }
}

/// <summary>One agent CLI the Manager may hire: its preset id, whether it can run here now, and why not.</summary>
public sealed record AgentChoice(string Cli, bool Ready, string Detail);

/// <summary>
/// The bus and the member runtimes for every team, in one process.
///
/// One gate serialises every state change, so the pump, the scheduler, run endings and tool calls never
/// interleave. Runners execute outside the gate and call back in through <see cref="RunContext"/>.
///
/// Delivery: the pump writes a pending delivery in the same transaction that moves the cursor;
/// a member with undelivered work enters the global run_queue; the scheduler admits the oldest entry whenever a
/// slot is free; admission takes a batch of up to 16 deliveries of the oldest workflow and marks them started.
/// A restart reports every run left open as interrupted (at most once after a run starts) and re-offers
/// deliveries that never started (at least once until then). The queue is a table, so it keeps its order.
/// </summary>
public sealed partial class BusEngine : IAsyncDisposable
{
    private readonly BusStore _store;
    private readonly IAgentRunner _runner;
    private readonly BusOptions _options;

    /// <summary>The team repositories (the Git dialog). Null in a core without an app data folder.</summary>
    public Teams.TeamRepos? Repos => _options.Repos;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<long, ActiveRun> _active = [];
    private readonly Dictionary<long, string> _statusLines = [];
    private Task? _pump;
    private volatile bool _stopping;

    private sealed record ActiveRun(RunContext Context, CancellationTokenSource Cancel, Task Task);

    public BusEngine(BusStore store, IAgentRunner runner, BusOptions? options = null)
    {
        _store = store;
        _runner = runner;
        _options = options ?? new BusOptions();
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Recovers from the last process (interrupted runs, re-offered deliveries), then starts the pump.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await WithGate(() =>
        {
            SeedStatusLines();
            Recover();
            Pump();
            Schedule();
        }, ct);
        _pump = Task.Run(PumpLoopAsync, CancellationToken.None);
    }

    /// <summary>
    /// Stops the pump and cancels every run WITHOUT recording an outcome, as a process exit would. Runs left open
    /// are reported interrupted by the next <see cref="StartAsync"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        await _stop.CancelAsync();
        Task[] tasks;
        await _gate.WaitAsync();
        try
        {
            foreach (var run in _active.Values) await run.Cancel.CancelAsync();
            tasks = [.. _active.Values.Select(r => r.Task), .. StopSessionsForShutdown(), .. _pump is null ? [] : new[] { _pump }];
        }
        finally
        {
            _gate.Release();
        }
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // A runner that ignores cancellation must not keep the core from stopping.
        }
        _store.Dispose();
    }

    /// <summary>
    /// The status lines live in memory; after a restart each member's card shows its last report from the log again,
    /// worded as the live code words it (<see cref="StatusLineOf"/>). One bounded query per member.
    /// </summary>
    private void SeedStatusLines()
    {
        foreach (var member in _store.AllMembers())
        {
            if (_statusLines.ContainsKey(member.Id)) continue;
            if (_store.LastStatusReport(member) is { } report && StatusLineOf(report) is { Length: > 0 } line) _statusLines[member.Id] = line;
        }
    }

    /// <summary>The status line a message of <see cref="BusStore.StatusLineTypes"/> set when it was published.</summary>
    internal static string? StatusLineOf(Message m)
    {
        string? Field(string name)
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Payload);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v)
                    && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return m.Type switch
        {
            MessageTypes.Handback => Field("delivered") is { } d ? "handed back: " + d : null,
            MessageTypes.Blocked => Field("reason") is { } r ? "blocked: " + r : null,
            MessageTypes.Progress => Field("status"),
            MessageTypes.NeedsDecision => Field("question") is { } q ? "needs decision: " + q : null,
            MessageTypes.WorkflowCompleted => Field("delivered") is { } d ? "declared complete: " + d : null,
            _ => null,
        };
    }

    private void Recover()
    {
        var interrupted = new List<(MemberRef, Message)>();
        foreach (var run in _store.Runs("running"))
        {
            var member = _store.Member(run.MemberId);
            _store.InTransaction(() =>
            {
                const string reason = "interrupted: the core stopped while this run was running";
                _store.EndRun(run.Id, "interrupted", reason);
                var failed = _store.Append(new NewMessage(MessageTypes.Failed,
                    JsonSerializer.Serialize(new { runId = run.Id, items = run.Items, reason, failureClass = FailureClasses.Interrupted }),
                    member?.Qualified ?? MessageTypes.HostSource, run.CausationSeq));
                _store.RemovePendingOfRun(run.Id);
                _store.SetFailureClass(run.Id, FailureClasses.Interrupted);
                if (member is not null) interrupted.Add((member, failed));
            });
            Log($"run {run.Id} of {member?.Qualified} marked interrupted");
        }
        WakeCutOffManagers(interrupted);
        RecoverSessions();
    }

    // ------------------------------------------------------------------ teams (the UI's operations)

    public async Task<long> CreateTeamAsync(string name, string managerName = "Manager", CancellationToken ct = default)
    {
        MemberRow? manager = null;
        var id = await WithGate(() => _store.InTransaction(() =>
        {
            var team = _store.CreateTeam(name);
            var m = AddMember(team, managerName, MemberRole.Manager, "claude");
            manager = _store.MemberRows(memberId: m.Id).Single();
            return team;
        }), ct);
        LogFirstAgent(manager!, Agents.Profiles.AgentChangeLog.HowTeamCreate);
        return id;
    }

    public async Task<MemberRef> AddMemberAsync(string team, string name, string cli = "claude", CancellationToken ct = default)
    {
        var m = await WithGate(() => _store.InTransaction(() => AddMember(TeamId(team), name, MemberRole.Member, cli)), ct);
        LogFirstAgent(_store.MemberRows(memberId: m.Id).Single(), HowNow());
        return m;
    }

    private long TeamId(string team) =>
        _store.AllMembers().FirstOrDefault(m => string.Equals(m.Team, team, StringComparison.OrdinalIgnoreCase))?.TeamId
        ?? throw new BusRefusal("no_such_team", $"there is no team named '{team}'");

    private MemberRef AddMember(long teamId, string name, MemberRole role, string cli)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) throw new BusRefusal("bad_name", "a member name is non-empty and has no '/'");
        var member = _store.AddMember(teamId, name.Trim(), role, cli);
        _store.Subscribe(member.Qualified, MessageTypes.Instruction(member));
        if (role == MemberRole.Manager)
        {
            foreach (var type in MessageTypes.ManagerWatches) _store.Subscribe(member.Qualified, type);
        }
        _store.InitCursor(member.Qualified);
        return member;
    }

    /// <summary>
    /// The person tells a member of a team. With no causation this roots a new workflow (depth 0); with one (a
    /// needs_decision answer) it joins that workflow.
    /// </summary>
    public Task<Message> PersonTellAsync(string team, string member, string text, long? causation = null, CancellationToken ct = default,
        System.Text.Json.Nodes.JsonObject? extra = null) =>
        WithGate(() =>
        {
            var target = _store.Member(team, member) ?? throw new BusRefusal("no_such_member", $"'{team}' has no member named '{member}'");
            if (_store.Teams().FirstOrDefault(t => t.Id == target.TeamId) is { Archived: true } archived)
                throw new BusRefusal("archived", $"'{archived.Name}' is archived, and an archived team does no work. Nothing was sent. Unarchive it first (it comes back paused), then resume it.");
            if (causation is { } cause) CheckDepth(cause);
            return Publish(new NewMessage(MessageTypes.Instruction(target), BatchText.InstructionPayload(text, extra), MessageTypes.PersonSource, causation));
        }, ct);

    // ------------------------------------------------------------------ the tools, identity from the run

    internal Task<Message> TellAsync(RunContext run, string to, string text, CancellationToken ct, System.Text.Json.Nodes.JsonObject? extra = null) =>
        WithGate(() =>
        {
            EnsureActive(run);
            var target = _store.Member(run.Member.Team, to)
                ?? throw new BusRefusal("no_such_member", $"your team has no member named '{to}'");
            if (target.Id == run.Member.Id) throw new BusRefusal("self", "a member cannot tell itself");
            if (run.Member.Role == MemberRole.Member && target.Role != MemberRole.Manager)
            {
                throw new BusRefusal("not_allowed", "members may tell only the Manager; telling a member is the Manager's");
            }
            CheckDepth(run.Causation);
            return Publish(new NewMessage(MessageTypes.Instruction(target), BatchText.InstructionPayload(text, extra), run.Member.Qualified, run.Causation));
        }, ct);

    /// <summary>The board's service (Board/BoardService.cs), for the Manager's board and backlog tools; set at start.</summary>
    public Board.BoardService? Board { get; set; }

    /// <summary>The person's integrations (their CLIs under rules), offered to agents as tools; null when not wired.</summary>
    public Integrations.IntegrationService? Integrations { get; set; }

    /// <summary>The network asks (hosts an agent was refused or asked for), behind the request_host tool; null when not wired.</summary>
    public Integrations.NetworkAskService? NetworkAsks { get; set; }

    internal Task<Message> HandbackAsync(RunContext run, string delivered, CancellationToken ct) =>
        WithGate(() =>
        {
            EnsureActive(run);
            if (run.Member.Role == MemberRole.Manager)
            {
                throw new BusRefusal("not_allowed", "the Manager has nobody to hand back to; declare with workflow_complete");
            }
            if (string.IsNullOrWhiteSpace(delivered)) throw new BusRefusal("empty", "say what you delivered");
            _statusLines[run.Member.Id] = "handed back: " + delivered;
            run.HandedBack = true;
            return Publish(new NewMessage(MessageTypes.Handback, JsonSerializer.Serialize(new { delivered }), run.Member.Qualified, run.Causation));
        }, ct);

    /// <summary>
    /// blocked: gives up the whole run, or with <paramref name="item"/> just that numbered item of the batch. The
    /// item's instruction is carried in the report and in the run's ending notice, so the Manager sees which card.
    /// </summary>
    internal Task<Message> BlockedAsync(RunContext run, string reason, int? item, CancellationToken ct) =>
        WithGate(() =>
        {
            EnsureActive(run);
            if (string.IsNullOrWhiteSpace(reason)) throw new BusRefusal("empty", "say why you are blocked");
            if (item is { } n && (n < 1 || n > run.Items.Count))
            {
                throw new BusRefusal("no_such_item", run.Items.Count == 1
                    ? "this run has one instruction, not numbered items: call blocked without item"
                    : $"item must be 1 to {run.Items.Count}, the numbered items of this run");
            }
            string? instruction = item is { } i ? BatchText.TextOf(run.Items[i - 1]) : null;
            run.Blocked.Add(new BlockedItem(item, instruction, reason));
            _statusLines[run.Member.Id] = "blocked: " + reason;
            return Publish(new NewMessage(MessageTypes.Blocked, JsonSerializer.Serialize(new { reason, item, instruction }), run.Member.Qualified, run.Causation));
        }, ct);

    internal Task<Message> ReportAsync(RunContext run, string type, object payload, CancellationToken ct, string statusLine) =>
        WithGate(() =>
        {
            EnsureActive(run);
            _statusLines[run.Member.Id] = statusLine;
            return Publish(new NewMessage(type, JsonSerializer.Serialize(payload), run.Member.Qualified, run.Causation));
        }, ct);

    /// <summary>
    /// The declaration rules: only the member the workflow's root instruction addressed, or its team's Manager on its behalf, may declare;
    /// only once; and never while other work of the workflow is running or pending.
    /// </summary>
    internal async Task<Message> WorkflowCompleteAsync(RunContext run, string delivered, CancellationToken ct)
    {
        // Before the declaration: does the team branch merge cleanly into the base? A conflict does not stop the
        // declaration; it rides on it, so the work is not reported as ready to land.
        var check = string.IsNullOrWhiteSpace(delivered) ? null : await MergeCheckForAsync(run.Member, ct);
        return await CompleteAsync(run, delivered, check, ct);
    }

    private async Task<Teams.TeamMergeCheck?> MergeCheckForAsync(MemberRef member, CancellationToken ct)
    {
        if (Repos is not { } repos) return null;
        try { return await repos.MergeCheckAsync(member.TeamId, member.Team, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log($"team {member.Team}: could not check that its branch merges cleanly: {e.Message}");
            return null;
        }
    }

    private Task<Message> CompleteAsync(RunContext run, string delivered, Teams.TeamMergeCheck? check, CancellationToken ct) =>
        WithGate(() =>
        {
            EnsureActive(run);
            if (string.IsNullOrWhiteSpace(delivered)) throw new BusRefusal("empty", "say what was delivered");
            var correlation = run.Correlation;
            var manager = _store.TeamMembers(run.Member.TeamId).Single(m => m.Role == MemberRole.Manager);
            var owner = OwnerOf(correlation) ?? manager;
            var onBehalf = owner.Id != run.Member.Id && run.Member.Id == manager.Id && owner.TeamId == run.Member.TeamId;
            if (owner.Id != run.Member.Id && !onBehalf)
            {
                throw new BusRefusal("not_owner",
                    $"this workflow was addressed to '{owner.Qualified}', so only that member, or its team's Manager on its behalf, may declare it completed");
            }
            if (_store.WorkflowEnded(correlation))
            {
                throw new BusRefusal("already_declared", "this workflow has already ended (declared complete, or closed by the person); end your turn, do not retry");
            }
            var busy = Busy(run.Member, correlation, watcher: run.Member.Id == manager.Id ? null : manager);
            if (busy.Count != 0)
            {
                throw new BusRefusal("busy", "workflow completion refused because work is still in flight under this workflow: " + string.Join("; ", busy));
            }
            _statusLines[run.Member.Id] = "declared complete: " + delivered;
            var conflict = check is { Clean: false } ? check : null;
            if (conflict is not null) delivered += $"\n\nNot ready to land: {conflict.Text}.";
            return Publish(new NewMessage(MessageTypes.WorkflowCompleted,
                JsonSerializer.Serialize(new
                {
                    delivered, declaredBy = run.Member.Qualified, onBehalfOf = onBehalf ? owner.Qualified : null,
                    mergeConflict = conflict is null ? null : new { into = conflict.BaseBranch, files = conflict.Files },
                }),
                run.Member.Qualified, run.Causation));
        }, ct);

    internal Task<IReadOnlyList<MemberStatus>> TeamStatusAsync(long teamId, CancellationToken ct) =>
        WithGate(() => TeamStatus(teamId), ct);

    public Task<IReadOnlyList<MemberStatus>> TeamStatusAsync(string team, CancellationToken ct = default) =>
        WithGate(() => TeamStatus(TeamId(team)), ct);

    private IReadOnlyList<MemberStatus> TeamStatus(long teamId)
    {
        var queue = _store.Queue();
        var unstarted = _store.Pending(started: false);
        TidyLease(_store.Clock.GetUtcNow());
        return _store.TeamMembers(teamId).Select(m =>
        {
            var position = RunScheduler.PositionOf(queue, q => q.MemberId == m.Id);
            var state = _active.Values.Any(a => a.Context.Member.Id == m.Id) ? "running"
                : _sessions.ContainsKey(m.Id) ? "session" : position is not null ? "queued" : "idle";
            var waiting = unstarted.Count(p => string.Equals(p.Subscriber, m.Qualified, StringComparison.OrdinalIgnoreCase));
            return new MemberStatus(m.Name, m.Role == MemberRole.Manager ? "manager" : "member", state, position, waiting,
                _statusLines.GetValueOrDefault(m.Id),
                LeaseOf(m.Id) is { } l ? (l.State == "holding" ? $"holds the heavy-work lease for \"{l.What}\"" : $"waiting for the heavy-work lease (position {l.Position})") : null);
        }).ToArray();
    }

    /// <summary>True when nothing is running, queued, undelivered or unpumped. For tests and the UI's "idle".</summary>
    public Task<bool> IsQuiescentAsync(CancellationToken ct = default) =>
        WithGate(() => _active.Count == 0 && _sessions.Count == 0 && _sessionWaiters.Count == 0 && _store.Queue().Count == 0 && _store.Pending(started: false).Count == 0
            && _store.Subscriptions().Keys.All(s => _store.Cursor(s) >= _store.MaxSeq()), ct);

    // ------------------------------------------------------------------ rules

    private void EnsureActive(RunContext run)
    {
        if (!_active.ContainsKey(run.RunId)) throw new BusRefusal("run_ended", "this run has ended; its tools no longer act");
    }

    private void CheckDepth(long cause)
    {
        var parent = _store.Find(cause) ?? throw new BusRefusal("no_such_cause", $"message {cause} does not exist");
        if (_options.DepthLimit > 0 && parent.Depth + 1 > _options.DepthLimit)
        {
            throw new BusRefusal("depth_limit",
                $"refused: this would be hop {parent.Depth + 1} of one causation chain, over the limit of {_options.DepthLimit}");
        }
    }

    /// <summary>The member a workflow's root instruction addressed (its owner); null when none was.</summary>
    private MemberRef? OwnerOf(long correlation)
    {
        var root = _store.Find(correlation);
        if (root is null || !root.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)) return null;
        return _store.MemberBySubscriber(root.Type[MessageTypes.InstructionPrefix.Length..]);
    }

    /// <summary>What still works a workflow, from the team's runs and pending deliveries (one sentence each).</summary>
    private List<string> Busy(MemberRef declarer, long correlation, MemberRef? watcher)
    {
        var busy = new List<string>();
        foreach (var run in _active.Values.Select(a => a.Context))
        {
            if (run.Member.TeamId != declarer.TeamId || run.Member.Id == declarer.Id || run.Correlation != correlation) continue;
            if (watcher is not null && run.Member.Id == watcher.Id && Watching(run.Items[0], declarer.TeamId, correlation, watcher)) continue;
            busy.Add($"{run.Member.Name} is running");
        }
        var team = _store.TeamMembers(declarer.TeamId).ToDictionary(m => m.Qualified, StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _store.Pending())
        {
            if (row.Correlation != correlation || !team.TryGetValue(row.Subscriber, out var holder)) continue;
            if (holder.Id == declarer.Id && row.Started) continue;
            if (watcher is not null && holder.Id == watcher.Id && _store.Find(row.Seq) is { } m && Watching(m, declarer.TeamId, correlation, watcher)) continue;
            counts[holder.Name] = counts.GetValueOrDefault(holder.Name) + 1;
        }
        busy.AddRange(counts.Select(kv => $"{kv.Key} has {kv.Value} pending deliver{(kv.Value == 1 ? "y" : "ies")} in this workflow"));
        return busy;
    }

    /// <summary>A report row a watching Manager holds about another member's work: it never blocks the owner.</summary>
    private bool Watching(Message row, long teamId, long correlation, MemberRef watcher) =>
        row.CorrelationId == correlation
        && !row.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
        && _store.MemberBySubscriber(row.Source) is { } source && source.TeamId == teamId && source.Id != watcher.Id;

    // ------------------------------------------------------------------ the pump

    private async Task PumpLoopAsync()
    {
        while (!_stopping)
        {
            try
            {
                await WithGate(() =>
                {
                    if (_stopping) return;
                    SweepTriggersIfDue();
                    Pump();
                    if (OfferIdleReminders()) Pump();
                    Schedule();
                    TidyLease(_store.Clock.GetUtcNow()); // the 30-minute limit, and a raised heavyWorkAtOnce
                }, _stop.Token);
            }
            catch (OperationCanceledException) when (_stopping)
            {
                break;
            }
            catch (Exception ex)
            {
                // The net under the net: one bad pass must not kill delivery for good.
                Log($"pump pass failed: {ex}");
            }
            try
            {
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                poll.CancelAfter(_options.SafetyPoll);
                await _wake.Reader.ReadAsync(poll.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void Signal() => _wake.Writer.TryWrite(true);

    private Message Publish(NewMessage message)
    {
        var row = _store.Append(message);
        Signal();
        return row;
    }

    /// <summary>Moves every subscriber's cursor to the end of the log, writing its pending deliveries first.</summary>
    private void Pump()
    {
        var end = _store.MaxSeq();
        foreach (var (subscriber, types) in _store.Subscriptions())
        {
            var position = _store.Cursor(subscriber);
            if (position >= end) continue;
            _store.InTransaction(() =>
            {
                foreach (var message in _store.MessagesAfter(position, end, types))
                {
                    if (Deliverable(subscriber, message)) _store.AddPending(subscriber, message.Seq);
                }
                _store.AdvanceCursor(subscriber, end);
            });
        }
    }

    /// <summary>
    /// Instructions are addressed by type. The global report types wake a subscriber only when the publisher is
    /// another member of the same team: messages never
    /// cross teams, and a Manager is never woken by its own row. A trigger's run that ends under its wake choice
    /// (WakeManagerPolicy) passes over the Manager, the only subscriber of the run-ending types.
    /// </summary>
    private static bool Deliverable(string subscriber, Message message)
    {
        if (message.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)) return true;
        if (string.Equals(message.Source, subscriber, StringComparison.OrdinalIgnoreCase)) return false;
        if (WakeManagerPolicy.PassesOver(message)) return false;
        var team = subscriber[..(subscriber.IndexOf('/') + 1)];
        return message.Source.StartsWith(team, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the scheduler

    private void Schedule()
    {
        EnqueueEligible();
        var limit = _options.MaxConcurrentAgents();
        // Every sandbox holds a slot: member runs, Manager runs and interactive sessions.
        foreach (var entry in RunScheduler.Admit(SlotsInUse, limit, _store.Queue()))
        {
            if (entry.Kind == QueueKinds.Interactive) AdmitSession(entry);
            else Admit(entry);
        }
    }

    /// <summary>Every member with undelivered work and no run joins the back of the queue, oldest work first.</summary>
    private void EnqueueEligible()
    {
        var waiting = _store.Pending(started: false)
            .Where(Ripe)
            .GroupBy(p => p.Subscriber, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Min(p => p.Seq));
        var held = _store.HeldTeams();
        foreach (var group in waiting)
        {
            if (_store.MemberBySubscriber(group.Key) is not { } member) continue;
            if (Held(member, held)) continue; // a paused or archived team's work waits where it is
            if (_active.Values.Any(a => a.Context.Member.Id == member.Id)) continue;
            if (HasSession(member.Id)) continue; // its instructions wait while a session is open or queued
            _store.Enqueue(member.Id); // INSERT OR IGNORE: a queued member keeps its place
        }
    }

    /// <summary>
    /// A hand-back waits for the end of the run that sent it: the host fetches the member's branch back (and pushes)
    /// only after the run, so a Manager woken by the hand-back alone would find the work not yet in the shared view
    /// and spend a run learning to wait. Once the run ends, the hand-back and the run's ending row reach the Manager
    /// together, in one run.
    /// </summary>
    private bool Ripe(BusStore.PendingRow row)
    {
        if (_store.Find(row.Seq) is not { } m || m.Type != MessageTypes.Handback) return true;
        return !_active.Values.Any(a => string.Equals(a.Context.Member.Qualified, m.Source, StringComparison.OrdinalIgnoreCase));
    }

    private void Admit(BusStore.QueueRow entry)
    {
        var member = _store.Member(entry.MemberId);
        var admitted = _store.InTransaction(() =>
        {
            _store.Dequeue(entry.Id);
            if (member is null) return ((long, List<BusStore.PendingRow>)?)null;
            var unstarted = _store.Pending(member.Qualified, started: false).Where(Ripe).ToList();
            if (unstarted.Count == 0) return null;
            var first = unstarted[0];
            var batch = unstarted.Where(p => p.Correlation == first.Correlation).Take(Math.Max(1, _options.BatchLimit)).ToList();
            var runId = _store.InsertRun(member.Id, entry.Id, first.Seq, first.Correlation, batch.Count, entry.EnqueuedAt);
            foreach (var row in batch) _store.StartPending(member.Qualified, row.Seq, runId);
            return (runId, batch);
        });
        if (admitted is not var (runId, rows)) return;
        var items = rows.Select(r => _store.Find(r.Seq)!).ToArray();
        var context = new RunContext(this, runId, member!, items)
        {
            Instructions = _store.MemberRows(memberId: member!.Id).SingleOrDefault()?.Instructions ?? "",
            Approvals = member.Role == MemberRole.Manager ? [.. _store.Approvals(member.TeamId).Select(a => a.Text)] : [],
            // What this member already asked the person in this workflow and is still waiting on: a run woken by
            // something else (a second member's hand-back) must not ask the same thing again.
            OpenQuestions = [.. _store.OpenDecisions(member.Team)
                .Where(q => q.CorrelationId == items[0].CorrelationId && string.Equals(q.Source, member.Qualified, StringComparison.OrdinalIgnoreCase))
                .Select(q => (q.Seq, Text(q, "question") ?? ""))],
        };
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            await started.Task;
            await ExecuteAsync(context, cancel);
        });
        _active[runId] = new ActiveRun(context, cancel, task);
        started.SetResult();
        Log($"run {runId} admitted: {member!.Qualified}, {items.Length} item(s), workflow {context.Correlation}");
    }

    private async Task ExecuteAsync(RunContext run, CancellationTokenSource cancel)
    {
        RunResult? result;
        try
        {
            result = await _runner.RunAsync(run, cancel.Token);
        }
        catch (OperationCanceledException) when (_stopping)
        {
            result = null;
        }
        catch (OperationCanceledException) when (StopWasRequested(run.RunId))
        {
            result = new RunResult(false, StoppedReason, FailureClasses.Stopped);
        }
        catch (Exception ex)
        {
            result = RunResult.Fail("the runner failed: " + ex.Message);
        }
        await _gate.WaitAsync();
        try
        {
            _active.Remove(run.RunId);
            ReleaseLeaseOf(run.RunId);
            _stopRequested.TryRemove(run.RunId, out _);
            if (_stopping || result is null) return; // left open: the next start reports it interrupted
            _store.InTransaction(() =>
            {
                _store.EndRun(run.RunId, result.Succeeded ? "completed" : "failed", result.Reason);
                var wakeManager = WakeManagerPolicy.OfRun(run.Items);
                // A member that ends without handing back but asked to be woken is not stuck: say when it comes back.
                var wake = run.Member.Role == MemberRole.Member && !run.HandedBack ? _store.NextWake(run.Member.Id) : null;
                _store.Append(new NewMessage(result.Succeeded ? MessageTypes.Completed : MessageTypes.Failed,
                    WakeManagerPolicy.WithChoice(JsonSerializer.Serialize(new
                    {
                        runId = run.RunId,
                        items = run.Items.Count,
                        reason = result.Reason,
                        failureClass = result.Succeeded ? null : result.FailureClass ?? FailureClasses.Failed,
                        run = result.Details,
                        handedBack = run.Member.Role == MemberRole.Member ? run.HandedBack : (bool?)null,
                        wake = wake is null ? null : new { at = wake.DueAt.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture), note = wake.Note },
                        blocked = run.Blocked.Select(b => new { item = b.Item, instruction = b.Instruction, reason = b.Reason }).ToArray(),
                    }), wakeManager),
                    run.Member.Qualified, run.Causation));
                _store.RemovePendingOfRun(run.RunId);
                if (wakeManager is not null && result.Succeeded) DeclareQuietTriggerWorkflow(run);
                if (!result.Succeeded) _store.SetFailureClass(run.RunId, result.FailureClass ?? FailureClasses.Failed);
            });
            // The idle reminder is decided in the next pump pass, after what the run published has been delivered.
            if (result.Succeeded) _endedRuns.Add(run);
            Signal();
        }
        finally
        {
            cancel.Dispose();
            _gate.Release();
        }
    }

    /// <summary>
    /// A trigger's run that ends well without handing back, under a wake choice that does not wake the Manager for it,
    /// leaves nobody to declare its workflow: the app declares it (declaredByPlatform), when nothing else is
    /// working the workflow and nobody declared it in the run. A hand-back leaves it to the Manager it wakes.
    /// </summary>
    private void DeclareQuietTriggerWorkflow(RunContext run)
    {
        var correlation = run.Correlation;
        if (run.HandedBack || _store.WorkflowDeclared(correlation)) return;
        if (Busy(run.Member, correlation, watcher: null).Count != 0) return;
        var delivered = $"{run.Member.Name}'s scheduled run finished without handing anything back; the app closed this workflow " +
            "(the trigger wakes the Manager only for a hand-back or a failure).";
        _store.Append(new NewMessage(MessageTypes.WorkflowCompleted,
            JsonSerializer.Serialize(new { delivered, declaredBy = MessageTypes.HostSource, declaredByPlatform = true, onBehalfOf = run.Member.Qualified }),
            MessageTypes.HostSource, run.Causation));
        Log($"workflow {correlation}: declared by the app after {run.Member.Qualified}'s quiet scheduled run");
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<T> WithGate<T>(Func<T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return work();
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task WithGate(Action work, CancellationToken ct) => WithGate(() => { work(); return 0; }, ct);

    private void Log(string line) => _options.Log?.Invoke(line);

    /// <summary>The skills offered to a run's member (its role, its team).</summary>
    internal IReadOnlyList<Skills.Skill> SkillsFor(MemberRef member)
    {
        var role = member.Role == MemberRole.Manager ? "manager" : "member";
        return _options.Skills?.Offered(role, member.TeamId)
            ?? Skills.SkillCatalog.BuiltIns.Where(x => x.Kind == Skills.SkillKinds.BuiltIn && x.Name is "manager" or "member" && x.OfferedTo(role)).ToList();
    }

    internal Skills.Skill SkillFor(MemberRef member, string name)
    {
        var offered = SkillsFor(member);
        return offered.FirstOrDefault(x => string.Equals(x.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"no skill named '{name}' is offered to you. Offered: {string.Join(", ", offered.Select(x => x.Name))}");
    }
}
