using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// HOW LONG A RECORDED FILE CHANGE LIVES, AND WHO READS IT. The log's purge removes the rows older
/// than the oldest log row it keeps; a team's deletion removes its rows and a same-name successor
/// reads none of its predecessor's; Reset's Delete memory removes the ticked members' rows only;
/// and <c>GET /api/file-changes</c> is a person's read, oldest first and paged. Rows are written
/// through <see cref="WorkspaceFileChangeStore"/> as the watch writes them; what the watch writes
/// is <see cref="WorkspaceFileChangeTests"/>'.
/// </summary>
public sealed class WorkspaceFileChangeReplayTests(WorkspaceFileChangeReplayTests.Bed bed)
    : IClassFixture<WorkspaceFileChangeReplayTests.Bed>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_log_purge_removes_the_file_changes_older_than_the_oldest_log_row_kept_and_keeps_the_rest()
    {
        var directory = Directory.CreateTempSubdirectory("harness-file-changes-purge-").FullName;
        try
        {
            var database = Path.Combine(directory, "messages.db");
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct: Ct);
            var log = new SqliteMessageStore(database);
            var store = new WorkspaceFileChangeStore(database);

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
            Assert.Equal(["at.txt", "after.txt"], await PathsAsync(store, Everyone("Alpha", "Beta")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Deleting_a_team_removes_its_rows_leaves_another_teams_and_its_deleted_row_counts_them()
    {
        var gone = await bed.TeamAsync("Replay deleted", "Dev");
        var kept = await bed.TeamAsync("Replay kept", "Dev");
        var at = DateTimeOffset.UtcNow;

        await bed.Store.AppendAsync(Change(gone, "Dev", at, "gone/one.txt"), 1, Ct);
        await bed.Store.AppendAsync(Change(gone, "Manager", at, "gone/two.txt"), 1, Ct);
        await bed.Store.AppendAsync(Change(kept, "Dev", at, "kept/one.txt"), 2, Ct);

        var deleted = await bed.Person.DeleteAsync($"/api/teams/{gone}", Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(2, (await deleted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("fileChanges").GetInt32());

        Assert.Equal(0, await bed.CountAsync(gone));
        Assert.Equal(["kept/one.txt"], await PathsAsync(bed.Store, Everyone(kept)));

        var row = await bed.TenantRowAsync(TenantActions.TeamDeleted, gone);
        Assert.Equal(2, JsonDocument.Parse(row.Detail!).RootElement.GetProperty("fileChanges").GetInt32());
    }

    [Fact]
    public async Task A_team_created_again_under_the_same_name_reads_none_of_its_predecessors_rows()
    {
        const string label = "Replay successor";
        var first = await bed.TeamAsync(label, "Dev");
        var deleted = await bed.Person.DeleteAsync($"/api/teams/{first}", Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        // A predecessor's row the deletion did not reach: one its last read wrote as it went.
        var leftover = DateTimeOffset.UtcNow;
        await bed.Store.AppendAsync(Change(first, "Dev", leftover, "predecessor.txt"), 1, Ct);
        await Task.Delay(20, Ct);

        var second = await bed.TeamAsync(label, "Dev");
        Assert.Equal(first, second);
        var created = await bed.CreatedAsync(second);
        Assert.True(created > leftover);
        await bed.Store.AppendAsync(Change(second, "Dev", created.AddSeconds(1), "successor.txt"), 2, Ct);

        var read = await bed.ReadAsync($"?to={Instant(created.AddMinutes(1))}");
        Assert.Equal(["successor.txt"], Paths(read, second));
    }

    [Fact]
    public async Task Reset_with_delete_memory_removes_only_the_ticked_members_rows_and_its_reset_row_counts_them()
    {
        var team = await bed.TeamAsync("Replay reset", "Dev", "Ops");
        var other = await bed.TeamAsync("Replay reset other", "Dev");
        var at = DateTimeOffset.UtcNow;

        await bed.Store.AppendAsync(Change(team, "Dev", at, "dev/one.txt"), 1, Ct);
        await bed.Store.AppendAsync(Change(team, "Dev", at, "dev/two.txt"), 1, Ct);
        await bed.Store.AppendAsync(Change(team, "Ops", at, "ops/one.txt"), 1, Ct);
        await bed.Store.AppendAsync(Change(other, "Dev", at, "other/one.txt"), 2, Ct);

        // Without Delete memory nothing goes.
        var floored = await bed.Person.PostAsJsonAsync($"/api/teams/{team}/reset", new { members = new[] { "Dev" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, floored.StatusCode);
        Assert.Equal(3, await bed.CountAsync(team));

        var reset = await bed.Person.PostAsJsonAsync(
            $"/api/teams/{team}/reset", new { members = new[] { "Dev" }, purge = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(2, (await reset.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("fileChanges").GetInt32());

        Assert.Equal(["ops/one.txt"], await PathsAsync(bed.Store, Everyone(team)));
        Assert.Equal(["other/one.txt"], await PathsAsync(bed.Store, Everyone(other)));

        var row = await bed.TenantRowAsync(TenantActions.TeamReset, team);
        Assert.Equal(2, JsonDocument.Parse(row.Detail!).RootElement.GetProperty("fileChanges").GetInt32());
    }

    [Fact]
    public async Task The_route_refuses_a_machine_principal()
    {
        var team = await bed.TeamAsync("Replay machine", "Dev");
        using var member = await bed.ContainerAsync(team, "Dev");

        var refused = await member.GetAsync("/api/file-changes", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task The_route_answers_a_periods_changes_oldest_first_paged_with_the_push_shape()
    {
        var team = await bed.TeamAsync("Replay paged", "Dev", "Ops");

        // A period of its own, after every other test's rows, so the pages are this test's alone.
        var t0 = DateTimeOffset.UtcNow.AddDays(30).AddMinutes(Random.Shared.Next(100_000));
        await bed.Store.AppendAsync(Change(team, "Dev", t0.AddSeconds(-10), "before.txt"), 7, Ct);
        await bed.Store.AppendAsync(Change(team, "Dev", t0, "one.txt"), 7, Ct);
        await bed.Store.AppendAsync(Change(team, "Ops", t0.AddSeconds(1), "two.txt"), null, Ct);
        await bed.Store.AppendAsync(Change(team, "Dev", t0.AddSeconds(2), "three.txt", commit: true), 7, Ct);
        await bed.Store.AppendAsync(Change(team, "Dev", t0.AddSeconds(3), "four.txt"), 7, Ct);
        await bed.Store.AppendAsync(Change(team, "Dev", t0.AddSeconds(30), "after.txt"), 7, Ct);

        var period = $"from={Instant(t0)}&to={Instant(t0.AddSeconds(3))}";
        var first = await bed.ReadAsync($"?{period}&take=2");
        Assert.Equal(["one.txt", "two.txt"], Paths(first, team));
        var next = first.GetProperty("next").GetInt64();

        var second = await bed.ReadAsync($"?{period}&take=2&after={next}");
        Assert.Equal(["three.txt", "four.txt"], Paths(second, team));

        var last = await bed.ReadAsync($"?{period}&take=2&after={second.GetProperty("next").GetInt64()}");
        Assert.Empty(last.GetProperty("changes").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("next").ValueKind);

        var one = first.GetProperty("changes")[0];
        Assert.Equal(team, one.GetProperty("team").GetString());
        Assert.Equal("Dev", one.GetProperty("member").GetString());
        Assert.Equal("Widget", one.GetProperty("repo").GetString());
        Assert.Equal(t0, one.GetProperty("at").GetDateTimeOffset());
        Assert.Equal(7, one.GetProperty("correlation").GetInt64());
        Assert.Equal(0, one.GetProperty("more").GetInt32());
        var file = Assert.Single(one.GetProperty("files").EnumerateArray());
        Assert.Equal("one.txt", file.GetProperty("path").GetString());
        Assert.Equal("Widget", file.GetProperty("project").GetString());
        Assert.Equal("created", file.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("changes")[1].GetProperty("correlation").ValueKind);

        var commit = Assert.Single(second.GetProperty("changes")[0].GetProperty("commits").EnumerateArray());
        Assert.Equal("abc1234", commit.GetProperty("sha").GetString());
        Assert.Equal("Add three.txt", commit.GetProperty("subject").GetString());

        var backwards = await bed.Person.GetAsync($"/api/file-changes?from={Instant(t0)}&to={Instant(t0.AddSeconds(-1))}", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
    }

    private static WorkspaceChange Change(string team, string member, DateTimeOffset at, string path, bool commit = false)
    {
        var file = new WorkspaceFile(path, "Widget", WorkspaceFileChanges.Created);
        return commit
            ? new WorkspaceChange(team, member, "Widget", at, [], [new WorkspaceCommit("abc1234", $"Add {path}", [file])], 0)
            : new WorkspaceChange(team, member, "Widget", at, [file], [], 0);
    }

    private static Dictionary<string, DateTimeOffset> Everyone(params string[] teams) =>
        teams.ToDictionary(team => team, _ => DateTimeOffset.MinValue, StringComparer.OrdinalIgnoreCase);

    private static async Task<List<string>> PathsAsync(WorkspaceFileChangeStore store, Dictionary<string, DateTimeOffset> teams) =>
        [.. (await store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 500, teams, Ct))
            .SelectMany(c => c.Files.Concat(c.Commits.SelectMany(k => k.Files)))
            .Select(f => f.Path)];

    private static List<string> Paths(JsonElement page, string team) =>
        [.. page.GetProperty("changes").EnumerateArray()
            .Where(c => c.GetProperty("team").GetString() == team)
            .SelectMany(c => c.GetProperty("files").EnumerateArray()
                .Concat(c.GetProperty("commits").EnumerateArray().SelectMany(k => k.GetProperty("files").EnumerateArray())))
            .Select(f => f.GetProperty("path").GetString()!)];

    private static string Instant(DateTimeOffset at) => Uri.EscapeDataString(at.UtcDateTime.ToString("o"));

    public sealed class Bed : IAsyncLifetime
    {
        private const string Email = "person@example.test";

        private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-file-change-replay-{Guid.NewGuid():N}");
        private WebApplicationFactory<Program> _factory = null!;

        public HttpClient Person { get; private set; } = null!;

        public WorkspaceFileChangeStore Store => _factory.Services.GetRequiredService<WorkspaceFileChangeStore>();

        public async ValueTask InitializeAsync()
        {
            Directory.CreateDirectory(_dataRoot);

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
                .UseSetting("DataRoot", _dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

            // Made at run time: no sign-in secret is written into the test.
            var password = $"pw-{Guid.NewGuid():N}";
            await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, password);
            Person = _factory.CreateClient();
            (await Person.PostAsJsonAsync("/api/auth/login", new { email = Email, password })).EnsureSuccessStatusCode();

            // The route answers nothing older than the oldest log row, so the log holds one first.
            await _factory.Services.GetRequiredService<IMessageLog>().AppendAsync(
                new NewMessage("test.file-changes", "{}", "test"));
            await Task.Delay(20);
        }

        public async ValueTask DisposeAsync()
        {
            Person.Dispose();
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
        }

        /// <summary>A new team with these members beside its Manager; answers its id.</summary>
        public async Task<string> TeamAsync(string label, params string[] members)
        {
            var team = (await _factory.Services.GetRequiredService<TeamRegistry>()
                .CreateAsync(label, "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

            foreach (var member in members)
            {
                var added = await Person.PostAsJsonAsync(
                    $"/api/teams/{team}/containers", new { name = member, agent = "claude-headless" }, Ct);
                Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            }

            return team;
        }

        public async Task<DateTimeOffset> CreatedAsync(string team) =>
            await _factory.Services.GetRequiredService<ITeamStore>().CreatedAtAsync(team, Ct)
                ?? throw new InvalidOperationException($"{team} has no creation time.");

        public async Task<HttpClient> ContainerAsync(string team, string member)
        {
            var key = await _factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
                new ContainerId(team, member).ToString(), PrincipalKind.Container, team, Permits.All);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
            return client;
        }

        public async Task<JsonElement> ReadAsync(string query)
        {
            var answer = await Person.GetAsync($"/api/file-changes{query}", Ct);
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            return await answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        /// <summary>Every row of the team, whatever its time.</summary>
        public async Task<int> CountAsync(string team) =>
            (await Store.ReadAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 500, Everyone(team), Ct)).Count;

        public async Task<TenantEvent> TenantRowAsync(string action, string team) =>
            (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(null, ITenantLog.MaxTake, Ct)).Events
                .First(e => e.Action == action && string.Equals(e.Subject, team, StringComparison.OrdinalIgnoreCase));
    }
}
