using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harness.Tests;

/// <summary>
/// FORGIVING TEAM REPOSITORIES (B001F). A team created with no repository gets a local one named
/// after it; a URL <c>git ls-remote</c> cannot read is refused before anything is made, with the
/// choices the caller may take; an agent is never let create on GitHub or attach anyway; deleting a
/// team keeps its local repository.
///
/// <para>
/// Nothing here reaches the network. <c>https://github.com/</c> is rewritten by git itself
/// (<c>url.&lt;base&gt;.insteadOf</c>, handed in through <c>GIT_CONFIG_*</c>) to a folder of bare
/// repositories in the test's root, so the Host's real <c>ls-remote</c> and real clone run against
/// them; GitHub is <see cref="FakeGitHub"/>, whose create makes the bare repository there. A network
/// failure is a connection to a closed local port.
/// </para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ForgivingTeamReposTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Missing = "https://github.com/acme/job-tracker";
    private const string Unreachable = "https://127.0.0.1:1/acme/widget.git";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-forgiving-repos-").FullName;
    private readonly string _dataRoot;
    private readonly string _gitHub;
    private readonly EnvironmentScope _environment;
    private readonly FakeGitHub _fakeGitHub;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ForgivingTeamReposTests()
    {
        _dataRoot = Path.Combine(_root, "data");
        _gitHub = Path.Combine(_root, "github");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_gitHub);
        _environment = new EnvironmentScope(
        [
            new("GIT_CONFIG_COUNT", "1"),
            new("GIT_CONFIG_KEY_0", $"url.file://{_gitHub}/.insteadOf"),
            new("GIT_CONFIG_VALUE_0", "https://github.com/"),
        ]);
        _fakeGitHub = new FakeGitHub(_gitHub);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.Replace(ServiceDescriptor.Singleton<IGitHubContributor>(_fakeGitHub));
            }));
    }

    private TeamRegistry Teams => _factory.Services.GetRequiredService<TeamRegistry>();

    private string Bare(string name) => Path.Combine(_dataRoot, "repos", name + ".git");

    // ---- the default ----

    [Fact]
    public async Task A_team_created_with_no_repository_gets_its_local_repository_cloned_with_its_default_branch_known()
    {
        var person = await PersonAsync();

        var created = await CreateTeamAsync(person, new { name = "Job Tracker", agent = Agent(), memberAgents = new[] { Agent() } });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await JsonAsync(created);
        var team = body.GetProperty("id").GetString()!;
        Assert.Equal(team, body.GetProperty("localRepository").GetProperty("name").GetString());
        Assert.Equal($"local:{team}", body.GetProperty("localRepository").GetProperty("reference").GetString());
        Assert.True(body.GetProperty("localRepository").GetProperty("created").GetBoolean());
        Assert.Equal([$"local:{team}"], Teams.ReposFor(team));
        Assert.True(Directory.Exists(Bare(team)));
        Assert.True(Directory.Exists(Path.Combine(ClonePath(team, team), ".git")), "the platform did not clone");
        Assert.Equal("main", Teams.DefaultBranchFor(team, team).Branch);

        var row = Assert.Single(await TenantRowsAsync(TenantActions.LocalRepoCreated));
        Assert.Equal(team, row.Subject);
        Assert.Contains($"local:{team}", Assert.Single(await TenantRowsAsync(TenantActions.TeamCreated)).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_team_create_tool_gives_a_team_with_no_repository_its_local_repository_and_localRepository_false_gives_none()
    {
        var person = await PersonAsync();
        var tools = ConciergeTools(await ConciergeKeyAsync());

        var reply = await tools.TeamCreate("Tool Team", agent: Agent(), cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", reply, StringComparison.Ordinal);
        var team = Teams.All().Single(t => t.Name == "Tool Team").Id;
        Assert.Equal([$"local:{team}"], Teams.ReposFor(team));
        Assert.True(Directory.Exists(Path.Combine(ClonePath(team, team), ".git")));
        Assert.Equal("main", Teams.DefaultBranchFor(team, team).Branch);

        var none = await tools.TeamCreate("Bare Team", agent: Agent(), localRepository: false, cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", none, StringComparison.Ordinal);
        var bareTeam = Teams.All().Single(t => t.Name == "Bare Team").Id;
        Assert.Empty(Teams.ReposFor(bareTeam));
        Assert.False(Directory.Exists(Bare(bareTeam)));
        person.Dispose();
    }

    [Fact]
    public void The_team_create_tool_says_a_team_with_no_repos_gets_a_local_repository_unless_told_not_to()
    {
        var method = typeof(PlatformMcpTools).GetMethod(nameof(PlatformMcpTools.TeamCreate))!;
        var description = ((System.ComponentModel.DescriptionAttribute)method
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Single()).Description;

        Assert.Contains("local repository", description, StringComparison.Ordinal);
        Assert.Contains("localRepository: false", description, StringComparison.Ordinal);
        Assert.Contains(method.GetParameters(), p => p.Name == "localRepository");
    }

    [Fact]
    public async Task Opting_out_creates_no_local_repository()
    {
        var person = await PersonAsync();

        var created = await CreateTeamAsync(person, new
        {
            name = "Plain", agent = Agent(), memberAgents = new[] { Agent() }, localRepository = false,
        });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await JsonAsync(created);
        Assert.False(body.TryGetProperty("localRepository", out _));
        Assert.Empty(Teams.ReposFor(body.GetProperty("id").GetString()!));
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "repos")) && Directory.EnumerateFileSystemEntries(Path.Combine(_dataRoot, "repos")).Any());
        Assert.Empty(await TenantRowsAsync(TenantActions.LocalRepoCreated));
    }

    [Fact]
    public async Task An_unused_local_repository_of_the_teams_name_is_reused_and_one_a_team_uses_gets_a_suffix()
    {
        var person = await PersonAsync();
        Assert.Equal(HttpStatusCode.Created, (await person.PostAsJsonAsync("/api/local-repos", new { name = "Gamma" }, Ct)).StatusCode);

        var first = await JsonAsync(await CreateTeamAsync(person, new { name = "Gamma", agent = Agent(), memberAgents = new[] { Agent() } }));
        Assert.Equal("Gamma", first.GetProperty("localRepository").GetProperty("name").GetString());
        Assert.False(first.GetProperty("localRepository").GetProperty("created").GetBoolean());

        // The first team is deleted, keeping Gamma; a second team takes Gamma, so a third gets Gamma-2.
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync("/api/teams/Gamma", Ct)).StatusCode);
        var again = await JsonAsync(await CreateTeamAsync(person, new { name = "Gamma", agent = Agent(), memberAgents = new[] { Agent() } }));
        Assert.Equal("Gamma", again.GetProperty("localRepository").GetProperty("name").GetString());

        var other = await JsonAsync(await CreateTeamAsync(person, new { name = "Delta", agent = Agent(), memberAgents = new[] { Agent() }, localRepository = false }));
        var otherTeam = other.GetProperty("id").GetString()!;
        var attached = await person.PostAsync($"/api/teams/{otherTeam}/local-repo", null, Ct);
        Assert.Equal(HttpStatusCode.Created, attached.StatusCode);
        Assert.Equal("Delta", (await JsonAsync(attached)).GetProperty("localRepository").GetProperty("name").GetString());

        // A name a team uses: the suffix.
        var suffixed = await _factory.Services.GetRequiredService<TeamRepoSetup>().EnsureLocalAsync("Gamma", Ct);
        Assert.Equal(new TeamLocalRepository("Gamma-2", "local:Gamma-2", true), suffixed);
        Assert.True(Directory.Exists(Bare("Gamma-2")));
    }

    [Fact]
    public async Task When_the_local_repository_cannot_be_made_no_team_is_created_and_the_reason_is_named()
    {
        var person = await PersonAsync();
        // `<dataRoot>/repos` is a file: no repository can be made under it.
        File.WriteAllText(Path.Combine(_dataRoot, "repos"), "not a folder");

        var created = await CreateTeamAsync(person, new { name = "Doomed", agent = Agent(), memberAgents = new[] { Agent() } });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Contains("could not be created", (await JsonAsync(created)).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Teams.All(), t => t.Id == "Doomed");
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "teams", "Doomed")));
        Assert.Empty(await TenantRowsAsync(TenantActions.TeamCreated));
    }

    // ---- a URL that cannot be read ----

    [Fact]
    public async Task A_missing_url_is_refused_naming_it_and_gits_reason_and_leaves_no_team_row_folder_or_clone()
    {
        var person = await PersonAsync();
        _fakeGitHub.CanCreate = true;

        var refused = await CreateTeamAsync(person, new
        {
            name = "Job Tracker", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Missing },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await JsonAsync(refused);
        var error = body.GetProperty("error").GetString()!;
        Assert.Contains(Missing, error, StringComparison.Ordinal);
        Assert.Contains("does not appear to be a git repository", error, StringComparison.Ordinal);
        Assert.Contains("create it on GitHub (private)", error, StringComparison.Ordinal);
        Assert.Contains("use a local repository instead", error, StringComparison.Ordinal);
        Assert.Equal("repo-check-failed", body.GetProperty("code").GetString());
        var entry = Assert.Single(body.GetProperty("repos").EnumerateArray());
        Assert.Equal(Missing, entry.GetProperty("url").GetString());
        Assert.Equal("not-found", entry.GetProperty("failure").GetString());
        Assert.Equal(["create-on-github", "use-local"], Strings(entry.GetProperty("choices")));

        Assert.Empty(Teams.All());
        Assert.Empty(await _factory.Services.GetRequiredService<ITeamStore>().TeamsAsync(Ct));
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "teams", "JobTracker")));
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "repos")) && Directory.EnumerateFileSystemEntries(Path.Combine(_dataRoot, "repos")).Any());
        Assert.Empty(await TenantRowsAsync(TenantActions.TeamCreated));
        Assert.DoesNotContain(_fakeGitHub.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_on_github_is_offered_only_when_the_token_can_create_repositories()
    {
        var person = await PersonAsync();
        _fakeGitHub.CanCreate = false;

        var refused = await JsonAsync(await CreateTeamAsync(person, new
        {
            name = "No Token", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Missing },
        }));

        Assert.Equal(["use-local"], Strings(Assert.Single(refused.GetProperty("repos").EnumerateArray()).GetProperty("choices")));
        Assert.DoesNotContain("GitHub", refused.GetProperty("error").GetString()!.Replace(Missing, "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_on_github_makes_it_private_through_the_faked_interface_and_ends_with_a_cloned_team()
    {
        var person = await PersonAsync();
        _fakeGitHub.CanCreate = true;

        var created = await CreateTeamAsync(person, new
        {
            name = "Job Tracker", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Missing },
            repoChoices = new Dictionary<string, string> { [Missing] = "create-on-github" },
        });

        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        Assert.Contains("create acme/job-tracker private", _fakeGitHub.Calls);
        var body = await JsonAsync(created);
        var team = body.GetProperty("id").GetString()!;
        Assert.Equal([Missing], Strings(body.GetProperty("createdOnGitHub")));
        Assert.False(body.TryGetProperty("localRepository", out _));
        Assert.Equal([Missing], Teams.ReposFor(team));
        Assert.True(Directory.Exists(Path.Combine(ClonePath(team, "job-tracker"), ".git")), "the platform did not clone");
        Assert.Equal("main", Teams.DefaultBranchFor(team, "job-tracker").Branch);
    }

    [Fact]
    public async Task Use_a_local_repository_instead_ends_with_a_cloned_team_on_its_local_repository()
    {
        var person = await PersonAsync();

        var created = await CreateTeamAsync(person, new
        {
            name = "Job Tracker", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Missing },
            repoChoices = new Dictionary<string, string> { [Missing] = "use-local" },
        });

        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        var team = (await JsonAsync(created)).GetProperty("id").GetString()!;
        Assert.Equal([$"local:{team}"], Teams.ReposFor(team));
        Assert.True(Directory.Exists(Path.Combine(ClonePath(team, team), ".git")));
        Assert.Equal("main", Teams.DefaultBranchFor(team, team).Branch);
        Assert.DoesNotContain(_fakeGitHub.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attach_anyway_is_offered_and_taken_for_a_network_failure_and_refused_for_a_url_that_is_not_there()
    {
        var person = await PersonAsync();

        var refused = await JsonAsync(await CreateTeamAsync(person, new
        {
            name = "Offline", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Unreachable },
        }));
        var entry = Assert.Single(refused.GetProperty("repos").EnumerateArray());
        Assert.Equal("unreachable", entry.GetProperty("failure").GetString());
        Assert.Equal(["use-local", "attach-anyway"], Strings(entry.GetProperty("choices")));
        Assert.Contains("attach it anyway", refused.GetProperty("error").GetString(), StringComparison.Ordinal);

        var attached = await CreateTeamAsync(person, new
        {
            name = "Offline", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Unreachable },
            repoChoices = new Dictionary<string, string> { [Unreachable] = "attach-anyway" },
        });
        Assert.True(attached.StatusCode == HttpStatusCode.OK, await attached.Content.ReadAsStringAsync(Ct));
        Assert.Equal([Unreachable], Teams.ReposFor((await JsonAsync(attached)).GetProperty("id").GetString()!));

        var notThere = await CreateTeamAsync(person, new
        {
            name = "Not There", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Missing },
            repoChoices = new Dictionary<string, string> { [Missing] = "attach-anyway" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, notThere.StatusCode);
        Assert.DoesNotContain(Teams.All(), t => t.Name == "Not There");
    }

    [Fact]
    public async Task A_url_that_exists_is_created_without_a_question()
    {
        var person = await PersonAsync();
        MakeGitHubRepository("acme", "exists");

        var created = await CreateTeamAsync(person, new
        {
            name = "Exists", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { "https://github.com/acme/exists.git" },
        });

        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        Assert.True(Directory.Exists(Path.Combine(ClonePath("Exists", "exists"), ".git")));
        Assert.False((await JsonAsync(created)).TryGetProperty("localRepository", out _));
    }

    // ---- agents ----

    [Fact]
    public async Task An_agent_is_told_the_choices_offered_only_a_local_repository_and_refused_github_create_and_attach_anyway()
    {
        await PersonAsync();
        _fakeGitHub.CanCreate = true;
        var concierge = await ConciergeClientAsync();

        var refused = await concierge.PostAsJsonAsync("/api/teams", new
        {
            name = "Agent Team", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Missing },
        }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await JsonAsync(refused);
        Assert.Equal(["use-local"], Strings(Assert.Single(body.GetProperty("repos").EnumerateArray()).GetProperty("choices")));
        var error = body.GetProperty("error").GetString()!;
        Assert.Contains("A person can create it on GitHub (private)", error, StringComparison.Ordinal);
        Assert.Contains("local repository", error, StringComparison.Ordinal);
        Assert.Contains("ask them to create the remote", error, StringComparison.Ordinal);

        foreach (var (url, choice) in new[] { (Missing, "create-on-github"), (Unreachable, "attach-anyway") })
        {
            var forbidden = await concierge.PostAsJsonAsync("/api/teams", new
            {
                name = "Agent Team", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { url },
                repoChoices = new Dictionary<string, string> { [url] = choice },
            }, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Contains("person's choice", await forbidden.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Empty(Teams.All());
        Assert.DoesNotContain(_fakeGitHub.Calls, c => c.StartsWith("create", StringComparison.Ordinal));

        // The team_create tool says the same, and its choice - no repos - gives a local repository.
        var tools = ConciergeTools(await ConciergeKeyAsync());
        var toolReply = await tools.TeamCreate("Agent Team", agent: Agent(), repos: [Missing], cancellationToken: Ct);
        Assert.StartsWith("HTTP 422", toolReply, StringComparison.Ordinal);
        Assert.Contains("ask them to create the remote", toolReply, StringComparison.Ordinal);
        Assert.StartsWith("HTTP 200", await tools.TeamCreate("Agent Team", agent: Agent(), cancellationToken: Ct), StringComparison.Ordinal);
        Assert.Equal(["local:AgentTeam"], Teams.ReposFor("AgentTeam"));
    }

    // ---- attach in Team settings ----

    [Fact]
    public async Task Attaching_a_missing_url_is_refused_with_the_list_unchanged_and_use_local_attaches_the_teams_local_repository()
    {
        var person = await PersonAsync();
        var team = (await JsonAsync(await CreateTeamAsync(person, new
        {
            name = "Settings", agent = Agent(), memberAgents = new[] { Agent() }, localRepository = false,
        }))).GetProperty("id").GetString()!;

        var refused = await person.PutAsJsonAsync($"/api/teams/{team}/repos", new[] { Missing }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains(Missing, (await JsonAsync(refused)).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(Teams.ReposFor(team));
        Assert.False(Directory.Exists(ClonePath(team, "job-tracker")));

        var local = await person.PutAsJsonAsync($"/api/teams/{team}/repos", new
        {
            repos = new[] { Missing },
            repoChoices = new Dictionary<string, string> { [Missing] = "use-local" },
        }, Ct);
        Assert.True(local.StatusCode == HttpStatusCode.OK, await local.Content.ReadAsStringAsync(Ct));
        Assert.Equal([$"local:{team}"], Teams.ReposFor(team));
        Assert.True(Directory.Exists(Path.Combine(ClonePath(team, team), ".git")));

        // Already attached: not read again.
        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync($"/api/teams/{team}/repos", new[] { $"local:{team}" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await person.PostAsync($"/api/teams/{team}/local-repo", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Create_a_local_repository_for_an_existing_team_clones_it_and_is_a_persons_action()
    {
        var person = await PersonAsync();
        var team = (await JsonAsync(await CreateTeamAsync(person, new
        {
            name = "Later", agent = Agent(), memberAgents = new[] { Agent() }, localRepository = false,
        }))).GetProperty("id").GetString()!;

        var created = await person.PostAsync($"/api/teams/{team}/local-repo", null, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await JsonAsync(created);
        Assert.Equal($"local:{team}", body.GetProperty("localRepository").GetProperty("reference").GetString());
        Assert.Equal([$"local:{team}"], Teams.ReposFor(team));
        Assert.True(Directory.Exists(Path.Combine(ClonePath(team, team), ".git")));
        Assert.Equal("main", Teams.DefaultBranchFor(team, team).Branch);
        Assert.Contains(await TenantRowsAsync(TenantActions.LocalRepoCreated), r => r.Subject == team);

        var concierge = await ConciergeClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await concierge.PostAsync($"/api/teams/{team}/local-repo", null, Ct)).StatusCode);
    }

    // ---- deletion ----

    [Fact]
    public async Task Deleting_a_team_keeps_its_local_repository_which_is_listed_as_unused()
    {
        var person = await PersonAsync();
        var team = (await JsonAsync(await CreateTeamAsync(person, new
        {
            name = "Short Lived", agent = Agent(), memberAgents = new[] { Agent() },
        }))).GetProperty("id").GetString()!;
        var before = Assert.Single((await person.GetFromJsonAsync<JsonElement>("/api/local-repos", Ct)).EnumerateArray());
        Assert.False(before.GetProperty("unused").GetBoolean());

        var deleted = await person.DeleteAsync($"/api/teams/{team}", Ct);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal([$"local:{team}"], Strings((await JsonAsync(deleted)).GetProperty("localRepositoriesKept")));
        Assert.True(Directory.Exists(Bare(team)));
        var after = Assert.Single((await person.GetFromJsonAsync<JsonElement>("/api/local-repos", Ct)).EnumerateArray());
        Assert.Equal(team, after.GetProperty("name").GetString());
        Assert.True(after.GetProperty("unused").GetBoolean());
        Assert.Empty(after.GetProperty("teams").EnumerateArray());
        Assert.Contains($"local:{team}", Assert.Single(await TenantRowsAsync(TenantActions.TeamDeleted)).Detail, StringComparison.Ordinal);
    }

    // ---- the check itself ----

    /// <summary>
    /// The check is `git ls-remote &lt;url&gt;`, and a github.com URL is handed GH_TOKEN by the same
    /// rule as Fetch; any other host is handed none. What git was handed is read at the origin: the
    /// URL is rewritten to an ssh transport whose command records GH_TOKEN, then serves the request.
    /// </summary>
    [Fact]
    public async Task The_check_of_a_github_url_is_handed_the_token_and_another_hosts_is_not()
    {
        var origin = MakeGitHubRepository("acme", "tokened");
        var seen = Path.Combine(_root, "seen");
        var ssh = Path.Combine(_root, "ssh.sh");
        File.WriteAllText(ssh, $"#!/bin/sh\nprintf '%s' \"${{GH_TOKEN:-none}}\" > '{seen}'\nfor last; do :; done\nexec sh -c \"$last\"\n");
        File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var _ = new EnvironmentScope(
        [
            new("GH_TOKEN", "token-from-the-host"),
            new("GIT_CONFIG_COUNT", "4"),
            new("GIT_CONFIG_KEY_0", $"url.ssh://fake{origin}.insteadOf"),
            new("GIT_CONFIG_VALUE_0", "https://github.com/acme/tokened"),
            new("GIT_CONFIG_KEY_1", "core.sshCommand"),
            new("GIT_CONFIG_VALUE_1", ssh),
            new("GIT_CONFIG_KEY_2", $"url.ssh://fake{origin}.insteadOf"),
            new("GIT_CONFIG_VALUE_2", "https://gitlab.example/acme/tokened"),
            new("GIT_CONFIG_KEY_3", "ssh.variant"),
            new("GIT_CONFIG_VALUE_3", "simple"),
        ]);
        var check = new RemoteRepoCheck(new GitRunner());

        Assert.Null(await check.CheckAsync("https://github.com/acme/tokened", Ct));
        Assert.Equal("token-from-the-host", File.ReadAllText(seen));

        Assert.Null(await check.CheckAsync("https://gitlab.example/acme/tokened", Ct));
        Assert.Equal("none", File.ReadAllText(seen));
    }

    [Fact]
    public async Task The_check_calls_a_missing_repository_not_found_a_network_failure_unreachable_and_an_empty_one_there()
    {
        var check = new RemoteRepoCheck(new GitRunner());

        Assert.Equal(RepoCheckFailures.NotFound, (await check.CheckAsync(Missing, Ct))!.Failure);
        Assert.Equal(RepoCheckFailures.Unreachable, (await check.CheckAsync(Unreachable, Ct))!.Failure);

        Git(_gitHub, "init", "--quiet", "--bare", Path.Combine(_gitHub, "acme", "empty.git"));
        Assert.Null(await check.CheckAsync("https://github.com/acme/empty.git", Ct));
    }

    // ---- the skills ----

    /// <summary>
    /// The Concierge once suggested `https://github.com/&lt;owner&gt;/job-tracker` as an example name,
    /// the person said to use it, and nobody had created it. Its skills now say: no URL is suggested
    /// as though it exists, a code-writing team gets a local repository unless the person names a
    /// remote that exists, and on a missing URL the person is offered a local one or asked to
    /// create the remote - the GitHub create and attach-anyway are theirs.
    /// </summary>
    [Fact]
    public void The_concierge_and_new_team_skills_never_suggest_a_url_and_offer_a_local_repository()
    {
        foreach (var name in new[] { "concierge", "new-team" })
        {
            var body = BuiltInSkills.Find(name)!.Body;
            Assert.Contains("local repository", body, StringComparison.Ordinal);
            Assert.Contains("as though it exists", body, StringComparison.Ordinal);
            Assert.Contains("already exists", body, StringComparison.Ordinal);
            Assert.Contains("create the remote", body, StringComparison.Ordinal);
            Assert.Contains("attaching it anyway", body, StringComparison.Ordinal);
        }

        Assert.Contains("will write code gets a", BuiltInSkills.Find("new-team")!.Body, StringComparison.Ordinal);
    }

    // ---- helpers ----

    // ---- backlog dispatch-to-new: the same path as create ----

    [Fact]
    public async Task Dispatching_to_a_new_team_with_no_repository_gives_it_its_local_repository_and_localRepository_false_gives_none()
    {
        var person = await PersonAsync();

        var dispatched = await DispatchToNewAsync(person, await ReadyItemAsync(person), new
        {
            name = "Dispatched", agent = Agent(), memberAgents = new[] { Agent() },
        });

        Assert.Equal(HttpStatusCode.OK, dispatched.StatusCode);
        var body = await JsonAsync(dispatched);
        var team = body.GetProperty("team").GetString()!;
        Assert.Equal($"local:{team}", body.GetProperty("localRepository").GetProperty("reference").GetString());
        Assert.True(body.GetProperty("localRepository").GetProperty("created").GetBoolean());
        Assert.Equal([$"local:{team}"], Teams.ReposFor(team));
        Assert.True(Directory.Exists(Path.Combine(ClonePath(team, team), ".git")), "the platform did not clone");
        Assert.Equal("main", Teams.DefaultBranchFor(team, team).Branch);
        Assert.Equal(team, Assert.Single(await TenantRowsAsync(TenantActions.LocalRepoCreated)).Subject);
        Assert.Single(await TenantRowsAsync(TenantActions.BacklogItemDispatched));

        var bare = await DispatchToNewAsync(person, await ReadyItemAsync(person), new
        {
            name = "DispatchedBare", agent = Agent(), memberAgents = new[] { Agent() }, localRepository = false,
        });
        Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
        var bareTeam = (await JsonAsync(bare)).GetProperty("team").GetString()!;
        Assert.Empty(Teams.ReposFor(bareTeam));
        Assert.False(Directory.Exists(Bare(bareTeam)));
    }

    [Fact]
    public async Task Dispatching_to_a_new_team_with_a_missing_url_is_refused_with_its_choices_leaving_nothing_and_use_local_ends_with_a_cloned_dispatched_team()
    {
        var person = await PersonAsync();
        _fakeGitHub.CanCreate = true;
        var item = await ReadyItemAsync(person);

        var refused = await DispatchToNewAsync(person, item, new
        {
            name = "JobTracker", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Missing },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await JsonAsync(refused);
        Assert.Equal("repo-check-failed", body.GetProperty("code").GetString());
        Assert.Contains(Missing, body.GetProperty("error").GetString(), StringComparison.Ordinal);
        var entry = Assert.Single(body.GetProperty("repos").EnumerateArray());
        Assert.Equal("not-found", entry.GetProperty("failure").GetString());
        Assert.Equal(["create-on-github", "use-local"], Strings(entry.GetProperty("choices")));
        Assert.Empty(Teams.All());
        Assert.Empty(await _factory.Services.GetRequiredService<ITeamStore>().TeamsAsync(Ct));
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "teams", "JobTracker")));
        Assert.Empty(await TenantRowsAsync(TenantActions.TeamCreated));
        Assert.Empty(await TenantRowsAsync(TenantActions.BacklogItemDispatched));

        var local = await DispatchToNewAsync(person, item, new
        {
            name = "JobTracker", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Missing },
            repoChoices = new Dictionary<string, string> { [Missing] = "use-local" },
        });

        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        var team = (await JsonAsync(local)).GetProperty("team").GetString()!;
        Assert.Equal([$"local:{team}"], Teams.ReposFor(team));
        Assert.True(Directory.Exists(Path.Combine(ClonePath(team, team), ".git")));
        Assert.Single(await TenantRowsAsync(TenantActions.BacklogItemDispatched));
    }

    [Fact]
    public async Task Dispatching_to_a_new_team_on_github_create_makes_it_private_through_the_faked_interface_and_ends_with_a_cloned_team()
    {
        var person = await PersonAsync();
        _fakeGitHub.CanCreate = true;

        var created = await DispatchToNewAsync(person, await ReadyItemAsync(person), new
        {
            name = "JobTracker", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Missing },
            repoChoices = new Dictionary<string, string> { [Missing] = "create-on-github" },
        });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await JsonAsync(created);
        var team = body.GetProperty("team").GetString()!;
        Assert.Contains("create acme/job-tracker private", _fakeGitHub.Calls);
        Assert.Equal([Missing], Strings(body.GetProperty("createdOnGitHub")));
        Assert.Equal([Missing], Teams.ReposFor(team));
        Assert.True(Directory.Exists(Path.Combine(ClonePath(team, "job-tracker"), ".git")));
    }

    [Fact]
    public async Task When_a_dispatched_teams_local_repository_cannot_be_made_no_team_is_created_and_nothing_is_dispatched()
    {
        var person = await PersonAsync();
        var item = await ReadyItemAsync(person);
        File.WriteAllText(Path.Combine(_dataRoot, "repos"), "not a folder");

        var refused = await DispatchToNewAsync(person, item, new { name = "Doomed", agent = Agent(), memberAgents = new[] { Agent() } });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("could not be created", (await JsonAsync(refused)).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(Teams.All());
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "teams", "Doomed")));
        Assert.Empty(await TenantRowsAsync(TenantActions.TeamCreated));
        Assert.Empty(await TenantRowsAsync(TenantActions.BacklogItemDispatched));
    }

    [Fact]
    public async Task An_agent_dispatching_to_a_new_team_is_offered_only_a_local_repository_and_refused_github_create_and_attach_anyway()
    {
        var person = await PersonAsync();
        _fakeGitHub.CanCreate = true;
        var item = await ReadyItemAsync(person);
        var concierge = await ConciergeClientAsync();

        var refused = await DispatchToNewAsync(concierge, item, new
        {
            name = "AgentTeam", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { Missing },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await JsonAsync(refused);
        Assert.Equal(["use-local"], Strings(Assert.Single(body.GetProperty("repos").EnumerateArray()).GetProperty("choices")));
        Assert.Contains("A person can create it on GitHub (private)", body.GetProperty("error").GetString(), StringComparison.Ordinal);

        foreach (var (url, choice) in new[] { (Missing, "create-on-github"), (Unreachable, "attach-anyway") })
        {
            var forbidden = await DispatchToNewAsync(concierge, item, new
            {
                name = "AgentTeam", agent = Agent(), memberAgents = new[] { Agent() }, repos = new[] { url },
                repoChoices = new Dictionary<string, string> { [url] = choice },
            });
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Contains("person's choice", await forbidden.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Empty(Teams.All());
        Assert.DoesNotContain(_fakeGitHub.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
        Assert.Empty(await TenantRowsAsync(TenantActions.BacklogItemDispatched));

        // Its choice - no repos - is a team on its local repository with the item on its board.
        var local = await DispatchToNewAsync(concierge, item, new { name = "AgentTeam", agent = Agent(), memberAgents = new[] { Agent() } });
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.Equal(["local:AgentTeam"], Teams.ReposFor("AgentTeam"));
    }

    private static async Task<long> ReadyItemAsync(HttpClient person)
    {
        var created = await person.PostAsJsonAsync("/api/backlog", new { title = "An item", body = "A spec." }, Ct);
        created.EnsureSuccessStatusCode();
        var id = (await JsonAsync(created)).GetProperty("id").GetInt64();
        (await person.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "ready" }, Ct)).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<HttpResponseMessage> DispatchToNewAsync(HttpClient client, long item, object body) =>
        await client.PostAsJsonAsync($"/api/backlog/{item}/dispatch-to-new", body, Ct);

    private string MakeGitHubRepository(string owner, string name)
    {
        var bare = Path.Combine(_gitHub, owner, name + ".git");
        FakeGitHub.MakeWithReadme(bare);
        // `insteadOf` maps `https://github.com/acme/exists` to the folder without `.git` as well.
        if (!Directory.Exists(Path.Combine(_gitHub, owner, name)))
        {
            Directory.CreateSymbolicLink(Path.Combine(_gitHub, owner, name), bare);
        }

        return bare;
    }

    private string Agent() =>
        _factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;

    private string ClonePath(string team, string repo) =>
        Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), repo, "main");

    private static async Task<HttpResponseMessage> CreateTeamAsync(HttpClient client, object body) =>
        await client.PostAsJsonAsync("/api/teams", body, Ct);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private async Task<IReadOnlyList<TenantEvent>> TenantRowsAsync(string action) =>
        [.. (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(null, ITenantLog.MaxTake, Ct)).Events.Where(e => e.Action == action)];

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

    private async Task<HttpClient> ConciergeClientAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", await ConciergeKeyAsync());
        return client;
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

    /// <summary>GitHub, answered locally: a create makes the bare repository the rewritten URL names,
    /// with a README on main, as <c>--add-readme</c> does. Every call is written down.</summary>
    private sealed class FakeGitHub(string root) : IGitHubContributor
    {
        public List<string> Calls { get; } = [];

        public bool CanCreate { get; set; }

        public Task<GitHubAnswer<bool>> CanCreateRepositoryAsync(string owner, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"can-create {owner}");
            return Task.FromResult(GitHubAnswer<bool>.Of(CanCreate));
        }

        public Task<GitHubAnswer<string>> CreatePrivateRepositoryAsync(string owner, string name, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"create {owner}/{name} private");
            var bare = Path.Combine(root, owner, name);
            MakeWithReadme(bare);
            return Task.FromResult(GitHubAnswer<string>.Of($"https://github.com/{owner}/{name}"));
        }

        public static void MakeWithReadme(string bare)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
            Git(Path.GetDirectoryName(bare)!, "init", "--quiet", "--bare", "-b", "main", bare);
            var seed = bare + ".seed";
            Git(Path.GetDirectoryName(bare)!, "clone", "--quiet", bare, seed);
            File.WriteAllText(Path.Combine(seed, "README.md"), "readme\n");
            Git(seed, "add", ".");
            Git(seed, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "commit", "--quiet", "-m", "Initial commit");
            Git(seed, "push", "--quiet", "origin", "main");
        }

        public Task<GitHubAnswer<ForkInfo>> ForkAsync(string upstreamUrl, string? organisation, CancellationToken ct) =>
            throw new InvalidOperationException("not asked here");

        public Task<GitHubAnswer<PullRequestInfo?>> FindOpenAsync(string upstreamUrl, string forkOwner, string branch, CancellationToken ct) =>
            throw new InvalidOperationException("not asked here");

        public Task<GitHubAnswer<PullRequestInfo>> CreateAsync(
            string upstreamUrl, string forkOwner, string branch, string baseBranch, string title, string body, CancellationToken ct) =>
            throw new InvalidOperationException("not asked here");

        public Task<GitHubAnswer<PullRequestInfo>> ReadAsync(string upstreamUrl, int number, CancellationToken ct) =>
            throw new InvalidOperationException("not asked here");
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _environment.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
