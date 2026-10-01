using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHAT HOLDS A LEASE AND WHO IS QUEUED FOR IT, read-only. What the activity monitor reads to show
/// the heavy lease's holder and queue; it can change nothing through this.
/// </summary>
public interface ILeaseState
{
    /// <summary>Every built-in lease, holders first-granted first and the queue in order.</summary>
    IReadOnlyList<LeaseState> Leases();
}

/// <summary>One lease: its name, how many may hold it at once, who holds it and who waits.</summary>
public sealed record LeaseState(
    string Name, int Limit, IReadOnlyList<LeaseHolding> Holders, IReadOnlyList<LeaseHolding> Queue);

/// <summary>
/// One holder or waiter: its team (null for the Concierge, which has none) and member, and since
/// when it has held or waited.
/// </summary>
public sealed record LeaseHolding(string? Team, string Member, DateTimeOffset Since)
{
    /// <summary>"Developer on team alpha", or "the Concierge".</summary>
    public string Words => Team is null ? $"the {Member}" : $"{Member} on team {Team}";
}

/// <summary>
/// Who asks for a lease. <see cref="Key"/> is the run's owner - a member's container id, a
/// Concierge's principal id - and is what the run's end releases by.
/// </summary>
public sealed record LeaseOwner(string Key, string? Team, string Member)
{
    public static LeaseOwner For(ContainerId member) => new(member.ToString(), member.Team, member.Name);

    public static LeaseOwner ForConcierge(string principalId) => new(principalId, null, "Concierge");
}

public enum LeaseOutcome
{
    Granted,
    Queued,
    Released,
    NotHeld,
    Unknown,
}

/// <summary>
/// The answer to one call, in the words the agent reads (<see cref="Sentence"/>), with what changed
/// for anybody else: <see cref="Promoted"/> owners took the lease from the queue, and
/// <see cref="Withdrawn"/> left the queue without it. <see cref="NewlyQueued"/> is true only on the
/// call that put the caller in the queue, so a waiter calling again is not re-announced.
/// </summary>
public sealed record LeaseAnswer(
    LeaseOutcome Outcome,
    string Sentence,
    int? Position = null,
    IReadOnlyList<LeaseHolding>? HeldBy = null,
    bool NewlyQueued = false,
    IReadOnlyList<LeaseOwner>? Promoted = null,
    IReadOnlyList<LeaseOwner>? Withdrawn = null);

/// <summary>
/// INSTANCE-WIDE NAMED LEASES, across every team. The only built-in name is <c>heavy</c>: what the
/// project calls heavy (a full test command, a full build, an image build) is the agent's call, and
/// this knows nothing of any language or tool. At most <c>leases.heavy.holders</c> hold it at once,
/// read through a delegate on every call; the rest queue in order and are granted it as holders
/// release. Waiting is waiting: nothing is refused but an unknown name.
///
/// IN MEMORY ONLY. A lease belongs to a run, and no run outlives the Host, so a restart starts
/// with none held. A run's end releases everything it holds or waits for (<see cref="Ended"/>),
/// however it ended.
/// </summary>
public sealed class InstanceLeases(Func<int> heavyHolders, TimeProvider? clock = null) : ILeaseState
{
    public const string Heavy = "heavy";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<(LeaseOwner Owner, DateTimeOffset Since)> _holders = [];
    private readonly List<(LeaseOwner Owner, DateTimeOffset Since)> _queue = [];

    /// <summary>The lease names this instance knows, in the words a refusal uses.</summary>
    public static IReadOnlyList<string> Names { get; } = [Heavy];

    public LeaseAnswer Acquire(string name, LeaseOwner owner)
    {
        if (Unknown(name) is { } refused) return refused;

        lock (_gate)
        {
            var promoted = Promote();

            if (IndexOf(_holders, owner) >= 0)
            {
                return new LeaseAnswer(
                    LeaseOutcome.Granted,
                    $"Granted: you hold the `{Heavy}` lease. Release it with `lease` when the heavy command finishes.",
                    Promoted: promoted);
            }

            var newlyQueued = false;
            if (IndexOf(_queue, owner) < 0)
            {
                if (_queue.Count == 0 && _holders.Count < Limit)
                {
                    _holders.Add((owner, _clock.GetUtcNow()));
                    return new LeaseAnswer(
                        LeaseOutcome.Granted,
                        $"Granted: you hold the `{Heavy}` lease. Release it with `lease` when the heavy command finishes.",
                        Promoted: promoted);
                }

                _queue.Add((owner, _clock.GetUtcNow()));
                newlyQueued = true;
            }

            var position = IndexOf(_queue, owner) + 1;
            var heldBy = Holdings(_holders);
            return new LeaseAnswer(
                LeaseOutcome.Queued,
                $"Queued, position {position}: the `{Heavy}` lease is held by "
                + $"{string.Join(", ", heldBy.Select(h => h.Words))}. Call `lease` acquire again to wait; "
                + "your run's silence clock is paused while you are queued.",
                position,
                heldBy,
                newlyQueued,
                promoted);
        }
    }

    public LeaseAnswer Release(string name, LeaseOwner owner)
    {
        if (Unknown(name) is { } refused) return refused;

        lock (_gate)
        {
            var held = Remove(_holders, owner);
            var queued = Remove(_queue, owner);
            var promoted = Promote();

            return held
                ? new LeaseAnswer(LeaseOutcome.Released, $"Released the `{Heavy}` lease.", Promoted: promoted)
                : queued
                    ? new LeaseAnswer(
                        LeaseOutcome.Released, $"Left the queue for the `{Heavy}` lease.",
                        Promoted: promoted, Withdrawn: [owner])
                    : new LeaseAnswer(
                        LeaseOutcome.NotHeld, $"You did not hold the `{Heavy}` lease, so there was nothing to release.",
                        Promoted: promoted);
        }
    }

    /// <summary>
    /// THE OWNER'S RUN HAS ENDED, however it ended: everything it held or waited for is released,
    /// and the queue moves up. No lease outlives the run that took it.
    /// </summary>
    public LeaseAnswer Ended(LeaseOwner owner)
    {
        lock (_gate)
        {
            var held = Remove(_holders, owner);
            var queued = Remove(_queue, owner);
            var promoted = Promote();

            return new LeaseAnswer(
                held || queued ? LeaseOutcome.Released : LeaseOutcome.NotHeld,
                held ? $"The run ended holding the `{Heavy}` lease; it is released." : "",
                Promoted: promoted,
                Withdrawn: queued ? [owner] : []);
        }
    }

    public IReadOnlyList<LeaseState> Leases()
    {
        lock (_gate)
        {
            return [new LeaseState(Heavy, Limit, Holdings(_holders), Holdings(_queue))];
        }
    }

    /// <summary>Read on every call: a change to the setting applies at the next acquire or release.</summary>
    private int Limit => Math.Max(1, heavyHolders());

    /// <summary>The sentence refusing a name that is not a lease, or null for a lease's name.</summary>
    public static string? UnknownName(string? name) =>
        string.Equals(name?.Trim(), Heavy, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"There is no lease named '{name?.Trim()}'. The only lease is `{Heavy}`: take it before "
              + "running anything you know to be heavy, such as the repository's full test command or a full build.";

    private static LeaseAnswer? Unknown(string name) =>
        UnknownName(name) is { } sentence ? new LeaseAnswer(LeaseOutcome.Unknown, sentence) : null;

    /// <summary>Moves the queue's head into free holder places, oldest first. Under the gate.</summary>
    private List<LeaseOwner> Promote()
    {
        List<LeaseOwner> promoted = [];
        while (_queue.Count > 0 && _holders.Count < Limit)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            _holders.Add((next.Owner, _clock.GetUtcNow()));
            promoted.Add(next.Owner);
        }

        return promoted;
    }

    private static int IndexOf(List<(LeaseOwner Owner, DateTimeOffset Since)> list, LeaseOwner owner) =>
        list.FindIndex(entry => string.Equals(entry.Owner.Key, owner.Key, StringComparison.OrdinalIgnoreCase));

    private static bool Remove(List<(LeaseOwner Owner, DateTimeOffset Since)> list, LeaseOwner owner) =>
        list.RemoveAll(entry => string.Equals(entry.Owner.Key, owner.Key, StringComparison.OrdinalIgnoreCase)) > 0;

    private static List<LeaseHolding> Holdings(List<(LeaseOwner Owner, DateTimeOffset Since)> list) =>
        [.. list.Select(entry => new LeaseHolding(entry.Owner.Team, entry.Owner.Member, entry.Since))];
}
