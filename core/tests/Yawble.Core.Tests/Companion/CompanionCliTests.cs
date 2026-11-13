extern alias yawble;

using System.Text.Json;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Companion;
using Yawble.Core.Concierge;
using Yawble.Core.Ipc;
using Yawble.Core.Provenance;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Ui;
using Xunit;
using Y = yawble::Yawble.Companion;

namespace Yawble.Core.Tests.Companion;

/// <summary>
/// A stand-in for the running app's control endpoint: answers each method through <see cref="HandleAsync"/> or
/// <see cref="Handle"/> (auth is accepted by default). Like the real endpoint, one connection serves one request at a time,
/// and a call its caller stops waiting for still runs to its end there. Never a pipe, never a process.
/// <see cref="NotRunning"/> makes every connect fail as with no app running; <see cref="BreakNext"/> makes the next call
/// fail as an app restart does (the old connection's write fails).
/// </summary>
internal sealed class FakeConnector : Y.IControlConnector
{
    public Func<string, JsonObject?, Action<JsonObject>?, JsonObject?>? Handle;
    public Func<string, JsonObject?, Task<JsonObject?>>? HandleAsync;
    public bool NotRunning;
    public bool BreakNext;
    public int Connects;
    public int Disposed;
    public readonly List<(string Method, JsonObject? P)> Calls = [];
    public IEnumerable<string> Methods { get { lock (Calls) return [.. Calls.Select(c => c.Method)]; } }

    public Task<Y.IControlChannel> ConnectAsync(CancellationToken ct)
    {
        if (NotRunning) throw new Y.NotRunningException();
        Interlocked.Increment(ref Connects);
        return Task.FromResult<Y.IControlChannel>(new Channel(this));
    }

    private sealed class Channel(FakeConnector owner) : Y.IControlChannel
    {
        private readonly SemaphoreSlim _serial = new(1, 1);
        private bool _broken;

        public async Task<JsonObject> CallAsync(string method, JsonObject? p, Action<JsonObject>? onEvent, CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (_broken) throw new Y.ConnectionLostException(sent: false);
            if (owner.BreakNext)
            {
                owner.BreakNext = false;
                _broken = true;
                throw new Y.ConnectionLostException(sent: false);
            }
            lock (owner.Calls) owner.Calls.Add((method, p?.DeepClone().AsObject()));
            var work = ServeAsync(method, p, onEvent);
            return await work.WaitAsync(ct);
        }

        private async Task<JsonObject> ServeAsync(string method, JsonObject? p, Action<JsonObject>? onEvent)
        {
            await _serial.WaitAsync();
            try
            {
                if (owner.HandleAsync is { } handleAsync && await handleAsync(method, p) is { } fromAsync) return fromAsync;
                if (owner.Handle?.Invoke(method, p, onEvent) is { } handled) return handled;
                if (method == CompanionProtocol.Auth)
                    return new JsonObject { ["authenticated"] = true, ["client"] = p!["client"]!.GetValue<string>(), ["level"] = "destructive" };
                throw new InvalidOperationException("the fake has no answer for " + method);
            }
            finally
            {
                _serial.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner.Disposed);
            return ValueTask.CompletedTask;
        }
    }

    public static JsonObject Result(string text, bool isError = false, string? requestId = null)
    {
        var o = new JsonObject { ["text"] = text, ["isError"] = isError };
        if (requestId is not null) o["requestId"] = requestId;
        return o;
    }
}

/// <summary>
/// Companion task 6: the yawble CLI, in process: a <see cref="FakeConnector"/> (or the real <see cref="Y.PipeConnector"/>
/// against a temp YAWBLE_HOME), StringReader/StringWriter for the console. The real %APPDATA%\yawble-companion (and the old
/// app's %APPDATA%\yawble) is never read or written; no program is run.
/// </summary>
public class CompanionCliTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _home = Directory.CreateTempSubdirectory("yawble-ycli-").FullName;

    public void Dispose()
    {
        try { TestFiles.DeleteTree(_home); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Func<string, string?> Env => name => name == ControlDiscovery.HomeVariable ? _home : null;

    private sealed record Run(int Exit, string Out, string Err);

    private async Task<Run> RunAsync(Y.IControlConnector connector, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await Y.Cli.RunAsync(args, Env, new StringReader(""), TextWriter.Synchronized(stdout), TextWriter.Synchronized(stderr), connector, Ct)
            .WaitAsync(TimeSpan.FromSeconds(60), Ct);
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private Y.KeyFiles Keys => new(_home);

    private static string Pad(string key) => key + new string('k', 40);

    // ---------------------------------------------------------------- not running, not paired

    [Fact]
    public async Task NotRunningSaysSoAndExits3()
    {
        Keys.Save("cc", Pad("key"));
        var run = await RunAsync(new FakeConnector { NotRunning = true }, "teams");
        Assert.Equal(Y.ExitCodes.NotRunning, run.Exit);
        Assert.Contains("Yawble is not running on this computer", run.Err);
        Assert.Equal("", run.Out);

        // the real connector with no control.json at all
        var none = await RunAsync(new Y.PipeConnector(_home), "teams");
        Assert.Equal(Y.ExitCodes.NotRunning, none.Exit);
        Assert.Contains("Yawble is not running on this computer", none.Err);
    }

    [Fact]
    public async Task AStaleControlFileSaysNotRunning()
    {
        Keys.Save("cc", Pad("key"));
        // A crashed core left control.json naming an address nobody listens on: a bounded connect, then "not running".
        ControlDiscovery.Write(_home, new ControlFile(CompanionProtocol.Version, CompanionServer.NewAddress(Path.GetTempPath()), 999_999, "0.1.0", DateTimeOffset.UtcNow));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var run = await RunAsync(new Y.PipeConnector(_home), "teams");
        Assert.Equal(Y.ExitCodes.NotRunning, run.Exit);
        Assert.Contains("Yawble is not running on this computer", run.Err);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");

        // and a control.json that is not one
        File.WriteAllText(ControlDiscovery.FilePath(_home), """{"version":1,"address":"x","address":"y"}""");
        Assert.Equal(Y.ExitCodes.NotRunning, (await RunAsync(new Y.PipeConnector(_home), "teams")).Exit);
    }

    [Fact]
    public async Task AServerThatClosesBeforeAnyReplySaysBusy()
    {
        // The core turns a connection away unread when too many are signing in: the stream ends before any reply.
        await using (var channel = new Y.StreamChannel(new MemoryStream()))
            await Assert.ThrowsAsync<Y.BusyException>(() => channel.CallAsync(CompanionProtocol.Auth, new JsonObject(), null, Ct));

        Keys.Save("cc", Pad("key"));
        var busy = new FakeConnector { Handle = (m, _, _) => m == CompanionProtocol.Auth ? throw new Y.BusyException() : null };
        var run = await RunAsync(busy, "teams");
        Assert.Equal(Y.ExitCodes.Failed, run.Exit);
        Assert.Contains("Yawble is busy; try again", run.Err);
        Assert.DoesNotContain("not running", run.Err);
    }

    [Fact]
    public async Task AStreamChannelKeepsWorkingAfterACallerStopsWaiting()
    {
        // The test plays the app on a temp pipe: it reads both requests, answers the abandoned one late, then the second.
        await using var endpoint = IpcEndpoint.Create(CompanionServer.NewAddress(Path.GetTempPath()));
        ControlDiscovery.Write(_home, new ControlFile(CompanionProtocol.Version, endpoint.Address, Environment.ProcessId, CoreInfo.Version, DateTimeOffset.UtcNow));
        var accepted = endpoint.AcceptAsync(Ct);
        await using var channel = (Y.StreamChannel)await new Y.PipeConnector(_home).ConnectAsync(Ct);
        await using var server = await accepted;

        using (var soon = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            soon.CancelAfter(200);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.CallAsync("slow", null, null, soon.Token));
        }
        Assert.False(channel.IsBroken);
        var second = channel.CallAsync("quick", null, null, Ct);

        var ids = new List<int>();
        for (var i = 0; i < 2; i++)
        {
            var frame = await FrameCodec.ReadAsync(server, Ct);
            ids.Add(JsonNode.Parse(frame!.Value.Payload.Span)!["id"]!.GetValue<int>());
        }
        foreach (var (id, text) in new[] { (ids[0], "late"), (ids[1], "second") })
            await FrameCodec.WriteAsync(server, new Frame(FrameKind.Json, System.Text.Encoding.UTF8.GetBytes(
                new JsonObject { ["id"] = id, ["result"] = new JsonObject { ["text"] = text } }.ToJsonString())), Ct);
        Assert.Equal("second", (await second.WaitAsync(TimeSpan.FromSeconds(30), Ct))["text"]!.GetValue<string>());
        Assert.False(channel.IsBroken);

        // the app going away does break it, and says so
        await server.DisposeAsync();
        await Assert.ThrowsAsync<Y.ConnectionLostException>(() => channel.CallAsync("after", null, null, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.True(channel.IsBroken);
    }

    [Fact]
    public async Task AThrowingEventCallbackDoesNotKillTheReader()
    {
        await using var endpoint = IpcEndpoint.Create(CompanionServer.NewAddress(Path.GetTempPath()));
        ControlDiscovery.Write(_home, new ControlFile(CompanionProtocol.Version, endpoint.Address, Environment.ProcessId, CoreInfo.Version, DateTimeOffset.UtcNow));
        var accepted = endpoint.AcceptAsync(Ct);
        await using var channel = (Y.StreamChannel)await new Y.PipeConnector(_home).ConnectAsync(Ct);
        await using var server = await accepted;

        var call = channel.CallAsync("pair.request", null, _ => throw new InvalidOperationException("a callback bug"), Ct);
        var id = JsonNode.Parse((await FrameCodec.ReadAsync(server, Ct))!.Value.Payload.Span)!["id"]!.GetValue<int>();
        foreach (var message in new[]
                 {
                     new JsonObject { ["event"] = CompanionProtocol.PairCode, ["data"] = new JsonObject { ["code"] = "ABC234" } },
                     new JsonObject { ["id"] = id, ["result"] = new JsonObject { ["approved"] = false } },
                 })
            await FrameCodec.WriteAsync(server, new Frame(FrameKind.Json, System.Text.Encoding.UTF8.GetBytes(message.ToJsonString())), Ct);
        Assert.False((await call.WaitAsync(TimeSpan.FromSeconds(30), Ct))["approved"]!.GetValue<bool>());
        Assert.False(channel.IsBroken);
    }

    [Fact]
    public async Task NotPairedTellsHowToPairAndExits4()
    {
        var fake = new FakeConnector();
        var run = await RunAsync(fake, "teams");
        Assert.Equal(Y.ExitCodes.NotPaired, run.Exit);
        Assert.Contains("yawble pair --name", run.Err);
        Assert.Equal(0, fake.Connects); // nothing to present: no connection

        // a key the app no longer accepts (revoked)
        Keys.Save("cc", Pad("old"));
        var revoked = new FakeConnector
        {
            Handle = (m, _, _) => m == CompanionProtocol.Auth ? throw new Y.ControlErrorException("unauthorized", "unknown client or wrong key") : null,
        };
        var again = await RunAsync(revoked, "teams");
        Assert.Equal(Y.ExitCodes.NotPaired, again.Exit);
        Assert.Contains("yawble pair --name cc", again.Err);

        // two names paired here and none chosen: a usage error that names them
        Keys.Save("other", Pad("x"));
        var two = await RunAsync(new FakeConnector(), "teams");
        Assert.Equal(Y.ExitCodes.Usage, two.Exit);
        Assert.Contains("--client", two.Err);
        Assert.Contains("cc", two.Err);
        Assert.Contains("other", two.Err);
    }

    [Fact]
    public async Task APopulatedYawbleHomeIsRefusedClearly()
    {
        File.WriteAllText(Path.Combine(_home, "notes.txt"), "someone else's folder");
        var fake = new FakeConnector();
        var run = await RunAsync(fake, "pair", "--name", "cc");
        Assert.Equal(Y.ExitCodes.Failed, run.Exit);
        Assert.Contains("already holds other files", run.Err);
        Assert.Contains("YAWBLE_HOME", run.Err);
        Assert.DoesNotContain(CompanionProtocol.PairRequest, fake.Methods); // checked before asking the app
        Assert.Equal("someone else's folder", File.ReadAllText(Path.Combine(_home, "notes.txt")));
    }

    // ---------------------------------------------------------------- commands

    [Fact]
    public void CommandsMapToToolsAndArgs()
    {
        (string Line, string Tool, string Args)[] table =
        [
            ("status", "system_status", "{}"),
            ("doctor", "doctor", "{}"),
            ("teams", "team_list", "{}"),
            ("team alpha", "team_status", """{"team":"alpha"}"""),
            ("team delete alpha", "team_delete", """{"team":"alpha"}"""),
            ("team show delete", "team_status", """{"team":"delete"}"""),
            ("tell alpha use --json here", "tell", """{"team":"alpha","instruction":"use --json here"}"""),
            ("tell alpha -- --x", "tell", """{"team":"alpha","instruction":"-- --x"}"""),
            ("tell -- alpha --json", "tell", """{"team":"alpha","instruction":"--json"}"""),
            ("answer alpha 3 --wait 5", "answer", """{"team":"alpha","question":3,"answer":"--wait 5"}"""),
            ("board comment 12 see --note", "board_comment", """{"card":12,"text":"see --note"}"""),
            ("board move 12 done --note=a|b", "board_move", """{"card":12,"lane":"done","note":"a b"}"""),
            ("backlog", "backlog_list", "{}"),
            ("backlog list", "backlog_list", "{}"),
            ("backlog list --archived", "backlog_list", """{"archived":true}"""),
            ("backlog show B006", "backlog_item", """{"item":"B006"}"""),
            ("backlog add --body Steps --team alpha Fix|the|login", "backlog_add", """{"title":"Fix the login","body":"Steps","team":"alpha"}"""),
            ("backlog add --team=alpha Fix body|handling", "backlog_add", """{"title":"Fix body handling","team":"alpha"}"""),
            ("backlog edit B6 --title New --team beta", "backlog_edit", """{"item":"B6","title":"New","team":"beta"}"""),
            ("backlog ready B6", "backlog_ready", """{"item":"B6"}"""),
            ("backlog dispatch B6 --team alpha", "backlog_dispatch", """{"item":"B6","team":"alpha"}"""),
            ("backlog archive B6", "backlog_archive", """{"item":"B6"}"""),
            ("backlog restore B6", "backlog_restore", """{"item":"B6"}"""),
            ("board", "board_list", "{}"),
            ("board --team alpha", "board_list", """{"team":"alpha"}"""),
            ("board move 12 done --note shipped", "board_move", """{"card":12,"lane":"done","note":"shipped"}"""),
            ("board comment 12 looks|good", "board_comment", """{"card":12,"text":"looks good"}"""),
            ("tell alpha add|a|login|form", "tell", """{"team":"alpha","instruction":"add a login form"}"""),
            ("answer alpha 3 use|postgres", "answer", """{"team":"alpha","question":3,"answer":"use postgres"}"""),
            ("pause alpha", "team_pause", """{"team":"alpha"}"""),
            ("resume alpha", "team_resume", """{"team":"alpha"}"""),
            ("git", "team_git", "{}"),
            ("git alpha", "team_git", """{"team":"alpha"}"""),
            ("git delete-branches alpha", "git_branches_delete", """{"team":"alpha"}"""),
            ("git delete-branches alpha --branches a,b --remote", "git_branches_delete", """{"team":"alpha","branches":"a,b","remote":true}"""),
            ("git prune alpha", "git_prune", """{"team":"alpha"}"""),
            ("git push alpha team/alpha", "git_push_branch", """{"team":"alpha","branch":"team/alpha"}"""),
            ("git sync alpha feature --repo web", "git_sync_branch", """{"team":"alpha","branch":"feature","repo":"web"}"""),
            ("git merge alpha", "git_merge_team", """{"team":"alpha"}"""),
            ("git merge alpha --repo web", "git_merge_team", """{"team":"alpha","repo":"web"}"""),
            ("workflow close alpha 7", "workflow_close", """{"team":"alpha","workflow":7}"""),
            ("backup list", "backups_list", "{}"),
            ("backup restore daily-20261110.db", "backup_restore", """{"backup":"daily-20261110.db"}"""),
            ("call team_activity --arg team=alpha --arg limit=5", "team_activity", """{"team":"alpha","limit":"5"}"""),
            ("call usage --args-json {\"days\":30}", "usage", """{"days":30}"""),
            ("call usage --args-json {\"days\":30,\"team\":\"x\"} --arg team=alpha", "usage", """{"days":30,"team":"alpha"}"""),
        ];
        foreach (var (line, tool, args) in table)
        {
            var argv = line.Split(' ').Select(a => a.Replace('|', ' ')).ToArray();
            Assert.True(Y.Commands.TryMap(argv, out var mapped, out var mappedArgs, out var error), $"{line}: {error}");
            Assert.Equal(tool, mapped);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(args), mappedArgs), $"{line}: {mappedArgs.ToJsonString()}");
        }

        // an option word after the title is almost surely meant as one: refused with where options go
        Assert.False(Y.Commands.TryMap("backlog add Fix login --team alpha".Split(' '), out _, out _, out var placed));
        Assert.Contains("options go before the title", placed);

        foreach (var bad in new[]
                 {
                     "nope", "team", "backlog dispatch B6", "backlog show", "board move twelve done", "answer alpha x yes", "tell alpha",
                     "git push alpha", "workflow close alpha seven", "backup restore", "call", "call usage --args-json [1]",
                     "call usage --args-json {\"a\":1,\"a\":2}", "call usage --arg nokey", "backlog list --nope", "git merge alpha extra",
                     "team show", "team delete alpha extra", "teams --help",
                     "backlog add Fix login --team alpha", "backlog add Fix --body x", "backlog add Fix -- x",
                 })
        {
            Assert.False(Y.Commands.TryMap(bad.Split(' '), out _, out _, out var error), bad);
            Assert.False(string.IsNullOrWhiteSpace(error), bad);
        }
    }

    [Fact]
    public async Task ACommandCallsItsToolAndPrintsTheText()
    {
        Keys.Save("cc", Pad("key"));
        var fake = new FakeConnector { Handle = (m, _, _) => m == CompanionProtocol.Call ? FakeConnector.Result("alpha: 1 agent, idle") : null };
        var run = await RunAsync(fake, "team", "alpha");
        Assert.Equal(Y.ExitCodes.Ok, run.Exit);
        Assert.Contains("alpha: 1 agent, idle", run.Out);
        Assert.Equal([CompanionProtocol.Auth, CompanionProtocol.Call], fake.Methods);
        var auth = fake.Calls[0].P!;
        Assert.Equal("cc", auth["client"]!.GetValue<string>());
        Assert.Equal(Pad("key"), auth["key"]!.GetValue<string>());
        var call = fake.Calls[1].P!;
        Assert.Equal("team_status", call["tool"]!.GetValue<string>());
        Assert.Equal("alpha", call["args"]!["team"]!.GetValue<string>());
    }

    [Fact]
    public async Task JsonOutputIsOneValidJsonDocument()
    {
        Keys.Save("cc", Pad("key"));
        var fake = new FakeConnector { Handle = (m, _, _) => m == CompanionProtocol.Call ? FakeConnector.Result("line one\nline \"two\"") : null };
        var run = await RunAsync(fake, "--json", "teams");
        Assert.Equal(Y.ExitCodes.Ok, run.Exit);
        using (var doc = JsonDocument.Parse(run.Out))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("line one\nline \"two\"", doc.RootElement.GetProperty("text").GetString());
        }

        // a failure is one JSON document too, with the exit code and the reason
        var down = await RunAsync(new FakeConnector { NotRunning = true }, "teams", "--json");
        Assert.Equal(Y.ExitCodes.NotRunning, down.Exit);
        using (var doc = JsonDocument.Parse(down.Out))
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(Y.ExitCodes.NotRunning, doc.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("not running", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        }

        // --version too
        using (var doc = JsonDocument.Parse((await RunAsync(new FakeConnector(), "--version", "--json")).Out))
            Assert.Equal(CoreInfo.Version, doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task ADestructiveCommandPrintsThePlanAndWaits_Exits6WhenStillPending_0WhenApproved()
    {
        Keys.Save("cc", Pad("key"));
        const string plan = "Delete team alpha for good: its agents, board and history.";
        var waits = new List<int>();
        var answers = new Queue<string>(["pending", "pending"]);
        JsonObject? Handle(string m, JsonObject? p, Action<JsonObject>? _)
        {
            switch (m)
            {
                case CompanionProtocol.Call:
                    return FakeConnector.Result($"NOT DONE: waiting for the person's approval in the Yawble window (request r1): {plan}", requestId: "r1");
                case CompanionProtocol.Request:
                    Assert.Equal("r1", p!["id"]!.GetValue<string>());
                    var wait = p["waitSeconds"]?.GetValue<int>() ?? 0;
                    Assert.InRange(wait, 0, CompanionProtocol.MaxWaitSeconds);
                    lock (waits) waits.Add(wait);
                    var state = answers.Count > 0 ? answers.Dequeue() : "pending";
                    return new JsonObject { ["id"] = "r1", ["state"] = state, ["plan"] = plan, ["result"] = state == "done" ? "ok: deleted team alpha." : null, ["nextId"] = null };
                default:
                    return null;
            }
        }

        // still pending after --wait 100: two long-polls of at most 60 s each, then exit 6 with how to check again
        var pending = await RunAsync(new FakeConnector { Handle = Handle }, "team", "delete", "alpha", "--wait", "100");
        Assert.Equal(Y.ExitCodes.Pending, pending.Exit);
        Assert.Contains(plan, pending.Out);
        Assert.Contains("yawble request r1", pending.Out + pending.Err);
        Assert.Equal([60, 40], waits);

        // --wait 0: no waiting at all
        waits.Clear();
        var noWait = await RunAsync(new FakeConnector { Handle = Handle }, "team", "delete", "alpha", "--wait", "0");
        Assert.Equal(Y.ExitCodes.Pending, noWait.Exit);
        Assert.Empty(waits);

        // approved while it waits: the result, exit 0
        waits.Clear();
        answers = new Queue<string>(["pending", "done"]);
        var approved = await RunAsync(new FakeConnector { Handle = Handle }, "team", "delete", "alpha");
        Assert.Equal(Y.ExitCodes.Ok, approved.Exit);
        Assert.Contains(plan, approved.Out);
        Assert.Contains("ok: deleted team alpha.", approved.Out);
        Assert.Equal([60, 60], waits); // the default --wait is 120

        // denied: refused
        answers = new Queue<string>(["denied"]);
        var denied = await RunAsync(new FakeConnector { Handle = Handle }, "team", "delete", "alpha");
        Assert.Equal(Y.ExitCodes.Refused, denied.Exit);
        Assert.Contains("denied", denied.Out + denied.Err);

        // and yawble request <id> checks one later
        answers = new Queue<string>(["done"]);
        waits.Clear();
        var later = await RunAsync(new FakeConnector { Handle = Handle }, "request", "r1");
        Assert.Equal(Y.ExitCodes.Ok, later.Exit);
        Assert.Contains("ok: deleted team alpha.", later.Out);
        Assert.Equal([0], waits);

        // the app restarted while it waited: the request is gone, and yawble says so (not "still waiting")
        var lost = new FakeConnector
        {
            Handle = (m, p, e) => m == CompanionProtocol.Request ? throw new Y.ControlErrorException("not_found", "there is no request 'r1' of this client") : Handle(m, p, e),
        };
        var gone = await RunAsync(lost, "team", "delete", "alpha");
        Assert.Equal(Y.ExitCodes.Failed, gone.Exit);
        Assert.Contains("restarted", gone.Err);
        Assert.Contains("ask again", gone.Err);
        Assert.DoesNotContain("Still waiting", gone.Out);
    }

    [Fact]
    public async Task GlobalOptionsComeBeforeTheCommand_FreeTextIsLiteral()
    {
        Keys.Save("cc", Pad("key"));
        Keys.Save("other", Pad("other"));
        var sent = new List<JsonObject>();
        var fake = new FakeConnector
        {
            Handle = (m, p, _) =>
            {
                if (m != CompanionProtocol.Call) return null;
                lock (sent) sent.Add(p!["args"]!.AsObject().DeepClone().AsObject());
                return FakeConnector.Result("ok: told");
            },
        };

        // an option-looking word inside the instruction is part of it: no JSON output, no word lost
        var json = await RunAsync(fake, "--client", "cc", "tell", "alpha", "use", "--json", "here");
        Assert.Equal(Y.ExitCodes.Ok, json.Exit);
        Assert.Equal("use --json here", sent[^1]["instruction"]!.GetValue<string>());
        Assert.Equal("ok: told", json.Out.Trim());

        // -h in the text sends it, and prints no help
        var help = await RunAsync(fake, "--client=cc", "tell", "alpha", "document", "the", "-h", "option");
        Assert.Equal(Y.ExitCodes.Ok, help.Exit);
        Assert.Equal("document the -h option", sent[^1]["instruction"]!.GetValue<string>());
        Assert.DoesNotContain("usage:", help.Out + help.Err);

        // --client=cc and --json before the command; the auth names cc
        var chosen = await RunAsync(fake, "--client=cc", "--json", "tell", "alpha", "--wait", "x");
        Assert.Equal(Y.ExitCodes.Ok, chosen.Exit);
        using (var doc = JsonDocument.Parse(chosen.Out)) Assert.Equal("ok: told", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("--wait x", sent[^1]["instruction"]!.GetValue<string>());
        Assert.Equal("cc", fake.Calls.Last(c => c.Method == CompanionProtocol.Auth).P!["client"]!.GetValue<string>());

        // after the positional words of a command without free text, global options still work (and in the --opt=value form)
        var after = await RunAsync(fake, "team", "alpha", "--client=other", "--json");
        Assert.Equal(Y.ExitCodes.Ok, after.Exit);
        using (JsonDocument.Parse(after.Out)) { }
        Assert.Equal("other", fake.Calls.Last(c => c.Method == CompanionProtocol.Auth).P!["client"]!.GetValue<string>());

        // an unknown option before the command is a usage error, not a command
        Assert.Equal(Y.ExitCodes.Usage, (await RunAsync(fake, "--nope", "teams")).Exit);
        Assert.Equal(Y.ExitCodes.Usage, (await RunAsync(fake, "--wait=soon", "teams")).Exit);
    }

    [Fact]
    public async Task AnUnexpectedFailureIsStillOneJsonDocument_CtrlCExitsQuietly130()
    {
        Keys.Save("cc", Pad("key"));
        // the fake throws InvalidOperationException for a method it has no answer for
        var run = await RunAsync(new FakeConnector(), "--json", "teams");
        Assert.Equal(Y.ExitCodes.Failed, run.Exit);
        using (var doc = JsonDocument.Parse(run.Out))
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("failed", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        var never = new TaskCompletionSource<JsonObject?>();
        var slow = new FakeConnector { HandleAsync = (m, _) => m == CompanionProtocol.Call ? never.Task : Task.FromResult<JsonObject?>(null) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(300);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await Y.Cli.RunAsync(["teams"], Env, new StringReader(""), stdout, stderr, slow, cts.Token).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(130, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Equal("", stderr.ToString());
        never.TrySetResult(null);
    }

    [Fact]
    public async Task RefusedByLevelExits5()
    {
        Keys.Save("cc", Pad("key"));
        var fake = new FakeConnector
        {
            Handle = (m, _, _) => m == CompanionProtocol.Call
                ? FakeConnector.Result("refused: team_delete needs the destructive level; this client was paired for read", isError: true)
                : null,
        };
        var run = await RunAsync(fake, "team", "delete", "alpha");
        Assert.Equal(Y.ExitCodes.Refused, run.Exit);
        Assert.Contains("needs the destructive level", run.Err);
        Assert.DoesNotContain(CompanionProtocol.Request, fake.Methods);

        // any other error result is a failure
        var failing = new FakeConnector { Handle = (m, _, _) => m == CompanionProtocol.Call ? FakeConnector.Result("there is no team 'zeta'", isError: true) : null };
        Assert.Equal(Y.ExitCodes.Failed, (await RunAsync(failing, "team", "zeta")).Exit);
    }

    [Fact]
    public async Task PairPrintsTheCode_SavesTheKeyUserOnly_NeverPrintsTheKey()
    {
        var key = "Zk3" + new string('q', 40) + "Xy";
        bool? savedBeforeReturn = null;
        var fake = new FakeConnector
        {
            Handle = (m, p, onEvent) =>
            {
                if (m != CompanionProtocol.PairRequest) return null;
                Assert.Equal("claude-code", p!["name"]!.GetValue<string>());
                Assert.Equal("act", p["level"]!.GetValue<string>());
                onEvent!(new JsonObject
                {
                    ["event"] = CompanionProtocol.PairCode,
                    ["data"] = new JsonObject { ["id"] = "p1", ["code"] = "ABC234", ["expires"] = "2026-11-11T10:05:00Z" },
                });
                return new JsonObject { ["approved"] = true, ["reason"] = "approved", ["key"] = key, ["level"] = "act" };
            },
        };
        var run = await RunAsync(fake, "pair", "--name", "claude-code", "--level", "act");
        savedBeforeReturn = Keys.Load("claude-code") is not null;
        Assert.Equal(Y.ExitCodes.Ok, run.Exit);
        Assert.Contains("ABC234", run.Out);
        Assert.Contains("claude-code", run.Out);
        Assert.DoesNotContain(key, run.Out);
        Assert.DoesNotContain(key, run.Err);
        Assert.True(savedBeforeReturn);
        Assert.Equal(key, Keys.Load("claude-code"));
        Assert.Equal("claude-code", Keys.OnlyName());
        var file = Path.Combine(ControlDiscovery.ClientsDir(_home), "claude-code.key");
        Assert.True(UserOnlyFile.IsUserOnly(file));
        Assert.True(UserOnlyFile.IsUserOnly(ControlDiscovery.ClientsDir(_home)));
        Assert.Equal([CompanionProtocol.PairRequest], fake.Methods); // no auth before pairing

        // a denial: no key file, refused
        var denied = new FakeConnector
        {
            Handle = (m, _, onEvent) =>
            {
                if (m != CompanionProtocol.PairRequest) return null;
                onEvent!(new JsonObject { ["event"] = CompanionProtocol.PairCode, ["data"] = new JsonObject { ["id"] = "p2", ["code"] = "XYZ789", ["expires"] = "x" } });
                return new JsonObject { ["approved"] = false, ["reason"] = "denied in the app" };
            },
        };
        var no = await RunAsync(denied, "pair", "--name", "other");
        Assert.Equal(Y.ExitCodes.Refused, no.Exit);
        Assert.Contains("denied", no.Out + no.Err);
        Assert.Null(Keys.Load("other"));

        // a bad name never reaches the app
        var bad = new FakeConnector();
        Assert.Equal(Y.ExitCodes.Usage, (await RunAsync(bad, "pair", "--name", "Bad Name")).Exit);
        Assert.Equal(Y.ExitCodes.Usage, (await RunAsync(bad, "pair")).Exit);
        Assert.Equal(Y.ExitCodes.Usage, (await RunAsync(bad, "pair", "--name", "x", "--level", "root")).Exit);
        Assert.Empty(bad.Methods);
    }

    [Fact]
    public async Task ForgetDeletesTheKeyFileAndSaysToRevokeInSettings()
    {
        Keys.Save("cc", Pad("key"));
        var file = Path.Combine(ControlDiscovery.ClientsDir(_home), "cc.key");
        Assert.True(File.Exists(file));
        var fake = new FakeConnector();
        var run = await RunAsync(fake, "forget", "--client", "cc");
        Assert.Equal(Y.ExitCodes.Ok, run.Exit);
        Assert.False(File.Exists(file));
        Assert.Contains("Settings", run.Out);
        Assert.Contains("Revoke", run.Out);
        Assert.Empty(fake.Methods); // forgetting is local

        Assert.Equal(Y.ExitCodes.NotPaired, (await RunAsync(fake, "forget", "--client", "cc")).Exit);
    }

    [Fact]
    public async Task WhoamiAndVersionAndUsage()
    {
        Keys.Save("cc", Pad("key"));
        var fake = new FakeConnector
        {
            Handle = (m, _, _) => m == CompanionProtocol.WhoAmI
                ? new JsonObject { ["client"] = "cc", ["level"] = "read", ["levelDescription"] = "Can look at teams. Changes nothing." }
                : null,
        };
        var who = await RunAsync(fake, "whoami");
        Assert.Equal(Y.ExitCodes.Ok, who.Exit);
        Assert.Contains("cc", who.Out);
        Assert.Contains("read", who.Out);

        var version = await RunAsync(fake, "--version");
        Assert.Equal(Y.ExitCodes.Ok, version.Exit);
        Assert.Equal($"yawble {CoreInfo.Version}", version.Out.Trim());

        Assert.Equal(Y.ExitCodes.Usage, (await RunAsync(fake)).Exit);
        Assert.Equal(Y.ExitCodes.Ok, (await RunAsync(fake, "help")).Exit);
        var unknown = await RunAsync(fake, "frobnicate");
        Assert.Equal(Y.ExitCodes.Usage, unknown.Exit);
        Assert.Contains("frobnicate", unknown.Err);
        Assert.Equal(Y.ExitCodes.Usage, (await RunAsync(fake, "teams", "--wait")).Exit);
        Assert.Equal(Y.ExitCodes.Usage, (await RunAsync(fake, "teams", "--client", "Bad Name")).Exit);
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public async Task EndToEnd_PairApproveThenBacklogListOverATempPipe()
    {
        using var db = new TempDb();
        var bus = new BusEngine(db.OpenStore(), new UnavailableRunner(), new BusOptions { IdleOffer = false });
        await bus.StartAsync(Ct);
        await using var _bus = bus;
        await bus.CreateTeamAsync("alpha", "Manager", "claude", null, Ct);
        Task<JsonNode?> Route(string method, JsonObject? p, CancellationToken ct) => Task.FromResult<JsonNode?>(method switch
        {
            "backlog.list" => JsonNode.Parse("""[{"label":"B042","state":"ready","title":"Fix the login form","team":"alpha"}]"""),
            _ => new JsonObject(),
        });
        var service = new CompanionService(bus, new ConciergeStore(db.ConnectionString), Route, (_, _) => null,
            new CompanionClientStore(db.ConnectionString), new AuditLog(db.ConnectionString), new AppEvents());
        await using var server = CompanionServer.Start(IpcEndpoint.Create(CompanionServer.NewAddress(Path.GetTempPath())), service);
        ControlDiscovery.Write(_home, new ControlFile(CompanionProtocol.Version, server.Address, Environment.ProcessId, CoreInfo.Version, DateTimeOffset.UtcNow));
        var connector = new Y.PipeConnector(_home);

        // pair: the CLI shows the code while it waits; the person approves it in the window
        var outText = new StringWriter();
        var errText = new StringWriter();
        var stdout = TextWriter.Synchronized(outText);
        var stderr = TextWriter.Synchronized(errText);
        string Out() { lock (stdout) return outText.ToString(); }
        var pairing = Y.Cli.RunAsync(["pair", "--name", "cc", "--level", "read"], Env, new StringReader(""), stdout, stderr, connector, Ct);
        JsonNode? shown = null;
        for (var i = 0; i < 200 && shown is null; i++)
        {
            shown = service.ListForWindow()["pairings"]!.AsArray().FirstOrDefault();
            if (shown is null) await Task.Delay(50, Ct);
        }
        Assert.NotNull(shown);
        var code = shown["code"]!.GetValue<string>();
        for (var i = 0; i < 200 && !Out().Contains(code); i++) await Task.Delay(25, Ct);
        Assert.Contains(code, Out());
        using (Actor.Use(Actor.Window)) service.DecidePairing(shown["id"]!.GetValue<string>(), approve: true, level: null);
        Assert.Equal(Y.ExitCodes.Ok, await pairing.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        var key = Keys.Load("cc");
        Assert.NotNull(key);
        Assert.DoesNotContain(key, outText.ToString());
        Assert.DoesNotContain(key, errText.ToString());
        Assert.True(UserOnlyFile.IsUserOnly(Path.Combine(ControlDiscovery.ClientsDir(_home), "cc.key")));

        // then a read over the same pipe, with the saved key
        var list = await RunAsync(connector, "backlog", "list");
        Assert.Equal(Y.ExitCodes.Ok, list.Exit);
        Assert.Contains("B042", list.Out);
        Assert.Contains("Fix the login form", list.Out);

        // the read level refuses an act call
        var tell = await RunAsync(connector, "tell", "alpha", "do it");
        Assert.Equal(Y.ExitCodes.Refused, tell.Exit);
        Assert.Contains("needs the act level", tell.Err);
    }
}
