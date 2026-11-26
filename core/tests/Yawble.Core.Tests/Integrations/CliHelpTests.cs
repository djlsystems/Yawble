using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>cli_help reads the help of a command path: command words only, and never of a command the floors or a rule refuse.</summary>
public class CliHelpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A team (Alpha/Ada) with a fake "gh": a script that prints its arguments and adds a line to ran.txt each time it runs.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDb Db = new();
        public BusEngine Engine = null!;
        public IntegrationService Integrations = null!;
        public long Alpha;
        private readonly ConcurrentDictionary<int, (object Args, TaskCompletionSource<string> Done)> _calls = new();
        private int _next;

        private string Marker => Path.Combine(Db.Dir, "ran.txt");

        /// <summary>How many times the fake program ran.</summary>
        public int Runs => File.Exists(Marker) ? File.ReadAllLines(Marker).Length : 0;

        public static async Task<Rig> Make()
        {
            var r = new Rig();
            r.Engine = new BusEngine(r.Db.OpenStore(), new ScriptedRunner { Default = r.Handle }, new BusOptions { SafetyPoll = TimeSpan.FromMilliseconds(100), IdleOffer = false });
            r.Integrations = new IntegrationService(r.Db.ConnectionString, r.Engine);
            r.Engine.Integrations = r.Integrations;
            r.Alpha = await r.Engine.CreateTeamAsync("Alpha", ct: Ct);
            await r.Engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
            await r.Engine.StartAsync(Ct);

            string path;
            if (OperatingSystem.IsWindows())
            {
                path = Path.Combine(r.Db.Dir, "gh.cmd");
                File.WriteAllText(path, "@echo off\r\necho x>>\"%~dp0ran.txt\"\r\necho ran %*\r\n");
            }
            else
            {
                path = Path.Combine(r.Db.Dir, "gh");
                File.WriteAllText(path, "#!/bin/sh\necho x >> \"$(dirname \"$0\")/ran.txt\"\necho ran \"$@\"\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection(r.Db.ConnectionString))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO integrations (kind, name, path, created_at, updated_at) VALUES ('cli', 'gh', $p, '2026-01-01', '2026-01-01');";
                cmd.Parameters.AddWithValue("$p", path);
                cmd.ExecuteNonQuery();
            }
            await r.Integrations.DispatchAsync("integrations.enable", new JsonObject { ["name"] = "gh", ["teamId"] = r.Alpha, ["on"] = true }, Ct);
            return r;
        }

        private async Task<RunResult> Handle(RunContext run, CancellationToken ct)
        {
            foreach (var (id, call) in _calls)
            {
                if (!run.Items.Any(m => m.Payload.Contains($"call#{id}#", StringComparison.Ordinal))) continue;
                _calls.TryRemove(id, out _);
                try
                {
                    var body = new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                        ["params"] = new JsonObject { ["name"] = "cli_help", ["arguments"] = System.Text.Json.JsonSerializer.SerializeToNode(call.Args) },
                    };
                    var reply = await new McpBusServer(run).HandleAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
                    call.Done.TrySetResult(JsonNode.Parse(reply.Json!)!["result"]!["content"]![0]!["text"]!.GetValue<string>());
                }
                catch (Exception e) { call.Done.TrySetException(e); }
            }
            return RunResult.Ok;
        }

        /// <summary>Ada calls cli_help(cli: "gh", args) in a run of her own; the text she reads.</summary>
        public async Task<string> Help(params string[] args)
        {
            var id = Interlocked.Increment(ref _next);
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls[id] = (new { cli = "gh", args }, done);
            await Engine.PersonTellAsync("Alpha", "Ada", $"call#{id}#", ct: Ct);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
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
    public async Task ACommandPathGetsItsHelp_WithTheHelpFlagAdded()
    {
        await using var rig = await Rig.Make();
        Assert.Contains("ran pr create --help", await rig.Help("pr", "create"));
        Assert.Contains("ran --help", await rig.Help());
        Assert.Equal(2, rig.Runs);
    }

    public static TheoryData<string[]> NotACommandPath => new()
    {
        new[] { "-c", "alias.x=!calc", "x" }, // git's way to run a program through an alias set on the line
        new[] { "alias.x=!calc" },
        new[] { "--" },
        new[] { "pr", "--", "create" },
        new[] { "pr", "list", "--web" },
        new[] { "-h" },
        new[] { "pr create" },
        new[] { "pr", "9" },
        new[] { "a", "b", "c", "d", "e", "f", "g" },
    };

    [Theory]
    [MemberData(nameof(NotACommandPath))]
    public async Task AnythingButCommandWordsIsRefused_AndNothingRuns(string[] args)
    {
        await using var rig = await Rig.Make();
        var text = await rig.Help(args);
        Assert.StartsWith("refused:", text);
        Assert.Contains("cli_help takes a command", text); // it says what is accepted
        Assert.Equal(0, rig.Runs);
    }

    [Fact]
    public async Task ACommandTheFloorsOrARuleRefuseHasNoHelpRun()
    {
        await using var rig = await Rig.Make();
        Assert.StartsWith("refused:", await rig.Help("auth", "token")); // the credential floor
        Assert.StartsWith("refused:", await rig.Help("browse")); // gh's own floor
        Assert.StartsWith("refused:", await rig.Help("repo", "delete")); // the preset's Deny
        await rig.Integrations.DispatchAsync("integrations.saveRule", new JsonObject { ["name"] = "gh", ["pattern"] = "issue*", ["decision"] = "deny" }, Ct);
        Assert.StartsWith("refused:", await rig.Help("issue", "close")); // the person's Deny rule
        Assert.Equal(0, rig.Runs);
        Assert.Contains("ran pr merge --help", await rig.Help("pr", "merge")); // a command that asks still has its help
    }

    [Fact]
    public void ThePathCheckAcceptsCommandWordsOnly()
    {
        Assert.Null(CliRules.HelpPathProblem([]));
        Assert.Null(CliRules.HelpPathProblem(["pr", "create"]));
        Assert.Null(CliRules.HelpPathProblem(["Storage", "account", "show-connection-string"]));
        Assert.Null(CliRules.HelpPathProblem(["a", "b", "c", "d", "e", "f"]));
        Assert.NotNull(CliRules.HelpPathProblem(["a", "b", "c", "d", "e", "f", "g"]));
        foreach (var bad in new[] { "-c", "--", "--help", "alias.x=!cmd", "9lives", "a b", "a/b", "a\\b", "x;y", "" })
            Assert.NotNull(CliRules.HelpPathProblem([bad]));
    }
}
