using Harness.Contracts;
using Harness.Host.Solutions;

namespace Harness.Host;

/// <summary>A member hired on a plugin id, by team and member, with the names a person reads.</summary>
public sealed record PluginMemberOn(string Team, string Member, string TeamName, string MemberName);

/// <summary>An installed plugin a newer one's manifest says it replaces: its id, active version, name,
/// and every member still on it, team by team.</summary>
public sealed record ReplacedPlugin(string Id, string Version, string Name, IReadOnlyList<PluginMemberOn> Members);

/// <summary>An installed plugin whose manifest says it replaces another: its id, active version and name.</summary>
public sealed record ReplacingPlugin(string Id, string Version, string Name);

/// <summary>What moving one member onto the replacing plugin did: <c>moved</c>, or <c>failed</c> with the
/// sentence why, and then nothing about that member changed.</summary>
public sealed record ReplacementMove(string Team, string Member, string TeamName, string MemberName, string Outcome, string? Reason);

/// <summary>
/// A RENAMED PLUGIN SAYS WHAT IT REPLACES (PLG 3, rename). Only the package's own manifest says it
/// (<see cref="PluginManifest.Replaces"/>), never a guess from a name or an id: two installed plugins
/// that merely share a name are two plugins.
///
/// <list type="bullet">
/// <item><see cref="ReplacedBy"/>: each replaced id still installed, with its version and every member
/// on it - what an install answers and the Plugins dialog shows.</item>
/// <item><see cref="ReplacingOf"/>: the installed plugins that say they replace an id.</item>
/// <item><see cref="MoveAsync"/>: a PERSON'S press moves members on the old id onto the new one, through
/// the same repoint a solution update makes (<see cref="RepointAsync"/>): settings and own secret values
/// are keyed by team and member, so they are carried as they are - a value for a setting the new
/// manifest no longer declares is kept too, as today. Each member is one transaction with its
/// <c>member.changed</c> row naming from and to; when that row cannot be written nothing about the
/// member changes.</item>
/// </list>
///
/// Nothing here removes anything, and nothing runs by itself: the old plugin stays installed until a
/// person removes it (<see cref="PluginRemover"/>, which refuses while any member is on it).
/// </summary>
public sealed class PluginReplacements(PluginCatalog catalog, TeamRegistry teams)
{
    /// <summary>Every member hired on <c>plugin:<paramref name="id"/></c>, in team then member order.</summary>
    public IReadOnlyList<PluginMemberOn> MembersOn(string id) =>
    [
        .. teams.All()
            .SelectMany(team => team.Containers.Select(member => (team, member)))
            .Where(pair => MemberRef.IsPlugin(pair.member.Agent, out var hired) && hired == id)
            .OrderBy(pair => pair.team.Id, StringComparer.Ordinal)
            .ThenBy(pair => pair.member.Id, StringComparer.Ordinal)
            .Select(pair => new PluginMemberOn(pair.team.Id, pair.member.Id, pair.team.Name, pair.member.Name)),
    ];

    /// <summary>Each id <paramref name="manifest"/> replaces that is installed now, in the manifest's
    /// order, with its version and the members on it. An id it names that is not installed is not listed.</summary>
    public IReadOnlyList<ReplacedPlugin> ReplacedBy(PluginManifest manifest) =>
    [
        .. manifest.Replaces
            .Select(catalog.For)
            .OfType<InstalledPlugin>()
            .Where(old => !old.BuiltIn)
            .Select(old => new ReplacedPlugin(old.Manifest.Id, old.Manifest.Version, old.Manifest.Name, MembersOn(old.Manifest.Id))),
    ];

    /// <summary>The installed plugins whose manifest says they replace <paramref name="id"/>, by id.</summary>
    public IReadOnlyList<ReplacingPlugin> ReplacingOf(string id) =>
    [
        .. catalog.Plugins
            .Where(p => !p.BuiltIn && p.Manifest.Replaces.Contains(id, StringComparer.Ordinal))
            .OrderBy(p => p.Manifest.Id, StringComparer.Ordinal)
            .Select(p => new ReplacingPlugin(p.Manifest.Id, p.Manifest.Version, p.Manifest.Name)),
    ];

    /// <summary>
    /// Why members on <paramref name="oldId"/> may not be moved onto <paramref name="newId"/>, as an HTTP
    /// status and a sentence; null when they may: the new plugin is installed and its own manifest names
    /// the old id in <c>replaces</c>.
    /// </summary>
    public (int Status, string Reason)? Refusal(string newId, string oldId)
    {
        if (!MemberRef.IsValidPluginId(newId) || !MemberRef.IsValidPluginId(oldId))
        {
            return (StatusCodes.Status400BadRequest, "Name two plugin ids.");
        }

        if (catalog.For(newId) is not { } replacing)
        {
            return (StatusCodes.Status404NotFound, $"'{newId}' is not installed, so nothing can be moved onto it.");
        }

        if (replacing.BuiltIn) return (StatusCodes.Status409Conflict, PluginCatalog.BuiltInRefusal(newId));

        if (!replacing.Manifest.Replaces.Contains(oldId, StringComparer.Ordinal))
        {
            return (StatusCodes.Status409Conflict,
                $"{replacing.Manifest.Name} ({newId} {replacing.Manifest.Version}) does not say it replaces '{oldId}'; "
                + "only a plugin whose own manifest names the old id in `replaces` takes its members this way.");
        }

        return null;
    }

    /// <summary>
    /// Moves the members on <paramref name="oldId"/> onto <paramref name="newId"/> - each one named in
    /// <paramref name="only"/> (team, member), or every one when it is null - one member and its
    /// <c>member.changed</c> row per transaction, in team then member order. A member that cannot be moved
    /// is answered <c>failed</c> with the sentence, unchanged, and the rest are still tried. Call
    /// <see cref="Refusal"/> first.
    /// </summary>
    public async Task<IReadOnlyList<ReplacementMove>> MoveAsync(
        string newId, string oldId, IReadOnlyCollection<(string Team, string Member)>? only, SolutionActor actor, CancellationToken ct = default)
    {
        var results = new List<ReplacementMove>();

        foreach (var member in MembersOn(oldId))
        {
            if (only is not null && !only.Any(o =>
                    string.Equals(o.Team, member.Team, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(o.Member, member.Member, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                await RepointAsync(teams, member.Team, member.Member, member.MemberName, oldId, newId, actor,
                    new { replaces = oldId }, ct);
                results.Add(new ReplacementMove(member.Team, member.Member, member.TeamName, member.MemberName, "moved", null));
            }
            catch (Exception refused) when (refused is not OperationCanceledException)
            {
                results.Add(new ReplacementMove(member.Team, member.Member, member.TeamName, member.MemberName, "failed", refused.Message));
            }
        }

        return results;
    }

    /// <summary>
    /// THE ONE REPOINT OF A PLUGIN MEMBER ONTO ANOTHER PLUGIN, which a solution update and a replacement
    /// both make: its stored agent becomes <c>plugin:<paramref name="to"/></c>, with a <c>member.changed</c>
    /// row naming <c>from</c> and <c>to</c> (and whatever <paramref name="detail"/> adds) in the same
    /// transaction - so when the row cannot be written the member is not moved. Its settings and its own
    /// secret values are keyed by team and member, so they stay as they are.
    /// </summary>
    public static Task RepointAsync(
        TeamRegistry teams, string team, string id, string memberName, string from, string to, SolutionActor actor,
        object? detail, CancellationToken ct)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["from"] = MemberRef.ForPlugin(from),
            ["to"] = MemberRef.ForPlugin(to),
        };

        if (detail is not null)
        {
            foreach (var property in detail.GetType().GetProperties()) fields[property.Name] = property.GetValue(detail);
        }

        return teams.UpdateMemberAsync(
            team, id, null, null, MemberRef.ForPlugin(to), actor.PromptSetter, ct,
            change => [actor.Row(TenantActions.MemberChanged, $"{team}/{id}", memberName, fields)]);
    }
}
