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
}
