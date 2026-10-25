using System.Text.Json;
using Harness.Contracts;
using Harness.Identity;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ITenantSettingsStore"/>, as each provider must keep it. The audit-row atomicity tests are
/// <c>TenantSettingsTests</c>' (<c>A_value_is_not_written_when_its_audit_row_cannot_be</c>,
/// <c>A_reset_removes_the_row_and_appends_its_tenant_row</c>,
/// <c>A_reset_removes_nothing_when_its_tenant_row_cannot_be_written</c>,
/// <c>A_reset_of_a_setting_with_no_row_writes_nothing</c>), asked of the store directly rather than
/// through the settings route; the rest are the interface's remarks, the case map and concurrency.
/// </summary>
public abstract class TenantSettingsStoreContract : IdentityContract
{
    private const string Email = "person@example.test";

    protected async Task<ITenantSettingsStore> CreateAsync() => (await DatabaseAsync()).TenantSettings();

    [Fact]
    public async Task A_write_stores_each_row_with_its_tenant_row_and_rows_read_back_by_name()
    {
        var database = await DatabaseAsync();
        var store = database.TenantSettings();

        var written = await store.WriteAsync(
            [new TenantSettingChange("zeta.limit", null, "5"), new TenantSettingChange("alpha.mode", "old", "new")], "u1", Email, Ct);

        Assert.Equal(["zeta.limit", "alpha.mode"], written.Select(w => w.Name));
        Assert.Equal(("5", Email), (written[0].Row!.Value, written[0].Row!.UpdatedBy));
        var rows = await store.ReadAllAsync(Ct);
        Assert.Equal(["alpha.mode", "zeta.limit"], rows.Select(r => r.Name));
        Assert.Equal(written[1].Row, rows[0]);

        var detail = JsonDocument.Parse((await database.TextAsync(
            $"SELECT detail FROM tenant_events WHERE action = '{TenantActions.TenantSettingChanged}' AND subject = 'alpha.mode'"))!).RootElement;
        Assert.Equal(("alpha.mode", "old", "new"), (detail.GetProperty("setting").GetString(), detail.GetProperty("old").GetString(), detail.GetProperty("new").GetString()));
        Assert.Equal(2, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE actor_id = 'u1' AND actor_email = '{Email}'"));
        Assert.Equal(written[0].Row!.UpdatedAt, (await database.TenantLog().FindLatestAsync(TenantActions.TenantSettingChanged, "zeta.limit", Ct))!.OccurredAt);
    }

    [Fact]
    public async Task A_value_is_not_written_when_its_audit_row_cannot_be()
    {
        var database = await DatabaseAsync();
        var store = database.TenantSettings();
        await store.WriteAsync([new TenantSettingChange("alpha.mode", null, "kept")], null, Email, Ct);
        await database.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(() => store.WriteAsync(
            [new TenantSettingChange("beta.mode", null, "new"), new TenantSettingChange("alpha.mode", "kept", "changed")], null, Email, Ct));

        Assert.Equal([("alpha.mode", "kept")], (await store.ReadAllAsync(Ct)).Select(r => (r.Name, r.Value)));
    }

    [Fact]
    public async Task A_reset_removes_the_row_and_appends_its_tenant_row()
    {
        var database = await DatabaseAsync();
        var store = database.TenantSettings();
        await store.WriteAsync([new TenantSettingChange("alpha.mode", null, "on")], null, Email, Ct);

        var written = await store.WriteAsync([new TenantSettingChange("alpha.mode", "on", null)], "u1", Email, Ct);

        Assert.Equal([new TenantSettingWritten("alpha.mode", null)], written);
        Assert.Empty(await store.ReadAllAsync(Ct));
        var reset = (await database.TenantLog().FindLatestAsync(TenantActions.TenantSettingReset, "alpha.mode", Ct))!;
        var detail = JsonDocument.Parse(reset.Detail!).RootElement;
        Assert.Equal(("on", JsonValueKind.Null, "reset to default"), (detail.GetProperty("old").GetString(), detail.GetProperty("new").ValueKind, detail.GetProperty("change").GetString()));
    }

    [Fact]
    public async Task A_reset_removes_nothing_when_its_tenant_row_cannot_be_written()
    {
        var database = await DatabaseAsync();
        var store = database.TenantSettings();
        await store.WriteAsync([new TenantSettingChange("alpha.mode", null, "on")], null, Email, Ct);
        await database.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(() => store.WriteAsync([new TenantSettingChange("alpha.mode", "on", null)], null, Email, Ct));

        Assert.Equal(["alpha.mode"], (await store.ReadAllAsync(Ct)).Select(r => r.Name));
    }

    [Fact]
    public async Task A_reset_of_a_setting_with_no_row_writes_nothing()
    {
        var database = await DatabaseAsync();
        var store = database.TenantSettings();

        Assert.Empty(await store.WriteAsync([new TenantSettingChange("alpha.mode", null, null)], null, Email, Ct));

        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM tenant_events"));
    }

    /// <summary>THE CASE MAP OF A SETTING: its name is exact, as SQLite's BINARY key is.</summary>
    [Fact]
    public async Task Setting_names_differing_only_in_case_are_two_settings()
    {
        var store = await CreateAsync();

        await store.WriteAsync([new TenantSettingChange("Alpha.Mode", null, "upper"), new TenantSettingChange("alpha.mode", null, "lower")], null, Email, Ct);

        Assert.Equal([("Alpha.Mode", "upper"), ("alpha.mode", "lower")], (await store.ReadAllAsync(Ct)).Select(r => (r.Name, r.Value)));
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight people set one setting nobody has set yet, at once. Every write succeeds,
    /// one row stands holding one of their values, and each write has its tenant row.
    /// </summary>
    [Fact]
    public async Task Concurrent_writes_of_one_new_setting_keep_one_row_and_every_tenant_row()
    {
        var database = await DatabaseAsync();
        var store = database.TenantSettings();

        for (var round = 0; round < 12; round++)
        {
            var name = $"raced.{round}";
            await RaceAsync(8, i => store.WriteAsync([new TenantSettingChange(name, null, $"v{i}")], null, Email, Ct));

            var row = Assert.Single(await store.ReadAllAsync(Ct), r => r.Name == name);
            Assert.StartsWith("v", row.Value, StringComparison.Ordinal);
            Assert.Equal(8, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE subject = '{name}'"));
        }
    }

    /// <summary>
    /// REVIEW FOCUS 2: two people each change the same two settings in one write, in opposite orders.
    /// Both writes land whole; neither is refused or half applied.
    /// </summary>
    [Fact]
    public async Task Concurrent_writes_of_two_settings_in_opposite_orders_both_land_whole()
    {
        var database = await DatabaseAsync();
        var store = database.TenantSettings();
        await store.WriteAsync([new TenantSettingChange("a.one", null, "0"), new TenantSettingChange("b.two", null, "0")], null, Email, Ct);

        for (var round = 1; round <= 3; round++)
        {
            var value = round.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await RaceAsync(8, i => store.WriteAsync(
                i % 2 == 0
                    ? [new TenantSettingChange("a.one", null, value + "a"), new TenantSettingChange("b.two", null, value + "a")]
                    : [new TenantSettingChange("b.two", null, value + "b"), new TenantSettingChange("a.one", null, value + "b")],
                null, Email, Ct));

            var rows = await store.ReadAllAsync(Ct);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.StartsWith(value, r.Value, StringComparison.Ordinal));
        }

        Assert.Equal(2 + (3 * 8 * 2), await database.LongAsync("SELECT COUNT(*) FROM tenant_events"));
    }
}

public sealed class SqliteTenantSettingsStoreTests : TenantSettingsStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerTenantSettingsStoreTests : TenantSettingsStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
