using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Harness.SqlServer.Messaging;

namespace Harness.Tests.Contracts;

/// <summary>
/// <see cref="IMessageLog"/>, as each provider must keep it.
///
/// <para>
/// WHERE EACH BEHAVIOUR COMES FROM. A test named as an existing SQLite test is that test, with the
/// same values, asked of the interface instead of <c>ContainerTestBed.Store</c>:
/// <c>MessageTeamIndexTests</c>, <c>TeamTokenTotalsTests</c>, <c>WorkflowEndingsTests</c>,
/// <c>LastSeqBeforeTests</c> and <c>RollupCacheTests</c>. The rest pin the interface's own remarks,
/// which the SQLite suite reaches only through a running host. The last three are Review Focus 2 and
/// 4 and the append-order rule: a reader paging by seq must never skip a row that commits late.
/// </para>
///
/// <para>
/// SPEND IS NOT ASKED HERE. The spend reads and the spend a workflow projection carries come from
/// the ledger tables, which the append writes through the ledger's own rows; their contract is the
/// ledger's.
/// </para>
/// </summary>
public abstract class MessageLogContract : IAsyncDisposable
{
    private static readonly ContainerId Dev = new("Alpha", "Dev");

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<IMessageLog> CreateAsync();

    public abstract ValueTask DisposeAsync();

    private static Task<Message> AppendAsync(IMessageLog log, string type, string payload, string source, long? cause = null) =>
        log.AppendAsync(new NewMessage(type, payload, source, cause), Ct);

    private static async Task<Message> RunAsync(IMessageLog log, ContainerId member, string text, bool finish = true)
    {
        var delivery = await AppendAsync(log, MessageTypes.InstructionFor(member), $$"""{"instruction":"{{text}}"}""", "console");
        await AppendAsync(log, MessageTypes.Started, "{}", member.ToString(), delivery.Seq);
        if (finish)
        {
            await AppendAsync(log, MessageTypes.Completed, """{"tokensIn":10,"tokensOut":5,"output":"done"}""", member.ToString(), delivery.Seq);
        }

        return delivery;
    }

    // ---- MessageTeamIndexTests ----

    [Fact]
    public async Task The_latest_window_narrowed_to_a_team_holds_that_teams_rows_however_many_another_wrote_since()
    {
        var log = await CreateAsync();
        var mine = await AppendAsync(log, MessageTypes.Completed, """{"output":"done"}""", "alpha/Dev");
        for (var i = 0; i < 30; i++)
        {
            await AppendAsync(log, MessageTypes.Completed, """{"output":"noise"}""", "beta/Dev");
        }

        var rows = await log.ReadLatestAsync(0, [MessageTypes.Completed], ["ALPHA"], 10, Ct);

        Assert.Equal([mine.Seq], rows.Select(r => r.Seq));
    }

    [Fact]
    public async Task A_members_latest_terminal_is_one_row_read_above_its_floor()
    {
        var log = await CreateAsync();
        var terminals = new[] { MessageTypes.Completed, MessageTypes.Blocked, MessageTypes.Failed, MessageTypes.NeedsDecision };
        await AppendAsync(log, MessageTypes.Completed, """{"output":"first"}""", "alpha/Dev");
        await AppendAsync(log, MessageTypes.Progress, """{"status":"working"}""", "alpha/Dev");
        var blocked = await AppendAsync(log, MessageTypes.Blocked, """{"reason":"stuck"}""", "alpha/Dev");
        await AppendAsync(log, MessageTypes.Completed, """{"output":"other"}""", "alpha/Ops");
        await AppendAsync(log, MessageTypes.Progress, """{"status":"later"}""", "alpha/Dev");

        Assert.Equal(blocked.Seq, (await log.LatestPublishedAsync("ALPHA/dev", terminals, 0, Ct))?.Seq);
        Assert.Null(await log.LatestPublishedAsync("alpha/Dev", terminals, blocked.Seq, Ct));
    }

    [Fact]
    public async Task A_team_id_with_an_underscore_matches_only_itself()
    {
        var log = await CreateAsync();
        foreach (var team in new[] { "a_b", "axb", "aab" })
        {
            await RunAsync(log, new ContainerId(team, "Dev"), $"work for {team}");
        }

        foreach (var team in new[] { "a_b", "axb" })
        {
            var usage = await log.SumUsageForTeamAsync(team, 0, Ct);
            Assert.Equal([$"{team}/Dev"], usage.Select(row => row.Source));

            var workflows = await log.WorkflowsForTeamAsync(team, 0, Ct);
            var expected = (await log.ReadRangeAsync(0, int.MaxValue, Ct))
                .Where(row => string.Equals(MessageTeam.Of(row), team, StringComparison.OrdinalIgnoreCase))
                .Select(row => row.CorrelationId)
                .Distinct()
                .ToList();
            Assert.Equal(expected.Count, workflows.TotalCount);
        }
    }

    [Fact]
    public async Task A_team_is_matched_whatever_its_case()
    {
        var log = await CreateAsync();
        await RunAsync(log, new ContainerId("Alpha", "Dev"), "go");

        var usage = await log.SumUsageForTeamAsync("alpha", 0, Ct);

        Assert.Equal(["Alpha/Dev"], usage.Select(row => row.Source));
    }

    // ---- TeamTokenTotalsTests ----

    private static string Split(int tokensIn, int tokensOut, int cachedIn, int cacheCreation, string source = "claude") =>
        $$"""{"output":"done","tokensIn":{{tokensIn}},"tokensOut":{{tokensOut}},"tokensTotal":null,"tokensCachedIn":{{cachedIn}},"tokensCacheCreation":{{cacheCreation}},"tokensReasoning":null,"tokensSource":"{{source}}"}""";

    private static string Combined(int total) =>
        $$"""{"output":"done","tokensIn":null,"tokensOut":null,"tokensTotal":{{total}},"tokensCachedIn":null,"tokensCacheCreation":null,"tokensReasoning":null,"tokensSource":"codex"}""";

    [Fact]
    public async Task Cache_reads_and_writes_are_summed_beside_tokensIn_and_billed_at_the_spend_weights()
    {
        var log = await CreateAsync();

        // Below the floor: a predecessor's row, not counted.
        var old = await AppendAsync(log, MessageTypes.Completed, Split(5, 5, 5_000_000, 5_000_000), "Alpha/Manager");

        await AppendAsync(log, MessageTypes.Completed, Split(1_000, 200, 50_000, 4_000), "Alpha/Manager");
        // Integer division per row, as the spend SQL does: 19 / 10 = 1, 3 * 5 / 4 = 3.
        await AppendAsync(log, MessageTypes.Failed, Split(7, 3, 19, 3), "Alpha/Manager");
        await AppendAsync(log, MessageTypes.Completed, Split(999_999, 1, 999_999, 1, UsageSource.ExcludedEstimate), "Alpha/Manager");
        await AppendAsync(log, MessageTypes.Completed, Combined(9_000), "Alpha/Writer");
        await AppendAsync(log, MessageTypes.Completed, """{"output":"before capture"}""", "Alpha/Old");

        var rows = await log.SumUsageForTeamAsync("Alpha", old.Seq, Ct);
        var manager = rows.Single(r => r.Source == "Alpha/Manager");
        var writer = rows.Single(r => r.Source == "Alpha/Writer");
        var unmeasured = rows.Single(r => r.Source == "Alpha/Old");

        Assert.Equal(1_007, manager.TokensIn);
        Assert.Equal(203, manager.TokensOut);
        Assert.Equal(50_019, manager.TokensCachedIn);
        Assert.Equal(4_003, manager.TokensCacheCreation);

        var expected =
            new InvocationUsage(1_000, 200, "claude", cachedIn: 50_000, cacheCreation: 4_000).BillableTokens()
            + new InvocationUsage(7, 3, "claude", cachedIn: 19, cacheCreation: 3).BillableTokens();
        Assert.Equal(11_214, expected);
        Assert.Equal(expected, manager.TokensBillable);

        Assert.Equal(InvocationUsage.Combined(9_000, "codex").BillableTokens(), writer.TokensBillable);
        Assert.Equal(0, writer.TokensCachedIn);
        Assert.Equal(0, unmeasured.TokensBillable);

        // Per member: measured figures where there are some, null where nothing was measured.
        var managerLine = MemberTokenTotals.FromUsage("Manager", "claude-headless", manager, true);
        Assert.Equal(1_007, managerLine.TokensIn);
        Assert.Equal(50_019, managerLine.TokensCachedIn);
        Assert.Equal(4_003, managerLine.TokensCacheCreation);
        Assert.Equal(11_214, managerLine.TokensBillable);

        var writerLine = MemberTokenTotals.FromUsage("Writer", "codex-headless", writer, true);
        Assert.Null(writerLine.TokensIn);
        Assert.Null(writerLine.TokensCachedIn);
        Assert.Null(writerLine.TokensCacheCreation);
        Assert.Equal(9_000, writerLine.TokensBillable);

        var oldLine = MemberTokenTotals.FromUsage("Old", "claude-headless", unmeasured, true);
        Assert.Null(oldLine.TokensCachedIn);
        Assert.Null(oldLine.TokensCacheCreation);
        Assert.Null(oldLine.TokensBillable);
        Assert.Equal(1, oldLine.Runs);
    }

    // ---- WorkflowEndingsTests ----

    [Fact]
    public async Task A_second_read_takes_only_the_rows_written_since_and_a_reset_starts_over()
    {
        var store = await CreateAsync();

        async Task<long> WorkflowAsync(string team) =>
            (await AppendAsync(store, MessageTypes.InstructionFor(new ContainerId(team, "Manager")), """{"text":"go"}""", "person-1")).Seq;

        var first = await WorkflowAsync("Alpha");
        await AppendAsync(store, MessageTypes.WorkflowCompleted, "{}", "Alpha/Manager", first);

        var log = CountingProxy.Over(store);
        var endings = new WorkflowEndings(log);

        var state = await endings.ReadAsync(Ct);
        Assert.Equal([first], state["Alpha"].EndedWorkflowCorrelations);
        Assert.Equal([first], state["Alpha"].DeclaredWorkflowCorrelations);
        var reads = CountingProxy.Of(log).CallsTo("ReadAfterAsync");
        Assert.True(reads >= 1);

        // NOTHING NEW: one look past the last row, and the same answer.
        state = await endings.ReadAsync(Ct);
        Assert.Equal(reads + 1, CountingProxy.Of(log).CallsTo("ReadAfterAsync"));
        Assert.Equal([first], state["Alpha"].EndedWorkflowCorrelations);

        // A WORKFLOW CLOSED SINCE is ended and not declared, and is read without the first one again.
        var second = await WorkflowAsync("Alpha");
        await AppendAsync(store, MessageTypes.WorkflowClosed, """{"team":"Alpha","reason":"done"}""", "person-1", second);

        state = await endings.ReadAsync(Ct);
        Assert.Equal(new HashSet<long> { first, second }, state["Alpha"].EndedWorkflowCorrelations);
        Assert.Equal([first], state["Alpha"].DeclaredWorkflowCorrelations);

        // A RESET STARTS OVER, so an ending row a purge removed does not stay ended here.
        var before = CountingProxy.Of(log).CallsTo("ReadAfterAsync");
        endings.Invalidate();
        state = await endings.ReadAsync(Ct);
        Assert.True(CountingProxy.Of(log).CallsTo("ReadAfterAsync") > before);
        Assert.Equal(new HashSet<long> { first, second }, state["Alpha"].EndedWorkflowCorrelations);
    }

    // ---- LastSeqBeforeTests ----

    [Fact]
    public async Task An_empty_log_answers_zero()
    {
        var log = await CreateAsync();

        Assert.Equal(0, await log.LastSeqBeforeAsync(new DateTimeOffset(2001, 2, 3, 4, 0, 0, TimeSpan.Zero), Ct));
    }

    /// <summary>LastSeqBeforeTests' rules over rows appended through the interface: a row stamped at
    /// the instant is not before it, and a gap a purge leaves answers what a scan would.</summary>
    [Fact]
    public async Task The_position_of_an_instant_is_the_last_row_written_before_it()
    {
        var log = await CreateAsync();
        var rows = new List<Message>();
        for (var i = 0; i < 5; i++)
        {
            rows.Add(await AppendAsync(log, "test.mark", "{}", "console"));
            await Task.Delay(5, Ct);
        }

        Assert.Equal(0, await log.LastSeqBeforeAsync(rows[0].OccurredAt, Ct));
        Assert.Equal(rows[1].Seq, await log.LastSeqBeforeAsync(rows[2].OccurredAt, Ct));
        Assert.Equal(rows[4].Seq, await log.LastSeqBeforeAsync(rows[4].OccurredAt.AddTicks(1), Ct));

        await log.DeleteAsync([rows[1].Seq, rows[2].Seq, rows[3].Seq], Ct);

        Assert.Equal(rows[0].Seq, await log.LastSeqBeforeAsync(rows[3].OccurredAt, Ct));
        Assert.Equal(rows[0].Seq, await log.LastSeqBeforeAsync(rows[4].OccurredAt, Ct));
    }

    // ---- RollupCacheTests ----

    private static int Reads(IMessageLog log) =>
        CountingProxy.Of(log).CallsTo(nameof(IMessageLog.ElapsedForTeamAsync))
        + CountingProxy.Of(log).CallsTo(nameof(IMessageLog.WorkflowsForTeamAsync));

    [Fact]
    public async Task A_persons_close_of_a_workflow_re_reads_the_team_although_no_member_wrote_a_row()
    {
        var store = await CreateAsync();
        var root = await RunAsync(store, new ContainerId("alpha", "Dev"), "one", finish: false);
        var log = CountingProxy.Over(store);
        var cache = new RollupCache(log);
        var open = await cache.ReadAsync("alpha", 0, Ct);
        Assert.Equal(1, open.Open.OpenCount);

        // A PERSON'S CLOSE: its source is the person, so the generated team column is null for it and
        // the team's highest member-sourced seq does not move. The row still ends the workflow.
        await AppendAsync(store, MessageTypes.WorkflowClosed, """{"team":"alpha","reason":"done"}""", "person-1", root.Seq);

        var after = await cache.ReadAsync("alpha", 0, Ct);
        Assert.Equal(4, Reads(log));
        Assert.Equal(0, after.Open.OpenCount);
        Assert.Equal(1, after.Open.TotalCount);
    }

    [Fact]
    public async Task A_floor_that_rose_and_an_invalidate_each_re_read()
    {
        var store = await CreateAsync();
        var alpha = new ContainerId("alpha", "Dev");
        var early = await RunAsync(store, alpha, "one");
        await RunAsync(store, alpha, "two");
        var log = CountingProxy.Over(store);
        var cache = new RollupCache(log);
        await cache.ReadAsync("alpha", 0, Ct);

        var raised = await cache.ReadAsync("alpha", early.Seq + 2, Ct);
        Assert.Equal(4, Reads(log));
        Assert.Equal(1, raised.Open.TotalCount);

        await cache.ReadAsync("alpha", early.Seq + 2, Ct);
        Assert.Equal(4, Reads(log));

        cache.Invalidate();
        await cache.ReadAsync("alpha", early.Seq + 2, Ct);
        Assert.Equal(6, Reads(log));
    }

    [Fact]
    public async Task A_run_in_flight_is_answered_the_same_from_the_cache_but_for_the_clock()
    {
        var log = await CreateAsync();
        await RunAsync(log, new ContainerId("alpha", "Dev"), "going", finish: false);
        var cache = new RollupCache(log);

        var first = await cache.ReadAsync("alpha", 0, Ct);
        var second = await cache.ReadAsync("alpha", 0, Ct);
        var fresh = await log.ElapsedForTeamAsync("alpha", 0, Ct);

        Assert.True(first.Elapsed.RunsUnfinished > 0);
        // Serialised: the record's list members compare by reference.
        Assert.Equal(
            JsonSerializer.Serialize(fresh with { ServerNow = default }),
            JsonSerializer.Serialize(second.Elapsed with { ServerNow = default }));
    }

    // ---- The interface's own remarks ----

    [Fact]
    public async Task A_root_correlates_to_itself_and_a_caused_row_inherits_its_workflow_one_deeper()
    {
        var log = await CreateAsync();

        var root = await AppendAsync(log, MessageTypes.InstructionFor(Dev), """{"instruction":"go"}""", "console");
        var started = await AppendAsync(log, MessageTypes.Started, "{}", Dev.ToString(), root.Seq);
        var done = await AppendAsync(log, MessageTypes.Completed, """{"output":"done"}""", Dev.ToString(), started.Seq);

        Assert.Equal((root.Seq, 0, (long?)null), (root.CorrelationId, root.Depth, root.CausationSeq));
        Assert.Equal((root.Seq, 1, (long?)root.Seq), (started.CorrelationId, started.Depth, started.CausationSeq));
        Assert.Equal((root.Seq, 2, (long?)started.Seq), (done.CorrelationId, done.Depth, done.CausationSeq));

        Assert.Equal([root, started, done], await log.ReadCorrelationAsync(root.Seq, Ct));
        Assert.Equal(started, await log.FindAsync(started.Seq, Ct));
        Assert.Null(await log.FindAsync(done.Seq + 1, Ct));
        Assert.Equal(done.Seq, await log.HighestSeqAsync(Ct));
    }

    [Fact]
    public async Task A_cause_that_does_not_exist_is_refused_and_nothing_is_stored()
    {
        var log = await CreateAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppendAsync(log, MessageTypes.Started, "{}", Dev.ToString(), 999));

        Assert.Equal("Message 999 does not exist, so nothing can be caused by it.", refused.Message);
        Assert.Equal(0, await log.HighestSeqAsync(Ct));
    }

    [Fact]
    public async Task A_url_carrying_userinfo_is_stored_without_it_and_an_empty_payload_as_an_object()
    {
        var log = await CreateAsync();

        var stored = await AppendAsync(log, MessageTypes.Progress, """{"output":"cloned https://bob:s3cret@example.com/x.git"}""", Dev.ToString());
        var empty = await AppendAsync(log, MessageTypes.Progress, "", Dev.ToString());

        Assert.DoesNotContain("s3cret", stored.Payload, StringComparison.Ordinal);
        Assert.Equal(stored.Payload, (await log.FindAsync(stored.Seq, Ct))!.Payload);
        Assert.Equal("", empty.Payload);
        Assert.Equal("{}", (await log.FindAsync(empty.Seq, Ct))!.Payload);
    }

    [Fact]
    public async Task An_append_within_commits_with_the_callers_work_or_not_at_all()
    {
        var log = await CreateAsync();

        Assert.Null(await log.AppendWithinAsync((_, _, _) => Task.FromResult<NewMessage?>(null), (_, _, _, _) => Task.CompletedTask, Ct));
        Assert.Equal(0, await log.HighestSeqAsync(Ct));

        Message? seen = null;
        var appended = await log.AppendWithinAsync(
            (_, _, _) => Task.FromResult<NewMessage?>(new NewMessage(MessageTypes.Progress, """{"status":"one"}""", Dev.ToString())),
            (_, _, row, _) =>
            {
                seen = row;
                return Task.CompletedTask;
            },
            Ct);
        Assert.Equal(appended, seen);
        Assert.Equal(appended, await log.FindAsync(appended!.Seq, Ct));

        await Assert.ThrowsAsync<IOException>(() => log.AppendWithinAsync(
            (_, _, _) => Task.FromResult<NewMessage?>(new NewMessage(MessageTypes.Progress, """{"status":"two"}""", Dev.ToString())),
            (_, _, _, _) => throw new IOException("after failed"),
            Ct));
        Assert.Equal(appended.Seq, await log.HighestSeqAsync(Ct));
    }

    [Fact]
    public async Task No_types_means_no_interest_and_a_read_after_is_oldest_first_by_type_and_capped()
    {
        var log = await CreateAsync();
        var a1 = await AppendAsync(log, "test.a", "{}", "console");
        await AppendAsync(log, "test.b", "{}", "console");
        var a2 = await AppendAsync(log, "test.a", "{}", "console");
        var a3 = await AppendAsync(log, "test.a", "{}", "console");

        Assert.Empty(await log.ReadAfterAsync(0, [], 10, Ct));
        Assert.Empty(await log.ReadAfterAsync(0, ["test.a"], 0, Ct));
        Assert.Equal([a1.Seq, a2.Seq], (await log.ReadAfterAsync(0, ["test.a"], 2, Ct)).Select(r => r.Seq));
        Assert.Equal([a2.Seq, a3.Seq], (await log.ReadAfterAsync(a1.Seq, ["test.a", "test.none"], 10, Ct)).Select(r => r.Seq));
        Assert.Equal(4, (await log.ReadRangeAsync(0, 10, Ct)).Count);
        Assert.Equal([a3.Seq], (await log.ReadRangeAsync(a2.Seq, 10, Ct)).Select(r => r.Seq));
    }

    [Fact]
    public async Task The_latest_window_is_the_newest_rows_oldest_first_and_holds_an_addressed_instruction_whatever_the_types()
    {
        var log = await CreateAsync();
        var done = await AppendAsync(log, MessageTypes.Completed, """{"output":"done"}""", Dev.ToString());
        var told = await AppendAsync(log, MessageTypes.InstructionFor(Dev), """{"instruction":"again"}""", "console");
        await AppendAsync(log, MessageTypes.Progress, """{"status":"working"}""", Dev.ToString());

        Assert.Equal([done.Seq, told.Seq], (await log.ReadLatestAsync(0, [MessageTypes.Completed], 10, Ct)).Select(r => r.Seq));
        Assert.Equal([told.Seq], (await log.ReadLatestAsync(0, [MessageTypes.Completed], 1, Ct)).Select(r => r.Seq));
        Assert.Empty(await log.ReadLatestAsync(0, [], 10, Ct));
    }

    [Fact]
    public async Task A_containers_own_rows_are_read_newest_first_above_its_floor_whatever_the_case()
    {
        var log = await CreateAsync();
        var below = await AppendAsync(log, MessageTypes.Progress, "{}", "Alpha/Dev");
        var first = await AppendAsync(log, MessageTypes.Progress, "{}", "alpha/dev");
        await AppendAsync(log, MessageTypes.Progress, "{}", "Alpha/Ops");
        var second = await AppendAsync(log, MessageTypes.Completed, "{}", "Alpha/Dev");

        Assert.Equal([second.Seq, first.Seq], (await log.ReadForContainerAsync("ALPHA/DEV", below.Seq, 10, Ct)).Select(r => r.Seq));
        Assert.Equal([second.Seq], (await log.ReadForContainerAsync("Alpha/Dev", below.Seq, 1, Ct)).Select(r => r.Seq));
    }

    [Fact]
    public async Task A_members_older_page_holds_what_it_was_told_and_what_it_published_of_the_feeds_types()
    {
        var log = await CreateAsync();
        var told = await AppendAsync(log, MessageTypes.InstructionFor(Dev), """{"instruction":"go"}""", "console");
        var done = await AppendAsync(log, MessageTypes.Completed, "{}", "alpha/DEV", told.Seq);
        await AppendAsync(log, MessageTypes.Progress, "{}", Dev.ToString(), told.Seq);
        await AppendAsync(log, MessageTypes.Completed, "{}", "Alpha/Ops");
        var head = await log.HighestSeqAsync(Ct);

        Assert.Equal([done.Seq, told.Seq], (await log.ReadMemberBeforeAsync(Dev, 0, head + 1, [MessageTypes.Completed], 10, Ct)).Select(r => r.Seq));
        Assert.Equal([told.Seq], (await log.ReadMemberBeforeAsync(Dev, 0, done.Seq, [MessageTypes.Completed], 10, Ct)).Select(r => r.Seq));
        Assert.Equal([told.Seq], (await log.ReadMemberBeforeAsync(Dev, 0, head + 1, [], 10, Ct)).Select(r => r.Seq));
        Assert.Empty(await log.ReadMemberBeforeAsync(Dev, told.Seq, head + 1, [], 10, Ct));
    }

    [Fact]
    public async Task A_finished_run_is_read_with_its_start_and_whether_it_blocked()
    {
        var log = await CreateAsync();
        var told = await AppendAsync(log, MessageTypes.InstructionFor(Dev), """{"instruction":"go"}""", "console");
        var started = await AppendAsync(log, MessageTypes.Started, "{}", Dev.ToString(), told.Seq);
        await AppendAsync(log, MessageTypes.Blocked, """{"reason":"need a key"}""", Dev.ToString(), told.Seq);
        var transcript = await AppendAsync(
            log, MessageTypes.Completed, $$"""{"output":"done","{{PayloadFields.AgentTranscript}}":"{{new string('t', 5_000)}}"}""", Dev.ToString(), told.Seq);

        var again = await AppendAsync(log, MessageTypes.InstructionFor(Dev), """{"instruction":"again"}""", "console");
        await AppendAsync(log, MessageTypes.Started, "{}", Dev.ToString(), again.Seq);
        var plain = await AppendAsync(log, MessageTypes.Completed, """{"output":"done"}""", Dev.ToString(), again.Seq);
        var head = await log.HighestSeqAsync(Ct);

        var run = Assert.Single(await log.ReadRunsAsync(new ContainerId("alpha", "dev"), 0, head + 1, 10, Ct));
        Assert.Equal(transcript, run.Terminal);
        Assert.Equal(started.OccurredAt, run.StartedAt);
        Assert.True(run.Blocked);
        Assert.False(run.InLatestRun);

        var any = await log.ReadRunsAsync(Dev, 0, head + 1, 10, RunsWith.AnyRun, Ct);
        Assert.Equal([plain.Seq, transcript.Seq], any.Select(r => r.Terminal.Seq));
        Assert.False(any[0].Blocked);
    }

    [Fact]
    public async Task A_members_marks_hold_until_it_starts_again_and_a_failure_until_it_completes()
    {
        var log = await CreateAsync();
        Assert.Same(ContainerMarks.None, await log.LiveMarksForAsync(Dev, 0, Ct));

        var blocked = await AppendAsync(log, MessageTypes.Blocked, """{"reason":"need a key"}""", Dev.ToString());
        await AppendAsync(log, MessageTypes.Completed, "{}", Dev.ToString());
        var failed = await AppendAsync(log, MessageTypes.Failed, """{"error":"boom"}""", "alpha/dev");

        var marks = await log.LiveMarksForAsync(Dev, 0, Ct);
        Assert.Equal(blocked, marks.Blocked);
        Assert.Null(marks.NeedsDecision);
        Assert.Equal(failed, marks.Failed);
        Assert.Same(ContainerMarks.None, await log.LiveMarksForAsync(Dev, failed.Seq, Ct));

        await AppendAsync(log, MessageTypes.Started, "{}", Dev.ToString());
        await AppendAsync(log, MessageTypes.Completed, "{}", Dev.ToString());

        Assert.Same(ContainerMarks.None, await log.LiveMarksForAsync(Dev, 0, Ct));
    }

    [Fact]
    public async Task A_closed_workflow_is_not_open_until_an_instruction_wakes_it()
    {
        var log = await CreateAsync();
        var running = await AppendAsync(log, MessageTypes.InstructionFor(Dev), """{"instruction":"one"}""", "console");
        var closed = await AppendAsync(log, MessageTypes.InstructionFor(Dev), """{"instruction":"two"}""", "console");
        await AppendAsync(log, MessageTypes.WorkflowClosed, """{"team":"Alpha"}""", "person-1", closed.Seq);
        var woken = await AppendAsync(log, MessageTypes.InstructionFor(Dev), """{"instruction":"three"}""", "console");
        var close = await AppendAsync(log, MessageTypes.WorkflowCompleted, "{}", "Alpha/Manager", woken.Seq);
        await AppendAsync(log, MessageTypes.InstructionFor(Dev), """{"instruction":"more"}""", "console", close.Seq);

        Assert.Empty(await log.OpenWorkflowsAmongAsync([], Ct));
        Assert.Equal(
            new HashSet<long> { running.Seq, woken.Seq },
            (await log.OpenWorkflowsAmongAsync([running.Seq, closed.Seq, woken.Seq, woken.Seq, 9_999], Ct)).ToHashSet());
    }

    [Fact]
    public async Task Each_workflow_stands_where_its_own_rows_put_it()
    {
        var log = await CreateAsync();
        var failing = await RunAsync(log, Dev, "fail", finish: false);
        await AppendAsync(log, MessageTypes.Failed, """{"error":"boom"}""", Dev.ToString(), failing.Seq);
        var stopped = await RunAsync(log, Dev, "stop", finish: false);
        await AppendAsync(log, MessageTypes.Failed, $$"""{"{{PayloadFields.StoppedByPerson}}":true}""", Dev.ToString(), stopped.Seq);
        var done = await RunAsync(log, Dev, "done");
        var completed = await AppendAsync(log, MessageTypes.WorkflowCompleted, "{}", "Alpha/Manager", done.Seq);
        var blocked = await RunAsync(log, Dev, "block", finish: false);
        await AppendAsync(log, MessageTypes.Blocked, """{"reason":"key"}""", Dev.ToString(), blocked.Seq);
        var otherTeams = await RunAsync(log, new ContainerId("Beta", "Dev"), "theirs", finish: false);
        await AppendAsync(log, MessageTypes.Failed, """{"error":"boom"}""", "Beta/Dev", otherTeams.Seq);

        Assert.Empty(await log.WorkflowStandingsAmongAsync(new Dictionary<long, string>(), Ct));
        var standings = await log.WorkflowStandingsAmongAsync(
            new Dictionary<long, string>
            {
                [failing.Seq] = "alpha", [stopped.Seq] = "Alpha", [done.Seq] = "Alpha",
                [blocked.Seq] = "Alpha", [otherTeams.Seq] = "Alpha", [9_999] = "Alpha",
            },
            Ct);

        Assert.Equal(new WorkflowStanding(failing.Seq, true, null, "Failed"), standings[failing.Seq]);
        Assert.Equal(new WorkflowStanding(stopped.Seq, true, null, null), standings[stopped.Seq]);
        Assert.Equal(new WorkflowStanding(done.Seq, false, completed.OccurredAt, null), standings[done.Seq]);
        Assert.Equal(new WorkflowStanding(blocked.Seq, true, null, "Blocked"), standings[blocked.Seq]);
        Assert.Equal(new WorkflowStanding(otherTeams.Seq, true, null, null), standings[otherTeams.Seq]);
        Assert.False(standings.ContainsKey(9_999));
    }

    [Fact]
    public async Task A_teams_stretch_runs_from_its_earliest_workflow_row_to_its_newest()
    {
        var log = await CreateAsync();
        Assert.Null(await log.WorkflowStretchForTeamAsync("Alpha", 0, Ct));

        var first = await RunAsync(log, Dev, "one");
        await RunAsync(log, new ContainerId("Beta", "Dev"), "theirs");
        await RunAsync(log, Dev, "two");
        var last = (await log.ReadCorrelationAsync(await log.HighestSeqAsync(Ct) - 2, Ct)).Last();
        await AppendAsync(log, MessageTypes.Progress, "{}", "Beta/Dev");

        Assert.Equal(new TeamWorkflowStretch(first.OccurredAt, last.OccurredAt, false), await log.WorkflowStretchForTeamAsync("alpha", 0, Ct));

        var floored = await log.WorkflowStretchForTeamAsync("Alpha", first.Seq, Ct);
        Assert.True(floored!.RootedBeforeFloor);
        Assert.True(floored.From > first.OccurredAt);
    }

    [Fact]
    public async Task A_teams_workflow_is_described_by_its_root_its_runs_and_its_ending()
    {
        var log = await CreateAsync();
        var root = await AppendAsync(log, MessageTypes.InstructionFor(Dev), """{"subject":"Fix the login page","instruction":"Fix the login page"}""", "console");
        await AppendAsync(log, MessageTypes.Started, "{}", Dev.ToString(), root.Seq);
        var done = await AppendAsync(log, MessageTypes.Completed, """{"output":"done"}""", Dev.ToString(), root.Seq);

        var timing = await log.ElapsedForTeamAsync("ALPHA", 0, Ct);
        Assert.True(timing.Available);
        Assert.Equal(root.Seq, timing.Correlation);
        Assert.Equal("Undeclared", timing.State);
        Assert.Equal("Fix the login page", timing.Subject);
        Assert.Equal(root.OccurredAt, timing.StartedAt);
        Assert.Equal(done.OccurredAt, timing.LastActivityAt);
        Assert.Null(timing.EndedAt);
        Assert.Equal(1, timing.RunsCounted);
        Assert.Equal(["Dev"], timing.Members.Select(m => m.Member));

        await AppendAsync(log, MessageTypes.Failed, """{"error":"boom"}""", Dev.ToString(), root.Seq);
        Assert.Equal("Failed", (await log.WorkflowForTeamAsync("Alpha", root.Seq, 0, Ct)).State);

        var close = await AppendAsync(log, MessageTypes.WorkflowClosed, """{"team":"Alpha"}""", "person-1", root.Seq);
        var closed = await log.WorkflowForTeamAsync("Alpha", root.Seq, 0, Ct);
        Assert.Equal("Closed", closed.State);
        Assert.Equal(close.OccurredAt, closed.EndedAt);

        var all = await log.WorkflowsForTeamAsync("Alpha", 0, Ct);
        Assert.Equal((true, 0, 1), (all.Available, all.OpenCount, all.TotalCount));
        Assert.Equal("Closed", Assert.Single(all.Workflows).State);
        Assert.False((await log.ElapsedForTeamAsync("Beta", 0, Ct)).Available);
    }

    [Fact]
    public async Task A_teams_highest_seq_counts_a_persons_row_addressed_to_it_and_no_other_teams()
    {
        var log = await CreateAsync();
        await AppendAsync(log, MessageTypes.Progress, "{}", "Alpha/Dev");
        var told = await AppendAsync(log, MessageTypes.InstructionFor(new ContainerId("alpha", "Ops")), "{}", "console");
        await AppendAsync(log, MessageTypes.Progress, "{}", "Beta/Dev");

        Assert.Equal(told.Seq, await log.TeamHighestSeqAsync("Alpha", Ct));

        var closed = await AppendAsync(log, MessageTypes.WorkflowClosed, """{"team":"ALPHA"}""", "person-1");
        await AppendAsync(log, MessageTypes.Progress, "{}", "Beta/Dev");

        Assert.Equal(closed.Seq, await log.TeamHighestSeqAsync("alpha", Ct));
        Assert.Equal(0, await log.TeamHighestSeqAsync("Gamma", Ct));
    }

    [Fact]
    public async Task A_purge_keeps_what_a_survivor_cites_and_takes_a_chain_wholly_inside_the_set()
    {
        var log = await CreateAsync();
        var root = await AppendAsync(log, MessageTypes.InstructionFor(Dev), "{}", "console");
        var cited = await AppendAsync(log, MessageTypes.Started, "{}", Dev.ToString(), root.Seq);
        var leaf = await AppendAsync(log, MessageTypes.Completed, "{}", Dev.ToString(), cited.Seq);
        var survivor = await AppendAsync(log, MessageTypes.Progress, "{}", Dev.ToString(), cited.Seq);

        Assert.Equal(new PurgeReport(1, 2), await log.DeleteAsync([root.Seq, cited.Seq, leaf.Seq], Ct));
        Assert.Null(await log.FindAsync(leaf.Seq, Ct));

        Assert.Equal(new PurgeReport(3, 0), await log.DeleteAsync([survivor.Seq, cited.Seq, root.Seq], Ct));
        Assert.Equal(0, await log.HighestSeqAsync(Ct));
        Assert.Equal(new PurgeReport(0, 0), await log.DeleteAsync([], Ct));
    }

    // ---- Review Focus 2 and 4, and the append-order rule ----

    /// <summary>
    /// REVIEW FOCUS 2: fifty appends at once are read back in strictly increasing seq with no gap
    /// between them once all have committed.
    /// </summary>
    [Fact]
    public async Task Concurrent_appends_keep_sequence_order()
    {
        var log = await CreateAsync();

        var appended = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            Task.Run(() => AppendAsync(log, "test.parallel", $$"""{"n":{{i}}}""", "console"), Ct)));

        var read = (await log.ReadAfterAsync(0, ["test.parallel"], 1_000, Ct)).Select(r => r.Seq).ToList();

        Assert.Equal(appended.Select(m => m.Seq).Order(), read);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => read[0] + i), read);
    }

    /// <summary>
    /// REVIEW FOCUS 4: a 200 KB payload with emoji, accents, CJK and a quote is stored and read back
    /// identical, through the append, a find and a page read.
    /// </summary>
    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var log = await CreateAsync();
        var text = string.Concat(Enumerable.Repeat("O'Brien \U0001F98A caf\u00E9 \u65E5\u672C ", 11_000));
        Assert.True(text.Length > 200_000);
        var payload = $$"""{"output":"{{text}}"}""";

        var stored = await AppendAsync(log, MessageTypes.Progress, payload, "O'Brien/Dev");

        Assert.Equal(payload, stored.Payload);
        Assert.Equal(payload, (await log.FindAsync(stored.Seq, Ct))!.Payload);
        var read = Assert.Single(await log.ReadAfterAsync(0, [MessageTypes.Progress], 10, Ct));
        Assert.Equal(payload, read.Payload);
        Assert.Equal("O'Brien/Dev", read.Source);
    }

    /// <summary>
    /// APPENDS COMMIT IN SEQ ORDER. Every reader pages forward by seq and advances past what it saw
    /// (a cursor, a feed, the endings view), so a row that took a lower seq but committed after a
    /// higher one would be skipped for good. While writers append at once, a reader that keeps
    /// reading after the last seq it saw ends having seen every row written, each once, in order.
    /// </summary>
    [Fact]
    public async Task A_reader_polling_during_concurrent_appends_misses_nothing()
    {
        var log = await CreateAsync();
        const string type = "test.polled";

        var writers = Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(async () =>
        {
            var seqs = new List<long>();
            for (var i = 0; i < 25; i++) seqs.Add((await AppendAsync(log, type, $$"""{"w":{{writer}},"i":{{i}}}""", "console")).Seq);
            return seqs;
        }, Ct)));

        var seen = new List<long>();
        var lastSeen = 0L;

        async Task<bool> PollAsync()
        {
            var page = await log.ReadAfterAsync(lastSeen, [type], 1_000, Ct);
            seen.AddRange(page.Select(r => r.Seq));
            if (page.Count > 0) lastSeen = page[^1].Seq;
            return page.Count > 0;
        }

        while (!writers.IsCompleted) await PollAsync();
        while (await PollAsync()) { }

        var written = (await writers).SelectMany(s => s).Order().ToList();
        Assert.Equal(200, written.Count);
        Assert.Equal(written, seen);
    }
}

public sealed class SqliteMessageLogTests : MessageLogContract
{
    private readonly SqliteContractDatabase _database = new();

    protected override Task<IMessageLog> CreateAsync() => Task.FromResult<IMessageLog>(new SqliteMessageStore(_database.Path));

    public override ValueTask DisposeAsync() => _database.DisposeAsync();
}

public sealed class SqlServerMessageLogTests : MessageLogContract
{
    private SqlServerContractDatabase? _database;

    protected override async Task<IMessageLog> CreateAsync()
    {
        _database = await SqlServerContractDatabase.CreateAsync(Ct);
        await _database.EnsureLedgerReadTablesAsync(Ct);
        return new SqlServerMessageStore(_database.ConnectionString);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }
}
