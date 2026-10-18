using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A RUN NOBODY WATCHES IS RECORDED STEP BY STEP, with control and the worker as real processes: the
/// live fake writes a transcript line a second on the worker, nobody opens its live view, and once
/// the run ends <c>GET /api/run-steps</c> answers one step per line, every one naming the run's
/// terminal row, with no word of the transcript.
/// </summary>
[Collection("worker processes")]
public sealed class AgentStepsOnWorkerProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_nobody_watches_on_a_worker_records_each_step_and_names_its_terminal_row()
    {
        await using var bed = new ProcessBed();
        bed.AddFakes();
        await bed.StartControlAsync();
        bed.StartFakeWorker("w1");
        await bed.UntilWorkerAsync("w1", WorkersView.Connected);

        var team = await bed.TeamAsync("Stepped");
        (await bed.Person.PostAsJsonAsync($"/api/teams/{team}/containers", new { name = "Quiet", agent = ConciergeBed.Live }, Ct)).EnsureSuccessStatusCode();
        (await bed.TellAsync(team, "Quiet", "Write a line a second.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("Quiet wrote three lines", () => Task.FromResult(Lines(bed, "Quiet") >= 3));
        bed.Go("Quiet");

        await bed.UntilAsync("Quiet completed", async () => (await bed.RowsOfAsync(team, "Quiet", MessageTypes.Completed)).Count >= 1);
        var terminal = (await bed.RowsOfAsync(team, "Quiet", MessageTypes.Completed)).First();
        var written = Lines(bed, "Quiet");

        // Every line the agent wrote is a step, the last among them: the follow ends after the run does.
        List<JsonElement> steps = [];
        string raw = "";
        await bed.UntilAsync("every line is recorded", async () =>
        {
            raw = await bed.Person.GetStringAsync("/api/run-steps?take=500", Ct);
            steps = [.. JsonDocument.Parse(raw).RootElement.GetProperty("steps").EnumerateArray()
                .Where(s => s.GetProperty("team").GetString() == team && s.GetProperty("member").GetString() == "Quiet")];
            return steps.Sum(s => s.GetProperty("count").GetInt32()) == written;
        });

        Assert.All(steps, step =>
        {
            Assert.Equal("user", step.GetProperty("kind").GetString());
            Assert.Equal("", step.GetProperty("label").GetString());
            Assert.Equal(terminal.Seq, step.GetProperty("run").GetInt64());
        });
        Assert.DoesNotContain("tick", raw, StringComparison.Ordinal);
    }

    private static int Lines(ProcessBed bed, string member)
    {
        var run = bed.FakeRuns().Where(r => r.Member == member).OrderBy(r => r.Pid).FirstOrDefault();
        if (run is null) return 0;

        var path = File.ReadAllText(Path.Combine(bed.Out, run.Name + ".transcript")).Trim();
        return File.Exists(path) ? File.ReadAllLines(path).Length : 0;
    }
}
