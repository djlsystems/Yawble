using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The host runs only in a Linux container, so Windows-only naming rules - device names,
/// a MAX_PATH budget - do not apply; what is refused is what Linux, or the name's other sinks,
/// refuse.
/// </summary>
public sealed class LinuxNamingRulesTests
{
    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    public void A_windows_device_name_is_an_ordinary_container_name(string name)
    {
        Assert.True(ContainerId.IsLegalName(name));
        Assert.Equal(name, ContainerId.DeriveName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a\0b")]
    [InlineData("-flag")]
    public void A_name_linux_or_a_sink_refuses_is_still_refused(string name)
    {
        Assert.False(ContainerId.IsLegalName(name));
    }

    [Fact]
    public void A_container_name_is_still_bounded()
    {
        Assert.False(ContainerId.IsLegalName(new string('a', ContainerId.MaxNameLength + 1)));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("nul.txt")]
    [InlineData("com1")]
    public void A_device_named_login_keeps_its_readable_workspace_name(string login)
    {
        var candidates = ConciergeWorkspaceName.CandidatesFor(login, "user-1");

        Assert.Equal(login, candidates[0]);
        Assert.Equal($"{login}-{ConciergeWorkspaceName.SuffixFor("user-1")}", candidates[1]);
    }

    [Fact]
    public void A_workspace_name_is_bounded_by_linux_name_max_not_max_path()
    {
        var login = new string('a', 300);

        var candidates = ConciergeWorkspaceName.CandidatesFor(login, "user-1");

        Assert.Equal(255, candidates[0].Length);
        Assert.All(candidates, candidate => Assert.True(candidate.Length <= 255));
        Assert.Equal(new string('a', 60), ConciergeWorkspaceName.Readable(new string('a', 60)));
    }

    // A folder or file name the host creates on disk. ':' and a trailing dot or space are
    // Windows rules (an NTFS stream marker; a name Windows strips); Linux stores all three as typed.
    [Theory]
    [InlineData("x.txt:hidden")]
    [InlineData("nope.")]
    [InlineData("...")]
    [InlineData("trailing ")]
    [InlineData(@"back\slash")]
    public void A_folder_name_only_windows_refuses_is_accepted(string name)
    {
        Assert.True(FileSystemEndpoints.IsSingleSegmentName(name, "folder", out var error), error);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\0b")]
    [InlineData(".")]
    [InlineData("..")]
    public void A_folder_name_linux_refuses_is_still_refused(string name)
    {
        Assert.False(FileSystemEndpoints.IsSingleSegmentName(name, "folder", out var error));
        Assert.NotEmpty(error);
    }

    // A repository's folder name is the URL's last segment, and the clone below it is git's.
    [Theory]
    [InlineData("https://example.com/org/a:b.git", "a:b")]
    [InlineData("https://example.com/org/name.", "name.")]
    [InlineData("https://example.com/org/name%20", "name ")]
    [InlineData("https://example.com/org/back%5Cslash.git", @"back\slash")]
    [InlineData("https://example.com/org/.dotfiles.git", ".dotfiles")]
    public void A_repo_folder_name_only_windows_refuses_is_accepted(string url, string name)
    {
        Assert.Equal([url], RepoUrls.Validate([url]));
        Assert.Equal(name, RepoUrls.DeriveName(url));
    }

    [Theory]
    [InlineData("https://example.com/org/a%00b.git")]
    [InlineData("https://example.com/org/a%2Fb.git")]
    [InlineData("https://example.com/org/%2E%2E")]
    [InlineData("https://example.com/org/.git")]
    [InlineData("https://example.com/org/.GIT.git")]
    [InlineData("https://example.com/org/")]
    public void A_repo_folder_name_linux_or_git_refuses_is_still_refused(string url)
    {
        Assert.Throws<ArgumentException>(() => RepoUrls.Validate([url]));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    public void A_workspace_name_never_holds_a_separator_or_a_dot_name(string login)
    {
        foreach (var candidate in ConciergeWorkspaceName.CandidatesFor(login, "user-1"))
        {
            Assert.DoesNotContain('/', candidate);
            Assert.NotEqual(".", candidate);
            Assert.NotEqual("..", candidate);
        }
    }
}
