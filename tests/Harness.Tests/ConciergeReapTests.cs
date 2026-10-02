using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Tests;

/// <summary>
/// A CONCIERGE IS ENDED ONLY WHEN NOBODY WATCHES IT AND IT HAS DONE NOTHING, both for the window:
/// output past its floor, a viewer's keystrokes or attach, and a platform call under its own
/// credential (one in flight included) each keep it. A session with a viewer is never ended. The
/// reaper's tenant row says why. Time is one hand-moved clock for every stamp and every judgement.
/// </summary>
public sealed class ConciergeReapTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly ConciergeSessionKey Person = new("user-1");
    private const long Floor = OutputFloor.DefaultBytesPerMinute;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_session_nobody_watches_that_does_nothing_is_ended_at_the_window()
    {
        var bed = new Bed();
        await bed.OpenAsync();

        bed.Clock.Advance(Window);

        var ended = Assert.Single(await bed.ReapAsync());
        Assert.Equal(Person, ended.Key);
        Assert.Equal(ConciergeActivity.Started, ended.LastActivity);
        Assert.False(bed.Store.Has(Person));
        Assert.True(bed.Engine.Spawned.Single().Disposed);
    }

    [Fact]
    public async Task A_session_nobody_watches_that_does_nothing_is_not_ended_a_moment_before_the_window()
    {
        var bed = new Bed();
        await bed.OpenAsync();

        bed.Clock.Advance(Window - Tick);

        Assert.Empty(await bed.ReapAsync());
        Assert.True(bed.Store.Has(Person));
    }

    [Fact]
    public async Task A_session_nobody_watches_that_keeps_printing_past_the_floor_is_not_ended_at_the_window()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        var pty = bed.Engine.Spawned.Single();

        // Busy every ten minutes for two windows: never ended.
        for (var i = 0; i < 12; i++)
        {
            bed.Clock.Advance(TimeSpan.FromMinutes(10));
            pty.Print(new byte[Floor + 1]);
            Assert.Empty(await bed.ReapAsync());
        }

        Assert.True(bed.Store.Has(Person));
        Assert.Equal(ConciergeActivity.Output, bed.Session.Activity.Last(bed.Clock.GetUtcNow()).Kind);
    }

    [Fact]
    public async Task A_session_nobody_watches_that_keeps_calling_the_platform_is_not_ended_at_the_window()
    {
        var bed = new Bed();
        await bed.OpenAsync();

        for (var i = 0; i < 12; i++)
        {
            bed.Clock.Advance(TimeSpan.FromMinutes(10));
            using (bed.Store.CallBegan(Person)) { }
            Assert.Empty(await bed.ReapAsync());
        }

        Assert.True(bed.Store.Has(Person));

        // And it is the call that kept it: a window of nothing after the last ends it.
        var lastCall = bed.Clock.GetUtcNow();
        bed.Clock.Advance(Window);
        var ended = Assert.Single(await bed.ReapAsync());
        Assert.Equal(ConciergeActivity.Call, ended.LastActivity);
        Assert.Equal(lastCall, ended.LastActivityAt);
    }

    [Fact]
    public async Task A_call_still_in_flight_keeps_a_session_past_the_window()
    {
        var bed = new Bed();
        await bed.OpenAsync();

        var call = bed.Store.CallBegan(Person)!;
        bed.Clock.Advance(Window * 3);
        Assert.Empty(await bed.ReapAsync());
        Assert.Equal(1, bed.Session.Activity.CallsInFlight);

        call.Dispose();
        var callEnd = bed.Clock.GetUtcNow();

        // The window runs from the call's END, not its start.
        bed.Clock.Advance(Window - Tick);
        Assert.Empty(await bed.ReapAsync());

        bed.Clock.Advance(Tick);
        var ended = Assert.Single(await bed.ReapAsync());
        Assert.Equal(callEnd, ended.LastActivityAt);
        Assert.Equal(ConciergeActivity.Call, ended.LastActivity);
    }

    [Fact]
    public async Task A_session_with_a_viewer_is_never_ended_however_long_it_has_done_nothing()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        using var evict = new CancellationTokenSource();
        bed.Session.Attachment.Attach(evict, 80, 24);

        for (var i = 0; i < 10; i++)
        {
            bed.Clock.Advance(Window * 5);
            Assert.Empty(await bed.ReapAsync());
        }

        Assert.True(bed.Store.Has(Person));
    }

    [Fact]
    public async Task A_viewer_who_leaves_after_the_window_gives_the_session_a_full_window_from_leaving()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        using var evict = new CancellationTokenSource();
        var viewer = bed.Session.Attachment.Attach(evict, 80, 24);

        bed.Clock.Advance(Window * 3);
        bed.Session.Attachment.Detach(viewer);

        // Every activity stamp is three windows old; the viewer's leaving is what counts.
        bed.Clock.Advance(Window - Tick);
        Assert.Empty(await bed.ReapAsync());

        bed.Clock.Advance(Tick);
        Assert.Single(await bed.ReapAsync());
    }

    [Fact]
    public async Task Output_below_the_floor_in_every_minute_does_not_keep_a_session_alive()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        var pty = bed.Engine.Spawned.Single();

        for (var minute = 0; minute < 60; minute++)
        {
            pty.Print(new byte[Floor / 2]);
            bed.Clock.Advance(TimeSpan.FromSeconds(30));
            pty.Print(new byte[Floor / 2 - 1]);
            bed.Clock.Advance(TimeSpan.FromSeconds(30));
        }

        var ended = Assert.Single(await bed.ReapAsync());
        Assert.Equal(ConciergeActivity.Started, ended.LastActivity);
        Assert.Equal(Start, ended.LastActivityAt);
    }

    [Fact]
    public async Task Output_of_exactly_the_floor_in_a_minute_is_not_activity()
    {
        var bed = new Bed();
        await bed.OpenAsync();

        bed.Clock.Advance(TimeSpan.FromSeconds(10));
        bed.Engine.Spawned.Single().Print(new byte[Floor]);

        bed.Clock.Advance(Window - TimeSpan.FromSeconds(10));
        var ended = Assert.Single(await bed.ReapAsync());
        Assert.Equal(ConciergeActivity.Started, ended.LastActivity);
    }

    [Fact]
    public async Task One_minute_above_the_floor_is_activity_at_that_minute()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        var pty = bed.Engine.Spawned.Single();

        bed.Clock.Advance(TimeSpan.FromSeconds(5));
        pty.Print(new byte[Floor]);
        bed.Clock.Advance(TimeSpan.FromSeconds(5));
        pty.Print(new byte[1]);
        var crossing = bed.Clock.GetUtcNow();

        Assert.Equal((crossing, ConciergeActivity.Output), bed.Session.Activity.Last(crossing));

        // A further chunk in the same minute moves it to its own time.
        bed.Clock.Advance(TimeSpan.FromSeconds(20));
        pty.Print(new byte[1]);
        var later = bed.Clock.GetUtcNow();
        Assert.Equal((later, ConciergeActivity.Output), bed.Session.Activity.Last(later));

        bed.Clock.Advance(Window - Tick);
        Assert.Empty(await bed.ReapAsync());

        bed.Clock.Advance(Tick);
        var ended = Assert.Single(await bed.ReapAsync());
        Assert.Equal(later, ended.LastActivityAt);
    }

    [Fact]
    public async Task Output_below_the_floor_either_side_of_a_minute_edge_is_not_activity()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        var pty = bed.Engine.Spawned.Single();

        // About twice the floor within two seconds, but each minute stays under it.
        bed.Clock.Advance(TimeSpan.FromSeconds(59));
        pty.Print(new byte[Floor - 1]);
        bed.Clock.Advance(TimeSpan.FromSeconds(2));
        pty.Print(new byte[Floor - 1]);

        bed.Clock.Advance(Window - TimeSpan.FromSeconds(61));
        var ended = Assert.Single(await bed.ReapAsync());
        Assert.Equal(ConciergeActivity.Started, ended.LastActivity);
    }

    [Fact]
    public async Task A_minute_slot_reused_after_an_hour_starts_from_zero()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        var pty = bed.Engine.Spawned.Single();

        // Keep it watched so only the counting is under test.
        using var evict = new CancellationTokenSource();
        var viewer = bed.Session.Attachment.Attach(evict, 80, 24);
        var attached = bed.Clock.GetUtcNow();

        pty.Print(new byte[Floor - 1]);
        bed.Clock.Advance(TimeSpan.FromMinutes(60));
        pty.Print(new byte[Floor - 1]);

        var now = bed.Clock.GetUtcNow();
        Assert.Equal((attached, ConciergeActivity.Attached), bed.Session.Activity.Last(now));

        var perMinute = bed.Session.Activity.PerMinute(now);
        Assert.Equal(ConciergeActivity.MinutesKept, perMinute.Count);
        Assert.Equal(Floor - 1, perMinute[^1]);
        Assert.All(perMinute.Take(perMinute.Count - 1), total => Assert.Equal(0, total));

        bed.Session.Attachment.Detach(viewer);
    }

    [Fact]
    public async Task A_viewers_keystrokes_count_as_activity()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        using var evict = new CancellationTokenSource();
        var viewer = bed.Session.Attachment.Attach(evict, 80, 24);

        bed.Clock.Advance(TimeSpan.FromMinutes(30));
        bed.Session.Session.Write("ls\r"u8);
        var typed = bed.Clock.GetUtcNow();
        bed.Session.Attachment.Detach(viewer);

        Assert.Equal("ls\r", bed.Engine.Spawned.Single().Typed);
        Assert.Equal((typed, ConciergeActivity.Typed), bed.Session.Activity.Last(typed));

        bed.Clock.Advance(Window - Tick);
        Assert.Empty(await bed.ReapAsync());
    }

    [Fact]
    public async Task Attaching_counts_as_activity()
    {
        var bed = new Bed();
        await bed.OpenAsync();

        bed.Clock.Advance(TimeSpan.FromMinutes(40));
        using var evict = new CancellationTokenSource();

        // The same call the socket route makes.
        var viewer = bed.Session.Attachment.Attach(evict, 80, 24);
        var attached = bed.Clock.GetUtcNow();
        bed.Session.Attachment.Detach(viewer);

        Assert.Equal((attached, ConciergeActivity.Attached), bed.Session.Activity.Last(attached));
        Assert.True(bed.Session.Activity.EverViewed);
    }

    [Fact]
    public async Task A_session_nobody_ever_attached_is_unwatched_from_its_start()
    {
        var bed = new Bed();
        await bed.OpenAsync();

        Assert.Equal(Start, bed.Session.Attachment.IdleSince);
        Assert.False(bed.Session.Activity.EverViewed);

        bed.Clock.Advance(Window);
        var ended = Assert.Single(await bed.ReapAsync());
        Assert.True(ended.NeverViewed);
        Assert.Equal(Start, ended.LastViewerAt);
    }

    [Fact]
    public async Task The_tenant_row_says_no_viewer_since_and_no_activity_since()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        using var evict = new CancellationTokenSource();
        var viewer = bed.Session.Attachment.Attach(evict, 80, 24);
        bed.Clock.Advance(TimeSpan.FromMinutes(4));
        bed.Session.Attachment.Detach(viewer);
        bed.Clock.Advance(TimeSpan.FromMinutes(1));
        using (bed.Store.CallBegan(Person)) { }

        bed.Clock.Advance(Window);
        Assert.Single(await bed.Reaper().SweepAsync(bed.Clock.GetUtcNow(), Ct));

        var row = Assert.Single(bed.Log.Rows);
        Assert.Equal(TenantActions.ConciergeEndedIdle, row.Action);
        Assert.Null(row.ActorId);
        Assert.Equal(Person.User, row.Subject);
        Assert.Equal("person@example.test", row.SubjectName);

        using var detail = JsonDocument.Parse(row.Detail!);
        var root = detail.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("worker").ValueKind);
        Assert.Equal("01:00:00", root.GetProperty("window").GetString());
        Assert.Equal(Start.AddMinutes(4), root.GetProperty("lastViewerAt").GetDateTimeOffset());
        Assert.Equal(Start.AddMinutes(5), root.GetProperty("lastActivityAt").GetDateTimeOffset());
        Assert.Equal("call", root.GetProperty("lastActivity").GetString());
        Assert.False(root.GetProperty("neverViewed").GetBoolean());
        Assert.Equal(
            "No viewer since 2026-10-02T12:04:00Z and no activity since 2026-10-02T12:05:00Z (last: call); the window is 01:00:00.",
            root.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_tenant_row_of_a_session_nobody_ever_watched_says_it_started_unwatched()
    {
        var bed = new Bed();
        await bed.OpenAsync();

        bed.Clock.Advance(Window);
        await bed.Reaper().SweepAsync(bed.Clock.GetUtcNow(), Ct);

        using var detail = JsonDocument.Parse(Assert.Single(bed.Log.Rows).Detail!);
        Assert.True(detail.RootElement.GetProperty("neverViewed").GetBoolean());
        Assert.Equal(
            "No viewer since it started at 2026-10-02T12:00:00Z and no activity since 2026-10-02T12:00:00Z (last: started); the window is 01:00:00.",
            detail.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_person_ending_a_session_writes_no_idle_row()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        var reaper = bed.Reaper();

        await bed.Store.EndAsync(Person);
        bed.Clock.Advance(Window * 2);
        Assert.Empty(await reaper.SweepAsync(bed.Clock.GetUtcNow(), Ct));

        Assert.Empty(bed.Log.Rows);
    }

    [Fact]
    public async Task The_reaper_writes_one_row_per_session_it_ends_and_keeps_sweeping_after_a_failed_row()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        await bed.OpenAsync(new ConciergeSessionKey("user-2"));
        var reaper = bed.Reaper();

        bed.Log.Refuse = true;
        bed.Clock.Advance(Window);
        Assert.Equal(2, (await reaper.SweepAsync(bed.Clock.GetUtcNow(), Ct)).Count);

        // The end stands though no row could be written.
        Assert.False(bed.Store.Has(Person));
        Assert.False(bed.Store.Has(new ConciergeSessionKey("user-2")));
        Assert.Empty(bed.Log.Rows);

        bed.Log.Refuse = false;
        await bed.OpenAsync(new ConciergeSessionKey("user-3"));
        bed.Clock.Advance(Window);
        Assert.Single(await reaper.SweepAsync(bed.Clock.GetUtcNow(), Ct));
        Assert.Equal("user-3", Assert.Single(bed.Log.Rows).Subject);
    }

    [Fact]
    public async Task A_reaped_session_releases_its_lease()
    {
        var leases = new InstanceLeases(() => 1);
        var actions = new LeaseActions(leases, new RunHeartbeat(), new NoReports());
        var revoked = 0;
        var bed = new Bed(actions.Releasing((_, _) =>
        {
            revoked++;
            return Task.CompletedTask;
        }));
        await bed.OpenAsync();
        var owner = LeaseOwner.ForConcierge(ConciergeLaunchFactory.PrincipalId(Person.User));
        Assert.Equal(LeaseOutcome.Granted, (await actions.AcquireAsync("heavy", owner, Ct)).Outcome);

        bed.Clock.Advance(Window);
        Assert.Single(await bed.ReapAsync());

        Assert.Empty(Assert.Single(leases.Leases()).Holders);
        Assert.Equal(1, revoked);
    }

    [Fact]
    public async Task A_person_ends_a_session_that_is_active_at_once()
    {
        var bed = new Bed();
        await bed.OpenAsync();
        using var call = bed.Store.CallBegan(Person);
        bed.Engine.Spawned.Single().Print(new byte[Floor * 4]);

        await bed.Store.EndAsync(Person);

        Assert.False(bed.Store.Has(Person));
        Assert.True(bed.Engine.Spawned.Single().Disposed);
    }

    [Fact]
    public async Task A_session_takes_the_floor_its_preset_declares_and_the_default_otherwise()
    {
        var declared = Preset(new IdleOutputFloor(100, "1.0.0, 15 minutes idle, at most 40 bytes a minute"));
        var undeclared = Preset(null);

        Assert.Equal(new OutputFloor(100, OutputFloor.Declared, "1.0.0, 15 minutes idle, at most 40 bytes a minute"), OutputFloor.Of(declared));
        Assert.Equal(new OutputFloor(Floor, OutputFloor.Default, null), OutputFloor.Of(undeclared));
        Assert.Equal(OutputFloor.PlatformDefault, OutputFloor.Of(null));

        // And the session judges its output by it: 101 bytes is activity under the declared floor.
        var bed = new Bed(floor: OutputFloor.Of(declared));
        await bed.OpenAsync();
        Assert.Equal(OutputFloor.Declared, bed.Session.Activity.Floor.Source);

        bed.Clock.Advance(TimeSpan.FromMinutes(30));
        bed.Engine.Spawned.Single().Print(new byte[101]);
        bed.Clock.Advance(Window - Tick);
        Assert.Empty(await bed.ReapAsync());

        // The same bytes under the default floor are not.
        var plain = new Bed();
        await plain.OpenAsync();
        Assert.Equal(OutputFloor.Default, plain.Session.Activity.Floor.Source);
        plain.Clock.Advance(TimeSpan.FromMinutes(30));
        plain.Engine.Spawned.Single().Print(new byte[101]);
        plain.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Single(await plain.ReapAsync());
    }

    private static AgentDefinition Preset(IdleOutputFloor? floor) =>
        new("fixture", AgentMode.Interactive, new AgentLaunch("fixture-cli", []), IdleOutput: floor);

    /// <summary>A store over fake terminals on one hand-moved clock, a tenant log that records, and a reaper over both.</summary>
    private sealed class Bed(ConciergeRevoke? revoke = null, OutputFloor? floor = null)
    {
        public ManualTime Clock { get; } = new(Start);

        public FakePtyEngine Engine { get; } = new();

        public RecordingLog Log { get; } = new();

        public ConciergeSessionStore Store => _store ??= new ConciergeSessionStore(
            Engine,
            (_, _, _, _) => Task.FromResult(new PtySpec("fixture-cli", Path.GetTempPath())),
            revoke ?? ((_, _) => Task.CompletedTask),
            Clock,
            floor is null ? null : _ => floor);

        public ConciergeSession Session { get; private set; } = null!;

        private ConciergeSessionStore? _store;

        public async Task OpenAsync(ConciergeSessionKey? key = null)
        {
            var session = await Store.AttachAsync(key ?? Person, "", 80, 24, Ct);
            if (key is null) Session = session;
        }

        public Task<IReadOnlyList<ConciergeReaped>> ReapAsync() => Store.ReapIdleAsync(Window, Clock.GetUtcNow());

        public ConciergeReaper Reaper() => new(
            Store, () => Window, TimeSpan.FromMinutes(1), NullLogger<ConciergeReaper>.Instance, Log,
            (_, _) => Task.FromResult<string?>("person@example.test"));
    }

    internal sealed record Row(string? ActorId, string Action, string? Subject, string? SubjectName, string? Detail);

    internal sealed class RecordingLog : ITenantLog
    {
        public List<Row> Rows { get; } = [];

        public bool Refuse { get; set; }

        public Task WriteAsync(
            string? actorId, string? actorEmail, string action, string? subject = null,
            string? subjectName = null, string? detail = null, CancellationToken ct = default)
        {
            if (Refuse) throw new IOException("the disk is full");

            lock (Rows) Rows.Add(new Row(actorId, action, subject, subjectName, detail));
            return Task.CompletedTask;
        }

        public Task<TenantEvent?> FindLatestAsync(string action, string subject, CancellationToken ct = default) =>
            Task.FromResult<TenantEvent?>(null);

        public Task<IReadOnlyList<TenantEvent>> FindLatestBySubjectAsync(IReadOnlyCollection<string> actions, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TenantEvent>>([]);

        public Task<TenantLogPage> ReadAsync(long? before = null, int take = 50, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoReports : IMemberReports
    {
        public Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default) => Ok();
        public Task<MemberReportOutcome> BlockedAsync(ContainerId member, string reason, int? item = null, CancellationToken ct = default) => Ok();
        public Task<MemberReportOutcome> DeferAsync(ContainerId member, int item, string reason, CancellationToken ct = default) => Ok();
        public Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default) => Ok();
        public Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default) => Ok();
        public Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default) => Ok();

        private static Task<MemberReportOutcome> Ok() => Task.FromResult(MemberReportOutcome.Ok);
    }
}
