using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE LEDGER'S ONE-TIME START AS ROWS: the backfill of the usage and workflow ledgers from the log
/// and the instance's id, in one transaction. A start depends on this, not on the SQLite class, so a
/// provider is a different implementation and never a different caller. See <see cref="LedgerStart"/>
/// for what it must do.
/// </summary>
public interface ILedgerStartRows
{
    Task<LedgerStartResult> RunAsync(CancellationToken ct = default);

    /// <summary>
    /// THE RATE EPOCH (<c>outcome-012</c>), written once: when <c>rate_epochs</c> is empty, one row of the
    /// rate in force now, from the stored <c>tenant_settings</c> rows and - with no rate row -
    /// <paramref name="configured"/> (<see cref="Harness.Messaging.RateEpochFacts.Decide"/>). With a row
    /// already there it writes nothing and answers that row: a later start, or a later rate, never moves it.
    /// </summary>
    Task<RateEpoch> RecordRateEpochAsync(RateEpochConfiguration configured, CancellationToken ct = default);
}

/// <summary>The Host's configured <c>Outcomes:AgentHourlyRate</c> and <c>Outcomes:Currency</c>, as read at
/// start: what the epoch falls back to when no rate row is stored.</summary>
public sealed record RateEpochConfiguration(string? AgentHourlyRate, string? Currency)
{
    public static readonly RateEpochConfiguration None = new(null, null);
}
