using Yawble.CodeBrowser;
using Yawble.Core.Code;
using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Code;

/// <summary>
/// The code browser's deny list holds through links: dangling links, a missing tail under a linked parent on either
/// side, a file link, a loop on either side, a canonicaliser that returns null or throws, and the whole code browser
/// (SafeTree) on a clone reached through a linked parent. Every failure denies. Skips where links cannot be made.
/// </summary>
public sealed class DeniedPathsLinkTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "yawble-dpb-" + Guid.NewGuid().ToString("N")[..10]);

    public DeniedPathsLinkTests() => Directory.CreateDirectory(_tmp);

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

    private static void FileLink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Assert.Skip("cannot make a symlink here"); }
    }

    [Fact]
    public void ADanglingLinkInTheDenyList_StillDeniesWhereItWillLead_AndItself()
    {
        // The deny entry is a link whose target does not exist yet; the target is made later.
        DirLink(P("future-link"), P("future"));
        var denied = new DeniedPaths(() => [P("future-link")]);
        Assert.True(denied.IsDenied(P("future-link", "a.txt")));
        Assert.True(denied.IsDenied(P("future", "a.txt")));
        Directory.CreateDirectory(P("future"));
        Assert.True(denied.IsDenied(P("future", "a.txt")));
        Assert.False(denied.IsDenied(P("other", "a.txt")));
    }

    [Fact]
    public void AMissingTailUnderALinkedParent_OnTheDenySide_DeniesTheRealPath()
    {
        Directory.CreateDirectory(P("real"));
        DirLink(P("link"), P("real"));
        // Neither "secret" nor anything under it exists, on either side.
        var denied = new DeniedPaths(() => [P("link", "secret", "deep")]);
        Assert.True(denied.IsDenied(P("real", "secret", "deep", "k.txt")));
        Assert.True(denied.IsDenied(P("link", "secret", "deep", "k.txt")));
        Assert.False(denied.IsDenied(P("real", "secret", "shallow.txt")));
    }

    [Fact]
    public void AQueryThroughADanglingLink_IsDeniedByTheRealEntry()
    {
        // query: dangling -> real/secret (not made yet); entry: real/secret
        Directory.CreateDirectory(P("real"));
        DirLink(P("dangling"), P("real", "secret"));
        var denied = new DeniedPaths(() => [P("real", "secret")]);
        Assert.True(denied.IsDenied(P("dangling", "x", "y.txt")));
        Assert.True(denied.IsDenied(P("dangling")));
    }

    [Fact]
    public void ALinkToALink_ToTheDeniedFolder_IsDenied_BothWays()
    {
        Directory.CreateDirectory(P("real", "secret"));
        DirLink(P("l1"), P("real"));
        DirLink(P("l2"), P("l1", "secret"));
        Assert.True(new DeniedPaths(() => [P("real", "secret")]).IsDenied(P("l2", "k.txt")));
        Assert.True(new DeniedPaths(() => [P("l2")]).IsDenied(P("real", "secret", "k.txt")));
    }

    [Fact]
    public void AFileLinkToADeniedFile_IsDenied_AndALinkInTheListDeniesItsTarget()
    {
        Directory.CreateDirectory(P("real"));
        File.WriteAllText(P("real", "id_key"), "k");
        FileLink(P("innocent.txt"), P("real", "id_key"));
        Assert.True(new DeniedPaths(() => [P("real", "id_key")]).IsDenied(P("innocent.txt")));
        Assert.True(new DeniedPaths(() => [P("innocent.txt")]).IsDenied(P("real", "id_key")));
    }

    [Fact]
    public void ALoopInTheDenyList_StillDeniesTheEntryAsWritten()
    {
        DirLink(P("loop-a"), P("loop-b"));
        DirLink(P("loop-b"), P("loop-a"));
        Assert.Null(HostPathCanonicalizer.TryCanonicalize(P("loop-a")));
        var denied = new DeniedPaths(() => [P("loop-a")]);
        // The query through the loop cannot be canonicalised: denied. A plain path elsewhere is allowed.
        Assert.True(denied.IsDenied(P("loop-a", "x")));
        Assert.True(denied.IsDenied(P("loop-b", "x")));
        Directory.CreateDirectory(P("plain"));
        Assert.False(denied.IsDenied(P("plain", "x")));
    }

    [Fact]
    public void ASelfLink_IsALoop_AndDenies()
    {
        DirLink(P("self"), P("self"));
        Assert.Null(HostPathCanonicalizer.TryCanonicalize(P("self", "a")));
        Assert.True(new DeniedPaths(() => []).IsDenied(P("self", "a")));
    }

    [Fact]
    public void ACanonicaliserThatThrows_Denies_AndOneThatReturnsNullForEveryEntry_StillDeniesTheEntriesAsWritten()
    {
        var secret = P("secret");
        var throwing = new DeniedPaths(() => [secret], canonicalize: _ => throw new InvalidOperationException("boom"));
        Assert.True(throwing.IsDenied(P("open", "a.txt")));
        // Null for the list's entries only: they still deny as written; null for a query: denied.
        var nullForList = new DeniedPaths(() => [secret], canonicalize: p => p.StartsWith(secret, StringComparison.OrdinalIgnoreCase) ? null : p);
        Assert.True(nullForList.IsDenied(P("secret", "k.txt")));
        Assert.False(nullForList.IsDenied(P("open", "a.txt")));
        var alwaysNull = new DeniedPaths(() => [], canonicalize: _ => null);
        Assert.True(alwaysNull.IsDenied(P("open", "a.txt")));
    }

    [Fact]
    public void EmptyNulAndRelativeQueries_AreDenied()
    {
        var denied = new DeniedPaths(() => []);
        Assert.True(denied.IsDenied(""));
        Assert.True(denied.IsDenied("   "));
        Assert.True(denied.IsDenied(P("a") + "\0b"));
        Assert.True(denied.IsDenied("relative"));
    }

    [Fact]
    public void OnWindows_CaseAndSlashDifferences_DoNotSlipPast()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows path comparison");
        Directory.CreateDirectory(P("Real", "Secret"));
        DirLink(P("link"), P("Real"));
        var denied = new DeniedPaths(() => [P("Real", "Secret")]);
        Assert.True(denied.IsDenied(P("real", "SECRET", "k.txt")));
        Assert.True(denied.IsDenied(P("LINK", "secret", "k.txt")));
        Assert.True(denied.IsDenied(P("link").Replace('\\', '/') + "/secret/k.txt"));
    }

    [Fact]
    public void TheCodeBrowser_OnACloneUnderALinkedParent_HidesAndRefusesAFileDeniedByItsRealPath()
    {
        // The macOS case end to end: the clone is reached as link/clone (as /var/... is /private/var/...), the deny list
        // names the real path. Listing marks it hidden, opening and resolving refuse it, the walk leaves it out.
        var real = P("real");
        Directory.CreateDirectory(Path.Combine(real, "clone", "src"));
        File.WriteAllText(Path.Combine(real, "clone", "src", "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(real, "clone", "src", "token.txt"), "t");
        DirLink(P("link"), real);
        var denied = new DeniedPaths(() => [Path.Combine(real, "clone", "src", "token.txt")]);
        var tree = new SafeTree(P("link", "clone"), denied.IsDenied);

        var listed = tree.List("src");
        Assert.True(listed.Single(e => e.Name == "token.txt").Hidden);
        Assert.False(listed.Single(e => e.Name == "a.cs").Hidden);
        Assert.Throws<CodeBrowserException>(() => tree.OpenFile("src/token.txt"));
        var (stream, _) = tree.OpenFile("src/a.cs");
        stream.Dispose();
        Assert.DoesNotContain(tree.WalkFiles(), f => f.Relative == "src/token.txt");
        Assert.True(tree.IsHidden("src/token.txt"));
    }

    [Fact]
    public void TheCodeBrowser_WithTheDenyEntryThroughALink_HidesTheFileInTheRealClone()
    {
        var real = P("real");
        Directory.CreateDirectory(Path.Combine(real, "clone", "src"));
        File.WriteAllText(Path.Combine(real, "clone", "src", "token.txt"), "t");
        DirLink(P("link"), real);
        var denied = new DeniedPaths(() => [P("link", "clone", "src")]);
        var tree = new SafeTree(Path.Combine(real, "clone"), denied.IsDenied);
        Assert.True(tree.List("").Single(e => e.Name == "src").Hidden);
        Assert.Throws<CodeBrowserException>(() => tree.List("src"));
        Assert.Throws<CodeBrowserException>(() => tree.OpenFile("src/token.txt"));
    }

    [Fact]
    public void OnPosix_ALinkWhoseRelativeTargetHasDotDotAfterALink_IsResolvedAsTheOsDoes()
    {
        // POSIX resolves "s/../y" by following s first, then "..": l -> s/../y with s -> deep/dir is deep/y.
        // Windows resolves ".." in a link target lexically, so this is only checked on POSIX.
        if (OperatingSystem.IsWindows()) Assert.Skip("POSIX link semantics");
        Directory.CreateDirectory(P("deep", "dir"));
        Directory.CreateDirectory(P("deep", "y"));
        Directory.CreateDirectory(P("a"));
        DirLink(P("a", "s"), P("deep", "dir"));
        DirLink(P("a", "l"), Path.Combine("s", "..", "y"));
        Assert.True(Directory.Exists(P("a", "l"))); // the OS agrees it leads somewhere
        var canonicalDeepY = HostPathCanonicalizer.TryCanonicalize(P("deep", "y"));
        Assert.Equal(canonicalDeepY, HostPathCanonicalizer.TryCanonicalize(P("a", "l")));
        Assert.True(new DeniedPaths(() => [P("deep", "y")]).IsDenied(P("a", "l", "k.txt")));
    }
}
