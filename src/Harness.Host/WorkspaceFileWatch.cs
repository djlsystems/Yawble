using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>One file a read saw move: its path relative to the repository, the project it belongs
/// to, and <c>created</c>, <c>changed</c> or <c>deleted</c>.</summary>
public sealed record WorkspaceFile(string Path, string Project, string Action);

/// <summary>One commit a read found: the 7-character sha, its subject's first line, its files.</summary>
public sealed record WorkspaceCommit(string Sha, string Subject, IReadOnlyList<WorkspaceFile> Files);

/// <summary>
/// The <c>workspaceChanged</c> push: what one read of one member's worktree found. <see cref="More"/>
/// is how many file changes and commits that read left out under the caps.
/// </summary>
public sealed record WorkspaceChange(
    string Team,
    string Member,
    string Repo,
    DateTimeOffset At,
    IReadOnlyList<WorkspaceFile> Files,
    IReadOnlyList<WorkspaceCommit> Commits,
    int More);

/// <summary>A member with a run in progress. A new <see cref="Correlation"/> is a new run.</summary>
public sealed record WorkspaceRun(string Team, string Member, long? Correlation);

/// <summary>Which members have a run in progress right now.</summary>
public interface IWorkspaceRuns
{
    IReadOnlyCollection<WorkspaceRun> Running();
}

/// <summary>The live containers' own answer: a snapshot in the Running state.</summary>
public sealed class HostWorkspaceRuns(ContainerHost host) : IWorkspaceRuns
{
    public IReadOnlyCollection<WorkspaceRun> Running() =>
        [.. host.Snapshots()
            .Where(s => s.State == ContainerState.Running)
            .Select(s => new WorkspaceRun(s.Team, s.Id, s.CurrentCorrelation))];
}

/// <summary>How often a running member's trees are read, and how often the loop looks for a run
/// starting or ending.</summary>
public sealed class WorkspaceFileWatchTiming
{
    public TimeSpan Read { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The pure half: reading git's output, comparing two reads, and naming a file's project.
/// </summary>
public static class WorkspaceFileChanges
{
    public const int MaxFiles = 50;
    public const int MaxCommits = 10;

    public const string Created = "created";
    public const string Changed = "changed";
    public const string Deleted = "deleted";

    private static readonly HashSet<string> ProjectFileNames = new(StringComparer.Ordinal)
    {
        "package.json", "go.mod", "Cargo.toml", "pyproject.toml", "setup.py", "pom.xml",
        "build.gradle", "build.gradle.kts",
    };

    private static readonly string[] ProjectFileExtensions = [".csproj", ".fsproj", ".vbproj", ".sln"];

    /// <summary>Whether a file's NAME makes its folder a project. The file is never opened.</summary>
    public static bool IsProjectFile(string path)
    {
        var name = NameOf(path);
        return ProjectFileNames.Contains(name)
            || ProjectFileExtensions.Any(ext => name.Length > ext.Length && name.EndsWith(ext, StringComparison.Ordinal));
    }

    /// <summary>Every folder holding a project file, relative to the repository; empty is its root.</summary>
    public static HashSet<string> ProjectFolders(IEnumerable<string> paths) =>
        new(paths.Where(IsProjectFile).Select(FolderOf), StringComparer.Ordinal);

    /// <summary>
    /// The nearest folder above the file that holds a project file, named by that folder, or the
    /// repository's name when that folder is its root. With none, the file's top-level folder; a
    /// file at the root is the repository's own.
    /// </summary>
    public static string ProjectOf(string path, IReadOnlySet<string> projectFolders, string repo)
    {
        var folder = FolderOf(path);

        while (true)
        {
            if (projectFolders.Contains(folder)) return folder.Length == 0 ? repo : NameOf(folder);
            if (folder.Length == 0) break;
            folder = FolderOf(folder);
        }

        var slash = path.IndexOf('/');
        return slash < 0 ? repo : path[..slash];
    }

    /// <summary>
    /// <c>git status --porcelain=v2 -z</c> as path to record. The record is the whole entry, so a
    /// path whose status line did not change compares equal and is not reported again. A rename's
    /// record carries its old path too, and is keyed by the new one.
    /// </summary>
    public static Dictionary<string, string> ParseStatus(string output)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokens = output.Split('\0');

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 3) continue;

            switch (token[0])
            {
                case '1':
                    if (Field(token, 9) is { } ordinary) entries[ordinary] = token;
                    break;
                case '2':
                    var original = i + 1 < tokens.Length ? tokens[++i] : "";
                    if (Field(token, 10) is { } renamed) entries[renamed] = token + "\0" + original;
                    break;
                case 'u':
                    if (Field(token, 11) is { } unmerged) entries[unmerged] = token;
                    break;
                case '?':
                    entries[token[2..]] = token[..1];
                    break;
            }
        }

        return entries;
    }

    /// <summary>
    /// What moved between two status reads. A path that appears or whose record changed is named by
    /// its new record; a path that left the status is named by what leaving means for its old one,
    /// unless a commit this read reports already covers it.
    /// </summary>
    public static List<(string Path, string Action)> Diff(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after,
        IReadOnlySet<string> committed)
    {
        var changes = new List<(string Path, string Action)>();

        foreach (var (path, record) in after)
        {
            if (!before.TryGetValue(path, out var was) || was != record) changes.Add((path, ActionOf(record)));
        }

        foreach (var (path, record) in before)
        {
            if (after.ContainsKey(path) || committed.Contains(path)) continue;
            changes.Add((path, LeftAction(record)));
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return changes;
    }

    /// <summary><c>git diff-tree --name-status -r -z</c> as (path, action): A and C are created, M and
    /// R (by the new path) changed, D deleted.</summary>
    public static List<(string Path, string Action)> ParseNameStatus(string output)
    {
        var files = new List<(string Path, string Action)>();
        var tokens = output.Split('\0');

        for (var i = 0; i < tokens.Length; i++)
        {
            var status = tokens[i];
            if (status.Length == 0) continue;

            if (status[0] is 'R' or 'C')
            {
                if (i + 2 >= tokens.Length) break;
                files.Add((tokens[i + 2], status[0] == 'C' ? Created : Changed));
                i += 2;
                continue;
            }

            if (i + 1 >= tokens.Length) break;
            files.Add((tokens[i + 1], status[0] switch { 'A' => Created, 'D' => Deleted, _ => Changed }));
            i += 1;
        }

        return files;
    }

    private static string ActionOf(string record)
    {
        if (record[0] == '?') return Created;
        if (record[0] == '2') return Changed;

        ReadOnlySpan<char> xy = record.Length >= 4 ? record.AsSpan(2, 2) : default;
        if (xy.Contains('D')) return Deleted;
        if (xy.Contains('A')) return Created;
        return Changed;
    }

    /// <summary>An untracked or added file that left the status without a commit is gone; a
    /// deleted one that left it was restored; anything else was put back as it was.</summary>
    private static string LeftAction(string record)
    {
        if (record[0] is '?' or '2') return Deleted;

        ReadOnlySpan<char> xy = record.Length >= 4 ? record.AsSpan(2, 2) : default;
        if (xy.Contains('D')) return Created;
        if (xy.Contains('A')) return Deleted;
        return Changed;
    }

    /// <summary>The last of <paramref name="count"/> space-separated fields, which may hold spaces.</summary>
    private static string? Field(string token, int count)
    {
        var parts = token.Split(' ', count);
        return parts.Length == count ? parts[^1] : null;
    }

    private static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// READS A RUNNING MEMBER'S OWN WORKTREES WHILE A PERSON WATCHES, and pushes what moved.
///
/// <para>
/// Only that member's per-card trees (<see cref="Worktrees.MemberOf"/>), never the main clone or
/// another member's, and only through <see cref="GitRunner"/>: <c>status --porcelain=v2</c> and
/// <c>HEAD</c>, then <c>rev-list</c> and <c>diff-tree</c> when HEAD moved. Paths only - never a
/// file's contents, a diff or a line.
/// </para>
///
/// <para>
/// THE FIRST READ OF A RUN IS THE BASELINE, so what was already dirty is not news. While nobody
/// watches nothing is read and every baseline is dropped, so the next person to watch starts from
/// a fresh one. A run that ends gets one last read, which catches a final commit.
/// </para>
///
/// <para>
/// IT WRITES NOTHING: no log row, no ledger entry, no tenant event. A failed read is skipped and
/// the next one compares against the last good read.
/// </para>
/// </summary>
public sealed class WorkspaceFileWatch(
    GitRunner git, TeamPaths paths, Func<WorkspaceChange, CancellationToken, Task> send, TimeProvider? clock = null)
{
    /// <summary>The hub group of people watching. Never a team group: a member's key can join those.</summary>
    public const string Group = "workspace-file-watch";

    public const string Message = "workspaceChanged";

    private readonly ConcurrentDictionary<string, byte> _watchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunState> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _watchEpoch;
    private long _seenEpoch;

    public bool Watched => !_watchers.IsEmpty;

    /// <summary>A person's connection started watching. Onto an empty group, every baseline is
    /// dropped so the reads start fresh.</summary>
    public void Join(string connectionId)
    {
        if (_watchers.IsEmpty) Interlocked.Increment(ref _watchEpoch);
        _watchers[connectionId] = 0;
    }

    public void Leave(string connectionId) => _watchers.TryRemove(connectionId, out _);

    /// <summary>
    /// One pass. A run that ended (or became another run) gets its last read; a run not yet known
    /// gets its baseline; with <paramref name="readAll"/>, every other known run is read. Returns how
    /// many pushes were sent.
    /// </summary>
    public async Task<int> PassAsync(IReadOnlyCollection<WorkspaceRun> running, bool readAll, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Watched)
            {
                _runs.Clear();
                return 0;
            }

            var epoch = Interlocked.Read(ref _watchEpoch);
            if (epoch != _seenEpoch)
            {
                _runs.Clear();
                _seenEpoch = epoch;
            }

            var current = new Dictionary<string, WorkspaceRun>(StringComparer.OrdinalIgnoreCase);
            foreach (var run in running) current[KeyOf(run.Team, run.Member)] = run;

            var sent = 0;

            foreach (var (key, state) in _runs.ToList())
            {
                if (current.TryGetValue(key, out var run) && run.Correlation == state.Run.Correlation) continue;

                sent += await ReadAsync(state, ct);
                _runs.Remove(key);
            }

            foreach (var (key, run) in current)
            {
                if (_runs.TryGetValue(key, out var state))
                {
                    if (readAll) sent += await ReadAsync(state, ct);
                    continue;
                }

                state = new RunState(run);
                await BaselineAsync(state, ct);
                _runs[key] = state;
            }

            return sent;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task BaselineAsync(RunState state, CancellationToken ct)
    {
        foreach (var (repo, path) in TreesOf(state.Run))
        {
            if (await ReadTreeAsync(path, ct) is not { } read) continue;

            state.Trees[path] = new TreeState(repo, read.Status, read.Head, await ProjectFoldersAsync(path, ct));
        }
    }

    private async Task<int> ReadAsync(RunState state, CancellationToken ct)
    {
        var sent = 0;
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (repo, path) in TreesOf(state.Run))
        {
            present.Add(path);

            // A tree cut after the baseline is compared with nothing: it was clean when it was cut,
            // so whatever is in it now is this run's.
            if (!state.Trees.TryGetValue(path, out var tree))
            {
                tree = new TreeState(repo, new Dictionary<string, string>(StringComparer.Ordinal), null,
                    await ProjectFoldersAsync(path, ct));
                state.Trees[path] = tree;
            }

            if (await ReadTreeAsync(path, ct) is not { } read) continue;

            var commits = new List<(string Sha, string Subject, List<(string Path, string Action)> Files)>();
            var commitsLeftOut = 0;

            if (tree.Head is { } before && read.Head is { } after && before != after)
            {
                (commits, commitsLeftOut) = await CommitsAsync(path, before, after, ct);
            }

            var committed = new HashSet<string>(commits.SelectMany(c => c.Files).Select(f => f.Path), StringComparer.Ordinal);
            var changes = WorkspaceFileChanges.Diff(tree.Status, read.Status, committed);

            tree.Status = read.Status;
            tree.Head = read.Head;

            foreach (var (file, action) in changes.Concat(commits.SelectMany(c => c.Files)))
            {
                if (!WorkspaceFileChanges.IsProjectFile(file)) continue;

                var folder = file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
                if (action == WorkspaceFileChanges.Deleted) tree.ProjectFolders.Remove(folder);
                else tree.ProjectFolders.Add(folder);
            }

            if (Shape(state.Run, tree, changes, commits, commitsLeftOut) is { } change)
            {
                await send(change, ct);
                sent++;
            }
        }

        foreach (var gone in state.Trees.Keys.Where(path => !present.Contains(path)).ToList()) state.Trees.Remove(gone);

        return sent;
    }

    /// <summary>The caps: at most <see cref="WorkspaceFileChanges.MaxFiles"/> files across the
    /// read, working-tree changes first; whatever is cut is counted in <c>more</c>.</summary>
    private WorkspaceChange? Shape(
        WorkspaceRun run, TreeState tree,
        List<(string Path, string Action)> changes,
        List<(string Sha, string Subject, List<(string Path, string Action)> Files)> commits,
        int commitsLeftOut)
    {
        if (changes.Count == 0 && commits.Count == 0 && commitsLeftOut == 0) return null;

        WorkspaceFile File((string Path, string Action) f) =>
            new(f.Path, WorkspaceFileChanges.ProjectOf(f.Path, tree.ProjectFolders, tree.Repo), f.Action);

        var budget = WorkspaceFileChanges.MaxFiles;
        var more = commitsLeftOut;

        var files = changes.Take(budget).Select(File).ToList();
        more += changes.Count - files.Count;
        budget -= files.Count;

        var shaped = new List<WorkspaceCommit>();
        foreach (var commit in commits)
        {
            var kept = commit.Files.Take(budget).Select(File).ToList();
            more += commit.Files.Count - kept.Count;
            budget -= kept.Count;
            shaped.Add(new WorkspaceCommit(commit.Sha, commit.Subject, kept));
        }

        return new WorkspaceChange(run.Team, run.Member, tree.Repo, _clock.GetUtcNow(), files, shaped, more);
    }

    /// <summary>
    /// The commits HEAD moved by: each of <c>rev-list old..new</c>, the newest
    /// <see cref="WorkspaceFileChanges.MaxCommits"/> of them oldest first, or the new HEAD alone when
    /// it does not descend from the old one.
    /// </summary>
    private async Task<(List<(string Sha, string Subject, List<(string Path, string Action)> Files)> Commits, int LeftOut)>
        CommitsAsync(string path, string before, string after, CancellationToken ct)
    {
        List<string> shas;
        var leftOut = 0;

        var descends = await git.RunGitAsync(path, ["merge-base", "--is-ancestor", before, after], ct);
        if (descends.ExitCode == 0)
        {
            var range = $"{before}..{after}";
            var counted = await git.RunGitAsync(path, ["rev-list", "--count", range], ct);
            var listed = await git.RunGitAsync(
                path, ["rev-list", $"--max-count={WorkspaceFileChanges.MaxCommits}", range], ct);
            if (listed.ExitCode != 0) return ([], 0);

            shas = [.. Lines(listed.Stdout).Reverse()];
            if (counted.ExitCode == 0
                && int.TryParse(counted.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var total))
            {
                leftOut = Math.Max(0, total - shas.Count);
            }
        }
        else
        {
            shas = [after];
        }

        var commits = new List<(string Sha, string Subject, List<(string Path, string Action)> Files)>();
        foreach (var sha in shas)
        {
            var message = await git.RunGitAsync(path, ["show", "-s", "--format=%B", sha], ct);
            var subject = message.ExitCode == 0 ? Lines(message.Stdout).FirstOrDefault() ?? "" : "";

            var tree = await git.RunGitAsync(
                path, ["diff-tree", "--no-commit-id", "--name-status", "-r", "--root", "-z", sha], ct);
            var files = tree.ExitCode == 0 ? WorkspaceFileChanges.ParseNameStatus(tree.Stdout) : [];

            commits.Add((sha.Length > 7 ? sha[..7] : sha, subject, files));
        }

        return (commits, leftOut);
    }

    private async Task<TreeRead?> ReadTreeAsync(string path, CancellationToken ct)
    {
        var status = await git.RunGitAsync(path, ["status", "--porcelain=v2", "-z", "--untracked-files=all"], ct);
        if (status.ExitCode != 0) return null;

        var head = await git.RunGitAsync(path, ["rev-parse", "--verify", "-q", "HEAD"], ct);

        return new TreeRead(
            WorkspaceFileChanges.ParseStatus(status.Stdout),
            head.ExitCode == 0 ? head.Stdout.Trim() : null);
    }

    /// <summary>The worktree's file names, once per tree: tracked and untracked, ignored left out.</summary>
    private async Task<HashSet<string>> ProjectFoldersAsync(string path, CancellationToken ct)
    {
        var listed = await git.RunGitAsync(path, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], ct);
        return WorkspaceFileChanges.ProjectFolders(
            listed.ExitCode == 0 ? listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries) : []);
    }

    /// <summary>This member's per-card trees in every repository of the team, by repository name.</summary>
    private List<(string Repo, string Path)> TreesOf(WorkspaceRun run)
    {
        string repos;
        try
        {
            repos = paths.ReposFor(run.Team);
        }
        catch (KeyNotFoundException)
        {
            return [];
        }

        if (!Directory.Exists(repos)) return [];

        return [.. Directory.EnumerateDirectories(repos)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .SelectMany(repo => paths.CardWorktreesIn(run.Team, repo)
                .Where(tree => string.Equals(tree.Member, run.Member, StringComparison.OrdinalIgnoreCase))
                .Select(tree => (repo, tree.Path)))];
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string KeyOf(string team, string member) => team + "\n" + member;

    private sealed record TreeRead(Dictionary<string, string> Status, string? Head);

    private sealed class RunState(WorkspaceRun run)
    {
        public WorkspaceRun Run { get; } = run;

        public Dictionary<string, TreeState> Trees { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TreeState(string repo, Dictionary<string, string> status, string? head, HashSet<string> projectFolders)
    {
        public string Repo { get; } = repo;

        public Dictionary<string, string> Status { get; set; } = status;

        public string? Head { get; set; } = head;

        public HashSet<string> ProjectFolders { get; } = projectFolders;
    }
}

/// <summary>
/// Drives <see cref="WorkspaceFileWatch"/>: every tick it looks for a run starting or ending, and
/// every <see cref="WorkspaceFileWatchTiming.Read"/> it reads every running member. While nobody
/// watches it asks for nothing.
/// </summary>
internal sealed class WorkspaceFileWatchService(
    WorkspaceFileWatch watch, IWorkspaceRuns runs, WorkspaceFileWatchTiming timing) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sinceRead = Stopwatch.StartNew();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(timing.Tick, stoppingToken);

                var due = sinceRead.Elapsed >= timing.Read;
                if (due) sinceRead.Restart();

                await watch.PassAsync(watch.Watched ? runs.Running() : [], due, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A pass that failed costs that pass's push; the next one compares against the last
                // good read. Nothing is written about it, by design: this feature writes nothing.
            }
        }
    }
}
