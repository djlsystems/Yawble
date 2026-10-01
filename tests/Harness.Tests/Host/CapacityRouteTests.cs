using System.Net;
using System.Text.Json;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// THE ONE CAPACITY ROUTE is a person's: a person reads the sample and its history, a machine
/// principal is refused with the HumansOnly sentence. The test Host measures no cgroup
/// (<see cref="CapacityIsolation"/>), so every container figure reads not measured, never 0.
/// </summary>
public sealed class CapacityRouteTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_person_reads_the_sample_and_its_history_with_not_measured_named()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        // Taken on demand so the read does not wait on the sampler's clock.
        host.Services.GetRequiredService<CapacitySampler>().Sample();

        var response = await client.GetAsync("/api/capacity", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = body.RootElement;
        Assert.Equal(CapacitySampler.DefaultInterval.TotalSeconds, root.GetProperty("intervalSeconds").GetDouble());
        Assert.True(root.GetProperty("history").GetArrayLength() >= 1);

        var latest = root.GetProperty("latest");
        Assert.True(latest.TryGetProperty("at", out _));
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("cgroup").ValueKind);
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("memory").GetProperty("limitBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("memory").GetProperty("inUseBytes").ValueKind);
        Assert.False(latest.GetProperty("memory").GetProperty("unlimited").GetBoolean());
        Assert.Contains(
            "memory.limit", latest.GetProperty("notMeasured").EnumerateArray().Select(name => name.GetString()));

        var runs = latest.GetProperty("runs");
        Assert.True(runs.TryGetProperty("limit", out _));
        Assert.True(runs.TryGetProperty("managerReserved", out _));
        Assert.True(runs.TryGetProperty("waiting", out _));
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("admission").GetProperty("holding").ValueKind);
        Assert.Equal(80, latest.GetProperty("admission").GetProperty("memoryPercent").GetInt32());
        Assert.Equal(10, latest.GetProperty("admission").GetProperty("memoryPressurePercent").GetInt32());
        Assert.Equal(JsonValueKind.Array, latest.GetProperty("topByMemory").ValueKind);
        Assert.Equal(JsonValueKind.Array, latest.GetProperty("topByCpu").ValueKind);
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("heavyLease").ValueKind);
    }

    [Fact]
    public async Task A_machine_principal_is_refused_the_route()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = host.Container(host.AlphaContainerKey);

        var response = await client.GetAsync("/api/capacity", ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(PermitGate.HumansOnlyMessage, body.RootElement.GetProperty("error").GetString());
    }
}
