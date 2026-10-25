using System.Globalization;
using Harness.Contracts;
using Harness.Identity;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Identity;

/// <summary>
/// <see cref="ISiteStore"/> over <c>sites</c>, <c>site_versions</c> and <c>site_documents</c> on SQL
/// Server: SQLite's <c>SqliteSiteStore</c> write for write, whose remarks are the argument. How a row
/// reads back and what a data write is refused for are <see cref="SiteRows"/>', shared with it. Every
/// site and version change commits with its <c>tenant_events</c> row
/// (<see cref="SqlServerTenantAuditRow"/>) or not at all.
///
/// <para>
/// A TEAM FOLDS CASE, AS SQLITE'S <c>COLLATE NOCASE</c> COLUMN DOES; a site, a collection and a
/// document id do not. Every text comparison is <see cref="SqlServerSql.Same"/>, so a trailing space
/// decides it as it does in SQLite.
/// </para>
///
/// <para>
/// SQLITE RUNS ONE WRITER AT A TIME, AND A DATA WRITE CHECKS ITS LIMITS INSIDE THE TRANSACTION THAT
/// WRITES IT, so two writes racing cannot both slip under a bound. Here they could: each would count
/// the site's bytes before the other's row commits. So a data write first takes its site's
/// <c>sites</c> row under <c>UPDLOCK, HOLDLOCK</c> (<see cref="SiteLock"/>; a held key range, so a
/// site with no row is held too) and only then counts and writes: two writes to one site wait for each
/// other, and the second counts the first's row. A create reads whether the name is taken under the
/// same hint, so two creates of one name make one site and the other answers false, as SQLite's
/// <c>ON CONFLICT DO NOTHING</c> does, rather than failing on the key.
/// </para>
///
/// <para>
/// LOCK ORDER: THE <c>sites</c> ROW, THEN ITS <c>site_versions</c> AND <c>site_documents</c> ROWS, THEN
/// <c>tenant_events</c>. A publish updates the site row before it inserts the version (SQLite inserts
/// first and updates after; the outcome is the same, the transaction refusing a missing site either
/// way), a deletion deletes the site row before its versions and data, and a team's deletion takes its
/// sites' rows (a key range) before anything else. No write locks a child row and then its parent.
/// </para>
/// </summary>
public sealed class SqlServerSiteStore(string connectionString) : ISiteStore
{
    private static readonly string SiteMatch = $"{SqlServerSql.Same("team", "@team")} AND {SqlServerSql.Same("name", "@name")}";

    private static readonly string ChildMatch = $"{SqlServerSql.Same("team", "@team")} AND {SqlServerSql.Same("site", "@site")}";

    private static readonly string DocumentMatch =
        $"{ChildMatch} AND {SqlServerSql.Same("collection", "@collection")} AND {SqlServerSql.Same("id", "@id")}";

    /// <summary>The site a data write belongs to, taken before anything is counted: see the remarks.</summary>
    private static readonly string SiteLock =
        $"SELECT COUNT(*) FROM sites WITH (UPDLOCK, HOLDLOCK) WHERE {SqlServerSql.Same("team", "@team")} AND {SqlServerSql.Same("name", "@site")}";

    private const string SelectSite = $"SELECT {SiteRows.SiteColumns} FROM sites";

    private const string SelectDocument = $"SELECT {SiteRows.DocumentColumns} FROM site_documents";

    private readonly string _connectionString = SqlServerOpen.ConnectionString(connectionString);

    public Task<IReadOnlyList<SiteRow>> ListAsync(string? team, CancellationToken ct = default) =>
        ReadAsync(
            team is null
                ? $"{SelectSite} ORDER BY {SqlServerSql.NoCaseOrder("team")}, name"
                : $"{SelectSite} WHERE {SqlServerSql.Same("team", "@team")} ORDER BY name",
            command => { if (team is not null) command.Parameters.AddWithValue("@team", team); },
            SiteRows.ReadSite,
            ct);

    public async Task<SiteRow?> FindAsync(string team, string name, CancellationToken ct = default) =>
        (await ReadAsync($"{SelectSite} WHERE {SiteMatch}", command => BindSite(command, team, name), SiteRows.ReadSite, ct))
            .SingleOrDefault();

    public Task<IReadOnlyList<SiteVersionRow>> VersionsAsync(string team, string name, CancellationToken ct = default) =>
        ReadAsync(
            $"SELECT {SiteRows.VersionColumns} FROM site_versions WHERE {ChildMatch} ORDER BY version DESC",
            command => Bind(command, team, name, null, null),
            SiteRows.ReadVersion,
            ct);

    /// <summary>
    /// The name is taken when a row holds it under the key's own comparison, which ignores a trailing
    /// space: the insert would fail on that key otherwise. A site name is a slug, which holds no space.
    /// </summary>
    public Task<bool> CreateAsync(SiteRow row, TriggerAudit audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

                await using (var command = Command(connection, transaction,
                    """
                    INSERT INTO sites (team, name, live_version, next_version, created_at, created_by)
                    SELECT @team, @name, NULL, 1, @at, @by
                    WHERE NOT EXISTS (SELECT 1 FROM sites WITH (UPDLOCK, HOLDLOCK) WHERE team = @team AND name = @name)
                    """))
                {
                    BindSite(command, row.Team, row.Name);
                    command.Parameters.AddWithValue("@at", Instant.Stamp(row.CreatedAt));
                    command.Parameters.AddWithValue("@by", row.CreatedBy);

                    if (await command.ExecuteNonQueryAsync(token) == 0) return false;
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await transaction.CommitAsync(token);
                return true;
            },
            ct);

    public Task RecordPublishAsync(
        string team, string name, SiteVersionRow version, IReadOnlyList<int> pruned, TriggerAudit audit,
        CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

                // The site row first (the lock order), taking the next number past this one: SQLite's
                // MAX(next_version, version + 1).
                await using (var command = Command(connection, transaction,
                    $"""
                     UPDATE sites
                        SET live_version = @version,
                            next_version = CASE WHEN next_version > @version + 1 THEN next_version ELSE @version + 1 END
                      WHERE {SiteMatch}
                     """))
                {
                    BindSite(command, team, name);
                    command.Parameters.AddWithValue("@version", version.Version);

                    if (await command.ExecuteNonQueryAsync(token) == 0)
                    {
                        throw new InvalidOperationException($"No site '{name}' in team '{team}' to publish.");
                    }
                }

                await using (var command = Command(connection, transaction,
                    """
                    INSERT INTO site_versions (team, site, version, published_at, published_by, source, files, bytes)
                    VALUES (@team, @site, @version, @at, @by, @source, @files, @bytes)
                    """))
                {
                    Bind(command, team, name, null, null);
                    command.Parameters.AddWithValue("@version", version.Version);
                    command.Parameters.AddWithValue("@at", Instant.Stamp(version.PublishedAt));
                    command.Parameters.AddWithValue("@by", version.PublishedBy);
                    command.Parameters.AddWithValue("@source", version.Source);
                    command.Parameters.AddWithValue("@files", version.Files);
                    command.Parameters.AddWithValue("@bytes", version.Bytes);
                    await command.ExecuteNonQueryAsync(token);
                }

                foreach (var old in pruned)
                {
                    await using var command = Command(connection, transaction,
                        $"DELETE FROM site_versions WHERE {ChildMatch} AND version = @version");
                    Bind(command, team, name, null, null);
                    command.Parameters.AddWithValue("@version", old);
                    await command.ExecuteNonQueryAsync(token);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await transaction.CommitAsync(token);
                return 0;
            },
            ct);

    public Task SetLiveAsync(string team, string name, int? version, TriggerAudit audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

                await using (var command = Command(connection, transaction, $"UPDATE sites SET live_version = @version WHERE {SiteMatch}"))
                {
                    BindSite(command, team, name);
                    command.Parameters.AddWithValue("@version", (object?)version ?? DBNull.Value);
                    await command.ExecuteNonQueryAsync(token);
                }

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await transaction.CommitAsync(token);
                return 0;
            },
            ct);

    public Task<bool> DeleteAsync(string team, string name, TriggerAudit audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

                if (await DeleteSiteRowsAsync(connection, transaction, team, name, token) == 0) return false;

                await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit, token);
                await transaction.CommitAsync(token);
                return true;
            },
            ct);

    public Task<IReadOnlyList<string>> DeleteTeamAsync(
        string team, Func<string, TriggerAudit> audit, CancellationToken ct = default) =>
        SqlServerOpen.RunAsync<IReadOnlyList<string>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

                // The team's sites, held as a key range until the end: a site created meanwhile waits,
                // rather than landing between the list and the sweep below and losing its data to it.
                var names = new List<string>();
                await using (var command = Command(connection, transaction,
                    $"SELECT name FROM sites WITH (UPDLOCK, HOLDLOCK) WHERE {SqlServerSql.Same("team", "@team")} ORDER BY name"))
                {
                    command.Parameters.AddWithValue("@team", team);
                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) names.Add(reader.GetString(0));
                }

                foreach (var name in names)
                {
                    await DeleteSiteRowsAsync(connection, transaction, team, name, token);
                    await SqlServerTenantAuditRow.AppendAsync(connection, transaction, audit(name), token);
                }

                // Data or versions left by a site row that is already gone: never listed, never counted.
                foreach (var table in new[] { "site_versions", "site_documents" })
                {
                    await using var command = Command(connection, transaction,
                        $"DELETE FROM {table} WHERE {SqlServerSql.Same("team", "@team")}");
                    command.Parameters.AddWithValue("@team", team);
                    await command.ExecuteNonQueryAsync(token);
                }

                await transaction.CommitAsync(token);
                return names;
            },
            ct);

    /// <summary>The site row first, then its versions and data (the lock order); 0 when there was no
    /// site row, which leaves the rest to the caller's rollback, as SQLite's does.</summary>
    private static async Task<int> DeleteSiteRowsAsync(
        SqlConnection connection, SqlTransaction transaction, string team, string name, CancellationToken ct)
    {
        int deleted;

        await using (var site = Command(connection, transaction, $"DELETE FROM sites WHERE {SiteMatch}"))
        {
            BindSite(site, team, name);
            deleted = await site.ExecuteNonQueryAsync(ct);
        }

        if (deleted == 0) return 0;

        foreach (var table in new[] { "site_versions", "site_documents" })
        {
            await using var command = Command(connection, transaction, $"DELETE FROM {table} WHERE {ChildMatch}");
            Bind(command, team, name, null, null);
            await command.ExecuteNonQueryAsync(ct);
        }

        return deleted;
    }

    public Task<IReadOnlyList<SiteDocument>> ListDocumentsAsync(
        string team, string site, string collection, CancellationToken ct = default) =>
        ReadAsync(
            $"{SelectDocument} WHERE {ChildMatch} AND {SqlServerSql.Same("collection", "@collection")} ORDER BY id",
            command => Bind(command, team, site, collection, null),
            SiteRows.ReadDocument,
            ct);

    public Task<IReadOnlyList<string>> CollectionsAsync(string team, string site, CancellationToken ct = default) =>
        ReadAsync(
            $"SELECT DISTINCT collection FROM site_documents WHERE {ChildMatch} ORDER BY collection",
            command => Bind(command, team, site, null, null),
            reader => reader.GetString(0),
            ct);

    public async Task<SiteDocument?> GetDocumentAsync(
        string team, string site, string collection, string id, CancellationToken ct = default) =>
        (await ReadAsync($"{SelectDocument} WHERE {DocumentMatch}", command => Bind(command, team, site, collection, id), SiteRows.ReadDocument, ct))
            .SingleOrDefault();

    public async Task<SiteWriteOutcome> PutDocumentAsync(
        string team, string site, string collection, string id, string json, string by, DateTimeOffset at,
        CancellationToken ct = default)
    {
        var bytes = SiteRows.Bytes(json);

        if (SiteRows.TooLarge(bytes) is { } tooLarge) return tooLarge;

        return await SqlServerOpen.RunAsync(
            _connectionString,
            async (connection, token) =>
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

                await using (var hold = Command(connection, transaction, SiteLock))
                {
                    Bind(hold, team, site, null, null);
                    await hold.ExecuteScalarAsync(token);
                }

                long? existing = null;

                await using (var command = Command(connection, transaction, $"SELECT bytes FROM site_documents WHERE {DocumentMatch}"))
                {
                    Bind(command, team, site, collection, id);
                    if (await command.ExecuteScalarAsync(token) is long found) existing = found;
                }

                if (existing is null)
                {
                    await using var count = Command(connection, transaction,
                        $"SELECT COUNT_BIG(*) FROM site_documents WHERE {ChildMatch} AND {SqlServerSql.Same("collection", "@collection")}");
                    Bind(count, team, site, collection, null);

                    if (SiteRows.CollectionFull(
                            collection, Convert.ToInt64(await count.ExecuteScalarAsync(token), CultureInfo.InvariantCulture)) is { } full)
                    {
                        return full;
                    }
                }

                await using (var total = Command(connection, transaction,
                    $"SELECT COALESCE(SUM(bytes), 0) FROM site_documents WHERE {ChildMatch}"))
                {
                    Bind(total, team, site, null, null);
                    var used = Convert.ToInt64(await total.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);

                    if (SiteRows.SiteFull(site, used, existing, bytes) is { } full) return full;
                }

                // On the key's own comparison, which the insert would otherwise fail on: a collection
                // and a document id are checked before any write, and hold no space.
                await using (var write = Command(connection, transaction,
                    """
                     MERGE site_documents WITH (HOLDLOCK) AS d
                     USING (SELECT @team AS team, @site AS site, @collection AS collection, @id AS id) AS n
                        ON d.team = n.team AND d.site = n.site AND d.collection = n.collection AND d.id = n.id
                     WHEN MATCHED THEN UPDATE SET doc = @doc, bytes = @bytes, updated_at = @at, updated_by = @by
                     WHEN NOT MATCHED THEN INSERT (team, site, collection, id, doc, bytes, updated_at, updated_by)
                         VALUES (@team, @site, @collection, @id, @doc, @bytes, @at, @by);
                     """))
                {
                    Bind(write, team, site, collection, id);
                    write.Parameters.AddWithValue("@doc", json);
                    write.Parameters.AddWithValue("@bytes", bytes);
                    write.Parameters.AddWithValue("@at", Instant.Stamp(at));
                    write.Parameters.AddWithValue("@by", by);
                    await write.ExecuteNonQueryAsync(token);
                }

                await transaction.CommitAsync(token);
                return SiteWriteOutcome.Ok;
            },
            ct);
    }

    public async Task<bool> DeleteDocumentAsync(
        string team, string site, string collection, string id, CancellationToken ct = default) =>
        await SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"DELETE FROM site_documents WHERE {DocumentMatch}",
            command => Bind(command, team, site, collection, id),
            ct) > 0;

    public async Task<SiteDataUsage> UsageAsync(string team, string site, CancellationToken ct = default) =>
        (await ReadAsync(
            $"SELECT COUNT_BIG(*), COALESCE(SUM(bytes), 0) FROM site_documents WHERE {ChildMatch}",
            command => Bind(command, team, site, null, null),
            SiteRows.ReadUsage,
            ct)).Single();

    private Task<IReadOnlyList<T>> ReadAsync<T>(
        string sql, Action<SqlCommand> bind, Func<SqlDataReader, T> read, CancellationToken ct) =>
        SqlServerOpen.RunAsync<IReadOnlyList<T>>(
            _connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind(command);

                var rows = new List<T>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(read(reader));

                return rows;
            },
            ct);

    private static void BindSite(SqlCommand command, string team, string name)
    {
        command.Parameters.AddWithValue("@team", team);
        command.Parameters.AddWithValue("@name", name);
    }

    private static void Bind(SqlCommand command, string team, string site, string? collection, string? id)
    {
        command.Parameters.AddWithValue("@team", team);
        command.Parameters.AddWithValue("@site", site);
        if (collection is not null) command.Parameters.AddWithValue("@collection", collection);
        if (id is not null) command.Parameters.AddWithValue("@id", id);
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
