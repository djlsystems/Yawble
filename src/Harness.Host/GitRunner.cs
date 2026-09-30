using System.Diagnostics;
using System.Text;
namespace Harness.Host;

/// <summary>
/// A CLOSED SET of typed git operations. No caller-supplied command line. No extra flags.
/// Every invocation is bounded and kills the process tree on expiry.
/// One operation per clone at a time, locked on the clone path.
///
/// <para>
/// NO GIT THE HOST STARTS RUNS AS <c>harness</c> WITH ITS CAPABILITIES. The clones
/// belong to the agent, which can write their <c>.git/config</c> and hooks, and the global config
/// in the shared HOME. So:
/// </para>
/// <list type="bullet">
/// <item><c>git</c> itself comes from a root-owned system directory, never from PATH
/// (<see cref="SystemCommand"/>).</item>
/// <item>It is started through <see cref="AgentLaunchUser.Prefix"/>: as <c>agent</c> with no
/// capability when the Host switches, and with its capabilities cleared when it does not. That is
/// the boundary. Whatever config, hook, filter or helper still runs, runs as a user who could have
/// run it anyway.</item>
/// <item>Hooks and fsmonitor are switched off on the command line (<see cref="Hardening"/>), which
/// beats every config file. None of these operations needs one.</item>
/// <item>Its PATH is <see cref="SystemCommand.SafePath"/>, and no provider key is in its
/// environment. <c>GH_TOKEN</c> is handed only to an operation that talks to a GitHub remote the
/// HOST knows the clone's team has (<c>remotesFor</c>, or the URL being cloned) - that
/// team's members already hold it, so nothing an agent-written helper could read is new to it.</item>
/// </list>
/// </summary>
public sealed class GitRunner
{
    /// <summary>
    /// Always in front of the operation. A <c>-c</c> is read after every config file, so a repo or
    /// global setting cannot turn these back on. <c>protocol.ext.allow</c> is git's default already;
    /// it is pinned because the <c>ext::</c> transport runs a command.
    /// </summary>
    public static IReadOnlyList<string> Hardening { get; } =
    [
        "-c", "core.hooksPath=/dev/null",
        "-c", "core.fsmonitor=false",
        "-c", "protocol.ext.allow=never",
    ];

    private readonly string _gitExecutable;
    private readonly AgentLaunchUser? _runAs;
    private readonly Func<string, IReadOnlyList<string>>? _remotesFor;
    private readonly Func<string, string?>? _localOriginFor;
    private readonly int _timeoutSeconds;
    private readonly int _cloneTimeoutSeconds;
    private readonly Dictionary<string, SemaphoreSlim> _locksByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lockDictLock = new();
    private long _activity;

    /// <summary>
    /// HOW MANY GIT OPERATIONS THIS RUNNER HAS STARTED OR FINISHED, EVER. Monotonic, never reset,
    /// and a HEARTBEAT rather than a statistic: the only question anybody asks of it is whether it
    /// CHANGED between two readings, so nothing depends on the number itself.
    ///
    /// <para>
    /// IT EXISTS FOR <see cref="TerminalPublish"/>, whose bound on a dying container's publish has
    /// to tell "the origin is not answering" from "this machine is busy". Elapsed time cannot -
    /// a HEALTHY push of a dozen branches to a LOCAL origin can take well over ten seconds on a
    /// loaded host, and a fixed time bound would throw away the push-then-record ordering
    /// guarantee. This can:
    /// a runner still starting and finishing operations is a runner that is working.
    /// </para>
    ///
    /// <para>
    /// STARTS AS WELL AS COMPLETIONS, so a publish whose single long `push` is in flight still
    /// reads as having done something at the moment it began - and a `push` that then hangs reads
    /// as idle from that moment on, which is exactly the case the bound must still end.
    /// </para>
    /// </summary>
    public long Activity => Interlocked.Read(ref _activity);

    /// <param name="runAs">Who git runs as; see the class comment. Null runs it as the Host.</param>
    /// <param name="remotesFor">The remotes the Host has on record for the team a clone path
    /// belongs to. Decides whether a fetch or push gets <c>GH_TOKEN</c>. Null: never.</param>
    /// <param name="localOriginFor">The bare repository a clone path's origin is, by the Host's
    /// record, when that origin is a local repository (<see cref="LocalRepos"/>); null otherwise.
    /// Decides that a push goes into it as the Host rather than as the agent. Null: never.</param>
    public GitRunner(
        string gitExecutable = "git", int timeoutSeconds = 30, int cloneTimeoutSeconds = 600,
        AgentLaunchUser? runAs = null, Func<string, IReadOnlyList<string>>? remotesFor = null,
        Func<string, string?>? localOriginFor = null)
    {
        _gitExecutable = gitExecutable;
        _runAs = runAs;
        _remotesFor = remotesFor;
        _localOriginFor = localOriginFor;
        _timeoutSeconds = timeoutSeconds;
        _cloneTimeoutSeconds = cloneTimeoutSeconds;
    }

    /// <summary>
    /// The raw git facts: commit shas, dirty state, worktrees, FETCH_HEAD mtime, ahead/behind counts.
    /// </summary>
    public sealed record GitStatus(
        string? MainSha,
        string? TeamSha,
        bool Dirty,
        DateTime? OriginCheckedAt,
        int? MainAhead,
        int? MainBehind,
        string? HeadCheckout,
        IReadOnlyList<WorktreeInfo> Worktrees);

    public sealed record WorktreeInfo(
        string Path,
        string? Branch,
        string? Sha,
        int? Ahead = null,
        int? Behind = null);

    public sealed record GitInvocation(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Get the status: main and team branch shas, dirty state, worktrees, and freshness of origin.
    /// Caller must provide the team branch ref (e.g., "refs/heads/team/stored-id").
    ///
    /// "MAIN" HERE IS THE REPOSITORY'S DEFAULT BRANCH, <paramref name="defaultBranch"/>,
    /// and never a literal. Null is not known: the main sha, ahead/behind and every worktree's
    /// counts are then null (unknown) rather than measured against a guessed `main`.
    ///
    /// <paramref name="baseRemote"/> is the remote ahead/behind is measured against: `origin` for
    /// an owned repository, `upstream` for one in contributor mode, whose origin is a fork.
    /// </summary>
    public async Task<GitStatus> StatusAsync(
        string clonePath, string? defaultBranch, string? teamBranchRef = null, CancellationToken ct = default,
        string baseRemote = ContributorRemotes.Origin)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            // THE DIRECTORY NAMED `main` IS NOT A PROMISE ABOUT HEAD. MainSha and MainAhead
            // are facts about refs/heads/main. Measuring HEAD and labelling the result `main`
            // is how a clone sitting on team/<id> reported the team tip as main.
            var mainSha = defaultBranch is null
                ? null
                : await RevParseAsync(clonePath, $"refs/heads/{defaultBranch}", cts.Token);

            // Team branch sha (optional, caller must provide the ref)
            string? teamSha = null;
            if (teamBranchRef is not null)
            {
                teamSha = await RevParseAsync(clonePath, teamBranchRef, cts.Token);
            }

            // Dirty state
            var dirtyInvocation = await ExecuteGitAsync(clonePath, ["status", "--porcelain"], cts.Token);
            var dirty = !string.IsNullOrWhiteSpace(dirtyInvocation.Stdout);

            // FETCH_HEAD mtime
            var fetchHeadPath = Path.Combine(clonePath, ".git", "FETCH_HEAD");
            var originCheckedAt = File.Exists(fetchHeadPath)
                ? File.GetLastWriteTimeUtc(fetchHeadPath)
                : (DateTime?)null;

            // Worktrees. Ahead/behind are against local main, not origin: the question is
            // whether this tree is stale relative to what the team is building on.
            var worktreesInvocation = await ExecuteGitAsync(clonePath, ["worktree", "list", "--porcelain"], cts.Token);
            var parsed = ParseWorktrees(worktreesInvocation.Stdout, clonePath);
            var worktrees = defaultBranch is null
                ? parsed
                : await CountWorktreesAsync(clonePath, parsed, defaultBranch, cts.Token);

            // Ahead/behind: local default branch vs its base remote's tracking ref, never HEAD.
            (int? ahead, int? behind) = defaultBranch is null
                ? (null, null)
                : await GetAheadBehindAsync(
                    clonePath, $"refs/heads/{defaultBranch}", $"refs/remotes/{baseRemote}/{defaultBranch}", cts.Token);

            var headCheckout = await HeadCheckoutAsync(clonePath, cts.Token);

            return new GitStatus(
                MainSha: mainSha?.Trim(),
                TeamSha: teamSha?.Trim(),
                Dirty: dirty,
                OriginCheckedAt: originCheckedAt,
                MainAhead: ahead,
                MainBehind: behind,
                HeadCheckout: headCheckout,
                Worktrees: worktrees);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>
    /// Clone <paramref name="url"/> into <paramref name="leafName"/> under
    /// <paramref name="parentPath"/>.
    ///
    /// THE PARENT IS THE WORKING DIRECTORY, AND THAT IS NOT A DETAIL. Every other operation here runs
    /// with the clone as its working directory, which cannot work for the one operation that CREATES
    /// it - `Process.Start` throws on a working directory that is not there. So the target arrives
    /// as an argument and git makes it.
    ///
    /// BOUNDED FAR LONGER THAN EVERYTHING ELSE, and the asymmetry is the point: every other call on
    /// this class is incremental against a repository that is already here, while this one transfers
    /// a whole history over a network. Thirty seconds is right for a `fetch` and would fail this on
    /// any repository worth cloning.
    /// </summary>
    ///
    /// <para>
    /// <paramref name="noLocal"/> for a local repository's folder: <c>--no-local</c> makes git copy
    /// through its transport instead of hard-linking the bare repository's object files into the
    /// agent's clone, where a later ownership pass over the team's folder would hand those shared
    /// inodes - the Host's objects - to the agent.
    /// </para>
    /// </summary>
    public async Task<GitInvocation> CloneAsync(
        string parentPath, string url, string leafName, CancellationToken ct = default, bool noLocal = false)
    {
        using var semaphore = await AcquireSemaphoreAsync(Path.Combine(parentPath, leafName));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_cloneTimeoutSeconds));

        return await ExecuteGitAsync(
            parentPath, noLocal ? ["clone", "--no-local", "--", url, leafName] : ["clone", url, leafName], cts.Token);
    }

    public async Task<GitInvocation> FetchAsync(string clonePath, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await ExecuteGitAsync(clonePath, ["fetch", "origin"], cts.Token);
    }

    /// <summary>
    /// <c>git fetch upstream</c>: a contributor-mode clone's second remote. Moves only
    /// <c>refs/remotes/upstream/*</c>, like <see cref="FetchAsync"/> moves only origin's.
    /// </summary>
    public async Task<GitInvocation> FetchUpstreamAsync(string clonePath, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await ExecuteGitAsync(clonePath, ["fetch", ContributorRemotes.Upstream], cts.Token);
    }

    /// <summary>
    /// Brings the clone's <c>upstream</c> remote in line with <paramref name="url"/>: adds
    /// it, re-points it, or - with null - removes it. Touches nothing else: not origin, not a local
    /// branch, not a worktree. Removing a remote also drops its own tracking refs, which is git's
    /// meaning of removing one. The URL stored is read from config, not <c>get-url</c>, so an
    /// <c>insteadOf</c> rewrite does not read as a different URL. Answers the failing invocation,
    /// or null when the remote already matched or was brought in line.
    /// </summary>
    public async Task<GitInvocation?> SetUpstreamRemoteAsync(string clonePath, string? url, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var current = await ExecuteGitAsync(
            clonePath, ["config", "--get", $"remote.{ContributorRemotes.Upstream}.url"], cts.Token);
        var existing = current.ExitCode == 0 ? current.Stdout.Trim() : null;

        GitInvocation run;
        if (url is null)
        {
            if (existing is null) return null;
            run = await ExecuteGitAsync(clonePath, ["remote", "remove", ContributorRemotes.Upstream], cts.Token);
        }
        else if (existing is null)
        {
            run = await ExecuteGitAsync(clonePath, ["remote", "add", ContributorRemotes.Upstream, url], cts.Token);
        }
        else if (!string.Equals(existing, url, StringComparison.Ordinal))
        {
            run = await ExecuteGitAsync(clonePath, ["remote", "set-url", ContributorRemotes.Upstream, url], cts.Token);
        }
        else
        {
            return null;
        }

        return run.ExitCode == 0 ? null : run;
    }

    /// <summary>
    /// How many commits <paramref name="reference"/> has that <paramref name="basis"/> does not:
    /// <c>git rev-list --count basis..reference</c>. Null when git cannot answer (a ref missing).
    /// </summary>
    public async Task<int?> CountCommitsNotInAsync(
        string clonePath, string reference, string basis, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var run = await ExecuteGitAsync(clonePath, ["rev-list", "--count", $"{basis}..{reference}"], cts.Token);
        return run.ExitCode == 0 && int.TryParse(run.Stdout.Trim(), out var count) ? count : null;
    }

    /// <summary>
    /// The branch origin's HEAD names: <c>git remote set-head origin --auto</c>, then the
    /// branch <c>refs/remotes/origin/HEAD</c> points at. NULL WHEN EITHER STEP FAILS OR HEAD NAMES
    /// NO BRANCH - that is not known, and nothing here substitutes `main` for it.
    /// </summary>
    public async Task<string?> ReadOriginHeadBranchAsync(string clonePath, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var setHead = await ExecuteGitAsync(clonePath, ["remote", "set-head", "origin", "--auto"], cts.Token);
        if (setHead.ExitCode != 0) return null;

        return await OriginHeadBranchAsync(clonePath, cts.Token);
    }

    /// <summary>
    /// The branch <c>refs/remotes/origin/HEAD</c> ALREADY names in this clone, asking nobody: what
    /// <c>git clone</c> or the last <c>set-head</c> recorded. Used at start, where a clone
    /// with no stored default branch has to be read without a network round trip per
    /// repository before anything else can answer. Null when the ref is missing or names no branch.
    /// </summary>
    public async Task<string?> ReadRecordedOriginHeadBranchAsync(string clonePath, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await OriginHeadBranchAsync(clonePath, cts.Token);
    }

    private async Task<string?> OriginHeadBranchAsync(string clonePath, CancellationToken ct)
    {
        var head = await ExecuteGitAsync(
            clonePath, ["symbolic-ref", "--quiet", "refs/remotes/origin/HEAD"], ct);
        if (head.ExitCode != 0) return null;

        const string prefix = "refs/remotes/origin/";
        var target = head.Stdout.Trim();
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var branch = target[prefix.Length..];
        return BranchNames.IsValid(branch) ? branch : null;
    }

    public async Task<GitInvocation> LsRemoteAsync(string clonePath, string url, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await ExecuteGitAsync(clonePath, ["ls-remote", "--exit-code", url], cts.Token);
    }

    public async Task<GitInvocation> MergeFastForwardAsync(string clonePath, string @ref, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await ExecuteGitAsync(clonePath, ["merge", "--ff-only", @ref], cts.Token);
    }

    public async Task<GitInvocation> PushAsync(string clonePath, string @ref, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        if (_localOriginFor?.Invoke(clonePath) is { } bare)
        {
            return await PushIntoLocalAsync(clonePath, bare, @ref, @ref, cts.Token);
        }

        return await ExecuteGitAsync(clonePath, ["push", "origin", @ref], cts.Token);
    }

    /// <summary>
    /// Push <paramref name="source"/> to origin as <paramref name="destination"/> by name.
    /// The server refuses unless the update is a fast-forward. Does not consult HEAD.
    /// </summary>
    public async Task<GitInvocation> PushRefspecAsync(
        string clonePath, string source, string destination, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        if (_localOriginFor?.Invoke(clonePath) is { } bare)
        {
            return await PushIntoLocalAsync(clonePath, bare, source, destination, cts.Token);
        }

        return await ExecuteGitAsync(clonePath, ["push", "origin", $"{source}:{destination}"], cts.Token);
    }

    /// <summary>
    /// A PUSH TO A LOCAL ORIGIN, made by the Host from the bare repository's side.
    ///
    /// <para>
    /// The bare repository is the Host's and never writable by the agent, and every other git here
    /// runs as the agent - so <c>git push</c> could not write it, and making it writable would let
    /// any agent push. Instead the commit is resolved in the clone (as the agent), and the Host, as
    /// itself with no capability and no global config, FETCHES it into the bare repository:
    /// <c>git fetch --no-tags &lt;clone&gt; &lt;sha&gt;:refs/heads/&lt;branch&gt;</c>. Without a <c>+</c> the fetch
    /// refuses anything that is not a fast-forward - <c>! [rejected] … (non-fast-forward)</c>, as a
    /// push would say - so nothing is ever forced. A delete is <c>update-ref -d</c> against the
    /// value it was read at. Then the clone's <c>refs/remotes/origin/&lt;branch&gt;</c> is moved as a
    /// push would move it. It returns only once the bare repository holds the branch, so the
    /// push-then-record ordering (<c>PublishOrderTests</c>) holds as it does for a remote.
    /// </para>
    ///
    /// <para>
    /// The clone's side of that fetch is <c>git upload-pack</c> running in the agent's clone as the
    /// Host. upload-pack is the half of git built to serve an untrusted repository, the hardening
    /// flags reach it through the environment, and the only config it honours for a command
    /// (<c>uploadpack.packObjectsHook</c>) is read from system and command-line config only.
    /// </para>
    /// </summary>
    private async Task<GitInvocation> PushIntoLocalAsync(
        string clonePath, string bare, string source, string destination, CancellationToken ct)
    {
        var target = destination.StartsWith("refs/", StringComparison.Ordinal) ? destination : "refs/heads/" + destination;
        var branch = target.StartsWith("refs/heads/", StringComparison.Ordinal) ? target["refs/heads/".Length..] : null;
        if (branch is null || !BranchNames.IsValid(branch))
        {
            return new GitInvocation(1, string.Empty, $"error: '{destination}' is not a branch; only branches are published to a local repository.");
        }

        var tracking = $"refs/remotes/{ContributorRemotes.Origin}/{branch}";
        using var bareLock = await AcquireSemaphoreAsync(bare);

        if (source.Length == 0)
        {
            var had = await ExecuteGitAsync(bare, ["rev-parse", "--verify", "--quiet", target], ct, asHost: true);
            if (had.ExitCode != 0)
            {
                return new GitInvocation(
                    1, string.Empty,
                    $"error: unable to delete '{branch}': remote ref does not exist\nerror: failed to push some refs to '{bare}'\n");
            }

            var deleted = await ExecuteGitAsync(bare, ["update-ref", "-d", target, had.Stdout.Trim()], ct, asHost: true);
            if (deleted.ExitCode != 0) return deleted;

            await ExecuteGitAsync(clonePath, ["update-ref", "-d", tracking], ct);
            return new GitInvocation(0, string.Empty, $"To {bare}\n - [deleted]         {branch}\n");
        }

        var resolved = await ExecuteGitAsync(clonePath, ["rev-parse", "--verify", "--quiet", "--end-of-options", source + "^{commit}"], ct);
        if (resolved.ExitCode != 0)
        {
            return new GitInvocation(
                1, string.Empty, $"error: src refspec {source} does not match any\nerror: failed to push some refs to '{bare}'\n");
        }

        var sha = resolved.Stdout.Trim();
        var fetched = await ExecuteGitAsync(
            bare,
            ["fetch", "--no-tags", "--no-write-fetch-head", "--", Path.GetFullPath(clonePath), $"{sha}:{target}"],
            ct, asHost: true);
        if (fetched.ExitCode != 0) return fetched;

        await ExecuteGitAsync(clonePath, ["update-ref", tracking, sha], ct);
        return fetched;
    }

    /// <summary>
    /// Makes a new bare repository at <paramref name="path"/> (inside <paramref name="root"/>),
    /// readable by the agent's group and never writable by it (<c>--shared=0640</c>), whose HEAD is
    /// <paramref name="branch"/> and which holds one empty commit on it. As the Host. Answers null,
    /// or git's first line when it refused.
    /// </summary>
    public async Task<string?> CreateLocalRepositoryAsync(
        string root, string path, string branch, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var init = await ExecuteGitAsync(
            root, ["init", "--quiet", "--bare", "--shared=0640", "-b", branch, "--", path], cts.Token, asHost: true);
        if (init.ExitCode != 0) return FirstLineOf(init);

        // The empty tree is known to every git without being stored.
        var commit = await ExecuteGitAsync(
            path, ["commit-tree", "-m", "Initial commit", EmptyTree], cts.Token, asHost: true,
            environment: new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = PlatformIdentity, ["GIT_AUTHOR_EMAIL"] = PlatformEmail,
                ["GIT_COMMITTER_NAME"] = PlatformIdentity, ["GIT_COMMITTER_EMAIL"] = PlatformEmail,
            });
        if (commit.ExitCode != 0) return FirstLineOf(commit);

        var update = await ExecuteGitAsync(
            path, ["update-ref", $"refs/heads/{branch}", commit.Stdout.Trim(), ""], cts.Token, asHost: true);
        return update.ExitCode != 0 ? FirstLineOf(update) : null;
    }

    /// <summary>The branch a local repository's HEAD names, as the Host reads it; null when it names
    /// none git would take.</summary>
    public async Task<string?> ReadLocalRepositoryHeadAsync(string path, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var head = await ExecuteGitAsync(path, ["symbolic-ref", "--quiet", "HEAD"], cts.Token, asHost: true);
        var target = head.Stdout.Trim();
        if (head.ExitCode != 0 || !target.StartsWith("refs/heads/", StringComparison.Ordinal)) return null;

        var branch = target["refs/heads/".Length..];
        return BranchNames.IsValid(branch) ? branch : null;
    }

    /// <summary>The newest commit on <paramref name="branch"/> in a local repository; null when it
    /// has none.</summary>
    public async Task<LocalRepoCommit?> ReadLocalRepositoryTipAsync(
        string path, string branch, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var log = await ExecuteGitAsync(
            path, ["log", "-1", "--format=%H%x1f%cI%x1f%s", $"refs/heads/{branch}", "--"], cts.Token, asHost: true);
        var parts = log.Stdout.TrimEnd('\r', '\n').Split('\u001f');
        if (log.ExitCode != 0 || parts.Length != 3) return null;

        return new LocalRepoCommit(
            parts[0], parts[2],
            DateTimeOffset.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var at) ? at : null);
    }

    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const string PlatformIdentity = "Platform";
    private const string PlatformEmail = "platform@localhost";

    private static string FirstLineOf(GitInvocation run) =>
        (run.Stderr + "\n" + run.Stdout)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "git failed and said nothing.";

    /// <summary>
    /// Every LOCAL branch in this clone, by short name, in git's own ref order.
    ///
    /// <para>
    /// A TYPED OPERATION RATHER THAN A CALL TO <see cref="RunGitAsync"/>, because its one caller is
    /// the acceptance publisher and what that caller pushes is decided by what this returns. A
    /// caller-supplied argument list there is a caller-supplied answer to "which refs go to origin",
    /// which is exactly the decision this class exists to keep in one auditable place.
    /// </para>
    ///
    /// <para>
    /// <c>refs/heads/</c> AND NOTHING ELSE. Remote-tracking refs and tags are not branches this
    /// clone owns, and a listing that included <c>refs/remotes/</c> would hand a pusher the names of
    /// things already on a remote.
    /// </para>
    ///
    /// <para>
    /// EMPTY ON ANY FAILURE, never an exception: a clone that cannot be read publishes nothing,
    /// which is the recoverable direction. The <see cref="GitInvocation"/> is not returned because
    /// there is no arm that would read it - a caller that cannot list branches has nothing to push.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> LocalBranchesAsync(
        string clonePath, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var result = await ExecuteGitAsync(
            clonePath, ["for-each-ref", "--format=%(refname:short)", "refs/heads/"], cts.Token);

        return result.ExitCode != 0
            ? []
            : result.Stdout.Split(
                ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Fast-forward a local ref from another local ref without checking it out:
    /// <c>git fetch . source:destination</c>. Git refuses unless it is a fast-forward, and
    /// refuses to fetch into the currently checked-out branch.
    /// </summary>
    public async Task<GitInvocation> FetchRefspecAsync(
        string clonePath, string source, string destination, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await ExecuteGitAsync(clonePath, ["fetch", ".", $"{source}:{destination}"], cts.Token);
    }

    /// <summary>
    /// Three-way merge of <paramref name="ours"/> and <paramref name="theirs"/> ENTIRELY IN THE
    /// OBJECT DATABASE: <c>git merge-tree --write-tree</c> writes the merged tree and prints its
    /// oid on the first line of stdout. HEAD, the index and the working tree are never touched,
    /// which is the whole reason this is how <c>MergeToMainAsync</c> builds a merge commit rather
    /// than checking a branch out and running <c>git merge</c>.
    ///
    /// THERE IS NOTHING TO ABORT WHEN THIS CONFLICTS, and that is a property of the mechanism
    /// rather than of the discipline around it. <c>RebaseRepoAsync</c> has to run <c>--abort</c>
    /// on every failing path, including one reached only by a cancellation, because a rebase that
    /// stops halfway leaves `.git/rebase-merge` and a half-replayed tree behind. This leaves a few
    /// unreferenced objects and nothing else, so the "clean tree on a conflict" guarantee cannot
    /// be lost by somebody adding a return path that forgets to clean up.
    ///
    /// EXIT 1 MEANS TWO DIFFERENT THINGS AND THE CALLER MUST TELL THEM APART: a real conflict
    /// (first line is still a tree oid, the rest names the conflicted paths) and a refusal to
    /// merge at all - an unresolvable ref, a git too old for <c>--write-tree</c> - where stdout
    /// carries no oid. Read the first line, do not read the exit code alone.
    ///
    /// <c>--name-only</c> so the conflict section names PATHS rather than printing a stage line of
    /// blob oids per path: this output reaches a person and a permanent tenant_events row.
    ///
    /// Needs git 2.38 or newer, which is when <c>--write-tree</c> arrived. An older git answers
    /// non-zero with its own "unknown option" on stderr, which lands in the same "could not run
    /// the merge" arm as any other refusal and names the real cause.
    /// </summary>
    public async Task<GitInvocation> MergeTreeAsync(
        string clonePath, string ours, string theirs, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await ExecuteGitAsync(
            clonePath, ["merge-tree", "--write-tree", "--name-only", ours, theirs], cts.Token);
    }

    /// <summary>
    /// Writes a two-parent commit object for <paramref name="tree"/> and prints its sha. Moves no
    /// ref: the commit is unreferenced until something pushes or fast-forwards to it, so a caller
    /// that gives up after this point leaves only a loose object for the next <c>git gc</c>.
    ///
    /// PARENT ORDER IS THE CALLER'S AND IT MATTERS. First parent is the side being merged INTO -
    /// for <c>MergeToMainAsync</c> that is <c>origin/main</c>, so main's first-parent history
    /// stays main's own.
    ///
    /// IDENTITY COMES FROM THE CLONE'S OWN GIT CONFIG, never from anything set here. A repository
    /// with no <c>user.email</c> fails with git's own "Please tell me who you are", which is both
    /// the true cause and an actionable one; inventing a committer would attribute somebody's
    /// merge to a name nobody chose. The failure lands BEFORE the push, so nothing is left behind.
    /// </summary>
    public async Task<GitInvocation> CommitTreeAsync(
        string clonePath,
        string tree,
        string firstParent,
        string secondParent,
        string subject,
        string body,
        CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await ExecuteGitAsync(
            clonePath,
            ["commit-tree", tree, "-p", firstParent, "-p", secondParent, "-m", subject, "-m", body],
            cts.Token);
    }

    public async Task<GitInvocation> RemoveWorktreeAsync(string clonePath, string worktreePath, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await ExecuteGitAsync(clonePath, ["worktree", "remove", worktreePath], cts.Token);
    }

    public async Task<GitInvocation> PruneWorktreesAsync(string clonePath, CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await ExecuteGitAsync(clonePath, ["worktree", "prune"], cts.Token);
    }

    /// <summary>
    /// Public helper for direct git operations - used by readers for merge-base checks and other queries.
    /// </summary>
    public async Task<GitInvocation> RunGitAsync(string clonePath, IReadOnlyList<string> args, CancellationToken ct)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        return await ExecuteGitAsync(clonePath, args, cts.Token);
    }

    /// <summary>
    /// How many commits reachable from <paramref name="reference"/> are reachable from NO
    /// remote-tracking ref in this clone.
    /// </summary>
    public async Task<int?> CountCommitsNotOnAnyRemoteAsync(
        string clonePath,
        string reference,
        CancellationToken ct = default)
    {
        using var semaphore = await AcquireSemaphoreAsync(clonePath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var result = await ExecuteGitAsync(
            clonePath,
            ["rev-list", "--count", reference, "--not", "--remotes"],
            cts.Token);

        return result.ExitCode == 0 && int.TryParse(result.Stdout.Trim(), out var count)
            ? count
            : null;
    }

    /// <summary>
    /// Whether local <paramref name="branch"/> is ahead of origin's copy, or origin has none: what a
    /// fast-forward push would publish. False when origin has it, or holds commits the clone lacks.
    /// Asked of the remote-tracking ref, so it is as fresh as the last fetch or push.
    ///
    /// <para>
    /// NOT THE SAME QUESTION AS <see cref="CountCommitsNotOnAnyRemoteAsync"/>. A branch moved to a
    /// commit origin already holds under another name - a Manager fast-forwarding the team branch
    /// to a member's pushed tip - has no commit missing from every remote, yet origin's own branch
    /// of that name is still where it was.
    /// </para>
    /// </summary>
    public async Task<bool> BranchAheadOfOriginAsync(string clonePath, string branch, CancellationToken ct = default)
    {
        var local = $"refs/heads/{branch}";
        var remote = $"refs/remotes/origin/{branch}";
        if ((await RunGitAsync(clonePath, ["rev-parse", "--verify", "--quiet", remote], ct)).ExitCode != 0)
        {
            return true;
        }

        if ((await RunGitAsync(clonePath, ["merge-base", "--is-ancestor", local, remote], ct)).ExitCode == 0)
        {
            return false;
        }

        return (await RunGitAsync(clonePath, ["merge-base", "--is-ancestor", remote, local], ct)).ExitCode == 0;
    }

    // Private helpers

    private async Task<string?> RevParseAsync(string clonePath, string @ref, CancellationToken ct)
    {
        var result = await ExecuteGitAsync(clonePath, ["rev-parse", @ref], ct);
        return result.ExitCode == 0 ? result.Stdout.Trim() : null;
    }

    /// <summary>
    /// The checked-out branch, or <c>detached</c> when HEAD is not a branch. Never a number:
    /// a detached checkout cannot be attributed to a ref the way an ahead count can.
    /// </summary>
    private async Task<string?> HeadCheckoutAsync(string clonePath, CancellationToken ct)
    {
        var result = await ExecuteGitAsync(clonePath, ["rev-parse", "--abbrev-ref", "HEAD"], ct);
        if (result.ExitCode != 0)
            return null;

        var name = result.Stdout.Trim();
        if (name.Length == 0)
            return null;

        return name == "HEAD" ? "detached" : name;
    }

    private async Task<(int?, int?)> GetAheadBehindAsync(string clonePath, string local, string remote, CancellationToken ct)
    {
        var result = await ExecuteGitAsync(clonePath, ["rev-list", "--left-right", "--count", $"{remote}...{local}"], ct);
        if (result.ExitCode != 0)
            return (null, null);

        var parts = result.Stdout.Trim().Split('\t');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var behind) || !int.TryParse(parts[1], out var ahead))
            return (null, null);

        return (ahead, behind);
    }

    /// <summary>
    /// One <c>rev-list --left-right --count &lt;branch&gt;...refs/heads/&lt;default&gt;</c> per worktree.
    /// A detached worktree has no branch and stays unknown.
    /// </summary>
    private async Task<IReadOnlyList<WorktreeInfo>> CountWorktreesAsync(
        string clonePath, IReadOnlyList<WorktreeInfo> worktrees, string defaultBranch, CancellationToken ct)
    {
        var counted = new List<WorktreeInfo>(worktrees.Count);
        foreach (var wt in worktrees)
        {
            if (wt.Branch is null)
            {
                counted.Add(wt);
                continue;
            }

            var result = await ExecuteGitAsync(
                clonePath,
                ["rev-list", "--left-right", "--count", $"{wt.Branch}...refs/heads/{defaultBranch}"],
                ct);
            if (result.ExitCode != 0)
            {
                counted.Add(wt);
                continue;
            }

            var parts = result.Stdout.Trim().Split('\t');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var ahead)
                || !int.TryParse(parts[1], out var behind))
            {
                counted.Add(wt);
                continue;
            }

            counted.Add(wt with { Ahead = ahead, Behind = behind });
        }

        return counted;
    }

    private IReadOnlyList<WorktreeInfo> ParseWorktrees(string porcelain, string clonePath)
    {
        var worktrees = new List<WorktreeInfo>();
        var lines = porcelain.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        // Normalize clonePath for comparison: same separator and resolved path
        var normalizedClonePath = Path.GetFullPath(clonePath).Replace('/', Path.DirectorySeparatorChar);

        string? currentPath = null;
        string? currentBranch = null;
        string? currentSha = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (currentPath is not null)
                {
                    // Normalize currentPath: same separator and resolved path
                    var normalizedCurrentPath = Path.GetFullPath(currentPath).Replace('/', Path.DirectorySeparatorChar);
                    if (!normalizedCurrentPath.Equals(normalizedClonePath, StringComparison.OrdinalIgnoreCase))
                    {
                        worktrees.Add(new WorktreeInfo(currentPath, currentBranch, currentSha));
                    }
                }
                currentPath = line[9..].Trim();
                currentBranch = null;
                currentSha = null;
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                currentBranch = line[7..].Trim();
                if (currentBranch.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    currentBranch = currentBranch[11..];
                }
            }
            else if (line.StartsWith("detached", StringComparison.Ordinal))
            {
                currentBranch = null;
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                currentSha = line[5..].Trim();
            }
        }

        // Add the last one
        if (currentPath is not null)
        {
            var normalizedCurrentPath = Path.GetFullPath(currentPath).Replace('/', Path.DirectorySeparatorChar);
            if (!normalizedCurrentPath.Equals(normalizedClonePath, StringComparison.OrdinalIgnoreCase))
            {
                worktrees.Add(new WorktreeInfo(currentPath, currentBranch, currentSha));
            }
        }

        return worktrees;
    }

    /// <param name="asHost">Run as the Host itself - for the Host's own bare repositories only
    /// (<see cref="LocalRepos"/>) - with every capability cleared and no global config, which lives
    /// in the agent's HOME.</param>
    private async Task<GitInvocation> ExecuteGitAsync(
        string clonePath, IReadOnlyList<string> args, CancellationToken ct,
        bool asHost = false, IReadOnlyDictionary<string, string>? environment = null)
    {
        // THE HEARTBEAT, AT THE ONE PLACE EVERY OPERATION ON THIS CLASS FUNNELS THROUGH. See
        // `Activity`. Ticked on the way in and, in the `finally`, on every way out - including the
        // cancellation `WaitForExitAsync` rethrows, because an operation that timed out is still an
        // operation that stopped happening and the next window must be able to see that it did.
        Interlocked.Increment(ref _activity);

        try
        {
            // FAIL CLOSED: every clone this runs in is the agent's, and so is the global
            // config in the shared HOME. When the agent user exists but the Host cannot switch to
            // it, git would read agent-written config (filters, credential.helper) as the Host.
            // 126: "found but cannot be run", the shell's code for it.
            if (_runAs is { Refuses: true })
            {
                return new GitInvocation(126, string.Empty, _runAs.Refusal("Host git"));
            }

            // From a root-owned system directory, never PATH - see the class comment. No fallback
            // to the bare name: Process.Start would search PATH for it.
            if (SystemCommand.Find(_gitExecutable) is not { } executable)
            {
                return new GitInvocation(
                    127,
                    string.Empty,
                    $"git could not be started ('{_gitExecutable}'): it is not in a root-owned system directory "
                    + $"({string.Join(", ", SystemCommand.Directories)}). Install git there, or set GitExecutable to its full path.");
            }

            var prefix = asHost ? _runAs?.HostPrefix ?? [] : _runAs?.Prefix ?? [];
            var start = new ProcessStartInfo(prefix.Count > 0 ? prefix[0] : executable)
            {
                WorkingDirectory = clonePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var part in prefix.Skip(1)) start.ArgumentList.Add(part);
            if (prefix.Count > 0) start.ArgumentList.Add(executable);
            foreach (var part in Hardening) start.ArgumentList.Add(part);
            foreach (var arg in args) start.ArgumentList.Add(arg);

            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_ASKPASS"] = "";
            start.Environment["SSH_ASKPASS"] = "";
            start.Environment["PATH"] = SystemCommand.SafePath;

            // No provider key, and GitHub's only where the Host itself says this is a GitHub team.
            foreach (var variable in AgentEnvironment.ProviderVariables) start.Environment.Remove(variable);
            start.Environment.Remove(AgentEnvironment.GitHubVariable);
            if (!asHost && GitHubTokenFor(clonePath, args) is { } token) start.Environment[AgentEnvironment.GitHubVariable] = token;

            if (asHost)
            {
                // The global config is the agent's (the shared HOME): the Host reads none of it.
                start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
                foreach (var variable in new[] { "XDG_CONFIG_HOME", "GIT_DIR", "GIT_WORK_TREE" }) start.Environment.Remove(variable);
            }

            foreach (var (key, value) in environment ?? new Dictionary<string, string>()) start.Environment[key] = value;

            // A MISSING GIT IS A REPORTED OUTCOME, NOT AN EXCEPTION, and this is the whole reason the
            // dashboard can render a prerequisite instead of a stack trace.
            //
            // `Process.Start` throws `Win32Exception` when the executable is not there, and every method
            // on this class funnels through here - so without this catch, ONE missing binary turns every
            // repo-status read into a 500 and the panel that exists to say "git is not installed" is the
            // thing that cannot load. `MemberDeletion` learned the same lesson from the other side: it
            // caught the exception and reported "git worktree prune failed", which sent the reader to
            // look at a worktree when the machine was simply unfinished.
            //
            // Exit 127 deliberately: the shell convention for "command not found", which is the closest
            // honest thing to what happened and is not a code git itself returns.
            Process? started;
            try
            {
                started = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                return new GitInvocation(
                    127,
                    string.Empty,
                    $"git could not be started ('{_gitExecutable}'): {exception.Message} "
                    + "Install git and put it on PATH, or set GitExecutable.");
            }

            using var process = started
                ?? throw new InvalidOperationException("The git process did not start.");

            // Pump both streams before waiting
            var stdoutText = new StringBuilder();
            var stderrText = new StringBuilder();
            var stdoutTask = PumpAsync(process.StandardOutput, stdoutText);
            var stderrTask = PumpAsync(process.StandardError, stderrText);

            // Wait for process with timeout
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }

            // Wait for stream pumps to complete
            await stdoutTask;
            await stderrTask;

            return new GitInvocation(process.ExitCode, stdoutText.ToString(), stderrText.ToString());
        }
        finally
        {
            Interlocked.Increment(ref _activity);
        }
    }

    /// <summary>
    /// <c>GH_TOKEN</c> for an operation that reaches a remote, when the remote is GitHub by the
    /// Host's own record: the URL being cloned or listed, or the team's registered repos for a
    /// fetch, a push or a listing by remote name. Never from the clone's <c>.git/config</c>, which the agent writes.
    /// </summary>
    private string? GitHubTokenFor(string clonePath, IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;

        // A local origin is a folder on this volume: GitHub is never asked about it.
        if (args[0] is not "clone" && _localOriginFor?.Invoke(clonePath) is not null) return null;

        return args[0] switch
        {
            // `--no-local` is passed only for a local repository's folder (see CloneAsync), which a
            // name like `github.com.git` must not turn into a GitHub remote.
            "clone" when args.Contains("--no-local") => null,
            "clone" => AgentEnvironment.GitHubTokenFor([.. args.Skip(1)]),
            // A URL is judged by itself; a remote NAME ("origin") names no host, so it is judged by
            // the team's remotes, as a fetch is - or `repo` refresh is refused by GitHub.
            "ls-remote" when args.Skip(1).Any(IsUrl) => AgentEnvironment.GitHubTokenFor([.. args.Skip(1)]),
            "fetch" or "push" or "pull" or "ls-remote" when args.Count < 2 || args[1] != "." =>
                _remotesFor is null ? null : AgentEnvironment.GitHubTokenFor(_remotesFor(clonePath)),
            // `set-head <remote> --auto` asks the remote which branch its HEAD names. Without the
            // token a private repository refuses it and every Fetch stores "not known".
            "remote" when args.Count > 1 && args[1] == "set-head" && args.Contains("--auto") =>
                _remotesFor is null ? null : AgentEnvironment.GitHubTokenFor(_remotesFor(clonePath)),
            _ => null,
        };
    }

    /// <summary>A URL or scp-style address (<c>git@host:path</c>), as opposed to a remote name or a flag.</summary>
    private static bool IsUrl(string arg) => !arg.StartsWith('-') && (arg.Contains("://") || arg.Contains(':'));

    private static async Task PumpAsync(StreamReader reader, StringBuilder builder)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            lock (builder)
            {
                builder.AppendLine(line);
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private async Task<IDisposable> AcquireSemaphoreAsync(string clonePath)
    {
        SemaphoreSlim semaphore;
        lock (_lockDictLock)
        {
            if (!_locksByPath.TryGetValue(clonePath, out semaphore!))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _locksByPath[clonePath] = semaphore;
            }
        }

        await semaphore.WaitAsync();
        return new SemaphoreReleaser(semaphore);
    }

    private sealed class SemaphoreReleaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;

        public SemaphoreReleaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => _semaphore.Release();
    }
}
