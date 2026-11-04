using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// HUMAN INTERVENTIONS ARE RECORDED BY THE ROUTE, NEVER PUBLISHED, through the real routes: each person's
/// act writes exactly one <c>intervention_ledger</c> row of its kind, with the right actor, team,
/// workflow, outcome and log row; a key is its owner, still readable once the key and the owner are gone;
/// a member, a trigger or the Host writes none; and nothing in a body can write one or choose its fields.
/// </summary>
public sealed class InterventionLedgerTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Email = "person@example.test";

    [Fact]
    public async Task A_persons_tell_writes_one_instruction_row_with_its_row_and_a_forged_body_is_ignored()
    {
        using var person = await host.PersonAsync();

        var seq = await TellAsync(person, new
        {
            instruction = "Look at the board.",
            kind = InterventionKinds.Close,
            intervention = new { kind = InterventionKinds.ConciergeSession, actorId = "someone-else", durationSeconds = 999 },
            actorId = "someone-else",
            actor_email = "boss@example.test",
            durationSeconds = 999,
            duration_measured = 1,
            related_seq = 1,
        });

        var row = Assert.Single(await RowsAsync($"related_seq = {seq}"));
        Assert.Equal(new Row(await PersonIdAsync(), Email, InterventionKinds.Instruction, host.Alpha, seq, null, seq, null, 0), row);
    }

    [Fact]
    public async Task A_tell_answering_a_needs_decision_is_decision_answered_and_the_members_question_writes_none()
    {
        using var person = await host.PersonAsync();
        var asked = await Log.AppendAsync(
            new NewMessage(MessageTypes.NeedsDecision, """{"question":"Ship it on Friday?"}""", new ContainerId(host.Alpha, "Worker").ToString()),
            Ct);

        var answer = await TellAsync(person, new { instruction = "Yes, ship it.", causation = asked.Seq.ToString() });

        Assert.Empty(await RowsAsync($"related_seq = {asked.Seq}"));
        Assert.Equal(
            new Row(await PersonIdAsync(), Email, InterventionKinds.DecisionAnswered, host.Alpha, asked.CorrelationId, null, answer, null, 0),
            Assert.Single(await RowsAsync($"related_seq = {answer}")));
    }

    [Fact]
    public async Task A_keys_act_is_recorded_as_its_owner_and_still_reads_after_the_key_is_revoked_and_the_owner_deleted()
    {
        var email = $"owner-{Guid.NewGuid():N}@example.test";
        var users = host.Services.GetRequiredService<IUserStore>();
        await users.CreateAsync(email, HostFixture.Password, ct: Ct);
        using var owner = host.Anonymous();
        (await owner.PostAsJsonAsync("/api/auth/login", new { email, password = HostFixture.Password }, Ct)).EnsureSuccessStatusCode();
        var ownerId = await ScalarAsync($"SELECT id FROM users WHERE email = '{email}'");

        var minted = await owner.PostAsJsonAsync("/api/keys", new { label = $"tool {Guid.NewGuid():N}" }, Ct);
        minted.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await minted.Content.ReadAsStringAsync(Ct)).RootElement;

        long seq;
        using (var key = host.Container(body.GetProperty("credential").GetString()!))
        {
            seq = await TellAsync(key, new { instruction = "From a tool." });
        }

        (await owner.DeleteAsync($"/api/keys/{body.GetProperty("id").GetString()}", Ct)).EnsureSuccessStatusCode();
        await users.DeleteAsync(ownerId!, Ct);
        Assert.Null(await ScalarAsync($"SELECT id FROM users WHERE email = '{email}'"));

        Assert.Equal(
            new Row(ownerId!, email, InterventionKinds.Instruction, host.Alpha, seq, null, seq, null, 0),
            Assert.Single(await RowsAsync($"related_seq = {seq}")));
    }

    [Fact]
    public async Task A_members_tell_and_rows_a_trigger_a_wake_or_the_host_append_write_no_row()
    {
        using var member = host.Container(host.AlphaContainerKey);
        var told = await TellAsync(member, new { instruction = "From a member.", kind = InterventionKinds.Instruction });

        var appended = new List<long> { told };
        foreach (var source in new[] { "schedule:7", "trigger:8", "wake:9", "host" })
        {
            appended.Add((await Log.AppendAsync(
                new NewMessage(
                    MessageTypes.InstructionFor(new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName)),
                    """{"instruction":"fire"}""", source),
                Ct)).Seq);
        }

        Assert.Empty(await RowsAsync($"related_seq IN ({string.Join(", ", appended)}) OR correlation IN ({string.Join(", ", appended)})"));
    }

    [Fact]
    public async Task Nudge_resume_and_close_each_write_one_row_with_the_row_they_went_with()
    {
        using var person = await host.PersonAsync();
        var workflow = await TellAsync(person, new { instruction = "Start something." });

        Assert.Equal(HttpStatusCode.NoContent, (await person.PostAsync($"/api/teams/{host.Alpha}/workflows/{workflow}/nudge", null, Ct)).StatusCode);

        await Log.AppendAsync(
            new NewMessage(
                MessageTypes.WorkflowPaused,
                JsonSerializer.Serialize(new { team = host.Alpha, reason = "Spent over its budget.", limitSource = "team" }),
                "host", workflow),
            Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await person.PostAsync($"/api/teams/{host.Alpha}/workflows/{workflow}/resume", null, Ct)).StatusCode);

        var closed = await person.PostAsJsonAsync($"/api/teams/{host.Alpha}/workflows/{workflow}/close", new { reason = "Done here." }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);

        var rows = await RowsAsync($"correlation = {workflow}");
        Assert.Equal(
            [InterventionKinds.Instruction, InterventionKinds.Nudge, InterventionKinds.Resume, InterventionKinds.Close],
            rows.Select(r => r.Kind));

        var personId = await PersonIdAsync();
        Assert.All(rows, r => Assert.Equal((personId, (string?)Email, (string?)host.Alpha, 0L, (long?)null), (r.ActorId, r.ActorEmail, r.Team, r.Measured, r.Duration)));

        // Each names the log row it was written with: the nudge's instruction, the resumed row, the closed row.
        Assert.Equal(MessageTypes.InstructionFor(new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName)), await TypeOfAsync(rows[1].RelatedSeq));
        Assert.Equal(MessageTypes.WorkflowResumed, await TypeOfAsync(rows[2].RelatedSeq));
        Assert.Equal(MessageTypes.WorkflowClosed, await TypeOfAsync(rows[3].RelatedSeq));
    }

    [Fact]
    public async Task A_dispatch_writes_one_row_naming_the_outcome_it_linked()
    {
        using var person = await host.PersonAsync();
        var outcome = await OutcomeAsync(person);
        var item = (await JsonAsync(await person.PostAsJsonAsync("/api/backlog", new { title = "Crawl openings", body = "spec" }, Ct)))
            .GetProperty("id").GetInt64();
        (await person.PatchAsJsonAsync($"/api/backlog/{item}", new { state = "ready", outcomeId = outcome }, Ct)).EnsureSuccessStatusCode();

        var dispatched = await person.PostAsync($"/api/teams/{host.Alpha}/backlog/{item}/dispatch", null, Ct);
        Assert.Equal(HttpStatusCode.OK, dispatched.StatusCode);
        var correlation = (await JsonAsync(dispatched)).GetProperty("correlation").GetInt64();

        // One dispatch is one act: the Manager's instruction the route also appends records nothing.
        Assert.Equal(
            new Row(await PersonIdAsync(), Email, InterventionKinds.Dispatch, host.Alpha, correlation, outcome, correlation, null, 0),
            Assert.Single(await RowsAsync($"correlation = {correlation}")));
    }

    [Fact]
    public async Task Setting_and_clearing_a_workflows_outcome_writes_outcome_changed_and_a_members_link_writes_none()
    {
        using var person = await host.PersonAsync();
        using var member = host.Container(host.AlphaContainerKey);
        var workflow = await TellAsync(person, new { instruction = "Start something." });
        var first = await OutcomeAsync(person);
        var second = await OutcomeAsync(person);

        Assert.Equal(HttpStatusCode.OK, (await member.PutAsJsonAsync($"/api/teams/{host.Alpha}/workflows/{workflow}/outcome", new { outcome = first }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync($"/api/teams/{host.Alpha}/workflows/{workflow}/outcome", new { outcome = second }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await person.DeleteAsync($"/api/teams/{host.Alpha}/workflows/{workflow}/outcome", Ct)).StatusCode);

        var personId = await PersonIdAsync();
        Assert.Equal(
            [
                new Row(personId, Email, InterventionKinds.OutcomeChanged, host.Alpha, workflow, second, null, null, 0),
                new Row(personId, Email, InterventionKinds.OutcomeChanged, host.Alpha, workflow, null, null, null, 0),
            ],
            await RowsAsync($"correlation = {workflow} AND kind = '{InterventionKinds.OutcomeChanged}'"));
    }

    [Fact]
    public async Task Resuming_a_paused_team_writes_one_row_and_resuming_a_running_one_writes_none()
    {
        using var person = await host.PersonAsync();
        var before = (await RowsAsync($"kind = '{InterventionKinds.Resume}' AND team_id = '{host.Beta}' AND correlation IS NULL")).Count;

        Assert.Equal(HttpStatusCode.NoContent, (await person.PostAsync($"/api/teams/{host.Beta}/pause", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await person.PostAsync($"/api/teams/{host.Beta}/resume", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await person.PostAsync($"/api/teams/{host.Beta}/resume", null, Ct)).StatusCode);

        var rows = await RowsAsync($"kind = '{InterventionKinds.Resume}' AND team_id = '{host.Beta}' AND correlation IS NULL");
        Assert.Equal(before + 1, rows.Count);
        Assert.Equal(new Row(await PersonIdAsync(), Email, InterventionKinds.Resume, host.Beta, null, null, null, null, 0), rows[^1]);
    }

    [Fact]
    public async Task Saving_a_connections_value_or_a_secret_records_that_it_happened_and_never_the_value()
    {
        using var person = await host.PersonAsync();
        var before = (await RowsAsync($"kind = '{InterventionKinds.CredentialSupplied}'")).Count;
        var value = $"sk_test_{Guid.NewGuid():N}";
        var secret = $"secret-{Guid.NewGuid():N}";

        var added = await person.PostAsJsonAsync("/api/connections/credentials", new
        {
            kind = "apikey",
            preset = "stripe",
            name = $"Stripe {Guid.NewGuid():N}",
            fields = new[] { new { name = "key", value } },
        }, Ct);
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(Ct));

        var key = $"INTERVENTION_TEST_{Guid.NewGuid():N}".ToUpperInvariant();
        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync($"/api/secrets/{key}", new { value = secret }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync($"/api/secrets/{key}", new { value = secret + "2" }, Ct)).StatusCode);
        (await person.DeleteAsync($"/api/secrets/{key}", Ct)).EnsureSuccessStatusCode();

        // A set and a replace each supplied a value; the clear supplied none.
        var rows = await RowsAsync($"kind = '{InterventionKinds.CredentialSupplied}'");
        Assert.Equal(before + 3, rows.Count);
        var personId = await PersonIdAsync();
        Assert.All(rows.Skip(before), r => Assert.Equal(new Row(personId, Email, InterventionKinds.CredentialSupplied, null, null, null, null, null, 0), r));

        await using var connection = Open();
        await using var dump = connection.CreateCommand();
        dump.CommandText = "SELECT * FROM intervention_ledger";
        await using var reader = await dump.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var text = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
                Assert.DoesNotContain(value, text, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task No_route_is_a_write_route_for_the_ledger()
    {
        var routes = host.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .ToList();
        Assert.DoesNotContain(routes, r => r.Contains("intervention", StringComparison.OrdinalIgnoreCase));
    }

    // ---- fixtures ----

    private sealed record Row(
        string ActorId, string? ActorEmail, string Kind, string? Team, long? Correlation, string? Outcome,
        long? RelatedSeq, long? Duration, long Measured);

    private IMessageLog Log => host.Services.GetRequiredService<IMessageLog>();

    private async Task<long> TellAsync(HttpClient client, object body)
    {
        var told = await client.PostAsJsonAsync(
            $"/api/teams/{host.Alpha}/containers/{TeamRegistry.DefaultManagerName}/tell", body, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        return (await JsonAsync(told)).GetProperty("seq").GetInt64();
    }

    private async Task<string> OutcomeAsync(HttpClient person)
    {
        var created = await person.PostAsJsonAsync("/api/outcomes", new { name = $"Outcome {Guid.NewGuid():N}" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await JsonAsync(created)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private async Task<string> PersonIdAsync() => (await ScalarAsync($"SELECT id FROM users WHERE email = '{Email}'"))!;

    private async Task<string?> TypeOfAsync(long? seq) => await ScalarAsync($"SELECT type FROM messages WHERE seq = {seq}");

    private async Task<List<Row>> RowsAsync(string where)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT actor_id, actor_email, kind, team_id, correlation, outcome_id_at_event, related_seq, duration_seconds, duration_measured "
            + $"FROM intervention_ledger WHERE {where} ORDER BY id";

        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new Row(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.GetInt64(8)));
        }

        return rows;
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(host.DataRoot, "messages.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
