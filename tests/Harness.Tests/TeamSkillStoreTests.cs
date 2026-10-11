using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Harness.Skills;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// A team skill write and its tenant row are ONE transaction: with <c>tenant_events</c> taken away, a
/// create, an edit, a delete and a team's sweep do not happen. And a built-in shipped later wins its
/// name from a team skill as it does from a custom one, which is kept under another name.
/// </summary>
public sealed class TeamSkillStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("harness-team-skills-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Database => Path.Combine(_directory, "messages.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private async Task<SqliteSkillStore> StoreAsync()
    {
        await new SchemaMigrator(Database).ApplyAsync(SchemaModules.All, ct: Ct);
        return new SqliteSkillStore(Database);
    }

    private static TriggerAudit Audit(string action, string name) => new(null, null, action, "alpha", name, null);

    private static SkillDraft Draft(string name, string body = "Body.") => new(name, "Use it.", ["manager"], body);

    [Fact]
    public async Task A_team_skill_write_whose_tenant_row_cannot_be_written_does_not_happen()
    {
        var store = await StoreAsync();
        await store.PutTeamSkillAsync("alpha", Draft("kept"), replace: false, null, Audit(TenantActions.SkillCreated, "kept"), Ct);

        await using (var connection = new SqliteConnection($"Data Source={Database};Pooling=false"))
        {
            await connection.OpenAsync(Ct);
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE tenant_events";
            await drop.ExecuteNonQueryAsync(Ct);
        }

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.PutTeamSkillAsync("alpha", Draft("lost"), replace: false, null, Audit(TenantActions.SkillCreated, "lost"), Ct));
        await Assert.ThrowsAsync<SqliteException>(() =>
            store.UpdateTeamSkillAsync("alpha", "kept", Draft("kept", "Changed."), null, Audit(TenantActions.SkillChanged, "kept"), Ct));
        await Assert.ThrowsAsync<SqliteException>(() =>
            store.DeleteTeamSkillAsync("alpha", "kept", Audit(TenantActions.SkillDeleted, "kept"), Ct));
        await Assert.ThrowsAsync<SqliteException>(() =>
            store.DeleteTeamSkillsAsync("alpha", name => Audit(TenantActions.SkillDeleted, name), Ct));

        var left = Assert.Single(await store.ListTeamAsync("alpha", Ct));
        Assert.Equal("kept", left.Name);
        Assert.Equal("Body.", left.Body);
    }

    [Fact]
    public async Task A_built_in_shipped_later_wins_its_name_from_a_team_skill_which_is_kept_under_another()
    {
        var store = await StoreAsync();
        await store.PutTeamSkillAsync("alpha", Draft("later-builtin", "Mine."), replace: false, null, Audit(TenantActions.SkillCreated, "later-builtin"), Ct);

        var moved = await store.ReplaceBuiltInsAsync([Draft("later-builtin", "The build's.")], DateTimeOffset.UtcNow, Ct);

        Assert.Contains(moved, m => m.Contains("later-builtin -> later-builtin-custom", StringComparison.Ordinal));
        Assert.Equal("The build's.", (await store.GetAsync("later-builtin", Ct))!.Body);
        Assert.Equal("Mine.", Assert.Single(await store.ListTeamAsync("alpha", Ct)).Body);
    }

    [Fact]
    public async Task What_a_role_is_offered_instance_wide_leaves_out_every_team_skill()
    {
        var store = await StoreAsync();
        await store.PutTeamSkillAsync("alpha", Draft("alpha-only", "zanzibar"), replace: false, null, Audit(TenantActions.SkillCreated, "alpha-only"), Ct);

        Assert.Empty(await store.ListAsync(SkillKindFilter.All, "zanzibar", SkillRoles.Manager, null, 50, Ct));
        Assert.Single(await store.ListAsync(SkillKindFilter.All, "zanzibar", null, null, 50, Ct));
        Assert.Single(await store.ListOfferedAsync(SkillRoles.Manager, "ALPHA", "zanzibar", 50, Ct));
        Assert.Empty(await store.ListOfferedAsync(SkillRoles.Manager, "beta", "zanzibar", 50, Ct));
        Assert.Empty(await store.ListOfferedAsync(SkillRoles.Manager, null, "zanzibar", 50, Ct));
        Assert.Null(await store.GetAsync("alpha-only", Ct));
    }
}
