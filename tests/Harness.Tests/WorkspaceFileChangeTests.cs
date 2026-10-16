using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// WHAT A RUNNING MEMBER'S WORKTREES DID, RECORDED DURING EVERY RUN AND PUSHED TO THE PEOPLE
/// WATCHING. Each test builds a real clone and a real per-card worktree under a team's repos folder,
/// changes files in it, and reads what <see cref="WorkspaceFileWatch"/> reports: one change per path
/// that appeared, changed or left the status, each new commit with its files, the caps, the project
/// rule, every change recorded with nobody watching and pushed only to a watcher, paths only, and
/// nothing read after the run's last read. The last tests listen on the real hub, as the Monitor does.
/// </summary>
public sealed class WorkspaceFileChangeTests : IAsyncDisposable
{
    private const string Team = "Alpha";
    private const string Member = "Dev";
    private const string Repo = "Widget";
    private const string Content = "a line of the file nobody may see";

    private static readonly WorkspaceRun Run = new(Team, Member, 1);

    private readonly string _root = Directory.CreateTempSubdirectory("harness-workspace-watch-").FullName;
    private readonly List<WorkspaceChange> _pushed = [];
    private readonly GitRunner _git = new();
    private readonly WorkspaceFileWatch _watch;
    private readonly WorkspaceFileChangeStore _record;
    private readonly string _clone;
    private readonly string _tree;
    private WebApplicationFactory<Program>? _factory;
    private readonly List<HubConnection> _hubs = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public WorkspaceFileChangeTests()
    {
        var paths = new TeamPaths(Path.Combine(_root, "data"));
        paths.Register(Team, null);
        (_clone, _tree) = MakeRepository(paths.ReposFor(Team), Member);

        var database = Path.Combine(_root, "messages.db");
        new SchemaMigrator(database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();
        _record = new WorkspaceFileChangeStore(database);

        _watch = Watch(paths, _record);
    }

    private WorkspaceFileWatch Watch(TeamPaths paths, WorkspaceFileChangeStore record) =>
        new(_git, paths, (change, _) =>
        {
            _pushed.Add(change);
            return Task.CompletedTask;
        }, record: record);

    [Fact]
    public async Task A_created_changed_renamed_and_deleted_file_each_make_one_change_with_its_action_and_project()
    {
        await StartAsync();

        Write("src/App/New.cs");
        Write("README.md", "changed");
        Git(_tree, "mv", "docs/guide.md", "docs/manual.md");
        File.Delete(Path.Combine(_tree, "src/App/Program.cs"));

        var change = Assert.Single(await ReadAsync());
        Assert.Equal(Team, change.Team);
        Assert.Equal(Member, change.Member);
        Assert.Equal(Repo, change.Repo);
        Assert.Empty(change.Commits);
        Assert.Equal(0, change.More);
        Assert.Equal(
            [
                new WorkspaceFile("README.md", Repo, WorkspaceFileChanges.Changed),
                new WorkspaceFile("docs/manual.md", "docs", WorkspaceFileChanges.Changed),
                new WorkspaceFile("src/App/New.cs", "App", WorkspaceFileChanges.Created),
                new WorkspaceFile("src/App/Program.cs", "App", WorkspaceFileChanges.Deleted),
            ],
            change.Files);
    }

    [Fact]
    public async Task An_unchanged_dirty_file_is_not_repeated()
    {
        await StartAsync();
        Write("README.md", "first edit");
        Assert.Single(await ReadAsync());

        Assert.Empty(await ReadAsync());

        Write("README.md", "second edit, the same status");
        Assert.Empty(await ReadAsync());
    }

    [Fact]
    public async Task A_file_already_dirty_before_the_run_is_not_shown()
    {
        Write("README.md", "dirty before the run");
        Write("notes.txt");
        await StartAsync();

        Assert.Empty(await ReadAsync());

        Write("src/App/Later.cs");
        var change = Assert.Single(await ReadAsync());
        Assert.Equal([new WorkspaceFile("src/App/Later.cs", "App", WorkspaceFileChanges.Created)], change.Files);
    }

    [Fact]
    public async Task A_commit_is_reported_with_its_short_sha_subject_line_and_files()
    {
        await StartAsync();
        Write("src/App/Feature.cs");
        Write("README.md", "edited");
        Git(_tree, "add", "-A");
        Git(_tree, "commit", "-q", "-m", "Add the feature\n\nThe body is not the subject.");
        var sha = Git(_tree, "rev-parse", "HEAD").Trim();

        var change = Assert.Single(await ReadAsync());
        Assert.Empty(change.Files);
        var commit = Assert.Single(change.Commits);
        Assert.Equal(sha[..7], commit.Sha);
        Assert.Equal("Add the feature", commit.Subject);
        Assert.Equal(
            [
                new WorkspaceFile("README.md", Repo, WorkspaceFileChanges.Changed),
                new WorkspaceFile("src/App/Feature.cs", "App", WorkspaceFileChanges.Created),
            ],
            commit.Files);
    }

    [Fact]
    public async Task Two_commits_at_once_are_each_reported_oldest_first()
    {
        await StartAsync();
        Commit("First", "one.txt");
        Git(_tree, "rm", "-q", "docs/guide.md");
        Git(_tree, "commit", "-q", "-m", "Second");

        var change = Assert.Single(await ReadAsync());
        Assert.Equal(["First", "Second"], change.Commits.Select(c => c.Subject));
        Assert.Equal([new WorkspaceFile("one.txt", Repo, WorkspaceFileChanges.Created)], change.Commits[0].Files);
        Assert.Equal([new WorkspaceFile("docs/guide.md", "docs", WorkspaceFileChanges.Deleted)], change.Commits[1].Files);
    }

    [Fact]
    public async Task A_rewrite_reports_the_new_head_commit_alone()
    {
        await StartAsync();
        Commit("Kept for now", "kept.txt");
        Assert.Single(await ReadAsync());

        Git(_tree, "reset", "-q", "--hard", "HEAD~1");
        Commit("Rewritten", "rewritten.txt");

        var change = Assert.Single(await ReadAsync());
        var commit = Assert.Single(change.Commits);
        Assert.Equal("Rewritten", commit.Subject);
        Assert.Equal([new WorkspaceFile("rewritten.txt", Repo, WorkspaceFileChanges.Created)], commit.Files);
    }

    [Fact]
    public async Task More_than_fifty_files_are_capped_and_counted_in_more()
    {
        await StartAsync();
        for (var i = 0; i < 60; i++) Write($"bulk/file{i:D2}.txt");

        var change = Assert.Single(await ReadAsync());
        Assert.Equal(WorkspaceFileChanges.MaxFiles, change.Files.Count);
        Assert.Equal(10, change.More);
    }

    [Fact]
    public async Task More_than_ten_commits_are_capped_and_counted_in_more()
    {
        await StartAsync();
        for (var i = 0; i < 12; i++) Commit($"Commit {i}", $"c{i}.txt");

        var change = Assert.Single(await ReadAsync());
        Assert.Equal(WorkspaceFileChanges.MaxCommits, change.Commits.Count);
        Assert.Equal("Commit 2", change.Commits[0].Subject);
        Assert.Equal("Commit 11", change.Commits[^1].Subject);
        Assert.Equal(2, change.More);
    }

    [Fact]
    public async Task Each_project_file_names_its_folder_and_a_file_with_none_falls_back_to_its_top_level_folder_or_the_repository()
    {
        string[] projects =
        [
            "cs/A.csproj", "fs/B.fsproj", "vb/C.vbproj", "sln/D.sln", "node/package.json", "go/go.mod",
            "rust/Cargo.toml", "py/pyproject.toml", "setup/setup.py", "maven/pom.xml", "gradle/build.gradle",
            "kts/build.gradle.kts",
        ];
        foreach (var project in projects) Write(project);
        Git(_tree, "add", "-A");
        Git(_tree, "commit", "-q", "-m", "Projects");
        await StartAsync();

        foreach (var project in projects) Write(project[..project.IndexOf('/')] + "/deep/er/file.txt");
        Write("lib/inner/file.txt");
        Write("top.txt");
        Write("made/here/here.csproj");
        Write("made/here/code.cs");

        var files = Assert.Single(await ReadAsync()).Files.ToDictionary(f => f.Path, f => f.Project);
        foreach (var project in projects)
        {
            var folder = project[..project.IndexOf('/')];
            Assert.Equal(folder, files[folder + "/deep/er/file.txt"]);
        }

        Assert.Equal("lib", files["lib/inner/file.txt"]);
        Assert.Equal(Repo, files["top.txt"]);
        Assert.Equal("here", files["made/here/code.cs"]);
        Assert.Equal("here", files["made/here/here.csproj"]);
    }

    [Fact]
    public void A_project_file_at_the_root_names_the_repository()
    {
        var folders = WorkspaceFileChanges.ProjectFolders(["package.json", "tools/cli/go.mod"]);

        Assert.Equal(Repo, WorkspaceFileChanges.ProjectOf("src/deep/x.ts", folders, Repo));
        Assert.Equal("cli", WorkspaceFileChanges.ProjectOf("tools/cli/main.go", folders, Repo));
        Assert.Equal(Repo, WorkspaceFileChanges.ProjectOf("README.md", folders, Repo));
        Assert.Equal("src", WorkspaceFileChanges.ProjectOf("src/x.cs", WorkspaceFileChanges.ProjectFolders([]), Repo));
    }

    [Fact]
    public async Task A_run_nobody_watches_records_each_path_action_and_commit_with_no_contents_and_pushes_nothing()
    {
        await _watch.PassAsync([Run], readAll: false, Ct);
        Assert.False(_watch.Watched);

        Write("src/App/New.cs", Content);
        Write("README.md", Content);
        File.Delete(Path.Combine(_tree, "src/App/Program.cs"));
        await _watch.PassAsync([Run], readAll: true, Ct);

        Commit("Record the commit", "committed.txt");
        var sha = Git(_tree, "rev-parse", "HEAD").Trim();
        await _watch.PassAsync([Run], readAll: true, Ct);

        Assert.Empty(_pushed);

        var rows = await RecordedAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal((Team, Member, Repo, 1L), (row.Team, row.Member, row.Repo, row.Correlation!.Value)));

        Assert.Equal(
            [
                new WorkspaceFile("README.md", Repo, WorkspaceFileChanges.Changed),
                new WorkspaceFile("src/App/New.cs", "App", WorkspaceFileChanges.Created),
                new WorkspaceFile("src/App/Program.cs", "App", WorkspaceFileChanges.Deleted),
            ],
            rows[0].Files);
        Assert.Empty(rows[0].Commits);

        Assert.Empty(rows[1].Files);
        var commit = Assert.Single(rows[1].Commits);
        Assert.Equal(sha[..7], commit.Sha);
        Assert.Equal("Record the commit", commit.Subject);
        Assert.Equal(
            [
                new WorkspaceFile("README.md", Repo, WorkspaceFileChanges.Changed),
                new WorkspaceFile("committed.txt", Repo, WorkspaceFileChanges.Created),
                new WorkspaceFile("src/App/New.cs", "App", WorkspaceFileChanges.Created),
                new WorkspaceFile("src/App/Program.cs", "App", WorkspaceFileChanges.Deleted),
            ],
            commit.Files);

        // No row holds a file's contents or a diff: not the rows as read, and not the database's
        // files on disk.
        var json = JsonSerializer.Serialize(rows);
        Assert.DoesNotContain(Content, json);
        Assert.DoesNotContain("@@", json);
        Assert.DoesNotContain("+++", json);
        foreach (var file in Directory.EnumerateFiles(_root, "messages.db*"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            Assert.DoesNotContain(Content, await reader.ReadToEndAsync(Ct));
        }
    }

    [Fact]
    public async Task A_person_who_joins_mid_run_is_pushed_what_is_found_next_and_it_is_recorded_too()
    {
        await _watch.PassAsync([Run], readAll: false, Ct);
        Write("unwatched.txt");
        await _watch.PassAsync([Run], readAll: true, Ct);
        Assert.Empty(_pushed);

        _watch.Join("person");
        Write("watched.txt");
        var pushed = Assert.Single(await ReadAsync());
        Assert.Equal([new WorkspaceFile("watched.txt", Repo, WorkspaceFileChanges.Created)], pushed.Files);

        _watch.Leave("person");
        Write("while.away.txt");
        Assert.Empty(await ReadAsync());

        Assert.Equal(
            ["unwatched.txt", "watched.txt", "while.away.txt"],
            (await RecordedAsync()).SelectMany(row => row.Files).Select(file => file.Path));
    }

    [Fact]
    public async Task A_change_that_cannot_be_recorded_is_skipped_and_the_push_still_goes()
    {
        // A database with no file_changes table: every record throws.
        var broken = new WorkspaceFileChangeStore(Path.Combine(_root, "unmigrated.db"));
        var paths = new TeamPaths(Path.Combine(_root, "data"));
        paths.Register(Team, null);
        var watch = Watch(paths, broken);

        watch.Join("person");
        await watch.PassAsync([Run], readAll: false, Ct);
        Write("still.pushed.txt");

        Assert.Equal(1, await watch.PassAsync([Run], readAll: true, Ct));
        Assert.Equal("still.pushed.txt", Assert.Single(Assert.Single(_pushed).Files).Path);
    }

    [Fact]
    public async Task A_run_that_ends_gets_one_last_read_that_catches_its_final_commit_and_nothing_after()
    {
        await StartAsync();
        Commit("The last word", "final.txt");

        await _watch.PassAsync([], readAll: false, Ct);
        var change = Assert.Single(_pushed);
        Assert.Equal("The last word", Assert.Single(change.Commits).Subject);

        Write("after.the.run.txt");
        var before = _git.Activity;
        await _watch.PassAsync([], readAll: true, Ct);
        Assert.Equal(before, _git.Activity);
        Assert.Single(_pushed);
    }

    [Fact]
    public async Task Only_the_running_members_own_trees_are_read_never_the_main_clone_or_another_members()
    {
        var other = Path.Combine(Path.GetDirectoryName(_clone)!, "wt_Other_1");
        Git(_clone, "worktree", "add", "-q", other, "-b", "other/1");
        await StartAsync();

        File.WriteAllText(Path.Combine(other, "theirs.txt"), Content);
        File.WriteAllText(Path.Combine(_clone, "clone.txt"), Content);

        Assert.Empty(await ReadAsync());
    }

    [Fact]
    public async Task A_push_carries_paths_only_never_a_files_contents_or_a_diff()
    {
        await StartAsync();
        Write("README.md", Content);
        Commit("Carry a line", "carried.txt");

        var change = Assert.Single(await ReadAsync());
        var json = JsonSerializer.Serialize(change);
        Assert.DoesNotContain(Content, json);
        Assert.DoesNotContain("@@", json);
        Assert.DoesNotContain("+++", json);
    }

    [Fact]
    public async Task The_push_reaches_a_watching_persons_window_on_the_real_hub_and_is_recorded_with_no_log_ledger_or_tenant_row()
    {
        var runs = new Runs();
        var factory = Factory(runs);
        var teamId = await TeamAsync(factory);
        var (_, tree) = MakeRepository(factory.Services.GetRequiredService<TeamPaths>().ReposFor(teamId), Member);

        var cookie = await SignInAsync(factory);
        var received = Channel.CreateUnbounded<string>();
        var hub = Hub(factory, options => options.Headers["Cookie"] = cookie);
        hub.On<JsonElement>(WorkspaceFileWatch.Message, change => received.Writer.TryWrite(change.GetRawText()));
        await hub.StartAsync(Ct);
        await hub.InvokeAsync("JoinFileWatch", Ct);

        var log = factory.Services.GetRequiredService<IMessageLog>();
        var tenant = factory.Services.GetRequiredService<ITenantLog>();
        var seq = await log.HighestSeqAsync(Ct);
        var tenantRows = (await tenant.ReadAsync(ct: Ct)).Total;

        runs.Set(new WorkspaceRun(teamId, Member, 7));
        await runs.PassesAsync(3);

        File.WriteAllText(Path.Combine(tree, "src/App/Seen.cs"), Content);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        var raw = await received.Reader.ReadAsync(wait.Token);

        var pushed = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(teamId, pushed.GetProperty("team").GetString());
        Assert.Equal(Member, pushed.GetProperty("member").GetString());
        Assert.Equal(Repo, pushed.GetProperty("repo").GetString());
        Assert.True(pushed.TryGetProperty("at", out _));
        Assert.Equal(0, pushed.GetProperty("more").GetInt32());
        Assert.Equal(0, pushed.GetProperty("commits").GetArrayLength());
        var file = Assert.Single(pushed.GetProperty("files").EnumerateArray());
        Assert.Equal("src/App/Seen.cs", file.GetProperty("path").GetString());
        Assert.Equal("App", file.GetProperty("project").GetString());
        Assert.Equal("created", file.GetProperty("action").GetString());
        Assert.DoesNotContain(Content, raw);

        Assert.Equal(seq, await log.HighestSeqAsync(Ct));
        Assert.Equal(tenantRows, (await tenant.ReadAsync(ct: Ct)).Total);

        var recorded = Assert.Single(await factory.Services.GetRequiredService<WorkspaceFileChangeStore>().ReadAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 10,
            new Dictionary<string, DateTimeOffset> { [teamId] = DateTimeOffset.MinValue }, Ct));
        Assert.Equal((Member, Repo, 7L), (recorded.Member, recorded.Repo, recorded.Correlation!.Value));
        Assert.Equal("src/App/Seen.cs", Assert.Single(recorded.Files).Path);
    }

    [Fact]
    public async Task A_machine_principal_cannot_join_the_watch()
    {
        var factory = Factory(new Runs());
        var teamId = await TeamAsync(factory);
        var key = await factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(teamId, Member).ToString(), PrincipalKind.Container, teamId, Permits.All, ct: Ct);

        var hub = Hub(factory, options => options.Headers[ApiKeyAuthenticationHandler.Header] = key);
        await hub.StartAsync(Ct);

        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinFileWatch", Ct));
        Assert.False(factory.Services.GetRequiredService<WorkspaceFileWatch>().Watched);
    }

    private async Task StartAsync()
    {
        _watch.Join("person");
        await _watch.PassAsync([Run], readAll: false, Ct);
    }

    private async Task<IReadOnlyList<RecordedWorkspaceChange>> RecordedAsync() =>
        await _record.ReadAsync(
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 0, 100,
            new Dictionary<string, DateTimeOffset> { [Team] = DateTimeOffset.MinValue }, Ct);

    private async Task<List<WorkspaceChange>> ReadAsync()
    {
        _pushed.Clear();
        await _watch.PassAsync([Run], readAll: true, Ct);
        return [.. _pushed];
    }

    private void Write(string path, string? text = null)
    {
        var full = Path.Combine(_tree, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text ?? path);
    }

    private void Commit(string subject, string path)
    {
        Write(path);
        Git(_tree, "add", "-A");
        Git(_tree, "commit", "-q", "-m", subject);
    }

    /// <summary>A clone at <c>repos/Widget/main</c> with one commit, and the member's per-card tree
    /// beside it, as the platform lays them out.</summary>
    private static (string Clone, string Tree) MakeRepository(string repos, string member)
    {
        var clone = Path.Combine(repos, Repo, "main");
        Directory.CreateDirectory(clone);
        Git(clone, "init", "-q", "-b", "main");

        foreach (var (path, text) in new[]
        {
            ("README.md", "readme"), ("src/App/App.csproj", "<Project />"), ("src/App/Program.cs", "class P {}"),
            ("docs/guide.md", "guide"),
        })
        {
            var full = Path.Combine(clone, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        Git(clone, "add", "-A");
        Git(clone, "commit", "-q", "-m", "Initial");

        var tree = Path.Combine(repos, Repo, Worktrees.DirectoryName(member, "1"));
        Git(clone, "worktree", "add", "-q", tree, "-b", "dev/1");
        return (clone, tree);
    }

    private static string Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false" })
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
        return stdout;
    }

    private WebApplicationFactory<Program> Factory(Runs runs)
    {
        var dataRoot = Path.Combine(_root, "host");
        Directory.CreateDirectory(dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IWorkspaceRuns>(runs);
                services.AddSingleton(new WorkspaceFileWatchTiming
                {
                    Read = TimeSpan.FromMilliseconds(100),
                    Tick = TimeSpan.FromMilliseconds(50),
                });
            }));
        return _factory;
    }

    private static async Task<string> TeamAsync(WebApplicationFactory<Program> factory)
    {
        var agent = factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        return (await factory.Services.GetRequiredService<TeamRegistry>().CreateAsync(Team, agent, memberAgent: agent)).Id;
    }

    private static async Task<string> SignInAsync(WebApplicationFactory<Program> factory)
    {
        const string email = "person@example.test";
        const string password = "correct horse battery";
        await factory.Services.GetRequiredService<IUserStore>().CreateAsync(email, password);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password }, Ct);
        login.EnsureSuccessStatusCode();
        return string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
    }

    private HubConnection Hub(WebApplicationFactory<Program> factory, Action<HttpConnectionOptions> credential)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hub/containers"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                credential(options);
            })
            .Build();
        _hubs.Add(hub);
        return hub;
    }

    /// <summary>The runs in progress, set by the test; counts how often the loop asked.</summary>
    private sealed class Runs : IWorkspaceRuns
    {
        private volatile WorkspaceRun[] _running = [];
        private int _asked;

        public void Set(params WorkspaceRun[] running)
        {
            _running = running;
            Interlocked.Exchange(ref _asked, 0);
        }

        public IReadOnlyCollection<WorkspaceRun> Running()
        {
            Interlocked.Increment(ref _asked);
            return _running;
        }

        /// <summary>Waits until the loop has asked this many times since <see cref="Set"/>: the
        /// pass after the first ask has taken the baseline.</summary>
        public async Task PassesAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Volatile.Read(ref _asked) < count)
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("the watch loop never asked which runs are in progress");
                await Task.Delay(20, Ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hub in _hubs) await hub.DisposeAsync();
        if (_factory is not null) await _factory.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
