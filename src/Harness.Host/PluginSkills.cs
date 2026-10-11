using Harness.Contracts;
using Harness.Skills;

namespace Harness.Host;

/// <summary>
/// AN INSTALLED PLUGIN'S SKILLS (assessment §9): read from the files its manifest names, named in
/// the plugin's own namespace, and indexed as LOCKED rows of kind <c>plugin</c> whenever the plugins
/// are loaded or rescanned.
///
/// <list type="bullet">
/// <item><b>Names are forced</b>: <c>plugin-&lt;id&gt;</c> for the skill naming the plugin itself,
/// <c>plugin-&lt;id&gt;-&lt;name&gt;</c> for any other (<see cref="PluginSkillNames.Forced"/>), so a
/// plugin cannot shadow <c>manager</c> or any other skill.</item>
/// <item><b>Roles default to <c>manager member</c></b>. A file's <c>roles</c> may narrow that, never
/// widen it: <c>any</c> means both, and <c>concierge</c> is never offered.</item>
/// <item><b>Never in a prompt's "Available skills"</b> (<see cref="SkillDirectory"/>). Found by the
/// roster, by <c>skills_search</c> and by the <c>hiring</c> tool.</item>
/// </list>
/// </summary>
public static class PluginSkills
{
    /// <summary>The roles a plugin skill is offered to unless its file narrows them.</summary>
    public static readonly IReadOnlyList<string> DefaultRoles = [SkillRoles.Manager, SkillRoles.Member];

    /// <summary>
    /// The skills a manifest names, read from <paramref name="directory"/>, or the sentence refusing
    /// the plugin - a skill that cannot be read is a manifest that is wrong, refused by name like any
    /// other, never half-loaded.
    /// </summary>
    public static (IReadOnlyList<SkillDraft> Skills, string? Refusal) Read(PluginManifest manifest, string directory)
    {
        var drafts = new List<SkillDraft>();

        foreach (var relative in manifest.Skills)
        {
            SkillFile.Parsed parsed;

            try
            {
                parsed = SkillFile.Parse(File.ReadAllText(Path.Combine(directory, relative)));
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return ([], $"its skill {relative} could not be read: {error.Message}");
            }

            var name = PluginSkillNames.Forced(manifest.Id, parsed.Name);

            if (!SqliteSkillStore.IsLegalName(name))
            {
                return ([], $"its skill {relative} would be named '{name}', which is not a legal skill name "
                    + "(lowercase kebab-case, at most 48 characters).");
            }

            if (drafts.Any(d => d.Name == name))
            {
                return ([], $"two of its skills would both be named '{name}'.");
            }

            if (string.IsNullOrWhiteSpace(parsed.Description) || string.IsNullOrWhiteSpace(parsed.Body))
            {
                return ([], $"its skill {relative} needs a one-line description and a body.");
            }

            IReadOnlyList<string> roles;

            try
            {
                roles = Narrow(parsed.Roles);
            }
            catch (ArgumentException error)
            {
                return ([], $"its skill {relative}: {error.Message}");
            }

            if (roles.Count == 0)
            {
                return ([], $"its skill {relative} is offered to neither {SkillRoles.Manager} nor {SkillRoles.Member}, "
                    + "the only roles a plugin skill may be offered to.");
            }

            drafts.Add(new SkillDraft(name, parsed.Description, roles, parsed.Body));
        }

        return (drafts, null);
    }

    /// <summary><see cref="DefaultRoles"/>, narrowed by what a file says.</summary>
    private static IReadOnlyList<string> Narrow(IReadOnlyList<string>? declared)
    {
        if (declared is null) return DefaultRoles;

        var roles = SkillRoles.Normalise(declared);
        return roles.Contains(SkillRoles.Any) ? DefaultRoles : [.. DefaultRoles.Where(roles.Contains)];
    }

    /// <summary>
    /// Makes the index agree with <paramref name="scan"/>: each installed plugin's skills replaced,
    /// and the skills of a plugin no longer installed removed. Returns a sentence per skill that
    /// was moved or not taken. Runs inside every load and rescan (<see cref="PluginCatalog.Attach"/>),
    /// however the rescan was started.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SyncAsync(ISkillStore store, PluginScan scan, CancellationToken ct = default)
    {
        var said = new List<string>();
        var at = DateTimeOffset.UtcNow;
        var installed = scan.Plugins.Select(p => p.Manifest.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var gone in (await store.PluginSourcesAsync(ct)).Where(s => !installed.Contains(s)))
        {
            await store.ReplacePluginSkillsAsync(gone, [], at, ct);
        }

        foreach (var plugin in scan.Plugins)
        {
            said.AddRange(await store.ReplacePluginSkillsAsync(plugin.Manifest.Id, plugin.Skills, at, ct));
        }

        return said;
    }

    /// <summary>The name of the skill that says how to use <paramref name="plugin"/>, or null when
    /// it ships none: its main skill, else its first.</summary>
    public static string? SkillOf(InstalledPlugin plugin) =>
        plugin.Skills.FirstOrDefault(s => s.Name == PluginSkillNames.Main(plugin.Manifest.Id))?.Name
        ?? plugin.Skills.FirstOrDefault()?.Name;
}
