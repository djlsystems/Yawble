using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// <see cref="ISiteStore"/> over the one database. Every site and version change commits with its
/// <c>tenant_events</c> row (<see cref="TenantAuditRow"/>) or not at all. A data write checks its
/// limits inside the transaction that writes it, so two writes racing cannot both slip under a bound.
/// </summary>
public sealed class SqliteSiteStore : ISiteStore
{
    private readonly string _connectionString;

    private readonly SqliteDurability _durability;

    public SqliteSiteStore(string databasePath, SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        _durability = durability;
    }

    public async Task<IReadOnlyList<SiteRow>> ListAsync(string? team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = team is null
            ? $"{SelectSite} ORDER BY team, name"
            : $"{SelectSite} WHERE team = $team ORDER BY name";
        if (team is not null) command.Parameters.AddWithValue("$team", team);

        return await ReadSitesAsync(command, ct);
    }

    public async Task<SiteRow?> FindAsync(string team, string name, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = $"{SelectSite} WHERE team = $team AND name = $name";
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$name", name);

        return (await ReadSitesAsync(command, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<SiteVersionRow>> VersionsAsync(string team, string name, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT version, published_at, published_by, source, files, bytes
              FROM site_versions WHERE team = $team AND site = $site ORDER BY version DESC
            """;
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$site", name);

        var rows = new List<SiteVersionRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SiteVersionRow(
                reader.GetInt32(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    public async Task<bool> CreateAsync(SiteRow row, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = Command(connection, transaction,
            """
            INSERT INTO sites (team, name, live_version, next_version, created_at, created_by)
            VALUES ($team, $name, NULL, 1, $at, $by)
            ON CONFLICT (team, name) DO NOTHING
            """))
        {
            command.Parameters.AddWithValue("$team", row.Team);
            command.Parameters.AddWithValue("$name", row.Name);
            command.Parameters.AddWithValue("$at", row.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$by", row.CreatedBy);

            if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task RecordPublishAsync(
        string team, string name, SiteVersionRow version, IReadOnlyList<int> pruned, TriggerAudit audit,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = Command(connection, transaction,
            """
            INSERT INTO site_versions (team, site, version, published_at, published_by, source, files, bytes)
            VALUES ($team, $site, $version, $at, $by, $source, $files, $bytes)
            """))
        {
            command.Parameters.AddWithValue("$team", team);
            command.Parameters.AddWithValue("$site", name);
            command.Parameters.AddWithValue("$version", version.Version);
            command.Parameters.AddWithValue("$at", version.PublishedAt.ToString("O"));
            command.Parameters.AddWithValue("$by", version.PublishedBy);
            command.Parameters.AddWithValue("$source", version.Source);
            command.Parameters.AddWithValue("$files", version.Files);
            command.Parameters.AddWithValue("$bytes", version.Bytes);
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var command = Command(connection, transaction,
            """
            UPDATE sites SET live_version = $version, next_version = MAX(next_version, $version + 1)
             WHERE team = $team AND name = $name
            """))
        {
            command.Parameters.AddWithValue("$team", team);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$version", version.Version);

            if (await command.ExecuteNonQueryAsync(ct) == 0)
            {
                throw new InvalidOperationException($"No site '{name}' in team '{team}' to publish.");
            }
        }

        foreach (var old in pruned)
        {
            await using var command = Command(connection, transaction,
                "DELETE FROM site_versions WHERE team = $team AND site = $site AND version = $version");
            command.Parameters.AddWithValue("$team", team);
            command.Parameters.AddWithValue("$site", name);
            command.Parameters.AddWithValue("$version", old);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task SetLiveAsync(string team, string name, int? version, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = Command(connection, transaction,
            "UPDATE sites SET live_version = $version WHERE team = $team AND name = $name"))
        {
            command.Parameters.AddWithValue("$team", team);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$version", (object?)version ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> DeleteAsync(string team, string name, TriggerAudit audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        if (await DeleteSiteRowsAsync(connection, transaction, team, name, ct) == 0) return false;

        await TenantAuditRow.AppendAsync(connection, transaction, audit, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<string>> DeleteTeamAsync(
        string team, Func<string, TriggerAudit> audit, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var names = new List<string>();

        await using (var command = Command(connection, transaction, "SELECT name FROM sites WHERE team = $team ORDER BY name"))
        {
            command.Parameters.AddWithValue("$team", team);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        }

        foreach (var name in names)
        {
            await DeleteSiteRowsAsync(connection, transaction, team, name, ct);
            await TenantAuditRow.AppendAsync(connection, transaction, audit(name), ct);
        }

        // Data or versions left by a site row that is already gone: never listed, never counted.
        foreach (var table in new[] { "site_versions", "site_documents" })
        {
            await using var command = Command(connection, transaction, $"DELETE FROM {table} WHERE team = $team");
            command.Parameters.AddWithValue("$team", team);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return names;
    }

    private static async Task<int> DeleteSiteRowsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string team, string name, CancellationToken ct)
    {
        foreach (var table in new[] { "site_versions", "site_documents" })
        {
            await using var command = Command(connection, transaction, $"DELETE FROM {table} WHERE team = $team AND site = $site");
            command.Parameters.AddWithValue("$team", team);
            command.Parameters.AddWithValue("$site", name);
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var site = Command(connection, transaction, "DELETE FROM sites WHERE team = $team AND name = $name");
        site.Parameters.AddWithValue("$team", team);
        site.Parameters.AddWithValue("$name", name);
        return await site.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SiteDocument>> ListDocumentsAsync(
        string team, string site, string collection, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = $"{SelectDocument} WHERE team = $team AND site = $site AND collection = $collection ORDER BY id";
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$site", site);
        command.Parameters.AddWithValue("$collection", collection);

        return await ReadDocumentsAsync(command, ct);
    }

    public async Task<IReadOnlyList<string>> CollectionsAsync(string team, string site, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT DISTINCT collection FROM site_documents WHERE team = $team AND site = $site ORDER BY collection";
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$site", site);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        return names;
    }

    public async Task<SiteDocument?> GetDocumentAsync(
        string team, string site, string collection, string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = $"{SelectDocument} WHERE team = $team AND site = $site AND collection = $collection AND id = $id";
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$site", site);
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$id", id);

        return (await ReadDocumentsAsync(command, ct)).SingleOrDefault();
    }

    public async Task<SiteWriteOutcome> PutDocumentAsync(
        string team, string site, string collection, string id, string json, string by, DateTimeOffset at,
        CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetByteCount(json);

        if (bytes > SiteRules.MaxDocumentBytes)
        {
            return SiteWriteOutcome.Refused(SiteRules.DocumentTooLarge(bytes), 413);
        }

        await using var connection = Open();

        // IMMEDIATE (Microsoft.Data.Sqlite's default, not deferred), so the counts below and the
        // write after them see one state: two writers cannot both pass the same bound.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        long? existing = null;

        await using (var command = Command(connection, transaction,
            "SELECT bytes FROM site_documents WHERE team = $team AND site = $site AND collection = $collection AND id = $id"))
        {
            Bind(command, team, site, collection, id);
            if (await command.ExecuteScalarAsync(ct) is long found) existing = found;
        }

        if (existing is null)
        {
            await using var count = Command(connection, transaction,
                "SELECT COUNT(*) FROM site_documents WHERE team = $team AND site = $site AND collection = $collection");
            Bind(count, team, site, collection, null);

            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) >= SiteRules.MaxDocumentsPerCollection)
            {
                return SiteWriteOutcome.Refused(
                    $"The collection \"{collection}\" already holds {SiteRules.MaxDocumentsPerCollection} documents, "
                    + "the most one collection may hold. Delete some, or use another collection.", 413);
            }
        }

        await using (var total = Command(connection, transaction,
            "SELECT COALESCE(SUM(bytes), 0) FROM site_documents WHERE team = $team AND site = $site"))
        {
            Bind(total, team, site, null, null);
            var used = Convert.ToInt64(await total.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

            if (used - (existing ?? 0) + bytes > SiteRules.MaxSiteDataBytes)
            {
                return SiteWriteOutcome.Refused(
                    $"This write would take the site \"{site}\" past its limit of {SiteRules.MaxSiteDataBytes} bytes "
                    + $"(50 MB) of data; it holds {used} bytes now. Delete documents it no longer needs.", 413);
            }
        }

        await using (var write = Command(connection, transaction,
            """
            INSERT INTO site_documents (team, site, collection, id, doc, bytes, updated_at, updated_by)
            VALUES ($team, $site, $collection, $id, $doc, $bytes, $at, $by)
            ON CONFLICT (team, site, collection, id) DO UPDATE SET
                doc = excluded.doc, bytes = excluded.bytes,
                updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """))
        {
            Bind(write, team, site, collection, id);
            write.Parameters.AddWithValue("$doc", json);
            write.Parameters.AddWithValue("$bytes", bytes);
            write.Parameters.AddWithValue("$at", at.ToString("O"));
            write.Parameters.AddWithValue("$by", by);
            await write.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return SiteWriteOutcome.Ok;
    }

    public async Task<bool> DeleteDocumentAsync(
        string team, string site, string collection, string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "DELETE FROM site_documents WHERE team = $team AND site = $site AND collection = $collection AND id = $id";
        Bind(command, team, site, collection, id);

        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<SiteDataUsage> UsageAsync(string team, string site, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT COUNT(*), COALESCE(SUM(bytes), 0) FROM site_documents WHERE team = $team AND site = $site";
        Bind(command, team, site, null, null);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new SiteDataUsage(reader.GetInt32(0), reader.GetInt64(1));
    }

    private const string SelectSite =
        "SELECT team, name, live_version, next_version, created_at, created_by FROM sites";

    private const string SelectDocument =
        "SELECT collection, id, doc, updated_at, updated_by FROM site_documents";

    private static async Task<IReadOnlyList<SiteRow>> ReadSitesAsync(SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<SiteRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SiteRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetString(5)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<SiteDocument>> ReadDocumentsAsync(SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<SiteDocument>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SiteDocument(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetString(4)));
        }

        return rows;
    }

    private static void Bind(SqliteCommand command, string team, string site, string? collection, string? id)
    {
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$site", site);
        if (collection is not null) command.Parameters.AddWithValue("$collection", collection);
        if (id is not null) command.Parameters.AddWithValue("$id", id);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }
}
