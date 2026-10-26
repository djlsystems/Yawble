using Harness.SqlServer.Host;
using Microsoft.Data.SqlClient;

namespace Harness.Tests;

/// <summary>
/// THE SQL SERVER CONTROL LOCK ON ITS OWN: one holder per database, released with its session, taken
/// again when its connection drops, and given up - the Host told to stop - when another holder took it
/// in the gap. The Host's own refusal of a second start is <c>SqlServerHostTests</c>.
/// </summary>
public sealed class SqlServerControlLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task A_second_holder_is_refused_and_the_lock_goes_with_the_first()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);

        using (var first = await SqlServerControlLock.AcquireAsync(database.ConnectionString, _ => { }, _ => { }, ct: Ct))
        {
            var refused = await Assert.ThrowsAsync<ControlInUseException>(
                () => SqlServerControlLock.AcquireAsync(database.ConnectionString, _ => { }, _ => { }, ct: Ct));

            Assert.Contains(database.Name, refused.Message, StringComparison.Ordinal);
        }

        using var second = await SqlServerControlLock.AcquireAsync(database.ConnectionString, _ => { }, _ => { }, ct: Ct);
        Assert.NotNull(second.SessionId);
    }

    [Fact]
    public async Task A_lock_whose_connection_drops_is_taken_again_on_a_new_session()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);
        var said = new List<string>();
        var lost = 0;

        using var held = await SqlServerControlLock.AcquireAsync(
            database.ConnectionString, line => { lock (said) said.Add(line); }, _ => Interlocked.Increment(ref lost), Quick, Ct);

        await DropSessionsAsync(database);

        // A session id is reused by the server, so the line is what says the lock was taken again.
        await Until(() => { lock (said) return said.Any(line => line.Contains("held again", StringComparison.Ordinal)); });

        Assert.Equal(0, lost);
        Assert.NotNull(held.SessionId);
        lock (said) Assert.Contains(said, line => line.Contains("was lost with its connection", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ControlInUseException>(
            () => SqlServerControlLock.AcquireAsync(database.ConnectionString, _ => { }, _ => { }, ct: Ct));
    }

    [Fact]
    public async Task A_lock_another_took_while_the_connection_was_down_tells_the_host_to_stop()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);
        string? stopped = null;

        using var held = await SqlServerControlLock.AcquireAsync(
            database.ConnectionString, _ => { }, sentence => Volatile.Write(ref stopped, sentence), TimeSpan.FromSeconds(2), Ct);

        // Dropped, and taken by another session before the holder's next check.
        await DropSessionsAsync(database);
        using var other = await SqlServerControlLock.AcquireAsync(database.ConnectionString, _ => { }, _ => { }, ct: Ct);

        await Until(() => Volatile.Read(ref stopped) is not null);

        Assert.Contains("another control Host took it", stopped, StringComparison.Ordinal);
        Assert.Null(held.SessionId);
    }

    // EVERY SESSION ON THE DATABASE ENDED, as a failover ends them: the database is taken to single
    // user with rollback, from master, and given back. The test login owns the database it created,
    // which is what ALTER DATABASE needs; KILL would need a server-wide permission it may not have.
    private static async Task DropSessionsAsync(SqlServerTestDatabase database)
    {
        var master = new SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        // The name is the test database's own, generated from a GUID.
        command.CommandText =
            $"ALTER DATABASE [{database.Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ALTER DATABASE [{database.Name}] SET MULTI_USER;";
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(50, Ct);
        }
    }
}
