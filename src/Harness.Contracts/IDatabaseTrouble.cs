namespace Harness.Contracts;

/// <summary>
/// TELLS DATABASE TROUBLE FROM ORDINARY TROUBLE, for the provider the Host runs on, so a request that
/// failed on a busy database is recorded as <see cref="DiagnosticKinds.DatabaseBusy"/> or
/// <see cref="DiagnosticKinds.DatabaseBusyTimeoutExpired"/> rather than as an unhandled exception.
///
/// <para>
/// AN INTERFACE BECAUSE EACH ENGINE SAYS "BUSY" IN ITS OWN NUMBERS: SQLite's result codes, SQL Server's
/// error numbers. Shared code asks this and names neither driver, so a deadlock on SQL Server is not
/// filed as <c>http.unhandled</c> because the only classifier knew SQLite's exception type.
/// </para>
/// </summary>
public interface IDatabaseTrouble
{
    /// <summary>The diagnostic kind for <paramref name="exception"/> or anything it wraps, or null when it
    /// is not about the database at all.</summary>
    string? KindOf(Exception? exception);
}
