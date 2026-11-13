using Yawble.Core.Bus;
using Yawble.Core.Launch;

namespace Yawble.Core.Ui;

/// <summary>
/// The launcher → the terminal windows. Every sandbox the launcher registers (a headless bus run or an
/// interactive session) becomes the session of its member's channel: its PTY/stdout bytes go into the channel's
/// replay and live stream, keyboard input and resizes go to the sandbox (interactive only), and the window's Stop
/// is the launcher's Stop (RunEnd.Stopped), never a cancellation of the bus run (which the launcher reads as the
/// core stopping). The subscription is made in <see cref="RunRegistry.Added"/>, which the launcher raises before the
/// supervisor starts pumping, so no byte is missed.
/// </summary>
public sealed class RunTerminalBridge : IDisposable
{
    private readonly RunRegistry _runs;
    private readonly TerminalHub _hub;
    private readonly Func<string, string, long?> _memberIdOf;
    private readonly Func<LiveRun, string?>? _channelOf;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Channel, Action<ReadOnlyMemory<byte>> Handler)> _attached = new(StringComparer.Ordinal);

    /// <param name="channelOf">A channel for a sandbox that is not a member's (the Agents dialog's Test), tried first.</param>
    public RunTerminalBridge(RunRegistry runs, TerminalHub hub, Func<string, string, long?> memberIdOf, Func<LiveRun, string?>? channelOf = null)
    {
        _runs = runs;
        _hub = hub;
        _memberIdOf = memberIdOf;
        _channelOf = channelOf;
        _runs.Added += OnAdded;
        _runs.Removed += OnRemoved;
    }

    private void OnAdded(LiveRun live)
    {
        var channel = _channelOf?.Invoke(live)
            ?? (_memberIdOf(live.Team, live.Member) is { } memberId ? TerminalHub.MemberChannel(memberId) : null);
        if (channel is null) return;
        var interactive = live.Kind == Sandboxing.RunKind.Interactive;
        void Handler(ReadOnlyMemory<byte> data) => _hub.Write(channel, data.Span);
        lock (_lock) _attached[live.RunKey] = (channel, Handler);
        _hub.Begin(channel, new TerminalSession(interactive ? "interactive" : "headless", live.BusRunId,
            DateTimeOffset.UtcNow.ToString("O"), interactive, null), new LiveRunInput(live));
        _hub.Write(channel, interactive
            ? $"\r\n\x1b[2m── interactive session in the sandbox · sandbox pid {live.SandboxPid} ──\x1b[0m\r\n"
            : $"\r\n\x1b[2m── run {live.BusRunId} in the sandbox · sandbox pid {live.SandboxPid} ──\x1b[0m\r\n");
        live.Output.Data += Handler;
    }

    private void OnRemoved(LiveRun live)
    {
        (string Channel, Action<ReadOnlyMemory<byte>> Handler) entry;
        lock (_lock)
        {
            if (!_attached.Remove(live.RunKey, out entry)) return;
        }
        live.Output.Data -= entry.Handler;
        _hub.Write(entry.Channel, "\r\n\x1b[2m— sandbox ended —\x1b[0m\r\n");
        _hub.End(entry.Channel, null, "ended");
    }

    public void Dispose()
    {
        _runs.Added -= OnAdded;
        _runs.Removed -= OnRemoved;
    }

    private sealed class LiveRunInput(LiveRun live) : ITerminalInput
    {
        public void Write(ReadOnlySpan<byte> data)
        {
            if (live.Kind == Sandboxing.RunKind.Interactive) live.WriteInput(data);
        }

        public void Resize(int cols, int rows) => live.Resize(new PtySize((ushort)rows, (ushort)cols));

        public Task StopAsync()
        {
            live.Stop();
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Open interactive CLI on the launcher's <see cref="InteractiveSessions"/>: the session takes a global run slot through
/// the bus (waiting in the FIFO when all are taken), then runs the member's CLI on a sandbox PTY. Its output reaches the
/// channel through <see cref="RunTerminalBridge"/>; this class only reports the wait and a start that failed.
/// </summary>
public sealed class LauncherInteractiveSessions(InteractiveSessions sessions, TerminalHub hub) : IInteractiveSessions
{
    public async Task StartAsync(MemberRow member, string channel, CancellationToken ct, PtySize? size = null)
    {
        if (hub.IsLive(channel)) throw new BusRefusal("busy", $"{member.Name} already has a session open in its terminal");
        var ticket = await sessions.OpenAsync(member.Team, member.Name, size ?? PtySize.Default, outcome =>
        {
            if (outcome.End == RunEnd.StartFailed)
                hub.Write(channel, $"\r\n\x1b[31m■ the interactive session did not start: {outcome.Reason}\x1b[0m\r\n");
        }, ct);
        if (!ticket.Admission.IsCompleted && await ticket.PositionAsync() is { } position)
            hub.Write(channel, $"\r\n\x1b[33m… waiting for a run slot (position {position})\x1b[0m\r\n");
    }
}
