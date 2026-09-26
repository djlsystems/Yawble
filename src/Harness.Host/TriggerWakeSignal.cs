namespace Harness.Host;

/// <summary>
/// Tells the schedule runner that the answer to "when is the next thing due?" has changed.
///
/// The runner sleeps until the earliest due instant rather than waking on a fixed tick, which is
/// what makes a ten-second schedule fire every ten seconds. That leaves one gap: a schedule created
/// or edited WHILE it is asleep is invisible until whatever it was already waiting for. Without
/// this, adding a schedule due in ten seconds could sit unnoticed for the whole of the runner's
/// maximum wait.
///
/// A SemaphoreSlim rather than a CancellationTokenSource, and that is the whole design. A token is
/// one-shot: signalling means replacing the source, which races the waiter reading it. A semaphore
/// is level-triggered and reusable - a release that arrives while nobody is waiting is REMEMBERED,
/// so a write that lands microseconds before the runner starts waiting is not lost. That race is
/// the one a scheduler actually meets, because the write and the wait are on different threads with
/// nothing ordering them.
///
/// Capped at one permit. Ten schedules saved at once should wake the runner once; it re-reads the
/// earliest due instant when it wakes, so the count carries no information.
/// </summary>
public sealed class TriggerWakeSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>
    /// Called after any write that could change when the next schedule is due - a create, an edit,
    /// an enable, a delete. Cheap, idempotent, and safe to call when nothing is listening.
    /// </summary>
    public void Signal()
    {
        // Release throws once the cap is reached, and "already pending" is the ordinary case rather
        // than a fault: it means an earlier write has woken the runner and it has not yet looked.
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>
    /// Waits until something is signalled or <paramref name="timeout"/> elapses. The return value is
    /// deliberately ignored by the runner: either way it re-reads the earliest due instant, so
    /// "woken early" and "waited it out" lead to exactly the same next step.
    /// </summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        _signal.WaitAsync(timeout, ct);

    public void Dispose() => _signal.Dispose();
}
