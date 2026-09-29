using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Pty;
using Harness.Skills;

namespace Harness.Host.Solutions;

/// <summary>
/// What the checker asks of the platform it would install onto, as delegates so the running Host,
/// the operator's <c>--solution-check</c> and a test each hand in their own.
/// </summary>
/// <param name="Preset">A headless Agent preset by name: null when there is none, else whether it
/// runs a language model (the high-volume rule reads it).</param>
/// <param name="Event">An event type the catalog knows now - the platform's and every installed
/// plugin's (<see cref="EventCatalog.For"/> in the Host). The package's own plugins' events are
/// added by the checker.</param>
/// <param name="RuntimeFound">Whether a runtime a plugin <c>requires</c> is on this machine.</param>
/// <param name="IsBuiltInSkill">Whether a skill name is a built-in's.</param>
public sealed record SolutionPlatform(
    Func<string, bool?> Preset,
    Func<string, EventDefinition?> Event,
    Func<string, bool> RuntimeFound,
    Func<string, bool> IsBuiltInSkill)
{
    /// <summary>The running Host's: its Agent catalog, the event catalog (plugins included) and its
    /// plugin catalog's runtime check.</summary>
    public static SolutionPlatform For(AgentCatalog agents, PluginCatalog plugins) => new(
        name => PresetOf(agents.Definitions, name),
        EventCatalog.For,
        plugins.RuntimeFound,
        BuiltInSkills.IsBuiltIn);

    /// <summary>
    /// An instance's, read from its data root with nothing running and nothing written: the built-in
    /// presets plus <c>agents.json</c>, the platform's events plus those of every plugin installed
    /// under <c>plugins/</c>, and this machine's PATH. What the operator's check uses.
    /// </summary>
    public static SolutionPlatform ForDataRoot(string dataRoot)
    {
        var presets = AgentCatalogFile.BuiltIns().Concat(AgentCatalogFile.LoadCustom(dataRoot, TextWriter.Null)).ToList();
        var installed = PluginCatalog.Scan(Path.Combine(dataRoot, "plugins"));
        var events = installed.Plugins
            .SelectMany(p => p.Manifest.Publishes.Select(e => e.Definition(p.Manifest.Id)))
            .GroupBy(e => e.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return new(
            name => PresetOf(presets, name),
            type => EventCatalog.For(type) ?? events.GetValueOrDefault(type),
            runtime => PathSearch.Find(runtime) is not null,
            BuiltInSkills.IsBuiltIn);
    }

    private static bool? PresetOf(IEnumerable<AgentDefinition> definitions, string name) =>
        definitions.FirstOrDefault(d => d.Mode == AgentMode.Headless && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            is { } found ? found.Launch.LanguageModel : null;
}

/// <summary>
/// A package that passed its check, with everything an install needs already read: the manifest,
/// the package's own plugins, skills and sites, and its tools. The install engine takes this rather
/// than reading the folder a second time.
/// </summary>
public sealed record SolutionPackage(
    string Folder,
    SolutionManifest Manifest,
    IReadOnlyList<SolutionPlugin> Plugins,
    IReadOnlyList<SolutionSkill> Skills,
    IReadOnlyList<SolutionSite> Sites,
    string? ToolsFolder,
    IReadOnlyList<string> Tools,
    bool HasReadme)
{
    public SolutionPlugin? Plugin(string? id) => Plugins.FirstOrDefault(p => p.Id == id);
}

/// <summary>One plugin the package ships, at <c>plugins/&lt;id&gt;/</c>: one built version.</summary>
public sealed record SolutionPlugin(string Id, string Folder, PluginManifest Manifest);

/// <summary>One team skill, read from <c>skills/&lt;name&gt;.md</c>.</summary>
public sealed record SolutionSkill(string Name, string Description, IReadOnlyList<string> Roles, string Body, string File);

/// <summary>One site, published from <c>sites/&lt;name&gt;/</c>.</summary>
public sealed record SolutionSite(string Name, string Folder, IReadOnlyList<string> Files);

/// <summary>
/// The answer to "what would installing this folder do": when <see cref="Ok"/>, the package and the
/// plan; otherwise every refusal, each naming its file and field.
/// </summary>
public sealed record SolutionCheck(
    string Folder,
    SolutionPackage? Package,
    SolutionPlan? Plan,
    IReadOnlyList<SolutionRefusal> Refusals)
{
    public bool Ok => Package is not null && Refusals.Count == 0;
}

/// <summary>
/// CHECKS A SOLUTION PACKAGE, and writes nothing. The one validator for everything that reads a
/// package: <c>POST /api/solutions/check</c>, the operator's <c>--solution-check</c> (and so the
/// CLI's <c>solution check</c>), and - reading <see cref="SolutionCheck.Package"/> - the install
/// engine and the board's notice.
///
/// <para>
/// It checks the folder's own files against each other (a trigger's member, a plugin member's
/// plugin, an input's setting or slot, a skill's and a site's files, every link inside the folder)
/// and against the platform it would install onto (<see cref="SolutionPlatform"/>: event types,
/// Agent presets, built-in skill names, runtimes). It does not look at teams: which team a package
/// becomes, and whether that name is free, is the install's question.
/// </para>
/// </summary>
public sealed partial class SolutionChecker(SolutionPlatform platform)
{
    public const string PluginsFolder = "plugins";
    public const string SkillsFolder = "skills";
    public const string SitesFolder = "sites";
    public const string ToolsFolderName = "tools";
    public const string ReadmeFile = "README.md";

    public SolutionCheck Check(string folder)
    {
        var refusals = new List<SolutionRefusal>();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

        if (!Directory.Exists(root))
        {
            refusals.Add(new(".", "(folder)", $"{root} is not a folder."));
            return new SolutionCheck(root, null, null, refusals);
        }

        var manifestPath = Path.Combine(root, SolutionManifest.FileName);

        if (!File.Exists(manifestPath))
        {
            refusals.Add(new(SolutionManifest.FileName, "(file)", $"{root} holds no {SolutionManifest.FileName}; a solution package has one at its root."));
            return new SolutionCheck(root, null, null, refusals);
        }

        // EVERY LINK FIRST: a link leaving the package would let every later read see a file the
        // package does not hold.
        refusals.AddRange(LinksLeaving(root));

        string json;

        try
        {
            json = File.ReadAllText(manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            refusals.Add(new(SolutionManifest.FileName, "(file)", $"{SolutionManifest.FileName} could not be read: {exception.Message}"));
            return new SolutionCheck(root, null, null, refusals);
        }

        // EVERY REFUSAL AT ONCE: a refusal in solution.json leaves the item it names out, and every
        // other part is still checked. Only a file that cannot be read past its format stops here.
        var read = SolutionManifest.Read(json);
        refusals.AddRange(read.Refusals);

        if (read.Manifest is not { } manifest)
        {
            return new SolutionCheck(root, null, null, refusals);
        }

        var plugins = ReadPlugins(root, refusals);
        var skills = ReadSkills(root, read, refusals);
        var sites = ReadSites(root, read, refusals);
        var toolsFolder = Path.Combine(root, ToolsFolderName);
        var tools = Directory.Exists(toolsFolder) ? FilesUnder(toolsFolder) : null;

        CheckMembers(read, plugins, tools is not null, refusals);
        CheckTriggers(read, plugins, tools is not null, refusals);
        CheckInputs(read, plugins, refusals);

        if (refusals.Count > 0) return new SolutionCheck(root, null, null, refusals);

        var package = new SolutionPackage(
            root, manifest, plugins, skills, sites,
            tools is null ? null : toolsFolder, tools ?? [],
            File.Exists(Path.Combine(root, ReadmeFile)));

        return new SolutionCheck(root, package, SolutionPlan.Of(package), []);
    }

    private List<SolutionPlugin> ReadPlugins(string root, List<SolutionRefusal> refusals)
    {
        var plugins = new List<SolutionPlugin>();
        var directory = Path.Combine(root, PluginsFolder);

        if (!Directory.Exists(directory)) return plugins;

        foreach (var pluginDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(pluginDirectory);
            var file = $"{PluginsFolder}/{id}/{PluginManifest.FileName}";

            if (!MemberRef.IsValidPluginId(id))
            {
                refusals.Add(new($"{PluginsFolder}/{id}", "(folder)", $"'{id}' is not a plugin id; each folder under {PluginsFolder}/ is named after its plugin's id."));
                continue;
            }

            var manifestPath = Path.Combine(pluginDirectory, PluginManifest.FileName);

            if (!File.Exists(manifestPath))
            {
                refusals.Add(new(file, "(file)", $"{PluginsFolder}/{id}/ holds no {PluginManifest.FileName}; each plugin is one built version, manifest at its root."));
                continue;
            }

            var (manifest, refusal) = PluginManifest.Parse(File.ReadAllText(manifestPath));

            if (manifest is null)
            {
                refusals.Add(new(file, FieldIn(refusal!), refusal!));
                continue;
            }

            if (manifest.Id != id)
            {
                refusals.Add(new(file, "id", $"`id` is '{manifest.Id}' but its folder is {PluginsFolder}/{id}/; name the folder after the id."));
                continue;
            }

            // THE CATALOG'S OWN CHECK, exactly as an install from a folder runs it before writing:
            // executable and skills inside the folder after links, skill files, runtimes.
            if (PluginCatalog.Check(pluginDirectory, manifest.Version, manifest, platform.RuntimeFound, requireExecutableBit: false)
                is { Reason: { } reason })
            {
                refusals.Add(new(file, FieldIn(reason, fallback: "executable"), $"The Host refuses {id} {manifest.Version}: {reason}"));
                continue;
            }

            plugins.Add(new SolutionPlugin(id, pluginDirectory, manifest));
        }

        return plugins;
    }

    private List<SolutionSkill> ReadSkills(string root, SolutionManifestRead read, List<SolutionRefusal> refusals)
    {
        var manifest = read.Manifest!;
        var skills = new List<SolutionSkill>();

        for (var index = 0; index < manifest.Skills.Count; index++)
        {
            var name = manifest.Skills[index];
            var at = $"skills[{read.At("skills", index)}]";
            var file = $"{SkillsFolder}/{name}.md";

            if (!SqliteSkillStore.IsLegalName(name))
            {
                refusals.Add(new(SolutionManifest.FileName, at, $"'{name}' is not a legal skill name. Use lowercase kebab-case (letters, digits, '-') and 48 characters or fewer."));
                continue;
            }

            if (platform.IsBuiltInSkill(name))
            {
                refusals.Add(new(SolutionManifest.FileName, at, $"'{name}' is a built-in skill's name. A team skill needs a name of its own."));
                continue;
            }

            if (PluginSkillNames.IsReserved(name))
            {
                refusals.Add(new(SolutionManifest.FileName, at, $"'{name}' begins '{PluginSkillNames.Prefix}', which is reserved for the skills plugins ship."));
                continue;
            }

            var path = Path.Combine(root, SkillsFolder, name + ".md");

            if (!File.Exists(path))
            {
                refusals.Add(new(SolutionManifest.FileName, at, $"'{name}' has no file {file}."));
                continue;
            }

            SkillFile.Parsed parsed;

            try
            {
                parsed = SkillFile.Parse(File.ReadAllText(path));
            }
            catch (ArgumentException malformed)
            {
                refusals.Add(new(file, "(front matter)", malformed.Message));
                continue;
            }

            if (!string.Equals(parsed.Name, name, StringComparison.Ordinal))
            {
                refusals.Add(new(file, "name", $"The front matter says `name: {parsed.Name}`, but solution.json and the file name say '{name}'."));
                continue;
            }

            if (parsed.Description.Length == 0)
            {
                refusals.Add(new(file, "description", "A skill needs a one-line `description` in its front matter."));
                continue;
            }

            if (parsed.Roles is not { Count: > 0 })
            {
                refusals.Add(new(file, "roles", $"A team skill must say who it is for: `roles`, any of {string.Join(", ", SkillRoles.All)}."));
                continue;
            }

            IReadOnlyList<string> roles;

            try
            {
                roles = SkillRoles.Normalise(parsed.Roles);
            }
            catch (ArgumentException unknown)
            {
                refusals.Add(new(file, "roles", unknown.Message));
                continue;
            }

            if (string.IsNullOrWhiteSpace(parsed.Body))
            {
                refusals.Add(new(file, "(body)", "A skill needs a body below its front matter."));
                continue;
            }

            skills.Add(new SolutionSkill(name, parsed.Description, roles, parsed.Body, file));
        }

        return skills;
    }

    private static List<SolutionSite> ReadSites(string root, SolutionManifestRead read, List<SolutionRefusal> refusals)
    {
        var manifest = read.Manifest!;
        var sites = new List<SolutionSite>();

        for (var index = 0; index < manifest.Sites.Count; index++)
        {
            var name = manifest.Sites[index];
            var at = $"sites[{read.At("sites", index)}]";

            if (!SiteRules.IsSlug(name))
            {
                refusals.Add(new(SolutionManifest.FileName, at, SiteRules.NotASlug("site", name)));
                continue;
            }

            var folder = Path.Combine(root, SitesFolder, name);

            if (!Directory.Exists(folder))
            {
                refusals.Add(new(SolutionManifest.FileName, at, $"'{name}' has no folder {SitesFolder}/{name}/."));
                continue;
            }

            if (!File.Exists(Path.Combine(folder, "index.html")))
            {
                refusals.Add(new($"{SitesFolder}/{name}/index.html", "(file)", $"{SitesFolder}/{name}/ has no index.html, which is the page a site opens at."));
                continue;
            }

            var files = FilesUnder(folder);

            if (files.Count > SiteRules.MaxPublishedFiles)
            {
                refusals.Add(new(SolutionManifest.FileName, at, $"{SitesFolder}/{name}/ holds {files.Count} files; a site publishes at most {SiteRules.MaxPublishedFiles}."));
                continue;
            }

            sites.Add(new SolutionSite(name, folder, files));
        }

        return sites;
    }

    private void CheckMembers(SolutionManifestRead read, List<SolutionPlugin> plugins, bool hasTools, List<SolutionRefusal> refusals)
    {
        var manifest = read.Manifest!;

        for (var index = 0; index < manifest.Members.Count; index++)
        {
            var member = manifest.Members[index];
            var at = $"members[{read.At("members", index)}]";

            if (member.Kind == MemberRef.AgentKind)
            {
                if (member.Preset is { } preset && platform.Preset(preset) is null)
                {
                    refusals.Add(new(SolutionManifest.FileName, $"{at}.preset", $"`{at}.preset` '{preset}' is not a headless Agent preset on this instance; omit it to let the team choose, or name one Admin → Agents lists."));
                }

                if (!hasTools && member.Instructions.Contains(SolutionManifest.SolutionToken, StringComparison.Ordinal))
                {
                    refusals.Add(new(SolutionManifest.FileName, $"{at}.instructions", $"`{at}.instructions` uses {SolutionManifest.SolutionToken}, but the package has no {ToolsFolderName}/ folder for it to name."));
                }

                continue;
            }

            if (plugins.FirstOrDefault(p => p.Id == member.PluginId) is not { } plugin)
            {
                var shipped = plugins.Count == 0 ? $"it ships none under {PluginsFolder}/" : "it ships " + string.Join(", ", plugins.Select(p => p.Id));
                refusals.Add(new(SolutionManifest.FileName, $"{at}.pluginId", $"`{at}.pluginId` '{member.PluginId}' is not a plugin in this package; {shipped}."));
                continue;
            }

            foreach (var (name, value) in member.Settings)
            {
                var field = $"{at}.settings.{name}";

                if (!plugin.Manifest.Config.TryGetValue(name, out var config))
                {
                    var known = plugin.Manifest.Config.Count == 0 ? "it has none" : "it has " + string.Join(", ", plugin.Manifest.Config.Keys);
                    refusals.Add(new(SolutionManifest.FileName, field, $"`{field}`: plugin {plugin.Id} has no setting '{name}'; {known}."));
                    continue;
                }

                if (config.Refusal(name, value) is { } invalid)
                {
                    refusals.Add(new(SolutionManifest.FileName, field, invalid));
                    continue;
                }

                // A PERSON-ONLY SETTING STAYS A PERSON'S. A package is written by anyone, an agent
                // included; what it may set is what an agent's hire may set.
                if (config.AgentRefusal(name, value) is not null)
                {
                    refusals.Add(new(SolutionManifest.FileName, field, $"`{field}` is set by a person only; a package may leave it at its default. Ask for it under `inputs.settings` instead."));
                }
            }

            foreach (var (name, config) in plugin.Manifest.Config)
            {
                // AN ABSENCE IS JUDGED ON A WHOLE FILE ONLY: an input refused above may be the one
                // that asks for it.
                if (read.Refusals.Count == 0 && config.Required && config.Default is null && !member.Settings.ContainsKey(name)
                    && !manifest.Inputs.Settings.Any(s => SameMember(s.Member, member.Name) && s.Setting == name))
                {
                    refusals.Add(new(SolutionManifest.FileName, $"{at}.settings", $"`{at}.settings` leaves '{name}' unset; plugin {plugin.Id} requires it. Set it here, or ask a person for it under `inputs.settings`."));
                }
            }
        }
    }

    private void CheckTriggers(SolutionManifestRead read, List<SolutionPlugin> plugins, bool hasTools, List<SolutionRefusal> refusals)
    {
        var manifest = read.Manifest!;

        var packageEvents = plugins
            .SelectMany(p => p.Manifest.Publishes.Select(e => e.Definition(p.Id)))
            .ToDictionary(e => e.Type, StringComparer.Ordinal);

        for (var index = 0; index < manifest.Triggers.Count; index++)
        {
            var trigger = manifest.Triggers[index];
            var at = $"triggers[{read.At("triggers", index)}]";
            var member = manifest.Member(trigger.Member)!;

            if (!hasTools && trigger.Instruction.Contains(SolutionManifest.SolutionToken, StringComparison.Ordinal))
            {
                refusals.Add(new(SolutionManifest.FileName, $"{at}.instruction", $"`{at}.instruction` uses {SolutionManifest.SolutionToken}, but the package has no {ToolsFolderName}/ folder for it to name."));
            }

            if (trigger.Event is not { } onEvent) continue;

            var definition = packageEvents.GetValueOrDefault(onEvent.Type) ?? platform.Event(onEvent.Type);

            if (definition is null)
            {
                var hint = onEvent.Type.StartsWith(EventCatalog.PluginPrefix, StringComparison.Ordinal)
                    ? " A plugin's event is plugin.<id>.<type>, for a type its manifest declares under events.publishes."
                    : " GET /api/events lists every type a trigger can name.";
                refusals.Add(new(SolutionManifest.FileName, $"{at}.event.type", $"`{at}.event.type` '{onEvent.Type}' is not an event type the catalog knows.{hint}"));
                continue;
            }

            // THE HIGH-VOLUME RULE, as the trigger route applies it: a language model is never woken
            // once per status line. An agent member with no preset named is judged as a model.
            if (definition.HighVolume && member.Kind == MemberRef.AgentKind
                && (member.Preset is null || platform.Preset(member.Preset) != false))
            {
                refusals.Add(new(SolutionManifest.FileName, $"{at}.event.type", TeamRegistry.FirehoseRefusal(onEvent.Type, member.Preset ?? member.Name)));
                continue;
            }

            if (onEvent.Filter is { } filter && TriggerFilter.Parse(filter) is { } parsed
                && !definition.Fields.Any(f => string.Equals(f.Name, parsed.Field, StringComparison.Ordinal)))
            {
                refusals.Add(new(SolutionManifest.FileName, $"{at}.event.filter",
                    $"`{at}.event.filter`: \"{onEvent.Type}\" carries no field named \"{parsed.Field}\". It carries: {string.Join(", ", definition.Fields.Select(f => f.Name))}."));
            }
        }
    }

    private static void CheckInputs(SolutionManifestRead read, List<SolutionPlugin> plugins, List<SolutionRefusal> refusals)
    {
        var manifest = read.Manifest!;

        PluginManifest? PluginOf(string memberName) =>
            plugins.FirstOrDefault(p => p.Id == manifest.Member(memberName)?.PluginId)?.Manifest;

        for (var index = 0; index < manifest.Inputs.Settings.Count; index++)
        {
            var input = manifest.Inputs.Settings[index];
            var at = $"inputs.settings[{read.At("inputs.settings", index)}].setting";

            if (PluginOf(input.Member) is not { } plugin) continue;

            if (!plugin.Config.TryGetValue(input.Setting, out var config))
            {
                refusals.Add(new(SolutionManifest.FileName, at, $"`{at}`: plugin {plugin.Id} has no setting '{input.Setting}'."));
            }
            else if (!config.PersonOnly)
            {
                refusals.Add(new(SolutionManifest.FileName, at, $"`{at}` '{input.Setting}' is not a person-only setting (`\"setBy\": \"person\"`); set it under the member's `settings`."));
            }
        }

        for (var index = 0; index < manifest.Inputs.Connections.Count; index++)
        {
            var input = manifest.Inputs.Connections[index];
            var at = $"inputs.connections[{read.At("inputs.connections", index)}].slot";

            if (PluginOf(input.Member) is { } plugin && !plugin.Connections.ContainsKey(input.Slot))
            {
                var known = plugin.Connections.Count == 0 ? "it declares none" : "it declares " + string.Join(", ", plugin.Connections.Keys);
                refusals.Add(new(SolutionManifest.FileName, at, $"`{at}`: plugin {plugin.Id} has no connection slot '{input.Slot}'; {known}."));
            }
        }

        // A REQUIRED SLOT NOBODY IS ASKED FOR blocks the member's every run, so the package must ask -
        // judged on a whole file only, as an input refused above may be the one that asks.
        if (read.Refusals.Count > 0) return;

        foreach (var member in manifest.Members.Where(m => m.Kind == MemberRef.PluginKind))
        {
            if (plugins.FirstOrDefault(p => p.Id == member.PluginId) is not { } plugin) continue;

            foreach (var (slot, declared) in plugin.Manifest.Connections)
            {
                if (declared.Required && !manifest.Inputs.Connections.Any(c => SameMember(c.Member, member.Name) && c.Slot == slot))
                {
                    refusals.Add(new(SolutionManifest.FileName, "inputs.connections",
                        $"`inputs.connections` does not ask for '{member.Name}''s slot '{slot}', which plugin {plugin.Id} requires; a person binds it at install."));
                }
            }
        }
    }

    private static bool SameMember(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every link under the package that is absolute or resolves outside it.</summary>
    private static IEnumerable<SolutionRefusal> LinksLeaving(string root)
    {
        var resolvedRoot = PluginCatalog.Resolved(root);
        var pending = new Stack<string>([root]);

        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var info = new FileInfo(entry);
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');

                if (info.LinkTarget is { } link)
                {
                    if (Path.IsPathRooted(link))
                    {
                        yield return new(relative, "(link)", $"{relative} is a link to an absolute path ({link}); a package's links must be relative and stay inside it.");
                        continue;
                    }

                    var resolved = PluginCatalog.Resolved(entry);
                    if (resolved != resolvedRoot && !resolved.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        yield return new(relative, "(link)", $"{relative} is a link leading outside the package ({link}).");
                    }

                    continue;
                }

                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
    }

    /// <summary>Every file under <paramref name="folder"/>, relative with <c>/</c>, in order;
    /// dot-names left out, as a site's publish leaves them.</summary>
    private static List<string> FilesUnder(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
            .Where(relative => !relative.Split('/').Any(part => part.StartsWith('.')))
            .Order(StringComparer.Ordinal)];

    /// <summary>The field a manifest refusal names in backticks, or <paramref name="fallback"/>.</summary>
    private static string FieldIn(string sentence, string fallback = "(manifest)") =>
        BacktickedField().Match(sentence) is { Success: true } match ? match.Groups[1].Value.Split(' ')[0] : fallback;

    [GeneratedRegex("`([A-Za-z][A-Za-z0-9_.\\-]*)[^`]*`")]
    private static partial Regex BacktickedField();
}
