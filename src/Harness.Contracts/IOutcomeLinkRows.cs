using System.Data.Common;

namespace Harness.Contracts;

/// <summary>
/// A LINK AND ITS TENANT ROW, WRITTEN INSIDE A CALLER'S APPEND: what a person's or the Concierge's
/// <c>tell</c> naming an outcome writes with its root row, in the transaction
/// <see cref="IMessageLog.AppendWithinAsync"/> hands its callback. Each provider's link writer and
/// tenant row, so the route never names a provider's connection type.
/// </summary>
public interface IOutcomeLinkRows
{
    /// <summary>Appends the link of <paramref name="correlation"/> to <paramref name="outcomeId"/>
    /// (the item it inherits included), then <paramref name="audit"/>, both in <paramref name="transaction"/>.</summary>
    Task WriteWithinAsync(
        DbConnection connection, DbTransaction transaction, long correlation, string outcomeId, string? team,
        string setBy, string setByKind, string how, TriggerAudit audit, CancellationToken ct);
}
