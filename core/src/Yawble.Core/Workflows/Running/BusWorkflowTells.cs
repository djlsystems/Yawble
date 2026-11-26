using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Workflows.Running;

/// <summary>
/// A workflow's tells through the bus: each one roots a deliverable (the Manager plans it; another agent member gets it as its one card),
/// and a waiting run's deliverable is read from the bus to know when it ended. Telling another team follows Settings > Workflows
/// (<see cref="WorkflowRunStore.TellOtherTeams"/>).
/// </summary>
public sealed class BusWorkflowTells(BusEngine bus, WorkflowRunStore store) : IWorkflowTellers, IWorkflowWaitSource
{
    public string Kind => WorkflowWait.DeliverableKind;

    public IWorkflowTeller For(WorkflowCaller caller, string workflow) => new Teller(bus, store, caller, workflow);

    public async Task<WorkflowWaitResult?> CheckAsync(WorkflowWait wait, CancellationToken ct)
    {
        if (wait.Deliverable is not { } deliverable) return null;
        var member = $"{Str(wait.Data, "team")}/{Str(wait.Data, "member")}";
        var outcome = await bus.WorkflowTellOutcomeAsync(deliverable, member, ct);
        return outcome is null ? null : new WorkflowWaitResult(outcome.Outcome, outcome.Text);
    }

    private static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private sealed class Teller(BusEngine bus, WorkflowRunStore store, WorkflowCaller caller, string workflow) : IWorkflowTeller
    {
        public async Task<WorkflowTellSent> TellAsync(WorkflowTellCall call, CancellationToken ct)
        {
            var team = call.Team ?? caller.Team;
            if (!string.Equals(team, caller.Team, StringComparison.OrdinalIgnoreCase) && !store.TellOtherTeams())
                throw new WorkflowActionException($"telling another team ({team}) is turned off in Settings > Workflows; nothing was sent");
            var extra = new JsonObject
            {
                ["workflow"] = new JsonObject
                {
                    ["workflow"] = workflow, ["memberId"] = caller.MemberId, ["member"] = caller.Member, ["team"] = caller.Team, ["run"] = caller.RunId,
                    ["step"] = call.StepId, ["wait"] = call.Wait,
                },
            };
            try
            {
                var sent = await bus.WorkflowTellAsync(team, call.Member, Instruction(call), extra, ct);
                return new WorkflowTellSent(sent.Deliverable, sent.Member, sent.Team);
            }
            catch (BusRefusal e)
            {
                throw new WorkflowActionException(e.Message);
            }
        }

        /// <summary>The instruction: where it came from (the workflow, the workflow member and its run), the step's text, and the reply it asks for.</summary>
        private string Instruction(WorkflowTellCall call)
        {
            var from = $"From the workflow {workflow}, run by the workflow member {caller.Member ?? "(a Test run)"} of {caller.Team} (workflow run {caller.RunId}). "
                + (call.Wait ? "The workflow waits for this deliverable to end and reads its closing summary." : "The workflow goes on without waiting.");
            var text = from + "\n\n" + call.Text.Trim();
            if (call.ReplySchemaName is { } name && call.ReplySchema is { } schema)
                text += "\n\n" + WorkflowTell.ReplyInstruction(name, schema);
            return text;
        }
    }
}
