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

    [Fact]
    public void Three_managers_told_at_once_on_three_workers_land_on_three_workers()
    {
        var bed = new Bed(max: 6, bound: _ => 2, memoryPerRunMb: 2048);
        bed.Join("w1", limit: 4 * Gb, inUse: 1.2 * Gb);
        bed.Join("w2", limit: 4 * Gb, inUse: 1.1 * Gb);
        bed.Join("w3", limit: 4 * Gb, inUse: 1.0 * Gb);
        Assert.Equal([new WorkerId("w3"), new WorkerId("w2"), new WorkerId("w1")], bed.Pool.Workers);

        // No new sample between them: each placement alone must move the next one on.
        var alpha = new ContainerId("Alpha", WipLedger.ManagerName);
        var beta = new ContainerId("Beta", WipLedger.ManagerName);
        var gamma = new ContainerId("Gamma", WipLedger.ManagerName);
        using var a = bed.Wip.TryEnter(alpha);
        Assert.Equal(new WorkerId("w3"), bed.Wip.PlacedOn(alpha));
        using var b = bed.Wip.TryEnter(beta);
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(beta));
        using var c = bed.Wip.TryEnter(gamma);
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(gamma));

        Assert.All(["w1", "w2", "w3"], w => Assert.Equal(1, bed.Wip.PlacedCount(new WorkerId(w))));
        Assert.All(bed.Wip.View().Running, hold => Assert.Null(hold.Reason));
    }

    [Fact]
    public void A_burst_of_members_spreads_across_workers_before_any_worker_reaches_its_bound()
    {
        var bed = new Bed(max: 18, bound: _ => 6, memoryPerRunMb: 2048);
        bed.Join("w1", limit: 12 * Gb, inUse: 1.0 * Gb);
        bed.Join("w2", limit: 12 * Gb, inUse: 1.1 * Gb);
        bed.Join("w3", limit: 12 * Gb, inUse: 1.2 * Gb);

        string[] expected = ["w1", "w2", "w3", "w1", "w2", "w3"];
        var slots = new List<IDisposable>();
        for (var i = 0; i < expected.Length; i++)
        {
            var member = Member($"M{i}");
            slots.Add(Assert.IsAssignableFrom<IDisposable>(bed.Wip.TryEnter(member)));
            Assert.Equal(new WorkerId(expected[i]), bed.Wip.PlacedOn(member));
        }

        Assert.All(["w1", "w2", "w3"], w => Assert.Equal(2, bed.Wip.PlacedCount(new WorkerId(w))));
        Assert.All(["w1", "w2", "w3"], w => Assert.True(bed.Wip.PlacedCount(new WorkerId(w)) < bed.Pool.Bound(new WorkerId(w))));
        slots.ForEach(slot => slot.Dispose());
    }

    [Fact]
    public void Runs_placed_since_a_workers_last_sample_count_against_it_until_its_next_sample()
    {
        var bed = new Bed(max: 5, bound: _ => null, memoryPerRunMb: 2048);
        bed.Join("w1", limit: 4 * Gb, inUse: 2.0 * Gb);
        bed.Join("w2", limit: 4 * Gb, inUse: 1.9 * Gb);
        bed.Join("w3", limit: 4 * Gb, inUse: 1.0 * Gb);

        // A takes w3 (3.0 GB free); counted at its allowance, w3 is left with about 0.85.
        using var a = bed.Wip.TryEnter(Member("A"));
        Assert.Equal(new WorkerId("w3"), bed.Wip.PlacedOn(Member("A")));
        using var b = bed.Wip.TryEnter(Member("B"));
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(Member("B")));

        // w3's next sample has its runs in its figures: what was counted against it is dropped.
        bed.Measure("w3", limit: 4 * Gb, inUse: 1.5 * Gb);
        using var c = bed.Wip.TryEnter(Member("C"));
        Assert.Equal(new WorkerId("w3"), bed.Wip.PlacedOn(Member("C")));
    }

    [Fact]
    public void Unmeasured_workers_rank_by_runs_placed_against_their_own_bound()
    {
        var bounds = new Dictionary<string, int> { ["w1"] = 6, ["w2"] = 2 };
        var bed = new Bed(max: 10, bound: info => bounds[info.Id.Value]);
        bed.Join("w1");
        bed.Join("w2");
        Assert.Null(bed.Pool.Gate(new WorkerId("w1")).Headroom);
        Assert.Null(bed.Pool.Gate(new WorkerId("w2")).Headroom);

        using var a = bed.Wip.TryEnter(Member("A"));
        using var b = bed.Wip.TryEnter(Member("B"));
        using var c = bed.Wip.TryEnter(Member("C"));
        using var d = bed.Wip.TryEnter(Member("D"));

        // D: w1 holds 2 of 6, w2 1 of 2.
        Assert.Equal(
            ["w1", "w2", "w1", "w1"],
            new[] { "A", "B", "C", "D" }.Select(m => bed.Wip.PlacedOn(Member(m))?.Value));
    }

    [Fact]
    public void A_manager_prefers_a_worker_under_its_bound()
    {
        var bounds = new Dictionary<string, int> { ["w1"] = 1, ["w2"] = 2 };
        var bed = new Bed(max: 5, bound: info => bounds[info.Id.Value]);
        bed.Join("w1", limit: 10 * Gb, inUse: 1 * Gb);
        bed.Join("w2", limit: 10 * Gb, inUse: 3 * Gb);
        var manager = Member(WipLedger.ManagerName);

        using var a = bed.Wip.TryEnter(Member("A"));
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(Member("A")));

        using var slot = bed.Wip.TryEnter(manager);
        Assert.NotNull(slot);
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(manager));
        Assert.Null(Assert.Single(bed.Wip.View().Running, h => h.Member == WipLedger.ManagerName).Reason);
    }

    [Fact]
    public void A_manager_takes_a_worker_under_its_bound_but_over_its_memory_threshold_before_going_over_a_bound()
    {
        var bounds = new Dictionary<string, int> { ["w1"] = 3, ["w2"] = 1 };
        var bed = new Bed(max: 5, bound: info => bounds[info.Id.Value]);
        bed.Join("w1", limit: 10 * Gb, inUse: 2 * Gb);
        bed.Join("w2", limit: 10 * Gb, inUse: 1 * Gb);
        var manager = Member(WipLedger.ManagerName);

        using var a = bed.Wip.TryEnter(Member("A"));
        using var b = bed.Wip.TryEnter(Member("B"));
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(Member("A")));
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(Member("B")));

        // w2 is at its bound; w1 is under its bound but over its memory threshold.
        bed.Measure("w1", limit: 10 * Gb, inUse: 9 * Gb);

        using var slot = bed.Wip.TryEnter(manager);
        Assert.NotNull(slot);
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(manager));
        Assert.Null(Assert.Single(bed.Wip.View().Running, h => h.Member == WipLedger.ManagerName).Reason);
        Assert.Equal(1, bed.Wip.PlacedCount(new WorkerId("w2")));
    }

    [Fact]
    public void A_manager_goes_over_a_bound_only_when_every_worker_is_at_it_and_then_on_the_least_loaded()
    {
        var bed = OverBoundBed();

        // Both workers hold their bound: w1 1 of 1, w2 2 of 2. w1 has fewer placed, though w2 ranks first.
        var alpha = Member(WipLedger.ManagerName);
        using var first = bed.Wip.TryEnter(alpha);
        Assert.NotNull(first);
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(alpha));

        // Now w1 holds 2 of 1, w2 2 of 2.
        var beta = new ContainerId("Beta", WipLedger.ManagerName);
        using var second = bed.Wip.TryEnter(beta);
        Assert.NotNull(second);
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(beta));

        // Members keep the bound, and the queue its order.
        var view = bed.Wip.View();
        var waiting = Assert.Single(view.Waiting);
        Assert.Equal(("Alpha", "D", WipLedger.SlotReason), (waiting.Team, waiting.Member, waiting.Reason));
        Assert.Equal(5, view.Running.Count);
        Assert.Equal(
            new HashSet<(string, string, string?)>
            {
                ("Alpha", "A", null), ("Alpha", "B", null), ("Alpha", "C", null),
                ("Alpha", WipLedger.ManagerName, $"over w1's bound of 1: {WipLedger.OverBoundReason}"),
                ("Beta", WipLedger.ManagerName, $"over w2's bound of 2: {WipLedger.OverBoundReason}"),
            },
            view.Running.Select(h => (h.Team, h.Member, h.Reason)).ToHashSet());
    }

    [Fact]
    public void A_manager_prefers_a_worker_with_room_over_one_held_by_memory()
    {
        var bed = new Bed();
        bed.Join("w1", limit: 100 * Gb, inUse: 95 * Gb);
        bed.Join("w2", limit: 4 * Gb, inUse: 1 * Gb);
        Assert.Equal([new WorkerId("w1"), new WorkerId("w2")], bed.Pool.Workers);
        var manager = Member(WipLedger.ManagerName);

        using var slot = bed.Wip.TryEnter(manager);

        Assert.NotNull(slot);
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(manager));
    }

    [Fact]
    public void The_workers_view_names_each_workers_runs_and_a_manager_over_a_bound_says_why()
    {
        var bed = OverBoundBed();
        using var manager = bed.Wip.TryEnter(Member(WipLedger.ManagerName));

        var workers = WorkersView.Of(bed.Pool, bed.Wip, "test");

        Assert.Equal(["w1", "w2"], workers.Select(w => w.Id));
        Assert.Equal(
            new HashSet<(string, string, string?)>
            {
                ("Alpha", "C", null),
                ("Alpha", WipLedger.ManagerName, $"over w1's bound of 1: {WipLedger.OverBoundReason}"),
            },
            workers[0].Runs.Select(r => (r.Team, r.Member, r.Reason)).ToHashSet());
        Assert.Equal(
            new HashSet<(string, string, string?)> { ("Alpha", "A", null), ("Alpha", "B", null) },
            workers[1].Runs.Select(r => (r.Team, r.Member, r.Reason)).ToHashSet());
    }

    [Fact]
    public void A_measurement_that_frees_a_waiter_counts_nothing_against_a_worker()
    {
        var bed = new Bed(max: 5, memoryPerRunMb: 2048);
        bed.Join("w1", limit: 10 * Gb, inUse: 9 * Gb);
        bed.Join("w2", limit: 10 * Gb, inUse: 9.5 * Gb);

        Assert.Null(bed.Wip.TryEnter(Member("A")));
        Assert.Equal("waiting for memory: 9.0 of 10.0 GB in use", Assert.Single(bed.Wip.View().Waiting).Reason);

        // The new measurement asks where A would go; asking places nothing.
        bed.Measure("w1", limit: 10 * Gb, inUse: 1 * Gb);
        bed.Measure("w2", limit: 10 * Gb, inUse: 3.5 * Gb);
        bed.Wip.HeadroomChanged();

        using var a = bed.Wip.TryEnter(Member("A"));
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(Member("A")));

        // w1: 9.0 GB free less one run's allowance still leads w2's 6.5; less two, it does not.
        using var b = bed.Wip.TryEnter(Member("B"));
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(Member("B")));
        using var c = bed.Wip.TryEnter(Member("C"));
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(Member("C")));
    }

    [Fact]
    public void A_run_that_asks_again_while_running_is_counted_once()
    {
        var bed = new Bed(max: 5, memoryPerRunMb: 2048);
        bed.Join("w1", limit: 10 * Gb, inUse: 1 * Gb);
        bed.Join("w2", limit: 10 * Gb, inUse: 4 * Gb);

        using var a = bed.Wip.TryEnter(Member("A"));
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(Member("A")));
        Assert.NotNull(bed.Wip.TryEnter(Member("A")));
        Assert.NotNull(bed.Wip.TryEnter(Member("A")));
        Assert.Equal(1, bed.Wip.PlacedCount(new WorkerId("w1")));

        // w1: 9.0 GB free less one allowance leads w2's 6.0; less two, it does not.
        using var b = bed.Wip.TryEnter(Member("B"));
        Assert.Equal(new WorkerId("w1"), bed.Wip.PlacedOn(Member("B")));
        using var c = bed.Wip.TryEnter(Member("C"));
        Assert.Equal(new WorkerId("w2"), bed.Wip.PlacedOn(Member("C")));
    }

    [Fact]
    public void A_fixed_pool_keeps_its_order_whatever_is_placed_on_it()
    {
        var clock = new ManualTime(Now);
        var gates = new[] { "w1", "w2" }.Select(id => (Id: new WorkerId(id), Gate: new HeadroomGate(() => 80, () => 0, clock))).ToArray();
        gates[0].Gate.Update(WorkerCapacity.NotMeasured with { MemoryLimitBytes = 10 * Gb, MemoryAnonBytes = 5 * Gb, MemoryShmemBytes = 0, NotMeasured = [] }, Now);
        gates[1].Gate.Update(WorkerCapacity.NotMeasured with { MemoryLimitBytes = 10 * Gb, MemoryAnonBytes = 1 * Gb, MemoryShmemBytes = 0, NotMeasured = [] }, Now);
        var pool = new WorkerPool([.. gates.Select(g => (g.Id, g.Gate))]);
        var wip = new WipLedger(0, pool);
        pool.Placed = wip.PlacedCount;

        var slots = Enumerable.Range(0, 10).Select(i => wip.TryEnter(Member($"M{i}"))!).ToList();

        Assert.Equal([new WorkerId("w1"), new WorkerId("w2")], pool.Workers);
        Assert.Equal(10, wip.PlacedCount(new WorkerId("w1")));
        slots.ForEach(slot => slot.Dispose());
    }

    /// <summary>Two workers each at its own bound - w1 holds C (1 of 1), w2 holds A and B (2 of 2) - and D
    /// waiting for a slot. w2 ranks first on headroom.</summary>
    private static Bed OverBoundBed()
    {
        var bounds = new Dictionary<string, int> { ["w1"] = 1, ["w2"] = 2 };
        var bed = new Bed(max: 5, bound: info => bounds[info.Id.Value]);
        bed.Join("w1", limit: 10 * Gb, inUse: 3 * Gb);
        bed.Join("w2", limit: 10 * Gb, inUse: 1 * Gb);

        foreach (var member in new[] { "A", "B", "C" }) Assert.NotNull(bed.Wip.TryEnter(Member(member)));
        Assert.Equal(["w2", "w2", "w1"], new[] { "A", "B", "C" }.Select(m => bed.Wip.PlacedOn(Member(m))?.Value));
        Assert.Null(bed.Wip.TryEnter(Member("D")));
        Assert.Equal(WipLedger.SlotReason, Assert.Single(bed.Wip.View().Waiting).Reason);
        return bed;
    }

    private static ContainerId Member(string name) => new("Alpha", name);

    private sealed class Bed
    {
        private readonly ManualTime _clock = new(Now);

        public Bed(int max = 5, Func<WorkerInfo, int?>? bound = null, int? memoryPerRunMb = null)
        {
            Pool = new WorkerPool(
                _ => new HeadroomGate(() => 80, () => 0, _clock), bound, _clock,
                memoryPerRunMb is { } mb ? () => mb : null);
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
