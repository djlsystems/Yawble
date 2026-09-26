namespace Harness.Host;

/// <summary>
/// WHAT THE GIT DIALOG HANDS THE TEAM WHEN A REBASE WOULD CONFLICT.
///
/// The dialog's rebase stops safely on a conflict and changes nothing, which on its own would leave
/// a person with a branch nobody could integrate and no way forward short of a terminal. The
/// resolution is the team's work, so the dialog names the conflicting files and offers to send the
/// Manager this instruction.
///
/// MERGE, NOT REBASE. The clone's main has been pushed as the team branch, and the seeded skills
/// tell agents never to rewrite a pushed commit; a merge of origin/main leaves main a fast-forward
/// of origin/main, which is all Push and Merge to main need.
/// </summary>
public static class BringCurrentAsk
{
    /// <summary>The card title, naming the repository's stored default branch.</summary>
    public static string Subject(string branch) => $"Bring {branch} current with origin/{branch}";

    /// <summary>
    /// The conflicted paths from <c>git merge-tree --write-tree --name-only</c>: the lines after the
    /// tree oid, up to the first blank line. Empty for a clean merge, and for output that does not
    /// start with a tree oid at all (the merge could not be run).
    /// </summary>
    public static IReadOnlyList<string> ConflictedPaths(string mergeTreeStdout)
    {
        var lines = mergeTreeStdout.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || !IsTreeOid(lines[0].Trim())) return [];

        var paths = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) break;
            paths.Add(line.Trim());
        }

        return paths;
    }

    public static string Instruction(string repo, string branch, IReadOnlyList<string> conflicts)
    {
        var files = conflicts.Count == 0
            ? "(the dialog could not name the files; run the merge to see them)"
            : string.Join("\n", conflicts.Select(path => $"- {path}"));

        return
            $"origin/{branch} has moved on since this team's work, and rebasing the {repo} clone's {branch} onto it "
            + $"conflicts, so the Git dialog changed nothing. Bring {branch} current:\n\n"
            + $"1. In the {repo} main clone, fetch origin. Merge origin/{branch} into {branch}; do not rebase, because {branch}'s "
            + "commits are already pushed as the team branch.\n"
            + "2. Resolve the conflicts, keeping the intent of both sides. The conflicting files are:\n"
            + files + "\n"
            + "3. Run the full .NET and web suites on the merge and fix what it breaks.\n"
            + $"4. Commit the merge on {branch}, then handback naming the merge commit and the test results.\n\n"
            + $"When {branch} is current, the person pushes it and merges to {branch} from the Git dialog.";
    }

    private static bool IsTreeOid(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}
