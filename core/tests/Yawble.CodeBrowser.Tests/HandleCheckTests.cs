using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Yawble.CodeBrowser.Tests;

/// <summary>
/// The handle checks driven through the seams (HandlePath, LinkCount, AfterEnumerate) with fake final paths and
/// link counts, plus a check that every refusal closes what it opened. They run on Windows (the Windows handle
/// path) and, through the same seams, on macOS / Linux (the POSIX path).
/// </summary>
public class HandleCheckTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _clone;

    public HandleCheckTests()
    {
        _clone = _tmp.Sub("clone");
        TempDir.Write(_clone, "src/app.cs", "class A {}\n");
        TempDir.Write(_clone, "src/lib/b.cs", "class B {}\n");
    }

    public void Dispose() => _tmp.Dispose();

    private static void HandleChecks()
    {
        if (!OperatingSystem.IsWindows() && !PosixHandles.Supported) Assert.Skip("no handle checks on this OS or CPU");
    }

    private static string Refused(Action a)
    {
        var e = Assert.Throws<CodeBrowserException>(a);
        Assert.Equal(CodeBrowserException.Refused, e.Code);
        return e.Message;
    }

    private static char Sep => Path.DirectorySeparatorChar;

    /// <summary>The real final path of the root, as SafeTree sees it (macOS: /private/var/...).</summary>
    private static string RealRoot(SafeTree tree)
    {
        string? root = null;
        var real = tree.HandlePath;
        tree.HandlePath = h => { var f = real(h); root ??= f; return f; };
        tree.List(""); // the first handle path asked for is the root folder's own
        tree.HandlePath = real;
        return root!;
    }

    // ------------------------------------------------------------------ link counts through the seam

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(uint.MaxValue)]
    public void AnyLinkCountButOneIsRefused(uint count)
    {
        HandleChecks();
        var tree = new SafeTree(_clone) { LinkCount = _ => count };
        Assert.Contains("hard link", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
    }

    [Fact]
    public void AnUnknownLinkCountIsRefusedAndOneIsRead()
    {
        HandleChecks();
        var tree = new SafeTree(_clone) { LinkCount = _ => null };
        Assert.Contains("hard link", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
        tree.LinkCount = _ => 1;
        using var s = tree.OpenFile("src/app.cs").Stream;
        Assert.Equal("class A {}\n", new StreamReader(s).ReadToEnd());
    }

    [Fact]
    public void TheRealLinkCountOfAPlainFileIsOne()
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        uint? seen = null;
        var real = tree.LinkCount;
        tree.LinkCount = h => seen = real(h);
        tree.OpenFile("src/app.cs").Stream.Dispose();
        Assert.Equal(1u, seen);
    }

    // ------------------------------------------------------------------ the file's final path through the seam

    [Fact]
    public void AFileWhoseFinalPathIsTheRootItselfIsRefused()
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var root = RealRoot(tree);
        var real = tree.HandlePath;
        tree.HandlePath = h => real(h) is { } f && f.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase) ? root : real(h);
        Assert.Contains("not inside the clone", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
    }

    [Theory]
    [InlineData("-evil")]
    [InlineData("2")]
    [InlineData(".bak")]
    [InlineData(" ")]
    public void AFileOutsideByAPrefixOfTheRootIsRefused(string suffix)
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var root = RealRoot(tree);
        var real = tree.HandlePath;
        tree.HandlePath = h => real(h) is { } f && f.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase)
            ? root + suffix + Sep + "src" + Sep + "app.cs" : real(h);
        Assert.Contains("not inside the clone", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
    }

    [Fact]
    public void AFileWhoseFinalPathIsTheRootsParentOrNullIsRefused()
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var root = RealRoot(tree);
        var real = tree.HandlePath;
        string? fake = Path.GetDirectoryName(root) + Sep + "app.cs";
        tree.HandlePath = h => real(h) is { } f && f.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase) ? fake : real(h);
        Assert.Contains("not inside the clone", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
        fake = null;
        Assert.Contains("not inside the clone", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
        fake = "";
        Assert.Contains("not inside the clone", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
    }

    [Theory]
    [InlineData(".GiT")]
    [InlineData(".GIT")]
    [InlineData(".git")]
    public void AFileWhoseFinalPathIsInGitInAnyCaseIsRefused(string git)
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var root = RealRoot(tree);
        var real = tree.HandlePath;
        tree.HandlePath = h => real(h) is { } f && f.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase)
            ? root + Sep + "src" + Sep + git + Sep + "config" : real(h);
        Assert.Contains(".git", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
        tree.HandlePath = h => real(h) is { } f && f.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase)
            ? root + Sep + git + Sep + "objects" + Sep + "ab" : real(h);
        Assert.Contains(".git", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
    }

    [Theory]
    [InlineData(".ENV.local")]
    [InlineData("ID_RSA")]
    [InlineData("Credentials.json")]
    [InlineData("deploy.KEY")]
    public void AFileWhoseFinalPathLooksLikeASecretIsRefused(string name)
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var root = RealRoot(tree);
        var real = tree.HandlePath;
        tree.HandlePath = h => real(h) is { } f && f.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase)
            ? root + Sep + "src" + Sep + name : real(h);
        Assert.Contains("secrets", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
    }

    [Fact]
    public void APolicyOnTheFinalPathOnlyIsHonoured()
    {
        HandleChecks();
        // The policy denies only the swapped-in final path, never the path asked for.
        var tree = new SafeTree(_clone, p => p.EndsWith("vault.txt", StringComparison.OrdinalIgnoreCase));
        var root = RealRoot(tree);
        var real = tree.HandlePath;
        tree.HandlePath = h => real(h) is { } f && f.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase)
            ? root + Sep + "src" + Sep + "vault.txt" : real(h);
        Assert.Contains("secrets", Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose()));
    }

    // ------------------------------------------------------------------ folders: final path and identity

    [Theory]
    [InlineData("-evil")]
    [InlineData("")]
    public void AFolderWhoseFinalPathLeadsOutsideIsRefused(string suffix)
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var root = RealRoot(tree);
        var real = tree.HandlePath;
        // "" + parent: the folder's final path is the root's parent; "-evil": a sibling sharing the root as a prefix.
        var fake = suffix.Length == 0 ? Path.GetDirectoryName(root)! : root + suffix + Sep + "src";
        tree.HandlePath = h => real(h) is { } f && f.EndsWith(Sep + "src", StringComparison.OrdinalIgnoreCase) ? fake : real(h);
        Assert.Contains("not inside the clone", Refused(() => tree.List("src")));
    }

    [Theory]
    [InlineData(".GiT")]
    [InlineData(".git")]
    public void AFolderWhoseFinalPathIsInGitIsRefused(string git)
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var root = RealRoot(tree);
        var real = tree.HandlePath;
        tree.HandlePath = h => real(h) is { } f && f.EndsWith(Sep + "src", StringComparison.OrdinalIgnoreCase)
            ? root + Sep + git + Sep + "refs" : real(h);
        Assert.Contains(".git", Refused(() => tree.List("src")));
    }

    [Fact]
    public void AFolderWhoseFinalPathIsUnknownIsRefused()
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var real = tree.HandlePath;
        tree.HandlePath = h => real(h) is { } f && f.EndsWith(Sep + "src", StringComparison.OrdinalIgnoreCase) ? null : real(h);
        Assert.Contains("not inside the clone", Refused(() => tree.List("src")));
    }

    [Fact]
    public void AFolderReplacedByACopyWithTheSameNameAndEntriesIsRefused()
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var src = Path.Combine(_clone, "src");
        tree.AfterEnumerate = _ =>
        {
            Directory.Move(src, src + "-old");
            TempDir.Write(_clone, "src/app.cs", "class A {}\n");
            TempDir.Write(_clone, "src/lib/b.cs", "class B {}\n");
        };
        Assert.Contains("changed while it was listed", Refused(() => tree.List("src")));
    }

    [Fact]
    public void ANestedFolderReplacedWhileListedIsRefused()
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var lib = Path.Combine(_clone, "src", "lib");
        tree.AfterEnumerate = _ =>
        {
            Directory.Move(lib, Path.Combine(_clone, "lib-old"));
            Directory.CreateDirectory(lib);
        };
        Assert.Contains("changed while it was listed", Refused(() => tree.List("src/lib")));
    }

    [Fact]
    public void AFolderDeletedWhileListedIsRefused()
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var lib = Path.Combine(_clone, "src", "lib");
        tree.AfterEnumerate = _ => Directory.Delete(lib, recursive: true);
        Refused(() => tree.List("src/lib"));
    }

    [Fact]
    public void AFolderMovedAwayAndBackWhileListedIsTheSameFolder()
    {
        HandleChecks();
        // Same (volume, id) and same final path after: not a swap, so the listing is shown.
        var tree = new SafeTree(_clone);
        var lib = Path.Combine(_clone, "src", "lib");
        tree.AfterEnumerate = _ =>
        {
            Directory.Move(lib, lib + "-away");
            Directory.Move(lib + "-away", lib);
        };
        Assert.Contains(tree.List("src/lib"), e => e.Name == "b.cs");
    }

    [Fact]
    public void TheRootListingIsChecked()
    {
        HandleChecks();
        var tree = new SafeTree(_clone);
        var real = tree.HandlePath;
        var calls = 0;
        // The first call is FinalRoot; afterwards the root folder's own handle reports a path outside.
        tree.HandlePath = h => ++calls == 1 ? real(h) : Path.GetDirectoryName(real(h)!);
        Refused(() => tree.List(""));
    }

    // ------------------------------------------------------------------ every refusal closes what it opened

    [Fact]
    public void EveryFileRefusalClosesTheFile()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("checked with an exclusive Windows open (FileShare.None)");
        var file = Path.Combine(_clone, "src", "app.cs");
        var setups = new List<Action<SafeTree>>
        {
            t => t.LinkCount = _ => 2,
            t => t.LinkCount = _ => null,
            t => t.HandlePath = _ => null,
            t => { var root = RealRoot(t); var r = t.HandlePath; t.HandlePath = h => r(h) is { } f && f.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase) ? root + "-evil" + Sep + "app.cs" : r(h); },
            t => { var root = RealRoot(t); var r = t.HandlePath; t.HandlePath = h => r(h) is { } f && f.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase) ? root + Sep + ".git" + Sep + "config" : r(h); },
            t => { var root = RealRoot(t); var r = t.HandlePath; t.HandlePath = h => r(h) is { } f && f.EndsWith("app.cs", StringComparison.OrdinalIgnoreCase) ? root + Sep + ".env" : r(h); },
        };
        foreach (var setup in setups)
        {
            var tree = new SafeTree(_clone);
            setup(tree);
            Refused(() => tree.OpenFile("src/app.cs").Stream.Dispose());
            // Only succeeds when no handle to the file is still open.
            using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    [Fact]
    public void EveryFolderRefusalClosesTheFolder()
    {
        // Folder handles are opened with no access rights, so an exclusive open does not see them: the process's handle
        // count is watched instead, over many refusals (a leak of one handle per refusal shows as hundreds).
        if (!OperatingSystem.IsWindows()) Assert.Skip("checked with the Windows process handle count");
        var setups = new List<Action<SafeTree>>
        {
            t => { var r = t.HandlePath; t.HandlePath = h => r(h) is { } f && f.EndsWith(Sep + "lib", StringComparison.OrdinalIgnoreCase) ? null : r(h); },
            t => { var root = RealRoot(t); var r = t.HandlePath; t.HandlePath = h => r(h) is { } f && f.EndsWith(Sep + "lib", StringComparison.OrdinalIgnoreCase) ? root + "-evil" : r(h); },
            t => { var root = RealRoot(t); var r = t.HandlePath; t.HandlePath = h => r(h) is { } f && f.EndsWith(Sep + "lib", StringComparison.OrdinalIgnoreCase) ? root + Sep + ".git" : r(h); },
        };
        const int Rounds = 300;
        foreach (var setup in setups)
        {
            var tree = new SafeTree(_clone);
            setup(tree);
            Refused(() => tree.List("src/lib")); // warm up
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var before = System.Diagnostics.Process.GetCurrentProcess().HandleCount;
            for (var i = 0; i < Rounds; i++) Refused(() => tree.List("src/lib"));
            var after = System.Diagnostics.Process.GetCurrentProcess().HandleCount;
            // No GC between: a handle left for the finalizer would still be counted here.
            Assert.True(after - before < Rounds / 2, $"handle count grew by {after - before} over {Rounds} refused listings");
        }
    }

    [Fact]
    public void AReadFileIsClosedWithItsStream()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("checked with an exclusive Windows open (FileShare.None)");
        var tree = new SafeTree(_clone);
        for (var i = 0; i < 50; i++) tree.OpenFile("src/app.cs").Stream.Dispose();
        tree.List("src");
        using var exclusive = new FileStream(Path.Combine(_clone, "src", "app.cs"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    // ------------------------------------------------------------------ PosixHandles off its platforms

    [Fact]
    public void PosixHandlesSaysNothingOnWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only: PosixHandles must be inert here");
        Assert.False(PosixHandles.Supported);
        using var s = File.OpenRead(Path.Combine(_clone, "src", "app.cs"));
        Assert.Null(PosixHandles.FinalPath(s.SafeFileHandle));
        Assert.Null(PosixHandles.FStat(s.SafeFileHandle));
    }

    [Fact]
    public void PosixHandlesOnAClosedOrInvalidHandleIsNull()
    {
        var closed = File.OpenRead(Path.Combine(_clone, "src", "app.cs"));
        var h = closed.SafeFileHandle;
        closed.Dispose();
        Assert.Null(PosixHandles.FinalPath(h));
        Assert.Null(PosixHandles.FStat(h));
        using var invalid = new SafeFileHandle(-1, ownsHandle: false);
        Assert.Null(PosixHandles.FinalPath(invalid));
        Assert.Null(PosixHandles.FStat(invalid));
    }

    [Fact]
    public void OnPosixTheRealStatOfAFileAndAFolderIsRight()
    {
        if (!PosixHandles.Supported) Assert.Skip("POSIX only (macOS / Linux x64, arm64): not run on Windows");
        using var s = File.OpenRead(Path.Combine(_clone, "src", "app.cs"));
        var st = PosixHandles.FStat(s.SafeFileHandle);
        Assert.NotNull(st);
        Assert.True(st.Value.IsRegular);
        Assert.False(st.Value.IsDirectory);
        Assert.Equal(1UL, st.Value.Links);
        Assert.NotEqual(0UL, st.Value.Inode);
        Assert.EndsWith(Path.Combine("src", "app.cs"), PosixHandles.FinalPath(s.SafeFileHandle));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            using var d = PosixHandles.OpenFolderNoFollow(Path.Combine(_clone, "src"));
            Assert.False(d.IsInvalid);
            Assert.True(PosixHandles.FStat(d) is { IsDirectory: true });
            Assert.EndsWith(Sep + "src", PosixHandles.FinalPath(d));
            // A file is not opened as a folder.
            using var notDir = PosixHandles.OpenFolderNoFollow(Path.Combine(_clone, "src", "app.cs"));
            Assert.True(notDir.IsInvalid);
            // A symlink as the last name is not opened (ELOOP).
            var link = Path.Combine(_clone, "src", "to-app.cs");
            File.CreateSymbolicLink(link, Path.Combine(_clone, "src", "app.cs"));
            IOException? e = null;
            try { PosixHandles.OpenFileNoFollow(link).Dispose(); }
            catch (IOException x) { e = x; }
            Assert.NotNull(e);
            Assert.Contains("link", e.Message);
        }
    }
}
