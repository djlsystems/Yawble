using System.Diagnostics;
using System.Net;
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
/// A team works on one of the instance's local repositories (<c>local:&lt;name&gt;</c>) the way it
/// works on a remote: the product's own <see cref="RepoClone"/> clones the bare repository under
/// <c>&lt;dataRoot&gt;/repos</c>, and every Git action - Fetch, Bring current, Push, Merge to main,
/// Delete remote branch - lands in it with no network and no <c>GH_TOKEN</c>. Nothing here fakes
/// the git: only the agent is fake.
/// </summary>
public sealed class LocalReposTests : IAsyncDisposable
{
    private const string Password = "correct horse battery";

    private readonly string _root = Directory.CreateTempSubdirectory("harness-local-repos-").FullName;
    private readonly string _dataRoot;
    private readonly WebApplicationFactory<Program> _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LocalReposTests()
    {
        _dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));
    }

    private string Bare(string name) => Path.Combine(_dataRoot, "repos", name + ".git");

    [Fact]
    public async Task A_new_local_repository_has_main_and_one_empty_commit_and_is_listed_and_logged()
    {
        var person = await PersonAsync();

        var created = await person.PostAsJsonAsync("/api/local-repos", new { name = "widget" }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var bare = Bare("widget");
        Assert.Equal("true", Run(bare, "rev-parse", "--is-bare-repository").Stdout.Trim());
        Assert.Equal("refs/heads/main", Run(bare, "symbolic-ref", "HEAD").Stdout.Trim());
        Assert.Equal("1", Run(bare, "rev-list", "--count", "main").Stdout.Trim());
        Assert.Equal("4b825dc642cb6eb9a060e54bf8d69288fbee4904", Run(bare, "rev-parse", "main^{tree}").Stdout.Trim());
        // Readable by the agent's group, never writable by it: git keeps these modes on every write.
        Assert.Equal("0640", Run(bare, "config", "core.sharedRepository").Stdout.Trim());

        var listed = await person.GetFromJsonAsync<JsonElement>("/api/local-repos", Ct);
        var entry = Assert.Single(listed.EnumerateArray());
        Assert.Equal("widget", entry.GetProperty("name").GetString());
        Assert.Equal("local:widget", entry.GetProperty("reference").GetString());
        Assert.Equal("main", entry.GetProperty("defaultBranch").GetString());
        Assert.Equal("Initial commit", entry.GetProperty("lastCommit").GetProperty("subject").GetString());
        Assert.True(entry.GetProperty("sizeBytes").GetInt64() > 0);
        Assert.Empty(entry.GetProperty("teams").EnumerateArray());

        var row = Assert.Single(await TenantRowsAsync(TenantActions.LocalRepoCreated));
        Assert.Equal("widget", row.Subject);
        Assert.Equal("person@example.test", row.ActorEmail);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a b")]
    [InlineData("widget.git")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("-x")]
    public async Task An_illegal_name_is_refused_naming_it_and_nothing_is_made(string name)
    {
        var person = await PersonAsync();

        var response = await person.PostAsJsonAsync("/api/local-repos", new { name }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"'{name}'", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "escape.git")));
        Assert.Empty(Directory.Exists(Path.Combine(_dataRoot, "repos"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(_dataRoot, "repos"))
            : []);
    }

    [Fact]
    public async Task A_second_repository_of_the_same_name_in_any_case_is_refused()
    {
        var person = await PersonAsync();
        Assert.Equal(HttpStatusCode.Created, (await person.PostAsJsonAsync("/api/local-repos", new { name = "widget" }, Ct)).StatusCode);

        var again = await person.PostAsJsonAsync("/api/local-repos", new { name = "Widget" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("'widget' already exists", await again.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("local:nope", "names no local repository")]
    [InlineData("local:../../keys", "is not a local repository name")]
    [InlineData("local:", "is not a local repository name")]
    [InlineData("local:a/b", "is not a local repository name")]
    public async Task A_team_naming_an_unknown_or_illegal_local_repository_is_refused_naming_it(string reference, string reason)
    {
        var person = await PersonAsync();
        var team = await TeamAsync("Refused");

        var response = await person.PutAsJsonAsync($"/api/teams/{team}/repos", new[] { reference }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains(reference, text, StringComparison.Ordinal);
        Assert.Contains(reason, text, StringComparison.Ordinal);
        Assert.Empty(_factory.Services.GetRequiredService<TeamRegistry>().ReposFor(team));
    }

    [Fact]
    public async Task A_new_team_on_a_local_repository_clones_it_with_the_bare_repository_as_origin()
    {
        var person = await PersonAsync();
        await CreateAsync(person, "widget");

        var response = await person.PostAsJsonAsync("/api/teams", new
        {
            name = "Local Team",
            agent = HeadlessAgent(),
            memberAgent = HeadlessAgent(),
            repos = new[] { "local:widget" },
        }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var team = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString()!;

        var clone = ClonePath(team, "widget");
        Assert.True(Directory.Exists(Path.Combine(clone, ".git")), "the platform did not clone");
        Assert.Equal(Bare("widget"), Run(clone, "remote", "get-url", "origin").Stdout.Trim());
        Assert.Equal("main", _factory.Services.GetRequiredService<TeamRegistry>().DefaultBranchFor(team, "widget").Branch);
        // Copied, never hard-linked: an object file shared with the clone would be handed to the
        // agent by the next ownership pass over the team's folder.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Bare("widget"), "objects"), "*", SearchOption.AllDirectories))
        {
            Assert.Equal("1", Run(_root, "stat", "-c", "%h", file).Stdout.Trim());
        }

        var listed = await person.GetFromJsonAsync<JsonElement>("/api/local-repos", Ct);
        Assert.Equal([team], Assert.Single(listed.EnumerateArray()).GetProperty("teams").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Push_merge_to_main_fetch_bring_current_and_delete_remote_branch_all_land_in_the_bare_repository()
    {
        var person = await PersonAsync();
        await CreateAsync(person, "widget");
        var team = await TeamAsync("Worker");
        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync($"/api/teams/{team}/repos", new[] { "local:widget" }, Ct)).StatusCode);
        var clone = ClonePath(team, "widget");
        var bare = Bare("widget");
        Git(clone, "config", "user.name", "t");
        Git(clone, "config", "user.email", "t@example.invalid");

        // THE HOST'S RECORD DECIDES WHERE A PUSH GOES, NOT THE CLONE'S CONFIG, which the agent
        // writes: a push URL pointed at a decoy is never used. (It also shows the push did not go
        // through `git push` as the agent, which could not write the bare repository in production.)
        var decoy = Path.Combine(_root, "decoy.git");
        Git(_root, "init", "--quiet", "--bare", decoy);
        Git(clone, "remote", "set-url", "--push", "origin", decoy);

        // A member's work on the team branch.
        Git(clone, "branch", $"team/{team}", "main");
        Git(clone, "checkout", $"team/{team}");
        File.WriteAllText(Path.Combine(clone, "work.txt"), "work\n");
        Commit(clone, "work");
        var work = RevParse(clone, "HEAD");
        Git(clone, "checkout", "main");

        var pushed = await ActAsync(person, team, "widget", "push");
        Assert.True(pushed.StatusCode == HttpStatusCode.OK, await pushed.Content.ReadAsStringAsync(Ct));
        Assert.Equal(work, RevParse(bare, $"refs/heads/team/{team}"));
        Assert.Equal(work, RevParse(clone, $"refs/remotes/origin/team/{team}"));

        var merged = await ActAsync(person, team, "widget", "merge-to-main");
        Assert.True(merged.StatusCode == HttpStatusCode.OK, await merged.Content.ReadAsStringAsync(Ct));
        Assert.Equal(work, RevParse(bare, "refs/heads/main"));
        Assert.Equal(work, RevParse(clone, "refs/heads/main"));
        Assert.Equal(string.Empty, Run(decoy, "for-each-ref").Stdout);

        var deleted = await ActAsync(person, team, "widget", "delete-remote-branch");
        Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync(Ct));
        Assert.Null(TryRevParse(bare, $"refs/heads/team/{team}"));

        // Somebody else's commit on the bare repository's main: Fetch sees it, Bring current takes it.
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", "--no-local", bare, other);
        File.WriteAllText(Path.Combine(other, "other.txt"), "other\n");
        Commit(other, "other");
        Git(other, "push", "origin", "main");
        var otherTip = RevParse(other, "HEAD");

        var fetched = await ActAsync(person, team, "widget", "fetch");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.True(JsonDocument.Parse(await fetched.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(otherTip, RevParse(clone, "refs/remotes/origin/main"));
        Assert.Equal("main", _factory.Services.GetRequiredService<TeamRegistry>().DefaultBranchFor(team, "widget").FromRemote);

        var current = await ActAsync(person, team, "widget", "bring-current");
        Assert.True(current.StatusCode == HttpStatusCode.OK, await current.Content.ReadAsStringAsync(Ct));
        Assert.Equal(otherTip, RevParse(clone, "refs/heads/main"));
    }

    [Fact]
    public async Task The_default_branch_is_read_from_the_local_origin_and_never_assumed()
    {
        var person = await PersonAsync();
        await CreateAsync(person, "widget");
        var bare = Bare("widget");
        Git(bare, "branch", "trunk", "main");
        Git(bare, "symbolic-ref", "HEAD", "refs/heads/trunk");
        var team = await TeamAsync("Trunk");

        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync($"/api/teams/{team}/repos", new[] { "local:widget" }, Ct)).StatusCode);

        Assert.Equal("trunk", _factory.Services.GetRequiredService<TeamRegistry>().DefaultBranchFor(team, "widget").FromRemote);

        // HEAD naming nothing: a Fetch stores not known, and Merge to main refuses and changes nothing.
        Git(bare, "symbolic-ref", "HEAD", "refs/heads/nowhere");
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(person, team, "widget", "fetch")).StatusCode);
        Assert.Null(_factory.Services.GetRequiredService<TeamRegistry>().DefaultBranchFor(team, "widget").Branch);
        var mainBefore = RevParse(bare, "refs/heads/main");
        var refused = await ActAsync(person, team, "widget", "merge-to-main");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("default branch is not known", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(mainBefore, RevParse(bare, "refs/heads/main"));
    }

    [Fact]
    public async Task Pull_requests_and_contributor_mode_answer_a_sentence_on_a_local_repository()
    {
        var person = await PersonAsync();
        await CreateAsync(person, "widget");
        var team = await TeamAsync("Owned");
        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync($"/api/teams/{team}/repos", new[] { "local:widget" }, Ct)).StatusCode);

        var draft = await person.GetAsync($"/api/teams/{team}/repos/widget/pull-request-draft", Ct);
        Assert.Equal(HttpStatusCode.Conflict, draft.StatusCode);
        Assert.Contains("pull requests do not apply", await draft.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var open = await person.PostAsJsonAsync($"/api/teams/{team}/repos/widget/pull-request", new { title = "t", body = "b" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        Assert.Contains("pull requests do not apply", await open.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var contributor = await person.PutAsJsonAsync($"/api/teams/{team}/repos/widget/contributor", new
        {
            upstreamUrl = "https://github.com/project/Widget.git", forkOwner = (string?)null, dcoSignOff = false, claSignedNote = (string?)null,
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, contributor.StatusCode);
        Assert.Contains("contributor mode does not apply", await contributor.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.False(_factory.Services.GetRequiredService<TeamRegistry>().ContributorFor(team, "widget").ContributorMode);

        var created = await person.PostAsJsonAsync("/api/teams", new
        {
            name = "Contributing", agent = HeadlessAgent(), memberAgent = HeadlessAgent(),
            repos = new[] { "local:widget" },
            upstreams = new Dictionary<string, string> { ["local:widget"] = "https://github.com/project/Widget.git" },
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Contains("contributor mode does not apply", await created.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_is_refused_while_a_team_uses_it_and_otherwise_removes_it_after_its_row()
    {
        var person = await PersonAsync();
        await CreateAsync(person, "widget");
        var team = await TeamAsync("User");
        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync($"/api/teams/{team}/repos", new[] { "local:widget" }, Ct)).StatusCode);

        var refused = await person.DeleteAsync("/api/local-repos/widget", Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(team, await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Bare("widget")));
        Assert.Empty(await TenantRowsAsync(TenantActions.LocalRepoDeleted));

        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync($"/api/teams/{team}/repos", Array.Empty<string>(), Ct)).StatusCode);
        var deleted = await person.DeleteAsync("/api/local-repos/widget", Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(Directory.Exists(Bare("widget")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_dataRoot, "repos")));
        Assert.Equal("widget", Assert.Single(await TenantRowsAsync(TenantActions.LocalRepoDeleted)).Subject);

        Assert.Equal(HttpStatusCode.NotFound, (await person.DeleteAsync("/api/local-repos/widget", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await person.DeleteAsync("/api/local-repos/..%2Fkeys", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_push_into_a_local_repository_is_never_forced_and_a_missing_branch_is_not_deleted()
    {
        var bare = Path.Combine(_root, "repos", "gadget.git");
        var git = new GitRunner(localOriginFor: _ => bare);
        var repos = new LocalRepos(_root, git);
        Assert.Null(await repos.CreateAsync("gadget", Ct));
        var clone = Path.Combine(_root, "clone");
        Git(_root, "clone", "--no-local", bare, clone);
        File.WriteAllText(Path.Combine(clone, "a.txt"), "a\n");
        Commit(clone, "a");
        var first = RevParse(clone, "HEAD");

        var pushed = await git.PushRefspecAsync(clone, "main", "feature", Ct);
        Assert.True(pushed.ExitCode == 0, pushed.Stderr);
        Assert.Equal(first, RevParse(bare, "refs/heads/feature"));

        // History rewritten in the clone: the push is refused as git's own would be, and nothing moves.
        Git(clone, "reset", "--hard", "HEAD~1");
        File.WriteAllText(Path.Combine(clone, "b.txt"), "b\n");
        Commit(clone, "b");
        var refused = await git.PushRefspecAsync(clone, "main", "feature", Ct);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("non-fast-forward", refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(first, RevParse(bare, "refs/heads/feature"));

        var missing = await git.PushRefspecAsync(clone, string.Empty, "refs/heads/nothing", Ct);
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("remote ref does not exist", missing.Stderr, StringComparison.Ordinal);

        var notABranch = await git.PushRefspecAsync(clone, "main", "refs/tags/v1", Ct);
        Assert.NotEqual(0, notABranch.ExitCode);
        Assert.Null(TryRevParse(bare, "refs/tags/v1"));
    }

    // ---- helpers ----

    private async Task CreateAsync(HttpClient person, string name)
    {
        var response = await person.PostAsJsonAsync("/api/local-repos", new { name }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }

    private string HeadlessAgent() =>
        _factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;

    private async Task<string> TeamAsync(string name)
    {
        var agent = HeadlessAgent();
        return (await _factory.Services.GetRequiredService<TeamRegistry>().CreateAsync(name, agent, memberAgent: agent, ct: Ct)).Id;
    }

    private string ClonePath(string team, string repo) =>
        Path.Combine(_factory.Services.GetRequiredService<TeamPaths>().ReposFor(team), repo, "main");

    private async Task<IReadOnlyList<TenantEvent>> TenantRowsAsync(string action) =>
        [.. (await _factory.Services.GetRequiredService<ITenantLog>().ReadAsync(null, ITenantLog.MaxTake, Ct)).Events.Where(e => e.Action == action)];

    /// <summary>An action as a person presses it: again while the answer is 409 "still working".</summary>
    private static async Task<HttpResponseMessage> ActAsync(HttpClient person, string team, string repo, string action)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await person.PostAsync($"/api/teams/{team}/repos/{repo}/{action}", null, Ct);
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
        var (exit, _) = Run(workingDirectory, ["git", .. args]);
        Assert.True(exit == 0, $"git {string.Join(' ', args)} failed in {workingDirectory}");
    }

    private static (int Exit, string Stdout) Run(string workingDirectory, params string[] args)
    {
        var command = args[0] is "git" or "stat" ? args : ["git", .. args];
        var start = new ProcessStartInfo(command[0]) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in command.Skip(1)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
