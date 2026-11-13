using Yawble.Core.Bus;
using Yawble.Core.Sandboxing;

namespace Yawble.Core.Launch;

/// <summary>One sandbox that is running now: what the card's Stop and the terminal window act on.</summary>
public sealed class LiveRun(
    string runKey, long? busRunId, string team, string member, RunKind kind,
    ISandboxProcess process, RunSupervisor supervisor, RunOutputBuffer output)
{
    public string RunKey => runKey;
    public long? BusRunId => busRunId;
    public string Team => team;
    public string Member => member;
    public RunKind Kind => kind;
    public int SandboxPid => process.Id;
    public RunOutputBuffer Output => output;

    public void Stop() => supervisor.RequestStop();

    /// <summary>Keyboard input. Only an interactive session has input: a headless run does not read the keyboard.</summary>
    public void WriteInput(ReadOnlySpan<byte> data)
    {
        if (kind != RunKind.Interactive || process.Input is not { } input) throw new BusRefusal("no_input", "input is off: this run does not read the keyboard");
        input.Write(data);
        input.Flush();
    }

    public void Resize(PtySize size)
    {
        if (kind == RunKind.Interactive) process.Resize(size);
    }
}

/// <summary>Every live sandbox, by run key, bus run id and member.</summary>
public sealed class RunRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LiveRun> _runs = new(StringComparer.Ordinal);

    /// <summary>Raised after a sandbox is registered and before its output is pumped (the terminal windows).</summary>
    public event Action<LiveRun>? Added;

    /// <summary>Raised after a sandbox has ended and is unregistered.</summary>
    public event Action<LiveRun>? Removed;

    internal void Add(LiveRun run)
    {
        lock (_lock) _runs[run.RunKey] = run;
        Added?.Invoke(run);
    }

    internal void Remove(LiveRun run)
    {
        lock (_lock) _runs.Remove(run.RunKey);
        Removed?.Invoke(run);
    }

    public IReadOnlyList<LiveRun> All() { lock (_lock) return [.. _runs.Values]; }

    public LiveRun? Get(string runKey) { lock (_lock) return _runs.GetValueOrDefault(runKey); }

    public LiveRun? ForMember(string team, string member)
    {
        lock (_lock)
        {
            return _runs.Values.FirstOrDefault(r =>
                string.Equals(r.Team, team, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Member, member, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The card's Stop: ends the member's sandbox tree, headless or interactive. False when none runs.</summary>
    public bool Stop(string team, string member)
    {
        var run = ForMember(team, member);
        run?.Stop();
        return run is not null;
    }
}

/// <summary>The bus's runner: each admitted batch is one headless sandbox run (the slot is the bus's).</summary>
public sealed class SandboxAgentRunner(SandboxLauncher launcher) : IAgentRunner
{
    public async Task<RunResult> RunAsync(RunContext run, CancellationToken ct)
    {
        // The prompt says who the agent is, its role text, its clone and branch, then the batch.
        var outcome = await launcher.RunAsync(new LaunchRequest(run.Member.Team, run.Member.Name, RunKind.Headless, Teams.TeamPrompt.For(run), run), ct);
        // The core is stopping: leave the run open; the next start reports it interrupted.
        if (outcome.End == RunEnd.Interrupted) throw new OperationCanceledException(outcome.Reason, ct);
        if (outcome.End == RunEnd.Completed) return new RunResult(true, null, null, outcome.Details());
        if (outcome.RateLimitedUntil is not null)
            return new RunResult(false, $"the model's usage limit was reached; it said to come back at {outcome.RateLimitedUntil:yyyy-MM-dd HH:mm} UTC",
                FailureClasses.Rate, outcome.Details());
        var failureClass = outcome.End switch
        {
            RunEnd.Timeout => FailureClasses.Timeout,
            RunEnd.Idle => FailureClasses.Idle,
            RunEnd.Stopped => FailureClasses.Stopped,
            RunEnd.Credential => FailureClasses.Credential,
            RunEnd.StartFailed => FailureClasses.StartFailed,
            _ => FailureClasses.Failed,
        };
        return new RunResult(false, outcome.Reason, failureClass, outcome.Details());
    }
}
