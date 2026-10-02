using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Microsoft.Extensions.Configuration;

namespace Harness.Tests;

/// <summary>
/// When runs go to workers that connect, the default run limit is the sum of each connected worker's
/// own bound, said worker by worker; a run's memory share on its worker follows that worker's bound;
/// and with the Host's own worker every figure and sentence is what it was.
/// </summary>
public sealed class WorkerRunLimitTests
{
    private static readonly WorkerBoundSource Four = new(new WorkerId("w1"), 4, 8192);
    private static readonly WorkerBoundSource Small = new(new WorkerId("w2"), 8, 4096);

    [Fact]
    public void The_default_run_limit_is_the_sum_of_each_connected_workers_bound()
    {
        var settings = Settings(cpus: 2, memoryMb: 2048);
        settings.Workers = () => [Four, Small];

        // w1: CPU bound 3 = 4 - 1, memory bound 4 = 8192 / 2048: 3. w2: CPU bound 7, memory bound 2: 2.
        var limit = settings.RunLimit();
        Assert.Equal(5, limit.Limit);
        Assert.Equal("workers", limit.Bound);
        Assert.Equal("5", settings.Fallback(TenantSettings.WipMaxRunningName));
        Assert.Equal(3, settings.WorkerBound(Four));
        Assert.Equal(2, settings.WorkerBound(Small));
    }

    [Fact]
    public void Wip_names_the_bound_and_each_worker_in_its_reason()
    {
        var settings = Settings(cpus: 2, memoryMb: 2048);
        settings.Workers = () => [Four, Small];

        Assert.Equal(
            "sum of 2 workers' bounds: "
            + "w1 3 (CPU bound 3 = 4 CPUs - 1, not above the memory bound 4 = 8192 MB / 2048 MB per run: the CPU bound applies), "
            + "w2 2 (memory bound 2 = 4096 MB / 2048 MB per run, below the CPU bound 7 = 8 CPUs - 1: the memory bound applies)",
            settings.RunLimit().Reason);
    }

    [Fact]
    public void With_one_worker_the_limit_and_its_reason_are_todays()
    {
        var own = Settings(cpus: 4, memoryMb: 8192);
        var before = own.RunLimit();

        // This Host runs its runs itself: its own container is the one bound, word for word.
        Assert.Null(own.Workers);
        Assert.Equal(TenantSettings.Bounds(4, 8192, TenantSettings.DefaultMemoryPerRunMb), before);
        Assert.Equal("cpu", before.Bound);
        Assert.Equal(3, before.Limit);
        Assert.Equal(
            "CPU bound 3 = 4 CPUs - 1, not above the memory bound 4 = 8192 MB / 2048 MB per run: the CPU bound applies",
            before.Reason);

        // And one connected worker of the same size bounds runs to the same figure.
        var one = Settings(cpus: 1, memoryMb: 1024);
        one.Workers = () => [Four];
        Assert.Equal(before.Limit, one.RunLimit().Limit);
        Assert.Equal(own.RunMemoryLimit(), one.RunMemoryLimit(Four));
    }

    [Fact]
    public void With_no_worker_connected_the_reason_says_so()
    {
        var settings = Settings(cpus: 4, memoryMb: 8192);
        settings.Workers = () => [];

        var limit = settings.RunLimit();
        Assert.Equal(0, limit.Limit);
        Assert.Equal("workers", limit.Bound);
        Assert.Equal(
            "no worker is connected, so no run can start; the default is the sum of each connected worker's own bound",
            limit.Reason);
    }

    [Fact]
    public void A_set_run_limit_leaves_no_worker_a_cap_of_its_own()
    {
        var settings = new TenantSettings(
            new SqliteTenantSettingsStore(Path.Combine(Path.GetTempPath(), $"harness-unused-{Guid.NewGuid():N}.db")),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Wip:MaxRunning"] = "9" }).Build(),
            2, 2048);
        settings.Workers = () => [Four, Small];

        Assert.Equal(9, settings.RunLimit().Limit);
        Assert.Null(settings.WorkerBound(Four));
    }

    [Fact]
    public void A_runs_memory_share_on_its_worker_follows_the_workers_bound()
    {
        var settings = Settings(cpus: 2, memoryMb: 2048);
        settings.Workers = () => [Four, Small];

        // Five runs; w1's bound is 3 of 5, so it holds ceil(5 * 3/5) = 3: (8192 - 1024) / 3.
        var onFour = settings.RunMemoryLimit(Four);
        Assert.Equal((8192 - 1024) / 3, onFour.Mb);
        Assert.Contains("8192 MB container limit - 1024 MB for the Host) / 3 (worker w1's share of wip.maxRunning 5)", onFour.Source);

        // w2's bound is 2 of 5: (4096 - 1024) / 2.
        Assert.Equal((4096 - 1024) / 2, settings.RunMemoryLimit(Small).Mb);
        Assert.Equal(8192 - 1024, settings.RunMemoryCeiling(Four).Mb);
    }

    [Fact]
    public void The_heavy_rule_says_what_it_said_before()
    {
        var settings = Settings(cpus: 8, memoryMb: 12288);

        var heavy = settings.HeavyRunMemoryLimit(2000, 4);
        Assert.Equal(9264, heavy.Mb);
        Assert.Equal(
            "the run holds the heavy lease, so 12288 MB container limit - 1024 MB for the Host - 2000 MB measured in use by 4 other running runs",
            heavy.Source);
        Assert.Equal(heavy, RunMemoryRules.Heavy(settings.RunMemoryLimit(), 12288, TenantSettings.HostReserveMb, 2000, 4));
        Assert.Equal(settings.RunMemoryCeiling(), RunMemoryRules.Ceiling(settings.RunMemoryLimit(), 12288, TenantSettings.HostReserveMb));
    }

    private static TenantSettings Settings(int cpus, long memoryMb) => new(
        new SqliteTenantSettingsStore(Path.Combine(Path.GetTempPath(), $"harness-unused-{Guid.NewGuid():N}.db")),
        new ConfigurationBuilder().Build(), cpus, memoryMb);
}
