using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Every skill's name, description and roles, held in memory for prompt composition.
///
/// A system prompt is composed synchronously - at creation, restore and every re-prompt - so the
/// "Available skills" list cannot wait on the database. This holds the built-ins from the build
/// from the moment it exists, and the custom skills from the last <see cref="RefreshAsync"/>, which
/// runs at start and after every custom skill write.
/// </summary>
public sealed class SkillDirectory
{
    public sealed record Entry(string Name, string Description, IReadOnlyList<string> Roles);

    private IReadOnlyList<Entry> _entries =
        [.. BuiltInSkills.All.Select(s => new Entry(s.Name, s.Description, s.Roles))];

    /// <summary>Raised after a refresh changed what some role is offered.</summary>
    public event Action? Changed;

    /// <summary>The skills <paramref name="role"/> is offered, by name.</summary>
    public IReadOnlyList<(string Name, string Description)> For(string role) =>
        [.. _entries
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
            entries.AddRange(page.Select(s => new Entry(s.Name, s.Description, s.Roles)));
            if (page.Count < 200) break;
            before = page[^1].Id;
        }

        var changed = !entries
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(Key)
            .SequenceEqual(_entries.OrderBy(e => e.Name, StringComparer.Ordinal).Select(Key));

        _entries = entries;

        if (changed) Changed?.Invoke();
    }

    private static string Key(Entry e) => $"{e.Name}\n{e.Description}\n{string.Join(' ', e.Roles)}";
}
