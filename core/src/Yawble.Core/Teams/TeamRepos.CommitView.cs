using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Monitor;

namespace Yawble.Core.Teams;

/// <summary>An author or committer: name, email and the date as git gives it (strict ISO 8601, with its offset).</summary>
public sealed record CommitPerson(string Name, string Email, string Date)
{
    public JsonObject ToJson() => new() { ["name"] = Name, ["email"] = Email, ["date"] = Date };
}

/// <summary>What a merge brought in: the branch it merged into and the branch merged (null when git and the app's records cannot tell), and how many commits it brought.</summary>
public sealed record CommitMerge(string? IntoBranch, string? FromBranch, string? FromMember, int CommitCount);

/// <summary>Who made the commit, from the app's own records (the Monitor's code events, the board): the member, and its card and workflow when known.</summary>
public sealed record CommitMade(string Member, long? Card, long? Workflow);

/// <summary>
/// Where the commit is: in the base branch (local or origin's); the team branch and member branches that have it;
/// <see cref="OnlyOnMember"/> when it is in neither the base nor the team branch, only a member's branch; whether any
/// origin ref has it (as of the last fetch); the tags that contain it. <see cref="OtherBranches"/>: the other local
/// branches that have it (not the base, the team branch or any members/ branch), sorted, so a commit on the person's
/// own branch is told apart from one whose branch was deleted.
/// </summary>
public sealed record CommitWhere(bool InMain, IReadOnlyList<string> Branches, string? OnlyOnMember, bool Pushed, IReadOnlyList<string> Tags,
    IReadOnlyList<string>? OtherBranches = null);

/// <summary>One file the commit changed (a merge: against its first parent). Status: added, deleted, modified or renamed.</summary>
public sealed record CommitFileChange(string Path, string? OldPath, string Status, int Added, int Deleted, bool Binary)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["path"] = Path };
        if (OldPath is not null) o["oldPath"] = OldPath;
        o["status"] = Status;
        o["added"] = Added;
        o["deleted"] = Deleted;
        o["binary"] = Binary;
        return o;
    }
}

/// <summary>
/// git.commitShow (B072): a commit of the team's repository, read with plain git. <see cref="Found"/> false: git does not
/// have it (<see cref="GoneReason"/> says why, in plain words), and only <see cref="Sha"/> is set.
/// </summary>
public sealed record CommitInfo(
    bool Found, string? GoneReason, string Sha, string? ShortSha = null, string? Title = null, string? Body = null,
    CommitPerson? Author = null, CommitPerson? Committer = null, IReadOnlyList<string>? Parents = null, CommitMerge? Merge = null,
    CommitMade? Made = null, CommitWhere? Where = null, IReadOnlyList<CommitFileChange>? Files = null)
{
    public bool IsMerge => Parents is { Count: > 1 };

    /// <summary>The window's contract (git.commitShow); absent optional fields are left out. <paramref name="codeShown"/>: false for a cloud viewer when members' code is not shown.</summary>
    public JsonObject ToJson(bool codeShown = true)
    {
        if (!Found) return new JsonObject { ["found"] = false, ["goneReason"] = GoneReason, ["sha"] = Sha };
        var o = new JsonObject
        {
            ["found"] = true,
            ["sha"] = Sha,
            ["shortSha"] = ShortSha,
            ["title"] = Title,
            ["body"] = Body,
            ["author"] = Author!.ToJson(),
        };
        if (Committer is not null) o["committer"] = Committer.ToJson();
        o["isMerge"] = IsMerge;
        o["parents"] = new JsonArray([.. (Parents ?? []).Select(p => (JsonNode?)p)]);
        if (Merge is not null)
        {
            var m = new JsonObject { ["intoBranch"] = Merge.IntoBranch, ["fromBranch"] = Merge.FromBranch };
            if (Merge.FromMember is not null) m["fromMember"] = Merge.FromMember;
            m["commitCount"] = Merge.CommitCount;
            o["merge"] = m;
        }
        if (Made is not null)
        {
            var m = new JsonObject { ["member"] = Made.Member };
            if (Made.Card is not null) m["card"] = Made.Card;
            if (Made.Workflow is not null) m["workflow"] = Made.Workflow;
            o["made"] = m;
        }
        var w = Where!;
        var where = new JsonObject { ["inMain"] = w.InMain, ["branches"] = new JsonArray([.. w.Branches.Select(b => (JsonNode?)b)]) };
        where["otherBranches"] = new JsonArray([.. (w.OtherBranches ?? []).Select(b => (JsonNode?)b)]);
        if (w.OnlyOnMember is not null) where["onlyOnMember"] = w.OnlyOnMember;
        where["pushed"] = w.Pushed;
        where["tags"] = new JsonArray([.. w.Tags.Select(t => (JsonNode?)t)]);
        o["where"] = where;
        o["files"] = new JsonArray([.. (Files ?? []).Select(f => (JsonNode)f.ToJson())]);
        o["codeShown"] = codeShown;
        return o;
    }
}

/// <summary>One line of a diff hunk. Kind: context, added or deleted; <see cref="Old"/> / <see cref="New"/> are its 1-based line numbers on each side (null on the side it is not on).</summary>
public sealed record CommitDiffLine(string Kind, int? Old, int? New, string Text)
{
    public const string Context = "context", Added = "added", Deleted = "deleted";
}

/// <summary>One hunk, as git's unified diff gives it (<see cref="Header"/> is its "@@ ... @@" line).</summary>
public sealed record CommitDiffHunk(string Header, int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<CommitDiffLine> Lines);

/// <summary>
/// git.commitDiff (B072): one file's diff in a commit (a merge: against its first parent). Binary or too large: a
/// <see cref="Summary"/> line and no hunks.
/// </summary>
public sealed record CommitFileDiff(string Path, string? OldPath, string Status, bool Binary, bool TooLarge, string? Summary, int Added, int Deleted,
    IReadOnlyList<CommitDiffHunk> Hunks)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["path"] = Path };
        if (OldPath is not null) o["oldPath"] = OldPath;
        o["status"] = Status;
        o["binary"] = Binary;
        o["tooLarge"] = TooLarge;
        if (Summary is not null) o["summary"] = Summary;
        o["added"] = Added;
        o["deleted"] = Deleted;
        var hunks = new JsonArray();
        foreach (var h in Hunks)
        {
            var lines = new JsonArray();
            foreach (var l in h.Lines)
            {
                var line = new JsonObject { ["kind"] = l.Kind };
                if (l.Old is not null) line["old"] = l.Old;
                if (l.New is not null) line["new"] = l.New;
                line["text"] = l.Text;
                lines.Add(line);
            }
            hunks.Add(new JsonObject
            {
                ["header"] = h.Header, ["oldStart"] = h.OldStart, ["oldCount"] = h.OldCount, ["newStart"] = h.NewStart, ["newCount"] = h.NewCount,
                ["lines"] = lines,
            });
        }
        o["hunks"] = hunks;
        return o;
    }
}

/// <summary>
/// B072, the commit view: any commit ID explained from plain git on the TEAM's repository (never a member's clone):
/// what it is (message, author, merge or not, the member, card and workflow that made it from the app's records),
/// where it is (base, team branch, member branches, origin, tags) and what it changed (files and their diffs; a
/// merge against its first parent). Reads only, outside the team's lock: nothing here moves a ref. Works the same
/// with any remote or none (origin is only read as its remote-tracking refs, as of the last fetch).
/// </summary>
public sealed partial class TeamRepos
{
    /// <summary>A file diff with more changed lines than this (added + deleted) is summarised, not shown.</summary>
    public const int MaxCommitDiffLines = 5000;

    /// <summary>A file diff longer than this many characters is summarised, not shown.</summary>
    public const int MaxCommitDiffChars = 2_000_000;

    private static readonly Regex CommitIdPattern = new("^[0-9a-fA-F]{4,64}$", RegexOptions.CultureInvariant);

    private const string Sep = "\x1f";

    /// <summary>git.commitShow: the commit, or Found = false with the reason when git does not have it.</summary>
    public async Task<CommitInfo> GitCommitShowAsync(long teamId, string team, string sha, CancellationToken ct = default, string? repoName = null)
    {
        var asked = CheckCommitId(sha);
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        var path = repo.Path;
        var (resolved, ambiguous) = await ResolveCommitAsync(path, asked, ct);
        if (resolved is null) return new CommitInfo(false, GoneReason(team, asked, ambiguous), asked);
        var head = await Git.TryRunAsync(path, ["show", "-s", "--no-color",
            $"--format=%H{Sep}%P{Sep}%an{Sep}%ae{Sep}%aI{Sep}%cn{Sep}%ce{Sep}%cI{Sep}%s{Sep}%b", resolved, "--"], ct);
        if (head.ExitCode != 0) return new CommitInfo(false, GoneReason(team, asked, false), asked);
        var f = head.Stdout.Split('\x1f', 10);
        if (f.Length < 10) throw new GitException($"git show {asked} printed what the app cannot read");
        var full = f[0].Trim();
        var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var author = new CommitPerson(f[2], f[3], f[4]);
        var committer = new CommitPerson(f[5], f[6], f[7]);
        var differs = !string.Equals(author.Name, committer.Name, StringComparison.Ordinal)
                      || !string.Equals(author.Email, committer.Email, StringComparison.OrdinalIgnoreCase);

        var files = await CommitFilesOfAsync(path, full, ct);
        var refs = (await Git.RunAsync(path, ["for-each-ref", "--contains", full, "--format=%(refname)", "refs/heads", "refs/remotes/origin", "refs/tags"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var heads = refs.Where(r => r.StartsWith("refs/heads/", StringComparison.Ordinal)).Select(r => r["refs/heads/".Length..]).ToHashSet(StringComparer.Ordinal);
        var origin = refs.Where(r => r.StartsWith("refs/remotes/origin/", StringComparison.Ordinal) && r != "refs/remotes/origin/HEAD")
            .Select(r => r["refs/remotes/origin/".Length..]).ToList();
        var tags = refs.Where(r => r.StartsWith("refs/tags/", StringComparison.Ordinal)).Select(r => r["refs/tags/".Length..]).ToList();

        var teamBranch = TeamBranchFor(team);
        var memberPrefix = MemberBranchPrefix(team);
        var memberBranches = heads.Where(h => h.StartsWith(memberPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var branches = new List<string>();
        if (heads.Contains(teamBranch)) branches.Add(teamBranch);
        branches.AddRange(memberBranches);
        var inMain = heads.Contains(repo.BaseBranch) || origin.Contains(repo.BaseBranch);
        var others = heads.Where(h => h != repo.BaseBranch && h != teamBranch && !h.StartsWith(BranchPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();

        var record = RecordedEventOf(team, full);
        CommitMerge? merge = null;
        if (parents.Length > 1) merge = await MergeOfAsync(teamId, team, path, full, parents, f[8], record, heads, repo.BaseBranch, ct);
        var onMemberBranchesOnly = !inMain && !heads.Contains(teamBranch);
        var made = MadeOf(teamId, team, parents.Length > 1, record, onMemberBranchesOnly ? memberBranches : []);

        string? onlyOn = null;
        if (onMemberBranchesOnly && memberBranches.Count > 0)
        {
            var owners = memberBranches.Select(b => MemberOfBranch(teamId, b)).Where(m => m is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            onlyOn = made is not null && owners.Contains(made.Member, StringComparer.OrdinalIgnoreCase) ? made.Member : owners.Count == 1 ? owners[0] : null;
        }

        return new CommitInfo(true, null, full, full[..Math.Min(12, full.Length)], f[8].Trim(), f[9].Trim(), author, differs ? committer : null,
            parents, merge, made, new CommitWhere(inMain, branches, onlyOn, origin.Count > 0, tags, others), files);
    }

    /// <summary>git.commitDiff: one file of the commit, as hunks of lines (a merge against its first parent); binary or too large, a summary only.</summary>
    public async Task<CommitFileDiff> GitCommitDiffAsync(long teamId, string team, string sha, string file, CancellationToken ct = default, string? repoName = null)
    {
        var asked = CheckCommitId(sha);
        if (string.IsNullOrEmpty(file)) throw new ArgumentException("path is required: the file of the commit to show");
        var repo = await GitRepoAsync(teamId, team, repoName, ct);
        var path = repo.Path;
        var (resolved, ambiguous) = await ResolveCommitAsync(path, asked, ct);
        var full = resolved ?? throw new TeamGitRefusal("gone", GoneReason(team, asked, ambiguous));
        var files = await CommitFilesOfAsync(path, full, ct);
        var change = files.FirstOrDefault(c => c.Path == file) ?? files.FirstOrDefault(c => c.OldPath == file)
                     ?? throw new TeamGitRefusal("no_such_file", $"commit {full[..Math.Min(12, full.Length)]} does not change {file}");
        CommitFileDiff Summary(bool binary, bool tooLarge, string text) =>
            new(change.Path, change.OldPath, change.Status, binary, tooLarge, text, change.Added, change.Deleted, []);
        if (change.Binary)
            return Summary(true, false, change.Status switch
            {
                "added" => "Binary file added; its content is not shown.",
                "deleted" => "Binary file deleted; its content is not shown.",
                "renamed" => "Binary file renamed; its content is not shown.",
                _ => "Binary file changed; its content is not shown.",
            });
        if (change.Added + change.Deleted > MaxCommitDiffLines) return Summary(false, true, TooLargeText(change));
        string[] paths = change.OldPath is { } old ? [old, change.Path] : [change.Path];
        var text = await Git.RunAsync(path, ["--literal-pathspecs", "show", "--format=", "--diff-merges=first-parent", "-M", "--no-ext-diff", "--no-textconv",
            "--no-color", "-U3", full, "--", .. paths], ct);
        if (text.Length > MaxCommitDiffChars) return Summary(false, true, TooLargeText(change));
        var hunks = ParseUnifiedDiff(text);
        return new CommitFileDiff(change.Path, change.OldPath, change.Status, false, false,
            hunks.Count == 0 ? "No lines changed: only the file's name or mode changed." : null, change.Added, change.Deleted, hunks);
    }

    private static string TooLargeText(CommitFileChange c) =>
        $"Too large to show here: {c.Added.ToString(CultureInfo.InvariantCulture)} lines added, {c.Deleted.ToString(CultureInfo.InvariantCulture)} deleted.";

    /// <summary>A commit ID as asked: 4 to 64 hex digits (never an option, a ref name or a revision expression).</summary>
    private static string CheckCommitId(string sha)
    {
        var s = (sha ?? "").Trim();
        if (!CommitIdPattern.IsMatch(s)) throw new ArgumentException($"'{sha}' is not a commit ID (4 to 64 hexadecimal digits)");
        return s.ToLowerInvariant();
    }

    /// <summary>The most objects an ID's prefix is looked at for (a 4-digit prefix in a large repository can match many).</summary>
    private const int MaxCommitIdCandidates = 64;

    /// <summary>
    /// The full ID of the commit an ID names, read as an object ID only, never a ref name (a branch called "cafe" never
    /// wins over the commit cafe...): the objects whose names start with it (git rev-parse --disambiguate), each peeled
    /// to its commit (an annotated tag's commit, as git's ^{commit} does). Null when none is a commit; ambiguous when
    /// they peel to more than one commit.
    /// </summary>
    private async Task<(string? Full, bool Ambiguous)> ResolveCommitAsync(string path, string asked, CancellationToken ct)
    {
        var listed = await Git.TryRunAsync(path, ["rev-parse", "--disambiguate=" + asked], ct);
        if (listed.ExitCode != 0) return (null, false);
        var objects = listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(o => CommitIdPattern.IsMatch(o) && o.StartsWith(asked, StringComparison.OrdinalIgnoreCase)).Take(MaxCommitIdCandidates + 1).ToList();
        if (objects.Count > MaxCommitIdCandidates) return (null, true);
        var commits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in objects)
        {
            // A full object ID: git reads it as the object even when a ref has the same name.
            var peeled = await Git.TryRunAsync(path, ["rev-parse", "--verify", "--quiet", o + "^{commit}"], ct);
            if (peeled.ExitCode == 0 && peeled.Stdout.Trim() is { Length: > 0 } c) commits.Add(c);
        }
        return commits.Count switch { 1 => (commits.First(), false), 0 => (null, false), _ => (null, true) };
    }

    /// <summary>Why git has no such commit, in plain words: the app's records say which branch it was on, when they know.</summary>
    private string GoneReason(string team, string sha, bool ambiguous)
    {
        if (ambiguous)
            return $"{sha} matches more than one object in the team's repository: use more of the commit ID.";
        var record = RecordedEventOf(team, sha);
        var shown = sha[..Math.Min(12, sha.Length)];
        if (record is not null)
        {
            var whose = record.Member is { } m && record.Branch.StartsWith(MemberBranchPrefix(team), StringComparison.Ordinal) ? $" ({m}'s branch)" : "";
            return $"Commit {shown} was on {record.Branch}{whose}, and git no longer has it: that branch was deleted and git has since cleaned up its commits.";
        }
        return $"git has no commit {shown} in the team's repository. It may have been on a branch that was deleted and since cleaned up by git, or it is from another repository.";
    }

    /// <summary>
    /// The files of a commit with their +/- counts (a merge: against its first parent; a root commit: all added), in
    /// git's order. Rename detection on.
    /// </summary>
    private async Task<IReadOnlyList<CommitFileChange>> CommitFilesOfAsync(string path, string sha, CancellationToken ct)
    {
        string[] common = ["show", "--format=", "-z", "-M", "--diff-merges=first-parent", "--no-ext-diff", "--no-textconv", "--no-color"];
        var names = ParseNameStatus(await Git.RunAsync(path, [.. common, "--name-status", sha, "--"], ct));
        var counts = ParseNumstat(await Git.RunAsync(path, [.. common, "--numstat", sha, "--"], ct));
        var list = new List<CommitFileChange>();
        foreach (var n in names)
        {
            var (added, deleted, binary) = counts.GetValueOrDefault(n.Path);
            list.Add(new CommitFileChange(n.Path, n.OldPath, n.Status switch { "A" => "added", "D" => "deleted", "R" => "renamed", _ => "modified" },
                added, deleted, binary));
        }
        return list;
    }

    /// <summary>git's <c>--numstat -z</c>: "added\tdeleted\tpath\0", or for a rename "added\tdeleted\t\0old\0new\0"; "-" counts are a binary file. Keyed by the new path.</summary>
    internal static Dictionary<string, (int Added, int Deleted, bool Binary)> ParseNumstat(string output)
    {
        var map = new Dictionary<string, (int, int, bool)>(StringComparer.Ordinal);
        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i].TrimStart('\n');
            if (t.Length == 0) continue;
            var parts = t.Split('\t', 3);
            if (parts.Length < 3) continue;
            var file = parts[2];
            if (file.Length == 0)
            {
                if (i + 2 >= tokens.Length) break;
                file = tokens[i + 2];
                i += 2;
            }
            var binary = parts[0] == "-" || parts[1] == "-";
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a);
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d);
            map[file] = (a, d, binary);
        }
        return map;
    }

    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant);

    /// <summary>
    /// The hunks of one file's unified diff (git show / git diff output): the header lines before the first "@@" are
    /// skipped, "\ No newline at end of file" is dropped, and a second file's section ends it. A context line git
    /// printed as a lone space and the core trimmed is still read as an empty context line.
    /// </summary>
    internal static IReadOnlyList<CommitDiffHunk> ParseUnifiedDiff(string text)
    {
        var hunks = new List<CommitDiffHunk>();
        List<CommitDiffLine>? lines = null;
        string header = "";
        int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0, o = 0, n = 0, oldLeft = 0, newLeft = 0;
        var seenFile = false;
        void Close()
        {
            // Context lines missing at the very end: empty lines git printed as " ", which the trimmed output lost.
            while (lines is not null && oldLeft > 0 && newLeft > 0)
            {
                lines.Add(new CommitDiffLine(CommitDiffLine.Context, o++, n++, ""));
                oldLeft--;
                newLeft--;
            }
            if (lines is not null) hunks.Add(new CommitDiffHunk(header, oldStart, oldCount, newStart, newCount, lines));
            lines = null;
        }
        foreach (var line in text.Split('\n'))
        {
            if (lines is not null && (oldLeft > 0 || newLeft > 0))
            {
                if (line.StartsWith('+')) { lines.Add(new CommitDiffLine(CommitDiffLine.Added, null, n++, line[1..])); newLeft--; continue; }
                if (line.StartsWith('-')) { lines.Add(new CommitDiffLine(CommitDiffLine.Deleted, o++, null, line[1..])); oldLeft--; continue; }
                if (line.StartsWith(' ') || line.Length == 0)
                {
                    lines.Add(new CommitDiffLine(CommitDiffLine.Context, o++, n++, line.Length == 0 ? "" : line[1..]));
                    oldLeft--;
                    newLeft--;
                    continue;
                }
                if (line.StartsWith('\\')) continue;
            }
            if (line.StartsWith('\\')) continue;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (seenFile) break;
                seenFile = true;
                continue;
            }
            var m = HunkHeader.Match(line);
            if (!m.Success) continue;
            Close();
            static int Num(Group g, int fallback) => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : fallback;
            oldStart = Num(m.Groups[1], 0);
            oldCount = Num(m.Groups[2], 1);
            newStart = Num(m.Groups[3], 0);
            newCount = Num(m.Groups[4], 1);
            header = line;
            lines = [];
            o = oldStart == 0 && oldCount == 0 ? 1 : oldStart;
            n = newStart == 0 && newCount == 0 ? 1 : newStart;
            oldLeft = oldCount;
            newLeft = newCount;
        }
        Close();
        return hunks;
    }

    /// <summary>The app's best record of this commit: a commit event (the member's own work) before a merge-team one, then any.</summary>
    private sealed record RecordedEvent(string Kind, string? Member, string Branch, long? WorkflowId, string? Into);

    private RecordedEvent? RecordedEventOf(string team, string sha)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // A short ID matches by prefix (the IDs are stored in full, lower case).
        cmd.CommandText = """
            SELECT kind, member, branch, workflow_id, extra FROM code_events
            WHERE team = $t COLLATE NOCASE AND kind IN ('commit', 'merge-team') AND (sha = $s OR (length($s) < 40 AND substr(sha, 1, length($s)) = $s))
            ORDER BY CASE kind WHEN 'commit' THEN 0 ELSE 1 END, id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$t", team);
        cmd.Parameters.AddWithValue("$s", sha.ToLowerInvariant());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        string? into = null;
        if (!r.IsDBNull(4))
        {
            try { into = (string?)(JsonNode.Parse(r.GetString(4)) as JsonObject)?["into"]; }
            catch (System.Text.Json.JsonException) { }
        }
        return new RecordedEvent(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), into);
    }

    /// <summary>
    /// Who made it: a commit event's member; for a merge the team branch recorded (merge-team), the team's Manager (the
    /// fetch-back of its run recorded it); otherwise, for a commit in neither the base nor the team branch, the one member whose branch has it. Null when unknown.
    /// </summary>
    private CommitMade? MadeOf(long teamId, string team, bool isMerge, RecordedEvent? record, IReadOnlyList<string> memberBranches)
    {
        string? member = null;
        long? workflow = null;
        if (record is { Kind: CodeEvent.Commit, Member: { } m })
        {
            member = m;
            workflow = record.WorkflowId;
        }
        else if (record is { Kind: CodeEvent.MergeTeam })
        {
            member = isMerge ? ManagerOf(teamId) : record.Member;
            workflow = record.WorkflowId;
        }
        else if (memberBranches.Count == 1)
        {
            member = MemberOfBranch(teamId, memberBranches[0]);
        }
        if (member is null) return null;
        return new CommitMade(member, workflow is { } w ? CardOf(team, member, w) : null, workflow);
    }

    private string? ManagerOf(long teamId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM members WHERE team_id = $t AND role = 'manager' ORDER BY id LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", teamId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>The member's card in that workflow (the newest), as the board keeps it.</summary>
    private long? CardOf(string team, string member, long workflow)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM board_cards WHERE team = $t COLLATE NOCASE AND member = $m COLLATE NOCASE AND workflow = $w ORDER BY id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$t", team);
        cmd.Parameters.AddWithValue("$m", member);
        cmd.Parameters.AddWithValue("$w", workflow);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>The member a members/&lt;team&gt;/&lt;name&gt; branch (or origin's copy) belongs to: the member whose clone is on it, else the member whose name it is.</summary>
    private string? MemberOfBranch(long teamId, string branch)
    {
        if (branch.StartsWith("origin/", StringComparison.Ordinal)) branch = branch["origin/".Length..];
        if (MemberForBranch(teamId, branch) is { } byClone) return byClone;
        var slash = branch.LastIndexOf('/');
        if (!branch.StartsWith(BranchPrefix, StringComparison.Ordinal) || slash < 0) return null;
        var slug = branch[(slash + 1)..];
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM members WHERE team_id = $t ORDER BY id;";
        cmd.Parameters.AddWithValue("$t", teamId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (SlugOf(r.GetString(0)) == slug) return r.GetString(0);
        return null;
    }

    private static readonly Regex[] MergeMessages =
    [
        // git's own: Merge branch 'x' [of <url>] [into y]; Merge remote-tracking branch 'origin/x' [into y]; GitLab's Merge branch 'x' into 'y'.
        new(@"^Merge (?:remote-tracking )?branch '(?<from>[^']+)'(?: of .*?)?(?: into '?(?<into>[^'\s]+)'?)?\s*$", RegexOptions.CultureInvariant),
        // GitHub: Merge pull request #12 from owner/branch
        new(@"^Merge pull request #\d+ from (?<from>\S+)", RegexOptions.CultureInvariant),
        // Plain: Merge x into y; Merged x into y
        new(@"^Merged? (?<from>[^\s']+) into (?<into>[^\s']+)", RegexOptions.CultureInvariant),
    ];

    /// <summary>
    /// What a merge brought in: from the app's record of it (a member branch merged into the team branch), else git's
    /// merge message, else the branch that has the merged side but not the merge. The count is the commits on the
    /// merged side(s) that the first parent did not have.
    /// </summary>
    private async Task<CommitMerge> MergeOfAsync(long teamId, string team, string path, string sha, IReadOnlyList<string> parents, string subject,
        RecordedEvent? record, IReadOnlySet<string> heads, string baseBranch, CancellationToken ct)
    {
        var log = await Git.RunAsync(path, ["log", "--format=%H", .. parents.Skip(1), "^" + parents[0], "--"], ct);
        var count = log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        string? from = null, into = null;
        if (record is { Kind: CodeEvent.MergeTeam })
        {
            from = record.Branch;
            into = record.Into;
        }
        foreach (var pattern in MergeMessages)
        {
            if (from is not null && into is not null) break;
            var m = pattern.Match(subject.Trim());
            if (!m.Success) continue;
            from ??= m.Groups["from"].Value;
            if (m.Groups["into"].Success) into ??= m.Groups["into"].Value;
            break;
        }
        if (from is null)
        {
            var holders = (await Git.RunAsync(path, ["for-each-ref", "--contains", parents[1], "--no-contains", sha, "--format=%(refname:short)", "refs/heads"], ct))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (holders.Length == 1) from = holders[0];
        }
        if (into is null)
        {
            // The branch the merge was made on: one whose first-parent line holds it. A branch that only contains it
            // (a team branch moved up to the base afterwards) did not receive it. The base first, then the team branch,
            // members' branches and any other; null when no branch has it on its own line.
            var teamBranch = TeamBranchFor(team);
            var order = new List<string>();
            if (heads.Contains(baseBranch)) order.Add(baseBranch);
            if (heads.Contains(teamBranch)) order.Add(teamBranch);
            order.AddRange(heads.Where(h => h != baseBranch && h != teamBranch)
                .OrderBy(h => h.StartsWith(BranchPrefix, StringComparison.Ordinal) ? 0 : 1).ThenBy(h => h, StringComparer.Ordinal));
            foreach (var h in order)
            {
                if (!await OnFirstParentLineAsync(path, h, sha, ct)) continue;
                into = h;
                break;
            }
        }
        string? fromMember = null;
        if (from is not null)
        {
            var local = from.StartsWith("origin/", StringComparison.Ordinal) ? from["origin/".Length..] : from;
            if (local.StartsWith(MemberBranchPrefix(team), StringComparison.Ordinal) || local.StartsWith(BranchPrefix, StringComparison.Ordinal))
                fromMember = record is { Kind: CodeEvent.MergeTeam, Member: { } rm } ? rm : MemberOfBranch(teamId, local);
        }
        return new CommitMerge(into, from, fromMember, count);
    }

    /// <summary>
    /// Whether <paramref name="sha"/> is on <paramref name="branch"/>'s first-parent line: the branch's tip, or the first
    /// parent of a commit on that line. The line is walked from the tip down to the commit and no further.
    /// </summary>
    private async Task<bool> OnFirstParentLineAsync(string path, string branch, string sha, CancellationToken ct)
    {
        var walked = await Git.TryRunAsync(path, ["rev-list", "--first-parent", "--parents", "refs/heads/" + branch, "^" + sha, "--"], ct);
        if (walked.ExitCode != 0) return false;
        var lines = walked.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            var tip = await Git.TryRunAsync(path, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], ct);
            return tip.ExitCode == 0 && tip.Stdout.Trim() == sha;
        }
        return lines.Any(l => l.Split(' ') is { Length: > 1 } p && p[1] == sha);
    }
}
