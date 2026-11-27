using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Workflows.Engine;
using Yawble.Core.Workflows.Language;

namespace Yawble.Core.Workflows.Running;

/// <summary>
/// A workflow's tells through the bus: each one roots a deliverable (the Manager plans it; another agent member gets it as its one card),
/// and a waiting run's deliverable is read from the bus to know when it ended. Telling another team follows Settings > Workflows
/// (<see cref="WorkflowRunStore.TellOtherTeams"/>). A tell's attachments are copied into the told team's documents (attachments/,
/// as <see cref="Ui.Attachments"/> names them), where its agents can read them without a grant on the source folder.
/// </summary>
/// <param name="teamDocuments">A team's documents folder (null: none; then a tell with attachments fails). Null: the bus's
/// <see cref="Teams.TeamDocuments"/> when it has the teams' folders.</param>
public sealed class BusWorkflowTells(BusEngine bus, WorkflowRunStore store, Func<string, string?>? teamDocuments = null, TimeProvider? clock = null)
    : IWorkflowTellers, IWorkflowWaitSource
{
    public string Kind => WorkflowWait.DeliverableKind;

    private readonly BusEngine _bus = bus;
    private readonly WorkflowRunStore _store = store;
    private readonly TimeProvider? _clock = clock;
    private readonly Func<string, string?> _docs = teamDocuments ?? (team => bus.Repos is { } repos ? new Teams.TeamDocuments(repos).Root(team) : null);

    public IWorkflowTeller For(WorkflowCaller caller, string workflow) => new Teller(this, caller, workflow);

    public async Task<WorkflowWaitResult?> CheckAsync(WorkflowWait wait, CancellationToken ct)
    {
        if (wait.Deliverable is not { } deliverable) return null;
        var member = $"{Str(wait.Data, "team")}/{Str(wait.Data, "member")}";
        var outcome = await _bus.WorkflowTellOutcomeAsync(deliverable, member, ct);
        return outcome is null ? null : new WorkflowWaitResult(outcome.Outcome, outcome.Text);
    }

    private static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private sealed class Teller(BusWorkflowTells owner, WorkflowCaller caller, string workflow) : IWorkflowTeller
    {
        private readonly BusEngine bus = owner._bus;

        public async Task<WorkflowTellSent> TellAsync(WorkflowTellCall call, CancellationToken ct)
        {
            var team = call.Team ?? caller.Team;
            if (!string.Equals(team, caller.Team, StringComparison.OrdinalIgnoreCase) && !owner._store.TellOtherTeams())
                throw new WorkflowActionException($"telling another team ({team}) is turned off in Settings > Workflows; nothing was sent");
            IReadOnlyList<string> copies = [];
            if (call.Attachments is { Count: > 0 } files)
            {
                var docs = owner._docs(team)
                    ?? throw new WorkflowActionException($"the team {team} has no documents folder to attach files in; nothing was sent");
                try { copies = Ui.Attachments.Copy(docs, files, owner._clock); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new WorkflowActionException($"attaching the files failed: {e.Message}; nothing was sent");
                }
            }
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
                var sent = await bus.WorkflowTellAsync(team, call.Member, Instruction(call, copies), extra, ct);
                return new WorkflowTellSent(sent.Deliverable, sent.Member, sent.Team, copies.Count == 0 ? null : copies);
            }
            catch (Exception e)
            {
                foreach (var copy in copies)
                    try { File.Delete(copy); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
                if (e is BusRefusal) throw new WorkflowActionException(e.Message);
                throw;
            }
        }

        /// <summary>
        /// The instruction: where it came from (the workflow, the workflow member and its run), the step's text, the attachments'
        /// copies, and the reply it asks for.
        /// </summary>
        private string Instruction(WorkflowTellCall call, IReadOnlyList<string> copies)
        {
            var from = $"From the workflow {workflow}, run by the workflow member {caller.Member ?? "(a Test run)"} of {caller.Team} (workflow run {caller.RunId}). "
                + (call.Wait ? "The workflow waits for this deliverable to end and reads its closing summary." : "The workflow goes on without waiting.");
            var text = from + "\n\n" + call.Text.Trim();
            if (copies.Count > 0) text += "\n\n" + WorkflowTell.AttachmentsText(copies);
            if (call.ReplySchemaName is { } name && call.ReplySchema is { } schema)
                text += "\n\n" + WorkflowTell.ReplyInstruction(name, schema);
            return text;
        }
    }
}
