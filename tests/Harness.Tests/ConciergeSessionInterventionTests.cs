using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Harness.Pty;
using Harness.Tests.Contracts;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// A CONCIERGE SESSION IS ONE INTERVENTION, measured open to close: one <c>concierge_session</c> row per
/// session, however it ended, written when it ends, with <c>duration_measured</c> 1. An explicit end or
/// the CLI's own exit closes it then; a session the reaper or the Host's shutdown ended closes at its
/// last activity, so the unattended time is not counted as a person's. The chat inside it is no row of
/// its own. Time is one hand-moved clock.
/// </summary>
public sealed class ConciergeSessionInterventionTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly ConciergeSessionKey Person = new("user-1");

    private readonly SqliteContractDatabase _database = new();
    private readonly ManualTime _clock = new(Start);
    private readonly FakePtyEngine _engine = new();
    private ConciergeSessionStore? _store;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ConciergeSessionStore Store => _store ??= new ConciergeSessionStore(
        _engine,
        (_, _, _, _) => Task.FromResult(new PtySpec("fixture-cli", Path.GetTempPath())),
        (_, _) => Task.CompletedTask,
        _clock,
        ended: ConciergeSessionRecord.Writing(new SqliteInterventionLedger(_database.Path)));

    [Fact]
    public async Task A_session_a_person_ends_is_one_row_measured_from_its_open_and_its_chat_is_no_row_of_its_own()
    {
        var session = await Store.AttachAsync(Person, "Alpha", 80, 24, Ct);
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(2));
            session.Session.Write("tell the Manager something\r"u8);
        }

        _clock.Advance(TimeSpan.FromMinutes(2.5));
        await Store.EndAsync(Person);
        await Store.EndAsync(Person);

        Assert.Equal([("user-1", "Alpha", 750L, 1L)], await RowsAsync());
    }

    [Fact]
    public async Task A_session_the_reaper_ends_is_measured_to_its_last_activity_not_to_the_reap()
    {
        var session = await Store.AttachAsync(Person, "Alpha", 80, 24, Ct);
        _clock.Advance(TimeSpan.FromMinutes(4));
        session.Session.Write("one question\r"u8);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Single(await Store.ReapIdleAsync(TimeSpan.FromHours(1), _clock.GetUtcNow()));

        Assert.Equal([("user-1", "Alpha", 240L, 1L)], await RowsAsync());
    }

    [Fact]
    public async Task A_session_whose_cli_exits_or_the_host_stops_is_still_one_row_each()
    {
        await Store.AttachAsync(Person, "Alpha", 80, 24, Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        _engine.Spawned.Single().Exit(0);
        await WaitUntilAsync(() => !Store.Has(Person));

        var other = new ConciergeSessionKey("user-2");
        await Store.AttachAsync(other, "Beta", 80, 24, Ct);
        _clock.Advance(TimeSpan.FromMinutes(3));
        await Store.DisposeAsync();

        Assert.Equal([("user-1", "Alpha", 60L, 1L), ("user-2", "Beta", 0L, 1L)], await RowsAsync());
    }

    private async Task<List<(string Actor, string? Team, long Seconds, long Measured)>> RowsAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database.Path, Pooling = false }.ToString());
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT actor_id, team_id, duration_seconds, duration_measured, kind FROM intervention_ledger ORDER BY id";

        var rows = new List<(string, string?, long, long)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            Assert.Equal(InterventionKinds.ConciergeSession, reader.GetString(4));
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        }

        return rows;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25, Ct);
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        await _database.DisposeAsync();
    }
}
