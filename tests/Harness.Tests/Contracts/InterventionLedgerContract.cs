using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// HUMAN INTERVENTIONS ARE RECORDED BY THE ROUTE, NEVER PUBLISHED (<c>intervention_ledger</c>,
/// <c>outcome-010</c>), as each provider's stores write them: a person's act handed to the log on its
/// <see cref="AppendContext"/>, or to the tenant log on its <see cref="TriggerAudit"/>, lands in the same
/// transaction as that row, naming it and its workflow; a key is stored as its owner, who still reads
/// after the key and the owner are gone; nobody but a person writes a row; the kind is from the closed
/// set; and nothing rewrites or deletes a row.
/// </summary>
public abstract class InterventionLedgerContract : ProviderContract
{
    private static readonly ContainerId Manager = new("Alpha", "Manager");

    private const string Columns =
        "actor_id, actor_email, kind, team_id, correlation, outcome_id_at_event, related_seq, duration_seconds, duration_measured";

    [Fact]
    public async Task A_persons_act_is_written_with_its_log_row_naming_the_row_its_workflow_and_its_team()
    {
        var stores = await StoresAsync();
        var person = new AppendContext(PrincipalKind.User, "user-1", Email: "ada@example.test");

        var root = await stores.Log.AppendAsync(Instruction("start", "user-1"), person.Intervening(InterventionKinds.Instruction, "Alpha"), Ct);
        var nudge = await stores.Log.AppendAsync(Instruction("nudge", "user-1", root.Seq), person.Intervening(InterventionKinds.Nudge, "Alpha"), Ct);

        Assert.Equal(
            [
                ["user-1", "ada@example.test", InterventionKinds.Instruction, "Alpha", root.Seq, null, root.Seq, null, 0L],
                ["user-1", "ada@example.test", InterventionKinds.Nudge, "Alpha", root.Seq, null, nudge.Seq, null, 0L],
            ],
            await RowsAsync(stores));
    }

    [Fact]
    public async Task The_act_names_the_workflows_outcome_at_that_moment()
    {
        var stores = await StoresAsync();
        var person = new AppendContext(PrincipalKind.User, "user-1", Email: "ada@example.test");
        var root = await stores.Log.AppendAsync(Instruction("start", "user-1"), Ct);
        var outcome = await stores.Outcomes.CreateAsync(
            "Qualified pipeline", new OutcomeEdit(), new OutcomeActor("ada@example.test", OutcomeActorKind.Person),
            new TriggerAudit(null, "ada@example.test", TenantActions.OutcomeCreated, null, null, null), Ct);
        await stores.Outcomes.LinkAsync(
            root.Seq, outcome.Outcome!.Id, "Alpha", new OutcomeActor("ada@example.test", OutcomeActorKind.Person), OutcomeLinkHow.Person,
            agentRule: false, new TriggerAudit("user-1", "ada@example.test", TenantActions.WorkflowOutcomeChanged, null, null, null)
            {
                Intervention = InterventionAct.Of(InterventionKinds.OutcomeChanged, person, "Alpha", root.Seq),
            },
            Ct);

        var closed = await stores.Log.AppendAsync(
            new NewMessage(MessageTypes.WorkflowClosed, """{"team":"Alpha"}""", "user-1", root.Seq),
            person.Intervening(InterventionKinds.Close, "Alpha"), Ct);

        // The tenant row's act names no log seq (it is not a message) but its workflow and the outcome it set.
        Assert.Equal(
            [
                ["user-1", "ada@example.test", InterventionKinds.OutcomeChanged, "Alpha", root.Seq, outcome.Outcome.Id, null, null, 0L],
                ["user-1", "ada@example.test", InterventionKinds.Close, "Alpha", root.Seq, outcome.Outcome.Id, closed.Seq, null, 0L],
            ],
            await RowsAsync(stores));
    }

    [Fact]
    public async Task An_api_key_act_is_its_owners_and_still_reads_after_the_key_and_the_owner_are_deleted()
    {
        var stores = await StoresAsync();
        await stores.ExecuteAsync(
            "INSERT INTO users (id, email, password_hash, created_at) "
            + "VALUES ('user-2', 'grace@example.test', 'x', '2026-10-01T00:00:00.0000000+00:00')");
        await stores.ExecuteAsync(
            "INSERT INTO principals (id, kind, team, hash, prefix, permits, created_at, owner_user_id) "
            + "VALUES ('apikey-1', 'ApiKey', NULL, 'h', 'p', '[]', '2026-10-01T00:00:00.0000000+00:00', 'user-2')");

        var root = await stores.Log.AppendAsync(
            Instruction("from a tool", "apikey-1"),
            new AppendContext(PrincipalKind.ApiKey, "apikey-1", OwnerUserId: "user-2").Intervening(InterventionKinds.Instruction, "Alpha"),
            Ct);

        await stores.ExecuteAsync("DELETE FROM users WHERE id = 'user-2'");
        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM principals WHERE id = 'apikey-1'"));

        Assert.Equal(
            [["user-2", "grace@example.test", InterventionKinds.Instruction, "Alpha", root.Seq, null, root.Seq, null, 0L]],
            await RowsAsync(stores));
    }

    [Fact]
    public async Task An_agent_a_member_key_a_concierge_a_trigger_and_the_host_write_no_row()
    {
        var stores = await StoresAsync();

        foreach (var context in new AppendContext?[]
        {
            new AppendContext(PrincipalKind.Container, "Alpha/Worker").Intervening(InterventionKinds.Instruction, "Alpha"),
            new AppendContext(PrincipalKind.Concierge, "concierge-1", OwnerUserId: "user-1").Intervening(InterventionKinds.Instruction, "Alpha"),
            new AppendContext(PrincipalKind.TenantConcierge, "concierge-2", OwnerUserId: "user-1").Intervening(InterventionKinds.Close, "Alpha"),
            null,
        })
        {
            await stores.Log.AppendAsync(Instruction("work", "Alpha/Worker"), context, Ct);
        }

        foreach (var source in new[] { "schedule:7", "trigger:8", "wake:9", "host" })
        {
            await stores.Log.AppendAsync(Instruction("fire", source), Ct);
        }

        Assert.Empty(await RowsAsync(stores));
    }

    [Fact]
    public async Task An_act_whose_log_row_does_not_land_is_not_written_either()
    {
        var stores = await StoresAsync();
        var person = new AppendContext(PrincipalKind.User, "user-1", Email: "ada@example.test").Intervening(InterventionKinds.Instruction, "Alpha");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => stores.Log.AppendWithinAsync(
            (_, _, _) => Task.FromResult<NewMessage?>(Instruction("start", "user-1")),
            (_, _, _, _) => throw new InvalidOperationException("the caller's own write failed"),
            person,
            Ct));

        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM messages"));
        Assert.Empty(await RowsAsync(stores));
    }

    [Fact]
    public async Task A_concierge_session_is_one_row_with_its_measured_duration()
    {
        var stores = await StoresAsync();

        await stores.Interventions.RecordAsync(InterventionAct.Session("user-1", "Alpha", 754), DateTimeOffset.UtcNow, Ct);

        Assert.Equal(
            [["user-1", null, InterventionKinds.ConciergeSession, "Alpha", null, null, null, 754L, 1L]],
            await RowsAsync(stores));
    }

    [Fact]
    public async Task A_kind_outside_the_set_or_a_duration_on_an_unmeasured_kind_is_refused_and_nothing_is_written()
    {
        var stores = await StoresAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            stores.Interventions.RecordAsync(new InterventionAct("pressed_a_button", "user-1", null, "Alpha"), DateTimeOffset.UtcNow, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stores.Interventions.RecordAsync(new InterventionAct(InterventionKinds.Close, "user-1", null, "Alpha", null, 60), DateTimeOffset.UtcNow, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stores.Interventions.RecordAsync(new InterventionAct(InterventionKinds.ConciergeSession, "user-1", null, "Alpha"), DateTimeOffset.UtcNow, Ct));

        // Through the log too: the row and its act are one transaction, so the row is refused with it.
        await Assert.ThrowsAsync<ArgumentException>(() => stores.Log.AppendAsync(
            Instruction("start", "user-1"),
            new AppendContext(PrincipalKind.User, "user-1") { Intervention = new InterventionAct("forged", "user-1", null, "Alpha") },
            Ct));

        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM messages"));
        Assert.Empty(await RowsAsync(stores));
    }

    [Fact]
    public async Task The_ledger_refuses_update_and_delete()
    {
        var stores = await StoresAsync();
        await stores.Log.AppendAsync(
            Instruction("start", "user-1"),
            new AppendContext(PrincipalKind.User, "user-1", Email: "ada@example.test").Intervening(InterventionKinds.Instruction, "Alpha"),
            Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => stores.ExecuteAsync("UPDATE intervention_ledger SET actor_id = 'someone-else'"));
        await Assert.ThrowsAnyAsync<Exception>(() => stores.ExecuteAsync("DELETE FROM intervention_ledger"));

        Assert.Equal("user-1", Assert.Single(await RowsAsync(stores))[0]);
    }

    // ---- fixtures ----

    private static async Task<IReadOnlyList<object?[]>> RowsAsync(ContractStores stores) =>
        [.. (await stores.RowsAsync($"SELECT {Columns} FROM intervention_ledger ORDER BY id")).Select(Normalise)];

    /// <summary>Each provider's integers read back as <see cref="long"/>, so one expectation holds for both.</summary>
    private static object?[] Normalise(object?[] row) =>
        [.. row.Select(value => value switch { int i => (long)i, short s => (long)s, _ => value })];

    private static NewMessage Instruction(string text, string source, long? causation = null) =>
        new(MessageTypes.InstructionFor(Manager), JsonSerializer.Serialize(new { instruction = text }), source, causation);
}

public sealed class SqliteInterventionLedgerTests : InterventionLedgerContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerInterventionLedgerTests : InterventionLedgerContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
