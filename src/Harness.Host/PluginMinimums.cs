using Harness.Contracts;
using Harness.Host.Solutions;

namespace Harness.Host;

/// <summary>One installed solution team's need of a plugin: the team, its package and version, and
/// the oldest version of the plugin it needs.</summary>
public sealed record PluginNeed(string Team, string TeamName, string PackageId, string Package, string Version, string Minimum);

/// <summary>
/// A PLUGIN IS THE INSTANCE'S, ONE ACTIVE VERSION, AND A SOLUTION TEAM MAY NEED A NEWER ONE THAN A
/// PERSON IS ABOUT TO MAKE ACTIVE. Each installed solution team keeps, per plugin its package ships,
/// the oldest version it needs (<see cref="TeamSolutionRow.MinimumFor"/>: the package's
/// <c>pluginMinimums</c>, else the version shipped). Making a version active - an install, a Replace,
/// a kept version installed again, through the route or the operator CLI's request file, all
/// <see cref="PluginInstaller"/> - is refused while it is older than any of those, naming each team.
///
/// <para>
/// An uninstalled (kept) package is not an installed team and needs nothing. A version newer than
/// the active one is never refused by this rule, so an instance below a minimum can always climb.
/// </para>
/// </summary>
public sealed class PluginMinimums(ITeamSolutionStore solutions, TeamRegistry teams)
{
    /// <summary>Every installed solution team whose package ships plugin <paramref name="id"/>, with
    /// the minimum it needs, by team.</summary>
    public async Task<IReadOnlyList<PluginNeed>> NeedsAsync(string id, CancellationToken ct = default) =>
        [.. (await solutions.ListAsync(ct))
            .Where(row => row.MinimumFor(id) is not null)
            .Select(row => new PluginNeed(row.Team, teams.LabelFor(row.Team), row.PackageId, row.Name, row.Version, row.MinimumFor(id)!))];

    /// <summary>
    /// The sentence refusing to make <paramref name="id"/> <paramref name="version"/> active over
    /// <paramref name="active"/> (null when none is), naming each installed team that needs newer:
    /// "sample-echo 0.1.0 is older than what Fixture Pack 1.0.0 on team Inbox needs (0.2.0 or
    /// newer)." Null when nothing needs newer, or the version is newer than the active one.
    /// </summary>
    public async Task<string?> RefusalAsync(string id, string version, string? active, CancellationToken ct = default)
    {
        if (active is not null && SolutionInstaller.CompareVersions(version, active) > 0) return null;

        var older = (await NeedsAsync(id, ct))
            .Where(need => SolutionInstaller.CompareVersions(version, need.Minimum) < 0)
            .Select(need => $"what {need.Package} {need.Version} on team {need.TeamName} needs ({need.Minimum} or newer)")
            .ToList();

        return older.Count == 0 ? null : $"{id} {version} is older than {string.Join(" and ", older)}.";
    }
}
