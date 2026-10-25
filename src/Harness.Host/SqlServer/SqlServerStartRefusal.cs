using Harness.Messaging;
using Harness.SqlServer;
using Microsoft.Data.SqlClient;

namespace Harness.Host.SqlServer;

/// <summary>
/// WHAT A SQLSERVER START SAYS WHEN IT CANNOT START: one line for the operator, for every failure the
/// start's schema step can meet, or null for one it does not know (which then fails as any other bug
/// does). The migrator's own refusals already are that line - a newer build's steps, a failed step, the
/// wrong collation, row versioning off - and are said as they are; a server that cannot be reached or
/// opened, and a connection string SqlClient cannot read, are said here, because SqlClient's own text
/// for them is a paragraph and a stack trace reads as "the Host crashed".
///
/// <para>
/// NEVER THE CONNECTION STRING: it may hold a password. The server's name is said, because that is the
/// part an operator mistypes and nothing secret is in it.
/// </para>
/// </summary>
public static class SqlServerStartRefusal
{
    public static string? Sentence(Exception exception, string connection) => exception switch
    {
        SchemaFromTheFutureException
            or MigrationFailedException
            or WrongCollationException
            or ReadCommittedSnapshotOffException => exception.Message,

        SqlException sql =>
            $"The SQL Server database at '{Server(connection)}' could not be reached or opened (SQL Server error {sql.Number}): "
            + "check the server, the network and the login that HARNESS_DATABASE_CONNECTION names, and start again.",

        // A FIXED SENTENCE, NO SQLCLIENT TEXT. SqlClient's message names the "keyword" it could not read,
        // and in a mis-quoted string (a password holding `;x=`) that keyword is a piece of the password.
        ArgumentException => Unreadable,

        _ => null,
    };

    /// <summary>What an unreadable connection string is said as, wherever it is said: the start, the
    /// operator switches and the doctor. Nothing of the string itself is in it.</summary>
    public const string Unreadable =
        "The SQL Server connection string (HARNESS_DATABASE_CONNECTION) could not be read; check its keywords and quoting.";

    /// <summary>Whether the failure is one the database itself answered, so a diagnostics row can be
    /// written to it as the SQLite start writes one: a step from a newer build, or a step that failed.</summary>
    public static bool IsRecorded(Exception exception) =>
        exception is SchemaFromTheFutureException or MigrationFailedException;

    private static string Server(string connection)
    {
        try
        {
            return new SqlConnectionStringBuilder(connection).DataSource;
        }
        catch (ArgumentException)
        {
            return "(unreadable)";
        }
    }
}
