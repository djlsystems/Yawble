using Yawble.Core.Bus;

namespace Yawble.Core.Teams;

/// <summary>
/// The prompt a headless run is started with (the launcher passes it to the CLI's -p): who the agent is, where its
/// role text is, its clone and branch, then the batch — one instruction as is, several as numbered items, and the
/// Manager's notices as sentences (<see cref="BatchText"/>).
/// </summary>
public static class TeamPrompt
{
    public static string For(RunContext run)
    {
        var role = run.Member.Role == MemberRole.Manager ? "manager" : "member";
        var who = run.Member.Role == MemberRole.Manager ? "the Manager" : "a member";
        var lines = new List<string>
        {
            $"You are {run.Member.Name}, {who} of team {run.Member.Team}. First call the skills_get tool with name \"{role}\" and follow your role text.",
        };
        if (!string.IsNullOrWhiteSpace(run.Instructions))
            lines.Add($"Your standing job on the team: {run.Instructions.Trim()}");
        lines.AddRange(ApprovalLines(run.Approvals));
        lines.AddRange(OpenQuestionLines(run.OpenQuestions));
        var skills = run.Skills().Where(x => x.Name != role).ToList();
        if (skills.Count > 0)
            lines.Add("Other skills you can load with skills_get when they apply: " + string.Join("; ", skills.Select(x => $"{x.Name} ({x.Description})")));
        if (run.Checkout is { } c)
        {
            lines.Add(run.Member.Role == MemberRole.Manager
                ? $"Your members commit on their own branches ({TeamRepos.MemberBranchPrefix(run.Member.Team)}<name>, e.g. {TeamRepos.BranchFor(run.Member.Team, "DeveloperPriya")}); the app brings each into the team's repository when their run ends. Your own branch, {c.Branch}, is the team's delivery branch: merge accepted work into it (see your role's Repositories section). The app pushes branches to the team repository's origin after each run; you never push."
                : $"Your working directory is your own clone of the team's repository, on branch {c.Branch}. Commit your work there. There is no remote: never push.");
            // Every branch of the team (teammates', the team branch) is in the team's bundle: say where, every run, so a
            // tester checking a developer's work never concludes it is unreachable.
            lines.Add($"Teammates' branches ({TeamRepos.MemberBranchPrefix(run.Member.Team)}<name>) and the team branch {TeamRepos.TeamBranchFor(run.Member.Team)} are in the team's bundle: " +
                      "git fetch \"$env:YAWBLE_TEAM_BUNDLE\" <branch> (on Linux and macOS \"$YAWBLE_TEAM_BUNDLE\"), then use FETCH_HEAD.");
            lines.Add("The team's documents folder is the path in the environment variable YAWBLE_TEAM_DOCS (outside your clone): " +
                      "a document \"in YAWBLE_TEAM_DOCS\" goes to \"$env:YAWBLE_TEAM_DOCS\\<name>\", never to a folder of that name in your clone.");
            lines.AddRange(RepositoryLines(c));
            lines.AddRange(CheckoutCommitLines(c));
            if (c.CloneBaseCommit is { } built)
                lines.Add($"Your branch was built on {c.BaseBranch} at {built}: report this base commit in your hand-back (and merge {c.BaseBranch} into your branch first if you are told it is behind).");
        }
        lines.Add("");
        lines.Add(run.Items.Count == 1 ? "Your instruction:" : "Your run:");
        lines.Add("");
        if (!string.IsNullOrWhiteSpace(run.SyncNote))
        {
            lines.Add(run.SyncNote.Trim());
            lines.Add("");
        }
        lines.Add(run.Prompt);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// What the member already asked the person in this workflow and is waiting on. Said in every run of the workflow,
    /// so a run woken by something else (another hand-back) does not ask the same thing a second time.
    /// </summary>
    public static IReadOnlyList<string> OpenQuestionLines(IReadOnlyList<(long Seq, string Question)> open)
    {
        if (open.Count == 0) return [];
        var lines = new List<string> { "You already asked the person in this workflow, and they have not answered yet. Do NOT ask it again with needs_decision or blocked: " +
            "carry on with anything that does not depend on the answer, or end your turn (the answer wakes you). If what you just learned changes the question, " +
            "say so in one progress line instead." };
        foreach (var (seq, q) in open)
        {
            var first = q.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? q;
            lines.Add($"- question {seq}: {(first.Length > 200 ? first[..199] + "…" : first)}");
        }
        return lines;
    }

    /// <summary>The person's standing approvals for the team: things the Manager no longer asks about.</summary>
    public static IReadOnlyList<string> ApprovalLines(IReadOnlyList<string> approvals)
    {
        if (approvals.Count == 0) return [];
        var lines = new List<string> { "The person has approved these for your team once and for all. Do not ask about them again: go ahead, and say what you did in your progress and hand-back:" };
        lines.AddRange(approvals.Select(a => $"- {a}"));
        return lines;
    }

    /// <summary>
    /// Where the person's own checkouts are (on their base branch, merged work only) and the commit each is at: the
    /// person's CLIs (az, gh, git) run there, so a build or deploy from them uses exactly these commits. An image tag or a
    /// pinned commit for something built there must be this commit, never one taken from a member's clone.
    /// </summary>
    public static IReadOnlyList<string> CheckoutCommitLines(MemberCheckout c)
    {
        var parts = new List<string>();
        if (c.BaseCommit is { } main) parts.Add($"main repository: {c.BaseBranch} at {main}");
        foreach (var e in c.Extras)
            if (e.BaseCommit is { } sha) parts.Add($"{e.Name}: {e.BaseBranch} at {sha}");
        if (parts.Count == 0) return [];
        return ["The person's merged checkouts, where the person's CLIs (az, gh, git) run, are at: " + string.Join("; ", parts) +
                ". A build or deploy run there uses exactly these commits: tag images and pin commits with them."];
    }

    /// <summary>The team's other repositories, one line each (none when it has only its main one).</summary>
    public static IReadOnlyList<string> RepositoryLines(MemberCheckout c)
    {
        if (c.Extras.Count == 0) return [];
        var lines = new List<string> { "The team has more repositories (by name; use the full paths, they are outside your working directory):" };
        foreach (var e in c.Extras)
            lines.Add(e.Writable
                ? $"- {e.Name}: your own clone at {e.Path}, on branch {e.Branch} (from {e.BaseBranch}). Commit there like in your main clone; the app brings the branch back and pushes it after your run, never push yourself." +
                  (e.SharedBundle is { } b ? $" Teammates' branches: git fetch \"{Path.Combine(b, TeamRepos.SharedBundleFile)}\" <branch>." : "")
                : $"- {e.Name}: READ-ONLY reference at {e.Path} ({e.BaseBranch}, kept current by the app). Read it; you cannot change it.");
        return lines;
    }
}
