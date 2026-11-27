using Yawble.Core.Ops;
using Xunit;

namespace Yawble.Core.Tests.Ops;

/// <summary>
/// Only a real link counts as one. OneDrive Files On-Demand files and folders, and deduplicated files, are reparse points
/// too, but they have no link target: they are plain files and folders, and a granted OneDrive folder must still work.
/// </summary>
public sealed class PathLinksTests
{
    [Fact]
    public void AReparsePointWithNoTargetIsNotALink() =>
        Assert.False(PathLinks.CountsAsLink(FileAttributes.ReparsePoint | FileAttributes.Directory, linkTarget: null));

    [Fact]
    public void AReparsePointWithATargetIsALink() =>
        Assert.True(PathLinks.CountsAsLink(FileAttributes.ReparsePoint | FileAttributes.Directory, @"C:\elsewhere"));

    [Fact]
    public void APlainPathWithNoTargetIsNotALink() =>
        Assert.False(PathLinks.CountsAsLink(FileAttributes.Archive, linkTarget: null));
}
