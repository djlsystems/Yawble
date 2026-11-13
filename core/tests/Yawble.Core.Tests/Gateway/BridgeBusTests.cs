using System.Diagnostics;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Gateway;

/// <summary>
/// yawble-bridge, spoken to over its stdio as an agent CLI would. Against an in-memory gateway (the default) it is the
/// bridge's own relay (BusRelay and GatewayAddress, compiled in from the bridge project) run in this process over
/// in-memory stdio, its HTTP client connecting in memory: the same code path but for Program.cs and the process
/// boundary. Against a real-loopback gateway (opt-in, YAWBLE_REAL_LOOPBACK=1) it is the real built yawble-bridge process.
/// </summary>
internal sealed class BridgeProcess : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly TextWriter _stdin;
    private readonly TextReader _stdout;
    private readonly Task? _relay;
    private readonly Stream? _cliEnd;
    private int _id;
    public List<string> Stderr { get; } = [];

    /// <summary>Whether this is the real yawble-bridge process (else the relay in this process).</summary>
    public bool IsProcess => _process is not null;

    private BridgeProcess(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
    }

    private BridgeProcess(Uri proxy, string token)
    {
        // Only what a sandbox would give it: HTTP_PROXY (the gateway) and YAWBLE_RUN_TOKEN, read as Program.cs reads them.
        var env = new Dictionary<string, string?> { ["HTTP_PROXY"] = proxy.ToString(), ["YAWBLE_RUN_TOKEN"] = token };
        var gateway = Yawble.Bridge.GatewayAddress.FromEnvironment(n => env.GetValueOrDefault(n), out var error)
            ?? throw new InvalidOperationException("yawble-bridge would not start: " + error);
        var (cli, bridge) = DuplexPipeStream.CreatePair();
        _cliEnd = cli;
        var utf8 = new System.Text.UTF8Encoding(false);
        _stdin = new StreamWriter(cli, utf8) { NewLine = "\n" };
        _stdout = new StreamReader(cli, utf8);
        var log = new StderrLines(Stderr);
        _relay = Task.Run(async () =>
        {
            await using var _ = bridge;
            using var relay = new Yawble.Bridge.BusRelay(gateway, InMemoryNetwork.ProxyHandler(gateway.Proxy));
            await relay.RunAsync(new StreamReader(bridge, utf8), new StreamWriter(bridge, utf8) { NewLine = "\n" }, log, CancellationToken.None);
        });
    }

    /// <summary>Starts yawble-bridge with only what a sandbox would give it: HTTP_PROXY (the gateway) and YAWBLE_RUN_TOKEN.</summary>
    public static BridgeProcess Start(Uri proxy, string token, Action<System.Diagnostics.ProcessStartInfo>? configure = null)
    {
        // In memory unless the gateway is on a real port. (cwd and HOME, which configure may set, are not read by the relay.)
        if (InMemoryNetwork.IsListening(proxy.Port)) return new BridgeProcess(proxy, token);
        var psi = Binaries.Bridge();
        configure?.Invoke(psi);
        psi.Environment["HTTP_PROXY"] = proxy.ToString();
        psi.Environment["YAWBLE_RUN_TOKEN"] = token;
        var process = Process.Start(psi)!;
        var bridge = new BridgeProcess(process);
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (bridge.Stderr) bridge.Stderr.Add(e.Data); };
        process.BeginErrorReadLine();
        return bridge;
    }

    public async Task<JsonObject> RequestAsync(string method, JsonObject? @params = null)
    {
        var id = Interlocked.Increment(ref _id);
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null) message["params"] = @params;
        await _stdin.WriteLineAsync(message.ToJsonString());
        await _stdin.FlushAsync();
        var line = await _stdout.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("yawble-bridge closed stdout; stderr: " + string.Join(" | ", Stderr));
        var reply = (JsonObject)JsonNode.Parse(line)!;
        Assert.Equal(id, reply["id"]!.GetValue<int>());
        return reply;
    }

    public async Task NotifyAsync(string method)
    {
        await _stdin.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToJsonString());
        await _stdin.FlushAsync();
    }

    /// <summary>tools/call; returns (text, isError).</summary>
    public async Task<(string Text, bool IsError)> CallAsync(string tool, JsonObject? args = null)
    {
        var reply = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args ?? [] });
        var result = reply["result"] ?? throw new InvalidOperationException("tool call failed: " + reply.ToJsonString());
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
    }

    public async Task<JsonObject> InitializeAsync()
    {
        var init = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test-cli", ["version"] = "0" },
        });
        await NotifyAsync("notifications/initialized");
        return init;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null)
        {
            await _cliEnd!.DisposeAsync(); // the CLI exits: stdin EOF
            await _relay!.WaitAsync(TimeSpan.FromSeconds(10)); // the relay ends cleanly (as the process exits 0)
            return;
        }
        _process.StandardInput.Close(); // the CLI exits: stdin EOF
        if (!_process.WaitForExit(10_000)) _process.Kill(entireProcessTree: true);
        Assert.Equal(0, _process.ExitCode);
        _process.Dispose();
    }

    /// <summary>The relay's log lines (its stderr), kept as the process's are.</summary>
    private sealed class StderrLines(List<string> lines) : TextWriter
    {
        private readonly System.Text.StringBuilder _line = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            lock (lines)
            {
                if (value == '\n') { lines.Add(_line.ToString().TrimEnd('\r')); _line.Clear(); }
                else _line.Append(value);
            }
        }
    }
}

/// <summary>
/// yawble-bridge, launched as a process with a run token, drives all 8 MCP tools against the
/// bus through the run's gateway — progress and handback recorded with the right causation — and a wrong
/// or expired token is refused. The runner here stands in for the launcher: for each admitted run it starts
/// the run's gateway and an yawble-bridge, and plays the agent's tool calls through the bridge. No sandbox, no real CLI.
/// </summary>
public class BridgeBusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class BridgeRunner(GatewayFixture f, Func<RunContext, BridgeProcess, Task> agent) : IAgentRunner
    {
        public async Task<RunResult> RunAsync(RunContext run, CancellationToken ct)
        {
            await using var gateway = f.Host.Start(
                new RunGatewaySpec(run.RunId, run.Member.Qualified, RunGatewaySpec.VhostsFor("claude", default), [], TimeSpan.FromHours(1)),
                new McpBusServer(run));
            await using var bridge = BridgeProcess.Start(gateway.ProxyUrl, gateway.Token.Value);
            await agent(run, bridge);
            return RunResult.Ok;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheBridgeDrivesAllEightTools_AgainstTheBus_WithIdentityAndCausationFromTheRun(bool realBridgeProcess)
    {
        if (realBridgeProcess) GatewayFixture.SkipUnlessRealLoopback("the real yawble-bridge process reaching the gateway over a socket");
        await using var f = await GatewayFixture.StartAsync(realLoopback: realBridgeProcess);
        var transcript = new List<string>();
        var handbacks = new HashSet<string>();
        var toolLists = new List<string[]>();

        async Task Agent(RunContext run, BridgeProcess bridge)
        {
            var init = await bridge.InitializeAsync();
            Assert.Equal("2025-06-18", init["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal("yawble-bus", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
            var tools = (await bridge.RequestAsync("tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();
            lock (toolLists) toolLists.Add(tools);

            if (run.Member.Role == MemberRole.Manager)
            {
                if (run.Items.Any(i => i.Source == MessageTypes.PersonSource))
                {
                    var (role, _) = await bridge.CallAsync("skills_get", new JsonObject { ["name"] = "manager" });
                    Assert.StartsWith("# Manager", role);
                    var (status, _) = await bridge.CallAsync("status");
                    Assert.Contains("\"name\":\"Ada\"", status);
                    Assert.Contains("\"name\":\"Bo\"", status);
                    Assert.False((await bridge.CallAsync("progress", new JsonObject { ["status"] = "splitting the work" })).IsError);
                    Assert.False((await bridge.CallAsync("tell", new JsonObject { ["member"] = "Ada", ["instruction"] = "build the parser" })).IsError);
                    Assert.False((await bridge.CallAsync("tell", new JsonObject { ["member"] = "Bo", ["instruction"] = "write the tests" })).IsError);
                    return;
                }
                lock (handbacks)
                {
                    foreach (var i in run.Items.Where(i => i.Type == MessageTypes.Handback)) handbacks.Add(i.Source);
                    if (handbacks.Count < 2) return;
                }
                var (done, refused) = await bridge.CallAsync("workflow_complete", new JsonObject { ["delivered"] = "parser and tests" });
                lock (transcript) transcript.Add($"workflow_complete -> {(refused ? "refused" : "ok")}: {done}");
                return;
            }

            // Members. A forged identity argument changes nothing: the gateway's run decides who is calling.
            Assert.False((await bridge.CallAsync("progress", new JsonObject { ["status"] = $"{run.Member.Name} working" })).IsError);
            var (role2, _) = await bridge.CallAsync("skills_get", new JsonObject { ["name"] = "member" });
            Assert.StartsWith("# Member", role2);
            var (sideways, sidewaysRefused) = await bridge.CallAsync("tell", new JsonObject { ["member"] = run.Member.Name == "Ada" ? "Bo" : "Ada", ["instruction"] = "do my part" });
            Assert.True(sidewaysRefused);
            Assert.Contains("not_allowed", sideways);
            if (run.Member.Name == "Ada")
            {
                Assert.False((await bridge.CallAsync("needs_decision", new JsonObject { ["question"] = "JSON or YAML?" })).IsError);
            }
            else
            {
                Assert.False((await bridge.CallAsync("blocked", new JsonObject { ["reason"] = "fixture missing, item 1 skipped", ["item"] = 1 })).IsError);
            }
            var (handback, handbackRefused) = await bridge.CallAsync("handback", new JsonObject { ["delivered"] = $"{run.Member.Name} finished", ["member"] = "Manager" });
            Assert.False(handbackRefused, handback);
        }

        await using var engine = new BusEngine(f.Db.OpenStore(), new BridgeRunner(f, Agent), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200), IdleOffer = false });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
        await engine.StartAsync(Ct);

        var root = await engine.PersonTellAsync("Alpha", "Manager", "ship the parser with tests", ct: Ct);
        using var reader = f.Db.OpenStore();
        await Wait.UntilAsync(() => reader.WorkflowDeclared(root.Seq), "workflow.completed via the bridge", TimeSpan.FromSeconds(90));
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "quiescent", TimeSpan.FromSeconds(30));

        // Every run saw all 8 tools.
        Assert.All(toolLists, t => Assert.Equal(McpBusServer.ToolNames.Order(), t.Order()));

        var log = reader.Workflow(root.Seq);
        var bySeq = log.ToDictionary(m => m.Seq);
        Message Single(string type, string source) => Assert.Single(log, m => m.Type == type && m.Source == source);
        Message CauseOf(Message m) => bySeq[m.CausationSeq!.Value];
        void ChainOk(Message m)
        {
            Assert.Equal(root.Seq, m.CorrelationId);
            Assert.Equal(CauseOf(m).Depth + 1, m.Depth);
        }

        // Manager's tools: progress and the two tells are caused by the person's root (depth 0 → 1).
        var managerProgress = Single(MessageTypes.Progress, "Alpha/Manager");
        Assert.Equal(root.Seq, managerProgress.CausationSeq);
        ChainOk(managerProgress);
        var tellAda = Single("agent.instruction.Alpha/Ada", "Alpha/Manager");
        var tellBo = Single("agent.instruction.Alpha/Bo", "Alpha/Manager");
        foreach (var tell in new[] { tellAda, tellBo })
        {
            Assert.Equal(root.Seq, tell.CausationSeq);
            Assert.Equal(1, tell.Depth);
        }
        // Members: progress, needs_decision, blocked and handback are each caused by the tell that woke them (depth 2).
        foreach (var (member, tell) in new[] { ("Alpha/Ada", tellAda), ("Alpha/Bo", tellBo) })
        {
            var progress = Single(MessageTypes.Progress, member);
            var handback = Single(MessageTypes.Handback, member);
            Assert.Equal(tell.Seq, progress.CausationSeq);
            Assert.Equal(tell.Seq, handback.CausationSeq);
            Assert.Equal(2, progress.Depth);
            Assert.Equal(2, handback.Depth);
            Assert.Equal($"{member.Split('/')[1]} finished", Payload.Field(handback, "delivered"));
        }
        var decision = Single(MessageTypes.NeedsDecision, "Alpha/Ada");
        Assert.Equal(tellAda.Seq, decision.CausationSeq);
        Assert.Equal("JSON or YAML?", Payload.Field(decision, "question"));
        var blocked = Single(MessageTypes.Blocked, "Alpha/Bo");
        Assert.Equal(tellBo.Seq, blocked.CausationSeq);
        // Nobody could tell sideways.
        Assert.DoesNotContain(log, m => m.Type == "agent.instruction.Alpha/Ada" && m.Source == "Alpha/Bo");
        Assert.DoesNotContain(log, m => m.Type == "agent.instruction.Alpha/Bo" && m.Source == "Alpha/Ada");
        // The declaration, by the Manager, caused by a member's report.
        var declared = Single(MessageTypes.WorkflowCompleted, "Alpha/Manager");
        ChainOk(declared);
        Assert.StartsWith("Alpha/", CauseOf(declared).Source);
        Assert.Contains(transcript, t => t.StartsWith("workflow_complete -> ok"));

        // The gateway log shows the bus traffic was authorised, and nothing was refused.
        Assert.DoesNotContain(f.Log.Read(), e => e.Kind == GatewayEventKinds.Unauthorized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongExpiredOrOtherRunsToken_IsRefused_AndLogged(bool realBridgeProcess)
    {
        if (realBridgeProcess) GatewayFixture.SkipUnlessRealLoopback("the real yawble-bridge process reaching the gateway over a socket");
        await using var f = await GatewayFixture.StartAsync(realLoopback: realBridgeProcess);
        var outcomes = new List<(string Case, JsonObject Reply)>();

        async Task Agent(RunContext run, BridgeProcess _)
        {
            // This run's gateway; the bridges below each present a token that must not work.
            await using var gateway = f.Host.Start(new RunGatewaySpec(1000 + run.RunId, run.Member.Qualified, new HashSet<string>(), [], TimeSpan.FromMinutes(5)), new McpBusServer(run));
            await using var otherRun = f.Host.Start(new RunGatewaySpec(2000 + run.RunId, "Alpha/other", new HashSet<string>(), [], TimeSpan.FromMinutes(5)), new NoBus());

            foreach (var (name, token) in new[] { ("wrong", "yawrt_not-the-token"), ("other run's", otherRun.Token.Value) })
            {
                await using var bridge = BridgeProcess.Start(gateway.ProxyUrl, token);
                var reply = await bridge.RequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" });
                lock (outcomes) outcomes.Add((name, reply));
            }
            // The right token works ...
            await using (var good = BridgeProcess.Start(gateway.ProxyUrl, gateway.Token.Value))
            {
                var ok = await good.RequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" });
                lock (outcomes) outcomes.Add(("valid", ok));
                // ... until it expires (the run's wall-clock limit passed).
                f.Clock.Advance(TimeSpan.FromMinutes(6));
                var late = await good.RequestAsync("tools/call", new JsonObject { ["name"] = "progress", ["arguments"] = new JsonObject { ["status"] = "late" } });
                lock (outcomes) outcomes.Add(("expired", late));
            }
        }

        await using var engine = new BusEngine(f.Db.OpenStore(), new BridgeRunner(f, Agent), new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(200), IdleOffer = false });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.StartAsync(Ct);
        var root = await engine.PersonTellAsync("Alpha", "Manager", "try the tokens", ct: Ct);
        await Wait.UntilAsync(() => engine.IsQuiescentAsync(Ct), "the run ended", TimeSpan.FromSeconds(60));

        Assert.Equal(["wrong", "other run's", "valid", "expired"], outcomes.Select(o => o.Case));
        foreach (var (name, reply) in outcomes)
        {
            if (name == "valid")
            {
                Assert.NotNull(reply["result"]);
                continue;
            }
            Assert.Null(reply["result"]);
            Assert.Contains("HTTP 401", reply["error"]!["message"]!.GetValue<string>());
        }
        var refused = f.Log.Read().Where(e => e.Kind == GatewayEventKinds.Unauthorized && e.Vhost == Vhosts.Bus).ToArray();
        Assert.Equal(3, refused.Length);
        Assert.All(refused, e => Assert.True(e.Guardrail));
        Assert.Equal(2, refused.Count(e => e.Detail!.StartsWith("Wrong")));
        Assert.Single(refused, e => e.Detail!.StartsWith("Expired"));

        // The late progress never reached the bus.
        using var reader = f.Db.OpenStore();
        Assert.DoesNotContain(reader.Workflow(root.Seq), m => m.Type == MessageTypes.Progress);
    }

    [Fact]
    public async Task ARevokedToken_StopsWorkingWhenTheRunEnds()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var tokens = new RunTokenRegistry(clock);
        var a = tokens.Issue(1, TimeSpan.FromMinutes(10));
        var b = tokens.Issue(2, TimeSpan.FromMinutes(10));
        Assert.StartsWith(RunTokenRegistry.Prefix, a.Value);
        Assert.NotEqual(a.Value, b.Value);
        Assert.Equal(TokenCheck.Valid, tokens.Check(1, a.Value));
        Assert.Equal(TokenCheck.Wrong, tokens.Check(1, b.Value)); // bound to its run
        Assert.Equal(TokenCheck.Missing, tokens.Check(1, null));
        Assert.Equal(TokenCheck.Wrong, tokens.Check(3, a.Value));
        tokens.Revoke(1);
        Assert.Equal(TokenCheck.Revoked, tokens.Check(1, a.Value));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(TokenCheck.Expired, tokens.Check(2, b.Value));
        Assert.DoesNotContain(a.Value, a.ToString());
        await Task.CompletedTask;
    }
}
