using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Every skill's name, description and roles, held in memory for prompt composition.
///
/// A system prompt is composed synchronously - at creation, restore and every re-prompt - so the
/// "Available skills" list cannot wait on the database. This holds the built-ins from the build
/// from the moment it exists, and the custom skills from the last <see cref="RefreshAsync"/>, which
/// runs at start and after every custom skill write.
///
/// <para>
/// <b>NEVER A PLUGIN'S SKILL.</b> Every installed plugin ships one, and listing them would grow every
/// prompt with every install. A plugin's skill is found by its Manager's roster (for the plugins on
/// its team), by <c>skills_search</c>, and by the <c>hiring</c> tool - never here.
/// </para>
///
/// <para>
/// A TEAM SKILL is listed only for that team's members of its roles (<see cref="For"/> given the
/// team); with no team - the Concierge - none is.
/// </para>
/// </summary>
public sealed class SkillDirectory
{
    public sealed record Entry(string Name, string Description, IReadOnlyList<string> Roles, string? Team = null);

    private IReadOnlyList<Entry> _entries =
        [.. BuiltInSkills.All.Select(s => new Entry(s.Name, s.Description, s.Roles))];

    /// <summary>Raised after a refresh changed what some role is offered.</summary>
    public event Action? Changed;

    /// <summary>The skills <paramref name="role"/> on <paramref name="team"/> is offered, by name:
    /// the instance-wide ones and that team's own.</summary>
    public IReadOnlyList<(string Name, string Description)> For(string role, string? team = null) =>
        [.. _entries
            .Where(e => e.Team is null || (team is not null && string.Equals(e.Team, team, StringComparison.OrdinalIgnoreCase)))
            .Where(e => SkillRoles.Offers(e.Roles, role))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => (e.Name, e.Description))];

    public async Task RefreshAsync(ISkillStore store, CancellationToken ct = default)
    {
        var entries = new List<Entry>();
        long? before = null;

        while (true)
        {
            var page = await store.ListAsync(SkillKindFilter.All, null, null, before, 200, ct);
            entries.AddRange(page.Where(s => s.Kind != SkillKind.Plugin).Select(s => new Entry(s.Name, s.Description, s.Roles, s.Team)));
            if (page.Count < 200) break;
            before = page[^1].Id;
        }

        var changed = !entries
            .Select(Key)
            .Order(StringComparer.Ordinal)
            .SequenceEqual(_entries.Select(Key).Order(StringComparer.Ordinal));

        _entries = entries;

        if (changed) Changed?.Invoke();
    }

    private static string Key(Entry e) => $"{e.Name}\n{e.Description}\n{string.Join(' ', e.Roles)}\n{e.Team}";
}
