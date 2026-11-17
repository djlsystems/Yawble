using Yawble.Core.Ops;
using Xunit;

namespace Yawble.Core.Tests.Ops;

/// <summary>
/// A folder walk never follows links: an agent's sandbox once held a link to its own parent folder, and the Doctor's
/// disk check walked it round and round (gigabytes of ever-longer paths a second).
/// </summary>
public sealed class FolderWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yawble-folderwalk-" + Guid.NewGuid().ToString("N"));

    public FolderWalkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A directory link, or null when this computer does not let the tests make one.</summary>
    private static string? TryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return link; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    [Fact]
    public void CountsTheFilesOnceAndDoesNotFollowALinkBackIntoItself()
    {
        var inner = Path.Combine(_root, "loop");
        Directory.CreateDirectory(inner);
        File.WriteAllBytes(Path.Combine(_root, "a.log"), new byte[100]);
        File.WriteAllBytes(Path.Combine(inner, "b.log"), new byte[23]);
        if (TryLink(Path.Combine(inner, "d"), inner) is null) Assert.Skip("this computer does not allow the tests to make a directory link");

        Assert.Equal(123, FolderWalk.Size(_root));
        Assert.Equal(123, FolderWalk.Size(_root, "*.log"));
        Assert.Equal(["a.log", "b.log"], FolderWalk.Files(_root).Select(f => f.Name).Order());
    }

    [Fact]
    public void DoesNotReachFilesOutsideTheFolderThroughALink()
    {
        var outside = Path.Combine(_root, "outside");
        var folder = Path.Combine(_root, "folder");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(folder);
        var precious = Path.Combine(outside, "keep.txt");
        File.WriteAllText(precious, "x");
        File.SetAttributes(precious, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(folder, "mine.txt"), "y");
        if (TryLink(Path.Combine(folder, "out"), outside) is null) Assert.Skip("this computer does not allow the tests to make a directory link");

        Assert.Equal(["mine.txt"], FolderWalk.Files(folder).Select(f => f.Name));
        File.SetAttributes(precious, FileAttributes.Normal);
    }

    [Fact]
    public void AFolderWithoutLinksIsWalkedWhole()
    {
        Directory.CreateDirectory(Path.Combine(_root, "x", "y"));
        File.WriteAllBytes(Path.Combine(_root, "x", "y", "z.bin"), new byte[7]);
        File.WriteAllBytes(Path.Combine(_root, "top.bin"), new byte[3]);
        Assert.Equal(10, FolderWalk.Size(_root));
    }
}
