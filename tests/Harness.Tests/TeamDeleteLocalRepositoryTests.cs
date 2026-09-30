using System.Diagnostics;
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
/// A TEAM DELETE CAN TAKE ITS LOCAL REPOSITORY WITH IT, WHEN A PERSON TICKS IT (B0020). Only a
/// <c>local:&lt;name&gt;</c> the team uses; deleted after the team through the delete Admin ->
/// Repositories uses (<see cref="LocalRepoDeletion"/>: its <c>local-repo.deleted</c> row first, the
/// same refusal while another team uses it); one that cannot go leaves the team deleted and is named
/// with its reason. A URL is never the platform's to delete.
///
/// <para>
/// Nothing reaches the network: <c>https://github.com/</c> is rewritten by git itself
/// (<c>url.&lt;base&gt;.insteadOf</c>) to a folder of bare repositories in the test's root.
/// </para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class TeamDeleteLocalRepositoryTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-delete-local-repo-").FullName;
    private readonly string _dataRoot;
    private readonly string _gitHub;
    private readonly EnvironmentScope _environment;
    private readonly RefusingLog _log = new();
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TeamDeleteLocalRepositoryTests()
    {
        _dataRoot = Path.Combine(_root, "data");
        _gitHub = Path.Combine(_root, "github");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_gitHub);
        _environment = new EnvironmentScope(
        [
            new("GIT_CONFIG_COUNT", "1"),
            new("GIT_CONFIG_KEY_0", $"url.file://{_gitHub}/.insteadOf"),
            new("GIT_CONFIG_VALUE_0", "https://github.com/"),
        ]);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());

                // The real log, refusing only the local-repo.deleted row a test names.
                _log.Inner = (ITenantLog)services.Last(d => d.ServiceType == typeof(ITenantLog)).ImplementationInstance!;
                services.Replace(ServiceDescriptor.Singleton<ITenantLog>(_log));
            }));
    }

    private TeamRegistry Teams => _factory.Services.GetRequiredService<TeamRegistry>();

    private string Bare(string name) => Path.Combine(_dataRoot, "repos", name + ".git");

    [Fact]
    public async Task A_ticked_local_repository_no_other_team_uses_is_deleted_after_the_team_with_both_tenant_rows()
    {
        var person = await PersonAsync();
        var team = await CreateTeamAsync(person, "Short Lived");

        var deleted = await person.DeleteAsync($"/api/teams/{team}?deleteLocalRepository=local:{team}", Ct);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var body = await JsonAsync(deleted);
        Assert.Equal([$"local:{team}"], Strings(body.GetProperty("localRepositoriesDeleted")));
        Assert.Empty(Strings(body.GetProperty("localRepositoriesKept")));
        Assert.Empty(body.GetProperty("localRepositoryFailures").EnumerateArray());
        Assert.Null(Teams.ExistingName(team));
        Assert.False(Directory.Exists(Bare(team)));
        Assert.Empty((await person.GetFromJsonAsync<JsonElement>("/api/local-repos", Ct)).EnumerateArray());

        var repoRow = Assert.Single(await TenantRowsAsync(TenantActions.LocalRepoDeleted));
        Assert.Equal(team, repoRow.Subject);
        Assert.Equal("person@example.test", repoRow.ActorEmail);

        using var teamRow = JsonDocument.Parse(Assert.Single(await TenantRowsAsync(TenantActions.TeamDeleted)).Detail!);
        Assert.Equal([$"local:{team}"], Strings(teamRow.RootElement.GetProperty("localRepositoriesDeleted")));
        Assert.Empty(Strings(teamRow.RootElement.GetProperty("localRepositoriesKept")));
    }

    [Fact]
    public async Task An_unticked_local_repository_is_kept_and_named_in_localRepositoriesKept()
    {
        var person = await PersonAsync();
        var team = await CreateTeamAsync(person, "Kept Repo");

        var deleted = await person.DeleteAsync($"/api/teams/{team}", Ct);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var body = await JsonAsync(deleted);
        Assert.Equal([$"local:{team}"], Strings(body.GetProperty("localRepositoriesKept")));
        Assert.Empty(Strings(body.GetProperty("localRepositoriesDeleted")));
        Assert.True(Directory.Exists(Bare(team)));
        Assert.Empty(await TenantRowsAsync(TenantActions.LocalRepoDeleted));

        using var teamRow = JsonDocument.Parse(Assert.Single(await TenantRowsAsync(TenantActions.TeamDeleted)).Detail!);
        Assert.Equal([$"local:{team}"], Strings(teamRow.RootElement.GetProperty("localRepositoriesKept")));
        Assert.Empty(Strings(teamRow.RootElement.GetProperty("localRepositoriesDeleted")));
    }

    [Fact]
    public async Task A_local_repository_another_team_uses_survives_whether_or_not_it_was_asked_for()
    {
        var person = await PersonAsync();
        var team = await CreateTeamAsync(person, "First User");
        var other = await CreateTeamAsync(person, "Second User", repos: [$"local:{team}"]);
        var third = await CreateTeamAsync(person, "Third User", repos: [$"local:{team}"]);

        // Listed as another team's, which is what the dialog reads to offer no checkbox.
        var listed = Assert.Single((await person.GetFromJsonAsync<JsonElement>("/api/local-repos", Ct)).EnumerateArray());
        Assert.Contains(other, Strings(listed.GetProperty("teams")));

        // Asked for anyway: the team goes, the repository is refused as the Repositories dialog refuses it.
        var asked = await person.DeleteAsync($"/api/teams/{team}?deleteLocalRepository=local:{team}", Ct);
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        var body = await JsonAsync(asked);
        Assert.Null(Teams.ExistingName(team));
        Assert.True(Directory.Exists(Bare(team)));
        Assert.Equal([$"local:{team}"], Strings(body.GetProperty("localRepositoriesKept")));
        var failure = Assert.Single(body.GetProperty("localRepositoryFailures").EnumerateArray());
        Assert.Equal($"local:{team}", failure.GetProperty("reference").GetString());
        Assert.Contains($"is used by {other}", failure.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Contains("Admin → Repositories", failure.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Empty(await TenantRowsAsync(TenantActions.LocalRepoDeleted));

        // Not asked for: kept, as always.
        var unasked = await person.DeleteAsync($"/api/teams/{other}", Ct);
        Assert.Equal(HttpStatusCode.OK, unasked.StatusCode);
        Assert.Equal([$"local:{team}"], Strings((await JsonAsync(unasked)).GetProperty("localRepositoriesKept")));
        Assert.True(Directory.Exists(Bare(team)));
        Assert.Equal([third], Teams.TeamsUsingLocalRepo(team));
    }

    [Fact]
    public async Task A_url_repository_is_never_deleted_and_asking_refuses_before_anything_is_deleted()
    {
        MakeGitHubRepository("acme", "app");
        var person = await PersonAsync();
        var team = await CreateTeamAsync(person, "Url Team", repos: ["https://github.com/acme/app.git"]);
        Assert.Equal(["https://github.com/acme/app.git"], Teams.ReposFor(team));

        var asked = await person.DeleteAsync(
            $"/api/teams/{team}?deleteLocalRepository={Uri.EscapeDataString("https://github.com/acme/app.git")}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, asked.StatusCode);
        Assert.Contains("is not a local repository this team uses", (await JsonAsync(asked)).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.NotNull(Teams.ExistingName(team));
        Assert.True(Directory.Exists(Path.Combine(_gitHub, "acme", "app.git")));
        Assert.Empty(await TenantRowsAsync(TenantActions.TeamDeleted));

        // Another team's local repository is not this team's to ask for either.
        var elsewhere = await CreateTeamAsync(person, "Elsewhere");
        var foreign = await person.DeleteAsync($"/api/teams/{team}?deleteLocalRepository=local:{elsewhere}", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.NotNull(Teams.ExistingName(team));
        Assert.True(Directory.Exists(Bare(elsewhere)));

        var deleted = await person.DeleteAsync($"/api/teams/{team}", Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var body = await JsonAsync(deleted);
        Assert.Empty(Strings(body.GetProperty("localRepositoriesKept")));
        Assert.Empty(Strings(body.GetProperty("localRepositoriesDeleted")));
    }

    [Fact]
    public async Task A_repository_delete_that_fails_leaves_the_team_deleted_and_names_the_repository_and_why()
    {
        var person = await PersonAsync();
        var team = await CreateTeamAsync(person, "Stuck Repo");
        _log.RefuseLocalRepoDeleted = team;

        var deleted = await person.DeleteAsync($"/api/teams/{team}?deleteLocalRepository=local:{team}", Ct);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var body = await JsonAsync(deleted);
        Assert.Null(Teams.ExistingName(team));
        Assert.True(Directory.Exists(Bare(team)));
        Assert.Equal([$"local:{team}"], Strings(body.GetProperty("localRepositoriesKept")));
        Assert.Empty(Strings(body.GetProperty("localRepositoriesDeleted")));
        var failure = Assert.Single(body.GetProperty("localRepositoryFailures").EnumerateArray());
        Assert.Equal($"local:{team}", failure.GetProperty("reference").GetString());
        var reason = failure.GetProperty("reason").GetString()!;
        Assert.Contains($"'{team}' was not deleted: its tenant log row could not be written", reason, StringComparison.Ordinal);
        Assert.Contains("Delete it from Admin → Repositories.", reason, StringComparison.Ordinal);

        using var teamRow = JsonDocument.Parse(Assert.Single(await TenantRowsAsync(TenantActions.TeamDeleted)).Detail!);
        var named = Assert.Single(teamRow.RootElement.GetProperty("localRepositoryFailures").EnumerateArray());
        Assert.Equal($"local:{team}", named.GetProperty("reference").GetString());

        // Finished from the Repositories dialog once it can be.
        _log.RefuseLocalRepoDeleted = null;
        Assert.Equal(HttpStatusCode.NoContent, (await person.DeleteAsync($"/api/local-repos/{team}", Ct)).StatusCode);
        Assert.False(Directory.Exists(Bare(team)));
    }

    [Fact]
    public async Task The_local_repository_list_says_what_deleting_one_would_lose()
    {
        var person = await PersonAsync();
        var team = await CreateTeamAsync(person, "Counted");

        var listed = Assert.Single((await person.GetFromJsonAsync<JsonElement>("/api/local-repos", Ct)).EnumerateArray());

        Assert.Equal(["main"], Strings(listed.GetProperty("branches")));
        Assert.Equal(1, listed.GetProperty("commitCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, listed.GetProperty("lastCommit").GetProperty("committedAt").ValueKind);
        Assert.Equal([team], Strings(listed.GetProperty("teams")));
    }

    [Fact]
    public async Task Team_delete_stays_a_persons_act()
    {
        var person = await PersonAsync();
        var team = await CreateTeamAsync(person, "Guarded");
        var user = await _factory.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        var key = await _factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(user!.Id) + Guid.NewGuid().ToString("N"), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: user.Id, ct: Ct);
        var concierge = _factory.CreateClient();
        concierge.DefaultRequestHeaders.Add("X-Api-Key", key);

        var refused = await concierge.DeleteAsync($"/api/teams/{team}?deleteLocalRepository=local:{team}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.NotNull(Teams.ExistingName(team));
        Assert.True(Directory.Exists(Bare(team)));
    }

    // ---- helpers ----

    private async Task<string> CreateTeamAsync(HttpClient person, string name, string[]? repos = null)
    {
        var agent = _factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        object body = repos is null
            ? new { name, agent, memberAgents = new[] { agent } }
            : new { name, agent, memberAgents = new[] { agent }, repos };
        var created = await person.PostAsJsonAsync("/api/teams", body, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        return (await JsonAsync(created)).GetProperty("id").GetString()!;
    }

    private void MakeGitHubRepository(string owner, string name)
    {
        var bare = Path.Combine(_gitHub, owner, name + ".git");
        var work = Path.Combine(_root, "work-" + name);
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(work);
        Git(bare, "init", "--bare", "-b", "main");
        Git(work, "init", "-b", "main");
        Git(work, "-c", "user.name=t", "-c", "user.email=t@example.test", "commit", "--allow-empty", "-m", "first");
        Git(work, "push", bare, "main");
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr}");
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

    /// <summary>The Host's own log, which cannot write the <c>local-repo.deleted</c> row for the
    /// repository named in <see cref="RefuseLocalRepoDeleted"/>.</summary>
    private sealed class RefusingLog : ITenantLog
    {
        public ITenantLog Inner { get; set; } = null!;

        public string? RefuseLocalRepoDeleted { get; set; }

        public Task WriteAsync(
            string? actorId, string? actorEmail, string action, string? subject = null,
            string? subjectName = null, string? detail = null, CancellationToken ct = default) =>
            action == TenantActions.LocalRepoDeleted && subject is not null && subject == RefuseLocalRepoDeleted
                ? throw new IOException("the disk is full")
                : Inner.WriteAsync(actorId, actorEmail, action, subject, subjectName, detail, ct);

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
        _environment.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
