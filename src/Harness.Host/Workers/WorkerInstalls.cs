using Harness.Contracts;

namespace Harness.Host;

/// <summary>One worker's answer to whether a command is on its PATH, and when it gave it.</summary>
public sealed record InstallMeasurement(string Worker, bool Installed, DateTimeOffset At);

/// <summary>
/// WHETHER AN AGENT CLI IS INSTALLED, AS THE WORKERS MEASURED IT. In <c>control</c> the agent CLIs
/// are on the workers and never on control's own PATH, so the only honest answer is a worker's: each
/// worker's newest answer per command, from the sign-in probe and from the measurement taken as it
/// joins. Only the workers a run may be placed on now count (<paramref name="placeable"/>): a dropped
/// or draining worker's answer says nothing about where the next run goes, and counts again when it
/// is back. Nothing is aged out by a timer, and nothing is estimated: a command no counted worker
/// answered for is not measured.
/// </summary>
/// <param name="placeable">The workers a run may be placed on now (<see cref="WorkerPool.Workers"/>).</param>
/// <param name="joined">How many workers are joined at all, dropped and draining ones included: what
/// tells "no worker is connected" from "none can take runs". Null: the same as <paramref name="placeable"/>.</param>
public sealed class WorkerInstalls(Func<IReadOnlyList<WorkerId>> placeable, Func<int>? joined = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<WorkerId, Dictionary<string, (bool Installed, DateTimeOffset At)>> _answers = [];

    /// <summary>Over a real pool: its placeable workers count, and every joined one tells a draining pool from an empty one.</summary>
    public static WorkerInstalls Over(WorkerPool pool, TimeProvider? clock = null) =>
        new(() => pool.Workers, () => pool.Entries().Count, clock);

    /// <summary>What <paramref name="worker"/> answered for each command, each replacing that worker's older answer.</summary>
    public void Record(WorkerId worker, IEnumerable<(string Command, bool Installed)> answers)
    {
        var at = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_answers.TryGetValue(worker, out var commands))
            {
                _answers[worker] = commands = new(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var (command, installed) in answers) commands[command] = (installed, at);
        }
    }

    /// <summary>The newest answer for <paramref name="command"/> from each worker that counts now, by worker id.</summary>
    public IReadOnlyList<InstallMeasurement> For(string command)
    {
        var counted = placeable();
        lock (_gate)
        {
            return [.. counted
                .Distinct()
                .Where(worker => _answers.TryGetValue(worker, out var commands) && commands.ContainsKey(command))
                .Select(worker => (worker, _answers[worker][command]))
                .OrderBy(found => found.worker.Value, StringComparer.Ordinal)
                .Select(found => new InstallMeasurement(found.worker.Value, found.Item2.Installed, found.Item2.At))];
        }
    }

    /// <summary>Why nothing is measured for a command, as the end of a sentence about it.</summary>
    public string NotMeasuredBecause() =>
        placeable().Count > 0 ? NoAnswerYet
        : (joined?.Invoke() ?? 0) > 0 ? NoneAvailable
        : NoneConnected;

    /// <summary>Not measured, with no worker joined.</summary>
    public const string NoneConnected = "no worker is connected";

    /// <summary>Not measured, with workers joined but none a run may be placed on (dropped or draining).</summary>
    public const string NoneAvailable = "no worker is available to run agents";

    /// <summary>Not measured, with a worker that counts but has not answered for the command.</summary>
    public const string NoAnswerYet = "no worker has reported whether it is installed";
}
