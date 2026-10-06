using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// THE OUTCOMES DASHBOARD (docs/specs/2026-10-05-outcomes-dashboard-design.md): a backlog item takes
/// the outcome its dispatch workflow is linked to when it has none, and never otherwise; an outcome's
/// figures carry its backlog by where it stands, the time it was blocked on a person, its efficiency,
/// its last eight weeks and its cost at the instance's rate; and each outcome carries a value.
/// </summary>
public sealed class OutcomeDashboardTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Email = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOutcomeStore Outcomes => host.Services.GetRequiredService<IOutcomeStore>();

    private string Database => Path.Combine(host.DataRoot, "messages.db");

    private static string Unique(string stem) => $"{stem} {Guid.NewGuid():N}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(Ct));

    private async Task<Outcome> CreateAsync(HttpClient person, string name)
    {
        var created = await person.PostAsJsonAsync("/api/outcomes", new { name }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Outcomes.FindAsync((await JsonAsync(created)).GetProperty("id").GetString()!, Ct))!;
    }

    private async Task<string> ItemAsync(HttpClient person, string title, string? outcome = null)
    {
        var id = (await JsonAsync(await person.PostAsJsonAsync("/api/backlog", new { title, body = "spec" }, Ct)))
            .GetProperty("id").GetInt64();
        var edited = await person.PatchAsJsonAsync($"/api/backlog/{id}", new { state = "ready", outcomeId = outcome }, Ct);
        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync(Ct));
        return PlatformBacklogId.Format(id);
    }

    private Task<OutcomeWrite> ManagerLinkAsync(long correlation, string outcome) =>
        Outcomes.LinkAsync(correlation, outcome, host.Alpha, new OutcomeActor($"{host.Alpha}/Manager", OutcomeActorKind.Member),
            OutcomeLinkHow.Manager, agentRule: true,
            new TriggerAudit($"{host.Alpha}/Manager", null, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct);

    private Task<OutcomeWrite> PersonLinkAsync(long correlation, string outcome) =>
        Outcomes.LinkAsync(correlation, outcome, host.Alpha, new OutcomeActor(Email, OutcomeActorKind.Person),
            OutcomeLinkHow.Person, agentRule: false,
            new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct);

    private async Task<string?> ItemOutcomeAsync(string item) =>
        await ScalarAsync("SELECT outcome_id FROM backlog_items WHERE id = $id", ("$id", item)) as string;

    private async Task<long> InheritedRowsAsync(string item) =>
        (long)(await ScalarAsync(
            "SELECT COUNT(*) FROM tenant_events WHERE action = $action AND subject = $id",
            ("$action", TenantActions.BacklogItemOutcomeInherited), ("$id", item)))!;

    [Fact]
    public async Task A_dispatched_item_with_no_outcome_takes_the_outcome_its_workflow_is_linked_to_with_a_tenant_row()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Inherited"));
        var item = await ItemAsync(person, "Crawl openings");
        var correlation = NextCorrelation();
        await DispatchAsync(item, correlation);

        Assert.Null(await ItemOutcomeAsync(item));
        Assert.True((await ManagerLinkAsync(correlation, outcome.Id)).Ok);

        Assert.Equal(outcome.Id, await ItemOutcomeAsync(item));
        Assert.Equal(1, await InheritedRowsAsync(item));

        var actor = await ScalarAsync(
            "SELECT actor_id FROM tenant_events WHERE action = $action AND subject = $id",
            ("$action", TenantActions.BacklogItemOutcomeInherited), ("$id", item));
        Assert.Equal($"{host.Alpha}/Manager", actor);
    }

    [Fact]
    public async Task An_items_outcome_is_never_overwritten_and_never_cleared_by_its_workflows_link()
    {
        var person = await host.PersonAsync();
        var first = await CreateAsync(person, Unique("First"));
        var second = await CreateAsync(person, Unique("Second"));

        // AN ITEM THAT ALREADY HAS ONE keeps it when its workflow is moved by a person.
        var owned = await ItemAsync(person, "Owned", first.Id);
        var ownedWorkflow = NextCorrelation();
        await DispatchAsync(owned, ownedWorkflow);
        Assert.True((await PersonLinkAsync(ownedWorkflow, second.Id)).Ok);
        Assert.Equal(first.Id, await ItemOutcomeAsync(owned));
        Assert.Equal(0, await InheritedRowsAsync(owned));

        // AN INHERITED ONE stays when the workflow is moved again, and when a person chooses None.
        var item = await ItemAsync(person, "Inherits once");
        var workflow = NextCorrelation();
        await DispatchAsync(item, workflow);
        Assert.True((await ManagerLinkAsync(workflow, first.Id)).Ok);
        Assert.True((await PersonLinkAsync(workflow, second.Id)).Ok);
        Assert.Equal(first.Id, await ItemOutcomeAsync(item));

        Assert.True((await Outcomes.UnlinkAsync(workflow, host.Alpha, new OutcomeActor(Email, OutcomeActorKind.Person),
            new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Ok);
        Assert.Equal(first.Id, await ItemOutcomeAsync(item));
        Assert.Equal(1, await InheritedRowsAsync(item));
    }

    [Fact]
    public async Task The_start_back_fill_gives_an_item_its_newest_dispatch_workflows_outcome_once()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Back-filled"));
        var item = await ItemAsync(person, "Dispatched before");
        var workflow = NextCorrelation();
        await DispatchAsync(item, workflow);

        // A LINK FROM BEFORE THE CHANGE: written as a row, so the live writer's inheritance never ran.
        await ExecuteAsync(
            """
            INSERT INTO workflow_outcome_links (correlation, outcome_id, team_id, team_name_at_link, outcome_name_at_link,
                set_by, set_by_kind, set_at, how)
            VALUES ($c, $o, $t, 'Alpha', 'old', 'alpha/Manager', 'member', $at, 'manager')
            """,
            ("$c", workflow), ("$o", outcome.Id), ("$t", host.Alpha), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        await ExecuteAsync("DELETE FROM tenant_settings WHERE name = $n", ("$n", BacklogOutcomeStart.DoneName));

        Assert.True(await BacklogOutcomeStart.RunAsync(Database, Ct) >= 1);
        Assert.Equal(outcome.Id, await ItemOutcomeAsync(item));
        Assert.Equal(1, await InheritedRowsAsync(item));

        Assert.Null(await BacklogOutcomeStart.RunAsync(Database, Ct));
        Assert.Equal(1, await InheritedRowsAsync(item));
    }

    [Fact]
    public async Task The_backlog_is_counted_by_where_it_stands_and_declared_follows_the_setting()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Buckets"));

        await ItemAsync(person, "Not started", outcome.Id);
        var running = await ItemAsync(person, "Running", outcome.Id);
        await DispatchAsync(running, NextCorrelation());
        var declared = await ItemAsync(person, "Declared", outcome.Id);
        await DispatchAsync(declared, NextCorrelation());
        await ExecuteAsync("UPDATE backlog_items SET state = 'declared' WHERE id = $id", ("$id", declared));
        var done = await ItemAsync(person, "Implemented and archived", outcome.Id);
        await DispatchAsync(done, NextCorrelation());
        await ExecuteAsync("UPDATE backlog_items SET state = 'implemented', archived_at = $at WHERE id = $id",
            ("$id", done), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        var setAside = await ItemAsync(person, "Set aside", outcome.Id);
        await ExecuteAsync("UPDATE backlog_items SET archived_at = $at WHERE id = $id",
            ("$id", setAside), ("$at", DateTimeOffset.UtcNow.ToString("O")));

        var backlog = (await FiguresAsync(person, outcome.Id)).GetProperty("backlog");
        Assert.Equal((1, 1, 2), (backlog.GetProperty("notStarted").GetInt32(), backlog.GetProperty("inProgress").GetInt32(), backlog.GetProperty("achieved").GetInt32()));

        try
        {
            await SetOkAsync(person, TenantSettings.OutcomesDeclaredCountsAsName, "in-progress");
            backlog = (await FiguresAsync(person, outcome.Id)).GetProperty("backlog");
            Assert.Equal((1, 2, 1), (backlog.GetProperty("notStarted").GetInt32(), backlog.GetProperty("inProgress").GetInt32(), backlog.GetProperty("achieved").GetInt32()));
        }
        finally
        {
            await SetAsync(person, TenantSettings.OutcomesDeclaredCountsAsName, null);
        }

        // THE DETAIL LISTS EACH ITEM WITH ITS BUCKET, and leaves the one set aside out.
        var detail = await JsonAsync(await person.GetAsync($"/api/outcomes/{outcome.Id}", Ct));
        var items = detail.GetProperty("backlogItems").EnumerateArray()
            .ToDictionary(i => i.GetProperty("title").GetString()!, i => i.GetProperty("bucket").GetString());
        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["Not started"] = "notStarted",
                ["Running"] = "inProgress",
                ["Declared"] = "achieved",
                ["Implemented and archived"] = "achieved",
            },
            items);
    }

    [Fact]
    public async Task Efficiency_is_agent_time_over_agent_time_and_time_blocked_on_a_person_read_from_the_ledger()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Efficiency"));
        var workflow = NextCorrelation();
        Assert.True((await PersonLinkAsync(workflow, outcome.Id)).Ok);

        var member = $"Dev{Guid.NewGuid():N}"[..12];
        var start = DateTimeOffset.UtcNow.AddHours(-3);

        // 30 MINUTES OF WORK, BLOCKED, then 60 minutes waiting on a person until the next run is
        // queued, then 30 more minutes of work: 3600 s of agent time, 3600 s blocked - 50%.
        await RunAsync(workflow, member, start, start.AddMinutes(30), "blocked");
        await RunAsync(workflow, member, start.AddMinutes(90), start.AddMinutes(120), "completed", queued: start.AddMinutes(90));

        var figures = await FiguresAsync(person, outcome.Id);
        Assert.Equal(3600, figures.GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Equal(3600, figures.GetProperty("blockedSeconds").GetDouble(), 3);
        Assert.Equal(0.5, figures.GetProperty("efficiency").GetDouble(), 6);

        // NO TIME AT ALL IS NO EFFICIENCY, never 0%.
        var idle = await CreateAsync(person, Unique("Idle"));
        Assert.Equal(JsonValueKind.Null, (await FiguresAsync(person, idle.Id)).GetProperty("efficiency").ValueKind);
    }

    [Fact]
    public async Task The_last_eight_weeks_and_the_cost_are_priced_at_the_rate_and_no_rate_prices_nothing()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Spend"));
        var workflow = NextCorrelation();
        Assert.True((await PersonLinkAsync(workflow, outcome.Id)).Ok);

        var now = DateTimeOffset.UtcNow;
        await RunAsync(workflow, "Dev", now.AddHours(-2), now.AddHours(-1), "completed", billable: 400);
        await RunAsync(workflow, "Dev", now.AddDays(-14).AddHours(-2), now.AddDays(-14), "completed", billable: 100);

        var unpriced = await FiguresAsync(person, outcome.Id);
        Assert.Equal(JsonValueKind.Null, unpriced.GetProperty("cost").GetProperty("amount").ValueKind);
        Assert.Equal("USD", unpriced.GetProperty("cost").GetProperty("currency").GetString());

        try
        {
            await SetOkAsync(person, TenantSettings.OutcomesAgentHourlyRateName, 90);
            await SetOkAsync(person, TenantSettings.OutcomesCurrencyName, "eur");

            var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
            Assert.Equal(90, list.GetProperty("money").GetProperty("agentHourlyRate").GetInt32());
            Assert.Equal("EUR", list.GetProperty("money").GetProperty("currency").GetString());

            var figures = list.GetProperty("outcomes").EnumerateArray()
                .Single(o => o.GetProperty("id").GetString() == outcome.Id).GetProperty("figures");
            Assert.Equal(270m, figures.GetProperty("cost").GetProperty("amount").GetDecimal());

            var weekly = figures.GetProperty("weekly").EnumerateArray().ToList();
            Assert.Equal(OutcomeFigures.Weeks, weekly.Count);
            Assert.Equal(OutcomeFigures.WeekOf(now), weekly[^1].GetProperty("weekStart").GetDateTimeOffset());
            Assert.Equal(500, weekly.Sum(w => w.GetProperty("billable").GetInt64()));
            Assert.Equal(270m, weekly.Sum(w => w.GetProperty("cost").GetDecimal()));
        }
        finally
        {
            await SetAsync(person, TenantSettings.OutcomesAgentHourlyRateName, null);
            await SetAsync(person, TenantSettings.OutcomesCurrencyName, null);
        }
    }

    [Fact]
    public async Task An_outcomes_value_is_a_persons_edit_kept_as_a_plain_amount_and_anything_else_is_refused()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Valued"));

        var set = await person.PatchAsJsonAsync($"/api/outcomes/{outcome.Id}", new { value = "20,000" }, Ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("20000", (await JsonAsync(set)).GetProperty("value").GetString());
        Assert.Equal("20000", (await Outcomes.FindAsync(outcome.Id, Ct))!.Value);

        var refused = await person.PatchAsJsonAsync($"/api/outcomes/{outcome.Id}", new { value = "lots" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(OutcomeValue.Refusal, (await JsonAsync(refused)).GetProperty("error").GetString());
        Assert.Equal("20000", (await Outcomes.FindAsync(outcome.Id, Ct))!.Value);

        var cleared = await person.PatchAsJsonAsync($"/api/outcomes/{outcome.Id}", new { value = "" }, Ct);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await Outcomes.FindAsync(outcome.Id, Ct))!.Value);

        var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
        Assert.Equal("No outcome assigned", list.GetProperty("noOutcome").GetProperty("name").GetString());
    }

    // ---- fixtures ----

    private async Task<JsonElement> FiguresAsync(HttpClient person, string outcome) =>
        (await JsonAsync(await person.GetAsync($"/api/outcomes/{outcome}", Ct))).GetProperty("outcome").GetProperty("figures");

    private Task<HttpResponseMessage> SetAsync(HttpClient person, string name, object? value) =>
        person.PutAsJsonAsync("/api/tenant/settings", new Dictionary<string, object?> { [name] = value }, Ct);

    private async Task SetOkAsync(HttpClient person, string name, object value)
    {
        var response = await SetAsync(person, name, value);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static long _correlation = 7_000_000_000 + Random.Shared.Next(1_000_000);

    private static long NextCorrelation() => Interlocked.Add(ref _correlation, 1000);

    private static long _run = 6_000_000_000 + Random.Shared.Next(1_000_000);

    private Task DispatchAsync(string item, long correlation) =>
        ExecuteAsync(
            """
            INSERT INTO backlog_dispatches (item, team_id, team_name, correlation, dispatched_at, dispatched_by)
            VALUES ($item, $team, 'Alpha', $c, $at, $by)
            """,
            ("$item", item), ("$team", host.Alpha), ("$c", correlation), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$by", Email));

    private Task RunAsync(
        long correlation, string member, DateTimeOffset started, DateTimeOffset ended, string outcome,
        DateTimeOffset? queued = null, long billable = 10) =>
        ExecuteAsync(
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, run_outcome,
                queued_at, started_at, ended_at, measured, tokens_in, tokens_out, billable)
            VALUES ($seq, $c, $team, 'Alpha', $member, $outcome, $queued, $started, $ended, 1, $billable, 0, $billable)
            """,
            ("$seq", Interlocked.Increment(ref _run)), ("$c", correlation), ("$team", host.Alpha), ("$member", member),
            ("$outcome", outcome), ("$queued", (object?)queued?.ToString("O") ?? DBNull.Value),
            ("$started", started.ToString("O")), ("$ended", ended.ToString("O")), ("$billable", billable));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync(Ct);
        return result is DBNull ? null : result;
    }
}
