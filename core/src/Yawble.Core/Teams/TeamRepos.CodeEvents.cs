using System.Globalization;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Monitor;

namespace Yawble.Core.Teams;

/// <summary>
/// The Monitor's code events, recorded when the host moves a branch in the TEAM repository (its primary one): a
/// fetch-back that lands a member's work (its new commits; on the team branch, the Manager's merges and own commits),
/// a Merge to main (landed) and a publish (push). Git runs only in the team repository, never in a member's clone; a
/// failure to record is logged and never fails the fetch-back, merge or push it describes.
/// </summary>
public sealed partial class TeamRepos
{
    /// <summary>The most commits one branch move records (a first fetch-back of a long history stays cheap).</summary>
    public const int MaxCodeEventCommits = 500;

    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private CodeEvents? _codeEvents;

    /// <summary>The store monitor.codeEvents reads (the same database as the rest of the team's state).</summary>
    public CodeEvents CodeEvents => _codeEvents ??= new CodeEvents(_connectionString, _clock);

    private sealed record LoggedCommit(string Sha, IReadOnlyList<string> Parents, DateTimeOffset At, string AuthorEmail, string Subject);

    private bool IsPrimaryRepo(long teamId, string repoPath) =>
        RepoOf(teamId) is { } primary && string.Equals(Path.GetFullPath(primary.Path).TrimEnd('\\', '/'), Path.GetFullPath(repoPath).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task SafeRecordAsync(string team, Func<Task<IReadOnlyList<CodeEvent>>> make, string what)
    {
        try
        {
            var events = await make();
            if (events.Count > 0) CodeEvents.Add(events);
        }
        catch (Exception e) when (e is GitException or SqliteException or IOException or FormatException or InvalidOperationException)
        {
            _log?.Invoke($"team {team}: the Monitor's code events for {what} were not recorded: {e.Message}");
        }
    }

    /// <summary>After a fetch-back moved <paramref name="branch"/> from <paramref name="before"/> to <paramref name="after"/>.</summary>
    internal Task RecordFetchedAsync(MemberRef member, string repoPath, string baseBranch, string branch, string? before, string after,
        long? deliverableId, CancellationToken ct) =>
        SafeRecordAsync(member.Team, async () => branch == TeamBranchFor(member.Team)
            ? await TeamBranchEventsAsync(member, repoPath, baseBranch, branch, before, after, deliverableId, ct)
            : await MemberCommitEventsAsync(member, repoPath, branch, before, after, deliverableId, ct), $"{member.Qualified}'s {branch}");

    /// <summary>
    /// A member branch's new commits: those on <paramref name="after"/> that no other branch (the base, a teammate's
    /// merged in, the team branch) and not <paramref name="before"/> has. Merge commits are not new work and are skipped.
    /// </summary>
    private async Task<IReadOnlyList<CodeEvent>> MemberCommitEventsAsync(MemberRef member, string repoPath, string branch, string? before, string after,
        long? deliverableId, CancellationToken ct)
    {
        var revs = new List<string> { "--no-merges", after };
        if (before is not null) revs.Add("^" + before);
        revs.AddRange(["--not", "--exclude=" + branch, "--branches", "--exclude=origin/" + branch, "--remotes=origin"]);
        var list = new List<CodeEvent>();
        foreach (var c in await LogAsync(repoPath, revs, ct))
            list.Add(new CodeEvent(CodeEvent.Commit, member.Team, member.Name, branch, c.Sha, c.Subject, await CommitFilesAsync(repoPath, c, ct), c.At, deliverableId));
        return list;
    }

    /// <summary>
    /// The team branch's new first-parent history: a merge commit of a member branch is a merge-team event (its files are
    /// what it brought in); a run of commits that are a member branch's own (a fast-forward to that member's tip) is one
    /// merge-team event; any other commit is the Manager's own (commit). A merge the host made (a sync, a fetch-back
    /// merge) is looked through: what its second side brought is classified the same way. The base branch's commits are
    /// never the team's.
    /// </summary>
    private async Task<IReadOnlyList<CodeEvent>> TeamBranchEventsAsync(MemberRef manager, string repoPath, string baseBranch, string branch,
        string? before, string after, long? deliverableId, CancellationToken ct)
    {
        var events = new List<CodeEvent>();
        var memberPrefix = "refs/heads/" + MemberBranchPrefix(manager.Team);
        var now = _clock.GetUtcNow();

        async Task<IReadOnlyList<string>> HoldersAsync(string sha) =>
            (await Git.RunAsync(repoPath, ct, "for-each-ref", "--contains", sha, "--format=%(refname:short)", memberPrefix))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        async Task MergedAsync(string memberBranch, string? from, string to, string message, DateTimeOffset at) =>
            events.Add(new CodeEvent(CodeEvent.MergeTeam, manager.Team, MemberForBranch(manager.TeamId, memberBranch), memberBranch, to, message,
                await RangeFilesAsync(repoPath, from, to, ct), at, deliverableId, Into: branch));

        async Task WalkAsync(string tip, IReadOnlyList<string> exclude, int depth)
        {
            var revs = new List<string> { "--first-parent", tip };
            revs.AddRange(exclude.Select(e => "^" + e));
            string? ffBranch = null, ffFrom = null;
            LoggedCommit? ffLast = null;
            async Task FlushAsync()
            {
                if (ffBranch is not null && ffLast is not null) await MergedAsync(ffBranch, ffFrom, ffLast.Sha, ffLast.Subject, now);
                ffBranch = null;
                ffFrom = null;
                ffLast = null;
            }
            foreach (var c in await LogAsync(repoPath, revs, ct))
            {
                var holders = await HoldersAsync(c.Sha);
                if (holders.Count > 0)
                {
                    var holder = ffBranch is not null && holders.Contains(ffBranch) ? ffBranch : holders[0];
                    if (holder != ffBranch)
                    {
                        await FlushAsync();
                        ffBranch = holder;
                        ffFrom = c.Parents.FirstOrDefault();
                    }
                    ffLast = c;
                    continue;
                }
                await FlushAsync();
                if (c.Parents.Count > 1)
                {
                    if (string.Equals(c.AuthorEmail, ManagedIdentityEmail, StringComparison.OrdinalIgnoreCase) && depth < 4)
                    {
                        await WalkAsync(c.Parents[1], [c.Parents[0], .. exclude], depth + 1);
                        continue;
                    }
                    var merged = (await HoldersAsync(c.Parents[1])).FirstOrDefault() ?? MemberBranchIn(c.Subject, manager.Team);
                    if (merged is not null) await MergedAsync(merged, c.Parents[0], c.Sha, c.Subject, c.At);
                    else events.Add(new CodeEvent(CodeEvent.Commit, manager.Team, manager.Name, branch, c.Sha, c.Subject,
                        await RangeFilesAsync(repoPath, c.Parents[0], c.Sha, ct), c.At, deliverableId));
                    continue;
                }
                events.Add(new CodeEvent(CodeEvent.Commit, manager.Team, manager.Name, branch, c.Sha, c.Subject, await CommitFilesAsync(repoPath, c, ct), c.At, deliverableId));
            }
            await FlushAsync();
        }

        var excluded = new List<string>();
        if (before is not null) excluded.Add(before);
        if (await RevAsync(repoPath, "refs/heads/" + baseBranch, ct) is { } local) excluded.Add(local);
        if (await RevAsync(repoPath, "refs/remotes/origin/" + baseBranch, ct) is { } remote) excluded.Add(remote);
        await WalkAsync(after, excluded, 0);
        return events;
    }

    /// <summary>A member branch named in a merge message ("Merge branch 'members/team/ada' ..."), when no branch holds the merged side.</summary>
    private static string? MemberBranchIn(string message, string team)
    {
        var prefix = MemberBranchPrefix(team);
        var i = message.IndexOf(prefix, StringComparison.Ordinal);
        if (i < 0) return null;
        var end = i;
        while (end < message.Length && !char.IsWhiteSpace(message[end]) && message[end] is not '\'' and not '"') end++;
        return message[i..end];
    }

    /// <summary>The member whose clone is on <paramref name="branch"/> (null when none is).</summary>
    private string? MemberForBranch(long teamId, string branch)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT m.name FROM member_clones mc JOIN members m ON m.id = mc.member_id WHERE m.team_id = $t AND mc.branch = $b LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", teamId);
        cmd.Parameters.AddWithValue("$b", branch);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>After a Merge to main: the team branch landed on the base (and, with an origin, the base was pushed).</summary>
    internal Task RecordLandedAsync(TeamRepo repo, string team, string teamBranch, string? start, string merged, bool pushed, CancellationToken ct) =>
        SafeRecordAsync(team, async () =>
        {
            if (!IsPrimaryRepo(repo.TeamId, repo.Path)) return [];
            var now = _clock.GetUtcNow();
            var subject = (await LogAsync(repo.Path, ["-1", merged], ct)).FirstOrDefault()?.Subject ?? "";
            var list = new List<CodeEvent>
            {
                new(CodeEvent.Landed, team, null, teamBranch, merged, subject, await RangeFilesAsync(repo.Path, start, merged, ct), now, Into: repo.BaseBranch),
            };
            if (pushed) list.Add(new CodeEvent(CodeEvent.Push, team, null, repo.BaseBranch, merged, $"Pushed {repo.BaseBranch} to origin", [], now, Branches: [repo.BaseBranch]));
            return list;
        }, "a merge into " + repo.BaseBranch);

    /// <summary>After a publish pushed <paramref name="published"/> to origin: one push event per branch.</summary>
    internal Task RecordPushedAsync(MemberRef member, string repoPath, IReadOnlyList<string> published, long? deliverableId, CancellationToken ct) =>
        SafeRecordAsync(member.Team, async () =>
        {
            if (published.Count == 0 || !IsPrimaryRepo(member.TeamId, repoPath)) return [];
            var now = _clock.GetUtcNow();
            var list = new List<CodeEvent>();
            foreach (var b in published)
            {
                if (await RevAsync(repoPath, "refs/heads/" + b, ct) is not { } sha) continue;
                var who = b == TeamBranchFor(member.Team) ? null : MemberForBranch(member.TeamId, b);
                list.Add(new CodeEvent(CodeEvent.Push, member.Team, who, b, sha, $"Pushed {b} to origin", [], now, deliverableId, Branches: published));
            }
            return list;
        }, "a push to origin");

    private async Task<IReadOnlyList<LoggedCommit>> LogAsync(string repoPath, IReadOnlyList<string> revs, CancellationToken ct)
    {
        var output = await Git.RunAsync(repoPath, ["log", "--reverse", "-n", MaxCodeEventCommits.ToString(CultureInfo.InvariantCulture),
            "--format=%H%x1f%P%x1f%cI%x1f%ae%x1f%s%x1e", .. revs, "--"], ct);
        var list = new List<LoggedCommit>();
        foreach (var record in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = record.Split('\x1f');
            if (f.Length < 5) continue;
            list.Add(new LoggedCommit(f[0], f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                DateTimeOffset.Parse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.None), f[3], f[4]));
        }
        return list;
    }

    /// <summary>A non-merge commit's files against its parent (or, for a root commit, all added).</summary>
    private async Task<IReadOnlyList<CodeFile>> CommitFilesAsync(string repoPath, LoggedCommit c, CancellationToken ct) =>
        ParseNameStatus(await Git.RunAsync(repoPath, ["diff-tree", "-r", "--root", "--no-commit-id", "--name-status", "-M", "-z", "--no-ext-diff", "--no-textconv", c.Sha], ct));

    /// <summary>The files that differ between <paramref name="from"/> (the empty tree when null) and <paramref name="to"/>.</summary>
    private async Task<IReadOnlyList<CodeFile>> RangeFilesAsync(string repoPath, string? from, string to, CancellationToken ct) =>
        ParseNameStatus(await Git.RunAsync(repoPath, ["diff", "--name-status", "-M", "-z", "--no-ext-diff", "--no-textconv", from ?? EmptyTree, to, "--"], ct));

    /// <summary>git's <c>--name-status -z</c> output: a status, then a path (two for a rename or copy). C counts as A, T as M.</summary>
    internal static IReadOnlyList<CodeFile> ParseNameStatus(string output)
    {
        var tokens = output.Split('\0');
        var list = new List<CodeFile>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var status = tokens[i].Trim('\n', ' ');
            if (status.Length == 0) continue;
            var letter = status[0];
            if (letter is 'R' or 'C')
            {
                if (i + 2 >= tokens.Length) break;
                var old = tokens[i + 1];
                var path = tokens[i + 2];
                list.Add(letter == 'R' ? new CodeFile(path, "R", old) : new CodeFile(path, "A"));
                i += 2;
                continue;
            }
            if (i + 1 >= tokens.Length) break;
            list.Add(new CodeFile(tokens[i + 1], letter switch { 'A' => "A", 'D' => "D", _ => "M" }));
            i++;
        }
        return list;
    }
}
