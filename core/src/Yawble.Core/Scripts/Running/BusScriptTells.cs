using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Scripts.Running;

/// <summary>
/// A script's tells through the bus: each one roots a workflow (the Manager plans it; another agent member gets it as its one card),
/// and a waiting run's workflow is read from the bus to know when it ended. Telling another team follows Settings > Scripts
/// (<see cref="ScriptRunStore.TellOtherTeams"/>).
/// </summary>
public sealed class BusScriptTells(BusEngine bus, ScriptRunStore store) : IScriptTellers, IScriptWaitSource
{
    public string Kind => ScriptWait.WorkflowKind;

    public IScriptTeller For(ScriptCaller caller, string script) => new Teller(bus, store, caller, script);

    public async Task<ScriptWaitResult?> CheckAsync(ScriptWait wait, CancellationToken ct)
    {
        if (wait.Workflow is not { } workflow) return null;
        var member = $"{Str(wait.Data, "team")}/{Str(wait.Data, "member")}";
        var outcome = await bus.ScriptTellOutcomeAsync(workflow, member, ct);
        return outcome is null ? null : new ScriptWaitResult(outcome.Outcome, outcome.Text);
    }

    private static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private sealed class Teller(BusEngine bus, ScriptRunStore store, ScriptCaller caller, string script) : IScriptTeller
    {
        public async Task<ScriptTellSent> TellAsync(ScriptTellCall call, CancellationToken ct)
        {
            var team = call.Team ?? caller.Team;
            if (!string.Equals(team, caller.Team, StringComparison.OrdinalIgnoreCase) && !store.TellOtherTeams())
                throw new ScriptActionException($"telling another team ({team}) is turned off in Settings > Scripts; nothing was sent");
            var extra = new JsonObject
            {
                ["script"] = new JsonObject
                {
                    ["script"] = script, ["memberId"] = caller.MemberId, ["member"] = caller.Member, ["team"] = caller.Team, ["run"] = caller.RunId,
                    ["step"] = call.StepId, ["wait"] = call.Wait,
                },
            };
            try
            {
                var sent = await bus.ScriptTellAsync(team, call.Member, Instruction(call), extra, ct);
                return new ScriptTellSent(sent.Workflow, sent.Member, sent.Team);
            }
            catch (BusRefusal e)
            {
                throw new ScriptActionException(e.Message);
            }
        }

        /// <summary>The instruction: where it came from (the script, the script member and its run), the step's text, and the reply it asks for.</summary>
        private string Instruction(ScriptTellCall call)
        {
            var from = $"From the script {script}, run by the script member {caller.Member ?? "(a Test run)"} of {caller.Team} (script run {caller.RunId}). "
                + (call.Wait ? "The script waits for this workflow to end and reads its closing summary." : "The script goes on without waiting.");
            var text = from + "\n\n" + call.Text.Trim();
            if (call.ReplySchemaName is { } name && call.ReplySchema is { } schema)
                text += "\n\n" + ScriptTell.ReplyInstruction(name, schema);
            return text;
        }
    }
}
