using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// WHAT EVERY <see cref="IDiagnosticsLog"/> DOES, on SQLite and on SQL Server alike. Taken from
/// <c>CursorPagingTests.Diagnostics_pages_by_seq_with_every_filter_still_applied</c> and
/// <c>CursorPagingEdgeTests</c>, which reach the store through the Host (the same values, asked of the
/// store), and from the interface's remarks: a write that never throws, redaction at the write, the
/// filtered total beside a seq-cursor page, an unreadable store answering null, and the retention
/// bound. Plus the search's dialect made into rules: ASCII case ignored, and <c>%</c>, <c>_</c> and
/// <c>[</c> taken literally.
/// </summary>
public abstract class DiagnosticsLogContract : IdentityContract
{
    protected async Task<IDiagnosticsLog> CreateAsync(DiagnosticRetention? retention = null) =>
        (await DatabaseAsync()).Diagnostics(retention);

    private static async Task<IReadOnlyList<DiagnosticEvent>> SearchAsync(IDiagnosticsLog log, string search) =>
        (await log.ReadAsync(new DiagnosticsFilter(Search: search), take: 200, ct: Ct)).Events;

    [Fact]
    public async Task Diagnostics_pages_by_seq_with_every_filter_still_applied()
    {
        var log = await CreateAsync();

        for (var i = 0; i < 12; i++)
        {
            await log.WriteAsync(
                i % 2 == 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Info,
                DiagnosticKinds.HttpServerError, message: $"cursor-token row {i}", ct: Ct);
            await log.WriteAsync(DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, message: $"noise {i}", ct: Ct);
        }

        var search = new DiagnosticsFilter(Search: "cursor-token");
        var first = await log.ReadAsync(search, take: 5, ct: Ct);
        Assert.Equal(12, first.Total);
        Assert.Equal(5, first.Events.Count);
        Assert.Equal("cursor-token row 11", first.Events[0].Message);

        var all = await WalkAsync(log, search, 5);
        Assert.Equal(Enumerable.Range(0, 12).Reverse().Select(i => $"cursor-token row {i}"), all.Select(e => e.Message));
        Assert.Equal(all.Select(e => e.Seq).OrderDescending(), all.Select(e => e.Seq));

        var past = await log.ReadAsync(search, before: all[^1].Seq, take: 5, ct: Ct);
        Assert.Empty(past.Events);
        Assert.Equal(12, past.Total);

        var errors = await WalkAsync(log, search with { Severities = [DiagnosticSeverity.Error] }, 2);
        Assert.Equal(6, errors.Count);
        Assert.All(errors, e => Assert.Equal(DiagnosticSeverity.Error, e.Severity));

        // A row written after the first page does not appear in, or shift, the older pages.
        var top = (await log.ReadAsync(search, take: 5, ct: Ct)).Events;
        await log.WriteAsync(DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, message: "cursor-token late", ct: Ct);
        var rest = await WalkAsync(log, search, 5, top[^1].Seq);
        Assert.Equal(all.Skip(5).Select(e => e.Seq), rest.Select(e => e.Seq));
        Assert.Equal("cursor-token late", (await log.ReadAsync(search, take: 5, ct: Ct)).Events[0].Message);
    }

    [Fact]
    public async Task The_take_is_clamped_and_the_total_ignores_the_cursor()
    {
        var log = await CreateAsync();
        for (var i = 0; i < 205; i++)
        {
            await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.HttpRefused, message: $"bulk {i}", ct: Ct);
        }

        var bulk = new DiagnosticsFilter(Search: "bulk");
        Assert.Equal(200, (await log.ReadAsync(bulk, take: 1000, ct: Ct)).Events.Count);
        Assert.Single((await log.ReadAsync(bulk, take: 0, ct: Ct)).Events);
        Assert.Single((await log.ReadAsync(bulk, take: -1, ct: Ct)).Events);
        Assert.Equal(50, (await log.ReadAsync(bulk, ct: Ct)).Events.Count);

        var page = await log.ReadAsync(bulk, before: (await log.ReadAsync(bulk, take: 10, ct: Ct)).Events[^1].Seq, take: 3, ct: Ct);
        Assert.Equal(205, page.Total);
        Assert.Equal(["bulk 194", "bulk 193", "bulk 192"], page.Events.Select(e => e.Message));
    }

    [Fact]
    public async Task Kinds_severities_and_the_time_window_narrow_and_combine()
    {
        var log = await CreateAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.HttpRefused, message: "refused", ct: Ct);
        await log.WriteAsync(DiagnosticSeverity.Warning, DiagnosticKinds.HttpRefused, message: "warned", ct: Ct);
        await log.WriteAsync(DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, message: "error", ct: Ct);
        await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.StartupInstance, message: "started", ct: Ct);
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        async Task<string?[]> MessagesAsync(DiagnosticsFilter filter) =>
            [.. (await log.ReadAsync(filter, ct: Ct)).Events.Select(e => e.Message)];

        Assert.Equal(["warned", "refused"], await MessagesAsync(new DiagnosticsFilter(Kinds: [DiagnosticKinds.HttpRefused, DiagnosticKinds.HttpRefused])));
        Assert.Equal(["started", "warned", "refused"], await MessagesAsync(new DiagnosticsFilter(Severities: [DiagnosticSeverity.Info, DiagnosticSeverity.Warning])));
        Assert.Equal(["refused"], await MessagesAsync(new DiagnosticsFilter(Kinds: [DiagnosticKinds.HttpRefused], Severities: [DiagnosticSeverity.Info])));
        Assert.Equal(4, (await log.ReadAsync(new DiagnosticsFilter(From: before, To: after), ct: Ct)).Total);
        Assert.Equal(0, (await log.ReadAsync(new DiagnosticsFilter(To: before), ct: Ct)).Total);
        Assert.Equal(0, (await log.ReadAsync(new DiagnosticsFilter(From: after), ct: Ct)).Total);
        Assert.Equal(4, (await log.ReadAsync(ct: Ct)).Total);
    }

    /// <summary>A row reads back whole, its message and detail redacted at the write.</summary>
    [Fact]
    public async Task A_row_reads_back_whole_with_its_message_and_detail_redacted()
    {
        var log = await CreateAsync();
        const string secret = "Qm7vX2pL9tRw4kZs8Hn3Yb6dJ1fA5gC0eT7uI2oP4sW";
        var started = DateTimeOffset.UtcNow.AddSeconds(-1);

        await log.WriteAsync(
            DiagnosticSeverity.Warning, DiagnosticKinds.HttpRefused, DiagnosticSources.Http, "/api/teams/{team}", 409,
            "System.InvalidOperationException", $"value {secret} end", $$"""{"token":"value {{secret}} end","who":"O'Brien 🚀"}""", Ct);

        var row = Assert.Single((await log.ReadAsync(ct: Ct)).Events);
        Assert.Equal(
            (DiagnosticSeverity.Warning, DiagnosticKinds.HttpRefused, DiagnosticSources.Http, "/api/teams/{team}", (int?)409, "System.InvalidOperationException"),
            (row.Severity, row.Kind, row.Source, row.Route, row.Status, row.ExceptionType));
        Assert.Equal("value [redacted] end", row.Message);
        Assert.Equal(DiagnosticRedaction.Redact($$"""{"token":"value {{secret}} end","who":"O'Brien 🚀"}"""), row.Detail);
        Assert.DoesNotContain(secret, row.Detail);
        Assert.InRange(row.OccurredAt, started, DateTimeOffset.UtcNow.AddSeconds(1));

        await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.StartupInstance, ct: Ct);
        var bare = (await log.ReadAsync(ct: Ct)).Events[0];
        Assert.Equal((null, null, null, null, null, null), (bare.Source, bare.Route, bare.Status, bare.ExceptionType, bare.Message, bare.Detail));
    }

    /// <summary>
    /// THE PLAN'S REQUIRED TEST. SQLite's LIKE ignores ASCII case, so <c>timeout</c> finds
    /// <c>TimeOut</c>, in each of the four searched columns and in none other.
    /// </summary>
    [Fact]
    public async Task Search_ignores_ascii_case()
    {
        var log = await CreateAsync();
        await log.WriteAsync(DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, message: "Connection TimeOut after 30s", ct: Ct);
        await log.WriteAsync(DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, detail: """{"reason":"TIMEOUT"}""", ct: Ct);
        await log.WriteAsync(DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, route: "/api/TimeOuts/{id}", ct: Ct);
        await log.WriteAsync(DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, exceptionType: "System.TimeoutException", ct: Ct);
        await log.WriteAsync(DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, source: "timeout", message: "unrelated", ct: Ct);

        Assert.Equal(4, (await SearchAsync(log, "timeout")).Count);
        Assert.Equal(4, (await SearchAsync(log, "TIMEOUT")).Count);
        Assert.Equal(4, (await SearchAsync(log, "  tImEoUt  ")).Count);
        Assert.Single(await SearchAsync(log, "connection timeout"));

        // Not over kind or severity: those have filters of their own.
        Assert.Empty(await SearchAsync(log, "server-error"));
        Assert.Empty(await SearchAsync(log, "error"));
    }

    /// <summary>
    /// RULING 4: <c>%</c>, <c>_</c> and <c>[</c> typed into the box are those characters, not patterns -
    /// SQLite escapes the first two and has no third; SQL Server reads all three, and escapes them.
    /// </summary>
    [Fact]
    public async Task Search_takes_percent_underscore_and_bracket_literally()
    {
        var log = await CreateAsync();
        foreach (var message in new[] { "100% done", "1000 done", "a_b", "axb", "[x]", "x", "[^a]", "b", "back\\slash", "backXslash" })
        {
            await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.HttpRefused, message: message, ct: Ct);
        }

        Assert.Equal(["100% done"], (await SearchAsync(log, "100%")).Select(e => e.Message));
        Assert.Equal(["a_b"], (await SearchAsync(log, "a_b")).Select(e => e.Message));
        Assert.Equal(["[x]"], (await SearchAsync(log, "[x]")).Select(e => e.Message));
        Assert.Equal(["[^a]"], (await SearchAsync(log, "[^a]")).Select(e => e.Message));
        Assert.Equal(["back\\slash"], (await SearchAsync(log, "k\\s")).Select(e => e.Message));
        Assert.Equal(2, (await SearchAsync(log, "%")).Count + (await SearchAsync(log, "_")).Count);
    }

    [Fact]
    public async Task Has_any_is_false_until_a_row_is_written_and_null_when_the_store_cannot_be_read()
    {
        var log = await CreateAsync();
        Assert.False(await log.HasAnyAsync(Ct));

        await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.StartupInstance, ct: Ct);
        Assert.True(await log.HasAnyAsync(Ct));

        await (await DatabaseAsync()).ExecuteAsync("DROP TABLE diagnostic_events");
        Assert.Null(await log.HasAnyAsync(Ct));
    }

    [Fact]
    public async Task A_write_never_throws_when_the_table_is_gone_or_the_write_is_cancelled()
    {
        var log = await CreateAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await log.WriteAsync(DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, message: "cancelled", ct: cancelled.Token);
        Assert.False(await log.HasAnyAsync(Ct));

        await (await DatabaseAsync()).ExecuteAsync("DROP TABLE diagnostic_events");
        await log.WriteAsync(DiagnosticSeverity.Error, DiagnosticKinds.HttpServerError, message: "lost", ct: Ct);
        await log.TrimAsync(Ct);
    }

    [Fact]
    public async Task A_trim_removes_rows_past_the_age_and_past_the_row_bound()
    {
        var log = await CreateAsync(new DiagnosticRetention(TimeSpan.FromDays(1), 3));
        await (await DatabaseAsync()).ExecuteAsync(
            "INSERT INTO diagnostic_events (occurred_at, severity, kind, message) VALUES ('2020-01-01T00:00:00.0000000+00:00', 'info', 'startup.instance', 'ancient')");
        for (var i = 0; i < 5; i++)
        {
            await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.StartupInstance, message: $"row {i}", ct: Ct);
        }

        await log.TrimAsync(Ct);

        Assert.Equal(["row 4", "row 3", "row 2"], (await log.ReadAsync(ct: Ct)).Events.Select(e => e.Message));
    }

    /// <summary>The trim runs from the write path, every two hundredth write, and on no timer.</summary>
    [Fact]
    public async Task Every_two_hundredth_write_trims()
    {
        var log = await CreateAsync(new DiagnosticRetention(TimeSpan.FromDays(1), 5));

        for (var i = 0; i < 199; i++)
        {
            await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.HttpRefused, message: $"row {i}", ct: Ct);
        }

        Assert.Equal(199, (await log.ReadAsync(ct: Ct)).Total);

        await log.WriteAsync(DiagnosticSeverity.Info, DiagnosticKinds.HttpRefused, message: "row 199", ct: Ct);
        Assert.Equal(5, (await log.ReadAsync(ct: Ct)).Total);
    }

    private static async Task<List<DiagnosticEvent>> WalkAsync(IDiagnosticsLog log, DiagnosticsFilter filter, int take, long? start = null)
    {
        var rows = new List<DiagnosticEvent>();
        var before = start;

        for (var page = 0; page < 100; page++)
        {
            var read = (await log.ReadAsync(filter, before, take, Ct)).Events;
            if (read.Count == 0) break;
            rows.AddRange(read);
            before = read[^1].Seq;
        }

        return rows;
    }
}

public sealed class SqliteDiagnosticsLogTests : DiagnosticsLogContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerDiagnosticsLogTests : DiagnosticsLogContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
