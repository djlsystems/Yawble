using System.Data.Common;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IPluginSecretRows"/>, as each provider must keep it, asked through the one class both share
/// (<see cref="PluginSecretStore"/>, which seals and opens) and of the rows directly. From
/// <c>FreshVolumeTests</c>' plugin key round trip, same values: a key is stored, read before the
/// environment, and cleared with its rows. Then the interface's remarks (set or replace, the row a
/// clear of nothing writes, the ciphertext kept as it was given), the audit atomicity, the case and
/// trailing-space map of a key, and two first sets of one key at once.
/// </summary>
public abstract class PluginSecretRowsContract : HostStoreContract
{
    private const string Key = "ACME_STORAGE_KEY";

    private async Task<PluginSecretStore> StoreAsync(Dictionary<string, string>? environment = null) =>
        new((await DatabaseAsync()).SecretRows(), Protection, new FixedSecrets(environment ?? []));

    [Fact]
    public async Task A_key_is_stored_read_before_the_environment_and_cleared_with_its_rows()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync(new() { [Key] = "from-the-environment" });

        Assert.Equal("environment", store.Status(Key).Source);

        await store.SetAsync(Key, "from-the-screen", Person, Ct);
        Assert.Equal("from-the-screen", store.TryGet(Key));
        Assert.Equal(("stored", Email), (store.Status(Key).Source, store.Status(Key).SetBy));

        await store.ClearAsync(Key, Person, Ct);
        Assert.Equal("from-the-environment", store.TryGet(Key));
        Assert.Equal(
            ["secrets.set", "secrets.cleared"],
            await database.ColumnAsync($"SELECT action FROM tenant_events WHERE subject = '{Key}' ORDER BY seq"));
        Assert.Empty(await database.ColumnAsync("SELECT [key] FROM plugin_secrets"));
    }

    [Fact]
    public async Task A_second_set_replaces_the_value_and_says_so()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();

        await store.SetAsync(Key, "first-value-1234", Person, Ct);
        await store.SetAsync(Key, "second-value-1234", new CredentialActor("u2", "other@example.test"), Ct);

        Assert.Equal("second-value-1234", store.TryGet(Key));
        Assert.Equal("other@example.test", store.Status(Key).SetBy);
        Assert.Equal(["secrets.set", "secrets.replaced"], await database.ColumnAsync($"SELECT action FROM tenant_events WHERE subject = '{Key}' ORDER BY seq"));
        Assert.Equal(1, await database.LongAsync("SELECT COUNT(*) FROM plugin_secrets"));
    }

    [Fact]
    public async Task A_clear_of_a_key_never_stored_changes_nothing_and_writes_no_row()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();

        await store.ClearAsync(Key, Person, Ct);

        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM tenant_events"));
    }

    /// <summary>THE CIPHERTEXT IS KEPT AS IT WAS GIVEN, and never the value: what the rows answer is the
    /// text that was sealed, character for character, with who stored it and when.</summary>
    [Fact]
    public async Task The_row_keeps_the_ciphertext_it_was_given_and_never_the_value()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        await store.SetAsync(Key, "the-value-1234", Person, Ct);

        var row = database.SecretRows().Find(Key)!;
        Assert.DoesNotContain("the-value-1234", row.ValueProtected, StringComparison.Ordinal);
        Assert.Equal(row.ValueProtected, await database.TextAsync("SELECT value_protected FROM plugin_secrets"));

        var at = new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);
        await database.SecretRows().SetAsync("OTHER_KEY", row.ValueProtected, "someone@example.test", at, _ => Audit(TenantActions.SecretSet, "OTHER_KEY"), Ct);
        Assert.Equal(new StoredSecret(row.ValueProtected, "someone@example.test", at), database.SecretRows().Find("OTHER_KEY"));
        Assert.Equal("the-value-1234", store.TryGet("OTHER_KEY"));
    }

    [Fact]
    public async Task A_write_whose_tenant_row_cannot_land_changes_nothing()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        await store.SetAsync(Key, "kept-value-1234", Person, Ct);
        await database.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<DbException>(() => store.SetAsync(Key, "changed-value-1234", Person, Ct));
        await Assert.ThrowsAnyAsync<DbException>(() => store.SetAsync("NEW_KEY", "new-value-1234", Person, Ct));
        await Assert.ThrowsAnyAsync<DbException>(() => store.ClearAsync(Key, Person, Ct));

        Assert.Equal("kept-value-1234", store.TryGet(Key));
        Assert.Null(store.TryGet("NEW_KEY"));
    }

    /// <summary>THE CASE MAP: a key is exact, as SQLite's BINARY key is.</summary>
    [Fact]
    public async Task Keys_differing_only_in_case_are_two_keys()
    {
        var database = await DatabaseAsync();
        var rows = database.SecretRows();
        var at = DateTimeOffset.UtcNow;

        await rows.SetAsync("ACME_KEY", "upper", Email, at, _ => Audit(TenantActions.SecretSet, "ACME_KEY"), Ct);
        await rows.SetAsync("acme_key", "lower", Email, at, existed => Audit(existed ? TenantActions.SecretReplaced : TenantActions.SecretSet, "acme_key"), Ct);

        Assert.Equal(("upper", "lower"), (rows.Find("ACME_KEY")!.ValueProtected, rows.Find("acme_key")!.ValueProtected));
        Assert.Null(rows.Find("Acme_Key"));
        Assert.Equal([TenantActions.SecretSet, TenantActions.SecretSet], await database.ColumnAsync("SELECT action FROM tenant_events ORDER BY seq"));
    }

    /// <summary>THE TRAILING-SPACE MAP: a key with a trailing space is another, and clears nothing.</summary>
    [Fact]
    public async Task A_key_with_a_trailing_space_is_another()
    {
        var database = await DatabaseAsync();
        var rows = database.SecretRows();
        await rows.SetAsync("ACME_KEY", "value", Email, DateTimeOffset.UtcNow, _ => Audit(TenantActions.SecretSet, "ACME_KEY"), Ct);

        Assert.Null(rows.Find("ACME_KEY "));
        await rows.ClearAsync("ACME_KEY ", Audit(TenantActions.SecretCleared, "ACME_KEY "), Ct);

        Assert.NotNull(rows.Find("ACME_KEY"));
        Assert.Equal(1, await database.LongAsync("SELECT COUNT(*) FROM tenant_events"));
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight people set one key nobody has set yet, at once. Every set succeeds, one row
    /// stands, and the tenant log says one set and seven replacements - never two sets.
    /// </summary>
    [Fact]
    public async Task Concurrent_first_sets_of_one_key_make_one_row_one_set_and_the_rest_replaced()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();

        for (var round = 0; round < 12; round++)
        {
            var key = $"RACED_{round}";
            await RaceAsync(8, async i =>
            {
                await store.SetAsync(key, $"value-{i}-123456", Person, Ct);
                return i;
            });

            Assert.StartsWith("value-", store.TryGet(key), StringComparison.Ordinal);
            Assert.Equal(1, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE subject = '{key}' AND action = '{TenantActions.SecretSet}'"));
            Assert.Equal(7, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE subject = '{key}' AND action = '{TenantActions.SecretReplaced}'"));
        }

        Assert.Equal(12, await database.LongAsync("SELECT COUNT(*) FROM plugin_secrets"));
    }

    private sealed class FixedSecrets(Dictionary<string, string> values) : ISecretStore
    {
        public string? TryGet(string logicalKey) => values.GetValueOrDefault(logicalKey);
    }
}

public sealed class SqlitePluginSecretRowsTests : PluginSecretRowsContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerPluginSecretRowsTests : PluginSecretRowsContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
