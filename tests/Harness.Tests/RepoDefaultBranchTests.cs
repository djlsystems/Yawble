using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// Each team repository's default branch is stored, read from origin's HEAD on clone and on
/// every Fetch, and is what every Git action means by "main". The origin here names `trunk` as its
/// HEAD and ALSO carries a `main`, so an action that guessed `main` would visibly act on the wrong
/// branch rather than fail by accident.
/// </summary>
public sealed class RepoDefaultBranchTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Repo = "Widget";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-default-branch-").FullName;
    private readonly string _origin;
    private readonly string _seed;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RepoDefaultBranchTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "trunk", _origin);
        Git(_root, "clone", _origin, _seed);
        Git(_seed, "checkout", "-b", "trunk");
        File.WriteAllText(Path.Combine(_seed, "README.md"), "widget\n");
        Commit(_seed, "base");
        Git(_seed, "push", "origin", "trunk");
        Git(_seed, "push", "origin", "trunk:main");

        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IRepoClone>(new LocalOriginClone(_origin));
            }));
    }

    [Fact]
    public async Task A_clone_stores_the_branch_origins_HEAD_names_and_the_status_shows_it()
    {
        var (team, person) = await TeamWithCloneAsync("Alpha");

        var status = await RepoStatusAsync(person, team);
        Assert.Equal("trunk", status.GetProperty("defaultBranch").GetString());
        Assert.Equal("remote", status.GetProperty("defaultBranchSource").GetString());
        Assert.Equal(0, status.GetProperty("mainAhead").GetInt32());
        Assert.Equal(0, status.GetProperty("mainBehind").GetInt32());

        var summary = await TeamAsync(person, team);
        var entry = Assert.Single(summary.GetProperty("defaultBranches").EnumerateArray());
        Assert.Equal(Repo, entry.GetProperty("repo").GetString());
        Assert.Equal("trunk", entry.GetProperty("branch").GetString());
    }

    [Fact]
    public async Task Bring_current_moves_the_stored_branch_and_never_main()
    {
        var (team, person) = await TeamWithCloneAsync("Beta");
        var clone = ClonePath(team);
        var mainBefore = RevParse(clone, "refs/remotes/origin/main");

        File.WriteAllText(Path.Combine(_seed, "next.txt"), "next\n");
        Commit(_seed, "next");
        Git(_seed, "push", "origin", "trunk");
        var trunkTip = RevParse(_seed, "HEAD");

        var response = await ActAsync(person, team, "bring-current");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(trunkTip, RevParse(clone, "refs/heads/trunk"));
        Assert.Equal(mainBefore, RevParse(clone, "refs/remotes/origin/main"));
        Assert.Contains("trunk brought current", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_to_main_pushes_to_the_stored_branch_and_leaves_origin_main_alone()
    {
        var (team, person) = await TeamWithCloneAsync("Gamma");
        var clone = ClonePath(team);
        var originMainBefore = RevParse(_origin, "refs/heads/main");

        Git(clone, "branch", $"team/{team}", "trunk");
        Git(clone, "checkout", $"team/{team}");
        File.WriteAllText(Path.Combine(clone, "work.txt"), "work\n");
        Commit(clone, "work");
        var work = RevParse(clone, "HEAD");
        Git(clone, "checkout", "trunk");

        var response = await ActAsync(person, team, "merge-to-main");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(work, RevParse(_origin, "refs/heads/trunk"));
        Assert.Equal(originMainBefore, RevParse(_origin, "refs/heads/main"));
        Assert.Equal(work, RevParse(clone, "refs/heads/trunk"));
    }

    [Fact]
    public async Task When_origins_HEAD_names_no_branch_it_is_not_known_and_every_action_says_so_and_stops()
    {
        var (team, person) = await TeamWithCloneAsync("Delta");
        var clone = ClonePath(team);

        // A team branch on origin, so delete-remote-branch reaches its "is it on main" question.
        Git(clone, "push", "origin", $"trunk:refs/heads/team/{team}");

        // origin's HEAD now names a branch that does not exist, so `set-head --auto` cannot read one.
        Git(_origin, "symbolic-ref", "HEAD", "refs/heads/nowhere");
        var fetched = await ActAsync(person, team, "fetch");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);

        var status = await RepoStatusAsync(person, team);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("defaultBranch").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("mainAhead").ValueKind);

        var originMainBefore = RevParse(_origin, "refs/heads/main");
        foreach (var action in new[] { "bring-current", "merge-to-main", "rebase", "push", "delete-remote-branch", "ask-team" })
        {
            var response = await ActAsync(person, team, action);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{action}: {response.StatusCode} {text}");
            Assert.Contains("default branch is not known", text, StringComparison.Ordinal);
        }

        Assert.Equal(originMainBefore, RevParse(_origin, "refs/heads/main"));
        Assert.Null(TryRevParse(clone, "refs/heads/main"));
    }

    [Fact]
    public async Task A_failed_fetch_leaves_a_stored_branch_alone()
    {
        var (team, person) = await TeamWithCloneAsync("Epsilon");

        Directory.Move(_origin, _origin + ".gone");
        try
        {
            var response = await ActAsync(person, team, "fetch");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
            Assert.False(body.GetProperty("success").GetBoolean());
        }
        finally
        {
            Directory.Move(_origin + ".gone", _origin);
        }

        Assert.Equal("trunk", (await RepoStatusAsync(person, team)).GetProperty("defaultBranch").GetString());
    }

    [Fact]
    public async Task A_repository_nothing_has_read_is_not_known_and_a_failed_fetch_leaves_it_so()
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync("Zeta", agent, memberAgent: agent, ct: Ct)).Id;
        var person = await PersonAsync();

        Directory.Move(_origin, _origin + ".gone");
        try
        {
            // The clone itself fails: nothing is read, nothing is stored, and main is not assumed.
            await registry.SetReposAsync(team, [$"https://github.com/example/{Repo}.git"], Ct);
            Assert.Null(registry.DefaultBranchFor(team, Repo).Branch);

            var status = await RepoStatusAsync(person, team);
            Assert.Equal(JsonValueKind.Null, status.GetProperty("defaultBranch").ValueKind);
        }
        finally
        {
            Directory.Move(_origin + ".gone", _origin);
        }
    }

    [Fact]
    public async Task A_persons_branch_is_kept_across_fetches_until_cleared_and_then_the_remotes_is_used()
    {
        var (team, person) = await TeamWithCloneAsync("Eta");

        var set = await person.PutAsJsonAsync($"/api/teams/{team}/repos/{Repo}/default-branch", new { branch = "main" }, Ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        var status = await RepoStatusAsync(person, team);
        Assert.Equal("main", status.GetProperty("defaultBranch").GetString());
        Assert.Equal("person", status.GetProperty("defaultBranchSource").GetString());

        // A remote-only value IS replaced by the next successful Fetch; the person's is not touched.
        Git(_origin, "symbolic-ref", "HEAD", "refs/heads/main");
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        Git(_origin, "symbolic-ref", "HEAD", "refs/heads/trunk");
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        var stored = _factory.Services.GetRequiredService<TeamRegistry>().DefaultBranchFor(team, Repo);
        Assert.Equal("main", stored.SetByPerson);
        Assert.Equal("trunk", stored.FromRemote);

        var cleared = await person.PutAsJsonAsync($"/api/teams/{team}/repos/{Repo}/default-branch", new { branch = (string?)null }, Ct);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        status = await RepoStatusAsync(person, team);
        Assert.Equal("trunk", status.GetProperty("defaultBranch").GetString());
        Assert.Equal("remote", status.GetProperty("defaultBranchSource").GetString());

        // Stored, not cached: a fresh store reads what the rows hold.
        var rows = await _factory.Services.GetRequiredService<ITeamStore>().RepoDefaultBranchesAsync(Ct);
        var row = Assert.Single(rows, r => string.Equals(r.Team, team, StringComparison.OrdinalIgnoreCase));
        Assert.Null(row.SetByPerson);
        Assert.Equal("trunk", row.FromRemote);
    }

    [Fact]
    public async Task Setting_a_branch_refuses_a_name_git_would_not_take_and_an_unknown_repository()
    {
        var (team, person) = await TeamWithCloneAsync("Theta");

        var bad = await person.PutAsJsonAsync($"/api/teams/{team}/repos/{Repo}/default-branch", new { branch = "--upload-pack=x" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var missing = await person.PutAsJsonAsync($"/api/teams/{team}/repos/Nope/default-branch", new { branch = "trunk" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        Assert.Null(_factory.Services.GetRequiredService<TeamRegistry>().DefaultBranchFor(team, Repo).SetByPerson);
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("release/2026.09", true)]
    [InlineData("-x", false)]
    [InlineData("a..b", false)]
    [InlineData("a b", false)]
    [InlineData("HEAD", false)]
    [InlineData("x.lock", false)]
    [InlineData("a:b", false)]
    [InlineData("", false)]
    public void Branch_names_are_what_git_would_take(string name, bool valid) =>
        Assert.Equal(valid, BranchNames.IsValid(name));

    private async Task<(string Team, HttpClient Person)> TeamWithCloneAsync(string name)
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent, ct: Ct)).Id;
        await registry.SetReposAsync(team, [$"https://github.com/example/{Repo}.git"], Ct);

        var clone = ClonePath(team);
        Assert.True(Directory.Exists(Path.Combine(clone, ".git")), "the platform did not clone");
        Git(clone, "config", "user.name", "t");
        Git(clone, "config", "user.email", "t@example.invalid");
        Assert.Equal("trunk", registry.DefaultBranchFor(team, Repo).Branch);

        return (team, await PersonAsync());
    }

    private string ClonePath(string team) =>
        Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), Repo, "main");

    private static async Task<JsonElement> RepoStatusAsync(HttpClient person, string team)
    {
        var body = await person.GetFromJsonAsync<JsonElement>($"/api/teams/{team}/repo-status", Ct);
        return Assert.Single(body.GetProperty("repos").EnumerateArray());
    }

    private static async Task<JsonElement> TeamAsync(HttpClient person, string team)
    {
        var body = await person.GetFromJsonAsync<JsonElement>("/api/overview", Ct);
        return body.GetProperty("teams").EnumerateArray()
            .Single(t => string.Equals(t.GetProperty("id").GetString(), team, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An action as a person presses it: again while the answer is 409 "still working", which the
    /// Manager's wake on attaching the repository can cause. Any other 409 is the answer.
    /// </summary>
    private static async Task<HttpResponseMessage> ActAsync(HttpClient person, string team, string action)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await person.PostAsync($"/api/teams/{team}/repos/{Repo}/{action}", null, Ct);
            if (response.StatusCode != HttpStatusCode.Conflict || DateTime.UtcNow > deadline) return response;

            var text = await response.Content.ReadAsStringAsync(Ct);
            if (!text.Contains("still working", StringComparison.Ordinal)) return response;
            await Task.Delay(100, Ct);
        }
    }

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

    private static void Commit(string repo, string message)
    {
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", message);
    }

    private static string RevParse(string repo, string reference) =>
        TryRevParse(repo, reference) ?? throw new InvalidOperationException($"{reference} does not resolve in {repo}");

    private static string? TryRevParse(string repo, string reference)
    {
        var (exit, stdout) = Run(repo, "rev-parse", "--verify", "--quiet", reference);
        return exit == 0 ? stdout.Trim() : null;
    }

    private static void Git(string workingDirectory, params string[] args) => Run(workingDirectory, args);

    private static (int Exit, string Stdout) Run(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout);
    }

    /// <summary>The product's own <see cref="RepoClone"/>, pointed at the local origin whatever URL the
    /// team names - so the clone-time read of origin's HEAD is the real one.</summary>
    private sealed class LocalOriginClone(string origin) : IRepoClone
    {
        private readonly RepoClone _real = new(new GitRunner());

        public async Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(
            IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct)
        {
            var outcomes = new List<RepoCloneOutcome>();
            foreach (var (url, path) in repos)
            {
                outcomes.Add((await _real.EnsureAsync(origin, path, ct)) with { Url = url });
            }

            return outcomes;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
