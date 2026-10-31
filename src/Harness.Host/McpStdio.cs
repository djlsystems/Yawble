using System.Text.Json.Nodes;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE HOST TALKING TO AN MCP SERVER THE PLATFORM RUNS (MCP 3), through a worker's connection: the worker starts it as
/// the <c>mcp</c> user and keeps it (<c>McpStdioServers</c>), and its pipes stay the worker's. A member's list and calls
/// go to the worker the member's run is placed on - so every caller on a worker shares that worker's one process - or,
/// outside a placement, to the worker a run would go to now. A check goes to any worker. Answers come back as
/// <see cref="McpForwarded"/>, as a web address server's do; a failure sentence already names the server.
/// </summary>
public sealed class McpStdio(
    WorkerAsks asks, Func<ContainerId, IRunWorker?>? workerOf = null, Func<IReadOnlyList<IRunWorker>>? all = null,
    TimeSpan? callTimeout = null, TimeSpan? startTimeout = null)
{
    /// <summary>How long a call may take once the process has answered initialize.</summary>
    public TimeSpan CallTimeout { get; } = callTimeout ?? McpForwarding.DefaultCallTimeout;

    /// <summary>How long a start - the package's install among it - may take before initialize is answered.</summary>
    public TimeSpan StartTimeout { get; } = startTimeout ?? TimeSpan.FromSeconds(McpStdioRequest.DefaultStartSeconds);

    /// <summary>What crossing to the worker and back may add to a request's own bounds.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(15);

    /// <summary>The server's tools for <paramref name="member"/>'s run, within <paramref name="bound"/>: a first start that
    /// takes longer goes on on the worker, and a later run lists it.</summary>
    public async Task<(IReadOnlyList<JsonObject>? Tools, string? Failure)> ListAsync(
        ContainerId member, McpStdioLaunch launch, TimeSpan bound, CancellationToken ct)
    {
        var request = new McpStdioRequest(WorkerAsks.NewRequest(), launch, McpStdioRequest.List, CallSeconds: Seconds(CallTimeout), StartSeconds: Seconds(StartTimeout));
        var answer = await AskAsync(Worker(member), request, bound, ct);
        if (answer.Result?["tools"] is JsonArray tools) return ([.. tools.OfType<JsonObject>()], null);

        return (null, answer.Failure ?? (answer.NoAnswer ? $"{launch.Name} did not answer." : $"{launch.Name} would not list its tools: {answer.Error}"));
    }

    /// <summary>One <c>tools/call</c> for <paramref name="member"/>, on the worker its run is on.</summary>
    public Task<McpForwarded> CallAsync(ContainerId member, McpStdioLaunch launch, string tool, JsonObject? arguments, CancellationToken ct)
    {
        var request = new McpStdioRequest(
            WorkerAsks.NewRequest(), launch, McpStdioRequest.Call, tool, arguments, Seconds(CallTimeout), Seconds(StartTimeout));
        return AskAsync(Worker(member), request, StartTimeout + CallTimeout + Slack, ct);
    }

    /// <summary>The entry's check (MCP 1): a process of its own on a worker, its tools listed, then stopped.</summary>
    public async Task<(IReadOnlyList<string>? Tools, string? Failure, bool Ran)> CheckAsync(McpStdioLaunch launch, CancellationToken ct)
    {
        var request = new McpStdioRequest(
            WorkerAsks.NewRequest(), launch, McpStdioRequest.Check, CallSeconds: Seconds(CallTimeout), StartSeconds: Seconds(StartTimeout));
        var asked = await asks.AskAsync<McpStdioAnswered>(request, StartTimeout + CallTimeout + Slack, ct);
        if (asked.Answer is not { } answer) return (null, asked.Why, false);

        if (answer.Result?["tools"] is JsonArray tools)
        {
            return ([.. tools.OfType<JsonObject>().Select(t => t["name"] is JsonValue n && n.TryGetValue<string>(out var name) ? name : null).OfType<string>().Where(n => n.Length > 0)], null, true);
        }

        return (null, answer.Failure ?? (answer.NoAnswer ? $"{launch.Name} did not answer." : $"{launch.Name} would not list its tools: {answer.Error}"), true);
    }

    /// <summary>
    /// Stops <paramref name="server"/>'s process on every connected worker: its entry was edited or removed, or a
    /// connection it reads changed. The next call starts it with what the entry holds then. Best effort: a worker that
    /// does not answer still meets the new values, because a call carrying another fingerprint restarts the process.
    /// </summary>
    public async Task StopEverywhereAsync(string server, CancellationToken ct = default)
    {
        IReadOnlyList<IRunWorker> workers = all?.Invoke() ?? (asks.Pick() is { } one ? [one] : []);
        await Task.WhenAll(workers.Select(worker =>
            asks.AskAsync<McpStdioAnswered>(worker, new McpStdioStop(WorkerAsks.NewRequest(), server), TimeSpan.FromSeconds(10), ct)));
    }

    /// <summary>A connection changed: every entry the platform runs that reads it is stopped everywhere.</summary>
    public async Task ConnectionChangedAsync(IMcpServerStore store, string connection, CancellationToken ct = default)
    {
        foreach (var server in (await store.ListAsync(ct)).Where(s => s.Kind == McpServerRecord.Runs && s.Env.Any(e => e.Connection == connection)))
        {
            await StopEverywhereAsync(server.Id, ct);
        }
    }

    private IRunWorker? Worker(ContainerId member)
    {
        try
        {
            return workerOf?.Invoke(member) ?? asks.Pick();
        }
        catch (InvalidOperationException)
        {
            return asks.Pick();
        }
    }

    private async Task<McpForwarded> AskAsync(IRunWorker? worker, McpStdioRequest request, TimeSpan bound, CancellationToken ct)
    {
        if (worker is null) return McpForwarded.Failed($"{request.Launch.Name} was not started: {WorkerAsks.NoWorker}");

        var asked = await asks.AskAsync<McpStdioAnswered>(worker, request, bound, ct);
        if (asked.Answer is not { } answer)
        {
            return asked.Why is { } why && why.EndsWith("did not answer.", StringComparison.Ordinal)
                ? McpForwarded.Silent
                : McpForwarded.Failed($"{request.Launch.Name} was not reached: {asked.Why}");
        }

        if (answer.Result is { } result) return McpForwarded.Answered(result);
        if (answer.Error is { } error) return McpForwarded.Refused(error);
        if (answer.NoAnswer) return McpForwarded.Silent;
        return McpForwarded.Failed(answer.Failure ?? $"{request.Launch.Name} answered nothing.");
    }

    private static int Seconds(TimeSpan span) => (int)Math.Max(1, Math.Ceiling(span.TotalSeconds));
}
