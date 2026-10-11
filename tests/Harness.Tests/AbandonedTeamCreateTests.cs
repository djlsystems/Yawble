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
    private SlowTeamCreatedLog? _tenantLog;
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
                    new RepoClone(sp.GetRequiredService<GitRunner>(), localRepos: sp.GetRequiredService<LocalRepos>()),
                    sp.GetRequiredService<LocalRepos>())));
                var tenantLog = services.Single(d => d.ServiceType == typeof(ITenantLog)).ImplementationInstance as ITenantLog
                    ?? throw new InvalidOperationException("the Host no longer registers its tenant log as an instance");
                services.Replace(ServiceDescriptor.Singleton<ITenantLog>(_tenantLog = new SlowTeamCreatedLog(tenantLog)));
            }));
    }

    private TeamRegistry Teams => _factory.Services.GetRequiredService<TeamRegistry>();

    private BlockingClone Clone => _clone ??= (BlockingClone)_factory.Services.GetRequiredService<IRepoClone>();

    private SlowTeamCreatedLog TenantLog => _tenantLog ??= (SlowTeamCreatedLog)_factory.Services.GetRequiredService<ITenantLog>();

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
        var created = Assert.Single(await TenantRowsAsync(TenantActions.TeamCreated), r => r.Subject == team);
        Assert.Equal(PersonEmail, created.ActorEmail);
        Assert.Equal(await PersonIdAsync(), created.ActorId);
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
        Assert.Equal(PersonEmail, dispatched.ActorEmail);
        Assert.Equal(await PersonIdAsync(), dispatched.ActorId);

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

    [Fact]
    public async Task A_create_whose_clone_fails_after_the_abort_still_tells_its_manager_the_repository_is_not_ready()
    {
        var person = await PersonAsync();
        Clone.Arm(failAfterRelease: true);
        TenantLog.Arm();

        await AbandonMidCloneAsync("/api/teams", token =>
            person.PostAsJsonAsync("/api/teams", new { name = "Abandoned Failure", agent = Agent(), memberAgents = new[] { Agent() } }, token));

        var team = Teams.All().Single(t => t.Name == "Abandoned Failure").Id;
        var clonePath = ClonePath(team, team);
        var manager = MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName));
        var messages = _factory.Services.GetRequiredService<IMessageLog>();
        bool Notice(Message m) => m.Payload.Contains(PayloadFields.RepoNotReady, StringComparison.Ordinal)
            && m.Payload.Contains(JsonSerializer.Serialize(clonePath).Trim('"'), StringComparison.Ordinal);

        // BOTH, NOT THE NOTICE ALONE: the notice is written inside the create, and team.created by its
        // caller afterwards, so the one can be there while the other is still on its way.
        await EventuallyAsync(async () => (await messages.ReadAfterAsync(0, [manager], 100, Ct)).Any(Notice)
            && (await TenantRowsAsync(TenantActions.TeamCreated)).Any(r => r.Subject == team));

        Assert.Contains(await messages.ReadAfterAsync(0, [manager], 100, Ct), Notice);
        Assert.Contains(await TenantRowsAsync(TenantActions.TeamCreated), r => r.Subject == team);
        Assert.Contains(Teams.All(), t => t.Id == team);
    }

    [Fact]
    public async Task A_failure_of_detached_work_whose_caller_has_gone_is_logged()
    {
        var logger = new RecordingLogger();
        var detached = new DetachedWork(new Lifetime(), logger);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();

        var waiting = detached.RunAsync<int>("Test work", async _ =>
        {
            await gate.Task;
            throw new InvalidOperationException("the clone fell over");
        }, caller.Token);

        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        gate.SetResult();

        await EventuallyAsync(() => Task.FromResult(!logger.Entries.IsEmpty));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        Assert.Contains("Test work", entry.Message, StringComparison.Ordinal);
        Assert.Equal("the clone fell over", entry.Exception?.Message);
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
        var notice = await NotReadyNoticeAsync(team);

        var response = await FetchAsync(person, team, team);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(HasCommit(ClonePath(team, team)), "Fetch left the empty clone empty");
        Assert.True(CloneReady(await RepoStatusAsync(person, team)), "the clone Fetch made does not read ready");
        await AssertToldReadyAsync(team, notice);
    }

    [Fact]
    public async Task Bring_current_makes_an_empty_clone_into_a_working_clone()
    {
        var person = await PersonAsync();
        var team = await CreatedTeamAsync(person, "Rebrought");
        Hollow(team);
        var notice = await NotReadyNoticeAsync(team);

        var response = await ActUntilFreeAsync(person, $"/api/teams/{team}/repos/{team}/bring-current");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(HasCommit(ClonePath(team, team)), "Bring current left the empty clone empty");
        Assert.True(CloneReady(await RepoStatusAsync(person, team)), "the clone Bring current made does not read ready");
        await AssertToldReadyAsync(team, notice);
    }

    // ---- Fetch, like the platform's clone, leaves alone anything that is not an empty clone ----

    [Fact]
    public async Task Fetch_leaves_alone_a_clone_with_a_commit_of_its_own()
    {
        var person = await PersonAsync();
        var team = await CreatedTeamAsync(person, "OwnCommit");
        var path = ClonePath(team, team);
        Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "work");
        Git(path, "remote", "add", "origin", Path.Combine(_dataRoot, "repos", team + ".git"));
        File.WriteAllText(Path.Combine(path, "notes.txt"), "mine\n");
        Git(path, "add", ".");
        Git(path, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "unpushed");
        var head = Head(path);

        await FetchAsync(person, team, team);

        AssertLeftAlone(path, "notes.txt", "mine\n");
        Assert.Equal(head, Head(path));
    }

    [Fact]
    public async Task Fetch_leaves_alone_a_repository_with_no_commit_but_a_file_in_its_working_tree()
    {
        var person = await PersonAsync();
        var team = await CreatedTeamAsync(person, "Drafted");
        Hollow(team);
        var path = ClonePath(team, team);
        File.WriteAllText(Path.Combine(path, "draft.txt"), "not yet committed\n");

        await FetchAsync(person, team, team);

        AssertLeftAlone(path, "draft.txt", "not yet committed\n");
        Assert.False(HasCommit(path), "Fetch checked a commit out over a working tree it should have left alone");
    }

    [Fact]
    public async Task Fetch_leaves_alone_a_folder_that_is_not_a_repository()
    {
        var person = await PersonAsync();
        var team = await CreatedTeamAsync(person, "Unrelated");
        var path = ClonePath(team, team);
        Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "unrelated.txt"), "somebody's work\n");

        await FetchAsync(person, team, team);

        AssertLeftAlone(path, "unrelated.txt", "somebody's work\n");
        Assert.False(Directory.Exists(Path.Combine(path, ".git")), "Fetch made a repository of a folder it should have left alone");
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

        // A CALL THAT ENDS BEFORE ITS CLONE STARTS says why, rather than leaving a bare timeout:
        // this test has failed with a timeout here, and the call's own answer is the evidence.
        var first = await Task.WhenAny(Clone.Started.Task, request).WaitAsync(Patience, Ct);
        if (first == request)
        {
            var answer = request.IsCompletedSuccessfully ? $"{request.Result}" : $"{request.Exception?.GetBaseException()}";
            Assert.Fail($"The call to {route} ended before its clone started: {answer}");
        }

        await caller.CancelAsync();

        try { await request.WaitAsync(Patience, Ct); }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }

        await EventuallyAsync(() => Task.FromResult(_witness.Aborted.Contains(route)));
        Assert.True(_witness.Aborted.Contains(route), $"the abort never reached the Host on {route}");

        Clone.Release();
    }

    private const string PersonEmail = "person@example.test";

    private async Task<string> PersonIdAsync() =>
        (await _factory.Services.GetRequiredService<IUserStore>().FindAsync(PersonEmail, Ct))!.Id;

    /// <summary>The notice a failed clone roots: an instruction to the Manager naming the clone path.</summary>
    private async Task<long> NotReadyNoticeAsync(string team)
    {
        var manager = MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName));
        var row = await _factory.Services.GetRequiredService<IMessageLog>().AppendAsync(
            new NewMessage(manager, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [PayloadFields.Instruction] = "The platform could not make one of this team's repositories ready.",
                [PayloadFields.RepoNotReady] = new[] { ClonePath(team, team) },
            }), "host"),
            Ct);
        return row.Seq;
    }

    /// <summary>The Manager was told, inside the notice's workflow, that the clone is ready now.</summary>
    private async Task AssertToldReadyAsync(string team, long notice)
    {
        var manager = MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName));
        Assert.Contains(
            await _factory.Services.GetRequiredService<IMessageLog>().ReadAfterAsync(notice, [manager], 100, Ct),
            m => m.CorrelationId == notice && m.Payload.Contains("is ready now", StringComparison.Ordinal));
    }

    /// <summary>The file is as it was, nothing was cloned over it, and nothing was set aside.</summary>
    private static void AssertLeftAlone(string path, string file, string content)
    {
        Assert.Equal(content, File.ReadAllText(Path.Combine(path, file)));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!), e => e != path);
    }

    private static string? Head(string path)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "rev-parse", "--verify", "--quiet", "HEAD" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? stdout.Trim() : null;
    }

    // Fails here, saying what it waited for, rather than returning and leaving the next assertion to
    // read an empty collection with no hint of why (seen once under a loaded full suite).
    private static async Task EventuallyAsync(Func<Task<bool>> condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? what = null)
    {
        var started = DateTime.UtcNow;
        var deadline = started + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100, Ct);
        }

        Assert.Fail($"Not true after {(DateTime.UtcNow - started).TotalSeconds:0.0} s: {what}");
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
    private static Task<HttpResponseMessage> FetchAsync(HttpClient person, string team, string repo) =>
        ActUntilFreeAsync(person, $"/api/teams/{team}/repos/{repo}/fetch");

    /// <summary>A Git dialog action as a person presses it: again, while the answer is 409 "still working".</summary>
    private static async Task<HttpResponseMessage> ActUntilFreeAsync(HttpClient person, string route)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await person.PostAsync(route, null, Ct);
            if (response.StatusCode != HttpStatusCode.Conflict || DateTime.UtcNow > deadline) return response;
            await Task.Delay(100, Ct);
        }
    }

    private static bool HasCommit(string clonePath)
    {
        if (!Directory.Exists(Path.Combine(clonePath, ".git"))) return false;
        var start = new ProcessStartInfo("git") { WorkingDirectory = clonePath, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "rev-parse", "--verify", "--quiet", "HEAD^{commit}" }) start.ArgumentList.Add(arg);

        // The clone folder can be replaced between the check above and the start (a clone made aside
        // and moved into place): that is "no commit yet", not an error.
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }

        using var _ = process;
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
            await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(PersonEmail, Password);
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
    /// object arrives - an initialised repository at the target with its origin - says it has
    /// started, and waits on the gate WITH THE TOKEN THE HOST HANDED IT. A cancelled token stops it
    /// there, as a killed git does, leaving the empty clone; otherwise, once the gate opens, the real
    /// clone is made - or, armed to fail, the clone fails as an unreachable remote does.
    /// </summary>
    private sealed class BlockingClone(IRepoClone real, LocalRepos localRepos) : IRepoClone
    {
        private volatile bool _armed;
        private volatile bool _fail;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(bool failAfterRelease = false)
        {
            _fail = failAfterRelease;
            _armed = true;
        }

        public void Release() => _gate.TrySetResult();

        public async Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(
            IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct)
        {
            if (!_armed) return await real.EnsureAllAsync(repos, ct);
            _armed = false;

            foreach (var (url, path) in repos)
            {
                Directory.CreateDirectory(path);
                Git(path, "init", "-q");
                Git(path, "remote", "add", "origin", localRepos.PathForReference(url) ?? url);
            }

            Started.TrySetResult();
            await _gate.Task.WaitAsync(ct);

            if (_fail)
            {
                return [.. repos.Select(r => new RepoCloneOutcome(
                    r.Url, r.Path, RepoCloneResult.Failed, "fatal: the remote end hung up unexpectedly"))];
            }

            foreach (var (_, path) in repos) Directory.Delete(path, recursive: true);
            return await real.EnsureAllAsync(repos, ct);
        }
    }

    /// <summary>
    /// The tenant log, with <c>team.created</c> held back. Armed, every write of that action waits
    /// before it reaches the real log - never skipped, never held behind another write - so the
    /// gap the Host already has between a create and its caller's tenant row is wide on every run.
    /// Every other write, and every read, goes straight through.
    /// </summary>
    private sealed class SlowTeamCreatedLog(ITenantLog inner) : ITenantLog
    {
        private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);
        private volatile bool _armed;

        public void Arm() => _armed = true;

        public async Task WriteAsync(string? actorId, string? actorEmail, string action, string? subject = null,
            string? subjectName = null, string? detail = null, CancellationToken ct = default)
        {
            if (_armed && action == TenantActions.TeamCreated) await Task.Delay(Delay, ct);
            await inner.WriteAsync(actorId, actorEmail, action, subject, subjectName, detail, ct);
        }

        public Task<TenantEvent?> FindLatestAsync(string action, string subject, CancellationToken ct = default) =>
            inner.FindLatestAsync(action, subject, ct);

        public Task<IReadOnlyList<TenantEvent>> FindLatestBySubjectAsync(
            IReadOnlyCollection<string> actions, CancellationToken ct = default) =>
            inner.FindLatestBySubjectAsync(actions, ct);

        public Task<TenantLogPage> ReadAsync(long? before = null, int take = 50, CancellationToken ct = default) =>
            inner.ReadAsync(before, take, ct);
    }

    private sealed class Lifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<DetachedWork>
    {
        public ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception), exception));
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
