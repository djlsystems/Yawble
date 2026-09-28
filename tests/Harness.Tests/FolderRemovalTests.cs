using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// A TEAM ROOT, A MEMBER'S WORKSPACE AND A RESET'S FOLDERS ARE REMOVED ONE WAY: <see cref="FolderRemoval"/>.
///
/// The marker goes last, so a removal that fails partway leaves a folder still recognisably the
/// platform's; whatever cannot be removed is named path by path and recorded as a removal
/// unfinished; a retry (at start, on request, or by creating the same team again) finishes it;
/// links are removed as links. The Host's own deletes go through <see cref="FolderRemoval.HostDeletes"/>
/// so a test can make a path fail the way an agent's owner-only directory fails for the product's
/// Host, whatever user the suite runs as.
/// </summary>
public sealed class FolderRemovalTests : IAsyncDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-removal-{Guid.NewGuid():N}");

    private readonly ContainerHost _host;
    private readonly TeamRegistry _teams;
    private readonly TeamDeletion _deletion;
    private readonly MemberDeletion _members;
    private readonly TeamReset _reset;
    private readonly UnfinishedRemovalRetry _retry;
    private readonly SqliteUnfinishedRemovals _unfinished;
    private readonly FlakyDeletes _deletes = new();
    private readonly TeamPaths _paths;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FolderRemovalTests()
    {
        Directory.CreateDirectory(_dataRoot);

        var database = Path.Combine(_dataRoot, "messages.db");
        new SchemaMigrator(database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();

        var store = new SqliteMessageStore(database);
        var pending = new SqlitePendingDeliveries(database);
        var triggers = new SqliteTriggerStore(database);
        var teamStore = new SqliteTeamStore(database);
        var principals = new SqlitePrincipalStore(database);
        var git = new GitRunner();

        _paths = new TeamPaths(_dataRoot);
        _unfinished = new SqliteUnfinishedRemovals(database);

        var removal = new FolderRemoval(unfinished: _unfinished, host: _deletes.Seam);

        _host = new ContainerHost(
            store, store, store,
            new InMemoryTranscriptStore(), pending, triggers);

        var catalog = new AgentCatalog(
            [new AgentDefinition("fake", AgentMode.Headless, new AgentLaunch("true", []))]);
        var effective = new EffectiveSubscriptions(teamStore, triggers, store, _host);

        _teams = new TeamRegistry(
            _host, catalog, new AgentMemberRunner(new FakeAgent()), _paths, teamStore,
            new AgentEnvironment(principals, catalog, "http://127.0.0.1:1", new TeamDocuments(_paths)),
            new FileBrowserPolicy([new FileBrowserRoot("data", _dataRoot, AllowCreate: true)]),
            store, effective, new RepoClone(git, enabled: false),
            removal: removal);

        _deletion = new TeamDeletion(
            _teams, _host, teamStore, store, store, effective, pending, triggers, principals, _paths, git, removal);
        _members = new MemberDeletion(
            _teams, _host, store, effective, pending, triggers, principals, _paths, store, removal: removal);
        _reset = new TeamReset(_teams, _host, teamStore, store, pending, store, _paths, removal);
        _retry = new UnfinishedRemovalRetry(removal, _unfinished, _teams, _host, _paths);
    }

    private string AlphaRoot => TeamPaths.ContainerOf(_dataRoot, "Alpha");

    private string AlphaMarker => TeamPaths.MarkerIn(AlphaRoot);

    [Fact]
    public async Task The_marker_is_removed_last_and_then_the_root()
    {
        await CreateAlphaAsync();
        await WriteAsync(Path.Combine(AlphaRoot, "workspaces", "Manager", "notes.md"));

        var deleted = await _deletion.DeleteAsync("Alpha", ct: Ct);

        Assert.NotNull(deleted);
        Assert.Empty(deleted.Failures);
        Assert.Empty(deleted.Remaining);
        Assert.False(Directory.Exists(AlphaRoot));

        // Everything else went first; the marker, then the root, are the last two removals.
        Assert.Equal(AlphaMarker, _deletes.Log[^2]);
        Assert.Equal(AlphaRoot, _deletes.Log[^1]);
    }

    [Fact]
    public async Task A_removal_that_fails_partway_keeps_the_marker_and_names_every_remaining_path()
    {
        await CreateAlphaAsync();
        var stuck = await WriteAsync(Path.Combine(AlphaRoot, "workspaces", "Manager", "tmp", "stuck.bin"));
        var gone = await WriteAsync(Path.Combine(AlphaRoot, "transcripts", "Manager", "run.jsonl"));
        _deletes.Refuse(stuck);

        var deleted = await _deletion.DeleteAsync("Alpha", ct: Ct);

        Assert.NotNull(deleted);
        Assert.Equal([stuck], deleted.Remaining);
        Assert.Contains(deleted.Failures, f => f.Contains("removal unfinished", StringComparison.Ordinal) && f.Contains(stuck, StringComparison.Ordinal));
        Assert.True(File.Exists(AlphaMarker));
        Assert.True(File.Exists(stuck));
        Assert.False(File.Exists(gone));
        Assert.DoesNotContain(AlphaMarker, _deletes.Log);

        var row = await _unfinished.FindAsync(AlphaRoot, Ct);
        Assert.NotNull(row);
        Assert.Equal(RemovalKinds.TeamRoot, row.Kind);
        Assert.Equal([stuck], row.Remaining);
    }

    [Fact]
    public async Task A_removal_that_fails_first_finishes_when_retried_on_request()
    {
        await CreateAlphaAsync();
        var stuck = await WriteAsync(Path.Combine(AlphaRoot, "repos", "Widget", "main", ".git", "objects", "ab", "cdef"));
        _deletes.Refuse(stuck);
        await _deletion.DeleteAsync("Alpha", ct: Ct);

        // Still failing: still recorded, one more attempt counted, marker still there.
        var again = Assert.Single(await _retry.RetryAsync(AlphaRoot, Ct));
        Assert.False(again.Finished);
        Assert.Equal([stuck], again.Remaining);
        Assert.Equal(2, (await _unfinished.FindAsync(AlphaRoot, Ct))!.Attempts);
        Assert.True(File.Exists(AlphaMarker));

        // Removable now.
        _deletes.Allow(stuck);
        var retried = Assert.Single(await _retry.RetryAsync(AlphaRoot, Ct));

        Assert.True(retried.Finished);
        Assert.Empty(retried.Remaining);
        Assert.False(Directory.Exists(AlphaRoot));
        Assert.Null(await _unfinished.FindAsync(AlphaRoot, Ct));
    }

    [Fact]
    public async Task A_root_whose_marker_went_is_never_deleted_by_a_retry()
    {
        await CreateAlphaAsync();
        var stuck = await WriteAsync(Path.Combine(AlphaRoot, "workspaces", "Manager", "stuck.bin"));
        _deletes.Refuse(stuck);
        await _deletion.DeleteAsync("Alpha", ct: Ct);

        File.Delete(AlphaMarker);
        _deletes.Allow(stuck);
        var retried = Assert.Single(await _retry.RetryAsync(ct: Ct));

        Assert.False(retried.Finished);
        Assert.Contains(TeamPaths.MarkerFileName, retried.Note);
        Assert.True(File.Exists(stuck));
        Assert.Null(await _unfinished.FindAsync(AlphaRoot, Ct));
    }

    [Fact]
    public async Task A_link_inside_the_root_pointing_outside_it_is_removed_as_a_link_and_its_target_is_untouched()
    {
        await CreateAlphaAsync();
        var outside = Directory.CreateDirectory(Path.Combine(_dataRoot, "outside")).FullName;
        var precious = await WriteAsync(Path.Combine(outside, "precious.txt"));
        var toDirectory = Path.Combine(AlphaRoot, "workspaces", "Manager", "elsewhere");
        var toFile = Path.Combine(AlphaRoot, "workspaces", "Manager", "precious-link.txt");
        Directory.CreateSymbolicLink(toDirectory, outside);
        File.CreateSymbolicLink(toFile, precious);

        var deleted = await _deletion.DeleteAsync("Alpha", ct: Ct);

        Assert.NotNull(deleted);
        Assert.Empty(deleted.Remaining);
        Assert.False(Directory.Exists(AlphaRoot));
        Assert.True(File.Exists(precious));
        Assert.Equal("keep me", await File.ReadAllTextAsync(precious, Ct));
    }

    [Fact]
    public void A_path_is_confined_only_when_it_is_inside_the_root_with_no_link_on_the_way()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dataRoot, "confined")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_dataRoot, "beyond")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "real", "deep"));
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);

        Assert.True(FolderRemoval.Confined(root, Path.Combine(root, "real", "deep")));
        Assert.True(FolderRemoval.Confined(root, Path.Combine(root, "link")));
        Assert.False(FolderRemoval.Confined(root, Path.Combine(root, "link", "file")));
        Assert.False(FolderRemoval.Confined(root, Path.Combine(root, "..", "beyond")));
        Assert.False(FolderRemoval.Confined(root, root));
    }

    [Fact]
    public async Task A_members_workspace_is_removed_the_same_way_and_retried()
    {
        await CreateAlphaAsync();
        await _teams.AddContainerAsync("Alpha", "Dev", "fake", "", [], ct: Ct);
        var workspace = _paths.WorkspaceFor(new ContainerId("Alpha", "Dev"));
        var stuck = await WriteAsync(Path.Combine(workspace, "tmp", "nuget", "lock"));
        await WriteAsync(Path.Combine(workspace, "notes.md"));
        _deletes.Refuse(stuck);

        var deleted = await _members.DeleteAsync("Alpha", "Dev", Ct);

        Assert.NotNull(deleted);
        Assert.Equal([stuck], deleted.Remaining);
        Assert.Contains(deleted.Failures, f => f.Contains(stuck, StringComparison.Ordinal));
        Assert.Equal(RemovalKinds.Workspace, (await _unfinished.FindAsync(workspace, Ct))!.Kind);

        _deletes.Allow(stuck);
        var retried = Assert.Single(await _retry.RetryAsync(ct: Ct));

        Assert.True(retried.Finished);
        Assert.False(Directory.Exists(workspace));
        Assert.True(File.Exists(AlphaMarker));
    }

    [Fact]
    public async Task A_workspace_whose_member_is_back_is_left_alone_by_a_retry()
    {
        await CreateAlphaAsync();
        await _teams.AddContainerAsync("Alpha", "Dev", "fake", "", [], ct: Ct);
        var workspace = _paths.WorkspaceFor(new ContainerId("Alpha", "Dev"));
        var stuck = await WriteAsync(Path.Combine(workspace, "stuck.bin"));
        _deletes.Refuse(stuck);
        await _members.DeleteAsync("Alpha", "Dev", Ct);

        await _teams.AddContainerAsync("Alpha", "Dev", "fake", "", [], ct: Ct);
        var work = await WriteAsync(Path.Combine(workspace, "new-work.md"));
        _deletes.Allow(stuck);
        var retried = Assert.Single(await _retry.RetryAsync(ct: Ct));

        Assert.False(retried.Finished);
        Assert.NotNull(retried.Note);
        Assert.True(File.Exists(work));
        Assert.Null(await _unfinished.FindAsync(workspace, Ct));
    }

    [Fact]
    public async Task A_reset_empties_the_same_way_and_a_retry_removes_only_what_it_named()
    {
        await CreateAlphaAsync();
        var workspace = _paths.WorkspaceFor(new ContainerId("Alpha", "Manager"));
        var stuck = await WriteAsync(Path.Combine(workspace, "tmp", "stuck.bin"));
        var gone = await WriteAsync(Path.Combine(workspace, "notes.md"));
        _deletes.Refuse(stuck);

        var reset = await _reset.ResetAsync(
            "Alpha", new TeamResetOptions(["Manager"], ForgetHistory: false, ClearWorkspaces: true), Ct);

        Assert.NotNull(reset);
        Assert.Equal([stuck], reset.Remaining);
        Assert.Contains(reset.Failures, f => f.Contains(stuck, StringComparison.Ordinal));
        Assert.False(File.Exists(gone));
        Assert.Equal(RemovalKinds.Emptied, (await _unfinished.FindAsync(workspace, Ct))!.Kind);

        // Written after the reset: not the retry's to remove.
        var since = await WriteAsync(Path.Combine(workspace, "since.md"));
        _deletes.Allow(stuck);
        var retried = Assert.Single(await _retry.RetryAsync(ct: Ct));

        Assert.True(retried.Finished);
        Assert.False(File.Exists(stuck));
        Assert.True(File.Exists(since));
        Assert.True(Directory.Exists(workspace));
    }

    [Fact]
    public async Task A_reset_retry_keeps_what_the_member_wrote_since_into_a_directory_the_reset_named()
    {
        await CreateAlphaAsync();
        var workspace = _paths.WorkspaceFor(new ContainerId("Alpha", "Manager"));
        var tmp = Path.Combine(workspace, "tmp");
        var cache = Path.Combine(workspace, "cache");
        var old = await WriteAsync(Path.Combine(tmp, "old.bin"));
        await WriteAsync(Path.Combine(cache, "old.bin"));
        _deletes.Refuse(tmp);
        _deletes.Refuse(cache);

        var reset = await _reset.ResetAsync(
            "Alpha", new TeamResetOptions(["Manager"], ForgetHistory: false, ClearWorkspaces: true), Ct);

        // Emptied, but the directories themselves could not go.
        Assert.NotNull(reset);
        Assert.Equal([cache, tmp], reset.Remaining);
        Assert.False(File.Exists(old));

        // The team is live again: the member works in tmp. cache stays as the reset left it.
        var since = await WriteSinceAsync(Path.Combine(tmp, "new-work.md"));
        _deletes.Allow(tmp);
        _deletes.Allow(cache);
        var retried = Assert.Single(await _retry.RetryAsync(ct: Ct));

        Assert.True(retried.Finished);
        Assert.True(File.Exists(since), "the retry removed work written after the reset");
        Assert.False(Directory.Exists(cache));
        Assert.Null(await _unfinished.FindAsync(workspace, Ct));
    }

    [Fact]
    public async Task A_reset_retry_of_a_folder_it_could_not_list_stays_unfinished_until_it_can_be_emptied()
    {
        await CreateAlphaAsync();
        var workspace = _paths.WorkspaceFor(new ContainerId("Alpha", "Manager"));
        var left = await WriteAsync(Path.Combine(workspace, "left.md"));
        _deletes.RefuseListing(workspace);

        var reset = await _reset.ResetAsync(
            "Alpha", new TeamResetOptions(["Manager"], ForgetHistory: false, ClearWorkspaces: true), Ct);

        Assert.NotNull(reset);
        Assert.Equal([workspace], reset.Remaining);

        // Still unlistable: named again, kept, never reported finished.
        var still = Assert.Single(await _retry.RetryAsync(ct: Ct));

        Assert.False(still.Finished);
        Assert.True(File.Exists(left));
        Assert.Equal([workspace], (await _unfinished.FindAsync(workspace, Ct))!.Remaining);

        // Listable at last: what the reset left goes, what was written since stays.
        _deletes.AllowListing(workspace);
        var since = await WriteSinceAsync(Path.Combine(workspace, "since.md"));
        var retried = Assert.Single(await _retry.RetryAsync(ct: Ct));

        Assert.True(retried.Finished);
        Assert.False(File.Exists(left));
        Assert.True(File.Exists(since));
        Assert.Null(await _unfinished.FindAsync(workspace, Ct));
    }

    [Fact]
    public async Task A_reset_retry_keeps_a_named_file_the_member_rewrote_since_and_judges_a_named_link_by_itself()
    {
        await CreateAlphaAsync();
        var workspace = _paths.WorkspaceFor(new ContainerId("Alpha", "Manager"));
        var notes = await WriteAsync(Path.Combine(workspace, "notes.md"));
        var stale = await WriteAsync(Path.Combine(workspace, "stale.md"));
        var target = await WriteAsync(Path.Combine(_dataRoot, "outside", "target.md"));
        var link = Path.Combine(workspace, "link");
        File.CreateSymbolicLink(link, target);
        _deletes.Refuse(notes);
        _deletes.Refuse(stale);
        _deletes.Refuse(link);

        var reset = await _reset.ResetAsync(
            "Alpha", new TeamResetOptions(["Manager"], ForgetHistory: false, ClearWorkspaces: true), Ct);

        Assert.NotNull(reset);
        Assert.Equal([link, notes, stale], reset.Remaining);

        // The member rewrites notes.md; the link's target changes too, but the link itself does not.
        await WriteSinceAsync(notes);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(1));
        _deletes.Allow(notes);
        _deletes.Allow(stale);
        _deletes.Allow(link);
        var retried = Assert.Single(await _retry.RetryAsync(ct: Ct));

        Assert.True(retried.Finished);
        Assert.True(File.Exists(notes), "the retry removed a file the member rewrote after the reset");
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(link) || Directory.Exists(link));
        Assert.True(File.Exists(target));
        Assert.Null(await _unfinished.FindAsync(workspace, Ct));
    }

    [Fact]
    public async Task A_finished_reset_retry_removes_the_empty_directories_above_what_the_reset_named()
    {
        await CreateAlphaAsync();
        var workspace = _paths.WorkspaceFor(new ContainerId("Alpha", "Manager"));
        var deep = await WriteAsync(Path.Combine(workspace, "a", "sub", "g.bin"));
        await WriteAsync(Path.Combine(workspace, "a", "f1.bin"));
        _deletes.Refuse(deep);

        var reset = await _reset.ResetAsync(
            "Alpha", new TeamResetOptions(["Manager"], ForgetHistory: false, ClearWorkspaces: true), Ct);

        Assert.NotNull(reset);
        Assert.Equal([deep], reset.Remaining);

        _deletes.Allow(deep);
        var retried = Assert.Single(await _retry.RetryAsync(ct: Ct));

        Assert.True(retried.Finished);
        Assert.True(Directory.Exists(workspace), "the retry removed the reset's own folder");
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
        Assert.Null(await _unfinished.FindAsync(workspace, Ct));
    }

    [Fact]
    public async Task Creating_a_team_over_the_unfinished_removal_of_a_deleted_team_finishes_it_first()
    {
        await CreateAlphaAsync();
        var stuck = await WriteAsync(Path.Combine(AlphaRoot, "workspaces", "Manager", "stuck.bin"));
        _deletes.Refuse(stuck);
        await _deletion.DeleteAsync("Alpha", ct: Ct);

        // Still stuck: the create is refused and names what remains.
        var refused = await Assert.ThrowsAsync<ArgumentException>(CreateAlphaAsync);
        Assert.Contains(stuck, refused.Message);
        Assert.True(File.Exists(stuck));

        _deletes.Allow(stuck);
        await CreateAlphaAsync();

        Assert.False(File.Exists(stuck));
        Assert.True(File.Exists(AlphaMarker));
        Assert.Null(await _unfinished.FindAsync(AlphaRoot, Ct));
    }

    [Fact]
    public async Task Creating_a_team_over_an_unmarked_folder_is_refused_as_before()
    {
        var precious = await WriteAsync(Path.Combine(AlphaRoot, "precious.txt"));

        var refused = await Assert.ThrowsAsync<ArgumentException>(CreateAlphaAsync);

        Assert.Contains("already exists", refused.Message);
        Assert.True(File.Exists(precious));

        // Recorded as unfinished but stripped of its marker: still somebody's folder, still refused.
        await _unfinished.RecordAsync(
            new UnfinishedRemoval(AlphaRoot, RemovalKinds.TeamRoot, "Alpha", null, [precious], DateTimeOffset.UtcNow, 1), Ct);

        await Assert.ThrowsAsync<ArgumentException>(CreateAlphaAsync);
        Assert.True(File.Exists(precious));
    }

    private Task CreateAlphaAsync() =>
        _teams.CreateAsync("Alpha", "fake", memberAgents: ["fake"], ct: Ct);

    private static async Task<string> WriteAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "keep me", Ct);
        return path;
    }

    /// <summary>Written after a reset. The file system stamps times from a coarse clock that can
    /// lag the one the row's time is read from by a few milliseconds, so the time is set plainly
    /// after it.</summary>
    private static async Task<string> WriteSinceAsync(string path)
    {
        await WriteAsync(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The Host's deletes, failing for the paths it is told to refuse, and logging what
    /// each removal removed, in order; and its listing, failing for the directories it is told
    /// cannot be listed.</summary>
    private sealed class FlakyDeletes
    {
        private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unlistable = new(StringComparer.Ordinal);

        public List<string> Log { get; } = [];

        public FolderRemoval.HostDeletes Seam => new(
            path =>
            {
                Check(path);
                File.Delete(path);
                Log.Add(path);
            },
            path =>
            {
                Check(path);
                Directory.Delete(path, recursive: false);
                Log.Add(path);
            },
            path => _unlistable.Contains(path)
                ? throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.")
                : Directory.EnumerateFileSystemEntries(path));

        public void Refuse(string path) => _refused.Add(path);

        public void Allow(string path) => _refused.Remove(path);

        public void RefuseListing(string path) => _unlistable.Add(path);

        public void AllowListing(string path) => _unlistable.Remove(path);

        private void Check(string path)
        {
            if (_refused.Contains(path)) throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        }
    }
}

/// <summary>
/// THE REAL SWITCH, in the style of <c>AgentLaunchUserLaunchTests</c>: a team root holding an
/// agent-owned <c>0700</c> directory with files in it is removed completely when the Host launches
/// agents as another user (<c>nobody</c> here). The Host's own deletes refuse everything inside
/// that directory, as the product's Host (<c>harness</c>) cannot write in it, so the removal has
/// to go through the agent even when the suite runs as root. Skipped with the reason where this
/// process cannot switch users.
/// </summary>
public sealed class FolderRemovalAsTheAgentTests : IDisposable
{
    private const string Target = "nobody";

    private readonly string _parent = Directory.CreateTempSubdirectory("harness-removal-as-agent-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FolderRemovalAsTheAgentTests() => Open(_parent);

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task An_agent_owned_owner_only_directory_with_files_in_it_is_removed_completely()
    {
        var runAs = AgentLaunchUser.Resolve(Target);
        if (!runAs.Switches) Assert.Skip($"This process cannot switch users: {runAs.Reason}.");

        var root = Path.Combine(_parent, "teams", "Alpha");
        var workspace = Path.Combine(root, "workspaces", "Dev");
        Directory.CreateDirectory(workspace);
        Open(Path.Combine(_parent, "teams"), root, Path.Combine(root, "workspaces"), workspace);
        await TeamPaths.EnsureSkeletonAsync(root, "Alpha", Ct);

        // Made BY THE AGENT, owner-only, the way `dotnet test` and `mktemp` leave them.
        var agentDir = Path.Combine(workspace, "tmp");
        var made = Run([.. runAs.Prefix, "/bin/sh", "-c",
            $"umask 077 && mkdir -p '{agentDir}/nuget/v3' && echo x > '{agentDir}/a.txt' && echo y > '{agentDir}/nuget/v3/b.bin' && chmod 700 '{agentDir}' '{agentDir}/nuget' '{agentDir}/nuget/v3' && stat -c '%u %a' '{agentDir}'"]);
        Assert.Equal($"{runAs.Uid} 700", made);

        var inside = agentDir + Path.DirectorySeparatorChar;
        var refusing = new FolderRemoval.HostDeletes(
            path =>
            {
                if (path.StartsWith(inside, StringComparison.Ordinal)) throw new UnauthorizedAccessException(path);
                File.Delete(path);
            },
            path =>
            {
                if (path.StartsWith(inside, StringComparison.Ordinal)) throw new UnauthorizedAccessException(path);
                Directory.Delete(path, recursive: false);
            });

        // Without the switch the Host cannot finish, and the marker stays.
        var alone = await new FolderRemoval(host: refusing).RemoveTeamRootAsync(root, "Alpha", Ct);
        Assert.NotEmpty(alone.Remaining);
        Assert.True(File.Exists(TeamPaths.MarkerIn(root)));

        var report = await new FolderRemoval(runAs, host: refusing).RemoveTeamRootAsync(root, "Alpha", Ct);

        Assert.True(report.Complete, string.Join(", ", report.Remaining) + report.Refused);
        Assert.False(Directory.Exists(root));
    }

    private static void Open(params string[] directories)
    {
        foreach (var directory in directories) File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_111);
    }

    private static string Run(IReadOnlyList<string> argv)
    {
        var start = new System.Diagnostics.ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in argv.Skip(1)) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : $"exit {process.ExitCode}: {error}";
    }
}
