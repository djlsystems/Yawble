namespace Harness.Contracts;

/// <summary>
/// THE SPEND READS AS THEY WERE BEFORE THE LEDGER: projections of the message log's terminal rows,
/// kept as the reference the ledger is held to. Read by the ledger parity tests and nothing else.
/// </summary>
public interface ILogSpend
{
    /// <summary>Spend since the last nudge. See <see cref="IMessageLog.GetSpendSinceNudgeAsync"/>.</summary>
    Task<WorkflowSpend> GetSpendSinceNudgeAsync(long correlationId, CancellationToken ct = default);

    Task<WorkflowSpend> GetWorkflowSpendAsync(long correlationId, CancellationToken ct = default);

    Task<WorkflowSpend> GetTriggerSpendAsync(
        IReadOnlyCollection<string> sources, DateTimeOffset since, CancellationToken ct = default);

    Task<IReadOnlyList<Message>> ReadRecentRunsAsync(string member, int max, CancellationToken ct = default);
}
