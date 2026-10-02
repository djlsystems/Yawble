using Harness.Containers;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A PERSON'S CONCIERGE ON A WORKER, with control and the worker as real processes and a fake
/// interactive CLI: it starts on the worker, and keystrokes, a resize and output cross control's relay;
/// ending it, by the person or by the CLI, releases its heavy lease; with no worker the person reads
/// "waiting for a worker" and the session starts when one connects; what is typed while the worker is
/// dropped never reaches the CLI; a lost worker ends the session with a sentence. Every wait polls with a
/// bound; processes are stopped only by the PIDs recorded when they started.
/// </summary>
[Collection("worker processes")]
public sealed class ConciergeOnWorkerProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Waiting = "Waiting for a worker: the Concierge starts on a worker, and none is connected.";

    [Fact]
    public async Task A_persons_concierge_runs_on_the_worker_through_controls_relay()
    {
        await using var bed = new ProcessBed();
        try
        {
            bed.AddFakes();
            await bed.StartControlAsync();
            await bed.UseFakeConciergeAsync();
            var w1 = bed.StartFakeWorker("w1");
            await bed.UntilWorkerAsync("w1", WorkersView.Connected);

            await using var browser = await bed.OpenConciergeAsync(100, 30);
            browser.Read();
            await bed.UntilAsync("READY crossed control's relay", () => Task.FromResult(browser.Seen.Contains("READY http", StringComparison.Ordinal)));

            // The CLI is the worker's child, not control's.
            var terminal = bed.TerminalProcess()!.Value;
            Assert.Contains(w1.Pid, terminal.Chain);
            Assert.DoesNotContain(bed.Control.Pid, terminal.Chain);

            await browser.TypeAsync("hello\r");
            await bed.UntilAsync("the keystrokes reached the CLI", () => Task.FromResult(browser.Seen.Contains("GOT:hello", StringComparison.Ordinal)));

            await browser.TypeAsync("size\r");
            await bed.UntilAsync("the CLI was spawned at the browser's size", () => Task.FromResult(browser.Seen.Contains("SIZE:30 100", StringComparison.Ordinal)));

            await browser.ResizeAsync(90, 20);
            await bed.UntilAsync("the resize reached the CLI", async () =>
            {
                await browser.TypeAsync("size\r");
                await Task.Delay(200, Ct);
                return browser.Seen.Contains("SIZE:20 90", StringComparison.Ordinal);
            });
        }
        finally
        {
            bed.StopTerminals();
        }
    }

    [Fact]
    public async Task A_concierge_terminals_memory_is_shown_under_its_worker_and_goes_when_it_ends()
    {
        await using var bed = new ProcessBed();
        try
        {
            bed.AddFakes();
            await bed.StartControlAsync();
            await bed.UseFakeConciergeAsync();
            bed.StartFakeWorker("w1");
            await bed.UntilWorkerAsync("w1", WorkersView.Connected);

            await using var browser = await bed.OpenConciergeAsync();
            browser.Read();
            await bed.UntilAsync("the Concierge is ready", () => Task.FromResult(browser.Seen.Contains("READY", StringComparison.Ordinal)));

            // Measured by the worker from the terminal's own process group, and listed under it.
            await bed.UntilAsync("the terminal's memory is under w1", async () =>
                await TerminalsOnAsync(bed, "w1") is [var hold]
                    && hold.TryGetProperty("residentBytes", out var bytes) && bytes.ValueKind == System.Text.Json.JsonValueKind.Number
                    && bytes.GetInt64() > 0
                    && hold.GetProperty("processes").GetInt32() >= 1);

            (await bed.Person.DeleteAsync("/api/concierge", Ct)).EnsureSuccessStatusCode();

            await bed.UntilAsync("the terminal left w1", async () => await TerminalsOnAsync(bed, "w1") is []);
        }
        finally
        {
            bed.StopTerminals();
        }
    }

    private static async Task<System.Text.Json.JsonElement[]> TerminalsOnAsync(ProcessBed bed, string worker)
    {
        var workers = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<System.Text.Json.JsonElement>(bed.Person, "/api/workers", Ct);
        var entry = workers.EnumerateArray().Single(w => w.GetProperty("id").GetString() == worker);
        return [.. entry.GetProperty("terminals").EnumerateArray()];
    }

    [Fact]
    public async Task Ending_the_concierge_releases_its_lease()
    {
        await using var bed = new ProcessBed();
        try
        {
            bed.AddFakes();
            await bed.StartControlAsync();
            await bed.UseFakeConciergeAsync();
            bed.StartFakeWorker("w1");
            await bed.UntilWorkerAsync("w1", WorkersView.Connected);

            await using var browser = await bed.OpenConciergeAsync();
            browser.Read();
            await bed.UntilAsync("the Concierge is ready", () => Task.FromResult(browser.Seen.Contains("READY", StringComparison.Ordinal)));
            var cli = bed.TerminalProcess()!.Value.Pid;

            await browser.TypeAsync("lease\r");
            await bed.UntilAsync("the Concierge holds heavy", () => Task.FromResult(browser.Seen.Contains("LEASE:{\"outcome\":\"granted\"", StringComparison.Ordinal)));

            // One holder at a time (the default): a member that asks now is queued behind the Concierge.
            var team = await bed.TeamAsync("Leases", "HeavyBlock");
            (await bed.TellAsync(team, "HeavyBlock", "Take heavy.")).EnsureSuccessStatusCode();
            await bed.UntilAsync("HeavyBlock asked for heavy", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "HeavyBlock" && r.Lease is not null)));
            Assert.Contains("\"outcome\":\"queued\"", bed.FakeRuns().Single(r => r.Member == "HeavyBlock").Lease, StringComparison.Ordinal);
            await bed.UntilAsync("the sample shows the Concierge holding and HeavyBlock queued", async () =>
            {
                var (holding, queued) = await bed.HeavyAsync();
                return holding.Count == 1 && holding[0] != "HeavyBlock" && queued.Contains("HeavyBlock");
            });

            (await bed.Person.DeleteAsync("/api/concierge", Ct)).EnsureSuccessStatusCode();

            await bed.UntilAsync("HeavyBlock was handed the lease", async () =>
            {
                var (holding, queued) = await bed.HeavyAsync();
                return holding.SequenceEqual(["HeavyBlock"]) && queued.Count == 0;
            });
            await bed.UntilAsync("the Concierge's CLI is gone", () => Task.FromResult(!ProcessBed.Alive(cli)));
            Assert.Equal(0, await bed.ConciergePrincipalsAsync());

            bed.Go("HeavyBlock");
        }
        finally
        {
            bed.StopTerminals();
        }
    }

    [Fact]
    public async Task A_concierge_whose_cli_exits_releases_its_lease()
    {
        await using var bed = new ProcessBed();
        try
        {
            bed.AddFakes();
            await bed.StartControlAsync();
            await bed.UseFakeConciergeAsync();
            bed.StartFakeWorker("w1");
            await bed.UntilWorkerAsync("w1", WorkersView.Connected);

            await using var browser = await bed.OpenConciergeAsync();
            browser.Read();
            await bed.UntilAsync("the Concierge is ready", () => Task.FromResult(browser.Seen.Contains("READY", StringComparison.Ordinal)));
            var cli = bed.TerminalProcess()!.Value.Pid;

            await browser.TypeAsync("lease\r");
            await bed.UntilAsync("the Concierge holds heavy", async () => (await bed.HeavyAsync()).Holding.Count == 1);

            await browser.TypeAsync("bye\r");

            await bed.UntilAsync("the browser was told the CLI exited", () => Task.FromResult(browser.CloseReason == "exit 0"));
            await bed.UntilAsync("nobody holds heavy", async () => (await bed.HeavyAsync()).Holding.Count == 0);
            await bed.UntilAsync("the Concierge's CLI is gone", () => Task.FromResult(!ProcessBed.Alive(cli)));
            Assert.Equal(0, await bed.ConciergePrincipalsAsync());
        }
        finally
        {
            bed.StopTerminals();
        }
    }

    [Fact]
    public async Task With_no_worker_the_concierge_waits_for_one_and_starts_when_it_connects()
    {
        await using var bed = new ProcessBed();
        try
        {
            bed.AddFakes();
            await bed.StartControlAsync();
            await bed.UseFakeConciergeAsync();

            await using var browser = await bed.OpenConciergeAsync();
            Assert.Equal(Waiting + "\r\n", await browser.FirstFrameAsync());
            browser.Read();

            // Nothing is minted while it waits: the store holds no Concierge credential.
            await Task.Delay(500, Ct);
            Assert.Equal(0, await bed.ConciergePrincipalsAsync());

            var w1 = bed.StartFakeWorker("w1");
            await bed.UntilAsync("READY arrived on the same socket", () => Task.FromResult(browser.Seen.Contains("READY http", StringComparison.Ordinal)));
            Assert.Contains(w1.Pid, bed.TerminalProcess()!.Value.Chain);

            // The same read finds the credential now that the session has started.
            Assert.Equal(1, await bed.ConciergePrincipalsAsync());
        }
        finally
        {
            bed.StopTerminals();
        }
    }

    [Fact]
    public async Task Keystrokes_typed_while_the_worker_is_dropped_are_not_typed_when_it_returns()
    {
        await using var bed = new ProcessBed();
        try
        {
            bed.AddFakes();

            // Dropped after three missed one-second pings; its grace far longer than the stop.
            await bed.StartControlAsync(graceSeconds: 60, keepAliveSeconds: 1);
            await bed.UseFakeConciergeAsync();
            var w1 = bed.StartFakeWorker("w1");
            await bed.UntilWorkerAsync("w1", WorkersView.Connected);

            await using var browser = await bed.OpenConciergeAsync();
            browser.Read();
            await bed.UntilAsync("the Concierge is ready", () => Task.FromResult(browser.Seen.Contains("READY", StringComparison.Ordinal)));

            ProcessBed.Signal(w1, "STOP");
            try
            {
                // Typed only once control has seen the drop: bytes written while the worker is merely
                // stopped would sit in the socket and arrive on SIGCONT, which is not what is ruled on.
                await bed.UntilWorkerAsync("w1", WorkersView.Dropped);
                await browser.TypeAsync("during\r");
                await Task.Delay(500, Ct);
            }
            finally
            {
                ProcessBed.Signal(w1, "CONT");
            }

            await bed.UntilWorkerAsync("w1", WorkersView.Connected);
            await browser.TypeAsync("after\r");
            await bed.UntilAsync("what was typed after the worker returned reached the CLI", () =>
                Task.FromResult(browser.Seen.Contains("GOT:after", StringComparison.Ordinal)));

            Assert.DoesNotContain("GOT:during", browser.Seen, StringComparison.Ordinal);
        }
        finally
        {
            bed.StopTerminals();
        }
    }

    [Fact]
    public async Task A_worker_lost_under_a_concierge_ends_it_with_a_sentence()
    {
        await using var bed = new ProcessBed();
        try
        {
            bed.AddFakes();
            await bed.StartControlAsync(graceSeconds: 2);
            await bed.UseFakeConciergeAsync();
            var w1 = bed.StartFakeWorker("w1");
            await bed.UntilWorkerAsync("w1", WorkersView.Connected);

            await using var browser = await bed.OpenConciergeAsync();
            browser.Read();
            await bed.UntilAsync("the Concierge is ready", () => Task.FromResult(browser.Seen.Contains("READY", StringComparison.Ordinal)));
            await browser.TypeAsync("lease\r");
            await bed.UntilAsync("the Concierge holds heavy", async () => (await bed.HeavyAsync()).Holding.Count == 1);

            w1.KillAlone();

            await bed.UntilAsync("the browser was told its worker stopped", () => Task.FromResult(browser.Seen.Contains(
                "The worker running this Concierge (w1) stopped, so this session ended. Open the Concierge again to start a new one.",
                StringComparison.Ordinal)));
            await bed.UntilAsync("the socket closed", () => Task.FromResult(browser.Closed));
            await bed.UntilAsync("nobody holds heavy", async () => (await bed.HeavyAsync()).Holding.Count == 0);
            Assert.Equal(0, await bed.ConciergePrincipalsAsync());
        }
        finally
        {
            bed.StopTerminals();
        }
    }

    /// <summary>Root only: a worker that runs agents as another user runs the Concierge as that user, and the CLI reads its MCP config.</summary>
    [Fact]
    public async Task The_concierge_on_a_worker_runs_as_the_agent()
    {
        var runAs = AgentLaunchUser.Resolve("nobody");
        if (!runAs.Switches) Assert.Skip($"This process cannot switch users: {runAs.Reason}.");

        await using var bed = new ProcessBed();
        try
        {
            bed.AddFakes();
            foreach (var path in new[] { bed.Work, bed.Root, bed.Out, ConciergeBed.Home(bed) })
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }

            File.SetUnixFileMode(Path.Combine(bed.Work, "terminal.sh"), (UnixFileMode)0b111_101_101);
            await bed.StartControlAsync();
            await bed.UseFakeConciergeAsync();
            bed.StartFakeWorker("w1", new Dictionary<string, string> { ["HARNESS_AGENT_USER"] = "nobody" });
            await bed.UntilWorkerAsync("w1", WorkersView.Connected);

            await using var browser = await bed.OpenConciergeAsync();
            browser.Read();
            await bed.UntilAsync("the Concierge is ready", () => Task.FromResult(browser.Seen.Contains("READY", StringComparison.Ordinal)));

            var pid = bed.TerminalProcess()!.Value.Pid;
            var uid = File.ReadLines($"/proc/{pid}/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal)).Split('\t')[1];
            Assert.Equal(runAs.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture), uid);
        }
        finally
        {
            bed.StopTerminals();
        }
    }
}
