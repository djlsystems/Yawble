using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IWorkspaceFileChangeStore"/>, as each provider must keep it. From the existing SQLite tests,
/// same names and values: <c>FreshVolumeTests</c>' change round trip and
/// <c>WorkspaceFileChangeReplayTests.A_log_purge_removes_the_file_changes_older_than_the_oldest_log_row_kept_and_keeps_the_rest</c>
/// (the log's own delete pruning these rows). The rest are the interface's remarks, the case and
/// trailing-space map of a team and a member, and long text.
/// </summary>
public abstract class WorkspaceFileChangeStoreContract : HostStoreContract
{
    protected async Task<IWorkspaceFileChangeStore> CreateAsync() => (await DatabaseAsync()).FileChanges();

    private static readonly DateTimeOffset At = new(2026, 10, 16, 20, 0, 1, TimeSpan.Zero);

    private static WorkspaceChange Change(string team, string member, DateTimeOffset at, string path) =>
        new(team, member, "Widget", at, [new WorkspaceFile(path, "Widget", WorkspaceFileChanges.Changed)], [], 0);

    private static async Task<List<string>> PathsAsync(IWorkspaceFileChangeStore store, Dictionary<string, DateTimeOffset> teams) =>
        [.. (await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 500, teams, Ct)).SelectMany(c => c.Files).Select(f => f.Path)];

    [Fact]
    public async Task A_change_round_trips_with_its_files_its_commits_its_more_and_its_run()
    {
        var store = await CreateAsync();
        var file = new WorkspaceFile("src/App/New.cs", "App", WorkspaceFileChanges.Created);
        await store.AppendAsync(new WorkspaceChange("Alpha", "Dev", "Widget", At, [file], [new WorkspaceCommit("abc1234", "Add it", [file])], 3), 9, Ct);

        var teams = new Dictionary<string, DateTimeOffset> { ["Alpha"] = DateTimeOffset.MinValue };
        var row = Assert.Single(await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 10, teams, Ct));
        Assert.Equal(("Alpha", "Dev", "Widget", At, 3, 9L), (row.Team, row.Member, row.Repo, row.At, row.More, row.Correlation!.Value));
        Assert.Equal([file], row.Files);
        Assert.Equal(("abc1234", "Add it"), (Assert.Single(row.Commits).Sha, row.Commits[0].Subject));

        Assert.Equal(1, await store.DeleteForTeamAsync("Alpha", Ct));
        Assert.Empty(await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 10, teams, Ct));
    }

    [Fact]
    public async Task A_change_with_no_run_reads_back_with_none()
    {
        var store = await CreateAsync();
        await store.AppendAsync(Change("Alpha", "Dev", At, "a.txt"), null, Ct);

        Assert.Null(Assert.Single(await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 10, Everyone("Alpha"), Ct)).Correlation);
    }

    [Fact]
    public async Task A_log_purge_removes_the_file_changes_older_than_the_oldest_log_row_kept_and_keeps_the_rest()
    {
        var database = await DatabaseAsync();
        var log = database.Log();
        var store = database.FileChanges();

        var first = await log.AppendAsync(new NewMessage("test.file-changes", "{}", "test"), Ct);
        await Task.Delay(20, Ct);
        var second = await log.AppendAsync(new NewMessage("test.file-changes", "{}", "test"), Ct);
        Assert.True(second.OccurredAt > first.OccurredAt);

        await store.AppendAsync(Change("Alpha", "Dev", first.OccurredAt.AddMinutes(-1), "before.txt"), 1, Ct);
        await store.AppendAsync(Change("Alpha", "Dev", first.OccurredAt.AddTicks(1), "between.txt"), 1, Ct);
        await store.AppendAsync(Change("Alpha", "Dev", second.OccurredAt, "at.txt"), 1, Ct);
        await store.AppendAsync(Change("Beta", "Ops", second.OccurredAt.AddMinutes(1), "after.txt"), 2, Ct);

        var report = await log.DeleteAsync([first.Seq], Ct);

        Assert.Equal(1, report.Purged);
        Assert.Equal(2, report.FileChanges);
        Assert.Equal(["at.txt", "after.txt"], await PathsAsync(store, Everyone("Alpha", "Beta")));
    }

    [Fact]
    public async Task A_read_is_scoped_by_each_teams_creation_and_paged_oldest_first()
    {
        var store = await CreateAsync();
        for (var i = 0; i < 5; i++) await store.AppendAsync(Change("Alpha", "Dev", At.AddMinutes(i), $"a{i}"), null, Ct);
        await store.AppendAsync(Change("Beta", "Dev", At.AddMinutes(2), "b"), null, Ct);
        await store.AppendAsync(Change("Gamma", "Dev", At, "g"), null, Ct);

        var since = new Dictionary<string, DateTimeOffset> { ["Alpha"] = At.AddMinutes(1), ["Beta"] = DateTimeOffset.MinValue };
        var all = await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 100, since, Ct);
        Assert.Equal(["a1", "a2", "a3", "a4", "b"], all.Select(c => c.Files[0].Path));

        var page = await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, all[1].Id, 2, since, Ct);
        Assert.Equal(["a3", "a4"], page.Select(c => c.Files[0].Path));

        var window = await store.ReadAsync(At.AddMinutes(2), At.AddMinutes(3), 0, 100, since, Ct);
        Assert.Equal(["a2", "a3", "b"], window.Select(c => c.Files[0].Path));

        Assert.Empty(await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 100, new Dictionary<string, DateTimeOffset>(), Ct));
        Assert.Empty(await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 0, since, Ct));
    }

    [Fact]
    public async Task Deleting_members_removes_only_their_changes_and_counts_them()
    {
        var store = await CreateAsync();
        await store.AppendAsync(Change("Alpha", "Dev", At, "dev"), null, Ct);
        await store.AppendAsync(Change("Alpha", "Ops", At, "ops"), null, Ct);
        await store.AppendAsync(Change("Alpha", "Lead", At, "lead"), null, Ct);
        await store.AppendAsync(Change("Beta", "Dev", At, "beta"), null, Ct);

        Assert.Equal(0, await store.DeleteForMembersAsync("Alpha", [], Ct));
        Assert.Equal(2, await store.DeleteForMembersAsync("Alpha", ["Dev", "Ops", "Nobody"], Ct));

        Assert.Equal(["lead", "beta"], await PathsAsync(store, Everyone("Alpha", "Beta")));
    }

    [Fact]
    public async Task A_team_or_member_differing_only_in_case_is_the_same()
    {
        var store = await CreateAsync();
        await store.AppendAsync(Change("Alpha", "Dev", At, "one"), null, Ct);

        Assert.Equal(["one"], await PathsAsync(store, new Dictionary<string, DateTimeOffset> { ["ALPHA"] = DateTimeOffset.MinValue }));
        Assert.Equal(1, await store.DeleteForMembersAsync("alpha", ["DEV"], Ct));

        await store.AppendAsync(Change("Alpha", "Dev", At, "two"), null, Ct);
        Assert.Equal(1, await store.DeleteForTeamAsync("aLPHA", Ct));
    }

    [Fact]
    public async Task A_team_or_member_with_a_trailing_space_is_another()
    {
        var store = await CreateAsync();
        await store.AppendAsync(Change("Alpha", "Dev", At, "one"), null, Ct);

        Assert.Empty(await PathsAsync(store, new Dictionary<string, DateTimeOffset> { ["Alpha "] = DateTimeOffset.MinValue }));
        Assert.Equal(0, await store.DeleteForMembersAsync("Alpha", ["Dev "], Ct));
        Assert.Equal(0, await store.DeleteForTeamAsync("Alpha ", Ct));
        Assert.Equal(["one"], await PathsAsync(store, Everyone("Alpha")));
    }

    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var store = await CreateAsync();
        var path = LongUnicode("src/");
        var subject = LongUnicode("subject");
        var file = new WorkspaceFile(path, "Widget", WorkspaceFileChanges.Created);
        await store.AppendAsync(new WorkspaceChange("Alpha", "Dev", "Wïdget 🚀", At, [file], [new WorkspaceCommit("abc1234", subject, [file])], 0), null, Ct);

        var row = Assert.Single(await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 10, Everyone("Alpha"), Ct));
        Assert.Equal(("Wïdget 🚀", path, subject), (row.Repo, row.Files[0].Path, row.Commits[0].Subject));
    }
}

public sealed class SqliteWorkspaceFileChangeStoreTests : WorkspaceFileChangeStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerWorkspaceFileChangeStoreTests : WorkspaceFileChangeStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
