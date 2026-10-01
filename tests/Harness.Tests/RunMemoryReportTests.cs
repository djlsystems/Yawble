using System.Net.Http.Json;
using System.Text.Json;
using Harness.Host;
using Harness.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE PER-RUN MEMORY <c>GET /api/wip</c> STATES IS THE HOST'S OWN: <c>runMemory</c> is read from
/// <see cref="RunMemoryLimits.Report"/>, which reads the limit a launch applies - never a formula of
/// its own. Pinned for cgroup, rlimit with a figure set, and not enforced, against the figure the
/// launch prefix carries and through the route.
/// </summary>
public sealed class RunMemoryReportTests
{
    private static readonly CgroupFacts Writable = new("/sys/fs/cgroup/app", "the test's cgroup is writable");
    private static readonly CgroupFacts NotWritable = new(null, "the test's cgroup is not writable");

    /// <summary>The Host's own settings for an 8192 MB container running four at once.</summary>
    private static TenantSettings EightGigabytesFourRuns(int memoryLimitMb = 0) =>
        new(null!, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Wip:MaxRunning"] = "4",
            ["Runs:MemoryLimitMb"] = memoryLimitMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build(), cpuCount: 8, memoryLimitMb: 8192);

    [Fact]
    public void Under_a_cgroup_the_figure_is_the_hosts_computed_share()
    {
        var settings = EightGigabytesFourRuns();
        Assert.Equal(4, settings.RunLimit().Limit);

        var report = RunMemoryLimits.Decide(Writable, "/usr/bin/prlimit", settings.RunMemoryLimit).Report();

        Assert.Equal(RunMemoryReport.Cgroup, report.Mechanism);
        Assert.Equal((8192 - 1024) / 4, report.PerRunMb);
        Assert.Equal(settings.RunMemoryLimit().Mb, report.PerRunMb);
        Assert.StartsWith(RunMemoryLimits.LogPrefix + ": cgroup", report.Detail);
        Assert.Contains("each run now: 1792 MB", report.Detail);
    }

    [Fact]
    public void Under_rlimit_the_figure_is_the_one_a_person_set_and_the_one_the_prefix_applies()
    {
        var limits = RunMemoryLimits.Decide(NotWritable, "/usr/bin/prlimit", () => new RunMemoryLimit(2048, "runs.memoryLimitMb is set to 2048 MB", Set: true));
        var report = limits.Report();

        Assert.Equal(RunMemoryReport.Rlimit, report.Mechanism);
        Assert.Equal(2048, report.PerRunMb);
        Assert.Contains($"--data={2048L * 1024 * 1024}:", limits.Prefix(limits.Limit(), limits.Ceiling())[1]);
        Assert.StartsWith(RunMemoryLimits.LogPrefix + ": rlimit", report.Detail);
    }

    [Fact]
    public void Not_enforced_reads_none_with_no_figure_and_the_host_says_why()
    {
        // Under rlimit with nothing set, the computed 1792 is never applied, and never reported.
        var settings = EightGigabytesFourRuns();
        var unset = RunMemoryLimits.Decide(NotWritable, "/usr/bin/prlimit", settings.RunMemoryLimit);
        Assert.Empty(unset.Prefix(unset.Limit(), unset.Ceiling()));
        Assert.Equal(new RunMemoryReport(RunMemoryReport.None, null, RunMemoryLimits.NotEnforcedLine), unset.Report());

        // No mechanism at all.
        var none = RunMemoryLimits.Decide(NotWritable, null, settings.RunMemoryLimit).Report();
        Assert.Equal(RunMemoryReport.None, none.Mechanism);
        Assert.Null(none.PerRunMb);
        Assert.Contains("not available", none.Detail);

        // A cgroup with no figure from anywhere applies nothing either.
        var nothing = RunMemoryLimits.Decide(Writable, null, () => new RunMemoryLimit(null, "no container limit to divide")).Report();
        Assert.Equal(RunMemoryReport.None, nothing.Mechanism);
        Assert.Null(nothing.PerRunMb);
        Assert.Contains("no container limit to divide", nothing.Detail);
    }

    [Fact]
    public async Task Api_wip_states_cgroup_rlimit_and_not_enforced_from_the_hosts_own_decision()
    {
        var ct = TestContext.Current.CancellationToken;

        // runs.memoryLimitMb set through the Tenant Settings, as a person sets it.
        var cgroup = await RunMemoryAsync(Writable, set: 3000, ct);
        Assert.Equal("cgroup", cgroup.GetProperty("mechanism").GetString());
        Assert.Equal(3000, cgroup.GetProperty("perRunMb").GetInt32());

        var rlimit = await RunMemoryAsync(NotWritable, set: 3000, ct);
        Assert.Equal("rlimit", rlimit.GetProperty("mechanism").GetString());
        Assert.Equal(3000, rlimit.GetProperty("perRunMb").GetInt32());

        var notEnforced = await RunMemoryAsync(NotWritable, set: null, ct);
        Assert.Equal("none", notEnforced.GetProperty("mechanism").GetString());
        Assert.Equal(JsonValueKind.Null, notEnforced.GetProperty("perRunMb").ValueKind);
        Assert.Equal(RunMemoryLimits.NotEnforcedLine, notEnforced.GetProperty("detail").GetString());
    }

    /// <summary>
    /// <c>runMemory</c> from a Host whose decision is <paramref name="cgroup"/> (with <c>prlimit</c>),
    /// over its own Tenant Settings - the route's answer, and the same as the Host's own report.
    /// </summary>
    private static async Task<JsonElement> RunMemoryAsync(CgroupFacts cgroup, int? set, CancellationToken ct)
    {
        var dataRoot = Directory.CreateTempSubdirectory("harness-run-memory-report-").FullName;
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
                .UseSetting("DataRoot", dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .ConfigureTestServices(services => services.AddSingleton(sp =>
                    RunMemoryLimits.Decide(cgroup, "/usr/bin/prlimit", sp.GetRequiredService<TenantSettings>().RunMemoryLimit))));

            await factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", "correct horse battery");
            using var client = factory.CreateClient();
            (await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = "correct horse battery" }, ct))
                .EnsureSuccessStatusCode();

            if (set is { } mb)
            {
                (await client.PutAsJsonAsync("/api/tenant/settings",
                    new Dictionary<string, object> { [TenantSettings.RunsMemoryLimitMbName] = mb }, ct)).EnsureSuccessStatusCode();
            }

            using var view = JsonDocument.Parse(await client.GetStringAsync("/api/wip", ct));
            var runMemory = view.RootElement.GetProperty("runMemory").Clone();

            var own = factory.Services.GetRequiredService<RunMemoryLimits>().Report();
            Assert.Equal(own.Mechanism, runMemory.GetProperty("mechanism").GetString());
            Assert.Equal(own.Detail, runMemory.GetProperty("detail").GetString());
            Assert.True(view.RootElement.TryGetProperty("limit", out _));
            return runMemory;
        }
        finally
        {
            try { Directory.Delete(dataRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
