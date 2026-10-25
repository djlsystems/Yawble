using System.Data.Common;
using Harness.SqlServer;
using Microsoft.Data.Sqlite;

namespace Harness.Host;

/// <summary>
/// THE ENGINE'S OWN NUMBER FOR A DATABASE FAULT, for the sentence that names it: SQLite's result code,
/// as these sentences have always said, or SQL Server's error number (<see cref="SqlServerOpen.ErrorNumber"/>).
/// The request paths that turn a fault into a sentence catch <see cref="DbException"/>, which both
/// providers' exceptions derive from, so a fault on SQL Server is the same sentence and not a 500 page.
/// </summary>
public static class DatabaseFault
{
    public static int Code(DbException exception) =>
        exception is SqliteException sqlite ? sqlite.SqliteErrorCode : SqlServerOpen.ErrorNumber(exception) ?? exception.ErrorCode;
}
