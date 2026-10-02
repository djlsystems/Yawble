using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The keep-both name: `name (copy).ext`, then `name (copy 2).ext`, before the last extension; a
/// folder and a dot-name have none; a copy suffix is not stacked; a name taken earlier in the same
/// batch counts as taken.
/// </summary>
public sealed class TeamDocumentsFreeNameTests
{
    [Theory]
    [InlineData("a.md", false, new string[] { "a.md" }, "a (copy).md")]
    [InlineData("a.md", false, new string[] { "a.md", "a (copy).md" }, "a (copy 2).md")]
    [InlineData("a.md", false, new string[] { "a.md", "a (copy).md", "a (copy 2).md" }, "a (copy 3).md")]
    [InlineData(".env", false, new string[] { ".env" }, ".env (copy)")]
    [InlineData("tree", true, new string[] { "tree" }, "tree (copy)")]
    [InlineData("v1.2", true, new string[] { "v1.2" }, "v1.2 (copy)")]
    [InlineData("a (copy).md", false, new string[] { "a (copy).md" }, "a (copy 2).md")]
    [InlineData("a (copy 4).md", false, new string[] { "a (copy 4).md" }, "a (copy).md")]
    [InlineData("a.tar.gz", false, new string[] { "a.tar.gz" }, "a.tar (copy).gz")]
    [InlineData("README", false, new string[] { "README" }, "README (copy)")]
    public void Keep_both_picks_the_first_free_copy_name(string name, bool isFolder, string[] taken, string expected)
    {
        Assert.Equal(expected, TeamDocuments.FreeName(name, isFolder, taken.Contains));
    }

    [Fact]
    public void A_name_chosen_earlier_in_the_same_batch_counts_as_taken()
    {
        var onDisk = new HashSet<string> { "a.md" };
        var chosen = new HashSet<string>();

        foreach (var _ in Enumerable.Range(0, 3))
        {
            chosen.Add(TeamDocuments.FreeName("a.md", false, n => onDisk.Contains(n) || chosen.Contains(n)));
        }

        Assert.Equal(["a (copy).md", "a (copy 2).md", "a (copy 3).md"], chosen);
    }
}
