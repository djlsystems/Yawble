using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE <c>containerChanged</c> PUSH, COALESCED PER MEMBER. A member publishes its snapshot at
/// least three times per instruction and again for every progress line, and every browser in a
/// team's group - every browser on the Monitor or the Teams table - pulled the feed, re-read the
/// board and re-read Needs you on each. A snapshot that differs from the last one sent (a state, a
/// workflow, a mark, the queue) goes at once. One that is the same as the last sent is the
/// "something happened" the feed pulls on; it is held for <see cref="DefaultWindow"/> and the
/// newest is sent once when the window closes, so a run printing twenty lines a second costs the
/// browsers four pushes, not twenty. A change arriving while one is held goes at once and the held
/// one is dropped: the change is the newer fact and the feed pulls on it.
/// </summary>
public sealed class SnapshotPushCoalescer(
    Func<ContainerSnapshot, Task> send, TimeSpan window, TimeProvider? clock = null, ILogger? log = null)
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<ContainerId, Entry> _members = [];

    private sealed class Entry
    {
        public ContainerSnapshot? Last;
        public ContainerSnapshot? Held;
        public ITimer? Timer;

        /// <summary>The member's sends, one after another: a held snapshot flushed as a change arrives never overtakes it.</summary>
        public Task Chain = Task.CompletedTask;
    }

    public void Offer(ContainerSnapshot snapshot)
    {
        var id = new ContainerId(snapshot.Team, snapshot.Id);

        lock (_gate)
        {
            if (!_members.TryGetValue(id, out var entry)) _members[id] = entry = new Entry();

            if (entry.Last is null || !Same(entry.Last, snapshot))
            {
                entry.Last = snapshot;
                entry.Held = null;
                Enqueue(entry, snapshot);
            }
            else
            {
                entry.Held = snapshot;
                entry.Timer ??= _clock.CreateTimer(_ => Flush(id), null, window, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Whether two snapshots say the same thing: every field, the collections by their items.
    /// A record compares a collection by reference, and a fresh snapshot carries fresh lists.
    /// </summary>
    public static bool Same(ContainerSnapshot a, ContainerSnapshot b) =>
        a with { Subscribes = b.Subscribes, UnresolvedAgents = b.UnresolvedAgents, Wakes = b.Wakes } == b
        && a.Subscribes.SequenceEqual(b.Subscribes)
        && SameItems(a.UnresolvedAgents, b.UnresolvedAgents)
        && SameItems(a.Wakes, b.Wakes);

    private static bool SameItems<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    private void Flush(ContainerId id)
    {
        lock (_gate)
        {
            if (!_members.TryGetValue(id, out var entry)) return;

            entry.Timer?.Dispose();
            entry.Timer = null;
            if (entry.Held is not { } held) return;

            entry.Held = null;
            entry.Last = held;
            Enqueue(entry, held);
        }
    }

    /// <summary>
    /// UNDER THE LOCK, ONTO THE MEMBER'S CHAIN: the order the snapshots were decided in is the
    /// order they reach the browser in, however long a send takes. A faulted send is logged and
    /// never breaks the chain.
    /// </summary>
    private void Enqueue(Entry entry, ContainerSnapshot snapshot)
    {
        entry.Chain = entry.Chain.ContinueWith(
            _ => SendAsync(snapshot),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }

    private async Task SendAsync(ContainerSnapshot snapshot)
    {
        try
        {
            await send(snapshot);
        }
        catch (Exception exception)
        {
            log?.LogWarning("containerChanged push failed: {Error}", exception.Message);
        }
    }
}
