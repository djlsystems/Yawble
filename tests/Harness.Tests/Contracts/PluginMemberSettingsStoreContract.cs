using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IPluginMemberSettingsStore"/>, as each provider must keep it: a member with no row has
/// none; a save replaces the row whole; a save with tenant rows lands with every one or not at all; the
/// connections and keys bound on a team are its members' only; the row goes with its member and its team.
/// These are the interface's and <c>SqlitePluginMemberSettings</c>' remarks (the SQLite suite reaches the
/// store only through the Host). Then the case map of a team and a member, long text, and concurrency:
/// two first saves of one member, and a team deleted while its members' settings are saved.
/// </summary>
public abstract class PluginMemberSettingsStoreContract : HostStoreContract
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");

    private static PluginMemberSettings Settings(string mode, IReadOnlyDictionary<string, string>? secrets = null, IReadOnlyDictionary<string, string>? connections = null) =>
        new(new Dictionary<string, JsonElement> { ["mode"] = JsonSerializer.SerializeToElement(mode), ["limit"] = JsonSerializer.SerializeToElement(5) },
            secrets ?? new Dictionary<string, string>())
        {
            Connections = connections ?? new Dictionary<string, string>(),
        };

    private static (string, string) Pair(KeyValuePair<string, string> entry) => (entry.Key, entry.Value);

    private async Task<IPluginMemberSettingsStore> HiredAsync(params string[] members)
    {
        var database = await DatabaseAsync();
        await HireAsync(database, "Alpha", members.Length == 0 ? ["Dev"] : members);
        return database.MemberSettings();
    }

    [Fact]
    public async Task A_member_with_no_row_has_no_settings()
    {
        var store = await HiredAsync();

        var none = await store.ForAsync(Dev, Ct);

        Assert.Empty(none.Config);
        Assert.Empty(none.Secrets);
        Assert.Empty(none.Connections);
    }

    [Fact]
    public async Task Settings_round_trip_and_a_save_replaces_them_whole()
    {
        var store = await HiredAsync();

        await store.SaveAsync(Dev, Settings("fast", new Dictionary<string, string> { ["token"] = "ACME_KEY" }, new Dictionary<string, string> { ["mail"] = "conn-1" }), Ct);
        var read = await store.ForAsync(Dev, Ct);
        Assert.Equal(("fast", 5), (read.Config["mode"].GetString(), read.Config["limit"].GetInt32()));
        Assert.Equal(("token", "ACME_KEY"), Pair(Assert.Single(read.Secrets)));
        Assert.Equal(("mail", "conn-1"), Pair(Assert.Single(read.Connections)));

        await store.SaveAsync(Dev, Settings("slow"), Ct);
        var replaced = await store.ForAsync(Dev, Ct);
        Assert.Equal("slow", replaced.Config["mode"].GetString());
        Assert.Empty(replaced.Secrets);
        Assert.Empty(replaced.Connections);
    }

    [Fact]
    public async Task A_save_with_tenant_rows_lands_with_every_one()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();

        await store.SaveAsync(Dev, Settings("one"), Audit(TenantActions.MemberPluginSettingsChanged, "Alpha/Dev"), Ct);
        await store.SaveAsync(Dev, Settings("two"), [Audit(TenantActions.MemberPluginSettingsChanged, "Alpha/Dev"), Audit(TenantActions.MemberConnectionsChanged, "Alpha/Dev")], Ct);

        Assert.Equal("two", (await store.ForAsync(Dev, Ct)).Config["mode"].GetString());
        Assert.Equal(
            [TenantActions.MemberPluginSettingsChanged, TenantActions.MemberPluginSettingsChanged, TenantActions.MemberConnectionsChanged],
            await database.ColumnAsync("SELECT action FROM tenant_events WHERE subject = 'Alpha/Dev' ORDER BY seq"));
    }

    [Fact]
    public async Task A_save_whose_tenant_row_cannot_be_written_changes_nothing()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();
        await store.SaveAsync(Dev, Settings("kept"), Ct);
        await database.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<DbException>(() => store.SaveAsync(Dev, Settings("changed"), Audit(TenantActions.MemberPluginSettingsChanged, "Alpha/Dev"), Ct));

        Assert.Equal("kept", (await store.ForAsync(Dev, Ct)).Config["mode"].GetString());
    }

    [Fact]
    public async Task The_connections_and_keys_bound_on_a_team_are_its_members_only()
    {
        var database = await DatabaseAsync();
        await HireAsync(database, "Alpha", "Dev", "Ops");
        await HireAsync(database, "Beta", "Dev");
        var store = database.MemberSettings();

        await store.SaveAsync(Dev, Settings("a", new Dictionary<string, string> { ["token"] = "ACME_KEY" }, new Dictionary<string, string> { ["mail"] = "conn-1" }), Ct);
        await store.SaveAsync(new ContainerId("Alpha", "Ops"), Settings("b", new Dictionary<string, string> { ["token"] = "ACME_KEY", ["other"] = "OTHER_KEY" }, new Dictionary<string, string> { ["mail"] = "conn-2", ["files"] = "conn-1" }), Ct);
        await store.SaveAsync(new ContainerId("Beta", "Dev"), Settings("c", new Dictionary<string, string> { ["token"] = "BETA_KEY" }, new Dictionary<string, string> { ["mail"] = "conn-9" }), Ct);

        Assert.Equal(["conn-1", "conn-2"], (await store.ConnectionsBoundOnAsync("Alpha", Ct)).Order(StringComparer.Ordinal));
        Assert.Equal(["ACME_KEY", "OTHER_KEY"], (await store.KeysBoundOnAsync("Alpha", Ct)).Order(StringComparer.Ordinal));
        Assert.Equal(["conn-9"], await store.ConnectionsBoundOnAsync("Beta", Ct));
        Assert.Empty(await store.KeysBoundOnAsync("Gamma", Ct));
    }

    [Fact]
    public async Task The_row_goes_with_its_member_and_its_team()
    {
        var database = await DatabaseAsync();
        await HireAsync(database, "Alpha", "Dev", "Ops");
        var store = database.MemberSettings();
        await store.SaveAsync(Dev, Settings("dev"), Ct);
        await store.SaveAsync(new ContainerId("Alpha", "Ops"), Settings("ops"), Ct);

        await database.Teams().DeleteMemberAsync("Alpha", "Dev", Ct);
        Assert.Empty((await store.ForAsync(Dev, Ct)).Config);
        Assert.Equal("ops", (await store.ForAsync(new ContainerId("Alpha", "Ops"), Ct)).Config["mode"].GetString());

        await database.Teams().DeleteTeamAsync("Alpha", Ct);
        Assert.Equal(0, await database.LongAsync("SELECT COUNT(*) FROM team_member_config"));
    }

    [Fact]
    public async Task A_save_for_no_such_member_is_refused()
    {
        var store = await HiredAsync();

        await Assert.ThrowsAnyAsync<DbException>(() => store.SaveAsync(new ContainerId("Alpha", "Nobody"), Settings("x"), Ct));
    }

    /// <summary>THE CASE MAP: a team and a member fold case, as SQLite's NOCASE key does.</summary>
    [Fact]
    public async Task A_team_or_member_differing_only_in_case_is_the_same_member()
    {
        var database = await DatabaseAsync();
        var store = await HiredAsync();
        await store.SaveAsync(Dev, Settings("first", connections: new Dictionary<string, string> { ["mail"] = "conn-1" }), Ct);

        await store.SaveAsync(new ContainerId("ALPHA", "dev"), Settings("second", connections: new Dictionary<string, string> { ["mail"] = "conn-2" }), Ct);

        Assert.Equal("second", (await store.ForAsync(new ContainerId("alpha", "DEV"), Ct)).Config["mode"].GetString());
        Assert.Equal(1, await database.LongAsync("SELECT COUNT(*) FROM team_member_config"));
        Assert.Equal(["conn-2"], await store.ConnectionsBoundOnAsync("aLPHA", Ct));
    }

    /// <summary>THE TRAILING-SPACE MAP: a team id with a trailing space binds nothing, as on SQLite.</summary>
    [Fact]
    public async Task A_team_with_a_trailing_space_binds_nothing()
    {
        var store = await HiredAsync();
        await store.SaveAsync(Dev, Settings("a", new Dictionary<string, string> { ["token"] = "ACME_KEY" }, new Dictionary<string, string> { ["mail"] = "conn-1" }), Ct);

        Assert.Empty(await store.ConnectionsBoundOnAsync("Alpha ", Ct));
        Assert.Empty(await store.KeysBoundOnAsync("Alpha ", Ct));
    }

    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var store = await HiredAsync();
        var text = LongUnicode("config");

        await store.SaveAsync(Dev, Settings(text), Ct);

        Assert.Equal(text, (await store.ForAsync(Dev, Ct)).Config["mode"].GetString());
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight first saves of one member's settings at once. Every save succeeds and one
    /// row stands, holding one of them whole.
    /// </summary>
    [Fact]
    public async Task Concurrent_first_saves_of_one_member_make_one_row()
    {
        var database = await DatabaseAsync();
        var members = Enumerable.Range(0, 12).Select(round => $"M{round}").ToArray();
        await HireAsync(database, "Alpha", members);
        var store = database.MemberSettings();

        foreach (var name in members)
        {
            var member = new ContainerId("Alpha", name);
            await RaceAsync(8, async i =>
            {
                await store.SaveAsync(member, Settings($"v{i}", connections: new Dictionary<string, string> { ["mail"] = $"conn-{i}" }), Ct);
                return i;
            });

            var read = await store.ForAsync(member, Ct);
            Assert.Equal(read.Config["mode"].GetString()![1..], Assert.Single(read.Connections).Value["conn-".Length..]);
        }

        Assert.Equal(members.Length, await database.LongAsync("SELECT COUNT(*) FROM team_member_config"));
    }

    /// <summary>
    /// THE LOCK ORDER (teams, member, settings): a team deleted while eight of its members' settings are
    /// saved. Each save lands or is refused by the foreign key, never anything else such as a deadlock,
    /// and nothing of the team is left.
    /// </summary>
    [Fact]
    public async Task A_team_deleted_while_its_members_settings_are_saved_leaves_no_row_and_no_deadlock()
    {
        var database = await DatabaseAsync();
        var store = database.MemberSettings();
        var teams = database.Teams();

        for (var round = 0; round < 12; round++)
        {
            var team = $"Doomed{round}";
            await HireAsync(database, team, [.. Enumerable.Range(0, 8).Select(i => $"M{i}")]);

            var outcomes = await RaceAsync(9, async i =>
            {
                try
                {
                    if (i == 8) await teams.DeleteTeamAsync(team, Ct);
                    else await store.SaveAsync(new ContainerId(team, $"M{i}"), Settings($"v{i}"), Audit(TenantActions.MemberPluginSettingsChanged, $"{team}/M{i}"), Ct);

                    return null;
                }
                catch (DbException refused)
                {
                    return refused.Message;
                }
            });

            Assert.All(outcomes, refusal => Assert.True(refusal is null || refusal.Contains("FOREIGN KEY", StringComparison.Ordinal), refusal));
            Assert.Equal(0, await database.LongAsync($"SELECT COUNT(*) FROM team_member_config WHERE team = '{team}'"));
        }
    }
}

public sealed class SqlitePluginMemberSettingsStoreTests : PluginMemberSettingsStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerPluginMemberSettingsStoreTests : PluginMemberSettingsStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
