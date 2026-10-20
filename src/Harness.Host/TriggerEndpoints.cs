using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

internal static class TriggerEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/teams/{team}/triggers", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams,
            ITriggerStore schedules,
            TriggerCost cost,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            return Results.Ok(await cost.ViewsAsync(await schedules.ListForTeamAsync(stored, ct), DateTimeOffset.UtcNow, ct));
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("List this team's triggers")
            .WithDescription(
                "Every trigger row currently stored for this team, oldest first, each with its read-only "
                + TriggerCost.SpendDescription + "\n\n"
                + "404 for an unknown team.");

        // THE PER-CONTAINER ROUTE IS WHAT THE CARD DIALOG READS. It declares {team}, so TeamGate covers
        // it structurally - the same declaration that lets its handler read a team at all. Shares
        // RequirePermit(Permits.Read) with the sibling per-container routes under
        // /api/teams/{team}/containers/{name}/..., since this is one more thing the `status` tool for a
        // member could read, not a human-only surface.
        app.MapGet("/api/teams/{team}/containers/{name}/triggers", async (
            [Description(Describe.Team)] string team,
            [Description("The member's current identifier, as addressed in its route.")] string name,
            TeamRegistry teams,
            ITriggerStore schedules,
            TriggerCost cost,
            CancellationToken ct) =>
        {
            try
            {
                var member = await teams.MemberAsync(team, name, ct);

                return Results.Ok(await cost.ViewsAsync(
                    await schedules.ListForContainerAsync(member.Team, member.Name, ct), DateTimeOffset.UtcNow, ct));
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        })
            .WithTags("Teams")
            .RequirePermit(Permits.Read)
            .WithSummary("List one member's triggers")
            .WithDescription(
                "Every trigger row currently stored for this one member, oldest first - what the member "
                + "card's own dialog reads, separately from its teammates', each with its read-only "
                + TriggerCost.SpendDescription + "\n\n"
                + "404 for an unknown team or member.");

        app.MapPost("/api/teams/{team}/triggers", async (
            [Description(Describe.Team)] string team,
            CreateSchedule request,
            TeamRegistry teams,
            TriggerCreation triggers,
            TriggerCost cost,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            // THE ONE PATH a trigger is made by, a person's here and a solution install's alike: every check,
            // the row with its tenant row in one transaction, the subscriptions recomputed, the runner woken.
            var created = await triggers.CreateAsync(
                stored,
                new NewTrigger(
                    request.Name, request.Container, request.Instruction, request.Kind, request.Expression,
                    request.Timezone, request.IntervalSeconds, request.FireAt, request.IdleOnly, request.Enabled,
                    request.NextDueAt, request.EventType, request.Filter, request.WatchRoot, request.WatchPath,
                    request.WatchGlob, request.PollSeconds, request.QuietSeconds, request.MinIntervalSeconds,
                    request.WakeManager, request.DailyTokenCap, request.OutcomeId),
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
                context.User.FindFirstValue(ClaimTypes.Email),
                row => TenantLogging.Row(
                    context, TenantActions.ScheduleCreated, row.Id, row.Name, new { team = row.Team, member = row.Container }),
                ct);

            if (created.Row is not { } row) return Results.BadRequest(new { error = created.Refusal });

            return Results.Created($"/api/teams/{row.Team}/triggers/{row.Id}", await cost.ViewAsync(row, DateTimeOffset.UtcNow, ct));
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Create a trigger on this team")
            .WithDescription(
                "Creates one durable trigger row. `container` defaults to the team's Manager when omitted. "
                + "Validation refuses a shape that is not valid for its kind, a member name this team does "
                + "not hold, for `kind: event`, an `eventType` the catalog does not declare, and a `filter` "
                + "naming a field that event type does not carry.\n\n"
                + "`kind: folderChange` watches `watchPath` under `watchRoot` (`documents` or "
                + "`root:<name>` for a file-browser root with allowWatch) and publishes `file.changed`; its "
                + "`eventType` is always `file.changed`. A folder outside those roots, a symbolic link, one "
                + "holding more than 10,000 entries, or one past the instance's watch cap is refused with "
                + "a sentence.\n\n"
                + "`wakeManager` defaults to `onHandbackOrFailure`; `dailyTokenCap` defaults to no cap. The "
                + "row returned carries the read-only " + TriggerCost.SpendDescription + "\n\n"
                + "400 for invalid input; 404 for an unknown team.");

        app.MapPatch("/api/teams/{team}/triggers/{id}", async (
            [Description(Describe.Team)] string team,
            [Description("The schedule row's identifier.")] string id,
            JsonElement body,
            TeamRegistry teams,
            ITriggerStore schedules,
            TriggerCost cost,
            FolderWatch folders,
            AgentCatalog catalog,
            EffectiveSubscriptions effective,
            TriggerWakeSignal wake,
            IOutcomeStore outcomes,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            if (await schedules.FindAsync(id, ct) is not { } existing
                || !string.Equals(existing.Team, stored, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound(new { error = $"No schedule '{id}' on team '{stored}'." });
            }

            if (!TryReadSchedulePatch(body, out var patch, out var patchError))
            {
                return Results.BadRequest(new { error = patchError });
            }

            if (ApplySchedulePatch(existing, patch, out var candidate, out var applyError) is false)
            {
                return Results.BadRequest(new { error = applyError });
            }

            if (!TeamHasMember(teams, stored, candidate.Container))
            {
                return Results.BadRequest(new { error = $"No member '{candidate.Container}' on team '{stored}'." });
            }

            if (Triggers.Validate(
                candidate.Kind,
                candidate.Expression,
                candidate.Timezone,
                candidate.IntervalSeconds,
                candidate.FireAt,
                onceMustFollow: null) is { } invalid)
            {
                return Results.BadRequest(new { error = invalid });
            }

            // See the POST route. The event type is the server's; the folder is checked here; the
            // instance cap counts only a row BECOMING a folder trigger, or editing one at the cap would be
            // refused for being one of the watches it is counted among.
            if (FolderWatch.IsFolderKind(candidate.Kind))
            {
                candidate = candidate with
                {
                    EventType = FolderWatch.FileChangedEventType,
                    WatchGlob = FolderWatch.Glob(candidate.WatchGlob),
                    PollSeconds = candidate.PollSeconds ?? FolderWatch.DefaultPollSeconds,
                    QuietSeconds = candidate.QuietSeconds ?? FolderWatch.DefaultQuietSeconds,
                    MinIntervalSeconds = candidate.MinIntervalSeconds ?? FolderWatch.DefaultMinIntervalSeconds,
                };

                if (await folders.RefusalForAsync(
                        stored, candidate.WatchRoot, candidate.WatchPath, candidate.WatchGlob,
                        candidate.PollSeconds, candidate.QuietSeconds, candidate.MinIntervalSeconds,
                        counting: !FolderWatch.IsFolderKind(existing.Kind), ct) is { } folderRefusal)
                {
                    return Results.BadRequest(new { error = folderRefusal });
                }

                var target = folders.Resolve(stored, candidate.WatchRoot, candidate.WatchPath, out _)!;
                candidate = candidate with { WatchRoot = target.Root, WatchPath = target.Folder };

                // A MOVED WATCH POLLS AT ONCE, to take its new baseline (the store forgets the old one),
                // and so does one switched back on.
                var moved = candidate.WatchRoot != existing.WatchRoot
                    || candidate.WatchPath != existing.WatchPath
                    || candidate.WatchGlob != existing.WatchGlob;

                if (moved || (candidate.Enabled && !existing.Enabled) || candidate.NextDueAt is null)
                {
                    candidate = candidate with { NextDueAt = DateTimeOffset.UtcNow };
                }
            }
            else if (FolderWatch.IsFolderKind(existing.Kind))
            {
                candidate = candidate with
                {
                    EventType = string.Equals(candidate.EventType, FolderWatch.FileChangedEventType, StringComparison.Ordinal)
                        && !string.Equals(candidate.Kind, "event", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : candidate.EventType,
                    WatchRoot = null,
                    WatchPath = null,
                    WatchGlob = null,
                    PollSeconds = null,
                    QuietSeconds = null,
                    MinIntervalSeconds = null,
                };
            }

            // AN EVENT TRIGGER NAMES A TYPE THE CATALOG DECLARES, or it can never fire. Refused rather than
            // stored: a trigger that silently never matches is indistinguishable from one whose event has
            // not happened yet, and a person would wait on it indefinitely.
            if (string.Equals(candidate.Kind, "event", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(candidate.EventType))
                {
                    return Results.BadRequest(new { error = "An event trigger must name an event type." });
                }

                if (EventCatalog.For(candidate.EventType) is null)
                {
                    return Results.BadRequest(new
                    {
                        error = $"\"{candidate.EventType}\" is not an event this platform publishes. "
                            + "GET /api/events lists every type a trigger can name.",
                    });
                }

                // THE SAME RULE, REACHED BY A DIFFERENT DOOR - see the POST route's identical check.
                // `TeamHasMember` above already confirmed `candidate.Container` exists, so this cannot 404.
                var subscriber = await teams.MemberAsync(stored, candidate.Container, ct);

                if (catalog.For(subscriber.Agent) is { LanguageModel: true }
                    && EventCatalog.IsHighVolume(candidate.EventType))
                {
                    return Results.BadRequest(
                        new { error = TeamRegistry.FirehoseRefusal(candidate.EventType, subscriber.Agent) });
                }
            }

            if (TriggerFilter.RefusalFor(candidate.Filter, candidate.EventType ?? "") is { } filterRefusal)
            {
                return Results.BadRequest(new { error = filterRefusal });
            }

            // THE OUTCOME ITS FIRES SERVE: a live one, named by id or exact name, stored by id.
            if (patch.HasOutcomeId && candidate.OutcomeId is { } outcomeAsked)
            {
                if (await outcomes.ResolveLiveAsync(outcomeAsked, ct) is not { } outcome)
                {
                    return Results.BadRequest(new { error = TriggerCreation.OutcomeRefusal(outcomeAsked) });
                }

                candidate = candidate with { OutcomeId = outcome.Id };
            }

            // A PERSON'S CHANGE WAKES A SCHEDULE ASLEEP ON ITS CAP. Raised above today's spend, or cleared,
            // the cap no longer stops it, so it fires at its next occurrence from now rather than tomorrow.
            // Lowering it wakes nothing. An edit to the schedule itself has already re-armed it from now
            // (see ApplySchedulePatch), and an explicit `nextDueAt` still wins.
            var now = DateTimeOffset.UtcNow;
            if (patch.HasDailyTokenCap
                && !patch.HasNextDueAt
                && existing.LastOutcome == "capped"
                && Enum.TryParse<TriggerKind>(candidate.Kind, ignoreCase: true, out var clockKind)
                && clockKind is TriggerKind.Cron or TriggerKind.Every or TriggerKind.Once)
            {
                var spent = await cost.SpentTodayAsync(existing, now, ct);
                if (TriggerCost.CapReached(existing, spent) && !TriggerCost.CapReached(candidate, spent))
                {
                    candidate = candidate with
                    {
                        NextDueAt = FirstOccurrence(
                            candidate.Kind, candidate.Expression, candidate.Timezone, candidate.IntervalSeconds, candidate.FireAt, now),
                    };
                }
            }

            // THE PERSON WHO CHANGED IT IS NOW ITS CONFIGURER: their email is what its fires' outcome links
            // name. A caller with no email keeps the one stored.
            candidate = candidate with { ConfiguredByEmail = context.User.FindFirstValue(ClaimTypes.Email) ?? existing.ConfiguredByEmail };

            // The row and its tenant_events row are one transaction: a change with no record of who made
            // it does not land.
            await schedules.SaveAsync(
                candidate,
                new TriggerAudit(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                    context.User.FindFirstValue(ClaimTypes.Email),
                    TenantActions.ScheduleChanged,
                    candidate.Id,
                    candidate.Name,
                    JsonSerializer.Serialize(new { team = candidate.Team, member = candidate.Container, changed = TriggerChanges.Of(existing, candidate) })),
                ct);

            // The edit can change EITHER the trigger's event type/enabled state OR which container holds
            // it. Both sides are recomputed: the target container (which may have gained or lost this
            // trigger's contribution) and, when a PATCH moved the trigger to a different container, the
            // ORIGINAL one too - it just lost a subscription this same write moved somewhere else, and
            // nothing else would ever recompute it.
            await effective.RecomputeAsync(new ContainerId(stored, candidate.Container), ct);

            if (!string.Equals(existing.Container, candidate.Container, StringComparison.OrdinalIgnoreCase))
            {
                await effective.RecomputeAsync(new ContainerId(stored, existing.Container), ct);
            }

            // The runner is asleep until whatever WAS due next. Without this it would not learn
            // about this write until its heartbeat expires, so a schedule due in ten seconds could
            // sit unnoticed for a minute - which reads as the feature being broken rather than
            // slow. Signalled AFTER the write, so waking early cannot read a row that is not there.
            wake.Signal();

            return Results.Ok(await cost.ViewAsync(candidate, DateTimeOffset.UtcNow, ct));
        })
            .Accepts<UpdateSchedule>("application/json")
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Change part of one trigger")
            .WithDescription(
                "PATCH semantics: absent means leave alone; present with a value stores that value; present "
                + "as null or blank clears nullable fields (`expression`, `timezone`, `intervalSeconds`, "
                + "`fireAt`, `nextDueAt`, `eventType`, `filter`, `dailyTokenCap`). `wakeManager` may not be "
                + "null. The row returned carries the read-only " + TriggerCost.SpendDescription + "\n\n"
                + "400 for invalid input, a member this team does not hold, (for `kind: event`) an "
                + "`eventType` the catalog does not declare, or a `filter` naming a field that event type "
                + "does not carry; 404 for an unknown team or trigger.");

        app.MapDelete("/api/teams/{team}/triggers/{id}", async (
            [Description(Describe.Team)] string team,
            [Description("The schedule row's identifier.")] string id,
            TeamRegistry teams,
            ITriggerStore schedules,
            EffectiveSubscriptions effective,
            TriggerWakeSignal wake,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            if (await schedules.FindAsync(id, ct) is not { } row
                || !string.Equals(row.Team, stored, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound(new { error = $"No schedule '{id}' on team '{stored}'." });
            }

            // One transaction with its tenant_events row, as the create and the change are.
            await schedules.DeleteAsync(
                row.Id,
                TenantLogging.Row(
                    context, TenantActions.ScheduleDeleted, row.Id, row.Name, new { team = row.Team, member = row.Container }),
                ct);

            // A DELETED event trigger can only ever remove a subscription - the base set is untouched, so
            // this can never eat a Manager's agentContainer.completed - but "can only add or remove nothing" is
            // exactly the reasoning that turns into drift the moment someone reaches for it as a shortcut.
            // Recomputed whole, same as every other write.
            await effective.RecomputeAsync(new ContainerId(stored, row.Container), ct);

            // The runner is asleep until whatever WAS due next. Without this it would not learn
            // about this write until its heartbeat expires, so a schedule due in ten seconds could
            // sit unnoticed for a minute - which reads as the feature being broken rather than
            // slow. Signalled AFTER the write, so waking early cannot read a row that is not there.
            wake.Signal();

            return Results.NoContent();
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Delete one trigger")
            .WithDescription(
                "Deletes one trigger row.\n\n"
                + "404 for an unknown team or trigger.");

        // RUN NOW: the fire the schedule makes, at a person's hand. TriggerSweep.RunNowAsync is the one
        // single-fire entry point (a solution's run-at-install calls it too); this route only resolves the
        // row and says who asked. Always 200 for a clock trigger: a skip is the answer, not a failure.
        app.MapPost("/api/teams/{team}/triggers/{id}/run", async (
            [Description(Describe.Team)] string team,
            [Description("The schedule row's identifier.")] string id,
            TeamRegistry teams,
            ITriggerStore schedules,
            TriggerSweep sweep,
            TriggerCost cost,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Results.NotFound(new { error = $"No team '{team}'." });
            }

            if (await schedules.FindAsync(id, ct) is not { } row
                || !string.Equals(row.Team, stored, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound(new { error = $"No schedule '{id}' on team '{stored}'." });
            }

            var now = DateTimeOffset.UtcNow;
            var run = await sweep.RunNowAsync(
                row.Id,
                now,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                context.User.FindFirstValue(ClaimTypes.Email),
                TenantActions.ScheduleRunNow,
                ct: ct);

            if (run is null)
            {
                return Results.BadRequest(new { error = "Run now fires a schedule (cron, every or once); this trigger fires on an event." });
            }

            return Results.Ok(new { outcome = run.Outcome, reason = run.Reason, seq = run.Seq, trigger = await cost.ViewAsync(run.Trigger, now, ct) });
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Run one schedule now")
            .WithDescription(
                "Fires a `cron`, `every` or `once` trigger now, the same fire its schedule makes: source "
                + "`schedule:<id>`, its instruction with its `wakeManager`, skipped for a paused team or a busy "
                + "idle-only member, and skipped with its `schedule.skipped` row when its daily token cap is "
                + "reached. Its stored next due time is not moved (a capped one sleeps, as the cap does). "
                + "Answers `outcome` (`fired`, `skipped`, `capped`, `member-missing`), `reason`, `seq` and "
                + "`trigger`, the row as the list routes return it. Audited as `schedule.run-now`.\n\n"
                + "400 for an event or folder trigger; 404 for an unknown team or trigger.");

        // "TEST THIS FOLDER". What a folder trigger there would see and how long the listing took,
        // so a person can tell a slow share from a wrong path before saving anything. Always 200 for a team
        // that exists: a refusal is the answer to the question asked, not a failed request.
        app.MapPost("/api/teams/{team}/triggers/test-folder", (
            [Description(Describe.Team)] string team,
            TestFolder request,
            TeamRegistry teams,
            FolderWatch folders) =>
            teams.ExistingName(team) is not { } stored
                ? Results.NotFound(new { error = $"No team '{team}'." })
                : Results.Ok(folders.Test(stored, request.WatchRoot, request.WatchPath, request.WatchGlob)))
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Test a folder for a folder trigger")
            .WithDescription(
                "Lists what a `folderChange` trigger on this folder would see: `ok`, `refusal` (a sentence, "
                + "or null), `folder`, `count`, `truncated`, `elapsedMs` and the first 200 `entries` "
                + "(`path`, `size`, `modifiedAt`). Nothing is stored.\n\n"
                + "404 for an unknown team.");

        // The cron builder's preview, and the start field's: the next firings of a schedule as the sweep
        // would arm it (Triggers.Preview uses NextOccurrence itself), in the schedule's own timezone.
        // Always 200 for a team that exists: a shape that cannot run answers with its sentence.
        app.MapPost("/api/teams/{team}/triggers/preview", (
            [Description(Describe.Team)] string team,
            PreviewSchedule request,
            TeamRegistry teams) =>
        {
            if (teams.ExistingName(team) is null) return Results.NotFound(new { error = $"No team '{team}'." });

            var kind = Enum.GetNames<TriggerKind>().FirstOrDefault(
                name => string.Equals(name, request.Kind?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (kind is null) return Results.Ok(new { occurrences = Array.Empty<DateTimeOffset>(), error = "kind must be one of: cron, every, once." });

            var preview = Triggers.Preview(
                Enum.Parse<TriggerKind>(kind), request.Expression, request.Timezone, request.IntervalSeconds,
                request.FireAt, DateTimeOffset.UtcNow, request.Count ?? 5);
            return Results.Ok(new { occurrences = preview.Occurrences, error = preview.Error });
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Preview a schedule's next firings")
            .WithDescription(
                "For a `cron`, `every` or `once` shape (`kind`, `expression`, `timezone`, `intervalSeconds`, "
                + "`fireAt` - for cron and every, the start before which it never fires - and `count`, 1 to "
                + "20, default 5), answers `occurrences`, the next firings as the scheduler would arm them, "
                + "and `error`, a sentence when the shape cannot run. Nothing is stored.\n\n"
                + "404 for an unknown team.");

        app.MapGet("/api/teams/{team}/triggers/watch-roots", (
            [Description(Describe.Team)] string team,
            TeamRegistry teams,
            FolderWatch folders) =>
            teams.ExistingName(team) is null
                ? Results.NotFound(new { error = $"No team '{team}'." })
                : Results.Ok(folders.RootOptions()))
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("List the areas a folder trigger may watch")
            .WithDescription(
                "`documents` (the team's documents) first, then every file-browser root configured with "
                + "allowWatch, as `{ value, label }`. The instance's own data folder is never offered.\n\n"
                + "404 for an unknown team.");
    }

    static bool TeamHasMember(TeamRegistry teams, string team, string member) =>
        teams.ContainerIdsOf(team).Any(id => string.Equals(id.Name, member, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// When a schedule with this shape should first fire, or null when the shape cannot say.
    ///
    /// Takes the kind as the STRING the wire carries, because that is what both routes hold - the
    /// validator reads the same string. Parsing here rather than at each call site keeps the
    /// case-insensitivity in one place.
    ///
    /// Null for an unparseable kind is safe: `Triggers.Validate` has already refused anything that is
    /// not a known kind by the time this is reached.
    /// </summary>
    static DateTimeOffset? FirstOccurrence(
        string kind,
        string? expression,
        string? timezone,
        int? intervalSeconds,
        DateTimeOffset? fireAt,
        DateTimeOffset after) =>
        Enum.TryParse<TriggerKind>(kind, ignoreCase: true, out var parsed)
            ? Triggers.NextOccurrence(parsed, expression, timezone, intervalSeconds, fireAt, after)
            : null;

    static bool TryReadSchedulePatch(JsonElement body, out SchedulePatch patch, out string error)
    {
        patch = default;
        error = "";

        if (body.ValueKind is not JsonValueKind.Object)
        {
            error = "PATCH body must be a JSON object.";
            return false;
        }

        UpdateSchedule? typed;

        try
        {
            typed = JsonSerializer.Deserialize<UpdateSchedule>(
                body.GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException exception)
        {
            error = $"Invalid schedule patch payload: {exception.Message}";
            return false;
        }

        typed ??= new UpdateSchedule();

        patch = new SchedulePatch(
            body.TryGetProperty("name", out _), typed.Name,
            body.TryGetProperty("container", out _), typed.Container,
            body.TryGetProperty("instruction", out _), typed.Instruction,
            body.TryGetProperty("kind", out _), typed.Kind,
            body.TryGetProperty("expression", out _), typed.Expression,
            body.TryGetProperty("timezone", out _), typed.Timezone,
            body.TryGetProperty("intervalSeconds", out _), typed.IntervalSeconds,
            body.TryGetProperty("fireAt", out _), typed.FireAt,
            body.TryGetProperty("idleOnly", out _), typed.IdleOnly,
            body.TryGetProperty("enabled", out _), typed.Enabled,
            body.TryGetProperty("nextDueAt", out _), typed.NextDueAt,
            body.TryGetProperty("eventType", out _), typed.EventType,
            body.TryGetProperty("filter", out _), typed.Filter,
            body.TryGetProperty("watchRoot", out _), typed.WatchRoot,
            body.TryGetProperty("watchPath", out _), typed.WatchPath,
            body.TryGetProperty("watchGlob", out _), typed.WatchGlob,
            body.TryGetProperty("pollSeconds", out _), typed.PollSeconds,
            body.TryGetProperty("quietSeconds", out _), typed.QuietSeconds,
            body.TryGetProperty("minIntervalSeconds", out _), typed.MinIntervalSeconds,
            body.TryGetProperty("wakeManager", out _), typed.WakeManager,
            body.TryGetProperty("dailyTokenCap", out _), typed.DailyTokenCap,
            body.TryGetProperty("outcomeId", out _), string.IsNullOrWhiteSpace(typed.OutcomeId) ? null : typed.OutcomeId.Trim());

        return true;
    }

    static bool ApplySchedulePatch(
        TriggerRow existing,
        SchedulePatch patch,
        out TriggerRow updated,
        out string error)
    {
        error = "";

        var name = existing.Name;
        var container = existing.Container;
        var instruction = existing.Instruction;
        var kind = existing.Kind;
        var expression = existing.Expression;
        var timezone = existing.Timezone;
        var intervalSeconds = existing.IntervalSeconds;
        var fireAt = existing.FireAt;
        var idleOnly = existing.IdleOnly;
        var enabled = existing.Enabled;
        var nextDueAt = existing.NextDueAt;
        var eventType = existing.EventType;
        var filter = existing.Filter;

        if (patch.HasName)
        {
            name = (patch.Name ?? "").Trim();
            if (name.Length == 0)
            {
                updated = existing;
                error = "A schedule needs a name.";
                return false;
            }
        }

        if (patch.HasContainer)
        {
            container = (patch.Container ?? "").Trim();
            if (container.Length == 0)
            {
                updated = existing;
                error = "A schedule needs a member name.";
                return false;
            }
        }

        if (patch.HasInstruction)
        {
            instruction = (patch.Instruction ?? "").Trim();
            if (instruction.Length == 0)
            {
                updated = existing;
                error = "A schedule needs an instruction.";
                return false;
            }
        }

        if (patch.HasKind)
        {
            kind = (patch.Kind ?? "").Trim();
            if (kind.Length == 0)
            {
                updated = existing;
                error = "A schedule needs a kind.";
                return false;
            }
        }

        if (patch.HasExpression) expression = string.IsNullOrWhiteSpace(patch.Expression) ? null : patch.Expression.Trim();
        if (patch.HasTimezone) timezone = string.IsNullOrWhiteSpace(patch.Timezone) ? null : patch.Timezone.Trim();
        if (patch.HasIntervalSeconds) intervalSeconds = patch.IntervalSeconds;
        if (patch.HasFireAt) fireAt = patch.FireAt;
        if (patch.HasEventType) eventType = string.IsNullOrWhiteSpace(patch.EventType) ? null : patch.EventType.Trim();
        if (patch.HasFilter) filter = string.IsNullOrWhiteSpace(patch.Filter) ? null : patch.Filter.Trim();

        if (patch.HasIdleOnly)
        {
            if (patch.IdleOnly is null)
            {
                updated = existing;
                error = "idleOnly cannot be null.";
                return false;
            }

            idleOnly = patch.IdleOnly.Value;
        }

        if (patch.HasEnabled)
        {
            if (patch.Enabled is null)
            {
                updated = existing;
                error = "enabled cannot be null.";
                return false;
            }

            enabled = patch.Enabled.Value;
        }

        if (patch.HasNextDueAt) nextDueAt = patch.NextDueAt;

        var wakeManager = existing.WakeManager;
        if (patch.HasWakeManager)
        {
            if (WakeManagerPolicy.Parse(patch.WakeManager) is not { } chosen)
            {
                updated = existing;
                error = TriggerCost.WakeManagerRefusal;
                return false;
            }

            wakeManager = chosen;
        }

        var dailyTokenCap = existing.DailyTokenCap;
        if (patch.HasDailyTokenCap)
        {
            if (patch.DailyTokenCap is < 1)
            {
                updated = existing;
                error = TriggerCost.DailyTokenCapRefusal;
                return false;
            }

            dailyTokenCap = patch.DailyTokenCap;
        }

        // RE-ARMED WHEN THE SHAPE MOVED, and armed when it was never armed at all.
        //
        // Two cases, and both would otherwise be dead ends. Changing an interval from a day to ten
        // minutes would leave the row pointing at tomorrow, so the edit would appear to work and change
        // nothing until the next fire. And re-enabling a row whose due time had been cleared - which is
        // what `container.once` leaves behind - would put it back in a state that can never fire, so
        // the toggle would do nothing at all.
        //
        // An explicit `nextDueAt` in the patch still wins, on the same contract creation uses: it means
        // "fire next at this time". Only an absent one is recomputed, so nothing here overrides a
        // caller that said when it wanted the next run.
        if (!patch.HasNextDueAt)
        {
            var shapeMoved = patch.HasKind
                || patch.HasExpression
                || patch.HasTimezone
                || patch.HasIntervalSeconds
                || patch.HasFireAt;

            if (shapeMoved || (enabled && nextDueAt is null))
            {
                nextDueAt = FirstOccurrence(
                    kind, expression, timezone, intervalSeconds, fireAt, DateTimeOffset.UtcNow);
            }
        }

        updated = existing with
        {
            Name = name,
            Container = container,
            Instruction = instruction,
            Kind = kind,
            Expression = expression,
            Timezone = timezone,
            IntervalSeconds = intervalSeconds,
            FireAt = fireAt,
            IdleOnly = idleOnly,
            Enabled = enabled,
            NextDueAt = nextDueAt,
            EventType = eventType,
            Filter = filter,
            WatchRoot = patch.HasWatchRoot ? patch.WatchRoot?.Trim() : existing.WatchRoot,
            WatchPath = patch.HasWatchPath ? patch.WatchPath ?? "" : existing.WatchPath,
            WatchGlob = patch.HasWatchGlob ? FolderWatch.Glob(patch.WatchGlob) : existing.WatchGlob,

            // Null on a PATCH puts a folder trigger's interval back to its default, applied at the route.
            PollSeconds = patch.HasPollSeconds ? patch.PollSeconds : existing.PollSeconds,
            QuietSeconds = patch.HasQuietSeconds ? patch.QuietSeconds : existing.QuietSeconds,
            MinIntervalSeconds = patch.HasMinIntervalSeconds ? patch.MinIntervalSeconds : existing.MinIntervalSeconds,
            WakeManager = wakeManager,
            DailyTokenCap = dailyTokenCap,
            OutcomeId = patch.HasOutcomeId ? patch.OutcomeId : existing.OutcomeId,
        };

        return true;
    }
}
