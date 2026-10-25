using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IPluginMemberSecretRows"/>, as each provider must keep it, asked through the class both
/// share (<see cref="PluginMemberSecretStore"/>, which seals and opens). From <c>FreshVolumeTests</c>'
/// member-secret round trip, same values: a member's own value is kept as ciphertext and goes with the
/// member's row. Then the interface's remarks (set, replace and clear with their rows; a clear of nothing;
/// a save with settings in one transaction; the one-time copy of the stored keys), the audit atomicity,
/// the case and trailing-space map of a field, and concurrency: two first sets of one field, two copies
/// at one start, and a team deleted while its members' values are set.
/// </summary>
public abstract class PluginMemberSecretRowsContract : HostStoreContract
{
    private static readonly ContainerId Keeper = new("kept-team", "Keeper");

    private async Task<PluginMemberSecretStore> StoreAsync() => new((await DatabaseAsync()).MemberSecretRows(), Protection);

    private async Task<PluginMemberSecretStore> HiredAsync(string team = "kept-team", params string[] members)
    {
        await HireAsync(await DatabaseAsync(), team, members.Length == 0 ? ["Keeper"] : members);
        return await StoreAsync();
    }

    private Task SetAsync(PluginMemberSecretStore store, ContainerId member, string field, string value) =>
        store.SetAsync(member, member.Name, "fixture", field, value, Person, Ct);

    [Fact]
    public async Task A_members_own_value_is_kept_as_ciphertext_and_goes_with_the_members_row()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();

        await store.SetAsync(Keeper, "Keeper", "fixture", "token", "own-value-1234", Person, Ct);
        Assert.Equal("own-value-1234", (await store.ValuesAsync(Keeper, Ct))["token"]);
        Assert.DoesNotContain("own-value-1234", Assert.Single(await database.ColumnAsync("SELECT value_protected FROM plugin_member_secrets")));

        // The row goes with the member's.
        await database.Teams().DeleteMemberAsync("kept-team", "Keeper", Ct);
        Assert.Empty(await database.ColumnAsync("SELECT field FROM plugin_member_secrets"));
    }

    [Fact]
    public async Task Set_replace_and_clear_each_land_with_their_row_naming_the_field_and_never_the_value()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();

        await SetAsync(store, Keeper, "token", "first-value-1234");
        await store.SetAsync(Keeper, "Keeper", "fixture", "token", "second-value-1234", new CredentialActor("u2", "other@example.test"), Ct);
        Assert.Equal("second-value-1234", (await store.ValuesAsync(Keeper, Ct))["token"]);
        Assert.Equal("other@example.test", (await store.StatesAsync(Keeper, Ct))["token"].SetBy);

        await store.ClearAsync(Keeper, "Keeper", "fixture", "token", Person, Ct);
        Assert.Empty(await store.ValuesAsync(Keeper, Ct));

        // A clear of nothing changes nothing and writes no row.
        await store.ClearAsync(Keeper, "Keeper", "fixture", "token", Person, Ct);

        Assert.Equal(
            [TenantActions.MemberSecretSet, TenantActions.MemberSecretReplaced, TenantActions.MemberSecretCleared],
            await database.ColumnAsync("SELECT action FROM tenant_events ORDER BY seq"));
        var detail = JsonDocument.Parse((await database.TextAsync("SELECT detail FROM tenant_events ORDER BY seq"))!).RootElement;
        Assert.Equal(("kept-team", "Keeper", "fixture", "token"),
            (detail.GetProperty("team").GetString(), detail.GetProperty("member").GetString(), detail.GetProperty("plugin").GetString(), detail.GetProperty("field").GetString()));
        Assert.DoesNotContain(await database.ColumnAsync("SELECT detail FROM tenant_events"), d => d!.Contains("value-1234", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Several_changes_land_in_field_order_in_one_transaction()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();
        await SetAsync(store, Keeper, "beta", "beta-value-1234");

        await store.ChangeAsync(
            Keeper, "Keeper", "fixture",
            new Dictionary<string, string?> { ["gamma"] = "gamma-value-1234", ["alpha"] = "alpha-value-1234", ["beta"] = null },
            Person, Ct);

        Assert.Equal(["alpha", "gamma"], (await store.ValuesAsync(Keeper, Ct)).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["beta", "alpha", "beta", "gamma"],
            (await database.ColumnAsync("SELECT detail FROM tenant_events ORDER BY seq")).Select(d => JsonDocument.Parse(d!).RootElement.GetProperty("field").GetString()));
    }

    [Fact]
    public async Task A_change_whose_tenant_row_cannot_land_changes_nothing()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();
        await SetAsync(store, Keeper, "token", "kept-value-1234");
        await database.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<DbException>(() => store.ChangeAsync(
            Keeper, "Keeper", "fixture", new Dictionary<string, string?> { ["other"] = "new-value-1234", ["token"] = null }, Person, Ct));

        Assert.Equal(["token"], (await store.ValuesAsync(Keeper, Ct)).Keys);
    }

    [Fact]
    public async Task A_save_with_settings_writes_the_settings_every_row_and_every_value_together_or_nothing()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();
        var settings = database.MemberSettings();
        var saved = new PluginMemberSettings(new Dictionary<string, JsonElement> { ["mode"] = JsonSerializer.SerializeToElement("on") }, new Dictionary<string, string>());

        await store.SaveWithSettingsAsync(
            Keeper, "Keeper", "fixture", saved, [Audit(TenantActions.MemberPluginSettingsChanged, "kept-team/Keeper")],
            new Dictionary<string, string?> { ["token"] = "own-value-1234" }, Person, Ct);

        Assert.Equal("on", (await settings.ForAsync(Keeper, Ct)).Config["mode"].GetString());
        Assert.Equal("own-value-1234", (await store.ValuesAsync(Keeper, Ct))["token"]);
        Assert.Equal([TenantActions.MemberPluginSettingsChanged, TenantActions.MemberSecretSet], await database.ColumnAsync("SELECT action FROM tenant_events ORDER BY seq"));

        await database.ExecuteAsync("DROP TABLE tenant_events");
        var changed = new PluginMemberSettings(new Dictionary<string, JsonElement> { ["mode"] = JsonSerializer.SerializeToElement("off") }, new Dictionary<string, string>());
        await Assert.ThrowsAnyAsync<DbException>(() => store.SaveWithSettingsAsync(
            Keeper, "Keeper", "fixture", changed, [], new Dictionary<string, string?> { ["token"] = "changed-value-1234" }, Person, Ct));

        Assert.Equal("on", (await settings.ForAsync(Keeper, Ct)).Config["mode"].GetString());
        Assert.Equal("own-value-1234", (await store.ValuesAsync(Keeper, Ct))["token"]);
    }

    [Fact]
    public async Task Two_members_of_one_plugin_never_read_each_others_values()
    {
        var store = await HiredAsync("kept-team", "One", "Two");
        var one = new ContainerId("kept-team", "One");
        var two = new ContainerId("kept-team", "Two");

        await SetAsync(store, one, "token", "one-value-1234");
        await SetAsync(store, two, "token", "two-value-1234");

        Assert.Equal("one-value-1234", (await store.ValuesAsync(one, Ct))["token"]);
        Assert.Equal("two-value-1234", (await store.ValuesAsync(two, Ct))["token"]);
    }

    /// <summary>THE CASE MAP: a team and a member fold case, as SQLite's NOCASE columns do; a field does not.</summary>
    [Fact]
    public async Task A_member_in_another_case_is_the_same_member_and_a_field_in_another_case_is_another_field()
    {
        var store = await HiredAsync();

        await SetAsync(store, Keeper, "token", "lower-value-1234");
        await SetAsync(store, new ContainerId("KEPT-TEAM", "keeper"), "Token", "upper-value-1234");

        var values = await store.ValuesAsync(new ContainerId("Kept-Team", "KEEPER"), Ct);
        Assert.Equal([("Token", "upper-value-1234"), ("token", "lower-value-1234")], values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => (v.Key, v.Value)));
    }

    /// <summary>THE TRAILING-SPACE MAP: a field with a trailing space is another, and clears nothing.</summary>
    [Fact]
    public async Task A_field_with_a_trailing_space_is_another()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();
        await SetAsync(store, Keeper, "token", "kept-value-1234");

        await store.ClearAsync(Keeper, "Keeper", "fixture", "token ", Person, Ct);

        Assert.Equal("kept-value-1234", (await store.ValuesAsync(Keeper, Ct))["token"]);
        Assert.Equal(1, await database.LongAsync("SELECT COUNT(*) FROM tenant_events"));
    }

    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var store = await HiredAsync();
        var value = LongUnicode("value");

        await SetAsync(store, Keeper, "token", value);

        Assert.Equal(value, (await store.ValuesAsync(Keeper, Ct))["token"]);
    }

    /// <summary>
    /// THE ONE-TIME COPY: a member field bound to a STORED key, with no value of its own, takes the key's
    /// value; one with its own value keeps it; a key only the environment holds is never copied; the
    /// step's row counts members, fields and keys; a second run copies nothing.
    /// </summary>
    [Fact]
    public async Task The_copy_takes_each_stored_key_onto_its_members_once_and_counts_them()
    {
        var database = await DatabaseAsync();
        await HireAsync(database, "kept-team", "One", "Two");
        var settings = database.MemberSettings();
        var keys = new PluginSecretStore(database.SecretRows(), Protection, new EmptySecrets());
        var store = await StoreAsync();
        var one = new ContainerId("kept-team", "One");
        var two = new ContainerId("kept-team", "Two");

        await keys.SetAsync("ACME_KEY", "stored-value-1234", Person, Ct);
        await settings.SaveAsync(one, Bound(("token", "ACME_KEY"), ("other", "ENV_ONLY_KEY")), Ct);
        await settings.SaveAsync(two, Bound(("token", "ACME_KEY")), Ct);
        await SetAsync(store, two, "token", "own-value-1234");

        Assert.True(await store.CopyStoredKeysOnceAsync(Protection, Ct));

        Assert.Equal([("token", "stored-value-1234")], (await store.ValuesAsync(one, Ct)).Select(v => (v.Key, v.Value)));
        Assert.Equal("own-value-1234", (await store.ValuesAsync(two, Ct))["token"]);
        var done = JsonDocument.Parse((await database.TextAsync($"SELECT detail FROM tenant_events WHERE action = '{TenantActions.MemberSecretsCopied}'"))!).RootElement;
        Assert.Equal((1, 1, 1), (done.GetProperty("members").GetInt32(), done.GetProperty("fields").GetInt32(), done.GetProperty("keys").GetInt32()));

        // The binding and the stored key stay; a second run copies nothing.
        Assert.Equal("ACME_KEY", (await settings.ForAsync(one, Ct)).Secrets["token"]);
        await database.ExecuteAsync("DELETE FROM plugin_member_secrets");
        Assert.False(await store.CopyStoredKeysOnceAsync(Protection, Ct));
        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM plugin_member_secrets"));
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight people set one field a member has no value for, at once. Every set succeeds,
    /// one row stands, and the log says one set and seven replacements.
    /// </summary>
    [Fact]
    public async Task Concurrent_first_sets_of_one_field_make_one_row_one_set_and_the_rest_replaced()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();

        for (var round = 0; round < 12; round++)
        {
            var field = $"raced{round}";
            await RaceAsync(8, async i =>
            {
                await SetAsync(store, Keeper, field, $"value-{i}-123456");
                return i;
            });

            Assert.StartsWith("value-", (await store.ValuesAsync(Keeper, Ct))[field], StringComparison.Ordinal);
            var actions = await database.ColumnAsync($"SELECT action FROM tenant_events WHERE detail LIKE '%\"field\":\"{field}\"%'");
            Assert.Equal(1, actions.Count(a => a == TenantActions.MemberSecretSet));
            Assert.Equal(7, actions.Count(a => a == TenantActions.MemberSecretReplaced));
        }

        Assert.Equal(12, await database.LongAsync("SELECT COUNT(*) FROM plugin_member_secrets"));
    }

    /// <summary>Two Hosts started on one database at once: one copies and marks it done, the other copies
    /// nothing, and there is one mark.</summary>
    [Fact]
    public async Task Concurrent_copies_at_one_start_copy_once()
    {
        var database = await DatabaseAsync();
        await HireAsync(database, "kept-team", "One");
        await new PluginSecretStore(database.SecretRows(), Protection, new EmptySecrets()).SetAsync("ACME_KEY", "stored-value-1234", Person, Ct);
        await database.MemberSettings().SaveAsync(new ContainerId("kept-team", "One"), Bound(("token", "ACME_KEY")), Ct);

        var store = await StoreAsync();

        for (var round = 0; round < 12; round++)
        {
            // A database the copy has not run on: no mark, no copied value.
            await database.ExecuteAsync($"DELETE FROM tenant_events WHERE action = '{TenantActions.MemberSecretsCopied}'");
            await database.ExecuteAsync("DELETE FROM plugin_member_secrets");

            var copied = await RaceAsync(4, _ => store.CopyStoredKeysOnceAsync(Protection, Ct));

            Assert.Equal(1, copied.Count(c => c));
            Assert.Equal(1, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.MemberSecretsCopied}'"));
            Assert.Equal(1, await database.LongAsync("SELECT COUNT(*) FROM plugin_member_secrets"));
        }
    }

    /// <summary>
    /// THE LOCK ORDER (teams, member, secrets): a team deleted while eight of its members' values are set.
    /// Each set lands or is refused by the foreign key, never a deadlock, and no value of the team is left.
    /// </summary>
    [Fact]
    public async Task A_team_deleted_while_its_members_values_are_set_leaves_no_row_and_no_deadlock()
    {
        var database = await DatabaseAsync();
        var store = await StoreAsync();
        var teams = database.Teams();

        for (var round = 0; round < 12; round++)
        {
            var team = $"doomed{round}";
            await HireAsync(database, team, [.. Enumerable.Range(0, 8).Select(i => $"M{i}")]);

            var outcomes = await RaceAsync(9, async i =>
            {
                try
                {
                    if (i == 8) await teams.DeleteTeamAsync(team, Ct);
                    else await SetAsync(store, new ContainerId(team, $"M{i}"), "token", $"value-{i}-123456");

                    return null;
                }
                catch (DbException refused)
                {
                    return refused.Message;
                }
            });

            Assert.All(outcomes, refusal => Assert.True(refusal is null || refusal.Contains("FOREIGN KEY", StringComparison.Ordinal), refusal));
            Assert.Equal(0, await database.LongAsync($"SELECT COUNT(*) FROM plugin_member_secrets WHERE team = '{team}'"));
        }
    }

    private static PluginMemberSettings Bound(params (string Field, string Key)[] bindings) =>
        new(new Dictionary<string, JsonElement>(), bindings.ToDictionary(b => b.Field, b => b.Key));

    private sealed class EmptySecrets : ISecretStore
    {
        public string? TryGet(string logicalKey) => null;
    }
}

public sealed class SqlitePluginMemberSecretRowsTests : PluginMemberSecretRowsContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerPluginMemberSecretRowsTests : PluginMemberSecretRowsContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
