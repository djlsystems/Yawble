using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Harness.Host;

/// <summary>
/// Follows a transcript from its first byte. The file is the agent's, mode 600, and stays that
/// way: when the Host switches users the reader is <c>tail -c +1 -F</c> started through the
/// same <c>setpriv</c> prefix as every agent child, and when it cannot switch it reads the file
/// itself, as it already can.
/// </summary>
public static class LiveTranscriptReader
{
    /// <summary>How long after the run ends the reader keeps collecting lines the agent wrote last.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    /// <summary>The command line that reads <paramref name="path"/> as the agent, or null when this Host reads it directly.</summary>
    public static IReadOnlyList<string>? ReaderCommand(AgentLaunchUser? runAs, string path) =>
        runAs is { Switches: true } && SystemCommand.Find("tail") is { } tail
            ? runAs.Wrap([tail, "-c", "+1", "-F", path])
            : null;

    /// <summary>Why this Host cannot follow a transcript at all, or null when it can.</summary>
    public static string? Refusal(AgentLaunchUser? runAs) =>
        runAs is { Switches: true } && SystemCommand.Find("tail") is null
            ? $"tail is not in a root-owned system directory ({string.Join(", ", SystemCommand.Directories)}), so the transcript cannot be read as the agent."
            : null;

    /// <summary>
    /// Each complete line of the transcript, from the start, then each new one, until
    /// <paramref name="runEnded"/> fires and what was written before it has been read, or
    /// <paramref name="ct"/> (the watcher leaving) fires.
    /// </summary>
    public static IAsyncEnumerable<string> LinesAsync(
        string path, AgentLaunchUser? runAs, CancellationToken runEnded, CancellationToken ct) =>
        ReaderCommand(runAs, path) is { } command
            ? FollowCommandAsync(command, runEnded, ct)
            : FollowDirectlyAsync(path, runEnded, ct);

    /// <summary>
    /// The lines <paramref name="command"/> prints (<see cref="ReaderCommand"/>'s tail), stopped
    /// when the watcher leaves or a little after the run ends. Public so the suite can drive the
    /// tail half where it cannot switch users.
    /// </summary>
    public static async IAsyncEnumerable<string> FollowCommandAsync(
        IReadOnlyList<string> command, CancellationToken runEnded, [EnumeratorCancellation] CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The transcript reader did not start.");

        // tail -F says "cannot open" until the agent creates the file. Drained, never shown.
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);

        void Stop()
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { }
        }

        // The watcher leaving stops the reader at once; the run ending stops it once the agent's
        // last lines have had time to arrive. What is already in the pipe is still read below.
        using var left = ct.Register(Stop);
        using var ended = runEnded.Register(() => _ = Task.Delay(Settle, CancellationToken.None).ContinueWith(_ => Stop(), TaskScheduler.Default));

        try
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                ct.ThrowIfCancellationRequested();
                yield return line;
            }
        }
        finally
        {
            Stop();
        }
    }

    private static async IAsyncEnumerable<string> FollowDirectlyAsync(
        string path, CancellationToken runEnded, [EnumeratorCancellation] CancellationToken ct)
    {
        // The agent may not have created it yet.
        while (!File.Exists(path))
        {
            if (runEnded.IsCancellationRequested) yield break;
            await Task.Delay(Poll, ct);
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var pending = new List<byte>();
        var buffer = new byte[16 * 1024];

        while (true)
        {
            // Read before looking at the clock would lose the last lines: a run that ended is read
            // to its end once more, then stops.
            var last = runEnded.IsCancellationRequested;

            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                var from = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;

                    pending.AddRange(buffer.AsSpan(from, i - from));
                    yield return Encoding.UTF8.GetString([.. pending]);
                    pending.Clear();
                    from = i + 1;
                }

                pending.AddRange(buffer.AsSpan(from, read - from));
            }

            if (last)
            {
                if (pending.Count > 0) yield return Encoding.UTF8.GetString([.. pending]);
                yield break;
            }

            try { await Task.Delay(Poll, runEnded); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();
        }
    }
}
