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

    /// <summary>Each connection the install asks for, with its slot's plugin, the providers it takes
    /// and what it asks of each - from the package, so it is known before anything is installed.</summary>
    public IReadOnlyList<SolutionPlanConnection> PersonConnections { get; init; } = [];

    /// <summary>What the solution's control panel and launcher tile take from the package.</summary>
    public SolutionPanel Panel { get; init; } = SolutionPanel.None;

    /// <summary>The setup wizard's questions, or null when the package has no <c>setup</c> block.</summary>
    public SolutionPlanSetup? Setup { get; init; }

    /// <summary>Where an install copies <c>tools/</c>, as <c>{solution}</c> names it: under the
    /// team's folder, <c>&lt;teams root&gt;/&lt;team&gt;/solution</c>.</summary>
    public const string InstalledToolsFolder = "solution";

    /// <summary>How the plan starts a schedule the install runs once: "runs once now, then every …".</summary>
    public const string RunsOnceNow = "runs once now, then ";

    /// <param name="activeVersion">The instance's active version of a plugin by id, null when none is;
    /// null for a plan read with no instance, which then says only what the package ships.</param>
    public static SolutionPlan Of(SolutionPackage package, Func<string, string?>? activeVersion = null)
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
                [.. p.Manifest.Publishes.Select(e => EventCatalog.PluginType(p.Id, e.Type))])
            {
                Minimum = package.Minimum(p.Id),
                OnInstall = activeVersion is null ? null : OnInstall(p.Id, p.Manifest.Version, package.Minimum(p.Id)!, activeVersion(p.Id)),
                NamedSecrets = p.Manifest.NamedSecrets,
            })],
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
                return new SolutionPlanSetting(input.Member, input.Setting, input.Description, input.Required, field?.Type, field?.Default, field?.Enum)
                {
                    Min = field?.Min,
                    Max = field?.Max,
                    Integer = field?.Integer ?? false,
                };
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
            PersonConnections = [.. manifest.Inputs.Connections.Select(input =>
            {
                var plugin = package.Plugin(manifest.Member(input.Member)?.PluginId)?.Manifest;
                var slot = plugin?.Connections.GetValueOrDefault(input.Slot);
                return new SolutionPlanConnection(
                    input.Member, input.Slot, input.Description, input.Required, plugin?.Id,
                    slot?.Providers ?? [], slot?.Scopes ?? new Dictionary<string, IReadOnlyList<string>>());
            })],
            Panel = manifest.Panel,
            Setup = SetupOf(package),
        };
    }

    /// <summary>The wizard's screens: each kind's questions in <c>setup</c> order, then every input
    /// of that kind it asks nothing about, behind Advanced… with no question.</summary>
    /// <summary>
    /// What the install does with a shipped plugin against the instance's active version, in words:
    /// the active one is left when it is the same or newer (an install never downgrades what other
    /// teams run), and the shipped one is installed and made active when it is newer - saying so when
    /// the active one is older than this team needs.
    /// </summary>
    public static string OnInstall(string id, string shipped, string minimum, string? active)
    {
        if (active is null) return $"{id} is not installed here: {shipped} is installed and made active.";

        var compared = SolutionInstaller.CompareVersions(active, shipped);

        if (compared == 0) return $"{id} {active} is active here already and stays.";

        if (compared > 0)
        {
            return $"{id} {active} is active here, newer than the {shipped} this package ships, and stays active (this team needs {minimum} or newer).";
        }

        return SolutionInstaller.CompareVersions(minimum, active) > 0
            ? $"{id} {active} is active here, older than the {minimum} this team needs: {shipped} is installed and made active."
            : $"{id} {active} is active here: {shipped} is installed and made active (this team needs {minimum} or newer).";
    }

    private static SolutionPlanSetup? SetupOf(SolutionPackage package)
    {
        var manifest = package.Manifest;
        if (manifest.Setup is not { } setup) return null;

        static List<(TInput Input, SolutionSetupQuestion? Asked)> Placed<TInput>(
            IReadOnlyList<SolutionSetupQuestion> questions, IReadOnlyList<TInput> inputs, Func<SolutionSetupQuestion, TInput, bool> asks) =>
            [.. questions.Select(q => (inputs.First(i => asks(q, i)), (SolutionSetupQuestion?)q)),
             .. inputs.Where(i => !questions.Any(q => asks(q, i))).Select(i => (i, (SolutionSetupQuestion?)null))];

        PluginManifest? PluginOf(string member) => package.Plugin(manifest.Member(member)?.PluginId)?.Manifest;

        return new SolutionPlanSetup(
            setup.Welcome,
            setup.Needs,
            [.. Placed(setup.Connections, manifest.Inputs.Connections, (q, i) => q.Member == i.Member && q.Target == i.Slot).Select(p =>
            {
                var plugin = PluginOf(p.Input.Member);
                var slot = plugin?.Connections.GetValueOrDefault(p.Input.Slot);
                return new SolutionPlanSetupConnection(
                    p.Input.Member, p.Input.Slot, p.Asked?.Question, p.Asked?.Hint, p.Asked?.Advanced ?? true, p.Input.Required, p.Input.Description,
                    plugin?.Id, slot?.Providers ?? [], slot?.Scopes ?? new Dictionary<string, IReadOnlyList<string>>());
            })],
            [.. Placed(setup.Documents, manifest.Inputs.Documents, (q, i) => q.Target == i.Folder).Select(p =>
                new SolutionPlanSetupDocument(
                    p.Input.Folder, p.Asked?.Question, p.Asked?.Hint, p.Asked?.Advanced ?? true, p.Input.Required, p.Input.Description))],
            [.. Placed(setup.Settings, manifest.Inputs.Settings, (q, i) => q.Member == i.Member && q.Target == i.Setting).Select(p =>
            {
                var field = PluginOf(p.Input.Member)?.Config.GetValueOrDefault(p.Input.Setting);
                return new SolutionPlanSetupSetting(
                    p.Input.Member, p.Input.Setting, p.Asked?.Question, p.Asked?.Hint, p.Asked?.Advanced ?? true, p.Input.Required, p.Input.Description,
                    p.Asked?.Default ?? field?.Default, field?.Type, field?.Enum, field?.Min, field?.Max, field?.Integer ?? false);
            })]);
    }
}

public sealed record SolutionPlanPackage(string Id, string Name, string Version, string Description, string Folder, bool Readme);

public sealed record SolutionPlanMember(
    string Name, string Kind, string Role, string? Preset, string Instructions, string? PluginId, string? PluginVersion,
    IReadOnlyDictionary<string, JsonElement> Settings);

public sealed record SolutionPlanPlugin(string Id, string Name, string Version, string Description, string Folder, IReadOnlyList<string> Events)
{
    /// <summary>The oldest version of it the team needs: <c>pluginMinimums</c>, else <see cref="Version"/>.</summary>
    public string? Minimum { get; init; }

    /// <summary>What the install does with it on this instance (<see cref="SolutionPlan.OnInstall"/>);
    /// null when the plan was read with no instance.</summary>
    public string? OnInstall { get; init; }

    /// <summary>Its manifest's <c>namedSecrets</c> block (label, description, max), null when it
    /// declares none: the wizard and Change setup show the named-secrets table from it.</summary>
    public PluginNamedSecrets? NamedSecrets { get; init; }
}

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
    string Member, string Setting, string Description, bool Required, string? Type, JsonElement? Default, IReadOnlyList<string>? Choices)
{
    /// <summary>The plugin field's bounds, when it is a number that declares them.</summary>
    public double? Min { get; init; }

    public double? Max { get; init; }

    public bool Integer { get; init; }
}

/// <summary>
/// A secret a plugin member binds: the member (the package's name), the plugin's secret
/// <paramref name="Field"/>, the logical <paramref name="Key"/> it is bound to, and what the
/// manifest says of it. <paramref name="When"/> is the setting and value it is needed for, or null
/// when always needed.
/// </summary>
public sealed record SolutionPlanSecret(
    string Member, string Field, string Key, string Description, bool Required, SolutionPlanSecretWhen? When);

public sealed record SolutionPlanSecretWhen(string Setting, string Value);

/// <summary>A connection slot the install asks a person to bind: <paramref name="Scopes"/> keyed by
/// provider, as the plugin's manifest declares them. Never a connection or a token.</summary>
public sealed record SolutionPlanConnection(
    string Member, string Slot, string Description, bool Required, string? Plugin,
    IReadOnlyList<string> Providers, IReadOnlyDictionary<string, IReadOnlyList<string>> Scopes);

/// <summary>
/// The package's <c>setup</c> block as the setup wizard asks it: the welcome, what the person will
/// need, and EVERY input of the package placed on its screen - the package's questions first, in
/// <c>setup</c> order, then each input it asks no question for, behind Advanced… with a null
/// question. Null on the plan when the package has no <c>setup</c>: the web shows today's wizard.
/// </summary>
public sealed record SolutionPlanSetup(
    string Welcome,
    string? Needs,
    IReadOnlyList<SolutionPlanSetupConnection> Connections,
    IReadOnlyList<SolutionPlanSetupDocument> Documents,
    IReadOnlyList<SolutionPlanSetupSetting> Settings);

/// <summary>A connection input on the mailbox screen, with the providers its slot accepts.</summary>
public sealed record SolutionPlanSetupConnection(
    string Member, string Slot, string? Question, string? Hint, bool Advanced, bool Required, string Description,
    string? Plugin, IReadOnlyList<string> Providers, IReadOnlyDictionary<string, IReadOnlyList<string>> Scopes);

/// <summary>A document input on the documents screen.</summary>
public sealed record SolutionPlanSetupDocument(
    string Folder, string? Question, string? Hint, bool Advanced, bool Required, string Description);

/// <summary>A setting input on the preferences screen: the package's default, else its plugin's,
/// and what the plugin's manifest says of the field.</summary>
public sealed record SolutionPlanSetupSetting(
    string Member, string Setting, string? Question, string? Hint, bool Advanced, bool Required, string Description,
    JsonElement? Default, string? Type, IReadOnlyList<string>? Choices, double? Min, double? Max, bool Integer);
