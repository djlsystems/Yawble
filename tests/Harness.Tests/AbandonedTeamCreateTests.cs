using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harness.Tests;

/// <summary>
/// A TEAM CREATE WHOSE CALLER GOES AWAY STILL FINISHES. A large clone can outlast the browser that
/// asked for it; the request is aborted, and the create, the clone and the dispatch must run to
/// completion on the Host's lifetime rather than stop at the abort and leave a team with an empty
/// clone and no dispatch. The same for <c>POST /api/teams</c>, the <c>team_create</c> tool and backlog
/// dispatch-to-new, which share one path.
///
/// <para>
/// And A CLONE THAT CAME OUT EMPTY IS NOT A WORKING CLONE: the repository card reads it not ready,
/// and Fetch - like the platform's own clone - makes it rather than calling it already there.
/// </para>
///
/// <para>
/// Nothing reaches the network and no agent runs. The abort lands mid-clone through
/// <see cref="BlockingClone"/>, a seam over the real clone of the team's local repository: it makes
/// what <c>git clone</c> makes before any object arrives, then waits on a gate with the token the Host
/// handed it. <see cref="AbortWitness"/> proves the abort reached the Host before the gate opens.
/// </para>
/// </summary>
public sealed class AbandonedTeamCreateTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly string _root = Directory.CreateTempSubdirectory("harness-abandoned-create-").FullName;
    private readonly string _dataRoot;
    private readonly AbortWitness _witness = new();
    private BlockingClone? _clone;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AbandonedTeamCreateTests()
    {
        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IStartupFilter>(_witness);
                services.Replace(ServiceDescriptor.Singleton<IRepoClone>(sp => _clone = new BlockingClone(
                    new RepoClone(sp.GetRequiredService<GitRunner>(), localRepos: sp.GetRequiredService<LocalRepos>()))));
            }));
    }

    private TeamRegistry Teams => _factory.Services.GetRequiredService<TeamRegistry>();

    private BlockingClone Clone => _clone ??= (BlockingClone)_factory.Services.GetRequiredService<IRepoClone>();

    // ---- the request is aborted mid-clone ----

    [Fact]
    public async Task A_create_aborted_mid_clone_finishes_with_a_cloned_team()
    {
        var person = await PersonAsync();
        Clone.Arm();

        await AbandonMidCloneAsync("/api/teams", token =>
            person.PostAsJsonAsync("/api/teams", new { name = "Abandoned Create", agent = Agent(), memberAgents = new[] { Agent() } }, token));

        var team = Teams.All().Single(t => t.Name == "Abandoned Create").Id;
        await EventuallyAsync(async () => HasCommit(ClonePath(team, team))
            && (await TenantRowsAsync(TenantActions.TeamCreated)).Any(r => r.Subject == team));

        Assert.True(HasCommit(ClonePath(team, team)),
            "the create stopped when its request was aborted: the team's clone is empty");
        Assert.Contains(await TenantRowsAsync(TenantActions.TeamCreated), r => r.Subject == team);
        Assert.Equal([$"local:{team}"], Teams.ReposFor(team));
    }

    [Fact]
    public async Task A_dispatch_to_new_aborted_mid_clone_finishes_with_a_cloned_team_and_the_item_dispatched()
    {
        var person = await PersonAsync();
        var item = await ReadyItemAsync(person);
        Clone.Arm();

        var route = $"/api/backlog/{item}/dispatch-to-new";
        await AbandonMidCloneAsync(route, token =>
            person.PostAsJsonAsync(route, new { name = "AbandonedDispatch", agent = Agent(), memberAgents = new[] { Agent() } }, token));

        const string team = "AbandonedDispatch";
        Assert.Contains(Teams.All(), t => t.Id == team);
        await EventuallyAsync(async () => HasCommit(ClonePath(team, team))
            && (await DispatchRowsAsync(item)).Count > 0);

        Assert.True(HasCommit(ClonePath(team, team)),
            "the create stopped when its request was aborted: the team's clone is empty");
        var dispatched = Assert.Single(await DispatchRowsAsync(item));
        Assert.Contains($"\"team\":\"{team}\"", dispatched.Detail, StringComparison.Ordinal);

        var manager = MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName));
        Assert.Contains(
            await _factory.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [manager], 100, Ct),
            m => m.Payload.Contains("has been dispatched to your team", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_team_create_tool_call_aborted_mid_clone_finishes_with_a_cloned_team()
    {
        using var person = await PersonAsync();
        var tools = ConciergeTools(await ConciergeKeyAsync());
        Clone.Arm();

        await AbandonMidCloneAsync("/api/teams", token =>
            tools.TeamCreate("Abandoned Tool", agent: Agent(), cancellationToken: token));

        var team = Teams.All().Single(t => t.Name == "Abandoned Tool").Id;
        await EventuallyAsync(async () => HasCommit(ClonePath(team, team))
            && (await TenantRowsAsync(TenantActions.TeamCreated)).Any(r => r.Subject == team));

        Assert.True(HasCommit(ClonePath(team, team)),
            "the create stopped when the tool call was abandoned: the team's clone is empty");
        Assert.Contains(await TenantRowsAsync(TenantActions.TeamCreated), r => r.Subject == team);
    }

    // ---- an empty clone is not a working clone ----

    [Fact]
    public async Task An_empty_clone_reads_not_ready_on_its_repository_card()
    {
        var person = await PersonAsync();
        var team = await CreatedTeamAsync(person, "Hollow");
        Assert.True(CloneReady(await RepoStatusAsync(person, team)), "a cloned repository must read ready");

        Hollow(team);

        Assert.False(CloneReady(await RepoStatusAsync(person, team)),
            "an empty clone read as ready on its repository card");
    }

    [Fact]
    public async Task Fetch_makes_an_empty_clone_into_a_working_clone()
    {
        var person = await PersonAsync();
        var team = await CreatedTeamAsync(person, "Refetched");
        Hollow(team);

        var response = await FetchAsync(person, team, team);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(HasCommit(ClonePath(team, team)), "Fetch left the empty clone empty");
        Assert.True(CloneReady(await RepoStatusAsync(person, team)), "the clone Fetch made does not read ready");
    }

    [Fact]
    public async Task The_platforms_clone_replaces_an_empty_clone_rather_than_calling_it_already_there()
    {
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "main", origin);
        Git(_root, "clone", origin, seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "widget\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "base");
        Git(seed, "push", "origin", "main");

        var path = Path.Combine(_root, "clones", "Widget", "main");
        Directory.CreateDirectory(path);
        Git(path, "init", "-q");
        Git(path, "remote", "add", "origin", origin);

        var cloner = new RepoClone(_factory.Services.GetRequiredService<GitRunner>());
        var outcome = await cloner.EnsureAsync(origin, path, Ct);

        Assert.Equal(RepoCloneResult.Cloned, outcome.Result);
        Assert.True(HasCommit(path), "the empty clone was left as it was");
        Assert.Equal("widget\n", File.ReadAllText(Path.Combine(path, "README.md")).Replace("\r\n", "\n"));
    }

    // ---- helpers ----

    /// <summary>
    /// Starts <paramref name="call"/>, waits until its clone has begun, aborts it, waits until the Host
    /// has seen <c>RequestAborted</c> on <paramref name="route"/>, then lets the clone go on.
    /// </summary>
    private async Task AbandonMidCloneAsync<T>(string route, Func<CancellationToken, Task<T>> call)
    {
        using var caller = new CancellationTokenSource();
        var request = call(caller.Token);

        await Clone.Started.Task.WaitAsync(Patience, Ct);
        await caller.CancelAsync();

        try { await request.WaitAsync(Patience, Ct); }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }

        await EventuallyAsync(() => Task.FromResult(_witness.Aborted.Contains(route)));
        Assert.True(_witness.Aborted.Contains(route), $"the abort never reached the Host on {route}");

        Clone.Release();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100, Ct);
        }
    }

    private async Task<string> CreatedTeamAsync(HttpClient person, string name)
    {
        var created = await person.PostAsJsonAsync("/api/teams", new { name, agent = Agent(), memberAgents = new[] { Agent() } }, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var team = (await JsonAsync(created)).GetProperty("id").GetString()!;
        Assert.True(HasCommit(ClonePath(team, team)), "the platform did not clone");
        return team;
    }

    /// <summary>What a killed <c>git clone</c> leaves: a repository with its origin and no commit.</summary>
    private void Hollow(string team)
    {
        var path = ClonePath(team, team);
        Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q");
        Git(path, "remote", "add", "origin", Path.Combine(_dataRoot, "repos", team + ".git"));
        Assert.False(HasCommit(path));
    }

    private static async Task<JsonElement> RepoStatusAsync(HttpClient person, string team)
    {
        var response = await person.GetAsync($"/api/teams/{team}/repo-status", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await JsonAsync(response)).GetProperty("repos").EnumerateArray());
    }

    private static bool CloneReady(JsonElement repo)
    {
        Assert.True(repo.TryGetProperty("cloneReady", out var ready),
            $"the repository card has no not-ready state for a clone: {repo}");
        return ready.GetBoolean();
    }

    /// <summary>Fetch as a person presses it: again, while the answer is 409 "still working".</summary>
    private static async Task<HttpResponseMessage> FetchAsync(HttpClient person, string team, string repo)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await person.PostAsync($"/api/teams/{team}/repos/{repo}/fetch", null, Ct);
            if (response.StatusCode != HttpStatusCode.Conflict || DateTime.UtcNow > deadline) return response;
            await Task.Delay(100, Ct);
        }
    }

    private static bool HasCommit(string clonePath)
    {
        if (!Directory.Exists(Path.Combine(clonePath, ".git"))) return false;
        var start = new ProcessStartInfo("git") { WorkingDirectory = clonePath, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "rev-parse", "--verify", "--quiet", "HEAD^{commit}" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static async Task<long> ReadyItemAsync(HttpClient person)
    {
        var created = await person.PostAsJsonAsync("/api/backlog", new { title = "An item", body = "A spec." }, Ct);
        created.EnsureSuccessStatusCode();
        var id = (await JsonAsync(created)).GetProperty("id").GetInt64();
        (await person.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "ready" }, Ct)).EnsureSuccessStatusCode();
        return id;
    }

    private string Agent() =>
        _factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;

    private string ClonePath(string team, string repo) =>
        Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), repo, "main");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private async Task<IReadOnlyList<TenantEvent>> TenantRowsAsync(string action) =>
        [.. (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(null, ITenantLog.MaxTake, Ct)).Events.Where(e => e.Action == action)];

    private async Task<IReadOnlyList<TenantEvent>> DispatchRowsAsync(long item) =>
        [.. (await TenantRowsAsync(TenantActions.BacklogItemDispatched)).Where(r => r.Subject == PlatformBacklogId.Format(item))];

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

    private async Task<string> ConciergeKeyAsync()
    {
        var user = await _factory.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        return await _factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(user!.Id) + Guid.NewGuid().ToString("N"), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: user.Id, ct: Ct);
    }

    /// <summary>The MCP tools as the Concierge calls them: its key on the request, the Host behind it.</summary>
    /// <remarks>Not async: the accessor's context is an AsyncLocal, which an async helper would set
    /// only for itself.</remarks>
    private PlatformMcpTools ConciergeTools(string key)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = key;
        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            _factory.Services.GetRequiredService<IPrincipalStore>(),
            _factory.Services.GetRequiredService<AgentCatalog>(),
            new FactoryClients(_factory));
    }

    private sealed class FactoryClients(WebApplicationFactory<Program> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => factory.CreateClient();
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

    /// <summary>
    /// The clone, held open partway. Armed, its next clone makes what <c>git clone</c> makes before any
    /// object arrives - an initialised repository at the target - says it has started, and waits on
    /// the gate WITH THE TOKEN THE HOST HANDED IT. A cancelled token stops it there, as a killed git
    /// does, leaving the empty clone; otherwise the real clone is made once the gate opens.
    /// </summary>
    private sealed class BlockingClone(IRepoClone real) : IRepoClone
    {
        private volatile bool _armed;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => _armed = true;

        public void Release() => _gate.TrySetResult();

        public async Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(
            IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct)
        {
            if (!_armed) return await real.EnsureAllAsync(repos, ct);
            _armed = false;

            foreach (var (_, path) in repos)
            {
                Directory.CreateDirectory(path);
                Git(path, "init", "-q");
            }

            Started.TrySetResult();
            await _gate.Task.WaitAsync(ct);

            foreach (var (_, path) in repos) Directory.Delete(path, recursive: true);
            return await real.EnsureAllAsync(repos, ct);
        }
    }

    /// <summary>Records each request path whose <c>RequestAborted</c> fired while it ran.</summary>
    private sealed class AbortWitness : IStartupFilter
    {
        public ConcurrentBag<string> Aborted { get; } = [];

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                var path = context.Request.Path.Value ?? "";

                // NOT DISPOSED WITH THE REQUEST: an abort can unwind the request inline, inside the
                // cancellation itself, and a registration disposed on the way out would never run.
                context.RequestAborted.Register(() => Aborted.Add(path));
                await inner(context);
            });
            next(app);
        };
    }

    public async ValueTask DisposeAsync()
    {
        Clone.Release();
        await _factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
