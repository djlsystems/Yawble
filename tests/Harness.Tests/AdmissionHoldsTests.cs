using System.Collections.Concurrent;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// ADMISSION HOLDS ARE RECORDED. <c>WipLedger</c> writes an <c>admission_holds</c> row when a claim is
/// first held and closes it when the hold ends - the run started, or its claim was withdrawn - one row
/// per reason; a write that fails is logged and admission carries on; and the table is append-only
/// like the usage ledger. See AGENTS.md, Admission.
/// </summary>
public sealed class AdmissionHoldsTests : IDisposable
{
    private static readonly ContainerId Dev1 = new("Alpha", "Dev1");
    private static readonly ContainerId Dev2 = new("Alpha", "Dev2");
    private static readonly ContainerId Manager = new("Alpha", WipLedger.ManagerName);

    private const string Memory = "waiting for memory: 11.2 of 12.9 GB in use";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-holds-{Guid.NewGuid():N}");
    private readonly SqliteAdmissionHolds _store;
    private readonly Lines _log = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Database => Path.Combine(_directory, "messages.db");

    public AdmissionHoldsTests()
    {
        Directory.CreateDirectory(_directory);
        new SchemaMigrator(Database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
        _store = new SqliteAdmissionHolds(Database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_held_claim_writes_a_row_with_its_reason_and_the_release_closes_it()
    {
        var writer = new AdmissionHoldWriter(_store, _log);
        var wip = new WipLedger(1, holds: writer);

        var running = wip.TryEnterFor(Dev1, 10);
        Assert.NotNull(running);
        Assert.Null(wip.TryEnterFor(Dev2, 42));
        Assert.Null(wip.TryEnterFor(Dev2, 42));
        await WrittenAsync(writer);

        var held = Assert.Single(await RowsAsync());
        Assert.Equal("Alpha", held.TeamId);
        Assert.Equal("Alpha", held.TeamName);
        Assert.Equal("Dev2", held.Member);
        Assert.Equal(42, held.DeliverySeq);
        Assert.Equal(AdmissionHoldKinds.Slot, held.ReasonKind);
        Assert.Equal(WipLedger.SlotReason, held.Reason);
        Assert.Equal(wip.View().Waiting.Single().Since, held.HeldAt);
        Assert.Null(held.ReleasedAt);
        Assert.False(held.Unfinished);

        // Started: the slot was released and the waiter took it.
        running.Dispose();
        using var started = wip.TryEnterFor(Dev2, 42);
        Assert.NotNull(started);
        await WrittenAsync(writer);

        var closed = Assert.Single(await RowsAsync());
        Assert.NotNull(closed.ReleasedAt);
        Assert.True(closed.ReleasedAt >= closed.HeldAt);
        Assert.False(closed.Unfinished);

        // Cancelled: a waiter that withdraws closes its row too.
        var dev3 = new ContainerId("Alpha", "Dev3");
        Assert.Null(wip.TryEnter(dev3));
        wip.Withdraw(dev3);
        await WrittenAsync(writer);

        var withdrawn = (await RowsAsync()).Single(r => r.Member == "Dev3");
        Assert.Null(withdrawn.DeliverySeq);
        Assert.NotNull(withdrawn.ReleasedAt);
    }

    [Fact]
    public async Task A_reason_change_while_held_closes_one_row_and_opens_another()
    {
        string? headroom = null;
        var writer = new AdmissionHoldWriter(_store, _log);
        var wip = new WipLedger(1, () => headroom, writer);

        var running = wip.TryEnter(Dev1);
        Assert.Null(wip.TryEnterFor(Dev2, 7));

        // The slot frees, but memory is now over its threshold: the same wait, a new reason.
        headroom = Memory;
        running!.Dispose();
        Assert.Null(wip.TryEnterFor(Dev2, 7));

        // A new figure with the same reason kind is the same hold.
        headroom = "waiting for memory: 11.4 of 12.9 GB in use";
        wip.HeadroomChanged();
        await WrittenAsync(writer);

        var rows = await RowsAsync();
        Assert.Equal(
            [$"{AdmissionHoldKinds.Slot}|{WipLedger.SlotReason}|closed", $"{AdmissionHoldKinds.Memory}|{Memory}|open"],
            rows.Select(r => $"{r.ReasonKind}|{r.Reason}|{(r.ReleasedAt is null ? "open" : "closed")}"));
        Assert.Equal(rows[0].ReleasedAt, rows[1].HeldAt);
        Assert.All(rows, r => Assert.Equal(7, r.DeliverySeq));

        // Memory pressure is a reason of its own.
        headroom = WipLedger.PressureReasonStart + "12% of the last 10 s";
        wip.HeadroomChanged();
        headroom = null;
        wip.HeadroomChanged();
        using var started = wip.TryEnterFor(Dev2, 7);
        Assert.NotNull(started);
        await WrittenAsync(writer);

        rows = await RowsAsync();
        Assert.Equal(
            [AdmissionHoldKinds.Slot, AdmissionHoldKinds.Memory, AdmissionHoldKinds.Pressure],
            rows.Select(r => r.ReasonKind));
        Assert.All(rows, r => Assert.NotNull(r.ReleasedAt));
    }

    [Fact]
    public async Task A_run_waiting_for_a_worker_is_recorded_as_such()
    {
        var writer = new AdmissionHoldWriter(_store, _log);
        var wip = new WipLedger(2, new NoWorkers(), writer);

        Assert.Null(wip.TryEnter(Dev1));
        await WrittenAsync(writer);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(AdmissionHoldKinds.Worker, row.ReasonKind);
        Assert.Equal(WipLedger.WorkerReason, row.Reason);
    }

    [Fact]
    public async Task A_manager_never_held_writes_nothing()
    {
        var writer = new AdmissionHoldWriter(_store, _log);
        var wip = new WipLedger(1, holds: writer);

        using var member = wip.TryEnter(Dev1);
        using var manager = wip.TryEnterFor(Manager, 3);
        Assert.NotNull(member);
        Assert.NotNull(manager);
        await WrittenAsync(writer);

        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task A_failed_write_never_blocks_admission()
    {
        var writer = new AdmissionHoldWriter(new FailingLedger(), _log);
        var wip = new WipLedger(1, holds: writer);

        var running = wip.TryEnter(Dev1);
        Assert.Null(wip.TryEnter(Dev2));
        running!.Dispose();
        using var started = wip.TryEnter(Dev2);
        Assert.NotNull(started);
        await WrittenAsync(writer);

        Assert.Contains(_log.All, line => line.Contains("admission hold", StringComparison.Ordinal) && line.Contains("disk full", StringComparison.Ordinal));

        // A recorder that throws in the ledger's own call is passed over the same way.
        var careless = new WipLedger(1, holds: new ThrowingHolds());
        var first = careless.TryEnter(Dev1);
        Assert.Null(careless.TryEnter(Dev2));
        first!.Dispose();
        using var second = careless.TryEnter(Dev2);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task While_the_Host_stops_a_withdrawn_waiter_leaves_its_row_open_and_a_start_still_closes_its_own()
    {
        var writer = new AdmissionHoldWriter(_store, _log);
        var wip = new WipLedger(1, holds: writer);
        var dev3 = new ContainerId("Alpha", "Dev3");

        var running = wip.TryEnterFor(Dev1, 10);
        Assert.NotNull(running);
        Assert.Null(wip.TryEnterFor(Dev2, 20));
        Assert.Null(wip.TryEnterFor(dev3, 30));

        wip.HostStopping();
        running.Dispose();
        using var started = wip.TryEnterFor(Dev2, 20);
        Assert.NotNull(started);
        wip.Withdraw(dev3);
        await WrittenAsync(writer);

        Assert.Equal(
            ["Dev2|closed", "Dev3|open"],
            (await RowsAsync()).OrderBy(r => r.Member).Select(r => $"{r.Member}|{(r.ReleasedAt is null ? "open" : "closed")}"));
    }

    [Fact]
    public async Task An_update_other_than_closing_once_and_any_delete_are_refused_and_a_log_purge_leaves_the_rows()
    {
        var heldAt = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
        await _store.OpenAsync("Alpha", "Dev1", 5, heldAt, AdmissionHoldKinds.Slot, WipLedger.SlotReason, Ct);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("UPDATE admission_holds SET reason = 'other'"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("UPDATE admission_holds SET unfinished = 1"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            "UPDATE admission_holds SET released_at = '2026-10-04T09:01:00.0000000+00:00', member = 'Dev9'"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            "UPDATE admission_holds SET released_at = '2026-10-04T09:01:00.0000000+00:00', unfinished = 2"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM admission_holds"));

        await _store.CloseAsync("Alpha", "Dev1", heldAt.AddMinutes(4), Ct);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            "UPDATE admission_holds SET released_at = '2026-10-04T09:09:00.0000000+00:00'"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("UPDATE admission_holds SET released_at = NULL"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync("DELETE FROM admission_holds"));

        // The log's one deletion - Reset's purge, and any retention built on it - leaves the holds.
        var log = new SqliteMessageStore(Database);
        var row = await log.AppendAsync(new NewMessage("test.mark", "{}", "console"), Ct);
        Assert.Equal(1, (await log.DeleteAsync([row.Seq], Ct)).Purged);

        var kept = Assert.Single(await RowsAsync());
        Assert.Equal(heldAt.AddMinutes(4), kept.ReleasedAt);
        Assert.Equal(WipLedger.SlotReason, kept.Reason);
    }

    [Fact]
    public async Task Closing_the_unfinished_rows_marks_them_and_keeps_their_reason()
    {
        var heldAt = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
        await _store.OpenAsync("Alpha", "Dev1", null, heldAt, AdmissionHoldKinds.Memory, Memory, Ct);
        await _store.OpenAsync("Alpha", "Dev2", null, heldAt, AdmissionHoldKinds.Slot, WipLedger.SlotReason, Ct);
        await _store.CloseAsync("Alpha", "Dev2", heldAt.AddMinutes(1), Ct);

        Assert.Equal(1, await _store.CloseUnfinishedAsync(heldAt.AddMinutes(9), Ct));

        var rows = await RowsAsync();
        Assert.Equal(
            ["Dev1|memory|9|True", "Dev2|slot|1|False"],
            rows.OrderBy(r => r.Member).Select(r => $"{r.Member}|{r.ReasonKind}|{(r.ReleasedAt!.Value - heldAt).TotalMinutes}|{r.Unfinished}"));
        Assert.Equal(Memory, rows.Single(r => r.Member == "Dev1").Reason);
    }

    /// <summary>Waits for the writer, bounded: a writer whose loop died fails the test instead of
    /// hanging it.</summary>
    private static Task WrittenAsync(AdmissionHoldWriter writer) =>
        writer.WrittenAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

    private Task<IReadOnlyList<AdmissionHoldRow>> RowsAsync() =>
        _store.ReadTeamAsync("Alpha", DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Ct);

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed class NoWorkers : IRunPlacement
    {
        public IReadOnlyList<WorkerId> Workers => [];

        public string? HeadroomReason(WorkerId worker) => null;
    }

    private sealed class FailingLedger : IAdmissionHoldLedger
    {
        public Task OpenAsync(
            string team, string member, long? deliverySeq, DateTimeOffset heldAt, string reasonKind, string reason,
            CancellationToken ct = default) => throw new IOException("disk full");

        public Task CloseAsync(string team, string member, DateTimeOffset releasedAt, CancellationToken ct = default) =>
            throw new IOException("disk full");

        public Task<int> CloseUnfinishedAsync(DateTimeOffset at, CancellationToken ct = default) =>
            throw new IOException("disk full");

        public Task<IReadOnlyList<AdmissionHoldRow>> ReadTeamAsync(
            string team, DateTimeOffset since, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new IOException("disk full");
    }

    private sealed class ThrowingHolds : IAdmissionHolds
    {
        public void Held(string team, string member, long? deliverySeq, DateTimeOffset at, string reasonKind, string reason) =>
            throw new InvalidOperationException("recorder broke");

        public void Released(string team, string member, DateTimeOffset at) =>
            throw new InvalidOperationException("recorder broke");
    }

    private sealed class Lines : ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IEnumerable<string> All => _lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue($"{formatter(state, exception)} {exception?.Message}");
    }
}
