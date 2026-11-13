using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Yawble.CodeBrowser;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;

namespace Yawble.Core.Code;

/// <summary>
/// B011: the code browser's RPCs (code.open / list / read / download / close) and its code.changed push, wired to the
/// members' clones. The work is in the Yawble.CodeBrowser library; this maps its refusals to RPC errors and lets
/// only the person's window and cloud viewers (RemoteAccess, and the Cloud Link's "Show members' code" setting) call.
/// </summary>
public sealed class CodeBrowserRpc(CodeBrowserService service)
{
    public CodeBrowserService Service => service;

    public async Task<JsonNode?> DispatchAsync(string method, JsonObject? p, CancellationToken ct)
    {
        // Only the person: at the window, or viewing through the Cloud Link. Each session answers only the one who opened it.
        var actor = Provenance.Actor.Current;
        if (actor is null || actor.Via is not (Provenance.Actor.App or Provenance.Actor.Cloud))
            throw new RpcException("forbidden", "the Code view is for the person, in the app or through the Cloud Link");
        try
        {
            return await service.DispatchAsync(method, p, $"{actor.Via}:{actor.User}", ct).ConfigureAwait(false);
        }
        catch (CodeBrowserException e)
        {
            throw new RpcException(e.Code, e.Message);
        }
    }
}

/// <summary>The core's side of <see cref="ICodeHost"/>: members and their clones from the bus and team repositories, live runs, pushes, git, policy.</summary>
public sealed class CodeHost : ICodeHost
{
    private readonly BusEngine _bus;
    private readonly Launch.RunRegistry _runs;
    private readonly Ui.AppEvents _events;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _started = new(StringComparer.Ordinal);
    private readonly DeniedPaths? _denied;

    public CodeHost(BusEngine bus, Launch.RunRegistry runs, Ui.AppEvents events, Policy.PolicyStore? policies)
    {
        _bus = bus;
        _runs = runs;
        _events = events;
        _denied = policies is null ? null : new DeniedPaths(() => policies.GetGlobal().Filesystem.Denied);
        runs.Added += run => _started[run.RunKey] = DateTimeOffset.UtcNow;
        runs.Removed += run => _started.TryRemove(run.RunKey, out _);
        Git = bus.Repos is { } repos ? new HostGitReader(repos.Git) : null;
    }

    public IGitReader? Git { get; }

    public async Task<CodeMember?> FindAsync(MemberQuery query, CancellationToken ct)
    {
        var teams = await _bus.TeamsAsync(ct).ConfigureAwait(false);
        var members = await _bus.AllMemberRowsAsync(ct).ConfigureAwait(false);
        MemberRow? row;
        if (query.MemberId is { } memberId) row = members.FirstOrDefault(m => m.Id == memberId);
        else
        {
            var team = query.TeamId is { } teamId ? teams.FirstOrDefault(t => t.Id == teamId)
                : teams.FirstOrDefault(t => string.Equals(t.Name, query.Team, StringComparison.OrdinalIgnoreCase));
            row = team is null ? null : members.FirstOrDefault(m => m.TeamId == team.Id && string.Equals(m.Name, query.Member, StringComparison.OrdinalIgnoreCase));
        }
        return row is null ? null : Describe(row);
    }

    /// <summary>
    /// Whether the member has a clone on this computer: the folder code.open would show exists (its clonePresent).
    /// teams.agents sends it as the card's hasClone, which the shell needs to show the Code icon.
    /// </summary>
    public static bool HasClone(Teams.TeamRepos? repos, MemberRow row)
    {
        if (repos is null) return false;
        try { return Directory.Exists(ClonePathOf(repos, row)); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The member's clone folder, &lt;member root&gt;/repo, whether or not the team has a repository (the same as the checkout's ClonePath).</summary>
    private static string ClonePathOf(Teams.TeamRepos repos, MemberRow row) => Path.Combine(repos.MemberRoot(row.Team, row.Name), "repo");

    private CodeMember? Describe(MemberRow row)
    {
        var repos = _bus.Repos;
        if (repos is null) return null;
        var role = string.Equals(row.Role, "manager", StringComparison.OrdinalIgnoreCase) ? MemberRole.Manager : MemberRole.Member;
        var member = new MemberRef(row.Id, row.TeamId, row.Team, row.Name, role);
        var key = $"{row.TeamId}/{row.Id}";
        try
        {
            if (repos.RepoOf(row.TeamId) is not { } repo)
                return new CodeMember(key, row.Team, row.Name, ClonePathOf(repos, row), null);
            var checkout = repos.CheckoutOf(member, repo);
            // The base is read from the team repository, else its shared view: both host-owned. Never from the clone.
            return new CodeMember(key, row.Team, row.Name, checkout.ClonePath,
                new BranchSource([repo.Path, repos.SharedRepoPath(row.Team)], repo.BaseBranch, checkout.Branch));
        }
        catch (ArgumentException)
        {
            return null; // a name that cannot be a folder has no clone
        }
    }

    public async Task<IReadOnlyList<(CodeMember Member, DateTimeOffset StartedAt)>> RunningAsync(CancellationToken ct)
    {
        var live = _runs.All();
        if (live.Count == 0) return [];
        var members = await _bus.AllMemberRowsAsync(ct).ConfigureAwait(false);
        var list = new List<(CodeMember, DateTimeOffset)>();
        foreach (var run in live)
        {
            var row = members.FirstOrDefault(m => string.Equals(m.Team, run.Team, StringComparison.OrdinalIgnoreCase) && string.Equals(m.Name, run.Member, StringComparison.OrdinalIgnoreCase));
            if (row is null || Describe(row) is not { } member) continue;
            list.Add((member, _started.GetOrAdd(run.RunKey, _ => DateTimeOffset.UtcNow)));
        }
        return list;
    }

    public void Publish(string name, JsonNode data) => _events.Publish(name, data);

    /// <summary>Whether a path is under one the global policy denies (filesystem.denied). Fails closed: see <see cref="DeniedPaths"/>.</summary>
    public bool PolicyDenied(string fullPath) => _denied?.IsDenied(fullPath) ?? false;

    /// <summary>The host's hardened git (no system or global config, no credentials, local paths only), in host-owned repositories.</summary>
    private sealed class HostGitReader(Teams.HostGit git) : IGitReader
    {
        public async Task<GitReadResult> RunAsync(string repository, IReadOnlyList<string> args, CancellationToken ct)
        {
            var r = await git.TryRunAsync(repository, args, ct).ConfigureAwait(false);
            return new GitReadResult(r.ExitCode, r.Stdout);
        }
    }
}

/// <summary>
/// The global policy's denied paths, read at most every 30 seconds. Fails closed: when the policy cannot be read the
/// last list read stands, and with none read yet every path counts as denied (shown as hidden), never allowed.
/// </summary>
internal sealed class DeniedPaths(Func<IReadOnlyList<string>> read, TimeProvider? clock = null)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private Policy.PolicyPaths? _paths;
    private IReadOnlyList<string>? _denied; // the last list read
    private DateTimeOffset? _readAt;

    public bool IsDenied(string fullPath)
    {
        IReadOnlyList<string>? denied;
        Policy.PolicyPaths? paths;
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            if (_readAt is null || now - _readAt.Value > Every)
            {
                _readAt = now; // tried: the next read is after the interval, whether or not this one works
                try
                {
                    _paths ??= new Policy.PolicyPaths(Policy.TargetOsExtensions.Host, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        Policy.HostPathCanonicalizer.Canonicalize);
                    _denied = read().Select(_paths.Resolve).OfType<string>().ToList();
                }
                catch (Exception)
                {
                    // Any failure (a locked or broken store, a bad entry): the last good list stands; with none, deny.
                }
            }
            denied = _denied;
            paths = _paths;
        }
        if (denied is null || paths is null) return true;
        try
        {
            foreach (var d in denied)
                if (paths.IsWithin(fullPath, d)) return true;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
