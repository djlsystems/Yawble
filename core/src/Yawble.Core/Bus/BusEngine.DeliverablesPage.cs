using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Bus;

/// <summary>The backlog item a deliverable was dispatched as. <paramref name="Spec"/>: its body (deliverables.get only).</summary>
public sealed record DeliverableItemRef(long Id, string Label, string Title, bool Archived, string Spec);

/// <summary>
/// A member working a deliverable now: running (its run is active), lease (its run waits for the heavy-work lease), or one of
/// <see cref="WaitStates"/> for work not started yet. <paramref name="WaitsFor"/>: with waits-for-run, the name of the member
/// whose run must end first (the hand-back's sender); null otherwise.
/// </summary>
public sealed record DeliverableWorker(long Id, string Name, string State, string? WaitsFor = null);

/// <summary>
/// Why a member's undelivered work has not started (the Deliverables page's members, team status, the timeline).
/// Only <see cref="Queued"/> is a wait for a run slot.
/// </summary>
public static class WaitStates
{
    /// <summary>queued: the member is in the run queue, waiting for a run slot.</summary>
    public const string Queued = "queued";

    /// <summary>waits-for-run: a hand-back held until the run that sent it ends (and its branch is fetched back).</summary>
    public const string WaitsForRun = "waits-for-run";

    /// <summary>held: the member's team is paused or archived.</summary>
    public const string Held = "held";

    /// <summary>pending: anything else (the member runs in another deliverable, has a session open, waits to retry a start, ...).</summary>
    public const string Pending = "pending";

    public static readonly IReadOnlyList<string> All = [Queued, WaitsForRun, Held, Pending];

    /// <summary>The state in words (the companion's and the Deliverables page's wording).</summary>
    public static string Text(string state, string? waitsFor) => state switch
    {
        Queued => "queued for a run slot",
        WaitsForRun => $"waits for {waitsFor ?? "the sender"}'s run to end",
        Held => "waits: team paused",
        _ => "has work waiting",
    };
}

/// <summary>
/// One row of the Deliverables page: the team's DeliverableSummary with its clean title, plus
/// its team, the Status column's word, who works it now and the backlog item it came from.
/// </summary>
public sealed record DeliverableRow(DeliverableSummary Summary, string Team, long TeamId, long? OwnerId, string Status, string? WaitingFor,
    bool Working, DeliverableItemRef? Item, IReadOnlyList<DeliverableWorker> Members);

/// <summary>deliverables.list's answer: the rows, the chips' counts (team and text filters only), whether rows were cut, and the core's now.</summary>
public sealed record DeliverablesPage(IReadOnlyList<DeliverableRow> Rows, IReadOnlyDictionary<string, int> Counts, bool Truncated, DateTimeOffset Now);

/// <summary>
/// The Deliverables page: every team's deliverables in one list. Read in a fixed number of statements however many
/// deliverables there are (the heads of all of them in one, then the threads, wakes, expiry rows, items and times of the
/// ones shown in one each): no per-deliverable query.
/// </summary>
public sealed partial class BusEngine
{
    public const int DefaultPageLimit = 200, MaxPageLimit = 1000;

    /// <summary>The Status column's words.</summary>
    public static class DeliverableStatuses
    {
        public const string Running = "running", NeedsYou = "needs-you", Waiting = "waiting", Idle = "idle", Stopped = "stopped",
            Completed = "completed", Closed = "closed", Expired = "expired";

        public static readonly IReadOnlyList<string> All = [Running, NeedsYou, Waiting, Idle, Stopped, Completed, Closed, Expired];
    }

    /// <summary>The state filter's chips: open, needs-you, done, closed (closed and expired), all; or one status.</summary>
    public static bool StateFilterMatches(string? filter, string status)
    {
        var f = (filter ?? "all").Trim().ToLowerInvariant();
        return f switch
        {
            "" or "all" => true,
            "open" => status is DeliverableStatuses.Running or DeliverableStatuses.NeedsYou or DeliverableStatuses.Waiting or DeliverableStatuses.Idle or DeliverableStatuses.Stopped,
            "done" => status == DeliverableStatuses.Completed,
            "closed" => status is DeliverableStatuses.Closed or DeliverableStatuses.Expired,
            _ when DeliverableStatuses.All.Contains(f) => status == f,
            _ => throw new BusRefusal("bad_params", $"state must be all, open, needs-you, done, closed or one of {string.Join(", ", DeliverableStatuses.All)}"),
        };
    }

    /// <summary>
    /// deliverables.list: every team's deliverables (or <paramref name="teamId"/>'s), filtered by <paramref name="q"/> and
    /// <paramref name="state"/>, newest activity first: the newest <paramref name="limit"/> and every open one however old.
    /// </summary>
    public Task<DeliverablesPage> DeliverablesPageAsync(long? teamId, string? q, string? state, int limit = DefaultPageLimit, CancellationToken ct = default) =>
        DeliverablesPageAsync(teamId, q, state, limit, include: null, ct);

    /// <summary>
    /// As above, keeping only the deliverables <paramref name="include"/> says yes to (the goal filter), before the counts:
    /// the chips count what the filter keeps, as with a team.
    /// </summary>
    public Task<DeliverablesPage> DeliverablesPageAsync(long? teamId, string? q, string? state, int limit, Func<long, bool>? include, CancellationToken ct)
    {
        StateFilterMatches(state, DeliverableStatuses.Running); // a bad filter is refused before anything is read
        return WithGate(() => ReadPage(teamId, q, state, Math.Clamp(limit, 1, MaxPageLimit), only: null, include), ct);
    }

    /// <summary>deliverables.get's row: one deliverable as deliverables.list shows it, with its item's spec; null when there is no such deliverable of a team.</summary>
    public Task<DeliverableRow?> DeliverableRowAsync(long deliverable, CancellationToken ct = default) =>
        WithGate(() => ReadPage(null, null, null, 1, only: deliverable, include: null).Rows.FirstOrDefault(), ct);

    private sealed record Candidate(DeliverableHead Head, TeamRow Team, MemberRef? Owner, DeliverableItemRef? Item, string Title)
    {
        public List<DeliverableWorker> Members { get; } = [];
        public string Status { get; set; } = DeliverableStatuses.Idle;
        public string? WaitingFor { get; set; }
        public bool Working { get; set; }
        public IdleDeliverable? Idle { get; set; }
        public Message? Question { get; set; }
    }

    /// <summary>Under the gate.</summary>
    private DeliverablesPage ReadPage(long? teamId, string? q, string? state, int limit, long? only, Func<long, bool>? include)
    {
        var now = _store.Clock.GetUtcNow();
        var teams = _store.Teams();
        if (teamId is { } tid && teams.All(t => t.Id != tid)) throw new BusRefusal("no_such_team", $"there is no team {tid}");
        var teamById = teams.ToDictionary(t => t.Id);
        var teamByName = teams.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var members = _store.AllMembers();
        var byQualified = members.GroupBy(m => m.Qualified, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byTeam = members.GroupBy(m => m.TeamId).ToDictionary(g => g.Key, g => (IReadOnlyList<MemberRef>)[.. g]);

        // 1. Which deliverables, and whose (HeadsOfTeams), and the goal filter.
        var heads = HeadsOfTeams(_store.DeliverableHeads(), teamById, teamByName, byQualified, teamId, only);
        if (include is not null) heads = heads.Where(h => include(h.Head.Id)).ToList();

        // 2. The backlog item each came from (its dispatch, else the root's payload item) and so its clean title.
        var items = ItemsOf(heads.Select(h => h.Head).ToList());
        var pending = _store.Pending();
        var leaseWaiting = _leaseQueue.Select(l => l.Run.RunId).ToHashSet();
        var queue = _store.Queue();
        var held = _store.HeldTeams();
        var candidates = new List<Candidate>();
        foreach (var (h, team, owner) in heads)
        {
            var item = items.GetValueOrDefault(h.Id);
            var c = new Candidate(h, team, owner, item, item?.Title is { Length: > 0 } t ? Clip(t) : TitleOf(h.Root));
            if (!h.Ended) c.Members.AddRange(WorkersOf(h.Id, pending, leaseWaiting, byQualified, queue, held));
            candidates.Add(c);
        }

        // 3. The text filter: every word in the id, title, item, team, owner or a member working it.
        var words = (q ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
            candidates = candidates.Where(c => words.All(w => Haystack(c, byTeam).Any(s => s.Contains(w, StringComparison.OrdinalIgnoreCase)))).ToList();

        // 4. Each one's status: an ended one by its end; an open one from its thread and what works it now.
        var open = candidates.Where(c => !c.Head.Ended).Select(c => c.Head.Id).ToList();
        var threads = new Dictionary<long, IReadOnlyList<Message>>(_store.Threads(open));
        var wakes = _store.OpenWakesOf(open);
        var expiries = _store.DeliverableExpiries(open);
        foreach (var c in candidates)
        {
            if (c.Head.Ended)
            {
                c.Status = c.Head.EndType switch
                {
                    MessageTypes.DeliverableClosed => DeliverableStatuses.Closed,
                    MessageTypes.DeliverableExpired => DeliverableStatuses.Expired,
                    _ => DeliverableStatuses.Completed,
                };
                continue;
            }
            var thread = threads.GetValueOrDefault(c.Head.Id) ?? [];
            var w = c.Head.Id;
            var openWakes = wakes.GetValueOrDefault(w);
            c.Idle = IdleOf(w, thread, pending, now, () => openWakes, () => expiries.GetValueOrDefault(w),
                () => Math.Clamp(teamById[c.Team.Id].DeliverableExpiryDays ?? DefaultDeliverableExpiryDays, MinDeliverableExpiryDays, MaxDeliverableExpiryDays));
            c.Question = c.Idle?.Question ?? OpenQuestion(thread);
            var active = _active.Values.Select(x => x.Context).Where(x => x.Correlation == w).ToList();
            c.Working = active.Count > 0;
            (c.Status, c.WaitingFor) =
                c.Question is not null ? (DeliverableStatuses.NeedsYou, null)
                : active.Any(r => !leaseWaiting.Contains(r.RunId)) ? (DeliverableStatuses.Running, null)
                : active.Count > 0 ? (DeliverableStatuses.Waiting, "lease")
                : pending.Any(p => p.Correlation == w) ? (DeliverableStatuses.Waiting, WaitingForOf(c.Members))
                : openWakes > 0 || ResumeDue(thread) ? (DeliverableStatuses.Waiting, "wake")
                : LastRunStopped(thread) ? (DeliverableStatuses.Stopped, null)
                : (DeliverableStatuses.Idle, (string?)null);
        }

        // 5. The chips' counts (before the state filter and the limit), then the state filter and the limit.
        var today = now.UtcDateTime.Date;
        var counts = new Dictionary<string, int>
        {
            ["all"] = candidates.Count,
            ["open"] = candidates.Count(c => !c.Head.Ended),
            ["needsYou"] = candidates.Count(c => c.Status == DeliverableStatuses.NeedsYou),
            ["done"] = candidates.Count(c => c.Status == DeliverableStatuses.Completed),
            ["closed"] = candidates.Count(c => c.Status is DeliverableStatuses.Closed or DeliverableStatuses.Expired),
            ["completedToday"] = candidates.Count(c => c.Status == DeliverableStatuses.Completed && c.Head.EndAt?.UtcDateTime.Date == today),
        };
        foreach (var s in new[] { DeliverableStatuses.Running, DeliverableStatuses.Waiting, DeliverableStatuses.Idle, DeliverableStatuses.Stopped, DeliverableStatuses.Completed, DeliverableStatuses.Expired })
            counts[s] = candidates.Count(c => c.Status == s);
        var matching = candidates.Where(c => StateFilterMatches(state, c.Status)).ToList(); // heads are newest activity first
        var shown = matching.Where((c, i) => i < limit || !c.Head.Ended).ToList();

        // 6. The rows: the ended ones' threads, everyone's time, and the summaries.
        var missing = shown.Where(c => !threads.ContainsKey(c.Head.Id)).Select(c => c.Head.Id).ToList();
        foreach (var (k, v) in _store.Threads(missing)) threads[k] = v;
        var shownThreads = shown.ToDictionary(c => c.Head.Id, c => threads.GetValueOrDefault(c.Head.Id) ?? []);
        var times = TimesOf(shownThreads, now);
        var rows = new List<DeliverableRow>();
        foreach (var c in shown)
        {
            var manager = byTeam.GetValueOrDefault(c.Team.Id)?.FirstOrDefault(m => m.Role == MemberRole.Manager);
            var owner = c.Owner ?? manager;
            var summary = PageSummary(c, owner, shownThreads[c.Head.Id], byTeam.GetValueOrDefault(c.Team.Id) ?? [], pending,
                wakes.GetValueOrDefault(c.Head.Id), times.GetValueOrDefault(c.Head.Id));
            rows.Add(new DeliverableRow(summary, c.Team.Name, c.Team.Id, owner?.Id, c.Status, c.WaitingFor, c.Working, c.Item,
                [.. c.Members.OrderBy(m => m.State switch { "running" => 0, "lease" => 1, _ => 2 }).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)]));
        }
        return new DeliverablesPage(rows, counts, shown.Count < matching.Count, now);
    }

    /// <summary>
    /// The deliverables of teams (of <paramref name="teamId"/> only, or just <paramref name="only"/>) and whose: the root's
    /// addressee's team, else the team of its first member's message. A deliverable of no team is left out. Under the gate.
    /// </summary>
    private static List<(DeliverableHead Head, TeamRow Team, MemberRef? Owner)> HeadsOfTeams(IReadOnlyList<DeliverableHead> all,
        IReadOnlyDictionary<long, TeamRow> teamById, IReadOnlyDictionary<string, TeamRow> teamByName,
        IReadOnlyDictionary<string, MemberRef> byQualified, long? teamId, long? only)
    {
        long? TeamOfQualified(string? qualified)
        {
            if (qualified is null) return null;
            if (byQualified.TryGetValue(qualified, out var m)) return m.TeamId;
            var slash = qualified.IndexOf('/');
            return slash > 0 && teamByName.TryGetValue(qualified[..slash], out var t) ? t.Id : null;
        }
        var heads = new List<(DeliverableHead Head, TeamRow Team, MemberRef? Owner)>();
        foreach (var h in all)
        {
            if (only is { } o && h.Id != o) continue;
            var addressee = h.Root is { } root && root.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
                ? root.Type[MessageTypes.InstructionPrefix.Length..] : null;
            var owner = addressee is not null && byQualified.TryGetValue(addressee, out var a) ? a : null;
            var team = owner?.TeamId ?? TeamOfQualified(h.FirstMemberSource) ?? TeamOfQualified(addressee);
            if (team is not { } id || !teamById.TryGetValue(id, out var row)) continue;
            if (teamId is { } want && id != want) continue;
            heads.Add((h, row, owner));
        }
        return heads;
    }

    /// <summary>DeliverableSummary from what <see cref="ReadPage"/> read (as <c>Summary</c> computes it, without a query of its own).</summary>
    private DeliverableSummary PageSummary(Candidate c, MemberRef? owner, IReadOnlyList<Message> thread, IReadOnlyList<MemberRef> teamMembers,
        IReadOnlyList<BusStore.PendingRow> pending, int openWakes, Board.TimeSummary? time)
    {
        var w = c.Head.Id;
        var ending = thread.LastOrDefault(m => MessageTypes.DeliverableEnds.Contains(m.Type));
        var ended = c.Head.Ended;
        var state = !ended || ending is null ? "open" : ending.Type switch
        {
            MessageTypes.DeliverableClosed => "closed",
            MessageTypes.DeliverableExpired => "expired",
            _ => "completed",
        };
        var busy = owner is null ? [] : Busy(owner, w, watcher: null, teamMembers, pending);
        if (owner is not null && _active.Values.Any(a => a.Context.Correlation == w && a.Context.Member.Id == owner.Id))
            busy.Insert(0, $"{owner.Name} is running");
        var undeclared = !ended && busy.Count == 0 && openWakes == 0;
        DateTimeOffset? endedAt = null, expiredAt = null;
        if (ended && ending is not null)
        {
            endedAt = state == "expired" ? Time(ending, "endedAt") ?? ending.OccurredAt : ending.OccurredAt;
            expiredAt = state == "expired" ? Time(ending, "expiredAt") ?? ending.OccurredAt : null;
        }
        var endingText = ending is null || !ended ? null : ending.Type switch
        {
            MessageTypes.DeliverableClosed => Text(ending, "reason"),
            MessageTypes.DeliverableExpired => Text(ending, "detail") ?? Text(ending, "reason"),
            _ => Text(ending, "delivered"),
        };
        var idle = c.Idle;
        return new DeliverableSummary(w, c.Title, owner?.Name, state, undeclared, busy, endingText, c.Head.First, c.Head.Last, c.Head.Count,
            endedAt, expiredAt, idle?.ExpiresAt,
            idle is { Warned: true, Question: { } q } ? WarningText(w, q.Seq) : null,
            ended ? null : c.Question?.Seq,
            time);
    }

    /// <summary>The times of these deliverables from the board, read together (null each without a board, or when it cannot be read).</summary>
    private IReadOnlyDictionary<long, Board.TimeSummary> TimesOf(IReadOnlyDictionary<long, IReadOnlyList<Message>> threads, DateTimeOffset now)
    {
        if (Board is not { } board || threads.Count == 0) return new Dictionary<long, Board.TimeSummary>();
        try { return board.Timeline.TimesOf(threads, LiveActivity(now), now); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or FormatException or ArgumentException)
        {
            Log($"deliverables page: times could not be read: {ex.Message}");
            return new Dictionary<long, Board.TimeSummary>();
        }
    }

    /// <summary>Each deliverable's backlog item: its newest dispatch (backlog_dispatches), else the root message's payload item.</summary>
    private Dictionary<long, DeliverableItemRef> ItemsOf(IReadOnlyList<DeliverableHead> heads)
    {
        var result = new Dictionary<long, DeliverableItemRef>();
        if (Board is not { } board || heads.Count == 0) return result;
        try
        {
            var store = board.Store;
            var dispatched = store.ItemsOfDeliverables(heads.Select(h => h.Id).ToList());
            var labelled = heads.Where(h => !dispatched.ContainsKey(h.Id))
                .Select(h => (h.Id, Item: h.Root is { } r ? Yawble.Core.Board.BoardStore.ItemId(Text(r, "item")) : null))
                .Where(x => x.Item is not null).ToList();
            var byId = store.ItemsById(labelled.Select(x => x.Item!.Value).Distinct().ToList());
            foreach (var (w, item) in dispatched) result[w] = Ref(item);
            foreach (var (w, id) in labelled)
                if (byId.TryGetValue(id!.Value, out var item)) result[w] = Ref(item);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            Log($"deliverables page: backlog items could not be read: {ex.Message}");
        }
        return result;

        static DeliverableItemRef Ref(Board.ItemRow i) => new(i.Id, i.Label, i.Title, i.ArchivedAt is not null, i.Body);
    }

    /// <summary>
    /// Who works the deliverable now: running (active run), lease (its active run waits for the lease), else why its
    /// delivery here has not started (<see cref="WaitOf"/>: queued, waits-for-run with the sender's name, held, pending).
    /// </summary>
    private IEnumerable<DeliverableWorker> WorkersOf(long deliverable, IReadOnlyList<BusStore.PendingRow> pending, IReadOnlySet<long> leaseWaiting,
        IReadOnlyDictionary<string, MemberRef> byQualified, IReadOnlyList<BusStore.QueueRow> queue, IReadOnlySet<long> held)
    {
        var seen = new HashSet<long>();
        foreach (var run in _active.Values.Select(a => a.Context).Where(r => r.Correlation == deliverable))
            if (seen.Add(run.Member.Id))
                yield return new DeliverableWorker(run.Member.Id, run.Member.Name, leaseWaiting.Contains(run.RunId) ? "lease" : "running");
        foreach (var g in pending.Where(p => p.Correlation == deliverable).GroupBy(p => p.Subscriber, StringComparer.OrdinalIgnoreCase))
            if (byQualified.TryGetValue(g.Key, out var m) && seen.Add(m.Id))
            {
                var (state, waitsFor) = WaitOf(m.Id, m.TeamId, [.. g], queue, held);
                yield return new DeliverableWorker(m.Id, m.Name, state, waitsFor);
            }
    }

    /// <summary>
    /// Why a member's undelivered work (<paramref name="rows"/>: its pending deliveries, in one deliverable or all) has
    /// not started. Its team paused or archived: held. Every unstarted row a hand-back still held for the run that sent it:
    /// waits-for-run, with that sender's name. In the run queue: queued (the only wait for a run slot). Else pending.
    /// Under the gate.
    /// </summary>
    private (string State, string? WaitsFor) WaitOf(long memberId, long teamId, IReadOnlyList<BusStore.PendingRow> rows,
        IReadOnlyList<BusStore.QueueRow> queue, IReadOnlySet<long> held)
    {
        if (held.Contains(teamId)) return (WaitStates.Held, null);
        var senders = rows.Where(r => !r.Started).Select(HeldBy).ToList();
        if (senders.Count > 0 && senders.All(s => s is not null)) return (WaitStates.WaitsForRun, senders[0]!.Member.Name);
        if (queue.Any(q => q.MemberId == memberId && q.Kind == QueueKinds.Run)) return (WaitStates.Queued, null);
        return (WaitStates.Pending, null);
    }

    /// <summary>The row's waitingFor when the deliverable waits on undelivered work: slot only while a member is queued for one.</summary>
    private static string WaitingForOf(IReadOnlyList<DeliverableWorker> workers)
    {
        var states = workers.Select(m => m.State).ToHashSet();
        return states.Contains(WaitStates.Queued) ? "slot"
            : states.Contains(WaitStates.WaitsForRun) ? WaitStates.WaitsForRun
            : states.Contains(WaitStates.Pending) ? WaitStates.Pending
            : states.Contains(WaitStates.Held) ? WaitStates.Held
            : WaitStates.Pending;
    }

    /// <summary>The newest run end in the thread was a run the person stopped (failed with class stopped).</summary>
    private static bool LastRunStopped(IReadOnlyList<Message> thread) =>
        thread.LastOrDefault(m => m.Type is MessageTypes.Completed or MessageTypes.Failed) is { Type: MessageTypes.Failed } f
        && Text(f, "failureClass") == FailureClasses.Stopped;

    private static IEnumerable<string> Haystack(Candidate c, IReadOnlyDictionary<long, IReadOnlyList<MemberRef>> byTeam)
    {
        yield return "#" + c.Head.Id.ToString(CultureInfo.InvariantCulture);
        yield return c.Title;
        yield return c.Team.Name;
        if (c.Item is { } i) { yield return i.Label; yield return i.Title; }
        var owner = c.Owner ?? byTeam.GetValueOrDefault(c.Team.Id)?.FirstOrDefault(m => m.Role == MemberRole.Manager);
        if (owner is not null) yield return owner.Name;
        foreach (var m in c.Members) yield return m.Name;
    }

    private static string Clip(string s)
    {
        var line = s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? s.Trim();
        return line.Length <= 100 ? line : line[..99] + "…";
    }
}
