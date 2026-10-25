using Harness.Messaging;

namespace Harness.SqlServer.Host;

/// <summary>
/// The SQL Server version of <see cref="IDatabaseMaintenance"/>: nothing to do, and said. SQL Server has
/// no write-ahead file for the Host to cut back, so the checkpoint after a quiet sweep is a no-op that
/// reports nothing copied and nothing busy; and the database is not a file this Host can copy, so the
/// daily copy takes none and every caller says <see cref="PointInTimeRestore"/> in its place.
/// </summary>
public sealed class SqlServerDatabaseMaintenance : IDatabaseMaintenance
{
    /// <summary>What backs a SQL Server database up, said wherever a SQLite copy would have been taken.</summary>
    public const string PointInTimeRestore = "point-in-time restore is the backup on SQL Server.";

    public Task<CheckpointResult> CheckpointAsync(CancellationToken ct = default) =>
        Task.FromResult(new CheckpointResult(Busy: false, LogFrames: 0, CheckpointedFrames: 0));

    public string? BackupElsewhere => PointInTimeRestore;

    public Task<string?> CopyToAsync(string destination, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
