using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;
using Harness.Skills;

namespace Harness.Host.Solutions;

/// <summary>Who installs: a person (their user id and email), or the operator's CLI (neither).
/// Recorded on every tenant row the install writes.</summary>
public sealed record SolutionActor(string? UserId, string? Email)
{
    public static readonly SolutionActor Operator = new(null, null);

    /// <summary>What the rows and the site versions say did it.</summary>
    public string Label => Email ?? UserId ?? "operator";

    internal SiteActor Site => UserId is { } id && Email is { } email ? SiteActor.Person(id, email) : SiteActor.Unbound(Label, Email);

    internal SystemPromptSetter PromptSetter => new(Label, SystemPromptSetter.Person, DateTimeOffset.UtcNow);

    internal TriggerAudit Row(string action, string? subject, string? subjectName, object? detail) =>
        new(UserId, Email, action, subject, subjectName, detail is null ? null : JsonSerializer.Serialize(detail));
}

/// <summary>What the person provides at install: the answers to the package's <c>inputs</c>.</summary>
/// <param name="Settings">Package member name to its person-only settings.</param>
/// <param name="Connections">Package member name to slot to connection id.</param>
/// <param name="Documents">An input folder to files already inside the instance (the operator CLI
/// stages them); copied into the team's documents once the install has succeeded. The web uploads
/// through the documents route instead.</param>
public sealed record SolutionAnswers(
    IReadOnlyDictionary<string, Dictionary<string, JsonElement>>? Settings = null,
    IReadOnlyDictionary<string, Dictionary<string, string>>? Connections = null,
    IReadOnlyDictionary<string, List<string>>? Documents = null);

/// <summary>A new team from a package.</summary>
public sealed record SolutionInstallRequest(
    string? Folder, string? TeamName = null, string? Agent = null, bool? LocalRepository = null,
    SolutionAnswers? Answers = null);

/// <summary>A team moved to a newer version of the package it came from.</summary>
public sealed record SolutionUpdateRequest(string? Folder, string? Team, SolutionAnswers? Answers = null);

/// <summary>One install step as the wizard and the CLI name it.</summary>
public sealed record SolutionStep(string Step, int Number, string Title, bool Done);

/// <summary>An input a person skipped that the package needs: the team shows as blocked on it.</summary>
public sealed record SolutionMissing(string Kind, string Name, string? Member, string Description);

/// <summary>Added, changed and removed names in one section of an update.</summary>
public sealed record SolutionDiffSection(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed)
{
    public bool Any => Added.Count + Changed.Count + Removed.Count > 0;
}

/// <summary>What an update changes, section by section, by name.</summary>
public sealed record SolutionDiff(
    SolutionDiffSection Members,
    SolutionDiffSection Triggers,
    SolutionDiffSection Skills,
    SolutionDiffSection Sites,
    SolutionDiffSection Tools,
    SolutionDiffSection Plugins);

/// <summary>
/// The outcome of an install or update. <see cref="Status"/> is the HTTP status the route answers
/// with <see cref="Body"/>: 200 for a done install and for a step that failed (the body says which,
/// and that what was made is undone), 400 for a folder refused, 409 for a team name taken.
/// </summary>
public sealed record SolutionOutcome(int Status, object Body)
{
    public bool Ok => Status == 200 && Body is SolutionDone;
}

public sealed record SolutionDone(
    bool Ok, string Team, string TeamName, string Id, string Version, string? From,
    SolutionDiff? Diff, IReadOnlyList<SolutionMissing> Missing, IReadOnlyList<SolutionStep> Steps)
{
    /// <summary>Every secret the team's plugin members bind, by key name, with whether the Host has
    /// it set and whether it is needed.</summary>
    public IReadOnlyList<SolutionSecret> Secrets { get; init; } = [];

    /// <summary>The keys still to set: bound, needed and not set on this Host. Not a failure - each
    /// one's source fails until it is set.</summary>
    public IReadOnlyList<string> Unset => [.. Secrets.Where(s => s.Needed != false && !s.Set).Select(s => s.Key).Distinct(StringComparer.Ordinal)];

    /// <summary>Each schedule's first run: ran now at install, or when it first comes due.</summary>
    public IReadOnlyList<SolutionFirstRun> FirstRuns { get; init; } = [];
}

/// <summary>
/// ONE SCHEDULE'S FIRST RUN, as the result screen and the CLI name it: "Fetch jobs ran now", or
/// "Fetch jobs first runs at 8:51 PM". <paramref name="Outcome"/> is <c>fired</c> for a schedule the
/// install ran (<paramref name="RanNow"/>), <c>scheduled</c> for one that waits for its first due
/// time, and for a first run at install that did not happen the fire's own word - <c>skipped</c>,
/// <c>capped</c>, <c>member-missing</c> - or <c>failed</c>. That is a run's outcome, never the
/// install's. <paramref name="At"/> is when it ran, or when it first runs; <paramref name="Next"/>
/// is its next due time after that.
/// </summary>
public sealed record SolutionFirstRun(
    string Trigger, string Member, bool RunAtInstall, bool RanNow, string Outcome, DateTimeOffset? At, DateTimeOffset? Next)
{
    public const string Fired = "fired";
    public const string Scheduled = "scheduled";
    public const string Failed = "failed";
}

/// <summary>
/// ONE SECRET A PACKAGE BINDS, as the wizard's "Your part", its result and the CLI show it: the
/// member, the plugin's secret field, the KEY NAME it is bound to (never a value), whether the Host
/// has that key set (by name only), the manifest's description, what it is needed for, and the exact
/// way to set it. <paramref name="Needed"/> is false for a secret whose setting the person left off
/// (a source not ticked), true when needed, and null in a preview where it waits on the person's
/// answer to <paramref name="When"/>'s setting.
/// </summary>
public sealed record SolutionSecret(
    string Member, string Field, string Key, string Description, bool Required, SolutionPlanSecretWhen? When,
    bool Set, bool? Needed, string SetWith)
{
    /// <summary>How an operator sets <paramref name="key"/>: the operator CLI prompts for the value,
    /// and the Host reads its environment when it starts. The web and the CLI print the command
    /// itself under the product's name.</summary>
    public static string SetWithFor(string key) =>
        $"the operator CLI's `secret set {key}` (it prompts for the value), then its `up` to restart the Host";
}

public sealed record SolutionStepFailed(
    bool Ok, string Step, int StepNumber, string Title, string Reason, IReadOnlyList<string> Undone,
    IReadOnlyList<string> NotUndone, IReadOnlyList<SolutionStep> Steps);

/// <summary>
/// INSTALLS A SOLUTION PACKAGE AS A TEAM, and updates one. Reads the package only through
/// <see cref="SolutionService"/> (the check every reader shares) and writes only through the stores
/// a person's own clicks use, in this order:
/// <list type="number">
/// <item>install the package's plugins (<see cref="PluginInstaller"/>);</item>
/// <item>create the team, with a new local repository by default (<see cref="TeamRegistry.CreateAsync"/>);</item>
/// <item>hire the members, the Manager named and instructed as the package says;</item>
/// <item>register the team skills (<see cref="TeamSkills.RegisterAsync"/>);</item>
/// <item>copy <c>tools/</c> to <c>&lt;team folder&gt;/solution</c>, read-only to agents, which every
/// agent member's prompt then names and <c>{solution}</c> resolves to;</item>
/// <item>publish the sites (<see cref="SiteService"/>);</item>
/// <item>create the triggers (<see cref="TriggerCreation"/>, the Triggers dialog's own path);</item>
/// <item>record the package and version in <c>team_solutions</c>, with <c>solution.installed</c>.</item>
/// </list>
/// Each step appends its usual tenant row. When a step fails, everything made so far is undone in
/// reverse order and the outcome names the step and the reason.
/// </summary>
public sealed class SolutionInstaller(
    SolutionService solutions,
    PluginInstaller pluginInstaller,
    PluginCatalog plugins,
    TeamRegistry teams,
    TeamRepoSetup repoSetup,
    TeamDeletion teamDeletion,
    MemberDeletion memberDeletion,
    TeamSkills teamSkills,
    TeamPaths paths,
    SiteService sites,
    TriggerCreation triggers,
    ITeamSolutionStore store,
    TeamDocuments documents,
    FolderWatch folders,
    TenantLogging audit,
    Func<string?> defaultAgent,
    Connections? connections = null,
    ConnectionStore? connectionStore = null,
    IPluginMemberSettingsStore? pluginSettings = null,
    TeamAnnouncements? announce = null,
    int group = -1,
    ISecretStore? secretStore = null,
    TriggerSweep? sweep = null)
{
    public const string StepPlugins = "plugins";
    public const string StepTeam = "team";
    public const string StepMembers = "members";
    public const string StepSkills = "skills";
    public const string StepTools = "tools";
    public const string StepSites = "sites";
    public const string StepTriggers = "triggers";
    public const string StepRecord = "record";

    /// <summary>The steps in order, with the words the wizard and the CLI show.</summary>
    public static readonly IReadOnlyList<(string Step, string Title)> Steps =
    [
        (StepPlugins, "Install the plugins"),
        (StepTeam, "Create the team"),
        (StepMembers, "Hire the members"),
        (StepSkills, "Register the team skills"),
        (StepTools, "Copy the tools"),
        (StepSites, "Publish the sites"),
        (StepTriggers, "Create the triggers"),
        (StepRecord, "Record the package"),
    ];

    /// <summary>The installed tools folder of a team: <c>&lt;team folder&gt;/solution</c>.</summary>
    public static string ToolsFolderOf(TeamPaths paths, string team) =>
        Path.Combine(paths.RootFor(team), SolutionPlan.InstalledToolsFolder);

    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    private const UnixFileMode ExecutableMode = FileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute;

    /// <summary>One install or update at a time: two at once could both take a name or a plugin.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    // ------------------------------------------------------------------ reads

    /// <summary>Why a new team cannot be called <paramref name="name"/>, or null when it can.</summary>
    public string? NameRefusal(string? name)
    {
        var trimmed = (name ?? "").Trim();

        if (trimmed.Length == 0) return "A team needs a name.";
        if (trimmed.Length > TeamRegistry.MaximumLabelLength)
        {
            return $"A team name cannot be longer than {TeamRegistry.MaximumLabelLength} characters.";
        }

        if (teams.TeamAnsweringTo(trimmed) is { } answering)
        {
            return $"A team called '{teams.LabelFor(answering)}' already exists. Choose another name, or update that team if it came from this package.";
        }

        if (ContainerId.DeriveName(trimmed) is { } derived && teams.ExistingName(derived) is { } existing)
        {
            return $"'{trimmed}' is too close to the existing team '{teams.LabelFor(existing)}'. Choose another name.";
        }

        return null;
    }

    /// <summary>Every team installed from a package.</summary>
    public async Task<IReadOnlyList<object>> InstalledAsync(CancellationToken ct = default) =>
        [.. (await store.ListAsync(ct))
            .Where(row => teams.ExistingName(row.Team) is not null)
            .Select(row => (object)new
            {
                team = row.Team,
                teamName = teams.LabelFor(row.Team),
                id = row.PackageId,
                name = row.Name,
                version = row.Version,
                installedAt = row.InstalledAt,
                updatedAt = row.UpdatedAt,
                installedBy = row.InstalledBy,
                plugins = row.Plugins.Keys.Order(StringComparer.Ordinal).ToList(),
            })];

    /// <summary>What a team was installed from, and what it still waits for; null for a team not
    /// installed from a package. What the board's blocked notice and the team delete dialog read.</summary>
    public async Task<object?> DescribeAsync(string team, CancellationToken ct = default)
    {
        if (teams.ExistingName(team) is not { } stored || await store.FindAsync(stored, ct) is not { } row) return null;

        return new
        {
            team = stored,
            teamName = teams.LabelFor(stored),
            id = row.PackageId,
            name = row.Name,
            version = row.Version,
            installedAt = row.InstalledAt,
            updatedAt = row.UpdatedAt,
            installedBy = row.InstalledBy,
            plugins = row.Plugins.Keys.Order(StringComparer.Ordinal).ToList(),
            missing = await MissingAsync(stored, row, ct),
        };
    }

    /// <summary>
    /// WHAT THE TEAM STILL WAITS FOR: each required document folder with no file in it, and each
    /// required connection slot of the package's plugin members with no connection bound. Read live,
    /// so providing the input clears it with nothing else to do.
    /// </summary>
    public async Task<IReadOnlyList<SolutionMissing>> MissingAsync(string team, TeamSolutionRow row, CancellationToken ct = default)
    {
        var (manifest, _) = SolutionManifest.Parse(row.Manifest);
        if (manifest is null) return [];

        var missing = new List<SolutionMissing>();
        var docs = documents.RootFor(team);

        foreach (var input in manifest.Inputs.Documents.Where(d => d.Required))
        {
            var folder = Path.Combine(docs, input.Folder);
            var hasFile = Directory.Exists(folder)
                && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Any(f => !Path.GetFileName(f).StartsWith('.'));

            if (!hasFile) missing.Add(new SolutionMissing("document", input.Folder + "/", null, input.Description));
        }

        foreach (var input in manifest.Inputs.Connections.Where(c => c.Required))
        {
            if (!row.Members.TryGetValue(input.Member, out var memberName)) continue;

            var bound = teams.ContainerIdsOf(team).Any(id => string.Equals(id.Name, memberName, StringComparison.OrdinalIgnoreCase))
                && (await teams.MemberAsync(team, memberName, ct)) is { } member
                && await BindingOfAsync(team, member.Name, input.Slot, ct);

            if (!bound) missing.Add(new SolutionMissing("connection", input.Slot, input.Member, input.Description));
        }

        return missing;
    }

    private async Task<bool> BindingOfAsync(string team, string member, string slot, CancellationToken ct) =>
        pluginSettings is not null
        && (await pluginSettings.ForAsync(new ContainerId(team, member), ct)).Connections.ContainsKey(slot);

    /// <summary>
    /// WHAT INSTALLING WOULD DO, writing nothing: a new team (and whether its name is free), or an
    /// update of <paramref name="team"/> with the diff. The wizard's Team and Review steps, and the
    /// CLI's first question, read this.
    /// </summary>
    public async Task<SolutionOutcome> PreviewAsync(string? folder, string? team, CancellationToken ct = default)
    {
        var check = solutions.Check(folder);
        if (check.FolderRefused) return new(400, new { error = check.Error });
        if (!check.Check!.Ok) return new(200, new { ok = false, refusals = check.Check.Refusals });

        var package = check.Check.Package!;
        var plan = check.Check.Plan!;
        var available = await ConnectionsAsync(ct);

        if (!string.IsNullOrWhiteSpace(team) && ExistingTeam(team) is { } stored)
        {
            var (row, refusal) = await UpdatableAsync(stored, package, ct);
            if (refusal is not null) return new(200, new { ok = false, error = refusal });

            return new(200, new
            {
                ok = true,
                mode = "update",
                team = stored,
                teamName = teams.LabelFor(stored),
                from = row!.Version,
                to = package.Manifest.Version,
                plan,
                diff = Diff(row, package),
                connections = available,
                kept = await KeptAsync(stored, row, package, ct),
                secrets = await SecretsAfterUpdateAsync(stored, row, package, ct),
            });
        }

        var name = string.IsNullOrWhiteSpace(team) ? package.Manifest.Team.Name : team.Trim();

        return new(200, new
        {
            ok = true,
            mode = "install",
            teamName = name,
            nameRefusal = NameRefusal(name),
            plan,
            connections = available,
            secrets = SecretStates(package, (member, field) => package.Manifest.Member(member)?.Secrets.GetValueOrDefault(field), config: null),
        });
    }

    /// <summary>
    /// WHAT AN UPDATE KEEPS of the person's part, so the wizard and the CLI show it instead of asking
    /// again: each kept member's person-only settings (its current value, null when unset) and
    /// connection slots (the bound connection's id, null when unbound), and the files already in
    /// each document folder the package asks for. An update never changes a kept member's settings
    /// or bindings; only a member the update adds is asked.
    /// </summary>
    private async Task<object> KeptAsync(string team, TeamSolutionRow row, SolutionPackage package, CancellationToken ct)
    {
        var manifest = package.Manifest;
        var settings = new List<object>();
        var connections = new List<object>();

        async Task<PluginMemberSettings?> CurrentAsync(string member) =>
            pluginSettings is not null && row.Members.TryGetValue(member, out var id) && MemberExists(team, id)
                ? await pluginSettings.ForAsync(new ContainerId(team, id), ct)
                : null;

        foreach (var input in manifest.Inputs.Settings)
        {
            if (await CurrentAsync(input.Member) is not { } current) continue;
            settings.Add(new { member = input.Member, setting = input.Setting, value = current.Config.TryGetValue(input.Setting, out var value) ? (JsonElement?)value : null });
        }

        foreach (var input in manifest.Inputs.Connections)
        {
            if (await CurrentAsync(input.Member) is not { } current) continue;
            connections.Add(new { member = input.Member, slot = input.Slot, connection = current.Connections.GetValueOrDefault(input.Slot) });
        }

        var docs = documents.RootFor(team);
        var present = manifest.Inputs.Documents.Select(input =>
        {
            var folder = Path.Combine(docs, input.Folder);
            var files = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith('.'))
                    .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : [];
            return (object)new { folder = input.Folder, files };
        }).ToList();

        return new { settings, connections, documents = present };
    }

    /// <summary>
    /// THE SECRETS LIST: each field the package's plugin members bind, with the key it will be (or
    /// is) bound to, whether the Host has that key set - asked of the secret store by NAME, the value
    /// never read into anything answered - and whether it is needed. <paramref name="config"/> answers a
    /// member's settings as installed; null in a preview, where a secret whose setting the person is
    /// asked for waits on the answer.
    /// </summary>
    private IReadOnlyList<SolutionSecret> SecretStates(
        SolutionPackage package, Func<string, string, string?> keyOf, Func<string, IReadOnlyDictionary<string, JsonElement>?>? config)
    {
        var states = new List<SolutionSecret>();

        foreach (var planned in SolutionPlan.Of(package).Secrets)
        {
            if (keyOf(planned.Member, planned.Field) is not { } key) continue;

            bool? needed = true;

            if (planned.When is { } when)
            {
                var member = package.Manifest.Member(planned.Member);
                var field = package.Plugin(member?.PluginId)?.Manifest.Config.GetValueOrDefault(when.Setting);
                var condition = new PluginSecretWhen(when.Setting, when.Value);

                if (config?.Invoke(planned.Member) is { } installed)
                {
                    needed = condition.HoldsIn(installed.TryGetValue(when.Setting, out var value) ? value : field?.Default);
                }
                else if (package.Manifest.Inputs.Settings.Any(i => SameName(i.Member, planned.Member) && i.Setting == when.Setting))
                {
                    needed = null;
                }
                else
                {
                    needed = condition.HoldsIn(member is not null && member.Settings.TryGetValue(when.Setting, out var value) ? value : field?.Default);
                }
            }

            states.Add(new SolutionSecret(
                planned.Member, planned.Field, key, planned.Description, planned.Required, planned.When,
                secretStore?.TryGet(key) is not null, needed, SolutionSecret.SetWithFor(key)));
        }

        return states;
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The secrets list as an update would leave the team: a kept member's bindings merged
    /// as <see cref="MergedSecrets"/> does, a new member's as the package says.</summary>
    private async Task<IReadOnlyList<SolutionSecret>> SecretsAfterUpdateAsync(
        string team, TeamSolutionRow row, SolutionPackage package, CancellationToken ct)
    {
        var (old, _) = SolutionManifest.Parse(row.Manifest);
        var bound = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var configs = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in package.Manifest.Members.Where(m => m.Kind == MemberRef.PluginKind))
        {
            if (pluginSettings is not null && row.Members.TryGetValue(member.Name, out var id) && MemberExists(team, id))
            {
                var current = await pluginSettings.ForAsync(new ContainerId(team, id), ct);
                bound[member.Name] = MergedSecrets(current.Secrets, old?.Member(member.Name), member);
                configs[member.Name] = current.Config;
            }
            else
            {
                bound[member.Name] = member.Secrets;
            }
        }

        return SecretStates(
            package,
            (member, field) => bound.GetValueOrDefault(member)?.GetValueOrDefault(field),
            member => configs.GetValueOrDefault(member));
    }

    /// <summary>
    /// AN UPDATE KEEPS A BINDING A PERSON CHANGED: a field still bound as the installed version of
    /// the package bound it takes the new version's key (or goes, when the new version names none);
    /// a field the person bound otherwise - to another key, or unbound - stays as the person left it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MergedSecrets(
        IReadOnlyDictionary<string, string> current, SolutionMember? installed, SolutionMember now)
    {
        var merged = new Dictionary<string, string>(current, StringComparer.Ordinal);
        var before = installed?.Secrets ?? new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in before.Keys.Union(now.Secrets.Keys, StringComparer.Ordinal))
        {
            if (current.GetValueOrDefault(field) != before.GetValueOrDefault(field)) continue;

            if (now.Secrets.TryGetValue(field, out var key)) merged[field] = key;
            else merged.Remove(field);
        }

        return merged;
    }

    /// <summary>The team's secrets list once installed or updated, read from what is stored.</summary>
    private async Task<IReadOnlyList<SolutionSecret>> InstalledSecretsAsync(
        string team, IReadOnlyDictionary<string, string> memberIds, SolutionPackage package, CancellationToken ct)
    {
        var stored = new Dictionary<string, PluginMemberSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in package.Manifest.Members.Where(m => m.Kind == MemberRef.PluginKind))
        {
            if (pluginSettings is not null && memberIds.TryGetValue(member.Name, out var id))
            {
                stored[member.Name] = await pluginSettings.ForAsync(new ContainerId(team, id), ct);
            }
        }

        return SecretStates(
            package,
            (member, field) => stored.GetValueOrDefault(member)?.Secrets.GetValueOrDefault(field),
            member => stored.GetValueOrDefault(member)?.Config);
    }

    private async Task<IReadOnlyList<object>> ConnectionsAsync(CancellationToken ct) =>
        connectionStore is null
            ? []
            : [.. (await connectionStore.ListAsync(ct)).Select(c => (object)new { id = c.Id, name = c.Name, provider = c.Provider, account = c.Account, status = c.Status })];

    /// <summary>A team the person named, by label or id.</summary>
    private string? ExistingTeam(string team) =>
        teams.ExistingName(team.Trim()) ?? teams.TeamAnsweringTo(team.Trim());

    private async Task<(TeamSolutionRow? Row, string? Refusal)> UpdatableAsync(string stored, SolutionPackage package, CancellationToken ct)
    {
        var label = teams.LabelFor(stored);

        if (await store.FindAsync(stored, ct) is not { } row)
        {
            return (null, $"'{label}' was not installed from a solution package, so it cannot be updated from one. Choose a new team name instead.");
        }

        if (!string.Equals(row.PackageId, package.Manifest.Id, StringComparison.Ordinal))
        {
            return (null, $"'{label}' was installed from the package '{row.PackageId}', not '{package.Manifest.Id}'.");
        }

        if (CompareVersions(package.Manifest.Version, row.Version) <= 0)
        {
            return (null, $"'{label}' already runs {row.Name} {row.Version}; an update needs a newer version than that, and this package is {package.Manifest.Version}.");
        }

        return (row, null);
    }

    /// <summary>Compares two package versions part by part: numbers as numbers, the rest as text.</summary>
    public static int CompareVersions(string a, string b)
    {
        var left = a.Split('.', '-', '+');
        var right = b.Split('.', '-', '+');

        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : "0";
            var y = i < right.Length ? right[i] : "0";
            var compared = long.TryParse(x, out var nx) && long.TryParse(y, out var ny)
                ? nx.CompareTo(ny)
                : string.CompareOrdinal(x, y);

            if (compared != 0) return Math.Sign(compared);
        }

        return 0;
    }

    // ------------------------------------------------------------------ install

    /// <summary>
    /// Installs the package at <c>request.Folder</c> as a new team. <paramref name="afterStep"/> is
    /// called after each step has made what it makes (tests throw from it to prove the undo).
    /// </summary>
    public async Task<SolutionOutcome> InstallAsync(
        SolutionInstallRequest request, SolutionActor actor, CancellationToken ct = default,
        Func<string, Task>? afterStep = null)
    {
        await _gate.WaitAsync(ct);

        try
        {
            return await InstallLockedAsync(request, actor, afterStep, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SolutionOutcome> InstallLockedAsync(
        SolutionInstallRequest request, SolutionActor actor, Func<string, Task>? afterStep, CancellationToken ct)
    {
        // EVERYTHING REFUSABLE IS REFUSED BEFORE THE FIRST WRITE.
        var check = solutions.Check(request.Folder);
        if (check.FolderRefused) return new(400, new { error = check.Error });
        if (!check.Check!.Ok) return new(200, new { ok = false, refusals = check.Check.Refusals });

        var package = check.Check.Package!;
        var manifest = package.Manifest;
        var teamName = string.IsNullOrWhiteSpace(request.TeamName) ? manifest.Team.Name : request.TeamName.Trim();

        if (NameRefusal(teamName) is { } nameRefusal) return new(409, new { error = nameRefusal });

        var answers = request.Answers ?? new SolutionAnswers();
        if (AnswersRefusal(package, answers, update: false) is { } answersRefusal) return new(400, new { error = answersRefusal });

        if (await BindingsRefusalAsync(package, answers, ct) is { } bindingRefusal) return new(400, new { error = bindingRefusal });

        var agent = string.IsNullOrWhiteSpace(request.Agent) ? defaultAgent() : request.Agent.Trim();
        if (agent is null)
        {
            return new(400, new { error = "No Agent is installed for the team's members to run. Install one (Admin → Agents), or name one in `agent`." });
        }

        var run = new Run(actor, afterStep);
        string? stored = null;
        var memberIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var triggerIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var firstDue = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);

        try
        {
            // 1. PLUGINS.
            await run.StepAsync(StepPlugins, async () =>
            {
                foreach (var plugin in package.Plugins) await InstallPluginAsync(plugin, run, ct);
            });

            // 2. THE TEAM, with its local repository unless asked not to.
            await run.StepAsync(StepTeam, async () =>
            {
                var repos = await repoSetup.PlanNewTeamAsync(null, null, null, person: true, request.LocalRepository, ct);
                var presets = manifest.Members
                    .Where(m => m.Kind == MemberRef.AgentKind && m.Preset is not null)
                    .Select(m => m.Preset!)
                    .Prepend(agent)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var managerAgent = manifest.Members.FirstOrDefault(m => m.Role == SolutionManifest.RoleManager)?.Preset ?? agent;

                TeamSummary created;

                try
                {
                    created = await teams.CreateAsync(
                        teamName, managerAgent,
                        additionalInstructions: string.IsNullOrWhiteSpace(manifest.Team.Instructions) ? null : manifest.Team.Instructions,
                        memberAgents: presets, repos: repos.Repos, ct: ct, addRepo: repos.AddRepoAsync);
                }
                catch
                {
                    await repos.ForgetUnlessCreatedAsync(teamCreated: false);
                    throw;
                }

                stored = created.Id;
                run.Made($"team {created.Id}", async () =>
                {
                    var announceDeleted = announce is null ? null : await announce.BeforeDeleteAsync(created.Id);
                    await teamDeletion.DeleteAsync(created.Id, ct: CancellationToken.None);
                    await repos.ForgetUnlessCreatedAsync(teamCreated: false);
                    if (announceDeleted is not null) await announceDeleted();
                });

                if (repos.LocalRepository is { Created: true } made)
                {
                    await audit.WriteAsAsync(actor.UserId, actor.Email, TenantActions.LocalRepoCreated, made.Name, made.Name,
                        new { reference = made.Reference, defaultBranch = LocalRepos.InitialBranch, team = created.Id }, ct);
                }

                await audit.WriteAsAsync(actor.UserId, actor.Email, TenantActions.TeamCreated, created.Id, created.Name,
                    new { manager = created.Containers.FirstOrDefault()?.Agent, solution = manifest.Id, version = manifest.Version }, ct);

                if (announce is not null) await announce.CreatedAsync(created.Id);
            });

            var tools = package.ToolsFolder is null ? null : ToolsFolderOf(paths, stored!);

            // 3. THE MEMBERS: the Manager takes the package's name and instructions, the rest are hired.
            await run.StepAsync(StepMembers, async () =>
            {
                foreach (var member in manifest.Members)
                {
                    memberIds[member.Name] = await HireAsync(stored!, member, package, answers, agent, tools, actor, run, ct);
                }
            });

            // 4. THE TEAM SKILLS.
            await run.StepAsync(StepSkills, async () =>
            {
                foreach (var skill in package.Skills) await RegisterSkillAsync(stored!, skill, actor, run, ct);
            });

            // 5. THE TOOLS, read-only to agents.
            await run.StepAsync(StepTools, async () =>
            {
                if (package.ToolsFolder is not { } source) return;

                CopyReadOnly(source, tools!);
                run.Made($"tools {tools}", () => { RemoveFolder(tools!); return Task.CompletedTask; });

                // Every agent member's prompt now names the folder.
                await teams.RepromptTeamAsync(stored!, ct);
            });

            // 6. THE SITES.
            await run.StepAsync(StepSites, async () =>
            {
                foreach (var site in package.Sites) await PublishSiteAsync(stored!, site, actor, run, isNew: true, ct);
            });

            // 7. THE TRIGGERS, after the document folders a folder trigger watches.
            await run.StepAsync(StepTriggers, async () =>
            {
                EnsureDocumentFolders(stored!, manifest, run);

                foreach (var trigger in manifest.Triggers)
                {
                    var made = await CreateTriggerAsync(stored!, trigger, memberIds, tools, actor, run, ct);
                    triggerIds[trigger.Name] = made.Id;
                    firstDue[trigger.Name] = made.NextDueAt;
                }
            });

            // 8. THE RECORD, with `solution.installed` in the same transaction.
            await run.StepAsync(StepRecord, async () =>
            {
                var row = new TeamSolutionRow(
                    stored!, manifest.Id, manifest.Name, manifest.Version, package.Folder, DateTimeOffset.UtcNow, actor.Label, null,
                    await File.ReadAllTextAsync(Path.Combine(package.Folder, SolutionManifest.FileName), ct),
                    Digests(package), memberIds, triggerIds,
                    package.Plugins.ToDictionary(p => p.Id, p => p.Manifest.Version, StringComparer.Ordinal));

                await store.SaveAsync(row, actor.Row(TenantActions.SolutionInstalled, stored, teams.LabelFor(stored!), new
                {
                    id = manifest.Id,
                    version = manifest.Version,
                    folder = package.Folder,
                    members = memberIds,
                    triggers = triggerIds.Keys,
                    skills = package.Skills.Select(s => s.Name),
                    sites = package.Sites.Select(s => s.Name),
                    tools = package.Tools,
                    plugins = row.Plugins,
                }), ct);

                run.Made($"record {stored}", () => store.DeleteAsync(stored!, null, CancellationToken.None));
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return await FailedAsync(run, exception, manifest, teamName);
        }

        await CopyDocumentsAsync(stored!, answers, actor, ct);

        // THE FIRST RUNS, only now that the last step has succeeded and the documents are in: a
        // failed step returned above, having made no fire.
        var firstRuns = await FirstRunsAsync(manifest, triggerIds, firstDue, actor, ct);

        var saved = (await store.FindAsync(stored!, ct))!;

        return new(200, new SolutionDone(
            true, stored!, teams.LabelFor(stored!), manifest.Id, manifest.Version, null, null,
            await MissingAsync(stored!, saved, ct), run.Done)
        {
            Secrets = await InstalledSecretsAsync(stored!, memberIds, package, ct),
            FirstRuns = firstRuns,
        });
    }

    // ------------------------------------------------------------------ update

    /// <summary>
    /// Moves <c>request.Team</c> to the newer version of its package at <c>request.Folder</c>: adds
    /// what is new, changes what changed and removes what the package no longer has, keeping the
    /// person's settings, bindings, uploaded documents and site data. What a failed step had made is
    /// undone; removals run last, once everything that can fail has.
    /// </summary>
    public async Task<SolutionOutcome> UpdateAsync(
        SolutionUpdateRequest request, SolutionActor actor, CancellationToken ct = default,
        Func<string, Task>? afterStep = null)
    {
        await _gate.WaitAsync(ct);

        try
        {
            return await UpdateLockedAsync(request, actor, afterStep, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// UNINSTALLS the solution <paramref name="team"/> was installed from: removes the triggers, the
    /// members (the team's Manager stays - every team has one - with the package's instructions
    /// cleared), the team skills, the sites with their data and the tools folder the package made,
    /// and forgets the <c>team_solutions</c> row with its <c>solution.uninstalled</c> row. The team
    /// and its DOCUMENTS stay: they are the person's. With <paramref name="removePlugins"/>, each of
    /// the package's plugins is removed too, but only when no other team has a member on it.
    ///
    /// Asking first is the caller's: this acts when called. Each removal goes through the store a
    /// person's own click uses, with its own tenant row; one that fails is named in <c>failures</c>
    /// and the rest go on, so a half-removed solution is never left looking installed.
    /// </summary>
    public async Task<SolutionOutcome> UninstallAsync(
        string team, bool removePlugins, SolutionActor actor, PluginRemover? remover, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        try
        {
            return await UninstallLockedAsync(team, removePlugins, actor, remover, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SolutionOutcome> UninstallLockedAsync(
        string team, bool removePlugins, SolutionActor actor, PluginRemover? remover, CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored || await store.FindAsync(stored, ct) is not { } row)
        {
            return new(404, new { error = $"'{team}' was not installed from a solution package." });
        }

        var manifest = SolutionManifest.Parse(row.Manifest).Manifest;
        var failures = new List<string>();
        var removedTriggers = new List<string>();
        var removedMembers = new List<string>();
        var removedSkills = new List<string>();
        var removedSites = new List<string>();

        async Task Try(string what, Func<Task> removal)
        {
            try
            {
                await removal();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"{what}: {exception.Message}");
            }
        }

        // 1. TRIGGERS FIRST, so nothing fires onto a member about to go.
        foreach (var (name, id) in row.Triggers.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            await Try($"trigger {name}", async () =>
            {
                if (await triggers.DeleteAsync(id, actor.Row(TenantActions.ScheduleDeleted, id, name, new { team = stored, solution = row.PackageId, uninstalled = true }), ct))
                {
                    removedTriggers.Add(name);
                }
            });
        }

        // 2. MEMBERS. The Manager stays, as every team has one; the package's instructions for it go.
        foreach (var (name, id) in row.Members.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (string.Equals(id, TeamRegistry.DefaultManagerName, StringComparison.OrdinalIgnoreCase))
            {
                await Try($"member {name}", async () =>
                {
                    if (!MemberExists(stored, id)) return;
                    var manager = await teams.MemberAsync(stored, id, ct);
                    await teams.UpdateMemberAsync(
                        stored, id, manager.Label, "", manager.Agent, actor.PromptSetter, ct,
                        change => [actor.Row(TenantActions.MemberInstructionsChanged, $"{stored}/{id}", name, new { solution = row.PackageId, uninstalled = true })]);
                });
                continue;
            }

            await Try($"member {name}", async () =>
            {
                if (!MemberExists(stored, id)) return;
                if (await memberDeletion.DeleteAsync(stored, id, ct) is null) return;
                await audit.WriteAsAsync(actor.UserId, actor.Email, TenantActions.MemberDeleted, $"{stored}/{id}", name, new { solution = row.PackageId, uninstalled = true }, ct);
                removedMembers.Add(id);
            });
        }

        // 3. TEAM SKILLS the package registered.
        foreach (var name in manifest?.Skills ?? [])
        {
            await Try($"skill {name}", async () =>
            {
                if (await teamSkills.DeleteAsync(stored, name,
                        actor.Row(TenantActions.SkillDeleted, stored, name, new { team = stored, name, solution = row.PackageId, uninstalled = true }), ct))
                {
                    removedSkills.Add(name);
                }
            });
        }

        // 4. SITES, with their data: the solution's app goes with it. Documents are not site data.
        foreach (var name in manifest?.Sites ?? [])
        {
            await Try($"site {name}", async () =>
            {
                if ((await sites.DeleteAsync(stored, name, confirmed: true, actor.Site, ct)).Ok) removedSites.Add(name);
            });
        }

        // 5. THE TOOLS FOLDER, the Host's own copy under the team's folder.
        var tools = ToolsFolderOf(paths, stored);
        var hadTools = Directory.Exists(tools);
        await Try("tools", () =>
        {
            RemoveFolder(tools);
            return Task.CompletedTask;
        });

        // 6. THE PLUGINS, only when asked and only when no other team hires them. After the members,
        // so this team's own members no longer count as a use.
        var pluginsRemoved = new List<string>();
        var pluginsKept = new List<object>();

        foreach (var id in row.Plugins.Keys.Order(StringComparer.Ordinal))
        {
            var usedBy = remover?.HiredOn(id).Select(m => m.Team).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

            if (!removePlugins || remover is null || usedBy.Count > 0)
            {
                pluginsKept.Add(new { id, usedBy });
                continue;
            }

            await Try($"plugin {id}", async () =>
            {
                var removed = await remover.RemoveAsync(id, null,
                    actor.Row(TenantActions.PluginRemoved, id, id, new { solution = row.PackageId, team = stored, uninstalled = true }), ct);

                if (removed.Status is >= 200 and < 300) pluginsRemoved.Add(id);
                else
                {
                    failures.Add($"plugin {id}: {removed.Reason}");
                    pluginsKept.Add(new { id, usedBy });
                }
            });
        }

        // 7. THE RECORD, with the row that says what went. Last, so the team shows as a solution
        // until everything that can be removed has been.
        var documentsFolder = documents.RootFor(stored);
        var documentsKept = Directory.Exists(documentsFolder) ? documentsFolder : null;

        var removedSummary = new
        {
            triggers = removedTriggers,
            members = removedMembers,
            skills = removedSkills,
            sites = removedSites,
            tools = hadTools && !Directory.Exists(tools),
        };

        await store.DeleteAsync(stored, actor.Row(TenantActions.SolutionUninstalled, stored, teams.LabelFor(stored), new
        {
            id = row.PackageId,
            version = row.Version,
            removed = removedSummary,
            plugins = new { removed = pluginsRemoved, kept = pluginsKept },
            documentsKept,
            failures,
        }), ct);

        return new(200, new
        {
            ok = failures.Count == 0,
            team = stored,
            teamName = teams.LabelFor(stored),
            id = row.PackageId,
            version = row.Version,
            removed = removedSummary,
            plugins = new { removed = pluginsRemoved, kept = pluginsKept },
            documentsKept,
            failures,
        });
    }

    private async Task<SolutionOutcome> UpdateLockedAsync(
        SolutionUpdateRequest request, SolutionActor actor, Func<string, Task>? afterStep, CancellationToken ct)
    {
        var check = solutions.Check(request.Folder);
        if (check.FolderRefused) return new(400, new { error = check.Error });
        if (!check.Check!.Ok) return new(200, new { ok = false, refusals = check.Check.Refusals });

        if (string.IsNullOrWhiteSpace(request.Team) || ExistingTeam(request.Team) is not { } stored)
        {
            return new(404, new { error = $"There is no team '{request.Team}' to update." });
        }

        var package = check.Check.Package!;
        var manifest = package.Manifest;
        var (row, refusal) = await UpdatableAsync(stored, package, ct);
        if (refusal is not null) return new(409, new { error = refusal });

        var answers = request.Answers ?? new SolutionAnswers();
        if (AnswersRefusal(package, answers, update: true) is { } answersRefusal) return new(400, new { error = answersRefusal });

        if (await BindingsRefusalAsync(package, answers, ct) is { } bindingRefusal) return new(400, new { error = bindingRefusal });

        var (old, _) = SolutionManifest.Parse(row!.Manifest);
        if (old is null) return new(409, new { error = $"The record of what '{teams.LabelFor(stored)}' was installed from cannot be read; it cannot be updated." });

        var diff = Diff(row, package);
        var run = new Run(actor, afterStep);
        var memberIds = new Dictionary<string, string>(row.Members, StringComparer.OrdinalIgnoreCase);
        var triggerIds = new Dictionary<string, string>(row.Triggers, StringComparer.Ordinal);
        var tools = package.ToolsFolder is null ? null : ToolsFolderOf(paths, stored);
        var agent = teams.MemberAgentsFor(stored)?.FirstOrDefault() ?? defaultAgent();
        var removals = new List<Func<Task>>();

        try
        {
            await run.StepAsync(StepPlugins, async () =>
            {
                foreach (var plugin in package.Plugins) await InstallPluginAsync(plugin, run, ct);
            });

            await run.StepAsync(StepMembers, async () =>
            {
                foreach (var member in manifest.Members)
                {
                    if (!memberIds.TryGetValue(member.Name, out var id) || !MemberExists(stored, id))
                    {
                        memberIds[member.Name] = await HireAsync(stored, member, package, answers, agent!, tools, actor, run, ct);
                        continue;
                    }

                    if (member.Kind == MemberRef.PluginKind)
                    {
                        await RebindSecretsAsync(stored, id, old.Member(member.Name), member, package, actor, run, ct);
                        continue;
                    }

                    if (diff.Members.Changed.Contains(member.Name, StringComparer.OrdinalIgnoreCase) && member.Kind == MemberRef.AgentKind)
                    {
                        var before = await teams.MemberAsync(stored, id, ct);
                        await teams.UpdateMemberAsync(
                            stored, id, member.Name, ResolveSolution(member.Instructions, tools), member.Preset, actor.PromptSetter, ct,
                            change => [actor.Row(TenantActions.MemberInstructionsChanged, $"{stored}/{id}", member.Name, new { solution = manifest.Id, version = manifest.Version })]);
                        run.Made($"member {member.Name} (changed)", () => teams.UpdateMemberAsync(
                            stored, id, before.Label, before.SystemPrompt ?? "", before.Agent, actor.PromptSetter, CancellationToken.None));
                    }
                }

                foreach (var name in diff.Members.Removed)
                {
                    if (row.Members.TryGetValue(name, out var id) && !string.Equals(id, TeamRegistry.DefaultManagerName, StringComparison.OrdinalIgnoreCase))
                    {
                        removals.Add(async () =>
                        {
                            await memberDeletion.DeleteAsync(stored, id, CancellationToken.None);
                            await audit.WriteAsAsync(actor.UserId, actor.Email, TenantActions.MemberDeleted, $"{stored}/{id}", name, new { solution = manifest.Id }, CancellationToken.None);
                        });
                        memberIds.Remove(name);
                    }
                }
            });

            await run.StepAsync(StepSkills, async () =>
            {
                var existing = (await teamSkills.ListAsync(stored, ct)) ?? [];

                foreach (var skill in package.Skills.Where(s => !diff.Skills.Added.Contains(s.Name) ? diff.Skills.Changed.Contains(s.Name) : true))
                {
                    var before = existing.FirstOrDefault(s => s.Name == skill.Name);

                    if (before is null)
                    {
                        await RegisterSkillAsync(stored, skill, actor, run, ct);
                        continue;
                    }

                    await teamSkills.RegisterAsync(stored, new SkillDraft(skill.Name, skill.Description, skill.Roles, skill.Body), actor.Label,
                        actor.Row(TenantActions.SkillChanged, stored, skill.Name, new { team = stored, name = skill.Name, solution = manifest.Id }), ct);
                    run.Made($"skill {skill.Name} (changed)", () => teamSkills.RegisterAsync(
                        stored, new SkillDraft(before.Name, before.Description, before.Roles, before.Body), actor.Label, null, CancellationToken.None));
                }

                foreach (var name in diff.Skills.Removed)
                {
                    removals.Add(() => teamSkills.DeleteAsync(stored, name,
                        actor.Row(TenantActions.SkillDeleted, stored, name, new { team = stored, name, solution = manifest.Id }), CancellationToken.None));
                }
            });

            await run.StepAsync(StepTools, () =>
            {
                if (!diff.Tools.Any) return Task.CompletedTask;

                // THE NEW COPY IS MADE BESIDE THE OLD ONE AND SWAPPED IN, so a failed copy leaves the
                // old tools, and the swap is undone by swapping back.
                var incoming = tools is null ? null : tools + ".incoming";
                var folder = ToolsFolderOf(paths, stored);
                var outgoing = folder + ".outgoing";

                if (incoming is not null)
                {
                    RemoveFolder(incoming);
                    CopyReadOnly(package.ToolsFolder!, incoming);
                }

                RemoveFolder(outgoing);
                if (Directory.Exists(folder)) Directory.Move(folder, outgoing);
                if (incoming is not null) Directory.Move(incoming, folder);

                run.Made("tools (changed)", () =>
                {
                    RemoveFolder(folder);
                    if (Directory.Exists(outgoing)) Directory.Move(outgoing, folder);
                    return Task.CompletedTask;
                });
                removals.Add(() => { RemoveFolder(outgoing); return Task.CompletedTask; });
                return Task.CompletedTask;
            });

            await run.StepAsync(StepSites, async () =>
            {
                foreach (var site in package.Sites)
                {
                    if (diff.Sites.Added.Contains(site.Name)) await PublishSiteAsync(stored, site, actor, run, isNew: true, ct);
                    else if (diff.Sites.Changed.Contains(site.Name)) await PublishSiteAsync(stored, site, actor, run, isNew: false, ct);
                }

                // A SITE THE PACKAGE NO LONGER HAS IS UNPUBLISHED, never deleted: its data is the person's.
                foreach (var name in diff.Sites.Removed)
                {
                    removals.Add(() => sites.UnpublishAsync(stored, name, actor.Site, CancellationToken.None));
                }
            });

            await run.StepAsync(StepTriggers, async () =>
            {
                EnsureDocumentFolders(stored, manifest, run);

                foreach (var trigger in manifest.Triggers)
                {
                    var added = diff.Triggers.Added.Contains(trigger.Name);
                    if (!added && !diff.Triggers.Changed.Contains(trigger.Name)) continue;

                    // A CHANGED TRIGGER IS MADE NEW, and the old one removed with the removals.
                    if (!added && triggerIds.TryGetValue(trigger.Name, out var oldId))
                    {
                        removals.Add(() => triggers.DeleteAsync(oldId,
                            actor.Row(TenantActions.ScheduleDeleted, oldId, trigger.Name, new { team = stored, solution = manifest.Id, replaced = true }), CancellationToken.None));
                    }

                    triggerIds[trigger.Name] = (await CreateTriggerAsync(stored, trigger, memberIds, tools, actor, run, ct)).Id;
                }

                foreach (var name in diff.Triggers.Removed)
                {
                    if (!triggerIds.Remove(name, out var id)) continue;
                    removals.Add(() => triggers.DeleteAsync(id,
                        actor.Row(TenantActions.ScheduleDeleted, id, name, new { team = stored, solution = manifest.Id }), CancellationToken.None));
                }
            });

            await run.StepAsync(StepRecord, async () =>
            {
                // WHAT THE PACKAGE NO LONGER HAS GOES LAST, once nothing else can fail.
                foreach (var removal in removals) await removal();

                var updated = row with
                {
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Folder = package.Folder,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Manifest = await File.ReadAllTextAsync(Path.Combine(package.Folder, SolutionManifest.FileName), ct),
                    Digests = Digests(package),
                    Members = memberIds,
                    Triggers = triggerIds,
                    Plugins = package.Plugins.ToDictionary(p => p.Id, p => p.Manifest.Version, StringComparer.Ordinal),
                };

                await store.SaveAsync(updated, actor.Row(TenantActions.SolutionUpdated, stored, teams.LabelFor(stored), new
                {
                    id = manifest.Id,
                    from = row.Version,
                    to = manifest.Version,
                    folder = package.Folder,
                    diff,
                }), ct);

                run.Made($"record {stored} (updated)", () => store.SaveAsync(row,
                    actor.Row(TenantActions.SolutionFailed, stored, teams.LabelFor(stored), new { id = row.PackageId, restored = row.Version }), CancellationToken.None));
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return await FailedAsync(run, exception, manifest, teams.LabelFor(stored));
        }

        await CopyDocumentsAsync(stored, answers, actor, ct);
        var saved = (await store.FindAsync(stored, ct))!;

        return new(200, new SolutionDone(
            true, stored, teams.LabelFor(stored), manifest.Id, manifest.Version, row.Version, diff,
            await MissingAsync(stored, saved, ct), run.Done)
        {
            Secrets = await InstalledSecretsAsync(stored, memberIds, package, ct),
        });
    }

    /// <summary>What changed from the team's installed version to <paramref name="package"/>.</summary>
    public static SolutionDiff Diff(TeamSolutionRow row, SolutionPackage package)
    {
        var (old, _) = SolutionManifest.Parse(row.Manifest);
        var manifest = package.Manifest;
        var digests = Digests(package);

        SolutionDiffSection Section<T>(IEnumerable<T> before, IEnumerable<T> after, Func<T, string> name, Func<T, T, bool> same, StringComparer comparer)
        {
            var was = before.ToDictionary(name, comparer);
            var now = after.ToDictionary(name, comparer);
            return new SolutionDiffSection(
                [.. now.Keys.Where(k => !was.ContainsKey(k))],
                [.. now.Keys.Where(k => was.TryGetValue(k, out var w) && !same(w, now[k]))],
                [.. was.Keys.Where(k => !now.ContainsKey(k))]);
        }

        SolutionDiffSection ByDigest(string prefix, IEnumerable<string> before, IEnumerable<string> after) => Section(
            before, after, n => n,
            (a, _) => row.Digests.GetValueOrDefault(prefix + a) == digests.GetValueOrDefault(prefix + a),
            StringComparer.Ordinal);

        var oldTools = row.Digests.Keys.Where(k => k.StartsWith("tool:", StringComparison.Ordinal)).Select(k => k[5..]);

        return new SolutionDiff(
            Section(old?.Members ?? [], manifest.Members, m => m.Name,
                (a, b) => Json(a) == Json(b)
                    && (a.PluginId is null || row.Plugins.GetValueOrDefault(a.PluginId) == package.Plugin(b.PluginId)?.Manifest.Version),
                StringComparer.OrdinalIgnoreCase),
            Section(old?.Triggers ?? [], manifest.Triggers, t => t.Name, (a, b) => Json(a) == Json(b), StringComparer.Ordinal),
            ByDigest("skill:", old?.Skills ?? [], manifest.Skills),
            ByDigest("site:", old?.Sites ?? [], manifest.Sites),
            ByDigest("tool:", oldTools, package.Tools),
            Section(row.Plugins.Select(p => (p.Key, p.Value)), package.Plugins.Select(p => (p.Id, p.Manifest.Version)),
                p => p.Item1, (a, b) => a.Item2 == b.Item2, StringComparer.Ordinal) is var plugins
                ? plugins with
                {
                    Changed = [.. plugins.Changed.Select(id => $"{id} {row.Plugins[id]} → {package.Plugin(id)!.Manifest.Version}")],
                }
                : null!);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    // ------------------------------------------------------------------ steps

    private async Task InstallPluginAsync(SolutionPlugin plugin, Run run, CancellationToken ct)
    {
        // A PLUGIN IS THE INSTANCE'S, one version active for every team. That version, or a newer one,
        // already active: nothing to make and nothing to undo - an install never downgrades what other
        // teams run.
        if (plugins.For(plugin.Id) is { } loaded && CompareVersions(loaded.Manifest.Version, plugin.Manifest.Version) >= 0) return;

        var directory = Path.Combine(plugins.Root, plugin.Id);
        var existed = Directory.Exists(directory);
        var activeFile = Path.Combine(directory, PluginCatalog.ActiveFile);
        var previous = File.Exists(activeFile) ? (await File.ReadAllTextAsync(activeFile, ct)).Trim() : null;

        // THIS VERSION IS KEPT BUT NOT ACTIVE (an older one is): made active again, not copied over.
        if (Directory.Exists(Path.Combine(directory, plugin.Manifest.Version)))
        {
            await File.WriteAllTextAsync(activeFile, plugin.Manifest.Version + "\n", ct);
            await plugins.RescanAsync(ct);

            run.Made($"plugin {plugin.Id} {plugin.Manifest.Version} (made active)", async () =>
            {
                if (previous is not null) await File.WriteAllTextAsync(activeFile, previous + "\n", CancellationToken.None);
                await plugins.RescanAsync(CancellationToken.None);
            });

            if (plugins.For(plugin.Id)?.Manifest.Version != plugin.Manifest.Version)
            {
                throw new SolutionStepException(
                    plugins.Refused.FirstOrDefault(r => r.Id == plugin.Id)?.Reason ?? $"{plugin.Id} {plugin.Manifest.Version} could not be made active.");
            }

            return;
        }

        var result = await pluginInstaller.InstallAsync(plugin.Folder, replace: false, ct);

        if (result.Status != 200 || !result.Installed)
        {
            throw new SolutionStepException(result.Reason ?? $"{plugin.Id} {plugin.Manifest.Version} was not installed.");
        }

        run.Made($"plugin {plugin.Id} {plugin.Manifest.Version}", async () =>
        {
            RemoveFolder(Path.Combine(directory, plugin.Manifest.Version));

            if (!existed)
            {
                RemoveFolder(directory);
            }
            else if (previous is not null)
            {
                await File.WriteAllTextAsync(activeFile, previous + "\n", CancellationToken.None);
            }

            await plugins.RescanAsync(CancellationToken.None);
        });

        await audit.WriteAsAsync(run.Actor.UserId, run.Actor.Email, TenantActions.PluginInstalled, plugin.Id, plugin.Id,
            new { id = plugin.Id, version = plugin.Manifest.Version, source = plugin.Folder, replaced = false, installed = true, solution = true }, ct);
    }

    private async Task<string> HireAsync(
        string team, SolutionMember member, SolutionPackage package, SolutionAnswers answers, string agent, string? tools,
        SolutionActor actor, Run run, CancellationToken ct)
    {
        var instructions = ResolveSolution(member.Instructions, tools);

        if (member.Role == SolutionManifest.RoleManager && member.Kind == MemberRef.AgentKind)
        {
            // THE MANAGER CAME WITH THE TEAM: it takes the package's name and instructions, and
            // goes when the team does.
            await teams.UpdateMemberAsync(
                team, TeamRegistry.DefaultManagerName, member.Name, instructions, null, actor.PromptSetter, ct,
                change => change.PromptChanged || change.Renamed
                    ? [actor.Row(TenantActions.MemberChanged, $"{team}/{TeamRegistry.DefaultManagerName}", member.Name, new { renamed = change.Renamed, solution = package.Manifest.Id })]
                    : []);
            return TeamRegistry.DefaultManagerName;
        }

        ContainerSnapshot snapshot;

        if (member.Kind == MemberRef.PluginKind)
        {
            var config = new Dictionary<string, JsonElement>(member.Settings, StringComparer.Ordinal);
            foreach (var (key, value) in AnswersFor(answers.Settings, member.Name)) config[key] = value;

            var bindings = new Dictionary<string, string>(AnswersFor(answers.Connections, member.Name), StringComparer.Ordinal);

            snapshot = await teams.HireMemberAsync(
                team, member.Name, MemberRef.PluginPrefix + member.PluginId, "", [], ct: ct,
                // THE PACKAGE'S SECRET BINDINGS land in the same hire as the member, checked by the
                // same SettingsRefusal a person's binding in the member's settings is - except that
                // a key the Host has not set yet is not a refusal: its source fails until it is set.
                // The install is a person's action, so it may bind keys no one bound on the team yet.
                settings: new PluginMemberSettings(config, new Dictionary<string, string>(member.Secrets, StringComparer.Ordinal)) { Connections = bindings },
                promptSetBy: actor.PromptSetter,
                connectionsAudit: bindings.Count == 0
                    ? null
                    : id => actor.Row(TenantActions.MemberConnectionsChanged, $"{id.Team}/{id.Name}", member.Name,
                        new { plugin = member.PluginId, slots = bindings.Keys.Order(StringComparer.Ordinal) }),
                secretsSetLater: true);
        }
        else
        {
            snapshot = await teams.HireMemberAsync(
                team, member.Name, member.Preset ?? agent, instructions, [], ct: ct, promptSetBy: actor.PromptSetter);
        }

        run.Made($"member {member.Name}", () => memberDeletion.DeleteAsync(team, snapshot.Id, CancellationToken.None));

        await audit.WriteAsAsync(actor.UserId, actor.Email, TenantActions.MemberAdded, $"{snapshot.Team}/{snapshot.Id}", snapshot.Name,
            new
            {
                resolvedAgent = snapshot.Agent,
                team = snapshot.Team,
                solution = package.Manifest.Id,
                secrets = member.Secrets.Keys.Order(StringComparer.Ordinal),
            }, ct);

        return snapshot.Id;
    }

    /// <summary>
    /// A KEPT PLUGIN MEMBER'S SECRET BINDINGS after an update, merged as <see cref="MergedSecrets"/>
    /// says, and saved - when anything changed - exactly as a person's change in the member's
    /// settings is: the same check and the same rows, in one transaction. Undone to what was there.
    /// </summary>
    private async Task RebindSecretsAsync(
        string team, string id, SolutionMember? installed, SolutionMember member, SolutionPackage package,
        SolutionActor actor, Run run, CancellationToken ct)
    {
        if (pluginSettings is null || package.Plugin(member.PluginId) is not { } plugin) return;

        var memberId = new ContainerId(team, id);
        var before = await pluginSettings.ForAsync(memberId, ct);
        var merged = MergedSecrets(before.Secrets, installed, member);

        if (merged.Count == before.Secrets.Count && merged.All(b => before.Secrets.GetValueOrDefault(b.Key) == b.Value)) return;

        var changed = before with { Secrets = merged };

        if (PluginMemberRunner.SettingsRefusal(plugin.Manifest, changed, secretStore, requireSet: false, stored: before) is { } refusal)
        {
            throw new SolutionStepException($"{member.Name}: {refusal}");
        }

        await pluginSettings.SaveAsync(
            memberId, changed,
            PluginEndpoints.PersonChangeRows(actor.UserId, actor.Email, memberId, member.Name, plugin.Id, before, changed),
            ct);

        run.Made($"member {member.Name} (secrets)", () => pluginSettings.SaveAsync(memberId, before, CancellationToken.None));
    }

    private async Task RegisterSkillAsync(string team, SolutionSkill skill, SolutionActor actor, Run run, CancellationToken ct)
    {
        await teamSkills.RegisterAsync(
            team, new SkillDraft(skill.Name, skill.Description, skill.Roles, skill.Body), actor.Label,
            actor.Row(TenantActions.SkillCreated, team, skill.Name, new { team, name = skill.Name, roles = skill.Roles, solution = true }), ct);

        run.Made($"skill {skill.Name}", () => teamSkills.DeleteAsync(team, skill.Name,
            actor.Row(TenantActions.SkillDeleted, team, skill.Name, new { team, name = skill.Name, reason = "install undone" }), CancellationToken.None));
    }

    private async Task PublishSiteAsync(string team, SolutionSite site, SolutionActor actor, Run run, bool isNew, CancellationToken ct)
    {
        if (isNew)
        {
            var made = await sites.CreateAsync(team, site.Name, actor.Site, ct);
            if (made.Value is null) throw new SolutionStepException($"The site '{site.Name}' could not be created: {made.Refusal}");

            run.Made($"site {site.Name}", async () =>
            {
                await sites.DeleteAsync(team, site.Name, confirmed: true, actor.Site, CancellationToken.None);
            });
        }

        var previous = (await sites.FindAsync(team, site.Name, null, ct)).Value?.LiveVersion;

        // PUBLISHED FROM A COPY IN THE TEAM'S FOLDER, where a site may be published from, and never
        // from the package's own folder, which its builder can still change.
        var staging = Path.Combine(paths.RootFor(team), ".solution-sites", site.Name);
        RemoveFolder(staging);
        CopyReadOnly(site.Folder, staging);

        try
        {
            var published = await sites.PublishAsync(team, site.Name, staging, actor.Site, ct);
            if (published.Value is null) throw new SolutionStepException($"The site '{site.Name}' could not be published: {published.Refusal}");
        }
        finally
        {
            RemoveFolder(Path.Combine(paths.RootFor(team), ".solution-sites"));
        }

        if (!isNew)
        {
            run.Made($"site {site.Name} (published)", async () =>
            {
                if (previous is { } version) await sites.RollbackAsync(team, site.Name, version, actor.Site, CancellationToken.None);
            });
        }
    }

    /// <summary>
    /// EACH SCHEDULE'S FIRST RUN after an install. A <c>runAtInstall</c> one is fired once now through
    /// <see cref="TriggerSweep.RunNowAsync"/> - Run now's own call, the fire its schedule makes, with its
    /// wakeManager and its daily cap, recorded as the installing person's `schedule.run-at-install` -
    /// and then comes due on its interval from now. Any other waits for the due time
    /// it was created with. A fire that does not happen is that run's outcome, not the install's.
    /// </summary>
    private async Task<IReadOnlyList<SolutionFirstRun>> FirstRunsAsync(
        SolutionManifest manifest, IReadOnlyDictionary<string, string> triggerIds,
        IReadOnlyDictionary<string, DateTimeOffset?> firstDue, SolutionActor actor, CancellationToken ct)
    {
        var runs = new List<SolutionFirstRun>();

        foreach (var trigger in manifest.Triggers.Where(t => t.Schedule is not null))
        {
            var due = firstDue.GetValueOrDefault(trigger.Name);

            if (!trigger.RunAtInstall || sweep is null || !triggerIds.TryGetValue(trigger.Name, out var id))
            {
                runs.Add(new(trigger.Name, trigger.Member, trigger.RunAtInstall, false, SolutionFirstRun.Scheduled, due, null));
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            TriggerRunNow? fired;

            try
            {
                fired = await sweep.RunNowAsync(
                    id, now, actor.UserId, actor.Email, TenantActions.ScheduleRunAtInstall, countOnFromNow: true, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                fired = null;
            }

            if (fired is { Outcome: SolutionFirstRun.Fired })
            {
                runs.Add(new(trigger.Name, trigger.Member, true, true, SolutionFirstRun.Fired, now, fired.Trigger.NextDueAt));
            }
            else
            {
                runs.Add(new(trigger.Name, trigger.Member, true, false, fired?.Outcome ?? SolutionFirstRun.Failed, fired?.Trigger.NextDueAt ?? due, null));
            }
        }

        return runs;
    }

    private async Task<TriggerRow> CreateTriggerAsync(
        string team, SolutionTrigger trigger, IReadOnlyDictionary<string, string> memberIds, string? tools,
        SolutionActor actor, Run run, CancellationToken ct)
    {
        var request = new NewTrigger(
            trigger.Name,
            memberIds.GetValueOrDefault(trigger.Member) ?? trigger.Member,
            ResolveSolution(trigger.Instruction, tools),
            trigger.PlatformKind switch
            {
                TriggerKind.Cron => "cron",
                TriggerKind.Every => "every",
                TriggerKind.FolderChange => "folderChange",
                _ => "event",
            },
            Expression: trigger.Schedule?.Cron,
            Timezone: trigger.Schedule?.Cron is null ? null : trigger.Schedule.Timezone ?? "UTC",
            IntervalSeconds: trigger.Schedule?.EverySeconds,
            IdleOnly: trigger.IdleOnly,
            EventType: trigger.Event?.Type,
            Filter: trigger.Event?.Filter,
            WatchRoot: trigger.Folder is null ? null : FolderWatchScope.DocumentsRoot,
            WatchPath: trigger.Folder?.Path,
            WatchGlob: trigger.Folder?.Glob,
            WakeManager: trigger.WakeManager,
            DailyTokenCap: trigger.DailyTokenCap);

        var created = await triggers.CreateAsync(team, request, actor.Label,
            row => actor.Row(TenantActions.ScheduleCreated, row.Id, row.Name, new { team = row.Team, member = row.Container, solution = true }), ct);

        if (created.Row is not { } made) throw new SolutionStepException($"The trigger '{trigger.Name}' was refused: {created.Refusal}");

        run.Made($"trigger {trigger.Name}", () => triggers.DeleteAsync(made.Id,
            actor.Row(TenantActions.ScheduleDeleted, made.Id, made.Name, new { team, reason = "install undone" }), CancellationToken.None));

        return made;
    }

    /// <summary>Each document input's folder, made now so a folder trigger can watch it and a
    /// person can upload into it; an empty one made here goes again when the install is undone.</summary>
    private void EnsureDocumentFolders(string team, SolutionManifest manifest, Run run)
    {
        var root = documents.RootFor(team);
        var wanted = manifest.Inputs.Documents.Select(d => d.Folder)
            .Concat(manifest.Triggers.Where(t => t.Folder is not null).Select(t => t.Folder!.Path))
            .Distinct(StringComparer.Ordinal);

        foreach (var folder in wanted)
        {
            var path = Path.Combine(root, folder);
            if (Directory.Exists(path)) continue;

            documents.CreateFolder(team, folder);
            run.Made($"documents folder {folder}", () =>
            {
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>The operator CLI's staged files, copied into their input folders.</summary>
    private async Task CopyDocumentsAsync(string team, SolutionAnswers answers, SolutionActor actor, CancellationToken ct)
    {
        foreach (var (folder, files) in answers.Documents ?? new Dictionary<string, List<string>>())
        {
            var saved = new List<string>();

            foreach (var file in files.Where(File.Exists))
            {
                await using var content = File.OpenRead(file);
                saved.Add((await documents.SaveAsync(team, folder, Path.GetFileName(file), content, ct)).Path);
            }

            if (saved.Count > 0) await folders.AnnounceAsync(team, folder, saved, actor.Label, ct);
        }
    }

    /// <summary>
    /// The person's answers, checked before anything is written: only settings and slots the
    /// package asks for, each suitable, and a required setting with no default answered - a plugin
    /// member cannot be hired without one. A skipped required document or connection is allowed:
    /// the team shows it as missing until it is provided.
    /// </summary>
    private string? AnswersRefusal(SolutionPackage package, SolutionAnswers answers, bool update)
    {
        var manifest = package.Manifest;

        foreach (var (memberName, values) in answers.Settings ?? new Dictionary<string, Dictionary<string, JsonElement>>())
        {
            foreach (var (setting, value) in values)
            {
                if (!manifest.Inputs.Settings.Any(i => Same(i.Member, memberName) && i.Setting == setting))
                {
                    return $"The package does not ask for '{memberName}''s setting '{setting}'.";
                }

                var field = package.Plugin(manifest.Member(memberName)?.PluginId)?.Manifest.Config.GetValueOrDefault(setting);
                if (field?.Refusal(setting, value) is { } invalid) return $"'{memberName}': {invalid}";
            }
        }

        foreach (var input in manifest.Inputs.Settings.Where(i => i.Required))
        {
            var field = package.Plugin(manifest.Member(input.Member)?.PluginId)?.Manifest.Config.GetValueOrDefault(input.Setting);
            var answered = AnswersFor(answers.Settings, input.Member).ContainsKey(input.Setting);

            if (!update && !answered && field is { Default: null })
            {
                return $"'{input.Member}''s setting '{input.Setting}' ({input.Description}) has no default, and its plugin cannot be hired without it. Provide it to install.";
            }
        }

        foreach (var (memberName, slots) in answers.Connections ?? new Dictionary<string, Dictionary<string, string>>())
        {
            foreach (var slot in slots.Keys)
            {
                if (!manifest.Inputs.Connections.Any(i => Same(i.Member, memberName) && i.Slot == slot))
                {
                    return $"The package does not ask for '{memberName}''s connection slot '{slot}'.";
                }
            }
        }

        foreach (var folder in (answers.Documents ?? new Dictionary<string, List<string>>()).Keys)
        {
            if (!manifest.Inputs.Documents.Any(d => d.Folder == folder)) return $"The package does not ask for documents in '{folder}'.";
        }

        return null;
    }

    /// <summary>Each connection the person chose must suit its slot's providers and hold its
    /// scopes, as a person's own hire is checked.</summary>
    private async Task<string?> BindingsRefusalAsync(SolutionPackage package, SolutionAnswers answers, CancellationToken ct)
    {
        if (connections is null) return null;

        foreach (var (memberName, bindings) in answers.Connections ?? new Dictionary<string, Dictionary<string, string>>())
        {
            if (bindings.Count == 0) continue;
            if (package.Plugin(package.Manifest.Member(memberName)?.PluginId) is not { } plugin) continue;

            if (await connections.BindingRefusalAsync(plugin.Manifest, bindings, null, ct) is { } refusal)
            {
                return $"'{memberName}': {refusal.Error}";
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, T> AnswersFor<T>(IReadOnlyDictionary<string, Dictionary<string, T>>? answers, string member) =>
        answers?.FirstOrDefault(a => Same(a.Key, member)).Value is { } found
            ? found
            : new Dictionary<string, T>(StringComparer.Ordinal);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool MemberExists(string team, string id) =>
        teams.ContainerIdsOf(team).Any(c => string.Equals(c.Name, id, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>{solution}</c> resolved to the installed tools folder.</summary>
    public static string ResolveSolution(string text, string? tools) =>
        tools is null ? text : text.Replace(SolutionManifest.SolutionToken, tools, StringComparison.Ordinal);

    /// <summary>SHA-256 of each skill body, each site's files and each tool file.</summary>
    public static IReadOnlyDictionary<string, string> Digests(SolutionPackage package)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var skill in package.Skills) digests["skill:" + skill.Name] = Hash(Encoding.UTF8.GetBytes($"{skill.Description}\n{string.Join(' ', skill.Roles)}\n{skill.Body}"));

        foreach (var site in package.Sites)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in site.Files)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(file + "\0"));
                hash.AppendData(File.ReadAllBytes(Path.Combine(site.Folder, file)));
            }

            digests["site:" + site.Name] = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        if (package.ToolsFolder is { } tools)
        {
            foreach (var file in package.Tools) digests["tool:" + file] = Hash(File.ReadAllBytes(Path.Combine(tools, file)));
        }

        return digests;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Undoes what the run made and answers the step that failed. The tenant row's subject
    /// is the team's NAME, as the person gave it: after the undo there may be no team to name by id.</summary>
    private async Task<SolutionOutcome> FailedAsync(Run run, Exception exception, SolutionManifest manifest, string team)
    {
        var (undone, notUndone) = await run.UndoAsync();
        var step = run.Current;
        var number = Steps.Select((s, i) => (s.Step, i)).First(s => s.Step == step).i + 1;
        var title = Steps[number - 1].Title;
        var reason = exception is SolutionStepException or ArgumentException or InvalidOperationException
            or NoSuchAgentException or PluginSettingsException or TeamNameTakenException or KeyNotFoundException
            or SkillRefusedException or RepoSetupRefusedException
            ? exception.Message
            : $"{exception.GetType().Name}: {exception.Message}";

        await audit.WriteAsAsync(run.Actor.UserId, run.Actor.Email, TenantActions.SolutionFailed, team, manifest.Name,
            new { id = manifest.Id, version = manifest.Version, step, reason, undone, notUndone }, CancellationToken.None);

        return new(200, new SolutionStepFailed(false, step, number, title, reason, undone, notUndone, run.Done));
    }

    // ------------------------------------------------------------------ files

    /// <summary>
    /// Copies a package folder the way a plugin install does: the Host's files with the agent's
    /// group, directories 0750, files 0640, a file executable in the package 0750 - so an agent reads
    /// and runs them and cannot change them. Links are not copied; the check refused any leaving it.
    /// </summary>
    private void CopyReadOnly(string from, string to)
    {
        MakeDirectory(to);

        foreach (var entry in Directory.EnumerateFileSystemEntries(from))
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.')) continue;

            var info = new FileInfo(entry);
            if (info.LinkTarget is not null) continue;

            var destination = Path.Combine(to, name);

            if (Directory.Exists(entry))
            {
                CopyReadOnly(entry, destination);
            }
            else
            {
                File.Copy(entry, destination);
                var executable = !OperatingSystem.IsWindows()
                    && (File.GetUnixFileMode(entry) & UnixFileMode.UserExecute) != 0;
                Own(destination, executable ? ExecutableMode : FileMode);
            }
        }

        Own(to, DirectoryMode);
    }

    private void MakeDirectory(string directory)
    {
        if (Directory.Exists(directory)) return;
        Directory.CreateDirectory(directory);
    }

    private void Own(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        if (group >= 0) _ = lchown(path, -1, group);
        File.SetUnixFileMode(path, mode);
    }

    /// <summary>Removes a folder the Host made itself (tools, staging): its own files, so no agent
    /// pass is needed.</summary>
    private static void RemoveFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;

        foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Prepend(folder))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, DirectoryMode);
        }

        Directory.Delete(folder, recursive: true);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int lchown(string path, int owner, int group);

    /// <summary>One install's progress: the step it is on, the steps done, and what to undo.</summary>
    private sealed class Run(SolutionActor actor, Func<string, Task>? afterStep)
    {
        private readonly Stack<(string What, Func<Task> Undo)> _made = new();

        private readonly List<SolutionStep> _done = [];

        public SolutionActor Actor => actor;

        public string Current { get; private set; } = StepPlugins;

        public IReadOnlyList<SolutionStep> Done => _done;

        public async Task StepAsync(string step, Func<Task> work)
        {
            Current = step;
            await work();
            if (afterStep is not null) await afterStep(step);

            var number = _done.Count + 1;
            _done.Add(new SolutionStep(step, Steps.Select((s, i) => (s.Step, i)).First(s => s.Step == step).i + 1,
                Steps.First(s => s.Step == step).Title, true));
            _ = number;
        }

        public void Made(string what, Func<Task> undo) => _made.Push((what, undo));

        /// <summary>Undoes everything made, newest first. An undo that fails is named, and the rest
        /// still run.</summary>
        public async Task<(IReadOnlyList<string> Undone, IReadOnlyList<string> NotUndone)> UndoAsync()
        {
            var undone = new List<string>();
            var notUndone = new List<string>();

            while (_made.TryPop(out var made))
            {
                try
                {
                    await made.Undo();
                    undone.Add(made.What);
                }
                catch (Exception exception)
                {
                    notUndone.Add($"{made.What}: {exception.Message}");
                }
            }

            return (undone, notUndone);
        }
    }
}

/// <summary>How the install tells open browsers a team came or went (the Host's team list push).
/// <paramref name="BeforeDeleteAsync"/> reads who may see the team while it still exists and answers
/// what to call once it is gone.</summary>
public sealed record TeamAnnouncements(Func<string, Task> CreatedAsync, Func<string, Task<Func<Task>>> BeforeDeleteAsync);

/// <summary>A step refused with a sentence a person reads.</summary>
public sealed class SolutionStepException(string message) : Exception(message);
