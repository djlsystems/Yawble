using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Messaging;

/// <summary>
/// The log, the cursors and the subscriptions on SQL Server: <c>SqliteMessageStore</c> query for
/// query, over the tables of <see cref="SqlServerMessagingSchema"/>. That class's remarks are the
/// argument for each read; this one says only where the dialect makes a difference. What a row
/// means - the workflow ladder, a payload's subject, the row mapping - is shared with it
/// (<see cref="WorkflowTimingFacts"/>, <see cref="WorkflowStandingRows"/>, <see cref="MessageRows"/>,
/// <see cref="MessageAppend"/>), so the two cannot word the same log differently.
///
/// <para>
/// <b>APPENDS COMMIT IN SEQ ORDER.</b> SQLite has one writer at a time, so a seq is taken and
/// committed before the next is taken, and every reader relies on it: a cursor, a feed or the
/// endings view reads <c>seq &gt; last</c> and moves past what it saw. SQL Server hands out an
/// IDENTITY at insert and commits transactions in any order, so seq 11 could commit and be read and
/// passed before seq 10 commits - and 10 would never be read by anyone. So every append takes one
/// exclusive application lock, held until its transaction ends (<see cref="SqlServerAppendLock"/>),
/// before it inserts: the next append cannot take a seq until this one has committed or rolled back,
/// which makes allocation order commit order. A rolled-back append leaves a gap no row will ever
/// fill, which no reader can skip a row through.
/// </para>
///
/// <para>
/// Every call is one unit of work on its own pooled connection, run through
/// <see cref="SqlServerOpen.RunAsync"/>: a deadlock victim runs again whole, and so does a transient
/// fault (a failover, throttling, a dropped connection) met before the commit was sent. One met on
/// the commit is thrown, since the append may have landed and running it again would store it twice.
/// </para>
/// </summary>
public sealed class SqlServerMessageStore : IMessageLog, ICursors, ISubscriptions
{
    private readonly string _connectionString;
    private readonly MessageWaitRegistry? _waits;
    private readonly ISqlServerAppendRows _appendRows;

    /// <param name="appendRows">The ledger and outcome rows written in each append's transaction.
    /// Required: an append without them would leave a run unbilled and a root unlinked.</param>
    public SqlServerMessageStore(string connectionString, ISqlServerAppendRows appendRows, MessageWaitRegistry? waits = null)
    {
        _connectionString = SqlServerOpen.ConnectionString(connectionString);
        _appendRows = appendRows ?? throw new ArgumentNullException(nameof(appendRows));
        _waits = waits;
    }

    private Task<T> RunAsync<T>(Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        SqlServerOpen.RunAsync(_connectionString, work, ct);

    private static Task TakeAppendLockAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct) =>
        SqlServerAppendLock.TakeAsync(connection, transaction, ct);

    public async Task<Message> AppendAsync(NewMessage message, CancellationToken ct = default)
    {
        MessageAppend.Validate(message);

        var stored = await RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                var row = await InsertAsync(connection, transaction, message, token);
                await SqlServerOpen.CommitAsync(transaction, token);
                return row;
            },
            ct);

        _waits?.Publish(stored);

        return stored;
    }

    /// <summary>
    /// THE LOCK IS TAKEN BEFORE <paramref name="before"/> RUNS, not only before the insert. SQLite
    /// begins this transaction holding its write lock, so two callers whose first statement is a
    /// read-modify-write deciding whether to append never decide from the same state; taking the
    /// append lock first gives these callers the same guarantee against each other.
    /// </summary>
    public async Task<Message?> AppendWithinAsync(
        Func<DbConnection, DbTransaction, CancellationToken, Task<NewMessage?>> before,
        Func<DbConnection, DbTransaction, Message, CancellationToken, Task> after,
        CancellationToken ct = default)
    {
        var stored = await RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                await TakeAppendLockAsync(connection, transaction, token);

                Message? row = null;
                if (await before(connection, transaction, token) is { } message)
                {
                    MessageAppend.Validate(message);

                    row = await InsertAsync(connection, transaction, message, token);
                    await after(connection, transaction, row, token);
                }

                await SqlServerOpen.CommitAsync(transaction, token);
                return row;
            },
            ct);

        // Published only once committed: a live subscriber must never be handed a row that rolled back.
        if (stored is not null) _waits?.Publish(stored);

        return stored;
    }

    /// <summary>The row itself, inside the caller's transaction and under the append lock. Nothing is published here.</summary>
    private async Task<Message> InsertAsync(
        SqlConnection connection, SqlTransaction transaction, NewMessage message, CancellationToken ct)
    {
        await TakeAppendLockAsync(connection, transaction, ct);

        var payload = MessageAppend.Redacted(message);

        long correlation = 0;
        var depth = 0;

        // A cause is resolved INSIDE the transaction, so a message cannot inherit from a parent that
        // is being written concurrently and might not commit.
        if (message.CausationSeq is { } cause)
        {
            await using var parent = connection.CreateCommand();
            parent.Transaction = transaction;
            parent.CommandText = "SELECT correlation_id, depth FROM messages WHERE seq = @seq";
            parent.Parameters.AddWithValue("@seq", cause);

            await using var reader = await parent.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct)) throw MessageAppend.NoSuchCause(cause);

            correlation = reader.GetInt64(0);
            depth = checked((int)reader.GetInt64(1) + 1);
        }

        var occurredAt = DateTimeOffset.UtcNow;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO messages (type, payload, source, correlation_id, causation_seq, depth, occurred_at)
            OUTPUT inserted.seq
            VALUES (@type, @payload, @source, @correlation, @causation, @depth, @at)
            """;

        insert.Parameters.AddWithValue("@type", message.Type);
        insert.Parameters.Add("@payload", System.Data.SqlDbType.NVarChar, -1).Value = MessageAppend.RowPayload(payload);
        insert.Parameters.AddWithValue("@source", message.Source);
        insert.Parameters.AddWithValue("@correlation", correlation);
        insert.Parameters.AddWithValue("@causation", (object?)message.CausationSeq ?? DBNull.Value);
        insert.Parameters.AddWithValue("@depth", (long)depth);
        insert.Parameters.AddWithValue("@at", Instant.Stamp(occurredAt));

        var seq = (long)(await insert.ExecuteScalarAsync(ct))!;

        // A root correlates to ITSELF, which is only knowable once the row has its seq. Same
        // transaction, so no reader can observe the placeholder zero.
        if (message.CausationSeq is null)
        {
            correlation = seq;

            await using var settle = connection.CreateCommand();
            settle.Transaction = transaction;
            settle.CommandText = "UPDATE messages SET correlation_id = @seq WHERE seq = @seq";
            settle.Parameters.AddWithValue("@seq", seq);
            await settle.ExecuteNonQueryAsync(ct);
        }

        // THE REDACTED PAYLOAD, NOT THE ONE HANDED IN - see SqliteMessageStore.InsertAsync.
        var stored = new Message(
            seq, message.Type, payload, message.Source,
            correlation, message.CausationSeq, depth, occurredAt);

        // THE ACCOUNTING AND THE OUTCOME LINK, IN THIS SAME TRANSACTION, as SqliteMessageStore writes
        // its LedgerRows and OutcomeLinks: when either throws, the row is not stored either.
        await _appendRows.OnAppendAsync(connection, transaction, stored, ct);

        return stored;
    }

    /// <summary>One parameter per value, named <paramref name="prefix"/>0, 1, ...: the IN list a type
    /// or team filter needs, never interpolated values.</summary>
    private static string InList(SqlCommand command, string prefix, IEnumerable<string> values)
    {
        var names = new List<string>();
        foreach (var value in values)
        {
            var name = $"@{prefix}{names.Count}";
            names.Add(name);
            command.Parameters.AddWithValue(name, value);
        }

        return string.Join(", ", names);
    }

    private Task<IReadOnlyList<Message>> ReadMessagesAsync(string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind(command);
                return await MessageRows.ReadAllAsync(command, token);
            },
            ct);

    private async Task<IReadOnlyList<Message>> ReadMessagesAsync(Func<SqlCommand, string> build, CancellationToken ct) =>
        await RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = build(command);
                return await MessageRows.ReadAllAsync(command, token);
            },
            ct);

    public async Task<IReadOnlyList<Message>> ReadAfterAsync(
        long afterSeq, IReadOnlyCollection<string> types, int max, CancellationToken ct = default)
    {
        // No types means no interest - see SqliteMessageStore.ReadAfterAsync.
        if (types.Count == 0 || max <= 0) return [];

        return await ReadMessagesAsync(
            command =>
            {
                command.Parameters.AddWithValue("@after", afterSeq);
                command.Parameters.AddWithValue("@max", max);
                return $"""
                    SELECT TOP (@max) {MessageRows.Columns}
                    FROM messages
                    WHERE seq > @after AND type IN ({InList(command, "t", types)})
                    ORDER BY seq
                    """;
            },
            ct);
    }

    public async Task<IReadOnlyList<Message>> ReadLatestAsync(
        long afterSeq, IReadOnlyCollection<string> types, int max, CancellationToken ct = default)
    {
        if (types.Count == 0 || max <= 0) return [];

        // PLUS an addressed instruction, matched by prefix: see SqliteMessageStore.ReadLatestAsync.
        var newestFirst = await ReadMessagesAsync(
            command =>
            {
                command.Parameters.AddWithValue("@after", afterSeq);
                command.Parameters.AddWithValue("@max", max);
                command.Parameters.AddWithValue("@instructionPrefix", MessageTypes.InstructionPrefix + "%");
                return $"""
                    SELECT TOP (@max) {MessageRows.Columns}
                    FROM messages
                    WHERE seq > @after
                      AND (type IN ({InList(command, "t", types)}) OR type LIKE @instructionPrefix {SqlServerSql.Like})
                    ORDER BY seq DESC
                    """;
            },
            ct);

        return [.. newestFirst.Reverse()];
    }

    public async Task<IReadOnlyList<Message>> ReadLatestAsync(
        long afterSeq, IReadOnlyCollection<string> types, IReadOnlyCollection<string> teams, int max, CancellationToken ct = default)
    {
        if (types.Count == 0 || teams.Count == 0 || max <= 0) return [];

        // THE TEAM BEFORE THE WINDOW, over the case-insensitive computed `team` and its index; a row
        // with no team is let through, as SQLite's is.
        var newestFirst = await ReadMessagesAsync(
            command =>
            {
                command.Parameters.AddWithValue("@after", afterSeq);
                command.Parameters.AddWithValue("@max", max);
                command.Parameters.AddWithValue("@instructionPrefix", MessageTypes.InstructionPrefix + "%");
                return $"""
                    SELECT TOP (@max) {MessageRows.Columns}
                    FROM messages
                    WHERE seq > @after
                      AND (team IN ({InList(command, "team", teams)}) OR team IS NULL)
                      AND (type IN ({InList(command, "t", types)}) OR type LIKE @instructionPrefix {SqlServerSql.Like})
                    ORDER BY seq DESC
                    """;
            },
            ct);

        return [.. newestFirst.Reverse()];
    }

    public async Task<Message?> LatestPublishedAsync(
        string qualifiedId, IReadOnlyCollection<string> types, long sinceSeq, CancellationToken ct = default)
    {
        if (types.Count == 0) return null;

        return (await ReadMessagesAsync(
            command =>
            {
                command.Parameters.AddWithValue("@source", qualifiedId);
                command.Parameters.AddWithValue("@since", sinceSeq);
                return $"""
                    SELECT TOP 1 {MessageRows.Columns}
                    FROM messages
                    WHERE source = @source COLLATE {SqlServerSql.NoCase} AND seq > @since AND type IN ({InList(command, "t", types)})
                    ORDER BY seq DESC
                    """;
            },
            ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<Message>> ReadForContainerAsync(
        string qualifiedId, long sinceSeq, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        return await ReadMessagesAsync(
            $"""
            SELECT TOP (@max) {MessageRows.Columns}
            FROM messages
            WHERE source = @id COLLATE {SqlServerSql.NoCase}
              AND seq > @since
            ORDER BY seq DESC
            """,
            command =>
            {
                command.Parameters.AddWithValue("@id", qualifiedId);
                command.Parameters.AddWithValue("@since", sinceSeq);
                command.Parameters.AddWithValue("@max", max);
            },
            ct);
    }

    public async Task<IReadOnlyList<Message>> ReadMemberBeforeAsync(
        ContainerId container, long sinceSeq, long beforeSeq, IReadOnlyCollection<string> types,
        int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        return await ReadMessagesAsync(
            command =>
            {
                command.Parameters.AddWithValue("@since", sinceSeq);
                command.Parameters.AddWithValue("@before", beforeSeq);
                command.Parameters.AddWithValue("@source", container.ToString());
                command.Parameters.AddWithValue("@instruction", MessageTypes.InstructionFor(container));
                command.Parameters.AddWithValue("@max", max);

                // With no types the published half matches nothing rather than everything.
                var published = types.Count == 0
                    ? "1 = 0"
                    : $"(source = @source COLLATE {SqlServerSql.NoCase} AND type IN ({InList(command, "t", types)}))";

                return $"""
                    SELECT TOP (@max) {MessageRows.Columns}
                    FROM messages
                    WHERE seq > @since AND seq < @before
                      AND (type = @instruction OR {published})
                    ORDER BY seq DESC
                    """;
            },
            ct);
    }

    public Task<IReadOnlyList<RunRow>> ReadRunsAsync(
        ContainerId container, long sinceSeq, long beforeSeq, int max, CancellationToken ct = default) =>
        ReadRunsAsync(container, sinceSeq, beforeSeq, max, RunsWith.Transcript, ct);

    public async Task<IReadOnlyList<RunRow>> ReadRunsAsync(
        ContainerId container, long sinceSeq, long beforeSeq, int max, RunsWith which, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        // SqliteMessageStore.ReadRunsAsync, the same subqueries: the run a terminal closed began at
        // this member's latest `started` below it, and a run-form `blocked` between the two made it
        // Blocked. The two flags are BIGINT so the shared reader takes them as SQLite's.
        var blockedEnd = which == RunsWith.AnyRun
            ? $"""
               OR (t.type = @blocked
                   AND {SqlServerSql.Present("t.payload", PayloadFields.Item)}
                   AND NOT EXISTS (SELECT 1 FROM messages n
                                   WHERE n.source = t.source AND n.seq > t.seq
                                     AND n.type IN (@completed, @failed, @blocked)
                                     AND n.seq < COALESCE((SELECT MIN(y.seq) FROM messages y
                                                           WHERE y.source = t.source AND y.type = @started
                                                             AND y.seq > t.seq), 9223372036854775807)))
               """
            : "";

        var terminalFilter = which == RunsWith.Transcript
            ? SqlServerSql.Present("t.payload", PayloadFields.AgentTranscript)
            : SqlServerSql.Absent("t.payload", PayloadFields.UsageCountedOn);

        var sql =
            $"""
            SELECT TOP (@max) {Prefixed("t")},
                   s.occurred_at,
                   CAST(CASE WHEN t.type = @blocked OR EXISTS (SELECT 1 FROM messages b
                           WHERE b.source = t.source AND b.type = @blocked
                             AND b.seq < t.seq AND b.seq > COALESCE(s.seq, @since)
                             AND {SqlServerSql.Absent("b.payload", "item")}) THEN 1 ELSE 0 END AS BIGINT),
                   CAST(CASE WHEN t.type = @blocked AND NOT EXISTS (SELECT 1 FROM messages y
                           WHERE y.source = t.source AND y.type = @started AND y.seq > t.seq) THEN 1 ELSE 0 END AS BIGINT)
            FROM messages t
            LEFT JOIN messages s ON s.seq = (
                SELECT MAX(x.seq) FROM messages x
                WHERE x.source = t.source AND x.type = @started AND x.seq < t.seq AND x.seq > @since)
            WHERE t.seq > @since AND t.seq < @before
              AND t.source = @source COLLATE {SqlServerSql.NoCase}
              AND ((t.type IN (@completed, @failed) AND {terminalFilter})
                   {blockedEnd})
            ORDER BY t.seq DESC
            """;

        return await RunAsync<IReadOnlyList<RunRow>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@since", sinceSeq);
                command.Parameters.AddWithValue("@before", beforeSeq);
                command.Parameters.AddWithValue("@source", container.ToString());
                command.Parameters.AddWithValue("@completed", MessageTypes.Completed);
                command.Parameters.AddWithValue("@failed", MessageTypes.Failed);
                command.Parameters.AddWithValue("@started", MessageTypes.Started);
                command.Parameters.AddWithValue("@blocked", MessageTypes.Blocked);
                command.Parameters.AddWithValue("@max", max);

                var runs = new List<RunRow>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) runs.Add(MessageRows.ReadRun(reader));
                return runs;
            },
            ct);
    }

    private static string Prefixed(string alias) =>
        string.Join(", ", MessageRows.Columns.Split(", ").Select(column => $"{alias}.{column}"));

    /// <summary>
    /// The LIKE pattern for a NESTED source (<c>team/x/y</c>) of a team, used with <c>ESCAPE '\'</c>;
    /// the team itself is matched on the computed <c>team</c> column. See
    /// <c>SqliteMessageStore.NestedSourcePattern</c> for why the id is escaped.
    /// </summary>
    private static string NestedSourcePattern(string team) => SqlServerSql.LikeLiteral(team) + "/%/%";

    /// <summary>A team's own rows: on the team column, and not a nested source. Binds <c>@team</c>
    /// and <c>@nested</c>.</summary>
    private static string TeamRows(string alias) =>
        $"{alias}team = @team AND {alias}source NOT LIKE @nested {SqlServerSql.Like} ESCAPE '\\'";

    private static void BindTeam(SqlCommand command, string team)
    {
        command.Parameters.AddWithValue("@team", team);
        command.Parameters.AddWithValue("@nested", NestedSourcePattern(team));
    }

    /// <summary>A JSON integer field as a BIGINT, NULL when absent or not a number - SQLite's
    /// <c>CAST(json_extract(...) AS INTEGER)</c> over the numbers the writers put there.</summary>
    private static string Int(string field) => $"TRY_CAST(JSON_VALUE(payload, '$.{field}') AS BIGINT)";

    /// <summary>SQLite's <c>json_extract(payload, '$.field') IS NOT NULL</c> for a number field.</summary>
    private static string Has(string field) => $"JSON_VALUE(payload, '$.{field}') IS NOT NULL";

    private static readonly string NotExcluded = $"COALESCE(JSON_VALUE(payload, '$.tokensSource'), N'') <> @excluded";
    private static readonly string Split = $"{Has("tokensIn")} AND {Has("tokensOut")} AND {NotExcluded}";

    /// <summary>
    /// LogSpend.BillableSum in T-SQL: billable tokens summed over completed/failed rows, weighted per
    /// row as <see cref="InvocationUsage.BillableTokens"/> weights one run. Binds <c>@excluded</c>.
    /// </summary>
    private static readonly string BillableSum =
        $"""
        COALESCE(SUM(CASE
            WHEN {NotExcluded}
             AND ({Has("tokensIn")} OR {Has("tokensTotal")})
            THEN ISNULL({Int("tokensIn")}, 0)
               + ISNULL({Int("tokensOut")}, 0)
               + ISNULL({Int("tokensTotal")}, 0)
               + ISNULL({Int("tokensCachedIn")}, 0) / 10
               + (ISNULL({Int("tokensCacheCreation")}, 0) * 5) / 4
            END), 0)
        """;

    public async Task<IReadOnlyList<ContainerUsageRow>> SumUsageForTeamAsync(
        string team, long sinceSeq, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(team)) return [];

        // SqliteMessageStore.SumUsageForTeamAsync's figures. Grouped by source WITHOUT CASE, as
        // SQLite's `GROUP BY source COLLATE NOCASE`, answering one spelling of each source.
        var sql =
            $"""
            SELECT
                MIN(source),
                CAST(COALESCE(SUM(CASE WHEN {Split} THEN {Int("tokensIn")} END), 0) AS BIGINT),
                CAST(COALESCE(SUM(CASE WHEN {Split} THEN {Int("tokensOut")} END), 0) AS BIGINT),
                CAST(SUM(CASE WHEN {Split} THEN 1 ELSE 0 END) AS BIGINT),
                CAST(SUM(CASE WHEN NOT ({Split}) THEN 1 ELSE 0 END) AS BIGINT),
                CAST(SUM(CASE WHEN {Has("tokensTotal")} AND {NotExcluded} THEN {Int("tokensTotal")} END) AS BIGINT),
                CAST(COALESCE(SUM(CASE WHEN {Split} THEN {Int("tokensCachedIn")} END), 0) AS BIGINT),
                CAST(COALESCE(SUM(CASE WHEN {Split} THEN {Int("tokensCacheCreation")} END), 0) AS BIGINT),
                CAST({BillableSum} AS BIGINT)
            FROM messages
            WHERE type IN (@completed, @failed)
              AND JSON_VALUE(payload, '$.usageCountedOn') IS NULL
              AND {TeamRows("")}
              AND seq > @since
            GROUP BY source COLLATE {SqlServerSql.NoCase}
            """;

        return await RunAsync<IReadOnlyList<ContainerUsageRow>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@completed", MessageTypes.Completed);
                command.Parameters.AddWithValue("@failed", MessageTypes.Failed);
                command.Parameters.AddWithValue("@excluded", UsageSource.ExcludedEstimate);
                command.Parameters.AddWithValue("@since", sinceSeq);
                BindTeam(command, team);

                var rows = new List<ContainerUsageRow>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) rows.Add(MessageRows.ReadUsage(reader));
                return rows;
            },
            ct);
    }

    public async Task<TeamWorkflowTiming> ElapsedForTeamAsync(
        string team, long sinceSeq, CancellationToken ct = default)
    {
        var serverNow = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(team)) return WorkflowTimingFacts.Unavailable(serverNow, "No team was named.");

        return await RunAsync(
            async (connection, token) =>
            {
                long correlation;

                // THE MOST RECENTLY ACTIVE WORKFLOW THIS TEAM'S OWN ROWS BELONG TO, above the floor and
                // not a member's removal - SqliteMessageStore.ElapsedForTeamAsync's pick.
                await using (var pick = connection.CreateCommand())
                {
                    pick.CommandText =
                        $"""
                        SELECT COALESCE(MAX(correlation_id), 0)
                        FROM (
                          SELECT TOP 1 correlation_id, MAX(occurred_at) AS last_activity
                          FROM messages
                          WHERE {TeamRows("")}
                            AND seq > @since
                            AND {SqlServerWorkflowSql.Counts("messages")}
                          GROUP BY correlation_id
                          ORDER BY last_activity DESC
                        ) latest
                        """;
                    BindTeam(pick, team);
                    pick.Parameters.AddWithValue("@since", sinceSeq);
                    SqlServerWorkflowSql.BindCounts(pick);

                    correlation = Convert.ToInt64(await pick.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                }

                if (correlation == 0)
                {
                    return WorkflowTimingFacts.Unavailable(
                        serverNow, "This team has published nothing since its members were created");
                }

                return await TimingForAsync(connection, team, correlation, sinceSeq, serverNow, token);
            },
            ct);
    }

    public async Task<TeamWorkflowTiming> WorkflowForTeamAsync(
        string team, long correlation, long sinceSeq, CancellationToken ct = default)
    {
        var serverNow = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(team)) return WorkflowTimingFacts.Unavailable(serverNow, "No team was named.");

        return await RunAsync(
            (connection, token) => TimingForAsync(connection, team, correlation, sinceSeq, serverNow, token), ct);
    }

    /// <summary>
    /// ONE WORKFLOW, FULLY DESCRIBED: SqliteMessageStore.TimingForAsync's queries, each answering one
    /// fact into <see cref="WorkflowTimingFacts"/>, which ranks them.
    /// </summary>
    private async Task<TeamWorkflowTiming> TimingForAsync(
        SqlConnection connection, string team, long correlation, long sinceSeq, DateTimeOffset serverNow, CancellationToken ct)
    {
        var facts = new WorkflowTimingFacts(correlation);

        SqlCommand Command(string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@correlation", correlation);
            command.Parameters.AddWithValue("@since", sinceSeq);
            return command;
        }

        // EVERY ROW IN THE CORRELATION, not only the team's own: a workflow starts at its root.
        await using (var bounds = Command(
            "SELECT MIN(occurred_at), MAX(occurred_at) FROM messages WHERE correlation_id = @correlation AND seq > @since"))
        {
            await using var reader = await bounds.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
            {
                facts.StartedAt = MessageRows.ReadStamp(reader.GetString(0));
                facts.LastActivityAt = MessageRows.ReadStamp(reader.GetString(1));
            }
        }

        // THE ROOT, FETCHED BY ITS OWN SEQ, unconditionally and without a floor.
        await using (var root = Command("SELECT type, payload FROM messages WHERE seq = @correlation"))
        {
            await using var reader = await root.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) facts.Root(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        // BOTH TERMINAL TYPES, AND WHICH ONE IT WAS, with the last close's instant.
        await using (var declared = Command(
            """
            SELECT DISTINCT type,
              (SELECT TOP 1 occurred_at FROM messages
               WHERE correlation_id = @correlation AND seq > @since
               AND type = @closed
               ORDER BY seq DESC) AS closed_at
            FROM messages
            WHERE correlation_id = @correlation AND seq > @since
              AND type IN (@completed, @closed)
            """))
        {
            declared.Parameters.AddWithValue("@completed", MessageTypes.WorkflowCompleted);
            declared.Parameters.AddWithValue("@closed", MessageTypes.WorkflowClosed);

            await using var reader = await declared.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) facts.Declared(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        // A closed workflow re-opened by an instruction or a card event since its last close.
        if (facts.Closed && facts.ClosedAt.HasValue)
        {
            await using var reopen = Command(
                $"""
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM messages woke
                    WHERE woke.correlation_id = @correlation
                      AND woke.seq > (
                          SELECT TOP 1 seq FROM messages
                          WHERE correlation_id = @correlation AND seq > @since
                          AND type = @closed
                          ORDER BY seq DESC)
                      AND (woke.type LIKE @instructionPrefix {SqlServerSql.Like} OR woke.type LIKE @cardPrefix {SqlServerSql.Like}))
                THEN 1 ELSE 0 END
                """);
            reopen.Parameters.AddWithValue("@closed", MessageTypes.WorkflowClosed);
            SqlServerWorkflowSql.BindWakePrefixes(reopen);

            if (Convert.ToInt32(await reopen.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != 0) facts.Closed = false;
        }

        // IS THIS ONE WORKFLOW PAUSED: the newest pause no resume follows, both above the floor.
        await using (var paused = Command(
            """
            SELECT TOP 1 occurred_at, payload
              FROM messages
             WHERE correlation_id = @correlation
               AND seq > @since
               AND type = @paused
               AND seq > COALESCE(
                   (SELECT MAX(seq) FROM messages
                     WHERE correlation_id = @correlation
                       AND seq > @since
                       AND type = @resumed), 0)
             ORDER BY seq DESC
            """))
        {
            paused.Parameters.AddWithValue("@paused", MessageTypes.WorkflowPaused);
            paused.Parameters.AddWithValue("@resumed", MessageTypes.WorkflowResumed);

            await using var reader = await paused.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) facts.Paused(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        // ONE ROW PER MEMBER: its LAST OUTCOME in this workflow, `completed` or `failed` only.
        await using (var last = Command(
            $"""
            SELECT source, type FROM messages
            WHERE seq IN (
                SELECT MAX(seq) FROM messages
                WHERE correlation_id = @correlation
                  AND seq > @since
                  AND type IN (@completed, @failed)
                  AND {TeamRows("")}
                GROUP BY source COLLATE {SqlServerSql.NoCase})
            ORDER BY seq
            """))
        {
            last.Parameters.AddWithValue("@completed", MessageTypes.Completed);
            last.Parameters.AddWithValue("@failed", MessageTypes.Failed);
            BindTeam(last, team);

            await using var reader = await last.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) facts.LastOutcome(reader.GetString(0), reader.GetString(1));
        }

        // Each member's last `failed` row, for whether a person stopped the run.
        await using (var failureDetails = Command(
            $"""
            SELECT m.source, m.payload FROM messages m
            WHERE m.correlation_id = @correlation
              AND m.seq > @since
              AND m.type = @failed
              AND {TeamRows("m.")}
              AND m.seq IN (
                SELECT MAX(seq) FROM messages
                WHERE correlation_id = @correlation
                  AND seq > @since
                  AND type = @failed
                  AND source = m.source COLLATE {SqlServerSql.NoCase})
            ORDER BY m.seq
            """))
        {
            failureDetails.Parameters.AddWithValue("@failed", MessageTypes.Failed);
            BindTeam(failureDetails, team);

            await using var reader = await failureDetails.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) facts.LastFailure(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        // A DECLARATION IS LIVE UNLESS THE MEMBER HAS STARTED AGAIN SINCE IT.
        await using (var declarations = Command(
            $"""
            SELECT d.source, d.type FROM messages d
            WHERE d.correlation_id = @correlation
              AND d.seq > @since
              AND d.type IN (@blocked, @needsDecision)
              AND {TeamRows("d.")}
              AND NOT EXISTS (
                  SELECT 1 FROM messages later
                  WHERE later.correlation_id = d.correlation_id
                    AND later.seq > d.seq
                    AND later.type = @started
                    AND later.source = d.source COLLATE {SqlServerSql.NoCase})
            ORDER BY d.seq
            """))
        {
            declarations.Parameters.AddWithValue("@blocked", MessageTypes.Blocked);
            declarations.Parameters.AddWithValue("@needsDecision", MessageTypes.NeedsDecision);
            declarations.Parameters.AddWithValue("@started", MessageTypes.Started);
            BindTeam(declarations, team);

            await using var reader = await declarations.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) facts.LiveDeclaration(reader.GetString(0), reader.GetString(1));
        }

        // A COUNT OF RUNS that failed, not of members, without the runs a person stopped. SQLite's
        // `json_extract(...) IS NOT 1` is true for anything but a JSON true there.
        await using (var failures = Command(
            $"""
            SELECT COUNT(*) FROM messages
            WHERE correlation_id = @correlation
              AND seq > @since
              AND type = @failed
              AND {TeamRows("")}
              AND COALESCE(JSON_VALUE(payload, '$.{PayloadFields.StoppedByPerson}'), N'') <> N'true'
            """))
        {
            failures.Parameters.AddWithValue("@failed", MessageTypes.Failed);
            BindTeam(failures, team);

            facts.RunsFailed = Convert.ToInt32(await failures.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        // EACH `started` PAIRED WITH THE NEXT TERMINAL EVENT FROM THE SAME SOURCE.
        await using (var pairs = Command(
            $"""
            SELECT s.source,
                   s.occurred_at,
                   (SELECT TOP 1 e.occurred_at FROM messages e
                     WHERE e.correlation_id = s.correlation_id
                       AND e.source = s.source COLLATE {SqlServerSql.NoCase}
                       AND e.seq > s.seq
                       AND e.type IN (@completed, @failed, @blocked)
                     ORDER BY e.seq)
            FROM messages s
            WHERE s.correlation_id = @correlation
              AND s.seq > @since
              AND s.type = @started
              AND {TeamRows("s.")}
            ORDER BY s.seq
            """))
        {
            pairs.Parameters.AddWithValue("@started", MessageTypes.Started);
            pairs.Parameters.AddWithValue("@completed", MessageTypes.Completed);
            pairs.Parameters.AddWithValue("@failed", MessageTypes.Failed);
            pairs.Parameters.AddWithValue("@blocked", MessageTypes.Blocked);
            BindTeam(pairs, team);

            await using var reader = await pairs.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                facts.RunPair(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        var spend = await SpendAsync(connection, correlation, sinceLastNudge: false, ct);
        var sinceNudge = await SpendAsync(connection, correlation, sinceLastNudge: true, ct);

        return facts.Build(serverNow, spend, sinceNudge);
    }

    public async Task<IReadOnlySet<long>> OpenWorkflowsAmongAsync(
        IReadOnlyCollection<long> correlations, CancellationToken ct = default)
    {
        var asked = correlations.Distinct().ToList();
        if (asked.Count == 0) return new HashSet<long>();

        // The asked list as ONE JSON parameter rather than one parameter each: SQL Server caps a
        // command at 2,100 parameters, far below SQLite's ceiling.
        return await RunAsync<IReadOnlySet<long>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    SELECT m.correlation_id
                    FROM (
                        SELECT DISTINCT correlation_id
                        FROM messages
                        WHERE correlation_id IN (SELECT CAST(value AS BIGINT) FROM OPENJSON(@asked))
                    ) m
                    WHERE {SqlServerWorkflowSql.NotClosed}
                    """;
                command.Parameters.AddWithValue("@asked", JsonSerializer.Serialize(asked));
                SqlServerWorkflowSql.BindNotClosed(command);

                var open = new HashSet<long>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) open.Add(reader.GetInt64(0));
                return open;
            },
            ct);
    }

    public async Task<IReadOnlyDictionary<long, WorkflowStanding>> WorkflowStandingsAmongAsync(
        IReadOnlyDictionary<long, string> teams, CancellationToken ct = default)
    {
        if (teams.Count == 0) return new Dictionary<long, WorkflowStanding>();

        // SqliteMessageStore.WorkflowStandingsAmongAsync's five facets in one statement, typed so the
        // shared fold reads each column as SQLite's.
        var sql =
            $"""
            WITH asked AS (SELECT CAST(value AS BIGINT) AS correlation_id FROM OPENJSON(@asked))
            SELECT CAST(N'row' AS NVARCHAR(10)), a.correlation_id, CAST(NULL AS NVARCHAR(450)), CAST(NULL AS NVARCHAR(450)),
                   CAST(NULL AS NVARCHAR(40)), CAST(0 AS BIGINT), CAST(0 AS BIGINT)
            FROM asked a
            WHERE EXISTS (SELECT 1 FROM messages r WHERE r.correlation_id = a.correlation_id)
            UNION ALL
            SELECT N'end', d.correlation_id, NULL, d.type, d.occurred_at,
                   CAST(CASE WHEN EXISTS (
                       SELECT 1 FROM messages woke
                       WHERE woke.correlation_id = d.correlation_id
                         AND woke.seq > d.seq
                         AND (woke.type LIKE @instructionPrefix {SqlServerSql.Like} OR woke.type LIKE @cardPrefix {SqlServerSql.Like}))
                        THEN 1 ELSE 0 END AS BIGINT),
                   d.seq
            FROM messages d
            WHERE d.correlation_id IN (SELECT correlation_id FROM asked)
              AND d.type IN (@completed, @closed)
            UNION ALL
            SELECT N'pause', p.correlation_id, NULL, p.type, NULL, 0, MAX(p.seq)
            FROM messages p
            WHERE p.correlation_id IN (SELECT correlation_id FROM asked)
              AND p.type IN (@paused, @resumed)
            GROUP BY p.correlation_id, p.type
            UNION ALL
            SELECT N'last', o.correlation_id, o.source, o.type, NULL,
                   CAST(CASE WHEN o.type = @runFailed AND ISJSON(o.payload) = 1
                             THEN CASE WHEN JSON_VALUE(o.payload, '$.{PayloadFields.StoppedByPerson}') = N'true' THEN 1 ELSE 0 END
                             ELSE 0 END AS BIGINT),
                   o.seq
            FROM messages o
            WHERE o.seq IN (
                SELECT MAX(seq) FROM messages
                WHERE correlation_id IN (SELECT correlation_id FROM asked)
                  AND type IN (@runCompleted, @runFailed)
                GROUP BY correlation_id, source COLLATE {SqlServerSql.NoCase})
            UNION ALL
            SELECT N'blocked', b.correlation_id, b.source, b.type, NULL, 0, b.seq
            FROM messages b
            WHERE b.correlation_id IN (SELECT correlation_id FROM asked)
              AND b.type = @blocked
              AND NOT EXISTS (
                  SELECT 1 FROM messages later
                  WHERE later.correlation_id = b.correlation_id
                    AND later.seq > b.seq
                    AND later.type = @started
                    AND later.source = b.source COLLATE {SqlServerSql.NoCase})
            """;

        return await RunAsync<IReadOnlyDictionary<long, WorkflowStanding>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@asked", JsonSerializer.Serialize(teams.Keys.ToList()));
                command.Parameters.AddWithValue("@paused", MessageTypes.WorkflowPaused);
                command.Parameters.AddWithValue("@resumed", MessageTypes.WorkflowResumed);
                command.Parameters.AddWithValue("@runCompleted", MessageTypes.Completed);
                command.Parameters.AddWithValue("@runFailed", MessageTypes.Failed);
                command.Parameters.AddWithValue("@blocked", MessageTypes.Blocked);
                command.Parameters.AddWithValue("@started", MessageTypes.Started);
                SqlServerWorkflowSql.BindNotClosed(command);

                var rows = new WorkflowStandingRows(teams);
                await using (var reader = await command.ExecuteReaderAsync(token))
                {
                    await rows.ReadAsync(reader, token);
                }

                return rows.Build();
            },
            ct);
    }

    public async Task<TeamWorkflows> WorkflowsForTeamAsync(
        string team, long sinceSeq, CancellationToken ct = default)
    {
        var serverNow = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(team))
        {
            return new TeamWorkflows(false, 0, 0, null, serverNow, [], "No team was named.");
        }

        // SqliteMessageStore.WorkflowsForTeamAsync's four reads: the newest fifty of the team's own
        // correlations above the floor, the uncapped open count and total, and the open span.
        var scope = $"{TeamRows("m.")} AND m.seq > @since AND {SqlServerWorkflowSql.Counts("m")}";

        return await RunAsync(
            async (connection, token) =>
            {
                SqlCommand Command(string sql)
                {
                    var command = connection.CreateCommand();
                    command.CommandText = sql;
                    BindTeam(command, team);
                    command.Parameters.AddWithValue("@since", sinceSeq);
                    SqlServerWorkflowSql.BindCounts(command);
                    return command;
                }

                var correlations = new List<long>();

                await using (var pick = Command(
                    $"SELECT DISTINCT TOP 50 m.correlation_id FROM messages m WHERE {scope} ORDER BY m.correlation_id DESC"))
                {
                    await using var reader = await pick.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) correlations.Add(reader.GetInt64(0));
                }

                if (correlations.Count == 0)
                {
                    return new TeamWorkflows(
                        false, 0, 0, null, serverNow, [],
                        "This team has published nothing since its members were created");
                }

                int openCount;
                await using (var count = Command(
                    $"SELECT COUNT(DISTINCT m.correlation_id) FROM messages m WHERE {scope} AND {SqlServerWorkflowSql.NotClosed}"))
                {
                    SqlServerWorkflowSql.BindNotClosed(count);
                    openCount = Convert.ToInt32(await count.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                }

                int totalCount;
                await using (var total = Command($"SELECT COUNT(DISTINCT m.correlation_id) FROM messages m WHERE {scope}"))
                {
                    totalCount = Convert.ToInt32(await total.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                }

                DateTimeOffset? earliest = null;
                await using (var span = Command(
                    $"""
                    SELECT MIN(occurred_at)
                    FROM messages
                    WHERE seq > @since
                      AND correlation_id IN (
                          SELECT DISTINCT m.correlation_id
                          FROM messages m
                          WHERE {scope}
                            AND {SqlServerWorkflowSql.NotClosed})
                    """))
                {
                    SqlServerWorkflowSql.BindNotClosed(span);
                    if (await span.ExecuteScalarAsync(token) is string stamp) earliest = MessageRows.ReadStamp(stamp);
                }

                var timings = new List<TeamWorkflowTiming>(correlations.Count);
                foreach (var correlation in correlations)
                {
                    timings.Add(await TimingForAsync(connection, team, correlation, sinceSeq, serverNow, token));
                }

                return new TeamWorkflows(true, openCount, totalCount, earliest, serverNow, timings);
            },
            ct);
    }

    public async Task<TeamWorkflowStretch?> WorkflowStretchForTeamAsync(
        string team, long sinceSeq, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(team)) return null;

        return await RunAsync(
            async (connection, token) =>
            {
                await using var stretch = connection.CreateCommand();
                stretch.CommandText =
                    $"""
                    SELECT MIN(occurred_at), MAX(occurred_at), MIN(correlation_id)
                    FROM messages
                    WHERE seq > @since
                      AND correlation_id IN (
                          SELECT DISTINCT m.correlation_id
                          FROM messages m
                          WHERE {TeamRows("m.")}
                            AND m.seq > @since)
                    """;
                BindTeam(stretch, team);
                stretch.Parameters.AddWithValue("@since", sinceSeq);

                await using var reader = await stretch.ExecuteReaderAsync(token);

                return await reader.ReadAsync(token) && !reader.IsDBNull(0)
                    ? new TeamWorkflowStretch(
                        MessageRows.ReadStamp(reader.GetString(0)),
                        MessageRows.ReadStamp(reader.GetString(1)),
                        reader.GetInt64(2) <= sinceSeq)
                    : null;
            },
            ct);
    }

    public async Task<ContainerMarks> LiveMarksForAsync(
        ContainerId member, long sinceSeq, CancellationToken ct = default)
    {
        var source = member.ToString();

        // SqliteMessageStore.LiveMarksForAsync's two predicates: a declaration is live until the
        // member starts again; a failure is live while it is the member's last outcome.
        var declarations = await ReadMessagesAsync(
            $"""
            SELECT {MessageRows.Columns}
            FROM messages d
            WHERE d.source = @source COLLATE {SqlServerSql.NoCase}
              AND d.seq > @since
              AND d.type IN (@blocked, @needsDecision)
              AND NOT EXISTS (
                  SELECT 1 FROM messages later
                  WHERE later.seq > d.seq
                    AND later.type = @started
                    AND later.source = d.source COLLATE {SqlServerSql.NoCase})
            ORDER BY d.seq
            """,
            command =>
            {
                command.Parameters.AddWithValue("@source", source);
                command.Parameters.AddWithValue("@since", sinceSeq);
                command.Parameters.AddWithValue("@blocked", MessageTypes.Blocked);
                command.Parameters.AddWithValue("@needsDecision", MessageTypes.NeedsDecision);
                command.Parameters.AddWithValue("@started", MessageTypes.Started);
            },
            ct);

        Message? blocked = null;
        Message? needsDecision = null;

        // ORDERED ASCENDING AND OVERWRITTEN, so each field ends up holding the NEWEST live row of its type.
        foreach (var row in declarations)
        {
            if (row.Type == MessageTypes.Blocked) blocked = row;
            else needsDecision = row;
        }

        var failed = (await ReadMessagesAsync(
            $"""
            SELECT {MessageRows.Columns}
            FROM messages
            WHERE seq = (
                SELECT MAX(seq) FROM messages
                WHERE source = @source COLLATE {SqlServerSql.NoCase}
                  AND seq > @since
                  AND type IN (@completed, @failed))
              AND type = @failed
            """,
            command =>
            {
                command.Parameters.AddWithValue("@source", source);
                command.Parameters.AddWithValue("@since", sinceSeq);
                command.Parameters.AddWithValue("@completed", MessageTypes.Completed);
                command.Parameters.AddWithValue("@failed", MessageTypes.Failed);
            },
            ct)).FirstOrDefault();

        return blocked is null && needsDecision is null && failed is null
            ? ContainerMarks.None
            : new ContainerMarks(blocked, needsDecision, failed);
    }

    public async Task<long> LastSeqBeforeAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        var highest = await HighestSeqAsync(ct);

        return await RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT TOP 1 seq, occurred_at FROM messages WHERE seq >= @seq ORDER BY seq";
                var seqParameter = command.Parameters.AddWithValue("@seq", 0L);

                async Task<(long, string)?> FirstAtOrAfterAsync(long seq)
                {
                    seqParameter.Value = seq;
                    await using var reader = await command.ExecuteReaderAsync(token);
                    return await reader.ReadAsync(token) ? (reader.GetInt64(0), reader.GetString(1)) : null;
                }

                return await LogPosition.LastSeqBeforeAsync(FirstAtOrAfterAsync, highest, at);
            },
            ct);
    }

    public async Task<Message?> FindAsync(long seq, CancellationToken ct = default) =>
        (await ReadMessagesAsync(
            $"SELECT {MessageRows.Columns} FROM messages WHERE seq = @seq",
            command => command.Parameters.AddWithValue("@seq", seq),
            ct)).SingleOrDefault();

    public Task<long> TeamHighestSeqAsync(string team, CancellationToken ct = default) =>
        ScalarAsync(
            $"""
            SELECT COALESCE(MAX(seq), 0) FROM messages
            WHERE team = @team
               OR (team IS NULL AND (type LIKE @instruction {SqlServerSql.Like}
                                     OR JSON_VALUE(payload, '$.team') = @team COLLATE {SqlServerSql.NoCase}))
            """,
            command =>
            {
                command.Parameters.AddWithValue("@team", team);
                command.Parameters.AddWithValue("@instruction", MessageTypes.InstructionPrefix + team + "/%");
            },
            ct);

    public Task<long> HighestSeqAsync(CancellationToken ct = default) =>
        ScalarAsync("SELECT COALESCE(MAX(seq), 0) FROM messages", _ => { }, ct);

    private Task<long> ScalarAsync(string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind(command);
                return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            },
            ct);

    public Task<IReadOnlyList<Message>> ReadCorrelationAsync(long correlationId, CancellationToken ct = default) =>
        ReadMessagesAsync(
            $"SELECT {MessageRows.Columns} FROM messages WHERE correlation_id = @id ORDER BY seq",
            command => command.Parameters.AddWithValue("@id", correlationId),
            ct);

    public Task<WorkflowSpend> GetSpendSinceNudgeAsync(long correlationId, CancellationToken ct = default) =>
        RunAsync((connection, token) => SpendAsync(connection, correlationId, sinceLastNudge: true, token), ct);

    public Task<WorkflowSpend> GetWorkflowSpendAsync(long correlationId, CancellationToken ct = default) =>
        RunAsync((connection, token) => SpendAsync(connection, correlationId, sinceLastNudge: false, token), ct);

    /// <summary>Billable tokens, measured runs and unmeasured runs over <c>usage_ledger</c>: see
    /// SqliteMessageStore.LedgerSpend.</summary>
    private const string LedgerSpend =
        """
        CAST(COALESCE(SUM(billable), 0) AS BIGINT), CAST(COALESCE(SUM(measured), 0) AS BIGINT), CAST(COALESCE(SUM(1 - measured), 0) AS BIGINT)
        """;

    /// <summary>
    /// ON THE LEDGER, with the nudge window the ledger's too, and the log's only for a nudge from
    /// before the ledger kept them - SqliteMessageStore.SpendAsync.
    /// </summary>
    private static async Task<WorkflowSpend> SpendAsync(
        SqlConnection connection, long correlationId, bool sinceLastNudge, CancellationToken ct)
    {
        long since = 0;

        if (sinceLastNudge)
        {
            await using var window = connection.CreateCommand();
            window.CommandText =
                $"""
                SELECT GREATEST(
                    (SELECT COALESCE(MAX(nudge_seq), 0) FROM nudge_ledger WHERE correlation = @id),
                    (SELECT COALESCE(MAX(seq), 0) FROM messages
                     WHERE correlation_id = @id
                       AND causation_seq = @id
                       AND type LIKE N'agentContainer.instruction.%' {SqlServerSql.Like}))
                """;
            window.Parameters.AddWithValue("@id", correlationId);

            since = Convert.ToInt64(await window.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {LedgerSpend}
            FROM usage_ledger
            WHERE correlation = @id
              AND run_seq > @since
            """;
        command.Parameters.AddWithValue("@id", correlationId);
        command.Parameters.AddWithValue("@since", since);

        return await ReadSpendAsync(command, ct);
    }

    private static async Task<WorkflowSpend> ReadSpendAsync(SqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct)
            ? new WorkflowSpend(
                reader.GetInt64(0),
                checked((int)reader.GetInt64(1)),
                checked((int)reader.GetInt64(2)))
            : new WorkflowSpend(0, 0, 0);
    }

    public async Task<WorkflowSpend> GetTriggerSpendAsync(
        IReadOnlyCollection<string> sources, DateTimeOffset since, CancellationToken ct = default)
    {
        if (sources.Count == 0) return new WorkflowSpend(0, 0, 0);

        return await RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    SELECT {LedgerSpend}
                    FROM usage_ledger
                    WHERE trigger_source IN ({InList(command, "source", sources)})
                      AND trigger_fired_at >= @since
                    """;
                command.Parameters.AddWithValue("@since", Instant.Stamp(since.ToUniversalTime()));

                return await ReadSpendAsync(command, token);
            },
            ct);
    }

    public async Task<IReadOnlyList<Message>> ReadRangeAsync(
        long afterSeq, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        return await ReadMessagesAsync(
            $"SELECT TOP (@max) {MessageRows.Columns} FROM messages WHERE seq > @after ORDER BY seq",
            command =>
            {
                command.Parameters.AddWithValue("@after", afterSeq);
                command.Parameters.AddWithValue("@max", max);
            },
            ct);
    }

    /// <summary>
    /// SqliteMessageStore.DeleteAsync: one statement repeated to a fixpoint, never deleting a row
    /// that something still cites, then the file changes and agent steps older than the oldest row
    /// kept. The ids go as one JSON parameter, as for <see cref="OpenWorkflowsAmongAsync"/>.
    /// </summary>
    public async Task<PurgeReport> DeleteAsync(
        IReadOnlyCollection<long> seqs, CancellationToken ct = default)
    {
        if (seqs.Count == 0) return new PurgeReport(0, 0);

        var ids = JsonSerializer.Serialize(seqs);

        return await RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);

                // UNDER THE APPEND LOCK, as SQLite's one writer is: an append citing a row this
                // decides no one cites would otherwise commit beside the delete, and the cause's
                // foreign key would fail one of the two instead of the row being retained.
                await TakeAppendLockAsync(connection, transaction, token);

                var purged = 0;

                while (true)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        DELETE FROM messages
                        WHERE seq IN (SELECT CAST(value AS BIGINT) FROM OPENJSON(@ids))
                          AND seq NOT IN (
                              SELECT causation_seq FROM messages WHERE causation_seq IS NOT NULL)
                          AND seq NOT IN (SELECT seq FROM pending_deliveries)
                          AND seq NOT IN (SELECT asked_seq FROM wakes)
                        """;
                    command.Parameters.Add("@ids", System.Data.SqlDbType.NVarChar, -1).Value = ids;

                    var removed = await command.ExecuteNonQueryAsync(token);
                    if (removed == 0) break;
                    purged += removed;
                }

                int fileChanges;
                int runSteps;

                await using (var prune = connection.CreateCommand())
                {
                    prune.Transaction = transaction;
                    prune.CommandText = "SELECT TOP 1 occurred_at FROM messages ORDER BY seq";
                    var oldest = await prune.ExecuteScalarAsync(token) is string stamp ? MessageRows.ReadStamp(stamp) : (DateTimeOffset?)null;

                    prune.CommandText = oldest is null ? "DELETE FROM file_changes" : "DELETE FROM file_changes WHERE at < @oldest";
                    if (oldest is { } at) prune.Parameters.AddWithValue("@oldest", MessageSchema.FileChangeInstant(at));
                    fileChanges = await prune.ExecuteNonQueryAsync(token);

                    prune.CommandText = oldest is null ? "DELETE FROM run_steps" : "DELETE FROM run_steps WHERE at < @oldest";
                    runSteps = await prune.ExecuteNonQueryAsync(token);
                }

                await SqlServerOpen.CommitAsync(transaction, token);

                return new PurgeReport(purged, seqs.Count - purged, fileChanges, runSteps);
            },
            ct);
    }

    public Task<long> PositionAsync(ContainerId subscriber, CancellationToken ct = default) =>
        ScalarAsync(
            "SELECT COALESCE((SELECT position FROM cursors WHERE subscriber = @s), 0)",
            command => command.Parameters.AddWithValue("@s", subscriber.ToString()),
            ct);

    /// <summary>
    /// ONE MERGE UNDER HOLDLOCK, so two advances of a new cursor at once cannot both insert, and the
    /// larger position wins: monotonic, as SQLite's `MAX(position, excluded.position)` upsert.
    /// </summary>
    public Task AdvanceAsync(ContainerId subscriber, long seq, CancellationToken ct = default) =>
        ExecuteAsync(
            """
            MERGE cursors WITH (HOLDLOCK) AS c
            USING (SELECT @s AS subscriber, @p AS position) AS n ON c.subscriber = n.subscriber
            WHEN MATCHED AND n.position > c.position THEN UPDATE SET position = n.position
            WHEN NOT MATCHED THEN INSERT (subscriber, position) VALUES (n.subscriber, n.position);
            """,
            command =>
            {
                command.Parameters.AddWithValue("@s", subscriber.ToString());
                command.Parameters.AddWithValue("@p", seq);
            },
            ct);

    public Task ForgetAsync(ContainerId subscriber, CancellationToken ct = default) =>
        ExecuteAsync(
            "DELETE FROM cursors WHERE subscriber = @s",
            command => command.Parameters.AddWithValue("@s", subscriber.ToString()),
            ct);

    public Task<int> ForgetTeamAsync(string team, CancellationToken ct = default) =>
        ExecuteAsync(TeamScoped("cursors"), command => command.Parameters.AddWithValue("@team", team), ct);

    /// <summary>
    /// The team half of a <c>Team/Name</c> subscriber key, matched without case. The slash guard is
    /// SQLite's: a subscriber that is not qualified never matches an empty team name.
    /// </summary>
    private static string TeamScoped(string table) =>
        $"""
        DELETE FROM {table}
        WHERE CHARINDEX(N'/', subscriber) > 0
          AND SUBSTRING(subscriber, 1, CHARINDEX(N'/', subscriber) - 1) = @team COLLATE {SqlServerSql.NoCase}
        """;

    public async Task<long?> SlowestAsync(CancellationToken ct = default) =>
        await RunAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT MIN(position) FROM cursors";
                return await command.ExecuteScalarAsync(token) is long slowest ? slowest : (long?)null;
            },
            ct);

    /// <summary>
    /// Replaced wholesale, in one transaction. The delete takes HOLDLOCK on this subscriber's key
    /// range, so a second set for the same subscriber waits for this one rather than interleaving
    /// with it, as SQLite's single writer makes it.
    /// </summary>
    public Task SetAsync(ContainerId subscriber, IReadOnlyCollection<string> types, CancellationToken ct = default) =>
        RunAsync(
            async (connection, token) =>
            {
                await using var transaction = await SqlServerOpen.BeginAsync(connection, token);
                var key = subscriber.ToString();

                await using (var clear = connection.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM subscriptions WITH (HOLDLOCK) WHERE subscriber = @s";
                    clear.Parameters.AddWithValue("@s", key);
                    await clear.ExecuteNonQueryAsync(token);
                }

                foreach (var type in types)
                {
                    await using var add = connection.CreateCommand();
                    add.Transaction = transaction;
                    add.CommandText =
                        """
                        INSERT INTO subscriptions (subscriber, type)
                        SELECT @s, @t WHERE NOT EXISTS (SELECT 1 FROM subscriptions WHERE subscriber = @s AND type = @t)
                        """;
                    add.Parameters.AddWithValue("@s", key);
                    add.Parameters.AddWithValue("@t", type);
                    await add.ExecuteNonQueryAsync(token);
                }

                await SqlServerOpen.CommitAsync(transaction, token);
                return true;
            },
            ct);

    public Task<IReadOnlyCollection<string>> ForAsync(ContainerId subscriber, CancellationToken ct = default) =>
        RunAsync<IReadOnlyCollection<string>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT type FROM subscriptions WHERE subscriber = @s ORDER BY type";
                command.Parameters.AddWithValue("@s", subscriber.ToString());

                var values = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) values.Add(reader.GetString(0));
                return values;
            },
            ct);

    public Task<int> ClearTeamAsync(string team, CancellationToken ct = default) =>
        ExecuteAsync(TeamScoped("subscriptions"), command => command.Parameters.AddWithValue("@team", team), ct);

    public Task<IReadOnlyCollection<ContainerId>> SubscribersAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyCollection<ContainerId>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT DISTINCT subscriber FROM subscriptions ORDER BY subscriber";

                // Parsing rather than tolerating, as SQLite's: a bare name here should fail loudly.
                var subscribers = new List<ContainerId>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) subscribers.Add(ContainerId.Parse(reader.GetString(0)));
                return subscribers;
            },
            ct);

    private Task<int> ExecuteAsync(string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        SqlServerOpen.ExecuteAsync(_connectionString, sql, bind, ct);
}
