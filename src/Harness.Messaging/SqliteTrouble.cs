using Microsoft.Data.Sqlite;
using Harness.Contracts;

namespace Harness.Messaging;

/// <summary>
/// TELLS DATABASE TROUBLE FROM ORDINARY TROUBLE, so a diagnostic row can say which.
///
/// <para>
/// <b>IT LIVES HERE BECAUSE THE HOST NAMES NO `Microsoft.Data.Sqlite` TYPE.</b> That rule is what
/// keeps a database driver off the composition root, and the way every other piece of this obeys it
/// is the same: the module that owns the file publishes a DATA answer and the Host reads it -
/// `MigrationStep` in `Contracts`, the steps as lists, and now an error code as a
/// <see cref="DiagnosticKinds"/> constant. The Host calls an Harness type; the driver stays on
/// this side of the line.
/// </para>
///
/// <para>
/// This project rather than `Harness.Identity` for the reason `SchemaMigrator` is here: this one
/// owns the file, the connection and the pragmas, and already reads `SqliteErrorCode` by number.
/// </para>
/// </summary>
public static class SqliteTrouble
{
    /// <summary>SQLITE_BUSY. Another connection holds a write lock.</summary>
    private const int Busy = 5;

    /// <summary>SQLITE_LOCKED. A table in this same connection is locked - the sibling of the
    /// above, and the same thing to whoever is reading a diagnostics screen.</summary>
    private const int Locked = 6;

    /// <summary>
    /// SQLITE_BUSY_TIMEOUT (0x0107). The EXTENDED code SQLite gives when the wait configured by
    /// `PRAGMA busy_timeout` was used up rather than when a lock was merely found held.
    /// </summary>
    private const int BusyTimeout = 261;

    /// <summary>
    /// The diagnostic kind for <paramref name="exception"/>, or null when it is not about the
    /// database at all.
    ///
    /// <para>
    /// WALKS THE INNER CHAIN, because by the time a busy database reaches a route it is usually
    /// wrapped - `MigrationFailedException` does it deliberately, and so does anything that adds
    /// context on the way up. Checking only the outermost type answers "not a database problem" for
    /// most of the cases this exists for.
    /// </para>
    ///
    /// <para>
    /// THE TWO KINDS ARE WORTH SEPARATING. Every connection this product opens carries
    /// `busy_timeout = 5000` (see <see cref="SqlitePragmas"/>), so contention normally never
    /// surfaces at all - the driver blocks and then succeeds. A row saying the timeout EXPIRED
    /// means the contention lasted seconds, which is a different problem with the same error code:
    /// one is a busy moment, the other is something holding a write lock far longer than anything
    /// here should.
    /// </para>
    /// </summary>
    public static string? KindOf(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not SqliteException sqlite) continue;

            if (sqlite.SqliteExtendedErrorCode == BusyTimeout)
            {
                return DiagnosticKinds.DatabaseBusyTimeoutExpired;
            }

            if (sqlite.SqliteErrorCode is Busy or Locked) return DiagnosticKinds.DatabaseBusy;
        }

        return null;
    }
}
