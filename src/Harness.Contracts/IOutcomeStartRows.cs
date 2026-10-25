namespace Harness.Contracts;

/// <summary>
/// THE ONE-TIME BACK-FILL OF BACKLOG ITEMS' OUTCOMES as rows: each item with no outcome takes its
/// newest dispatch workflow's, and a tenant setting marks it done, in one transaction. A start depends
/// on this, not on the SQLite class, so a provider is a different implementation and never a different
/// caller.
/// </summary>
public interface IOutcomeStartRows
{
    /// <summary>How many items it filled, or null when it had already run.</summary>
    Task<int?> RunAsync(CancellationToken ct = default);
}
