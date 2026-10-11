using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// A TRIGGER WRITE AND ITS TENANT ROW ARE ONE TRANSACTION. A person's change to a trigger, and the
/// capped skip the platform logs, land with their `tenant_events` row or not at all.
/// </summary>
public sealed class TriggerAuditTransactionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-trigger-tx-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Database => Path.Combine(_directory, "messages.db");

    public TriggerAuditTransactionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private static readonly TriggerAudit Audit =
        new("u1", "person@example.test", TenantActions.ScheduleChanged, "t1", "Poll", """{"team":"t"}""");

    private async Task<SqliteTriggerStore> StoreWithATriggerAsync()
    {
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct);
        await ExecuteAsync("INSERT INTO teams (id, created_utc) VALUES ('t', '2026-09-01T00:00:00Z')");

        var store = new SqliteTriggerStore(Database);
        await store.SaveAsync(
            new TriggerRow(
                "t1", "t", "Dev", "Poll", "look", "every", null, null, 60, null,
                IdleOnly: false, Enabled: true, NextDueAt: null, LastFiredAt: null, LastOutcome: null,
                LastSeq: null, MissedCount: 0, CreatedAt: DateTimeOffset.UnixEpoch, CreatedBy: "person",
                DailyTokenCap: 1000),
            Ct);
        return store;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task A_change_is_saved_with_its_tenant_row()
    {
        var store = await StoreWithATriggerAsync();
        var row = (await store.FindAsync("t1", Ct))!;

        await store.SaveAsync(row with { DailyTokenCap = 5000 }, Audit, Ct);

        Assert.Equal(5000, (await store.FindAsync("t1", Ct))!.DailyTokenCap);
        var logged = await new SqliteTenantLog(Database).FindLatestAsync(TenantActions.ScheduleChanged, "t1", Ct);
        Assert.NotNull(logged);
        Assert.Equal("person@example.test", logged.ActorEmail);
    }

    [Fact]
    public async Task A_change_is_not_saved_when_its_tenant_row_cannot_be()
    {
        var store = await StoreWithATriggerAsync();
        var row = (await store.FindAsync("t1", Ct))!;
        await ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(row with { DailyTokenCap = 5000 }, Audit, Ct));

        Assert.Equal(1000, (await store.FindAsync("t1", Ct))!.DailyTokenCap);
    }

    [Fact]
    public async Task A_logged_capped_skip_is_not_recorded_when_its_tenant_row_cannot_be()
    {
        var store = await StoreWithATriggerAsync();
        await ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(() => store.RecordCappedSkipAsync(
            "t1", 42, Audit with { Action = TenantActions.ScheduleSkipped, ActorId = "schedule:t1", ActorEmail = null }, Ct));

        Assert.Null((await store.FindAsync("t1", Ct))!.LastSeq);
    }

    [Fact]
    public async Task A_clock_triggers_capped_skip_stores_the_time_it_sleeps_until_in_utc()
    {
        var store = await StoreWithATriggerAsync();

        // A cron time in Tokyo comes back with Tokyo's offset; stored, it must sort as the instant.
        var midnightInTokyo = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.FromHours(9));
        var day = TriggerCost.StartOfDay("Asia/Tokyo", midnightInTokyo.AddHours(-1));

        Assert.Equal(1, await store.CountCappedSkipAsync("t1", day, rearm: true, midnightInTokyo, Ct));

        var row = (await store.FindAsync("t1", Ct))!;
        Assert.Equal("capped", row.LastOutcome);
        Assert.Equal(midnightInTokyo, row.NextDueAt);
        Assert.Empty(await store.DueAsync(new DateTimeOffset(2026, 9, 28, 14, 59, 59, TimeSpan.Zero), Ct));
        Assert.Single(await store.DueAsync(new DateTimeOffset(2026, 9, 28, 15, 0, 0, TimeSpan.Zero), Ct));
    }
}
