using System.Data.Common;
using Harness.Contracts;
using Harness.Containers;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IAdmissionHoldLedger"/>, as each provider must keep it. The first two are
/// <c>AdmissionHoldsTests</c>' with the same names and values (a row is only ever closed, once, and
/// nothing deletes one; a start closes what it did not see end); the rest are the interface's
/// remarks (the team's name as it was; which holds a period reads; a close finds the member whatever
/// the case). The store has no read-then-write to race: an open is one insert and a close one update.
/// </summary>
public abstract class AdmissionHoldLedgerContract : ProviderContract
{
    private const string Memory = "Waiting for memory: 2 runs hold 3.0 GB of 4.0 GB.";

    private static readonly DateTimeOffset HeldAt = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    protected async Task<IAdmissionHoldLedger> CreateAsync() => (await StoresAsync()).AdmissionHolds;

    [Fact]
    public async Task An_update_other_than_closing_once_and_any_delete_are_refused_and_a_log_purge_leaves_the_rows()
    {
        var holds = await CreateAsync();
        var stores = await StoresAsync();

        await holds.OpenAsync("Alpha", "Dev1", 5, HeldAt, AdmissionHoldKinds.Slot, WipLedger.SlotReason, Ct);

        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("UPDATE admission_holds SET reason = 'other'"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("UPDATE admission_holds SET unfinished = 1"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync(
            "UPDATE admission_holds SET released_at = '2026-10-04T09:01:00.0000000+00:00', member = 'Dev9'"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync(
            "UPDATE admission_holds SET released_at = '2026-10-04T09:01:00.0000000+00:00', unfinished = 2"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("DELETE FROM admission_holds"));

        await holds.CloseAsync("Alpha", "Dev1", HeldAt.AddMinutes(4), Ct);
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync(
            "UPDATE admission_holds SET released_at = '2026-10-04T09:09:00.0000000+00:00'"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("UPDATE admission_holds SET released_at = NULL"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("DELETE FROM admission_holds"));

        // The log's one deletion - Reset's purge, and any retention built on it - leaves the holds.
        var row = await stores.Log.AppendAsync(new NewMessage("test.mark", "{}", "console"), Ct);
        Assert.Equal(1, (await stores.Log.DeleteAsync([row.Seq], Ct)).Purged);

        var kept = Assert.Single(await RowsAsync(holds));
        Assert.Equal(HeldAt.AddMinutes(4), kept.ReleasedAt);
        Assert.Equal(WipLedger.SlotReason, kept.Reason);
    }

    [Fact]
    public async Task Closing_the_unfinished_rows_marks_them_and_keeps_their_reason()
    {
        var holds = await CreateAsync();

        await holds.OpenAsync("Alpha", "Dev1", null, HeldAt, AdmissionHoldKinds.Memory, Memory, Ct);
        await holds.OpenAsync("Alpha", "Dev2", null, HeldAt, AdmissionHoldKinds.Slot, WipLedger.SlotReason, Ct);
        await holds.CloseAsync("Alpha", "Dev2", HeldAt.AddMinutes(1), Ct);

        Assert.Equal(1, await holds.CloseUnfinishedAsync(HeldAt.AddMinutes(9), Ct));

        var rows = await RowsAsync(holds);
        Assert.Equal(
            ["Dev1|memory|9|True", "Dev2|slot|1|False"],
            rows.OrderBy(r => r.Member).Select(r => $"{r.Member}|{r.ReasonKind}|{(r.ReleasedAt!.Value - HeldAt).TotalMinutes}|{r.Unfinished}"));
        Assert.Equal(Memory, rows.Single(r => r.Member == "Dev1").Reason);
    }

    [Fact]
    public async Task A_hold_keeps_the_teams_name_as_it_was_and_its_id_when_it_has_none()
    {
        var holds = await CreateAsync();
        var stores = await StoresAsync();

        await stores.ExecuteAsync("INSERT INTO teams (id, name, created_utc) VALUES ('Alpha', 'Alpha Squad', '2026-10-01T00:00:00.0000000+00:00')");
        await holds.OpenAsync("Alpha", "Dev1", 7, HeldAt, AdmissionHoldKinds.Worker, "No worker is connected.", Ct);
        await stores.ExecuteAsync("UPDATE teams SET name = 'Renamed' WHERE id = 'Alpha'");
        await holds.OpenAsync("Beta", "Dev1", null, HeldAt, AdmissionHoldKinds.Slot, WipLedger.SlotReason, Ct);

        var alpha = Assert.Single(await RowsAsync(holds));
        Assert.Equal(("Alpha", "Alpha Squad", 7L, AdmissionHoldKinds.Worker), (alpha.TeamId, alpha.TeamName, alpha.DeliverySeq!.Value, alpha.ReasonKind));

        var beta = Assert.Single(await holds.ReadTeamAsync("Beta", DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Ct));
        Assert.Equal("Beta", beta.TeamName);
        Assert.Null(beta.DeliverySeq);
    }

    [Fact]
    public async Task A_period_reads_the_holds_since_the_teams_creation_that_overlap_it_in_the_order_they_were_held()
    {
        var holds = await CreateAsync();

        await holds.OpenAsync("Alpha", "Old", null, HeldAt.AddMinutes(-60), AdmissionHoldKinds.Slot, "before creation", Ct);
        await holds.CloseAsync("Alpha", "Old", HeldAt.AddMinutes(50), Ct);
        await holds.OpenAsync("Alpha", "Early", null, HeldAt, AdmissionHoldKinds.Slot, "released before the period", Ct);
        await holds.CloseAsync("Alpha", "Early", HeldAt.AddMinutes(5), Ct);
        await holds.OpenAsync("Alpha", "Across", null, HeldAt.AddMinutes(8), AdmissionHoldKinds.Slot, "released inside it", Ct);
        await holds.CloseAsync("Alpha", "Across", HeldAt.AddMinutes(12), Ct);
        await holds.OpenAsync("Alpha", "Open", null, HeldAt.AddMinutes(2), AdmissionHoldKinds.Memory, "still held", Ct);
        await holds.OpenAsync("Alpha", "Late", null, HeldAt.AddMinutes(30), AdmissionHoldKinds.Slot, "held after it", Ct);
        await holds.OpenAsync("Beta", "Other", null, HeldAt.AddMinutes(9), AdmissionHoldKinds.Slot, "another team", Ct);

        var read = await holds.ReadTeamAsync("alpha", HeldAt, HeldAt.AddMinutes(10), HeldAt.AddMinutes(20), Ct);

        Assert.Equal(["Open", "Across"], read.Select(r => r.Member));
    }

    [Fact]
    public async Task A_close_closes_the_members_open_row_whatever_the_case_and_no_other()
    {
        var holds = await CreateAsync();

        await holds.OpenAsync("Alpha", "Dev1", null, HeldAt, AdmissionHoldKinds.Slot, WipLedger.SlotReason, Ct);
        await holds.OpenAsync("Alpha", "Dev2", null, HeldAt, AdmissionHoldKinds.Slot, WipLedger.SlotReason, Ct);

        await holds.CloseAsync("ALPHA", "dev1", HeldAt.AddMinutes(3), Ct);
        await holds.CloseAsync("Alpha", "Dev1", HeldAt.AddMinutes(7), Ct);

        var rows = (await RowsAsync(holds)).OrderBy(r => r.Member).ToList();
        Assert.Equal(HeldAt.AddMinutes(3), rows[0].ReleasedAt);
        Assert.Null(rows[1].ReleasedAt);
    }

    private static Task<IReadOnlyList<AdmissionHoldRow>> RowsAsync(IAdmissionHoldLedger holds) =>
        holds.ReadTeamAsync("Alpha", DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Ct);
}

public sealed class SqliteAdmissionHoldLedgerTests : AdmissionHoldLedgerContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerAdmissionHoldLedgerTests : AdmissionHoldLedgerContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
