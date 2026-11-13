using System.Text.Json;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Launch;
using Xunit;
using AgentCli = Yawble.Core.Policy.AgentCli;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The team flow through the REAL launcher in REAL sandboxes (Linux, bubblewrap): the person
/// gives the Manager a small coding task in a repository they picked when creating the team (repoPath,
/// with an origin standing in for GitHub). The Manager splits it between ada (Claude Code) and ben (Grok) through
/// yawble-bridge; each commits with git inside its own sandbox, in its own clone, on its own branch; both hand back;
/// the Manager declares complete. Only the agent CLI is the stand-in (Launch/RealSandbox/standin-cli.py); the launcher,
/// gateway, bridge, the sandbox and git are real, and every secret is a fake.
/// </summary>
[Collection(RealSandboxCollection.Name)]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class RealSandboxTeamFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Manager() => new
    {
        runs = new object[]
        {
            new
            {
                say = "split",
                bus = new object[]
                {
                    new { tool = "skills_get", args = new { name = "manager" } },
                    new { tool = "status", args = new { } },
                    new { tool = "tell", args = new { member = "ada", instruction = "Add app/health.py with health() returning {\"ok\": True}. Commit it on your branch." } },
                    new { tool = "tell", args = new { member = "ben", instruction = "Add tests/test_health.py testing health(). Commit it on your branch." } },
                    new { tool = "progress", args = new { status = "split: endpoint to ada, test to ben" } },
                },
            },
            new
            {
                say = "declare",
                bus = new object[] { new { tool = "workflow_complete", args = new { delivered = "health() on members/ada and its test on members/ben" } } },
            },
        },
    };

    private static object Member(string file, string text, string bare) => new
    {
        say = "writes " + file,
        model = true,
        guardrails = new { connect = new[] { "github.com:443" } },
        files = new Dictionary<string, string> { [file] = text },
        git = new[]
        {
            new[] { "symbolic-ref", "--short", "HEAD" },
            new[] { "add", file },
            new[] { "commit", "-q", "-m", "add " + file },
            new[] { "rev-parse", "HEAD" },
            new[] { "remote", "-v" },
            new[] { "config", "--get-all", "--show-origin", "credential.helper" },
            new[] { "config", "--get", "credential.helper" },
            new[] { "push", "origin", "HEAD" },
            new[] { "push", bare, "HEAD:refs/heads/from-sandbox" },
            new[] { "push", "https://github.com/example/example.git", "HEAD" },
        },
        bus = new object[]
        {
            new { tool = "skills_get", args = new { name = "member" } },
            new { tool = "progress", args = new { status = "committed " + file } },
            new { tool = "handback", args = new { delivered = file + " committed on my branch" } },
        },
    };

    [Fact]
    public async Task TheManagerSplitsATask_TwoMembersCommitInsideRealSandboxes_AndTheWorkflowCompletes_WithNothingPushed()
    {
        RealSandbox.SkipUnlessAvailable();
        var outside = Path.Combine(Path.GetTempPath(), "yawble-p6-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        try
        {
            var (picked, bare) = TeamReposTests.PersonsRepoWithOrigin(outside);
            var originBefore = AgentGit.Host(bare, "for-each-ref");
            var scenario = new Dictionary<string, object>
            {
                ["lead"] = Manager(),
                ["ada"] = Member("app/health.py", "def health():\n    return {\"ok\": True}\n", bare),
                ["ben"] = Member("tests/test_health.py", "from app.health import health\n\ndef test_health():\n    assert health() == {\"ok\": True}\n", bare),
            };
            TeamRepos? repos = null;
            var log = new List<string>();
            await using var kit = await RealSandboxLauncherTests.Kit.StartAsync(scenario, wrapRunner: (inner, host, connection) =>
            {
                repos = new TeamRepos(host.AppDataRoot, connection, log: line => { lock (log) log.Add(line); });
                return new TeamRepoRunner(inner, repos, line => { lock (log) log.Add(line); });
            });
            var bus = kit.Launch.Bus;
            // Team creation, with the person's own repository picked.
            var teamId = await bus.CreateTeamAsync("acme", "lead", "claude", picked, Ct);
            await bus.AddMemberAsync(teamId, "ada", "claude", Ct);
            await bus.AddMemberAsync(teamId, "ben", "grok", Ct);

            var root = await bus.PersonTellAsync("acme", "lead", "Add a health() endpoint and its test.", ct: Ct);
            using var reader = kit.Launch.Db.OpenStore();
            await Wait.UntilAsync(() => reader.WorkflowDeclared(root.Seq), "the workflow to be declared complete", TimeSpan.FromMinutes(4));
            await Wait.UntilAsync(() => bus.IsQuiescentAsync(Ct).GetAwaiter().GetResult(), "the bus to settle", TimeSpan.FromMinutes(1));

            var output = TestContext.Current.TestOutputHelper!;
            var wf = reader.Workflow(root.Seq);
            foreach (var m in wf) output.WriteLine($"seq {m.Seq,3} depth {m.Depth} cause {m.CausationSeq?.ToString() ?? "-",3} {m.Source,-10} {m.Type,-28} {BatchText.TextOf(m)}");
            lock (log) foreach (var line in log) output.WriteLine("core: " + line);

            // ---- the bus: every run completed in its sandbox; tells at depth 1, handbacks at depth 2; one declaration
            using var store = kit.Launch.Db.OpenStore();
            var members = store.AllMembers().ToDictionary(m => m.Id);
            var runs = store.Runs().Where(r => members[r.MemberId].Team == "acme").ToList();
            foreach (var r in runs) output.WriteLine($"run {r.Id} {members[r.MemberId].Name} {r.State} {r.Reason}");
            Assert.All(runs, r => Assert.True(r.State == "completed", $"{members[r.MemberId].Name} run {r.Id}: {r.State} {r.Reason}"));
            var tellAda = Assert.Single(wf, m => m.Type == "agent.instruction.acme/ada");
            var tellBen = Assert.Single(wf, m => m.Type == "agent.instruction.acme/ben");
            Assert.All(new[] { tellAda, tellBen }, t => { Assert.Equal(root.Seq, t.CausationSeq); Assert.Equal(1, t.Depth); });
            var adaBack = Assert.Single(wf, m => m.Type == MessageTypes.Handback && m.Source == "acme/ada");
            var benBack = Assert.Single(wf, m => m.Type == MessageTypes.Handback && m.Source == "acme/ben");
            Assert.Equal(tellAda.Seq, adaBack.CausationSeq);
            Assert.Equal(tellBen.Seq, benBack.CausationSeq);
            Assert.Equal(2, benBack.Depth);
            Assert.Equal("acme/lead", Assert.Single(wf, m => m.Type == MessageTypes.WorkflowCompleted).Source);

            // ---- inside each member's sandbox: its own CLI and model host, git on its branch, no way to push
            var h = kit.Launch.Host;
            foreach (var (name, cli) in new[] { ("ada", AgentCli.Claude), ("ben", AgentCli.Grok) })
            {
                var layout = Yawble.Core.Policy.RunLayout.Standard(h.Os, h.Home, h.AppDataRoot, h.InstallDir,
                    new Yawble.Core.Policy.MemberRef("acme", name), cli, await kit.Launch.Launcher.DetectCliAsync(cli, false, Ct), []);
                Assert.Equal(repos!.MemberRoot("acme", name), Path.GetDirectoryName(layout.ClonePath));
                var o = JsonDocument.Parse(File.ReadAllText(Path.Combine(layout.Workspace, "standin-out.json"))).RootElement;
                output.WriteLine($"{name} stand-in: {o}");
                Assert.True(o.TryGetProperty("done", out _), $"{name}: {o}");
                Assert.Equal(cli == AgentCli.Claude ? "claude" : "grok", o.GetProperty("cli").GetString());
                Assert.Equal(200, o.GetProperty("steps").GetProperty("model").GetProperty("status").GetInt32());
                // The launcher started the CLI with the team prompt: who it is, its role text, its branch.
                var argv = string.Join(' ', o.GetProperty("argv").EnumerateArray().Select(a => a.GetString()));
                Assert.Contains($"You are {name}, a member of team acme", argv);
                Assert.Contains($"on branch members/{name}", argv);

                var git = o.GetProperty("steps").GetProperty("git").EnumerateArray().ToList();
                JsonElement G(params string[] args) => git.Single(g => g.GetProperty("args").EnumerateArray().Select(a => a.GetString()).SequenceEqual(args));
                int Code(JsonElement g) => g.GetProperty("code").GetInt32();
                Assert.Equal("members/" + name, G("symbolic-ref", "--short", "HEAD").GetProperty("out").GetString());
                Assert.All(git.Take(6), g => Assert.True(Code(g) == 0, $"{name}: {g}"));
                Assert.Equal("", G("remote", "-v").GetProperty("out").GetString());
                // The clone's empty helper is the last one and resets the list (a system-wide helper would come before it).
                Assert.Equal("file:.git/config", G("config", "--get-all", "--show-origin", "credential.helper").GetProperty("out").GetString()!.Split('\n')[^1].TrimEnd());
                Assert.Equal("", G("config", "--get", "credential.helper").GetProperty("out").GetString());
                Assert.All(git.Where(g => g.GetProperty("args")[0].GetString() == "push"), g => Assert.True(Code(g) != 0, $"{name}: a push succeeded: {g}"));
                var connect = o.GetProperty("steps").GetProperty("guardrails").GetProperty("connect github.com:443");
                Assert.Contains(" 403", connect.GetProperty("statusLine").GetString());
                var sha = G("rev-parse", "HEAD").GetProperty("out").GetString()!;
                // ---- the team's repository (the one the person picked) has the member's commit on its branch
                Assert.Equal(sha, AgentGit.Host(picked, "rev-parse", "members/" + name));
                Assert.Equal($"{name} (acme)", AgentGit.Host(picked, "log", "-1", "--format=%an", "members/" + name));
            }
            Assert.Contains("return {\"ok\": True}", AgentGit.Host(picked, "show", "members/ada:app/health.py"));
            Assert.Contains("def test_health", AgentGit.Host(picked, "show", "members/ben:tests/test_health.py"));
            Assert.Equal(["members/ada", "members/ben", "trunk"], // the Manager committed nothing
                AgentGit.Host(picked, "for-each-ref", "--format=%(refname:short)", "refs/heads/").Split('\n').Order());
            Assert.Equal("1", AgentGit.Host(picked, "rev-list", "--count", "trunk")); // merging is the person's
            Assert.Equal(picked, Path.GetFullPath(repos!.RepoOf(teamId)!.Path));
            output.WriteLine("team repo:\n" + AgentGit.Host(picked, "log", "--all", "--format=%h %d %an: %s"));

            // ---- nothing pushed: the person's origin has exactly what it had
            Assert.Equal(originBefore, AgentGit.Host(bare, "for-each-ref"));

            // ---- no remote and no git credential in any clone or home; the post-run check found nothing to remove
            foreach (var m in members.Values.Where(m => m.Team == "acme"))
            {
                var memberRoot = repos.MemberRoot("acme", m.Name);
                var clone = Path.Combine(memberRoot, "repo");
                Assert.Equal("", AgentGit.Host(clone, "remote"));
                var config = File.ReadAllText(Path.Combine(clone, ".git", "config"));
                Assert.DoesNotContain("[remote", config);
                Assert.Matches(@"\[credential\]\s*\n\s*helper\s*=\s*\n", config);
                foreach (var file in new[] { ".git-credentials", ".netrc", ".gitconfig", Path.Combine(".config", "git", "credentials") })
                    Assert.False(File.Exists(Path.Combine(memberRoot, "home", file)), $"{m.Name}: {file}");
                Assert.Equal("[]", repos.Row(m.Id)!.LastCheck);
            }

            // ---- the gateways refused the git host and logged it; nothing reached it
            var events = kit.Gateway.Log.Read();
            // Two per member: the raw CONNECT and git's own push over https, both through the gateway and both refused.
            Assert.Equal(4, events.Count(e => e.Kind == GatewayEventKinds.ConnectDenied && e.Target!.StartsWith("github.com", StringComparison.Ordinal)));
            Assert.DoesNotContain(events, e => e.Kind is GatewayEventKinds.Forward or GatewayEventKinds.ConnectAllowed
                && (e.Target ?? "").Contains("github", StringComparison.OrdinalIgnoreCase));
            RealSandbox.Report($"team flow, real sandboxes: runs {string.Join(", ", runs.Select(r => $"{members[r.MemberId].Name}#{r.Id}({r.State})"))}; " +
                $"branches {AgentGit.Host(picked, "for-each-ref", "--format=%(refname:short)=%(objectname:short)", "refs/heads/").Replace('\n', ' ')}; origin unchanged");
        }
        finally
        {
            TestFiles.DeleteTree(outside);
        }
    }
}
