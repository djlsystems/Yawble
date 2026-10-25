using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Messaging;

namespace Harness.Host;

/// <summary>
/// WHAT A <c>run_steps</c> OR <c>file_changes</c> ROW HOLDS AND HOW IT READS BACK, for every provider's
/// <see cref="IAgentStepStore"/> and <see cref="IWorkspaceFileChangeStore"/>: the stamp each instant is
/// written as, the JSON a change's lists are kept in and the readers over a <see cref="DbDataReader"/>,
/// each column read by its position in the store's SELECT. No SQL that differs between providers.
/// </summary>
public static class MonitorRows
{
    /// <summary>A file change's columns, in the order <see cref="ReadChange"/> reads them.</summary>
    public const string ChangeColumns = "id, team, member, repo, at, files_json, commits_json, more, correlation";

    /// <summary>An instant as both tables keep it: round-trip UTC text of one width, so text order is time order.</summary>
    public static string Instant(DateTimeOffset at) => MessageSchema.FileChangeInstant(at);

    public static string FilesJson(WorkspaceChange change) => JsonSerializer.Serialize(change.Files, JsonSerializerOptions.Web);

    public static string CommitsJson(WorkspaceChange change) => JsonSerializer.Serialize(change.Commits, JsonSerializerOptions.Web);

    /// <summary>A step: id, team, member, run, workflow, at, kind, label, MCP tool and count.</summary>
    public static AgentStep ReadStep(DbDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        Parse(reader.GetString(5)),
        reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        checked((int)reader.GetInt64(9)));

    /// <summary>A recorded change, its columns as <see cref="ChangeColumns"/> lists them.</summary>
    public static RecordedWorkspaceChange ReadChange(DbDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        Parse(reader.GetString(4)),
        JsonSerializer.Deserialize<List<WorkspaceFile>>(reader.GetString(5), JsonSerializerOptions.Web) ?? [],
        JsonSerializer.Deserialize<List<WorkspaceCommit>>(reader.GetString(6), JsonSerializerOptions.Web) ?? [],
        checked((int)reader.GetInt64(7)),
        reader.IsDBNull(8) ? null : reader.GetInt64(8));

    private static DateTimeOffset Parse(string stamp) =>
        DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
