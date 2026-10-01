using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// OUTCOMES, on the real Host: links made at the root of a workflow (a dispatch, a trigger's
/// fire, a <c>tell</c>), the <c>outcome</c> tool's rule that a Manager never overrides a link a person
/// caused, the name rule, a rename and a merge that never rewrite a link, a reject refused while links
/// exist, the figures read from the ledger, and who may do what.
/// </summary>
public sealed class OutcomeTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Email = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOutcomeStore Outcomes => host.Services.GetRequiredService<IOutcomeStore>();

    private IMessageLog Log => host.Services.GetRequiredService<IMessageLog>();

    private string Database => Path.Combine(host.DataRoot, "messages.db");

    private static string Unique(string stem) => $"{stem} {Guid.NewGuid():N}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("error").GetString()!;

    private async Task<Outcome> CreateAsync(HttpClient person, string name)
    {
        var created = await person.PostAsJsonAsync("/api/outcomes", new { name }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Outcomes.FindAsync((await JsonAsync(created)).GetProperty("id").GetString()!, Ct))!;
    }

    /// <summary>A workflow on <paramref name="team"/>: a person's tell to its Manager, with no causation.</summary>
    private async Task<long> WorkflowAsync(HttpClient person, string team, string? outcome = null)
    {
        var told = await person.PostAsJsonAsync(
            $"/api/teams/{team}/containers/{TeamRegistry.DefaultManagerName}/tell",
            new { instruction = "look at the openings", outcome }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        return (await JsonAsync(told)).GetProperty("correlationId").GetInt64();
    }

    /// <summary>The tools as <paramref name="id"/> holding <paramref name="permits"/> calls them.</summary>
    private PlatformMcpTools Tools(string id, PrincipalKind kind, string team, IReadOnlySet<string> permits, string? owner = null)
    {
        var key = host.Services.GetRequiredService<IPrincipalStore>()
            .MintAsync(id, kind, team, permits, ownerUserId: owner, ct: Ct).GetAwaiter().GetResult();
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(new Principal(id, kind, permits, owner), "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    private PlatformMcpTools ManagerTools(string team) =>
        Tools(new ContainerId(team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, team, TeamRegistry.ManagerPermits);

    // ---- linking at the root ----

    [Fact]
    public async Task A_tell_without_causation_links_its_new_workflow_to_the_named_outcome_as_the_person()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Maintain a pipeline of openings"));

        var byName = await WorkflowAsync(person, host.Alpha, outcome.Name);
        var byId = await WorkflowAsync(person, host.Alpha, outcome.Id);

        foreach (var workflow in new[] { byName, byId })
        {
            var link = (await Outcomes.CurrentLinkAsync(workflow, Ct))!;
            Assert.Equal((outcome.Id, OutcomeLinkHow.Tell, Email, OutcomeActorKind.Person), (link.OutcomeId, link.How, link.SetBy, link.SetByKind));
            Assert.Equal(host.Alpha, link.TeamId);
        }

        // A tell that joins a workflow names no outcome; an unknown one is refused and nothing is sent.
        var joined = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/containers/Manager/tell",
            new { instruction = "more", causation = byName.ToString(), outcome = outcome.Id }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, joined.StatusCode);
        Assert.Contains("`outcome` tool", await ErrorAsync(joined), StringComparison.Ordinal);

        var before = await Log.HighestSeqAsync(Ct);
        var unknown = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/containers/Manager/tell",
            new { instruction = "more", outcome = "no such outcome" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(before, await Log.HighestSeqAsync(Ct));
    }

    [Fact]
    public async Task A_backlog_dispatch_links_its_workflow_to_the_items_outcome_attributed_to_the_dispatcher()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Qualified openings"));

        var item = (await JsonAsync(await person.PostAsJsonAsync("/api/backlog", new { title = "Crawl openings", body = "spec" }, Ct)))
            .GetProperty("id").GetInt64();
        var edited = await person.PatchAsJsonAsync($"/api/backlog/{item}", new { state = "ready", outcomeId = outcome.Name }, Ct);
        Assert.Equal(outcome.Id, (await JsonAsync(edited)).GetProperty("outcomeId").GetString());

        var dispatched = await person.PostAsync($"/api/teams/{host.Alpha}/backlog/{item}/dispatch", null, Ct);
        Assert.Equal(HttpStatusCode.OK, dispatched.StatusCode);
        var correlation = (await JsonAsync(dispatched)).GetProperty("correlation").GetInt64();

        var link = (await Outcomes.CurrentLinkAsync(correlation, Ct))!;
        Assert.Equal((outcome.Id, OutcomeLinkHow.Dispatch, Email, OutcomeActorKind.Person), (link.OutcomeId, link.How, link.SetBy, link.SetByKind));

        // An item with no outcome links nothing.
        var plain = (await JsonAsync(await person.PostAsJsonAsync("/api/backlog", new { title = "No outcome", body = "spec" }, Ct)))
            .GetProperty("id").GetInt64();
        await person.PatchAsJsonAsync($"/api/backlog/{plain}", new { state = "ready" }, Ct);
        var unlinked = (await JsonAsync(await person.PostAsync($"/api/teams/{host.Alpha}/backlog/{plain}/dispatch", null, Ct)))
            .GetProperty("correlation").GetInt64();
        Assert.Null(await Outcomes.CurrentLinkAsync(unlinked, Ct));
    }

    [Fact]
    public async Task A_schedules_fire_links_the_workflow_it_roots_to_the_triggers_outcome_attributed_to_its_configurer()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Fresh openings"));

        var created = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/triggers", new
        {
            name = Unique("crawl"), kind = "every", intervalSeconds = 3600, instruction = "crawl", idleOnly = false,
            outcomeId = outcome.Name,
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var trigger = await JsonAsync(created);
        Assert.Equal(outcome.Id, trigger.GetProperty("outcomeId").GetString());

        var ran = await JsonAsync(await person.PostAsync($"/api/teams/{host.Alpha}/triggers/{trigger.GetProperty("id").GetString()}/run", null, Ct));
        Assert.Equal("fired", ran.GetProperty("outcome").GetString());
        var fired = (await Log.FindAsync(ran.GetProperty("seq").GetInt64(), Ct))!;

        var link = (await Outcomes.CurrentLinkAsync(fired.CorrelationId, Ct))!;
        Assert.Equal((outcome.Id, OutcomeLinkHow.Trigger, OutcomeActorKind.Person), (link.OutcomeId, link.How, link.SetByKind));
        // SET_BY IS THE CONFIGURING PERSON'S EMAIL, as on dispatch and tell links, not the user id.
        Assert.Equal(Email, link.SetBy);
        Assert.NotEqual(trigger.GetProperty("createdBy").GetString(), link.SetBy);

        // A trigger naming no live outcome is refused.
        var refused = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/triggers", new
        {
            name = Unique("bad"), kind = "every", intervalSeconds = 3600, instruction = "crawl", outcomeId = "nothing",
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    /// <summary>A person other than the fixture's, signed in: their user id, email and client.</summary>
    private async Task<(string Id, string Email, HttpClient Client)> OtherPersonAsync()
    {
        var email = $"configurer-{Guid.NewGuid():N}@example.test";
        var user = await host.Services.GetRequiredService<IUserStore>().CreateAsync(email, HostFixture.Password, Ct);
        var client = host.Anonymous();
        (await client.PostAsJsonAsync("/api/auth/login", new { email, password = HostFixture.Password }, Ct)).EnsureSuccessStatusCode();
        return (user.Id, email, client);
    }

    /// <summary>The link Run now's fire of <paramref name="trigger"/> made.</summary>
    private async Task<OutcomeLink> RunNowLinkAsync(HttpClient person, string trigger)
    {
        var ran = await JsonAsync(await person.PostAsync($"/api/teams/{host.Alpha}/triggers/{trigger}/run", null, Ct));
        Assert.Equal("fired", ran.GetProperty("outcome").GetString());
        var fired = (await Log.FindAsync(ran.GetProperty("seq").GetInt64(), Ct))!;
        return (await Outcomes.CurrentLinkAsync(fired.CorrelationId, Ct))!;
    }

    [Fact]
    public async Task A_trigger_whose_configurer_was_deleted_still_fires_and_links_with_their_email()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Openings after a departure"));
        var (id, email, configurer) = await OtherPersonAsync();

        var created = await configurer.PostAsJsonAsync($"/api/teams/{host.Alpha}/triggers", new
        {
            name = Unique("crawl"), kind = "every", intervalSeconds = 3600, instruction = "crawl", idleOnly = false,
            outcomeId = outcome.Id,
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var trigger = (await JsonAsync(created)).GetProperty("id").GetString()!;

        await host.Services.GetRequiredService<IUserStore>().DeleteAsync(id, Ct);
        Assert.Null(await host.Services.GetRequiredService<IUserStore>().FindByIdAsync(id, Ct));

        // SET_BY STAYS THE PERSON'S EMAIL: the trigger snapshotted it, so no raw user id reaches the link.
        var link = await RunNowLinkAsync(person, trigger);
        Assert.Equal((outcome.Id, OutcomeLinkHow.Trigger, email, OutcomeActorKind.Person), (link.OutcomeId, link.How, link.SetBy, link.SetByKind));
    }

    [Fact]
    public async Task A_persons_change_to_a_trigger_makes_them_its_configurer()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Openings, reconfigured"));
        var (_, email, configurer) = await OtherPersonAsync();

        var created = await configurer.PostAsJsonAsync($"/api/teams/{host.Alpha}/triggers", new
        {
            name = Unique("crawl"), kind = "every", intervalSeconds = 3600, instruction = "crawl", idleOnly = false,
            outcomeId = outcome.Id,
        }, Ct);
        var trigger = (await JsonAsync(created)).GetProperty("id").GetString()!;
        Assert.Equal(email, (await host.Services.GetRequiredService<ITriggerStore>().FindAsync(trigger, Ct))!.ConfiguredByEmail);

        var changed = await person.PatchAsJsonAsync($"/api/teams/{host.Alpha}/triggers/{trigger}", new { instruction = "crawl again" }, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(Email, (await RunNowLinkAsync(person, trigger)).SetBy);
    }

    /// <summary>
    /// A trigger from before <c>auth-018</c> has no snapshot: its <c>created_by</c> resolves through
    /// <c>users</c> as before, and only when that user is gone too does the link keep the raw id.
    /// </summary>
    [Fact]
    public async Task A_trigger_from_before_the_snapshot_resolves_its_creator_and_falls_back_to_the_id_only_when_they_are_gone()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Openings from an old trigger"));
        var users = host.Services.GetRequiredService<IUserStore>();
        var store = host.Services.GetRequiredService<ITriggerStore>();
        var (id, email, _) = await OtherPersonAsync();

        TriggerRow Old(string createdBy) => new(
            Guid.NewGuid().ToString("N"), host.Alpha, TeamRegistry.DefaultManagerName, Unique("old"), "crawl", "every",
            null, null, 3600, null, false, true, null, null, null, null, 0, DateTimeOffset.UtcNow, createdBy)
        {
            OutcomeId = outcome.Id,
        };

        var old = Old(id);
        await store.SaveAsync(old, Ct);
        Assert.Null((await store.FindAsync(old.Id, Ct))!.ConfiguredByEmail);

        Assert.Equal(email, (await RunNowLinkAsync(person, old.Id)).SetBy);

        await users.DeleteAsync(id, Ct);

        var gone = await RunNowLinkAsync(person, old.Id);
        Assert.Equal((id, OutcomeActorKind.Person), (gone.SetBy, gone.SetByKind));
    }

    // ---- the Manager's set, and the rule ----

    [Fact]
    public async Task A_managers_set_links_an_unlinked_workflow_and_is_refused_on_a_dispatch_linked_one_and_on_another_teams()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Pipeline"));
        var tools = ManagerTools(host.Alpha);

        // UNLINKED: set works, as the Manager.
        var workflow = await WorkflowAsync(person, host.Alpha);
        var set = await tools.Outcome("set", outcome: outcome.Name, causation: workflow.ToString(), cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", set, StringComparison.Ordinal);
        var link = (await Outcomes.CurrentLinkAsync(workflow, Ct))!;
        Assert.Equal((OutcomeLinkHow.Manager, $"{host.Alpha}/Manager", OutcomeActorKind.Member), (link.How, link.SetBy, link.SetByKind));

        // A MANAGER MAY MOVE ITS OWN LINK.
        var other = await CreateAsync(person, Unique("Other"));
        Assert.StartsWith("HTTP 200", await tools.Outcome("set", outcome: other.Id, causation: workflow.ToString(), cancellationToken: Ct), StringComparison.Ordinal);

        // DISPATCH-LINKED: refused with a sentence, and the link stands.
        var item = (await JsonAsync(await person.PostAsJsonAsync("/api/backlog", new { title = "t", body = "b" }, Ct))).GetProperty("id").GetInt64();
        await person.PatchAsJsonAsync($"/api/backlog/{item}", new { state = "ready", outcomeId = outcome.Id }, Ct);
        var dispatched = (await JsonAsync(await person.PostAsync($"/api/teams/{host.Alpha}/backlog/{item}/dispatch", null, Ct)))
            .GetProperty("correlation").GetInt64();

        var refused = await tools.Outcome("set", outcome: other.Id, causation: dispatched.ToString(), cancellationToken: Ct);
        Assert.StartsWith("Refused: the outcome tool's set: ", refused, StringComparison.Ordinal);
        Assert.Contains("only a person moves it", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/", refused, StringComparison.Ordinal);
        Assert.Equal(outcome.Id, (await Outcomes.CurrentLinkAsync(dispatched, Ct))!.OutcomeId);

        // A PERSON ALWAYS MAY.
        var moved = await person.PutAsJsonAsync($"/api/teams/{host.Alpha}/workflows/{dispatched}/outcome", new { outcome = other.Id }, Ct);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal((other.Id, OutcomeLinkHow.Person), ((await Outcomes.CurrentLinkAsync(dispatched, Ct))!.OutcomeId, (await Outcomes.CurrentLinkAsync(dispatched, Ct))!.How));

        // ...and a person's own choice is not the Manager's to move either.
        Assert.StartsWith("Refused:", await tools.Outcome("set", outcome: outcome.Id, causation: dispatched.ToString(), cancellationToken: Ct), StringComparison.Ordinal);

        // ANOTHER TEAM'S WORKFLOW: refused, nothing linked.
        var beta = await WorkflowAsync(person, host.Beta);
        var foreign = await tools.Outcome("set", outcome: outcome.Id, causation: beta.ToString(), cancellationToken: Ct);
        Assert.StartsWith("Refused: the outcome tool's set: ", foreign, StringComparison.Ordinal);
        Assert.Contains("own team's workflows", foreign, StringComparison.Ordinal);
        Assert.Null(await Outcomes.CurrentLinkAsync(beta, Ct));

        // ...and through the route with the Manager's key on Beta's path, TeamGate answers as for a missing team.
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, "Manager").ToString(), PrincipalKind.Container, host.Alpha, TeamRegistry.ManagerPermits, ct: Ct);
        using var manager = host.Container(key);
        var gated = await manager.PutAsJsonAsync($"/api/teams/{host.Beta}/workflows/{beta}/outcome", new { outcome = outcome.Id }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, gated.StatusCode);
        Assert.Null(await Outcomes.CurrentLinkAsync(beta, Ct));
    }

    [Fact]
    public async Task A_managers_set_is_refused_on_a_persons_own_tell_link_and_allowed_on_the_concierges()
    {
        var person = await host.PersonAsync();
        var chosen = await CreateAsync(person, Unique("Chosen"));
        var other = await CreateAsync(person, Unique("Manager's pick"));
        var tools = ManagerTools(host.Alpha);

        // A PERSON'S OWN TELL: the person caused it, so the Manager is refused and the link stands.
        var told = await WorkflowAsync(person, host.Alpha, chosen.Id);
        Assert.Equal((OutcomeLinkHow.Tell, OutcomeActorKind.Person),
            ((await Outcomes.CurrentLinkAsync(told, Ct))!.How, (await Outcomes.CurrentLinkAsync(told, Ct))!.SetByKind));

        var refused = await tools.Outcome("set", outcome: other.Id, causation: told.ToString(), cancellationToken: Ct);
        Assert.StartsWith("Refused: the outcome tool's set: ", refused, StringComparison.Ordinal);
        Assert.Contains("only a person moves it", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("http", refused, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(chosen.Id, (await Outcomes.CurrentLinkAsync(told, Ct))!.OutcomeId);

        // ...and a propose on it is refused too, creating nothing.
        var name = Unique("Never created");
        Assert.StartsWith("Refused: the outcome tool's propose: ",
            await tools.Outcome("propose", name: name, causation: told.ToString(), cancellationToken: Ct), StringComparison.Ordinal);
        Assert.Null(await Outcomes.FindLiveByNameAsync(name, Ct));

        // THE CONCIERGE'S TELL: an agent made it, so the Manager may move it.
        var owner = await host.Services.GetRequiredService<IUserStore>().FindAsync(Email, Ct);
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(owner!.Id), PrincipalKind.TenantConcierge, host.Alpha,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: owner.Id, ct: Ct);
        using var concierge = host.Container(key);
        var byConcierge = await WorkflowAsync(concierge, host.Alpha, chosen.Id);
        var agentTell = (await Outcomes.CurrentLinkAsync(byConcierge, Ct))!;
        Assert.Equal((OutcomeLinkHow.Tell, OutcomeActorKind.Member), (agentTell.How, agentTell.SetByKind));

        // Its `workflow.outcome-changed` row names the member, not the person it acts for.
        var row = (await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.WorkflowOutcomeChanged, chosen.Id, Ct))!;
        Assert.Equal(ConciergeLaunchFactory.PrincipalId(owner.Id), row.ActorId);
        Assert.Null(row.ActorEmail);

        Assert.StartsWith("HTTP 200", await tools.Outcome("set", outcome: other.Id, causation: byConcierge.ToString(), cancellationToken: Ct), StringComparison.Ordinal);
        var moved = (await Outcomes.CurrentLinkAsync(byConcierge, Ct))!;
        Assert.Equal((other.Id, OutcomeLinkHow.Manager), (moved.OutcomeId, moved.How));
    }

    [Fact]
    public async Task Proposing_an_existing_name_links_the_existing_outcome_and_creates_none()
    {
        var person = await host.PersonAsync();
        var name = Unique("Maintain qualified openings");
        var tools = ManagerTools(host.Alpha);

        var first = await WorkflowAsync(person, host.Alpha);
        var proposed = await tools.Outcome("propose", name: name, description: "openings", causation: first.ToString(), cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", proposed, StringComparison.Ordinal);

        var made = (await Outcomes.FindLiveByNameAsync(name, Ct))!;
        Assert.Equal((OutcomeStatus.Proposed, $"{host.Alpha}/Manager", OutcomeActorKind.Member), (made.Status, made.CreatedBy, made.CreatedByKind));
        Assert.Equal(made.Id, (await Outcomes.CurrentLinkAsync(first, Ct))!.OutcomeId);
        var count = (await Outcomes.ListAsync(Ct)).Count;

        // The same name again, differently cased and spaced: nothing created, the existing one linked.
        var second = await WorkflowAsync(person, host.Alpha);
        var again = await tools.Outcome("propose", name: "  " + name.ToUpperInvariant().Replace(" ", "   ") + " ", causation: second.ToString(), cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", again, StringComparison.Ordinal);
        Assert.Contains("\"created\":false", again, StringComparison.Ordinal);
        Assert.Contains("nothing was created", again, StringComparison.Ordinal);
        Assert.Equal(count, (await Outcomes.ListAsync(Ct)).Count);
        Assert.Equal(made.Id, (await Outcomes.CurrentLinkAsync(second, Ct))!.OutcomeId);

        // Each wrote a tenant row naming the member.
        var rows = (await host.Services.GetRequiredService<ITenantLog>().ReadAsync(take: 200, ct: Ct)).Events
            .Where(e => e.Subject == made.Id).ToList();
        Assert.Contains(rows, e => e.Action == TenantActions.OutcomeCreated && e.ActorId == $"{host.Alpha}/Manager");
        Assert.Equal(2, rows.Count(e => e.Action == TenantActions.WorkflowOutcomeChanged && e.ActorId == $"{host.Alpha}/Manager"));
    }

    // ---- the name rule, rename, merge, reject ----

    [Fact]
    public async Task A_name_is_unique_among_live_outcomes_and_a_retired_name_may_be_used_again()
    {
        var person = await host.PersonAsync();
        var name = Unique("Keep the pipeline current");
        var first = await CreateAsync(person, name);

        var taken = await person.PostAsJsonAsync("/api/outcomes", new { name = "  " + name.ToUpperInvariant() + "  " }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains(first.Id, await ErrorAsync(taken), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await person.PostAsync($"/api/outcomes/{first.Id}/retire", null, Ct)).StatusCode);

        var reused = await CreateAsync(person, name);
        Assert.NotEqual(first.Id, reused.Id);

        // The retired one cannot come back while its name is taken.
        var back = await person.PostAsync($"/api/outcomes/{first.Id}/reactivate", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
    }

    [Fact]
    public async Task A_rename_keeps_every_link_and_its_name_snapshot()
    {
        var person = await host.PersonAsync();
        var before = Unique("Run the crawler");
        var outcome = await CreateAsync(person, before);
        var workflow = await WorkflowAsync(person, host.Alpha, outcome.Id);
        var linkBefore = (await Outcomes.CurrentLinkAsync(workflow, Ct))!;

        var after = Unique("Maintain a current pipeline of qualified job openings");
        var renamed = await person.PatchAsJsonAsync($"/api/outcomes/{outcome.Id}", new { name = after }, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var link = (await Outcomes.CurrentLinkAsync(workflow, Ct))!;
        Assert.Equal(linkBefore, link);
        Assert.Equal((outcome.Id, before), (link.OutcomeId, link.OutcomeNameAtLink));
        Assert.Equal(after, (await Outcomes.FindAsync(outcome.Id, Ct))!.Name);

        var detail = await JsonAsync(await person.GetAsync($"/api/outcomes/{outcome.Id}", Ct));
        Assert.Equal(before, detail.GetProperty("history")[0].GetProperty("outcomeNameAtLink").GetString());
        Assert.NotNull(await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.OutcomeRenamed, outcome.Id, Ct));
    }

    [Fact]
    public async Task The_detail_lists_its_renames_status_changes_and_merges_with_who_and_when()
    {
        var person = await host.PersonAsync();
        var first = Unique("Hire engineers");
        var outcome = await CreateAsync(person, first);
        var second = Unique("Fill open engineering roles");
        var since = DateTimeOffset.UtcNow.AddSeconds(-5);
        Assert.Equal(HttpStatusCode.OK, (await person.PatchAsJsonAsync($"/api/outcomes/{outcome.Id}", new { name = second }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsync($"/api/outcomes/{outcome.Id}/retire", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsync($"/api/outcomes/{outcome.Id}/reactivate", null, Ct)).StatusCode);

        var absorbed = await CreateAsync(person, Unique("Recruit developers"));
        Assert.Equal(HttpStatusCode.OK,
            (await person.PostAsJsonAsync($"/api/outcomes/{absorbed.Id}/merge", new { into = outcome.Id }, Ct)).StatusCode);

        var events = (await JsonAsync(await person.GetAsync($"/api/outcomes/{outcome.Id}", Ct)))
            .GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(
            [TenantActions.OutcomeCreated, TenantActions.OutcomeRenamed, TenantActions.OutcomeRetired,
             TenantActions.OutcomeReactivated, TenantActions.OutcomeCreated, TenantActions.OutcomeMerged],
            events.Select(e => e.GetProperty("action").GetString()));
        Assert.All(events, e =>
        {
            Assert.Equal(Email, e.GetProperty("by").GetString());
            Assert.True(e.GetProperty("at").GetDateTimeOffset() >= since);
        });

        var renamed = events[1];
        Assert.Equal((first, second), (renamed.GetProperty("from").GetString(), renamed.GetProperty("name").GetString()));
        Assert.Equal(outcome.Id, events[2].GetProperty("outcomeId").GetString());
        Assert.Equal(absorbed.Id, events[5].GetProperty("outcomeId").GetString());
        Assert.Equal(outcome.Id, events[5].GetProperty("detail").GetProperty("into").GetString());

        // Another outcome's acts are not in this one's history.
        Assert.DoesNotContain(events, e => e.GetProperty("outcomeId").GetString() is var id && id != outcome.Id && id != absorbed.Id);
    }

    [Fact]
    public async Task A_merge_moves_the_figures_without_rewriting_a_link_and_its_preview_changes_nothing()
    {
        var person = await host.PersonAsync();
        var from = await CreateAsync(person, Unique("Old wording"));
        var into = await CreateAsync(person, Unique("New wording"));

        var correlation = NextCorrelation();
        await LinkAsync(correlation, from.Id, host.Alpha);
        await RunAsync(correlation, host.Alpha, "Alpha", At(0), At(10), billable: 100);
        var linksBefore = await Outcomes.ReadLinksAsync(Ct);

        var preview = await person.PostAsJsonAsync($"/api/outcomes/{from.Id}/merge?preview=true", new { into = into.Id }, Ct);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var answer = await JsonAsync(preview);
        Assert.Equal(1, answer.GetProperty("moves").GetProperty("links").GetInt32());
        Assert.Equal(100, answer.GetProperty("moves").GetProperty("figures").GetProperty("tokens").GetProperty("billable").GetInt64());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("refusal").ValueKind);

        // THE PREVIEW CHANGED NOTHING.
        Assert.Equal(OutcomeStatus.Active, (await Outcomes.FindAsync(from.Id, Ct))!.Status);
        Assert.Equal(linksBefore, await Outcomes.ReadLinksAsync(Ct));
        Assert.Null(await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.OutcomeMerged, from.Id, Ct));

        var merged = await person.PostAsJsonAsync($"/api/outcomes/{from.Id}/merge", new { into = into.Id }, Ct);
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Equal((OutcomeStatus.Merged, into.Id), ((await Outcomes.FindAsync(from.Id, Ct))!.Status, (await Outcomes.FindAsync(from.Id, Ct))!.MergedInto));

        // NO LINK REWRITTEN, AND THE FIGURES FOLLOW merged_into.
        Assert.Equal(linksBefore, await Outcomes.ReadLinksAsync(Ct));
        var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
        var target = list.GetProperty("outcomes").EnumerateArray().Single(o => o.GetProperty("id").GetString() == into.Id);
        Assert.Equal(100, target.GetProperty("figures").GetProperty("tokens").GetProperty("billable").GetInt64());
        Assert.DoesNotContain(list.GetProperty("outcomes").EnumerateArray(), o => o.GetProperty("id").GetString() == from.Id);

        var detail = await JsonAsync(await person.GetAsync($"/api/outcomes/{from.Id}", Ct));
        Assert.Equal(into.Id, detail.GetProperty("resolvedTo").GetString());
    }

    [Fact]
    public async Task Rejecting_a_proposed_outcome_is_refused_while_a_link_names_it()
    {
        var person = await host.PersonAsync();
        var tools = ManagerTools(host.Alpha);

        var workflow = await WorkflowAsync(person, host.Alpha);
        await tools.Outcome("propose", name: Unique("Linked proposal"), causation: workflow.ToString(), cancellationToken: Ct);
        var linked = (await Outcomes.CurrentLinkAsync(workflow, Ct))!.OutcomeId;

        var refused = await person.DeleteAsync($"/api/outcomes/{linked}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("linked to 1 workflow", await ErrorAsync(refused), StringComparison.Ordinal);
        Assert.NotNull(await Outcomes.FindAsync(linked, Ct));

        var bare = (await Outcomes.ProposeAsync(Unique("Bare proposal"), null, new OutcomeActor("x", OutcomeActorKind.Member), null, null,
            new TriggerAudit("x", null, TenantActions.OutcomeCreated, null, null, null), Ct)).Outcome!;
        Assert.Equal(HttpStatusCode.NoContent, (await person.DeleteAsync($"/api/outcomes/{bare.Id}", Ct)).StatusCode);
        Assert.Null(await Outcomes.FindAsync(bare.Id, Ct));
        Assert.NotNull(await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.OutcomeRejected, bare.Id, Ct));

        // Only a proposed one is rejected; a confirmed one is retired instead.
        var active = await CreateAsync(person, Unique("Active"));
        Assert.Equal(HttpStatusCode.Conflict, (await person.DeleteAsync($"/api/outcomes/{active.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Only_a_person_confirms_and_a_confirmed_proposal_records_who()
    {
        var person = await host.PersonAsync();
        var proposal = (await Outcomes.ProposeAsync(Unique("To confirm"), null, new OutcomeActor("Alpha/Manager", OutcomeActorKind.Member), null, null,
            new TriggerAudit("Alpha/Manager", null, TenantActions.OutcomeCreated, null, null, null), Ct)).Outcome!;

        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, "Manager").ToString(), PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);
        using var manager = host.Container(key);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"/api/outcomes/{proposal.Id}/confirm", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await person.PostAsync($"/api/outcomes/{proposal.Id}/confirm", null, Ct)).StatusCode);
        var confirmed = (await Outcomes.FindAsync(proposal.Id, Ct))!;
        Assert.Equal((OutcomeStatus.Active, Email), (confirmed.Status, confirmed.ConfirmedBy));
    }

    // ---- figures ----

    [Fact]
    public async Task The_figures_sum_agent_time_give_elapsed_as_median_and_longest_count_unmeasured_runs_and_name_a_deleted_team()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Figures"));

        // THREE WORKFLOWS: elapsed 60, 120 and 600 seconds - median 120, longest 600, never 780.
        var one = NextCorrelation();
        var two = NextCorrelation();
        var three = NextCorrelation();

        foreach (var (correlation, elapsed) in new[] { (one, 60), (two, 120), (three, 600) })
        {
            await LinkAsync(correlation, outcome.Id, host.Alpha);
            await CloseAsync(correlation, host.Alpha, "Alpha", At(0), At(elapsed));
        }

        // TWO PARALLEL RUNS in one workflow, 30 seconds each over the same half-minute: 60 of agent time.
        await RunAsync(one, host.Alpha, "Alpha", At(0), At(30), billable: 1000, queued: At(-5));
        await RunAsync(one, host.Alpha, "Alpha", At(0), At(30), billable: 500, queued: At(-5));

        // AN UNMEASURED RUN, counted and adding nothing.
        await RunAsync(two, host.Alpha, "Alpha", At(0), At(10), billable: null);

        // A TEAM THAT IS GONE, named from the ledger's snapshot.
        await RunAsync(three, "gone-team", "Gone Team", At(0), At(10), billable: 7);

        // AN UNLINKED WORKFLOW, carried by "No outcome".
        var unlinked = NextCorrelation();
        await RunAsync(unlinked, host.Alpha, "Alpha", At(0), At(40), billable: 3);

        var list = await JsonAsync(await person.GetAsync(
            $"/api/outcomes?from={Uri.EscapeDataString(At(-3600).ToString("O"))}&to={Uri.EscapeDataString(At(3600).ToString("O"))}", Ct));
        var figures = list.GetProperty("outcomes").EnumerateArray()
            .Single(o => o.GetProperty("id").GetString() == outcome.Id).GetProperty("figures");

        Assert.Equal(3, figures.GetProperty("workflows").GetProperty("total").GetInt32());
        Assert.Equal(3, figures.GetProperty("workflows").GetProperty("completed").GetInt32());
        Assert.Equal(80, figures.GetProperty("agentSeconds").GetDouble());
        Assert.Equal(10, figures.GetProperty("waitingSeconds").GetDouble());
        Assert.Equal(120, figures.GetProperty("elapsed").GetProperty("medianSeconds").GetDouble());
        Assert.Equal(600, figures.GetProperty("elapsed").GetProperty("longestSeconds").GetDouble());
        Assert.Equal(1507, figures.GetProperty("tokens").GetProperty("billable").GetInt64());
        Assert.Equal(3, figures.GetProperty("tokens").GetProperty("measuredRuns").GetInt32());
        Assert.Equal(1, figures.GetProperty("tokens").GetProperty("unmeasuredRuns").GetInt32());

        var gone = figures.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("id").GetString() == "gone-team");
        Assert.Equal(("Gone Team", true), (gone.GetProperty("name").GetString(), gone.GetProperty("deleted").GetBoolean()));
        var alpha = figures.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("id").GetString() == host.Alpha);
        Assert.False(alpha.GetProperty("deleted").GetBoolean());

        var none = list.GetProperty("noOutcome");
        Assert.Equal(OutcomeFigures.NoOutcomeName, none.GetProperty("name").GetString());
        Assert.Equal(1, none.GetProperty("figures").GetProperty("workflows").GetProperty("total").GetInt32());
        Assert.Equal(3, none.GetProperty("figures").GetProperty("tokens").GetProperty("billable").GetInt64());
        Assert.True(list.TryGetProperty("ledgerStartedAt", out _));
    }

    // ---- permissions ----

    [Fact]
    public async Task The_Outcomes_permit_is_the_managers_and_the_concierges_only_and_a_member_is_refused_propose_and_set()
    {
        Assert.Contains(Permits.Outcomes, Permits.All);
        Assert.Contains(Permits.Outcomes, TeamRegistry.ManagerPermits);
        Assert.Contains(Permits.Outcomes, ConciergeLaunchFactory.ConciergePermits);

        // A member hired even with every permit named does not hold it; the Manager does.
        var registry = host.Services.GetRequiredService<TeamRegistry>();
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        await registry.AddContainerAsync(host.Alpha, "Greedy", agent, "", [], permits: Permits.All, ct: Ct);
        var members = await host.Services.GetRequiredService<ITeamStore>().MembersAsync(Ct);
        Assert.DoesNotContain(Permits.Outcomes, members.Single(m => m.Team == host.Alpha && m.Name == "Greedy").Permits);
        Assert.Contains(Permits.Outcomes, members.Single(m => m.Team == host.Alpha && m.Name == TeamRegistry.DefaultManagerName).Permits);

        // A member's credential: the tool refuses it, and the routes refuse it for the permit.
        var person = await host.PersonAsync();
        var workflow = await WorkflowAsync(person, host.Alpha);
        var outcome = await CreateAsync(person, Unique("Member refused"));
        var memberPermits = new HashSet<string> { Permits.Read, Permits.Progress, Permits.Skills, Permits.Sites };
        var member = Tools(new ContainerId(host.Alpha, "Dev").ToString(), PrincipalKind.Container, host.Alpha, memberPermits);

        foreach (var answer in new[]
        {
            await member.Outcome("set", outcome: outcome.Id, causation: workflow.ToString(), cancellationToken: Ct),
            await member.Outcome("propose", name: Unique("x"), causation: workflow.ToString(), cancellationToken: Ct),
            await member.Outcome("list", cancellationToken: Ct),
        })
        {
            Assert.StartsWith("Refused: the outcome tool is a Manager's and the Concierge's.", answer, StringComparison.Ordinal);
        }

        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, "Dev").ToString(), PrincipalKind.Container, host.Alpha, memberPermits, ct: Ct);
        using var client = host.Container(key);
        var put = await client.PutAsJsonAsync($"/api/teams/{host.Alpha}/workflows/{workflow}/outcome", new { outcome = outcome.Id }, Ct);
        Assert.Equal(PermitGate.Missing(Permits.Outcomes), await ErrorAsync(put));
        var propose = await client.PostAsJsonAsync("/api/outcomes/propose", new { name = "x", team = host.Alpha, correlation = workflow }, Ct);
        Assert.Equal(PermitGate.Missing(Permits.Outcomes), await ErrorAsync(propose));
        Assert.Null(await Outcomes.CurrentLinkAsync(workflow, Ct));

        // The Concierge holds it and links any team's workflow, as an agent.
        var owner = await host.Services.GetRequiredService<IUserStore>().FindAsync(Email, Ct);
        var concierge = Tools(ConciergeLaunchFactory.PrincipalId(owner!.Id), PrincipalKind.TenantConcierge, host.Alpha,
            ConciergeLaunchFactory.ConciergePermits, owner.Id);
        var byConcierge = await concierge.Outcome("set", team: host.Beta, outcome: outcome.Id,
            causation: (await WorkflowAsync(person, host.Beta)).ToString(), cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", byConcierge, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_principal_is_refused_every_person_only_outcome_route()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Persons only"));
        var into = await CreateAsync(person, Unique("Into"));

        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, "Manager").ToString(), PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);
        using var machine = host.Container(key);

        var calls = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, "/api/outcomes", new { name = Unique("m") }),
            (HttpMethod.Patch, $"/api/outcomes/{outcome.Id}", new { name = Unique("m") }),
            (HttpMethod.Post, $"/api/outcomes/{outcome.Id}/confirm", null),
            (HttpMethod.Post, $"/api/outcomes/{outcome.Id}/retire", null),
            (HttpMethod.Post, $"/api/outcomes/{outcome.Id}/reactivate", null),
            (HttpMethod.Post, $"/api/outcomes/{outcome.Id}/merge", new { into = into.Id }),
            (HttpMethod.Post, $"/api/outcomes/{outcome.Id}/merge?preview=true", new { into = into.Id }),
            (HttpMethod.Delete, $"/api/outcomes/{outcome.Id}", null),
        };

        foreach (var (method, path, body) in calls)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            var refused = await machine.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(PermitGate.HumansOnlyMessage, await ErrorAsync(refused));
        }

        Assert.Equal(OutcomeStatus.Active, (await Outcomes.FindAsync(outcome.Id, Ct))!.Status);

        // Reading is Read's.
        Assert.Equal(HttpStatusCode.OK, (await machine.GetAsync("/api/outcomes", Ct)).StatusCode);
    }

    // ---- ids, the ledger, the export, the board ----

    [Fact]
    public async Task An_outcome_id_is_a_guid_and_a_workflows_close_records_the_outcome_it_served()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Guid"));
        Assert.True(Guid.TryParseExact(outcome.Id, "D", out _));

        var workflow = await WorkflowAsync(person, host.Alpha, outcome.Id);
        var closed = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/workflows/{workflow}/close", new { reason = "enough" }, Ct);
        Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync(Ct));

        var row = (await host.Services.GetRequiredService<IUsageLedger>().ReadWorkflowAsync(workflow, Ct))!;
        Assert.Equal(outcome.Id, row.OutcomeIdAtClose);

        var export = await JsonAsync(await person.GetAsync("/api/ledger/export", Ct));
        Assert.Contains(export.GetProperty("outcomes").EnumerateArray(), o => o.GetProperty("id").GetString() == outcome.Id);
        Assert.Contains(export.GetProperty("links").EnumerateArray(), l => l.GetProperty("correlation").GetInt64() == workflow);
    }

    [Fact]
    public async Task The_board_narrows_to_an_outcomes_cards_or_to_the_cards_with_none()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Board"));
        var linked = await WorkflowAsync(person, host.Alpha, outcome.Id);
        var unlinked = await WorkflowAsync(person, host.Alpha);

        var board = await JsonAsync(await person.GetAsync($"/api/teams/{host.Alpha}/kanban/board?outcome={outcome.Id}", Ct));
        var workflows = board.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("workflowSeq").GetInt64()).ToList();
        Assert.Equal([linked], workflows);

        var none = await JsonAsync(await person.GetAsync($"/api/kanban/board?team={host.Alpha}&outcome=none", Ct));
        var noneWorkflows = none.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("workflowSeq").GetInt64()).ToList();
        Assert.Contains(unlinked, noneWorkflows);
        Assert.DoesNotContain(linked, noneWorkflows);
    }

    [Fact]
    public async Task Every_card_carries_its_outcome_a_proposed_one_with_its_status_a_merged_one_as_its_target_and_none_as_null()
    {
        var person = await host.PersonAsync();
        var active = await CreateAsync(person, Unique("Card active"));
        var target = await CreateAsync(person, Unique("Card merge target"));
        var merged = await CreateAsync(person, Unique("Card merged"));

        var linked = await WorkflowAsync(person, host.Alpha, active.Id);
        var viaMerge = await WorkflowAsync(person, host.Alpha, merged.Id);
        var unlinked = await WorkflowAsync(person, host.Alpha);
        var byManager = await WorkflowAsync(person, host.Alpha);

        var proposedName = Unique("Card proposed");
        Assert.StartsWith("HTTP 2", await ManagerTools(host.Alpha).Outcome("propose", name: proposedName, causation: byManager.ToString(), cancellationToken: Ct), StringComparison.Ordinal);
        var proposed = (await Outcomes.FindLiveByNameAsync(proposedName, Ct))!;

        var merge = await person.PostAsJsonAsync($"/api/outcomes/{merged.Id}/merge", new { into = target.Id }, Ct);
        Assert.True(merge.IsSuccessStatusCode, await merge.Content.ReadAsStringAsync(Ct));

        var board = await JsonAsync(await person.GetAsync($"/api/teams/{host.Alpha}/kanban/board", Ct));
        JsonElement OutcomeOf(long workflow) => board.GetProperty("cards").EnumerateArray()
            .Single(c => c.GetProperty("workflowSeq").GetInt64() == workflow).GetProperty("outcome");

        Assert.Equal((active.Id, active.Name, OutcomeStatus.Active),
            (OutcomeOf(linked).GetProperty("id").GetString(), OutcomeOf(linked).GetProperty("name").GetString(), OutcomeOf(linked).GetProperty("status").GetString()));
        Assert.Equal((proposed.Id, OutcomeStatus.Proposed),
            (OutcomeOf(byManager).GetProperty("id").GetString(), OutcomeOf(byManager).GetProperty("status").GetString()));
        Assert.Equal(target.Id, OutcomeOf(viaMerge).GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, OutcomeOf(unlinked).ValueKind);

        // The tenant board and one card carry the same.
        var tenant = await JsonAsync(await person.GetAsync($"/api/kanban/board?team={host.Alpha}", Ct));
        var card = tenant.GetProperty("cards").EnumerateArray().Single(c => c.GetProperty("workflowSeq").GetInt64() == linked);
        Assert.Equal(active.Id, card.GetProperty("outcome").GetProperty("id").GetString());
        var one = await JsonAsync(await person.GetAsync($"/api/teams/{host.Alpha}/kanban/cards/{card.GetProperty("id").GetString()}", Ct));
        Assert.Equal(active.Id, one.GetProperty("outcome").GetProperty("id").GetString());

        // The team workflows view's rows carry the same outcome. The view lists a workflow once the
        // team's own member has written in it, so the Manager reports progress in each.
        var manager = new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString();
        foreach (var workflow in new[] { linked, byManager, unlinked })
        {
            await Log.AppendAsync(new NewMessage(MessageTypes.Progress, """{"text":"working"}""", manager, workflow), Ct);
        }

        var listed = await JsonAsync(await person.GetAsync($"/api/teams/{host.Alpha}/workflows", Ct));
        JsonElement RowOutcome(long workflow) => listed.GetProperty("workflows").EnumerateArray()
            .Single(w => w.GetProperty("correlation").GetInt64() == workflow).GetProperty("outcome");
        Assert.Equal(active.Id, RowOutcome(linked).GetProperty("id").GetString());
        Assert.Equal(OutcomeStatus.Proposed, RowOutcome(byManager).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, RowOutcome(unlinked).ValueKind);

        // FILTERS: a merged outcome's id finds its target's cards; the tag and the filter agree.
        var byMerged = await JsonAsync(await person.GetAsync($"/api/teams/{host.Alpha}/kanban/board?outcome={merged.Id}", Ct));
        Assert.Equal([viaMerge], byMerged.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("workflowSeq").GetInt64()).ToList());
        var byProposed = await JsonAsync(await person.GetAsync($"/api/kanban/board?outcome={proposed.Id}", Ct));
        Assert.Equal([byManager], byProposed.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("workflowSeq").GetInt64()).ToList());
        var none = await JsonAsync(await person.GetAsync($"/api/teams/{host.Alpha}/kanban/board?outcome=none", Ct));
        Assert.All(none.GetProperty("cards").EnumerateArray(), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("outcome").ValueKind));
        Assert.Contains(unlinked, none.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("workflowSeq").GetInt64()));
        Assert.Equal("none", none.GetProperty("filters").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_cards_outcome_is_its_open_workflows_else_its_latest_workflows_read_in_one_query()
    {
        var person = await host.PersonAsync();
        var first = await CreateAsync(person, Unique("Older workflow"));
        var second = await CreateAsync(person, Unique("Newer workflow"));
        var older = await WorkflowAsync(person, host.Alpha, first.Id);
        var newer = await WorkflowAsync(person, host.Alpha, second.Id);

        var card = new Harness.Kanban.KanbanCard(
            "c", older, host.Alpha, null, null, "t", "", "running", "todo", "grey", [], DateTime.UtcNow, DateTime.UtcNow, false,
            Workflows: [older, newer]);

        var open = await CardOutcomes.AttachAsync([card with { OpenWorkflow = new Harness.Kanban.CardWorkflow(older, older) }], Outcomes, Ct);
        Assert.Equal(first.Id, open[0].Outcome!.Id);

        var closed = await CardOutcomes.AttachAsync([card], Outcomes, Ct);
        Assert.Equal(second.Id, closed[0].Outcome!.Id);

        // ONE query answers every workflow asked about, and a workflow with no link is absent.
        var unlinked = await WorkflowAsync(person, host.Alpha);
        var found = await Outcomes.CurrentOutcomesAsync([older, newer, unlinked], Ct);
        Assert.Equal((first.Id, second.Id, false), (found[older].Id, found[newer].Id, found.ContainsKey(unlinked)));
    }

    [Fact]
    public async Task The_tools_list_puts_this_teams_outcomes_first_and_names_no_url_in_a_refusal()
    {
        var person = await host.PersonAsync();
        var used = await CreateAsync(person, Unique("zz Used by Alpha"));
        await WorkflowAsync(person, host.Alpha, used.Id);

        var list = await ManagerTools(host.Alpha).Outcome("list", cancellationToken: Ct);
        Assert.StartsWith("HTTP 200", list, StringComparison.Ordinal);
        var first = JsonDocument.Parse(list.Split(Environment.NewLine, 2)[1]).RootElement.GetProperty("outcomes")[0];
        Assert.True(first.GetProperty("teamWorkflows").GetInt32() > 0);

        foreach (var answer in new[]
        {
            await ManagerTools(host.Alpha).Outcome("delete", cancellationToken: Ct),
            await ManagerTools(host.Alpha).Outcome("set", cancellationToken: Ct),
            await ManagerTools(host.Alpha).Outcome("set", outcome: used.Id, cancellationToken: Ct),
            await ManagerTools(host.Alpha).Outcome("propose", cancellationToken: Ct),
            await ManagerTools(host.Alpha).Outcome("show", outcome: "nothing at all", cancellationToken: Ct),
        })
        {
            Assert.StartsWith("Refused: the outcome tool", answer, StringComparison.Ordinal);
            Assert.DoesNotContain("/api/", answer, StringComparison.Ordinal);
            Assert.DoesNotContain("http", answer, StringComparison.OrdinalIgnoreCase);
        }

        Assert.StartsWith("HTTP 200", await ManagerTools(host.Alpha).Outcome("show", outcome: used.Name, cancellationToken: Ct), StringComparison.Ordinal);
    }

    // ---- the skills and the log of links ----

    [Fact]
    public void The_manager_concierge_and_kanban_skills_and_the_transport_name_the_outcome_tool()
    {
        var manager = BuiltInSkills.Find("manager")!.Body;
        foreach (var sentence in new[]
        {
            "Every workflow you work in must have an outcome.",
            "An outcome names the result being\n            produced, not the tasks performed.".Replace("\n            ", " "),
        })
        {
            Assert.Contains(sentence, Flat(manager), StringComparison.Ordinal);
        }

        Assert.Contains("Before proposing one, list the active outcomes and reuse one that means the same result; "
            + "propose a new one only when the intended result is materially different.", Flat(manager), StringComparison.Ordinal);
        Assert.Contains("Name outcomes as results (\"Maintain a current pipeline of qualified job openings\"), "
            + "never activities (\"Run the job crawler every 15 minutes\").", Flat(manager), StringComparison.Ordinal);
        Assert.Contains("Ask the person only when you cannot tell which outcome applies.", Flat(manager), StringComparison.Ordinal);

        Assert.Contains("pass it on `tell` as `outcome`", Flat(BuiltInSkills.Find("concierge")!.Body), StringComparison.Ordinal);
        Assert.Contains("the Outcome filter", Flat(BuiltInSkills.Find("kanban")!.Body), StringComparison.Ordinal);

        // THE TRANSPORT PREAMBLE, on every built-in, names the tool.
        Assert.All(BuiltInSkills.Bodies(), body => Assert.Contains("`site`, and `outcome`.", body, StringComparison.Ordinal));
    }

    private static string Flat(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    [Fact]
    public async Task A_link_row_is_never_updated_or_deleted()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Append only"));
        var workflow = await WorkflowAsync(person, host.Alpha, outcome.Id);

        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);

        foreach (var sql in new[]
        {
            "UPDATE workflow_outcome_links SET outcome_name_at_link = 'rewritten' WHERE correlation = $c",
            "DELETE FROM workflow_outcome_links WHERE correlation = $c",
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$c", workflow);
            var refused = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Contains("append-only", refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal(outcome.Name, (await Outcomes.CurrentLinkAsync(workflow, Ct))!.OutcomeNameAtLink);
    }

    // ---- ledger fixtures, written as the ledger's own writer would ----

    private static long _correlation = 9_000_000_000 + Random.Shared.Next(1_000_000);

    private static long NextCorrelation() => Interlocked.Add(ref _correlation, 1000);

    /// <summary>An instant in a fixed hour of 2001, so a windowed read sees only these rows.</summary>
    private static DateTimeOffset At(int seconds) => new DateTimeOffset(2001, 2, 3, 4, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private Task LinkAsync(long correlation, string outcome, string team) =>
        Outcomes.LinkAsync(correlation, outcome, team, new OutcomeActor(Email, OutcomeActorKind.Person), OutcomeLinkHow.Person,
            agentRule: false, new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct);

    private static long _run = 8_000_000_000 + Random.Shared.Next(1_000_000);

    private async Task RunAsync(
        long correlation, string team, string teamName, DateTimeOffset started, DateTimeOffset ended, long? billable,
        DateTimeOffset? queued = null)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, run_outcome,
                queued_at, started_at, ended_at, measured, tokens_in, tokens_out, billable)
            VALUES ($seq, $correlation, $team, $name, 'Dev', 'completed', $queued, $started, $ended,
                $measured, $billable, 0, $billable)
            """;
        insert.Parameters.AddWithValue("$seq", Interlocked.Increment(ref _run));
        insert.Parameters.AddWithValue("$correlation", correlation);
        insert.Parameters.AddWithValue("$team", team);
        insert.Parameters.AddWithValue("$name", teamName);
        insert.Parameters.AddWithValue("$queued", (object?)queued?.ToString("O") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$started", started.ToString("O"));
        insert.Parameters.AddWithValue("$ended", ended.ToString("O"));
        insert.Parameters.AddWithValue("$measured", billable is null ? 0 : 1);
        insert.Parameters.AddWithValue("$billable", (object?)billable ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private async Task CloseAsync(long correlation, string team, string teamName, DateTimeOffset root, DateTimeOffset closed)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO workflow_ledger (close_seq, correlation, team_id, team_name, root_at, closed_at, how_closed)
            VALUES ($seq, $correlation, $team, $name, $root, $closed, 'completed')
            """;
        insert.Parameters.AddWithValue("$seq", Interlocked.Increment(ref _run));
        insert.Parameters.AddWithValue("$correlation", correlation);
        insert.Parameters.AddWithValue("$team", team);
        insert.Parameters.AddWithValue("$name", teamName);
        insert.Parameters.AddWithValue("$root", root.ToString("O"));
        insert.Parameters.AddWithValue("$closed", closed.ToString("O"));
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
