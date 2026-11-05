using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Microsoft.Extensions.Configuration;

namespace Harness.Tests.Host;

/// <summary>
/// A container without the cpu controller has no cpu.max, so its CPUs cannot be read from its
/// cgroup. The CPU bound then comes from a CPU count measured another way - the engine's figure the
/// CLI hands in, else the processors the Host (or worker) sees - and its reason names which. 3 is the
/// bound only when no CPU count was measured at all. Measured live: rootless WSL Podman 6.1.4,
/// cgroupControllers=[], 14 CPUs; the limit fell from 13 to 3.
/// </summary>
public sealed class RunLimitCpuSourceTests
{
    private static TenantSettings Settings(int cpuCount, string? engineCpus = null, int? processors = null)
    {
        var values = new Dictionary<string, string?>();
        if (engineCpus is not null) values[CpuCount.EngineVariable] = engineCpus;
        return new TenantSettings(
            new SqliteTenantSettingsStore(Path.Combine(Path.GetTempPath(), $"harness-unused-{Guid.NewGuid():N}.db")),
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            cpuCount, 0, processors);
    }

    [Fact]
    public void With_no_cpu_controller_the_cpu_bound_comes_from_the_engines_cpus()
    {
        var limit = Settings(cpuCount: 0, engineCpus: "14", processors: 8).RunLimit();

        Assert.Equal(13, limit.Limit);
        Assert.Equal(14, limit.Cpus);
        Assert.Equal(CpuCount.Engine, limit.CpusSource);
        Assert.Equal(
            "CPU bound 13 = 14 CPUs reported by the engine - 1, as the container's cgroup does not say its CPUs; the container has no memory limit",
            limit.Reason);
    }

    [Fact]
    public void With_no_cpu_controller_and_no_engine_figure_the_cpu_bound_comes_from_the_processors_the_Host_sees()
    {
        var limit = Settings(cpuCount: 0, processors: 14).RunLimit();

        Assert.Equal(13, limit.Limit);
        Assert.Equal(CpuCount.Processors, limit.CpusSource);
        Assert.Equal(
            "CPU bound 13 = 14 processors the Host sees - 1, as the container's cgroup does not say its CPUs; the container has no memory limit",
            limit.Reason);
    }

    [Fact]
    public void The_cgroups_cpus_win_over_the_engines_figure_and_read_as_before()
    {
        var limit = Settings(cpuCount: 4, engineCpus: "14", processors: 14).RunLimit();

        Assert.Equal(3, limit.Limit);
        Assert.Equal(CpuCount.Cgroup, limit.CpusSource);
        Assert.Equal("CPU bound 3 = 4 CPUs - 1; the container has no memory limit", limit.Reason);
    }

    [Fact]
    public void Three_is_the_bound_only_when_no_cpu_count_was_measured()
    {
        var limit = Settings(cpuCount: 0, engineCpus: "not a number").RunLimit();

        Assert.Equal(TenantSettings.UnknownCpuBound, limit.Limit);
        Assert.Null(limit.Cpus);
        Assert.Null(limit.CpusSource);
        Assert.Equal(
            "CPU bound 3, as no CPU count could be measured: not the container's cgroup, the engine's figure or the processors the Host sees; the container has no memory limit",
            limit.Reason);
    }

    [Fact]
    public void A_worker_without_the_cpu_controller_is_bounded_by_the_cpus_it_measured_and_says_where_from()
    {
        var settings = Settings(cpuCount: 2);
        settings.Workers = () => [new WorkerBoundSource(new WorkerId("worker-1"), 14, null, CpuCount.Engine)];

        var limit = settings.RunLimit();
        Assert.Equal(13, limit.Limit);
        Assert.Equal(CpuCount.Engine, limit.CpusSource);
        // Word for word what cli/internal/doctor/testdata/host-doctor-no-cpu-controller.json carries.
        Assert.Equal(
            "sum of 1 worker's bound: worker-1 13 (CPU bound 13 = 14 CPUs reported by the engine - 1, as the container's cgroup does not say its CPUs; the container has no memory limit)",
            limit.Reason);

        settings.Workers = () => [new WorkerBoundSource(new WorkerId("worker-1"), 14, null, CpuCount.Processors)];
        Assert.Contains("CPU bound 13 = 14 processors the worker sees - 1", settings.RunLimit().Reason);

        // A worker that measured nothing is the one case left for 3.
        settings.Workers = () => [new WorkerBoundSource(new WorkerId("worker-1"), null, null)];
        Assert.Equal(3, settings.RunLimit().Limit);
        // Word for word what cli/internal/doctor/testdata/host-doctor-no-cpu-count.json carries.
        Assert.Equal(
            "sum of 1 worker's bound: worker-1 3 (CPU bound 3, as no CPU count could be measured: not the container's cgroup, the engine's figure or the processors the worker sees; the container has no memory limit)",
            settings.RunLimit().Reason);
    }

    [Fact]
    public void A_worker_that_does_not_say_where_its_cpus_came_from_reads_as_its_cgroups()
    {
        var bound = WorkerBounds.Of(new WorkerInfo(new WorkerId("worker-1"), "1.0", 4, null, DateTimeOffset.UnixEpoch));

        Assert.Null(bound.CpusSource);
        Assert.Equal("CPU bound 3 = 4 CPUs - 1; the container has no memory limit",
            TenantSettings.Bounds(bound.Cpus, bound.MemoryLimitMb, 2048, bound.CpusSource, "the worker").Reason);
    }

    [Fact]
    public void A_workers_hello_carries_where_its_cpus_came_from_to_its_bound()
    {
        var bound = WorkerBounds.Of(new WorkerInfo(new WorkerId("worker-1"), "1.0", 14, null, DateTimeOffset.UnixEpoch, CpuCount.Engine));

        Assert.Equal(new WorkerBoundSource(new WorkerId("worker-1"), 14, null, CpuCount.Engine), bound);
    }

    [Theory]
    [InlineData(4.0, "14", 8, 4.0, CpuCount.Cgroup)]
    [InlineData(null, "14", 8, 14.0, CpuCount.Engine)]
    [InlineData(null, " 14 ", 8, 14.0, CpuCount.Engine)]
    [InlineData(null, null, 8, 8.0, CpuCount.Processors)]
    [InlineData(null, "0", 8, 8.0, CpuCount.Processors)]
    [InlineData(null, "-2", 8, 8.0, CpuCount.Processors)]
    [InlineData(0.0, "", 8, 8.0, CpuCount.Processors)]
    [InlineData(null, null, null, null, null)]
    [InlineData(null, "x", 0, null, null)]
    public void The_cpu_count_is_the_cgroups_then_the_engines_then_the_processors_seen(
        double? cgroup, string? engine, int? processors, double? cpus, string? source)
    {
        Assert.Equal((cpus, source), CpuCount.Measure(cgroup, engine, processors));
    }
}
