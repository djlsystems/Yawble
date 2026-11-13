using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;

namespace Yawble.Core.Teams;

/// <summary>A team's own repository on the host. Never granted to a sandbox; never pushed by the app.</summary>
public sealed record TeamRepo(long TeamId, string Path, bool Managed, string BaseBranch);

/// <summary>
/// One member's working copy: its clone (the run's cwd), workspace (HOME) and temp folder, which are exactly the
/// run's implicit read/write grants (<see cref="Policy.RunLayout.ImplicitReadwrite"/>), and its one branch.
/// </summary>
public sealed record MemberCheckout(string ClonePath, string Workspace, string Tmp, string Branch, string BaseBranch, string TeamRepoPath)
{
    /// <summary>The team's other repositories as this run sees them (<see cref="ExtraCheckout"/>), by name.</summary>
    public IReadOnlyList<ExtraCheckout> Extras { get; init; } = [];

    /// <summary>The commit of the person's own checkout of the main repository on its base branch (where the person's
    /// CLIs run by default), or null.</summary>
    public string? BaseCommit { get; init; }

    /// <summary>The base-branch commit this member's own branch was built on (the newest base commit it contains): what the
    /// run's record stores, and what a hand-back reports.</summary>
    public string? CloneBaseCommit { get; init; }
}

public sealed record FetchResult(bool Fetched, string? Sha, string? Error)
{
    public static FetchResult Nothing { get; } = new(false, null, null);
}

/// <summary>Something the post-run git check found in a member's clone or workspace, and removed.</summary>
public sealed record GitFinding(string Where, string What);

/// <summary>
/// Team repositories and per-member clones.
///
/// <para>Each team has one local repository: by default one the app makes at <c>&lt;appdata&gt;/teams/&lt;team&gt;/repo</c>,
/// or a folder the person picked. Before a member's first run the host makes the member's own
/// <c>git clone --local --no-hardlinks</c> at <c>&lt;appdata&gt;/teams/&lt;team&gt;/members/&lt;member&gt;/repo</c> (the same path
/// <see cref="Policy.RunLayout"/> grants read/write and uses as cwd), checks out the member's branch
/// <c>members/&lt;name&gt;</c> and REMOVES the remote: a sandbox never holds a remote or a git credential.
/// <c>--no-hardlinks</c> because a hard-linked object file is the same inode as the team repository's, and the
/// sandbox may write its clone. Shared-.git worktrees are not used: they need write access to the main .git.</para>
///
/// <para>After every run the host checks the clone and workspace (remotes, credential helpers, url rewrites,
/// credential files and object alternates are removed and recorded), then fetches the member's branch INTO the
/// team repository. Merging is the person's action. After each run the host pushes branches that are new or ahead to the team
/// repository's origin, if it has one (<see cref="PublishAsync"/>); a sandbox never pushes.</para>
///
/// <para>Once an agent has run in a clone its config and hooks are untrusted, so the host never again runs a git
/// command inside it: it only edits the config file with <c>git config --file</c>, and reads the branch with
/// <c>ls-remote</c>/<c>fetch</c> from the team repository (upload-pack ignores hooks and the repository's
/// packObjectsHook). A clone whose <c>.git</c> is not a plain folder is refused.</para>
/// </summary>
public sealed partial class TeamRepos
{
    public const string BranchPrefix = "members/";
    private const string ManagedIdentityName = "Yawble";
    private const string ManagedIdentityEmail = "yawble@localhost";

    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _teamLocks = new();

    public TeamRepos(string appDataRoot, string connectionString, TimeProvider? clock = null, Action<string>? log = null)
    {
        AppDataRoot = Path.GetFullPath(appDataRoot);
        _connectionString = connectionString;
        _clock = clock ?? TimeProvider.System;
        _log = log;
        Git = new HostGit(Path.Combine(AppDataRoot, "data", "git", "empty.gitconfig"));
    }

    public string AppDataRoot { get; }
    public HostGit Git { get; }

    // ------------------------------------------------------------------ layout (matches Policy.RunLayout.Standard)

    public string TeamRoot(string team) => Path.Combine(AppDataRoot, "teams", Segment(team));
    public string DefaultRepoPath(string team) => Path.Combine(TeamRoot(team), "repo");

    /// <summary>
    /// The team's shared view: a bare repository the host keeps in step with the team repository's branches, granted
    /// READ-ONLY to the team's members (<see cref="Policy.RunLayout.SharedRepo"/>), so a member can fetch a teammate's
    /// branch (a tester, the developer's work) without any sandbox reaching the team repository or another clone.
    /// Only the host ever writes it, and only by fetching from the team repository; it has no remote and no config of
    /// the person's.
    /// </summary>
    public string SharedRepoPath(string team) => Path.Combine(TeamRoot(team), SharedRepoFolder);

    public const string SharedRepoFolder = "shared.git";
    public string MemberRoot(string team, string member) => Path.Combine(TeamRoot(team), "members", Segment(member));

    /// <summary>The member's one branch: members/&lt;name&gt;, lower-case, with anything git dislikes replaced.</summary>
    public static string BranchFor(string memberName)
    {
        var sb = new StringBuilder();
        foreach (var c in memberName.Trim().ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return BranchPrefix + (slug.Length == 0 ? "member" : slug);
    }

    /// <summary>
    /// The team's delivery branch, team/&lt;team&gt;: the Manager's own branch, into which it
    /// merges accepted work. Landing it on the base branch is the person's.
    /// </summary>
    public static string TeamBranchFor(string team) => TeamBranchPrefix + BranchFor(team)[BranchPrefix.Length..];

    public const string TeamBranchPrefix = "team/";

    /// <summary>
    /// A team or member name as one folder name. The rule is the same on every OS, and is Windows' (the strictest),
    /// so a name that works on Linux cannot fail on Windows: no separators, no &lt;&gt;:"|?* or control characters, no
    /// trailing dot or space (Windows drops them, so "Ada." and "Ada" would be one folder), no device name (CON, NUL…).
    /// </summary>
    internal static string Segment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(ForbiddenNameChars) >= 0
            || name.EndsWith('.') || name.EndsWith(' ') || IsWindowsDeviceName(name))
            throw new ArgumentException($"'{name}' cannot be a folder name in the app's layout");
        return name;
    }

    private static readonly char[] ForbiddenNameChars =
        ['/', '\\', ':', '<', '>', '"', '|', '?', '*', .. Enumerable.Range(0, 32).Select(i => (char)i)];

    private static bool IsWindowsDeviceName(string name)
    {
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] is (>= '0' and <= '9') or '\u00b9' or '\u00b2' or '\u00b3');
    }

    /// <summary>The member's folders and branch, without touching the disk.</summary>
    public MemberCheckout CheckoutOf(MemberRef member, TeamRepo repo)
    {
        var root = MemberRoot(member.Team, member.Name);
        var branch = Row(member.Id)?.Branch ?? (member.Role == MemberRole.Manager ? TeamBranchFor(member.Team) : BranchFor(member.Name));
        return new MemberCheckout(Path.Combine(root, "repo"), Path.Combine(root, "home"), Path.Combine(root, "tmp"), branch, repo.BaseBranch, repo.Path);
    }

    // ------------------------------------------------------------------ team repositories

    public TeamRepo? RepoOf(long teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, managed, base_branch FROM team_repos WHERE team_id = $t;";
        cmd.Parameters.AddWithValue("$t", teamId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new TeamRepo(teamId, r.GetString(0), r.GetInt64(1) == 1, r.GetString(2)) : null;
    }

    /// <summary>
    /// The team's repository. The first call uses the folder the person picked when creating the team
    /// (team_settings.repo_path), or else makes the managed one in the app data folder. A picked folder that is not a
    /// git repository fails the run with the reason; the app never runs git init in a person's folder.
    /// </summary>
    public async Task<TeamRepo> EnsureTeamRepoAsync(long teamId, string team, CancellationToken ct = default)
    {
        var gate = _teamLocks.GetOrAdd(teamId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (RepoOf(teamId) is { } known) return known;
            if (PickedRepoPath(teamId) is { } picked) return await UsePickedAsync(teamId, team, picked, ct);
            return await CreateManagedAsync(teamId, team, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TeamRepo> CreateManagedAsync(long teamId, string team, CancellationToken ct)
    {
        var path = DefaultRepoPath(team);
        if (!Directory.Exists(Path.Combine(path, ".git")))
        {
            Directory.CreateDirectory(path);
            await Git.RunAsync(null, ct, "init", "-q", "-b", "main", path);
            await Git.RunAsync(path, ct, "-c", "user.name=" + ManagedIdentityName, "-c", "user.email=" + ManagedIdentityEmail,
                "commit", "-q", "--allow-empty", "-m", $"Team repository for {team}");
        }
        var baseBranch = await CurrentBranchAsync(path, ct) ?? "main";
        Insert(teamId, path, managed: true, baseBranch);
        _log?.Invoke($"team {team}: made its repository at {path}");
        return new TeamRepo(teamId, path, true, baseBranch);
    }

    /// <summary>
    /// Uses a repository folder the person picked instead of the managed one. Its remotes stay as the
    /// person set them; the app never pushes to them, and they never reach a member's clone.
    /// </summary>
    public async Task<TeamRepo> UseExistingRepoAsync(long teamId, string team, string path, CancellationToken ct = default)
    {
        var full = await CheckPickedAsync(team, path, ct);
        var gate = _teamLocks.GetOrAdd(teamId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (RepoOf(teamId) is { } existing) throw new InvalidOperationException($"team {team} already uses {existing.Path}");
            return await RecordPickedAsync(teamId, team, full, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Where a repository cloned from a URL for a team lives: outside the teams folder, so deleting the team
    /// never takes work that was not pushed.</summary>
    public string ClonePathFor(string team) => Path.Combine(AppDataRoot, "repos", Segment(team));

    /// <summary>
    /// Clones a repository from a URL (https or ssh) for a new team, with the person's own git and sign-in (no prompt:
    /// a missing credential fails with git's reason). Its origin stays the URL, so the team's branches are pushed back
    /// there after runs, as for a folder the person picked. Returns the clone's folder.
    /// </summary>
    public Task<string> CloneForTeamAsync(string team, string url, CancellationToken ct = default) =>
        CloneFromAddressAsync(team, url, ClonePathFor(team), ct);

    /// <summary>Clones an address to a folder with the person's own git (no prompt). Returns the folder.</summary>
    private async Task<string> CloneFromAddressAsync(string team, string url, string target, CancellationToken ct)
    {
        var u = (url ?? "").Trim();
        var ok = Uri.TryCreate(u, UriKind.Absolute, out var parsed) && (parsed.Scheme is "https" or "http" or "ssh" || parsed.Scheme == "file" && u.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            || System.Text.RegularExpressions.Regex.IsMatch(u, @"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^\s]+$"); // git@github.com:owner/repo.git
        if (!ok || u.StartsWith('-')) throw new ArgumentException("give the repository's address, e.g. https://github.com/owner/repo.git");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new ArgumentException($"{target} is already there; pick it as the team's folder instead, or remove it first");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var r = await new PersonGitPush(TimeSpan.FromMinutes(10)).RunAsync(Path.GetDirectoryName(target)!, ["clone", "--", u, target], ct);
        if (r.ExitCode != 0)
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new ArgumentException("git could not clone it: " + PersonGitPush.Redact(string.Join(' ', r.Stderr, r.Stdout).Trim()) +
                " (if it needs a sign-in, clone it once in your own terminal so git remembers it, then try again)");
        }
        _log?.Invoke($"team {team}: cloned {PersonGitPush.Redact(u)} to {target}");
        return target;
    }

    /// <summary>Records a picked folder as the team's repository (the caller holds the team's lock).</summary>
    private async Task<TeamRepo> UsePickedAsync(long teamId, string team, string path, CancellationToken ct) =>
        await RecordPickedAsync(teamId, team, await CheckPickedAsync(team, path, ct), ct);

    /// <summary>The picked folder's full path, refused when it is inside the app's own folders or not a git repository.</summary>
    private async Task<string> CheckPickedAsync(string team, string path, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        var teams = Path.Combine(AppDataRoot, "teams") + Path.DirectorySeparatorChar;
        var data = Path.Combine(AppDataRoot, "data") + Path.DirectorySeparatorChar;
        if ((full + Path.DirectorySeparatorChar).StartsWith(teams, PathComparison) || (full + Path.DirectorySeparatorChar).StartsWith(data, PathComparison))
            throw new ArgumentException("a team repository cannot be inside the app's own teams or data folders");
        var top = await Git.TryRunAsync(full, ["rev-parse", "--path-format=absolute", "--git-dir"], ct);
        if (top.ExitCode != 0) throw new ArgumentException($"the folder picked for team {team}, {full}, is not a git repository: {top.Stderr}");
        return full;
    }

    private async Task<TeamRepo> RecordPickedAsync(long teamId, string team, string full, CancellationToken ct)
    {
        var baseBranch = await CurrentBranchAsync(full, ct) ?? "main";
        Insert(teamId, full, managed: false, baseBranch);
        _log?.Invoke($"team {team}: uses the repository the person picked at {full}");
        return new TeamRepo(teamId, full, false, baseBranch);
    }

    /// <summary>Whether the app pushes this team's branches to its repository's origin after runs (0019; on by default).</summary>
    public bool PushesToOrigin(long teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT push_to_origin FROM teams WHERE id = $t;";
        cmd.Parameters.AddWithValue("$t", teamId);
        return cmd.ExecuteScalar() is not long v || v != 0;
    }

    public void SetPushesToOrigin(long teamId, bool on)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE teams SET push_to_origin = $v WHERE id = $t;";
        cmd.Parameters.AddWithValue("$v", on ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", teamId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The folder the person picked when creating the team (migration 0006), if any.</summary>
    private string? PickedRepoPath(long teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT repo_path FROM team_settings WHERE team_id = $t;";
        cmd.Parameters.AddWithValue("$t", teamId);
        return cmd.ExecuteScalar() is string p && !string.IsNullOrWhiteSpace(p) ? p.Trim() : null;
    }

    private async Task<string?> CurrentBranchAsync(string repo, CancellationToken ct)
    {
        var head = await Git.TryRunAsync(repo, ["symbolic-ref", "--short", "-q", "HEAD"], ct);
        return head.ExitCode == 0 && head.Stdout.Length > 0 ? head.Stdout : null;
    }

    // ------------------------------------------------------------------ member clones

    /// <summary>
    /// Makes the member's clone, workspace and temp folder before its first run. A clone that already exists is
    /// left alone: no git command runs inside a clone an agent has used.
    /// </summary>
    public async Task<MemberCheckout> PrepareAsync(MemberRef member, CancellationToken ct = default)
    {
        var repo = await EnsureTeamRepoAsync(member.TeamId, member.Team, ct);
        var gate = _teamLocks.GetOrAdd(member.TeamId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (AppCloned(repo.Path)) await RefreshFromOriginAsync(repo.Path, repo.BaseBranch, ct);
            await UpdateSharedLockedAsync(member.Team, repo, ct);
            var checkout = CheckoutOf(member, repo);
            checkout = checkout with
            {
                Extras = await PrepareExtrasLockedAsync(member, checkout.Branch, ct),
                BaseCommit = await BaseCommitAsync(repo.Path, repo.BaseBranch, ct),
            };
            var root = Path.GetDirectoryName(checkout.ClonePath)!;
            CreatePrivate(root);
            Directory.CreateDirectory(checkout.Workspace);
            Directory.CreateDirectory(checkout.Tmp);
            if (Directory.Exists(checkout.ClonePath) && Directory.EnumerateFileSystemEntries(checkout.ClonePath).Any())
            {
                if (Row(member.Id) is null) InsertClone(member.Id, checkout.ClonePath, checkout.Branch);
                // The clone may not have pulled what the host synced since: the base commit is the clone's own.
                return checkout with { CloneBaseCommit = await CloneBaseCommitAsync(repo.Path, repo.BaseBranch, checkout.Branch, checkout.ClonePath, ct) };
            }
            await CloneAsync(member, repo, checkout, ct);
            if (Row(member.Id) is null) InsertClone(member.Id, checkout.ClonePath, checkout.Branch);
            else UpdateClone(member.Id, "last_error = NULL");
            _log?.Invoke($"{member.Qualified}: cloned {repo.Path} to {checkout.ClonePath} on {checkout.Branch}");
            return checkout with { CloneBaseCommit = await CloneBaseCommitAsync(repo.Path, repo.BaseBranch, checkout.Branch, checkout.ClonePath, ct) };
        }
        finally
        {
            gate.Release();
        }
    }

    private Task CloneAsync(MemberRef member, TeamRepo repo, MemberCheckout checkout, CancellationToken ct) =>
        CloneIntoAsync(member, repo.Path, repo.BaseBranch, checkout.ClonePath, checkout.Branch, ct);

    /// <summary>A member's clone of a host repository (the team's, or one of its writable extras), on its branch, with
    /// no remote and no credential helper.</summary>
    private async Task CloneIntoAsync(MemberRef member, string sourcePath, string baseBranch, string clone, string branch, CancellationToken ct)
    {
        const string origin = "team";
        await Git.RunAsync(null, ct, "clone", "-q", "--local", "--no-hardlinks", "--origin", origin, sourcePath, clone);
        // The clone is ours until the first run: these are the only git commands ever run inside it.
        var hasOwnBranch = (await Git.TryRunAsync(clone, ["rev-parse", "--verify", "-q", $"refs/remotes/{origin}/{branch}"], ct)).ExitCode == 0;
        var hasBase = (await Git.TryRunAsync(clone, ["rev-parse", "--verify", "-q", $"refs/remotes/{origin}/{baseBranch}"], ct)).ExitCode == 0;
        if (hasOwnBranch) await Git.RunAsync(clone, ct, "checkout", "-q", "-B", branch, $"{origin}/{branch}");
        else if (hasBase) await Git.RunAsync(clone, ct, "checkout", "-q", "-B", branch, $"{origin}/{baseBranch}");
        else await Git.RunAsync(clone, ct, "symbolic-ref", "HEAD", "refs/heads/" + branch); // empty team repo
        await Git.RunAsync(clone, ct, "remote", "remove", origin);
        foreach (var other in (await Git.RunAsync(clone, ct, "for-each-ref", "--format=%(refname)", "refs/heads/")).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (other != "refs/heads/" + branch) await Git.RunAsync(clone, ct, "update-ref", "-d", other);
        }
        var config = Path.Combine(clone, ".git", "config");
        await SetAsync(config, "user.name", $"{member.Name} ({member.Team})", ct);
        await SetAsync(config, "user.email", $"{BranchFor(member.Name)[BranchPrefix.Length..]}@{Slug(member.Team)}.yawble.local", ct);
        // An empty helper resets the list, so no system or global helper (Git Credential Manager on Windows) applies.
        await SetAsync(config, "credential.helper", "", ct);
        await SetAsync(config, "commit.gpgSign", "false", ct);
        await SetAsync(config, "push.default", "nothing", ct);
    }

    private Task SetAsync(string configFile, string key, string value, CancellationToken ct) =>
        Git.RunAsync(null, ct, "config", "--file", configFile, key, value);

    private static string Slug(string s) => BranchFor(s)[BranchPrefix.Length..];

    // ------------------------------------------------------------------ after a run

    /// <summary>
    /// The post-run git check (no git credential anywhere). Removes from the clone's config every remote,
    /// credential section, url rewrite, http section and include; deletes credential files from the workspace and
    /// object alternates from the clone; and re-asserts the empty credential helper. Returns what it found.
    /// </summary>
    public async Task<IReadOnlyList<GitFinding>> CheckAndScrubAsync(MemberRef member, CancellationToken ct = default)
    {
        var repo = RepoOf(member.TeamId);
        if (repo is null) return [];
        var checkout = CheckoutOf(member, repo);
        var findings = new List<GitFinding>();
        findings.AddRange(await ScrubCloneAsync(checkout.ClonePath, "clone", ct));
        foreach (var extra in WritableExtraClones(member)) findings.AddRange(await ScrubCloneAsync(extra.Path, $"clone of {extra.Name}", ct));
        foreach (var rel in new[] { ".git-credentials", ".netrc", "_netrc", Path.Combine(".config", "git", "credentials") })
        {
            var file = Path.Combine(checkout.Workspace, rel);
            if (File.Exists(file))
            {
                DeleteFile(file);
                findings.Add(new GitFinding("workspace " + rel.Replace('\\', '/'), "credential file (deleted)"));
            }
        }
        foreach (var rel in new[] { ".gitconfig", Path.Combine(".config", "git", "config") })
        {
            var file = Path.Combine(checkout.Workspace, rel);
            if (File.Exists(file)) findings.AddRange(await ScrubConfigAsync(file, "workspace " + rel.Replace('\\', '/'), ownClone: false, ct));
        }
        UpdateClone(member.Id, "last_check = $v", ("$v", JsonSerializer.Serialize(findings.Select(f => new { where = f.Where, what = f.What }))));
        foreach (var f in findings) _log?.Invoke($"{member.Qualified}: git check: {f.Where}: {f.What}");
        return findings;
    }

    /// <summary>The clone half of the post-run check: its .git/config and object alternates.</summary>
    private async Task<IReadOnlyList<GitFinding>> ScrubCloneAsync(string clonePath, string label, CancellationToken ct)
    {
        var findings = new List<GitFinding>();
        var gitDir = Path.Combine(clonePath, ".git");
        if (!Directory.Exists(gitDir) || new DirectoryInfo(gitDir).LinkTarget is not null) return findings;
        var config = Path.Combine(gitDir, "config");
        if (File.Exists(config)) findings.AddRange(await ScrubConfigAsync(config, label + " .git/config", ownClone: true, ct));
        foreach (var name in new[] { "alternates", "http-alternates" })
        {
            var file = Path.Combine(gitDir, "objects", "info", name);
            if (File.Exists(file))
            {
                DeleteFile(file);
                findings.Add(new GitFinding(label + " .git/objects/info/" + name, "object alternates (removed)"));
            }
        }
        return findings;
    }

    private static readonly string[] ForbiddenSections = ["remote", "credential", "url", "http", "include", "includeif"];

    private async Task<IReadOnlyList<GitFinding>> ScrubConfigAsync(string file, string where, bool ownClone, CancellationToken ct)
    {
        var findings = new List<GitFinding>();
        // An agent may have made the file read-only. git rewrites config through config.lock and a rename, which
        // Windows refuses over a read-only file; on Linux/macOS the rename would succeed anyway. Clear it everywhere.
        ClearReadOnly(file);
        foreach (var section in GitConfigText.Sections(await File.ReadAllTextAsync(file, ct)))
        {
            if (!ForbiddenSections.Contains(section.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var ours = ownClone && section.Name.Equals("credential", StringComparison.OrdinalIgnoreCase) && section.Subsection is null
                && section.Keys.All(k => k.Key.Equals("helper", StringComparison.OrdinalIgnoreCase) && k.Value.Length == 0);
            var full = section.Subsection is null ? section.Name : $"{section.Name}.{section.Subsection}";
            if (!ours) findings.Add(new GitFinding(where, $"[{full}] {string.Join(", ", section.Keys.Select(k => k.Key))} (removed)"));
            var removed = await Git.TryRunAsync(null, ["config", "--file", file, "--remove-section", full], ct);
            if (removed.ExitCode != 0)
            {
                // Odd syntax git cannot address: drop the whole file rather than leave it.
                DeleteFile(file);
                findings.Add(new GitFinding(where, "config git could not edit (file deleted)"));
                return findings;
            }
        }
        if (ownClone && File.Exists(file)) await SetAsync(file, "credential.helper", "", ct);
        return findings;
    }

    /// <summary>Fetches the member's branch into the team repository (merging is the person's). Never pushes.</summary>
    public async Task<FetchResult> FetchBackAsync(MemberRef member, CancellationToken ct = default)
    {
        var repo = RepoOf(member.TeamId);
        if (repo is null || Row(member.Id) is null) return FetchResult.Nothing;
        var checkout = CheckoutOf(member, repo);
        var result = await FetchBranchAsync(member, repo.Path, repo.BaseBranch, checkout.ClonePath, checkout.Branch, SharedRepoPath(member.Team),
            error => UpdateClone(member.Id, "last_error = $e", ("$e", error)),
            sha => UpdateClone(member.Id, "fetched_at = $t, fetched_sha = $s, last_error = NULL", ("$t", Now()), ("$s", sha)), ct);
        if (result.Error is not null) return result;
        // The team's other writable repositories: their branches come back the same way. A failure is reported with
        // the repository's name; it never hides the primary's result.
        var extraErrors = new List<string>();
        foreach (var extra in ExtrasOf(member.TeamId).Where(e => e.Writable))
        {
            if (ExtraRow(member.Id, extra.Name) is not { } row) continue;
            var r = await FetchBranchAsync(member, extra.Path, extra.BaseBranch, row.Path, row.Branch, ExtraSharedPath(member.Team, extra.Name),
                error => UpdateExtraClone(member.Id, extra.Name, "last_error = $e", ("$e", error)),
                sha => UpdateExtraClone(member.Id, extra.Name, "fetched_at = $t, fetched_sha = $s, last_error = NULL", ("$t", Now()), ("$s", sha)), ct);
            if (r.Error is not null) extraErrors.Add($"{extra.Name}: {r.Error}");
        }
        return extraErrors.Count == 0 ? result : result with { Error = string.Join("; ", extraErrors) };
    }

    /// <summary>
    /// Why a member clone's .git must not be read by the host (null when it is a plain folder the host may fetch from).
    /// The clone is the agent's; the host never runs git in it, it only fetches from its .git folder (with
    /// <see cref="StrictUploadPack"/>) into a repository of its own. Refused: a clone folder or .git that is a link or
    /// another reparse point (junction); a .git/.git entry (upload-pack, given &lt;clone&gt;/.git, would try it first, and a
    /// gitfile there points anywhere); commondir; object alternates (they make the clone's objects another repository's);
    /// objects/refs missing or links.
    /// </summary>
    public static string? CloneGitDirProblem(string clonePath)
    {
        if (IsLink(new DirectoryInfo(clonePath))) return "the clone folder is a link (or junction)";
        var gitDir = Path.Combine(clonePath, ".git");
        if (IsLink(new FileInfo(gitDir)) || IsLink(new DirectoryInfo(gitDir))) return "the clone's .git is a link (or junction)";
        if (!Directory.Exists(gitDir)) return "the clone has no .git folder";
        if (Path.Exists(Path.Combine(gitDir, ".git"))) return "the clone's .git/.git exists (it could point git at another repository)";
        if (File.Exists(Path.Combine(gitDir, "commondir"))) return "the clone's .git points at another repository (commondir)";
        if (new[] { "objects", "refs" }.Any(d => new DirectoryInfo(Path.Combine(gitDir, d)) is var info && (!info.Exists || IsLink(info))))
            return "the clone's .git/objects or .git/refs is missing or a link";
        if (IsLink(new DirectoryInfo(Path.Combine(gitDir, "objects", "info")))) return "the clone's .git/objects/info is a link";
        foreach (var name in new[] { "alternates", "http-alternates" })
            if (Path.Exists(Path.Combine(gitDir, "objects", "info", name)))
                return $"the clone's .git/objects/info/{name} exists (object alternates point at another repository)";
        return null;
    }

    /// <summary>A symbolic link, a junction, or any other reparse point.</summary>
    private static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true; // cannot tell: treat as a link
        }
    }

    /// <summary>
    /// For every fetch or ls-remote from a member clone: upload-pack takes the given path as the repository exactly, and
    /// does not try &lt;path&gt;/.git or &lt;path&gt;.git first (a gitfile there could redirect it to any repository on the computer).
    /// </summary>
    public const string StrictUploadPack = "--upload-pack=git upload-pack --strict";

    /// <summary>
    /// Fetches a member's branch from its clone into a host repository (the team's, or a writable extra), then brings
    /// that repository's shared view up to date. Never pushes; never runs git inside the clone.
    /// </summary>
    private async Task<FetchResult> FetchBranchAsync(MemberRef member, string repoPath, string baseBranch, string clonePath, string branch,
        string sharedPath, Action<string> recordError, Action<string> recordFetched, CancellationToken ct)
    {
        var gitDir = Path.Combine(clonePath, ".git");
        var error = CloneGitDirProblem(clonePath);
        if (error is not null)
        {
            recordError(error);
            return new FetchResult(false, null, error);
        }

        var gate = _teamLocks.GetOrAdd(member.TeamId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            FetchResult Fail(string e)
            {
                recordError(e);
                _log?.Invoke($"{member.Qualified}: {e}");
                return new FetchResult(false, null, e);
            }
            var refName = "refs/heads/" + branch;
            var listed = await Git.TryRunAsync(repoPath, ["ls-remote", StrictUploadPack, "--", gitDir, refName], ct);
            if (listed.ExitCode != 0) return Fail("could not read the member's branch: " + listed.Stderr);
            var sha = listed.Stdout.Split('\t', 2)[0].Trim();
            if (sha.Length == 0) return FetchResult.Nothing; // no commit on the branch yet
            // A member that never committed (a Manager, usually) leaves no branch behind: nothing new to show.
            var baseSha = await Git.TryRunAsync(repoPath, ["rev-parse", "--verify", "-q", "refs/heads/" + baseBranch], ct);
            var known = await Git.TryRunAsync(repoPath, ["rev-parse", "--verify", "-q", refName], ct);
            if (known.ExitCode != 0 && baseSha.ExitCode == 0 && baseSha.Stdout == sha) return FetchResult.Nothing;
            // Nor does one whose branch is only an older point of the base branch (its clone was made before the base
            // moved on): nothing of its own. An unknown commit (exit 128) is the member's own work and is fetched.
            if (known.ExitCode != 0 && baseSha.ExitCode == 0
                && (await Git.TryRunAsync(repoPath, ["merge-base", "--is-ancestor", sha, "refs/heads/" + baseBranch], ct)).ExitCode == 0)
                return FetchResult.Nothing;
            // Into a staging ref first, never straight onto the branch: the host may have moved the branch since the
            // clone was made (a sync before new work), and a clone that has not pulled that must not put it back.
            var staging = FetchStagingPrefix + branch;
            var fetched = await Git.TryRunAsync(repoPath,
                ["fetch", "-q", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", StrictUploadPack, "--", gitDir, $"+{refName}:{staging}"], ct);
            if (fetched.ExitCode != 0) return Fail("could not fetch the member's branch: " + fetched.Stderr);
            recordFetched(sha);
            var landed = await LandFetchedAsync(member, repoPath, branch, sha, ct);
            if (landed is not null)
            {
                AddSyncNote(member.TeamId, landed);
                return Fail(landed);
            }
            _log?.Invoke($"{member.Qualified}: fetched {branch} at {sha[..Math.Min(12, sha.Length)]} into {repoPath}");
            try
            {
                await UpdateSharedAtAsync(sharedPath, repoPath, baseBranch, ct);
            }
            catch (GitException e)
            {
                _log?.Invoke($"team {member.Team}: the shared view of {repoPath} was not updated: {e.Message}");
            }
            return new FetchResult(true, sha, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Where a member's fetched branch lands first (refs/sandbox/fetch/&lt;branch&gt;): the host's, never in the bundle.</summary>
    public const string FetchStagingPrefix = "refs/sandbox/fetch/";

    /// <summary>
    /// Brings a fetched clone tip (<paramref name="sha"/>, at the staging ref) onto <paramref name="branch"/> in the host
    /// repository without ever moving the branch backwards: a new branch is created; a branch the tip contains is
    /// fast-forwarded; a branch that already contains the tip (the clone is behind, e.g. after a sync) is left alone;
    /// diverged ones are merged in the object database (merge-tree, commit-tree) when clean. Every move is a
    /// compare-and-swap update-ref. Returns null, or why the work could not be brought in (the branch is then unchanged
    /// and the work stays at the staging ref). Runs git only in the host repository; the caller holds the team lock.
    /// </summary>
    private async Task<string?> LandFetchedAsync(MemberRef member, string repoPath, string branch, string sha, CancellationToken ct)
    {
        var refName = "refs/heads/" + branch;
        var current = await RevAsync(repoPath, refName, ct);
        if (current == sha) return null;
        if (current is not null && await CheckedOutAnywhereAsync(repoPath, branch, ct) is { } where)
            return $"{branch} is checked out in {where}, so the app did not update it; the clone's work is at {FetchStagingPrefix}{branch}";
        string next;
        if (current is null || await IsAncestorAsync(repoPath, current, sha, ct)) next = sha;
        else if (await IsAncestorAsync(repoPath, sha, current, ct))
        {
            _log?.Invoke($"{member.Qualified}: the clone's {branch} ({Short(sha)}) is behind the team repository's ({Short(current)}); left as it is");
            return null;
        }
        else
        {
            var (tree, files) = await MergeTreeAsync(repoPath, current, sha, ct);
            if (tree is null) return ParkedNote(member.Name, branch, files);
            next = await Git.RunAsync(repoPath, ct, "-c", "user.name=" + ManagedIdentityName, "-c", "user.email=" + ManagedIdentityEmail,
                "commit-tree", tree, "-p", current, "-p", sha, "-m", $"Merge {member.Name}'s {branch} into the synced {branch}");
        }
        var update = await Git.TryRunAsync(repoPath, ["update-ref", "-m", "fetch back " + member.Qualified, refName, next, current ?? ""], ct);
        return update.ExitCode == 0 ? null : $"{branch} moved while its work was being brought in; the clone's work is at {FetchStagingPrefix}{branch}";
    }

    private static string ParkedNote(string memberName, string branch, IReadOnlyList<string> files) =>
        $"{memberName}'s new work on {branch} does not merge cleanly with the synced {branch} (conflicts in: {string.Join(", ", files)}), "
        + $"so {branch} was left as it was. {memberName} must merge {branch} from the team bundle into its branch and resolve the conflicts.";

    /// <summary>
    /// The fetched clone work that is still parked because it conflicts with its branch (a staging ref its branch does
    /// not contain): one note each. Read-only, in the host repository.
    /// </summary>
    private async Task<IReadOnlyList<string>> ParkedNotesAsync(long teamId, string repoPath, CancellationToken ct)
    {
        var notes = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT mc.branch, m.name FROM member_clones mc JOIN members m ON m.id = mc.member_id WHERE m.team_id = $t "
                + "UNION ALL SELECT x.branch, m.name FROM member_extra_clones x JOIN members m ON m.id = x.member_id WHERE m.team_id = $t;";
            cmd.Parameters.AddWithValue("$t", teamId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.TryAdd(r.GetString(0), r.GetString(1));
        }
        foreach (var line in (await Git.RunAsync(repoPath, ct, "for-each-ref", "--format=%(refname) %(objectname)", FetchStagingPrefix))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', 2);
            var branch = parts[0][FetchStagingPrefix.Length..];
            if (await RevAsync(repoPath, "refs/heads/" + branch, ct) is not { } tip || await IsAncestorAsync(repoPath, parts[1], tip, ct)) continue;
            if (await IsAncestorAsync(repoPath, tip, parts[1], ct)) continue; // a fast-forward the next fetch-back makes
            var (tree, files) = await MergeTreeAsync(repoPath, tip, parts[1], ct);
            if (tree is null) notes.Add(ParkedNote(names.GetValueOrDefault(branch, branch), branch, files));
        }
        return notes;
    }

    /// <summary>Where <paramref name="branch"/> is checked out in the host repository (its main or a linked worktree), or null.</summary>
    private async Task<string?> CheckedOutAnywhereAsync(string repoPath, string branch, CancellationToken ct)
    {
        var list = await Git.TryRunAsync(repoPath, ["worktree", "list", "--porcelain"], ct);
        string? dir = null;
        foreach (var line in list.Stdout.Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) dir = line[9..].Trim();
            else if (line.Trim() == "branch refs/heads/" + branch) return dir ?? repoPath;
        }
        return list.ExitCode == 0 ? null : (await CurrentBranchAsync(repoPath, ct) == branch ? repoPath : null);
    }

    /// <summary>
    /// Brings the team's shared view up to date with the team repository's branches (the caller holds the team lock).
    /// Both repositories are the host's own (the team repository was made or picked by the person; the shared view is
    /// never writable by a sandbox), so running git in them is safe.
    /// </summary>
    private Task UpdateSharedLockedAsync(string team, TeamRepo repo, CancellationToken ct) =>
        UpdateSharedAtAsync(SharedRepoPath(team), repo.Path, repo.BaseBranch, ct);

    private async Task UpdateSharedAtAsync(string shared, string repoPath, string baseBranch, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(shared, "objects")))
        {
            Directory.CreateDirectory(shared);
            await Git.RunAsync(null, ct, "init", "-q", "--bare", "-b", baseBranch, shared);
        }
        await Git.RunAsync(shared, ct, "fetch", "-q", "--no-tags", "--prune", "--no-write-fetch-head", "--no-recurse-submodules", "--",
            repoPath, "+refs/heads/*:refs/heads/*");
        await WriteSharedBundleAsync(shared, ct);
    }

    /// <summary>
    /// The file members fetch from (&lt;shared view&gt;/team.bundle, every branch). Fetching from a repository path
    /// starts git-upload-pack through a shell, and on Windows Git's sh.exe cannot start in a ProcessContainer (microsoft/mxc
    /// #1061:"Could not read from remote repository"); reading a bundle needs no other process, on every OS.
    /// Written to a temporary file and moved into place, so a member never reads a half-written bundle.
    /// </summary>
    private async Task WriteSharedBundleAsync(string shared, CancellationToken ct)
    {
        var bundle = Path.Combine(shared, SharedBundleFile);
        var heads = await Git.RunAsync(shared, ct, "for-each-ref", "--format=%(refname)", "refs/heads/");
        if (string.IsNullOrWhiteSpace(heads))
        {
            // git refuses to write an empty bundle; no branch means nothing to fetch.
            File.Delete(bundle);
            return;
        }
        var temp = bundle + ".tmp";
        File.Delete(temp);
        await Git.RunAsync(shared, ct, "bundle", "create", "-q", temp, "--branches");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, bundle, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                // A member may be reading the old bundle right now (Windows will not replace an open file).
                await Task.Delay(250, ct);
            }
        }
    }

    /// <summary>The bundle of every branch in the shared view, in the shared view's folder (so it is granted with it).</summary>
    public const string SharedBundleFile = "team.bundle";

    private FetchResult Failed(MemberRef member, string error)
    {
        UpdateClone(member.Id, "last_error = $e", ("$e", error));
        _log?.Invoke($"{member.Qualified}: {error}");
        return new FetchResult(false, null, error);
    }

    /// <summary>At start-up: checks and fetches back every clone, so work of runs the last process left open is visible.</summary>
    public async Task FetchBackAllAsync(IEnumerable<MemberRef> members, CancellationToken ct = default)
    {
        foreach (var member in members)
        {
            if (Row(member.Id) is null) continue;
            try
            {
                await CheckAndScrubAsync(member, ct);
                await FetchBackAsync(member, ct);
            }
            catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"{member.Qualified}: start-up fetch-back failed: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ publishing

    /// <summary>The branches the app never pushes: a push is not a merge.</summary>
    public static readonly IReadOnlySet<string> NeverPublished = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main", "master", "HEAD" };

    public PersonGitPush Pusher { get; init; } = new();

    /// <summary>
    /// Pushes every branch of the member's team repository that origin does not have yet: members' branches and the
    /// team branch, never the base branch or main/master. Called after each run's fetch-back (and so after the
    /// Manager's run in which it declared the workflow complete). Never forced; a refused branch does not stop the
    /// others; the first transport failure stops the push and names what did not go. Never throws for git: a failed
    /// push is reported, never the run's failure (a transport problem, not an agent fault).
    /// </summary>
    public async Task<TeamPublishReport> PublishAsync(MemberRef member, CancellationToken ct = default)
    {
        var repo = RepoOf(member.TeamId);
        if (repo is null || !Directory.Exists(repo.Path)) return TeamPublishReport.None;
        if (!PushesToOrigin(member.TeamId)) return TeamPublishReport.None; // the person turned pushing off for this team
        var report = await PublishRepoAsync(member, repo.Path, repo.BaseBranch, ct);
        // The team's other writable repositories are pushed the same way; their outcome is folded into the report.
        foreach (var extra in ExtrasOf(member.TeamId).Where(e => e.Writable && Directory.Exists(e.Path)))
            report = TeamPublishReport.Combine(report, extra.Name, await PublishRepoAsync(member, extra.Path, extra.BaseBranch, ct));
        return report;
    }

    private async Task<TeamPublishReport> PublishRepoAsync(MemberRef member, string repoPath, string baseBranch, CancellationToken ct)
    {
        var repo = (Path: repoPath, BaseBranch: baseBranch);
        var gate = _teamLocks.GetOrAdd(member.TeamId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var origin = await Git.TryRunAsync(repo.Path, ["config", "--get", "remote.origin.url"], ct);
            if (origin.ExitCode != 0 || origin.Stdout.Length == 0) return TeamPublishReport.None;

            var branches = (await Git.RunAsync(repo.Path, ct, "for-each-ref", "--format=%(refname:short)", "refs/heads/"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(b => !NeverPublished.Contains(b) && !string.Equals(b, repo.BaseBranch, StringComparison.Ordinal));
            var unpublished = new List<string>();
            foreach (var branch in branches)
            {
                // A local question first: commits on the branch that no remote-tracking ref has.
                var ahead = await Git.TryRunAsync(repo.Path, ["rev-list", "--count", "refs/heads/" + branch, "--not", "--remotes=origin"], ct);
                var isAhead = ahead.ExitCode != 0 || ahead.Stdout != "0";
                // The team branch is also compared with origin's copy: a fast-forward to a member's tip adds no commit,
                // yet origin's team branch has not moved.
                if (!isAhead && branch.StartsWith(TeamBranchPrefix, StringComparison.Ordinal))
                {
                    var local = await Git.TryRunAsync(repo.Path, ["rev-parse", "refs/heads/" + branch], ct);
                    var remote = await Git.TryRunAsync(repo.Path, ["rev-parse", "--verify", "-q", "refs/remotes/origin/" + branch], ct);
                    isAhead = remote.ExitCode != 0 || remote.Stdout != local.Stdout;
                    // ...unless the team branch is only a point of origin's base branch (no work of the team on it):
                    // a branch on origin that says nothing.
                    if (isAhead && remote.ExitCode != 0
                        && (await Git.TryRunAsync(repo.Path, ["merge-base", "--is-ancestor", "refs/heads/" + branch, "refs/remotes/origin/" + repo.BaseBranch], ct)).ExitCode == 0)
                        isAhead = false;
                }
                if (isAhead) unpublished.Add(branch);
            }
            if (unpublished.Count == 0) return TeamPublishReport.Nothing;

            var published = new List<string>();
            var rejected = new List<string>();
            string? rejection = null;
            for (var i = 0; i < unpublished.Count; i++)
            {
                var run = await Pusher.PushAsync(repo.Path, unpublished[i], ct);
                var output = PersonGitPush.Redact(string.Join('\n', run.Stderr, run.Stdout).Trim());
                if (run.ExitCode == 0)
                {
                    published.Add(unpublished[i]);
                    continue;
                }
                if (PersonGitPush.IsRefused(output))
                {
                    rejected.Add(unpublished[i]);
                    rejection ??= output;
                    continue;
                }
                _log?.Invoke($"team {member.Team}: push to origin failed: {output}");
                return new TeamPublishReport(TeamPublishResult.PushFailed, [.. rejected, .. unpublished.Skip(i)], output, published);
            }
            if (published.Count > 0) _log?.Invoke($"team {member.Team}: pushed {string.Join(", ", published)} to origin");
            return rejected.Count > 0
                ? new TeamPublishReport(TeamPublishResult.Rejected, rejected, rejection, published)
                : new TeamPublishReport(TeamPublishResult.Published, published);
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException)
        {
            return new TeamPublishReport(TeamPublishResult.PushFailed, [], PersonGitPush.Redact(ex.Message));
        }
        finally
        {
            gate.Release();
        }
    }

    // ------------------------------------------------------------------ store

    public sealed record CloneRow(string Path, string Branch, string? FetchedSha, string? LastError, string? LastCheck);

    public CloneRow? Row(long memberId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, branch, fetched_sha, last_error, last_check FROM member_clones WHERE member_id = $m;";
        cmd.Parameters.AddWithValue("$m", memberId);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new CloneRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4))
            : null;
    }

    private void Insert(long teamId, string path, bool managed, string baseBranch) =>
        Exec("INSERT INTO team_repos (team_id, path, managed, base_branch, created_at) VALUES ($t, $p, $m, $b, $c);",
            ("$t", teamId), ("$p", path), ("$m", managed ? 1 : 0), ("$b", baseBranch), ("$c", Now()));

    private void InsertClone(long memberId, string path, string branch) =>
        Exec("INSERT OR REPLACE INTO member_clones (member_id, path, branch, created_at) VALUES ($m, $p, $b, $c);",
            ("$m", memberId), ("$p", path), ("$b", branch), ("$c", Now()));

    private void UpdateClone(long memberId, string set, params (string, object)[] args) =>
        Exec($"UPDATE member_clones SET {set} WHERE member_id = $member;", [.. args, ("$member", memberId)]);

    private void Exec(string sql, params (string Name, object Value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Deletes a file an agent may have marked read-only: Windows refuses to delete a read-only file
    /// (UnauthorizedAccessException), which would leave a credential file in place and fail the run's fetch-back.
    /// </summary>
    private static void DeleteFile(string file)
    {
        ClearReadOnly(file);
        File.Delete(file);
    }

    private static void ClearReadOnly(string file)
    {
        var info = new FileInfo(file);
        if (info.Exists && info.LinkTarget is null && info.Attributes.HasFlag(FileAttributes.ReadOnly))
            info.Attributes &= ~FileAttributes.ReadOnly;
    }

    private static void CreatePrivate(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            return;
        }
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

/// <summary>Just enough of git's config syntax to list sections and their keys (for the post-run check).</summary>
public static class GitConfigText
{
    public sealed record Section(string Name, string? Subsection, IReadOnlyList<KeyValuePair<string, string>> Keys);

    public static IReadOnlyList<Section> Sections(string text)
    {
        var result = new List<Section>();
        string? name = null, sub = null;
        List<KeyValuePair<string, string>> keys = [];
        void Flush()
        {
            if (name is not null) result.Add(new Section(name, sub, keys));
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                Flush();
                keys = [];
                var end = line.IndexOf(']');
                var header = (end < 0 ? line[1..] : line[1..end]).Trim();
                var quote = header.IndexOf('"');
                if (quote >= 0)
                {
                    name = header[..quote].Trim();
                    sub = header[(quote + 1)..].TrimEnd('"').Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
                }
                else
                {
                    var dot = header.IndexOf('.');
                    (name, sub) = dot < 0 ? (header, null) : (header[..dot], header[(dot + 1)..]);
                }
                // A key may follow the header on the same line: "[remote "x"] url = ...".
                if (end >= 0 && end + 1 < line.Length) AddKey(line[(end + 1)..]);
                continue;
            }
            AddKey(line);
        }
        Flush();
        return result;

        void AddKey(string line)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') return;
            var eq = line.IndexOf('=');
            keys.Add(eq < 0 ? new(line, "true") : new(line[..eq].Trim(), line[(eq + 1)..].Trim().Trim('"')));
        }
    }
}
