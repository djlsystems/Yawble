using Harness.Contracts;

namespace Harness.Host;

/// <summary>A trigger to create: <c>POST /api/teams/{team}/triggers</c>'s body, and what an installer
/// hands in. See that route's description for each field.</summary>
public sealed record NewTrigger(
    string? Name,
    string? Container,
    string? Instruction,
    string? Kind,
    string? Expression = null,
    string? Timezone = null,
    int? IntervalSeconds = null,
    DateTimeOffset? FireAt = null,
    bool? IdleOnly = null,
    bool? Enabled = null,
    DateTimeOffset? NextDueAt = null,
    string? EventType = null,
    string? Filter = null,
    string? WatchRoot = null,
    string? WatchPath = null,
    string? WatchGlob = null,
    int? PollSeconds = null,
    int? QuietSeconds = null,
    int? MinIntervalSeconds = null,
    string? WakeManager = null,
    long? DailyTokenCap = null);

/// <summary>The trigger made, or the sentence that refused it (and nothing was written).</summary>
public sealed record TriggerCreated(TriggerRow? Row, string? Refusal);

/// <summary>
/// CREATES ONE TRIGGER, with every check the Triggers dialog's route makes: the one path for that
/// route and for a solution install, so an installed trigger cannot skip a check a person's would
/// meet. The row lands with the tenant row <c>audit</c> builds, in one transaction; then the member's
/// effective subscriptions are recomputed and the runner is woken.
/// </summary>
public sealed class TriggerCreation(
    TeamRegistry teams,
    ITriggerStore schedules,
    FolderWatch folders,
    AgentCatalog catalog,
    EffectiveSubscriptions effective,
    TriggerWakeSignal wake)
{
    public async Task<TriggerCreated> CreateAsync(
        string stored, NewTrigger request, string createdBy, Func<TriggerRow, TriggerAudit> audit, CancellationToken ct = default)
    {
        var name = (request.Name ?? "").Trim();
        var instruction = (request.Instruction ?? "").Trim();
        var kind = (request.Kind ?? "").Trim();
        var container = string.IsNullOrWhiteSpace(request.Container)
            ? TeamRegistry.DefaultManagerName
            : request.Container.Trim();

        if (name.Length == 0) return Refused("A schedule needs a name.");
        if (instruction.Length == 0) return Refused("A schedule needs an instruction.");
        if (container.Length == 0) return Refused("A schedule needs a member name.");
        if (kind.Length == 0) return Refused("A schedule needs a kind.");

        // A NEW TRIGGER WAKES THE MANAGER ONLY WHEN ITS RUN HANDS BACK OR FAILS, unless a person chose
        // otherwise. A trigger from before the choice existed keeps `always` (the column's default).
        var wakeManager = request.WakeManager is null
            ? WakeManagerPolicy.OnHandbackOrFailure
            : WakeManagerPolicy.Parse(request.WakeManager);
        if (wakeManager is null) return Refused(TriggerCost.WakeManagerRefusal);
        if (request.DailyTokenCap is < 1) return Refused(TriggerCost.DailyTokenCapRefusal);

        if (!teams.ContainerIdsOf(stored).Any(id => string.Equals(id.Name, container, StringComparison.OrdinalIgnoreCase)))
        {
            return Refused($"No member '{container}' on team '{stored}'.");
        }

        if (Triggers.Validate(
            kind, request.Expression, request.Timezone, request.IntervalSeconds, request.FireAt,
            onceMustFollow: DateTimeOffset.UtcNow)
            is { } invalid)
        {
            return Refused(invalid);
        }

        // A FOLDER TRIGGER IS AN EVENT TRIGGER ON `file.changed` THAT ALSO POLLS, so its event
        // type is the server's to set, never the caller's. Its folder is checked here, where a person
        // reads the refusal, rather than discovered unreachable on the first poll.
        var folderKind = FolderWatch.IsFolderKind(kind);
        var eventType = folderKind
            ? FolderWatch.FileChangedEventType
            : string.IsNullOrWhiteSpace(request.EventType) ? null : request.EventType.Trim();
        WatchTarget? watchTarget = null;

        if (folderKind)
        {
            request = request with
            {
                WatchGlob = FolderWatch.Glob(request.WatchGlob),
                PollSeconds = request.PollSeconds ?? FolderWatch.DefaultPollSeconds,
                QuietSeconds = request.QuietSeconds ?? FolderWatch.DefaultQuietSeconds,
                MinIntervalSeconds = request.MinIntervalSeconds ?? FolderWatch.DefaultMinIntervalSeconds,
            };

            if (await folders.RefusalForAsync(
                    stored, request.WatchRoot, request.WatchPath, request.WatchGlob,
                    request.PollSeconds, request.QuietSeconds, request.MinIntervalSeconds,
                    counting: true, ct) is { } folderRefusal)
            {
                return Refused(folderRefusal);
            }

            // Stored as RESOLVED - `Inbox/` and `./Inbox` are one folder, and the delivery pump
            // compares the stored spelling against every `file.changed` path.
            watchTarget = folders.Resolve(stored, request.WatchRoot, request.WatchPath, out _);
        }

        // AN EVENT TRIGGER NAMES A TYPE THE CATALOG DECLARES, or it can never fire. Refused rather than
        // stored: a trigger that silently never matches is indistinguishable from one whose event has
        // not happened yet, and a person would wait on it indefinitely.
        if (string.Equals(kind, "event", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.EventType))
            {
                return Refused("An event trigger must name an event type.");
            }

            if (EventCatalog.For(request.EventType) is null)
            {
                return Refused(
                    $"\"{request.EventType}\" is not an event this platform publishes. "
                    + "GET /api/events lists every type a trigger can name.");
            }

            // THE SAME RULE, REACHED BY A DIFFERENT DOOR. Without this a trigger is a way around the
            // refusal AddContainerAsync already makes - the same illegal pair (a language model
            // subscribed to a high-volume type) made through the trigger screen instead of the member
            // one. The member check above already confirmed `container` exists on this team.
            var subscriber = await teams.MemberAsync(stored, container, ct);

            if (catalog.For(subscriber.Agent) is { LanguageModel: true }
                && EventCatalog.IsHighVolume(request.EventType))
            {
                return Refused(TeamRegistry.FirehoseRefusal(request.EventType, subscriber.Agent));
            }
        }

        if (TriggerFilter.RefusalFor(request.Filter, eventType ?? "") is { } filterRefusal)
        {
            return Refused(filterRefusal);
        }

        var expression = string.IsNullOrWhiteSpace(request.Expression) ? null : request.Expression.Trim();
        var timezone = string.IsNullOrWhiteSpace(request.Timezone) ? null : request.Timezone.Trim();

        var row = new TriggerRow(
            Guid.NewGuid().ToString("N"),
            stored,
            container,
            name,
            instruction,
            kind,
            expression,
            timezone,
            request.IntervalSeconds,
            request.FireAt,
            request.IdleOnly ?? true,
            request.Enabled ?? true,

            // ARMED HERE, and nothing else arms it. A schedule fires only when `next_due_at` is set
            // and past, and the ONLY other call to NextOccurrence is in the sweep, AFTER a fire. So a
            // row created without one could never fire and could never be given one.
            //
            // A SUPPLIED VALUE STILL WINS: it means "fire first at this time, then follow the shape".
            // Absent means "start from now", which is what a person ticking Enabled means.
            request.NextDueAt ?? FirstOccurrence(kind, expression, timezone, request.IntervalSeconds, request.FireAt, DateTimeOffset.UtcNow),
            null,
            null,
            null,
            0,
            DateTimeOffset.UtcNow,
            createdBy,
            EventType: eventType,
            Filter: request.Filter)
        {
            WatchRoot = watchTarget?.Root,
            WatchPath = watchTarget?.Folder,
            WatchGlob = folderKind ? request.WatchGlob : null,
            PollSeconds = folderKind ? request.PollSeconds : null,
            QuietSeconds = folderKind ? request.QuietSeconds : null,
            MinIntervalSeconds = folderKind ? request.MinIntervalSeconds : null,
            WakeManager = wakeManager,
            DailyTokenCap = request.DailyTokenCap,
        };

        // The row and its tenant_events row are one transaction: a trigger with no record of who made
        // it does not land.
        await schedules.SaveAsync(row, audit(row), ct);

        // A NEW event trigger changes what wakes its container, so the effective set is recomputed
        // right after the row lands - a no-op for a clock-driven trigger, which contributes nothing.
        await effective.RecomputeAsync(new ContainerId(stored, container), ct);

        // The runner is asleep until whatever WAS due next. Signalled AFTER the write, so waking
        // early cannot read a row that is not there.
        wake.Signal();

        return new TriggerCreated(row, null);
    }

    /// <summary>
    /// Deletes a trigger with its tenant row, and recomputes what wakes its member - what undoing an
    /// install's trigger, or an update removing one, needs.
    /// </summary>
    public async Task<bool> DeleteAsync(string id, TriggerAudit audit, CancellationToken ct = default)
    {
        if (await schedules.FindAsync(id, ct) is not { } row) return false;

        await schedules.DeleteAsync(id, audit, ct);
        await effective.RecomputeAsync(new ContainerId(row.Team, row.Container), ct);
        wake.Signal();
        return true;
    }

    private static TriggerCreated Refused(string sentence) => new(null, sentence);

    private static DateTimeOffset? FirstOccurrence(
        string kind, string? expression, string? timezone, int? intervalSeconds, DateTimeOffset? fireAt, DateTimeOffset after) =>
        Enum.TryParse<TriggerKind>(kind, ignoreCase: true, out var parsed)
            ? Triggers.NextOccurrence(parsed, expression, timezone, intervalSeconds, fireAt, after)
            : null;
}
