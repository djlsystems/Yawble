using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Credentials;
using Yawble.Core.Gateway;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The team flow against fakes (no sandbox, no real CLI): the person gives the Manager a small coding task; the Manager splits it
/// between Ada (Claude Code) and Ben (Grok) through yawble-bridge; both work at the same time, each committing on its own
/// branch in its own clone; both hand back; the Manager declares complete. Both branches are in the team's repository,
/// no clone or workspace holds a remote or a git credential, the gateway refuses a git host, and nothing is pushed.
///
/// The runner stands in for the launcher: per run it starts the run's gateway (with the member's CLI's vhosts)
/// and an yawble-bridge whose cwd is the member's clone and whose HOME is its workspace, then plays the agent: its MCP
/// calls go through the bridge, and its git commands run as a sandboxed agent's would (cwd = clone, HOME = workspace,
/// nothing else inherited).
/// </summary>
public class TeamFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record RunSeen(string Member, IReadOnlySet<string> Vhosts, string Prompt, string? Cwd);

    private sealed class FakeLauncher(GatewayFixture f, IReadOnlyDictionary<string, string> cliOf, Func<RunContext, BridgeProcess, RunGateway, Task> agent) : IAgentRunner
    {
        public ConcurrentQueue<RunSeen> Runs { get; } = new();

        public async Task<RunResult> RunAsync(RunContext run, CancellationToken ct)
        {
            var cli = cliOf.GetValueOrDefault(run.Member.Name, "claude");
            var vhosts = RunGatewaySpec.VhostsFor(cli, GrokMode.Session);
            Runs.Enqueue(new RunSeen(run.Member.Name, vhosts, TeamPrompt.For(run), run.Checkout?.ClonePath));
            await using var gateway = f.Host.Start(new RunGatewaySpec(run.RunId, run.Member.Qualified, vhosts, [], TimeSpan.FromHours(1)), new McpBusServer(run));
            await using var bridge = BridgeProcess.Start(gateway.ProxyUrl, gateway.Token.Value, psi =>
            {
                psi.WorkingDirectory = run.Checkout!.ClonePath;
                psi.Environment["HOME"] = run.Checkout.Workspace;
            });
            await bridge.InitializeAsync();
            await agent(run, bridge, gateway);
            return RunResult.Ok;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheManagerSplitsACodingTask_TwoMembersCommitOnTheirBranches_AndTheWorkflowCompletes_AndTheirBranchesArePushedButNotTrunk(bool realBridgeProcess)
    {
        if (realBridgeProcess) GatewayFixture.SkipUnlessRealLoopback("the real yawble-bridge processes reaching the gateway over a socket");
        await using var f = await GatewayFixture.StartAsync(realLoopback: realBridgeProcess);
        var appRoot = Path.Combine(f.Db.Dir, "app");
        var log = new ConcurrentQueue<string>();
        var repos = new TeamRepos(appRoot, f.Db.ConnectionString, log: log.Enqueue);
        var bothRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var handbacks = new HashSet<string>();
        var declarations = new ConcurrentQueue<string>();
        var connectToGitHost = new ConcurrentQueue<string>();

        async Task Agent(RunContext run, BridgeProcess bridge, RunGateway gateway)
        {
            if (run.Member.Role == MemberRole.Manager)
            {
                if (run.Items.Any(i => i.Source == MessageTypes.PersonSource))
                {
                    var (role, _) = await bridge.CallAsync("skills_get", new JsonObject { ["name"] = "manager" });
                    Assert.Contains("members/<team>/<name>", role);
                    Assert.Contains("never ask a member to push", role);
                    var (status, _) = await bridge.CallAsync("status");
                    Assert.Contains("\"name\":\"Ada\"", status);
                    Assert.Contains("\"name\":\"Ben\"", status);
                    Assert.False((await bridge.CallAsync("tell", new JsonObject
                    {
                        ["member"] = "Ada",
                        ["instruction"] = "Add a GET /health endpoint in app/health.py returning {\"ok\": true}. Commit it on your branch.",
                    })).IsError);
                    Assert.False((await bridge.CallAsync("tell", new JsonObject
                    {
                        ["member"] = "Ben",
                        ["instruction"] = "Write tests/test_health.py checking GET /health returns {\"ok\": true}. Commit it on your branch.",
                    })).IsError);
                    Assert.False((await bridge.CallAsync("progress", new JsonObject { ["status"] = "split: endpoint to Ada, test to Ben" })).IsError);
                    return;
                }
                lock (handbacks)
                {
                    foreach (var i in run.Items.Where(i => i.Type == MessageTypes.Handback)) handbacks.Add(i.Source);
                    if (handbacks.Count < 2) return;
                }
                var (text, refused) = await bridge.CallAsync("workflow_complete", new JsonObject
                {
                    ["delivered"] = "GET /health on members/alpha/ada and its test on members/alpha/ben",
                });
                declarations.Enqueue(refused ? "refused: " + text : "ok");
                return; // refused while a member's run is still open: end the turn and be woken by its ending
            }

            // A member. Both members are running at the same time before either does its work.
            if (Interlocked.Increment(ref started) == 2) bothRunning.TrySetResult();
            await bothRunning.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

            var (memberRole, _) = await bridge.CallAsync("skills_get", new JsonObject { ["name"] = "member" });
            Assert.Contains("Never push", memberRole);
            Assert.False((await bridge.CallAsync("progress", new JsonObject { ["status"] = "writing the code" })).IsError);

            var c = run.Checkout!;
            Assert.Equal(TeamRepos.BranchFor(run.Member.Team, run.Member.Name), AgentGit.Ok(c, "symbolic-ref", "--short", "HEAD"));
            string file;
            if (run.Member.Name == "Ada")
            {
                file = "app/health.py";
                Directory.CreateDirectory(Path.Combine(c.ClonePath, "app"));
                File.WriteAllText(Path.Combine(c.ClonePath, file), "def health():\n    return {\"ok\": True}\n");
            }
            else
            {
                file = "tests/test_health.py";
                Directory.CreateDirectory(Path.Combine(c.ClonePath, "tests"));
                File.WriteAllText(Path.Combine(c.ClonePath, file), "from app.health import health\n\ndef test_health():\n    assert health() == {\"ok\": True}\n");
            }
            AgentGit.Ok(c, "add", file);
            AgentGit.Ok(c, "commit", "-q", "-m", $"{run.Member.Name}: {file}");
            var sha = AgentGit.Ok(c, "rev-parse", "--short", "HEAD");

            // The guardrails, from inside: no remote, no credential helper, a push fails, a git host is unreachable.
            Assert.Equal("", AgentGit.Ok(c, "remote", "-v"));
            // A host may configure a helper system-wide (Git Credential Manager on Windows, osxkeychain on macOS); the
            // clone's empty value comes last and resets the list, so git uses no helper at all.
            var helpers = AgentGit.Run(c, "config", "--get-all", "--show-origin", "credential.helper").Stdout.Split('\n');
            Assert.Equal("file:.git/config", helpers[^1].TrimEnd());
            Assert.Equal("", AgentGit.Run(c, "config", "--get", "credential.helper").Stdout);
            var push = AgentGit.Run(c, "push", "origin", "HEAD");
            Assert.NotEqual(0, push.ExitCode);
            var (connect, _, client) = await ProxyClient.RawAsync(gateway, "CONNECT github.com:443 HTTP/1.1\r\nHost: github.com:443\r\n\r\n");
            client.Dispose();
            connectToGitHost.Enqueue($"{run.Member.Name}: {connect}");

            Assert.False((await bridge.CallAsync("handback", new JsonObject
            {
                ["delivered"] = $"{file} committed as {sha} on {c.Branch}",
            })).IsError);
        }

        var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(f.Db.Dir);
        var originBefore = AgentGit.Host(bare, "for-each-ref");
        var launcher = new FakeLauncher(f, new Dictionary<string, string> { ["Ben"] = "grok" }, Agent);
        await using var engine = new BusEngine(f.Db.OpenStore(), new TeamRepoRunner(launcher, repos, log.Enqueue),
            new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200) });
        var teamId = await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", "claude", Ct);
        await engine.AddMemberAsync("Alpha", "Ben", "grok", Ct);
        await repos.UseExistingRepoAsync(teamId, "Alpha", picked, Ct);
        await engine.StartAsync(Ct);

        var root = await engine.PersonTellAsync("Alpha", "Manager", "Add a /health endpoint and its test.", ct: Ct);
        using var reader = f.Db.OpenStore();
        try
        {
            await Wait.UntilAsync(() => reader.WorkflowDeclared(root.Seq), "workflow.completed", TimeSpan.FromSeconds(120));
        }
        catch
        {
            // On failure, what happened: the workflow, every run's end and reason, and the core's log.
            var o = TestContext.Current.TestOutputHelper!;
            foreach (var m in reader.Workflow(root.Seq)) o.WriteLine($"seq {m.Seq,3} {m.Source,-14} {m.Type,-28} {BatchText.TextOf(m)} {m.Payload}");
            foreach (var r in reader.Runs()) o.WriteLine($"run {r.Id} member {r.MemberId} {r.State}: {r.Reason}");
            foreach (var d in declarations) o.WriteLine("workflow_complete: " + d);
            foreach (var line in log) o.WriteLine("core: " + line);
            throw;
        }
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiescent", TimeSpan.FromSeconds(30));

        // ---- the bus: causation and depth, and the declaration
        var wf = reader.Workflow(root.Seq);
        var bySeq = wf.ToDictionary(m => m.Seq);
        Assert.All(wf, m => Assert.Equal(root.Seq, m.CorrelationId));
        Assert.All(wf.Where(m => m.CausationSeq is not null), m => Assert.Equal(bySeq[m.CausationSeq!.Value].Depth + 1, m.Depth));
        var tellAda = Assert.Single(wf, m => m.Type == "agent.instruction.Alpha/Ada");
        var tellBen = Assert.Single(wf, m => m.Type == "agent.instruction.Alpha/Ben");
        Assert.All(new[] { tellAda, tellBen }, t => { Assert.Equal(root.Seq, t.CausationSeq); Assert.Equal(1, t.Depth); });
        var adaBack = Assert.Single(wf, m => m.Type == MessageTypes.Handback && m.Source == "Alpha/Ada");
        var benBack = Assert.Single(wf, m => m.Type == MessageTypes.Handback && m.Source == "Alpha/Ben");
        Assert.Equal(tellAda.Seq, adaBack.CausationSeq);
        Assert.Equal(tellBen.Seq, benBack.CausationSeq);
        Assert.Equal(2, adaBack.Depth);
        var declared = Assert.Single(wf, m => m.Type == MessageTypes.WorkflowCompleted);
        Assert.Equal("Alpha/Manager", declared.Source);
        Assert.Contains("ok", declarations);
        Assert.All(reader.Runs(), r => Assert.Equal("completed", r.State));
        Assert.Equal(["Ada", "Ben"], launcher.Runs.Where(r => r.Member != "Manager").Select(r => r.Member).Order());

        // ---- the CLIs: Ada on Claude (model vhost), Ben on Grok (grok vhost); both cwd = own clone
        var adaRun = Assert.Single(launcher.Runs, r => r.Member == "Ada");
        var benRun = Assert.Single(launcher.Runs, r => r.Member == "Ben");
        Assert.Equal([Vhosts.Model], adaRun.Vhosts);
        Assert.Equal([Vhosts.Grok], benRun.Vhosts);
        Assert.Equal(repos.MemberRoot("Alpha", "Ada") + Path.DirectorySeparatorChar + "repo", adaRun.Cwd);
        Assert.Contains("on branch members/alpha/ada", adaRun.Prompt);
        Assert.Contains("skills_get tool with name \"member\"", adaRun.Prompt);
        // The Manager was told in sentences: who handed back, and that the runs ended.
        var managerNotices = string.Join("\n", launcher.Runs.Where(r => r.Member == "Manager").Select(r => r.Prompt));
        Assert.Contains("Ada handed back: app/health.py committed as", managerNotices);
        Assert.Contains("Ben handed back: tests/test_health.py committed as", managerNotices);

        // ---- the team's repository: both branches, with the work; trunk untouched (merging is the person's)
        var branches = AgentGit.Host(picked, "for-each-ref", "--format=%(refname:short)", "refs/heads/");
        Assert.Equal(["members/alpha/ada", "members/alpha/ben", "trunk"], branches.Split('\n').Order());
        Assert.Contains("return {\"ok\": True}", AgentGit.Host(picked, "show", "members/alpha/ada:app/health.py"));
        Assert.Contains("def test_health", AgentGit.Host(picked, "show", "members/alpha/ben:tests/test_health.py"));
        Assert.Equal("1", AgentGit.Host(picked, "rev-list", "--count", "trunk"));
        Assert.Equal("Ada (Alpha)", AgentGit.Host(picked, "log", "-1", "--format=%an", "members/alpha/ada"));

        // ---- pushed by the host: the members' branches reach origin, trunk does not move
        Assert.Equal(AgentGit.Host(picked, "rev-parse", "members/alpha/ada"), AgentGit.Host(bare, "rev-parse", "refs/heads/members/alpha/ada"));
        Assert.Equal(AgentGit.Host(picked, "rev-parse", "members/alpha/ben"), AgentGit.Host(bare, "rev-parse", "refs/heads/members/alpha/ben"));
        Assert.Contains(originBefore.Split('\n').Single(l => l.EndsWith("refs/heads/trunk")), AgentGit.Host(bare, "for-each-ref"));
        // Whichever run ends after a branch is fetched back pushes it (runs overlap, so one push may carry both).
        Assert.Matches(@"The app pushed [^.]*members/alpha/ada[^.]* to origin\.", managerNotices);
        Assert.Matches(@"The app pushed [^.]*members/alpha/ben[^.]* to origin\.", managerNotices);

        // ---- no remote and no git credential in any clone or workspace
        foreach (var member in new[] { "Manager", "Ada", "Ben" })
        {
            var memberRoot = repos.MemberRoot("Alpha", member);
            var clone = Path.Combine(memberRoot, "repo");
            Assert.Equal("", AgentGit.Host(clone, "remote"));
            var config = File.ReadAllText(Path.Combine(clone, ".git", "config"));
            Assert.DoesNotContain("[remote", config);
            Assert.DoesNotContain("[url", config);
            Assert.Matches(@"\[credential\]\s*\n\s*helper\s*=\s*\n", config);
            foreach (var file in new[] { ".git-credentials", ".netrc", "_netrc", ".gitconfig", Path.Combine(".config", "git", "credentials") })
            {
                Assert.False(File.Exists(Path.Combine(memberRoot, "home", file)), $"{member}: {file}");
            }
        }
        using (var store = f.Db.OpenStore())
        {
            foreach (var m in store.AllMembers()) Assert.Equal("[]", repos.Row(m.Id)!.LastCheck);
        }

        // ---- the gateway: no git host reachable and no git credential; the attempt is in the gateway log
        Assert.Equal(2, connectToGitHost.Count);
        Assert.All(connectToGitHost, line => Assert.DoesNotContain(" 200", line));
        var events = f.Log.Read();
        Assert.Equal(2, events.Count(e => e.Kind == GatewayEventKinds.ConnectDenied && e.Target!.StartsWith("github.com", StringComparison.Ordinal)));
        Assert.DoesNotContain(events, e => e.Kind is GatewayEventKinds.Forward or GatewayEventKinds.ConnectAllowed
            && (e.Target ?? "").Contains("github", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(f.Upstream.Requests); // and no model call was needed by the fakes
        // ---- a readable trace, for the hand-back
        var output = TestContext.Current.TestOutputHelper!;
        foreach (var m in wf) output.WriteLine($"seq {m.Seq,3} depth {m.Depth} cause {m.CausationSeq?.ToString() ?? "-",3} {m.Source,-14} {m.Type,-28} {BatchText.TextOf(m)}");
        foreach (var r in reader.Runs()) output.WriteLine($"run {r.Id} member {r.MemberId} items {r.Items} {r.State} started {r.StartedAt} ended {r.EndedAt}");
        foreach (var d in declarations) output.WriteLine("workflow_complete: " + d);
        foreach (var c in connectToGitHost) output.WriteLine("CONNECT github.com:443 -> " + c);
        output.WriteLine("team repo branches:\n" + AgentGit.Host(picked, "log", "--all", "--format=%h %d %an: %s"));
        foreach (var line in log) output.WriteLine("core: " + line);
        foreach (var kind in Enum.GetValues<CredentialSlot>()) Assert.DoesNotContain("git", kind.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
