using Yawble.Core.Bus;

namespace Yawble.Core.Workflows.Running;

/// <summary>The failure decision as a bus decision card from the workflow member (Needs you), outside any run.</summary>
public sealed class BusWorkflowDecisions(BusEngine bus) : IWorkflowDecisions
{
    public async Task<long?> OpenFailureDecisionAsync(long memberId, string summary, string details, CancellationToken ct) =>
        await bus.OpenMemberDecisionAsync(memberId, new DecisionAsk(summary, summary,
        [
            new DecisionOption("Keep it running", "Keep running; I have looked at the failures."),
            new DecisionOption("I will fix the workflow", "I will fix the workflow or its settings."),
        ], details), ct);
}
