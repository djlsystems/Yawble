using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// End to end through the app's MCP server and a fake program: the Tool calls row of a gh or az call keeps the action and the option
/// names, and of the content options and phone numbers only their length or last 4 digits, while the program itself gets every word.
/// An allowed call and an asked-then-approved one both go through the row writer. The fake program only echoes its arguments.
/// </summary>
public class CommandLineCallRowVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public BusEngine Engine = null!;
        public IntegrationService Integrations = null!;
        public long Alpha;
        private readonly ConcurrentDictionary<int, (string Tool, object Args, TaskCompletionSource<string> Done)> _calls = new();
        private int _next;

        public static async Task<Rig> Make()
        {
            var r = new Rig();
            var runner = new ScriptedRunner { Default = r.Handle };
            r.Engine = new BusEngine(r.Db.OpenStore(), runner, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            r.Integrations = new IntegrationService(r.Db.ConnectionString, r.Engine);
            r.Engine.Integrations = r.Integrations;
            r.Alpha = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await r.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await r.Engine.StartAsync(Ct);
            return r;
        }

        public async Task AddFakeCli(string name)
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
            await Rpc("integrations.enable", new JsonObject { ["name"] = name, ["teamId"] = Alpha, ["on"] = true });
        }

        private async Task<RunResult> Handle(RunContext run, CancellationToken ct)
        {
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

        public async Task<string> Call(string tool, params string[] args)
        {
            var id = Interlocked.Increment(ref _next);
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = (tool, new { args, reason = "needed" }, done);
            await Engine.PersonTellAsync("Alpha", "Ada", $"call#{id}#", ct: Ct);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        public async Task<JsonNode?> Rpc(string method, JsonObject p) => await Integrations.DispatchAsync(method, p, Ct);

        public List<(string Args, string Decision, long? Exit)> Rows(string integration)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(Db.ConnectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT args, decision, exit_code FROM integration_calls WHERE integration = $i ORDER BY id;";
            cmd.Parameters.AddWithValue("$i", integration);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string, long?)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2)));
            return list;
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
    public async Task AnAllowedGhCall_TheProgramGetsEveryWord_TheRowKeepsOnlyTheLengthsOfWhatWasSaid()
    {
        await using var r = await Rig.Make();
        await r.AddFakeCli("gh");
        await r.Rpc("integrations.saveRule", new JsonObject { ["name"] = "gh", ["teamId"] = r.Alpha, ["pattern"] = "issue create*", ["decision"] = "allow" });

        var said = await r.Call("gh", "issue", "create", "--title", "Quarterly invoice", "--body=Paid by Dana", "-R", "acme/web", "--label", "billing");
        Assert.Contains("Quarterly invoice", said); // the program itself got the real words
        Assert.Contains("Paid by Dana", said);

        var row = Assert.Single(r.Rows("gh"));
        Assert.Equal("allow", row.Decision);
        Assert.Equal(0, row.Exit);
        var args = JsonNode.Parse(row.Args)!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        Assert.Equal(["issue", "create", "--title", "(17 chars)", "--body=(12 chars)", "-R", "acme/web", "--label", "billing"], args);
        Assert.Equal(0, r.Scalar("SELECT count(*) FROM integration_calls WHERE args LIKE '%Quarterly%' OR args LIKE '%Dana%'"));

        // The Logs page's Tool calls source shows the same kept words.
        var logs = new LogService(r.Db.Dir, r.Db.ConnectionString, TimeProvider.System);
        var page = logs.Query(new LogQuery("tools", Limit: 10));
        var shown = page["rows"]!.AsArray().Select(x => x!.AsObject().ToJsonString()).Single();
        Assert.Contains("(17 chars)", shown);
        Assert.DoesNotContain("Quarterly", shown);
        Assert.DoesNotContain("Dana", shown);
    }

    [Fact]
    public async Task AnAskedThenApprovedAzCall_ItsRowKeepsPhonesToTheLast4_AndMultiLineWordsAsTheirLength()
    {
        await using var r = await Rig.Make();
        await r.AddFakeCli("az");

        var asked = await r.Call("az", "communication", "sms", "send", "--sender", "+15550009876", "--recipient", "+15550001234", "--message", "Your code is 4411");
        Assert.StartsWith("asked:", asked);
        var ask = (JsonObject)Assert.Single((JsonArray)(await r.Rpc("integrations.asks", new JsonObject { ["teamId"] = r.Alpha }))!)!;
        await r.Rpc("integrations.approve", new JsonObject { ["id"] = ask["id"]!.GetValue<long>(), ["remember"] = "once" });
        await Wait.UntilAsync(() => r.Integrations.PendingCount() == 0 && r.Scalar("SELECT count(*) FROM integration_calls WHERE integration = 'az' AND decision = 'approved'") == 1,
            "the approved call ran");

        foreach (var row in r.Rows("az"))
        {
            Assert.DoesNotContain("15550009876", row.Args);
            Assert.DoesNotContain("15550001234", row.Args);
            Assert.DoesNotContain("Your code", row.Args);
        }
        var approved = r.Rows("az").Single(x => x.Decision == "approved");
        var args = JsonNode.Parse(approved.Args)!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        Assert.Equal(["communication", "sms", "send", "--sender", "...9876", "--recipient", "...1234", "--message", "(17 chars)"], args);
    }
}
