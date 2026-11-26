using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Code;

/// <summary>
/// The canonicaliser's POSIX walk, where a ".." after a link goes to the link's real parent (as Linux
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
    private void MakeTheLinkedLayout()
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
        MakeTheLinkedLayout();
        var deepY = Posix(P("deep", "y"));
        Assert.NotNull(deepY);
        Assert.Equal(deepY, Posix(P("a", "l")));
        Assert.Equal(Path.Combine(deepY!, "k.txt"), Posix(P("a", "l", "k.txt"))); // a missing tail under it
    }

    [Fact]
    public void TheWindowsRule_StillCollapsesDotDotFirst()
    {
        MakeTheLinkedLayout();
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

    [Fact]
    public void AnAbsoluteTargetWithDotDotThroughALink_GoesToTheRealParent()
    {
        // a/l -> <tmp>/a/s/../y (absolute), a/s -> deep/dir: the ".." is taken after s is followed, so deep/y.
        Directory.CreateDirectory(P("deep", "dir"));
        Directory.CreateDirectory(P("deep", "y"));
        Directory.CreateDirectory(P("a"));
        DirLink(P("a", "s"), P("deep", "dir"));
        DirLink(P("a", "l"), P("a", "s") + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "y");
        var stored = new DirectoryInfo(P("a", "l")).LinkTarget ?? "";
        Assert.SkipUnless(stored.Contains(".."), $"the OS stored the absolute target collapsed: {stored}");
        Assert.Equal(Posix(P("deep", "y")), Posix(P("a", "l")));
    }

    [Fact]
    public void ALinkToALink_ThenDotDot_GoesToTheLastRealParent()
    {
        // a/m -> a/s -> deep/dir; a/m/../secret is deep/secret, not a/secret.
        Directory.CreateDirectory(P("deep", "dir"));
        Directory.CreateDirectory(P("deep", "secret"));
        Directory.CreateDirectory(P("a", "secret"));
        DirLink(P("a", "s"), P("deep", "dir"));
        DirLink(P("a", "m"), "s");
        var raw = P("a", "m") + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "secret";
        Assert.Equal(Posix(P("deep", "secret")), Posix(raw));
        Assert.NotEqual(Posix(P("a", "secret")), Posix(raw));
    }

    [Fact]
    public void ADeniedFolderReachedThroughDotDotAfterALink_CanonicalisesToTheDeniedFolder()
    {
        // The deny list holds deep/secret canonicalised; a query for a/s/../secret/key must compare equal under it.
        Directory.CreateDirectory(P("deep", "dir"));
        Directory.CreateDirectory(P("deep", "secret"));
        File.WriteAllText(P("deep", "secret", "key"), "x");
        Directory.CreateDirectory(P("a"));
        DirLink(P("a", "s"), P("deep", "dir"));
        var denied = Posix(P("deep", "secret"))!;
        var q = Posix(P("a", "s") + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + Path.Combine("secret", "key"));
        Assert.Equal(Path.Combine(denied, "key"), q);
    }
}
