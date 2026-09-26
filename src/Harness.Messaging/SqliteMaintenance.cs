using Harness.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Messaging;

/// <summary>What <c>PRAGMA wal_checkpoint</c> answered: whether it was blocked, how many frames
/// the -wal held, and how many of them reached the database.</summary>
public readonly record struct CheckpointResult(bool Busy, long LogFrames, long CheckpointedFrames);

/// <summary>
/// Housekeeping on the database FILE rather than on any store's rows.
/// </summary>
public static class SqliteMaintenance
{
    /// <summary>
    /// <c>PRAGMA wal_checkpoint(TRUNCATE)</c>: copy the -wal into the database and cut the -wal back
    /// to zero bytes.
    ///
    /// SQLite's automatic checkpoint is PASSIVE and never shrinks the file, so on a busy instance the
    /// -wal only grows to its high-water mark and stays there. A reader holding an old snapshot makes
    /// the checkpoint answer busy rather than fail; that is reported, not thrown, and the next sweep
    /// tries again.
    /// </summary>
    public static async Task<CheckpointResult> CheckpointAsync(
        string databasePath, CancellationToken ct = default)
    {
        // Unpooled, for the reason BackUpAsync gives: a one-shot connection whose handle must not
        // outlive the call.
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");

        await connection.OpenAsync(ct);
        await SqlitePragmas.ApplyAsync(connection, ct: ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new CheckpointResult(reader.GetInt64(0) != 0, reader.GetInt64(1), reader.GetInt64(2));
    }
}
