using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Skills;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Policy;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// The Manager builds its own team (hire / hiring), every agent loads skills over the bus (skills_list / skills_get,
/// built-in and the person's own), and members share their branches through the team's read-only shared view.
/// </summary>
public class HiringAndSkillsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One MCP tools/call on the run's bus server; the tool's text, and whether it was an error.</summary>
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

    /// <summary>Runs the Manager once with <paramref name="script"/> as its agent and waits for the run to end.</summary>
    private static async Task WithManagerRun(TempDb db, SkillCatalog? skills, Func<RunContext, Task> script, Func<RunContext, Task>? member = null,
        Task? alsoWaitFor = null)
    {
        var done = new TaskCompletionSource();
        var runner = new ScriptedRunner();
        runner.On("Alpha/Manager", async (run, _) =>
        {
            try { await script(run); done.TrySetResult(); }
            catch (Exception e) { done.TrySetException(e); }
            return RunResult.Ok;
        });
        if (member is not null) runner.Default = async (run, _) => { await member(run); return RunResult.Ok; };
        await using var engine = new BusEngine(db.OpenStore(), runner, new BusOptions
        {
            SafetyPoll = TimeSpan.FromMilliseconds(200),
            Skills = skills,
            AvailableAgents = _ => Task.FromResult<IReadOnlyList<AgentChoice>>(
                [new AgentChoice("claude", true, "installed and signed in"), new AgentChoice("codex", true, "installed and signed in"),
                 new AgentChoice("antigravity", false, "pending")]),
        });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Manager", "build a hello world app", ct: Ct);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        if (alsoWaitFor is not null) await alsoWaitFor.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    [Fact]
    public async Task TheManagerSeesWhatItMayHireAndHiresMembersWithTheirJobs()
    {
        using var db = new TempDb();
        await WithManagerRun(db, null, async run =>
        {
            var (hiring, _) = await Call(run, "hiring", new { });
            Assert.Contains("\"cli\":\"codex\"", hiring);
            Assert.Contains("\"ready\":false", hiring);

            var (dev, devError) = await Call(run, "hire", new { name = "Dev", cli = "codex", job = "Developer: writes the console app" });
            Assert.False(devError, dev);
            Assert.Contains("hired Dev (codex)", dev);
            var (tester, _) = await Call(run, "hire", new { name = "Tess", job = "Tester: builds and checks it" });
            Assert.Contains("hired Tess (claude)", tester); // the Manager's own CLI by default

            var (again, againError) = await Call(run, "hire", new { name = "dev" });
            Assert.True(againError);
            Assert.Contains("exists", again);
            var (bad, badError) = await Call(run, "hire", new { name = "Con" });
            Assert.True(badError);
            Assert.Contains("bad_name", bad); // a Windows device name cannot be a member's folder
            var (cli, cliError) = await Call(run, "hire", new { name = "Zed", cli = "nonesuch" });
            Assert.True(cliError);
            Assert.Contains("bad_cli", cli);
        });
        using var store = db.OpenStore();
        var rows = store.MemberRows(store.Teams().Single().Id);
        Assert.Equal(["Manager", "Dev", "Tess"], rows.Select(r => r.Name));
        Assert.Equal("Developer: writes the console app", rows.Single(r => r.Name == "Dev").Instructions);
        Assert.Equal("member", rows.Single(r => r.Name == "Dev").Role);
    }

    [Fact]
    public async Task OnlyTheManagerHires_AndAMembersJobIsInItsPrompt()
    {
        using var db = new TempDb();
        string? memberPrompt = null;
        (string Text, bool Error) memberHire = default;
        var memberDone = new TaskCompletionSource();
        await WithManagerRun(db, new SkillCatalog(db.ConnectionString), async run =>
        {
            await Call(run, "hire", new { name = "Dev", job = "Developer: writes the console app" });
            await run.TellAsync("Dev", "write it", Ct);
        }, async run =>
        {
            memberPrompt = TeamPrompt.For(run);
            memberHire = await Call(run, "hire", new { name = "Sneaky" });
            memberDone.TrySetResult();
        }, memberDone.Task);
        Assert.True(memberHire.Error);
        Assert.Contains("only the Manager", memberHire.Text);
        Assert.Contains("Your standing job on the team: Developer: writes the console app", memberPrompt);
        Assert.Contains("team-git", memberPrompt);
        Assert.DoesNotContain("hiring (", memberPrompt); // a manager-only skill is not offered to a member
    }

    [Fact]
    public async Task SkillsAreListedAndLoadedByRole_AndThePersonsOwnReplaceABuiltIn()
    {
        using var db = new TempDb();
        var skills = new SkillCatalog(db.ConnectionString);
        Assert.Equal(["manager", "member", "board", "dev-tools", "hiring", "integrations", "recovery", "team-git", "test-credentials", "testing-a-web-app", "testing-console-apps", "wrap-up", "writing-skills"],
            SkillCatalog.BuiltIns.Select(s => s.Name));
        Assert.Contains("integrations", skills.Offered("manager", 1).Select(s => s.Name));
        Assert.Contains("integrations", skills.Offered("member", 1).Select(s => s.Name));
        Assert.Contains("action list", SkillCatalog.BuiltIns.Single(s => s.Name == "integrations").Body);
        Assert.Contains("sign in again", SkillCatalog.BuiltIns.Single(s => s.Name == "integrations").Body);
        Assert.Contains("never try to sign in yourself", SkillCatalog.BuiltIns.Single(s => s.Name == "integrations").Body);
        Assert.Contains("web_<name>", SkillCatalog.BuiltIns.Single(s => s.Name == "integrations").Body);
        Assert.Contains("missing access", SkillCatalog.BuiltIns.Single(s => s.Name == "integrations").Body);
        skills.Save("release-notes", "How to write release notes.", ["any"], "Write them short.", null);
        skills.Save("team-git", "Our own git rules.", ["manager", "member"], "Rebase before you hand back.", null);
        await WithManagerRun(db, skills, async run =>
        {
            var (list, _) = await Call(run, "skills_list", new { });
            Assert.Contains("hiring - ", list);
            Assert.Contains("release-notes - How to write release notes.", list);
            Assert.DoesNotContain("testing-console-apps", list); // members only
            Assert.StartsWith("# Manager", (await Call(run, "skills_get", new { name = "manager" })).Text);
            Assert.Equal("Rebase before you hand back.", (await Call(run, "skills_get", new { name = "team-git" })).Text);
            var (member, error) = await Call(run, "skills_get", new { name = "testing-console-apps" });
            Assert.True(error);
            Assert.Contains("no skill named", member);
        });
    }

    [Fact]
    public void ASkillNeedsASlugNameAOneLineDescriptionARoleAndABody()
    {
        using var db = new TempDb();
        var skills = new SkillCatalog(db.ConnectionString);
        Assert.Throws<ArgumentException>(() => skills.Save("Release Notes", "d", ["any"], "b", null));
        Assert.Throws<ArgumentException>(() => skills.Save("notes", "two\nlines", ["any"], "b", null));
        Assert.Throws<ArgumentException>(() => skills.Save("notes", "d", ["boss"], "b", null));
        Assert.Throws<ArgumentException>(() => skills.Save("notes", "d", [], "b", null));
        Assert.Throws<ArgumentException>(() => skills.Save("notes", "d", ["any"], "  ", null));
        var saved = skills.Save("notes", "d", ["member"], "b", null);
        Assert.Equal(SkillKinds.Custom, saved.Kind);
        Assert.True(skills.Delete(saved.Id!.Value));
        Assert.DoesNotContain(skills.All(), s => s.Name == "notes");
    }

    [Fact]
    public async Task TheSharedViewHoldsEveryMembersBranch_AndAMemberFetchesATeammatesWorkFromIt()
    {
        using var f = new TeamsFixture();
        var (_, ada, ben) = f.Team();
        var adaCheckout = await f.Repos.PrepareAsync(ada, Ct);
        var shared = f.Repos.SharedRepoPath("Alpha");
        Assert.True(Directory.Exists(Path.Combine(shared, "objects")), "the shared view is made with the first clone");

        File.WriteAllText(Path.Combine(adaCheckout.ClonePath, "hello.txt"), "hi");
        AgentGit.Ok(adaCheckout, "add", "hello.txt");
        AgentGit.Ok(adaCheckout, "commit", "-q", "-m", "hello");
        Assert.True((await f.Repos.FetchBackAsync(ada, Ct)).Fetched);
        Assert.Contains("refs/heads/members/alpha/ada", AgentGit.Host(shared, "for-each-ref", "--format=%(refname)"));

        // Members fetch from the bundle, not the repository: a path fetch starts git-upload-pack through sh.exe on
        // Windows, which cannot start in a ProcessContainer (microsoft/mxc#1061); reading a bundle is in-process.
        var bundle = Path.Combine(shared, TeamRepos.SharedBundleFile);
        Assert.True(File.Exists(bundle), "the shared view's bundle is written with every update");
        Assert.Contains("refs/heads/members/alpha/ada", AgentGit.Host(shared, "bundle", "list-heads", bundle));

        var benCheckout = await f.Repos.PrepareAsync(ben, Ct);
        AgentGit.Ok(benCheckout, "fetch", "-q", bundle, "members/alpha/ada");
        AgentGit.Ok(benCheckout, "merge", "-q", "--no-edit", "FETCH_HEAD");
        Assert.Equal("hi", File.ReadAllText(Path.Combine(benCheckout.ClonePath, "hello.txt")));
    }

    [Fact]
    public void TheSharedViewIsGrantedReadOnly_WithItsPathInTheEnvironment_AndNeverWritable()
    {
        var run = PolicyFixtures.Run(TargetOs.Windows, CredentialMode.ClaudeHostSignIn);
        run = run with { Layout = run.Layout with { SharedRepo = run.Layout.SharedRepoCandidate } };
        var compiled = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, run);
        Assert.Empty(SandboxRequestInvariants.Check(compiled, run));
        var shared = run.Layout.SharedRepo!;
        Assert.EndsWith(@"\teams\alpha\shared.git", shared);
        Assert.Contains(shared, compiled.Request["filesystem"]!["readonlyPaths"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("YAWBLE_TEAM_REPO=" + shared, compiled.Request["process"]!["env"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains(@"YAWBLE_TEAM_BUNDLE=" + shared + @"\team.bundle", compiled.Request["process"]!["env"]!.AsArray().Select(n => n!.GetValue<string>()));

        var writable = (JsonObject)compiled.Request.DeepClone();
        writable["filesystem"]!["readwritePaths"]!.AsArray().Add(shared);
        writable["filesystem"]!["readonlyPaths"] = new JsonArray(writable["filesystem"]!["readonlyPaths"]!.AsArray()
            .Where(n => n!.GetValue<string>() != shared).Select(n => n!.DeepClone()).ToArray());
        Assert.Contains(SandboxRequestInvariants.CheckRequest(writable, run), p => p.Contains("inside the app data root"));

        var otherTeam = run with { Layout = run.Layout with { SharedRepo = @"C:\Users\alice\AppData\Local\Yawble\teams\beta\shared.git" } };
        Assert.Throws<InvalidOperationException>(() => SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, otherTeam));
    }

    [Fact]
    public void AntigravityRunsDenyItsOwnCommandTool_AndGetTheShellServer_AndARuleThatSaysSo()
    {
        var layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Antigravity);
        var argv = AgentPresets.Arguments(layout, CredentialMode.AntigravityHostSignIn, RunKind.Headless, "hi", "s-1", null);
        var settings = argv[argv.ToList().IndexOf("--settings") + 1];
        Assert.Equal("command(*)", JsonNode.Parse(settings)!["permissions"]!["deny"]!.AsArray().Single()!.GetValue<string>());

        var seeds = AgentPresets.Seeds(layout, CredentialMode.AntigravityHostSignIn);
        var mcp = JsonNode.Parse(seeds.Single(s => s.Path.EndsWith("mcp_config.json")).Content)!["mcpServers"]!;
        Assert.Equal("--shell", mcp["shell"]!["args"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal(mcp["bus"]!["command"]!.GetValue<string>(), mcp["shell"]!["command"]!.GetValue<string>());
        var rule = seeds.Single(s => s.Path.EndsWith("yawble-commands.md")).Content;
        Assert.StartsWith("---\ntrigger: always_on\n---", rule);
        Assert.Contains("run_shell", rule);
    }

    [Fact]
    public void CodexAndCopilotRunsGetTheirOwnSignIn_AndTheirVendorHostsOnlyForThemselves()
    {
        var codex = PolicyFixtures.Run(TargetOs.Windows, CredentialMode.CodexHostSignIn);
        var c = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, codex);
        var env = c.Request["process"]!["env"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("CODEX_HOME=" + AgentPresets.CodexHome(codex.Layout), env);
        Assert.Contains("chatgpt.com", c.Gateway.AllowHosts);
        Assert.Contains(c.Seeds, s => s.Path.EndsWith("config.toml") && s.Content.Contains("[mcp_servers.bus]") && s.Content.Contains("env_vars"));
        Assert.DoesNotContain(c.Seeds, s => s.Path.EndsWith("auth.json")); // copied by the launcher, never seeded

        var copilot = PolicyFixtures.Run(TargetOs.Windows, CredentialMode.CopilotHostSignIn);
        var p = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, copilot);
        Assert.Contains(AgentPresets.CopilotTokenVariable + "=" + PolicyFixtures.CopilotToken,
            p.Request["process"]!["env"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("api.githubcopilot.com", p.Gateway.AllowHosts);
        Assert.DoesNotContain("chatgpt.com", p.Gateway.AllowHosts);

        var claude = SandboxRequestCompiler.Compile(PolicyFixtures.Global, null, null, PolicyFixtures.Run(TargetOs.Windows, CredentialMode.ClaudeHostSignIn));
        Assert.DoesNotContain("chatgpt.com", claude.Gateway.AllowHosts);
        Assert.DoesNotContain("api.githubcopilot.com", claude.Gateway.AllowHosts);
        // No policy can add them either: the floor rules refuse wildcards, and the vendor hosts stay preset-only.
        Assert.NotNull(FloorRules.HostProblem("*.githubcopilot.com"));
    }

    [Fact]
    public async Task ACodexCopyTheRunRefreshedGoesBackToTheHost_AnOlderOneNeverDoes()
    {
        using var db = new TempDb();
        var home = Path.Combine(db.Dir, "home");
        var hostFile = Path.Combine(home, ".codex", "auth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(hostFile)!);
        var now = DateTimeOffset.UtcNow;
        static string Auth(DateTimeOffset at, string token) => $$"""{"tokens":{"refresh_token":"{{token}}"},"last_refresh":"{{at:O}}"}""";
        File.WriteAllText(hostFile, Auth(now.AddDays(-1), "host"));

        var layout = PolicyFixtures.Layout(TargetOs.Windows, AgentCli.Codex, signIn: true);
        var workspace = Path.Combine(db.Dir, "member-home");
        layout = layout with
        {
            Workspace = workspace,
            CliInstall = layout.CliInstall with
            {
                SignIn = layout.CliInstall.SignIn! with { Files = [new Yawble.Core.Agents.CliSignInFile(hostFile, Yawble.Core.Agents.SignInAccess.CopyIn, true, "Codex")] },
            },
        };
        var copyPath = Path.Combine(workspace, ".codex", "auth.json");

        await using (await SignInCopy.PrepareAsync(layout, TimeProvider.System, null, Ct))
        {
            Assert.Equal(File.ReadAllText(hostFile), File.ReadAllText(copyPath));
            File.WriteAllText(copyPath, Auth(now, "refreshed")); // the CLI refreshed during the run
        }
        Assert.Contains("refreshed", File.ReadAllText(hostFile));

        await using (await SignInCopy.PrepareAsync(layout, TimeProvider.System, null, Ct))
            File.WriteAllText(copyPath, Auth(now.AddDays(-30), "stale"));
        Assert.Contains("refreshed", File.ReadAllText(hostFile)); // an older copy never replaces the host's
    }

    [Fact]
    public void HostToolsAreTheFoldersOfGitDotnetNodeAndPython_NeverTheAppsOrTheStoreAlias()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("host tools are detected on Windows only");
        using var db = new TempDb();
        string Dir(string name, params string[] exes)
        {
            var d = Path.Combine(db.Dir, name);
            Directory.CreateDirectory(d);
            foreach (var e in exes) File.WriteAllText(Path.Combine(d, e + ".exe"), "");
            return d;
        }
        var app = Path.Combine(db.Dir, "app");
        var inApp = Dir(Path.Combine("app", "bin"), "git");
        var store = Dir(Path.Combine("WindowsApps"), "python");
        var git = Dir("git", "git");
        var dotnet = Dir("dotnet", "dotnet");
        var node = Dir("node", "node", "npm");
        var path = string.Join(';', inApp, store, git, dotnet, node, git);
        Assert.Equal([git, dotnet, node], HostTools.Detect(app, path, home: Path.Combine(db.Dir, "home")));
    }

    [Fact]
    public void AToolFolderSavedWithAVariable_IsFound()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("host tools are detected on Windows only");
        using var db = new TempDb();
        var tools = Path.Combine(db.Dir, "tools");
        Directory.CreateDirectory(Path.Combine(tools, "dotnet"));
        File.WriteAllText(Path.Combine(tools, "dotnet", "dotnet.exe"), "");
        // The saved PATH keeps entries such as %ProgramFiles%\dotnet unexpanded.
        Environment.SetEnvironmentVariable("YAWBLE_TEST_TOOLS", tools);
        try
        {
            Assert.Equal([Path.Combine(tools, "dotnet")],
                HostTools.Detect(Path.Combine(db.Dir, "app"), @"%YAWBLE_TEST_TOOLS%\dotnet", home: Path.Combine(db.Dir, "home")));
        }
        finally { Environment.SetEnvironmentVariable("YAWBLE_TEST_TOOLS", null); }
    }
}
