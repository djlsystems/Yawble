namespace Harness.Contracts;

/// <summary>
/// THE TENANT ROW OF A PLUGIN REMOVAL, bracketing the file move. `PluginRemover` owns the files; this
/// owns only the transaction around them, so both providers keep the one guarantee: the row and the
/// move land together or neither does.
/// </summary>
public interface IPluginRemovalRows
{
    /// <summary>
    /// Appends <paramref name="row"/> in a transaction, then runs <paramref name="moveFiles"/>. When that
    /// answers false the transaction is dropped uncommitted (no row for a removal that did not happen)
    /// and this answers false. Otherwise the transaction commits and this answers true; a commit that
    /// fails runs <paramref name="undoMove"/> and rethrows.
    /// </summary>
    Task<bool> RecordWhileAsync(TriggerAudit row, Func<bool> moveFiles, Action undoMove, CancellationToken ct = default);
}
