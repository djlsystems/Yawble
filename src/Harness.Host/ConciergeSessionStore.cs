using System.Collections.Concurrent;
using Harness.Pty;

namespace Harness.Host;

/// <summary>How one person's Concierge is launched. Supplied by the Host so this type stays
/// ignorant of agents, workspaces and environment.</summary>
/// <remarks>Asynchronous because launching mints the session's credential, which is a database
/// write.</remarks>
/// <param name="publicUrl">The address the person's browser reached this Host on, from the request
/// that opened the session, or null when there was none. See
/// <see cref="ConciergeLaunchFactory.ForAsync"/>.</param>
public delegate Task<PtySpec> ConciergeLaunch(
    ConciergeSessionKey key, string team, string? publicUrl, CancellationToken ct);

/// <summary>How that session's credential is ended. Separate from the launch rather than folded
/// into disposal, because this type does not know what a credential is - only that ending a
/// session ends everything the launch created for it.</summary>
public delegate Task ConciergeRevoke(ConciergeSessionKey key, CancellationToken ct);

/// <summary>The spec is kept because its TempFiles outlive the spawn: a system-prompt file has to
/// survive as long as the child that may still read it, and be deleted when the session retires
/// rather than never - otherwise every console leaks one.</summary>
/// <param name="Attachment">Which client currently drives this session's terminal - the desktop panel
/// or a phone, never both. It belongs to the SESSION, so it is created here rather than per
/// connection.</param>
public sealed record ConciergeSession(
    ConciergeSessionKey Key, PtySpec Spec, IPtySession Session, PtyRecord Record, PtyAttachment Attachment);

/// <summary>
/// The Concierge sessions - one per signed-in person, tenant-wide.
///
/// A console is NOT an Agent Container and not a team member: it holds no Harness state, receives
/// no events, and has no privileged channel - it reaches the system through the same CLI everything
/// else uses. That is what lets it run identically in this UI, a desktop terminal, or embedded in
/// another app.
///
/// One per PERSON, not one per team. What that buys is that a console's credential can be the
/// DRIVER's authority rather than the team's, and that two people on one team do not evict each
/// other. The cost is real and is why <see cref="ReapIdleAsync"/> exists: a session is a child
/// process holding a PTY.
///
/// In memory on purpose. The child dies with the Host, so persisting the registry would only record
/// sessions that no longer exist.
/// </summary>
public sealed class ConciergeSessionStore(
    IPtyEngine engine, ConciergeLaunch launch, ConciergeRevoke revoke) : IAsyncDisposable
{
    // Default comparer: ConciergeSessionKey carries its own ordinal equality.
    private readonly ConcurrentDictionary<ConciergeSessionKey, ConciergeSession> _sessions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Has(ConciergeSessionKey key) => _sessions.ContainsKey(key);

    /// <summary>
    /// Find-or-create. Serialised on one gate rather than using GetOrAdd, because the factory spawns
    /// a process: GetOrAdd may run its factory more than once under contention, and a discarded
    /// duplicate would be an orphaned child nobody holds a handle to.
    /// </summary>
    /// <param name="cols">The attaching client's geometry. Used only when this call actually SPAWNS
    /// the child, and it matters because the child starts printing immediately: a banner drawn at a
    /// guessed default width and then replayed into the real terminal is wrapped for a screen that
    /// never existed, which is what made every first open look broken. An existing session ignores
    /// this - PtyAttachment resizes it instead.</param>
    /// <param name="publicUrl">The address the attaching browser used. Used only when this call
    /// SPAWNS the child, for <paramref name="cols"/>'s reason: an environment is fixed at spawn.</param>
    public async Task<ConciergeSession> AttachAsync(
        ConciergeSessionKey key, string team, int cols, int rows, CancellationToken ct, string? publicUrl = null)
    {
        if (_sessions.TryGetValue(key, out var existing)) return existing;

        await _gate.WaitAsync(ct);

        try
        {
            if (_sessions.TryGetValue(key, out existing)) return existing;

            var record = new PtyRecord(key.ToString());
            var spec = await launch(key, team, publicUrl, ct) with { Cols = cols, Rows = rows };
            var session = await engine.SpawnAsync(spec, ct);

            // Subscribed BEFORE the session is handed out, so the record is complete from birth and
            // a client attaching later replays everything rather than joining mid-stream.
            session.Output += record.Append;
            session.Exited += record.Complete;

            var console = new ConciergeSession(
                key, spec, session, record, new PtyAttachment(session, record, spec.Cols, spec.Rows));
            _sessions[key] = console;

            // A CLI THAT EXITS BY ITSELF ENDS ITS SESSION, viewer or not: otherwise what it held
            // (the heavy lease, its credential) stays held until someone ends it or the idle window
            // passes. Subscribed after the session is stored, so an exit that has already latched
            // still finds it. Off this thread, because Exited is raised from the session's own pump,
            // which disposal waits for. Ends only THIS console, never a newer one under the key.
            session.Exited += code => Task.Run(() => EndAsync(console));

            return console;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ending a session is explicit and separate from detaching. Closing the panel or reloading the
    /// browser drops a socket; neither may reach this.
    /// </summary>
    public async Task EndAsync(ConciergeSessionKey key)
    {
        if (!_sessions.TryGetValue(key, out var console)) return;

        await EndAsync(console);
    }

    private async Task EndAsync(ConciergeSession console)
    {
        // Removed only if it is still the one stored, so a late exit of an ended session cannot
        // end the session that replaced it, and a second end of one session does nothing.
        if (!_sessions.TryRemove(new KeyValuePair<ConciergeSessionKey, ConciergeSession>(console.Key, console))) return;

        try
        {
            await console.Session.DisposeAsync();

            foreach (var file in console.Spec.TempFiles ?? [])
            {
                // Best effort, and after disposal: the child is gone, so nothing can still be reading
                // it. A file that will not delete must not stop the session from ending.
                try { File.Delete(file); } catch (IOException) { }
            }
        }
        finally
        {
            // The credential and the lease die with the session they belonged to, WHATEVER disposal
            // did: a dispose that throws must not leave the heavy lease held. Not passed the
            // caller's token: this also runs from DisposeAsync during shutdown, where that token is
            // already cancelled and a skipped revoke would leave a live key behind for a terminal
            // that no longer exists.
            await revoke(console.Key, CancellationToken.None);
        }
    }

    /// <summary>
    /// Ends every session whose last viewer left longer ago than <paramref name="window"/>, and
    /// returns what it ended.
    ///
    /// <paramref name="now"/> is a parameter rather than a clock this reads, so a spec can state the
    /// passage of time instead of sleeping through it, and so the policy stays with the caller that
    /// was configured with it.
    ///
    /// A session someone is WATCHING has no idle time and is never a candidate, however long it has
    /// been open - this reaps the unattended, not the long-lived. That distinction is the whole
    /// reason closing the panel can stay free: detaching starts a clock, and nothing else does.
    /// </summary>
    public async Task<IReadOnlyList<ConciergeSessionKey>> ReapIdleAsync(TimeSpan window, DateTimeOffset now)
    {
        List<ConciergeSessionKey> ended = [];

        foreach (var (key, console) in _sessions)
        {
            if (console.Attachment.IdleSince is not { } since) continue;
            if (now - since < window) continue;

            await EndAsync(key);
            ended.Add(key);
        }

        return ended;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _sessions.Keys) await EndAsync(key);

        _gate.Dispose();
    }
}
