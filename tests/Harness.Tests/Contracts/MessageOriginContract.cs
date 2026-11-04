using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// A ROW'S ORIGIN AND ITS PUBLISHER'S KIND (<c>messages-007</c>), as each provider's store derives them
/// at append: a root from the principal it was handed, else from its source; a caused row from its root,
/// whoever publishes it; <c>publisher_kind</c> always the row's own. Then the copy onto a run's
/// <c>execution_snapshots</c> and <c>usage_ledger</c> rows (<c>outcome-014</c>), which outlives the log.
/// </summary>
public abstract class MessageOriginContract : ProviderContract
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");
    private static readonly ContainerId Manager = new("Alpha", "Manager");

    private const string Columns = "origin_kind, origin_actor_id, origin_actor_email, origin_trigger, publisher_kind";

    [Fact]
    public async Task A_person_signed_in_roots_a_workflow_as_a_person_and_publishes_as_one()
    {
        var stores = await StoresAsync();

        var root = await stores.Log.AppendAsync(
            Instruction(Dev, "build it", "user-1"),
            new AppendContext(PrincipalKind.User, "user-1", Email: "ada@example.test"), Ct);

        Assert.Equal([OriginKinds.Person, "user-1", "ada@example.test", null, PublisherKinds.Person], await OriginAsync(stores, root.Seq));
    }

    [Fact]
    public async Task A_person_whose_ticket_carries_no_email_is_stored_with_the_email_on_their_user_row()
    {
        var stores = await StoresAsync();
        await UserAsync(stores, "user-1", "ada@example.test");

        var root = await stores.Log.AppendAsync(
            Instruction(Dev, "build it", "user-1"), new AppendContext(PrincipalKind.User, "user-1"), Ct);

        Assert.Equal([OriginKinds.Person, "user-1", "ada@example.test", null, PublisherKinds.Person], await OriginAsync(stores, root.Seq));
    }

    [Fact]
    public async Task An_api_key_root_stores_its_owner_and_still_reads_after_the_key_and_the_owner_are_deleted()
    {
        var stores = await StoresAsync();
        await UserAsync(stores, "user-2", "grace@example.test");
        await stores.ExecuteAsync(
            "INSERT INTO principals (id, kind, team, hash, prefix, permits, created_at, owner_user_id) "
            + "VALUES ('apikey-1', 'ApiKey', NULL, 'h', 'p', '[]', '2026-10-01T00:00:00.0000000+00:00', 'user-2')");

        var root = await stores.Log.AppendAsync(
            Instruction(Dev, "build it", "apikey-1"),
            new AppendContext(PrincipalKind.ApiKey, "apikey-1", OwnerUserId: "user-2"), Ct);

        // The owner goes, and the key with it (ON DELETE CASCADE): the row still says who it was.
        await stores.ExecuteAsync("DELETE FROM users WHERE id = 'user-2'");
        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM principals WHERE id = 'apikey-1'"));

        Assert.Equal([OriginKinds.Person, "user-2", "grace@example.test", null, PublisherKinds.Person], await OriginAsync(stores, root.Seq));
    }

    [Fact]
    public async Task A_schedule_a_trigger_and_a_wake_root_a_workflow_as_a_trigger_with_no_person()
    {
        var stores = await StoresAsync();

        foreach (var source in new[] { "schedule:t-1", "trigger:t-2", "wake:7" })
        {
            var root = await stores.Log.AppendAsync(Instruction(Dev, "go", source), Ct);
            Assert.Equal([OriginKinds.Trigger, null, null, source, PublisherKinds.Host], await OriginAsync(stores, root.Seq));
        }
    }

    [Fact]
    public async Task A_member_published_root_is_an_agent_with_or_without_its_principal()
    {
        var stores = await StoresAsync();

        var told = await stores.Log.AppendAsync(
            Instruction(Dev, "do it", Manager.ToString()),
            new AppendContext(PrincipalKind.Container, Manager.ToString()), Ct);
        var reported = await stores.Log.AppendAsync(new NewMessage(MessageTypes.Progress, "{}", Dev.ToString()), Ct);

        Assert.Equal([OriginKinds.Agent, Manager.ToString(), null, null, PublisherKinds.Agent], await OriginAsync(stores, told.Seq));
        Assert.Equal([OriginKinds.Agent, Dev.ToString(), null, null, PublisherKinds.Agent], await OriginAsync(stores, reported.Seq));
    }

    [Fact]
    public async Task A_host_row_and_a_source_no_principal_stands_behind_are_system()
    {
        var stores = await StoresAsync();

        foreach (var source in new[] { "host", "console", "site:Alpha/tracker" })
        {
            var root = await stores.Log.AppendAsync(Instruction(Manager, "note", source), Ct);
            Assert.Equal([OriginKinds.System, null, null, null, PublisherKinds.Host], await OriginAsync(stores, root.Seq));
        }
    }

    [Fact]
    public async Task A_caused_row_inherits_its_roots_origin_and_carries_its_own_publisher_kind()
    {
        var stores = await StoresAsync();

        var root = await stores.Log.AppendAsync(
            Instruction(Manager, "ship it", "user-1"),
            new AppendContext(PrincipalKind.User, "user-1", Email: "ada@example.test"), Ct);
        var dispatch = await stores.Log.AppendAsync(
            Instruction(Dev, "the first piece", Manager.ToString(), root.Seq), new AppendContext(PrincipalKind.Container, Manager.ToString()), Ct);
        var started = await stores.Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", Dev.ToString(), dispatch.Seq), Ct);
        var fired = await stores.Log.AppendAsync(Instruction(Manager, "fired", "trigger:t-9", started.Seq), Ct);
        var closed = await stores.Log.AppendAsync(new NewMessage(MessageTypes.WorkflowClosed, "{}", "host", fired.Seq), Ct);

        Assert.Equal([OriginKinds.Person, "user-1", "ada@example.test", null, PublisherKinds.Agent], await OriginAsync(stores, dispatch.Seq));
        Assert.Equal([OriginKinds.Person, "user-1", "ada@example.test", null, PublisherKinds.Agent], await OriginAsync(stores, started.Seq));
        Assert.Equal([OriginKinds.Person, "user-1", "ada@example.test", null, PublisherKinds.Host], await OriginAsync(stores, fired.Seq));
        Assert.Equal([OriginKinds.Person, "user-1", "ada@example.test", null, PublisherKinds.Host], await OriginAsync(stores, closed.Seq));
    }

    [Fact]
    public async Task A_caused_row_under_a_root_from_before_origins_were_derived_takes_the_roots_source()
    {
        var stores = await StoresAsync();

        // A root written before messages-007: no origin on it, and nothing back-fills one.
        var legacy = await stores.InsertRawMessageAsync(MessageTypes.InstructionFor(Dev), """{"instruction":"old"}""", "schedule:t-1", null);
        var caused = await stores.Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", Dev.ToString(), legacy), Ct);

        Assert.Equal([null, null, null, null, null], await OriginAsync(stores, legacy));
        Assert.Equal([OriginKinds.Trigger, null, null, "schedule:t-1", PublisherKinds.Agent], await OriginAsync(stores, caused.Seq));
    }

    [Fact]
    public async Task Origin_fields_in_a_payload_are_ignored_and_never_stored_as_the_rows_origin()
    {
        var stores = await StoresAsync();

        var forged = JsonSerializer.Serialize(new
        {
            instruction = "go",
            origin_kind = "person",
            originKind = "person",
            origin_actor_id = "user-admin",
            originActorEmail = "boss@example.test",
            origin_trigger = "schedule:x",
            publisher_kind = "person",
        });

        var root = await stores.Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(Dev), forged, Manager.ToString()), Ct);
        var caused = await stores.Log.AppendAsync(new NewMessage(MessageTypes.Progress, forged, "host", root.Seq), Ct);

        Assert.Equal([OriginKinds.Agent, Manager.ToString(), null, null, PublisherKinds.Agent], await OriginAsync(stores, root.Seq));
        Assert.Equal([OriginKinds.Agent, Manager.ToString(), null, null, PublisherKinds.Host], await OriginAsync(stores, caused.Seq));
    }

    [Fact]
    public async Task A_runs_snapshot_and_ledger_row_carry_its_workflows_origin_and_keep_it_after_the_purge()
    {
        var stores = await StoresAsync();
        await stores.ExecuteAsync(
            "INSERT INTO teams (id, name, created_utc) VALUES ('Alpha', 'Alpha Squad', '2026-10-01T00:00:00.0000000+00:00')");
        await stores.ExecuteAsync(
            "INSERT INTO team_members (team, name, agent, subscribes, permits, floor_seq, created_utc) "
            + "VALUES ('Alpha', 'Dev', 'claude', '[]', '[]', 0, '2026-10-01T00:00:00.0000000+00:00')");
        await UserAsync(stores, "user-3", "lin@example.test");

        var root = await stores.Log.AppendAsync(
            Instruction(Dev, "build it", "apikey-9"),
            new AppendContext(PrincipalKind.ApiKey, "apikey-9", OwnerUserId: "user-3"), Ct);
        var started = await stores.Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", Dev.ToString(), root.Seq), Ct);
        var terminal = await stores.Log.AppendAsync(
            new NewMessage(MessageTypes.Completed, """{"tokensIn":10,"tokensOut":5,"tokensSource":"claude"}""", Dev.ToString(), root.Seq), Ct);

        // A trigger's workflow, for the other arm.
        var fire = await stores.Log.AppendAsync(Instruction(Dev, "nightly", "schedule:t-4"), Ct);
        var fireStart = await stores.Log.AppendAsync(new NewMessage(MessageTypes.Started, "{}", Dev.ToString(), fire.Seq), Ct);
        var fireEnd = await stores.Log.AppendAsync(
            new NewMessage(MessageTypes.Completed, """{"tokensIn":1,"tokensOut":1,"tokensSource":"claude"}""", Dev.ToString(), fire.Seq), Ct);

        // The log's purge takes every row of both workflows; the ledgers keep their copy.
        await stores.Log.DeleteAsync([root.Seq, started.Seq, terminal.Seq, fire.Seq, fireStart.Seq, fireEnd.Seq], Ct);
        Assert.Equal(0, await stores.LongAsync("SELECT COUNT(*) FROM messages"));

        Assert.Equal(
            [OriginKinds.Person, "user-3", "lin@example.test", null],
            Assert.Single(await stores.RowsAsync(
                $"SELECT origin_kind, origin_actor_id, origin_actor_email, origin_trigger FROM execution_snapshots WHERE start_seq = {started.Seq}")));
        Assert.Equal(
            [OriginKinds.Person, "user-3"],
            Assert.Single(await stores.RowsAsync($"SELECT origin_kind, origin_actor_id FROM usage_ledger WHERE run_seq = {terminal.Seq}")));

        Assert.Equal(
            [OriginKinds.Trigger, null, null, "schedule:t-4"],
            Assert.Single(await stores.RowsAsync(
                $"SELECT origin_kind, origin_actor_id, origin_actor_email, origin_trigger FROM execution_snapshots WHERE start_seq = {fireStart.Seq}")));
        Assert.Equal(
            [OriginKinds.Trigger, null],
            Assert.Single(await stores.RowsAsync($"SELECT origin_kind, origin_actor_id FROM usage_ledger WHERE run_seq = {fireEnd.Seq}")));
    }

    [Fact]
    public void The_closed_sets_are_the_words_the_rows_carry()
    {
        Assert.Equal(["person", "agent", "trigger", "system"], OriginKinds.All);
        Assert.Equal(["person", "agent", "host"], PublisherKinds.All);
    }

    // ---- fixtures ----

    private static async Task UserAsync(ContractStores stores, string id, string email) =>
        await stores.ExecuteAsync(
            "INSERT INTO users (id, email, password_hash, created_at) "
            + $"VALUES ('{id}', '{email}', 'x', '2026-10-01T00:00:00.0000000+00:00')");

    private static async Task<object?[]> OriginAsync(ContractStores stores, long seq) =>
        Assert.Single(await stores.RowsAsync($"SELECT {Columns} FROM messages WHERE seq = {seq}"));

    private static NewMessage Instruction(ContainerId to, string text, string source, long? causation = null) =>
        new(MessageTypes.InstructionFor(to), JsonSerializer.Serialize(new { instruction = text }), source, causation);
}

public sealed class SqliteMessageOriginTests : MessageOriginContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerMessageOriginTests : MessageOriginContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
