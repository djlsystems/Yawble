using Microsoft.Data.SqlClient;

namespace Harness.SqlServer.Host;

/// <summary>Refuses a start rather than letting two control Hosts serve one SQL Server database. See
/// <see cref="SqlServerControlLock"/>.</summary>
public sealed class ControlInUseException(string message) : Exception(message);

/// <summary>
/// ONE CONTROL PROCESS PER SQL SERVER DATABASE, which is what <c>DataRootLock</c> is on SQLite. Two
/// Hosts on two data roots share no file, so the data root's lock lets both start on one database, and
/// both would run the delivery pump, the sweeps, the wakes and retention over the same rows: every
/// delivery made twice. A serving Host holds this for its whole lifetime; another that names the same
/// database is refused, by one sentence, as a second Host on a held data root is.
///
/// <para>
/// THE SESSION IS THE FACT, as the handle is for the data root: an exclusive application lock owned by
/// the session (<c>sp_getapplock ... @LockOwner = 'Session'</c>) on a connection of its own, held open
/// for the life of the Host. A Host that is killed ends its session, and SQL Server releases the lock
/// with it, so a crash never leaves a lock that refuses every later start. The connection is unpooled,
/// so closing it ends the session rather than parking it in a pool still holding the lock, and it does
/// not reconnect by itself (<c>ConnectRetryCount=0</c>), because a session reopened behind the Host's
/// back would hold nothing while the Host believed it held the lock.
/// </para>
///
/// <para>
/// A CONNECTION THAT DROPS IS NOTICED AND THE LOCK TAKEN AGAIN. Every <see cref="DefaultCheckEvery"/> the
/// Host asks its own session whether it still holds the lock. When it does not (a failover, the session
/// killed, the network gone), the Host says so and takes it again on a new connection; while the server
/// cannot be reached it keeps trying. If another Host took the lock in the gap, this one is the second
/// Host and stops: <c>lost</c> is told, and the Host ends with a non-zero exit. A failover therefore
/// costs one line, never the instance, and two Hosts never go on serving one database.
/// </para>
///
/// <para>
/// Operator commands (<c>--reset-password</c>, <c>--list-users</c>, <c>--doctor</c> and the rest) never
/// take it, as they never take the data root's lock: they run before the Host takes either, and they
/// are what an operator runs beside a live Host.
/// </para>
/// </summary>
public sealed class SqlServerControlLock : IDisposable
{
    /// <summary>The lock's name, the same for every Host.</summary>
    public const string Resource = "host.control";

    /// <summary>How often a serving Host checks that its session still holds the lock.</summary>
    public static readonly TimeSpan DefaultCheckEvery = TimeSpan.FromSeconds(10);

    private readonly string _dedicated;
    private readonly string _server;
    private readonly Action<string> _say;
    private readonly Action<string> _lost;
    private readonly CancellationTokenSource _stop = new();
    private SqlConnection? _connection;
    private Task _watch = Task.CompletedTask;

    private SqlServerControlLock(string dedicated, string server, SqlConnection connection, Action<string> say, Action<string> lost)
    {
        _dedicated = dedicated;
        _server = server;
        _connection = connection;
        _say = say;
        _lost = lost;
    }

    /// <summary>The session holding the lock now, or null while it is being taken again; for tests.</summary>
    internal int? SessionId { get; private set; }

    /// <summary>
    /// Takes the database's control lock, or refuses with <see cref="ControlInUseException"/> naming the
    /// server and database when another session holds it. <paramref name="say"/> is told a line when the
    /// lock is lost and taken again; <paramref name="lost"/> when another Host took it, after which this
    /// Host must stop. A SqlClient fault reaching the server propagates as it is, for the start's own
    /// sentence about an unreachable server.
    /// </summary>
    public static async Task<SqlServerControlLock> AcquireAsync(
        string connectionString, Action<string> say, Action<string> lost,
        TimeSpan? checkEvery = null, CancellationToken ct = default)
    {
        var dedicated = Dedicated(connectionString);
        var server = Named(connectionString);

        var (connection, session) = await TakeAsync(dedicated, ct);

        if (connection is null) throw new ControlInUseException(InUse(server));

        var held = new SqlServerControlLock(dedicated, server, connection, say, lost) { SessionId = session };
        held._watch = held.WatchAsync(checkEvery ?? DefaultCheckEvery, held._stop.Token);
        return held;
    }

    /// <summary>
    /// The configured string through <see cref="SqlServerOpen.ConnectionString"/>, then made unpooled
    /// and non-reconnecting whatever it says: for this one connection the session is the lock, so
    /// neither the operator's pooling nor the driver's reconnect may stand between them.
    /// </summary>
    private static string Dedicated(string connectionString) =>
        new SqlConnectionStringBuilder(SqlServerOpen.ConnectionString(connectionString))
        {
            Pooling = false,
            ConnectRetryCount = 0,
        }.ConnectionString;

    // The server and database names only, never the string: it may hold a password.
    private static string Named(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return $"{builder.DataSource}/{builder.InitialCatalog}";
    }

    private static string InUse(string server) =>
        $"Another control Host is already running on the SQL Server database '{server}': one database, one "
        + "control process, since two would each deliver every message. Stop that Host, or give this "
        + "instance a database of its own.";

    // The lock on a new connection without waiting, with the session that holds it; no connection when
    // another session holds it.
    private static async Task<(SqlConnection? Connection, int Session)> TakeAsync(string dedicated, CancellationToken ct)
    {
        var connection = new SqlConnection(dedicated);

        try
        {
            await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                DECLARE @granted INT;
                EXEC @granted = sp_getapplock @Resource = N'{Resource}', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0;
                SELECT @granted, @@SPID;
                """;

            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);

            var granted = reader.GetInt32(0);
            var session = reader.GetInt16(1);

            // -1 is the timeout: another session holds it. Anything else below zero is the lock refused
            // for a reason of its own, which is a fault, not a second Host.
            if (granted == -1)
            {
                await connection.DisposeAsync();
                return (null, 0);
            }

            if (granted < 0) throw new InvalidOperationException($"The control lock could not be taken (sp_getapplock answered {granted}).");

            return (connection, session);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> StillHeldAsync(SqlConnection? connection, CancellationToken ct)
    {
        if (connection is null) return false;

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT APPLOCK_MODE(N'public', N'{Resource}', N'Session')";
            return Convert.ToString(await command.ExecuteScalarAsync(ct)) == "Exclusive";
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task WatchAsync(TimeSpan every, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(every);
        var unreachableSaid = false;

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (await StillHeldAsync(_connection, ct)) continue;

                if (_connection is not null)
                {
                    await _connection.DisposeAsync();
                    _connection = null;
                    SessionId = null;
                    _say($"The control lock on the SQL Server database '{_server}' was lost with its connection; taking it again.");
                }

                SqlConnection? again;
                int session;

                try
                {
                    (again, session) = await TakeAsync(_dedicated, ct);
                }
                catch (Exception exception) when (exception is SqlException or InvalidOperationException)
                {
                    // The server cannot be reached, so nothing else can be using it through this
                    // Host either. Said once, and tried again on every check until it answers.
                    if (!unreachableSaid) _say($"The control lock could not be taken again yet (SQL Server error {SqlServerOpen.ErrorNumber(exception)?.ToString() ?? "none"}); trying again.");
                    unreachableSaid = true;
                    continue;
                }

                if (again is null)
                {
                    _lost(
                        $"This Host lost its control lock on the SQL Server database '{_server}' and another control "
                        + "Host took it: one database, one control process. This Host stops.");
                    return;
                }

                _connection = again;
                SessionId = session;
                unreachableSaid = false;
                _say($"The control lock on the SQL Server database '{_server}' is held again.");
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed: the Host is stopping.
        }
    }

    /// <summary>Stops watching and ends the session, which releases the lock.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;

        _stop.Cancel();

        try
        {
            _watch.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The watch ends by cancellation; nothing it threw changes what is released here.
        }

        _connection?.Dispose();
        _connection = null;
        _stop.Dispose();
    }
}
