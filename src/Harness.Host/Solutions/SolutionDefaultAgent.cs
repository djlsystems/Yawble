namespace Harness.Host.Solutions;

/// <summary>
/// THE AGENT a package's agent members run when it names none: the first headless model preset
/// known to be installed where runs go (<see cref="AgentInstallProbe.Installed"/>: in control, a
/// worker measured it; not measured is not installed), else the first headless model preset, else
/// the first headless preset. The wizard may name one.
/// </summary>
public static class SolutionDefaultAgent
{
    public static string? Of(AgentCatalog agents, AgentInstallProbe probe)
    {
        var headless = agents.Definitions.Where(d => d.Mode == AgentMode.Headless).ToList();
        return (headless.FirstOrDefault(d => d.Launch.LanguageModel && probe.Installed(d))
            ?? headless.FirstOrDefault(d => d.Launch.LanguageModel)
            ?? headless.FirstOrDefault())?.Name;
    }
}
