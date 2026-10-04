using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// The log, the cursors and the subscriptions over one SQLite file.
///
/// All three live in one class because they must share a transaction. Delivering a message to a
/// container means advancing that container's cursor and enqueueing its work, and those have to
/// happen together or a crash between them either loses the wake or repeats it. One process and one
/// database (the single-process constraint) is what makes that a plain local transaction rather than
/// a distributed-commit problem — the whole at-least-once versus at-most-once dilemma simply does
/// not arise here, and it would the moment the log and the queue became separate systems.
///
/// Connections are opened per operation and pooled by the driver. That is deliberate over holding
/// one: a single shared connection would need its own lock, and SQLite already serialises writers.
///
/// Constructing this does NOT create the schema. SchemaMigrator does, once, before any store is
/// built - see MessageSchema.Steps. A constructor cannot, because a step that must run once cannot
/// live in something that runs four times.
/// </summary>
public sealed class SqliteMessageStore : IMessageLog, ICursors, ISubscriptions
{
    private readonly string _connectionString;
    private readonly MessageWaitRegistry? _waits;

    public SqliteMessageStore(string databasePath, MessageWaitRegistry? waits = null)
    {
        _waits = waits;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,

            // POOLING OFF, FOR THE REASON `SqliteUserStore` ALREADY GIVES: a pooled connection
            // keeps its NATIVE handle - and the file - alive for the lifetime of the POOL rather
            // than the connection, and the pool is PROCESS-WIDE.
            //
            // That makes the handle something any other code in this process can dispose. It is
            // not hypothetical: `SqliteConnection.ClearAllPools()` is process-wide and appears in
            // 55 test teardowns, so one finishing test drops the handle a running one is using,
            // which surfaces as `ObjectDisposedException: SQLitePCL.sqlite3` in whichever database
            // call happened to be in flight - on a different test every run, and never in a fully
            // serial run.
            //
            // Every `Open()` sets its own pragmas because they are per-connection, so
            // nothing here depended on reusing one.
            Pooling = false,
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // Round-trip format, invariant culture. "yyyy" renders through the current culture's CALENDAR --
    // th-TH is Buddhist, so a 2026 timestamp would persist as 2569 and sort against everything else
    // written on a machine with a different locale. Reading back is MessageRows' half of the same
    // rule.
    private static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// THE LAST LINE OF DEFENCE FOR A WRITE SITE NOBODY HAS FOUND YET - userinfo out of the payload,
    /// at the store, so no call site can forget.
    ///
    /// <para>
    /// <b>WHY IT IS HERE AND NOT ONLY AT THE CALL SITES.</b> <c>SqliteDiagnosticsLog</c> already makes
    /// this argument for the diagnostics store - "the store applies it, so no call site can forget" -
    /// and this store is the stricter of the two: it is APPEND-ONLY, it is read back into other
    /// agents' prompts, and a row on it cannot be deleted. A credential can reach it quoted into a
    /// payload by an agent or composed by the platform itself at team creation
    /// (<c>RepoSetupMessage.Line</c>); guarding only call sites leaves the next site unguarded.
    /// </para>
    ///
    /// <para>
    /// <b>USERINFO ONLY, AND THE FULL REDACTOR IS DELIBERATELY NOT USED.</b>
    /// <c>DiagnosticRedaction</c> truncates at 4,000 characters and
    /// <c>GitOutputRedaction.Redact</c> bounds to 8 lines. An addressed instruction is a whole prompt
    /// and routinely exceeds both, so applying either here would silently cut the product's own
    /// messages in half to close a leak - a guard that breaks the thing it guards is a guard somebody
    /// removes. <c>RedactUserInfo</c> changes a payload's length only when it actually carried a
    /// credential, which is what makes it safe to run on every append.
    /// </para>
    ///
    /// <para>
    /// <b>IT IS A NET AND NOT A LICENCE.</b> The first rule is still that a call site does not compose
    /// a credential into a row - <c>RepoSetupMessage.Line</c> and <c>TeamPublisher.RowFor</c> both
    /// redact where the row is composed, and they must go on doing so. This catches a URL carrying
    /// userinfo, and nothing else.
    /// </para>
    ///
    /// <para>
    /// <c>Type</c> and <c>Source</c> are not redacted: a type is a constant from <c>MessageTypes</c>
    /// and a source is a <c>ContainerId</c>, neither of which any code path composes from a URL.
    /// </para>
    /// </summary>
    public async Task<Message> AppendAsync(NewMessage message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Type);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Source);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var stored = await InsertAsync(connection, transaction, message, ct);

        await transaction.CommitAsync(ct);

        _waits?.Publish(stored);

        return stored;
    }

    public async Task<Message?> AppendWithinAsync(
        Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, Task<NewMessage?>> before,
        Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, Message, CancellationToken, Task> after,
        CancellationToken ct = default)
    {
        await using var connection = Open();

        // Not deferred (the driver's default, as for every transaction here): the caller's first
        // statement is often a read-modify-write that decides whether anything is appended, and two
        // of those must not both decide from the same state.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        Message? stored = null;
        if (await before(connection, transaction, ct) is { } message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message.Type);
            ArgumentException.ThrowIfNullOrWhiteSpace(message.Source);

            stored = await InsertAsync(connection, transaction, message, ct);
            await after(connection, transaction, stored, ct);
        }

        await transaction.CommitAsync(ct);

        // Published only once committed: a live subscriber must never be handed a row that rolled back.
        if (stored is not null) _waits?.Publish(stored);

        return stored;
    }

    /// <summary>The row itself, inside the caller's transaction. Nothing is published here.</summary>
    private static async Task<Message> InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, NewMessage message, CancellationToken ct)
    {
        // `?? message.Payload` only ever fires on a null the compiler cannot rule out: `RedactUserInfo`
        // returns its argument unchanged when there is nothing to redact.
        var payload = GitOutputRedaction.RedactUserInfo(message.Payload) ?? message.Payload;

        long correlation = 0;
        var depth = 0;

        // A cause is resolved INSIDE the transaction, so a message cannot inherit from a parent that
        // is being written concurrently and might not commit.
        if (message.CausationSeq is { } cause)
        {
            await using var parent = connection.CreateCommand();
            parent.Transaction = transaction;
            parent.CommandText = "SELECT correlation_id, depth FROM messages WHERE seq = $seq";
            parent.Parameters.AddWithValue("$seq", cause);

            await using var reader = await parent.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
            {
                // Refused rather than silently treated as a root. A dangling cause would report
                // depth 0 for a message arbitrarily deep in a chain, which is precisely the value
                // the loop bound depends on.
                throw new InvalidOperationException(
                    $"Message {cause} does not exist, so nothing can be caused by it.");
            }

            correlation = reader.GetInt64(0);
            depth = reader.GetInt32(1) + 1;
        }

        var occurredAt = DateTimeOffset.UtcNow;

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO messages (type, payload, source, correlation_id, causation_seq, depth, occurred_at)
            VALUES ($type, $payload, $source, $correlation, $causation, $depth, $at)
            RETURNING seq
            """;

        insert.Parameters.AddWithValue("$type", message.Type);
        insert.Parameters.AddWithValue("$payload", string.IsNullOrEmpty(payload) ? "{}" : payload);
        insert.Parameters.AddWithValue("$source", message.Source);
        insert.Parameters.AddWithValue("$correlation", correlation);
        insert.Parameters.AddWithValue("$causation", (object?)message.CausationSeq ?? DBNull.Value);
        insert.Parameters.AddWithValue("$depth", depth);
        insert.Parameters.AddWithValue("$at", Stamp(occurredAt));

        var seq = (long)(await insert.ExecuteScalarAsync(ct))!;

        // A root correlates to ITSELF, which is only knowable once the row has its seq. Same
        // transaction, so no reader can observe the placeholder zero.
        if (message.CausationSeq is null)
        {
            correlation = seq;

            await using var settle = connection.CreateCommand();
            settle.Transaction = transaction;
            settle.CommandText = "UPDATE messages SET correlation_id = $seq WHERE seq = $seq";
            settle.Parameters.AddWithValue("$seq", seq);
            await settle.ExecuteNonQueryAsync(ct);
        }

        // THE REDACTED PAYLOAD, NOT THE ONE HANDED IN. This is what the row holds and what every
        // later reader gets, and `_waits.Publish` hands it straight to a live subscriber - returning
        // the original here would put the credential back into the one delivery that skips the read.
        var stored = new Message(
            seq, message.Type, payload, message.Source,
            correlation, message.CausationSeq, depth, occurredAt);

        // THE ACCOUNTING, IN THIS SAME TRANSACTION. A run's terminal row and a workflow's completion
        // or close write their ledger row here, the one place every row is appended, so no caller
        // can skip it; when it cannot be written this throws and the row is not stored either.
        await LedgerRows.WriteAsync(connection, transaction, stored, backfilled: false, ct);

        // THE OUTCOME, AT THE ROOT, IN THIS SAME TRANSACTION: a dispatch links to its item's outcome
        // and a trigger's fire to its trigger's. See OutcomeLinks.
        await OutcomeLinks.OnAppendAsync(connection, transaction, stored, ct);

        return stored;
    }

    public async Task<IReadOnlyList<Message>> ReadAfterAsync(
        long afterSeq, IReadOnlyCollection<string> types, int max, CancellationToken ct = default)
    {
        // No types means no interest. Returning everything would be the more "helpful" reading and
        // is exactly wrong: a container that has not declared a subscription must receive nothing,
        // not the entire log.
        if (types.Count == 0 || max <= 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // Parameterised IN list built from the count, never interpolated values -- a type name
        // arrives from configuration and must not be able to reach the SQL text.
        var names = types.Select((_, i) => $"$t{i}").ToArray();

        command.CommandText =
            $"""
             SELECT seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at
             FROM messages
             WHERE seq > $after AND type IN ({string.Join(", ", names)})
             ORDER BY seq
             LIMIT $max
             """;

        command.Parameters.AddWithValue("$after", afterSeq);
        command.Parameters.AddWithValue("$max", max);

        var i = 0;
        foreach (var type in types) command.Parameters.AddWithValue(names[i++], type);

        return await MessageRows.ReadAllAsync(command, ct);
    }

    public async Task<IReadOnlyList<Message>> ReadLatestAsync(
        long afterSeq, IReadOnlyCollection<string> types, int max, CancellationToken ct = default)
    {
        if (types.Count == 0 || max <= 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        var names = types.Select((_, i) => $"$t{i}").ToArray();

        // DESC to take the newest rows, then reversed below so the caller still gets oldest first.
        // Ordering in the query and reversing a bounded list in memory, rather than a subselect:
        // the list is at most `max` long and ix_messages_seq_type serves either direction, so the
        // clever version buys nothing and reads worse.
        //
        // PLUS an addressed instruction, MATCHED BY PREFIX rather than named in `types`.
        //
        // `container.instruction.*` is per-container - one distinct type per live container - so it
        // cannot be a member of a finite catalog set the way every other type here is; that is why
        // `EventCatalog` exempts the prefix rather than enumerating it. The feed's types come from the
        // catalog alone, so without this arm an addressed instruction would have no path into the
        // feed at all: not a narrowing of an edge case, but every `tell` invisible on
        // `/api/messages`, which the route's own
        // description promises to show ("instructions going in, and what members published coming
        // back"). `WorkflowOpenSql.NotClosed` already matches this same prefix with `LIKE` for the
        // identical reason - an unbounded per-container family that cannot sit in an IN-list.
        //
        // The LIKE costs nothing extra here. `seq` is `INTEGER PRIMARY KEY`, so it IS the rowid, and
        // `ORDER BY seq DESC LIMIT` is already a reverse rowid scan with no index to seek through -
        // there is no plan in which `type` is anything but a residual per-row predicate. The OR does not
        // turn a seek into a scan; it is a scan either way.
        command.CommandText =
            $"""
             SELECT seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at
             FROM messages
             WHERE seq > $after
               AND (type IN ({string.Join(", ", names)}) OR type LIKE $instructionPrefix)
             ORDER BY seq DESC
             LIMIT $max
             """;

        command.Parameters.AddWithValue("$after", afterSeq);
        command.Parameters.AddWithValue("$max", max);
        command.Parameters.AddWithValue("$instructionPrefix", MessageTypes.InstructionPrefix + "%");

        var i = 0;
        foreach (var type in types) command.Parameters.AddWithValue(names[i++], type);

        var newestFirst = await MessageRows.ReadAllAsync(command, ct);

        // A feed reads downwards. Handing back the page reversed would put the newest row at the
        // bottom of a list whose every other consumer expects ascending seq.
        return [.. newestFirst.Reverse()];
    }

    public async Task<IReadOnlyList<Message>> ReadForContainerAsync(
        string qualifiedId, long sinceSeq, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // COLLATE NOCASE is explicit because the column carries no collation of its own and
        // ContainerId equality is case-insensitive everywhere else - a case-varied source is
        // otherwise a silent miss. The filter is IN the query and the cap is applied AFTER it, or a
        // quiet member behind a page of somebody else's rows answers empty forever instead of its
        // own tail.
        //
        // THE FLOOR IS IN THE QUERY FOR THE SAME REASON. Applied afterwards, a container whose
        // predecessor filled the window would answer empty rather than answering its own rows -
        // the identical degrade-to-nothing this method already exists to avoid, one filter along.
        command.CommandText =
            $"""
             SELECT {MessageRows.Columns}
             FROM messages
             WHERE source = $id COLLATE NOCASE
               AND seq > $since
             ORDER BY seq DESC
             LIMIT $max
             """;

        command.Parameters.AddWithValue("$id", qualifiedId);
        command.Parameters.AddWithValue("$since", sinceSeq);
        command.Parameters.AddWithValue("$max", max);

        return await MessageRows.ReadAllAsync(command, ct);
    }

    public async Task<IReadOnlyList<Message>> ReadMemberBeforeAsync(
        ContainerId container, long sinceSeq, long beforeSeq, IReadOnlyCollection<string> types,
        int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        var names = types.Select((_, i) => $"$t{i}").ToArray();

        // The published half is narrowed to `types`; with none, it matches nothing rather than
        // everything, the same "no types means no interest" ReadAfterAsync keeps.
        var published = names.Length == 0
            ? "0"
            : $"(source = $source COLLATE NOCASE AND type IN ({string.Join(", ", names)}))";

        command.CommandText =
            $"""
             SELECT {MessageRows.Columns}
             FROM messages
             WHERE seq > $since AND seq < $before
               AND (type = $instruction OR {published})
             ORDER BY seq DESC
             LIMIT $max
             """;

        command.Parameters.AddWithValue("$since", sinceSeq);
        command.Parameters.AddWithValue("$before", beforeSeq);
        command.Parameters.AddWithValue("$source", container.ToString());
        command.Parameters.AddWithValue("$instruction", MessageTypes.InstructionFor(container));
        command.Parameters.AddWithValue("$max", max);

        var i = 0;
        foreach (var type in types) command.Parameters.AddWithValue(names[i++], type);

        return await MessageRows.ReadAllAsync(command, ct);
    }

    public async Task<IReadOnlyList<RunRow>> ReadRunsAsync(
        ContainerId container, long sinceSeq, long beforeSeq, int max, CancellationToken ct = default) =>
        await ReadRunsAsync(container, sinceSeq, beforeSeq, max, RunsWith.Transcript, ct);

    public async Task<IReadOnlyList<RunRow>> ReadRunsAsync(
        ContainerId container, long sinceSeq, long beforeSeq, int max, RunsWith which, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // The run a terminal row closed began at this member's latest `started` row below it; a
        // `blocked` it published lies between the two. Only the run form counts: a `blocked` naming an
        // `item` blocks one card, not the run. Both are subqueries on the same source so the cap stays
        // after the filter, for ReadMemberBeforeAsync's reason.
        //
        // A run that blocked EVERY item of its batch writes no completed or failed row: each item's
        // `blocked` closed its delivery instead. For RunsWith.AnyRun such a run ends at its last
        // item `blocked` - one with no completed, failed or other `blocked` after it before the
        // member's next `started` - and that row stands as its terminal. RunsWith.Transcript leaves
        // it out: the run recorded no transcript, so there is nothing to open.
        //
        // Such a row is also what an item blocked by a run STILL WORKING looks like: the run's own
        // terminal has not landed yet (E5-c). Only the runtime knows whether the run is over, so a
        // `blocked` terminal with no `started` after it - one in the member's latest run - comes back
        // marked InLatestRun, for the caller to hold back while the member is running.
        var blockedEnd = which == RunsWith.AnyRun
            ? $"""
               OR (t.type = $blocked
                   AND json_extract(t.payload, '$.{PayloadFields.Item}') IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM messages n
                                   WHERE n.source = t.source AND n.seq > t.seq
                                     AND n.type IN ($completed, $failed, $blocked)
                                     AND n.seq < COALESCE((SELECT MIN(y.seq) FROM messages y
                                                           WHERE y.source = t.source AND y.type = $started
                                                             AND y.seq > t.seq), 9223372036854775807)))
               """
            : "";

        command.CommandText =
            $"""
             SELECT {Prefixed("t")},
                    s.occurred_at,
                    t.type = $blocked OR EXISTS (SELECT 1 FROM messages b
                            WHERE b.source = t.source AND b.type = $blocked
                              AND b.seq < t.seq AND b.seq > COALESCE(s.seq, $since)
                              AND json_extract(b.payload, '$.item') IS NULL),
                    t.type = $blocked AND NOT EXISTS (SELECT 1 FROM messages y
                            WHERE y.source = t.source AND y.type = $started AND y.seq > t.seq)
             FROM messages t
             LEFT JOIN messages s ON s.seq = (
                 SELECT MAX(x.seq) FROM messages x
                 WHERE x.source = t.source AND x.type = $started AND x.seq < t.seq AND x.seq > $since)
             WHERE t.seq > $since AND t.seq < $before
               AND t.source = $source COLLATE NOCASE
               AND ((t.type IN ($completed, $failed)
                     AND {(which == RunsWith.Transcript
                         ? $"json_extract(t.payload, '$.{PayloadFields.AgentTranscript}') IS NOT NULL"
                         : $"json_extract(t.payload, '$.{PayloadFields.UsageCountedOn}') IS NULL")})
                    {blockedEnd})
             ORDER BY t.seq DESC
             LIMIT $max
             """;

        command.Parameters.AddWithValue("$since", sinceSeq);
        command.Parameters.AddWithValue("$before", beforeSeq);
        command.Parameters.AddWithValue("$source", container.ToString());
        command.Parameters.AddWithValue("$completed", MessageTypes.Completed);
        command.Parameters.AddWithValue("$failed", MessageTypes.Failed);
        command.Parameters.AddWithValue("$started", MessageTypes.Started);
        command.Parameters.AddWithValue("$blocked", MessageTypes.Blocked);
        command.Parameters.AddWithValue("$max", max);

        var runs = new List<RunRow>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var terminal = new Message(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt32(6),
                MessageRows.ReadStamp(reader.GetString(7)));

            runs.Add(new RunRow(
                terminal,
                reader.IsDBNull(8) ? null : MessageRows.ReadStamp(reader.GetString(8)),
                reader.GetInt64(9) != 0,
                reader.GetInt64(10) != 0));
        }

        return runs;
    }

    private static string Prefixed(string alias) =>
        string.Join(", ", MessageRows.Columns.Split(", ").Select(column => $"{alias}.{column}"));

    /// <summary>
    /// THE `Team/%` / `Team/%/%` `LIKE` PAIR EVERY TEAM-SCOPED QUERY IN THIS FILE MEANS BY "this
    /// team's own rows, not a member's own nested subtree" - built in ONE place so a caller cannot
    /// compute it by hand and drift from the others.
    ///
    /// ESCAPES `_` AND `%` IN THE TEAM NAME FIRST. Both are LEGAL IN A TEAM ID -
    /// `ContainerId.IsLegalName` permits `-` and `_`, and `DeriveName` preserves them - and both are
    /// SQLite `LIKE` WILDCARDS, so an unescaped `_` in a team name matches ANY character at that
    /// position: `'Alpha/Manager' LIKE 'A_pha/%'` is TRUE, so a caller bound only to `A_pha` would
    /// read `Alpha`'s rows as its own. Reasoning only about `/` (true: neither half of a
    /// `ContainerId` may contain one, so a shorter team name cannot swallow a longer one) misses
    /// `_` - which is exactly the kind of collision a same-length team name produces. Every call site pairs the returned patterns
    /// with `ESCAPE '\'` on both the `LIKE` and the `NOT LIKE` they appear in.
    /// </summary>
    private static (string Prefix, string Nested) TeamSourcePattern(string team)
    {
        var escaped = team
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return (escaped + "/%", escaped + "/%/%");
    }

    public async Task<IReadOnlyList<ContainerUsageRow>> SumUsageForTeamAsync(
        string team, long sinceSeq, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(team)) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        var (prefix, nested) = TeamSourcePattern(team);

        // COLLATE NOCASE because ContainerId equality is case-insensitive and the column has no
        // collation of its own. `ESCAPE '\'` pairs with `TeamSourcePattern`'s escaping of `_` and
        // `%` in the team name - see that method's own doc comment for why. json_extract returns
        // NULL when the key is absent; SUM skips NULL, so rows without usage keys do not become
        // zeros.
        command.CommandText =
            $"""
            SELECT
                source,
                COALESCE(SUM(CASE
                    WHEN json_extract(payload, '$.tokensIn') IS NOT NULL
                     AND json_extract(payload, '$.tokensOut') IS NOT NULL
                     AND COALESCE(json_extract(payload, '$.tokensSource'), '') <> $excluded
                    THEN CAST(json_extract(payload, '$.tokensIn') AS INTEGER) END), 0),
                COALESCE(SUM(CASE
                    WHEN json_extract(payload, '$.tokensIn') IS NOT NULL
                     AND json_extract(payload, '$.tokensOut') IS NOT NULL
                     AND COALESCE(json_extract(payload, '$.tokensSource'), '') <> $excluded
                    THEN CAST(json_extract(payload, '$.tokensOut') AS INTEGER) END), 0),
                SUM(CASE
                    WHEN json_extract(payload, '$.tokensIn') IS NOT NULL
                     AND json_extract(payload, '$.tokensOut') IS NOT NULL
                     AND COALESCE(json_extract(payload, '$.tokensSource'), '') <> $excluded
                    THEN 1 ELSE 0 END),
                SUM(CASE
                    WHEN json_extract(payload, '$.tokensIn') IS NULL
                      OR json_extract(payload, '$.tokensOut') IS NULL
                      OR COALESCE(json_extract(payload, '$.tokensSource'), '') = $excluded
                    THEN 1 ELSE 0 END),
                -- THE COMBINED FIGURE, SUMMED SEPARATELY AND NEVER FOLDED INTO THE TWO ABOVE. A
                -- brand that reports one number for a run (codex) cannot be added to an in total or
                -- an out total without deciding which way its tokens went, and nothing knows that.
                -- Those runs therefore stay in the without-usage count: the team's in/out total is
                -- honest about not including them, and this column is what lets a member row say
                -- what the run actually cost.
                SUM(CASE
                    WHEN json_extract(payload, '$.tokensTotal') IS NOT NULL
                     AND COALESCE(json_extract(payload, '$.tokensSource'), '') <> $excluded
                    THEN CAST(json_extract(payload, '$.tokensTotal') AS INTEGER) END),
                -- Cache reads and writes over the same rows as tokensIn/tokensOut. Beside tokensIn,
                -- never added into it.
                COALESCE(SUM(CASE
                    WHEN json_extract(payload, '$.tokensIn') IS NOT NULL
                     AND json_extract(payload, '$.tokensOut') IS NOT NULL
                     AND COALESCE(json_extract(payload, '$.tokensSource'), '') <> $excluded
                    THEN CAST(json_extract(payload, '$.tokensCachedIn') AS INTEGER) END), 0),
                COALESCE(SUM(CASE
                    WHEN json_extract(payload, '$.tokensIn') IS NOT NULL
                     AND json_extract(payload, '$.tokensOut') IS NOT NULL
                     AND COALESCE(json_extract(payload, '$.tokensSource'), '') <> $excluded
                    THEN CAST(json_extract(payload, '$.tokensCacheCreation') AS INTEGER) END), 0),
                {LogSpend.BillableSum}
            FROM messages
            WHERE type IN ($completed, $failed)
              AND json_extract(payload, '$.usageCountedOn') IS NULL
              AND source LIKE $prefix COLLATE NOCASE ESCAPE '\'
              AND source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
              AND seq > $since
            GROUP BY source COLLATE NOCASE
            """;

        command.Parameters.AddWithValue("$completed", MessageTypes.Completed);
        command.Parameters.AddWithValue("$failed", MessageTypes.Failed);
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$nested", nested);
        command.Parameters.AddWithValue("$excluded", UsageSource.ExcludedEstimate);
        command.Parameters.AddWithValue("$since", sinceSeq);

        var rows = new List<ContainerUsageRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ContainerUsageRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                checked((int)reader.GetInt64(3)),
                checked((int)reader.GetInt64(4)),
                // NULL WHERE NO ROW CARRIED ONE, never 0. SUM over no matching rows is SQL NULL,
                // which is exactly the distinction wanted here: a member that reported no combined
                // figure has not reported a combined figure of nothing.
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8)));
        }

        return rows;
    }

    /// <summary>
    /// The workflow projection behind the board's Workflow tile. See
    /// <see cref="IMessageLog.ElapsedForTeamAsync"/> for why the floor is a required parameter.
    /// </summary>
    public async Task<TeamWorkflowTiming> ElapsedForTeamAsync(
        string team, long sinceSeq, CancellationToken ct = default)
    {
        // READ ONCE, AT THE TOP, and carried on the payload. The client takes the offset between
        // this and its own clock and applies it to every tick, so a browser whose clock is ten
        // minutes fast does not render a workflow that started in the future.
        var serverNow = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(team))
        {
            return Unavailable(serverNow, "No team was named.");
        }

        await using var connection = Open();

        // See TeamSourcePattern's own doc comment for why this is built there and not by hand.
        var (prefix, nested) = TeamSourcePattern(team);

        long correlation;

        await using (var pick = connection.CreateCommand())
        {
            // THE MOST RECENTLY ACTIVE WORKFLOW THIS TEAM'S OWN ROWS BELONG TO. Which is the
            // CURRENT one while a member is running and the LAST one while the team is idle - the
            // same value either way, which is why the running case needs no separate query.
            //
            // The floor is what stops it being a PREDECESSOR'S workflow. A team deleted and
            // recreated under the same names shares its members' qualified ids with whatever came
            // before, so without `seq > $since` a brand-new team reports the previous life's
            // duration on the day it is created.
            //
            // Grouped by correlation_id and ordered by maximum occurred_at, so this picks the
            // workflow that was ACTIVE most recently, not merely the one that STARTED most recently.
            pick.CommandText =
                """
                SELECT COALESCE(MAX(correlation_id), 0)
                FROM (
                  SELECT correlation_id, MAX(occurred_at) as last_activity
                  FROM messages
                  WHERE source LIKE $prefix COLLATE NOCASE ESCAPE '\'
                    AND source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
                    AND seq > $since
                  GROUP BY correlation_id
                  ORDER BY last_activity DESC
                  LIMIT 1
                )
                """;

            pick.Parameters.AddWithValue("$prefix", prefix);
            pick.Parameters.AddWithValue("$nested", nested);
            pick.Parameters.AddWithValue("$since", sinceSeq);

            correlation = Convert.ToInt64(await pick.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        if (correlation == 0)
        {
            return Unavailable(
                serverNow,
                "This team has published nothing since its members were created");
        }

        return await TimingForAsync(connection, team, correlation, sinceSeq, serverNow, ct);
    }

    /// <summary>One named workflow. See <see cref="IMessageLog.WorkflowForTeamAsync"/>.</summary>
    public async Task<TeamWorkflowTiming> WorkflowForTeamAsync(
        string team, long correlation, long sinceSeq, CancellationToken ct = default)
    {
        var serverNow = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(team))
        {
            return Unavailable(serverNow, "No team was named.");
        }

        await using var connection = Open();
        return await TimingForAsync(connection, team, correlation, sinceSeq, serverNow, ct);
    }

    /// <summary>
    /// ONE WORKFLOW, FULLY DESCRIBED, once the correlation is chosen. Every query inside it is
    /// scoped to that correlation; the CHOICE is the caller's.
    ///
    /// SIX SMALL QUERIES RATHER THAN ONE CLEVER ONE, all bounded by the single correlation the
    /// caller chose. The alternative is a window function over the whole table whose failure mode
    /// is a plan nobody reads; these each answer one question the record asks and each is readable
    /// next to the assertion that pins it.
    ///
    /// It takes an OPEN CONNECTION because the plural caller runs it several times and opening a
    /// connection per workflow would turn a tile into N round trips against the same file.
    /// </summary>
    private async Task<TeamWorkflowTiming> TimingForAsync(
        SqliteConnection connection,
        string team,
        long correlation,
        long sinceSeq,
        DateTimeOffset serverNow,
        CancellationToken ct)
    {
        // See TeamSourcePattern's own doc comment for why this is built there and not by hand.
        var (prefix, nested) = TeamSourcePattern(team);

        DateTimeOffset? startedAt = null;
        DateTimeOffset? lastActivityAt = null;

        await using (var bounds = connection.CreateCommand())
        {
            // EVERY ROW IN THE CORRELATION, not only the team's own. A workflow's ROOT is usually
            // an instruction published by a person or by another container, so a query filtered to
            // `Team/%` would measure elapsed from the member's first reply instead of from the
            // moment the work was asked for.
            //
            // MIN/MAX over the stored text is an ordering over ISO-8601 round-trip stamps, all
            // written UTC by Stamp() above, so it is the same order as the instants they denote.
            bounds.CommandText =
                """
                SELECT MIN(occurred_at), MAX(occurred_at)
                FROM messages
                WHERE correlation_id = $correlation AND seq > $since
                """;

            bounds.Parameters.AddWithValue("$correlation", correlation);
            bounds.Parameters.AddWithValue("$since", sinceSeq);

            await using var reader = await bounds.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
            {
                startedAt = MessageRows.ReadStamp(reader.GetString(0));
                lastActivityAt = MessageRows.ReadStamp(reader.GetString(1));
            }
        }

        // THE ROOT, FETCHED BY ITS OWN SEQ - "a root correlates to ITSELF" is `AppendAsync`'s own
        // settling rule above, so `seq = $correlation` IS the root row, unconditionally and without
        // a floor: a subject is a label, not an authority, and the row it names is what began this very
        // workflow whichever side of `sinceSeq` it happens to sit on.
        string? subject = null;

        await using (var root = connection.CreateCommand())
        {
            root.CommandText = "SELECT type, payload FROM messages WHERE seq = $correlation";
            root.Parameters.AddWithValue("$correlation", correlation);

            await using var reader = await root.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(0);

                // NULL FOR ANYTHING THAT IS NOT AN ADDRESSED INSTRUCTION. A workflow can be rooted
                // in something else entirely - `TriggerSweep`'s scheduled wakes, the repositories
                // instruction in `Teams.cs` - and none of those carry a subject to report.
                if (type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal))
                {
                    subject = SubjectFrom(reader.IsDBNull(1) ? null : reader.GetString(1));
                }
            }
        }

        var completed = false;
        var closed = false;
        DateTimeOffset? closedAt = null;

        await using (var declared = connection.CreateCommand())
        {
            // BOTH TERMINAL TYPES, AND WHICH ONE IT WAS - not merely whether there was one.
            // `WorkflowOpenSql` already treats `workflow.closed` as terminal, so a closed workflow
            // drops out of the OPEN count; asking here for `workflow.completed` alone left the
            // singular projection with no terminal fact at all, and it fell through to
            // `Undeclared` with a null `EndedAt` and stayed there. A person clicked Close, got a
            // 204, and nothing they could see changed.
            //
            // SELECTING THE TYPE rather than counting is what keeps the two DISTINGUISHABLE, which
            // the ladder below needs: `Closed` is not `Completed`.
            //
            // Also get the timestamp of the last close, so we can check if there's
            // activity after it. A closed workflow that receives new activity stops being closed.
            declared.CommandText =
                """
                SELECT DISTINCT type, 
                  (SELECT occurred_at FROM messages 
                   WHERE correlation_id = $correlation AND seq > $since 
                   AND type = $closed 
                   ORDER BY seq DESC LIMIT 1) as closed_at
                FROM messages
                WHERE correlation_id = $correlation AND seq > $since
                  AND type IN ($completed, $closed)
                """;

            declared.Parameters.AddWithValue("$correlation", correlation);
            declared.Parameters.AddWithValue("$since", sinceSeq);
            declared.Parameters.AddWithValue("$completed", MessageTypes.WorkflowCompleted);
            declared.Parameters.AddWithValue("$closed", MessageTypes.WorkflowClosed);

            await using var reader = await declared.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(
                        reader.GetString(0), MessageTypes.WorkflowCompleted, StringComparison.Ordinal))
                {
                    completed = true;
                }
                else
                {
                    if (!reader.IsDBNull(1))
                    {
                        closedAt = MessageRows.ReadStamp(reader.GetString(1));
                    }
                    closed = true;
                }
            }
        }

        // Check if a closed workflow has been reopened by a proper re-opening event
        // (an instruction or kanban card event), not just any later row per WorkflowOpenSql.
        if (closed && closedAt.HasValue)
        {
            await using (var reopen = connection.CreateCommand())
            {
                reopen.CommandText =
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM messages woke
                        WHERE woke.correlation_id = $correlation
                          AND woke.seq > (
                              SELECT seq FROM messages 
                              WHERE correlation_id = $correlation AND seq > $since
                              AND type = $closed 
                              ORDER BY seq DESC LIMIT 1)
                          AND (woke.type LIKE $instructionPrefix OR woke.type LIKE $cardPrefix))
                    """;

                reopen.Parameters.AddWithValue("$correlation", correlation);
                reopen.Parameters.AddWithValue("$since", sinceSeq);
                reopen.Parameters.AddWithValue("$closed", MessageTypes.WorkflowClosed);
                reopen.Parameters.AddWithValue("$instructionPrefix", MessageTypes.InstructionPrefix + "%");
                reopen.Parameters.AddWithValue("$cardPrefix", MessageTypes.KanbanCardPrefix + "%");

                var hasReopen = Convert.ToBoolean(
                    await reopen.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

                if (hasReopen)
                {
                    closed = false;
                }
            }
        }

        // IS THIS ONE WORKFLOW PAUSED, and if so with what figure and in whose words.
        //
        // THE SAME PREDICATE AS `WorkflowPause.IsPaused`, EXPRESSED AS SQL, and it has to stay
        // that way: the pump asks the C# one before writing a pause row and `IdleWorkflowOffer`
        // asks it before offering a wake, so a projection that disagreed would show PAUSED on a
        // workflow the pump is still delivering to, or the reverse. It is SQL here for the reason
        // every other clause in this method is - the alternative is reading a whole correlation
        // into memory on a route that is already six queries.
        //
        // NEWEST-WINS, NOT A COUNT. A workflow can be paused, resumed and paused again; counting
        // would make the second episode invisible once the first release had landed. The
        // `seq >` subquery is the "no resumed row after it" half.
        //
        // `seq > $since` ON BOTH HALVES, matching every other query in this method: a caller
        // reading a window must not be told about a pause outside it - and, more to the point,
        // must not be told about a RESUME outside it either, which would be a pause that looks
        // still in force.
        DateTimeOffset? pausedAt = null;
        string? pausedReason = null;
        long? pausedLimit = null;

        await using (var paused = connection.CreateCommand())
        {
            paused.CommandText =
                """
                SELECT occurred_at, payload
                  FROM messages
                 WHERE correlation_id = $correlation
                   AND seq > $since
                   AND type = $paused
                   AND seq > COALESCE(
                       (SELECT MAX(seq) FROM messages
                         WHERE correlation_id = $correlation
                           AND seq > $since
                           AND type = $resumed), 0)
                 ORDER BY seq DESC
                 LIMIT 1
                """;

            paused.Parameters.AddWithValue("$correlation", correlation);
            paused.Parameters.AddWithValue("$since", sinceSeq);
            paused.Parameters.AddWithValue("$paused", MessageTypes.WorkflowPaused);
            paused.Parameters.AddWithValue("$resumed", MessageTypes.WorkflowResumed);

            await using var reader = await paused.ExecuteReaderAsync(ct);

            if (await reader.ReadAsync(ct))
            {
                pausedAt = MessageRows.ReadStamp(reader.GetString(0));
                (pausedReason, pausedLimit) =
                    PauseDetailFrom(reader.IsDBNull(1) ? null : reader.GetString(1));
            }
        }

        var failedMembers = new List<string>();
        string? blockedBy = null;
        string? awaitingFrom = null;
        var everyTerminalCompleted = true;
        var terminals = 0;

        // Track which members have any non-stopped failures
        var membersWithRealFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var last = connection.CreateCommand())
        {
            // ONE ROW PER MEMBER: its LAST OUTCOME in this workflow. "The last run of one or more
            // members was container.failed, AND NOTHING HAS RUN SINCE" is the whole of the FAILED
            // condition, and the second half of it is exactly what taking the maximum seq per
            // source expresses - a member that failed and has since completed is not failed.
            //
            // ONLY `completed` AND `failed` ARE ASKED HERE. A run ends one way or the other, so
            // those two are ALTERNATIVES and MAX(seq) is right for them. `blocked` and
            // `needs-decision` are not outcomes at all: the agent publishes them MID-RUN and the
            // platform then publishes `completed` when the run exits - `Containers.cs` states it as a
            // design property. Including them here would let a member's own `completed` always
            // outrank its declaration, so `blockedBy` and `awaitingFrom` would be null in every
            // ordinary case and the tile could never reach BLOCKED or AWAITING: it would render
            // UNDECLARED, "the runs succeeded and the work stopped anyway", about a team whose agent
            // had given up and asked for a person.
            //
            // We get the type of the last terminal event, but we also need to check
            // if the member has a Failed message with stoppedByPerson: true. If so, treat as Awaiting.
            last.CommandText =
                """
                SELECT source, type FROM messages
                WHERE seq IN (
                    SELECT MAX(seq) FROM messages
                    WHERE correlation_id = $correlation
                      AND seq > $since
                      AND type IN ($completed, $failed)
                      AND source LIKE $prefix COLLATE NOCASE ESCAPE '\'
                      AND source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
                    GROUP BY source COLLATE NOCASE)
                ORDER BY seq
                """;

            last.Parameters.AddWithValue("$correlation", correlation);
            last.Parameters.AddWithValue("$since", sinceSeq);
            last.Parameters.AddWithValue("$completed", MessageTypes.Completed);
            last.Parameters.AddWithValue("$failed", MessageTypes.Failed);
            last.Parameters.AddWithValue("$prefix", prefix);
            last.Parameters.AddWithValue("$nested", nested);

            await using var reader = await last.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var member = NameOf(reader.GetString(0));
                var type = reader.GetString(1);

                terminals++;

                if (type != MessageTypes.Completed) everyTerminalCompleted = false;

                if (type == MessageTypes.Failed)
                {
                    membersWithRealFailures.Add(member);
                }
            }
        }

        // Check each member that has a Failed to see if it's a real failure
        // or just a stopped-by-person failure
        await using (var failureDetails = connection.CreateCommand())
        {
            // For each member with a failure, get details of their last failed message
            failureDetails.CommandText =
                """
                SELECT m.source, m.payload FROM messages m
                WHERE m.correlation_id = $correlation
                  AND m.seq > $since
                  AND m.type = $failed
                  AND m.source LIKE $prefix COLLATE NOCASE ESCAPE '\'
                  AND m.source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
                  AND m.seq IN (
                    SELECT MAX(seq) FROM messages
                    WHERE correlation_id = $correlation
                      AND seq > $since
                      AND type = $failed
                      AND source = m.source COLLATE NOCASE)
                ORDER BY m.seq
                """;

            failureDetails.Parameters.AddWithValue("$correlation", correlation);
            failureDetails.Parameters.AddWithValue("$since", sinceSeq);
            failureDetails.Parameters.AddWithValue("$failed", MessageTypes.Failed);
            failureDetails.Parameters.AddWithValue("$prefix", prefix);
            failureDetails.Parameters.AddWithValue("$nested", nested);

            await using var reader = await failureDetails.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var member = NameOf(reader.GetString(0));
                var payload = reader.IsDBNull(1) ? null : reader.GetString(1);

                // Check if this failure was stopped by a person
                var stoppedByPerson = false;
                if (payload is not null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        if (doc.RootElement.TryGetProperty(PayloadFields.StoppedByPerson, out var value))
                        {
                            stoppedByPerson = value.GetBoolean();
                        }
                    }
                    catch (Exception)
                    {
                        // If we can't parse the payload, treat it as a normal failure
                    }
                }

                if (stoppedByPerson)
                {
                    // A run stopped by a person is Awaiting, not Failed
                    awaitingFrom ??= member;
                    // Remove from real failures list
                    membersWithRealFailures.Remove(member);
                    // The run was stopped on purpose, so not all runs completed successfully
                    everyTerminalCompleted = false;
                }
            }
        }

        // Add members with real failures to failedMembers
        failedMembers.AddRange(membersWithRealFailures);

        await using (var declarations = connection.CreateCommand())
        {
            // A DECLARATION IS LIVE UNLESS THE MEMBER HAS STARTED AGAIN SINCE IT.
            //
            // MAX(seq) alone cannot ask this. "Is there a later
            // row" is always yes - the same run's own `completed` - so the right test is whether
            // the member has had ANOTHER GO, which is a `container.started` at a higher seq from
            // the same source. A member that gave up, was told again, and finished has not given
            // up; a member that gave up and has not run since still has.
            declarations.CommandText =
                """
                SELECT d.source, d.type FROM messages d
                WHERE d.correlation_id = $correlation
                  AND d.seq > $since
                  AND d.type IN ($blocked, $needsDecision)
                  AND d.source LIKE $prefix COLLATE NOCASE ESCAPE '\'
                  AND d.source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
                  AND NOT EXISTS (
                      SELECT 1 FROM messages later
                      WHERE later.correlation_id = d.correlation_id
                        AND later.seq > d.seq
                        AND later.type = $started
                        AND later.source = d.source COLLATE NOCASE)
                ORDER BY d.seq
                """;

            declarations.Parameters.AddWithValue("$correlation", correlation);
            declarations.Parameters.AddWithValue("$since", sinceSeq);
            declarations.Parameters.AddWithValue("$blocked", MessageTypes.Blocked);
            declarations.Parameters.AddWithValue("$needsDecision", MessageTypes.NeedsDecision);
            declarations.Parameters.AddWithValue("$started", MessageTypes.Started);
            declarations.Parameters.AddWithValue("$prefix", prefix);
            declarations.Parameters.AddWithValue("$nested", nested);

            await using var reader = await declarations.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var member = NameOf(reader.GetString(0));
                var type = reader.GetString(1);

                // A MEMBER CARRYING A LIVE DECLARATION IS NOT ONE WHOSE RUNS SUCCEEDED, whatever
                // its own run's outcome says. Without this the two answers disagree about one
                // member: its outcome IS `completed`, which is precisely how these teams fell
                // through to `Undeclared`. `UNDECLARED` and `AWAITING` are documented as mutually
                // exclusive BY CONSTRUCTION, and this is the line that makes that true rather than
                // vacuous.
                everyTerminalCompleted = false;

                if (type == MessageTypes.Blocked) blockedBy ??= member;
                else awaitingFrom ??= member;
            }
        }

        var runsFailed = 0;

        await using (var failures = connection.CreateCommand())
        {
            // A COUNT OF RUNS, not of members. Four runs can fail across two members within
            // seconds, and the tile says both numbers.
            //
            // Exclude failures with stoppedByPerson: true, which are not platform
            // failures but runs stopped by a person.
            failures.CommandText =
                $"""
                SELECT COUNT(*) FROM messages
                WHERE correlation_id = $correlation
                  AND seq > $since
                  AND type = $failed
                  AND source LIKE $prefix COLLATE NOCASE ESCAPE '\'
                  AND source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
                  AND json_extract(payload, '$.{PayloadFields.StoppedByPerson}') IS NOT 1
                """;

            failures.Parameters.AddWithValue("$correlation", correlation);
            failures.Parameters.AddWithValue("$since", sinceSeq);
            failures.Parameters.AddWithValue("$failed", MessageTypes.Failed);
            failures.Parameters.AddWithValue("$prefix", prefix);
            failures.Parameters.AddWithValue("$nested", nested);

            runsFailed = Convert.ToInt32(
                await failures.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        var runs = new Dictionary<string, MemberRuns>(StringComparer.OrdinalIgnoreCase);

        await using (var pairs = connection.CreateCommand())
        {
            // EACH `started` PAIRED WITH THE NEXT TERMINAL EVENT FROM THE SAME SOURCE, inside this
            // correlation. The subselect is the pairing, and taking the LOWEST seq above the start
            // is what makes it "the next" rather than "any".
            //
            // A `started` WITH NO PARTNER IS A RUN THAT NEVER FINISHED - a host restart mid-flight,
            // which happened repeatedly during this project. It comes back NULL here and is counted
            // as unfinished below: never as zero, and never as "still running".
            pairs.CommandText =
                """
                SELECT s.source,
                       s.occurred_at,
                       (SELECT e.occurred_at FROM messages e
                         WHERE e.correlation_id = s.correlation_id
                           AND e.source = s.source COLLATE NOCASE
                           AND e.seq > s.seq
                           AND e.type IN ($completed, $failed, $blocked)
                         ORDER BY e.seq LIMIT 1)
                FROM messages s
                WHERE s.correlation_id = $correlation
                  AND s.seq > $since
                  AND s.type = $started
                  AND s.source LIKE $prefix COLLATE NOCASE ESCAPE '\'
                  AND s.source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
                ORDER BY s.seq
                """;

            pairs.Parameters.AddWithValue("$correlation", correlation);
            pairs.Parameters.AddWithValue("$since", sinceSeq);
            pairs.Parameters.AddWithValue("$started", MessageTypes.Started);
            pairs.Parameters.AddWithValue("$completed", MessageTypes.Completed);
            pairs.Parameters.AddWithValue("$failed", MessageTypes.Failed);
            pairs.Parameters.AddWithValue("$blocked", MessageTypes.Blocked);
            pairs.Parameters.AddWithValue("$prefix", prefix);
            pairs.Parameters.AddWithValue("$nested", nested);

            await using var reader = await pairs.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var member = NameOf(reader.GetString(0));

                if (!runs.TryGetValue(member, out var row))
                {
                    row = new MemberRuns();
                    runs[member] = row;
                }

                row.Runs++;

                if (reader.IsDBNull(2))
                {
                    row.Unfinished++;
                    continue;
                }

                var from = MessageRows.ReadStamp(reader.GetString(1));
                var to = MessageRows.ReadStamp(reader.GetString(2));

                // Clamped at zero rather than allowed negative. Two rows a millisecond apart can
                // round the wrong way, and a negative run time on a dialog is the kind of visible
                // nonsense that gets reported as a pairing bug.
                var seconds = (long)Math.Max(0, Math.Round((to - from).TotalSeconds));

                row.Counted++;
                row.Seconds += seconds;
            }
        }

        var members = runs
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new MemberExecution(
                entry.Key,
                entry.Value.Runs,

                // NULL, NOT ZERO, for a member whose every run was cut short. It spent real time
                // and nothing here knows how much.
                entry.Value.Counted > 0 ? entry.Value.Seconds : null,
                entry.Value.Unfinished))
            .ToList();

        var counted = runs.Values.Sum(row => row.Counted);
        var unfinished = runs.Values.Sum(row => row.Unfinished);
        var execution = runs.Values.Sum(row => row.Seconds);

        // THE RANKING, and every step of it is an argument rather than an ordering. A platform
        // failure outranks an agent's decision to stop, which outranks an agent's decision to ask -
        // because a run that died after asking did not get its question answered either. The tab
        // chip orders these identically, or a chip and the card beneath it disagree about one
        // member.
        //
        // `Running` here does NOT mean a member is running: the log cannot see a roster. It means
        // the workflow is open and no member's last terminal fact says anything louder. The caller
        // decides RUNNING from live containers, and it ranks first.
        //
        // `Closed` ranks above `Failed`/`Blocked`/`Awaiting`, because it is a
        // person's declaration to stop counting a workflow. However, `Completed` still outranks
        // `Closed`, because a declaration of delivery is louder than an administrative close.
        //
        // `Paused` SITS BETWEEN `Closed` AND `Failed`, and that placement is the argument.
        // A paused workflow whose last member run also failed will not run again whatever the
        // failure says - so telling a reader FAILED sends them to inspect a run when the reason
        // nothing is moving is the budget, and the fix is on a different screen entirely.
        // `Completed` and `Closed` still outrank it: a workflow paused and then closed is closed.
        //
        // DERIVED FROM `pausedAt` AND NOWHERE ELSE, so the word and the timestamp cannot
        // disagree, and no client re-derives it - the same pairing `blockedBy`/`Blocked` and
        // `awaitingFrom`/`Awaiting` already have.
        var state =
            completed ? "Completed"
            : closed ? "Closed"
            : pausedAt is not null ? "Paused"
            : failedMembers.Count > 0 ? "Failed"
            : blockedBy is not null ? "Blocked"
            : awaitingFrom is not null ? "Awaiting"

            // UNDECLARED CARRIES ONLY THE CLAUSES THE LOG CAN ANSWER: the workflow is open and every
            // member that ran ended `completed`. Nothing Running, no queue depth and the grace
            // period belong to the caller, which owns the roster and the clock - and there must go
            // on being exactly one grace constant, which is in the SPA.
            : terminals > 0 && everyTerminalCompleted ? "Undeclared"
            : "Running";

        // Per-workflow spend for budget checking. Null when there are no completed/failed runs.
        var spend = await GetWorkflowSpendAsync(correlation, ct);
        var spendToReturn = correlation > 0 && (spend.RunsWithMeasuredUsage > 0 || spend.RunsWithoutUsage > 0) 
            ? spend 
            : null;

        // AND THE SAME MEASUREMENT OVER THE WINDOW THE BOUND ACTUALLY USES.
        //
        // The figure above is the WHOLE workflow and never comes back down, so a screen drawn from
        // it goes on reading over-limit after a nudge or a resume has plainly released the
        // workflow - the display contradicting the decision. Both are answered here, from the same
        // read, so nothing downstream has to choose between two routes to get the one it needs.
        //
        // NULL UNDER THE SAME CONDITION, deliberately: the two fields appear and disappear
        // together, so a client cannot end up with one and not the other and quietly fall back to
        // the wrong one.
        var sinceNudge = await GetSpendSinceNudgeAsync(correlation, ct);
        var sinceNudgeToReturn = spendToReturn is null ? null : sinceNudge;

        return new TeamWorkflowTiming(
            true,
            correlation,
            state,
            startedAt,

            // NULL WHILE OPEN, and open is the ordinary case: `workflow.completed` is a manager's
            // declaration and most workflows never get one.
            //
            // A CLOSED WORKFLOW HAS ENDED TOO, whatever word it wears. The reasoning above is about
            // OPEN-ness, not about who declared it: `workflow.closed` is terminal for
            // `WorkflowOpenSql`, so a closed workflow is not open and its clock must stop. A
            // `completed`-only test would keep a closed workflow ticking forever on every surface.
            completed || closed ? lastActivityAt : null,
            serverNow,
            execution,
            unfinished > 0,
            counted,
            unfinished,
            blockedBy,
            runsFailed,
            failedMembers,
            null,
            members,
            lastActivityAt,
            awaitingFrom,
            subject,
            spendToReturn,

            // A PAUSED WORKFLOW IS STILL OPEN, which is why `EndedAt` above is untouched here.
            // `WorkflowOpenSql` counts it open, `StatusCommand.IsOpen` and the SPA's
            // `workflowOpen` both test `endedAt == null`, and a paused workflow that vanished
            // from the open list would be one nobody could find in order to resume it.
            pausedAt,
            pausedReason,
            pausedLimit,
            sinceNudgeToReturn);
    }

    /// <summary>
    /// The sentence and the figure off a `workflow.paused` payload.
    ///
    /// BOTH OPTIONAL, INDEPENDENTLY. The log is append-only and replayed, so a row written by an
    /// older or different writer may carry neither - and a pause with no reason is still a pause.
    /// The STATE comes from the row's existence, never from these, which is why a parse failure
    /// here is silent rather than a workflow that stops reading as paused.
    /// </summary>
    private static (string? Reason, long? Limit) PauseDetailFrom(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return (null, null);

        try
        {
            using var json = JsonDocument.Parse(payload);

            if (json.RootElement.ValueKind is not JsonValueKind.Object) return (null, null);

            var reason =
                json.RootElement.TryGetProperty(PayloadFields.Reason, out var reasonField)
                && reasonField.ValueKind is JsonValueKind.String
                && reasonField.GetString() is { Length: > 0 } text
                    ? text
                    : null;

            var limit =
                json.RootElement.TryGetProperty(PayloadFields.Limit, out var limitField)
                && limitField.ValueKind is JsonValueKind.Number
                && limitField.TryGetInt64(out var bound)
                    ? bound
                    : (long?)null;

            return (reason, limit);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// A ROOT MESSAGE'S SUBJECT, out of its payload - or null. Two shapes, both live, for
    /// <see cref="InstructionText"/>'s own reason: a row may carry `subject` (and `body`) beside
    /// `instruction`, or `instruction` alone, and the log is APPEND-ONLY and REPLAYED, so both
    /// shapes are met on the same team.
    ///
    /// READS, NEVER RE-DERIVES: an explicit `subject` field wins outright, exactly as
    /// `KanbanProjector.ExtractTitleAndBody` reads the identical stored field for a card's title.
    /// The fallback is `InstructionText.Split`, the ONE place that decides what a subject is - not a
    /// clamp of its own, which would let this answer disagree with the card's title over the same
    /// row.
    /// </summary>
    private static string? SubjectFrom(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(payload);

            if (json.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            if (json.RootElement.TryGetProperty("subject", out var subjectField)
                && subjectField.ValueKind is JsonValueKind.String
                && subjectField.GetString() is { Length: > 0 } subject)
            {
                return subject;
            }

            if (json.RootElement.TryGetProperty("instruction", out var instructionField)
                && instructionField.ValueKind is JsonValueKind.String)
            {
                var parts = InstructionText.Split(instructionField.GetString());
                return parts.Subject.Length > 0 ? parts.Subject : null;
            }

            return null;
        }
        catch (JsonException)
        {
            // THE PAYLOAD IS OPAQUE TO THIS STORE, exactly as it is to `KanbanProjector`'s own
            // `StringField`: nothing here may assume a row appended by some other caller is even
            // JSON, and a subject nobody can read is the same answer as one nobody wrote.
            return null;
        }
    }

    /// <summary>
    /// WHICH OF THESE CORRELATIONS ARE OPEN. See <see cref="IMessageLog.OpenWorkflowsAmongAsync"/>.
    ///
    /// <para>
    /// THE PREDICATE IS `WorkflowOpenSql.NotClosed`, UNCHANGED - the one place "open" is defined,
    /// so this and the team projection cannot disagree. It is correlated against an alias `m`, and
    /// here `m` is a DERIVED TABLE of the distinct correlations asked about rather than the
    /// `messages` rows themselves: evaluated per message row it would run the NOT EXISTS once per
    /// row of a workflow that may hold hundreds, and DISTINCT would then collapse hundreds of
    /// identical answers. Per correlation it runs once each, over `ix_messages_correlation`.
    /// </para>
    ///
    /// <para>
    /// ANY ROW OF THE CORRELATION ADMITS IT, not the root by seq. A correlation is its root's seq, so
    /// `seq IN (...)` would be one primary-key read each - but a root that retention has purged
    /// would then drop a workflow whose later rows are still there, and a purged workflow that
    /// still has rows is not the same thing as a closed one.
    /// </para>
    ///
    /// <para>
    /// ONE PARAMETER PER CORRELATION. SQLite's default variable ceiling is 32,766; a backlog with
    /// that many dispatched items on one screen is not a shape this product has, and a list that
    /// large has bigger problems than this query.
    /// </para>
    /// </summary>
    public async Task<IReadOnlySet<long>> OpenWorkflowsAmongAsync(
        IReadOnlyCollection<long> correlations, CancellationToken ct = default)
    {
        var open = new HashSet<long>();

        var asked = correlations.Distinct().ToList();
        if (asked.Count == 0) return open;

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        var names = new List<string>(asked.Count);

        for (var i = 0; i < asked.Count; i++)
        {
            var name = $"$c{i}";
            names.Add(name);
            command.Parameters.AddWithValue(name, asked[i]);
        }

        command.CommandText =
            $"""
            SELECT m.correlation_id
            FROM (
                SELECT DISTINCT correlation_id
                FROM messages
                WHERE correlation_id IN ({string.Join(", ", names)})
            ) m
            WHERE {WorkflowOpenSql.NotClosed}
            """;

        WorkflowOpenSql.Bind(command);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) open.Add(reader.GetInt64(0));

        return open;
    }

    /// <summary>
    /// EVERY WORKFLOW THIS TEAM HAS RUN SINCE ITS FLOOR, open and closed alike. See
    /// <see cref="IMessageLog.WorkflowsForTeamAsync"/>.
    /// </summary>
    public async Task<TeamWorkflows> WorkflowsForTeamAsync(
        string team, long sinceSeq, CancellationToken ct = default)
    {
        var serverNow = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(team))
        {
            return new TeamWorkflows(false, 0, 0, null, serverNow, [], "No team was named.");
        }

        await using var connection = Open();

        var (prefix, nested) = TeamSourcePattern(team);

        var correlations = new List<long>();

        await using (var pick = connection.CreateCommand())
        {
            // THE TEAM'S OWN CORRELATIONS ABOVE THE FLOOR, newest first, AND NO OPEN/CLOSED FILTER
            // AT ALL. Filtering to `WorkflowOpenSql.NotClosed` with a `LIMIT 1` fallback for a
            // blank tile would render a team's three finished workflows as ONE row under a heading
            // saying the team held it open. The list is every workflow, each reporting its own
            // state, and the tile takes the first row because a team that has ever run always has
            // one. There is no
            // mode flag and no second route; see `TeamWorkflows`.
            //
            // THE `NOT LIKE $nested` PREDICATE IS WHAT KEEPS THIS LIST THIS TEAM'S. Neither half of
            // a ContainerId may contain a `/`, so `Team/%` minus `Team/%/%` is exactly this team's
            // own members and never a nested one's. Listing closed workflows does not widen ownership.
            //
            // THE FLOOR STAYS, AND IT IS LOAD-BEARING HERE MORE THAN ANYWHERE. "Every workflow this
            // team has run" means since the floor: a team deleted and recreated under the same
            // names shares its members' qualified ids with whatever came before, and without
            // `m.seq > $since` a brand-new team would open with its predecessor's whole history.
            //
            // CAPPED. Fifty is far more than a person can read and small enough that the tile stays
            // cheap. `OpenCount`, `TotalCount` and `EarliestStartedAt` are computed UNCAPPED below -
            // see TeamWorkflows for why a capped count would silently under-report, and note that
            // `TotalCount`, not `OpenCount`, is the one to compare this list's length against.
            //
            // ORDERED BY CORRELATION ID, WHICH IS A ROOT SEQ - so "newest first" means most recently
            // STARTED, the ordering the wire tests pin. A re-woken old workflow therefore sorts by
            // when it began rather than by when it was last touched.
            pick.CommandText =
                """
                SELECT DISTINCT m.correlation_id
                FROM messages m
                WHERE m.source LIKE $prefix COLLATE NOCASE ESCAPE '\'
                  AND m.source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
                  AND m.seq > $since
                ORDER BY m.correlation_id DESC
                LIMIT 50
                """;

            pick.Parameters.AddWithValue("$prefix", prefix);
            pick.Parameters.AddWithValue("$nested", nested);
            pick.Parameters.AddWithValue("$since", sinceSeq);

            await using var reader = await pick.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) correlations.Add(reader.GetInt64(0));
        }

        // AN EMPTY LIST MEANS ONE THING ONLY: this team has published nothing since its floor. It
        // does NOT mean "nothing OPEN" - the list above carries closed workflows, so a team that has
        // ever run cannot reach this branch at all, and no fallback belongs here.
        //
        // `Available: false` AND NOT A ZERO, so the caller renders an em dash. Zero is a measured
        // count and this is an unmeasured one - "never ran" is a different fact from "ran and
        // finished", and only the first belongs here.
        if (correlations.Count == 0)
        {
            return new TeamWorkflows(
                false, 0, 0, null, serverNow, [],
                "This team has published nothing since its members were created");
        }

        int openCount;

        await using (var count = connection.CreateCommand())
        {
            // HOW MANY THIS TEAM HOLDS OPEN - `WorkflowOpenSql.NotClosed` over the same team scope
            // floor as `pick`, uncapped. NOT THE SAME PREDICATE AS `pick`: `pick` lists every
            // workflow and this counts the open subset, so the two legitimately disagree and
            // `OpenCount` is NOT the truncation signal. `TotalCount` below is. `OpenCount` stays because the team
            // tile and the `status` tool both read it for "how much is this team still carrying",
            // which no length of the list can answer.
            count.CommandText =
                $"""
                SELECT COUNT(DISTINCT m.correlation_id)
                FROM messages m
                WHERE m.source LIKE $prefix COLLATE NOCASE ESCAPE '\'
                  AND m.source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
                  AND m.seq > $since
                  AND {WorkflowOpenSql.NotClosed}
                """;

            count.Parameters.AddWithValue("$prefix", prefix);
            count.Parameters.AddWithValue("$nested", nested);
            count.Parameters.AddWithValue("$since", sinceSeq);
            WorkflowOpenSql.Bind(count);

            openCount = Convert.ToInt32(
                await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        int totalCount;

        await using (var total = connection.CreateCommand())
        {
            // EXACTLY `pick`'S PREDICATE, MINUS THE CAP - the sibling of `openCount` that carries
            // the UNCAPPED total since the floor. `Workflows` truncates at fifty so the tile stays
            // cheap; this must not, or a team that has run sixty workflows reports fifty with
            // nothing on the payload saying so. A client rendering "50 of 63" reads 63 from here.
            total.CommandText =
                """
                SELECT COUNT(DISTINCT m.correlation_id)
                FROM messages m
                WHERE m.source LIKE $prefix COLLATE NOCASE ESCAPE '\'
                  AND m.source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
                  AND m.seq > $since
                """;

            total.Parameters.AddWithValue("$prefix", prefix);
            total.Parameters.AddWithValue("$nested", nested);
            total.Parameters.AddWithValue("$since", sinceSeq);

            totalCount = Convert.ToInt32(
                await total.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        DateTimeOffset? earliest = null;

        await using (var span = connection.CreateCommand())
        {
            // ALSO UNCAPPED, and over EVERY ROW in an OPEN correlation - not only the team's own -
            // exactly as TimingForAsync's own bounds query reads a workflow's root. STILL THE OPEN
            // SUBSET even though the list above is not: this is the start of the span the tile
            // renders for work still in flight, so a team whose every workflow is finished reports
            // null here and a list of finished rows beside it. The fifty-entry cap above drops the
            // OLDEST correlations first (`ORDER BY ... DESC`), which is exactly where the earliest
            // start is most likely to live, so this cannot be derived from the capped list. Never taken from the smallest open CORRELATION ID: a correlation id is a
            // seq, and seq order equals time order only when the store stamps the clock - which the
            // test fixtures for this very feature deliberately override. `MIN(occurred_at)` makes no
            // ordering assumption.
            span.CommandText =
                $"""
                SELECT MIN(occurred_at)
                FROM messages
                WHERE seq > $since
                  AND correlation_id IN (
                      SELECT DISTINCT m.correlation_id
                      FROM messages m
                      WHERE m.source LIKE $prefix COLLATE NOCASE ESCAPE '\'
                        AND m.source NOT LIKE $nested COLLATE NOCASE ESCAPE '\'
                        AND m.seq > $since
                        AND {WorkflowOpenSql.NotClosed})
                """;

            span.Parameters.AddWithValue("$prefix", prefix);
            span.Parameters.AddWithValue("$nested", nested);
            span.Parameters.AddWithValue("$since", sinceSeq);
            WorkflowOpenSql.Bind(span);

            if (await span.ExecuteScalarAsync(ct) is string stamp)
            {
                earliest = MessageRows.ReadStamp(stamp);
            }
        }

        // ONE PROJECTION PER ROW, CLOSED ONES INCLUDED - the state ranking `TimingForAsync` already
        // computes is what each row reports, and `Undeclared` is a real state among them rather
        // than an error. Nothing here re-decides whether a workflow is open; the row says.
        var timings = new List<TeamWorkflowTiming>(correlations.Count);
        foreach (var correlation in correlations)
        {
            timings.Add(await TimingForAsync(connection, team, correlation, sinceSeq, serverNow, ct));
        }

        return new TeamWorkflows(true, openCount, totalCount, earliest, serverNow, timings);
    }

    /// <summary>Runs accumulated for one member while the pairing query is read.</summary>
    private sealed class MemberRuns
    {
        public int Runs;
        public int Counted;
        public int Unfinished;
        public long Seconds;
    }

    /// <summary>
    /// The bare member name from a qualified source. Neither half of a ContainerId may contain a
    /// `/`, so the last one is the separator - the same reading the SPA's own fold does.
    /// </summary>
    private static string NameOf(string source) => source[(source.LastIndexOf('/') + 1)..];

    /// <summary>
    /// A team with no workflow to report. AVAILABLE FALSE AND NOT A ZERO: zero is a measured
    /// duration and this is an absent one, so the caller renders an em dash.
    /// </summary>
    private static TeamWorkflowTiming Unavailable(DateTimeOffset serverNow, string missing) =>
        new(false, null, null, null, null, serverNow, 0, false, 0, 0, null, 0, [], missing, []);

    public async Task<ContainerMarks> LiveMarksForAsync(
        ContainerId member, long sinceSeq, CancellationToken ct = default)
    {
        await using var connection = Open();

        var source = member.ToString();

        Message? blocked = null;
        Message? needsDecision = null;

        await using (var declarations = connection.CreateCommand())
        {
            // THE SAME PREDICATE THE TILE USES, ASKED OF A MEMBER INSTEAD OF A WORKFLOW - see the
            // `declarations` query in ElapsedForTeamAsync, whose comment carries the argument in
            // full. A declaration is live unless the member has HAD ANOTHER GO, because its own
            // run's `completed` always follows it and always would clear it.
            //
            // NO CORRELATION CLAUSE, and that is the one difference from the tile's version. A card
            // asks whether this member has been woken since it spoke; a wake counts whatever
            // workflow it arrived under. Scoping this would clear a member's unanswered give-up
            // because somebody ELSE was given a job.
            declarations.CommandText =
                """
                SELECT seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at
                FROM messages d
                WHERE d.source = $source COLLATE NOCASE
                  AND d.seq > $since
                  AND d.type IN ($blocked, $needsDecision)
                  AND NOT EXISTS (
                      SELECT 1 FROM messages later
                      WHERE later.seq > d.seq
                        AND later.type = $started
                        AND later.source = d.source COLLATE NOCASE)
                ORDER BY d.seq
                """;

            declarations.Parameters.AddWithValue("$source", source);
            declarations.Parameters.AddWithValue("$since", sinceSeq);
            declarations.Parameters.AddWithValue("$blocked", MessageTypes.Blocked);
            declarations.Parameters.AddWithValue("$needsDecision", MessageTypes.NeedsDecision);
            declarations.Parameters.AddWithValue("$started", MessageTypes.Started);

            // ORDERED ASCENDING AND OVERWRITTEN, so each field ends up holding the NEWEST live row
            // of its type. A member that gave up twice without being woken in between has said the
            // same thing twice, and the second sentence is the current one.
            foreach (var row in await MessageRows.ReadAllAsync(declarations, ct))
            {
                if (row.Type == MessageTypes.Blocked) blocked = row;
                else needsDecision = row;
            }
        }

        Message? failed = null;

        await using (var outcome = connection.CreateCommand())
        {
            // THE OTHER PREDICATE. `failed` is a run OUTCOME, so it and `completed` are
            // ALTERNATIVES and MAX(seq) over the pair is exactly right for them - which is what
            // makes it wrong for the two above, and why this is a second query rather than three
            // types in the first one.
            outcome.CommandText =
                """
                SELECT seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at
                FROM messages
                WHERE seq = (
                    SELECT MAX(seq) FROM messages
                    WHERE source = $source COLLATE NOCASE
                      AND seq > $since
                      AND type IN ($completed, $failed))
                  AND type = $failed
                """;

            outcome.Parameters.AddWithValue("$source", source);
            outcome.Parameters.AddWithValue("$since", sinceSeq);
            outcome.Parameters.AddWithValue("$completed", MessageTypes.Completed);
            outcome.Parameters.AddWithValue("$failed", MessageTypes.Failed);

            failed = (await MessageRows.ReadAllAsync(outcome, ct)).FirstOrDefault();
        }

        return blocked is null && needsDecision is null && failed is null
            ? ContainerMarks.None
            : new ContainerMarks(blocked, needsDecision, failed);
    }

    public async Task<long> LastSeqBeforeAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // The first row at or after $seq: rows are appended in time order, so whether it was written
        // before `at` only ever turns from yes to no as $seq rises, gaps from deletion included.
        command.CommandText = "SELECT seq, occurred_at FROM messages WHERE seq >= $seq ORDER BY seq LIMIT 1";
        var seqParameter = command.Parameters.AddWithValue("$seq", 0L);

        async Task<long?> FirstAtOrAfterAsync(long seq)
        {
            seqParameter.Value = seq;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return MessageRows.ReadStamp(reader.GetString(1)) < at ? reader.GetInt64(0) : null;
        }

        // The highest seq whose first row at or after it was written before `at`.
        var low = 0L;
        var high = await HighestSeqAsync(ct);
        var found = 0L;

        while (low <= high)
        {
            var mid = low + (high - low) / 2;

            if (await FirstAtOrAfterAsync(mid) is { } before)
            {
                found = before;
                low = before + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    public async Task<Message?> FindAsync(long seq, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at
            FROM messages WHERE seq = $seq
            """;

        command.Parameters.AddWithValue("$seq", seq);

        return (await MessageRows.ReadAllAsync(command, ct)).SingleOrDefault();
    }

    public async Task<long> HighestSeqAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // COALESCE, because MAX over an empty table is NULL rather than 0.
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM messages";

        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<Message>> ReadCorrelationAsync(long correlationId, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT seq, type, payload, source, correlation_id, causation_seq, depth, occurred_at
            FROM messages WHERE correlation_id = $id ORDER BY seq
            """;

        command.Parameters.AddWithValue("$id", correlationId);

        return await MessageRows.ReadAllAsync(command, ct);
    }

    /// <summary>
    /// Spend since the last nudge. See <see cref="IMessageLog.GetSpendSinceNudgeAsync"/> for why the
    /// budget uses this and a screen does not.
    /// </summary>
    public Task<WorkflowSpend> GetSpendSinceNudgeAsync(long correlationId, CancellationToken ct = default) =>
        SpendAsync(correlationId, sinceLastNudge: true, ct);

    public Task<WorkflowSpend> GetWorkflowSpendAsync(long correlationId, CancellationToken ct = default) =>
        SpendAsync(correlationId, sinceLastNudge: false, ct);

    /// <summary>
    /// Billable tokens, measured runs and unmeasured runs over <c>usage_ledger</c> rows: the three
    /// columns every spend read answers. <c>billable</c> is NULL for an unmeasured run, so SUM adds
    /// nothing for it, and it is counted as unmeasured instead - never a zero. Shared by the workflow
    /// spend and the trigger spend so the two cannot drift. The weights are the ledger writer's
    /// (<see cref="LedgerRows.Usage"/>), held to the log's by <c>LedgerParityTests</c>.
    /// </summary>
    private const string LedgerSpend =
        """
        COALESCE(SUM(billable), 0), COALESCE(SUM(measured), 0), COALESCE(SUM(1 - measured), 0)
        """;

    /// <summary>
    /// ON THE LEDGER, so Reset's "Delete memory" no longer lowers a workflow's spend: the member's
    /// log rows go, its ledger rows stay. The NUDGE WINDOW is the ledger's too: each nudge writes a
    /// <c>nudge_ledger</c> row with its log row (<see cref="LedgerRows"/>), so a Reset that deletes
    /// the nudge leaves the window where it was. A nudge from before <c>outcome-003</c> has no
    /// ledger row and is still read from the log while the log holds it.
    /// </summary>
    private async Task<WorkflowSpend> SpendAsync(
        long correlationId, bool sinceLastNudge, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT {LedgerSpend}
            FROM usage_ledger
            WHERE correlation = $id
              AND run_seq > $since
            """;

        // THE WINDOW. Zero for the whole-workflow figure, so the clause costs nothing there;
        // for the budget it is the last instruction caused by the correlation itself - a nudge, by
        // construction - and 0 again when there has been none, which reads the whole workflow.
        // The ledger's record first; the log only for a nudge from before the ledger kept them.
        long since = 0;

        if (sinceLastNudge)
        {
            await using var window = connection.CreateCommand();
            window.CommandText =
                """
                SELECT MAX(
                    (SELECT COALESCE(MAX(nudge_seq), 0) FROM nudge_ledger WHERE correlation = $id),
                    (SELECT COALESCE(MAX(seq), 0) FROM messages
                     WHERE correlation_id = $id
                       AND causation_seq = $id
                       AND type LIKE 'agentContainer.instruction.%'))
                """;
            window.Parameters.AddWithValue("$id", correlationId);

            since = (await window.ExecuteScalarAsync(ct)) is long found ? found : 0;
        }

        command.Parameters.AddWithValue("$id", correlationId);
        command.Parameters.AddWithValue("$since", since);

        return await ReadSpendAsync(command, ct);
    }

    private static async Task<WorkflowSpend> ReadSpendAsync(SqliteCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct)
            ? new WorkflowSpend(
                reader.GetInt64(0),
                checked((int)reader.GetInt64(1)),
                checked((int)reader.GetInt64(2)))
            : new WorkflowSpend(0, 0, 0);
    }

    /// <summary>
    /// ON THE LEDGER. Each run's row names the trigger fire it answered - directly, or as a Manager
    /// run woken by such a run's `completed`, `failed` or `handback` row - and when that fire was
    /// appended (<see cref="LedgerRows"/>), so a Reset that deletes the instruction rows between a
    /// trigger and its runs no longer lowers the trigger's spend for the day.
    /// </summary>
    public async Task<WorkflowSpend> GetTriggerSpendAsync(
        IReadOnlyCollection<string> sources, DateTimeOffset since, CancellationToken ct = default)
    {
        if (sources.Count == 0) return new WorkflowSpend(0, 0, 0);

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        var names = sources.Select((_, index) => $"$source{index}").ToArray();

        command.CommandText =
            $"""
            SELECT {LedgerSpend}
            FROM usage_ledger
            WHERE trigger_source IN ({string.Join(", ", names)})
              AND trigger_fired_at >= $since
            """;

        var index = 0;
        foreach (var source in sources)
        {
            command.Parameters.AddWithValue(names[index++], source);
        }

        command.Parameters.AddWithValue("$since", Stamp(since.ToUniversalTime()));

        return await ReadSpendAsync(command, ct);
    }
    public async Task<IReadOnlyList<Message>> ReadRangeAsync(
        long afterSeq, int max, CancellationToken ct = default)
    {
        if (max <= 0) return [];

        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText =
            $"""
             SELECT {MessageRows.Columns}
             FROM messages
             WHERE seq > $after
             ORDER BY seq
             LIMIT $max
             """;

        command.Parameters.AddWithValue("$after", afterSeq);
        command.Parameters.AddWithValue("$max", max);

        return await MessageRows.ReadAllAsync(command, ct);
    }

    public async Task<PurgeReport> DeleteAsync(
        IReadOnlyCollection<long> seqs, CancellationToken ct = default)
    {
        if (seqs.Count == 0) return new PurgeReport(0, 0);

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        // The id list is INTERPOLATED, and that is safe HERE AND ONLY HERE: every element is a
        // `long` joined out of a typed collection, so there is no string that could reach the SQL
        // text. A parameter per row would be thousands of them on a real log.
        var ids = string.Join(',', seqs);
        var purged = 0;

        // ONE STATEMENT, REPEATED TO A FIXPOINT. Each pass removes the candidates NOTHING currently
        // cites; removing those un-pins whatever they cited, so the next pass reaches one link
        // deeper. It terminates because a pass removing nothing ends the loop and a pass removing
        // something strictly shrinks the table - the bound is the depth of the longest causation
        // chain, which is small.
        //
        // A DELIVERY STILL QUEUED FOR A RUN IS CITED TOO (`pending_deliveries`): its run will write
        // `started` and its terminal row caused by it, and the log refuses a row whose cause is gone,
        // so deleting it would let the run spend and leave no row - and no ledger row - behind.
        //
        // The obvious alternative - delete in descending seq order and catch the constraint
        // violation - is wrong twice over: it relies on foreign keys being ENFORCED, which this
        // store's Open() does not arrange, and it uses an exception for an ordinary outcome that has
        // to be counted and reported.
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText =
                $"""
                 DELETE FROM messages
                 WHERE seq IN ({ids})
                   AND seq NOT IN (
                       SELECT causation_seq FROM messages WHERE causation_seq IS NOT NULL)
                   AND seq NOT IN (SELECT seq FROM pending_deliveries)
                 """;

            var removed = await command.ExecuteNonQueryAsync(ct);

            if (removed == 0) break;

            purged += removed;
        }

        await transaction.CommitAsync(ct);

        return new PurgeReport(purged, seqs.Count - purged);
    }

    public async Task<long> PositionAsync(ContainerId subscriber, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT position FROM cursors WHERE subscriber = $s";
        command.Parameters.AddWithValue("$s", subscriber.ToString());

        return await command.ExecuteScalarAsync(ct) is long position ? position : 0;
    }

    public async Task AdvanceAsync(ContainerId subscriber, long seq, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // MAX, so an out-of-order or repeated advance cannot move a cursor BACKWARDS and redeliver
        // work already done. Monotonic by construction rather than by every caller remembering.
        command.CommandText =
            """
            INSERT INTO cursors (subscriber, position) VALUES ($s, $p)
            ON CONFLICT(subscriber) DO UPDATE SET position = MAX(position, excluded.position)
            """;

        command.Parameters.AddWithValue("$s", subscriber.ToString());
        command.Parameters.AddWithValue("$p", seq);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ForgetAsync(ContainerId subscriber, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        // Removed, never zeroed. A cursor at 0 says "has read nothing" and holds SlowestAsync down
        // forever; an absent one says the subscriber is gone, which is the truth here.
        command.CommandText = "DELETE FROM cursors WHERE subscriber = $s";
        command.Parameters.AddWithValue("$s", subscriber.ToString());

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> ForgetTeamAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = TeamScoped("cursors");
        command.Parameters.AddWithValue("$team", team);

        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The team half of a <c>Team/Name</c> subscriber key, matched NOCASE.
    /// </summary>
    /// <remarks>
    /// <c>instr</c> ANSWERS 0 WHEN THERE IS NO SLASH, and <c>substr(s, 1, -1)</c> is the empty
    /// string rather than an error - so without the guard, a row whose subscriber is not qualified
    /// would match a team whose name is empty. Nothing writes such a row today; the guard is here
    /// because the failure would be a silent DELETE of somebody else's row.
    ///
    /// NOCASE because ContainerId equality is case-insensitive on both halves, so the stored
    /// spelling and the caller's need not agree - and a case-sensitive purge would leave exactly the
    /// rows it was called to remove.
    /// </remarks>
    private static string TeamScoped(string table) =>
        $"""
        DELETE FROM {table}
        WHERE instr(subscriber, '/') > 0
          AND substr(subscriber, 1, instr(subscriber, '/') - 1) = $team COLLATE NOCASE
        """;

    public async Task<long?> SlowestAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT MIN(position) FROM cursors";

        return await command.ExecuteScalarAsync(ct) is long slowest ? slowest : null;
    }

    public async Task SetAsync(ContainerId subscriber, IReadOnlyCollection<string> types, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var key = subscriber.ToString();

        // Replaced wholesale rather than merged: a subscription set has to be readable as exactly
        // what it is, without reasoning about what an earlier call left underneath.
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM subscriptions WHERE subscriber = $s";
            clear.Parameters.AddWithValue("$s", key);
            await clear.ExecuteNonQueryAsync(ct);
        }

        foreach (var type in types)
        {
            await using var add = connection.CreateCommand();
            add.Transaction = transaction;
            add.CommandText = "INSERT OR IGNORE INTO subscriptions (subscriber, type) VALUES ($s, $t)";
            add.Parameters.AddWithValue("$s", key);
            add.Parameters.AddWithValue("$t", type);
            await add.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyCollection<string>> ForAsync(ContainerId subscriber, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT type FROM subscriptions WHERE subscriber = $s ORDER BY type";
        command.Parameters.AddWithValue("$s", subscriber.ToString());

        return await ReadStringsAsync(command, ct);
    }

    public async Task<int> ClearTeamAsync(string team, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = TeamScoped("subscriptions");
        command.Parameters.AddWithValue("$team", team);

        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyCollection<ContainerId>> SubscribersAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT DISTINCT subscriber FROM subscriptions ORDER BY subscriber";

        // Parsing rather than tolerating is deliberate: after the messages-003 step has run there
        // can be no unqualified row left, so one appearing here means something wrote a bare name
        // and should fail loudly at the seam rather than quietly downstream.
        var subscribers = new List<ContainerId>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            subscribers.Add(ContainerId.Parse(reader.GetString(0)));
        }

        return subscribers;
    }

    private static async Task<IReadOnlyCollection<string>> ReadStringsAsync(SqliteCommand command, CancellationToken ct)
    {
        var values = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) values.Add(reader.GetString(0));

        return values;
    }
}
