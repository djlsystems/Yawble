using Xunit;

namespace Yawble.CodeBrowser.Tests;

/// <summary>
/// "Changed on branch" from a temp team repository and a bare repository standing in for shared.git. The clone is a
/// plain folder of files (with a booby-trapped .git): git never runs in it, asserted through the recording runner.
/// </summary>
public class BranchViewTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDir _tmp = new();
    private readonly string _team, _shared, _clone;
    private readonly RealGit _git;

    public BranchViewTests()
    {
        _team = _tmp.Sub("team-repo");
        _clone = _tmp.Sub("clone");
        _shared = Path.Combine(_tmp.Path, "shared.git");
        _git = new RealGit(_tmp.Path);
        if (!RealGit.Available) return;
        // C1 on main: the base the member's branch starts from.
        _git.Ok(_tmp.Path, "init", "-q", "-b", "main", _team);
        TempDir.Write(_team, "a.txt", "1\n2\n3\n4\n");
        TempDir.Write(_team, "keep.txt", "same\n");
        TempDir.Write(_team, "crlf.txt", "x\ny\n");
        TempDir.Write(_team, "sub/b.txt", "b\n");
        _git.Ok(_team, "add", "-A");
        _git.Ok(_team, "commit", "-q", "-m", "C1");
        // C2 on members/ada: a.txt line 2 changed.
        _git.Ok(_team, "checkout", "-q", "-b", "members/ada");
        TempDir.Write(_team, "a.txt", "1\nTWO\n3\n4\n");
        _git.Ok(_team, "commit", "-q", "-am", "C2");
        // C3 on main, after the branch started: keep.txt changed there (not the member's change).
        _git.Ok(_team, "checkout", "-q", "main");
        TempDir.Write(_team, "keep.txt", "changed on main later\n");
        _git.Ok(_team, "commit", "-q", "-am", "C3");
        // The shared view: a bare repository with the same branches (a copy of the team repository's .git, made bare,
        // so no transport runs; the sandbox the tests may run in refuses git's local transport for some paths).
        CopyDir(Path.Combine(_team, ".git"), _shared);
        _git.Ok(_shared, "--git-dir=" + _shared, "config", "core.bare", "true");

        // The clone as the agent left it: C2's files, plus live edits.
        TempDir.Write(_clone, "a.txt", "1\nTWO\n3\n4\nfive\n");
        TempDir.Write(_clone, "keep.txt", "same\n");
        TempDir.Write(_clone, "crlf.txt", "x\r\ny\r\n");
        TempDir.Write(_clone, "sub/b.txt", "b\n");
        TempDir.Write(_clone, "new.cs", "n1\nn2\n");
        TempDir.Write(_clone, "node_modules/pkg/index.js", "noise");
        TempDir.Write(_clone, ".env", "SECRET=1");
        // A clone's .git is the agent's: hooks and fsmonitor that would run if git ever ran here.
        TempDir.Write(_clone, ".git/config", "[core]\n\tfsmonitor = echo ran > fsmonitor-ran\n\thooksPath = evil\n");
        TempDir.Write(_clone, ".git/HEAD", "ref: refs/heads/members/ada\n");
    }

    public void Dispose() => _tmp.Dispose();

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private (BranchView View, RecordingGit Git) View(params string[] repos)
    {
        var recording = new RecordingGit(_git);
        return (new BranchView(recording, new SafeTree(_clone), new BranchSource(repos.Length == 0 ? [_team, _shared] : repos, "main", "members/ada")), recording);
    }

    private static void SkipWithoutGit()
    {
        if (!RealGit.Available) Assert.Skip("git is not installed here");
    }

    [Fact]
    public async Task FilesChangedSinceTheBranchStartedAreMarked()
    {
        SkipWithoutGit();
        var (view, _) = View();
        await view.RefreshAsync(Ct);
        Assert.Null(view.Problem);
        Assert.True(view.IsChanged("a.txt", false));
        Assert.True(view.IsChanged("new.cs", false));
        Assert.False(view.IsChanged("keep.txt", false));   // same as the base (main moved on later: not the member's)
        Assert.False(view.IsChanged("crlf.txt", false));   // only its line ends differ (checked out with CRLF)
        Assert.False(view.IsChanged("sub/b.txt", false));
        Assert.False(view.IsChanged("sub", true));
        Assert.True(view.IsChanged("", true));
        Assert.False(view.IsChanged("node_modules/pkg/index.js", false));
        Assert.False(view.IsChanged(".env", false));
        Assert.Equal(["a.txt", "new.cs"], view.ChangedPaths().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ChangedLinesAreAgainstTheBase()
    {
        SkipWithoutGit();
        var (view, _) = View();
        await view.RefreshAsync(Ct);
        Assert.Equal([(2, 2), (5, 5)], await view.LinesAsync("a.txt", ["1", "TWO", "3", "4", "five"], Ct));
        Assert.Equal([(1, 2)], await view.LinesAsync("new.cs", ["n1", "n2"], Ct));
        Assert.Empty(await view.LinesAsync("keep.txt", ["same"], Ct));
    }

    [Fact]
    public async Task TheSharedViewStandsInForTheTeamRepository()
    {
        SkipWithoutGit();
        var (view, git) = View(Path.Combine(_tmp.Path, "no-such-repo"), _shared);
        await view.RefreshAsync(Ct);
        Assert.True(view.IsChanged("a.txt", false));
        Assert.False(view.IsChanged("keep.txt", false));
        Assert.All(git.Calls, c => Assert.Equal(_shared, c.Repository));
    }

    [Fact]
    public async Task WithNoMemberBranchTheBaseTipIsTheStart()
    {
        SkipWithoutGit();
        var recording = new RecordingGit(_git);
        var view = new BranchView(recording, new SafeTree(_clone), new BranchSource([_team], "main", "members/nobody"));
        await view.RefreshAsync(Ct);
        Assert.True(view.IsChanged("keep.txt", false)); // main's tip has the later keep.txt
    }

    [Fact]
    public async Task ATouchedFileIsRechecked()
    {
        SkipWithoutGit();
        var (view, _) = View();
        await view.RefreshAsync(Ct);
        TempDir.Write(_clone, "a.txt", "1\n2\n3\n4\n"); // back to the base
        view.Touch("a.txt");
        Assert.False(view.IsChanged("a.txt", false));
        TempDir.Write(_clone, "keep.txt", "edited\n");
        view.Touch("keep.txt");
        Assert.True(view.IsChanged("keep.txt", false));
    }

    [Fact]
    public async Task GitNeverRunsInTheClone()
    {
        SkipWithoutGit();
        var (view, git) = View();
        await view.RefreshAsync(Ct);
        await view.LinesAsync("a.txt", ["1", "TWO", "3", "4", "five"], Ct);
        Assert.NotEmpty(git.Calls);
        var allowed = new HashSet<string> { "rev-parse", "merge-base", "ls-tree", "cat-file" };
        foreach (var (repo, args) in git.Calls)
        {
            Assert.False(SafeTree.IsUnder(Path.GetFullPath(repo), _clone), $"git ran in the clone: {repo}");
            // The repository's git folder named outright (git never searches), then the hardening, then the command.
            Assert.StartsWith("--git-dir=", args[0], StringComparison.Ordinal);
            Assert.False(SafeTree.IsUnder(Path.GetFullPath(args[0]["--git-dir=".Length..]), _clone));
            Assert.Equal(BranchView.Hardening, args.Skip(1).Take(BranchView.Hardening.Count));
            Assert.Contains("--no-replace-objects", args);
            Assert.Contains("core.fsmonitor=false", args);
            Assert.Contains(args, a => a.StartsWith("core.hooksPath=", StringComparison.Ordinal));
            Assert.Contains(args[1 + BranchView.Hardening.Count], allowed);
            Assert.DoesNotContain(args, a => a.Contains(_clone, StringComparison.OrdinalIgnoreCase));
        }
        Assert.False(File.Exists(Path.Combine(_clone, "fsmonitor-ran")));
    }

    [Fact]
    public async Task ARepositoryInsideTheCloneIsNeverUsed()
    {
        SkipWithoutGit();
        var (view, git) = View(Path.Combine(_clone, ".git"), _clone);
        await view.RefreshAsync(Ct);
        Assert.Empty(git.Calls);
        Assert.NotNull(view.Problem);
    }

    [Fact]
    public void BlobIdsAreGits()
    {
        // git hash-object of "hello\n"
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", BranchView.BlobId("hello\n"u8));
    }
}
