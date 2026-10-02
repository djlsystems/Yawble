namespace Harness.Pty;

/// <summary>
/// Who is currently driving one PTY session, and at what size. Exactly one client at a time, the
/// newest one; attaching detaches whoever was there.
///
/// A PTY has one size, and this system has no multiplexer. Every attached socket receives the SAME
/// bytes, so those bytes can only be laid out for one geometry: a TUI positions its cursor absolutely,
/// and output written for 40 columns lands scrambled on a 165-column grid. Sharing was tried - each
/// socket resizing freely (last writer wins), then driving the child at the smallest attached viewer,
/// tmux's rule - and both produce the same mangled screen for the same reason. tmux gets away with the
/// minimum because tmux RE-RENDERS per client; nothing here does.
///
/// So the newest device takes the terminal and the previous one is told it was detached, which is at
/// least a truthful state rather than a corrupted one. Reattaching is reopening the panel.
///
/// The size is applied at ATTACH, before the new client is sent the replay, which is the whole reason
/// the geometry travels on the connect request rather than only in a later resize frame. Resizing
/// after the replay would mean every handover replayed the previous device's layout first.
/// </summary>
/// <param name="cols">The size the child was spawned at, so a client attaching at that same size is
/// correctly recognised as needing no resize at all.</param>
/// <param name="clock">What <see cref="IdleSince"/> is stamped from: the clock of whatever reads it, so
/// an idle time and the moment it is judged at are the same kind of time.</param>
/// <param name="attached">Told each time a client takes the terminal, after it holds it.</param>
public sealed class PtyAttachment(
    IPtySession session, PtyRecord record, int cols, int rows, TimeProvider? clock = null, Action? attached = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly object _gate = new();
    private long _issued;
    private long _current;
    private CancellationTokenSource? _evict;
    private (int Cols, int Rows) _applied = (cols, rows);

    // Set at construction, not on first detach: a session that spawned and was never reached is
    // unattended from birth, and anything that reaps the unattended has to be able to see that.
    // Otherwise a child whose client died during the upgrade would live until the host restarted.
    private DateTimeOffset? _idleSince = (clock ?? TimeProvider.System).GetUtcNow();

    /// <summary>Whether a client currently holds the terminal.</summary>
    public bool HasViewer { get { lock (_gate) { return _current != 0; } } }

    /// <summary>
    /// When the last viewer left, or null while one is attached.
    ///
    /// Null means "in use"; a value means "unattended since". Deliberately not a bool plus a
    /// timestamp - two fields would let a caller read them a moment apart and act on a session that
    /// was reattached in between.
    /// </summary>
    public DateTimeOffset? IdleSince { get { lock (_gate) { return _idleSince; } } }

    /// <summary>
    /// Takes the terminal at <paramref name="cols"/> x <paramref name="rows"/>, cancelling
    /// <paramref name="evict"/> for whoever held it. The caller supplies its own source rather than
    /// receiving one, so the only thing that can be cancelled is something the caller already owns
    /// and already disposes.
    /// </summary>
    public long Attach(CancellationTokenSource evict, int cols, int rows)
    {
        CancellationTokenSource? previous;
        long viewer;

        lock (_gate)
        {
            previous = _evict;
            _evict = evict;
            viewer = ++_issued;
            _current = viewer;
            _idleSince = null;

            Resize(cols, rows);
        }

        attached?.Invoke();

        try
        {
            // OUTSIDE the lock. Cancel runs its registrations synchronously, and one of them unwinds
            // the evicted pump - which calls Detach on its way out and would deadlock on a lock this
            // thread still held.
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That viewer's pump already finished and disposed its source between us reading it and
            // cancelling it. Evicting someone who has already left is the outcome we wanted anyway.
        }

        return viewer;
    }

    /// <summary>
    /// Applies a client's geometry to the child, if that client still holds the terminal. A frame
    /// from an evicted viewer is dropped rather than obeyed - a detached tab can have a resize in
    /// flight, and honouring it would reflow the child out from under the device that took over.
    /// </summary>
    public void Report(long viewer, int cols, int rows)
    {
        lock (_gate)
        {
            if (viewer != _current) return;

            Resize(cols, rows);
        }
    }

    /// <summary>
    /// Gives up the terminal, resizing nothing. Detaching is not ending: the session keeps running,
    /// and reflowing an unattended child would corrupt what the next viewer replays.
    /// </summary>
    public void Detach(long viewer)
    {
        lock (_gate)
        {
            if (viewer != _current) return;

            _current = 0;
            _evict = null;

            // AFTER the guard. An evicted viewer's pump also calls Detach on its way out, and
            // stamping this before the check would restart the idle clock for the client that just
            // took over - so a busy terminal would look unattended to anything reaping.
            _idleSince = _clock.GetUtcNow();
        }
    }

    /// <summary>Caller must hold the gate.</summary>
    private void Resize(int cols, int rows)
    {
        // A client refits on every keyboard open, orientation change and scroll, and most of those
        // report a size the child already has. Forwarding each one would be a SIGWINCH storm that
        // makes a TUI redraw for nothing - and would throw away the replay every time, since a
        // resize is what invalidates it.
        if ((cols, rows) == _applied) return;

        _applied = (cols, rows);
        session.Resize(cols, rows);

        // Marked BEFORE the child has redrawn, deliberately. The bytes on either side of this point
        // are the ones laid out for the old size and the new one respectively, and the child's
        // response to SIGWINCH lands after it.
        record.Reflowed();
    }
}
