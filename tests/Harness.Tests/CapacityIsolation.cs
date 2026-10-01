using System.Runtime.CompilerServices;

namespace Harness.Tests;

/// <summary>
/// A test Host measures no cgroup. Left alone, every test Host would admit runs by the memory of the
/// machine the suite runs on, and a suite run on a busy instance would hold its own runs "waiting for
/// memory" until its tests timed out. Pointed at a folder with no cgroup files, the Host's figures are
/// not measured and admission falls back to the run limit, as it must. A test that exercises the gate
/// builds its own reader over a fixture tree.
/// </summary>
internal static class CapacityIsolation
{
    public static readonly string NoCgroup =
        Path.Combine(Path.GetTempPath(), "harness-tests-no-cgroup");

    [ModuleInitializer]
    internal static void Isolate() =>
        Environment.SetEnvironmentVariable("Capacity__CgroupRoot", NoCgroup);
}
