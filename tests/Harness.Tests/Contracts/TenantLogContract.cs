using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="ITenantLog"/>, as each provider must keep it: the store's own remarks, which the SQLite
/// suite reaches only through a running Host (latest by seq and never by time, the subject compared
/// exactly, the page newest first under its cursor with a total that counts the whole log, the page
/// size clamped) and a subject as long as a document path can be.
/// </summary>
public abstract class TenantLogContract : IdentityContract
{
    protected async Task<ITenantLog> CreateAsync() => (await DatabaseAsync()).TenantLog();

    [Fact]
    public async Task A_written_row_reads_back_whole_and_the_latest_is_by_seq_with_the_subject_compared_exactly()
    {
        var log = await CreateAsync();
        await log.WriteAsync("u1", "a@example.test", "suite.passed", "Alpha/abc123/unit", "Alpha", """{"n":1}""", Ct);
        await log.WriteAsync("u2", "b@example.test", "suite.passed", "Alpha/abc123/unit", "Alpha", """{"n":2}""", Ct);
        await log.WriteAsync(null, null, "suite.passed", "Alpha/ABC123/unit", null, null, Ct);

        var latest = await log.FindLatestAsync("suite.passed", "Alpha/abc123/unit", Ct);

        Assert.NotNull(latest);
        Assert.Equal(("u2", "b@example.test", "suite.passed", "Alpha/abc123/unit", "Alpha", """{"n":2}"""),
            (latest.ActorId, latest.ActorEmail, latest.Action, latest.Subject, latest.SubjectName, latest.Detail));
        Assert.True(latest.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Null((await log.FindLatestAsync("suite.passed", "Alpha/ABC123/unit", Ct))!.ActorId);
        Assert.Null(await log.FindLatestAsync("suite.passed", "alpha/abc123/unit", Ct));
        Assert.Null(await log.FindLatestAsync("Suite.Passed", "Alpha/abc123/unit", Ct));
    }

    [Fact]
    public async Task The_latest_row_of_each_subject_is_read_for_the_actions_asked_newest_first()
    {
        var log = await CreateAsync();
        Assert.Empty(await log.FindLatestBySubjectAsync([], Ct));

        await log.WriteAsync(null, null, "sweep.quiet-team", "Alpha", ct: Ct);
        await log.WriteAsync(null, null, "sweep.quiet-team", "Beta", ct: Ct);
        await log.WriteAsync(null, null, "sweep.quiet-team-cleared", "Alpha", ct: Ct);
        await log.WriteAsync(null, null, "sweep.quiet-team", "Alpha", detail: "again", ct: Ct);
        await log.WriteAsync(null, null, "sweep.quiet-team", null, ct: Ct);
        await log.WriteAsync(null, null, "team.created", "Gamma", ct: Ct);

        var latest = await log.FindLatestBySubjectAsync(["sweep.quiet-team", "sweep.quiet-team-cleared", "sweep.quiet-team"], Ct);

        Assert.Equal(
            [("sweep.quiet-team", "Alpha", "again"), ("sweep.quiet-team-cleared", "Alpha", null), ("sweep.quiet-team", "Beta", null)],
            latest.Select(e => (e.Action, e.Subject!, e.Detail)));
        Assert.Equal(latest.Select(e => e.Seq).OrderDescending(), latest.Select(e => e.Seq));
    }

    [Fact]
    public async Task A_page_is_newest_first_below_its_cursor_and_the_total_counts_the_whole_log()
    {
        var log = await CreateAsync();
        Assert.Equal((0, 0L), ((await log.ReadAsync(ct: Ct)).Events.Count, (await log.ReadAsync(ct: Ct)).Total));

        for (var i = 1; i <= 5; i++) await log.WriteAsync(null, null, "team.created", $"t{i}", ct: Ct);

        var first = await log.ReadAsync(take: 2, ct: Ct);
        Assert.Equal(["t5", "t4"], first.Events.Select(e => e.Subject));
        Assert.Equal(5, first.Total);

        var next = await log.ReadAsync(before: first.Events[^1].Seq, take: 2, ct: Ct);
        Assert.Equal(["t3", "t2"], next.Events.Select(e => e.Subject));
        Assert.Equal(5, next.Total);

        await log.WriteAsync(null, null, "team.created", "t6", ct: Ct);
        Assert.Equal(6, (await log.ReadAsync(take: 1, ct: Ct)).Total);
        Assert.Equal(["t6"], (await log.ReadAsync(take: 0, ct: Ct)).Events.Select(e => e.Subject));
    }

    [Fact]
    public async Task A_page_is_never_larger_than_the_cap()
    {
        var database = await DatabaseAsync();
        var log = database.TenantLog();
        for (var i = 0; i < ITenantLog.MaxTake + 5; i++) await log.WriteAsync(null, null, "team.created", $"t{i}", ct: Ct);

        var page = await log.ReadAsync(take: 10_000, ct: Ct);

        Assert.Equal(ITenantLog.MaxTake, page.Events.Count);
        Assert.Equal(ITenantLog.MaxTake + 5, page.Total);
    }

    /// <summary>
    /// A subject is whatever the action names - a document path as long as a path can be - and a
    /// detail any JSON: both come back byte for byte, and the long subject is still found exactly.
    /// </summary>
    [Fact]
    public async Task Long_and_unicode_subjects_and_details_round_trip_and_are_found()
    {
        var log = await CreateAsync();
        var subject = "/data/documents/" + string.Concat(Enumerable.Repeat("dossier-été-🚀/", 120)) + "O'Brien.md";
        var detail = LongUnicode("{\"note\":\"") + "\"}";

        await log.WriteAsync(null, "o'brien@example.test", TenantActions.DocumentUploaded, subject, "O'Brien.md", detail, Ct);

        Assert.True(subject.Length > 1_000);
        var found = await log.FindLatestAsync(TenantActions.DocumentUploaded, subject, Ct);
        Assert.NotNull(found);
        Assert.Equal((subject, detail, "O'Brien.md", "o'brien@example.test"), (found.Subject, found.Detail, found.SubjectName, found.ActorEmail));
        Assert.Equal(subject, Assert.Single(await log.FindLatestBySubjectAsync([TenantActions.DocumentUploaded], Ct)).Subject);
    }
}

public sealed class SqliteTenantLogTests : TenantLogContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerTenantLogTests : TenantLogContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
