using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Harness.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Host;

/// <summary>The <c>agentSteps</c> push: steps one member's run took since the last push.</summary>
public sealed record AgentStepsPush(string Team, string Member, IReadOnlyList<AgentStep> Steps);

/// <summary>Which run a member's live run is: its first delivery and its workflow.</summary>
public sealed record AgentStepRun(long Delivery, long? Workflow);

/// <summary>
/// THE PER-SECOND CAP on one run's recorded steps. At most <see cref="PerSecond"/> steps of a run
/// are recorded for any one second of its transcript's time; a step over that is counted into the
/// last recorded step of its kind in that second (or the second's last step, when none is of its
/// kind), never dropped and never estimated.
/// </summary>
public sealed class AgentStepCap
{
    /// <summary>The most steps of one run recorded for one second.</summary>
    public const int PerSecond = 5;

    private readonly Dictionary<long, List<NewAgentStep>> _seconds = [];

    /// <summary>
    /// Takes <paramref name="step"/>: answers it when it is recorded as a row of its own, or the
    /// recorded step whose <see cref="NewAgentStep.Count"/> it was added to.
    /// </summary>
    public (NewAgentStep? Added, NewAgentStep? Counted) Take(NewAgentStep step)
    {
        var second = step.At.ToUnixTimeSeconds();
        if (!_seconds.TryGetValue(second, out var recorded)) _seconds[second] = recorded = [];

        if (recorded.Count < PerSecond)
        {
            recorded.Add(step);
            return (step, null);
        }

        var into = recorded.LastOrDefault(s => s.Kind == step.Kind) ?? recorded[^1];
        into.Count += step.Count;
        return (null, into);
    }
}

/// <summary>
/// RECORDS WHAT EVERY AGENT RUN DID, whether or not anybody watches, and pushes it to the people
/// watching the Monitor.
///
/// <para>
/// Each run that begins (<see cref="LiveRuns.Begun"/>) with a transcript to follow - its preset's
/// <c>liveView</c> - is followed on its worker, as the agent, from its first line to the run's end,
/// through the same <see cref="FollowTranscript"/> the live view uses. Each line is classified where
/// it is rendered (<see cref="TranscriptLines.Lines"/>), and each step becomes one
/// <see cref="AgentStepStore"/> row: kind, tool name and time. NO WORDS OF THE TRANSCRIPT are kept:
/// the text crosses from the worker as stream chunks and is dropped once it is classified.
/// </para>
///
/// <para>
/// OFF THE RUN'S PATH, as the file-change watch is: the follow is its own task, nothing the run
/// awaits, so a follow that fails or lags never holds the run or its end. A run whose preset has no
/// live view (a plugin member's run begins none) records nothing and logs nothing; a transcript that
/// cannot be found or read is logged once for its run, naming the team, the member and why, and is
/// never the run's failure. A row that cannot be written, or a push that cannot be sent, is logged
/// and skipped.
/// </para>
///
/// <para>
/// BOUNDED by <see cref="AgentStepCap"/>, and written and pushed at most once a second per member:
/// <c>agentSteps</c> goes to the people watching (the <c>workspaceChanged</c> group), never to a
/// machine principal, which cannot join it.
/// </para>
/// </summary>
public sealed class AgentStepFollow
{
    public const string Message = "agentSteps";

    /// <summary>How often a run's steps are written and pushed, at most.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>The most characters of a tool's name kept; a longer or stranger name is <see cref="Unnamed"/>.</summary>
    public const int MaxName = 64;

    /// <summary>What a tool whose name is empty or not a plain name is recorded as.</summary>
    public const string Unnamed = "?";

    private static readonly Regex PlainName = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    private readonly Func<ContainerId, AgentStepRun?> _runOf;
    private readonly Func<ContainerId, ((IRunWorker Worker, RunId Run)? Placed, string? Why)> _place;
    private readonly WorkerStreams _streams;
    private readonly AgentStepStore? _store;
    private readonly Func<AgentStepsPush, CancellationToken, Task> _send;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ContainerId, DateTimeOffset> _pushed = new();
    private readonly ConcurrentDictionary<Task, byte> _following = new();

    /// <param name="runOf">Which run the member's live run is; null when it has none, and nothing is recorded.</param>
    /// <param name="place">The worker to follow the member's transcript on, or none and why.</param>
    public AgentStepFollow(
        LiveRuns live,
        Func<ContainerId, AgentStepRun?> runOf,
        Func<ContainerId, ((IRunWorker Worker, RunId Run)? Placed, string? Why)> place,
        WorkerStreams streams,
        AgentStepStore? store,
        Func<AgentStepsPush, CancellationToken, Task> send,
        TimeProvider? clock = null,
        ILogger<AgentStepFollow>? logger = null)
    {
        _runOf = runOf;
        _place = place;
        _streams = streams;
        _store = store;
        _send = send;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AgentStepFollow>.Instance;

        live.Begun += Begin;
    }

    /// <summary>The follows still going: what a test waits on.</summary>
    public Task Settled() => Task.WhenAll(_following.Keys);

    private void Begin(LiveRun run)
    {
        // Read now, while the run is the member's current one; the follow itself runs on its own.
        var finding = run.Finding;
        if (run.Transcript is null && !finding) return;
        if (_runOf(run.Container) is not { } which) return;

        var task = Task.Run(() => FollowAsync(run, which, finding), CancellationToken.None);
        _following[task] = 0;
        _ = task.ContinueWith(done => _following.TryRemove(done, out _), TaskScheduler.Default);
    }

    private async Task FollowAsync(LiveRun run, AgentStepRun which, bool finding)
    {
        var member = run.Container;
        try
        {
            var transcript = run.Transcript ?? await run.Located;
            if (transcript is null)
            {
                // Looked for and not found: said once. A preset with no live view never reaches here.
                if (finding) Unreadable(member, run.Reason);
                return;
            }

            var format = run.Format ?? LiveView.ClaudeJsonl;

            var (placed, why) = _place(member);
            if (placed is not { } at)
            {
                Unreadable(member, why ?? WorkerReads.NoWorkerText);
                return;
            }

            await RecordAsync(run, which, at.Worker, at.Run, transcript, format);
        }
        catch (Exception exception)
        {
            Unreadable(member, exception.Message);
        }
    }

    private async Task RecordAsync(LiveRun run, AgentStepRun which, IRunWorker worker, RunId runId, string transcript, string format)
    {
        var member = run.Container;
        var id = "steps:" + Guid.NewGuid().ToString("N");
        using var stream = _streams.Open(id, worker);
        var batch = new Batch(this, member, which);
        var lines = 0;
        string? gap = null;
        var stopped = false;

        try
        {
            await worker.SendAsync(new FollowTranscript(id, runId, transcript, format), CancellationToken.None);

            // The run ending is said to the worker, which reads what the agent wrote last and ends.
            using var ended = run.Ended.Register(() => _ = StopAsync(worker, id, runEnded: true));

            while (true)
            {
                StreamChunk chunk;
                try
                {
                    chunk = await batch.NextAsync(stream.Reader);
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                if (chunk.Text is { } raw)
                {
                    lines++;
                    foreach (var step in Steps(format, raw)) batch.Add(step);
                }

                if (chunk.End)
                {
                    stopped = chunk.N > 0;
                    gap = chunk.Gap;
                    break;
                }
            }
        }
        finally
        {
            if (!stopped) _ = StopAsync(worker, id);
            await batch.FlushAsync(force: true);
        }

        if (gap is not null) Unreadable(member, gap);
        else if (lines == 0) Unreadable(member, $"its transcript ({transcript}) was not found or held nothing by the run's end");
    }

    /// <summary>The steps of one raw transcript line: each display line of a step kind, with no words of it.</summary>
    internal IEnumerable<NewAgentStep> Steps(string format, string raw)
    {
        var lines = TranscriptLines.Lines(format, raw);
        if (lines.Count == 0) yield break;

        var at = TranscriptLines.Timestamp(format, raw) ?? _clock.GetUtcNow();
        foreach (var line in lines)
        {
            if (!LiveLineKinds.Steps.Contains(line.Kind)) continue;

            var tool = line.Kind == LiveLineKinds.Tool;
            var label = tool ? Name(line.Label) : "";
            yield return new NewAgentStep(at.ToUniversalTime(), line.Kind, label, tool && label == "MCP" ? Name(line.McpTool) : null);
        }
    }

    /// <summary>A tool's name as it is kept: a plain name of at most <see cref="MaxName"/> characters, else <see cref="Unnamed"/>.</summary>
    public static string Name(string? name) =>
        name is { Length: > 0 and <= MaxName } && PlainName.IsMatch(name) ? name : Unnamed;

    private void Unreadable(ContainerId member, string why) =>
        _logger.LogWarning(
            "The agent steps of {Team}/{Member}'s run are not recorded: {Why}", member.Team, member.Name, why);

    private static async Task StopAsync(IRunWorker worker, string stream, bool runEnded = false)
    {
        try
        {
            await worker.SendAsync(new StopStream(stream, runEnded));
        }
        catch (InvalidOperationException)
        {
            // Gone: its tail went with it.
        }
    }

    /// <summary>One run's steps waiting to be written and pushed.</summary>
    private sealed class Batch(AgentStepFollow owner, ContainerId member, AgentStepRun which)
    {
        private readonly AgentStepCap _cap = new();
        private readonly List<NewAgentStep> _added = [];
        private readonly HashSet<NewAgentStep> _counted = new(ReferenceEqualityComparer.Instance);

        private bool Pending => _added.Count > 0 || _counted.Count > 0;

        public void Add(NewAgentStep step)
        {
            var (added, counted) = _cap.Take(step);
            if (added is not null) _added.Add(added);
            if (counted is not null && counted.Id > 0) _counted.Add(counted);
        }

        /// <summary>The next chunk; while steps wait, a due write and push happens first.</summary>
        public async Task<StreamChunk> NextAsync(ChannelReader<StreamChunk> reader)
        {
            while (true)
            {
                if (!Pending) return await reader.ReadAsync();

                var due = Due();
                if (due <= TimeSpan.Zero)
                {
                    await FlushAsync(force: false);
                    continue;
                }

                var read = reader.ReadAsync().AsTask();
                using var wait = new CancellationTokenSource();
                var timer = Task.Delay(due, owner._clock, wait.Token);
                if (await Task.WhenAny(read, timer) == read)
                {
                    await wait.CancelAsync();
                    return await read;
                }

                await FlushAsync(force: false);
                return await read;
            }
        }

        private TimeSpan Due() =>
            owner._pushed.TryGetValue(member, out var last) ? last + Interval - owner._clock.GetUtcNow() : TimeSpan.Zero;

        public async Task FlushAsync(bool force)
        {
            if (!Pending) return;

            // The push waits its second; at the end it waits out what is left of it.
            if (Due() is var wait && wait > TimeSpan.Zero)
            {
                if (!force) return;
                await Task.Delay(wait, owner._clock);
            }

            var added = _added.ToList();
            var counted = _counted.ToList();
            _added.Clear();
            _counted.Clear();

            if (owner._store is { } store)
            {
                try
                {
                    await store.WriteAsync(member.Team, member.Name, which.Delivery, which.Workflow, added, counted);
                }
                catch (Exception exception)
                {
                    owner._logger.LogWarning(exception, "Agent steps of {Team}/{Member} could not be recorded and were skipped", member.Team, member.Name);
                }
            }

            if (added.Count == 0) return;

            owner._pushed[member] = owner._clock.GetUtcNow();
            try
            {
                await owner._send(
                    new AgentStepsPush(member.Team, member.Name,
                        [.. added.Select(s => new AgentStep(s.Id, member.Team, member.Name, which.Delivery, which.Workflow, s.At, s.Kind, s.Label, s.McpTool, s.Count))]),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                owner._logger.LogWarning(exception, "Agent steps of {Team}/{Member} could not be pushed", member.Team, member.Name);
            }
        }
    }
}
