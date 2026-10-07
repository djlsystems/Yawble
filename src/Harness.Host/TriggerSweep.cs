using System.Globalization;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;
using Harness.Host.Solutions;

namespace Harness.Host;

/// <summary>
/// Fires due schedules. Its own class for the same reason TeamDeletion and TeamReset are:
/// schedule-store, host, pending-delivery, message-log and tenant-log concerns do not belong on the
/// team registry.
/// </summary>
public sealed class TriggerSweep(
    ITriggerStore schedules,
    ContainerHost host,
    IPendingDeliveries pending,
    IMessageLog log,
    TenantLogging tenant,
    FolderWatch folders,
    ILogger<TriggerSweep> logger,
    TriggerCost? cost = null,
    SolutionWait? waiting = null)
{
    public async Task FireDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        // THE HEARTBEAT THAT ENDS A WAIT provided with no hook of its own, so its marks clear.
        if (waiting is not null)
        {
            try
            {
                await waiting.ObserveAllAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Reading the solution teams' missing inputs failed.");
            }
        }

        foreach (var row in await schedules.DueAsync(now, ct))
        {
            try
            {
                await FireOneAsync(row, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Schedule sweep failed for {ScheduleId}.", row.Id);
            }
        }
    }

    private async Task FireOneAsync(TriggerRow row, DateTimeOffset now, CancellationToken ct)
    {
        if (row.NextDueAt is null) return;
        if (!TryKind(row.Kind, out var kind)) throw new InvalidOperationException(
            $"Schedule '{row.Id}' has unknown kind '{row.Kind}'.");

        // A due folder trigger is a due POLL. It publishes `file.changed` when its folder has
        // changed and settled, and the event path delivers that - so nothing below (skip, busy,
        // instruction) applies to it here.
        if (kind == TriggerKind.FolderChange)
        {
            await folders.PollAsync(row, now, ct);
            return;
        }

        var source = SourceOf(row.Id);
        var due = row.NextDueAt.Value;
        var next = Next(row, kind, due);
        var missed = CountMissedOccurrences(row, kind, now, due, next);

        if (missed > 0)
        {
            var future = AdvanceToFuture(row, kind, now, due, next);
            await schedules.RecordOutcomeAsync(row.Id, firedAt: null, future, "missed", seq: null, row.MissedCount + missed, ct);
            await tenant.WriteAsAsync(
                source,
                actorEmail: null,
                TenantActions.ScheduleMissed,
                row.Id,
                row.Name,
                new
                {
                    team = row.Team,
                    container = row.Container,
                    missed,
                    dueAt = due.ToString("O", CultureInfo.InvariantCulture),
                    nextDueAt = future?.ToString("O", CultureInfo.InvariantCulture),
                },
                ct);
            return;
        }

        var id = new ContainerId(row.Team, row.Container);
        var container = host.Find(id);
        if (container is null)
        {
            await schedules.RecordOutcomeAsync(row.Id, firedAt: null, next, "member-missing", seq: null, row.MissedCount, ct);
            await tenant.WriteAsAsync(
                source,
                actorEmail: null,
                TenantActions.ScheduleMemberMissing,
                row.Id,
                row.Name,
                new
                {
                    team = row.Team,
                    container = row.Container,
                    dueAt = due.ToString("O", CultureInfo.InvariantCulture),
                    nextDueAt = next?.ToString("O", CultureInfo.InvariantCulture),
                },
                ct);
            return;
        }

        // A scheduled message type is matched under BINARY collation while ContainerId equality folds
        // case, so every type and identity-bearing payload field is built from the FOUND container.
        var found = container.Id;
        if (await SkipReasonAsync(row, container, ct) is { } reason)
        {
            var skipped = await AppendSkippedAsync(found, reason, source, ct);

            await schedules.RecordOutcomeAsync(row.Id, firedAt: null, next, "skipped", skipped.Seq, row.MissedCount, ct);
            await tenant.WriteAsAsync(
                source,
                actorEmail: null,
                TenantActions.ScheduleSkipped,
                row.Id,
                row.Name,
                new
                {
                    team = found.Team,
                    container = found.Name,
                    reason,
                    dueAt = due.ToString("O", CultureInfo.InvariantCulture),
                    nextDueAt = next?.ToString("O", CultureInfo.InvariantCulture),
                },
                ct);
            return;
        }

        // THE DAILY CAP, asked last: a skip for a paused team or a busy member says why better,
        // and neither costs anything. Skipped, re-armed, and fired again on its next due time -
        // which, once the day has turned in the trigger's timezone, counts from zero.
        if (cost is not null && await cost.SkipIfCappedAsync(row, found, now, next, cause: null, ct))
        {
            return;
        }

        var instruction = await AppendInstructionAsync(row, found, source, ct);

        var late = kind == TriggerKind.Once && due <= now;
        await schedules.RecordOutcomeAsync(row.Id, now, next, "fired", instruction.Seq, row.MissedCount, ct);
        await tenant.WriteAsAsync(
            source,
            actorEmail: null,
            TenantActions.ScheduleFired,
            row.Id,
            row.Name,
            new
            {
                team = found.Team,
                container = found.Name,
                instructionSeq = instruction.Seq,
                late,
                dueAt = due.ToString("O", CultureInfo.InvariantCulture),
                firedAt = now.ToString("O", CultureInfo.InvariantCulture),
                nextDueAt = next?.ToString("O", CultureInfo.InvariantCulture),
            },
            ct);
    }

    /// <summary>
    /// FIRES ONE CLOCK TRIGGER NOW, outside its schedule: the one single-fire entry point, for a
    /// person's Run now and a solution's run-at-install. It is the fire the sweep makes - source
    /// `schedule:&lt;id&gt;`, the trigger's instruction with its wakeManager and the sweep's
    /// `schedule.fired` row, skipped with its `schedule.skipped` row for a paused team, a team
    /// waiting for a missing required input or a busy idle-only member, and skipped as the sweep's capped fire is when the daily cap is reached - but
    /// it is nobody's due time, so nothing is counted as missed.
    ///
    /// THE CLOCK: by default the stored `next_due_at` is left alone (a capped one sleeps, as the cap
    /// always does). With <paramref name="countOnFromNow"/> the next due time counts on from now, as
    /// after any fire - a run-at-install "runs once now, then every …". Either way the schedule's next
    /// run is never skipped: the next occurrence after now is never later than one interval from now.
    ///
    /// WHO ASKED is written: one tenant row, <paramref name="action"/> by
    /// <paramref name="actorId"/>, whatever the outcome, beside the rows the fire itself writes.
    /// Null for an unknown row, or an event or folder trigger, which has no instruction of its own
    /// to fire.
    /// </summary>
    public async Task<TriggerRunNow?> RunNowAsync(
        string id,
        DateTimeOffset now,
        string? actorId,
        string? actorEmail,
        string action,
        bool countOnFromNow = false,
        CancellationToken ct = default)
    {
        if (await schedules.FindAsync(id, ct) is not { } row
            || !TryKind(row.Kind, out var kind) || kind is not (TriggerKind.Cron or TriggerKind.Every or TriggerKind.Once))
        {
            return null;
        }

        var next = countOnFromNow ? Next(row, kind, now) : row.NextDueAt;
        var (outcome, seq, reason) = await RunNowOutcomeAsync(row, now, next, ct);

        await tenant.WriteAsAsync(
            actorId,
            actorEmail,
            action,
            row.Id,
            row.Name,
            new
            {
                team = row.Team,
                container = row.Container,
                outcome,
                reason,
                seq,
                at = now.ToString("O", CultureInfo.InvariantCulture),
            },
            ct);

        return new TriggerRunNow(outcome, seq, reason, await schedules.FindAsync(row.Id, ct) ?? row);
    }

    private async Task<(string Outcome, long? Seq, string? Reason)> RunNowOutcomeAsync(
        TriggerRow row, DateTimeOffset now, DateTimeOffset? next, CancellationToken ct)
    {
        if (host.Find(new ContainerId(row.Team, row.Container)) is not { } container)
        {
            return ("member-missing", null, null);
        }

        var source = SourceOf(row.Id);
        var found = container.Id;
        var moves = next != row.NextDueAt;

        if (await SkipReasonAsync(row, container, ct) is { } reason)
        {
            var skipped = await AppendSkippedAsync(found, reason, source, ct);
            if (moves) await schedules.RecordOutcomeAsync(row.Id, firedAt: null, next, "skipped", skipped.Seq, row.MissedCount, ct);
            else await schedules.RecordSkipAsync(row.Id, "skipped", skipped.Seq, ct);
            await WriteFireRowAsync(TenantActions.ScheduleSkipped, row, found, now, next, instructionSeq: null, reason, ct);
            return ("skipped", skipped.Seq, reason);
        }

        if (cost is not null && await cost.SkipIfCappedAsync(row, found, now, next, cause: null, ct))
        {
            // Its `schedule.skipped` row is the cap's, written once a day; a later one is counted.
            return ("capped", null, MessageTypes.ScheduleSkippedCapReason);
        }

        var instruction = await AppendInstructionAsync(row, found, source, ct);
        if (moves) await schedules.RecordOutcomeAsync(row.Id, now, next, "fired", instruction.Seq, row.MissedCount, ct);
        else await schedules.RecordFireAsync(row.Id, now, "fired", instruction.Seq, ct);
        await WriteFireRowAsync(TenantActions.ScheduleFired, row, found, now, next, instruction.Seq, reason: null, ct);
        return ("fired", instruction.Seq, null);
    }

    /// <summary>The `schedule.fired` or `schedule.skipped` tenant row of a Run now, written by the
    /// schedule as the sweep's are, marked <c>runNow</c> since it had no due time.</summary>
    private Task WriteFireRowAsync(
        string action, TriggerRow row, ContainerId found, DateTimeOffset now, DateTimeOffset? next,
        long? instructionSeq, string? reason, CancellationToken ct) =>
        tenant.WriteAsAsync(
            SourceOf(row.Id),
            actorEmail: null,
            action,
            row.Id,
            row.Name,
            new
            {
                team = found.Team,
                container = found.Name,
                runNow = true,
                instructionSeq,
                reason,
                firedAt = now.ToString("O", CultureInfo.InvariantCulture),
                nextDueAt = next?.ToString("O", CultureInfo.InvariantCulture),
            },
            ct);

    /// <summary>Why a fire of <paramref name="row"/> is skipped before its cap is asked: the team
    /// is paused, the team waits for a missing required input ("waiting for …", see
    /// <see cref="SolutionWait"/>), or an idle-only trigger's member is busy. Null when none.</summary>
    private async Task<string?> SkipReasonAsync(TriggerRow row, MemberRuntime container, CancellationToken ct)
    {
        if (host.IsPaused(container.Id.Team)) return MessageTypes.ScheduleSkippedPausedReason;
        if (waiting is not null && await waiting.WaitingForAsync(container.Id.Team, ct) is { } input) return input;
        if (row.IdleOnly && await IsBusyAsync(container, ct)) return MessageTypes.ScheduleSkippedBusyReason;
        return null;
    }

    // `member`, not `container`: this row's own Source is `schedule:<id>` (see SourceOf below),
    // never the member's identity, so this field is the ONLY carrier of it - not a duplicate of the
    // Source the way the other publishers' copies are. See EventCatalog's ScheduleSkipped entry.
    private Task<Message> AppendSkippedAsync(ContainerId found, string reason, string source, CancellationToken ct) =>
        log.AppendAsync(
            new NewMessage(
                MessageTypes.ScheduleSkipped,
                JsonSerializer.Serialize(new { member = found.ToString(), reason }),
                source),
            ct);

    private Task<Message> AppendInstructionAsync(TriggerRow row, ContainerId found, string source, CancellationToken ct) =>
        log.AppendAsync(
            new NewMessage(
                MessageTypes.InstructionFor(found),
                WakeManagerPolicy.InstructionPayload(row.Instruction, row.WakeManager),
                source),
            ct);

    private async Task<bool> IsBusyAsync(MemberRuntime container, CancellationToken ct)
    {
        var accepted = await pending.ForAsync(container.Id, ct);
        return ContainerBusy.IsBusy(container, accepted);
    }

    private static string SourceOf(string id) => $"schedule:{id}";

    private static bool TryKind(string kind, out TriggerKind parsed) =>
        Enum.TryParse(kind, ignoreCase: true, out parsed);

    private static DateTimeOffset? Next(TriggerRow row, TriggerKind kind, DateTimeOffset after) =>
        Triggers.NextOccurrence(
            kind,
            row.Expression,
            row.Timezone,
            row.IntervalSeconds,
            row.FireAt,
            after);

    private static int CountMissedOccurrences(
        TriggerRow row,
        TriggerKind kind,
        DateTimeOffset now,
        DateTimeOffset due,
        DateTimeOffset? next)
    {
        if (kind == TriggerKind.Once || next is null || next > now) return 0;

        var count = 0;
        var cursor = due;
        var candidate = next;

        while (candidate is not null && candidate <= now)
        {
            count++;
            cursor = candidate.Value;
            candidate = Next(row, kind, cursor);
        }

        return count;
    }

    private static DateTimeOffset? AdvanceToFuture(
        TriggerRow row,
        TriggerKind kind,
        DateTimeOffset now,
        DateTimeOffset due,
        DateTimeOffset? next)
    {
        var cursor = due;
        var candidate = next ?? Next(row, kind, cursor);

        while (candidate is not null && candidate <= now)
        {
            cursor = candidate.Value;
            candidate = Next(row, kind, cursor);
        }

        return candidate;
    }
}

/// <summary>What one <see cref="TriggerSweep.RunNowAsync"/> did: `fired`, `skipped` (paused team, a
/// team waiting for a missing input or a busy idle-only member, <c>Reason</c> says which), `capped` or `member-missing`, and the seq of the
/// instruction or `schedule.skipped` row it appended (null when capped: that row is the cap's), and
/// the trigger as the fire left it.</summary>
public sealed record TriggerRunNow(string Outcome, long? Seq, string? Reason, TriggerRow Trigger);
