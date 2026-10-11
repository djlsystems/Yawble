using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHAT A LEASE CALL DOES BEYOND THE LEASE: a member put in the queue has its run's silence clock
/// paused and its card reads "waiting for a heavy-work slot"; a member granted the lease from the
/// queue, or leaving it, has its clock running again from a full window. The route and the run's
/// end both come through here, so the clock and the card follow the lease whichever moved it.
///
/// The Concierge has no card and no silence clock, so for it only the lease moves; its session's
/// end comes through here too (<see cref="Releasing"/>), so a member it hands the lease to is told.
///
/// THE MEMORY FOLLOWS THE LEASE TOO: after every call, <see cref="RunAllowances.ReconcileAsync"/>
/// raises the run that now holds <c>heavy</c> and lowers the one that no longer does, so any path
/// that releases a lease through here restores the run's own limit.
/// </summary>
public sealed class LeaseActions : IRunWorkerClient
{
    private readonly InstanceLeases leases;
    private readonly IRunWorker worker;
    private readonly IMemberReports reports;

    /// <summary>
    /// The Host's: lease moves reach the runs through <paramref name="worker"/>, as
    /// <see cref="HoldIdleClock"/> and <see cref="ChangeRunMemoryAllowance"/>.
    /// </summary>
    public LeaseActions(InstanceLeases leases, IRunWorker worker, IMemberReports reports)
    {
        this.leases = leases;
        this.worker = worker;
        this.reports = reports;
    }

    /// <summary>
    /// Over a worker of its own, in this process, made from <paramref name="heartbeat"/> and
    /// <paramref name="allowances"/>: the moves cross the protocol all the same.
    /// </summary>
    public LeaseActions(InstanceLeases leases, RunHeartbeat heartbeat, IMemberReports reports, RunAllowances? allowances = null)
        : this(
            leases,
            InProcessWorker.Connect(
                WorkerId.Local,
                events => new WorkerHost(WorkerId.Local, events, new RunLauncher(heartbeat), heartbeat, allowances),
                (_, _) => Task.CompletedTask).Worker,
            reports)
    {
    }

    /// <summary>The worker whose runs these leases move.</summary>
    public IRunWorker Worker => worker;

    /// <summary>The words a queued member's card reads.</summary>
    public const string WaitingWords = "waiting for a heavy-work slot";

    /// <summary>The words a member's card reads when the queue hands it the lease.</summary>
    public const string GrantedWords = "took the heavy-work slot";

    public async Task<LeaseAnswer> AcquireAsync(string name, LeaseOwner owner, CancellationToken ct = default)
    {
        var answer = leases.Acquire(name, owner);

        // PAUSED BEFORE THE CARD IS TOLD: the progress row below resets a running clock, and a
        // paused one ignores it.
        if (answer.NewlyQueued && Member(owner) is { } member)
        {
            await worker.SendAsync(new HoldIdleClock(member, Held: true), CancellationToken.None);
            await CardAsync(member, WaitingWords, ct);
        }

        // GRANTED IS NEVER PAUSED: every release comes through here and un-pauses the member it
        // promotes, and this keeps a granted call from leaving a clock paused all the same.
        if (answer.Outcome == LeaseOutcome.Granted && Member(owner) is { } holder)
        {
            await worker.SendAsync(new HoldIdleClock(holder, Held: false), CancellationToken.None);
        }

        await MovedAsync(answer, ct);
        return answer;
    }

    public async Task<LeaseAnswer> ReleaseAsync(string name, LeaseOwner owner, CancellationToken ct = default)
    {
        var answer = leases.Release(name, owner);
        await MovedAsync(answer, ct);
        return answer;
    }

    /// <summary>The owner's run is over, however it ended: nothing it held or waited for is kept.</summary>
    public async Task<LeaseAnswer> EndedAsync(LeaseOwner owner, CancellationToken ct = default)
    {
        var answer = leases.Ended(owner);
        await MovedAsync(answer, ct);
        return answer;
    }

    /// <summary>
    /// A Concierge's revoke that first ends the session's lease through <see cref="EndedAsync"/>,
    /// then runs <paramref name="then"/> however the release went. A Concierge's run is its
    /// session, so its end is the session's end; the token is not passed to the release, because
    /// the revoke also runs during shutdown with that token already cancelled.
    /// </summary>
    public ConciergeRevoke Releasing(ConciergeRevoke then) => async (key, ct) =>
    {
        try
        {
            await EndedAsync(LeaseOwner.ForConcierge(ConciergeLaunchFactory.PrincipalId(key.User)), CancellationToken.None);
        }
        finally
        {
            await then(key, ct);
        }
    };

    private async Task MovedAsync(LeaseAnswer answer, CancellationToken ct)
    {
        // Before the cards: a granted run has its allowance by the time its lease call answers.
        // A courtesy like the card: the lease has moved whether or not the limit could follow.
        {
            try
            {
                await worker.SendAsync(
                    new ChangeRunMemoryAllowance([.. leases.Holders(InstanceLeases.Heavy).Select(o => o.Key)]),
                    CancellationToken.None);
            }
            catch (Exception)
            {
            }
        }

        foreach (var owner in answer.Withdrawn ?? [])
        {
            if (Member(owner) is { } member) await worker.SendAsync(new HoldIdleClock(member, Held: false), CancellationToken.None);
        }

        foreach (var owner in answer.Promoted ?? [])
        {
            if (Member(owner) is not { } member) continue;

            await worker.SendAsync(new HoldIdleClock(member, Held: false), CancellationToken.None);
            await CardAsync(member, GrantedWords, ct);
        }
    }

    /// <summary>
    /// The member's card line, as a progress row. A courtesy: the lease has moved whether or not
    /// the row is written, and a failed write must not leave the caller without its answer.
    /// </summary>
    private async Task CardAsync(ContainerId member, string words, CancellationToken ct)
    {
        try
        {
            await reports.ProgressAsync(member, words, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
    }

    private static ContainerId? Member(LeaseOwner owner) =>
        owner.Team is not null && ContainerId.TryParse(owner.Key, out var id) ? id : null;
}
