using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// A DELETED TEAM'S AGENT SESSION FOLDERS GO WITH IT. The agent CLIs keep a folder per
/// working directory in the shared agent home - Claude's <c>~/.claude/projects/&lt;dashed&gt;</c> and
/// <c>~/.cache/claude-cli-nodejs/&lt;dashed&gt;</c>, grok's <c>~/.grok/sessions/&lt;encoded&gt;</c>, as each
/// built-in preset names them - and a team deletion removes the ones keyed to that team's own
/// workspaces through <see cref="FolderRemoval"/>, links removed and never followed. Nothing else in
/// the home is touched, and nothing of a live team's.
/// </summary>
public sealed class TeamDeleteSessionFoldersTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-session-folders-").FullName;
    private readonly string _dataRoot;
    private readonly string _home;

    private readonly ContainerHost _host;
    private readonly TeamRegistry _teams;
    private readonly TeamDeletion _deletion;
    private readonly TeamPaths _paths;
    private readonly RefusingDeletes _deletes = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TeamDeleteSessionFoldersTests()
    {
        _dataRoot = Path.Combine(_root, "data");
        _home = Path.Combine(_root, "agent-home");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_home);

        var database = Path.Combine(_dataRoot, "messages.db");
        new SchemaMigrator(database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();

        var store = new SqliteMessageStore(database);
        var pending = new SqlitePendingDeliveries(database);
        var triggers = new SqliteTriggerStore(database);
        var teamStore = new SqliteTeamStore(database);
        var principals = new SqlitePrincipalStore(database);
        var git = new GitRunner();

        _paths = new TeamPaths(_dataRoot);
        var removal = new FolderRemoval(unfinished: new SqliteUnfinishedRemovals(database), host: _deletes.Seam);

        _host = new ContainerHost(store, store, store, new InMemoryTranscriptStore(), pending, triggers);

        var catalog = new AgentCatalog([new AgentDefinition("fake", AgentMode.Headless, new AgentLaunch("true", []))]);
        var effective = new EffectiveSubscriptions(teamStore, triggers, store, _host);

        _teams = new TeamRegistry(
            _host, catalog, new AgentMemberRunner(new FakeAgent()), _paths, teamStore,
            new AgentEnvironment(principals, catalog, "http://127.0.0.1:1", new TeamDocuments(_paths)),
            new FileBrowserPolicy([new FileBrowserRoot("data", _dataRoot, AllowCreate: true)]),
            store, effective, new RepoClone(git, enabled: false),
            removal: removal);

        // The built-in presets' own session folders, read from the build: the test does not name them.
        _deletion = new TeamDeletion(
            _teams, _host, teamStore, store, store, effective, pending, triggers, principals, _paths, git, removal,
            agentHome: _home);
    }

    [Fact]
    public void The_built_in_presets_name_claudes_projects_and_cache_folders_and_groks_sessions()
    {
        Assert.Equal(
            ["~/.claude/projects/{workspaceDashed}", "~/.cache/claude-cli-nodejs/{workspaceDashed}", "~/.grok/sessions/{workspaceEncoded}"],
            AgentSessionFolders.BuiltIn());

        // Every built-in preset of those CLIs names them, beside its live view.
        foreach (var preset in AgentCatalogFile.BuiltIns().Where(d => d.Launch.FileName is "claude" or "grok"))
        {
            Assert.NotNull(preset.SessionFolders);
        }

        Assert.Null(AgentSessionFolders.Resolve("~/.claude/../{workspaceDashed}", _home, "/w"));
        Assert.Null(AgentSessionFolders.Resolve("/etc/{workspaceDashed}", _home, "/w"));
        Assert.Null(AgentSessionFolders.Resolve("~/.claude/projects", _home, "/w"));
        Assert.Null(AgentSessionFolders.Resolve("~/{workspaceDashed}/x", _home, "/w"));
        Assert.Equal(Path.Combine(_home, ".claude", "projects", "-data-teams-a-b"),
            AgentSessionFolders.Resolve("~/.claude/projects/{workspaceDashed}", _home, "/data/teams/a.b"));
    }

    [Fact]
    public async Task Deleting_a_team_removes_its_members_session_folders_and_leaves_a_live_teams_and_every_other_folder()
    {
        await _teams.CreateAsync("Alpha", "fake", memberAgents: ["fake"], ct: Ct);
        await _teams.CreateAsync("Beta", "fake", memberAgents: ["fake"], ct: Ct);

        // The Manager the registry names, and a member's workspace on disk beside it.
        var alpha = _teams.ContainerIdsOf("Alpha").Select(_paths.WorkspaceFor).ToList();
        Assert.Single(alpha);
        alpha.Add(Path.Combine(_paths.RootFor("Alpha"), "workspaces", "Developer"));
        Directory.CreateDirectory(alpha[1]);
        var beta = _teams.ContainerIdsOf("Beta").Select(_paths.WorkspaceFor).ToList();
        beta.Add(Path.Combine(_paths.RootFor("Beta"), "workspaces", "Developer"));
        Directory.CreateDirectory(beta[1]);

        // A member deleted earlier whose workspace folder is still on disk is the team's too.
        var earlier = Path.Combine(_paths.RootFor("Alpha"), "workspaces", "Gone");
        Directory.CreateDirectory(earlier);

        var doomed = new List<string>();
        foreach (var workspace in alpha.Append(earlier))
        {
            doomed.Add(Write(Projects(workspace), "session.jsonl"));
            doomed.Add(Write(Cache(workspace), "mcp-logs", "log.txt"));
        }

        doomed.Add(Write(Grok(alpha[0]), "0000", "updates.jsonl"));

        var live = new List<string>();
        foreach (var workspace in beta)
        {
            live.Add(Write(Projects(workspace), "session.jsonl"));
            live.Add(Write(Cache(workspace), "mcp-logs", "log.txt"));
            live.Add(Write(Grok(workspace), "0000", "updates.jsonl"));
        }

        var others = new List<string>
        {
            Write(Path.Combine(_home, ".claude", "projects", "-some-other-folder"), "session.jsonl"),
            Write(Path.Combine(_home, ".claude"), "settings.json"),
            Write(Path.Combine(_home, ".cache", "claude-cli-nodejs", "-another"), "log.txt"),
            Write(Path.Combine(_home, ".grok", "sessions", "%2Felsewhere"), "0000", "updates.jsonl"),
            Write(Path.Combine(_home, ".codex", "sessions", "2026", "09", "30"), "rollout-1.jsonl"),
        };

        var deleted = await _deletion.DeleteAsync("Alpha", ct: Ct);

        Assert.NotNull(deleted);
        Assert.Equal(7, deleted.SessionFolders);
        Assert.Empty(deleted.SessionFoldersRemaining);
        Assert.Empty(deleted.Failures);

        foreach (var file in doomed) Assert.False(Directory.Exists(Path.GetDirectoryName(file)), $"{file} is still there");
        foreach (var workspace in alpha.Append(earlier))
        {
            Assert.False(Directory.Exists(Projects(workspace)));
            Assert.False(Directory.Exists(Cache(workspace)));
        }

        Assert.False(Directory.Exists(Grok(alpha[0])));
        foreach (var file in live.Concat(others)) Assert.True(File.Exists(file), $"{file} was touched");
        Assert.True(Directory.Exists(Path.Combine(_home, ".claude", "projects")));
        Assert.True(Directory.Exists(Path.Combine(_home, ".cache", "claude-cli-nodejs")));
    }

    [Fact]
    public async Task A_folder_a_live_teams_workspace_dashes_to_as_well_is_kept_and_not_counted()
    {
        await _teams.CreateAsync("Alpha", "fake", memberAgents: ["fake"], ct: Ct);
        await _teams.CreateAsync("Alpha-workspaces-X", "fake", memberAgents: ["fake"], ct: Ct);
        var own = _paths.WorkspaceFor(Assert.Single(_teams.ContainerIdsOf("Alpha")));
        var liveWorkspace = _paths.WorkspaceFor(Assert.Single(_teams.ContainerIdsOf("Alpha-workspaces-X")));

        // A folder left in the deleted team's workspaces whose path dashes exactly as the live
        // team's workspace does: `/` and `-` both become `-`.
        var colliding = Path.Combine(_paths.RootFor("Alpha"), "workspaces", "X-workspaces-" + Path.GetFileName(liveWorkspace));
        Directory.CreateDirectory(colliding);
        Assert.NotEqual(liveWorkspace, colliding);
        Assert.Equal(LiveView.Dashed(liveWorkspace), LiveView.Dashed(colliding));

        var doomed = Write(Projects(own), "session.jsonl");
        var live = Write(Projects(liveWorkspace), "session.jsonl");

        var deleted = await _deletion.DeleteAsync("Alpha", ct: Ct);

        Assert.NotNull(deleted);
        Assert.False(File.Exists(doomed));
        Assert.True(File.Exists(live), "the live team's session folder was removed");
        Assert.Equal(1, deleted.SessionFolders);
        Assert.Empty(deleted.SessionFoldersRemaining);
    }

    [Fact]
    public async Task A_session_folder_that_is_a_link_is_removed_as_a_link_and_what_it_points_at_is_kept()
    {
        await _teams.CreateAsync("Alpha", "fake", memberAgents: ["fake"], ct: Ct);
        var workspace = _paths.WorkspaceFor(Assert.Single(_teams.ContainerIdsOf("Alpha")));

        var target = Write(Path.Combine(_root, "elsewhere"), "precious.txt");
        Directory.CreateDirectory(Path.Combine(_home, ".claude", "projects"));
        Directory.CreateSymbolicLink(Projects(workspace), Path.GetDirectoryName(target)!);

        var deleted = await _deletion.DeleteAsync("Alpha", ct: Ct);

        Assert.NotNull(deleted);
        Assert.Equal(1, deleted.SessionFolders);
        Assert.False(Path.Exists(Projects(workspace)));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task A_folder_reached_through_a_linked_cli_folder_is_left_and_named()
    {
        await _teams.CreateAsync("Alpha", "fake", memberAgents: ["fake"], ct: Ct);
        var workspace = _paths.WorkspaceFor(Assert.Single(_teams.ContainerIdsOf("Alpha")));

        // `~/.claude` planted as a link to somewhere outside the home.
        var outside = Path.Combine(_root, "outside-claude");
        var kept = Write(Path.Combine(outside, "projects", LiveView.Dashed(workspace)), "session.jsonl");
        Directory.CreateSymbolicLink(Path.Combine(_home, ".claude"), outside);

        var deleted = await _deletion.DeleteAsync("Alpha", ct: Ct);

        Assert.NotNull(deleted);
        Assert.Equal(0, deleted.SessionFolders);
        Assert.True(File.Exists(kept));
        var left = Assert.Single(deleted.SessionFoldersRemaining);
        Assert.Contains("no symbolic link on the way", left, StringComparison.Ordinal);
        Assert.Contains(deleted.Failures, f => f.StartsWith("agent session folders:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_cannot_be_removed_is_named_in_the_result()
    {
        await _teams.CreateAsync("Alpha", "fake", memberAgents: ["fake"], ct: Ct);
        var workspace = _paths.WorkspaceFor(Assert.Single(_teams.ContainerIdsOf("Alpha")));
        var stuck = Write(Projects(workspace), "held.jsonl");
        _deletes.Refuse(stuck);

        var deleted = await _deletion.DeleteAsync("Alpha", ct: Ct);

        Assert.NotNull(deleted);
        Assert.Equal(0, deleted.SessionFolders);
        Assert.Equal([stuck], deleted.SessionFoldersRemaining);
        Assert.Contains(deleted.Failures, f => f.StartsWith("agent session folders:", StringComparison.Ordinal) && f.Contains(stuck, StringComparison.Ordinal));
        Assert.True(File.Exists(stuck));
    }

    private string Projects(string workspace) => Path.Combine(_home, ".claude", "projects", LiveView.Dashed(workspace));

    private string Cache(string workspace) => Path.Combine(_home, ".cache", "claude-cli-nodejs", LiveView.Dashed(workspace));

    private string Grok(string workspace) => Path.Combine(_home, ".grok", "sessions", LiveView.Encoded(workspace));

    private static string Write(string folder, params string[] relative)
    {
        var path = Path.Combine([folder, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The Host's deletes, failing for the paths a test names, as an agent's owner-only
    /// file fails for the product's Host whatever user the suite runs as.</summary>
    private sealed class RefusingDeletes
    {
        private readonly HashSet<string> _refused = new(StringComparer.Ordinal);

        public void Refuse(string path) => _refused.Add(path);

        public FolderRemoval.HostDeletes Seam => new(
            path =>
            {
                if (_refused.Contains(path)) throw new UnauthorizedAccessException($"refused: {path}");
                File.Delete(path);
            },
            path =>
            {
                if (_refused.Contains(path)) throw new UnauthorizedAccessException($"refused: {path}");
                Directory.Delete(path, recursive: false);
            });
    }
}
