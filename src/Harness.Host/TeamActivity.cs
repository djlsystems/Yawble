using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// WHAT EACH MEMBER WAS DOING, AND WHEN: <c>GET /api/teams/{team}/activity</c>. Per member, the
/// stretches of a period it was running, waiting, held for admission, blocked, failed or idle, decided
/// here once from recorded times so the web only draws and buckets them.
///
/// <para>
/// THE SOURCE IS THE LEDGER, plus the log's run in progress. <c>usage_ledger</c> keeps every finished
/// run's queue, start and end and how it ended, and nothing deletes it, so the spans survive Reset's
/// "Delete memory" and log retention. The log adds only what the ledger cannot know yet - a run still
/// going - and the words of a block or failure while it still holds them. <c>admission_holds</c>,
/// as lasting as the ledger, says which part of a wait was a hold for a slot and why.
/// </para>
///
/// <para>
/// NOTHING IS ESTIMATED. A ledger row with no queue time makes no waiting, one with no start makes
/// neither waiting nor running, and a stretch nothing recorded is no span at all: "no data" is the
/// absence of a span, never a state.
/// </para>
/// </summary>
public static class TeamActivity
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Held = "held";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
    public const string Idle = "idle";

    /// <summary>The longest period one read answers.</summary>
    public static DateTimeOffset LatestTo(DateTimeOffset from) => from.AddYears(1);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/teams/{team}/activity", async (
                [Description(Describe.Team)] string team,
                [Description("Start of the period, a UTC instant. Give it with `to`, or neither for the "
                    + "team's own window.")] DateTimeOffset? from,
                [Description("End of the period, a UTC instant, at most a year after `from`.")] DateTimeOffset? to,
                TeamRegistry teams, ITeamStore store, ContainerHost host, IMessageLog log, IUsageLedger ledger,
                IAdmissionHoldLedger holds, TimeProvider clock, CancellationToken ct) =>
            {
                if (teams.ExistingName(team) is not { } stored)
                {
                    return Results.NotFound(new { error = $"No team '{team}'." });
                }

                if (Refusal(from, to) is { } refusal) return Results.BadRequest(new { error = refusal });

                return Results.Ok(await ReadAsync(stored, from, to, teams, store, host, log, ledger, holds, clock.GetUtcNow(), ct));
            })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("What each member of this team was doing, and when")
            .WithDescription(
                "Per member, ordered non-overlapping `spans` of `running`, `waiting`, `held`, `blocked` (a block "
                + "or a question for a person), `failed` or `idle`, clipped to the period, derived from "
                + "the usage ledger and the run in progress. A stretch nothing recorded has no span: no "
                + "data is never a state. A span's `to` is null when it is still open at `serverNow` "
                + "inside a requested period; "
                + "`workflow` names the workflow a run or its block belongs to; `reason` is a block's or "
                + "failure's words while the log still holds them.\n\n"
                + "`held` (waiting for a slot) is the part of a wait an admission hold covers, with the hold's "
                + "`reason` sentence and its `reasonKind` (`slot`, `memory`, `pressure` or `worker`), from "
                + "`admission_holds`; the rest of the wait stays `waiting`, and running still wins. Waits "
                + "from before holds were recorded stay `waiting`.\n\n"
                + "With no `from` and `to`, the team's own window (`window: \"workflows\"`): from the root "
                + "of its earliest workflow, open or closed, to the latest activity of any of them - a "
                + "closed workflow's end, an open one's newest row - and never to the clock, so it does "
                + "not move while nothing happens; a span still open there ends at the window's end. A "
                + "team that has never run answers `\"none\"` and no members. With both, that period "
                + "(`\"requested\"`), at most one year.\n\n"
                + "`members`: the Manager first, then the team's members in board order, then any "
                + "member with runs in the period who has since been removed, with `current: false`.");
    }

    /// <summary>The sentence a period is refused with, or null for one this read answers.</summary>
    public static string? Refusal(DateTimeOffset? from, DateTimeOffset? to) => (from, to) switch
    {
        (null, null) => null,
        ({ }, null) or (null, { }) => "Give both from and to, or neither for the team's own window.",
        ({ } f, { } t) when f > t => "The period's from is after its to.",
        ({ } f, { } t) when t > LatestTo(f) => "The period is longer than a year; ask for one year or less.",
        _ => null,
    };

    public static async Task<TeamActivityAnswer> ReadAsync(
        string team, DateTimeOffset? from, DateTimeOffset? to,
        TeamRegistry teams, ITeamStore store, ContainerHost host, IMessageLog log, IUsageLedger ledger,
        IAdmissionHoldLedger holdLedger, DateTimeOffset now, CancellationToken ct)
    {
        // SCOPED BY THE TEAM'S CREATION, NOT BY ITS MEMBERS' FLOOR: Reset's "Delete memory" raises
        // every named member's floor to the log head, and the spans must outlive it. The creation
        // instant is turned into the log position it fell at, and every ledger row and log row read
        // here is above that, so a team re-created under the same name never shows the runs of the
        // team it replaced: their runs ended before it existed.
        var created = await store.CreatedAtAsync(team, ct);
        var floor = created is { } at ? await log.LastSeqBeforeAsync(at, ct) : 0;

        string window;
        DateTimeOffset start;
        DateTimeOffset end;

        if (from is { } f && to is { } t)
        {
            (window, start, end) = ("requested", f, t);
        }
        else
        {
            // THE TEAM'S WHOLE WORKFLOW STRETCH: from its earliest workflow's root, open or closed, to
            // the newest row of any of them. Never the clock: while nothing happens the end stays.
            if (await log.WorkflowStretchForTeamAsync(team, floor, ct) is not { } stretch)
            {
                return new TeamActivityAnswer(null, null, now, "none", []);
            }

            (window, start, end) = ("workflows", stretch.From, stretch.To);
        }

        // THE CURRENT MEMBERS, the Manager first and the rest as the board lists them.
        var current = teams.ContainerIdsOf(team)
            .Select(id => (Id: id, Snapshot: host.Find(id)?.Snapshot()))
            .Where(m => m.Snapshot is not null)
            .OrderBy(m => IsManager(m.Id.Name) ? 0 : 1)
            .ToList();

        var runs = await ledger.ReadTeamRunsAsync(team, floor, start, end, [.. current.Select(m => m.Id.Name)], ct);

        // THE HOLDS, by the same scope in time: a hold that began before the team was created was its
        // predecessor's under the same name.
        var holds = await holdLedger.ReadTeamAsync(team, created ?? DateTimeOffset.MinValue, start, end, ct);

        var members = new List<TeamActivityMember>();

        foreach (var (id, snapshot) in current)
        {
            var mine = runs.Where(r => Same(r.Member, id.Name)).ToList();
            var inProgress = await InProgressAsync(log, id, snapshot!.SinceSeq, ct);
            var held = holds.Where(h => Same(h.Member, id.Name)).ToList();
            var spans = await SpansAsync(mine, held, inProgress, start, end, now, log, id, floor, ct);

            members.Add(new TeamActivityMember(id.Name, MemberRef.KindOf(snapshot.Agent), IsManager(id.Name), true, spans));
        }

        // A MEMBER SINCE REMOVED is listed when it has a run in the period; its runs still count.
        var removed = runs
            .Where(r => !current.Any(m => Same(m.Id.Name, r.Member)))
            .Where(r => Overlaps(r, start, end))
            .Select(r => r.Member)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in removed)
        {
            var mine = runs.Where(r => Same(r.Member, name)).ToList();
            var held = holds.Where(h => Same(h.Member, name)).ToList();
            var spans = await SpansAsync(mine, held, null, start, end, now, log, new ContainerId(team, name), floor, ct);

            members.Add(new TeamActivityMember(name, mine[^1].MemberKind, IsManager(name), false, spans));
        }

        return new TeamActivityAnswer(start, end, now, window, members);
    }

    /// <summary>A member's run in progress, from the log: its latest <c>started</c> row with no
    /// terminal row after it, or null.</summary>
    private static async Task<Message?> InProgressAsync(IMessageLog log, ContainerId id, long floor, CancellationToken ct)
    {
        var latest = (await log.ReadMemberBeforeAsync(
                id, floor, long.MaxValue, [MessageTypes.Started, MessageTypes.Completed, MessageTypes.Failed], 50, ct))
            .FirstOrDefault(m => m.Type is MessageTypes.Started or MessageTypes.Completed or MessageTypes.Failed);

        return latest?.Type == MessageTypes.Started ? latest : null;
    }

    /// <summary>One recorded stretch before clipping. <see cref="Rank"/> decides an overlap: running
    /// over held over waiting over what a run left behind. An open stretch ends at
    /// <see cref="DateTimeOffset.MaxValue"/>.</summary>
    private sealed record Stretch(
        string State, DateTimeOffset From, DateTimeOffset To, int Rank, long? Workflow, UsageLedgerRow? Run,
        AdmissionHoldRow? Hold = null);

    /// <summary>
    /// One member's spans over [<paramref name="from"/>, <paramref name="to"/>), from its ledger rows
    /// in <c>run_seq</c> order and its run in progress.
    /// </summary>
    private static async Task<IReadOnlyList<TeamActivitySpan>> SpansAsync(
        IReadOnlyList<UsageLedgerRow> runs, IReadOnlyList<AdmissionHoldRow> holds, Message? inProgress,
        DateTimeOffset from, DateTimeOffset to,
        DateTimeOffset now, IMessageLog log, ContainerId member, long floor, CancellationToken ct)
    {
        var stretches = new List<Stretch>();

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];

            if (run.QueuedAt is { } queued && run.StartedAt is { } started && queued < started)
            {
                stretches.Add(new Stretch(Waiting, queued, started, 2, run.Correlation, run));
            }

            if (run.StartedAt is { } start) stretches.Add(new Stretch(Running, start, run.EndedAt, 4, run.Correlation, run));

            // WHAT THE RUN LEFT BEHIND lasts until the member's next run is queued or starts - the
            // earliest time that run recorded - or, after its last, until its run in progress or now.
            var next = i + 1 < runs.Count
                ? Earliest(runs[i + 1])
                : inProgress?.OccurredAt ?? DateTimeOffset.MaxValue;

            var after = After(run.RunOutcome);
            stretches.Add(new Stretch(after, run.EndedAt, next, 1, after == Idle ? null : run.Correlation, run));
        }

        if (inProgress is not null)
        {
            stretches.Add(new Stretch(Running, inProgress.OccurredAt, DateTimeOffset.MaxValue, 4, inProgress.CorrelationId, null));
        }

        // WHAT ADMISSION RECORDED: a hold is held over a wait, and over what the last run left behind
        // while the next one is still queued, until it was released; one still held is open.
        foreach (var hold in holds)
        {
            stretches.Add(new Stretch(Held, hold.HeldAt, hold.ReleasedAt ?? DateTimeOffset.MaxValue, 3, null, null, hold));
        }

        // CLIPPED TO THE PERIOD, and to now: nothing is said about time that has not passed.
        var end = to < now ? to : now;
        var cuts = stretches
            .SelectMany(s => new[] { s.From, s.To })
            .Append(from)
            .Append(end)
            .Where(at => at >= from && at <= end)
            .Distinct()
            .Order()
            .ToList();

        var spans = new List<(Stretch Stretch, DateTimeOffset From, DateTimeOffset To)>();

        for (var i = 0; i + 1 < cuts.Count; i++)
        {
            var (a, b) = (cuts[i], cuts[i + 1]);
            var winner = stretches
                .Where(s => s.From <= a && s.To >= b && s.From < s.To)
                .OrderByDescending(s => s.Rank)
                .ThenByDescending(s => s.From)
                .FirstOrDefault();

            if (winner is null) continue;

            // A held stretch belongs to the run whose wait it is in, when that run has finished.
            if (winner.State == Held
                && stretches.FirstOrDefault(s => s.State == Waiting && s.From <= a && s.To >= b) is { } wait)
            {
                winner = winner with { Workflow = wait.Workflow };
            }

            if (spans.Count > 0 && spans[^1].To == a && Joins(spans[^1].Stretch, winner))
            {
                spans[^1] = (spans[^1].Stretch, spans[^1].From, b);
            }
            else
            {
                spans.Add((winner, a, b));
            }
        }

        var answer = new List<TeamActivitySpan>();

        foreach (var (stretch, start, stop) in spans)
        {
            var open = stretch.To == DateTimeOffset.MaxValue && stop == now;
            var reason = stretch.Run is { } run && stretch.State is Blocked or Failed
                ? await ReasonAsync(log, member, floor, run, ct)
                : null;

            if (stretch.Hold is { } hold)
            {
                answer.Add(new TeamActivitySpan(Held, start, open ? null : stop, stretch.Workflow, hold.Reason, hold.ReasonKind));
                continue;
            }

            answer.Add(new TeamActivitySpan(stretch.State, start, open ? null : stop, stretch.Workflow, reason));
        }

        return answer;
    }

    /// <summary>Two neighbouring stretches read as one span: the same state of the same run or the
    /// same hold, or idle after idle.</summary>
    private static bool Joins(Stretch a, Stretch b) =>
        a.State == b.State && a.Hold == b.Hold && (a.Run == b.Run || (a.State == Idle && b.State == Idle));

    private static string After(string runOutcome) => runOutcome switch
    {
        "blocked" => Blocked,
        "failed" => Failed,
        _ => Idle,
    };

    /// <summary>The earliest time a run recorded: its queue, else its start, else its end.</summary>
    private static DateTimeOffset Earliest(UsageLedgerRow run) => run.QueuedAt ?? run.StartedAt ?? run.EndedAt;

    /// <summary>
    /// A block's or failure's words, while the log still holds the row that said them: the failed
    /// row itself, or the member's own <c>blocked</c> or <c>needs-decision</c> row inside the run (not
    /// one closing a single item of a batch). Null once a Reset or retention has taken it.
    /// </summary>
    private static async Task<string?> ReasonAsync(
        IMessageLog log, ContainerId member, long floor, UsageLedgerRow run, CancellationToken ct)
    {
        if (run.RunOutcome == "failed")
        {
            return await log.FindAsync(run.RunSeq, ct) is { Type: MessageTypes.Failed } failed
                ? new ContainerMarks(null, null, failed).FailureReason
                : null;
        }

        var rows = await log.ReadMemberBeforeAsync(
            member, floor, run.RunSeq, [MessageTypes.Blocked, MessageTypes.NeedsDecision, MessageTypes.Started], 50, ct);

        foreach (var row in rows)
        {
            if (row.Type == MessageTypes.Started) break;
            if (row.Type == MessageTypes.Blocked && !HasItem(row)) return new ContainerMarks(row, null, null).BlockedReason;
            if (row.Type == MessageTypes.NeedsDecision) return new ContainerMarks(null, row, null).Question;
        }

        return null;
    }

    private static bool HasItem(Message row)
    {
        try
        {
            using var payload = JsonDocument.Parse(row.Payload);
            return payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty(PayloadFields.Item, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Overlaps(UsageLedgerRow run, DateTimeOffset from, DateTimeOffset to) =>
        run.EndedAt >= from && (run.QueuedAt ?? run.StartedAt ?? run.EndedAt) < to;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsManager(string name) => Same(name, TeamRegistry.DefaultManagerName);
}

/// <summary>The answer of <c>GET /api/teams/{team}/activity</c>; see <see cref="TeamActivity"/>.</summary>
public sealed record TeamActivityAnswer(
    DateTimeOffset? From,
    DateTimeOffset? To,
    DateTimeOffset ServerNow,
    string Window,
    IReadOnlyList<TeamActivityMember> Members);

/// <summary>One member's lane: <see cref="Current"/> is false for a member since removed.</summary>
public sealed record TeamActivityMember(
    string Member,
    string? Kind,
    bool IsManager,
    bool Current,
    IReadOnlyList<TeamActivitySpan> Spans);

/// <summary>One stretch of one state. <see cref="To"/> null is still open at the answer's
/// <c>serverNow</c>. <see cref="ReasonKind"/> is a held span's only.</summary>
public sealed record TeamActivitySpan(
    string State,
    DateTimeOffset From,
    DateTimeOffset? To,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Workflow = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasonKind = null);
