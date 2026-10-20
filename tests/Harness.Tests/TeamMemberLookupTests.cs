using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// One member is read by its team and name, as a <see cref="ContainerId"/> compares them, without
/// listing every member of every team.
/// </summary>
public sealed class TeamMemberLookupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-member-lookup-{Guid.NewGuid():N}");
    private readonly string _database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TeamMemberLookupTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "messages.db");
        new SchemaMigrator(_database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_member_is_read_by_team_and_name_in_any_case_and_a_missing_one_is_null()
    {
        var store = new SqliteTeamStore(_database);
        await store.SaveTeamAsync(new PersistedTeam("Alpha", "Alpha"), Ct);
        await store.SaveTeamAsync(new PersistedTeam("Beta", "Beta"), Ct);
        await store.SaveMemberAsync(new PersistedMember("Alpha", "Developer", null, "claude", "be careful", ["x"], ["Read"], 7, "developer"), Ct);
        await store.SaveMemberAsync(new PersistedMember("Alpha", "Tester", null, "claude", null, [], [], 0, null), Ct);
        await store.SaveMemberAsync(new PersistedMember("Beta", "Developer", null, "codex", "other", [], [], 0, null), Ct);

        var found = await store.MemberAsync("alpha", "DEVELOPER", Ct);

        Assert.NotNull(found);
        Assert.Equal("Alpha", found.Team);
        Assert.Equal("Developer", found.Name);
        Assert.Equal("be careful", found.SystemPrompt);
        Assert.Equal(7, found.FloorSeq);
        Assert.Equal(["x"], found.Subscribes);
        Assert.Null(await store.MemberAsync("Alpha", "Designer", Ct));
        Assert.Null(await store.MemberAsync("Gamma", "Developer", Ct));
    }
}
