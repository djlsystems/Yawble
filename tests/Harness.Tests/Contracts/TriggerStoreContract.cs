using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests.Contracts;

/// <summary>
/// WHAT EVERY <see cref="ITriggerStore"/> DOES, on SQLite and on SQL Server alike. Taken from
/// <c>TriggerAuditTransactionTests</c> and <c>FreshVolumeTests</c>' trigger tests (same names, same
/// values; a raw insert without a column stands for a row from before the step that added it), and
/// from the interface's and the SQLite store's remarks: narrow outcome, fire, skip and poll writes, a
/// moved watch starting from a fresh baseline, a team and a container folding case. Plus the
/// providers' differences made into rules: a trailing space, two writers at once, and a team deleted
/// while its triggers are saved.
/// </summary>
public abstract class TriggerStoreContract : IdentityContract
{
    private const string At = "2026-09-01T00:00:00Z";

    private static readonly TriggerAudit ChangeAudit =
        new("u1", "person@example.test", TenantActions.ScheduleChanged, "t1", "Poll", """{"team":"t"}""");

    protected async Task<ITriggerStore> CreateAsync() => (await DatabaseAsync()).Triggers();

    private async Task TeamAsync(string id) =>
        await (await DatabaseAsync()).ExecuteAsync($"INSERT INTO teams (id, created_utc) VALUES ('{id}', '{At}')");

    private static TriggerRow Row(
        string id, string team = "t", string container = "Dev", DateTimeOffset? createdAt = null, long? cap = 1000) =>
        new(
            id, team, container, "Poll", "look", "every", null, null, 60, null,
            IdleOnly: false, Enabled: true, NextDueAt: null, LastFiredAt: null, LastOutcome: null,
            LastSeq: null, MissedCount: 0, CreatedAt: createdAt ?? DateTimeOffset.UnixEpoch, CreatedBy: "person",
            DailyTokenCap: cap);

    /// <summary>A trigger <c>t1</c> on team <c>t</c>, as <c>TriggerAuditTransactionTests</c> makes it.</summary>
    private async Task<ITriggerStore> StoreWithATriggerAsync()
    {
        var store = await CreateAsync();
        await TeamAsync("t");
        await store.SaveAsync(Row("t1"), Ct);
        return store;
    }

    /// <summary>A trigger row from before the later columns: what SQLite's steps leave on an old volume.</summary>
    private async Task OldTriggerAsync(string kind = "every", string interval = "300", string capColumn = "", string capValue = "")
    {
        var database = await DatabaseAsync();
        await TeamAsync("old-team");
        await database.ExecuteAsync(
            $"""
             INSERT INTO triggers
                 (id, team, container, name, instruction, kind, interval_seconds, idle_only, enabled,
                  missed_count, created_at, created_by{capColumn})
             VALUES
                 ('t1', 'old-team', 'Manager', 'Poll', 'look', '{kind}', {interval}, 1, 1, 0,
                  '{At}', 'person'{capValue})
             """);
    }

    [Fact]
    public async Task A_change_is_saved_with_its_tenant_row()
    {
        var store = await StoreWithATriggerAsync();
        var row = (await store.FindAsync("t1", Ct))!;

        await store.SaveAsync(row with { DailyTokenCap = 5000 }, ChangeAudit, Ct);

        Assert.Equal(5000, (await store.FindAsync("t1", Ct))!.DailyTokenCap);
        var logged = await (await DatabaseAsync()).TenantLog().FindLatestAsync(TenantActions.ScheduleChanged, "t1", Ct);
        Assert.NotNull(logged);
        Assert.Equal("person@example.test", logged.ActorEmail);
    }

    [Fact]
    public async Task A_change_is_not_saved_when_its_tenant_row_cannot_be()
    {
        var store = await StoreWithATriggerAsync();
        var row = (await store.FindAsync("t1", Ct))!;
        await (await DatabaseAsync()).ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(row with { DailyTokenCap = 5000 }, ChangeAudit, Ct));

        Assert.Equal(1000, (await store.FindAsync("t1", Ct))!.DailyTokenCap);
    }

    [Fact]
    public async Task A_logged_capped_skip_is_not_recorded_when_its_tenant_row_cannot_be()
    {
        var store = await StoreWithATriggerAsync();
        await (await DatabaseAsync()).ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(() => store.RecordCappedSkipAsync(
            "t1", 42, ChangeAudit with { Action = TenantActions.ScheduleSkipped, ActorId = "schedule:t1", ActorEmail = null }, Ct));

        Assert.Null((await store.FindAsync("t1", Ct))!.LastSeq);
    }

    /// <summary>The audited delete: it lands with its tenant row, and not at all without one.</summary>
    [Fact]
    public async Task A_deletion_lands_with_its_tenant_row_or_not_at_all()
    {
        var store = await StoreWithATriggerAsync();
        await store.SaveAsync(Row("t2"), Ct);
        var database = await DatabaseAsync();

        await store.DeleteAsync("t1", ChangeAudit with { Action = TenantActions.ScheduleDeleted }, Ct);
        Assert.Null(await store.FindAsync("t1", Ct));
        Assert.NotNull(await database.TenantLog().FindLatestAsync(TenantActions.ScheduleDeleted, "t1", Ct));

        await database.ExecuteAsync("DROP TABLE tenant_events");
        await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteAsync("t2", ChangeAudit with { Subject = "t2" }, Ct));
        Assert.NotNull(await store.FindAsync("t2", Ct));
    }

    [Fact]
    public async Task A_clock_triggers_capped_skip_stores_the_time_it_sleeps_until_in_utc()
    {
        var store = await StoreWithATriggerAsync();

        // A cron time in Tokyo comes back with Tokyo's offset; stored, it must sort as the instant.
        var midnightInTokyo = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.FromHours(9));
        var day = TriggerCost.StartOfDay("Asia/Tokyo", midnightInTokyo.AddHours(-1));

        Assert.Equal(1, await store.CountCappedSkipAsync("t1", day, rearm: true, midnightInTokyo, Ct));

        var row = (await store.FindAsync("t1", Ct))!;
        Assert.Equal("capped", row.LastOutcome);
        Assert.Equal(midnightInTokyo, row.NextDueAt);
        Assert.Empty(await store.DueAsync(new DateTimeOffset(2026, 9, 28, 14, 59, 59, TimeSpan.Zero), Ct));
        Assert.Single(await store.DueAsync(new DateTimeOffset(2026, 9, 28, 15, 0, 0, TimeSpan.Zero), Ct));
    }

    [Fact]
    public async Task A_trigger_from_before_the_configurer_step_has_no_email_and_a_save_by_no_person_keeps_one()
    {
        await OldTriggerAsync();
        var store = await CreateAsync();
        var old = (await store.FindAsync("t1", Ct))!;
        Assert.Null(old.ConfiguredByEmail);

        await store.SaveAsync(old with { ConfiguredByEmail = "person@example.test" }, Ct);
        await store.SaveAsync(old with { Name = "Renamed" }, Ct);
        Assert.Equal("person@example.test", (await store.FindAsync("t1", Ct))!.ConfiguredByEmail);
    }

    [Fact]
    public async Task A_trigger_from_before_the_wake_choice_step_keeps_always_and_no_cap()
    {
        await OldTriggerAsync();
        var store = await CreateAsync();
        var old = (await store.FindAsync("t1", Ct))!;

        Assert.Equal(WakeManagerPolicy.Always, old.WakeManager);
        Assert.Null(old.DailyTokenCap);

        await store.SaveAsync(old with { WakeManager = WakeManagerPolicy.Never, DailyTokenCap = 50_000 }, Ct);
        var saved = (await store.FindAsync("t1", Ct))!;

        Assert.Equal(WakeManagerPolicy.Never, saved.WakeManager);
        Assert.Equal(50_000, saved.DailyTokenCap);
    }

    [Fact]
    public async Task A_trigger_from_before_the_capped_skip_step_has_skipped_nothing_today()
    {
        await OldTriggerAsync(kind: "event", interval: "NULL", capColumn: ", daily_token_cap", capValue: ", 1000");
        var store = await CreateAsync();
        var old = (await store.FindAsync("t1", Ct))!;
        Assert.Null(old.CappedSkipsDay);
        Assert.Equal(0, old.CappedSkips);

        var day = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, await store.CountCappedSkipAsync("t1", day, rearm: false, nextDueAt: null, Ct));
        Assert.Equal(2, await store.CountCappedSkipAsync("t1", day, rearm: false, nextDueAt: null, Ct));

        // A stale whole-row save does not put the count back.
        await store.SaveAsync(old with { Name = "Renamed" }, Ct);
        var saved = (await store.FindAsync("t1", Ct))!;
        Assert.Equal(2, saved.CappedSkips);
        Assert.Equal(day, saved.CappedSkipsDay);

        // The next day counts from one.
        Assert.Equal(1, await store.CountCappedSkipAsync("t1", day.AddDays(1), rearm: false, nextDueAt: null, Ct));
    }

    /// <summary>From <c>FreshVolumeTests.A_trigger_and_an_item_from_before_the_outcome_steps_...</c>, the trigger half.</summary>
    [Fact]
    public async Task A_trigger_from_before_the_outcome_steps_serves_no_outcome_until_one_is_named()
    {
        await OldTriggerAsync();
        var store = await CreateAsync();
        Assert.Null((await store.FindAsync("t1", Ct))!.OutcomeId);

        await store.SaveAsync((await store.FindAsync("t1", Ct))! with { OutcomeId = "outcome-1" }, Ct);
        Assert.Equal("outcome-1", (await store.FindAsync("t1", Ct))!.OutcomeId);
    }

    [Fact]
    public async Task A_trigger_round_trips_every_column_a_save_writes()
    {
        var store = await CreateAsync();
        await TeamAsync("Alpha");
        var at = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);
        var row = new TriggerRow(
            "full", "Alpha", "Dev", LongUnicode("name"), LongUnicode("instruction"), "folderChange", "0 9 * * *",
            "Asia/Tokyo", 90, at.AddDays(1), IdleOnly: true, Enabled: true, NextDueAt: at.AddHours(1),
            LastFiredAt: at, LastOutcome: "fired", LastSeq: 41, MissedCount: 3, CreatedAt: at, CreatedBy: "user-1",
            EventType: "file.changed", Filter: """{"path":"O'Brien 🚀"}""", WatchRoot: "documents", WatchPath: "in/café",
            WatchGlob: "*.pdf", PollSeconds: 30, QuietSeconds: 10, MinIntervalSeconds: 60,
            WakeManager: WakeManagerPolicy.OnHandbackOrFailure, DailyTokenCap: 9_000_000_000)
        {
            OutcomeId = "outcome-7",
            ConfiguredByEmail = "o'brien@example.test",
        };

        await store.SaveAsync(row, Ct);

        // The poll columns and the capped count are not the save's to write.
        Assert.Equal(row, (await store.FindAsync("full", Ct))!);
    }

    [Fact]
    public async Task Lists_for_a_team_and_a_container_fold_case_and_follow_creation_order()
    {
        var store = await CreateAsync();
        await TeamAsync("Alpha");
        await TeamAsync("Beta");
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        await store.SaveAsync(Row("b", "Alpha", "Dev", at), Ct);
        await store.SaveAsync(Row("a", "Alpha", "Dev", at), Ct);
        await store.SaveAsync(Row("c", "Alpha", "Ops", at.AddMinutes(-1)), Ct);
        await store.SaveAsync(Row("d", "Beta", "Dev", at), Ct);

        Assert.Equal(["c", "a", "b"], (await store.ListForTeamAsync("ALPHA", Ct)).Select(t => t.Id));
        Assert.Equal(["a", "b"], (await store.ListForContainerAsync("alpha", "DEV", Ct)).Select(t => t.Id));
        Assert.Equal(4, await store.CountKindAsync("EVERY", Ct));
        Assert.Equal(0, await store.CountKindAsync("event", Ct));
    }

    [Fact]
    public async Task Deletes_by_team_and_container_fold_case_and_count_what_they_removed()
    {
        var store = await CreateAsync();
        await TeamAsync("Alpha");
        await TeamAsync("Beta");
        await store.SaveAsync(Row("a", "Alpha", "Dev"), Ct);
        await store.SaveAsync(Row("b", "Alpha", "Dev"), Ct);
        await store.SaveAsync(Row("c", "Alpha", "Ops"), Ct);
        await store.SaveAsync(Row("d", "Beta", "Dev"), Ct);

        Assert.Equal(2, await store.DeleteForContainerAsync("ALPHA", "dev", Ct));
        Assert.Equal(1, await store.DeleteForTeamAsync("alpha", Ct));
        await store.DeleteAsync("d", Ct);

        Assert.Empty(await store.ListForTeamAsync("Alpha", Ct));
        Assert.Empty(await store.ListForTeamAsync("Beta", Ct));
    }

    [Fact]
    public async Task Trigger_ids_differing_only_in_case_are_two_triggers()
    {
        var store = await CreateAsync();
        await TeamAsync("Alpha");
        await store.SaveAsync(Row("T1", "Alpha"), Ct);
        await store.SaveAsync(Row("t1", "Alpha") with { Name = "lower" }, Ct);

        Assert.Equal("Poll", (await store.FindAsync("T1", Ct))!.Name);
        Assert.Equal("lower", (await store.FindAsync("t1", Ct))!.Name);
        Assert.Equal(2, (await store.ListForTeamAsync("Alpha", Ct)).Count);
    }

    /// <summary>
    /// RULING 5: A TRAILING SPACE DECIDES A COMPARISON, as SQLite's <c>=</c> does. An id arrives from a
    /// route and a team from a request, so a lookup by <c>"t1 "</c> must not answer <c>t1</c>.
    /// </summary>
    [Fact]
    public async Task A_key_with_a_trailing_space_is_another_key()
    {
        var store = await StoreWithATriggerAsync();

        Assert.Null(await store.FindAsync("t1 ", Ct));
        Assert.Empty(await store.ListForTeamAsync("t ", Ct));
        Assert.Empty(await store.ListForContainerAsync("t", "Dev ", Ct));
        Assert.Equal(0, await store.CountKindAsync("every ", Ct));
        Assert.Equal(0, await store.DeleteForContainerAsync("t", "Dev ", Ct));
        Assert.Equal(0, await store.DeleteForTeamAsync("t ", Ct));

        await store.RecordSkipAsync("t1 ", "skipped", 9, Ct);
        await store.DeleteAsync("t1 ", Ct);

        var kept = (await store.FindAsync("t1", Ct))!;
        Assert.Null(kept.LastSeq);
    }

    [Fact]
    public async Task Due_reads_enabled_triggers_due_by_now_in_due_order_and_the_earliest_is_the_soonest()
    {
        var store = await CreateAsync();
        await TeamAsync("Alpha");
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(await store.EarliestDueAsync(Ct));

        await store.SaveAsync(Row("late", "Alpha") with { NextDueAt = now.AddMinutes(5) }, Ct);
        await store.SaveAsync(Row("b", "Alpha") with { NextDueAt = now.AddMinutes(-1) }, Ct);
        await store.SaveAsync(Row("a", "Alpha") with { NextDueAt = now.AddMinutes(-1) }, Ct);
        await store.SaveAsync(Row("first", "Alpha") with { NextDueAt = now.AddHours(-1) }, Ct);
        await store.SaveAsync(Row("off", "Alpha") with { NextDueAt = now.AddHours(-2), Enabled = false }, Ct);
        await store.SaveAsync(Row("unarmed", "Alpha"), Ct);

        Assert.Equal(["first", "a", "b"], (await store.DueAsync(now, Ct)).Select(t => t.Id));
        Assert.Equal(now.AddHours(-1), await store.EarliestDueAsync(Ct));
    }

    [Fact]
    public async Task An_outcome_a_fire_and_a_skip_each_touch_only_their_own_columns()
    {
        var store = await StoreWithATriggerAsync();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));

        await store.RecordOutcomeAsync("t1", at, at.AddMinutes(1), "fired", 10, 2, Ct);
        var outcome = (await store.FindAsync("t1", Ct))!;
        Assert.Equal((at, at.AddMinutes(1), "fired", 10L, 2), (outcome.LastFiredAt!.Value, outcome.NextDueAt!.Value, outcome.LastOutcome, outcome.LastSeq!.Value, outcome.MissedCount));
        Assert.Equal(("Poll", 1000L), (outcome.Name, outcome.DailyTokenCap!.Value));

        // A fire keeps the due time and the missed count: a watch's next due is its next poll.
        await store.RecordFireAsync("t1", at.AddHours(1), "woke", 11, Ct);
        var fired = (await store.FindAsync("t1", Ct))!;
        Assert.Equal((at.AddHours(1), "woke", 11L, at.AddMinutes(1), 2), (fired.LastFiredAt!.Value, fired.LastOutcome, fired.LastSeq!.Value, fired.NextDueAt!.Value, fired.MissedCount));

        // A skip keeps the fire's time too.
        await store.RecordSkipAsync("t1", "capped", 12, Ct);
        var skipped = (await store.FindAsync("t1", Ct))!;
        Assert.Equal(("capped", 12L, at.AddHours(1)), (skipped.LastOutcome, skipped.LastSeq!.Value, skipped.LastFiredAt!.Value));

        // An outcome with nothing clears what it names.
        await store.RecordOutcomeAsync("t1", null, null, null, null, 0, Ct);
        var cleared = (await store.FindAsync("t1", Ct))!;
        Assert.Equal((null, null, null, null, 0), (cleared.LastFiredAt, cleared.NextDueAt, cleared.LastOutcome, cleared.LastSeq, cleared.MissedCount));
    }

    [Fact]
    public async Task A_poll_that_could_not_list_keeps_the_baseline_and_the_watch_state_reads_back()
    {
        var store = await StoreWithATriggerAsync();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(new FolderWatchState(null, null, null), await store.WatchStateAsync("t1", Ct));
        Assert.Equal(new FolderWatchState(null, null, null), await store.WatchStateAsync("missing", Ct));

        await store.RecordPollAsync("t1", new FolderPollRecord(at, 12, 3, null, at.AddSeconds(30), "fp1", "[\"a\"]", "fp2", at, at), Ct);
        var polled = (await store.FindAsync("t1", Ct))!;
        Assert.Equal((at, 12, 3, (string?)null, at.AddSeconds(30), "fp1", at), (polled.LastPollAt!.Value, polled.LastPollMs!.Value, polled.LastPollEntries!.Value, polled.LastPollError, polled.NextDueAt!.Value, polled.LastFingerprint, polled.LastChangeAt!.Value));
        Assert.Equal(new FolderWatchState("[\"a\"]", "fp2", at), await store.WatchStateAsync("t1", Ct));

        await store.RecordPollAsync("t1", new FolderPollRecord(at.AddMinutes(1), 5, null, "The folder is gone.", at.AddMinutes(2), null, null, null, null, null), Ct);
        var failed = (await store.FindAsync("t1", Ct))!;
        Assert.Equal((5, 3, "The folder is gone.", "fp1", at), (failed.LastPollMs!.Value, failed.LastPollEntries!.Value, failed.LastPollError, failed.LastFingerprint, failed.LastChangeAt!.Value));
        Assert.Equal(new FolderWatchState("[\"a\"]", null, null), await store.WatchStateAsync("t1", Ct));
    }

    [Fact]
    public async Task A_moved_watch_starts_again_from_a_fresh_baseline()
    {
        var store = await StoreWithATriggerAsync();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var watch = (await store.FindAsync("t1", Ct))! with { Kind = "folderChange", WatchRoot = "documents", WatchPath = "in", WatchGlob = null };
        await store.SaveAsync(watch, Ct);
        await store.RecordPollAsync("t1", new FolderPollRecord(at, 1, 1, null, null, "fp1", "[\"a\"]", "fp2", at, null), Ct);

        await store.SaveAsync(watch with { Name = "Renamed" }, Ct);
        Assert.Equal("fp1", (await store.FindAsync("t1", Ct))!.LastFingerprint);
        Assert.Equal(new FolderWatchState("[\"a\"]", "fp2", at), await store.WatchStateAsync("t1", Ct));

        await store.SaveAsync(watch with { WatchPath = "In" }, Ct);
        Assert.Null((await store.FindAsync("t1", Ct))!.LastFingerprint);
        Assert.Equal(new FolderWatchState(null, null, at), await store.WatchStateAsync("t1", Ct));

        await store.RecordPollAsync("t1", new FolderPollRecord(at, 1, 1, null, null, "fp3", "[\"b\"]", null, null, null), Ct);
        await store.SaveAsync(watch with { WatchPath = "In", WatchGlob = "*.pdf" }, Ct);
        Assert.Null((await store.FindAsync("t1", Ct))!.LastFingerprint);
    }

    /// <summary>RULING 5: a watch's folder is what a person typed, and SQLite's <c>IS</c> tells
    /// <c>in</c> from <c>in </c>, so moving to it drops the baseline.</summary>
    [Fact]
    public async Task A_watch_folder_differing_only_by_a_trailing_space_is_a_moved_watch()
    {
        var store = await StoreWithATriggerAsync();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var watch = (await store.FindAsync("t1", Ct))! with { Kind = "folderChange", WatchRoot = "documents", WatchPath = "in" };
        await store.SaveAsync(watch, Ct);
        await store.RecordPollAsync("t1", new FolderPollRecord(at, 1, 1, null, null, "fp1", "[\"a\"]", null, null, null), Ct);

        await store.SaveAsync(watch with { WatchPath = "in " }, Ct);

        var moved = (await store.FindAsync("t1", Ct))!;
        Assert.Equal("in ", moved.WatchPath);
        Assert.Null(moved.LastFingerprint);
    }

    [Fact]
    public async Task A_trigger_on_a_team_that_does_not_exist_is_refused_and_a_deleted_team_takes_its_triggers()
    {
        var store = await CreateAsync();
        var database = await DatabaseAsync();

        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => store.SaveAsync(Row("orphan", "Nobody"), Ct));

        await TeamAsync("Alpha");
        await store.SaveAsync(Row("a", "Alpha"), Ct);
        await database.Teams().DeleteTeamAsync("alpha", Ct);

        Assert.Null(await store.FindAsync("a", Ct));
    }

    /// <summary>The capped skip's two writes inside a caller's transaction, as the log's append runs
    /// them: they land with it, and a rollback takes them back.</summary>
    [Fact]
    public async Task A_capped_skip_inside_the_callers_transaction_lands_only_with_it()
    {
        var store = await StoreWithATriggerAsync();
        var database = await DatabaseAsync();
        var day = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        var skip = ChangeAudit with { Action = TenantActions.ScheduleSkipped, ActorId = "schedule:t1", ActorEmail = null };

        await using (var connection = await database.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            Assert.Equal(1, await store.CountCappedSkipAsync(connection, transaction, "t1", day, rearm: false, null, Ct));
            await store.RecordCappedSkipAsync(connection, transaction, "t1", 7, skip, Ct);
            await transaction.RollbackAsync(Ct);
        }

        var untouched = (await store.FindAsync("t1", Ct))!;
        Assert.Equal((0, (long?)null), (untouched.CappedSkips, untouched.LastSeq));
        Assert.Null(await database.TenantLog().FindLatestAsync(TenantActions.ScheduleSkipped, "t1", Ct));

        await using (var connection = await database.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            Assert.Equal(1, await store.CountCappedSkipAsync(connection, transaction, "t1", day, rearm: false, null, Ct));
            await store.RecordCappedSkipAsync(connection, transaction, "t1", 7, skip, Ct);
            await transaction.CommitAsync(Ct);
        }

        var counted = (await store.FindAsync("t1", Ct))!;
        Assert.Equal((1, (long?)7, "capped"), (counted.CappedSkips, counted.LastSeq, counted.LastOutcome));
        Assert.NotNull(await database.TenantLog().FindLatestAsync(TenantActions.ScheduleSkipped, "t1", Ct));
    }

    /// <summary>REVIEW FOCUS 2: eight saves of one new trigger at once. Every save succeeds and one row stands.</summary>
    [Fact]
    public async Task Concurrent_saves_of_one_new_trigger_make_one_row()
    {
        var store = await CreateAsync();
        await TeamAsync("Alpha");

        for (var round = 0; round < 12; round++)
        {
            var id = $"raced{round}";
            await RaceAsync(8, async i =>
            {
                await store.SaveAsync(Row(id, "Alpha") with { Name = $"name {i}" }, Audit(TenantActions.ScheduleChanged, id), Ct);
                return i;
            });

            Assert.Single(await store.ListForTeamAsync("Alpha", Ct), t => t.Id == id);
        }
    }

    /// <summary>
    /// REVIEW FOCUS 2: eight skips of one trigger on one day at once count 1 to 8, each once - so
    /// exactly one is the first, the one that is logged.
    /// </summary>
    [Fact]
    public async Task Concurrent_capped_skips_count_every_skip_once()
    {
        var store = await StoreWithATriggerAsync();

        for (var round = 0; round < 6; round++)
        {
            var day = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddDays(round);
            var counts = await RaceAsync(8, _ => store.CountCappedSkipAsync("t1", day, rearm: false, nextDueAt: null, Ct));

            Assert.Equal(Enumerable.Range(1, 8), counts.Order());
            Assert.Equal(8, (await store.FindAsync("t1", Ct))!.CappedSkips);
        }
    }

    /// <summary>
    /// A TEAM DELETED WHILE ITS TRIGGERS ARE SAVED: each save lands and goes with the team, or is
    /// refused for the foreign key; nothing else, so neither a deadlock nor an orphan.
    /// </summary>
    [Fact]
    public async Task A_team_deleted_while_its_triggers_are_saved_leaves_none_and_no_deadlock()
    {
        var store = await CreateAsync();
        var teams = (await DatabaseAsync()).Teams();

        for (var round = 0; round < 12; round++)
        {
            var team = $"Doomed{round}";
            await TeamAsync(team);
            await store.SaveAsync(Row($"{team}-0", team), Ct);

            var outcomes = await RaceAsync(8, async i =>
            {
                try
                {
                    if (i == 0) await teams.DeleteTeamAsync(team, Ct);
                    else await store.SaveAsync(Row(i % 2 == 0 ? $"{team}-0" : $"{team}-{i}", team), Audit(TenantActions.ScheduleChanged, team), Ct);

                    return null;
                }
                catch (System.Data.Common.DbException refused)
                {
                    return refused.Message;
                }
            });

            Assert.All(outcomes, refusal => Assert.True(refusal is null || refusal.Contains("FOREIGN KEY", StringComparison.Ordinal), refusal));
            Assert.Empty(await store.ListForTeamAsync(team, Ct));
        }
    }
}

public sealed class SqliteTriggerStoreTests : TriggerStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerTriggerStoreTests : TriggerStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
