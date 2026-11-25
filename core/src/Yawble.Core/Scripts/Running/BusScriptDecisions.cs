using Yawble.Core.Bus;

namespace Yawble.Core.Scripts.Running;

/// <summary>The failure decision as a bus decision card from the script member (Needs you), outside any run.</summary>
public sealed class BusScriptDecisions(BusEngine bus) : IScriptDecisions
{
    public async Task<long?> OpenFailureDecisionAsync(long memberId, string summary, string details, CancellationToken ct) =>
        await bus.OpenMemberDecisionAsync(memberId, new DecisionAsk(summary, summary,
        [
            new DecisionOption("Keep it running", "Keep running; I have looked at the failures."),
            new DecisionOption("I will fix the script", "I will fix the script or its settings."),
        ], details), ct);
}
