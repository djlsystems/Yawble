using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Code;

/// <summary>
/// B031 finding 1a re-verify (card 148), tester's extra layouts for the POSIX ".." walk, driven on every OS through
/// the internal physicalDotDot switch. Skips where links cannot be made.
/// </summary>
public sealed class PosixDotDotWalkBreakTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("yawble-pddb-").FullName;

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
