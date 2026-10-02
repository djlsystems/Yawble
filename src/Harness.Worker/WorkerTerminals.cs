using System.Collections.Concurrent;
using Harness.Contracts;
using Harness.Pty;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// THE TERMINALS ON THIS WORKER, by session: spawned, typed into, resized and stopped as control
/// says, their output sent to control as a stream and their end as one sequenced event.
/// </summary>
/// <remarks>
/// <para>
/// OUTPUT IS NEVER IN THE OUTBOX. Each read of the terminal is one chunk, sent straight to the
/// connection. While the connection is down a terminal keeps at most <see cref="HeldBytes"/> of
/// output, oldest dropped first, and sends it when control is back; the first chunk after a drop
/// carries <see cref="GapText"/>. The end (<see cref="TerminalEnded"/>) is published after the last
/// chunk was sent, so control never sees output after an end.
/// </para>
/// <para>
/// KEYSTROKES ARE TYPED AS THEY ARRIVE, outside the command queue; control drops what is typed while
/// this worker is not connected, so nothing reaches here late.
/// </para>
/// </remarks>
public sealed class WorkerTerminals(
    IRunStreamSink sink,
    IPtyEngine engine,
    AgentLaunchUser? runAs,
    Func<WorkerEvent, Task> publish,
    ILogger? log = null,
    TimeProvider? clock = null)
{
    /// <summary>The output one terminal keeps while the connection is down.</summary>
    public const int HeldBytes = 1024 * 1024;

    /// <summary>What the first chunk after output was dropped says.</summary>
    public const string GapText = "Output from while the worker's connection was down was lost.";

    /// <summary>How often a chunk that could not be sent is tried again.</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<string, Terminal> _terminals = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<WorkerEvent, Task> _publish = publish;
    private readonly ILogger? _log = log;

    /// <summary>The sessions open on this worker.</summary>
    public IReadOnlyCollection<string> Sessions => [.. _terminals.Keys];

    /// <summary>Spawns a terminal. Throws, with nothing left behind, when it cannot be made or spawned.</summary>
    public async Task StartAsync(StartTerminal start, CancellationToken ct = default)
    {
        if (_terminals.ContainsKey(start.Session)) throw new InvalidOperationException($"Terminal {start.Session} is already open.");

        var (spec, mcp) = ConciergeTerminal.Materialize(
            start.Launch, runAs, PtyDimensionLimits.Clamp(Math.Max(1, start.Cols)), PtyDimensionLimits.Clamp(Math.Max(1, start.Rows)));

        IPtySession session;
        try
        {
            session = await engine.SpawnAsync(spec, ct);
        }
        catch
        {
            Remove(spec, mcp);
            throw;
        }

        var terminal = new Terminal(this, start.Session, session, spec, mcp);
        _terminals[start.Session] = terminal;
        terminal.Begin();
    }

    /// <summary>Types <paramref name="input"/> into its terminal; a terminal that is gone takes nothing.</summary>
    public void Input(StreamInput input)
    {
        if (_terminals.TryGetValue(input.Stream, out var terminal)) terminal.Session.Write(input.Bytes);
    }

    public void Resize(ResizeTerminal resize)
    {
        if (_terminals.TryGetValue(resize.Session, out var terminal))
        {
            terminal.Session.Resize(PtyDimensionLimits.Clamp(Math.Max(1, resize.Cols)), PtyDimensionLimits.Clamp(Math.Max(1, resize.Rows)));
        }
    }

    /// <summary>Stops a terminal. Its end is published once, whether its CLI said one or not.</summary>
    public async Task StopAsync(string session)
    {
        if (!_terminals.TryGetValue(session, out var terminal)) return;

        try
        {
            await terminal.Session.DisposeAsync();
        }
        catch (Exception exception)
        {
            _log?.LogWarning("Stopping terminal {Session} failed: {Message}", session, exception.Message);
        }

        terminal.Exit(-1);
    }

    /// <summary>Stops every terminal: the worker is stopping.</summary>
    public async Task StopAllAsync()
    {
        foreach (var session in _terminals.Keys) await StopAsync(session);
    }

    private static void Remove(PtySpec spec, McpLaunchConfig? mcp)
    {
        foreach (var file in spec.TempFiles ?? [])
        {
            // Best effort: a file that will not delete must not stop the terminal from ending.
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        mcp?.Delete();
    }

    /// <summary>One terminal: its output queue, held while the connection is down, and its one end.</summary>
    private sealed class Terminal(WorkerTerminals owner, string id, IPtySession session, PtySpec spec, McpLaunchConfig? mcp)
    {
        private readonly Lock _gate = new();
        private readonly LinkedList<byte[]> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _queued;
        private bool _gap;
        private long _n;

        public IPtySession Session => session;

        public void Begin()
        {
            session.Output += Enqueue;
            session.Exited += Exit;
            _ = Task.Run(PumpAsync);
        }

        public void Exit(int code)
        {
            if (_exit.TrySetResult(code)) _signal.Release();
        }

        private void Enqueue(byte[] bytes)
        {
            lock (_gate)
            {
                _queue.AddLast(bytes);
                _queued += bytes.Length;

                // Held while the connection is down: the oldest goes first, and the next chunk says so.
                while (_queued > HeldBytes && _queue.First is { } first)
                {
                    _queued -= first.Value.Length;
                    _queue.RemoveFirst();
                    _gap = true;
                }
            }

            _signal.Release();
        }

        private async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    await _signal.WaitAsync();

                    while (Next() is { } chunk)
                    {
                        while (!await owner.SendAsync(chunk)) await Task.Delay(Retry, owner._clock);
                    }

                    if (_exit.Task.IsCompleted && Empty()) break;
                }

                owner._terminals.TryRemove(new KeyValuePair<string, Terminal>(id, this));
                session.Output -= Enqueue;
                Remove(spec, mcp);
                await owner._publish(new TerminalEnded(id, await _exit.Task));
            }
            catch (Exception exception)
            {
                owner._log?.LogWarning("Terminal {Session} ended, and saying so failed: {Message}", id, exception.Message);
            }
        }

        private StreamChunk? Next()
        {
            lock (_gate)
            {
                if (_queue.First is not { } first) return null;

                _queue.RemoveFirst();
                _queued -= first.Value.Length;
                var gap = _gap ? GapText : null;
                _gap = false;
                return new StreamChunk(id, ++_n, Bytes: first.Value, Gap: gap);
            }
        }

        private bool Empty()
        {
            lock (_gate) return _queue.Count == 0;
        }
    }

    private async Task<bool> SendAsync(StreamChunk chunk)
    {
        try
        {
            return await sink.StreamAsync(chunk);
        }
        catch (Exception exception)
        {
            _log?.LogWarning("A terminal's output was not sent: {Message}", exception.Message);
            return false;
        }
    }
}
