using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Yawble.Core.Bus;

namespace Yawble.Core.Teams;

/// <summary>
/// One of a team's other repositories (0022), besides its primary one. A repository on the host, never granted to a
/// sandbox itself. <paramref name="Source"/> is the address the app cloned it from (null: a folder the person picked).
/// </summary>
public sealed record ExtraRepo(long TeamId, string Name, string Path, bool Writable, string BaseBranch, string? Source);

/// <summary>
/// An extra repository as one run sees it. Writable: the member's own clone (<paramref name="Path"/>, read/write) on
/// <paramref name="Branch"/>, and the bundle of the team's branches (<paramref name="SharedBundle"/>, read-only).
/// Reference: the team's read-only checkout of the base branch at <paramref name="Path"/>; no branch, no bundle.
/// </summary>
public sealed record ExtraCheckout(string Name, string Path, bool Writable, string? Branch, string BaseBranch, string? SharedBundle)
{
    /// <summary>The commit of the person's own checkout of this repository on its base branch: where the person's CLIs
    /// (az, gh, git) run, so what a build or deploy from there uses. Null when it cannot be read.</summary>
    public string? BaseCommit { get; init; }

    /// <summary>The person's own checkout of this repository (where the person's CLIs run with repo: this name).</summary>
    public string? PersonCheckout { get; init; }
}

/// <summary>
/// A team's other repositories (0022). The primary repository stays the run's cwd; every other one is a short name.
/// <list type="bullet">
/// <item>Writable: exactly like the primary. Each member gets its own clone at
/// <c>&lt;team root&gt;/members/&lt;member&gt;/repos/&lt;name&gt;</c> (no remote, its own branch), fetched back after each run
/// into the host repository and pushed to its origin with the team's other branches; the team's shared view of it is
/// <c>&lt;team root&gt;/shared/&lt;name&gt;.git</c>.</item>
/// <item>Reference (read-only): the host keeps one checkout of the base branch at
/// <c>&lt;team root&gt;/reference/&lt;name&gt;</c>, granted read-only, and brings it up to date before each run. A sandbox can
/// never write it, so running git in it stays safe.</item>
/// </list>
/// A repository the app cloned from an address (and the primary, when the app cloned it) is brought up to date from its
/// origin before a member's clone is made from it, at most every few minutes, fast-forward only.
/// </summary>
public sealed partial class TeamRepos
{
    public const string ExtraClonesFolder = "repos";
    public const string ReferenceFolder = "reference";
    public const string ExtraSharedFolder = "shared";

    /// <summary>How long a repository's origin is trusted to be current before it is fetched again.</summary>
    public static TimeSpan OriginRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _originFetched = new(StringComparer.OrdinalIgnoreCase);

    public string ExtraClonePath(string team, string member, string name) => Path.Combine(MemberRoot(team, member), ExtraClonesFolder, RepoName(name));
    public string ReferencePath(string team, string name) => Path.Combine(TeamRoot(team), ReferenceFolder, RepoName(name));
    public string ExtraSharedPath(string team, string name) => Path.Combine(TeamRoot(team), ExtraSharedFolder, RepoName(name) + ".git");

    /// <summary>Where an extra repository cloned from an address lives: outside the teams folder, as the primary's.</summary>
    public string ExtraClonePathFor(string team, string name) => Path.Combine(AppDataRoot, "repos", Segment(team) + " - " + RepoName(name));

    /// <summary>
    /// A repository's short name, as the folder members see: letters, digits, '-', '_' and '.', starting with a letter
    /// or digit, at most 40 characters.
    /// </summary>
    public static string RepoName(string name)
    {
        var n = (name ?? "").Trim();
        if (!RepoNamePattern().IsMatch(n) || n.EndsWith('.'))
            throw new ArgumentException($"'{name}' cannot be a repository's name: use letters, digits, '-', '_' or '.' (at most 40), e.g. yawble-cloud");
        return n;
    }

    public static bool IsRepoName(string name) => name is not null && RepoNamePattern().IsMatch(name) && !name.EndsWith('.');

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$")]
    private static partial Regex RepoNamePattern();

    /// <summary>A name for a repository from its address or folder: the last path segment without .git.</summary>
    public static string SuggestRepoName(string pathOrUrl)
    {
        var s = (pathOrUrl ?? "").Trim().TrimEnd('/', '\\');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        var last = s.Split('/', '\\', ':').LastOrDefault() ?? "";
        var cleaned = Regex.Replace(last, "[^A-Za-z0-9._-]", "-").Trim('-', '.');
        if (cleaned.Length > 40) cleaned = cleaned[..40];
        return cleaned.Length == 0 ? "repo" : cleaned.ToLowerInvariant();
    }

    // ------------------------------------------------------------------ the list

    public IReadOnlyList<ExtraRepo> ExtrasOf(long teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, path, writable, base_branch, source FROM team_extra_repos WHERE team_id = $t ORDER BY created_at, name;";
        cmd.Parameters.AddWithValue("$t", teamId);
        using var r = cmd.ExecuteReader();
        var list = new List<ExtraRepo>();
        while (r.Read())
            list.Add(new ExtraRepo(teamId, r.GetString(0), r.GetString(1), r.GetInt64(2) == 1, r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    /// <summary>
    /// Adds a repository to a team: a folder on this computer (<paramref name="path"/>) or an address the app clones
    /// with the person's own git (<paramref name="url"/>). Writable or a read-only reference. The name defaults to the
    /// folder's or address's last part. Members get it from their next run.
    /// </summary>
    public async Task<ExtraRepo> AddExtraAsync(long teamId, string team, string? name, string? path, string? url, bool writable, CancellationToken ct = default)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(url);
        var hasPath = !string.IsNullOrWhiteSpace(path);
        if (hasUrl == hasPath) throw new ArgumentException("give either the repository's folder on this computer or its address, not both");
        var repoName = RepoName(string.IsNullOrWhiteSpace(name) ? SuggestRepoName(hasUrl ? url! : path!) : name!);
        if (ExtrasOf(teamId).Any(e => string.Equals(e.Name, repoName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"team {team} already has a repository named {repoName}");

        string full;
        if (hasUrl)
        {
            var target = ExtraClonePathFor(team, repoName);
            full = await CloneFromAddressAsync(team, url!, target, ct);
        }
        else
        {
            full = await CheckPickedAsync(team, path!, ct);
        }
        if (RepoOf(teamId) is { } primary && string.Equals(Path.GetFullPath(primary.Path), full, PathComparison))
            throw new ArgumentException($"{full} is already team {team}'s main repository");
        var baseBranch = await CurrentBranchAsync(full, ct) ?? "main";
        Exec("INSERT INTO team_extra_repos (team_id, name, path, writable, base_branch, source, created_at) VALUES ($t, $n, $p, $w, $b, $s, $c);",
            ("$t", teamId), ("$n", repoName), ("$p", full), ("$w", writable ? 1 : 0), ("$b", baseBranch),
            ("$s", hasUrl ? PersonGitPush.Redact(url!.Trim()) : DBNull.Value), ("$c", Now()));
        _log?.Invoke($"team {team}: added repository {repoName} ({(writable ? "writable" : "reference")}) at {full}");
        return new ExtraRepo(teamId, repoName, full, writable, baseBranch, hasUrl ? url : null);
    }

    /// <summary>
    /// Removes a repository from a team: the team's reference checkout, shared view and members' clones of it go (the
    /// work on its branches is already in the host repository, fetched back after every run). The host repository
    /// itself is never deleted. The caller makes sure no member is running.
    /// </summary>
    public IReadOnlyList<string> RemoveExtra(long teamId, string team, IEnumerable<string> memberNames, string name)
    {
        var repo = ExtrasOf(teamId).FirstOrDefault(e => string.Equals(e.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"team {team} has no repository named {name}");
        Exec("DELETE FROM member_extra_clones WHERE repo_name = $n AND member_id IN (SELECT id FROM members WHERE team_id = $t);", ("$n", repo.Name), ("$t", teamId));
        Exec("DELETE FROM team_extra_repos WHERE team_id = $t AND name = $n;", ("$t", teamId), ("$n", repo.Name));
        var left = new List<string>();
        foreach (var folder in new[] { ReferencePath(team, repo.Name), ExtraSharedPath(team, repo.Name) }
                     .Concat(memberNames.Select(m => ExtraClonePath(team, m, repo.Name))))
            DeleteTreeQuietly(folder, left);
        _log?.Invoke($"team {team}: removed repository {repo.Name} (its folder {repo.Path} is kept)");
        return left;
    }

    // ------------------------------------------------------------------ before a run

    /// <summary>
    /// The team's extra repositories for one member's run (the caller holds the team's lock): references brought up to
    /// date, the member's clone of each writable one made if missing. A repository that cannot be prepared is left out
    /// of the run, with the reason logged, rather than failing the member's work on the primary.
    /// </summary>
    private async Task<IReadOnlyList<ExtraCheckout>> PrepareExtrasLockedAsync(MemberRef member, string branch, CancellationToken ct)
    {
        var list = new List<ExtraCheckout>();
        foreach (var extra in ExtrasOf(member.TeamId))
        {
            try
            {
                if (!Directory.Exists(extra.Path)) throw new IOException($"its folder {extra.Path} is not there");
                if (extra.Source is not null) await RefreshFromOriginAsync(extra.Path, extra.BaseBranch, ct);
                if (!extra.Writable)
                {
                    list.Add(new ExtraCheckout(extra.Name, await RefreshReferenceAsync(member.Team, extra, ct), false, null, extra.BaseBranch, null)
                        { BaseCommit = await BaseCommitAsync(extra.Path, extra.BaseBranch, ct), PersonCheckout = extra.Path });
                    continue;
                }
                var shared = ExtraSharedPath(member.Team, extra.Name);
                var row = ExtraRow(member.Id, extra.Name);
                var clone = ExtraClonePath(member.Team, member.Name, extra.Name);
                // A rehired member reusing a pre-B016 extra clone left on disk (no row): record the branch it is on first.
                if (row is null && member.Role != MemberRole.Manager && AdoptLegacyClone(member, clone) is { } adopted)
                {
                    Exec("INSERT OR REPLACE INTO member_extra_clones (member_id, repo_name, path, branch, created_at) VALUES ($m, $n, $p, $b, $c);",
                        ("$m", member.Id), ("$n", extra.Name), ("$p", clone), ("$b", adopted), ("$c", Now()));
                    _log?.Invoke($"{member.Qualified}: {extra.Name}: reusing the clone on disk, on {adopted}");
                    row = ExtraRow(member.Id, extra.Name);
                }
                // B016: an extra clone still on members/<name> moves too, before the shared view is updated.
                if (row is not null) row = await MoveLegacyExtraBranchLockedAsync(member, extra, row, ct);
                var cloneBranch = row?.Branch ?? branch;
                // B020, again after the refresh from origin: a blocker fails the member's start (not caught here).
                await CheckMemberBranchFreeAsync(member, cloneBranch, $"repository {extra.Name}", extra.Path, shared, clone, ct);
                await UpdateSharedAtAsync(shared, extra.Path, extra.BaseBranch, ct);
                if (!(Directory.Exists(clone) && Directory.EnumerateFileSystemEntries(clone).Any()))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(clone)!);
                    await CloneIntoAsync(member, extra.Path, extra.BaseBranch, clone, cloneBranch, ct);
                    _log?.Invoke($"{member.Qualified}: cloned {extra.Path} to {clone} on {cloneBranch}");
                }
                if (row is null)
                    Exec("INSERT OR REPLACE INTO member_extra_clones (member_id, repo_name, path, branch, created_at) VALUES ($m, $n, $p, $b, $c);",
                        ("$m", member.Id), ("$n", extra.Name), ("$p", clone), ("$b", cloneBranch), ("$c", Now()));
                var bundle = Path.Combine(shared, SharedBundleFile);
                list.Add(new ExtraCheckout(extra.Name, clone, true, cloneBranch, extra.BaseBranch, File.Exists(bundle) ? shared : null)
                    { BaseCommit = await BaseCommitAsync(extra.Path, extra.BaseBranch, ct), PersonCheckout = extra.Path });
            }
            catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                _log?.Invoke($"{member.Qualified}: repository {extra.Name} left out of this run: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>
    /// B020, before anything of the member's start changes: every writable extra repository the member's branch would be
    /// made in is checked for a blocking ref (<see cref="CheckMemberBranchFreeAsync"/>). A repository that cannot be read
    /// is left to <see cref="PrepareExtrasLockedAsync"/>, which leaves it out of the run with the reason.
    /// </summary>
    private async Task CheckExtrasFreeLockedAsync(MemberRef member, string branch, CancellationToken ct)
    {
        foreach (var extra in ExtrasOf(member.TeamId).Where(e => e.Writable && Directory.Exists(e.Path)))
        {
            try
            {
                var cloneBranch = ExtraRow(member.Id, extra.Name)?.Branch ?? branch;
                await CheckMemberBranchFreeAsync(member, cloneBranch, $"repository {extra.Name}", extra.Path, ExtraSharedPath(member.Team, extra.Name),
                    ExtraClonePath(member.Team, member.Name, extra.Name), ct);
            }
            catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                _log?.Invoke($"{member.Qualified}: repository {extra.Name}: could not check its branches before the start: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The team's read-only checkout of a reference repository's base branch, made or brought up to date. The host owns
    /// it and a sandbox can only read it, so git may run in it. Returns its folder.
    /// </summary>
    private async Task<string> RefreshReferenceAsync(string team, ExtraRepo extra, CancellationToken ct)
    {
        var dir = ReferencePath(team, extra.Name);
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            DeleteTreeQuietly(dir, []);
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            await Git.RunAsync(null, ct, "clone", "-q", "--local", "--no-hardlinks", "--no-checkout", "--origin", "source", extra.Path, dir);
            await Git.RunAsync(dir, ct, "remote", "remove", "source");
        }
        const string tracking = "refs/remotes/source/base";
        await Git.RunAsync(dir, ct, "fetch", "-q", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", "--",
            extra.Path, $"+refs/heads/{extra.BaseBranch}:{tracking}");
        await Git.RunAsync(dir, ct, "checkout", "-q", "-f", "--detach", tracking);
        await Git.RunAsync(dir, ct, "clean", "-q", "-f", "-d", "-x");
        return dir;
    }

    /// <summary>
    /// Brings a repository the app cloned up to date with its origin: fetch with the person's git, then fast-forward the
    /// base branch when it is checked out and clean. At most every <see cref="OriginRefreshInterval"/>; a failure is
    /// logged and the repository is used as it is.
    /// </summary>
    private async Task RefreshFromOriginAsync(string repoPath, string baseBranch, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_originFetched.TryGetValue(repoPath, out var last) && now - last < OriginRefreshInterval) return;
        _originFetched[repoPath] = now;
        var origin = await Git.TryRunAsync(repoPath, ["config", "--get", "remote.origin.url"], ct);
        if (origin.ExitCode != 0 || origin.Stdout.Length == 0) return;
        // The person's own git for all of it: the person's git cloned this working tree, so its checkout follows the
        // person's config (core.autocrlf on Windows). The app's config-free git would see every CRLF file as changed
        // and refuse the fast-forward ("local changes would be overwritten") while its status still read clean.
        var person = new PersonGitPush(TimeSpan.FromMinutes(3));
        var fetched = await person.RunAsync(repoPath, ["fetch", "-q", "--no-tags", "origin"], ct);
        if (fetched.ExitCode != 0)
        {
            _log?.Invoke($"{repoPath}: could not fetch from origin: {PersonGitPush.Redact(fetched.Stderr)}");
            return;
        }
        if (await CurrentBranchAsync(repoPath, ct) != baseBranch) return;
        var dirty = await person.RunAsync(repoPath, ["status", "--porcelain", "--untracked-files=no"], ct);
        if (dirty.ExitCode != 0 || dirty.Stdout.Length > 0)
        {
            _log?.Invoke($"{repoPath}: {baseBranch} has local changes; not brought up to date with origin");
            return;
        }
        var ff = await person.RunAsync(repoPath, ["merge", "-q", "--ff-only", "--no-edit", $"refs/remotes/origin/{baseBranch}"], ct);
        if (ff.ExitCode != 0) _log?.Invoke($"{repoPath}: {baseBranch} was not fast-forwarded to origin's: {ff.Stderr}");
    }

    /// <summary>The commit the person's checkout's base branch is at (full 40 characters), or null.</summary>
    private async Task<string?> BaseCommitAsync(string repoPath, string baseBranch, CancellationToken ct)
    {
        var r = await Git.TryRunAsync(repoPath, ["rev-parse", "--verify", "-q", "refs/heads/" + baseBranch], ct);
        return r.ExitCode == 0 && r.Stdout.Length >= 40 ? r.Stdout.Trim() : null;
    }

    /// <summary>Whether the primary repository is one the app cloned from an address (under &lt;appdata&gt;/repos).</summary>
    private bool AppCloned(string path) =>
        (Path.GetFullPath(path) + Path.DirectorySeparatorChar).StartsWith(Path.Combine(AppDataRoot, "repos") + Path.DirectorySeparatorChar, PathComparison);

    // ------------------------------------------------------------------ store

    public sealed record ExtraCloneRow(string Name, string Path, string Branch, string? FetchedSha, string? LastError);

    public ExtraCloneRow? ExtraRow(long memberId, string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT repo_name, path, branch, fetched_sha, last_error FROM member_extra_clones WHERE member_id = $m AND repo_name = $n;";
        cmd.Parameters.AddWithValue("$m", memberId);
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ExtraCloneRow(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)) : null;
    }

    /// <summary>The member's clones of the team's writable extra repositories that exist.</summary>
    private IEnumerable<ExtraCloneRow> WritableExtraClones(MemberRef member) =>
        ExtrasOf(member.TeamId).Where(e => e.Writable).Select(e => ExtraRow(member.Id, e.Name)).OfType<ExtraCloneRow>();

    private void UpdateExtraClone(long memberId, string name, string set, params (string, object)[] args) =>
        Exec($"UPDATE member_extra_clones SET {set} WHERE member_id = $member AND repo_name = $repo;", [.. args, ("$member", memberId), ("$repo", name)]);

    private static void DeleteTreeQuietly(string folder, List<string> left)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                if (f.Attributes.HasFlag(FileAttributes.ReadOnly)) f.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            left.Add($"{folder}: {e.Message}");
        }
    }
}
