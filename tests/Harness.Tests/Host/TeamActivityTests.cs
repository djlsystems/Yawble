using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// THE TEAM ACTIVITY READ, on the real Host with a fake agent. <c>GET /api/teams/{team}/activity</c>
/// derives each member's running, waiting, blocked, failed and idle stretches from
/// <c>usage_ledger</c> and the log's run in progress, and nothing else: a ledger row without a time
/// contributes only the times it has, and no data is no span. Most tests write ledger rows by hand
/// at fixed instants in 2001 on a team of their own and ask for a period around them; the window
/// tests, the run in progress and the reset drive real runs.
/// </summary>
public sealed class TeamActivityTests(TeamActivityTests.Bed bed) : IClassFixture<TeamActivityTests.Bed>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An instant in a fixed hour of 2001: the base every hand-written row counts from.</summary>
    private static readonly DateTimeOffset T0 = new(2001, 2, 3, 4, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset T(int seconds) => T0.AddSeconds(seconds);

    [Fact]
    public async Task A_run_is_running_from_its_start_to_its_end_and_waiting_from_queued_to_start()
    {
        var team = await bed.TeamAsync("Activity running", "Dev");
        await bed.RunAsync(team, "Dev", "completed", 101, queued: T(10), started: T(20), ended: T(50));

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        Assert.Equal("requested", answer.GetProperty("window").GetString());
        Assert.Equal(["waiting 10-20 w101", "running 20-50 w101", "idle 50-100"], Spans(answer, "Dev"));
    }

    [Fact]
    public async Task A_blocked_run_stays_blocked_until_the_members_next_queue_or_start()
    {
        var team = await bed.TeamAsync("Activity blocked", "Dev", "Ops");
        await bed.RunAsync(team, "Dev", "blocked", 201, queued: T(5), started: T(10), ended: T(30));
        await bed.RunAsync(team, "Dev", "completed", 202, queued: T(60), started: T(70), ended: T(80));

        // With no queue time on the next run, its start ends the block.
        await bed.RunAsync(team, "Ops", "blocked", 203, queued: null, started: T(10), ended: T(30));
        await bed.RunAsync(team, "Ops", "completed", 204, queued: null, started: T(70), ended: T(80));

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        Assert.Equal(
            ["waiting 5-10 w201", "running 10-30 w201", "blocked 30-60 w201", "waiting 60-70 w202", "running 70-80 w202", "idle 80-100"],
            Spans(answer, "Dev"));
        Assert.Equal(["running 10-30 w203", "blocked 30-70 w203", "running 70-80 w204", "idle 80-100"], Spans(answer, "Ops"));
    }

    [Fact]
    public async Task A_failed_run_stays_failed_until_the_members_next_queue_or_start()
    {
        var team = await bed.TeamAsync("Activity failed", "Dev");
        await bed.RunAsync(team, "Dev", "failed", 301, queued: T(5), started: T(10), ended: T(30));
        await bed.RunAsync(team, "Dev", "completed", 302, queued: T(60), started: T(70), ended: T(80));
        await bed.RunAsync(team, "Dev", "failed", 303, queued: null, started: T(85), ended: T(90));

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        Assert.Equal(
            ["waiting 5-10 w301", "running 10-30 w301", "failed 30-60 w301", "waiting 60-70 w302", "running 70-80 w302",
                "idle 80-85", "running 85-90 w303", "failed 90-100 w303"],
            Spans(answer, "Dev"));
    }

    [Fact]
    public async Task A_completed_or_handed_back_run_is_idle_until_the_next()
    {
        // A hand-back's run ends `completed`; a quiet plugin run ends `quiet`. Both leave the member idle.
        var team = await bed.TeamAsync("Activity idle", "Dev");
        await bed.RunAsync(team, "Dev", "completed", 401, queued: null, started: T(10), ended: T(30));
        await bed.RunAsync(team, "Dev", "quiet", 402, queued: T(50), started: T(55), ended: T(60));

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        Assert.Equal(
            ["running 10-30 w401", "idle 30-50", "waiting 50-55 w402", "running 55-60 w402", "idle 60-100"],
            Spans(answer, "Dev"));
    }

    [Fact]
    public async Task Running_wins_where_a_member_is_queued_behind_its_own_run()
    {
        var team = await bed.TeamAsync("Activity overlap", "Dev");
        await bed.RunAsync(team, "Dev", "completed", 501, queued: null, started: T(10), ended: T(50));
        await bed.RunAsync(team, "Dev", "completed", 502, queued: T(20), started: T(50), ended: T(70));

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        Assert.Equal(["running 10-50 w501", "running 50-70 w502", "idle 70-100"], Spans(answer, "Dev"));
    }

    [Fact]
    public async Task The_state_before_the_window_carries_into_it()
    {
        var team = await bed.TeamAsync("Activity carry", "Dev", "Ops");
        await bed.RunAsync(team, "Dev", "completed", 601, queued: null, started: T(-90), ended: T(-80));
        await bed.RunAsync(team, "Dev", "blocked", 602, queued: null, started: T(-20), ended: T(-10));

        // A run that began before the window and ends inside it is running from the window's start.
        await bed.RunAsync(team, "Ops", "failed", 603, queued: T(-30), started: T(-5), ended: T(20));

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        Assert.Equal(["blocked 0-100 w602"], Spans(answer, "Dev"));
        Assert.Equal(["running 0-20 w603", "failed 20-100 w603"], Spans(answer, "Ops"));
    }

    [Fact]
    public async Task The_run_in_progress_is_running_to_now_with_no_end()
    {
        var team = await bed.TeamAsync("Activity in progress", "Dev");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bed.Hold(team, "Dev", hold);

        try
        {
            var root = await bed.TellAsync(team, "Dev", "work");
            var started = await bed.AwaitRowAsync(
                m => m.Type == MessageTypes.Started && m.Source == $"{team}/Dev" && m.CorrelationId == root.Seq, "Dev's start");

            // A requested period that reaches past now: the run is open at now.
            var answer = await bed.ActivityAsync(team, started.OccurredAt.AddMinutes(-1), started.OccurredAt.AddHours(1));

            var span = Assert.Single(Member(answer, "Dev").GetProperty("spans").EnumerateArray());
            Assert.Equal("running", span.GetProperty("state").GetString());
            Assert.Equal(started.OccurredAt, span.GetProperty("from").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, span.GetProperty("to").ValueKind);
            Assert.Equal(root.Seq, span.GetProperty("workflow").GetInt64());
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [Fact]
    public async Task A_ledger_row_without_queued_or_started_times_contributes_only_what_it_has()
    {
        var team = await bed.TeamAsync("Activity partial", "Dev");

        // No queue and no start: nothing before its end, and its end starts the failure.
        await bed.RunAsync(team, "Dev", "failed", 801, queued: null, started: null, ended: T(30));

        // A queue and no start: the failure ends at the queue, and no waiting or running is made up.
        await bed.RunAsync(team, "Dev", "completed", 802, queued: T(50), started: null, ended: T(70));

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        Assert.Equal(["failed 30-50 w801", "idle 70-100"], Spans(answer, "Dev"));
    }

    [Fact]
    public async Task The_default_window_runs_from_the_earliest_workflow_start_to_the_latest_workflow_activity()
    {
        var team = await bed.TeamAsync("Activity window", "Dev");

        var first = await bed.TellAndSettleAsync(team, "Dev", "first");
        var second = await bed.TellAndSettleAsync(team, "Dev", "second");
        await bed.DeclareAsync(team, first);
        await bed.QuietAsync(team);

        var answer = await bed.ActivityAsync(team);

        // From the earliest workflow's root, open or closed, to the newest row of any of them.
        Assert.Equal("workflows", answer.GetProperty("window").GetString());
        Assert.Equal(first.OccurredAt, answer.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(await bed.LatestActivityAsync(first, second), answer.GetProperty("to").GetDateTimeOffset());
        Assert.True(answer.GetProperty("to").GetDateTimeOffset() < answer.GetProperty("serverNow").GetDateTimeOffset());

        // Every span lies inside it, and the last one ends at its end.
        var spans = Member(answer, "Dev").GetProperty("spans").EnumerateArray().ToList();
        Assert.All(spans, span =>
        {
            Assert.True(span.GetProperty("from").GetDateTimeOffset() >= answer.GetProperty("from").GetDateTimeOffset());
            Assert.True(span.GetProperty("to").GetDateTimeOffset() <= answer.GetProperty("to").GetDateTimeOffset());
        });
        Assert.Equal(answer.GetProperty("to").GetDateTimeOffset(), spans[^1].GetProperty("to").GetDateTimeOffset());
    }

    [Fact]
    public async Task The_window_never_ends_at_the_clock_while_nothing_happens()
    {
        var team = await bed.TeamAsync("Activity still", "Dev");

        // An open workflow whose runs have all ended: nothing happens in it while time passes.
        await bed.TellAndSettleAsync(team, "Dev", "work");
        await bed.QuietAsync(team);

        var before = await bed.SettledActivityAsync(team);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        var after = await bed.ActivityAsync(team);

        Assert.Equal("workflows", after.GetProperty("window").GetString());
        Assert.Equal(before.GetProperty("to").GetDateTimeOffset(), after.GetProperty("to").GetDateTimeOffset());
        Assert.True(after.GetProperty("to").GetDateTimeOffset() < after.GetProperty("serverNow").GetDateTimeOffset().AddSeconds(-1));

        // The idle after Dev's run is drawn to the window's end, not left open to now.
        var last = Member(after, "Dev").GetProperty("spans").EnumerateArray().Last();
        Assert.Equal("idle", last.GetProperty("state").GetString());
        Assert.Equal(after.GetProperty("to").GetDateTimeOffset(), last.GetProperty("to").GetDateTimeOffset());
        Assert.Equal(Spans(before, "Dev"), Spans(after, "Dev"));
    }

    [Fact]
    public async Task A_closed_workflow_counts_for_the_window_as_an_open_one_does()
    {
        var team = await bed.TeamAsync("Activity all closed", "Dev");

        var first = await bed.TellAndSettleAsync(team, "Dev", "first");
        var second = await bed.TellAndSettleAsync(team, "Dev", "second");
        await bed.DeclareAsync(team, first);
        await bed.DeclareAsync(team, second);
        await bed.QuietAsync(team);

        var answer = await bed.ActivityAsync(team);

        // Nothing is open: the window still runs from the first workflow's root to the second's end.
        Assert.Equal("workflows", answer.GetProperty("window").GetString());
        Assert.Equal(first.OccurredAt, answer.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(await bed.LatestActivityAsync(first, second), answer.GetProperty("to").GetDateTimeOffset());

        var workflows = Member(answer, "Dev").GetProperty("spans").EnumerateArray()
            .Where(s => s.GetProperty("state").GetString() == "running")
            .Select(s => s.GetProperty("workflow").GetInt64())
            .ToList();
        Assert.Contains(first.Seq, workflows);
        Assert.Contains(second.Seq, workflows);
    }

    [Fact]
    public async Task With_no_workflow_the_answer_is_empty_and_says_none()
    {
        var team = await bed.TeamAsync("Activity none", "Dev");

        var answer = await bed.ActivityAsync(team);

        Assert.Equal("none", answer.GetProperty("window").GetString());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("from").ValueKind);
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("to").ValueKind);
        Assert.Equal(JsonValueKind.String, answer.GetProperty("serverNow").ValueKind);
        Assert.Empty(answer.GetProperty("members").EnumerateArray());
    }

    [Fact]
    public async Task Runs_of_closed_workflows_inside_the_window_are_shown()
    {
        var team = await bed.TeamAsync("Activity closed", "Dev");

        // The earliest workflow sets the window's start; one begun and declared inside it still shows.
        var open = await bed.TellAndSettleAsync(team, "Dev", "long job");
        var closed = await bed.TellAndSettleAsync(team, "Dev", "short job");
        await bed.DeclareAsync(team, closed);

        var answer = await bed.ActivityAsync(team);

        Assert.Equal("workflows", answer.GetProperty("window").GetString());
        Assert.Equal(open.OccurredAt, answer.GetProperty("from").GetDateTimeOffset());

        var workflows = Member(answer, "Dev").GetProperty("spans").EnumerateArray()
            .Where(s => s.GetProperty("state").GetString() == "running")
            .Select(s => s.GetProperty("workflow").GetInt64())
            .ToList();
        Assert.Contains(open.Seq, workflows);
        Assert.Contains(closed.Seq, workflows);
    }

    [Fact]
    public async Task A_removed_member_with_runs_in_the_window_is_listed_not_current()
    {
        var team = await bed.TeamAsync("Activity removed", "Dev", "Temp", "Early");
        await bed.RunAsync(team, "Temp", "completed", 1201, queued: null, started: T(10), ended: T(20));
        await bed.RunAsync(team, "Early", "completed", 1202, queued: null, started: T(-20), ended: T(-10));

        await bed.RemoveAsync(team, "Temp");
        await bed.RemoveAsync(team, "Early");

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        var members = answer.GetProperty("members").EnumerateArray()
            .Select(m => $"{m.GetProperty("member").GetString()} {(m.GetProperty("current").GetBoolean() ? "current" : "removed")}"
                + (m.GetProperty("isManager").GetBoolean() ? " manager" : ""))
            .ToArray();

        // Manager first, then the board's members, then the removed member whose run is in the window;
        // the one whose only run ended before it is not listed.
        Assert.Equal([$"{TeamRegistry.DefaultManagerName} current manager", "Dev current", "Temp removed"], members);
        Assert.Equal(["running 10-20 w1201", "idle 20-100"], Spans(answer, "Temp"));
        Assert.Equal(MemberRef.AgentKind, Member(answer, "Dev").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_removed_member_whose_run_outlasts_the_window_is_listed_not_current()
    {
        var team = await bed.TeamAsync("Activity removed late", "Dev", "Late", "Long");
        await bed.RunAsync(team, "Late", "completed", 1251, queued: null, started: T(80), ended: T(150));
        await bed.RunAsync(team, "Long", "completed", 1252, queued: null, started: T(-10), ended: T(150));

        await bed.RemoveAsync(team, "Late");
        await bed.RemoveAsync(team, "Long");

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        // A run overlaps the window when it began before its end and ended after its start: one
        // that ends after the window still puts its member in it, its running clipped to the end.
        Assert.Equal(
            [TeamRegistry.DefaultManagerName, "Dev", "Late", "Long"],
            answer.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("member").GetString()).ToArray());
        Assert.False(Member(answer, "Late").GetProperty("current").GetBoolean());
        Assert.False(Member(answer, "Long").GetProperty("current").GetBoolean());
        Assert.Equal(["running 80-100 w1251"], Spans(answer, "Late"));
        Assert.Equal(["running 0-100 w1252"], Spans(answer, "Long"));
    }

    [Fact]
    public async Task A_period_over_a_year_is_refused_with_a_sentence()
    {
        var team = await bed.TeamAsync("Activity year", "Dev");
        var person = bed.Person;

        var year = await person.GetAsync(Url(team, T0, T0.AddYears(1)), Ct);
        Assert.Equal(HttpStatusCode.OK, year.StatusCode);

        var longer = await person.GetAsync(Url(team, T0, T0.AddYears(1).AddSeconds(1)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, longer.StatusCode);
        var tooLong = (await longer.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()!;
        Assert.EndsWith(".", tooLong);
        Assert.Contains("year", tooLong);

        var backwards = await person.GetAsync(Url(team, T(10), T(0)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.EndsWith(".", (await backwards.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task A_container_is_refused_another_teams_activity_with_what_a_missing_team_gets()
    {
        var own = await bed.TeamAsync("Activity own", "Dev");
        var other = await bed.TeamAsync("Activity other", "Dev");
        using var container = await bed.ContainerAsync(own, "Dev");

        var mine = await container.GetAsync($"/api/teams/{own}/activity", Ct);
        var theirs = await container.GetAsync($"/api/teams/{other}/activity", Ct);
        var missing = await container.GetAsync("/api/teams/no-such-team/activity", Ct);

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(missing.StatusCode, theirs.StatusCode);
        Assert.Equal(TeamGate.NoSuchTeam, await theirs.Content.ReadAsStringAsync(Ct));
        Assert.Equal(await missing.Content.ReadAsStringAsync(Ct), await theirs.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_spans_survive_a_reset_that_deletes_memory()
    {
        var team = await bed.TeamAsync("Activity reset", "Dev");
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var to = DateTimeOffset.UtcNow.AddHours(1);

        // Dev asks a person to decide and its run ends: needs-decision counts as blocked. The
        // platform's nudge of a workflow nobody is working runs Dev again, and it asks again.
        bed.Decide(team, "Dev", "Which database?");
        var root = await bed.TellAndSettleAsync(team, "Dev", "pick one");
        await bed.QuietAsync(team);

        var before = await bed.ActivityAsync(team, from, to);
        var blocked = Member(before, "Dev").GetProperty("spans").EnumerateArray().Last();
        Assert.Equal("blocked", blocked.GetProperty("state").GetString());
        Assert.Equal("Which database?", blocked.GetProperty("reason").GetString());
        Assert.Equal(root.Seq, blocked.GetProperty("workflow").GetInt64());

        var reset = await bed.ResetMemoryAsync(team);
        Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync(Ct));
        Assert.Null(await bed.Log.FindAsync(root.Seq, Ct));

        var after = await bed.ActivityAsync(team, from, to);

        // Every span is where it was; the reason went with the log rows that held it.
        Assert.Equal(Spans(before, "Dev"), Spans(after, "Dev"));
        Assert.False(Member(after, "Dev").GetProperty("spans").EnumerateArray().Last().TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task A_recreated_team_does_not_show_its_predecessors_runs()
    {
        var team = await bed.TeamAsync("Activity recreated", "Dev");

        // The predecessor's runs carry the log positions they ran at, below anything its successor runs.
        await bed.RunAsync(team, "Dev", "blocked", 1601, queued: null, started: T(-20), ended: T(-10), seq: await bed.HeadAsync());
        await bed.RunAsync(team, "Dev", "completed", 1602, queued: T(10), started: T(20), ended: T(30), seq: await bed.HeadAsync());
        await bed.RunAsync(team, "Old", "failed", 1603, queued: null, started: T(10), ended: T(40), seq: await bed.HeadAsync());

        await bed.DeleteTeamAsync(team);
        Assert.Equal(team, await bed.TeamAsync("Activity recreated", "Dev"));

        await bed.RunAsync(team, "Dev", "completed", 1604, queued: null, started: T(60), ended: T(70));

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        Assert.Equal(
            [TeamRegistry.DefaultManagerName, "Dev"],
            answer.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("member").GetString()).ToArray());
        Assert.Equal(["running 60-70 w1604", "idle 70-100"], Spans(answer, "Dev"));
    }

    [Fact]
    public async Task A_waiting_stretch_is_held_where_a_hold_row_covers_it_and_waiting_elsewhere()
    {
        var team = await bed.TeamAsync("Activity held", "Dev");
        await bed.CreatedAtAsync(team, T(-100));
        await bed.RunAsync(team, "Dev", "completed", 1701, queued: T(10), started: T(60), ended: T(80));
        await bed.HoldAsync(team, "Dev", "slot", "waiting for a slot", T(20), T(30));
        await bed.HoldAsync(team, "Dev", "memory", "waiting for memory: 11.2 of 12.9 GB in use", T(30), T(70));

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        // Running still wins: the memory hold's last ten seconds are the run's.
        Assert.Equal(
            ["waiting 10-20 w1701", "held 20-30 w1701", "held 30-60 w1701", "running 60-80 w1701", "idle 80-100"],
            Spans(answer, "Dev"));

        var held = Member(answer, "Dev").GetProperty("spans").EnumerateArray()
            .Where(span => span.GetProperty("state").GetString() == "held")
            .Select(span => $"{span.GetProperty("reasonKind").GetString()}|{span.GetProperty("reason").GetString()}")
            .ToArray();
        Assert.Equal(["slot|waiting for a slot", "memory|waiting for memory: 11.2 of 12.9 GB in use"], held);
    }

    [Fact]
    public async Task A_hold_still_open_is_held_to_the_end_of_the_period()
    {
        var team = await bed.TeamAsync("Activity held open", "Dev");
        await bed.CreatedAtAsync(team, T(-100));
        await bed.RunAsync(team, "Dev", "completed", 1711, queued: null, started: T(10), ended: T(20));
        await bed.HoldAsync(team, "Dev", "worker", "waiting for a worker", T(50), null);

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        Assert.Equal(["running 10-20 w1711", "idle 20-50", "held 50-100"], Spans(answer, "Dev"));
    }

    [Fact]
    public async Task With_no_hold_rows_of_its_own_a_members_spans_are_unchanged()
    {
        var team = await bed.TeamAsync("Activity unheld", "Dev", "Ops");
        var other = await bed.TeamAsync("Activity unheld other", "Dev");
        await bed.CreatedAtAsync(team, T(5));
        await bed.CreatedAtAsync(other, T(-100));
        await bed.RunAsync(team, "Dev", "completed", 1721, queued: T(10), started: T(20), ended: T(50));

        // Another member's hold, another team's hold, and a hold from before this team existed - the
        // team it replaced under the same name - change nothing of Dev's.
        await bed.HoldAsync(team, "Ops", "slot", "waiting for a slot", T(10), T(20));
        await bed.HoldAsync(other, "Dev", "slot", "waiting for a slot", T(10), T(20));
        await bed.HoldAsync(team, "Dev", "slot", "waiting for a slot", T(2), T(18));

        var answer = await bed.ActivityAsync(team, T(0), T(100));

        Assert.Equal(["waiting 10-20 w1721", "running 20-50 w1721", "idle 50-100"], Spans(answer, "Dev"));
        Assert.Equal(["held 10-20"], Spans(answer, "Ops"));
    }

    [Fact]
    public async Task Hold_rows_survive_a_reset_that_deletes_memory()
    {
        var team = await bed.TeamAsync("Activity held reset", "Dev");
        await bed.CreatedAtAsync(team, T(-100));
        await bed.RunAsync(team, "Dev", "completed", 1731, queued: T(10), started: T(40), ended: T(50));
        await bed.HoldAsync(team, "Dev", "slot", "waiting for a slot", T(20), T(40));
        await bed.TellAndSettleAsync(team, "Dev", "look");
        await bed.QuietAsync(team);

        var before = await bed.ActivityAsync(team, T(0), T(100));
        Assert.Contains("held 20-40 w1731", Spans(before, "Dev"));

        var reset = await bed.ResetMemoryAsync(team);
        Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync(Ct));

        Assert.Equal(Spans(before, "Dev"), Spans(await bed.ActivityAsync(team, T(0), T(100)), "Dev"));
    }

    private static string Url(string team, DateTimeOffset from, DateTimeOffset to) =>
        $"/api/teams/{team}/activity?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private static JsonElement Member(JsonElement answer, string member) =>
        answer.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("member").GetString() == member);

    /// <summary>A member's spans as "state from-to wN", seconds from <see cref="T0"/>; "open" for no end.</summary>
    private static string[] Spans(JsonElement answer, string member) =>
        [.. Member(answer, member).GetProperty("spans").EnumerateArray().Select(span =>
        {
            var from = span.GetProperty("from").GetDateTimeOffset();
            var to = span.GetProperty("to");
            var end = to.ValueKind == JsonValueKind.Null ? "open" : Seconds(to.GetDateTimeOffset());
            var workflow = span.TryGetProperty("workflow", out var w) ? $" w{w.GetInt64()}" : "";
            return $"{span.GetProperty("state").GetString()} {Seconds(from)}-{end}{workflow}";
        })];

    private static string Seconds(DateTimeOffset at) =>
        at.Year == T0.Year ? ((long)(at - T0).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : at.ToString("O");

    /// <summary>
    /// The real Host over its own data root with a fake agent: every run ends at once unless a test
    /// holds it, and a member a test names may ask for a decision mid-run. Each test makes its own team.
    /// </summary>
    public sealed class Bed : IAsyncLifetime
    {
        private const string Email = "person@example.test";
        private const string Password = "correct horse battery";

        private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-team-activity-{Guid.NewGuid():N}");
        private readonly FakeAgent _agents = new();
        private readonly Dictionary<ContainerId, TaskCompletionSource> _holds = [];
        private readonly Dictionary<ContainerId, string> _decisions = [];
        private WebApplicationFactory<Program> _factory = null!;
        private static long _run = 9_000_000_000 + Random.Shared.Next(1_000_000);

        public HttpClient Person { get; private set; } = null!;

        public IMessageLog Log => _factory.Services.GetRequiredService<IMessageLog>();

        private string Database => Path.Combine(_dataRoot, "messages.db");

        public async ValueTask InitializeAsync()
        {
            Directory.CreateDirectory(_dataRoot);

            _agents.Behaviour = async invocation =>
            {
                TaskCompletionSource? hold;
                string? question;
                lock (_holds)
                {
                    _holds.TryGetValue(invocation.Container, out hold);
                    _decisions.TryGetValue(invocation.Container, out question);
                }

                if (question is not null)
                {
                    await _factory.Services.GetRequiredService<MemberReports>()
                        .NeedsDecisionAsync(invocation.Container, question, Ct);
                }

                if (hold is not null) await hold.Task;

                return new AgentResult(0, $"done by {invocation.Container.Name}");
            };

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
                .UseSetting("DataRoot", _dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning")
                .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

            await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
            Person = _factory.CreateClient();
            (await Person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password })).EnsureSuccessStatusCode();
        }

        public async ValueTask DisposeAsync()
        {
            lock (_holds)
            {
                foreach (var hold in _holds.Values) hold.TrySetResult();
            }

            Person.Dispose();
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dataRoot, recursive: true); }
            catch (IOException) { }
        }

        /// <summary>A new team with these members beside its Manager; answers its id.</summary>
        public async Task<string> TeamAsync(string label, params string[] members)
        {
            var team = (await _factory.Services.GetRequiredService<TeamRegistry>()
                .CreateAsync(label, "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

            foreach (var member in members)
            {
                var added = await Person.PostAsJsonAsync(
                    $"/api/teams/{team}/containers", new { name = member, agent = "claude-headless" }, Ct);
                Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            }

            return team;
        }

        public async Task RemoveAsync(string team, string member)
        {
            var removed = await Person.DeleteAsync($"/api/teams/{team}/containers/{member}", Ct);
            Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync(Ct));
        }

        public async Task<HttpClient> ContainerAsync(string team, string member)
        {
            var key = await _factory.Services.GetRequiredService<IPrincipalStore>().MintAsync(
                new ContainerId(team, member).ToString(), PrincipalKind.Container, team, Permits.All);

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
            return client;
        }

        /// <summary>Holds this member's runs until <paramref name="hold"/> is set.</summary>
        public void Hold(string team, string member, TaskCompletionSource hold)
        {
            lock (_holds) _holds[new ContainerId(team, member)] = hold;
        }

        /// <summary>This member asks a person <paramref name="question"/> in every run.</summary>
        public void Decide(string team, string member, string question)
        {
            lock (_holds) _decisions[new ContainerId(team, member)] = question;
        }

        /// <summary>One finished run, written to the ledger as its writer would.</summary>
        public async Task RunAsync(
            string team, string member, string outcome, long correlation,
            DateTimeOffset? queued, DateTimeOffset? started, DateTimeOffset ended, long? seq = null)
        {
            await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
            await connection.OpenAsync(Ct);
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO usage_ledger (run_seq, correlation, team_id, team_name, member, member_kind, run_outcome,
                    queued_at, started_at, ended_at, measured)
                VALUES ($seq, $correlation, $team, $team, $member, 'agent', $outcome, $queued, $started, $ended, 0)
                """;
            insert.Parameters.AddWithValue("$seq", seq ?? Interlocked.Increment(ref _run));
            insert.Parameters.AddWithValue("$correlation", correlation);
            insert.Parameters.AddWithValue("$team", team);
            insert.Parameters.AddWithValue("$member", member);
            insert.Parameters.AddWithValue("$outcome", outcome);
            insert.Parameters.AddWithValue("$queued", (object?)queued?.ToString("O") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$started", (object?)started?.ToString("O") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$ended", ended.ToString("O"));
            await insert.ExecuteNonQueryAsync(Ct);
        }

        /// <summary>One admission hold, written to <c>admission_holds</c> as its writer would.</summary>
        public async Task HoldAsync(
            string team, string member, string kind, string reason, DateTimeOffset heldAt, DateTimeOffset? releasedAt)
        {
            await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
            await connection.OpenAsync(Ct);
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO admission_holds (team_id, team_name, member, held_at, released_at, reason_kind, reason)
                VALUES ($team, $team, $member, $held, $released, $kind, $reason)
                """;
            insert.Parameters.AddWithValue("$team", team);
            insert.Parameters.AddWithValue("$member", member);
            insert.Parameters.AddWithValue("$held", heldAt.ToString("O"));
            insert.Parameters.AddWithValue("$released", (object?)releasedAt?.ToString("O") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$reason", reason);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        /// <summary>Moves the team's creation back, so rows written at fixed instants fall after it.</summary>
        public async Task CreatedAtAsync(string team, DateTimeOffset created)
        {
            await using var connection = new SqliteConnection($"Data Source={Database};Pooling=false");
            await connection.OpenAsync(Ct);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE teams SET created_utc = $created WHERE id = $team";
            update.Parameters.AddWithValue("$team", team);
            update.Parameters.AddWithValue("$created", created.ToString("O"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync(Ct));
        }

        /// <summary>A log position no ledger row holds yet: a fresh row's own.</summary>
        public async Task<long> HeadAsync() =>
            (await Log.AppendAsync(new NewMessage("test.mark", "{}", "console"), Ct)).Seq;

        public async Task DeleteTeamAsync(string team)
        {
            var deleted = await Person.DeleteAsync($"/api/teams/{team}", Ct);
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        }

        public Task<Message> TellAsync(string team, string member, string instruction) =>
            Log.AppendAsync(new NewMessage(
                MessageTypes.InstructionFor(new ContainerId(team, member)),
                JsonSerializer.Serialize(new { instruction }), "console"), Ct);

        /// <summary>
        /// Roots a workflow with an instruction to <paramref name="member"/> and waits for its run and
        /// the Manager run its completion wakes, so nothing more is written into the workflow.
        /// </summary>
        public async Task<Message> TellAndSettleAsync(string team, string member, string instruction)
        {
            var root = await TellAsync(team, member, instruction);
            await AwaitRowAsync(m => IsTerminal(m) && m.Source == $"{team}/{member}" && m.CorrelationId == root.Seq, $"{member}'s run");
            await AwaitRowAsync(
                m => IsTerminal(m) && m.Source == $"{team}/{TeamRegistry.DefaultManagerName}" && m.CorrelationId == root.Seq,
                "the Manager's run");
            return root;
        }

        /// <summary>The Manager declares the workflow <paramref name="root"/> began complete.</summary>
        public async Task DeclareAsync(string team, Message root)
        {
            var manager = (await Log.ReadCorrelationAsync(root.Seq, Ct))
                .Last(m => IsTerminal(m) && m.Source == $"{team}/{TeamRegistry.DefaultManagerName}");

            await Log.AppendAsync(new NewMessage(
                MessageTypes.WorkflowCompleted, "{}", $"{team}/{TeamRegistry.DefaultManagerName}", manager.Seq), Ct);
        }

        public async Task<JsonElement> ActivityAsync(string team, DateTimeOffset? from = null, DateTimeOffset? to = null)
        {
            var url = from is { } f && to is { } t ? Url(team, f, t) : $"/api/teams/{team}/activity";
            var response = await Person.GetAsync(url, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} from {url}: {body}");
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        /// <summary>
        /// THE DEFAULT WINDOW ONCE IT HAS STOPPED MOVING: two reads 600 ms apart with the same end.
        /// Quiet as <see cref="QuietAsync"/> judges it (no run going, nothing queued) can still have
        /// one more row of the workflow on its way - under a release's load one landed 0.7 s after
        /// the team read quiet and moved the window's end - so a test that compares two windows
        /// over time takes its first only once the end has held.
        /// </summary>
        public async Task<JsonElement> SettledActivityAsync(string team)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var previous = await ActivityAsync(team);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(600, Ct);
                var current = await ActivityAsync(team);
                if (current.GetProperty("to").GetString() == previous.GetProperty("to").GetString()) return current;
                previous = current;
            }

            throw new TimeoutException($"{team}'s activity window did not settle.");
        }

        public async Task<Message> AwaitRowAsync(Func<Message, bool> match, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);

            while (DateTime.UtcNow < deadline)
            {
                if ((await Log.ReadAfterAsync(0, [MessageTypes.Started, MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                    .FirstOrDefault(match) is { } row)
                {
                    return row;
                }

                await Task.Delay(50, Ct);
            }

            throw new TimeoutException($"No row: {what}.");
        }

        /// <summary>The newest row of any of these workflows: a closed one's end, an open one's latest.</summary>
        public async Task<DateTimeOffset> LatestActivityAsync(params Message[] roots)
        {
            var latest = DateTimeOffset.MinValue;

            foreach (var root in roots)
            {
                foreach (var row in await Log.ReadCorrelationAsync(root.Seq, Ct))
                {
                    if (row.OccurredAt > latest) latest = row.OccurredAt;
                }
            }

            return latest;
        }

        /// <summary>Waits until no member of <paramref name="team"/> has a run going and none has
        /// started one for a moment.</summary>
        /// <summary>
        /// A RESET OF DEV AND THE MANAGER THAT DELETES MEMORY, taken once the team is quiet. Quiet as
        /// <see cref="QuietAsync"/> reads it can come a moment before the Manager is woken to read
        /// Dev's finished run; a reset then is refused, correctly, with "still working" (409). That
        /// refusal changes nothing, so it is waited out and asked again, up to 30 s, rather than
        /// failing a test about something else.
        /// </summary>
        public async Task<HttpResponseMessage> ResetMemoryAsync(string team)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);

            while (true)
            {
                await QuietAsync(team);
                var reset = await Person.PostAsJsonAsync($"/api/teams/{team}/reset", new
                {
                    members = new[] { "Dev", TeamRegistry.DefaultManagerName },
                    forgetHistory = true,
                    purge = true,
                    clearTranscripts = true,
                }, Ct);

                if (reset.StatusCode != HttpStatusCode.Conflict || DateTime.UtcNow > deadline) return reset;
                await Task.Delay(300, Ct);
            }
        }

        public async Task QuietAsync(string team)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            long? last = null;

            while (DateTime.UtcNow < deadline)
            {
                var rows = (await Log.ReadAfterAsync(0, [MessageTypes.Started, MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                    .Where(m => m.Source.StartsWith($"{team}/", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var going = rows.GroupBy(m => m.Source).Any(g => g.MaxBy(m => m.Seq)!.Type == MessageTypes.Started);
                var newest = rows.Count == 0 ? 0 : rows.Max(m => m.Seq);

                // QUIET AS THE PLATFORM JUDGES IT: nothing queued and nothing appended but not yet
                // handed out, as well as no run going (see TeamQuiet).
                if (!going && newest == last && await TeamQuiet.IsQuietAsync(_factory.Services, team, Ct)) return;

                last = going ? null : newest;
                await Task.Delay(300, Ct);
            }

            throw new TimeoutException($"{team} did not go quiet.");
        }

        private static bool IsTerminal(Message m) => m.Type is MessageTypes.Completed or MessageTypes.Failed;
    }
}
