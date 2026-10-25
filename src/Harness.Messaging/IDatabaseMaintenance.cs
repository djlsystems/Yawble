namespace Harness.Messaging;

/// <summary>
/// HOUSEKEEPING ON THE DATABASE ITSELF rather than on any store's rows. Only the checkpoint is called
/// through this today; the daily backup, the before-step backup and `VACUUM` stay on the SQLite file
/// helpers (`SchemaMigrator`) until a second provider needs them.
/// </summary>
public interface IDatabaseMaintenance
{
    /// <summary>Copies the write-ahead log into the database and cuts it back to nothing, reporting a
    /// blocked attempt rather than throwing so the next sweep tries again.</summary>
    Task<CheckpointResult> CheckpointAsync(CancellationToken ct = default);
}
