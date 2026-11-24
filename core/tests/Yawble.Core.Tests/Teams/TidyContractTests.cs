using System.Text.Json.Nodes;
using Yawble.Core.Teams;
using Yawble.Core.Ui;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// Card 363 (verify B067): the core's git.tidyPlan answer (UiRpc.TidyPlanJson) in the window's contract, as
/// shell/src/shared/api.ts TidyPlan / TidyBranch / TidyClass declare it: the same field names, the same seven classes,
/// optional fields left out when absent, and no local path in the answer. No git needed.
/// </summary>
public sealed class TidyContractTests
{
    // shell/src/shared/api.ts (card 362): TidyClass and TidyBranch's keys.
    private static readonly string[] ShellClasses = ["merged", "contained", "working", "moved", "own-work", "origin-only", "scratch"];
    private static readonly string[] ShellBranchKeys = ["name", "where", "class", "proof", "commits", "teamSha", "cloneSha", "member"];

    [Fact]
    public void TheCoresClasses_AreTheShellsClasses()
    {
        var core = typeof(TidyClasses).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).Order().ToArray();
        Assert.Equal(ShellClasses.Order().ToArray(), core);
    }

    [Fact]
    public void TidyPlanJson_UsesTheShellsFieldNames_LeavesAbsentOnesOut_AndHasNoLocalPath()
    {
        var full = new TidyBranch("members/t/marlo", ["local", "origin", "clone:DeveloperMarlo"], TidyClasses.Moved)
        {
            Proof = "the team's copy has 1 commit(s)", Commits = [new TidyCommit("aa11", "Retry")], TeamSha = "5bbb", CloneSha = "e0f2", Member = "DeveloperMarlo",
        };
        var bare = new TidyBranch("members/t/old", ["local"], TidyClasses.Merged);
        var plan = new TeamGitTidyPlan([new TidyRepo("main", @"C:\secret\team\repo", "main", [full, bare]) { Notes = ["a clone was not read"] }]);

        var json = UiRpc.TidyPlanJson(plan);
        var repo = Assert.IsType<JsonObject>(Assert.Single(json["repos"]!.AsArray()));
        Assert.Equal("main", (string?)repo["repo"]);
        Assert.Equal("a clone was not read", (string?)repo["notes"]![0]);
        Assert.DoesNotContain(@"C:\secret", json.ToJsonString().Replace(@"\\", @"\"));

        var branches = repo["branches"]!.AsArray().Select(b => b!.AsObject()).ToList();
        Assert.Equal(ShellBranchKeys.Order(), branches[0].Select(kv => kv.Key).Order());
        Assert.Equal(["aa11", "Retry"], new[] { (string?)branches[0]["commits"]![0]!["sha"], (string?)branches[0]["commits"]![0]!["subject"] });
        Assert.Equal(["class", "name", "where"], branches[1].Select(kv => kv.Key).Order());
        Assert.All(branches.SelectMany(b => b.Select(kv => kv.Key)), k => Assert.Contains(k, ShellBranchKeys));
    }
}
