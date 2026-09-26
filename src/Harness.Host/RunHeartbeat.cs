using System.Collections.Concurrent;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// The signal that turns a run's clock from a wall clock into an IDLE clock.
///
/// A member narrates itself by calling the <c>progress</c> tool, which is a request of its own to
/// the MCP endpoint - so the runner waiting on that member's actual child has no way to know it
/// happened. This is the only path between the two, and it is deliberately one-way and
/// tiny: the route says "this one is still alive", and whatever is running it resets its own clock.
///
/// WHY THIS IS NOT A CLOCK THAT RESETS ON OUTPUT:
/// output is incidental. A stuck agent in a retry loop prints spinner frames forever and would hold
/// a clock open forever with them. Calling <c>progress</c> is a DELIBERATE act - the agent decided
/// it had something to say - so it distinguishes a working member from a wedged one in a way raw
/// bytes cannot.
///
/// It carries no history and answers no questions. A container that is not running is not here, and
/// touching one that nothing is waiting on does nothing at all.
/// </summary>
public sealed class RunHeartbeat
{
    private readonly ConcurrentDictionary<ContainerId, Action> _waiting = new();

    /// <summary>
    /// Registers <paramref name="alive"/> to be called whenever this container reports progress,
    /// for as long as the returned handle is held. Disposing it deregisters.
    ///
    /// One registration per container, because one container runs one invocation at a time. A second
    /// registration replaces the first rather than stacking - if that ever happens the old run is
    /// already over, and leaving its callback behind would reset a clock nobody is watching.
    /// </summary>
    public IDisposable WhileRunning(ContainerId container, Action alive)
    {
        _waiting[container] = alive;

        return new Registration(this, container);
    }

    /// <summary>
    /// Says this container is still working. Called by the progress route, on the FOUND container's
    /// id rather than the caller's spelling, for the same reason everything else on that route is.
    ///
    /// Never throws. This is a courtesy signal on a path whose real job is recording a message, and
    /// a run that is ending as the call arrives is the ordinary race rather than a fault - the clock
    /// it would have reset belongs to a run that is already over.
    /// </summary>
    public void Touch(ContainerId container)
    {
        if (!_waiting.TryGetValue(container, out var alive)) return;

        try
        {
            alive();
        }
        catch (ObjectDisposedException)
        {
            // The run ended between the lookup and the call. Its clock is gone and does not need
            // resetting.
        }
    }

    private sealed class Registration(RunHeartbeat owner, ContainerId container) : IDisposable
    {
        public void Dispose() => owner._waiting.TryRemove(container, out _);
    }
}
