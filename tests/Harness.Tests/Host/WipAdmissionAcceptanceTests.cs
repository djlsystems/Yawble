using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// Admission, through the real routes: the limit lowered from 4 to 2 in a PUT while six members are runnable leaves two
/// running and four waiting in FIFO order, the rollup counts them per team, and the tenant log names
/// the person. No restart: the same host answers before and after.
/// </summary>
public sealed class LimitLoweredAcceptanceTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task Lowering_4_to_2_with_six_runnable_members_runs_two_and_queues_four_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();
        var wip = host.Services.GetRequiredService<WipLedger>();

        var four = await client.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { ["wip.maxRunning"] = 4 }, ct);
        Assert.Equal(HttpStatusCode.OK, four.StatusCode);

        var lowered = await client.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { ["wip.maxRunning"] = 2 }, ct);
        Assert.Equal(HttpStatusCode.OK, lowered.StatusCode);
        Assert.Equal(2, wip.Max);

        // Six runnable members across both teams, in this order.
        var members = new[]
        {
            new ContainerId(host.Alpha, "DevA1"), new ContainerId(host.Beta, "DevB1"),
            new ContainerId(host.Alpha, "DevA2"), new ContainerId(host.Beta, "DevB2"),
            new ContainerId(host.Alpha, "DevA3"), new ContainerId(host.Beta, "DevB3"),
        };

        var claims = members.Select(wip.TryEnter).ToList();

        try
        {
            Assert.Equal(2, claims.Count(claim => claim is not null));
            Assert.NotNull(claims[0]);
            Assert.NotNull(claims[1]);

            using var view = JsonDocument.Parse(await client.GetStringAsync("/api/wip", ct));
            Assert.Equal(2, view.RootElement.GetProperty("max").GetInt32());
            Assert.Equal(2, view.RootElement.GetProperty("running").GetArrayLength());
            Assert.Equal(
                ["DevA2", "DevB2", "DevA3", "DevB3"],
                view.RootElement.GetProperty("waiting").EnumerateArray()
                    .Select(hold => hold.GetProperty("member").GetString()));

            using var rollup = JsonDocument.Parse(await client.GetStringAsync("/api/teams/rollup", ct));
            var rows = rollup.RootElement.GetProperty("teams").EnumerateArray()
                .ToDictionary(row => row.GetProperty("team").GetString()!, StringComparer.OrdinalIgnoreCase);

            foreach (var team in new[] { host.Alpha, host.Beta })
            {
                Assert.Equal(1, rows[team].GetProperty("running").GetInt32());
                Assert.Equal(2, rows[team].GetProperty("waiting").GetInt32());
                Assert.Equal("waiting for a slot", rows[team].GetProperty("slotStatus").GetString());
            }

            // Audit: old 4, new 2, the person's email.
            var audit = await host.Services.GetRequiredService<ITenantLog>()
                .FindLatestAsync(TenantActions.TenantSettingChanged, "wip.maxRunning", ct);
            Assert.NotNull(audit);
            Assert.Equal("person@example.test", audit!.ActorEmail);
            using var detail = JsonDocument.Parse(audit.Detail!);
            Assert.Equal("4", detail.RootElement.GetProperty("old").GetString());
            Assert.Equal("2", detail.RootElement.GetProperty("new").GetString());

            // A released slot goes to the head of the queue, and only one does.
            claims[0]!.Dispose();
            Assert.Null(wip.TryEnter(members[5]));
            Assert.Null(wip.TryEnter(members[3]));
            claims[2] = wip.TryEnter(members[2]);
            Assert.NotNull(claims[2]);
            Assert.Equal(3, wip.View().Waiting.Count);
        }
        finally
        {
            foreach (var claim in claims) claim?.Dispose();
            foreach (var member in members) wip.Withdraw(member);
        }
    }
}

/// <summary>
/// `tell` is never held. With every slot taken - the reserved Manager slot included - a tell
/// to another team's Manager answers at once and is written; the Manager's run is what waits, its
/// snapshot says so, and its team's rollup row reads "waiting for a slot" naming it.
/// </summary>
/// <remarks>
/// The claims are deliberately NOT released: releasing them would let the held Manager start a real
/// agent run. Fixture disposal stops the container while it is still waiting.
/// </remarks>
public sealed class TellNeverHeldTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_tell_into_a_full_pool_answers_at_once_and_the_run_waits_visibly()
    {
        var ct = TestContext.Current.CancellationToken;
        var wip = host.Services.GetRequiredService<WipLedger>();
        var containers = host.Services.GetRequiredService<ContainerHost>();
        wip.SetMax(1);

        Assert.NotNull(wip.TryEnter(new ContainerId(host.Alpha, "Filler")));
        Assert.NotNull(wip.TryEnter(new ContainerId(host.Alpha, "Manager")));

        using var client = await host.PersonAsync();
        var clock = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync(
            $"/api/teams/{host.Beta}/containers/Manager/tell",
            new { instruction = "Wake while the pool is full." }, ct);
        clock.Stop();

        Assert.True(response.IsSuccessStatusCode, $"tell answered {(int)response.StatusCode}");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"tell took {clock.Elapsed}");

        var betaManager = new ContainerId(host.Beta, "Manager");

        // The run is what waits, visibly: queued in the ledger and held on the snapshot.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline
               && !(containers.Find(betaManager)?.Snapshot().Held == true
                    && wip.View().Waiting.Any(hold => hold.Team == host.Beta)))
        {
            await Task.Delay(50, ct);
        }

        Assert.True(containers.Find(betaManager)!.Snapshot().Held, "the Manager's snapshot is not held");
        Assert.NotEqual(ContainerState.Running, containers.Find(betaManager)!.State);
        Assert.Contains(wip.View().Waiting, hold => hold.Team == host.Beta && hold.Member == "Manager");

        using var rollup = JsonDocument.Parse(await client.GetStringAsync("/api/teams/rollup", ct));
        var beta = rollup.RootElement.GetProperty("teams").EnumerateArray()
            .Single(row => string.Equals(row.GetProperty("team").GetString(), host.Beta, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("waiting for a slot", beta.GetProperty("slotStatus").GetString());
        Assert.Equal(["Manager"], beta.GetProperty("held").EnumerateArray().Select(e => e.GetString()));

        // A second tell to the same held Manager is not refused either.
        var again = await client.PostAsJsonAsync(
            $"/api/teams/{host.Beta}/containers/Manager/tell",
            new { instruction = "Second wake." }, ct);
        Assert.True(again.IsSuccessStatusCode, $"second tell answered {(int)again.StatusCode}");
    }
}
