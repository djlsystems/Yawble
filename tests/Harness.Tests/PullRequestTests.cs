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
/// Open pull request takes the place of Merge to main in contributor mode; a DCO check stops
/// it on a commit with no sign-off; landed in contributor mode is the recorded pull request's state,
/// asked of GitHub at most once a minute; Fork it for me forks an upstream. GITHUB IS NEVER CALLED:
/// <see cref="FakeGitHub"/> answers for it, and the local bare repositories of
/// <see cref="ContributorModeTests"/> stand in for the fork and the upstream.
/// </summary>
public sealed class PullRequestTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Repo = "Widget";
    private const string ForkUrl = "https://github.com/fork-owner/Widget.git";
    private const string UpstreamUrl = "https://github.com/project/Widget.git";
    private const string Trailer = "Signed-off-by: Pat Person <pat@example.test>";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-pull-request-").FullName;
    private readonly string _upstream;
    private readonly string _fork;
    private readonly FakeGitHub _gitHub = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<string> _appended = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PullRequestTests()
    {
        _upstream = Path.Combine(_root, "upstream.git");
        _fork = Path.Combine(_root, "fork.git");
        var seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "trunk", _upstream);
        Git(_root, "clone", _upstream, seed);
        Git(seed, "checkout", "-b", "trunk");
        File.WriteAllText(Path.Combine(seed, "README.md"), "widget\n");
        Commit(seed, "base");
        Git(seed, "push", "origin", "trunk");
        Git(_root, "clone", "--bare", _upstream, _fork);

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
                services.AddSingleton<IRepoClone>(new LocalOriginClone(_fork));
                services.Replace(ServiceDescriptor.Singleton<IGitHubContributor>(_gitHub));

                var logged = services.Single(d => d.ServiceType == typeof(IMessageLog));
                var inner = (IMessageLog)logged.ImplementationInstance!;
                services.Replace(ServiceDescriptor.Singleton(OrderProbe.Wrap(inner, message =>
                {
                    lock (_appended) _appended.Add(message.Type);
                })));
            }));

        // THE ROW AFTER THE CREATE: whatever was appended when GitHub was asked is what the fake saw.
        _gitHub.OnCreate = () =>
        {
            lock (_appended) return _appended.Count(t => t == MessageTypes.RepoPullRequestOpened);
        };
    }

    [Fact]
    public async Task Contributor_mode_refuses_Merge_to_main_and_an_owned_repository_refuses_Open_pull_request()
    {
        var (owned, person) = await TeamWithCloneAsync("Alpha");
        var (contributing, _) = await ContributorTeamAsync("Beta");

        var merge = await ActAsync(person, contributing, "merge-to-main");
        var mergeText = await merge.Content.ReadAsStringAsync(Ct);
        Assert.True(merge.StatusCode == HttpStatusCode.Conflict, mergeText);
        Assert.Contains("Open pull request", mergeText, StringComparison.Ordinal);

        var open = await OpenAsync(person, owned);
        var openText = await open.Content.ReadAsStringAsync(Ct);
        Assert.True(open.StatusCode == HttpStatusCode.Conflict, openText);
        Assert.Contains("Merge to main", openText, StringComparison.Ordinal);
        Assert.Empty(_gitHub.Calls);

        // The status says which: a fork owner and a CLA note only in contributor mode.
        Assert.Equal(JsonValueKind.Null, (await RepoStatusAsync(person, owned)).GetProperty("forkOwner").ValueKind);
        Assert.Equal("fork-owner", (await RepoStatusAsync(person, contributing)).GetProperty("forkOwner").GetString());
    }

    [Fact]
    public async Task Open_pull_request_opens_from_the_fork_records_it_and_appends_the_row_after_GitHub_opened_it()
    {
        var (team, person) = await ContributorTeamAsync("Gamma");
        PushTeamBranch(team, $"team work\n\n{Trailer}");

        var response = await OpenAsync(person, team, "Widget: the change", "What was delivered.\n\nBacklog B0001");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var create = Assert.Single(_gitHub.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
        Assert.Equal($"create {UpstreamUrl} fork-owner:team/{team} base=trunk title=Widget: the change", create);
        Assert.Equal("What was delivered.\n\nBacklog B0001", _gitHub.LastBody);

        // No row existed while GitHub was being asked; one exists after.
        Assert.Equal(0, _gitHub.RowsSeenAtCreate);
        lock (_appended) Assert.Single(_appended, t => t == MessageTypes.RepoPullRequestOpened);

        var recorded = _factory.Services.GetRequiredService<TeamRegistry>().ContributorFor(team, Repo).PullRequest;
        Assert.NotNull(recorded);
        Assert.Equal(FakeGitHub.Number, recorded.Number);
        Assert.Equal(PullRequestStates.Open, recorded.State);

        var pull = (await RepoStatusAsync(person, team)).GetProperty("pullRequest");
        Assert.Equal(BacklogLandedStates.InReview, pull.GetProperty("landing").GetString());
        Assert.Equal(FakeGitHub.Url, pull.GetProperty("url").GetString());
    }

    [Fact]
    public async Task One_already_open_for_the_branch_is_linked_and_no_second_is_opened()
    {
        var (team, person) = await ContributorTeamAsync("Delta");
        PushTeamBranch(team, "team work");
        _gitHub.Open = new PullRequestInfo("https://github.com/project/Widget/pull/3", 3, PullRequestStates.Open);

        var response = await OpenAsync(person, team);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.Contains("already open", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_gitHub.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
        Assert.Equal(3, _factory.Services.GetRequiredService<TeamRegistry>().ContributorFor(team, Repo).PullRequest?.Number);
        lock (_appended) Assert.DoesNotContain(MessageTypes.RepoPullRequestOpened, _appended);
    }

    [Fact]
    public async Task A_commit_without_the_sign_off_stops_Open_pull_request_and_is_named()
    {
        var (team, person) = await ContributorTeamAsync("Epsilon");
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(person, team, new { upstreamUrl = UpstreamUrl, dcoSignOff = true })).StatusCode);
        var unsigned = PushTeamBranch(team, $"signed one\n\n{Trailer}", "the unsigned one");

        var response = await OpenAsync(person, team);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, text);
        Assert.Contains("1 commit", text, StringComparison.Ordinal);
        Assert.Contains($"{unsigned[..7]} the unsigned one", text, StringComparison.Ordinal);
        Assert.DoesNotContain("signed one", text.Replace("the unsigned one", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain(_gitHub.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
        lock (_appended) Assert.DoesNotContain(MessageTypes.RepoPullRequestOpened, _appended);
    }

    [Fact]
    public async Task Nothing_is_opened_while_the_default_branch_is_not_known_or_the_branch_is_not_on_the_fork()
    {
        var (team, person) = await ContributorTeamAsync("Zeta");

        var missing = await OpenAsync(person, team);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        Assert.Contains($"team/{team} is not on the fork", await missing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        PushTeamBranch(team, "team work");
        await _factory.Services.GetRequiredService<TeamRegistry>().RecordRemoteDefaultBranchAsync(team, Repo, null, Ct);
        var unknown = await OpenAsync(person, team);
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        Assert.Contains("default branch is not known", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        Assert.DoesNotContain(_gitHub.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PullRequestStates.Open, BacklogLandedStates.InReview)]
    [InlineData(PullRequestStates.Closed, BacklogLandedStates.Declined)]
    [InlineData(PullRequestStates.Merged, BacklogLandedStates.Landed)]
    [InlineData(null, BacklogLandedStates.Unknown)]
    public async Task Landed_in_contributor_mode_is_the_recorded_pull_requests_state_and_unreachable_is_unknown(
        string? gitHubSays, string landing)
    {
        var (team, _) = await ContributorTeamAsync("Eta");
        PushTeamBranch(team, "team work");
        var person = await PersonAsync();
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);

        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        var earlier = new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
        await registry.RecordPullRequestAsync(team, Repo, new RepoPullRequest(FakeGitHub.Url, FakeGitHub.Number, PullRequestStates.Open, earlier), Ct);

        _gitHub.State = gitHubSays;
        var landed = await LandedAsync(team);

        Assert.Equal(landing, landed.State);
        if (gitHubSays is null)
        {
            // Unreachable is unknown - the last answer and when it was read are said, not passed off.
            Assert.Contains("could not say", landed.Detail, StringComparison.Ordinal);
            Assert.Contains("2026-09-25 08:00 UTC", landed.Detail, StringComparison.Ordinal);
            Assert.Equal(earlier, landed.ReadAt);
            Assert.Equal(PullRequestStates.Open, registry.ContributorFor(team, Repo).PullRequest?.State);
        }
        else
        {
            Assert.Equal(gitHubSays, registry.ContributorFor(team, Repo).PullRequest?.State);
            Assert.NotEqual(earlier, landed.ReadAt);
        }
    }

    [Fact]
    public async Task GitHub_is_asked_about_one_pull_request_at_most_once_a_minute()
    {
        var (team, _) = await ContributorTeamAsync("Theta");
        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        await registry.RecordPullRequestAsync(team, Repo,
            new RepoPullRequest(FakeGitHub.Url, FakeGitHub.Number, PullRequestStates.Open, DateTimeOffset.UnixEpoch), Ct);

        var now = new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
        var reader = new PullRequestStateReader(_gitHub, registry, () => now);
        _gitHub.State = PullRequestStates.Open;

        await reader.ReadAsync(team, Repo, Ct);
        now = now.AddSeconds(59);
        _gitHub.State = PullRequestStates.Merged;
        var second = await reader.ReadAsync(team, Repo, Ct);
        Assert.Equal(1, _gitHub.Calls.Count(c => c.StartsWith("read", StringComparison.Ordinal)));
        Assert.Equal(BacklogLandedStates.InReview, second!.Landing);

        now = now.AddSeconds(2);
        var third = await reader.ReadAsync(team, Repo, Ct);
        Assert.Equal(2, _gitHub.Calls.Count(c => c.StartsWith("read", StringComparison.Ordinal)));
        Assert.Equal(BacklogLandedStates.Landed, third!.Landing);
        Assert.Equal(now, third.PullRequest.ReadAt);
    }

    [Fact]
    public async Task Fork_it_for_me_answers_the_fork_and_a_refusal_is_GitHubs_sentence_and_what_the_token_needs()
    {
        var person = await PersonAsync();

        var made = await person.PostAsJsonAsync("/api/github/fork", new { upstreamUrl = "https://github.com/project/Widget" }, Ct);
        var body = await made.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        Assert.Equal("https://github.com/fork-owner/Widget", body.GetProperty("forkUrl").GetString());
        Assert.Equal("fork-owner", body.GetProperty("forkOwner").GetString());
        Assert.Equal("fork https://github.com/project/Widget org=", _gitHub.Calls[^1]);

        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync("/api/github/fork",
            new { upstreamUrl = "https://github.com/project/Widget", organisation = "some-org" }, Ct)).StatusCode);
        Assert.Equal("fork https://github.com/project/Widget org=some-org", _gitHub.Calls[^1]);

        _gitHub.ForkRefusal = GhContributor.Refused<ForkInfo>(new GhContributor.GhRun(
            "", "", "HTTP 403: Resource not accessible by personal access token (https://api.github.com/repos/project/Widget/forks)"));
        var refused = await person.PostAsJsonAsync("/api/github/fork", new { upstreamUrl = "https://github.com/project/Widget" }, Ct);
        var text = await refused.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Resource not accessible by personal access token", text, StringComparison.Ordinal);
        Assert.Contains("classic GitHub token with the public_repo scope", text, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await person.PostAsJsonAsync("/api/github/fork",
            new { upstreamUrl = "https://gitlab.com/project/Widget" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_draft_is_the_items_title_then_the_delivery_text_and_the_citation()
    {
        var (team, person) = await ContributorTeamAsync("Iota");
        var messages = _factory.Services.GetRequiredService<IMessageLog>();
        var backlog = _factory.Services.GetRequiredService<IBacklogStore>();

        var root = await messages.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(new ContainerId(team, "Manager")),
            """{"instruction":"do it","subject":"The workflow's subject"}""", "console"), Ct);
        await messages.AppendAsync(new NewMessage(
            MessageTypes.WorkflowCompleted, """{"delivered":"Widget now spins."}""", $"{team}/Manager", root.Seq), Ct);

        var draft = await person.GetFromJsonAsync<JsonElement>($"/api/teams/{team}/repos/{Repo}/pull-request-draft", Ct);
        Assert.Equal("The workflow's subject", draft.GetProperty("title").GetString());
        Assert.Equal("Widget now spins.", draft.GetProperty("body").GetString());

        var item = await backlog.CreateAsync(null, "Make the widget spin", "body", "person@example.test", Ct);
        await backlog.AddDispatchAsync(item.Id, team, team, root.CorrelationId, "person@example.test", Ct);

        draft = await person.GetFromJsonAsync<JsonElement>($"/api/teams/{team}/repos/{Repo}/pull-request-draft", Ct);
        var citation = PlatformBacklogId.Format(item.Id);
        Assert.Equal("Make the widget spin", draft.GetProperty("title").GetString());
        Assert.Equal($"Widget now spins.\n\nBacklog {citation}", draft.GetProperty("body").GetString());
    }

    [Fact]
    public void GitHub_answers_read_as_open_closed_or_merged_and_a_network_failure_as_unreachable()
    {
        static string StateOf(string json)
        {
            using var document = JsonDocument.Parse(json);
            return GhContributor.Describe(document.RootElement).State;
        }

        Assert.Equal(PullRequestStates.Open, StateOf("""{"html_url":"u","number":1,"state":"open","merged":false}"""));
        Assert.Equal(PullRequestStates.Closed, StateOf("""{"html_url":"u","number":1,"state":"closed","merged":false,"merged_at":null}"""));
        Assert.Equal(PullRequestStates.Merged, StateOf("""{"html_url":"u","number":1,"state":"closed","merged":true}"""));

        var offline = GhContributor.Refused<PullRequestInfo>(new GhContributor.GhRun("", "", "error connecting to api.github.com"));
        Assert.True(offline.Unreachable);
        var refused = GhContributor.Refused<PullRequestInfo>(new GhContributor.GhRun("", "", "HTTP 401: Bad credentials"));
        Assert.Contains("public_repo", refused.Refusal, StringComparison.Ordinal);
    }

    private async Task<BacklogLanded> LandedAsync(string team)
    {
        var services = _factory.Services;
        var dispatch = new BacklogDispatch(1, 42, team, team, long.MaxValue, "", "", null, null);
        var landed = await BacklogLandedState.ForAsync(
            [dispatch], services.GetRequiredService<TeamRegistry>(), services.GetRequiredService<TeamPaths>(),
            services.GetRequiredService<GitRunner>(), new BacklogLandedCache(TimeSpan.Zero), Ct,
            new PullRequestStateReader(_gitHub, services.GetRequiredService<TeamRegistry>()));
        return landed[42];
    }

    /// <summary>Pushes commits to the fork as team/{id}, answering the last one's sha.</summary>
    private string PushTeamBranch(string team, params string[] messages)
    {
        var work = Path.Combine(_root, $"work-{team}");
        Git(_root, "clone", "-b", "trunk", _fork, work);
        var sha = "";
        for (var i = 0; i < messages.Length; i++)
        {
            File.WriteAllText(Path.Combine(work, $"change{i}.txt"), $"{i}\n");
            Commit(work, messages[i]);
            sha = Run(work, "rev-parse", "HEAD").Stdout.Trim();
        }

        Git(work, "push", "origin", $"HEAD:refs/heads/team/{team}");
        return sha;
    }

    private static Task<HttpResponseMessage> OpenAsync(
        HttpClient person, string team, string title = "A title", string body = "A body") =>
        person.PostAsJsonAsync($"/api/teams/{team}/repos/{Repo}/pull-request", new { title, body }, Ct);

    private async Task<(string Team, HttpClient Person)> ContributorTeamAsync(string name)
    {
        var (team, person) = await TeamWithCloneAsync(name);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(person, team, new { upstreamUrl = UpstreamUrl })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        return (team, person);
    }

    private async Task<(string Team, HttpClient Person)> TeamWithCloneAsync(string name)
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent, ct: Ct)).Id;
        await registry.SetReposAsync(team, [ForkUrl], Ct);

        var clone = Path.Combine(services.GetRequiredService<TeamPaths>().ReposFor(team), Repo, "main");
        Assert.True(Directory.Exists(Path.Combine(clone, ".git")), "the platform did not clone");
        Git(clone, "config", $"url.{_upstream}.insteadOf", UpstreamUrl);
        Assert.Equal("trunk", registry.DefaultBranchFor(team, Repo).Branch);

        return (team, await PersonAsync());
    }

    /// <summary>An action as a person presses it: again while the answer is 409 "still working".</summary>
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

    private static Task<HttpResponseMessage> SetAsync(HttpClient person, string team, object settings) =>
        person.PutAsJsonAsync($"/api/teams/{team}/repos/{Repo}/contributor", settings, Ct);

    private static async Task<JsonElement> RepoStatusAsync(HttpClient person, string team)
    {
        var body = await person.GetFromJsonAsync<JsonElement>($"/api/teams/{team}/repo-status", Ct);
        return Assert.Single(body.GetProperty("repos").EnumerateArray());
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

    /// <summary>GitHub, answered locally. Every call is written down; nothing leaves the machine.</summary>
    private sealed class FakeGitHub : IGitHubContributor
    {
        public const string Url = "https://github.com/project/Widget/pull/12";
        public const int Number = 12;

        public List<string> Calls { get; } = [];
        public PullRequestInfo? Open { get; set; }
        public string? State { get; set; } = PullRequestStates.Open;
        public GitHubAnswer<ForkInfo>? ForkRefusal { get; set; }
        public Func<int>? OnCreate { get; set; }
        public int? RowsSeenAtCreate { get; private set; }
        public string? LastBody { get; private set; }

        public Task<GitHubAnswer<ForkInfo>> ForkAsync(string upstreamUrl, string? organisation, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"fork {upstreamUrl} org={organisation}");
            return Task.FromResult(ForkRefusal ?? GitHubAnswer<ForkInfo>.Of(
                new ForkInfo($"https://github.com/{organisation ?? "fork-owner"}/Widget", organisation ?? "fork-owner")));
        }

        public Task<GitHubAnswer<PullRequestInfo?>> FindOpenAsync(string upstreamUrl, string forkOwner, string branch, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"find {upstreamUrl} {forkOwner}:{branch}");
            return Task.FromResult(GitHubAnswer<PullRequestInfo?>.Of(Open));
        }

        public Task<GitHubAnswer<PullRequestInfo>> CreateAsync(
            string upstreamUrl, string forkOwner, string branch, string baseBranch, string title, string body, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"create {upstreamUrl} {forkOwner}:{branch} base={baseBranch} title={title}");
            RowsSeenAtCreate = OnCreate?.Invoke();
            LastBody = body;
            return Task.FromResult(GitHubAnswer<PullRequestInfo>.Of(new PullRequestInfo(Url, Number, PullRequestStates.Open)));
        }

        public Task<GitHubAnswer<PullRequestInfo>> ReadAsync(string upstreamUrl, int number, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"read {upstreamUrl} #{number}");
            return Task.FromResult(State is null
                ? new GitHubAnswer<PullRequestInfo>(null, "GitHub said: error connecting to api.github.com", Unreachable: true)
                : GitHubAnswer<PullRequestInfo>.Of(new PullRequestInfo(Url, number, State)));
        }
    }

    /// <summary>The product's own <see cref="RepoClone"/>, pointed at the local fork.</summary>
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
