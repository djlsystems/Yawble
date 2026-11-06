using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// AN EMPTY REPOSITORY GETS ITS MAIN. A remote with no commits clones to exactly what a killed clone
/// leaves, so nothing could work in it: no default branch, a card reading "Not ready", and agents left
/// to improvise a first commit on a branch of their own. When origin answers NO ref at all, the
/// platform pushes one empty "Initial commit" as <c>main</c>, never forced, and records <c>main</c> as
/// the default branch read from the remote. An origin with any ref is never written to.
///
/// <para>
/// Every origin here is a bare repository on disk. The Host-level tests route the team's https URL
/// to it through <see cref="ToBare"/>, over the real <see cref="RepoClone"/>.
/// </para>
/// </summary>
public sealed class EmptyOriginGetsMainTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Repo = "Widget";
    private const string Url = $"https://github.com/example/{Repo}.git";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-empty-origin-").FullName;
    private readonly string _origin;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public EmptyOriginGetsMainTests()
    {
        _origin = Path.Combine(_root, $"{Repo}.git");
        Git(_root, "init", "--bare", "-b", "main", _origin);
    }

    // ---- RepoClone over an empty origin ----

    [Fact]
    public async Task An_empty_origin_gets_main_holding_one_initial_commit()
    {
        var path = Path.Combine(_root, "clone", "main");

        var outcome = await new RepoClone(new GitRunner()).EnsureAsync(_origin, path, Ct);

        Assert.Equal(RepoCloneResult.Cloned, outcome.Result);
        Assert.Equal("main", outcome.DefaultBranch);
        var made = Assert.IsType<RepoInitialized>(outcome.Initialized);
        Assert.Equal("main", made.Branch);

        Assert.Equal(made.Sha, Git(_origin, "rev-parse", "refs/heads/main"));
        Assert.Equal("1", Git(_origin, "rev-list", "--count", "main"));
        Assert.Equal("Initial commit", Git(_origin, "log", "-1", "--format=%s", "main"));
        Assert.Equal("Platform", Git(_origin, "log", "-1", "--format=%an", "main"));
        Assert.Equal("refs/heads/main", Git(_origin, "for-each-ref", "--format=%(refname)"));

        // The clone has main as any clone of a repository with commits has it.
        Assert.Equal("main", Git(path, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(made.Sha, Git(path, "rev-parse", "HEAD"));
        Assert.Equal(made.Sha, Git(path, "rev-parse", "origin/main"));
        Assert.Equal("origin/main", Git(path, "rev-parse", "--abbrev-ref", "main@{upstream}"));
        Assert.Equal(string.Empty, Git(path, "status", "--porcelain"));
        Assert.False(await RepoClone.IsEmptyCloneAsync(new GitRunner(), path, Ct));

        // The team branch is cut from it as from any default branch.
        Git(path, "branch", "team/alpha", "main");
        Assert.Equal(made.Sha, Git(path, "rev-parse", "team/alpha"));
    }

    [Fact]
    public async Task The_first_commit_is_authored_by_the_instance_identity_when_one_is_set()
    {
        var path = Path.Combine(_root, "clone", "main");

        var outcome = await new RepoClone(new GitRunner(), identity: () => new GitIdentity("Pat Person", "pat@example.test"))
            .EnsureAsync(_origin, path, Ct);

        Assert.NotNull(outcome.Initialized);
        Assert.Equal("Pat Person <pat@example.test>", Git(_origin, "log", "-1", "--format=%an <%ae>", "main"));
        Assert.Equal("Pat Person <pat@example.test>", Git(_origin, "log", "-1", "--format=%cn <%ce>", "main"));
    }

    [Fact]
    public async Task An_origin_that_gains_a_ref_between_the_check_and_the_push_is_not_written_to()
    {
        var seed = Seed("other");
        var path = Path.Combine(_root, "clone", "main");
        var cloner = new RepoClone(new GitRunner())
        {
            // Somebody pushes after the platform found origin empty and before it pushes.
            BeforeInitialPush = () => { Git(seed, "push", "origin", "other"); return Task.CompletedTask; },
        };

        var outcome = await cloner.EnsureAsync(_origin, path, Ct);

        Assert.Equal(RepoCloneResult.Cloned, outcome.Result);
        Assert.Null(outcome.Initialized);
        Assert.Equal("refs/heads/other", Git(_origin, "for-each-ref", "--format=%(refname)"));

        // Handled as an ordinary repository: cloned again, holding what origin holds.
        Assert.Equal(Git(_origin, "rev-parse", "other"), Git(path, "rev-parse", "origin/other"));
    }

    [Fact]
    public async Task A_refused_push_leaves_origin_and_the_clone_as_they_were_and_says_why()
    {
        RefusePushes();
        var path = Path.Combine(_root, "clone", "main");
        var cloner = new RepoClone(new GitRunner());

        var outcome = await cloner.EnsureAsync(_origin, path, Ct);

        Assert.Equal(RepoCloneResult.Failed, outcome.Result);
        Assert.Null(outcome.Initialized);
        Assert.StartsWith($"{Repo} has no commits, and origin refused", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("main", outcome.Error, StringComparison.Ordinal);
        Assert.Equal(outcome.Error, cloner.NotReadyReason(path));

        Assert.Equal(string.Empty, Git(_origin, "for-each-ref"));
        Assert.True(await RepoClone.IsEmptyCloneAsync(new GitRunner(), path, Ct), "the clone kept a ref of a commit origin refused");
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("tag")]
    public async Task An_origin_with_any_ref_is_never_written_to(string kind)
    {
        var seed = Seed("work");
        if (kind == "branch")
        {
            Git(seed, "push", "origin", "work");
        }
        else
        {
            Git(seed, "tag", "v1");
            Git(seed, "push", "origin", "v1");
        }

        var before = Git(_origin, "for-each-ref", "--format=%(refname) %(objectname)");
        var path = Path.Combine(_root, "clone", "main");

        var outcome = await new RepoClone(new GitRunner()).EnsureAsync(_origin, path, Ct);

        Assert.Equal(RepoCloneResult.Cloned, outcome.Result);
        Assert.Null(outcome.Initialized);
        Assert.Equal(before, Git(_origin, "for-each-ref", "--format=%(refname) %(objectname)"));
        Assert.Equal("1", Git(_origin, "rev-list", "--all", "--count"));
    }

    [Fact]
    public async Task A_local_repository_with_no_commits_gets_main_the_same_way()
    {
        var dataRoot = Path.Combine(_root, "local-data");
        var bare = Path.Combine(dataRoot, "repos", "Empty.git");
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        Git(_root, "init", "--bare", "-b", "main", bare);
        var path = Path.Combine(_root, "local-clone", "main");

        // The Host's push into a local repository goes from the bare repository's side.
        var git = new GitRunner(localOriginFor: clone =>
            string.Equals(Path.GetFullPath(clone), Path.GetFullPath(path), StringComparison.Ordinal) ? bare : null);
        var outcome = await new RepoClone(git, localRepos: new LocalRepos(dataRoot, git)).EnsureAsync("local:Empty", path, Ct);

        Assert.Equal(RepoCloneResult.Cloned, outcome.Result);
        var made = Assert.IsType<RepoInitialized>(outcome.Initialized);
        Assert.Equal("main", outcome.DefaultBranch);
        Assert.Equal(made.Sha, Git(bare, "rev-parse", "refs/heads/main"));
        Assert.Equal("1", Git(bare, "rev-list", "--count", "main"));
        Assert.Equal(made.Sha, Git(path, "rev-parse", "HEAD"));
    }

    // ---- through the Host ----

    [Fact]
    public async Task A_team_created_with_an_empty_repository_has_main_and_a_ready_card()
    {
        var registry = Host().Services.GetRequiredService<TeamRegistry>();
        var team = (await registry.CreateAsync("Alpha", Agent(), memberAgent: Agent(), repos: [Url], ct: Ct)).Id;

        await AssertInitializedAsync(team);
    }

    [Fact]
    public async Task An_empty_repository_attached_to_an_existing_team_has_main_and_a_ready_card()
    {
        var registry = Host().Services.GetRequiredService<TeamRegistry>();
        var team = (await registry.CreateAsync("Beta", Agent(), memberAgent: Agent(), ct: Ct)).Id;

        await registry.SetReposAsync(team, [Url], Ct);

        await AssertInitializedAsync(team);
    }

    [Fact]
    public async Task A_refused_first_push_leaves_the_repository_not_ready_with_the_reason_and_tells_the_manager()
    {
        RefusePushes();
        var services = Host().Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var team = (await registry.CreateAsync("Gamma", Agent(), memberAgent: Agent(), repos: [Url], ct: Ct)).Id;

        Assert.Equal(string.Empty, Git(_origin, "for-each-ref"));
        Assert.Null(registry.DefaultBranchFor(team, Repo).Branch);

        var manager = MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName));
        var notice = Assert.Single(
            await services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [manager], 100, Ct),
            m => m.Payload.Contains("COULD NOT CLONE", StringComparison.Ordinal));
        Assert.Contains($"{Repo} has no commits, and origin refused", notice.Payload, StringComparison.Ordinal);

        var status = await StatusAsync(team);
        Assert.False(status.GetProperty("cloneReady").GetBoolean());
        Assert.StartsWith($"{Repo} has no commits, and origin refused", status.GetProperty("notReadyReason").GetString(), StringComparison.Ordinal);
        Assert.Null(await services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.RepoInitialized, $"{team}/{Repo}", Ct));
    }

    private async Task AssertInitializedAsync(string team)
    {
        var services = Host().Services;
        var registry = services.GetRequiredService<TeamRegistry>();

        Assert.Equal("refs/heads/main", Git(_origin, "for-each-ref", "--format=%(refname)"));
        Assert.Equal("1", Git(_origin, "rev-list", "--count", "main"));
        var sha = Git(_origin, "rev-parse", "main");

        var stored = registry.DefaultBranchFor(team, Repo);
        Assert.Equal("main", stored.FromRemote);
        Assert.Null(stored.SetByPerson);

        var clonePath = Path.Combine(services.GetRequiredService<TeamPaths>().ReposFor(team), Repo, "main");
        Assert.Equal(sha, Git(clonePath, "rev-parse", "main"));

        // The team branch is cut from the clone's default branch, as for any repository.
        Git(clonePath, "branch", $"team/{team}", "main");
        Assert.Equal(sha, Git(clonePath, "rev-parse", $"team/{team}"));

        var status = await StatusAsync(team);
        Assert.True(status.GetProperty("cloneReady").GetBoolean(), status.ToString());
        Assert.Equal("main", status.GetProperty("defaultBranch").GetString());
        Assert.Equal("remote", status.GetProperty("defaultBranchSource").GetString());
        Assert.StartsWith(status.GetProperty("mainSha").GetString()!, sha, StringComparison.Ordinal);
        Assert.StartsWith(status.GetProperty("teamSha").GetString()!, sha, StringComparison.Ordinal);

        var row = await services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.RepoInitialized, $"{team}/{Repo}", Ct);
        Assert.NotNull(row);
        Assert.Null(row.ActorId);
        var detail = JsonDocument.Parse(row.Detail!).RootElement;
        Assert.Equal(team, detail.GetProperty("team").GetString());
        Assert.Equal(Repo, detail.GetProperty("repository").GetString());
        Assert.Equal("main", detail.GetProperty("branch").GetString());
        Assert.Equal(sha, detail.GetProperty("sha").GetString());
    }

    private async Task<JsonElement> StatusAsync(string team)
    {
        var person = await PersonAsync();
        var response = await person.GetAsync($"/api/teams/{team}/repo-status", Ct);
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        return body.GetProperty("repos").EnumerateArray().Single(r => r.GetProperty("name").GetString() == Repo);
    }

    private WebApplicationFactory<Program> Host()
    {
        if (_factory is not null) return _factory;

        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IRepoClone>(sp =>
                    new ToBare(new RepoClone(sp.GetRequiredService<GitRunner>()), _origin));
            }));
        return _factory;
    }

    private string Agent() =>
        Host().Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;

    private async Task<HttpClient> PersonAsync()
    {
        var users = Host().Services.GetRequiredService<IUserStore>();
        if (await users.FindAsync("person@example.test", Ct) is null)
        {
            await users.CreateAsync("person@example.test", Password, Ct);
        }

        var client = Host().CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = Password }, Ct);
        login.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>A clone with one commit on <paramref name="branch"/>, whose origin is the bare one.
    /// Nothing is pushed.</summary>
    private string Seed(string branch)
    {
        var seed = Path.Combine(_root, "seed");
        Git(_root, "clone", _origin, seed);
        Git(seed, "checkout", "-b", branch);
        File.WriteAllText(Path.Combine(seed, "README.md"), "widget\n");
        Git(seed, "add", ".");
        Git(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "-m", "base");
        return seed;
    }

    /// <summary>Origin refuses every ref update, as a protection rule or a missing permission does:
    /// its refs folder cannot be written.</summary>
    private void RefusePushes()
    {
        var heads = Path.Combine(_origin, "refs", "heads");
        File.SetUnixFileMode(heads, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(Path.Combine(_origin, "refs"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return stdout.Trim();
    }

    /// <summary>The real clone, of the bare origin in place of the team's https URL.</summary>
    private sealed class ToBare(RepoClone inner, string origin) : IRepoClone
    {
        public async Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(
            IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct)
        {
            var outcomes = new List<RepoCloneOutcome>();
            foreach (var (url, path) in repos)
            {
                outcomes.Add(await inner.EnsureAsync(origin, path, ct) with { Url = url });
            }

            return outcomes;
        }

        public string? NotReadyReason(string path) => inner.NotReadyReason(path);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        foreach (var dir in new[] { Path.Combine(_origin, "refs"), Path.Combine(_origin, "refs", "heads") })
        {
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (IOException) { }
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
