using Harness.Host;

namespace Harness.Tests.Host;

/// <summary>
/// What agents are told about a repository whose clone is not ready. A repository the team can see
/// always has its default branch - the platform gives an empty origin its first <c>main</c> - so no
/// prompt or skill served to an agent has a branch for setting up a repository with no commits.
/// A clone that is not ready is reported to the person with <c>blocked</c>; an agent left to
/// improvise made a first commit on its own branch, which origin then took as its default branch.
/// </summary>
public sealed class NotReadyCloneGuidanceTests
{
    private static IEnumerable<(string Name, string Text)> Served() =>
        BuiltInPrompts.All.Select(p => ($"prompt:{p.Role}", p.Text))
            .Concat(BuiltInSkills.All.Select(s => ($"skill:{s.Name}", s.Body)))
            .Append(("repo-section", RepoSetupMessage.PromptSection([("https://example.test/r.git", "/t/repos/r/main")])!));

    private static string Normalised(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void No_served_prompt_or_skill_says_how_to_set_up_a_repository_with_no_commits()
    {
        string[] setup =
        [
            "--allow-empty", "git init", "has no commits", "no commits yet", "with no commits",
            "empty repository", "first commit on", "make the first commit", "create main",
            "push main", "push `main`", "orphan",
        ];

        foreach (var (name, text) in Served())
        {
            var flat = Normalised(text);
            foreach (var phrase in setup)
                Assert.False(flat.Contains(phrase, StringComparison.OrdinalIgnoreCase), $"{name} says \"{phrase}\".");
        }
    }

    [Theory]
    [InlineData("prompt:Manager")]
    [InlineData("prompt:Member")]
    [InlineData("skill:worktrees")]
    [InlineData("repo-section")]
    public void Each_says_to_report_a_clone_that_is_not_ready_and_never_to_stand_in_for_the_default_branch(string name)
    {
        var text = Normalised(Served().Single(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).Text);

        Assert.Contains("not ready", text, StringComparison.Ordinal);
        Assert.Contains("`blocked`, naming the repository", text, StringComparison.Ordinal);
        Assert.Contains("make a first commit", text, StringComparison.Ordinal);
        Assert.Contains("push a branch to stand in for the default branch", text, StringComparison.Ordinal);
    }
}
