using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IOutcomeStore"/>, and the link writer behind it, as each provider must keep them. The
/// existing tests reach the store through the Host's routes, so each behaviour here is one of theirs
/// asked of the store itself, under its name where it maps one to one (<c>OutcomeTests</c>,
/// <c>OutcomeAuditTests</c>, <c>OutcomeVerificationTests</c>, <c>OutcomeDashboardTests</c>); the rest
/// are the interface's remarks. Then Review Focus 2's two writers at once: concurrent creates of one
/// name, and concurrent unlinks of one workflow.
/// </summary>
public abstract class OutcomeStoreContract : ProviderContract
{
    private const string Email = "person@example.test";
    private const string At = "2026-10-01T00:00:00.0000000+00:00";

    private static readonly OutcomeActor Person = new(Email, OutcomeActorKind.Person);
    private static readonly OutcomeActor Manager = new("Alpha/Manager", OutcomeActorKind.Member);

    protected async Task<IOutcomeStore> CreateAsync() => (await StoresAsync()).Outcomes;

    [Fact]
    public async Task A_name_is_unique_among_live_outcomes_and_a_retired_name_may_be_used_again()
    {
        var outcomes = await CreateAsync();

        var first = await outcomes.CreateAsync("Qualified  pipeline", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct);
        Assert.Equal((true, 201, "Qualified pipeline", OutcomeStatus.Active), (first.Created, first.Status, first.Outcome!.Name, first.Outcome.Status));
        Assert.Equal(Email, first.Outcome.ConfirmedBy);

        var taken = await outcomes.CreateAsync(" qualified PIPELINE ", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct);
        Assert.Equal(409, taken.Status);
        Assert.Contains(first.Outcome.Id, taken.Refusal);

        Assert.Equal(first.Outcome.Id, (await outcomes.FindLiveByNameAsync("QUALIFIED pipeline", Ct))?.Id);
        Assert.Equal(first.Outcome.Id, (await outcomes.ResolveLiveAsync(first.Outcome.Id, Ct))?.Id);
        Assert.Equal(first.Outcome.Id, (await outcomes.ResolveLiveAsync(" Qualified pipeline ", Ct))?.Id);
        Assert.Null(await outcomes.ResolveLiveAsync("qualified pipeline", Ct));

        Assert.True((await outcomes.RetireAsync(first.Outcome.Id, Audit(TenantActions.OutcomeRetired), Ct)).Ok);
        Assert.Null(await outcomes.FindLiveByNameAsync("Qualified pipeline", Ct));

        var again = await outcomes.CreateAsync("Qualified pipeline", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct);
        Assert.Equal(201, again.Status);
        Assert.NotEqual(first.Outcome.Id, again.Outcome!.Id);

        // A retired name taken again cannot come back while the new one is live.
        var back = await outcomes.ReactivateAsync(first.Outcome.Id, Audit(TenantActions.OutcomeReactivated), Ct);
        Assert.Equal(409, back.Status);

        Assert.Equal(
            [(first.Outcome.Id, OutcomeStatus.Retired), (again.Outcome.Id, OutcomeStatus.Active)],
            (await outcomes.ListAsync(Ct)).Select(o => (o.Id, o.Status)));
    }

    [Fact]
    public async Task Proposing_an_existing_name_links_the_existing_outcome_and_creates_none()
    {
        var outcomes = await CreateAsync();
        var existing = (await outcomes.CreateAsync("Pipeline", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;

        var proposed = await outcomes.ProposeAsync("PIPELINE", "ignored", Manager, 42, "Alpha", Audit(TenantActions.OutcomeCreated, Manager), Ct);

        Assert.Equal((false, 200, existing.Id), (proposed.Created, proposed.Status, proposed.Outcome!.Id));
        Assert.Equal((42L, existing.Id, OutcomeLinkHow.Manager, "Alpha/Manager", OutcomeActorKind.Member, "Alpha", "Alpha", "Pipeline"),
            (proposed.Link!.Correlation, proposed.Link.OutcomeId, proposed.Link.How, proposed.Link.SetBy, proposed.Link.SetByKind,
             proposed.Link.TeamId, proposed.Link.TeamNameAtLink, proposed.Link.OutcomeNameAtLink));
        Assert.Single(await outcomes.ListAsync(Ct));

        // A new name is a proposal, made live at once, and linked.
        var fresh = await outcomes.ProposeAsync("Fresh", "Why", Manager, 43, "Alpha", Audit(TenantActions.OutcomeCreated, Manager), Ct);
        Assert.Equal((true, 201, OutcomeStatus.Proposed, "Why"), (fresh.Created, fresh.Status, fresh.Outcome!.Status, fresh.Outcome.Description));
        Assert.Null(fresh.Outcome.ConfirmedBy);
        Assert.Equal(fresh.Outcome.Id, (await outcomes.CurrentLinkAsync(43, Ct))?.OutcomeId);
    }

    [Fact]
    public async Task A_rename_keeps_every_link_and_its_name_snapshot()
    {
        var outcomes = await CreateAsync();
        var outcome = (await outcomes.CreateAsync("Before", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        await outcomes.LinkAsync(42, outcome.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);

        var renamed = await outcomes.EditAsync(
            outcome.Id, new OutcomeEdit(Name: "After", Description: "  why  ", Value: "1,250.5"),
            Audit(TenantActions.OutcomeRenamed), Audit(TenantActions.OutcomeChanged), Ct);

        Assert.Equal(("After", "why", "1250.5"), (renamed.Outcome!.Name, renamed.Outcome.Description, renamed.Outcome.Value));
        Assert.Equal("Before", Assert.Single(await outcomes.ReadLinksAsync(Ct)).OutcomeNameAtLink);
        Assert.Equal(new WorkflowOutcome(outcome.Id, "After", OutcomeStatus.Active), (await outcomes.CurrentOutcomesAsync([42], Ct))[42]);

        // An edit that changes nothing writes nothing; a value that is not one is refused.
        Assert.Equal(renamed.Outcome, (await outcomes.EditAsync(outcome.Id, new OutcomeEdit(Name: "After"), Audit(TenantActions.OutcomeRenamed), Audit(TenantActions.OutcomeChanged), Ct)).Outcome);
        Assert.Equal(400, (await outcomes.EditAsync(outcome.Id, new OutcomeEdit(Value: "-3"), Audit(TenantActions.OutcomeRenamed), Audit(TenantActions.OutcomeChanged), Ct)).Status);
        Assert.Equal(
            [TenantActions.OutcomeCreated, TenantActions.OutcomeRenamed],
            (await outcomes.ReadEventsAsync([outcome.Id], Ct)).Select(e => e.Action));
    }

    [Fact]
    public async Task A_merge_moves_the_figures_without_rewriting_a_link_and_a_chain_ends_at_the_last_outcome()
    {
        var outcomes = await CreateAsync();
        var a = (await outcomes.CreateAsync("A", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var b = (await outcomes.CreateAsync("B", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var c = (await outcomes.CreateAsync("C", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        await outcomes.LinkAsync(42, a.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);
        await outcomes.LinkAsync(43, b.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);

        var merged = await outcomes.MergeAsync(a.Id, b.Id, Audit(TenantActions.OutcomeMerged), Ct);
        Assert.Equal((OutcomeStatus.Merged, b.Id), (merged.Outcome!.Status, merged.Outcome.MergedInto));
        Assert.Equal(409, (await outcomes.MergeAsync(a.Id, c.Id, Audit(TenantActions.OutcomeMerged), Ct)).Status);
        Assert.Equal(409, (await outcomes.MergeAsync(c.Id, a.Id, Audit(TenantActions.OutcomeMerged), Ct)).Status);
        Assert.Equal(409, (await outcomes.MergeAsync(c.Id, c.Id, Audit(TenantActions.OutcomeMerged), Ct)).Status);

        Assert.True((await outcomes.MergeAsync(b.Id, c.Id, Audit(TenantActions.OutcomeMerged), Ct)).Ok);

        var now = await outcomes.CurrentOutcomesAsync([42, 43, 44], Ct);
        Assert.Equal(new WorkflowOutcome(c.Id, "C", OutcomeStatus.Active), now[42]);
        Assert.Equal(new WorkflowOutcome(c.Id, "C", OutcomeStatus.Active), now[43]);
        Assert.False(now.ContainsKey(44));
        Assert.Equal([a.Id, b.Id], (await outcomes.ReadLinksAsync(Ct)).Select(l => l.OutcomeId));

        // A link asked for a merged outcome lands on the one it was merged into.
        var link = await outcomes.LinkAsync(45, a.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);
        Assert.Equal(c.Id, link.Link!.OutcomeId);
    }

    [Fact]
    public async Task Rejecting_a_proposed_outcome_is_refused_while_a_link_names_it()
    {
        var outcomes = await CreateAsync();
        var linked = (await outcomes.ProposeAsync("Linked", null, Manager, 42, "Alpha", Audit(TenantActions.OutcomeCreated, Manager), Ct)).Outcome!;
        var loose = (await outcomes.ProposeAsync("Loose", null, Manager, null, null, Audit(TenantActions.OutcomeCreated, Manager), Ct)).Outcome!;
        var active = (await outcomes.CreateAsync("Active", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;

        var refused = await outcomes.RejectAsync(linked.Id, Audit(TenantActions.OutcomeRejected), Ct);
        Assert.Equal(409, refused.Status);
        Assert.Contains("linked to 1 workflow", refused.Refusal);
        Assert.Equal(409, (await outcomes.RejectAsync(active.Id, Audit(TenantActions.OutcomeRejected), Ct)).Status);
        Assert.Equal(404, (await outcomes.RejectAsync("no-such", Audit(TenantActions.OutcomeRejected), Ct)).Status);

        Assert.True((await outcomes.RejectAsync(loose.Id, Audit(TenantActions.OutcomeRejected), Ct)).Ok);
        Assert.Null(await outcomes.FindAsync(loose.Id, Ct));
        Assert.NotNull(await outcomes.FindAsync(linked.Id, Ct));
    }

    [Fact]
    public async Task Only_a_proposed_outcome_is_confirmed_and_a_confirmed_proposal_records_who()
    {
        var outcomes = await CreateAsync();
        var proposed = (await outcomes.ProposeAsync("Proposal", null, Manager, null, null, Audit(TenantActions.OutcomeCreated, Manager), Ct)).Outcome!;

        var confirmed = await outcomes.ConfirmAsync(proposed.Id, Person, Audit(TenantActions.OutcomeConfirmed), Ct);
        Assert.Equal((OutcomeStatus.Active, Email), (confirmed.Outcome!.Status, confirmed.Outcome.ConfirmedBy));
        Assert.NotNull(confirmed.Outcome.ConfirmedAt);

        Assert.Equal(409, (await outcomes.ConfirmAsync(proposed.Id, Person, Audit(TenantActions.OutcomeConfirmed), Ct)).Status);
        Assert.Equal(404, (await outcomes.ConfirmAsync("no-such", Person, Audit(TenantActions.OutcomeConfirmed), Ct)).Status);
    }

    [Fact]
    public async Task None_appends_an_unlink_with_its_tenant_row_and_a_second_is_refused()
    {
        var outcomes = await CreateAsync();
        var stores = await StoresAsync();
        var outcome = (await outcomes.CreateAsync("Served", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        await outcomes.LinkAsync(42, outcome.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);

        var none = await outcomes.UnlinkAsync(42, "Alpha", Person, Audit(TenantActions.WorkflowOutcomeChanged), Ct);
        Assert.True(none.Ok);
        Assert.True(none.Link!.IsUnlink);
        Assert.True((await outcomes.CurrentLinkAsync(42, Ct))!.IsUnlink);
        Assert.Empty(await outcomes.CurrentOutcomesAsync([42], Ct));

        var detail = JsonDocument.Parse((await stores.TextAsync(
            $"SELECT detail FROM tenant_events WHERE action = '{TenantActions.WorkflowOutcomeChanged}' AND subject = '{outcome.Id}' ORDER BY seq DESC"))!).RootElement;
        Assert.Equal((outcome.Id, JsonValueKind.Null, 42L), (detail.GetProperty("from").GetString(), detail.GetProperty("to").ValueKind, detail.GetProperty("workflow").GetInt64()));

        Assert.Equal(409, (await outcomes.UnlinkAsync(42, "Alpha", Person, Audit(TenantActions.WorkflowOutcomeChanged), Ct)).Status);
        Assert.Equal(409, (await outcomes.UnlinkAsync(99, "Alpha", Person, Audit(TenantActions.WorkflowOutcomeChanged), Ct)).Status);
        Assert.Equal(2, (await outcomes.ReadLinksAsync(Ct)).Count);
    }

    [Fact]
    public async Task A_managers_set_is_refused_on_a_persons_link_and_allowed_on_an_agents()
    {
        var outcomes = await CreateAsync();
        var a = (await outcomes.CreateAsync("A", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var b = (await outcomes.CreateAsync("B", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;

        await outcomes.LinkAsync(42, a.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);
        var refused = await outcomes.LinkAsync(42, b.Id, "Alpha", Manager, OutcomeLinkHow.Manager, agentRule: true, Audit(TenantActions.WorkflowOutcomeChanged, Manager), Ct);
        Assert.Equal(409, refused.Status);
        Assert.Equal(409, (await outcomes.ProposeAsync("C", null, Manager, 42, "Alpha", Audit(TenantActions.OutcomeCreated, Manager), Ct)).Status);
        Assert.Null(await outcomes.FindLiveByNameAsync("C", Ct));

        await outcomes.LinkAsync(43, a.Id, "Alpha", Manager, OutcomeLinkHow.Manager, agentRule: true, Audit(TenantActions.WorkflowOutcomeChanged, Manager), Ct);
        Assert.True((await outcomes.LinkAsync(43, b.Id, "Alpha", Manager, OutcomeLinkHow.Manager, agentRule: true, Audit(TenantActions.WorkflowOutcomeChanged, Manager), Ct)).Ok);
        Assert.Equal(b.Id, (await outcomes.CurrentLinkAsync(43, Ct))?.OutcomeId);

        // A retired outcome is linked to nothing.
        await outcomes.RetireAsync(a.Id, Audit(TenantActions.OutcomeRetired), Ct);
        Assert.Equal(409, (await outcomes.LinkAsync(44, a.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct)).Status);
        Assert.Equal(404, (await outcomes.LinkAsync(44, "no-such", "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct)).Status);
    }

    [Fact]
    public async Task A_link_row_is_never_updated_or_deleted()
    {
        var outcomes = await CreateAsync();
        var stores = await StoresAsync();
        var a = (await outcomes.CreateAsync("A", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        await outcomes.LinkAsync(42, a.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);

        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("UPDATE workflow_outcome_links SET how = 'manager'"));
        await Assert.ThrowsAnyAsync<DbException>(() => stores.ExecuteAsync("DELETE FROM workflow_outcome_links"));
        Assert.Equal(OutcomeLinkHow.Person, Assert.Single(await outcomes.ReadLinksAsync(Ct)).How);
    }

    [Fact]
    public async Task Every_persons_write_is_refused_and_changes_nothing_when_its_tenant_row_cannot_be_written()
    {
        var outcomes = await CreateAsync();
        var stores = await StoresAsync();

        var active = (await outcomes.CreateAsync("Active outcome", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var into = (await outcomes.CreateAsync("Into outcome", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var retired = (await outcomes.CreateAsync("Retired outcome", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        await outcomes.RetireAsync(retired.Id, Audit(TenantActions.OutcomeRetired), Ct);
        var proposed = (await outcomes.ProposeAsync("Proposed outcome", null, Manager, null, null, Audit(TenantActions.OutcomeCreated, Manager), Ct)).Outcome!;
        await outcomes.LinkAsync(42, active.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);

        var before = await outcomes.ListAsync(Ct);
        var linksBefore = await outcomes.ReadLinksAsync(Ct);

        await stores.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(() => outcomes.CreateAsync("New outcome", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => outcomes.EditAsync(active.Id, new OutcomeEdit(Name: "Renamed"), Audit(TenantActions.OutcomeRenamed), Audit(TenantActions.OutcomeChanged), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => outcomes.ConfirmAsync(proposed.Id, Person, Audit(TenantActions.OutcomeConfirmed), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => outcomes.RetireAsync(active.Id, Audit(TenantActions.OutcomeRetired), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => outcomes.ReactivateAsync(retired.Id, Audit(TenantActions.OutcomeReactivated), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => outcomes.MergeAsync(active.Id, into.Id, Audit(TenantActions.OutcomeMerged), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => outcomes.RejectAsync(proposed.Id, Audit(TenantActions.OutcomeRejected), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => outcomes.LinkAsync(43, active.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => outcomes.UnlinkAsync(42, "Alpha", Person, Audit(TenantActions.WorkflowOutcomeChanged), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => outcomes.ProposeAsync("Another", null, Manager, 44, "Alpha", Audit(TenantActions.OutcomeCreated, Manager), Ct));

        Assert.Equal(before, await outcomes.ListAsync(Ct));
        Assert.Equal(linksBefore, await outcomes.ReadLinksAsync(Ct));
    }

    [Fact]
    public async Task A_dispatched_item_with_no_outcome_takes_the_outcome_its_workflow_is_linked_to_with_a_tenant_row()
    {
        var outcomes = await CreateAsync();
        var stores = await StoresAsync();
        var a = (await outcomes.CreateAsync("A", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var b = (await outcomes.CreateAsync("B", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        await ItemAsync(stores, "item-1", null);
        await DispatchAsync(stores, "item-1", 42);

        await outcomes.LinkAsync(42, a.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);

        var item = Assert.Single(await outcomes.ReadBacklogAsync(Ct));
        Assert.Equal(("item-1", a.Id, 42L, "Alpha", false), (item.Id, item.OutcomeId, item.Correlation!.Value, item.TeamName, item.Archived));
        Assert.Equal(1, await stores.LongAsync(
            $"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.BacklogItemOutcomeInherited}' AND subject = 'item-1' AND actor_email = '{Email}'"));

        // NEVER OVERWRITTEN by a later link, and never cleared by an unlink.
        await outcomes.LinkAsync(42, b.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);
        await outcomes.UnlinkAsync(42, "Alpha", Person, Audit(TenantActions.WorkflowOutcomeChanged), Ct);
        Assert.Equal(a.Id, Assert.Single(await outcomes.ReadBacklogAsync(Ct)).OutcomeId);
    }

    [Fact]
    public async Task A_backlog_dispatch_links_its_workflow_to_the_items_outcome_attributed_to_the_dispatcher()
    {
        var outcomes = await CreateAsync();
        var stores = await StoresAsync();
        var a = (await outcomes.CreateAsync("A", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        await ItemAsync(stores, PlatformBacklogId.Format(7), a.Id);
        await ItemAsync(stores, PlatformBacklogId.Format(8), null);

        var dispatched = await stores.Log.AppendAsync(new NewMessage(
            MessageTypes.BacklogItemDispatched, JsonSerializer.Serialize(new { item = 7, team = "Alpha" }), Email), Ct);
        var plain = await stores.Log.AppendAsync(new NewMessage(
            MessageTypes.BacklogItemDispatched, JsonSerializer.Serialize(new { item = 8, team = "Alpha" }), Email), Ct);

        var link = await outcomes.CurrentLinkAsync(dispatched.CorrelationId, Ct);
        Assert.NotNull(link);
        Assert.Equal((a.Id, OutcomeLinkHow.Dispatch, Email, OutcomeActorKind.Person, "Alpha", "A"),
            (link.OutcomeId, link.How, link.SetBy, link.SetByKind, link.TeamId, link.OutcomeNameAtLink));
        Assert.Null(await outcomes.CurrentLinkAsync(plain.CorrelationId, Ct));
    }

    [Fact]
    public async Task A_trigger_from_before_the_snapshot_resolves_its_creator_and_falls_back_to_the_id_only_when_they_are_gone()
    {
        var outcomes = await CreateAsync();
        var stores = await StoresAsync();
        var a = (await outcomes.CreateAsync("A", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        await stores.ExecuteAsync($"INSERT INTO teams (id, name, created_utc) VALUES ('Alpha', 'Alpha', '{At}')");
        await stores.ExecuteAsync($"INSERT INTO users (id, email, password_hash, created_at) VALUES ('user-1', 'creator@example.test', 'x', '{At}')");
        await TriggerAsync(stores, "t1", a.Id, "user-1", "configurer@example.test");
        await TriggerAsync(stores, "t2", a.Id, "user-1", null);
        await TriggerAsync(stores, "t3", a.Id, "user-gone", null);
        await TriggerAsync(stores, "t4", null, "user-1", null);

        async Task<OutcomeLink?> FireAsync(string source) =>
            await outcomes.CurrentLinkAsync((await stores.Log.AppendAsync(new NewMessage(
                MessageTypes.InstructionFor(new ContainerId("Alpha", "Dev")), """{"instruction":"poll"}""", source), Ct)).CorrelationId, Ct);

        var configured = await FireAsync("schedule:t1");
        Assert.Equal((a.Id, OutcomeLinkHow.Trigger, "configurer@example.test", OutcomeActorKind.Person),
            (configured!.OutcomeId, configured.How, configured.SetBy, configured.SetByKind));
        Assert.Equal("creator@example.test", (await FireAsync("trigger:t2"))!.SetBy);
        Assert.Equal("user-gone", (await FireAsync("schedule:t3"))!.SetBy);
        Assert.Null(await FireAsync("schedule:t4"));

        // A FIRE JOINING A WORKFLOW THAT HAS ONE leaves it as it is.
        var b = (await outcomes.CreateAsync("B", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var root = await stores.Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(new ContainerId("Alpha", "Dev")), "{}", "console"), Ct);
        await outcomes.LinkAsync(root.CorrelationId, b.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);
        await stores.Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(new ContainerId("Alpha", "Dev")), "{}", "trigger:t1", root.Seq), Ct);
        Assert.Equal(b.Id, (await outcomes.CurrentLinkAsync(root.CorrelationId, Ct))!.OutcomeId);
    }

    [Fact]
    public async Task An_outcome_id_is_a_guid_and_a_workflows_close_records_the_outcome_it_served()
    {
        var outcomes = await CreateAsync();
        var stores = await StoresAsync();
        var a = (await outcomes.CreateAsync("A", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        Assert.True(Guid.TryParse(a.Id, out _));

        var root = await stores.Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(new ContainerId("Alpha", "Dev")), "{}", "console"), Ct);
        await outcomes.LinkAsync(root.CorrelationId, a.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);
        await stores.Log.AppendAsync(new NewMessage(MessageTypes.WorkflowCompleted, "{}", "Alpha/Manager", root.Seq), Ct);

        Assert.Equal(a.Id, (await stores.UsageLedger.ReadWorkflowAsync(root.CorrelationId, Ct))!.OutcomeIdAtClose);

        var ledger = await outcomes.ReadLedgerAsync(null, null, Ct);
        Assert.Equal(a.Id, Assert.Single(ledger.CurrentLinks).OutcomeId);
        Assert.Equal(a.Id, Assert.Single(ledger.Closes).OutcomeIdAtClose);
        Assert.Empty(ledger.Runs);
    }

    [Fact]
    public async Task The_detail_lists_its_renames_status_changes_and_merges_with_who_and_when()
    {
        var outcomes = await CreateAsync();
        var a = (await outcomes.CreateAsync("A", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var b = (await outcomes.CreateAsync("B", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        await outcomes.EditAsync(a.Id, new OutcomeEdit(Name: "A2"), Audit(TenantActions.OutcomeRenamed), Audit(TenantActions.OutcomeChanged), Ct);
        await outcomes.EditAsync(a.Id, new OutcomeEdit(Description: "now described"), Audit(TenantActions.OutcomeRenamed), Audit(TenantActions.OutcomeChanged), Ct);
        await outcomes.RetireAsync(a.Id, Audit(TenantActions.OutcomeRetired), Ct);
        await outcomes.ReactivateAsync(a.Id, Audit(TenantActions.OutcomeReactivated), Ct);
        await outcomes.MergeAsync(a.Id, b.Id, Audit(TenantActions.OutcomeMerged), Ct);

        var events = await outcomes.ReadEventsAsync([a.Id], Ct);
        Assert.Equal(
            [TenantActions.OutcomeCreated, TenantActions.OutcomeRenamed, TenantActions.OutcomeChanged, TenantActions.OutcomeRetired,
             TenantActions.OutcomeReactivated, TenantActions.OutcomeMerged],
            events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal((Email, a.Id), (e.ActorEmail, e.Subject)));
        Assert.Equal(["A", "A2", "A2", "A2", "A2", "A2"], events.Select(e => e.SubjectName));
        Assert.Empty(await outcomes.ReadEventsAsync([], Ct));
    }

    /// <summary>
    /// A TELL NAMING AN OUTCOME (<see cref="IOutcomeLinkRows"/>, inside the log's append): the root, its
    /// link and its tenant row land together, and when the tenant row cannot be written none does.
    /// </summary>
    [Fact]
    public async Task A_persons_tell_that_names_an_outcome_appends_its_workflow_outcome_changed_row()
    {
        var outcomes = await CreateAsync();
        var stores = await StoresAsync();
        var a = (await outcomes.CreateAsync("A", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var told = new NewMessage(MessageTypes.InstructionFor(new ContainerId("Alpha", "Manager")), """{"instruction":"look"}""", Email);

        Task<Message?> TellAsync() => stores.Log.AppendWithinAsync(
            (_, _, _) => Task.FromResult<NewMessage?>(told),
            (connection, transaction, stored, token) => stores.OutcomeLinkRows.WriteWithinAsync(
                connection, transaction, stored.CorrelationId, a.Id, "Alpha", Email, OutcomeActorKind.Person, OutcomeLinkHow.Tell,
                new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, a.Id, a.Name, "{}"), token),
            Ct);

        var root = (await TellAsync())!;
        var link = await outcomes.CurrentLinkAsync(root.CorrelationId, Ct);
        Assert.Equal((a.Id, OutcomeLinkHow.Tell, Email), (link!.OutcomeId, link.How, link.SetBy));
        Assert.Equal(1, await stores.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.WorkflowOutcomeChanged}' AND subject = '{a.Id}'"));

        var head = await stores.Log.HighestSeqAsync(Ct);
        await stores.ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(TellAsync);
        Assert.Equal(head, await stores.Log.HighestSeqAsync(Ct));
        Assert.Single(await outcomes.ReadLinksAsync(Ct));
    }

    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var outcomes = await CreateAsync();
        var name = "Pipeline 🚀 d'été 測試";
        var description = string.Concat(Enumerable.Repeat("line 'quoted' 😀 éè ", 12_000));

        var created = (await outcomes.CreateAsync(name, new OutcomeEdit(Description: description), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var found = await outcomes.FindAsync(created.Id, Ct);

        Assert.True(description.Trim().Length > 200_000);
        Assert.Equal(name, found!.Name);
        Assert.Equal(description.Trim(), found.Description);
        Assert.Equal(created.Id, (await outcomes.FindLiveByNameAsync(name.ToUpperInvariant(), Ct))?.Id);
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight people create one name at once. One is created and the rest are told it is
    /// taken - none fails on the unique index, because each create's read of the name holds it.
    /// </summary>
    [Fact]
    public async Task Concurrent_creates_of_one_name_make_one_outcome()
    {
        var outcomes = await CreateAsync();

        for (var round = 0; round < 3; round++)
        {
            var writes = await RaceAsync(8, () =>
                outcomes.CreateAsync($"Raced {round}", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct));

            Assert.Equal(1, writes.Count(w => w.Status == 201));
            Assert.Equal(7, writes.Count(w => w.Status == 409));
        }

        Assert.Equal(3, (await outcomes.ListAsync(Ct)).Count);
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight people choose None for one workflow at once. One unlink is appended and the
    /// rest are refused - each reads the newest link and holds it, so none decides from a link another
    /// has already left.
    /// </summary>
    [Fact]
    public async Task Concurrent_unlinks_of_one_workflow_append_one_row()
    {
        var outcomes = await CreateAsync();
        var a = (await outcomes.CreateAsync("A", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;

        for (var workflow = 42L; workflow < 45; workflow++)
        {
            await outcomes.LinkAsync(workflow, a.Id, "Alpha", Person, OutcomeLinkHow.Person, agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);

            var asked = workflow;
            var writes = await RaceAsync(8, () => outcomes.UnlinkAsync(asked, "Alpha", Person, Audit(TenantActions.WorkflowOutcomeChanged), Ct));

            Assert.Equal(1, writes.Count(w => w.Ok));
            Assert.Equal(7, writes.Count(w => w.Status == 409));
        }

        Assert.Equal(6, (await outcomes.ReadLinksAsync(Ct)).Count);
    }

    /// <summary>
    /// REVIEW FOCUS 2: A TRIGGER FIRES INTO A WORKFLOW WHILE A PERSON LINKS IT. A root link never
    /// replaces one, so whichever lands first the person's link is the workflow's newest: the fire's
    /// append reads the workflow's newest link and holds it, and the person's link holds it too.
    /// The outcome store takes no append lock, so nothing else orders the two.
    /// </summary>
    [Fact]
    public async Task A_trigger_fire_racing_a_persons_link_never_replaces_it()
    {
        var outcomes = await CreateAsync();
        var stores = await StoresAsync();
        var persons = (await outcomes.CreateAsync("Person's", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        var triggers = (await outcomes.CreateAsync("Trigger's", new OutcomeEdit(), Person, Audit(TenantActions.OutcomeCreated), Ct)).Outcome!;
        await stores.ExecuteAsync($"INSERT INTO teams (id, name, created_utc) VALUES ('Alpha', 'Alpha', '{At}')");
        await TriggerAsync(stores, "t1", triggers.Id, "user-1", "configurer@example.test");
        var dev = new ContainerId("Alpha", "Dev");

        for (var round = 0; round < 25; round++)
        {
            var root = await stores.Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(dev), "{}", "console"), Ct);

            using var start = new ManualResetEventSlim();
            var link = Task.Run(async () =>
            {
                start.Wait(Ct);
                return await outcomes.LinkAsync(root.CorrelationId, persons.Id, "Alpha", Person, OutcomeLinkHow.Person,
                    agentRule: false, Audit(TenantActions.WorkflowOutcomeChanged), Ct);
            }, Ct);
            var fire = Task.Run(async () =>
            {
                start.Wait(Ct);
                return await stores.Log.AppendAsync(new NewMessage(MessageTypes.InstructionFor(dev), "{}", "trigger:t1", root.Seq), Ct);
            }, Ct);

            start.Set();
            Assert.True((await link).Ok);
            await fire;

            Assert.Equal(persons.Id, (await outcomes.CurrentLinkAsync(root.CorrelationId, Ct))!.OutcomeId);
        }
    }

    private static async Task<OutcomeWrite[]> RaceAsync(int writers, Func<Task<OutcomeWrite>> write)
    {
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
        {
            start.Wait(Ct);
            return await write();
        }, Ct)).ToList();

        start.Set();
        return await Task.WhenAll(tasks);
    }

    private static TriggerAudit Audit(string action, OutcomeActor? actor = null) =>
        actor is { Kind: OutcomeActorKind.Member }
            ? new TriggerAudit(actor.Id, null, action, null, null, null)
            : new TriggerAudit(null, Email, action, null, null, null);

    private static Task ItemAsync(ContractStores stores, string id, string? outcome) =>
        stores.ExecuteAsync(
            $"""
             INSERT INTO backlog_items (id, team, title, body, state, position, created_at, updated_at, created_by, outcome_id)
             VALUES ('{id}', 'Alpha', 'Title {id}', 'Body', 'ready', 1, '{At}', '{At}', '{Email}', {(outcome is null ? "NULL" : $"'{outcome}'")})
             """);

    private static Task DispatchAsync(ContractStores stores, string item, long correlation) =>
        stores.ExecuteAsync(
            $"""
             INSERT INTO backlog_dispatches (item, team_id, team_name, correlation, dispatched_at, dispatched_by)
             VALUES ('{item}', 'Alpha', 'Alpha', {correlation}, '{At}', '{Email}')
             """);

    private static Task TriggerAsync(ContractStores stores, string id, string? outcome, string createdBy, string? configuredBy) =>
        stores.ExecuteAsync(
            $"""
             INSERT INTO triggers (id, team, container, name, instruction, kind, idle_only, enabled, missed_count,
                 created_at, created_by, outcome_id, configured_by_email)
             VALUES ('{id}', 'Alpha', 'Dev', '{id}', 'poll', 'interval', 0, 1, 0, '{At}', '{createdBy}',
                 {(outcome is null ? "NULL" : $"'{outcome}'")}, {(configuredBy is null ? "NULL" : $"'{configuredBy}'")})
             """);
}

public sealed class SqliteOutcomeStoreTests : OutcomeStoreContract
{
    protected override Task<ContractStores> OpenAsync() => Task.FromResult<ContractStores>(new SqliteContractStores());
}

public sealed class SqlServerOutcomeStoreTests : OutcomeStoreContract
{
    protected override async Task<ContractStores> OpenAsync() => await SqlServerContractStores.CreateAsync();
}
