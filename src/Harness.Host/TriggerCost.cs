using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Harness.Host;

/// <summary>
/// What a trigger's runs cost today, whether its daily cap stops the next fire, and what a member's
/// recent runs actually cost.
///
/// MEASURED ONLY. Spend is <see cref="InvocationUsage.BillableTokens"/>, summed from the usage ledger with the
/// weights the workflow budget uses. A run that reported no usage is counted as UNMEASURED, beside
/// the figure: it is never a zero and never an estimate, and it never convicts a cap. A run that ran
/// NO MODEL (a plugin's, <see cref="UsageSource.NoModel"/>) is not that: its cost is known, and it is
/// counted as a measured run of 0.
/// </summary>
public sealed class TriggerCost(
    IMessageLog log,
    IUsageLedger ledger,
    ITriggerStore triggers,
    IOptions<JsonOptions> json)
{
    public const string WakeManagerRefusal =
        "wakeManager must be one of: always, onHandbackOrFailure, never.";

    public const string DailyTokenCapRefusal =
        "dailyTokenCap must be a whole number of at least 1, or null for no cap.";

    /// <summary>The read-only fields every trigger route returns, for the route descriptions.</summary>
    public const string SpendDescription =
        "`spentToday` (`billableTokens`, `measuredRuns`, `unmeasuredRuns`: what the runs it started "
        + "today in its timezone cost, the Manager runs they woke included; an unmeasured run is "
        + "counted as such, never as zero), `capReachedToday`, `cappedUntil` (a schedule asleep on "
        + "its cap: the time it resumes, else null) and `skippedToday` (fires the cap skipped today; "
        + "only the first was logged).";

    /// <summary>How many of a member's latest runs the dialog's measured cost looks at.</summary>
    public const int RecentRuns = 10;

    /// <summary>`schedule:&lt;id&gt;` for the sweep's fires, `trigger:&lt;id&gt;` for the event arm's.
    /// Both, because a PATCH may change a trigger's kind during the day.</summary>
    public static IReadOnlyCollection<string> SourcesOf(string triggerId) =>
        [$"schedule:{triggerId}", $"trigger:{triggerId}"];

    /// <summary>
    /// Midnight today in <paramref name="timezone"/> (UTC when it has none, or one that does not
    /// resolve), as an instant. A midnight a clock change skips is the first instant after it.
    /// </summary>
    public static DateTimeOffset StartOfDay(string? timezone, DateTimeOffset now)
    {
        var zone = ZoneOf(timezone);
        return Midnight(zone, TimeZoneInfo.ConvertTime(now, zone).Date);
    }

    /// <summary>Midnight at the end of today in <paramref name="timezone"/>: the start of the next
    /// day by the same boundary <see cref="StartOfDay"/> draws.</summary>
    public static DateTimeOffset StartOfNextDay(string? timezone, DateTimeOffset now)
    {
        var zone = ZoneOf(timezone);
        return Midnight(zone, TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1));
    }

    /// <summary>
    /// When a clock trigger capped at <paramref name="now"/> fires again: its first occurrence on or
    /// after the start of the next day in its timezone, in UTC. <paramref name="next"/> is the
    /// occurrence it would have fired at next; an every-N with no anchor keeps that phase. Null when
    /// it has no occurrence left (a one-off).
    /// </summary>
    public static DateTimeOffset? ResumeAt(TriggerRow row, DateTimeOffset now, DateTimeOffset? next)
    {
        if (next is null || !Enum.TryParse<TriggerKind>(row.Kind, ignoreCase: true, out var kind)) return null;

        var boundary = StartOfNextDay(row.Timezone, now);
        if (next.Value >= boundary) return next.Value.ToUniversalTime();

        var anchor = kind == TriggerKind.Every ? row.FireAt ?? next : row.FireAt;
        return Triggers.NextOccurrence(
            kind, row.Expression, row.Timezone, row.IntervalSeconds, anchor, boundary.AddTicks(-1))?.ToUniversalTime();
    }

    /// <summary>The skip reason of a schedule asleep on its cap: when it resumes, in its own
    /// timezone's offset.</summary>
    public static string CapReason(string? timezone, DateTimeOffset resumesAt) =>
        $"{MessageTypes.ScheduleSkippedCapReason}; resumes at "
        + TimeZoneInfo.ConvertTime(resumesAt, ZoneOf(timezone))
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static TimeZoneInfo ZoneOf(string? timezone)
    {
        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static DateTimeOffset Midnight(TimeZoneInfo zone, DateTime date)
    {
        var midnight = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);

        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(15);

        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    public Task<WorkflowSpend> SpentTodayAsync(TriggerRow row, DateTimeOffset now, CancellationToken ct = default) =>
        log.GetTriggerSpendAsync(SourcesOf(row.Id), StartOfDay(row.Timezone, now), ct);

    /// <summary>Set, and reached by MEASURED spend. Unmeasured runs never reach a cap.</summary>
    public static bool CapReached(TriggerRow row, WorkflowSpend spent) =>
        row.DailyTokenCap is { } cap && spent.TokensSpent >= cap;

    /// <summary>
    /// Whether <paramref name="row"/>'s next fire is stopped by its daily cap, and when it is, the
    /// skip recorded. The caller fires nothing.
    ///
    /// SAID ONCE A DAY. The first skip of the day writes a `schedule.skipped` row and a
    /// `tenant_events` row and sets the outcome `capped`; every later one that day is only counted
    /// (`skippedToday`). A clock trigger also SLEEPS: its stored `next_due_at` becomes its first
    /// occurrence of the next day (<see cref="ResumeAt"/>), and the reason names that time. Nothing
    /// is held in memory, so a restart waits for the stored time like any other.
    /// <paramref name="nextDueAt"/> is the occurrence the sweep would re-arm a clock trigger to;
    /// null for an event or folder trigger, whose due time is not this fire's to move.
    /// </summary>
    public async Task<bool> SkipIfCappedAsync(
        TriggerRow row,
        ContainerId member,
        DateTimeOffset now,
        DateTimeOffset? nextDueAt,
        long? cause,
        CancellationToken ct = default)
    {
        if (row.DailyTokenCap is null) return false;

        var spent = await SpentTodayAsync(row, now, ct);
        if (!CapReached(row, spent)) return false;

        var clock = TriggerKindIsClock(row.Kind);
        var resumesAt = clock ? ResumeAt(row, now, nextDueAt) : null;

        var reason = resumesAt is { } resumes ? CapReason(row.Timezone, resumes) : MessageTypes.ScheduleSkippedCapReason;
        var source = clock ? $"schedule:{row.Id}" : $"trigger:{row.Id}";
        var dayStart = StartOfDay(row.Timezone, now);

        // ONE UNIT: the count and the sleep, the `schedule.skipped` row and its tenant row commit
        // together. If the rows cannot be written nothing is stored, and the next fire tries again
        // rather than finding a trigger asleep with no row saying why. A later skip that day is
        // only counted, in the same transaction shape, with nothing appended.
        await log.AppendWithinAsync(
            async (connection, transaction, token) =>
                await triggers.CountCappedSkipAsync(
                    connection, transaction, row.Id, dayStart, rearm: clock, resumesAt, token) > 1
                    ? null
                    : new NewMessage(
                        MessageTypes.ScheduleSkipped,
                        JsonSerializer.Serialize(new
                        {
                            member = member.ToString(),
                            reason,
                        }),
                        source,
                        cause),
            (connection, transaction, skipped, token) => triggers.RecordCappedSkipAsync(
                connection,
                transaction,
                row.Id,
                skipped.Seq,
                new TriggerAudit(
                    source,
                    ActorEmail: null,
                    TenantActions.ScheduleSkipped,
                    row.Id,
                    row.Name,
                    JsonSerializer.Serialize(new
                    {
                        team = member.Team,
                        container = member.Name,
                        reason,
                        dailyTokenCap = row.DailyTokenCap,
                        spentToday = spent.TokensSpent,
                        skippedAt = now.ToString("O", CultureInfo.InvariantCulture),
                        nextDueAt = (clock ? resumesAt : null)?.ToString("O", CultureInfo.InvariantCulture),
                    })),
                token),
            ct);

        return true;
    }

    /// <summary>The trigger as every route returns it: its row, plus `spentToday` and
    /// `capReachedToday`, read-only.</summary>
    public async Task<JsonObject> ViewAsync(TriggerRow row, DateTimeOffset now, CancellationToken ct = default)
    {
        var spent = await SpentTodayAsync(row, now, ct);
        var view = JsonSerializer.SerializeToNode(row, json.Value.SerializerOptions)!.AsObject();

        view["spentToday"] = new JsonObject
        {
            ["billableTokens"] = spent.TokensSpent,
            ["measuredRuns"] = spent.RunsWithMeasuredUsage,
            ["unmeasuredRuns"] = spent.RunsWithoutUsage,
        };
        var reached = CapReached(row, spent);
        view["capReachedToday"] = reached;

        // Asleep on the cap: a schedule whose last fire the cap skipped, still over it today, and
        // due later. A cap raised or cleared since ends it, and so does the next day. The time is
        // when it really resumes, the same ResumeAt the sleep stores: after a person edits the
        // schedule it is due again from now, and that fire only skips and sleeps.
        view["cappedUntil"] = TriggerKindIsClock(row.Kind)
            && reached
            && row.LastOutcome == "capped"
            && row.NextDueAt is { } due
            && due > now
            && ResumeAt(row, now, due) is { } resumes
                ? resumes.ToString("O", CultureInfo.InvariantCulture)
                : null;

        view["skippedToday"] = row.CappedSkipsDay == StartOfDay(row.Timezone, now) ? row.CappedSkips : 0;

        // The count's own columns are read through `skippedToday`, which knows which day they are.
        view.Remove("cappedSkipsDay");
        view.Remove("cappedSkips");

        return view;
    }

    public async Task<IReadOnlyList<JsonObject>> ViewsAsync(
        IEnumerable<TriggerRow> rows, DateTimeOffset now, CancellationToken ct = default)
    {
        var views = new List<JsonObject>();
        foreach (var row in rows) views.Add(await ViewAsync(row, now, ct));
        return views;
    }

    /// <summary>What a member's last <see cref="RecentRuns"/> runs measured. See the route.</summary>
    public async Task<MemberRecentCost> RecentAsync(ContainerId member, string kind, CancellationToken ct = default)
    {
        // FROM THE LEDGER, one row per run, so a Reset's "Delete memory" leaves a member's recent
        // cost as it was. `billable` is NULL for an unmeasured run and 0 for one that ran no model -
        // the rule BillableOf reads a log row by.
        var runs = await ledger.ReadRecentRunsAsync(member, RecentRuns, ct);
        var measured = runs.Where(run => run.Measured).Select(run => run.Billable ?? 0).Order().ToArray();

        long? median = measured.Length == 0
            ? null
            : measured.Length % 2 == 1
                ? measured[measured.Length / 2]
                : (measured[(measured.Length / 2) - 1] + measured[measured.Length / 2]) / 2;

        return new MemberRecentCost(runs.Count, measured.Length, runs.Count - measured.Length, median, kind);
    }

    /// <summary>
    /// One terminal row's <see cref="InvocationUsage.BillableTokens"/>, or null when it measured
    /// nothing (no split and no combined total, or an excluded estimate); 0 for a run that ran no
    /// model - the same rule the spend SQL counts by.
    /// </summary>
    public static long? BillableOf(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("tokensSource", out var source)
                && source.ValueKind == JsonValueKind.String
                && source.GetString() == UsageSource.ExcludedEstimate)
            {
                return null;
            }

            // A run that ran no model (a plugin's) is measured, and it cost nothing.
            if (source.ValueKind == JsonValueKind.String && source.GetString() == UsageSource.NoModel) return 0;

            var tokensIn = Int(root, "tokensIn");
            var tokensOut = Int(root, "tokensOut");
            var total = Int(root, "tokensTotal");
            var sourceName = source.ValueKind == JsonValueKind.String ? source.GetString() ?? "" : "";

            if (total is { } combined) return InvocationUsage.Combined(combined, sourceName).BillableTokens();
            if (tokensIn is null || tokensOut is null) return null;

            return new InvocationUsage(
                tokensIn.Value,
                tokensOut.Value,
                sourceName,
                cachedIn: Int(root, "tokensCachedIn"),
                cacheCreation: Int(root, "tokensCacheCreation")).BillableTokens();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool TriggerKindIsClock(string kind) =>
        Enum.TryParse<TriggerKind>(kind, ignoreCase: true, out var parsed)
        && parsed is TriggerKind.Cron or TriggerKind.Every or TriggerKind.Once;
}

/// <summary>`GET /api/teams/{team}/containers/{name}/cost`.</summary>
public sealed record MemberRecentCost(
    int LastRuns,
    int MeasuredRuns,
    int UnmeasuredRuns,
    long? MedianBillableTokens,
    string Kind);
