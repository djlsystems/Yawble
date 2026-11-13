using Microsoft.Data.Sqlite;
using Yawble.Core.Connections;

namespace Yawble.Core.Storage;

/// <summary>The Connections a backup's sidecar lists. Names only; a credential value is never put in a sidecar.</summary>
public static class BackupConnections
{
    /// <summary>
    /// The Connections as <see cref="ConnectionService.SecretKeys"/> sees them: id, name, kind, and the keys of the secret fields
    /// that are set (and those the store cannot vouch for without reading). No credential value is read. Never throws: a sidecar
    /// without Connections is better than no backup.
    /// </summary>
    public static IReadOnlyList<BackupConnection> FromService(ConnectionService service)
    {
        try
        {
            return service.SecretKeys()
                .Select(c => new BackupConnection(c.Id, c.Name, c.Kind, c.Set, c.Unknown))
                .ToList();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return []; }
    }

    /// <summary>
    /// The connections table alone (id, name, kind; no secret field keys), for a backup made before the Connection service
    /// exists (the one before a migration). Never throws.
    /// </summary>
    public static IReadOnlyList<BackupConnection> FromDatabase(string connectionString)
    {
        try
        {
            using var c = new SqliteConnection(connectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, name, kind FROM connections ORDER BY id;";
            using var r = cmd.ExecuteReader();
            var list = new List<BackupConnection>();
            while (r.Read()) list.Add(new BackupConnection(r.GetInt64(0), r.GetString(1), r.GetString(2), []));
            return list;
        }
        catch (SqliteException) { return []; }
    }
}
