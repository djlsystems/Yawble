using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yawble.Core.Teams;

/// <summary>One commit an ID names: the team and repository it is in (<see cref="Repo"/> null: the team's main repository), its full ID, title and whether origin has it.</summary>
public sealed record CommitMatch(string Team, long TeamId, string? Repo, string FullId, string Title, bool Pushed)
{
    public JsonObject ToJson() => new()
    {
        ["team"] = Team, ["teamId"] = TeamId, ["repo"] = Repo, ["fullId"] = FullId, ["shortId"] = FullId[..Math.Min(12, FullId.Length)],
        ["title"] = Title, ["pushed"] = Pushed,
    };
}

/// <summary>One asked ID and the commits it names: none (not a commit), one, or more (ambiguous: the shell offers a choice).</summary>
public sealed record CommitIdResolution(string Id, bool Valid, IReadOnlyList<CommitMatch> Matches)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["valid"] = Valid, ["matches"] = new JsonArray([.. Matches.Select(m => (JsonNode)m.ToJson())]),
    };
}

/// <summary>A repository git.commitsResolve could not read.</summary>
public sealed record CommitResolveError(string Team, long TeamId, string? Repo, string Message);

/// <summary>git.commitsResolve (B074): every asked ID's matches, in the order asked, and the repositories that could not be read.</summary>
public sealed record CommitsResolved(IReadOnlyList<CommitIdResolution> Results, IReadOnlyList<CommitResolveError> Errors)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["max"] = TeamRepos.MaxResolveIds,
            ["results"] = new JsonArray([.. Results.Select(r => (JsonNode)r.ToJson())]),
        };
        if (Errors.Count > 0)
            o["errors"] = new JsonArray([.. Errors.Select(e => (JsonNode)new JsonObject
            {
                ["team"] = e.Team, ["teamId"] = e.TeamId, ["repo"] = e.Repo, ["message"] = e.Message,
            })]);
        return o;
    }
}

/// <summary>
/// B074: which of a batch of hex words are commits of the team's repositories (or every team's), for the shell to turn
/// commit IDs in text into Commit view links. The same rules as the Commit view (B072): only commit objects whose ID
/// starts with the digits match, never a branch or tag named like hex; pushed is "some origin branch has it". A
/// constant number of git processes per repository, whatever the batch: reads only, outside the team's lock.
/// </summary>
public sealed partial class TeamRepos
{
    /// <summary>The most distinct IDs one git.commitsResolve call answers.</summary>
    public const int MaxResolveIds = 200;

    /// <summary>The fewest and most digits an ID to resolve has (7: git's shortest usual abbreviation; 40: a full SHA-1).</summary>
    public const int MinResolveIdLength = 7, MaxResolveIdLength = 40;

    /// <summary>The most objects git.commitsResolve takes per asked ID in one repository (beyond: the ID is too short to mean one).</summary>
    private const int MaxResolveCandidatesPerId = 16;

    /// <summary>Revisions per git log / rev-list call, to stay well inside the command line's length on every OS.</summary>
    private const int ResolveArgsPerCall = 250;

    private static readonly Regex ResolveIdPattern = new($"^[0-9a-fA-F]{{{MinResolveIdLength},{MaxResolveIdLength}}}$", RegexOptions.CultureInvariant);
    private static readonly Regex FullHexPattern = new("^[0-9a-f]{40,64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// git.commitsResolve: each distinct asked ID (lower case, in the order asked) with the commits it names in the
    /// given teams' main repositories (only those that exist: nothing is created) and writable extra repositories.
    /// More than <see cref="MaxResolveIds"/> distinct IDs: <see cref="ArgumentException"/>.
    /// </summary>
    public async Task<CommitsResolved> ResolveCommitIdsAsync(IReadOnlyList<(long Id, string Name)> teams, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var asked = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in ids)
        {
            var id = (raw ?? "").Trim().ToLowerInvariant();
            if (seen.Add(id)) asked.Add(id);
        }
        if (asked.Count > MaxResolveIds)
            throw new ArgumentException($"at most {MaxResolveIds} commit IDs per call ({asked.Count} asked)");
        var valid = asked.Where(a => ResolveIdPattern.IsMatch(a)).ToList();

        var places = new List<(long TeamId, string Team, string? Repo, string Path)>();
        foreach (var (teamId, team) in teams)
        {
            if (RepoOf(teamId) is { } main) places.Add((teamId, team, null, main.Path));
            foreach (var extra in ExtrasOf(teamId).Where(e => e.Writable)) places.Add((teamId, team, extra.Name, extra.Path));
        }

        var found = new Dictionary<string, List<CommitMatch>>(StringComparer.Ordinal);
        var errors = new List<CommitResolveError>();
        if (valid.Count > 0)
        {
            var perRepo = await Task.WhenAll(places.Select(async p =>
            {
                try
                {
                    return (p, Commits: await ResolveInRepoAsync(p.Path, valid, ct), Error: (string?)null);
                }
                catch (Exception e) when (e is GitException or IOException or InvalidOperationException)
                {
                    return (p, Commits: (IReadOnlyList<(string Id, string Full, string Title, bool Pushed)>)[], Error: e.Message);
                }
            }));
            foreach (var (p, commits, error) in perRepo)
            {
                if (error is not null) errors.Add(new CommitResolveError(p.Team, p.TeamId, p.Repo, error));
                foreach (var c in commits)
                {
                    if (!found.TryGetValue(c.Id, out var list)) found[c.Id] = list = [];
                    list.Add(new CommitMatch(p.Team, p.TeamId, p.Repo, c.Full, c.Title, c.Pushed));
                }
            }
        }
        var results = asked.Select(a => new CommitIdResolution(a, ResolveIdPattern.IsMatch(a), found.TryGetValue(a, out var m) ? m : [])).ToList();
        return new CommitsResolved(results, errors);
    }

    /// <summary>
    /// The commits of one repository the asked IDs name (an ID may name several), in at most three git processes:
    /// <c>rev-parse --disambiguate</c> for every ID at once (object names only, never ref names) together with whether
    /// origin refs exist; <c>log --no-walk</c> for the candidates that are commits and their titles; <c>rev-list --not
    /// --remotes=origin</c> for the ones origin lacks.
    /// </summary>
    private async Task<IReadOnlyList<(string Id, string Full, string Title, bool Pushed)>> ResolveInRepoAsync(string path, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (!Directory.Exists(path)) throw new IOException($"the repository folder {path} is missing");
        var listed = await Git.RunAsync(path, ["rev-parse", "--symbolic-full-name", "--remotes=origin", .. ids.Select(i => "--disambiguate=" + i)], ct);
        var lines = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasOrigin = lines.Any(l => l.StartsWith("refs/remotes/origin/", StringComparison.Ordinal));
        var objects = lines.Where(l => FullHexPattern.IsMatch(l)).ToHashSet(StringComparer.Ordinal);

        // Each ID's candidates (capped: an ID that starts many objects' names means none of them in particular).
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var mine = objects.Where(o => o.StartsWith(id, StringComparison.Ordinal)).Take(MaxResolveCandidatesPerId + 1).ToList();
            if (mine.Count <= MaxResolveCandidatesPerId) candidates.UnionWith(mine);
        }
        if (candidates.Count == 0) return [];

        // Commits only: a blob or tree is dropped (--ignore-missing), an annotated tag peels to a commit whose ID does
        // not start with the digits and is dropped below.
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in candidates.Order(StringComparer.Ordinal).Chunk(ResolveArgsPerCall))
        {
            var log = await Git.TryRunAsync(path, ["log", "--no-walk=unsorted", "--ignore-missing", "--no-color", "--format=%H%x1f%s",
                .. chunk.Select(c => c + "^{commit}"), "--"], ct);
            if (log.ExitCode != 0) throw new GitException($"git log could not read the commits: {log.Stderr}");
            foreach (var line in log.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\x1f', 2);
                var full = parts[0].Trim();
                if (candidates.Contains(full)) titles[full] = parts.Length > 1 ? parts[1].Trim() : "";
            }
        }
        if (titles.Count == 0) return [];

        var notPushed = new HashSet<string>(StringComparer.Ordinal);
        if (hasOrigin)
        {
            foreach (var chunk in titles.Keys.Order(StringComparer.Ordinal).Chunk(ResolveArgsPerCall))
            {
                var unpushed = await Git.RunAsync(path, ["rev-list", .. chunk, "--not", "--remotes=origin", "--"], ct);
                foreach (var line in unpushed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (titles.ContainsKey(line)) notPushed.Add(line);
            }
        }
        var matches = new List<(string, string, string, bool)>();
        foreach (var id in ids)
            foreach (var (full, title) in titles.Where(t => t.Key.StartsWith(id, StringComparison.Ordinal)).OrderBy(t => t.Key, StringComparer.Ordinal))
                matches.Add((id, full, title, hasOrigin && !notPushed.Contains(full)));
        return matches;
    }
}
