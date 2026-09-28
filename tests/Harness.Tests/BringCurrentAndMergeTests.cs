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
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harness.Tests;

/// <summary>
/// Bring current and merge, and a team branch that exists only in the clone, on a real clone of a
/// local origin. The origin's HEAD is `trunk` and it also carries a `main`, so anything that
/// guessed `main` would act on the wrong branch visibly.
/// </summary>
public sealed class BringCurrentAndMergeTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Repo = "Widget";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-bring-merge-").FullName;
    private readonly string _origin;
    private readonly string _seed;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BringCurrentAndMergeTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "trunk", _origin);
        Git(_root, "clone", _origin, _seed);
        Git(_seed, "checkout", "-b", "trunk");
        File.WriteAllText(Path.Combine(_seed, "README.md"), "widget\n");
        File.WriteAllText(Path.Combine(_seed, "shared.txt"), "one\n");
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

                // The Git dialog is what is under test, so nothing else pushes: the platform's own
                // publisher, run when a member's run ends, would publish a branch these tests keep
                // unpushed on purpose whenever the Manager's wake happened to end after it was made.
                services.Replace(ServiceDescriptor.Singleton<ITeamPublisher>(new NoPublisher()));
            }));
    }

    [Fact]
    public async Task A_pushed_team_branch_behind_a_moved_default_branch_is_brought_current_pushed_and_merged()
    {
        var (team, person) = await TeamWithCloneAsync("Alpha");
        var clone = ClonePath(team);
        var work = PushedTeamWork(clone, team, "work.txt", "work\n");
        var decoy = RevParse(_origin, "refs/heads/main");

        // Another team landed on trunk meanwhile, in a different file.
        File.WriteAllText(Path.Combine(_seed, "other.txt"), "other\n");
        Commit(_seed, "other team");
        Git(_seed, "push", "origin", "trunk");
        var other = RevParse(_seed, "HEAD");

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        var before = await RepoStatusAsync(person, team);
        Assert.Equal(1, before.GetProperty("teamBranchBehindDefault").GetInt32());
        Assert.Empty(before.GetProperty("filesChangedOnBothSides").EnumerateArray());

        var response = await ActAsync(person, team, "bring-current-and-merge");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var trunk = RevParse(_origin, "refs/heads/trunk");
        Assert.True(IsAncestor(_origin, work, trunk), "trunk lacks the team's work");
        Assert.True(IsAncestor(_origin, other, trunk), "trunk lost the other team's work");

        // The team branch on origin carries the merge and still has the team's own commit: not rebased.
        var teamOnOrigin = RevParse(_origin, $"refs/heads/team/{team}");
        Assert.True(IsAncestor(_origin, work, teamOnOrigin));
        Assert.True(IsAncestor(_origin, other, teamOnOrigin));
        Assert.Equal(work, RevParse(_origin, $"refs/heads/team/{team}^1"));
        Assert.Equal(teamOnOrigin, RevParse(clone, $"refs/heads/team/{team}"));

        Assert.Equal(decoy, RevParse(_origin, "refs/heads/main"));
        Assert.Contains($"merged into team/{team}", text, StringComparison.Ordinal);

        var after = JsonDocument.Parse(text).RootElement.GetProperty("status");
        Assert.True(after.GetProperty("teamMergedToMain").GetBoolean());
        Assert.Equal(0, after.GetProperty("teamBranchBehindDefault").GetInt32());
    }

    [Fact]
    public async Task A_conflict_pushes_nothing_leaves_the_clone_as_it_was_and_names_the_files()
    {
        var (team, person) = await TeamWithCloneAsync("Beta");
        var clone = ClonePath(team);
        Git(clone, "checkout", "-b", $"team/{team}", "trunk");
        File.WriteAllText(Path.Combine(clone, "shared.txt"), "team\n");
        Commit(clone, "team edits shared");
        Git(clone, "push", "origin", $"team/{team}");

        File.WriteAllText(Path.Combine(_seed, "shared.txt"), "other\n");
        Commit(_seed, "other edits shared");
        Git(_seed, "push", "origin", "trunk");

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        var status = await RepoStatusAsync(person, team);
        Assert.Equal(["shared.txt"], status.GetProperty("filesChangedOnBothSides").EnumerateArray().Select(f => f.GetString()));

        var originTeam = RevParse(_origin, $"refs/heads/team/{team}");
        var originTrunk = RevParse(_origin, "refs/heads/trunk");
        var refsBefore = Run(clone, "for-each-ref").Stdout;
        var headBefore = Run(clone, "symbolic-ref", "HEAD").Stdout;
        var fileBefore = File.ReadAllText(Path.Combine(clone, "shared.txt"));

        var response = await ActAsync(person, team, "bring-current-and-merge");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, text);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["shared.txt"], body.GetProperty("conflicts").EnumerateArray().Select(f => f.GetString()));
        Assert.Contains("nothing was pushed", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains($"must resolve it on team/{team}: shared.txt", body.GetProperty("detail").GetString(), StringComparison.Ordinal);

        Assert.Equal(originTeam, RevParse(_origin, $"refs/heads/team/{team}"));
        Assert.Equal(originTrunk, RevParse(_origin, "refs/heads/trunk"));
        Assert.Equal(refsBefore, Run(clone, "for-each-ref").Stdout);
        Assert.Equal(headBefore, Run(clone, "symbolic-ref", "HEAD").Stdout);
        Assert.Equal(fileBefore, File.ReadAllText(Path.Combine(clone, "shared.txt")));
        Assert.Equal(string.Empty, Run(clone, "status", "--porcelain").Stdout);
        Assert.False(File.Exists(Path.Combine(clone, ".git", "MERGE_HEAD")));
    }

    [Fact]
    public async Task A_default_branch_that_is_not_known_refuses_and_changes_nothing()
    {
        var (team, person) = await TeamWithCloneAsync("Gamma");
        var clone = ClonePath(team);
        PushedTeamWork(clone, team, "work.txt", "work\n");

        Git(_origin, "symbolic-ref", "HEAD", "refs/heads/nowhere");
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        var originTeam = RevParse(_origin, $"refs/heads/team/{team}");
        var originTrunk = RevParse(_origin, "refs/heads/trunk");

        var response = await ActAsync(person, team, "bring-current-and-merge");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, text);
        Assert.Contains("default branch is not known", text, StringComparison.Ordinal);
        Assert.Equal(originTeam, RevParse(_origin, $"refs/heads/team/{team}"));
        Assert.Equal(originTrunk, RevParse(_origin, "refs/heads/trunk"));
    }

    [Fact]
    public async Task Contributor_mode_refuses_and_pushes_nothing()
    {
        var (team, person) = await TeamWithCloneAsync("Delta");
        var clone = ClonePath(team);
        PushedTeamWork(clone, team, "work.txt", "work\n");
        File.WriteAllText(Path.Combine(_seed, "other.txt"), "other\n");
        Commit(_seed, "other team");
        Git(_seed, "push", "origin", "trunk");

        await _factory.Services.GetRequiredService<TeamRegistry>().SetContributorAsync(
            team, Repo, "https://github.com/upstream/Widget.git", "example", false, null, Ct);
        var originTeam = RevParse(_origin, $"refs/heads/team/{team}");
        var originTrunk = RevParse(_origin, "refs/heads/trunk");

        var response = await ActAsync(person, team, "bring-current-and-merge");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, text);
        Assert.Contains("Open pull request", text, StringComparison.Ordinal);
        Assert.Equal(originTeam, RevParse(_origin, $"refs/heads/team/{team}"));
        Assert.Equal(originTrunk, RevParse(_origin, "refs/heads/trunk"));
    }

    [Fact]
    public async Task A_team_branch_only_in_the_clone_is_reported_not_pushed_and_Push_publishes_it()
    {
        var (team, person) = await TeamWithCloneAsync("Epsilon");
        var clone = ClonePath(team);

        Git(clone, "checkout", "-b", $"team/{team}", "trunk");
        File.WriteAllText(Path.Combine(clone, "work.txt"), "work\n");
        Commit(clone, "work");
        var work = RevParse(clone, "HEAD");

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        var before = await RepoStatusAsync(person, team);
        Assert.True(before.GetProperty("teamBranchUnpushed").GetBoolean());
        Assert.False(before.GetProperty("teamPushed").GetBoolean());

        var response = await ActAsync(person, team, "push");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.Equal(work, RevParse(_origin, $"refs/heads/team/{team}"));
        var after = JsonDocument.Parse(text).RootElement.GetProperty("status");
        Assert.False(after.GetProperty("teamBranchUnpushed").GetBoolean());
        Assert.True(after.GetProperty("teamPushed").GetBoolean());

        // Origin behind the local branch is the same state, and Push fast-forwards it.
        File.WriteAllText(Path.Combine(clone, "more.txt"), "more\n");
        Commit(clone, "more");
        Assert.True((await RepoStatusAsync(person, team)).GetProperty("teamBranchUnpushed").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "push")).StatusCode);
        Assert.Equal(RevParse(clone, "HEAD"), RevParse(_origin, $"refs/heads/team/{team}"));
    }

    /// <summary>The Manager's pattern: team/{id} cut from the default branch, work on it, pushed.</summary>
    private static string PushedTeamWork(string clone, string team, string file, string content)
    {
        Git(clone, "checkout", "-b", $"team/{team}", "trunk");
        File.WriteAllText(Path.Combine(clone, file), content);
        Commit(clone, "work");
        Git(clone, "push", "origin", $"team/{team}");
        return RevParse(clone, "HEAD");
    }

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

    /// <summary>An action as a person presses it, again while the answer is 409 "still working".</summary>
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

    private static bool IsAncestor(string repo, string ancestor, string descendant) =>
        Run(repo, "merge-base", "--is-ancestor", ancestor, descendant).Exit == 0;

    private static string RevParse(string repo, string reference)
    {
        var (exit, stdout) = Run(repo, "rev-parse", "--verify", "--quiet", reference);
        return exit == 0 ? stdout.Trim() : throw new InvalidOperationException($"{reference} does not resolve in {repo}");
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var (exit, _) = Run(workingDirectory, args);
        if (exit != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {workingDirectory}");
    }

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

    private sealed class NoPublisher : ITeamPublisher
    {
        public Task<TeamPublishReport> PublishAsync(
            string team, IReadOnlyList<string> repoUrls, ContainerId source, long? causation, CancellationToken ct) =>
            Task.FromResult(TeamPublishReport.Nothing);
    }

    /// <summary>The product's own <see cref="RepoClone"/>, pointed at the local origin.</summary>
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
