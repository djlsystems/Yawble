using System.Diagnostics;
using Yawble.Core.Bus;
using Yawble.Core.Ipc;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// Killing the core mid-run gives interrupted on restart. The real core executable runs
/// the bus with fake runners (YAWBLE_RUNNER), is killed with SIGKILL while four runs are open and two members
/// are queued, and is then started again on the same database.
/// </summary>
public class CoreKillRestartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KillingTheCoreMidRunGivesInterruptedOnRestart_AndTheQueueKeepsItsOrder()
    {
        using var db = new TempDb();
        var members = new[] { "m1", "m2", "m3", "m4", "m5", "m6" };
        var roots = new Dictionary<string, long>();

        // Seed the team and six instructions with the core not running (default limit: 4 at once).
        var seeding = new BusEngine(db.OpenStore(), new UnavailableRunner());
        await seeding.CreateTeamAsync("Kill", ct: Ct);
        foreach (var m in members) await seeding.AddMemberAsync("Kill", m, ct: Ct);
        foreach (var m in members) roots[m] = (await seeding.PersonTellAsync("Kill", m, $"long job {m}", ct: Ct)).Seq;
        await seeding.DisposeAsync(); // never started: nothing pumped or admitted

        using var probe = db.OpenStore();

        // ---- first core process: runs hang until the process dies
        var core = StartCore(db, "fake-hang");
        try
        {
            await Wait.UntilAsync(() => probe.Runs("running").Count == 4 && probe.Queue().Count == 2, "4 running, 2 queued", TimeSpan.FromSeconds(30));
            core.Kill(entireProcessTree: true); // SIGKILL: no shutdown code runs
            await core.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);
        }
        finally
        {
            if (!core.HasExited) core.Kill(entireProcessTree: true);
            core.Dispose();
        }

        var open = probe.Runs("running");
        Assert.Equal(["Kill/m1", "Kill/m2", "Kill/m3", "Kill/m4"], open.Select(r => probe.Member(r.MemberId)!.Qualified));
        Assert.Equal(["Kill/m5", "Kill/m6"], probe.Queue().Select(q => probe.Member(q.MemberId)!.Qualified));
        Assert.Equal(4, probe.Pending(started: true).Count);

        // ---- second core process on the same database: members hand back and finish
        core = StartCore(db, "fake-complete");
        try
        {
            await Wait.UntilAsync(
                () => probe.Runs("running").Count == 0 && probe.Queue().Count == 0 && probe.Pending().Count == 0
                      && probe.Runs().Count(r => r.State == "completed") >= 2,
                "restart to settle", TimeSpan.FromSeconds(30));
        }
        finally
        {
            core.StandardInput.Close();
            if (!core.WaitForExit(15_000)) core.Kill(entireProcessTree: true);
            core.Dispose();
        }

        var runs = probe.Runs();
        foreach (var run in runs)
        {
            TestContext.Current.SendDiagnosticMessage(
                $"run {run.Id} {probe.Member(run.MemberId)!.Qualified} queue={run.QueueId} causation={run.CausationSeq} {run.State} {run.Reason}");
        }

        // The four killed runs are interrupted - not re-run - and each is reported once on the bus,
        // caused by the instruction it was running, so it lands in that workflow at depth 1.
        foreach (var m in new[] { "m1", "m2", "m3", "m4" })
        {
            var run = Assert.Single(runs, r => r.CausationSeq == roots[m]);
            Assert.Equal("interrupted", run.State);
            Assert.NotNull(run.EndedAt);
            var failed = Assert.Single(probe.AllMessages(), x => x.Type == MessageTypes.Failed && x.CausationSeq == roots[m]);
            Assert.Equal(($"Kill/{m}", roots[m], 1), (failed.Source, failed.CorrelationId, failed.Depth));
            Assert.Equal(FailureClasses.Interrupted, Payload.Field(failed, "failureClass"));
        }

        // The two queued members kept their place: admitted first after the restart, m5 then m6, and they completed.
        var afterRestart = runs.Where(r => r.State != "interrupted").ToList();
        Assert.Equal(["Kill/m5", "Kill/m6"], afterRestart.Take(2).Select(r => probe.Member(r.MemberId)!.Qualified));
        Assert.All(afterRestart.Take(2), r => Assert.Equal("completed", r.State));
        // The Manager was woken by the interruptions and the handbacks, through the same queue.
        Assert.Contains(afterRestart, r => probe.Member(r.MemberId)!.Name == "Manager");
    }

    private static Process StartCore(TempDb db, string runner)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "yawble-core.dll"), "--ipc", IpcEndpoint.NewAddress(db.Dir), "--data-dir", db.Dir },
            Environment = { ["YAWBLE_IPC_TOKEN"] = token, [BusHost.RunnerVariable] = runner },
        };
        CoreLaunch.IsolateCompanionHome(start); // never the real %APPDATA%\yawble-companion
        var process = Process.Start(start)!;
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) TestContext.Current.SendDiagnosticMessage("core: " + e.Data); };
        process.BeginErrorReadLine();
        return process;
    }
}
