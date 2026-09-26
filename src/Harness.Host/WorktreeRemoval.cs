using System.Text.Json;
using Harness.Contracts;
using Harness.Kanban;

namespace Harness.Host;

/// <summary>A tree the platform would have removed and did not, and why.</summary>
public sealed record WorktreeLeft(string Path, string Reason);

/// <summary>What one removal pass did: the trees it removed and the ones it left.</summary>
public sealed record WorktreeRemovalReport(IReadOnlyList<string> Removed, IReadOnlyList<WorktreeLeft> Left)
{
    public static WorktreeRemovalReport Empty { get; } = new([], []);

    public WorktreeRemovalReport Add(WorktreeRemovalReport other) =>
        new([.. Removed, .. other.Removed], [.. Left, .. other.Left]);
}

/// <summary>
/// REMOVES A SETTLED CARD'S WORKTREES, AND NEVER FORCES ONE. The one place a card's tree is
/// removed, shared by its three callers: the workflow-completed handler, <see cref="MemberDeletion"/>
/// and the Git dialog's "clean up worktrees" button.
///
/// <para>
/// <b>A TREE IS REMOVED ONLY WHEN NOTHING IN IT CAN BE LOST.</b> Its HEAD's commits must all be on a
/// remote ("its branch is pushed"), and then <c>git worktree remove</c> runs WITHOUT
/// <c>--force</c>, so git itself refuses a tree with uncommitted edits. Either refusal leaves the
/// tree where it is and says so; nothing here deletes a directory recursively. A tree left behind
/// costs disk; a tree forced away costs somebody's work, and only a person can tell which it was.
/// </para>
///
/// <para>
/// <b>WHICH TREES ARE SETTLED</b> is <see cref="OpenKeysAsync"/>: a card key is open while its card
/// is (see <see cref="WorkflowLooseEnds.IsSettled"/>), and a <c>w&lt;correlation&gt;</c> key while its
/// workflow is.
/// </para>
/// </summary>
public sealed class WorktreeRemoval(GitRunner git, IMessageLog log)
{
    /// <summary>
    /// Removes each of <paramref name="trees"/> from the clone at <paramref name="clonePath"/>, never
    /// forcing, then prunes the clone's worktree records. A path that is not on disk is skipped.
    /// </summary>
    public async Task<WorktreeRemovalReport> RemoveAsync(
        string clonePath, IEnumerable<string> trees, CancellationToken ct)
    {
        var removed = new List<string>();
        var left = new List<WorktreeLeft>();

        foreach (var tree in trees)
        {
            if (!Directory.Exists(tree)) continue;

            // Asked in the tree itself, of its own HEAD, so a detached checkout is measured too.
            var unpushed = await git.CountCommitsNotOnAnyRemoteAsync(tree, "HEAD", ct);

            if (unpushed is not 0)
            {
                left.Add(new WorktreeLeft(tree, unpushed is null
                    ? "Could not tell whether its commits are on origin, so it was not removed."
                    : $"It holds {unpushed} commit(s) that are not on origin, so it was not removed."));
                continue;
            }

            var result = await git.RemoveWorktreeAsync(clonePath, tree, ct);

            if (result.ExitCode == 0)
            {
                removed.Add(tree);
            }
            else
            {
                var said = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
                left.Add(new WorktreeLeft(tree, $"git worktree remove refused it: {GitOutputRedaction.Redact(said.Trim())}"));
            }
        }

        if (Directory.Exists(clonePath))
        {
            var prune = await git.PruneWorktreesAsync(clonePath, ct);
            if (prune.ExitCode != 0)
            {
                left.Add(new WorktreeLeft(clonePath, $"git worktree prune failed: {GitOutputRedaction.Redact(prune.Stderr.Trim())}"));
            }
        }

        return new WorktreeRemovalReport(removed, left);
    }

    /// <summary>
    /// Every per-card tree in the team's repositories whose key is in <paramref name="keys"/>, for
    /// every member, removed under <see cref="RemoveAsync"/>'s rule. Each tree left is named on the
    /// team feed.
    /// </summary>
    public async Task<WorktreeRemovalReport> RemoveKeysAsync(
        TeamPaths paths, string team, IEnumerable<string> repoUrls, IReadOnlySet<string> keys,
        long? causation, CancellationToken ct)
    {
        var report = WorktreeRemovalReport.Empty;

        if (keys.Count == 0) return report;

        foreach (var repo in repoUrls.Select(RepoUrls.DeriveName))
        {
            var trees = paths.CardWorktreesIn(team, repo)
                .Where(tree => keys.Contains(tree.Key))
                .ToList();

            if (trees.Count == 0) continue;

            var clone = Path.Combine(paths.ReposFor(team), repo, "main");
            var pass = await RemoveAsync(clone, trees.Select(tree => tree.Path), ct);

            foreach (var tree in pass.Left)
            {
                var owner = trees.FirstOrDefault(t => t.Path == tree.Path)?.Member;
                if (owner is not null)
                    await ReportLeftAsync(new ContainerId(team, owner), repo, tree, causation, ct);
            }

            report = report.Add(pass);
        }

        return report;
    }

    /// <summary>
    /// The Git dialog's "clean up worktrees": every tree git has registered for this clone
    /// (<paramref name="registered"/>) plus any per-card tree on disk it has lost track of, minus
    /// the ones whose card or workflow is still open, removed under <see cref="RemoveAsync"/>'s
    /// rule. Returns what it did and the open trees it kept; each tree left is named on the feed.
    /// A tree that is not a card's (a per-member <c>wt_&lt;Member&gt;</c>) has no card holding it open.
    /// </summary>
    public async Task<(WorktreeRemovalReport Report, IReadOnlyList<string> Kept)> CleanUpSettledAsync(
        TeamPaths paths, KanbanStore kanban, string team, string repo, string clonePath,
        IEnumerable<string> registered, CancellationToken ct)
    {
        static string Normal(string path) =>
            Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var candidates = registered.Select(Normal)
            .Concat(paths.CardWorktreesIn(team, repo).Select(tree => Normal(tree.Path)))
            .Where(path => !string.Equals(path, Normal(clonePath), StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var keys = candidates
            .Select(path => Worktrees.KeyOf(Path.GetFileName(path)))
            .OfType<string>()
            .ToList();
        var open = await OpenKeysAsync(kanban, log, team, keys, ct);

        var kept = candidates
            .Where(path => Worktrees.KeyOf(Path.GetFileName(path)) is { } key && open.Contains(key))
            .ToList();

        var report = await RemoveAsync(clonePath, candidates.Except(kept), ct);

        foreach (var tree in report.Left)
        {
            if (OwnerOf(tree.Path) is { } owner)
                await ReportLeftAsync(new ContainerId(team, owner), repo, tree, causation: null, ct);
        }

        return (report, kept);
    }

    /// <summary>The member a tree under a repository belongs to: a per-card tree's, or a per-member
    /// <c>wt_&lt;Member&gt;</c>'s. Null for anything else.</summary>
    private static string? OwnerOf(string tree)
    {
        var leaf = Path.GetFileName(tree);

        if (Worktrees.MemberOf(leaf) is { } member) return member;

        return leaf.StartsWith(Worktrees.Prefix, StringComparison.Ordinal)
            && ContainerId.IsLegalName(leaf[Worktrees.Prefix.Length..])
                ? leaf[Worktrees.Prefix.Length..]
                : null;
    }

    /// <summary>Names a tree that was left on the team feed. Source is the member it belongs to.</summary>
    public Task ReportLeftAsync(
        ContainerId owner, string repo, WorktreeLeft tree, long? causation, CancellationToken ct) =>
        log.AppendAsync(
            new NewMessage(
                MessageTypes.RepoWorktreeLeft,
                JsonSerializer.Serialize(new { repo, worktree = tree.Path, reason = tree.Reason }),
                owner.ToString(),
                causation),
            ct);

    /// <summary>
    /// The keys a declared workflow settles: every card of it that is settled - including one it
    /// resumed from an earlier workflow (<see cref="KanbanCard.BelongsTo"/>) - plus the workflow's
    /// own <c>w&lt;correlation&gt;</c> key. Read BEFORE the declaration is appended, because that row
    /// moves every card to Done - including a loose end the declaration <c>dropped</c>, whose tree
    /// must stay. The declaring member's own cards count as settled, as
    /// <see cref="WorkflowLooseEnds"/> counts them: it is running because it is the one declaring.
    /// </summary>
    public static async Task<IReadOnlySet<string>> SettledKeysAsync(
        KanbanStore kanban, string team, long correlation, string declaringMember)
    {
        var board = await kanban.GetBoardAsync(new KanbanFilter(Team: team));

        var keys = board.Cards
            .Where(card => card.BelongsTo(correlation))
            .Where(card => WorkflowLooseEnds.IsSettled(card)
                || string.Equals(card.Member, declaringMember, StringComparison.OrdinalIgnoreCase))
            .Select(card => Worktrees.Sanitise(card.Id))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        keys.Add(Worktrees.KeyFor(null, correlation));
        return keys;
    }

    /// <summary>
    /// Which of <paramref name="keys"/> belong to work that is still open: a card that is not
    /// settled, or a <c>w&lt;correlation&gt;</c> whose workflow has not ended. A card key that names no
    /// card on the team's board has nothing holding it open.
    /// </summary>
    public static async Task<IReadOnlySet<string>> OpenKeysAsync(
        KanbanStore kanban, IMessageLog log, string team, IReadOnlyCollection<string> keys,
        CancellationToken ct)
    {
        var open = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Count == 0) return open;

        var board = await kanban.GetBoardAsync(new KanbanFilter(Team: team));

        var openCards = board.Cards
            .Where(card => !WorkflowLooseEnds.IsSettled(card))
            .Select(card => Worktrees.Sanitise(card.Id))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var correlations = keys
            .Select(key => (Key: key, Correlation: Worktrees.CorrelationOf(key)))
            .Where(k => k.Correlation is not null)
            .ToList();

        var openWorkflows = await log.OpenWorkflowsAmongAsync(
            correlations.Select(k => k.Correlation!.Value).Distinct().ToList(), ct);

        foreach (var key in keys)
        {
            if (Worktrees.CorrelationOf(key) is { } correlation
                ? openWorkflows.Contains(correlation)
                : openCards.Contains(key))
            {
                open.Add(key);
            }
        }

        return open;
    }

    /// <summary>
    /// The bytes a tree takes on disk, not following links. Unreadable entries are skipped, so this
    /// is a floor rather than an exact figure; null when the tree is not there.
    /// </summary>
    public static long? SizeOnDisk(string tree)
    {
        if (!Directory.Exists(tree)) return null;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        long total = 0;
        foreach (var file in new DirectoryInfo(tree).EnumerateFiles("*", options))
        {
            try { total += file.Length; }
            catch (IOException) { }
        }

        return total;
    }
}
