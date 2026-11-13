using Yawble.Core.Bus;
using Yawble.Core.Sandboxing;

namespace Yawble.Core.Launch;

/// <summary>
/// "Open interactive CLI": the member's own CLI TUI in a fresh sandbox under the same effective policy, on
/// a sandbox PTY. It takes a global run slot through the bus (waiting in the FIFO when all are taken) and gives it
/// back when the CLI exits or the person presses Stop. The idle watchdog is off for a session; the wall-clock
/// timeout still applies.
/// </summary>
public sealed class InteractiveSessions(BusEngine bus, SandboxLauncher launcher)
{
    public Task<SessionTicket> OpenAsync(string team, string member, PtySize size, Action<LaunchOutcome>? ended = null, CancellationToken ct = default) =>
        bus.RequestSessionAsync(team, member, async token =>
        {
            var outcome = await launcher.RunAsync(new LaunchRequest(team, member, RunKind.Interactive, null, null, size), token);
            ended?.Invoke(outcome);
        }, ct);
}
