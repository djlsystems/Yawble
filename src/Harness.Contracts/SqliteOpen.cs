namespace Harness.Contracts;

/// <summary>
/// THE ONE CONNECTION STRING EVERY STORE OPENS WITH. Thirty stores each built their own; two of
/// them opened with the shared cache, under which readers and the writer conflict (SQLITE_LOCKED)
/// and the driver sleeps a thread 150 ms at a time to retry, and some opened with pooling, which
/// keeps the native handle alive for the process-wide pool's lifetime so another finishing
/// caller's <c>ClearAllPools</c> disposes it under a running call.
///
/// <para>
/// So: <c>Pooling=False</c>, no cache mode (each connection its own page cache, which is what WAL
/// readers want), and <c>Mode=ReadWrite</c> only for a probe that must not create a file. The
/// pragmas are per connection and applied by <see cref="SqlitePragmas"/> right after the open;
/// every store does both, and <c>SqliteOpenTests</c> scans for one that does not.
/// </para>
/// </summary>
public static class SqliteOpen
{
    /// <summary>The connection string for <paramref name="databasePath"/>; <paramref name="readOnly"/> refuses to create a missing file.</summary>
    public static string ConnectionString(string databasePath, bool readOnly = false) =>
        $"Data Source=\"{databasePath.Replace("\"", "\"\"", StringComparison.Ordinal)}\";Pooling=False"
        + (readOnly ? ";Mode=ReadWrite" : "");
}
