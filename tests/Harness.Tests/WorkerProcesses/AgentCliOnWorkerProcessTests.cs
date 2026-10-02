using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// CONTROL AND WORKERS AS REAL PROCESSES: the sign-in probe, the tool pre-flight and the launch check
/// run on a worker - on the fake <c>claude</c> only the worker can find - and control keeps their files
/// and routes as they were; with no worker nothing is measured and nothing reads ok; and a person's
/// update waits for runs on two workers, holds launches on both and runs once.
/// </summary>
[Collection("worker processes")]
public sealed class AgentCliOnWorkerProcessTests
{
    [Fact]
    public async Task The_probe_pre_flight_and_launch_check_run_on_the_worker_and_control_keeps_their_files()
    {
        await using var bed = new ProcessBed();
        bed.WriteFakeClaude();
        await bed.StartControlAsync();
        var worker = bed.StartCliWorker("w1");
        await bed.UntilAsync("w1 is connected", async () => (await bed.WorkersAsync()).Any(w => w.GetProperty("id").GetString() == "w1"));

        // A catalog save runs the pre-flight and the launch check again, now that a worker is there.
        await bed.SaveCatalogAsync();

        // THE PROBE: signed in, as the worker's status command said - the worker's words, measured on it.
        JsonElement auth = default;
        await bed.UntilAsync("claude-headless is measured", async () =>
            (await bed.AuthOfAsync("claude-headless")) is { } found && (auth = found).GetProperty("authenticated").ValueKind == JsonValueKind.True);
        Assert.True(auth.GetProperty("installed").GetBoolean());
        Assert.Equal("The status command exited 0.", auth.GetProperty("detail").GetString());

        // THE LAUNCH CHECK: the fake's version flag through a member's launch, on the worker.
        await bed.UntilAsync("claude-headless's launch is checked", async () =>
            (await bed.AuthOfAsync("claude-headless")) is { } found && (auth = found).GetProperty("launch").GetProperty("result").GetString() == AgentLaunchReport.Ok);
        Assert.Equal(0, auth.GetProperty("launch").GetProperty("exitCode").GetInt32());

        // THE PRE-FLIGHT: the fake's listing, named - switched off by the preset's --strict-mcp-config.
        JsonElement tools = default;
        try
        {
            await bed.UntilAsync("claude-headless is listed", async () =>
                (await bed.ToolsOfAsync("claude-headless")) is { } found
                && (tools = found).GetProperty("switchedOff").EnumerateArray().Any(i => i.GetProperty("name").GetString() == CliBed.Tool));
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException($"{timeout.Message}\nLast listing: {tools}\nCalls: {string.Join(" | ", bed.CliCalls().Select(c => c.Arguments))}");
        }
        Assert.Contains(tools.GetProperty("ran").EnumerateArray(), r => r.GetString() == "claude mcp list");

        // CONTROL KEEPS THE FILES, as before: what the routes said, on its data root.
        var probed = AgentAuthRecord.Read(bed.Root)!;
        Assert.Equal("w1", probed.Worker);
        Assert.Equal(new CommandSignIn("claude", true, true, "The status command exited 0."), probed.For("claude"));
        Assert.Contains(
            AgentToolsRecord.Read(bed.Root)!.Presets.Single(p => p.Preset == "claude-headless").SwitchedOff,
            item => item.Name == CliBed.Tool);
        Assert.Equal(AgentLaunchReport.Ok, AgentLaunchChecksRecord.Read(bed.Root)!.Presets.Single(p => p.Preset == "claude-headless").Launch.Result);

        // EVERY CALL WAS THE WORKER'S: each kind at least once, each one a descendant of the worker and
        // none of control - which never had the fake on its PATH.
        var calls = bed.CliCalls();
        Assert.Contains(calls, c => c.Arguments == "auth status");
        Assert.Contains(calls, c => c.Arguments == "mcp list");
        Assert.Contains(calls, c => c.Arguments == "plugin list --json");
        Assert.Contains(calls, c => c.Arguments == "--version");
        Assert.All(calls, c =>
        {
            Assert.Contains(worker.Pid, c.Chain);
            Assert.DoesNotContain(bed.Control.Pid, c.Chain);
        });
    }

    [Fact]
    public async Task With_no_worker_nothing_is_measured_and_nothing_reads_ok()
    {
        await using var bed = new ProcessBed();
        bed.WriteFakeClaude();
        await bed.StartControlAsync();

        var auth = (await bed.AuthOfAsync("claude-headless"))!.Value;
        Assert.Equal(JsonValueKind.Null, auth.GetProperty("installed").ValueKind);
        Assert.Equal(JsonValueKind.Null, auth.GetProperty("authenticated").ValueKind);
        Assert.Equal(AgentAuthProbe.NoWorkerText, auth.GetProperty("detail").GetString());
        Assert.Equal(AgentLaunchReport.NotChecked, auth.GetProperty("launch").GetProperty("result").GetString());
        Assert.Equal(ProcessAgentRunner.NotCheckedHere, auth.GetProperty("launch").GetProperty("detail").GetString());

        JsonElement tools = default;
        await bed.UntilAsync("the pre-flight has run", async () => (await bed.ToolsOfAsync("claude-headless")) is { } found && (tools = found).ValueKind == JsonValueKind.Object);
        Assert.Equal(ToolVerdicts.NotMeasured, tools.GetProperty("verdict").GetString());
        Assert.Contains(WorkerListingRunner.NoWorkerText, tools.GetProperty("detail").GetString());

        // Recorded as not measured, for the doctor.
        Assert.Null(AgentAuthRecord.Read(bed.Root)!.For("claude")!.Installed);
        Assert.Empty(bed.CliCalls());
    }

    [Fact]
    public async Task An_update_waits_for_runs_on_two_workers_holds_launches_on_both_and_runs_once()
    {
        await using var bed = new ProcessBed();
        var updates = Directory.CreateDirectory(Path.Combine(bed.Out, "update")).FullName;
        var script = Path.Combine(bed.Work, "fake-update.sh");
        File.WriteAllText(script, $$$"""
            #!/bin/sh
            out='{{{updates}}}/'"$$"
            p=$$; : > "$out.chain"
            while [ -n "$p" ] && [ "$p" -gt 1 ]; do echo "$p" >> "$out.chain"; p=$(sed 's/.*) //' /proc/$p/stat 2>/dev/null | cut -d' ' -f2); done
            date +%s%3N > "$out.ended"
            echo updated
            """);

        await bed.StartControlAsync();
        await bed.SaveCatalogAsync(fake => fake with { Updates = new AgentUpdates(Update: ["sh", script]) });
        var w1 = bed.StartWorker("w1");
        var w2 = bed.StartWorker("w2");
        await bed.UntilAsync("w1 and w2 are connected", async () => (await bed.WorkersAsync()).Count == 2);
        var team = await bed.TeamAsync("Updates", "BlockOne", "BlockTwo", "Dev3");

        // (1) One run on each worker.
        (await bed.TellAsync(team, "BlockOne", "One.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("BlockOne runs", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "BlockOne")));
        (await bed.TellAsync(team, "BlockTwo", "Two.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("BlockTwo runs", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "BlockTwo")));
        var one = bed.FakeRuns().Single(r => r.Member == "BlockOne");
        var two = bed.FakeRuns().Single(r => r.Member == "BlockTwo");
        Assert.Equal(1, new[] { one.Chain.Contains(w1.Pid), two.Chain.Contains(w1.Pid) }.Count(x => x));
        Assert.Equal(1, new[] { one.Chain.Contains(w2.Pid), two.Chain.Contains(w2.Pid) }.Count(x => x));

        // (2) The update is asked for, and waits for both.
        var asked = await bed.Person.PostAsync("/api/agents/fake/update", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var state = await bed.UpdateStateAsync("fake");
        Assert.Equal(AgentUpdatePhases.Waiting, state.GetProperty("phase").GetString());
        Assert.Equal(["BlockOne", "BlockTwo"], state.GetProperty("inFlight").EnumerateArray().Select(h => h.GetProperty("member").GetString()).Order());

        // (3) A launch now is held, said on its card, and never started.
        (await bed.TellAsync(team, "Dev3", "Three.")).EnsureSuccessStatusCode();
        try
        {
            await bed.UntilAsync("Dev3 is held", async () =>
                (await bed.RowsOfAsync(team, "Dev3", MessageTypes.Progress)).Any(r => r.Payload.TryGetProperty("status", out var status) && status.GetString() == ProcessAgentRunner.HeldText("sh")));
        }
        catch (TimeoutException timeout)
        {
            var rows = await bed.RowsOfAsync(team, "Dev3");
            throw new TimeoutException($"{timeout.Message}\nDev3's rows: {string.Join(" | ", rows.Select(r => r.Type + " " + r.Payload))}\nState: {await bed.UpdateStateAsync("fake")}");
        }
        Assert.Equal("Dev3", Assert.Single((await bed.UpdateStateAsync("fake")).GetProperty("held").EnumerateArray()).GetProperty("member").GetString());
        Assert.DoesNotContain(bed.FakeRuns(), r => r.Member == "Dev3");

        // (4) One run ends: still waiting, nothing ran.
        bed.Go("BlockOne");
        await bed.UntilAsync("BlockOne completed", async () => (await bed.RowsOfAsync(team, "BlockOne", MessageTypes.Completed)).Count >= 1);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(AgentUpdatePhases.Waiting, (await bed.UpdateStateAsync("fake")).GetProperty("phase").GetString());
        Assert.Empty(Directory.GetFiles(updates, "*.ended"));

        // (5) The other ends: the update runs once, on one worker, and is recorded.
        bed.Go("BlockTwo");
        await bed.UntilAsync("the update is done", async () => (await bed.UpdateStateAsync("fake")).GetProperty("phase").GetString() == AgentUpdatePhases.Done);
        var ended = Assert.Single(Directory.GetFiles(updates, "*.ended"));
        var chain = File.ReadAllLines(Path.ChangeExtension(ended, ".chain")).Select(int.Parse).ToList();
        Assert.Equal(1, new[] { chain.Contains(w1.Pid), chain.Contains(w2.Pid) }.Count(x => x));
        Assert.DoesNotContain(bed.Control.Pid, chain);
        Assert.Single(File.ReadAllLines(Path.Combine(bed.Root, "cli-versions.jsonl")), l => l.Contains("\"by\":\"update\"", StringComparison.Ordinal));

        // (6) The held launch's process started after the update ended.
        await bed.UntilAsync("Dev3's CLI started", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "Dev3")));
        var dev3 = bed.FakeRuns().First(r => r.Member == "Dev3");
        var started = new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(bed.Out, dev3.Name + ".chain")), TimeSpan.Zero);
        var updateEnded = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(File.ReadAllText(ended).Trim()));
        Assert.True(started >= updateEnded, $"Dev3's CLI started {started:O}, the update ended {updateEnded:O}");
    }
}
