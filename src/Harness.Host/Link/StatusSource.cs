using Harness.Containers;
using Harness.Contracts;
using Harness.Contracts.Link;

namespace Harness.Host.Link;

/// <summary>
/// THE LINK'S STATUS SUMMARY, as the link client reads it: the latest summary, and word when it has
/// changed. <see cref="Notify"/> is told of every team, member and worker change; each is read at once,
/// so <see cref="Current"/> is the latest throughout, but <see cref="Changed"/> is raised once per
/// <see cref="Quiet"/> after the first change of a burst, and only when the summary differs from the
/// one last announced. The wait is on the injected clock.
/// </summary>
public sealed class StatusSource : ILinkSummarySource, IDisposable
{
    /// <summary>How long a burst of changes is gathered before one status goes.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _clock;
    private readonly ILogger<StatusSource> _logger;
    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private StatusSummary _latest = new(StatusSummary.Healthy, BuildVersion.Current.Version, [], [], 0);
    private string? _announced;
    private ITimer? _burst;
    private long _asked;
    private long _read;

    public StatusSource(TimeProvider clock, ILogger<StatusSource> logger)
    {
        _clock = clock;
        _logger = logger;
        _announced = Wire(_latest);
    }

    /// <summary>How a summary is read. Set once the Host can read every figure; until then the summary stays as it was.</summary>
    public Func<CancellationToken, Task<StatusSummary>>? Read { get; set; }

    public event Action? Changed;

    public StatusSummary Current() => Volatile.Read(ref _latest);

    /// <summary>Something a summary is made of may have changed: read it now, and announce it once the burst is quiet.</summary>
    public void Notify()
    {
        _ = RefreshAsync(_stopping.Token);

        lock (_gate)
        {
            if (_burst is not null || _stopping.IsCancellationRequested) return;

            _burst = _clock.CreateTimer(_ => _ = AnnounceAsync(), null, Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Reads the summary into <see cref="Current"/>. Reads never overlap: a caller that asked while a read
    /// was running waits for one that starts after it asked, so a burst costs at most two reads in flight.
    /// A read that fails keeps the summary it had.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var asked = Interlocked.Increment(ref _asked);
        try
        {
            await _reading.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (Interlocked.Read(ref _read) >= asked || Read is not { } read) return;

            var covers = Interlocked.Read(ref _asked);
            Volatile.Write(ref _latest, await read(ct));
            Interlocked.Exchange(ref _read, covers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Not a line a person sees: an instance that is never attached logs nothing new.
            _logger.LogDebug("The link's status summary could not be read: {Error}", ex.Message);
        }
        finally
        {
            _reading.Release();
        }
    }

    private async Task AnnounceAsync()
    {
        lock (_gate)
        {
            _burst?.Dispose();
            _burst = null;
        }

        await RefreshAsync(_stopping.Token);

        var wire = Wire(Current());
        if (wire == Volatile.Read(ref _announced)) return;

        Volatile.Write(ref _announced, wire);
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("A listener to the link's status summary failed: {Error}", ex.Message);
        }
    }

    private static string Wire(StatusSummary summary) => System.Text.Json.JsonSerializer.Serialize(summary, LinkJson.Options);

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping.Cancel();
            _burst?.Dispose();
            _burst = null;
        }
    }
}

/// <summary>A member's state, in the words <c>web/src/lib/memberState.ts</c> uses.</summary>
public static class MemberStates
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Blocked = "blocked";
    public const string Decision = "decision";
    public const string Failed = "failed";
}

/// <summary>
/// WHAT THE SUMMARY IS MADE OF, read from what each screen already reads. Health and version are
/// <c>GET /api/service-status</c>'s (the same three checks, the same build), workers are the list its
/// Workers section shows, teams are <see cref="TeamRegistry"/>'s active teams with the workflow the
/// Teams list reads for each (<see cref="RollupCache"/>, on the same floor), and Needs you is
/// <c>GET /api/needs-you</c>'s count, both parts. A team crosses as its id, its name and four counts;
/// a worker as its id and connected or dropped. Nothing else of either is read into the summary.
///
/// <para>
/// THE TEAM'S WORD AND ITS MEMBERS' STATES ARE THE WEB'S RULES, PORTED EXACTLY: <c>teamStatus</c> and
/// <c>teamLogFromTiming</c> in <c>web/src/lib/teamKpis.ts</c>, and <c>memberStateOf</c> in
/// <c>web/src/lib/memberState.ts</c>. The Host holds no other copy of them; a change to one is a
/// change to both.
/// </para>
/// </summary>
public static class LinkStatusFacts
{
    /// <summary>How long a team is silent before it reads undeclared: <c>StalledBadgeGrace</c>.</summary>
    public static readonly TimeSpan UndeclaredGrace = TimeSpan.FromMinutes(2);

    private const string NeedsPerson = "needs-person";

    /// <summary>Reads every figure from the Host's own services, in process.</summary>
    public static async Task<StatusSummary> ReadAsync(
        IServiceProvider services, string dataRoot, Func<IReadOnlyList<WorkerSample>> workers, CancellationToken ct)
    {
        var checks = await HealthEndpoints.ChecksAsync(
            services.GetRequiredService<IDatabaseHealth>(), dataRoot, services.GetRequiredService<PumpHeartbeat>(), ct);

        var registry = services.GetRequiredService<TeamRegistry>();
        var host = services.GetRequiredService<ContainerHost>();
        var rollup = services.GetRequiredService<RollupCache>();
        var teams = new List<(TeamSummary, TeamWorkflowTiming?)>();
        foreach (var team in registry.All().Where(t => !t.Archived))
        {
            var (workflow, _) = await rollup.ReadAsync(team.Id, TeamEndpoints.RollupFloor(registry, host, team.Id), ct);
            teams.Add((team, workflow));
        }

        var needsYou = (await NeedsYou.BothAsync(services, ct)).Count;

        return Summarize(
            checks.All(c => c.Ok), BuildVersion.Current.Version, workers(), teams, needsYou,
            services.GetRequiredService<TimeProvider>().GetUtcNow());
    }

    /// <summary>The summary, from the allow-listed fields only.</summary>
    public static StatusSummary Summarize(
        bool healthy, string version, IEnumerable<WorkerSample> workers,
        IEnumerable<(TeamSummary Team, TeamWorkflowTiming? Workflow)> teams, int needsYou, DateTimeOffset now) =>
        new(
            healthy ? StatusSummary.Healthy : StatusSummary.Unhealthy,
            version,
            [.. workers.Select(w => new WorkerState(w.Id, w.State == WorkerState.Connected ? WorkerState.Connected : WorkerState.Dropped))],
            [.. teams.Select(t => TeamStateOf(t.Team, t.Workflow, now))],
            needsYou);

    /// <summary>One team as its row in the Teams list shows it.</summary>
    public static TeamState TeamStateOf(TeamSummary team, TeamWorkflowTiming? workflow, DateTimeOffset now)
    {
        var states = team.Containers.Select(MemberStateOf).ToList();
        return new TeamState(
            team.Id, team.Name, TeamStatusOf(team, workflow, now),
            states.Count(s => s == MemberStates.Running),
            states.Count(s => s == MemberStates.Idle),
            states.Count(s => s == MemberStates.Blocked),
            states.Count(s => s == MemberStates.Decision));
    }

    /// <summary><c>memberStateOf</c>: a mark first (failed, then needs you, then blocked), then its run.</summary>
    public static string MemberStateOf(ContainerSnapshot member)
    {
        if (Set(member.NeedsPerson) || (Set(member.Failed) && member.FailureClass == NeedsPerson)) return MemberStates.Decision;
        if (Set(member.Failed)) return MemberStates.Failed;
        if (Set(member.NeedsDecision)) return MemberStates.Decision;
        if (Set(member.Blocked)) return MemberStates.Blocked;
        if (member.State == ContainerState.Running) return member.Held ? MemberStates.Waiting : MemberStates.Running;
        if (member.QueueDepth > 0 || member.Held) return MemberStates.Waiting;
        return MemberStates.Idle;
    }

    /// <summary><c>teamStatus</c>, with the team's workflow read as <c>teamLogFromTiming</c> reads it.</summary>
    public static string TeamStatusOf(TeamSummary team, TeamWorkflowTiming? workflow, DateTimeOffset now)
    {
        var log = LogOf(workflow);
        bool misconfigured = false, failed = false, blocked = false, running = false, queued = false, waiting = false;

        foreach (var member in team.Containers)
        {
            if (Set(member.MissingAgent) || Set(member.UnreachableRoot)) misconfigured = true;

            var movedOn = member.State == ContainerState.Running || member.QueueDepth > 0;
            var waitsForPerson = Set(member.NeedsPerson) || member.FailureClass == NeedsPerson;
            if (!movedOn && !waitsForPerson && log is not null
                && log.Terminal.TryGetValue(member.Id, out var last) && last.Event == Terminal.Failed)
            {
                failed = true;
            }

            if (Set(member.Blocked) || waitsForPerson) blocked = true;
            if (member.State == ContainerState.Running) running = true;
            if (member.QueueDepth > 0) queued = true;
            if (member.Held && member.State != ContainerState.Running) waiting = true;
        }

        if (misconfigured) return "misconfigured";
        if (failed) return "failed";
        if (blocked) return "blocked";
        if (team.Paused) return "paused";
        if (running) return "running";
        if (waiting) return "waiting";
        if (queued) return "queued";
        if (IsUndeclared(team.Containers, log, now)) return "undeclared";
        return "idle";
    }

    private static bool IsUndeclared(IReadOnlyCollection<ContainerSnapshot> members, TeamLog? log, DateTimeOffset now)
    {
        if (log is null || !log.WorkflowOpen || members.Count == 0) return false;

        DateTimeOffset? newest = null;
        var ran = 0;
        foreach (var member in members)
        {
            if (member.State == ContainerState.Running || member.QueueDepth != 0 || member.Held) return false;
            if (!log.Terminal.TryGetValue(member.Id, out var last)) continue;
            if (last.Event != Terminal.Completed) return false;

            ran++;
            if (last.At is not { } at) return false;
            if (newest is null || at > newest) newest = at;
        }

        return ran > 0 && now - newest!.Value >= UndeclaredGrace;
    }

    /// <summary><c>teamLogFromTiming</c>: each member's last terminal fact in the team's workflow.</summary>
    private static TeamLog? LogOf(TeamWorkflowTiming? workflow)
    {
        if (workflow is null) return null;

        var at = workflow.LastActivityAt ?? workflow.StartedAt;
        var terminal = new Dictionary<string, (string Event, DateTimeOffset? At)>(StringComparer.Ordinal);
        var settled = workflow.State is "Completed" or "Closed";

        if (!settled)
        {
            foreach (var member in workflow.FailedMembers ?? []) terminal[member] = (Terminal.Failed, at);
        }

        if (Set(workflow.BlockedBy)) terminal[workflow.BlockedBy!] = (Terminal.Blocked, at);
        if (Set(workflow.AwaitingFrom)) terminal[workflow.AwaitingFrom!] = (Terminal.NeedsDecision, at);

        if (workflow.RunsUnfinished == 0)
        {
            foreach (var member in workflow.Members ?? [])
            {
                if (member.Runs == 0 || terminal.ContainsKey(member.Member)) continue;

                terminal[member.Member] = (Terminal.Completed, at);
            }
        }

        return new TeamLog(workflow.Available && workflow.EndedAt is null, terminal);
    }

    private static bool Set(string? value) => !string.IsNullOrEmpty(value);

    private sealed record TeamLog(bool WorkflowOpen, IReadOnlyDictionary<string, (string Event, DateTimeOffset? At)> Terminal);

    private static class Terminal
    {
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Blocked = "blocked";
        public const string NeedsDecision = "needs-decision";
    }
}
