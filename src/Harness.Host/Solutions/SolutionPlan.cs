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
    /// <summary>Every secret a plugin member binds, by KEY NAME - never a value. Whether the Host
    /// has each set is the Host's, not the package's: the preview and the result add it.</summary>
    public IReadOnlyList<SolutionPlanSecret> Secrets { get; init; } = [];

    /// <summary>What the solution's control panel and launcher tile take from the package.</summary>
    public SolutionPanel Panel { get; init; } = SolutionPanel.None;

    /// <summary>Where an install copies <c>tools/</c>, as <c>{solution}</c> names it: under the
    /// team's folder, <c>&lt;teams root&gt;/&lt;team&gt;/solution</c>.</summary>
    public const string InstalledToolsFolder = "solution";

    /// <summary>How the plan starts a schedule the install runs once: "runs once now, then every …".</summary>
    public const string RunsOnceNow = "runs once now, then ";

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
                t.Schedule is null ? null : t.RunAtInstall ? $"{RunsOnceNow}{t.Schedule}" : t.Schedule.ToString(),
                t.Schedule?.Cron, t.Schedule?.Timezone, t.Schedule?.EverySeconds,
                t.Event?.Type, t.Event?.Filter, t.Folder?.Path, t.Folder?.Glob)
            {
                RunAtInstall = t.RunAtInstall,
            })],
            [.. package.Skills.Select(s => new SolutionPlanSkill(s.Name, s.Description, s.Roles, s.File, s.Body))],
            [.. package.Sites.Select(s => new SolutionPlanSite(s.Name, $"{SolutionChecker.SitesFolder}/{s.Name}", s.Files))],
            package.ToolsFolder is null ? null : new SolutionPlanTools(SolutionChecker.ToolsFolderName, InstalledToolsFolder, package.Tools),
            manifest.Inputs,
            [.. manifest.Inputs.Settings.Select(input =>
            {
                var field = package.Plugin(manifest.Member(input.Member)?.PluginId)?.Manifest.Config.GetValueOrDefault(input.Setting);
                return new SolutionPlanSetting(input.Member, input.Setting, input.Description, input.Required, field?.Type, field?.Default, field?.Enum);
            })],
            manifest.Ignored)
        {
            Secrets = [.. manifest.Members.SelectMany(m => m.Secrets.Select(binding =>
            {
                var declared = package.Plugin(m.PluginId)?.Manifest.Secrets.GetValueOrDefault(binding.Key);
                return new SolutionPlanSecret(
                    m.Name, binding.Key, binding.Value, declared?.Description ?? "", declared?.Required ?? false,
                    declared?.When is { } when ? new SolutionPlanSecretWhen(when.Setting, when.Value) : null);
            }))],
            Panel = manifest.Panel,
        };
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
    string? FolderPath, string? FolderGlob)
{
    /// <summary>The schedule fires once right after the install's last step, then on its interval;
    /// <c>Schedule</c> then reads "runs once now, then every …".</summary>
    public bool RunAtInstall { get; init; }
}

public sealed record SolutionPlanSkill(string Name, string Description, IReadOnlyList<string> Roles, string File, string Body);

public sealed record SolutionPlanSite(string Name, string Folder, IReadOnlyList<string> Files);

/// <param name="Folder">In the package.</param>
/// <param name="InstalledAs">Under the team's folder once installed.</param>
public sealed record SolutionPlanTools(string Folder, string InstalledAs, IReadOnlyList<string> Files);

/// <summary>A person-only setting the install asks for, with what its manifest says of it.</summary>
public sealed record SolutionPlanSetting(
    string Member, string Setting, string Description, bool Required, string? Type, JsonElement? Default, IReadOnlyList<string>? Choices);

/// <summary>
/// A secret a plugin member binds: the member (the package's name), the plugin's secret
/// <paramref name="Field"/>, the logical <paramref name="Key"/> it is bound to, and what the
/// manifest says of it. <paramref name="When"/> is the setting and value it is needed for, or null
/// when always needed.
/// </summary>
public sealed record SolutionPlanSecret(
    string Member, string Field, string Key, string Description, bool Required, SolutionPlanSecretWhen? When);

public sealed record SolutionPlanSecretWhen(string Setting, string Value);
