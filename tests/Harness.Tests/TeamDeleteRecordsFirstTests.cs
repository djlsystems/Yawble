using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harness.Tests;

/// <summary>
/// A TEAM DELETION RECORDS FIRST (B0027). <c>team.deleting</c> is appended to the tenant log BEFORE
/// anything is removed, and not through the swallowing <c>TenantLogging</c>: when it cannot be
/// written the delete answers 500 with a sentence and the team, its containers and its root are all
/// still there. <c>team.deleted</c> stays the row after the act, carrying the result.
/// </summary>
public sealed class TeamDeleteRecordsFirstTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-delete-records-first-").FullName;
    private readonly WatchingLog _log = new();
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TeamDeleteRecordsFirstTests()
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());

                // The real log, refusing team.deleting when a test asks and noting what was still
                // there at the moment that row was written.
                _log.Inner = (ITenantLog)services.Last(d => d.ServiceType == typeof(ITenantLog)).ImplementationInstance!;
                services.Replace(ServiceDescriptor.Singleton<ITenantLog>(_log));
            }));
        _log.Teams = () => _factory.Services.GetRequiredService<TeamRegistry>();
    }

    private TeamRegistry Teams => _factory.Services.GetRequiredService<TeamRegistry>();

    private string RootOf(string team) => _factory.Services.GetRequiredService<TeamPaths>().RootFor(team);

    [Fact]
    public async Task A_team_deleting_row_that_cannot_be_written_refuses_with_500_and_removes_nothing()
    {
        var person = await PersonAsync();
        var team = await CreateTeamAsync(person, "Unrecorded");
        var root = RootOf(team);
        var containers = Teams.ContainerIdsOf(team);
        Assert.NotEmpty(containers);
        _log.RefuseTeamDeleting = true;

        var refused = await person.DeleteAsync($"/api/teams/{team}", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, refused.StatusCode);
        var error = (await JsonAsync(refused)).GetProperty("error").GetString()!;
        Assert.Equal(
            $"'{team}' was not deleted: its tenant log row could not be written (the disk is full). Nothing was removed.",
            error);

        // Nothing moved: the team, every container, the root and its marker are all still there.
        Assert.Equal(team, Teams.ExistingName(team));
        Assert.Equal(containers, Teams.ContainerIdsOf(team));
        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(root, TeamPaths.MarkerFileName)));
        Assert.Empty(await TenantRowsAsync(TenantActions.TeamDeleting));
        Assert.Empty(await TenantRowsAsync(TenantActions.TeamDeleted));

        // And once the row can be written, the same delete goes through.
        _log.RefuseTeamDeleting = false;
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{team}", Ct)).StatusCode);
        Assert.Null(Teams.ExistingName(team));
    }

    [Fact]
    public async Task A_team_delete_writes_team_deleting_before_anything_is_removed_and_team_deleted_with_the_result_after()
    {
        var person = await PersonAsync();
        var team = await CreateTeamAsync(person, "Recorded");
        var root = RootOf(team);
        var containers = Teams.ContainerIdsOf(team).Select(id => id.ToString()).ToArray();

        var deleted = await person.DeleteAsync($"/api/teams/{team}", Ct);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Null(Teams.ExistingName(team));
        Assert.False(Directory.Exists(root));

        // Before: written while the team was registered and its root and marker still on disk,
        // naming the containers and root it was about to remove, by the person who asked.
        var before = Assert.Single(await TenantRowsAsync(TenantActions.TeamDeleting));
        Assert.Equal(new Seen(TeamRegistered: true, RootThere: true, MarkerThere: true), _log.AtTeamDeleting);
        Assert.Equal(team, before.Subject);
        Assert.Equal("person@example.test", before.ActorEmail);
        using var plan = JsonDocument.Parse(before.Detail!);
        Assert.Equal(containers, Strings(plan.RootElement.GetProperty("containers")));
        Assert.Equal(root, plan.RootElement.GetProperty("root").GetString());

        // After: the result, in a later row.
        var after = Assert.Single(await TenantRowsAsync(TenantActions.TeamDeleted));
        Assert.True(after.Seq > before.Seq);
        Assert.Equal(team, after.Subject);
        using var result = JsonDocument.Parse(after.Detail!);
        Assert.Equal(containers.Length, result.RootElement.GetProperty("containers").GetInt32());
        Assert.Empty(result.RootElement.GetProperty("remaining").EnumerateArray());
        Assert.Empty(result.RootElement.GetProperty("failures").EnumerateArray());
    }

    // ---- helpers ----

    private async Task<string> CreateTeamAsync(HttpClient person, string name)
    {
        var agent = _factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var created = await person.PostAsJsonAsync("/api/teams", new { name, agent, memberAgents = new[] { agent } }, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        return (await JsonAsync(created)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private async Task<IReadOnlyList<TenantEvent>> TenantRowsAsync(string action) =>
        [.. (await _log.ReadAsync(null, ITenantLog.MaxTake, Ct)).Events.Where(e => e.Action == action)];

    private bool _personMade;

    private async Task<HttpClient> PersonAsync()
    {
        if (!_personMade)
        {
            await _factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", Password);
            _personMade = true;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = Password }, Ct);
        login.EnsureSuccessStatusCode();
        return client;
    }

    private sealed record Seen(bool TeamRegistered, bool RootThere, bool MarkerThere);

    /// <summary>The Host's own log, which refuses <c>team.deleting</c> while
    /// <see cref="RefuseTeamDeleting"/> is set and otherwise notes what was still there when it
    /// was written.</summary>
    private sealed class WatchingLog : ITenantLog
    {
        public ITenantLog Inner { get; set; } = null!;

        public Func<TeamRegistry> Teams { get; set; } = null!;

        public bool RefuseTeamDeleting { get; set; }

        public Seen? AtTeamDeleting { get; private set; }

        public Task WriteAsync(
            string? actorId, string? actorEmail, string action, string? subject = null,
            string? subjectName = null, string? detail = null, CancellationToken ct = default)
        {
            if (action == TenantActions.TeamDeleting)
            {
                if (RefuseTeamDeleting) throw new IOException("the disk is full");

                var root = JsonDocument.Parse(detail!).RootElement.GetProperty("root").GetString()!;
                AtTeamDeleting = new Seen(
                    Teams().ExistingName(subject!) is not null,
                    Directory.Exists(root),
                    File.Exists(Path.Combine(root, TeamPaths.MarkerFileName)));
            }

            return Inner.WriteAsync(actorId, actorEmail, action, subject, subjectName, detail, ct);
        }

        public Task<TenantEvent?> FindLatestAsync(string action, string subject, CancellationToken ct = default) =>
            Inner.FindLatestAsync(action, subject, ct);

        public Task<IReadOnlyList<TenantEvent>> FindLatestBySubjectAsync(
            IReadOnlyCollection<string> actions, CancellationToken ct = default) =>
            Inner.FindLatestBySubjectAsync(actions, ct);

        public Task<TenantLogPage> ReadAsync(long? before = null, int take = 50, CancellationToken ct = default) =>
            Inner.ReadAsync(before, take, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
