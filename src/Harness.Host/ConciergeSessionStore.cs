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
/// <param name="Session">The terminal as a viewer drives it: what is typed into it counts as activity.</param>
/// <param name="Activity">What the session has done, for the reap rule and the session list.</param>
/// <param name="Terminal">The terminal as the engine made it, for what only it knows - which worker runs it.</param>
public sealed record ConciergeSession(
    ConciergeSessionKey Key, PtySpec Spec, IPtySession Session, PtyRecord Record, PtyAttachment Attachment,
    ConciergeActivity Activity, IPtySession Terminal)
{
    /// <summary>The worker running the terminal, or null when it does not run on one.</summary>
    public string? Worker => Terminal is WorkerPtySession remote ? remote.Worker.Value : null;

    /// <summary>The terminal's session id on its worker, or null when it does not run on one.</summary>
    public string? TerminalId => Terminal is WorkerPtySession remote ? remote.Id : null;
}

/// <summary>
/// A session the reaper ended, and why: nobody had it open since <paramref name="LastViewerAt"/>
/// (its start, when <paramref name="NeverViewed"/>) and it did nothing since
/// <paramref name="LastActivityAt"/>, both at least <paramref name="Window"/> before.
/// </summary>
public sealed record ConciergeReaped(
    ConciergeSessionKey Key,
    string? Worker,
    DateTimeOffset LastViewerAt,
    bool NeverViewed,
    DateTimeOffset LastActivityAt,
    string LastActivity,
    TimeSpan Window)
{
    /// <summary>The sentence the tenant row carries.</summary>
    public string Reason
    {
        get
        {
            var viewer = NeverViewed
                ? $"No viewer since it started at {Format(LastViewerAt)}"
                : $"No viewer since {Format(LastViewerAt)}";
            return $"{viewer} and no activity since {Format(LastActivityAt)} (last: {LastActivity}); "
                + $"the window is {Window.ToString("c", System.Globalization.CultureInfo.InvariantCulture)}.";
        }
    }

    /// <summary>How a time is written in <see cref="Reason"/>: UTC, to the second.</summary>
    public static string Format(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

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
/// <param name="clock">The one clock of the reap rule: every activity stamp and every idle time is
/// read from it.</param>
/// <param name="floorOf">The output floor a session launched by a spec must pass; the platform default when null.</param>
public sealed class ConciergeSessionStore(
    IPtyEngine engine, ConciergeLaunch launch, ConciergeRevoke revoke,
    TimeProvider? clock = null, Func<PtySpec, OutputFloor>? floorOf = null) : IAsyncDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Default comparer: ConciergeSessionKey carries its own ordinal equality.
    private readonly ConcurrentDictionary<ConciergeSessionKey, ConciergeSession> _sessions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Has(ConciergeSessionKey key) => _sessions.ContainsKey(key);

    /// <summary>The clock every stamp and idle time is read from.</summary>
    public TimeProvider Clock => _clock;

    /// <summary>Raised after a session starts or ends.</summary>
    public event Action? Changed;

    /// <summary>The sessions running now.</summary>
    public IReadOnlyList<ConciergeSession> Sessions() => [.. _sessions.Values];

    /// <summary>
    /// A platform call under <paramref name="key"/>'s own credential has begun: activity until the
    /// handle is disposed. Null when that person has no session.
    /// </summary>
    public IDisposable? CallBegan(ConciergeSessionKey key) =>
        _sessions.TryGetValue(key, out var console) ? console.Activity.CallBegan() : null;

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
            var activity = new ConciergeActivity(floorOf?.Invoke(spec) ?? OutputFloor.PlatformDefault, _clock);
            session.Output += record.Append;
            session.Output += activity.Printed;
            session.Exited += record.Complete;

            var typing = new KeystrokeStamping(session, activity);
            var console = new ConciergeSession(
                key, spec, typing, record,
                new PtyAttachment(typing, record, spec.Cols, spec.Rows, _clock, activity.Attach),
                activity, session);
            _sessions[key] = console;

            // A CLI THAT EXITS BY ITSELF ENDS ITS SESSION, viewer or not: otherwise what it held
            // (the heavy lease, its credential) stays held until someone ends it or the idle window
            // passes. Subscribed after the session is stored, so an exit that has already latched
            // still finds it. Off this thread, because Exited is raised from the session's own pump,
            // which disposal waits for. Ends only THIS console, never a newer one under the key.
            session.Exited += code => Task.Run(() => EndAsync(console));

            Changed?.Invoke();
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
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Ends every session that nobody has had open for <paramref name="window"/> AND that has done
    /// nothing for <paramref name="window"/>, and returns what it ended and why.
    ///
    /// <paramref name="now"/> is a parameter rather than a clock this reads, so a spec can state the
    /// passage of time instead of sleeping through it, and so the policy stays with the caller that
    /// was configured with it.
    ///
    /// A session someone is WATCHING is never a candidate, whatever it has done - this reaps the
    /// unattended, not the long-lived. And an unattended session that is WORKING is not one either:
    /// output past its floor, a keystroke or attach, or a platform call under its own credential
    /// (one still in flight counts until it ends) each restart the window. A person who closes the
    /// tab on a Concierge running a long task comes back to it still running.
    /// </summary>
    public async Task<IReadOnlyList<ConciergeReaped>> ReapIdleAsync(TimeSpan window, DateTimeOffset now)
    {
        List<ConciergeReaped> ended = [];

        foreach (var (key, console) in _sessions)
        {
            if (console.Attachment.IdleSince is not { } since) continue;
            if (now - since < window) continue;

            var (last, kind) = console.Activity.Last(now);
            if (now - last < window) continue;

            await EndAsync(console);
            ended.Add(new ConciergeReaped(key, console.Worker, since, !console.Activity.EverViewed, last, kind, window));
        }

        return ended;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _sessions.Keys) await EndAsync(key);

        _gate.Dispose();
    }
}

/// <summary>
/// The terminal as a viewer drives it: each keystroke is stamped as activity, then passed on.
/// Everything else is the terminal's own, untouched - its output, its end and its disposal.
/// </summary>
internal sealed class KeystrokeStamping(IPtySession inner, ConciergeActivity activity) : IPtySession
{
    public event Action<byte[]> Output
    {
        add => inner.Output += value;
        remove => inner.Output -= value;
    }

    public event Action<int> Exited
    {
        add => inner.Exited += value;
        remove => inner.Exited -= value;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        activity.Keystrokes();
        inner.Write(bytes);
    }

    public void Resize(int cols, int rows) => inner.Resize(cols, rows);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
