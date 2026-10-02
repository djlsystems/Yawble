namespace Harness.Host;

/// <summary>
/// One run's memory limit in megabytes (null: none), and the sentence saying where it came from.
/// <paramref name="Set"/> is true only when a person set the figure (<c>runs.memoryLimitMb</c> &gt; 0):
/// a per-process rlimit is applied only then, never from a computed figure (<see cref="RunMemoryLimits"/>).
/// </summary>
public sealed record RunMemoryLimit(long? Mb, string Source, bool Set = false);
