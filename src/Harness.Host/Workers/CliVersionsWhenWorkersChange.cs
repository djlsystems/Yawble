using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE CLI VERSION RECORD, MEASURED AGAIN WHEN THE WORKERS CHANGE: one request to the worker a run
/// placed now would go to, running each launched command's <c>--version</c> with its update-off - the
/// same run the platform's update measures with, and never the update itself. A line is appended to
/// <c>cli-versions.jsonl</c> only when a version differs from the newest line, marked
/// <c>measured</c>. A command the update gate holds is not asked. No worker, or none that answers,
/// writes nothing: the record keeps its last dated line, and a version is never blanked by a
/// measurement that did not happen.
/// </summary>
public static class CliVersionsWhenWorkersChange
{
    /// <summary>How long one <c>--version</c> may take.</summary>
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>What the line the measurement appends is marked by.</summary>
    public const string By = "measured";

    /// <summary>One command's version run: its update-off arguments and environment, then <c>--version</c>.</summary>
    public static AgentCliRun VersionRun(string command, AgentUpdates? updates) =>
        new(command, [.. updates?.Arguments ?? [], "--version"], updates?.Environment ?? new Dictionary<string, string>(), [],
            (int)VersionTimeout.TotalSeconds);

    /// <summary>Measures every launched command not held by <paramref name="gate"/>, and appends a line when one changed.</summary>
    public static async Task MeasureAsync(
        AgentCatalog catalog, WorkerAsks asks, string dataRoot, AgentUpdateGate? gate, ILogger? log = null, CancellationToken ct = default)
    {
        var turn = gate?.Turn ?? 0;
        var commands = MeasureInstallsWhenAWorkerJoins.Commands(catalog)
            .Where(command => gate?.Holding(command) is null)
            .ToList();
        if (commands.Count == 0) return;

        var runs = commands.Select(command => VersionRun(command, AgentUpdates.ForCommand(command, catalog.Definitions))).ToList();
        var asked = await asks.AskAsync<AgentCommandsRan>(
            new RunAgentCommands(WorkerAsks.NewRequest(), runs), VersionTimeout * runs.Count + TimeSpan.FromSeconds(15), ct);

        if (asked.Answer is not { } ran || ran.Results.Count != commands.Count)
        {
            log?.LogInformation("The CLI versions were not measured: {Why}", asked.Why ?? "the worker's answer did not match the request");
            return;
        }

        var history = CliVersionHistory.In(dataRoot);
        var newest = (await history.ReadAsync(1, ct)).FirstOrDefault();
        var changed = new Dictionary<string, string?>(StringComparer.Ordinal);

        for (var i = 0; i < commands.Count; i++)
        {
            // An answer from across an update's hold says nothing about the install it replaced.
            if (gate?.HeldSince(commands[i], turn) == true) continue;
            if (VersionOf(ran.Results[i]) is not { } version) continue;
            if (newest?.Versions.GetValueOrDefault(commands[i]) != version) changed[commands[i]] = version;
        }

        if (changed.Count > 0) await history.AppendAsync(changed, By, ct, worker: asked.Worker?.Value);
    }

    /// <summary>The first line a <c>--version</c> printed, or null when it is not installed or did not exit 0.</summary>
    public static string? VersionOf(AgentCliRunResult version) =>
        version is { Installed: true, ExitCode: 0 }
            ? version.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            : null;
}
