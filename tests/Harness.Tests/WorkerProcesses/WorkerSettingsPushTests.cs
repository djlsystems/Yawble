using System.Net.Http.Json;

namespace Harness.Tests;

/// <summary>
/// A SETTING CHANGED WHILE WORKERS ARE CONNECTED REACHES THEM AT ONCE: control sends every connected
/// worker the figures it runs by, as its welcome does. A worker not connected at that moment gets them
/// in its next welcome.
/// </summary>
[Collection("worker processes")]
public sealed class WorkerSettingsPushTests
{
    private const string Figure = "Control's figures for runs: 777 MB a run";

    [Fact]
    public async Task A_memory_setting_changed_while_connected_reaches_every_connected_worker()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync();
        var w1 = bed.StartWorker("w1");
        var w2 = bed.StartWorker("w2");
        await bed.UntilAsync("both are connected", async () => (await bed.WorkersAsync()).Count == 2);
        Assert.DoesNotContain(Figure, w1.Text());

        (await bed.Person.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object> { ["runs.memoryLimitMb"] = 777 },
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        await bed.UntilAsync("w1 has the figure", () => Task.FromResult(w1.Text().Contains(Figure, StringComparison.Ordinal)));
        await bed.UntilAsync("w2 has the figure", () => Task.FromResult(w2.Text().Contains(Figure, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_memory_setting_changed_while_connected_reaches_a_dropped_worker_on_its_welcome()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync(graceSeconds: 30);
        (await bed.Person.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object> { ["runs.memoryLimitMb"] = 777 },
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        // Not connected when it changed: its welcome carries it.
        var w1 = bed.StartWorker("w1");
        await bed.UntilAsync("w1 has the figure from its welcome", () => Task.FromResult(w1.Text().Contains(Figure, StringComparison.Ordinal)));
    }
}
