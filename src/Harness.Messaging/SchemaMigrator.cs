using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// The database has taken steps this binary has never heard of, so it was written by a newer build.
///
/// Its own type rather than InvalidOperationException so Program.cs can print the steps and stop
/// without catching an unrelated failure from the same call and reporting it as a downgrade.
/// </summary>
public sealed class SchemaFromTheFutureException(IReadOnlyList<string> unknownSteps)
    : InvalidOperationException(
        "This database was written by a newer build of Harness. Unknown schema steps: "
        + string.Join(", ", unknownSteps)
        + ". Start the newer build, or restore a backup taken before it ran.")
{
    public IReadOnlyList<string> UnknownSteps { get; } = unknownSteps;
}

/// <summary>
/// The pre-migration copy could not be written, so nothing was applied.
///
/// Its own type because Program.cs must be able to say "your data is untouched" with certainty, and
/// an InvalidOperationException from anywhere in the call would not carry that.
/// </summary>
public sealed class BackupFailedException(string destination, Exception inner)
    : InvalidOperationException(
        $"Could not write the pre-migration backup to '{destination}', so no schema change was "
        + "applied and your database is untouched.", inner);

/// <summary>
/// A step's own SQL failed, so that step did nothing.
///
/// Its own type for the same reason the two above are: Program.cs has to be able to print a sentence
/// naming what went wrong. A failing step is the MOST likely real migration failure rather than the
/// least, and without its own type it would arrive as the thirty-line stack trace those catches
/// exist to prevent.
/// </summary>
public sealed class MigrationFailedException(string stepId, Exception inner)
    : InvalidOperationException(
        $"Schema step '{stepId}' failed: {inner.Message} That step ran inside a transaction which "
        + "has been rolled back, so your database is unchanged by it. Steps applied before it stand "
        + "and are recorded; start again once the cause is fixed.", inner)
{
    public string StepId { get; } = stepId;
}

/// <summary>
/// The one thing that moves this database's schema forward.
///
/// It replaced four store constructors each calling an EnsureCreated of their own, in an order set
/// by Program.cs's declaration sequence. That worked only because every statement was
/// CREATE TABLE IF NOT EXISTS, and it is exactly the property that makes an ordered, recorded
/// migration impossible: a step that must run ONCE cannot live in a constructor that runs four
/// times.
///
/// Here rather than in Identity because this project already owns the file, the connection and the
/// WAL pragma. Identity's steps arrive as data.
/// </summary>
public sealed class SchemaMigrator(
    string databasePath,
    string? backupDirectory = null,
    SqliteDurability durability = SqliteDurability.SurvivesPowerLoss)
{
    /// <summary>
    /// WHAT `PRAGMA journal_mode` ACTUALLY ANSWERED on the last connection this migrator opened -
    /// `wal` when the file entered WAL, something else when it did not, null when the pragma could
    /// not be read at all.
    ///
    /// <para>
    /// EXPOSED RATHER THAN PRINTED OR LOGGED, which is this class's standing rule: it is a module,
    /// the console belongs to whoever called it, and the Host is the thing that knows where a fact
    /// like this should land. Null is "not measured" and is not a failure - the same distinction
    /// `CredentialUseRunner` makes about a probe that could not see.
    /// </para>
    ///
    /// <para>
    /// It matters because every other guarantee in this file assumes WAL. A reader never blocking a
    /// writer, `--backup` being safe against a live database, two stores writing one file at all -
    /// all of it is WAL, and this is what tells you whether the file is in it.
    /// </para>
    /// </summary>
    public string? JournalMode { get; private set; }

    /// <summary>
    /// Applies every step not already recorded, in the order given. Returns the path of the
    /// pre-migration backup, or null when nothing was pending and none was taken.
    ///
    /// Each step runs inside a transaction that ALSO writes its id. SQLite's DDL is transactional,
    /// so a crash between the statement and the record cannot happen - which is what makes the
    /// applied set trustworthy rather than merely optimistic.
    ///
    /// The backup path is RETURNED rather than printed, because this is a module and the console
    /// belongs to whoever called it - Program.cs prints it, a test reads it. Null rather than a
    /// path is how "nothing was pending" is said: it is what keeps an ordinary restart silent.
    ///
    /// <para>
    /// INVARIANT: <paramref name="steps"/> must be a SUPERSET of everything ever applied to this
    /// file. "Unknown" is computed as applied-minus-what-was-handed-in, so a caller passing a
    /// partial list is indistinguishable here from a database written by a newer build: hand an
    /// auth-migrated file only <c>MessageSchema.Steps</c> and it throws
    /// <see cref="SchemaFromTheFutureException"/> naming the seven auth steps, which reads to
    /// whoever meets it as CORRUPTION or a downgrade rather than as a caller error, and no step is
    /// applied. Program.cs and the operator commands pass both lists for exactly this reason; the
    /// specs that legitimately pass one half do so only against a file that has never seen the
    /// other. Pinned by
    /// <c>SchemaMigratorTests.A_partial_step_list_is_indistinguishable_from_a_newer_build</c>.
    /// </para>
    /// </summary>
    public async Task<string?> ApplyAsync(
        IReadOnlyList<MigrationStep> steps, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await EnsureLedgerAsync(connection, ct);

        var applied = await ReadAppliedAsync(connection, ct);

        var known = steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = applied.Where(id => !known.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // BEFORE any step runs. A downgrade that applied half of what it knew and then noticed
        // would leave the file in a state neither build has a name for.
        if (unknown.Length > 0) throw new SchemaFromTheFutureException(unknown);

        var pending = steps.Where(s => !applied.Contains(s.Id)).ToArray();

        if (pending.Length == 0) return null;

        // BEFORE the first step, and only when there is one. Every ordinary restart would otherwise
        // clone the database.
        var backup = await BackUpBeforeAsync(pending[0], ct);

        // Wrapped HERE rather than by widening Program.cs's catch to Exception: the step id is known
        // only at this seam, and a catch-all up there would swallow an unrelated failure and report
        // it as a schema problem. Cancellation is passed through untouched, matching BackUpAsync -
        // a shutdown is not a migration failure and must not be dressed as one.
        foreach (var step in pending)
        {
            try
            {
                await ApplyOneAsync(connection, step, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new MigrationFailedException(step.Id, exception);
            }
        }

        return backup;
    }

    /// <summary>Every step id this database has taken. Ordered by id so a caller comparing two
    /// databases is not reading insertion order.</summary>
    public async Task<IReadOnlyList<string>> AppliedAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await EnsureLedgerAsync(connection, ct);

        return [.. (await ReadAppliedAsync(connection, ct)).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A consistent copy, through SQLite rather than the filesystem.
    ///
    /// VACUUM INTO, never File.Copy: the database is in WAL, so copying messages.db alone yields a
    /// file missing everything still in the -wal. This is the single reason scripts/backup.ps1
    /// wraps this method instead of being a Copy-Item, and it is safe while a host is running
    /// because WAL permits a concurrent reader.
    /// </summary>
    public static async Task<string> BackUpAsync(
        string databasePath, string destination, CancellationToken ct = default)
    {
        // `Pooling=False`: this connection's native handle must not outlive the method, and must
        // not be disposable by anything else in the process. The `finally` below already had to
        // clear the pool by hand to stop the handle holding `databasePath` open against
        // `--restore`; unpooled, there is nothing to hold and nothing to clear.
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();

            // No parameter is accepted in this position, and the path is composed here rather than
            // supplied by anything outside the process.
            command.CommandText = $"VACUUM INTO '{destination.Replace("'", "''")}'";

            await command.ExecuteNonQueryAsync(ct);

            return destination;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new BackupFailedException(destination, exception);
        }
        finally
        {
            // Microsoft.Data.Sqlite pools by default: closing this connection returns its native
            // handle to the pool rather than releasing it, which keeps `databasePath` (and, since
            // that file is in WAL mode, its -wal sidecar) open on Windows for the lifetime of the
            // POOL rather than the connection. A caller that immediately overwrites or deletes that
            // same path in this same process - exactly what OperatorCommands.RestoreAsync does right
            // after the pre-restore backup - would fail against a handle this method never let go of.
            // Scoped to this one connection string rather than SqliteConnection.ClearAllPools(),
            // which would also drop every OTHER pooled connection in the process; nothing here needs
            // that wider a hammer.
            await connection.CloseAsync();
            SqliteConnection.ClearPool(connection);
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Whether another connection currently holds SQLite's write lock on this database - asked of
    /// SQLite itself (BEGIN IMMEDIATE, then rolled back) rather than inferred from the file, because
    /// on Windows SQLite opens its own files with a share mode permissive enough that a plain
    /// exclusive File.Open still succeeds while a real host is serving requests through it. That is
    /// why <c>OperatorCommands.IsHeld</c> alone did not catch a live host.
    ///
    /// This alone is NOT sufficient either, and callers must not treat it as such: SQLite holds a
    /// lock only for the DURATION of an active transaction, so an idle-but-running host - which is
    /// most of the time, between polls - answers false here. <c>OperatorCommands</c> also checks for
    /// the process itself for exactly that reason; this method exists to catch the moments that check
    /// cannot, such as an actual migration or write in flight.
    /// </summary>
    public static async Task<bool> IsInUseAsync(string databasePath, CancellationToken ct = default)
    {
        if (!File.Exists(databasePath)) return false;

        // Pooling off: this is a one-shot probe, and a pooled handle left open afterward would be
        // exactly the kind of stray lock a caller like RestoreAsync is trying to avoid leaving behind.
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");

        try
        {
            await connection.OpenAsync(ct);

            // FAIL FAST. This is a probe, not a wait - the ordinary 5000ms would make every restore
            // pause for it on the happy path, so this is the one caller that passes its own value.
            await SqlitePragmas.ApplyAsync(connection, busyTimeoutMilliseconds: 0, ct: ct);

            await using (var begin = connection.CreateCommand())
            {
                // IMMEDIATE, not the bare default: it asks for the write lock right now, so a
                // contended database fails HERE rather than on some later statement that never runs.
                begin.CommandText = "BEGIN IMMEDIATE;";

                // ONE SECOND, NOT THE THIRTY THE PROVIDER DEFAULTS TO. busy_timeout=0 above tells
                // SQLite not to wait, but Microsoft.Data.Sqlite has its own retry loop around a
                // SQLITE_BUSY that runs until CommandTimeout - so without this line the "fail fast"
                // probe would take thirty seconds to say a host was serving.
                begin.CommandTimeout = 1;
                await begin.ExecuteNonQueryAsync(ct);
            }

            await using (var rollback = connection.CreateCommand())
            {
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync(ct);
            }

            return false;
        }
        catch (SqliteException exception) when (
            exception.SqliteErrorCode == 5 /* SQLITE_BUSY */
            || exception.SqliteErrorCode == 6 /* SQLITE_LOCKED */)
        {
            return true;
        }
        finally
        {
            await connection.CloseAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>Kept forever. They appear only when the schema actually moves, so they are rare,
    /// and automatically deleting the artifact that exists to save you is not a decision this
    /// makes on an operator's behalf.</summary>
    private async Task<string> BackUpBeforeAsync(MigrationStep first, CancellationToken ct)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var directory = backupDirectory
            ?? Path.Combine(Path.GetDirectoryName(databasePath)!, "backups");

        var destination = Path.Combine(directory, $"messages-{stamp}-before-{first.Id}.db");

        // VACUUM INTO refuses a destination that already exists, and the stamp is only accurate to
        // the second - so two migrations of one database inside one second would collide and the
        // SECOND one would refuse to run at all, reported as "could not write the backup". That is
        // not a deployment, but it is exactly what an adoption test does, and a backup that stops a
        // migration because an earlier backup shares its name is the wrong trade: another copy is
        // cheap and a refused migration is not.
        for (var n = 2; File.Exists(destination); n++)
        {
            destination = Path.Combine(directory, $"messages-{stamp}-before-{first.Id}-{n}.db");
        }

        return await BackUpAsync(databasePath, destination, ct);
    }

    /// <summary>
    /// A connection with the pragmas this database needs.
    ///
    /// journal_mode is a property of the FILE and persists; foreign_keys and busy_timeout are
    /// per-CONNECTION and do not, which is why they are set here rather than being steps. A step
    /// runs once, and every connection opened after it would have foreign keys off - under which
    /// every ON DELETE CASCADE still parses, still applies as schema, and still deletes
    /// nothing.
    /// </summary>
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        // `Pooling=False`, like every other connection this assembly opens: a pooled handle is
        // process-wide state that another thread can dispose mid-migration.
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");

        await connection.OpenAsync(ct);

        // JOURNAL MODE IS THE MIGRATOR'S ALONE and stays here: it is a property of the FILE rather
        // than of a connection, so running it on every open would buy nothing. The per-connection
        // pragmas are shared, because one fact stored in eight places drifts into different
        // spellings — see `SqlitePragmas`.
        await using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode = WAL;";

            // THE ANSWER IS KEPT. `PRAGMA journal_mode` RETURNS the mode the file is now in, and
            // firing it blind and throwing the row away would make a file that did not enter WAL
            // look in every respect like one that did. It is read here rather than at the caller
            // because this is the only place the pragma is ever run.
            //
            // A pragma that cannot be read is NOT a migration failure and must not be dressed as
            // one: this records "(unknown)" and carries on, for the same reason the durability
            // setting's unparseable value does. Whether the answer is worth an alarm is the Host's
            // question, not the migrator's, and the migrator RETURNS rather than prints - the
            // console belongs to its caller.
            try
            {
                JournalMode = (await journal.ExecuteScalarAsync(ct))?.ToString();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                JournalMode = null;
            }
        }

        await SqlitePragmas.ApplyAsync(connection, durability: durability, ct: ct);

        return connection;
    }

    private static async Task EnsureLedgerAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();

        // The one table that cannot itself be a step, for the obvious reason.
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                id         TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<string>> ReadAppliedAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM schema_migrations";

        var applied = new HashSet<string>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));

        return applied;
    }

    private static async Task ApplyOneAsync(
        SqliteConnection connection, MigrationStep step, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);

        if (!await AlreadyDoneAsync(connection, transaction, step, ct))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = step.Sql;

            await command.ExecuteNonQueryAsync(ct);
        }

        await using var record = connection.CreateCommand();
        record.Transaction = (SqliteTransaction)transaction;
        record.CommandText =
            "INSERT INTO schema_migrations (id, applied_at) VALUES ($id, $at)";
        record.Parameters.AddWithValue("$id", step.Id);
        record.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));

        await record.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
    }

    private static async Task<bool> AlreadyDoneAsync(
        SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        MigrationStep step, CancellationToken ct)
    {
        if (step.SkipWhen is null) return false;

        await using var probe = connection.CreateCommand();
        probe.Transaction = (SqliteTransaction)transaction;
        probe.CommandText = step.SkipWhen;

        return Convert.ToInt64(await probe.ExecuteScalarAsync(ct)) > 0;
    }
}
