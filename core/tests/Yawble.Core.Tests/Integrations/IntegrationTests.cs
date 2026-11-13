using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Integrations, first slices: the person's CLIs as agent tools, under rules, with approvals.</summary>
public class IntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ rules

    [Fact]
    public void FloorsBeatEverything_TheTeamBeatsGlobal_TheMostSpecificWins_ElseAsk()
    {
        var gh = CliCatalog.Of("gh")!;
        CliRule R(string p, string d) => new(p, d, "");
        Assert.Equal(Decisions.Deny, CliRules.Decide(["auth", "token"], gh, [R("auth*", Decisions.Allow)], []).Decision); // floor
        Assert.Equal(Decisions.Allow, CliRules.Decide(["pr", "list", "--state", "open"], gh, [], []).Decision); // preset
        Assert.Equal(Decisions.Ask, CliRules.Decide(["pr", "create", "--fill"], gh, [], []).Decision); // preset
        Assert.Equal(Decisions.Allow, CliRules.Decide(["pr", "create", "--fill"], gh, [R("pr create*", Decisions.Allow)], [R("pr create*", Decisions.Deny)]).Decision); // team over global
        Assert.Equal(Decisions.Deny, CliRules.Decide(["pr", "merge", "12"], gh, [], [R("pr*", Decisions.Allow), R("pr merge*", Decisions.Deny)]).Decision); // most specific
        Assert.Equal(Decisions.Ask, CliRules.Decide(["codespace", "create"], gh, [], []).Decision); // nothing says: ask
        Assert.Equal(Decisions.Deny, CliRules.Decide(["storage", "account", "keys", "list"], CliCatalog.Of("az")!, [], []).Decision); // az floor beats "* list*"
        Assert.Equal(Decisions.Allow, CliRules.Decide(["group", "list"], CliCatalog.Of("az")!, [], []).Decision);
    }

    // ------------------------------------------------------------------ running

    [Fact]
    public async Task ACliRunsWithoutAShell_TokensAreRedacted_AndCmdSyntaxIsRefusedForABatchFile()
    {
        var git = CliRunner.Find("git");
        Assert.NotNull(git);
        var o = await CliRunner.RunAsync(git!, ["--version"], null, null, TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(0, o.ExitCode);
        Assert.StartsWith("git version", o.Stdout);
        Assert.Equal("token [redacted] and [redacted]", CliRunner.Redact("token ghp_abcdefghijklmnopqrstuvwxyz0123 and Bearer abcdefghijklmnopqrstuvwxyz"));

        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("yawble-cli-").FullName;
        try
        {
            var cmd = Path.Combine(dir, "fake.cmd");
            File.WriteAllText(cmd, "@echo off\r\necho args: %*\r\n");
            Assert.Contains("args: hello world", (await CliRunner.RunAsync(cmd, ["hello", "world"], null, null, TimeSpan.FromSeconds(30), Ct)).Stdout);
            await Assert.ThrowsAsync<ArgumentException>(() => CliRunner.RunAsync(cmd, ["x & calc"], null, null, TimeSpan.FromSeconds(30), Ct));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------ the agent's side and the person's

    private static async Task<(string Text, bool Error)> Call(RunContext run, string tool, object args)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
        };
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        var result = JsonNode.Parse(reply.Json!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    private static async Task<JsonArray> ToolList(RunContext run)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" };
        var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
        return JsonNode.Parse(reply.Json!)!["result"]!["tools"]!.AsArray();
    }

    [Fact]
    public async Task AnEnabledCliIsATool_AllowedCallsRun_AskingOnesWaitForThePerson_AndTheAgentIsToldTheResult()
    {
        using var db = new TempDb();
        string? version = null, noReason = null, asked = null, denied = null, help = null;
        string[]? tools = null;
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            if (run.Items[0].Payload.Contains("approved") || run.Items[0].Payload.Contains("declined")) return RunResult.Ok;
            tools = (await ToolList(run)).Select(t => t!["name"]!.GetValue<string>()).ToArray();
            version = (await Call(run, "git", new { args = new[] { "--version" }, reason = "which git" })).Text;
            noReason = (await Call(run, "git", new { args = new[] { "count-objects" } })).Text;
            asked = (await Call(run, "git", new { args = new[] { "count-objects", "-v" }, reason = "see what changed" })).Text;
            denied = (await Call(run, "git", new { args = new[] { "push", "--force" }, reason = "push it" })).Text;
            help = (await Call(run, "cli_help", new { cli = "git", args = new[] { "status" } })).Text;
            return RunResult.Ok;
        });
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await integrations.DispatchAsync("integrations.enable", new JsonObject { ["name"] = "git", ["teamId"] = alpha, ["on"] = true }, Ct);
        await integrations.DispatchAsync("integrations.saveRule", new JsonObject { ["name"] = "git", ["pattern"] = "push*", ["decision"] = "deny" }, Ct);
        await engine.StartAsync(Ct);
        var root = await engine.PersonTellAsync("Alpha", "Ada", "look at the repo", ct: Ct);
        await Wait.UntilAsync(() => help is not null, "Ada's calls");

        Assert.Contains("git", tools!);
        Assert.Contains("cli_help", tools!);
        Assert.Contains("git version", version);
        Assert.Contains("give a reason", noReason);
        Assert.StartsWith("asked:", asked);
        Assert.StartsWith("refused:", denied);
        Assert.Contains("usage: git status", help!, StringComparison.OrdinalIgnoreCase);

        var pending = integrations.Asks(alpha, pendingOnly: true);
        var ask = Assert.Single(pending);
        Assert.Equal(("git", "Alpha/Ada", "see what changed"), (ask.Integration, ask.Member, ask.Reason));
        Assert.Equal(1, integrations.PendingCount());

        await integrations.DispatchAsync("integrations.approve", new JsonObject { ["id"] = ask.Id, ["always"] = true }, Ct);
        Assert.Equal(0, integrations.PendingCount());
        // The approved call runs in the background (the click returns at once); the result reaches Ada in her workflow.
        await Wait.UntilAsync(() =>
        {
            using var s = db.OpenStore();
            return Task.FromResult(s.Workflow(root.Seq).Any(m => m.Payload.Contains("approved your call")));
        }, "the approved call's result");
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "Ada woken with the result");
        using var store = db.OpenStore();
        var told = store.Workflow(root.Seq).Last(m => m.Type == MessageTypes.InstructionPrefix + "Alpha/Ada");
        Assert.Contains("The person approved your call `git count-objects -v`", Payload.Field(told, "text"));
        Assert.Contains("exit ", Payload.Field(told, "text"));
        // "Always" on a one-word guess of a CLI never discovered ("count-objects") remembers the exact line for the team, as before.
        var list = (await integrations.DispatchAsync("integrations.list", null, Ct))!["integrations"]!.AsArray();
        var git = list.Single(i => i!["name"]!.GetValue<string>() == "git")!;
        Assert.Contains(git["rules"]!.AsArray(), r => r!["pattern"]!.GetValue<string>() == "count-objects -v" && r["teamId"]!.GetValue<long>() == alpha);
        Assert.DoesNotContain(integrations.Discover.GetChecklist("git", alpha).Nodes, n => n.Path == "count-objects");
        var calls = (await integrations.DispatchAsync("integrations.calls", null, Ct))!.AsArray().Select(c => c!["decision"]!.GetValue<string>()).ToArray();
        Assert.Contains("allow", calls);
        Assert.Contains("asked", calls);
        Assert.Contains("deny", calls);
        Assert.Contains("approved", calls);
    }

    [Fact]
    public async Task ACallRunsInTheRepositoryItNames_TheMainOneByDefault_AndAnAskedCallKeepsIt()
    {
        using var db = new TempDb();
        var app = Path.Combine(db.Dir, "app");
        var repos = new Yawble.Core.Teams.TeamRepos(app, db.ConnectionString);
        var mainDir = Path.Combine(db.Dir, "main-repo");
        var cloudDir = Path.Combine(db.Dir, "cloud-repo");
        foreach (var d in new[] { mainDir, cloudDir })
        {
            Directory.CreateDirectory(d);
            Yawble.Core.Tests.Teams.AgentGit.Host(d, "init", "-q", "-b", "main");
            Yawble.Core.Tests.Teams.AgentGit.Host(d, "-c", "user.name=p", "-c", "user.email=p@x", "commit", "-q", "--allow-empty", "-m", "first");
        }
        string? inMain = null, inCloud = null, unknown = null, asked = null;
        var runner = new ScriptedRunner().On("Alpha/Ada", async (run, ct) =>
        {
            if (run.Items[0].Payload.Contains("approved")) return RunResult.Ok;
            inMain = (await Call(run, "git", new { args = new[] { "rev-parse", "--show-toplevel" }, reason = "where" })).Text;
            inCloud = (await Call(run, "git", new { args = new[] { "rev-parse", "--show-toplevel" }, reason = "where", repo = "cloud" })).Text;
            unknown = (await Call(run, "git", new { args = new[] { "rev-parse", "--show-toplevel" }, reason = "where", repo = "nope" })).Text;
            asked = (await Call(run, "git", new { args = new[] { "count-objects", "-v" }, reason = "see the cloud repo", repo = "cloud" })).Text;
            return RunResult.Ok;
        });
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false, Repos = repos });
        var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await repos.UseExistingRepoAsync(alpha, "Alpha", mainDir, Ct);
        await repos.AddExtraAsync(alpha, "Alpha", "cloud", cloudDir, null, writable: true, Ct);
        await integrations.DispatchAsync("integrations.enable", new JsonObject { ["name"] = "git", ["teamId"] = alpha, ["on"] = true }, Ct);
                await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "where are you", ct: Ct);
        await Wait.UntilAsync(() => asked is not null, "Ada's calls");

        Assert.Contains("main-repo", inMain);
        Assert.Contains("cloud-repo", inCloud);
        Assert.Contains("no repository named 'nope'", unknown);
        Assert.StartsWith("asked:", asked);
        Assert.Contains("(in cloud)", asked);
        var ask = Assert.Single(integrations.Asks(alpha, pendingOnly: true));
        Assert.Equal("cloud", ask.Repo);
        var shown = (await integrations.DispatchAsync("integrations.asks", new JsonObject { ["teamId"] = alpha }, Ct))!.AsArray();
        Assert.Equal("cloud", shown[0]!["repo"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithGhEnabled_PrOpenAsksUnderTheGhRules_AndPrStatusUsesTheTeamBranch()
    {
        if (CliRunner.Find("gh") is null) Assert.Skip("gh is not installed here");
        using var db = new TempDb();
        var repos = new TeamRepos(db.Dir, db.ConnectionString);
        string? opened = null, status = null;
        string[]? tools = null;
        var runner = new ScriptedRunner().On("Alpha/Manager", async (run, ct) =>
        {
            tools = (await ToolList(run)).Select(t => t!["name"]!.GetValue<string>()).ToArray();
            opened = (await Call(run, "pr_open", new { title = "Greeting", body = "Says hi.", reason = "the work is done" })).Text;
            status = (await Call(run, "pr_status", new { })).Text;
            return RunResult.Ok;
        });
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false, Repos = repos });
        var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        await repos.EnsureTeamRepoAsync(alpha, "Alpha", Ct);
        await integrations.DispatchAsync("integrations.enable", new JsonObject { ["name"] = "gh", ["teamId"] = alpha, ["on"] = true }, Ct);
        // pr_status is allowed by its built-in row; deny it for this team so the test never reaches GitHub.
        await integrations.DispatchAsync("integrations.saveRule", new JsonObject { ["name"] = "gh", ["teamId"] = alpha, ["pattern"] = "pr_status*", ["decision"] = "deny" }, Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Manager", "open the PR", ct: Ct);
        await Wait.UntilAsync(() => status is not null, "the Manager's calls");

        Assert.Contains("pr_open", tools!);
        Assert.Contains("pr_status", tools!);
        Assert.StartsWith("asked:", opened);
        var ask = Assert.Single(integrations.Asks(alpha, pendingOnly: true));
        Assert.Equal(["pr", "create", "--head", "team/alpha", "--base", "main", "--title", "Greeting", "--body", "Says hi."], ask.Args);
        Assert.StartsWith("refused:", status);
        Assert.Contains("pr list --head team/alpha", status);
    }

    [Fact]
    public async Task PushingToOriginIsOnUnlessTurnedOff()
    {
        using var db = new TempDb();
        var repos = new TeamRepos(db.Dir, db.ConnectionString);
        await using var engine = new BusEngine(db.OpenStore(), new ScriptedRunner(), new BusOptions());
        var id = await engine.CreateTeamAsync("Alpha", ct: Ct);
        Assert.True(repos.PushesToOrigin(id));
        repos.SetPushesToOrigin(id, false);
        Assert.False(repos.PushesToOrigin(id));
    }

    [Fact]
    public async Task ATeamWithoutTheIntegrationDoesNotSeeIt()
    {
        using var db = new TempDb();
        string[]? betaTools = null;
        var runner = new ScriptedRunner().On("Beta/Bo", async (run, ct) =>
        {
            betaTools = (await ToolList(run)).Select(t => t!["name"]!.GetValue<string>()).ToArray();
            return RunResult.Ok;
        });
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
        var integrations = new IntegrationService(db.ConnectionString, engine);
        engine.Integrations = integrations;
        var alpha = await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.CreateTeamAsync("Beta", ct: Ct);
        await engine.AddMemberAsync("Beta", "Bo", ct: Ct);
        await integrations.DispatchAsync("integrations.enable", new JsonObject { ["name"] = "git", ["teamId"] = alpha, ["on"] = true }, Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Beta", "Bo", "hi", ct: Ct);
        await Wait.UntilAsync(() => betaTools is not null, "Bo's run");
        Assert.DoesNotContain("git", betaTools!);
        Assert.DoesNotContain("cli_help", betaTools!);
    }
}
