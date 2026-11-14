using Yawble.Core.Policy;
using Yawble.Core.Teams;
using Xunit;
using PolicyMember = Yawble.Core.Policy.MemberRef;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The team repository and per-member clones: layout, no remote, no git credential, no shared inodes,
/// fetch-back into the team repository, the post-run git check, and the host never running git inside a used clone.
/// </summary>
public class TeamReposTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCloneIsTheRunLayoutsImplicitGrant_OnItsOwnBranch_WithNoRemoteAndNoCredentialHelper()
    {
        using var f = new TeamsFixture();
        var (_, ada, ben) = f.Team();
        var checkout = await f.Repos.PrepareAsync(ada, Ct);

        // Exactly the folders RunLayout grants read/write and uses as cwd.
        var layout = RunLayout.Standard(TargetOsExtensions.Host, Path.GetTempPath(), f.AppRoot, Path.Combine(f.Db.Dir, "install"),
            new PolicyMember("Alpha", "Ada"), AgentCli.Claude, Agents.TestClis.InstallFor(f.Db.Dir, Path.GetTempPath(), AgentCli.Claude), [new PolicyMember("Alpha", "Ben")]);
        Assert.Equal(layout.ClonePath, checkout.ClonePath);
        Assert.Equal(layout.Workspace, checkout.Workspace);
        Assert.Equal(layout.Tmp, checkout.Tmp);
        Assert.All(layout.ImplicitReadwrite, p => Assert.True(Directory.Exists(p), p));
        // The team repository is not under any member's grant.
        var repo = f.Repos.RepoOf(ada.TeamId)!;
        Assert.True(repo.Managed);
        Assert.Equal(f.Repos.DefaultRepoPath("Alpha"), repo.Path);
        Assert.DoesNotContain(layout.ImplicitReadwrite, p => repo.Path.StartsWith(p, StringComparison.Ordinal));

        Assert.Equal("members/alpha/ada", checkout.Branch);
        Assert.Equal("members/alpha/ada", AgentGit.Ok(checkout, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal("", AgentGit.Ok(checkout, "remote"));
        Assert.Equal("members/alpha/ada", AgentGit.Ok(checkout, "for-each-ref", "--format=%(refname:short)"));
        var config = File.ReadAllText(Path.Combine(checkout.ClonePath, ".git", "config"));
        Assert.DoesNotContain("[remote", config);
        Assert.DoesNotContain("url =", config);
        // The empty helper resets any system helper, so none applies inside the sandbox.
        Assert.Equal("", AgentGit.Run(checkout, "config", "--get", "credential.helper").Stdout);
        Assert.Equal("Ada (Alpha)", AgentGit.Ok(checkout, "config", "user.name"));
        // A push has nowhere to go.
        var push = AgentGit.Run(checkout, "push");
        Assert.NotEqual(0, push.ExitCode);
        Assert.Contains("No configured push destination", push.Stderr);

        // No hard links into the team repository: the sandbox may write its clone. Every OS: stat(1) on Linux and
        // macOS, the file's link count from Windows itself (TestFiles.LinkCount). The clone has objects to check.
        var objects = Directory.EnumerateFiles(Path.Combine(checkout.ClonePath, ".git", "objects"), "*", SearchOption.AllDirectories)
            .Where(o => !o.Contains(Path.DirectorySeparatorChar + "info" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(objects);
        Assert.All(objects, obj => Assert.True(TestFiles.LinkCount(obj) == 1, $"{obj} is hard-linked"));
        // The member folder is the owner's only. POSIX modes only: on Windows the folder inherits the user-only ACL
        // of the per-user app data folder it sits in, and the app sets no ACL of its own (TeamRepos.CreatePrivate).
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(checkout.ClonePath)!) & (UnixFileMode)0x1FF);
        }

        var benCheckout = await f.Repos.PrepareAsync(ben, Ct);
        Assert.Equal("members/alpha/ben", benCheckout.Branch);
        Assert.NotEqual(checkout.ClonePath, benCheckout.ClonePath);
    }

    [Fact]
    public async Task FetchBack_BringsTheMembersBranchIntoTheTeamRepo_AndANewCloneResumesIt()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        var team = f.Repos.RepoOf(ada.TeamId)!.Path;
        // Before any work the branch is the team's base commit: nothing to bring back, no branch made.
        Assert.Equal(FetchResult.Nothing, await f.Repos.FetchBackAsync(ada, Ct));
        Assert.Equal("main", AgentGit.Host(team, "for-each-ref", "--format=%(refname:short)", "refs/heads/"));

        File.WriteAllText(Path.Combine(checkout.ClonePath, "hello.txt"), "hello\n");
        AgentGit.Ok(checkout, "add", "hello.txt");
        AgentGit.Ok(checkout, "commit", "-q", "-m", "add hello");
        var sha = AgentGit.Ok(checkout, "rev-parse", "HEAD");

        var fetched = await f.Repos.FetchBackAsync(ada, Ct);
        Assert.True(fetched.Fetched, fetched.Error);
        Assert.Equal(sha, fetched.Sha);
        Assert.Equal(sha, AgentGit.Host(team, "rev-parse", "refs/heads/members/alpha/ada"));
        Assert.Equal("hello", AgentGit.Host(team, "show", "members/alpha/ada:hello.txt"));
        Assert.Equal(sha, f.Repos.Row(ada.Id)!.FetchedSha);
        // main is untouched: merging is the person's.
        Assert.Equal("1", AgentGit.Host(team, "rev-list", "--count", "main"));

        // The clone is lost; the next one starts from the member's branch in the team repo, not from main.
        TestFiles.DeleteTree(checkout.ClonePath); // git's object files are read-only: a plain delete fails on Windows
        var again = await f.Repos.PrepareAsync(ada, Ct);
        Assert.Equal(sha, AgentGit.Ok(again, "rev-parse", "HEAD"));
        Assert.Equal("", AgentGit.Ok(again, "remote"));
    }

    [Fact]
    public async Task AFolderThePersonPicked_KeepsItsRemote_ButTheCloneHasNone_AndNothingIsPushedToIt()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, bare) = PersonsRepoWithOrigin(f);
        var before = AgentGit.Host(bare, "for-each-ref");

        var repo = await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        Assert.False(repo.Managed);
        Assert.Equal("trunk", repo.BaseBranch);
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        Assert.Equal("", AgentGit.Ok(checkout, "remote"));
        Assert.True(File.Exists(Path.Combine(checkout.ClonePath, "README.md")));
        File.WriteAllText(Path.Combine(checkout.ClonePath, "x.txt"), "x");
        AgentGit.Ok(checkout, "add", "x.txt");
        AgentGit.Ok(checkout, "commit", "-q", "-m", "x");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);

        Assert.Contains("members/alpha/ada", AgentGit.Host(picked, "branch", "--list"));
        Assert.Equal(before, AgentGit.Host(bare, "for-each-ref"));
        // A folder inside the app's own tree is refused.
        await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", Path.Combine(f.AppRoot, "teams", "Alpha", "members"), Ct));
    }

    [Fact]
    public async Task TheFolderPickedAtTeamCreation_IsTheTeamRepository()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, bare) = PersonsRepoWithOrigin(f);
        var before = AgentGit.Host(bare, "for-each-ref");
        using (var store = f.Db.OpenStore()) store.SetTeamRepo(ada.TeamId, picked); // the team dialog

        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        var repo = f.Repos.RepoOf(ada.TeamId)!;
        Assert.False(repo.Managed);
        Assert.Equal(Path.GetFullPath(picked), repo.Path);
        Assert.Equal("trunk", repo.BaseBranch);
        Assert.True(File.Exists(Path.Combine(checkout.ClonePath, "README.md")));
        Assert.Equal("", AgentGit.Ok(checkout, "remote"));
        Assert.False(Directory.Exists(f.Repos.DefaultRepoPath("Alpha")), "a managed repository was made as well");
        Assert.Equal(before, AgentGit.Host(bare, "for-each-ref"));
    }

    [Fact]
    public async Task APickedFolderThatIsNotARepository_FailsWithTheReason_AndIsLeftAlone()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var plain = Path.Combine(f.Db.Dir, "not-a-repo");
        Directory.CreateDirectory(plain);
        using (var store = f.Db.OpenStore()) store.SetTeamRepo(ada.TeamId, plain);

        var e = await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.PrepareAsync(ada, Ct));
        Assert.Contains("is not a git repository", e.Message);
        Assert.Null(f.Repos.RepoOf(ada.TeamId));
        Assert.Empty(Directory.EnumerateFileSystemEntries(plain)); // no git init in the person's folder
        Assert.False(Directory.Exists(f.Repos.DefaultRepoPath("Alpha")));
    }

    /// <summary>The person's own repository on branch trunk, with an "origin" (a local bare repo) standing in for GitHub.</summary>
    internal static (string Picked, string Bare) PersonsRepoWithOrigin(TeamsFixture f) => PersonsRepoWithOrigin(f.Db.Dir);

    internal static (string Picked, string Bare) PersonsRepoWithOrigin(string dir)
    {
        var picked = Path.Combine(dir, "persons-repo");
        var bare = Path.Combine(dir, "persons-origin.git");
        Directory.CreateDirectory(picked);
        AgentGit.Host(dir, "init", "-q", "--bare", bare);
        AgentGit.Host(picked, "init", "-q", "-b", "trunk");
        File.WriteAllText(Path.Combine(picked, "README.md"), "# service\n");
        AgentGit.Host(picked, "add", "README.md");
        AgentGit.Host(picked, "-c", "user.name=Person", "-c", "user.email=p@example.invalid", "commit", "-q", "-m", "initial");
        AgentGit.Host(picked, "remote", "add", "origin", bare);
        AgentGit.Host(picked, "push", "-q", "origin", "trunk");
        return (picked, bare);
    }

    [Fact]
    public async Task ThePostRunCheck_RemovesRemotesCredentialHelpersAndCredentialFiles_AndRecordsThem()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        Assert.Empty(await f.Repos.CheckAndScrubAsync(ada, Ct)); // a fresh clone is clean

        // What an agent could do in its own clone and HOME (with fake values only).
        AgentGit.Ok(checkout, "remote", "add", "origin", "https://FAKE-user:FAKE-token@github.com/acme/app.git");
        AgentGit.Ok(checkout, "config", "credential.helper", "store");
        AgentGit.Ok(checkout, "config", "url.https://FAKE@github.com/.insteadOf", "gh:");
        AgentGit.Ok(checkout, "config", "http.extraHeader", "Authorization: Bearer FAKE");
        File.WriteAllText(Path.Combine(checkout.Workspace, ".git-credentials"), "https://FAKE-user:FAKE-token@github.com\n");
        File.WriteAllText(Path.Combine(checkout.Workspace, ".netrc"), "machine github.com login FAKE password FAKE\n");
        File.WriteAllText(Path.Combine(checkout.Workspace, ".gitconfig"), "[user]\n\tname = A\n[credential]\n\thelper = store\n");
        File.WriteAllText(Path.Combine(checkout.ClonePath, ".git", "objects", "info", "alternates"), "/elsewhere/objects\n");

        var findings = await f.Repos.CheckAndScrubAsync(ada, Ct);
        var text = string.Join("\n", findings.Select(x => $"{x.Where}: {x.What}"));
        Assert.Contains("clone .git/config: [remote.origin]", text);
        Assert.Contains("clone .git/config: [credential] helper", text);
        Assert.Contains("[url.https://FAKE@github.com/]", text);
        Assert.Contains("[http] extraHeader", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workspace .git-credentials", text);
        Assert.Contains("workspace .netrc", text);
        Assert.Contains("workspace .gitconfig: [credential]", text);
        Assert.Contains("alternates", text);
        Assert.DoesNotContain("FAKE-token", text); // findings name sections and keys, never values

        Assert.Equal("", AgentGit.Ok(checkout, "remote"));
        Assert.Equal("", AgentGit.Run(checkout, "config", "--get", "credential.helper").Stdout);
        var config = File.ReadAllText(Path.Combine(checkout.ClonePath, ".git", "config"));
        Assert.DoesNotContain("FAKE", config);
        Assert.False(File.Exists(Path.Combine(checkout.Workspace, ".git-credentials")));
        Assert.False(File.Exists(Path.Combine(checkout.Workspace, ".netrc")));
        Assert.DoesNotContain("credential", File.ReadAllText(Path.Combine(checkout.Workspace, ".gitconfig")));
        Assert.Contains(".git-credentials", f.Repos.Row(ada.Id)!.LastCheck);
        Assert.Empty(await f.Repos.CheckAndScrubAsync(ada, Ct)); // and it stays clean
    }

    [Fact]
    public async Task TheHostNeverRunsGitInsideAUsedClone_SoItsHooksAndFsmonitorNeverRun()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        File.WriteAllText(Path.Combine(checkout.ClonePath, "a.txt"), "a");
        AgentGit.Ok(checkout, "add", "a.txt");
        AgentGit.Ok(checkout, "commit", "-q", "-m", "a");

        // An agent plants code that runs if the HOST ever runs git in this clone.
        var marker = Path.Combine(f.Db.Dir, "host-ran-agent-code");
        // Git for Windows runs hooks and fsmonitor scripts with its own sh, which takes C:/... paths.
        var markerForSh = marker.Replace('\\', '/');
        var script = Path.Combine(checkout.ClonePath, ".git", "evil.sh");
        File.WriteAllText(script, $"#!/bin/sh\ntouch '{markerForSh}'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, (UnixFileMode)0x1C0);
        AgentGit.Ok(checkout, "config", "core.fsmonitor", script);
        AgentGit.Ok(checkout, "config", "core.hooksPath", Path.Combine(checkout.ClonePath, ".git", "evilhooks"));
        AgentGit.Ok(checkout, "config", "uploadpack.packObjectsHook", script);
        Directory.CreateDirectory(Path.Combine(checkout.ClonePath, ".git", "evilhooks"));
        foreach (var hook in new[] { "post-checkout", "reference-transaction", "pre-push", "post-merge", "pre-commit" })
        {
            var h = Path.Combine(checkout.ClonePath, ".git", "hooks", hook);
            File.WriteAllText(h, $"#!/bin/sh\ntouch '{markerForSh}'\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(h, (UnixFileMode)0x1C0);
            File.Copy(h, Path.Combine(checkout.ClonePath, ".git", "evilhooks", hook));
        }

        await f.Repos.PrepareAsync(ada, Ct);
        await f.Repos.CheckAndScrubAsync(ada, Ct);
        var fetched = await f.Repos.FetchBackAsync(ada, Ct);
        await f.Repos.FetchBackAllAsync([ada], Ct);

        Assert.True(fetched.Fetched, fetched.Error);
        Assert.False(File.Exists(marker), "the host ran code the agent planted in its clone");
    }

    [Fact]
    public async Task AGitDirThatPointsElsewhere_IsRefusedAtFetchBack()
    {
        using var f = new TeamsFixture();
        var (_, ada, ben) = f.Team();
        var adaCheckout = await f.Repos.PrepareAsync(ada, Ct);
        await f.Repos.PrepareAsync(ben, Ct);
        var gitDir = Path.Combine(adaCheckout.ClonePath, ".git");
        TestFiles.DeleteTree(gitDir);
        File.WriteAllText(gitDir, "gitdir: " + Path.Combine(f.Repos.MemberRoot("Alpha", "Ben"), "repo", ".git"));

        var result = await f.Repos.FetchBackAsync(ada, Ct);
        Assert.False(result.Fetched);
        Assert.Contains("no .git folder", result.Error);
        Assert.Equal(result.Error, f.Repos.Row(ada.Id)!.LastError);
    }

    [Fact]
    public async Task AGitdirFileInsideTheClonesDotGit_OrAlternates_AreRefusedAtFetchBack_AndNothingElseIsRead()
    {
        using var f = new TeamsFixture();
        var (_, ada, ben) = f.Team();
        var adaCheckout = await f.Repos.PrepareAsync(ada, Ct);
        var benCheckout = await f.Repos.PrepareAsync(ben, Ct);
        var team = f.Repos.RepoOf(ada.TeamId)!.Path;
        // Ben's clone stands in for any other repository on this computer: it has a branch Ada must not get read.
        AgentGit.Ok(benCheckout, "checkout", "-q", "-b", "members/alpha/ada");
        File.WriteAllText(Path.Combine(benCheckout.ClonePath, "ben.txt"), "ben\n");
        AgentGit.Ok(benCheckout, "add", "ben.txt");
        AgentGit.Ok(benCheckout, "commit", "-q", "-m", "not ada's");
        var foreign = AgentGit.Ok(benCheckout, "rev-parse", "HEAD");
        // upload-pack, given <clone>/.git, would first try <clone>/.git/.git: a gitfile there points anywhere.
        var redirect = Path.Combine(adaCheckout.ClonePath, ".git", ".git");
        File.WriteAllText(redirect, "gitdir: " + Path.Combine(benCheckout.ClonePath, ".git"));

        var result = await f.Repos.FetchBackAsync(ada, Ct);
        Assert.False(result.Fetched);
        Assert.Contains(".git/.git", result.Error);
        Assert.NotEqual(foreign, AgentGit.Host(team, "for-each-ref", "--format=%(objectname)", "refs/heads/members/alpha/ada"));

        File.Delete(redirect);
        File.WriteAllText(Path.Combine(adaCheckout.ClonePath, ".git", "objects", "info", "alternates"), Path.Combine(benCheckout.ClonePath, ".git", "objects"));
        var alt = await f.Repos.FetchBackAsync(ada, Ct);
        Assert.False(alt.Fetched);
        Assert.Contains("alternates", alt.Error);
    }

    [Fact]
    public async Task HostGit_ReadsNoConfigFromTheMachine_AndCanReachOnlyLocalPaths()
    {
        using var f = new TeamsFixture();
        var origins = await f.Repos.Git.RunAsync(f.AppRoot, Ct, "config", "--list", "--show-origin");
        foreach (var line in origins.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.StartsWith("command line:", line);
        }
        Assert.Contains("credential.helper=", origins);
        var remote = await f.Repos.Git.TryRunAsync(f.AppRoot, ["ls-remote", "https://github.com/git/git.git"], Ct);
        Assert.NotEqual(0, remote.ExitCode);
        Assert.Contains("transport 'https' not allowed", remote.Stderr);
    }

    [Fact]
    public async Task AtStartUp_TheCoreFetchesBackWorkOfARunTheLastProcessLeftOpen_AndWrapsRunsInClones()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        File.WriteAllText(Path.Combine(checkout.ClonePath, "half.txt"), "half done");
        AgentGit.Ok(checkout, "add", "half.txt");
        AgentGit.Ok(checkout, "commit", "-q", "-m", "half"); // then the core was killed: no fetch-back happened
        var team = f.Repos.RepoOf(ada.TeamId)!.Path;
        Assert.DoesNotContain("members/alpha/ada", AgentGit.Host(team, "branch", "--list"));

        var env = new Dictionary<string, string?> { [global::Yawble.Core.Bus.BusHost.RunnerVariable] = "fake-complete" };
        await using (var engine = await global::Yawble.Core.Bus.BusHost.StartAsync(f.Db.ConnectionString, k => env.GetValueOrDefault(k), log: null, launcher: null, runner: null, maxConcurrentAgents: null, appDataRoot: f.AppRoot))
        {
            Assert.Contains("members/alpha/ada", AgentGit.Host(team, "branch", "--list"));
            Assert.Equal("half done", AgentGit.Host(team, "show", "members/alpha/ada:half.txt"));

            // And a run of the fake runner now works in Ben's clone, made on demand.
            await engine.PersonTellAsync("Alpha", "Ben", "anything", ct: Ct);
            await global::Yawble.Core.Tests.Bus.Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiescent");
            using var store = f.Db.OpenStore();
            Assert.NotEmpty(store.Runs());
            Assert.All(store.Runs(), run => Assert.True(run.State == "completed", $"{run.State}: {run.Reason}"));
            Assert.True(Directory.Exists(Path.Combine(f.Repos.MemberRoot("Alpha", "Ben"), "repo", ".git")));
        }
    }

    [Fact]
    public async Task ThePostRunCheck_RemovesFilesTheAgentMadeReadOnly()
    {
        // Windows refuses to delete a read-only file and git cannot rename its config.lock over one; on every OS the
        // check must still remove the credential file and the remote, and the fetch-back still succeed.
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var checkout = await f.Repos.PrepareAsync(ada, Ct);
        AgentGit.Ok(checkout, "remote", "add", "origin", "https://FAKE-user:FAKE-token@github.com/acme/app.git");
        var config = Path.Combine(checkout.ClonePath, ".git", "config");
        var credentials = Path.Combine(checkout.Workspace, ".git-credentials");
        File.WriteAllText(credentials, "https://FAKE-user:FAKE-token@github.com\n");
        foreach (var file in new[] { config, credentials }) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        var findings = await f.Repos.CheckAndScrubAsync(ada, Ct);

        Assert.Contains(findings, x => x.Where == "clone .git/config" && x.What.StartsWith("[remote.origin]", StringComparison.Ordinal));
        Assert.Contains(findings, x => x.Where == "workspace .git-credentials");
        Assert.False(File.Exists(credentials));
        Assert.DoesNotContain("FAKE", File.ReadAllText(config));
        Assert.Equal("", AgentGit.Ok(checkout, "remote"));
    }

    [Theory]
    [InlineData("Ada", true)]
    [InlineData("Ben Smith", true)]
    [InlineData("Zoë", true)]
    [InlineData("COMet", true)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("C:", false)]
    [InlineData("what?", false)]
    [InlineData("a*b", false)]
    [InlineData("<x>", false)]
    [InlineData("a|b", false)]
    [InlineData("say \"hi\"", false)]
    [InlineData("tab\tname", false)]
    [InlineData("Ada.", false)]
    [InlineData("Ada ", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("com1", false)]
    [InlineData("LPT9", false)]
    [InlineData("..", false)]
    public void FolderNames_FollowWindowsRules_OnEveryOs(string name, bool allowed)
    {
        // The same names are refused on Linux and macOS, so a team made there behaves the same when moved to Windows.
        if (allowed) Assert.Equal(name, TeamRepos.Segment(name));
        else Assert.Throws<ArgumentException>(() => TeamRepos.Segment(name));
    }

    [Fact]
    public async Task AMemberWhoseNameCannotBeAWindowsFolder_GetsAFailedPrepare_NotAHalfMadeClone()
    {
        using var f = new TeamsFixture();
        using var store = f.Db.OpenStore();
        var id = store.CreateTeam("Alpha");
        var odd = store.AddMember(id, "Ada?", global::Yawble.Core.Bus.MemberRole.Member, "claude");
        await Assert.ThrowsAsync<ArgumentException>(() => f.Repos.PrepareAsync(odd, Ct));
        Assert.False(Directory.Exists(Path.Combine(f.AppRoot, "teams", "Alpha", "members")));
    }

    [Theory]
    [InlineData("Alpha", "Ada", "members/alpha/ada")]
    [InlineData("Yawble Engineering Team", "Ben Smith", "members/yawble-engineering-team/ben-smith")]
    [InlineData("Alpha", "  ./..  ", "members/alpha/member")]
    [InlineData("Zoë's", "Zoë", "members/zo-s/zo")]
    public void BranchNames(string team, string member, string branch)
    {
        // B016: the team part is the same slug as the team branch's.
        Assert.Equal(branch, TeamRepos.BranchFor(team, member));
        Assert.Equal("team/" + branch.Split('/')[1], TeamRepos.TeamBranchFor(team));
    }

    [Theory]
    [InlineData("members/ada", true)]
    [InlineData("members/alpha/ada", false)]
    [InlineData("members/", false)]
    [InlineData("team/alpha", false)]
    [InlineData("feature/x", false)]
    public void LegacyMemberBranches(string branch, bool legacy) => Assert.Equal(legacy, TeamRepos.IsLegacyMemberBranch(branch));

    [Fact]
    public void AppRoot_IsTheParentOfADataFolder_AndNothingElse()
    {
        var root = Path.Combine(Path.GetTempPath(), "userData");
        Assert.Equal(root, global::Yawble.Core.Bus.BusHost.AppRootFor(Path.Combine(root, "data")));
        Assert.Null(global::Yawble.Core.Bus.BusHost.AppRootFor(Path.Combine(root, "yawble-data-x")));
    }
}
