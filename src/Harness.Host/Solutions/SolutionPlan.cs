using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host.Solutions;

/// <summary>
/// WHAT AN INSTALL WOULD CREATE, in the words a person reviews it in: every member, every trigger
/// with its FULL instruction text and its daily cap, every skill with its body, every site, the tools
/// and what the person is asked for. Agent-written instructions become prompts, so the review shows
/// them whole, never summarised. The web review, the CLI and the board notice all render this.
/// </summary>
public sealed record SolutionPlan(
    SolutionPlanPackage Package,
    SolutionTeam Team,
    IReadOnlyList<SolutionPlanMember> Members,
    IReadOnlyList<SolutionPlanPlugin> Plugins,
    IReadOnlyList<SolutionPlanTrigger> Triggers,
    IReadOnlyList<SolutionPlanSkill> Skills,
    IReadOnlyList<SolutionPlanSite> Sites,
    SolutionPlanTools? Tools,
    SolutionInputs Inputs,
    IReadOnlyList<SolutionPlanSetting> PersonSettings,
    IReadOnlyList<string> Ignored)
{
    /// <summary>Where an install copies <c>tools/</c>, as <c>{solution}</c> names it: under the
    /// team's folder, <c>&lt;teams root&gt;/&lt;team&gt;/solution</c>.</summary>
    public const string InstalledToolsFolder = "solution";

    public static SolutionPlan Of(SolutionPackage package)
    {
        var manifest = package.Manifest;

        return new SolutionPlan(
            new SolutionPlanPackage(manifest.Id, manifest.Name, manifest.Version, manifest.Description, package.Folder, package.HasReadme),
            manifest.Team,
            [.. manifest.Members.Select(m => new SolutionPlanMember(
                m.Name, m.Kind, m.Role, m.Preset, m.Instructions, m.PluginId,
                m.PluginId is null ? null : package.Plugin(m.PluginId)?.Manifest.Version,
                m.Settings))],
            [.. package.Plugins.Select(p => new SolutionPlanPlugin(
                p.Id, p.Manifest.Name, p.Manifest.Version, p.Manifest.Description,
                $"{SolutionChecker.PluginsFolder}/{p.Id}",
                [.. p.Manifest.Publishes.Select(e => EventCatalog.PluginType(p.Id, e.Type))]))],
            [.. manifest.Triggers.Select(t => new SolutionPlanTrigger(
                t.Name, t.Kind, t.PlatformKind.ToString(), t.Member, t.Instruction, t.WakeManager, t.DailyTokenCap, t.IdleOnly,
                t.Schedule?.ToString(), t.Schedule?.Cron, t.Schedule?.Timezone, t.Schedule?.EverySeconds,
                t.Event?.Type, t.Event?.Filter, t.Folder?.Path, t.Folder?.Glob))],
            [.. package.Skills.Select(s => new SolutionPlanSkill(s.Name, s.Description, s.Roles, s.File, s.Body))],
            [.. package.Sites.Select(s => new SolutionPlanSite(s.Name, $"{SolutionChecker.SitesFolder}/{s.Name}", s.Files))],
            package.ToolsFolder is null ? null : new SolutionPlanTools(SolutionChecker.ToolsFolderName, InstalledToolsFolder, package.Tools),
            manifest.Inputs,
            [.. manifest.Inputs.Settings.Select(input =>
            {
                var field = package.Plugin(manifest.Member(input.Member)?.PluginId)?.Manifest.Config.GetValueOrDefault(input.Setting);
                return new SolutionPlanSetting(input.Member, input.Setting, input.Description, input.Required, field?.Type, field?.Default, field?.Enum);
            })],
            manifest.Ignored);
    }
}

public sealed record SolutionPlanPackage(string Id, string Name, string Version, string Description, string Folder, bool Readme);

public sealed record SolutionPlanMember(
    string Name, string Kind, string Role, string? Preset, string Instructions, string? PluginId, string? PluginVersion,
    IReadOnlyDictionary<string, JsonElement> Settings);

public sealed record SolutionPlanPlugin(string Id, string Name, string Version, string Description, string Folder, IReadOnlyList<string> Events);

/// <param name="Kind">The package's word: schedule, event or folder.</param>
/// <param name="PlatformKind">What the Triggers dialog will show: Cron, Every, Event or FolderChange.</param>
/// <param name="Instruction">The whole instruction, verbatim; <c>{event.*}</c> resolves at each fire
/// and <c>{solution}</c> to the installed tools folder.</param>
public sealed record SolutionPlanTrigger(
    string Name, string Kind, string PlatformKind, string Member, string Instruction, string WakeManager, long? DailyTokenCap,
    bool IdleOnly, string? Schedule, string? Cron, string? Timezone, int? EverySeconds, string? EventType, string? Filter,
    string? FolderPath, string? FolderGlob);

public sealed record SolutionPlanSkill(string Name, string Description, IReadOnlyList<string> Roles, string File, string Body);

public sealed record SolutionPlanSite(string Name, string Folder, IReadOnlyList<string> Files);

/// <param name="Folder">In the package.</param>
/// <param name="InstalledAs">Under the team's folder once installed.</param>
public sealed record SolutionPlanTools(string Folder, string InstalledAs, IReadOnlyList<string> Files);

/// <summary>A person-only setting the install asks for, with what its manifest says of it.</summary>
public sealed record SolutionPlanSetting(
    string Member, string Setting, string Description, bool Required, string? Type, JsonElement? Default, IReadOnlyList<string>? Choices);
