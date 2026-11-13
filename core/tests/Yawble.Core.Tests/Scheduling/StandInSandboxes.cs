using System.Collections.Concurrent;
using System.Diagnostics;

namespace Yawble.Core.Tests.Scheduling;

/// <summary>
/// Stand-in sandboxes for the run-limit tests: each admitted run or session starts one
/// real, long-lived OS process and ends it when the run ends, as the launcher starts and kills one sandbox per run.
/// <see cref="CountLive"/> counts them from the OS process list (Process.GetProcesses), not from the scheduler's
/// own bookkeeping, and a sampler records the highest count seen.
/// </summary>
internal sealed class StandInSandboxes : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, Process> _started = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sampler;
    private int _max;
    private int _samples;

    public StandInSandboxes()
    {
        _sampler = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var live = CountLive();
                Interlocked.Increment(ref _samples);
                int seen;
                while (live > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, live, seen) != seen) { }
                try { await Task.Delay(5, _stop.Token); } catch (OperationCanceledException) { }
            }
        });
    }

    /// <summary>The most stand-in sandboxes the OS process list showed alive at once.</summary>
    public int MaxLive => Volatile.Read(ref _max);

    public int Samples => Volatile.Read(ref _samples);

    public int Started => _started.Count;

    /// <summary>Starts one stand-in sandbox: a sleeping process that runs until it is ended.</summary>
    public Process Start()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping") { ArgumentList = { "-n", "600", "127.0.0.1" } }
            : new ProcessStartInfo("sleep") { ArgumentList = { "600" } };
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _started[process.Id] = process;
        return process;
    }

    /// <summary>Ends a stand-in sandbox and waits until the OS has reaped it, as the launcher's kill does.</summary>
    public static void End(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
            // Already ended and released.
        }
    }

    /// <summary>Stand-ins alive now, by the OS process list.</summary>
    public int CountLive()
    {
        var ids = _started.Keys.ToHashSet();
        var live = 0;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!ids.Contains(p.Id)) continue;
                bool exited;
                try { exited = _started[p.Id].HasExited; } catch (InvalidOperationException) { exited = true; }
                if (!exited) live++;
            }
        }
        return live;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _sampler;
        foreach (var p in _started.Values)
        {
            End(p);
            p.Dispose();
        }
        _stop.Dispose();
    }
}
