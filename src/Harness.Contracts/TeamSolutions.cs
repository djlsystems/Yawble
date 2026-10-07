namespace Harness.Contracts;

/// <summary>
/// One team's <c>team_solutions</c> row: the solution package it was installed from, and what the
/// install made of it, so an update can show a diff and change only what the package made.
/// </summary>
/// <param name="Team">The team's stored id.</param>
/// <param name="Manifest">The package's <c>solution.json</c> as installed, verbatim.</param>
/// <param name="Digests">SHA-256 per installed file: <c>skill:&lt;name&gt;</c>, <c>site:&lt;name&gt;</c>
/// (over every file of the site) and <c>tool:&lt;path&gt;</c>.</param>
/// <param name="Members">Package member name to the member id it became.</param>
/// <param name="Triggers">Package trigger name to its trigger row id.</param>
/// <param name="Plugins">Plugin id to the version the package shipped.</param>
public sealed record TeamSolutionRow(
    string Team,
    string PackageId,
    string Name,
    string Version,
    string Folder,
    DateTimeOffset InstalledAt,
    string? InstalledBy,
    DateTimeOffset? UpdatedAt,
    string Manifest,
    IReadOnlyDictionary<string, string> Digests,
    IReadOnlyDictionary<string, string> Members,
    IReadOnlyDictionary<string, string> Triggers,
    IReadOnlyDictionary<string, string> Plugins)
{
    /// <summary>When the package was uninstalled, keeping the team and its sites; null while it is
    /// installed. An uninstalled row is the record that the team held this package, which a
    /// reinstall onto the same team reads; <see cref="ITeamSolutionStore.FindAsync"/> never answers it.</summary>
    public DateTimeOffset? UninstalledAt { get; init; }

    /// <summary>The person's answers when it was uninstalled, as <c>{"settings":{member:{setting:value}},
    /// "connections":{member:{slot:id}}}</c>, for the reinstall's wizard to prefill; null while installed.</summary>
    public string? Answers { get; init; }
}

/// <summary>The <c>team_solutions</c> table. Every write lands with its tenant row or not at all.</summary>
public interface ITeamSolutionStore
{
    /// <summary>The package the team has installed; null when none is (an uninstalled row included).</summary>
    Task<TeamSolutionRow?> FindAsync(string team, CancellationToken ct = default);

    /// <summary>Every installed row; uninstalled rows are not listed.</summary>
    Task<IReadOnlyList<TeamSolutionRow>> ListAsync(CancellationToken ct = default);

    /// <summary>The record of a package the team had and uninstalled, keeping its sites; null when
    /// the team has a package installed or never had one.</summary>
    Task<TeamSolutionRow?> FindUninstalledAsync(string team, CancellationToken ct = default);

    /// <summary>Every uninstalled row.</summary>
    Task<IReadOnlyList<TeamSolutionRow>> ListUninstalledAsync(CancellationToken ct = default);

    /// <summary>Inserts or replaces the team's row, installed or uninstalled as
    /// <see cref="TeamSolutionRow.UninstalledAt"/> says, with <paramref name="audit"/> in one transaction.</summary>
    Task SaveAsync(TeamSolutionRow row, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>Removes the team's row, installed or uninstalled; <paramref name="audit"/>, when given, lands with it. Answers
    /// whether there was one.</summary>
    Task<bool> DeleteAsync(string team, TriggerAudit? audit, CancellationToken ct = default);
}
