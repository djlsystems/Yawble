using Harness.Contracts;
using Harness.Host;
using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// A CONCIERGE'S LEASE ENDS WITH ITS SESSION, through the same path as a run's end
/// (<see cref="LeaseActions.EndedAsync"/>): however the session ends - ended by a person, its CLI
/// exiting with a viewer still attached, or a dispose that throws - the lease is released and a
/// member queued behind it is un-paused and its card told at once.
/// </summary>
public sealed class ConciergeLeaseTests
{
    private static readonly ContainerId Dev = new("alpha", "DeveloperRowan");
    private static readonly ConciergeSessionKey Person = new("user-1");
    private static readonly LeaseOwner Concierge = LeaseOwner.ForConcierge(ConciergeLaunchFactory.PrincipalId(Person.User));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_member_queued_behind_a_concierge_is_unpaused_and_told_when_the_session_ends()
    {
        var heartbeat = new RunHeartbeat();
        var reports = new RecordedReports();
        var leases = new InstanceLeases(() => 1);
        var actions = new LeaseActions(leases, heartbeat, reports);
        var revoked = 0;
        await using var store = Store(new Engine(), actions, () => revoked++);

        await store.AttachAsync(Person, "", 80, 24, Ct);
        Assert.Equal(LeaseOutcome.Granted, (await actions.AcquireAsync("heavy", Concierge, Ct)).Outcome);

        using var clock = ChildProcess.Clock(heartbeat, Dev, 1, Ct);
        Assert.Equal(LeaseOutcome.Queued, (await actions.AcquireAsync("heavy", LeaseOwner.For(Dev), Ct)).Outcome);
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
        Assert.False(clock.Expired);

        await store.EndAsync(Person);

        // Promoted and told at once, with no further acquire from the member.
        Assert.Equal((Dev, LeaseActions.GrantedWords), reports.Lines[^1]);
        Assert.Equal(["alpha/DeveloperRowan"], Assert.Single(leases.Leases()).Holders.Select(h => $"{h.Team}/{h.Member}"));
        Assert.Equal(1, revoked);

        // Its clock runs again: silence stops it as before.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!clock.Expired && DateTime.UtcNow < deadline) await Task.Delay(50, Ct);
        Assert.True(clock.Expired);
    }

    [Fact]
    public async Task A_concierge_process_that_exits_with_a_viewer_attached_releases_its_lease()
    {
        var leases = new InstanceLeases(() => 1);
        var actions = new LeaseActions(leases, new RunHeartbeat(), new RecordedReports());
        var engine = new Engine();
        await using var store = Store(engine, actions, () => { });

        var console = await store.AttachAsync(Person, "", 80, 24, Ct);
        using var evict = new CancellationTokenSource();
        console.Attachment.Attach(evict, 80, 24);
        Assert.True(console.Attachment.HasViewer);
        Assert.Equal(LeaseOutcome.Granted, (await actions.AcquireAsync("heavy", Concierge, Ct)).Outcome);

        engine.Last!.Exit(0);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Assert.Single(leases.Leases()).Holders.Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
        Assert.Empty(Assert.Single(leases.Leases()).Holders);
        Assert.False(store.Has(Person));
    }

    [Fact]
    public async Task A_dispose_that_throws_still_releases_the_lease()
    {
        var leases = new InstanceLeases(() => 1);
        var actions = new LeaseActions(leases, new RunHeartbeat(), new RecordedReports());
        var revoked = 0;
        await using var store = Store(new Engine(throwOnDispose: true), actions, () => revoked++);

        await store.AttachAsync(Person, "", 80, 24, Ct);
        Assert.Equal(LeaseOutcome.Granted, (await actions.AcquireAsync("heavy", Concierge, Ct)).Outcome);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EndAsync(Person));

        Assert.Empty(Assert.Single(leases.Leases()).Holders);
        Assert.Equal(1, revoked);
        Assert.False(store.Has(Person));
    }

    /// <summary>The store as Program.cs wires it: the revoke is <see cref="LeaseActions.Releasing"/>
    /// around the credential's own revoke.</summary>
    private static ConciergeSessionStore Store(Engine engine, LeaseActions actions, Action credentialRevoked) =>
        new(
            engine,
            (_, _, _, _) => Task.FromResult(new PtySpec("true", Path.GetTempPath())),
            actions.Releasing((_, _) =>
            {
                credentialRevoked();
                return Task.CompletedTask;
            }));

    private sealed class Engine(bool throwOnDispose = false) : IPtyEngine
    {
        public Session? Last { get; private set; }

        public Task<IPtySession> SpawnAsync(PtySpec spec, CancellationToken ct) =>
            Task.FromResult<IPtySession>(Last = new Session(throwOnDispose));
    }

    private sealed class Session(bool throwOnDispose) : IPtySession
    {
#pragma warning disable CS0067 // never raised: this terminal is silent
        public event Action<byte[]>? Output;
#pragma warning restore CS0067
        public event Action<int>? Exited;

        public void Exit(int code) => Exited?.Invoke(code);

        public void Write(ReadOnlySpan<byte> bytes) { }

        public void Resize(int cols, int rows) { }

        public ValueTask DisposeAsync() =>
            throwOnDispose ? throw new InvalidOperationException("dispose failed") : ValueTask.CompletedTask;
    }

    private sealed class RecordedReports : IMemberReports
    {
        public List<(ContainerId, string)> Lines { get; } = [];

        public Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default)
        {
            lock (Lines) Lines.Add((member, status));
            return Task.FromResult(MemberReportOutcome.Ok);
        }

        public Task<MemberReportOutcome> BlockedAsync(ContainerId member, string reason, int? item = null, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> DeferAsync(ContainerId member, int item, string reason, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);

        public Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default) =>
            Task.FromResult(MemberReportOutcome.Ok);
    }
}
