using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host.Solutions;

/// <summary>
/// One thing wrong with a solution package: the FILE it is in (relative to the package, with
/// <c>/</c>), the FIELD inside that file (a JSON path such as <c>triggers[2].member</c>, or the
/// front-matter key of a skill), and a sentence a person can act on. Every refusal names both, so a
/// person - or the agent that wrote the package - goes straight to the line to change.
/// </summary>
public sealed record SolutionRefusal(string File, string Field, string Reason)
{
    public override string ToString() => $"{File} {Field}: {Reason}";
}

/// <summary>
/// <c>solution.json</c>, format 1: a whole working team in one folder. See <c>docs/solutions.md</c>.
///
/// <para>
/// This is the FILE, parsed and checked on its own terms - shapes, required fields, names, the
/// references between its own sections. What needs the rest of the package (a plugin's manifest, a
/// skill's file, a site's folder) or the platform (the event catalog, the Agent presets, the
/// built-in skills) is <see cref="SolutionChecker"/>'s.
/// </para>
///
/// <para>
/// OPEN FOR LATER FORMATS WITHOUT A NEW NUMBER: a top-level key this format does not know is kept in
/// <see cref="Extra"/> and named in <see cref="Ignored"/>, never refused, so a package written for a
/// later Host (a control panel, status lines) still checks here. <c>format</c> is the hard gate.
/// </para>
/// </summary>
public sealed record SolutionManifest(
    int Format,
    string Id,
    string Name,
    string Version,
    string Description,
    SolutionTeam Team,
    IReadOnlyList<SolutionMember> Members,
    IReadOnlyList<SolutionTrigger> Triggers,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Sites,
    SolutionInputs Inputs,
    IReadOnlyDictionary<string, JsonElement> Extra)
{
    public const string FileName = "solution.json";

    public const int CurrentFormat = 1;

    /// <summary>The instruction token naming the installed <c>tools/</c> folder.</summary>
    public const string SolutionToken = "{solution}";

    public const string RoleManager = "manager";
    public const string RoleMember = "member";

    public const string KindSchedule = "schedule";
    public const string KindEvent = "event";
    public const string KindFolder = "folder";

    public static IReadOnlySet<string> KnownKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "format", "id", "name", "version", "description", "team", "members", "triggers", "skills",
        "sites", "inputs", "panel",
    };

    /// <summary>What the solution's control panel and launcher tile show: its <c>panel</c> key.</summary>
    public SolutionPanel Panel { get; init; } = SolutionPanel.None;

    /// <summary>Unknown top-level keys, in file order: kept, never acted on.</summary>
    public IReadOnlyList<string> Ignored => [.. Extra.Keys];

    /// <summary>The package member named <paramref name="name"/>, matched as member names are
    /// everywhere: case-insensitively.</summary>
    public SolutionMember? Member(string? name) =>
        name is null ? null : Members.FirstOrDefault(m => string.Equals(m.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads <c>solution.json</c>. Answers the manifest, or null with every refusal found - not the
    /// first only, so one round trip shows a package's author all of it. Never throws on content.
    /// </summary>
    public static (SolutionManifest? Manifest, IReadOnlyList<SolutionRefusal> Refusals) Parse(string json)
    {
        var read = Read(json);
        return read.Refusals.Count > 0 ? (null, read.Refusals) : (read.Manifest, []);
    }

    /// <summary>
    /// Reads <c>solution.json</c> as far as it can be read. Past the format gate the answer carries a
    /// manifest even when there are refusals - the parts that read cleanly, with each refused item
    /// left out - so the checker can go on to check what is left and report every refusal at once.
    /// Only <see cref="Parse"/>'s whole manifest may be installed.
    /// </summary>
    public static SolutionManifestRead Read(string json)
    {
        var read = new Reader();
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException exception)
        {
            read.Refuse("(file)", $"{FileName} is not JSON: {exception.Message}");
            return read.Answer(null);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                read.Refuse("(file)", $"{FileName} must be a JSON object.");
                return read.Answer(null);
            }

            // THE HARD GATE, and the only one that stops the read: a format this Host does not know may
            // mean anything below.
            if (!root.TryGetProperty("format", out var formatElement) || formatElement.ValueKind != JsonValueKind.Number
                || !formatElement.TryGetInt32(out var format))
            {
                read.Refuse("format", $"`format` is required and must be a number ({CurrentFormat}).");
                return read.Answer(null);
            }

            if (format != CurrentFormat)
            {
                read.Refuse("format", $"`format` {format} is not one this Host reads (it reads {CurrentFormat}).");
                return read.Answer(null);
            }

            var id = read.Required(root, "id", "id");
            if (id is not null && !MemberRef.IsValidPluginId(id))
            {
                read.Refuse("id", $"`id` '{id}' must be a slug: lowercase letters, digits and hyphens, starting with a letter or digit, at most 48 characters.");
            }

            var name = read.Required(root, "name", "name");
            if (name is not null && name.Length > TeamRegistry.MaximumLabelLength)
            {
                read.Refuse("name", $"`name` is longer than {TeamRegistry.MaximumLabelLength} characters.");
            }

            var version = read.Required(root, "version", "version");
            if (version is not null && !IsVersion(version))
            {
                read.Refuse("version", $"`version` '{version}' must be a plain version such as 1.0.0.");
            }

            var description = read.Required(root, "description", "description");

            var team = ReadTeam(read, root, name);
            var members = ReadMembers(read, root);
            var triggers = ReadTriggers(read, root, members);
            var skills = ReadNames(read, root, "skills", "skill");
            var sites = ReadNames(read, root, "sites", "site");
            var inputs = ReadInputs(read, root, members);
            var panel = ReadPanel(read, root, members, sites);

            var extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!KnownKeys.Contains(property.Name)) extra[property.Name] = property.Value.Clone();
            }

            return read.Answer(new SolutionManifest(
                format, id ?? "", name ?? "", version ?? "", description ?? "", team, members, triggers, skills, sites, inputs, extra)
            {
                Panel = panel,
            });
        }
    }

    private static SolutionTeam ReadTeam(Reader read, JsonElement root, string? packageName)
    {
        if (!root.TryGetProperty("team", out var team) || team.ValueKind == JsonValueKind.Null)
        {
            return new SolutionTeam(packageName ?? "", "");
        }

        if (team.ValueKind != JsonValueKind.Object)
        {
            read.Refuse("team", "`team` must be an object: { \"name\": ..., \"instructions\": ... }.");
            return new SolutionTeam(packageName ?? "", "");
        }

        var name = read.Optional(team, "name", "team.name") ?? packageName ?? "";

        if (name.Length > TeamRegistry.MaximumLabelLength)
        {
            read.Refuse("team.name", $"`team.name` is longer than {TeamRegistry.MaximumLabelLength} characters.");
        }
        else if (name.Length > 0 && ContainerId.DeriveName(name) is null)
        {
            read.Refuse("team.name", $"`team.name` '{name}' has no letters or digits to make a team name from.");
        }

        return new SolutionTeam(name, read.Optional(team, "instructions", "team.instructions") ?? "");
    }

    private static List<SolutionMember> ReadMembers(Reader read, JsonElement root)
    {
        var members = new List<SolutionMember>();

        if (!root.TryGetProperty("members", out var array) || array.ValueKind == JsonValueKind.Null) return members;

        if (array.ValueKind != JsonValueKind.Array)
        {
            read.Refuse("members", "`members` must be an array.");
            return members;
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var at = $"members[{index++}]";

            if (item.ValueKind != JsonValueKind.Object)
            {
                read.Refuse(at, $"`{at}` must be an object.");
                continue;
            }

            var name = read.Required(item, "name", $"{at}.name");

            if (name is not null)
            {
                if (name.Length > TeamRegistry.MaximumLabelLength)
                {
                    read.Refuse($"{at}.name", $"`{at}.name` is longer than {TeamRegistry.MaximumLabelLength} characters.");
                }
                else if (ContainerId.DeriveName(name) is not { } derived)
                {
                    read.Refuse($"{at}.name", $"`{at}.name` '{name}' has no letters or digits to make a member name from.");
                }
                else if (members.FirstOrDefault(m => string.Equals(ContainerId.DeriveName(m.Name), derived, StringComparison.OrdinalIgnoreCase)) is { } clash)
                {
                    read.Refuse($"{at}.name", $"`{at}.name` '{name}' is the same member name as '{clash.Name}'.");
                }
            }

            var pluginId = read.Optional(item, "pluginId", $"{at}.pluginId");
            var kind = read.Optional(item, "kind", $"{at}.kind") ?? (pluginId is null ? MemberRef.AgentKind : MemberRef.PluginKind);

            if (kind is not (MemberRef.AgentKind or MemberRef.PluginKind))
            {
                read.Refuse($"{at}.kind", $"`{at}.kind` '{kind}' must be agent or plugin.");
                continue;
            }

            if (kind == MemberRef.PluginKind)
            {
                if (pluginId is null)
                {
                    read.Refuse($"{at}.pluginId", $"`{at}.pluginId` is required for a plugin member.");
                }
                else if (!MemberRef.IsValidPluginId(pluginId))
                {
                    read.Refuse($"{at}.pluginId", $"`{at}.pluginId` '{pluginId}' is not a plugin id.");
                }

                foreach (var agentOnly in new[] { "role", "preset", "instructions" })
                {
                    if (item.TryGetProperty(agentOnly, out var present) && present.ValueKind != JsonValueKind.Null)
                    {
                        read.Refuse($"{at}.{agentOnly}", $"`{at}.{agentOnly}` is an agent member's; a plugin member runs no model and takes none.");
                    }
                }

                var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

                if (item.TryGetProperty("settings", out var settingsElement) && settingsElement.ValueKind != JsonValueKind.Null)
                {
                    if (settingsElement.ValueKind != JsonValueKind.Object)
                    {
                        read.Refuse($"{at}.settings", $"`{at}.settings` must be an object of setting name to value.");
                    }
                    else
                    {
                        foreach (var setting in settingsElement.EnumerateObject()) settings[setting.Name] = setting.Value.Clone();
                    }
                }

                var secrets = ReadSecrets(read, item, at);

                if (name is not null && pluginId is not null)
                {
                    members.Add(new SolutionMember(name, MemberRef.PluginKind, RoleMember, null, "", pluginId, settings) { Secrets = secrets });
                    read.Placed("members", index - 1);
                }

                continue;
            }

            if (pluginId is not null)
            {
                read.Refuse($"{at}.pluginId", $"`{at}.pluginId` belongs to a plugin member; this one is an agent.");
            }

            if (item.TryGetProperty("settings", out var agentSettings) && agentSettings.ValueKind != JsonValueKind.Null)
            {
                read.Refuse($"{at}.settings", $"`{at}.settings` belongs to a plugin member; an agent member takes instructions.");
            }

            if (item.TryGetProperty("secrets", out var agentSecrets) && agentSecrets.ValueKind != JsonValueKind.Null)
            {
                read.Refuse($"{at}.secrets", $"`{at}.secrets` belongs to a plugin member; an agent member binds no secrets.");
            }

            var role = (read.Optional(item, "role", $"{at}.role") ?? RoleMember).ToLowerInvariant();

            if (role is not (RoleManager or RoleMember))
            {
                read.Refuse($"{at}.role", $"`{at}.role` '{role}' must be manager or member.");
            }
            else if (role == RoleManager && members.FirstOrDefault(m => m.Role == RoleManager) is { } manager)
            {
                read.Refuse($"{at}.role", $"`{at}.role` makes a second manager; '{manager.Name}' is already the team's Manager.");
            }

            var preset = read.Optional(item, "preset", $"{at}.preset");
            var instructions = read.Optional(item, "instructions", $"{at}.instructions") ?? "";

            if (name is not null)
            {
                members.Add(new SolutionMember(
                    name, MemberRef.AgentKind, role, preset, instructions, null,
                    new Dictionary<string, JsonElement>(StringComparer.Ordinal)));
                read.Placed("members", index - 1);
            }
        }

        return members;
    }

    private static List<SolutionTrigger> ReadTriggers(Reader read, JsonElement root, IReadOnlyList<SolutionMember> members)
    {
        var triggers = new List<SolutionTrigger>();

        if (!root.TryGetProperty("triggers", out var array) || array.ValueKind == JsonValueKind.Null) return triggers;

        if (array.ValueKind != JsonValueKind.Array)
        {
            read.Refuse("triggers", "`triggers` must be an array.");
            return triggers;
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var at = $"triggers[{index++}]";

            if (item.ValueKind != JsonValueKind.Object)
            {
                read.Refuse(at, $"`{at}` must be an object.");
                continue;
            }

            var refusalsBefore = read.Refusals.Count;
            var name = read.Required(item, "name", $"{at}.name");

            if (name is not null && triggers.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                read.Refuse($"{at}.name", $"`{at}.name` '{name}' is used by another trigger.");
            }

            var member = read.Required(item, "member", $"{at}.member");

            // THE PACKAGE'S OWN NAME FOR THE MEMBER, not a team's: the team does not exist yet.
            if (member is not null && members.All(m => !string.Equals(m.Name, member, StringComparison.OrdinalIgnoreCase)))
            {
                var known = members.Count == 0 ? "the package has no members" : "its members are " + string.Join(", ", members.Select(m => m.Name));
                read.Refuse($"{at}.member", $"`{at}.member` '{member}' names no member of this package; {known}.");
            }

            var instruction = read.Required(item, "instruction", $"{at}.instruction", trim: false);

            var wakeManager = WakeManagerPolicy.OnHandbackOrFailure;
            if (read.Optional(item, "wakeManager", $"{at}.wakeManager") is { } wake)
            {
                if (WakeManagerPolicy.Parse(wake) is { } parsed) wakeManager = parsed;
                else read.Refuse($"{at}.wakeManager", $"`{at}.wakeManager` '{wake}' must be one of: {string.Join(", ", WakeManagerPolicy.All)}.");
            }

            long? cap = null;
            if (item.TryGetProperty("dailyTokenCap", out var capElement) && capElement.ValueKind != JsonValueKind.Null)
            {
                if (capElement.ValueKind != JsonValueKind.Number || !capElement.TryGetInt64(out var capValue) || capValue < 1)
                {
                    read.Refuse($"{at}.dailyTokenCap", $"`{at}.dailyTokenCap` must be a whole number of tokens, at least 1, or null for no cap.");
                }
                else
                {
                    cap = capValue;
                }
            }

            var idleOnly = true;
            if (item.TryGetProperty("idleOnly", out var idleElement) && idleElement.ValueKind != JsonValueKind.Null)
            {
                if (idleElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    read.Refuse($"{at}.idleOnly", $"`{at}.idleOnly` must be true or false.");
                }
                else
                {
                    idleOnly = idleElement.ValueKind == JsonValueKind.True;
                }
            }

            var runAtInstall = false;
            if (item.TryGetProperty("runAtInstall", out var runElement) && runElement.ValueKind != JsonValueKind.Null)
            {
                if (runElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    read.Refuse($"{at}.runAtInstall", $"`{at}.runAtInstall` must be true or false.");
                }
                else
                {
                    runAtInstall = runElement.ValueKind == JsonValueKind.True;
                }
            }

            var kind = read.Required(item, "kind", $"{at}.kind")?.ToLowerInvariant();
            SolutionSchedule? schedule = null;
            SolutionEvent? onEvent = null;
            SolutionFolder? folder = null;

            switch (kind)
            {
                case null:
                    break;

                case KindSchedule:
                    schedule = ReadSchedule(read, item, at);
                    break;

                case KindEvent:
                    onEvent = ReadEvent(read, item, at);
                    break;

                case KindFolder:
                    folder = ReadFolder(read, item, at);
                    break;

                default:
                    read.Refuse($"{at}.kind", $"`{at}.kind` '{kind}' must be schedule, event or folder.");
                    break;
            }

            if (read.Refusals.Count == refusalsBefore)
            {
                triggers.Add(new SolutionTrigger(
                    name!, kind!, member!, instruction!, wakeManager, cap, idleOnly, schedule, onEvent, folder)
                {
                    RunAtInstall = runAtInstall,
                });
                read.Placed("triggers", index - 1);
            }
        }

        return triggers;
    }

    private static SolutionSchedule? ReadSchedule(Reader read, JsonElement item, string at)
    {
        var field = $"{at}.schedule";

        if (!item.TryGetProperty("schedule", out var schedule) || schedule.ValueKind != JsonValueKind.Object)
        {
            read.Refuse(field, $"`{field}` is required for a schedule trigger: {{ \"cron\": \"0 0 8 * * 1-5\", \"timezone\": \"Europe/London\" }} or {{ \"everySeconds\": 3600 }}.");
            return null;
        }

        var cron = read.Optional(schedule, "cron", $"{field}.cron");
        var timezone = read.Optional(schedule, "timezone", $"{field}.timezone");
        int? every = null;

        if (schedule.TryGetProperty("everySeconds", out var everyElement) && everyElement.ValueKind != JsonValueKind.Null)
        {
            if (everyElement.ValueKind != JsonValueKind.Number || !everyElement.TryGetInt32(out var seconds))
            {
                read.Refuse($"{field}.everySeconds", $"`{field}.everySeconds` must be a whole number of seconds.");
                return null;
            }

            every = seconds;
        }

        if ((cron is null) == (every is null))
        {
            read.Refuse(field, $"`{field}` needs exactly one of `cron` and `everySeconds`.");
            return null;
        }

        if (every is not null)
        {
            if (timezone is not null)
            {
                read.Refuse($"{field}.timezone", $"`{field}.timezone` belongs to a cron schedule; an every-N schedule has none.");
                return null;
            }

            if (Harness.Host.Triggers.Validate(nameof(TriggerKind.Every), null, null, every, null, null) is not null)
            {
                read.Refuse($"{field}.everySeconds", $"`{field}.everySeconds` must be at least {Harness.Host.Triggers.MinimumIntervalSeconds}.");
                return null;
            }

            return new SolutionSchedule(TriggerKind.Every, null, null, every);
        }

        // A CRON WITH NO TIMEZONE RUNS IN UTC, and says so on the review.
        timezone ??= "UTC";

        if (Harness.Host.Triggers.Validate(nameof(TriggerKind.Cron), cron, timezone, null, null, null) is { } refusal)
        {
            var key = refusal.Contains("timezone", StringComparison.OrdinalIgnoreCase) ? "timezone" : "cron";
            read.Refuse($"{field}.{key}", $"`{field}.{key}`: {refusal} A cron here has six fields, seconds first: \"0 0 8 * * 1-5\".");
            return null;
        }

        return new SolutionSchedule(TriggerKind.Cron, cron, timezone, null);
    }

    private static SolutionEvent? ReadEvent(Reader read, JsonElement item, string at)
    {
        var field = $"{at}.event";

        if (!item.TryGetProperty("event", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            read.Refuse(field, $"`{field}` is required for an event trigger: {{ \"type\": \"site.action\", \"filter\": \"siteAction eq tracker/apply\" }}.");
            return null;
        }

        var type = read.Required(element, "type", $"{field}.type");
        var filter = read.Optional(element, "filter", $"{field}.filter");

        if (filter is not null && TriggerFilter.Parse(filter) is null)
        {
            read.Refuse($"{field}.filter", $"`{field}.filter` reads `field op value`, where op is `eq` or `contains`.");
            return null;
        }

        return type is null ? null : new SolutionEvent(type, filter);
    }

    private static SolutionFolder? ReadFolder(Reader read, JsonElement item, string at)
    {
        var field = $"{at}.folder";

        if (!item.TryGetProperty("folder", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            read.Refuse(field, $"`{field}` is required for a folder trigger: {{ \"path\": \"Inbox\", \"glob\": \"*.pdf\" }}, inside the team's documents.");
            return null;
        }

        var path = read.Optional(element, "path", $"{field}.path") ?? "";

        if (RelativeRefusal($"{field}.path", path, allowEmpty: true) is { } escape)
        {
            read.Refuse($"{field}.path", escape);
            return null;
        }

        return new SolutionFolder(path.Trim('/'), read.Optional(element, "glob", $"{field}.glob"));
    }

    private static List<string> ReadNames(Reader read, JsonElement root, string key, string what)
    {
        var names = new List<string>();

        if (!root.TryGetProperty(key, out var array) || array.ValueKind == JsonValueKind.Null) return names;

        if (array.ValueKind != JsonValueKind.Array)
        {
            read.Refuse(key, $"`{key}` must be an array of {what} names.");
            return names;
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var at = $"{key}[{index++}]";

            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                read.Refuse(at, $"`{at}` must be a {what} name.");
                continue;
            }

            var name = item.GetString()!.Trim();

            if (name.Contains('/') || name.Contains('\\') || name is "." or "..")
            {
                read.Refuse(at, $"`{at}` '{name}' is a path; name the {what}, and its files are found under {key}/.");
                continue;
            }

            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                read.Refuse(at, $"`{at}` names '{name}' twice.");
                continue;
            }

            names.Add(name);
            read.Placed(key, index - 1);
        }

        return names;
    }

    private static SolutionInputs ReadInputs(Reader read, JsonElement root, IReadOnlyList<SolutionMember> members)
    {
        var settings = new List<SolutionSettingInput>();
        var connections = new List<SolutionConnectionInput>();
        var documents = new List<SolutionDocumentInput>();

        if (!root.TryGetProperty("inputs", out var inputs) || inputs.ValueKind == JsonValueKind.Null)
        {
            return new SolutionInputs(settings, connections, documents);
        }

        if (inputs.ValueKind != JsonValueKind.Object)
        {
            read.Refuse("inputs", "`inputs` must be an object with `settings`, `connections` and `documents`.");
            return new SolutionInputs(settings, connections, documents);
        }

        foreach (var (item, at) in Items(read, inputs, "settings", "inputs.settings"))
        {
            var member = PluginMemberOf(read, item, at, members);
            var setting = read.Required(item, "setting", $"{at}.setting");
            var description = read.Optional(item, "description", $"{at}.description") ?? "";

            if (member is not null && setting is not null)
            {
                settings.Add(new SolutionSettingInput(member, setting, description, RequiredFlag(read, item, at)));
                read.Placed("inputs.settings", Position(at));
            }
        }

        foreach (var (item, at) in Items(read, inputs, "connections", "inputs.connections"))
        {
            var member = PluginMemberOf(read, item, at, members);
            var slot = read.Required(item, "slot", $"{at}.slot");
            var description = read.Optional(item, "description", $"{at}.description") ?? "";

            if (member is not null && slot is not null)
            {
                connections.Add(new SolutionConnectionInput(member, slot, description, RequiredFlag(read, item, at)));
                read.Placed("inputs.connections", Position(at));
            }
        }

        foreach (var (item, at) in Items(read, inputs, "documents", "inputs.documents"))
        {
            var folder = read.Required(item, "folder", $"{at}.folder");
            var description = read.Required(item, "description", $"{at}.description");

            if (folder is not null && RelativeRefusal($"{at}.folder", folder, allowEmpty: false) is { } escape)
            {
                read.Refuse($"{at}.folder", escape);
                continue;
            }

            if (folder is not null && documents.Any(d => string.Equals(d.Folder, folder.Trim('/'), StringComparison.Ordinal)))
            {
                read.Refuse($"{at}.folder", $"`{at}.folder` '{folder}' is asked for twice.");
                continue;
            }

            if (folder is not null && description is not null)
            {
                documents.Add(new SolutionDocumentInput(folder.Trim('/'), description, RequiredFlag(read, item, at)));
            }
        }

        return new SolutionInputs(settings, connections, documents);
    }

    /// <summary>
    /// <c>panel</c>: the primary site the tile opens, the documents folders the panel lists as
    /// results, the plugin settings it shows first, and the status line template. What needs the
    /// plugins' manifests (a setting the plugin has) is the checker's.
    /// </summary>
    private static SolutionPanel ReadPanel(Reader read, JsonElement root, IReadOnlyList<SolutionMember> members, IReadOnlyList<string> sites)
    {
        if (!root.TryGetProperty("panel", out var panel) || panel.ValueKind == JsonValueKind.Null) return SolutionPanel.None;

        if (panel.ValueKind != JsonValueKind.Object)
        {
            read.Refuse("panel", "`panel` must be an object with `primarySite`, `outputs`, `settings` and `status`.");
            return SolutionPanel.None;
        }

        // NAMED BUT REFUSED is not the same as not named: the status line's "no primarySite"
        // refusal is for a package that names none, and would only mislead beside this one.
        var primarySiteNamed = panel.TryGetProperty("primarySite", out var named) && named.ValueKind != JsonValueKind.Null;
        var primarySite = read.Optional(panel, "primarySite", "panel.primarySite");
        if (primarySite is not null && !sites.Contains(primarySite, StringComparer.OrdinalIgnoreCase))
        {
            read.Refuse("panel.primarySite", $"`panel.primarySite` '{primarySite}' is not one of this package's `sites`.");
            primarySite = null;
        }
        else if (primarySite is not null)
        {
            primarySite = sites.First(s => string.Equals(s, primarySite, StringComparison.OrdinalIgnoreCase));
        }

        var outputs = new List<string>();
        if (panel.TryGetProperty("outputs", out var outputArray) && outputArray.ValueKind != JsonValueKind.Null)
        {
            if (outputArray.ValueKind != JsonValueKind.Array)
            {
                read.Refuse("panel.outputs", "`panel.outputs` must be an array of documents folder names, such as \"Applications\".");
            }
            else
            {
                var index = 0;
                foreach (var item in outputArray.EnumerateArray())
                {
                    var at = $"panel.outputs[{index++}]";

                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        read.Refuse(at, $"`{at}` must be a documents folder name.");
                        continue;
                    }

                    var folder = item.GetString()!;

                    if (OutputRefusal(at, folder) is { } refusal)
                    {
                        read.Refuse(at, refusal);
                        continue;
                    }

                    var trimmed = folder.Trim().Trim('/');
                    if (outputs.Contains(trimmed, StringComparer.Ordinal))
                    {
                        read.Refuse(at, $"`{at}` names '{trimmed}' twice.");
                        continue;
                    }

                    outputs.Add(trimmed);
                }
            }
        }

        var settings = new List<SolutionPanelSetting>();
        foreach (var (item, at) in Items(read, panel, "settings", "panel.settings"))
        {
            var member = PluginMemberOf(read, item, at, members);
            var setting = read.Required(item, "setting", $"{at}.setting");

            if (member is null || setting is null) continue;

            if (settings.Any(s => s.Member == member && s.Setting == setting))
            {
                read.Refuse($"{at}.setting", $"`{at}` lists {member}'s '{setting}' twice.");
                continue;
            }

            settings.Add(new SolutionPanelSetting(member, setting));
            read.Placed("panel.settings", Position(at));
        }

        var status = read.Optional(panel, "status", "panel.status");
        if (status is not null)
        {
            var (template, refusal) = SolutionStatusTemplate.Parse(status);

            if (refusal is not null)
            {
                read.Refuse("panel.status", $"`panel.status` {refusal}");
                status = null;
            }
            else if (template!.ReadsData && primarySite is null && !primarySiteNamed)
            {
                read.Refuse("panel.status", "`panel.status` counts site data ({data.…}) but the package names no `panel.primarySite` to read it from.");
                status = null;
            }
        }

        return new SolutionPanel(primarySite, outputs, settings, status);
    }

    /// <summary>
    /// An output folder: a folder of the team's documents named as a relative path - no <c>..</c>,
    /// no leading <c>/</c>, no hidden (<c>.</c>) part and no wildcard - so the panel lists exactly
    /// the folder the package named and nothing outside the documents.
    /// </summary>
    internal static string? OutputRefusal(string field, string folder)
    {
        if (RelativeRefusal(field, folder, allowEmpty: false) is { } escape) return escape;

        var parts = folder.Trim().Trim('/').Split('/');

        if (parts.Any(part => part.StartsWith('.')))
        {
            return $"`{field}` '{folder}' names a hidden folder; name a folder a person sees, such as Applications.";
        }

        if (folder.IndexOfAny(['*', '?', '[', ']', ':', '\0']) >= 0 || folder.Any(char.IsControl))
        {
            return $"`{field}` '{folder}' must be a plain folder name, with no wildcard.";
        }

        return null;
    }

    private static IEnumerable<(JsonElement Item, string At)> Items(Reader read, JsonElement parent, string key, string field)
    {
        if (!parent.TryGetProperty(key, out var array) || array.ValueKind == JsonValueKind.Null) yield break;

        if (array.ValueKind != JsonValueKind.Array)
        {
            read.Refuse(field, $"`{field}` must be an array.");
            yield break;
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var at = $"{field}[{index++}]";

            if (item.ValueKind != JsonValueKind.Object)
            {
                read.Refuse(at, $"`{at}` must be an object.");
                continue;
            }

            yield return (item, at);
        }
    }

    private static string? PluginMemberOf(Reader read, JsonElement item, string at, IReadOnlyList<SolutionMember> members)
    {
        var name = read.Required(item, "member", $"{at}.member");
        if (name is null) return null;

        var member = members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        if (member is null)
        {
            read.Refuse($"{at}.member", $"`{at}.member` '{name}' names no member of this package.");
            return null;
        }

        if (member.Kind != MemberRef.PluginKind)
        {
            read.Refuse($"{at}.member", $"`{at}.member` '{name}' is an agent member; only a plugin member has settings and connection slots.");
            return null;
        }

        return member.Name;
    }

    private static bool RequiredFlag(Reader read, JsonElement item, string at)
    {
        if (!item.TryGetProperty("required", out var required) || required.ValueKind == JsonValueKind.Null) return false;

        if (required.ValueKind is JsonValueKind.True or JsonValueKind.False) return required.ValueKind == JsonValueKind.True;

        read.Refuse($"{at}.required", $"`{at}.required` must be true or false.");
        return false;
    }

    /// <summary>A path inside the package or the team's documents: relative, with no <c>..</c> and
    /// no rooted form. Links are the checker's, which sees the files.</summary>
    internal static string? RelativeRefusal(string field, string path, bool allowEmpty)
    {
        var trimmed = path.Trim().Trim('/');

        if (trimmed.Length == 0)
        {
            return allowEmpty ? null : $"`{field}` is required.";
        }

        if (Path.IsPathRooted(path.Trim()) || path.Contains('\\') || trimmed.Split('/').Any(part => part is ".." or "." or ""))
        {
            return $"`{field}` '{path}' must be a relative path that stays inside; no `..`, no leading `/`.";
        }

        return null;
    }

    /// <summary>
    /// A plugin member's <c>secrets</c>: each field of its plugin's manifest <c>secrets</c> bound to a
    /// LOGICAL KEY, never a value. A refusal names the file and the field and NEVER REPEATS THE
    /// ENTRY: what was written there may be the credential itself.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadSecrets(Reader read, JsonElement item, string at)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!item.TryGetProperty("secrets", out var element) || element.ValueKind == JsonValueKind.Null) return secrets;

        if (element.ValueKind != JsonValueKind.Object)
        {
            read.Refuse($"{at}.secrets", $"`{at}.secrets` must be an object of the plugin's secret field to a key name, as in {{\"apiKey\": \"ACME_API_KEY\"}}.");
            return secrets;
        }

        foreach (var entry in element.EnumerateObject())
        {
            var field = $"{at}.secrets.{entry.Name}";

            if (entry.Value.ValueKind != JsonValueKind.String || LooksLikeValue(entry.Value.GetString()!))
            {
                read.Refuse(field, $"`{field}` looks like a secret's value. A package names a key, never a value: bind it to a key name such as ACME_API_KEY, and set the value on the Host with the operator CLI's `secret set`.");
                continue;
            }

            var key = entry.Value.GetString()!;

            if (EnvironmentSecretStore.Refusal(key) is not null)
            {
                read.Refuse(field, IsEnvironmentName(key)
                    ? $"`{field}` names a key the platform keeps for itself (its own HARNESS_ variables and model providers' credentials); bind another key name."
                    : $"`{field}` is not a legal environment variable name: use upper-case letters, digits and underscores, starting with a letter, as in ACME_API_KEY.");
                continue;
            }

            secrets[entry.Name] = key;
        }

        return secrets;
    }

    private static bool IsEnvironmentName(string key) =>
        key.Length is > 0 and <= 128
        && (char.IsAsciiLetterUpper(key[0]) || key[0] == '_')
        && key.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

    /// <summary>
    /// Whether a secrets entry reads as a credential rather than a key name: a known credential
    /// prefix, a PEM block, or a long run of letters and digits with no underscore to separate words
    /// (<c>AKIA...</c>, a hex or base64 token). A key name reads as words: <c>ADZUNA_APP_KEY</c>.
    /// </summary>
    public static bool LooksLikeValue(string entry)
    {
        string[] prefixes = ["sk-", "sk_", "pk_", "rk_", "ghp_", "gho_", "ghs_", "ghu_", "github_pat_", "glpat-", "xox", "AKIA", "ASIA", "AIza", "eyJ", "-----BEGIN"];

        if (prefixes.Any(p => entry.StartsWith(p, StringComparison.Ordinal))) return true;

        return entry.Length >= 16 && !entry.Contains('_') && entry.Any(char.IsAsciiDigit) && entry.Any(char.IsAsciiLetter);
    }

    /// <summary>The index in a field such as <c>inputs.settings[3]</c>.</summary>
    private static int Position(string at) =>
        int.Parse(at[(at.LastIndexOf('[') + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsVersion(string value) =>
        value.Length is > 0 and <= 64 && char.IsAsciiDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    /// <summary>Collects refusals against <c>solution.json</c> as the parse goes.</summary>
    private sealed class Reader
    {
        public List<SolutionRefusal> Refusals { get; } = [];

        private readonly Dictionary<string, List<int>> positions = new(StringComparer.Ordinal);

        /// <summary>Records that the item just kept in <paramref name="list"/> is item
        /// <paramref name="index"/> of the file's array; a refused item before it shifts the two.</summary>
        public void Placed(string list, int index)
        {
            if (!positions.TryGetValue(list, out var kept)) positions[list] = kept = [];
            kept.Add(index);
        }

        public SolutionManifestRead Answer(SolutionManifest? manifest) =>
            new(manifest, Refusals, positions.ToDictionary(p => p.Key, p => (IReadOnlyList<int>)p.Value, StringComparer.Ordinal));

        public void Refuse(string field, string reason) => Refusals.Add(new SolutionRefusal(FileName, field, reason));

        public string? Required(JsonElement parent, string key, string field, bool trim = true)
        {
            if (!parent.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                Refuse(field, $"`{field}` is required.");
                return null;
            }

            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                Refuse(field, $"`{field}` must be a non-empty string.");
                return null;
            }

            return trim ? value.GetString()!.Trim() : value.GetString()!;
        }

        public string? Optional(JsonElement parent, string key, string field)
        {
            if (!parent.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;

            if (value.ValueKind != JsonValueKind.String)
            {
                Refuse(field, $"`{field}` must be a string.");
                return null;
            }

            return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim();
        }
    }
}

/// <summary>
/// What <see cref="SolutionManifest.Read"/> made of a file: the manifest as far as it read (null
/// when nothing past the format gate could be read), every refusal, and where each kept item stood
/// in the file, so a later refusal names the file's own index.
/// </summary>
public sealed record SolutionManifestRead(
    SolutionManifest? Manifest,
    IReadOnlyList<SolutionRefusal> Refusals,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Positions)
{
    /// <summary>The file's index for the <paramref name="kept"/>th item kept in <paramref name="list"/>
    /// (<c>members</c>, <c>triggers</c>, <c>skills</c>, <c>sites</c>, <c>inputs.settings</c>,
    /// <c>inputs.connections</c>).</summary>
    public int At(string list, int kept) =>
        Positions.TryGetValue(list, out var indexes) && kept < indexes.Count ? indexes[kept] : kept;
}

/// <summary>The team a package makes: its default name (the package's name when not given) and
/// the additional instructions every member's prompt carries.</summary>
public sealed record SolutionTeam(string Name, string Instructions);

/// <summary>
/// One member, by the package's own name for it - the name triggers and inputs use.
/// <paramref name="Kind"/> is <see cref="MemberRef.AgentKind"/> or <see cref="MemberRef.PluginKind"/>.
/// An agent has a <paramref name="Role"/> (<c>manager</c> for the team's Manager, else
/// <c>member</c>), an optional <paramref name="Preset"/> (the team resolves one when absent) and its
/// own <paramref name="Instructions"/>; a plugin has <paramref name="PluginId"/> and its
/// <paramref name="Settings"/>.
/// </summary>
public sealed record SolutionMember(
    string Name,
    string Kind,
    string Role,
    string? Preset,
    string Instructions,
    string? PluginId,
    IReadOnlyDictionary<string, JsonElement> Settings)
{
    /// <summary>A plugin member's secret bindings: its plugin's secret field to a LOGICAL KEY, never a
    /// value. The install binds them as a person's binding in the member's settings would.</summary>
    public IReadOnlyDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// One trigger. <paramref name="Kind"/> is <c>schedule</c>, <c>event</c> or <c>folder</c>, and
/// exactly the matching one of <paramref name="Schedule"/>, <paramref name="Event"/> and
/// <paramref name="Folder"/> is set. <paramref name="Member"/> is the package's member name.
/// <paramref name="Instruction"/> is kept verbatim: <c>{event.*}</c> resolves at each fire and
/// <c>{solution}</c> at install.
/// </summary>
public sealed record SolutionTrigger(
    string Name,
    string Kind,
    string Member,
    string Instruction,
    string WakeManager,
    long? DailyTokenCap,
    bool IdleOnly,
    SolutionSchedule? Schedule,
    SolutionEvent? Event,
    SolutionFolder? Folder)
{
    /// <summary>The trigger kind the platform stores for this one.</summary>
    public TriggerKind PlatformKind => Kind switch
    {
        SolutionManifest.KindSchedule => Schedule!.Kind,
        SolutionManifest.KindFolder => TriggerKind.FolderChange,
        _ => TriggerKind.Event,
    };

    /// <summary>A schedule's first run at install: fired once, right after the install's last step,
    /// then on its interval. The check refuses it on an event or folder trigger.</summary>
    public bool RunAtInstall { get; init; }
}

/// <summary>A clock: a seconds-format cron in a timezone, or every N seconds.</summary>
public sealed record SolutionSchedule(TriggerKind Kind, string? Cron, string? Timezone, int? EverySeconds)
{
    public override string ToString() =>
        Cron is not null ? $"cron {Cron} ({Timezone})" : $"every {EverySeconds} seconds";
}

/// <summary>An event type and an optional <c>field op value</c> filter.</summary>
public sealed record SolutionEvent(string Type, string? Filter);

/// <summary>A folder inside the team's documents, and an optional wildcard.</summary>
public sealed record SolutionFolder(string Path, string? Glob);

/// <summary>What only a person provides, asked for by the install.</summary>
public sealed record SolutionInputs(
    IReadOnlyList<SolutionSettingInput> Settings,
    IReadOnlyList<SolutionConnectionInput> Connections,
    IReadOnlyList<SolutionDocumentInput> Documents);

/// <summary>A person-only plugin setting (<c>"setBy": "person"</c> in its manifest).</summary>
public sealed record SolutionSettingInput(string Member, string Setting, string Description, bool Required);

/// <summary>A plugin's connection slot, bound by a person to one of their connections.</summary>
public sealed record SolutionConnectionInput(string Member, string Slot, string Description, bool Required);

/// <summary>A folder of the team's documents a person uploads into, such as <c>Resume</c>.</summary>
public sealed record SolutionDocumentInput(string Folder, string Description, bool Required);

/// <summary>
/// <c>panel</c>: what the platform's control panel and launcher tile take from the package - the
/// site the tile opens, the documents folders listed as results (relative, in the package's order),
/// the plugin settings shown first, and the status line template (null for the default line).
/// </summary>
public sealed record SolutionPanel(
    string? PrimarySite,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<SolutionPanelSetting> Settings,
    string? Status)
{
    public static readonly SolutionPanel None = new(null, [], [], null);
}

/// <summary>One plugin setting the panel shows first: the package's member name and the setting.</summary>
public sealed record SolutionPanelSetting(string Member, string Setting);
