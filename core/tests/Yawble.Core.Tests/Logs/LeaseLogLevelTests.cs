using System.Text.Json.Nodes;
using Yawble.Core.Bus;
using Yawble.Core.Gateway;
using Yawble.Core.Logs;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Logs;

/// <summary>
/// The heavy-work lease's Diagnostics lines: a place in the queue and the lease handed to the next in line are routine
/// (debug), like held and released; a holder passing the 30-minute limit is a warning, with its ids. Scripted runs over
/// a temp database and a fake clock.
/// </summary>
public sealed class LeaseLogLevelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDb _db = new();
    private readonly FakeClock _clock = new(Now);

    public LeaseLogLevelTests() => _db.OpenStore().Dispose(); // the schema

    public void Dispose() => _db.Dispose();

    private static List<DiagEntry> Captured(List<DiagEntry> seen, string contains)
    {
        lock (seen) return [.. seen.Where(e => e.Text.Contains(contains, StringComparison.Ordinal))];
    }

    private static async Task Lease(RunContext run, string action, string? what = null)
    {
        var args = what is null ? new JsonObject { ["action"] = action } : new JsonObject { ["action"] = action, ["what"] = what };
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "lease", ["arguments"] = args },
        };
        await new McpBusServer(run).HandleAsync(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()), Ct);
    }

    [Fact]
    public async Task QueuedAndHandedTo_AreDebug_PassingTheLimit_IsAWarningWithItsIds()
    {
        var seen = new List<DiagEntry>();
        using var capture = Diag.Capture(e => { lock (seen) seen.Add(e); });
        var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new Dictionary<string, TaskCompletionSource<RunContext>>
        {
            ["Alpha/Ada"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["Alpha/Bo"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var runner = new ScriptedRunner();
        foreach (var who in started.Keys)
        {
            runner.On(who, async (run, ct) =>
            {
                started[who].TrySetResult(run);
                await end.Task.WaitAsync(ct);
                return RunResult.Ok;
            });
        }
        await using var engine = new BusEngine(new BusStore(_db.ConnectionString, _clock), runner, new BusOptions
        {
            SafetyPoll = TimeSpan.FromMilliseconds(100),
            IdleOffer = false,
            MaxConcurrentAgents = () => 2,
        });
        await engine.CreateTeamAsync("Alpha", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Ada", ct: Ct);
        await engine.AddMemberAsync("Alpha", "Bo", ct: Ct);
        await engine.StartAsync(Ct);
        await engine.PersonTellAsync("Alpha", "Ada", "work", ct: Ct);
        await engine.PersonTellAsync("Alpha", "Bo", "work", ct: Ct);
        var ada = await started["Alpha/Ada"].Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var bo = await started["Alpha/Bo"].Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        await Lease(ada, "acquire", "suite");
        await Lease(bo, "acquire", "build");
        var queued = Assert.Single(Captured(seen, "lease: Alpha/Bo queued for \"build\""));
        Assert.Equal(LogLevels.Debug, queued.Level);
        Assert.Equal("Alpha/Bo", queued.Field("member"));

        await Lease(ada, "release");
        var handed = Assert.Single(Captured(seen, "lease: handed to Alpha/Bo"));
        Assert.Equal(LogLevels.Debug, handed.Level);
        Assert.Equal(bo.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture), handed.Field("run"));

        // Bo keeps it past the limit: the next pump pass takes it back and says so.
        _clock.Advance(BusEngine.LeaseLimit + TimeSpan.FromMinutes(1));
        await Wait.UntilAsync(() => Task.FromResult(Captured(seen, "passed the 30-minute limit").Count > 0), "the limit line");
        var limit = Assert.Single(Captured(seen, "passed the 30-minute limit"));
        Assert.Equal("lease: Alpha/Bo passed the 30-minute limit", limit.Text);
        Assert.Equal(LogLevels.Warning, limit.Level);
        Assert.Equal("Alpha", limit.Field("team"));
        Assert.Equal("Alpha/Bo", limit.Field("member"));
        Assert.Equal("build", limit.Field("step"));
        end.TrySetResult();
    }
}
