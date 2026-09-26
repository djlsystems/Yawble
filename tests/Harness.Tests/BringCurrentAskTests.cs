using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The Git dialog's way out of a conflicting rebase: name the files, and hand the resolution to
/// the team, rather than leaving the dialog with nothing to offer.
/// </summary>
public sealed class BringCurrentAskTests
{
    // A real `git merge-tree --write-tree --name-only main origin/main`, trimmed.
    private const string Wave5MergeTree =
        "01b8d571f5999a7e1e573a79add04a83657f4af2\n"
        + "web/src/components/AuthForm.vue\n"
        + "web/src/components/DiagnosticsDialog.vue\n"
        + "\n"
        + "Auto-merging web/src/api/client.ts\n"
        + "Auto-merging web/src/components/AuthForm.vue\n"
        + "CONFLICT (content): Merge conflict in web/src/components/AuthForm.vue\n";

    [Fact]
    public void The_conflicted_paths_are_the_lines_between_the_tree_and_the_blank_line()
    {
        Assert.Equal(
            ["web/src/components/AuthForm.vue", "web/src/components/DiagnosticsDialog.vue"],
            BringCurrentAsk.ConflictedPaths(Wave5MergeTree));
    }

    [Fact]
    public void A_clean_merge_names_no_paths()
    {
        Assert.Empty(BringCurrentAsk.ConflictedPaths("01b8d571f5999a7e1e573a79add04a83657f4af2\n"));
    }

    [Fact]
    public void Output_with_no_tree_is_not_a_conflict_list()
    {
        Assert.Empty(BringCurrentAsk.ConflictedPaths("fatal: unknown option `write-tree'\n"));
    }

    [Fact]
    public void The_instruction_names_every_file_and_says_merge_not_rebase()
    {
        var text = BringCurrentAsk.Instruction(
            "Harness", "main", ["web/src/components/AuthForm.vue", "web/src/components/DiagnosticsDialog.vue"]);

        Assert.Contains("web/src/components/AuthForm.vue", text);
        Assert.Contains("web/src/components/DiagnosticsDialog.vue", text);
        Assert.Contains("Merge origin/main into main", text);
        Assert.Contains("do not rebase", text);
        Assert.Contains("handback", text);
    }

    [Fact]
    public void The_instruction_and_subject_name_the_stored_default_branch_not_main()
    {
        var text = BringCurrentAsk.Instruction("Harness", "trunk", ["a.txt"]);

        Assert.Contains("Merge origin/trunk into trunk", text);
        Assert.DoesNotContain("origin/main", text);
        Assert.Equal("Bring trunk current with origin/trunk", BringCurrentAsk.Subject("trunk"));
    }
}
