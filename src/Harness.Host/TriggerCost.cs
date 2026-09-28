using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Harness.Host;

/// <summary>
/// What a trigger's runs cost today, whether its daily cap stops the next fire, and what a member's
/// recent runs actually cost.
///
/// MEASURED ONLY. Spend is <see cref="InvocationUsage.BillableTokens"/>, summed by the log with the
/// weights the workflow budget uses. A run that reported no usage is counted as UNMEASURED, beside
/// the figure: it is never a zero and never an estimate, and it never convicts a cap. A run that ran
/// NO MODEL (a plugin's, <see cref="UsageSource.NoModel"/>) is not that: its cost is known, and it is
/// counted as a measured run of 0.
/// </summary>
public sealed class TriggerCost(
    IMessageLog log,
    ITriggerStore triggers,
    TenantLogging tenant,
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
        + "counted as such, never as zero) and `capReachedToday`.";

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
        var zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var midnight = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);

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
    /// skip recorded: a `schedule.skipped` row with <see cref="MessageTypes.ScheduleSkippedCapReason"/>,
    /// the trigger's outcome `capped`, and a `tenant_events` row. The caller fires nothing.
    /// <paramref name="nextDueAt"/> is what the sweep re-arms a clock trigger to; null for an event
    /// or folder trigger, whose due time is not this fire's to move.
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

        var source = TriggerKindIsClock(row.Kind) ? $"schedule:{row.Id}" : $"trigger:{row.Id}";
        var skipped = await log.AppendAsync(
            new NewMessage(
                MessageTypes.ScheduleSkipped,
                JsonSerializer.Serialize(new
                {
                    member = member.ToString(),
                    reason = MessageTypes.ScheduleSkippedCapReason,
                }),
                source,
                cause),
            ct);

        if (TriggerKindIsClock(row.Kind))
        {
            await triggers.RecordOutcomeAsync(row.Id, firedAt: null, nextDueAt, "capped", skipped.Seq, row.MissedCount, ct);
        }
        else
        {
            await triggers.RecordSkipAsync(row.Id, "capped", skipped.Seq, ct);
        }

        await tenant.WriteAsAsync(
            source,
            actorEmail: null,
            TenantActions.ScheduleSkipped,
            row.Id,
            row.Name,
            new
            {
                team = member.Team,
                container = member.Name,
                reason = MessageTypes.ScheduleSkippedCapReason,
                dailyTokenCap = row.DailyTokenCap,
                spentToday = spent.TokensSpent,
                skippedAt = now.ToString("O", CultureInfo.InvariantCulture),
                nextDueAt = nextDueAt?.ToString("O", CultureInfo.InvariantCulture),
            },
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
        view["capReachedToday"] = CapReached(row, spent);

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
        var runs = await log.ReadRecentRunsAsync(member.ToString(), RecentRuns, ct);
        var measured = runs.Select(run => BillableOf(run.Payload)).OfType<long>().Order().ToArray();

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
