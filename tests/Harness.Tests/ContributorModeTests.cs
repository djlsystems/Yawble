using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;

namespace Harness.Tests;

/// <summary>
/// A repository with an upstream URL is in contributor mode: its origin is a fork and the
/// clone gets an `upstream` remote beside it. Fetch fetches both; Bring current and Rebase use
/// upstream/&lt;default&gt;; Bring current also fast-forwards the fork's &lt;default&gt; (Sync fork), never
/// forced; DCO sign-off is a commit-msg hook with the instance's git identity.
///
/// Three local bare repositories stand in for GitHub: the upstream, the fork (origin), and the
/// team names GitHub URLs for both. The fork is reached through <see cref="LocalOriginClone"/>, the
/// upstream through an <c>insteadOf</c> rewrite in the clone's config - so the URL the platform
/// stores and adds as a remote is the real-looking one, and git still reaches a local directory.
/// Both name `trunk` as HEAD, so nothing passes by guessing `main`.
/// </summary>
public sealed class ContributorModeTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Repo = "Widget";
    private const string ForkUrl = "https://github.com/fork-owner/Widget.git";
    private const string UpstreamUrl = "https://github.com/project/Widget.git";
    private const string Name = "Pat Person";
    private const string Email = "pat@example.test";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-contributor-").FullName;
    private readonly string _upstream;
    private readonly string _fork;
    private readonly string _seed;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<(string Type, string ForkTrunk)> _appended = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ContributorModeTests()
    {
        _upstream = Path.Combine(_root, "upstream.git");
        _fork = Path.Combine(_root, "fork.git");
        _seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "trunk", _upstream);
        Git(_root, "clone", _upstream, _seed);
        Git(_seed, "checkout", "-b", "trunk");
        File.WriteAllText(Path.Combine(_seed, "README.md"), "widget\n");
        Commit(_seed, "base");
        Git(_seed, "push", "origin", "trunk");
        Git(_root, "clone", "--bare", _upstream, _fork);

        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting(InstanceGitIdentity.NameVariable, Name)
            .UseSetting(InstanceGitIdentity.EmailVariable, Email)
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IRepoClone>(new LocalOriginClone(_fork));

                // Every append is observed with the fork's trunk AS IT IS AT THAT MOMENT, which is
                // what pins "the row is appended after the push".
                var logged = services.Single(d => d.ServiceType == typeof(IMessageLog));
                var inner = (IMessageLog)logged.ImplementationInstance!;
                services.Replace(ServiceDescriptor.Singleton(OrderProbe.Wrap(inner, message =>
                {
                    lock (_appended) _appended.Add((message.Type, TryRevParse(_fork, "refs/heads/trunk") ?? ""));
                })));
            }));
    }

    [Fact]
    public async Task A_repository_with_an_upstream_gets_both_remotes_and_clearing_it_removes_that_remote_and_nothing_else()
    {
        var (team, person) = await TeamWithCloneAsync("Alpha");
        var clone = ClonePath(team);

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(person, team, new { upstreamUrl = UpstreamUrl })).StatusCode);

        Assert.Equal(_fork, Config(clone, "remote.origin.url"));
        Assert.Equal(UpstreamUrl, Config(clone, "remote.upstream.url"));

        // Fetch reaches both remotes.
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        Assert.NotNull(TryRevParse(clone, "refs/remotes/upstream/trunk"));

        // The status, the team and the stored row all say so.
        var status = await RepoStatusAsync(person, team);
        Assert.Equal(UpstreamUrl, status.GetProperty("upstreamUrl").GetString());
        Assert.Equal(ForkUrl, status.GetProperty("originUrl").GetString());
        var entry = Assert.Single((await TeamAsync(person, team)).GetProperty("contributors").EnumerateArray());
        Assert.Equal(UpstreamUrl, entry.GetProperty("upstreamUrl").GetString());
        Assert.Equal("fork-owner", entry.GetProperty("forkOwner").GetString());

        // Everything but the upstream remote, before clearing it.
        var before = Snapshot(clone);

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(person, team, new { upstreamUrl = (string?)null })).StatusCode);

        Assert.Null(Config(clone, "remote.upstream.url"));
        Assert.Equal(before, Snapshot(clone));
        Assert.Equal(JsonValueKind.Null, (await RepoStatusAsync(person, team)).GetProperty("upstreamUrl").ValueKind);
        Assert.Null(_factory.Services.GetRequiredService<TeamRegistry>().ContributorFor(team, Repo).UpstreamUrl);
    }

    [Fact]
    public async Task A_team_created_with_an_upstream_is_cloned_with_both_remotes()
    {
        var services = _factory.Services;
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var person = await PersonAsync();

        var refused = await person.PostAsJsonAsync("/api/teams", new
        {
            name = "Mu", agent, memberAgents = new[] { agent }, repos = new[] { ForkUrl },
            upstreams = new Dictionary<string, string> { [ForkUrl] = "not a url" },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(services.GetRequiredService<TeamRegistry>().ExistingName("Mu"));

        var created = await person.PostAsJsonAsync("/api/teams", new
        {
            name = "Mu", agent, memberAgents = new[] { agent }, repos = new[] { ForkUrl },
            upstreams = new Dictionary<string, string> { [ForkUrl] = UpstreamUrl },
        }, Ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));
        var team = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;

        var clone = ClonePath(team);
        Assert.Equal(_fork, Config(clone, "remote.origin.url"));
        Assert.Equal(UpstreamUrl, Config(clone, "remote.upstream.url"));
        Assert.Equal("fork-owner", services.GetRequiredService<TeamRegistry>().ContributorFor(team, Repo).ForkOwner);
    }

    [Fact]
    public async Task Bring_current_uses_upstream_and_fast_forwards_the_fork_appending_the_row_after_the_push()
    {
        var (team, person) = await ContributorTeamAsync("Beta");
        var clone = ClonePath(team);

        File.WriteAllText(Path.Combine(_seed, "next.txt"), "next\n");
        Commit(_seed, "upstream moved");
        Git(_seed, "push", "origin", "trunk");
        var upstreamTip = RevParse(_upstream, "refs/heads/trunk");
        Assert.NotEqual(upstreamTip, RevParse(_fork, "refs/heads/trunk"));

        var response = await ActAsync(person, team, "bring-current");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.Equal(upstreamTip, RevParse(clone, "refs/heads/trunk"));
        Assert.Equal(upstreamTip, RevParse(_fork, "refs/heads/trunk"));
        Assert.Contains("fast-forwarded", text, StringComparison.Ordinal);

        // One row, appended when the fork already held upstream's tip.
        List<(string Type, string ForkTrunk)> synced;
        lock (_appended) synced = [.. _appended.Where(a => a.Type == MessageTypes.RepoForkSynced)];
        Assert.Equal([(MessageTypes.RepoForkSynced, upstreamTip)], synced);

        // Level already: a second press pushes nothing and appends nothing.
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "bring-current")).StatusCode);
        lock (_appended) Assert.Single(_appended, a => a.Type == MessageTypes.RepoForkSynced);
    }

    [Fact]
    public async Task A_fork_with_commits_upstream_does_not_have_is_refused_naming_the_count_and_nothing_moves()
    {
        var (team, person) = await ContributorTeamAsync("Gamma");
        var clone = ClonePath(team);

        // Two commits pushed to the fork's trunk that upstream never had.
        var elsewhere = Path.Combine(_root, "elsewhere");
        Git(_root, "clone", "-b", "trunk", _fork, elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "a.txt"), "a\n");
        Commit(elsewhere, "fork only 1");
        File.WriteAllText(Path.Combine(elsewhere, "b.txt"), "b\n");
        Commit(elsewhere, "fork only 2");
        Git(elsewhere, "push", "origin", "trunk");
        var forkTip = RevParse(_fork, "refs/heads/trunk");

        File.WriteAllText(Path.Combine(_seed, "next.txt"), "next\n");
        Commit(_seed, "upstream moved");
        Git(_seed, "push", "origin", "trunk");
        var cloneTrunk = RevParse(clone, "refs/heads/trunk");

        var response = await ActAsync(person, team, "bring-current");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, text);
        Assert.Contains("the fork's trunk has 2 commits that are not upstream; it is not synced", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(forkTip, RevParse(_fork, "refs/heads/trunk"));
        Assert.Equal(cloneTrunk, RevParse(clone, "refs/heads/trunk"));
        lock (_appended) Assert.DoesNotContain(_appended, a => a.Type == MessageTypes.RepoForkSynced);
    }

    [Fact]
    public async Task Rebase_goes_onto_upstream_and_the_status_measures_against_it()
    {
        var (team, person) = await ContributorTeamAsync("Delta");
        var clone = ClonePath(team);
        Git(clone, "checkout", "trunk");
        File.WriteAllText(Path.Combine(clone, "team.txt"), "team\n");
        Commit(clone, "team work");

        File.WriteAllText(Path.Combine(_seed, "next.txt"), "next\n");
        Commit(_seed, "upstream moved");
        Git(_seed, "push", "origin", "trunk");
        var upstreamTip = RevParse(_upstream, "refs/heads/trunk");

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        var status = await RepoStatusAsync(person, team);
        Assert.Equal(1, status.GetProperty("mainAhead").GetInt32());
        Assert.Equal(1, status.GetProperty("mainBehind").GetInt32());

        var response = await ActAsync(person, team, "rebase");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.Contains("Rebased onto upstream/trunk.", text, StringComparison.Ordinal);
        Assert.Equal(upstreamTip, RevParse(clone, "trunk~1"));
    }

    [Fact]
    public async Task The_sign_off_hook_adds_the_trailer_once_not_twice_in_the_clone_and_its_worktrees()
    {
        var (team, person) = await TeamWithCloneAsync("Epsilon");
        var clone = ClonePath(team);

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(person, team, new { dcoSignOff = true })).StatusCode);
        Assert.True(DcoHook.IsOurs(clone));

        var tree = Path.Combine(_root, "tree");
        Git(clone, "worktree", "add", "-b", "work", tree, "trunk");
        const string trailer = $"Signed-off-by: {Name} <{Email}>";

        File.WriteAllText(Path.Combine(tree, "one.txt"), "one\n");
        Commit(tree, "plain message");
        Assert.Equal(1, Occurrences(Message(tree), trailer));

        // Amending re-runs the hook over a message that already carries it.
        Git(tree, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "--amend", "--no-edit");
        Assert.Equal(1, Occurrences(Message(tree), trailer));

        // A message that arrives signed off already keeps the one it has.
        File.WriteAllText(Path.Combine(tree, "two.txt"), "two\n");
        Commit(tree, $"signed by hand\n\n{trailer}");
        Assert.Equal(1, Occurrences(Message(tree), trailer));

        // Turned off, the hook goes and a new commit carries none.
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(person, team, new { dcoSignOff = false })).StatusCode);
        Assert.False(File.Exists(DcoHook.PathFor(clone)));
        File.WriteAllText(Path.Combine(tree, "three.txt"), "three\n");
        Commit(tree, "after");
        Assert.Equal(0, Occurrences(Message(tree), "Signed-off-by:"));
    }

    [Fact]
    public async Task Turning_sign_off_on_with_no_git_identity_is_refused_with_the_fix_and_stores_nothing()
    {
        var (team, person) = await TeamWithCloneAsync("Zeta");
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();
        configuration[InstanceGitIdentity.NameVariable] = null;
        configuration[InstanceGitIdentity.EmailVariable] = null;

        var response = await SetAsync(person, team, new { dcoSignOff = true });
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, text);
        Assert.Contains("operator CLI", text, StringComparison.Ordinal);
        Assert.Contains("secret set GIT_AUTHOR_NAME", text, StringComparison.Ordinal);
        Assert.Contains("secret set GIT_AUTHOR_EMAIL", text, StringComparison.Ordinal);
        Assert.False(File.Exists(DcoHook.PathFor(ClonePath(team))));
        Assert.False(_factory.Services.GetRequiredService<TeamRegistry>().ContributorFor(team, Repo).DcoSignOff);
    }

    [Fact]
    public async Task A_commit_msg_hook_the_platform_did_not_write_is_never_replaced()
    {
        var (team, person) = await TeamWithCloneAsync("Eta");
        var hook = DcoHook.PathFor(ClonePath(team));
        File.WriteAllText(hook, "#!/bin/sh\nexit 0\n");

        var response = await SetAsync(person, team, new { dcoSignOff = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("#!/bin/sh\nexit 0\n", File.ReadAllText(hook));
        Assert.False(_factory.Services.GetRequiredService<TeamRegistry>().ContributorFor(team, Repo).DcoSignOff);
    }

    [Fact]
    public async Task Settings_are_refused_when_the_upstream_is_not_a_url_or_is_the_fork_itself()
    {
        var (team, person) = await TeamWithCloneAsync("Theta");

        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(person, team, new { upstreamUrl = "--upload-pack=x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(person, team, new { upstreamUrl = ForkUrl })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(person, team, new { upstreamUrl = UpstreamUrl, forkOwner = "no spaces" })).StatusCode);
        Assert.Null(Config(ClonePath(team), "remote.upstream.url"));
    }

    [Fact]
    public async Task The_settings_and_the_pull_request_record_are_stored_apart()
    {
        var (team, person) = await TeamWithCloneAsync("Iota");
        var registry = _factory.Services.GetRequiredService<TeamRegistry>();
        var store = _factory.Services.GetRequiredService<ITeamStore>();
        var readAt = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var pullRequest = new RepoPullRequest("https://github.com/project/Widget/pull/7", 7, "open", readAt);

        await registry.RecordPullRequestAsync(team, Repo, pullRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, (await SetAsync(person, team, new { upstreamUrl = UpstreamUrl, claSignedNote = "signed 2026-09-20" })).StatusCode);

        var row = Assert.Single(await store.RepoContributorsAsync(Ct), r => string.Equals(r.Team, team, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(pullRequest, row.PullRequest);
        Assert.Equal(UpstreamUrl, row.UpstreamUrl);
        Assert.Equal("signed 2026-09-20", row.ClaSignedNote);

        await registry.RecordPullRequestAsync(team, Repo, null, Ct);
        row = Assert.Single(await store.RepoContributorsAsync(Ct), r => string.Equals(r.Team, team, StringComparison.OrdinalIgnoreCase));
        Assert.Null(row.PullRequest);
        Assert.Equal(UpstreamUrl, row.UpstreamUrl);
    }

    /// <summary>
    /// Agents are not told, handed or offered anything new: no tool, no skill text about forks,
    /// pull requests, upstream or pushing, and a member of a contributor-mode team gets exactly the
    /// environment a member of an owned team gets - nothing naming the upstream, and no git identity.
    /// </summary>
    [Fact]
    public async Task Agents_environment_and_skills_are_unchanged()
    {
        var tools = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            ["backlog", "blocked", "handback", "hiring", "kanban", "member", "needs_decision", "progress", "repo",
             "skills_get", "skills_search", "status", "team_create", "team_current", "team_list", "tell", "wip",
             "workflow_complete", "workflow_show"],
            tools);

        foreach (var body in BuiltInSkills.Bodies())
        {
            foreach (var word in new[] { "git push", "gh pr", "gh repo fork", "upstream", "Signed-off-by", "forkSynced" })
            {
                Assert.DoesNotContain(word, body, StringComparison.OrdinalIgnoreCase);
            }
        }

        var (owned, _) = await TeamWithCloneAsync("Kappa");
        var (contributing, _) = await ContributorTeamAsync("Lambda");
        await SetAsync(await PersonAsync(), contributing, new { upstreamUrl = UpstreamUrl, dcoSignOff = true });

        var ownedEnvironment = await MemberEnvironmentAsync(owned);
        var contributingEnvironment = await MemberEnvironmentAsync(contributing);

        Assert.Equal(ownedEnvironment.Keys.Order(StringComparer.Ordinal), contributingEnvironment.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(contributingEnvironment.Values, v => v.Contains("project/Widget", StringComparison.Ordinal));
        Assert.DoesNotContain(InstanceGitIdentity.NameVariable, contributingEnvironment.Keys);
        Assert.DoesNotContain(InstanceGitIdentity.EmailVariable, contributingEnvironment.Keys);
    }

    private async Task<IReadOnlyDictionary<string, string>> MemberEnvironmentAsync(string team)
    {
        var services = _factory.Services;
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var member = await services.GetRequiredService<TeamRegistry>().AddContainerAsync(team, "DeveloperRowan", agent, "", [], ct: Ct);
        return services.GetRequiredService<ContainerHost>().Find(new ContainerId(member.Team, member.Name))!.Environment;
    }

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

        var clone = ClonePath(team);
        Assert.True(Directory.Exists(Path.Combine(clone, ".git")), "the platform did not clone");
        Git(clone, "config", "user.name", "t");
        Git(clone, "config", "user.email", "t@example.invalid");
        Git(clone, "config", $"url.{_upstream}.insteadOf", UpstreamUrl);
        Assert.Equal("trunk", registry.DefaultBranchFor(team, Repo).Branch);

        return (team, await PersonAsync());
    }

    private static Task<HttpResponseMessage> SetAsync(HttpClient person, string team, object settings) =>
        person.PutAsJsonAsync($"/api/teams/{team}/repos/{Repo}/contributor", settings, Ct);

    private string ClonePath(string team) =>
        Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), Repo, "main");

    /// <summary>What clearing the upstream must leave alone: every ref outside refs/remotes/upstream,
    /// every config line outside remote.upstream, and the hooks directory.</summary>
    private static string Snapshot(string clone)
    {
        var refs = Run(clone, "for-each-ref", "--format=%(refname) %(objectname)").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("refs/remotes/upstream/", StringComparison.Ordinal));
        var config = Run(clone, "config", "--local", "--list").Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("remote.upstream.", StringComparison.Ordinal));
        var hooks = Directory.GetFiles(Path.Combine(clone, ".git", "hooks")).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        return string.Join("\n", refs.Concat(config).Concat(hooks!));
    }

    private static string? Config(string clone, string key)
    {
        var (exit, stdout) = Run(clone, "config", "--get", key);
        return exit == 0 ? stdout.Trim() : null;
    }

    private static string Message(string tree) => Run(tree, "log", "-1", "--format=%B").Stdout;

    private static int Occurrences(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

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

    /// <summary>The product's own <see cref="RepoClone"/>, pointed at the local fork whatever URL
    /// the team names.</summary>
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
