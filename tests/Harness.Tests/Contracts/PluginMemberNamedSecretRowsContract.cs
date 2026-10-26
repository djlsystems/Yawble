using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// A plugin member's NAMED secrets (<c>plugin_member_named_secrets</c>, auth-030) as each provider must
/// keep them, asked through the class both share (<see cref="PluginMemberSecretStore"/>, which seals
/// them under their own purpose): kept as ciphertext, gone with the member's row and its team; set,
/// replace and removal each with its row naming the name and never the value; a name in another case
/// is the same secret; a change whose row cannot land, and a settings save carrying named secrets,
/// change everything or nothing; two members never read each other's.
/// </summary>
public abstract class PluginMemberNamedSecretRowsContract : HostStoreContract
{
    private static readonly ContainerId Keeper = new("kept-team", "Keeper");

    private async Task<PluginMemberSecretStore> HiredAsync(string team = "kept-team", params string[] members)
    {
        var database = await DatabaseAsync();
        await HireAsync(database, team, members.Length == 0 ? ["Keeper"] : members);
        return new PluginMemberSecretStore(database.MemberSecretRows(), Protection);
    }

    private static Task SetAsync(PluginMemberSecretStore store, ContainerId member, string name, string? value, CredentialActor? actor = null) =>
        store.ChangeNamedAsync(member, member.Name, "fixture", new Dictionary<string, string?> { [name] = value }, actor ?? Person, Ct);

    [Fact]
    public async Task A_named_secret_is_kept_as_ciphertext_under_its_own_purpose_and_goes_with_the_member_and_its_team()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync("kept-team", "Keeper", "Other");

        await SetAsync(store, Keeper, "ApiKey", "own-value-1234");
        await SetAsync(store, new ContainerId("kept-team", "Other"), "ApiKey", "other-value-1234");
        Assert.Equal("own-value-1234", (await store.NamedValuesAsync(Keeper, Ct))["ApiKey"]);

        var cipher = await database.ColumnAsync("SELECT value_protected FROM plugin_member_named_secrets");
        Assert.All(cipher, c => Assert.DoesNotContain("value-1234", c!, StringComparison.Ordinal));

        // Its own purpose: a field value's protector does not open it.
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Unprotect(
                Protection.CreateProtector(PluginMemberSecretStore.Purpose), cipher[0]!));
        Assert.Empty(await store.ValuesAsync(Keeper, Ct));

        await database.Teams().DeleteMemberAsync("kept-team", "Keeper", Ct);
        Assert.Equal(["Other"], await database.ColumnAsync("SELECT name FROM plugin_member_named_secrets"));

        await database.Teams().DeleteTeamAsync("kept-team", Ct);
        Assert.Empty(await database.ColumnAsync("SELECT name FROM plugin_member_named_secrets"));
    }

    [Fact]
    public async Task Set_replace_and_remove_each_land_with_their_row_naming_the_name_and_never_the_value()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();

        await SetAsync(store, Keeper, "ApiKey", "first-value-1234");
        await SetAsync(store, Keeper, "ApiKey", "second-value-1234", new CredentialActor("u2", "other@example.test"));
        Assert.Equal("second-value-1234", (await store.NamedValuesAsync(Keeper, Ct))["ApiKey"]);
        Assert.Equal("other@example.test", Assert.Single(await store.NamedStatesAsync(Keeper, Ct)).SetBy);

        await SetAsync(store, Keeper, "ApiKey", null);
        Assert.Empty(await store.NamedValuesAsync(Keeper, Ct));

        // Removing a name not held changes nothing and writes no row.
        await SetAsync(store, Keeper, "ApiKey", null);

        Assert.Equal(
            [TenantActions.MemberNamedSecretSet, TenantActions.MemberNamedSecretReplaced, TenantActions.MemberNamedSecretRemoved],
            await database.ColumnAsync("SELECT action FROM tenant_events ORDER BY seq"));
        Assert.All(await database.ColumnAsync("SELECT detail FROM tenant_events"), detail =>
            Assert.Equal(JsonSerializer.Serialize(new { team = "kept-team", member = "Keeper", plugin = "fixture", name = "ApiKey" }), detail));
    }

    /// <summary>THE CASE MAP: a name folds case, unlike a field - one member holds one <c>ApiKey</c>.</summary>
    [Fact]
    public async Task A_name_in_another_case_is_the_same_secret_and_keeps_its_first_spelling()
    {
        var store = await HiredAsync();

        await SetAsync(store, Keeper, "ApiKey", "first-value-1234");
        await SetAsync(store, new ContainerId("KEPT-TEAM", "keeper"), "APIKEY", "upper-value-1234");

        Assert.Equal([("ApiKey", "upper-value-1234")], (await store.NamedValuesAsync(Keeper, Ct)).Select(v => (v.Key, v.Value)));

        await SetAsync(store, Keeper, "apikey", null);
        Assert.Empty(await store.NamedValuesAsync(Keeper, Ct));
    }

    [Fact]
    public async Task A_change_whose_tenant_row_cannot_land_changes_nothing()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();
        await SetAsync(store, Keeper, "Kept", "kept-value-1234");
        await database.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<DbException>(() => store.ChangeNamedAsync(
            Keeper, "Keeper", "fixture", new Dictionary<string, string?> { ["Added"] = "new-value-1234", ["Kept"] = null }, Person, Ct));

        Assert.Equal(["Kept"], (await store.NamedValuesAsync(Keeper, Ct)).Keys);
    }

    [Fact]
    public async Task A_save_with_settings_writes_the_settings_the_field_values_and_the_named_secrets_together_or_nothing()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();
        var settings = database.MemberSettings();
        PluginMemberSettings Mode(string mode) =>
            new(new Dictionary<string, JsonElement> { ["mode"] = JsonSerializer.SerializeToElement(mode) }, new Dictionary<string, string>());

        await store.SaveWithSettingsAsync(
            Keeper, "Keeper", "fixture", Mode("on"), [],
            new Dictionary<string, string?> { ["token"] = "own-value-1234" },
            new Dictionary<string, string?> { ["ApiKey"] = "named-value-1234" }, Person, Ct);

        Assert.Equal("on", (await settings.ForAsync(Keeper, Ct)).Config["mode"].GetString());
        Assert.Equal("named-value-1234", (await store.NamedValuesAsync(Keeper, Ct))["ApiKey"]);
        Assert.Equal([TenantActions.MemberSecretSet, TenantActions.MemberNamedSecretSet], await database.ColumnAsync("SELECT action FROM tenant_events ORDER BY seq"));

        // A named secret's row that cannot land takes the settings and the field value with it.
        await database.ExecuteAsync("DROP TABLE tenant_events");
        await Assert.ThrowsAnyAsync<DbException>(() => store.SaveWithSettingsAsync(
            Keeper, "Keeper", "fixture", Mode("off"), [],
            new Dictionary<string, string?>(),
            new Dictionary<string, string?> { ["ApiKey"] = null, ["Other"] = "other-value-1234" }, Person, Ct));

        Assert.Equal("on", (await settings.ForAsync(Keeper, Ct)).Config["mode"].GetString());
        Assert.Equal(["ApiKey"], (await store.NamedValuesAsync(Keeper, Ct)).Keys);
    }

    [Fact]
    public async Task Two_members_of_one_plugin_never_read_each_others_named_secrets()
    {
        var store = await HiredAsync("kept-team", "One", "Two");
        var one = new ContainerId("kept-team", "One");
        var two = new ContainerId("kept-team", "Two");

        await SetAsync(store, one, "ApiKey", "one-value-1234");
        await SetAsync(store, two, "ApiKey", "two-value-1234");
        await SetAsync(store, two, "ApiKey", "two-changed-1234");

        Assert.Equal("one-value-1234", (await store.NamedValuesAsync(one, Ct))["ApiKey"]);
        Assert.Equal("two-changed-1234", (await store.NamedValuesAsync(two, Ct))["ApiKey"]);
    }
}

public sealed class SqlitePluginMemberNamedSecretRowsTests : PluginMemberNamedSecretRowsContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerPluginMemberNamedSecretRowsTests : PluginMemberNamedSecretRowsContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
