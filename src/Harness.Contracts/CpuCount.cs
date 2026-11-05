using System.Globalization;

namespace Harness.Contracts;

/// <summary>
/// The CPU count a container's running bound is computed from, and where it was measured. The
/// container's cgroup (<c>cpu.max</c>) comes first; when it does not say (no cpu controller, as on a
/// rootless Podman machine under cgroupfs, or no quota set), the engine's own CPU figure the CLI hands
/// in as <see cref="EngineVariable"/>, and then the processors the process itself sees. Nothing is
/// estimated: when none of the three was measured the count is null, and the caller says so.
/// </summary>
public static class CpuCount
{
    /// <summary>The engine's CPU figure, as the CLI measured it (<c>podman machine inspect</c>,
    /// <c>docker info</c>'s NCPU, or the computer's processors for a local engine).</summary>
    public const string EngineVariable = "HARNESS_ENGINE_CPUS";

    /// <summary>The count is the container's cgroup's: <c>cpu.max</c>'s quota over its period.</summary>
    public const string Cgroup = "cgroup";

    /// <summary>The count is the engine's, handed in by the CLI as <see cref="EngineVariable"/>.</summary>
    public const string Engine = "engine";

    /// <summary>The count is the processors the process sees (<see cref="Environment.ProcessorCount"/>).</summary>
    public const string Processors = "processors";

    /// <summary>
    /// The first of the cgroup's CPUs, the engine's figure and the processors seen that was measured
    /// (above 0), with its source; (null, null) when none was.
    /// </summary>
    public static (double? Cpus, string? Source) Measure(double? cgroupCpus, string? engineCpus, int? processors)
    {
        if (cgroupCpus is > 0) return (cgroupCpus, Cgroup);
        if (EngineFigure(engineCpus) is { } engine) return (engine, Engine);
        if (processors is > 0) return (processors, Processors);
        return (null, null);
    }

    /// <summary><see cref="EngineVariable"/>'s value as a CPU count; null when unset or not a whole number above 0.</summary>
    public static int? EngineFigure(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var cpus) && cpus > 0 ? cpus : null;
}
