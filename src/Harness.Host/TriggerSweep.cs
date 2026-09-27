using System.Globalization;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

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
    ILogger<TriggerSweep> logger)
{
    public async Task FireDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
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
        if (host.IsPaused(found.Team))
        {
            var skipped = await log.AppendAsync(
                new NewMessage(
                    MessageTypes.ScheduleSkipped,
                    JsonSerializer.Serialize(new
                    {
                        member = found.ToString(),
                        reason = MessageTypes.ScheduleSkippedPausedReason,
                    }),
                    source),
                ct);

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
                    dueAt = due.ToString("O", CultureInfo.InvariantCulture),
                    nextDueAt = next?.ToString("O", CultureInfo.InvariantCulture),
                },
                ct);
            return;
        }

        var busy = row.IdleOnly && await IsBusyAsync(container, ct);
        if (busy)
        {
            var skipped = await log.AppendAsync(
                new NewMessage(
                    MessageTypes.ScheduleSkipped,
                    // `member`, not `container`: this row's own Source is `schedule:<id>` (see
                    // SourceOf below), never the member's identity, so this field is the ONLY
                    // carrier of it - not a duplicate of the Source the way the other publishers'
                    // copies are. See EventCatalog's ScheduleSkipped entry for the full reasoning.
                    JsonSerializer.Serialize(new
                    {
                        member = found.ToString(),
                        reason = MessageTypes.ScheduleSkippedBusyReason,
                    }),
                    source),
                ct);

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
                    dueAt = due.ToString("O", CultureInfo.InvariantCulture),
                    nextDueAt = next?.ToString("O", CultureInfo.InvariantCulture),
                },
                ct);
            return;
        }

        var instruction = await log.AppendAsync(
            new NewMessage(
                MessageTypes.InstructionFor(found),
                JsonSerializer.Serialize(new { instruction = row.Instruction }),
                source),
            ct);

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
