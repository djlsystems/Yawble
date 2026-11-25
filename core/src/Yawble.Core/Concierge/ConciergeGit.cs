using System.Text;
using Yawble.Core.Teams;

namespace Yawble.Core.Concierge;

/// <summary>One repository the Concierge's git actions may act on: its folder, base branch and the member clones that could have a branch checked out.</summary>
public sealed record ActionRepo(string Name, string Path, string BaseBranch, IReadOnlyList<string> MemberClones);

/// <summary>What a destructive git action WOULD do: the exact text to show the person, and the key that pins it.</summary>
public sealed record GitPlan(string Text, string Key, bool Empty, Func<CancellationToken, Task<string>> Run);

/// <summary>
/// The Concierge's git actions, run by the HOST on the team's repository (never in a member's sandbox, never inside a
/// member's clone: a clone's HEAD is only READ from its file). Rules, all enforced here and not left to the prompt:
/// <list type="bullet">
/// <item>Nothing is forced. Deleting a ref is compare-and-delete on the SHA that was shown.</item>
/// <item>A branch some clone has checked out, the base branch, and a branch the base does not contain (unmerged) are never
///   deleted, unless the person NAMED the unmerged one (<c>unmergedOk</c>); a checked-out one never. A branch whose every
///   commit already has an equivalent in the base (contained, see <see cref="ContainedProofAsync"/>) counts as merged.</item>
/// <item>Every deleted branch's SHA is recorded (in the result and in <c>.git/yawble-concierge-deleted-branches.txt</c>)
///   so it can be restored with <c>git branch &lt;name&gt; &lt;sha&gt;</c>.</item>
/// </list>
/// </summary>
public sealed class ConciergeGit(HostGit git, PersonGitPush pusher, Func<DateTimeOffset>? now = null)
{
    public const string DeletedLog = "yawble-concierge-deleted-branches.txt";
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    private static bool Protected(ActionRepo repo, string branch) =>
        string.Equals(branch, repo.BaseBranch, StringComparison.OrdinalIgnoreCase)
        || new[] { "main", "master", "HEAD" }.Contains(branch, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ delete branches

    /// <summary>Plans deleting <paramref name="names"/> (or, when none, every merged branch) from the repository; refusals are listed with the reason.
    /// <paramref name="refuse"/>: the caller's own refusal for a branch (why it is not deleted), or null; applied to every
    /// branch considered, named or not (another team's member still on it).</summary>
    /// <param name="deleted">Called after each local branch is deleted, with its name and the SHA it had (the app records its commits).</param>
    public async Task<GitPlan> PlanDeleteAsync(ActionRepo repo, IReadOnlyList<string> names, IReadOnlyList<string> unmergedOk, bool remote, CancellationToken ct,
        Func<string, string?>? refuse = null, Func<string, string, CancellationToken, Task>? deleted = null)
    {
        var previews = new GitPreviews(git);
        var path = repo.Path;
        var baseSha = await RevAsync(path, "refs/heads/" + repo.BaseBranch, ct)
                      ?? throw new ArgumentException($"{repo.Name}: there is no base branch {repo.BaseBranch}");
        var originBase = await RevAsync(path, "refs/remotes/origin/" + repo.BaseBranch, ct);
        var checkedOut = await CheckedOutAsync(repo, ct);
        var sb = new StringBuilder();
        List<string> wanted;
        if (names.Count > 0)
        {
            // Plan, guard, key and delete on the exact ref names git will act on.
            var (exact, ambiguous) = await ResolveBranchNamesAsync(path, names, remote, ct);
            foreach (var line in ambiguous) sb.AppendLine(line);
            wanted = exact;
        }
        else wanted = await LocalBranchesAsync(path, ct);
        bool UnmergedOk(string branch) => unmergedOk.Contains(branch, StringComparer.OrdinalIgnoreCase);
        var key = new StringBuilder();
        var deletable = new List<(string Branch, string Sha)>();
        var remoteDeletable = new List<(string Branch, string Sha)>();
        foreach (var branch in wanted.Distinct())
        {
            var sha = await RevAsync(path, "refs/heads/" + branch, ct);
            var rsha = remote ? await RevAsync(path, "refs/remotes/origin/" + branch, ct) : null;
            if (sha is null && rsha is null)
            {
                if (names.Count > 0) sb.AppendLine($"- {branch}: REFUSED, no such branch");
                continue;
            }
            if (Protected(repo, branch))
            {
                if (names.Count > 0) sb.AppendLine($"- {branch}: REFUSED, it is the base/protected branch");
                continue;
            }
            if (refuse?.Invoke(branch) is { } why)
            {
                sb.AppendLine($"- {branch}: REFUSED, {why}");
                continue;
            }
            // A checked-out branch is refused on origin's side too (a clone on it, only origin has it yet).
            if (checkedOut.TryGetValue(branch, out var where))
            {
                sb.AppendLine($"- {branch} ({Short(sha ?? rsha!)}): REFUSED, checked out in {where}");
                continue;
            }
            // A branch whose every commit already has an equivalent in the base (cherry-picked there) counts as merged:
            // nothing is lost by deleting it, and the plan line carries the proof.
            async Task<(bool Merged, string? Proof)> State(string tip) =>
                await IsAncestorAsync(path, tip, baseSha, ct) || (originBase is not null && await IsAncestorAsync(path, tip, originBase, ct))
                    ? (true, null)
                    : (false, await ContainedProofAsync(path, repo.BaseBranch, baseSha, tip, ct)
                              ?? (originBase is not null && originBase != baseSha ? await ContainedProofAsync(path, "origin/" + repo.BaseBranch, originBase, tip, ct) : null));
            static string How((bool Merged, string? Proof) s) =>
                s.Merged ? " (merged)" : s.Proof is { } proof ? $" (contained in base: {proof})" : " (UNMERGED, you named it)";
            if (sha is not null)
            {
                var state = await State(sha);
                if (!state.Merged && state.Proof is null && !UnmergedOk(branch))
                {
                    sb.AppendLine($"- {branch} ({Short(sha)}): KEPT, not merged into {repo.BaseBranch} (name it in unmerged_ok to delete it anyway)");
                    continue;
                }
                sb.AppendLine($"- delete {branch} at {sha}{How(state)}");
                // What deleting it loses, the same text as the Git dialog's git.deletePreview.
                foreach (var line in (await previews.DeletePreviewAsync(path, repo.BaseBranch, branch, ct)).Text.Split('\n'))
                    sb.Append("    ").AppendLine(line);
                key.Append("L ").Append(branch).Append(' ').AppendLine(sha);
                deletable.Add((branch, sha));
            }
            if (rsha is not null)
            {
                var state = await State(rsha);
                if (!state.Merged && state.Proof is null && !UnmergedOk(branch))
                {
                    sb.AppendLine($"- origin/{branch} ({Short(rsha)}): KEPT, not merged into {repo.BaseBranch}");
                    continue;
                }
                sb.AppendLine($"- delete origin/{branch} at {rsha}{How(state)}");
                key.Append("R ").Append(branch).Append(' ').AppendLine(rsha);
                remoteDeletable.Add((branch, rsha));
            }
        }
        var empty = deletable.Count == 0 && remoteDeletable.Count == 0;
        var text = $"{repo.Name} ({path}), base {repo.BaseBranch}:\n{(sb.Length == 0 ? "(nothing matched)\n" : sb.ToString())}";
        return new GitPlan(text.TrimEnd(), key.ToString(), empty, async token =>
        {
            var done = new StringBuilder();
            var log = new StringBuilder();
            foreach (var (branch, sha) in deletable)
            {
                // Re-check at the moment of deleting: checked out since the list was shown, or moved on, means kept.
                if ((await CheckedOutAsync(repo, token)).ContainsKey(branch)) { done.AppendLine($"- {branch}: kept (checked out meanwhile)"); continue; }
                var r = await git.TryRunAsync(path, ["update-ref", "-d", "refs/heads/" + branch, sha], token);
                if (r.ExitCode != 0) { done.AppendLine($"- {branch}: kept (it moved since the list was shown: {r.Stderr})"); continue; }
                done.AppendLine($"- deleted {branch}; was {sha}; restore with: git branch {branch} {sha}");
                log.AppendLine($"{_now():O} deleted {branch} {sha}");
                if (deleted is not null) await deleted(branch, sha, token);
            }
            foreach (var (branch, sha) in remoteDeletable)
            {
                var r = await pusher.RunAsync(path, ["push", "--no-verify", $"--force-with-lease=refs/heads/{branch}:{sha}", "origin", "--delete", branch], token);
                if (r.ExitCode != 0) { done.AppendLine($"- origin/{branch}: kept (it moved on origin since the list was shown, or the push failed: {PersonGitPush.Redact(r.Stderr)})"); continue; }
                done.AppendLine($"- deleted origin/{branch}; was {sha}; restore with: git push origin {sha}:refs/heads/{branch}");
                log.AppendLine($"{_now():O} deleted origin/{branch} {sha}");
            }
            if (log.Length > 0) await AppendLogAsync(path, log.ToString(), token);
            return done.ToString().TrimEnd();
        });
    }

    // ------------------------------------------------------------------ contained in base

    /// <summary>At most this many base commits are fingerprinted when git cherry cannot tell (a branch that merged the base after a cherry-pick).</summary>
    public const int MaxFingerprintedBaseCommits = 5000;

    /// <summary>
    /// Whether every commit on <paramref name="tip"/> that <paramref name="baseSha"/> lacks already has an equivalent in the base
    /// (the same change, cherry-picked there), and if so the proof as text; null when not (or the branch is merged, or
    /// git could not tell). Merge commits are skipped, as git cherry does. First <c>git cherry -v base tip</c>: every line
    /// "-" is the proof. When the branch merged the base after its commits were picked, git cherry has nothing to compare
    /// with (the base's copies are on the branch too), so each own commit's change is compared with the base's commits since
    /// the branch forked, by a patch fingerprint (git patch-id's idea: the diff without line numbers and whitespace).
    /// Read-only, in the host's own repository.
    /// </summary>
    public async Task<string?> ContainedProofAsync(string path, string baseName, string baseSha, string tip, CancellationToken ct)
    {
        var own = await git.TryRunAsync(path, ["rev-list", "--no-merges", "--reverse", tip, "^" + baseSha], ct);
        if (own.ExitCode != 0) return null;
        var commits = own.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (commits.Length == 0)
        {
            // Only merge commits ahead (the base merged in): contained when merging the branch into the base changes nothing.
            if (await IsAncestorAsync(path, tip, baseSha, ct)) return null;
            var merge = await git.TryRunAsync(path, ["merge-tree", "--write-tree", "--no-messages", baseSha, tip], ct);
            var baseTree = await git.TryRunAsync(path, ["rev-parse", baseSha + "^{tree}"], ct);
            return merge.ExitCode == 0 && baseTree.ExitCode == 0 && merge.Stdout.Split('\n')[0].Trim() == baseTree.Stdout.Trim()
                ? $"its only commits ahead of {baseName} are merges, and merging it into {baseName} changes nothing"
                : null;
        }
        var cherry = await git.TryRunAsync(path, ["cherry", "-v", baseSha, tip], ct);
        var lines = cherry.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (cherry.ExitCode == 0 && lines.Length > 0 && lines.All(l => l.StartsWith("- ", StringComparison.Ordinal)))
            return $"git cherry {baseName} {Short(tip)}: every commit has an equivalent in {baseName}: " + Listed(lines.Select(l => l[2..]).Select(ShortLine));

        // The base's commits since the branch forked from it (before its first own commit), fingerprinted.
        var parent = await git.TryRunAsync(path, ["rev-parse", "--verify", "-q", commits[0] + "^"], ct);
        var fork = parent.ExitCode == 0 ? await git.TryRunAsync(path, ["merge-base", baseSha, parent.Stdout.Trim()], ct) : null;
        var range = fork is { ExitCode: 0, Stdout.Length: > 0 } ? new[] { baseSha, "^" + fork.Stdout.Trim() } : [baseSha];
        var upstream = await FingerprintsAsync(path, [.. range, "--max-count=" + MaxFingerprintedBaseCommits], ct);
        var mine = await FingerprintsAsync(path, [tip, "^" + baseSha], ct);
        if (upstream is null || mine is null) return null;
        var byPrint = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sha, print, _) in upstream) byPrint.TryAdd(print, sha);
        var pairs = new List<string>();
        foreach (var (sha, print, subject) in mine)
        {
            if (!byPrint.TryGetValue(print, out var same)) return null;
            pairs.Add($"{Short(sha)} {subject} = {baseName} {Short(same)}");
        }
        return pairs.Count == 0 ? null
            : $"every commit of {Short(tip)} not in {baseName} has the same change in {baseName} (git cherry cannot see it: the branch merged {baseName} since): " + Listed(pairs);

        static string ShortLine(string l) => l.IndexOf(' ') is var sp && sp > 12 ? Short(l[..sp]) + l[sp..] : l;
        static string Listed(IEnumerable<string> items)
        {
            var all = items.ToList();
            return string.Join("; ", all.Take(20)) + (all.Count > 20 ? $"; and {all.Count - 20} more" : "");
        }
    }

    /// <summary>Each non-merge commit of the range: its SHA, its change's fingerprint and its subject. Null when git failed.</summary>
    private async Task<List<(string Sha, string Print, string Subject)>?> FingerprintsAsync(string path, IReadOnlyList<string> range, CancellationToken ct)
    {
        var r = await git.TryRunAsync(path, ["-c", "core.quotePath=false", "log", "--no-merges", "--no-color", "--no-ext-diff", "--no-textconv", "-p",
            "--format=%x00%H %s", .. range, "--"], ct);
        if (r.ExitCode != 0) return null;
        var list = new List<(string, string, string)>();
        foreach (var chunk in r.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var nl = chunk.IndexOf('\n');
            var head = nl < 0 ? chunk : chunk[..nl];
            var sp = head.IndexOf(' ');
            if (sp < 40) continue;
            var print = new StringBuilder();
            foreach (var line in (nl < 0 ? "" : chunk[(nl + 1)..]).Split('\n'))
            {
                if (line.StartsWith("index ", StringComparison.Ordinal)) continue;
                var kept = line.StartsWith("@@", StringComparison.Ordinal) ? "@@" : line;
                foreach (var c in kept) if (!char.IsWhiteSpace(c)) print.Append(c);
                print.Append('\n');
            }
            list.Add((head[..sp], Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(print.ToString().Trim('\n')))), head[(sp + 1)..]));
        }
        return list;
    }

    // ------------------------------------------------------------------ prune

    public async Task<GitPlan> PlanPruneAsync(ActionRepo repo, CancellationToken ct)
    {
        var path = repo.Path;
        if (await git.TryRunAsync(path, ["config", "--get", "remote.origin.url"], ct) is not { ExitCode: 0 })
            throw new ArgumentException($"{repo.Name} has no origin: nothing to prune");
        var dry = await pusher.RunAsync(path, ["remote", "prune", "origin", "--dry-run"], ct);
        if (dry.ExitCode != 0) throw new ArgumentException("could not check origin: " + PersonGitPush.Redact(dry.Stderr));
        var refs = dry.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains("[would prune]", StringComparison.Ordinal)).Select(l => l.Split("] ", 2)[1].Trim()).ToList();
        var shown = new StringBuilder();
        foreach (var r in refs)
            shown.AppendLine($"- forget remote-tracking {r} at {await RevAsync(path, "refs/remotes/" + r, ct) ?? "?"}");
        var text = $"{repo.Name} ({path}): " + (refs.Count == 0 ? "no stale remote-tracking branches." : $"{refs.Count} stale remote-tracking branch(es) origin no longer has (local branches are never touched):\n{shown}");
        return new GitPlan(text.TrimEnd(), string.Join('\n', refs), refs.Count == 0, async token =>
        {
            var before = new List<string>();
            foreach (var r in refs) before.Add($"{r} {await RevAsync(path, "refs/remotes/" + r, token)}");
            var run = await pusher.RunAsync(path, ["remote", "prune", "origin"], token);
            if (run.ExitCode != 0) return "prune failed; nothing more changed: " + PersonGitPush.Redact(run.Stderr);
            await AppendLogAsync(path, string.Concat(before.Select(b => $"{_now():O} pruned {b}\n")), token);
            return "pruned (SHAs, to restore with git update-ref):\n" + string.Join('\n', before.Select(b => "- " + b));
        });
    }

    // ------------------------------------------------------------------ push / sync

    /// <summary>Pushes one branch to origin (never forced). The result names the SHA that went.</summary>
    public async Task<string> PushBranchAsync(ActionRepo repo, string branch, CancellationToken ct)
    {
        var path = repo.Path;
        var sha = await RevAsync(path, "refs/heads/" + branch, ct) ?? throw new ArgumentException($"{repo.Name} has no branch {branch}");
        if (await git.TryRunAsync(path, ["config", "--get", "remote.origin.url"], ct) is not { ExitCode: 0 })
            throw new ArgumentException($"{repo.Name} has no origin to push to");
        var push = await pusher.PushAsync(path, branch, ct);
        var said = PersonGitPush.Redact(string.Join('\n', push.Stderr, push.Stdout).Trim());
        if (push.ExitCode == 0) return $"pushed {branch} at {sha} to origin (not forced).";
        throw new ArgumentException(PersonGitPush.IsRefused(said)
            ? $"origin refused {branch} (it is not a fast-forward; the app never forces). Nothing changed. git said: {said}"
            : $"could not push {branch}; nothing changed. git said: {said}");
    }

    /// <summary>Brings a branch current with the base: a merge commit made without touching any working tree; a conflict changes nothing.</summary>
    public async Task<string> SyncBranchAsync(ActionRepo repo, string branch, CancellationToken ct)
    {
        var path = repo.Path;
        if (Protected(repo, branch)) throw new ArgumentException($"{branch} is the base branch: nothing to sync it with");
        var sha = await RevAsync(path, "refs/heads/" + branch, ct) ?? throw new ArgumentException($"{repo.Name} has no branch {branch}");
        var baseSha = await RevAsync(path, "refs/heads/" + repo.BaseBranch, ct) ?? throw new ArgumentException($"{repo.Name} has no base branch {repo.BaseBranch}");
        if ((await CheckedOutAsync(repo, ct)).TryGetValue(branch, out var where))
            throw new ArgumentException($"{branch} is checked out in {where}: its files would not follow, so it is left alone");
        if (await IsAncestorAsync(path, baseSha, sha, ct)) return $"{branch} already has everything on {repo.BaseBranch} ({Short(sha)}); nothing changed.";
        var merge = await git.TryRunAsync(path, ["merge-tree", "--write-tree", "--name-only", "--no-messages", sha, baseSha], ct);
        if (merge.ExitCode == 1)
            throw new ArgumentException($"{branch} does not merge cleanly with {repo.BaseBranch}; nothing changed. Conflicts: {string.Join(", ", merge.Stdout.Split('\n').Skip(1).Where(l => l.Length > 0).Distinct())}");
        if (merge.ExitCode != 0) throw new ArgumentException(GitProgram.LooksTooOld(merge.Stderr) ? GitProgram.TooOldMessage("git merge-tree") : "git merge-tree failed: " + merge.Stderr);
        var tree = merge.Stdout.Split('\n')[0].Trim();
        var merged = await git.RunAsync(path, ct, "-c", "user.name=Yawble", "-c", "user.email=yawble@localhost",
            "commit-tree", tree, "-p", sha, "-p", baseSha, "-m", $"Merge {repo.BaseBranch} into {branch}");
        var move = await git.TryRunAsync(path, ["update-ref", "refs/heads/" + branch, merged, sha], ct);
        if (move.ExitCode != 0) throw new ArgumentException($"{branch} moved while the app was working; nothing changed: {move.Stderr}");
        await AppendLogAsync(path, $"{_now():O} synced {branch} {sha} -> {merged}\n", ct);
        return $"{branch} now has {repo.BaseBranch}: {sha} -> {merged} (merge commit; the old tip {sha} is recorded so it can be restored).";
    }

    // ------------------------------------------------------------------ what is checked out where

    /// <summary>Branch -> where it is checked out: the repository itself, its worktrees, and every member clone (HEAD file read, never git run there).</summary>
    public async Task<Dictionary<string, string>> CheckedOutAsync(ActionRepo repo, CancellationToken ct)
    {
        // Ignoring case: git on Windows and macOS treats Members/Ada and members/ada as one loose ref.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if ((await git.TryRunAsync(repo.Path, ["symbolic-ref", "-q", "--short", "HEAD"], ct)) is { ExitCode: 0, Stdout.Length: > 0 } head)
            map[head.Stdout] = repo.Path;
        var wt = await git.TryRunAsync(repo.Path, ["worktree", "list", "--porcelain"], ct);
        string? dir = null;
        foreach (var line in wt.Stdout.Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) dir = line[9..];
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal)) map.TryAdd(line[18..], dir ?? "a worktree");
        }
        foreach (var clone in repo.MemberClones)
        {
            if (!Directory.Exists(clone)) continue;
            // Read as a file, never by git in the clone; anything odd (a link, too long, not a plain branch or commit) refuses.
            if (CloneHead.Read(System.IO.Path.Combine(clone, ".git")) is not { } cloneHead)
                throw new ArgumentException($"cannot tell what is checked out in {clone} (its .git/HEAD is not a plain branch or commit); nothing was changed");
            if (cloneHead.Branch is { } branch) map.TryAdd(branch, clone);
        }
        return map;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<List<string>> LocalBranchesAsync(string path, CancellationToken ct) =>
        (await git.RunAsync(path, ct, "for-each-ref", "--format=%(refname:short)", "refs/heads/")).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    internal async Task AppendLogAsync(string path, string text, CancellationToken ct)
    {
        var gitDir = await git.TryRunAsync(path, ["rev-parse", "--absolute-git-dir"], ct);
        if (gitDir.ExitCode == 0) await File.AppendAllTextAsync(System.IO.Path.Combine(gitDir.Stdout, DeletedLog), text, ct);
    }

    /// <summary>The repository's origin address as configured (may carry a credential: redact before showing), or null.</summary>
    public async Task<string?> OriginUrlAsync(string path, CancellationToken ct)
    {
        var r = await git.TryRunAsync(path, ["config", "--get", "remote.origin.url"], ct);
        return r.ExitCode == 0 && r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : null;
    }

    /// <summary>
    /// Git on Windows and macOS finds a loose ref whatever its case (asked for Members/Ada it reads and
    /// deletes members/ada), so every guard must see the name git will act on. Each requested name is matched, ignoring
    /// case, against the branches there are (refs/heads, and refs/remotes/origin when <paramref name="remote"/>): one exact
    /// name found is used instead; none is kept as asked (git finds nothing either); two or more that differ only in case
    /// are refused as ambiguous (in <c>Refused</c>, a plan line each).
    /// </summary>
    public async Task<(List<string> Exact, List<string> Refused)> ResolveBranchNamesAsync(string path, IEnumerable<string> names, bool remote, CancellationToken ct)
    {
        var known = new List<string>();
        foreach (var prefix in remote ? new[] { "refs/heads/", "refs/remotes/origin/" } : ["refs/heads/"])
        {
            var r = await git.TryRunAsync(path, ["for-each-ref", "--format=%(refname)", prefix], ct);
            if (r.ExitCode != 0) continue;
            foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (line.Trim() is var full && full.StartsWith(prefix, StringComparison.Ordinal)) known.Add(full[prefix.Length..]);
        }
        var exact = new List<string>();
        var refused = new List<string>();
        foreach (var name in names)
        {
            var matches = known.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToList();
            if (matches.Count > 1)
                refused.Add($"- {name}: REFUSED, more than one branch has this name in another case ({string.Join(", ", matches)}); nothing is deleted by an unclear name");
            else
            {
                var e = matches.Count == 1 ? matches[0] : name;
                if (!exact.Contains(e, StringComparer.Ordinal)) exact.Add(e);
            }
        }
        return (exact, refused);
    }

    private async Task<string?> RevAsync(string path, string reference, CancellationToken ct)
    {
        var r = await git.TryRunAsync(path, ["rev-parse", "--verify", "-q", reference + "^{commit}"], ct);
        return r.ExitCode == 0 && r.Stdout.Length > 0 ? r.Stdout : null;
    }

    private async Task<bool> IsAncestorAsync(string path, string ancestor, string of, CancellationToken ct) =>
        (await git.TryRunAsync(path, ["merge-base", "--is-ancestor", ancestor, of], ct)).ExitCode == 0;

    private static string Short(string sha) => sha[..Math.Min(12, sha.Length)];
}

/// <summary>
/// What a member clone has checked out, read from its .git/HEAD FILE (the clone is the agent's: git is never run in it).
/// The file is the agent's to write, so it is read defensively: not through a link or reparse point, at most
/// <see cref="MaxBytes"/> bytes, the first line only, and accepted only as <c>ref: refs/heads/&lt;valid branch name&gt;</c>
/// (git check-ref-format's rules) or a 40- or 64-hex commit id. Anything else is unreadable (null).
/// </summary>
public sealed record CloneHead(string? Branch, string? Detached)
{
    public const int MaxBytes = 256;

    public static CloneHead? Read(string gitDir)
    {
        try
        {
            var info = new FileInfo(System.IO.Path.Combine(gitDir, "HEAD"));
            if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaxBytes) return null;
            var buffer = new byte[MaxBytes + 1];
            int n;
            using (var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (n > MaxBytes) return null;
            var line = Encoding.UTF8.GetString(buffer, 0, n).Split('\n')[0].TrimEnd('\r');
            const string prefix = "ref: refs/heads/";
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return IsBranchName(line[prefix.Length..]) ? new CloneHead(line[prefix.Length..], null) : null;
            return line.Length is 40 or 64 && line.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? new CloneHead(null, line) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether refs/heads/<paramref name="name"/> is a valid ref name (git check-ref-format, without running git).</summary>
    public static bool IsBranchName(string name)
    {
        if (name.Length == 0 || name == "@" || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.')) return false;
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal)) return false;
        if (name.Any(c => c < 0x20 || c == 0x7f || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')) return false;
        return name.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));
    }
}
