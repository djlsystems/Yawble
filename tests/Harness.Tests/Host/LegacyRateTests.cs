using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A RUN FROM BEFORE SNAPSHOTS, PRICED FROM THE RATE'S HISTORY, through a real Host whose rate is set
/// only in configuration (<c>Outcomes:AgentHourlyRate</c> = 30): the start that applies
/// <c>outcome-012</c> captures that rate once in <c>rate_epochs</c>, and a later start or rate change
/// does not move it. An agent run is priced at the tenant rate in effect at its start
/// (<c>reconstructed</c>), or - before every recorded change - at the epoch (<c>fallback-at-upgrade</c>);
/// a plugin run is never priced and never Agent Hours; a snapshotted run keeps its snapshot, a
/// snapshotted none included. Each test has its own Host, so its rate history is its own.
/// </summary>
public sealed class LegacyRateTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-legacy-rate-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;

    private string Database => Path.Combine(_dataRoot, "messages.db");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = Start();
        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<Program> Start()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host
                .UseSetting("DataRoot", _dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .UseSetting(RateEpochFacts.ConfiguredRateKey, "30"));
        _ = factory.Services;
        return factory;
    }

    [Fact]
    public async Task The_start_captures_a_configuration_only_rate_once_and_a_later_rate_change_or_start_does_not_move_it()
    {
        var first = await EpochAsync();
        Assert.Equal(("30", "USD", RateEpochFacts.SourceConfiguration), (first.Amount, first.Currency, first.Source));

        // A person sets the rate, and the Host starts again over the same volume.
        var person = await PersonAsync();
        await SetOkAsync(person, 90);
        await _factory.DisposeAsync();
        _factory = Start();

        Assert.Equal(first, await EpochAsync());
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM rate_epochs"));
    }

    [Fact]
    public async Task A_legacy_agent_run_is_priced_at_the_rate_in_effect_at_its_start_and_a_later_change_does_not_move_it()
    {
        var person = await PersonAsync();
        var team = await NewTeamAsync();
        var outcome = await CreateOutcomeAsync(person);
        var workflow = await LinkedAsync(outcome, team);
        var now = DateTimeOffset.UtcNow;

        // The tenant rate was set to 60 two weeks ago; the epoch (30, from configuration) is before it.
        await TenantEventAsync(now.AddDays(-14), TenantActions.TenantSettingChanged, "60");

        await RunAsync(workflow, team, now.AddDays(-10), now.AddDays(-10).AddHours(1));                       // reconstructed at 60
        await RunAsync(workflow, team, now.AddDays(-20), now.AddDays(-20).AddHours(1));                       // fallback at 30
        await RunAsync(workflow, team, now.AddDays(-10), now.AddDays(-10).AddHours(2), kind: MemberRef.PluginKind); // never priced
        await RunAsync(workflow, team, now.AddDays(-10), now.AddDays(-10).AddHours(1), snapshot: true);        // a snapshotted none

        // THE RATE CHANGES AFTER EVERY RUN STARTED: nothing moves.
        await SetOkAsync(person, 90);

        var read = await JsonAsync(await person.GetAsync($"/api/teams/{team}/cost", Ct));
        var totals = read.GetProperty("totals");

        // 60 + 30; three agent hours, priced or not, and no plugin hour; 90 over the two priced agent hours.
        Assert.Equal(90m, totals.GetProperty("cost").GetDecimal());
        Assert.Equal(3 * 3600, totals.GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Equal(45m, totals.GetProperty("agentRate").GetDecimal());
        Assert.Equal(
            [RateEpochFacts.RateSourceReconstructed, RateEpochFacts.RateSourceFallback],
            totals.GetProperty("legacyRates").EnumerateArray().Select(e => e.GetString()));

        var entry = Assert.Single(read.GetProperty("outcomes").EnumerateArray());
        Assert.Equal(90m, entry.GetProperty("outcomeTotalCost").GetDecimal());

        // The summary's figure says so too, and the outcomes dashboard prices the same runs.
        var summary = await JsonAsync(await person.GetAsync("/api/teams/cost/summary", Ct));
        Assert.Equal(90m, summary.GetProperty("total").GetProperty("cost").GetDecimal());
        Assert.Equal(2, summary.GetProperty("total").GetProperty("legacyRates").GetArrayLength());

        var list = await JsonAsync(await person.GetAsync("/api/outcomes", Ct));
        var cost = list.GetProperty("outcomes").EnumerateArray()
            .Single(o => o.GetProperty("id").GetString() == outcome).GetProperty("figures").GetProperty("cost");
        Assert.Equal(90m, cost.GetProperty("amount").GetDecimal());
        Assert.Equal(2, cost.GetProperty("legacyRates").GetArrayLength());

        // THE EXPORT: each run's rate source and estimate, from the same service.
        var export = await JsonAsync(await person.GetAsync("/api/ledger/export?take=200", Ct));
        var runs = export.GetProperty("runs").EnumerateArray()
            .Where(r => r.GetProperty("correlation").GetInt64() == workflow)
            .Select(r => (
                Kind: r.GetProperty("memberKind").GetString(),
                Source: r.GetProperty("rateSource").GetString(),
                Amount: r.GetProperty("estimate").GetProperty("amount") is { ValueKind: JsonValueKind.Number } a ? a.GetDecimal() : (decimal?)null,
                Currency: r.GetProperty("estimate").GetProperty("currency").GetString()))
            .OrderBy(r => r.Source).ThenBy(r => r.Kind)
            .ToList();

        Assert.Equal(
            [
                ("agent", RateEpochFacts.RateSourceFallback, (decimal?)30m, (string?)"USD"),
                ("agent", ExecutionSnapshotFacts.RateSourceNone, null, null),
                ("plugin", ExecutionSnapshotFacts.RateSourceNone, null, null),
                ("agent", RateEpochFacts.RateSourceReconstructed, 60m, "USD"),
            ],
            runs);
    }

    [Fact]
    public async Task A_zero_or_a_reset_in_effect_at_start_is_unavailable_and_the_figure_keeps_its_hours_without_money()
    {
        var person = await PersonAsync();
        var team = await NewTeamAsync();
        var now = DateTimeOffset.UtcNow;

        await TenantEventAsync(now.AddDays(-14), TenantActions.TenantSettingChanged, "0");
        await TenantEventAsync(now.AddDays(-7), TenantActions.TenantSettingReset, null);

        await RunAsync(NextCorrelation(), team, now.AddDays(-10), now.AddDays(-10).AddHours(1));
        await RunAsync(NextCorrelation(), team, now.AddDays(-5), now.AddDays(-5).AddHours(1));

        var totals = (await JsonAsync(await person.GetAsync($"/api/teams/{team}/cost", Ct))).GetProperty("totals");

        Assert.Equal(JsonValueKind.Null, totals.GetProperty("cost").ValueKind);
        Assert.Equal(JsonValueKind.Null, totals.GetProperty("agentRate").ValueKind);
        Assert.Equal(7200, totals.GetProperty("agentSeconds").GetDouble(), 3);
        Assert.Equal(0, totals.GetProperty("legacyRates").GetArrayLength());
    }

    // ---- fixtures ----

    private async Task<HttpClient> PersonAsync()
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task SetOkAsync(HttpClient person, int rate)
    {
        var response = await person.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object?> { [TenantSettings.OutcomesAgentHourlyRateName] = rate }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<string> NewTeamAsync()
    {
        var agent = _factory.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        return (await _factory.Services.GetRequiredService<TeamRegistry>()
            .CreateAsync($"Legacy {Guid.NewGuid():N}"[..20], agent, memberAgent: agent, ct: Ct)).Id;
    }

    private static async Task<string> CreateOutcomeAsync(HttpClient person)
    {
        var created = await person.PostAsJsonAsync("/api/outcomes", new { name = $"Legacy {Guid.NewGuid():N}" }, Ct);
        return (await JsonAsync(created)).GetProperty("id").GetString()!;
    }

    private async Task<long> LinkedAsync(string outcome, string team)
    {
        var correlation = NextCorrelation();
        Assert.True((await _factory.Services.GetRequiredService<IOutcomeStore>().LinkAsync(
            correlation, outcome, team, new OutcomeActor(Email, OutcomeActorKind.Person), OutcomeLinkHow.Person, agentRule: false,
            new TriggerAudit(null, Email, TenantActions.WorkflowOutcomeChanged, null, null, null), Ct)).Ok);
        return correlation;
    }

    private static long _correlation = 7_000_000_000 + Random.Shared.Next(1_000_000);

    private static long NextCorrelation() => Interlocked.Add(ref _correlation, 1000);

    private static long _run = 6_000_000_000 + Random.Shared.Next(1_000_000);

    /// <summary>A tenant row for <c>outcomes.agentHourlyRate</c> at <paramref name="at"/>, as a settings write leaves one.</summary>
    private Task TenantEventAsync(DateTimeOffset at, string action, string? value) =>
        ExecuteAsync(
            """
            INSERT INTO tenant_events (occurred_at, actor_id, actor_email, action, subject, subject_name, detail)
            VALUES ($at, NULL, $email, $action, $setting, $setting, $detail)
            """,
            ("$at", Instant.Stamp(at)), ("$email", Email), ("$action", action),
            ("$setting", TenantSettings.OutcomesAgentHourlyRateName),
            ("$detail", JsonSerializer.Serialize(new { setting = TenantSettings.OutcomesAgentHourlyRateName, old = (string?)null, @new = value })));

    /// <summary>A ledger row; with <paramref name="snapshot"/>, one that names a snapshot with no rate and the
    /// workflow's link then.</summary>
    private async Task RunAsync(
        long correlation, string team, DateTimeOffset started, DateTimeOffset ended, string kind = MemberRef.AgentKind, bool snapshot = false)
    {
        object start = DBNull.Value;
        if (snapshot)
        {
            start = Interlocked.Increment(ref _run);
            await ExecuteAsync(
                """
                INSERT INTO execution_snapshots (start_seq, correlation, team_id, team_name, member, member_kind, rate_source,
                    link_id, started_at)
                VALUES ($start, $c, $team, $team, 'Dev', $kind, 'none',
                    (SELECT MAX(id) FROM workflow_outcome_links WHERE correlation = $c), $started)
                """,
                ("$start", start), ("$c", correlation), ("$team", team), ("$kind", kind), ("$started", Instant.Stamp(started)));
        }

        await ExecuteAsync(
            """
            INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, member_kind, run_outcome,
                started_at, ended_at, measured, tokens_in, tokens_out, billable, start_seq)
            VALUES ($seq, $c, $team, $team, 'Dev', $kind, 'completed', $started, $ended, 1, 10, 0, 10, $start)
            """,
            ("$seq", Interlocked.Increment(ref _run)), ("$c", correlation), ("$team", team), ("$kind", kind),
            ("$started", Instant.Stamp(started)), ("$ended", Instant.Stamp(ended)), ("$start", start));
    }

    private async Task<(string? Amount, string? Currency, string Source)> EpochAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rate_amount, rate_currency, rate_source FROM rate_epochs";
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2));
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

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
