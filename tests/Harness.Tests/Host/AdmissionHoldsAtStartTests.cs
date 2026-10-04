using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
            var stop = new FreeSlotsOnStop();
            AdmissionHoldWriter writer;
            await using (var host = StartHost(root, role, services => services.AddHostedService(_ => stop)))
            {
                team = (await host.Services.GetRequiredService<TeamRegistry>()
                    .CreateAsync("Alpha", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
                writer = host.Services.GetRequiredService<AdmissionHoldWriter>();

                // Both slots taken (the Manager's reserved one too), so the team's Manager is held for
                // a slot. In `control` a worker is connected first, so the slots run on it.
                var wip = host.Services.GetRequiredService<WipLedger>();
                if (role is not null)
                {
                    var worker = new IdleWorker(new WorkerId("w1"));
                    host.Services.GetRequiredService<WorkerPool>()
                        .Join(worker, new WorkerInfo(worker.Id, "test", 4, null, DateTimeOffset.UtcNow));
                    wip.WorkersChanged();
                }

                wip.SetMax(1);
                stop.Wip = wip;
                stop.Team = team;
                stop.Slots.Add(Assert.IsAssignableFrom<IDisposable>(wip.TryEnter(new ContainerId("Busy", "Dev"))));
                stop.Slots.Add(Assert.IsAssignableFrom<IDisposable>(wip.TryEnter(new ContainerId("Busy", WipLedger.ManagerName))));

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

                Assert.Equal(WipLedger.SlotReason, Assert.Single(wip.View().Waiting).Reason);
            }

            // The stop freed the slots and the held Manager's claim loop, not yet cancelled, took one.
            Assert.True(stop.AdmittedDuringStop, "The held Manager was not admitted while the Host stopped.");

            // The Host going down is not the hold ending: the row stays open for the next start. Every
            // write the stop queued has landed before this read.
            await writer.WrittenAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
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
    private static WebApplicationFactory<Program> StartHost(string root, string? role, Action<IServiceCollection>? services = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataRoot", root).UseSetting("Logging:LogLevel:Default", "Warning")
                .ConfigureTestServices(s =>
                {
                    s.AddSingleton<IAgentRunner>(new FakeAgent());
                    services?.Invoke(s);
                });
            if (role is not null) builder.UseSetting("Role", role);
        });

    /// <summary>
    /// What a stop does to a running run: its slot is released after the Host began stopping and before
    /// the members' claim loops are cancelled. Stopped first of the hosted services (registered last),
    /// it frees the busy slots and waits for the held Manager to take one.
    /// </summary>
    private sealed class FreeSlotsOnStop : IHostedService
    {
        public List<IDisposable> Slots { get; } = [];

        public WipLedger? Wip { get; set; }

        public string? Team { get; set; }

        public bool AdmittedDuringStop { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Slots.ForEach(slot => slot.Dispose());
            if (Wip is null) return;

            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                // Out of the queue with its claim loop still live: admitted (its run may already be over).
                if (!Wip.View().Waiting.Any(hold => hold.Team == Team && hold.Member == WipLedger.ManagerName))
                {
                    AdmittedDuringStop = true;
                    return;
                }

                await Task.Delay(20, CancellationToken.None);
            }
        }
    }

    private sealed class IdleWorker(WorkerId id) : IRunWorker
    {
        public WorkerId Id => id;

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public Task SendAsync(ControlMessage message, CancellationToken ct = default) => Task.CompletedTask;
    }
}
