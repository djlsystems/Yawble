using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THIS WORKER'S ANSWERS ABOUT ITS AGENT CLIS: a sign-in probe, a list of commands run, a removal as
/// the agent. Each request is applied on its own task, as a launch check is, so the worker's ordered
/// command queue is never held by a CLI, and answered by one event under the request's id.
/// </summary>
public sealed class WorkerAgentCli(WorkerId id, AgentLaunchUser? runAs, RunHomes homes, ILogger? log = null)
{
    private readonly AgentCommands _commands = new(runAs, homes);

    /// <summary>What a path outside the removal's boundary is refused with.</summary>
    public static string NotConfined(string path, string boundary) => $"Refused: {path} is not inside {boundary}.";

    /// <summary>Starts the request's work and returns at once; <paramref name="publish"/> says the answer.</summary>
    public void Apply(AgentCliCommand request, Func<WorkerEvent, Task> publish) =>
        _ = Task.Run(() => AnswerAsync(request, publish), CancellationToken.None);

    /// <summary>The request's answer, worked out here. Never throws: a failure is the answer's words.</summary>
    public async Task<WorkerEvent> AnswerAsync(AgentCliCommand request, CancellationToken ct = default)
    {
        switch (request)
        {
            case ProbeSignIn probe:
                var probed = new List<SignInProbeResult>(probe.Commands.Count);
                foreach (var spec in probe.Commands)
                {
                    try
                    {
                        probed.Add(await SignInProbe.ProbeAsync(spec, runAs, ct));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        probed.Add(new SignInProbeResult(spec.Command, true, null, $"The probe failed on worker {id}: {exception.Message}"));
                    }
                }

                return new SignInProbed(probe.Request, probed);

            case RunAgentCommands run:
                var ran = new List<AgentCliRunResult>(run.Commands.Count);
                foreach (var command in run.Commands) ran.Add(await _commands.RunAsync(command, run.Credential, ct));
                return new AgentCommandsRan(run.Request, ran);

            case RemoveAsAgent remove:
                return new RemovedAsAgent(remove.Request, await RemoveAsync(remove, ct));

            default:
                throw new NotSupportedException($"A worker does not take {request.GetType().Name}.");
        }
    }

    private async Task AnswerAsync(AgentCliCommand request, Func<WorkerEvent, Task> publish)
    {
        try
        {
            await publish(await AnswerAsync(request));
        }
        catch (Exception exception)
        {
            log?.LogWarning("A {Request} ended, and saying so failed: {Message}", request.GetType().Name, exception.Message);
        }
    }

    /// <summary>
    /// <c>rm -rf --one-file-system -- &lt;paths&gt;</c> as the agent, through the launch prefix, in
    /// chunks of 100. Each path is checked against the boundary again here: one that is not inside it
    /// with no link on the way is refused, never removed. Null when every confined path was handed to
    /// <c>rm</c>; what <c>rm</c> could not remove the caller's own pass finds.
    /// </summary>
    private async Task<string?> RemoveAsync(RemoveAsAgent remove, CancellationToken ct)
    {
        var refused = remove.Paths.Where(path => !RunHomeRemoval.Confined(remove.Boundary, path)).ToList();
        var targets = remove.Paths.Except(refused, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToList();

        if (targets.Count > 0)
        {
            if (SystemCommand.Find("rm") is not { } rm) return $"No rm in a system directory on worker {id}; nothing was removed as the agent.";
            if (runAs is not { Switches: true } agent) return $"Worker {id} does not start children as another user, so it removes nothing as the agent.";

            // The line that says this worker, not control, was asked.
            log?.LogInformation("Worker {Worker} removes {Count} path(s) inside {Boundary} as the agent.", id, targets.Count, remove.Boundary);

            foreach (var chunk in targets.Chunk(100))
            {
                await RunHomeRemoval.RunAsync([.. agent.Prefix, rm, "-rf", "--one-file-system", "--", .. chunk], ct);
            }
        }

        return refused.Count == 0 ? null : string.Join(" ", refused.Select(path => NotConfined(path, remove.Boundary)));
    }
}
