using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// WHAT A WORKFLOW'S ROWS SAY, read out of their payloads - the C# half of the workflow projection,
/// shared by both providers' logs so a SQL Server board and a SQLite one cannot word the same row
/// differently. The SQL that finds the rows is each provider's own; what a row MEANS is decided here.
/// </summary>
public static class WorkflowPayloads
{
    /// <summary>
    /// A DISPATCH ROOT'S ITEM, out of its payload - <c>item</c> (the route number) and <c>title</c>
    /// as `BacklogEndpoints` writes them - or null when either is missing or the payload is not
    /// JSON. Nothing is made up for a missing title: an item with no words is the number alone,
    /// which the reader already has.
    /// </summary>
    public static WorkflowItem? ItemFrom(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;

        try
        {
            using var json = JsonDocument.Parse(payload);

            if (json.RootElement.ValueKind is not JsonValueKind.Object) return null;

            return json.RootElement.TryGetProperty("item", out var idField)
                && idField.ValueKind is JsonValueKind.Number
                && idField.TryGetInt64(out var id)
                && json.RootElement.TryGetProperty("title", out var titleField)
                && titleField.ValueKind is JsonValueKind.String
                && titleField.GetString() is { Length: > 0 } title
                    ? new WorkflowItem(id, title)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The sentence and the figure off a `workflow.paused` payload.
    ///
    /// BOTH OPTIONAL, INDEPENDENTLY. The log is append-only and replayed, so a row written by an
    /// older or different writer may carry neither - and a pause with no reason is still a pause.
    /// The STATE comes from the row's existence, never from these, which is why a parse failure
    /// here is silent rather than a workflow that stops reading as paused.
    /// </summary>
    public static (string? Reason, long? Limit) PauseDetailFrom(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return (null, null);

        try
        {
            using var json = JsonDocument.Parse(payload);

            if (json.RootElement.ValueKind is not JsonValueKind.Object) return (null, null);

            var reason =
                json.RootElement.TryGetProperty(PayloadFields.Reason, out var reasonField)
                && reasonField.ValueKind is JsonValueKind.String
                && reasonField.GetString() is { Length: > 0 } text
                    ? text
                    : null;

            var limit =
                json.RootElement.TryGetProperty(PayloadFields.Limit, out var limitField)
                && limitField.ValueKind is JsonValueKind.Number
                && limitField.TryGetInt64(out var bound)
                    ? bound
                    : (long?)null;

            return (reason, limit);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// A ROOT MESSAGE'S SUBJECT, out of its payload - or null. Two shapes, both live, for
    /// <see cref="InstructionText"/>'s own reason: a row may carry `subject` (and `body`) beside
    /// `instruction`, or `instruction` alone, and the log is APPEND-ONLY and REPLAYED, so both
    /// shapes are met on the same team.
    ///
    /// READS, NEVER RE-DERIVES: an explicit `subject` field wins outright, exactly as
    /// `KanbanProjector.ExtractTitleAndBody` reads the identical stored field for a card's title.
    /// The fallback is `InstructionText.Split`, the ONE place that decides what a subject is - not a
    /// clamp of its own, which would let this answer disagree with the card's title over the same
    /// row.
    /// </summary>
    public static string? SubjectFrom(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(payload);

            if (json.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            if (json.RootElement.TryGetProperty("subject", out var subjectField)
                && subjectField.ValueKind is JsonValueKind.String
                && subjectField.GetString() is { Length: > 0 } subject)
            {
                return subject;
            }

            if (json.RootElement.TryGetProperty("instruction", out var instructionField)
                && instructionField.ValueKind is JsonValueKind.String)
            {
                var parts = InstructionText.Split(instructionField.GetString());
                return parts.Subject.Length > 0 ? parts.Subject : null;
            }

            return null;
        }
        catch (JsonException)
        {
            // THE PAYLOAD IS OPAQUE TO THIS STORE, exactly as it is to `KanbanProjector`'s own
            // `StringField`: nothing here may assume a row appended by some other caller is even
            // JSON, and a subject nobody can read is the same answer as one nobody wrote.
            return null;
        }
    }

    /// <summary>
    /// Whether a `failed` row says a person stopped the run (<see cref="PayloadFields.StoppedByPerson"/>).
    /// A payload nobody can parse is a normal failure.
    /// </summary>
    public static bool StoppedByPerson(string? payload)
    {
        if (payload is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty(PayloadFields.StoppedByPerson, out var value) && value.GetBoolean();
        }
        catch (Exception)
        {
            // If we can't parse the payload, treat it as a normal failure
            return false;
        }
    }
}

/// <summary>Who a qualified source is, as the workflow projection reads it.</summary>
public static class TeamSources
{
    /// <summary>
    /// The bare member name from a qualified source. Neither half of a ContainerId may contain a
    /// `/`, so the last one is the separator - the same reading the SPA's own fold does.
    /// </summary>
    public static string NameOf(string source) => source[(source.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Whether <paramref name="source"/> is one of <paramref name="team"/>'s own members: <c>Team/x</c>,
    /// case-insensitive, and not a nested <c>Team/x/y</c>.
    /// </summary>
    public static bool TeamOwn(string team, string source) =>
        source.Length > team.Length + 1
        && source.StartsWith(team + "/", StringComparison.OrdinalIgnoreCase)
        && source.IndexOf('/', team.Length + 1) < 0;
}

/// <summary>
/// ONE WORKFLOW'S FACTS, gathered query by query in the order the projection asks them, and the
/// state ladder that turns them into a <see cref="TeamWorkflowTiming"/>. Each provider's log runs
/// its own SQL and hands the rows here, so the ranking exists once.
/// </summary>
public sealed class WorkflowTimingFacts(long correlation)
{
    private readonly List<string> _failedOrder = [];

    // Track which members have any non-stopped failures
    private readonly HashSet<string> _membersWithRealFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MemberRuns> _runs = new(StringComparer.OrdinalIgnoreCase);

    private bool _everyTerminalCompleted = true;
    private int _terminals;

    public long Correlation { get; } = correlation;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public string? Subject { get; set; }
    public WorkflowItem? Item { get; set; }
    public bool Completed { get; set; }
    public bool Closed { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public string? PausedReason { get; set; }
    public long? PausedLimit { get; set; }
    public string? BlockedBy { get; private set; }
    public string? AwaitingFrom { get; private set; }
    public int RunsFailed { get; set; }

    /// <summary>
    /// The root row: a subject for an addressed instruction, an item for a dispatch. NULL FOR
    /// ANYTHING ELSE - a workflow can be rooted in `TriggerSweep`'s scheduled wakes or the
    /// repositories instruction in `Teams.cs`, and none of those carry a subject to report.
    /// </summary>
    public void Root(string type, string? payload)
    {
        if (type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
        {
            Subject = WorkflowPayloads.SubjectFrom(payload);
        }

        // A DISPATCH ROOT NAMES ITS ITEM, with the title it had when it was dispatched. The current
        // title is the Host's to overlay (`WorkflowItems`): no store can read the backlog.
        else if (type == MessageTypes.BacklogItemDispatched)
        {
            Item = WorkflowPayloads.ItemFrom(payload);
        }
    }

    /// <summary>One `workflow.completed` or `workflow.closed` row type, with the last close's instant.</summary>
    public void Declared(string type, string? closedAt)
    {
        if (string.Equals(type, MessageTypes.WorkflowCompleted, StringComparison.Ordinal))
        {
            Completed = true;
        }
        else
        {
            if (closedAt is not null) ClosedAt = MessageRows.ReadStamp(closedAt);
            Closed = true;
        }
    }

    /// <summary>A pause still in force: when, and the sentence and figure off its payload.</summary>
    public void Paused(string at, string? payload)
    {
        PausedAt = MessageRows.ReadStamp(at);
        (PausedReason, PausedLimit) = WorkflowPayloads.PauseDetailFrom(payload);
    }

    /// <summary>One member's LAST run outcome in this workflow: `completed` or `failed`.</summary>
    public void LastOutcome(string source, string type)
    {
        var member = TeamSources.NameOf(source);

        _terminals++;

        if (type != MessageTypes.Completed) _everyTerminalCompleted = false;

        if (type == MessageTypes.Failed && _membersWithRealFailures.Add(member)) _failedOrder.Add(member);
    }

    /// <summary>One member's last `failed` row, read for whether a person stopped the run.</summary>
    public void LastFailure(string source, string? payload)
    {
        var member = TeamSources.NameOf(source);

        if (!WorkflowPayloads.StoppedByPerson(payload)) return;

        // A run stopped by a person is Awaiting, not Failed
        AwaitingFrom ??= member;
        // Remove from real failures list
        if (_membersWithRealFailures.Remove(member)) _failedOrder.Remove(member);
        // The run was stopped on purpose, so not all runs completed successfully
        _everyTerminalCompleted = false;
    }

    /// <summary>
    /// A LIVE DECLARATION - a `blocked` or `needs-decision` its member has not started again since.
    /// A member carrying one is not one whose runs succeeded, whatever its own run's outcome says:
    /// `UNDECLARED` and `AWAITING` are documented as mutually exclusive BY CONSTRUCTION, and this is
    /// the line that makes that true rather than vacuous.
    /// </summary>
    public void LiveDeclaration(string source, string type)
    {
        var member = TeamSources.NameOf(source);

        _everyTerminalCompleted = false;

        if (type == MessageTypes.Blocked) BlockedBy ??= member;
        else AwaitingFrom ??= member;
    }

    /// <summary>
    /// One `started` row and the stamp of the terminal row that paired with it, or null for a run
    /// that never finished - counted as unfinished, never as zero and never as "still running".
    /// </summary>
    public void RunPair(string source, string startedAt, string? endedAt)
    {
        var member = TeamSources.NameOf(source);

        if (!_runs.TryGetValue(member, out var row))
        {
            row = new MemberRuns();
            _runs[member] = row;
        }

        row.Runs++;

        if (endedAt is null)
        {
            row.Unfinished++;
            return;
        }

        var from = MessageRows.ReadStamp(startedAt);
        var to = MessageRows.ReadStamp(endedAt);

        // Clamped at zero rather than allowed negative. Two rows a millisecond apart can round the
        // wrong way, and a negative run time on a dialog is the kind of visible nonsense that gets
        // reported as a pairing bug.
        var seconds = (long)Math.Max(0, Math.Round((to - from).TotalSeconds));

        row.Counted++;
        row.Seconds += seconds;
    }

    /// <summary>
    /// THE RANKING, and every step of it is an argument rather than an ordering. A platform failure
    /// outranks an agent's decision to stop, which outranks an agent's decision to ask - because a
    /// run that died after asking did not get its question answered either. The tab chip orders
    /// these identically, or a chip and the card beneath it disagree about one member.
    ///
    /// `Running` here does NOT mean a member is running: the log cannot see a roster. It means the
    /// workflow is open and no member's last terminal fact says anything louder. The caller decides
    /// RUNNING from live containers, and it ranks first.
    ///
    /// `Closed` ranks above `Failed`/`Blocked`/`Awaiting`, because it is a person's declaration to
    /// stop counting a workflow. However, `Completed` still outranks `Closed`, because a declaration
    /// of delivery is louder than an administrative close.
    ///
    /// `Paused` SITS BETWEEN `Closed` AND `Failed`, and that placement is the argument. A paused
    /// workflow whose last member run also failed will not run again whatever the failure says - so
    /// telling a reader FAILED sends them to inspect a run when the reason nothing is moving is the
    /// budget, and the fix is on a different screen entirely. `Completed` and `Closed` still outrank
    /// it: a workflow paused and then closed is closed.
    ///
    /// DERIVED FROM `pausedAt` AND NOWHERE ELSE, so the word and the timestamp cannot disagree, and
    /// no client re-derives it - the same pairing `blockedBy`/`Blocked` and `awaitingFrom`/`Awaiting`
    /// already have.
    ///
    /// <paramref name="spend"/> and <paramref name="sinceNudge"/> are the workflow's whole spend and
    /// its spend since the last nudge; both are returned, or neither, so a client cannot end up with
    /// one and quietly fall back to the wrong one.
    /// </summary>
    public TeamWorkflowTiming Build(DateTimeOffset serverNow, WorkflowSpend spend, WorkflowSpend sinceNudge)
    {
        var failedMembers = new List<string>(_failedOrder);

        var members = _runs
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new MemberExecution(
                entry.Key,
                entry.Value.Runs,

                // NULL, NOT ZERO, for a member whose every run was cut short. It spent real time
                // and nothing here knows how much.
                entry.Value.Counted > 0 ? entry.Value.Seconds : null,
                entry.Value.Unfinished))
            .ToList();

        var counted = _runs.Values.Sum(row => row.Counted);
        var unfinished = _runs.Values.Sum(row => row.Unfinished);
        var execution = _runs.Values.Sum(row => row.Seconds);

        var state =
            Completed ? "Completed"
            : Closed ? "Closed"
            : PausedAt is not null ? "Paused"
            : failedMembers.Count > 0 ? "Failed"
            : BlockedBy is not null ? "Blocked"
            : AwaitingFrom is not null ? "Awaiting"

            // UNDECLARED CARRIES ONLY THE CLAUSES THE LOG CAN ANSWER: the workflow is open and every
            // member that ran ended `completed`. Nothing Running, no queue depth and the grace
            // period belong to the caller, which owns the roster and the clock - and there must go
            // on being exactly one grace constant, which is in the SPA.
            : _terminals > 0 && _everyTerminalCompleted ? "Undeclared"
            : "Running";

        // Per-workflow spend for budget checking. Null when there are no completed/failed runs.
        var spendToReturn = Correlation > 0 && (spend.RunsWithMeasuredUsage > 0 || spend.RunsWithoutUsage > 0)
            ? spend
            : null;

        // AND THE SAME MEASUREMENT OVER THE WINDOW THE BOUND ACTUALLY USES, null under the same
        // condition: the figure above is the WHOLE workflow and never comes back down.
        var sinceNudgeToReturn = spendToReturn is null ? null : sinceNudge;

        return new TeamWorkflowTiming(
            true,
            Correlation,
            state,
            StartedAt,

            // NULL WHILE OPEN, and open is the ordinary case: `workflow.completed` is a manager's
            // declaration and most workflows never get one. A CLOSED WORKFLOW HAS ENDED TOO,
            // whatever word it wears: `workflow.closed` is terminal for `WorkflowOpenSql`, so a
            // closed workflow is not open and its clock must stop.
            Completed || Closed ? LastActivityAt : null,
            serverNow,
            execution,
            unfinished > 0,
            counted,
            unfinished,
            BlockedBy,
            RunsFailed,
            failedMembers,
            null,
            members,
            LastActivityAt,
            AwaitingFrom,
            Subject,
            spendToReturn,

            // A PAUSED WORKFLOW IS STILL OPEN, which is why `EndedAt` above is untouched here.
            // `WorkflowOpenSql` counts it open, `StatusCommand.IsOpen` and the SPA's `workflowOpen`
            // both test `endedAt == null`, and a paused workflow that vanished from the open list
            // would be one nobody could find in order to resume it.
            PausedAt,
            PausedReason,
            PausedLimit,
            sinceNudgeToReturn,
            Item: Item);
    }

    /// <summary>
    /// A team with no workflow to report. AVAILABLE FALSE AND NOT A ZERO: zero is a measured
    /// duration and this is an absent one, so the caller renders an em dash.
    /// </summary>
    public static TeamWorkflowTiming Unavailable(DateTimeOffset serverNow, string missing) =>
        new(false, null, null, null, null, serverNow, 0, false, 0, 0, null, 0, [], missing, []);

    /// <summary>Runs accumulated for one member while the pairing query is read.</summary>
    private sealed class MemberRuns
    {
        public int Runs;
        public int Counted;
        public int Unfinished;
        public long Seconds;
    }
}

/// <summary>
/// WHERE EACH OF A SET OF WORKFLOWS STANDS, folded from the one tagged statement each provider's
/// <see cref="IMessageLog.WorkflowStandingsAmongAsync"/> runs. Each row is
/// <c>(facet, correlation, source, type, at, flag, seq)</c>, with correlation, flag and seq 64-bit
/// integers; the facets are <c>row</c>, <c>end</c>, <c>pause</c>, <c>last</c> and <c>blocked</c>.
///
/// <para>
/// THE LADDER IS <see cref="WorkflowTimingFacts.Build"/>'S: Completed (any completion), Closed (a close
/// nothing woke), Paused, then Failed (a member whose last outcome failed, not stopped by a person),
/// then Blocked. Only the last two are answered, as <see cref="WorkflowStanding.Stuck"/>.
/// </para>
/// </summary>
public sealed class WorkflowStandingRows(IReadOnlyDictionary<long, string> teams)
{
    private readonly HashSet<long> _known = [];
    private readonly Dictionary<long, List<(long Seq, string Type, DateTimeOffset At, bool Woken)>> _ends = [];
    private readonly Dictionary<long, (long Paused, long Resumed)> _pauses = [];
    private readonly HashSet<long> _failed = [];
    private readonly HashSet<long> _blocked = [];

    /// <summary>Reads every row of <paramref name="reader"/>.</summary>
    public async Task ReadAsync(DbDataReader reader, CancellationToken ct)
    {
        while (await reader.ReadAsync(ct)) Add(reader);
    }

    private void Add(DbDataReader reader)
    {
        var correlation = reader.GetInt64(1);

        switch (reader.GetString(0))
        {
            case "row":
                _known.Add(correlation);
                break;

            case "end":
                if (!_ends.TryGetValue(correlation, out var list)) _ends[correlation] = list = [];
                list.Add((reader.GetInt64(6), reader.GetString(3), MessageRows.ReadStamp(reader.GetString(4)), reader.GetInt64(5) != 0));
                break;

            case "pause":
                var pause = _pauses.GetValueOrDefault(correlation);
                _pauses[correlation] = reader.GetString(3) == MessageTypes.WorkflowPaused
                    ? pause with { Paused = reader.GetInt64(6) }
                    : pause with { Resumed = reader.GetInt64(6) };
                break;

            case "last" when TeamSources.TeamOwn(teams[correlation], reader.GetString(2)):
                if (reader.GetString(3) == MessageTypes.Failed && reader.GetInt64(5) == 0) _failed.Add(correlation);
                break;

            case "blocked" when TeamSources.TeamOwn(teams[correlation], reader.GetString(2)):
                _blocked.Add(correlation);
                break;
        }
    }

    public Dictionary<long, WorkflowStanding> Build()
    {
        var standings = new Dictionary<long, WorkflowStanding>();

        foreach (var correlation in _known)
        {
            var rows = _ends.GetValueOrDefault(correlation) ?? [];
            var ending = rows.Where(r => !r.Woken).OrderBy(r => r.Seq).LastOrDefault();
            var open = ending.Type is null;

            var completed = rows.Any(r => r.Type == MessageTypes.WorkflowCompleted);
            var closed = rows.Any(r => r.Type == MessageTypes.WorkflowClosed && !r.Woken);
            var paused = _pauses.TryGetValue(correlation, out var p) && p.Paused > p.Resumed;

            var stuck = completed || closed || paused ? null
                : _failed.Contains(correlation) ? "Failed"
                : _blocked.Contains(correlation) ? "Blocked"
                : null;

            standings[correlation] = new WorkflowStanding(
                correlation,
                open,
                ending.Type == MessageTypes.WorkflowCompleted ? ending.At : null,
                stuck);
        }

        return standings;
    }
}
