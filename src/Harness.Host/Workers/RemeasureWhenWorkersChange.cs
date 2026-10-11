using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// EVERYTHING CONTROL MEASURES THROUGH A WORKER IS MEASURED AGAIN WHEN THE WORKERS CHANGE, in control:
/// the sign-in probe, the launch check, the tool pre-flight and the CLI version record run as one pass
/// when a worker joins, comes back, drains or goes, and when an update ends. Measured only at start
/// they asked no worker - none had joined - and said "no worker is connected" for as long as control ran.
///
/// ONE PASS FOR A BURST: each poke restarts a quiet window, and the pass runs when it has passed, so
/// workers joining together are measured once. A steady stream of pokes postpones it by at most
/// <see cref="Ceiling"/> from the first one it has not answered. A poke during a pass is one more
/// pass after it. Each poke drops the sign-in and launch-check caches at once (<c>forget</c>), so a
/// read inside the window measures the workers as they are now, never as they were.
///
/// With no worker placeable the same pass runs, and each measurement writes not measured; nothing is
/// kept from a worker that went, and nothing is estimated.
/// </summary>
public sealed class RemeasureWhenWorkersChange
{
    /// <summary>How long the workers must stay unchanged before the pass runs.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    /// <summary>The most a steady stream of pokes may postpone the pass.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    /// <summary>What the Host logs when it wires the pass, which it does in control alone.</summary>
    public const string WiredText = "Each change of workers measures the agent CLIs again, once the changes settle.";

    private readonly IReadOnlyList<(string Name, Func<CancellationToken, Task> Step)> _steps;
    private readonly Action? _forget;
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _ceiling;
    private readonly TimeProvider _clock;
    private readonly ILogger? _log;
    private readonly CancellationToken _stopping;
    private readonly Lock _gate = new();
    private readonly ITimer _timer;
    private readonly List<(int Passes, TaskCompletionSource Done)> _waiting = [];
    private DateTimeOffset? _firstPoke;
    private bool _running;
    private bool _again;
    private int _passes;

    /// <param name="steps">The measurements, in order, each by name; one that fails does not skip the rest.</param>
    /// <param name="forget">Drops the cached measurements, at each poke.</param>
    public RemeasureWhenWorkersChange(
        IReadOnlyList<(string Name, Func<CancellationToken, Task> Step)> steps, Action? forget = null,
        TimeSpan? quiet = null, TimeSpan? ceiling = null, TimeProvider? clock = null, ILogger? log = null,
        CancellationToken stopping = default)
    {
        _steps = steps;
        _forget = forget;
        _quiet = quiet ?? Quiet;
        _ceiling = ceiling ?? Ceiling;
        _clock = clock ?? TimeProvider.System;
        _log = log;
        _stopping = stopping;
        _timer = _clock.CreateTimer(_ => Due(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>How many passes have finished.</summary>
    public int Passes
    {
        get
        {
            lock (_gate) return _passes;
        }
    }

    /// <summary>Completes once <paramref name="passes"/> passes have finished.</summary>
    public Task WaitForPassAsync(int passes)
    {
        lock (_gate)
        {
            if (_passes >= passes) return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((passes, done));
            return done.Task;
        }
    }

    /// <summary>The workers changed, or an update ended (<paramref name="why"/>): measure again once it settles.</summary>
    public void Poke(string why)
    {
        _forget?.Invoke();

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            _firstPoke ??= now;
            var due = Min(now + _quiet, _firstPoke.Value + _ceiling) - now;
            _timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        }

        _log?.LogDebug("Agent CLIs are measured again once the workers settle: {Why}.", why);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private void Due()
    {
        lock (_gate)
        {
            if (_running)
            {
                _again = true;
                return;
            }

            _running = true;
            _firstPoke = null;
        }

        _ = Task.Run(PassesAsync);
    }

    private async Task PassesAsync()
    {
        while (true)
        {
            foreach (var (name, step) in _steps)
            {
                if (_stopping.IsCancellationRequested) break;

                try
                {
                    await step(_stopping);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    _log?.LogWarning("Measuring {Step} again after the workers changed failed: {Message}", name, exception.Message);
                }
            }

            List<TaskCompletionSource> done;
            lock (_gate)
            {
                _passes++;
                done = [.. _waiting.Where(w => w.Passes <= _passes).Select(w => w.Done)];
                _waiting.RemoveAll(w => w.Passes <= _passes);

                if (!_again)
                {
                    _running = false;
                }
                else
                {
                    _again = false;
                    _firstPoke = null;
                }
            }

            foreach (var waiter in done) waiter.TrySetResult();

            lock (_gate)
            {
                if (!_running) return;
            }
        }
    }

    /// <summary>
    /// Wires the pass, when <paramref name="control"/>: poked by every change of the workers
    /// (<paramref name="onChanged"/>), by each worker once it can be sent to (<paramref name="onAttached"/>,
    /// new or back), and by the end of each update (<paramref name="onUpdateEnded"/>), which also asks
    /// <paramref name="everyWorker"/> to measure that command on every placeable worker. Returns null
    /// in all, where no worker joins.
    /// </summary>
    public static RemeasureWhenWorkersChange? Wire(
        bool control, Action<Action> onChanged, Action<Action<WorkerId>> onAttached, Action<Action<string>> onUpdateEnded,
        Func<RemeasureWhenWorkersChange> build, Func<string, Task>? everyWorker = null, ILogger? log = null)
    {
        if (!control) return null;

        log?.LogInformation(WiredText);
        var pass = build();

        onChanged(() => pass.Poke("the workers changed"));
        onAttached(worker => pass.Poke($"worker {worker} can be asked"));
        onUpdateEnded(command =>
        {
            pass.Poke($"the update of {command} ended");
            if (everyWorker is null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await everyWorker(command);
                }
                catch (Exception exception)
                {
                    log?.LogWarning("Measuring {Command} on every worker after its update failed: {Message}", command, exception.Message);
                }
            });
        });

        return pass;
    }
}
