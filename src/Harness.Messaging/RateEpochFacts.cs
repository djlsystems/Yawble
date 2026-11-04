using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// THE RATE EPOCH AND THE RATE TIMELINE, decided once for both providers (<c>OutcomeSchema</c>'s
/// <c>outcome-012</c>).
///
/// <para>
/// THE EPOCH IS THE RATE IN FORCE WHEN THE STEP WAS APPLIED, written once by that start and never again:
/// a positive stored <c>outcomes.agentHourlyRate</c> row is <see cref="SourceTenant"/>; with no such row,
/// a positive configured <c>Outcomes:AgentHourlyRate</c> is <see cref="SourceConfiguration"/>; anything
/// else - a zero, an unreadable value, a stored 0 that hides a configured rate - is
/// <see cref="ExecutionSnapshotFacts.RateSourceNone"/> with no amount. This is the only place a
/// configuration-only rate is read: a new run's snapshot still reads the stored row alone.
/// </para>
///
/// <para>
/// THE TIMELINE is every <c>tenant.settingChanged</c> and <c>tenant.setting-reset</c> row whose subject
/// is <c>outcomes.agentHourlyRate</c>, read when a figure is asked for: the rate a run started under is
/// the new value of the latest such row at or before its start (<see cref="RateAt"/>).
/// </para>
/// </summary>
public static class RateEpochFacts
{
    /// <summary>The epoch's rate was the stored tenant setting.</summary>
    public const string SourceTenant = "tenant";

    /// <summary>The epoch's rate was set only in the Host's configuration.</summary>
    public const string SourceConfiguration = "configuration";

    /// <summary>A legacy agent run priced from the tenant rate in effect at its start.</summary>
    public const string RateSourceReconstructed = "reconstructed";

    /// <summary>A legacy agent run that started before every recorded rate change, priced from the epoch.</summary>
    public const string RateSourceFallback = "fallback-at-upgrade";

    /// <summary>The configuration key the tenant rate falls back to, as <c>TenantSettings</c> defines it.</summary>
    public const string ConfiguredRateKey = "Outcomes:AgentHourlyRate";

    /// <summary>The configuration key the tenant currency falls back to.</summary>
    public const string ConfiguredCurrencyKey = "Outcomes:Currency";

    /// <summary>The epoch row to write, from the stored rows (null when there is none) and the configured values.</summary>
    public static (string? Amount, string? Currency, string Source) Decide(
        string? storedRate, string? storedCurrency, string? configuredRate, string? configuredCurrency)
    {
        var (raw, source) = storedRate is not null ? (storedRate, SourceTenant) : (configuredRate, SourceConfiguration);

        if (Positive(raw) is not { } amount) return (null, null, ExecutionSnapshotFacts.RateSourceNone);

        var currency = !string.IsNullOrWhiteSpace(storedCurrency) ? storedCurrency
            : !string.IsNullOrWhiteSpace(configuredCurrency) ? configuredCurrency
            : ExecutionSnapshotFacts.DefaultCurrency;

        return (ExecutionSnapshotFacts.Canonical(amount), currency.Trim().ToUpperInvariant(), source);
    }

    /// <summary>A positive decimal, or null for anything else: missing, blank, zero, negative, unreadable.</summary>
    public static decimal? Positive(string? text) =>
        text is not null
        && decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
        && value > 0
            ? value
            : null;

    /// <summary>
    /// The rate a legacy agent run that started at <paramref name="startedAt"/> is priced at, and its
    /// source: the latest change at or before it - <see cref="RateSourceReconstructed"/> when positive,
    /// none when it set 0, nothing or a reset - else the epoch's - <see cref="RateSourceFallback"/> when
    /// positive, none otherwise. A later row's old value is never read.
    /// </summary>
    public static (decimal? Rate, string Source) RateAt(RateTimeline timeline, DateTimeOffset startedAt)
    {
        RateChange? latest = null;
        foreach (var change in timeline.Changes)
        {
            if (change.At > startedAt) continue;
            if (latest is null || change.At > latest.At || (change.At == latest.At && change.Seq > latest.Seq)) latest = change;
        }

        if (latest is not null)
        {
            return Positive(latest.NewValue) is { } rate
                ? (rate, RateSourceReconstructed)
                : (null, ExecutionSnapshotFacts.RateSourceNone);
        }

        return Positive(timeline.Epoch?.RateAmount) is { } epoch
            ? (epoch, RateSourceFallback)
            : (null, ExecutionSnapshotFacts.RateSourceNone);
    }

    /// <summary>The tenant rows the timeline is read from: their actions, and the subject they name.</summary>
    public static readonly string[] ChangeActions = [TenantActions.TenantSettingChanged, TenantActions.TenantSettingReset];

    /// <summary>The value a setting row's detail set: its <c>new</c>, null for a reset or a detail with none.</summary>
    public static string? NewValueOf(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;

        try
        {
            using var document = JsonDocument.Parse(detail);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("new", out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The timeline's rows from a reader over <c>seq, occurred_at, detail</c>, oldest first.</summary>
    public static async Task<IReadOnlyList<RateChange>> ReadChangesAsync(DbCommand command, CancellationToken ct)
    {
        var changes = new List<RateChange>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!DateTimeOffset.TryParse(
                    reader.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            {
                continue;
            }

            changes.Add(new RateChange(at, reader.GetInt64(0), NewValueOf(reader.IsDBNull(2) ? null : reader.GetString(2))));
        }

        return changes;
    }

    /// <summary>The epoch row from a reader over <c>recorded_at, rate_amount, rate_currency, rate_source</c>, or null.</summary>
    public static async Task<RateEpoch?> ReadEpochAsync(DbCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new RateEpoch(
            DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3));
    }
}
