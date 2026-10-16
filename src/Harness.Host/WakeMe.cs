using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// The clock a wake is asked, due and fired by. Its own class so a test moves it alone: the Host's
/// <see cref="TimeProvider"/> also times things a test must not jump.
/// </summary>
public sealed class WakeClock(TimeProvider time)
{
    public DateTimeOffset Now => time.GetUtcNow();
}

/// <summary>Wakes the <see cref="WakeRunner"/> early: a wake was asked, cancelled or the clock moved.</summary>
public sealed class WakeSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

    public void Dispose() => _signal.Dispose();
}

/// <summary>A wake refused, with the status the route answers and the sentence the agent reads.</summary>
public sealed record WakeRefusal(int Status, string Error);

/// <summary>
/// <c>wake_me</c>: A MEMBER ASKS TO BE WOKEN ONCE, LATER, IN THE WORKFLOW IT IS WORKING.
///
/// <para>
/// A run that must wait for something outside the platform - DNS, a certificate, a deployment, a
/// rate limit - otherwise holds its run open sleeping, ends and hopes, or blocks on a person; and a
/// process it starts to look again later ends with its run. A wake is one instruction to the member
/// that asked, appended at its time with source <c>wake:&lt;id&gt;</c> and the asking row as its
/// causation, so it joins the workflow as a <c>tell</c> with causation does and is delivered as any
/// instruction is: held while the team is paused, waiting for a slot, never started twice.
/// </para>
///
/// <para>
/// <b>ITSELF, ONCE, SHORT AND FEW.</b> Addressed to the caller, read from its credential; at least
/// <see cref="MinimumDelay"/> and at most <c>wake.maxDelay</c> away; at most
/// <see cref="MaximumPerMember"/> pending per member and one per workflow. Every refusal is a
/// sentence naming the pending wakes and how to cancel one.
/// </para>
///
/// <para>
/// <b>THE WORKFLOW'S BOUNDS HOLD.</b> A wake over its workflow's budget, or whose instruction would
/// pass the chain limit, is refused at the ask and skipped at the fire with the bound's own sentence
/// (<see cref="WorkflowBounds"/>); its run is the workflow's like any other. An archived team's wake
/// is skipped with a row; a deleted team's wakes go with it.
/// </para>
///
/// <para>
/// <b>EVERY ACT IS A ROW, AND THE ROW AND THE STORED WAKE MOVE TOGETHER.</b> <c>wake.asked</c>,
/// <c>wake.cancelled</c>, <c>wake.skipped</c> and the firing instruction are each appended in one
/// transaction with the wake's own write (<see cref="IMessageLog.AppendWithinAsync"/>), so a wake
/// that fired cannot also be cancelled and none fires twice; <c>wake.fired</c> follows its
/// instruction. All four <c>wake.*</c> rows are out of the ledger, like progress.
/// </para>
/// </summary>
public sealed class WakeMe(
    IWakeStore store,
    IMessageLog log,
    TeamRegistry teams,
    ContainerHost host,
    WakeClock clock,
    WakeSignal signal,
    Func<TimeSpan> maxDelay,
    Func<int> depthLimit,
    ILogger<WakeMe>? diagnostics = null)
{
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(30);

    public const int MaximumPerMember = 3;

    public const int MaximumNote = 300;

    /// <summary>A wake this much past its time when it fires says how late: the runner fires on
    /// time while the Host is up, so lateness this large means the Host was down.</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromSeconds(30);

    private static readonly Regex DelayPattern = new(
        @"^\s*(?:(?<h>\d{1,4})\s*h)?\s*(?:(?<m>\d{1,6})\s*m)?\s*(?:(?<s>\d{1,7})\s*s)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public IWakeStore Store => store;

    /// <summary>
    /// <paramref name="text"/> as a delay: whole hours, minutes and seconds, such as <c>90s</c>,
    /// <c>10m</c>, <c>2h</c> or <c>1h30m</c>. Null for anything else.
    /// </summary>
    public static TimeSpan? ParseDelay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = DelayPattern.Match(text);
        if (!match.Success || !(match.Groups["h"].Success || match.Groups["m"].Success || match.Groups["s"].Success))
        {
            return null;
        }

        static long Part(Match m, string name) =>
            m.Groups[name].Success ? long.Parse(m.Groups[name].Value, CultureInfo.InvariantCulture) : 0;

        return TimeSpan.FromSeconds(Part(match, "h") * 3600 + Part(match, "m") * 60 + Part(match, "s"));
    }

    /// <summary>A span in words: "45 seconds", "10 minutes", "2 hours 5 minutes".</summary>
    public static string Words(TimeSpan span)
    {
        var seconds = (long)Math.Max(0, Math.Round(span.TotalSeconds));
        if (seconds < 60) return Plural(seconds, "second");

        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var rest = seconds % 60;

        var parts = new List<string>();
        if (hours > 0) parts.Add(Plural(hours, "hour"));
        if (minutes > 0) parts.Add(Plural(minutes, "minute"));
        if (hours == 0 && rest > 0) parts.Add(Plural(rest, "second"));

        return string.Join(' ', parts);
    }

    private static string Plural(long count, string unit) =>
        count == 1 ? $"1 {unit}" : $"{count.ToString(CultureInfo.InvariantCulture)} {unit}s";

    /// <summary>An instant on the Host's clock - the clock a member's process reads - and in UTC.</summary>
    public static string Both(DateTimeOffset at)
    {
        var zone = TimeZoneInfo.Local;
        var utc = at.ToUniversalTime();

        // A Host on UTC has one clock to name, not two that say the same.
        if (zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime)
        {
            return $"{utc:yyyy-MM-dd HH:mm:ss} UTC";
        }

        var local = TimeZoneInfo.ConvertTime(at, zone);
        return $"{local:yyyy-MM-dd HH:mm:ss} {zone.Id} ({utc:yyyy-MM-dd HH:mm:ss} UTC)";
    }

    /// <summary>One pending wake as an agent reads it in a refusal or an answer.</summary>
    public static string Describe(PendingWake wake) =>
        $"wake {wake.Id.ToString(CultureInfo.InvariantCulture)} in workflow {wake.Workflow.ToString(CultureInfo.InvariantCulture)}, "
        + $"due {wake.DueAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC: \"{wake.Note}\"";

    private static string CancelHint(PendingWake wake) =>
        $"cancel it with wake_me and cancel {wake.Id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// <paramref name="member"/> asks to be woken <paramref name="delay"/> from now with
    /// <paramref name="note"/>, from inside the run that is handling <paramref name="causation"/>.
    /// Answers the stored wake, or the refusal.
    /// </summary>
    public async Task<(PendingWake? Wake, WakeRefusal? Refused)> AskAsync(
        ContainerId member, long? causation, string? delay, string? note, CancellationToken ct = default)
    {
        if (ParseDelay(delay) is not { } span)
        {
            return Refuse(400,
                $"'{delay}' is not a delay. Say how long from now as whole hours, minutes or seconds: 90s, 10m, 2h or 1h30m.");
        }

        if (span < MinimumDelay)
        {
            return Refuse(400,
                $"A wake is at least {Words(MinimumDelay)} away, and {Words(span)} is sooner. "
                + "If it is that close, look now instead of ending your run.");
        }

        var furthest = maxDelay();
        if (span > furthest)
        {
            return Refuse(400,
                $"A wake is at most {Words(furthest)} away (the instance's wake.maxDelay setting), and "
                + $"{Words(span)} is further. A wake is once, not a schedule: for work that repeats, "
                + "ask the person for a trigger.");
        }

        var line = OneLine(note);
        if (line.Length == 0)
        {
            return Refuse(400, "Say in the note, in one line, what to look at when you are woken.");
        }

        if (line.Length > MaximumNote)
        {
            return Refuse(400, $"The note is {line.Length} characters, and a wake's note is at most {MaximumNote}. Say it in one shorter line.");
        }

        if (causation is not { } running || await log.FindAsync(running, ct) is not { } found || found.CorrelationId <= 0)
        {
            return Refuse(409,
                "You are not running a workflow, so there is nothing to wake you in. Ask from inside "
                + "the run that is doing the work.");
        }

        var workflow = found.CorrelationId;

        // THE CHAIN LIMIT, on the instruction the wake will append: one step past the asking row,
        // which is one past the row this run is handling.
        if (depthLimit() is > 0 and var limit && found.Depth + 2 > limit)
        {
            return Refuse(409, WorkflowBounds.DepthRefusal(found.Depth + 2));
        }

        if (await BoundRefusalAsync(member.Team, workflow, ct) is { } bound)
        {
            return Refuse(409, bound);
        }

        var now = clock.Now;
        var due = now + span;
        WakeRefusal? refused = null;
        PendingWake? stored = null;

        // THE LIMITS ARE READ INSIDE THE TRANSACTION THAT STORES THE WAKE, so two asks at once
        // cannot both pass them.
        await log.AppendWithinAsync(
            async (connection, transaction, token) =>
            {
                var pending = await store.ForMemberWithinAsync(connection, transaction, member, token);

                if (pending.FirstOrDefault(w => w.Workflow == workflow) is { } same)
                {
                    refused = new WakeRefusal(409,
                        $"You already have a wake pending in this workflow: {Describe(same)}. A member has "
                        + $"at most one per workflow. Keep it, or {CancelHint(same)} and ask again.");
                    return null;
                }

                if (pending.Count >= MaximumPerMember)
                {
                    refused = new WakeRefusal(409,
                        $"You already have {pending.Count} pending wakes, which is the most a member may hold: "
                        + string.Join("; ", pending.Select(Describe))
                        + $". Cancel one with wake_me and cancel <id> before asking for another.");
                    return null;
                }

                stored = await store.InsertWithinAsync(
                    connection, transaction,
                    new PendingWake(0, member.Team, member.Name, workflow, 0, now, due, line),
                    token);

                return new NewMessage(
                    MessageTypes.WakeAsked,
                    Payload(stored, new Dictionary<string, object?>()),
                    member.ToString(),
                    running);
            },
            async (connection, transaction, row, token) =>
            {
                await store.SetAskedWithinAsync(connection, transaction, stored!.Id, row.Seq, token);
                stored = stored with { AskedSeq = row.Seq };
            },
            ct);

        if (refused is not null) return (null, refused);

        await ChangedAsync(member, ct);
        return (stored, null);
    }

    /// <summary>The sentence the asking member is answered with.</summary>
    public string AskedNotice(PendingWake wake) =>
        $"Wake {wake.Id.ToString(CultureInfo.InvariantCulture)} is set for {Both(wake.DueAt)}, "
        + $"in {Words(wake.DueAt - clock.Now)}, in workflow {wake.Workflow.ToString(CultureInfo.InvariantCulture)}. "
        + "You will be told once, with your note; end your run now. "
        + $"To cancel it, call wake_me with cancel {wake.Id.ToString(CultureInfo.InvariantCulture)}.";

    /// <summary>
    /// Cancels wake <paramref name="id"/>, when it is still pending and, with
    /// <paramref name="owner"/>, that member's own. <paramref name="by"/> is who cancelled it, as the
    /// row says it. Answers the cancelled wake, or null when there was none to cancel.
    /// </summary>
    public async Task<PendingWake?> CancelAsync(
        long id, string by, string reason, ContainerId? owner = null, string? team = null, CancellationToken ct = default)
    {
        // NOT ITS OWN, OR NOT THIS TEAM'S, IS ANSWERED EXACTLY AS A MISSING WAKE. A stored wake never
        // changes hands, so reading it before the transaction that takes it decides nothing late.
        if (await store.FindAsync(id, ct) is not { } found
            || (owner is { } who && !found.Container.Equals(who))
            || (team is not null && !string.Equals(found.Team, team, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        PendingWake? taken = null;

        await log.AppendWithinAsync(
            async (connection, transaction, token) =>
            {
                taken = await store.TakeWithinAsync(connection, transaction, id, token);
                if (taken is null) return null;

                return new NewMessage(
                    MessageTypes.WakeCancelled,
                    Payload(taken, new Dictionary<string, object?>
                    {
                        [PayloadFields.By] = by,
                        [PayloadFields.Reason] = reason,
                    }),
                    taken.Container.ToString(),
                    taken.AskedSeq);
            },
            (_, _, _, _) => Task.CompletedTask,
            ct);

        if (taken is not null) await ChangedAsync(taken.Container, ct);
        return taken;
    }

    /// <summary>Cancels every wake pending in <paramref name="workflow"/>, a row each - a person's
    /// close, or the owner's declaration. Answers what it cancelled.</summary>
    public async Task<IReadOnlyList<PendingWake>> CancelWorkflowAsync(
        long workflow, string by, string reason, CancellationToken ct = default)
    {
        var cancelled = new List<PendingWake>();

        foreach (var wake in await store.ForWorkflowAsync(workflow, ct))
        {
            if (await CancelAsync(wake.Id, by, reason, ct: ct) is { } done) cancelled.Add(done);
        }

        return cancelled;
    }

    /// <summary>The sentence a declaration that cancelled wakes answers with.</summary>
    public static string CancelledNotice(IReadOnlyList<PendingWake> cancelled) =>
        "Declaring the workflow complete cancelled its pending "
        + (cancelled.Count == 1 ? "wake" : "wakes") + ": "
        + string.Join("; ", cancelled.Select(w => $"{w.Member}'s " + Describe(w))) + ".";

    /// <summary>
    /// Fires or skips every wake due by the clock now. One that came due while the Host was down
    /// fires on the first pass after the start, once, saying how late (<see cref="LateAfter"/>).
    /// </summary>
    public async Task FireDueAsync(CancellationToken ct = default)
    {
        var now = clock.Now;

        foreach (var wake in await store.DueAsync(now, ct))
        {
            try
            {
                await FireAsync(wake, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // ONE BAD WAKE DOES NOT STOP THE OTHERS; it is still stored and is tried again.
                diagnostics?.LogWarning(exception, "Firing wake {Wake} for {Member} failed.", wake.Id, wake.Container);
            }
        }
    }

    private async Task FireAsync(PendingWake wake, DateTimeOffset now, CancellationToken ct)
    {
        // A DELETED TEAM'S WAKES GO WITH IT: deletion removes them, and one a deletion missed is
        // removed here with no row - there is no team left to write one for.
        if (teams.ExistingName(wake.Team) is not { } stored)
        {
            await store.DeleteForTeamAsync(wake.Team, ct);
            return;
        }

        if (teams.IsArchived(stored))
        {
            await SkipAsync(wake,
                $"The team '{teams.LabelFor(stored)}' is archived, and a wake never starts work on an "
                + "archived team. Nothing was sent.", ct);
            return;
        }

        if (host.Find(wake.Container) is not { } container)
        {
            await SkipAsync(wake, $"'{wake.Member}' is no longer a member of this team. Nothing was sent.", ct);
            return;
        }

        if (!(await log.OpenWorkflowsAmongAsync([wake.Workflow], ct)).Contains(wake.Workflow))
        {
            await SkipAsync(wake, $"Workflow {wake.Workflow} has ended. Nothing was sent.", ct);
            return;
        }

        if (await log.FindAsync(wake.AskedSeq, ct) is not { } asked)
        {
            await SkipAsync(wake, "The row that asked for this wake is no longer on the log. Nothing was sent.", ct);
            return;
        }

        if (depthLimit() is > 0 and var limit && asked.Depth + 1 > limit)
        {
            await SkipAsync(wake, WorkflowBounds.DepthRefusal(asked.Depth + 1), ct);
            return;
        }

        if (await BoundRefusalAsync(stored, wake.Workflow, ct) is { } bound)
        {
            await SkipAsync(wake, bound, ct);
            return;
        }

        var late = now - wake.DueAt;
        var lateSeconds = late >= LateAfter ? (long?)Math.Round(late.TotalSeconds) : null;
        var text = InstructionFor(wake, lateSeconds is null ? null : late);
        var parts = InstructionText.Split(text);

        PendingWake? taken = null;

        // THE INSTRUCTION AND THE WAKE'S REMOVAL ARE ONE TRANSACTION: a wake cancelled a moment
        // ago is gone and appends nothing, and one that fired cannot fire again. The type is built
        // from the FOUND container's id, as `tell` builds it.
        var instruction = await log.AppendWithinAsync(
            async (connection, transaction, token) =>
            {
                taken = await store.TakeWithinAsync(connection, transaction, wake.Id, token);
                if (taken is null) return null;

                return new NewMessage(
                    MessageTypes.InstructionFor(container.Id),
                    Payload(taken, new Dictionary<string, object?>
                    {
                        [PayloadFields.Instruction] = text,
                        ["subject"] = parts.Subject,
                        [PayloadFields.Body] = parts.Body,
                        [PayloadFields.AskedAt] = Instant(taken.AskedAt),
                        [PayloadFields.LateSeconds] = lateSeconds,
                    }),
                    MessageTypes.WakeSource(taken.Id),
                    taken.AskedSeq);
            },
            (_, _, _, _) => Task.CompletedTask,
            ct);

        if (instruction is null || taken is null) return;

        await log.AppendAsync(
            new NewMessage(
                MessageTypes.WakeFired,
                Payload(taken, new Dictionary<string, object?>
                {
                    [PayloadFields.AskedAt] = Instant(taken.AskedAt),
                    [PayloadFields.LateSeconds] = lateSeconds,
                    [PayloadFields.Instruction] = instruction.Seq,
                }),
                taken.Container.ToString(),
                taken.AskedSeq),
            ct);

        diagnostics?.LogInformation(
            "Fired wake {Wake} for {Member} in workflow {Workflow} (seq {Seq}).",
            taken.Id, taken.Container, taken.Workflow, instruction.Seq);

        await ChangedAsync(taken.Container, ct);
    }

    private async Task SkipAsync(PendingWake wake, string reason, CancellationToken ct)
    {
        PendingWake? taken = null;

        await log.AppendWithinAsync(
            async (connection, transaction, token) =>
            {
                taken = await store.TakeWithinAsync(connection, transaction, wake.Id, token);
                if (taken is null) return null;

                return new NewMessage(
                    MessageTypes.WakeSkipped,
                    Payload(taken, new Dictionary<string, object?> { [PayloadFields.Reason] = reason }),
                    taken.Container.ToString(),
                    taken.AskedSeq);
            },
            (_, _, _, _) => Task.CompletedTask,
            ct);

        if (taken is not null) await ChangedAsync(taken.Container, ct);
    }

    /// <summary>The words the woken member reads: the note, when it asked, and how late, if late.</summary>
    public static string InstructionFor(PendingWake wake, TimeSpan? late)
    {
        var text = new StringBuilder();
        text.Append("Wake: ").Append(wake.Note).Append("\n\n");
        text.Append(CultureInfo.InvariantCulture,
            $"You asked to be woken (wake {wake.Id}, asked at {wake.AskedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC, ");
        text.Append(CultureInfo.InvariantCulture, $"due at {wake.DueAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC). ");

        if (late is { } by)
        {
            text.Append("It fires ").Append(Words(by))
                .Append(" late: the platform was not running when it came due. ");
        }

        text.Append("Your note: ").Append(wake.Note).Append("\n\n");
        text.Append(
            "Look at what the note names and carry on with the work in this workflow. This wake has fired "
            + "and is gone; if you still have to wait, write down what for, set another with wake_me, "
            + "and end your run.");

        return text.ToString();
    }

    /// <summary>
    /// The workflow's own bounds now: paused for its spend, or over its budget. Null when neither.
    /// </summary>
    private async Task<string?> BoundRefusalAsync(string team, long workflow, CancellationToken ct)
    {
        var thread = await log.ReadCorrelationAsync(workflow, ct);
        if (WorkflowPause.PausedBy(thread) is { } paused)
        {
            return "This workflow is paused for its spend until a person resumes it: "
                + (PayloadText(paused, PayloadFields.Reason) ?? "it reached its budget") + ".";
        }

        return await WorkflowBounds.OverBudgetAsync(teams, log, team, workflow, ct);
    }

    /// <summary>
    /// Every member's card says its pending wakes now. Asked after every change and on the runner's
    /// heartbeat, so a member restored or re-registered since is put right.
    /// </summary>
    public async Task RefreshMarksAsync(CancellationToken ct = default)
    {
        var all = (await store.AllAsync(ct))
            .GroupBy(w => w.Container)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<WakeView>)g.Select(w => w.View()).ToList());

        foreach (var snapshot in host.Snapshots())
        {
            var id = new ContainerId(snapshot.Team, snapshot.Id);
            host.Find(id)?.MarkWakes(all.GetValueOrDefault(id) ?? []);
        }
    }

    private async Task ChangedAsync(ContainerId member, CancellationToken ct)
    {
        try
        {
            var mine = (await store.AllAsync(ct))
                .Where(w => w.Container.Equals(member))
                .Select(w => w.View())
                .ToList();

            host.Find(member)?.MarkWakes(mine);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics?.LogWarning(exception, "Could not mark {Member}'s pending wakes.", member);
        }

        signal.Signal();
    }

    private static (PendingWake?, WakeRefusal?) Refuse(int status, string error) => (null, new WakeRefusal(status, error));

    /// <summary>The note as one line: every run of whitespace, line breaks included, one space.</summary>
    public static string OneLine(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "" : Regex.Replace(note.Trim(), @"\s+", " ");

    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static string Payload(PendingWake wake, Dictionary<string, object?> extra)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [PayloadFields.Wake] = wake.Id,
            [PayloadFields.DueAt] = Instant(wake.DueAt),
            [PayloadFields.Note] = wake.Note,
        };

        foreach (var (key, value) in extra)
        {
            if (value is not null) payload[key] = value;
        }

        return JsonSerializer.Serialize(payload);
    }

    private static string? PayloadText(Message row, string field)
    {
        try
        {
            using var json = JsonDocument.Parse(row.Payload);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Fires wakes when they are due, sleeping until the next one rather than ticking - the
/// <see cref="TriggerRunner"/>'s shape. Its first pass, at start, fires every wake that came due
/// while the Host was down, once, each saying how late it is.
/// </summary>
public sealed class WakeRunner(WakeMe wakes, WakeClock clock, WakeSignal signal, ILogger<WakeRunner> logger)
    : BackgroundService
{
    /// <summary>The longest it sleeps without looking again: a heartbeat for a clock that jumped and
    /// for the cards of members restored since.</summary>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = MaximumWait;

            try
            {
                await wakes.FireDueAsync(stoppingToken);
                await wakes.RefreshMarksAsync(stoppingToken);

                if (await wakes.Store.EarliestDueAsync(stoppingToken) is { } earliest)
                {
                    var until = earliest - clock.Now;
                    delay = until > MaximumWait ? MaximumWait : until < MinimumWait ? MinimumWait : until;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A wake sweep failed.");
            }

            try
            {
                await signal.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
