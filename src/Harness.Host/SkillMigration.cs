using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Skills;

namespace Harness.Host;

/// <summary>
/// A start on a volume that still holds skill files on disk.
///
/// A volume may carry an editable copy of each built-in as files. The build is the only source of
/// a built-in and a custom skill lives in the database, so each such folder is dealt with once and
/// then MOVED to <see cref="TeamPaths.SkillBackups"/> - never deleted:
///
/// - a skill whose name is a built-in's is replaced by the built-in;
/// - `tenant-manager`, which is stale, is not migrated;
/// - any other skill becomes a Custom skill, offered to `member` unless its frontmatter names roles
///   or its description clearly names the Concierge or the Manager.
///
/// A folder that is gone has nothing left to migrate, so running this on every start is harmless.
/// </summary>
public static class SkillMigration
{
    public const string RetiredSkill = "tenant-manager";

    private static readonly Regex NamesConcierge = new(@"\bconcierge\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NamesManager = new(@"\bmanag(er|ers|ing)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record Report(
        IReadOnlyList<string> ReplacedByBuiltIn,
        IReadOnlyList<string> Imported,
        IReadOnlyList<string> NotMigrated,
        IReadOnlyList<string> Warnings)
    {
        public bool Moved => ReplacedByBuiltIn.Count + Imported.Count + NotMigrated.Count + Warnings.Count > 0;
    }

    /// <summary>
    /// Migrates every folder in <paramref name="roots"/> (label, path), moving each to a backup
    /// under <paramref name="backupRoot"/>/label. A skill name already taken in the database - a
    /// second copy in a team or draft folder - is backed up and not imported again.
    /// </summary>
    public static async Task<Report> RunAsync(
        IReadOnlyList<(string Label, string Path)> roots,
        string backupRoot,
        ISkillStore store,
        CancellationToken ct = default)
    {
        var replaced = new List<string>();
        var imported = new List<string>();
        var notMigrated = new List<string>();
        var warnings = new List<string>();

        foreach (var (label, root) in roots)
        {
            if (!Directory.Exists(root)) continue;

            var backup = Path.Combine(backupRoot, label);

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);

                if (BuiltInSkills.IsBuiltIn(name))
                {
                    replaced.Add(name);
                }
                else if (string.Equals(name, RetiredSkill, StringComparison.OrdinalIgnoreCase))
                {
                    notMigrated.Add(name);
                }
                else
                {
                    var (done, note) = await ImportAsync(directory, name, store, ct);
                    if (done) imported.Add(name);
                    if (note is not null) warnings.Add($"{label}/{name}: {note}");
                }

                MoveAside(directory, Path.Combine(backup, name));
            }

            // The folder itself, once it is empty. A stray file left in it stays where it is.
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }

        return new Report(replaced, imported, notMigrated, warnings);
    }

    /// <summary>The roles an old skill is offered to. Frontmatter wins; otherwise the description
    /// names the role; otherwise `member`.</summary>
    public static IReadOnlyList<string> RolesFor(SkillFile.Parsed parsed)
    {
        if (parsed.Roles is { Count: > 0 } declared)
        {
            try
            {
                return SkillRoles.Normalise(declared);
            }
            catch (ArgumentException)
            {
                // Unreadable roles are not a reason to lose the skill: fall through to the text.
            }
        }

        var roles = new List<string>();
        if (NamesConcierge.IsMatch(parsed.Description)) roles.Add(SkillRoles.Concierge);
        if (NamesManager.IsMatch(parsed.Description)) roles.Add(SkillRoles.Manager);

        return roles.Count > 0 ? SkillRoles.Normalise(roles) : [SkillRoles.Member];
    }

    private static async Task<(bool Imported, string? Note)> ImportAsync(
        string directory, string name, ISkillStore store, CancellationToken ct)
    {
        var file = Path.Combine(directory, "SKILL.md");
        if (!File.Exists(file)) return (false, "SKILL.md is missing, so it was only backed up.");

        SkillFile.Parsed parsed;
        try
        {
            parsed = SkillFile.Parse(await File.ReadAllTextAsync(file, ct));
        }
        catch (ArgumentException exception)
        {
            return (false, $"SKILL.md could not be read ({exception.Message}), so it was only backed up.");
        }

        if (await store.GetAsync(name, ct) is not null)
        {
            return (false, "a skill by that name already exists, so this copy was only backed up.");
        }

        var extra = Directory.EnumerateFileSystemEntries(directory)
            .Any(entry => !string.Equals(Path.GetFileName(entry), "SKILL.md", StringComparison.Ordinal));

        try
        {
            await store.CreateCustomAsync(
                new SkillDraft(
                    name,
                    string.IsNullOrWhiteSpace(parsed.Description) ? $"Imported skill {name}." : parsed.Description,
                    RolesFor(parsed),
                    parsed.Body),
                "migration",
                ct);
        }
        catch (Exception exception) when (exception is ArgumentException or SkillRefusedException)
        {
            return (false, $"{exception.Message} It was only backed up.");
        }

        return (true, extra ? "imported; the files beside SKILL.md are in the backup only." : null);
    }

    /// <summary>Moves a folder to its backup, beside any earlier backup of the same name.</summary>
    private static void MoveAside(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var target = destination;
        for (var n = 2; Directory.Exists(target) || File.Exists(target); n++)
        {
            target = $"{destination}-{n}";
        }

        Directory.Move(source, target);
    }
}
