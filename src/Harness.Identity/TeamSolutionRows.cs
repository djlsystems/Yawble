using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// WHAT A <c>team_solutions</c> ROW HOLDS IN EACH COLUMN, whichever database holds it: the column list,
/// the value each saved column takes and the row reader, shared by both providers' solution stores so
/// a row either writes reads the same on the other.
/// </summary>
public static class TeamSolutionRows
{
    /// <summary>The columns <see cref="Read"/> reads, in its order, which is also the order of
    /// <see cref="SaveValues"/>.</summary>
    public const string Columns =
        """
        team, package_id, name, version, folder, installed_at, installed_by, updated_at,
                       manifest, digests, members, triggers, plugins, uninstalled_at, answers, plugin_minimums
        """;

    /// <summary>The value of every column a save writes, by parameter name without its prefix.</summary>
    public static IReadOnlyList<(string Name, object Value)> SaveValues(TeamSolutionRow row) =>
    [
        ("team", row.Team),
        ("id", row.PackageId),
        ("name", row.Name),
        ("version", row.Version),
        ("folder", row.Folder),
        ("installedAt", Instant.Stamp(row.InstalledAt)),
        ("installedBy", (object?)row.InstalledBy ?? DBNull.Value),
        ("updatedAt", row.UpdatedAt is { } updated ? Instant.Stamp(updated) : DBNull.Value),
        ("manifest", row.Manifest),
        ("digests", JsonSerializer.Serialize(row.Digests)),
        ("members", JsonSerializer.Serialize(row.Members)),
        ("triggers", JsonSerializer.Serialize(row.Triggers)),
        ("plugins", JsonSerializer.Serialize(row.Plugins)),
        ("uninstalledAt", row.UninstalledAt is { } uninstalled ? Instant.Stamp(uninstalled) : DBNull.Value),
        ("answers", (object?)row.Answers ?? DBNull.Value),
        ("pluginMinimums", JsonSerializer.Serialize(row.PluginMinimums)),
    ];

    public static TeamSolutionRow Read(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            reader.GetString(8),
            Map(reader.GetString(9)),
            Map(reader.GetString(10)),
            Map(reader.GetString(11)),
            Map(reader.GetString(12)))
        {
            UninstalledAt = reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
            Answers = reader.IsDBNull(14) ? null : reader.GetString(14),
            PluginMinimums = Map(reader.GetString(15)),
        };

    private static IReadOnlyDictionary<string, string> Map(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } map
            ? new Dictionary<string, string>(map, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
}
