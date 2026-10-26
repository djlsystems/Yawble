using Harness.Contracts;
using Harness.Messaging;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer;

/// <summary>
/// The database compares names without regard to case, so a table, a column or a key that the
/// SQLite stores treat as two different things would be one here.
///
/// Its own type for the reason the migrator's other refusals are: the Host must be able to say
/// "create the database with this collation" instead of printing a stack trace, and the sentence
/// has to name the collation that was found so the operator can see what they have.
/// </summary>
public sealed class WrongCollationException(string actual)
    : InvalidOperationException(
        $"This database's collation is '{actual}', but the Host needs '{SqlServerMigrator.RequiredCollation}' "
        + "so that names compare by exact code point, as they do everywhere else Harness runs. Create "
        + $"the database with COLLATE {SqlServerMigrator.RequiredCollation} and start again. Nothing "
        + "was changed in this one.")
{
    public string Actual { get; } = actual;
}

/// <summary>
/// The database runs READ COMMITTED with locks, so a reader waits on a writer and two writers that
/// read before they write can each wait on the other: the stores' concurrency was written and
/// proven under row versioning (READ_COMMITTED_SNAPSHOT ON), which Azure SQL Database has on and a
/// SQL Server installed on premises has off.
///
/// A type of its own beside <see cref="WrongCollationException"/> rather than a generalisation of it:
/// the fix is a different statement, and the sentence has to name this database so the operator can
/// run it as written.
/// </summary>
public sealed class ReadCommittedSnapshotOffException(string database)
    : InvalidOperationException(
        $"This database ('{database}') has READ_COMMITTED_SNAPSHOT off, but the Host needs it on: its "
        + "stores are written for readers that never wait on a writer. Run "
        + $"ALTER DATABASE [{database}] SET READ_COMMITTED_SNAPSHOT ON (it waits for other connections "
        + "to the database to close) and start again. Nothing was changed in this one.")
{
    public string Database { get; } = database;
}

/// <summary>
/// Another start or operator command held this database's migrate lock for longer than one apply may
/// wait. Its own type so the start and the operator commands say it as one sentence.
/// </summary>
public sealed class SchemaBusyException()
    : InvalidOperationException(
        "Another start or operator command has been moving this database's schema forward for longer than "
        + "this one will wait. Let it finish, or stop it, and start again. Nothing was changed by this one.");

/// <summary>
/// The one thing that moves a SQL Server database's schema forward, mirroring the SQLite
/// <see cref="SchemaMigrator"/> step for step: an ordered list, a ledger of what has run, a refusal
/// of a database that has run something this build does not know.
///
/// Differences that follow from the engine, not from taste: there is no file to copy first, so no
/// backup is taken here (a point-in-time restore is the backup, and <see cref="ApplyAsync"/> says so
/// by returning null); the database's collation is checked BEFORE the ledger exists, because the
/// ledger's own primary key would otherwise be created under the wrong rules; and so is row
/// versioning (READ_COMMITTED_SNAPSHOT), which every store's concurrency is written for.
/// </summary>
public sealed class SqlServerMigrator(string connectionString)
{
    /// <summary>
    /// Binary ordering on UTF-8: names compare by code point, as SQLite's default BINARY does.
    /// The only place this is spelled; the test database and the error both read it from here.
    /// </summary>
    public const string RequiredCollation = "Latin1_General_100_BIN2_UTF8";

    // Lower case because under the required collation identifiers are case-sensitive, so the ledger
    // must be addressed by exactly these spellings everywhere.
    private const string Ledger = "schema_steps";

    /// <summary>The application lock one apply holds from before the ledger to its last step.</summary>
    public const string MigrateLock = "schema.migrate";

    // Long enough for any real apply to finish; a wait past it is a stuck holder, said as a sentence.
    private const int MigrateLockWaitMilliseconds = 5 * 60 * 1000;

    /// <summary>
    /// Applies every step not already recorded, in the order given. Always returns null: there is no
    /// file to copy before a migration, so no backup path exists to hand back.
    ///
    /// Each step runs inside a transaction that ALSO writes its id, so a crash between the statement
    /// and the record cannot leave a step applied but unrecorded. SQL Server DDL is transactional.
    ///
    /// <para>
    /// INVARIANT, as for the SQLite migrator: <paramref name="steps"/> must be a SUPERSET of everything
    /// ever applied here. A partial list is indistinguishable from a database written by a newer build
    /// and throws <see cref="SchemaFromTheFutureException"/>.
    /// </para>
    /// </summary>
    public async Task<string?> ApplyAsync(IReadOnlyList<MigrationStep> steps, CancellationToken ct = default)
    {
        Applied = 0;

        await using var connection = await OpenAsync(ct);

        await EnsureCollationAsync(connection, ct);
        await EnsureReadCommittedSnapshotAsync(connection, ct);

        // ONE APPLY AT A TIME, from before the ledger is created to the last step: a Host start and an
        // operator command on a fresh database would otherwise both read an empty ledger, and the second
        // fail on the first's table or record. The second waits, then reads the ledger the first wrote.
        await TakeMigrateLockAsync(connection, ct);

        try
        {
            await ApplyHeldAsync(connection, steps, ct);
        }
        finally
        {
            await ReleaseMigrateLockAsync(connection);
        }

        return null;
    }

    private async Task ApplyHeldAsync(SqlConnection connection, IReadOnlyList<MigrationStep> steps, CancellationToken ct)
    {
        await EnsureLedgerAsync(connection, ct);

        var applied = await ReadAppliedAsync(connection, ct);

        var known = steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = applied.Where(id => !known.Contains(id)).Order(StringComparer.Ordinal).ToArray();

        // BEFORE any step runs: a downgrade that applied half of what it knew and then noticed would
        // leave a database neither build has a name for.
        if (unknown.Length > 0) throw new SchemaFromTheFutureException(unknown);

        // Wrapped here, where the step id is known, rather than by a catch-all up in the Host that
        // would report an unrelated failure as a schema problem. Cancellation passes through: a
        // shutdown is not a migration failure.
        foreach (var step in steps.Where(s => !applied.Contains(s.Id)))
        {
            try
            {
                await ApplyOneAsync(connection, step, ct);
                Applied++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new MigrationFailedException(step.Id, exception);
            }
        }
    }

    /// <summary>
    /// How many steps the last <see cref="ApplyAsync"/> applied: what the Host reads, since there is no
    /// backup path to tell it whether the schema moved, to say so once when it did.
    /// </summary>
    public int Applied { get; private set; }

    /// <summary>
    /// Every step id this database has taken, ordered by id, WRITING NOTHING: an empty list where the
    /// ledger does not exist yet, rather than creating it. What the doctor reads, which reports a database and never changes it. The collation and row versioning
    /// are still checked first, so a database the Host would refuse is refused here with the same
    /// sentence.
    /// </summary>
    public async Task<IReadOnlyList<string>> AppliedIfAnyAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await EnsureCollationAsync(connection, ct);
        await EnsureReadCommittedSnapshotAsync(connection, ct);

        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = $"SELECT CASE WHEN OBJECT_ID(N'{Ledger}', N'U') IS NULL THEN 0 ELSE 1 END";
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) == 0) return [];
        }

        return [.. (await ReadAppliedAsync(connection, ct)).Order(StringComparer.Ordinal)];
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(SqlServerOpen.ConnectionString(connectionString));

        try
        {
            await connection.OpenAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }

    private static async Task EnsureCollationAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(NVARCHAR(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))";

        var actual = Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? "";

        if (!string.Equals(actual, RequiredCollation, StringComparison.OrdinalIgnoreCase))
        {
            throw new WrongCollationException(actual);
        }
    }

    /// <summary>
    /// Refused BEFORE the ledger, as the collation is: a database the stores cannot run on correctly
    /// is left exactly as it was found, so the operator's one statement is the whole fix.
    /// </summary>
    private static async Task EnsureReadCommittedSnapshotAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DB_NAME(), CAST(is_read_committed_snapshot_on AS INT) FROM sys.databases WHERE database_id = DB_ID()";

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        if (reader.GetInt32(1) == 0) throw new ReadCommittedSnapshotOffException(reader.GetString(0));
    }

    /// <summary>
    /// Owned by the session, not a transaction, because each step commits in a transaction of its own
    /// and the lock must outlive them all. Released explicitly: the connection is pooled, and a pooled
    /// session that is only closed keeps its locks until the pool reuses it.
    /// </summary>
    private static async Task TakeMigrateLockAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            $"""
            DECLARE @granted INT;
            EXEC @granted = sp_getapplock @Resource = N'{MigrateLock}', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = {MigrateLockWaitMilliseconds};
            SELECT @granted;
            """;

        var granted = Convert.ToInt32(await command.ExecuteScalarAsync(ct));

        if (granted == -1) throw new SchemaBusyException();
        if (granted < 0) throw new InvalidOperationException($"The schema's migrate lock could not be taken (sp_getapplock answered {granted}).");
    }

    private static async Task ReleaseMigrateLockAsync(SqlConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"EXEC sp_releaseapplock @Resource = N'{MigrateLock}', @LockOwner = N'Session';";
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            // A connection that broke took its session, and the lock, with it.
        }
    }

    private static async Task EnsureLedgerAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();

        // The one table that cannot itself be a step, for the obvious reason.
        command.CommandText =
            $"""
            IF OBJECT_ID(N'{Ledger}', N'U') IS NULL
            CREATE TABLE {Ledger} (
                id         NVARCHAR(200) NOT NULL PRIMARY KEY,
                applied_at NVARCHAR(40)  NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<string>> ReadAppliedAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM {Ledger}";

        var applied = new HashSet<string>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));

        return applied;
    }

    private static async Task ApplyOneAsync(SqlConnection connection, MigrationStep step, CancellationToken ct)
    {
        // Disposed without a commit means rolled back, which is what a failing statement relies on.
        await using var transaction = await SqlServerOpen.BeginAsync(connection, ct);

        if (!await AlreadyDoneAsync(connection, transaction, step, ct))
        {
            // The whole Sql is ONE batch, so a step may hold several statements. There is no GO.
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = step.Sql;

            await command.ExecuteNonQueryAsync(ct);
        }

        await using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = $"INSERT INTO {Ledger} (id, applied_at) VALUES (@id, @at)";
        record.Parameters.AddWithValue("@id", step.Id);
        record.Parameters.AddWithValue("@at", Instant.Now());

        await record.ExecuteNonQueryAsync(ct);

        await SqlServerOpen.CommitAsync(transaction, ct);
    }

    private static async Task<bool> AlreadyDoneAsync(
        SqlConnection connection, SqlTransaction transaction, MigrationStep step, CancellationToken ct)
    {
        if (step.SkipWhen is null) return false;

        await using var probe = connection.CreateCommand();
        probe.Transaction = transaction;
        probe.CommandText = step.SkipWhen;

        return Convert.ToInt64(await probe.ExecuteScalarAsync(ct)) > 0;
    }
}
