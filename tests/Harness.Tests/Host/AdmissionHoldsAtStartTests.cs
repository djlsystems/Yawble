using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
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
}
