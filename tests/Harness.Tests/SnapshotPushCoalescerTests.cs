using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The containerChanged push, coalesced per member: a snapshot that changed something goes at
/// once; one that says what the last one said - a progress line - is held and the newest is sent
/// once per window, so twenty lines a second cost the browsers four pushes, not twenty.
/// </summary>
public sealed class SnapshotPushCoalescerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static ContainerSnapshot Snapshot(ContainerState state = ContainerState.Idle, int queued = 0) =>
        new("alpha", "Developer", "Developer", "claude", state, queued, 16, ["x"], null);

    private static (SnapshotPushCoalescer Pushes, List<ContainerSnapshot> Sent, ManualTime Clock) Bed()
    {
        var sent = new List<ContainerSnapshot>();
        var clock = new ManualTime(Start);
        var pushes = new SnapshotPushCoalescer(
            snapshot => { sent.Add(snapshot); return Task.CompletedTask; },
            SnapshotPushCoalescer.DefaultWindow, clock);
        return (pushes, sent, clock);
    }

    [Fact]
    public void A_state_change_is_sent_at_once()
    {
        var (pushes, sent, _) = Bed();

        pushes.Offer(Snapshot());
        pushes.Offer(Snapshot(ContainerState.Running));
        pushes.Offer(Snapshot(ContainerState.Running, queued: 1));

        Assert.Equal(3, sent.Count);
    }

    [Fact]
    public void Three_identical_snapshots_in_a_window_send_the_newest_once_when_it_closes()
    {
        var (pushes, sent, clock) = Bed();
        pushes.Offer(Snapshot(ContainerState.Running));

        pushes.Offer(Snapshot(ContainerState.Running));
        pushes.Offer(Snapshot(ContainerState.Running));
        var newest = Snapshot(ContainerState.Running);
        pushes.Offer(newest);
        Assert.Single(sent);

        clock.Advance(SnapshotPushCoalescer.DefaultWindow);

        Assert.Equal(2, sent.Count);
        Assert.Same(newest, sent[1]);

        clock.Advance(SnapshotPushCoalescer.DefaultWindow);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void A_change_while_one_is_held_goes_at_once_and_the_held_one_is_dropped()
    {
        var (pushes, sent, clock) = Bed();
        pushes.Offer(Snapshot(ContainerState.Running));
        pushes.Offer(Snapshot(ContainerState.Running));
        Assert.Single(sent);

        var idle = Snapshot();
        pushes.Offer(idle);
        Assert.Equal(2, sent.Count);
        Assert.Same(idle, sent[1]);

        clock.Advance(SnapshotPushCoalescer.DefaultWindow);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void Two_members_are_held_apart()
    {
        var (pushes, sent, clock) = Bed();
        var other = Snapshot(ContainerState.Running) with { Id = "Tester", Name = "Tester" };
        pushes.Offer(Snapshot(ContainerState.Running));
        pushes.Offer(other);
        pushes.Offer(Snapshot(ContainerState.Running));
        pushes.Offer(other);
        Assert.Equal(2, sent.Count);

        clock.Advance(SnapshotPushCoalescer.DefaultWindow);

        Assert.Equal(4, sent.Count);
    }

    [Fact]
    public void A_snapshot_whose_wakes_changed_is_a_change()
    {
        var a = Snapshot() with { Wakes = [new WakeView(1, "Developer", 7, Start.AddMinutes(5), Start, "later")] };
        var b = Snapshot() with { Wakes = [new WakeView(1, "Developer", 7, Start.AddMinutes(5), Start, "later")] };
        var c = Snapshot() with { Wakes = [new WakeView(2, "Developer", 7, Start.AddMinutes(9), Start, "later still")] };

        Assert.True(SnapshotPushCoalescer.Same(a, b));
        Assert.False(SnapshotPushCoalescer.Same(a, c));
    }
}
