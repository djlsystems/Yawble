using System.Globalization;
using Harness.Containers;

namespace Harness.Host.Capacity;

/// <summary>
/// WHETHER THE INSTANCE HAS MEASURED ROOM FOR ANOTHER RUN, the question <c>WipLedger</c> asks after
/// the run limit says yes.
///
/// <para>
/// A run waits while memory in use (anonymous + shmem, <see cref="CgroupFigures.MemoryInUseBytes"/>)
/// is at or above <c>admission.memoryPercent</c> of the container's memory limit, or while memory
/// pressure's <c>some avg10</c> is at or above <c>admission.memoryPressurePercent</c>. Both settings
/// are read through delegates on every question, so a change applies without a restart; 0 turns a
/// check off.
/// </para>
///
/// <para>
/// <b>NOT MEASURED ADMITS.</b> A figure that is not measured (no cgroup, no limit, no pressure file,
/// no measurement yet, or a last measurement older than <see cref="StaleAfter"/>) holds nothing, and
/// admission falls back to the run limit alone. Nothing here estimates what it could not read.
/// </para>
///
/// <para>
/// Answers from the last measurement handed to <see cref="Update"/> and does no I/O, because the
/// ledger asks it under its lock.
/// </para>
/// </summary>
public sealed class HeadroomGate(
    Func<int> memoryPercent, Func<int> pressurePercent, TimeProvider? clock = null)
{
    /// <summary>A measurement older than this is not measured: a sampler that stopped must not hold work forever.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private volatile Measured? _last;
    private long _samples;

    public void Update(CgroupFigures figures, DateTimeOffset at)
    {
        _last = new Measured(figures, at);
        Interlocked.Increment(ref _samples);
    }

    /// <summary>
    /// How many measurements have been handed to <see cref="Update"/>: what tells one sample from the
    /// next, even when two carry the same time.
    /// </summary>
    public long Samples => Interlocked.Read(ref _samples);

    /// <summary>
    /// Bytes of memory the last measurement left under the limit, or null when the limit or the use
    /// is not measured or the measurement is stale: an unmeasured worker has no headroom figure,
    /// never 0 and never unlimited.
    /// </summary>
    public long? Headroom =>
        _last is { } last && _clock.GetUtcNow() - last.At <= StaleAfter
            && last.Figures.MemoryLimitBytes is > 0 and var limit && last.Figures.MemoryInUseBytes is { } used
            ? limit - used
            : null;

    /// <summary>When the last measurement was taken, or null before the first.</summary>
    public DateTimeOffset? MeasuredAt => _last?.At;

    /// <summary>The last measurement, or null before the first.</summary>
    public CgroupFigures? Figures => _last?.Figures;

    /// <summary>Null when a run may start; otherwise the sentence it waits with.</summary>
    public string? Reason()
    {
        if (_last is not { } last || _clock.GetUtcNow() - last.At > StaleAfter) return null;

        var figures = last.Figures;

        if (memoryPercent() is > 0 and var limitPercent
            && figures.MemoryInUseBytes is { } used
            && figures.MemoryLimitBytes is > 0 and var limit
            && used * 100.0 >= limit * (double)limitPercent)
        {
            return $"waiting for memory: {Gb(used)} of {Gb(limit)} GB in use";
        }

        if (pressurePercent() is > 0 and var pressureLimit
            && figures.MemoryPressure?.Some.Avg10 is { } avg10
            && avg10 >= pressureLimit)
        {
            return WipLedger.PressureReasonStart
                + avg10.ToString("0.#", CultureInfo.InvariantCulture) + "% of the last 10 s";
        }

        return null;
    }

    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture);

    private sealed record Measured(CgroupFigures Figures, DateTimeOffset At);
}
