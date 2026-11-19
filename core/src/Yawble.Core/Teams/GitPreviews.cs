using System.Globalization;
using System.Text;

namespace Yawble.Core.Teams;

/// <summary>An author as a commit list shows it.</summary>
public sealed record CommitAuthor(string Name, string Email);

/// <summary>
/// B073: one commit of a list (newest first everywhere). <see cref="NotPushed"/> is THE local-only flag: no origin
/// remote-tracking ref has it, as of the last fetch (always true when the repository has no origin), computed only by
/// <see cref="GitPreviews.NotPushedAsync"/>. <see cref="Date"/>: the author date, strict ISO 8601.
/// </summary>
public record CommitRow(string Sha, string ShortSha, string Subject, CommitAuthor Author, string Date, bool IsMerge, bool NotPushed);

/// <summary>A commit of a branch about to be deleted, marked by git cherry: <c>in-base</c> (the same change is in the base), <c>own</c> or <c>merge</c>.</summary>
public sealed record CherryCommitRow(string Sha, string ShortSha, string Subject, CommitAuthor Author, string Date, bool IsMerge, bool NotPushed, string State)
    : CommitRow(Sha, ShortSha, Subject, Author, Date, IsMerge, NotPushed)
{
    public const string InBase = "in-base", Own = "own", Merge = "merge";
}

/// <summary>The exact pair a count stands for: the commits on <see cref="To"/> that <see cref="From"/> lacks (full IDs).</summary>
public sealed record CommitRangeRef(string From, string To);

/// <summary>THE branch local-only flag: whether origin has a branch of that name, and how many of its commits no origin ref has.</summary>
public sealed record BranchPush(string Branch, bool OnOrigin, int NotPushed);

/// <summary>Commits of a range: at most the limit asked, newest first; <see cref="Count"/> is the whole range.</summary>
public sealed record CommitList(IReadOnlyList<CommitRow> Commits, int Count, bool Truncated)
{
    public static readonly CommitList Empty = new([], 0, false);
}

/// <summary>git.commitRange: the commits behind a count.</summary>
public sealed record CommitRange(string From, string To, string FromSha, string ToSha, int Count, bool Truncated, bool HasOrigin, IReadOnlyList<CommitRow> Commits);

/// <summary>git.mergePreview: what Merge to main would do, computed without moving anything.</summary>
public sealed record MergePreview(
    string BaseBranch, string TeamBranch, string Outcome, string Summary, string Text, string? From, string? To, string? NewSha,
    IReadOnlyList<string> ConflictFiles, IReadOnlyList<CommitRow> Commits, int Count, bool Truncated, IReadOnlyList<CommitFileChange> Files,
    string? DiffFrom, string? DiffTo, bool HasOrigin, BranchPush? TeamPush);

/// <summary>git.deletePreview: what deleting one branch loses.</summary>
public sealed record DeletePreview(
    string Branch, string BaseBranch, bool Exists, string? Sha, bool Merged, IReadOnlyList<CherryCommitRow> Commits, int Own, int InBase,
    bool Truncated, string Lost, string Text, bool HasOrigin, BranchPush? Push);

/// <summary>
/// B073: the commit lists and the Merge / Delete previews, read with plain git in the repository (never a member's clone).
/// One implementation for the Git dialog's calls (TeamRepos) and the Concierge's tools (ConciergeGit, ConciergeReadTools),
/// so both show the same text. Reads only: <c>git merge-tree --write-tree</c> writes objects, never a ref or a file.
/// </summary>
public sealed class GitPreviews(HostGit git)
{
    /// <summary>The default number of commits a list carries.</summary>
    public const int DefaultLimit = 200;

    /// <summary>The most commits a list carries.</summary>
    public const int MaxLimit = 1000;

    /// <summary>The most files the preview TEXT lists (the payload has them all).</summary>
    public const int MaxTextFiles = 50;

    /// <summary>The most commits the preview TEXT lists.</summary>
    public const int MaxTextCommits = 50;

    private const string Sep = "\x1f";

    public static string Short(string sha) => sha[..Math.Min(12, sha.Length)];

    private static string Plural(int n, string one) => $"{n.ToString(CultureInfo.InvariantCulture)} {one}{(n == 1 ? "" : "s")}";

    public async Task<bool> HasOriginAsync(string path, CancellationToken ct)
    {
        var r = await git.TryRunAsync(path, ["config", "--get", "remote.origin.url"], ct);
        return r.ExitCode == 0 && r.Stdout.Trim().Length > 0;
    }

    public async Task<string?> RevAsync(string path, string reference, CancellationToken ct)
    {
        var r = await git.TryRunAsync(path, ["rev-parse", "--verify", "-q", reference + "^{commit}"], ct);
        return r.ExitCode == 0 && r.Stdout.Length > 0 ? r.Stdout.Trim() : null;
    }

    private async Task<bool> IsAncestorAsync(string path, string ancestor, string of, CancellationToken ct) =>
        (await git.TryRunAsync(path, ["merge-base", "--is-ancestor", ancestor, of], ct)).ExitCode == 0;

    private static IEnumerable<string> Excluding(IEnumerable<string?> notIn) => notIn.Where(n => n is not null).Distinct().Select(n => "^" + n);

    /// <summary>Commits on <paramref name="to"/> that none of <paramref name="notIn"/> has.</summary>
    public async Task<int> CountAsync(string path, string to, IEnumerable<string?> notIn, CancellationToken ct) =>
        int.TryParse((await git.TryRunAsync(path, ["rev-list", "--count", to, .. Excluding(notIn), "--"], ct)).Stdout.Trim(), out var n) ? n : 0;

    /// <summary>
    /// THE local-only flag for commits: of the range <paramref name="to"/> minus <paramref name="notIn"/>, the commits no
    /// origin remote-tracking ref has (as of the last fetch). With no origin refs at all, every commit of the range.
    /// </summary>
    public async Task<HashSet<string>> NotPushedAsync(string path, string to, IEnumerable<string?> notIn, CancellationToken ct)
    {
        var r = await git.TryRunAsync(path, ["rev-list", to, .. Excluding(notIn), "--not", "--remotes=origin", "--"], ct);
        return r.ExitCode == 0
            ? r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
            : [];
    }

    /// <summary>THE local-only flag for a branch (null when there is no such local branch).</summary>
    public async Task<BranchPush?> BranchPushAsync(string path, string branch, CancellationToken ct)
    {
        if (await RevAsync(path, "refs/heads/" + branch, ct) is not { } tip) return null;
        var onOrigin = await RevAsync(path, "refs/remotes/origin/" + branch, ct) is not null;
        var count = await git.TryRunAsync(path, ["rev-list", "--count", tip, "--not", "--remotes=origin", "--"], ct);
        return new BranchPush(branch, onOrigin, int.TryParse(count.Stdout.Trim(), out var n) ? n : 0);
    }

    /// <summary>The commits on <paramref name="to"/> that none of <paramref name="notIn"/> has, newest first, at most <paramref name="limit"/>.</summary>
    public async Task<CommitList> ListAsync(string path, string to, IEnumerable<string?> notIn, int limit, CancellationToken ct)
    {
        var excluded = notIn.ToList();
        limit = Math.Clamp(limit, 1, MaxLimit);
        var count = await CountAsync(path, to, excluded, ct);
        if (count == 0) return CommitList.Empty;
        var log = await git.RunAsync(path, ["log", "--no-color", "-n", limit.ToString(CultureInfo.InvariantCulture),
            $"--format=%H{Sep}%P{Sep}%an{Sep}%ae{Sep}%aI{Sep}%s%x1e", to, .. Excluding(excluded), "--"], ct);
        var notPushed = await NotPushedAsync(path, to, excluded, ct);
        var rows = new List<CommitRow>();
        foreach (var record in log.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.Trim('\n', '\r').Split('\x1f');
            if (f.Length < 6) continue;
            var sha = f[0].Trim();
            rows.Add(new CommitRow(sha, Short(sha), f[5], new CommitAuthor(f[2], f[3]), f[4],
                f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 1, notPushed.Contains(sha)));
        }
        return new CommitList(rows, count, count > rows.Count);
    }

    /// <summary>The merged tree, or null with the conflicting files (no ref or file moves).</summary>
    public async Task<(string? Tree, IReadOnlyList<string> Files)> MergeTreeAsync(string path, string ours, string theirs, CancellationToken ct)
    {
        var r = await git.TryRunAsync(path, ["merge-tree", "--write-tree", "--name-only", "--no-messages", ours, theirs], ct);
        var lines = r.Stdout.Split('\n');
        if (r.ExitCode == 0) return (lines[0].Trim(), []);
        if (r.ExitCode == 1) return (null, lines.Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToArray());
        throw new GitException(GitProgram.LooksTooOld(r.Stderr) ? GitProgram.TooOldMessage("git merge-tree") : $"git merge-tree failed ({r.ExitCode}): {r.Stderr}");
    }

    /// <summary>The files that differ between two commits or trees, with their +/- counts (renames detected).</summary>
    public async Task<IReadOnlyList<CommitFileChange>> DiffFilesAsync(string path, string from, string to, CancellationToken ct)
    {
        string[] common = ["diff", "-z", "-M", "--no-ext-diff", "--no-textconv", "--no-color"];
        var names = TeamRepos.ParseNameStatus(await git.RunAsync(path, [.. common, "--name-status", from, to, "--"], ct));
        var counts = TeamRepos.ParseNumstat(await git.RunAsync(path, [.. common, "--numstat", from, to, "--"], ct));
        return names.Select(n =>
        {
            var (added, deleted, binary) = counts.GetValueOrDefault(n.Path);
            return new CommitFileChange(n.Path, n.OldPath, n.Status switch { "A" => "added", "D" => "deleted", "R" => "renamed", _ => "modified" }, added, deleted, binary);
        }).ToList();
    }

    /// <summary>One file of the diff between two commits or trees, as hunks (binary or too large: a summary only).</summary>
    public async Task<CommitFileDiff> FileDiffAsync(string path, string from, string to, string file, CancellationToken ct)
    {
        var files = await DiffFilesAsync(path, from, to, ct);
        var change = files.FirstOrDefault(c => c.Path == file) ?? files.FirstOrDefault(c => c.OldPath == file)
                     ?? throw new TeamGitRefusal("no_such_file", $"the diff does not change {file}");
        CommitFileDiff Summary(bool binary, bool tooLarge, string text) =>
            new(change.Path, change.OldPath, change.Status, binary, tooLarge, text, change.Added, change.Deleted, []);
        if (change.Binary) return Summary(true, false, $"Binary file {change.Status}; its content is not shown.");
        if (change.Added + change.Deleted > TeamRepos.MaxCommitDiffLines) return Summary(false, true, TeamRepos.TooLargeText(change));
        string[] paths = change.OldPath is { } old ? [old, change.Path] : [change.Path];
        var text = await git.RunAsync(path, ["--literal-pathspecs", "diff", "-M", "--no-ext-diff", "--no-textconv", "--no-color", "-U3", from, to, "--", .. paths], ct);
        if (text.Length > TeamRepos.MaxCommitDiffChars) return Summary(false, true, TeamRepos.TooLargeText(change));
        var hunks = TeamRepos.ParseUnifiedDiff(text);
        return new CommitFileDiff(change.Path, change.OldPath, change.Status, false, false,
            hunks.Count == 0 ? "No lines changed: only the file's name or mode changed." : null, change.Added, change.Deleted, hunks);
    }

    // ------------------------------------------------------------------ Merge to main

    /// <summary>
    /// What Merge to main would do with the repository as it is now (origin as of the last fetch: the merge itself fetches
    /// first): the same start, refusals and outcomes as TeamRepos' merge, and nothing moves. <paramref name="dirty"/> is the
    /// merge's own check of a checked-out base with uncommitted changes (its refusal sentence, or null), run at the same
    /// point as the merge runs it, so the preview refuses exactly when Merge would.
    /// </summary>
    public async Task<MergePreview> MergePreviewAsync(string path, string baseBranch, string teamBranch, CancellationToken ct, int limit = DefaultLimit,
        Func<CancellationToken, Task<string?>>? dirty = null)
    {
        var hasOrigin = await HasOriginAsync(path, ct);
        var teamSha = await RevAsync(path, "refs/heads/" + teamBranch, ct);
        MergePreview Plain(string outcome, string summary, string? from = null) =>
            new(baseBranch, teamBranch, outcome, summary, summary, from, teamSha, null, [], [], 0, false, [], null, null, hasOrigin, null);
        if (teamSha is null) return Plain("none", $"There is no {teamBranch} yet: nothing to merge.");
        var teamPush = await BranchPushAsync(path, teamBranch, ct);
        var local = await RevAsync(path, "refs/heads/" + baseBranch, ct);
        var remote = hasOrigin ? await RevAsync(path, "refs/remotes/origin/" + baseBranch, ct) : null;
        string? start;
        if (remote is null) start = local;
        else if (local is null || local == remote || await IsAncestorAsync(path, local, remote, ct)) start = remote;
        else
        {
            var mine = await CountAsync(path, local, [remote], ct);
            return Plain("refused", $"Your {baseBranch} has {Plural(mine, "commit")} that origin does not have, so Merge to main would refuse: "
                + "push them (or drop them) yourself first. Nothing changes.") with { TeamPush = teamPush };
        }
        if (start is null) return Plain("refused", $"There is no {baseBranch} branch to merge into.") with { TeamPush = teamPush };
        if (await IsAncestorAsync(path, teamSha, start, ct))
            return Plain("up-to-date", $"{baseBranch} already has everything on {teamBranch}: nothing to merge.", start) with { TeamPush = teamPush };
        if (dirty is not null && await dirty(ct) is { } refusal) return Plain("refused", refusal, start) with { TeamPush = teamPush };

        var list = await ListAsync(path, teamSha, [start], limit, ct);
        string outcome, summary;
        string? newSha = null, diffFrom = start, diffTo;
        IReadOnlyList<string> conflicts = [];
        if (await IsAncestorAsync(path, start, teamSha, ct))
        {
            outcome = "fast-forward";
            newSha = teamSha;
            diffTo = teamSha;
            summary = $"{baseBranch} will fast-forward to {Short(teamSha)}: {Plural(list.Count, "commit")} land.";
        }
        else
        {
            var (tree, files) = await MergeTreeAsync(path, start, teamSha, ct);
            if (tree is null)
            {
                outcome = "conflict";
                conflicts = files;
                var mb = await git.TryRunAsync(path, ["merge-base", start, teamSha], ct);
                diffFrom = mb.ExitCode == 0 && mb.Stdout.Trim().Length > 0 ? mb.Stdout.Trim() : null;
                diffTo = teamSha;
                summary = $"Merging {teamBranch} would conflict in {string.Join(", ", files)}: nothing changes.";
            }
            else
            {
                outcome = "merge";
                diffTo = tree;
                var baseOnly = await CountAsync(path, start, [teamSha], ct);
                summary = $"A merge commit will be made: {teamBranch} brings {Plural(list.Count, "commit")}, and {baseBranch} has {Plural(baseOnly, "commit")} {teamBranch} lacks.";
            }
        }
        IReadOnlyList<CommitFileChange> changed = diffFrom is null ? [] : await DiffFilesAsync(path, diffFrom, diffTo, ct);
        if (changed.Count == 0) diffFrom = diffTo = null;
        var text = MergeText(summary, outcome, baseBranch, teamBranch, list, changed, hasOrigin);
        return new MergePreview(baseBranch, teamBranch, outcome, summary, text, start, teamSha, newSha, conflicts, list.Commits, list.Count, list.Truncated,
            changed, diffFrom, diffTo, hasOrigin, teamPush);
    }

    /// <summary>The merge preview as text: the Git dialog's <c>text</c> and the Concierge's git_merge_preview answer.</summary>
    public static string MergeText(string summary, string outcome, string baseBranch, string teamBranch, CommitList list, IReadOnlyList<CommitFileChange> files, bool hasOrigin)
    {
        var sb = new StringBuilder(summary);
        if (list.Count > 0)
        {
            sb.Append('\n').Append(outcome == "conflict"
                ? $"{Plural(list.Count, "commit")} on {teamBranch} not in {baseBranch}:"
                : $"{Plural(list.Count, "commit")} will land on {baseBranch}:");
            foreach (var c in list.Commits.Take(MaxTextCommits)) sb.Append('\n').Append(CommitLine(c, hasOrigin));
            if (list.Count > Math.Min(list.Commits.Count, MaxTextCommits)) sb.Append($"\n  ... and {list.Count - Math.Min(list.Commits.Count, MaxTextCommits)} more");
        }
        if (files.Count > 0)
        {
            sb.Append('\n').Append(outcome == "conflict" ? $"Files {teamBranch} changes ({files.Count}):" : $"Files changed ({files.Count}):");
            foreach (var f in files.Take(MaxTextFiles))
                sb.Append($"\n  {f.Status} {(f.OldPath is { } o ? o + " -> " : "")}{f.Path}{(f.Binary ? " (binary)" : $" (+{f.Added} -{f.Deleted})")}");
            if (files.Count > MaxTextFiles) sb.Append($"\n  ... and {files.Count - MaxTextFiles} more");
        }
        if (hasOrigin) sb.Append($"\n(origin as of the last fetch; Merge to main fetches first.)");
        return sb.ToString();
    }

    private static string CommitLine(CommitRow c, bool hasOrigin, string? state = null) =>
        $"  - {c.ShortSha} {c.Subject} ({c.Author.Name}){state}{(c.NotPushed ? hasOrigin ? ", not pushed" : ", local only" : "")}";

    // ------------------------------------------------------------------ Delete a branch

    /// <summary>
    /// What deleting the local branch <paramref name="branch"/> loses: its commits not in the base (local or origin's), each
    /// marked by <c>git cherry</c> as already in the base (the same change) or its own, and a one-line summary.
    /// </summary>
    public async Task<DeletePreview> DeletePreviewAsync(string path, string baseBranch, string branch, CancellationToken ct, int limit = DefaultLimit)
    {
        var hasOrigin = await HasOriginAsync(path, ct);
        var tip = await RevAsync(path, "refs/heads/" + branch, ct);
        if (tip is null)
        {
            var none = $"There is no branch {branch} here: nothing to delete.";
            return new DeletePreview(branch, baseBranch, false, null, false, [], 0, 0, false, none, none, hasOrigin, null);
        }
        var push = await BranchPushAsync(path, branch, ct);
        var local = await RevAsync(path, "refs/heads/" + baseBranch, ct);
        var remote = hasOrigin ? await RevAsync(path, "refs/remotes/origin/" + baseBranch, ct) : null;
        var merged = local is not null && await IsAncestorAsync(path, tip, local, ct) || remote is not null && await IsAncestorAsync(path, tip, remote, ct);
        var list = merged ? CommitList.Empty : await ListAsync(path, tip, [local, remote], limit, ct);

        // git cherry against the base Merge to main would use: origin's when the local one is only behind it.
        var upstream = remote is not null && (local is null || await IsAncestorAsync(path, local, remote, ct)) ? remote : local;
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list.Count > 0 && upstream is not null)
        {
            var cherry = await git.TryRunAsync(path, ["cherry", upstream, tip], ct);
            if (cherry.ExitCode == 0)
                foreach (var line in cherry.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (line.Length > 2) states[line[2..].Trim()] = line[0] == '-' ? CherryCommitRow.InBase : CherryCommitRow.Own;
        }
        // Every commit of the range (not only the listed ones) for the counts; git cherry skips merges.
        HashSet<string> allNotPushed = list.Count == 0 ? [] : await NotPushedAsync(path, tip, [local, remote], ct);
        string[] rangeShas = list.Count == 0 ? [] : (await git.RunAsync(path, ["rev-list", tip, .. Excluding([local, remote]), "--"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string StateOf(string sha) => states.TryGetValue(sha, out var s) ? s
            : upstream is null ? CherryCommitRow.Own : CherryCommitRow.Merge;
        var ownShas = rangeShas.Where(s => StateOf(s) == CherryCommitRow.Own).ToList();
        var own = ownShas.Count;
        var inBase = rangeShas.Count(s => StateOf(s) == CherryCommitRow.InBase);
        var rows = list.Commits.Select(c => new CherryCommitRow(c.Sha, c.ShortSha, c.Subject, c.Author, c.Date, c.IsMerge, c.NotPushed,
            c.IsMerge && !states.ContainsKey(c.Sha) ? CherryCommitRow.Merge : StateOf(c.Sha))).ToList();

        string lost;
        if (merged || list.Count == 0) lost = $"Nothing is lost: every commit of {branch} is in {baseBranch}.";
        else if (own == 0)
            lost = inBase == list.Count
                ? list.Count == 1
                    ? $"Nothing is lost: its 1 commit not in {baseBranch} already has the same change there."
                    : $"Nothing is lost: each of its {list.Count} commits not in {baseBranch} already has the same change there."
                : $"Nothing of its own is lost: its {Plural(list.Count, "commit")} not in {baseBranch} {(list.Count == 1 ? "is a merge" : "are merges or already have the same change there")}.";
        else
        {
            var shown = string.Join(", ", ownShas.Take(5).Select(Short)) + (own > 5 ? ", ..." : "");
            var unpushed = ownShas.Count(allNotPushed.Contains);
            var where = !hasOrigin ? $"; this repository has no origin, so {(own == 1 ? "it is" : "they are")} nowhere else"
                : unpushed == own ? $"; {(own == 1 ? "it is" : "they are")} not on origin either"
                : unpushed == 0 ? $"; origin still has {(own == 1 ? "it" : "them")}"
                : $"; {unpushed} of them {(unpushed == 1 ? "is" : "are")} not on origin either";
            lost = $"Deleting it loses {Plural(own, "commit")} of its own ({shown}){where}.";
        }
        var text = DeleteText(branch, tip, baseBranch, lost, rows, list.Count, hasOrigin);
        return new DeletePreview(branch, baseBranch, true, tip, merged, rows, own, inBase, list.Truncated, lost, text, hasOrigin, push);
    }

    /// <summary>The delete preview as text: git.deletePreview's <c>text</c>, and what the dialog's and the Concierge's delete lists show under each branch.</summary>
    public static string DeleteText(string branch, string tip, string baseBranch, string lost, IReadOnlyList<CherryCommitRow> rows, int count, bool hasOrigin)
    {
        var sb = new StringBuilder($"{branch} at {Short(tip)}: {lost}");
        if (count > 0) sb.Append($"\n{Plural(count, "commit")} not in {baseBranch}:");
        foreach (var c in rows.Take(MaxTextCommits))
        {
            var state = c.State switch
            {
                CherryCommitRow.InBase => $": already in {baseBranch}",
                CherryCommitRow.Merge => ": a merge",
                _ => ": work of its own",
            };
            sb.Append('\n').Append(CommitLine(c, hasOrigin, state));
        }
        if (count > Math.Min(rows.Count, MaxTextCommits)) sb.Append($"\n  ... and {count - Math.Min(rows.Count, MaxTextCommits)} more");
        return sb.ToString();
    }
}
