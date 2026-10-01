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
/// RESET REPOSITORIES, on the real Host through the reset route, with real git against a local bare
/// origin. The origin's HEAD names `trunk` and it ALSO carries a `main`, so a reset that guessed
/// `main` would visibly act on the wrong branch - and every test checks that neither moved, on the
/// clone or on origin.
/// </summary>
public sealed class TeamResetRepositoriesTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";
    private const string Repo = "Widget";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-reset-repos-").FullName;
    private readonly string _origin;
    private readonly string _seed;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TeamResetRepositoriesTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _seed = Path.Combine(_root, "seed");
        Git(_root, "init", "--bare", "-b", "trunk", _origin);
        Git(_root, "clone", _origin, _seed);
        Git(_seed, "checkout", "-b", "trunk");
        File.WriteAllText(Path.Combine(_seed, "README.md"), "widget\n");
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
            }));
    }

    [Fact]
    public async Task A_ticked_members_clean_trees_and_merged_branches_go_and_dirty_trees_and_unpushed_branches_are_kept_and_named()
    {
        var team = await TeamAsync("Alpha");
        var clone = ClonePath(team);
        var before = DefaultBranchShas(clone);

        // Dev: a clean tree whose branch is pushed, a tree with an uncommitted edit, a branch with a
        // commit on no remote, and a branch with nothing on it.
        var clean = Tree(team, "Dev", "c1", "dev/c1");
        Commit(clean, "c1 work", "c1.txt");
        Git(clean, "push", "origin", "dev/c1");

        var dirty = Tree(team, "Dev", "c2", "dev/c2");
        File.WriteAllText(Path.Combine(dirty, "wip.txt"), "not committed\n");

        Git(clone, "branch", "dev/unpushed", "trunk");
        var side = Tree(team, "Dev", "tmp", "dev/tmp");
        Git(side, "checkout", "dev/unpushed");
        Commit(side, "only here", "only.txt");
        Git(side, "checkout", "--detach");
        Git(clone, "worktree", "remove", side);

        Git(clone, "branch", "dev/merged", "trunk");

        // Writer is not ticked: its tree and branch stay whatever state they are in.
        var writers = Tree(team, "Writer", "c3", "writer/c3");

        // A team branch, so a reset of one member visibly leaves it alone.
        Git(clone, "branch", $"team/{team}", "trunk");
        var teamBefore = RevParse(clone, $"refs/heads/team/{team}");

        var person = await PersonAsync();
        var response = await ResetAsync(person, team, ["Dev"]);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var repos = JsonDocument.Parse(body).RootElement.GetProperty("repositories");

        // The clean tree and its pushed branch, and the empty branch, went.
        Assert.False(Directory.Exists(clean));
        Assert.Null(TryRevParse(clone, "refs/heads/dev/c1"));
        Assert.Null(TryRevParse(clone, "refs/heads/dev/merged"));
        Assert.Contains(clean, Names(repos, "worktreesRemoved"));
        Assert.Contains("dev/c1", Names(repos, "branchesDeleted"));
        Assert.Contains("dev/merged", Names(repos, "branchesDeleted"));

        // The tree with an uncommitted edit, and the branch holding a commit on no remote, stayed and
        // are named with the reason.
        Assert.True(File.Exists(Path.Combine(dirty, "wip.txt")));
        Assert.NotNull(TryRevParse(clone, "refs/heads/dev/unpushed"));
        Assert.Contains("git worktree remove refused it", Reason(repos, "worktreesKept", dirty), StringComparison.Ordinal);
        Assert.Contains("on no remote", Reason(repos, "branchesKept", "dev/unpushed"), StringComparison.Ordinal);
        Assert.Contains("checked out in", Reason(repos, "branchesKept", "dev/c2"), StringComparison.Ordinal);

        // Named on the team feed too, by the member they belong to.
        var log = _factory.Services.GetRequiredService<IMessageLog>();
        var left = await log.ReadAfterAsync(0, [MessageTypes.RepoWorktreeLeft], 50, Ct);
        Assert.Contains(left, row => row.Source == $"{team}/Dev" && row.Payload.Contains(Path.GetFileName(dirty), StringComparison.Ordinal));
        var kept = await log.ReadAfterAsync(0, [MessageTypes.RepoBranchKept], 50, Ct);
        Assert.Contains(kept, row => row.Source == $"{team}/Dev" && row.Payload.Contains("dev/unpushed", StringComparison.Ordinal));

        // And in the tenant log: the row before the act naming what may go, the row after naming
        // what went and what stayed, with the reasons.
        var tenant = _factory.Services.GetRequiredService<ITenantLog>();
        var planned = await tenant.FindLatestAsync(TenantActions.TeamResetRepositories, team, Ct);
        Assert.NotNull(planned);
        Assert.Contains(Path.GetFileName(clean), planned.Detail, StringComparison.Ordinal);
        var done = await tenant.FindLatestAsync(TenantActions.TeamReset, team, Ct);
        Assert.NotNull(done);
        Assert.True(planned.Seq < done.Seq);
        var detail = JsonDocument.Parse(done.Detail!).RootElement.GetProperty("repositories");
        Assert.Contains(dirty, Names(detail, "worktreesKept"));
        Assert.Contains(clean, Names(detail, "worktreesRemoved"));
        Assert.Contains("on no remote", Reason(detail, "branchesKept", "dev/unpushed"), StringComparison.Ordinal);

        // Nobody else's, and not the team branch: not every member was ticked.
        Assert.True(Directory.Exists(writers));
        Assert.NotNull(TryRevParse(clone, "refs/heads/writer/c3"));
        Assert.Equal(teamBefore, RevParse(clone, $"refs/heads/team/{team}"));
        Assert.Equal(0, repos.GetProperty("teamBranchReset").GetArrayLength());

        Assert.Equal(before, DefaultBranchShas(clone));
    }

    [Fact]
    public async Task With_every_member_ticked_the_team_branch_is_reset_and_kept_on_origin_while_it_holds_work_the_default_branch_lacks()
    {
        var team = await TeamAsync("Beta");
        var clone = ClonePath(team);
        var before = DefaultBranchShas(clone);

        // The team branch holds pushed work trunk does not have.
        Git(clone, "branch", $"team/{team}", "trunk");
        var work = Tree(team, "Dev", "c1", "dev/c1");
        Commit(work, "team work", "team.txt");
        Git(work, "push", "origin", "dev/c1");
        Git(clone, "update-ref", $"refs/heads/team/{team}", RevParse(work, "HEAD"));
        Git(clone, "push", "origin", $"team/{team}");
        var onOrigin = RevParse(_origin, $"refs/heads/team/{team}");

        var person = await PersonAsync();
        var response = await ResetAsync(person, team, ["Manager", "Dev", "Writer"]);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var repos = JsonDocument.Parse(body).RootElement.GetProperty("repositories");

        // Locally reset to the STORED default branch - trunk, never main.
        Assert.Equal(RevParse(clone, "refs/heads/trunk"), RevParse(clone, $"refs/heads/team/{team}"));
        Assert.Contains($"team/{team}", Names(repos, "teamBranchReset"));

        // On origin it holds work trunk lacks, so it stays, named.
        Assert.Equal(onOrigin, RevParse(_origin, $"refs/heads/team/{team}"));
        Assert.Contains("does not have", Reason(repos, "teamBranchKept", $"origin/team/{team}"), StringComparison.Ordinal);

        Assert.Equal(before, DefaultBranchShas(clone));
    }

    [Fact]
    public async Task With_every_member_ticked_a_team_branch_holding_nothing_new_is_deleted_on_origin_and_the_default_branch_never_moves()
    {
        var team = await TeamAsync("Gamma");
        var clone = ClonePath(team);
        var before = DefaultBranchShas(clone);

        Git(clone, "branch", $"team/{team}", "trunk");
        Git(clone, "push", "origin", $"team/{team}");

        var person = await PersonAsync();
        var response = await ResetAsync(person, team, ["Manager", "Dev", "Writer"]);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var repos = JsonDocument.Parse(body).RootElement.GetProperty("repositories");

        Assert.Null(TryRevParse(_origin, $"refs/heads/team/{team}"));
        Assert.Contains($"origin/team/{team}", Names(repos, "teamBranchReset"));

        Assert.Equal(before, DefaultBranchShas(clone));
    }

    [Fact]
    public async Task A_team_branch_that_gained_work_on_origin_after_the_clone_last_fetched_is_kept_on_origin_and_named()
    {
        var team = await TeamAsync("Zeta");
        var clone = ClonePath(team);
        var before = DefaultBranchShas(clone);

        Git(clone, "branch", $"team/{team}", "trunk");
        Git(clone, "push", "origin", $"team/{team}");

        // Another clone pushes new work to origin's team branch. The team's clone has not fetched
        // since, so its refs/remotes/origin/team/<id> still says "nothing trunk lacks".
        Git(_seed, "fetch", "origin");
        Git(_seed, "checkout", "-b", $"team/{team}", $"origin/team/{team}");
        Commit(_seed, "pushed from elsewhere", "elsewhere.txt");
        Git(_seed, "push", "origin", $"team/{team}");
        var pushed = RevParse(_seed, "HEAD");
        Assert.NotEqual(pushed, RevParse(clone, $"refs/remotes/origin/team/{team}"));

        var person = await PersonAsync();
        var response = await ResetAsync(person, team, ["Manager", "Dev", "Writer"]);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var repos = JsonDocument.Parse(body).RootElement.GetProperty("repositories");

        // The commit is still on origin, and the branch is named as kept, with the reason.
        Assert.Equal(pushed, TryRevParse(_origin, $"refs/heads/team/{team}"));
        Assert.DoesNotContain($"origin/team/{team}", Names(repos, "teamBranchReset"));
        Assert.Contains("does not have", Reason(repos, "teamBranchKept", $"origin/team/{team}"), StringComparison.Ordinal);

        var done = await _factory.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.TeamReset, team, Ct);
        Assert.NotNull(done);
        var detail = JsonDocument.Parse(done.Detail!).RootElement.GetProperty("repositories");
        Assert.Contains("does not have", Reason(detail, "teamBranchKept", $"origin/team/{team}"), StringComparison.Ordinal);

        Assert.Equal(before, DefaultBranchShas(clone));
    }

    [Fact]
    public async Task The_origin_delete_is_leased_on_the_sha_compared_so_a_push_since_refuses_it()
    {
        // A push that lands between the compare and the delete: the delete names the sha compared,
        // and origin has moved on.
        var clone = Path.Combine(_root, "racer");
        Git(_root, "clone", _origin, clone);
        Git(clone, "checkout", "-b", "team/Eta", "origin/trunk");
        Git(clone, "push", "origin", "team/Eta");
        var compared = RevParse(clone, "refs/remotes/origin/team/Eta");

        Commit(_seed, "landed since", "since.txt");
        Git(_seed, "push", "origin", "HEAD:refs/heads/team/Eta");
        var landed = RevParse(_seed, "HEAD");

        var git = new GitRunner();
        var refused = await git.DeleteOriginBranchIfAtAsync(clone, "team/Eta", compared, Ct);

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Equal(landed, TryRevParse(_origin, "refs/heads/team/Eta"));

        // At the sha origin really holds, it deletes.
        var deleted = await git.DeleteOriginBranchIfAtAsync(clone, "team/Eta", landed, Ct);
        Assert.True(deleted.ExitCode == 0, deleted.Stderr);
        Assert.Null(TryRevParse(_origin, "refs/heads/team/Eta"));
    }

    [Fact]
    public async Task A_team_branch_that_moved_on_origin_after_the_compare_is_kept_by_the_lease_and_named()
    {
        var team = await TeamAsync("Theta");
        var clone = ClonePath(team);
        var before = DefaultBranchShas(clone);

        Git(clone, "branch", $"team/{team}", "trunk");
        Git(clone, "push", "origin", $"team/{team}");

        // The reset reads a view of origin in which the team branch holds nothing trunk lacks; by the
        // time it deletes, origin holds a push that landed since. The clone fetches and ls-remotes a
        // copy of origin taken now, and pushes (so deletes) to origin itself.
        var view = Path.Combine(_root, "view.git");
        Git(_root, "clone", "--bare", _origin, view);
        Git(_seed, "fetch", "origin");
        Git(_seed, "checkout", "-b", $"team/{team}", $"origin/team/{team}");
        Commit(_seed, "landed after the compare", "after.txt");
        Git(_seed, "push", "origin", $"team/{team}");
        var landed = RevParse(_seed, "HEAD");
        Git(clone, "remote", "set-url", "origin", view);
        Git(clone, "remote", "set-url", "--push", "origin", _origin);

        var person = await PersonAsync();
        var response = await ResetAsync(person, team, ["Manager", "Dev", "Writer"]);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var repos = JsonDocument.Parse(body).RootElement.GetProperty("repositories");

        // The lease refused the delete: the commit that landed is still on origin, and the answer
        // names the branch as kept because it changed after it was compared.
        Assert.Equal(landed, TryRevParse(_origin, $"refs/heads/team/{team}"));
        Assert.DoesNotContain($"origin/team/{team}", Names(repos, "teamBranchReset"));
        Assert.Contains("changed on origin after it was compared", Reason(repos, "teamBranchKept", $"origin/team/{team}"), StringComparison.Ordinal);

        var done = await _factory.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.TeamReset, team, Ct);
        Assert.NotNull(done);
        var detail = JsonDocument.Parse(done.Detail!).RootElement.GetProperty("repositories");
        Assert.Contains("changed on origin after it was compared", Reason(detail, "teamBranchKept", $"origin/team/{team}"), StringComparison.Ordinal);

        Assert.Equal(before, DefaultBranchShas(clone));
    }

    [Fact]
    public async Task A_team_branch_origin_could_not_be_asked_about_is_kept_on_origin_and_named()
    {
        var team = await TeamAsync("Iota");
        var clone = ClonePath(team);
        var before = DefaultBranchShas(clone);

        Git(clone, "branch", $"team/{team}", "trunk");
        Git(clone, "push", "origin", $"team/{team}");
        var onOrigin = RevParse(_origin, $"refs/heads/team/{team}");

        // Origin cannot be reached: git ls-remote fails. The clone's own view still says the branch
        // holds nothing trunk lacks, which a reset must not act on.
        Git(clone, "remote", "set-url", "origin", Path.Combine(_root, "unreachable.git"));

        var person = await PersonAsync();
        var response = await ResetAsync(person, team, ["Manager", "Dev", "Writer"]);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var repos = JsonDocument.Parse(body).RootElement.GetProperty("repositories");

        Assert.Equal(onOrigin, TryRevParse(_origin, $"refs/heads/team/{team}"));
        Assert.DoesNotContain($"origin/team/{team}", Names(repos, "teamBranchReset"));
        Assert.Contains("Could not ask origin for it", Reason(repos, "teamBranchKept", $"origin/team/{team}"), StringComparison.Ordinal);

        var done = await _factory.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.TeamReset, team, Ct);
        Assert.NotNull(done);
        var detail = JsonDocument.Parse(done.Detail!).RootElement.GetProperty("repositories");
        Assert.Contains("Could not ask origin for it", Reason(detail, "teamBranchKept", $"origin/team/{team}"), StringComparison.Ordinal);

        Assert.Equal(before, DefaultBranchShas(clone));
    }

    [Fact]
    public async Task A_team_branch_that_could_not_be_fetched_is_kept_on_origin_and_named()
    {
        var team = await TeamAsync("Kappa");
        var clone = ClonePath(team);
        var before = DefaultBranchShas(clone);

        Git(clone, "branch", $"team/{team}", "trunk");
        Git(clone, "push", "origin", $"team/{team}");

        // Origin moves on, and the clone cannot take the new sha: its remote-tracking ref is locked,
        // so git ls-remote answers but the fetch fails. The clone's stale view says "nothing new".
        Git(_seed, "fetch", "origin");
        Git(_seed, "checkout", "-b", $"team/{team}", $"origin/team/{team}");
        Commit(_seed, "pushed from elsewhere", "elsewhere.txt");
        Git(_seed, "push", "origin", $"team/{team}");
        var pushed = RevParse(_seed, "HEAD");
        var tracking = Path.Combine(clone, ".git", "refs", "remotes", "origin", "team", team);
        Assert.True(File.Exists(tracking), "the clone's view of the team branch is not a loose ref");
        File.WriteAllText(tracking + ".lock", "");

        var person = await PersonAsync();
        var response = await ResetAsync(person, team, ["Manager", "Dev", "Writer"]);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var repos = JsonDocument.Parse(body).RootElement.GetProperty("repositories");

        Assert.Equal(pushed, TryRevParse(_origin, $"refs/heads/team/{team}"));
        Assert.DoesNotContain($"origin/team/{team}", Names(repos, "teamBranchReset"));
        Assert.Contains("Could not fetch it from origin", Reason(repos, "teamBranchKept", $"origin/team/{team}"), StringComparison.Ordinal);

        var done = await _factory.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.TeamReset, team, Ct);
        Assert.NotNull(done);
        var detail = JsonDocument.Parse(done.Detail!).RootElement.GetProperty("repositories");
        Assert.Contains("Could not fetch it from origin", Reason(detail, "teamBranchKept", $"origin/team/{team}"), StringComparison.Ordinal);

        Assert.Equal(before, DefaultBranchShas(clone));
    }

    [Fact]
    public async Task While_the_default_branch_is_not_known_resetting_every_members_repositories_is_refused_and_nothing_changes()
    {
        var team = await TeamAsync("Delta");
        var clone = ClonePath(team);
        var person = await PersonAsync();

        var clean = Tree(team, "Dev", "c1", "dev/c1");
        Git(clone, "branch", $"team/{team}", "trunk");
        Git(clone, "push", "origin", $"team/{team}");

        // origin's HEAD now names a branch that does not exist, so a Fetch stores "not known".
        Git(_origin, "symbolic-ref", "HEAD", "refs/heads/nowhere");
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "fetch")).StatusCode);
        Assert.Null(_factory.Services.GetRequiredService<TeamRegistry>().DefaultBranchFor(team, Repo).Branch);
        var before = DefaultBranchShas(clone);

        var response = await ResetAsync(person, team, ["Manager", "Dev", "Writer"]);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(DefaultBranchNotKnown.Message(Repo), JsonDocument.Parse(body).RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);

        // Nothing else changed: the tree, the branches, the team branch on both sides - and no reset
        // was recorded, because none happened.
        Assert.True(Directory.Exists(clean));
        Assert.NotNull(TryRevParse(clone, "refs/heads/dev/c1"));
        Assert.NotNull(TryRevParse(clone, $"refs/heads/team/{team}"));
        Assert.NotNull(TryRevParse(_origin, $"refs/heads/team/{team}"));
        Assert.Equal(before, DefaultBranchShas(clone));
        Assert.Null(await _factory.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.TeamReset, team, Ct));
    }

    [Fact]
    public async Task No_row_no_reset()
    {
        var team = await TeamAsync("Epsilon");
        var clean = Tree(team, "Dev", "c1", "dev/c1");

        var reset = _factory.Services.GetRequiredService<TeamReset>();

        await Assert.ThrowsAsync<ResetNotRecordedException>(() => reset.ResetAsync(
            team, new TeamResetOptions(["Dev"], ResetRepositories: true), Ct,
            (_, _) => throw new InvalidOperationException("the tenant log is down")));

        Assert.True(Directory.Exists(clean));
        Assert.NotNull(TryRevParse(ClonePath(team), "refs/heads/dev/c1"));
    }

    [Fact]
    public async Task The_preview_lists_each_members_trees_and_branches_and_the_team_branch()
    {
        var team = await TeamAsync("Zeta");
        var tree = Tree(team, "Dev", "c1", "dev/c1");
        Git(ClonePath(team), "branch", "writer/idea", "trunk");

        var person = await PersonAsync();
        var preview = await person.GetFromJsonAsync<JsonElement>($"/api/teams/{team}/reset/repositories", Ct);

        Assert.Contains(preview.GetProperty("worktrees").EnumerateArray(),
            item => item.GetProperty("name").GetString() == tree && item.GetProperty("member").GetString() == "Dev");
        Assert.Contains(preview.GetProperty("branches").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "writer/idea" && item.GetProperty("member").GetString() == "Writer");
        Assert.DoesNotContain(preview.GetProperty("branches").EnumerateArray(),
            item => item.GetProperty("name").GetString() is "trunk" or "main");
        Assert.Equal($"team/{team}", preview.GetProperty("teamBranch").GetString());
        Assert.Equal(0, preview.GetProperty("defaultBranchNotKnown").GetArrayLength());
    }

    // --- helpers

    private async Task<string> TeamAsync(string name)
    {
        var services = _factory.Services;
        var registry = services.GetRequiredService<TeamRegistry>();
        var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var team = (await registry.CreateAsync(name, agent, memberAgent: agent, ct: Ct)).Id;
        await registry.AddContainerAsync(team, "Dev", agent, "", [], ct: Ct);
        await registry.AddContainerAsync(team, "Writer", agent, "", [], ct: Ct);
        await registry.SetReposAsync(team, [$"https://github.com/example/{Repo}.git"], Ct);

        var clone = ClonePath(team);
        Assert.True(Directory.Exists(Path.Combine(clone, ".git")), "the platform did not clone");
        Git(clone, "config", "user.name", "t");
        Git(clone, "config", "user.email", "t@example.invalid");
        Assert.Equal("trunk", registry.DefaultBranchFor(team, Repo).Branch);

        // QUIET BEFORE ANY LOCAL-ONLY STATE IS MADE. A run ending publishes every branch that is on no
        // remote, so a Manager run woken by the repository being attached would push the very commit a
        // test needs to be on no remote. Paused, nothing new starts; idle twice in a row, nothing is
        // in flight.
        var person = await PersonAsync();
        (await person.PostAsync($"/api/teams/{team}/pause", null, Ct)).EnsureSuccessStatusCode();

        var host = services.GetRequiredService<ContainerHost>();
        var pending = services.GetRequiredService<IPendingDeliveries>();
        var quiet = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (quiet < 2 && DateTime.UtcNow < deadline)
        {
            var busy = false;
            foreach (var id in registry.ContainerIdsOf(team))
            {
                var member = host.Find(id);
                busy |= member is { State: ContainerState.Running } or { QueueDepth: > 0 }
                    || (await pending.ForAsync(id, Ct)).Count > 0;
            }

            quiet = busy ? 0 : quiet + 1;
            await Task.Delay(200, Ct);
        }

        Assert.True(quiet >= 2, "the team never went quiet");
        return team;
    }

    private string ClonePath(string team) =>
        Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), Repo, "main");

    /// <summary>A member's per-card tree, cut from the clone as the worktrees skill says.</summary>
    private string Tree(string team, string member, string key, string branch)
    {
        var path = Path.Combine(
            _factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), Repo, Worktrees.DirectoryName(member, key));
        Git(ClonePath(team), "worktree", "add", path, "-b", branch, "trunk");
        return path;
    }

    private (string?, string?, string?, string?) DefaultBranchShas(string clone) =>
        (TryRevParse(clone, "refs/heads/trunk"), TryRevParse(clone, "refs/heads/main"),
            TryRevParse(_origin, "refs/heads/trunk"), TryRevParse(_origin, "refs/heads/main"));

    private static async Task<HttpResponseMessage> ResetAsync(HttpClient person, string team, string[] members)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await person.PostAsJsonAsync(
                $"/api/teams/{team}/reset",
                new { members, forgetHistory = false, resetRepositories = true }, Ct);

            // A Manager woken by the repository being attached may still be finishing.
            if (response.StatusCode != HttpStatusCode.Conflict || DateTime.UtcNow > deadline) return response;
            var text = await response.Content.ReadAsStringAsync(Ct);
            if (!text.Contains("still working", StringComparison.Ordinal)) return response;
            await Task.Delay(100, Ct);
        }
    }

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

    private static IReadOnlyList<string?> Names(JsonElement repositories, string list) =>
        [.. repositories.GetProperty(list).EnumerateArray().Select(item => item.GetProperty("name").GetString())];

    private static string Reason(JsonElement repositories, string list, string name) =>
        repositories.GetProperty(list).EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name)
            .GetProperty("reason").GetString() ?? "";

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

    private static void Commit(string repo, string message, string? file = null)
    {
        if (file is not null) File.WriteAllText(Path.Combine(repo, file), message + "\n");
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

    /// <summary>The product's own <see cref="RepoClone"/>, pointed at the local origin whatever URL the
    /// team names - so the clone-time read of origin's HEAD is the real one.</summary>
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
