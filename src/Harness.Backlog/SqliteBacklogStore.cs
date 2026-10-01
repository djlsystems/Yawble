using System.Globalization;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Backlog;

/// <summary>
/// The backlog, in SQLite.
///
/// <para>
/// THE PRAGMAS ARE APPLIED ON EVERY CONNECTION, not once. <c>foreign_keys</c> and
/// <c>busy_timeout</c> are per-CONNECTION and <c>Pooling = false</c> makes every
/// <see cref="Open"/> a new one - so a store that set them once would have them off everywhere
/// afterwards. Neither of this module's tables carries a foreign key, deliberately (see
/// <see cref="BacklogSchema"/>), but the pragma stays because the next table added here might.
/// </para>
/// </summary>
public sealed class SqliteBacklogStore : IBacklogStore
{
    private readonly string _connectionString;
    private readonly SqliteDurability _durability;

    /// <summary>
    /// The gap a fresh item is appended with. Integers by default, so the common case - append,
    /// append, append - never needs a midpoint at all and the positions stay readable in a dump.
    /// </summary>
    private const double AppendGap = 1024d;

    public SqliteBacklogStore(
        string databasePath, SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        _durability = durability;
    }

    public async Task<IReadOnlyList<BacklogItem>> ListAsync(
        IReadOnlySet<string>? teams,
        bool includeUnlinked,
        bool archived,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // THE ARCHIVE IS A SECOND AXIS AND THIS IS THE ONLY THING THAT DECIDES WHICH LIST AN ITEM IS
        // IN - not its state. An `implemented` item stays in the backlog until somebody archives it,
        // which is what makes the two axes visible rather than theoretical.
        command.CommandText = archived
            ? "SELECT id, team, title, body, state, position, archived_at, created_at, updated_at, created_by"
              + " FROM backlog_items WHERE archived_at IS NOT NULL ORDER BY position, id"
            : "SELECT id, team, title, body, state, position, archived_at, created_at, updated_at, created_by"
              + " FROM backlog_items WHERE archived_at IS NULL ORDER BY position, id";

        var rows = new List<BacklogItem>();

        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        }

        // FILTERED HERE RATHER THAN IN SQL, and that is a choice worth stating: the caller's
        // effective teams are resolved per request and are a small set, while composing an IN clause
        // from them is the shape that invites a caller-supplied fragment. At this scale the read is
        // the cost and the filter is free.
        if (teams is null) return rows;

        return rows
            .Where(item => item.Team is null
                ? includeUnlinked
                : teams.Contains(item.Team))
            .ToList();
    }

    public async Task<IReadOnlyList<BacklogItem>> ListArchivedAsync(
        IReadOnlySet<string>? teams,
        bool includeUnlinked,
        long? before,
        int take,
        CancellationToken ct = default)
    {
        if (take <= 0) return [];

        var archived = await ListAsync(teams, includeUnlinked, archived: true, ct);

        // ORDERED ON THE NUMBER, IN MEMORY, never on the stored key in SQL. The key is a citation
        // (`B001F`) that sorts by name only while it is four characters wide; past that, and for
        // any row stored in another spelling, string order and id order part company, and a
        // cursor compared as text would skip or repeat rows at exactly that seam.
        return archived
            .Where(item => before is not { } cursor || item.Id < cursor)
            .OrderByDescending(item => item.Id)
            .Take(take)
            .ToList();
    }

    public async Task<BacklogItem?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT id, team, title, body, state, position, archived_at, created_at, updated_at, created_by"
            + " FROM backlog_items WHERE id = $id";
        command.Parameters.AddWithValue("$id", Key(id));

        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<BacklogItem> CreateAsync(
        string? team, string title, string body, string createdBy, CancellationToken ct = default)
    {
        var now = Now();

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        double position;

        await using (var last = connection.CreateCommand())
        {
            last.Transaction = transaction;
            last.CommandText = "SELECT MAX(position) FROM backlog_items";

            var max = await last.ExecuteScalarAsync(ct);
            position = max is null or DBNull
                ? AppendGap
                : Convert.ToDouble(max, CultureInfo.InvariantCulture) + AppendGap;
        }

        long id;

        // MINTED FROM THE COUNTER, IN THIS TRANSACTION, never from MAX(id): a deleted item's id
        // must not be handed to the next one. See BacklogSchema.
        await using (var mint = connection.CreateCommand())
        {
            mint.Transaction = transaction;
            mint.CommandText = "UPDATE backlog_id_sequence SET last = last + 1 WHERE id = 1 RETURNING last";

            id = Convert.ToInt64(await mint.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO backlog_items
                    (id, team, title, body, state, position, archived_at, created_at, updated_at, created_by)
                VALUES ($id, $team, $title, $body, $state, $position, NULL, $now, $now, $by);
                """;
            insert.Parameters.AddWithValue("$id", Key(id));
            insert.Parameters.AddWithValue("$team", (object?)team ?? DBNull.Value);
            insert.Parameters.AddWithValue("$title", title);
            insert.Parameters.AddWithValue("$body", body);
            insert.Parameters.AddWithValue("$state", BacklogStates.Pending);
            insert.Parameters.AddWithValue("$position", position);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$by", createdBy);

            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return new BacklogItem(id, team, title, body, BacklogStates.Pending, position, null, now, now, createdBy);
    }

    public async Task UpdateAsync(
        long id,
        string? title = null,
        string? body = null,
        string? state = null,
        CancellationToken ct = default)
    {
        // NULL LEAVES A FIELD ALONE, so the SET list is composed from what was actually passed. The
        // alternative - a whole-record save - is the change to refuse: it writes every other column
        // back as the caller read them, so renaming a title silently reverts a concurrent body edit.
        var sets = new List<string>();

        if (title is not null) sets.Add("title = $title");
        if (body is not null) sets.Add("body = $body");
        if (state is not null) sets.Add("state = $state");

        if (sets.Count == 0) return;

        sets.Add("updated_at = $now");

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = $"UPDATE backlog_items SET {string.Join(", ", sets)} WHERE id = $id";
        command.Parameters.AddWithValue("$id", Key(id));
        command.Parameters.AddWithValue("$now", Now());

        if (title is not null) command.Parameters.AddWithValue("$title", title);
        if (body is not null) command.Parameters.AddWithValue("$body", body);
        if (state is not null) command.Parameters.AddWithValue("$state", state);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetTeamAsync(long id, string? team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE backlog_items SET team = $team, updated_at = $now WHERE id = $id";
        command.Parameters.AddWithValue("$id", Key(id));
        command.Parameters.AddWithValue("$team", (object?)team ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetPositionAsync(long id, double position, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // ONE ROW. `updated_at` deliberately does NOT move: a reorder is not an edit of the item, and
        // a Modified column that jumped every time somebody dragged something would stop meaning
        // anything - which matters because it is a sortable column people read.
        command.CommandText = "UPDATE backlog_items SET position = $position WHERE id = $id";
        command.Parameters.AddWithValue("$id", Key(id));
        command.Parameters.AddWithValue("$position", position);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<BacklogItem>> RenumberAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var ids = new List<long>();

        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;

            // EVERY ITEM, archived included. Position is a property of the whole backlog, and an
            // archived item keeps its place so Restore can return it there rather than to an end.
            read.CommandText = "SELECT id FROM backlog_items ORDER BY position, id";

            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) ids.Add(Id(reader.GetString(0)));
        }

        for (var i = 0; i < ids.Count; i++)
        {
            await using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "UPDATE backlog_items SET position = $position WHERE id = $id";
            write.Parameters.AddWithValue("$id", Key(ids[i]));
            write.Parameters.AddWithValue("$position", (i + 1) * AppendGap);
            await write.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        var all = new List<BacklogItem>();

        foreach (var id in ids)
        {
            if (await GetAsync(id, ct) is { } item) all.Add(item);
        }

        return all;
    }

    public async Task SetArchivedAsync(long id, string? archivedAt, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "UPDATE backlog_items SET archived_at = $archived, updated_at = $now WHERE id = $id";
        command.Parameters.AddWithValue("$id", Key(id));
        command.Parameters.AddWithValue("$archived", (object?)archivedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        // THE DISPATCH ROWS GO WITH IT, BY HAND RATHER THAN BY CASCADE. `backlog_dispatches` has no
        // foreign key to `backlog_items` on purpose - a cascade there would take the execution
        // record with an item, and the archive exists to keep exactly that. Delete is the one
        // operation that really is meant to take it, so it says so itself.
        await using (var tips = connection.CreateCommand())
        {
            tips.Transaction = transaction;
            tips.CommandText =
                "DELETE FROM backlog_dispatch_tips WHERE dispatch IN (SELECT id FROM backlog_dispatches WHERE item = $id)";
            tips.Parameters.AddWithValue("$id", Key(id));
            await tips.ExecuteNonQueryAsync(ct);
        }

        await using (var bases = connection.CreateCommand())
        {
            bases.Transaction = transaction;
            bases.CommandText =
                "DELETE FROM backlog_dispatch_bases WHERE dispatch IN (SELECT id FROM backlog_dispatches WHERE item = $id)";
            bases.Parameters.AddWithValue("$id", Key(id));
            await bases.ExecuteNonQueryAsync(ct);
        }

        await using (var missed = connection.CreateCommand())
        {
            missed.Transaction = transaction;
            missed.CommandText =
                "DELETE FROM backlog_dispatch_missed_starts WHERE dispatch IN (SELECT id FROM backlog_dispatches WHERE item = $id)";
            missed.Parameters.AddWithValue("$id", Key(id));
            await missed.ExecuteNonQueryAsync(ct);
        }

        await using (var dispatches = connection.CreateCommand())
        {
            dispatches.Transaction = transaction;
            dispatches.CommandText = "DELETE FROM backlog_dispatches WHERE item = $id";
            dispatches.Parameters.AddWithValue("$id", Key(id));
            await dispatches.ExecuteNonQueryAsync(ct);
        }

        await using (var item = connection.CreateCommand())
        {
            item.Transaction = transaction;
            item.CommandText = "DELETE FROM backlog_items WHERE id = $id";
            item.Parameters.AddWithValue("$id", Key(id));
            await item.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<BacklogDispatch>> DispatchesAsync(
        long item, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT " + DispatchColumns
            + " FROM backlog_dispatches WHERE item = $item ORDER BY id";
        command.Parameters.AddWithValue("$item", Key(item));

        var rows = new List<BacklogDispatch>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadDispatch(reader));

        return rows;
    }

    public async Task<BacklogDispatch> AddDispatchAsync(
        long item,
        string teamId,
        string teamName,
        long correlation,
        string dispatchedBy,
        CancellationToken ct = default)
    {
        var now = Now();

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO backlog_dispatches
                (item, team_id, team_name, correlation, dispatched_at, dispatched_by, frozen_at, frozen_stats)
            VALUES ($item, $teamId, $teamName, $correlation, $now, $by, NULL, NULL);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$item", Key(item));
        command.Parameters.AddWithValue("$teamId", teamId);
        command.Parameters.AddWithValue("$teamName", teamName);
        command.Parameters.AddWithValue("$correlation", correlation);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", dispatchedBy);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

        return new BacklogDispatch(id, item, teamId, teamName, correlation, now, dispatchedBy, null, null);
    }

    public async Task FreezeDispatchAsync(
        long dispatchId, string? frozenAt, string? frozenStats, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "UPDATE backlog_dispatches SET frozen_at = $at, frozen_stats = $stats WHERE id = $id";
        command.Parameters.AddWithValue("$id", dispatchId);
        command.Parameters.AddWithValue("$at", (object?)frozenAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$stats", (object?)frozenStats ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// ONE QUERY FOR EVERY ITEM'S CURRENT DISPATCH. `MAX(id) GROUP BY item` is the same "the
    /// current one is the last" rule <see cref="DispatchesAsync"/> orders by, so the two can never
    /// name different records for one item. The table is small - one row per dispatch, ever - and
    /// the grouped subquery walks it once.
    /// </summary>
    public async Task<IReadOnlyList<BacklogDispatch>> LatestDispatchesAsync(
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT " + DispatchColumns
            + " FROM backlog_dispatches"
            + " WHERE id IN (SELECT MAX(id) FROM backlog_dispatches GROUP BY item)"
            + " ORDER BY item";

        var rows = new List<BacklogDispatch>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadDispatch(reader));

        return rows;
    }

    /// <summary>Every column <see cref="ReadDispatch"/> reads, in its order.</summary>
    private const string DispatchColumns =
        "id, item, team_id, team_name, correlation, dispatched_at, dispatched_by, frozen_at, frozen_stats,"
        + " landed_at, landed_sha, landed_branch";

    public async Task<BacklogDispatch?> DispatchForCorrelationAsync(
        long correlation, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT " + DispatchColumns
            + " FROM backlog_dispatches WHERE correlation = $correlation ORDER BY id LIMIT 1";
        command.Parameters.AddWithValue("$correlation", correlation);

        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct) ? ReadDispatch(reader) : null;
    }

    public async Task RecordTipAsync(long dispatchId, string repo, string sha, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // REPLACED, NOT APPENDED: the tip is where the work stands now, and the newer publish is
        // the one that says so.
        command.CommandText =
            """
            INSERT INTO backlog_dispatch_tips (dispatch, repo, sha, recorded_at)
            VALUES ($dispatch, $repo, $sha, $now)
            ON CONFLICT (dispatch, repo) DO UPDATE SET sha = excluded.sha, recorded_at = excluded.recorded_at;
            """;
        command.Parameters.AddWithValue("$dispatch", dispatchId);
        command.Parameters.AddWithValue("$repo", repo);
        command.Parameters.AddWithValue("$sha", sha);
        command.Parameters.AddWithValue("$now", Now());

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<BacklogDispatchTip>> TipsAsync(long dispatchId, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT dispatch, repo, sha, recorded_at FROM backlog_dispatch_tips WHERE dispatch = $dispatch ORDER BY repo";
        command.Parameters.AddWithValue("$dispatch", dispatchId);

        var rows = new List<BacklogDispatchTip>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BacklogDispatchTip(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    public async Task RecordBaseAsync(
        long dispatchId, string repo, string defaultSha, string? teamSha, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // KEPT, NOT REPLACED: where the dispatch started is one moment, and a later write would
        // move it past work that is the dispatch's own.
        command.CommandText =
            """
            INSERT INTO backlog_dispatch_bases (dispatch, repo, default_sha, team_sha, recorded_at)
            VALUES ($dispatch, $repo, $default, $team, $now)
            ON CONFLICT (dispatch, repo) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$dispatch", dispatchId);
        command.Parameters.AddWithValue("$repo", repo);
        command.Parameters.AddWithValue("$default", defaultSha);
        command.Parameters.AddWithValue("$team", (object?)teamSha ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<BacklogDispatchBase>> BasesAsync(long dispatchId, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT dispatch, repo, default_sha, team_sha, recorded_at FROM backlog_dispatch_bases"
            + " WHERE dispatch = $dispatch ORDER BY repo";
        command.Parameters.AddWithValue("$dispatch", dispatchId);

        var rows = new List<BacklogDispatchBase>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BacklogDispatchBase(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }

    public async Task RecordMissedStartAsync(
        long dispatchId, string repo, string reason, string? teamSha, string? defaultSha, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // THE SHAS ARE THE DISPATCH'S MOMENT AND ARE KEPT; only the reason moves, and a stopped
        // retry stays stopped with the reason it stopped for.
        command.CommandText =
            """
            INSERT INTO backlog_dispatch_missed_starts (dispatch, repo, reason, team_sha, default_sha, stopped_at, recorded_at)
            VALUES ($dispatch, $repo, $reason, $team, $default, NULL, $now)
            ON CONFLICT (dispatch, repo) DO UPDATE SET reason = excluded.reason
            WHERE backlog_dispatch_missed_starts.stopped_at IS NULL;
            """;
        command.Parameters.AddWithValue("$dispatch", dispatchId);
        command.Parameters.AddWithValue("$repo", repo);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$team", (object?)teamSha ?? DBNull.Value);
        command.Parameters.AddWithValue("$default", (object?)defaultSha ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task StopStartRetryAsync(long dispatchId, string repo, string reason, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "UPDATE backlog_dispatch_missed_starts SET stopped_at = $now, reason = $reason"
            + " WHERE dispatch = $dispatch AND repo = $repo AND stopped_at IS NULL";
        command.Parameters.AddWithValue("$dispatch", dispatchId);
        command.Parameters.AddWithValue("$repo", repo);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", Now());

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<BacklogDispatchMissedStart>> MissedStartsAsync(
        long dispatchId, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "SELECT dispatch, repo, reason, team_sha, default_sha, stopped_at, recorded_at"
            + " FROM backlog_dispatch_missed_starts WHERE dispatch = $dispatch ORDER BY repo";
        command.Parameters.AddWithValue("$dispatch", dispatchId);

        return await ReadMissedAsync(command, ct);
    }

    public async Task<IReadOnlyList<BacklogDispatchMissedStart>> UnrecordedStartsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT m.dispatch, m.repo, m.reason, m.team_sha, m.default_sha, m.stopped_at, m.recorded_at
            FROM backlog_dispatch_missed_starts m
            WHERE NOT EXISTS (
                SELECT 1 FROM backlog_dispatch_bases b WHERE b.dispatch = m.dispatch AND b.repo = m.repo)
            ORDER BY m.dispatch, m.repo
            """;

        return await ReadMissedAsync(command, ct);
    }

    public async Task<IReadOnlySet<long>> DispatchesWithStartsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT DISTINCT dispatch FROM backlog_dispatch_bases";

        var ids = new HashSet<long>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));

        return ids;
    }

    private static async Task<IReadOnlyList<BacklogDispatchMissedStart>> ReadMissedAsync(
        SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<BacklogDispatchMissedStart>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BacklogDispatchMissedStart(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6)));
        }

        return rows;
    }

    public async Task<bool> RecordLandedAsync(
        long dispatchId, string sha, string branch, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // `landed_at IS NULL` IS THE WHOLE RULE: a proven landed is never downgraded, cleared or
        // moved, whoever proves it again later.
        command.CommandText =
            "UPDATE backlog_dispatches SET landed_at = $now, landed_sha = $sha, landed_branch = $branch"
            + " WHERE id = $id AND landed_at IS NULL";
        command.Parameters.AddWithValue("$id", dispatchId);
        command.Parameters.AddWithValue("$sha", sha);
        command.Parameters.AddWithValue("$branch", branch);
        command.Parameters.AddWithValue("$now", Now());

        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static BacklogItem Read(SqliteDataReader reader) =>
        new(
            Id(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9));

    private static BacklogDispatch ReadDispatch(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            Id(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));

    /// <summary>The stored key for an item: the citation itself, <c>B001F</c>. The contract
    /// keeps the number, so the conversion lives here and nowhere else.</summary>
    private static string Key(long id) => PlatformBacklogId.Format(id);

    private static long Id(string key) =>
        PlatformBacklogId.TryParse(key, out var id)
            ? id
            : throw new InvalidOperationException($"Backlog key '{key}' is not a backlog id.");

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection, durability: _durability);

        return connection;
    }
}
