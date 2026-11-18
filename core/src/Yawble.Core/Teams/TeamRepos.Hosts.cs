namespace Yawble.Core.Teams;

/// <summary>
/// One of a team's repositories as the provider layer sees it (B075): where it is hosted (detected from its origin, or
/// the person's override), its link pattern, and whether pull requests can be made from here.
/// </summary>
public sealed record RepoHostView(
    string Repo, string? Origin, RepoProviderKind Provider, RepoProviderKind Detected, RepoProviderKind? Override, string? LinkPattern,
    bool PrSupported, string? PrUnavailable);

public sealed partial class TeamRepos
{
    /// <summary>The person's override for one repository ("main" or an extra's name); null: none (detected).</summary>
    public RepoHostSetting? HostSettingOf(long teamId, string? repo)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT provider, link_pattern FROM team_repo_hosts WHERE team_id = $t AND repo = $r COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$t", teamId);
        cmd.Parameters.AddWithValue("$r", HostKey(repo));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new RepoHostSetting(r.IsDBNull(0) ? null : RepoProviders.FromWire(r.GetString(0)), r.IsDBNull(1) ? null : r.GetString(1));
    }

    /// <summary>The override by team name (the Concierge's read tools know the team by name).</summary>
    public RepoHostSetting? HostSettingOf(string teamName, string? repo)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM teams WHERE name = $n COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$n", teamName);
        return cmd.ExecuteScalar() is long id ? HostSettingOf(id, repo) : null;
    }

    /// <summary>
    /// Sets (or, with neither a provider nor a pattern, clears) a repository's override. A pattern that fits no link is
    /// refused (<see cref="ArgumentException"/>); the repository must be the team's.
    /// </summary>
    public void SetHostSetting(long teamId, string? repo, RepoProviderKind? provider, string? linkPattern)
    {
        var key = HostKey(repo);
        if (!string.Equals(key, MainRepoName, StringComparison.OrdinalIgnoreCase) && !ExtrasOf(teamId).Any(e => string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"the team has no repository named {key}");
        var pattern = string.IsNullOrWhiteSpace(linkPattern) ? null : linkPattern.Trim();
        if (RepoProviders.PatternProblem(pattern) is { } problem) throw new ArgumentException(problem);
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (provider is null && pattern is null)
            cmd.CommandText = "DELETE FROM team_repo_hosts WHERE team_id = $t AND repo = $r COLLATE NOCASE;";
        else
        {
            cmd.CommandText = "DELETE FROM team_repo_hosts WHERE team_id = $t AND repo = $r COLLATE NOCASE; " +
                              "INSERT INTO team_repo_hosts (team_id, repo, provider, link_pattern, updated_at) VALUES ($t, $r, $p, $l, $u);";
            cmd.Parameters.AddWithValue("$p", provider is { } k ? RepoProviders.WireName(k) : DBNull.Value);
            cmd.Parameters.AddWithValue("$l", (object?)pattern ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O"));
        }
        cmd.Parameters.AddWithValue("$t", teamId);
        cmd.Parameters.AddWithValue("$r", key);
        cmd.ExecuteNonQuery();
    }

    private static string HostKey(string? repo) => string.IsNullOrWhiteSpace(repo) ? MainRepoName : repo.Trim();

    /// <summary>The folder of a team's repository by name ("main" or none: the main one; else an extra, a reference included); null when unknown.</summary>
    public string? HostRepoPath(long teamId, string? repo, out string name)
    {
        name = HostKey(repo);
        var key = name;
        if (ExtrasOf(teamId).FirstOrDefault(e => string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase)) is { } extra) { name = extra.Name; return extra.Path; }
        if (string.Equals(key, MainRepoName, StringComparison.OrdinalIgnoreCase)) { name = MainRepoName; return RepoOf(teamId)?.Path; }
        return null;
    }

    /// <summary>A repository's origin address as git has it (unredacted: for detection only, never shown), or null.</summary>
    public async Task<string?> OriginOfAsync(string? path, CancellationToken ct)
    {
        if (path is null || !Directory.Exists(path)) return null;
        var r = await Git.TryRunAsync(path, ["config", "--get", "remote.origin.url"], ct);
        return r.ExitCode == 0 && r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : null;
    }

    /// <summary>The provider layer's view of one repository (teams.repos' provider fields).</summary>
    public async Task<RepoHostView> HostViewAsync(long teamId, string? repo, CancellationToken ct)
    {
        var path = HostRepoPath(teamId, repo, out var name);
        return HostView(name, await OriginOfAsync(path, ct), HostSettingOf(teamId, name));
    }

    /// <summary>The view from an origin and an override; whether the CLI is on PATH is <paramref name="installed"/> (default: <see cref="PullRequestProviders.IsInstalled"/>).</summary>
    public static RepoHostView HostView(string repo, string? origin, RepoHostSetting? setting, Func<string, bool>? installed = null)
    {
        var detected = RepoProviders.Detect(origin);
        var provider = setting?.Provider ?? detected;
        var why = RepoHost.PullRequestsUnavailable(repo, origin, provider);
        if (why is null && PullRequestProviders.CliOf(provider) is { } cli && !(installed ?? PullRequestProviders.IsInstalled)(cli))
            why = $"pull requests for {RepoProviders.Label(provider)} are made from here with {cli}, which is not installed on this computer; " +
                  $"install it and sign it in ({PullRequestProviders.SignInOf(cli)}), or open the pull request on {RepoProviders.Label(provider)}";
        return new RepoHostView(repo, origin, provider, detected, setting?.Provider, setting?.LinkPattern, why is null, why);
    }

    /// <summary>
    /// git_web_link (B075): the web address of a commit, branch, file at a commit, compare or pull request of a team's
    /// repository, or null: no origin, no provider layout or pattern for it, or the commit/branch is not on origin (as of
    /// the last fetch: an unpushed commit has no page there).
    /// </summary>
    public async Task<string?> GitWebLinkAsync(long teamId, string? repo, WebLinkRequest req, CancellationToken ct)
    {
        var path = HostRepoPath(teamId, repo, out var name);
        if (path is null)
        {
            // The main repository not made yet: nothing is on origin, so no link.
            if (name == MainRepoName) return null;
            throw new ArgumentException($"the team has no repository named {name}");
        }
        var origin = await OriginOfAsync(path, ct);
        if (origin is null) return null;
        var url = RepoProviders.Link(origin, HostSettingOf(teamId, name), req);
        if (url is null) return null;
        var onOrigin = req.Kind switch
        {
            WebLinkKind.Commit or WebLinkKind.File => await CommitOnOriginAsync(path, req.Commit!, ct),
            WebLinkKind.Branch => await BranchOnOriginAsync(path, req.Branch!, ct),
            WebLinkKind.Compare => await RefOnOriginAsync(path, req.From!, ct) && await RefOnOriginAsync(path, req.To!, ct),
            _ => true,
        };
        return onOrigin ? url : null;
    }

    private async Task<bool> BranchOnOriginAsync(string path, string branch, CancellationToken ct) =>
        (await Git.TryRunAsync(path, ["rev-parse", "--verify", "-q", "refs/remotes/origin/" + branch], ct)).ExitCode == 0;

    /// <summary>A commit some origin branch holds (as of the last fetch).</summary>
    private async Task<bool> CommitOnOriginAsync(string path, string sha, CancellationToken ct)
    {
        var full = await Git.TryRunAsync(path, ["rev-parse", "--verify", "-q", sha + "^{commit}"], ct);
        if (full.ExitCode != 0) return false;
        var r = await Git.TryRunAsync(path, ["for-each-ref", "--contains", full.Stdout.Trim(), "--count=1", "--format=%(refname)", "refs/remotes/origin"], ct);
        return r.ExitCode == 0 && r.Stdout.Trim().Length > 0;
    }

    private async Task<bool> RefOnOriginAsync(string path, string name, CancellationToken ct) =>
        await BranchOnOriginAsync(path, name, ct) || (RepoProviders.IsSha(name) && await CommitOnOriginAsync(path, name, ct));
}
