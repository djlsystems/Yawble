using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A dispatch whose start could not be recorded - here, origin unreachable at the moment of
/// dispatch - tries again on each read of the item and on the team's publish, and records it only
/// while the team branch is unchanged since the dispatch; once the team has committed, never. The
/// start's budget counts the fetch alone. An unrecorded start says so on the item, in the
/// `backlog` tool's show and on the list, and a person may record it by hand with a tenant row,
/// refused once the team has committed. Every origin is a local bare repository; the URL only
/// names it.
/// </summary>
public sealed class LandedStartRetryTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Person = "person@example.test";
    private const string Repo = "Widget";
    private const string Url = "https://github.com/owner/Widget.git";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-start-retry-").FullName;
    private readonly string _origin;
    private readonly string _base;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LandedStartRetryTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "trunk", _origin);
        Git(_root, "clone", _origin, seed);
        Git(seed, "checkout", "-b", "trunk");
        File.WriteAllText(Path.Combine(seed, "README.md"), "widget\n");
        Commit(seed, "base");
        Git(seed, "push", "origin", "trunk");
        _base = Run(seed, "rev-parse", "HEAD").Stdout.Trim();

        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting(InstanceGitIdentity.NameVariable, "Pat Person")
            .UseSetting(InstanceGitIdentity.EmailVariable, "pat@example.test")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new HeldAgent());
                services.AddSingleton<IRepoClone>(new LocalOriginClone(_origin));
            }));
    }

    private IBacklogStore Backlog => _factory.Services.GetRequiredService<IBacklogStore>();

    [Fact]
    public async Task A_start_whose_fetch_failed_is_recorded_on_the_next_read_while_the_team_branch_is_unchanged()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Alpha");
        var clone = Clone(team);
        Git(clone, "branch", $"team/{team}");            // cut at dispatch, no commits of its own

        var item = await DispatchWithOriginAwayAsync(team);
        var dispatch = await CurrentDispatchAsync(item);
        Assert.Empty(await Backlog.BasesAsync(dispatch.Id, Ct));
        var missed = Assert.Single(await Backlog.MissedStartsAsync(dispatch.Id, Ct));
        Assert.Equal("the fetch from origin failed", missed.Reason);
        Assert.Equal(_base, missed.TeamSha);

        var read = await ItemAsync(person, item);

        Assert.True(read.GetProperty("startRecorded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("startDetail").ValueKind);
        var start = Assert.Single(await Backlog.BasesAsync(dispatch.Id, Ct));
        Assert.Equal(_base, start.DefaultSha);
        Assert.Equal(_base, start.TeamSha);
    }

    [Fact]
    public async Task After_the_team_commits_no_start_is_recorded_and_the_retry_stops_for_good()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Beta");
        var item = await DispatchWithOriginAwayAsync(team);
        var dispatch = await CurrentDispatchAsync(item);

        var clone = Clone(team);
        Git(clone, "checkout", "-b", $"team/{team}");
        File.WriteAllText(Path.Combine(clone, "beta.txt"), "beta\n");
        Commit(clone, "beta work");

        var read = await ItemAsync(person, item);

        Assert.False(read.GetProperty("startRecorded").GetBoolean());
        Assert.False(read.GetProperty("startRecordable").GetBoolean());
        Assert.Contains("had commits of its own", read.GetProperty("startDetail").GetString());
        Assert.Empty(await Backlog.BasesAsync(dispatch.Id, Ct));
        Assert.NotNull(Assert.Single(await Backlog.MissedStartsAsync(dispatch.Id, Ct)).StoppedAt);

        // FOR GOOD: the branch put back where it was, then a publish, records nothing.
        Git(clone, "checkout", "trunk");
        Git(clone, "branch", "-D", $"team/{team}");
        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        await _factory.Services.GetRequiredService<ITeamPublisher>()
            .PublishAsync(team, registry.ReposFor(team), new ContainerId(team, "Manager"), dispatch.Correlation, Ct);

        Assert.Empty(await Backlog.BasesAsync(dispatch.Id, Ct));
        Assert.False((await ItemAsync(person, item)).GetProperty("startRecorded").GetBoolean());
    }

    [Fact]
    public async Task The_retry_runs_as_the_teams_publish_begins_before_any_tip_is_recorded()
    {
        var team = await TeamAsync("Gamma");
        var item = await DispatchWithOriginAwayAsync(team);
        var dispatch = await CurrentDispatchAsync(item);

        // The Manager cuts the team branch after the dispatch, at the default branch as it stood
        // then: no commits of its own. A publish begins.
        var clone = Clone(team);
        Git(clone, "branch", $"team/{team}", "origin/trunk");
        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        await _factory.Services.GetRequiredService<ITeamPublisher>()
            .PublishAsync(team, registry.ReposFor(team), new ContainerId(team, "Manager"), dispatch.Correlation, Ct);

        var start = Assert.Single(await Backlog.BasesAsync(dispatch.Id, Ct));
        Assert.Equal(_base, start.DefaultSha);
        Assert.Equal(_base, start.TeamSha);
    }

    [Fact]
    public async Task A_slow_fetch_within_the_budget_records_its_start_and_the_budget_counts_the_fetch_alone()
    {
        var team = await TeamAsync("Delta");

        // The fetch takes 1.5 s and every local read 0.4 s - about 2.4 s of local reads. With a
        // 2.5 s budget over the whole recording this would run out; over the fetch alone it does not.
        var recorder = SlowRecorder(fetchSeconds: 1.5, readSeconds: 0.4, budget: TimeSpan.FromSeconds(2.5));
        var dispatch = await AddDispatchAsync(team);
        var watch = Stopwatch.StartNew();

        await recorder.RecordBaseAsync(dispatch, Ct);

        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(2.5), $"the recording took only {watch.Elapsed}");
        Assert.Equal(_base, Assert.Single(await Backlog.BasesAsync(dispatch.Id, Ct)).DefaultSha);
        Assert.Empty(await Backlog.MissedStartsAsync(dispatch.Id, Ct));
    }

    [Fact]
    public async Task A_fetch_over_the_budget_records_nothing_and_says_so()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Epsilon");
        var recorder = SlowRecorder(fetchSeconds: 5, readSeconds: 0, budget: TimeSpan.FromSeconds(1));
        var dispatch = await AddDispatchAsync(team);

        await recorder.RecordBaseAsync(dispatch, Ct);

        Assert.Empty(await Backlog.BasesAsync(dispatch.Id, Ct));
        Assert.Equal(
            "the fetch from origin took longer than 1 s",
            Assert.Single(await Backlog.MissedStartsAsync(dispatch.Id, Ct)).Reason);
    }

    [Fact]
    public async Task The_start_budget_is_a_setting_read_on_every_recording_and_defaults_to_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), BacklogTipRecorder.DefaultFetchBudget);

        var budget = TimeSpan.FromSeconds(1);
        var asked = 0;
        var team = await TeamAsync("Eta");
        var recorder = SlowRecorder(fetchSeconds: 2, readSeconds: 0, budget: () => { asked++; return budget; });

        await recorder.RecordBaseAsync(await AddDispatchAsync(team), Ct);
        budget = TimeSpan.FromSeconds(10);
        var second = await AddDispatchAsync(team);
        await recorder.RecordBaseAsync(second, Ct);

        Assert.Equal(2, asked);
        Assert.Single(await Backlog.BasesAsync(second.Id, Ct));
    }

    [Fact]
    public async Task An_unrecorded_start_is_on_the_item_the_list_and_the_backlog_tools_show()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Theta");
        var moved = _origin + ".away";
        Directory.Move(_origin, moved);                     // unreachable for the whole test
        var item = await DispatchAsync(team);
        var sentence = "Where this dispatch started was not recorded (the fetch from origin failed); its landed "
            + "state is read live and is not kept after its team is gone.";

        var read = await ItemAsync(person, item);
        Assert.False(read.GetProperty("startRecorded").GetBoolean());
        Assert.Equal(sentence, read.GetProperty("startDetail").GetString());
        Assert.True(read.GetProperty("startRecordable").GetBoolean());

        var list = await person.GetFromJsonAsync<JsonElement>("/api/backlog?", Ct);
        var row = list.EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == item);
        Assert.False(row.GetProperty("startRecorded").GetBoolean());
        Assert.Equal(sentence, row.GetProperty("startDetail").GetString());

        // The Concierge's `backlog` show, and the Manager's.
        var concierge = await ToolsAsync(PrincipalKind.TenantConcierge, null);
        var shown = Body(await concierge.Backlog("show", id: item.ToString(), cancellationToken: Ct));
        Assert.False(shown.GetProperty("item").GetProperty("startRecorded").GetBoolean());
        Assert.Equal(sentence, shown.GetProperty("item").GetProperty("startDetail").GetString());

        var manager = await ToolsAsync(PrincipalKind.Container, team);
        var given = Body(await manager.Backlog("show", id: item.ToString(), team: team, cancellationToken: Ct));
        Assert.False(given.GetProperty("startRecorded").GetBoolean());
        Assert.Equal(sentence, given.GetProperty("startDetail").GetString());

        Directory.Move(moved, _origin);
    }

    [Fact]
    public async Task Record_where_it_started_now_is_a_persons_action_with_a_tenant_row()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Iota");
        var item = await DispatchWithOriginAwayAsync(team);
        var dispatch = await CurrentDispatchAsync(item);

        var concierge = await ToolsAsync(PrincipalKind.TenantConcierge, null);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, concierge.Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/backlog/{item}/record-start", null, Ct)).StatusCode);
        Assert.Empty(await Backlog.BasesAsync(dispatch.Id, Ct));

        var response = await person.PostAsync($"/api/backlog/{item}/record-start", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_base, Assert.Single(await Backlog.BasesAsync(dispatch.Id, Ct)).DefaultSha);
        var row = await _factory.Services.GetRequiredService<ITenantLog>()
            .FindLatestAsync(TenantActions.BacklogItemStartRecorded, PlatformBacklogId.Format(item), Ct);
        Assert.NotNull(row);
        Assert.Equal(Person, row.ActorEmail);
        Assert.True((await ItemAsync(person, item)).GetProperty("startRecorded").GetBoolean());
    }

    [Fact]
    public async Task Record_where_it_started_now_is_refused_once_the_team_has_committed()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Kappa");
        var item = await DispatchWithOriginAwayAsync(team);
        var dispatch = await CurrentDispatchAsync(item);
        var clone = Clone(team);
        Git(clone, "checkout", "-b", $"team/{team}");
        File.WriteAllText(Path.Combine(clone, "kappa.txt"), "kappa\n");
        Commit(clone, "kappa work");

        var response = await person.PostAsync($"/api/backlog/{item}/record-start", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("has commits of its own", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(await Backlog.BasesAsync(dispatch.Id, Ct));
        Assert.Null(await _factory.Services.GetRequiredService<ITenantLog>()
            .FindLatestAsync(TenantActions.BacklogItemStartRecorded, PlatformBacklogId.Format(item), Ct));
    }

    /// <summary>
    /// B0025GapProbeTests.Probe5, pinned: the start fetch fails at dispatch, the start is retried on
    /// a read before the team's first commit, and landed proven afterwards survives the team.
    /// </summary>
    [Fact]
    public async Task A_dispatch_whose_start_fetch_failed_and_was_retried_before_its_first_commit_reads_landed_after_its_team_is_deleted()
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Omicron");
        var item = await DispatchWithOriginAwayAsync(team);
        Assert.True((await ItemAsync(person, item)).GetProperty("startRecorded").GetBoolean());

        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        var publisher = _factory.Services.GetRequiredService<ITeamPublisher>();
        var clone = Clone(team);
        Git(clone, "checkout", "-b", $"team/{team}");
        File.WriteAllText(Path.Combine(clone, "o.txt"), "o\n");
        Commit(clone, "O");
        await publisher.PublishAsync(team, registry.ReposFor(team), new ContainerId(team, "Manager"), null, Ct);
        Git(clone, "push", "origin", $"team/{team}:trunk");
        Git(clone, "fetch", "origin");
        Assert.Equal(BacklogLandedStates.Landed, (await LandedAsync(person, item)).GetProperty("state").GetString());
        Assert.NotNull((await CurrentDispatchAsync(item)).LandedAt);

        // A delete is refused while a member runs, so the team is brought to quiet as a person would.
        await QuietAsync(person, team);
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{team}", Ct)).StatusCode);

        Assert.Equal(BacklogLandedStates.Landed, (await LandedAsync(person, item)).GetProperty("state").GetString());
    }

    /// <summary>Pauses the team and stops every run in flight (this suite's runs never end on their
    /// own), until nothing runs and nothing is queued.</summary>
    private async Task QuietAsync(HttpClient person, string team)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await person.PostAsync($"/api/teams/{team}/pause", null, Ct)).StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            foreach (var member in _factory.Services.GetRequiredService<TeamRegistry>().ContainerIdsOf(team))
            {
                await person.PostAsync($"/api/teams/{team}/containers/{member.Name}/stop", null, Ct);
            }

            var check = await person.GetFromJsonAsync<JsonElement>($"/api/teams/{team}/archive-check", Ct);
            if (check.GetProperty("quiet").GetBoolean()) return;
            Assert.True(DateTime.UtcNow < deadline, check.GetProperty("reason").GetString());
            await Task.Delay(100, Ct);
        }
    }

    private async Task<JsonElement> ItemAsync(HttpClient person, long item)
    {
        _factory.Services.GetRequiredService<BacklogLandedCache>().Clear();
        var body = await person.GetFromJsonAsync<JsonElement>($"/api/backlog/{item}", Ct);
        return body.GetProperty("item");
    }

    private async Task<JsonElement> LandedAsync(HttpClient person, long item) =>
        (await ItemAsync(person, item)).GetProperty("landed");

    private async Task<BacklogDispatch> CurrentDispatchAsync(long item) =>
        (await Backlog.DispatchesAsync(item, Ct))[^1];

    /// <summary>Dispatches with origin moved out of reach, then puts it back.</summary>
    private async Task<long> DispatchWithOriginAwayAsync(string team)
    {
        var moved = _origin + ".away";
        Directory.Move(_origin, moved);
        try
        {
            return await DispatchAsync(team);
        }
        finally
        {
            Directory.Move(moved, _origin);
        }
    }

    private async Task<long> DispatchAsync(string team)
    {
        var dispatch = await AddDispatchAsync(team);
        await _factory.Services.GetRequiredService<BacklogTipRecorder>().RecordBaseAsync(dispatch, Ct);
        return dispatch.Item;
    }

    private async Task<BacklogDispatch> AddDispatchAsync(string team)
    {
        var item = await Backlog.CreateAsync(null, $"Item for {team}", "body", Person, Ct);

        // ABOVE THE TEAM'S FLOOR, as a real dispatch's correlation is: the row it wrote.
        var row = await _factory.Services.GetRequiredService<IMessageLog>().AppendAsync(
            new NewMessage("test.dispatched", "{}", "test", null), Ct);
        return await Backlog.AddDispatchAsync(item.Id, team, team, row.Seq, Person, Ct);
    }

    private BacklogTipRecorder SlowRecorder(double fetchSeconds, double readSeconds, TimeSpan budget) =>
        SlowRecorder(fetchSeconds, readSeconds, () => budget);

    /// <summary>
    /// The product's recorder over a git that sleeps before each fetch and each rev-parse, so the
    /// fetch and the local reads can be timed apart.
    /// </summary>
    private BacklogTipRecorder SlowRecorder(double fetchSeconds, double readSeconds, Func<TimeSpan> budget)
    {
        var script = Path.Combine(_root, $"slow-git-{Guid.NewGuid():N}.sh");
        File.WriteAllText(script, string.Join('\n',
            "#!/bin/sh",
            "for a in \"$@\"; do",
            "  case \"$a\" in",
            FormattableString.Invariant($"    fetch) sleep {fetchSeconds}; break;;"),
            FormattableString.Invariant($"    rev-parse) sleep {readSeconds}; break;;"),
            "  esac",
            "done",
            "exec git \"$@\"",
            ""));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var services = _factory.Services;
        return new BacklogTipRecorder(
            Backlog,
            services.GetRequiredService<IMessageLog>(),
            services.GetRequiredService<TeamRegistry>(),
            services.GetRequiredService<TeamPaths>(),
            new GitRunner(gitExecutable: script),
            budget);
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
            await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Person, Password);
            _personMade = true;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = Person, password = Password }, Ct);
        login.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>The `backlog` tool as a tenant Concierge acting for the person, or a team's Manager.</summary>
    private async Task<KeyedTools> ToolsAsync(PrincipalKind kind, string? team)
    {
        var services = _factory.Services;
        string id;
        string key;

        if (kind == PrincipalKind.TenantConcierge)
        {
            var user = await services.GetRequiredService<IUserStore>().FindAsync(Person, Ct);
            id = ConciergeLaunchFactory.PrincipalId(user!.Id);
            key = await services.GetRequiredService<IPrincipalStore>().MintAsync(
                id, kind, null, ConciergeLaunchFactory.ConciergePermits, ownerUserId: user.Id, ct: Ct);
        }
        else
        {
            id = new ContainerId(team!, TeamRegistry.DefaultManagerName).ToString();
            key = await services.GetRequiredService<IPrincipalStore>().MintAsync(id, kind, team, Permits.All);
        }

        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(new Principal(id, kind, new HashSet<string>()), "test");

        return new KeyedTools(
            new PlatformMcpTools(
                new FixedAccessor(context),
                services.GetRequiredService<IPrincipalStore>(),
                services.GetRequiredService<AgentCatalog>(),
                new ServerClients(_factory.Server.CreateHandler())),
            key);
    }

    private sealed record KeyedTools(PlatformMcpTools Tools, string Key)
    {
        public Task<string> Backlog(string action, string? id = null, string? team = null, CancellationToken cancellationToken = default) =>
            Tools.Backlog(action, id: id, team: team, cancellationToken: cancellationToken);
    }

    private static JsonElement Body(string reply)
    {
        Assert.StartsWith("HTTP 200", reply);
        return JsonDocument.Parse(reply[reply.IndexOf('{')..]).RootElement.Clone();
    }

    /// <summary>Held in a field: <see cref="HttpContextAccessor"/> is async-local.</summary>
    private sealed class FixedAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
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

    /// <summary>
    /// AN AGENT WHOSE RUNS NEVER END on their own. A run's end publishes, and that publish retries the
    /// team's latest dispatch's start: the Manager's first run, ending whenever a loaded host gets to
    /// it, would otherwise retry in the middle of a test - with origin away, or holding back the read
    /// the test makes next. Here the only publishes are the ones a test makes itself.
    /// </summary>
    private sealed class HeldAgent : IAgentRunner
    {
        public async Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentResult(0, "done");
        }
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
