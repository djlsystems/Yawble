using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A PERSON DELETES ENDED OUTCOMES, on the real Host: only retired and merged ones, every outcome
/// merged into one going with it, each workflow taken off by an unlink row (the old links stay), the
/// backlog items and triggers that named one cleared, one tenant row per outcome and per workflow,
/// and a preview that changes nothing. Readers that meet an id no outcome has any more count it under
/// No outcome. The tenant-row failure is <see cref="OutcomeAuditTests"/>', which takes the table away.
/// </summary>
public sealed class OutcomeDeleteTests(HostFixture host) : IClassFixture<HostFixture>
{
    private const string Email = "person@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOutcomeStore Outcomes => host.Services.GetRequiredService<IOutcomeStore>();

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

    private static async Task RetireAsync(HttpClient person, Outcome outcome) =>
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsync($"/api/outcomes/{outcome.Id}/retire", null, Ct)).StatusCode);

    private static async Task MergeAsync(HttpClient person, Outcome outcome, Outcome into) =>
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync($"/api/outcomes/{outcome.Id}/merge", new { into = into.Id }, Ct)).StatusCode);

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, bool preview, params string[] ids) =>
        client.PostAsJsonAsync($"/api/outcomes/delete{(preview ? "?preview=true" : "")}", new { ids }, Ct);

    private async Task<JsonElement> ListAsync(HttpClient person, string? status = null) =>
        await JsonAsync(await person.GetAsync($"/api/outcomes{(status is null ? "" : $"?status={status}")}", Ct));

    private static JsonElement? Find(JsonElement list, string id) =>
        list.GetProperty("outcomes").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(o => o!.Value.GetProperty("id").GetString() == id);

    private static int NoOutcomeTotal(JsonElement list) =>
        list.GetProperty("noOutcome").GetProperty("figures").GetProperty("workflows").GetProperty("total").GetInt32();

    [Fact]
    public async Task Deleting_a_retired_outcome_unlinks_its_workflows_keeps_their_links_clears_its_item_and_trigger_and_records_each()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Retired openings"));

        var workflows = new[] { NextCorrelation(), NextCorrelation(), NextCorrelation() };
        foreach (var workflow in workflows) await LinkAsync(workflow, outcome.Id, host.Alpha);

        var item = (await JsonAsync(await person.PostAsJsonAsync("/api/backlog", new { title = "Crawl openings", body = "spec" }, Ct)))
            .GetProperty("id").GetInt64();
        await person.PatchAsJsonAsync($"/api/backlog/{item}", new { outcomeId = outcome.Id }, Ct);
        var trigger = (await JsonAsync(await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/triggers", new
        {
            name = Unique("crawl"), kind = "every", intervalSeconds = 3600, instruction = "crawl", idleOnly = false, outcomeId = outcome.Id,
        }, Ct))).GetProperty("id").GetString()!;
        Assert.Equal(outcome.Id, await ColumnAsync("backlog_items", PlatformBacklogId.Format(item)));
        Assert.Equal(outcome.Id, await ColumnAsync("triggers", trigger));

        await RetireAsync(person, outcome);

        // THE ENDED ROW'S COLUMNS: three workflows reach it, and it ended when it was retired.
        var ended = Find(await ListAsync(person, "retired,merged"), outcome.Id)!.Value;
        Assert.Equal(3, ended.GetProperty("linkedWorkflows").GetInt32());
        Assert.Equal(JsonValueKind.String, ended.GetProperty("endedAt").ValueKind);

        var noneBefore = NoOutcomeTotal(await ListAsync(person));
        var linksBefore = await Outcomes.ReadLinksAsync(Ct);
        var head = await TenantHeadAsync();

        var deleted = await DeleteAsync(person, preview: false, outcome.Id);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var answer = await JsonAsync(deleted);
        Assert.False(answer.GetProperty("preview").GetBoolean());
        Assert.Equal((3, 1, 1), (answer.GetProperty("workflowsUnlinked").GetInt32(),
            answer.GetProperty("backlogItemsCleared").GetInt32(), answer.GetProperty("triggersCleared").GetInt32()));

        // THE OUTCOME IS GONE: no row, 404 by id, absent from the list.
        Assert.Null(await Outcomes.FindAsync(outcome.Id, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await person.GetAsync($"/api/outcomes/{outcome.Id}", Ct)).StatusCode);
        Assert.Null(Find(await ListAsync(person, "proposed,active,retired,merged"), outcome.Id));

        // EACH WORKFLOW HAS AN UNLINK ROW, the person's, and its old link stays where it was.
        var linksAfter = await Outcomes.ReadLinksAsync(Ct);
        Assert.Equal(linksBefore, linksAfter.Take(linksBefore.Count));
        foreach (var workflow in workflows)
        {
            var current = (await Outcomes.CurrentLinkAsync(workflow, Ct))!;
            Assert.Equal((true, OutcomeLinkHow.Person, Email), (current.IsUnlink, current.How, current.SetBy));
            Assert.Contains(linksAfter, l => l.Correlation == workflow && l.OutcomeId == outcome.Id);
        }

        Assert.Equal(noneBefore + 3, NoOutcomeTotal(await ListAsync(person)));
        Assert.Null(await ColumnAsync("backlog_items", PlatformBacklogId.Format(item)));
        Assert.Null(await ColumnAsync("triggers", trigger));

        // ONE outcome.deleted ROW AND THREE workflow.outcome-changed ROWS, each to no outcome.
        var rows = await TenantRowsAsync(head);
        var gone = Assert.Single(rows, r => r.Action == TenantActions.OutcomeDeleted);
        Assert.Equal((outcome.Id, outcome.Name), (gone.Subject, gone.SubjectName));
        var detail = JsonDocument.Parse(gone.Detail!).RootElement;
        Assert.Equal((OutcomeStatus.Retired, 3), (detail.GetProperty("status").GetString(), detail.GetProperty("workflowsUnlinked").GetInt32()));
        Assert.Equal([PlatformBacklogId.Format(item)], detail.GetProperty("backlogItems").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([trigger], detail.GetProperty("triggers").EnumerateArray().Select(e => e.GetString()));

        var changed = rows.Where(r => r.Action == TenantActions.WorkflowOutcomeChanged).ToList();
        Assert.Equal(3, changed.Count);
        Assert.All(changed, r =>
        {
            var d = JsonDocument.Parse(r.Detail!).RootElement;
            Assert.Equal(outcome.Id, r.Subject);
            Assert.Equal(outcome.Id, d.GetProperty("from").GetString());
            Assert.Equal(JsonValueKind.Null, d.GetProperty("to").ValueKind);
        });
        Assert.Equal(workflows.Order(), changed.Select(r => JsonDocument.Parse(r.Detail!).RootElement.GetProperty("workflow").GetInt64()).Order());

        // ITS NAME IS FREE AGAIN.
        await CreateAsync(person, outcome.Name);
    }

    [Fact]
    public async Task Deleting_a_merged_outcome_takes_its_workflows_off_the_outcome_it_merged_into_and_that_outcomes_figures_drop_by_theirs()
    {
        var person = await host.PersonAsync();
        var merged = await CreateAsync(person, Unique("Old wording"));
        var into = await CreateAsync(person, Unique("New wording"));

        var mine = new[] { NextCorrelation(), NextCorrelation() };
        foreach (var workflow in mine)
        {
            await LinkAsync(workflow, merged.Id, host.Alpha);
            await RunAsync(workflow, host.Alpha, "Alpha", At(0), At(10), billable: 100);
        }

        var kept = NextCorrelation();
        await LinkAsync(kept, into.Id, host.Alpha);
        await RunAsync(kept, host.Alpha, "Alpha", At(0), At(10), billable: 5);

        await MergeAsync(person, merged, into);

        var before = Find(await ListAsync(person), into.Id)!.Value.GetProperty("figures");
        Assert.Equal((3, 205L), (before.GetProperty("workflows").GetProperty("total").GetInt32(), before.GetProperty("tokens").GetProperty("billable").GetInt64()));
        Assert.Equal(3, Find(await ListAsync(person), into.Id)!.Value.GetProperty("linkedWorkflows").GetInt32());
        var ended = Find(await ListAsync(person, "merged"), merged.Id)!.Value;
        Assert.Equal(2, ended.GetProperty("linkedWorkflows").GetInt32());
        Assert.Equal(JsonValueKind.String, ended.GetProperty("endedAt").ValueKind);

        var deleted = await DeleteAsync(person, preview: false, merged.Id);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var only = Assert.Single((await JsonAsync(deleted)).GetProperty("outcomes").EnumerateArray());
        Assert.Equal((merged.Id, OutcomeStatus.Merged, into.Id, 2),
            (only.GetProperty("id").GetString(), only.GetProperty("status").GetString(), only.GetProperty("mergedInto").GetString(), only.GetProperty("workflowsUnlinked").GetInt32()));

        // THE TARGET STAYS, AND ITS FIGURES ARE ITS OWN WORKFLOW'S ONLY.
        Assert.NotNull(await Outcomes.FindAsync(into.Id, Ct));
        var after = Find(await ListAsync(person), into.Id)!.Value.GetProperty("figures");
        Assert.Equal((1, 5L), (after.GetProperty("workflows").GetProperty("total").GetInt32(), after.GetProperty("tokens").GetProperty("billable").GetInt64()));

        foreach (var workflow in mine) Assert.True((await Outcomes.CurrentLinkAsync(workflow, Ct))!.IsUnlink);
        Assert.Equal(into.Id, (await Outcomes.CurrentLinkAsync(kept, Ct))!.OutcomeId);
    }

    [Fact]
    public async Task Deleting_an_outcome_another_was_merged_into_deletes_both_and_the_preview_named_both()
    {
        var person = await host.PersonAsync();
        var holder = await CreateAsync(person, Unique("Holder"));
        var mergedIn = await CreateAsync(person, Unique("Merged in"));

        var throughMerge = NextCorrelation();
        await LinkAsync(throughMerge, mergedIn.Id, host.Alpha);
        var direct = NextCorrelation();
        await LinkAsync(direct, holder.Id, host.Alpha);

        await MergeAsync(person, mergedIn, holder);
        await RetireAsync(person, holder);

        var preview = await JsonAsync(await DeleteAsync(person, preview: true, holder.Id));
        Assert.True(preview.GetProperty("preview").GetBoolean());
        var named = preview.GetProperty("outcomes").EnumerateArray()
            .Select(o => (o.GetProperty("id").GetString(), o.GetProperty("name").GetString(), o.GetProperty("cascadedFrom").GetString()))
            .ToList();
        Assert.Equal([(holder.Id, holder.Name, null), (mergedIn.Id, mergedIn.Name, holder.Id)], named);
        Assert.Equal(2, preview.GetProperty("workflowsUnlinked").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(person, preview: false, holder.Id)).StatusCode);

        Assert.Null(await Outcomes.FindAsync(holder.Id, Ct));
        Assert.Null(await Outcomes.FindAsync(mergedIn.Id, Ct));
        Assert.True((await Outcomes.CurrentLinkAsync(throughMerge, Ct))!.IsUnlink);
        Assert.True((await Outcomes.CurrentLinkAsync(direct, Ct))!.IsUnlink);
        Assert.NotNull(await host.Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.OutcomeDeleted, mergedIn.Id, Ct));
    }

    [Fact]
    public async Task The_preview_changes_nothing_an_active_or_proposed_id_refuses_the_whole_request_naming_it_and_a_machine_principal_is_refused()
    {
        var person = await host.PersonAsync();
        var retired = await CreateAsync(person, Unique("Retired"));
        var workflow = NextCorrelation();
        await LinkAsync(workflow, retired.Id, host.Alpha);
        await RetireAsync(person, retired);
        var active = await CreateAsync(person, Unique("Still active"));
        var proposed = (await Outcomes.ProposeAsync(Unique("Proposed"), null, new OutcomeActor("Alpha/Manager", OutcomeActorKind.Member),
            null, null, new TriggerAudit("Alpha/Manager", null, TenantActions.OutcomeCreated, null, null, null), Ct)).Outcome!;

        var links = await Outcomes.ReadLinksAsync(Ct);
        var outcomes = await Outcomes.ListAsync(Ct);
        var head = await TenantHeadAsync();

        // THE PREVIEW ANSWERS AND CHANGES NOTHING.
        var preview = await DeleteAsync(person, preview: true, retired.Id);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(1, (await JsonAsync(preview)).GetProperty("workflowsUnlinked").GetInt32());
        Assert.Equal(links, await Outcomes.ReadLinksAsync(Ct));
        Assert.Equal(outcomes, await Outcomes.ListAsync(Ct));
        Assert.Empty(await TenantRowsAsync(head));

        // A LIVE ID REFUSES THE WHOLE REQUEST, BY NAME, AND THE RETIRED ONE BESIDE IT STAYS.
        foreach (var (live, word) in new[] { (active, "active"), (proposed, "proposed") })
        {
            foreach (var asPreview in new[] { true, false })
            {
                var refused = await DeleteAsync(person, asPreview, retired.Id, live.Id);
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                var sentence = await ErrorAsync(refused);
                Assert.Contains($"\"{live.Name}\" is {word}", sentence, StringComparison.Ordinal);
                Assert.Contains("Nothing was deleted", sentence, StringComparison.Ordinal);
            }
        }

        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(person, preview: false, retired.Id, "no-such-outcome")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DeleteAsync(person, preview: false)).StatusCode);

        // A MACHINE PRINCIPAL HOLDING EVERY PERMIT IS REFUSED, preview and delete alike.
        var key = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, "Manager").ToString(), PrincipalKind.Container, host.Alpha, Permits.All, ct: Ct);
        using var machine = host.Container(key);
        foreach (var asPreview in new[] { true, false })
        {
            var refused = await DeleteAsync(machine, asPreview, retired.Id);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(PermitGate.HumansOnlyMessage, await ErrorAsync(refused));
        }

        // AND THE outcome TOOL HAS NO DELETE.
        var tools = Tools(new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, host.Alpha, Permits.All);
        Assert.StartsWith("Refused: the outcome tool's action is list, show, set, or propose",
            await tools.Outcome("delete", outcome: retired.Id, cancellationToken: Ct), StringComparison.Ordinal);

        Assert.Equal(links, await Outcomes.ReadLinksAsync(Ct));
        Assert.Equal(outcomes, await Outcomes.ListAsync(Ct));
        Assert.Empty(await TenantRowsAsync(head));
    }

    [Fact]
    public async Task A_figure_or_ledger_read_over_a_workflow_closed_while_serving_a_deleted_outcome_counts_it_under_no_outcome_and_does_not_fail()
    {
        var person = await host.PersonAsync();
        var outcome = await CreateAsync(person, Unique("Closed under"));

        var told = await person.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers/{TeamRegistry.DefaultManagerName}/tell",
            new { instruction = "look at the openings", outcome = outcome.Id }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        var workflow = (await JsonAsync(told)).GetProperty("correlationId").GetInt64();
        var closed = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/workflows/{workflow}/close", new { reason = "enough" }, Ct);
        Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync(Ct));
        Assert.Equal(outcome.Id, (await host.Services.GetRequiredService<IUsageLedger>().ReadWorkflowAsync(workflow, Ct))!.OutcomeIdAtClose);

        await RetireAsync(person, outcome);
        var noneBefore = NoOutcomeTotal(await ListAsync(person));
        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(person, preview: false, outcome.Id)).StatusCode);

        // THE LEDGER ROW IS UNTOUCHED, AND EVERY READ ANSWERS.
        Assert.Equal(outcome.Id, (await host.Services.GetRequiredService<IUsageLedger>().ReadWorkflowAsync(workflow, Ct))!.OutcomeIdAtClose);
        Assert.Equal(noneBefore + 1, NoOutcomeTotal(await ListAsync(person)));

        var export = await person.GetAsync("/api/ledger/export", Ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var books = await JsonAsync(export);
        Assert.DoesNotContain(books.GetProperty("outcomes").EnumerateArray(), o => o.GetProperty("id").GetString() == outcome.Id);
        var named = books.GetProperty("deletedOutcomes").EnumerateArray().Single(o => o.GetProperty("id").GetString() == outcome.Id);
        Assert.Equal($"{outcome.Name} (deleted)", named.GetProperty("name").GetString());

        // A LINK THAT STILL NAMES AN OUTCOME WHICH IS GONE - no unlink row after it - is No outcome too.
        var orphan = await CreateAsync(person, Unique("Gone underneath"));
        var orphaned = NextCorrelation();
        await LinkAsync(orphaned, orphan.Id, host.Alpha);
        await RunAsync(orphaned, host.Alpha, "Alpha", At(0), At(10), billable: 1);
        await CloseAsync(orphaned, host.Alpha, "Alpha", At(0), At(20), orphan.Id);
        var noneWithOrphanLinked = NoOutcomeTotal(await ListAsync(person));
        await ExecuteAsync($"DELETE FROM outcomes WHERE id = '{orphan.Id}'");

        var list = await person.GetAsync("/api/outcomes", Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(noneWithOrphanLinked + 1, NoOutcomeTotal(await JsonAsync(list)));
        Assert.Equal(HttpStatusCode.OK, (await person.GetAsync("/api/ledger/export", Ct)).StatusCode);
        Assert.Equal(OutcomeNudge.NoOutcome, await new OutcomeNudge(null!, Outcomes).LineAsync(orphaned, Ct));
        Assert.Empty(await Outcomes.CurrentOutcomesAsync([orphaned], Ct));
    }

    // ---- helpers ----

    private PlatformMcpTools Tools(string id, PrincipalKind kind, string? team, IReadOnlySet<string> permits)
    {
        var key = host.Services.GetRequiredService<IPrincipalStore>().MintAsync(id, kind, team, permits, ct: Ct).GetAwaiter().GetResult();
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(new Principal(id, kind, permits, null), "test");

        return new PlatformMcpTools(
            new HttpContextAccessor { HttpContext = context },
            host.Services.GetRequiredService<IPrincipalStore>(),
            host.Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(host.ServerHandler()));
    }

    private static long _correlation = 7_000_000_000 + Random.Shared.Next(1_000_000) * 1000L;

    private static long NextCorrelation() => Interlocked.Add(ref _correlation, 1000);

    private static DateTimeOffset At(int seconds) => new DateTimeOffset(2001, 2, 3, 4, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private Task LinkAsync(long correlation, string outcome, string team) =>
        Outcomes.LinkAsync(correlation, outcome, team, new OutcomeActor(Email, OutcomeActorKind.Person), OutcomeLinkHow.Person,
            agentRule: false, new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct);

    private static long _run = 9_000_000_000 + Random.Shared.Next(1_000_000);

    private async Task RunAsync(long correlation, string team, string teamName, DateTimeOffset started, DateTimeOffset ended, long billable)
    {
        await using var connection = await OpenAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, run_outcome,
                started_at, ended_at, measured, tokens_in, tokens_out, billable)
            VALUES ($seq, $correlation, $team, $name, 'Dev', 'completed', $started, $ended, 1, $billable, 0, $billable)
            """;
        insert.Parameters.AddWithValue("$seq", Interlocked.Increment(ref _run));
        insert.Parameters.AddWithValue("$correlation", correlation);
        insert.Parameters.AddWithValue("$team", team);
        insert.Parameters.AddWithValue("$name", teamName);
        insert.Parameters.AddWithValue("$started", started.ToString("O"));
        insert.Parameters.AddWithValue("$ended", ended.ToString("O"));
        insert.Parameters.AddWithValue("$billable", billable);
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private async Task CloseAsync(long correlation, string team, string teamName, DateTimeOffset root, DateTimeOffset closed, string outcome)
    {
        await using var connection = await OpenAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO workflow_ledger (close_seq, correlation, team_id, team_name, root_at, closed_at, how_closed, outcome_id_at_close)
            VALUES ($seq, $correlation, $team, $name, $root, $closed, 'completed', $outcome)
            """;
        insert.Parameters.AddWithValue("$seq", Interlocked.Increment(ref _run));
        insert.Parameters.AddWithValue("$correlation", correlation);
        insert.Parameters.AddWithValue("$team", team);
        insert.Parameters.AddWithValue("$name", teamName);
        insert.Parameters.AddWithValue("$root", root.ToString("O"));
        insert.Parameters.AddWithValue("$closed", closed.ToString("O"));
        insert.Parameters.AddWithValue("$outcome", outcome);
        await insert.ExecuteNonQueryAsync(Ct);
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The outcome a backlog item or a trigger names now.</summary>
    private async Task<string?> ColumnAsync(string table, string id)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT outcome_id FROM {table} WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private async Task<long> TenantHeadAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM tenant_events";
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed record Row(string Action, string? Subject, string? SubjectName, string? Detail);

    /// <summary>The outcome and link rows after <paramref name="head"/>: what a delete writes.</summary>
    private async Task<IReadOnlyList<Row>> TenantRowsAsync(long head)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT action, subject, subject_name, detail FROM tenant_events
            WHERE seq > $head AND (action LIKE 'outcome.%' OR action = 'workflow.outcome-changed')
            ORDER BY seq
            """;
        command.Parameters.AddWithValue("$head", head);

        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new Row(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
