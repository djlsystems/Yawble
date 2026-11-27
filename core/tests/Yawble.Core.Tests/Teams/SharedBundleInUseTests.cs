using System.Text.Json;
using Yawble.Core.Bus;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The bundle members fetch from is replaced after every fetch-back, and a member may be reading it at that moment.
/// Windows refuses to replace an open file with "Access to the path is denied". The fetch-back waits for the reader;
/// when the reader never lets go, the branch is still in the team repository and the run is not failed for it. A run
/// whose agent finished but whose branch cannot be brought in is recorded with a failure class of its own. Local
/// repositories in temp dirs, no network.
/// </summary>
public class SharedBundleInUseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TeamsFixture F, MemberRef Ada, MemberCheckout Clone, string Repo, string Bundle)> AdaWithFetchedWork()
    {
        SandboxGit.RequireLocalTransport();
        var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var repo = (await f.Repos.EnsureTeamRepoAsync(ada.TeamId, "Alpha", Ct)).Path;
        var clone = await f.Repos.PrepareAsync(ada, Ct);
        Work(clone, "one.txt", "first work");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        var bundle = Path.Combine(f.Repos.SharedRepoPath("Alpha"), TeamRepos.SharedBundleFile);
        Assert.True(File.Exists(bundle));
        return (f, ada, clone, repo, bundle);
    }

    private static string Work(MemberCheckout clone, string file, string message)
    {
        File.WriteAllText(Path.Combine(clone.ClonePath, file), message + "\n");
        AgentGit.Ok(clone, "add", file);
        AgentGit.Ok(clone, "commit", "-q", "-m", message);
        return AgentGit.Ok(clone, "rev-parse", "HEAD");
    }

    [Fact]
    public async Task AMemberReadingTheBundle_DelaysItsReplacement_AndTheFetchBackSucceeds()
    {
        var (f, ada, clone, repo, bundle) = await AdaWithFetchedWork();
        using var _f = f;
        f.Repos.BundleSwapDelay = TimeSpan.FromMilliseconds(50);
        var work = Work(clone, "two.txt", "second work");

        // A reader holds the bundle open while the fetch-back wants to replace it, then lets go.
        var reader = File.Open(bundle, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(300, Ct); reader.Dispose(); }, Ct);
        var fetched = await f.Repos.FetchBackAsync(ada, Ct);
        await release;

        Assert.Null(fetched.Error);
        Assert.Equal(work, AgentGit.Host(repo, "rev-parse", "refs/heads/members/alpha/ada"));
        Assert.Contains(work + " refs/heads/members/alpha/ada", AgentGit.Host(repo, "bundle", "list-heads", bundle));
    }

    [Fact]
    public async Task ABundleThatStaysInUse_DoesNotFailTheFetchBack_TheBranchIsInTheTeamRepository()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("only Windows refuses to replace a file that is open");
        var (f, ada, clone, repo, bundle) = await AdaWithFetchedWork();
        using var _f = f;
        f.Repos.BundleSwapAttempts = 3;
        f.Repos.BundleSwapDelay = TimeSpan.FromMilliseconds(20);
        var work = Work(clone, "two.txt", "second work");

        FetchResult fetched;
        using (File.Open(bundle, FileMode.Open, FileAccess.Read, FileShare.Read))
            fetched = await f.Repos.FetchBackAsync(ada, Ct);

        Assert.Null(fetched.Error);
        Assert.True(fetched.Fetched);
        Assert.Equal(work, AgentGit.Host(repo, "rev-parse", "refs/heads/members/alpha/ada"));
        lock (f.Log) Assert.Contains(f.Log, line => line.Contains("the shared view of") && line.Contains("was not updated"));
        // The next fetch-back, with nobody reading, brings the bundle up to date.
        Assert.Null((await f.Repos.FetchBackAsync(ada, Ct)).Error);
        Assert.Contains(work + " refs/heads/members/alpha/ada", AgentGit.Host(repo, "bundle", "list-heads", bundle));
    }

    /// <summary>The agent's part of the run: it finishes, and leaves its clone in a state the host refuses to fetch from.</summary>
    private sealed class FinishesWithAnUnreadableClone : IAgentRunner
    {
        public Task<RunResult> RunAsync(RunContext run, CancellationToken ct)
        {
            if (run.Checkout is { } checkout) File.WriteAllText(Path.Combine(checkout.ClonePath, ".git", "commondir"), "elsewhere\n");
            return Task.FromResult(RunResult.Ok);
        }
    }

    [Fact]
    public async Task ARunThatFinished_ButWhoseBranchIsNotBroughtIn_IsFailedWithItsOwnClass_AndSaysTheRunFinished()
    {
        SandboxGit.RequireLocalTransport();
        using var f = new TeamsFixture();
        await using var engine = new BusEngine(f.Db.OpenStore(), new TeamRepoRunner(new FinishesWithAnUnreadableClone(), f.Repos),
            new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) });
        var teamId = await engine.CreateTeamAsync("Alpha", ct: Ct);
        await f.Repos.EnsureTeamRepoAsync(teamId, "Alpha", Ct);
        await engine.StartAsync(Ct);

        await engine.PersonTellAsync("Alpha", "Manager", "Do the thing.", ct: Ct);

        using var store = f.Db.OpenStore();
        Message? failed = null;
        for (var i = 0; i < 300 && failed is null; i++)
        {
            failed = store.AllMessages().FirstOrDefault(m => m.Type == MessageTypes.Failed);
            if (failed is null) await Task.Delay(100, Ct);
        }
        Assert.NotNull(failed);
        using var payload = JsonDocument.Parse(failed.Payload);
        Assert.Equal(FailureClasses.NotBroughtIn, payload.RootElement.GetProperty("failureClass").GetString());
        Assert.StartsWith("the member's branch was not brought into the team repository: ", payload.RootElement.GetProperty("reason").GetString());
        // What the rest of the team reads says the run finished and what did not come in, not that the run failed.
        var text = BatchText.TextOf(failed);
        Assert.Contains("'s run finished, but the member's branch was not brought into the team repository", text);
        Assert.DoesNotContain("run failed", text);
    }
}
