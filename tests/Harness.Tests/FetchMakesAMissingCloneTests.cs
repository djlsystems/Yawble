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
/// A repository attached while its token is revoked has no clone once the token is fixed. The Git
/// dialog, with no clone to measure, offers Fetch, and Bring current is never offered in that
/// state - so Fetch must make a missing clone first, as Bring current does, rather than answer
/// 404 "Repository not cloned yet".
/// </summary>
public sealed class FetchMakesAMissingCloneTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Repo = "Widget";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-fetch-clone-").FullName;
    private readonly LateClone _cloner;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FetchMakesAMissingCloneTests()
    {
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "main", origin);
        Git(_root, "clone", origin, seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "widget\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "base");
        Git(seed, "push", "origin", "main");

        _cloner = new LateClone(origin);
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IRepoClone>(_cloner);
            }));
    }

    [Fact]
    public async Task Fetch_makes_a_clone_that_failed_when_the_repository_was_attached()
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync("Alpha", agent, memberAgent: agent, ct: Ct)).Id;

        // Attached while the credential was bad: the platform's clone fails, and nothing is there.
        await registry.SetReposAsync(team, [$"https://github.com/example/{Repo}.git"], Ct);
        var clonePath = Path.Combine(services.GetRequiredService<TeamPaths>().ReposFor(team), Repo, "main");
        Assert.False(Directory.Exists(clonePath));

        // The credential is fixed; the person opens the Git dialog and presses what it offers.
        _cloner.Reachable = true;
        var person = await PersonAsync();
        var response = await FetchAsync(person, team);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(clonePath, ".git")), "Fetch did not make the missing clone");
        Assert.Equal("widget\n", File.ReadAllText(Path.Combine(clonePath, "README.md")).Replace("\r\n", "\n"));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.True(body.GetProperty("success").GetBoolean(), body.ToString());
    }

    /// <summary>
    /// The failure notice roots its own workflow and the Manager reports the block there. When
    /// Fetch later makes the clone, the clone answers that notice, so the team does not read
    /// BLOCKED after its real work has finished.
    /// </summary>
    [Fact]
    public async Task A_clone_made_later_tells_the_manager_in_the_workflow_the_failure_opened()
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var messages = services.GetRequiredService<IMessageLog>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync("Gamma", agent, memberAgent: agent, ct: Ct)).Id;
        var manager = MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName));

        await registry.SetReposAsync(team, [$"https://github.com/example/{Repo}.git"], Ct);
        var notice = Assert.Single(await messages.ReadAfterAsync(0, [manager], 100, Ct),
            m => m.Payload.Contains("COULD NOT CLONE", StringComparison.Ordinal));

        _cloner.Reachable = true;
        var person = await PersonAsync();
        var response = await FetchAsync(person, team);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Other platform notices may also answer the workflow (the quiet-workflow offer); this one is
        // the answer to the failure, inside its workflow.
        var answer = Assert.Single(await messages.ReadAfterAsync(notice.Seq, [manager], 100, Ct),
            m => m.Payload.Contains("ready now", StringComparison.Ordinal));
        Assert.Equal(notice.Seq, answer.CausationSeq);
        Assert.Equal(notice.CorrelationId, answer.CorrelationId);
        var text = JsonDocument.Parse(answer.Payload).RootElement.GetProperty("instruction").GetString()!;
        Assert.Contains("workflow_complete", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clone_that_still_fails_is_said_in_gits_words_and_nothing_is_left()
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync("Beta", agent, memberAgent: agent, ct: Ct)).Id;
        await registry.SetReposAsync(team, [$"https://github.com/example/{Repo}.git"], Ct);

        var person = await PersonAsync();
        var response = await FetchAsync(person, team);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("could not be cloned", text, StringComparison.Ordinal);
        Assert.Contains(LateClone.Refusal, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fetch as a person presses it: again, while the answer is 409 "still working". Attaching the
    /// repository wakes the Manager with the failure notice (and the quiet-workflow offer can wake
    /// it again), and the route refuses while any member runs. Under a loaded suite that run
    /// overlapped the first press and the test failed on a 409 that said nothing about the fix.
    /// </summary>
    private static async Task<HttpResponseMessage> FetchAsync(HttpClient person, string team)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await person.PostAsync($"/api/teams/{team}/repos/{Repo}/fetch", null, Ct);
            if (response.StatusCode != HttpStatusCode.Conflict || DateTime.UtcNow > deadline) return response;
            await Task.Delay(100, Ct);
        }
    }

    private async Task<HttpClient> PersonAsync()
    {
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", Password);
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = Password }, Ct);
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static void Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    /// <summary>A cloner that refuses the way GitHub refuses a revoked token until it is told the
    /// credential is fixed, then clones the local origin into the path it is given.</summary>
    private sealed class LateClone(string origin) : IRepoClone
    {
        public const string Refusal = "fatal: Authentication failed for 'https://github.com/example/Widget.git/'";

        public volatile bool Reachable;

        public Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct)
        {
            var outcomes = new List<RepoCloneOutcome>();
            foreach (var (url, path) in repos)
            {
                if (!Reachable)
                {
                    outcomes.Add(new RepoCloneOutcome(url, path, RepoCloneResult.Failed, Refusal));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Git(Path.GetDirectoryName(path)!, "clone", origin, path);
                outcomes.Add(new RepoCloneOutcome(url, path, RepoCloneResult.Cloned));
            }

            return Task.FromResult<IReadOnlyList<RepoCloneOutcome>>(outcomes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
