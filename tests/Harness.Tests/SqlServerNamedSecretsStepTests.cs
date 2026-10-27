using Harness.Contracts;
using Harness.Host;
using Harness.Host.SqlServer;
using Harness.SqlServer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace Harness.Tests;

/// <summary>
/// SQL Server's `auth-030`, the twin of <see cref="FreshVolumeTests.The_plugin_member_named_secrets_step_applies_to_an_empty_and_an_existing_volume"/>:
/// it applies to a database holding a team and a member, which stay, and to an empty one; a named secret
/// is kept as ciphertext, one per name ignoring case, and goes with the member's row.
/// </summary>
public sealed class SqlServerNamedSecretsStepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_plugin_member_named_secrets_step_applies_to_an_empty_and_an_existing_database()
    {
        await using var existing = await SqlServerTestDatabase.CreateAsync(Ct, applyModules: false);
        var migrator = new SqlServerMigrator(existing.ConnectionString);
        await migrator.ApplyAsync([.. SqlServerSchemaModules.All.Where(s => s.Id != "auth-030")], Ct);
        Assert.Equal(0, await ScalarAsync(existing, "SELECT COUNT(*) FROM sys.tables WHERE name = 'plugin_member_named_secrets'"));

        var teams = new Harness.SqlServer.Identity.SqlServerTeamStore(existing.ConnectionString);
        await teams.SaveTeamAsync(new PersistedTeam("kept-team", null), Ct);
        await teams.SaveMemberAsync(new PersistedMember("kept-team", "Keeper", null, "plugin:fixture", null, [], [], 0, null), Ct);

        await migrator.ApplyAsync(SqlServerSchemaModules.All, Ct);

        Assert.Contains("auth-030", await migrator.AppliedIfAnyAsync(Ct));
        Assert.Equal(1, await ScalarAsync(existing, "SELECT COUNT(*) FROM team_members WHERE name = N'Keeper'"));

        var store = new PluginMemberSecretStore(new SqlServerPluginMemberSecretRows(existing.ConnectionString), new EphemeralDataProtectionProvider());
        var keeper = new ContainerId("kept-team", "Keeper");
        var person = new CredentialActor(null, "person@example.test");
        await store.ChangeNamedAsync(keeper, "Keeper", "fixture", new Dictionary<string, string?> { ["ApiKey"] = "own-value-1234" }, person, Ct);
        await store.ChangeNamedAsync(keeper, "Keeper", "fixture", new Dictionary<string, string?> { ["APIKEY"] = "new-value-1234" }, person, Ct);
        Assert.Equal("new-value-1234", (await store.NamedValuesAsync(keeper, Ct))["ApiKey"]);
        Assert.Equal(1, await ScalarAsync(existing, "SELECT COUNT(*) FROM plugin_member_named_secrets"));
        Assert.Equal(0, await ScalarAsync(existing, "SELECT COUNT(*) FROM plugin_member_named_secrets WHERE value_protected LIKE N'%value-1234%'"));

        // The row goes with the member's.
        await teams.DeleteMemberAsync("kept-team", "Keeper", Ct);
        Assert.Equal(0, await ScalarAsync(existing, "SELECT COUNT(*) FROM plugin_member_named_secrets"));

        // And an empty database, from nothing.
        await using var empty = await SqlServerTestDatabase.CreateAsync(Ct);
        Assert.Equal(1, await ScalarAsync(empty, "SELECT COUNT(*) FROM sys.tables WHERE name = 'plugin_member_named_secrets'"));
    }

    private static async Task<int> ScalarAsync(SqlServerTestDatabase database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
