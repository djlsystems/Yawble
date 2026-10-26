using System.Globalization;
using Harness.Backlog;
using Harness.Contracts;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Backlog;

/// <summary>
/// <see cref="IBacklogStore"/> on SQL Server: SQLite's <c>SqliteBacklogStore</c> write for write. A row's
/// columns, its reading, the stored key and the team filter are <see cref="BacklogRows"/>', shared with
/// it. A create, an edit, an archive, a delete and a dispatch that carry a tenant row commit with it or
/// not at all, through the writer the store is made with (<see cref="SqlServerTenantAuditRow"/> in the
/// Host); one asked for without a writer is refused, as on SQLite.
///
/// <para>
/// THE KEYS ARE THE CITATION AND NUMBERS. An item's key is minted from a number by
/// <see cref="BacklogRows.Key"/>, a dispatch's is its IDENTITY and a correlation is a number, so none
/// can carry a trailing space and each compares with a plain <c>=</c>. A REPOSITORY NAME CAN: it is the
/// last segment of a URL a person gave. So a repository's rows are found by its hashed key and decided
/// by its text (<see cref="RepoMatch"/>), which folds case as SQLite's <c>NOCASE</c> key does and keeps a
/// trailing space as SQLite does. Ordered by name, the names sort as SQLite's NOCASE sorts them
/// (<see cref="SqlServerSql.NoCaseOrder"/>).
/// </para>
///
/// <para>
/// EVERY READ-THEN-WRITE HOLDS WHAT IT READ. SQLite runs one writer at a time; here two at once would
/// otherwise each act on what the other is about to change:
/// </para>
/// <list type="bullet">
///   <item>A create reads the highest position under <c>UPDLOCK, HOLDLOCK</c>, so two creates cannot
///     both append at one position: the second waits for the first's row. The id comes from the
///     counter's one-row <c>UPDATE ... OUTPUT</c>, which hands each create its own.</item>
///   <item>A renumber is one <c>UPDATE</c> over every row in position order, so no move lands between
///     its read and its writes.</item>
///   <item>A delete holds the item and its dispatches before it removes their tips, starts and missed
///     starts, so no dispatch is added to it half way.</item>
///   <item>A tip, a start and a missed start are each one <c>MERGE</c> under <c>HOLDLOCK</c>, so two
///     writers of one dispatch and repository make one row, not a key violation.</item>
///   <item>Every other write is one statement, its condition (<c>landed_at IS NULL</c>,
///     <c>stopped_at IS NULL</c>) read under the statement's own update lock.</item>
/// </list>
///
/// <para>
/// LOCK ORDER: <c>backlog_items</c>, THEN <c>backlog_id_sequence</c> (a create), THEN
/// <c>backlog_dispatches</c>, THEN a dispatch's tips, starts and missed starts, THEN <c>tenant_events</c>,
/// whose rows are inserts that read nothing. No write here takes an outcome or a team row, so it cannot
/// meet an outcome link (which takes its link rows, then backlog items) or a team write (teams, then
/// their rows, then <c>tenant_events</c>) in the other order. None takes the message append lock.
/// </para>
/// </summary>
public sealed class SqlServerBacklogStore : IBacklogStore
{
    private readonly string _connectionString;
    private readonly Func<SqlConnection, SqlTransaction, TriggerAudit, CancellationToken, Task>? _audit;

    private const string Select = $"SELECT {BacklogRows.ItemColumns} FROM backlog_items";

    private const string SelectDispatch = $"SELECT {BacklogRows.DispatchColumns} FROM backlog_dispatches";

    /// <summary>
    /// ONE REPOSITORY'S ROWS, of the dispatch <c>@dispatch</c> and the repository <c>@repo</c>, on
    /// <paramref name="alias"/>: the hashed key finds them and the text, compared as SQLite's NOCASE
    /// compares it with a trailing space deciding, keeps only the one SQLite would.
    /// </summary>
    private static string RepoMatch(string alias) =>
        $"{alias}.dispatch = @dispatch AND {alias}.repo_key = {RepoKeyOf("@repo")} AND {SqlServerSql.Same(alias + ".repo", "@repo")}";

    /// <summary>
    /// SQLITE'S ORDER OF A NOCASE NAME, with the tie SQL Server's padding makes broken as SQLite breaks it:
    /// names differing only by trailing spaces compare equal here, and SQLite puts the shorter first.
    /// </summary>
    private static string RepoOrder(string column) => $"{SqlServerSql.NoCaseOrder(column)}, DATALENGTH({column})";

    /// <summary>The key <paramref name="value"/> is stored under: <see cref="SqlServerBacklogSchema.RepoKey"/> of it.</summary>
    private static string RepoKeyOf(string value) =>
        $"CAST(HASHBYTES('SHA2_256', LOWER({value} COLLATE {SqlServerSql.NoCase})) AS BINARY(32))";

    /// <param name="audit">Appends one tenant row inside a transaction of this store's; the tenant log is
    /// in the same database. Without it, a write that carries a row is refused.</param>
    public SqlServerBacklogStore(
        string connectionString,
        Func<SqlConnection, SqlTransaction, TriggerAudit, CancellationToken, Task>? audit = null)
    {
        _connectionString = SqlServerOpen.ConnectionString(connectionString);
        _audit = audit;
    }

    public Task<IReadOnlyList<BacklogItem>> ListAsync(
        IReadOnlySet<string>? teams,
        bool includeUnlinked,
        bool archived,
        CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<BacklogItem>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();

                // THE ARCHIVE IS A SECOND AXIS, and this alone decides which list an item is in.
                command.CommandText = archived
                    ? $"{Select} WHERE archived_at IS NOT NULL ORDER BY position, id"
                    : $"{Select} WHERE archived_at IS NULL ORDER BY position, id";

                var rows = new List<BacklogItem>();

                await using (var reader = await command.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token)) rows.Add(BacklogRows.Read(reader));
                }

                return BacklogRows.Visible(rows, teams, includeUnlinked);
            },
            ct);

    public async Task<IReadOnlyList<BacklogItem>> ListArchivedAsync(
        IReadOnlySet<string>? teams,
        bool includeUnlinked,
        long? before,
        int take,
        CancellationToken ct = default)
    {
        if (take <= 0) return [];

        return BacklogRows.ArchivedPage(await ListAsync(teams, includeUnlinked, archived: true, ct), before, take);
    }

    public Task<BacklogItem?> GetAsync(long id, CancellationToken ct = default) =>
        RunAsync<BacklogItem?>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"{Select} WHERE id = @id";
                command.Parameters.AddWithValue("@id", BacklogRows.Key(id));

                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token) ? BacklogRows.Read(reader) : null;
            },
            ct);

    /// <summary>
    /// ONE STATEMENT, whatever the count: the keys travel as one JSON array parameter and
    /// <c>OPENJSON</c> expands it, as <c>json_each</c> does on SQLite, so there is no per-id parameter
    /// to run into the 2,100-parameter limit.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, string>> TitlesAsync(
        IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        var keys = ids.Distinct().Select(BacklogRows.Key).ToList();
        if (keys.Count == 0) return new Dictionary<long, string>();

        return await RunAsync<IReadOnlyDictionary<long, string>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, title FROM backlog_items WHERE id IN (SELECT value FROM OPENJSON(@keys))";
                command.Parameters.AddWithValue("@keys", System.Text.Json.JsonSerializer.Serialize(keys));

                var found = new Dictionary<long, string>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) found[BacklogRows.Id(reader.GetString(0))] = reader.GetString(1);

                return found;
            },
            ct);
    }

    private void RequireWriterFor(TriggerAudit? audit)
    {
        if (audit is not null && _audit is null) throw BacklogRows.NoWriter();
    }

    public Task<BacklogItem> CreateAsync(
        string? team, string title, string body, string createdBy, CancellationToken ct = default, TriggerAudit? audit = null)
    {
        RequireWriterFor(audit);

        return RunAsync(
            async (connection, token) =>
            {
                var now = Instant.Now();
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                double position;

                // HELD UNTIL THE COMMIT: a second create waits here and appends after this one's row.
                await using (var last = Command(connection, transaction, "SELECT MAX(position) FROM backlog_items WITH (UPDLOCK, HOLDLOCK)"))
                {
                    var max = await last.ExecuteScalarAsync(token);
                    position = max is null or DBNull
                        ? BacklogRows.AppendGap
                        : Convert.ToDouble(max, CultureInfo.InvariantCulture) + BacklogRows.AppendGap;
                }

                long id;

                // MINTED FROM THE COUNTER, IN THIS TRANSACTION, never from MAX(id): a deleted item's id
                // must not be handed to the next one.
                await using (var mint = Command(connection, transaction, "UPDATE backlog_id_sequence SET last = last + 1 OUTPUT inserted.last WHERE id = 1"))
                {
                    id = Convert.ToInt64(await mint.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                }

                await using (var insert = Command(
                    connection, transaction,
                    """
                    INSERT INTO backlog_items
                        (id, team, title, body, state, position, archived_at, created_at, updated_at, created_by)
                    VALUES (@id, @team, @title, @body, @state, @position, NULL, @now, @now, @by);
                    """))
                {
                    insert.Parameters.AddWithValue("@id", BacklogRows.Key(id));
                    insert.Parameters.AddWithValue("@team", (object?)team ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@title", title);
                    insert.Parameters.AddWithValue("@body", body);
                    insert.Parameters.AddWithValue("@state", BacklogStates.Pending);
                    insert.Parameters.AddWithValue("@position", position);
                    insert.Parameters.AddWithValue("@now", now);
                    insert.Parameters.AddWithValue("@by", createdBy);
                    await insert.ExecuteNonQueryAsync(token);
                }

                if (audit is not null) await _audit!(connection, transaction, BacklogRows.Naming(audit, id), token);
                await SqlServerOpen.CommitAsync(transaction, token);

                return new BacklogItem(id, team, title, body, BacklogStates.Pending, position, null, now, now, createdBy);
            },
            ct);
    }

    public Task UpdateAsync(
        long id,
        string? title = null,
        string? body = null,
        string? state = null,
        CancellationToken ct = default)
    {
        var sets = BacklogRows.Sets('@', title, body, state, setOutcome: false);
        if (sets.Count == 0) return Task.CompletedTask;

        sets.Add("updated_at = @now");

        return SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"UPDATE backlog_items SET {string.Join(", ", sets)} WHERE id = @id",
            command => BindEdit(command, id, title, body, state, setOutcome: false, outcomeId: null),
            ct);
    }

    public Task EditAsync(
        long id,
        string? title,
        string? body,
        string? state,
        bool setOutcome,
        string? outcomeId,
        IReadOnlyList<TriggerAudit> audit,
        CancellationToken ct = default)
    {
        if (audit.Count > 0) RequireWriterFor(audit[0]);

        var sets = BacklogRows.Sets('@', title, body, state, setOutcome);

        return RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                if (sets.Count > 0)
                {
                    await using var command = Command(
                        connection, transaction,
                        $"UPDATE backlog_items SET {string.Join(", ", sets)}, updated_at = @now WHERE id = @id");
                    BindEdit(command, id, title, body, state, setOutcome, outcomeId);
                    await command.ExecuteNonQueryAsync(token);
                }

                foreach (var row in audit) await _audit!(connection, transaction, row, token);

                await SqlServerOpen.CommitAsync(transaction, token);
                return 0;
            },
            ct);
    }

    private static void BindEdit(
        SqlCommand command, long id, string? title, string? body, string? state, bool setOutcome, string? outcomeId)
    {
        command.Parameters.AddWithValue("@id", BacklogRows.Key(id));
        command.Parameters.AddWithValue("@now", Instant.Now());

        if (title is not null) command.Parameters.AddWithValue("@title", title);
        if (body is not null) command.Parameters.AddWithValue("@body", body);
        if (state is not null) command.Parameters.AddWithValue("@state", state);
        if (setOutcome) command.Parameters.AddWithValue("@outcome", (object?)outcomeId ?? DBNull.Value);
    }

    /// <summary>ONE ROW, and <c>updated_at</c> deliberately does not move: a reorder is not an edit.</summary>
    public Task SetPositionAsync(long id, double position, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            "UPDATE backlog_items SET position = @position WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@id", BacklogRows.Key(id));
                command.Parameters.AddWithValue("@position", position);
            },
            ct);

    /// <summary>
    /// EVERY ITEM, archived included, renumbered by one statement in the order position then id gives
    /// them, and read back in that order inside the same transaction.
    /// </summary>
    public Task<IReadOnlyList<BacklogItem>> RenumberAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<BacklogItem>>(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var write = Command(
                    connection, transaction,
                    """
                    WITH ordered AS (
                        SELECT position, ROW_NUMBER() OVER (ORDER BY position, id) AS place FROM backlog_items)
                    UPDATE ordered SET position = place * @gap
                    """))
                {
                    write.Parameters.AddWithValue("@gap", BacklogRows.AppendGap);
                    await write.ExecuteNonQueryAsync(token);
                }

                var all = new List<BacklogItem>();

                await using (var read = Command(connection, transaction, $"{Select} ORDER BY position, id"))
                await using (var reader = await read.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token)) all.Add(BacklogRows.Read(reader));
                }

                await SqlServerOpen.CommitAsync(transaction, token);
                return all;
            },
            ct);

    public Task SetArchivedAsync(long id, string? archivedAt, CancellationToken ct = default, TriggerAudit? audit = null)
    {
        RequireWriterFor(audit);

        return RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                await using (var command = Command(
                    connection, transaction,
                    "UPDATE backlog_items SET archived_at = @archived, updated_at = @now WHERE id = @id"))
                {
                    command.Parameters.AddWithValue("@id", BacklogRows.Key(id));
                    command.Parameters.AddWithValue("@archived", (object?)archivedAt ?? DBNull.Value);
                    command.Parameters.AddWithValue("@now", Instant.Now());
                    await command.ExecuteNonQueryAsync(token);
                }

                if (audit is not null) await _audit!(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return 0;
            },
            ct);
    }

    /// <summary>
    /// THE DISPATCH ROWS GO WITH IT, BY HAND RATHER THAN BY CASCADE, as on SQLite. The item and its
    /// dispatches are held first, in the store's lock order, so the children removed are all it has.
    /// </summary>
    public Task DeleteAsync(long id, CancellationToken ct = default, TriggerAudit? audit = null)
    {
        RequireWriterFor(audit);

        return RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                foreach (var sql in new[]
                {
                    "SELECT COUNT(*) FROM backlog_items WITH (UPDLOCK, HOLDLOCK) WHERE id = @id",
                    "SELECT COUNT(*) FROM backlog_dispatches WITH (UPDLOCK, HOLDLOCK) WHERE item = @id",
                    "DELETE FROM backlog_dispatch_tips WHERE dispatch IN (SELECT id FROM backlog_dispatches WHERE item = @id)",
                    "DELETE FROM backlog_dispatch_bases WHERE dispatch IN (SELECT id FROM backlog_dispatches WHERE item = @id)",
                    "DELETE FROM backlog_dispatch_missed_starts WHERE dispatch IN (SELECT id FROM backlog_dispatches WHERE item = @id)",
                    "DELETE FROM backlog_dispatches WHERE item = @id",
                    "DELETE FROM backlog_items WHERE id = @id",
                })
                {
                    await using var command = Command(connection, transaction, sql);
                    command.Parameters.AddWithValue("@id", BacklogRows.Key(id));
                    await command.ExecuteNonQueryAsync(token);
                }

                if (audit is not null) await _audit!(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return 0;
            },
            ct);
    }

    public Task<IReadOnlyList<BacklogDispatch>> DispatchesAsync(long item, CancellationToken ct = default) =>
        ReadDispatchesAsync(
            $"{SelectDispatch} WHERE item = @item ORDER BY id",
            command => command.Parameters.AddWithValue("@item", BacklogRows.Key(item)),
            ct);

    public Task<BacklogDispatch> AddDispatchAsync(
        long item,
        string teamId,
        string teamName,
        long correlation,
        string dispatchedBy,
        CancellationToken ct = default,
        TriggerAudit? audit = null)
    {
        RequireWriterFor(audit);

        return RunAsync(
            async (connection, token) =>
            {
                var now = Instant.Now();
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                long id;

                await using (var command = Command(
                    connection, transaction,
                    """
                    INSERT INTO backlog_dispatches
                        (item, team_id, team_name, correlation, dispatched_at, dispatched_by, frozen_at, frozen_stats)
                    OUTPUT inserted.id
                    VALUES (@item, @teamId, @teamName, @correlation, @now, @by, NULL, NULL);
                    """))
                {
                    command.Parameters.AddWithValue("@item", BacklogRows.Key(item));
                    command.Parameters.AddWithValue("@teamId", teamId);
                    command.Parameters.AddWithValue("@teamName", teamName);
                    command.Parameters.AddWithValue("@correlation", correlation);
                    command.Parameters.AddWithValue("@now", now);
                    command.Parameters.AddWithValue("@by", dispatchedBy);
                    id = Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                }

                if (audit is not null) await _audit!(connection, transaction, audit, token);
                await SqlServerOpen.CommitAsync(transaction, token);

                return new BacklogDispatch(id, item, teamId, teamName, correlation, now, dispatchedBy, null, null);
            },
            ct);
    }

    public Task FreezeDispatchAsync(
        long dispatchId, string? frozenAt, string? frozenStats, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            "UPDATE backlog_dispatches SET frozen_at = @at, frozen_stats = @stats WHERE id = @id",
            command =>
            {
                command.Parameters.AddWithValue("@id", dispatchId);
                command.Parameters.AddWithValue("@at", (object?)frozenAt ?? DBNull.Value);
                command.Parameters.AddWithValue("@stats", (object?)frozenStats ?? DBNull.Value);
            },
            ct);

    /// <summary>The newest dispatch of every item: the "the current one is the last" rule, as SQLite.</summary>
    public Task<IReadOnlyList<BacklogDispatch>> LatestDispatchesAsync(CancellationToken ct = default) =>
        ReadDispatchesAsync(
            $"{SelectDispatch} WHERE id IN (SELECT MAX(id) FROM backlog_dispatches GROUP BY item) ORDER BY item",
            _ => { },
            ct);

    public Task<IReadOnlyList<BacklogCurrentDispatch>> CurrentDispatchesAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<BacklogCurrentDispatch>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {BacklogRows.DispatchColumnsOf("d")}, i.title, i.state, i.archived_at"
                    + " FROM backlog_dispatches d JOIN backlog_items i ON i.id = d.item"
                    + " WHERE d.id IN (SELECT MAX(id) FROM backlog_dispatches GROUP BY item)"
                    + " ORDER BY d.item";

                var rows = new List<BacklogCurrentDispatch>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(BacklogRows.ReadCurrent(reader));

                return rows;
            },
            ct);

    public async Task<BacklogDispatch?> DispatchForCorrelationAsync(long correlation, CancellationToken ct = default) =>
        (await ReadDispatchesAsync(
            $"SELECT TOP (1) {BacklogRows.DispatchColumns} FROM backlog_dispatches WHERE correlation = @correlation ORDER BY id",
            command => command.Parameters.AddWithValue("@correlation", correlation),
            ct)).SingleOrDefault();

    /// <summary>REPLACED, NOT APPENDED: the tip is where the work stands now.</summary>
    public Task RecordTipAsync(long dispatchId, string repo, string sha, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"""
             MERGE backlog_dispatch_tips WITH (HOLDLOCK) AS t
             USING (SELECT 1 AS one) AS n ON {RepoMatch("t")}
             WHEN MATCHED THEN UPDATE SET sha = @sha, recorded_at = @now
             WHEN NOT MATCHED THEN INSERT (dispatch, repo, sha, recorded_at) VALUES (@dispatch, @repo, @sha, @now);
             """,
            command =>
            {
                BindRepo(command, dispatchId, repo);
                command.Parameters.AddWithValue("@sha", sha);
                command.Parameters.AddWithValue("@now", Instant.Now());
            },
            ct);

    public Task<IReadOnlyList<BacklogDispatchTip>> TipsAsync(long dispatchId, CancellationToken ct = default) =>
        ReadAsync(
            $"SELECT dispatch, repo, sha, recorded_at FROM backlog_dispatch_tips WHERE dispatch = @dispatch ORDER BY {RepoOrder("repo")}",
            dispatchId, BacklogRows.ReadTip, ct);

    /// <summary>KEPT, NOT REPLACED: where the dispatch started is one moment.</summary>
    public Task RecordBaseAsync(
        long dispatchId, string repo, string defaultSha, string? teamSha, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"""
             MERGE backlog_dispatch_bases WITH (HOLDLOCK) AS b
             USING (SELECT 1 AS one) AS n ON {RepoMatch("b")}
             WHEN NOT MATCHED THEN INSERT (dispatch, repo, default_sha, team_sha, recorded_at)
                 VALUES (@dispatch, @repo, @default, @team, @now);
             """,
            command =>
            {
                BindRepo(command, dispatchId, repo);
                command.Parameters.AddWithValue("@default", defaultSha);
                command.Parameters.AddWithValue("@team", (object?)teamSha ?? DBNull.Value);
                command.Parameters.AddWithValue("@now", Instant.Now());
            },
            ct);

    public Task<IReadOnlyList<BacklogDispatchBase>> BasesAsync(long dispatchId, CancellationToken ct = default) =>
        ReadAsync(
            "SELECT dispatch, repo, default_sha, team_sha, recorded_at FROM backlog_dispatch_bases"
            + $" WHERE dispatch = @dispatch ORDER BY {RepoOrder("repo")}",
            dispatchId, BacklogRows.ReadBase, ct);

    /// <summary>
    /// THE SHAS ARE THE DISPATCH'S MOMENT AND ARE KEPT; only the reason moves, and a stopped retry
    /// stays stopped with the reason it stopped for.
    /// </summary>
    public Task RecordMissedStartAsync(
        long dispatchId, string repo, string reason, string? teamSha, string? defaultSha, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"""
             MERGE backlog_dispatch_missed_starts WITH (HOLDLOCK) AS m
             USING (SELECT 1 AS one) AS n ON {RepoMatch("m")}
             WHEN MATCHED AND m.stopped_at IS NULL THEN UPDATE SET reason = @reason
             WHEN NOT MATCHED THEN INSERT (dispatch, repo, reason, team_sha, default_sha, stopped_at, recorded_at)
                 VALUES (@dispatch, @repo, @reason, @team, @default, NULL, @now);
             """,
            command =>
            {
                BindRepo(command, dispatchId, repo);
                command.Parameters.AddWithValue("@reason", reason);
                command.Parameters.AddWithValue("@team", (object?)teamSha ?? DBNull.Value);
                command.Parameters.AddWithValue("@default", (object?)defaultSha ?? DBNull.Value);
                command.Parameters.AddWithValue("@now", Instant.Now());
            },
            ct);

    public Task StopStartRetryAsync(long dispatchId, string repo, string reason, CancellationToken ct = default) =>
        SqlServerOpen.ExecuteAsync(
            _connectionString,
            $"UPDATE m SET stopped_at = @now, reason = @reason FROM backlog_dispatch_missed_starts m WHERE {RepoMatch("m")} AND m.stopped_at IS NULL",
            command =>
            {
                BindRepo(command, dispatchId, repo);
                command.Parameters.AddWithValue("@reason", reason);
                command.Parameters.AddWithValue("@now", Instant.Now());
            },
            ct);

    public Task<IReadOnlyList<BacklogDispatchMissedStart>> MissedStartsAsync(
        long dispatchId, CancellationToken ct = default) =>
        ReadAsync(
            "SELECT dispatch, repo, reason, team_sha, default_sha, stopped_at, recorded_at"
            + $" FROM backlog_dispatch_missed_starts WHERE dispatch = @dispatch ORDER BY {RepoOrder("repo")}",
            dispatchId, BacklogRows.ReadMissed, ct);

    public Task<IReadOnlyList<BacklogDispatchMissedStart>> UnrecordedStartsAsync(CancellationToken ct = default) =>
        ReadAsync(
            $"""
             SELECT m.dispatch, m.repo, m.reason, m.team_sha, m.default_sha, m.stopped_at, m.recorded_at
             FROM backlog_dispatch_missed_starts m
             WHERE NOT EXISTS (
                 SELECT 1 FROM backlog_dispatch_bases b
                 WHERE b.dispatch = m.dispatch AND b.repo_key = m.repo_key AND {SqlServerSql.Same("b.repo", "m.repo")})
             ORDER BY m.dispatch, {RepoOrder("m.repo")}
             """,
            null, BacklogRows.ReadMissed, ct);

    public Task<IReadOnlySet<long>> DispatchesWithStartsAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlySet<long>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT DISTINCT dispatch FROM backlog_dispatch_bases";

                var ids = new HashSet<long>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) ids.Add(reader.GetInt64(0));

                return ids;
            },
            ct);

    /// <summary>`landed_at IS NULL` IS THE WHOLE RULE: a proven landed is never downgraded, cleared or moved.</summary>
    public async Task<bool> RecordLandedAsync(
        long dispatchId, string sha, string branch, CancellationToken ct = default) =>
        await SqlServerOpen.ExecuteAsync(
            _connectionString,
            "UPDATE backlog_dispatches SET landed_at = @now, landed_sha = @sha, landed_branch = @branch"
            + " WHERE id = @id AND landed_at IS NULL",
            command =>
            {
                command.Parameters.AddWithValue("@id", dispatchId);
                command.Parameters.AddWithValue("@sha", sha);
                command.Parameters.AddWithValue("@branch", branch);
                command.Parameters.AddWithValue("@now", Instant.Now());
            },
            ct) == 1;

    private static void BindRepo(SqlCommand command, long dispatchId, string repo)
    {
        command.Parameters.AddWithValue("@dispatch", dispatchId);
        command.Parameters.AddWithValue("@repo", repo);
    }

    private Task<IReadOnlyList<BacklogDispatch>> ReadDispatchesAsync(
        string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        RunAsync<IReadOnlyList<BacklogDispatch>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind(command);

                var rows = new List<BacklogDispatch>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(BacklogRows.ReadDispatch(reader));

                return rows;
            },
            ct);

    private Task<IReadOnlyList<T>> ReadAsync<T>(
        string sql, long? dispatchId, Func<SqlDataReader, T> read, CancellationToken ct) =>
        RunAsync<IReadOnlyList<T>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                if (dispatchId is { } id) command.Parameters.AddWithValue("@dispatch", id);

                var rows = new List<T>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(read(reader));

                return rows;
            },
            ct);

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    private Task RunAsync(Func<SqlConnection, CancellationToken, Task<int>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);
}
