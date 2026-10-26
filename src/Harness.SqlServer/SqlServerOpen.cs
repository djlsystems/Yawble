using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Harness.SqlServer;

/// <summary>
/// THE ONE CONNECTION STRING EVERY SQL SERVER STORE OPENS WITH, and the one retry around a call
/// that can meet a deadlock or a transient fault, which never runs again a unit whose commit may
/// have reached the server.
///
/// <para>
/// The SQLite stores open unpooled because a file handle is process state; SQL Server is the
/// opposite, where a pooled connection is the cheap path and an unpooled one costs a login per call.
/// So pooling is on, and the driver is told to reconnect (<c>ConnectRetryCount=3</c>) when a pooled
/// connection turns out to be dead, which a failover or a firewall idle-timeout makes routine.
/// Either is left alone when the operator's own string sets it - their word on their server wins.
/// </para>
///
/// <para>
/// Managed identity is the connection string's own <c>Authentication=...</c>; nothing here reads a
/// credential.
/// </para>
/// </summary>
public static class SqlServerOpen
{
    private const int Attempts = 3;

    /// <summary>
    /// The configured string with retry and pooling defaults added where it does not say. Checked
    /// against the raw keys rather than the typed builder, whose indexer answers a default for a key
    /// nobody wrote and so cannot tell "unset" from "set to the default".
    /// </summary>
    public static string ConnectionString(string configured)
    {
        var written = new DbConnectionStringBuilder { ConnectionString = configured };
        var builder = new SqlConnectionStringBuilder(configured);

        if (!written.ContainsKey("ConnectRetryCount") && !written.ContainsKey("Connect Retry Count"))
        {
            builder.ConnectRetryCount = 3;
        }

        if (!written.ContainsKey("Pooling"))
        {
            builder.Pooling = true;
        }

        return builder.ConnectionString;
    }

    /// <summary>SQL Server's own error number for <paramref name="exception"/>, or null when it is not a
    /// SQL Server error: what a sentence naming a database fault says on this provider.</summary>
    public static int? ErrorNumber(Exception exception) => exception is SqlException sql ? sql.Number : null;

    /// <summary>The error number SQL Server gives a deadlock victim: its transaction was rolled back
    /// and running the work again is the documented remedy.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>
    /// AN APPLICATION LOCK'S DEADLOCK VICTIM, as the stores' own <c>THROW</c> spells it.
    /// <c>sp_getapplock</c> answers -3 rather than raising 1205 when its request is chosen as the
    /// victim, so a store that takes one throws this number for that answer alone. It is retried as
    /// 1205 is: the lock is taken before anything in its transaction commits, and the transaction is
    /// rolled back as the unit unwinds, so running the unit again cannot apply anything twice.
    /// </summary>
    public const int LockDeadlockVictim = 51205;

    /// <summary>
    /// THE FAULTS A STORE RUNS AGAIN, beside a deadlock victim: Microsoft's documented transient
    /// errors for Azure SQL Database ("Troubleshoot transient connection errors"), which a
    /// reconfiguration, a failover or throttling answers, and the connection-level errors (a broken
    /// or reset transport) that SqlClient's own configurable retry logic and EF Core's SQL Server
    /// detector treat as transient. SqlClient 6 marks none of them with <c>IsTransient</c>, which is
    /// why this is a list.
    ///
    /// <para>
    /// LEFT OUT ON PURPOSE: a command timeout (-2) and a lock timeout (1222). Both mean the database
    /// was busy for seconds and are recorded as <c>db.busy-timeout-expired</c> rather than hidden by
    /// a retry (<see cref="SqlServerTrouble"/>), and a command that timed out may still complete on
    /// the server.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlySet<int> TransientFaults = new HashSet<int>
    {
        4060,   // the database named in the login cannot be opened (it is being moved or brought online)
        4221,   // a login to a readable secondary waited out the database's change of role
        10928,  // a resource limit was reached; the request was refused
        10929,  // the minimum guarantee for this database cannot be met right now; the request was refused
        40197,  // the service hit an error processing the request (a reconfiguration or failover)
        40501,  // the service is busy
        40613,  // the database is not currently available (a reconfiguration or failover)
        49918,  // not enough resources to process the request
        49919,  // too many create or update operations in progress
        49920,  // too many operations in progress
        64,     // the connection was dropped by the remote host
        233,    // no process is on the other end of the pipe: the server closed the connection
        10053,  // the connection was aborted by the local host
        10054,  // the connection was reset by the remote host
        10060,  // the connection attempt timed out
    };

    /// <summary>
    /// WHETHER A FAULT IS ONE A STORE MAY RUN AGAIN AT ALL: a deadlock victim (1205, or
    /// <see cref="LockDeadlockVictim"/>) or one of <see cref="TransientFaults"/>. WHEN it may is
    /// <see cref="Retries"/>: a transient fault is never retried once a commit may have reached the
    /// server.
    /// </summary>
    internal static bool IsRetryable(Exception exception) => IsDeadlock(exception) || IsTransient(exception);

    private static bool IsDeadlock(Exception exception) =>
        exception is SqlException { Number: DeadlockVictim or LockDeadlockVictim };

    private static bool IsTransient(Exception exception) =>
        exception is SqlException sql && TransientFaults.Contains(sql.Number);

    /// <summary>
    /// How far one attempt at a unit of work has got with the server, which decides whether a
    /// transient fault may be retried. It only ever moves forward.
    /// </summary>
    internal enum Sent
    {
        /// <summary>The connection is not open yet: nothing has reached the server.</summary>
        Nothing,

        /// <summary>The connection is open and statements may have run outside a transaction, each
        /// committing itself: a write among them may have landed.</summary>
        Statements,

        /// <summary>Inside a transaction (<see cref="BeginAsync"/>) whose commit has not been sent:
        /// a fault leaves nothing of it applied.</summary>
        Transaction,

        /// <summary>The commit has been sent (<see cref="CommitAsync"/>): it may have landed even if
        /// its answer never arrives.</summary>
        Commit,
    }

    /// <summary>
    /// THE ONE RETRY POLICY. A deadlock victim is retried wherever it happens: SQL Server rolled its
    /// transaction back. A transient fault is retried only when nothing the attempt sent can have
    /// committed - before the connection opened, or inside a transaction whose commit was not yet
    /// sent. A fault on the commit itself, or after a statement that commits on its own, is thrown:
    /// the commit may have landed with its answer lost, and the units are not idempotent (an append
    /// run again would store the message, and its ledger rows, twice).
    /// </summary>
    internal static bool Retries(Exception exception, Sent sent) =>
        IsDeadlock(exception) || IsTransient(exception) && sent is Sent.Nothing or Sent.Transaction;

    // The attempt in progress on this flow, so a store's begin and commit can say how far it got
    // without every store carrying the state. Set per attempt by the loop below, and an object, so a
    // mark made deeper in the call is seen by the loop when the call throws.
    private sealed class Attempt(Sent sent)
    {
        public Sent Sent { get; set; } = sent;
    }

    private static readonly AsyncLocal<Attempt?> Current = new();

    internal static void Mark(Sent sent)
    {
        if (Current.Value is { } attempt && sent > attempt.Sent) attempt.Sent = sent;
    }

    /// <summary>
    /// BEGINS A UNIT'S TRANSACTION, the only way a SQL Server store begins one, so the retry knows a
    /// fault from here until the commit leaves nothing applied. A unit begins its transaction before
    /// its first statement: one that ran a write on its own first would have that write run again.
    /// </summary>
    public static async Task<SqlTransaction> BeginAsync(SqlConnection connection, CancellationToken ct)
    {
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        Mark(Sent.Transaction);
        return transaction;
    }

    /// <summary>
    /// COMMITS A UNIT'S TRANSACTION, the only way a SQL Server store commits one. Marked BEFORE the
    /// commit is sent, so a fault while it is in flight - the case where it may have landed - is never
    /// retried as a transient one.
    /// </summary>
    public static Task CommitAsync(SqlTransaction transaction, CancellationToken ct)
    {
        Mark(Sent.Commit);
        return transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/>, trying again (three attempts in all) as <see cref="Retries"/>
    /// allows. The work opens its own connection, so a transient fault is retried only inside a
    /// transaction begun with <see cref="BeginAsync"/> and not yet committed; a deadlock victim
    /// always. Anything else, and the last retryable one, propagates unchanged.
    ///
    /// Whole units of work only: the caller passes a call that opens its own connection and, if it
    /// writes, does so in one transaction, so running it again cannot apply half of it twice.
    /// </summary>
    public static Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) =>
        RetryAsync(work, ct, Attempts, Retries, TimeSpan.FromMilliseconds(200), Sent.Statements);

    /// <summary>
    /// ONE UNIT OF WORK ON ITS OWN POOLED CONNECTION, retried whole as <see cref="Retries"/> allows:
    /// the shape every SQL Server store's call takes, here once so no store keeps its own copy. A
    /// transient fault while the connection opens is retried, as is one inside a transaction begun
    /// with <see cref="BeginAsync"/> before its <see cref="CommitAsync"/>; a deadlock victim always.
    /// A transient fault on a statement outside a transaction is thrown, since it may have committed.
    /// <paramref name="connectionString"/> is the store's, already through <see cref="ConnectionString"/>.
    /// </summary>
    public static Task<T> RunAsync<T>(
        string connectionString, Func<SqlConnection, CancellationToken, Task<T>> work, CancellationToken ct) =>
        RetryAsync(
            async token =>
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(token);
                Mark(Sent.Statements);
                return await work(connection, token);
            },
            ct, Attempts, Retries, TimeSpan.FromMilliseconds(200), Sent.Nothing);

    /// <summary>One statement as one unit of work: the rows it changed.</summary>
    public static Task<int> ExecuteAsync(
        string connectionString, string sql, Action<SqlCommand> bind, CancellationToken ct) =>
        RunAsync(
            connectionString,
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                bind(command);
                return await command.ExecuteNonQueryAsync(token);
            },
            ct);

    /// <summary>
    /// The loop with its policy as arguments, so a test can pin the attempt cap, the give-up-at-once
    /// behaviour and the commit boundary without waiting out real delays. The public overloads are
    /// the only callers outside tests and pass the production <see cref="Retries"/>.
    /// <paramref name="first"/> is how far an attempt counts as having got before the work marks
    /// anything: <see cref="Sent.Nothing"/> where the loop opens the connection itself.
    /// </summary>
    internal static async Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> work, CancellationToken ct,
        int attempts, Func<Exception, Sent, bool> retryable, TimeSpan delayStep, Sent first = Sent.Statements)
    {
        for (var attempt = 1; ; attempt++)
        {
            var progress = new Attempt(first);
            Current.Value = progress;

            try
            {
                return await work(ct);
            }
            catch (Exception exception) when (retryable(exception, progress.Sent) && attempt < attempts)
            {
                await Task.Delay(delayStep * attempt, ct);
            }
        }
    }
}
