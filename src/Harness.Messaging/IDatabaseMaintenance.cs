namespace Harness.Messaging;

/// <summary>
/// HOUSEKEEPING ON THE DATABASE ITSELF rather than on any store's rows: the checkpoint after a quiet
/// sweep, and the consistent copy the daily backup takes. A provider whose database is not a file this
/// Host copies (SQL Server) answers each with a no-op and says what backs the database up instead
/// (<see cref="BackupElsewhere"/>), so a caller reports that rather than a copy it never took.
///
/// <para>
/// The copy before a schema step stays inside each provider's migrator, and the operator's
/// <c>--backup</c> and <c>--restore</c> on the SQLite file helpers (<c>SchemaMigrator.BackUpAsync</c>):
/// neither runs through the Host's composition.
/// </para>
/// </summary>
public interface IDatabaseMaintenance
{
    /// <summary>Copies the write-ahead log into the database and cuts it back to nothing, reporting a
    /// blocked attempt rather than throwing so the next sweep tries again.</summary>
    Task<CheckpointResult> CheckpointAsync(CancellationToken ct = default);

    /// <summary>
    /// NULL WHERE THIS HOST COPIES THE DATABASE TO A FILE ITSELF (SQLite: the daily copy, the copy before
    /// a schema step). Otherwise the sentence naming what backs the database up instead, which every
    /// place a copy would have been taken says in its stead.
    /// </summary>
    string? BackupElsewhere { get; }

    /// <summary>
    /// A consistent copy of the whole database at <paramref name="destination"/>, answered as the path
    /// written: SQLite's <c>VACUUM INTO</c>. Null, writing nothing, where <see cref="BackupElsewhere"/>
    /// says why.
    /// </summary>
    Task<string?> CopyToAsync(string destination, CancellationToken ct = default);
}
