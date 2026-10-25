namespace Harness.Contracts;

/// <summary>
/// WHICH MESSAGE-LOG ROWS THE DAILY RETENTION PASS MAY HAND TO <see cref="IMessageLog.DeleteAsync"/>.
/// It chooses and never deletes: the log's one delete does that, so a row a survivor cites or one still
/// queued for a run is retained there whatever is chosen here.
/// </summary>
public interface ILogRetention
{
    /// <summary>At most <paramref name="max"/> rows after <paramref name="afterSeq"/>, oldest first, of
    /// the eligible workflows, never a root, declaration or close.</summary>
    Task<IReadOnlyList<long>> BodyAsync(DateTimeOffset cutoff, long afterSeq, int max, CancellationToken ct = default);

    /// <summary>Whole eligible workflows, every row each still has, within <paramref name="max"/>
    /// rows in all, oldest workflow first.</summary>
    Task<IReadOnlyList<long>> WholeAsync(DateTimeOffset cutoff, int max, CancellationToken ct = default);
}
