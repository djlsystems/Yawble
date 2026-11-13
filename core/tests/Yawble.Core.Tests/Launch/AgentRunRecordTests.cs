using System.Text.Json.Nodes;
using Yawble.Core.Agents;
using Yawble.Core.Agents.Profiles;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Yawble.Core.Launch;
using Yawble.Core.Policy;
using Yawble.Core.Provenance;
using Yawble.Core.Tests.Agents;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>Card 46 (b)-(d): default model resolution, run rows with agent/model/version/argv, and the core API. Fakes only: no ports, no CLI.</summary>
public class AgentRunRecordTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeTerminal : IUpdateTerminal
    {
        public List<IReadOnlyList<string>> Opened { get; } = [];
        public void Open(string title, IReadOnlyList<string> command) => Opened.Add(command);
    }

    private static void SetMemberModel(LaunchKit kit, string member, string model)
    {
        var id = new SqliteMemberDirectory(kit.Db.ConnectionString).Find("alpha", member)!.Id;
        new BusStore(kit.Db.ConnectionString).UpdateMember(id, "claude", "", model, "default");
    }

    private static async Task<RunRecordStore.Record> RunDev1Async(LaunchKit kit, string prompt = "do the work")
    {
        var before = kit.Records.All().Count(r => r.Member == "dev1");
        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };
        await kit.Bus.PersonTellAsync("alpha", "dev1", prompt, ct: Ct);
        await Wait.UntilAsync(() => kit.Records.All().Count(r => r.Member == "dev1" && r.EndClass is not null) > before, "dev1's run");
        return kit.Records.All().Last(r => r.Member == "dev1");
    }

    private static string[] Argv(RunRecordStore.Record r) => JsonNode.Parse(r.Argv!)!.AsArray().Select(a => a!.GetValue<string>()).ToArray();

    [Fact]
    public void Resolution_order_is_member_then_agent_then_cli()
    {
        Assert.Equal(new ModelResolution("opus", ModelSources.Member), AgentModels.Resolve("opus", "sonnet"));
        Assert.Equal(new ModelResolution("sonnet", ModelSources.Agent), AgentModels.Resolve("", "sonnet"));
        Assert.Equal(new ModelResolution("sonnet", ModelSources.Agent), AgentModels.Resolve(null, " sonnet "));
        Assert.Equal(new ModelResolution("", ModelSources.Cli), AgentModels.Resolve(" ", ""));
    }

    [Fact]
    public async Task A_run_without_any_model_records_the_cli_default_and_passes_no_model_flag()
    {
        await using var kit = await LaunchKit.StartAsync();
        var r = await RunDev1Async(kit);
        Assert.Equal("claude", r.Agent);
        Assert.Equal("", r.Model);
        Assert.Equal(ModelSources.Cli, r.ModelSource);
        Assert.Equal("2.1.318", r.CliVersion);
        Assert.DoesNotContain("--model", Argv(r));
        Assert.NotNull(r.StartedAt);
        Assert.NotNull(r.EndedAt);
        Assert.EndsWith("+00:00", r.StartedAt);
    }

    [Fact]
    public async Task The_agent_default_applies_and_a_member_override_beats_it_and_both_are_launched_and_recorded()
    {
        await using var kit = await LaunchKit.StartAsync();
        kit.Defaults.Set(AgentCli.Claude, "sonnet");
        var viaAgent = await RunDev1Async(kit);
        Assert.Equal(("sonnet", ModelSources.Agent), (viaAgent.Model, viaAgent.ModelSource));
        Assert.Equal("sonnet", Argv(viaAgent)[Array.IndexOf(Argv(viaAgent), "--model") + 1]);

        SetMemberModel(kit, "dev1", "opus");
        var viaMember = await RunDev1Async(kit);
        Assert.Equal(("opus", ModelSources.Member), (viaMember.Model, viaMember.ModelSource));
        Assert.Equal("opus", Argv(viaMember)[Array.IndexOf(Argv(viaMember), "--model") + 1]);

        // Another agent's default does not leak: dev2 is a grok member with no default of its own.
        kit.Backend.Script = (_, p) => { p.Exit(0); return Task.CompletedTask; };
        await kit.Bus.PersonTellAsync("alpha", "dev2", "hello", ct: Ct);
        await Wait.UntilAsync(() => kit.Records.All().Any(r => r.Member == "dev2" && r.EndClass is not null), "dev2's run");
        Assert.Equal(ModelSources.Cli, kit.Records.All().Last(r => r.Member == "dev2").ModelSource);
    }

    [Fact]
    public async Task The_recorded_argv_cuts_the_prompt_to_200_chars_and_holds_no_run_token()
    {
        await using var kit = await LaunchKit.StartAsync();
        var r = await RunDev1Async(kit, new string('x', 900));
        var argv = Argv(r);
        var gateway = kit.Gateways.Started.Single(g => g.Run.Member == "dev1");
        Assert.DoesNotContain(argv, a => a.Contains(gateway.RunToken));
        Assert.DoesNotContain(gateway.RunToken, r.Argv);
        Assert.All(argv, a => Assert.True(a.Length <= RunArgv.PromptLimit + 1 || a.Contains("mcp.json") || a.Contains(".exe") || a.Length < 260, a));
        Assert.DoesNotContain(argv, a => a.Length > 400);
    }

    [Fact]
    public void Redaction_cuts_the_prompt_and_hides_secrets_and_token_shapes()
    {
        var prompt = new string('p', 500) + " sk-ant-abcdefghijkl";
        var argv = RunArgv.ForRecord(["claude", "-p", prompt, "--token=tok-12345678", "gho_abcdefghijkl"], prompt, ["tok-12345678"]);
        Assert.Equal(RunArgv.PromptLimit + 1, argv[2].Length);
        Assert.Equal("--token=" + RunArgv.Redacted, argv[3]);
        Assert.Equal(RunArgv.Redacted, argv[4]);
        Assert.DoesNotContain("sk-ant", string.Join(' ', argv));
    }

    [Fact]
    public void The_model_list_is_the_apps_own_for_every_cli_and_every_item_is_a_valid_model()
    {
        var catalog = new ModelCatalog();
        foreach (var cli in Enum.GetValues<AgentCli>())
        {
            var listing = catalog.List(cli);
            Assert.Equal("app", listing.Source);
            Assert.NotEmpty(listing.Models);
            Assert.All(listing.Models, m => Assert.True(AgentModels.IsValidModel(m), m));
        }
        Assert.Equal(["opus", "sonnet", "haiku"], catalog.List(AgentCli.Claude).Models.Take(3));
    }

    [Fact]
    public async Task Asking_for_models_runs_no_cli_and_looks_nothing_up_from_the_window_or_over_the_cloud_link()
    {
        await using var kit = await LaunchKit.StartAsync();
        // A detector that finds every CLI: if listing asked a CLI it would have to find it first.
        var clis = new FakeCliDetector([.. Enum.GetValues<AgentCli>().Select(c => TestClis.Found(c.Id(), c.Id() + ".exe", [], []))]);
        var terminal = new FakeTerminal();
        var rpc = new AgentInfoRpc(kit.Launcher, kit.Defaults, new ModelCatalog(), clis, kit.Records,
            new SqliteMemberDirectory(kit.Db.ConnectionString), terminal);
        foreach (var actor in new[] { Actor.Window, new Actor(Actor.Cloud, "viewer"), new Actor(Actor.Agent, null), new Actor(Actor.Concierge, null) })
        {
            using (Actor.Use(actor))
            {
                foreach (var cli in new[] { "claude", "grok", "codex", "copilot", "antigravity" })
                {
                    // What Settings > Agents and Member settings call when they open and after a save.
                    var models = await rpc.DispatchAsync("agents.models", new JsonObject { ["cli"] = cli }, Ct);
                    Assert.Equal("app", models!["source"]!.GetValue<string>());
                    await rpc.DispatchAsync("agents.defaultModels", null, Ct);
                }
            }
        }
        Assert.Equal(0, clis.Calls);
        Assert.Empty(terminal.Opened);
    }

    [Fact]
    public async Task The_rpc_shows_the_launched_command_line_resolves_models_and_refuses_what_is_not_the_persons()
    {
        await using var kit = await LaunchKit.StartAsync();
        var terminal = new FakeTerminal();
        var rpc = new AgentInfoRpc(kit.Launcher, kit.Defaults, new ModelCatalog(), kit.Clis,
            kit.Records, new SqliteMemberDirectory(kit.Db.ConnectionString), terminal);
        var window = Actor.Window;

        using (Actor.Use(new Actor(Actor.Agent, null)))
        {
            await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("agents.setDefaultModel", new JsonObject { ["cli"] = "claude", ["model"] = "haiku" }, Ct));
            await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("agents.checkForUpdates", new JsonObject { ["cli"] = "claude" }, Ct));
        }
        Assert.Empty(terminal.Opened);
        Assert.Equal("", kit.Defaults.Get(AgentCli.Claude));

        var changes = new List<(AgentCli, string, string)>();
        kit.Defaults.Changed += (c, from, to) => changes.Add((c, from, to));
        using (Actor.Use(window))
        {
            var set = await rpc.DispatchAsync("agents.setDefaultModel", new JsonObject { ["cli"] = "claude", ["model"] = "haiku" }, Ct);
            Assert.Equal("", set!["previous"]!.GetValue<string>());
            await rpc.DispatchAsync("agents.setDefaultModel", new JsonObject { ["cli"] = "claude", ["model"] = "haiku" }, Ct); // no change: no event
            await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("agents.setDefaultModel", new JsonObject { ["cli"] = "claude", ["model"] = "a b; rm" }, Ct));

            var resolved = await rpc.DispatchAsync("agents.resolveModel", new JsonObject { ["team"] = "alpha", ["member"] = "dev1" }, Ct);
            Assert.Equal(("haiku", "agent", "Agent default (haiku)"), (resolved!["model"]!.GetValue<string>(), resolved["source"]!.GetValue<string>(), resolved["label"]!.GetValue<string>()));

            var shown = await rpc.DispatchAsync("agents.commandLine", new JsonObject { ["team"] = "alpha", ["member"] = "dev1" }, Ct);
            var headless = shown!["headless"]!["argv"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
            Assert.Contains("<prompt>", headless);
            Assert.Contains("<session id>", headless);
            Assert.Contains("<workspace>/.yawble/mcp.json", headless);
            Assert.Equal("haiku", headless[headless.IndexOf("--model") + 1]);
            Assert.Contains("CLAUDE_CONFIG_DIR", shown["headless"]!["env"]!.AsArray().Select(a => a!.GetValue<string>()));
            Assert.DoesNotContain("-p", shown["interactive"]!["argv"]!.AsArray().Select(a => a!.GetValue<string>()));

            var models = await rpc.DispatchAsync("agents.models", new JsonObject { ["cli"] = "grok" }, Ct);
            Assert.Equal("app", models!["source"]!.GetValue<string>());
            var all = await rpc.DispatchAsync("agents.defaultModels", null, Ct);
            Assert.Equal("haiku", all!["claude"]!.GetValue<string>());

            var update = await rpc.DispatchAsync("agents.checkForUpdates", new JsonObject { ["cli"] = "claude" }, Ct);
            Assert.True(update!["opened"]!.GetValue<bool>());
        }
        Assert.Equal([(AgentCli.Claude, "", "haiku")], changes);
        Assert.Equal("update", Assert.Single(terminal.Opened)[^1]);
    }

    [Fact]
    public async Task Version_changed_since_the_last_run_is_reported_from_the_run_rows()
    {
        await using var kit = await LaunchKit.StartAsync();
        var rpc = (ICliDetector clis) => new AgentInfoRpc(kit.Launcher, kit.Defaults, new ModelCatalog(), clis, kit.Records,
            new SqliteMemberDirectory(kit.Db.ConnectionString), new FakeTerminal());
        var same = await rpc(kit.Clis).DispatchAsync("agents.versionStatus", new JsonObject { ["cli"] = "claude" }, Ct);
        Assert.False(same!["changed"]!.GetValue<bool>()); // no run yet
        await RunDev1Async(kit);
        same = await rpc(kit.Clis).DispatchAsync("agents.versionStatus", new JsonObject { ["cli"] = "claude" }, Ct);
        Assert.Equal(("2.1.318", "2.1.318", false), (same!["detected"]!.GetValue<string>(), same["lastRun"]!.GetValue<string>(), same["changed"]!.GetValue<bool>()));
        var updated = new FakeCliDetector(TestClis.Found("claude", "claude.exe", [], [], version: "2.2.0"));
        var changed = await rpc(updated).DispatchAsync("agents.versionStatus", new JsonObject { ["cli"] = "claude" }, Ct);
        Assert.True(changed!["changed"]!.GetValue<bool>());
        Assert.Equal("2.2.0", changed["detected"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_cloud_viewer_is_not_shown_the_agent_terminal_sessions_run_records()
    {
        await using var kit = await LaunchKit.StartAsync();
        kit.Records.Started("r-member", null, "alpha", "dev1", "claude", "headless", "fake");
        kit.Records.Started("r-test", null, AgentTestSessions.Team, "claude", "claude", "interactive", "fake");
        var rpc = new AgentInfoRpc(kit.Launcher, kit.Defaults, new ModelCatalog(), kit.Clis, kit.Records,
            new SqliteMemberDirectory(kit.Db.ConnectionString), new FakeTerminal());
        async Task<string[]> Keys(Actor actor, JsonObject? p)
        {
            using var _ = Actor.Use(actor);
            return (await rpc.DispatchAsync("agents.runRecords", p, Ct))!.AsArray().Select(r => r!["runKey"]!.GetValue<string>()).ToArray();
        }

        Assert.Equal(["r-test", "r-member"], await Keys(Actor.Window, null));
        Assert.Equal(["r-member"], await Keys(Actor.CloudViewer("ines"), null));
        Assert.Empty(await Keys(Actor.CloudViewer("ines"), new JsonObject { ["team"] = AgentTestSessions.Team }));
        Assert.Empty(await Keys(Actor.CloudViewer("ines"), new JsonObject { ["team"] = "_AGENT-TEST", ["member"] = "claude" }));
        // The limit counts what the viewer is shown: the hidden row does not take the only place.
        Assert.Equal(["r-member"], await Keys(Actor.CloudViewer("ines"), new JsonObject { ["limit"] = 1 }));
    }

    [Fact]
    public async Task Run_records_are_listed_through_the_rpc_with_agent_model_version_and_argv()
    {
        await using var kit = await LaunchKit.StartAsync();
        kit.Defaults.Set(AgentCli.Claude, "sonnet");
        await RunDev1Async(kit);
        var rpc = new AgentInfoRpc(kit.Launcher, kit.Defaults, new ModelCatalog(), kit.Clis, kit.Records,
            new SqliteMemberDirectory(kit.Db.ConnectionString), new FakeTerminal());
        var rows = (await rpc.DispatchAsync("agents.runRecords", new JsonObject { ["team"] = "alpha", ["member"] = "dev1" }, Ct))!.AsArray();
        var row = Assert.Single(rows)!;
        Assert.Equal(("claude", "sonnet", "agent", "2.1.318"),
            (row["agent"]!.GetValue<string>(), row["model"]!.GetValue<string>(), row["modelSource"]!.GetValue<string>(), row["cliVersion"]!.GetValue<string>()));
        Assert.Contains("--model", row["argv"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.NotNull(row["startedAt"]);
        Assert.NotNull(row["endedAt"]);
    }

    private static List<string> Items(JsonNode? a) => a!.AsArray().Select(x => x!.GetValue<string>()).ToList();

    [Fact]
    public async Task The_command_line_comes_in_groups_and_a_draft_is_described_with_its_changes()
    {
        await using var kit = await LaunchKit.StartAsync();
        var rpc = new AgentInfoRpc(kit.Launcher, kit.Defaults, new ModelCatalog(), kit.Clis, kit.Records,
            new SqliteMemberDirectory(kit.Db.ConnectionString), new FakeTerminal());

        var claude = await rpc.DispatchAsync("agents.commandLine", new JsonObject { ["cli"] = "claude" }, Ct);
        var groups = claude!["headless"]!["groups"]!.AsArray();
        var mcp = groups.Single(g => Items(g!["tokens"])[0] == "--mcp-config")!;
        Assert.True(mcp["locked"]!.GetValue<bool>());
        Assert.False(mcp["removed"]!.GetValue<bool>());
        Assert.Equal(Items(claude["headless"]!["argv"]), [.. Items(claude["headless"]!["launch"]), .. groups.SelectMany(g => Items(g!["tokens"]))]);
        Assert.True(claude["interactive"]!["groups"]!.AsArray().Single(g => g!["tail"]!.GetValue<bool>())!["locked"]!.GetValue<bool>());

        // (Codex's unlocked update-check group is covered in CommandLineTests: this kit's fake credentials give non-Claude CLIs Grok's mode.)

        var draft = await rpc.DispatchAsync("agents.commandLine", new JsonObject
        {
            ["cli"] = "grok", ["model"] = "grok-4", ["argsBefore"] = new JsonArray(), ["argsAfter"] = new JsonArray("--flag=value with space"),
            ["removedArgs"] = new JsonArray("--no-auto-update"),
        }, Ct);
        var removed = draft!["headless"]!["groups"]!.AsArray().Single(g => Items(g!["tokens"])[0] == "--no-auto-update")!;
        Assert.True(removed["removed"]!.GetValue<bool>());
        var argv = Items(draft["headless"]!["argv"]);
        Assert.DoesNotContain("--no-auto-update", argv);
        Assert.Contains("--flag=value with space", argv);
        Assert.Equal("grok-4", argv[argv.IndexOf("--model") + 1]);
        Assert.Equal(("grok-4", "agent"), (draft["model"]!["model"]!.GetValue<string>(), draft["model"]!["source"]!.GetValue<string>()));
        Assert.DoesNotContain("--no-auto-update", Items(draft["interactive"]!["argv"]));

        var refused = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("agents.commandLine", new JsonObject
        {
            ["cli"] = "claude", ["argsAfter"] = new JsonArray("--mcp-config", "x.json"),
        }, Ct));
        Assert.Equal("bad_params", refused.Code);
        Assert.Contains("--mcp-config", refused.Message);

        // A model that is not a string is bad_params (not an InvalidOperationException), with or without a draft.
        foreach (var asked in new[] { new JsonObject { ["cli"] = "claude", ["model"] = 5 },
                     new JsonObject { ["cli"] = "claude", ["model"] = new JsonArray("x"), ["argsAfter"] = new JsonArray() } })
            Assert.Equal("bad_params", (await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("agents.commandLine", asked, Ct))).Code);
    }

    [Fact]
    public async Task A_custom_agents_name_as_cli_still_shows_its_own_command_line()
    {
        await using var kit = await LaunchKit.StartAsync();
        var cs = kit.Db.ConnectionString;
        AgentProfileStore? store = null;
        var log = new AgentChangeLog(cs, null, (a, m) => store!.ResolveModel(a, m));
        store = new AgentProfileStore(cs, log, new SqliteAgentUsage(cs));
        var mine = store.Duplicate(AgentCli.Grok, "Mine");
        store.Update(mine.Id, mine.Name, "grok-4", ["--custom-before=1"], [], ["--no-auto-update"]);
        var rpc = new AgentInfoRpc(kit.Launcher, kit.Defaults, new ModelCatalog(), kit.Clis, kit.Records,
            new SqliteMemberDirectory(cs), new FakeTerminal(), store);

        var shown = await rpc.DispatchAsync("agents.commandLine", new JsonObject { ["cli"] = "Mine" }, Ct);
        Assert.Equal(("grok", "Mine"), (shown!["cli"]!.GetValue<string>(), shown["agent"]!.GetValue<string>()));
        var argv = Items(shown["headless"]!["argv"]);
        Assert.Contains("--custom-before=1", argv);
        Assert.DoesNotContain("--no-auto-update", argv);
        Assert.Equal("Agent default (grok-4)", shown["model"]!["label"]!.GetValue<string>());
        Assert.True(shown["headless"]!["groups"]!.AsArray().Single(g => Items(g!["tokens"])[0] == "--no-auto-update")!["removed"]!.GetValue<bool>());
    }
}
