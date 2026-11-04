using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// AN OUTCOME TOTAL COUNTS EACH RUN ONCE. Every outcome figure - <c>GET /api/outcomes</c>, the Teams list
/// summary's "Where it goes", each team's by-outcome entries and <c>outcomeTotalCost</c> - is a sum of
/// <see cref="ExecutionCost"/>'s per-run estimates keyed by the run, never the team: a run is in exactly one
/// bucket, an outcome (followed through <c>merged_into</c>) or Unattributed, and Unattributed plus every
/// outcome is the period's Estimated Execution Cost to the cent. And how a person reads an outcome -
/// <c>presentation</c>, project or operational - is theirs to set, through the outcome edit route alone.
/// </summary>
public sealed class OutcomeRollupTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Email = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOutcomeStore Outcomes => host.Services.GetRequiredService<IOutcomeStore>();

    private string Database => Path.Combine(host.DataRoot, "messages.db");

    // ---- each run once, in one bucket ----

    [Fact]
    public async Task A_run_is_counted_once_across_two_teams_sharing_an_outcome()
    {
        var person = await host.PersonAsync();
        var alpha = await NewTeamAsync("Rollup alpha");
        var beta = await NewTeamAsync("Rollup beta");
        var shared = await CreateAsync(person, "Shared rollup");
        var now = DateTimeOffset.UtcNow;

        // One workflow per team on the outcome, and one workflow whose runs are on BOTH teams: keyed by the
        // team, its outcome would be counted twice; keyed by the run, once.
        var onAlpha = await LinkedAsync(shared.Id, alpha);
        var onBeta = await LinkedAsync(shared.Id, beta);
        var crossing = await LinkedAsync(shared.Id, alpha);
        await RunAsync(onAlpha, alpha, now.AddHours(-2), now.AddHours(-1), rate: "60");
        await RunAsync(onBeta, beta, now.AddHours(-3), now.AddHours(-1), rate: "60");
        await RunAsync(crossing, alpha, now.AddMinutes(-90), now.AddMinutes(-60), rate: "60");
        await RunAsync(crossing, beta, now.AddMinutes(-60), now.AddMinutes(-30), rate: "60");

        // 1 h + 2 h + 0.5 h + 0.5 h at 60/h, each run once.
        var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
        var figures = OutcomeIn(list, shared.Id).GetProperty("figures");
        Assert.Equal(240m, figures.GetProperty("cost").GetProperty("amount").GetDecimal());
        Assert.Equal(60m, figures.GetProperty("cost").GetProperty("agentRate").GetDecimal());
        Assert.Equal(4, figures.GetProperty("runs").GetInt32());
        Assert.Equal(4 * 3600, figures.GetProperty("agentSeconds").GetDouble(), 3);

        // Each team's entry is its own runs; together they are the outcome's, and each says the same total.
        var alphaEntry = OutcomeIn(await CostAsync(person, alpha), shared.Id);
        var betaEntry = OutcomeIn(await CostAsync(person, beta), shared.Id);
        Assert.Equal(90m, alphaEntry.GetProperty("totals").GetProperty("cost").GetDecimal());
        Assert.Equal(150m, betaEntry.GetProperty("totals").GetProperty("cost").GetDecimal());
        Assert.Equal(240m, alphaEntry.GetProperty("outcomeTotalCost").GetDecimal());
        Assert.Equal(240m, betaEntry.GetProperty("outcomeTotalCost").GetDecimal());

        // "Where it goes" on the Teams list: the outcome once, across both teams.
        var summary = await SummaryAsync(person);
        var whereItGoes = OutcomeIn(summary, shared.Id);
        Assert.Equal(240m, whereItGoes.GetProperty("cost").GetDecimal());
        Assert.Equal(240m, whereItGoes.GetProperty("outcomeTotalCost").GetDecimal());

        // The detail answers the same figure the list does.
        var detail = await JsonAsync(await person.GetAsync($"/api/outcomes/{shared.Id}", Ct));
        Assert.Equal(240m, detail.GetProperty("outcome").GetProperty("figures").GetProperty("cost").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task A_merged_outcomes_runs_follow_merged_into_and_are_counted_once_there()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Rollup merge");
        var from = await CreateAsync(person, "Merged away");
        var into = await CreateAsync(person, "Merged into");
        var now = DateTimeOffset.UtcNow;

        await RunAsync(await LinkedAsync(from.Id, team), team, now.AddHours(-2), now.AddHours(-1), rate: "60");
        await RunAsync(await LinkedAsync(into.Id, team), team, now.AddHours(-3), now.AddHours(-1), rate: "60");

        // The preview says what would move: the merged outcome's one run, by its figures.
        var preview = await JsonAsync(await person.PostAsJsonAsync($"/api/outcomes/{from.Id}/merge?preview=true", new { into = into.Id }, Ct));
        Assert.Equal(1, preview.GetProperty("moves").GetProperty("figures").GetProperty("runs").GetInt32());

        (await person.PostAsJsonAsync($"/api/outcomes/{from.Id}/merge", new { into = into.Id }, Ct)).EnsureSuccessStatusCode();

        var list = await JsonAsync(await person.GetAsync("/api/outcomes?status=active,merged", Ct));
        Assert.Equal(180m, OutcomeIn(list, into.Id).GetProperty("figures").GetProperty("cost").GetProperty("amount").GetDecimal());
        Assert.Equal(2, OutcomeIn(list, into.Id).GetProperty("figures").GetProperty("runs").GetInt32());
        // The merged one holds nothing of its own now: its run is counted where it was merged, once.
        Assert.Equal(0, OutcomeIn(list, from.Id).GetProperty("figures").GetProperty("runs").GetInt32());

        var entries = (await CostAsync(person, team)).GetProperty("outcomes").EnumerateArray().ToList();
        var entry = Assert.Single(entries, e => e.GetProperty("id").GetString() == into.Id);
        Assert.Equal(180m, entry.GetProperty("totals").GetProperty("cost").GetDecimal());
        Assert.DoesNotContain(entries, e => e.GetProperty("id").GetString() == from.Id);

        var summary = await SummaryAsync(person);
        Assert.Equal(180m, OutcomeIn(summary, into.Id).GetProperty("cost").GetDecimal());
        Assert.DoesNotContain(summary.GetProperty("outcomes").EnumerateArray(), o => o.GetProperty("id").GetString() == from.Id);
    }

    [Fact]
    public async Task A_deleted_outcome_and_an_unlink_go_to_Unattributed_and_a_later_unlink_does_not_move_a_started_run()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Rollup gone");
        var deleted = await CreateAsync(person, "Deleted later");
        var left = await CreateAsync(person, "Unlinked from");
        var now = DateTimeOffset.UtcNow;
        var before = await NoOutcomeCostAsync(person);

        // A snapshotted run on an outcome that is then retired and deleted: 1 h.
        await RunAsync(await LinkedAsync(deleted.Id, team), team, now.AddHours(-5), now.AddHours(-4), rate: "60");

        // A workflow a person unlinked before its run started: the snapshot names the unlink. 0.5 h.
        var unlinked = await LinkedAsync(left.Id, team);
        await UnlinkAsync(unlinked, team);
        await RunAsync(unlinked, team, now.AddMinutes(-240), now.AddMinutes(-210), rate: "60");

        // A run from before snapshots whose workflow's newest link is an unlink: 0.25 h, at no rate it has.
        var legacy = await LinkedAsync(left.Id, team);
        await RunAsync(legacy, team, now.AddMinutes(-200), now.AddMinutes(-185));
        await UnlinkAsync(legacy, team);

        // A workflow with no link at all: 0.5 h.
        await RunAsync(NextCorrelation(), team, now.AddMinutes(-180), now.AddMinutes(-150), rate: "60");

        // A snapshotted run that STARTED linked, unlinked afterwards: it keeps the outcome it started for. 1 h.
        var kept = await LinkedAsync(left.Id, team);
        await RunAsync(kept, team, now.AddHours(-2), now.AddHours(-1), rate: "60");
        await UnlinkAsync(kept, team);

        (await person.PostAsync($"/api/outcomes/{deleted.Id}/retire", null, Ct)).EnsureSuccessStatusCode();
        (await person.PostAsJsonAsync("/api/outcomes/delete", new { ids = new[] { deleted.Id } }, Ct)).EnsureSuccessStatusCode();
        Assert.Null(await Outcomes.FindAsync(deleted.Id, Ct));

        // The team's Unattributed entry: the deleted outcome's run, the unlinked one and the unlinked one.
        var entries = (await CostAsync(person, team)).GetProperty("outcomes").EnumerateArray().ToList();
        var unattributed = entries[0];
        Assert.Equal(JsonValueKind.Null, unattributed.GetProperty("id").ValueKind);
        Assert.Equal("Unattributed", unattributed.GetProperty("name").GetString());
        Assert.Equal(120m, unattributed.GetProperty("totals").GetProperty("cost").GetDecimal());
        Assert.Equal((1 + 0.5 + 0.25 + 0.5) * 3600, unattributed.GetProperty("totals").GetProperty("agentSeconds").GetDouble(), 3);
        Assert.DoesNotContain(entries, e => e.GetProperty("id").GetString() == deleted.Id);
        Assert.Equal(60m, Assert.Single(entries, e => e.GetProperty("id").GetString() == left.Id).GetProperty("totals").GetProperty("cost").GetDecimal());

        // The Outcomes dialog's Unattributed carries the same money, computed: no outcome row holds it.
        var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
        Assert.Equal("Unattributed", list.GetProperty("noOutcome").GetProperty("name").GetString());
        Assert.Equal(before + 120m, await NoOutcomeCostAsync(person));
        Assert.DoesNotContain(await Outcomes.ListAsync(Ct), o => o.Name == "Unattributed");

        var summary = await SummaryAsync(person);
        var bucket = summary.GetProperty("outcomes")[0];
        Assert.Equal(JsonValueKind.Null, bucket.GetProperty("id").ValueKind);
        Assert.Equal("Unattributed", bucket.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Unattributed_and_every_outcome_add_up_to_the_periods_estimate_to_the_cent()
    {
        var person = await host.PersonAsync();
        var alpha = await NewTeamAsync("Identity alpha");
        var beta = await NewTeamAsync("Identity beta");
        var shared = await CreateAsync(person, "Identity shared");
        var other = await CreateAsync(person, "Identity other");
        var now = DateTimeOffset.UtcNow;
        var lastWeek = OutcomeFigures.WeekOf(now).AddDays(-7);

        // Odd seconds at an odd rate, so every part rounds; one run crosses a week, so it is split and each
        // part rounded on its own; two teams share one outcome; and some work serves none.
        var onAlpha = await LinkedAsync(shared.Id, alpha);
        var onBeta = await LinkedAsync(shared.Id, beta);
        await RunAsync(onAlpha, alpha, lastWeek.AddSeconds(-1237), lastWeek.AddSeconds(4321), rate: "47.37");
        await RunAsync(onBeta, beta, now.AddSeconds(-7777), now.AddSeconds(-1111), rate: "33.33");
        await RunAsync(onBeta, alpha, now.AddSeconds(-999), now.AddSeconds(-1), rate: "47.37");
        await RunAsync(await LinkedAsync(other.Id, beta), beta, now.AddSeconds(-5003), now.AddSeconds(-2), rate: "71.11");
        await RunAsync(NextCorrelation(), alpha, now.AddSeconds(-3333), now.AddSeconds(-17), rate: "19.99");
        await RunAsync(NextCorrelation(), beta, lastWeek.AddSeconds(-61), lastWeek.AddSeconds(59), rate: "88.88");

        static decimal Sum(IEnumerable<JsonElement> entries, Func<JsonElement, JsonElement> cost) =>
            entries.Sum(e => cost(e) is { ValueKind: JsonValueKind.Number } c ? c.GetDecimal() : 0m);

        // THE TEAMS LIST: Where it goes - Unattributed and every outcome - is the summary's Estimated Execution Cost.
        var summary = await SummaryAsync(person);
        var total = summary.GetProperty("total").GetProperty("cost").GetDecimal();
        Assert.Equal(total, Sum(summary.GetProperty("outcomes").EnumerateArray(), o => o.GetProperty("cost")));
        Assert.Contains(summary.GetProperty("outcomes").EnumerateArray(), o => o.GetProperty("id").ValueKind == JsonValueKind.Null);

        // EACH TEAM, and the all-teams read: its entries are its total, and the teams' totals are the summary's.
        var teams = new List<decimal>();
        foreach (var team in new[] { alpha, beta })
        {
            var read = await CostAsync(person, team);
            var teamTotal = read.GetProperty("totals").GetProperty("cost").GetDecimal();
            Assert.Equal(teamTotal, Sum(read.GetProperty("outcomes").EnumerateArray(), e => e.GetProperty("totals").GetProperty("cost")));
            teams.Add(teamTotal);
        }

        var all = await JsonAsync(await person.GetAsync("/api/teams/cost", Ct));
        Assert.Equal(total, Sum(all.GetProperty("teams").EnumerateArray(), t => t.GetProperty("totals").GetProperty("cost")));
        Assert.Contains(teams, t => t > 0);

        // THE OUTCOMES DIALOG, all time: every outcome's cost and Unattributed's are every run's estimate, once.
        var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
        var buckets = Sum(list.GetProperty("outcomes").EnumerateArray(), o => o.GetProperty("figures").GetProperty("cost").GetProperty("amount"))
            + (list.GetProperty("noOutcome").GetProperty("figures").GetProperty("cost").GetProperty("amount") is { ValueKind: JsonValueKind.Number } n ? n.GetDecimal() : 0m);
        var runs = ExecutionCost.Of(await Outcomes.ReadExecutionRunsAsync(null, null, pricedOnly: false, Ct), await Outcomes.ListAsync(Ct), "USD");
        Assert.Equal(runs.Sum(r => r.Currency == "USD" ? r.Estimate ?? 0m : 0m), buckets);
    }

    // ---- throughput, for an operational outcome ----

    [Fact]
    public async Task An_outcomes_throughput_counts_its_workflows_closed_in_each_of_the_eight_weeks()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Rollup throughput");
        var outcome = await CreateAsync(person, "Throughput");
        var now = DateTimeOffset.UtcNow;
        var thisWeek = OutcomeFigures.WeekOf(now);

        var done = await LinkedAsync(outcome.Id, team);
        var closed = await LinkedAsync(outcome.Id, team);
        var earlier = await LinkedAsync(outcome.Id, team);
        await LinkedAsync(outcome.Id, team);
        await CloseAsync(done, team, thisWeek.AddMinutes(1), "completed");
        await CloseAsync(closed, team, thisWeek.AddMinutes(2), "closed");
        await CloseAsync(earlier, team, thisWeek.AddDays(-14).AddHours(1), "completed");

        var figures = (await JsonAsync(await person.GetAsync($"/api/outcomes/{outcome.Id}", Ct))).GetProperty("outcome").GetProperty("figures");
        var throughput = figures.GetProperty("throughput").EnumerateArray().ToList();

        Assert.Equal(
            figures.GetProperty("weekly").EnumerateArray().Select(w => w.GetProperty("weekStart").GetDateTimeOffset()),
            throughput.Select(w => w.GetProperty("weekStart").GetDateTimeOffset()));
        Assert.Equal(2, throughput[^1].GetProperty("closed").GetInt32());
        Assert.Equal(1, throughput[^1].GetProperty("completed").GetInt32());
        Assert.Equal(1, throughput[^3].GetProperty("closed").GetInt32());
        Assert.Equal(1, throughput[^3].GetProperty("completed").GetInt32());
        Assert.Equal(3, throughput.Sum(w => w.GetProperty("closed").GetInt32()));
    }

    // ---- presentation: a person's choice ----

    [Fact]
    public async Task A_person_sets_presentation_through_the_edit_route_and_its_tenant_row_names_the_old_and_new_value()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, "Presented");

        // Never picked: stored NULL, read as project.
        Assert.Null(outcome.Presentation);
        Assert.Equal(OutcomePresentation.Project, OutcomePresentation.Read(outcome.Presentation));
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(person, outcome.Id)).GetProperty("presentation").ValueKind);

        var set = await person.PatchAsJsonAsync($"/api/outcomes/{outcome.Id}", new { presentation = "operational" }, Ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("operational", (await JsonAsync(set)).GetProperty("presentation").GetString());
        Assert.Equal("operational", (await Outcomes.FindAsync(outcome.Id, Ct))!.Presentation);
        Assert.Equal("operational", (await DetailAsync(person, outcome.Id)).GetProperty("presentation").GetString());
        Assert.Equal("operational", OutcomeIn(await JsonAsync(await person.GetAsync("/api/outcomes", Ct)), outcome.Id).GetProperty("presentation").GetString());

        (await person.PatchAsJsonAsync($"/api/outcomes/{outcome.Id}", new { presentation = "project" }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal("project", (await Outcomes.FindAsync(outcome.Id, Ct))!.Presentation);

        // The same value again is no change, and writes no row.
        (await person.PatchAsJsonAsync($"/api/outcomes/{outcome.Id}", new { presentation = "project" }, Ct)).EnsureSuccessStatusCode();

        var rows = await ChangedRowsAsync(outcome.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(TenantActions.OutcomeChanged, r.Action));
        Assert.All(rows, r => Assert.Equal(Email, r.ActorEmail));
        Assert.Equal(JsonValueKind.Null, rows[0].Detail.GetProperty("presentation").GetProperty("old").ValueKind);
        Assert.Equal("operational", rows[0].Detail.GetProperty("presentation").GetProperty("new").GetString());
        Assert.Equal("operational", rows[1].Detail.GetProperty("presentation").GetProperty("old").GetString());
        Assert.Equal("project", rows[1].Detail.GetProperty("presentation").GetProperty("new").GetString());

        // A rename with it is one renamed row naming both.
        var name = $"Presented again {Guid.NewGuid():N}";
        (await person.PatchAsJsonAsync($"/api/outcomes/{outcome.Id}", new { name, presentation = "operational" }, Ct)).EnsureSuccessStatusCode();
        var renamed = (await ChangedRowsAsync(outcome.Id, TenantActions.OutcomeRenamed)).Single();
        Assert.Equal(name, renamed.SubjectName);
        Assert.Equal("operational", renamed.Detail.GetProperty("presentation").GetProperty("new").GetString());
    }

    [Fact]
    public async Task Presentation_is_refused_to_a_member_a_member_key_an_agent_and_a_concierge_and_nothing_is_stored()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, "Not theirs");
        var principals = host.Services.GetRequiredService<IPrincipalStore>();
        var manager = await principals.MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);
        var concierge = await principals.MintAsync(
            $"console:{Guid.NewGuid():N}", PrincipalKind.Concierge, host.Alpha, Permits.All, ct: Ct);

        foreach (var key in new[] { host.AlphaContainerKey, manager, concierge })
        {
            using var agent = host.Container(key);
            var refused = await agent.PatchAsJsonAsync($"/api/outcomes/{outcome.Id}", new { presentation = "operational" }, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Null((await Outcomes.FindAsync(outcome.Id, Ct))!.Presentation);
        Assert.Empty(await ChangedRowsAsync(outcome.Id));
    }

    [Fact]
    public async Task An_unknown_presentation_is_a_400_that_stores_nothing()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, "Unknown presentation");

        foreach (var value in new[] { "Operational", "weekly", "", " project" })
        {
            var refused = await person.PatchAsJsonAsync(
                $"/api/outcomes/{outcome.Id}", new { name = $"Never {Guid.NewGuid():N}", presentation = value }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(OutcomePresentation.Refusal, (await JsonAsync(refused)).GetProperty("error").GetString());
        }

        var stored = (await Outcomes.FindAsync(outcome.Id, Ct))!;
        Assert.Null(stored.Presentation);
        Assert.Equal(outcome.Name, stored.Name);
        Assert.Empty(await ChangedRowsAsync(outcome.Id));
        Assert.Empty(await ChangedRowsAsync(outcome.Id, TenantActions.OutcomeRenamed));
    }

    // ---- helpers ----

    private static JsonElement OutcomeIn(JsonElement read, string id) =>
        read.GetProperty("outcomes").EnumerateArray().Single(o => o.GetProperty("id").GetString() == id);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode || (int)response.StatusCode >= 400, text);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private async Task<JsonElement> DetailAsync(HttpClient person, string id) =>
        (await JsonAsync(await person.GetAsync($"/api/outcomes/{id}", Ct))).GetProperty("outcome");

    private async Task<decimal> NoOutcomeCostAsync(HttpClient person) =>
        (await JsonAsync(await person.GetAsync("/api/outcomes", Ct))).GetProperty("noOutcome").GetProperty("figures")
            .GetProperty("cost").GetProperty("amount") is { ValueKind: JsonValueKind.Number } amount ? amount.GetDecimal() : 0m;

    private async Task<JsonElement> SummaryAsync(HttpClient person) =>
        await JsonAsync(await person.GetAsync("/api/teams/cost/summary", Ct));

    private async Task<JsonElement> CostAsync(HttpClient person, string team) =>
        await JsonAsync(await person.GetAsync($"/api/teams/{team}/cost", Ct));

    private async Task<string> NewTeamAsync(string label)
    {
        var agent = host.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        return (await host.Services.GetRequiredService<TeamRegistry>()
            .CreateAsync($"{label} {Guid.NewGuid():N}"[..24], agent, memberAgent: agent, ct: Ct)).Id;
    }

    private async Task<Outcome> CreateAsync(HttpClient person, string name)
    {
        var created = await person.PostAsJsonAsync("/api/outcomes", new { name = $"{name} {Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await Outcomes.FindAsync((await JsonAsync(created)).GetProperty("id").GetString()!, Ct))!;
    }

    private async Task<long> LinkedAsync(string outcome, string team)
    {
        var correlation = NextCorrelation();
        Assert.True((await Outcomes.LinkAsync(correlation, outcome, team, new OutcomeActor(Email, OutcomeActorKind.Person),
            OutcomeLinkHow.Person, agentRule: false,
            new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Ok);
        return correlation;
    }

    private async Task UnlinkAsync(long correlation, string team) =>
        Assert.True((await Outcomes.UnlinkAsync(correlation, team, new OutcomeActor(Email, OutcomeActorKind.Person),
            new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Ok);

    private sealed record ChangedRow(string Action, string? ActorEmail, string? SubjectName, JsonElement Detail);

    private async Task<List<ChangedRow>> ChangedRowsAsync(string outcome, string action = TenantActions.OutcomeChanged)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT action, actor_email, subject_name, detail FROM tenant_events WHERE subject = $id AND action = $action ORDER BY seq";
        command.Parameters.AddWithValue("$id", outcome);
        command.Parameters.AddWithValue("$action", action);

        var rows = new List<ChangedRow>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new ChangedRow(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                JsonSerializer.Deserialize<JsonElement>(reader.IsDBNull(3) ? "{}" : reader.GetString(3))));
        }

        return rows;
    }

    private static long _correlation = 7_000_000_000 + Random.Shared.Next(1_000_000);

    private static long NextCorrelation() => Interlocked.Add(ref _correlation, 1000);

    private static long _run = 6_000_000_000 + Random.Shared.Next(1_000_000);

    /// <summary>
    /// A ledger row for an agent run. With a <paramref name="rate"/> it names an execution snapshot written as
    /// the writer writes one at start: that rate and the workflow's newest link then. Without, it is a run
    /// from before snapshots.
    /// </summary>
    private async Task RunAsync(long correlation, string team, DateTimeOffset started, DateTimeOffset ended, string? rate = null)
    {
        object start = DBNull.Value;
        if (rate is not null)
        {
            start = Interlocked.Increment(ref _run);
            await ExecuteAsync(
                """
                INSERT INTO execution_snapshots (start_seq, correlation, team_id, team_name, member, member_kind,
                    rate_amount, rate_currency, rate_source, link_id, started_at)
                VALUES ($start, $c, $team, $team, 'Dev', 'agent', $rate, 'USD', 'member',
                    (SELECT MAX(id) FROM workflow_outcome_links WHERE correlation = $c), $started)
                """,
                ("$start", start), ("$c", correlation), ("$team", team), ("$rate", rate), ("$started", started.ToString("O")));
        }

        await ExecuteAsync(
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, member_kind, run_outcome,
                started_at, ended_at, measured, tokens_in, tokens_out, billable, start_seq)
            VALUES ($seq, $c, $team, $team, 'Dev', 'agent', 'completed', $started, $ended, 1, 10, 0, 10, $start)
            """,
            ("$seq", Interlocked.Increment(ref _run)), ("$c", correlation), ("$team", team),
            ("$started", started.ToString("O")), ("$ended", ended.ToString("O")), ("$start", start));
    }

    private Task CloseAsync(long correlation, string team, DateTimeOffset closed, string how) =>
        ExecuteAsync(
            """
            INSERT INTO workflow_ledger (close_seq, correlation, team_id, team_name, root_at, closed_at, how_closed)
            VALUES ($seq, $c, $team, $team, $root, $closed, $how)
            """,
            ("$seq", Interlocked.Increment(ref _run)), ("$c", correlation), ("$team", team),
            ("$root", closed.AddHours(-1).ToString("O")), ("$closed", closed.ToString("O")), ("$how", how));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
