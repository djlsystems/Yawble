using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Capacity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace Harness.Tests;

/// <summary>
/// With workers that connect and go, admission stays in control: a run goes to the connected worker
/// with the most measured memory headroom whose own gate has room, no worker takes more than its own
/// bound under the default limit, and with no worker connected a run waits "waiting for a worker".
/// </summary>
public sealed class WorkerPlacementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);

    private const long Gb = 1_000_000_000;

    [Fact]
    public void A_run_goes_to_the_worker_with_the_most_measured_headroom()
    {
        var bed = new Bed();
        bed.Join("w1", limit: 10 * Gb, inUse: 6 * Gb);
        bed.Join("w2", limit: 10 * Gb, inUse: 2 * Gb);

        using var slot = bed.Wip.TryEnter(Member("Developer"));

        Assert.NotNull(slot);
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(Member("Developer")));
    }

    [Fact]
    public void A_worker_over_its_memory_threshold_is_passed_over_while_another_has_room()
    {
        var bed = new Bed();

        // w1 has more bytes free, but is over its 80% threshold; w2 has room.
        bed.Join("w1", limit: 100 * Gb, inUse: 95 * Gb);
        bed.Join("w2", limit: 4 * Gb, inUse: 1 * Gb);
        Assert.Equal([new WorkerId("w1"), new WorkerId("w2")], bed.Pool.Workers);

        using var slot = bed.Wip.TryEnter(Member("Developer"));

        Assert.NotNull(slot);
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(Member("Developer")));
    }

    [Fact]
    public void A_run_waits_with_the_first_workers_reason_only_when_no_worker_has_room()
    {
        var bed = new Bed();
        bed.Join("w1", limit: 10 * Gb, inUse: 9 * Gb);
        bed.Join("w2", limit: 10 * Gb, inUse: 9.5 * Gb);

        Assert.Null(bed.Wip.TryEnter(Member("Developer")));
        var hold = Assert.Single(bed.Wip.View().Waiting);
        Assert.Equal("waiting for memory: 9.0 of 10.0 GB in use", hold.Reason);

        // One of them gets room: the waiter is placed there.
        bed.Measure("w2", limit: 10 * Gb, inUse: 1 * Gb);
        bed.Wip.HeadroomChanged();
        using var slot = bed.Wip.TryEnter(Member("Developer"));
        Assert.NotNull(slot);
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(Member("Developer")));
    }

    [Fact]
    public void A_worker_not_yet_measured_holds_nothing_and_ranks_after_measured_workers()
    {
        var bed = new Bed();
        bed.Join("fresh");
        bed.Join("measured", limit: 10 * Gb, inUse: 2 * Gb);

        // Connected first, but with no figures it is tried after a measured worker with room.
        Assert.Equal([new WorkerId("measured"), new WorkerId("fresh")], bed.Pool.Workers);
        Assert.Null(bed.Pool.Gate(new WorkerId("fresh")).Headroom);
        using (var first = bed.Wip.TryEnter(Member("A")))
        {
            Assert.Equal(new WorkerId("measured"), bed.Wip.PlacedOn(Member("A")));
        }

        // The measured one fills up: the unmeasured one holds nothing back and takes the run.
        bed.Measure("measured", limit: 10 * Gb, inUse: 9 * Gb);
        using var second = bed.Wip.TryEnter(Member("B"));
        Assert.NotNull(second);
        Assert.Equal(new WorkerId("fresh"), bed.Wip.PlacedOn(Member("B")));
    }

    [Fact]
    public void Under_the_default_limit_a_worker_takes_no_more_runs_than_its_own_bound()
    {
        var bounds = new Dictionary<string, int> { ["w1"] = 1, ["w2"] = 2 };
        var bed = new Bed(max: 3, bound: info => bounds[info.Id.Value]);
        bed.Join("w1", limit: 10 * Gb, inUse: 1 * Gb);
        bed.Join("w2", limit: 10 * Gb, inUse: 3 * Gb);

        using var a = bed.Wip.TryEnter(Member("A"));
        using var b = bed.Wip.TryEnter(Member("B"));
        using var c = bed.Wip.TryEnter(Member("C"));

        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(Member("A")));
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(Member("B")));
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(Member("C")));
        Assert.Equal(1, bed.Wip.PlacedCount(new WorkerId("w1")));
        Assert.Equal(2, bed.Wip.PlacedCount(new WorkerId("w2")));
        Assert.Null(bed.Wip.TryEnter(Member("D")));
        Assert.Equal(WipLedger.SlotReason, Assert.Single(bed.Wip.View().Waiting).Reason);
    }

    [Fact]
    public void A_set_run_limit_is_the_instance_total_with_no_per_worker_cap()
    {
        var bed = new Bed(max: 3, bound: _ => null);
        bed.Join("w1", limit: 10 * Gb, inUse: 1 * Gb);
        bed.Join("w2", limit: 10 * Gb, inUse: 3 * Gb);

        using var a = bed.Wip.TryEnter(Member("A"));
        using var b = bed.Wip.TryEnter(Member("B"));
        using var c = bed.Wip.TryEnter(Member("C"));

        Assert.Equal(3, bed.Wip.PlacedCount(new WorkerId("w1")));
        Assert.Null(bed.Pool.Bound(new WorkerId("w1")));
    }

    [Fact]
    public void A_manager_skips_the_headroom_gate_but_needs_a_connected_worker()
    {
        var bed = new Bed();
        var manager = Member(WipLedger.ManagerName);

        Assert.Null(bed.Wip.TryEnter(manager));
        Assert.Equal(WipLedger.WorkerReason, Assert.Single(bed.Wip.View().Waiting).Reason);

        bed.Join("w1", limit: 10 * Gb, inUse: 9.5 * Gb);
        bed.Wip.WorkersChanged();

        using var slot = bed.Wip.TryEnter(manager);
        Assert.NotNull(slot);
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(manager));
    }

    [Fact]
    public void With_no_worker_connected_an_admitted_run_waits_for_a_worker()
    {
        var bed = new Bed();

        Assert.Null(bed.Wip.TryEnter(Member("Developer")));
        Assert.Null(bed.Wip.TryEnter(Member("Tester")));

        var view = bed.Wip.View();
        Assert.Empty(view.Running);
        Assert.Equal(["waiting for a worker", "waiting for a worker"], view.Waiting.Select(h => h.Reason));
        Assert.Equal(["Developer", "Tester"], view.Waiting.Select(h => h.Member));
    }

    [Fact]
    public async Task A_run_waiting_for_a_worker_starts_when_one_connects()
    {
        var bed = new Bed();
        Assert.Null(bed.Wip.TryEnter(Member("Developer")));
        var changed = bed.Wip.Changed;
        Assert.False(changed.IsCompleted);

        bed.Join("w1");
        bed.Wip.WorkersChanged();
        await changed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var slot = bed.Wip.TryEnter(Member("Developer"));
        Assert.NotNull(slot);
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(Member("Developer")));
        Assert.Empty(bed.Wip.View().Waiting);
    }

    [Fact]
    public void A_dropped_worker_is_never_chosen_until_it_is_back()
    {
        var bed = new Bed();
        bed.Join("w1", limit: 10 * Gb, inUse: 1 * Gb);
        bed.Pool.Drop(new WorkerId("w1"));

        Assert.Empty(bed.Pool.Workers);
        Assert.Null(bed.Wip.TryEnter(Member("Developer")));
        Assert.Equal(WipLedger.WorkerReason, Assert.Single(bed.Wip.View().Waiting).Reason);

        bed.Pool.Back(new WorkerId("w1"));
        using var slot = bed.Wip.TryEnter(Member("Developer"));
        Assert.NotNull(slot);
    }

    [Fact]
    public void Ties_go_to_the_worker_with_fewer_runs_placed()
    {
        var bed = new Bed();
        bed.Join("w1");
        bed.Join("w2");

        using var a = bed.Wip.TryEnter(Member("A"));
        using var b = bed.Wip.TryEnter(Member("B"));

        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(Member("A")));
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(Member("B")));
    }

    [Fact]
    public async Task A_tell_is_accepted_while_no_worker_is_connected()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("harness-tell-no-worker-").FullName;
        try
        {
            await using var control = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
                .UseSetting("DataRoot", root).UseSetting("Role", "control").UseSetting("Logging:LogLevel:Default", "Warning"));
            var services = control.Services;
            var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
            var team = (await services.GetRequiredService<TeamRegistry>().CreateAsync("Alpha", agent, memberAgent: agent, ct: ct)).Id;
            await services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", Host.HostFixture.Password, ct);
            using var person = control.CreateClient();
            (await person.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = Host.HostFixture.Password }, ct))
                .EnsureSuccessStatusCode();

            // The tell is the log's: it is taken at once. The hold is at the wake, waiting for a worker.
            var told = await person.PostAsJsonAsync($"/api/teams/{team}/containers/{WipLedger.ManagerName}/tell", new { instruction = "Plan the work." }, ct);
            Assert.True(told.IsSuccessStatusCode, $"{(int)told.StatusCode} {await told.Content.ReadAsStringAsync(ct)}");

            var wip = services.GetRequiredService<WipLedger>();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!wip.View().Waiting.Any(h => h.Member == WipLedger.ManagerName) && DateTime.UtcNow < deadline) await Task.Delay(50, ct);

            var hold = Assert.Single(wip.View().Waiting, h => h.Member == WipLedger.ManagerName);
            Assert.Equal(WipLedger.WorkerReason, hold.Reason);
            Assert.Empty(wip.View().Running);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static ContainerId Member(string name) => new("Alpha", name);

    private sealed class Bed
    {
        private readonly ManualTime _clock = new(Now);

        public Bed(int max = 5, Func<WorkerInfo, int?>? bound = null)
        {
            Pool = new WorkerPool(_ => new HeadroomGate(() => 80, () => 0, _clock), bound, _clock);
            Wip = new WipLedger(max, Pool);
            Pool.Placed = Wip.PlacedCount;
        }

        public WorkerPool Pool { get; }

        public WipLedger Wip { get; }

        public void Join(string id, double? limit = null, double? inUse = null)
        {
            var worker = new Idle(new WorkerId(id));
            Pool.Join(worker, new WorkerInfo(worker.Id, "test", 4, (long?)limit, Now));
            if (limit is not null) Measure(id, limit.Value, inUse ?? 0);
        }

        public void Measure(string id, double limit, double inUse) =>
            Pool.Gate(new WorkerId(id)).Update(
                WorkerCapacity.NotMeasured with
                {
                    MemoryLimitBytes = (long)limit, MemoryAnonBytes = (long)inUse, MemoryShmemBytes = 0, MemoryCurrentBytes = (long)inUse,
                    NotMeasured = [],
                },
                _clock.GetUtcNow());
    }

    private sealed class Idle(WorkerId id) : IRunWorker
    {
        public WorkerId Id => id;

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public Task SendAsync(ControlMessage message, CancellationToken ct = default) => Task.CompletedTask;
    }
}
