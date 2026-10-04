using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A HOST START CLOSES WHAT IT DID NOT SEE END. A hold row still open when the Host starts belonged
/// to a Host that went down while the run waited: it is closed at the start, its reason unchanged,
/// and marked unfinished - in `all` and `control` alike. See AGENTS.md, Admission.
/// </summary>
public sealed class AdmissionHoldsAtStartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("control")]
    public async Task An_open_row_at_start_is_closed_unfinished_with_its_reason(string? role)
    {
        var root = Directory.CreateTempSubdirectory("harness-holds-start-").FullName;
        try
        {
            var database = Path.Combine(root, "messages.db");
            await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct: Ct);

            var store = new SqliteAdmissionHolds(database);
            var heldAt = DateTimeOffset.UtcNow.AddMinutes(-30);
            const string memory = "waiting for memory: 11.2 of 12.9 GB in use";
            await store.OpenAsync("Alpha", "Dev", 12, heldAt, AdmissionHoldKinds.Memory, memory, Ct);
            await store.OpenAsync("Alpha", "Ops", null, heldAt, AdmissionHoldKinds.Slot, WipLedger.SlotReason, Ct);
            await store.CloseAsync("Alpha", "Ops", heldAt.AddMinutes(2), Ct);

            var before = DateTimeOffset.UtcNow;
            await using (var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                         {
                             builder.UseSetting("DataRoot", root).UseSetting("Logging:LogLevel:Default", "Warning");
                             if (role is not null) builder.UseSetting("Role", role);
                         }))
            {
                Assert.NotNull(host.Services.GetRequiredService<IAdmissionHoldLedger>());
            }

            var rows = await store.ReadTeamAsync("Alpha", DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Ct);

            var dev = rows.Single(r => r.Member == "Dev");
            Assert.True(dev.Unfinished);
            Assert.NotNull(dev.ReleasedAt);
            Assert.InRange(dev.ReleasedAt!.Value, before, DateTimeOffset.UtcNow);
            Assert.Equal(AdmissionHoldKinds.Memory, dev.ReasonKind);
            Assert.Equal(memory, dev.Reason);
            Assert.Equal(12, dev.DeliverySeq);

            // A row closed before the Host went down is left as it was.
            var ops = rows.Single(r => r.Member == "Ops");
            Assert.False(ops.Unfinished);
            Assert.Equal(heldAt.AddMinutes(2), ops.ReleasedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
        }

    [Theory]
    [InlineData(null)]
    [InlineData("control")]
    public async Task A_graceful_stop_leaves_a_held_waiters_row_open_and_the_next_start_closes_it_unfinished(string? role)
    {
        var root = Directory.CreateTempSubdirectory("harness-holds-stop-").FullName;
        try
        {
            string team;
            await using (var host = StartHost(root, role))
            {
                team = (await host.Services.GetRequiredService<TeamRegistry>()
                    .CreateAsync("Alpha", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

                // Both slots taken (the Manager's reserved one too), so the team's Manager is held -
                // for a slot in `all`, for a worker in `control`, where none is connected.
                var wip = host.Services.GetRequiredService<WipLedger>();
                wip.SetMax(1);
                wip.TryEnter(new ContainerId("Busy", "Dev"));
                wip.TryEnter(new ContainerId("Busy", WipLedger.ManagerName));

                await host.Services.GetRequiredService<IMessageLog>().AppendAsync(new NewMessage(
                    MessageTypes.InstructionFor(new ContainerId(team, WipLedger.ManagerName)),
                    JsonSerializer.Serialize(new { instruction = "plan" }), "console"), Ct);

                var store = host.Services.GetRequiredService<IAdmissionHoldLedger>();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (!(await store.ReadTeamAsync(team, DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Ct))
                           .Any(row => row.Member == WipLedger.ManagerName))
                {
                    if (DateTimeOffset.UtcNow > deadline) Assert.Fail("The held Manager's row was never written.");
                    await Task.Delay(50, Ct);
                }
            }

            // The Host going down is not the hold ending: the row stays open for the next start.
            var holds = new SqliteAdmissionHolds(Path.Combine(root, "messages.db"));
            var stopped = Assert.Single(await holds.ReadTeamAsync(team, DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Ct));
            Assert.Null(stopped.ReleasedAt);

            var before = DateTimeOffset.UtcNow;
            await using (var restarted = StartHost(root, role))
            {
                Assert.NotNull(restarted.Services.GetRequiredService<IAdmissionHoldLedger>());
            }

            // The restart resumes the delivery, which may be held again: a row of its own.
            var closed = (await holds.ReadTeamAsync(team, DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Ct))
                .OrderBy(row => row.HeldAt).First();
            Assert.Equal(stopped.HeldAt, closed.HeldAt);
            Assert.True(closed.Unfinished);
            Assert.NotNull(closed.ReleasedAt);
            Assert.InRange(closed.ReleasedAt!.Value, before, DateTimeOffset.UtcNow);
            Assert.Equal(stopped.Reason, closed.Reason);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>The real Host over <paramref name="root"/>, its agents a fake that runs nothing.</summary>
    private static WebApplicationFactory<Program> StartHost(string root, string? role) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataRoot", root).UseSetting("Logging:LogLevel:Default", "Warning")
                .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent()));
            if (role is not null) builder.UseSetting("Role", role);
        });
}
