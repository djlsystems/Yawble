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
/// A dispatch records its team branch's tip when the publish pushes it, and <c>landed</c>,
/// once proven, is stored on the dispatch and kept after the branch, the clone and the team are
/// gone. A team that is gone is read from its recorded tip in another team's clone; with no clone
/// anywhere it is <c>unknown</c>, saying so. Merge to main stores landed at once. Every origin is a
/// local bare repository; the URLs only name it.
/// </summary>
public sealed class LandedSurvivesCleanupTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Repo = "Widget";
    private const string Url = "https://github.com/owner/Widget.git";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-landed-kept-").FullName;
    private readonly string _origin;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LandedSurvivesCleanupTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "trunk", _origin);
        Git(_root, "clone", _origin, seed);
        Git(seed, "checkout", "-b", "trunk");
        File.WriteAllText(Path.Combine(seed, "README.md"), "widget\n");
        Commit(seed, "base");
        Git(seed, "push", "origin", "trunk");

        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting(InstanceGitIdentity.NameVariable, "Pat Person")
            .UseSetting(InstanceGitIdentity.EmailVariable, "pat@example.test")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IRepoClone>(new LocalOriginClone(_origin));
            }));
    }

    private IBacklogStore Backlog => _factory.Services.GetRequiredService<IBacklogStore>();

    [Fact]
    public async Task Landed_then_branch_and_team_deleted_still_reads_landed_with_landed_at()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Alpha");
        var item = await DispatchAsync(team);

        // The work reaches origin as team/{id} and is merged to trunk outside the platform.
        var sha = PushWork(team, "the work", alsoTo: "trunk");
        Git(Clone(team), "fetch", "origin");

        var first = await LandedOverHttpAsync(person, item);
        Assert.Equal(BacklogLandedStates.Landed, first.GetProperty("state").GetString());

        var stored = await CurrentDispatchAsync(item);
        Assert.NotNull(stored.LandedAt);
        Assert.Equal(sha, stored.LandedSha);
        Assert.Equal("trunk", stored.LandedBranch);

        // NEVER DOWNGRADED OR MOVED: a second proof stores nothing.
        Assert.False(await Backlog.RecordLandedAsync(stored.Id, "0000000", "other", Ct));

        // Tidied: the branch goes on origin, then the team with its clone.
        var clone = Clone(team);
        Git(_origin, "branch", "-D", $"team/{team}");
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{team}", Ct)).StatusCode);
        Assert.False(Directory.Exists(clone));

        _factory.Services.GetRequiredService<BacklogLandedCache>().Clear();
        var after = await LandedOverHttpAsync(person, item);

        Assert.Equal(BacklogLandedStates.Landed, after.GetProperty("state").GetString());
        Assert.Equal(DateTimeOffset.Parse(stored.LandedAt!, System.Globalization.CultureInfo.InvariantCulture),
            after.GetProperty("landedAt").GetDateTimeOffset());
        Assert.Contains(sha[..7], after.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var kept = await CurrentDispatchAsync(item);
        Assert.Equal(stored.LandedAt, kept.LandedAt);
        Assert.Equal(sha, kept.LandedSha);
    }

    [Fact]
    public async Task The_publish_records_the_tip_and_a_gone_teams_tip_reachable_in_another_teams_clone_reads_landed()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Beta");
        var item = await DispatchAsync(team);

        // A member's commit on the clone's team branch; the platform's publish pushes it.
        var clone = Clone(team);
        Git(clone, "checkout", "-b", $"team/{team}");
        File.WriteAllText(Path.Combine(clone, "beta.txt"), "beta\n");
        Commit(clone, "beta work");
        var sha = Run(clone, "rev-parse", "HEAD").Stdout.Trim();

        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        await _factory.Services.GetRequiredService<ITeamPublisher>().PublishAsync(
            team, registry.ReposFor(team), new ContainerId(team, "Manager"), null, Ct);

        var tip = Assert.Single(await Backlog.TipsAsync((await CurrentDispatchAsync(item)).Id, Ct));
        Assert.Equal(Repo, tip.Repo);
        Assert.Equal(sha, tip.Sha);

        // Another team's clone exists BEFORE the merge, so only a fetch can show it.
        var other = await TeamAsync("Gamma");

        // Merged outside the platform, then the branch and the team are tidied away unread.
        Git(clone, "checkout", "trunk");
        Git(clone, "branch", "-D", $"team/{team}");
        Git(_origin, "update-ref", "refs/heads/trunk", sha);
        Git(_origin, "branch", "-D", $"team/{team}");
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{team}", Ct)).StatusCode);

        var landed = await LandedOverHttpAsync(person, item);

        Assert.Equal(BacklogLandedStates.Landed, landed.GetProperty("state").GetString());
        Assert.Contains(registry.LabelFor(other), landed.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(sha, (await CurrentDispatchAsync(item)).LandedSha);
    }

    [Fact]
    public async Task A_gone_teams_recorded_tip_with_no_clone_anywhere_reads_unknown_and_says_why()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Delta");
        var item = await DispatchAsync(team);

        var sha = PushWork(team, "delta work", alsoTo: "trunk");
        await Backlog.RecordTipAsync((await CurrentDispatchAsync(item)).Id, Repo, sha, Ct);
        Git(_origin, "branch", "-D", $"team/{team}");
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{team}", Ct)).StatusCode);

        var landed = await LandedOverHttpAsync(person, item);

        Assert.Equal(BacklogLandedStates.Unknown, landed.GetProperty("state").GetString());
        Assert.Contains(
            $"no clone of {Repo} on this instance to check the recorded tip against",
            landed.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Null((await CurrentDispatchAsync(item)).LandedAt);
    }

    [Fact]
    public async Task Merge_to_main_stores_landed_at_once()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Epsilon");
        var item = await DispatchAsync(team);

        var sha = PushWork(team, "epsilon work");
        Assert.Null((await CurrentDispatchAsync(item)).LandedAt);

        var merged = await person.PostAsync($"/api/teams/{team}/repos/{Repo}/merge-to-main", null, Ct);
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        // Straight from the store - no backlog read has derived anything.
        var stored = await CurrentDispatchAsync(item);
        Assert.NotNull(stored.LandedAt);
        Assert.Equal(sha, stored.LandedSha);
        Assert.Equal("trunk", stored.LandedBranch);
        Assert.Equal(sha, Assert.Single(await Backlog.TipsAsync(stored.Id, Ct)).Sha);
    }

    private async Task<JsonElement> LandedOverHttpAsync(HttpClient person, long item)
    {
        var body = await person.GetFromJsonAsync<JsonElement>($"/api/backlog/{item}", Ct);
        return body.GetProperty("item").GetProperty("landed");
    }

    private async Task<BacklogDispatch> CurrentDispatchAsync(long item) =>
        (await Backlog.DispatchesAsync(item, Ct))[^1];

    private async Task<long> DispatchAsync(string team)
    {
        var item = await Backlog.CreateAsync(null, $"Item for {team}", "body", "person@example.test", Ct);

        // ABOVE THE TEAM'S FLOOR, as a real dispatch's correlation is: the row it wrote.
        var row = await _factory.Services.GetRequiredService<IMessageLog>().AppendAsync(
            new NewMessage("test.dispatched", "{}", "test", null), Ct);
        await Backlog.AddDispatchAsync(item.Id, team, team, row.Seq, "person@example.test", Ct);
        return item.Id;
    }

    /// <summary>Commits on top of trunk from a scratch clone and pushes them as team/{id} (and to
    /// <paramref name="alsoTo"/> when given), answering the sha.</summary>
    private string PushWork(string team, string message, string? alsoTo = null)
    {
        var work = Path.Combine(_root, $"work-{team}");
        Git(_root, "clone", "-b", "trunk", _origin, work);
        File.WriteAllText(Path.Combine(work, $"{team}.txt"), $"{message}\n");
        Commit(work, message);
        var sha = Run(work, "rev-parse", "HEAD").Stdout.Trim();

        Git(work, "push", "origin", $"HEAD:refs/heads/team/{team}");
        if (alsoTo is not null) Git(work, "push", "origin", $"HEAD:refs/heads/{alsoTo}");
        return sha;
    }

    private string Clone(string team) =>
        Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), Repo, "main");

    private async Task<string> TeamAsync(string name)
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent, ct: Ct)).Id;
        await registry.SetReposAsync(team, [Url], Ct);

        Assert.True(Directory.Exists(Path.Combine(Clone(team), ".git")), "the platform did not clone");
        Assert.Equal("trunk", registry.DefaultBranchFor(team, Repo).Branch);
        return team;
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
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "core.hooksPath=/dev/null", "commit", "-m", message);
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
