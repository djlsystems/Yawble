using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Concierge;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Teams;
using Xunit;

namespace Yawble.Core.Tests.Concierge;

/// <summary>
/// Git tidy through the Concierge: git_tidy_plan's text, a contained branch deleted by git_branches_delete without
/// unmerged_ok, and git_branch_realign's confirm flow (show old -> new, the person's yes in a later turn, the old commit
/// recorded). Temp repositories only.
/// </summary>
public sealed class ConciergeTidyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Id = ["-c", "user.name=Person", "-c", "user.email=p@example.invalid"];

    private static string PersonCommit(string repo, string file)
    {
        File.WriteAllText(Path.Combine(repo, file), file);
        AgentGit.Host(repo, "add", file);
        AgentGit.Host(repo, [.. Id, "commit", "-q", "-m", file]);
        return AgentGit.Host(repo, "rev-parse", "HEAD");
    }

    private static async Task<BusEngine> BusAsync(TeamsFixture f)
    {
        var bus = new BusEngine(f.Db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false, Repos = f.Repos });
        await bus.StartAsync(Ct);
        return bus;
    }

    private static ConciergeActions Actions(BusEngine bus, TeamsFixture f, ConfirmationBook book, ActionRepo? repo = null)
    {
        var empty = new Func<string, JsonObject?, CancellationToken, Task<JsonNode?>>((_, _, _) => Task.FromResult<JsonNode?>(new JsonObject()));
        var git = new HostGit(Path.Combine(f.Db.Dir, "empty.gitconfig"));
        return new ConciergeActions(bus, empty, empty, new ConciergeGit(git, f.Repos.Pusher!),
            (_, _) => Task.FromResult<IReadOnlyList<ActionRepo>>(repo is null ? [] : [repo]), book,
            turn: new ConciergeTurn("c1", byPerson: true, remote: false));
    }

    private static async Task<string> Call(ConciergeActions a, string tool, JsonObject args)
    {
        try { return await a.TryCallAsync(tool, args, Ct) ?? throw new InvalidOperationException("not an action tool"); }
        catch (ArgumentException e) { return "REFUSED: " + e.Message; }
    }

    private static string Token(string text) => System.Text.RegularExpressions.Regex.Match(text, "confirm=\"([0-9a-f]+)\"").Groups[1].Value;

    private static string? Rev(string repo, string reference)
    {
        var r = AgentGit.Run(repo, Path.GetTempPath(), "rev-parse", "--verify", "-q", reference);
        return r.ExitCode == 0 ? r.Stdout : null;
    }

    /// <summary>No origin, no clones: runs where git's local transport cannot start (a sandboxed test run).</summary>
    [Fact]
    public async Task TidyPlanText_ShowsTheProof_AndAContainedBranchDeletesWithoutUnmergedOk()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var picked = Path.Combine(f.Db.Dir, "local-repo");
        Directory.CreateDirectory(picked);
        AgentGit.Host(picked, "init", "-q", "-b", "trunk");
        PersonCommit(picked, "README.md");
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        AgentGit.Host(picked, "switch", "-q", "-c", "feature/picked");
        var pick = PersonCommit(picked, "picked.txt");
        AgentGit.Host(picked, "switch", "-q", "-c", "feature/own", "trunk");
        PersonCommit(picked, "own.txt");
        AgentGit.Host(picked, "switch", "-q", "trunk");
        AgentGit.Host(picked, [.. Id, "cherry-pick", "-x", pick]);

        await using var bus = await BusAsync(f);
        var text = await new ConciergeReadTools(bus, (_, _, _) => Task.FromResult<JsonNode?>(null), f.Db.ConnectionString, remote: false)
            .CallAsync("git_tidy_plan", new JsonObject { ["team"] = "Alpha" }, Ct);
        Assert.Contains("== Alpha: main", text);
        Assert.Contains("contained in the base", text);
        Assert.Contains($"- feature/picked [local]: git cherry trunk {pick[..12]}", text);
        Assert.Contains("own work", text);
        Assert.Contains("- feature/own [local]", text);
        Assert.Equal(pick, Rev(picked, "refs/heads/feature/picked")); // reading changed nothing

        var book = new ConfirmationBook();
        var repo = new ActionRepo("main", picked, "trunk", []);
        var args = new JsonObject { ["team"] = "Alpha", ["branches"] = "feature/picked, feature/own" };
        var shown = await Call(Actions(bus, f, book, repo), "git_branches_delete", args.DeepClone().AsObject());
        Assert.StartsWith("NOT DONE", shown);
        Assert.Contains($"delete feature/picked at {pick} (contained in base: git cherry trunk", shown);
        Assert.Contains("feature/own", shown);
        Assert.Contains("KEPT, not merged", shown);

        var yes = args.DeepClone().AsObject();
        yes["confirm"] = Token(shown);
        var done = await Call(Actions(bus, f, book, repo), "git_branches_delete", yes); // the person's yes: a later turn
        Assert.StartsWith("ok:", done);
        Assert.Null(Rev(picked, "refs/heads/feature/picked"));
        Assert.NotNull(Rev(picked, "refs/heads/feature/own"));
    }

    /// <summary>Needs member clones and an origin (git's local transport).</summary>
    [Fact]
    public async Task Realign_ShowsOldAndNew_RunsOnlyOnTheLaterYes_AndRecordsTheOldCommit()
    {
        using var f = new TeamsFixture();
        var (_, ada, _) = f.Team();
        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f);
        await f.Repos.UseExistingRepoAsync(ada.TeamId, "Alpha", picked, Ct);
        var clone = await f.Repos.PrepareAsync(ada, Ct);
        var trunk = AgentGit.Host(picked, "rev-parse", "trunk");
        File.WriteAllText(Path.Combine(clone.ClonePath, "ada.txt"), "ada");
        AgentGit.Ok(clone, "add", "ada.txt");
        AgentGit.Ok(clone, "-c", "user.name=a", "-c", "user.email=a@x", "commit", "-q", "-m", "ada");
        var adaWork = AgentGit.Ok(clone, "rev-parse", "HEAD");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        await f.Repos.PublishAsync(ada, Ct);
        AgentGit.Ok(clone, "reset", "-q", "--hard", trunk);

        await using var bus = await BusAsync(f);
        var book = new ConfirmationBook();
        var args = new JsonObject { ["team"] = "Alpha", ["repo"] = "main", ["member"] = "Ada" };
        var turn1 = Actions(bus, f, book);
        var shown = await Call(turn1, "git_branch_realign", args.DeepClone().AsObject());
        Assert.StartsWith("NOT DONE", shown);
        Assert.Contains(adaWork[..12], shown);
        Assert.Contains(trunk[..12], shown);
        Assert.Equal(adaWork, Rev(picked, "refs/heads/members/alpha/ada")); // nothing changed by asking

        var yes = args.DeepClone().AsObject();
        yes["confirm"] = Token(shown);
        Assert.Contains("has not said yes yet", await Call(turn1, "git_branch_realign", yes.DeepClone().AsObject()));
        Assert.Equal(adaWork, Rev(picked, "refs/heads/members/alpha/ada"));

        var done = await Call(Actions(bus, f, book), "git_branch_realign", yes);
        Assert.StartsWith("ok:", done);
        Assert.Equal(trunk, Rev(picked, "refs/heads/members/alpha/ada"));
        Assert.Equal(trunk, Rev(bare, "refs/heads/members/alpha/ada"));
        Assert.Contains(adaWork, File.ReadAllText(Path.Combine(picked, ".git", TeamRepos.RealignLog)));
    }
}
