using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Storage;

/// <summary>
/// Applies the embedded Storage/Migrations/NNNN_name.sql files in order, each once, each in its own
/// transaction, and records them in schema_migrations. Append-only.
/// </summary>
public static class SchemaMigrator
{
    private const string ResourcePrefix = "Yawble.Core.Migrations.";

    /// <summary>Every migration shipped in this build, in order (e.g. "0001_baseline").</summary>
    public static IReadOnlyList<string> Available { get; } = typeof(SchemaMigrator).Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
        .Select(n => n[ResourcePrefix.Length..^4])
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>Brings the database up to date and returns the full list of applied migrations.</summary>
    public static IReadOnlyList<string> Migrate(string connectionString) => Migrate(connectionString, null);

    /// <summary>
    /// Brings the database up to date. When <paramref name="beforeFirstPending"/> is given, an existing database with at
    /// least one pending migration is handed to it once (with the name of the first pending migration) before anything is
    /// applied; if it throws, nothing is applied and a <see cref="MigrationBackupException"/> says why.
    /// </summary>
    public static IReadOnlyList<string> Migrate(string connectionString, Action<SqliteConnection, string>? beforeFirstPending)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var existed = !string.IsNullOrEmpty(dataSource) && dataSource != ":memory:" && File.Exists(dataSource) && new FileInfo(dataSource).Length > 0;
        using var connection = Db.OpenBare(connectionString);
        Execute(connection, null, "PRAGMA journal_mode=WAL;");
        Execute(connection, null, "PRAGMA foreign_keys=ON;");
        Execute(connection, null,
            "CREATE TABLE IF NOT EXISTS schema_migrations (name TEXT PRIMARY KEY NOT NULL, applied_at TEXT NOT NULL) STRICT;");

        var applied = Applied(connection);
        var pending = Available.Where(n => !applied.Contains(n)).ToList();
        if (existed && beforeFirstPending is not null && pending.Count > 0)
        {
            try { beforeFirstPending(connection, pending[0]); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new MigrationBackupException(
                    $"the database was not updated, because the backup before the update failed: {e.Message}", e);
            }
        }
        foreach (var name in pending)
        {
            var sql = ReadMigration(name);
            // A migration that rebuilds a table other tables point at (SQLite's 12-step table change) runs with foreign
            // keys off, so dropping the old table cascades into nothing; the keys are checked before it commits.
            var keysOff = sql.StartsWith(ForeignKeysOffMarker, StringComparison.Ordinal);
            if (keysOff) Execute(connection, null, "PRAGMA foreign_keys=OFF;");
            try
            {
                using var tx = connection.BeginTransaction();
                Execute(connection, tx, sql);
                if (keysOff) CheckForeignKeys(connection, tx, name);
                using var record = connection.CreateCommand();
                record.Transaction = tx;
                record.CommandText = "INSERT INTO schema_migrations (name, applied_at) VALUES ($n, $t);";
                record.Parameters.AddWithValue("$n", name);
                record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
                tx.Commit();
            }
            finally
            {
                if (keysOff) Execute(connection, null, "PRAGMA foreign_keys=ON;");
            }
        }
        return Applied(connection).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The first line of a migration that runs with foreign keys off (see <see cref="Migrate(string, Action{SqliteConnection, string}?)"/>).</summary>
    public const string ForeignKeysOffMarker = "-- yawble:foreign-keys-off";

    private static void CheckForeignKeys(SqliteConnection connection, SqliteTransaction tx, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA foreign_key_check;";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            throw new InvalidOperationException($"migration {name} left a broken foreign key in {reader.GetString(0)}");
    }

    private static HashSet<string> Applied(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM schema_migrations;";
        using var reader = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) set.Add(reader.GetString(0));
        return set;
    }

    private static string ReadMigration(string name)
    {
        using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream(ResourcePrefix + name + ".sql")
            ?? throw new InvalidOperationException($"migration {name} is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>The backup before a database update failed, so the update was not run.</summary>
public sealed class MigrationBackupException(string message, Exception inner) : Exception(message, inner);
