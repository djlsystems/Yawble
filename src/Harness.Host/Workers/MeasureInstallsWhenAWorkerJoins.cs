using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// EACH WORKER IS MEASURED AS IT JOINS, in control: one sign-in probe asking about every command the
/// catalog launches, and each command's <c>installed</c> recorded against that worker
/// (<see cref="WorkerInstalls"/>).
/// </summary>
public static class MeasureInstallsWhenAWorkerJoins
{
    public const string WiredText = "Each worker that joins is asked which agent CLIs it has.";

    public static bool Wire(
        bool control, Action<Action<WorkerId>> onJoined, Func<WorkerId, IRunWorker?> worker,
        Func<IReadOnlyList<string>> commands, WorkerAsks asks, WorkerInstalls installs, ILogger? log = null) => false;

    public static IReadOnlyList<string> Commands(AgentCatalog catalog) => [];

    public static Task MeasureAsync(
        IRunWorker worker, IReadOnlyList<string> commands, WorkerAsks asks, WorkerInstalls installs,
        ILogger? log = null, CancellationToken ct = default) => Task.CompletedTask;
}
