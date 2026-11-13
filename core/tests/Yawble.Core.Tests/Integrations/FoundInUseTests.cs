using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>An undecided call asks; always / keep asking / never save a "found in use" row; gh's built-in PR rows.</summary>
public class FoundInUseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A small MCP server: its tools are echo and frobnicate (nothing in the name says what it does).</summary>
    private const string FakeServer = """
        const rl = require('readline').createInterface({ input: process.stdin });
        const send = (m) => process.stdout.write(JSON.stringify(m) + '\n');
        rl.on('line', (line) => {
          const m = JSON.parse(line);
          if (m.method === 'initialize') send({ jsonrpc: '2.0', id: m.id, result: { protocolVersion: m.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'fake', version: '1' } } });
          else if (m.method === 'tools/list') send({ jsonrpc: '2.0', id: m.id, result: { tools: [
            { name: 'echo', description: 'Says it back.', inputSchema: { type: 'object', properties: {} } },
            { name: 'frobnicate', description: 'Frobnicates.', inputSchema: { type: 'object', properties: {} } } ] } });
          else if (m.method === 'tools/call') send({ jsonrpc: '2.0', id: m.id, result: { content: [{ type: 'text', text: 'ran ' + m.params.name }] } });
        });
        """;

    /// <summary>Two teams (Alpha/Ada, Beta/Bo) and a fake CLI "fk" offered to both, that prints its arguments. Calls go through the app's MCP server.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public BusEngine Engine = null!;
        public IntegrationService Integrations = null!;
        public long Alpha, Beta;
        private readonly ConcurrentDictionary<int, (string Tool, object Args, TaskCompletionSource<string> Done)> _calls = new();
        private int _next;

        public TeamRepos? Repos;

        public static async Task<Rig> Make(bool withRepos = false)
        {
            var r = new Rig();
            var repos = withRepos ? r.Repos = new TeamRepos(r.Db.Dir, r.Db.ConnectionString) : null;
            var runner = new ScriptedRunner { Default = r.Handle };
            r.Engine = new BusEngine(r.Db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false, Repos = repos });
            r.Integrations = new IntegrationService(r.Db.ConnectionString, r.Engine);
            r.Engine.Integrations = r.Integrations;
            r.Alpha = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await r.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            r.Beta = await r.Engine.CreateTeamAsync("Beta", ct: Ct);
            await r.Engine.AddMemberAsync("Beta", "Bo", ct: Ct);
            await r.Engine.StartAsync(Ct);
            return r;
        }

        /// <summary>Adds a fake CLI (by default "fk"; a script that echoes its arguments) and offers it to both teams.</summary>
        public async Task AddFakeCli(string name = "fk")
        {
            string path;
            if (OperatingSystem.IsWindows())
            {
                path = Path.Combine(Db.Dir, name + ".cmd");
                File.WriteAllText(path, "@echo off\r\necho ran %*\r\n");
            }
            else
            {
                path = Path.Combine(Db.Dir, name);
                File.WriteAllText(path, "#!/bin/sh\necho ran \"$@\"\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO integrations (kind, name, path, created_at, updated_at) VALUES ('cli', $n, $p, '2026-01-01', '2026-01-01');";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$p", path);
                cmd.ExecuteNonQuery();
            }
            foreach (var t in new[] { Alpha, Beta })
                await Rpc("integrations.enable", new JsonObject { ["name"] = name, ["teamId"] = t, ["on"] = true });
        }

        private async Task<RunResult> Handle(RunContext run, CancellationToken ct)
        {
            // Every item of the batch: a call told while the member's previous run is still ending waits and can be batched
            // behind another pending message of the workflow (an approval notice, say).
            foreach (var (id, call) in _calls)
            {
                if (!run.Items.Any(m => m.Payload.Contains($"call#{id}#", StringComparison.Ordinal))) continue;
                _calls.TryRemove(id, out _);
                try { call.Done.TrySetResult(await CallTool(run, call.Tool, call.Args)); }
                catch (Exception e) { call.Done.TrySetException(e); }
            }
            return RunResult.Ok;
        }

        private static async Task<string> CallTool(RunContext run, string tool, object args)
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(args) },
            };
            var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
            return JsonNode.Parse(reply.Json!)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        }

        /// <summary>The member makes the call in a run of its own; the text it reads.</summary>
        public async Task<string> CallAs(string team, string member, string tool, object args)
        {
            var id = Interlocked.Increment(ref _next);
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = (tool, args, done);
            await Engine.PersonTellAsync(team, member, $"call#{id}#", ct: Ct);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        public Task<string> Fk(string team, params string[] args) =>
            CallAs(team, team == "Alpha" ? "Ada" : "Bo", "fk", new { args, reason = "needed" });

        /// <summary>A row as a walk of the help would have found it (origin discovered).</summary>
        public void AddDiscoveredRow(string cli, string path) =>
            new DiscoverStore(Db.ConnectionString, TimeProvider.System).UpsertRow(Scalar($"SELECT id FROM integrations WHERE name = '{cli}'"), path,
                string.Join(' ', path.Split(' ')[..^1]), "", CommandClassifier.Classify(cli, CliCatalog.Of(cli), path, false), RowOrigin.Discovered);

        public async Task<JsonNode?> Rpc(string method, JsonObject p) => await Integrations.DispatchAsync(method, p, Ct);

        public JsonObject PendingAsk(long team) => (JsonObject)Assert.Single((JsonArray)(Rpc("integrations.asks", new JsonObject { ["teamId"] = team }).Result)!)!;

        public ChecklistNode Node(string cli, long? team, string path) => Integrations.Discover.GetChecklist(cli, team).Nodes.Single(n => n.Path == path);

        /// <summary>Whether Ada was told a text containing this, in the workflow of her first ask.</summary>
        public bool Told(Func<Message, bool> match)
        {
            using var s = Db.OpenStore();
            return s.Workflow(Integrations.Asks(Alpha, false).First().Workflow).Any(match);
        }

        public long Scalar(string sql)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public async ValueTask DisposeAsync()
        {
            await Wait.UntilAsync(() => Engine.IsQuiescentAsync(Ct), "the runs ended");
            await Integrations.DisposeAsync();
            await Engine.DisposeAsync();
            Db.Dispose();
        }
    }

    [Fact]
    public void FoundPath_IsTheLongestKnownPrefix_OrLeadingWords_OrNullWhenAFlagComesFirst()
    {
        Assert.Equal("pr lock", DiscoverService.FoundPath(["pr", "lock", "3"], []));
        Assert.Equal("pr list", DiscoverService.FoundPath(["pr", "list", "--state", "open"], []));
        Assert.Equal("version", DiscoverService.FoundPath(["version"], []));
        Assert.Equal("repo deploy-key list", DiscoverService.FoundPath(["repo", "deploy-key", "list"], ["repo deploy-key list", "repo"]));
        Assert.Equal("repo deploy-key list", DiscoverService.FoundPath(["Repo", "deploy-key", "LIST", "--json"], ["repo deploy-key", "repo deploy-key list"]));
        Assert.Null(DiscoverService.FoundPath(["--repo", "x", "pr", "list"], []));
        Assert.Null(DiscoverService.FoundPath([], []));
    }

    [Fact]
    public async Task AnUndecidedCallAsks_AndAlwaysAllowSavesAFoundRowForTheTeam()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "lock", "3"));
        var ask = rig.PendingAsk(rig.Alpha);
        Assert.Equal("pr lock", ask["action"]!.GetValue<string>());
        Assert.True(ask["canAlways"]!.GetValue<bool>());

        var r = await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "always" });
        Assert.Equal("pr lock", r!["remembered"]!.GetValue<string>());
        var node = rig.Node("fk", rig.Alpha, "pr lock");
        Assert.Equal(RowOrigin.Found, node.Origin);
        Assert.Equal(Decisions.Allow, node.TeamDecision);
        Assert.Null(node.DefaultDecision);

        Assert.Contains("ran pr lock 4", await rig.Fk("Alpha", "pr", "lock", "4"));
        Assert.StartsWith("asked:", await rig.Fk("Beta", "pr", "lock", "4"));
        await Wait.UntilAsync(() => rig.Told(m => Payload.Field(m, "text")?.Contains("allowed `pr lock` for your team from now on") == true), "Ada told what was remembered");
    }

    [Fact]
    public async Task AlwaysForEveryTeam_WritesTheDefault()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "lock", "3"));
        var ask = rig.PendingAsk(rig.Alpha);
        await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "always", ["scope"] = "all" });
        var node = rig.Node("fk", null, "pr lock");
        Assert.Equal(Decisions.Allow, node.DefaultDecision);
        Assert.Null(rig.Node("fk", rig.Alpha, "pr lock").TeamDecision);
        Assert.Contains("ran pr lock 4", await rig.Fk("Beta", "pr", "lock", "4"));
    }

    [Fact]
    public async Task KeepAsking_ApprovesThisCall_AndSavesAnAskRow()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "lock", "3"));
        var ask = rig.PendingAsk(rig.Alpha);
        var r = await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "ask" });
        Assert.Equal("pr lock", r!["remembered"]!.GetValue<string>());
        Assert.Equal(0, rig.Integrations.PendingCount());
        var node = rig.Node("fk", rig.Alpha, "pr lock");
        Assert.Equal((RowOrigin.Found, Decisions.Ask), (node.Origin, node.TeamDecision));
        await Wait.UntilAsync(() => rig.Told(m => Payload.Field(m, "text")?.Contains("will keep asking for `pr lock`") == true), "Ada told it keeps asking");
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "lock", "4"));
    }

    [Fact]
    public async Task Never_DeclinesAndSavesOff()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "lock", "3"));
        var ask = rig.PendingAsk(rig.Alpha);
        var r = await rig.Rpc("integrations.decline", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "never" });
        Assert.Equal("pr lock", r!["remembered"]!.GetValue<string>());
        Assert.Equal(0, rig.Integrations.PendingCount());
        Assert.Equal(Decisions.Deny, rig.Node("fk", rig.Alpha, "pr lock").TeamDecision);
        Assert.StartsWith("refused:", await rig.Fk("Alpha", "pr", "lock", "4"));
        Assert.StartsWith("asked:", await rig.Fk("Beta", "pr", "lock", "4"));
    }

    [Fact]
    public async Task OldShellsAlwaysFlagStillWorks()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "lock", "3"));
        var ask = rig.PendingAsk(rig.Alpha);
        await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["always"] = true });
        var node = rig.Node("fk", rig.Alpha, "pr lock");
        Assert.Equal((RowOrigin.Found, Decisions.Allow), (node.Origin, node.TeamDecision));
        Assert.Contains("ran pr lock 9", await rig.Fk("Alpha", "pr", "lock", "9"));
    }

    [Fact]
    public async Task AFlagFirstCallFallsBackToTheExactLineRule()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "--repo", "x", "pr", "lock", "3"));
        var ask = rig.PendingAsk(rig.Alpha);
        Assert.Null(ask["action"]);
        Assert.False(ask["canAlways"]!.GetValue<bool>());
        var r = await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "always" });
        Assert.Null(r!["remembered"]);
        var list = rig.Integrations.Discover.GetChecklist("fk", rig.Alpha);
        Assert.Empty(list.Nodes);
        Assert.Contains(list.OtherRules, o => o.Pattern == "--repo x pr lock 3" && o.TeamId == rig.Alpha && o.Decision == Decisions.Allow);
        Assert.Contains("ran --repo x pr lock 3", await rig.Fk("Alpha", "--repo", "x", "pr", "lock", "3"));
    }

    [Fact]
    public async Task AlwaysAllowIsRefusedForACredentialRow_AndAllowedForADestructiveOne()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        // a known row the classifier locked (the call's own words do not hit the floor, the row does)
        var id = rig.Scalar("SELECT id FROM integrations WHERE name = 'fk'");
        new DiscoverStore(rig.Db.ConnectionString, TimeProvider.System).UpsertRow(id, "pr peek", "pr", "",
            new Classification(CommandClass.Credential, ClassSource.Floor, "reveals a sign-in"), RowOrigin.Discovered);
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "peek", "3"));
        var ask = rig.PendingAsk(rig.Alpha);
        Assert.Equal("pr peek", ask["action"]!.GetValue<string>());
        Assert.False(ask["canAlways"]!.GetValue<bool>());
        var e = await Assert.ThrowsAsync<RpcException>(() => rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "always" }));
        Assert.Contains("never allowed", e.Message);
        Assert.Equal(1, rig.Integrations.PendingCount());
        Assert.Null(rig.Node("fk", rig.Alpha, "pr peek").TeamDecision);
        await rig.Rpc("integrations.decline", new JsonObject { ["id"] = ask["id"]!.GetValue<long>() });

        Assert.StartsWith("asked:", await rig.Fk("Alpha", "repo", "delete", "o/x")); // (a plain word after it would be part of the guess: Ruling 12)
        var del = rig.PendingAsk(rig.Alpha);
        Assert.True(del["canAlways"]!.GetValue<bool>());
        var r = await rig.Rpc("integrations.approve", new JsonObject { ["id"] = del["id"]!.GetValue<long>(), ["remember"] = "always" });
        Assert.Equal("repo delete", r!["remembered"]!.GetValue<string>());
        var node = rig.Node("fk", rig.Alpha, "repo delete");
        Assert.Equal((CommandClass.Destructive, Decisions.Allow), (node.Class, node.TeamDecision));
    }

    [Fact]
    public async Task PrStatusStillRunsWithoutAChoice_AndPrOpenHasItsOwnRow()
    {
        if (CliRunner.Find("gh") is null) Assert.Skip("gh is not installed here");
        await using var gh = await Rig.Make(withRepos: true);
        await gh.Repos!.EnsureTeamRepoAsync(gh.Alpha, "Alpha", Ct);
        await gh.Rpc("integrations.enable", new JsonObject { ["name"] = "gh", ["teamId"] = gh.Alpha, ["on"] = true });

        var list = gh.Integrations.Discover.GetChecklist("gh", null);
        var open = list.Nodes.Single(n => n.Path == "pr_open");
        var status = list.Nodes.Single(n => n.Path == "pr_status");
        Assert.Equal((RowOrigin.Builtin, CommandClass.Change, ""), (open.Origin, open.Class, open.ParentPath));
        Assert.Equal((RowOrigin.Builtin, CommandClass.ReadOnly), (status.Origin, status.Class));
        Assert.Equal("open a PR from the team branch", open.Summary);

        var said = await gh.CallAs("Alpha", "Ada", "pr_status", new { });
        Assert.DoesNotContain("asked:", said);
        Assert.DoesNotContain("refused:", said);

        gh.Integrations.Discover.SaveChoices("gh", null, [new RowChoice("pr_open", RowStates.Allow)]);
        new DiscoverStore(gh.Db.ConnectionString, TimeProvider.System).UpsertRow(gh.Scalar("SELECT id FROM integrations WHERE name = 'gh'"), "pr create", "pr", "",
            CommandClassifier.Classify("gh", CliCatalog.Of("gh"), "pr create", false), RowOrigin.Found);
        gh.Integrations.Discover.SaveChoices("gh", null, [new RowChoice("pr create", RowStates.Off)]);
        var opened = await gh.CallAs("Alpha", "Ada", "pr_open", new { title = "Greeting", body = "Says hi.", reason = "done" });
        Assert.DoesNotContain("refused:", opened);
        Assert.DoesNotContain("asked:", opened);
        Assert.StartsWith("refused:", await gh.CallAs("Alpha", "Ada", "gh", new { args = new[] { "pr", "create", "--fill" }, reason = "x" }));
    }

    [Fact]
    public async Task UnmatchedPlainTypedRulesBecomeFoundRows()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        await rig.Rpc("integrations.saveRule", new JsonObject { ["name"] = "fk", ["pattern"] = "pr lock*", ["decision"] = "allow" });
        await rig.Rpc("integrations.saveRule", new JsonObject { ["name"] = "fk", ["pattern"] = "*--force*", ["decision"] = "deny" });
        await rig.Rpc("integrations.saveRule", new JsonObject { ["name"] = "fk", ["pattern"] = "pr * --x*", ["decision"] = "deny" });
        var list = rig.Integrations.Discover.GetChecklist("fk", null);
        var node = list.Nodes.Single(n => n.Path == "pr lock");
        Assert.Equal((RowOrigin.Found, Decisions.Allow), (node.Origin, node.DefaultDecision));
        Assert.Equal(["*--force*", "pr * --x*"], list.OtherRules.Select(o => o.Pattern).Order().ToArray());
        Assert.Equal(1, list.ChoiceCount);
        // the choice adopted the rule: no second rule was written
        Assert.Equal(3, rig.Scalar("SELECT count(*) FROM integration_rules"));
        Assert.Contains("ran pr lock 1", await rig.Fk("Alpha", "pr", "lock", "1"));
    }

    [Fact]
    public async Task Mcp_UndecidedToolAsks_AndAlwaysSavesAFoundRow()
    {
        if (CliRunner.Find("node") is null) Assert.Skip("node is not installed here");
        await using var rig = await Rig.Make();
        var script = Path.Combine(rig.Db.Dir, "fake-mcp.js");
        File.WriteAllText(script, FakeServer);
        await rig.Rpc("integrations.mcpSave", new JsonObject { ["name"] = "fake", ["command"] = "node", ["args"] = new JsonArray(script) });
        await rig.Rpc("integrations.mcpCheck", new JsonObject { ["name"] = "fake" });
        await rig.Rpc("integrations.mcpEnable", new JsonObject { ["name"] = "fake", ["all"] = true });

        Assert.StartsWith("asked:", await rig.CallAs("Alpha", "Ada", "fake__frobnicate", new { _reason = "try it" }));
        var ask = rig.PendingAsk(rig.Alpha);
        Assert.Equal("frobnicate", ask["action"]!.GetValue<string>());
        Assert.True(ask["canAlways"]!.GetValue<bool>());
        var r = await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "always" });
        Assert.Equal("frobnicate", r!["remembered"]!.GetValue<string>());
        var node = rig.Integrations.Discover.GetMcpChecklist("fake", rig.Alpha).Nodes.Single(n => n.Path == "frobnicate");
        Assert.Equal((RowOrigin.Found, Decisions.Allow), (node.Origin, node.TeamDecision));
        Assert.Equal("ran frobnicate", await rig.CallAs("Alpha", "Ada", "fake__frobnicate", new { _reason = "again" }));
        Assert.StartsWith("asked:", await rig.CallAs("Beta", "Bo", "fake__frobnicate", new { _reason = "try it" }));
    }

    // ------------------------------------------------------------------ fix round 1

    [Fact]
    public async Task ABuiltInsRowNeverLoosensTheFloorOnTheRealArguments()
    {
        await using var rig = await Rig.Make(withRepos: true);
        await rig.Repos!.EnsureTeamRepoAsync(rig.Alpha, "Alpha", Ct);
        await rig.AddFakeCli("gh"); // a fake gh: nothing here may reach GitHub
        rig.Integrations.Discover.GetChecklist("gh", null); // the built-in rows
        rig.Integrations.Discover.SaveChoices("gh", null, [new RowChoice("pr_open", RowStates.Allow)]);
        Assert.Equal(Decisions.Allow, rig.Node("gh", rig.Alpha, "pr_open").Effective);

        var said = await rig.CallAs("Alpha", "Ada", "pr_open", new { title = "token", body = "Says hi.", reason = "done" });
        Assert.StartsWith("refused:", said);
        Assert.Contains("it would reveal or change the person's sign-in", said);
        Assert.Equal(0, rig.Integrations.PendingCount());
        var calls = (JsonArray)(await rig.Rpc("integrations.calls", new JsonObject()))!;
        var call = Assert.Single(calls, c => c!["integration"]!.GetValue<string>() == "gh")!;
        Assert.Equal(Decisions.Deny, call["decision"]!.GetValue<string>());
        Assert.Null(call["exitCode"]); // it never ran
    }

    [Fact]
    public async Task APrOpenAsk_IsRememberedUnderItsBuiltInRow()
    {
        await using var rig = await Rig.Make(withRepos: true);
        await rig.Repos!.EnsureTeamRepoAsync(rig.Alpha, "Alpha", Ct);
        await rig.AddFakeCli("gh"); // a fake gh: nothing here may reach GitHub
        rig.Integrations.Discover.GetChecklist("gh", null); // the built-in rows
        Assert.StartsWith("asked:", await rig.CallAs("Alpha", "Ada", "pr_open", new { title = "Greeting", body = "Says hi.", reason = "done" }));
        var ask = rig.PendingAsk(rig.Alpha);
        Assert.True(ask["canAlways"]!.GetValue<bool>());
        Assert.Equal("pr_open", ask["action"]!.GetValue<string>());
        var r = await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "always" });
        Assert.Equal("pr_open", r!["remembered"]!.GetValue<string>());
        Assert.Equal(Decisions.Allow, rig.Node("gh", rig.Alpha, "pr_open").TeamDecision);
        Assert.DoesNotContain(rig.Integrations.Discover.GetChecklist("gh", rig.Alpha).OtherRules, o => o.Pattern.StartsWith("pr create", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AShortGuessNeverCoversALongerCommand_AndARememberedRowStillRunsItsOwn()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "m", "1"));
        var ask = rig.PendingAsk(rig.Alpha);
        var r = await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "always" });
        Assert.Equal("pr m", r!["remembered"]!.GetValue<string>());
        Assert.Contains("ran pr m 2", await rig.Fk("Alpha", "pr", "m", "2"));
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "merge", "1", "--admin"));
        await rig.Rpc("integrations.decline", new JsonObject { ["id"] = rig.PendingAsk(rig.Alpha)["id"]!.GetValue<long>() });

        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "lock", "3"));
        await rig.Rpc("integrations.approve", new JsonObject { ["id"] = rig.PendingAsk(rig.Alpha)["id"]!.GetValue<long>(), ["remember"] = "always" });
        Assert.Contains("ran pr lock 4", await rig.Fk("Alpha", "pr", "lock", "4"));
        Assert.Contains("ran pr lock", await rig.Fk("Alpha", "pr", "lock"));
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "pr", "locker", "4"));
        Assert.Equal(["pr lock *", "pr m *"], rig.Integrations.Discover.GetChecklist("fk", rig.Alpha).Nodes.Where(n => n.TeamDecision is not null)
            .Select(n => n.Path + " *").Order().ToArray());
    }

    [Fact]
    public async Task AOneWordGuessFallsBackToTheExactLine_WhenTheCliWasNeverDiscoveredOrItsRulesGoDeeper()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        // never discovered: "frob" alone is not a row anyone saw
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "frob", "1"));
        var ask = rig.PendingAsk(rig.Alpha);
        Assert.Null(ask["action"]);
        Assert.False(ask["canAlways"]!.GetValue<bool>());
        var r = await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "always" });
        Assert.Null(r!["remembered"]);
        var list = rig.Integrations.Discover.GetChecklist("fk", rig.Alpha);
        Assert.Empty(list.Nodes);
        Assert.Contains(list.OtherRules, o => o.Pattern == "frob 1" && o.TeamId == rig.Alpha);
        Assert.StartsWith("asked:", await rig.Fk("Alpha", "frob", "2"));
        await rig.Rpc("integrations.decline", new JsonObject { ["id"] = rig.PendingAsk(rig.Alpha)["id"]!.GetValue<long>() });

        // discovered, nothing deeper under the word: it is rememberable
        rig.AddDiscoveredRow("fk", "issue list");
        Assert.Equal("frob", rig.Integrations.Discover.FoundPathOf("fk", ["frob", "1"]));

        // gh's own rules go deeper under "pr" and "api": a one-word guess is never remembered there, even when gh was discovered
        await rig.AddFakeCli("gh");
        rig.AddDiscoveredRow("gh", "issue list");
        Assert.Null(rig.Integrations.Discover.FoundPathOf("gh", ["pr", "12"]));
        Assert.Null(rig.Integrations.Discover.FoundPathOf("gh", ["api", "repos/x/y"]));
        Assert.False(rig.Integrations.Discover.CanAlways("gh", ["pr", "12"], rig.Integrations.Discover.FoundPathOf("gh", ["pr", "12"])));
        Assert.Equal("pr m", rig.Integrations.Discover.FoundPathOf("gh", ["pr", "m", "1"]));
        Assert.Null(rig.Integrations.Discover.Remember("gh", rig.Alpha, ["pr", "12"], null, Decisions.Allow));
        Assert.Contains(rig.Integrations.Discover.GetChecklist("gh", rig.Alpha).OtherRules, o => o.Pattern == "pr 12");
    }

    [Fact]
    public void ARowsRuleMatchesItsOwnWordsOnly()
    {
        Assert.True(CliRules.Matches("pr lock *", "pr lock"));
        Assert.True(CliRules.Matches("pr lock *", "pr lock 4 --x"));
        Assert.False(CliRules.Matches("pr lock *", "pr locker"));
        Assert.False(CliRules.Matches("pr m *", "pr merge 1 --admin"));
        Assert.True(CliRules.Matches("pr lock*", "pr locker")); // a typed rule keeps its meaning
        Assert.Equal("pr lock *", DiscoverStore.PatternOf("pr lock"));
    }

    [Fact]
    public async Task AdoptingTypedRulesIsSafeUnderConcurrentReads()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        for (var i = 0; i < 5; i++)
            await rig.Rpc("integrations.saveRule", new JsonObject { ["name"] = "fk", ["pattern"] = $"cmd{i} go*", ["decision"] = "allow" });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => rig.Integrations.Discover.GetChecklist("fk", null))));
        Assert.Equal(5, rig.Scalar("SELECT count(*) FROM integration_choices"));
        Assert.Equal(5, rig.Scalar("SELECT count(*) FROM integration_rules"));
    }

    // ------------------------------------------------------------------ edge cases

    private static Task<string> Call(Rig rig, string cli, params string[] args) => rig.CallAs("Alpha", "Ada", cli, new { args, reason = "needed" });

    [Fact]
    public async Task C1_AGuessTakesAllItsPlainWords_SoAlwaysOnAnUpdateNeverAllowsADelete()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli("az"); // az-shaped and never discovered: its preset turns "* delete*" off
        Assert.Equal("storage account update", DiscoverService.FoundPath(["storage", "account", "update", "--name", "x"], []));
        Assert.Equal("a b c d e", DiscoverService.FoundPath(["a", "b", "c", "d", "e", "1"], []));

        Assert.StartsWith("asked:", await Call(rig, "az", "storage", "account", "update", "--name", "x"));
        var ask = rig.PendingAsk(rig.Alpha);
        Assert.Equal("storage account update", ask["action"]!.GetValue<string>());
        var r = await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "always" });
        Assert.Equal("storage account update", r!["remembered"]!.GetValue<string>());
        await Wait.UntilAsync(() => rig.Integrations.PendingCount() == 0 && rig.Scalar("SELECT count(*) FROM integration_calls WHERE decision = 'approved'") == 1, "the approved call ran");

        Assert.Contains("ran storage account update --name y", await Call(rig, "az", "storage", "account", "update", "--name", "y"));
        Assert.StartsWith("refused:", await Call(rig, "az", "storage", "account", "delete", "--name", "x", "--yes"));
        Assert.StartsWith("asked:", await Call(rig, "az", "storage", "account", "create", "--name", "x"));
    }

    [Fact]
    public async Task I4_APassthroughCommandIsOnlyEverRememberedAsTheExactLine()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli("gh");
        await rig.AddFakeCli();
        rig.AddDiscoveredRow("gh", "api");
        rig.AddDiscoveredRow("gh", "issue list");
        rig.AddDiscoveredRow("fk", "issue list");
        var d = rig.Integrations.Discover;
        Assert.Null(d.FoundPathOf("gh", ["api", "repos/o/r/pulls"]));
        Assert.Null(d.FoundPathOf("gh", ["api", "graphql", "-f", "q=x"]));
        Assert.Null(d.FoundPathOf("fk", ["cloud", "rest", "get"]));
        Assert.Null(d.FoundPathOf("fk", ["graphql"]));
        Assert.False(d.CanAlways("gh", ["api", "user"], "api"));
        // an ask remembered under the bare "api" key: still the exact line
        Assert.Null(d.Remember("gh", rig.Beta, ["api", "user"], "api", Decisions.Allow));
        Assert.Contains(d.GetChecklist("gh", rig.Beta).OtherRules, o => o.Pattern == "api user" && o.TeamId == rig.Beta);

        Assert.StartsWith("asked:", await Call(rig, "gh", "api", "repos/o/r/pulls"));
        var ask = rig.PendingAsk(rig.Alpha);
        Assert.Null(ask["action"]);
        Assert.False(ask["canAlways"]!.GetValue<bool>());
        var r = await rig.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "always" });
        Assert.Null(r!["remembered"]);
        await Wait.UntilAsync(() => rig.Integrations.PendingCount() == 0 && rig.Scalar("SELECT count(*) FROM integration_calls WHERE decision = 'approved'") == 1, "the approved call ran");
        Assert.Contains("ran api repos/o/r/pulls", await Call(rig, "gh", "api", "repos/o/r/pulls"));
        Assert.StartsWith("asked:", await Call(rig, "gh", "api", "-X", "DELETE", "repos/o/r"));
    }

    [Fact]
    public async Task I5_DiscoverRefreshesTheSignInStatus()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli(); // its status command (--version for an unknown CLI) succeeds
        var before = ((JsonArray)(await rig.Rpc("integrations.list", new JsonObject()))!["integrations"]!).Single(i => i!["name"]!.GetValue<string>() == "fk")!;
        Assert.Null(before["status"]);
        var s = await rig.Rpc("integrations.discover", new JsonObject { ["name"] = "fk" });
        Assert.True(s!["started"]!.GetValue<bool>());
        await Wait.UntilAsync(async () => (await rig.Rpc("integrations.discoverStatus", new JsonObject { ["name"] = "fk" }))!["state"]!.GetValue<string>() != "running", "the walk ended");
        var after = ((JsonArray)(await rig.Rpc("integrations.list", new JsonObject()))!["integrations"]!).Single(i => i!["name"]!.GetValue<string>() == "fk")!;
        Assert.StartsWith("signed in", after["status"]!.GetValue<string>());
    }

    [Fact]
    public void Ruling11_TheDenylistIsCaseInsensitive_WithOrWithoutAnExtension()
    {
        foreach (var name in new[] { "shutdown", "Shutdown.exe", "cmd.BAT", "rmdir.cmd", "PowerShell", "pwsh", "reg", "net.exe", "ICACLS" })
            Assert.Contains("cannot be added as an integration", CliCatalog.Refused(name));
        foreach (var name in new[] { "gh", "git", "az", "node", "fk", "shutdowner", "netstat" })
            Assert.Null(CliCatalog.Refused(name));
    }

    /// <summary>
    /// Only names that are cmd.exe built-ins (no program on PATH): if the guard ever broke, these would fail as "not installed" and
    /// still run nothing. (Never a real program such as `shutdown`: a broken guard would run it with the test's arguments.)
    /// </summary>
    [Theory]
    [InlineData("del")]
    [InlineData("ERASE.exe")]
    [InlineData("rd.cmd")]
    [InlineData("Rmdir.BAT")]
    public async Task Ruling11_ASystemCommandCannotBeAdded(string name)
    {
        Assert.Null(CliRunner.Find(name)); // nothing that could run
        await using var rig = await Rig.Make();
        foreach (var (method, p) in new[]
        {
            ("integrations.detect", new JsonObject { ["name"] = name }),
            ("integrations.enable", new JsonObject { ["name"] = name, ["all"] = true }),
            ("integrations.discover", new JsonObject { ["name"] = name }),
            ("integrations.saveRule", new JsonObject { ["name"] = name, ["pattern"] = "x*", ["decision"] = "allow" }),
        })
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => rig.Rpc(method, p));
            Assert.Equal("bad_params", e.Code);
            Assert.Contains("cannot be added as an integration", e.Message);
        }
        Assert.Contains("cannot be added as an integration", (await Assert.ThrowsAsync<ArgumentException>(() => rig.Integrations.Discover.DiscoverAsync(name, null, null, Ct))).Message);
        Assert.Contains("cannot be added as an integration", Assert.Throws<ArgumentException>(() => rig.Integrations.Discover.Start(name)).Message);
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM integrations"));
    }

    [Fact]
    public async Task Ruling11_DetectingAnUnknownCliAsksForItsHelpWithDashDashHelpOnly()
    {
        // Safety: only a fake .cmd in this test's temp folder ever runs. Its name is no system program and nothing on PATH,
        // so nothing else could be found on PATH.
        if (!OperatingSystem.IsWindows()) Assert.Skip("a .cmd fake");
        const string name = "yawblefakeprobe";
        Assert.Null(CliCatalog.Refused(name));
        Assert.Null(CliCatalog.Of(name));
        Assert.Null(CliRunner.Find(name));
        await using var rig = await Rig.Make();
        await rig.AddFakeCli(name);
        var fake = Path.Combine(rig.Db.Dir, name + ".cmd");
        var log = Path.Combine(rig.Db.Dir, name + "-args.txt");
        // records each call's arguments, then prints two lines of help (readable, no commands to walk)
        File.WriteAllText(fake, $"@echo off\r\necho [%*]>> \"{log}\"\r\necho Usage: {name} [options]\r\necho   --version  print the version\r\n");

        await rig.Rpc("integrations.detect", new JsonObject { ["name"] = name });
        var detected = File.ReadAllLines(log);
        Assert.Contains("[--help]", detected);
        Assert.Contains("[--version]", detected);
        Assert.DoesNotContain(detected, l => l.Split(' ', '[', ']').Contains("-h"));

        File.Delete(log);
        var outcome = await rig.Integrations.Discover.DiscoverAsync(name, null, null, Ct);
        Assert.Null(outcome.Error);
        var discovered = File.ReadAllLines(log);
        Assert.Contains("[--help]", discovered);
        Assert.DoesNotContain(discovered, l => l.Split(' ', '[', ']').Contains("-h"));
        Assert.All(discovered.Concat(detected), l => Assert.True(l is "[--help]" or "[--version]", $"the fake was called with {l}"));
    }

    [Fact]
    public async Task AnAskStoredBeforeTheGuessRules_IsRecheckedWhenRemembered()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        var d = rig.Integrations.Discover;
        // never discovered: a one-word stored action would remember the whole "frob" family
        Assert.False(d.CanAlways("fk", ["frob", "1"], "frob"));
        Assert.Null(d.Remember("fk", rig.Alpha, ["frob", "1"], "frob", Decisions.Allow));
        Assert.Contains(d.GetChecklist("fk", rig.Alpha).OtherRules, o => o.Pattern == "frob 1" && o.TeamId == rig.Alpha);
        Assert.DoesNotContain(d.GetChecklist("fk", rig.Alpha).Nodes, n => n.Path == "frob");

        // discovered now, but the stored guess is shorter than the call's plain words
        rig.AddDiscoveredRow("fk", "issue list");
        Assert.False(d.CanAlways("fk", ["storage", "account", "update", "--x"], "storage account"));
        Assert.Null(d.Remember("fk", rig.Beta, ["storage", "account", "update", "--x"], "storage account", Decisions.Allow));
        Assert.DoesNotContain(d.GetChecklist("fk", rig.Beta).Nodes, n => n.Path == "storage account");

        // a stored action that holds is still remembered as its row
        Assert.True(d.CanAlways("fk", ["storage", "account", "update", "--x"], "storage account update"));
        Assert.Equal("storage account update", d.Remember("fk", rig.Beta, ["storage", "account", "update", "--x"], "storage account update", Decisions.Allow));
    }

    [Fact]
    public async Task Ruling11c_ARuleSavedForAnUnknownCli_DoesNotMakeItAnIntegrationTheCloudLinkMayRun()
    {
        // Saving a rule adds a row with no path; only a CLI detected here (a stored path to a program) counts for the Cloud Link.
        await using var rig = await Rig.Make();
        await rig.Rpc("integrations.saveRule", new JsonObject { ["name"] = "mytool", ["pattern"] = "x *", ["decision"] = "allow" });
        Assert.Equal(1, rig.Scalar("SELECT count(*) FROM integrations WHERE name = 'mytool'"));
        Assert.False(rig.Integrations.HasIntegration("mytool"));
        Assert.NotNull(Yawble.Core.Link.RemoteAccess.Refusal("integrations.detect", new JsonObject { ["name"] = "mytool" }, false, rig.Integrations.HasIntegration));
        await rig.AddFakeCli(); // detected: a stored path that exists
        Assert.True(rig.Integrations.HasIntegration("fk"));
        Assert.True(rig.Integrations.HasIntegration(" FK "));
        Assert.Null(Yawble.Core.Link.RemoteAccess.Refusal("integrations.detect", new JsonObject { ["name"] = "fk" }, false, rig.Integrations.HasIntegration));
        File.Delete(Path.Combine(rig.Db.Dir, OperatingSystem.IsWindows() ? "fk.cmd" : "fk")); // its program is gone
        Assert.False(rig.Integrations.HasIntegration("fk"));
    }

    [Fact]
    public async Task DetectRunsTheStoredPathFirst_TheProgramTheCallsRun()
    {
        // Safety: both programs are fakes in this test's temp folders; the PATH one only ever gets a unique test-only name.
        if (!OperatingSystem.IsWindows()) Assert.Skip("a .cmd fake");
        const string name = "yawblefakestored";
        Assert.Null(CliCatalog.Refused(name));
        Assert.Null(CliRunner.Find(name));
        await using var rig = await Rig.Make();
        await rig.AddFakeCli(name);
        var log = Path.Combine(rig.Db.Dir, name + "-ran.txt");
        File.WriteAllText(Path.Combine(rig.Db.Dir, name + ".cmd"), $"@echo off\r\necho stored>> \"{log}\"\r\necho Usage: {name}\r\n");
        var other = Directory.CreateTempSubdirectory("yawble-pathfake-").FullName;
        File.WriteAllText(Path.Combine(other, name + ".cmd"), $"@echo off\r\necho path>> \"{log}\"\r\necho Usage: {name}\r\n");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", other + Path.PathSeparator + oldPath);
            Assert.NotNull(CliRunner.Find(name));
            var r = await rig.Rpc("integrations.detect", new JsonObject { ["name"] = name });
            Assert.Equal(Path.Combine(rig.Db.Dir, name + ".cmd"), r!["path"]!.GetValue<string>());
            Assert.All(File.ReadAllLines(log), l => Assert.Equal("stored", l.Trim()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            try { Directory.Delete(other, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ThePlainGhToolRefusesABuiltInsName()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli("gh");
        foreach (var first in new[] { "pr_open", "PR_STATUS" })
        {
            var said = await Call(rig, "gh", first, "x");
            Assert.Contains("is a built-in tool", said);
        }
        Assert.Equal(0, rig.Scalar("SELECT count(*) FROM integration_calls"));
    }

    [Fact]
    public async Task SaveChecklistWithANonStringPathOrState_IsBadParams()
    {
        await using var rig = await Rig.Make();
        await rig.AddFakeCli();
        foreach (var bad in new[] { new JsonObject { ["path"] = 5, ["state"] = "allow" }, new JsonObject { ["path"] = "x", ["state"] = true } })
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => rig.Rpc("integrations.saveChecklist", new JsonObject { ["name"] = "fk", ["choices"] = new JsonArray(bad) }));
            Assert.Equal("bad_params", e.Code);
        }
    }
}
