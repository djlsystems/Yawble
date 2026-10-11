using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A RUNNING MEMBER'S LIVE VIEW AND A FINISHED RUN'S TRANSCRIPT ARE READ ON A WORKER, with control and
/// the worker as real processes and a fake CLI that writes a transcript line a second: the live view
/// stalls while its worker is stopped and answers 503 with a sentence once control sees the drop,
/// though the file is on the same disk; a finished transcript answers 503 with a sentence with no
/// worker connected, and 410 once the file is gone; and a transcript is redacted of a key only the
/// worker's environment held.
/// </summary>
[Collection("worker processes")]
public sealed class LiveViewOnWorkerProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>More lines than a stopped worker can have had on their way: the fake writes one a second.</summary>
    private const int InFlightLines = 10;

    [Fact]
    public async Task A_running_members_live_view_is_followed_on_its_worker()
    {
        await using var bed = new ProcessBed();
        bed.AddFakes();

        // Dropped after three missed one-second pings; its grace far longer than the stop.
        await bed.StartControlAsync(graceSeconds: 60, keepAliveSeconds: 1);
        var w1 = bed.StartFakeWorker("w1");
        await bed.UntilWorkerAsync("w1", WorkersView.Connected);
        var team = await LiveTeamAsync(bed, "BlockLive");
        (await bed.TellAsync(team, "BlockLive", "Write a line a second.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("BlockLive is running", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "BlockLive")));

        var route = $"/api/teams/{team}/members/BlockLive/live";
        await bed.UntilAsync("the live view answers", async () => (await bed.Person.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, Ct)).StatusCode == HttpStatusCode.OK);
        using (var watching = await bed.Person.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, watching.StatusCode);
            using var lines = new StreamReader(await watching.Content.ReadAsStreamAsync(Ct));
            Assert.Equal("\tUser: tick 1", await lines.ReadLineAsync(Ct).AsTask().WaitAsync(ProcessBed.Bound, Ct));
            Assert.Equal("\tUser: tick 2", await lines.ReadLineAsync(Ct).AsTask().WaitAsync(ProcessBed.Bound, Ct));

            ProcessBed.Signal(w1, "STOP");
            try
            {
                // What was already on its way arrives; then nothing, while the fake still writes a line a second.
                // Bounded, so a view that kept streaming fails here within the bed's bound, not when the fake stops.
                var draining = Stopwatch.StartNew();
                var drained = 0;
                var pending = lines.ReadLineAsync(Ct).AsTask();
                while (await Task.WhenAny(pending, Task.Delay(1500, Ct)) == pending)
                {
                    await pending;
                    Assert.True(
                        ++drained <= InFlightLines && draining.Elapsed < ProcessBed.Bound,
                        $"the live view kept streaming while its worker was stopped: {drained} lines in {draining.Elapsed.TotalSeconds:0} s");
                    pending = lines.ReadLineAsync(Ct).AsTask();
                }

                var writtenBefore = Lines(bed, "BlockLive");
                await Task.Delay(3000, Ct);
                Assert.False(pending.IsCompleted, "the live view kept streaming while its worker was stopped");
                Assert.True(Lines(bed, "BlockLive") > writtenBefore, "the fake did not keep writing");

                await bed.UntilWorkerAsync("w1", WorkersView.Dropped);
                var dropped = await bed.Person.GetAsync(route, Ct);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, dropped.StatusCode);
                Assert.Equal(
                    "The worker running this run (w1) is not connected, so its live view cannot be read; it comes back if the worker reconnects within its grace.",
                    (await dropped.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());
            }
            finally
            {
                ProcessBed.Signal(w1, "CONT");
            }
        }

        await bed.UntilWorkerAsync("w1", WorkersView.Connected);
        using (var again = await bed.Person.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            using var lines = new StreamReader(await again.Content.ReadAsStreamAsync(Ct));
            Assert.Equal("\tUser: tick 1", await lines.ReadLineAsync(Ct).AsTask().WaitAsync(ProcessBed.Bound, Ct));
        }

        bed.Go("BlockLive");
        await bed.UntilAsync("BlockLive completed", async () => (await bed.RowsOfAsync(team, "BlockLive", MessageTypes.Completed)).Count >= 1);
    }

    [Fact]
    public async Task A_finished_runs_transcript_is_read_on_a_worker()
    {
        await using var bed = new ProcessBed();
        bed.AddFakes();
        await bed.StartControlAsync(graceSeconds: 2);
        var w1 = bed.StartFakeWorker("w1");
        await bed.UntilWorkerAsync("w1", WorkersView.Connected);
        var team = await LiveTeamAsync(bed, "Lively");
        bed.Go("Lively");
        (await bed.TellAsync(team, "Lively", "Finish.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("Lively completed", async () => (await bed.RowsOfAsync(team, "Lively", MessageTypes.Completed)).Count >= 1);

        var (route, path) = await FirstRunAsync(bed, team, "Lively");
        var read = await bed.Person.GetAsync(route, Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Contains("User: last", await read.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // No worker: the file is still on the disk control shares, and control does not read it.
        w1.KillAlone();
        await bed.UntilAsync("no worker is connected", async () => (await bed.WorkersAsync()).Count == 0);
        Assert.True(File.Exists(path));

        var none = await bed.Person.GetAsync(route, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, none.StatusCode);
        Assert.Equal(
            "No worker is connected, and a run's transcript is read as the agent on a worker; it can be read once a worker connects.",
            (await none.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString());

        // Gone, read by a worker: 410 as before.
        File.Delete(path);
        bed.StartFakeWorker("w2");
        await bed.UntilWorkerAsync("w2", WorkersView.Connected);
        // Listed as connected a moment before control can send it a request (WorkerConnections.Attached):
        // a read in that moment is still the 503 above, so wait until the worker answers.
        HttpResponseMessage gone = null!;
        await bed.UntilAsync("w2 answers the read", async () =>
            (gone = await bed.Person.GetAsync(route, Ct)).StatusCode != HttpStatusCode.ServiceUnavailable);
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        Assert.Equal("This run's transcript is no longer on disk.", await gone.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_finished_transcript_is_redacted_of_a_key_only_the_worker_held()
    {
        // A credential variable this process - and so control, which inherits its environment - does not hold.
        var name = AgentEnvironment.ProviderVariables.Order(StringComparer.Ordinal)
            .FirstOrDefault(n => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n)));
        Assert.NotNull(name);
        var value = "worker-only-" + Guid.NewGuid().ToString("N");

        await using var bed = new ProcessBed();
        bed.AddFakes();
        await bed.StartControlAsync();
        bed.StartFakeWorker("w1", new Dictionary<string, string> { [name] = value });
        await bed.UntilWorkerAsync("w1", WorkersView.Connected);
        var team = await LiveTeamAsync(bed, "Keyed");
        bed.Go("Keyed");
        (await bed.TellAsync(team, "Keyed", "Finish.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("Keyed completed", async () => (await bed.RowsOfAsync(team, "Keyed", MessageTypes.Completed)).Count >= 1);

        // No run's own child was given it, so control never learned it from a run either.
        Assert.All(bed.FakeRuns().Where(r => r.Member == "Keyed"), run => Assert.DoesNotContain(value, run.Environment, StringComparison.Ordinal));

        // The agent wrote it into its transcript.
        var (route, path) = await FirstRunAsync(bed, team, "Keyed");
        await File.AppendAllTextAsync(path, $"{{\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":\"key {value}\"}}}}\n", Ct);
        Assert.Contains(value, await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);

        var read = await bed.Person.GetAsync(route, Ct);
        var text = await read.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        Assert.Contains("User: key " + DiagnosticRedaction.Placeholder, text, StringComparison.Ordinal);
    }

    /// <summary>A team whose <paramref name="member"/> runs the live fake.</summary>
    private static async Task<string> LiveTeamAsync(ProcessBed bed, string member)
    {
        var team = await bed.TeamAsync("Live" + member);
        (await bed.Person.PostAsJsonAsync($"/api/teams/{team}/containers", new { name = member, agent = ConciergeBed.Live })).EnsureSuccessStatusCode();
        return team;
    }

    /// <summary>The member's first finished run: its transcript route, and the transcript its row recorded.</summary>
    private static async Task<(string Route, string Path)> FirstRunAsync(ProcessBed bed, string team, string member)
    {
        var row = (await bed.RowsOfAsync(team, member, MessageTypes.Completed)).First();
        return (
            $"/api/teams/{team}/members/{member}/runs/{row.Seq}/transcript",
            row.Payload.GetProperty(PayloadFields.AgentTranscript).GetString()!);
    }

    /// <summary>The transcript the member's first run is writing, as it recorded it.</summary>
    private static string TranscriptOf(ProcessBed bed, string member)
    {
        var run = bed.FakeRuns().Where(r => r.Member == member).OrderBy(r => r.Pid).First();
        return File.ReadAllText(Path.Combine(bed.Out, run.Name + ".transcript")).Trim();
    }

    private static int Lines(ProcessBed bed, string member) =>
        File.Exists(TranscriptOf(bed, member)) ? File.ReadAllLines(TranscriptOf(bed, member)).Length : 0;
}
