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
/// HUMAN ATTENTION TIME COUNTS MEASURED DURATIONS ONLY, and the other two human figures beside it, through the
/// real read routes: Human Interventions counts each <c>intervention_ledger</c> row once, in one bucket - an
/// outcome followed through <c>merged_into</c>, or Unattributed - on the outcome reads and on the team and
/// all-teams cost reads; Human Attention Time sums only measured durations, and is null - no measured time -
/// when none is; Waiting on a person is the existing blocked time, never added to it; and a member, a trigger,
/// a wake or the Host adds nothing.
/// </summary>
public sealed class HumanFiguresTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Email = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOutcomeStore Outcomes => host.Services.GetRequiredService<IOutcomeStore>();

    private IInterventionLedger Ledger => host.Services.GetRequiredService<IInterventionLedger>();

    private string Database => Path.Combine(host.DataRoot, "messages.db");

    [Fact]
    public async Task Each_intervention_is_counted_once_across_two_teams_sharing_an_outcome()
    {
        var person = await host.PersonAsync();
        var alpha = await NewTeamAsync("Human alpha");
        var beta = await NewTeamAsync("Human beta");
        var shared = await CreateAsync(person, "Human shared");
        var now = DateTimeOffset.UtcNow;

        // A workflow on each team, and one whose acts are on BOTH: keyed by team per outcome, it would count twice.
        var onAlpha = await LinkedAsync(shared.Id, alpha);
        var onBeta = await LinkedAsync(shared.Id, beta);
        var crossing = await LinkedAsync(shared.Id, alpha);
        await RunAsync(onAlpha, alpha, now.AddHours(-2), now.AddHours(-1));
        await RunAsync(onBeta, beta, now.AddHours(-2), now.AddHours(-1));

        await ActAsync(InterventionKinds.Dispatch, alpha, onAlpha, now.AddHours(-3));
        await ActAsync(InterventionKinds.Instruction, alpha, crossing, now.AddMinutes(-50));
        await ActAsync(InterventionKinds.Close, beta, crossing, now.AddMinutes(-40));
        await ActAsync(InterventionKinds.Nudge, beta, onBeta, now.AddMinutes(-30));

        // THE OUTCOME: four rows, each once, every kind counted.
        var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
        var human = OutcomeIn(list, shared.Id).GetProperty("figures").GetProperty("human");
        Assert.Equal(4, human.GetProperty("interventions").GetInt32());
        Assert.Equal(4, (await DetailAsync(person, shared.Id)).GetProperty("figures").GetProperty("human").GetProperty("interventions").GetInt32());

        // EACH TEAM by team_id: two and two, the same four between them - never four on each.
        Assert.Equal(2, EntryHuman(await CostAsync(person, alpha), shared.Id).GetProperty("interventions").GetInt32());
        Assert.Equal(2, EntryHuman(await CostAsync(person, beta), shared.Id).GetProperty("interventions").GetInt32());
        Assert.Equal(2, (await CostAsync(person, alpha)).GetProperty("human").GetProperty("interventions").GetInt32());

        var all = await JsonAsync(await person.GetAsync("/api/teams/cost", Ct));
        var teams = all.GetProperty("teams").EnumerateArray().Where(t => t.GetProperty("team").GetString() is { } id && (id == alpha || id == beta));
        Assert.Equal(4, teams.Sum(t => t.GetProperty("human").GetProperty("interventions").GetInt32()));

        // THE TEAMS LIST SUMMARY: the outcome's four, once, across both teams.
        var summary = await SummaryAsync(person);
        Assert.Equal(4, OutcomeIn(summary, shared.Id).GetProperty("human").GetProperty("interventions").GetInt32());

        // And the all-teams total is every team's row once: the per-team counts add up to it.
        var reached = all.GetProperty("teams").EnumerateArray().Sum(t => t.GetProperty("human").GetProperty("interventions").GetInt32());
        Assert.Equal(reached, summary.GetProperty("human").GetProperty("interventions").GetInt32());
    }

    [Fact]
    public async Task An_intervention_under_a_merged_outcome_follows_merged_into()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Human merge");
        var from = await CreateAsync(person, "Human merged away");
        var into = await CreateAsync(person, "Human merged into");
        var now = DateTimeOffset.UtcNow;

        await ActAsync(InterventionKinds.Dispatch, team, await LinkedAsync(from.Id, team), now.AddMinutes(-20));
        await ActAsync(InterventionKinds.Instruction, team, await LinkedAsync(into.Id, team), now.AddMinutes(-10));

        (await person.PostAsJsonAsync($"/api/outcomes/{from.Id}/merge", new { into = into.Id }, Ct)).EnsureSuccessStatusCode();

        // The row keeps the outcome it was made under; the read follows merged_into, and counts it once, there.
        var list = await JsonAsync(await person.GetAsync("/api/outcomes?status=active,merged", Ct));
        Assert.Equal(2, OutcomeIn(list, into.Id).GetProperty("figures").GetProperty("human").GetProperty("interventions").GetInt32());
        Assert.Equal(0, OutcomeIn(list, from.Id).GetProperty("figures").GetProperty("human").GetProperty("interventions").GetInt32());
        Assert.Equal(2, (await DetailAsync(person, from.Id)).GetProperty("figures").GetProperty("human").GetProperty("interventions").GetInt32());
    }

    [Fact]
    public async Task A_deleted_outcome_and_a_row_with_no_outcome_count_under_Unattributed()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Human gone");
        var deleted = await CreateAsync(person, "Human deleted later");
        var now = DateTimeOffset.UtcNow;
        var before = await UnattributedAsync(person);

        // One act on an outcome then deleted, one on a workflow with no link, one on no workflow at all.
        await ActAsync(InterventionKinds.Dispatch, team, await LinkedAsync(deleted.Id, team), now.AddMinutes(-30));
        await ActAsync(InterventionKinds.Instruction, team, NextCorrelation(), now.AddMinutes(-20));
        await ActAsync(InterventionKinds.CredentialSupplied, team, null, now.AddMinutes(-10));

        (await person.PostAsync($"/api/outcomes/{deleted.Id}/retire", null, Ct)).EnsureSuccessStatusCode();
        (await person.PostAsJsonAsync("/api/outcomes/delete", new { ids = new[] { deleted.Id } }, Ct)).EnsureSuccessStatusCode();

        // The delete's own unlink writes nothing to the ledger here (no person's tenant act through the store), so
        // exactly the three rows land in Unattributed, computed - no outcome row holds them.
        Assert.Equal(before + 3, await UnattributedAsync(person));
        Assert.DoesNotContain(await Outcomes.ListAsync(Ct), o => o.Name == OutcomeFigures.UnattributedName);

        var cost = await CostAsync(person, team);
        Assert.Equal(3, cost.GetProperty("human").GetProperty("interventions").GetInt32());
        Assert.Equal(3, cost.GetProperty("human").GetProperty("unattributed").GetProperty("interventions").GetInt32());
    }

    [Fact]
    public async Task Unmeasured_rows_add_no_attention_time_and_none_measured_is_no_measured_time()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Human measured");
        var outcome = await CreateAsync(person, "Human measured");
        var now = DateTimeOffset.UtcNow;
        var workflow = await LinkedAsync(outcome.Id, team);

        await ActAsync(InterventionKinds.Dispatch, team, workflow, now.AddMinutes(-30));
        await ActAsync(InterventionKinds.Close, team, workflow, now.AddMinutes(-20));

        // Two acts, nothing measured: no measured time - null, never 0.
        var human = (await DetailAsync(person, outcome.Id)).GetProperty("figures").GetProperty("human");
        Assert.Equal(2, human.GetProperty("interventions").GetInt32());
        Assert.Equal(0, human.GetProperty("measuredInterventions").GetInt32());
        Assert.Equal(JsonValueKind.Null, human.GetProperty("attentionSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await CostAsync(person, team)).GetProperty("human").GetProperty("attentionSeconds").ValueKind);

        // One measured session: its 600 s is the whole of the attention time; the two unmeasured add nothing.
        await Ledger.RecordAsync(new InterventionAct(InterventionKinds.ConciergeSession, await PersonIdAsync(), Email, team, workflow, 600), now.AddMinutes(-5), Ct);

        human = (await DetailAsync(person, outcome.Id)).GetProperty("figures").GetProperty("human");
        Assert.Equal(3, human.GetProperty("interventions").GetInt32());
        Assert.Equal(1, human.GetProperty("measuredInterventions").GetInt32());
        Assert.Equal(600, human.GetProperty("attentionSeconds").GetDouble());

        var thisWeek = (await DetailAsync(person, outcome.Id)).GetProperty("figures").GetProperty("humanWeekly").EnumerateArray().Last();
        Assert.Equal(3, thisWeek.GetProperty("interventions").GetInt32());
        Assert.Equal(600, thisWeek.GetProperty("attentionSeconds").GetDouble());
        Assert.Equal(600, (await CostAsync(person, team)).GetProperty("human").GetProperty("attentionSeconds").GetDouble());
    }

    [Fact]
    public async Task Waiting_on_a_person_is_never_added_to_attention_time()
    {
        var person = await host.PersonAsync();
        var team = await NewTeamAsync("Human waiting");
        var outcome = await CreateAsync(person, "Human waiting");
        var now = DateTimeOffset.UtcNow;
        var workflow = await LinkedAsync(outcome.Id, team);

        // A run that ended blocked, and the member's next run an hour later: an hour waiting on a person.
        await RunAsync(workflow, team, now.AddHours(-3), now.AddHours(-2), outcome: "blocked");
        await RunAsync(workflow, team, now.AddHours(-1), now.AddMinutes(-30));
        await Ledger.RecordAsync(new InterventionAct(InterventionKinds.ConciergeSession, await PersonIdAsync(), Email, team, workflow, 120), now.AddMinutes(-90), Ct);

        var figures = (await DetailAsync(person, outcome.Id)).GetProperty("figures");
        Assert.Equal(3600, figures.GetProperty("blockedSeconds").GetDouble(), 3);
        Assert.Equal(120, figures.GetProperty("human").GetProperty("attentionSeconds").GetDouble());

        var human = (await CostAsync(person, team)).GetProperty("human");
        Assert.Equal(3600, human.GetProperty("waitingSeconds").GetDouble(), 3);
        Assert.Equal(120, human.GetProperty("attentionSeconds").GetDouble());

        // No property anywhere joins the two.
        foreach (var read in new[] { figures.GetRawText(), human.GetRawText(), (await SummaryAsync(person)).GetRawText() })
        {
            Assert.DoesNotContain("attentionAndWaiting", read, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("humanSeconds", read, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Members_triggers_wakes_and_the_host_add_nothing_and_a_persons_tell_adds_one()
    {
        using var person = await host.PersonAsync();
        var before = await TeamInterventionsAsync(person, host.Alpha);

        // A member's tell through the real route, and the Host's own appends from each machine source.
        using var member = host.Container(host.AlphaContainerKey);
        await TellAsync(member, new { instruction = "From a member.", kind = InterventionKinds.Instruction, durationSeconds = 999 });
        foreach (var source in new[] { "schedule:7", "trigger:8", "wake:9", "host" })
        {
            await host.Services.GetRequiredService<IMessageLog>().AppendAsync(
                new NewMessage(
                    MessageTypes.InstructionFor(new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName)),
                    """{"instruction":"fire"}""", source),
                Ct);
        }

        Assert.Equal(before, await TeamInterventionsAsync(person, host.Alpha));

        // A person's tell is the control: it counts, once.
        await TellAsync(person, new { instruction = "From a person." });
        Assert.Equal(before + 1, await TeamInterventionsAsync(person, host.Alpha));
    }

    // ---- helpers ----

    private async Task<int> TeamInterventionsAsync(HttpClient person, string team) =>
        (await CostAsync(person, team)).GetProperty("human").GetProperty("interventions").GetInt32();

    private async Task TellAsync(HttpClient client, object body)
    {
        var told = await client.PostAsJsonAsync($"/api/teams/{host.Alpha}/containers/{TeamRegistry.DefaultManagerName}/tell", body, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>A person's act, written by the ledger's one insert - the outcome is the workflow's newest link then.</summary>
    private async Task ActAsync(string kind, string team, long? correlation, DateTimeOffset at) =>
        await Ledger.RecordAsync(new InterventionAct(kind, await PersonIdAsync(), Email, team, correlation), at, Ct);

    private async Task<string> PersonIdAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM users WHERE email = $email";
        command.Parameters.AddWithValue("$email", Email);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static JsonElement OutcomeIn(JsonElement read, string id) =>
        read.GetProperty("outcomes").EnumerateArray().Single(o => o.GetProperty("id").GetString() == id);

    private static JsonElement EntryHuman(JsonElement cost, string id) => OutcomeIn(cost, id).GetProperty("human");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private async Task<JsonElement> DetailAsync(HttpClient person, string id) =>
        (await JsonAsync(await person.GetAsync($"/api/outcomes/{id}", Ct))).GetProperty("outcome");

    private async Task<int> UnattributedAsync(HttpClient person) =>
        (await JsonAsync(await person.GetAsync("/api/outcomes", Ct))).GetProperty("noOutcome").GetProperty("figures")
            .GetProperty("human").GetProperty("interventions").GetInt32();

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

    private static long _correlation = 8_000_000_000 + Random.Shared.Next(1_000_000);

    private static long NextCorrelation() => Interlocked.Add(ref _correlation, 1000);

    private static long _run = 9_000_000_000 + Random.Shared.Next(1_000_000);

    /// <summary>A ledger row for an agent run from before snapshots, ending as <paramref name="outcome"/> says.</summary>
    private async Task RunAsync(long correlation, string team, DateTimeOffset started, DateTimeOffset ended, string outcome = "completed")
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, member_kind, run_outcome,
                started_at, ended_at, measured, tokens_in, tokens_out, billable)
            VALUES ($seq, $c, $team, $team, 'Dev', 'agent', $outcome, $started, $ended, 1, 10, 0, 10)
            """;
        command.Parameters.AddWithValue("$seq", Interlocked.Increment(ref _run));
        command.Parameters.AddWithValue("$c", correlation);
        command.Parameters.AddWithValue("$team", team);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$started", started.ToString("O"));
        command.Parameters.AddWithValue("$ended", ended.ToString("O"));
        await command.ExecuteNonQueryAsync(Ct);
    }
}
