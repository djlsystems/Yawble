using System.Text.Json;
using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>A member hired on a plugin, as a refusal names it: team and member ids, and what a person
/// reads for each.</summary>
public sealed record PluginHiredMember(string Team, string Member, string TeamName, string MemberName);

/// <summary>What a removal did, or why it did nothing. <paramref name="Status"/> is the HTTP status the
/// route answers: 200 removed, 400 not an id or a version, 404 no such plugin or version, 409 refused
/// (members hired on it, named in <paramref name="Members"/>, or the active version while others are
/// kept), 500 the files could not be moved and nothing was removed.</summary>
public sealed record PluginRemoveResult(
    int Status, string? Id, string? Version, bool Whole, IReadOnlyList<string> Versions, string? Reason,
    IReadOnlyList<PluginHiredMember> Members)
{
    public static PluginRemoveResult Refused(int status, string reason, string? id = null, string? version = null,
        IReadOnlyList<PluginHiredMember>? members = null) =>
        new(status, id, version, false, [], reason, members ?? []);
}

/// <summary>
/// REMOVES A PLUGIN, OR ONE VERSION OF IT, from the plugins directory: the web app's half of
/// the operator CLI's <c>plugin remove</c>, with the same rules (<c>cli/internal/cli/plugin.go</c>).
///
/// <list type="bullet">
/// <item>The whole plugin - asked for, or its only version - is REFUSED WHILE A MEMBER IS HIRED ON IT,
/// naming each one. A kept version is removed whoever is hired: no member runs it.</item>
/// <item>The ACTIVE version while others are kept is refused: its members would be left on nothing.
/// Install the version to use first, or remove the whole plugin.</item>
/// </list>
///
/// <para>
/// THE TENANT ROW AND THE FILES GO TOGETHER. The row is appended in a transaction, the folder is
/// moved aside (one rename, on the same volume), and only then is the transaction committed; a move
/// that fails rolls the row back, and a commit that fails moves the folder back. What was moved aside
/// is deleted after the commit, and the catalog is rescanned, which unregisters the plugin's skill and
/// events. Deleting a team never comes here: a plugin is the instance's, not a team's.
/// </para>
/// </summary>
public sealed class PluginRemover(PluginCatalog catalog, PluginInstaller installer, TeamRegistry teams, string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
    }.ToString();

    /// <summary>Removes plugin <paramref name="id"/>, or only its <paramref name="version"/>, recording
    /// <paramref name="audit"/>'s actor on a <see cref="TenantActions.PluginRemoved"/> row.</summary>
    public Task<PluginRemoveResult> RemoveAsync(string id, string? version, TriggerAudit audit, CancellationToken ct = default) =>
        installer.ExclusiveAsync(() => RemoveLockedAsync(id, string.IsNullOrWhiteSpace(version) ? null : version.Trim(), audit, ct), ct);

    /// <summary>Who is hired on plugin <paramref name="id"/>, ordered by team then member.</summary>
    public IReadOnlyList<PluginHiredMember> HiredOn(string id) =>
        [.. teams.All()
            .SelectMany(team => team.Containers.Select(member => (team, member)))
            .Where(pair => MemberRef.IsPlugin(pair.member.Agent, out var hired) && hired == id)
            .Select(pair => new PluginHiredMember(pair.team.Id, pair.member.Id, pair.team.Name, pair.member.Name))
            .OrderBy(m => m.TeamName, StringComparer.Ordinal)
            .ThenBy(m => m.MemberName, StringComparer.Ordinal)];

    private async Task<PluginRemoveResult> RemoveLockedAsync(string id, string? version, TriggerAudit audit, CancellationToken ct)
    {
        if (!MemberRef.IsValidPluginId(id))
        {
            return PluginRemoveResult.Refused(400, $"'{id}' is not a plugin id (lowercase letters, digits and hyphens).");
        }

        if (version is not null && (version is "." or ".." || version.StartsWith('.') || version.Contains('/') || version.Contains('\\')))
        {
            return PluginRemoveResult.Refused(400, $"'{version}' is not a plugin version.", id);
        }

        var pluginDirectory = Path.Combine(catalog.Root, id);

        if (!Directory.Exists(pluginDirectory))
        {
            return PluginRemoveResult.Refused(404, $"Plugin {id} is not installed.", id, version);
        }

        var versions = Directory.EnumerateDirectories(pluginDirectory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(v => !v.StartsWith('.'))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (version is not null && !versions.Contains(version))
        {
            return PluginRemoveResult.Refused(
                404, $"Plugin {id} has no version {version} (it has: {(versions.Count == 0 ? "none" : string.Join(", ", versions))}).", id, version);
        }

        var whole = version is null || versions.Count == 1;

        if (!whole && version == InUse(pluginDirectory, versions))
        {
            return PluginRemoveResult.Refused(
                409,
                $"{id} {version} is the active version. Install the version you want to use (Install from a folder, with Replace), "
                + "then remove this one; or remove the whole plugin.",
                id, version);
        }

        if (whole && HiredOn(id) is { Count: > 0 } hired)
        {
            return PluginRemoveResult.Refused(
                409,
                $"Plugin {id} is in use by {string.Join(", ", hired.Select(m => $"{m.TeamName} / {m.MemberName}"))}; remove those members first.",
                id, version, hired);
        }

        var removed = whole ? versions : [version!];
        var target = whole ? pluginDirectory : Path.Combine(pluginDirectory, version!);

        // Aside on the same volume, where no scan reads it: the data root for a whole plugin (a dot
        // folder under the plugins root would be read, and refused, as a plugin), the plugin's own
        // folder for one version (the catalog and the listing skip its dot folders, as the installer's).
        var aside = whole
            ? Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(catalog.Root))!, $".plugin-removed-{id}-{Guid.NewGuid():N}")
            : Path.Combine(pluginDirectory, $".removed-{version}-{Guid.NewGuid():N}");

        var row = audit with
        {
            Action = TenantActions.PluginRemoved,
            Subject = id,
            SubjectName = id,
            Detail = JsonSerializer.Serialize(new { id, whole, versions = removed }),
        };

        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

            await TenantAuditRow.AppendAsync(connection, transaction, row, ct);

            try
            {
                Directory.Move(target, aside);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The transaction is disposed uncommitted: no row for a removal that did not happen.
                return PluginRemoveResult.Refused(500, $"{Path.GetFileName(target)} could not be removed: {exception.Message}", id, version);
            }

            try
            {
                await transaction.CommitAsync(ct);
            }
            catch
            {
                Directory.Move(aside, target);
                throw;
            }
        }

        TryDelete(aside);

        await catalog.RescanAsync(ct);

        return new PluginRemoveResult(200, id, whole ? null : version, whole, removed, null, []);
    }

    /// <summary>The version the Host loads: <c>active</c>, or the only version when there is none -
    /// the catalog's reading, and the CLI's <c>inUse</c>.</summary>
    private static string? InUse(string pluginDirectory, IReadOnlyList<string> versions)
    {
        var active = Path.Combine(pluginDirectory, PluginCatalog.ActiveFile);
        if (File.Exists(active)) return File.ReadAllText(active).Trim();
        return versions.Count == 1 ? versions[0] : null;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"WARNING: a removed plugin folder was left at {directory}: {exception.Message}");
        }
    }
}
