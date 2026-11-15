using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Code;

/// <summary>
/// B031 finding 1a: the canonicaliser's POSIX walk, where a ".." after a link goes to the link's real parent (as Linux
/// and macOS resolve it), driven on every OS through the internal physicalDotDot switch with real links. Skips where
/// links cannot be made.
/// </summary>
public sealed class PosixDotDotWalkTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "yawble-pdd-" + Guid.NewGuid().ToString("N")[..10]);

    public PosixDotDotWalkTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(params string[] parts) => Path.Combine([_tmp, .. parts]);

    private static void DirLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Assert.Skip("cannot make a symlink here"); }
    }

    private static string? Posix(string path) => HostPathCanonicalizer.TryCanonicalize(path, physicalDotDot: true);

    private static string? Windows(string path) => HostPathCanonicalizer.TryCanonicalize(path, physicalDotDot: false);

    /// <summary>deep/dir, deep/y, a/s -> deep/dir and a/l -> s/../y (relative).</summary>
    private void MakeTheTesterLayout()
    {
        Directory.CreateDirectory(P("deep", "dir"));
        Directory.CreateDirectory(P("deep", "y"));
        Directory.CreateDirectory(P("a"));
        DirLink(P("a", "s"), P("deep", "dir"));
        DirLink(P("a", "l"), Path.Combine("s", "..", "y"));
    }

    [Fact]
    public void ARelativeTargetWithDotDotAfterALink_GoesToTheLinksRealParent()
    {
        MakeTheTesterLayout();
        var deepY = Posix(P("deep", "y"));
        Assert.NotNull(deepY);
        Assert.Equal(deepY, Posix(P("a", "l")));
        Assert.Equal(Path.Combine(deepY!, "k.txt"), Posix(P("a", "l", "k.txt"))); // a missing tail under it
    }

    [Fact]
    public void TheWindowsRule_StillCollapsesDotDotFirst()
    {
        MakeTheTesterLayout();
        // Lexically s/../y from a is a/y, which does not exist: the missing tail is kept under the real a.
        Assert.Equal(Path.Combine(Windows(P("a"))!, "y"), Windows(P("a", "l")));
    }

    [Fact]
    public void ADotDotInTheQueriedPathAfterALink_GoesToTheLinksRealParent()
    {
        Directory.CreateDirectory(P("deep", "dir"));
        Directory.CreateDirectory(P("deep", "secret"));
        Directory.CreateDirectory(P("a"));
        DirLink(P("a", "s"), P("deep", "dir"));
        var raw = P("a", "s") + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "secret";
        Assert.Equal(Posix(P("deep", "secret")), Posix(raw));
    }

    [Fact]
    public void ADotDotThatLandsOnAMissingTail_IsCollapsedAndWalkedAgain()
    {
        // a/l -> s/../gone/../y: deep/gone is missing, so the tail gone/../y is collapsed to y and walked: deep/y.
        Directory.CreateDirectory(P("deep", "dir"));
        Directory.CreateDirectory(P("deep", "y"));
        Directory.CreateDirectory(P("a"));
        DirLink(P("a", "s"), P("deep", "dir"));
        DirLink(P("a", "l"), Path.Combine("s", "..", "gone", "..", "y"));
        Assert.Equal(Posix(P("deep", "y")), Posix(P("a", "l")));
    }

    [Fact]
    public void ADotDotAtTheRoot_StaysAtTheRoot()
    {
        var root = Path.GetPathRoot(_tmp)!;
        Assert.Equal(Posix(_tmp), Posix(root + ".." + Path.DirectorySeparatorChar + _tmp[root.Length..]));
    }

    [Fact]
    public void ALoopThroughDotDot_IsNull()
    {
        Directory.CreateDirectory(P("a"));
        DirLink(P("a", "x"), Path.Combine("..", "a", "y"));
        DirLink(P("a", "y"), Path.Combine("..", "a", "x"));
        Assert.Null(Posix(P("a", "x")));
        Assert.Null(Posix(P("a", "x", "f.txt")));
    }

    [Fact]
    public void APlainPathWithoutLinks_IsUnchanged()
    {
        Directory.CreateDirectory(P("plain", "sub"));
        Assert.Equal(Windows(P("plain", "sub")), Posix(P("plain", "sub")));
        Assert.Equal(Windows(P("plain", "missing", "f")), Posix(P("plain", "missing", "f")));
    }
}
