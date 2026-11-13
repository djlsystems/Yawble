using Xunit;

namespace Yawble.CodeBrowser.Tests;

/// <summary>Paths stay under the clone: '..', absolute paths, links (symlinks, junctions), .git and the deny list are refused.</summary>
public class SafeTreeTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _clone;
    private readonly string _outside;

    public SafeTreeTests()
    {
        _clone = _tmp.Sub("clone");
        _outside = _tmp.Sub("outside");
        TempDir.Write(_outside, "secret.txt", "outside the clone");
        TempDir.Write(_clone, "src/app.cs", "class A {}\n");
        TempDir.Write(_clone, "README.md", "# hi\n");
        TempDir.Write(_clone, ".git/config", "[core]\n");
        TempDir.Write(_clone, ".git/HEAD", "ref: refs/heads/main\n");
    }

    public void Dispose() => _tmp.Dispose();

    private static string Refused(Action a) => Assert.Throws<CodeBrowserException>(a) is { Code: CodeBrowserException.Refused } e ? e.Message : "wrong code";

    [Theory]
    [InlineData("..")]
    [InlineData("../outside/secret.txt")]
    [InlineData("src/../../outside/secret.txt")]
    [InlineData("src/./app.cs")]
    [InlineData("src//app.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("\\Windows\\win.ini")]
    public void DotDotAndAbsolutePathsAreRefused(string path)
    {
        var tree = new SafeTree(_clone);
        Refused(() => tree.Resolve(path));
    }

    [Fact]
    public void AnAbsolutePathToTheCloneItselfIsRefused()
    {
        var tree = new SafeTree(_clone);
        Refused(() => tree.Resolve(Path.Combine(_clone, "README.md")));
        if (OperatingSystem.IsWindows()) Refused(() => tree.Resolve("C:README.md"));
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".git/config")]
    [InlineData(".GIT/config")]
    [InlineData("src/.git")]
    public void GitIsNotBrowsable(string path)
    {
        var tree = new SafeTree(_clone);
        Refused(() => tree.Resolve(path));
    }

    [Fact]
    public void GitIsNotListed()
    {
        var names = new SafeTree(_clone).List("").Select(e => e.Name).ToList();
        Assert.DoesNotContain(".git", names, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["src", "README.md"], names);
    }

    [Fact]
    public void AShortNameCannotReachGit()
    {
        // On Windows .git may have the 8.3 name GIT~1: segments are matched against real names only.
        var tree = new SafeTree(_clone);
        Refused(() => tree.Resolve("GIT~1/config"));
    }

    [Fact]
    public void AJunctionOutsideIsListedAsALinkAndNotFollowed()
    {
        if (!Links.TryJunction(Path.Combine(_clone, "escape"), _outside)) Assert.Skip("cannot make a junction or symlink here");
        var tree = new SafeTree(_clone);
        var entry = Assert.Single(tree.List(""), e => e.Name == "escape");
        Assert.Equal("link", entry.Kind);
        Assert.Contains("link", Refused(() => tree.Resolve("escape/secret.txt")));
        Assert.Contains("link", Refused(() => tree.List("escape")));
        Assert.Contains("link", Refused(() => tree.OpenFile("escape/secret.txt")));
    }

    [Fact]
    public void AFileSymlinkIsListedAsALinkAndNotOpened()
    {
        if (!Links.TrySymlinkFile(Path.Combine(_clone, "pointer.txt"), Path.Combine(_outside, "secret.txt"))) Assert.Skip("cannot make a file symlink here (needs developer mode on Windows)");
        var tree = new SafeTree(_clone);
        Assert.Equal("link", Assert.Single(tree.List(""), e => e.Name == "pointer.txt").Kind);
        Refused(() => tree.OpenFile("pointer.txt"));
    }

    [Fact]
    public void AJunctionInsideIsALinkToo()
    {
        if (!Links.TryJunction(Path.Combine(_clone, "alias"), Path.Combine(_clone, "src"))) Assert.Skip("cannot make a junction or symlink here");
        var tree = new SafeTree(_clone);
        Refused(() => tree.OpenFile("alias/app.cs"));
        Assert.DoesNotContain(tree.WalkFiles(), f => f.Relative.StartsWith("alias", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("server.pem")]
    [InlineData("tls.KEY")]
    [InlineData("cert.pfx")]
    [InlineData("cert.p12")]
    [InlineData("id_rsa")]
    [InlineData("id_rsa.pub")]
    [InlineData("vault.kdbx")]
    [InlineData("credentials.json")]
    [InlineData("credentials")]
    [InlineData("secrets.yaml")]
    public void TheDenyListHidesLikelySecrets(string name)
    {
        TempDir.Write(_clone, "conf/" + name, "TOKEN=abc");
        var tree = new SafeTree(_clone);
        var entry = Assert.Single(tree.List("conf"));
        Assert.True(entry.Hidden);
        Assert.Contains("secrets", Refused(() => tree.OpenFile("conf/" + name)));
    }

    [Theory]
    [InlineData("environment.ts")]
    [InlineData("keyboard.cs")]
    [InlineData("secretsauce.md")]
    [InlineData("my.keys")]
    public void LookalikesAreNotHidden(string name) => Assert.False(SafeTree.IsDeniedName(name));

    [Fact]
    public void AFileInsideAHiddenFolderIsRefused()
    {
        TempDir.Write(_clone, "credentials/aws.txt", "x");
        var tree = new SafeTree(_clone);
        Refused(() => tree.OpenFile("credentials/aws.txt"));
        Assert.True(tree.IsHidden("credentials/aws.txt"));
    }

    [Fact]
    public void ThePolicysDeniedPathsAreHiddenToo()
    {
        TempDir.Write(_clone, "private/notes.txt", "x");
        var denied = Path.Combine(_clone, "private");
        var tree = new SafeTree(_clone, p => SafeTree.IsUnder(p, denied));
        Assert.True(Assert.Single(tree.List(""), e => e.Name == "private").Hidden);
        Refused(() => tree.OpenFile("private/notes.txt"));
    }

    [Fact]
    public void ListingPutsFoldersFirst()
    {
        TempDir.Write(_clone, "b.txt", "b");
        TempDir.Write(_clone, "a/x.txt", "x");
        var entries = new SafeTree(_clone).List("");
        Assert.Equal(["a", "src", "b.txt", "README.md"], entries.Select(e => e.Name));
        Assert.Equal("src/app.cs", Assert.Single(new SafeTree(_clone).List("src")).Path);
    }

    [Fact]
    public void ANameIsMatchedAgainstTheRealEntry()
    {
        var tree = new SafeTree(_clone);
        if (!OperatingSystem.IsLinux())
            Assert.Equal("src/app.cs", tree.Resolve("SRC/App.cs").Relative); // the real spelling comes back
        Refused(() => tree.Resolve("src/missing.cs"));
    }

    [Fact]
    public void NoCloneIsSaidAsSuch()
    {
        var tree = new SafeTree(Path.Combine(_tmp.Path, "not-there"));
        Assert.Equal(CodeBrowserException.NoClone, Assert.Throws<CodeBrowserException>(() => tree.List("")).Code);
    }

    [Fact]
    public void ReadingNeverWritesToTheClone()
    {
        var before = Snapshot();
        var tree = new SafeTree(_clone);
        tree.List("");
        tree.List("src");
        using (tree.OpenFile("src/app.cs").Stream) { }
        _ = tree.WalkFiles().ToList();
        Assert.Equal(before, Snapshot());
    }

    private string Snapshot() => string.Join('\n', Directory.EnumerateFileSystemEntries(_clone, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir")));
}
