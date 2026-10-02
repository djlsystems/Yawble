namespace Harness.Host;

/// <summary>
/// The arithmetic and the words of a run's raised memory figures, over one container's limit: the
/// heavy allowance and the ceiling. Pure, so control computes them over its own container and a
/// worker over its own, in the same words.
/// </summary>
public static class RunMemoryRules
{
    /// <summary>
    /// THE HEAVY ALLOWANCE: the container's limit less <paramref name="reserveMb"/> less what the
    /// other running runs are measured to use now, never below the run's own limit
    /// (<paramref name="normal"/>). With no container limit, the run keeps its own.
    /// </summary>
    public static RunMemoryLimit Heavy(RunMemoryLimit normal, long? containerLimitMb, long reserveMb, long othersMb, int others)
    {
        if (containerLimitMb is not { } containerMb)
        {
            return normal with
            {
                Source = $"the container has no memory limit to measure headroom against, so a run holding the heavy lease keeps its own limit ({normal.Source})",
            };
        }

        var headroom = containerMb - reserveMb - othersMb;
        var said = $"the run holds the heavy lease, so {containerMb} MB container limit - {reserveMb} MB for the Host - "
            + $"{othersMb} MB measured in use by {others} other running run{(others == 1 ? "" : "s")}";

        return normal.Mb is { } own && headroom <= own
            ? new RunMemoryLimit(own, $"{said} leaves no more than its own limit, so it keeps {own} MB ({normal.Source})")
            : new RunMemoryLimit(headroom, said);
    }

    /// <summary>
    /// The most any run could be raised to: the container's limit less <paramref name="reserveMb"/>,
    /// never below the run's own limit; the run's own when the container has none.
    /// </summary>
    public static RunMemoryLimit Ceiling(RunMemoryLimit normal, long? containerLimitMb, long reserveMb)
    {
        if (containerLimitMb is not { } containerMb || normal.Mb is not { } own || containerMb - reserveMb <= own)
        {
            return normal;
        }

        return new RunMemoryLimit(containerMb - reserveMb,
            $"{containerMb} MB container limit - {reserveMb} MB for the Host, the most a run holding the heavy lease can be given");
    }
}
