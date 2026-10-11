namespace Harness.Contracts;

/// <summary>
/// The names of the tenant settings a runtime worker quotes in what it says. The settings
/// themselves stay in control; only a name a worker's sentence names lives here.
/// </summary>
public static class SettingNames
{
    public const string RunsMemoryLimitMb = "runs.memoryLimitMb";
}
